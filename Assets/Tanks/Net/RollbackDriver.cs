using System;
using Tanks.Sim;

namespace Tanks.Net;

/// <summary>
/// GGPO-style rollback driver for a 1v1 match over an established protocol session.
///
/// Each executed tick samples the LOCAL player from an input source and fills the
/// REMOTE player from the session — the actual input when it has arrived, otherwise a
/// prediction (repeat their newest known input). When a late input contradicts a
/// prediction, the driver restores the snapshot before the first wrong tick and
/// replays to the present with corrected inputs; ticks executed with authoritative
/// inputs on both sides are "confirmed" and never change again.
///
/// Two bounds gate every tick (both are what synchronize match start — neither side
/// can run away from tick 1):
///  - prediction bound: never simulate more than <see cref="PredictionWindow"/> ticks
///    past the newest remote input;
///  - ack bound: never outrun the remote's ack of OUR inputs by more than the
///    redundant send window (<see cref="Input.Count"/>). This is the invariant that
///    makes the fixed 8-slot window lossless: a packet stamped tick T always covers
///    everything the receiver can be missing, so holes cannot form.
///
/// Desync detection: peers exchange hashes of their newest CONFIRMED tick; we compare
/// against our own history once the tick is confirmed on our side too. Mismatch sets
/// <see cref="DesyncDetected"/> (loud, but the match keeps running).
///
/// Tick-advantage throttling (GGPO timesync) is deliberately not here yet — the
/// Advantage message flows, but only its ack half is consumed.
/// </summary>
public sealed class RollbackDriver : IMatchDriver
{
    /// <summary>History/input-ring depth. Bounds how far back a rollback can reach —
    /// vastly deeper than the prediction window, so it never binds in practice.</summary>
    public const int RingCapacity = 256;

    private const int MaxStepsPerFrame = 5; // clamp to avoid a death spiral after a hitch

    private readonly SimConfig _config;
    private readonly Simulation _simulation;
    private readonly Arena _arena;
    private readonly IInputSource _localSource;
    private readonly int _localPlayer; // sim slot of the local player (PeerId order decides)
    private readonly Session _session;
    private readonly RemoteState? _remote; // null in pure tests: nothing is sent
    private readonly ILog _logger;

    private readonly StateHistory _history = new StateHistory(RingCapacity);
    private readonly PlayerInput[] _localInputs = new PlayerInput[RingCapacity]; // what we sampled, by tick
    private readonly PeerInput[] _remoteApplied = new PeerInput[RingCapacity];   // what we ticked with, by tick
    private readonly PlayerInput[] _tickInputs = new PlayerInput[2];

    private GameState _state = null!; // set by ResetMatch in the ctor
    private ulong _lastHash;
    private uint _currentTick;         // last executed tick; the first executed tick is 1
    private uint _confirmedRemoteTick; // every tick <= this ran with authoritative remote input
    private uint _lastHashCheckedTick;
    private double _accumulator;

    /// <summary>How many ticks we may simulate past the newest remote input.</summary>
    public int PredictionWindow { get; set; } = Input.Count;

    public RollbackDriver(
        SimConfig config,
        Simulation simulation,
        Arena arena,
        IInputSource localSource,
        int localPlayer,
        Session session,
        RemoteState? remote,
        ILog logger)
    {
        ThrowHelper.ThrowIfNull(config);
        ThrowHelper.ThrowIfNull(simulation);
        ThrowHelper.ThrowIfNull(arena);
        ThrowHelper.ThrowIfNull(localSource);
        ThrowHelper.ThrowIfNull(session);
        ThrowHelper.ThrowIfNull(logger);
        if (localPlayer != 0 && localPlayer != 1)
            throw new ArgumentOutOfRangeException(nameof(localPlayer));
        if (config.PlayerCount != 2)
            throw new ArgumentException("Rollback driver is 1v1 only.", nameof(config));

        _config = config;
        _simulation = simulation;
        _arena = arena;
        _localSource = localSource;
        _localPlayer = localPlayer;
        _session = session;
        _remote = remote;
        _logger = logger;
        ResetMatch();
    }

    /// <summary>The sim slot the local player controls (0 or 1, from PeerId order).</summary>
    public int LocalPlayer => _localPlayer;

    public GameState State => _state;
    public Arena Arena => _arena;
    public SimConfig Config => _config;
    public ulong LastHash => _lastHash;
    public StateHistory History => _history;
    public Session Session => _session;

    public bool AllowsLocalReset => false; // a local reset would desync the opponent

    // Netcode visibility (HUD / tests).
    public uint CurrentTick => _currentTick;
    public uint ConfirmedTick => _confirmedRemoteTick;
    public uint RemoteFrontier => _session.LatestInputTick;
    public uint AckedTick => _session.LastAckedTick;
    public int RollbackCount { get; private set; }
    public int LastRollbackDepth { get; private set; }
    public long TotalRolledBackTicks { get; private set; }
    public long StalledSteps { get; private set; }
    public bool DesyncDetected { get; private set; }
    public uint DesyncTick { get; private set; }

    public void ResetMatch()
    {
        _state = GameState.CreateInitial(_config);
        _localSource.Reset();
        _history.Clear();
        _lastHash = _history.Record(_state);
        Array.Clear(_localInputs, 0, _localInputs.Length);
        Array.Clear(_remoteApplied, 0, _remoteApplied.Length);
        _currentTick = 0;
        _confirmedRemoteTick = 0;
        _lastHashCheckedTick = 0;
        _accumulator = 0;
    }

    public void Advance(double deltaTime)
    {
        // Corrections may have arrived since last frame (SimRunner polls the pipe
        // before advancing us) — settle them before simulating further.
        RollBackIfMispredicted();

        double step = 1.0 / _config.TickRate;
        _accumulator += deltaTime;
        double maxAccumulator = step * MaxStepsPerFrame;
        if (_accumulator > maxAccumulator) _accumulator = maxAccumulator;

        int steps = 0;
        while (_accumulator >= step && steps < MaxStepsPerFrame)
        {
            if (!CanExecute(_currentTick + 1))
            {
                StalledSteps++;
                _accumulator = 0; // don't bank time while stalled: resume gently, not in a burst
                break;
            }
            StepOnce();
            _accumulator -= step;
            steps++;
        }

        CheckDesync();

        // One bundle per frame, even (especially) while stalled — the redundant input
        // window in these sends is what lets the remote catch up and clear the stall.
        Send();
    }

    private bool CanExecute(uint tick)
    {
        // Prediction bound.
        if (tick > _session.LatestInputTick + (uint)PredictionWindow) return false;
        // Ack bound (see class docs — this is what keeps the 8-slot window lossless).
        if (tick > _session.LastAckedTick + (uint)Input.Count) return false;
        return true;
    }

    private void StepOnce()
    {
        uint tick = _currentTick + 1;

        PlayerInput local = _localSource.Sample();
        _localInputs[(int)(tick % RingCapacity)] = local;
        _remote?.RecordLocalInput(tick, local);

        ExecuteTick(tick, local, ResolveRemoteInput(tick));

        if (tick <= _session.LatestInputTick && _confirmedRemoteTick == tick - 1)
            _confirmedRemoteTick = tick; // executed with the actual remote input
    }

    private void ExecuteTick(uint tick, PlayerInput local, PeerInput remote)
    {
        _remoteApplied[(int)(tick % RingCapacity)] = remote;
        _tickInputs[_localPlayer] = local;
        _tickInputs[1 - _localPlayer] = new PlayerInput(remote.Buttons, remote.TurretAim);
        _simulation.Tick(_state, _arena, _tickInputs);
        _lastHash = _history.Record(_state);
        _currentTick = tick;
    }

    private PeerInput ResolveRemoteInput(uint tick)
    {
        uint latest = _session.LatestInputTick;
        if (latest == 0) return default; // nothing ever received: predict "no input"
        // The actual input when we have it; otherwise repeat their newest known input.
        return _session.InputAt(tick <= latest ? tick : latest);
    }

    private void RollBackIfMispredicted()
    {
        uint frontier = Math.Min(_session.LatestInputTick, _currentTick);
        if (frontier <= _confirmedRemoteTick) return;

        // Scan the newly-confirmed range for the first tick whose actual input differs
        // from the prediction we executed with.
        uint firstWrong = 0;
        for (uint t = _confirmedRemoteTick + 1; t <= frontier; t++)
        {
            PeerInput actual = _session.InputAt(t);
            PeerInput applied = _remoteApplied[(int)(t % RingCapacity)];
            if (actual.Buttons != applied.Buttons || actual.TurretAim != applied.TurretAim)
            {
                firstWrong = t;
                break;
            }
        }

        if (firstWrong != 0)
        {
            uint restoreTick = firstWrong - 1;
            if (!_history.TryGet(restoreTick, out StateHistorySlot slot))
            {
                // Unreachable while the stall bounds hold (depth <= PredictionWindow << RingCapacity).
                _logger.LogError($"rollback to tick {restoreTick} impossible: snapshot aged out (current {_currentTick})");
                return;
            }

            uint target = _currentTick;
            int depth = (int)(target - restoreTick);

            _state.CopyFrom(slot.State);
            _history.Rewind(restoreTick);
            _currentTick = restoreTick;

            // Replay to the present: actual remote inputs where known (the correction),
            // refreshed predictions past the frontier, our own inputs from the ring.
            for (uint t = firstWrong; t <= target; t++)
            {
                ExecuteTick(t, _localInputs[(int)(t % RingCapacity)], ResolveRemoteInput(t));
            }

            RollbackCount++;
            LastRollbackDepth = depth;
            TotalRolledBackTicks += depth;
        }

        _confirmedRemoteTick = frontier;
    }

    private void CheckDesync()
    {
        // Compare only ticks that are confirmed locally; the remote only ever sends
        // hashes of ITS confirmed ticks, so both sides of the comparison are final.
        uint upTo = _confirmedRemoteTick;
        for (uint t = _lastHashCheckedTick + 1; t <= upTo; t++)
        {
            if (!_session.TryGetRemoteHash(t, out ulong remoteHash)) continue; // hashes are per-frame, not per-tick
            if (!_history.TryGet(t, out StateHistorySlot slot)) continue;      // aged out of the ring

            if (slot.Hash != remoteHash && !DesyncDetected)
            {
                DesyncDetected = true;
                DesyncTick = t;
                _logger.LogError($"DESYNC at tick {t}: local hash {slot.Hash:X16} != remote hash {remoteHash:X16}");
            }
        }
        if (upTo > _lastHashCheckedTick)
            _lastHashCheckedTick = upTo;
    }

    private void Send()
    {
        if (_remote is null || _currentTick == 0) return;

        uint hashTick = _confirmedRemoteTick;
        ulong hash = 0;
        if (hashTick != 0 && _history.TryGet(hashTick, out StateHistorySlot slot))
            hash = slot.Hash;
        else
            hashTick = 0; // receiver drops tick-0 hashes

        _remote.Send(_currentTick, hashTick, hash);
    }
}

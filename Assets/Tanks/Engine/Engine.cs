using System;
using System.Buffers;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading;
using Tanks.Net;
using Tanks.Sim;

namespace Tanks.Engine;

// Engine thread
public interface IEngine
{
    public Umem Umem { get; }
    public void Start();
    public bool Stop(TimeSpan timeout = default);
    public bool IsAlive { get; }
}

public sealed unsafe class Engine : IEngine, IDisposable
{
    private Umem _umem;
    private readonly Network _network;
    private readonly ILog _logger;
    private readonly SimConfig _config;

    private readonly object _sync = new();
    private bool _stopping;
    private Thread? _thread;
    private CancellationTokenSource? _cancellation;
    private int _disposed;

    public Engine(
        Network network,
        ILog logger,
        SimConfig config)
    {
        ThrowHelper.ThrowIfNull(network);
        ThrowHelper.ThrowIfNull(logger);
        ThrowHelper.ThrowIfNull(config);

        _network = network;
        _logger = logger;
        _config = config;
        _umem = new Umem(1024, 4096);
    }

    public Umem Umem => _umem;
    public bool IsAlive => _thread?.IsAlive is true;

    public void Dispose()
    {
        Stop();
        if (_thread is null)
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
            using (_umem)
            {
            }
        }
    }

    public void Start()
    {
        bool start = false;

        lock (_sync)
        {
            if (_thread is not null || Volatile.Read(ref _disposed) != 0)
            {
                return;
            }

            _cancellation = new CancellationTokenSource();
            _thread = new Thread(() => Loop(new EngineState(_umem, _logger, _network, _config), _cancellation.Token)) { IsBackground = true };
            start = true;
        }

        if (start)
        {
            try
            {
                _thread.Start();
            }
            catch
            {
                using (_cancellation)
                {
                    _cancellation.Cancel();
                    if (_thread.IsAlive)
                        _thread.Join();

                    _cancellation = null;
                    _thread = null;
                    throw;
                }
            }
        }
    }

    public bool Stop(TimeSpan timeout = default)
    {
        if (timeout <= TimeSpan.Zero)
        {
            timeout = TimeSpan.FromSeconds(5); // default timeout
        }

        Thread? thread = null;

        lock (_sync)
        {
            thread = _thread;

            if (thread is null)
            {
                _logger.LogDebug("network thread already stopped");
                goto PassiveStop;
            }

            if (_stopping)
            {
                goto PassiveStop;
            }
            else
            {
                _stopping = true;
                goto ActiveStop;
            }
        }

    ActiveStop:
        {
            Debug.Assert(_cancellation is not null);
            Debug.Assert(_thread is not null);

            try
            {
                try { _cancellation.Cancel(); } catch { }
                bool killed = !thread.IsAlive;
                if (!killed)
                {
                    killed = thread.Join(timeout);
                    if (!killed)
                    {
                        _logger.LogError("failed to kill network thread");
                    }
                }
                if (killed)
                {
                    _logger.LogInformation("killed network thread");
                    using (_cancellation)
                    {
                        _thread = null;
                        _cancellation = null;
                    }
                }
            }
            finally
            {
                _stopping = false;
            }

            return !IsAlive;
        }

    PassiveStop:
        {
            if (thread is not null)
            {
                _logger.LogInformation("waiting on existing stop operation...");
                thread?.Join(timeout);
            }
            bool stopped = !IsAlive;

            if (stopped)
                _logger.LogInformation("network thread stopped");
            else
                _logger.LogWarning("network thread still running");

            return stopped;
        }
    }

    private static void Loop(EngineState engine, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                Tick(ref engine);
                Thread.Sleep(1);
            }
            catch (Exception ex)
            {
                engine.Logger.LogError($"Error in network thread: {ex}");
            }
        }
    }

    /// <summary>Ticks we may simulate past the newest remote input (prediction depth).</summary>
    public const int PredictionWindow = Input.Count;

    private static void Tick(ref EngineState engine)
    {
        // Process messages from network
        DrainNetworkRx(ref engine);

        // Process messages from render thread
        DrainRenderRx(ref engine);

        switch (engine.Phase)
        {
            case PhaseState.GameView:
                TickGameView(ref engine);
                break;
        }
    }

    private static void TickGameView(ref EngineState engine)
    {
        ref GameViewState gv = ref engine.GameView;

        // Corrections may have arrived in the drains above — settle them before
        // simulating further.
        RollBackIfMispredicted(ref engine);

        double step = 1.0 / engine.Config.TickRate;
        long now = Stopwatch.GetTimestamp();
        gv.Accumulator += (now - gv.LastTimestamp) / (double)Stopwatch.Frequency;
        gv.LastTimestamp = now;

        const int MaxStepsPerPass = 5; // clamp to avoid a death spiral after a hitch
        double maxAccumulator = step * MaxStepsPerPass;
        if (gv.Accumulator > maxAccumulator) gv.Accumulator = maxAccumulator;

        int steps = 0;
        while (gv.Accumulator >= step && steps < MaxStepsPerPass)
        {
            uint tick = gv.GameState.Tick + 1; // the tick being produced (current tick derives from state; never passed in)
            if (!CanExecute(ref engine, tick))
            {
                gv.StalledSteps++;
                gv.Accumulator = 0; // don't bank time while stalled: resume gently, not in a burst
                break;
            }
            ExecuteTick(ref engine, advancePastFrontier: true);
            gv.Accumulator -= step;
            steps++;
        }

        CheckDesync(ref engine);

        // One bundle after advancing — and a heartbeat even while stalled, since the
        // redundant input window in these sends is what lets the remote catch up.
        const double HeartbeatSeconds = 1.0 / 60.0;
        if (steps > 0 || (now - gv.LastSendTimestamp) / (double)Stopwatch.Frequency >= HeartbeatSeconds)
        {
            SendMatchBundles(ref engine);
            gv.LastSendTimestamp = now;
        }
    }

    private static bool CanExecute(ref EngineState engine, uint tick)
    {
        ref GameViewState gv = ref engine.GameView;

        for (int i = 0; i < gv.Peers.Length; i++)
        {
            PeerState peer = gv.Peers[i];
            if (peer.IsLocal) continue; // self: frontier is always current, ack is definitionally current

            // Prediction bound: never simulate more than PredictionWindow ticks past the
            // newest remote input (Frontier is exclusive: newest covered = Frontier - 1).
            for (int p = 0; p < peer.Players.Length; p++)
            {
                if (tick + 1 > peer.Players[p].Frontier + PredictionWindow)
                    return false;
            }

            // Ack bound: never outrun the remote's ack of OUR inputs by more than the
            // redundant send window. This is what makes the fixed window lossless: a
            // packet stamped tick T always reaches back over everything the receiver
            // can still be missing, so holes cannot form.
            if (tick > peer.LastAckedTick + (uint)Input.Count)
                return false;
        }

        return true;
    }

    /// <summary>
    /// Execute exactly one sim tick. The tick number always derives from the state
    /// itself (<c>GameState.Tick + 1</c>): it is never passed in, so executing the
    /// wrong tick is inexpressible. When <paramref name="advancePastFrontier"/> is
    /// true (live play), local players' inputs are minted from their latest level
    /// sample; during a rollback replay the already-minted authoritative inputs are
    /// used verbatim (stamped inputs are final).
    /// </summary>
    private static void ExecuteTick(ref EngineState engine, bool advancePastFrontier)
    {
        ref GameViewState gv = ref engine.GameView;
        uint tick = gv.GameState.Tick + 1;

        if (advancePastFrontier)
        {
            // Mint local inputs for this tick: derive edges from level samples (the render
            // thread ships LEVELS — held buttons + aim; Dash is edge-triggered in the sim,
            // so the edge is derived HERE, at the stamping station, per executed tick).
            PlayerState[] locals = engine.LocalPeer.Players;
            for (int i = 0; i < locals.Length; i++)
            {
                PlayerState player = locals[i];
                PlayerInput level = player.LatestSample;
                InputButtons buttons = level.Buttons & ~InputButtons.Dash;
                if ((level.Buttons & InputButtons.Dash) != 0 && (player.PrevLevel.Buttons & InputButtons.Dash) == 0)
                    buttons |= InputButtons.Dash;
                player.PrevLevel = level;
                player.MintLocal(tick, new PlayerInput(buttons, level.TurretAim));
            }
        }

        // Fill the scratch inputs: authoritative where covered, predicted otherwise.
        for (int i = 0; i < gv.Players.Length; i++)
        {
            PlayerState player = gv.Players[i];
            PlayerInput input = player.ResolveInput(tick);
            player.RecordApplied(tick, input);
            gv.TickInputs[player.PlayerIndex] = input;
        }

        gv.Simulation.Tick(gv.GameState, gv.Arena, gv.TickInputs);
        Debug.Assert(gv.GameState.Tick == tick);
        gv.History.Record(gv.GameState);
        engine.EmitGameState(gv.GameState);
    }

    private static void RollBackIfMispredicted(ref EngineState engine)
    {
        ref GameViewState gv = ref engine.GameView;
        if (gv.Players is null) return; // no match state yet

        // New confirmation frontier: every tick <= C now has authoritative inputs from
        // every player. Local players never bind (their frontier tracks the current tick).
        uint c = gv.GameState.Tick;
        for (int i = 0; i < gv.Players.Length; i++)
        {
            PlayerState player = gv.Players[i];
            uint covered = player.Frontier == 0 ? 0 : player.Frontier - 1;
            if (covered < c) c = covered;
        }
        if (c <= gv.ConfirmedTick) return;

        // Scan the newly-confirmed range for the first tick whose actual input differs
        // from the prediction we executed with.
        uint firstWrong = 0;
        for (uint t = gv.ConfirmedTick + 1; t <= c && firstWrong == 0; t++)
        {
            for (int i = 0; i < gv.Players.Length; i++)
            {
                PlayerState player = gv.Players[i];
                PlayerInput actual = player.InputAt(t);
                PlayerInput applied = player.AppliedAt(t);
                if (!actual.Equals(applied))
                {
                    firstWrong = t;
                    break;
                }
            }
        }

        if (firstWrong != 0)
        {
            uint restoreTick = firstWrong - 1;
            uint target = gv.GameState.Tick;

            if (!gv.History.TryGet(restoreTick, out StateHistorySlot slot))
            {
                // Unreachable while the stall bounds hold (depth <= PredictionWindow << history capacity).
                engine.Logger.LogError($"rollback to tick {restoreTick} impossible: snapshot aged out (current {target})");
                return;
            }

            gv.GameState.CopyFrom(slot.State);
            gv.History.Rewind(restoreTick);

            // Replay to the present: authoritative inputs where known (the correction),
            // refreshed predictions past the frontier. Local inputs are already minted —
            // stamped inputs are final, so the replay consumes them verbatim.
            while (gv.GameState.Tick < target)
            {
                ExecuteTick(ref engine, advancePastFrontier: false);
            }

            gv.RollbackCount++;
            gv.TotalRolledBackTicks += target - restoreTick;
        }

        gv.ConfirmedTick = c;
    }

    private static void CheckDesync(ref EngineState engine)
    {
        ref GameViewState gv = ref engine.GameView;

        // Compare only mutually CONFIRMED ticks: confirmed states are final on both
        // sides, so hashes of them are exact (a speculative hash would flag every
        // remote rollback as a desync).
        uint upTo = gv.ConfirmedTick;
        for (uint t = gv.LastHashCheckedTick + 1; t <= upTo; t++)
        {
            for (int i = 0; i < gv.Peers.Length; i++)
            {
                PeerState peer = gv.Peers[i];
                if (peer.IsLocal) continue;
                if (!peer.TryGetRemoteHash(t, out ulong remoteHash)) continue; // hashes ride once per bundle, not per tick
                if (!gv.History.TryGet(t, out StateHistorySlot slot)) continue;

                if (slot.Hash != remoteHash && !gv.DesyncDetected)
                {
                    gv.DesyncDetected = true;
                    engine.Logger.LogError($"DESYNC at tick {t}: local hash {slot.Hash:X16} != remote hash {remoteHash:X16}");
                }
            }
        }
        if (upTo > gv.LastHashCheckedTick)
            gv.LastHashCheckedTick = upTo;
    }

    private static void SendMatchBundles(ref EngineState engine)
    {
        ref GameViewState gv = ref engine.GameView;

        // TODO(multi-local): the bundle carries ONE input stream (local player 0), and the
        // receive path maps it to peer.Players[0]. Multiple local players per peer need a
        // player id per Input message — negotiated at a future match handshake.
        PlayerState local = engine.LocalPeer.Players[0];
        if (local.Frontier == 0)
            return; // nothing minted yet — nothing to say

        uint newest = local.Frontier - 1;

        foreach (PeerState peer in engine.RemotePeers.Values)
        {
            if (peer.State != SessionState.Established) continue;

            BufferHandle handle = default;
            if (!engine.NetworkTx.TryRent(ref handle))
            {
                engine.Logger.LogError($"failed to send match bundle at tick {gv.GameState.Tick}: network TX pool exhausted");
                return;
            }

            bool consumed = false;
            try
            {
                byte* buffer = engine.NetworkTx.Deref(handle);
                var metadata = (NetworkEventMetadata*)buffer;
                metadata->SessionId = peer.LocalSessionId;
                metadata->Kind = NetworkEventKind.ApplicationData;
                metadata->Offset = (byte)sizeof(PacketHeader);

                byte* payloadStart = buffer + NetworkConstants.NetworkEventHeadroom + metadata->Offset;
                byte* cursor = payloadStart;

                // Input window: our newest Input.Count ticks, ring-keyed by tick % Count.
                // Redundancy is what makes per-packet loss harmless.
                var tag = (MatchMessageTag*)cursor;
                tag->Type = MatchMessageType.Input;
                cursor += sizeof(MatchMessageTag);
                var input = (Input*)cursor;
                input->InputTick = newest;
                var window = new Span<PlayerInput>(input->Inputs, Input.Count);
                for (uint t = newest; t != 0 && t + (uint)Input.Count > newest; t--)
                    window[(int)(t % Input.Count)] = local.InputAt(t);
                cursor += sizeof(Input);

                // State hash of our newest CONFIRMED tick (0 = none yet; receiver ignores).
                tag = (MatchMessageTag*)cursor;
                tag->Type = MatchMessageType.StateHash;
                cursor += sizeof(MatchMessageTag);
                var stateHash = (StateHash*)cursor;
                stateHash->Tick = 0;
                stateHash->Hash = 0;
                if (gv.ConfirmedTick != 0 && gv.History.TryGet(gv.ConfirmedTick, out StateHistorySlot slot))
                {
                    stateHash->Tick = gv.ConfirmedTick;
                    stateHash->Hash = slot.Hash;
                }
                cursor += sizeof(StateHash);

                // Advantage: CurrentTick doubles as liveness, LastTickRecvd is our ack of THEM.
                tag = (MatchMessageTag*)cursor;
                tag->Type = MatchMessageType.Advantage;
                cursor += sizeof(MatchMessageTag);
                var advantage = (Advantage*)cursor;
                advantage->CurrentTick = gv.GameState.Tick;
                advantage->LastTickRecvd = peer.LastTickRecvd;
                cursor += sizeof(Advantage);

                metadata->Length = (ushort)(cursor - payloadStart);

                if (!(consumed = engine.NetworkTx.TryEnqueue(handle)))
                {
                    engine.Logger.LogError($"failed to send match bundle at tick {gv.GameState.Tick}: network TX ring full");
                }
            }
            finally
            {
                if (!consumed)
                    engine.NetworkTx.Abandon(handle);
            }
        }
    }

    private static void DrainNetworkRx(ref EngineState engine)
    {
        while (engine.NetworkRx.TryDequeue(ref engine.Handle))
        {
            try
            {
                byte* buffer = engine.NetworkRx.Deref(engine.Handle);
                var metadata = (NetworkEventMetadata*)buffer;
                int totalLength = NetworkConstants.NetworkEventHeadroom + metadata->Offset + metadata->Length;
                if (totalLength > engine.NetworkRx.SlotSize)
                {
                    engine.Logger.LogWarning($"network RX dropped: total length {totalLength} exceeds slot size {engine.NetworkRx.SlotSize}");
                    continue;
                }
                byte* payload = buffer + NetworkConstants.NetworkEventHeadroom + metadata->Offset;

                if (metadata->Kind == NetworkEventKind.SessionClosed)
                {
                    if (metadata->Length == sizeof(SessionClosed))
                    {
                        var msg = (SessionClosed*)payload;
                        if (engine.RemotePeers.TryGetValue(metadata->SessionId, out PeerState peer))
                        {
                            // TODO: communicate reason(s) to render thread
                            engine.Logger.LogDebug($"session closed: {metadata->SessionId}");
                            peer.OnSessionClosed(ref *msg);
                            engine.RemotePeers.Remove(peer.LocalSessionId);
                            engine.Phase = PhaseState.ConnectScreen;
                            engine.Emit(EngineEventKind.MatchEnded, sizeof(EndMatchMessage));
                        }
                    }
                }

                // Lifecycle facts are phase-independent: sessions accept/establish/close on the
                // protocol's clock, not ours. (The active opener is already in ConnectingToPeer
                // when its own SessionAccepted arrives — a phase-gated handler would drop it.)
                switch (metadata->Kind)
                {
                    case NetworkEventKind.SessionAccepted:
                        if (metadata->Length == SessionAccepted.Size && engine.Phase != PhaseState.GameView)
                        {
                            var msg = (SessionAccepted*)payload;
                            engine.Logger.LogDebug($"session accepted: {metadata->SessionId} (peer: {msg->RemotePeerId})");
                            if (engine.RemotePeers.Remove(metadata->SessionId))
                                engine.Logger.LogWarning($"removed stale session {metadata->SessionId} (peer: {msg->RemotePeerId})");
                            var peer = new PeerState(256, 1, false);
                            peer.OnSessionAccepted(ref *msg);
                            engine.RemotePeers.Add(metadata->SessionId, peer);
                            engine.Phase = PhaseState.ConnectingToPeer;
                            engine.Emit(EngineEventKind.ConnectingToPeer);
                        }
                        break;
                    case NetworkEventKind.SessionEstablished:
                        if (metadata->Length == 0 && engine.Phase != PhaseState.GameView)
                        {
                            if (engine.RemotePeers.TryGetValue(metadata->SessionId, out PeerState peer))
                            {
                                engine.Logger.LogDebug($"session established: {metadata->SessionId}");
                                peer.OnSessionEstablished();

                                // NOTE: For now assume session established == start 1v1 match
                                CreateLocalPeer(ref engine, online: true);
                                StartMatch(ref engine);
                            }
                        }
                        break;
                    case NetworkEventKind.ApplicationData:
                        if (engine.Phase == PhaseState.GameView &&
                            engine.RemotePeers.TryGetValue(metadata->SessionId, out PeerState dataPeer))
                        {
                            HandleApplicationData(ref engine, payload, metadata->Length, dataPeer);
                        }
                        break;
                }
            }
            finally
            {
                engine.NetworkRx.Return(engine.Handle);
            }
        }
    }

    private static void StartMatch(ref EngineState engine)
    {
        // Derive player indices deterministically from peers
        PlayerState[] players;

        int peerCount = 1 + engine.RemotePeers.Count;
        var peers = new PeerState[peerCount];
        {
            int pi = 0;
            peers[pi++] = engine.LocalPeer;
            foreach (var peer in engine.RemotePeers.Values)
                peers[pi++] = peer;

            Array.Sort(peers, static (a, b) => a.RemotePeerId.CompareTo(b.RemotePeerId));

            int totalPlayerCount = 0;
            for (int peer = 0; peer < peerCount; peer++)
                totalPlayerCount += peers[peer].Players.Length;

            players = new PlayerState[totalPlayerCount];

            for (int peerIdx = 0, playerIdx = 0; peerIdx < peerCount; peerIdx++)
            {
                ref var peer = ref peers[peerIdx];

                int playerCount = peer.Players.Length;
                for (int i = 0; i < playerCount; i++)
                {
                    ref PlayerState player = ref peer.Players[i];
                    players[playerIdx] = player;
                    player.PlayerIndex = playerIdx;
                    playerIdx++;
                }
            }
        }

        var gameState = GameState.CreateInitial(engine.Config);
        var history = new StateHistory(256); // >= PlayerState ring capacity; rollback depth is bounded by PredictionWindow anyway
        history.Record(gameState); // genesis snapshot (tick 0): the floor every rollback can restore to

        long now = Stopwatch.GetTimestamp();
        engine.GameView = new GameViewState()
        {
            Arena = Arena.CreateDefault(engine.Config),
            GameState = gameState,
            Simulation = new Simulation(engine.Config),
            Peers = peers,
            Players = players,
            History = history,
            TickInputs = new PlayerInput[players.Length],
            LastTimestamp = now,
            LastSendTimestamp = now,
        };

        engine.Phase = PhaseState.GameView;
        engine.EmitMatchStarted();
    }

    private static void HandleApplicationData(ref EngineState engine, byte* payload, int length, PeerState peer)
    {
        if (peer.State != SessionState.Established)
            return;

        while (length > 0)
        {
            if (length < MatchMessageTag.Size)
            {
                engine.Logger.LogError("undersized match message (no tag)");
                return;
            }

            var tag = (MatchMessageTag*)payload;
            payload += MatchMessageTag.Size;
            length -= MatchMessageTag.Size;

            switch (tag->Type)
            {
                case MatchMessageType.Input:
                    if (length < Input.Size)
                    {
                        engine.Logger.LogError("undersized match Input message");
                        return;
                    }
                    var input = (Input*)payload;
                    if (input->InputTick > 0)
                    {
                        peer.OnTick(input->InputTick); // ack coverage: we heard about their tick
                        var inputs = new ReadOnlySpan<PlayerInput>((PlayerInput*)input->Inputs, Input.Count);
                        peer.Players[0].OnInput(input->InputTick, inputs); // TODO: support multiple local players in a multiplayer online match
                    }
                    length -= Input.Size;
                    payload += Input.Size;
                    break;
                case MatchMessageType.StateHash:
                    if (length < StateHash.Size)
                    {
                        engine.Logger.LogError("undersized match StateHash message");
                        return;
                    }
                    peer.OnStateHash(ref *(StateHash*)payload);
                    length -= StateHash.Size;
                    payload += StateHash.Size;
                    break;
                case MatchMessageType.Advantage:
                    if (length < Advantage.Size)
                    {
                        engine.Logger.LogError("undersized match Advantage message");
                        return;
                    }
                    peer.OnAdvantage(ref *(Advantage*)payload);
                    length -= Advantage.Size;
                    payload += Advantage.Size;
                    break;
                default:
                    engine.Logger.LogError($"unhandled match message type '{tag->Type}' from session '{peer.LocalSessionId}' with peer '{peer.RemotePeerId}'. dropped {length} bytes of application data");
                    return;
            }
        }
    }

    private static void DrainRenderRx(ref EngineState engine)
    {
        while (engine.RenderRx.TryDequeue(ref engine.Handle))
        {
            try
            {
                byte* buffer = engine.RenderRx.Deref(engine.Handle);
                var metadata = (EngineEventMetadata*)buffer;
                int totalLength = sizeof(EngineEventMetadata) + metadata->Offset + metadata->Length;
                if (totalLength > engine.RenderRx.SlotSize)
                {
                    engine.Logger.LogWarning($"render RX dropped: total length {totalLength} exceeds slot size {engine.RenderRx.SlotSize}.");
                    continue;
                }
                byte* payload = buffer + sizeof(EngineEventMetadata) + metadata->Offset;

                switch (engine.Phase)
                {
                    case PhaseState.ConnectScreen:
                        switch (metadata->Kind)
                        {
                            case EngineEventKind.StartMatch:
                                if (metadata->Length == sizeof(StartMatchMessage))
                                {
                                    CreateLocalPeer(ref engine, online: true);
                                    engine.Phase = PhaseState.ConnectingToPeer;
                                    var msg = (StartMatchMessage*)payload;
                                    engine.Network.Connect(msg->PeerId, msg->NetAddress.ToIPEndPoint());
                                }
                                break;
                            case EngineEventKind.StartLocalMatch:
                                if (metadata->Length == sizeof(StartLocalMatchMessage))
                                {
                                    engine.Logger.LogDebug("starting local match");

                                    CreateLocalPeer(ref engine, online: false);

                                    StartMatch(ref engine);
                                }
                                break;
                        }
                        break;
                    case PhaseState.ConnectingToPeer:
                        switch (metadata->Kind)
                        {
                            case EngineEventKind.LeaveMatch:
                                LeaveMatch(ref engine);
                                break;
                        }
                        break;
                    case PhaseState.GameView:
                        switch (metadata->Kind)
                        {
                            case EngineEventKind.LeaveMatch:
                                LeaveMatch(ref engine);
                                break;
                            case EngineEventKind.LocalInput:
                                if (metadata->Length > sizeof(LocalInputMessage))
                                {
                                    var header = (LocalInputMessage*)payload;
                                    byte* cursor = payload + sizeof(LocalInputMessage);
                                    int computedLength = sizeof(LocalInputMessage) + header->Count * sizeof(LocalInputPayload);
                                    if (computedLength < engine.RenderRx.SlotSize && computedLength == metadata->Length)
                                    {
                                        for (int i = 0; i < header->Count; i++)
                                        {
                                            var msg = (LocalInputPayload*)cursor;
                                            // Samples are LEVELS and arrive unstamped: tick assignment happens
                                            // at the mint (ExecuteTick), never here. Extra players (e.g. couch
                                            // sampler during an online 1-local-player match) are ignored.
                                            if (msg->Player < engine.LocalPeer.Players.Length)
                                                engine.LocalPeer.Players[msg->Player].OnInputSample(msg->Input);
                                            cursor += sizeof(LocalInputPayload);
                                        }
                                        Debug.Assert((int)(cursor - payload) == computedLength);
                                    }
                                }
                                break;
                        }
                        break;
                }
            }
            finally
            {
                engine.RenderRx.Return(engine.Handle);
            }
        }

        static void LeaveMatch(ref EngineState engine)
        {
            foreach (var sid in engine.RemotePeers.Keys)
            {
                engine.Network.Disconnect(sid);
            }
            engine.RemotePeers.Clear();
            engine.Emit(EngineEventKind.MatchEnded, sizeof(EndMatchMessage));
            engine.Phase = PhaseState.ConnectScreen;
        }
    }

    private static void CreateLocalPeer(ref EngineState engine, bool online)
    {
        if (online)
        {
            engine.LocalPeer = new PeerState(256, 1, true);
            SessionAccepted sessionAccepted = new SessionAccepted
            {
                LocalPeerId = engine.Network.LocalPeerId,
                RemotePeerId = engine.Network.LocalPeerId,
                LocalSessionId = default,
                RemoteSessionId = default,
            };
            engine.LocalPeer.OnSessionAccepted(ref sessionAccepted);
            engine.LocalPeer.OnSessionEstablished();
        }
        else
        {
            engine.LocalPeer = new PeerState(256, engine.Config.PlayerCount, true);
            SessionAccepted sessionAccepted = default;
            engine.LocalPeer.OnSessionAccepted(ref sessionAccepted);
            engine.LocalPeer.OnSessionEstablished();
        }
    }
}
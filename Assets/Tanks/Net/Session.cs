using System;

namespace Tanks.Net;

// Game thread's concrete view of the session.
//
// Tick numbering: the first executed sim tick is 1 (CreateInitial is tick 0), so
// tick 0 in any message field means "nothing yet" — no special-casing needed.
public sealed class Session : ISession
{
    private readonly int _capacity;

    private readonly PeerInput[] _inputs;
    private uint _latestInputTick;

    private readonly StateHash[] _hashes;

    private uint _lastTickRecvd;
    private uint _lastAckedTick;

    private SessionState _state;
    private SessionAccepted _sessionAccepted;
    private SessionClosed _sessionClosed;

    public Session(int capacity)
    {
        ThrowHelper.ThrowIfNegativeOrZero(capacity);

        _capacity = capacity;
        _inputs = new PeerInput[capacity];
        _hashes = new StateHash[capacity];
    }

    public ref readonly PeerId LocalPeerId => ref _sessionAccepted.LocalPeerId;
    public ref readonly PeerId RemotePeerId => ref _sessionAccepted.RemotePeerId;
    public ref readonly SessionId LocalSessionId => ref _sessionAccepted.LocalSessionId;
    public ref readonly SessionId RemoteSessionId => ref _sessionAccepted.RemoteSessionId;
    public SessionState State => _state;
    public ref readonly DisconnectReason PeerReason => ref _sessionClosed.PeerReason;
    public ref readonly EndReason EndReason => ref _sessionClosed.EndReason;

    /// <summary>Newest tick heard from the remote in any message — what we ack back to them.</summary>
    public uint LastTickRecvd => _lastTickRecvd;

    /// <summary>Newest remote input tick received. Contiguous coverage below this is
    /// guaranteed by the sender-side ack bound (see RollbackDriver.CanExecute).</summary>
    public uint LatestInputTick => _latestInputTick;

    /// <summary>Newest of OUR ticks the remote reports having received (their ack).</summary>
    public uint LastAckedTick => _lastAckedTick;

    /// <summary>The remote input for <paramref name="tick"/>. Only meaningful for
    /// ticks in (LatestInputTick - capacity, LatestInputTick].</summary>
    public PeerInput InputAt(uint tick) => _inputs[(int)(tick % (uint)_capacity)];

    /// <summary>The remote's state hash for <paramref name="tick"/>, if one arrived.</summary>
    public bool TryGetRemoteHash(uint tick, out ulong hash)
    {
        ref readonly StateHash slot = ref _hashes[(int)(tick % (uint)_capacity)];
        if (tick != 0 && slot.Tick == tick)
        {
            hash = slot.Hash;
            return true;
        }
        hash = 0;
        return false;
    }

    public void OnSessionAccepted(ref SessionAccepted evt)
    {
        _sessionAccepted = evt;
        _state = SessionState.Init;
    }

    public void OnSessionEstablished()
    {
        _state = SessionState.Established;
    }

    public void OnSessionClosed(ref SessionClosed evt)
    {
        _sessionClosed = evt;
        _state = SessionState.Closed;
    }

    public void OnInput(uint inputTick, ReadOnlySpan<PeerInput> inputs)
    {
        if (inputTick > _lastTickRecvd)
            _lastTickRecvd = inputTick;

        if (inputTick <= _latestInputTick)
            return; // stale or duplicate window; everything in it is already stored

        uint latest = _latestInputTick;
        _latestInputTick = inputTick;

        // The window carries the sender's last inputs.Length ticks (ring keyed by
        // tick % length). Copy the ones we haven't seen, newest down to latest+1.
        int count = inputTick < (uint)inputs.Length ? (int)inputTick : inputs.Length;

        uint tick = inputTick;
        for (int i = 0; i < count && tick > latest; i++, tick--)
        {
            int srcIdx = (int)(tick % (uint)inputs.Length);
            int dstIdx = (int)(tick % (uint)_capacity);
            _inputs[dstIdx] = inputs[srcIdx];
        }
    }

    public void OnStateHash(ref StateHash msg)
    {
        if (msg.Tick > _lastTickRecvd)
            _lastTickRecvd = msg.Tick;

        if (msg.Tick == 0)
            return; // sender had no confirmed tick yet

        int idx = (int)(msg.Tick % (uint)_capacity);
        ref var slot = ref _hashes[idx];
        if (msg.Tick > slot.Tick)
        {
            slot = msg;
        }
    }

    public void OnAdvantage(ref Advantage msg)
    {
        if (msg.CurrentTick > _lastTickRecvd)
            _lastTickRecvd = msg.CurrentTick;

        if (msg.LastTickRecvd > _lastAckedTick)
            _lastAckedTick = msg.LastTickRecvd;

        // TODO(advantage): tick-advantage throttling lands in a follow-up. The ack half
        // of this message is already load-bearing (see RollbackDriver.CanExecute).
    }
}

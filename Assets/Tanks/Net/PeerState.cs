using System;
using Tanks.Sim;

namespace Tanks.Net;

public sealed class PeerState
{
    private readonly int _capacity;
    private readonly int _playerCount;
    private readonly StateHash[] _hashes;
    private uint _lastTickRecvd;
    private uint _lastAckedTick;

    private SessionState _state;
    private SessionAccepted _sessionAccepted;
    private SessionClosed _sessionClosed;

    public PeerState(int capacity, int playerCount, bool isLocal)
    {
        ThrowHelper.ThrowIfNegativeOrZero(capacity);
        ThrowHelper.ThrowIfNegativeOrZero(playerCount);
        _capacity = capacity;
        _playerCount = playerCount;
        _hashes = new StateHash[_capacity];
        _state = SessionState.Init;
        IsLocal = isLocal;
        Players = new PlayerState[_playerCount];
        for (int i = 0; i < _playerCount; i++)
            Players[i] = new PlayerState(_capacity);
    }

    public readonly bool IsLocal;
    public readonly PlayerState[] Players;

    /// <summary>Newest of THEIR ticks we've heard about in any message (what we ack back).</summary>
    public uint LastTickRecvd => _lastTickRecvd;

    /// <summary>Newest of OUR ticks the remote reports having received (their ack of us).</summary>
    public uint LastAckedTick => _lastAckedTick;

    /// <summary>This peer's state hash for <paramref name="tick"/>, if one arrived.</summary>
    public bool TryGetRemoteHash(uint tick, out ulong hash)
    {
        ref readonly StateHash slot = ref _hashes[(int)(tick % (uint)_capacity)];
        if (slot.Tick == tick && tick != 0)
        {
            hash = slot.Hash;
            return true;
        }
        hash = 0;
        return false;
    }

    public void Reset()
    {
        _state = SessionState.Init;
        _sessionAccepted = default;
        _lastTickRecvd = 0;
        _lastAckedTick = 0;
        Array.Clear(_hashes, 0, _hashes.Length);
        for (int i = 0; i < Players.Length; i++)
            Players[i].Reset();
    }

    public ref readonly PeerId LocalPeerId => ref _sessionAccepted.LocalPeerId;
    public ref readonly PeerId RemotePeerId => ref _sessionAccepted.RemotePeerId;
    public ref readonly SessionId LocalSessionId => ref _sessionAccepted.LocalSessionId;
    public ref readonly SessionId RemoteSessionId => ref _sessionAccepted.RemoteSessionId;
    public SessionState State => _state;
    public ref readonly DisconnectReason PeerReason => ref _sessionClosed.PeerReason;
    public ref readonly EndReason EndReason => ref _sessionClosed.EndReason;

    // Match events

    public void OnTick(uint tick)
    {
        if (tick > _lastTickRecvd)
            _lastTickRecvd = tick;
    }

    public void OnStateHash(ref StateHash msg)
    {
        if (msg.Tick > _lastTickRecvd)
            _lastTickRecvd = msg.Tick;

        int idx = (int)(msg.Tick % _capacity);
        ref var slot = ref _hashes[idx];
        if (msg.Tick > slot.Tick || (msg.Tick == 0 && slot.Tick == 0))
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
        // (LastAckedTick) is already load-bearing: it feeds the engine's ack stall bound,
        // which is what keeps the fixed 8-slot input window lossless (a packet stamped
        // tick T always reaches back far enough to cover anything the receiver misses).
    }

    // Peer lifecycle

    public void OnSessionAccepted(ref SessionAccepted evt)
    {
        if (_state == SessionState.Init || _state == SessionState.Closed)
        {
            Reset();
            _sessionAccepted = evt;
            _state = SessionState.Init;
        }
    }

    public void OnSessionEstablished()
    {
        if (_state == SessionState.Init)
        {
            _state = SessionState.Established;
        }
    }

    public void OnSessionClosed(ref SessionClosed evt)
    {
        _sessionClosed = evt;
        _state = SessionState.Closed;
    }
}
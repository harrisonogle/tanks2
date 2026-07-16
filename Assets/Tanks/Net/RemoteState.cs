using System;
using System.Collections.Generic;
using System.Diagnostics;
using Tanks.Sim;

namespace Tanks.Net;

// The game thread's pump for the protocol pipe: drains RX (session lifecycle +
// match messages) into per-session state, and emits the per-frame outbound bundle
// (redundant input window + confirmed state hash + advantage/ack).
//
// Single-threaded by contract: Poll/RecordLocalInput/Send and the callbacks all
// run on the game thread. The pipe rings are the only surface shared with the
// net thread.
public unsafe class RemoteState
{
    private readonly INetwork _network;
    private readonly NetworkPipe _pipe;
    private readonly ILog _logger;
    private readonly Dictionary<SessionId, Session> _sessions;
    private Session? _recycle;
    private Input _localInputs; // mutable: the ring of our last Input.Count inputs, resent every frame

    /// <summary>Fired when a session finishes its handshake (game can start a match).</summary>
    public Action<Session>? OnEstablished;

    /// <summary>Fired when a session ends, after PeerReason/EndReason are populated.</summary>
    public Action<Session>? OnClosed;

    public RemoteState(INetwork network, ILog logger)
    {
        ThrowHelper.ThrowIfNull(network);
        ThrowHelper.ThrowIfNull(network.Pipe);
        ThrowHelper.ThrowIfNull(logger);

        _network = network;
        _pipe = _network.Pipe;
        _logger = logger;
        _sessions = new();
    }

    public Enumerator GetEnumerator() => new Enumerator(this);

    public struct Enumerator
    {
        private Dictionary<SessionId, Session>.Enumerator _it;
        internal Enumerator(RemoteState value) => _it = value._sessions.GetEnumerator();
        public bool MoveNext() => _it.MoveNext();
        public KeyValuePair<SessionId, Session> Current => _it.Current;
        public void Dispose() => _it.Dispose();
    }

    /// <summary>Drain everything the net thread queued since the last frame.</summary>
    public void Poll()
    {
        BufferHandle handle = default;

        while (_pipe.RX.Ring.TryDequeue(ref handle))
        {
            try
            {
                byte* buffer = _pipe.RX.Pool.Deref(handle);
                var metadata = (PipeEventMetadata*)buffer;
                Debug.Assert(metadata->Offset + metadata->Length <= _pipe.RX.Pool.SlotSize);
                byte* payload = buffer + NetworkConstants.PipeEventHeadroom + metadata->Offset;
                int length = metadata->Length;
                SessionId sid = metadata->SessionId;

                switch (metadata->Kind)
                {
                    case PipeEventKind.SessionAccepted:
                        HandleSessionAccepted(sid, payload, length);
                        break;
                    case PipeEventKind.SessionEstablished:
                        HandleSessionEstablished(sid, payload, length);
                        break;
                    case PipeEventKind.SessionClosed:
                        HandleSessionClosed(sid, payload, length);
                        break;
                    case PipeEventKind.ApplicationData:
                        HandleApplicationData(sid, payload, length);
                        break;
                    default:
                        _logger.LogWarning($"unhandled event from RX ring: {metadata->Kind}");
                        break;
                }
            }
            finally
            {
                _pipe.RX.Pool.Return(handle);
            }
        }
    }

    /// <summary>
    /// Store the local input for <paramref name="tick"/> in the redundant send window.
    /// Called once per EXECUTED tick (including multiple ticks in one frame) so every
    /// tick's input enters the window; <see cref="Send"/> then reships the whole window
    /// every frame, which is what makes packet loss survivable.
    /// </summary>
    public void RecordLocalInput(uint tick, in PlayerInput input)
    {
        fixed (byte* p = _localInputs.Inputs)
        {
            ((PeerInput*)p)[(int)(tick % Input.Count)] = new PeerInput { Buttons = input.Buttons, TurretAim = input.TurretAim };
        }
        if (tick > _localInputs.InputTick)
            _localInputs.InputTick = tick;
    }

    /// <summary>
    /// Emit one outbound bundle to every established session: the input window, our
    /// newest CONFIRMED state hash (confirmed ticks are final — a hash of a
    /// speculative tick would flag every remote rollback as a desync), and the
    /// advantage/ack message. Called once per frame — also while stalled, since the
    /// resends are exactly what clear a stall.
    /// </summary>
    public void Send(uint currentTick, uint confirmedHashTick, ulong confirmedHash)
    {
        if (_localInputs.InputTick == 0)
            return; // no tick executed yet — nothing to say

        if (_pipe.TX.Pool.SlotSize < NetworkConstants.PipeEventHeadroom + sizeof(PacketHeader) +
            3 * sizeof(MatchMessageTag) + sizeof(Input) + sizeof(StateHash) + sizeof(Advantage))
        {
            _logger.LogError($"failed to emit TX at tick {currentTick}: insufficient slot size");
            return;
        }

        foreach (Session session in _sessions.Values)
        {
            if (session.State != SessionState.Established) continue;

            BufferHandle handle = default;

            if (!_pipe.TX.Pool.TryRent(ref handle))
            {
                _logger.LogError($"failed to send inputs at tick {currentTick}: TX pool exhausted");
                return;
            }

            bool consumedBuffer = false;

            try
            {
                byte* buffer = _pipe.TX.Pool.Deref(handle);
                var metadata = (PipeEventMetadata*)buffer;
                metadata->SessionId = session.LocalSessionId;
                metadata->Kind = PipeEventKind.ApplicationData;
                metadata->Offset = (byte)sizeof(PacketHeader);

                // Payload begins after the pipe headroom plus the room the net thread
                // uses for the packet header; Length counts payload bytes only.
                byte* payloadStart = buffer + NetworkConstants.PipeEventHeadroom + metadata->Offset;
                byte* cursor = payloadStart;

                // Send inputs
                var tag = (MatchMessageTag*)cursor;
                tag->Type = MatchMessageType.Input;
                cursor += sizeof(MatchMessageTag);
                var input = (Input*)cursor;
                *input = _localInputs;
                cursor += sizeof(Input);

                // Send state hash (of our newest confirmed tick; 0 = none yet, receiver drops it)
                tag = (MatchMessageTag*)cursor;
                tag->Type = MatchMessageType.StateHash;
                cursor += sizeof(MatchMessageTag);
                var stateHash = (StateHash*)cursor;
                stateHash->Tick = confirmedHashTick;
                stateHash->Hash = confirmedHash;
                cursor += sizeof(StateHash);

                // Send advantage (CurrentTick doubles as liveness; LastTickRecvd is our ack)
                tag = (MatchMessageTag*)cursor;
                tag->Type = MatchMessageType.Advantage;
                cursor += sizeof(MatchMessageTag);
                var advantage = (Advantage*)cursor;
                advantage->CurrentTick = currentTick;
                advantage->LastTickRecvd = session.LastTickRecvd;
                cursor += sizeof(Advantage);

                // Stamp the length
                metadata->Length = (ushort)(cursor - payloadStart);

                if (!(consumedBuffer = _pipe.TX.Ring.TryEnqueue(handle)))
                {
                    _logger.LogError($"failed to send inputs at tick {currentTick}: TX ring full");
                }
            }
            finally
            {
                if (!consumedBuffer)
                {
                    _pipe.TX.Pool.Abandon(handle);
                }
            }
        }
    }

    private void HandleSessionAccepted(SessionId sid, byte* payload, int length)
    {
        if (length != SessionAccepted.Size)
        {
            _logger.LogError("dropped undersized SessionAccepted payload from RX ring");
            return;
        }

        var msg = (SessionAccepted*)payload;
        Session session = _recycle ??= new Session(256);

        if (!_sessions.TryAdd(msg->LocalSessionId, session))
        {
            _logger.LogError($"failed to index accepted session '{msg->LocalSessionId}' with peer '{msg->RemotePeerId}'");

            if (_sessions.TryGetValue(msg->LocalSessionId, out var existingSession))
            {
                _network.Disconnect(existingSession.LocalSessionId);
            }

            return;
        }

        _recycle = null;
        session.OnSessionAccepted(ref *msg); // mark the session as accepted
        _logger.LogDebug($"session '{msg->LocalSessionId}' accepted with remote peer '{msg->RemotePeerId}'");
    }

    private void HandleSessionEstablished(SessionId sid, byte* payload, int length)
    {
        /* empty payload */

        if (_sessions.TryGetValue(sid, out Session session))
        {
            session.OnSessionEstablished(); // mark the session as established
            OnEstablished?.Invoke(session);
        }
        else
        {
            _logger.LogError($"established session '{sid}' not found");
            _network.Disconnect(sid);
        }
    }

    private void HandleSessionClosed(SessionId sid, byte* payload, int length)
    {
        if (length != SessionClosed.Size)
        {
            _logger.LogError($"invalid SessionClosed payload of length {length}");
            return;
        }

        var msg = (SessionClosed*)payload;

        if (_sessions.Remove(sid, out Session session))
        {
            session.OnSessionClosed(ref *msg); // populate reasons + mark closed
            _logger.LogDebug($"session '{sid}' closed. PeerReason={msg->PeerReason}, EndReason={msg->EndReason}, RemotePeerId:{session.RemotePeerId}");
            OnClosed?.Invoke(session);
        }
        else
        {
            _logger.LogWarning($"closed session '{sid}' not found");
        }
    }

    private void HandleApplicationData(SessionId sid, byte* payload, int length)
    {
        Session session;
        if (!_sessions.TryGetValue(sid, out session))
        {
            _logger.LogError($"session '{sid}' not found. dropped {length} bytes of application data");
            return;
        }

        if (session.State != SessionState.Established)
        {
            _logger.LogError($"session '{sid}' lifecycle corrupted - received {length} bytes of application data in state '{session.State}'");
            _network.Disconnect(sid);
            return;
        }

        while (length > 0)
        {
            if (length < MatchMessageTag.Size)
            {
                _logger.LogError("undersized match message (no tag)");
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
                        _logger.LogError("undersized match Input message");
                        return;
                    }
                    var input = (Input*)payload;
                    var inputs = new ReadOnlySpan<PeerInput>((PeerInput*)input->Inputs, Input.Count);
                    session.OnInput(input->InputTick, inputs);
                    length -= Input.Size;
                    payload += Input.Size;
                    break;
                case MatchMessageType.StateHash:
                    if (length < StateHash.Size)
                    {
                        _logger.LogError("undersized match StateHash message");
                        return;
                    }
                    session.OnStateHash(ref *(StateHash*)payload);
                    length -= StateHash.Size;
                    payload += StateHash.Size;
                    break;
                case MatchMessageType.Advantage:
                    if (length < Advantage.Size)
                    {
                        _logger.LogError("undersized match Advantage message");
                        return;
                    }
                    session.OnAdvantage(ref *(Advantage*)payload);
                    length -= Advantage.Size;
                    payload += Advantage.Size;
                    break;
                default:
                    _logger.LogError($"unhandled match message type '{tag->Type}' from session '{sid}' with peer '{session?.RemotePeerId}'. dropped {length} bytes of application data");
                    return;
            }
        }
    }
}

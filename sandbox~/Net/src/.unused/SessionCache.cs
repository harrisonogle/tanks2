using System;
using System.Collections.Generic;
using System.Diagnostics;
using Tanks.Sim;

namespace Tanks.Net;

public unsafe class SessionCache
{
    private readonly INetwork _network;
    private readonly UmemPool _rx;
    private readonly UmemPool _tx;
    private readonly ILog _logger;
    private readonly Dictionary<SessionId, Session> _sessions;
    private Session? _recycle;
    private readonly Input _localInputs;

    public SessionCache(INetwork network, ILog logger)
    {
        ThrowHelper.ThrowIfNull(network);
        ThrowHelper.ThrowIfNull(network.Umem);
        ThrowHelper.ThrowIfNull(logger);

        _network = network;
        _rx = _network.Umem.Rx;
        _tx = _network.Umem.Tx;
        _logger = logger;
        _sessions = new();
        _localInputs = new();
    }

    public Enumerator GetEnumerator() => new Enumerator(this);

    public struct Enumerator
    {
        private Dictionary<SessionId, Session>.Enumerator _it;
        internal Enumerator(SessionCache value) => _it = value._sessions.GetEnumerator();
        public bool MoveNext() => _it.MoveNext();
        public KeyValuePair<SessionId, Session> Current => _it.Current;
        public void Dispose() => _it.Dispose();
    }

    public void DrainRx()
    {
        BufferHandle handle = default;

        while (_rx.TryDequeue(ref handle))
        {
            try
            {
                byte* buffer = _rx.Deref(handle);
                var metadata = (NetworkEventMetadata*)buffer;
                Debug.Assert(metadata->Offset + metadata->Length <= _rx.SlotSize);
                byte* payload = buffer + sizeof(NetworkEventMetadata) + metadata->Offset;
                int length = metadata->Length;
                SessionId sid = metadata->SessionId;

                switch (metadata->Kind)
                {
                    case NetworkEventKind.SessionAccepted:
                        HandleSessionAccepted(sid, payload, length);
                        break;
                    case NetworkEventKind.SessionEstablished:
                        HandleSessionEstablished(sid, payload, length);
                        break;
                    case NetworkEventKind.SessionClosed:
                        HandleSessionClosed(sid, payload, length);
                        break;
                    case NetworkEventKind.ApplicationData:
                        HandleApplicationData(sid, payload, length);
                        break;
                    default:
                        _logger.LogWarning($"unhandled event from RX ring: {metadata->Kind}");
                        break;
                }
            }
            finally
            {
                _rx.Return(handle);
            }
        }
    }

    // TODO: zero-copy
    public void EmitTx(uint tick, ref Input input, ref StateHash stateHash, ref Advantage advantage)
    {
        if (_tx.SlotSize < sizeof(NetworkEventMetadata) + sizeof(PacketHeader) +
            3 * sizeof(MatchMessageTag) + sizeof(Input) + sizeof(StateHash) + sizeof(Advantage))
        {
            _logger.LogError($"failed to emit TX at tick {tick}: insufficient slot size");
            return;
        }

        // Send our inputs and other state to everyone we have an established session with,
        // since we currently only do that to play a game
        foreach (Session session in _sessions.Values)
        {
            if (session.State != SessionState.Established) continue;

            BufferHandle handle = default;

            if (!_tx.TryRent(ref handle))
            {
                _logger.LogError($"failed to send inputs at tick {tick}: TX pool exhausted");
                return;
            }

            bool consumedBuffer = false;

            try
            {
                byte* buffer = _tx.Deref(handle);
                var metadata = (NetworkEventMetadata*)buffer;
                metadata->SessionId = session.LocalSessionId;
                metadata->Kind = NetworkEventKind.ApplicationData;
                metadata->Offset = (byte)sizeof(PacketHeader);

                byte* cursor = buffer;

                // Send inputs
                var tag = (MatchMessageTag*)cursor;
                tag->Type = MatchMessageType.Input;
                cursor += sizeof(MatchMessageTag);
                *(Input*)cursor = input;
                cursor += sizeof(Input);

                // Send state hash
                tag = (MatchMessageTag*)cursor;
                tag->Type = MatchMessageType.StateHash;
                cursor += sizeof(MatchMessageTag);
                *(StateHash*)cursor = stateHash;
                cursor += sizeof(StateHash);

                // Send advantage
                tag = (MatchMessageTag*)cursor;
                tag->Type = MatchMessageType.Advantage;
                cursor += sizeof(MatchMessageTag);
                *(Advantage*)cursor = advantage;
                cursor += sizeof(Advantage);

                // Stamp the length
                metadata->Length = (ushort)(cursor - buffer);

                if (!(consumedBuffer = _tx.TryEnqueue(handle)))
                {
                    _tx.Abandon(handle);
                    consumedBuffer = true;
                    _logger.LogError($"failed to send inputs at tick {tick}: TX ring full");
                }
            }
            finally
            {
                if (!consumedBuffer)
                {
                    _tx.Abandon(handle);
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
        Session session;

        if (_sessions.Remove(sid, out session))
        {
            _logger.LogDebug($"session '{sid}' closed. PeerReason={msg->PeerReason}, EndReason={msg->EndReason}, RemotePeerId:{session?.RemotePeerId}");
        }
        else
        {
            if (_sessions.TryGetValue(sid, out session))
            {
                _logger.LogError($"failed to remove closed session '{sid}' with peer '{session?.RemotePeerId}'");
                _network.Disconnect(sid);
            }
            else
            {
                _logger.LogWarning($"closed session '{sid}' not found");
            }
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
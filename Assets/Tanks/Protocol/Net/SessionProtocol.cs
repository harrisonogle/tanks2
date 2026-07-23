#pragma warning disable CS0164 // unreferenced goto label
using System;
using System.Diagnostics;
using System.Security.Cryptography;

namespace Tanks.Net;

public sealed unsafe class SessionProtocol
{
    private static long GetStopwatchTicks(TimeSpan duration) => (long)(duration.TotalSeconds * Stopwatch.Frequency);
    private static readonly long HedgeIntervalTicks = GetStopwatchTicks(NetworkConstants.HedgeInterval);
    private static readonly int HedgeSendCount = Math.Max(1, (int)(NetworkConstants.HedgeTimeout / NetworkConstants.HedgeInterval));
    private static readonly long SessionIdleTimeoutTicks = GetStopwatchTicks(NetworkConstants.SessionIdleTimeout);
    private static readonly long KeepAliveIntervalTicks = GetStopwatchTicks(NetworkConstants.KeepAliveInterval);
    private readonly byte* _txBuffer; // private buffer for outbound protocol packets
    private readonly int _txBufferLength;
    private readonly SessionContext _ctx;
    private readonly IDatagramTransport _transport;
    private readonly ILog _logger;
    private readonly NetworkMetrics _metrics;
    private readonly UmemPool _tx;
    private readonly UmemPool _rx;
    private BufferHandle _buffer;
    private LifecycleEvents _lifecycle;

    public SessionProtocol(
        byte* txBuffer,
        int txBufferLength,
        SessionContext ctx,
        IDatagramTransport transport,
        Umem umem,
        ILog logger,
        NetworkMetrics metrics)
    {
        _txBuffer = txBuffer;
        _txBufferLength = txBufferLength;
        _ctx = ctx;
        _transport = transport;
        _logger = logger;
        _metrics = metrics;
        _tx = umem.Tx;
        _rx = umem.Rx;

        _lifecycle.Reset();
        ClearHedge();
        Context = _ctx;
    }

    public readonly SessionContext Context;

    // Handshake hedge state. Set by the state machine when it enters
    // a hedge-sending state (PingSent as initiator, or "just sent Pong" as responder).
    public int HedgeSendsRemaining; // 0 means done
    public long HedgeBeginTicks;
    public long HedgeNextSendTicks; // absolute Stopwatch ticks
    public DisconnectReason DisconnectReason; // for hedging disconnect packets
    public bool HasPendingLifecycle => _lifecycle.Pending > 0;
    public bool IsAlive => _ctx.State != SessionState.Closed || _lifecycle.Pending > 0;

    // NOTE: bodyLen includes auth tag if Pong, otherwise it doesn't
    public void OnReceive(
        PacketHeader* packet, int packetLength,
        byte* body, int bodyLength, ref NetAddress source)
    {
        switch (_ctx.State)
        {
            case SessionState.PingSent:
                switch (packet->Type)
                {
                    case PacketType.Pong:
                        HandlePong(packet, packetLength, body, bodyLength, ref source);
                        break;
                    case PacketType.Ping:
                    case PacketType.Disconnect:
                    default:
                        // Even drop disconnect requests in PingSent because
                        // no session key means no authentication means no way
                        // to trust the sender is the peer.
                        _metrics.RxDroppedBadState++;
                        _logger.LogWarning($"[{_ctx.State}] {_ctx.LocalSessionId} dropped {packet->Type}");
                        break;
                }
                break;
            case SessionState.Established:
                switch (packet->Type)
                {
                    case PacketType.Disconnect:
                        HandleRemoteDisconnect(packet, packetLength, body, bodyLength, ref source);
                        break;
                    case PacketType.KeepAlive:
                        _logger.LogTrace($"{_ctx.LocalSessionId} recv KeepAlive from {packet->PeerId}/{packet->SSID}");
                        UpdateLastRecv();
                        break;
                    case PacketType.Ping:
                    case PacketType.Pong:
                        // Drop everything in these states
                        break;
                    default:
                        _metrics.RxDroppedBadState++;
                        _logger.LogWarning($"[{_ctx.State}] {_ctx.LocalSessionId} dropped {packet->Type}");
                        break;
                }
                break;
            case SessionState.Closing:
                switch (packet->Type)
                {
                    case PacketType.Disconnect:
                        ClearHedge(); // stop hedging Disconnect messages
                        break;
                    default:
                        _metrics.RxDroppedBadState++;
                        _logger.LogWarning($"[{_ctx.State}] {_ctx.LocalSessionId} dropped {packet->Type}");
                        break;
                }
                break;
            case SessionState.Closed:
                // Drop everything in these states
                break;
            default:
                // TODO: metric
                _logger.LogError($"invalid session state '{_ctx.State}' for session '{_ctx.LocalSessionId}/{_ctx.RemoteSessionId}' (peer {_ctx.RemotePeerId})");
                break;
        }
    }

    public void OnTick(long nowTicks)
    {
        FlushPendingLifecycle();

        // Compute idle duration and disconnect if session timed out
        if (_ctx.State != SessionState.Closing && _ctx.State != SessionState.Closed)
        {
            long idleTicks = nowTicks - _ctx.LastRecvTimestamp;

            if (idleTicks > SessionIdleTimeoutTicks)
            {
                // Timeout is redundant if already closing or closed
                _logger.LogWarning($"peer '{_ctx.RemotePeerId}' session '{_ctx.LocalSessionId}' timed out. ({TimeSpan.FromSeconds(idleTicks / (double)Stopwatch.Frequency)})");
                Disconnect(DisconnectReason.Timeout, EndReason.Timeout);
                return; // already sent Disconnect; don't hedge
            }
        }

        // Send "hedged" (duplicated) protocol packet(s), if applicable.
        // Rationale: transport does not guarantee packet delivery.
        // Hedging is fine; don't need reliability for handshake - timeout covers all failure modes.
        if (HedgeSendsRemaining > 0)
        {
            if (nowTicks >= HedgeNextSendTicks)
            {
                switch (_ctx.State)
                {
                    case SessionState.PingSent:
                        TrySendPing();
                        break;
                    case SessionState.Established:
                        if (_ctx.LastRecvTimestamp > HedgeBeginTicks)
                        {
                            // Peer sent an authenticated packet, so stop hedging
                            ClearHedge();
                            break;
                        }
                        TrySendPong();
                        break;
                    case SessionState.Closing:
                        TrySendDisconnect(DisconnectReason);
                        break;
                }

                HedgeSendsRemaining--;
                HedgeNextSendTicks = nowTicks + HedgeIntervalTicks;
            }
        }
        else if (_ctx.State == SessionState.Closing)
        {
            // Done hedging disconnect. Safe to reap
            Transition(SessionState.Closed);
            return;
        }

        // TODO: separate session timeout and keepalive timeout
        //       for now it's irrelevant; slowloris is possible sans keepalive via game messages
        if (_ctx.State == SessionState.Established)
        {
            long silentTicks = nowTicks - _ctx.LastSendTimestamp;

            if (silentTicks > KeepAliveIntervalTicks)
            {
                TrySendKeepAlive();
            }
        }
    }

    // Handle a Pong message received when we're in PingSent state
    private void HandlePong(
        PacketHeader* packet, int packetLen,
        byte* body, int bodyLen, ref NetAddress source)
    {
        // paranoid redundant assertions
        Debug.Assert(packet->Type == PacketType.Pong);
        Debug.Assert((byte*)packet + PacketHeader.Size == body);
        Debug.Assert(body + bodyLen == (byte*)packet + packetLen);
        Debug.Assert(packet->PeerId.Equals(_ctx.RemotePeerId));
        Debug.Assert(_ctx.LocalSessionId.Equals(packet->DSID));

        Pong* pong;
        byte* responderPubKey, encryptedResponderNonce, responderSignature;
        int responderPubKeyLen, encryptedResponderNonceLen, responderSignatureLen;
        if (!Wire.TryParsePong(body, bodyLen, out pong,
            out responderPubKey, out responderPubKeyLen,
            out encryptedResponderNonce, out encryptedResponderNonceLen,
            out responderSignature, out responderSignatureLen))
        {
            _metrics.HandshakesRejected++;
            _logger.LogError("failed to parse Pong message");
            return;
        }

        if (pong->AppProtocolId != _ctx.AppProtocolId || pong->AppProtocolVersion != _ctx.AppProtocolVersion)
        {
            _metrics.HandshakesRejected++;
            _logger.LogDebug($"unexpected app protocol in Pong: {pong->AppProtocolId}.{pong->AppProtocolVersion}");
            return;
        }

        if ((byte*)pong + pong->Length > (byte*)packet + packetLen)
        {
            _metrics.HandshakesRejected++;
            _logger.LogError("oversized Pong message");
            return;
        }

        // Verify the peer ID is actually derived from the public key.
        NetworkHelper.GetPeerId(
            new ReadOnlySpan<byte>(responderPubKey, responderPubKeyLen),
            out PeerId computedPeerId);

        if (!_ctx.RemotePeerId.Equals(computedPeerId))
        {
            _metrics.HandshakesRejected++;
            _logger.LogWarning($"invalid responder peer ID: {packet->PeerId} != {computedPeerId}");
            return;
        }

        IPeerVerifier peerVerifier = _ctx.Crypto.CreateVerifier(
            new ReadOnlySpan<byte>(responderPubKey, responderPubKeyLen));

        Debug.Assert(responderSignature > body + Pong.Size && responderSignature + responderSignatureLen == body + pong->Length);
        if (!peerVerifier.Verify(
            message: new ReadOnlySpan<byte>(body, (int)(responderSignature - body)),
            signature: new ReadOnlySpan<byte>(responderSignature, responderSignatureLen)))
        {
            _metrics.HandshakesRejected++;
            _logger.LogWarning("invalid responder Pong signature");
            goto CleanupVerifier;
        }

        // Remote peer encrypted its nonce using our public key. Decrypt
        // it so we can form the session key
        int responderNonceLen;
        if (!_ctx.LocalKeyPair.TryDecrypt(
            message: new ReadOnlySpan<byte>(
                encryptedResponderNonce, encryptedResponderNonceLen),
            destination: _ctx.RemoteNonce,
            bytesWritten: out responderNonceLen))
        {
            _metrics.HandshakesRejected++;
            _logger.LogError("failed to decrypt responder nonce");
            goto CleanupRemoteNonce;
        }
        if (responderNonceLen != Nonce.Size)
        {
            _metrics.HandshakesRejected++;
            _logger.LogWarning($"invalid responder nonce: expected {Nonce.Size} bytes, received {responderNonceLen} bytes");
            goto CleanupRemoteNonce;
        }

        // Derive the directional key material using the two nonces
        _ctx.Crypto.Kdf.Derive(
            ikm: _ctx.RemoteNonce,
            salt: _ctx.LocalNonce,
            info: NetworkConstants.SessionKeyInfo,
            okm: _ctx.SessionKey);

        // We sent the Ping, so we hold the initiator half of the key material.
        IPacketAuthenticator auth = _ctx.Crypto.CreateAuthenticator(_ctx.SessionKey, initiator: true);

        // Validate the packet's auth tag (the Pong is the responder's first
        // authenticated packet; its Seq is the nonce).
        if (!Wire.VerifyAuth((byte*)packet, packetLen, auth, packet->Seq, out _))
        {
            _metrics.HandshakesRejected++;
            _logger.LogError("pong has bad auth tag");
            goto CleanupAuth;
        }

        _ctx.RemoteVerifier = peerVerifier;
        _ctx.Auth = auth;
        _ctx.RemoteSessionId = packet->SSID;
        _ctx.Replay.TryAccept(packet->Seq); // seed the window with the responder's first authenticated packet

        // Success - session context was populated
        ClearHedge(); // don't hedge any more outbound Ping messages
        UpdateLastRecv();
        Transition(SessionState.Established);
        EmitSessionEstablished();
        _logger.LogInformation($"successfully established session {_ctx.LocalSessionId} with remote peer {packet->PeerId} ({source})");
        return;

        // Unwind pattern so that we don't need scratch buffers
        // Alternative is not mutating the session context until
        // fully validated - requires scratch space, pooling, or allocation
    CleanupAuth:
        auth.Dispose();
    CleanupSessionKey:
        _ctx.SessionKey.Clear();
    CleanupRemoteNonce:
        _ctx.RemoteNonce.Clear();
    CleanupVerifier:
        peerVerifier.Dispose();
    }

    private void HandleRemoteDisconnect(
        PacketHeader* packet, int packetLen,
        byte* body, int bodyLen, ref NetAddress source)
    {
        // paranoid redundant assertions
        Debug.Assert(packet->Type == PacketType.Disconnect);
        Debug.Assert((byte*)packet + PacketHeader.Size == body);
        Debug.Assert(body + bodyLen < (byte*)packet + packetLen);
        Debug.Assert(packet->PeerId.Equals(_ctx.RemotePeerId));
        Debug.Assert(_ctx.LocalSessionId.Equals(packet->DSID));

        if (bodyLen > Net.Disconnect.Size)
        {
            // This can happen if the peer sent multiple messages
            // or if the packet is genuinely malformed
            _logger.LogError($"oversized Disconnect packet; length: {bodyLen}");
            return;
        }

        UpdateLastRecv();

        var dc = (Disconnect*)body;
        Disconnect(dc->Reason, EndReason.RemoteClosed);

        // Send a symmetric DC so the remote can stop hedging early
        // A dedicated message might be cleaner here (instead of repurposing
        // Disconnect contextually)
        TrySendDisconnect(DisconnectReason.None, count: 1);
    }

    public void Disconnect(DisconnectReason reason, EndReason endReason)
    {
        SessionState initialState = _ctx.State;
        _logger.LogDebug($"(disconnect) reason:{reason}, endReason:{endReason}, state:{initialState}");

        if (initialState == SessionState.Closed)
        {
            // Closed is terminal state - nothing to do
            return;
        }

        bool send; // whether we should send packets (in Closing) or else immediately transition to Closed

        switch (endReason)
        {
            case EndReason.LocalClosed:
            case EndReason.Timeout:
                send = true;
                break;
            case EndReason.RemoteClosed:
            case EndReason.SimultaneousOpen:
                send = false;
                break;
            default:
                _logger.LogWarning($"(disconnect) unrecognized end reason: {endReason} (reason: {reason})");
                // Just send a single unhedged disconnect
                TrySendDisconnect(reason, 1);
                send = false;
                break;
        }

        if (send)
        {
            if (initialState == SessionState.Closing)
            {
                _logger.LogWarning($"(disconnect) already hedging disconnect ({DisconnectReason}) - skipping {reason}/{endReason}");
                return;
            }

            // If we initiate disconnect, inform the remote peer (best-effort)
            TrySendDisconnect(reason);
            DisconnectReason = reason; // for hedge resend
            ArmHedge();
            Transition(SessionState.Closing);
            EmitSessionClosed(reason, endReason);
        }
        else
        {
            if (initialState == SessionState.Closing)
            {
                ClearHedge();
                Transition(SessionState.Closed);
            }
            else
            {
                Transition(SessionState.Closed);
                EmitSessionClosed(reason, endReason);
            }
        }
    }

    public void ActiveOpen(ref NetAddress remote)
    {
        _ctx.RemoteEndPoint = remote;
        TrySendPing();
        ArmHedge();
        Transition(SessionState.PingSent);
        EmitSessionAccepted();
    }

    public void PassiveOpen(ref NetAddress remote)
    {
        _ctx.RemoteEndPoint = remote;
        TrySendPong();
        ArmHedge();
        Transition(SessionState.Established);
        EmitSessionAccepted();
        EmitSessionEstablished();
    }

    // Stamp header and auth, then send over transport
    public bool TrySendApplicationData(byte* slot, int slotSize)
    {
        Debug.Assert(_ctx.State == SessionState.Established);

        IPacketAuthenticator? auth = _ctx.Auth;
        if (auth is null)
        {
            _logger.LogError("failed to send outbound ApplicationData: missing authenticator");
            return false;
        }

        var metadata = (NetworkEventMetadata*)slot;

        Debug.Assert(metadata->Kind == NetworkEventKind.ApplicationData);
        Debug.Assert(metadata->SessionId.Equals(_ctx.LocalSessionId));

        if (metadata->Offset < PacketHeader.Size)
        {
            _logger.LogError("insufficient offset for outbound ApplicationData packet header");
            return false;
        }

        if (metadata->Length > NetworkConstants.MaxApplicationDataLength)
        {
            _logger.LogError("outbound application data too large");
            return false;
        }

        byte* slotEnd = slot + slotSize;
        byte* payload = slot + NetworkConstants.NetworkEventHeadroom + metadata->Offset;
        byte* payloadEnd = payload + metadata->Length;

        if (payloadEnd > slotEnd)
        {
            _logger.LogError("outbound ApplicationData payload too large");
            return false;
        }

        byte* packet = payload - PacketHeader.Size;
        Debug.Assert(packet >= slot + NetworkConstants.NetworkEventHeadroom);

        // Stamp header
        var header = (PacketHeader*)packet;
        Wire.Initialize(header, _ctx, PacketType.ApplicationData);

        // Stamp auth
        int packetLengthBeforeAuth = PacketHeader.Size + metadata->Length;
        int authBytesWritten;
        if (!Wire.TryWriteAuth(
            buffer: packet,
            length: packetLengthBeforeAuth,
            capacity: (int)(slotEnd - packet),
            auth: auth,
            seq: header->Seq,
            out authBytesWritten))
        {
            _logger.LogError("failed to stamp auth tag on outbound ApplicationData packet");
            return false;
        }
        Debug.Assert(payloadEnd + authBytesWritten <= slotEnd);

        return TrySend(packet, packetLengthBeforeAuth + authBytesWritten, ref _ctx.RemoteEndPoint, 1);
    }

    private void ArmHedge()
    {
        HedgeSendsRemaining = HedgeSendCount;
        HedgeBeginTicks = Stopwatch.GetTimestamp();
        HedgeNextSendTicks = HedgeBeginTicks + HedgeIntervalTicks;
    }

    private void ClearHedge()
    {
        HedgeSendsRemaining = 0;
        HedgeBeginTicks = 0;
        HedgeNextSendTicks = 0;
    }

    // Only call when not dropped
    private void UpdateLastRecv()
    {
        _ctx.LastRecvTimestamp = Stopwatch.GetTimestamp();
    }

    private void UpdateLastSend()
    {
        _ctx.LastSendTimestamp = Stopwatch.GetTimestamp();
    }

    // One place to hang entry/exit actions, logging, invariant checks,
    // and assertions about legal transitions.
    private void Transition(SessionState newState)
    {
        _ctx.State = newState;
    }

    private bool TrySendKeepAlive()
    {
        // Header
        var header = (PacketHeader*)_txBuffer;
        Wire.Initialize(header, _ctx, PacketType.KeepAlive);

        /* empty payload */

        IPacketAuthenticator? auth = _ctx.Auth;
        if (auth is null)
        {
            _logger.LogError($"failed to send KeepAlive packet - authenticator unavailable");
            return false;
        }

        int contentLength = PacketHeader.Size; // + 0

        if (!Wire.TryWriteAuth(
            _txBuffer,
            contentLength,
            _txBufferLength,
            auth,
            header->Seq,
            out int authBytesWritten))
        {
            _logger.LogError("failed to seal outbound KeepAlive packet");
            return false;
        }

        int packetLength = contentLength + authBytesWritten;

        if (!TrySend(_txBuffer, packetLength, ref _ctx.RemoteEndPoint, 1))
        {
            _logger.LogError($"failed to send Disconnect (L:{packetLength}) to {_ctx.RemotePeerId}:{_ctx.RemoteSessionId}");
            return false;
        }

        _logger.LogDebug($"{_ctx.LocalSessionId} sent KeepAlive (L:{packetLength}) to {_ctx.RemotePeerId}:{_ctx.RemoteSessionId}");
        return true;
    }

    private bool TrySendDisconnect(DisconnectReason reason, int count = 1)
    {
        // Header
        var header = (PacketHeader*)_txBuffer;
        Wire.Initialize(header, _ctx, PacketType.Disconnect);

        // Disconnect message
        var disconnect = (Disconnect*)(_txBuffer + PacketHeader.Size);
        *disconnect = default; // zero it
        disconnect->Reason = reason;

        IPacketAuthenticator? auth = _ctx.Auth;
        if (auth is null)
        {
            _logger.LogError($"failed to send Disconnect message - authenticator unavailable");
            return false;
        }

        int contentLength = PacketHeader.Size + Net.Disconnect.Size;

        if (!Wire.TryWriteAuth(
            _txBuffer,
            contentLength,
            _txBufferLength,
            auth,
            header->Seq,
            out int macBytesWritten))
        {
            _logger.LogError("failed to seal outbound Disconnect packet");
            return false;
        }

        int packetLength = contentLength + macBytesWritten;

        if (!TrySend(_txBuffer, packetLength, ref _ctx.RemoteEndPoint, count))
        {
            _logger.LogError($"failed to send Disconnect (L:{packetLength}) to {_ctx.RemotePeerId}:{_ctx.RemoteSessionId}");
            return false;
        }

        _logger.LogInformation($"{_ctx.LocalSessionId} sent Disconnect (L:{packetLength}) to {_ctx.RemotePeerId}:{_ctx.RemoteSessionId}");
        return true;
    }

    private bool TrySendPing()
    {
        int packetLen = Wire.WritePing(_txBuffer, _txBufferLength, _ctx);

        if (!TrySend(_txBuffer, packetLen, ref _ctx.RemoteEndPoint, 1))
        {
            _logger.LogError($"failed to send Ping (length {packetLen}) to {_ctx.RemotePeerId}:{_ctx.RemoteSessionId}");
            return false;
        }

        _logger.LogDebug($"{_ctx.LocalSessionId} sent Ping (length {packetLen}) to {_ctx.RemotePeerId}:{_ctx.RemoteSessionId}");
        return true;
    }

    private bool TrySendPong()
    {
        // Write the Pong packet into the TX buffer
        int packetLen = Wire.WritePong(_txBuffer, _txBufferLength, _ctx);

        if (!TrySend(_txBuffer, packetLen, ref _ctx.RemoteEndPoint, 1))
        {
            _logger.LogError($"failed to send Pong (L:{packetLen}) to {_ctx.RemotePeerId}");
            return false;
        }

        _logger.LogDebug($"{_ctx.LocalSessionId} sent Pong (L:{packetLen}) to {_ctx.RemotePeerId}:{_ctx.RemoteSessionId}");
        return true;
    }

    private bool TrySend(byte* buffer, int length, ref NetAddress remote, int count)
    {
        bool flag = true;

        for (int i = 0; i < count; i++)
            flag &= _transport.TrySend(buffer, length, ref remote);

        if (flag)
        {
            _metrics.TxPackets += count;
            _metrics.TxBytes += (long)length * count;
            UpdateLastSend();
        }
        else
        {
            _metrics.TxSendFailed++;
        }

        return flag;
    }

    private void EmitSessionAccepted()
    {
        FlushPendingLifecycle();
        if (_lifecycle.SessionAccepted.Emitted)
            return;
        if (_lifecycle.SessionAccepted.Pending)
            return;

        Debug.Assert(!_lifecycle.SessionEstablished.Pending);
        Debug.Assert(!_lifecycle.SessionClosed.Pending);
        Debug.Assert(!_lifecycle.SessionEstablished.Emitted);
        Debug.Assert(!_lifecycle.SessionClosed.Emitted);

        ref NetworkEventMetadata metadata = ref _lifecycle.SessionAccepted.Metadata;
        ref SessionAccepted payload = ref _lifecycle.SessionAccepted.Payload;

        bool rented = _rx.TryRent(ref _buffer);
        if (rented)
        {
            byte* buffer = _rx.Deref(_buffer);
            metadata = ref *(NetworkEventMetadata*)buffer;
            payload = ref *(SessionAccepted*)(buffer + NetworkConstants.NetworkEventHeadroom);
        }

        metadata.SessionId = _ctx.LocalSessionId;
        metadata.Kind = NetworkEventKind.SessionAccepted;
        metadata.Offset = 0;
        metadata.Length = SessionAccepted.Size;
        payload.LocalPeerId = _ctx.LocalPeerId;
        payload.RemotePeerId = _ctx.RemotePeerId;
        payload.LocalSessionId = _ctx.LocalSessionId;
        payload.RemoteSessionId = _ctx.RemoteSessionId;

        if (!rented)
        {
            _lifecycle.SessionAccepted.Pending = true;
            _lifecycle.Pending++;
            _metrics.LifecycleEventsQueued++;
            return;
        }

        if (!_rx.TryEnqueue(_buffer))
        {
            _rx.Abandon(_buffer);
            _logger.LogError("failed to emit SessionAccepted: RX ring full");

            _lifecycle.SessionAccepted.Metadata = metadata;
            _lifecycle.SessionAccepted.Payload = payload;
            _lifecycle.SessionAccepted.Pending = true;
            _lifecycle.Pending++;
            _metrics.LifecycleEventsQueued++;
            return;
        }

        _lifecycle.SessionAccepted.Emitted = true;
        _metrics.SessionsAccepted++;
        _logger.LogTrace($"enqueue: SessionAccepted (handle: {_buffer}). LocalPeerId:{payload.LocalPeerId}, LocalSessionId:{payload.LocalSessionId}, RemotePeerId:{payload.RemotePeerId}, RemoteSessionId:{payload.RemoteSessionId}");
    }

    private void EmitSessionEstablished()
    {
        FlushPendingLifecycle();
        if (_lifecycle.SessionEstablished.Emitted)
            return;
        if (_lifecycle.SessionEstablished.Pending)
            return;
        bool previousPending = _lifecycle.SessionAccepted.Pending;
        ref NetworkEventMetadata metadata = ref _lifecycle.SessionEstablished.Metadata;
        bool rented = !previousPending && _rx.TryRent(ref _buffer);
        if (rented)
        {
            metadata = ref *(NetworkEventMetadata*)_rx.Deref(_buffer);
        }
        metadata.SessionId = _ctx.LocalSessionId;
        metadata.Kind = NetworkEventKind.SessionEstablished;
        metadata.Offset = 0;
        metadata.Length = 0;
        /* (empty payload) */
        if (!rented)
        {
            _lifecycle.SessionEstablished.Pending = true;
            _lifecycle.Pending++;
            _metrics.LifecycleEventsQueued++;
            return;
        }
        if (!_rx.TryEnqueue(_buffer))
        {
            _rx.Abandon(_buffer);
            _logger.LogError("failed to emit SessionEstablished: RX ring full");
            _lifecycle.SessionEstablished.Metadata = metadata;
            _lifecycle.SessionEstablished.Pending = true;
            _lifecycle.Pending++;
            _metrics.LifecycleEventsQueued++;
            return;
        }
        _lifecycle.SessionEstablished.Emitted = true;
        _metrics.SessionsEstablished++;
        _logger.LogTrace($"enqueue SessionEstablished (handle: {_buffer}).");
    }

    private void EmitSessionClosed(DisconnectReason peerReason, EndReason endReason)
    {
        FlushPendingLifecycle();
        if (_lifecycle.SessionClosed.Emitted)
            return;
        if (_lifecycle.SessionClosed.Pending)
            return;
        bool previousPending = _lifecycle.SessionAccepted.Pending || _lifecycle.SessionEstablished.Pending;
        ref NetworkEventMetadata metadata = ref _lifecycle.SessionClosed.Metadata;
        ref SessionClosed payload = ref _lifecycle.SessionClosed.Payload;
        bool rented = !previousPending && _rx.TryRent(ref _buffer);
        if (rented)
        {
            byte* buffer = _rx.Deref(_buffer);
            metadata = ref *(NetworkEventMetadata*)buffer;
            payload = ref *(SessionClosed*)(buffer + NetworkConstants.NetworkEventHeadroom);
        }
        metadata.SessionId = _ctx.LocalSessionId;
        metadata.Kind = NetworkEventKind.SessionClosed;
        metadata.Offset = 0;
        metadata.Length = SessionClosed.Size;
        payload.PeerReason = peerReason;
        payload.EndReason = endReason;
        if (!rented)
        {
            _lifecycle.SessionClosed.Pending = true;
            _lifecycle.Pending++;
            _metrics.LifecycleEventsQueued++;
            return;
        }
        if (!_rx.TryEnqueue(_buffer))
        {
            _rx.Abandon(_buffer);
            _logger.LogError("failed to emit SessionClosed: RX ring full");
            _lifecycle.SessionClosed.Metadata = metadata;
            _lifecycle.SessionClosed.Payload = payload;
            _lifecycle.SessionClosed.Pending = true;
            _lifecycle.Pending++;
            _metrics.LifecycleEventsQueued++;
            return;
        }
        _lifecycle.SessionClosed.Emitted = true;
        _metrics.SessionsClosed++;
        _logger.LogTrace($"enqueue: SessionClosed (handle: {_buffer}). PeerReason:{payload.PeerReason}, EndReason:{payload.EndReason}");
    }

    private void FlushPendingSessionAccepted()
    {
        Debug.Assert(_lifecycle.SessionAccepted.Pending);
        Debug.Assert(!_lifecycle.SessionAccepted.Emitted);

        if (!_rx.TryRent(ref _buffer)) return;

        byte* slot = _rx.Deref(_buffer);
        *(NetworkEventMetadata*)slot = _lifecycle.SessionAccepted.Metadata;
        *(SessionAccepted*)(slot + NetworkConstants.NetworkEventHeadroom) = _lifecycle.SessionAccepted.Payload;

        if (_rx.TryEnqueue(_buffer))
        {
            _lifecycle.SessionAccepted.Pending = false;
            _lifecycle.SessionAccepted.Emitted = true;
            _lifecycle.Pending--;
            _metrics.SessionsAccepted++;
        }
        else
        {
            _rx.Abandon(_buffer);
            return; // RX ring full
        }
        return;
    }

    private void FlushPendingSessionEstablished()
    {
        Debug.Assert(_lifecycle.SessionEstablished.Pending);
        Debug.Assert(!_lifecycle.SessionEstablished.Emitted);
        Debug.Assert(_lifecycle.SessionAccepted.Emitted);

        if (!_rx.TryRent(ref _buffer)) return;

        *(NetworkEventMetadata*)_rx.Deref(_buffer) = _lifecycle.SessionEstablished.Metadata;

        if (_rx.TryEnqueue(_buffer))
        {
            _lifecycle.SessionEstablished.Pending = false;
            _lifecycle.SessionEstablished.Emitted = true;
            _lifecycle.Pending--;
            _metrics.SessionsEstablished++;
        }
        else
        {
            _rx.Abandon(_buffer);
            return; // RX ring full
        }
        return;
    }

    private void FlushPendingSessionClosed()
    {
        Debug.Assert(_lifecycle.SessionClosed.Pending);
        Debug.Assert(!_lifecycle.SessionClosed.Emitted);
        Debug.Assert(_lifecycle.SessionAccepted.Emitted);

        if (!_rx.TryRent(ref _buffer)) return;

        byte* slot = _rx.Deref(_buffer);
        *(NetworkEventMetadata*)slot = _lifecycle.SessionClosed.Metadata;
        *(SessionClosed*)(slot + NetworkConstants.NetworkEventHeadroom) = _lifecycle.SessionClosed.Payload;

        if (_rx.TryEnqueue(_buffer))
        {
            _lifecycle.SessionClosed.Pending = false;
            _lifecycle.SessionClosed.Emitted = true;
            _lifecycle.Pending--;
            _metrics.SessionsClosed++;
            return;
        }
        else
        {
            _rx.Abandon(_buffer);
            return; // RX ring full
        }
    }

    private void FlushPendingLifecycle()
    {
        if (_lifecycle.SessionAccepted.Pending && !_lifecycle.SessionAccepted.Emitted)
        {
            FlushPendingSessionAccepted();
            if (!_lifecycle.SessionAccepted.Emitted)
                return;
        }
        if (_lifecycle.SessionEstablished.Pending && !_lifecycle.SessionEstablished.Emitted)
        {
            FlushPendingSessionEstablished();
            if (!_lifecycle.SessionEstablished.Emitted)
                return;
        }
        if (_lifecycle.SessionClosed.Pending && !_lifecycle.SessionClosed.Emitted)
        {
            FlushPendingSessionClosed();
            if (!_lifecycle.SessionClosed.Emitted)
                return;
        }
    }

    private struct LifecycleEvents
    {
        internal struct SessionAcceptedBlob
        {
            public bool Emitted;
            public bool Pending;
            public NetworkEventMetadata Metadata;
            public SessionAccepted Payload;
        }
        internal struct SessionEstablishedBlob
        {
            public bool Emitted;
            public bool Pending;
            public NetworkEventMetadata Metadata;
        }
        internal struct SessionClosedBlob
        {
            public bool Emitted;
            public bool Pending;
            public NetworkEventMetadata Metadata;
            public SessionClosed Payload;
        }
        public int Pending;
        public SessionAcceptedBlob SessionAccepted;
        public SessionEstablishedBlob SessionEstablished;
        public SessionClosedBlob SessionClosed;
        public void Reset()
        {
            Pending = 0;
            SessionAccepted.Emitted = false;
            SessionAccepted.Pending = false;
            SessionEstablished.Emitted = false;
            SessionEstablished.Pending = false;
            SessionClosed.Emitted = false;
            SessionClosed.Pending = false;
        }
    }
}
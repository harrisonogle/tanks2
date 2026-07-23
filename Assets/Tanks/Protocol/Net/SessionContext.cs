using System.Diagnostics;
using System;

namespace Tanks.Net;

// Net thread's TCB. All handshake state lives here.
// Sole writer: the net thread. State field is the only cross-thread read.
// TODO: Make this resettable for pooling
public sealed class SessionContext : IDisposable
{
    private readonly IPeerKeyPair _localKeyPair;
    private readonly IPeerCrypto _crypto;
    private readonly byte[] _buffer; // single managed byte allocation per session context
    private readonly Memory<byte> _localNonce;
    private readonly Memory<byte> _remoteNonce;
    private readonly Memory<byte> _sessionKey;

    internal SessionContext(
        PeerId localPeerId,
        PeerId remotePeerId,
        in NetAddress remoteEndPoint,
        SessionId localSessionId,
        ushort appProtocolId,
        ushort appProtocolVersion,
        IPeerKeyPair localKey,
        IPeerCrypto crypto)
    {
        LocalPeerId = localPeerId;
        RemotePeerId = remotePeerId;
        RemoteEndPoint = remoteEndPoint;
        LocalSessionId = localSessionId;
        AppProtocolId = appProtocolId;
        AppProtocolVersion = appProtocolVersion;
        _localKeyPair = localKey;
        _crypto = crypto;

        int sessionKeySize = crypto.SessionKeySize;
        _buffer = new byte[Nonce.Size + Nonce.Size + sessionKeySize];
        _localNonce = new Memory<byte>(_buffer, 0, Nonce.Size);
        _remoteNonce = new Memory<byte>(_buffer, Nonce.Size, Nonce.Size);
        _sessionKey = new Memory<byte>(_buffer, 2 * Nonce.Size, sessionKeySize);

        LastRecvTimestamp = Stopwatch.GetTimestamp();
    }

    public readonly PeerId LocalPeerId;
    public readonly PeerId RemotePeerId;
    public readonly SessionId LocalSessionId;
    public readonly ushort AppProtocolId;
    public readonly ushort AppProtocolVersion;
    public SessionId RemoteSessionId;

    public SessionState State;

    public IPeerKeyPair LocalKeyPair => _localKeyPair;
    public IPeerCrypto Crypto => _crypto;
    public Span<byte> LocalNonce => _localNonce.Span;
    public Span<byte> RemoteNonce => _remoteNonce.Span;
    public Span<byte> SessionKey => _sessionKey.Span;
    public long LastRecvTimestamp;
    public long LastSendTimestamp;
    public IPeerVerifier? RemoteVerifier;
    public IPacketAuthenticator? Auth;
    public ulong SendSeq; // next outbound sequence number (this direction's nonce space)
    public ReplayWindow Replay; // inbound seq filter; only touched by authenticated packets
    public NetAddress RemoteEndPoint;

    public void Dispose()
    {
        using (RemoteVerifier)
        using (Auth)
        {
            RemoteVerifier = null;
            Auth = null;
        }
    }
}
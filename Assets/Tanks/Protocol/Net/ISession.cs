namespace Tanks.Net;

// Game thread view. Handed out via TryConnect/TryAccept. Read-only surface.
public interface ISession
{
    ref readonly PeerId LocalPeerId { get; }
    ref readonly PeerId RemotePeerId { get; }
    ref readonly SessionId LocalSessionId { get; }
    ref readonly SessionId RemoteSessionId { get; }
    SessionState State { get; }
    ref readonly DisconnectReason PeerReason { get; }
    ref readonly EndReason EndReason { get; }

    void OnSessionAccepted(ref SessionAccepted evt);
    void OnSessionEstablished();
    void OnSessionClosed(ref SessionClosed evt);
}
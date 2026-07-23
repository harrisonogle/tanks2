// A local-only value describing how the session ended from your side's perspective
// Used to tell the game thread the reason for session end
public enum EndReason : byte
{
    LocalClosed = 1, // game thread issued Disconnect
    RemoteClosed = 2, // peer sent Disconnect
    Timeout = 3, // silence timeout
    SimultaneousOpen = 4, // remote peer won the tiebreaker
    // maybe more later? ProtocolError, PeerRejected, etc.
}
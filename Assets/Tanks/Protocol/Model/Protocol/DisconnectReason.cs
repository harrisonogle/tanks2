namespace Tanks.Net;

public enum DisconnectReason : int
{
    None = 0,
    Timeout = 1,
    ProtocolError = 2,
    ActiveClose = 3,
    // ...
}
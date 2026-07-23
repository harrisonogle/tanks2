namespace Tanks.Net;

public enum SessionState : byte
{
    Init = 0,
    PingSent = 1,
    Established = 2,
    Closing = 3, // sent/received Disconnect, hedge draining
    Closed = 4, // terminal - ready to reap
}
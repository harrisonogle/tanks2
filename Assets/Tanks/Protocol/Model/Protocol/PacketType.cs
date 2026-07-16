namespace Tanks.Net;

public enum PacketType : byte
{
    None = 0,
    Ping = 1,
    Pong = 2,
    Disconnect = 3,
    KeepAlive = 4,
    ApplicationData = 5,
}
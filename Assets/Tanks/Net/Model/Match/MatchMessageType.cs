namespace Tanks.Net;

public enum MatchMessageType : byte
{
    None = 0,
    Input = 1,
    StateHash = 2,
    Advantage = 3,
}
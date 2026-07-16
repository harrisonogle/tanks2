namespace Tanks;

public enum LogLevel : byte
{
    Trace = 0,
    Debug = 1,
    Information = 2,
    Warning = 3,
    Error = 4,
    None = 5, // disables everything when used as a minimum level
}

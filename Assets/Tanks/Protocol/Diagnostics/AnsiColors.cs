namespace Tanks;

public static class AnsiColors
{
    // Reset
    public const string Reset = "\x1b[0m";

    // Text Styles
    public const string Bold = "\x1b[1m";
    public const string Underline = "\x1b[4m";
    public const string Reversed = "\x1b[7m";

    // Standard Foreground Colors
    public const string Black = "\x1b[30m";
    public const string Red = "\x1b[31m";
    public const string Green = "\x1b[32m";
    public const string Yellow = "\x1b[33m";
    public const string Blue = "\x1b[34m";
    public const string Magenta = "\x1b[35m";
    public const string Cyan = "\x1b[36m";
    public const string White = "\x1b[37m"; // Standard White is often light gray
    public const string Gray = "\x1b[90m"; // Also known as Bright Black

    // Bright Foreground Colors
    public const string LtBlack = "\x1b[90m";
    public const string LtRed = "\x1b[91m";
    public const string LtGreen = "\x1b[92m";
    public const string LtYellow = "\x1b[93m";
    public const string LtBlue = "\x1b[94m";
    public const string LtMagenta = "\x1b[95m";
    public const string LtCyan = "\x1b[96m";
    public const string LtWhite = "\x1b[97m";
    public const string LtGray = "\x1b[37m"; // Standard White is often light gray

    // Background Colors
    public const string BgBlack = "\x1b[40m";
    public const string BgRed = "\x1b[41m";
    public const string BgGreen = "\x1b[42m";
    public const string BgYellow = "\x1b[43m";
    public const string BgBlue = "\x1b[44m";
    public const string BgMagenta = "\x1b[45m";
    public const string BgCyan = "\x1b[46m";
    public const string BgWhite = "\x1b[47m";
    public const string BgGray = "\x1b[100m"; // technically "bright black"
}
using System.Runtime.InteropServices;

namespace Tanks.Net;

// The other ones not exposed by OSPlatform itself.
// NOTE: Use System.OperatingSystem in .NET 5+
public static class OSPlatforms
{
    public static readonly OSPlatform Android = OSPlatform.Create("ANDROID");
    public static readonly OSPlatform Browser = OSPlatform.Create("BROWSER");
    public static readonly OSPlatform FreeBSD = OSPlatform.Create("FREEBSD");
    public static readonly OSPlatform iOS = OSPlatform.Create("IOS");
    public static readonly OSPlatform MacCatalyst = OSPlatform.Create("MACCATALYST");
    public static readonly OSPlatform tvOS = OSPlatform.Create("TVOS"); // Apple TV
    public static readonly OSPlatform Wasi = OSPlatform.Create("WASI");
    public static readonly OSPlatform watchOS = OSPlatform.Create("WATCHOS"); // Apple watch

    // All BSD-derived platforms put sa_len at byte 0 and family at byte 1
    // in sockaddr_*. Linux/Android/Windows put family as u16 at byte 0.
    internal static readonly bool IsBsdLayout =
        RuntimeInformation.IsOSPlatform(OSPlatform.OSX) ||
        RuntimeInformation.IsOSPlatform(OSPlatforms.iOS) ||
        RuntimeInformation.IsOSPlatform(OSPlatforms.tvOS) ||
        RuntimeInformation.IsOSPlatform(OSPlatforms.watchOS) ||
        RuntimeInformation.IsOSPlatform(OSPlatforms.MacCatalyst) ||
        RuntimeInformation.IsOSPlatform(OSPlatforms.FreeBSD);

    // Cache it for efficiency.
    internal static readonly bool IsWindows = RuntimeInformation.IsOSPlatform(OSPlatform.Windows);
}
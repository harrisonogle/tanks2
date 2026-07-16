using System.Runtime.InteropServices;

namespace Tanks.Net;

[StructLayout(LayoutKind.Explicit, Size = Size)]
public struct SessionClosed
{
    public const int Size = 8;
    [FieldOffset(0)] public EndReason EndReason;
    [FieldOffset(4)] public DisconnectReason PeerReason;
}
using System.Runtime.InteropServices;

namespace Tanks.Net;

[StructLayout(LayoutKind.Explicit, Size = Size)]
public unsafe struct Disconnect
{
    public const int Size = 4;
    [FieldOffset(0)] public DisconnectReason Reason;
}
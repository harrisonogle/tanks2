using System.Runtime.InteropServices;

namespace Tanks.Net;

[StructLayout(LayoutKind.Explicit, Size = Size)]
public struct Advantage
{
    public const int Size = 8;
    [FieldOffset(0)] public uint CurrentTick;
    [FieldOffset(4)] public uint LastTickRecvd;
}
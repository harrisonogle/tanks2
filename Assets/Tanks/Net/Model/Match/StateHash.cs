using System.Runtime.InteropServices;

namespace Tanks.Net;

[StructLayout(LayoutKind.Explicit, Size = Size)]
public struct StateHash
{
    public const int Size = 12;
    [FieldOffset(0)] public uint Tick;
    [FieldOffset(4)] public ulong Hash;
}
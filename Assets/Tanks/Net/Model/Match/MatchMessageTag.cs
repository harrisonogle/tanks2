using System.Runtime.InteropServices;

namespace Tanks.Net;

[StructLayout(LayoutKind.Explicit, Size = Size)]
public struct MatchMessageTag
{
    public const int Size = 4;
    [FieldOffset(0)] public MatchMessageType Type;
}
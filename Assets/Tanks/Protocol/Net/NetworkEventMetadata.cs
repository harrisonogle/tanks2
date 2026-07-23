using System.Runtime.InteropServices;

namespace Tanks.Net;

[StructLayout(LayoutKind.Explicit, Size = Size)]
public struct NetworkEventMetadata
{
    internal const int Size = 12;
    [FieldOffset(0)] public SessionId SessionId;
    [FieldOffset(8)] public NetworkEventKind Kind;
    [FieldOffset(9)] public byte Offset; // does not include fixed headroom (NetworkConstants.PipeEventHeadroom)
    [FieldOffset(10)] public ushort Length;
}
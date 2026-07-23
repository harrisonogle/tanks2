using System.Runtime.InteropServices;

namespace Tanks.Engine;

[StructLayout(LayoutKind.Explicit, Size = Size)]
public struct EngineEventMetadata
{
    internal const int Size = 4;
    [FieldOffset(0)] public EngineEventKind Kind;
    [FieldOffset(1)] public byte Offset; // does not include fixed headroom (sizeof(EngineEventMetadata))
    [FieldOffset(2)] public ushort Length;
}
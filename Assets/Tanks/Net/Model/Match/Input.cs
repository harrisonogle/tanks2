using System;
using System.Runtime.InteropServices;

namespace Tanks.Net;

[StructLayout(LayoutKind.Explicit, Size = Size)]
public unsafe struct Input
{
    public const int Size = 36;
    public const int Count = 8;
    [FieldOffset(0)] public uint InputTick;
    [FieldOffset(4)] public fixed byte Inputs[Count * PeerInput.Size];

    public Span<PeerInput> Span
    {
        get
        {
            fixed (byte* p = Inputs)
            {
                return new Span<PeerInput>((PeerInput*)p, Count);
            }
        }
    }
}
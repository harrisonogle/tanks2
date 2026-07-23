using System;
using System.Runtime.InteropServices;
using Tanks.Sim;

namespace Tanks.Net;

[StructLayout(LayoutKind.Explicit, Size = Size)]
public unsafe struct Input
{
    public const int Size = 36;
    public const int Count = 8;
    [FieldOffset(0)] public uint InputTick; // the newest tick whose input is in this window, 0 = no ticks executed yet
    [FieldOffset(4)] public fixed byte Inputs[Count * PlayerInput.Size];

    public Span<PlayerInput> Span
    {
        get
        {
            fixed (byte* p = Inputs)
            {
                return new Span<PlayerInput>((PlayerInput*)p, Count);
            }
        }
    }
}
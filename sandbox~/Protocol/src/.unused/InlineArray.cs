using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.InteropServices;

// Use MemoryMarshal.Read<BytesN>(buffer) for a copy
// Use MemoryMarshal.AsRef<BytesN>(buffer) for a ref (zero-copy)

[StructLayout(LayoutKind.Explicit, Size = Size)]
public struct Bytes16
{
    public const int Size = 16;

    [UnscopedRef]
    public Span<byte> AsSpan() =>
        MemoryMarshal.AsBytes(MemoryMarshal.CreateSpan(ref this, 1));

    public unsafe ref byte this[int index]
    {
        get
        {
            Debug.Assert((uint)index < (uint)Size);
            fixed (Bytes16* self = &this)
                return ref *((byte*)self + index);
        }
    }
}

[StructLayout(LayoutKind.Explicit, Size = Size)]
public struct Bytes32
{
    public const int Size = 32;

    [UnscopedRef]
    public Span<byte> AsSpan() =>
        MemoryMarshal.AsBytes(MemoryMarshal.CreateSpan(ref this, 1));

    public unsafe ref byte this[int index]
    {
        get
        {
            Debug.Assert((uint)index < (uint)Size);
            fixed (Bytes32* self = &this)
                return ref *((byte*)self + index);
        }
    }
}

[StructLayout(LayoutKind.Explicit, Size = Size)]
public struct Bytes64
{
    public const int Size = 64;

    [UnscopedRef]
    public Span<byte> AsSpan() =>
        MemoryMarshal.AsBytes(MemoryMarshal.CreateSpan(ref this, 1));

    public unsafe ref byte this[int index]
    {
        get
        {
            Debug.Assert((uint)index < (uint)Size);
            fixed (Bytes64* self = &this)
                return ref *((byte*)self + index);
        }
    }
}

[StructLayout(LayoutKind.Explicit, Size = Size)]
public struct Bytes128
{
    public const int Size = 128;

    [UnscopedRef]
    public Span<byte> AsSpan() =>
        MemoryMarshal.AsBytes(MemoryMarshal.CreateSpan(ref this, 1));

    public unsafe ref byte this[int index]
    {
        get
        {
            Debug.Assert((uint)index < (uint)Size);
            fixed (Bytes128* self = &this)
                return ref *((byte*)self + index);
        }
    }
}
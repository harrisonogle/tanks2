#if false
using System.Diagnostics.CodeAnalysis;
using System.Runtime.InteropServices;

namespace Tanks.Net;

// Block of unmanaged memory. Exists for lifecycle.
// Does not validate or synchronize - left to the user (they're optional in some cases).
public sealed unsafe class Arena : IDisposable
{
    [StructLayout(LayoutKind.Explicit, Size = 8)]
    private struct Handle
    {
        public const uint MaxOffset = (1 << 56) - 1;
        private const int GenShift = 24;
        private const uint GenMask = 0xFF000000;
        private const uint OffsetMask = ~GenMask;
        public Handle(uint offset, uint gen)
        {
            _value = (offset & OffsetMask) | ((gen & GenMask) << GenShift);
        }
        [FieldOffset(0)] private uint _value;
        public int Offset => (int)(_value & OffsetMask);
        public int Gen => (int)(_value & GenMask >> GenShift);
    }

    private byte* _start;
    private byte* _end; // first address directly after the block
    private byte* _cursor;

    private ushort _gen;

    public Arena(int capacity)
    {
        ThrowHelper.ThrowIfNegativeOrZero(capacity);
        // ThrowHelper.ThrowIfGreaterThan(capacity, Handle.MaxOffset);
        _start = (byte*)Marshal.AllocHGlobal(capacity);
        _end = _start + capacity;
    }

    public bool TryAlloc(int length, out BufferHandle handle)
    {
        byte* nxt = _cursor + length;
        //nxt = (byte*)((((long)nxt) + 63) & ~63); // round up to multiple of 64
        if (nxt >= _end)
        {
            handle = default;
            return false;
        }
        var h = new Handle((uint)_cursor, _gen);
        handle = new BufferHandle(0, nxt, _gen);
        _cursor = nxt;
        return true;
    }

    public byte* Deref(ArenaHandle handle)
    {
        var h = (Handle*)&handle;

        if (h->Gen != _gen)
        {
            ThrowInvalidHandle();
        }

        byte* p = h->Offset + _start;

        if (p >= _end)
        {
            ThrowInvalidHandle();
        }

        return p;
    }

    public void Reset()
    {
        _cursor = _start;
        _gen++;
    }

    public void Dispose()
    {
        if (_start != null)
        {
            Marshal.FreeHGlobal((IntPtr)_start);
        }
    }

    [DoesNotReturn]
    private static void ThrowInvalidHandle() => throw new InvalidOperationException("Invalid handle.");
}
#endif
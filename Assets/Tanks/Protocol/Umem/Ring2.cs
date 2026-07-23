using System.Runtime.InteropServices;
using System;
using System.Threading;

namespace Tanks.Net;

// SPSC ring buffer over unmanaged memory
// WARNING: value type, mind the copy semantics - designed for use as private member
public unsafe struct Ring2
{
    // State written to by both producer and consumer threads, padded so each gets its own cache line
    // Needs to be defined externally because generic types can't define private types with explicit layout
    [StructLayout(LayoutKind.Explicit, Size = 64 + 8 + 64 + 8)]
    private struct Ring2State
    {
        [FieldOffset(64)] public long Producer;
        [FieldOffset(64 + 8 + 64)] public long Consumer;
    }

    private BufferHandle* _slots;
    private int _count;
    private int _mask;
    private Ring2State _shared;

    public Ring2(BufferHandle* slots, int count)
    {
        ThrowHelper.ThrowIfNegativeOrZero(count);
        if ((count & (count - 1)) != 0)
            throw new ArgumentException("Length must be a power of 2.", nameof(count));

        _slots = slots;
        _count = count;
        _mask = _count - 1;
        _shared = default;
    }

    // Producer calls
    public bool TryEnqueue(BufferHandle value)
    {
        long producer = Volatile.Read(ref _shared.Producer);
        long consumer = Volatile.Read(ref _shared.Consumer);
        if (producer - consumer >= _count)
        {
            // Producer wrapped around and bumped into consumer
            return false;
        }
        _slots[producer & _mask] = value;
        Volatile.Write(ref _shared.Producer, producer + 1);
        return true;
    }

    // Consumer calls
    public bool TryDequeue(ref BufferHandle value)
    {
        long producer = Volatile.Read(ref _shared.Producer);
        long consumer = Volatile.Read(ref _shared.Consumer);
        if (consumer >= producer)
        {
            // Consumer bumped into producer
            return false;
        }
        value = _slots[consumer & _mask];
        Volatile.Write(ref _shared.Consumer, consumer + 1);
        return true;
    }
}
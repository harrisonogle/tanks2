using System;

namespace Tanks.Net;

// Single-threaded ring buffer over unmanaged memory
// WARNING: value type, mind the copy semantics - designed for use as private member
public unsafe struct Ring1
{
    private BufferHandle* _slots;
    private int _count;
    private int _mask;
    private long _producer;
    private long _consumer;

    public Ring1(BufferHandle* slots, int count)
    {
        ThrowHelper.ThrowIfNegativeOrZero(count);
        if ((count & (count - 1)) != 0)
            throw new ArgumentException("Length must be a power of 2.", nameof(count));

        _slots = slots;
        _count = count;
        _mask = count - 1;
        _producer = 0;
        _consumer = 0;
    }

    // Producer calls
    public bool TryEnqueue(BufferHandle value)
    {
        if (_producer - _consumer >= _count)
        {
            // Producer wrapped around and bumped into consumer
            return false;
        }
        _slots[_producer & _mask] = value;
        _producer++;
        return true;
    }

    // Consumer calls
    public bool TryDequeue(ref BufferHandle value)
    {
        if (_consumer >= _producer)
        {
            // Consumer bumped into producer
            return false;
        }
        value = _slots[_consumer & _mask];
        _consumer++;
        return true;
    }
}
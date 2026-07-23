using System;
using System.Threading;

namespace Tanks.Net;

// Managed MPMC ring buffer
internal sealed class MpmcRing<T>
{
    private struct Cell
    {
        public long Sequence;
        public T Value;
    }

    private readonly Cell[] _buffer;
    private readonly int _bufferMask;
    
    // Padding prevents false sharing (cache line bouncing) between producers and consumers
    private readonly long[] _padding1 = new long[7]; 
    private long _enqueuePos;
    private readonly long[] _padding2 = new long[7];
    private long _dequeuePos;
    private readonly long[] _padding3 = new long[7];

    public MpmcRing(int capacity)
    {
        // Capacity must be a power of two for fast bitwise modulo operations
        if (capacity < 2 || (capacity & (capacity - 1)) != 0)
        {
            throw new ArgumentException("Capacity must be a power of 2.", nameof(capacity));
        }

        _buffer = new Cell[capacity];
        _bufferMask = capacity - 1;

        for (int i = 0; i < _buffer.Length; i++)
        {
            _buffer[i].Sequence = i;
        }
    }

    public bool TryEnqueue(T item)
    {
        Cell[] buffer = _buffer;
        int mask = _bufferMask;

        while (true)
        {
            long pos = Volatile.Read(ref _enqueuePos);
            int index = (int)(pos & mask);
            
            // Fetch sequence atomically to see if the cell is ready for a write
            long seq = Volatile.Read(ref buffer[index].Sequence);
            long diff = seq - pos;

            if (diff == 0)
            {
                // Slot is ready. Try to claim it before other producers do.
                if (Interlocked.CompareExchange(ref _enqueuePos, pos + 1, pos) == pos)
                {
                    buffer[index].Value = item;
                    // Atomically signal the consumer that the data is ready
                    Volatile.Write(ref buffer[index].Sequence, pos + 1);
                    return true;
                }
            }
            else if (diff < 0)
            {
                // Buffer is full (enqueue position has lapped dequeue position)
                return false;
            }
            
            // If diff > 0, another producer claimed it first. Spin/retry.
        }
    }

    public bool TryDequeue(out T result)
    {
        Cell[] buffer = _buffer;
        int mask = _bufferMask;

        while (true)
        {
            long pos = Volatile.Read(ref _dequeuePos);
            int index = (int)(pos & mask);
            
            long seq = Volatile.Read(ref buffer[index].Sequence);
            long diff = seq - (pos + 1);

            if (diff == 0)
            {
                // Slot has data and is ready to be consumed.
                // Single consumer doesn't strictly need Interlocked, but we advance the position.
                if (Interlocked.CompareExchange(ref _dequeuePos, pos + 1, pos) == pos)
                {
                    result = buffer[index].Value;
                    
                    // Clear the reference for GC if T is a class
                    buffer[index].Value = default!; 
                    
                    // Atomically signal producers that this slot is empty again
                    Volatile.Write(ref buffer[index].Sequence, pos + mask + 1);
                    return true;
                }
            }
            else if (diff < 0)
            {
                // Queue is empty
                result = default!;
                return false;
            }

            // Spin/retry if another thread is mid-operation
        }
    }
}

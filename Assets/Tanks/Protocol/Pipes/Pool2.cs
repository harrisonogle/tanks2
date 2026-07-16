using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System;
using System.Threading;

namespace Tanks.Net;

// SPSC pool: rent and return on different threads
public sealed unsafe class Pool2 : IDisposable
{
    [StructLayout(LayoutKind.Explicit, Size = 4)]
    private struct SlotMetadata
    {
        [FieldOffset(0)] public uint Gen;
    }

    private readonly uint _slotSize;
    private readonly uint _count;

    private byte* _start;
    private readonly byte* _dataEnd;
    private readonly byte* _end;
    private readonly Ring2<BufferHandle> _free;
    private readonly Ring1<BufferHandle> _recycle; // renter-local recycle
    private readonly SlotMetadata* _metadata;

    private int _disposed;

    public Pool2(int slotSize, int count)
    {
        ThrowHelper.ThrowIfNegative(slotSize);
        ThrowHelper.ThrowIfNegativeOrZero(count);

        _slotSize = (uint)slotSize;
        _count = (uint)count;

        // Layout: [ data | fill_ring | abandon_ring | metadata ]
        long bytesPerSlot = _slotSize + 2 * sizeof(BufferHandle) + sizeof(SlotMetadata);
        long totalBytes = bytesPerSlot * _count;
        ThrowHelper.ThrowIfNegative(totalBytes);
        ThrowHelper.ThrowIfGreaterThan(totalBytes, int.MaxValue); // chunk it if you really want >2GiB
        _start = (byte*)Marshal.AllocHGlobal((int)totalBytes);
        _dataEnd = _start + _slotSize * _count;
        _end = _start + totalBytes;

        byte* p = _start + _slotSize * _count;
        _free = new Ring2<BufferHandle>((BufferHandle*)p, (int)_count);
        p += _count * sizeof(BufferHandle);
        _recycle = new Ring1<BufferHandle>((BufferHandle*)p, (int)_count); // same size as _fill so Abandon should always succeed
        p += _count * sizeof(BufferHandle);
        _metadata = (SlotMetadata*)p;
        p += _count * sizeof(SlotMetadata);
        if (p != _end)
            throw new InvalidOperationException("Error constructing pool.");

        uint gen0 = (uint)RandomNumberGenerator.GetInt32(int.MaxValue);

        // Populate the free ring.
        for (uint slotId = 0; slotId < _count; slotId++)
        {
            _metadata[slotId] = new SlotMetadata { Gen = gen0 };

            var handle = new BufferHandle(
                slotId: slotId,
                gen: gen0);

            if (!_recycle.TryEnqueue(handle))
                throw new InvalidOperationException("Error populating recycle ring.");
        }
    }

    public int SlotSize => (int)_slotSize;

    public bool TryRent(ref BufferHandle handle)
    {
        return _recycle.TryDequeue(ref handle) || _free.TryDequeue(ref handle);
    }

    public void Abandon(BufferHandle handle)
    {
        // Validate here on the renter thread. The returner thread will trust handles
        // dequeued from _abandon without revalidating. 
        ThrowIfInvalid(handle);

        if (!_recycle.TryEnqueue(handle))
        {
            // recycle ring same size as fill ring, enqueue should never fail here.
            ThrowPoolCorrupted();
        }
    }

    public void Return(BufferHandle handle)
    {
        ThrowIfInvalid(handle);

        uint nextGen = handle.Gen + 1;
        _metadata[handle.SlotId].Gen = nextGen;

        if (!_free.TryEnqueue(new BufferHandle(
            handle.SlotId,
            nextGen)))
        {
            // Free ring can't be full: sized to _count, at most _count handles outstanding.
            // Reaching here means double-return, SPSC violation, or counter corruption.
            ThrowPoolCorrupted();
        }
    }

    public byte* Deref(BufferHandle handle)
    {
        CheckDisposed();
        ThrowIfInvalid(handle);
        byte* p = _start + _slotSize * handle.SlotId;
        if (p < _start || p >= _dataEnd)
            throw new ArgumentException("Invalid handle.", nameof(handle));
        return p;
    }

    private void ThrowIfInvalid(BufferHandle handle)
    {
        ThrowHelper.ThrowIfGreaterThanOrEqual(handle.SlotId, _count);
        ThrowHelper.ThrowIfNotEqual(handle.Gen, _metadata[handle.SlotId].Gen);
    }

    [Conditional("DEBUG")]
    private void AssertValid(BufferHandle handle)
    {
        Debug.Assert(handle.SlotId >= 0);
        Debug.Assert(handle.SlotId < _count);
        Debug.Assert(handle.Gen == _metadata[handle.SlotId].Gen);
    }

    ~Pool2() => DisposeCore();

    public void Dispose()
    {
        DisposeCore();
        GC.SuppressFinalize(this);
    }

    private void DisposeCore()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;

        if (_start != null)
            Marshal.FreeHGlobal((IntPtr)_start);
        _start = null;
    }

    private void CheckDisposed()
    {
        ThrowHelper.ThrowIfDisposed(_disposed != 0, GetType());
    }

    [DoesNotReturn]
    private static void ThrowPoolCorrupted()
    {
        throw new InvalidOperationException("Pool corrupted.");
    }
}
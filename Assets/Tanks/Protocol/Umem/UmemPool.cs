using System;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Threading;

namespace Tanks.Net;

// SPSC pool: rent+enqueue, dequeue+return on different threads
public sealed unsafe class UmemPool : IDisposable
{
    [StructLayout(LayoutKind.Explicit, Size = 4)]
    private struct SlotMetadata
    {
        [FieldOffset(0)] public uint Gen;
    }

    private readonly uint _slotSize;
    private readonly uint _slotCount;

    private byte* _start;
    private readonly byte* _dataEnd;
    private readonly byte* _end;
    private Ring2 _fill;
    private Ring1 _free; // renter-local recycle
    private Ring2 _xfer;
    private readonly SlotMetadata* _metadata;

    private int _disposed;

    public UmemPool(int slotSize, int slotCount)
    {
        ThrowHelper.ThrowIfNegative(slotSize);
        ThrowHelper.ThrowIfNegativeOrZero(slotCount);

        _slotSize = (uint)slotSize;
        _slotCount = (uint)slotCount;
        SlotSize = slotSize;
        SlotCount = slotCount;

        // Layout: [ data | fill_ring | free_ring | xfer_ring | metadata ]
        long bytesPerSlot = _slotSize + 3 * sizeof(BufferHandle) + sizeof(SlotMetadata);
        long totalBytes = bytesPerSlot * _slotCount;
        ThrowHelper.ThrowIfNegative(totalBytes);
        ThrowHelper.ThrowIfGreaterThan(totalBytes, int.MaxValue); // chunk it if you really want >2GiB
        _start = (byte*)Marshal.AllocHGlobal((int)totalBytes);
        _dataEnd = _start + _slotSize * _slotCount;
        _end = _start + totalBytes;

        byte* p = _start + _slotSize * _slotCount;
        _fill = new Ring2((BufferHandle*)p, (int)_slotCount);
        p += _slotCount * sizeof(BufferHandle);
        _free = new Ring1((BufferHandle*)p, (int)_slotCount); // same size as _fill so Abandon should always succeed
        p += _slotCount * sizeof(BufferHandle);
        _xfer = new Ring2((BufferHandle*)p, (int)_slotCount);
        p += _slotCount * sizeof(BufferHandle);
        _metadata = (SlotMetadata*)p;
        p += _slotCount * sizeof(SlotMetadata);
        if (p != _end)
            throw new InvalidOperationException("Error constructing pool.");

        uint gen0 = (uint)RandomNumberGenerator.GetInt32(int.MaxValue);

        // Populate the free ring.
        for (uint slotId = 0; slotId < _slotCount; slotId++)
        {
            _metadata[slotId] = new SlotMetadata { Gen = gen0 };

            var handle = new BufferHandle(
                slotId: slotId,
                gen: gen0);

            if (!_free.TryEnqueue(handle))
                throw new InvalidOperationException("Error populating free ring.");
        }
    }

    public readonly int SlotSize;
    public readonly int SlotCount;

    public bool TryRent(ref BufferHandle handle)
    {
        return _free.TryDequeue(ref handle) || _fill.TryDequeue(ref handle);
    }

    public void Abandon(BufferHandle handle)
    {
        AssertValid(handle);

        if (!_free.TryEnqueue(handle))
        {
            // recycle ring same size as fill ring, enqueue should never fail here.
            ThrowPoolCorrupted();
        }
    }

    public bool TryEnqueue(BufferHandle handle) => _xfer.TryEnqueue(handle);

    public bool TryDequeue(ref BufferHandle handle) => _xfer.TryDequeue(ref handle);

    public void Return(BufferHandle handle)
    {
        ThrowIfInvalid(handle);

        uint nextGen = handle.Gen + 1;
        _metadata[handle.SlotId].Gen = nextGen;

        if (!_fill.TryEnqueue(new BufferHandle(
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
        AssertValid(handle);
        byte* p = _start + _slotSize * handle.SlotId;
        if (p < _start || p >= _dataEnd)
            throw new ArgumentException("Invalid handle.", nameof(handle));
        return p;
    }

    // intended for test usage only (in assemblies that are not `unsafe`)
    public ref T Deref<T>(BufferHandle handle) where T : unmanaged
    {
        return ref *(T*)Deref(handle);
    }

    private void ThrowIfInvalid(BufferHandle handle)
    {
        ThrowHelper.ThrowIfGreaterThanOrEqual(handle.SlotId, _slotCount);
        ThrowHelper.ThrowIfNotEqual(handle.Gen, _metadata[handle.SlotId].Gen);
    }

    [Conditional("DEBUG")]
    private void AssertValid(BufferHandle handle)
    {
        Debug.Assert(handle.SlotId >= 0);
        Debug.Assert(handle.SlotId < _slotCount);
        Debug.Assert(handle.Gen == _metadata[handle.SlotId].Gen);
    }

    ~UmemPool() => DisposeCore();

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
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.InteropServices;
using System.Security.Cryptography;

namespace Tanks.Net;

public sealed unsafe class Pool1 : IDisposable
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
    private readonly Ring1<BufferHandle> _free;
    private readonly SlotMetadata* _metadata;

    private int _disposed;

    public Pool1(int slotSize, int count)
    {
        ThrowHelper.ThrowIfNegativeOrZero(slotSize);
        ThrowHelper.ThrowIfNegativeOrZero(count);

        _slotSize = (uint)slotSize;
        _count = (uint)count;

        // Layout: [ data | fill_ring | metadata ]
        long bytesPerSlot = ((int)_slotSize) + sizeof(BufferHandle) + sizeof(SlotMetadata);
        long totalBytes = bytesPerSlot * (int)_count;
        ThrowHelper.ThrowIfNegative(totalBytes);
        ThrowHelper.ThrowIfGreaterThan(totalBytes, int.MaxValue);
        _start = (byte*)Marshal.AllocHGlobal((int)totalBytes);
        _dataEnd = _start + _slotSize * _count;
        _end = _start + totalBytes;

        byte* p = _start + _slotSize * _count;
        _free = new Ring1<BufferHandle>((BufferHandle*)p, (int)_count);
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

            if (!_free.TryEnqueue(handle))
                throw new InvalidOperationException("Error populating free ring.");
        }
    }

    public int SlotSize => (int)_slotSize;

    public bool TryRent(ref BufferHandle handle)
    {
        if (_free.TryDequeue(ref handle))
        {
            _metadata[handle.SlotId].Gen = handle.Gen;
            return true;
        }
        return false;
    }

    public void Return(BufferHandle handle)
    {
        ThrowIfInvalid(handle);

        if (!_free.TryEnqueue(new BufferHandle(
            handle.SlotId,
            handle.Gen + 1)))
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

    public bool TryDeref(BufferHandle handle, out byte* buffer)
    {
        CheckDisposed();
        if (!IsValid(handle))
        {
            buffer = default;
            return false;
        }
        byte* p = _start + _slotSize * handle.SlotId;
        if (p < _start || p >= _dataEnd)
        {
            buffer = default;
            return false;
        }
        buffer = p;
        return true;
    }

    private void ThrowIfInvalid(BufferHandle handle)
    {
        ThrowHelper.ThrowIfGreaterThanOrEqual(handle.SlotId, _count);
        ThrowHelper.ThrowIfNotEqual(handle.Gen, _metadata[handle.SlotId].Gen);
    }

    private bool IsValid(BufferHandle handle)
    {
        return handle.SlotId < _count &&
            handle.Gen == _metadata[handle.SlotId].Gen;
    }

    [Conditional("DEBUG")]
    private void AssertValid(BufferHandle handle)
    {
        Debug.Assert(IsValid(handle));
    }

    ~Pool1() => DisposeCore();

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
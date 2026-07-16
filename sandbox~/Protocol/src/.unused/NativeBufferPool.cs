using System.Buffers;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace Tanks.Net;

public sealed unsafe class NativeBufferPool : IDisposable
{
    private readonly ConcurrentStack<IntPtr> _free = new();
    private byte* _base;
    private readonly int _bufferSize;
    private readonly int _capacity;
    private int _disposed;

    public int BufferSize => _bufferSize;
    public int Capacity => _capacity;

    public NativeBufferPool(int bufferSize, int capacity)
    {
        _bufferSize = bufferSize;
        _capacity = capacity;
        _base = (byte*)Marshal.AllocHGlobal(bufferSize * capacity);
        for (int i = 0; i < capacity; i++)
            _free.Push((IntPtr)(_base + i * bufferSize));
    }

    public byte* Rent()
    {
        if (_free.TryPop(out IntPtr ptr)) return (byte*)ptr;
        throw new InvalidOperationException("Pool exhausted");
    }

    public void Return(byte* ptr)
    {
        Debug.Assert(ptr >= _base && ptr < _base + _capacity * _bufferSize);
        Debug.Assert((ptr - _base) % _bufferSize == 0);
        _free.Push((IntPtr)ptr);
    }

    ~NativeBufferPool() => DisposeCore();

    public void Dispose()
    {
        DisposeCore();
        GC.SuppressFinalize(this);
    }

    private void DisposeCore()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        if (_base != null)
        {
            Marshal.FreeHGlobal((IntPtr)_base);
            _base = null;
        }
    }
}
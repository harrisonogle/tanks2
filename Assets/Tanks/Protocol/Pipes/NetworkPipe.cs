using System.Runtime.InteropServices;
using System;
using System.Threading;

namespace Tanks.Net;

public readonly struct NetworkQueue
{
    public NetworkQueue(
        Ring2<BufferHandle> ring,
        Pool2 pool)
    {
        Ring = ring;
        Pool = pool;
        Reader = new PipeReader(Ring, Pool);
        Writer = new PipeWriter(Ring, Pool);
    }

    public readonly Pool2 Pool;
    public readonly Ring2<BufferHandle> Ring;
    public readonly IPipeReader Reader;
    public readonly IPipeWriter Writer;
}

public readonly struct NetworkPipe
{
    public NetworkPipe(NetworkQueue rx, NetworkQueue tx)
    {
        RX = rx;
        TX = tx;
    }

    public readonly NetworkQueue RX;
    public readonly NetworkQueue TX;
}

public interface INetworkPipeLifetime : IDisposable
{
    public NetworkPipe Pipe { get; }
}

public interface INetworkPipeFactory
{
    INetworkPipeLifetime Create(int slotCount = 0);
}

public sealed class NetworkPipeFactory : INetworkPipeFactory
{
    public INetworkPipeLifetime Create(int slotCount = 0)
    {
        return new NetworkPipeLifetime(slotCount);
    }
}

internal sealed unsafe class NetworkPipeLifetime : INetworkPipeLifetime
{
    private readonly Pool2 _rxPool;
    private readonly Pool2 _txPool;
    private readonly Ring2<BufferHandle> _rxRing;
    private readonly Ring2<BufferHandle> _txRing;
    private readonly NetworkPipe _networkInterface;

    private byte* _start;

    private int _disposed;

    public NetworkPipe Pipe
    {
        get
        {
            ThrowHelper.ThrowIfDisposed(_disposed != 0, GetType());
            return _networkInterface;
        }
    }

    public NetworkPipeLifetime(int slotCount = 0)
    {
        ThrowHelper.ThrowIfNegative(slotCount);

        // The number of slots in RX and TX rings must be at least as large as expected max
        // session count because peers will exchange packets ~once per tick
        const int DefaultSlotCount = 1 << 17;

        if (slotCount == 0)
        {
            slotCount = DefaultSlotCount;
        }
        else if ((slotCount & (slotCount - 1)) != 0)
        {
            throw new ArgumentException("Slot count must be a power of 2.", nameof(slotCount));
        }

        try
        {
            _rxPool = new Pool2(NetworkConstants.ApplicationDataSlotSize, slotCount);
            _txPool = new Pool2(NetworkConstants.ApplicationDataSlotSize, slotCount);

            long bytesPerRing = sizeof(BufferHandle) * slotCount;
            long totalBytes = bytesPerRing * 2;
            ThrowHelper.ThrowIfNegative(totalBytes);
            ThrowHelper.ThrowIfGreaterThan(totalBytes, int.MaxValue); // chunk it if you really want >2GiB
            _start = (byte*)Marshal.AllocHGlobal((int)totalBytes);

            BufferHandle* p = (BufferHandle*)_start;
            _rxRing = new Ring2<BufferHandle>(p, slotCount);
            p += slotCount;
            _txRing = new Ring2<BufferHandle>(p, slotCount);
            p += slotCount;
            if ((byte*)p != _start + totalBytes)
                throw new InvalidOperationException("Failed to create message multiplexer rings.");

            _networkInterface = new NetworkPipe(
                rx: new NetworkQueue(_rxRing, _rxPool),
                tx: new NetworkQueue(_txRing, _txPool));
        }
        catch
        {
            Dispose();
            throw;
        }
    }

    ~NetworkPipeLifetime() => DisposeCore();

    public void Dispose()
    {
        DisposeCore();
        GC.SuppressFinalize(this);
    }

    private void DisposeCore()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        using (_rxPool)
        using (_txPool)
        {
            if (_start != null)
            {
                Marshal.FreeHGlobal((IntPtr)_start);
                _start = null;
            }
        }
    }
}
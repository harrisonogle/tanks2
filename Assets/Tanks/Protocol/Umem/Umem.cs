using System.Runtime.InteropServices;
using System;
using System.Threading;

namespace Tanks.Net;

public sealed unsafe class Umem : IDisposable
{
    private int _disposed;

    public Umem(int slotCount, int slotSize)
    {
        ThrowHelper.ThrowIfNegativeOrZero(slotCount);
        ThrowHelper.ThrowIfNegativeOrZero(slotSize);

        if ((slotCount & (slotCount - 1)) != 0)
        {
            throw new ArgumentException("Slot count must be a power of 2.", nameof(slotCount));
        }

        try
        {
            Rx = new UmemPool(slotSize, slotCount);
            Tx = new UmemPool(slotSize, slotCount);
        }
        catch
        {
            Dispose();
            throw;
        }
    }

    public readonly UmemPool Rx; // RX + FILL
    public readonly UmemPool Tx; // TX + COMPLETION

    ~Umem() => DisposeCore();

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

        using (Rx)
        using (Tx)
        {
        }
    }
}
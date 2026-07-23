using Tanks.Net;
using Xunit;

namespace Tests;

public sealed unsafe class UmemPoolTests
{
    private static BufferHandle Rent(UmemPool pool)
    {
        BufferHandle handle = default;
        Assert.True(pool.TryRent(ref handle));
        return handle;
    }

    [Fact]
    public void RentAll_ThenRentFails()
    {
        using var pool = new UmemPool(slotSize: 64, slotCount: 4);

        for (int i = 0; i < 4; i++)
            _ = Rent(pool);
        BufferHandle handle = default;
        Assert.False(pool.TryRent(ref handle));
    }

    [Fact]
    public void Return_MakesSlotRentable_AndBumpsGeneration()
    {
        using var pool = new UmemPool(slotSize: 64, slotCount: 1);

        BufferHandle first = Rent(pool);
        _ = pool.Deref(first); // valid while rented
        pool.Return(first);

        BufferHandle second = Rent(pool);
        Assert.Equal(first.SlotId, second.SlotId);
        Assert.NotEqual(first.Gen, second.Gen);

        // The stale handle must no longer deref.
        Assert.ThrowsAny<Exception>(() => pool.Deref(first));
        _ = pool.Deref(second);
    }

    [Fact]
    public void Abandon_RecyclesOnRenterThread()
    {
        using var pool = new UmemPool(slotSize: 64, slotCount: 4);

        // Rent everything, abandon everything - all slots must come back
        // without any Return (the renter-local recycle ring).
        var handles = new BufferHandle[4];
        for (int i = 0; i < 4; i++)
            handles[i] = Rent(pool);
        for (int i = 0; i < 4; i++)
            pool.Abandon(handles[i]);
        for (int i = 0; i < 4; i++)
            _ = Rent(pool);
    }

    [Fact]
    public void DoubleReturn_Throws()
    {
        using var pool = new UmemPool(slotSize: 64, slotCount: 2);

        BufferHandle h = Rent(pool);
        pool.Return(h);
        Assert.ThrowsAny<Exception>(() => pool.Return(h)); // stale gen detected
    }

    [Fact]
    public void CrossThread_RentAndReturn_Spsc()
    {
        using var pool = new UmemPool(slotSize: 64, slotCount: 16);
        const int iterations = 100_000;

        // Producer rents, consumer returns - the pool's intended topology.
        var handoff = new System.Collections.Concurrent.BlockingCollection<BufferHandle>(boundedCapacity: 16);

        var consumer = new Thread(() =>
        {
            foreach (BufferHandle h in handoff.GetConsumingEnumerable())
                pool.Return(h);
        })
        { IsBackground = true };
        consumer.Start();

        BufferHandle handle = default;
        for (int i = 0; i < iterations;)
        {
            if (pool.TryRent(ref handle))
            {
                handoff.Add(handle);
                i++;
            }
            else
            {
                Thread.SpinWait(20);
            }
        }
        handoff.CompleteAdding();
        Assert.True(consumer.Join(TimeSpan.FromSeconds(30)));

        // All slots must eventually be home again.
        for (int i = 0; i < 16; i++)
            _ = Rent(pool);
        Assert.False(pool.TryRent(ref handle));
    }
}

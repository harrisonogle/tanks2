using System.Runtime.InteropServices;
using Tanks.Net;
using Xunit;

namespace Tests;

public sealed unsafe class RingTests
{
    [Fact]
    public void Ring1_FillDrain_PreservesOrderAndCapacity()
    {
        const int capacity = 8;
        long* slots = (long*)Marshal.AllocHGlobal(sizeof(long) * capacity);
        try
        {
            var ring = new Ring1<long>(slots, capacity);

            for (int i = 0; i < capacity; i++)
                Assert.True(ring.TryEnqueue(i));
            Assert.False(ring.TryEnqueue(99)); // full

            long v = 0;
            for (int i = 0; i < capacity; i++)
            {
                Assert.True(ring.TryDequeue(ref v));
                Assert.Equal(i, v);
            }
            Assert.False(ring.TryDequeue(ref v)); // empty
        }
        finally
        {
            Marshal.FreeHGlobal((nint)slots);
        }
    }

    [Fact]
    public void Ring1_WrapAround_Works()
    {
        const int capacity = 4;
        long* slots = (long*)Marshal.AllocHGlobal(sizeof(long) * capacity);
        try
        {
            var ring = new Ring1<long>(slots, capacity);

            // Push the indices far past the capacity to cross the wrap.
            long v = 0;
            for (long i = 0; i < 1000; i++)
            {
                Assert.True(ring.TryEnqueue(i));
                Assert.True(ring.TryDequeue(ref v));
                Assert.Equal(i, v);
            }
        }
        finally
        {
            Marshal.FreeHGlobal((nint)slots);
        }
    }

    [Fact]
    public void Ring_RejectsNonPowerOfTwoCapacity()
    {
        Assert.ThrowsAny<ArgumentException>(() =>
        {
            long* s = (long*)Marshal.AllocHGlobal(sizeof(long) * 3);
            try { _ = new Ring2<long>(s, 3); }
            finally { Marshal.FreeHGlobal((nint)s); }
        });
    }

    [Fact]
    public void Ring2_SpscStress_NothingLostNothingReordered()
    {
        const int capacity = 1024;
        const long count = 1_000_000;
        long* slots = (long*)Marshal.AllocHGlobal(sizeof(long) * capacity);
        try
        {
            var ring = new Ring2<long>(slots, capacity);
            long consumerError = -1;

            var consumer = new Thread(() =>
            {
                long expected = 0;
                long v = 0;
                while (expected < count)
                {
                    if (ring.TryDequeue(ref v))
                    {
                        if (v != expected)
                        {
                            consumerError = expected;
                            return;
                        }
                        expected++;
                    }
                    else
                    {
                        Thread.SpinWait(10);
                    }
                }
            })
            { IsBackground = true };

            consumer.Start();

            for (long i = 0; i < count;)
            {
                if (ring.TryEnqueue(i)) i++;
                else Thread.SpinWait(10);
            }

            Assert.True(consumer.Join(TimeSpan.FromSeconds(30)), "consumer did not finish");
            Assert.Equal(-1, consumerError);
        }
        finally
        {
            Marshal.FreeHGlobal((nint)slots);
        }
    }
}

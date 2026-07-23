using System.Runtime.InteropServices;
using Tanks.Net;
using Xunit;

namespace Tests;

// The rings are BufferHandle-typed now (no generics): sequence numbers ride in
// SlotId, and Gen carries the bitwise complement so any torn/stale read of the
// two halves is caught by the pairing check.
public sealed unsafe class RingTests
{
    private static BufferHandle Value(long i) => new BufferHandle((uint)i, ~(uint)i);

    private static void AssertValue(long expected, BufferHandle actual)
    {
        Assert.Equal((uint)expected, actual.SlotId);
        Assert.Equal(~(uint)expected, actual.Gen);
    }

    [Fact]
    public void Ring1_FillDrain_PreservesOrderAndCapacity()
    {
        const int capacity = 8;
        var slots = (BufferHandle*)Marshal.AllocHGlobal(sizeof(BufferHandle) * capacity);
        try
        {
            var ring = new Ring1(slots, capacity);

            for (int i = 0; i < capacity; i++)
                Assert.True(ring.TryEnqueue(Value(i)));
            Assert.False(ring.TryEnqueue(Value(99))); // full

            BufferHandle v = default;
            for (int i = 0; i < capacity; i++)
            {
                Assert.True(ring.TryDequeue(ref v));
                AssertValue(i, v);
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
        var slots = (BufferHandle*)Marshal.AllocHGlobal(sizeof(BufferHandle) * capacity);
        try
        {
            var ring = new Ring1(slots, capacity);

            // Push the indices far past the capacity to cross the wrap.
            BufferHandle v = default;
            for (long i = 0; i < 1000; i++)
            {
                Assert.True(ring.TryEnqueue(Value(i)));
                Assert.True(ring.TryDequeue(ref v));
                AssertValue(i, v);
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
            var s = (BufferHandle*)Marshal.AllocHGlobal(sizeof(BufferHandle) * 3);
            try { _ = new Ring2(s, 3); }
            finally { Marshal.FreeHGlobal((nint)s); }
        });
    }

    [Fact]
    public void Ring2_SpscStress_NothingLostNothingReordered()
    {
        const int capacity = 1024;
        const long count = 1_000_000;
        var slots = (BufferHandle*)Marshal.AllocHGlobal(sizeof(BufferHandle) * capacity);
        try
        {
            var ring = new Ring2(slots, capacity);
            long consumerError = -1;

            var consumer = new Thread(() =>
            {
                long expected = 0;
                BufferHandle v = default;
                while (expected < count)
                {
                    if (ring.TryDequeue(ref v))
                    {
                        // Order AND integrity: Gen must be SlotId's complement,
                        // or the consumer saw a torn/stale slot.
                        if (v.SlotId != (uint)expected || v.Gen != ~(uint)expected)
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
                if (ring.TryEnqueue(Value(i))) i++;
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

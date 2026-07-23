using Tanks.Net;
using Xunit;

namespace Tests;

public sealed unsafe class UmemTests
{
    [Fact]
    public void LayoutConstants_HoldTheDocumentedInvariants()
    {
        // A whole datagram (plus the oversize-detection byte) must fit in a
        // slot after the metadata headroom.
        Assert.True(NetworkConstants.ApplicationDataSlotSize >=
            NetworkConstants.NetworkEventHeadroom + NetworkConstants.MaxDatagramSize + 1);

        Assert.Equal(NetworkEventMetadata.Size, NetworkConstants.NetworkEventHeadroom);
        Assert.Equal(
            NetworkConstants.MaxDatagramSize - PacketHeader.Size - NetworkConstants.AuthTagLength,
            NetworkConstants.MaxApplicationDataLength);
    }

    [Fact]
    public void Create_RejectsNonPowerOfTwoSlotCount()
    {
        Assert.ThrowsAny<ArgumentException>(() => new Umem(slotCount: 3, slotSize: 64));
        Assert.ThrowsAny<ArgumentException>(() => new UmemPool(slotSize: 64, slotCount: 3));
    }

    [Fact]
    public void SessionEvent_MetadataRoundtrips_ThroughThePool()
    {
        using var umem = new Umem(slotCount: 8, slotSize: NetworkConstants.ApplicationDataSlotSize);
        UmemPool rx = umem.Rx;

        // The producer writes metadata + payload directly into the slot, then
        // publishes the bare handle. (Regression guard: metadata must be written
        // to the slot BEFORE the ring enqueue, or the consumer can see garbage.)
        BufferHandle handle = default;
        Assert.True(rx.TryRent(ref handle));
        byte* slot = rx.Deref(handle);
        var metadata = (NetworkEventMetadata*)slot;
        metadata->SessionId = new SessionId(42, 7);
        metadata->Kind = NetworkEventKind.ApplicationData;
        metadata->Offset = PacketHeader.Size;
        metadata->Length = 123;
        slot[NetworkConstants.NetworkEventHeadroom + metadata->Offset] = 0xAB;
        Assert.True(rx.TryEnqueue(handle));

        BufferHandle received = default;
        Assert.True(rx.TryDequeue(ref received));
        byte* readSlot = rx.Deref(received);
        var read = (NetworkEventMetadata*)readSlot;
        Assert.Equal(received.SlotId, handle.SlotId);
        Assert.Equal(new SessionId(42, 7), read->SessionId);
        Assert.Equal(NetworkEventKind.ApplicationData, read->Kind);
        Assert.Equal(PacketHeader.Size, read->Offset);
        Assert.Equal(123, read->Length);
        Assert.Equal(0xAB, readSlot[NetworkConstants.NetworkEventHeadroom + read->Offset]);

        rx.Return(received);
    }

    [Fact]
    public void RxAndTx_AreIndependentPools()
    {
        using var umem = new Umem(slotCount: 2, slotSize: 64);

        // Exhaust RX; TX must be unaffected (separate data areas and rings).
        BufferHandle handle = default;
        Assert.True(umem.Rx.TryRent(ref handle));
        Assert.True(umem.Rx.TryRent(ref handle));
        Assert.False(umem.Rx.TryRent(ref handle));

        Assert.True(umem.Tx.TryRent(ref handle));
    }

    [Fact]
    public void PoolGenerations_AreRandomizedPerPool()
    {
        // Cross-pool handle confusion should be caught probabilistically:
        // two pools must not (except astronomically rarely) share gen zero.
        using var a = new UmemPool(slotSize: 64, slotCount: 2);
        using var b = new UmemPool(slotSize: 64, slotCount: 2);

        BufferHandle ha = default, hb = default;
        Assert.True(a.TryRent(ref ha));
        Assert.True(b.TryRent(ref hb));
        Assert.NotEqual(ha.Gen, hb.Gen);
    }
}

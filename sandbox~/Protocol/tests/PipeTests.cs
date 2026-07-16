using Tanks.Net;
using Xunit;

namespace Tests;

public sealed unsafe class PipeTests
{
    [Fact]
    public void LayoutConstants_HoldTheDocumentedInvariants()
    {
        // A whole datagram (plus the oversize-detection byte) must fit in a
        // slot after the metadata headroom.
        Assert.True(NetworkConstants.ApplicationDataSlotSize >=
            NetworkConstants.PipeEventHeadroom + NetworkConstants.MaxDatagramSize + 1);

        Assert.Equal(PipeEventMetadata.Size, NetworkConstants.PipeEventHeadroom);
        Assert.Equal(
            NetworkConstants.MaxDatagramSize - PacketHeader.Size - NetworkConstants.AuthTagLength,
            NetworkConstants.MaxApplicationDataLength);
    }

    [Fact]
    public void Create_RejectsNonPowerOfTwoSlotCount()
    {
        var factory = new NetworkPipeFactory();
        Assert.ThrowsAny<ArgumentException>(() => factory.Create(slotCount: 3));
    }

    [Fact]
    public void PipeEvent_MetadataRoundtrips_ThroughWriterAndReader()
    {
        using INetworkPipeLifetime lifetime = new NetworkPipeFactory().Create(slotCount: 8);
        NetworkPipe pipe = lifetime.Pipe;
        IPipeWriter writer = pipe.RX.Writer;
        IPipeReader reader = pipe.RX.Reader;

        var sent = default(PipeEvent);
        Assert.True(writer.TryRent(ref sent.Buffer));
        sent.Metadata.SessionId = new SessionId(42, 7);
        sent.Metadata.Kind = PipeEventKind.ApplicationData;
        sent.Metadata.Offset = PacketHeader.Size;
        sent.Metadata.Length = 123;

        byte* payload = writer.Deref(ref sent);
        payload[sent.Metadata.Offset] = 0xAB;

        // Metadata must be visible to the consumer the instant the handle
        // is published (regression guard: metadata is written to the slot
        // BEFORE the ring enqueue).
        Assert.True(writer.TryEnqueue(ref sent));

        var received = default(PipeEvent);
        Assert.True(reader.TryDequeue(ref received));
        Assert.Equal(sent.Buffer.SlotId, received.Buffer.SlotId);
        Assert.Equal(new SessionId(42, 7), received.Metadata.SessionId);
        Assert.Equal(PipeEventKind.ApplicationData, received.Metadata.Kind);
        Assert.Equal(PacketHeader.Size, received.Metadata.Offset);
        Assert.Equal(123, received.Metadata.Length);
        Assert.Equal(0xAB, reader.Deref(ref received)[received.Metadata.Offset]);

        reader.Return(received.Buffer);
    }

    [Fact]
    public void RawHandlePath_MatchesDriverPattern()
    {
        using INetworkPipeLifetime lifetime = new NetworkPipeFactory().Create(slotCount: 8);
        NetworkPipe pipe = lifetime.Pipe;

        // The driver writes metadata directly into the slot, then enqueues
        // the bare handle - same contract, no PipeEvent wrapper.
        BufferHandle handle = default;
        Assert.True(pipe.TX.Pool.TryRent(ref handle));
        byte* slot = pipe.TX.Pool.Deref(handle);
        var metadata = (PipeEventMetadata*)slot;
        metadata->SessionId = new SessionId(1, 1);
        metadata->Kind = PipeEventKind.ActiveOpen;
        metadata->Offset = 0;
        metadata->Length = Connect.Size;
        Assert.True(pipe.TX.Ring.TryEnqueue(handle));

        BufferHandle dequeued = default;
        Assert.True(pipe.TX.Ring.TryDequeue(ref dequeued));
        var read = (PipeEventMetadata*)pipe.TX.Pool.Deref(dequeued);
        Assert.Equal(PipeEventKind.ActiveOpen, read->Kind);
        Assert.Equal(Connect.Size, read->Length);
        pipe.TX.Pool.Return(dequeued);
    }

    [Fact]
    public void PoolGenerations_AreRandomizedPerPool()
    {
        // Cross-pool handle confusion should be caught probabilistically:
        // two pools must not (except astronomically rarely) share gen zero.
        using var a = new Pool2(slotSize: 64, count: 2);
        using var b = new Pool2(slotSize: 64, count: 2);

        BufferHandle ha = default, hb = default;
        Assert.True(a.TryRent(ref ha));
        Assert.True(b.TryRent(ref hb));
        Assert.NotEqual(ha.Gen, hb.Gen);
    }
}

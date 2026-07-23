using System.Net;
using Tanks.Net;
using Xunit;

namespace Tests;

// Two real Networks over the in-memory transport pair: the test body plays
// the role of both game threads (draining RX queues, sending payloads).
public sealed unsafe class IntegrationTests
{
    private const ushort AppId = 1;
    private const ushort AppVer = 1;
    private const int UmemSlotCount = 1 << 10;
    private static readonly TimeSpan HandshakeTimeout = TimeSpan.FromSeconds(15);

    private static NetAddress Addr(string ip) =>
        NetAddress.FromIPEndPoint(new IPEndPoint(IPAddress.Parse(ip), 55555));

    private static void SendPayload(UmemPool tx, SessionId sid, int length)
    {
        BufferHandle handle = default;
        Assert.True(tx.TryRent(ref handle));
        byte* slot = tx.Deref(handle);
        var metadata = (NetworkEventMetadata*)slot;
        metadata->SessionId = sid;
        metadata->Kind = NetworkEventKind.ApplicationData;
        metadata->Offset = PacketHeader.Size;
        metadata->Length = (ushort)length;
        byte* payload = slot + NetworkConstants.NetworkEventHeadroom + PacketHeader.Size;
        for (int i = 0; i < length; i++)
            payload[i] = (byte)(i * 7);
        Assert.True(tx.TryEnqueue(handle));
    }

    [Fact]
    public void SimultaneousOpen_Establishes_AndPayloadsRoundtrip()
    {
        var (ta, tb) = InMemoryDatagramTransport.Create();
        ta.Bind(Addr("1.1.1.1"));
        tb.Bind(Addr("2.2.2.2"));
        using var a = new Network(ta, TanksPeerCrypto.Instance, NullLog.Instance, AppId, AppVer, UmemSlotCount);
        using var b = new Network(tb, TanksPeerCrypto.Instance, NullLog.Instance, AppId, AppVer, UmemSlotCount);

        try
        {
            a.Start();
            b.Start();

            // Both sides dial: exercises the simultaneous-open tiebreak.
            a.Connect(b.LocalPeerId, new IPEndPoint(IPAddress.Parse("2.2.2.2"), 55555));
            b.Connect(a.LocalPeerId, new IPEndPoint(IPAddress.Parse("1.1.1.1"), 55555));

            SessionId sidA = default, sidB = default;
            Assert.True(TestHelpers.WaitFor(a.Umem.Rx, NetworkEventKind.SessionEstablished, HandshakeTimeout,
                (ref NetworkEventMetadata md, byte* _) => sidA = md.SessionId), "A did not establish");
            Assert.True(TestHelpers.WaitFor(b.Umem.Rx, NetworkEventKind.SessionEstablished, HandshakeTimeout,
                (ref NetworkEventMetadata md, byte* _) => sidB = md.SessionId), "B did not establish");

            // A -> B payload, zero-copy both ways.
            SendPayload(a.Umem.Tx, sidA, length: 32);

            bool matched = false;
            Assert.True(TestHelpers.WaitFor(b.Umem.Rx, NetworkEventKind.ApplicationData, HandshakeTimeout,
                (ref NetworkEventMetadata md, byte* payload) =>
                {
                    Assert.Equal(32, md.Length);
                    matched = true;
                    for (int i = 0; i < 32; i++)
                        if (payload[i] != (byte)(i * 7)) { matched = false; break; }
                }), "B did not receive the payload");
            Assert.True(matched, "payload bytes did not roundtrip");

            // Liveness counters agree there was no loss.
            Assert.Equal(0, a.Metrics.RxDroppedBadMac);
            Assert.Equal(0, b.Metrics.RxDroppedBadMac);
        }
        finally
        {
            a.Stop();
            b.Stop();
        }
    }

    [Fact]
    public void DuplicatedLink_RepliesDropped_PayloadDeliveredOnce()
    {
        var (ia, ib) = InMemoryDatagramTransport.Create();
        ia.Bind(Addr("1.1.1.1"));
        ib.Bind(Addr("2.2.2.2"));

        // Every datagram is sent twice in both directions from the start.
        var ta = new LossyTransport(ia, seed: 7, dropRate: 0) { DuplicateAll = true };
        var tb = new LossyTransport(ib, seed: 8, dropRate: 0) { DuplicateAll = true };

        using var a = new Network(ta, TanksPeerCrypto.Instance, NullLog.Instance, AppId, AppVer, UmemSlotCount);
        using var b = new Network(tb, TanksPeerCrypto.Instance, NullLog.Instance, AppId, AppVer, UmemSlotCount);

        try
        {
            a.Start();
            b.Start();

            a.Connect(b.LocalPeerId, new IPEndPoint(IPAddress.Parse("2.2.2.2"), 55555));

            SessionId sidA = default;
            Assert.True(TestHelpers.WaitFor(a.Umem.Rx, NetworkEventKind.SessionEstablished, HandshakeTimeout,
                (ref NetworkEventMetadata md, byte* _) => sidA = md.SessionId));
            Assert.True(TestHelpers.WaitFor(b.Umem.Rx, NetworkEventKind.SessionEstablished, HandshakeTimeout));

            // One payload; B must deliver it exactly once (the duplicate is
            // an authenticated replay and dies at the window).
            SendPayload(a.Umem.Tx, sidA, length: 24);
            Assert.True(TestHelpers.WaitFor(b.Umem.Rx, NetworkEventKind.ApplicationData, HandshakeTimeout));

            // Give the duplicate time to arrive, then assert nothing else
            // surfaced and the replay counter moved.
            Thread.Sleep(300);
            Assert.False(TestHelpers.WaitFor(b.Umem.Rx, NetworkEventKind.ApplicationData, TimeSpan.FromMilliseconds(200)),
                "duplicate payload reached the game thread");
            Assert.True(b.Metrics.RxDroppedReplay > 0, "no replays were counted");
        }
        finally
        {
            a.Stop();
            b.Stop();
        }
    }

    [Fact]
    public void Handshake_SurvivesHeavyPacketLoss()
    {
        var (ia, ib) = InMemoryDatagramTransport.Create();
        ia.Bind(Addr("1.1.1.1"));
        ib.Bind(Addr("2.2.2.2"));

        // 35% loss in both directions; the 250ms hedge must carry it.
        var ta = new LossyTransport(ia, seed: 42, dropRate: 0.35);
        var tb = new LossyTransport(ib, seed: 43, dropRate: 0.35);

        using var a = new Network(ta, TanksPeerCrypto.Instance, NullLog.Instance, AppId, AppVer, UmemSlotCount);
        using var b = new Network(tb, TanksPeerCrypto.Instance, NullLog.Instance, AppId, AppVer, UmemSlotCount);

        try
        {
            a.Start();
            b.Start();

            a.Connect(b.LocalPeerId, new IPEndPoint(IPAddress.Parse("2.2.2.2"), 55555));

            Assert.True(TestHelpers.WaitFor(a.Umem.Rx, NetworkEventKind.SessionEstablished, HandshakeTimeout),
                "initiator did not establish under loss");
            Assert.True(TestHelpers.WaitFor(b.Umem.Rx, NetworkEventKind.SessionEstablished, HandshakeTimeout),
                "responder did not establish under loss");
        }
        finally
        {
            a.Stop();
            b.Stop();
        }
    }

    [Fact]
    public void DeadLink_TimesOut_AndDeliversSessionClosed()
    {
        var (ia, ib) = InMemoryDatagramTransport.Create();
        ia.Bind(Addr("1.1.1.1"));
        ib.Bind(Addr("2.2.2.2"));
        var ta = new LossyTransport(ia, seed: 1, dropRate: 0);
        var tb = new LossyTransport(ib, seed: 2, dropRate: 0);

        using var a = new Network(ta, TanksPeerCrypto.Instance, NullLog.Instance, AppId, AppVer, UmemSlotCount);
        using var b = new Network(tb, TanksPeerCrypto.Instance, NullLog.Instance, AppId, AppVer, UmemSlotCount);

        try
        {
            a.Start();
            b.Start();

            a.Connect(b.LocalPeerId, new IPEndPoint(IPAddress.Parse("2.2.2.2"), 55555));
            Assert.True(TestHelpers.WaitFor(a.Umem.Rx, NetworkEventKind.SessionEstablished, HandshakeTimeout));
            Assert.True(TestHelpers.WaitFor(b.Umem.Rx, NetworkEventKind.SessionEstablished, HandshakeTimeout));

            // Kill the link. Both sides must idle-timeout and tell their
            // game thread (SessionClosed survives via the pending-lifecycle
            // machinery and the IsAlive reap gate).
            ta.DropAll = true;
            tb.DropAll = true;

            EndReason reasonA = default;
            Assert.True(TestHelpers.WaitFor(a.Umem.Rx, NetworkEventKind.SessionClosed,
                NetworkConstants.SessionIdleTimeout + TimeSpan.FromSeconds(10),
                (ref NetworkEventMetadata md, byte* payload) =>
                {
                    var msg = (SessionClosed*)payload;
                    reasonA = msg->EndReason;
                }), "A never observed SessionClosed");
            Assert.Equal(EndReason.Timeout, reasonA);

            Assert.True(TestHelpers.WaitFor(b.Umem.Rx, NetworkEventKind.SessionClosed,
                NetworkConstants.SessionIdleTimeout + TimeSpan.FromSeconds(10)),
                "B never observed SessionClosed");
        }
        finally
        {
            a.Stop();
            b.Stop();
        }
    }
}

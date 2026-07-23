using System.Net;
using System.Net.Sockets;
using Tanks.Net;
using Xunit;

namespace Tests;

// The workshop flow, end to end, over REAL loopback UDP: PeerId shared as text,
// endpoint typed as "ip:port", ManualPeerDiscovery feeding Connect. Everything the
// Unity connect screen does except drawing — over NativeTransport (the transport
// the game actually uses), so the P/Invoke recv/send paths get exercised too.
public sealed unsafe class LoopbackSocketTests
{
    private const ushort AppId = 1;
    private const ushort AppVer = 1;
    private static readonly TimeSpan HandshakeTimeout = TimeSpan.FromSeconds(15);

    // Fixed base + walk-forward, same strategy the Unity NetworkHost uses, so
    // parallel test runs (or a stray process) don't fail the bind.
    private static NativeTransport BindLoopback(int basePort, out IPAddress loopback, out int port)
    {
        var transport = new NativeTransport();
        loopback = transport.DualMode ? IPAddress.IPv6Loopback : IPAddress.Loopback;
        for (int candidate = basePort; candidate < basePort + 50; candidate++)
        {
            try
            {
                transport.Bind(NetAddress.FromIPEndPoint(new IPEndPoint(loopback, candidate)));
                port = candidate;
                return transport;
            }
            catch (SocketException ex) when (ex.SocketErrorCode == SocketError.AddressAlreadyInUse)
            {
            }
        }
        transport.Dispose();
        throw new Exception($"no free loopback UDP port in {basePort}..{basePort + 49}");
    }

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
            payload[i] = (byte)(i * 3);
        Assert.True(tx.TryEnqueue(handle));
    }

    [Fact]
    public void WorkshopFlow_TypedPeerIdAndEndPoint_EstablishesOverRealSockets()
    {
        using var ta = BindLoopback(48910, out IPAddress loopback, out int portA);
        using var tb = BindLoopback(portA + 1, out _, out int portB);
        using var a = new Network(ta, TanksPeerCrypto.Instance, NullLog.Instance, AppId, AppVer, 1 << 10);
        using var b = new Network(tb, TanksPeerCrypto.Instance, NullLog.Instance, AppId, AppVer, 1 << 10);

        try
        {
            a.Start();
            b.Start();

            // What the players share out of band: B's id and endpoint, as text.
            string sharedPeerId = b.LocalPeerId.ToVerboseString();
            string sharedEndPoint = loopback.AddressFamily == AddressFamily.InterNetworkV6
                ? $"[{loopback}]:{portB}"
                : $"{loopback}:{portB}";

            // What A's connect screen does with the typed text.
            Assert.True(PeerId.TryParse(sharedPeerId, out PeerId remoteId));
            Assert.True(EndPointParser.TryParse(sharedEndPoint, out IPEndPoint? remoteEndPoint, out string? error));
            Assert.Null(error);

            var discovery = new ManualPeerDiscovery();
            discovery.Set(remoteId, remoteEndPoint!);
            foreach (PeerDiscoveryResult peer in discovery.GetPeers())
                a.Connect(peer.PeerId, peer.EndPoint);

            SessionId sidA = default;
            Assert.True(TestHelpers.WaitFor(a.Umem.Rx, NetworkEventKind.SessionEstablished, HandshakeTimeout,
                (ref NetworkEventMetadata md, byte* _) => sidA = md.SessionId), "initiator did not establish");
            Assert.True(TestHelpers.WaitFor(b.Umem.Rx, NetworkEventKind.SessionEstablished, HandshakeTimeout),
                "responder did not establish");

            // One authenticated payload across the real socket for good measure.
            SendPayload(a.Umem.Tx, sidA, length: 32);
            Assert.True(TestHelpers.WaitFor(b.Umem.Rx, NetworkEventKind.ApplicationData, HandshakeTimeout,
                (ref NetworkEventMetadata md, byte* payload) =>
                {
                    Assert.Equal(32, md.Length);
                    for (int i = 0; i < 32; i++)
                        Assert.Equal((byte)(i * 3), payload[i]);
                }), "payload did not arrive over the socket");

            Assert.Equal(0, a.Metrics.RxDroppedBadMac);
            Assert.Equal(0, b.Metrics.RxDroppedBadMac);
        }
        finally
        {
            a.Stop();
            b.Stop();
        }
    }
}

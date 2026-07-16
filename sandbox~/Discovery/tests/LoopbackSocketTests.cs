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

    private static void SendPayload(in NetworkQueue tx, SessionId sid, int length)
    {
        BufferHandle handle = default;
        Assert.True(tx.Pool.TryRent(ref handle));
        byte* slot = tx.Pool.Deref(handle);
        var metadata = (PipeEventMetadata*)slot;
        metadata->SessionId = sid;
        metadata->Kind = PipeEventKind.ApplicationData;
        metadata->Offset = PacketHeader.Size;
        metadata->Length = (ushort)length;
        byte* payload = slot + NetworkConstants.PipeEventHeadroom + PacketHeader.Size;
        for (int i = 0; i < length; i++)
            payload[i] = (byte)(i * 3);
        Assert.True(tx.Ring.TryEnqueue(handle));
    }

    [Fact]
    public void WorkshopFlow_TypedPeerIdAndEndPoint_EstablishesOverRealSockets()
    {
        using var ta = BindLoopback(48910, out IPAddress loopback, out int portA);
        using var tb = BindLoopback(portA + 1, out _, out int portB);
        using var a = new Network(new NetworkPipeFactory(), ta, TanksPeerCrypto.Instance, NullLog.Instance, AppId, AppVer);
        using var b = new Network(new NetworkPipeFactory(), tb, TanksPeerCrypto.Instance, NullLog.Instance, AppId, AppVer);

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
            Assert.True(TestHelpers.WaitFor(a.Pipe.RX, PipeEventKind.SessionEstablished, HandshakeTimeout,
                (ref PipeEvent evt, byte* _) => sidA = evt.Metadata.SessionId), "initiator did not establish");
            Assert.True(TestHelpers.WaitFor(b.Pipe.RX, PipeEventKind.SessionEstablished, HandshakeTimeout),
                "responder did not establish");

            // One authenticated payload across the real socket for good measure.
            SendPayload(a.Pipe.TX, sidA, length: 32);
            Assert.True(TestHelpers.WaitFor(b.Pipe.RX, PipeEventKind.ApplicationData, HandshakeTimeout,
                (ref PipeEvent evt, byte* payload) =>
                {
                    Assert.Equal(32, evt.Metadata.Length);
                    byte* data = payload + evt.Metadata.Offset;
                    for (int i = 0; i < 32; i++)
                        Assert.Equal((byte)(i * 3), data[i]);
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

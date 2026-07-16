using System.Diagnostics;
using System.Net;
using Tanks.Net;
using Tanks.Sim;
using Xunit;

namespace Tests;

// The whole stack, twice over: two real Networks (crypto handshake, net threads),
// two RemoteState pumps, two RollbackDrivers with different scripted inputs, linked
// by the in-memory transport pair — optionally through packet loss + duplication.
// Wall-clock timing makes rollback counts nondeterministic; the INVARIANTS can't be:
// both sims must agree, hash-for-hash, on every mutually confirmed tick.
public sealed unsafe class NetcodeE2ETests
{
    private const ushort AppId = 1;
    private const ushort AppVer = 1;
    private static readonly TimeSpan HandshakeTimeout = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan MatchTimeout = TimeSpan.FromSeconds(60);

    private sealed class Peer : IDisposable
    {
        public required Network Network;
        public required RemoteState Remote;
        public Session? Session;
        public RollbackDriver? Driver;

        public void Dispose()
        {
            Network.Stop();
            Network.Dispose();
        }
    }

    private static Peer NewPeer(IDatagramTransport transport)
    {
        var network = new Network(new NetworkPipeFactory(), transport, TanksPeerCrypto.Instance, NullLog.Instance, AppId, AppVer);
        var remote = new RemoteState(network, NullLog.Instance);
        var peer = new Peer { Network = network, Remote = remote };
        remote.OnEstablished = session => peer.Session = session;
        network.Start();
        return peer;
    }

    private static void StartDriver(Peer peer, Func<uint, PlayerInput> script)
    {
        var config = new SimConfig();
        int localPlayer = peer.Network.LocalPeerId.CompareTo(peer.Session!.RemotePeerId) < 0 ? 0 : 1;
        peer.Driver = new RollbackDriver(
            config, new Simulation(config), Arena.CreateDefault(config),
            new ScriptedInputSource(script), localPlayer, peer.Session!, peer.Remote, NullLog.Instance);
    }

    private static void RunMatch(IDatagramTransport ta, IDatagramTransport tb, uint targetTicks)
    {
        using Peer a = NewPeer(ta);
        using Peer b = NewPeer(tb);

        a.Network.Connect(b.Network.LocalPeerId, new IPEndPoint(IPAddress.Parse("2.2.2.2"), 55555));

        // Handshake: pump both until each side's session is established.
        var sw = Stopwatch.StartNew();
        while (a.Session is null || b.Session is null)
        {
            Assert.True(sw.Elapsed < HandshakeTimeout, "handshake did not complete");
            a.Remote.Poll();
            b.Remote.Poll();
            Thread.Sleep(1);
        }

        StartDriver(a, Scripts.A);
        StartDriver(b, Scripts.B);
        Assert.NotEqual(a.Driver!.LocalPlayer, b.Driver!.LocalPlayer); // PeerId order must split the slots

        // Both scripts drive whichever sim slot PeerId order assigned them; the
        // reference for cross-checking is each other, not a precomputed run.
        double step = 1.0 / a.Driver.Config.TickRate;
        sw.Restart();
        while (a.Driver.ConfirmedTick < targetTicks || b.Driver.ConfirmedTick < targetTicks)
        {
            Assert.True(sw.Elapsed < MatchTimeout,
                $"match stalled: A conf={a.Driver.ConfirmedTick} cur={a.Driver.CurrentTick} stalls={a.Driver.StalledSteps}, " +
                $"B conf={b.Driver.ConfirmedTick} cur={b.Driver.CurrentTick} stalls={b.Driver.StalledSteps}");

            a.Remote.Poll();
            a.Driver.Advance(step);
            b.Remote.Poll();
            b.Driver.Advance(step);
            Thread.Sleep(1); // let the net threads move packets
        }

        // The one invariant that matters: bit-identical simulations on confirmed ticks.
        Assert.False(a.Driver.DesyncDetected, $"A flagged desync at tick {a.Driver.DesyncTick}");
        Assert.False(b.Driver.DesyncDetected, $"B flagged desync at tick {b.Driver.DesyncTick}");

        uint common = Math.Min(a.Driver.ConfirmedTick, b.Driver.ConfirmedTick);
        uint from = common > 100 ? common - 100 : 1; // stay inside both history rings
        for (uint t = from; t <= common; t++)
        {
            Assert.True(a.Driver.History.TryGet(t, out StateHistorySlot sa), $"A missing history at {t}");
            Assert.True(b.Driver.History.TryGet(t, out StateHistorySlot sb), $"B missing history at {t}");
            Assert.True(sa.Hash == sb.Hash, $"tick {t}: A {sa.Hash:X16} != B {sb.Hash:X16}");
        }
    }

    private static NetAddress Addr(string ip) =>
        NetAddress.FromIPEndPoint(new IPEndPoint(IPAddress.Parse(ip), 55555));

    [Fact]
    public void CleanLink_MatchConvergesTickForTick()
    {
        var (ta, tb) = InMemoryDatagramTransport.Create();
        ta.Bind(Addr("1.1.1.1"));
        tb.Bind(Addr("2.2.2.2"));
        RunMatch(ta, tb, targetTicks: 600);
    }

    [Fact]
    public void LossyDuplicatedLink_MatchStillConverges()
    {
        var (ia, ib) = InMemoryDatagramTransport.Create();
        ia.Bind(Addr("1.1.1.1"));
        ib.Bind(Addr("2.2.2.2"));
        var ta = new LossyTransport(ia, seed: 11, dropRate: 0.25) { DuplicateAll = true };
        var tb = new LossyTransport(ib, seed: 12, dropRate: 0.25) { DuplicateAll = true };
        RunMatch(ta, tb, targetTicks: 400);
    }
}

// Copied from the Protocol test support (test projects are per-library): drops
// outbound datagrams at a seeded rate; DuplicateAll sends everything twice.
public sealed unsafe class LossyTransport : IDatagramTransport
{
    private readonly IDatagramTransport _inner;
    private readonly Random _rng;
    private readonly double _dropRate;

    public volatile bool DropAll;
    public volatile bool DuplicateAll;

    public LossyTransport(IDatagramTransport inner, int seed, double dropRate)
    {
        _inner = inner;
        _rng = new Random(seed);
        _dropRate = dropRate;
    }

    public ref readonly NetAddress LocalEndPoint => ref _inner.LocalEndPoint;
    public void Bind(in NetAddress local) => _inner.Bind(local);
    public void Close() => _inner.Close();
    public bool PollRead(int microseconds) => _inner.PollRead(microseconds);

    public bool TryReceive(byte* buffer, int length, out int bytesRead, ref NetAddress source)
        => _inner.TryReceive(buffer, length, out bytesRead, ref source);

    public bool TrySend(byte* datagram, int length, ref NetAddress destination)
    {
        if (DropAll)
            return true; // swallowed; pretend the send succeeded
        if (_rng.NextDouble() < _dropRate)
            return true;
        if (DuplicateAll)
            _inner.TrySend(datagram, length, ref destination);
        return _inner.TrySend(datagram, length, ref destination);
    }
}

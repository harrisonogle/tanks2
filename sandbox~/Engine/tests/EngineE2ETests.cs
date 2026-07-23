using System.Diagnostics;
using Tanks;
using Tanks.Net;
using Tanks.Sim;
using Xunit;

namespace Tanks.Engine;

// The whole stack, twice over: two Networks (crypto handshake, net threads), two
// Engines (sim threads with the rollback netcode), linked by the in-memory transport
// pair, driven exactly the way Unity drives them — through EngineBus over the render
// rings. The test thread plays the role of both render threads.
//
// Wall-clock pacing makes tick/rollback counts nondeterministic; the INVARIANT can't
// be: both sims must agree, hash-for-hash, on every mutually confirmed tick — which
// the engines verify against each other via the StateHash stream (DESYNC log = fail).
public sealed unsafe class EngineE2ETests
{
    private const ushort AppId = 1;
    private const ushort AppVer = 1;

    // Captures errors so desync (or any engine failure) fails the test loudly.
    private sealed class CapturingLog : ILog
    {
        public readonly List<string> Errors = new();
        public bool IsEnabled(LogLevel level) => level >= LogLevel.Error;
        public void Log(ref LogMessage message)
        {
            if (message.Level >= LogLevel.Error && message.State is string s)
                lock (Errors) Errors.Add(s);
            message.Return();
        }
        public void LogTrace(string? message) { }
        public void LogDebug(string? message) { }
        public void LogInformation(string? message) { }
        public void LogWarning(string? message) { }
        public void LogError(string? message) { if (message != null) lock (Errors) Errors.Add(message); }
    }

    private static PlayerInput ScriptA(uint frame)
    {
        InputButtons b = InputButtons.None;
        if (frame % 4 < 2) b |= InputButtons.Forward;
        if (frame % 3 == 0) b |= InputButtons.Left;
        if (frame % 16 < 8) b |= InputButtons.Fire;   // levels: hold, don't pulse
        if (frame % 64 < 4) b |= InputButtons.Dash;   // held-then-released; engine derives the edge
        return new PlayerInput(b, (int)(frame * 5));
    }

    private static PlayerInput ScriptB(uint frame)
    {
        InputButtons b = InputButtons.None;
        if (frame % 5 < 3) b |= InputButtons.Forward;
        if (frame % 3 == 1) b |= InputButtons.Right;
        if (frame % 20 < 6) b |= InputButtons.Fire;
        if (frame % 48 < 3) b |= InputButtons.Dash;
        return new PlayerInput(b, (int)(frame * 11));
    }

    [Fact]
    public void OnlineMatch_TwoEngines_ConvergeWithoutDesync()
    {
        var config = new SimConfig();
        var logA = new CapturingLog();
        var logB = new CapturingLog();

        var (ta, tb) = InMemoryDatagramTransport.Create();
        ta.Bind(NetAddress.FromIPEndPoint(new System.Net.IPEndPoint(System.Net.IPAddress.Parse("1.1.1.1"), 55555)));
        tb.Bind(NetAddress.FromIPEndPoint(new System.Net.IPEndPoint(System.Net.IPAddress.Parse("2.2.2.2"), 55555)));

        using var na = new Network(ta, TanksPeerCrypto.Instance, logA, AppId, AppVer, 1 << 10);
        using var nb = new Network(tb, TanksPeerCrypto.Instance, logB, AppId, AppVer, 1 << 10);
        using var ea = new Engine(na, logA, config);
        using var eb = new Engine(nb, logB, config);

        na.Start();
        nb.Start();
        ea.Start();
        eb.Start();

        var busA = new EngineBus(ea.Umem, logA, config);
        var busB = new EngineBus(eb.Umem, logB, config);

        try
        {
            // The manual-discovery flow: A dials B.
            busA.StartMatch(nb.LocalPeerId, NetAddress.FromIPEndPoint(new System.Net.IPEndPoint(System.Net.IPAddress.Parse("2.2.2.2"), 55555)));

            // Pump both "render threads" until both are in a match.
            var sw = Stopwatch.StartNew();
            while (busA.Phase != PhaseState.GameView || busB.Phase != PhaseState.GameView)
            {
                Assert.True(sw.Elapsed < TimeSpan.FromSeconds(15), $"match did not start: A={busA.Phase} B={busB.Phase}");
                busA.Pump();
                busB.Pump();
                Thread.Sleep(1);
            }

            // Play: sample scripted LEVEL inputs at ~render rate, watch ticks advance.
            const uint TargetTick = 300; // ~5s of simulated time at 60Hz
            uint frame = 0;
            uint tickA = 0, tickB = 0;
            sw.Restart();
            while (tickA < TargetTick || tickB < TargetTick)
            {
                Assert.True(sw.Elapsed < TimeSpan.FromSeconds(30),
                    $"match stalled: A tick={tickA} B tick={tickB} (errors A: {string.Join(" | ", logA.Errors)} B: {string.Join(" | ", logB.Errors)})");

                frame++;
                busA.SendInput(0, ScriptA(frame));
                busB.SendInput(0, ScriptB(frame));
                busA.Pump();
                busB.Pump();

                if (busA.TryGetGameState(out GameStateView va)) tickA = va.Header->Tick;
                if (busB.TryGetGameState(out GameStateView vb)) tickB = vb.Header->Tick;

                Thread.Sleep(15); // ~render cadence
            }

            // The engines cross-check hashes on every confirmed tick; any mismatch logs
            // "DESYNC ..." at Error level. No errors of any kind are acceptable here.
            Assert.True(logA.Errors.Count == 0, $"engine A errors: {string.Join(" | ", logA.Errors)}");
            Assert.True(logB.Errors.Count == 0, $"engine B errors: {string.Join(" | ", logB.Errors)}");
        }
        finally
        {
            ea.Stop();
            eb.Stop();
            na.Stop();
            nb.Stop();
        }
    }

    [Fact]
    public void OnlineMatch_LossyDuplicatedLink_StillConverges()
    {
        var config = new SimConfig();
        var logA = new CapturingLog();
        var logB = new CapturingLog();

        var (ia, ib) = InMemoryDatagramTransport.Create();
        ia.Bind(NetAddress.FromIPEndPoint(new System.Net.IPEndPoint(System.Net.IPAddress.Parse("1.1.1.1"), 55555)));
        ib.Bind(NetAddress.FromIPEndPoint(new System.Net.IPEndPoint(System.Net.IPAddress.Parse("2.2.2.2"), 55555)));
        var ta = new LossyTransport(ia, seed: 11, dropRate: 0.25) { DuplicateAll = true };
        var tb = new LossyTransport(ib, seed: 12, dropRate: 0.25) { DuplicateAll = true };

        using var na = new Network(ta, TanksPeerCrypto.Instance, logA, AppId, AppVer, 1 << 10);
        using var nb = new Network(tb, TanksPeerCrypto.Instance, logB, AppId, AppVer, 1 << 10);
        using var ea = new Engine(na, logA, config);
        using var eb = new Engine(nb, logB, config);
        na.Start(); nb.Start(); ea.Start(); eb.Start();
        var busA = new EngineBus(ea.Umem, logA, config);
        var busB = new EngineBus(eb.Umem, logB, config);

        try
        {
            busA.StartMatch(nb.LocalPeerId, NetAddress.FromIPEndPoint(new System.Net.IPEndPoint(System.Net.IPAddress.Parse("2.2.2.2"), 55555)));

            var sw = Stopwatch.StartNew();
            while (busA.Phase != PhaseState.GameView || busB.Phase != PhaseState.GameView)
            {
                Assert.True(sw.Elapsed < TimeSpan.FromSeconds(20), $"match did not start under loss: A={busA.Phase} B={busB.Phase}");
                busA.Pump(); busB.Pump();
                Thread.Sleep(1);
            }

            const uint TargetTick = 200;
            uint frame = 0, tickA = 0, tickB = 0;
            sw.Restart();
            while (tickA < TargetTick || tickB < TargetTick)
            {
                Assert.True(sw.Elapsed < TimeSpan.FromSeconds(30),
                    $"lossy match stalled: A={tickA} B={tickB} (A errs: {string.Join(" | ", logA.Errors)} B errs: {string.Join(" | ", logB.Errors)})");
                frame++;
                busA.SendInput(0, ScriptA(frame));
                busB.SendInput(0, ScriptB(frame));
                busA.Pump(); busB.Pump();
                if (busA.TryGetGameState(out GameStateView va)) tickA = va.Header->Tick;
                if (busB.TryGetGameState(out GameStateView vb)) tickB = vb.Header->Tick;
                Thread.Sleep(15);
            }

            Assert.True(logA.Errors.Count == 0, $"engine A errors: {string.Join(" | ", logA.Errors)}");
            Assert.True(logB.Errors.Count == 0, $"engine B errors: {string.Join(" | ", logB.Errors)}");
        }
        finally
        {
            ea.Stop(); eb.Stop(); na.Stop(); nb.Stop();
        }
    }

    [Fact]
    public void LocalMatch_CouchCoop_TicksAdvance()
    {
        var config = new SimConfig();
        var log = new CapturingLog();

        var (ta, _) = InMemoryDatagramTransport.Create();
        ta.Bind(NetAddress.FromIPEndPoint(new System.Net.IPEndPoint(System.Net.IPAddress.Parse("1.1.1.1"), 55555)));

        using var network = new Network(ta, TanksPeerCrypto.Instance, log, AppId, AppVer, 1 << 10);
        using var engine = new Engine(network, log, config);
        network.Start();
        engine.Start();
        var bus = new EngineBus(engine.Umem, log, config);

        try
        {
            bus.StartLocalMatch();

            var sw = Stopwatch.StartNew();
            while (bus.Phase != PhaseState.GameView)
            {
                Assert.True(sw.Elapsed < TimeSpan.FromSeconds(5), "local match did not start");
                bus.Pump();
                Thread.Sleep(1);
            }

            uint frame = 0;
            uint tick = 0;
            sw.Restart();
            while (tick < 120) // ~2s at 60Hz
            {
                Assert.True(sw.Elapsed < TimeSpan.FromSeconds(10), $"local match stalled at tick {tick}");
                frame++;
                bus.SendInput(ScriptA(frame), ScriptB(frame));
                bus.Pump();
                if (bus.TryGetGameState(out GameStateView view)) tick = view.Header->Tick;
                Thread.Sleep(15);
            }

            Assert.True(log.Errors.Count == 0, $"engine errors: {string.Join(" | ", log.Errors)}");
        }
        finally
        {
            engine.Stop();
            network.Stop();
        }
    }
}

// Drops outbound datagrams at a seeded rate; DuplicateAll sends everything twice.
sealed unsafe class LossyTransport : IDatagramTransport
{
    private readonly IDatagramTransport _inner;
    private readonly Random _rng;
    private readonly double _dropRate;
    public bool DuplicateAll;

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
        if (_rng.NextDouble() < _dropRate) return true;
        if (DuplicateAll) _inner.TrySend(datagram, length, ref destination);
        return _inner.TrySend(datagram, length, ref destination);
    }
}

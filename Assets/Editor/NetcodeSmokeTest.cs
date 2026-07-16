using System;
using System.Diagnostics;
using System.Net;
using System.Threading;
using Tanks.Net;
using Tanks.Sim;
using UnityEditor;

namespace Tanks.Editor
{
    /// <summary>
    /// Headless netcode smoke test on UNITY'S runtime (Mono): two full peers — real
    /// loopback UDP via NativeTransport, crypto handshake, RemoteState pumps, and
    /// RollbackDrivers fed by scripted inputs — play a match to 600 confirmed ticks.
    /// Passes only if both sims stayed hash-identical on every mutually confirmed tick.
    ///
    /// Run (exit code 0 = pass, 1 = fail; grep the log for "NETCODE SMOKE"):
    ///   Unity -batchmode -nographics -projectPath "&lt;repo&gt;" \
    ///     -executeMethod Tanks.Editor.NetcodeSmokeTest.Run -logFile "&lt;log&gt;"
    /// </summary>
    public static class NetcodeSmokeTest
    {
        private const uint TargetTicks = 600;
        private static readonly TimeSpan HandshakeTimeout = TimeSpan.FromSeconds(15);
        private static readonly TimeSpan MatchTimeout = TimeSpan.FromSeconds(60);

        public static void Run()
        {
            try
            {
                RunMatch();
                UnityEngine.Debug.Log("NETCODE SMOKE PASSED");
                EditorApplication.Exit(0);
            }
            catch (Exception ex)
            {
                UnityEngine.Debug.LogError($"NETCODE SMOKE FAILED: {ex}");
                EditorApplication.Exit(1);
            }
        }

        private sealed class Peer
        {
            public Network Network;
            public RemoteState Remote;
            public Session Session;
            public RollbackDriver Driver;
        }

        private static void RunMatch()
        {
            var ta = new NativeTransport();
            var tb = new NativeTransport();
            IPAddress loopback = ta.DualMode ? IPAddress.IPv6Loopback : IPAddress.Loopback;
            int portA = Bind(ta, loopback, 48980);
            int portB = Bind(tb, loopback, portA + 1);

            using (ta)
            using (tb)
            using (var na = new Network(new NetworkPipeFactory(), ta, TanksPeerCrypto.Instance, NullLogger.Instance, 1, 1))
            using (var nb = new Network(new NetworkPipeFactory(), tb, TanksPeerCrypto.Instance, NullLogger.Instance, 1, 1))
            {
                try
                {
                    var a = NewPeer(na);
                    var b = NewPeer(nb);

                    na.Connect(nb.LocalPeerId, new IPEndPoint(loopback, portB));

                    var sw = Stopwatch.StartNew();
                    while (a.Session == null || b.Session == null)
                    {
                        if (sw.Elapsed > HandshakeTimeout) throw new Exception("handshake did not complete");
                        a.Remote.Poll();
                        b.Remote.Poll();
                        Thread.Sleep(1);
                    }

                    StartDriver(a, ScriptA);
                    StartDriver(b, ScriptB);
                    if (a.Driver.LocalPlayer == b.Driver.LocalPlayer)
                        throw new Exception("PeerId order failed to split the player slots");

                    double step = 1.0 / a.Driver.Config.TickRate;
                    sw.Restart();
                    while (a.Driver.ConfirmedTick < TargetTicks || b.Driver.ConfirmedTick < TargetTicks)
                    {
                        if (sw.Elapsed > MatchTimeout)
                            throw new Exception($"match stalled: A conf={a.Driver.ConfirmedTick}, B conf={b.Driver.ConfirmedTick}");
                        a.Remote.Poll();
                        a.Driver.Advance(step);
                        b.Remote.Poll();
                        b.Driver.Advance(step);
                        Thread.Sleep(1);
                    }

                    if (a.Driver.DesyncDetected) throw new Exception($"A flagged desync at tick {a.Driver.DesyncTick}");
                    if (b.Driver.DesyncDetected) throw new Exception($"B flagged desync at tick {b.Driver.DesyncTick}");

                    uint common = Math.Min(a.Driver.ConfirmedTick, b.Driver.ConfirmedTick);
                    uint from = common > 100 ? common - 100 : 1;
                    for (uint t = from; t <= common; t++)
                    {
                        if (!a.Driver.History.TryGet(t, out StateHistorySlot sa)) throw new Exception($"A missing history at {t}");
                        if (!b.Driver.History.TryGet(t, out StateHistorySlot sb)) throw new Exception($"B missing history at {t}");
                        if (sa.Hash != sb.Hash) throw new Exception($"tick {t}: A {sa.Hash:X16} != B {sb.Hash:X16}");
                    }

                    UnityEngine.Debug.Log(
                        $"[netcode smoke] match ran to A={a.Driver.CurrentTick}/B={b.Driver.CurrentTick} ticks; " +
                        $"rollbacks A={a.Driver.RollbackCount} B={b.Driver.RollbackCount}; " +
                        $"stalls A={a.Driver.StalledSteps} B={b.Driver.StalledSteps}; hashes agree on ticks {from}..{common}");
                }
                finally
                {
                    na.Stop();
                    nb.Stop();
                }
            }
        }

        private static Peer NewPeer(Network network)
        {
            var peer = new Peer { Network = network, Remote = new RemoteState(network, NullLogger.Instance) };
            peer.Remote.OnEstablished = session => peer.Session = session;
            network.Start();
            return peer;
        }

        private static void StartDriver(Peer peer, Func<uint, PlayerInput> script)
        {
            var config = new SimConfig();
            int localPlayer = peer.Network.LocalPeerId.CompareTo(peer.Session.RemotePeerId) < 0 ? 0 : 1;
            peer.Driver = new RollbackDriver(
                config, new Simulation(config), Arena.CreateDefault(config),
                new ScriptedSource(script), localPlayer, peer.Session, peer.Remote, NullLogger.Instance);
        }

        private static int Bind(NativeTransport transport, IPAddress address, int basePort)
        {
            for (int port = basePort; port < basePort + 50; port++)
            {
                try
                {
                    transport.Bind(NetAddress.FromIPEndPoint(new IPEndPoint(address, port)));
                    return port;
                }
                catch (System.Net.Sockets.SocketException ex) when (ex.SocketErrorCode == System.Net.Sockets.SocketError.AddressAlreadyInUse)
                {
                }
            }
            throw new Exception($"no free loopback UDP port in {basePort}..{basePort + 49}");
        }

        private static PlayerInput ScriptA(uint tick)
        {
            InputButtons btn = InputButtons.None;
            if (tick % 4 == 0) btn |= InputButtons.Forward;
            if (tick % 3 == 0) btn |= InputButtons.Left;
            if (tick % 11 == 0) btn |= InputButtons.Fire;
            return new PlayerInput(btn, (int)(tick * 5));
        }

        private static PlayerInput ScriptB(uint tick)
        {
            InputButtons btn = InputButtons.None;
            if (tick % 5 == 0) btn |= InputButtons.Forward;
            if (tick % 3 == 0) btn |= InputButtons.Right;
            if (tick % 13 == 0) btn |= InputButtons.Fire;
            return new PlayerInput(btn, (int)(tick * 11));
        }

        private sealed class ScriptedSource : IInputSource
        {
            private readonly Func<uint, PlayerInput> _script;
            private uint _tick;
            public ScriptedSource(Func<uint, PlayerInput> script) => _script = script;
            public PlayerInput Sample() => _script(++_tick);
            public void Reset() => _tick = 0;
        }

        private sealed class NullLogger : ILog
        {
            public static readonly NullLogger Instance = new NullLogger();
            public bool IsEnabled(LogLevel level) => false;
            public void Log(ref LogMessage message) => message.Return();
            public void LogTrace(string message) { }
            public void LogDebug(string message) { }
            public void LogInformation(string message) { }
            public void LogWarning(string message) { }
            public void LogError(string message) { }
        }
    }
}

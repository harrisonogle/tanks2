using Tanks.Net;
using Tanks.Sim;
using UnityEngine;

namespace Tanks.Game
{
    /// <summary>
    /// Entry point — think of <see cref="Boot"/> as <c>main()</c>.
    ///
    /// Everything in the running game is created here from code: camera, light, and the
    /// single "Tanks" object that carries the simulation runner, the renderer, and the HUD.
    /// There is intentionally NO hand-authored scene content, so there's nothing to wire up
    /// in the Editor — just press Play in any (even empty) scene and this runs.
    /// </summary>
    public static class Bootstrap
    {
        // Both peers default to the same port; NetworkHost walks forward if it's taken
        // (e.g. Editor + standalone build on one machine).
        private const int DefaultPort = 47777;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        private static void Boot()
        {
            var config = new SimConfig();
            float w = config.ArenaWidth.ToFloat();
            float h = config.ArenaHeight.ToFloat();

            // Top-down orthographic camera looking straight down the world -Y axis.
            var camGO = new GameObject("Tanks Camera");
            var cam = camGO.AddComponent<Camera>();
            cam.orthographic = true;
            cam.orthographicSize = h / 2f + 1f;
            cam.clearFlags = CameraClearFlags.SolidColor;
            cam.backgroundColor = new Color(0.08f, 0.09f, 0.11f);
            camGO.transform.position = new Vector3(w / 2f, 30f, h / 2f);
            camGO.transform.rotation = Quaternion.Euler(90f, 0f, 0f);
            camGO.tag = "MainCamera";

            // A single directional light + ambient fill so the primitives aren't black.
            var lightGO = new GameObject("Tanks Light");
            var light = lightGO.AddComponent<Light>();
            light.type = LightType.Directional;
            light.intensity = 1.0f;
            lightGO.transform.rotation = Quaternion.Euler(55f, -30f, 0f);
            RenderSettings.ambientLight = new Color(0.45f, 0.45f, 0.5f);

            // The game object: simulation runner (shell) + view + HUD all live here.
            var root = new GameObject("Tanks");
            var runner = root.AddComponent<SimRunner>();
            var gameView = root.AddComponent<GameView>();
            var debugHud = root.AddComponent<DebugHud>();
            
            // Protocol service: UDP socket + Network poll thread, alive for the app's
            // lifetime (a protocol is a service that's always running, not per-match).
            var host = root.AddComponent<NetworkHost>();
            host.Initialize(DefaultPort, new UnityLog(LogLevel.Debug));

            // Game-thread pump for the protocol pipe. SimRunner polls it every frame;
            // its lifecycle callbacks below are what start and end networked matches.
            var remote = new RemoteState(host.Network, new UnityLog(LogLevel.Debug));
            runner.Remote = remote;

            // Discovery shim: the connect screen *is* the discovery protocol for now —
            // a human carries (PeerId, endpoint) between machines. Real discovery slots
            // in behind IPeerDiscovery later without the consumer changing.
            var discovery = new ManualPeerDiscovery();

            // Pre-match screen. The match doesn't start until the player picks a mode.
            var screen = root.AddComponent<ConnectScreen>();
            screen.LocalPeerId = host.Network.LocalPeerId.ToVerboseString();
            screen.LocalPort = host.BoundPort;
            screen.PlayLocalRequested = () =>
            {
                runner.StartMatch(BuildCouchCoopDriver(config));
            };
            screen.ConnectRequested = (peerId, endPoint) =>
            {
                discovery.Set(peerId, endPoint);
                foreach (PeerDiscoveryResult peer in discovery.GetPeers())
                    host.Network.Connect(peer.PeerId, peer.EndPoint);
                screen.SetStatus("Connecting… (handshake in flight; [Net] logs in the Console)");
            };

            remote.OnEstablished = session =>
            {
                // Same deterministic rule as the protocol's simultaneous-open tiebreak:
                // lower PeerId is player 0. The local player always plays with the
                // primary (player-0) bindings, whichever sim slot they control.
                int localPlayer = host.Network.LocalPeerId.CompareTo(session.RemotePeerId) < 0 ? 0 : 1;
                var driver = new RollbackDriver(
                    config, new Simulation(config), Arena.CreateDefault(config),
                    new UnityInputSource(0, config), localPlayer, session, remote,
                    new UnityLog(LogLevel.Debug));
                Debug.Log($"Session established with {session.RemotePeerId}; local player = P{localPlayer + 1}.");
                screen.Hide();
                runner.StartMatch(driver);
            };
            remote.OnClosed = session =>
            {
                runner.EndMatch();
                screen.Show($"Disconnected (peer: {session.PeerReason}, end: {session.EndReason}).");
            };
        }

        /// <summary>Both players sampled locally — the offline mode, and the pre-netcode default.</summary>
        private static SimDriver BuildCouchCoopDriver(SimConfig config)
        {
            var sources = new IInputSource[]
            {
                new UnityInputSource(0, config),
                new UnityInputSource(1, config),
            };
            return new SimDriver(config, new Simulation(config), Arena.CreateDefault(config), sources);
        }
    }
}

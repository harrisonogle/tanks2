using Tanks.Net;
using Tanks.Sim;
using UnityEngine;

namespace Tanks.Game
{
    /// <summary>
    /// Entry point — think of <see cref="Boot"/> as <c>main()</c>.
    ///
    /// Builds the M1 two-peer rig entirely from code: one shared in-process network (with a
    /// pump driving its clock) and two peers. Each peer owns one player, one SimRunner, one
    /// GameView on its own layer, one DebugHud, and one transport endpoint; two viewport
    /// cameras render the peers side by side. There is intentionally NO hand-authored scene
    /// content — press Play in any (even empty) scene and this runs.
    ///
    /// This is the shape every later milestone wants: for real UDP (M5), swap the endpoint
    /// passed to <see cref="CreatePeer"/> for a UdpTransport and nothing else changes.
    /// </summary>
    public static class Bootstrap
    {
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        private static void Boot()
        {
            // One directional light + ambient fill, shared (lights hit all layers by default).
            var lightGO = new GameObject("Tanks Light");
            var light = lightGO.AddComponent<Light>();
            light.type = LightType.Directional;
            light.intensity = 1.0f;
            lightGO.transform.rotation = Quaternion.Euler(55f, -30f, 0f);
            RenderSettings.ambientLight = new Color(0.45f, 0.45f, 0.5f);

            // The shared fake network, the pump that drives its delivery clock, and the
            // slider HUD for degrading the wire live.
            var network = new InProcessNetwork(latencyTicks: 2);
            var netGO = new GameObject("Tanks Network");
            netGO.AddComponent<NetworkPump>().Network = network;
            netGO.AddComponent<NetworkHud>().Network = network;

            // Peer 0 owns P1 on the left half; peer 1 owns P2 on the right half.
            CreatePeer(0, network.EndpointA, new Rect(0f, 0f, 0.5f, 1f), layer: 8);
            CreatePeer(1, network.EndpointB, new Rect(0.5f, 0f, 0.5f, 1f), layer: 9);
        }

        private static void CreatePeer(int player, ITransport transport, Rect viewport, int layer)
        {
            float w = SimConfig.ArenaWidth.ToFloat();
            float h = SimConfig.ArenaHeight.ToFloat();

            // Top-down orthographic camera over this peer's half of the screen, culled to
            // this peer's layer so it doesn't see the other peer's copy of the world.
            var camGO = new GameObject($"Peer{player} Camera");
            var cam = camGO.AddComponent<Camera>();
            cam.orthographic = true;
            cam.rect = viewport;          // set BEFORE reading cam.aspect — the rect changes it
            cam.cullingMask = 1 << layer; // unnamed layers 8/9 work fine from code
            // Fit by whichever dimension binds: the half-width viewport halves the aspect
            // ratio, so sizing by height alone would crop the arena left/right.
            cam.orthographicSize = Mathf.Max(h / 2f + 1f, (w / 2f + 1f) / cam.aspect);
            cam.clearFlags = CameraClearFlags.SolidColor;
            cam.backgroundColor = new Color(0.08f, 0.09f, 0.11f);
            camGO.transform.position = new Vector3(w / 2f, 30f, h / 2f);
            camGO.transform.rotation = Quaternion.Euler(90f, 0f, 0f);

            // The peer object: simulation + view + HUD. AddComponent runs Awake immediately;
            // the fields we set right after land before Start/Update ever run.
            var peerGO = new GameObject($"Peer{player}");
            var runner = peerGO.AddComponent<SimRunner>();
            runner.LocalPlayer = player;
            runner.Transport = transport;
            peerGO.AddComponent<GameView>().Layer = layer;
            peerGO.AddComponent<DebugHud>().Side = player;
        }
    }
}

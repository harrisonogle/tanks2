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
            root.AddComponent<GameView>();
            root.AddComponent<DebugHud>();

            // Composition root: build the pure netcode driver by hand and inject it. Couch-coop
            // for now — both players sampled locally. The session swaps in transport + discovery
            // and reduces each peer to one local source plus remote input fed over the wire.
            var sources = new IInputSource[]
            {
                new UnityInputSource(0, config),
                new UnityInputSource(1, config),
            };
            var driver = new SimDriver(config, new Simulation(config), Arena.CreateDefault(config), sources, historyCapacity: 256);
            runner.Driver = driver;
        }
    }
}

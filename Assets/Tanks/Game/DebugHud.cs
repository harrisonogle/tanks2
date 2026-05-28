using Tanks.Sim;
using UnityEngine;

namespace Tanks.Game
{
    /// <summary>
    /// Crude IMGUI overlay (zero setup). Shows the live tick, the deterministic state hash,
    /// frame rate, per-player status, and controls.
    ///
    /// The state-hash readout is the seed of the "make netcode visible" idea: once two peers
    /// are connected, show BOTH hashes side by side — they must match every confirmed tick,
    /// and the moment they don't, you've caught a desync.
    /// </summary>
    public sealed class DebugHud : MonoBehaviour
    {
        private SimRunner _runner;
        private bool _visible;   // default false (hidden); H or gamepad Select toggles

        private void Start() => _runner = GetComponent<SimRunner>();

        private void Update()
        {
            if (InputSampler.IsHudToggleRequested())
                _visible = !_visible;
        }

        private void OnGUI()
        {
            if (!_visible) return;
            var state = _runner != null ? _runner.State : null;
            if (state == null) return;

            GUILayout.BeginArea(new Rect(10, 10, 440, 320), GUI.skin.box);

            GUILayout.Label("TANKS - local sandbox (no netcode yet)");
            GUILayout.Space(4);
            GUILayout.Label($"Tick:       {state.Tick}");
            GUILayout.Label($"State hash: {(_runner.LastHash & 0xFFFFFFFFUL):X8}");
            GUILayout.Label($"FPS:        {(Time.smoothDeltaTime > 0f ? 1f / Time.smoothDeltaTime : 0f):0}");

            GUILayout.Space(4);
            for (int i = 0; i < SimConfig.PlayerCount; i++)
            {
                ref readonly Tank t = ref state.Tanks[i];
                int bodyDeg = (t.Angle * 360) / Trig.AngleCount;
                int turretDeg = (t.TurretAngle * 360) / Trig.AngleCount;
                string dash =
                    t.DashTicks > 0 ? "ACTIVE" :
                    t.DashCooldown > 0 ? $"cd {t.DashCooldown / (float)SimConfig.TickRate:0.0}s" :
                    "ready";
                GUILayout.Label($"P{i + 1}: {(t.Alive ? "alive" : "DEAD ")}  body={bodyDeg,4}°  turret={turretDeg,4}°  dash={dash,-7}");
            }

            int aliveCount = state.CountAlive();
            if (aliveCount <= 1)
            {
                string msg = aliveCount == 0 ? "Draw!" : $"P{WinnerIndex(state) + 1} wins!";
                GUILayout.Space(2);
                GUILayout.Label($">> {msg}  (press R to reset)");
            }

            GUILayout.Space(8);
            GUILayout.Label("P1 (blue): WASD move, Q/E turret, Space fire, LShift dash");
            GUILayout.Label("P2 (red):  Arrows move, , / . turret, Enter fire, RShift dash");
            GUILayout.Label("R (keyboard) or Start/Options (gamepad): reset match");
            GUILayout.Label("H (keyboard) or Select (gamepad): hide this HUD");

            GUILayout.Space(8);
            GUILayout.Label("Netcode seam ready in SimRunner (local only for now).");

            GUILayout.EndArea();
        }

        private static int WinnerIndex(GameState state)
        {
            for (int i = 0; i < state.Tanks.Length; i++)
                if (state.Tanks[i].Alive) return i;
            return -1;
        }
    }
}

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
        /// <summary>0 = left half of the screen, 1 = right half. Set by Bootstrap (one HUD per peer);
        /// IMGUI is screen-global (it ignores cameras and layers), so the two panels must be
        /// placed apart by hand.</summary>
        public int Side;

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

            bool desync = !_runner.HashesAgree;
            if (desync) GUI.color = new Color(1f, 0.3f, 0.3f); // the on-screen desync alarm

            float x = Side == 0 ? 10f : Screen.width / 2f + 10f;
            GUILayout.BeginArea(new Rect(x, 10, 440, 360), GUI.skin.box);

            GUILayout.Label(desync
                ? $"TANKS — PEER {Side} owns P{_runner.LocalPlayer + 1}   >>> DESYNC (see console) <<<"
                : $"TANKS — PEER {Side} owns P{_runner.LocalPlayer + 1}   (lockstep, delay {SimRunner.InputDelay})");
            GUILayout.Space(4);
            GUILayout.Label($"Tick:      {state.Tick}   stalls: {_runner.Stalls}");
            GUILayout.Label($"My hash:   {_runner.LastHash:X16}");
            // The peer's report is for ITS latest confirmed tick — usually a few ticks off
            // ours, so the two lines rarely show the same value. The real comparison happens
            // tick-aligned against history inside SimRunner; this just surfaces the verdict.
            GUILayout.Label($"Peer hash: {_runner.PeerHash:X16} @tick {_runner.PeerHashTick}  {(desync ? "MISMATCH" : "ok")}");
            GUILayout.Label($"FPS:       {(Time.smoothDeltaTime > 0f ? 1f / Time.smoothDeltaTime : 0f):0}");
            GUILayout.Label($"rx:        {_runner.PacketsReceived} pkts  (last: tick {_runner.LastRxTick}, {_runner.LastRxInput})");

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

            GUILayout.EndArea();
            GUI.color = Color.white; // don't leak the alarm tint into other OnGUI draws
        }

        private static int WinnerIndex(GameState state)
        {
            for (int i = 0; i < state.Tanks.Length; i++)
                if (state.Tanks[i].Alive) return i;
            return -1;
        }
    }
}

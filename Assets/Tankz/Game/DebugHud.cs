using Tankz.Sim;
using UnityEngine;

namespace Tankz.Game
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

        private void Start() => _runner = GetComponent<SimRunner>();

        private void OnGUI()
        {
            var state = _runner != null ? _runner.State : null;
            if (state == null) return;

            GUILayout.BeginArea(new Rect(10, 10, 360, 300), GUI.skin.box);

            GUILayout.Label("TANKZ - local sandbox (no netcode yet)");
            GUILayout.Space(4);
            GUILayout.Label($"Tick:       {state.Tick}");
            GUILayout.Label($"State hash: {(_runner.LastHash & 0xFFFFFFFFUL):X8}");
            GUILayout.Label($"FPS:        {(Time.smoothDeltaTime > 0f ? 1f / Time.smoothDeltaTime : 0f):0}");

            GUILayout.Space(4);
            for (int i = 0; i < SimConfig.PlayerCount; i++)
            {
                bool alive = state.Tanks[i].Alive;
                GUILayout.Label($"P{i + 1}: {(alive ? "alive" : "DEAD ")}  input={_runner.InputOf(i).Buttons}");
            }

            int aliveCount = state.CountAlive();
            if (aliveCount <= 1)
            {
                string msg = aliveCount == 0 ? "Draw!" : $"P{WinnerIndex(state) + 1} wins!";
                GUILayout.Space(2);
                GUILayout.Label($">> {msg}  (press R to reset)");
            }

            GUILayout.Space(8);
            GUILayout.Label("P1 (blue): W/A/S/D move+turn, Space fire");
            GUILayout.Label("P2 (red):  Arrows move+turn, Enter fire");
            GUILayout.Label("R: reset match");

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

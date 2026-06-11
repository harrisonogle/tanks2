using Tanks.Net;
using UnityEngine;

namespace Tanks.Game
{
    /// <summary>
    /// Live knobs for the shared fake network — latency / jitter / loss as on-screen sliders,
    /// so you can degrade the wire mid-match and watch lockstep stall (and, in M3, watch
    /// rollback absorb it) without restarting. Sits at the bottom-center, clear of the two
    /// peer HUDs. Toggles with the same H / gamepad-Select as the rest of the debug UI.
    ///
    /// BRIEFING's M2 stress check: latency 12, jitter 3, loss 0.05 — hashes must stay green
    /// while the game visibly stutters. (With InputDelay 3 against latency 12, expect a HARD
    /// slowdown — roughly (delay+1)/(delay+1+latency) of full speed. That's lockstep being
    /// correct-but-slow, not broken; it's the itch M3 scratches.)
    /// </summary>
    public sealed class NetworkHud : MonoBehaviour
    {
        public InProcessNetwork Network;

        private bool _visible; // default hidden, same as DebugHud

        private void Update()
        {
            if (InputSampler.IsHudToggleRequested())
                _visible = !_visible;
        }

        private void OnGUI()
        {
            if (!_visible || Network == null) return;

            GUILayout.BeginArea(new Rect(Screen.width / 2f - 170, Screen.height - 130, 340, 120), GUI.skin.box);

            GUILayout.Label($"NETWORK   in-flight: {Network.InFlightCount}");
            GUILayout.Label($"latency {Network.LatencyTicks,2}t   jitter {Network.JitterTicks,2}t   loss {Network.LossChance:P0}");
            Network.LatencyTicks = Mathf.RoundToInt(GUILayout.HorizontalSlider(Network.LatencyTicks, 0f, 30f));
            Network.JitterTicks = Mathf.RoundToInt(GUILayout.HorizontalSlider(Network.JitterTicks, 0f, 10f));
            Network.LossChance = GUILayout.HorizontalSlider(Network.LossChance, 0f, 0.3f);

            GUILayout.EndArea();
        }
    }
}

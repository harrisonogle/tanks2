using UnityEngine.InputSystem;

namespace Tanks.Game
{
    /// <summary>
    /// Global, stateless UI controls read from input devices (match reset, HUD toggle). These
    /// are deliberately static: they hold no state and have no per-player identity. Per-player
    /// game input lives in <see cref="UnityInputSource"/> (an <c>IInputSource</c> instance).
    /// </summary>
    public static class InputSampler
    {
        /// <summary>
        /// Edge-triggered reset signal: true on the frame the keyboard `R` or any connected
        /// gamepad's Start/Options button is first pressed.
        /// </summary>
        public static bool IsResetRequested()
        {
            var kb = Keyboard.current;
            if (kb != null && kb[Key.R].wasPressedThisFrame) return true;

            foreach (var pad in Gamepad.all)
            {
                if (pad.startButton.wasPressedThisFrame) return true;
            }
            return false;
        }

        /// <summary>
        /// Edge-triggered HUD-toggle signal: true on the frame the keyboard `H` or any connected
        /// gamepad's Select / View / Share button is first pressed.
        /// </summary>
        public static bool IsHudToggleRequested()
        {
            var kb = Keyboard.current;
            if (kb != null && kb[Key.H].wasPressedThisFrame) return true;

            foreach (var pad in Gamepad.all)
            {
                if (pad.selectButton.wasPressedThisFrame) return true;
            }
            return false;
        }
    }
}

using Tanks.Sim;
using UnityEngine;
using UnityEngine.InputSystem;
// UnityEngine.InputSystem ships its own PlayerInput MonoBehaviour (for editor-bound device
// configuration, which we don't use). Alias the unqualified name to OUR struct so the rest
// of this file reads naturally.
using PlayerInput = Tanks.Sim.PlayerInput;

namespace Tanks.Game
{
    /// <summary>
    /// Per-player input sampler built on Unity's Input System.
    ///
    /// Auto-binding:
    ///   P1 = Gamepad.all[0] if at least one gamepad is connected, else WASD / Space / Q/E /
    ///        LShift keyboard.
    ///   P2 = Gamepad.all[1] if at least two gamepads are connected, else Arrows / Enter / ,/. /
    ///        RShift keyboard.
    /// Plug an Xbox + PS controller in before pressing Play and both peers pick them up.
    ///
    /// Determinism story: float math (stick atan2, keyboard accumulation, trigger thresholding)
    /// happens LOCALLY, then the sampler quantizes / edge-triggers and hands the sim integer
    /// values. The sim and wire never see a float for aim, and Dash arrives as a discrete bit
    /// only on the tick it transitions from "not held" to "held".
    /// </summary>
    public static class InputSampler
    {
        // Below these thresholds the stick is treated as neutral.
        private const float MoveDeadzone = 0.4f;
        private const float AimDeadzoneSqr = 0.3f * 0.3f;
        private const float TriggerThreshold = 0.5f;

        // Per-player stored state: current turret aim + previous-tick dash state (for edge detection).
        private static int s_p1Turret;
        private static int s_p2Turret;
        private static bool s_p1DashWasHeld;
        private static bool s_p2DashWasHeld;

        /// <summary>Realign stored sampler state to defaults (call on match reset).</summary>
        public static void Reset()
        {
            s_p1Turret = SimConfig.SpawnAngle(0);
            s_p2Turret = SimConfig.SpawnAngle(1);
            s_p1DashWasHeld = false;
            s_p2DashWasHeld = false;
        }

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

        public static PlayerInput SampleP1()
        {
            var pads = Gamepad.all;
            if (pads.Count >= 1) return SampleGamepad(pads[0], ref s_p1Turret, ref s_p1DashWasHeld);
            return SampleKeyboardLeft(ref s_p1Turret, ref s_p1DashWasHeld);
        }

        public static PlayerInput SampleP2()
        {
            var pads = Gamepad.all;
            if (pads.Count >= 2) return SampleGamepad(pads[1], ref s_p2Turret, ref s_p2DashWasHeld);
            return SampleKeyboardRight(ref s_p2Turret, ref s_p2DashWasHeld);
        }

        // --- Gamepad ---

        private static PlayerInput SampleGamepad(Gamepad pad, ref int storedTurret, ref bool dashWasHeld)
        {
            Vector2 left = pad.leftStick.ReadValue();
            Vector2 right = pad.rightStick.ReadValue();

            InputButtons b = InputButtons.None;
            if (left.y > MoveDeadzone) b |= InputButtons.Forward;
            if (left.y < -MoveDeadzone) b |= InputButtons.Back;
            if (left.x < -MoveDeadzone) b |= InputButtons.Left;
            if (left.x > MoveDeadzone) b |= InputButtons.Right;

            if (pad.buttonSouth.isPressed || pad.rightTrigger.ReadValue() > TriggerThreshold)
                b |= InputButtons.Fire;

            // Edge-triggered dash on the left trigger (so holding it doesn't auto-redash on cooldown).
            bool dashHeld = pad.leftTrigger.ReadValue() > TriggerThreshold;
            if (dashHeld && !dashWasHeld) b |= InputButtons.Dash;
            dashWasHeld = dashHeld;

            // Right stick: when past the deadzone, snap absolute aim to the stick direction.
            // Inside the deadzone, hold the last aim (so releasing the stick doesn't snap to 0).
            if (right.sqrMagnitude > AimDeadzoneSqr)
            {
                // atan2 returns radians in [-π, π]; 0 = +X, π/2 = +Y (CCW).
                // Map to a Trig angle index: scale by AngleCount / (2π), then wrap.
                float angleRad = Mathf.Atan2(right.y, right.x);
                int aim = Mathf.RoundToInt(angleRad / (2f * Mathf.PI) * Trig.AngleCount);
                storedTurret = Trig.Normalize(aim); // & AngleMask handles negatives correctly
            }

            return new PlayerInput(b, storedTurret);
        }

        // --- Keyboard (fallback) ---

        private static PlayerInput SampleKeyboardLeft(ref int storedTurret, ref bool dashWasHeld)
        {
            var kb = Keyboard.current;
            if (kb == null) return new PlayerInput(InputButtons.None, storedTurret);

            InputButtons b = InputButtons.None;
            if (kb[Key.W].isPressed) b |= InputButtons.Forward;
            if (kb[Key.S].isPressed) b |= InputButtons.Back;
            if (kb[Key.A].isPressed) b |= InputButtons.Left;
            if (kb[Key.D].isPressed) b |= InputButtons.Right;
            if (kb[Key.Space].isPressed) b |= InputButtons.Fire;

            // Edge-triggered dash on Left Shift.
            bool dashHeld = kb[Key.LeftShift].isPressed;
            if (dashHeld && !dashWasHeld) b |= InputButtons.Dash;
            dashWasHeld = dashHeld;

            // Q / E rotate the turret in WORLD space (independent of body).
            if (kb[Key.Q].isPressed) storedTurret = Trig.Normalize(storedTurret + SimConfig.KeyboardTurretTurnSpeed);
            if (kb[Key.E].isPressed) storedTurret = Trig.Normalize(storedTurret - SimConfig.KeyboardTurretTurnSpeed);

            return new PlayerInput(b, storedTurret);
        }

        private static PlayerInput SampleKeyboardRight(ref int storedTurret, ref bool dashWasHeld)
        {
            var kb = Keyboard.current;
            if (kb == null) return new PlayerInput(InputButtons.None, storedTurret);

            InputButtons b = InputButtons.None;
            if (kb[Key.UpArrow].isPressed) b |= InputButtons.Forward;
            if (kb[Key.DownArrow].isPressed) b |= InputButtons.Back;
            if (kb[Key.LeftArrow].isPressed) b |= InputButtons.Left;
            if (kb[Key.RightArrow].isPressed) b |= InputButtons.Right;
            if (kb[Key.Enter].isPressed) b |= InputButtons.Fire;

            // Edge-triggered dash on Right Shift.
            bool dashHeld = kb[Key.RightShift].isPressed;
            if (dashHeld && !dashWasHeld) b |= InputButtons.Dash;
            dashWasHeld = dashHeld;

            if (kb[Key.Comma].isPressed) storedTurret = Trig.Normalize(storedTurret + SimConfig.KeyboardTurretTurnSpeed);
            if (kb[Key.Period].isPressed) storedTurret = Trig.Normalize(storedTurret - SimConfig.KeyboardTurretTurnSpeed);

            return new PlayerInput(b, storedTurret);
        }
    }
}

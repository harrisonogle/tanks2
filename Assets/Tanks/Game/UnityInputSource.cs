using Tanks.Net;
using Tanks.Sim;
using UnityEngine;
using UnityEngine.InputSystem;
// UnityEngine.InputSystem ships its own PlayerInput MonoBehaviour; alias the unqualified name
// to OUR struct so the rest of this file reads naturally.
using PlayerInput = Tanks.Sim.PlayerInput;

namespace Tanks.Game
{
    /// <summary>
    /// Device-backed <see cref="IInputSource"/> for one player. Holds that player's own turret
    /// aim and dash edge-detection state (per-instance — no shared statics). Binding by index:
    ///   player 0 -> Gamepad.all[0] if present, else WASD / Space / Q-E / LShift
    ///   player 1 -> Gamepad.all[1] if present, else Arrows / Enter / ,-. / RShift
    ///
    /// Determinism story: float math (stick atan2, deadzones, trigger thresholds) happens
    /// LOCALLY, then quantizes / edge-triggers before producing an integer PlayerInput. The sim
    /// and wire never see a float for aim; Dash arrives as a discrete bit only on the tick it
    /// transitions from "not held" to "held".
    /// </summary>
    public sealed class UnityInputSource : IInputSource
    {
        // Below these thresholds the stick is treated as neutral.
        private const float MoveDeadzone = 0.4f;
        private const float AimDeadzoneSqr = 0.3f * 0.3f;
        private const float TriggerThreshold = 0.5f;

        private readonly int _player;   // 0 or 1
        private readonly SimConfig _config;
        private int _turret;            // current absolute turret aim (Trig angle index)
        // NOTE: no dash edge detection here anymore. This sampler ships LEVELS (buttons
        // currently held + absolute aim); the engine derives the Dash edge at the tick
        // boundary (the minting station). Edge bits must never cross a latest-wins ring.

        public UnityInputSource(int player, SimConfig config)
        {
            _player = player;
            _config = config;
            Reset();
        }

        public void Reset()
        {
            _turret = _config.SpawnAngle(_player);
        }

        public PlayerInput Sample()
        {
            var pads = Gamepad.all;
            if (pads.Count > _player) return SampleGamepad(pads[_player]);
            return _player == 0 ? SampleKeyboardLeft() : SampleKeyboardRight();
        }

        // --- Gamepad ---

        private PlayerInput SampleGamepad(Gamepad pad)
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
            if (dashHeld) b |= InputButtons.Dash; // level, not edge

            // Right stick: when past the deadzone, snap absolute aim to the stick direction.
            // Inside the deadzone, hold the last aim (so releasing the stick doesn't snap to 0).
            if (right.sqrMagnitude > AimDeadzoneSqr)
            {
                // atan2 returns radians in [-π, π]; 0 = +X, π/2 = +Y (CCW).
                // Map to a Trig angle index: scale by AngleCount / (2π), then wrap.
                float angleRad = Mathf.Atan2(right.y, right.x);
                int aim = Mathf.RoundToInt(angleRad / (2f * Mathf.PI) * Trig.AngleCount);
                _turret = Trig.Normalize(aim); // & AngleMask handles negatives correctly
            }

            return new PlayerInput(b, _turret);
        }

        // --- Keyboard (fallback) ---

        private PlayerInput SampleKeyboardLeft()
        {
            var kb = Keyboard.current;
            if (kb == null) return new PlayerInput(InputButtons.None, _turret);

            InputButtons b = InputButtons.None;
            if (kb[Key.W].isPressed) b |= InputButtons.Forward;
            if (kb[Key.S].isPressed) b |= InputButtons.Back;
            if (kb[Key.A].isPressed) b |= InputButtons.Left;
            if (kb[Key.D].isPressed) b |= InputButtons.Right;
            if (kb[Key.Space].isPressed) b |= InputButtons.Fire;

            // Edge-triggered dash on Left Shift.
            bool dashHeld = kb[Key.LeftShift].isPressed;
            if (dashHeld) b |= InputButtons.Dash; // level, not edge

            // Q / E rotate the turret in WORLD space (independent of body).
            if (kb[Key.Q].isPressed) _turret = Trig.Normalize(_turret + _config.KeyboardTurretTurnSpeed);
            if (kb[Key.E].isPressed) _turret = Trig.Normalize(_turret - _config.KeyboardTurretTurnSpeed);

            return new PlayerInput(b, _turret);
        }

        private PlayerInput SampleKeyboardRight()
        {
            var kb = Keyboard.current;
            if (kb == null) return new PlayerInput(InputButtons.None, _turret);

            InputButtons b = InputButtons.None;
            if (kb[Key.UpArrow].isPressed) b |= InputButtons.Forward;
            if (kb[Key.DownArrow].isPressed) b |= InputButtons.Back;
            if (kb[Key.LeftArrow].isPressed) b |= InputButtons.Left;
            if (kb[Key.RightArrow].isPressed) b |= InputButtons.Right;
            if (kb[Key.Enter].isPressed) b |= InputButtons.Fire;

            // Edge-triggered dash on Right Shift.
            bool dashHeld = kb[Key.RightShift].isPressed;
            if (dashHeld) b |= InputButtons.Dash; // level, not edge

            if (kb[Key.Comma].isPressed) _turret = Trig.Normalize(_turret + _config.KeyboardTurretTurnSpeed);
            if (kb[Key.Period].isPressed) _turret = Trig.Normalize(_turret - _config.KeyboardTurretTurnSpeed);

            return new PlayerInput(b, _turret);
        }
    }
}

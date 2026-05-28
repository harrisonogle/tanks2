using Tanks.Sim;
using UnityEngine;

namespace Tanks.Game
{
    /// <summary>
    /// Reads the keyboard into <see cref="PlayerInput"/> structs (legacy Input — zero setup).
    /// Both players are sampled locally for the offline sandbox. Once netcode is wired, only
    /// the LOCAL player is sampled here; the remote player's input arrives over the transport.
    ///
    /// Turret aim is absolute. The sampler maintains each player's current turret angle as
    /// stored state (Q/E for P1, comma/period for P2 nudge it in world space, independent of
    /// the body's facing). This means: float math (mouse projection, gamepad atan2, …) can
    /// later replace this keyboard accumulator, and the sim never sees a non-integer angle.
    /// </summary>
    public static class InputSampler
    {
        // Per-tick angle delta when a turret-turn key is held. Matched to body turn speed so
        // both feel equally responsive. Purely a UX choice — the sim has no opinion on rate.
        private const int KeyboardTurretTurnSpeed = SimConfig.TankTurnSpeed;

        private static int s_p1Turret;
        private static int s_p2Turret;

        /// <summary>Reset stored turret angles to the spawn orientations. Call from match reset.</summary>
        public static void Reset()
        {
            s_p1Turret = SimConfig.SpawnAngle(0);
            s_p2Turret = SimConfig.SpawnAngle(1);
        }

        public static PlayerInput SampleP1()
        {
            InputButtons b = InputButtons.None;
            if (Input.GetKey(KeyCode.W)) b |= InputButtons.Forward;
            if (Input.GetKey(KeyCode.S)) b |= InputButtons.Back;
            if (Input.GetKey(KeyCode.A)) b |= InputButtons.Left;
            if (Input.GetKey(KeyCode.D)) b |= InputButtons.Right;
            if (Input.GetKey(KeyCode.Space)) b |= InputButtons.Fire;

            // Q / E rotate the turret in WORLD space (independent of body).
            // Q = CCW (matches body Left = increasing angle); E = CW.
            if (Input.GetKey(KeyCode.Q)) s_p1Turret = Trig.Normalize(s_p1Turret + KeyboardTurretTurnSpeed);
            if (Input.GetKey(KeyCode.E)) s_p1Turret = Trig.Normalize(s_p1Turret - KeyboardTurretTurnSpeed);

            return new PlayerInput(b, s_p1Turret);
        }

        public static PlayerInput SampleP2()
        {
            InputButtons b = InputButtons.None;
            if (Input.GetKey(KeyCode.UpArrow)) b |= InputButtons.Forward;
            if (Input.GetKey(KeyCode.DownArrow)) b |= InputButtons.Back;
            if (Input.GetKey(KeyCode.LeftArrow)) b |= InputButtons.Left;
            if (Input.GetKey(KeyCode.RightArrow)) b |= InputButtons.Right;
            if (Input.GetKey(KeyCode.Return)) b |= InputButtons.Fire;

            // ',' / '.' rotate the turret. Comma = CCW, period = CW.
            if (Input.GetKey(KeyCode.Comma)) s_p2Turret = Trig.Normalize(s_p2Turret + KeyboardTurretTurnSpeed);
            if (Input.GetKey(KeyCode.Period)) s_p2Turret = Trig.Normalize(s_p2Turret - KeyboardTurretTurnSpeed);

            return new PlayerInput(b, s_p2Turret);
        }
    }
}

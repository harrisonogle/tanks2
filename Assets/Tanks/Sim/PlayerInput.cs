using System;

namespace Tanks.Sim
{
    /// <summary>
    /// One player's input for one tick. Small on purpose: this is exactly what gets
    /// serialized and sent over the wire for lockstep/rollback.
    /// </summary>
    [Flags]
    public enum InputButtons : byte
    {
        None    = 0,
        Forward = 1 << 0, // +Y movement
        Back    = 1 << 1, // -Y movement
        Left    = 1 << 2, // -X movement
        Right   = 1 << 3, // +X movement
        Fire    = 1 << 4,
        Dash    = 1 << 5, // burst of speed (sampler edge-triggers this)
    }

    public readonly struct PlayerInput : IEquatable<PlayerInput>
    {
        public readonly InputButtons Buttons;

        /// <summary>
        /// Absolute turret aim, as an angle index in [0, <see cref="Trig.AngleCount"/>).
        /// The input layer maintains this locally (mouse projection / right-stick / accumulated
        /// keyboard offset) and sends it every tick. Quantization to integer happens BEFORE the
        /// sim ever sees it — that's what keeps determinism intact across machines.
        /// </summary>
        public readonly ushort TurretAim;

        public PlayerInput(InputButtons buttons) : this(buttons, 0) { }

        public PlayerInput(InputButtons buttons, int turretAim)
        {
            Buttons = buttons;
            TurretAim = (ushort)(turretAim & Trig.AngleMask);
        }

        public static readonly PlayerInput None = new PlayerInput(InputButtons.None, 0);

        public bool Forward => (Buttons & InputButtons.Forward) != 0;
        public bool Back    => (Buttons & InputButtons.Back) != 0;
        public bool Left    => (Buttons & InputButtons.Left) != 0;
        public bool Right   => (Buttons & InputButtons.Right) != 0;
        public bool Fire    => (Buttons & InputButtons.Fire) != 0;
        public bool Dash    => (Buttons & InputButtons.Dash) != 0;

        public bool Equals(PlayerInput other) => Buttons == other.Buttons && TurretAim == other.TurretAim;
        public override bool Equals(object obj) => obj is PlayerInput p && Equals(p);
        public override int GetHashCode() => ((int)Buttons * 397) ^ TurretAim;
        public override string ToString() => $"{Buttons} aim={TurretAim}";
    }
}

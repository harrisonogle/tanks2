using System;

namespace Tanks.Sim
{
    /// <summary>
    /// One player's input for one tick, packed into a single byte. Small on purpose:
    /// this is exactly what gets serialized and sent over the wire for lockstep/rollback,
    /// so keeping it tiny keeps per-tick bandwidth tiny.
    /// </summary>
    [Flags]
    public enum InputButtons : byte
    {
        None    = 0,
        Forward = 1 << 0,
        Back    = 1 << 1,
        Left    = 1 << 2, // rotate counter-clockwise
        Right   = 1 << 3, // rotate clockwise
        Fire    = 1 << 4,
    }

    public readonly struct PlayerInput : IEquatable<PlayerInput>
    {
        public readonly InputButtons Buttons;

        public PlayerInput(InputButtons buttons) { Buttons = buttons; }

        public static readonly PlayerInput None = new PlayerInput(InputButtons.None);

        public bool Forward => (Buttons & InputButtons.Forward) != 0;
        public bool Back    => (Buttons & InputButtons.Back) != 0;
        public bool Left    => (Buttons & InputButtons.Left) != 0;
        public bool Right   => (Buttons & InputButtons.Right) != 0;
        public bool Fire    => (Buttons & InputButtons.Fire) != 0;

        public byte ToByte() => (byte)Buttons;
        public static PlayerInput FromByte(byte b) => new PlayerInput((InputButtons)b);

        public bool Equals(PlayerInput other) => Buttons == other.Buttons;
        public override bool Equals(object obj) => obj is PlayerInput p && Equals(p);
        public override int GetHashCode() => (int)Buttons;
        public override string ToString() => Buttons.ToString();
    }
}

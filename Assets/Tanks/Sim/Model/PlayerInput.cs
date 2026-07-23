using System;
using System.Runtime.InteropServices;

namespace Tanks.Sim;

[StructLayout(LayoutKind.Explicit, Size = Size)]
public readonly struct PlayerInput : IEquatable<PlayerInput>
{
    public const int Size = 4;

    public static readonly PlayerInput None = new PlayerInput(InputButtons.None, 0);

    [FieldOffset(0)] public readonly InputButtons Buttons;

    /// <summary>
    /// Absolute turret aim, as an angle index in [0, <see cref="Trig.AngleCount"/>).
    /// </summary>
    [FieldOffset(2)] public readonly ushort TurretAim;

    public PlayerInput(InputButtons buttons) : this(buttons, 0) { }

    public PlayerInput(InputButtons buttons, int turretAim)
    {
        Buttons = buttons;
        TurretAim = (ushort)(turretAim & Trig.AngleMask);
    }

    public bool Forward => (Buttons & InputButtons.Forward) != 0;
    public bool Back => (Buttons & InputButtons.Back) != 0;
    public bool Left => (Buttons & InputButtons.Left) != 0;
    public bool Right => (Buttons & InputButtons.Right) != 0;
    public bool Fire => (Buttons & InputButtons.Fire) != 0;
    public bool Dash => (Buttons & InputButtons.Dash) != 0;

    public bool Equals(PlayerInput other) => Buttons == other.Buttons && TurretAim == other.TurretAim;
    public override bool Equals(object? obj) => obj is PlayerInput p && Equals(p);
    public override int GetHashCode() => ((int)Buttons * 397) ^ TurretAim;
    public override string ToString() => $"{Buttons} aim={TurretAim}";
}

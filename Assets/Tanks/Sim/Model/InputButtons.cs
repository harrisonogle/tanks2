using System;

namespace Tanks.Sim;

/// <summary>
/// One player's input for one tick. Small on purpose: this is exactly what gets
/// serialized and sent over the wire for lockstep/rollback.
/// </summary>
[Flags]
public enum InputButtons : byte
{
    None = 0,
    Forward = 1 << 0, // +Y movement
    Back = 1 << 1, // -Y movement
    Left = 1 << 2, // -X movement
    Right = 1 << 3, // +X movement
    Fire = 1 << 4,
    Dash = 1 << 5, // burst of speed (sampler edge-triggers this)
}
using Tanks.Sim;

namespace Tanks.Net
{
    /// <summary>
    /// One player's per-tick input, abstracted so the driver is decoupled from *how* input is
    /// produced. At runtime the Unity shell supplies a device-backed implementation; tests
    /// supply a scripted one. Each source owns its own per-player state (e.g. turret angle,
    /// dash edge detection) — no shared statics.
    /// </summary>
    public interface IInputSource
    {
        /// <summary>Produce this player's input for the current tick.</summary>
        PlayerInput Sample();

        /// <summary>Realign any stored state to defaults (called on match reset).</summary>
        void Reset();
    }
}

using Tanks.Sim;
using UnityEngine;

namespace Tanks.Game
{
    /// <summary>
    /// Reads the keyboard into <see cref="PlayerInput"/> structs (legacy Input — zero setup).
    /// Both players are sampled locally for the offline sandbox. Once netcode is wired, only
    /// the LOCAL player is sampled here; the remote player's input arrives over the transport.
    ///
    /// P1 (blue): W/A/S/D + Space.   P2 (red): Arrow keys + Enter.
    /// </summary>
    public static class InputSampler
    {
        public static PlayerInput SampleP1()
        {
            InputButtons b = InputButtons.None;
            if (Input.GetKey(KeyCode.W)) b |= InputButtons.Forward;
            if (Input.GetKey(KeyCode.S)) b |= InputButtons.Back;
            if (Input.GetKey(KeyCode.A)) b |= InputButtons.Left;
            if (Input.GetKey(KeyCode.D)) b |= InputButtons.Right;
            if (Input.GetKey(KeyCode.Space)) b |= InputButtons.Fire;
            return new PlayerInput(b);
        }

        public static PlayerInput SampleP2()
        {
            InputButtons b = InputButtons.None;
            if (Input.GetKey(KeyCode.UpArrow)) b |= InputButtons.Forward;
            if (Input.GetKey(KeyCode.DownArrow)) b |= InputButtons.Back;
            if (Input.GetKey(KeyCode.LeftArrow)) b |= InputButtons.Left;
            if (Input.GetKey(KeyCode.RightArrow)) b |= InputButtons.Right;
            if (Input.GetKey(KeyCode.Return)) b |= InputButtons.Fire;
            return new PlayerInput(b);
        }
    }
}

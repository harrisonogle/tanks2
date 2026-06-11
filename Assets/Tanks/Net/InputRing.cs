using Tanks.Sim;

namespace Tanks.Net
{
    /// <summary>
    /// Ring buffer of per-player inputs keyed by tick — the bookkeeping lockstep needs.
    /// Each peer owns one: its own inputs go in when sampled (scheduled a few ticks ahead),
    /// the remote's go in as packets arrive, and the sim may advance to a tick only when
    /// <see cref="Has"/> is true for BOTH players at that tick.
    ///
    /// Slots are keyed <c>tick % Capacity</c> with an exact-tick check (same scheme as
    /// StateHistory), so a slot left over from an old tick reads as "missing", never as a
    /// wrong input. Writing a new tick into a slot evicts whatever was there.
    ///
    /// Pure C# on purpose: it lives in Tanks.Net so the headless test suite covers it.
    /// </summary>
    public sealed class InputRing
    {
        public const int Capacity = 256; // matches StateHistory; >> input delay + any sane latency

        private struct Slot
        {
            public uint Tick;
            public PlayerInput P0;
            public PlayerInput P1;
            public byte HasMask; // bit 0 = P0 recorded, bit 1 = P1 recorded
        }

        private readonly Slot[] _slots = new Slot[Capacity];

        public void Record(uint tick, int player, PlayerInput input)
        {
            ref Slot s = ref _slots[(int)(tick % Capacity)];
            if (s.Tick != tick)
            {
                s.Tick = tick;
                s.HasMask = 0; // new tick claims the slot; the old tick's inputs are gone
            }
            if (player == 0) s.P0 = input; else s.P1 = input;
            s.HasMask |= (byte)(1 << player);
        }

        public bool Has(uint tick, int player)
        {
            ref readonly Slot s = ref _slots[(int)(tick % Capacity)];
            return s.Tick == tick && (s.HasMask & (1 << player)) != 0;
        }

        /// <summary>The recorded input, or <see cref="PlayerInput.None"/> if missing (gate on <see cref="Has"/> first).</summary>
        public PlayerInput Get(uint tick, int player)
        {
            ref readonly Slot s = ref _slots[(int)(tick % Capacity)];
            if (s.Tick != tick || (s.HasMask & (1 << player)) == 0) return PlayerInput.None;
            return player == 0 ? s.P0 : s.P1;
        }

        public void Clear() => System.Array.Clear(_slots, 0, Capacity);
    }
}

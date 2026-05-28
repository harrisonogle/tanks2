namespace Tanks.Sim
{
    /// <summary>
    /// Tiny deterministic PRNG (xorshift32). Same seed -> same sequence everywhere.
    /// Used by the simulation (reserved) and by the fake network's loss/jitter model.
    /// </summary>
    public struct Xorshift32
    {
        public uint State;

        public Xorshift32(uint seed) { State = seed == 0 ? 1u : seed; }

        public uint Next()
        {
            uint x = State;
            x ^= x << 13;
            x ^= x >> 17;
            x ^= x << 5;
            State = x;
            return x;
        }

        /// <summary>Inclusive range. <paramref name="minInclusive"/> must be &lt;= <paramref name="maxInclusive"/>.</summary>
        public int NextRange(int minInclusive, int maxInclusive)
        {
            uint span = (uint)(maxInclusive - minInclusive + 1);
            return minInclusive + (int)(Next() % span);
        }

        /// <summary>Uniform float in [0, 1).</summary>
        public float NextFloat01() => (Next() >> 8) * (1.0f / 16777216.0f);
    }
}

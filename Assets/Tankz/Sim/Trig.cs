using System;

namespace Tankz.Sim
{
    /// <summary>
    /// Deterministic trig via precomputed lookup tables. Angles are integer indices
    /// in [0, AngleCount). This keeps rotation bit-exact and cheap.
    ///
    /// NOTE on cross-machine determinism: the tables are generated once at static init
    /// from <see cref="Math.Sin"/>/<see cref="Math.Cos"/>. On a single machine (and for
    /// our in-process fake network) this is fully deterministic. For STRICT cross-machine
    /// determinism you'd bake these as integer constants instead of generating from double.
    /// Left as a generated table for readability; swap later if you take it cross-machine.
    /// </summary>
    public static class Trig
    {
        public const int AngleCount = 2048;       // full circle; power of two for cheap wrap
        public const int AngleMask = AngleCount - 1;
        public const int QuarterTurn = AngleCount / 4;

        private static readonly Fixed[] s_Sin = new Fixed[AngleCount];
        private static readonly Fixed[] s_Cos = new Fixed[AngleCount];

        static Trig()
        {
            for (int i = 0; i < AngleCount; i++)
            {
                double radians = (2.0 * Math.PI * i) / AngleCount;
                s_Sin[i] = Fixed.FromFloat((float)Math.Sin(radians));
                s_Cos[i] = Fixed.FromFloat((float)Math.Cos(radians));
            }
        }

        /// <summary>Wrap any integer angle into [0, AngleCount). Works for negatives.</summary>
        public static int Normalize(int angle) => angle & AngleMask;

        public static Fixed Sin(int angle) => s_Sin[angle & AngleMask];
        public static Fixed Cos(int angle) => s_Cos[angle & AngleMask];

        /// <summary>Unit direction vector for an angle (angle 0 = +X, increasing CCW toward +Y).</summary>
        public static FixVec2 Direction(int angle) => new FixVec2(Cos(angle), Sin(angle));
    }
}

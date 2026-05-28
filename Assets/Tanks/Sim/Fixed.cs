using System;

namespace Tanks.Sim
{
    /// <summary>
    /// Deterministic 16.16 signed fixed-point number.
    ///
    /// Why this exists: rollback / lockstep netcode requires that the same inputs
    /// produce *bit-identical* state on every machine. IEEE floats do not guarantee
    /// that across CPUs/compilers/build flags. Integer math does. So the entire
    /// simulation uses <see cref="Fixed"/> instead of float/double.
    ///
    /// Layout: a single <see cref="int"/> where the low 16 bits are the fraction.
    /// Range ~[-32768, 32767], resolution ~1.5e-5. Plenty for a small arena.
    /// </summary>
    public readonly struct Fixed : IEquatable<Fixed>, IComparable<Fixed>
    {
        public const int FractionalBits = 16;
        public const int OneRaw = 1 << FractionalBits; // 65536

        /// <summary>The underlying fixed-point bits. This is the canonical value for hashing/serialization.</summary>
        public readonly int Raw;

        private Fixed(int raw) { Raw = raw; }

        public static Fixed FromRaw(int raw) => new Fixed(raw);
        public static Fixed FromInt(int value) => new Fixed(value << FractionalBits);

        /// <summary>
        /// Convert from float. Use ONLY for compile-time-ish constants (config, lookup-table
        /// generation) — never inside the per-tick simulation, or determinism is lost.
        /// </summary>
        public static Fixed FromFloat(float value) => new Fixed((int)Math.Round(value * OneRaw));

        public static readonly Fixed Zero = FromRaw(0);
        public static readonly Fixed One = FromRaw(OneRaw);
        public static readonly Fixed Half = FromRaw(OneRaw / 2);

        public float ToFloat() => Raw / (float)OneRaw;
        public int ToInt() => Raw >> FractionalBits; // floor toward negative infinity

        public static Fixed operator +(Fixed a, Fixed b) => FromRaw(a.Raw + b.Raw);
        public static Fixed operator -(Fixed a, Fixed b) => FromRaw(a.Raw - b.Raw);
        public static Fixed operator -(Fixed a) => FromRaw(-a.Raw);

        // Multiply/divide widen to long to avoid intermediate overflow, then shift back.
        public static Fixed operator *(Fixed a, Fixed b) => FromRaw((int)(((long)a.Raw * b.Raw) >> FractionalBits));
        public static Fixed operator /(Fixed a, Fixed b) => FromRaw((int)(((long)a.Raw << FractionalBits) / b.Raw));

        public static Fixed operator *(Fixed a, int b) => FromRaw(a.Raw * b);
        public static Fixed operator /(Fixed a, int b) => FromRaw(a.Raw / b);

        public static bool operator ==(Fixed a, Fixed b) => a.Raw == b.Raw;
        public static bool operator !=(Fixed a, Fixed b) => a.Raw != b.Raw;
        public static bool operator <(Fixed a, Fixed b) => a.Raw < b.Raw;
        public static bool operator >(Fixed a, Fixed b) => a.Raw > b.Raw;
        public static bool operator <=(Fixed a, Fixed b) => a.Raw <= b.Raw;
        public static bool operator >=(Fixed a, Fixed b) => a.Raw >= b.Raw;

        public static Fixed Abs(Fixed a) => FromRaw(a.Raw < 0 ? -a.Raw : a.Raw);
        public static Fixed Min(Fixed a, Fixed b) => a.Raw < b.Raw ? a : b;
        public static Fixed Max(Fixed a, Fixed b) => a.Raw > b.Raw ? a : b;
        public static int Sign(Fixed a) => a.Raw == 0 ? 0 : (a.Raw < 0 ? -1 : 1);

        public static Fixed Clamp(Fixed v, Fixed lo, Fixed hi)
            => v.Raw < lo.Raw ? lo : (v.Raw > hi.Raw ? hi : v);

        /// <summary>Deterministic square root (floor) for non-negative values.</summary>
        public static Fixed Sqrt(Fixed a)
        {
            if (a.Raw <= 0) return Zero;
            // sqrt(raw/2^16) in fixed = isqrt(raw << 16). Widen to ulong; raw<<16 fits in 48 bits.
            return FromRaw((int)ISqrt((ulong)a.Raw << FractionalBits));
        }

        /// <summary>Integer square root (floor) via Newton's method. Deterministic.</summary>
        public static uint ISqrt(ulong n)
        {
            if (n == 0) return 0;
            ulong x = n;
            ulong y = (x + 1) >> 1;
            while (y < x)
            {
                x = y;
                y = (x + n / x) >> 1;
            }
            return (uint)x;
        }

        public bool Equals(Fixed other) => Raw == other.Raw;
        public override bool Equals(object obj) => obj is Fixed f && f.Raw == Raw;
        public override int GetHashCode() => Raw;
        public int CompareTo(Fixed other) => Raw.CompareTo(other.Raw);
        public override string ToString() => ToFloat().ToString("0.000");
    }
}

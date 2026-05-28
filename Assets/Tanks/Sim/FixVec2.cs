using System;

namespace Tanks.Sim
{
    /// <summary>Deterministic 2D vector built on <see cref="Fixed"/>.</summary>
    public readonly struct FixVec2 : IEquatable<FixVec2>
    {
        public readonly Fixed X;
        public readonly Fixed Y;

        public FixVec2(Fixed x, Fixed y) { X = x; Y = y; }

        public static readonly FixVec2 Zero = new FixVec2(Fixed.Zero, Fixed.Zero);

        public static FixVec2 operator +(FixVec2 a, FixVec2 b) => new FixVec2(a.X + b.X, a.Y + b.Y);
        public static FixVec2 operator -(FixVec2 a, FixVec2 b) => new FixVec2(a.X - b.X, a.Y - b.Y);
        public static FixVec2 operator -(FixVec2 a) => new FixVec2(-a.X, -a.Y);
        public static FixVec2 operator *(FixVec2 a, Fixed s) => new FixVec2(a.X * s, a.Y * s);

        public Fixed LengthSq() => X * X + Y * Y;
        public Fixed Length() => Fixed.Sqrt(X * X + Y * Y);

        public bool Equals(FixVec2 other) => X == other.X && Y == other.Y;
        public override bool Equals(object obj) => obj is FixVec2 v && Equals(v);
        public override int GetHashCode() => (X.Raw * 397) ^ Y.Raw;
        public override string ToString() => $"({X}, {Y})";
    }
}

namespace Tanks.Sim
{
    /// <summary>Axis-aligned bounding box in fixed-point world space.</summary>
    public readonly struct Aabb
    {
        public readonly Fixed MinX, MinY, MaxX, MaxY;

        public Aabb(Fixed minX, Fixed minY, Fixed maxX, Fixed maxY)
        {
            MinX = minX; MinY = minY; MaxX = maxX; MaxY = maxY;
        }

        public static Aabb FromInts(int minX, int minY, int maxX, int maxY)
            => new Aabb(Fixed.FromInt(minX), Fixed.FromInt(minY), Fixed.FromInt(maxX), Fixed.FromInt(maxY));

        public Fixed CenterX => (MinX + MaxX) / 2;
        public Fixed CenterY => (MinY + MaxY) / 2;

        public bool ContainsPoint(Fixed x, Fixed y)
            => x >= MinX && x <= MaxX && y >= MinY && y <= MaxY;

        /// <summary>True if a square of the given half-extent centered at (x,y) overlaps this box.</summary>
        public bool OverlapsSquare(Fixed x, Fixed y, Fixed halfExtent)
            => x + halfExtent > MinX && x - halfExtent < MaxX
            && y + halfExtent > MinY && y - halfExtent < MaxY;
    }

    /// <summary>
    /// Static level geometry: outer bounds (handled implicitly via reflection/clamping)
    /// plus a small set of interior wall blocks. Crude on purpose.
    /// </summary>
    public sealed class Arena
    {
        public readonly Fixed Width;
        public readonly Fixed Height;
        public readonly Aabb[] Walls;

        public Arena(Fixed width, Fixed height, Aabb[] walls)
        {
            Width = width;
            Height = height;
            Walls = walls;
        }

        public static Arena CreateDefault()
        {
            var walls = new[]
            {
                Aabb.FromInts(10, 6, 12, 14),   // left pillar
                Aabb.FromInts(20, 6, 22, 14),   // right pillar
                Aabb.FromInts(15, 9, 17, 11),   // center block
            };
            return new Arena(SimConfig.ArenaWidth, SimConfig.ArenaHeight, walls);
        }
    }
}

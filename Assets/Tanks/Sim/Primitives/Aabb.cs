using System.Runtime.InteropServices;

namespace Tanks.Sim;

/// <summary>Axis-aligned bounding box in fixed-point world space.</summary>
[StructLayout(LayoutKind.Explicit, Size = Size)]
public readonly struct Aabb
{
    public const int Size = 4 * Fixed.Size;

    [FieldOffset(0 * Fixed.Size)] public readonly Fixed MinX;
    [FieldOffset(1 * Fixed.Size)] public readonly Fixed MinY;
    [FieldOffset(2 * Fixed.Size)] public readonly Fixed MaxX;
    [FieldOffset(3 * Fixed.Size)] public readonly Fixed MaxY;

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
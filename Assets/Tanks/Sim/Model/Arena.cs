using System;
using System.Diagnostics;

namespace Tanks.Sim;

/// <summary>
/// Static level geometry: outer bounds (handled implicitly via reflection/clamping)
/// plus a small set of interior wall blocks. Crude on purpose.
/// </summary>
public sealed unsafe class Arena
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

    public int GetByteCount()
    {
        return sizeof(ArenaHeader) + sizeof(Aabb) * Walls.Length;
    }

    public bool TrySerialize(byte* buffer, int length, out int bytesWritten)
    {
        if (length < GetByteCount())
        {
            bytesWritten = 0;
            return false;
        }
        byte* cursor = buffer;
        var header = (ArenaHeader*)cursor;
        header->Width = Width;
        header->Height = Height;
        header->WallCount = Walls.Length;
        cursor += sizeof(ArenaHeader);
        var walls = new Span<Aabb>((Aabb*)cursor, Walls.Length);
        new ReadOnlySpan<Aabb>(Walls).CopyTo(walls);
        cursor += sizeof(Aabb) * Walls.Length;
        bytesWritten = (int)(cursor - buffer);
        Debug.Assert(bytesWritten == GetByteCount());
        return true;
    }

    public static Arena CreateDefault(SimConfig config)
    {
        var walls = new[]
        {
                Aabb.FromInts(10, 6, 12, 14),   // left pillar
                Aabb.FromInts(20, 6, 22, 14),   // right pillar
                Aabb.FromInts(15, 9, 17, 11),   // center block
            };
        return new Arena(config.ArenaWidth, config.ArenaHeight, walls);
    }
}

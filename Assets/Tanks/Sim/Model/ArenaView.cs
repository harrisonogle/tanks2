using System;
using System.Runtime.InteropServices;

namespace Tanks.Sim;

[StructLayout(LayoutKind.Explicit, Size = Size)]
public struct ArenaHeader
{
    public const int Size = 12;
    [FieldOffset(0)] public Fixed Width;
    [FieldOffset(4)] public Fixed Height;
    [FieldOffset(8)] public int WallCount;
}

public unsafe ref struct ArenaView
{
    public static bool TryCreate(byte* buffer, int length, out ArenaView view)
    {
        if (length < sizeof(ArenaHeader))
        {
            view = default;
            return false;
        }

        byte* cursor = buffer;
        var header = (ArenaHeader*)cursor;

        if (header->WallCount < 0)
        {
            view = default;
            return false;
        }

        cursor += sizeof(ArenaHeader);
        var walls = (Aabb*)cursor;
        cursor += sizeof(Aabb) * header->WallCount;

        if ((int)(cursor - buffer) > length)
        {
            view = default;
            return false;
        }

        view = new ArenaView(buffer, length, header, walls);
        return true;
    }

    private ArenaView(byte* buffer, int length, ArenaHeader* header, Aabb* walls)
    {
        Buffer = buffer;
        Length = length;
        Header = header;
        _walls = walls;
    }

    internal readonly byte* Buffer;
    internal readonly int Length;
    private Aabb* _walls;
    public ArenaHeader* Header;
    public Fixed Width => Header->Width;
    public Fixed Height => Header->Height;
    public ReadOnlySpan<Aabb> Walls => new ReadOnlySpan<Aabb>(_walls, Header->WallCount);
}
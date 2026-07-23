using System;
using System.Runtime.InteropServices;

namespace Tanks.Sim;

[StructLayout(LayoutKind.Explicit, Size = Size)]
public struct GameStateHeader
{
    public const int Size = 16;
    [FieldOffset(0)] public uint Tick;
    [FieldOffset(4)] public uint Rng;
    [FieldOffset(8)] public uint TankCount;
    [FieldOffset(12)] public uint BulletCount;
}

public unsafe ref struct GameStateView
{
    public static bool TryCreate(byte* buffer, int length, out GameStateView view)
    {
        if (length < sizeof(GameStateHeader))
        {
            view = default;
            return false;
        }

        var header = (GameStateHeader*)buffer;

        if (header->TankCount < 0 || header->BulletCount < 0)
        {
            view = default;
            return false;
        }

        var tanks = (Tank*)(buffer + sizeof(GameStateHeader));
        var bullets = (Bullet*)(tanks + header->TankCount);

        byte* end = (byte*)(bullets + header->BulletCount);

        if (end - buffer > length)
        {
            view = default;
            return false;
        }

        view = new GameStateView(buffer, length, header, tanks, bullets);
        return true;
    }

    private GameStateView(byte* buffer, int length, GameStateHeader* header, Tank* tanks, Bullet* bullets)
    {
        Buffer = buffer;
        Length = length;
        Header = header;
        _tanks = tanks;
        _bullets = bullets;
    }

    private Tank* _tanks;
    private Bullet* _bullets;
    internal readonly byte* Buffer;
    internal readonly int Length;
    public GameStateHeader* Header;
    public uint Tick => Header->Tick;
    public uint Rng => Header->Rng;
    public ReadOnlySpan<Tank> Tanks => new ReadOnlySpan<Tank>(_tanks, (int)Header->TankCount);
    public ReadOnlySpan<Bullet> Bullets => new ReadOnlySpan<Bullet>(_bullets, (int)Header->BulletCount);
}
using System.Runtime.InteropServices;
using System;

namespace Tanks.Net;

[StructLayout(LayoutKind.Explicit, Size = Size)]
public readonly struct SessionId : IEquatable<SessionId>
{
    public const int Size = 8;

    internal SessionId(uint slotId, uint gen)
    {
        SlotId = slotId;
        Gen = gen;
    }

    [FieldOffset(0)] internal readonly uint SlotId;
    [FieldOffset(4)] internal readonly uint Gen;

    public bool Equals(SessionId other)
    {
        return other.SlotId == SlotId && other.Gen == Gen;
    }

    public override int GetHashCode()
    {
        return (int)(SlotId ^ Gen);
    }

    public override string ToString()
    {
        return $"s{SlotId}.{Gen}";
    }
}
using System.Buffers.Binary;
using System.Runtime.InteropServices;
using System;

namespace Tanks.Net;

[StructLayout(LayoutKind.Explicit, Size = Size)]
public unsafe readonly struct PeerId : IEquatable<PeerId>, IComparable<PeerId>
{
    public const int Size = 16;
    [FieldOffset(0)] private readonly ulong _lo;
    [FieldOffset(8)] private readonly ulong _hi;

    [FieldOffset(0)] private readonly int _a;
    [FieldOffset(4)] private readonly short _b;
    [FieldOffset(6)] private readonly short _c;
    [FieldOffset(8)] private readonly byte _d;
    [FieldOffset(9)] private readonly byte _e;
    [FieldOffset(10)] private readonly byte _f;
    [FieldOffset(11)] private readonly byte _g;
    [FieldOffset(12)] private readonly byte _h;
    [FieldOffset(13)] private readonly byte _i;
    [FieldOffset(14)] private readonly byte _j;
    [FieldOffset(15)] private readonly byte _k;

    public PeerId(byte* peerId)
    {
        this = *(PeerId*)peerId;
    }

    public override int GetHashCode()
    {
        ulong combined = _lo ^ _hi;
        return (int)(combined ^ (combined >> 32));
    }

    public bool Equals(PeerId other)
    {
        return _lo == other._lo && _hi == other._hi;
    }

    public override bool Equals(object obj)
    {
        return obj is PeerId p && Equals(other: p);
    }

    public override string ToString()
    {
        return new Guid(_a, _b, _c, _d, _e, _f, _g, _h, _i, _j, _k).ToString().Substring(0, 8);
    }

    public string ToVerboseString()
    {
        return new Guid(_a, _b, _c, _d, _e, _f, _g, _h, _i, _j, _k).ToString();
    }

    // Inverse of ToVerboseString (accepts any format Guid.TryParse accepts).
    // The short ToString() form is truncated and cannot round-trip.
    public static bool TryParse(string? value, out PeerId peerId)
    {
        if (Guid.TryParse(value, out Guid guid))
        {
            // Guid.ToByteArray lays out {int, short, short, byte[8]} little-endian,
            // which matches this struct's field layout on the little-endian
            // machines the protocol already requires (Network rejects big-endian).
            byte[] bytes = guid.ToByteArray();
            fixed (byte* p = bytes)
            {
                peerId = new PeerId(p);
            }
            return true;
        }
        peerId = default;
        return false;
    }

    public static bool Equals(in PeerId p1, in PeerId p2)
    {
        return p1._lo == p2._lo && p1._hi == p2._hi;
    }

    public int CompareTo(PeerId other)
    {
        // Byte-lexicographic comparison, little-endian interpretation.
        // SIDE NOTE: The `!= then <?-1:1` structure is what enables a compiler
        // to potentially emit branchless sign generation (sbb+or or cmov).
        ulong a = BitConverter.IsLittleEndian ? _lo : BinaryPrimitives.ReverseEndianness(_lo);
        ulong b = BitConverter.IsLittleEndian ? other._lo : BinaryPrimitives.ReverseEndianness(other._lo);
        if (a != b) return a < b ? -1 : 1;
        a = BitConverter.IsLittleEndian ? _hi : BinaryPrimitives.ReverseEndianness(_hi);
        b = BitConverter.IsLittleEndian ? other._hi : BinaryPrimitives.ReverseEndianness(other._hi);
        if (a != b) return a < b ? -1 : 1;
        return 0;
    }
}
using System.Net;
using System.Runtime.InteropServices;
using System;

namespace Tanks.Net;

public enum NetAddressFamily : byte
{
    None = 0,
    IPv4 = 1,
    IPv6 = 2,
}

// Allocation-free address + port: what IPEndPoint is, minus the GC.
// The transports produce and consume this on the hot path; IPEndPoint only
// appears at cold API edges (Connect, Bind, logging).
// IPv4-mapped IPv6 addresses (::ffff:a.b.c.d) canonicalize to IPv4 so the
// same peer compares equal regardless of which stack delivered the packet.
[StructLayout(LayoutKind.Explicit, Size = Size)]
public unsafe struct NetAddress : IEquatable<NetAddress>
{
    public const int Size = 20;

    [FieldOffset(0)] public NetAddressFamily Family;
    [FieldOffset(2)] public ushort Port; // host byte order
    [FieldOffset(4)] public fixed byte Addr[16]; // IPv4 uses the first 4 bytes; rest stay zero

    // IPv4 overlay for fast copying.
    [FieldOffset(4)] internal uint Addr4;

    // IPv6 overlays for fast copying/equality/hashing.
    [FieldOffset(4)] internal ulong Addr16Low;
    [FieldOffset(12)] internal ulong Addr16High;

    // Canonicalize overlays
    [FieldOffset(4)] private ulong _a_0_8;
    [FieldOffset(12)] private ushort _a_8_2;
    [FieldOffset(14)] private ushort _a_10_2;
    [FieldOffset(16)] private uint _a_12_4;
    [FieldOffset(8)] private ulong _a_4_8;
    [FieldOffset(4)] private uint _a_0_4;

    public readonly bool Equals(NetAddress other)
    {
        return Family == other.Family
            && Port == other.Port
            && Addr16Low == other.Addr16Low
            && Addr16High == other.Addr16High;
    }

    public override readonly bool Equals(object? obj) => obj is NetAddress other && Equals(other);

    public override readonly int GetHashCode()
    {
        ulong x = Addr16Low ^ Addr16High ^ ((ulong)Port << 32) ^ (ulong)Family;
        return (int)(x ^ (x >> 32));
    }

    // Turns ::ffff:a.b.c.d into plain IPv4. No-op otherwise.
    public void Canonicalize()
    {
        if (Family != NetAddressFamily.IPv6)
            return;
        
        if (_a_0_8 != 0) return; // bytes 0-7 must be zero
        if (_a_8_2 != 0) return; // bytes 8-9 must be zero
        if (_a_10_2 != 0xFFFF) return; // bytes 10-11 must be 0xFFFF
        _a_0_4 = _a_12_4; // move the v4 bytes down
        _a_4_8 = 0;
        _a_12_4 = 0;
        Family = NetAddressFamily.IPv4;
    }

    // Cold path (Connect commands, Bind). Not for per-packet use.
    public static NetAddress FromIPEndPoint(IPEndPoint endpoint)
    {
        ThrowHelper.ThrowIfNull(endpoint);

        var result = new NetAddress { Port = (ushort)endpoint.Port };
        if (!endpoint.Address.TryWriteBytes(new Span<byte>(result.Addr, 16), out int written) ||
            (written != 4 && written != 16))
        {
            throw new ArgumentException($"Unsupported address family: {endpoint.AddressFamily}", nameof(endpoint));
        }
        result.Family = written == 4 ? NetAddressFamily.IPv4 : NetAddressFamily.IPv6;
        result.Canonicalize();
        return result;
    }

    // Cold path (Bind, diagnostics). Allocates.
    public readonly IPEndPoint ToIPEndPoint()
    {
        fixed (byte* a = Addr)
        {
            IPAddress ip = Family == NetAddressFamily.IPv4
                ? new IPAddress(new ReadOnlySpan<byte>(a, 4))
                : new IPAddress(new ReadOnlySpan<byte>(a, 16));
            return new IPEndPoint(ip, Port);
        }
    }

    // Cold path (diagnostics). Allocates.
    public override readonly string ToString()
    {
        return Family == NetAddressFamily.None ? "(none)" : ToIPEndPoint().ToString();
    }

    internal void ClearHighBytes()
    {
        _a_4_8 = 0;
        _a_12_4 = 0;
    }
}

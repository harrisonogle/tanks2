using System.Runtime.InteropServices;

namespace Tanks.Net;

[StructLayout(LayoutKind.Explicit, Size = Size)]
public unsafe struct PacketHeader
{
    public const int Size = 52;
    [FieldOffset(0)] public fixed byte Magic[6]; // t, a, n, k, s, !
    [FieldOffset(6)] public byte MajorVersion;
    [FieldOffset(7)] public byte MinorVersion;
    [FieldOffset(8)] public PeerId PeerId; // TODO: only required during handshake; candidate for removal in "short header"
    [FieldOffset(24)] public SessionId SSID; // TODO: can use "short/long headers" later if desired
    [FieldOffset(32)] public SessionId DSID; // zeroed during PING
    [FieldOffset(40)] public PacketType Type;
    [FieldOffset(41)] public PacketFlags Flags;
    // 42-43 reserved
    // Per-direction monotonic send counter; nonce material for packet auth
    // (never reused, incremented per send attempt). Covered by the auth tag,
    // and implicitly verified again by being the nonce: a tampered Seq
    // derives the wrong one-time key and the tag fails either way.
    [FieldOffset(44)] public ulong Seq;
}
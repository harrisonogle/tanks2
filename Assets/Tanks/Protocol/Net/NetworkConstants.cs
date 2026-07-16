using System;

namespace Tanks.Net;

public static class NetworkConstants
{
    public const byte MajorVersion = 1;
    public const byte MinorVersion = 0;
    public const int AuthTagLength = 16; // Poly1305 tag
    public const int MaxDatagramSize = 1200; // same as QUIC; prevents IP fragmentation with breathing room
    public const int MaxApplicationDataLength = MaxDatagramSize - PacketHeader.Size - AuthTagLength; // 1132
    public const int ApplicationDataSlotSize = 1280; // pooled datagram slot size, aligned to typical cache line size +1 extra cache line
    public const int PipeEventHeadroom = PipeEventMetadata.Size; // headroom reserved for pipe events

    // TODO: increase timeouts after protocol testing (or parameterize them now)
    public static readonly TimeSpan SessionIdleTimeout = TimeSpan.FromSeconds(10); // session dies if no non-keepalive messages within this timeout
    public static readonly TimeSpan KeepAliveInterval = TimeSpan.FromSeconds(4); // to "disable" keepalive, make it higher than session idle timeout
    public static readonly TimeSpan HedgeTimeout = TimeSpan.FromSeconds(5);
    public static readonly TimeSpan HedgeInterval = TimeSpan.FromMilliseconds(250);

    // Compiler bakes this directly into the assembly's compiled
    // binary data section (no allocation).
    public static ReadOnlySpan<byte> Magic => new byte[]
    {
        0x74, // t
        0x61, // a
        0x6E, // n
        0x6B, // k
        0x73, // s
        0x21, // !
    };

    public static ReadOnlySpan<byte> SessionKeyInfo => new byte[]
    {
        0x74, // t
        0x61, // a
        0x6E, // n
        0x6B, // k
        0x73, // s
        0x21, // !
        0x73, // s
        0x65, // e
        0x73, // s
        0x73, // s
        0x69, // i
        0x6F, // o
        0x6E, // n
        0x6B, // k
        0x65, // e
        0x79, // y
    };
}
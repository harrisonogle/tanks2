using System.Buffers.Binary;
using System.Runtime.InteropServices;
using System;

namespace Tanks.Net;

[StructLayout(LayoutKind.Explicit, Size = Size)]
public unsafe struct SockAddrIn
{
    public const int Size = 16;
    private const ushort AfInet = 2; // AF_INET, same on all supported OSes

    [FieldOffset(0)] public fixed byte Header[2];
    [FieldOffset(2)] private ushort _port;
    [FieldOffset(4)] public fixed byte Addr[4];

    public ushort Port
    {
        get => BitConverter.IsLittleEndian ? BinaryPrimitives.ReverseEndianness(_port) : _port;
        set => _port = BitConverter.IsLittleEndian ? BinaryPrimitives.ReverseEndianness(value) : value;
    }

    public static void Init(SockAddrIn* p)
    {
        // Zero the whole 16-byte struct (padding tail must be zero on some kernels).
        *p = default;

        byte* dst = (byte*)p;

        // BSD-derived (macOS/iOS/tvOS/watchOS/FreeBSD) put sa_len at byte 0
        // and family at byte 1. Linux/Windows put family as u16 at byte 0.
        if (OSPlatforms.IsBsdLayout)
        {
            dst[0] = Size; // sa_len
            dst[1] = (byte)AfInet; // sa_family
        }
        else
        {
            *(ushort*)dst = AfInet; // sin_family (u16, host byte order)
        }
    }
}
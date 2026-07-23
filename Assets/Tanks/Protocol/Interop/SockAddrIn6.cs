using System.Buffers.Binary;
using System.Runtime.InteropServices;
using System;

namespace Tanks.Net;

[StructLayout(LayoutKind.Explicit, Size = Size)]
public unsafe struct SockAddrIn6
{
    public const int Size = 28;

    // AF_INET6 differs per OS.
    internal static readonly ushort AfInet6 =
        RuntimeInformation.IsOSPlatform(OSPlatform.Windows)       ? (ushort)23 :
        RuntimeInformation.IsOSPlatform(OSPlatform.OSX)           ? (ushort)30 :
        RuntimeInformation.IsOSPlatform(OSPlatforms.iOS)          ? (ushort)30 :
        RuntimeInformation.IsOSPlatform(OSPlatforms.tvOS)         ? (ushort)30 :
        RuntimeInformation.IsOSPlatform(OSPlatforms.watchOS)      ? (ushort)30 :
        RuntimeInformation.IsOSPlatform(OSPlatforms.MacCatalyst)  ? (ushort)30 :
        RuntimeInformation.IsOSPlatform(OSPlatforms.FreeBSD)      ? (ushort)28 :
        /* Linux/Android */                                         (ushort)10;

    [FieldOffset(0)] public fixed byte Header[2];
    [FieldOffset(2)] private ushort _port;
    [FieldOffset(4)] public fixed byte FlowInfo[4];
    [FieldOffset(8)] public fixed byte Addr[16];
    [FieldOffset(24)] public uint ScopeId; // host order always

    public ushort Port
    {
        get => BitConverter.IsLittleEndian ? BinaryPrimitives.ReverseEndianness(_port) : _port;
        set => _port = BitConverter.IsLittleEndian ? BinaryPrimitives.ReverseEndianness(value) : value;
    }

    public static void Init(SockAddrIn6* p)
    {
        // Zero the whole 28-byte struct (padding tail must be zero on some kernels).
        *p = default;

        byte* dst = (byte*)p;

        if (OSPlatforms.IsBsdLayout)
        {
            dst[0] = Size; // sin6_len
            dst[1] = (byte)AfInet6;// sin6_family
        }
        else
        {
            *(ushort*)dst = AfInet6; // sin6_family (u16)
        }
    }
}
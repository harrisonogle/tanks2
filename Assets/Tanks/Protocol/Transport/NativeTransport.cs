using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System;

namespace Tanks.Net;

// This exists because System.Net.Sockets.Socket did not land Span<byte>
// overloads for ReceiveFrom or SendTo until .NET 8, and this library is
// zero-copy everywhere, so copying to a byte[] array is not okay.
// When Unity lands CoreCLR (supposedly late 2026), we can target modern
// .NET, throw this component away, and just use Socket's span APIs.
public sealed unsafe class NativeTransport : IDatagramTransport, IDisposable
{
    private const ushort AfInet = 2; // AF_INET, same on all supported OSes

    private readonly Socket _socket;
    private readonly bool _dualMode;
    private NetAddress _localEndPoint;

    public NativeTransport()
    {
        // Prefer a dual-stack (IPv4 + IPv6) socket; fall back to IPv4-only.
        Socket? socket = null;
        bool dualMode = false;

        if (Socket.OSSupportsIPv6)
        {
            try
            {
                socket = new Socket(AddressFamily.InterNetworkV6, SocketType.Dgram, ProtocolType.Udp);
                socket.DualMode = true; // clears IPV6_V6ONLY
                dualMode = true;
            }
            catch (SocketException)
            {
                socket?.Dispose();
                socket = null;
            }
            catch (NotSupportedException)
            {
                socket?.Dispose();
                socket = null;
            }
        }

        socket ??= new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        socket.Blocking = false;

        if (OSPlatforms.IsWindows)
        {
            // Windows-only: an ICMP port-unreachable from a previous sendto surfaces as
            // WSAECONNRESET on the next recvfrom. Happens routinely when we Ping a peer
            // that hasn't bound yet; disable so it doesn't throw out of TryReceive.
            socket.IOControl(-1744830452, new byte[] { 0, 0, 0, 0 }, null);
            // -1744830452 == unchecked((int)0x9800000C) == SIO_UDP_CONNRESET
        }

        _socket = socket;
        _dualMode = dualMode;
    }

    public ref readonly NetAddress LocalEndPoint => ref _localEndPoint;

    public bool DualMode => _dualMode;

    public void Bind(in NetAddress local)
    {
        // One-time; the managed Bind (and its allocations) are fine here.
        IPEndPoint ep = local.ToIPEndPoint();
        if (_dualMode && ep.AddressFamily == AddressFamily.InterNetwork)
            ep = new IPEndPoint(ep.Address.MapToIPv6(), ep.Port);
        _socket.Bind(ep);
        _localEndPoint = local;
    }

    public void Close()
    {
        _socket.Close();
    }

    public void Dispose()
    {
        _socket.Dispose();
    }

    public bool PollRead(int microseconds)
    {
        return _socket.Poll(microseconds, SelectMode.SelectRead);
    }

    public bool TryReceive(
        byte* buffer,
        int length,
        out int bytesRead,
        ref NetAddress source)
    {
        ThrowHelper.ThrowIfNegative(length);

        // sockaddr_in6 covers both families (sockaddr_in is smaller).
        byte* srcAddr = stackalloc byte[SockAddrIn6.Size];
        int addrLen = SockAddrIn6.Size;
        int bytesReceived;

        if (OSPlatforms.IsWindows)
        {
            bytesReceived = NativeWindows.RecvFrom(
                _socket.Handle, buffer, length, 0, srcAddr, &addrLen);
        }
        else
        {
            uint uaddrLen = (uint)addrLen;
            bytesReceived = (int)NativeUnix.RecvFrom(
                (int)_socket.Handle, buffer, (nuint)length, 0, srcAddr, &uaddrLen);
            addrLen = (int)uaddrLen;
        }

        if (bytesReceived < 0)
        {
            int err = Marshal.GetLastWin32Error();

            // EAGAIN=11 (Linux), 35 (macOS), WSAEWOULDBLOCK=10035 (Windows)
            if (err == 11 || err == 35 || err == 10035)
            {
                bytesRead = 0;
                return false;
            }

            // WSAEMSGSIZE=10040 (Windows): datagram larger than the buffer.
            // Unix truncates silently instead; either way the datagram is
            // consumed by the OS, so treat it as "nothing valid received".
            if (err == 10040)
            {
                bytesRead = 0;
                return false;
            }

            throw new SocketException(err);
        }

        if (bytesReceived > length)
        {
            bytesRead = default;
            return false;
        }

        // Parse the source sockaddr into a NetAddress - no allocation.
        ushort family = OSPlatforms.IsBsdLayout ? srcAddr[1] : *(ushort*)srcAddr;

        if (family == AfInet && addrLen >= SockAddrIn.Size)
        {
            var sa = (SockAddrIn*)srcAddr;
            source.Family = NetAddressFamily.IPv4;
            source.Port = sa->Port;
            source.Addr4 = *(uint*)sa->Addr;
            source.ClearHighBytes(); // not strictly necessary unless we use multiple families
        }
        else if (family == SockAddrIn6.AfInet6 && addrLen >= SockAddrIn6.Size)
        {
            var sa6 = (SockAddrIn6*)srcAddr;
            source.Family = NetAddressFamily.IPv6;
            source.Port = sa6->Port;
            source.Addr16Low = *(ulong*)sa6->Addr;
            source.Addr16High = *(ulong*)(sa6->Addr + 8);
            source.Canonicalize(); // v4 senders over dual-stack arrive v4-mapped
        }
        else
        {
            // Unknown family; the datagram is already consumed - drop it.
            bytesRead = default;
            return false;
        }

        bytesRead = bytesReceived;

        return true;
    }

    public bool TrySend(byte* datagram, int length, ref NetAddress destination)
    {
        ThrowHelper.ThrowIfNegative(length);

        byte* toAddr = stackalloc byte[SockAddrIn6.Size];
        int toLen;

        if (_dualMode)
        {
            // Dual-stack socket sends everything as AF_INET6; IPv4
            // destinations go v4-mapped (::ffff:a.b.c.d).
            var sa6 = (SockAddrIn6*)toAddr;
            SockAddrIn6.Init(sa6);
            sa6->Port = destination.Port;

            if (destination.Family == NetAddressFamily.IPv4)
            {
                *(ulong*)sa6->Addr = 0;
                *(ushort*)(sa6->Addr + 8) = 0;
                *(ushort*)(sa6->Addr + 10) = 0xFFFF;
                *(uint*)(sa6->Addr + 12) = destination.Addr4;
            }
            else if (destination.Family == NetAddressFamily.IPv6)
            {
                *(ulong*)sa6->Addr = destination.Addr16Low;
                *(ulong*)(sa6->Addr + 8) = destination.Addr16High;
            }
            else
            {
                return false;
            }

            toLen = SockAddrIn6.Size;
        }
        else
        {
            // IPv4-only fallback socket can't reach IPv6 destinations.
            if (destination.Family != NetAddressFamily.IPv4)
                return false;

            var sa = (SockAddrIn*)toAddr;
            SockAddrIn.Init(sa);
            sa->Port = destination.Port;
            *(uint*)sa->Addr = destination.Addr4;
            toLen = SockAddrIn.Size;
        }

        int bytesSent;

        if (OSPlatforms.IsWindows)
        {
            bytesSent = NativeWindows.SendTo(
                _socket.Handle, datagram, length, flags: 0, toAddr, toLen);
        }
        else
        {
            bytesSent = (int)NativeUnix.SendTo(
                (int)_socket.Handle, datagram, (nuint)length, flags: 0, toAddr, (uint)toLen);
        }

        if (bytesSent < 0)
        {
            int err = Marshal.GetLastWin32Error();

            // EAGAIN=11 (Linux), 35 (macOS), WSAEWOULDBLOCK=10035 (Windows)
            if (err == 11 || err == 35 || err == 10035)
            {
                return false;
            }

            throw new SocketException(err);
        }

        Debug.Assert(bytesSent == length);

        return true;
    }
}

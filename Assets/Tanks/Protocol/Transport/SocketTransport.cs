using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System;

namespace Tanks.Net;

// Designed for use by one thread.
public sealed unsafe class SocketTransport : IDatagramTransport, IDisposable
{
    private readonly Socket _socket;
    private readonly bool _dualMode;

    // Temporary until Unity lands CoreCLR (late 2026) and we can use
    // `Span<byte>` in `Socket` APIs. Until then, we have to abandon
    // zero-copy (unless we P/Invoke).
    private readonly byte[] _temporaryDepression; // NOT the final shape

    private NetAddress _localEndPoint;

    // Cache the last destination so steady-state sends (one peer) don't
    // allocate an IPEndPoint per packet.
    private NetAddress _lastDestination;
    private IPEndPoint? _lastDestinationEp;

    // ReceiveFrom requires a non-null remoteEP whose family matches the socket;
    // it's replaced (not filled in) on return, so one sentinel can seed every call.
    private readonly EndPoint _receiveSentinel;

    public SocketTransport()
    {
        // Prefer a dual-stack (IPv4 + IPv6) socket; fall back to IPv4-only.
        // By default AF_INET6 is dual-stack on non-Windows, so need
        // to set a socket option on Windows: setsockopt(IPV6_V6ONLY, 0)
        // (DualMode does that for us).
        Socket? socket = null;
        bool dualMode = false;

        if (Socket.OSSupportsIPv6)
        {
            try
            {
                socket = new Socket(AddressFamily.InterNetworkV6, SocketType.Dgram, ProtocolType.Udp);
                socket.DualMode = true;
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

        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            // Windows-only: SocketError.ConnectionReset (WSAECONNRESET) on a UDP socket
            // means "a previous sendto got an ICMP port-unreachable back." It's noisy
            // on Windows and useless most of the time.
            // If we didn't disable, we'd see see spurious ConnectionReset throws on
            // Windows when the peer's port is briefly not listening.
            socket.IOControl(-1744830452, new byte[] { 0, 0, 0, 0 }, null);
            // -1744830452 == unchecked((int)0x9800000C) == SIO_UDP_CONNRESET
        }

        _socket = socket;
        _dualMode = dualMode;
        _temporaryDepression = new byte[NetworkConstants.MaxDatagramSize + 1];
        _receiveSentinel = new IPEndPoint(dualMode ? IPAddress.IPv6Any : IPAddress.Any, 0);
    }

    public ref readonly NetAddress LocalEndPoint => ref _localEndPoint;
    public bool DualMode => _dualMode;

    public void Bind(in NetAddress local)
    {
        IPEndPoint ep = local.ToIPEndPoint();
        if (_dualMode && ep.AddressFamily == AddressFamily.InterNetwork)
            ep = new IPEndPoint(ep.Address.MapToIPv6(), ep.Port);
        _socket.Bind(ep);
        _localEndPoint = local;
    }

    public void Close() => _socket.Close();
    public void Dispose() => _socket.Dispose();
    public bool PollRead(int microseconds) => _socket.Poll(microseconds, SelectMode.SelectRead);

    public bool TryReceive(
        byte* buffer,
        int length,
        out int bytesRead,
        ref NetAddress source)
    {
        ThrowHelper.ThrowIfNegative(length);

        if (!_socket.Poll(microSeconds: 0, mode: SelectMode.SelectRead))
        {
            bytesRead = default;
            return false;
        }

        // Placeholder until Unity lands CoreCLR (late 2026) and we can use
        // `Span<byte>`/SocketAddress in `Socket` APIs. ReceiveFrom allocates
        // an IPEndPoint per call on netstandard2.1; NativeTransport is the
        // allocation-free path.
        EndPoint ep = _receiveSentinel;
        bytesRead = _socket.ReceiveFrom(
            _temporaryDepression,
            Math.Min(_temporaryDepression.Length, length),
            default,
            ref ep);

        if (ep is IPEndPoint ip)
        {
            source = NetAddress.FromIPEndPoint(ip); // canonicalizes v4-mapped
        }
        else
        {
            return false;
        }

        new Span<byte>(_temporaryDepression, 0, bytesRead).CopyTo(
            new Span<byte>(buffer, length));

        return true;
    }

    public bool TrySend(byte* datagram, int length, ref NetAddress destination)
    {
        ThrowHelper.ThrowIfNegative(length);

        if (!_dualMode && destination.Family != NetAddressFamily.IPv4)
            return false; // IPv4-only fallback socket can't reach IPv6

        // Steady state is one peer; rebuild the IPEndPoint only when the
        // destination changes. (Dual-mode sockets accept IPv4 IPEndPoints
        // directly and map them internally.)
        if (_lastDestinationEp is null || !destination.Equals(_lastDestination))
        {
            _lastDestinationEp = destination.ToIPEndPoint();
            _lastDestination = destination;
        }

        // Placeholder until Unity lands CoreCLR (late 2026) and we can use
        // `Span<byte>` in `Socket` APIs. Until then, we have to abandon
        // zero-copy (unless we P/Invoke).
        new ReadOnlySpan<byte>(datagram, length).CopyTo(
            new Span<byte>(_temporaryDepression));

        try
        {
            int bytesWritten = _socket.SendTo(_temporaryDepression, length, default, _lastDestinationEp);

            Debug.Assert(bytesWritten == length);

            return true;
        }
        catch (SocketException ex) when (ex.SocketErrorCode == SocketError.WouldBlock)
        {
            // No space in OS buffer.
            // For UDP it should never happen unless outbound throughput
            // is so high that we've exhausted the NIC's memory
            return false;
        }
    }
}

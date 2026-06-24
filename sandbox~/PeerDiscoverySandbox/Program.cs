using System.Buffers.Binary;
using System.Diagnostics;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;

Guid selfId = Guid.NewGuid(); // nonce
var multicastAddr = IPAddress.Parse("239.255.42.42"); // multicast group. 239.255.* is the prefix
var multicastPort = 42424; // multicast port
var multicastIfAddrs = GetMulticastInterfaces(); // loopback, ethernet, and WiFi

Console.WriteLine($"Self peer ID: {selfId}");

// Create a UDP multicast socket to discover peers
var multicastSocket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);

// Allow port reuse so different processes on this machine can do multicast
multicastSocket.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, 1);

// Must bind before joining groups on interfaces
multicastSocket.Bind(new IPEndPoint(IPAddress.Any, multicastPort));
IPEndPoint multicastSocketEndPoint = (IPEndPoint)multicastSocket.LocalEndPoint!;

// Max number of router hops of outbound sends
multicastSocket.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.MulticastTimeToLive, 15);

// Receive our own sends; supports localhost peers
multicastSocket.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.MulticastLoopback, 1);

// Join the group on each of the NICs
// Lets us "listen" on loopback, ethernet, and WiFi
// Only needs to be done once on the socket
foreach (var addr in multicastIfAddrs)
    multicastSocket.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.AddMembership, new MulticastOption(multicastAddr, addr));

// Create a UDP unicast socket for netcode between peers
using var unicastSocket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
unicastSocket.Bind(new IPEndPoint(IPAddress.Any, 0)); // 0 = let the OS pick a port
IPEndPoint unicastEndPoint = (IPEndPoint)unicastSocket.LocalEndPoint!;

using var cancellation = new CancellationTokenSource();
new Thread(MulticastLoop) { IsBackground = true }.Start();
new Thread(UnicastLoop) { IsBackground = true }.Start();
Repl();

void Repl()
{
    while (!cancellation.IsCancellationRequested)
    {
        Console.WriteLine("Enter 'q' to quit.");
        if (Console.ReadLine()?.Contains("q") is true)
        {
            Console.WriteLine("Stopping.");
            try { cancellation.Cancel(); } catch { }
            break;
        }
    }
}

void MulticastLoop()
{
    // Dumb multicast protocol: UTF8("hello ") + ID + port
    //                          = 6 + 16 + 4 = 26 bytes
    const int MulticastMessagePrefix = 6;
    const int MulticastMessageLength = MulticastMessagePrefix + 16 + 4;
    var buffer = new byte[MulticastMessageLength];

    long prevTimestamp = Stopwatch.GetTimestamp();

    while (!cancellation.IsCancellationRequested)
    {
        Console.WriteLine($"[multicast] Broadcasting local address {unicastEndPoint.Address}:{unicastEndPoint.Port}");

        // When sending multicast to multiple interfaces, must send per interface, and need to set socket option
        // Alternative is using multiple sockets, but that's supposedly less efficient
        foreach (var addr in multicastIfAddrs)
        {
            multicastSocket.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.MulticastInterface, addr.GetAddressBytes());

            var outbound = new Span<byte>(buffer, 0, MulticastMessageLength);

            if (!Encoding.UTF8.TryGetBytes("hello ", outbound[..MulticastMessagePrefix], out int prefixBytesWritten))
            {
                Console.WriteLine($"[multicast] Failed to write multicast prefix bytes. (expected {MulticastMessagePrefix}; wrote {prefixBytesWritten})");
                try { cancellation.Cancel(); } catch { }
                break;
            }
            outbound = outbound[MulticastMessagePrefix..]; // move the "write cursor"

            if (!selfId.TryWriteBytes(outbound, bigEndian: true, out int selfIdBytesWritten) ||
                selfIdBytesWritten != 16)
            {
                Console.WriteLine($"[multicast] Failed to write self ID bytes. (expected 16; wrote {selfIdBytesWritten})");
                try { cancellation.Cancel(); } catch { }
                break;
            }
            outbound = outbound[16..];

            BinaryPrimitives.WriteInt32BigEndian(outbound, unicastEndPoint.Port);

            int totalBytesSent = 0;
            while (totalBytesSent < MulticastMessageLength)
            {
                int bytesSent = multicastSocket.SendTo(buffer.AsSpan(totalBytesSent, MulticastMessageLength), new IPEndPoint(multicastAddr, multicastPort));
                // Console.WriteLine($"[multicast] Sent {bytesSent} byte(s) via interface {addr} to {multicastAddr}:{multicastPort}.");
                if (bytesSent <= 0)
                    break;
                totalBytesSent += bytesSent;
            }
        }

        if (cancellation.IsCancellationRequested)
            break;

        // Wait for data on the multicast socket, with timeout
        //   true := received data
        //   false := timed out
        bool received = multicastSocket.Poll(TimeSpan.FromSeconds(5), SelectMode.SelectRead);

        if (received)
        {
            EndPoint from = new IPEndPoint(IPAddress.Any, 0);
            int bytesReceived = multicastSocket.ReceiveFrom(buffer, ref from);
            Console.WriteLine($"[multicast] Received {bytesReceived} multicast bytes from {from}: {Convert.ToBase64String(buffer, 0, bytesReceived)}");
            if (from is not IPEndPoint fromEndPoint)
            {
                Console.WriteLine($"[multicast] Invalid endpoint: {from}");
                try { cancellation.Cancel(); } catch { }
                break;
            }

            // Validate the inbound multicast message
            if (bytesReceived != MulticastMessageLength ||
                Encoding.UTF8.GetString(buffer, 0, MulticastMessagePrefix) != "hello ")
            {
                Console.WriteLine($"Received bad multicast message from '{from}' ({fromEndPoint.Address}:{fromEndPoint.Port}).");
                try { cancellation.Cancel(); } catch { }
                break;
            }

            // Read the inbound multicast message IP and port
            int peerIdOffset  = MulticastMessagePrefix; // start reading the peerId after the prefix
            int portOffset = peerIdOffset + 16; // start reading the port after the peer ID
            IPAddress remoteAddr = fromEndPoint.Address;
            var remotePeerId = new Guid(buffer.AsSpan(peerIdOffset, 16), bigEndian: true);
            int remotePort = BinaryPrimitives.ReadInt32BigEndian(buffer.AsSpan(start: portOffset, length: 4));

            if (cancellation.IsCancellationRequested)
                break;

            if (remotePeerId == selfId)
            {
                // Console.WriteLine($"[multicast] Ignoring message from self.");
            }
            else
            {
                // Let's just send one unicast "ping" and then exit
                int bytesSent = unicastSocket.SendTo(Encoding.UTF8.GetBytes("ping"), new IPEndPoint(remoteAddr, remotePort));
                Console.WriteLine($"[multicast] Discovered peer {remotePeerId} from {remoteAddr}:{remotePort}");
                Console.WriteLine($"[multicast] Initiated handshake 'ping' with remote peer {remotePeerId} (sent {bytesSent} bytes).");
                return; // exit multicast loop
            }
        }

        TimeSpan elapsed = Stopwatch.GetElapsedTime(prevTimestamp);
        TimeSpan minDelay = TimeSpan.FromSeconds(5);
        if (elapsed < minDelay)
        {
            Thread.Sleep(minDelay - elapsed);
        }
        prevTimestamp = Stopwatch.GetTimestamp();
    }
}

void UnicastLoop()
{
    // Dumb unicast handshake protocol: initiator sends "ping", responder returns "pong"
    const int UnicastMessageLength = 4;
    byte[] buffer = new byte[UnicastMessageLength];

    while (!cancellation.IsCancellationRequested)
    {
        // Wait for handshake initiation from remote peer, with timeout
        Console.WriteLine("[unicast] Listening for remote peer.");
        bool received = unicastSocket.Poll(TimeSpan.FromSeconds(5), SelectMode.SelectRead);
        if (received)
        {
            EndPoint from = new IPEndPoint(IPAddress.Any, 0);
            int bytesReceived = unicastSocket.ReceiveFrom(buffer, ref from);
            Console.WriteLine($"[unicast] Received {bytesReceived} bytes from {from}: {Convert.ToBase64String(buffer[..bytesReceived])}");
            if (bytesReceived != UnicastMessageLength)
            {
                Console.WriteLine("[unicast] Received invalid data from remote peer.");
                try { cancellation.Cancel(); } catch { }
                break;
            }
            string inboundHandshakeMessage = Encoding.UTF8.GetString(buffer[..UnicastMessageLength]);
            Console.WriteLine($"[unicast] Peer handshake: {inboundHandshakeMessage}");
            switch (inboundHandshakeMessage)
            {
                case "ping":
                    {
                        Console.WriteLine($"[unicast] Sending handshake response 'pong' to {from}.");
                        unicastSocket.SendTo(Encoding.UTF8.GetBytes("pong"), from);
                        break;
                    }
                case "pong":
                    {
                        Console.WriteLine($"[unicast] Handshake completed! Ready to start game.");
                        break;
                    }
                default:
                    {
                        Console.WriteLine($"[unicast] Received invalid handshake message from remote peer: {inboundHandshakeMessage}");
                        try { cancellation.Cancel(); } catch { }
                        return;
                    }
            }
        }
    }
}

static ICollection<IPAddress> GetMulticastInterfaces()
{
    var list = new List<IPAddress>() { IPAddress.Loopback };

    // Join on LAN and loopback.
    // Easiest way is to listen on every NIC, but don't do that because
    // it's bad to do over corporate VPNs.
    foreach (NetworkInterface nic in NetworkInterface.GetAllNetworkInterfaces())
    {
        if (nic.OperationalStatus != OperationalStatus.Up)
            continue;

        if (!nic.SupportsMulticast)
            continue;

        if (!IsPhysical(nic))
            continue;

        if (nic.NetworkInterfaceType != NetworkInterfaceType.Wireless80211 &&
            nic.NetworkInterfaceType != NetworkInterfaceType.Ethernet &&
            nic.NetworkInterfaceType != NetworkInterfaceType.GigabitEthernet)
        {
            continue;
        }

        foreach (var addr in nic.GetIPProperties().UnicastAddresses)
        {
            // Filter out non-IPv4 and loopback
            if (addr.Address.AddressFamily != AddressFamily.InterNetwork ||
                IPAddress.IsLoopback(addr.Address))
            {
                continue;
            }

            try
            {
                list.Add(addr.Address);
                Console.WriteLine($"Joining address '{addr.Address}' on NIC '{nic.Name}'.");
            }
            catch (SocketException ex)
            {
                Console.WriteLine($"Failed to join address '{addr.Address}' on NIC '{nic.Name}'. ({ex.Message})");
            }
        }
    }

    return list;

    static bool IsPhysical(NetworkInterface nic)
    {
        if (OperatingSystem.IsLinux())
            return Directory.Exists($"/sys/class/net/{nic.Name}/device");
        // if (OperatingSystem.IsWindows())
        //     return GetPhysicalMacs_Windows().Contains(
        //         BitConverter.ToString(nic.GetPhysicalAddress().GetAddressBytes()).Replace("-", ""));
        return nic.GetPhysicalAddress().GetAddressBytes().Length > 0; // macOS fallback
    }
}
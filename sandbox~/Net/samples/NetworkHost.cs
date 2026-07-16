using System.Net;
using System.Net.Sockets;
using Tanks.Net;

namespace Tanks.Game;

/// <summary>
/// Owns the protocol service for the lifetime of the app: the UDP socket and the
/// Network poll thread ("a network protocol is a service that's always running").
/// The only thing this class adds is Unity lifecycle — construction at boot,
/// teardown in OnDestroy so the net thread dies when Play mode stops instead of
/// polling the socket forever in the Editor's background.
/// </summary>
public sealed class NetworkHost : IDisposable
{
    // Identifies "tanks lockstep" to the session protocol; peers with a different
    // id/version are rejected during the handshake. Same values as the sandbox driver.
    private const ushort AppProtocolId = 1;
    private const ushort AppProtocolVersion = 1;

    private Network? _network;
    private NativeTransport? _transport;

    /// <summary>The protocol service. Game-thread consumers reach the pipe through this.</summary>
    public Network? Network => _network;

    /// <summary>The UDP port actually bound (basePort, or the first free port above it).</summary>
    public int BoundPort => _network is not null ? _network.LocalEndPoint.Port : -1;

    public void Initialize(int basePort, ILog log)
    {
        if (_network != null)
            throw new InvalidOperationException("NetworkHost is already initialized.");

        // NativeTransport (P/Invoke sendto/recvfrom), not SocketTransport: the managed
        // Socket API has no span overloads on netstandard2.1, and this project doesn't copy.
        var transport = new NativeTransport();
        try
        {
            // The Editor and a standalone build on the same machine both want a
            // port; walk forward from basePort so two local instances can coexist.
            IPAddress bindAddress = transport.DualMode ? IPAddress.IPv6Any : IPAddress.Any;
            SocketException? lastError = null;
            for (int port = basePort; port < basePort + 10; port++)
            {
                try
                {
                    transport.Bind(NetAddress.FromIPEndPoint(new IPEndPoint(bindAddress, port)));
                    lastError = null;
                    break;
                }
                catch (SocketException ex) when (ex.SocketErrorCode == SocketError.AddressAlreadyInUse)
                {
                    lastError = ex;
                }
            }
            if (lastError != null)
                throw new InvalidOperationException($"No free UDP port in {basePort}..{basePort + 9}.", lastError);

            _network = new Network(new NetworkPipeFactory(), transport, TanksPeerCrypto.Instance, log, AppProtocolId, AppProtocolVersion);
        }
        catch
        {
            transport.Dispose();
            throw;
        }

        _transport = transport;
        _network.Start();
    }

    public void Dispose()
    {
        _network?.Dispose(); // Stop()s the net thread before freeing
        _transport?.Dispose();
        _network = null;
        _transport = null;
    }
}

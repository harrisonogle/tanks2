using System;
using System.Net;
using System.Net.Sockets;
using System.Net.NetworkInformation;
using Tanks.Engine;
using Tanks.Net;
using Tanks.Sim;
using UnityEngine;

namespace Tanks.Game;

using Engine = Tanks.Engine.Engine;

public sealed class EngineHost : MonoBehaviour
{
    // Identifies "tanks lockstep" to the session protocol; peers with a different
    // id/version are rejected during the handshake. Same values as the sandbox driver.
    private const ushort AppProtocolId = 1;
    private const ushort AppProtocolVersion = 1;

    private NativeTransport? _transport;
    private Network? _network;
    private Tanks.Engine.Engine? _engine;
    private EngineBus? _bus;
    private ILog? _logger;

    public EngineBus Bus => _bus;
    public ILog Logger => _logger ??= new UnityLog(LogLevel.Debug);
    public int BoundPort => _transport is not null ? _transport.LocalEndPoint.Port : -1;
    public string LocalPeerId => _network is not null ? _network.LocalPeerId.ToVerboseString() : "";

    // Device-backed LEVEL samplers, one per couch seat. Sampled every render frame and
    // shipped over the ring; the engine assigns ticks and derives edges at the mint.
    private UnityInputSource? _sampler0;
    private UnityInputSource? _sampler1;

    public void Initialize(int basePort, ILog log, SimConfig config)
    {
        if (_network != null)
            throw new InvalidOperationException("Network is already initialized.");

        _logger = log;

        // NativeTransport (P/Invoke sendto/recvfrom), not SocketTransport: the managed
        // Socket API has no span overloads on netstandard2.1, and this project doesn't copy.
        var transport = new NativeTransport();
        Engine? engine = null;
        Network? network = null;
        try
        {
            // The Editor and a standalone build on the same machine both want a
            // port; walk forward from basePort so two local instances can coexist.
            IPAddress bindAddress = transport.DualMode ? IPAddress.IPv6Any : IPAddress.Any;
            SocketException lastError = null;
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

            network = new Network(transport, TanksPeerCrypto.Instance, log, AppProtocolId, AppProtocolVersion, NetworkConstants.ApplicationDataSlotCount);
            network.Start();
            engine = new Tanks.Engine.Engine(network, log, config);
            engine.Start();
            var bus = new EngineBus(engine.Umem, log, config);
            _network = network;
            _transport = transport;
            _engine = engine;
            _bus = bus;
        }
        catch
        {
            using (engine)
            using (network)
            using (transport)
            {
                throw;
            }
        }
    }

    private void Update()
    {
        if (_bus is null) return;
        _bus.Pump();

        if (_bus.Phase == PhaseState.GameView)
        {
            // Always ship both seats' levels; the engine ignores seats beyond its local
            // player count (online 1v1 keeps seat 0, couch co-op consumes both).
            _sampler0 ??= new UnityInputSource(0, _bus.Config);
            _sampler1 ??= new UnityInputSource(1, _bus.Config);
            _bus.SendInput(_sampler0.Sample(), _sampler1.Sample());
        }
    }

    private void OnDestroy()
    {
        using (_engine)
        using (_network)
        using (_transport)
        {
            _engine = null;
            _network = null;
            _transport = null;
            _bus = null;
            _logger?.LogInformation("engine host destroyed");
        }
    }
}
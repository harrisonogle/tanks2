using System;
using System.Collections.Generic;
using Tanks.Net;
using Tanks.Sim;

namespace Tanks.Engine;

public ref struct EngineState
{
    public EngineState(Umem umem, ILog logger, Network network, SimConfig config)
    {
        Umem = umem;
        RenderRx = umem.Rx;
        RenderTx = umem.Tx;
        Logger = logger;
        Network = network;
        NetworkRx = Network.Umem.Rx;
        NetworkTx = Network.Umem.Tx;
        Config = config;
        Handle = default;
        Phase = default;
        GameView = default;
        ConnectScreen = default;
        LocalPeer = default!;
        RemotePeers = new();
    }
    public readonly Umem Umem;
    public readonly UmemPool RenderRx;
    public readonly UmemPool RenderTx;
    public readonly UmemPool NetworkRx;
    public readonly UmemPool NetworkTx;
    public readonly ILog Logger;
    public readonly Network Network;
    public readonly SimConfig Config;
    public BufferHandle Handle;
    public PhaseState Phase;
    public GameViewState GameView;
    public ConnectScreenState ConnectScreen;
    public PeerState LocalPeer;
    public Dictionary<SessionId, PeerState> RemotePeers;
}

public struct GameViewState
{
    public PeerState[] Peers;
    public PlayerState[] Players;
    public Arena Arena;
    public GameState GameState;
    public Simulation Simulation;

    // Netcode/tick machinery (per-match; rebuilt by StartMatch)
    public StateHistory History;
    public PlayerInput[] TickInputs;   // scratch: one entry per player slot, fed to Simulation.Tick
    public long LastTimestamp;         // Stopwatch timestamp of the previous pacing step
    public double Accumulator;         // seconds of unsimulated time
    public long LastSendTimestamp;     // Stopwatch timestamp of the last outbound match bundle
    public uint ConfirmedTick;         // every tick <= this ran with authoritative inputs from ALL players
    public uint LastHashCheckedTick;   // desync scan frontier
    public bool DesyncDetected;

    // Visibility counters (logs/HUD)
    public int RollbackCount;
    public long TotalRolledBackTicks;
    public long StalledSteps;
}

public struct ConnectScreenState
{
}
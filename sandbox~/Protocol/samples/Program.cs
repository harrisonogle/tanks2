using System.Buffers;
using System.Diagnostics;
using System.Net;
using System.Runtime.InteropServices;
using System.Text;
using Tanks;
using Tanks.Net;

Stdout.Info("Hello, World!");

if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
    NativeWindows.TimeBeginPeriod(1); // set windows timer period to 1ms, like Unity does (Windows default is 15.4ms)

var ipAddressA = IPAddress.Parse("1.1.1.1");
var ipAddressB = IPAddress.Parse("2.2.2.2");
var portA = 55555;
var portB = 55555;
var ipEndPointA = new IPEndPoint(ipAddressA, portA);
var ipEndPointB = new IPEndPoint(ipAddressB, portB);
var (transportA, transportB) = InMemoryDatagramTransport.Create();
transportA.Bind(NetAddress.FromIPEndPoint(ipEndPointA));
transportB.Bind(NetAddress.FromIPEndPoint(ipEndPointB));
var pipeFactoryA = new NetworkPipeFactory();
var pipeFactoryB = new NetworkPipeFactory();
var cryptoA = TanksPeerCrypto.Instance;
var cryptoB = TanksPeerCrypto.Instance;
var loggerA = new PeerLogger(Stdout.Instance, "A");
var loggerB = new PeerLogger(Stdout.Instance, "B", "            ");
loggerB.SuppressOutput = false;
const ushort AppProtocolId = 1;
const ushort AppProtocolVersion = 1;
using var networkA = new Network(pipeFactoryA, transportA, cryptoA, loggerA, AppProtocolId, AppProtocolVersion);
using var networkB = new Network(pipeFactoryB, transportB, cryptoB, loggerB, AppProtocolId, AppProtocolVersion);

networkA.Start();
networkB.Start();

// shutdown escape hatch - canceling stops everything
using var shutdown = new CancellationTokenSource();

// Canceling these stops the associated game loop
using var stopGameA = CancellationTokenSource.CreateLinkedTokenSource(shutdown.Token);
using var stopGameB = CancellationTokenSource.CreateLinkedTokenSource(shutdown.Token);

var gameThreadA = new Thread(() => GameLoop("A", networkA, () => !loggerA.SuppressOutput, stopGameA.Token)) { IsBackground = true };
var gameThreadB = new Thread(() => GameLoop("B", networkB, () => !loggerB.SuppressOutput, stopGameB.Token)) { IsBackground = true };
gameThreadA.Start();
gameThreadB.Start();

var metricsThread = new Thread(PrintMetricsLoop) { IsBackground = true };
metricsThread.Start();
Repl();

void PrintMetricsLoop()
{
    int seconds = 0;
    while (!shutdown.IsCancellationRequested)
    {
        Thread.Sleep(1000);

        if (++seconds % 5 == 0)
        {
            if (!loggerA.SuppressOutput) PrintMetrics("A", networkA.Metrics);
            if (!loggerB.SuppressOutput) PrintMetrics("B", networkB.Metrics);
        }
    }
}

static void PrintMetrics(string name, NetworkMetrics m)
{
    Stdout.Info(
        $"[M][{name}] rx:{m.RxPackets}p/{m.RxBytes}B tx:{m.TxPackets}p/{m.TxBytes}B " +
        $"drops(mac:{m.RxDroppedBadMac} replay:{m.RxDroppedReplay} state:{m.RxDroppedBadState} ring:{m.RxDroppedRingFull} nosess:{m.TxDroppedNoSession}) " +
        $"stall:{m.RxStalledPoolExhausted} hs(acc:{m.SessionsAccepted} est:{m.SessionsEstablished} closed:{m.SessionsClosed} rej:{m.HandshakesRejected}) " +
        $"lifecycleq:{m.LifecycleEventsQueued}");
}

static unsafe void GameLoop(string name, Network network, Func<bool> enableLogging, CancellationToken ct)
{
    void Log(string message)
    {
        if (enableLogging.Invoke())
            Stdout.Info($"[G][{name}] {message}");
    }

    NetworkPipe pipe = network.Pipe;
    BufferHandle handle = default;
    var sessions = new Dictionary<SessionId, GameSession>();
    long tick = 0;

    try
    {
        while (!ct.IsCancellationRequested)
        {
            long tickBegin = Stopwatch.GetTimestamp();
            tick++;

            while (pipe.RX.Ring.TryDequeue(ref handle))
            {
                try
                {
                    byte* slot = pipe.RX.Pool.Deref(handle);
                    var metadata = (PipeEventMetadata*)slot;
                    byte* payload = slot + NetworkConstants.PipeEventHeadroom + metadata->Offset;
                    int payloadLength = metadata->Length;

                    switch (metadata->Kind)
                    {
                        case PipeEventKind.SessionAccepted:
                            {
                                var msg = (SessionAccepted*)payload;
                                Log($"{metadata->SessionId} recv: {metadata->Kind} (handle: {handle}, slot: {(IntPtr)slot}). LocalPeerId:{msg->LocalPeerId}, LocalSessionId:{msg->LocalSessionId}, RemotePeerId:{msg->RemotePeerId}, RemoteSessionId:{msg->RemoteSessionId}");
                                var session = new GameSession();
                                session.Context.OnSessionAccepted(ref *msg);
                                sessions.Add(msg->LocalSessionId, session);
                            }
                            break;
                        case PipeEventKind.SessionEstablished:
                            {
                                Log($"{metadata->SessionId} recv: {metadata->Kind} (handle: {handle}, slot: {(IntPtr)slot}).");
                                GameSession session = sessions[metadata->SessionId];
                                session.Context.OnSessionEstablished();
                            }
                            break;
                        case PipeEventKind.ApplicationData:
                            {
                                var applicationData = new Span<byte>(payload, payloadLength);
                                if ((tick % (1 << 6)) == 0)
                                    Log($"{metadata->SessionId} recv: {metadata->Kind} (handle: {handle}, slot: {(IntPtr)slot}). L:{metadata->Length}, D:{Encoding.UTF8.GetString(applicationData)}");
                                GameSession session = sessions[metadata->SessionId];
                                session.Enqueue(applicationData);
                            }
                            break;
                        case PipeEventKind.SessionClosed:
                            {
                                var msg = (SessionClosed*)payload;
                                Log($"{metadata->SessionId} recv: {metadata->Kind} (handle: {handle}, slot: {(IntPtr)slot}). PeerReason:{msg->PeerReason}, EndReason:{msg->EndReason}");
                                GameSession session = sessions[metadata->SessionId];
                                session.Context.OnSessionClosed(ref *msg);
                                session.Dispose();
                                if (!sessions.Remove(session.Context.LocalSessionId))
                                    throw new Exception($"Failed to remove session: {session.Context.LocalSessionId}");
                            }
                            break;
                        default:
                            Log($"{metadata->SessionId} recv: {metadata->Kind} ({handle})");
                            break;
                    }
                }
                finally
                {
                    pipe.RX.Pool.Return(handle);
                }
            }

            foreach (var (sessionId, session) in sessions)
            {
                if (!pipe.TX.Pool.TryRent(ref handle))
                {
                    throw new Exception("Failed to rent TX buffer.");
                }
                bool consumedBuffer = false;
                try
                {
                    byte* slot = pipe.TX.Pool.Deref(handle);
                    var metadata = (PipeEventMetadata*)slot;
                    metadata->SessionId = sessionId;
                    metadata->Kind = PipeEventKind.ApplicationData;
                    metadata->Offset = PacketHeader.Size;
                    byte* payload = slot + NetworkConstants.PipeEventHeadroom + PacketHeader.Size;
                    int remainingLength = pipe.TX.Pool.SlotSize - (NetworkConstants.PipeEventHeadroom + PacketHeader.Size);
                    if (!Encoding.UTF8.TryGetBytes(
                        chars: $"tick {tick} from '{name}'",
                        bytes: new Span<byte>(payload, remainingLength),
                        out int bytesWritten))
                    {
                        throw new Exception("Failed to write small UTF-8 message.");
                    }
                    metadata->Length = (ushort)bytesWritten;
                    if (!pipe.TX.Ring.TryEnqueue(handle))
                    {
                        throw new Exception("TX ring full.");
                    }
                    consumedBuffer = true;
                }
                finally
                {
                    if (!consumedBuffer)
                    {
                        pipe.TX.Pool.Abandon(handle);
                    }
                }
            }

            TimeSpan tickLatency = TimeSpan.FromSeconds(
                (Stopwatch.GetTimestamp() - tickBegin) / (double)Stopwatch.Frequency);

            TimeSpan remainingSleep = TimeSpan.FromMilliseconds(16.666666) - tickLatency;

            if (remainingSleep > TimeSpan.Zero) Thread.Sleep(remainingSleep);
        }
    }
    catch (ObjectDisposedException)
    {
        Stdout.Info($"reader '{name}' canceled");
    }
    catch (Exception ex)
    {
        Stdout.Info($"reader '{name}' exception: {ex}");
    }
}

void Repl()
{
    const string DefaultMode = nameof(DefaultMode);
    const string SelectedA = nameof(SelectedA);
    const string SelectedB = nameof(SelectedB);
    const string SelectedAll = nameof(SelectedAll);
    const string Quit = nameof(Quit);

    string mode = DefaultMode;

    void Log(string message) => Stdout.Info($"[{mode}] {message}");

    while (!shutdown.IsCancellationRequested)
    {
        switch (mode)
        {
            case DefaultMode:
                mode = HandleDefaultMode();
                break;
            case SelectedA:
            case SelectedB:
                mode = HandleSelect(mode);
                break;
            case SelectedAll:
                mode = HandleSelectAll(mode);
                break;
            case Quit:
                HandleQuit();
                return;
            default:
                throw new Exception("REPL error.");
        }
    }

    string HandleDefaultMode()
    {
        while (!shutdown.IsCancellationRequested)
        {
            Log("select an option: [a] - select peer A, [b] - select peer B, [x] - select all peers, [q]uit");
            char key = Console.ReadLine()?[0] ?? '?';
            switch (key)
            {
                case 'a': case 'A': // select peer A
                    return SelectedA;
                case 'b': case 'B': // select peer B
                    return SelectedB;
                case 'x': case 'X':
                    return SelectedAll;
                case 'q': case 'Q': // quit
                    return Quit;
                default:
                    Log($"Unrecognized command: {key}");
                    break;
            }
        }
        return Quit;
    }

    void HandleQuit()
    {
        using (networkA)
        using (networkB)
        {
            Log("shutting down...");
            try { shutdown.Cancel(); } catch { }
            Log("waiting for game thread A...");
            gameThreadA.Join(TimeSpan.FromSeconds(5));
            Log("waiting for game thread B...");
            gameThreadB.Join(TimeSpan.FromSeconds(5));
            Log("waiting for network thread A...");
            networkA.Stop(TimeSpan.FromSeconds(5));
            Log("waiting for network thread B...");
            networkB.Stop(TimeSpan.FromSeconds(5));
        }
    }

    string HandleSelectAll(string mode)
    {
        while (!shutdown.IsCancellationRequested)
        {
            Log("select an option: [o]pen (simultaneous), [c]lose (simultaneous), [l]ogs toggle, [q]uit");
            char key = Console.ReadLine()?[0] ?? '?';
            switch (key)
            {
                case 'o': case 'O': // open
                    // Simultaneous open
                    Log("executing simultaneous open");
                    networkA.Connect(networkB.LocalPeerId, ipEndPointB);
                    networkB.Connect(networkA.LocalPeerId, ipEndPointA);
                    break;
                case 'c': case 'C': // close
                    Log("executing simultaneous active close (via peer ID)");
                    networkA.Disconnect(networkB.LocalPeerId);
                    networkB.Disconnect(networkA.LocalPeerId);
                    break;
                case 'l': case 'L': // toggle logs
                    loggerA.SuppressOutput = !loggerA.SuppressOutput;
                    loggerB.SuppressOutput = !loggerB.SuppressOutput;
                    break;
                case 'q': case 'Q': // quit
                    return DefaultMode;
                default:
                    Log($"unrecognized command: {key}");
                    return DefaultMode;
            }
        }
        return Quit;
    }

    string HandleSelect(string mode)
    {
        CancellationTokenSource stopGame;
        Thread gameThread;
        Network network;
        PeerLogger logger;
        PeerId remotePeerId;
        IPEndPoint remoteEndPoint;

        if (mode == SelectedA)
        {
            stopGame = stopGameA;
            gameThread = gameThreadA;
            network = networkA;
            logger = loggerA;
            remotePeerId = networkB.LocalPeerId;
            remoteEndPoint = ipEndPointB;
        }
        else if (mode == SelectedB)
        {
            stopGame = stopGameB;
            gameThread = gameThreadB;
            network = networkB;
            logger = loggerB;
            remotePeerId = networkA.LocalPeerId;
            remoteEndPoint = ipEndPointA;
        }
        else
        {
            Log($"Unrecognized mode: {mode}");
            return Quit;
        }

        while (!shutdown.IsCancellationRequested)
        {
            Log("select an option: [n]etwork (stop), [g]ame (stop), [o]pen, [c]lose, [l]ogs toggle, [q]uit");
            char key = Console.ReadLine()?[0] ?? '?';
            switch (key)
            {
                case 'n': case 'N': // stop network thread
                    Log($"stopping network thread");
                    if (network.Stop(TimeSpan.FromSeconds(5)))
                        Log($"stopped network thread");
                    else
                        Log($"failed to stop network thread");
                    break;
                case 'g': case 'G': // stop game thread
                    Log($"stopping game thread");
                    try { stopGame.Cancel(); } catch { }
                    if (gameThread.Join(TimeSpan.FromSeconds(5)))
                        Log($"stopped game thread");
                    else
                        Log($"failed to stop game thread");
                    break;
                case 'o': case 'O': // open
                    Log($"connecting with remote peer '{remotePeerId}'");
                    network.Connect(remotePeerId, remoteEndPoint);
                    break;
                case 'c': case 'C': // close
                    Log($"disconnecting with remote peer '{remotePeerId}'");
                    network.Disconnect(remotePeerId);
                    break;
                case 'l': case 'L': // toggle logs
                    logger.SuppressOutput = !logger.SuppressOutput;
                    break;
                case 'q': case 'Q': // quit
                    return DefaultMode;
                default:
                    Log($"Unrecognized key: '{key}'");
                    return DefaultMode;
            }
        }
        return Quit;
    }
}

sealed class GameSession : IDisposable
{
    private readonly Queue<(IMemoryOwner<byte> buffer, int length)> _applicationData;
    private readonly SampleSession _session;
    private readonly bool _saveData;

    public GameSession(bool saveData = false)
    {
        _session = new();
        _applicationData = new();
        _saveData = saveData;
    }

    public SampleSession Context => _session;

    public void Enqueue(Span<byte> applicationData)
    {
        SessionState state = _session.State;
        if (state != SessionState.Established)
            throw new InvalidOperationException($"Invalid state for application data: {state}");

        if (!_saveData) return;

        IMemoryOwner<byte> buffer = MemoryPool<byte>.Shared.Rent(applicationData.Length);
        applicationData.CopyTo(buffer.Memory.Span);
        _applicationData.Enqueue((buffer, applicationData.Length));
    }
    public bool TryDequeue(out (IMemoryOwner<byte> buffer, int length) applicationData)
    {
        return _applicationData.TryDequeue(out applicationData);
    }
    public void Dispose()
    {
        while (_applicationData.TryDequeue(out var item))
        {
            item.buffer.Dispose();
        }
    }
}

sealed class PeerLogger : ILog
{
    private readonly ILog _logger;
    private readonly string _name;
    private readonly string _prefix;
    public PeerLogger(ILog logger, string name, string prefix = "")
    {
        _logger = logger;
        _name = name;
        _prefix = prefix;
    }
    public bool SuppressOutput { get; set; }
    public bool IsEnabled(LogLevel level) => !SuppressOutput && _logger.IsEnabled(level);
    public void Log(ref LogMessage message)
    {
        if (SuppressOutput) return;
        if (message.State is null)
            return;
        if (message.State is string value)
        {
            message.State = $"{_prefix}[{_name}] {value}";
            _logger.Log(ref message);
            return;
        }

        if (message.State is string[] strings)
        {
            var sb = new StringBuilder();
            sb.Append(_prefix);
            sb.Append('[');
            sb.Append(_name);
            sb.Append("] ");
            for (int i = 0; i < message.Length; i++)
                sb.Append(strings[i]);
            message.State = sb.ToString();
            ArrayPool<string>.Shared.Return(strings);
            message.Length = 1;
        }
        else if (message.State is char[] chars)
        {
            message.State = $"{_prefix}[{_name}] {new string(chars, 0, message.Length)}";
            ArrayPool<char>.Shared.Return(chars);
            message.Length = 1;
        }

        _logger.Log(ref message);
    }
    public void LogDebug(string? message) { if (!SuppressOutput) _logger.LogDebug($"{_prefix}[{_name}] {message}"); }
    public void LogError(string? message) { if (!SuppressOutput) _logger.LogError($"{_prefix}[{_name}] {message}"); }
    public void LogInformation(string? message) { if (!SuppressOutput) _logger.LogInformation($"{_prefix}[{_name}] {message}"); }
    public void LogWarning(string? message) { if (!SuppressOutput) _logger.LogWarning($"{_prefix}[{_name}] {message}"); }
    public void LogTrace(string? message) { if (!SuppressOutput) _logger.LogTrace($"{_prefix}[{_name}] {message}"); }

}
// Minimal game-thread session view for this sample; the real one (with input/hash
// rings) lives in Tanks.Net, which this Protocol-only sample doesn't reference.
sealed class SampleSession : ISession
{
    private SessionState _state;
    private SessionAccepted _sessionAccepted;
    private SessionClosed _sessionClosed;

    public ref readonly PeerId LocalPeerId => ref _sessionAccepted.LocalPeerId;
    public ref readonly PeerId RemotePeerId => ref _sessionAccepted.RemotePeerId;
    public ref readonly SessionId LocalSessionId => ref _sessionAccepted.LocalSessionId;
    public ref readonly SessionId RemoteSessionId => ref _sessionAccepted.RemoteSessionId;
    public SessionState State => _state;
    public ref readonly DisconnectReason PeerReason => ref _sessionClosed.PeerReason;
    public ref readonly EndReason EndReason => ref _sessionClosed.EndReason;

    public void OnSessionAccepted(ref SessionAccepted evt)
    {
        _sessionAccepted = evt;
        _state = SessionState.Init;
    }

    public void OnSessionEstablished()
    {
        _state = SessionState.Established;
    }

    public void OnSessionClosed(ref SessionClosed evt)
    {
        _sessionClosed = evt;
        _state = SessionState.Closed;
    }
}

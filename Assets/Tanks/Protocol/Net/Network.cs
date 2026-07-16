using System;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Threading;

namespace Tanks.Net;

// Network worker - poll loop thread
// All public APIs here are called by an external thread (game thread)
public interface INetwork
{
    public ref readonly PeerId LocalPeerId { get; }
    public ref readonly NetworkPipe Pipe { get; }
    public void Start();
    public bool Stop(TimeSpan timeout = default);
    public bool IsAlive { get; }
    public void Connect(PeerId peerId, IPEndPoint endpoint);
    public void Disconnect(SessionId sid);
    public void Disconnect(PeerId peerId);
}

public sealed unsafe class Network : INetwork, IDisposable
{
    private struct NetworkState
    {
        public NetworkState(
            NetworkPipe pipe, IDatagramTransport transport, IPeerCrypto crypto,
            IPeerKeyPair localKey, PeerId localPeerId, ushort appProtocolId,
            ushort appProtocolVersion, SessionTable sessions, PeerTable peers,
            ILog logger, byte* txBuffer, int txBufferLength,
            NetworkMetrics metrics)
        {
            Pipe = pipe;
            RxPool = pipe.RX.Pool;
            RxRing = pipe.RX.Ring;
            RxSlotSize = RxPool.SlotSize;
            TxPool = pipe.TX.Pool;
            TxRing = pipe.TX.Ring;
            TxSlotSize = TxPool.SlotSize;
            Transport = transport;
            Crypto = crypto;
            LocalKey = localKey;
            LocalPeerId = localPeerId;
            AppProtocolId = appProtocolId;
            AppProtocolVersion = appProtocolVersion;
            Sessions = sessions;
            Peers = peers;
            Logger = logger;
            TxBuffer = txBuffer;
            TxBufferLength = txBufferLength;
            Metrics = metrics;
            Handle = default;
            SessionIter = default;
            SourceAddr = default;
        }
        public readonly NetworkPipe Pipe;
        public readonly Pool2 RxPool;
        public readonly Ring2<BufferHandle> RxRing;
        public readonly int RxSlotSize;
        public readonly Pool2 TxPool;
        public readonly Ring2<BufferHandle> TxRing;
        public readonly int TxSlotSize;
        public readonly IDatagramTransport Transport;
        public readonly IPeerCrypto Crypto;
        public readonly IPeerKeyPair LocalKey;
        public readonly PeerId LocalPeerId;
        public readonly ushort AppProtocolId;
        public readonly ushort AppProtocolVersion;
        public readonly SessionTable Sessions;
        public readonly PeerTable Peers;
        public readonly ILog Logger;
        public readonly byte* TxBuffer; // outbound packets (public buffer for protocol packets)
        public readonly int TxBufferLength;
        public readonly NetworkMetrics Metrics;
        // single-thread ref return zero-copy pattern, use fields to avoid default init
        public BufferHandle Handle;
        public SessionTable.Iterator SessionIter;
        public NetAddress SourceAddr;
    }

    private readonly INetworkPipeLifetime _pipeLifetime;
    private IPeerKeyPair _localKey;
    private readonly byte* _txBuffer;
    private readonly object _sync = new();
    private bool _stopping;
    private Thread? _thread;
    private CancellationTokenSource? _cancellation;
    private int _disposed;
    private readonly ILog _logger;
    private readonly Pool2 _txPool;
    private readonly Ring2<BufferHandle> _txRing;

    private readonly NetworkMetrics _metrics;
    private readonly NetworkState _state;

    public Network(
        INetworkPipeFactory pipeFactory,
        IDatagramTransport transport,
        IPeerCrypto crypto,
        ILog logger,
        ushort appProtocolId,
        ushort appProtocolVersion)
    {
        ThrowHelper.ThrowIfNull(pipeFactory);
        ThrowHelper.ThrowIfNull(transport);
        ThrowHelper.ThrowIfNull(crypto);
        ThrowHelper.ThrowIfNull(logger);

        if (!BitConverter.IsLittleEndian)
            throw new InvalidOperationException("Big endian is not supported.");

        _logger = logger;
        _pipeLifetime = pipeFactory.Create();
        _txPool = _pipeLifetime.Pipe.TX.Pool;
        _txRing = _pipeLifetime.Pipe.TX.Ring;

        int txBufferLength = NetworkConstants.MaxDatagramSize;
        _txBuffer = (byte*)Marshal.AllocHGlobal(txBufferLength);

        // Generate public/private key pair for local peer crypto.
        _localKey = crypto.GenerateKeyPair();
        _logger.LogDebug($"generated local key pair. pubkeylen: {_localKey.PublicKey.Length}");

        // Hash the public key and truncate to get peer ID.
        NetworkHelper.GetPeerId(_localKey.PublicKey, out PeerId localPeerId);
        _logger.LogInformation($"local peer ID: {localPeerId.ToVerboseString()}");

        var sessions = new SessionTable();
        var peers = new PeerTable();

        _metrics = new NetworkMetrics();
        _state = new NetworkState(
            _pipeLifetime.Pipe, transport, crypto, _localKey, localPeerId,
            appProtocolId, appProtocolVersion, sessions, peers, _logger,
            _txBuffer, txBufferLength, _metrics);
    }

    public ref readonly NetAddress LocalEndPoint => ref _state.Transport.LocalEndPoint;
    public ref readonly PeerId LocalPeerId => ref _state.LocalPeerId;

    // Single writer: the net thread. Safe to read from other threads for display.
    public NetworkMetrics Metrics => _metrics;
    public ref readonly NetworkPipe Pipe => ref _state.Pipe;
    public bool IsAlive => _thread?.IsAlive is true;

    public void Dispose()
    {
        Stop();
        if (_thread is null)
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
            using (_pipeLifetime)
            using (_localKey)
            {
                Marshal.FreeHGlobal((nint)_txBuffer);
            }
        }
    }

    public void Start()
    {
        bool start = false;

        lock (_sync)
        {
            if (_thread is not null || Volatile.Read(ref _disposed) != 0)
            {
                return;
            }

            _cancellation = new CancellationTokenSource();
            _thread = new Thread(() => Loop(_state, _cancellation.Token)) { IsBackground = true };
            start = true;
        }

        if (start)
        {
            try
            {
                _thread.Start();
            }
            catch
            {
                using (_cancellation)
                {
                    _cancellation.Cancel();
                    if (_thread.IsAlive)
                        _thread.Join();

                    _cancellation = null;
                    _thread = null;
                    throw;
                }
            }
        }
    }

    // TODO: Shouldn't we assume this is a single-threaded API? Who is using `Network` concurrently...?
    public bool Stop(TimeSpan timeout = default)
    {
        if (timeout <= TimeSpan.Zero)
        {
            timeout = TimeSpan.FromSeconds(5); // default timeout
        }

        Thread? thread = null;

        lock (_sync)
        {
            thread = _thread;

            if (thread is null)
            {
                _logger.LogDebug("network thread already stopped");
                goto PassiveStop;
            }

            if (_stopping)
            {
                goto PassiveStop;
            }
            else
            {
                _stopping = true;
                goto ActiveStop;
            }
        }

    ActiveStop:
        {
            Debug.Assert(_cancellation is not null);
            Debug.Assert(_thread is not null);

            try
            {
                try { _cancellation.Cancel(); } catch { }
                bool killed = !thread.IsAlive;
                if (!killed)
                {
                    killed = thread.Join(timeout);
                    if (!killed)
                    {
                        _logger.LogError("failed to kill network thread");
                    }
                }
                if (killed)
                {
                    _logger.LogInformation("killed network thread");
                    using (_cancellation)
                    {
                        _thread = null;
                        _cancellation = null;
                    }
                }
            }
            finally
            {
                _stopping = false;
            }

            return !IsAlive;
        }

    PassiveStop:
        {
            if (thread is not null)
            {
                _logger.LogInformation("waiting on existing stop operation...");
                thread?.Join(timeout);
            }
            bool stopped = !IsAlive;

            if (stopped)
                _logger.LogInformation("network thread stopped");
            else
                _logger.LogWarning("network thread still running");

            return stopped;
        }
    }

    // Starts a new connection with a remote peer
    // Handshake will not be complete at first
    public void Connect(
        PeerId peerId,
        IPEndPoint endpoint)
    {
        ThrowHelper.ThrowIfNull(endpoint);

        if (endpoint.Port < 0 || endpoint.Port > ushort.MaxValue)
        {
            _logger.LogError($"failed to initiate connection with peer '{peerId}' at endpoint '{endpoint}': invalid remote port: {endpoint.Port}");
            return;
        }
        if (endpoint.AddressFamily != AddressFamily.InterNetwork &&
            endpoint.AddressFamily != AddressFamily.InterNetworkV6)
        {
            _logger.LogError($"failed to initiate active open with peer '{peerId}' at endpoint '{endpoint}': invalid address family: {endpoint.AddressFamily}");
            return;
        }

        BufferHandle handle = default;
        if (!_txPool.TryRent(ref handle))
        {
            _logger.LogError($"failed to initiate active open with '{peerId}' at endpoint '{endpoint}': TX pool exhausted");
            return;
        }
        bool consumedBuffer = false;
        try
        {
            byte* buffer = _txPool.Deref(handle);
            var metadata = (PipeEventMetadata*)buffer;
            metadata->SessionId = default;
            metadata->Kind = PipeEventKind.ActiveOpen;
            metadata->Offset = 0;
            metadata->Length = Net.Connect.Size;
            buffer += NetworkConstants.PipeEventHeadroom;
            var msg = (Connect*)buffer;
            msg->PeerId = peerId;
            msg->Address = NetAddress.FromIPEndPoint(endpoint);
            if (consumedBuffer = _txRing.TryEnqueue(handle))
                _logger.LogDebug($"initiated active open with remote peer '{peerId}' at endpont '{endpoint}'");
            else
                _logger.LogError($"failed to initiate active open with remote peer '{peerId}' at endpont '{endpoint}': TX ring full");
        }
        finally
        {
            if (!consumedBuffer)
                _txPool.Abandon(handle);
        }
    }

    public void Disconnect(PeerId remotePeerId)
    {
        BufferHandle handle = default;
        if (!_txPool.TryRent(ref handle))
        {
            _logger.LogError($"failed to initiate active close with remote peer '{remotePeerId}': TX pool exhausted");
            return;
        }
        bool consumedBuffer = false;
        try
        {
            byte* buffer = _txPool.Deref(handle);
            var metadata = (PipeEventMetadata*)buffer;
            metadata->SessionId = default;
            metadata->Kind = PipeEventKind.ActiveClose;
            metadata->Offset = 0;
            metadata->Length = DisconnectPeer.Size;
            var msg = (DisconnectPeer*)(buffer + NetworkConstants.PipeEventHeadroom + metadata->Offset);
            msg->RemotePeerId = remotePeerId;
            if (consumedBuffer = _txRing.TryEnqueue(handle))
                _logger.LogDebug($"initiated active close with remote peer '{remotePeerId}'");
            else
                _logger.LogError($"failed to initiate active close with remote peer '{remotePeerId}': TX ring full");
        }
        finally
        {
            if (!consumedBuffer)
                _txPool.Abandon(handle);
        }
    }

    public void Disconnect(SessionId localSessionId)
    {
        BufferHandle handle = default;
        if (!_txPool.TryRent(ref handle))
        {
            _logger.LogError($"failed initiate active close for active close of session '{localSessionId}': TX pool exhausted");
            return;
        }
        bool consumedBuffer = false;
        try
        {
            byte* buffer = _txPool.Deref(handle);
            var metadata = (PipeEventMetadata*)buffer;
            metadata->SessionId = localSessionId;
            metadata->Kind = PipeEventKind.ActiveClose;
            metadata->Offset = 0;
            metadata->Length = 0;
            if (consumedBuffer = _txRing.TryEnqueue(handle))
                _logger.LogDebug($"initiated active close for session '{localSessionId}'");
            else
                _logger.LogError($"failed to initiate active close for session '{localSessionId}': TX ring full");
        }
        finally
        {
            if (!consumedBuffer)
                _txPool.Abandon(handle);
        }
    }

    private static void Loop(NetworkState network, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                Poll(ref network); // continues while data still available
            }
            catch (Exception ex)
            {
                network.Logger.LogError($"Error in network thread: {ex}");
            }
        }
    }

    private static void Poll(ref NetworkState network)
    {
        DrainRx(ref network);
        DrainTx(ref network);
        TimerSweep(ref network);
    }

    private static void TimerSweep(ref NetworkState network)
    {
        if (!network.Sessions.TryGetIterator(ref network.SessionIter))
            return;

        long nowTicks = Stopwatch.GetTimestamp();
        SessionProtocol session;
        bool reap;

        do
        {
            session = network.SessionIter.Current;
            session.OnTick(nowTicks);
            reap = !session.IsAlive;
            if (reap)
            {
                network.Logger.LogInformation($"(timers) reaping session '{session.Context.LocalSessionId}' with peer '{session.Context.RemotePeerId}'");

                if (!network.Peers.TryRemove(in session.Context.RemotePeerId, out _))
                {
                    network.Logger.LogError($"(timers) failed to reap peer table for remote peer '{session.Context.RemotePeerId}'");
                }

                session.Context.Dispose();
            }
        }
        while (network.SessionIter.Advance(reap));
    }

    private static void DrainTx(ref NetworkState network)
    {
        const int TxBudget = 64; // copied NAPI

        int budget = 0;

        while (budget++ < TxBudget && network.TxRing.TryDequeue(ref network.Handle))
        {
            try
            {
                byte* slot = network.TxPool.Deref(network.Handle);
                var metadata = (PipeEventMetadata*)slot;
                SessionProtocol? session;

                switch (metadata->Kind)
                {
                    case PipeEventKind.ActiveOpen:
                        ActiveOpen(ref network, slot);
                        break;
                    case PipeEventKind.ActiveClose:
                        ActiveClose(ref network, slot);
                        break;
                    case PipeEventKind.ApplicationData:
                        if (network.Sessions.TryGetValue(metadata->SessionId, out session) &&
                            session.Context.State == SessionState.Established)
                        {
                            session.TrySendApplicationData(slot, network.TxSlotSize);
                        }
                        else
                        {
                            // Session closed (or never existed) by the time the
                            // net thread drained this - drop silently per contract.
                            network.Metrics.TxDroppedNoSession++;
                        }
                        break;
                    default:
                        network.Logger.LogDebug($"TX dropped: unhandled event kind {metadata->Kind}");
                        break;
                }
            }
            finally
            {
                network.TxPool.Return(network.Handle);
            }
        }
    }

    private static void DrainRx(ref NetworkState network)
    {
        const int RxBudget = 64; // copied NAPI

        if (!network.Transport.PollRead(microseconds: 1000))
        {
            return;
        }

        int budget = 0;

        do
        {
            try
            {
                if (!PollRx(ref network))
                    break;
            }
            catch (Exception ex)
            {
                // Catch per packet to avoid unauthenticated malformed packets from executing DoS
                network.Logger.LogError($"Error in network thread RX: {ex}");
                return; // don't block TX or timers
            }
        }
        while (++budget < RxBudget && network.Transport.PollRead(0));
    }

    private static bool PollRx(ref NetworkState network)
    {
        // RX:
        // - receive packet from the transport
        // - look up session by DSID
        // - If no existing session, try parsing PING and create session
        // - If existing session
        //     - If handshake message or disconnect, pump the state machine
        //     - If established, send the messages across the RX pipe

        if (!network.RxPool.TryRent(ref network.Handle))
        {
            network.Metrics.RxStalledPoolExhausted++;
            return false;
        }

        bool consumedBuffer = false;

        try
        {
            byte* slot = network.RxPool.Deref(network.Handle);
            byte* slotEnd = slot + network.RxSlotSize;
            byte* packetBegin = slot + NetworkConstants.PipeEventHeadroom;

            if (!network.Transport.TryReceive(
                packetBegin,
                (int)(slotEnd - packetBegin),
                out int packetLength,
                ref network.SourceAddr))
            {
                return true;
            }

            network.Metrics.RxPackets++;
            network.Metrics.RxBytes += packetLength;

            PacketHeader* packet = (PacketHeader*)packetBegin;

            if (packetLength < PacketHeader.Size)
            {
                network.Metrics.RxDroppedUndersized++;
                network.Logger.LogDebug("dropped undersized packet");
                return true;
            }
            if (packetLength > NetworkConstants.MaxDatagramSize)
            {
                network.Metrics.RxDroppedOversized++;
                network.Logger.LogDebug($"dropped oversized packet (L:{packetLength})");
                return true;
            }

            var magic = new ReadOnlySpan<byte>(packet->Magic, 6);
            if (!magic.SequenceEqual(NetworkConstants.Magic))
            {
                network.Metrics.RxDroppedBadMagic++;
                network.Logger.LogDebug("dropped packet with bad magic");
                return true;
            }

            if (packet->MajorVersion != NetworkConstants.MajorVersion ||
                packet->MinorVersion != NetworkConstants.MinorVersion)
            {
                network.Metrics.RxDroppedBadVersion++;
                network.Logger.LogDebug("RX dropped: bad version");
                return true; // require 1.0, since it's only 1.0
            }

            SessionProtocol? session = network.Sessions.Get(packet->DSID);

            if (session is null)
            {
                // No existing session found with this remote peer, so only valid
                // scenario is a passive open (from local perspective).
                // Validate the peer is actually trying to connect, otherwise drop.
                PassiveOpen(ref network, packetBegin, packetLength);
                return true;
            }

            // Found existing session with remote peer.
            //_logger.LogTrace($"[RX] {session.Context.RemotePeerId}/{session.Context.LocalSessionId}/{session.Context.State}");

            if (!packet->DSID.Equals(session.Context.LocalSessionId))
            {
                // Impossible under invariants; this firing means a bug
                // session lookup code or some other type of corruption
                // TODO: reap session? throw exception?
                network.Logger.LogError($"invalid DSID ('{packet->DSID}' != '{session.Context.LocalSessionId}')");
                return true;
            }

            byte* payload = packetBegin + PacketHeader.Size;
            int payloadLength = packetLength - PacketHeader.Size;

            // All packets that are not Ping packets get authenticated.
            // Pong packets get authenticated, but only after parsing them,
            // because they contain the data required to derive a session key.
            if (packet->Type != PacketType.Ping && packet->Type != PacketType.Pong)
            {
                IPacketAuthenticator? auth = session.Context.Auth;
                if (auth is null)
                {
                    // no session key yet - can't authenticate
                    network.Metrics.RxDroppedNoKey++;
                    network.Logger.LogDebug($"dropped '{packet->Type}' from peer {session.Context.RemotePeerId}: no session key yet - can't authenticate");
                    return true;
                }
                int tagLength;
                if (!Wire.VerifyAuth(packetBegin, packetLength, auth, packet->Seq, out tagLength))
                {
                    network.Metrics.RxDroppedBadMac++;
                    network.Logger.LogDebug($"dropped from peer {session.Context.RemotePeerId}: bad auth tag");
                    return true;
                }
                payloadLength -= tagLength;

                // Replay filter: only authenticated seqs may touch the window,
                // and replays must not refresh liveness or reach the game.
                if (!session.Context.Replay.TryAccept(packet->Seq))
                {
                    network.Metrics.RxDroppedReplay++;
                    return true;
                }

                // Any authenticated, fresh packet refreshes LastRecvTimestamp
                session.Context.LastRecvTimestamp = Stopwatch.GetTimestamp();
            }

            Debug.Assert(payloadLength >= 0);
            Debug.Assert(payloadLength <= NetworkConstants.MaxApplicationDataLength);

            if (packet->Type != PacketType.ApplicationData)
            {
                // Route to protocol, even if in Established.
                session.OnReceive(packet, packetLength, payload, payloadLength, ref network.SourceAddr);
            }
            else
            {
                if (session.Context.State != SessionState.Established)
                {
                    network.Metrics.RxDroppedBadState++;
                    network.Logger.LogDebug($"RX dropped application data from {session.Context.RemotePeerId} due to invalid state: {session.Context.State}");
                    return true;
                }

                var metadata = (PipeEventMetadata*)slot;
                metadata->SessionId = session.Context.LocalSessionId;
                metadata->Kind = PipeEventKind.ApplicationData;
                metadata->Offset = PacketHeader.Size;
                metadata->Length = (ushort)payloadLength;
                if (!network.RxRing.TryEnqueue(network.Handle))
                {
                    network.Metrics.RxDroppedRingFull++;
                    network.Logger.LogDebug("dropped application data: RX ring full");
                    return false;
                }
                consumedBuffer = true;
            }

            if (!session.IsAlive)
            {
                network.Logger.LogDebug($"session '{session.Context.LocalSessionId}' is closed with peer '{session.Context.RemotePeerId}'; reaping lookup tables");
                ref readonly PeerId remotePeerId = ref session.Context.RemotePeerId;
                if (network.Sessions.TryRemove(session.Context.LocalSessionId, out _)) session.Context.Dispose();
                network.Peers.TryRemove(in remotePeerId, out _);
            }

            return true;
        }
        finally
        {
            if (!consumedBuffer)
            {
                network.RxPool.Abandon(network.Handle);
            }
        }
    }

    private static void ActiveClose(ref NetworkState network, byte* slot)
    {
        var metadata = (PipeEventMetadata*)slot;
        Debug.Assert(metadata->Kind == PipeEventKind.ActiveClose);

        SessionId sid;

        // Two flavors of active close
        // - by SessionId: use SID from metadata, get session from session LUT
        // - by PeerId: use PeerId from payload, look up SID in peer LUT, get sesion from session LUT

        if (metadata->Length == DisconnectPeer.Size)
        {
            var msg = (DisconnectPeer*)(slot + NetworkConstants.PipeEventHeadroom + metadata->Offset);
            if (!network.Peers.TryGetValue(in msg->RemotePeerId, out sid))
            {
                network.Logger.LogWarning($"(active close) no session for remote peer '{msg->RemotePeerId}'");
                return;
            }
        }
        else if (metadata->Length == 0)
        {
            sid = metadata->SessionId;
        }
        else
        {
            network.Logger.LogWarning($"(active close) invalid metadata length: {metadata->Length}");
            return;
        }

        SessionProtocol? session;
        if (!network.Sessions.TryGetValue(sid, out session))
        {
            network.Logger.LogWarning($"(active close) no session found with ID '{sid}'");
            return;
        }
        session.Disconnect(DisconnectReason.ActiveClose, EndReason.LocalClosed);
    }

    private static void ActiveOpen(ref NetworkState network, byte* slot)
    {
        var metadata = (PipeEventMetadata*)slot;
        Debug.Assert(metadata->Kind == PipeEventKind.ActiveOpen);
        byte* payload = slot + NetworkConstants.PipeEventHeadroom + metadata->Offset;
        var connect = (Connect*)payload;
        NetAddress endpoint = connect->Address;

        SessionId sid;
        if (!network.Sessions.TryPeek(out sid))
        {
            network.Logger.LogError("(active open) max session capacity reached");
            return;
        }
        else network.Logger.LogDebug($"(active open) peeked new session ID '{sid}'");

        if (!network.Peers.TryAdd(in connect->PeerId, sid))
        {
            network.Logger.LogError("active open failed: found existing peer session OR peer table full");
            return;
        }
        else network.Logger.LogDebug($"(active open) added peer '{connect->PeerId}' to peer lookup table");

        var ctx = new SessionContext(
            network.LocalPeerId, connect->PeerId, endpoint, sid,
            network.AppProtocolId, network.AppProtocolVersion,
            network.LocalKey, network.Crypto)
        {
            RemoteSessionId = default,
        };

        var session = new SessionProtocol(
            network.TxBuffer, network.TxBufferLength, ctx, network.Transport, network.Pipe, network.Logger, network.Metrics);

        if (!network.Sessions.TryAdd(sid, session))
        {
            network.Logger.LogError("(active open) failed to add new session to session table");

            if (!network.Peers.TryRemove(in connect->PeerId, out _))
            {
                network.Logger.LogError("(active open) failed to clean up peer table entry");
            }

            return;
        }
        else network.Logger.LogDebug($"(active open) added session '{sid}' to session lookup table (peer: {connect->PeerId}/{session.Context.RemotePeerId}, addr: {session.Context.RemoteEndPoint})");

        session.ActiveOpen(ref connect->Address);
    }

    private static void PassiveOpen(ref NetworkState network, byte* buffer, int length)
    {
        PacketHeader* packet = (PacketHeader*)buffer;

        network.Logger.LogTrace($"no existing session '{packet->DSID}' with remote peer '{packet->PeerId}'");

        if (packet->Type != PacketType.Ping)
        {
            network.Logger.LogDebug($"(passive open) dropped '{packet->Type}' message from unknown remote peer {packet->PeerId}");
            return;
        }

        buffer += PacketHeader.Size;
        length -= PacketHeader.Size;

        // Read Ping:
        // - Read public key
        // - Validate packet PeerId
        // - Verify signature
        // - Create session, add to lookup table(s)

        // Write Pong:
        // - Write initiator nonce and own public key
        // - Encrypt own nonce and write
        // - Write offsets and length
        // - Sign Pong message

        if (!Wire.TryParsePing(
            buffer,
            length,
            out Ping* ping,
            out byte* initiatorPubKey, out int initiatorPubKeyLen,
            out byte* pingSignature, out int pingSignatureLen))
        {
            network.Metrics.HandshakesRejected++;
            network.Logger.LogWarning("(passive open) malformed ping");
            return;
        }

        if (ping->AppProtocolId != network.AppProtocolId || ping->AppProtocolVersion != network.AppProtocolVersion)
        {
            network.Metrics.HandshakesRejected++;
            network.Logger.LogDebug($"(passive open) dropped unknown app protocol Ping: {ping->AppProtocolId}.{ping->AppProtocolVersion}");
            return;
        }

        IPeerVerifier? remoteKey = null;
        SessionContext? ctx = null;
        bool peerAdded = false;
        bool sessionAdded = false;
        SessionId sid = default;
        bool succeeded = false;

        try
        {
            // Detect whether it's a duplicate (or just retransmitted) Ping.
            SessionProtocol? staleSession = null; // store stale session here if we lose the tiebreaker
            if (network.Peers.TryGetValue(in packet->PeerId, out SessionId staleSessionId))
            {
                network.Logger.LogDebug($"(passive open) warm Ping from remote peer '{packet->PeerId}'");

                SessionProtocol? existingSession;
                if (!network.Sessions.TryGetValue(staleSessionId, out existingSession))
                {
                    // Invariant failed: for every peer in peer LUT, there must be a session in session LUT
                    network.Logger.LogError($"(passive open) failed to get session for remote peer '{packet->PeerId}'");
                    return;
                }

                // If we're in PingSent, then we did active open, but remote peer sent Ping,
                // so handle the simultaneous open scenario here
                if (existingSession.Context.State == SessionState.PingSent)
                {
                    PeerId remotePeerId = existingSession.Context.RemotePeerId;
                    int cmp = packet->PeerId.CompareTo(network.LocalPeerId);
                    if (cmp == 0)
                    {
                        network.Logger.LogError($"(simultaneous open) failed to resolve simultaneous open for matching peer IDs: {network.LocalPeerId}/{remotePeerId}");
                        return;
                    }
                    else if (cmp < 0)
                    {
                        // Remote peer has "lower" peer ID, so it "wins" the simultaneous open - let it be the initiator
                        network.Logger.LogInformation($"(simultaneous open) remote peer is initiator");

                        // We can't act yet - the inbound Ping is not verified.
                        staleSession = existingSession;
                    }
                    else
                    {
                        // Local peer has "lower" peer ID, so it "wins" the simultaneous open - let it be the initiator
                        network.Logger.LogInformation("(simultaneous open) local peer is initiator");
                        return; // drop this inbound Ping (abandon passive open)
                    }
                }
            }

            if (!network.Sessions.TryPeek(out sid))
            {
                network.Logger.LogWarning("(passive open) max session capacity reached");
                return;
            }

            // Verify the peer ID is actually derived from the public key.
            NetworkHelper.GetPeerId(
                new ReadOnlySpan<byte>(initiatorPubKey, initiatorPubKeyLen),
                out PeerId computedPeerId);

            if (!packet->PeerId.Equals(computedPeerId))
            {
                network.Metrics.HandshakesRejected++;
                network.Logger.LogWarning($"(passive open) invalid initiator peer ID: {packet->PeerId} != {computedPeerId}");
                return;
            }

            remoteKey = network.Crypto.CreateVerifier(
                new ReadOnlySpan<byte>(initiatorPubKey, initiatorPubKeyLen));

            // Get the peer's ping signature and validate it using the verifier (public key).
            if (!remoteKey.Verify(
                message: new ReadOnlySpan<byte>((byte*)ping, (int)(pingSignature - (byte*)ping)),
                signature: new ReadOnlySpan<byte>(pingSignature, pingSignatureLen)))
            {
                network.Metrics.HandshakesRejected++;
                network.Logger.LogWarning("(passive open) invalid initiator signature");
                return;
            }

            // Remote peer's Ping is valid - if we lost the tiebreaker, the session that our "active open"
            // created is now stale, so clean it up
            if (staleSession is not null)
            {
                ref readonly PeerId remotePeerId = ref staleSession.Context.RemotePeerId;

                // NOTE: We can't send Disconnect over the wire for existing session, because there is no session key
                //       for computing the MAC. If the remote peer is tracking this session, let it time out.

                // Tell the game thread this session is closed
                staleSession.Disconnect(DisconnectReason.None, EndReason.SimultaneousOpen);

                if (staleSession.IsAlive)
                {
                    network.Logger.LogWarning($"(simultaneous open) stale session {staleSession.Context.LocalSessionId} cleanup delayed");
                    return;
                }

                // Remove this existing session because it's associated with our active open
                // WARNING: Invalidates previous TryPeek
                if (!network.Sessions.TryRemove(staleSession.Context.LocalSessionId, out _))
                {
                    network.Logger.LogError($"(simultaneous open) failed to reap session during simultaneous open resolution (p:{remotePeerId}, sid:{staleSession.Context.LocalSessionId})");
                    return;
                }
                staleSession.Context.Dispose();

                // Remove the existing peer entry because it's a peer->session mapping to a now-stale session
                if (!network.Peers.TryRemove(in remotePeerId, out _))
                {
                    network.Logger.LogError($"(simultaneous open) failed to reap peer during simultaneous open resolution (p:{remotePeerId})");
                    return;
                }

                // Teardown freed a slot onto the free stack, invalidating the earlier peek.
                // Can't fail: we just freed a slot, so capacity only improved.
                bool didPeek = network.Sessions.TryPeek(out sid);
                Debug.Assert(didPeek);
            }

            // Create the new connection.
            ctx = new SessionContext(
                network.LocalPeerId, packet->PeerId, network.SourceAddr, sid,
                network.AppProtocolId, network.AppProtocolVersion,
                network.LocalKey, network.Crypto)
            {
                LastRecvTimestamp = Stopwatch.GetTimestamp(),
                RemoteVerifier = remoteKey,
                RemoteSessionId = packet->SSID,
            };
            new ReadOnlySpan<byte>((byte*)&ping->InitiatorNonce, 16).CopyTo(ctx.RemoteNonce);

            // Generate local peer nonce
            RandomNumberGenerator.Fill(ctx.LocalNonce);

            // Compute the directional key material
            network.Crypto.Kdf.Derive(
                ikm: ctx.LocalNonce, // responder nonce is secret
                salt: ctx.RemoteNonce, // initiator nonce is salt
                info: NetworkConstants.SessionKeyInfo, // "tanks!sessionkey"
                okm: ctx.SessionKey);

            // We received the Ping, so we hold the responder half.
            ctx.Auth = network.Crypto.CreateAuthenticator(ctx.SessionKey, initiator: false);

            var session = new SessionProtocol(
                network.TxBuffer, network.TxBufferLength, ctx, network.Transport, network.Pipe, network.Logger, network.Metrics);

            peerAdded = network.Peers.TryAdd(in packet->PeerId, sid);
            if (!peerAdded)
            {
                network.Logger.LogError($"(passive open) failed to add new peer session to peer table (p:{packet->PeerId}/s:{sid})");
                return;
            }
            else network.Logger.LogDebug($"(passive open) addded new peer session to peer table (p:{packet->PeerId}/s:{sid})");

            sessionAdded = network.Sessions.TryAdd(sid, session);
            if (!sessionAdded)
            {
                network.Logger.LogError($"(passive open) failed to add new session to session table (p:{packet->PeerId}/s:{sid})");
                return;
            }
            else network.Logger.LogDebug($"(passive open) addded new session to session table (p:{packet->PeerId}/s:{sid})");

            session.PassiveOpen(ref network.SourceAddr);

            succeeded = true;
        }
        finally
        {
            if (!succeeded)
            {
                using (remoteKey)
                using (ctx)
                {
                    if (peerAdded)
                    {
                        if (!network.Peers.TryRemove(in packet->PeerId, out _))
                            network.Logger.LogError($"(passive open) failed to clean up peer table entry: {packet->PeerId}");
                    }
                    if (sessionAdded)
                    {
                        if (!network.Sessions.TryRemove(sid, out _))
                            network.Logger.LogError($"(passive open) failed to clean up session table entry: {sid}");
                    }
                }
            }
        }
    }
}
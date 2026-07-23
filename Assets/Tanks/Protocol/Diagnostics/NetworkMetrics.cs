namespace Tanks.Net;

// Counters written by the network thread (single writer, no synchronization).
// Plain fields, incremented directly - no interface dispatch on the hot path.
// Other threads may read them for display; 64-bit aligned longs don't tear.
public sealed class NetworkMetrics
{
    // RX
    public long RxPackets;
    public long RxBytes;
    public long RxDroppedUndersized;
    public long RxDroppedOversized;
    public long RxDroppedBadMagic;
    public long RxDroppedBadVersion;
    public long RxDroppedNoKey;         // non-handshake packet before a session key exists
    public long RxDroppedBadMac;
    public long RxDroppedReplay;        // authenticated but already-seen (or too-old) seq
    public long RxDroppedBadState;      // e.g. application data before Established
    public long RxDroppedMalformed;     // empty/inconsistent payload
    public long RxDroppedUnknownType;
    public long RxDroppedRingFull;      // game thread not draining the RX ring
    public long RxStalledPoolExhausted; // backpressure engaged; datagrams left in kernel buffer

    // TX
    public long TxPackets;
    public long TxBytes;
    public long TxDroppedNoSession;     // payload for a closed/unknown session (teardown race)
    public long TxSendFailed;

    // Sessions / handshake
    public long SessionsAccepted;
    public long SessionsEstablished;
    public long SessionsClosed;
    public long HandshakesRejected;     // malformed/mismatched/badly-signed Ping or Pong
    public long LifecycleEventsQueued;  // RX ring was full; event deferred to the pending queue
}

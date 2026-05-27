using System;

namespace Tankz.Net
{
    /// <summary>
    /// Minimal datagram transport: send bytes to the peer, poll for bytes that have arrived.
    /// Deliberately tiny and unreliable (UDP-like) — sequencing/reliability/ack logic is the
    /// netcode's job, layered on top during your session.
    ///
    /// Implementations:
    ///   - <see cref="InProcessNetwork"/> endpoints: same-process loopback with simulated
    ///     latency/jitter/loss (for development and the determinism tests).
    ///   - (later) a real System.Net.Sockets UDP transport, swapped in behind this same interface.
    /// </summary>
    public interface ITransport
    {
        /// <summary>Identifier of this endpoint (0 or 1 for our 1v1 loopback).</summary>
        int LocalEndpoint { get; }

        /// <summary>Send a datagram to the peer. Fire-and-forget; may be dropped or delayed.</summary>
        void Send(ReadOnlySpan<byte> data);

        /// <summary>Pop the next datagram that has arrived, if any. Returns false when the inbox is empty.</summary>
        bool TryReceive(out byte[] data);
    }
}

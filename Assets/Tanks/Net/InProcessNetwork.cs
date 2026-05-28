using System;
using System.Collections.Generic;
using Tanks.Sim;

namespace Tanks.Net
{
    /// <summary>
    /// A same-process link between two endpoints (0 and 1) that models real-network pain:
    /// latency, jitter (which can reorder packets), and loss. Delivery is driven by the
    /// SIMULATION TICK (not wall-clock), so behavior is reproducible and testable.
    ///
    /// Workflow each tick:
    ///   1. endpoints Send() — packets enter "in flight"
    ///   2. network.Poll(tick) — packets whose delivery tick has arrived move to inboxes
    ///   3. endpoints TryReceive() — drain delivered packets
    ///
    /// The latency/jitter/loss fields are public and meant to be twiddled live from the HUD —
    /// this is the substrate for the "make the netcode visible" features.
    /// </summary>
    public sealed class InProcessNetwork
    {
        /// <summary>Base one-way delay in ticks.</summary>
        public int LatencyTicks;
        /// <summary>Random extra delay in [-JitterTicks, +JitterTicks] ticks (clamped at 0). Can reorder packets.</summary>
        public int JitterTicks;
        /// <summary>Probability in [0,1] that any given packet is dropped.</summary>
        public float LossChance;

        public uint CurrentTick { get; private set; }

        private struct InFlight
        {
            public int To;
            public uint DeliverAt;
            public ulong Seq;   // tiebreaker so same-tick deliveries keep a stable order
            public byte[] Data;
        }

        private readonly List<InFlight> _inFlight = new List<InFlight>();
        private readonly Queue<byte[]>[] _inbox = { new Queue<byte[]>(), new Queue<byte[]>() };
        private Xorshift32 _rng;
        private ulong _seqCounter;

        public ITransport EndpointA { get; }
        public ITransport EndpointB { get; }

        public InProcessNetwork(int latencyTicks = 0, int jitterTicks = 0, float lossChance = 0f, uint rngSeed = 0xC0FFEEu)
        {
            LatencyTicks = latencyTicks;
            JitterTicks = jitterTicks;
            LossChance = lossChance;
            _rng = new Xorshift32(rngSeed);
            EndpointA = new Endpoint(this, 0);
            EndpointB = new Endpoint(this, 1);
        }

        private void SendInternal(int from, ReadOnlySpan<byte> data)
        {
            int to = from ^ 1; // 0->1, 1->0

            // Loss (only consult the RNG when loss is enabled, so the no-loss path stays
            // perfectly deterministic regardless of seed).
            if (LossChance > 0f && _rng.NextFloat01() < LossChance)
                return;

            int delay = LatencyTicks;
            if (JitterTicks > 0)
                delay += _rng.NextRange(-JitterTicks, JitterTicks);
            if (delay < 0) delay = 0;

            _inFlight.Add(new InFlight
            {
                To = to,
                DeliverAt = CurrentTick + (uint)delay,
                Seq = _seqCounter++,
                Data = data.ToArray(),
            });
        }

        /// <summary>Advance the link clock and move any now-due packets into their inbox.</summary>
        public void Poll(uint tick)
        {
            CurrentTick = tick;

            // Deliver in (DeliverAt, Seq) order so reordering is well-defined.
            _inFlight.Sort(static (a, b) =>
            {
                int c = a.DeliverAt.CompareTo(b.DeliverAt);
                return c != 0 ? c : a.Seq.CompareTo(b.Seq);
            });

            int i = 0;
            while (i < _inFlight.Count && _inFlight[i].DeliverAt <= tick)
            {
                _inbox[_inFlight[i].To].Enqueue(_inFlight[i].Data);
                i++;
            }
            if (i > 0) _inFlight.RemoveRange(0, i);
        }

        private bool ReceiveInternal(int endpoint, out byte[] data)
        {
            var q = _inbox[endpoint];
            if (q.Count > 0) { data = q.Dequeue(); return true; }
            data = null;
            return false;
        }

        /// <summary>Number of packets currently in flight (for HUD/debugging).</summary>
        public int InFlightCount => _inFlight.Count;

        private sealed class Endpoint : ITransport
        {
            private readonly InProcessNetwork _net;
            public int LocalEndpoint { get; }

            public Endpoint(InProcessNetwork net, int id) { _net = net; LocalEndpoint = id; }

            public void Send(ReadOnlySpan<byte> data) => _net.SendInternal(LocalEndpoint, data);
            public bool TryReceive(out byte[] data) => _net.ReceiveInternal(LocalEndpoint, out data);
        }
    }
}

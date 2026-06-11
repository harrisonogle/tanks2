using System;
using System.Collections.Generic;
using NUnit.Framework;
using Tanks.Net;
using Tanks.Sim;

namespace Tanks.Tests
{
    /// <summary>
    /// The M2 reference loop: the exact schedule-once / resend-window / advance-gate logic
    /// SimRunner runs in Unity, exercised headless over a hostile wire. If this is green and
    /// the Unity build still desyncs, the bug is in the wiring, not the protocol.
    ///
    /// Protocol summary (per peer, per network tick):
    ///   1. drain:    record every received (tick, player, input) into the ring
    ///   2. schedule: sample the local input for tick (next + DELAY) — EXACTLY ONCE per tick
    ///                index; re-sampling on a stalled frame could send two different values
    ///                for the same tick and desync the peers
    ///   3. resend:   send every scheduled tick the peer could still need (see window note)
    ///   4. advance:  while inputs for (state.Tick + 1) are known for BOTH players, Tick
    ///
    /// Resend window: peers can't drift more than DELAY+1 ticks apart — to simulate tick T
    /// you need an input the peer scheduled when its own next tick was at least T-DELAY.
    /// So re-sending [next - DELAY - 1 .. lastScheduled] every tick means a dropped packet
    /// is always retransmitted while still needed: pure redundancy, no ack protocol, and no
    /// way for a single unlucky loss to deadlock the session.
    /// </summary>
    public class LockstepTests
    {
        private const uint InputDelay = 3;

        [Test]
        public void LockstepOnCleanWireRunsAtFullRate()
        {
            var net = new InProcessNetwork(latencyTicks: 0);
            RunLockstep(net, netTicks: 1000, minProgress: 990);
        }

        [Test]
        public void LockstepOverLossyLatentWireStaysInSync()
        {
            // The BRIEFING M2 stress profile, roughly: real-ish latency, reordering jitter, 5% loss.
            //
            // Throughput note (this is the M3 motivation, quantified): with INPUT_DELAY (3)
            // SMALLER than the one-way transit (~7 net ticks here), lockstep cannot run at
            // full speed — peers consume a (DELAY+1)-tick burst of inputs, then wait out the
            // rest of the round trip. Expected rate ≈ (DELAY+1)/(DELAY+1+transit) ≈ 4/7 ≈ 57%;
            // this seeded run confirms ~1200 of 2000. Correctness (hash agreement) is still
            // absolute — lockstep trades SPEED for sync, and rollback (M3) buys the speed back.
            var net = new InProcessNetwork(latencyTicks: 6, jitterTicks: 3, lossChance: 0.05f);
            RunLockstep(net, netTicks: 2000, minProgress: 1000);
        }

        private static void RunLockstep(InProcessNetwork net, uint netTicks, uint minProgress)
        {
            var arena = Arena.CreateDefault();
            var state = new[] { GameState.CreateInitial(), GameState.CreateInitial() };
            var ring = new[] { new InputRing(), new InputRing() };
            var lastScheduled = new[] { InputDelay, InputDelay };
            var hashes = new[] { new Dictionary<uint, ulong>(), new Dictionary<uint, ulong>() };
            ITransport[] transport = { net.EndpointA, net.EndpointB };
            var inputs = new PlayerInput[2];
            var buf = new byte[InputCodec.MessageSize];

            // Pre-seed the delay window: both peers agree by convention that ticks
            // 1..DELAY are None for everyone. Without this, both deadlock at tick 1
            // waiting for inputs nobody ever scheduled.
            for (int p = 0; p < 2; p++)
            {
                for (uint t = 1; t <= InputDelay; t++)
                {
                    ring[p].Record(t, 0, PlayerInput.None);
                    ring[p].Record(t, 1, PlayerInput.None);
                }
            }

            for (uint netTick = 1; netTick <= netTicks; netTick++)
            {
                net.Poll(netTick);

                for (int p = 0; p < 2; p++)
                {
                    // 1. drain
                    while (transport[p].TryReceive(out var pkt))
                    {
                        InputCodec.Read(pkt, out uint t, out int who, out PlayerInput pi);
                        if (who != p) ring[p].Record(t, who, pi);
                    }

                    // 2. schedule-once: keep our inputs scheduled through next+DELAY. The
                    //    `while` matters — a multi-tick advance moves `next` by several ticks
                    //    at once, and EVERY tick needs exactly one scheduled input (an `if`
                    //    here leaves gaps that stall both peers forever).
                    uint next = state[p].Tick + 1;
                    while (lastScheduled[p] < next + InputDelay)
                    {
                        lastScheduled[p]++;
                        ring[p].Record(lastScheduled[p], p, ScriptedInput(p, lastScheduled[p]));
                    }

                    // 3. resend window
                    uint windowStart = next > InputDelay + 1 ? next - InputDelay - 1 : 1;
                    for (uint t = windowStart; t <= lastScheduled[p]; t++)
                    {
                        InputCodec.Write(buf, t, p, ring[p].Get(t, p));
                        transport[p].Send(buf);
                    }

                    // 4. advance while the gate allows (cap mirrors MaxStepsPerFrame)
                    for (int step = 0; step < 5; step++)
                    {
                        next = state[p].Tick + 1;
                        if (!ring[p].Has(next, 0) || !ring[p].Has(next, 1)) break;
                        inputs[0] = ring[p].Get(next, 0);
                        inputs[1] = ring[p].Get(next, 1);
                        Simulation.Tick(state[p], arena, inputs);
                        hashes[p][state[p].Tick] = state[p].Hash();
                    }
                }
            }

            uint confirmed = Math.Min(state[0].Tick, state[1].Tick);
            Assert.That(confirmed, Is.GreaterThanOrEqualTo(minProgress),
                "lockstep barely progressed — scheduling or delivery bug");
            for (uint t = 1; t <= confirmed; t++)
                Assert.That(hashes[1][t], Is.EqualTo(hashes[0][t]), $"peers desynced at tick {t}");
        }

        /// <summary>Varied, deterministic inputs: movement, fire, dash, and a sweeping turret.</summary>
        private static PlayerInput ScriptedInput(int player, uint tick)
        {
            InputButtons b = InputButtons.None;
            uint phase = tick + (uint)(player * 31);
            if (phase % 3 == 0) b |= InputButtons.Forward;
            if (phase % 7 == 0) b |= InputButtons.Back;
            if (phase % 5 == 0) b |= (player == 0 ? InputButtons.Left : InputButtons.Right);
            if (phase % 13 == 0) b |= InputButtons.Fire;
            if (phase % 97 == 0) b |= InputButtons.Dash;
            return new PlayerInput(b, (int)((tick * 7 + (uint)player * 100) % 2048));
        }
    }
}

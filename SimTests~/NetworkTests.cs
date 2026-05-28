using NUnit.Framework;
using Tanks.Net;
using Tanks.Sim;

namespace Tanks.Tests
{
    public class NetworkTests
    {
        [Test]
        public void CodecRoundTrips()
        {
            var input = new PlayerInput(InputButtons.Forward | InputButtons.Fire | InputButtons.Right);
            var bytes = InputCodec.ToBytes(12345u, 1, input);

            Assert.That(bytes.Length, Is.EqualTo(InputCodec.MessageSize));
            InputCodec.Read(bytes, out uint tick, out int player, out PlayerInput decoded);

            Assert.That(tick, Is.EqualTo(12345u));
            Assert.That(player, Is.EqualTo(1));
            Assert.That(decoded, Is.EqualTo(input));
        }

        [Test]
        public void ZeroLatencyDeliversImmediately()
        {
            var net = new InProcessNetwork(latencyTicks: 0);
            net.EndpointA.Send(new byte[] { 1, 2, 3 });
            net.Poll(0);

            Assert.That(net.EndpointB.TryReceive(out var data), Is.True);
            Assert.That(data, Is.EqualTo(new byte[] { 1, 2, 3 }));
            Assert.That(net.EndpointA.TryReceive(out _), Is.False, "sender should not receive its own packet");
        }

        [Test]
        public void LatencyDelaysDelivery()
        {
            var net = new InProcessNetwork(latencyTicks: 3);
            net.EndpointA.Send(new byte[] { 9 });

            net.Poll(0); net.Poll(1); net.Poll(2);
            Assert.That(net.EndpointB.TryReceive(out _), Is.False, "should not arrive before the latency window");

            net.Poll(3);
            Assert.That(net.EndpointB.TryReceive(out _), Is.True, "should arrive exactly at the latency tick");
        }

        [Test]
        public void TotalLossDropsEverything()
        {
            var net = new InProcessNetwork(latencyTicks: 0, jitterTicks: 0, lossChance: 1f);
            net.EndpointA.Send(new byte[] { 42 });
            net.Poll(0);
            Assert.That(net.EndpointB.TryReceive(out _), Is.False);
        }

        /// <summary>
        /// The seam proof: two independent simulations, each owning one player, exchange their
        /// inputs over the (fake) wire and apply the merged set. They must stay bit-identical
        /// every tick. This is exactly the loop lockstep netcode will run — minus the input
        /// delay and prediction, which is the session's work.
        /// </summary>
        [Test]
        public void InputsExchangedOverWireKeepSimsInLockstep()
        {
            var net = new InProcessNetwork(latencyTicks: 0);
            var arena = Arena.CreateDefault();
            var simA = GameState.CreateInitial();
            var simB = GameState.CreateInitial();

            var mergedA = new[] { PlayerInput.None, PlayerInput.None };
            var mergedB = new[] { PlayerInput.None, PlayerInput.None };

            for (uint t = 0; t < 1000; t++)
            {
                PlayerInput localA = ScriptedInput(0, t); // peer A owns player 0
                PlayerInput localB = ScriptedInput(1, t); // peer B owns player 1

                // Send each peer's local input across the wire.
                net.EndpointA.Send(InputCodec.ToBytes(t, 0, localA));
                net.EndpointB.Send(InputCodec.ToBytes(t, 1, localB));
                net.Poll(t);

                // Each peer assembles the full input set: local + whatever arrived.
                mergedA[0] = localA;
                while (net.EndpointA.TryReceive(out var data))
                {
                    InputCodec.Read(data, out _, out int pl, out PlayerInput pi);
                    mergedA[pl] = pi;
                }

                mergedB[1] = localB;
                while (net.EndpointB.TryReceive(out var data))
                {
                    InputCodec.Read(data, out _, out int pl, out PlayerInput pi);
                    mergedB[pl] = pi;
                }

                Simulation.Tick(simA, arena, mergedA);
                Simulation.Tick(simB, arena, mergedB);

                Assert.That(simB.Hash(), Is.EqualTo(simA.Hash()), $"peers desynced at tick {t}");
            }
        }

        private static PlayerInput ScriptedInput(int player, uint tick)
        {
            InputButtons b = InputButtons.None;
            uint phase = tick + (uint)(player * 23);
            if (phase % 3 == 0) b |= InputButtons.Forward;
            if (phase % 5 == 0) b |= (player == 0 ? InputButtons.Left : InputButtons.Right);
            if (phase % 13 == 0) b |= InputButtons.Fire;
            return new PlayerInput(b);
        }
    }
}

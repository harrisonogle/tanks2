using NUnit.Framework;

namespace Tanks.Tests
{
    /// <summary>
    /// Tests for the in-process fake transport's delivery semantics (immediate delivery,
    /// latency windows, loss). The wire format and the lockstep exchange loop are the
    /// session's work (M1/M2) — those tests get written then, against the real driver.
    /// </summary>
    public class NetworkTests
    {
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
    }
}

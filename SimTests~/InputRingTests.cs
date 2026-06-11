using NUnit.Framework;
using Tanks.Net;
using Tanks.Sim;

namespace Tanks.Tests
{
    public class InputRingTests
    {
        [Test]
        public void RecordedInputRoundTrips()
        {
            var ring = new InputRing();
            var a = new PlayerInput(InputButtons.Forward | InputButtons.Fire, turretAim: 123);
            var b = new PlayerInput(InputButtons.Back, turretAim: 1500);

            ring.Record(42, 0, a);
            ring.Record(42, 1, b);

            Assert.That(ring.Has(42, 0), Is.True);
            Assert.That(ring.Has(42, 1), Is.True);
            Assert.That(ring.Get(42, 0), Is.EqualTo(a));
            Assert.That(ring.Get(42, 1), Is.EqualTo(b));
        }

        [Test]
        public void MissingTickOrPlayerReadsAsAbsent()
        {
            var ring = new InputRing();
            ring.Record(42, 0, new PlayerInput(InputButtons.Forward));

            Assert.That(ring.Has(42, 1), Is.False, "other player not recorded");
            Assert.That(ring.Has(43, 0), Is.False, "other tick not recorded");
            Assert.That(ring.Get(43, 0), Is.EqualTo(PlayerInput.None));
        }

        [Test]
        public void NewTickEvictsTheSlotItWrapsOnto()
        {
            var ring = new InputRing();
            ring.Record(5, 0, new PlayerInput(InputButtons.Fire));

            // 5 + Capacity lands on the same slot: claiming it must evict tick 5 entirely,
            // including the OTHER player's flag — stale data must read as missing, never wrong.
            ring.Record(5 + InputRing.Capacity, 1, new PlayerInput(InputButtons.Back));

            Assert.That(ring.Has(5, 0), Is.False, "old tick evicted");
            Assert.That(ring.Has(5 + InputRing.Capacity, 1), Is.True);
            Assert.That(ring.Has(5 + InputRing.Capacity, 0), Is.False, "evicting tick only has its own writer");
        }

        [Test]
        public void ClearEmptiesEverything()
        {
            var ring = new InputRing();
            ring.Record(7, 0, new PlayerInput(InputButtons.Left));
            ring.Record(7, 1, new PlayerInput(InputButtons.Right));

            ring.Clear();

            Assert.That(ring.Has(7, 0), Is.False);
            Assert.That(ring.Has(7, 1), Is.False);
        }
    }
}

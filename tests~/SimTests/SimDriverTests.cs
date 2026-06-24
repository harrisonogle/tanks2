using NUnit.Framework;
using Tanks.Net;
using Tanks.Sim;

namespace Tanks.Tests
{
    /// <summary>
    /// Exercises the pure driver's fixed-tick accumulator and reset, with a scripted input
    /// source — no Unity, no devices. This is the kind of testability the extraction buys:
    /// the loop the runtime runs is now drivable directly from .NET.
    /// </summary>
    public class SimDriverTests
    {
        /// <summary>A deterministic input source for tests — always returns the same input.</summary>
        private sealed class FixedInputSource : IInputSource
        {
            private readonly PlayerInput _input;
            public FixedInputSource(PlayerInput input) { _input = input; }
            public PlayerInput Sample() => _input;
            public void Reset() { }
        }

        private static SimDriver NewDriver(PlayerInput? input = null)
        {
            var config = new SimConfig();
            var pi = input ?? PlayerInput.None;
            var sources = new IInputSource[] { new FixedInputSource(pi), new FixedInputSource(pi) };
            return new SimDriver(config, new Simulation(config), Arena.CreateDefault(config), sources, historyCapacity: 256);
        }

        [Test]
        public void AdvanceRunsFixedTicksFromAccumulatedTime()
        {
            var d = NewDriver();
            uint start = d.State.Tick;

            d.Advance(3.5 / 60.0);   // 3.5 steps' worth -> 3 ticks, 0.5 step left over
            Assert.That(d.State.Tick - start, Is.EqualTo(3u));

            d.Advance(0.5 / 60.0);   // leftover + 0.5 -> exactly 1 more step
            Assert.That(d.State.Tick - start, Is.EqualTo(4u));
        }

        [Test]
        public void AdvanceClampsRunawayCatchUp()
        {
            var d = NewDriver();
            uint start = d.State.Tick;

            d.Advance(1.0);          // 60 steps' worth, clamped to MaxStepsPerFrame (5)
            Assert.That(d.State.Tick - start, Is.EqualTo(5u));
        }

        [Test]
        public void ResetReturnsToInitialTick()
        {
            var d = NewDriver(input: new PlayerInput(InputButtons.Forward));
            uint start = d.State.Tick;

            d.Advance(10.0 / 60.0);
            Assert.That(d.State.Tick, Is.GreaterThan(start), "state should have advanced");

            d.ResetMatch();
            Assert.That(d.State.Tick, Is.EqualTo(start), "reset should restore the initial tick");
        }
    }
}

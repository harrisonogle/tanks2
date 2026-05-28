using NUnit.Framework;
using Tanks.Sim;

namespace Tanks.Tests
{
    /// <summary>
    /// These are the tests rollback depends on. If any fails, online sync is impossible —
    /// so they're the canary for the whole netcode effort.
    /// </summary>
    public class DeterminismTests
    {
        // A varied but fully deterministic scripted input for one player at a given tick.
        private static PlayerInput Scripted(int player, uint tick)
        {
            InputButtons b = InputButtons.None;
            uint phase = tick + (uint)(player * 17);
            if (phase % 4 == 0) b |= InputButtons.Forward;
            if (phase % 7 == 0) b |= InputButtons.Back;
            if (phase % 3 == 0) b |= (player == 0 ? InputButtons.Left : InputButtons.Right);
            if (phase % 11 == 0) b |= InputButtons.Fire;
            return new PlayerInput(b);
        }

        private static PlayerInput[] InputsAt(uint tick)
            => new[] { Scripted(0, tick), Scripted(1, tick) };

        [Test]
        public void SameInputsProduceIdenticalHashesEveryTick()
        {
            var arena = Arena.CreateDefault();
            var a = GameState.CreateInitial();
            var b = GameState.CreateInitial();

            for (uint t = 0; t < 2000; t++)
            {
                var inputs = InputsAt(t);
                Simulation.Tick(a, arena, inputs);
                Simulation.Tick(b, arena, inputs);
                Assert.That(b.Hash(), Is.EqualTo(a.Hash()), $"diverged at tick {t}");
            }
        }

        [Test]
        public void CloneThenContinue_MatchesUnclonedRun()
        {
            var arena = Arena.CreateDefault();
            var original = GameState.CreateInitial();

            // Advance to a mid-match tick, then branch a clone.
            for (uint t = 0; t < 300; t++)
                Simulation.Tick(original, arena, InputsAt(t));

            var clone = original.Clone();
            Assert.That(clone.Hash(), Is.EqualTo(original.Hash()));

            // Continue both with identical inputs — they must stay identical.
            for (uint t = 300; t < 900; t++)
            {
                var inputs = InputsAt(t);
                Simulation.Tick(original, arena, inputs);
                Simulation.Tick(clone, arena, inputs);
                Assert.That(clone.Hash(), Is.EqualTo(original.Hash()), $"clone diverged at tick {t}");
            }
        }

        [Test]
        public void CopyFrom_RestoresStateExactly()
        {
            var arena = Arena.CreateDefault();
            var live = GameState.CreateInitial();
            var snapshot = GameState.CreateInitial();

            for (uint t = 0; t < 200; t++)
                Simulation.Tick(live, arena, InputsAt(t));

            snapshot.CopyFrom(live);

            // Mutate live further...
            for (uint t = 200; t < 260; t++)
                Simulation.Tick(live, arena, InputsAt(t));

            // ...then restore and re-run; we must land exactly where live is.
            live.CopyFrom(snapshot);
            for (uint t = 200; t < 260; t++)
                Simulation.Tick(live, arena, InputsAt(t));

            var reference = snapshot.Clone();
            for (uint t = 200; t < 260; t++)
                Simulation.Tick(reference, arena, InputsAt(t));

            Assert.That(live.Hash(), Is.EqualTo(reference.Hash()));
        }
    }
}

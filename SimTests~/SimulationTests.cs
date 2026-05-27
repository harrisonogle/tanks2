using NUnit.Framework;
using Tankz.Sim;

namespace Tankz.Tests
{
    /// <summary>Gameplay sanity checks — crude, but they pin down the core rules.</summary>
    public class SimulationTests
    {
        private static PlayerInput[] Both(PlayerInput p0, PlayerInput p1) => new[] { p0, p1 };
        private static PlayerInput Btn(InputButtons b) => new PlayerInput(b);

        [Test]
        public void TankMovesForwardAlongFacing()
        {
            var arena = Arena.CreateDefault();
            var s = GameState.CreateInitial();
            Fixed startX = s.Tanks[0].X; // P0 faces +X

            for (int i = 0; i < 10; i++)
                Simulation.Tick(s, arena, Both(Btn(InputButtons.Forward), PlayerInput.None));

            Assert.That(s.Tanks[0].X.Raw, Is.GreaterThan(startX.Raw), "P0 should have moved in +X");
        }

        [Test]
        public void ArenaBoundsClampTank()
        {
            var arena = Arena.CreateDefault();
            var s = GameState.CreateInitial();

            // Drive P0 into the +X wall for a long time.
            for (int i = 0; i < 2000; i++)
                Simulation.Tick(s, arena, Both(Btn(InputButtons.Forward), PlayerInput.None));

            Fixed maxX = arena.Width - SimConfig.TankRadius;
            Assert.That(s.Tanks[0].X.Raw, Is.LessThanOrEqualTo(maxX.Raw + 2), "tank should not leave the arena");
        }

        [Test]
        public void FiringSpawnsExactlyOneBullet()
        {
            var arena = Arena.CreateDefault();
            var s = GameState.CreateInitial();

            Simulation.Tick(s, arena, Both(Btn(InputButtons.Fire), PlayerInput.None));

            int active = 0;
            foreach (var b in s.Bullets) if (b.Active) active++;
            Assert.That(active, Is.EqualTo(1));
        }

        [Test]
        public void FireCooldownLimitsRateOfFire()
        {
            var arena = Arena.CreateDefault();
            var s = GameState.CreateInitial();

            // Hold fire for a few ticks; cooldown should prevent a burst.
            for (int i = 0; i < 5; i++)
                Simulation.Tick(s, arena, Both(Btn(InputButtons.Fire), PlayerInput.None));

            int active = 0;
            foreach (var b in s.Bullets) if (b.Active) active++;
            Assert.That(active, Is.EqualTo(1), "cooldown should gate firing");
        }

        [Test]
        public void BulletReflectsOffArenaWall()
        {
            var arena = Arena.CreateDefault();
            var s = GameState.CreateInitial();

            // P0 faces +X; fire and let the shell travel into the right wall and bounce.
            Simulation.Tick(s, arena, Both(Btn(InputButtons.Fire), PlayerInput.None));

            // Find the shell.
            int idx = -1;
            for (int i = 0; i < s.Bullets.Length; i++) if (s.Bullets[i].Active) { idx = i; break; }
            Assert.That(idx, Is.GreaterThanOrEqualTo(0));
            Assert.That(s.Bullets[idx].VX.Raw, Is.GreaterThan(0), "shell starts moving +X");

            // Step until it reflects (VX flips sign) or we give up.
            bool reflected = false;
            for (int i = 0; i < 1000 && s.Bullets[idx].Active; i++)
            {
                Simulation.Tick(s, arena, Both(PlayerInput.None, PlayerInput.None));
                if (s.Bullets[idx].Active && s.Bullets[idx].VX.Raw < 0) { reflected = true; break; }
            }
            Assert.That(reflected, Is.True, "shell should bounce off the right wall and travel -X");
        }
    }
}

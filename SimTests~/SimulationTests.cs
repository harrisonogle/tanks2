using NUnit.Framework;
using Tanks.Sim;

namespace Tanks.Tests
{
    /// <summary>Gameplay sanity checks — crude, but they pin down the core rules.</summary>
    public class SimulationTests
    {
        private static PlayerInput[] Both(PlayerInput p0, PlayerInput p1) => new[] { p0, p1 };
        private static PlayerInput Btn(InputButtons b) => new PlayerInput(b);

        [Test]
        public void ForwardInputMovesTankInPositiveYRegardlessOfBody()
        {
            var arena = Arena.CreateDefault();
            var s = GameState.CreateInitial();
            Fixed startX = s.Tanks[0].X;
            Fixed startY = s.Tanks[0].Y;

            for (int i = 0; i < 10; i++)
                Simulation.Tick(s, arena, Both(Btn(InputButtons.Forward), PlayerInput.None));

            // 8-way world-space movement: Forward = +Y, never along body facing.
            Assert.That(s.Tanks[0].Y.Raw, Is.GreaterThan(startY.Raw), "Forward should translate the tank in +Y");
            Assert.That(s.Tanks[0].X, Is.EqualTo(startX), "Forward only affects Y; X should be unchanged");
        }

        [Test]
        public void ArenaBoundsClampTank()
        {
            var arena = Arena.CreateDefault();
            var s = GameState.CreateInitial();

            // Drive P0 into the +Y wall for a long time.
            for (int i = 0; i < 2000; i++)
                Simulation.Tick(s, arena, Both(Btn(InputButtons.Forward), PlayerInput.None));

            Fixed maxY = arena.Height - SimConfig.TankRadius;
            Assert.That(s.Tanks[0].Y.Raw, Is.LessThanOrEqualTo(maxY.Raw + 2), "tank should not leave the arena");
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

        [Test]
        public void BulletFiresAlongTurretAimNotBody()
        {
            var arena = Arena.CreateDefault();
            var s = GameState.CreateInitial();
            // P0's body faces +X (angle 0). Aim the turret at +Y (quarter turn CCW).
            int aimAtPositiveY = Trig.AngleCount / 4;
            var fire = new PlayerInput(InputButtons.Fire, aimAtPositiveY);

            Simulation.Tick(s, arena, new[] { fire, PlayerInput.None });

            // Find the spawned shell.
            int idx = -1;
            for (int i = 0; i < s.Bullets.Length; i++) if (s.Bullets[i].Active) { idx = i; break; }
            Assert.That(idx, Is.GreaterThanOrEqualTo(0), "shell should have spawned");

            // Velocity should point +Y (the turret direction), NOT +X (the body direction).
            // cos(90°) is exactly 0 in the lookup table for a quarter turn, so VX is exactly 0.
            Assert.That(s.Bullets[idx].VY.Raw, Is.GreaterThan(0), "shell should travel in +Y (turret aim)");
            Assert.That(s.Bullets[idx].VX.Raw, Is.EqualTo(0), "shell VX should be zero for a 90° aim");
        }

        [Test]
        public void DiagonalMovementSpeedMatchesCardinalSpeed()
        {
            var arena = Arena.CreateDefault();

            // Cardinal run: Right for N ticks.
            var sCardinal = GameState.CreateInitial();
            Fixed cardStartX = sCardinal.Tanks[0].X;
            for (int i = 0; i < 30; i++)
                Simulation.Tick(sCardinal, arena, Both(Btn(InputButtons.Right), PlayerInput.None));
            Fixed cardinalDist = sCardinal.Tanks[0].X - cardStartX;

            // Diagonal run: Right + Forward for the same N ticks. Distance traveled (the
            // hypotenuse) should equal the cardinal distance — that's what 1/sqrt(2)
            // per-axis scaling buys us.
            var sDiag = GameState.CreateInitial();
            Fixed diagStartX = sDiag.Tanks[0].X;
            Fixed diagStartY = sDiag.Tanks[0].Y;
            for (int i = 0; i < 30; i++)
                Simulation.Tick(sDiag, arena, Both(Btn(InputButtons.Right | InputButtons.Forward), PlayerInput.None));
            Fixed dx = sDiag.Tanks[0].X - diagStartX;
            Fixed dy = sDiag.Tanks[0].Y - diagStartY;
            Fixed diagDist = Fixed.Sqrt(dx * dx + dy * dy);

            // Allow ~1% slack for fixed-point quantization of the 1/sqrt(2) constant.
            float ratio = diagDist.ToFloat() / cardinalDist.ToFloat();
            Assert.That(ratio, Is.EqualTo(1f).Within(0.01f),
                $"diagonal {diagDist} should ~= cardinal {cardinalDist}");
        }

        [Test]
        public void BodyAngleSnapsToInputDirection()
        {
            var arena = Arena.CreateDefault();
            var s = GameState.CreateInitial();

            // Forward => +Y => AngleCount/4 (90°).
            Simulation.Tick(s, arena, Both(Btn(InputButtons.Forward), PlayerInput.None));
            Assert.That(s.Tanks[0].Angle, Is.EqualTo(Trig.AngleCount / 4));

            // Forward + Right => NE => AngleCount/8 (45°).
            Simulation.Tick(s, arena, Both(Btn(InputButtons.Forward | InputButtons.Right), PlayerInput.None));
            Assert.That(s.Tanks[0].Angle, Is.EqualTo(Trig.AngleCount / 8));

            // No directional input => body angle holds its last value.
            Simulation.Tick(s, arena, Both(PlayerInput.None, PlayerInput.None));
            Assert.That(s.Tanks[0].Angle, Is.EqualTo(Trig.AngleCount / 8));

            // Back + Left => SW => 5 * AngleCount/8 (225°).
            Simulation.Tick(s, arena, Both(Btn(InputButtons.Back | InputButtons.Left), PlayerInput.None));
            Assert.That(s.Tanks[0].Angle, Is.EqualTo(5 * Trig.AngleCount / 8));
        }
    }
}

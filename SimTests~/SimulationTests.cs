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

        [Test]
        public void BulletDetonatesOnSecondSurfaceContact()
        {
            // Hand-built bullet path with no tanks in the way: shoot along y=2 (below the
            // interior pillars at y[6,14]) so the only surfaces it can hit are the left and
            // right arena bounds. With BulletMaxBounces = 1, the first bound reflects and
            // the second detonates the shell.
            var arena = Arena.CreateDefault();
            var s = GameState.CreateInitial();
            s.Tanks[0].Health = 0;
            s.Tanks[1].Health = 0;
            s.Bullets[0] = new Bullet
            {
                Active = true,
                X = Fixed.FromInt(5),
                Y = Fixed.FromInt(2),
                VX = -SimConfig.BulletSpeed,
                VY = Fixed.Zero,
                Owner = 0,
                BouncesLeft = SimConfig.BulletMaxBounces,
                Life = SimConfig.BulletLifeTicks,
                Grace = 0,
            };

            int bounceCount = 0;
            int prevSign = Fixed.Sign(s.Bullets[0].VX);
            for (int i = 0; i < 2000 && s.Bullets[0].Active; i++)
            {
                Simulation.Tick(s, arena, new[] { PlayerInput.None, PlayerInput.None });
                if (!s.Bullets[0].Active) break;
                int sign = Fixed.Sign(s.Bullets[0].VX);
                if (sign != 0 && sign != prevSign) { bounceCount++; prevSign = sign; }
            }

            Assert.That(s.Bullets[0].Active, Is.False, "shell should detonate after its second surface contact");
            Assert.That(bounceCount, Is.EqualTo(1), "shell should bounce exactly once before detonating");
        }

        [Test]
        public void DashWithNoMovementInputDoesNothing()
        {
            var arena = Arena.CreateDefault();
            var s = GameState.CreateInitial();
            Fixed startX = s.Tanks[0].X;
            Fixed startY = s.Tanks[0].Y;

            // Dash bit set, no direction bits.
            var dashOnly = new PlayerInput(InputButtons.Dash);
            for (int i = 0; i < 5; i++)
                Simulation.Tick(s, arena, Both(dashOnly, PlayerInput.None));

            Assert.That(s.Tanks[0].X, Is.EqualTo(startX), "no direction input => no movement");
            Assert.That(s.Tanks[0].Y, Is.EqualTo(startY), "no direction input => no movement");
            Assert.That(s.Tanks[0].DashTicks, Is.EqualTo(0), "dash should not have started");
            Assert.That(s.Tanks[0].DashCooldown, Is.EqualTo(0), "cooldown should not have been set");
        }

        [Test]
        public void DashMultipliesDisplacementWhileActive()
        {
            var arena = Arena.CreateDefault();

            // Baseline: hold Right for DashDurationTicks ticks (no dash bit).
            var sBaseline = GameState.CreateInitial();
            var rightOnly = new PlayerInput(InputButtons.Right);
            for (int i = 0; i < SimConfig.DashDurationTicks; i++)
                Simulation.Tick(sBaseline, arena, Both(rightOnly, PlayerInput.None));
            Fixed baseDx = sBaseline.Tanks[0].X - Fixed.FromInt(3);

            // With dash: tick 0 carries Dash+Right (the rising edge triggers); remaining ticks
            // carry Right only (so the dash stays active for its full duration but isn't
            // retriggered — same as what the sampler's edge detection would produce live).
            var sDash = GameState.CreateInitial();
            var dashAndRight = new PlayerInput(InputButtons.Right | InputButtons.Dash);
            Simulation.Tick(sDash, arena, Both(dashAndRight, PlayerInput.None));
            for (int i = 1; i < SimConfig.DashDurationTicks; i++)
                Simulation.Tick(sDash, arena, Both(rightOnly, PlayerInput.None));
            Fixed dashDx = sDash.Tanks[0].X - Fixed.FromInt(3);

            float ratio = dashDx.ToFloat() / baseDx.ToFloat();
            Assert.That(ratio, Is.EqualTo((float)SimConfig.DashSpeedMultiplier).Within(0.02f),
                $"dash should multiply displacement by ~{SimConfig.DashSpeedMultiplier}x");
        }

        [Test]
        public void DashSecondTriggerWithinCooldownIsIgnored()
        {
            var arena = Arena.CreateDefault();
            var s = GameState.CreateInitial();
            var dashAndRight = new PlayerInput(InputButtons.Right | InputButtons.Dash);

            // First tick: dash triggers and sets the full cooldown.
            Simulation.Tick(s, arena, Both(dashAndRight, PlayerInput.None));
            Assert.That(s.Tanks[0].DashTicks, Is.GreaterThan(0), "first dash should start");
            Assert.That(s.Tanks[0].DashCooldown, Is.EqualTo(SimConfig.DashCooldownTicks));

            // Second tick keeps asserting Dash (bypassing the sampler's edge detection). The
            // sim's cooldown gate must reject the retrigger: cooldown should keep decrementing
            // rather than resetting back to the full DashCooldownTicks value.
            Simulation.Tick(s, arena, Both(dashAndRight, PlayerInput.None));
            Assert.That(s.Tanks[0].DashCooldown, Is.LessThan(SimConfig.DashCooldownTicks),
                "second dash within cooldown should be ignored");
        }
    }
}

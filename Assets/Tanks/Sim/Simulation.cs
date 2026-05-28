namespace Tanks.Sim
{
    /// <summary>
    /// The deterministic step function: <c>state + inputs -> next state</c>.
    ///
    /// This is the ENTIRE game. It has no Unity dependency, no floats, no wall-clock time,
    /// and no I/O. Given identical (state, inputs) it always produces an identical next state
    /// on any machine. That property is what makes lockstep and rollback possible:
    ///   - lockstep: both peers call Tick with the same inputs every tick
    ///   - rollback: re-call Tick over a buffer of past ticks when a prediction was wrong
    /// </summary>
    public static class Simulation
    {
        /// <summary>Advance the state by exactly one tick. Mutates <paramref name="state"/> in place.</summary>
        public static void Tick(GameState state, Arena arena, PlayerInput[] inputs)
        {
            UpdateTanks(state, arena, inputs);
            UpdateBullets(state, arena);
            state.Tick++;
        }

        private static void UpdateTanks(GameState state, Arena arena, PlayerInput[] inputs)
        {
            for (int i = 0; i < SimConfig.PlayerCount; i++)
            {
                ref Tank t = ref state.Tanks[i];
                if (t.FireCooldown > 0) t.FireCooldown--;
                if (!t.Alive) continue;

                PlayerInput input = inputs[i];

                // Turret aim is absolute — the input layer maintains it locally and sends it
                // every tick (mouse / right-stick / accumulated Q/E offset). The sim just
                // adopts it. Balance / turn-speed is the sampler's concern, not the sim's.
                t.TurretAngle = input.TurretAim;

                // Direct 8-way movement in WORLD space (faithful to original Tanks — NOT
                // tank-controls). Forward/Back/Left/Right bits map to +Y/-Y/-X/+X; the body
                // visually snaps to face the input direction (no rotation inertia yet).
                int wx = (input.Right ? 1 : 0) - (input.Left ? 1 : 0);
                int wy = (input.Forward ? 1 : 0) - (input.Back ? 1 : 0);
                if (wx != 0 || wy != 0)
                {
                    // Scale per-axis on diagonals so total speed equals cardinal speed.
                    Fixed perAxis = (wx != 0 && wy != 0) ? SimConfig.DiagonalMoveSpeed : SimConfig.TankMoveSpeed;
                    Fixed dx = perAxis * wx;
                    Fixed dy = perAxis * wy;
                    t.X = ResolveTankX(t.X + dx, t.Y, arena);
                    t.Y = ResolveTankY(t.X, t.Y + dy, arena);
                    t.Angle = AngleFromInputDir(wx, wy);
                }

                // Firing
                if (input.Fire && t.FireCooldown == 0 && CountOwnedBullets(state, i) < SimConfig.MaxBulletsPerPlayer)
                {
                    if (TrySpawnBullet(state, i, t))
                        t.FireCooldown = SimConfig.FireCooldownTicks;
                }
            }
        }

        private static Fixed ResolveTankX(Fixed x, Fixed y, Arena arena)
        {
            Fixed r = SimConfig.TankRadius;
            x = Fixed.Clamp(x, r, arena.Width - r);
            foreach (var w in arena.Walls)
            {
                if (w.OverlapsSquare(x, y, r))
                    x = x < w.CenterX ? w.MinX - r : w.MaxX + r;
            }
            return x;
        }

        private static Fixed ResolveTankY(Fixed x, Fixed y, Arena arena)
        {
            Fixed r = SimConfig.TankRadius;
            y = Fixed.Clamp(y, r, arena.Height - r);
            foreach (var w in arena.Walls)
            {
                if (w.OverlapsSquare(x, y, r))
                    y = y < w.CenterY ? w.MinY - r : w.MaxY + r;
            }
            return y;
        }

        private static int CountOwnedBullets(GameState state, int owner)
        {
            int n = 0;
            for (int i = 0; i < state.Bullets.Length; i++)
                if (state.Bullets[i].Active && state.Bullets[i].Owner == owner) n++;
            return n;
        }

        private static bool TrySpawnBullet(GameState state, int owner, in Tank t)
        {
            int slot = -1;
            for (int i = 0; i < state.Bullets.Length; i++)
            {
                if (!state.Bullets[i].Active) { slot = i; break; }
            }
            if (slot < 0) return false;

            // Bullets travel along the TURRET angle, not the body angle.
            FixVec2 dir = Trig.Direction(t.TurretAngle);
            // Spawn just outside the muzzle so the shell doesn't instantly self-collide.
            Fixed offset = SimConfig.TankRadius + SimConfig.BulletRadius + Fixed.FromFloat(0.05f);

            state.Bullets[slot] = new Bullet
            {
                Active = true,
                X = t.X + dir.X * offset,
                Y = t.Y + dir.Y * offset,
                VX = dir.X * SimConfig.BulletSpeed,
                VY = dir.Y * SimConfig.BulletSpeed,
                Owner = owner,
                BouncesLeft = SimConfig.BulletMaxBounces,
                Life = SimConfig.BulletLifeTicks,
                Grace = SimConfig.BulletSpawnGraceTicks,
            };
            return true;
        }

        private static void UpdateBullets(GameState state, Arena arena)
        {
            for (int i = 0; i < state.Bullets.Length; i++)
            {
                ref Bullet b = ref state.Bullets[i];
                if (!b.Active) continue;

                if (b.Grace > 0) b.Grace--;
                if (--b.Life <= 0) { b.Active = false; continue; }

                StepBulletWithReflection(ref b, arena);
                if (b.BouncesLeft < 0) { b.Active = false; continue; }

                CheckBulletHitsTanks(state, ref b);
            }
        }

        private static void StepBulletWithReflection(ref Bullet b, Arena arena)
        {
            Fixed nx = b.X + b.VX;
            Fixed ny = b.Y + b.VY;

            // Reflect off the outer arena bounds.
            if (nx < Fixed.Zero) { nx = -nx; b.VX = -b.VX; b.BouncesLeft--; }
            else if (nx > arena.Width) { nx = arena.Width * 2 - nx; b.VX = -b.VX; b.BouncesLeft--; }

            if (ny < Fixed.Zero) { ny = -ny; b.VY = -b.VY; b.BouncesLeft--; }
            else if (ny > arena.Height) { ny = arena.Height * 2 - ny; b.VY = -b.VY; b.BouncesLeft--; }

            // Reflect off interior wall blocks (escape along the shallowest-penetration axis).
            foreach (var w in arena.Walls)
            {
                if (!w.ContainsPoint(nx, ny)) continue;

                Fixed distLeft = nx - w.MinX;
                Fixed distRight = w.MaxX - nx;
                Fixed distBottom = ny - w.MinY;
                Fixed distTop = w.MaxY - ny;

                Fixed penX = Fixed.Min(distLeft, distRight);
                Fixed penY = Fixed.Min(distBottom, distTop);

                if (penX < penY)
                {
                    nx = distLeft < distRight ? w.MinX : w.MaxX;
                    b.VX = -b.VX;
                }
                else
                {
                    ny = distBottom < distTop ? w.MinY : w.MaxY;
                    b.VY = -b.VY;
                }
                b.BouncesLeft--;
            }

            b.X = nx;
            b.Y = ny;
        }

        private static void CheckBulletHitsTanks(GameState state, ref Bullet b)
        {
            Fixed reach = SimConfig.TankRadius + SimConfig.BulletRadius;
            for (int j = 0; j < SimConfig.PlayerCount; j++)
            {
                ref Tank t = ref state.Tanks[j];
                if (!t.Alive) continue;
                if (j == b.Owner && b.Grace > 0) continue; // own shell is harmless until grace expires

                if (Fixed.Abs(b.X - t.X) <= reach && Fixed.Abs(b.Y - t.Y) <= reach)
                {
                    t.Health = 0;
                    b.Active = false;
                    return;
                }
            }
        }

        /// <summary>
        /// Body facing for an 8-way input vector. Each component is in {-1, 0, +1}; the caller
        /// guards against (0, 0). Returns one of 8 angle indices snapped to 45° steps (CCW from +X).
        /// </summary>
        private static int AngleFromInputDir(int wx, int wy)
        {
            int octant =
                wy > 0 ? (wx > 0 ? 1 : (wx < 0 ? 3 : 2)) :
                wy < 0 ? (wx > 0 ? 7 : (wx < 0 ? 5 : 6)) :
                (wx > 0 ? 0 : 4);
            return octant * (Trig.AngleCount / 8);
        }
    }
}

using System;

namespace Tankz.Sim
{
    public struct Tank
    {
        public Fixed X;
        public Fixed Y;
        public int Angle;        // facing; turret == body for now
        public int Health;
        public int FireCooldown; // ticks until allowed to fire again

        public readonly bool Alive => Health > 0;
    }

    public struct Bullet
    {
        public bool Active;
        public Fixed X;
        public Fixed Y;
        public Fixed VX;
        public Fixed VY;
        public int Owner;
        public int BouncesLeft;
        public int Life;   // ticks remaining
        public int Grace;  // ticks during which this shell ignores its owner
    }

    /// <summary>
    /// The COMPLETE, authoritative state of the match at one tick. This is the only thing
    /// the simulation reads/writes. Because it is fully self-contained and cheaply cloneable,
    /// rollback is just "keep old copies and re-run <see cref="Simulation.Tick"/>".
    /// </summary>
    public sealed class GameState
    {
        public uint Tick;
        public uint Rng;            // deterministic xorshift seed (reserved for future use; hashed for safety)
        public Tank[] Tanks;        // length SimConfig.PlayerCount
        public Bullet[] Bullets;    // length SimConfig.MaxBullets

        public static GameState CreateInitial(uint seed = 0x1234_5678u)
        {
            var s = new GameState
            {
                Tick = 0,
                Rng = seed == 0 ? 1u : seed,
                Tanks = new Tank[SimConfig.PlayerCount],
                Bullets = new Bullet[SimConfig.MaxBullets],
            };

            for (int i = 0; i < SimConfig.PlayerCount; i++)
            {
                var spawn = SimConfig.SpawnPosition(i);
                s.Tanks[i] = new Tank
                {
                    X = spawn.X,
                    Y = spawn.Y,
                    Angle = SimConfig.SpawnAngle(i),
                    Health = SimConfig.TankMaxHealth,
                    FireCooldown = 0,
                };
            }
            return s;
        }

        /// <summary>Deep copy. Used to snapshot states for the rollback ring buffer.</summary>
        public GameState Clone()
        {
            var c = new GameState
            {
                Tick = Tick,
                Rng = Rng,
                Tanks = (Tank[])Tanks.Clone(),
                Bullets = (Bullet[])Bullets.Clone(),
            };
            return c;
        }

        /// <summary>Copy another state's contents into this one without allocating (for pooling later).</summary>
        public void CopyFrom(GameState other)
        {
            Tick = other.Tick;
            Rng = other.Rng;
            Array.Copy(other.Tanks, Tanks, Tanks.Length);
            Array.Copy(other.Bullets, Bullets, Bullets.Length);
        }

        public int CountAlive()
        {
            int n = 0;
            for (int i = 0; i < Tanks.Length; i++)
                if (Tanks[i].Alive) n++;
            return n;
        }

        /// <summary>
        /// Deterministic 64-bit fingerprint of the full state. If two machines disagree on
        /// this for the same tick, the simulation has diverged — the single most important
        /// signal when debugging rollback. The debug HUD shows the low bits live.
        /// </summary>
        public ulong Hash()
        {
            ulong h = 14695981039346656037UL; // FNV offset basis
            h = Mix(h, (int)Tick);
            h = Mix(h, (int)Rng);
            for (int i = 0; i < Tanks.Length; i++)
            {
                ref readonly Tank t = ref Tanks[i];
                h = Mix(h, t.X.Raw);
                h = Mix(h, t.Y.Raw);
                h = Mix(h, t.Angle);
                h = Mix(h, t.Health);
                h = Mix(h, t.FireCooldown);
            }
            for (int i = 0; i < Bullets.Length; i++)
            {
                ref readonly Bullet b = ref Bullets[i];
                h = Mix(h, b.Active ? 1 : 0);
                if (!b.Active) continue; // inactive slots have no meaningful payload
                h = Mix(h, b.X.Raw);
                h = Mix(h, b.Y.Raw);
                h = Mix(h, b.VX.Raw);
                h = Mix(h, b.VY.Raw);
                h = Mix(h, b.Owner);
                h = Mix(h, b.BouncesLeft);
                h = Mix(h, b.Life);
                h = Mix(h, b.Grace);
            }
            return h;
        }

        private static ulong Mix(ulong h, int value)
        {
            unchecked
            {
                h ^= (uint)value;
                h *= 1099511628211UL; // FNV prime
            }
            return h;
        }
    }
}

namespace Tankz.Sim
{
    /// <summary>
    /// All gameplay tuning lives here as deterministic constants. Speeds are expressed
    /// PER TICK (the sim has no concept of wall-clock time — it only advances in ticks).
    /// </summary>
    public static class SimConfig
    {
        public const int TickRate = 60;            // simulation ticks per second
        public const int PlayerCount = 2;          // 1v1
        public const int MaxBullets = 16;          // shared bullet pool size
        public const int MaxBulletsPerPlayer = 5;  // max simultaneous shells per tank (Tankz-style)

        // Arena (origin at bottom-left; units are arbitrary "meters").
        public static readonly Fixed ArenaWidth = Fixed.FromInt(32);
        public static readonly Fixed ArenaHeight = Fixed.FromInt(20);

        // Tank
        public static readonly Fixed TankRadius = Fixed.FromFloat(0.6f);     // treated as a square half-extent for collision
        public static readonly Fixed TankMoveSpeed = Fixed.FromFloat(0.12f); // units per tick (~7.2 u/s)
        public const int TankTurnSpeed = 24;                                 // angle units per tick (~253 deg/s)
        public const int TankMaxHealth = 1;                                  // one-shot kill, classic Tankz

        // Bullet
        public static readonly Fixed BulletSpeed = Fixed.FromFloat(0.28f);   // units per tick (~16.8 u/s)
        public static readonly Fixed BulletRadius = Fixed.FromFloat(0.12f);
        public const int BulletMaxBounces = 4;
        public const int BulletLifeTicks = 60 * 8;
        public const int BulletSpawnGraceTicks = 6;                          // ticks during which a shell ignores its owner
        public const int FireCooldownTicks = 24;

        // Spawns: P0 on the left facing +X, P1 on the right facing -X.
        public static FixVec2 SpawnPosition(int player)
            => player == 0
                ? new FixVec2(Fixed.FromInt(3), Fixed.FromInt(10))
                : new FixVec2(Fixed.FromInt(29), Fixed.FromInt(10));

        public static int SpawnAngle(int player)
            => player == 0 ? 0 : Trig.AngleCount / 2; // 0 = +X, half = -X
    }
}

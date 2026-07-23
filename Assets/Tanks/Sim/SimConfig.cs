namespace Tanks.Sim;

/// <summary>
/// All gameplay tuning, as deterministic values on an injectable instance. Speeds are
/// expressed PER TICK (the sim has no concept of wall-clock time — it only advances in
/// ticks). Constructed once at the composition root and threaded through the sim; both
/// peers must use identical config (it's part of the session's agreed configuration).
/// </summary>
public sealed class SimConfig
{
    public int TickRate { get; } = 60;            // simulation ticks per second
    public int PlayerCount { get; } = 2;          // 1v1
    public int MaxBullets { get; } = 32;          // shared bullet pool size
    public int MaxBulletsPerPlayer { get; } = 5;  // max simultaneous shells per tank (Tanks-style)

    // Arena (origin at bottom-left; units are arbitrary "meters").
    public Fixed ArenaWidth { get; } = Fixed.FromInt(32);
    public Fixed ArenaHeight { get; } = Fixed.FromInt(20);

    // Tank
    public Fixed TankRadius { get; } = Fixed.FromFloat(0.6f);     // treated as a square half-extent for collision
    public Fixed TankMoveSpeed { get; } = Fixed.FromFloat(0.12f); // units per tick
                                                                  // Per-axis speed for diagonals so total speed matches cardinal (TankMoveSpeed * 1/sqrt(2)).
    public Fixed DiagonalMoveSpeed { get; }                       // set in ctor (depends on TankMoveSpeed)
    public int KeyboardTurretTurnSpeed { get; } = 24;             // angle units per tick (~253 deg/s); keyboard turret-aim fallback
    public int TankMaxHealth { get; } = 1;                        // one-shot kill, classic Tanks

    // Dash
    public int DashDurationTicks { get; } = 9;                    // length of the speed burst when you trigger a dash (~100 ms at 60 Hz)
    public int DashCooldownTicks { get; } = 15;                   // minimum ticks between dashes
    public int DashSpeedMultiplier { get; } = 3;                  // movement speed factor while DashTicks > 0

    // Bullet
    public Fixed BulletSpeed { get; } = Fixed.FromFloat(0.20f);   // units per tick
    public Fixed BulletRadius { get; } = Fixed.FromFloat(0.12f);
    public int BulletMaxBounces { get; } = 1;                     // single ricochet; the next surface contact detonates the shell (classic Tanks!)
    public int BulletLifeTicks { get; } = 60 * 8;
    public int FireCooldownTicks { get; } = 12;

    public SimConfig()
    {
        DiagonalMoveSpeed = TankMoveSpeed * Fixed.FromFloat(0.70710678f);
    }

    // Spawns: P0 on the left facing +X, P1 on the right facing -X.
    public FixVec2 SpawnPosition(int player)
        => player == 0
            ? new FixVec2(Fixed.FromInt(3), Fixed.FromInt(10))
            : new FixVec2(Fixed.FromInt(29), Fixed.FromInt(10));

    public int SpawnAngle(int player)
        => player == 0 ? 0 : Trig.AngleCount / 2; // 0 = +X, half = -X
}

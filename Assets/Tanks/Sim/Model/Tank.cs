namespace Tanks.Sim;

public struct Tank
{
    public Fixed X;
    public Fixed Y;
    public int Angle;         // body facing (movement direction)
    public int TurretAngle;   // turret facing (fire direction); set absolute from PlayerInput each tick
    public int Health;
    public int FireCooldown;  // ticks until allowed to fire again
    public int DashTicks;     // remaining ticks of the speed burst (>0 = currently dashing)
    public int DashCooldown;  // ticks until allowed to dash again

    public readonly bool Alive => Health > 0;
}
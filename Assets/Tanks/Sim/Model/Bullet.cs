namespace Tanks.Sim;

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
}
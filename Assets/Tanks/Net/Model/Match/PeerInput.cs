using System.Runtime.InteropServices;
using Tanks.Sim;

namespace Tanks.Net;

[StructLayout(LayoutKind.Explicit, Size = Size)]
public struct PeerInput
{
    public const int Size = 4;
    [FieldOffset(0)] public InputButtons Buttons;
    [FieldOffset(2)] public ushort TurretAim;
}
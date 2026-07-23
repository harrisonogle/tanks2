using System.Runtime.InteropServices;
using Tanks.Sim;

namespace Tanks.Engine;

[StructLayout(LayoutKind.Explicit, Size = Size)]
public struct LocalInputMessage
{
    public const int Size = 4;
    [FieldOffset(0)] public byte Count;
}

[StructLayout(LayoutKind.Explicit, Size = Size)]
public struct LocalInputPayload
{
    public const int Size = 8;
    [FieldOffset(0)] public byte Player;
    [FieldOffset(4)] public PlayerInput Input;
}
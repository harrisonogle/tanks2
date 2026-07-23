using System.Runtime.InteropServices;
using Tanks.Net;

namespace Tanks.Engine;

[StructLayout(LayoutKind.Explicit, Size = Size)]
public struct StartMatchMessage
{
    public const int Size = 36;

    [FieldOffset(0)] public PeerId PeerId;
    [FieldOffset(16)] public NetAddress NetAddress;
}
using System.Runtime.InteropServices;

namespace Tanks.Net;

// Command sent by game thread to network thread
[StructLayout(LayoutKind.Explicit, Size = Size)]
public struct Connect
{
    public const int Size = 36;
    [FieldOffset(0)] public PeerId PeerId;
    [FieldOffset(16)] public NetAddress Address;
}
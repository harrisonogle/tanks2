using System.Runtime.InteropServices;

namespace Tanks.Net;

[StructLayout(LayoutKind.Explicit, Size = Size)]
public struct DisconnectPeer
{
    public const int Size = PeerId.Size;
    [FieldOffset(0)] public PeerId RemotePeerId;
}
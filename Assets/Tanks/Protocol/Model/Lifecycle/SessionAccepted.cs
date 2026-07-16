using System.Runtime.InteropServices;

namespace Tanks.Net;

[StructLayout(LayoutKind.Explicit, Size = Size)]
public struct SessionAccepted
{
    public const int Size = 48;
    [FieldOffset(0)] public PeerId LocalPeerId;
    [FieldOffset(16)] public PeerId RemotePeerId;
    [FieldOffset(32)] public SessionId LocalSessionId;
    [FieldOffset(40)] public SessionId RemoteSessionId;
}
using System.Runtime.InteropServices;

namespace Tanks.Net;

[StructLayout(LayoutKind.Explicit, Size = Size)]
public struct SessionStateChanged
{
    public const int Size = 8;
    [FieldOffset(0)] public SessionState PreviousState;
    [FieldOffset(4)] public SessionState CurrentState;
    [FieldOffset(2)] public EndReason EndReason;    // meaningful when CurrentState == Closed
    [FieldOffset(3)] public DisconnectReason PeerReason;  // meaningful when EndReason == RemoteClosed
    // 4 bytes padding
}
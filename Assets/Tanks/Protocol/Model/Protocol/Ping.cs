using System.Runtime.InteropServices;

namespace Tanks.Net;

[StructLayout(LayoutKind.Explicit, Size = Size)]
public struct Ping
{
    public const int Size = 28;
    [FieldOffset(0)] public ushort Length; // whole message length
    [FieldOffset(4)] public ushort AppProtocolId;
    [FieldOffset(6)] public ushort AppProtocolVersion;
    [FieldOffset(8)] public Nonce InitiatorNonce;
    [FieldOffset(24)] public ushort InitiatorPubKeyOffset;
    [FieldOffset(26)] public ushort PingSignatureOffset; // signature of the ping message
}
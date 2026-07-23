using System.Runtime.InteropServices;

namespace Tanks.Net;

[StructLayout(LayoutKind.Explicit, Size = Size)]
public struct Pong
{
    public const int Size = 28;
    [FieldOffset(0)] public Nonce EchoedInitiatorNonce;
    [FieldOffset(16)] public ushort Length; // total length of message
    [FieldOffset(18)] public ushort AppProtocolId; // echoed from Ping
    [FieldOffset(20)] public ushort AppProtocolVersion; // echoed from Ping
    [FieldOffset(22)] public ushort ResponderPubKeyOffset;
    [FieldOffset(24)] public ushort EncryptedResponderNonceOffset; // encrypt with remote peer public key
    [FieldOffset(26)] public ushort PongSignatureOffset; // sign with local peer private key
}
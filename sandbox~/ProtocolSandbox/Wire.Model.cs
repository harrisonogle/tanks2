using Tanks.Sim;

namespace Tanks.Net;

public partial struct GameProtocolHeader
{
    public const int MinimumLength = 8; // 1 + 1 + 1 + 1 + 4
    public byte Magic;
    public byte MajorVersion;
    public byte MinorVersion;
    public byte Flags;
    public int SessionId;
}

public enum MessageType : int
{
    None = 0, // don't put a real one here so it needs to be explicitly specified; no implicit parsing
    Ping = 1, // handshake leg #1
    Pong = 2, // handshake leg #2
    PingPong = 3, // handshake leg #3
    Disconnect = 4,
    Input = 5, // send inputs
    StateHash = 6, // send game state hash
    Advantage = 7, // "frame advantage" (GGPO)
}

public partial struct MessageHeader
{
    public const int MinimumLength = 8; // 4 + 4
    public MessageType Type;
    public int Length;
}

public partial struct PingMessage
{
    public const int MinimumLength = 24; // 8 + 8 + 8
    public long Nonce;
    public long PeerId;
    public long SendTimestamp;
}

public partial struct PongMessage
{
    public const int MinimumLength = 40; // 8 + 8 + 8 + 8 + 8
    public long InitiatorNonce;
    public long ResponderNonce;
    public long PeerId;
    public long EchoTimestamp;
    public long SendTimestamp;
}

public partial struct PingPongMessage
{
    public const int MinimumLength = 16; // 8 + 8
    public long ResponderNonce;
    public long EchoTimestamp;
}

public enum DisconnectReason : byte
{
    Unknown = 0,
    PeerInitiated = 1,
    Timeout = 2,
    ProtocolError = 3,
    VersionMismatch = 4,
}

public partial struct DisconnectMessage
{
    public const int MinimumLength = 1;
    public DisconnectReason Reason;
}

public struct PeerInput
{
    public const int MinimumLength = 4; // 2 + 2
    public InputButtons Buttons;
    public ushort TurretAim;
}

public partial struct InputMessage
{
    public const int MinimumLength = 5; // 4 + 1
    public uint InputTick;
    public byte InputCount; // send the last `InputCount` inputs; 0 is allowed
    public PeerInput[] Inputs; // ascending order; inputs for `InputTick` are last; array can be oversized (length > `InputCount`)
}

public partial struct StateHashMessage
{
    public const int MinimumLength = 12; // 4 + 8
    public uint Tick;
    public ulong Hash;
}

public partial struct AdvantageMessage
{
    public const int MinimumLength = 8; // 4 + 4
    public uint CurrentTick; // current tick
    public uint LastReceivedTick; // highest tick from remote where we received inputs
}

using System.Buffers.Binary;
using System.Diagnostics;
using System.Net.Mail;
using Tanks.Sim;

namespace Tanks.Net;

public struct GameProtocolHeader
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

public struct MessageHeader
{
    public const int MinimumLength = 8; // 4 + 4
    public MessageType Type;
    public int Length;
}

public struct PingMessage
{
    public const int MinimumLength = 24; // 8 + 8 + 8
    public long Nonce;
    public long PeerId;
    public long SendTimestamp;
}

public struct PongMessage
{
    public const int MinimumLength = 40; // 8 + 8 + 8 + 8 + 8
    public long InitiatorNonce;
    public long ResponderNonce;
    public long PeerId;
    public long EchoTimestamp;
    public long SendTimestamp;
}

public struct PingPongMessage
{
    public const int MinimumLength = 16; // 8 + 8
    public long ResponderNonce;
    public long EchoTimestamp;
}

public enum DisconnectReason : byte
{
    Unknown = 0,
}

public struct DisconnectMessage
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

public struct InputMessage
{
    public const int MinimumLength = 5; // 4 + 1
    public uint InputTick;
    public byte InputCount; // send the last `InputCount` inputs; 0 is allowed
    public PeerInput[] Inputs; // ascending order; inputs for `InputTick` are last; array can be oversized (length > `InputCount`)
}

public struct StateHashMessage
{
    public const int MinimumLength = 12; // 4 + 8
    public uint Tick;
    public ulong Hash;
}

public struct AdvantageMessage
{
    public const int MinimumLength = 8; // 4 + 4
    public uint SeqTick; // current tick
    public uint AckTick; // highest tick from remote where we received inputs
}

public sealed class GameProtocolSerializer
{
    public bool TryReadPacketHeader(ReadOnlySpan<byte> buffer, out GameProtocolHeader header)
    {
        if (buffer.Length < header.MinimumLength)
        {
            header = default;
            return false;
        }

        header = new GameProtocolHeader();
        header.Magic = buffer[0];
        header.MajorVersion = buffer[1];
        header.MinorVersion = buffer[2];
        header.Flags = buffer[3];
        header.SessionId = BinaryPrimitives.ReadInt32BigEndian(buffer[4..8]);

        return true;
    }

    public bool TryWritePacketHeader(ref GameProtocolHeader header, Span<byte> buffer, out int bytesWritten)
    {
        if (buffer.Length < GameProtocolHeader.MinimumLength)
        {
            bytesWritten = 0;
            return false;
        }

        buffer[0] = header.Magic;
        buffer[1] = header.MajorVersion;
        buffer[2] = header.MinorVersion;
        buffer[3] = header.Flags;
        BinaryPrimitives.WriteInt32BigEndian(buffer[4..8], header.SessionId);
        bytesWritten = 8;
        return true;
    }

    public bool TryReadMessageHeader(ReadOnlySpan<byte> buffer, out MessageHeader header)
    {
        if (buffer.Length < MessageHeader.MinimumLength)
        {
            header = default;
            return false;
        }

        header = new MessageHeader();
        header.Type = (MessageType)BinaryPrimitives.ReadInt32BigEndian(buffer[0..4]);
        header.Length = BinaryPrimitives.ReadInt32BigEndian(buffer[4..8]);
        return true;
    }

    public bool TryWriteMessageHeader(ref MessageHeader header, Span<byte> buffer, out int bytesWritten)
    {
        if (buffer.Length < MessageHeader.MinimumLength)
        {
            bytesWritten = 0;
            return false;
        }

        BinaryPrimitives.WriteInt32BigEndian(buffer[0..4], (int)header.Type);
        BinaryPrimitives.WriteInt32BigEndian(buffer[4..8], header.Length);
        bytesWritten = 8;
        return true;
    }

    public bool TryReadPingMessage(ReadOnlySpan<byte> buffer, out PingMessage message)
    {
        if (buffer.Length < PingMessage.MinimumLength)
        {
            message = default;
            return false;
        }

        message = new PingMessage();
        message.Nonce = BinaryPrimitives.ReadInt64BigEndian(buffer[0..8]);
        message.PeerId = BinaryPrimitives.ReadInt64BigEndian(buffer[8..16]);
        message.SendTimestamp = BinaryPrimitives.ReadInt64BigEndian(buffer[16..24]);
        return true;
    }

    public bool TryWritePingMessage(ref PingMessage message, Span<byte> buffer, out int bytesWritten)
    {
        if (buffer.Length < PingMessage.MinimumLength)
        {
            bytesWritten = 0;
            return false;
        }

        // Can do it like this if message is not simple. Usually need to if it has any dynamic length fields

        bytesWritten = 0;

        BinaryPrimitives.WriteInt64BigEndian(buffer[(bytesWritten)..(bytesWritten + 8)], message.Nonce);
        bytesWritten += 8;

        BinaryPrimitives.WriteInt64BigEndian(buffer[(bytesWritten)..(bytesWritten + 8)], message.PeerId);
        bytesWritten += 8;

        BinaryPrimitives.WriteInt64BigEndian(buffer[(bytesWritten)..(bytesWritten + 8)], message.SendTimestamp);
        bytesWritten += 8;

        return true;
    }

    public bool TryReadPongMessage(ReadOnlySpan<byte> buffer, out PongMessage message)
    {
        if (buffer.Length < PongMessage.MinimumLength)
        {
            message = default;
            return false;
        }

        message = new PongMessage();

        message.InitiatorNonce = BinaryPrimitives.ReadInt64BigEndian(buffer[..8]);
        buffer = buffer[8..];

        message.ResponderNonce = BinaryPrimitives.ReadInt64BigEndian(buffer[..8]);
        buffer = buffer[8..];

        message.PeerId = BinaryPrimitives.ReadInt64BigEndian(buffer[..8]);
        buffer = buffer[8..];

        message.EchoTimestamp = BinaryPrimitives.ReadInt64BigEndian(buffer[..8]);
        buffer = buffer[8..];

        message.SendTimestamp = BinaryPrimitives.ReadInt64BigEndian(buffer[..8]);
        buffer = buffer[8..];

        return true;
    }

    public bool TryWritePongMessage(ref PongMessage message, Span<byte> buffer, out int bytesWritten)
    {
        if (buffer.Length < PongMessage.MinimumLength)
        {
            bytesWritten = 0;
            return false;
        }

        int initialLength = buffer.Length;

        BinaryPrimitives.WriteInt64BigEndian(buffer[..8], message.InitiatorNonce);
        buffer = buffer[8..];

        BinaryPrimitives.WriteInt64BigEndian(buffer[..8], message.ResponderNonce);
        buffer = buffer[8..];

        BinaryPrimitives.WriteInt64BigEndian(buffer[..8], message.PeerId);
        buffer = buffer[8..];

        BinaryPrimitives.WriteInt64BigEndian(buffer[..8], message.EchoTimestamp);
        buffer = buffer[8..];

        BinaryPrimitives.WriteInt64BigEndian(buffer[..8], message.SendTimestamp);
        buffer = buffer[8..];

        bytesWritten = initialLength - buffer.Length;
        return true;
    }

    public bool TryReadPingPongMessage(ReadOnlySpan<byte> buffer, out PingPongMessage message)
    {
        if (buffer.Length < PingPongMessage.MinimumLength)
        {
            message = default;
            return false;
        }

        message = new PingPongMessage();
        message.ResponderNonce = BinaryPrimitives.ReadInt64BigEndian(buffer[0..8]);
        message.EchoTimestamp = BinaryPrimitives.ReadInt64BigEndian(buffer[8..16]);
        return true;
    }

    public bool TryWritePingPongMessage(ref PingPongMessage message, Span<byte> buffer, out int bytesWritten)
    {
        if (buffer.Length < PingPongMessage.MinimumLength)
        {
            bytesWritten = 0;
            return false;
        }

        BinaryPrimitives.WriteInt64BigEndian(buffer[0..8], message.ResponderNonce);
        BinaryPrimitives.WriteInt64BigEndian(buffer[8..16], message.EchoTimestamp);
        bytesWritten = 16;
        return true;
    }

    public bool TryReadDisconnectMessage(ReadOnlySpan<byte> buffer, out DisconnectMessage message)
    {
        if (buffer.Length < DisconnectMessage.MinimumLength)
        {
            message = default;
            return false;
        }

        message = new DisconnectMessage();
        message.Reason = (DisconnectReason)buffer[0];
        return true;
    }

    public bool TryWriteDisconnectMessage(ref DisconnectMessage message, Span<byte> buffer, out int bytesWritten)
    {
        if (buffer.Length < DisconnectMessage.MinimumLength)
        {
            bytesWritten = 0;
            return false;
        }

        buffer[0] = (byte)message.Reason;
        bytesWritten = 1;
        return true;
    }

    public bool TryReadInputMessage(ReadOnlySpan<byte> buffer, out InputMessage message)
    {
        if (buffer.Length < InputMessage.MinimumLength)
        {
            message = default;
            return false;
        }

        message = new InputMessage();

        message.InputTick = BinaryPrimitives.ReadUInt32BigEndian(buffer[..4]);
        buffer = buffer[4..];

        message.InputCount = buffer[0];
        buffer = buffer[1..];

        if (message.InputCount < 0)
        {
            throw new InvalidOperationException();
        }

        message.Inputs = new PeerInput[message.InputCount];
        for (int i = 0; i < message.InputCount; i++)
        {
            ref PeerInput peerInput = ref message.Inputs[i]; // it's initialized to default; at any rate, we'll set all fields

            if (buffer.Length < 2) return false;
            peerInput.Buttons = (InputButtons)BinaryPrimitives.ReadUInt16BigEndian(buffer[..2]);
            buffer = buffer[2..];

            if (buffer.Length < 2) return false;
            peerInput.TurretAim = BinaryPrimitives.ReadUInt16BigEndian(buffer[..2]);
            buffer = buffer[2..];
        }

        return true;
    }

    public bool TryWriteInputMessage(ref InputMessage message, Span<byte> buffer, out int bytesWritten)
    {
        if (message.InputCount < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(message.InputCount));
        }

        if (buffer.Length < InputMessage.MinimumLength)
        {
            bytesWritten = 0;
            return false;
        }

        int initialLength = buffer.Length;

        BinaryPrimitives.WriteUInt32BigEndian(buffer[..4], message.InputTick);
        buffer = buffer[4..];

        buffer[0] = message.InputCount;
        buffer = buffer[1..];

        for (int i = 0; i < message.InputCount; i++)
        {
            ref PeerInput peerInput = ref message.Inputs[i];

            if (buffer.Length < 2) goto Fail;
            BinaryPrimitives.WriteUInt16BigEndian(buffer[..2], (ushort)peerInput.Buttons);
            buffer = buffer[2..];

            if (buffer.Length < 2) goto Fail;
            BinaryPrimitives.WriteUInt16BigEndian(buffer[..2], (ushort)peerInput.Buttons);
            buffer = buffer[2..];
        }

        bytesWritten = initialLength - buffer.Length;
        return true;

    Fail:
        bytesWritten = initialLength - buffer.Length;
        return false;
    }

    public bool TryReadStateHashMessage(ReadOnlySpan<byte> buffer, out StateHashMessage message)
    {
        if (buffer.Length < StateHashMessage.MinimumLength)
        {
            message = default;
            return false;
        }

        message = new StateHashMessage();
        message.Tick = BinaryPrimitives.ReadUInt32BigEndian(buffer[0..4]);
        message.Hash = BinaryPrimitives.ReadUInt64BigEndian(buffer[4..12]);
        return true;
    }
    public bool TryWriteStateHashMessage(ref StateHashMessage message, Span<byte> buffer, out int bytesWritten)
    {
        if (buffer.Length < StateHashMessage.MinimumLength)
        {
            bytesWritten = 0;
            return false;
        }

        BinaryPrimitives.WriteUInt32BigEndian(buffer[0..4], message.Tick);
        BinaryPrimitives.WriteUInt64BigEndian(buffer[4..12], message.Hash);
        bytesWritten = 12;
        return true;
    }

    public bool TryReadAdvantageMessage(ReadOnlySpan<byte> buffer, out AdvantageMessage message)
    {
        if (buffer.Length < AdvantageMessage.MinimumLength)
        {
            message = default;
            return false;
        }

        message = new AdvantageMessage();
        message.SeqTick = BinaryPrimitives.ReadUInt32BigEndian(buffer[0..4]);
        message.AckTick = BinaryPrimitives.ReadUInt32BigEndian(buffer[4..8]);
        return true;
    }

    public bool TryWriteAdvantageMessage(ref AdvantageMessage message, Span<byte> buffer, out int bytesWritten)
    {
        if (buffer.Length < AdvantageMessage.MinimumLength)
        {
            bytesWritten = 0;
            return false;
        }

        BinaryPrimitives.WriteUInt32BigEndian(buffer[0..4], message.SeqTick);
        BinaryPrimitives.WriteUInt32BigEndian(buffer[4..8], message.AckTick);
        bytesWritten = 8;
        return true;
    }
}
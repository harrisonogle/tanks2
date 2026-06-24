using System.Buffers.Binary;
using Tanks.Sim;

partial struct GameProtocolHeader
{
    public static bool TryRead(ReadOnlySpan<byte> buffer, out GameProtocolHeader header, out int bytesRead)
    {
        if (buffer.Length < GameProtocolHeader.MinimumLength)
        {
            header = default;
            bytesRead = 0;
            return false;
        }

        header = new GameProtocolHeader();
        header.Magic = buffer[0];
        header.MajorVersion = buffer[1];
        header.MinorVersion = buffer[2];
        header.Flags = buffer[3];
        header.SessionId = BinaryPrimitives.ReadInt32BigEndian(buffer[4..8]);
        bytesRead = 8;
        return true;
    }

    public static bool TryWrite(ref GameProtocolHeader header, Span<byte> buffer, out int bytesWritten)
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
}

partial struct MessageHeader
{
    public static bool TryRead(ReadOnlySpan<byte> buffer, out MessageHeader header, out int bytesRead)
    {
        if (buffer.Length < MessageHeader.MinimumLength)
        {
            header = default;
            bytesRead = 0;
            return false;
        }

        header = new MessageHeader();
        header.Type = (MessageType)BinaryPrimitives.ReadInt32BigEndian(buffer[0..4]);
        header.Length = BinaryPrimitives.ReadInt32BigEndian(buffer[4..8]);
        bytesRead = 8;
        return true;
    }

    public static bool TryWrite(ref MessageHeader header, Span<byte> buffer, out int bytesWritten)
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
}

partial struct PingMessage
{
    public static bool TryRead(ReadOnlySpan<byte> buffer, out PingMessage message, out int bytesRead)
    {
        if (buffer.Length < PingMessage.MinimumLength)
        {
            message = default;
            bytesRead = 0;
            return false;
        }

        message = new PingMessage();
        message.Nonce = BinaryPrimitives.ReadInt64BigEndian(buffer[0..8]);
        message.PeerId = BinaryPrimitives.ReadInt64BigEndian(buffer[8..16]);
        message.SendTimestamp = BinaryPrimitives.ReadInt64BigEndian(buffer[16..24]);
        bytesRead = 24;
        return true;
    }

    public static bool TryWrite(ref PingMessage message, Span<byte> buffer, out int bytesWritten)
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
}

partial struct PongMessage
{
    public static bool TryRead(ReadOnlySpan<byte> buffer, out PongMessage message, out int bytesRead)
    {
        if (buffer.Length < PongMessage.MinimumLength)
        {
            message = default;
            bytesRead = 0;
            return false;
        }

        int initialLength = buffer.Length;
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

        bytesRead = initialLength - buffer.Length;
        return true;
    }

    public static bool TryWrite(ref PongMessage message, Span<byte> buffer, out int bytesWritten)
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
}

partial struct PingPongMessage
{
    public static bool TryRead(ReadOnlySpan<byte> buffer, out PingPongMessage message, out int bytesRead)
    {
        if (buffer.Length < PingPongMessage.MinimumLength)
        {
            message = default;
            bytesRead = 0;
            return false;
        }

        message = new PingPongMessage();
        message.ResponderNonce = BinaryPrimitives.ReadInt64BigEndian(buffer[0..8]);
        message.EchoTimestamp = BinaryPrimitives.ReadInt64BigEndian(buffer[8..16]);
        bytesRead = 16;
        return true;
    }

    public static bool TryWrite(ref PingPongMessage message, Span<byte> buffer, out int bytesWritten)
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
}

partial struct DisconnectMessage
{
    public static bool TryRead(ReadOnlySpan<byte> buffer, out DisconnectMessage message, out int bytesRead)
    {
        if (buffer.Length < DisconnectMessage.MinimumLength)
        {
            message = default;
            bytesRead = 0;
            return false;
        }

        message = new DisconnectMessage();
        message.Reason = (DisconnectReason)buffer[0];
        bytesRead = 1;
        return true;
    }

    public static bool TryWrite(ref DisconnectMessage message, Span<byte> buffer, out int bytesWritten)
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
}

partial struct InputMessage
{
    public static bool TryRead(ReadOnlySpan<byte> buffer, out InputMessage message, out int bytesRead)
    {
        if (buffer.Length < InputMessage.MinimumLength)
        {
            message = default;
            bytesRead = 0;
            return false;
        }

        int initialLength = buffer.Length;
        message = new InputMessage();

        message.InputTick = BinaryPrimitives.ReadUInt32BigEndian(buffer[..4]);
        buffer = buffer[4..];

        message.InputCount = buffer[0];
        buffer = buffer[1..];

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

        bytesRead = initialLength - buffer.Length;
        return true;
    }

    public static bool TryWrite(ref InputMessage message, Span<byte> buffer, out int bytesWritten)
    {
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
            BinaryPrimitives.WriteUInt16BigEndian(buffer[..2], (ushort)peerInput.TurretAim);
            buffer = buffer[2..];
        }

        bytesWritten = initialLength - buffer.Length;
        return true;

    Fail:
        bytesWritten = initialLength - buffer.Length;
        return false;
    }
}

partial struct StateHashMessage
{
    public static bool TryRead(ReadOnlySpan<byte> buffer, out StateHashMessage message, out int bytesRead)
    {
        if (buffer.Length < StateHashMessage.MinimumLength)
        {
            message = default;
            bytesRead = 0;
            return false;
        }

        message = new StateHashMessage();
        message.Tick = BinaryPrimitives.ReadUInt32BigEndian(buffer[0..4]);
        message.Hash = BinaryPrimitives.ReadUInt64BigEndian(buffer[4..12]);
        bytesRead = 12;
        return true;
    }
    public bool TryWrite(ref StateHashMessage message, Span<byte> buffer, out int bytesWritten)
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
}

partial struct AdvantageMessage
{
    public static bool TryRead(ReadOnlySpan<byte> buffer, out AdvantageMessage message, out int bytesRead)
    {
        if (buffer.Length < AdvantageMessage.MinimumLength)
        {
            message = default;
            bytesRead = 0;
            return false;
        }

        message = new AdvantageMessage();
        message.SeqTick = BinaryPrimitives.ReadUInt32BigEndian(buffer[0..4]);
        message.AckTick = BinaryPrimitives.ReadUInt32BigEndian(buffer[4..8]);
        bytesRead = 8;
        return true;
    }

    public static bool TryWrite(ref AdvantageMessage message, Span<byte> buffer, out int bytesWritten)
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

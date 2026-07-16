using System.Runtime.InteropServices;

namespace Tanks.Net;

public enum PipeEventKind : byte
{
    None = 0,

    // Wire: both directions.
    ApplicationData = 1,

    // TX: game -> net commands.
    ActiveOpen = 2, // start a new session (active open)
    ActiveClose = 3, // emit disconnect message to remote peer (active close)

    // RX: net -> game lifecycle.
    SessionAccepted = 4, // session created (payload: metadata + initial state)
    SessionEstablished = 5, // handshake completed (payload: none needed - identity via SessionId)
    SessionClosed = 6, // session terminated (payload: EndReason + optional DisconnectReason)
}

[StructLayout(LayoutKind.Explicit, Size = Size)]
public struct PipeEventMetadata
{
    internal const int Size = 12;
    [FieldOffset(0)] public SessionId SessionId;
    [FieldOffset(8)] public PipeEventKind Kind;
    [FieldOffset(9)] public byte Offset; // does not include fixed headroom (NetworkConstants.PipeEventHeadroom)
    [FieldOffset(10)] public ushort Length;
}

[StructLayout(LayoutKind.Explicit, Size = 20)]
public struct PipeEvent
{
    public PipeEvent(BufferHandle buffer, SessionId sid, PipeEventKind kind)
    {
        Metadata.SessionId = sid;
        Metadata.Kind = kind;
        Metadata.Offset = 0;
        Metadata.Length = 0;
        Buffer = buffer;
    }

    public PipeEvent(BufferHandle buffer, SessionId sid, PipeEventKind kind, byte offset, ushort length)
    {
        Metadata.SessionId = sid;
        Metadata.Kind = kind;
        Metadata.Offset = offset;
        Metadata.Length = length;
        Buffer = buffer;
    }

    [FieldOffset(0)] public BufferHandle Buffer;
    [FieldOffset(8)] public PipeEventMetadata Metadata;
}
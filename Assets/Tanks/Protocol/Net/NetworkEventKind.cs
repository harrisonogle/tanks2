using System.Runtime.InteropServices;

namespace Tanks.Net;

public enum NetworkEventKind : byte
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
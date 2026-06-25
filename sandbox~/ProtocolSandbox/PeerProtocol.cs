using System.Diagnostics.CodeAnalysis;
using System.Net.WebSockets;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Tanks.Net;

/*
Initiator                          Responder
─────────                          ─────────
CLOSED                             CLOSED
  │ user opens                       │
  │ send PING ───────────────────>   │
  ▼                                  │ on PING:
PING_SENT                            │ send PONG
  │              <────────────────── │
  │ on PONG:                         ▼
  │ send PINGPONG ──────────────>  PING_RCVD
  ▼                                  │ on PINGPONG:
ESTABLISHED                          ▼
                                   ESTABLISHED
*/

public enum PeerProtocolState
{
    Closed = 0,
    PingSent = 1,
    PingReceived = 2,
    Established = 3,
}

public sealed class PeerMessage
{
    public MessageType Type;

    public PingMessage Ping;

    public PongMessage Pong;

    public PingPongMessage PingPong;

    public DisconnectMessage Disconnect;

    public InputMessage Input;

    public StateHashMessage StateHash;

    public AdvantageMessage Advantage;

    public void Reset()
    {
        Type = MessageType.None;
        // Don't clear all the structs; that's a lot of unnecessary copying.
        // That means observing an incorrect field is undefined (which it already was).
    }
}

public interface IPeerProtocol
{
    public bool TrySend(PeerMessage message);
    public bool TryReceive([NotNullWhen(true)] out PeerMessage? message);
}
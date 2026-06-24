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

public enum GameProtocolState
{
    Closed = 0,
    PingSent = 1,
    PingReceived = 2,
    Established = 3,
}

public sealed class GameProtocol
{
}
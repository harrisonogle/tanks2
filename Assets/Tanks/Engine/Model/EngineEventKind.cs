using System.Runtime.InteropServices;

namespace Tanks.Engine;

public enum EngineEventKind : byte
{
    None = 0,

    // TX: render -> engine
    StartMatch = 1,
    StartLocalMatch = 2,
    LeaveMatch = 3,
    LocalInput = 4,

    // RX: engine -> render
    ConnectingToPeer = 5,
    MatchStarted = 6,
    GameState = 7,
    MatchEnded = 8,
}
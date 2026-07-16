using Tanks.Sim;

namespace Tanks.Net;

/// <summary>
/// What the Unity shell (SimRunner) needs from a running match, regardless of whether
/// the opponent is on the couch (<see cref="SimDriver"/>) or across the wire
/// (<see cref="RollbackDriver"/>).
/// </summary>
public interface IMatchDriver
{
    GameState State { get; }
    Arena Arena { get; }
    SimConfig Config { get; }
    ulong LastHash { get; }

    /// <summary>False when a local reset would desync a networked opponent.</summary>
    bool AllowsLocalReset { get; }

    /// <summary>Accumulate real elapsed time and run the fixed sim ticks that are due.</summary>
    void Advance(double deltaTime);

    void ResetMatch();
}

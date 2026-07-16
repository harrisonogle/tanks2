using Tanks;
using Tanks.Net;
using Tanks.Sim;

namespace Tests;

public sealed class NullLog : ILog
{
    public static readonly NullLog Instance = new();
    public bool IsEnabled(LogLevel level) => false;
    public void Log(ref LogMessage message) => message.Return(); // release any pooled backing
    public void LogInformation(string? message) { }
    public void LogWarning(string? message) { }
    public void LogError(string? message) { }
    public void LogDebug(string? message) { }
    public void LogTrace(string? message) { }
}

/// <summary>Deterministic input source driven by a tick-indexed script.</summary>
public sealed class ScriptedInputSource : IInputSource
{
    private readonly Func<uint, PlayerInput> _script;
    private uint _tick;

    public ScriptedInputSource(Func<uint, PlayerInput> script) => _script = script;

    // The driver samples exactly once per executed tick (replays reuse the recorded
    // ring, not the source), so a simple counter tracks the tick being executed.
    public PlayerInput Sample() => _script(++_tick);
    public void Reset() => _tick = 0;
}

/// <summary>Deterministic input scripts with enough variety to force mispredictions.</summary>
public static class Scripts
{
    public static PlayerInput A(uint tick)
    {
        InputButtons b = InputButtons.None;
        if (tick % 4 == 0) b |= InputButtons.Forward;
        if (tick % 7 == 0) b |= InputButtons.Back;
        if (tick % 3 == 0) b |= InputButtons.Left;
        if (tick % 11 == 0) b |= InputButtons.Fire;
        if (tick % 31 == 0) b |= InputButtons.Dash;
        return new PlayerInput(b, (int)(tick * 5));
    }

    public static PlayerInput B(uint tick)
    {
        InputButtons b = InputButtons.None;
        if (tick % 5 == 0) b |= InputButtons.Forward;
        if (tick % 3 == 0) b |= InputButtons.Right;
        if (tick % 13 == 0) b |= InputButtons.Fire;
        if (tick % 29 == 0) b |= InputButtons.Dash;
        return new PlayerInput(b, (int)(tick * 11));
    }

    public static PeerInput Peer(PlayerInput p) => new PeerInput { Buttons = p.Buttons, TurretAim = p.TurretAim };
}

public static class TestHelpers
{
    /// <summary>
    /// Deliver the remote's input window for frontier tick <paramref name="frontier"/> to a
    /// session, exactly the way the wire would: the sender's last Input.Count ticks in a
    /// ring keyed by tick % Input.Count.
    /// </summary>
    public static void DeliverInputs(Session session, uint frontier, Func<uint, PlayerInput> script)
    {
        Span<PeerInput> window = stackalloc PeerInput[Input.Count];
        for (uint i = 0; i < Input.Count && frontier > i; i++)
        {
            uint tick = frontier - i;
            window[(int)(tick % Input.Count)] = Scripts.Peer(script(tick));
        }
        session.OnInput(frontier, window);
    }

    /// <summary>Serial no-network reference run; returns per-tick hashes (index = tick, [0] unused).</summary>
    public static ulong[] ReferenceHashes(SimConfig config, uint ticks, Func<uint, PlayerInput> p0, Func<uint, PlayerInput> p1)
    {
        var sim = new Simulation(config);
        var arena = Arena.CreateDefault(config);
        var state = GameState.CreateInitial(config);
        var inputs = new PlayerInput[2];
        var hashes = new ulong[ticks + 1];
        for (uint t = 1; t <= ticks; t++)
        {
            inputs[0] = p0(t);
            inputs[1] = p1(t);
            sim.Tick(state, arena, inputs);
            if (state.Tick != t) throw new InvalidOperationException($"reference run tick mismatch: {state.Tick} != {t}");
            hashes[t] = state.Hash();
        }
        return hashes;
    }
}

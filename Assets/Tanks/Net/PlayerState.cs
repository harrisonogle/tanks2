using System;
using System.Diagnostics;
using Tanks.Sim;

namespace Tanks.Net;

public struct PlayerTickInput
{
    public uint Tick;
    public PlayerInput PlayerInput;
}

public sealed class PlayerState
{
    private readonly int _capacity;

    public PlayerState(int capacity)
    {
        ThrowHelper.ThrowIfNegativeOrZero(capacity);

        _capacity = capacity;
        AuthoritativeInput = new PlayerTickInput[_capacity];
        AppliedInput = new PlayerTickInput[_capacity];
        Frontier = default;
    }

    public uint Frontier; // next tick for which we need authoritative input; below frontier => we have inputs on-and-before
    public readonly PlayerTickInput[] AuthoritativeInput;
    public readonly PlayerTickInput[] AppliedInput;
    public int PlayerIndex; // set externally at match start time
    public PlayerInput LatestSample; // meaningful for local players only: the raw LEVEL sample (held buttons + aim)
    public PlayerInput PrevLevel;    // meaningful for local players only: previous minted level, for edge derivation

    public void Reset()
    {
        Array.Clear(AuthoritativeInput, 0, AuthoritativeInput.Length);
        Array.Clear(AppliedInput, 0, AppliedInput.Length);
        Frontier = default;
        LatestSample = default;
        PrevLevel = default;
    }

    public void OnInputSample(PlayerInput playerInput)
    {
        LatestSample = playerInput;
    }

    /// <summary>
    /// Stamp a locally-authored input for <paramref name="tick"/>. This is the minting
    /// station: local streams get their tick assigned HERE, at the tick boundary — never
    /// by the render thread. Stamped inputs are final (they ship to peers immediately).
    /// </summary>
    public void MintLocal(uint tick, in PlayerInput input)
    {
        // First executed tick is 1 (genesis state is tick 0, produced without inputs), so the
        // first mint legally jumps Frontier 0 -> 2. After that, mints are strictly contiguous.
        Debug.Assert(tick >= Frontier, "local stamps are final: never re-mint a tick at or below the frontier");
        Debug.Assert(tick - Frontier <= 1, "local mint must not skip ticks");
        ref var slot = ref AuthoritativeInput[(int)(tick % (uint)_capacity)];
        slot.Tick = tick;
        slot.PlayerInput = input;
        Frontier = tick + 1;
    }

    /// <summary>Authoritative input for a covered tick (caller must check tick &lt; Frontier).</summary>
    public PlayerInput InputAt(uint tick)
    {
        ref readonly var slot = ref AuthoritativeInput[(int)(tick % (uint)_capacity)];
        Debug.Assert(slot.Tick == tick, "authoritative ring hole or aged-out read");
        return slot.PlayerInput;
    }

    /// <summary>
    /// The input to feed the sim for <paramref name="tick"/>: authoritative if covered,
    /// else predicted (repeat the newest authoritative input; default before any arrive).
    /// </summary>
    public PlayerInput ResolveInput(uint tick)
    {
        if (tick < Frontier)
            return InputAt(tick);
        if (Frontier > 0)
            return AuthoritativeInput[(int)((Frontier - 1) % (uint)_capacity)].PlayerInput;
        return default;
    }

    /// <summary>Record what the sim actually consumed for <paramref name="tick"/> (prediction or authoritative).</summary>
    public void RecordApplied(uint tick, in PlayerInput input)
    {
        ref var slot = ref AppliedInput[(int)(tick % (uint)_capacity)];
        slot.Tick = tick;
        slot.PlayerInput = input;
    }

    /// <summary>What the sim consumed for <paramref name="tick"/> (valid within the ring window).</summary>
    public PlayerInput AppliedAt(uint tick)
    {
        ref readonly var slot = ref AppliedInput[(int)(tick % (uint)_capacity)];
        Debug.Assert(slot.Tick == tick, "applied ring hole or aged-out read");
        return slot.PlayerInput;
    }

    public void OnInput(uint inputTick, ReadOnlySpan<PlayerInput> inputs)
    {
        // Recall: Frontier is the first tick for which we need input; all ticks below are covered.
        // A window whose newest tick is exactly Frontier still helps (it carries the tick we need).
        if (inputTick < Frontier)
        {
            // NOTE: This behavior assumes the value of _capacity is consistent with state snapshot (rollback) capacity
            return;
        }

        uint oldFrontier = Frontier;

        int delta = (int)(inputTick - oldFrontier + 1);

        if (delta <= 0)
        {
            // overflow
            return;
        }

        Frontier = inputTick + 1;

        int count = Math.Min(delta, inputs.Length);

        Debug.Assert(inputs.Length <= _capacity); // wrap-safety margin: window never reaches back past live ring slots

        uint tick = inputTick;
        for (int i = 0; i < count; i++, tick--)
        {
            Debug.Assert(tick <= inputTick && tick >= oldFrontier);
            int srcIdx = (int)(tick % inputs.Length);
            int dstIdx = (int)(tick % _capacity);
            ref var slot = ref AuthoritativeInput[dstIdx];
            slot.Tick = tick;
            slot.PlayerInput = inputs[srcIdx];
        }
    }
}

using System;
using Tanks.Sim;

namespace Tanks.Net;

public struct StateHistorySlot
{
    public GameState State;
    public ulong Hash;
    public bool IsEmpty => State is null;
}

/// <summary>
/// Ring buffer of past game snapshots, indexed by tick.
/// </summary>
public sealed class StateHistory
{
    private readonly StateHistorySlot[] _buffer;
    private readonly int _capacity;

    private uint _latestTick;

    public StateHistory(int capacity)
    {
        if (capacity < 1) throw new ArgumentOutOfRangeException(nameof(capacity));
        _capacity = capacity;
        _buffer = new StateHistorySlot[capacity];
        _latestTick = unchecked((uint)-1);
    }

    public uint LatestTick => _latestTick;

    public uint EarliestTick
    {
        get
        {
            uint tick = unchecked((uint)(_latestTick - _capacity));
            return tick > _latestTick ? _latestTick : tick;
        }
    }

    public ulong Record(GameState state)
    {
        if (state.Tick != unchecked(_latestTick + 1))
        {
            throw new InvalidOperationException($"Missing tick(s). tick: {state.Tick}, latest: {_latestTick}");
        }

        int i = (int)(state.Tick % (uint)_capacity);
        ref var slot = ref _buffer[i];

        // Snapshot a copy so later mutation of the live state doesn't corrupt history.
        if (slot.IsEmpty) slot.State = state.Clone();
        else slot.State.CopyFrom(state);

        slot.Hash = slot.State.Hash();
        _latestTick = state.Tick;
        return slot.Hash; // the caller invariably wants it; saves hashing the state twice
    }

    /// <summary>
    /// Rewind the recording frontier to <paramref name="latestTick"/> so a rollback can
    /// re-<see cref="Record"/> from there. Slots above the rewind point keep their stale
    /// pre-rollback contents until the replay overwrites them in order — callers must not
    /// read past the frontier mid-replay.
    /// </summary>
    public void Rewind(uint latestTick)
    {
        if (latestTick > _latestTick)
            throw new ArgumentOutOfRangeException(nameof(latestTick), "Cannot rewind forward.");
        _latestTick = latestTick;
    }

    /// <summary>Returns the snapshot for an exact tick, or null if it's no longer in the buffer.</summary>
    public bool TryGet(uint tick, out StateHistorySlot slot)
    {
        int i = (int)(tick % (uint)_capacity);
        ref var s = ref _buffer[i];
        if (!s.IsEmpty && s.State.Tick == tick)
        {
            slot = s;
            return true;
        }

        slot = default;
        return false;
    }

    public void Clear()
    {
        Array.Clear(_buffer, 0, _capacity);
        _latestTick = unchecked((uint)-1); // so Record(tick 0) passes the contiguity check again
    }
}
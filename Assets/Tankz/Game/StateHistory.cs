using System;
using Tankz.Sim;

namespace Tankz.Game
{
    /// <summary>
    /// Ring buffer of past <see cref="GameState"/> snapshots, indexed by tick. This is the
    /// substrate rollback needs: when a prediction turns out wrong, you fetch the last
    /// known-good snapshot and re-simulate forward from there.
    ///
    /// For the bootstrap it just records history (nothing reads it yet). Wiring the rollback
    /// loop that consumes it is the session's job — the data is already here for you.
    /// </summary>
    public sealed class StateHistory
    {
        private readonly GameState[] _buffer;
        private readonly int _capacity;

        public StateHistory(int capacity)
        {
            _capacity = capacity;
            _buffer = new GameState[capacity];
        }

        public void Record(GameState state)
        {
            int i = (int)(state.Tick % (uint)_capacity);
            // Snapshot a copy so later mutation of the live state doesn't corrupt history.
            if (_buffer[i] == null) _buffer[i] = state.Clone();
            else _buffer[i].CopyFrom(state);
        }

        /// <summary>Returns the snapshot for an exact tick, or null if it's no longer in the buffer.</summary>
        public GameState Get(uint tick)
        {
            int i = (int)(tick % (uint)_capacity);
            var s = _buffer[i];
            return (s != null && s.Tick == tick) ? s : null;
        }

        public void Clear() => Array.Clear(_buffer, 0, _capacity);
    }
}

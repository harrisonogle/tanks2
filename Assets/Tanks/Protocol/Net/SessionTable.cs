using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using System;

namespace Tanks.Net;

public sealed class SessionTable
{
    private struct Slot
    {
        public SessionProtocol? Session;
        public int Index;
    }
    private const int MaxCapacity = 1 << 18;
    private Slot[] _slots;
    private uint[] _gens;
    private int _capacity;
    private uint[] _free; // available slot indices
    private int _freeCount;
    private uint[] _index; // used slot indices
    private int _count;

    public SessionTable()
    {
        _capacity = 256; // initial capacity
        _slots = new Slot[_capacity];
        _gens = new uint[_capacity];
        _free = new uint[_capacity];
        _index = new uint[_capacity];
        for (uint i = 0; i < _capacity; i++)
            _free[i] = i;
        _freeCount = _capacity;
    }

    public int Count => _count;

    public bool TryPeek(out SessionId sid)
    {
        if (_freeCount <= 0)
        {
            if (_capacity >= MaxCapacity)
            {
                sid = default;
                return false;
            }

            var capacity = _capacity;
            var slots = _slots;
            var gens = _gens;
            var index = _index;

            _capacity = Math.Min(MaxCapacity, _capacity * 2);
            _slots = new Slot[_capacity];
            _gens = new uint[_capacity];
            _index = new uint[_capacity];

            Array.Copy(slots, _slots, capacity);
            Array.Copy(gens, _gens, capacity);
            Array.Copy(index, _index, capacity);

            _free = new uint[_capacity];
            for (uint slotId = (uint)capacity; slotId < _capacity; slotId++)
                _free[_freeCount++] = slotId;
        }

        sid = new SessionId(
            slotId: _free[_freeCount - 1],
            gen: NextGen(_gens[_free[_freeCount - 1]]));

        return true;
    }

    public bool TryAdd(SessionId sid, SessionProtocol session)
    {
        if (sid.SlotId >= _capacity)
        {
            return false;
        }
        if (_slots[sid.SlotId].Session is not null)
        {
            return false;
        }
        if (_freeCount <= 0)
        {
            return false;
        }
        if (sid.SlotId != _free[_freeCount - 1])
        {
            return false;
        }
        uint nextGen = NextGen(_gens[sid.SlotId]);
        if (sid.Gen != nextGen)
        {
            return false;
        }
        _freeCount--;
        _gens[sid.SlotId] = nextGen;
        _slots[sid.SlotId] = new Slot { Session = session, Index = _count };

        _index[_count] = sid.SlotId;
        _count++;

        return true;
    }

    // Invariants:
    // - return value true => slot is now empty
    // - `out session` not null => found existing session
    //
    // If returned false but session is not null, gen mismatch (stale SID).
    public bool TryRemove(SessionId sid, out SessionProtocol? session)
    {
        if (sid.SlotId >= _capacity)
        {
            session = null;
            return true;
        }

        ref Slot slot = ref _slots[sid.SlotId];
        session = slot.Session;

        if (session is null)
        {
            return true;
        }

        if (sid.Gen != _gens[sid.SlotId])
        {
            // Stale SID; don't remove
            return false;
        }

        Remove(ref slot, sid.SlotId);

        return true;
    }

    private void Remove(ref Slot slot, uint slotId)
    {
        slot.Session = null;

        Debug.Assert(_freeCount < _capacity);
        _free[_freeCount] = slotId;
        _freeCount++;

        int removedIndex = slot.Index;
        if (removedIndex != _count - 1)
        {
            uint lastSlotId = _index[_count - 1];
            ref Slot lastSlot = ref _slots[lastSlotId];
            Debug.Assert(lastSlot.Index == _count - 1);

            lastSlot.Index = removedIndex;
            _index[removedIndex] = lastSlotId;
        }
        _count--;
    }

    public SessionProtocol? Get(SessionId sid)
    {
        if (sid.SlotId >= _capacity) return null;
        if (_gens[sid.SlotId] != sid.Gen) return null;
        return _slots[sid.SlotId].Session;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool TryGetValue(SessionId sid, [NotNullWhen(true)] out SessionProtocol? session)
    {
        if (sid.SlotId >= _capacity)
        {
            session = default;
            return false;
        }
        session = _slots[sid.SlotId].Session;
        if (session is null)
        {
            return false;
        }
        if (sid.Gen != _gens[sid.SlotId])
        {
            return false;
        }
        return true;
    }

    public bool TryGetIterator(ref Iterator iterator)
    {
        if (_count < 1)
        {
            return false;
        }
        iterator = new Iterator(this);
        return true;
    }

    public struct Iterator
    {
        private readonly SessionTable _table;
        private readonly Slot[] _slots;
        private readonly uint[] _index;
        private int _i;

        internal Iterator(SessionTable table)
        {
            Debug.Assert(table._count > 0);
            _table = table;
            _slots = _table._slots;
            _index = _table._index;
            _i = 0;
        }

        public SessionProtocol Current =>
            _slots[_index[_i]].Session!;

        public bool Advance(bool remove)
        {
            if (!remove)
            {
                _i++;
                return _i < _table._count;
            }

            Debug.Assert(_i >= 0);
            uint slotId = _index[_i];
            ref Slot slot = ref _slots[slotId];
            _table.Remove(ref slot, slotId);
            return _i < _table._count;
        }
    }

    // Skip gen 0 on wrap to avoid colliding with the all-zero "unset DSID" sentinel that routes Pings to PassiveOpen
    private static uint NextGen(uint gen)
    {
        uint g = gen + 1;
        return g == 0 ? 1 : g;
    }
}
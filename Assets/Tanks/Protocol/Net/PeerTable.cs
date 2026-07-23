using System.Diagnostics;
using System.Runtime.InteropServices;
using System;

namespace Tanks.Net;

[StructLayout(LayoutKind.Explicit, Size = 32)]
public readonly struct PeerSessionEntry
{
    [FieldOffset(0)] public readonly PeerId PeerId;
    [FieldOffset(16)] public readonly SessionId SessionId;
}

public sealed class PeerTable
{
    [StructLayout(LayoutKind.Explicit, Size = 32)]
    private struct Slot
    {
        [FieldOffset(0)] public PeerId PeerId;
        [FieldOffset(16)] public SessionId SessionId;
        [FieldOffset(24)] public bool Occupied;
        [FieldOffset(28)] public int Index; // index in _index
    }

    private const int MaxCapacity = 1 << 17;

    private int _capacity; // invariant: must stay a power of 2 so that modulo via mask works
    private int _loadThreshold;
    private int _mask;
    private Slot[] _slots;
    private int _count;

    // used indices, packed to start of array, according to _count
    // invariant: _slots[_index[slot.Index]] == slot (if slot is occupied)
    private int[] _index;

    public PeerTable()
    {
        _capacity = 512;
        _loadThreshold = _capacity / 2;
        _mask = _capacity - 1;
        _slots = new Slot[_capacity];
        _index = new int[_loadThreshold];
    }

    public int Count => _count;

    public bool TryGetValue(in PeerId peerId, out SessionId sessionId)
    {
        int slotId = peerId.GetHashCode() & _mask;

        // worst case: check every slot if they all hash the same
        for (int i = 0; i < _capacity; i++, slotId = (slotId + 1) & _mask)
        {
            ref Slot slot = ref _slots[slotId];

            if (!slot.Occupied)
            {
                break;
            }

            if (PeerId.Equals(in peerId, in slot.PeerId))
            {
                sessionId = slot.SessionId;
                return true;
            }
        }

        sessionId = default;
        return false;
    }

    public bool TryRemove(in PeerId peerId, out SessionId sessionId)
    {
        int slotId = peerId.GetHashCode() & _mask;

        // worst case: check every slot if they all hash the same
        for (int i = 0; i < _capacity; i++, slotId = (slotId + 1) & _mask)
        {
            ref Slot slot = ref _slots[slotId];

            if (!slot.Occupied)
            {
                break;
            }

            if (PeerId.Equals(in peerId, in slot.PeerId))
            {
                sessionId = slot.SessionId;

                slot.Occupied = false;

                // Patch indices
                if (_count > 1 && slot.Index != _count - 1)
                {
                    int lastSlotId = _index[_count - 1];
                    _slots[lastSlotId].Index = slot.Index;
                    _index[slot.Index] = lastSlotId;
                }

                _count--;

                // Backshift
                int freed = slotId;
                int scan = (freed + 1) & _mask;
                while (_slots[scan].Occupied)
                {
                    int idealSlot = _slots[scan].PeerId.GetHashCode() & _mask;
                    // If scan's entry ideally goes at or before freed, moving it back is safe
                    // Distance from ideal to freed (forward) < distance from ideal to scan (forward)
                    bool shouldMove = ((freed - idealSlot) & _mask) < ((scan - idealSlot) & _mask);
                    if (shouldMove)
                    {
                        _slots[freed] = _slots[scan];
                        _index[_slots[freed].Index] = freed;
                        _slots[scan].Occupied = false;
                        freed = scan;
                    }
                    scan = (scan + 1) & _mask;
                }

                return true;
            }
        }

        sessionId = default;
        return false;
    }

    // Only returns false if max load has already been reached and we'd need to insert
    // Overwrites existing mapping, if present.
    public bool TrySet(in PeerId peerId, SessionId sessionId)
    {
        bool hasSpace = true;
        if (_count >= _loadThreshold)
        {
            if (!TryGrow())
            {
                hasSpace = false;
            }
        }

        int slotId = peerId.GetHashCode() & _mask;

        for (int i = 0; i < _capacity; i++, slotId = (slotId + 1) & _mask)
        {
            ref Slot slot = ref _slots[slotId];

            if (!slot.Occupied)
            {
                if (!hasSpace)
                {
                    return false;
                }
                slot.Occupied = true;
                slot.Index = _count;
                slot.PeerId = peerId;
                slot.SessionId = sessionId;
                _index[_count] = slotId;
                _count++;
                return true;
            }

            if (PeerId.Equals(in peerId, in slot.PeerId))
            {
                slot.SessionId = sessionId;
                return true;
            }
        }

        Debug.Assert(false, "unreachable");
        return false;
    }

    // Returns false if no space or if peer already exists and not equal
    // idempotent: successive calls with the same args return same result
    // Does not overwrite existing mapping, if present (and different).
    public bool TryAdd(in PeerId peerId, SessionId sessionId)
    {
        bool hasSpace = true;
        if (_count >= _loadThreshold)
        {
            if (!TryGrow())
            {
                hasSpace = false;
            }
        }

        int slotId = peerId.GetHashCode() & _mask;

        for (int i = 0; i < _capacity; i++, slotId = (slotId + 1) & _mask)
        {
            ref Slot slot = ref _slots[slotId];

            if (!slot.Occupied)
            {
                if (!hasSpace)
                {
                    return false;
                }
                slot.Occupied = true;
                slot.Index = _count;
                slot.PeerId = peerId;
                slot.SessionId = sessionId;
                _index[_count] = slotId;
                _count++;
                return true;
            }

            if (PeerId.Equals(in peerId, in slot.PeerId))
            {
                return sessionId.Equals(slot.SessionId);
            }
        }

        Debug.Assert(false, "unreachable");
        return false;
    }

    private bool TryGrow()
    {
        if (_capacity == MaxCapacity)
        {
            return false;
        }

        // Snapshot
        int capacity = _capacity;
        Slot[] slots = _slots;

        // Grow
        _capacity *= 2;
        _mask = _capacity - 1;
        _loadThreshold = _capacity / 2;
        _slots = new Slot[_capacity];
        _index = new int[_loadThreshold];

        // Assert invariants
        Debug.Assert(_capacity <= MaxCapacity);
        Debug.Assert((_capacity & _mask) == 0); // _capacity is a power of 2
        Debug.Assert(_count < _capacity);

        int count = 0;

        // Rehash
        for (int i = 0; i < capacity; i++)
        {
            ref var slot = ref slots[i];

            if (slot.Occupied)
            {
                // Move to new slot
                int dst = slot.PeerId.GetHashCode() & _mask;
                while (_slots[dst].Occupied)
                    dst = (dst + 1) & _mask;
                _slots[dst] = slot;

                // Rewire index
                _slots[dst].Index = count;
                _index[count++] = dst;
            }
        }

        Debug.Assert(count == _count, "total rehash");

        return true;
    }

    public Enumerator GetEntries() => new Enumerator(this);

    public readonly ref struct Enumerator
    {
        private readonly ReadOnlySpan<PeerSessionEntry> _entries;
        private readonly int[] _index;
        private readonly int _count;

        internal Enumerator(PeerTable table)
        {
            _entries = MemoryMarshal.Cast<Slot, PeerSessionEntry>(table._slots);
            _index = table._index;
            _count = table._count;
        }

        public ref readonly PeerSessionEntry this[int i] => ref _entries[_index[i]];
        public int Count => _count;
    }
}
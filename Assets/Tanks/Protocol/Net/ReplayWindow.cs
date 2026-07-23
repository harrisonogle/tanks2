namespace Tanks.Net;

// Sliding-window replay filter (the IPsec/DTLS algorithm). 17 bytes of state
// per receive direction; tracks seen-ness of the last 64 sequence numbers,
// never packets. O(1), allocation-free.
//
// Call ONLY with authenticated sequence numbers (after the auth tag has been
// verified) - forged seqs must never advance the window, or an attacker
// could blackhole legitimate traffic by racing the counter forward.
public struct ReplayWindow
{
    private const int WindowSize = 64;

    private ulong _max;    // highest authenticated seq accepted so far
    private ulong _seen;   // bit i => seq (_max - i) was accepted; bit 0 = _max itself
    private bool _primed;  // false until the first authenticated packet arrives

    // Returns true if `seq` is fresh (and records it); false for a replay or
    // a seq too old to track. Out-of-order delivery within the window is
    // accepted - at one packet per tick, 64 slots is ~1s of reordering.
    public bool TryAccept(ulong seq)
    {
        if (!_primed)
        {
            _primed = true;
            _max = seq;
            _seen = 1;
            return true;
        }

        if (seq > _max)
        {
            ulong advance = seq - _max;
            // C# shift counts mask to 63; branch the full-slide case explicitly.
            _seen = advance >= WindowSize ? 1UL : (_seen << (int)advance) | 1UL;
            _max = seq;
            return true;
        }

        ulong behind = _max - seq;
        if (behind >= WindowSize)
            return false; // too old to track - drop

        ulong bit = 1UL << (int)behind;
        if ((_seen & bit) != 0)
            return false; // replay

        _seen |= bit;
        return true;
    }
}

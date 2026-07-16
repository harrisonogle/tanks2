using System;

namespace Tanks.Net;

// Per-session packet authenticator. Holds BOTH directional keys: Seal uses
// this side's send key, Verify uses the peer's send key. Directional keys
// exist because Poly1305 one-time keys are derived from (key, seq), and both
// sides count sequences from zero - a shared key would collide the nonce
// space (and permit reflection).
//
// `seq` is the per-direction monotonic sequence number from the packet
// header. The caller must never seal two DIFFERENT byte sequences under the
// same seq; sealing an identical retransmit is safe, but the send paths
// allocate a fresh seq per send attempt so the question never arises.
public interface IPacketAuthenticator : IDisposable
{
    int TagSize { get; }
    void Seal(ulong seq, ReadOnlySpan<byte> content, Span<byte> tag);
    bool Verify(ulong seq, ReadOnlySpan<byte> content, ReadOnlySpan<byte> tag);
}

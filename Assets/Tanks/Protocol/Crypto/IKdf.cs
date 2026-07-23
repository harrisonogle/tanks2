using System;

namespace Tanks.Net;

// Key derivation function
// Used to generate arbitrary-length key material from a small shared "master" key
public interface IKdf
{
    void Derive(
        ReadOnlySpan<byte> ikm,
        ReadOnlySpan<byte> salt,
        ReadOnlySpan<byte> info,
        Span<byte> okm);
}
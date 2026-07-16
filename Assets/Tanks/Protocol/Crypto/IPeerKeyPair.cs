using System;

namespace Tanks.Net;

// Wraps a private key
public interface IPeerKeyPair : IDisposable
{
    ReadOnlySpan<byte> PublicKey { get; }
    void Sign(ReadOnlySpan<byte> message, Span<byte> signature);
    bool TryDecrypt(ReadOnlySpan<byte> message, Span<byte> destination, out int bytesWritten);
}
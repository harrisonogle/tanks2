using System;

namespace Tanks.Net;

// Wraps a public key
public interface IPeerVerifier : IDisposable
{
    bool Verify(ReadOnlySpan<byte> message, ReadOnlySpan<byte> signature);
    bool TryEncrypt(ReadOnlySpan<byte> message, Span<byte> destination, out int bytesWritten);
}
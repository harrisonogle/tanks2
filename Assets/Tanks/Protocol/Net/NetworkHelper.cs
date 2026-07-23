using System.Security.Cryptography;
using System;

namespace Tanks.Net;

public static class NetworkHelper
{
    public static unsafe void GetPeerId(ReadOnlySpan<byte> publicKey, out PeerId peerId)
    {
        // Hash the public key and truncate to get peer ID.
        using (var sha256 = SHA256.Create())
        {
            byte* pubKeyHash = stackalloc byte[32];
            int pubKeyHashLen;
            if (!sha256.TryComputeHash(
                    source: publicKey,
                    destination: new Span<byte>(pubKeyHash, 32),
                    out pubKeyHashLen) ||
                pubKeyHashLen != 32)
            {
                throw new InvalidOperationException("Failed to hash local peer public key.");
            }
            peerId = new PeerId(pubKeyHash); // truncation is free
        }
    }
}
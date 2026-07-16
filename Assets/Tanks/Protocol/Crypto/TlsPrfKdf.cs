using System.Diagnostics;
using System.Security.Cryptography;
using System;

namespace Tanks.Net;

// NOTE: In .NET 5+, use HKDF class instead.
// Below is the (SHA-256) key derivation function (KDF) used in TLS up to TLS 1.2 (called "PRF", for pseudorandom function)
// It's still secure, but the technique was standardized into HKDF, so TLS 1.3 uses that
// The point is still to start with a small key and use HMAC to expand it into a bigger one
public sealed class TlsPrfKdf : IKdf
{
    public static readonly TlsPrfKdf Instance = new();
    private TlsPrfKdf() { }

    public void Derive(
        ReadOnlySpan<byte> ikm, // secret
        ReadOnlySpan<byte> salt, // seed
        ReadOnlySpan<byte> info, // label
        Span<byte> okm) // output
    {
        const int HashOutputSize = 32; // SHA-256

        // https://tools.ietf.org/html/rfc4346#section-5
        //
        // P_hash(secret, seed) = HMAC_hash(secret, A(1) + seed) +
        //                        HMAC_hash(secret, A(2) + seed) +
        //                        HMAC_hash(secret, A(3) + seed) + ...
        //
        // A(0) = seed
        // A(i) = HMAC_hash(secret, A(i-1))
        //
        // This is called via PRF, which turns (label || seed) into seed.

        using (IncrementalHash hasher = IncrementalHash.CreateHMAC(HashAlgorithmName.SHA256, ikm.ToArray()))
        {
            Span<byte> a = stackalloc byte[HashOutputSize];
            Span<byte> p = stackalloc byte[HashOutputSize];

            // A(1)
            hasher.AppendData(info);
            hasher.AppendData(salt);

            if (!hasher.TryGetHashAndReset(a, out int bytesWritten) || bytesWritten != HashOutputSize)
            {
                throw new CryptographicException();
            }

            while (true)
            {
                // HMAC_hash(secret, A(i) || seed) => p
                hasher.AppendData(a);
                hasher.AppendData(info);
                hasher.AppendData(salt);

                if (!hasher.TryGetHashAndReset(p, out bytesWritten) || bytesWritten != HashOutputSize)
                {
                    throw new CryptographicException();
                }

                Debug.Assert(p.Length == HashOutputSize);

                if (okm.Length > HashOutputSize)
                {
                    p.Slice(0, HashOutputSize).CopyTo(okm);
                    okm = okm.Slice(HashOutputSize);
                }
                else
                {
                    p.Slice(0, okm.Length).CopyTo(okm);
                    return;
                }

                // Build the next A(i)
                hasher.AppendData(a);

                if (!hasher.TryGetHashAndReset(a, out bytesWritten) || bytesWritten != HashOutputSize)
                {
                    throw new CryptographicException();
                }
            }
        }
    }
}
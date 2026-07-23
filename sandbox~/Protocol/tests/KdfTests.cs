using System.Security.Cryptography;
using System.Text;
using Tanks.Net;
using Xunit;

namespace Tests;

public sealed class KdfTests
{
    // Widely-circulated TLS 1.2 PRF (SHA-256) test vector.
    private static readonly byte[] Secret = Convert.FromHexString("9bbe436ba940f017b17652849a71db35");
    private static readonly byte[] Seed = Convert.FromHexString("a0ba9f936cda311827a6f796ffd5198c");
    private static readonly byte[] Label = Encoding.ASCII.GetBytes("test label");

    [Fact]
    public void Derive_MatchesIndependentPrfImplementation()
    {
        // Reference P_hash implemented separately (plain HMACSHA256 loop).
        // Guards both correctness and cross-architecture determinism.
        byte[] labelSeed = Label.Concat(Seed).ToArray();
        byte[] expected = PHashReference(Secret, labelSeed, 100);

        var actual = new byte[100];
        TlsPrfKdf.Instance.Derive(Secret, Seed, Label, actual);

        Assert.Equal(expected, actual);
    }

    [Fact]
    public void Derive_MatchesKnownVectorPrefix()
    {
        var okm = new byte[32];
        TlsPrfKdf.Instance.Derive(Secret, Seed, Label, okm);

        // First 16 bytes of the published 100-byte vector output.
        Assert.Equal("e3f229ba727be17b8d122620557cd453", Convert.ToHexString(okm.AsSpan(0, 16).ToArray()).ToLowerInvariant());
    }

    [Fact]
    public void Derive_IsDeterministic_AndSaltSensitive()
    {
        var a = new byte[32];
        var b = new byte[32];
        TlsPrfKdf.Instance.Derive(Secret, Seed, Label, a);
        TlsPrfKdf.Instance.Derive(Secret, Seed, Label, b);
        Assert.Equal(a, b);

        var differentSalt = (byte[])Seed.Clone();
        differentSalt[0] ^= 1;
        TlsPrfKdf.Instance.Derive(Secret, differentSalt, Label, b);
        Assert.NotEqual(a, b);
    }

    private static byte[] PHashReference(byte[] secret, byte[] labelSeed, int outputLength)
    {
        using var hmac = new HMACSHA256(secret);
        var result = new byte[outputLength];
        int filled = 0;

        byte[] a = hmac.ComputeHash(labelSeed); // A(1)
        while (filled < outputLength)
        {
            byte[] input = new byte[a.Length + labelSeed.Length];
            a.CopyTo(input, 0);
            labelSeed.CopyTo(input, a.Length);

            byte[] p = hmac.ComputeHash(input); // HMAC(secret, A(i) || labelSeed)
            int n = Math.Min(p.Length, outputLength - filled);
            Array.Copy(p, 0, result, filled, n);
            filled += n;

            a = hmac.ComputeHash(a); // A(i+1)
        }

        return result;
    }
}

using System.Security.Cryptography;
using Tanks.Net;
using Xunit;

namespace Tests;

// The hand-rolled DER codec exists because Unity's Mono throws PNSE on
// Export/ImportRSAPublicKey. On .NET those APIs work, so assert byte-for-byte
// equivalence against them — that's the cross-runtime wire-compat guarantee.
public sealed class RsaPkcs1PublicKeyTests
{
    [Fact]
    public void Export_MatchesExportRSAPublicKey_ByteForByte()
    {
        using var rsa = RSA.Create(2048);
        Assert.Equal(rsa.ExportRSAPublicKey(), RsaPkcs1PublicKey.Export(rsa));
    }

    [Fact]
    public void Import_ReadsExportRSAPublicKeyOutput()
    {
        using var source = RSA.Create(2048);
        byte[] der = source.ExportRSAPublicKey();

        using var imported = RSA.Create();
        RsaPkcs1PublicKey.Import(imported, der, out int bytesRead);
        Assert.Equal(der.Length, bytesRead);
        Assert.Equal(der, imported.ExportRSAPublicKey());
    }

    [Fact]
    public void ImportRSAPublicKey_ReadsExportOutput()
    {
        using var source = RSA.Create(2048);
        byte[] der = RsaPkcs1PublicKey.Export(source);

        using var imported = RSA.Create();
        imported.ImportRSAPublicKey(der, out int bytesRead);
        Assert.Equal(der.Length, bytesRead);
        Assert.Equal(der, RsaPkcs1PublicKey.Export(imported));
    }

    [Fact]
    public void Import_IgnoresTrailingBytes_AndReportsConsumedLength()
    {
        using var source = RSA.Create(2048);
        byte[] der = RsaPkcs1PublicKey.Export(source);
        byte[] padded = new byte[der.Length + 16];
        der.CopyTo(padded, 0);

        using var imported = RSA.Create();
        RsaPkcs1PublicKey.Import(imported, padded, out int bytesRead);
        Assert.Equal(der.Length, bytesRead);
    }

    [Theory]
    [InlineData(new byte[0])]
    [InlineData(new byte[] { 0x30 })] // truncated header
    [InlineData(new byte[] { 0x31, 0x03, 0x02, 0x01, 0x01 })] // wrong outer tag
    [InlineData(new byte[] { 0x30, 0x03, 0x04, 0x01, 0x01 })] // wrong inner tag
    [InlineData(new byte[] { 0x30, 0x06, 0x02, 0x01, 0x81, 0x02, 0x01, 0x01 })] // negative modulus
    [InlineData(new byte[] { 0x30, 0x84, 0x01, 0x01, 0x01, 0x01 })] // oversized length-of-length
    public void Import_ThrowsOnMalformedInput(byte[] der)
    {
        using var rsa = RSA.Create();
        Assert.Throws<CryptographicException>(() => RsaPkcs1PublicKey.Import(rsa, der, out _));
    }

    [Fact]
    public void PeerKeyPairAndVerifier_SignVerify_RoundTrip()
    {
        // The actual peer classes end to end: key pair exports, verifier imports,
        // signature verifies, and a tampered message doesn't.
        using var keyPair = new RsaSha256PeerKeyPair();
        using var verifier = new RsaSha256PeerVerifier(keyPair.PublicKey);

        byte[] message = new byte[64];
        for (int i = 0; i < message.Length; i++) message[i] = (byte)(i * 11);
        byte[] signature = new byte[256];
        keyPair.Sign(message, signature);

        Assert.True(verifier.Verify(message, signature));
        message[3] ^= 0x01;
        Assert.False(verifier.Verify(message, signature));
    }
}

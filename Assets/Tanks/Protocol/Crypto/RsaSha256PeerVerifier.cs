using System.Security.Cryptography;
using System;

namespace Tanks.Net;

public sealed class RsaSha256PeerVerifier : IPeerVerifier
{
    private readonly RSA _rsa;

    public RsaSha256PeerVerifier(ReadOnlySpan<byte> publicKey)
    {
        _rsa = RSA.Create();
        // ImportRSAPublicKey throws PlatformNotSupportedException on Unity's Mono BCL.
        RsaPkcs1PublicKey.Import(_rsa, publicKey, out int bytesRead);
        if (bytesRead < 256)
        {
            throw new InvalidOperationException($"Failed to import RSA public key; expected (at least) 256 bytes, read {bytesRead}.");
        }
    }

    public bool Verify(ReadOnlySpan<byte> message, ReadOnlySpan<byte> signature)
    {
        return _rsa.VerifyData(message, signature, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
    }

    public bool TryEncrypt(ReadOnlySpan<byte> message, Span<byte> destination, out int bytesWritten)
    {
        // OAEP-SHA1 to match RsaSha256PeerKeyPair.TryDecrypt — see the comment there
        // (Unity's Mono RSA rejects OaepSHA256).
        return _rsa.TryEncrypt(message, destination, RSAEncryptionPadding.OaepSHA1, out bytesWritten);
    }

    public void Dispose()
    {
        _rsa.Dispose();
    }
}
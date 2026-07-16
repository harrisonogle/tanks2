using System.Security.Cryptography;
using System;

namespace Tanks.Net;

public sealed class RsaSha256PeerKeyPair : IPeerKeyPair
{
    private readonly RSA _rsa;
    private readonly byte[] _publicKey;

    public RsaSha256PeerKeyPair()
    {
        _rsa = CreateRsa2048();
        // PKCS#1, but via our own DER framing: ExportRSAPublicKey throws
        // PlatformNotSupportedException on Unity's Mono BCL.
        _publicKey = RsaPkcs1PublicKey.Export(_rsa);
    }

    private static RSA CreateRsa2048()
    {
        // On Unity's Mono, RSA.Create(keySizeInBits) SILENTLY ignores the requested
        // size: it returns an RSACryptoServiceProvider whose size is pinned at
        // construction (1024). Verify the actual modulus and fall back to the
        // explicit ctor. (That ctor is Windows-only on modern .NET, but there
        // RSA.Create honors the size and the fallback never runs.)
        RSA rsa = RSA.Create(keySizeInBits: 2048);
        if (ModulusSize(rsa) == 256)
            return rsa;

        rsa.Dispose();
        rsa = new RSACryptoServiceProvider(dwKeySize: 2048);
        if (ModulusSize(rsa) != 256)
            throw new CryptographicException($"Runtime refuses to generate a 2048-bit RSA key (got {rsa.KeySize} bits).");
        return rsa;
    }

    private static int ModulusSize(RSA rsa)
    {
        // Forces lazy key generation; KeySize alone can't be trusted on Mono.
        byte[]? modulus = rsa.ExportParameters(includePrivateParameters: false).Modulus;
        return modulus is null ? 0 : modulus.Length;
    }

    public ReadOnlySpan<byte> PublicKey => _publicKey;

    public void Sign(ReadOnlySpan<byte> message, Span<byte> signature)
    {
        if (!_rsa.TrySignData(message, signature, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1, out _))
        {
            throw new InvalidOperationException("Failed to sign data.");
        }
    }

    /*
    TryDecrypt on the receive path can be a DoS vector. Each call does an RSA private-key operation (~1ms).
    If an attacker spams Pongs to your responder, you do RSA decryption on each one before realizing the
    signature is bogus. Mitigation: verify the signature first, decrypt second. The signature on Pong covers
    the ciphertext, so a forged Pong fails the verify step before you spend the decrypt cost. Cheap to enforce:
    just structure the receive handler as verify-then-decrypt. Worth pinning in the protocol order, not the
    crypto contract.
    */
    public bool TryDecrypt(ReadOnlySpan<byte> message, Span<byte> destination, out int bytesWritten)
    {
        // OAEP-SHA1, not -SHA256: Unity's Mono RSA only implements Pkcs1 and OaepSHA1
        // padding ("Specified padding mode is not valid for this algorithm" otherwise).
        // OAEP's security doesn't inherit SHA-1's collision weakness (it's an MGF here),
        // and the nonce this protects is also signature-covered. Revisit on CoreCLR.
        return _rsa.TryDecrypt(message, destination, RSAEncryptionPadding.OaepSHA1, out bytesWritten);
    }

    public void Dispose()
    {
        _rsa.Dispose();
    }
}
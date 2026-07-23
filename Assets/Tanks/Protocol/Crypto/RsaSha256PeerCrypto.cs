using System;

namespace Tanks.Net;

// Tanks crypto scheme:
// - Encryption + signing: RSA 2048-bit keys with SHA-256 signatures
// - Packet auth: ChaCha20+Poly1305 (one-time key per packet, RFC 8439 style)
// - KDF: TLS PRF
public sealed class TanksPeerCrypto : IPeerCrypto
{
    public static readonly TanksPeerCrypto Instance = new();
    private TanksPeerCrypto() { }

    public int SignatureSize => 256;
    public IPeerKeyPair GenerateKeyPair() => new RsaSha256PeerKeyPair();
    public IPeerVerifier CreateVerifier(ReadOnlySpan<byte> publicKey) => new RsaSha256PeerVerifier(publicKey);

    public int SessionKeySize => ChaCha20Poly1305Authenticator.KeyMaterialSize; // two directional keys
    public IPacketAuthenticator CreateAuthenticator(ReadOnlySpan<byte> sessionKeyMaterial, bool initiator) =>
        new ChaCha20Poly1305Authenticator(sessionKeyMaterial, initiator);

    public IKdf Kdf => TlsPrfKdf.Instance;
}
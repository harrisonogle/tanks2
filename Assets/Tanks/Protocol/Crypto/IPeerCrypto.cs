using System;

namespace Tanks.Net;

// Defines the elements of a crypto scheme needed by Tanks netcode protocol
// - Encryption + signature (handshake identity)
// - Packet authentication (per-packet auth tags under directional keys)
// - KDF (key derivation function, to expand the handshake nonces into
//   the session's directional key material)
public interface IPeerCrypto
{
    // Encryption + signature
    int SignatureSize { get; }
    IPeerKeyPair GenerateKeyPair();
    IPeerVerifier CreateVerifier(ReadOnlySpan<byte> publicKey);

    // Packet authentication
    int SessionKeySize { get; }
    IPacketAuthenticator CreateAuthenticator(ReadOnlySpan<byte> sessionKeyMaterial, bool initiator);

    // KDF
    IKdf Kdf { get; }
}
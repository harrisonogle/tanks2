using System.Security.Cryptography;
using Tanks.Net;
using Xunit;

namespace Tests;

public sealed unsafe class WireTests
{
    private static ChaCha20Poly1305Authenticator CreateAuth(bool initiator, byte seed = 0x5c)
    {
        Span<byte> keyMaterial = stackalloc byte[ChaCha20Poly1305Authenticator.KeyMaterialSize];
        for (int i = 0; i < keyMaterial.Length; i++)
            keyMaterial[i] = (byte)(seed + i * 3);
        return new ChaCha20Poly1305Authenticator(keyMaterial, initiator);
    }

    [Fact]
    public void Auth_WriteVerify_Roundtrip()
    {
        using var initiator = CreateAuth(initiator: true);
        using var responder = CreateAuth(initiator: false);

        const int contentLength = 100;
        const ulong seq = 7;
        byte* buffer = stackalloc byte[contentLength + NetworkConstants.AuthTagLength];
        for (int i = 0; i < contentLength; i++) buffer[i] = (byte)i;

        Assert.True(Wire.TryWriteAuth(buffer, contentLength, contentLength + NetworkConstants.AuthTagLength, initiator, seq, out int written));
        Assert.Equal(NetworkConstants.AuthTagLength, written);

        // The responder verifies what the initiator sealed.
        Assert.True(Wire.VerifyAuth(buffer, contentLength + written, responder, seq, out int verified));
        Assert.Equal(written, verified);

        // Wrong seq derives the wrong one-time key.
        Assert.False(Wire.VerifyAuth(buffer, contentLength + written, responder, seq + 1, out _));

        // Corrupt content fails.
        buffer[10] ^= 0xFF;
        Assert.False(Wire.VerifyAuth(buffer, contentLength + written, responder, seq, out _));
    }

    [Fact]
    public void Auth_IsDirectional_ReflectionFails()
    {
        // A packet sealed by the initiator must NOT verify against the
        // initiator's own receive key (i.e., reflecting a peer's packet back
        // at them fails at key level).
        using var initiator = CreateAuth(initiator: true);

        const int contentLength = 64;
        byte* buffer = stackalloc byte[contentLength + NetworkConstants.AuthTagLength];
        for (int i = 0; i < contentLength; i++) buffer[i] = (byte)(i * 5);

        Assert.True(Wire.TryWriteAuth(buffer, contentLength, contentLength + NetworkConstants.AuthTagLength, initiator, seq: 3, out int written));
        Assert.False(Wire.VerifyAuth(buffer, contentLength + written, initiator, seq: 3, out _));
    }

    [Fact]
    public void Auth_InsufficientCapacity_Fails()
    {
        using var auth = CreateAuth(initiator: true);

        byte* buffer = stackalloc byte[100];
        Assert.False(Wire.TryWriteAuth(buffer, 90, 100, auth, seq: 0, out int written)); // only 10 bytes of tailroom
        Assert.Equal(0, written);
    }

    [Fact]
    public void Ping_WriteParse_Roundtrip_WithValidSignature()
    {
        SessionContext ctx = TestHelpers.CreateSessionContext();
        RandomNumberGenerator.Fill(ctx.LocalNonce);

        byte* buffer = stackalloc byte[NetworkConstants.MaxDatagramSize];
        int packetLength = Wire.WritePing(buffer, NetworkConstants.MaxDatagramSize, ctx);
        Assert.InRange(packetLength, PacketHeader.Size + Ping.Size, NetworkConstants.MaxDatagramSize);

        var header = (PacketHeader*)buffer;
        Assert.Equal(PacketType.Ping, header->Type);
        Assert.Equal(NetworkConstants.MajorVersion, header->MajorVersion);

        byte* body = buffer + PacketHeader.Size;
        int bodyLength = packetLength - PacketHeader.Size;

        Assert.True(Wire.TryParsePing(
            body, bodyLength,
            out Ping* ping,
            out byte* pubKey, out int pubKeyLen,
            out byte* signature, out int signatureLen));

        // ALPN fields survive the roundtrip and sit under the signature.
        Assert.Equal(ctx.AppProtocolId, ping->AppProtocolId);
        Assert.Equal(ctx.AppProtocolVersion, ping->AppProtocolVersion);

        // The nonce is echoed verbatim.
        Assert.True(ctx.LocalNonce.SequenceEqual(
            new ReadOnlySpan<byte>((byte*)&ping->InitiatorNonce, 16)));

        // The advertised pubkey verifies the signature over [ping .. sig).
        Assert.True(ctx.LocalKeyPair.PublicKey.SequenceEqual(new ReadOnlySpan<byte>(pubKey, pubKeyLen)));
        using IPeerVerifier verifier = TanksPeerCrypto.Instance.CreateVerifier(new ReadOnlySpan<byte>(pubKey, pubKeyLen));
        Assert.True(verifier.Verify(
            new ReadOnlySpan<byte>((byte*)ping, (int)(signature - (byte*)ping)),
            new ReadOnlySpan<byte>(signature, signatureLen)));

        // And the peer id binds to the pubkey.
        NetworkHelper.GetPeerId(new ReadOnlySpan<byte>(pubKey, pubKeyLen), out PeerId computed);
        Assert.True(computed.Equals(ctx.LocalPeerId));
    }

    [Fact]
    public void ParsePing_Fuzz_NeverThrows()
    {
        SessionContext ctx = TestHelpers.CreateSessionContext();
        RandomNumberGenerator.Fill(ctx.LocalNonce);

        byte* buffer = stackalloc byte[NetworkConstants.MaxDatagramSize];
        int packetLength = Wire.WritePing(buffer, NetworkConstants.MaxDatagramSize, ctx);
        byte* body = buffer + PacketHeader.Size;
        int bodyLength = packetLength - PacketHeader.Size;

        byte* mutated = stackalloc byte[bodyLength];
        var rng = new Random(1234);

        for (int iteration = 0; iteration < 20_000; iteration++)
        {
            Buffer.MemoryCopy(body, mutated, bodyLength, bodyLength);

            // Flip 1-4 random bytes (length/offset fields included).
            int flips = 1 + rng.Next(4);
            for (int i = 0; i < flips; i++)
                mutated[rng.Next(bodyLength)] ^= (byte)(1 + rng.Next(255));

            // Sometimes also lie about the available length (truncation).
            int available = rng.Next(3) == 0 ? rng.Next(bodyLength + 1) : bodyLength;

            // Must never throw or read out of bounds; rejection is fine.
            Wire.TryParsePing(mutated, available, out _, out _, out _, out _, out _);
        }
    }

    [Fact]
    public void ParsePong_Fuzz_NeverThrows()
    {
        // No convenient writer without a full handshake; fuzz from whole cloth.
        byte* body = stackalloc byte[256];
        var rng = new Random(5678);

        for (int iteration = 0; iteration < 20_000; iteration++)
        {
            for (int i = 0; i < 256; i++)
                body[i] = (byte)rng.Next(256);

            Wire.TryParsePong(body, rng.Next(257), out _, out _, out _, out _, out _, out _, out _);
        }
    }
}

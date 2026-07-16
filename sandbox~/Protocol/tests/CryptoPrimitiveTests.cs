using System.Text;
using Tanks.Net;
using Xunit;

namespace Tests;

// Known-answer tests against the RFC 8439 test vectors (fetched verbatim from
// rfc-editor.org, not transcribed from memory).
public sealed class CryptoPrimitiveTests
{
    private static byte[] Hex(string s) => Convert.FromHexString(s.Replace(" ", "").Replace("\n", ""));

    [Fact]
    public void ChaCha20_Block_Rfc8439_Section_2_3_2()
    {
        byte[] key = Hex("000102030405060708090a0b0c0d0e0f101112131415161718191a1b1c1d1e1f");
        byte[] nonce = Hex("000000090000004a00000000");
        const uint counter = 1;

        byte[] expected = Hex(
            "10f1e7e4d13b5915500fdd1fa32071c4" +
            "c7d1f4c733c068030422aa9ac3d46c4e" +
            "d2826446079faa0914c2d705d98b02a2" +
            "b5129cd1de164eb9cbd083e8a2503c4e");

        var block = new byte[ChaCha20.BlockSize];
        ChaCha20.Block(key, counter, nonce, block);

        Assert.Equal(expected, block);
    }

    [Fact]
    public void Poly1305_Tag_Rfc8439_Section_2_5_2()
    {
        byte[] key = Hex("85d6be7857556d337f4452fe42d506a80103808afb0db2fd4abff6af4149f51b");
        byte[] message = Encoding.ASCII.GetBytes("Cryptographic Forum Research Group");
        byte[] expected = Hex("a8061dc1305136c6c22b8baf0c0127a9");

        var tag = new byte[Poly1305.TagSize];
        Poly1305.ComputeTag(key, message, tag);

        Assert.Equal(expected, tag);
        Assert.True(Poly1305.VerifyTag(key, message, expected));
    }

    // The exact operation the integration performs per packet: derive a
    // one-time Poly1305 key from ChaCha20.Block(sessionKey, nonce, counter=0).
    [Fact]
    public void Poly1305KeyGen_Rfc8439_Section_2_6_2()
    {
        byte[] key = Hex("808182838485868788898a8b8c8d8e8f909192939495969798999a9b9c9d9e9f");
        byte[] nonce = Hex("000000000001020304050607");
        byte[] expected = Hex("8ad5a08b905f81cc815040274ab29471a833b637e3fd0da508dbb8e2fdd1a646");

        var block = new byte[ChaCha20.BlockSize];
        ChaCha20.Block(key, counter: 0, nonce, block);

        // The one-time key is the first 32 bytes of the block.
        Assert.Equal(expected, block.AsSpan(0, Poly1305.KeySize).ToArray());
    }

    [Fact]
    public void Poly1305_VerifyRejects_TamperedMessageAndTag()
    {
        byte[] key = Hex("85d6be7857556d337f4452fe42d506a80103808afb0db2fd4abff6af4149f51b");
        byte[] message = Encoding.ASCII.GetBytes("Cryptographic Forum Research Group");
        var tag = new byte[Poly1305.TagSize];
        Poly1305.ComputeTag(key, message, tag);

        byte[] tamperedMsg = (byte[])message.Clone();
        tamperedMsg[0] ^= 0x01;
        Assert.False(Poly1305.VerifyTag(key, tamperedMsg, tag));

        byte[] tamperedTag = (byte[])tag.Clone();
        tamperedTag[15] ^= 0x01;
        Assert.False(Poly1305.VerifyTag(key, message, tamperedTag));

        Assert.False(Poly1305.VerifyTag(key, message, tag.AsSpan(0, 15).ToArray())); // wrong length
    }

    // Independent of any vector: with an empty message no blocks are absorbed,
    // so the tag is just the pad, key[16..32].
    [Fact]
    public void Poly1305_EmptyMessage_TagIsPad()
    {
        byte[] key = Hex("85d6be7857556d337f4452fe42d506a80103808afb0db2fd4abff6af4149f51b");
        var tag = new byte[Poly1305.TagSize];
        Poly1305.ComputeTag(key, ReadOnlySpan<byte>.Empty, tag);

        Assert.Equal(key.AsSpan(16, 16).ToArray(), tag);
    }

    // Block-boundary coverage: exact-multiple and partial-final paths must both
    // authenticate (regression guard on the fullLength / remaining split).
    [Theory]
    [InlineData(1)]
    [InlineData(15)]
    [InlineData(16)]
    [InlineData(17)]
    [InlineData(32)]
    [InlineData(63)]
    [InlineData(64)]
    public void Poly1305_LengthBoundaries_VerifyRoundtrips(int length)
    {
        byte[] key = Hex("808182838485868788898a8b8c8d8e8f909192939495969798999a9b9c9d9e9f");
        var message = new byte[length];
        for (int i = 0; i < length; i++) message[i] = (byte)(i * 7 + 1);

        var tag = new byte[Poly1305.TagSize];
        Poly1305.ComputeTag(key, message, tag);
        Assert.True(Poly1305.VerifyTag(key, message, tag));
    }
}

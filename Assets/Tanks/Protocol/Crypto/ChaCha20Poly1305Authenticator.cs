using System.Buffers.Binary;
using System.Security.Cryptography;
using System;

namespace Tanks.Net;

// RFC 8439-style packet authentication: each packet's Poly1305 key is
// derived fresh from ChaCha20.Block(directionKey, nonce(seq), counter: 0),
// so the one-time-key rule holds by construction as long as seqs are unique
// per direction.
//
// Key material layout (both peers derive the same 64 bytes from the
// handshake KDF): [0..32) authenticates initiator->responder traffic,
// [32..64) authenticates responder->initiator traffic. Each side's role
// decides which half seals and which half verifies.
public sealed class ChaCha20Poly1305Authenticator : IPacketAuthenticator
{
    public const int KeyMaterialSize = 64;

    private readonly byte[] _keys; // [0..32) send key, [32..64) receive key
    private bool _disposed;

    public ChaCha20Poly1305Authenticator(ReadOnlySpan<byte> keyMaterial, bool initiator)
    {
        ThrowHelper.ThrowIfNotEqual(keyMaterial.Length, KeyMaterialSize);

        _keys = new byte[KeyMaterialSize];
        if (initiator)
        {
            keyMaterial.CopyTo(_keys); // send = i2r half, recv = r2i half
        }
        else
        {
            keyMaterial.Slice(32).CopyTo(_keys);         // send = r2i half
            keyMaterial.Slice(0, 32).CopyTo(_keys.AsSpan(32)); // recv = i2r half
        }
    }

    public int TagSize => Poly1305.TagSize;

    public void Seal(ulong seq, ReadOnlySpan<byte> content, Span<byte> tag)
    {
        ComputeTag(_keys.AsSpan(0, 32), seq, content, tag);
    }

    public bool Verify(ulong seq, ReadOnlySpan<byte> content, ReadOnlySpan<byte> tag)
    {
        if (_disposed || tag.Length != Poly1305.TagSize)
            return false;

        Span<byte> computed = stackalloc byte[Poly1305.TagSize];
        ComputeTag(_keys.AsSpan(32, 32), seq, content, computed);
        return CryptographicOperations.FixedTimeEquals(computed, tag);
    }

    private void ComputeTag(ReadOnlySpan<byte> directionKey, ulong seq, ReadOnlySpan<byte> content, Span<byte> tag)
    {
        ThrowHelper.ThrowIfDisposed(_disposed, GetType());

        // 96-bit nonce: 4 zero bytes + the sequence number, little-endian.
        // Uniqueness comes from the seq; direction separation comes from the key.
        Span<byte> nonce = stackalloc byte[ChaCha20.NonceSize];
        nonce.Slice(0, 4).Clear();
        BinaryPrimitives.WriteUInt64LittleEndian(nonce.Slice(4), seq);

        // One-time Poly1305 key = first half of the ChaCha20 block at counter 0.
        Span<byte> block = stackalloc byte[ChaCha20.BlockSize];
        ChaCha20.Block(directionKey, counter: 0, nonce, block);
        Poly1305.ComputeTag(block.Slice(0, Poly1305.KeySize), content, tag);
        CryptographicOperations.ZeroMemory(block);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        CryptographicOperations.ZeroMemory(_keys);
    }
}

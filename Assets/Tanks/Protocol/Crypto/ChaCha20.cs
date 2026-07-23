using System.Buffers.Binary;
using System;

namespace Tanks.Net;

// ChaCha20 block function (RFC 8439 section 2.3).
//
// This is the primitive the Poly1305 integration uses to derive a fresh
// one-time authentication key per packet (RFC 8439 section 2.6):
//   poly_key = ChaCha20-Block(session_key, nonce, counter=0)[0..32]
//
// The full stream cipher (XOR the keystream into the plaintext, counter
// starting at 1) is a short addition on top of Block, if payload encryption
// is wanted later. Pure software, no allocation, netstandard2.1-only.
public static class ChaCha20
{
    public const int KeySize = 32;
    public const int NonceSize = 12;
    public const int BlockSize = 64;

    // The four constant words: ASCII "expand 32-byte k", little-endian.
    private const uint C0 = 0x61707865;
    private const uint C1 = 0x3320646e;
    private const uint C2 = 0x79622d32;
    private const uint C3 = 0x6b206574;

    // Produces one 64-byte keystream block for (key, counter, nonce) into `block`.
    public static void Block(
        ReadOnlySpan<byte> key,
        uint counter,
        ReadOnlySpan<byte> nonce,
        Span<byte> block)
    {
        ThrowHelper.ThrowIfNotEqual(key.Length, KeySize);
        ThrowHelper.ThrowIfNotEqual(nonce.Length, NonceSize);
        ThrowHelper.ThrowIfLessThan(block.Length, BlockSize);

        // Initial state (RFC 8439 section 2.3): constants | key | counter | nonce.
        uint s0 = C0, s1 = C1, s2 = C2, s3 = C3;
        uint s4 = BinaryPrimitives.ReadUInt32LittleEndian(key);
        uint s5 = BinaryPrimitives.ReadUInt32LittleEndian(key.Slice(4));
        uint s6 = BinaryPrimitives.ReadUInt32LittleEndian(key.Slice(8));
        uint s7 = BinaryPrimitives.ReadUInt32LittleEndian(key.Slice(12));
        uint s8 = BinaryPrimitives.ReadUInt32LittleEndian(key.Slice(16));
        uint s9 = BinaryPrimitives.ReadUInt32LittleEndian(key.Slice(20));
        uint s10 = BinaryPrimitives.ReadUInt32LittleEndian(key.Slice(24));
        uint s11 = BinaryPrimitives.ReadUInt32LittleEndian(key.Slice(28));
        uint s12 = counter;
        uint s13 = BinaryPrimitives.ReadUInt32LittleEndian(nonce);
        uint s14 = BinaryPrimitives.ReadUInt32LittleEndian(nonce.Slice(4));
        uint s15 = BinaryPrimitives.ReadUInt32LittleEndian(nonce.Slice(8));

        uint x0 = s0, x1 = s1, x2 = s2, x3 = s3;
        uint x4 = s4, x5 = s5, x6 = s6, x7 = s7;
        uint x8 = s8, x9 = s9, x10 = s10, x11 = s11;
        uint x12 = s12, x13 = s13, x14 = s14, x15 = s15;

        // 20 rounds = 10 iterations of (column round + diagonal round).
        for (int i = 0; i < 10; i++)
        {
            // Column rounds.
            QuarterRound(ref x0, ref x4, ref x8, ref x12);
            QuarterRound(ref x1, ref x5, ref x9, ref x13);
            QuarterRound(ref x2, ref x6, ref x10, ref x14);
            QuarterRound(ref x3, ref x7, ref x11, ref x15);
            // Diagonal rounds.
            QuarterRound(ref x0, ref x5, ref x10, ref x15);
            QuarterRound(ref x1, ref x6, ref x11, ref x12);
            QuarterRound(ref x2, ref x7, ref x8, ref x13);
            QuarterRound(ref x3, ref x4, ref x9, ref x14);
        }

        // Add the original input state, then serialize little-endian.
        BinaryPrimitives.WriteUInt32LittleEndian(block, x0 + s0);
        BinaryPrimitives.WriteUInt32LittleEndian(block.Slice(4), x1 + s1);
        BinaryPrimitives.WriteUInt32LittleEndian(block.Slice(8), x2 + s2);
        BinaryPrimitives.WriteUInt32LittleEndian(block.Slice(12), x3 + s3);
        BinaryPrimitives.WriteUInt32LittleEndian(block.Slice(16), x4 + s4);
        BinaryPrimitives.WriteUInt32LittleEndian(block.Slice(20), x5 + s5);
        BinaryPrimitives.WriteUInt32LittleEndian(block.Slice(24), x6 + s6);
        BinaryPrimitives.WriteUInt32LittleEndian(block.Slice(28), x7 + s7);
        BinaryPrimitives.WriteUInt32LittleEndian(block.Slice(32), x8 + s8);
        BinaryPrimitives.WriteUInt32LittleEndian(block.Slice(36), x9 + s9);
        BinaryPrimitives.WriteUInt32LittleEndian(block.Slice(40), x10 + s10);
        BinaryPrimitives.WriteUInt32LittleEndian(block.Slice(44), x11 + s11);
        BinaryPrimitives.WriteUInt32LittleEndian(block.Slice(48), x12 + s12);
        BinaryPrimitives.WriteUInt32LittleEndian(block.Slice(52), x13 + s13);
        BinaryPrimitives.WriteUInt32LittleEndian(block.Slice(56), x14 + s14);
        BinaryPrimitives.WriteUInt32LittleEndian(block.Slice(60), x15 + s15);
    }

    private static void QuarterRound(ref uint a, ref uint b, ref uint c, ref uint d)
    {
        a += b; d ^= a; d = RotL(d, 16);
        c += d; b ^= c; b = RotL(b, 12);
        a += b; d ^= a; d = RotL(d, 8);
        c += d; b ^= c; b = RotL(b, 7);
    }

    // netstandard2.1 has no BitOperations.RotateLeft; the JIT folds this to ROL.
    private static uint RotL(uint v, int n) => (v << n) | (v >> (32 - n));
}

using System.Buffers.Binary;
using System.Security.Cryptography;
using System;

namespace Tanks.Net;

// Poly1305 one-time authenticator (RFC 8439 section 2.5).
//
// Port of the poly1305-donna 32-bit reference (radix-2^26, 5 limbs, using
// 32x32->64 multiplies), which is the representation that maps onto vanilla
// netstandard2.1 (no UInt128, no Math.BigMul(ulong,ulong)). Pure software,
// no allocation.
//
// CRITICAL: Poly1305 is a ONE-TIME authenticator. A given key must never
// authenticate two different messages, or an attacker recovers the key
// algebraically and forges at will. Callers derive a fresh key per message
// (RFC 8439 section 2.6: ChaCha20.Block(sessionKey, nonce)[0..32]); never
// reuse a key across packets.
public static class Poly1305
{
    public const int KeySize = 32;
    public const int TagSize = 16;
    private const int BlockSize = 16;

    public static void ComputeTag(ReadOnlySpan<byte> key, ReadOnlySpan<byte> message, Span<byte> tag)
    {
        ThrowHelper.ThrowIfNotEqual(key.Length, KeySize);
        ThrowHelper.ThrowIfLessThan(tag.Length, TagSize);

        State st = default;
        st.Init(key);

        // Complete 16-byte blocks carry the implicit high bit (1 << 128).
        int fullLength = message.Length & ~(BlockSize - 1);
        if (fullLength > 0)
            st.Blocks(message.Slice(0, fullLength), hibit: 1u << 24);

        // A trailing partial block is padded with an explicit 0x01 byte, so it
        // takes hibit = 0. An exact multiple has no trailing block (matches the
        // reference: finish() only runs on leftover > 0).
        int remaining = message.Length & (BlockSize - 1);
        if (remaining > 0)
        {
            Span<byte> tail = stackalloc byte[BlockSize];
            tail.Clear();
            message.Slice(fullLength).CopyTo(tail);
            tail[remaining] = 1;
            st.Blocks(tail, hibit: 0);
        }

        st.Finish(tag);
    }

    public static bool VerifyTag(ReadOnlySpan<byte> key, ReadOnlySpan<byte> message, ReadOnlySpan<byte> tag)
    {
        if (tag.Length != TagSize)
            return false;

        Span<byte> computed = stackalloc byte[TagSize];
        ComputeTag(key, message, computed);
        return CryptographicOperations.FixedTimeEquals(computed, tag);
    }

    private struct State
    {
        private uint r0, r1, r2, r3, r4;
        private uint h0, h1, h2, h3, h4;
        private uint pad0, pad1, pad2, pad3;

        public void Init(ReadOnlySpan<byte> key)
        {
            // r &= 0xffffffc0ffffffc0ffffffc0fffffff, spread over 5 26-bit limbs.
            r0 = (U32(key, 0)) & 0x3ffffff;
            r1 = (U32(key, 3) >> 2) & 0x3ffff03;
            r2 = (U32(key, 6) >> 4) & 0x3ffc0ff;
            r3 = (U32(key, 9) >> 6) & 0x3f03fff;
            r4 = (U32(key, 12) >> 8) & 0x00fffff;

            // h = 0 (default), pad = key[16..32].
            pad0 = U32(key, 16);
            pad1 = U32(key, 20);
            pad2 = U32(key, 24);
            pad3 = U32(key, 28);
        }

        // Processes a run of complete 16-byte blocks. `hibit` is 1<<24 for real
        // message blocks (the implicit 2^128 bit) and 0 for a padded final block.
        public void Blocks(ReadOnlySpan<byte> m, uint hibit)
        {
            uint r0 = this.r0, r1 = this.r1, r2 = this.r2, r3 = this.r3, r4 = this.r4;
            uint s1 = r1 * 5, s2 = r2 * 5, s3 = r3 * 5, s4 = r4 * 5;
            uint h0 = this.h0, h1 = this.h1, h2 = this.h2, h3 = this.h3, h4 = this.h4;

            while (m.Length >= BlockSize)
            {
                // h += m (each limb 26 bits, little-endian, bit-aligned)
                h0 += (U32(m, 0)) & 0x3ffffff;
                h1 += (U32(m, 3) >> 2) & 0x3ffffff;
                h2 += (U32(m, 6) >> 4) & 0x3ffffff;
                h3 += (U32(m, 9) >> 6) & 0x3ffffff;
                h4 += (U32(m, 12) >> 8) | hibit;

                // h *= r  (schoolbook, folding the high limbs back in via s = r*5)
                ulong d0 = (ulong)h0 * r0 + (ulong)h1 * s4 + (ulong)h2 * s3 + (ulong)h3 * s2 + (ulong)h4 * s1;
                ulong d1 = (ulong)h0 * r1 + (ulong)h1 * r0 + (ulong)h2 * s4 + (ulong)h3 * s3 + (ulong)h4 * s2;
                ulong d2 = (ulong)h0 * r2 + (ulong)h1 * r1 + (ulong)h2 * r0 + (ulong)h3 * s4 + (ulong)h4 * s3;
                ulong d3 = (ulong)h0 * r3 + (ulong)h1 * r2 + (ulong)h2 * r1 + (ulong)h3 * r0 + (ulong)h4 * s4;
                ulong d4 = (ulong)h0 * r4 + (ulong)h1 * r3 + (ulong)h2 * r2 + (ulong)h3 * r1 + (ulong)h4 * r0;

                // (partial) h %= p
                uint c;
                c = (uint)(d0 >> 26); h0 = (uint)d0 & 0x3ffffff;
                d1 += c; c = (uint)(d1 >> 26); h1 = (uint)d1 & 0x3ffffff;
                d2 += c; c = (uint)(d2 >> 26); h2 = (uint)d2 & 0x3ffffff;
                d3 += c; c = (uint)(d3 >> 26); h3 = (uint)d3 & 0x3ffffff;
                d4 += c; c = (uint)(d4 >> 26); h4 = (uint)d4 & 0x3ffffff;
                h0 += c * 5; c = h0 >> 26; h0 = h0 & 0x3ffffff;
                h1 += c;

                m = m.Slice(BlockSize);
            }

            this.h0 = h0; this.h1 = h1; this.h2 = h2; this.h3 = h3; this.h4 = h4;
        }

        public void Finish(Span<byte> tag)
        {
            uint h0 = this.h0, h1 = this.h1, h2 = this.h2, h3 = this.h3, h4 = this.h4;
            uint c;

            // fully carry h
            c = h1 >> 26; h1 &= 0x3ffffff;
            h2 += c; c = h2 >> 26; h2 &= 0x3ffffff;
            h3 += c; c = h3 >> 26; h3 &= 0x3ffffff;
            h4 += c; c = h4 >> 26; h4 &= 0x3ffffff;
            h0 += c * 5; c = h0 >> 26; h0 &= 0x3ffffff;
            h1 += c;

            // compute h + -p
            uint g0 = h0 + 5; c = g0 >> 26; g0 &= 0x3ffffff;
            uint g1 = h1 + c; c = g1 >> 26; g1 &= 0x3ffffff;
            uint g2 = h2 + c; c = g2 >> 26; g2 &= 0x3ffffff;
            uint g3 = h3 + c; c = g3 >> 26; g3 &= 0x3ffffff;
            uint g4 = h4 + c - (1u << 26);

            // select h if h < p, or h + -p if h >= p (constant time)
            uint mask = (g4 >> 31) - 1;
            g0 &= mask; g1 &= mask; g2 &= mask; g3 &= mask; g4 &= mask;
            mask = ~mask;
            h0 = (h0 & mask) | g0;
            h1 = (h1 & mask) | g1;
            h2 = (h2 & mask) | g2;
            h3 = (h3 & mask) | g3;
            h4 = (h4 & mask) | g4;

            // h = h % (2^128): re-pack the 5 26-bit limbs into 4 32-bit words
            h0 = (h0) | (h1 << 26);
            h1 = (h1 >> 6) | (h2 << 20);
            h2 = (h2 >> 12) | (h3 << 14);
            h3 = (h3 >> 18) | (h4 << 8);

            // mac = (h + pad) % (2^128)
            ulong f;
            f = (ulong)h0 + pad0; h0 = (uint)f;
            f = (ulong)h1 + pad1 + (f >> 32); h1 = (uint)f;
            f = (ulong)h2 + pad2 + (f >> 32); h2 = (uint)f;
            f = (ulong)h3 + pad3 + (f >> 32); h3 = (uint)f;

            BinaryPrimitives.WriteUInt32LittleEndian(tag, h0);
            BinaryPrimitives.WriteUInt32LittleEndian(tag.Slice(4), h1);
            BinaryPrimitives.WriteUInt32LittleEndian(tag.Slice(8), h2);
            BinaryPrimitives.WriteUInt32LittleEndian(tag.Slice(12), h3);
        }

        private static uint U32(ReadOnlySpan<byte> p, int offset)
            => BinaryPrimitives.ReadUInt32LittleEndian(p.Slice(offset));
    }
}

using System;
using Tanks.Sim;

namespace Tanks.Net
{
    /// <summary>
    /// Wire format for a single player's input on a single tick, plus a piggybacked state
    /// hash — the on-the-wire desync alarm. Twenty bytes:
    ///   [0..3]   tick the input is for (uint32, little-endian)
    ///   [4]      player id
    ///   [5]      button bitfield
    ///   [6..7]   turret aim (uint16 LE; low 11 bits used — see <see cref="Trig.AngleCount"/>)
    ///   [8..11]  hashTick: the sender's latest confirmed tick (uint32 LE)
    ///   [12..19] hash: the sender's GameState.Hash() at hashTick (uint64 LE)
    ///
    /// The receiver compares the reported hash against its own history for that tick — any
    /// mismatch means the sims diverged. Real netcode would batch several ticks per datagram
    /// and add sequencing/acks, but the payload stays this small.
    /// </summary>
    public static class InputCodec
    {
        public const int MessageSize = 20;

        /// <summary>Write with no hash report (fields zeroed). Kept for callers that don't track hashes.</summary>
        public static int Write(Span<byte> dst, uint tick, int player, PlayerInput input)
            => Write(dst, tick, player, input, 0u, 0UL);

        public static int Write(Span<byte> dst, uint tick, int player, PlayerInput input, uint hashTick, ulong hash)
        {
            if (dst.Length < MessageSize) throw new ArgumentException("buffer too small", nameof(dst));
            dst[0] = (byte)(tick & 0xFF);
            dst[1] = (byte)((tick >> 8) & 0xFF);
            dst[2] = (byte)((tick >> 16) & 0xFF);
            dst[3] = (byte)((tick >> 24) & 0xFF);
            dst[4] = (byte)player;
            dst[5] = (byte)input.Buttons;
            dst[6] = (byte)(input.TurretAim & 0xFF);
            dst[7] = (byte)((input.TurretAim >> 8) & 0xFF);
            dst[8] = (byte)(hashTick & 0xFF);
            dst[9] = (byte)((hashTick >> 8) & 0xFF);
            dst[10] = (byte)((hashTick >> 16) & 0xFF);
            dst[11] = (byte)((hashTick >> 24) & 0xFF);
            for (int i = 0; i < 8; i++)
                dst[12 + i] = (byte)((hash >> (8 * i)) & 0xFF);
            return MessageSize;
        }

        public static byte[] ToBytes(uint tick, int player, PlayerInput input)
            => ToBytes(tick, player, input, 0u, 0UL);

        public static byte[] ToBytes(uint tick, int player, PlayerInput input, uint hashTick, ulong hash)
        {
            var buffer = new byte[MessageSize];
            Write(buffer, tick, player, input, hashTick, hash);
            return buffer;
        }

        /// <summary>Read ignoring the hash report (for callers that don't track hashes).</summary>
        public static void Read(ReadOnlySpan<byte> src, out uint tick, out int player, out PlayerInput input)
            => Read(src, out tick, out player, out input, out _, out _);

        public static void Read(ReadOnlySpan<byte> src, out uint tick, out int player, out PlayerInput input,
                                out uint hashTick, out ulong hash)
        {
            if (src.Length < MessageSize) throw new ArgumentException("buffer too small", nameof(src));
            tick = (uint)(src[0] | (src[1] << 8) | (src[2] << 16) | (src[3] << 24));
            player = src[4];
            int turretAim = src[6] | (src[7] << 8);
            input = new PlayerInput((InputButtons)src[5], turretAim);
            hashTick = (uint)(src[8] | (src[9] << 8) | (src[10] << 16) | (src[11] << 24));
            hash = 0;
            for (int i = 0; i < 8; i++)
                hash |= (ulong)src[12 + i] << (8 * i);
        }
    }
}

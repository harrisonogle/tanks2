using System;
using Tanks.Sim;

namespace Tanks.Net
{
    /// <summary>
    /// Wire format for a single player's input on a single tick. Six bytes:
    ///   [0..3] tick (uint32, little-endian)
    ///   [4]    player id
    ///   [5]    button bitfield
    ///
    /// This is the smallest useful packet for lockstep/rollback. Real netcode will batch
    /// several ticks per datagram and add sequencing/acks — but the payload stays this small.
    /// </summary>
    public static class InputCodec
    {
        public const int MessageSize = 6;

        public static int Write(Span<byte> dst, uint tick, int player, PlayerInput input)
        {
            if (dst.Length < MessageSize) throw new ArgumentException("buffer too small", nameof(dst));
            dst[0] = (byte)(tick & 0xFF);
            dst[1] = (byte)((tick >> 8) & 0xFF);
            dst[2] = (byte)((tick >> 16) & 0xFF);
            dst[3] = (byte)((tick >> 24) & 0xFF);
            dst[4] = (byte)player;
            dst[5] = input.ToByte();
            return MessageSize;
        }

        public static byte[] ToBytes(uint tick, int player, PlayerInput input)
        {
            var buffer = new byte[MessageSize];
            Write(buffer, tick, player, input);
            return buffer;
        }

        public static void Read(ReadOnlySpan<byte> src, out uint tick, out int player, out PlayerInput input)
        {
            if (src.Length < MessageSize) throw new ArgumentException("buffer too small", nameof(src));
            tick = (uint)(src[0] | (src[1] << 8) | (src[2] << 16) | (src[3] << 24));
            player = src[4];
            input = PlayerInput.FromByte(src[5]);
        }
    }
}

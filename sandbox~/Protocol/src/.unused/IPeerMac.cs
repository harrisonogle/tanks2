using System.Buffers;
using System.Security.Cryptography;

namespace Tanks.Net;

public interface IPeerMac : IDisposable
{
    int TagSize { get; }
    void ComputeTag(ReadOnlySpan<byte> data, Span<byte> tag);
}

public static class PeerMacExtensions
{
    public static bool VerifyTag(this IPeerMac mac, ReadOnlySpan<byte> data, ReadOnlySpan<byte> tag)
    {
        ThrowHelper.ThrowIfNull(mac);

        if (tag.Length != mac.TagSize)
        {
            return false;
        }

        byte[]? computedTagRental = null;
        Span<byte> computedTag = mac.TagSize <= 1024
            ? stackalloc byte[mac.TagSize]
            : (computedTagRental = ArrayPool<byte>.Shared.Rent(mac.TagSize));

        try
        {
            mac.ComputeTag(data, computedTag);

            // This API helps prevent timing attacks
            // Otherwise SequenceEqual is faster
            return CryptographicOperations.FixedTimeEquals(tag, computedTag);
        }
        finally
        {
            if (computedTagRental is byte[] rental)
                ArrayPool<byte>.Shared.Return(rental);
        }
    }
}
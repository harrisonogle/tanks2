using System.Security.Cryptography;

namespace Tanks.Net;

public sealed class HmacSha256PeerMac : IPeerMac
{
    private readonly HMACSHA256 _hmac;

    public HmacSha256PeerMac(ReadOnlySpan<byte> key)
    {
        // netstandard2.1 requires an allocation here
        _hmac = new HMACSHA256(key.ToArray());
    }

    public int TagSize => 32;

    public void ComputeTag(ReadOnlySpan<byte> data, Span<byte> tag)
    {
        ThrowHelper.ThrowIfNotEqual(tag.Length, TagSize);

        if (!_hmac.TryComputeHash(data, tag, out int bytesWritten))
        {
            throw new CryptographicException("Failed to compute HMAC-SHA256 tag.");
        }
        if (bytesWritten != tag.Length)
        {
            throw new CryptographicException($"Unexpected MAC output length: wrote {bytesWritten}, not {tag.Length}");
        }
    }

    public void Dispose()
    {
        _hmac.Dispose();
    }
}
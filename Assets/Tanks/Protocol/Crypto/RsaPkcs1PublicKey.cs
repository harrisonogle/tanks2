using System.Security.Cryptography;
using System;

namespace Tanks.Net;

// PKCS#1 RSAPublicKey DER — SEQUENCE { INTEGER modulus, INTEGER publicExponent } —
// hand-rolled because Unity's Mono BCL throws PlatformNotSupportedException on
// RSA.ExportRSAPublicKey / ImportRSAPublicKey (.NET Core 3.0-era virtuals that Mono
// never implemented). ExportParameters/ImportParameters are supported everywhere,
// so we do the ASN.1 framing ourselves. Byte-for-byte identical to what
// ExportRSAPublicKey produces (asserted against the real API in the .NET tests).
// Cold path — runs once per handshake leg; allocations are fine here.
public static class RsaPkcs1PublicKey
{
    public static byte[] Export(RSA rsa)
    {
        ThrowHelper.ThrowIfNull(rsa);
        RSAParameters p = rsa.ExportParameters(includePrivateParameters: false);
        if (p.Modulus is null || p.Exponent is null)
            throw new CryptographicException("RSA parameters are missing the public key.");

        MeasureInteger(p.Modulus, out int nSkip, out int nContent);
        MeasureInteger(p.Exponent, out int eSkip, out int eContent);

        int seqContent = HeaderLength(nContent) + nContent + HeaderLength(eContent) + eContent;
        var der = new byte[HeaderLength(seqContent) + seqContent];

        int offset = WriteHeader(der, 0, tag: 0x30, seqContent);
        offset = WriteInteger(der, offset, p.Modulus, nSkip, nContent);
        offset = WriteInteger(der, offset, p.Exponent, eSkip, eContent);
        if (offset != der.Length)
            throw new CryptographicException("DER encoding error.");
        return der;
    }

    public static void Import(RSA rsa, ReadOnlySpan<byte> der, out int bytesRead)
    {
        ThrowHelper.ThrowIfNull(rsa);

        int offset = 0;
        ReadHeader(der, ref offset, expectedTag: 0x30, out int seqLength);
        int seqEnd = offset + seqLength;

        ReadOnlySpan<byte> modulus = ReadInteger(der.Slice(0, seqEnd), ref offset);
        ReadOnlySpan<byte> exponent = ReadInteger(der.Slice(0, seqEnd), ref offset);
        if (offset != seqEnd)
            throw new CryptographicException("Malformed RSAPublicKey: trailing bytes inside SEQUENCE.");

        rsa.ImportParameters(new RSAParameters
        {
            Modulus = modulus.ToArray(),
            Exponent = exponent.ToArray(),
        });
        bytesRead = seqEnd;
    }

    // --- encode ---

    // DER INTEGER is two's complement: strip redundant leading zeros from the
    // magnitude, then re-prefix one 0x00 if the top bit is set (values are unsigned).
    private static void MeasureInteger(ReadOnlySpan<byte> magnitude, out int skip, out int contentLength)
    {
        skip = 0;
        while (skip < magnitude.Length - 1 && magnitude[skip] == 0)
            skip++;
        bool pad = (magnitude[skip] & 0x80) != 0;
        contentLength = (magnitude.Length - skip) + (pad ? 1 : 0);
    }

    private static int HeaderLength(int contentLength)
    {
        if (contentLength < 0x80) return 2;         // tag, len
        if (contentLength <= 0xFF) return 3;        // tag, 0x81, len
        if (contentLength <= 0xFFFF) return 4;      // tag, 0x82, hi, lo
        throw new CryptographicException("RSA key too large to encode.");
    }

    private static int WriteHeader(byte[] der, int offset, byte tag, int contentLength)
    {
        der[offset++] = tag;
        if (contentLength < 0x80)
        {
            der[offset++] = (byte)contentLength;
        }
        else if (contentLength <= 0xFF)
        {
            der[offset++] = 0x81;
            der[offset++] = (byte)contentLength;
        }
        else
        {
            der[offset++] = 0x82;
            der[offset++] = (byte)(contentLength >> 8);
            der[offset++] = (byte)contentLength;
        }
        return offset;
    }

    private static int WriteInteger(byte[] der, int offset, ReadOnlySpan<byte> magnitude, int skip, int contentLength)
    {
        offset = WriteHeader(der, offset, tag: 0x02, contentLength);
        if (contentLength > magnitude.Length - skip)
            der[offset++] = 0x00; // sign pad
        magnitude.Slice(skip).CopyTo(der.AsSpan(offset));
        return offset + (magnitude.Length - skip);
    }

    // --- decode ---

    private static void ReadHeader(ReadOnlySpan<byte> der, ref int offset, byte expectedTag, out int contentLength)
    {
        if (offset + 2 > der.Length || der[offset] != expectedTag)
            throw new CryptographicException($"Malformed RSAPublicKey: expected tag 0x{expectedTag:X2}.");
        offset++;

        byte first = der[offset++];
        if ((first & 0x80) == 0)
        {
            contentLength = first;
        }
        else
        {
            int lengthBytes = first & 0x7F;
            if (lengthBytes == 0 || lengthBytes > 2 || offset + lengthBytes > der.Length)
                throw new CryptographicException("Malformed RSAPublicKey: unsupported DER length.");
            contentLength = 0;
            for (int i = 0; i < lengthBytes; i++)
                contentLength = (contentLength << 8) | der[offset++];
        }

        if (contentLength < 0 || offset + contentLength > der.Length)
            throw new CryptographicException("Malformed RSAPublicKey: content length exceeds input.");
    }

    private static ReadOnlySpan<byte> ReadInteger(ReadOnlySpan<byte> der, ref int offset)
    {
        ReadHeader(der, ref offset, expectedTag: 0x02, out int contentLength);
        if (contentLength == 0)
            throw new CryptographicException("Malformed RSAPublicKey: empty INTEGER.");

        ReadOnlySpan<byte> content = der.Slice(offset, contentLength);
        offset += contentLength;

        if ((content[0] & 0x80) != 0)
            throw new CryptographicException("Malformed RSAPublicKey: negative INTEGER.");

        // Strip the sign pad / redundant leading zeros down to the magnitude.
        int skip = 0;
        while (skip < content.Length - 1 && content[skip] == 0)
            skip++;
        return content.Slice(skip);
    }
}

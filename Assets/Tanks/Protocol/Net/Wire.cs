using System.Buffers.Binary;
using System.Diagnostics;
using System.Security.Cryptography;
using System;

namespace Tanks.Net;

public static class Wire
{
    // Verifies the auth tag at the packet trailer. `seq` is the header's
    // sequence number (the nonce); the tag covers everything before it,
    // header included.
    public static unsafe bool VerifyAuth(byte* buffer, int length, IPacketAuthenticator auth, ulong seq, out int tagLength)
    {
        ThrowHelper.ThrowIfNull(auth);

        tagLength = auth.TagSize;
        Debug.Assert(tagLength == NetworkConstants.AuthTagLength);
        if (length < tagLength)
        {
            return false;
        }

        int contentLength = length - tagLength;
        return auth.Verify(
            seq,
            new ReadOnlySpan<byte>(buffer, contentLength),
            new ReadOnlySpan<byte>(buffer + contentLength, tagLength));
    }

    // Parses a Ping message body (buffer starts at the Ping struct — past PacketHeader and MessageTag).
    // On success, out pointers point INTO the input buffer. They're only valid for its lifetime.
    public static unsafe bool TryParsePing(
        byte* buffer, int available,
        out Ping* ping,
        out byte* initiatorPubKey, out int initiatorPubKeyLen,
        out byte* pingSignature, out int pingSignatureLen)
    {
        ping = null;
        initiatorPubKey = null;
        initiatorPubKeyLen = 0;
        pingSignature = null;
        pingSignatureLen = 0;

        // Fixed header must fit.
        if (available < Ping.Size)
            return false;

        var p = (Ping*)buffer;

        // Length must cover the header and fit in the input.
        if (p->Length < Ping.Size)
            return false;
        if (p->Length > available)
            return false;

        // Offsets are relative to end-of-header. Must be monotonic and within the dynamic section.
        int dynamicSize = p->Length - Ping.Size;
        if (p->InitiatorPubKeyOffset > p->PingSignatureOffset)
            return false;
        if (p->PingSignatureOffset > dynamicSize)
            return false;

        byte* payload = buffer + Ping.Size;
        ping = p;
        initiatorPubKey = payload + p->InitiatorPubKeyOffset;
        initiatorPubKeyLen = p->PingSignatureOffset - p->InitiatorPubKeyOffset;
        pingSignature = payload + p->PingSignatureOffset;
        pingSignatureLen = dynamicSize - p->PingSignatureOffset;
        return true;
    }

    // Parses a Pong message body (buffer starts at the Pong struct — past PacketHeader and MessageTag).
    // On success, out pointers point INTO the input buffer. They're only valid for its lifetime.
    public static unsafe bool TryParsePong(
        byte* buffer, int available,
        out Pong* pong,
        out byte* responderPubKey, out int responderPubKeyLen,
        out byte* encryptedResponderNonce, out int encryptedResponderNonceLen,
        out byte* responderSignature, out int responderSignatureLen)
    {
        pong = null;
        responderPubKey = null;
        responderPubKeyLen = 0;
        encryptedResponderNonce = null;
        encryptedResponderNonceLen = 0;
        responderSignature = null;
        responderSignatureLen = 0;

        // Fixed header must fit.
        if (available < Pong.Size)
            return false;

        var p = (Pong*)buffer;

        // Length must cover the header and fit in the input.
        if (p->Length < Pong.Size)
            return false;
        if (p->Length > available)
            return false;

        // Offsets are relative to end-of-header. Must be monotonic and within the dynamic section.
        int dynamicSize = p->Length - Pong.Size;
        if (p->ResponderPubKeyOffset > p->EncryptedResponderNonceOffset)
            return false;
        if (p->EncryptedResponderNonceOffset > p->PongSignatureOffset)
            return false;
        if (p->PongSignatureOffset > dynamicSize)
            return false;

        byte* payload = buffer + Pong.Size;
        pong = p;
        responderPubKey = payload + p->ResponderPubKeyOffset;
        responderPubKeyLen = p->EncryptedResponderNonceOffset - p->ResponderPubKeyOffset;
        encryptedResponderNonce = payload + p->EncryptedResponderNonceOffset;
        encryptedResponderNonceLen = p->PongSignatureOffset - p->EncryptedResponderNonceOffset;
        responderSignature = payload + p->PongSignatureOffset;
        responderSignatureLen = dynamicSize - p->PongSignatureOffset;
        return true;
    }

    // Writes a Ping packet into buffer. Returns total bytes written.
    // Caller owns the initiator nonce (needs to keep it for KDF later).
    // Writes a Ping packet into buffer. Returns total bytes written.
    public static unsafe int WritePing(
        byte* buffer,
        int capacity,
        SessionContext ctx)
    {
        ThrowHelper.ThrowIfNull(ctx);

        // Header
        var header = (PacketHeader*)buffer;
        Initialize(header, ctx, PacketType.Ping);
        header->DSID = default; // zeroed on Ping; can't be known yet

        // Ping header (backpatch offsets below)
        var ping = (Ping*)(buffer + PacketHeader.Size);
        *ping = default;
        ping->AppProtocolId = ctx.AppProtocolId;
        ping->AppProtocolVersion = ctx.AppProtocolVersion;
        ctx.LocalNonce.CopyTo(new Span<byte>((byte*)&ping->InitiatorNonce, 16));

        byte* payload = (byte*)ping + Ping.Size;
        byte* cursor = payload;
        byte* ceil = buffer + capacity;

        // Field 1: initiator pubkey
        ping->InitiatorPubKeyOffset = (ushort)(cursor - payload);
        ctx.LocalKeyPair.PublicKey.CopyTo(new Span<byte>(cursor, (int)(ceil - cursor)));
        cursor += ctx.LocalKeyPair.PublicKey.Length;

        // Field 2: signature slot (written last, after we know Length)
        ping->PingSignatureOffset = (ushort)(cursor - payload);
        byte* sigSlot = cursor;
        cursor += ctx.Crypto.SignatureSize;

        if (cursor > ceil)
            throw new InvalidOperationException("Insufficient buffer size to write Ping.");

        ping->Length = (ushort)(cursor - (byte*)ping);

        // Sign everything before the sig slot
        ctx.LocalKeyPair.Sign(
            new ReadOnlySpan<byte>(ping, (int)(sigSlot - (byte*)ping)),
            new Span<byte>(sigSlot, ctx.Crypto.SignatureSize));

        return (int)(cursor - buffer);
    }

    // Writes a Pong packet into buffer. Returns total bytes written.
    // TODO: TryWritePong
    public static unsafe int WritePong(
        byte* buffer,
        int capacity,
        SessionContext ctx)
    {
        ThrowHelper.ThrowIfNull(ctx);
        var verifier = ctx.RemoteVerifier;
        var auth = ctx.Auth;
        ThrowHelper.ThrowIfNull(verifier);
        ThrowHelper.ThrowIfNull(auth);

        // Header
        var header = (PacketHeader*)buffer;
        Initialize(header, ctx, PacketType.Pong);

        // Pong header (backpatch offsets below)
        var pong = (Pong*)(buffer + PacketHeader.Size);
        *pong = default;
        pong->AppProtocolId = ctx.AppProtocolId;
        pong->AppProtocolVersion = ctx.AppProtocolVersion;
        ctx.RemoteNonce.CopyTo(new Span<byte>((byte*)&pong->EchoedInitiatorNonce, 16));

        byte* payload = (byte*)pong + Pong.Size;
        byte* cursor = payload;
        byte* ceil = buffer + capacity;

        // Field 1: responder pubkey
        pong->ResponderPubKeyOffset = (ushort)(cursor - payload);
        ctx.LocalKeyPair.PublicKey.CopyTo(new Span<byte>(cursor, (int)(ceil - cursor)));
        cursor += ctx.LocalKeyPair.PublicKey.Length;

        // Field 2: encrypted responder nonce
        pong->EncryptedResponderNonceOffset = (ushort)(cursor - payload);
        if (!verifier.TryEncrypt(ctx.LocalNonce, new Span<byte>(cursor, (int)(ceil - cursor)), out int encLen))
            throw new InvalidOperationException("Failed to encrypt responder nonce.");
        cursor += encLen;

        // Field 3: signature slot (write last, after we know Length)
        pong->PongSignatureOffset = (ushort)(cursor - payload);
        byte* sigSlot = cursor;
        int signatureSize = ctx.Crypto.SignatureSize;
        cursor += signatureSize;

        if (cursor > ceil)
            throw new InvalidOperationException("Insufficient buffer size to write Pong.");

        pong->Length = (ushort)(cursor - (byte*)pong);

        // Sign everything before the sig slot
        ctx.LocalKeyPair.Sign(
            new ReadOnlySpan<byte>(pong, (int)(sigSlot - (byte*)pong)),
            new Span<byte>(sigSlot, signatureSize));

        // Authenticate the whole packet up to the tag slot
        if (!TryWriteAuth(buffer, (int)(cursor - buffer), capacity, auth, header->Seq, out int authBytesWritten))
        {
            throw new InvalidOperationException("Failed to write auth tag.");
        }

        cursor += authBytesWritten;

        return (int)(cursor - buffer);
    }

    // Seal the buffer contents up to `length` bytes, writing the auth tag
    // immediately after them. `seq` must match the header's Seq field.
    public static unsafe bool TryWriteAuth(byte* buffer, int length, int capacity, IPacketAuthenticator auth, ulong seq, out int bytesWritten)
    {
        ThrowHelper.ThrowIfNull(auth);
        ThrowHelper.ThrowIfLessThan(capacity, length);

        int tagLength = auth.TagSize;
        Debug.Assert(tagLength == NetworkConstants.AuthTagLength);

        if (capacity - length < tagLength)
        {
            bytesWritten = 0;
            return false;
        }

        auth.Seal(
            seq,
            new ReadOnlySpan<byte>(buffer, length),
            new Span<byte>(buffer + length, tagLength));

        bytesWritten = tagLength;
        return true;
    }

    // Initializes the packet header and returns a pointer to the packet payload.
    public static unsafe byte* Initialize(PacketHeader* header, SessionContext ctx, PacketType type)
    {
        ThrowHelper.ThrowIfNull(ctx);

        NetworkConstants.Magic.CopyTo(
            new Span<byte>(header->Magic, NetworkConstants.Magic.Length));
        header->MajorVersion = NetworkConstants.MajorVersion;
        header->MinorVersion = NetworkConstants.MinorVersion;
        header->PeerId = ctx.LocalPeerId;
        header->SSID = ctx.LocalSessionId;
        header->DSID = ctx.RemoteSessionId;
        header->Type = type;
        header->Flags = default;
        header->Seq = ctx.SendSeq++; // fresh per send attempt; never reused
        return (byte*)header + PacketHeader.Size;
    }
}
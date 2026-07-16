# Wire contract

The session protocol is an *envelope* (cf. the DTLS record layer): it owns
identity, handshake, keys, MAC, liveness, and teardown. Everything else rides
inside `ApplicationData` packets as an opaque payload owned by exactly one
application protocol per session, agreed at handshake time.

## Invariants

- **Byte order is little-endian**, enforced: `Network`'s constructor throws on
  big-endian hosts. Structs are written to the wire as-is; both target
  architectures (x86_64, ARM64) are little-endian.
- **Max datagram size is 1200 bytes** (`NetworkConstants.MaxDatagramSize`),
  both directions, all packet types. Rationale: IP fragmentation avoidance
  (QUIC's number). This caps handshake crypto — RSA-2048 fits, RSA-3072 does
  not; the upgrade path is ECC, which shrinks the handshake.
- **One session per peer pair.** The application protocol may multiplex
  internally; the envelope never grows per subprotocol.
- Unordered, unreliable. The envelope drops replays: a 64-entry sliding
  window per receive direction (IPsec/DTLS style, 17 bytes of state, no
  packet buffering) rejects already-seen and too-old sequence numbers after
  tag verification. Out-of-order delivery within the window is tolerated
  (~1s at one packet per tick). The application protocol's tick idempotency
  remains as defense-in-depth.

## Packet header (long form, 52 bytes)

| Offset | Size | Field        | Notes                                    |
|-------:|-----:|--------------|------------------------------------------|
| 0      | 6    | Magic        | `tanks!`                                  |
| 6      | 1    | MajorVersion | exact match required (currently 1)        |
| 7      | 1    | MinorVersion | exact match required (currently 0)        |
| 8      | 16   | PeerId       | SHA-256(pubkey) truncated; handshake-only use; short-header removal candidate |
| 24     | 8    | SSID         | sender's session id (slot u32 + gen u32)  |
| 32     | 8    | DSID         | receiver's session id; zeroed on Ping     |
| 40     | 1    | Type         | `PacketType`                              |
| 41     | 1    | Flags        | bit 0 reserved: short/long discriminator  |
| 42     | 2    | (padding)    |                                           |
| 44     | 8    | Seq          | per-direction monotonic send counter; nonce for packet auth; incremented per send attempt, never reused |

## Packet types

| Type            | Payload                          | MAC              |
|-----------------|----------------------------------|------------------|
| Ping            | `Ping` struct (signed)           | none (no key yet)|
| Pong            | `Pong` struct (signed)           | yes (key derived from Ping+Pong nonces) |
| Disconnect      | `Disconnect` (4-byte reason)     | yes              |
| KeepAlive       | empty                            | yes              |
| ApplicationData | opaque, owned by the app protocol| yes              |

Auth: ChaCha20+Poly1305 (RFC 8439 style). Per packet, a one-time Poly1305
key is derived as `ChaCha20-Block(directionKey, nonce, counter=0)[0..32]`
with nonce = 4 zero bytes || Seq (LE). The 16-byte tag covers the entire
packet (header + payload, Seq included) and is appended as a trailer.

The handshake KDF emits 64 bytes of key material: the first 32 authenticate
initiator->responder traffic, the last 32 responder->initiator. Directional
keys keep the two sides' nonce spaces disjoint (both count from zero) and
make reflection fail at key level. Poly1305's one-time-key rule holds
because Seq is never reused within a direction.

## Handshake

Two legs (Ping → Pong), responder optimistic. Ping and Pong each carry
`AppProtocolId`/`AppProtocolVersion` (under the signature); a mismatch drops
the handshake. Simultaneous open resolves by peer-ID tiebreak: **lower PeerId
becomes the initiator**. Handshake legs (and Disconnect) retransmit on the
hedge schedule (250ms for up to 5s) until the state machine advances or an
authenticated packet proves the peer moved on.

## Pipe slot layout (not wire — the cross-thread rings)

Payload slots are `ApplicationDataSlotSize` (1280) bytes:
`[ PipeEventMetadata (12) | datagram bytes ... ]`. On RX the whole datagram
lands at offset 12, so the application payload begins at
`PipeEventHeadroom + metadata.Offset` (offset is `PacketHeader.Length` for
ApplicationData). On TX the game writes its payload at the same position and
the net thread stamps the header into the headroom and the MAC after the
payload — no copies in either direction.

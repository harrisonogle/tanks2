# Protocol.md

## Overview

A peer-to-peer UDP-based protocol for a deterministic lockstep tank game between two peers. Designed for rollback netcode with frame-advantage throttling. Friends-only build — no authentication or encryption.

## Transport

- UDP, peer-to-peer, single connection between two peers
- No reliability layer below the protocol — the protocol handles reliability where it needs it (handshake) and exploits redundancy where it doesn't (game inputs)
- Packets are unordered; messages within a packet are parse-order-independent

## Determinism prerequisites

The sim must be bit-exact across both peers given identical inputs:

- All state in integers (16.16 fixed-point via `Fixed` struct)
- No `MathF` / `Math` floating-point operations in sim code
- Deterministic PRNG seeded from handshake nonces
- Stable hash function for state hashing (not `HashCode.Combine` — it's randomized per process)
- No iteration over `Dictionary`/`HashSet` in sim code; use sorted containers or explicit ID iteration
- No `DateTime.Now` / `Stopwatch` — time advances by tick count only

## Lifecycle

### Roles

Any peer can be initiator or responder. On simultaneous open, **lower `PeerId` wins the responder role**; the higher-ID peer abandons its Ping and restarts as responder.

### State machines

**Initiator role:**
```
CLOSED ──user opens / send Ping──> SYN_SENT ──recv Pong / send PingPong──> ESTABLISHED
```

**Responder role:**
```
CLOSED ──recv Ping / send Pong──> SYN_RCVD ──recv PingPong / recv any data──> ESTABLISHED
```

### Handshake legs (3-leg, 1.5 RTT)

| Leg | From | Message | Proves |
|-----|------|---------|--------|
| 1 | I → R | Ping | I exists, wants to talk |
| 2 | R → I | Pong | I→R direction works |
| 3 | I → R | PingPong | R→I direction works |

### Retransmit discipline

- **Ping**: initiator retransmits every ~250ms until Pong arrives
- **Pong**: responder retransmits every ~250ms until *either* PingPong *or* any post-handshake data packet arrives
- **PingPong**: initiator sends once, then immediately starts sending steady-state data. The first data packet doubles as proof to responder that initiator reached ESTABLISHED.

### Optimistic responder

Responder may start sending data immediately after sending Pong (in SYN_RCVD), before receiving PingPong. If the handshake never completes, the data is wasted but cheap.

### Bootstrap RTT

Ping carries `SendTimestamp`; Pong echoes it as `EchoTimestamp` and adds its own `SendTimestamp`; PingPong echoes that. Used once to seed initial input delay buffer sizing. Not used in steady state.

### Silence detection

If no packet arrives for ~1 second in any post-handshake state, tear down. Catches half-open states and connection loss.

### Teardown

`DisconnectMessage` with a reason byte, or silence timeout. Terminal — no graceful close handshake.

## Steady state

### Tick model

- Fixed tick rate (60 Hz)
- Local tick counter starts at 0 when ESTABLISHED fires locally
- Tick and render-frame rates are decoupled in the sim

### Input delay

- Default 2 ticks (configurable, user-adjustable 1–4)
- Local input polled on tick N is stamped "apply on tick N+delay" and queued + sent
- Absorbs expected one-way delay so rollback only handles jitter
- Set once at session start; do not adjust mid-match

### Input redundancy

- Each `InputMessage` carries the last K=8 ticks of inputs
- Up to K−1 consecutive packet losses are recovered without retransmission
- No explicit retransmit mechanism for inputs

### Inputs are state, not events

Inputs are `InputButtons` bitfields (`[Flags] enum : ushort`) + quantized analog (`TurretAim` ushort, 0–65535 → 0–360°). The sim decides what each bitfield means in context; "input is impossible later" is not a wire-protocol concern.

### Frame advantage (throttling)

Every steady-state packet carries an `AdvantageMessage` with `(CurrentTick, AckedTick)`:
- `CurrentTick` — sender's current local tick
- `AckedTick` — highest tick from peer for which inputs have been received

**Computation (per peer):**
1. `local_adv = CurrentTick - AckedTick` (own staleness in ticks)
2. On receive: `sample = (local_adv_self - local_adv_remote) / 2`
3. Push sample into 32-entry ring buffer
4. `mean = average(ring_buffer)`
5. If `mean > 2` ticks (deadband), stall the local sim for `floor(mean)` ticks
6. **Reset the ring buffer immediately after stalling** (prevents integrator windup)
7. Cooldown: don't make another stall decision until the buffer has refilled

**Path-symmetry assumption.** The `/2` averaging cancels one-way delay only if forward and reverse paths are roughly equal. Asymmetric routes bias by `(OWD_forward − OWD_reverse)/2`.

### Desync detection

- Every ~30 ticks, sender includes a `StateHashMessage` with `(Tick, Hash)`
- Receiver compares against its own simulated hash for that tick — but only after its own sim has caught up to that tick. Queue remote hashes that arrive ahead.
- Mismatch → real divergence. Policy: end match (preferred for competitive); log + continue (development).

## Implicit ACKs and replay defense

No packet sequence numbers, no separate ACK messages. Each message defends itself via its own tick-stamped idempotency:

- **InputMessage**: receiver only files inputs for ticks it doesn't already have
- **AdvantageMessage**: receiver only updates the smoother if `CurrentTick > last_seen_CurrentTick` from this peer
- **StateHashMessage**: receiver only stores/checks if it doesn't already have a hash for that tick
- **Handshake**: nonces + state machine handle replay
- **Disconnect**: terminal

Out-of-order delivery is naturally tolerated — every check is content-based, not order-based.

## Wire format

All multi-byte fields are **big-endian** (network byte order). Fields are not word-aligned; pack tightly.

### Packet structure

```
[GameProtocolHeader]
[MessageHeader][message body]
[MessageHeader][message body]
...
```

One UDP datagram per packet. Every packet must fit in path MTU (~1200 bytes conservative). Bulk transfer (snapshots, replays) is out-of-scope — would need its own packet type and chunking sub-protocol.

### Packet header (8 bytes)

| Field | Size | Notes |
|-------|------|-------|
| Magic | 1B | Rejects stray UDP |
| MajorVersion | 1B | Incompatible bump |
| MinorVersion | 1B | Forward-compatible bump (unknown messages must be skippable for this to mean anything; not yet implemented) |
| Flags | 1B | Reserved; sender writes 0, receiver ignores |
| SessionId | 4B | Established during handshake |

### Message header (8 bytes)

| Field | Size | Notes |
|-------|------|-------|
| Type | 4B | `MessageType` enum |
| Length | 4B | Body length (not including this header) |

Dispatcher slices each message body to `Length` and hands the bounded span to the typed reader. Type/length sizes are oversized for the actual ranges (~20 types, ~1200 byte messages) — could be `byte` + `ushort` for ~5 bytes/message savings; current choice favors simplicity.

### Message types

| Value | Name | Lifecycle | Notes |
|-------|------|-----------|-------|
| 0 | None | — | Sentinel; never sent |
| 1 | Ping | Handshake leg 1 | |
| 2 | Pong | Handshake leg 2 | |
| 3 | PingPong | Handshake leg 3 | |
| 4 | Disconnect | Any | Terminal |
| 5 | Input | Steady state | |
| 6 | StateHash | Steady state | ~1 per 30 packets |
| 7 | Advantage | Steady state | Every packet |

### Message bodies

**PingMessage (24 bytes)**

| Field | Size |
|-------|------|
| Nonce | 8B (long) |
| PeerId | 8B (long) — for tie-break |
| SendTimestamp | 8B (long) — for bootstrap RTT |

**PongMessage (40 bytes)**

| Field | Size |
|-------|------|
| InitiatorNonce | 8B (echoed) |
| ResponderNonce | 8B |
| PeerId | 8B |
| EchoTimestamp | 8B (initiator's, echoed) |
| SendTimestamp | 8B (responder's) |

**PingPongMessage (16 bytes)**

| Field | Size |
|-------|------|
| ResponderNonce | 8B (echoed) |
| EchoTimestamp | 8B (responder's, echoed) |

**DisconnectMessage (1 byte)**

| Field | Size |
|-------|------|
| Reason | 1B (`DisconnectReason` enum) |

**InputMessage (5 + 4×N bytes)**

| Field | Size | Notes |
|-------|------|-------|
| InputTick | 4B (uint) | Newest tick in this packet |
| InputCount | 1B (byte) | Number of inputs; 0 allowed |
| Inputs | 4×N | Oldest-first; `Inputs[N-1]` is at `InputTick`, `Inputs[0]` is at `InputTick - InputCount + 1` |

**PeerInput (4 bytes)** — element of `Inputs[]`

| Field | Size |
|-------|------|
| Buttons | 2B (`InputButtons` `[Flags]` enum, ushort) |
| TurretAim | 2B (ushort, 0–65535 → 0–360°) |

**StateHashMessage (12 bytes)**

| Field | Size |
|-------|------|
| Tick | 4B (uint) |
| Hash | 8B (ulong) |

**AdvantageMessage (8 bytes)**

| Field | Size |
|-------|------|
| CurrentTick | 4B (uint) | Sender's current tick |
| AckedTick | 4B (uint) | Highest tick from remote where inputs have been received |

## Parsing rules

- Packet header parses first; magic + major version are validated by the parser (not the serializer) before any further work.
- Message header `Length` is body-only; dispatcher uses it to slice the next message.
- Messages within a packet are parse-order-independent. Each message handler updates state only; it does not act. Acting (stalling, advancing sim, etc.) happens at tick boundaries, reading the accumulated state.
- A duplicate message type within one packet is malformed — reject the whole packet.
- A read failure on any message is treated as packet-level malformed — discard the whole packet, do not partially apply.

## Behavior summary (the "fancy" parts)

Things not obvious from a naive "send inputs over UDP" sketch:

1. **Frame advantage** — symmetric tick exchange, ring buffer, deadband, reset-on-stall
2. **Input delay as static budget separate from rollback** — the hybrid is the actually-shipped pattern, not pure-rollback-zero-delay
3. **Input redundancy as the loss-recovery mechanism** — no retransmits, just K=8 ticks per packet
4. **State hash for desync detection** — and the integer-state discipline that makes the hash meaningful
5. **Implicit ACKs via tick numbers** — no seq numbers, no separate ACK messages
6. **Content-based replay defense** — each message type defends itself via "only update if newer"
7. **Bootstrap RTT in handshake only** — not in steady state
8. **Optimistic responder** — sends data after Pong, before PingPong arrives
9. **Tie-break collision resolution** — simpler than TCP's simultaneous-open merge
10. **Two-generals tolerance** — handshake convergence bounded by silence timeout, not strict reliability

## Open items / deferred

- **HMAC authentication** — dropped for the friends-only build. Add back as a truncated HMAC (8 bytes) over the whole packet if shipping publicly. Frame number (`CurrentTick`) serves as the implicit replay-defense nonce inside the MAC'd region.
- **Forward-compatible message skipping** — `MinorVersion` exists but the dispatcher does not yet skip unknown message types. Either implement the skip path or collapse to a single version byte.
- **Vestigial `Flags` byte** — reserved; drop if no use materializes.
- **Type/Length size reduction** — could shrink message header from 8B to 3B (`byte` + `ushort`). Cosmetic; revisit if bandwidth matters.
- **Bulk transfer sub-protocol** — needed for full state snapshots / resync. Currently out of scope.

## Default tunables

| Parameter | Default | Notes |
|-----------|---------|-------|
| Tick rate | 60 Hz | |
| Input delay | 2 ticks | User-configurable 1–4 |
| Input redundancy K | 8 ticks | |
| State hash cadence | every 30 ticks | |
| Ring buffer size | 32 samples | |
| Stall deadband | 2 ticks | |
| Handshake retransmit | 250 ms | |
| Silence timeout | 1000 ms | |
| Fixed-point format | 16.16 | ±32,768 range, ~1.5e-5 precision |
# Game protocol design concerns from chat with my friend

Copied summary below.

## Recap of what we baked in

Working backwards from the design, here's everything that's "active behavior" beyond the basic packet shape:

### Handshake (3-leg, 1.5 RTT)

- **Ping / Pong / PingPong** legs. Initiator sends Ping; responder sends Pong (committing to SYN_RCVD optimistically — it can start sending data after Pong, before seeing PingPong); initiator sends PingPong on receiving Pong and transitions to ESTABLISHED.
- **Tie-break collision resolution.** If both sides try to initiate simultaneously, lower `PeerId` wins the responder role; higher restarts as responder. No TCP-style "merge" — simpler state machine.
- **Retransmit with timeout.** Each leg is retransmitted (~250ms) until the sender sees evidence the other moved on. Ping retransmits until Pong arrives; Pong retransmits until PingPong *or* any post-handshake data arrives; PingPong retransmits until... no further confirmation possible.
- **First data packet as implicit ACK-of-PingPong.** Two-generals workaround: if PingPong is lost, the first authenticated steady-state packet from initiator tells responder "we're established." If everything is lost, both peers time out via silence detection.
- **Silence detection.** ~1 second of no packets → tear down. Catches half-open states.
- **Bootstrap RTT measurement** via `SendTimestamp` / `EchoTimestamp` in Ping/Pong. Used to seed initial input delay buffer size; doesn't appear in steady state.

### Steady state — per-tick data flow

- **Input delay** (default ~2 ticks). Local inputs polled on tick N are stamped "apply on tick N+delay" and queued. This is the static budget that absorbs expected one-way delay; rollback absorbs the jitter on top.
- **Input redundancy.** Each `InputMessage` carries the last K ticks of inputs (default K=8), so up to K-1 consecutive packet losses recover without retransmission. `InputTick` is the newest tick; `inputs[]` is oldest-first; receiver only files ticks it doesn't already have (natural idempotency, doubles as replay defense for INPUT).
- **State-based input semantics.** Inputs are bitfields per tick, not events. The sim decides what each bitfield means in context — no preconditions, no "this input is impossible later." Makes rollback trivial.

### Frame advantage (the GGPO mechanism)

This is the big one you flagged. Specifically:

- **Symmetric tick exchange.** Every steady-state packet carries an `AdvantageMessage` with `(CurrentTick, AckedTick)` — sender's current tick, and highest tick from peer where inputs have been received.
- **Local advantage computation.** Each peer computes `local_adv = CurrentTick - AckedTick` (their staleness).
- **Symmetric averaging.** Receiver computes `(local_adv_self - local_adv_remote) / 2` to extract clock-skew-equivalent in ticks, with one-way delay canceling out under path-symmetry assumption.
- **Ring buffer + mean.** 32-sample ring buffer of those values, take the mean.
- **Deadband + stall.** If mean exceeds ~2 ticks, stall the local sim (skip a tick) by that many ticks. Buffer is reset on stall to prevent integrator windup; deadband prevents micro-corrections.

### Desync detection

- **State hash every ~30 ticks.** `StateHashMessage` carries `(Tick, Hash)`. Receiver checks against its own simulated hash for that tick when it catches up. Mismatch → real desync → end match (your call on policy).
- **Determinism prerequisites you already have.** Integer-only state, 16.16 fixed-point, deterministic PRNG seeded from handshake, no float trig, stable hash function (not `HashCode.Combine`).

### Lifecycle / teardown

- **`DisconnectMessage`** with a reason byte. Terminal.
- **Silence timeout** also triggers teardown.

### Packet structure (TLV)

- **Outer header**: magic, version (major/minor), flags (vestigial), session_id. No `seq_num` (we decided each message defends itself via tick-based idempotency; you mentioned adding it back for wire debugging only).
- **TLV messages inside**: parse-order-independent, each self-contained, dispatcher slices to `MessageHeader.Length` and hands the bounded slice to the typed reader.
- **No HMAC** (you just dropped it — friends-only build).

---

## Things in the "fancy / not-obvious" category

You asked specifically about things at the level of frame advantage. The list of mechanisms that aren't obvious from a naive "send inputs over UDP" sketch:

1. **Frame advantage** — symmetric exchange + ring buffer + deadband + reset-on-stall
2. **Input delay as a static budget separate from rollback** — most people coming to rollback for the first time think "rollback = no delay"; the hybrid is the actually-shipped pattern
3. **Input redundancy as the loss-recovery mechanism** — no retransmits, just send the last K ticks every packet
4. **State hash as desync detection** — and the integer-state discipline that makes it work
5. **Implicit ACK via tick numbers** — no separate ACK packets, no seq numbers, just "AckedTick" in AdvantageMessage and "newest tick in InputMessage"
6. **Idempotency by content, not protocol** — INPUT, ADVANTAGE, STATE_HASH each defend against replay via their own tick fields rather than via a packet-level seq
7. **Bootstrap RTT in handshake** — one-shot, doesn't recur in steady state
8. **Optimistic responder** — responder can send data after Pong without waiting for PingPong; first data packet doubles as proof initiator reached ESTABLISHED
9. **Tie-break collision resolution** — simpler than TCP's simultaneous-open merge
10. **Two-generals tolerance** — handshake doesn't promise strict reliability; convergence is bounded by silence-detection timeout

The ones I'd flag as "easy to forget when writing this from scratch":

- **Reset the ring buffer on stall.** Without it, the smoother keeps insisting you're ahead even after you've corrected, and you oscillate.
- **Deadband on the stall decision.** Without it, you stall constantly on noise.
- **Input redundancy oldest-first in the array, newest at `InputTick`.** Easy to get the indexing flipped.
- **State hash check happens when the receiver catches up to that tick, not on receive.** Receiver may get the hash for tick 500 while still simulating tick 470 — queue and check later.
- **Tick numbers in messages are the replay defense.** Without seq_num, the parser/handlers have to enforce "only update if newer" per message type, or replays silently re-apply.
- **Responder retransmits Pong until *either* PingPong or any data packet.** Easy to only handle the PingPong case and end up retransmitting Pong forever in the "PingPong lost but data arrives" path.
- **Handshake retransmit isn't reliability for steady state.** Don't accidentally build a general retransmit mechanism — it's specifically the 3 handshake legs that need it.

That's the lot. Frame advantage is the headline mechanism but #2 (input delay as static budget) and #4 (state hash + integer discipline) are the other two that change the architecture meaningfully versus a naive design.
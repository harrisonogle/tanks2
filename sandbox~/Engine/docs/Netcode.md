# The netcode, in one read

Everything lands in `Engine.cs` (`TickGameView` and below) + `PlayerState`/`PeerState`.
Three commits: `106d9cd` (core), `ba5569d` (Unity wiring), plus this doc.

## The one-sentence theory

Every player stream has exactly one **stamping authority** (local streams are stamped
here at the tick boundary; remote streams were stamped there), stamped inputs are
**final at transmit time**, and both sims execute identical `(tick, inputs)` histories —
prediction fills the gap while remote stamps are in flight, rollback repairs the
difference when they land.

## Data model (who owns what)

- `PlayerState` — one per player slot. `AuthoritativeInput` ring (stamped, final),
  `AppliedInput` ring (what the sim actually consumed — prediction or authoritative),
  `Frontier` (exclusive: next tick we still need), `LatestSample`/`PrevLevel`
  (local hydration only).
- `PeerState` — one per sim node, self included (`IsLocal`). Owns the per-peer streams:
  received hash ring, `LastTickRecvd` (what we ack THEM), `LastAckedTick` (their ack of US).
- `GameViewState` — per-match: sim, `StateHistory` (snapshot+hash per tick),
  `ConfirmedTick`, accumulator/pacing, counters.

## The tick (`TickGameView`, ~1ms loop cadence)

1. **RollBackIfMispredicted** — settle corrections that arrived in the drains.
2. **Accumulator** gates 60Hz sim ticks (Stopwatch-driven; stall zeroes the
   accumulator so resume is gentle, not a burst).
3. Per tick T = `GameState.Tick + 1` (derived, never passed — wrong-T is inexpressible):
   - `CanExecute`? Two bounds, both also serving as implicit match-start sync:
     - **prediction bound**: `T + 1 <= remoteFrontier + 8` — never simulate more than
       8 ticks past the newest remote input;
     - **ack bound**: `T <= theirAckOfUs + 8` — never outrun what they've confirmed
       receiving from US. This is what makes the fixed 8-slot window lossless: a
       packet stamped T always reaches back over everything the receiver can miss.
   - **Mint** local inputs: `LatestSample` (a LEVEL: held buttons + aim) → derive the
     Dash **edge** against `PrevLevel` → `MintLocal(T)`. Tick assignment happens here
     and only here — the render thread ships unstamped levels (edge bits must never
     cross a latest-wins ring; a coalesced edge is a lost press, a coalesced level
     is nothing).
   - **Resolve** every player's input: authoritative if `T < Frontier`, else predict
     (repeat newest authoritative). Record what was applied.
   - `Simulation.Tick` → `History.Record` (snapshot + hash) → `EmitGameState`.
4. **CheckDesync** — compare hashes at mutually CONFIRMED ticks only (speculative
   hashes would flag every remote rollback as a desync).
5. **SendMatchBundles** — after ticks, plus a heartbeat while stalled (the resends
   are what clear the remote's stall). Bundle = input window (our newest 8 stamps,
   ring-keyed by `tick % 8`) + confirmed-tick StateHash + Advantage{CurrentTick,
   LastTickRecvd}. Advantage's ack half feeds the ack bound; throttling is still TODO.

## Rollback (`RollBackIfMispredicted`)

Confirmed frontier C = min over players of (covered ticks), capped at current tick.
Scan (`ConfirmedTick`, C] comparing applied vs authoritative; at the first mismatch F:
restore snapshot F−1 (`CopyFrom`), `History.Rewind(F−1)`, replay to the present via the
same `ExecuteTick` with `advancePastFrontier: false` — local inputs are consumed
verbatim from the ring (stamped = final; re-minting would desync the peer that already
has our stamps). Confirmed ticks never change again — that's what makes their hashes
exchangeable.

## Unity side (`ba5569d`)

Render thread never touches `Network` — the engine owns it exclusively (the TX rings
are SPSC; two producers would corrupt them). ConnectScreen → `bus.StartMatch/
StartLocalMatch` intents; `OnMatchStarted` builds the Match root (GameView on it, Host
injected); `OnMatchEnded` destroys the subtree. EngineHost samples both couch seats'
levels per frame → `bus.SendInput` (engine ignores extra seats in online matches).
GameView's OnGUI shows tick + FNV of the snapshot — the eyeball canary; the engines
also cross-check via the StateHash stream and log `DESYNC` loudly.

## Bugs found while landing (worth knowing for the rewrite)

1. **Active-opener roach motel**: `SessionAccepted` was phase-gated under
   `ConnectScreen`, but the dialer is already in `ConnectingToPeer` when its own
   accept arrives → dialing out could never start a match. Lifecycle events are
   phase-independent (third instance of this lesson).
2. **`engine.Handle` clobber**: emits ran inside the drain loops while sharing the
   drain's iteration handle — `EmitMatchStarted` (called from the `StartMatch` intent
   handler) overwrote the in-flight RX handle, and the drain's `finally` then returned
   a TX-pool handle to the RX pool. Emits use local handles now.
3. **Cross-pool deref in EngineBus**: `TryGetGameState`/`TryGetArena` deref'd retained
   handles against `_rx`, but they were dequeued from `_tx`. Per-pool randomized gens
   caught it instantly — the guard working exactly as designed.

## Validation

`sandbox~/Engine/tests` — three E2E tests drive the WHOLE stack through EngineBus
exactly as Unity does (test thread = render thread): online match to tick 300 with
cross-checked hashes; the same under 25% loss + duplication; couch co-op. All green.
`dotnet test sandbox~/Sandbox.slnx` = 124 tests green.

Manual two-client run: Editor (Play) + `Builds/Tanks.app`, second instance binds
47778 automatically. On one: enter the other's PeerId + `127.0.0.1:<port>`, Connect.
Same tick ⇒ same hash on both HUDs.

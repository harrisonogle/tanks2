# Netcode Session — Kickoff

> Briefing for both developers. One of you wrote the bootstrap; the other is walking in
> cold. Read this once at the start, then keep `CLAUDE.md` and `README.md` open as references.

## The one-paragraph version

This is a crude *Tanks* (Wii) recreation built **specifically to learn netcode**. The
game itself is already done — a deterministic, locally-playable 1v1 sandbox. **This
session's job is to make it network-playable: lockstep first, then rollback, then a
visualization layer that makes the netcode visible on screen.** The graphics will stay
crude; the netcode is the point.

## The plan, and why it's shaped this way

1. **Phase 1 (this session): P2P deterministic lockstep → rollback.**
2. **Phase 2 (later): authoritative client-server** built on the same simulation core.

Neither of us are netcode experts going in, and the eventual "real game" target is
authoritative client-server. So why P2P-rollback first?

- **Cleanest first lesson.** Rollback's mental model is small — pure sim + ring buffer +
  re-sim — and the topology is symmetric (two equal peers, no host/client asymmetry).
  Client-server has more fuzzy, tunable parts (interpolation buffers, snapshot rates,
  lag-compensation windows) where things can look fine but be subtly wrong; harder to
  learn on cold.
- **Definitive correctness signal.** With a deterministic sim, two peers' state hashes
  must match on every confirmed tick. That's a *binary* canary — any mismatch is a bug.
  Client-server gives you no such crisp signal.
- **~80% of the machinery transfers.** Client-side prediction + server reconciliation
  *is* rollback — you "roll back" to the last server-confirmed snapshot and replay
  unacknowledged local inputs. Same `GameState.Clone`, same `History`, same `Tick`.
  Phase 2 reuses everything we build today; it just adds a third process (the server)
  and changes what triggers the rollback (a server snapshot, not a remote input).
- **Determinism is a gift to client-server too.** When client and server run byte-
  identical logic, reconciliation is cheap and accurate.

The architecture is deliberately set up so phase 2 layers on rather than rewrites.

## Lay of the land

```
Assets/Tanks/
  Sim/   Tanks.Sim    — pure deterministic C# (no UnityEngine, no float in logic)
                        Fixed (16.16), Trig (lookup tables), GameState, Simulation.Tick,
                        Arena, Xorshift32
  Net/   Tanks.Net    — also pure C#; ITransport, InProcessNetwork (latency/jitter/loss),
                        InputCodec (8-byte wire format: tick + player + buttons + turret aim)
  Game/  Tanks.Game   — thin Unity layer; Bootstrap (entry point, RuntimeInitialize),
                        SimRunner (fixed-tick loop + history + the SEAM),
                        GameView (primitives), DebugHud (IMGUI), InputSampler
SimTests~/            — standalone `dotnet test` project linking Sim/Net source.
                        23 tests covering math, gameplay, determinism, network seam.
                        Run with: dotnet test (from repo root or that folder)
```

The two asmdefs `Tanks.Sim` and `Tanks.Net` are marked `noEngineReferences: true`.
**That constraint is load-bearing** — it's what lets `SimTests~` compile the same `.cs`
files under plain .NET, and it's what keeps the simulation deterministic. If you find
yourself wanting to reach for `UnityEngine` or `float` inside Sim/Net, stop.

## The contract (invariants you cannot break)

1. **Determinism.** `state + inputs → next state` is a pure function. No floats in
   simulation logic (use `Fixed`), no wall-clock time (the sim only knows ticks), no
   `Random.Range` (use `Xorshift32` with seed in `GameState.Rng`), no `UnityEngine` in
   Sim/Net.
2. **`GameState.Hash()` is the desync canary.** If two peers disagree on the hash for the
   same confirmed tick, the sim has diverged — fix that *first* before anything else.
   `DeterminismTests` enforce this; never let them regress.
3. **`Tanks.Game` is a view.** It samples input, calls `Simulation.Tick`, and renders.
   It never contains gameplay rules. Netcode logic also lives in Game (or a new
   `Tanks.Netcode` module if you want to split it) — but it talks to Sim/Net only
   through their public APIs.
4. **Code-driven setup.** `Bootstrap` builds everything at play time via
   `[RuntimeInitializeOnLoadMethod]`. There are no hand-authored scenes and no inspector
   wiring. Keep it that way — it stays reviewable and version-controllable.

## What's already wired (and what's NOT)

Already wired, working, and tested:
- The deterministic sim. Two tanks driving and shooting, bullets bouncing off walls,
  one-shot kill. **Movement is 8-way in world space** (faithful to original *Tanks* —
  not tank-controls). The four direction bits map to ±Y/±X; the body visually snaps to
  face the input direction; diagonals are scaled by `1/√2` so total speed stays constant.
  **Turret aims independently of body** — absolute angle, quantized to 11 bits (2048
  steps); the input layer maintains the angle locally and the integer rides the wire.
  **Inputs come from the Unity Input System** (`com.unity.inputsystem`): P1 =
  `Gamepad.all[0]` if connected else WASD+Q/E keyboard; P2 = `Gamepad.all[1]` else
  arrows+,/. keyboard. Right stick → quantized turret aim; A/Cross or right trigger →
  fire. 60-tick fixed step. `R` resets the match.
- `SimRunner` ticks at 60 Hz with frame-rate independence, hashes every state, and
  records snapshots into a 256-tick `StateHistory` ring buffer.
- `InProcessNetwork` — two endpoints, send/receive bytes, tunable
  `LatencyTicks` / `JitterTicks` / `LossChance`. Reproducible (RNG-seeded).
- `InputCodec` — 8 bytes per (tick, player, buttons + turret aim) message.
- A test (`InputsExchangedOverWireKeepSimsInLockstep`) that already proves two
  independent sims exchanging inputs over the fake wire stay bit-identical for 1000
  ticks. **This is the loop you're going to build at runtime in `SimRunner` — it's
  literally already working in a test.** Read it first.

Explicitly NOT wired:
- `SimRunner` currently samples **both** players locally (couch-coop sandbox).
- The `Network` field on `SimRunner` exists but doesn't carry any traffic yet.
- `StateHistory` records but nothing reads it.
- Look for the `===== NETCODE SEAM =====` comment block in `SimRunner.cs` — that's
  where you'll be working.

## Roadmap (in order)

Each milestone has an acceptance criterion. Don't move on without it.

### M1 — `Peer` refactor: two sims in one process (warmup; ~30 min)

The current `Bootstrap` creates ONE `SimRunner` with both players sampled on one
keyboard. That's the warm-up sandbox; it's not the shape netcode wants. Refactor to a
`Peer` abstraction — one peer owns its own `SimRunner`, `GameState`, `History`,
`GameView`, `InputSampler`, and one `ITransport` endpoint. `Bootstrap` creates **two
peers** plus one shared `InProcessNetwork`, with peer A bound to `network.EndpointA`,
peer B to `EndpointB`.

- Each peer samples only its assigned player's input (peer A → `SampleP1`,
  peer B → `SampleP2`) and `Send`s it across its own transport.
- Neither peer consumes the *other's* input into its sim yet — just log received bytes
  to confirm the wire is live. Consumption happens in M2.
- **Cameras:** each peer gets its own `Camera` with
  `cam.rect = new Rect(0, 0, 0.5f, 1f)` (left half, peer A) or
  `new Rect(0.5f, 0, 0.5f, 1f)` (right half, peer B). Each peer's `GameView` spawns its
  objects on a peer-specific Unity layer; that camera's `cullingMask` is restricted to
  that layer. Without layer separation, both cameras render both peers' objects piled
  on top of each other.

Why this isn't throwaway scaffolding: it's the shape M2–M5 want anyway. For M5 you'll
swap `network.EndpointA` for `new UdpTransport(...)` and **nothing else changes**.

**Acceptance:** two peers, side-by-side cameras, each drives its own tank only. The
"other" tank is visibly frozen on each half (because the remote input isn't yet
consumed). Each peer's HUD (or console log) shows it's receiving the peer's input
bytes across the `InProcessNetwork`.

### M2 — Lockstep (the main lift of phase 1)

Now make each peer's sim *consume* the remote input it's been receiving in M1, and
step in lockstep with its peer.

- Per-player input ring buffer keyed by tick, owned by each peer.
- Choose `INPUT_DELAY` (start at 3 ticks ≈ 50 ms; tune later).
- Each tick: sample local input, record it at `currentTick + INPUT_DELAY`, and `Send`
  it. Drain `Network.TryReceive` into the ring.
- Advance the sim only when **both** players' inputs for `state.Tick + 1` are known.
  Otherwise stall this frame; the view re-renders the last good state.
- **Hash piggyback (the on-screen desync alarm).** Extend `InputCodec` to also carry the
  sender's `LastHash` (uint64) for some recent confirmed tick alongside the input. When
  you receive a peer's hash for tick T, compare to your own hash for T — they MUST
  match. Flash the HUD red and dump the offending tick if they ever don't.

**Acceptance — all three layers, no skipping:**

1. **Visual.** Both tanks drive when their assigned keys are pressed; bullets bounce;
   matches end correctly. Both halves of the split-screen look the same (up to a small
   tick offset under latency — see below).
2. **Hash agreement.** Each peer's HUD shows its own `LastHash` AND the peer's most
   recently reported hash. They must match for every confirmed tick. (The piggyback
   is what surfaces this live.)
3. **Stress.** With `Network.LatencyTicks = 12, JitterTicks = 3, LossChance = 0.05f`
   the game stays correct: hashes still match, and lockstep visibly stalls during
   slow/dropped packets. That perceived input lag is exactly what rollback hides in M3.

Expose `Network`'s latency/jitter/loss fields on the HUD as live sliders so you can
twiddle them without restarting.

### M3 — Rollback (the headliner)
- Stop stalling on missing remote input. **Predict** it ("same as last tick" is a fine
  starting policy) and tick immediately.
- Keep both `predictedInputs` and `confirmedInputs` per (tick, player).
- When a remote input arrives and the prediction was wrong:
  - `state.CopyFrom(History.Get(t))` to the confirmed snapshot at the misprediction
    tick `t` (no allocation — that's why `CopyFrom` exists).
  - Re-run `Simulation.Tick` for `t..currentTick` using the corrected inputs.
  - Update the history snapshots along the way.
- Cap the rollback budget (e.g., 12 ticks) — if a misprediction is older than that, the
  connection is too laggy for rollback to hide; fall back to stall.

**Acceptance:** at the same simulated latency as M2, the game feels responsive (you
move "now", not in 50 ms). Hash still matches every confirmed tick. The HUD shows
non-zero rollback frames when you crank loss/jitter up.

### M4 — Make it visible (the fun part)
Suggested overlays — pick what looks coolest:
- RTT (ping-pong probe; piggyback on input packets, or send a tiny probe).
- Current `INPUT_DELAY` and predicted-ahead distance (`localTick - confirmedTick`).
- Rollback counter (per-second; max in last N seconds).
- Frames re-simulated per rollback event (histogram or scrolling text).
- **Predicted vs confirmed ghost** of the remote tank — render a faint outline at the
  confirmed position while the solid tank is at the predicted position. When you
  mispredict, you see the ghost snap visibly.
- Latency / jitter / loss knobs as on-screen sliders (already live fields on `Network`).
- Hash-divergence alarm: if confirmed hashes ever differ, flash the HUD red and dump
  the offending tick's inputs+states to a file.

### M5 — Real UDP transport (when you want to play across two machines)
- New `UdpTransport : ITransport` using `System.Net.Sockets.UdpClient` (Sim/Net are
  pure C# already; no Unity dependency means System.Net Just Works).
- Swap it in behind `ITransport` — the rest of the code doesn't change. The fake
  network stays for repro / tests / single-machine dev.

## High-level pseudocode

Both loops are written from ONE peer's perspective. In the M1 two-peers-in-process
setup, both peers run their own copy of the loop against opposite ends of
`InProcessNetwork`.

### Lockstep tick loop (M2)

```
INPUT_DELAY = 3  # ticks

each Update():
  accumulate Time.deltaTime; while >= 1/TICK_RATE:
    tickToPlay = state.Tick + 1
    inputForFuture = SampleLocal()
    inputs.Record(currentTick + INPUT_DELAY, LocalPlayer, inputForFuture)
    Send( Encode(currentTick + INPUT_DELAY, LocalPlayer, inputForFuture) )

    while Network.TryReceive(pkt):
      (t, who, in) = Decode(pkt)
      inputs.Record(t, who, in)

    if inputs.HasBoth(tickToPlay):
      Simulation.Tick(state, arena, inputs.At(tickToPlay))
      history.Record(state)
      currentTick++
    else:
      break  # stall this frame; the view re-renders last good state
```

### Rollback tick loop (M3)

```
each Update():
  accumulate; while >= 1/TICK_RATE:
    tickToPlay = state.Tick + 1
    localIn = SampleLocal()
    inputs.Record(tickToPlay, LocalPlayer, localIn, confirmed=true)
    Send( Encode(tickToPlay, LocalPlayer, localIn) )

    earliestDirty = uint.MaxValue
    while Network.TryReceive(pkt):
      (t, who, actual) = Decode(pkt)
      if inputs.Predicted(t, who) != actual:
        earliestDirty = min(earliestDirty, t)
      inputs.Record(t, who, actual, confirmed=true)

    # Predict missing remote inputs (naive: "same as last")
    inputs.PredictMissingFor(tickToPlay)

    # If we mispredicted, rewind and re-simulate
    if earliestDirty != uint.MaxValue:
      state.CopyFrom( history.Get(earliestDirty - 1) )   # confirmed-good snapshot
      for t in earliestDirty .. tickToPlay - 1:
        Simulation.Tick(state, arena, inputs.At(t))
        history.Record(state)

    # Advance one new tick using local + predicted remote
    Simulation.Tick(state, arena, inputs.At(tickToPlay))
    history.Record(state)
    currentTick++
```

This is rough — flesh out the bookkeeping (per-player rings, "predicted vs confirmed"
flag per slot, prediction policy) as you go. Keep `Tick` and `Hash` untouched.

## Gotchas and tips

- **Allocation discipline matters under rollback.** A misprediction re-runs many
  `Tick`s; if each allocates, GC spikes. `Tick` itself is allocation-free as written.
  Use `GameState.CopyFrom(snapshot)` (not `state = snapshot.Clone()`) when restoring.
  The `History` already reuses its slots — keep it that way.
- **Floats vs Fixed at the boundary.** Floats are fine for *rendering* (`GameView`
  converts `Fixed.ToFloat()`). They are NOT fine in `Simulation` or anything that
  feeds back into state. Easy to slip; review diffs for this.
- **Don't fix non-determinism by adding a clamp.** If `DeterminismTests` fail, the bug
  is real and will bite you later. Find the root cause.
- **Use the fake network knobs.** Develop with `LatencyTicks: 6, JitterTicks: 3,
  LossChance: 0.05f` — close enough to "real-ish" to catch bugs the clean path hides.
- **Tick number sanity.** `state.Tick` is the tick of the *current* state. The "next"
  tick to play is `state.Tick + 1`. The input you sample at frame F is for some future
  tick (with input delay) or for `state.Tick + 1` (rollback). Pick a convention and
  comment it; the off-by-ones will eat hours otherwise.
- **Prediction policy.** "Same as last tick" is the standard starting policy and is
  surprisingly good. Tank inputs are held buttons (Forward/Left/...) so consecutive
  ticks usually match. Improving the predictor is a knob to tune later, not now.
- **Order of operations under rollback matters.** Make sure when you re-simulate
  `t..currentTick`, the *new* states overwrite the *old* in history, so future rollbacks
  use the correct base.

## Testing

- **`dotnet test "SimTests~/SimTests.csproj"`** — fast, no Unity, run after every change
  to Sim/Net (or anything that links into them). Adding tests as you go is encouraged:
  - A "lockstep through fake network with latency" test would be a nice extension of
    `InputsExchangedOverWireKeepSimsInLockstep` — same loop, but `latencyTicks: 6`
    and only advance the sim once both inputs for a tick are known.
  - A "rollback recovers from misprediction" test: lie about the remote input for a
    few ticks, then deliver the truth, then verify the corrected sim's hash matches a
    reference run that had truth all along.
- **In Unity (M1–M4):** the two-peers-in-one-process setup from M1 is your primary
  runtime validation for everything up through rollback + visualization. Both peers
  live in one editor process talking over `InProcessNetwork` — you see both halves of
  the split-screen at once, and both peers' HUDs (hashes, ticks, rollback counts) are
  right there to compare.
- **In Unity (M5+, separate processes):** install **Multiplayer Play Mode**
  (Window → Package Manager → `com.unity.multiplayer.playmode`). It spawns up to 4
  virtual-player processes that share the project but run independently — they'll talk
  over UDP loopback once `UdpTransport` exists. Validates the real transport path
  without needing a second machine. You'll need a way to tell each instance which
  player it is (commandline arg, environment var, or an MPPM tag).
- **Cross-machine (M5+):** Build And Run on two machines and pass each `--player 0/1`
  plus an IP. Or run a build alongside the editor on the same box with `127.0.0.1`
  and two ports.

## What's deferred to phase 2 (authoritative server)

A short preview, so the choices today make sense:

- One process acts as the **authoritative server** (could be a third process or one of
  the peers serving as host). It runs the same `Simulation` we have now.
- Clients sample local input, send it to the server, and **predict** locally by ticking
  their own copy of the sim with their input + a guess at the remote input
  (or just the local input + "everyone else stays the same"). This is the same
  predict-and-tick we built in M3.
- Server periodically sends authoritative **snapshots** (state at tick T, plus the
  inputs it applied). Clients **reconcile**: `state.CopyFrom(snapshot)`, then replay
  unacknowledged local inputs forward to "now". Same `CopyFrom`-and-replay machinery.
- Remote players are typically **interpolated** from snapshots (lag-buffer for a tick
  or two) rather than predicted from inputs, since you don't have their inputs.
- The same `Hash()` desync canary works: client's post-reconciliation hash for tick T
  must equal server's hash for tick T. If not, your prediction has a bug.

So phase 2 reuses: `Simulation.Tick`, `GameState.Clone`/`CopyFrom`, `History`,
`InputCodec` (you'll add a `SnapshotCodec`), and `ITransport`. New work is the server
process and the snapshot/reconciliation/interpolation loop.

## First 30 minutes of the session

1. Both: read this file. (~10 min)
2. Both: skim `CLAUDE.md` and `Assets/Tanks/Sim/Simulation.cs` + `SimRunner.cs`. (~10 min)
3. Run `dotnet test` from the repo root — confirm 23/23. (~1 min)
4. Open in Unity, press Play, drive the tanks a few seconds. (~2 min)
5. Open `SimRunner.cs` to the `===== NETCODE SEAM =====` block. Start on M1.

Good luck. Have fun with the rollback frames.

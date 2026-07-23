# Netcode Session — Kickoff

> Briefing for both developers. One of you wrote the bootstrap; the other is walking in
> cold. Read this once at the start, then keep `CLAUDE.md` and `README.md` open as references.

## The one-paragraph version

This is a crude *Tanks* (Wii) recreation built **specifically to learn netcode**. The
game itself is already done — a deterministic, locally-playable 1v1 sandbox. **This
session's job is to make it network-playable across two real processes: lockstep first,
then rollback, then a visualization layer that makes the netcode visible on screen.** Two
instances find each other on the network, agree on who's who, and exchange inputs. The
graphics stay crude; the netcode is the point.

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

### Why two separate processes from the start (not "two sims in one process")

An earlier draft of this plan emulated both peers inside one Unity process (split-screen,
two cameras, layer separation) and bolted on the real transport at the very end. We
dropped that. Reasons:

- **No throwaway scaffolding.** The split-screen/layer rig exists *only* to fake two
  peers in one process; running one peer per process makes it unnecessary. We'd rather
  build the real shape once than build emulation now and clean it up later.
- **It matches how netcode is actually developed.** Pros run separate instances (Unreal
  PIE multi-client, Unity Multiplayer Play Mode, or N builds) and degrade a *real*
  network, rather than co-locating peers.
- **The reproducibility we'd lose lives better in tests anyway.** The single-process
  setup's real value was reproducible, side-by-side, single-debugger desync hunting. That
  belongs in `SimTests~` — two sims, a seeded fake network, hash assertions, pure .NET. So
  we keep the rigor there and let the interactive runtime be two honest processes.

The cost — debugging a desync across two live processes is harder than in one — is paid
down by the deterministic test harness: get lockstep/rollback **passing tests** against
`InProcessNetwork`, then trust the live run.

## Lay of the land

```
Assets/Tanks/
  Sim/   Tanks.Sim    — pure deterministic C# (no UnityEngine, no float in logic)
                        Fixed (16.16), Trig (lookup tables), GameState, Simulation.Tick,
                        Arena, Xorshift32
  Net/   Tanks.Net    — also pure C#; ITransport, InProcessNetwork (latency/jitter/loss),
                        InputCodec, and (you'll add) UdpTransport + multicast discovery +
                        tagged messages. Pure System.Net.Sockets — no Unity dependency.
  Game/  Tanks.Game   — thin Unity layer; Bootstrap (entry point, RuntimeInitialize),
                        SimRunner (per-peer shell), GameView (primitives),
                        DebugHud (IMGUI), InputSampler
SimTests~/            — standalone `dotnet test` project linking Sim/Net source.
                        23 tests covering math, gameplay, determinism, network seam.
                        Run with: dotnet test (from repo root or that folder)
```

The two asmdefs `Tanks.Sim` and `Tanks.Net` are marked `noEngineReferences: true`.
**That constraint is load-bearing** — it's what lets `SimTests~` compile the same `.cs`
files under plain .NET, and it's what keeps the simulation deterministic. The same purity
is why `UdpTransport`/discovery (plain `System.Net.Sockets`) can live in `Tanks.Net`. If
you find yourself wanting to reach for `UnityEngine` or `float` inside Sim/Net, stop.

> **Heads-up — the in-Unity compiler is C# 9 / .NET Standard 2.1 / Mono.** `SimTests~`
> targets `net10.0` and will happily compile newer C#, so it's possible to write Sim/Net
> source that passes `dotnet test` but **fails the Unity build**. Keep Sim/Net within C# 9.

## The contract (invariants you cannot break)

1. **Determinism.** `state + inputs → next state` is a pure function. No floats in
   simulation logic (use `Fixed`), no wall-clock time (the sim only knows ticks), no
   `Random.Range` (use `Xorshift32` with seed in `GameState.Rng`), no `UnityEngine` in
   Sim/Net.
2. **`GameState.Hash()` is the desync canary.** If two peers disagree on the hash for the
   same **confirmed** tick, the sim has diverged — fix that *first* before anything else.
   `DeterminismTests` enforce this; never let them regress.
3. **`Tanks.Game` is a thin shell over a pure driver.** It samples input, calls
   `Simulation.Tick`, and renders — never gameplay rules. The netcode loop lives in a
   **pure C# driver** (a plain class, constructor-injected, unit-testable), and the
   MonoBehaviour (`SimRunner`) is a humble object that just forwards `Update()` to it and
   marshals Unity values (`Time.deltaTime`, input, rendering) across. See *Architecture*
   below. Put new netcode code in a pure class (a `Tanks.Netcode` module is fine), talking
   to Sim/Net only through public APIs.
4. **Code-driven setup.** `Bootstrap` builds everything at play time via
   `[RuntimeInitializeOnLoadMethod]`. There are no hand-authored scenes and no inspector
   wiring. Keep it that way — it stays reviewable and version-controllable.
5. **2 players, full stop.** This is a 1v1. Bake `PlayerCount = 2` in; don't carry
   generality for N players. It makes discovery and role assignment trivial.

## Architecture: thin Unity shell over a pure driver

The MonoBehaviours are **humble objects** — they hold no logic, they delegate. The netcode
loop is a plain, Unity-free class you construct and unit-test.

- `Bootstrap` is the **composition root**: it builds the dependency graph by hand (no DI
  container — the graph is tiny and a container fights IL2CPP/lifetimes). It creates the
  pure driver, hands it its dependencies, and attaches a thin `SimRunner` that forwards
  lifecycle calls.
- **Why the local player index is irreducible:** the `PlayerInput[]` handed to `Simulation.Tick` must be
  in canonical *global* order (slot 0 = player 0) **identically on both peers**, or they
  diverge. A "local is always slot 0" convention would desync. So each peer must know its
  *global* index — and in M1 it comes from discovery (below), not a hardcoded flag.

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
  fire; **left trigger / Shift → dash** (3× speed for 6 ticks, 1 s cooldown; edge-triggered
  by the sampler). 60-tick fixed step. `R` or gamepad **Start/Options** resets the match;
  `H` or gamepad **Select** toggles the debug HUD (default hidden).
- `SimRunner` ticks at 60 Hz with frame-rate independence, hashes every state, and
  records snapshots into a 256-tick `StateHistory` ring buffer.
- `InProcessNetwork` — two endpoints, send/receive bytes, tunable
  `LatencyTicks` / `JitterTicks` / `LossChance`. Reproducible (RNG-seeded). **Keep this
  forever** — it's the transport your tests and reproducible debugging run on.
- `InputCodec` — the per-(tick, player, buttons + turret aim) input message.
- A test (`InputsExchangedOverWireKeepSimsInLockstep`) that already proves two
  independent sims exchanging inputs over the fake wire stay bit-identical for 1000
  ticks. **This is the loop you're going to build at runtime — it's literally already
  working in a test, and it's the seed of the M2 test gate.** Read it first.

Explicitly NOT wired (this session's work):
- `SimRunner` currently samples **both** players locally (couch-coop sandbox) inside one
  process. M1 replaces that shape.
- No real transport, no discovery, no role negotiation yet.
- `StateHistory` records but nothing reads it (rollback in M3 will).
- Look for the `===== NETCODE SEAM =====` comment block in `SimRunner.cs` — that's the
  starting point.

## Roadmap (in order)

Each milestone has acceptance criteria. Don't move on without them.

### M1 — Two real peers find each other and exchange inputs (the foundation)

This is the chunky one (discovery + transport + the peer refactor), but there's **no
throwaway work** — it's the real shape from here on. Build, in `Tanks.Net` where possible:

**a. Peer refactor (pure driver + thin shell).** Pull the tick loop out of `SimRunner`
into a pure netcode driver class. `Bootstrap` becomes a composition root that creates
**one** peer (single full-screen camera — no split-screen, no layers) and injects a
`PeerContext`. The MonoBehaviour just forwards `Update()`. **Do the `Bootstrap`/`PeerContext`
wiring _after_ (b)/(c):** its shape is `(what discovery produces) + (what the loop needs)`, so
build the networking first and let the peer wiring consume its output. `Bootstrap` keeps its
current couch-coop wiring until then.

**b. `UdpTransport : ITransport`** using `System.Net.Sockets`. Bind the game socket to
**`IPAddress.Any` : gamePort** — you never need to know your own IP; the OS accepts
packets on any interface, and when you send to the peer it picks your source address. Keep
`InProcessNetwork` behind the same `ITransport` for tests.

**c. Multicast peer discovery + automatic role assignment.** No hardcoded endpoints, no
hardcoded "who is player 1." On boot, nothing starts until the peer is found.

- All peers join a fixed multicast group `G` on a fixed discovery port `D` (admin-scoped,
  e.g. `239.255.x.x`, `TTL = 1`, `SO_REUSEADDR`, multicast-loopback on). One uniform code
  path covers *both* two-instances-on-one-machine and one-instance-on-two-machines —
  multicast is built for exactly that (loopback delivery to other local processes is part
  of the model; `SO_REUSEADDR` for multiple listeners is the *intended* pattern, not a
  hack). This is what mDNS does.
- Each peer periodically **beacons** to `G:D` carrying **only its game port** (a number) —
  *not* its IP. The receiver learns the peer's IP from the **packet's source address**
  (`Receive(ref remoteEP)`), which the OS stamps correctly for whatever path the packet
  took. So the peer's reachable endpoint = **(observed source IP, advertised game port)**.
  This dodges the entire "which of my IPs do I advertise?" problem on localhost/LAN/WLAN.
- **Roles fall out of the network, not a flag.** Assigning distinct roles to two symmetric
  peers is *leader election* — provably impossible to break symmetry without a unique
  distinguishing value. Use the value the network already assigns uniquely: the
  **(IP, port) endpoint**. Rule: the peer with the lexicographically **smaller** endpoint
  is **player 0**. Both peers learn both endpoints from the handshake and compute the same
  answer — no coordinator, no hardcoding. (Deterministic, not random; fine since the two
  slots are symmetric. Want a fair coin-flip instead? Hash the combined endpoints for a
  seed — still purely network-derived.) The result feeds `PeerContext.LocalPlayer`.
- **Manual-IP override — optional, defer until a network forces it.** Not part of M1's
  core. If you ever hit a network that blocks discovery (some guest/public WiFi blocks all
  P2P — multicast *and* unicast), you can bypass discovery by supplying the peer's
  `IP:port` directly. Cheapest form is a launch arg / env var (no UI); a debug text field
  is only a couple of `GUILayout` lines if you want it in-game. It reuses everything else —
  role assignment still falls out of the two endpoints (yours + the one you supplied), so
  this doesn't reopen the identity question. Add it only if a real network needs it.

**d. Exchange inputs, don't consume yet.** Each peer samples only its assigned player's
input and `Send`s it; log received bytes to confirm the wire is live. Consumption is M2,
so the *remote* tank stays frozen (its slot gets `PlayerInput.None`).

**Acceptance:** launch two instances (Multiplayer Play Mode). With nothing hardcoded, they
discover each other, agree on which is player 0 vs 1, and connect. Each window drives only
its own tank (the other is frozen); each logs that it's receiving the peer's input bytes.
Killing/relaunching one re-discovers cleanly. Then try it on two laptops on the same link —
same code, no config.

### M2 — Lockstep (the main lift of phase 1)

Make each peer's sim *consume* the remote input and step in lockstep.

- Per-player input ring buffer keyed by tick, owned by each peer. **Idempotent insert** —
  a `(tick, player)` may arrive late, out of order, or duplicated; the value is immutable,
  so re-inserting is a no-op. Inputs are *never* revised.
- Choose `INPUT_DELAY` (start at 3 ticks ≈ 50 ms; tune later).
- Each tick: sample local input, record it at `currentTick + INPUT_DELAY`, and `Send`
  it. Drain `Transport.TryReceive` into the ring.
- Advance the sim only when **both** players' inputs for `state.Tick + 1` are known.
  Otherwise stall this frame; the view re-renders the last good state.
- **Hash verification is its own message type.** Send a separate, tagged
  `HashMessage { tick, hash }` for your latest **confirmed** tick. Inputs and hashes are
  different concerns — different cadence, different reliability, the hash is a removable
  debug aid — so they're distinct message types over the same transport, never welded into
  one format. (Packing several messages into one datagram — *coalescing* — is a separate,
  transport-level efficiency choice and is fine: a datagram can carry, say, a hash for tick
  T and inputs for tick N. Unrelated messages sharing a datagram is not the same as welding
  two message types into one format. Over `InProcessNetwork` it's moot.) On receipt,
  look up your own stored hash for that tick and compare —
  flash the HUD red and dump the tick if they ever differ. In lockstep there's no
  chicken-and-egg: you never tick on a prediction, so **every executed tick is confirmed**
  and every hash is trustworthy.

**Acceptance (all required; write the test whenever fits your session — it does not have
to come before the implementation):**

1. **Test gate — required.** A deterministic lockstep test in `SimTests~`: two sims, each
   generating its own player's inputs from a **seed**, exchanging over `InProcessNetwork`
   *with latency/jitter/loss*, advancing only when both inputs for a tick are known —
   stays bit-identical (`A.Hash() == B.Hash()`) every confirmed tick for N ticks. (This
   tests the **buffering/lockstep plumbing**, not the sim matching itself — an off-by-one
   in the ring would diverge the two peers. It's the extension of
   `InputsExchangedOverWireKeepSimsInLockstep`.)
2. **Visual.** Both tanks drive when their keys are pressed; bullets bounce; matches end
   correctly. Both windows agree (up to a small tick offset under latency).
3. **Hash agreement.** Each peer's HUD shows its own latest-confirmed hash AND the peer's;
   they match every confirmed tick.
4. **Stress.** With `LatencyTicks = 12, JitterTicks = 3, LossChance = 0.05f` the game stays
   correct: hashes still match, and lockstep visibly stalls during slow/dropped packets.
   That perceived input lag is exactly what rollback hides in M3.

Expose latency/jitter/loss as live HUD sliders (on `InProcessNetwork` in tests; for the
UDP path, a local artificial-delay wrapper) so you can twiddle without restarting.

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
- **Rollback is local.** A misprediction corrects *your own* sim from inputs you already
  received; you send nothing back. The peer independently rolls back using *your* inputs.
- **Hash verification under prediction.** Now the *current* tick's hash is computed from
  guesses and is meaningless to compare — so only ever report/compare the hash of your
  **latest confirmed** tick (it lags the current tick by the prediction window). That's why
  `HashMessage` carries a *confirmed* tick, not the bleeding edge.

**Acceptance (all required; test in any order):**

1. **Test gate — required.** A rollback-recovery test in `SimTests~`: feed a *wrong*
   predicted remote input for a few ticks, deliver the truth, trigger the rollback, and
   assert the post-rollback hash **equals a reference run that had the truth from the
   start**. Proves rollback reconstructs the true timeline.
2. At the same simulated latency as M2, the game feels responsive (you move "now", not in
   50 ms). Hash still matches every confirmed tick. The HUD shows non-zero rollback frames
   when you crank loss/jitter up.

### M4 — Make it visible (the fun part)
Per-process overlays — pick what looks coolest:
- RTT (ping-pong probe; its own tagged message, optionally coalesced into a datagram you're
  already sending).
- Current `INPUT_DELAY` and predicted-ahead distance (`localTick - confirmedTick`).
- Rollback counter (per-second; max in last N seconds).
- Frames re-simulated per rollback event (histogram or scrolling text).
- **Predicted vs confirmed ghost** of the remote tank — render a faint outline at the
  confirmed position while the solid tank is at the predicted position. When you
  mispredict, you see the ghost snap visibly.
- Latency / jitter / loss knobs as on-screen sliders.
- Hash-divergence alarm: if confirmed hashes ever differ, flash the HUD red and dump
  the offending tick's inputs+states to a file.

## High-level pseudocode

Each process runs **one** peer. The loop lives in the pure driver; `SimRunner.Update()`
just forwards `driver.Advance(Time.deltaTime)`. The transport is `UdpTransport` at runtime,
`InProcessNetwork` in tests — both behind `ITransport`.

### Lockstep tick loop (M2)

```
INPUT_DELAY = 3  # ticks

each Advance(dt):
  accumulate dt; while >= 1/TICK_RATE:
    tickToPlay = state.Tick + 1
    inputForFuture = SampleLocal()
    inputs.Record(currentTick + INPUT_DELAY, LocalPlayer, inputForFuture)
    Send( InputMessage(currentTick + INPUT_DELAY, LocalPlayer, inputForFuture) )
    Send( HashMessage(latestConfirmedTick, HashAt(latestConfirmedTick)) )   # decoupled

    while Transport.TryReceive(pkt):
      switch pkt.kind:
        Input: (t, who, in) = Decode(pkt); inputs.Record(t, who, in)   # idempotent
        Hash:  (t, h) = Decode(pkt); if HashAt(t) != h: RaiseDesyncAlarm(t)

    if inputs.HasBoth(tickToPlay):
      Simulation.Tick(state, arena, inputs.At(tickToPlay))
      history.Record(state)
      currentTick++                       # every executed tick is CONFIRMED here
    else:
      break  # stall this frame; the view re-renders last good state
```

### Rollback tick loop (M3)

```
each Advance(dt):
  accumulate dt; while >= 1/TICK_RATE:
    tickToPlay = state.Tick + 1
    localIn = SampleLocal()
    inputs.Record(tickToPlay, LocalPlayer, localIn, confirmed=true)
    Send( InputMessage(tickToPlay, LocalPlayer, localIn) )
    Send( HashMessage(latestConfirmedTick, HashAt(latestConfirmedTick)) )

    earliestDirty = uint.MaxValue
    while Transport.TryReceive(pkt):
      if pkt.kind == Input:
        (t, who, actual) = Decode(pkt)
        if inputs.Predicted(t, who) != actual:
          earliestDirty = min(earliestDirty, t)
        inputs.Record(t, who, actual, confirmed=true)
      elif pkt.kind == Hash:
        (t, h) = Decode(pkt); if HashAt(t) != h: RaiseDesyncAlarm(t)

    inputs.PredictMissingFor(tickToPlay)        # naive: "same as last"

    if earliestDirty != uint.MaxValue:          # mispredicted → rewind & re-sim (LOCAL only)
      state.CopyFrom( history.Get(earliestDirty - 1) )
      for t in earliestDirty .. tickToPlay - 1:
        Simulation.Tick(state, arena, inputs.At(t))
        history.Record(state)                   # new states overwrite old

    Simulation.Tick(state, arena, inputs.At(tickToPlay))   # advance with local + predicted
    history.Record(state)
    currentTick++
```

This is rough — flesh out the bookkeeping (per-player rings, "predicted vs confirmed" flag
per slot, latest-confirmed-tick tracking, prediction policy) as you go. Keep `Tick` and
`Hash` untouched.

## Gotchas and tips

- **Threading boundary at the UDP edge.** A real `UdpClient` receive may land on a
  **background thread**. Don't make the sim thread-safe — confine threading to the socket:
  the receive thread enqueues bytes into a `ConcurrentQueue`, and `Advance()` **drains it on
  the main thread**. The sim stays single-threaded and lock-free (which also helps
  determinism). The in-process transport has no threads, so this only matters for
  `UdpTransport`. (You can also do non-blocking polling receives entirely on the main
  thread and skip threads altogether.)
- **You never need your own IP.** Bind game receive to `IPAddress.Any:gamePort`; send to the
  peer's endpoint and let the OS pick your source. Advertise only your *port*; the peer
  reads your *IP* from the packet source. Works identically on localhost/LAN/WLAN.
- **Inputs are immutable; predictions are not.** A peer never sends a revised input for a
  past tick. Rollback corrects your *local prediction* of their input, locally. So design
  for "late/duplicate/out-of-order arrival," never for "the value changed."
- **Hash only confirmed ticks.** Lockstep: every executed tick is confirmed (no
  prediction). Rollback: report the latest *confirmed* tick, which lags the current tick.
- **Allocation discipline under rollback.** A misprediction re-runs many `Tick`s; if each
  allocates, GC spikes. `Tick` is allocation-free; use `GameState.CopyFrom(snapshot)` (not
  `state = snapshot.Clone()`) when restoring. `History` reuses slots — keep it that way.
  (Also: make sure `CopyFrom` is a real deep copy, or a stored snapshot aliases live state.)
- **Floats vs Fixed at the boundary.** Floats are fine for *rendering* (`GameView` converts
  `Fixed.ToFloat()`) and for *local input sampling before quantizing*. They are NOT fine in
  `Simulation` or anything that feeds back into state or crosses the wire.
- **Don't fix non-determinism by adding a clamp.** If `DeterminismTests` fail, the bug is
  real and will bite you later. Find the root cause.
- **Tick number sanity.** `state.Tick` is the tick of the *current* state; the "next" tick
  is `state.Tick + 1`. The input you sample is for a future tick (with input delay) or for
  `state.Tick + 1` (rollback). Pick a convention and comment it; off-by-ones eat hours.
- **Prediction policy.** "Same as last tick" is the standard start and is surprisingly good
  — tank inputs are held buttons, so consecutive ticks usually match. Tune later.
- **Order of operations under rollback.** When you re-simulate `t..currentTick`, the *new*
  states must overwrite the *old* in history, so future rollbacks use the correct base.

## Testing

- **`dotnet test "SimTests~/SimTests.csproj"`** — fast, no Unity, run after every change to
  Sim/Net. This is where the **required** lockstep and rollback test gates live (see M2/M3).
  It's also the reproducible, single-debugger home for chasing any desync you see live —
  reproduce it here against `InProcessNetwork` with a fixed seed, then fix it.
- **Primary dev loop: Multiplayer Play Mode** (Window → Package Manager →
  `com.unity.multiplayer.playmode`). Spawns virtual-player processes that share the project
  and run independently — fast edit/reload across all instances, no separate build step.
  This is the everyday loop for M1–M4. (With discovery in place, instances self-pair; no
  per-instance config needed.)
- **Optional validation: build alongside the editor.** Strictly optional. Run a standalone
  **build** (IL2CPP) next to the editor (Mono) on the same machine. Because the two sides
  run *different runtimes*, matching hashes are a free **cross-runtime determinism check**
  (Mono vs IL2CPP) and validate the real player runtime. Use it occasionally, not daily.
- **Cross-machine:** with discovery, just run a build on each of two laptops on the same
  link — they find each other. (Direct ethernet is the most reliable; normal WiFi is fine;
  locked-down guest WiFi may block P2P entirely — use the manual-IP fallback or another
  network.)

## What's deferred to phase 2 (authoritative server)

A short preview, so the choices today make sense:

- One process acts as the **authoritative server** (a third process, or one peer hosting).
  It runs the same `Simulation` we have now.
- Clients sample local input, send it to the server, and **predict** locally by ticking
  their own copy of the sim with their input + a guess at the remote input. This is the
  same predict-and-tick we built in M3.
- Server periodically sends authoritative **snapshots** (state at tick T, plus the inputs
  it applied). Clients **reconcile**: `state.CopyFrom(snapshot)`, then replay unacknowledged
  local inputs forward to "now". Same `CopyFrom`-and-replay machinery.
- Remote players are typically **interpolated** from snapshots rather than predicted from
  inputs, since you don't have their inputs.
- The same `Hash()` desync canary works: client's post-reconciliation hash for tick T must
  equal the server's hash for tick T. If not, your prediction has a bug.

So phase 2 reuses: `Simulation.Tick`, `GameState.Clone`/`CopyFrom`, `History`, `InputCodec`
(you'll add a `SnapshotCodec`), `ITransport`, and the tagged-message envelope. New work is
the server process and the snapshot/reconciliation/interpolation loop.

## First 30 minutes of the session

1. Both: read this file. (~10 min)
2. Both: skim `CLAUDE.md` and `Assets/Tanks/Sim/Simulation.cs` + `SimRunner.cs`. (~10 min)
3. Run `dotnet test` from the repo root — confirm 23/23. (~1 min)
4. Open in Unity, press Play, drive the tanks a few seconds (current couch-coop sandbox). (~2 min)
5. Read `InputsExchangedOverWireKeepSimsInLockstep` — it's the M2 test gate in miniature.
6. Open `SimRunner.cs` to the `===== NETCODE SEAM =====` block. Start on M1 (the pure-driver
   refactor first, then transport, then discovery).

Good luck. Have fun with the rollback frames.

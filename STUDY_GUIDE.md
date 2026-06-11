# Tanks2 — Code Tour (re-onboarding in ~20 minutes)

> Companion docs: **`BRIEFING.md`** explains *why* the project is shaped this way and defines
> the milestones. **`SESSION_PLAN.md`** is the step-by-step execution plan for M1/M2.
> This file rebuilds your mental model of the code itself. Read top to bottom once;
> after that you only need §3 and §8.

## 0. The 30-second version

- The game is a **pure deterministic C# simulation** (`Tanks.Sim`) — integer math only,
  no Unity, no clocks. `Simulation.Tick(state, arena, inputs)` *is* the entire game.
- `GameState.Hash()` is the truth signal: two machines running the same inputs must
  produce the same hash every tick. Everything in the netcode plan reduces to keeping
  that true while inputs travel over a wire.
- `Tanks.Net` (also pure C#) has the wire format and a fake in-process network with
  latency/jitter/loss knobs. **It is built but carries no traffic yet.**
- `Tanks.Game` is the Unity shell: sample input → tick → draw cubes.
- Today one `SimRunner` samples *both* players on one machine (couch co-op sandbox).
  The session turns that into two peers exchanging inputs: M1 wiring → M2 lockstep →
  M3 rollback.

## 1. The map

```
            Tanks.Game (Unity allowed)          Tanks.Net (pure)            Tanks.Sim (pure)
            ──────────────────────────          ────────────────            ────────────────
 main() →   Bootstrap                           ITransport                  Simulation.Tick()
            SimRunner   ← NETCODE SEAM    →     InProcessNetwork            GameState (+Hash/Clone/CopyFrom)
            GameView (render only)              InputCodec (8 bytes)        Fixed (16.16) · Trig (2048 baked)
            DebugHud (IMGUI)                                                Arena · SimConfig · PlayerInput
            InputSampler (devices → ints)                                   Xorshift32
            StateHistory (256-tick ring)

 SimTests~  — plain `dotnet test` project that compiles the SAME Sim/Net .cs files. 29 tests, ~seconds.
```

Dependencies point left-to-right only. `Tanks.Sim` and `Tanks.Net` have
`noEngineReferences: true` in their asmdefs — that constraint is load-bearing: it's what
lets the tests run without Unity and what keeps the sim deterministic.

## 2. One frame, today (the loop you'll be rewiring)

1. `SimRunner.Update()` — checks `R`/Start for reset, adds `Time.deltaTime` to an
   accumulator, and runs up to 5 fixed steps of 1/60 s each (catch-up cap so a hitch
   doesn't death-spiral).
2. `SimRunner.StepOnce()` — samples **both** players via `InputSampler.SampleP1/P2()`,
   calls `Simulation.Tick`, stores `LastHash = State.Hash()`, records a snapshot into
   `History`.
3. `GameView.LateUpdate()` — reads `runner.State` and positions the primitives.
   Floats are fine here (render only — they never feed back into the sim).
4. `DebugHud.OnGUI()` — IMGUI overlay; `H` or gamepad Select toggles it.

What is *not* happening: `SimRunner.Network` is constructed in `Awake` and never touched
again. `StateHistory` records but nothing reads it. And note — **nothing anywhere calls
`Network.Poll()`**, and the fake network only moves packets into inboxes during `Poll`.
First thing M1 has to add, or the wire stays silent.

## 3. The six files that matter (read in this order)

1. **`Assets/Tanks/Sim/Simulation.cs`** — `Tick` = `UpdateTanks` → `UpdateBullets` →
   `state.Tick++`. Movement is 8-way in world space (direction bits → ±X/±Y, diagonals
   scaled by 1/√2, body angle snaps to the input octant). Turret angle is adopted
   verbatim from input. Dash = 3× speed for 9 ticks, gated by a 15-tick cooldown.
   Fire = gated by 12-tick cooldown and a ≤5-live-shells-per-player cap. Bullets fly
   along the *turret* angle, reflect off bounds/walls once, and detonate on the second
   surface contact; a shell can't hurt its owner until it has ricocheted.
2. **`Assets/Tanks/Sim/GameState.cs`** — plain arrays of `Tank`/`Bullet` structs plus
   `Tick` and `Rng`. Three methods to internalize: `Clone()` (allocating deep copy, used
   by history), `CopyFrom()` (allocation-free restore — this is *the* rollback primitive),
   `Hash()` (FNV-1a over every field, **including `Tick`** — so comparing hashes from
   different ticks always mismatches; compare like ticks only).
3. **`Assets/Tanks/Game/SimRunner.cs`** — everything in §2, ~90 lines. The
   `===== NETCODE SEAM =====` comment marks where the session's work goes.
4. **`Assets/Tanks/Net/InProcessNetwork.cs`** — one shared object exposing two crossed
   `ITransport` endpoints (`EndpointA` ↔ `EndpointB`; internally `to = from ^ 1`).
   Tick-driven: `Send` queues a packet with `DeliverAt = CurrentTick + latency ± jitter`;
   `Poll(tick)` advances the clock and moves due packets to inboxes; `TryReceive` drains.
   Loss/jitter use a seeded `Xorshift32`, so runs are reproducible. `LatencyTicks`,
   `JitterTicks`, `LossChance` are public mutable fields — designed to become HUD sliders.
5. **`Assets/Tanks/Net/InputCodec.cs`** — the wire format, 8 bytes: tick `u32` LE,
   player `u8`, buttons `u8`, turret aim `u16` LE. M2 extends this with a hash piggyback.
6. **`SimTests~/NetworkTests.cs` → `InputsExchangedOverWireKeepSimsInLockstep`** — two
   independent sims, each owning one player, exchange inputs over the fake wire and stay
   bit-identical for 1000 ticks. **This is M2 in miniature** (minus latency, input delay,
   and stalling). The session is essentially porting this loop into `SimRunner` and
   hardening it. The briefing says read it first; it's right.

## 4. Supporting cast (skim now, reference later)

| File | What to remember |
|---|---|
| `Sim/Fixed.cs` | 16.16 fixed-point in one `int` (`Raw`). Mul/div widen to `long`. `FromFloat` is for constants/config only — never per-tick. `Sqrt` = integer Newton. |
| `Sim/Trig.cs` | 2048-step circle. Sin table **baked as integer literals** so x86_64 and ARM64 agree bit-for-bit. `Cos(a) = Sin(a + 512)`. `Direction(angle)` → unit `FixVec2`. `Normalize` = `& 2047`. |
| `Sim/FixVec2.cs` | Tiny fixed-point vector. |
| `Sim/Arena.cs` | 32×20 field, 3 AABB pillars, `OverlapsSquare`/`ContainsPoint`. Bounds handled implicitly by clamp/reflect. |
| `Sim/SimConfig.cs` | **All tuning constants, in per-tick units.** When a doc and this file disagree, this file wins. |
| `Sim/PlayerInput.cs` | 6 button flags in a byte + 11-bit absolute `TurretAim`. This struct *is* the wire payload. |
| `Sim/Xorshift32.cs` | Seeded PRNG. The sim reserves `GameState.Rng` (unused so far); the fake network uses its own instance. |
| `Game/InputSampler.cs` | Static. Devices → quantized ints: float math (stick atan2, etc.) happens locally, then aim is quantized to the 2048-step circle and dash is edge-triggered, so the sim/wire never see floats. P1 = gamepad 0 else WASD; P2 = gamepad 1 else arrows. Keeps per-player turret memory in statics. |
| `Game/GameView.cs` | Builds primitives in `Start`, snaps them to state in `LateUpdate`. `Fixed.ToFloat()` happens only here. |
| `Game/DebugHud.cs` | IMGUI panel reading its sibling `SimRunner`. Default hidden. |
| `Game/StateHistory.cs` | 256-snapshot ring keyed `tick % 256` with an exact-tick check on `Get`. `Record` clones once per slot then `CopyFrom`s — allocation-free at steady state. Currently write-only; rollback (M3) is its reader. Note: pure C# despite living in the Game assembly. |
| `Game/Bootstrap.cs` | `[RuntimeInitializeOnLoadMethod]` static method = the `main()`. Builds camera, light, and one "Tanks" GameObject carrying SimRunner+GameView+DebugHud. No scene content, ever. |

## 5. Unity-isms cheat sheet (for the C#-native reader)

- A **MonoBehaviour** is a component you attach to a `GameObject` with
  `go.AddComponent<T>()` — you never `new` it. Unity calls its magic methods.
- Lifecycle order: `Awake` (runs *during* `AddComponent`) → `Start` (before the object's
  first frame) → every frame `Update` → `LateUpdate` → `OnGUI` (possibly several times a
  frame). Consequence Bootstrap relies on: set public fields right after `AddComponent`
  and they're visible in `Start`/`Update`, but **not** in `Awake`.
- `[RuntimeInitializeOnLoadMethod]` = "call this static method when Play starts" — how
  this repo avoids scenes entirely.
- `Time.deltaTime` = wall-clock seconds since last frame; the accumulator pattern in
  `SimRunner.Update` converts variable frame rate into exact 60 Hz sim steps.
- **Layers**: each GameObject has `go.layer` (an int 0–31). It is **not inherited** by
  children created at runtime — set it on every object. A camera renders only layers in
  its `cullingMask` bitmask (`1 << layer`). Layers 8+ are free; unnamed layers work fine
  from code. (This is how M1 keeps the two peers' overlapping worlds visually separate.)
- `camera.rect` = normalized viewport rectangle → split-screen. Read `camera.aspect`
  only *after* setting `rect`, since the rect changes it.
- `OnGUI`/IMGUI draws in **screen space** — it ignores cameras and layers, so two HUDs
  must be given different screen rectangles by hand.
- `[DefaultExecutionOrder(-100)]` on a class makes its `Update` run before others' —
  ordering without Editor settings (used for the network pump in M1).

## 6. The contract (memorize; everything else is negotiable)

1. No `UnityEngine`, no `float`/`double`, no wall-clock, no I/O inside Sim/Net logic.
2. `GameState.Hash()` agreement per confirmed tick is **the definition of working**.
   Any mismatch is a real bug — fix it first, never paper over it (no clamps).
3. `Tanks.Game` stays a view; netcode may live in Game but talks to Sim/Net only
   through public APIs.
4. All setup stays code-driven (no scenes, no inspector wiring).

## 7. Doc drift — trust code over prose

Found while preparing this tour (2026-06-11):

- BRIEFING says "23 tests" → actually **29/29 passing** (verified, 32 ms).
- BRIEFING says dash is "3× for 6 ticks, 1 s cooldown" → `SimConfig` says **9 ticks
  (150 ms), 15-tick cooldown (250 ms)**; the `~100 ms` comment in SimConfig is stale
  too. The 3× multiplier is correct.
- README's roadmap items 4/5/6 = BRIEFING's M2/M3/M4. The M1 peer refactor exists only
  in BRIEFING.

## 8. Numbers to hold in your head

60 Hz tick · 2 players · arena 32×20 · tank radius 0.6, speed 0.12/tick · bullet speed
0.2/tick, 1 bounce, ≤5 live/player, 12-tick fire cd · turret = 2048 angle steps ·
history = 256 ticks ≈ 4.3 s · input packet = 8 bytes (→ 20 in M2) · INPUT_DELAY = 3
(M2 starting value) · max 5 catch-up sim steps per rendered frame.

## 9. 15 minutes before the meeting

- [ ] `cd SimTests~ && dotnet test` → expect **29/29**.
- [ ] Skim `Simulation.cs` and `SimRunner.cs` (~8 min).
- [ ] Read `InputsExchangedOverWireKeepSimsInLockstep` carefully (~3 min).
- [ ] Unity → Play: drive both tanks, `H` for HUD, `R` to reset (~2 min).
- [ ] Open `SESSION_PLAN.md`, start at M1.

Controls: P1 WASD + Q/E turret + Space fire + LShift dash · P2 arrows + ,/. turret +
Enter fire + RShift dash · gamepads auto-bind if connected · R reset · H HUD.

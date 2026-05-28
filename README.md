# Tanks2

A crude recreation of the Wii game *Tanks* as an **online 1v1**, built to learn netcode
(deterministic lockstep → rollback, with client-server reachable later). Graphics are
intentionally minimal — primitives and a debug HUD. The interesting part is the netcode.

## The big idea

The game is a **pure, deterministic C# simulation** with zero Unity dependencies. Unity is
just a thin "view": it samples input, calls the sim, and draws the result with primitives.

```
state + inputs  ──Simulation.Tick()──▶  next state     (pure, integer math, no Unity, no wall-clock)
```

Because the sim is deterministic (fixed-point math, lookup-table trig, no floats), the same
inputs produce **bit-identical** state on every machine. That property is the entire basis
for lockstep and rollback:

- **lockstep** — both peers feed the sim the same inputs each tick
- **rollback** — when a predicted remote input was wrong, restore an old snapshot and re-run `Tick`

## Layout

| Folder | Assembly | Unity? | Purpose |
|---|---|---|---|
| `Assets/Tanks/Sim` | `Tanks.Sim` | no (`noEngineReferences`) | the entire game: `Fixed` math, `Trig`, `GameState`, `Simulation.Tick`, `Arena` |
| `Assets/Tanks/Net` | `Tanks.Net` | no | `ITransport`, `InProcessNetwork` (fake net w/ latency/jitter/loss), `InputCodec` (wire format) |
| `Assets/Tanks/Game` | `Tanks.Game` | yes | `Bootstrap` (entry point), `SimRunner` (tick loop + history), `GameView` (rendering), `DebugHud`, `InputSampler` |
| `SimTests~` | — | no | standalone `dotnet test` project; links the Sim/Net source for a fast CLI feedback loop |

> `SimTests~` ends in `~` so Unity ignores it. It compiles the *same* `.cs` files the game
> runs, which is only possible because Sim/Net are pure C#.

## Run the game

1. Open **Unity Hub → Add → Add project from disk** and select this folder
   (`...\harrisonogle\Tanks2`). It's already configured for editor **6000.4.8f1**.
2. Open it, wait for import, then press **Play**. Everything is created from code by
   `Bootstrap` (see `Assets/Tanks/Game/Bootstrap.cs`) — there is no scene to set up.

### Controls (local 2-player sandbox)

Movement is 8-way in world space (faithful to original *Tanks* — not tank-controls).
The body visually rotates to face the input direction; the turret aims independently.

**Gamepad** (Xbox or PlayStation, auto-detected when connected): left stick to drive,
right stick to aim the turret, A/Cross or right trigger to fire. P1 binds to the first
connected gamepad, P2 to the second.

**Keyboard** (fallback for whichever slot has no gamepad):

- **P1 (blue):** `W`/`A`/`S`/`D` move (8 directions), `Q`/`E` rotate turret, `Space` fire
- **P2 (red):** Arrow keys move (8 directions), `,` / `.` rotate turret, `Enter` fire

**`R`** (keyboard) or **Start / Options** (gamepad): reset the match

## Run the tests (no Unity needed)

```
cd SimTests~
dotnet test
```

These verify the math, gameplay rules, and — most importantly — **determinism** and the
**network seam** (two sims exchanging inputs over the fake wire stay bit-identical).

## Roadmap

- [x] **0–3 (done):** deterministic sim, locally-playable 2-tank sandbox, code-driven
      rendering, debug HUD, transport interface + in-process fake network, determinism tests.
- [ ] **4 — lockstep:** sample only the local player; send/receive inputs over `Network`;
      advance with a small input delay once both inputs for a tick are known.
- [ ] **5 — rollback:** predict the remote input, tick immediately, and on a misprediction
      restore `SimRunner.History.Get(t)` and re-tick to now.
- [ ] **6 — make it visible:** overlay RTT, input delay, rollback count/frames re-simulated,
      predicted-vs-confirmed ghosts, and live latency/jitter/loss knobs (already on `InProcessNetwork`).
- [ ] **later:** real UDP transport behind `ITransport`; client-server authoritative mode.

The netcode work starts at the clearly-marked **NETCODE SEAM** in
`Assets/Tanks/Game/SimRunner.cs`.

## Notes

- Built-in render pipeline, legacy Input — chosen for zero setup. Both are easy to swap.
- IntelliSense: Unity will generate a solution for Visual Studio (already installed). If you
  prefer Rider/VS Code, add the matching IDE package via **Window → Package Manager**.

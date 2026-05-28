# Tanks2 — notes for Claude

Crude Unity recreation of *Tanks* as an online 1v1. **The point is netcode learning**
(deterministic lockstep → rollback; client-server later), not graphics. Keep things simple.

## Hard architectural rules

- **`Tanks.Sim` and `Tanks.Net` must stay pure C#** — no `UnityEngine`, no `float`/`double`
  in simulation logic, no wall-clock time, no I/O. Both asmdefs set `"noEngineReferences": true`.
  This is what lets `SimTests~` compile the same source under plain .NET, and what keeps the
  sim deterministic. If you reach for `UnityEngine` or `float` inside the sim, stop.
- **Determinism is the product.** All sim math goes through `Fixed` (16.16) and `Trig` (lookup
  tables). The canonical correctness signal is `GameState.Hash()` — two machines must agree on
  it per tick. The `DeterminismTests` are the canary; never let them regress.
- **Same sim behavior on x86_64 AND ARM64.** Cross-architecture netcode is a goal (e.g., a
  Windows PC playing against an Apple Silicon Mac). Anything that crosses the wire must be
  bit-identical across architectures — int math is; `Math.Sin` / `Math.Cos` aren't guaranteed
  to be in their last bits. The `Trig` table is baked as integer constants in source (see
  `Trig.cs`) for that reason; if you add another precomputed lookup table for the sim, bake
  it the same way (don't compute from a math-library call at static init).
- **`Tanks.Game` is a thin view.** It samples input, calls `Simulation.Tick`, and renders
  primitives. It never contains gameplay rules.
- **Code-driven setup, no hand-authored scenes.** `Bootstrap` ([RuntimeInitializeOnLoadMethod])
  builds the camera/light/objects at play time. Don't add scene files or inspector wiring;
  keep setup in code so it's reviewable and so the user avoids Editor fiddling.

## Where things are

- Entry point: `Assets/Tanks/Game/Bootstrap.cs` (think `main()`).
- Tick loop + rollback history + the **NETCODE SEAM**: `Assets/Tanks/Game/SimRunner.cs`.
- The whole game: `Assets/Tanks/Sim/Simulation.cs` (+ `GameState`, `Arena`, `SimConfig`).
- Wire format / fake network: `Assets/Tanks/Net/`.

## How to verify changes

- **Sim/Net logic (fast, do this often):**
  `dotnet test "SimTests~/SimTests.csproj"`  — pure C#, no Unity, ~seconds.
- **Unity compiles (the `Game` layer):** batch-mode import/compile, then check for `error CS`
  and that `Library/ScriptAssemblies/Tanks.*.dll` were produced:
  ```
  & "C:\Program Files\Unity\Hub\Editor\6000.4.8f1\Editor\Unity.exe" `
    -batchmode -quit -nographics -accept-apiupdate `
    -projectPath "<repo>" -logFile "<repo>\unity_import.log"
  ```
  (I can't press Play or see rendering — rely on the user for runtime/visual feedback.)

## Environment

- Unity **6000.4.8f1**, built-in render pipeline, legacy Input (`activeInputHandler: 0`).
- .NET SDK 10 present; `SimTests~` targets `net10.0` and links Sim/Net source via `<Compile Include>`.
- User: strong C#/systems/networking background, newer to Unity. Explain Unity-isms, not C#.
- User prefers questions asked **inline in prose**, never via the AskUserQuestion popup.

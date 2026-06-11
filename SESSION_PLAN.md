# Session Plan — M1 & M2, step by step

> Acceptance criteria are quoted from `BRIEFING.md`; this file adds the code-level steps,
> sketches, and the traps that aren't written down anywhere. If the meeting stalls, find
> your symptom in §6. Read `STUDY_GUIDE.md` first if the codebase isn't fresh.

**Shape of the session:** M1 wiring (~45 min) → M2 lockstep (~2–2.5 h) → stretch into M3.
Verify with `dotnet test` after every Sim/Net change (29/29 green today), and with
Play-mode eyeballs for the Game layer.

---

## 1. Suggested division of labor

The two tracks touch disjoint files, so you can genuinely parallelize after a shared
10-minute kickoff:

- **Track A — Unity side (all of M1):** `Bootstrap`, new `NetworkPump`, `GameView`
  layers, `DebugHud` split. Output: two live peers, side-by-side, wire visibly carrying
  bytes.
- **Track B — pure C# side (M2 core):** `InputCodec` v2, new `InputRing`, and the
  headless lockstep-under-latency test, all under `dotnet test` with no Unity open.
  Output: the M2 loop proven correct before it ever touches a MonoBehaviour.
- **Merge (pair):** wire Track B's pieces into `SimRunner` (~30–45 min), then stress.

If you'd rather stay together, do M1 → M2 in order; the plan below reads that way.

---

## 2. M1 — Peer refactor: two sims in one process (~45 min)

**Acceptance (BRIEFING):** two peers, side-by-side cameras, each drives its own tank
only; the other tank is visibly frozen on each half; each peer's HUD/log shows it is
receiving the peer's input bytes across the `InProcessNetwork`.

**Design call (so you don't debate it live):** a "Peer" doesn't need a new class — it's
a GameObject carrying the existing trio (`SimRunner` + `GameView` + `DebugHud`) with
fields injected by `Bootstrap`. The components already find each other with
`GetComponent` on the same GameObject, so that pattern survives unchanged. The briefing
lists `InputSampler` as peer-owned; it's a static class today — "ownership" just means
each peer calls only its own `SampleP1` *or* `SampleP2`. Don't instance-ify it now.

### Step 1 — `SimRunner`: own one player and one transport

- Delete `Network = new InProcessNetwork()` from `Awake` (Bootstrap owns the shared
  network now). Replace the `Network` property with injected fields.
- Sample only the local player; send every sampled input; drain and count what arrives.

```csharp
public int LocalPlayer;        // 0 or 1 — set by Bootstrap right after AddComponent
public ITransport Transport;   // peer A gets net.EndpointA, peer B gets net.EndpointB

// M1 diagnostics (the acceptance criterion "wire is live" reads these)
public int PacketsReceived { get; private set; }
public uint LastRxTick { get; private set; }
public PlayerInput LastRxInput { get; private set; }

private void Update()
{
    if (InputSampler.IsResetRequested()) ResetMatch();
    DrainNetwork();                            // before stepping — the shape M2 wants
    /* accumulator loop unchanged */
}

private void DrainNetwork()
{
    while (Transport.TryReceive(out var pkt))
    {
        InputCodec.Read(pkt, out uint tick, out int player, out PlayerInput input);
        PacketsReceived++; LastRxTick = tick; LastRxInput = input;
        // M2: ring.Record(tick, player, input) goes here. M1: just observe.
    }
}

private void StepOnce()
{
    PlayerInput local = LocalPlayer == 0 ? InputSampler.SampleP1() : InputSampler.SampleP2();
    _inputs[LocalPlayer] = local;
    _inputs[1 - LocalPlayer] = PlayerInput.None;   // remote tank frozen until M2
    Transport.Send(InputCodec.ToBytes(State.Tick + 1, LocalPlayer, local));

    Simulation.Tick(State, Arena, _inputs);
    LastHash = State.Hash();
    History.Record(State);
}
```

### Step 2 — `NetworkPump` (new ~20-line MonoBehaviour) — **without this, zero packets ever deliver**

`InProcessNetwork` only moves packets on `Poll(tick)`, and nothing calls it today. Give
the network its own free-running 60 Hz clock. Crucially this clock is **not** either
sim's tick — in M2 the sims will stall while waiting for input, but the network must
keep delivering (real networks don't pause when your game loop does).

```csharp
[DefaultExecutionOrder(-100)]   // poll before any SimRunner drains, every frame
public sealed class NetworkPump : MonoBehaviour
{
    public InProcessNetwork Network;
    private double _acc;
    private uint _netTick;

    private void Update()
    {
        double step = 1.0 / SimConfig.TickRate;
        _acc += Time.deltaTime;
        while (_acc >= step) { Network.Poll(++_netTick); _acc -= step; }
    }
}
```

### Step 3 — `GameView`: one Unity layer per peer

Both peers' objects occupy the same world coordinates; layers + camera culling keep the
two halves visually separate.

- Add `public int Layer;` and apply `go.layer = Layer` to **every** object the view
  creates: floor, 4 borders, walls, tank roots, bodies, markers, barrels, bullets.
- `CreatePrimitive`/`CreateBox`/`CreateSphere` are `static` — make them instance methods
  so they can read `Layer` (or pass it through).
- Unity-ism: `layer` is **not inherited** from a parent at runtime. Set it on children
  explicitly (the tank body/marker created under `root` still need it).

### Step 4 — `DebugHud`: per-side screen rect

IMGUI is screen-global (ignores cameras/layers), so two HUDs at `Rect(10,10,…)` would
overlap. Add `public int Side;` and offset:

```csharp
float x = Side == 0 ? 10f : Screen.width / 2f + 10f;
GUILayout.BeginArea(new Rect(x, 10, 440, 320), GUI.skin.box);
GUILayout.Label($"PEER {Side} (P{_runner.LocalPlayer + 1})");
GUILayout.Label($"rx: {_runner.PacketsReceived} pkts, last tick {_runner.LastRxTick}, {_runner.LastRxInput}");
```

### Step 5 — `Bootstrap`: one shared network, one pump, two peers

```csharp
var net = new InProcessNetwork(latencyTicks: 2);   // start gentle; sliders come in M2

var pump = new GameObject("Network").AddComponent<NetworkPump>();
pump.Network = net;

CreatePeer(0, net.EndpointA, new Rect(0f,   0f, 0.5f, 1f), layer: 8);
CreatePeer(1, net.EndpointB, new Rect(0.5f, 0f, 0.5f, 1f), layer: 9);

static void CreatePeer(int player, ITransport transport, Rect viewport, int layer)
{
    float w = SimConfig.ArenaWidth.ToFloat(), h = SimConfig.ArenaHeight.ToFloat();

    var cam = new GameObject($"Peer{player} Camera").AddComponent<Camera>();
    cam.orthographic = true;
    cam.rect = viewport;                    // set rect BEFORE reading cam.aspect
    cam.cullingMask = 1 << layer;
    // Half-width viewport halves the aspect — size by whichever dimension binds,
    // or the arena gets clipped left/right (classic 20-minute time sink):
    cam.orthographicSize = Mathf.Max(h / 2f + 1f, (w / 2f + 1f) / cam.aspect);
    cam.clearFlags = CameraClearFlags.SolidColor;
    cam.backgroundColor = new Color(0.08f, 0.09f, 0.11f);
    cam.transform.position = new Vector3(w / 2f, 30f, h / 2f);
    cam.transform.rotation = Quaternion.Euler(90f, 0f, 0f);
    // don't tag either camera MainCamera; nothing uses Camera.main

    var go = new GameObject($"Peer{player}");
    var runner = go.AddComponent<SimRunner>();   // Awake runs inside AddComponent…
    runner.LocalPlayer = player;                 // …fields land before Start/Update
    runner.Transport = transport;
    go.AddComponent<GameView>().Layer = layer;   // Start (builds objects) runs later
    go.AddComponent<DebugHud>().Side = player;
}
```

Keep the single directional light as-is (lights hit all layers by default).

### M1 traps

1. **Forgot the pump / `Poll` never runs** → rx counters stay 0 forever. Check first.
2. **`cam.aspect` read before `cam.rect` set**, or ortho size not recomputed → arena
   clipped horizontally in each half. Use the `Mathf.Max` formula above.
3. **A child object missed `layer`** → it ghosts into both views. Sweep every
   `CreatePrimitive`/`new GameObject` in `GameView`.
4. If the scene you press Play in has its own Main Camera, it'll fight the two peer
   cameras — delete it from the scene (the repo expects an empty scene).
5. `InputSampler.Reset()` is static and resets *both* players' turret memory. Both
   runners calling it on the same frame (shared `R` key) is harmless in-process — just
   don't be surprised that peer A's reset touches P2 state.
6. Endpoints are crossed internally (A's `Send` arrives at B). Don't "helpfully" swap
   them: peer A ↔ `EndpointA`.

### M1 definition of done

- [ ] Two viewports; each shows one full arena, one blue + one red tank.
- [ ] WASD (or pad 0) moves the **left** half's blue tank only; arrows (or pad 1) move
      the **right** half's red tank only. Each side's other tank is frozen.
- [ ] Both HUDs show `rx` counting up at ~60/s with plausible tick numbers.
- [ ] Firing works on your own half; bullets bounce; `R` resets both halves.
- [ ] `dotnet test` still 29/29 (M1 shouldn't touch Sim/Net at all).

---

## 3. M2 — Lockstep (~2–2.5 h)

**Acceptance (BRIEFING), all three layers, no skipping:** (1) both halves play
identically (small tick offset under latency is fine); (2) each HUD shows its own hash
AND the peer's reported hash, matching on every confirmed tick; (3) with
`LatencyTicks=12, JitterTicks=3, LossChance=0.05` hashes still match and lockstep
visibly stalls. Plus: latency/jitter/loss as live HUD sliders.

### Step 0 — Nail the tick convention (write this comment into SimRunner verbatim)

```
// state.Tick   = tick of the CURRENT state (already simulated)
// next         = state.Tick + 1               — the tick we want to simulate now
// scheduled    = next + INPUT_DELAY           — the tick a freshly sampled input is for
// We may simulate `next` only when BOTH players' inputs for `next` are in the ring.
```

`INPUT_DELAY = 3`. Worked startup (both peers do the same; ring pre-seeded at reset):

| about to play | schedule & send | gate on | result |
|---|---|---|---|
| (reset) | — | — | ring[1..3] = `None` for **both** players (pre-seed by convention) |
| tick 1 | sample → ring[4] + send | ring has both @1 ✓ (seeded) | advance |
| tick 2 | → ring[5] + send | seeded ✓ | advance |
| tick 3 | → ring[6] + send | seeded ✓ | advance |
| tick 4 | → ring[7] + send | local ✓ + remote@4 arrived? | advance or **stall** |

Without the pre-seed, both peers deadlock at tick 1 waiting for inputs nobody ever
scheduled — a classic first-hour stall. Expected feel: your own tank responds
`INPUT_DELAY` ticks (50 ms) late; under heavy latency the whole sim stutters. Correct.

### Step 1 (Track B) — `InputCodec` v2: hash piggyback, 20 bytes

```
[0..3]   input tick  u32 LE
[4]      player      u8
[5]      buttons     u8
[6..7]   turret aim  u16 LE
[8..11]  hashTick    u32 LE   — sender's latest confirmed tick
[12..19] hash        u64 LE   — sender's State.Hash() at hashTick
```

- Bump `MessageSize` to 20; add `Write/ToBytes/Read` overloads carrying
  `(uint hashTick, ulong hash)`. **Keep the old 3-arg overloads** (hash fields = 0) so
  `InputsExchangedOverWireKeepSimsInLockstep` compiles untouched.
- The existing codec tests assert against `MessageSize`, so they keep passing; add one
  round-trip test for the hash fields.

### Step 2 (Track B) — `InputRing` (new file: `Assets/Tanks/Net/InputRing.cs`)

Pure C#, namespace `Tanks.Net` — the SimTests csproj globs the Net folder, so it's
automatically testable with `dotnet test`. Per-player inputs keyed by tick:

```csharp
public sealed class InputRing
{
    public const int Capacity = 256;   // matches StateHistory; ≥ delay + worst latency
    private struct Slot { public uint Tick; public PlayerInput P0, P1; public byte HasMask; }
    private readonly Slot[] _slots = new Slot[Capacity];

    public void Record(uint tick, int player, PlayerInput input);  // resets slot if Tick != tick
    public bool Has(uint tick, int player);                        // slot.Tick == tick && mask bit
    public PlayerInput Get(uint tick, int player);
    public void Clear();
}
```

Semantics: last-write-wins is fine *because* the sender schedules each tick exactly once
(Step 4) — every write for a (tick, player) carries the same value. Unit-test: record/
get round trip, `Has` false for wrong tick, slot reuse after 256, `Clear`.

### Step 3 (Track B) — headless lockstep-under-fire test (write it BEFORE the Unity wiring)

This is your binary canary for the whole milestone, runnable in seconds without Unity —
and it doubles as the reference implementation for Step 4. New `SimTests~/LockstepTests.cs`:

```csharp
[Test]
public void LockstepOverLossyLatentWireStaysInSync()
{
    var net = new InProcessNetwork(latencyTicks: 6, jitterTicks: 3, lossChance: 0.05f);
    var arena = Arena.CreateDefault();
    const uint DELAY = 3;

    var state = new[] { GameState.CreateInitial(), GameState.CreateInitial() };
    var ring  = new[] { new InputRing(), new InputRing() };
    var lastScheduled = new uint[] { DELAY, DELAY };
    var hashes = new[] { new Dictionary<uint, ulong>(), new Dictionary<uint, ulong>() };
    var inputs = new PlayerInput[2];
    ITransport[] tp = { net.EndpointA, net.EndpointB };

    for (int p = 0; p < 2; p++)                       // pre-seed the delay window
        for (uint t = 1; t <= DELAY; t++)
            { ring[p].Record(t, 0, PlayerInput.None); ring[p].Record(t, 1, PlayerInput.None); }

    for (uint netTick = 1; netTick <= 2000; netTick++)
    {
        net.Poll(netTick);
        for (int p = 0; p < 2; p++)
        {
            while (tp[p].TryReceive(out var pkt))     // drain
            {
                InputCodec.Read(pkt, out uint t, out int who, out PlayerInput pi);
                ring[p].Record(t, who, pi);
            }
            uint scheduled = state[p].Tick + 1 + DELAY;
            if (scheduled > lastScheduled[p])          // schedule each tick exactly once
            {
                ring[p].Record(scheduled, p, ScriptedInput(p, scheduled));
                lastScheduled[p] = scheduled;
            }
            for (uint t = lastScheduled[p] - 2; t <= lastScheduled[p]; t++)   // redundant resend ×3
                tp[p].Send(InputCodec.ToBytes(t, p, ring[p].Get(t, p)));

            for (int step = 0; step < 5; step++)       // advance while the gate allows
            {
                uint next = state[p].Tick + 1;
                if (!ring[p].Has(next, 0) || !ring[p].Has(next, 1)) break;
                inputs[0] = ring[p].Get(next, 0); inputs[1] = ring[p].Get(next, 1);
                Simulation.Tick(state[p], arena, inputs);
                hashes[p][state[p].Tick] = state[p].Hash();
            }
        }
    }

    uint confirmed = Math.Min(state[0].Tick, state[1].Tick);
    Assert.That(confirmed, Is.GreaterThan(1500), "lockstep barely progressed — scheduling bug?");
    for (uint t = 1; t <= confirmed; t++)
        Assert.That(hashes[1][t], Is.EqualTo(hashes[0][t]), $"desync at tick {t}");
}
```

(Reuse a `ScriptedInput` like the ones already in the test files.) When this is green,
M2's logic is correct — the Unity wiring is then mechanical.

### Step 4 (merge) — rewire `SimRunner.StepOnce` into the lockstep loop

Order inside each fixed step: **schedule-once → resend → gate → advance**. Drain stays
in `Update` before the step loop (the pump's `DefaultExecutionOrder(-100)` already
polled this frame).

```csharp
private uint _lastScheduledTick;   // = INPUT_DELAY after reset
public int Stalls { get; private set; }

private bool TryStepOnce()
{
    uint next = State.Tick + 1;

    uint scheduled = next + InputDelay;
    if (scheduled > _lastScheduledTick)               // sample each tick EXACTLY once
    {
        PlayerInput local = LocalPlayer == 0 ? InputSampler.SampleP1() : InputSampler.SampleP2();
        _ring.Record(scheduled, LocalPlayer, local);
        _lastScheduledTick = scheduled;
    }
    for (uint t = _lastScheduledTick - 2; t <= _lastScheduledTick; t++)   // loss armor
        Transport.Send(InputCodec.ToBytes(t, LocalPlayer, _ring.Get(t, LocalPlayer),
                                          State.Tick, LastHash));         // hash piggyback

    if (!_ring.Has(next, 0) || !_ring.Has(next, 1)) { Stalls++; return false; }

    _inputs[0] = _ring.Get(next, 0);
    _inputs[1] = _ring.Get(next, 1);
    Simulation.Tick(State, Arena, _inputs);
    LastHash = State.Hash();
    History.Record(State);
    return true;
}
```

In `Update`: `while (_accumulator >= step && steps < MaxStepsPerFrame) { if (!TryStepOnce()) break; … }`,
and clamp the accumulator (e.g. to 0.25 s) so a long stall doesn't bank seconds of
fast-forward.

**The desync trap this design dodges:** if you re-sample on a stalled frame and
overwrite the same scheduled slot, the receiver may consume the *first* value while you
later simulate with the *second* → desync that only appears under stall. Sample once per
tick index; on stalled frames you only *re-send* the already-recorded value. The ×3
redundant send is what makes 5% loss survivable with no ack protocol (~1.4 kB/s — free).

### Step 5 — drain with guards + desync alarm

```csharp
private uint _peerHashTick; private ulong _peerHash;
public bool HashesAgree { get; private set; } = true;
public uint PeerHashTick => _peerHashTick;

private void DrainNetwork()
{
    while (Transport.TryReceive(out var pkt))
    {
        InputCodec.Read(pkt, out uint tick, out int player, out PlayerInput input,
                        out uint hashTick, out ulong hash);
        PacketsReceived++;
        if (player == LocalPlayer) continue;                    // belt & braces
        if (tick > State.Tick + InputDelay + 64) continue;      // stale-future guard (see reset)
        _ring.Record(tick, player, input);
        if (hashTick > _peerHashTick) { _peerHashTick = hashTick; _peerHash = hash; }
    }

    var snap = History.Get(_peerHashTick);                      // null if too old/not yet reached
    if (snap != null && snap.Hash() != _peerHash)
    {
        HashesAgree = false;
        Debug.LogError($"DESYNC @tick {_peerHashTick}: mine={snap.Hash():X16} theirs={_peerHash:X16}");
    }
}
```

HUD additions per peer: `tick`, own hash, `peer hash @tick` with a ✓/✗ that flashes the
box red on mismatch (`GUI.color = Color.red`), `stalls`, `rx/s`. Remember
`GameState.Hash()` mixes in `Tick` — only ever compare hashes for the **same** tick.

### Step 6 — reset, without time bombs

In-process, both peers see `R` on the same frame, so resets coincide. Per peer:
`ResetMatch` → `_ring.Clear()`, re-seed ticks `1..INPUT_DELAY` for both players,
`_lastScheduledTick = InputDelay`, clear `_peerHashTick/_peerHash/HashesAgree`.

**The time bomb:** packets from *before* the reset are still in flight, stamped with
huge tick numbers. Without the stale-future guard above, one lands in the fresh ring at
slot `tick % 256` and detonates as a desync minutes later when the sim reaches that
tick. The guard (2 lines) kills the whole class. Optionally also add
`InProcessNetwork.Reset()` (clear `_inFlight` + both inboxes) called once on reset.

### Step 7 — network knob sliders (top-center, on the pump's GameObject)

```csharp
GUILayout.BeginArea(new Rect(Screen.width / 2f - 150, 10, 300, 110), GUI.skin.box);
GUILayout.Label($"latency {Network.LatencyTicks}t   jitter {Network.JitterTicks}t   loss {Network.LossChance:P0}   in-flight {Network.InFlightCount}");
Network.LatencyTicks = (int)GUILayout.HorizontalSlider(Network.LatencyTicks, 0, 30);
Network.JitterTicks  = (int)GUILayout.HorizontalSlider(Network.JitterTicks, 0, 10);
Network.LossChance   = GUILayout.HorizontalSlider(Network.LossChance, 0f, 0.3f);
GUILayout.EndArea();
```

### M2 definition of done (maps 1:1 to the briefing's three layers)

- [ ] **Visual:** both tanks drive on both halves; bullets bounce; kills register; the
      halves match (up to a small tick offset under latency).
- [ ] **Hash:** both HUDs show own + peer hash agreeing every confirmed tick, ✓ stays
      green during a full match including kills and resets.
- [ ] **Stress:** sliders at 12 / 3 / 0.05 → hashes still agree; play visibly stalls.
      (At latency 12 vs delay 3 it *should* stutter constantly — that pain is M3's
      motivation, not a bug.)
- [ ] Headless `LockstepOverLossyLatentWireStaysInSync` green; full suite green.
- [ ] `R` mid-stress resets cleanly and stays in sync afterward (the §Step-6 guard).

---

## 4. Stretch — M3 rollback, first moves only

If M2 lands early (details in BRIEFING — the loop is already pseudocoded there):

1. Prep refactor (~10 min): move `StateHistory.cs` from `Assets/Tanks/Game/` to
   `Assets/Tanks/Net/` and change its namespace to `Tanks.Net` (it's already pure C#;
   `SimRunner` already has `using Tanks.Net`). Now the rollback loop is headless-testable
   and the SimTests glob picks it up automatically.
2. Extend `InputRing` slots with a per-player `Confirmed` bit; add
   `PredictMissingFor(tick)` = copy last known input ("same as last tick" policy).
3. In `TryStepOnce`: never stall — predict and advance; on receiving a confirmed input
   that contradicts a prediction, `State.CopyFrom(History.Get(t - 1))`, re-`Tick`
   through now with corrected inputs, overwriting history as you go. Cap at 12 ticks
   re-simulated; beyond that, fall back to stall.
4. First test: "lie about the remote input for N ticks, deliver truth, assert the
   corrected hash equals a truth-from-the-start reference run" (BRIEFING suggests it).

---

## 5. Timeline sanity check (for a ~3 h session)

| t | milestone |
|---|---|
| 0:00 | both read BRIEFING/STUDY_GUIDE, run tests, press Play (the briefing's first-30-min list) |
| 0:20 | split: Track A starts M1, Track B starts codec v2 + InputRing + headless test |
| 1:05 | M1 accepted (rx counters live); headless lockstep test green |
| 1:50 | merge done: ring + schedule-once + gate wired into SimRunner; layer-1/2 acceptance |
| 2:20 | hash piggyback + alarm + sliders; stress test passes → **M2 done** |
| 2:20+ | M3 prep refactor + prediction, as far as you get |

Behind at 1:50? Skip sliders (hardcode the stress values) and keep the desync alarm —
it's the acceptance-critical part.

---

## 6. If you get stuck (symptom → check, in order)

- **rx counter stays 0** → does `NetworkPump` exist and is `Poll` running? Endpoints
  crossed right (peer A ↔ `EndpointA`)? Is `Send` actually called each step?
- **Sims freeze at tick 3** (or 0) → pre-seed missing, or gate checks `state.Tick`
  instead of `state.Tick + 1`, or you stamped sends with `State.Tick` instead of the
  scheduled tick. Re-read the §Step-0 table.
- **Desync at tick 1** → one peer pre-seeded `None`, the other didn't, or hash compared
  across different ticks (`Hash()` includes `Tick`).
- **Sporadic desync only under loss/jitter** → re-sampling on stalled frames (the
  schedule-once guard missing), or a stale-future packet recorded into the ring (the
  drain guard missing), or inputs applied at `tick` instead of `scheduled`.
- **Desync some seconds after a reset** → in-flight pre-reset packets; see §Step 6.
- **Hashes "mismatch" but gameplay looks identical** → almost always comparing
  different ticks; print both tick numbers next to the hashes.
- **Stall never recovers after a drop** → redundant resend missing (a lost packet is
  never retransmitted, and lockstep waits forever).
- **Determinism test fails after your change** → a float or `UnityEngine` type crept
  into Sim/Net, or iteration order changed. Never fix with a clamp; find the cause.

Fast loops: `cd SimTests~ && dotnet test` after every Sim/Net edit; for the Game layer,
Unity Play mode is the only check (batch-mode compile catches `error CS` if needed —
command in `CLAUDE.md`).

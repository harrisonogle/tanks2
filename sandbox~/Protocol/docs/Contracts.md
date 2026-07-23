# Contracts.md

## Scope

Defines the *external surface* of each component in the netcode subsystem:
- What each component depends on
- What signals (method calls) it accepts
- What events it emits

Out of scope: internal state machines, timers, retransmit logic, queue capacities, concurrency-primitive choices, and any other implementation detail. Those live in code or in separate state-machine documents.

Some drift between this document and the code is expected and acceptable — the contracts here are the shape, not the law.

## Threads

| Thread | Lifetime | Runs |
|--------|----------|------|
| Game thread (Unity main) | App | Sim, UI, `UXStateMachine` |
| Discovery thread | App (started at bootstrap) | `Discovery` component |
| Session protocol thread | App (started at bootstrap) | `SessionProtocol` component |

Both network threads are bootstrapped at app startup and live for the lifetime of the application, in the spirit of "a network protocol is a service that's always running" — not "spun up per match."

## Concurrency primitives at thread boundaries

These are not components — they are seams between components on different threads. Their concrete implementation (which .NET primitive, what capacity, what drop policy) is out of scope.

- **Event queue** — produced by `SessionProtocol`, consumed by the game thread. Carries status events and in-session data events. Thread-safe, single-producer / single-consumer.
- **Peer table** — produced by `Discovery`, exposed as a read-only lookup (`PeerLookup`) to any thread that depends on it. Single-writer / multi-reader.

## Components

### Discovery

Finds peers on the network and surfaces them via a read-only lookup. Autonomous — no signals from other components drive it.

```
component Discovery
  runs on: discovery thread

  depends on:
    DiscoverySocket   (network abstraction — multicast for now, STUN/equivalent later)

  exposes:
    PeerLookup        (read-only peer table, see contract below)

  signals: none
  events:  none
```

### SessionProtocol

Implements the wire protocol described in `Protocol.md`. Owns the peer-to-peer connection lifecycle: discovery hand-off, handshake, in-session message exchange, teardown.

```
component SessionProtocol
  runs on: peer protocol thread

  depends on:
    PeerLookup        (to find a peer when matchmaking)
    SessionSocket        (UDP send/receive abstraction)

  control signals (called by game thread):
    StartMatchmaking()
      Query PeerLookup, auto-pick a peer, begin handshake.
      No-op if already matchmaking, connecting, or in-session.

    LeaveMatch(reason)
      Send Disconnect to peer, tear down, return to idle.
      No-op if not in-session.

  outgoing-data signals (called by game thread, typically once per tick):
    SendInput(tick, input)
      Enqueue an input to be carried in the next outgoing packet (with redundancy).

    SendAdvantage(currentTick, ackedTick)
      Update the values reported in the next outgoing AdvantageMessage.

    SendStateHash(tick, hash)
      Enqueue a state hash to be carried in the next outgoing packet.

  events (emitted to game thread via event queue):
    MatchmakingStatusChanged(status)
      status ∈ { Searching, PeerFound, DiscoveryFailed(reason) }

    HandshakeStatusChanged(status)
      status ∈ { Connecting, Established, HandshakeFailed(reason) }

    Disconnected(reason)
      Terminal for the current session. Component returns to idle.

    RemoteInputsArrived(latestTick, count, inputs)
    RemoteAdvantageReceived(currentTick, ackedTick)
    RemoteStateHashReceived(tick, hash)
```

Notes on the contract:

- Outgoing-data signals are *best-effort*. The component may drop them under back-pressure; callers must not assume every call results in a packet.
- Signals are safe to call from the game thread without external synchronization. The component is responsible for thread-safety of its own surface.
- Events are delivered via the event queue and consumed on the game thread's tick boundary. The component never calls into the game thread directly.

### UXStateMachine

Lives on the game thread. Tracks which UX state the player is in (per `UXFlow.md`) and translates between SessionProtocol's events and the UI's rendering needs. It is the *only* thing on the game thread that calls SessionProtocol's control signals; the UI code calls the UXStateMachine.

```
component UXStateMachine
  runs on: game thread

  depends on:
    SessionProtocol   (calls control signals; consumes events from the event queue)

  signals (called by UI code):
    PlayerRequestedMatchmaking()
    PlayerRequestedLeaveMatch()

  events (observed by UI code, typically via polling current state):
    CurrentState ∈ { MainMenu, Searching, Connecting, InMatch }
    LastErrorPresentation (optional, set when transitioning to MainMenu from a failure)
```

The UX state machine consumes SessionProtocol's events on each game-thread tick (drain queue → apply transitions). UI code reads `CurrentState` to render the appropriate screen.

## Contracts

### PeerLookup

Read interface to the peer table. Exposed by `Discovery`; consumed by `SessionProtocol`.

```
contract PeerLookup
  guarantees:
    single-writer  (Discovery, on the discovery thread)
    multi-reader   (any thread, no external locking required)
    consistent per query  (each query returns a coherent snapshot of one peer or one list)

  queries:
    TryFindPeer(criteria) -> PeerInfo | none
    Snapshot()            -> list of PeerInfo

  PeerInfo:
    PeerId
    EndPoint
    (plus discovery-specific metadata as needed)
```

### SessionSocket and DiscoverySocket

Abstractions over the OS UDP socket. Mentioned for completeness; their contracts are the obvious send/receive surface and aren't elaborated here.

## Component dependency graph

```
                ┌──────────────┐
                │  UI code     │  (Unity scenes, MonoBehaviours)
                └──────┬───────┘
                       │ signals / state reads
                       ▼
                ┌──────────────┐
                │ UXStateMachine │
                └──────┬───────┘
                       │ signals
                       │ event consumption
                       ▼
                ┌──────────────┐         ┌──────────────┐
                │ SessionProtocol │─────►│ PeerLookup   │
                └──────┬───────┘ reads   └──────┬───────┘
                       │ UDP                    │ exposed by
                       ▼                        ▼
                ┌──────────────┐         ┌──────────────┐
                │ SessionSocket │        │ Discovery    │
                └──────────────┘         └──────┬───────┘
                                                │ UDP
                                                ▼
                                         ┌──────────────┐
                                         │DiscoverySocket│
                                         └──────────────┘
```

Dependencies are one-way: nothing on the right calls back into anything on the left.

## Open items / deferred

- **Match lifecycle signals.** With Option A (endless matches) there's no `MatchEnded` signal. If Option B is adopted later, add `Disconnected(MatchEnded)` semantics and a corresponding UI transition.
- **Cancellation signals.** `UXFlow.md` defers cancellation of `Searching` / `Connecting`. When added: `CancelMatchmaking()` and `AbortHandshake()` signals on SessionProtocol, plus the matching UI signals on UXStateMachine.
- **Peer selection.** Auto-pick only. If a lobby UI is added later, `SessionProtocol.StartMatchmaking()` would gain a `peerId` parameter (or split into `BeginPeerDiscovery()` + `ConnectToPeer(peerId)`), and the game thread would gain visibility into the peer table via `PeerLookup`.
- **Pre-match settings negotiation.** Settings freeze at match-start in v1. A future settings-negotiation flow would add signals/events for proposing and accepting settings during handshake.
- **Multiple concurrent sessions.** Out of scope — only one peer session at a time. SessionProtocol could be extended to manage N sessions; not modeled here.
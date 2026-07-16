# UXFlow.md

## Overview

User-facing flow for the netcode-enabled multiplayer mode of the tank game. Covers the screens the player moves through from the main menu through an in-match session, and the transitions between them. Friends-only build, 2 players, auto-pick peer selection.

Scope: what the player experiences and what UX states exist. Does **not** cover:
- How the netcode is decomposed across threads (see `Threading.md` / `Concurrency.md`)
- The wire protocol (see `Protocol.md`)
- Sim-level mechanics (death, respawn, win/loss detection)

## States

The player is always in exactly one of these states:

| State | What the player sees | What's happening underneath |
|-------|----------------------|------------------------------|
| `MainMenu` | Title screen, "Find Match" button, optional inline status indicator for last error | Discovery may or may not be running in the background; game-protocol component does not exist |
| `Searching` | "Searching for peer..." with a cancel-less progress indicator | Discovery is actively scanning; auto-pick logic waits for the first qualifying peer |
| `Connecting` | "Connecting to peer..." | Game-protocol handshake legs are in flight (Ping/Pong/PingPong) |
| `InMatch` | Gameplay screen; on death, win/loss banner overlay with auto-reset countdown | Steady-state protocol running; sim is ticking; sim handles death/reset internally via inputs |

No cancellation paths from `Searching` or `Connecting` in v1 — the player waits for success or failure.

## Transitions

```
                       ┌───────────────────────────────────────────────┐
                       │                                               │
                       ▼                                               │
                  ┌─────────┐  "Find Match"   ┌───────────┐            │
                  │MainMenu │ ──────────────► │ Searching │            │
                  └─────────┘                 └─────┬─────┘            │
                       ▲                            │                  │
                       │ DiscoveryFailed            │ PeerFound        │
                       │ (status: "No peers")       ▼                  │
                       │                      ┌────────────┐           │
                       │ HandshakeFailed      │ Connecting │           │
                       ├──────────────────────└─────┬──────┘           │
                       │ (status: "Couldn't        │ Established      │
                       │  connect")                 ▼                  │
                       │                      ┌─────────┐              │
                       │ Disconnected         │ InMatch │ ─── death ───┤
                       └──────────────────────└─────────┘  (sim-level, │
                          (status: "Lost                    auto-reset │
                           connection")                      continues │
                                                             in-state) │
                                                                       │
                                                  (loops forever       │
                                                   until Disconnect) ──┘
```

### Transition table

| From | Event | To | Notes |
|------|-------|-------|-------|
| `MainMenu` | Player clicks "Find Match" | `Searching` | Sends `StartMatchmaking` intent |
| `Searching` | Discovery surfaces a qualifying peer | `Connecting` | Auto-pick picks the first one; game-protocol begins handshake |
| `Searching` | Discovery times out / no peers | `MainMenu` | Status: "No peers found" |
| `Connecting` | Handshake completes (`ConnectionEstablished`) | `InMatch` | Sim starts ticking |
| `Connecting` | Handshake fails | `MainMenu` | Status: "Couldn't connect to peer" |
| `InMatch` | Player dies | `InMatch` | Sim-level event; banner overlay shown, auto-reset after N seconds, stays in `InMatch` |
| `InMatch` | `Disconnected(reason)` from net layer | `MainMenu` | Status varies by reason — see Presentation |

## Presentation: error/status indicator

Single inline status indicator on `MainMenu` (toast, overlay, or read-only debug console line — exact form is UI's call). Same UX treatment for all failure types; only the message text varies based on the underlying cause:

| Cause | Message (strawman) |
|-------|--------------------|
| Discovery timeout / no peers found | "No peers found." |
| Handshake failure | "Couldn't connect to peer." |
| Mid-match peer disconnect (peer-initiated) | "Opponent left." |
| Mid-match silence timeout | "Lost connection to peer." |
| Protocol error / desync | "Connection error." |
| Unknown / catch-all | "Disconnected." |

State-machine-wise, every failure → same `MainMenu` state. The message is a presentation parameter, not a state.

## The `InMatch` loop (Option A: endless match)

In v1, matches do not end at the netcode level. When a player dies:

1. Sim detects death.
2. Both peers' sims independently show a "You win" / "You lose" banner (deterministic — both sims agree on who died, so both show the right banner from each player's perspective).
3. After N seconds (or on a button press), an auto-reset input is applied. The reset is just another input the sim handles; rollback covers it the same as any other input.
4. Sim state resets, banner clears, gameplay continues.

This means the netcode never sees the death/reset cycle — it's entirely a sim-level concern carried by the existing input stream. The `InMatch` UX state does not change during this cycle.

If both players want to stop playing, they exit the app (or, future work, add a "Leave Match" button that sends an explicit disconnect).

## Intents and status events

The events that cross the boundary between the game thread and the netcode subsystem, derived from this flow:

**Game thread → netcode (intents):**
- `StartMatchmaking` — fired on entering `Searching`

**Netcode → game thread (status events):**
- `MatchmakingStatusChanged(PeerFound | DiscoveryFailed(reason))`
- `HandshakeStatusChanged(Established | HandshakeFailed(reason))`
- `Disconnected(reason)`
- (Plus in-match data events: `RemoteInputsArrived`, `RemoteAdvantageReceived`, etc. — these don't drive UX state, they feed the sim.)

The exact wire shape of these intents/events is in `Threading.md` (or wherever the message-contract doc lives). This doc just establishes which ones must exist and what UX state each one corresponds to.

## Deferred / open items

Things consciously omitted from v1, listed so future-me knows they're deliberate omissions rather than oversights:

- **Cancellation paths.** No way to back out of `Searching` or `Connecting`. Player waits for success or failure. Adding cancellation = new intents (`CancelMatchmaking`, `AbortHandshake`) and new transitions back to `MainMenu`.
- **Peer selection UI.** Auto-pick only. No lobby list, no peer picker, no host/join asymmetry. Adding selection = new `Lobby` state between `Searching` and `Connecting`.
- **Pre-match settings.** Settings (input delay, etc.) live in the main menu and freeze at match-start. No per-match negotiation. Adding it = new `MatchSetup` state and a settings-negotiation step in the protocol.
- **Match-end flow (Option B).** Matches are endless in v1. If matches ever end naturally (best-of-N rounds, time limit, etc.), `InMatch` gains an outbound transition to either `MainMenu` or a future `PostMatch` state, and the `DisconnectReason.MatchEnded` variant gets used on the wire.
- **Rematch prompt.** Not applicable while matches are endless. If Option B is adopted later, this becomes a `PostMatch` state with "Rematch" / "Back to Main Menu" choices.
- **Player display names.** Status events could carry a peer display name for "Connecting to PlayerX..." UI. Not used in v1; auto-pick + no peer selection means there's no name to show.
- **Settings screen accessible during search.** Main menu only; can't fiddle with settings while `Searching`. Trivial to relax later.
# P425 — Command a builder drone (or any owned NPC) from the MonoGame client

Status: implemented, tested, live-proven. Feature committed on `master` (not
pushed — Domingo pushes).

## What this adds

A minimal 2-selection keyboard path to command a same-side ship/drone (e.g. a
Builder) from the MonoGame client:

- **Commandee selection** (WHO to command), a NEW state distinct from the
  target reticle: **PageDown** cycles the commandee through same-side ships.
- **Issue order** (WHAT to order them at): **Insert** sends a real
  `ORDER_CHANGE(93)` for the selected commandee against the selected target.
- **Feedback**: console lines + a HUD readout naming the commandee, and the
  server's real refusal reasons are surfaced to the commanding client as a
  private "Negative." chat (native's command-refusal ack) — never silent.

## The core finding: the "already working server-side" claim needed a small extension

The brief asserted that sending `ORDER_CHANGE { ShipID = the drone,
CidBuild, OT_asteroid }` "does the rest" server-side because P422 proved it for
a real player. **Verified against the live code: that is true for a real PLAYER
ship but false for an NPC DRONE.** In `HandleOrderChange`
(`Server.ShipCommands.cs:1054`) the drone resolves as the commandee, but then
the flying-NPC branch (`target.IsNpc && StationId is null`) swallows **every**
commandID — including `CidBuild` — as "return to base" (`:1145-1147`), and the
generic Build path only runs for `!target.IsNpc`. P422's live proof used a real
Builder-hulled PEER (PilotType=Player), which is why it never hit this. So P425
adds a **direct-NPC Build branch** to `HandleOrderChange` — a real command to a
same-side drone reaches construction instead of returning it to base. This is
the honest correction; the task is the minimal hookup to use a real mechanic,
plus the one server-side arm that mechanic's drone case needed.

## Server-side changes (`src/Allegiance.Server/Server.ShipCommands.cs`)

- **Direct-NPC Build branch** (`:1086-1092`), inserted at the top of
  `HandleOrderChange` before the two NPC arms (docked / return-to-base), gated
  on `target.IsNpc && req.CommandId == CidBuild`. Routes to the new
  `HandleDirectNpcBuildOrder`.
- **`HandleDirectNpcBuildOrder`** (`:1531`) — commanding a drone is commanding
  someone OTHER than the sender, so this deliberately has NO self-command gate
  (native gives drone orders straight onto the drone's accepted slot,
  `OfflineCommandToDrone` / `clintlib.cpp`). Everything else is the shared
  legality body.
- **`StartBuildIfLegal`** (`:1583`) — the shared legality body extracted from
  P422's `HandlePlayerBuildOrder`, reused by BOTH the player self path and the
  drone path, so an illegal request is refused identically everywhere:
  Builder-role (now also via the real `PilotType == PilotTypeBuilder` — native's
  `c_ptBuilder` — OR the P422 `HabmBuilder` hull stand-in), `OT_asteroid` only,
  capability match against `Ship.BuilderStationType`, builder's sector present,
  and the rock in the builder's sector. On any refusal it calls
  `RefuseBuild`.
- **`RefuseBuild`** (`:1659`) — logs AND sends the commanding player a private
  `ChatMessage` "Negative. <reason>" (native `SendChat(pshipTo,
  CHAT_INDIVIDUAL, ..., "Negative.")`, clintlib.cpp:2261-2268). This is the
  "surface the server's real refusal reasons" requirement, reusing the existing
  private-chat wire the MonoGame client already renders.
- `HandlePlayerBuildOrder` (`:1507`) is now a thin self-command-gated wrapper
  over the shared body (P422 behavior preserved — its 6 tests still pass).

## Client-side changes (`src/Allegiance.ClientCore.MonoGame/`)

- **`CommandOrderResolver.cs`** (new) — the pure, headlessly-testable decision:
  given the commandee's role bits (its hull `HabmCapabilities`) + target kind,
  returns the real `ORDER_CHANGE` payload (CommandId/ObjectType/ObjectID), or
  null when no order applies. Only BUILD is implemented (Builder-role commandee
  at an asteroid → `c_cidBuild`); a Miner/Goto order is a noted near-zero-cost
  future case once the server grows the matching direct-NPC arm (out of scope).
- **`Game1.Targeting.cs`** — `SelectCommandee` (reuses the P415 same-side-ship
  filter machinery — `GatherSectorCandidates` + `FilterCandidates` +
  `StepCycle` with `TargetRelation.Friendly`, ships only — into the NEW
  `_commandee` slot; no new candidate-gathering system) and
  `TryIssueOrderToCommandee` (resolves the order and calls the production
  `ConnectionSession.SendOrderChange(commandeeId, …, CmdAccepted, …)`).
- **`Game1.FlightInput.cs`** — the PageDown / Insert keys, edge-triggered and
  composing-gated like every other flight key.
- **`Game1.cs`** — `_commandee` field + the two key-latch fields.
- **`Game1.Rendering.cs` / `Rendering/HudRenderer.cs`** — a commandee readout
  line (mint, distinct from the leader-command cyan) naming the commanded ship
  + the Insert hint, present only while a commandee is selected.
- **`Rendering/KeyBindingTable.cs`** — a new "COMMANDED SHIP (DRONE)" help group.

## Keys added

| Key | Action |
|-----|--------|
| **PageDown** | Cycle which same-side ship/drone you are commanding (nearest first, wraps). |
| **Insert**   | Issue the order: commandee builds the targeted asteroid (Builder role required). |

Both unclaimed (grep-verified over every `XnaKeys.*` binding).

## Honest scope notes

- **Approach**: consistent with P422/P165's "no travel-before-build gate", a
  legal order starts the timed construction immediately; the port does not model
  the native physical fly-and-enter (documented divergence in P422 §3). The
  live proof confirms construction, drone consumption and the new station, as
  P422's own arc manifests in this port.
- **P165 autonomous Builder interplay**: an idle Builder drone with a configured
  station type auto-picks a rock on its own (P165). The live tests RESERVE the
  drone (`IsBuilding=true`) to isolate the direct-command path from that
  separate feature; the writeup documents that in real play the commander's
  explicit order is what commits the drone. P165 is untouched.
- **Queued/accept-reject (P424)** is deliberately NOT used — this is the direct
  NPC path (a drone obeys immediately), distinct from commanding other real
  players who get free will.

## Verification — real proof

### Wire-shape tests
- `CommandOrderResolverTests` (5, MonoGame.Tests): a Builder commandee at an
  asteroid → exact `CidBuild / OT_asteroid / rockId`; non-Builder commandee,
  Builder-at-ship/station/warp, non-ship commandee, and unknown role bits →
  null. Discriminating: neutralising the asteroid arm → `Builder_commandee_at_
  asteroid_yields_real_build_order_change` red (proved, reverted).

### Server live-loopback tests (`DirectNpcBuildOrderLiveTests`, 4, Core.Tests)
Real in-process server + real bot commander + a real Builder DRONE
(PilotType==6) on the commander's own side, over the real wire:
- `Commander_directs_builder_drone_to_build_creating_real_station` — ShipID=the
  drone → construction registered on the commanded rock → ticked to completion →
  the rock becomes a real station on the drone's side, the DRONE is consumed.
  **Must-fail (proved red, reverted):** neutralising the `CidBuild` NPC branch →
  no construction (the order fell into the return-to-base arm).
- Non-Builder drone refused + the "Negative." ack reaches the commander; a
  wrong-type rock refused + acked; a no-station-type drone refused + acked.

### MonoGame client live proof (`CommandedDroneBuildKeyLiveTests`, 2, MonoGame.Tests)
The PRODUCTION client sender `ConnectionSession.SendOrderChange` (the exact
method the Insert key calls) drives a real Builder drone on a real server:
- Happy path: construction on the commanded rock, full P422 arc (station +
  drone consumed), driven by the client's own sender.
- Illegal (wrong-type rock): refused and the "Negative." refusal is SURFACED to
  the client (`World.Events.Chats`) — the "don't leave the player guessing"
  requirement proven over the wire.

### Whole-solution build
`dotnet build AllegianceCore.slnx -c Release`: **0 warnings, 0 errors**.

### Full suite (≥2 runs each)
- **Core.Tests** 3522 (baseline 3518 + 4 new): 2 full runs + a 3rd; only the
  pre-existing rotating live-wire timing flakes recur (`LiveTurretGunnerBindingTests`
  documented persistent; `LiveWinConditionTests` documented; `GameOverPlayersWireTests`
  — confirmed environmental by flapping 2/3 on the PRISTINE tree in Release;
  plus `LiveRipcordTests`, `ConquestTerritoryLiveTests`, `CargoBayReloadTests`,
  `AsteroidMinedLiveTests`, `LiveMoneyTests`, `WarpBombTests`, `AddPartTests`
  rotating). Every one passes in isolation where not a documented persistent
  flake, and none references a P425 symbol. All 4 new DirectNpcBuild tests pass
  in the full run.
- **MonoGame.Tests** 833 (826 + 7 new): **833/833 green, 2×**.
- **ClientCore.Tests** 240: 238/240 ×2 — the same two machine-state live flakes
  P424 documented (`ShipDeleteGoneLiveTests`, `JoinFlowLiveTests`), unrelated.
- **Lobby.Tests** 12/12.

## Files changed
- `src/Allegiance.Server/Server.ShipCommands.cs`
- `src/Allegiance.ClientCore.MonoGame/CommandOrderResolver.cs` (new)
- `src/Allegiance.ClientCore.MonoGame/Game1.Targeting.cs`
- `src/Allegiance.ClientCore.MonoGame/Game1.FlightInput.cs`
- `src/Allegiance.ClientCore.MonoGame/Game1.cs`
- `src/Allegiance.ClientCore.MonoGame/Game1.Rendering.cs`
- `src/Allegiance.ClientCore.MonoGame/Rendering/HudRenderer.cs`
- `src/Allegiance.ClientCore.MonoGame/Rendering/KeyBindingTable.cs`
- `tests/Allegiance.Core.Tests/DirectNpcBuildOrderLiveTests.cs` (new, 4)
- `tests/Allegiance.ClientCore.MonoGame.Tests/CommandOrderResolverTests.cs` (new, 5)
- `tests/Allegiance.ClientCore.MonoGame.Tests/CommandedDroneBuildKeyLiveTests.cs` (new, 2)
- `tests/Allegiance.ClientCore.MonoGame.Tests/HelpOverlayTests.cs` (group count 9→10)

Committed on `master` (not pushed).

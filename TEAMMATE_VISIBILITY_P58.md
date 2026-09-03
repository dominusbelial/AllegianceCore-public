# P58 — Teammate ship-status visibility (same-side broadcast)

Date: 2026-09-02
Branch: `master`
Scope: `AllegianceCore`-only (no Windows VM, no native repo involvement).

## Problem

Today's live testing found the last real blocker in the economy arc: a
commander's own `UndockMinersAndStartMining`
(`~/Development/Allegiance/src/Wopr/Wopr/Strategies/CommanderResearchAndExpand.cs:1555-1622`)
can never find a docked miner to dispatch, because the commander's client never
learns the miner's ship exists or is docked in the first place.

Root cause, confirmed from source: `Server.BroadcastShipStates`
(`src/Allegiance.Server/Server.cs:339-401` before this change) only ever sent
each peer a `SHIP_STATUS` about **their own ship**
(`foreach (var kv in _peerShips.ToArray()) ... SendPeer(kv.Key, ...MakeOwnShipStatus(ship.Id, live)...)`).
The reference server does something fundamentally different:
`CFSMission`'s per-player status send
(`~/Development/Allegiance/src/FedSrv/fsmission.cpp:4303-4323`) loops **every
ship in the whole mission** (`m_pMission->GetShips()`, no self-filter at all)
and sends that player a `SHIP_STATUS` for **each one** — "We know something
about the ship, even if it is obsolete info." A player's own client is meant to
build a full picture of every ship it knows about, not just its own.

The native client side needs zero changes — confirmed:
`CommanderResearchAndExpand.MessageReceiver_FMD_S_SHIP_STATUS`
(`CommanderResearchAndExpand.cs:463-483`) already does a generic
`client.GetCore().GetShip(message.shipID)` lookup with no self-filter — it was
always written to handle receiving status about *any* ship, this server has
simply never sent it any.

## What changed

`src/Allegiance.Server/Server.cs`:

- New per-(recipient, subject-ship) throttle map
  `_lastTeammateStatusTick` (Server.cs:67-72), sim-thread only, mirroring the
  existing per-peer maps (`_lastStatusTick`, `_lastClusterInfoTick`).
- New cadence constant `TeammateStatusRefreshTicks = 100`
  (Server.cs:346) ≈ **5 s at 20 upd/s** — the choice, matching the ~5 s cadence
  `CommanderResearchAndExpand`'s own ping-driven dispatch already runs on
  (its `AttachMessages` comment: "drive infrequent updates"). Enough for a bot
  to notice a teammate went docked within a few seconds.
- New block at the end of `BroadcastShipStates` (Server.cs:424-455): for each
  recipient peer, loop the same `_peerShips` snapshot and, for every **same-side**
  peer ship (excluding the recipient's own ship, which the existing block already
  handles), send a `FM_S_SHIP_STATUS(90)` throttled to once per
  `TeammateStatusRefreshTicks` per (recipient, subject) pair.
- Logged as `[teamshipstatus] peer <recipient> <- ship <subject> sector <s> [DOCKED@<station>| flying]` (Server.cs:449-451), matching the file's bracket-tagged style.
- **Reuses `MakeOwnShipStatus` unchanged** (Server.cs:447) — its signature already
  takes an arbitrary `(short shipId, LiveShip live)` pair rather than assuming the
  receiving peer's own ship, so no duplication of the status-assembly logic.

Scope limit: **same-side only.** The reference has no visibility / fog-of-war
restriction at all; same-side is the minimum this project's own use case needs
and deliberately avoids opening enemy-tracking questions. The `SideId` gate is
`tv.Value.Ship.SideId != recipientSide → continue` (Server.cs:442).

Out of scope and untouched: `CommanderResearchAndExpand.cs`,
`ScoutExploreMap.cs`, any native repo file, the CS_PING echo fix (already
landed), and dispatch logic itself.

## Proven (loopback, no VM)

All 3 consecutive full-suite runs green (2263/2263 each). Added two tests in
`tests/Allegiance.Core.Tests/ShipStatusServerTests.cs`:

1. **`SameSideShips_are_broadcast_to_each_peer_but_enemy_ships_are_not`**
   (ShipStatusServerTests.cs:132) — three bots join a 2-team map (default spawn
   round-robins sides, guaranteeing a same-side pair and a cross-side odd-one-out).
   Each peer's client receives a `SHIP_STATUS` about every ship on its own side
   (self via the pre-existing path, teammates via the new path) and **never**
   receives a status about a ship on the opposing side. Nobody moves, nobody
   polls — visibility arrives purely from the periodic broadcast.

2. **`DockedState_propagates_to_same_side_peer`** (ShipStatusServerTests.cs:202)
   — two peers forced onto one side via `ShipSpawner`; one is docked server-side
   with a real station id; the other decodes a `SHIP_STATUS` for it carrying
   `State = c_ssDocked` and the real `StationID` — the exact datum
   `UndockMinersAndStartMining` needs (not just that a miner exists, but that it
   is docked).

Baseline reconfirmed before the change: `dotnet test` = 2261 passed, 0 failed
on `master` at `9f682b5`.

## What Claude Code's own live-VM pass should check next

End-to-end closure of the commander→miner→mine→sell loop, for the first time:

- Bring up a commander on one side and a `.Miner`-named ship on the **same**
  side, both as real clients. Let the miner dock at a friendly station.
- Watch the commander client: within one ~5 s teammate-refresh interval it
  should now receive `SHIP_STATUS(90)` for the miner ship with `State =
  c_ssDocked` and the real `StationID`.
- Confirm `UndockMinersAndStartMining`
  (`CommanderResearchAndExpand.cs:1555-1622`) finally finds that docked miner,
  dispatches it, and the miner→mine→sell loop actually runs.
- Sanity on the scope cut: a `.Miner` on the **opposing** side must not appear in
  the commander's status feed (same-side-only).

## Files touched

- `src/Allegiance.Server/Server.cs` (the only production file)
- `tests/Allegiance.Core.Tests/ShipStatusServerTests.cs` (tests only)
- `TEAMMATE_VISIBILITY_P58.md` (this doc)

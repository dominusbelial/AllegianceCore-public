# NPC_SHIP_FOUNDATION_P147

Task: ORDER_CHANGE(93) foundation arc — Stage 1. Prove an NPC ship can exist
and be seen by real players, with zero behavior and zero new broadcast code.

Repo: /home/domingo/Development/AllegianceCore, branch master (7028b66 P146 + P147).

## Verdict up front

**The "zero new broadcast code" hypothesis HELD.**

An ownerless NPC ship — a `Ship` constructed and placed into a sector with
**no** `_peerShips` entry, no connection, no `ExportShip`, and no `PilotType`
field — appears in a real connected `Bot` peer's live
`HEAVY_SHIPS_UPDATE`(176) stream, with its real placed position, using
**no new broadcast code whatsoever**. The encouraging reading from the plan
page was confirmed by execution, not just trusted.

The only code added is:
  1. `Srv.SpawnNpcShip(Mission, Sector, short sideId, Vector3 position)`
     — a minimal server primitive that builds the ownerless ship and places it.
  2. A live loopback test proving it.

Nothing in the broadcast path (`BroadcastShipsUpdate`, `Sector.
GetShipUpdateCandidates`, `HeavyShipUpdateEntry`) was modified.

## What was verified (with file:line read this session)

The three pieces the plan flagged as the enabling assumptions all check out:

1. **Placement is peer-independent.** `Mission.PlaceShip(ship, sector)`
   (`src/Allegiance.Core/Model/Mission.cs:347-358`) seeds the ship's clock,
   sets its Mission/Sector back-refs, and calls `sector.Add(ship)`. It touches
   nothing connection-related. `_peerShips` is purely the peer-connection
   wire-I/O registry (`Server.cs:58`); it is not the ship registry.
   `SpawnDefaultShip` (`Server.cs:2581-2615`) is the same pattern minus the
   `_peerShips` registration (registration happens after, in the logon path).

2. **Candidate selection is cluster-membership only.** `Sector.
   GetShipUpdateCandidates()` (`src/Allegiance.Core/Model/Sector.cs:290-302`)
   filters only on `ParentShip is null`, `!Dead`, `StationId is null`. It does
   **not** check peer ownership. Any sector-resident `Ship` qualifies.

3. **The broadcast reaches every peer in the sector.** `BroadcastShipsUpdate`
   (`Server.cs:6043`, called live each tick from `Server.cs:541`) iterates
   `_peerShips` as the **send targets**, and for each peer emits every entry of
   `sector.GetShipUpdateCandidates()` (minus self/minus command target) as a
   heavy record. The NPC — present in the candidate list — is therefore relayed
   to every seated peer in that sector automatically.

Because the heavy record carries **no pilot type** — `HeavyShipUpdateEntry` is
39 bytes: shipId, stateM, controls, CompactTime, CompactPosition,
CompactOrientation, CompactVelocity, turnRates, CompactShipFractions, power
(`src/Allegiance.Core/Wire/HeavyShipsUpdate.cs:7-26,50-178`) — the NPC is fully
representable on the wire without ever touching `ExportShip`'s
`PilotTypePlayer`-only assertion (`src/Allegiance.Core/Wire/ExportShip.cs:181`,
left untouched).

## The new code (honestly scoped)

`Srv.SpawnNpcShip(Mission mission, Sector sector, short sideId, Vector3
position)` (`src/Allegiance.Server/Server.cs:2620-2650`) — modeled exactly on
`SpawnDefaultShip`'s own pattern:
  - allocates the ship id from the **shared** `_nextShipId` counter
    (`Server.cs:59`) — same ShipID/ObjectID namespace as player ships, so no
    collision with any seated peer (all peer ids come from that same counter,
    `Server.cs:1107`);
  - constructs `new Ship(shipId, hull, position, zeroVel, Identity)`;
  - sets a real `SideId` (drones are side-owned in native);
  - calls `mission.PlaceShip(ship, sector)` and does **not** create a
    `_peerShips` entry;
  - returns the `Ship`.

**Honest call-site statement:** its only call site today is the P147 live
test. It is a real, callable, compiling server primitive — no fake production
call site was invented. Wiring a real NPC into production spawning is a later
stage (the plan's Stage 2+). Per scope, it deliberately has no weapon mount,
no `PilotType` field, no movement/AI/command behavior — it sits still.

## The live proof

`Ownerless_npc_ship_appears_in_peer_heavy_updates_with_position`
(`tests/Allegiance.Core.Tests/NpcShipFoundationLiveTests.cs`):

1. Boots a 1-bot live server (`ShipsUpdateLiveTests.cs` harness pattern, no
   VM); the peer sits at the origin in `Sectors[0]`.
2. Spawns the ownerless NPC via `SpawnNpcShip` at (950, 740, 880), side = the
   peer's own side.
3. Asserts the NPC has **no** `_peerShips` entry.
4. Pumps ticks, grabs the peer's first `HEAVY_SHIPS_UPDATE`.
5. Asserts the peer's heavy stream **contains the NPC's id**, and its decoded
   position matches the authoritative placed position within the port's
   standard ±1.0-unit `CompactPosition` tolerance (the same convention as
   `LightHeavyShipsUpdateCodecTests.cs:129`).
6. Asserts the peer still never receives its own ship.

Result: **PASSED** on first run (Duration 79 ms in the filtered run).

## The one accepted unknown (unchanged from the plan)

Whether a *real native* client would gracefully handle an un-exported shipId
(i.e. a heavy update for a ship it was never introduced to via `ExportShip`) is
still untested here — this port's own `Allegiance.Bot` harness decodes heavy
updates unconditionally (`Bot.cs:959-960`, no prior-knowledge check), so this
port's verification bar passes either way, and no live WinTrek pass is in this
arc's scope. Recorded honestly as an accepted unknown, not claimed solved.

## Diff scope

- Added: `SpawnNpcShip` (server helper) + `NpcShipFoundationLiveTests.cs` (test).
- Touched (broadcast/placement/ExportShip): none.

## Test suite

2726 baseline + 1 new = 2727 total; full suite green across 3 consecutive runs
(the four documented flakes were rerun to clean when they fired; none was a
regression from this change).

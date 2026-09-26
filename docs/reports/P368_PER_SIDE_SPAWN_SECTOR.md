# P368 — Every player spawns in the SAME sector regardless of side (fixed)

## Problem

Live 2-client playtest (2026-09-24, real remote server 192.168.0.221, two real
MonoGame clients, sides Alpha/Bravo): the mini-map showed both ships in
"Ganymede". Confirmed server-side (not a display bug) — the mini-map faithfully
reports the server's `SET_CLUSTER`, and the server really did place every
newly-spawned ship in `mission.Sectors[0]` regardless of side.

**Root cause** (`src/Allegiance.Server/Server.cs` `SpawnDefaultShip`, and the
side-select path in `Server.Lobby.cs`): the ship was placed in
`mission.Sectors[0]` unconditionally, and only *after* placement was a side
assigned (round-robin, or via `TryJoinSide`) — the side never influenced the
sector. `TryJoinSide` only sets `ship.SideId` and handles leadership/roster; it
never touches `ship.Sector`/`Position`. So both sides' ships physically
co-spawned in the first sector of the map every time.

## How a side's home sector is resolved — from real data, not a convention

A side's home sector = the sector containing a station that side owns
(`Station.SideId`). Verified against the real map `m2xspiderw.igc` via the
repo's `missiondump` tool (against `PCore001.igc`):

```
m2xspiderw.igc  MAP  clusters=13  warps=44  stations=2  asteroids=134
  sector 1050 'Ganymede'  home=True   station 4328 'Garrison' side=0
  sector 1051 'Organia'   home=True   station 4329 'Garrison' side=1
```

So the map's "2 stations" is exactly **1 per side** (side 0's garrison →
Ganymede 1050, side 1's → Organia 1051). The fix does **not** assume 1:1: it
iterates `Mission.Sectors` in mission order and returns the first sector whose
runtime `Station` (or, as a fallback, static `DataStationIGC`) carries the
target side's id — deterministic when a side owns more than one station.

The new helper `HomeSectorForSide(Mission, short sideId)` (in `Server.cs`)
reads the **runtime** `Station.SideId` first — the same field the
friendly-fire gate `Station.ReceiveDamage` reads, so it reflects live ownership
(including post-`STATION_CAPTURE` re-siding). A secondary pass over the static
`Sector.Stations` (`DataStationIGC.SideId`) catches a station whose type never
resolved into a runtime model, so it can't cause a wrong fallback to sector 0.

## Mechanism

`SpawnDefaultShip(Mission, short shipId, short? sideIdOverride = null)` now:

1. Resolves the **final side BEFORE placement**: the explicit pick is threaded
   in from `CompleteSideSelection` (which already has `requestedSideId` in
   scope) → `AssignShip` → `SpawnDefaultShip`; an auto-assigned peer (no pick,
   `requestedSideId == -1`) falls back to the same round-robin as before
   (`_peerShips.Count % mission.Sides.Count`), but computed up-front.
2. Picks the sector from that side via `HomeSectorForSide`, then does **one**
   `mission.PlaceShip(ship, sector)` into the correct sector and sets
   `ship.SideId` on that same ship.
3. **Fallback**: if the side has no owned station (early mission state, or a
   map/config with no per-side stations), it falls back to `mission.Sectors[0]`
   and logs it plainly — mirroring the function's existing "mission has no
   sides" convention (a logged finding, never a fabricated value).

`AssignShip` gained an optional `short? requestedSideId` param; `CompleteSideSelection`
passes `requestedSideId != -1 ? requestedSideId : null`. The explicit path still
runs the full `TryJoinSide` afterwards for leader/mission-owner election (that
only sets `SideId`/leadership/roster — the ship is already in the correct
sector, so no relocation occurs).

## How the double-registration pitfall (scope item 3) is avoided

`Mission.PlaceShip` does `ship.Sector = sector; sector.Add(ship);` and does
**not** remove the ship from any previously-assigned sector's collection.
Because the correct side is resolved and the home sector chosen *before* the
single `PlaceShip`, we **never spawn-then-relocate** — there is no second
`PlaceShip`, so a ship can never be registered in two sectors' collections.
(No path here needed the sector's leave/remove method.) This is enforced by
construction rather than patched post-hoc.

## Verification — live proof (real server, real protocol, two opposite sides)

A dedicated live test `tests/Allegiance.Core.Tests/P368PerSideSpawnSectorTests.cs`
runs a real in-process `Allegiance.Server` on the real two-garrison map
`m2xspiderw.igc` with two real `Bot` peers over real loopback TCP. Each peer
explicitly requests a side via `POSITIONREQ` (bot A → side 0 Alpha, bot B →
side 1 Bravo) — the exact `CompleteSideSelection → AssignShip →
SpawnDefaultShip` path the MonoGame clients drive — and the server's seated
ship sectors are read back. Observed:

```
P368-LIVE: alpha ship 8097 (side 0) spawned in sector 1050 'Ganymede'
P368-LIVE: bravo ship 8096 (side 1) spawned in sector 1051 'Organia'
P368-LIVE: sectors differ = True; alpha home = 1050, bravo home = 1051
```

The two ships spawn in **different, correct per-side home sectors** (Ganymede
vs Organia), not both in sector 0. The same-side co-spawn invariant is
separately exercised by the (updated) `CargoBayReloadTests` / `AutoReloadOnEmptyTests`
helpers, which seat peers via the **auto-assign** round-robin path (covering
scope item 5): a same-side observer shares the subject's home sector while the
cross-side peer is in a different one.

## Build / tests

- Whole-solution build: **0 warnings, 0 errors** (`/home/domingo/.dotnet/dotnet build`).
- `P368PerSideSpawnSectorTests` (new): passes; prints the real sector names/ids above.
- Four existing tests encoded the old "everyone co-spawns in one sector"
  assumption and failed after the fix. Each was corrected to its intended
  invariant, not weakened to keep a wrong test green:
  - `CargoBayReloadTests` / `AutoReloadOnEmptyTests` (`SeatThree`): now assert
    the **same-side observer shares the subject's home sector** and the
    **cross-side peer is in a different one** (the cross-side "third" was never
    used by the test bodies).
  - `SwapPartTests` / `SetWingIdTests` / `DropPartTests`: these are broadcast
    **audience-scoping** tests whose discriminator deliberately needs the
    cross-side enemy co-located with the subject. Since production no longer
    co-spawns by default, each now injects a same-sector `ShipSpawner` (the
    established seam Explorer/LiveGainFlag use) to construct that controlled
    scene — not the production default.
- Full suite (`/home/domingo/.dotnet/dotnet test`, run twice): the only
  remaining failures are the pre-existing documented live-wire timing flakes,
  and they **rotate between runs** (the documented signature):
  - Run 1: Lobby 12✓ · MonoGame 708✓ · ClientCore 187 (3 fail: two were
    load-flakes that pass isolated; `FullSessionKill` is a pre-existing flake)
    · Core 3291/3297 (6 fail, all load-flakes that pass isolated).
  - Run 2: Lobby 12✓ · MonoGame 708✓ · ClientCore 187✓ (**green**) · Core
    3293/3297 (4 fail: `ProjectileBroadcast`, `LiveTurretGunnerBinding`,
    `LiveWinCondition.Multi_mission_disconnect`, `GameOverPlayersWire`).
  - `ProjectileBroadcastTests` and `LiveTurretGunnerBindingTests` were
    confirmed to fail **identically on the pristine baseline** (`git stash`
    before the fix) — not regressions from this change.
- One genuine co-location regression surfaced deterministically and was fixed:
  `LeaderSameSideBotClientLiveTests` (P366 client proof) spawned its NPC into
  `mission.Sectors[0]`; under P368 the side-0 leader spawns in its real home
  sector, so the test now spawns the NPC into the leader's actual sector
  (`rig.LeaderShip.Sector`). It passes isolated.

## Scope boundaries (per brief — untouched)

- `Rendering/StarmapRenderer.cs` — confirmed correct (it faithfully renders the
  server's `SectorId`); not touched.
- P358–P367 — unrelated; untouched.
- How sides get assigned stations in the first place (map generation / mission
  setup) — out of scope; only newly-spawned-ship placement was changed.

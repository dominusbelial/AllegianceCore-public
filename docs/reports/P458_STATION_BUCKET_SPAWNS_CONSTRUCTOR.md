# P458 — Investing in a station type produces an AI constructor drone

Port of native `FedSrvSiteBase::StationTypeCompleted`
(`FedSrv/src/FedSrv/FedSrv.CPP:11343-11372`): a team that invests in (funds) a
station-type bucket spawns an **AI constructor drone** that requests an
asteroid and waits for a Build order — the missing half of the expansion loop
Domingo flagged in the 2026-09-29 gameplay-parity pass ("I don't have to use
any of the builders, so there is no tech development").

Together with the already-landed pieces — P189 (development buckets +
`DevelopmentCompleted`), P425 (command an NPC to BUILD), P444 (building techs
from owned stations) — this closes the "invest in a station type → a
constructor appears and can be ordered to build" chain.

## Pre-fix failure (recorded)

Against the unfixed port there is **no station-type bucket at all**:
`Bucket.ForSide` emitted only the synthetic ship-type bucket (id 1) plus the
P189 development buckets; `OnBucketCompleted` acted only on
`BitDevelopment`. There was no `BucketItemType.BitStationType` to reference,
and `SpawnNpcShip` gave every NPC the core's lowest-`HullId` hull regardless of
drone type.

The recorded failure is the new loopback test
(`Completing_station_type_bucket_spawns_waiting_constructor_and_build_order_builds`)
run against the pre-fix catalog (station-type creation disabled, enum kept so
the test compiles) — the test's very first lookup finds no bucket to fund:

    System.InvalidOperationException : Sequence contains no matching element
    Failed Allegiance.Core.Tests.StationBucketSpawnsConstructorLiveTests.

That is the "no bucket exists" pre-fix symptom the brief anticipated. Even had
a station-type bucket existed, the pre-fix completion handler fell through to
the P119 detection-only log ("nothing produced"), and the spawned ship would
have worn the wrong (lowest) hull.

## What was built

1. **Real drone hull in `SpawnNpcShip`** (`src/Allegiance.Server/Server.cs`).
   When a drone type is given, the spawned ship now uses that drone type's
   `HullTypeId` (native `CreateStockDrone` uses
   `pdronetype->GetHullTypeID()`, `FedSrv.CPP:2228`), falling back to the
   lowest-`HullId` hull only when no drone type is given or its hull id is
   absent from the core. **This changes the initial miners' hull** (they now
   wear the real `.Miner` drone hull 3). The initial-miner tests still hold
   unchanged — they assert pilot type / station / sector, never the exact hull
   (`InitialMinerSpawnLiveTests`), and other NPC-spawn tests that cared about a
   specific hull already call `ChangeHull` immediately after `SpawnNpcShip`.
   Verified by `SpawnNpcShip_uses_the_drone_types_real_hull`.

2. **Station-type and drone-type buckets in `Bucket.ForSide`**
   (`src/Allegiance.Core/Model/Bucket.cs`). New `BucketItemType.BitDroneType`
   (4) and `BitStationType` (5), each wrapping the real decoded `DroneType` /
   `StationType` record (the same carrier pattern `Development` uses). Buckets
   are appended after the developments in native `CreateBuckets` order
   (developments, then drone types, then station types — `sideigc.cpp:204-255`),
   each gated on the side's reachable techs. Existing ids are untouched: the
   synthetic id 1 and the developments (starting at 2) keep their ids; the new
   buckets continue numbering after them. Drone-type buckets are **created
   only** — their completion effect is the explicit P459 follow-up, so
   `OnBucketCompleted` still treats them as detection-only.

3. **Station-type bucket completion** (`OnBucketCompleted` →
   `CompleteStationType` in `src/Allegiance.Server/Server.Economy.cs`), the
   port of `StationTypeCompleted`:
   - **Active gate** — nothing spawns for an inactive side (`pside->GetActiveF()`,
     `FedSrv.CPP:11348`).
   - **Spawn the construction drone** — the station type's
     `ConstructionDroneTypeId` via `SpawnNpcShip` at the side's home station
     (`ResolveSideHomeStation`, the same pattern `SpawnInitialMiners` uses;
     native `CreateStockDrone(...GetConstructionDroneType(), side, pstationtype)`).
   - **Assign the base data** — `Ship.BuilderStationType` = the completed
     station type (the port's stand-in for native `SetBaseData`, what the
     drone builds).
   - **No auto-build** — native rewrites the constructor's default `c_cidBuild`
     to a Goto-to-that-rock + queued DoNothing (`FedSrv.CPP:11358-11366`); the
     port collapses that to the `Ship.WaitsForBuildOrder` gate.
   - **Start undocked** — native docks then immediately undocks
     (`SetStation(pstation)` then `SetStation(NULL)`); the port spawns it
     already-undocked at the home station position (the native post-undock end
     state).
   - **Request a rock** — the side-team chat "New constructor requesting
     asteroid." with the station type's `ConstructorNeedRockSound` as the voice
     id (`FedSrv.CPP:11369-11370`), broadcast to every peer on that side in the
     mission.

4. **No-auto-build gate** (`src/Allegiance.Core/Sim/Ship.cs`). New
   `Ship.WaitsForBuildOrder` flag (default `false`); when set,
   `DecideBuilderDefaultOrder` stands by instead of starting a build — the
   port collapses the native "fly to the rock then DoNothing" to simply not
   starting the construction, since this port's builder mechanic abstracts the
   travel phase away (no travel-before-build gate). A subsequent P425 Build
   order at a compatible rock starts construction via the shared
   `StartBuildIfLegal` body. A player-flown or pre-existing Builder is
   unaffected (flag defaults off).

## Bucket list created (observed, PCore001 + XXV_simple)

The bucket catalog is per-faction: each civ's reachable-tech set admits exactly
that faction's station and drone types (faithful to native — Iron can invest in
Iron's techbase, not Rixian's). Observed:

**Side 0 (Iron, civ 1):**

| kind | count | ids |
|---|---|---|
| BitShipType (synthetic) | 1 | 1 |
| BitDevelopment | 296 | 2–297 |
| BitDroneType | 5 | 298–302 (.Miner h3, .TowerAC h4, .Carrier h6, .TowerSC h4, .Caltrop h223) |
| BitStationType | 6 | 303–308 (Outpost 4, Garrison (Str) 18, Refinery 25, Garrison 26, SpecMine (He) 27, Teleport 28) |
| **total** | **308** | |

**Side 1 (Rixian, civ 3):** 1 ship-type (1), 293 developments (2–294), 6
drone-types (295–300), 6 station-types (301–306) = **306**.

(Counts measured live by booting the in-process server and grouping
`side.Buckets` by item type; the drone/station names are the wrapped
`DroneType`/`StationType` records, hulls are `HullTypeId`.)

## Tech-gate (reachable-techs) — native `ttbmLocalUltimate` mirrored

`BucketCatalog.ReachableTechs` reproduces native `CsideIGC::CreateBuckets`'
`ttbmLocalUltimate` (`sideigc.cpp:119-175,206`) with every term sourced from
the decoded core:

- `ttbmLocalUltimate` seeded with the OR of **every station type's
  `TtbmLocal`** (`sideigc.cpp:142`);
- `m_ttbmUltimateTechs` = the civ's base techs (`initialDevelopmentTechs`),
  OR-ed with every **part type's** effect techs (`core.PartTypes`, which the
  loader already bridges the launcher forms into) and every **station type's**
  effect techs (`sideigc.cpp:123-133`), then grown to a fixed point by the
  development buy-closure (`sideigc.cpp:149-173`);
- the single gate mask = `ttbmLocalUltimate | m_ttbmUltimateTechs`
  (`sideigc.cpp:206`), tested against each drone type's / station type's
  `RequiredTechs` (`<=` = `IsSubsetOf`).

**No native term is omitted.** The only divergence from a literal read is a
mapping, not a simplification: this port's loader splits `OT_partType` into
full `DataPartTypeIGC` records (containing the launcher-form parts bridged in
with their effect techs) plus a compact launcher-only list; the reachable walk
uses `PartTypes`, the faithful analogue of native's single `GetPartTypes()`
walk. The gate is intentionally permissive like native's (the ultimate includes
every faction's station effect techs), and each faction's bucket list is what
its own civ's base techs plus that union make reachable.

## Constructor-type table (scope item 4)

Confirmed against PCore001: **every one of the 143 station types'**
`ConstructionDroneTypeId` **resolves to a Builder-pilot (pilot==6) drone type**
— no station type names a missing or non-Builder construction drone.

The construction drone types are the "Constructor (Sml)"/"Constructor (Lrg)"
family (plus each faction's named variant); the reachable Iron-Coalition
examples actually exercised by the test invest in **Outpost (st 4) →
Constructor (Sml) dr 6 → hull 12 'Sm Std Constructor'**. The distinct
construction drone type → hull mapping (small/large constructors by faction):

| Constructor drone type | pilot | hull | |
|---|---|---|---|
| 5, 10, 16, 26, 31, 53, 59 | 6 (Builder) | 9, 45, 110, 217, 182, 719, 819 | Large variants |
| 6, 11, 15, 27, 30, 54, 60 | 6 (Builder) | 12, 48, 97, 271, 179, 770, 870 | Small variants |
| 326/327, 426/427, 526/527, 626/627 | 6 (Builder) | 319/370, 419/470, 519/570, 619/670 | Faction small/large pairs |

All 143 station types fall into one of these Builder construction-drone rows.

## No-auto-build implementation

`Ship.WaitsForBuildOrder` is the smallest gate. In
`DecideBuilderDefaultOrder`, once the flag is set the builder logs "standing
by — awaits a Build order" and returns without calling
`Sector.StartBuildingEffect`. This is the port's collapsed form of native's
Goto-the-rock + queued DoNothing override: the constructor neither starts a
build on its own nor registers one, and remains commandable. The loopback test
proves the sequence: spawn → no auto-build over ~10 sim-seconds → a P425
`BUILD` at a matching rock starts construction.

## Verification

- `tests/Allegiance.Core.Tests/StationBucketSpawnsConstructorLiveTests.cs`:
  `Completing_station_type_bucket_spawns_waiting_constructor_and_build_order_builds`
  (the full arc — funds a real station-type bucket via the P189
  `FundAndComplete`/`SendDonate` loopback, asserts the spawned Builder NPC with
  the construction drone type's real hull and right `BuilderStationType`, the
  side-team "New constructor requesting asteroid." chat with
  `ConstructorNeedRockSound`, that it does **not** build within ~10 s, then that
  a P425 Build order at a matching rock starts construction); and
  `SpawnNpcShip_uses_the_drone_types_real_hull` (the drone-type → real-hull
  contract, plus the no-drone lowest-hull fallback).
- Build with `/home/domingo/.dotnet/dotnet build`: **0 warnings, 0 errors**.
- Targeted impacted suites (buckets, development, prosperity, direct-drone
  build, builder default order, initial miners, layer): 28/28 pass.
- Full `Allegiance.Core.Tests` ×3 consecutive runs + whole-solution
  `dotnet test`. The new P458 tests passed in every run; the only failures are
  the repo's documented live-wire timing flakes, which **rotate run to run** and
  none reference P458's code paths:
  - **Persistent (documented)**:
    `ConquestTerritoryLiveTests.*` (3), `GameOverPlayersWireTests.Conquest_*` (2),
    `LiveTurretGunnerBindingTests` (1) — 10 s live-wire timeouts.
  - **Rotating across runs**:
    `AddPartTests` (pickup timing), `LiveRipcordTests`,
    `LiveChatSectorWireTests`, `WarpBombTests`,
    `Transport.TcpLoopbackProofTests` (session-loss race),
    `AsteroidsUpdateLiveTests.Mining_depletion...` (ore-sample race),
    `AutoReloadOnEmptyTests` (seating race),
    `JoinFlowLiveTests`, `ShipDeleteGoneLiveTests`.
  None involve buckets, station-type/drone-type spawns, or spawned-NPC hulls, so
  none are attributable to P458.

## P285 decision flag (for Domingo)

Native constructors are AI drones (`c_ptBuilder`, created only by
`CreateStockDrone`); this pass found **no native path where a player buys and
flies a builder**. The P285 player-flyable builder (`SetBuilderTargetOnShip`,
`Server.ShipCommands.cs`) therefore looks like a **port invention**. Per scope
it was left **unchanged** — whether to keep, restrict, or retire it is a
decision for Domingo (candidate: P470 in the prompt queue is titled "remove
player-flyable builder"). This task's constructors are the native-style AI
drones and do not depend on P285.

## Files

- `src/Allegiance.Core/Model/Bucket.cs` — `BitDroneType`/`BitStationType`,
  `DroneType`/`StationType` carriers, `ForSide` appends both after
  developments, new `ReachableTechs` helper.
- `src/Allegiance.Server/Server.cs` — `SpawnNpcShip` uses the drone type's real
  hull.
- `src/Allegiance.Server/Server.Economy.cs` — `OnBucketCompleted` resolves the
  owning side once then branches by type; new `CompleteStationType` +
  `BroadcastConstructorNeedRock`.
- `src/Allegiance.Core/Sim/Ship.cs` — `WaitsForBuildOrder` flag + gate in
  `DecideBuilderDefaultOrder`.
- `tests/Allegiance.Core.Tests/StationBucketSpawnsConstructorLiveTests.cs` — the
  two new live loopback tests.

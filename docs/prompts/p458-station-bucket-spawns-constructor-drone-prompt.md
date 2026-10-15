# P458 — investing in a station type produces an AI constructor drone

## Why this task exists

From the 2026-09-29 gameplay-parity pass. Domingo: "I don't have to use any
of the builders, so there is no tech development." In the original, a
team expands by **investing in a station type**. That produces an **AI
constructor drone**, which asks for a rock, and the commander orders it to
build. The port has neither half.

- **Native completion:** `FedSrvSiteBase::StationTypeCompleted`
  (`~/Development/Allegiance/src/FedSrv/FedSrv.CPP:11342-11367`) calls
  `CreateStockDrone(pstationtype->GetConstructionDroneType(), side, pstationtype)`.
  - The station type becomes the drone's base data, which is what it will
    build.
  - The drone starts at the station, calls `PickDefaultOrder`, and undocks.
  - **"Don't auto-build"**: if the default order is `c_cidBuild`, it is
    rewritten to goto that rock and queue DoNothing (`:11356-11364`).
  - It sends a team chat with the station type's
    `GetConstructorNeedRockSound()`: "New constructor requesting asteroid."
    (`:11366-11367`).
- **Native `CreateStockDrone`** (`FedSrv.CPP:2220-2250`) uses the **drone
  type's own hull** (`pdronetype->GetHullTypeID()`) and its
  shoot/move/bravery skills.
- **Native bucket creation:** `CsideIGC::CreateBuckets`
  (`~/Development/Allegiance/src/Igc/sideigc.cpp:113-310`) creates a
  bucket for every development that isn't obsolete, then every **drone
  type** (`:205-231`) and every **station type** (`:233-255`) whose
  `RequiredTechs <= ttbmLocalUltimate`, then paid hulls and parts, in that
  order.
- **Port:**
  - `Bucket.ForSide` (`src/Allegiance.Core/Model/Bucket.cs:270-330`)
    creates only one synthetic drone-hull bucket (id 1) plus development
    buckets. **Station-type and drone-type buckets don't exist.**
  - `OnBucketCompleted` (`src/Allegiance.Server/Server.Economy.cs:633-664`)
    acts only on development buckets; anything else is logged and builds
    nothing (the P119 detection-only boundary).
  - `SpawnNpcShip` (`src/Allegiance.Server/Server.cs:~680-735`) gives
    **every** NPC the core's lowest-`HullId` hull instead of the drone
    type's `HullTypeId` (`DataDroneTypeIGC.HullTypeId`,
    `Core/Data/DataDroneTypeIGC.cs:20-36`).
  - The needed data is already decoded: `DataStationTypeIGC.ConstructionDroneTypeId`
    and `ConstructorNeedRockSound` (`Core/Data/DataStationTypeIGC.cs:47-48, 286-287`).
  - The command side already exists: P425 `HandleDirectNpcBuildOrder`
    (`Server.ShipCommands.cs:1531`) lets a player order a Builder NPC to
    build at an asteroid.
  - The builder NPC's default order `DecideBuilderDefaultOrder`
    (`src/Allegiance.Core/Sim/Ship.cs:5728+`) **auto-builds** at the
    least-targeted rock. That is correct for native `PickDefaultOrder`, but
    native explicitly overrides it for bucket-spawned constructors.

## Scope

1. **Real drone hull.** `SpawnNpcShip` uses the drone type's
   `HullTypeId` when a drone type is given, and keeps the current fallback
   only when there isn't one. This changes the initial miners'
   hull. Check that the initial-miner tests still hold, or update them to
   the real hull, and say which.
2. **Create station-type AND drone-type buckets** in `Bucket.ForSide`,
   appended **after** the developments in native order (drone types, then
   station types), each gated on `RequiredTechs <= ` the side's reachable
   techs.
   - Mirror native's `ttbmLocalUltimate` as closely as the port's existing
     `DevelopmentLogic` allows, and document any simplification.
   - Existing bucket ids (the synthetic id 1 and developments) must not
     change.
   - Drone-type buckets are **created only** here. P459 gives them their
     completion effect.
3. **Station-type bucket completion** in `OnBucketCompleted`, porting
   `StationTypeCompleted`:
   - Spawn `ConstructionDroneTypeId` via `SpawnNpcShip` at the side's home
     station (the port's buckets carry no station; use the
     `ResolveSideHomeStation` pattern `SpawnInitialMiners` uses).
   - Set `Ship.BuilderStationType` to the completed station type.
   - Apply native's **no-auto-build** rule: the constructor travels to the
     rock it would have picked and then **waits** for a Build order. Add
     the smallest flag/gate for this on the builder default order. Don't
     change what a player-flown or pre-existing builder does.
   - Send the side-team chat "New constructor requesting asteroid." with
     the station type's `ConstructorNeedRockSound` as the voice id.
   - Only when the side is `Active` (native `pside->GetActiveF()`).
4. Confirm in PCore001 that every buyable station type's
   `ConstructionDroneTypeId` resolves to a Builder-pilot drone type, and
   list them in the report.

## Explicitly out of scope

- **Drone-type bucket completion** (miners/wingmen/layers/carriers) → P459.
- **The P285 player-flyable builder** (`SetBuilderTargetOnShip`,
  `Server.ShipCommands.cs:2038-2063`) stays exactly as it is. Native
  constructors are AI drones (`c_ptBuilder`, created only by
  `CreateStockDrone`), and this pass found no native path where a player
  buys and flies a builder. So P285 looks like a port invention. Whether
  to keep it, restrict it or retire it is **a decision for Domingo**. Do
  not remove it; flag it in the report.
- The client investment UI (donate/progress screen). The loopback test
  funds through `BUCKET_DONATE` directly, as the P189 tests do.
- `GetSuccessorStationType` upgrade-at-build-time and the asteroid-type
  construction discount.
- Hull/part buckets.

## Verification

- A loopback test reusing P189's pattern (`FundAndComplete` in
  `tests/Allegiance.Core.Tests/TechTreeDevelopmentLiveTests.cs:147+`,
  a `FltBot` sending `SendDonate`): fund a real station-type bucket to
  completion. Assert that:
  - a new NPC appears on that side with `PilotType == Builder`, the
    construction drone type's real hull, and the right `BuilderStationType`
  - the side receives the request-rock team chat
  - the constructor does **not** start building on its own within N seconds
  - a P425 Build order at a matching rock then starts construction

  **Run against unfixed code first and record the failure** (no bucket
  exists, or nothing spawns).
- A `SpawnNpcShip` hull test: a drone type with a known `HullTypeId` → the
  spawned ship has that hull.
- Build with `/home/domingo/.dotnet/dotnet build`: 0 warnings, 0 errors.
- Full suite with `/home/domingo/.dotnet/dotnet test`, 3 consecutive clean
  runs. Only documented live-wire flakes are acceptable; name any you see.

## Deliverables

1. The fix and tests, committed on **`master`**, not pushed. Check
   `git branch --show-current` first.
2. Report doc `docs/reports/P458_STATION_BUCKET_SPAWNS_CONSTRUCTOR.md`.
   Cover the pre-fix failure, the bucket list created (count and ids),
   the tech-gate simplification, the constructor-type table, the
   no-auto-build implementation, and the P285 decision flag for Domingo.

# P382 — Initial Miner Spawn (docked at home, at mission start)

Status: LANDED on `master` (unpushed — Domingo pushes). Depends on **P381**
(docked-NPC auto-launch) and **P379** (debug-mode test topology), both now on
master (P379 was rebased onto current master incl. P381 and merged as a
prerequisite for this task).

## What this adds

At mission boot, for **every** real game (NOT gated on `debugMode`), each side
gets its stock miner drone spawned **docked at its home station**, mirroring
native's "Create miners for each side in the sector with their home starbase"
block (`FedSrv/fsmission.cpp:2196-2217` → `CreateStockDrone` →
`CreateDrone`, `Igc/common.cpp:2407-2484`). With P381's `RunNpcAutoLaunch`
pass already in place, each docked miner auto-undocks on its own after the ~10 s
cooldown and the existing decision loop begins mining completely unprompted —
the behaviour Domingo reported missing ("I'm expecting them to undock and just
start working... I never spawned as a miner, I had to defend or destroy them").

Implementation: `Server.SpawnInitialMiners(Mission)` +
`Server.ResolveSideHomeStation(Mission, short)`, called from `Boot` once
`_missions` is assigned (see the Boot call site in `Server.Lifecycle.cs` for
why it cannot run inside `InstantiateGames` — `SpawnNpcShip`'s ship-id
allocator `AllocateShipId → IsShipIdLive` walks `_missions`, which is null
while the mission batch is still being built).

### 1. Drone type — resolved from real core data, not a hardcoded id

The lowest-`DroneTypeId` entry in `mission.Core.DroneTypes` whose
`PilotType == Ship.PilotTypeMiner` (native's `c_ptMiner`) is chosen
deterministically. Against `PCore001.igc` that is **drone type 1** (one of the
eleven race-specific `.Miner` drone types; lowest id wins). The chosen id is
passed straight through `SpawnNpcShip`'s existing `droneTypeId` convention
(the P160 established seam), which sets the ship's real `PilotType`.

**Hull note (documented divergence, not a guessing error):** `SpawnNpcShip`
itself assigns the lowest-`HullId` hull regardless of drone type — a
pre-existing, P160-documented simplification. On PCore001 that resolves to
hull 1 `Adv Miner`, itself a valid miner hull with the cargo the mining loop
needs, so the spawned ship is a fully-functional miner even though it isn't
the drone type's exact per-race hull (`Miner`, hull 3). I did **not** change
`SpawnNpcShip`'s hull selection: dozens of existing Layer/Wingman/Builder live
tests pass a non-null `droneTypeId` and would be behavior-changed by it, and
the brief explicitly says "reuse exactly".

A core with no Miner-pilot drone type spawns none (native would too).

### 2. Spawn count — documented default constant (real-offset path explained)

The count comes from `DefaultInitialMinersPerTeam = 1` in
`Server.Lifecycle.cs`, matching native's own default
(`MissionParams.nInitialMinersPerTeam = 1`, `Igc/igc.h:1330`, consumed at
`fsmission.cpp:2205`).

**Why not the real `misparms` byte offset:** locating the field's offset in the
native `MissionParams` struct is straightforward (it is the byte after
`nMaxPlayersPerTeam`), but reading it in this port would always yield **0** —
the port's `MissionDef` treats the whole `misparms` block as an opaque 252-byte
blob (`Wire/MissionDef.cs:63`) and only populates the join-path fields
(`szIGCStaticFile`, `verIGCcore`, `nTeams`, `rgCivID`); everything else,
including `nInitialMinersPerTeam`, is zero-filled because the port carries no
real per-mission param data (there is no parsed `.mis` file). So the honest
"real offset" is 0 → no miners. Decoding the full `MissionParams` struct
field-by-field is explicitly out of scope for P382, so a small, clearly-named,
documented default constant (1, native's default) is used instead. Tune the
constant (or promote to a config field) to change the initial per-team count.

Each side that has no resolvable home station spawns none (no forced nonzero
default — native iterates the side's own station list, so no station ⇒ no
miners).

### 3. Docked at home, handed to P381

Each spawned ship is latched `StationId = home` (native `SetStation(pstation)`)
and stamped `LastDockTime = _simNow`, exactly as P381's dock latches do — so a
docked-from-birth miner carries a real dock clock (with the P381 default
`NegativeInfinity` the ~10 s cooldown would read as elapsed and it would
undock instantly). The per-tick `RunNpcAutoLaunch` pass then undocks it on its
own and mining begins. Home station position is the dock position.

### 4. Debug-mode home resolution (reuses P379, doesn't duplicate)

`ResolveSideHomeStation` is debug-aware: when `liveSettings.debugMode` is
active it resolves to the **synthetic** Omega/Plato garrisons P379 injects
(`OmegaDebugStationId` side 0 / `PlatoDebugStationId` side 1), so debug testing
shows miners at Omega/Plato — never the real, now-unused Ganymede/Organia
garrisons. Normal mode reuses the established `HomeSectorForSide` +
first-side-owned-station resolution (the P368/P379 shape). The synthetic
station ids are the P379 constants themselves; no second copy of P379's logic.

### 5. Test-facing opt-out (new seam)

`Server.SpawnInitialMinersAtBoot` (default `true`) is a test seam, not a
gameplay toggle: narrowly-scoped live fixtures that spawn their own NPCs into a
pristine arena (single-rock mining/contention arbitration, cache-cleanup with
occupant-free home stations) set it to `false` before `Boot` so a wandering
initial miner can't perturb their precise assertions. It is deliberately
separate from `debugMode` and never read by the per-mission spawn itself.

## Regression work (brief item 3)

Introducing a boot-time initial miner (an extra persistent NPC subject/actor in
every mission with a side-owned home station) legitimately broke four tests.
All were stale assumptions about a world with no stock miners; each was fixed:

1. **`MiningTests.Loopback_default_spawn_is_unaffected_and_statuses_stay_normal`**
   and **`DockDockingTests.Ship_nowhere_near_a_station_is_unaffected`** asserted
   *every* SHIP_STATUS a peer hears is Flying/StationId -1. The bot now also
   hears the docked initial miners' (correct) Docked statuses. Scoped both
   assertions to the player's **own** ship id — the tests' original "ordinary
   player flight is unaffected" intent.
2. **`NpcCrossSideContentionLiveTests.Enemy_contender_evicts_everyone...`** — the
   initial miner joining the single-rock arena reshuffles P167 cross-side
   arbitration, violating "none mines / never redirected". Fixture opted out of
   initial miners (pristine arena preserved).
3. **`ConnectionCacheCleanupTests.Connection_scoped_caches_drain_to_zero...`** —
   the M1 drain-to-zero invariant counted a residual `_teammateLoadoutCache`
   entry keyed by a **persistent initial-miner NPC subject** (NPCs never
   depart). That is bounded by NPC count, not connection churn — the test's real
   concern is unaffected — but its zero assertion assumed no persistent NPC
   subjects. Fixture opted out.
4. The structurally-identical single-rock precision fixtures
   (`NpcAsteroidContentionLiveTests`, `MinerDecisionCycleLiveTests`,
   `NpcAutoLaunchLiveTests`, `NpcCrossClusterLiveTests`,
   `NpcCrossClusterMinerDecisionLiveTests`) were also opted out to prevent the
   same latent flake.

**P379 defect fixed as part of landing it:** `ApplyDebugTestTopology` hardcoded
m2xspiderw's sectors/warps and **threw** for any other map with
`debugMode=true` — which broke the P378 endurance test (`Debug_mode_defaults_to_1800s_endurance`
boots XXV_simple + debug). Added a guard: if the map lacks the m2xspiderw
Omega/Plato/Alderan sectors (matched by id AND name), skip the topology
gracefully (debug tunables still apply). m2xspiderw gets the full P379 topology
unchanged.

## Verification

- **Whole-solution build** (`dotnet build AllegianceCore.slnx -c Debug`):
  0 warnings, 0 errors.
- **Live, normal mode** (real `Allegiance.Server` boot against `m2xspiderw.igc`,
  `debugMode` unset):
  ```
  [initial-miner] side 0 spawned initial miner #1/1 ship 8096 (hull 'Adv Miner', droneType 1) docked at station 4328 'Garrison' in 'Ganymede'
  [initial-miner] side 1 spawned initial miner #1/1 ship 8097 (hull 'Adv Miner', droneType 1) docked at station 4329 'Garrison' in 'Organia'
  ```
  Each side's real home garrison (Ganymede 4328 / Organia 4329) holds one docked
  miner at game start.
- **Live, debug mode** (`liveSettings.debugMode=true`, same map):
  ```
  [initial-miner] side 0 spawned initial miner #1/1 ship 8096 (hull 'Adv Miner', droneType 1) docked at station 24465 'Omega Debug Garrison' in 'Omega'
  [initial-miner] side 1 spawned initial miner #1/1 ship 8097 (hull 'Adv Miner', droneType 1) docked at station 24466 'Plato Debug Garrison' in 'Plato'
  ```
  The synthetic garrisons (24465/24466 = wrapped P379 ids 90001/90002), in
  Omega/Plato — not the real Ganymede/Organia.
- **Automated live tests** (`InitialMinerSpawnLiveTests`, real in-process server +
  real loopback TCP, m2xspiderw), run repeatedly green:
  - `Normal_mode_spawns_one_docked_miner_per_side_at_real_home_and_auto_undocks` —
    asserts 1 miner/side docked at 4328/4329 in Ganymede/Organia, then both
    auto-undock (P381) with no input.
  - `Debug_mode_spawns_docked_miners_at_synthetic_omega_plato_not_real_homes` —
    asserts 1 miner/side at the synthetic garrisons in Omega/Plato, then
    auto-undock and each targets a He3 asteroid in Alderan (1052).
  - The docked-miner → auto-undock → **mine** progression is further proven by
    P381's own `NpcAutoLaunchLiveTests` (docked NPC miner undocks and mines in a
    controlled sector). Manual live-client radar observation wasn't run from this
    environment; the automated tests exercise the real server's full wire path.
- **Full test suite** (`dotnet test`, run 2x): 3366 tests, ~3362 passing each run.
  The only failures are the documented pre-existing **live-wire timing flakes**
  (a rotating subset each run — Chat/Turret/Multi-mission-disconnect/
  ShipsUpdate/Ripcord/WarpBomb/WinCondition; `LiveTurretGunnerBindingTests` is
  the one consistent failure and was **proved to fail on the pristine tree**
  via stash-and-rerun). No P382-related test (mining, contention, dock, status,
  P378, P379, cache-cleanup) failed in either run.

## Out of scope (respected)

No full `misparms` struct decode beyond this one field's status (explained
above); no player "buy a miner drone" purchase flow; no Wopr-style
`CommanderResearchAndExpand` economic dispatcher; no `ResolveMining` changes;
P368–P381 only reused (P381 `LastDockTime`, P379 home-station resolution).

Files changed (unpushed on `master`):
- `src/Allegiance.Server/Server.Lifecycle.cs` (spawn + resolution + opt-out +
  P379 map guard)
- `tests/Allegiance.Core.Tests/InitialMinerSpawnLiveTests.cs` (new, P382)
- `tests/Allegiance.Core.Tests/{MiningTests,DockDockingTests,
  NpcCrossSideContentionLiveTests,ConnectionCacheCleanupTests,
  NpcAsteroidContentionLiveTests,MinerDecisionCycleLiveTests,
  NpcAutoLaunchLiveTests,NpcCrossClusterLiveTests,
  NpcCrossClusterMinerDecisionLiveTests}.cs` (regression scoping / opt-outs)

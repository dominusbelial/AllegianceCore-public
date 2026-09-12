# NPC_DODGE_STATIONS_P170 — include stations in the dodge scan

Stage 2 of the NPC collision-avoidance arc (P168 → P170). Branch `master`,
top commit `d9e1181` (P169). Full suite **2807/2807** (P169 was 2805; +2 new
live tests), only the five documented flakes ever interrupted, each confirmed
green in isolation, my new tests clean in 3 isolated runs.

## What changed

`Ship.TryComputeDodgeGoal` (`src/Allegiance.Core/Sim/Ship.cs:2800`) gained a
third candidate category — **stations in the ship's own sector** — and the
"ignore my own goal" exclusion was extended to the station-typed `PlanTarget`.

### 1. Station data source — confirmed: `Sector.StationRuntimes`

Scanned `sector.StationRuntimes` (`src/Allegiance.Core/Model/Sector.cs:54`, the
runtime `Station` list), **not** the static `Sector.Stations`
(`Sector.cs:44`, a `IReadOnlyList<DataStationIGC>`).

Reason, verified by reading the records:
- `DataStationIGC` (`src/Allegiance.Core/Data/DataStationIGC.cs:21`) carries
  `Position` but **no radius** — radius lives on the resolved type
  `DataStationTypeIGC.Radius`.
- The runtime `Station` (`src/Allegiance.Core/Sim/Station.cs:85`) is the object
  that binds position and radius together: `Station.Radius` = `Type.Radius`
  (`Station.cs:145`, the decoded `DataStationTypeIGC.Radius`, which native
  assigns the collision sphere from, `stationIGC.cpp:249`), `Station.Center`
  = derived world position (`Station.cs:169-170`), and `Station.Data`
  (`Station.cs:98`) exposes the backing static `DataStationIGC` — exactly what
  the goal-exclusion needs.
- This mirrors the asteroid category's data source one-for-one: asteroids scan
  `Sector.Mineable` (the runtime `Asteroid` models, `Ship.cs:2855`), using
  `rock.Radius` + `rock.Data.Position`. Stations are the same shape — runtime
  models binding radius+position.

So `StationRuntimes` is both the faithful choice (real collision radii from the
resolved types, like `sector.Mineable`) and the one that carries the static
record needed for the `PlanTarget` comparison. The static `Stations` list alone
cannot supply a radius without a type lookup and would be a worse fit.

### 2. The new station scan (identical static-object treatment)

The station loop (`Ship.cs:2886-2898`) is the asteroid branch's shape exactly:
`solveForImpact`-based imminence with **`deltaV = −Velocity`** (stations don't
move; static like rocks — no new math), guarded by `station.Dead`
(belt-and-suspenders like the rock/ship passes). Winner selection and the away-
vector derivation fold the station in (`Ship.cs:2900-2907`).

### 3. Goal-exclusion extended to `PlanTarget`

`ignoreStation(st) => st.Data == PlanTarget` (`Ship.cs:2822`). `PlanTarget`
(`Ship.cs:678`) is the `DataStationIGC?` station-typed goal of Miner-selling /
Layer / Builder-adjacent flows (P151). Because `DataStationIGC` is a
`readonly record struct`, `==` is structural equality — robust against whether
the goal record is the same instance or a field-equal copy of the sector's
record. Without this, an NPC would dodge the very station it is docking at and
never arrive — the exact failure mode P168's doc comment already warned about
for the asteroid/ship case (`Ship.cs:2796-2798`), now closed for stations too.

## Why stations were wrong to exclude (native citations read this session)

- Native's **default** `Dodge()` scan is every pickable model in the cluster:
  `common.cpp:1713-1715` — `bShipsOnly ? pcluster->GetShips() :
  pcluster->GetPickableModels()`. The default `bShipsOnly` path is
  `GetPickableModels()`, which includes stations.
- The ships-only narrowing is **not** a general station exclusion — it is
  driven by `c_gpmDodgeShips` passed from `GotoPlan::SetControls`
  (`common.cpp:2001-2003`: `Dodge(pship, pmodelSkip, pstate, (gpm &
  c_gpmDodgeShips) != 0)`), where `gpm` comes from
  `GetGotoPosition(...)`'s phased fine-maneuver waypoint system
  (`common.cpp:1992-1997`). The default/idle call sites pass `bShipsOnly`
  defaulted false and `pmodelSkip = NULL` (`common.cpp:1920,1932`), i.e. the
  full-pickable scan.
- Conclusion: the faithful **general** case is "dodge everything" (stations
  included). P168's exclusion of stations was a stated departure from that
  default; P170 corrects it.

The doc comment was updated to reflect this (`Ship.cs:2757-2798`).

## Explicitly out of scope (unchanged, re-confirmed)

- **The waypoint-phase ships-only narrowing / no-dodge-during-fine-maneuver
  suppression** (`c_gpmDodgeShips`/`c_gpmNoDodge`, `common.cpp:2001-2003`).
  Both key off `Waypoint::GetGotoPosition`'s phased fine-maneuver system — part
  of `GotoPlan`'s still-excluded machinery. This port's flat single-goal
  steering has no equivalent phase to hang this off; there is no clean port
  target. Not attempted.
- **`IsSafeToRipcord`'s reuse of `Dodge()` as a ripcord-safety predicate**
  (`shipIGC.cpp:4200-4229`) — a real, separate finding from the same
  investigation, but a ripcord-safety feature, scoped as its own arc
  (allegiance-npc-ripcord-travel-plan). Not this task.
- **No change to any P103–P168 code** beyond `TryComputeDodgeGoal`'s candidate
  scan (Ship.cs:2886-2898) and its goal-exclusion check (Ship.cs:2822), plus
  the doc comment. The `IsImminent`/`SolveForImpact` utilities and the
  ship/asteroid branches are untouched.

## Live tests (NpcDodgeLiveTests.cs) + discrimination proof

Two new live loopback tests (boot the real in-process server; a Miner NPC with
a `PlanTarget` steers through the sector). The P168 test class is unchanged
and passes unmodified (asteroid/ship/enemy scenarios untouched).

1. **`Traveling_npc_steers_around_a_station_obstacle_and_reaches_its_own_station_goal`**
   (`NpcDodgeLiveTests.cs:396`) — a small-radius station (radius ~60) sits on
   the straight line near the goal; the NPC dodges it, then arrives at its real
   station goal. Measured: **WITH** the station scan `minObsDist = 96.9` (clears
   the surface); **WITHOUT** the station scan (scan loop emptied)
   `minObsDist = 45.1` — the ship's centre enters the station's 60-radius
   volume, i.e. a genuine collision avoided by the new category. Discriminates:
   removing the change turns the test red.

2. **`Traveling_npc_does_not_dodge_the_station_it_is_docking_at`**
   (`NpcDodgeLiveTests.cs:461`) — the ship's `PlanTarget` IS the (only) placed
   station; it flies a near-direct line in and arrives. Measured: **WITH** the
   goal-exclusion `finalGoal = 14.5` (arrives), `maxLineDeviation = 136.8`;
   **WITHOUT** it (exclusion forced off) `finalGoal = 322.2` (never arrives),
   `maxLineDeviation = 308.4` (huge self-dodge detour). Discriminates: removing
   the exclusion turns the test red.

### Test-design note (why the obstacle is placed near the goal)

The NPC's autopilot naturally bows ~110–120 units off the straight chord
(measured `maxLineDeviation ≈ 117` across P168's own tests too), so a
**midpoint** obstacle is cleared "naturally" whether or not the dodge runs — a
tautology (this is why P168's asteroid test does *not* actually discriminate;
verified by emptying the asteroid scan: `minRockDist` stays 109.5). P170's Test
1 therefore places the obstacle near the goal (85% along the chord), where the
autopilot has converged onto the line and the ship genuinely flies through it —
making the dodge's effect real and measurable (96.9 vs 45.1). This is the 
discrimination the arc's "must fail if removed" requirement demands; noted here
so a future reader understands why the geometry differs from P168's.

## Verification summary

- Build clean (0 warnings / 0 errors).
- `NpcDodgeLiveTests` (all 6 incl. the 2 new): 3× clean in isolation.
- Full suite: **2807/2807** clean on multiple runs (P169 baseline 2805 + 2).
  The only interruption across all runs was the documented flake
  `LiveSuicideShipDeleteTests.Combat_death_produces_ship_delete_to_same_audience`,
  confirmed green in isolation — not a regression.
- No existing docking-flow test broke (Miner selling, Layer/Builder approaches,
  Take-Cover, OrderChange return-to-base, CrossCluster miner decisions, etc. all
  pass unmodified) — the `PlanTarget` goal-exclusion correctly keeps normal
  docking approaches from self-dodging.

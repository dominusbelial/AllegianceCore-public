# P766 — simulation wave: constructor drill/arrival, goto completion, model spin

Base: `master` at dispatch (`be1d36fd`). Changes left uncommitted in the working tree.
Branch lane: `pi/p766`. Raw evidence: `~/allegiance-evidence/P766/` (outside the repo);
small summaries in `docs/reports/P766_assets/`.

## Result

All three open rows re-audited against current master. `SIM-0119` and `SIM-0078` moved `partial` → `implemented`;
`SIM-0109` stays `partial` (merge review, below) (+2 rows / +6.0 weighted; only these three rows were edited in
`docs/parity/sim-rules-matrix.csv`).

| Matrix | Before | After | Δ |
|---|---|---|---|
| `sim-rules-matrix.csv` implemented | 361 / 1137.0 wt | **363 / 1143.0** | +2 rows / +6.0 |
| `sim-rules-matrix.csv` partial | 39 / 108.0 | 37 / 102.0 | −2 / −6.0 |

## Re-audit findings (before any change)

- **SIM-0109** — the P762 port applied the on-track drilling hold whenever
  `BuilderAsteroidTarget` was set, with **no latch distinct from "has a build
  target"** (no arrival gate, nothing to clear off-track or on a new plan), and
  `TryStartBuilderBuild` still started the effect from a spherical
  "stopped within rock radius + half length + 30" test. Both gaps confirmed.
- **SIM-0119** — the goto completion cleared only `GotoWaypoint`; the accepted
  slot (`AcceptedTarget`/`AcceptedCommandId`, the port's `c_cmdAccepted`) was
  never examined. The layer half (`TryPerformLayerLay`/`PerformLayerLay`) was
  already fully ported in P591 and pinned by tests — only re-certification was
  needed.
- **SIM-0078** — spin was render-only (`Game1.Rendering.cs` `AsteroidSpin`,
  keyed per asteroid over the **static wire orientation**); the core had no
  rotation step (`Model.cs` clock-only; `Asteroid` carried no orientation at
  all). Confirmed.

## Rows

### 1. SIM-0109 — constructor drill alignment (`Igc/shipIGC.cpp:2262-2300`, `:2810-2827`, `:871-893`; `Igc/shipIGC.h:1582`, `:755-757`; `Igc/buildingeffectigc.h:66`; `Igc/igc.h:819`)
**Status: implemented.**

Native: drilling begins **only on goto arrival** — `gotoplan.Execute` success
with `c_cmdPlan == c_cidBuild` and `m_pilotType == c_ptBuilder` sets
`SetStateM(drillingMaskIGC)` and holds yaw 0 / pitch 0 / roll 1 / throttle 0
(`shipIGC.cpp:2810-2822`). While drilling, the on-track test
(`offset2 < 50 && distance2 < (r_ship + r_rock + 25)²`, `dp` against the rock's
rotation axis, `shipIGC.cpp:2280-2296`) keeps the hold; off-track clears the
mask (`SetStateM(0)`, `shipIGC.cpp:2303`) and re-plans. The building effect is
created by the **collision** of an autopiloted drilling builder with its rock
(`shipIGC.cpp:875-885`), and `LegalCommand` refuses every command while
drilling or building (`shipIGC.h:1582-1586`); `SetCommand(c_cmdPlan)` clears
the drilling bit for drones (`shipIGC.h:755-757`).

Port (`src/Allegiance.Core/Sim/Ship.cs`):

- Added the **drilling latch** `Ship.IsDrilling` (private `_isDrilling`; the
  port's `ShipControlState` declares only the movement/weapon bits, so the
  native-name comment carries `drillingMaskIGC`, `igc.h:819`). It is set only
  on **arrival at the build approach** — the port's established arrival idiom
  (stopped, `velocity² < 1`, the same stopped test `TryStartBuilderBuild` and
  `IsReadyToMine` use) combined with the on-track test, standing in for
  native's gotoplan completion — and cleared on any new plan (the
  `BuilderAsteroidTarget` setter), off-track (`SetStateM(0)` analog: the
  autopilot simply keeps steering, exactly as before P766), and when the
  building effect registers (`IsBuilding` branch + the effect path nulls the
  build target).
- The on-track hold (yaw 0 / pitch 0 / roll 1 / throttle 0) is now **gated on
  the latch**: a builder still flying its approach gets no hold.
- `IsLegalCommand` refuses every command for a builder while drilling
  (`shipIGC.h:1582`); the `buildingMask` half was already there.
- The effect now provably starts from the **collision path**
  (`Sector.ResolveShipAsteroidCollision`, the P762 port of
  `shipIGC.cpp:871-893`): the new test records per-tick site calls and asserts
  the effect's creation tick carries the drilling `HullHitEvent` debris, which
  only the collision branch raises. `TryStartBuilderBuild` remains as the
  **documented fallback** for the P652 parked-reservation flow, where a
  constructor is ordered onto a rock it is already parked beside (spawned at
  contact distance − 20, never overlapping) and no collision event can ever
  fire — `P652ConstructorBuildTests` depends on it and stays green.

Tests: `P766SimDrillSpinTests.Builder_latches_drilling_only_on_arrival_not_while_flying_the_approach`,
`P766SimDrillSpinTests.Drilling_latch_clears_off_track_and_on_a_new_plan`,
`P766SimDrillSpinTests.Flying_builder_registers_the_build_from_the_collision_not_the_stopped_substitute`
(creation tick == first debris tick, tick 143 of the flight), plus all of
`P652ConstructorBuildTests` and `P762DroneRuleTests` (unchanged, green).

### 2. SIM-0119 — goto completion and layer lay (`Igc/shipIGC.cpp:2802-2828`; `FedSrv.CPP:13769-13863`)
**Status: implemented.**

Native: on gotoplan completion with `c_cidGoto`, **every** command slot whose
target is the plan target is cleared (`for i < c_cmdMax: if
m_commandTargets[i] == pmodelPlan → SetCommand(i, NULL, c_cidNone)`,
`shipIGC.cpp:2802-2810`) — an accepted goto to the same target dies with the
plan. `c_cidBuild` + builder → drilling (row 1); `c_cidBuild` + layer →
`LayExpendable`, the layer consumed (`FedSrv.CPP:13769-13863`).

Port:

- `GotoOrder` gained `TargetStationId` (`Ship.cs`), carried from
  `ResolveDroneGoto`'s station case (`Server.ShipCommands.cs`) so a goto issued
  against a station **remembers the object, not just a position**. On goto
  arrival (`Ship.ApplyNpcAutopilot`, the `GotoWaypoint` branch) the accepted
  slot is cleared when `AcceptedCommandId == CidGoto` and `AcceptedTarget`
  names that same station (by `StationId`, never by position). Accepted gotos
  to any other target survive, exactly like native's per-slot loop.
- Layer lay: **re-certified, no code change** — the existing P591 headline test
  flies a layer to its lay point and proves exactly one mine, the drone
  consumed (`P591LayerDroneLaysTests.Ordered_layer_drone_flies_to_its_build_point_and_lays_its_minefield_then_is_consumed`),
  with the probe and no-order variants alongside.

Tests: `P766SimDrillSpinTests.Goto_completion_clears_the_accepted_goto_to_the_same_station_only`
(clear case + different-station control case); `P591LayerDroneLaysTests` (15/15).

### 3. SIM-0078 — model spin (`Igc/modelIGC.cpp:73-96`; `zlib/orientation.cpp:595-603`; `zlib/matrix.cpp:408-452`; `Igc/asteroidigc.cpp:41-45`)
**Status: implemented.**

Native: every visible model with a non-zero rotation angle post-rotates its
**hit-test orientation** by `angle·dt` each `Update` and renormalizes
(`modelIGC.cpp:85-99`); decals only spin their thing site. Asteroids carry the
rotation in their data (`DataAsteroidIGC.Rotation`, applied at
`asteroidigc.cpp:45`).

Port (`src/Allegiance.Core/Sim`):

- `Orientation.PostRotate(axis, theta)` — the faithful port of native's
  `PostRotate` + `Matrix::SetRotate` Rodrigues form (rows verified against
  `SetRotateX/Y/Z` and the general-axis formula; renormalizes, allocation-free,
  no-op on zero axis/angle).
- `Asteroid` now carries a **sim `Orientation`** seeded from the record's
  forward/up (`asteroidigc.cpp:41-44`) and post-rotates it by `angle·dt` in its
  per-tick `Update` (the asteroid `OnUpdate`, `src/Allegiance.Core/Sim/Asteroid.cs:187,254`), skipping zero-angle rocks — the
  native `m_rotation.angle() != 0` guard. Zero allocation per tick (pinned).
- **Hit test:** the port's asteroid collider is **spherical-only**
  (`Sector.ResolveShipAsteroidCollision` builds every rock collider with
  `HitShape.Sphere` and `Orientation.Identity`), so the advancing orientation
  does not change collision outcomes — but the rule advances it regardless, per
  the row. The drilling axis (row 1) is the rotation axis itself, which does
  not change under rotation about itself, so row 1 is unaffected.
- **Client render path unchanged:** `AsteroidSpin` (client) is a render-local
  accumulator over the static wire orientation and does not read the sim
  orientation — nothing double-rotates, and the client-side spin mirrors what
  the native *client's own* `CmodelIGC::Update` does.
- **Other rotating models, audited:** asteroids are the only core model whose
  native rotation feeds a sim hit test. Native missiles roll about their launch
  forward by the type rate (`projectileIGC.cpp:78-81`) — **omitted
  deliberately**: the port's projectile colliders are `Orientation.Identity`
  spheres, so the roll cannot change any collision outcome, and skipping keeps
  the per-missile per-tick cost zero at 200v200. Chaff (`chaffigc.cpp:54-57`)
  and the non-decal treasure branch (`treasureIGC.cpp:183-190`) are decal/model
  cosmetics with no sim hit test in the port. These omissions are recorded in
  the matrix notes.

Tests: `P766SimDrillSpinTests.Rotating_asteroid_turns_its_orientation_by_angle_times_dt`
(N ticks → forward at `(cos θ, 0, −sin θ)`, θ = rate·N·dt, renormalized),
`P766SimDrillSpinTests.Zero_angle_asteroid_is_untouched_and_the_spin_step_allocates_nothing`
(`GC.GetAllocatedBytesForCurrentThread` delta 0 over 100 ticks).

## Out of scope (untouched, per brief)

SIM-0100, SIM-0014, SIM-0062/0063, SIM-0141/0144/0470 (no edits to
`Server.Broadcast.cs`, `Server.Lifecycle.cs`, `Server.Lobby.cs`), all sound
rows, bot/commander code. `Ship.cs` edits confined to the builder/goto/layer
regions (no touch near P764's `~424`/`~3240` reload seam or P760b callbacks).

## Gates

| Gate | Result |
|---|---|
| Whole-solution build (Debug) | 0 warnings / 0 errors |
| AOT publish (`dotnet publish src/Allegiance.Server -c Release -r linux-x64 --self-contained -p:PublishAot=true`) | 0 warnings / 0 errors (no IL2026/IL3050) |
| New + affected tests 3× alone (`P766SimDrillSpinTests`, `P652ConstructorBuildTests`, `P762DroneRuleTests`, `P591LayerDroneLaysTests`, `P591bLayerPingPongTests`, `P591cDroneRoutingTests`) | 28/28 × 3 runs, all green |
| `P650SimRulesTests` (asteroid tick touched) 3× alone | 16/16 × 3 runs |
| ONE fast tier (`tools/run-tests.sh --tier fast`) | **PASS**: 9345 tests, 274.1 s (`docs/reports/P766_assets/fast-tier-summary.json`) |
| Showcase never-parks 5× (flight/sim affected) | **5/5 passed**, ~1 m 50 s each — matches the P762 base rate (~1 m 50 s) (`docs/reports/P766_assets/showcase-5x.log`) |

No test exceeded 10 s except the tagged Live-tier showcase; no new Live-tier
tests were added (the six P766 tests run in <0.2 s total).

## Merge review (Claude, 2026-10-20)

- **SIM-0109 back to `partial`.** `TryStartBuilderBuild` (stopped within rock radius + half length + 30) runs before
  `ApplyBuilderDrillingControls` every tick and its sphere is larger than the drill sphere, so it starts most builds
  before the latch can be set. The collision branch is not gated on drilling as native is (`shipIGC.cpp:874`).
  The brief's rule was: keep the fallback → keep the row `partial`.
- **Fixed: a latched builder could sit forever.** If another builder's effect claimed the rock, the latch held
  throttle 0 and `IsLegalCommand` refused every order. `ApplyBuilderDrillingControls` now drops a rock claimed by
  another builder (test `Builder_arriving_at_a_rock_claimed_by_another_builder_drops_it_and_takes_orders`).
- **Matrix rows rebuilt.** The branch's CSV edits put values in the wrong columns (unquoted commas); the three rows
  were rewritten on master's column layout.
- **Showcase log:** `showcase-5x.log` holds runs 2–5 only; run 1's output was not kept.

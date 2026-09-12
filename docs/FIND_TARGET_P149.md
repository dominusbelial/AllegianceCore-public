# FIND_TARGET_P149 — nearest friendly station with an ability (same cluster)

Task: port the one narrow, real slice of native `FindTarget` that
`ORDER_CHANGE`'s "return to base" branch will consume —
`FindTarget(pship, c_ttFriendly|c_ttStation|c_ttNearest|c_ttAnyCluster,
NULL, NULL, NULL, NULL, c_sabmRepair)` — **minus** `c_ttAnyCluster`: the
nearest friendly station in this ship's own sector whose station type has a
given ability bit. Landed as a single new, additive search primitive +
a pure unit test. Not wired into `ORDER_CHANGE` yet (that is the next task).

Branch `master`, commit on top of `e73fe68` (P148).

## What was ported

New method `Ship.FindNearestFriendlyStation(short abilityBit)`
(`src/Allegiance.Core/Sim/Ship.cs`) — placed on `Ship` (not `Sector`)
because `Ship` already carries every input the search needs (`Sector`,
`SideId`, `Position`, `Mission` → `Mission.Core.StationTypes`), so the port
touches **zero** existing code (the task's hard constraint: "no change to
P103-P148 code beyond this new, additive search method and its test"). It
reads more naturally for the eventual call site too: a ship looks for its
base.

Algorithm — a faithful port of the exact code path the call site uses
(`src/Igc/common.cpp`, read this session):

1. Resolve `mission.Core.StationTypes` and the ship's own `Sector`, `SideId`;
   a ship with no sector/mission/side finds `null` (native falls to
   `c_ttNeutral`, which never matches `c_ttFriendly`, `common.cpp:269-273`).
2. Iterate the ship's sector's station list — `Sector.Stations`
   (`Sector.cs:44`), the per-sector `DataStationIGC` list; **own sector only**,
   matching native's `pcluster->GetStations()` model-list selection when
   `(ttMask & c_ttAllTypes) == c_ttStation` (`common.cpp:498-506`).
3. Per candidate, the `FindableModel` steps (`common.cpp:254-353`)
   specialised to `OT_station`:
   - **same side**: `station.SideId == sideId` = the `sidebits &
     c_ttFriendly` test (`common.cpp:269-273`);
   - **resolvable type**: `mission.Core.StationTypes.TryGetValue(...)` (the
     `GetStationType()` deref, `common.cpp:321` — an unresolvable type has no
     type record to ask, so it is skipped);
   - **ability bit**: `DataStationTypeIGC.HasCapability(abilityBit)`
     (`DataStationTypeIGC.cs:100`, the `StationTypeIGC::HasCapability` bit
     test, `stationtypeigc.h:168-170`) — the `OT_station` case at
     `common.cpp:321`.
4. Keep the nearest by squared Euclidean distance
   `dp.LengthSquared()` (`common.cpp:575-578`), strict `<` replace
   (`common.cpp:617`), same as native's `bReplace = (d < distance)`. Return
   it, or `null` if none (`common.cpp:648-652`'s no-match path).

The `abilityBit` is a general parameter (native's own `abmAbilities` shape),
so `c_sabmCapLand` for the native carrier case is a one-argument change, not
a new function. `c_sabmRepair = 0x40` (`Wopr/Wopr/Constants/
StationAbilityBitMask.cs:16`) is used only as the test's bit; the method
itself is bit-agnostic.

## What is explicitly deferred (accepted, documented gaps vs native)

- **`c_ttAnyCluster` / cross-cluster warp-graph BFS.** The native call passes
  `c_ttAnyCluster`, so a local miss recurses ring-expanding outward through
  warp connections into neighbouring clusters (`common.cpp:648-741`, the
  recursive `FindTarget` at `729`). This is a genuinely separate, larger piece
  of work (confirmed this session). Here a station search that finds nothing in
  the ship's own sector simply returns `null` (the native early return at
  `common.cpp:648-652` when `c_ttAnyCluster` is unset) — stated plainly as a
  documented behavior gap, not silently narrowed.
- `c_ttLeastTargeted` (asteroid-contention counting, `common.cpp:582-614`).
- `c_ttFront`/orientation-based targeting (`common.cpp:576-577`).
- `c_ttPrevious`/current-target cycling (`common.cpp:529-532,640-643`).
- Ship/warp/treasure/probe candidate types — stations only.
- Alliance-tier (`iAllies`) handling — **this port has no alliance system**
  (confirmed absent repeatedly this session), so the `IsideIGC::AlliedSides`
  / `#ALLY` branches in `FindableModel` (`common.cpp:271,275-278,308-320`)
  are dropped; `pside == pHisSide` is the whole friendship story.
- Wiring this into `ORDER_CHANGE`'s "return to base" branch, or any
  `PickDefaultOrder` pilot-type behavior — future tasks.

## The `FindableModel` simplification

`FindableModel` itself (`common.cpp:254-353`) is ~100 lines with alliance-tier
logic, a probe `c_eabmRescueAny` special case (`common.cpp:284-285`), and an
`OT_ship` parent-ship/hull-capability case (`common.cpp:291-301`) — none of
which apply to a station-only, no-alliance search. The port inlines only the
three steps above (same side → resolvable type → `HasCapability`) directly in
the loop, which is exactly the net effect of the `OT_station` branch
(`common.cpp:303-323`) for this call.

## Test

`tests/Allegiance.Core.Tests/FindNearestFriendlyStationTests.cs` — pure,
data-driven, no live/network (the honest, sufficient bar for this method; no
VM needed). Five `[Fact]`s, one per task requirement:

1. a friendly repair-capable station is found;
2. a friendly non-repair-capable station is correctly excluded;
3. an enemy-side repair-capable station is correctly excluded;
4. when two friendly repair stations exist, the nearer one wins (and a
   still-nearer friendly non-repair station does not steal it);
5. when nothing qualifies, `null` is returned.

Stations are synthetic `DataStationIGC` records placed via the public
`Mission.PlaceStation` seam into `XXV_simple.igc` sector 1052 (the
P111-documented no-station fly sector, so no real stations compete). Station
**types** are real records pulled from the `PCore001.igc` static core — a
repair-capable and a non-repair-capable type are selected data-driven (asserted
both exist), so the ability-bit branch is exercised against genuine capability
masks, not literals bolted onto the code under test.

## Verification

- New test class: 5/5 passed in isolation.
- Full suite (unmodified + additions): **2734 passed / 0 failed** — 2729
  baseline going in (per P148 checkpoint) + 5 new. Rerun for 3 consecutive
  clean bars (P149 discipline). Documented flakes (`LiveChatSectorWireTests`,
  `LiveCombatWireTests`, `LiveInSpaceEjectTests.
  Already_a_lifepod_is_truly_killed_not_re_ejected`, `LiveSuicideShipDeleteTests.
  Combat_death_produces_ship_delete_to_same_audience`, `ShipsUpdateLiveTests.
  Locked_target_fills_first_heavy_slot_with_real_fractions`) did **not** fire;
  if any does on rerun it is a known flake, not a P149 regression (the diff is
  additive-only and touches none of their code).
- Every claim above cites the exact `common.cpp` line read this session.
- No VM, no live loopback test required (state plainly): the method is pure
  data-driven logic.

## Files changed

- `src/Allegiance.Core/Sim/Ship.cs` — new `FindNearestFriendlyStation`
  (additive method only).
- `tests/Allegiance.Core.Tests/FindNearestFriendlyStationTests.cs` — new
  test class.
- `FIND_TARGET_P149.md` — this writeup.

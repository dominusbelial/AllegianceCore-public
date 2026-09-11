# NPC_BUILDER_DEFAULT_ORDER_P165 — the Builder AI decision layer (Stage 3)

Closes the loop left open by P163 (the `BuildingEffect` mechanic) and P164
(the `BUILDINGEFFECT_DESTROYED`(181) interruption wire): both were triggered
**explicitly** by a test calling `Sector.StartBuildingEffect` directly. This
task makes an idle Builder NPC pick a target on its own — the last piece for
the builder/station-construction arc to run autonomously, mirroring how P156
closed the Miner loop after P155's movement primitive existed.

Scope: two new `Ship` fields, the `IsBuilding` set/clear sites in `Sector`, a
new decision method, and its `OnUpdate` wiring. No change to any P103–P164 code
beyond those. No VM needed.

---

## 1. The decision, and the direct reuse of the P161 search

`Ship.DecideBuilderDefaultOrder` (`src/Allegiance.Core/Sim/Ship.cs:2966`) is the
port of native `PickDefaultOrder`'s `c_ptBuilder` case
(`Igc/shipIGC.h:2388-2415`): for an idle (`!IsBuilding`), undocked
(`StationId is null`) `PilotType == PilotTypeBuilder` ship with a non-null
`BuilderStationType`, it picks the least-targeted reachable neutral asteroid and
starts a real build on it.

Native's `c_ptBuilder` case (`shipIGC.h:2388-2415`) and `c_ptLayer` case
(`shipIGC.h:2417-2434`) share the **identical** `FindTarget` shape —

```
c_ttNeutral | c_ttAsteroid | c_ttNearest | c_ttLeastTargeted | c_ttAnyCluster | c_ttCowardly
```

— differing only in the trailing ability-bitmask argument (`m_abmOrders` for
Builder, a plain `0` for Layer) and in what happens once a target is found
(Builder starts an actual `c_cidBuild`; Layer just `c_cidGoto`s there). This
port's own `Ship.FindLeastTargetedAsteroidAnyCluster` (P161,
`Ship.cs:1058`) — built for `DecideLayerDefaultOrder` (`Ship.cs:2846`) —
implements that exact search and is **reused directly for Builder**, not
reimplemented.

### 1a. The ability-bitmask simplification, cited against P161's precedent

Native's `m_abmOrders` filtering matches a specific station-buildability
requirement against an asteroid's own capability flags. **This port's asteroids
carry no such sub-type data to filter on** (confirmed directly):

- `DataAsteroidIGC` (`src/Allegiance.Core/Data/DataAsteroidIGC.cs:30-47`) exposes
  exactly one capability field, `AabmCapabilities` (`:38`), a `short` with no
  station-buildability bit decoded.
- The only interpreted bit is `Asteroid.CapMineHe3 = 0x01`
  (`src/Allegiance.Core/Sim/Asteroid.cs:65`), consumed solely by
  `Asteroid.CanMineHe3` (`Asteroid.cs:93`). There is no "buildable" capability.

So the honest port equivalent of native's ability-mask filter is "any neutral
asteroid qualifies," which is exactly what `FindLeastTargetedAsteroidAnyCluster`
already returns (P161 deliberately accepted any unowned asteroid for the same
reason — `Ship.cs:1058`'s doc states its own
`c_ttNeutral | c_ttAsteroid` filter with no He3 gate because every asteroid is
unowned here). **This is the applied simplification, mirroring P161's own stated
simplification for the identical reason** (see the remarks on
`DecideBuilderDefaultOrder`, `Ship.cs:2966`, and the P161 writeup).

---

## 2. `Ship.IsBuilding` — the committed-build flag

Nothing on `Ship` knew it was mid-build: P163's `BuildingEffect` lives only in
`Sector._buildingEffects`, never referenced back from the ship. Without a
ship-side flag the idle Builder would re-decide (and re-register a duplicate
build) every idle tick.

`Ship.IsBuilding` (`Ship.cs:1474`) mirrors `IsMining`'s shape exactly
(`IsMining`, `Ship.cs:1457`): a plain `bool`, default `false`.

Set/clear sites (all in `Sector`):

- **Set `true`** in `Sector.StartBuildingEffect` right where the effect is
  registered (`Sector.cs:316`) — the one-line addition at the existing call
  site, after `_buildingEffects.Add(effect)`.
- **Cleared `false`** in both of `Sector.ResolveBuildingEffects`'s terminal
  branches (`Sector.cs:2471`):
  - interruption abort (Builder died) — `effect.Builder.IsBuilding = false`
    (`Sector.cs:2488`);
  - completion — `builder.IsBuilding = false` in `CompleteBuild`
    (`Sector.cs:2554`).

**Ordering on the completion path.** `CompleteBuild` sets `IsBuilding = false`
*before* `Remove(builder)` (`Sector.cs:2554-2555`). The order is safe either way
— the ship is removed from the sector's live model list, so no further tick will
re-decide on it regardless — but clearing before removal reads most faithfully
(the build is done, so it is no longer building) and keeps both terminal
branches symmetric with the interruption path's clear. Documented in the code
comment at the site.

---

## 3. `Ship.BuilderStationType` — the configured station type

Native's `SetBaseData` (`Igc/shipIGC.h:2050-2065`) sets `m_pbaseData` (the
station type a Builder is assigned to construct) and `m_abmOrders` (derived from
`((IstationTypeIGC*)pbase)->GetBuildAABM()`) **once, at ship configuration
time** — a Builder always builds the one station type it was configured with,
not a type it discovers. This port has no drone-config table wired for that
derivation, so the honest equivalent is `Ship.BuilderStationType`
(`Ship.cs:759`): a `DataStationTypeIGC?`, settable, following this port's
established per-purpose-field convention, set **directly** (at spawn, or by the
test) — not derived from any drone-type table this port doesn't have wired for
it.

If null, the Builder has nothing to build and stands by (logged), matching the
no-target no-op precedent every other decision cycle uses (`DecideMiningOrSelling`,
`DecideLayerDefaultOrder`, `DecideWingmanDefaultOrder`).

---

## 4. OnUpdate wiring

`Ship.OnUpdate`'s `IsNpc` dispatch gained a `PilotType == PilotTypeBuilder`
branch alongside the Miner/Layer/Wingman branches (`Ship.cs:3344`), the same
`else if (PilotType == ...)` shape already established there:

```
else if (PilotType == PilotTypeBuilder)
{
    if (!IsBuilding && StationId is null)
        DecideBuilderDefaultOrder();
}
```

Once a build registers, `IsBuilding` becomes `true` so the ship is no longer
idle and does not churn; the 40 s construction runs to completion in `Sector`'s
per-tick resolver. The take-cover idle gate was deliberately **not** extended to
`IsBuilding` (that would be a change to P151/P160-P162 code beyond the task's
allowed surface); an un-damaged building Builder is not diverted, matching the
other cycles' behavior for undamaged ships.

---

## 5. Explicit exclusions

- **Any `m_abmOrders`/ability-bitmask asteroid filtering** — this port's
  asteroids carry no such sub-type data (Section 1a); every neutral asteroid
  qualifies. Mirrors P161's own stated simplification.
- **The `m_abmOrders == c_aabmBuildable → c_cidGoto` native edge case**
  (`shipIGC.h:2390,2403`) — no clean port given the ability-mask
  simplification; a Builder here always starts a real build when it finds a
  target, never a plain Goto. Stated in `DecideBuilderDefaultOrder`'s remarks
  (`Ship.cs:2966`).
- **Any drone-type-driven `BuilderStationType` discovery** — set directly, not
  derived from a table (Section 3).
- **Multiple simultaneous Builders contending for the same asteroid** (fair-share
  arbitration analogous to P159's miner contention) — a real, separate, later
  concern; not modeled. The shared `FindLeastTargetedAsteroidAnyCluster` counts
  same-side `LayerAsteroidTarget` contenders only, so two Builders could
  nominally pick the same rock — explicitly out of scope.
- **No travel-before-build gate** — the Builder commits to the build immediately
  on target selection (`StartBuildingEffect` called as soon as a target is
  found), the shape the task prescribed; it does not first fly to the rock. The
  test spawns the Builder far from the rock and documents that distance is
  immaterial.

---

## 6. Evidence

New live loopback test `tests/Allegiance.Core.Tests/NpcBuilderDefaultOrderLiveTests.cs`
(in-process server + one seated bot, no VM; real `PCore001.igc`):

1. **`Idle_builder_autonomously_builds_station_from_neutral_asteroid`** — a real
   Builder drone type from the core (`core.DroneTypes.First(kv =>
   kv.Value.PilotType == Ship.PilotTypeBuilder)`, i.e. PCore001's
   'Constructor (Lrg)'/'Constructor (Sml)', PilotType 6) with
   `BuilderStationType` set to a real station type, placed near a real neutral
   asteroid: the idle Builder picks the rock and starts the build on its own;
   asserts `IsBuilding == true` while in progress (the flag's own check) and
   again ~15 s in; then ticks the full 40 s — the asteroid is gone from
   `Sector.Mineable`, a new `Station` of the configured type/side sits at the
   rock's position (runtime and static lists), the Builder ship is consumed
   (gone from `Sector.Models`), and `IsBuilding` is cleared on the completion
   path.
2. **`Idle_builder_without_configured_station_type_stands_by`** — a Builder with
   a null `BuilderStationType` stands by: no effect registers, the rock is
   untouched, the Builder stays in the sector (the no-op precedent).

Verification: full suite **2796/2796** across three consecutive clean runs
(2794 at the P164 checkpoint + the 2 new tests). The five documented timing-
sensitive flakes intermittently fired on this machine (an elevated-flake-rate
environmental condition, per the task note); each was confirmed pre-existing by
isolated single-test reruns passing, and none touches Builder/BuildingEffect
code.

Session logged via `~/Development/allegiance-tools/session_stats.py`.

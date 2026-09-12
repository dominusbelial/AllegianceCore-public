# P163 — Builder station-construction, Stage 1: the BuildingEffect mechanic

Stage 1 of the builder/station-construction arc (plan:
`allegiance-builder-station-construction-plan.md`). The **mechanic first** step,
exactly as the arc's staging demanded: prove the simulation mechanic — a real,
persistent, timed world-transformation — before any wire (Stage 2) or any
Builder AI decision layer (Stage 3). This is the first task in the whole NPC
pilot-type line that is not "AI on top of existing mechanics" but genuinely new
simulation: a Builder commits to building a station at an asteroid, a timed
effect runs, and on completion the rock becomes a real station and the Builder
consumes itself.

Everything below is confirmed by the files actually read this task, each cited
with `file:line`. No VM, no sockets, no wire — this is pure sim.

## 1. Timing model — the real 40 s sum, cited

The native `CbuildingEffectIGC` lifecycle is five phases driven by four
constants:

```
buildingeffectigc.h:19-22   const float c_dtShrink = 10.0f; c_dtOpaque = 10.0f;
                            c_dtGrow = 10.0f; c_dtEnvelope = 10.0f;
buildingeffectigc.h:50-54   m_timeEnvelope = ...timeStart;
                            m_timeGrow   = m_timeEnvelope + c_dtEnvelope;   // +10
                            m_timeOpaque = m_timeGrow     + c_dtGrow;       // +20
                            m_timeShrink = m_timeOpaque   + c_dtOpaque;     // +30
                            m_timeComplete = m_timeShrink + c_dtShrink;     // +40
```

Sum = `10+10+10+10 = 40` s, folded into `m_timeComplete`. This task needs only
that sum, captured once as `BuildingEffect.BuildDuration = 40.0`
(`src/Allegiance.Core/Sim/BuildingEffect.cs:61`, cited against
`buildingeffectigc.h:19-22`), and applied as the completion deadline
`BuildingEffect.CompletionTime` (= `start + 40`, Sector.cs:287).

The **cosmetic five-phase animation** (`Update`, `buildingeffectigc.h:160-225` —
position/radius/color/alpha interpolation) is deliberately **not** represented:
this port has no client and no visual-effects system to drive it (confirmed
throughout the preceding slices). Phase-boundary times (`m_timeGrow`/`Opaque`/
`Shrink`) are not retained because **nothing observes them yet** — only the
terminal deadline matters for when the transformation fires. Native nuance
recorded rather than reproduced: the transformation itself fires in
`AsteroidUpdate` at the **shrink** boundary (`now >= m_timeShrink`,
`buildingeffectigc.h:314`, i.e. 30 s in) while the cosmetic object winds down to
`m_timeComplete` (40 s); with no animation observed, Stage 1 uses the full
40-second completion deadline the task specifies — immaterial to the sim
outcome.

## 2. The terminal transformation — the native reference, and the port

Native completion (`AsteroidUpdate`, `buildingeffectigc.h:312-353`), a one-way
self-sacrificing transaction:

```
buildingeffectigc.h:328-334   terminate the Builder's parts (no treasure),
                              SetAmmo(0), SetFuel(0)
buildingeffectigc.h:338-342   SetStateM(0), KillShipEvent (quiet) → builder gone
buildingeffectigc.h:344-351   m_pasteroid->SetBuildingEffect(NULL);
                              GetIgcSite()->BuildStation(pasteroid, side, type, ...)
```

And `BuildStation` assembles the replacement station's record
(`FedSrv.CPP:11386-11402`):

```
FedSrv.CPP:11387  ds.name        = pstationtype->GetName()
FedSrv.CPP:11388  ds.clusterID   = pcluster->GetObjectID()        (the rock's sector)
FedSrv.CPP:11389  ds.position    = pasteroid->GetPosition()       (the rock's position)
FedSrv.CPP:11390  ds.forward.z   = 1
FedSrv.CPP:11392  ds.up.y        = 1
FedSrv.CPP:11396  ds.sideID      = pside->GetObjectID()           (the Builder's side)
FedSrv.CPP:11397  ds.stationID   = pmission->GenerateNewStationID()
FedSrv.CPP:11399  ds.stationTypeID = pstationtype->GetObjectID()
FedSrv.CPP:11401  ds.bpHull      = pasteroid->GetFraction(); ds.bpShield = 0
```

### Port — `CompleteBuild` (Sector.cs:2500) and `BuildStationRecord` (Sector.cs:2524)

1. **Remove the asteroid** — `Sector.RemoveAsteroid` (Sector.cs:252), the
   established removal contract the combat pass uses when a rock is destroyed
   (`ResolveCombatCollisions`, Sector.cs:1314/1731 for the ship/rock-death
   removal idiom).
2. **Construct + place the new station** — a `Station` of `effect.StationType`
   at the asteroid's position, owned by `effect.SideId`, registered via
   `Mission.PlaceStation` (Mission.cs:433, the already-tested synthetic-injection
   entry the loopback station-combat tests use). `BuildStationRecord` sets every
   `DataStationIGC` field explicitly to mirror the native lines above: name from
   the type, `ClusterId = sector.Id` (the rock's sector), position = the rock's,
   fixed identity orientation, `SideId` = the Builder's, `StationId` from
   `Mission.GenerateNewStationID()`, `StationTypeId` = the supplied type,
   `BpHull = 1` / `BpShield = 0`.
3. **Consume the Builder** — `Sector.Remove(builder)` (Sector.cs:241, the
   `_models.Remove` path). This is the port's real, established ship-removal
   mechanism, reused exactly rather than inventing a parallel one: the combat
   pass removes a dead ship the same way — `if (ship.Dead) _models.Remove(ship)`
   (Sector.cs:1314 and Sector.cs:1731), the "death contract" documented at
   README.md:413-417. (Native additionally zeroes the Builder's
   parts/ammo/fuel before a quiet `KillShipEvent` to prevent a treasure spawn,
   `buildingeffectigc.h:328-340`; this port has no treasure-spawn-on-death
   linkage for this path, so removal alone is the equivalent outcome — the ship
   is gone from the sector's live list, the observation the tests assert.)

### Port — the per-sector-tick resolver `ResolveBuildingEffects` (Sector.cs:2442)

Follows the established resolver shape (`ResolveMining`, `ResolveWarpCollisions`
— iterate a dedicated list, test a condition, mutate state), wired into the
sector tick after the mining pass (Sector.cs:526). Two terminal conditions per
build per tick:

- **Interruption** — the Builder has died (`Ship.Dead`, `_fraction <= 0`,
  Ship.cs:118): the effect is dropped, the asteroid is untouched and no station
  is created. Port of native `BuilderTerminated()` +
  `KillBuildingEffectEvent` (`buildingeffectigc.h:294-297`, triggered from
  `CshipIGC::Terminate`'s Builder branch, `shipIGC.cpp:209-220`), minus the
  `BUILDINGEFFECT_DESTROYED`(181) wire — Stage 2.
- **Completion** — `now >= CompletionTime` (start + 40 s): `CompleteBuild`.

### The explicit start-a-build entry point — `Sector.StartBuildingEffect` (Sector.cs:287)

`StartBuildingEffect(Ship builder, Asteroid target, DataStationTypeIGC stationType, double now)`.
This is the test-authored trigger point that Stage 3's future Builder AI
decision layer will call instead of a test setting it directly. The target
station type is a direct input (no `m_abmOrders`/`c_aabmBuildable` build-ability
discovery — that decision logic is Stage 3). Preconditions asserted: the Builder
is in this sector and the target rock is in `_mineable` (native likewise asserts
a non-null Builder/asteroid at Initialize, `buildingeffectigc.h:65,72`). The
finished station's side is captured from `Builder.SideId` at start, mirroring
native's initialize-time capture (`buildingeffectigc.h:68`).

## 3. Station-construction inputs and id-allocation approach

The concrete construction inputs (all in `BuildStationRecord`, Sector.cs:2524)
match native `BuildStation` field-for-field (cited above). The **id allocation**
mirrors native `pmission->GenerateNewStationID()` (FedSrv.CPP:11397) with a
dedicated monotonic counter on the Mission — `Mission._nextStationId`
(Mission.cs:343) + `Mission.GenerateNewStationID()` (Mission.cs:357) — sharing
the exact counter shape of the port's other runtime id allocator, the server's
ship ids `_nextShipId` (Server.cs:59). The counter starts at 9187, comfortably
above the map's low static station ids so a fresh build can never shadow an
assembled station. (A station id is only compared against other station ids in
this port — `RemoveStation`, Sector.cs:202 — so distinctness from the ship-id
space is cosmetic but kept tidy.)

## 4. What is explicitly OUT of scope (each with citation)

- **`BUILDINGEFFECT_DESTROYED`(181) / any wire message at all.** No client ever
  sees this in Stage 1. The interruption path in the resolver
  (Sector.cs:2442) deliberately stops at clearing the effect — no fmid is
  emitted (the native `KillBuildingEffectEvent` site is `shipIGC.cpp:218`; a
  separate, later Stage 2).
- **Cosmetic/visual effect.** No client, nothing to animate — the five-phase
  radius/colour/alpha animation (`buildingeffectigc.h:160-225`) is not ported.
- **The Builder AI decision layer** (`PickDefaultOrder`'s `c_ptBuilder` case,
  `shipIGC.h:2388-2415`, `Ship.PilotTypeBuilder` = 6 at Ship.cs:374, the
  least-targeted buildable-asteroid search). Stage 3. This task's build is always
  started explicitly by the caller/test (`StartBuildingEffect`).
- **`m_abmOrders`/build-ability-bitmask semantics** (native
  `IstationTypeIGC::GetBuildAABM()` / `c_aabmBuildable` discovery). Not needed
  until Stage 3's AI must choose among possible types; the type is supplied
  directly.
- **No change to P103-P162 code** beyond the new `BuildingEffect`
  representation, its per-sector-tick resolver, the explicit start-a-build entry
  point, and the Mission id-allocator they share.

## 5. Verification

- Pure-loopback live tests (real `PCore001.igc`, real mission/map, `NullIgcSite`
  — no sockets, no VM): `tests/Allegiance.Core.Tests/BuildingConstructionLiveTests.cs`.
  - **Successful build** (`Completed_build_replaces_asteroid_with_station_and_consumes_builder`):
    build started, ticked through the 40 s deadline → rock gone from
    `Sector.Mineable`, exactly one new `Station` of the right type/side at the
    rock's position in `Sector.StationRuntimes` and `Sector.Stations`, Builder
    removed from `Sector.Models`.
  - **Interrupted build** (`Interrupted_build_aborts_without_touching_asteroid_or_creating_station`):
    build started, Builder killed partway (lethal `Ship.ReceiveDamage` → `Dead`),
    ticked past what would have been completion → rock still in `Mineable`
    unchanged, station count unchanged (no station created), effect cleared.
- Full existing suite: baseline 2787 (pre-change, clean); with P163 the total is
  **2789 (2787 + 2 new), three consecutive clean full runs** (0 failures each).
  Residual runs that did not go clean hit only the five documented flakes
  (LiveCombatWireTests / LiveSuicideShipDeleteTests etc.), each of which passes
  in isolation and is not a P163 regression; a concurrently-running qemu VM
  (ubuntu2604, 32 GB) elevates those live-network flake rates, matching the
  session's earlier observation.
- Session stats logged via `python3 ~/Development/allegiance-tools/session_stats.py`.

## Files touched

- `src/Allegiance.Core/Sim/BuildingEffect.cs` (new) — the effect object.
- `src/Allegiance.Core/Model/Sector.cs` — `_buildingEffects` field (169),
  `BuildingEffects` accessor (259), `StartBuildingEffect` (287), tick hook (526),
  `ResolveBuildingEffects` (2442), `CompleteBuild` (2500),
  `BuildStationRecord` (2524).
- `src/Allegiance.Core/Model/Mission.cs` — `GenerateNewStationID` allocator (343, 357).
- `tests/Allegiance.Core.Tests/BuildingConstructionLiveTests.cs` (new) — the two
  live loopback proofs.
- `BUILDER_CONSTRUCTION_MECHANIC_P163.md` (this file).

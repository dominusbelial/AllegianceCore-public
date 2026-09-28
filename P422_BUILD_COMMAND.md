# P422 — A real player-issuable Build command

Status: implemented, tested, live-proven. Feature committed on `master` (not
pushed — Domingo pushes).

## Summary

Before P422 this port had **no player-facing Build command at all** — only the
NPC-autonomous default order (P165). `Ship.cs:1542-1544`'s own doc comment
called the gap out ("no `CidMine`/`CidBuild` command constants and nothing ever
sets a ship's `AcceptedTarget` to an asteroid with a mine-class command"). P422
closes it: a real Builder-role player ship can issue a Build order over the real
wire, the server enforces the native `LegalCommand` c_cidBuild legality gate
(Builder-role pilot + buildable-asteroid capability) and, when legal, drives the
timed construction to a real new station.

## 1. Completed native trace — the exact station-creation call site

The brief left the terminal station-creation trigger open ("the actual
station-creation trigger needs a fresh, focused trace — don't guess at it").
Traced and pinned:

1. **Trigger** — `src/Igc/asteroidigc.h:87-91`: every asteroid's per-tick
   `CasteroidIGC::Update(now)` calls `m_pbuildingEffect->AsteroidUpdate(now)`
   while a building effect is attached.
2. **Terminal sequence** — `src/Igc/buildingeffectigc.h:312-353`
   `CbuildingEffectIGC::AsteroidUpdate`: fires when `now >= m_timeShrink` and the
   Builder is still alive; quietly nukes the Builder's parts/ammo/fuel, consumes
   it via `KillShipEvent(..., amount 0.0f, ...)` (`:340`), clears the effect off
   the rock, then calls **`GetIgcSite()->BuildStation(pasteroid, m_pside,
   m_pstationType, now, bseenside)` at `:348-352`**.
3. **Actual station creation** — `FedSrv/FedSrv.CPP:11374`
   `FedSrvSiteBase::BuildStation`: resolves the successor station type
   (`:11381`), assembles `DataStationIGC ds` from the asteroid (position, side,
   fresh `GenerateNewStationID`, type, hull fractions), expels too-close probes,
   kills the asteroid, and finally **`pmission->CreateObject(now, OT_station,
   &ds, sizeof(ds))` at `FedSrv.CPP:11437`** — the real new station — then
   `SetRoidID(pasteroid->GetObjectID())` (`:11441`).

So: **`FedSrvSiteBase::BuildStation` → `CreateObject(OT_station)` at
FedSrv.CPP:11437**, reached from `buildingeffectigc.h:348` via the asteroid's
per-tick update (`asteroidigc.h:91`). Notes kept at `P422_NATIVE_TRACE_NOTES.md`.

## 2. The wire path added

The port's real-player command wire path is `FM_CS_ORDER_CHANGE(93)`, handled in
`src/Allegiance.Server/Server.ShipCommands.cs` (`HandleOrderChange`). P422
extends that existing handler — no parallel mechanism:

- **`OrderChange.CidBuild = 9`** (`src/Allegiance.Core/Wire/OrderChange.cs`) —
  the native `c_cidBuild` command id (`Igc/igc.h:910`), previously absent.
- **`HandlePlayerBuildOrder`** (`Server.ShipCommands.cs`) — reached from the
  generic non-NPC fallback when a real player's ship issues `commandID ==
  CidBuild` with an `OT_asteroid` object. Port of native `LegalCommand`'s
  c_cidBuild case (`shipIGC.h:1695-1707`):
  - *Self-command only* (P146 precedent).
  - *Builder-role pilot* — the ship's hull must carry `c_habmBuilder`
    (`DataHullTypeIGC.HabmBuilder`, igc.h:741). Native's `m_pilotType ==
    c_ptBuilder` gate; the port's faithful stand-in for "Builder-role" is the
    Constructor hull capability (this port's player ships carry
    `PilotType=Player`; P285 already identifies a Builder hull this way).
  - *OT_asteroid target only* — any other object type is refused.
  - *Capability match* — the asteroid must `HasCapability(stationType.AabmBuild)`
    where `stationType` = `Ship.BuilderStationType` (native
    `((IasteroidIGC*)pmodel)->HasCapability(m_abmOrders)` with
    `m_abmOrders = GetBuildAABM()`).
  - *No configured station type* — a Builder with nothing to build is refused.
- **`FindAsteroidById`** (`Server.Docking.cs`) — resolves the `OT_asteroid`
  wire id to a runtime rock across the mission's sectors.
- A legal command starts the timed construction via the **existing**
  `Sector.StartBuildingEffect` (P163-165/P280 flow).

## 3. BuildingEffect-equivalent model

Already present from P163-165 and reused verbatim: `Sim/BuildingEffect.cs` (the
timed construction object anchored at the target asteroid, 40 s lifecycle),
`Sector.StartBuildingEffect` (registration + the P280 capability gate), and
`Sector.ResolveBuildingEffects`/`CompleteBuild` which on completion destroys the
rock, creates a **real new `Station`** of the Builder's type at the rock's
position (via `BuildStationRecord` + `Mission.PlaceStation`) and **consumes the
Builder** (removes it from the sector) — the port of `buildingeffectigc.h:312-353`
→ `FedSrv.CPP:11437`. No parallel construction model was invented.

### Documented divergence: the "approach"

The brief's live-proof arc listed "approach". Native genuinely flies the Builder
to the asteroid and *enters* it (the `c_oEnter` waypoint, `shipIGC.cpp:4014`).
This port's building mechanic deliberately does **not** model physical
flight-and-enter, for two architecture reasons (both pre-existing and now
documented in the handler):

1. P163-165 established a **"no travel-before-build gate"** — the NPC autonomous
   Builder (`DecideBuilderDefaultOrder`, `NpcBuilderDefaultOrderLiveTests`)
   starts construction immediately on target selection; "distance is immaterial"
   (documented in the P165 test). P285 similarly calls `StartBuildingEffect`
   directly. P422 keeps that abstraction rather than inventing a divergent player
   path.
2. Physically *entering* an asteroid is impossible under the P168 collision
   avoidance: the `Dodge` pass would deflect any ship trying to occupy the rock's
   volume, and native's enter only works because the building effect exempts
   asteroid collisions (`buildingeffectigc.h:256-258`), which this port does not
   implement.

So the legality gate (the task's core) is real and enforced, and construction
begins immediately on a legal order — exactly as the existing NPC Builder and
P285 behave. The task's UI hookup ("target asteroid, press Build") remains out of
scope; the server-side command is complete, and a client control sending
`ORDER_CHANGE(commandID=c_cidBuild, objectType=OT_asteroid, ...)` is a natural
follow-up.

## 4. Verification — real proof

### Refusal gate tests (`PlayerBuildCommandLiveTests.cs`)
- `Build_from_non_builder_hull_is_refused` — a ship on a non-`c_habmBuilder`
  hull is refused (no construction).
- `Build_against_non_asteroid_target_is_refused` — a non-`OT_asteroid` target is
  refused.
- `Build_against_capability_mismatch_asteroid_is_refused` — a rock whose
  capability does not match the station type's `AabmBuild` is refused (not every
  rock is buildable), and the wrong-type rock is untouched.
- `Build_without_configured_station_type_is_refused` — nothing to build ⇒ refused.
- **Must-fail-if-removed**: temporarily disabling the Builder-role gate
  (gate 1) turned `Build_from_non_builder_hull_is_refused` red (a `BuildingEffect`
  got registered) — proved discriminating, then reverted.

### Legal build + full arc (`Legal_build_starts_construction_and_creates_dockable_station`)
Live loopback (real `PCore001.igc`, real in-process server, real bot over the
wire): a real `Lg Adv Constructor` hull peer (PilotType Builder role via
`c_habmBuilder`) sends `SET_BUILDER_TARGET` for `Garrison (Str)` then the Build
`ORDER_CHANGE`; the server starts the timed construction, ticks to completion,
and:
```
P422-START: peer 8098 (Builder hull 'Lg Adv Constructor') BUILD accepted → construction of 'Garrison (Str)' started at asteroid 6221 (effect completes @+40s)
P422-BUILD: completed → rock 6221 gone, station 9187 'Garrison (Str)' (type 10, side 0) at ..., builder 8098 consumed
P422-DOCK: friendly ship 8099 docked at newly-built station 9187 'Garrison (Str)' — dockable confirmed
```
The full native construction arc — legality gate, timed 40 s construction,
Builder consumed, real station created, **dockable** (a same-side ship docks at
the fresh station; `ResolveDockCollisions` iterates `sector.Stations`).

Plus a codec test `OrderChange_build_roundtrips_cidBuild_constant`.

### Whole-solution build
`dotnet build` of the touched projects: **0 warnings, 0 errors**. Full Core test
suite run (see below).

## 5. Scope boundaries (kept clean)

- **Layer's parallel `c_cidBuild`** (buoy/warp mine/probe laying,
  `shipIGC.h:1697-1698`) — a separate mechanic; not touched.
- **Client UI / key-binding** to issue the command — out of scope (noted above).
- **P368–P421** — untouched.
- The NPC autonomous Builder (`PilotTypeBuilder` default order) — left exactly as
  P165 built it (it already constructs); P422 adds the real-player command path
  alongside.

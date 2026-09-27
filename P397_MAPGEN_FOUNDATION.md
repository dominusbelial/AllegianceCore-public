# P397 — Procedural mission-map generation: foundation + SingleRing

Task 1 of the (intentionally split) procedural mission-map generation port.
Native has a live fallback path this port never had: when no custom map file is
named, `missionigc.cpp:3902-3906` calls `ImapMakerIGC::Create(...)` to
procedurally generate the whole mission. This port's boot path
(`Server.Lifecycle.cs:666-670`) only ever loaded a real map file and threw
`InvalidDataException` when `ServerConfig.Map` was missing/invalid. This task
lands the shared infrastructure, the map-type dispatch, and exactly one real
topology — `SingleRing`, native's default/simplest — as a complete end-to-end
working proof. The other 14 topologies are out of scope (separate follow-up
tasks, once this foundation's real API is settled).

---

## 1. The architecture decision: a generated map IS a `MissionMap`

The central design question was how a generator-built sector/warp/station list
should reach `Mission.Assemble(StaticCore, MissionMap, IReadOnlyList<SideSpec>,
IIgcSite)` (`Mission.cs:425`), whose `Sectors` list is fixed at construction
and whose normal input is a file-decoded `MissionMap`.

**Decision: (b) — construct a synthetic in-memory `MissionMap` and flow it
through the existing `Assemble` path unchanged.**

`MissionMap` is already a thin, immutable holder of exactly the four map object
kinds the generator produces — `Clusters`, `Warps`, `Stations`, `Asteroids`
(`MissionMap.cs:17-38`). The generator is therefore "another producer of a
`MissionMap`", perfectly symmetric with `MissionMap.Load`. `Assemble` then does
everything a generated map needs for free:

- per-`OT_cluster` `Sector` construction (`Sector.cs:223`) — the exact
  `CreateObject(OT_cluster, &dc)` counterpart;
- warp `DestinationId` → destination-`Sector` resolution
  (`Mission.Assemble`, `Mission.cs:489-501`);
- station/asteroid attachment **including the runtime `Station`/`Asteroid`
  wrappers** (`AttachStations`/`AttachAsteroids`), so generated bases are
  dockable and generated rocks are minable with zero extra code;
- sector↔mission and side↔mission back-links.

This is a strict improvement over option (a) (a bespoke `Assemble` overload):
one assembly path, byte-for-byte identical for generated and file-loaded maps,
so the regression story is trivial (a generated map exercises the same
assembly the shipped corpus uses) and no `Sector.AttachWarp`/`AttachStations`
visibility widening or generator-specific branch in `Assemble` is needed. The
only change to `MissionMap` is one new public factory
`MissionMap.FromLists(...)` (`MissionMap.cs:58`) — the mirror of `Load`.

**Consequence for the primitives the brief suggested reusing:** `Sector.AttachWarp`
stays `internal`, and `Mission.PlaceStation`/`PlaceAsteroid` are not used by the
generator — the generator produces plain `DataWarpIGC`/`DataStationIGC`/
`DataAsteroidIGC` records, and `Assemble` attaches them exactly as it does a
file map's. `PlaceStation`/`PlaceAsteroid` remain the runtime injection seams
for synthetic tests, untouched. This is a deliberate, documented consequence of
choosing option (b), not an oversight.

---

## 2. Real API shape (what follow-up topology tasks must target)

New files (all in `Allegiance.Core`):

- `src/Allegiance.Core/MapGen/MapGenType.cs` — `enum MapGenType { SingleRing }`.
  The port of native's `MissionParams.mmMapType` dispatch switch
  (`mapmakerigc.cpp:129-201`). New topologies add enum values and a
  `case` in `MissionMapGenerator.Generate`.
- `src/Allegiance.Core/MapGen/MapGenParams.cs` — the config object the
  generator reads (port of the relevant `MissionParams` fields): `Teams`,
  `CivIds`, `MapSize`, the six per-sector asteroid counts, the four treasure
  counts (parsed, creation deferred), `He3Density`, `FlagsGame`,
  `ArtifactsGame`, and the (retained-but-inert) `ShowHomeSector`/`ShowMap`/
  `AllowAlliedViz`. Native defaults baked in.
- `src/Allegiance.Core/MapGen/MapGenContext.cs` — port of native `CMapData`
  (`mapmakerigc.h:40-175`): the 72-name cluster pool
  (`GetNewClusterName`, verbatim names from `mapmakerigc.cpp:21-41`), the
  25-poster planet pool, per-slot cluster/side arrays, team/neutral/other
  counts, and `GetNextWarpId()`. Internal — pure state plumbing.
- `src/Allegiance.Core/MapGen/MissionMapGenerator.cs` — the algorithm
  (`mapmakerigc.cpp:340-483` and its pieces). Public surface:
  `public static MissionMap Generate(MapGenParams pmp, StaticCore core, double now)`.
  Dispatches on `pmp.Type`, runs `GenerateLayout` (the base-class default =
  SingleRing), the minimal functional `PlaceWarps`, and `PopulateClusters`,
  then returns `MissionMap.FromLists(...)`.
  Private methods a future topology overrides by *replacing the private
  `GenerateLayout`/`LinkClusters()` bodies* (the port has no virtual dispatch —
  a future task swaps in the layout/link logic for its topology, which is the
  C# translation of native's `GenerateLayout`/`LinkClusters`/`GenerateTeamClusterScreenPosition`
  virtuals): `GenerateRingClusters`, `GenerateTeamCluster`,
  `GenerateNeutralCluster`, `LinkClusters()` (no-arg ring default),
  `LinkClusters(short, short, int)` (3-arg warp-pair builder),
  `CrossLinkClusters`, `GenerateStarbase`, `PopulateClusters`,
  `PopulateCluster`, `AddAsteroid`, `PlaceWarps`.

Changed files:

- `src/Allegiance.Core/Model/MissionMap.cs` — added `MissionMap.FromLists(...)`.
- `src/Allegiance.Server/ServerConfig.cs` — added per-game
  `GameConfig.MapGen` (nullable `MapGenConfig`) and the `MapGenConfig` class
  (nullable-override fields mirroring `MapGenParams`; absent = native default).
- `src/Allegiance.Server/ServerJsonContext.cs` — `[JsonSerializable(typeof(MapGenConfig))]`.
- `src/Allegiance.Server/Server.Lifecycle.cs` — the boot branch + the
  `ToCoreParams(MapGenConfig, int, int[])` mapper.

Tests: `tests/Allegiance.Core.Tests/MapGenFoundationTests.cs` (5 structural),
`tests/Allegiance.Core.Tests/MapGenLiveProofTests.cs` (2 live).

### How the generated ring links resolve (critical for follow-ups)

`LinkClusters(sId1, sId2)` builds a **bidirectional warp pair with consecutive
global ids**: warp `N` in cluster `sId1` with `DestinationId = N+1`, warp `N+1`
in cluster `sId2` with `DestinationId = N` (native `mapmakerigc.cpp:812-834`).
`Mission.Assemble` resolves a warp's destination sector as the owner of the
*partner* warp id (`Mission.cs:491-500`), so this consecutive-pair shape is
exactly what makes a generated pair traverse as a ring connection with zero
extra machinery. Every future topology must keep building warp pairs this way.

### The warp `DestinationId` ↔ partner warp ↔ destination-sector rule
Already covered above; the point to preserve is global uniqueness of warp ids
across the whole map (`GetNextWarpId`), which the consecutive-pair scheme
depends on.

---

## 3. `PopulateCluster` trace (the shared dependency every topology needs)

Native `CmapMakerIGC::PopulateCluster` is a thin wrapper
(`mapmakerigc.cpp:944-949`) over the free function
`::PopulateCluster(pmission, pmp, pcluster, amountHe3, bAsteroids=true,
bTreasures=true, …multipliers=1)` at `common.cpp:2963-3109`. Behaviour traced:

1. **Flags-game pedestals** (`common.cpp:2979-3001`) — creates flag pedestal +
   flag treasures at every station with `c_sabmStart`. **Deferred**: depends on
   treasure creation, itself deferred (see below).
2. **Station visibility reset** (`common.cpp:3003-3018`) — N/A: the port's
   visibility is dynamic per-tick (P132), no static flag to set.
3. **Asteroids** (`common.cpp:3020-3078`), gated on `bAsteroids=true`, three
   kinds, each count `n` derived from home-vs-neutral params and multiplied by
   the multiplier:
   - He3 minable (`IasteroidIGC::GetRandomType(c_aabmMineHe3)`),
     amount `amountHe3` ore each;
   - special (`GetSpecialAsterioid(pmp, offset++)`);
   - generic buildable (`GetRandomType(c_aabmBuildable)`).
   Each created by `CreateAsteroid` (`common.cpp:2844-2893`): random forward/up
   orientation, `radius = randomInt(def.radius, 2·def.radius)`,
   `signature = radius/100`, `rotation.axis=forward, angle=0.2/signature`,
   `ore *= amountHe3`, `oreMax = ore`, random `RandomPosition(...)`.
4. **Treasures** (`common.cpp:3080-3108`) — `GenerateTreasure` per count,
   plus an artifacts-game hack. **Deferred**: `MissionMap` has no treasure
   list, so generated treasures cannot ride the chosen option-(b) assembly
   path. The port's sector treasure pickup (P123) uses runtime `Treasure`
   models, not map records; a follow-up task should add a generated-treasure
   injection (likely a runtime `PlaceTreasure`-style seam or a generated
   `DataTreasureIGC` list threaded alongside `MissionMap`). Stated plainly so it
   is not silently lost.

**Port of `amountHe3`** (the caller `PopulateClusters`, `mapmakerigc.cpp:954-956`):
`GetFloatConstant(c_fcidAmountHe3)[9] · fHe3Density · teams /
GetMinableAsteroids()`, where `GetMinableAsteroids =
nPlayerSectorMineable·teamClusters + nNeutralSectorMineable·(otherClusters +
neutralClusters)` (`mapmakerigc.h:125-131`). The port reads `c_fcidAmountHe3`
from `core.Constants.FloatConstants[9]`, consistent with how P215/P279 read
indices 28/29/31. He3 rocks carry `Ore = OreMax = 1.0 · amountHe3`.

**Simplify-correctly:** the port's `AddAsteroid` has no `OT_asteroidType`
catalog (none exists in `StaticCore`), so it constructs the `DataAsteroidIGC`
directly from the requested `AabmCapabilities` (0x01 He3 / 0x10 special / 0x08
buildable) with fixed base radii and model names — the honest stand-in for
`GetTypeDefaults`. Rocks are genuinely minable: `Asteroid.CanMineHe3` reads
`AabmCapabilities & 0x01` and the mining pass (`Sector.cs:3120`) drains the
`Ore`/`OreMax` pool.

---

## 4. Other deliberate simplifications (stated, not silent)

- **The two native "cosmetic/random" passes are skipped.** The screen-position
  randomizer and the "random encounters" warp-pruning
  (`mapmakerigc.cpp:219-337, 362-424`) are out of scope: no map-editor screen
  exists and `iRandomEncounters` is not modelled. **But** the warp *world*
  position is functionally required (the transit sim consumes it), so
  `PlaceWarps` lays each sector's warps evenly on a ring at
  `0.6 · c_radiusUniverse (5000)` from the sector origin — deterministic, not
  the shuffled cosmetic version.
- **Reveal/Activate side steps** (`mapmakerigc.cpp:969-1070`) are N/A: the
  port's visibility is dynamic (P132) and `Side.Active` defaults true (P202).
- **Starbase successor leg skipped.** Native calls
  `GetSuccessorStationType` to allow a developed upgrade (`mapmakerigc.cpp:929`);
  a fresh game has no development, so the civ's `InitialStationTypeId` is the
  faithful no-dev choice.
- **Treasure creation deferred** (see §3).
- **The `Mission` is generated with `now=0`** at boot; timestamps are not
  carried by the port's records.

---

## 5. Verification (real proof)

### Structural unit tests — `MapGenFoundationTests` (5)
Against a real corpus core, generate a 2-team `SingleRing` (iMapSize 0 →
`(1+1)·2 = 4` sectors):
- `SingleRing_TwoTeams_ProducesFourSectors` — 4 clusters; exactly 2 home, 2
  neutral.
- `SingleRing_EachSectorLinksItsTwoRingNeighbours` — every sector has exactly 2
  warps whose destinations are its two ring neighbours `(i±1) mod 4`.
- `SingleRing_HomeSectorsGetStations_NeutralSectorsHaveNone` — home sectors
  each carry 1 station; neutrals 0.
- `SingleRing_EverySectorHasMinableAsteroids` — every sector has ≥1
  `CanMineHe3` rock.
- `SingleRing_RequiresTwoOrMoreTeams` — 1 team throws (native `nTeams≥2`).

**Must-fail-if-removed (verified red):** suppressing the no-arg `LinkClusters()`
makes the ring-neighbour test fail (0 warps/sector); suppressing
`GenerateStarbase` makes the home-station test fail (empty); suppressing the
He3 loop makes the minable-asteroid test fail (no `CanMineHe3` rock).

### Live proof — `MapGenLiveProofTests` (2)
Real in-process `Server` booted with `GameConfig.MapGen = SingleRing` (no map
file), real `FltBot` client over the real TCP transport, real `Mission`
assembled from the generated `MissionMap`:
- `GeneratedSingleRing_PeerFliesThroughWarp_ToRingNeighbour` — bot spawns in a
  generated sector (id 0..3), is placed inside a generated warp's collision
  volume, transits (`ServerHost.HitWarpEvent`), receives a second `SET_CLUSTER`;
  the server-side sector change agrees **and the destination is a ring
  neighbour of the origin** — transit follows the generated ring.
- `GeneratedSingleRing_DefaultSpawnedPeer_DocksAtGeneratedHomeStation` — with
  the **default** spawner (which resolves the peer's side-home via
  `HomeSectorForSide`, i.e. the generated station-owned cluster), the peer is
  seated to its side, its home sector is a generated `HomeSector`, and driving
  it into the generated home station's `DockRadius` docks it — the generated
  base is genuinely dockable.

### Boot-path + regression
`Server` run headless against a generated config booted:
```
[boot] game 'Generated SingleRing' generating map: topology=SingleRing, 4 sectors / 8 warps / 2 stations / 80 asteroids
[initial-miner] side 0 spawned initial miner ... docked at station 0 'Garrison' in 'Cirt'
[initial-miner] side 1 spawned initial miner ... docked at station 1 'Garrison' in 'Titty Baby'
```
— the initial miners docked at the generated home stations ('Cirt'/'Titty
Baby' from the name pool). The same server against a **plain** (no `MapGen`)
config took the untouched file-load branch verbatim
(`map=XXV_simple.igc, 13 sectors, …`), proving `ServerConfig.Map` remains
fully backward compatible. The only change to the file path is wrapping the
original three statements in an `else` block.

### Build / suite
Whole solution builds 0 warnings / 0 errors. Full `Allegiance.Core.Tests` run
N times: only the pre-existing documented live-wire timing flakes expected
(confirmed at baseline before this task).

---

## 6. Scope boundaries

- **Implemented:** `MapGenContext`, dispatch (`MapGenType.SingleRing`),
  cluster generation, the bidirectional warp-pair builder, `SingleRing`'s
  default ring layout, `GenerateStarbase`, `PopulateCluster` (asteroids), the
  synthetic-`MissionMap` boot hook, the opt-in `GameConfig.MapGen` config.
- **Deferred (explicit follow-up tasks):** the other 14 topologies; cosmetic
  screen-position randomization (no editor screen); the "random encounters"
  warp-pruning; treasure creation.
- Untouched: P368–P396, all other subsystems.

Relationship of the pieces: `MissionMapGenerator.Generate` is the live
fallback `missionigc.cpp:3902-3906` reached this port never had; it composes
the shared algorithm into a `MissionMap` that `Mission.Assemble` consumes
identically to a file-decoded one.

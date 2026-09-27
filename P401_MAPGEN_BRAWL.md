# P401 — Procedural mission-map generation: Brawl (task 5 of 5, final topology)

Completes the procedural map-generation port. Following P397 (foundation +
`SingleRing`), P398 (ring family: `DoubleRing`, `BigRing`, `HiLo`, `HiHigher`,
`LargeSplit`, `InsideOut`), P399 (radial family: `Star`, `PinWheel`,
`DiamondRing`, `SnowFlake`) and P400 (geographic family: `Grid`, `EastWest`,
`SplitBase`), this task lands **`Brawl` — the 15th and final native topology**.
With it, **all 15 native procedural map types are ported.**

`Brawl` is structurally the odd one out. Every prior topology builds a
multi-cluster layout — team/neutral clusters linked by warps. `Brawl` builds a
**single shared sector** with every side's home station co-located inside it
and **zero inter-cluster links**. Its whole purpose is that all sides start in
one arena where combat and mining are immediately possible with no travel —
demonstrated live in §5.

---

## 1. The traced algorithm (native `CmapMakerBrawlIGC`)

All against `~/Development/Allegiance/src/Igc/mapmakerigc.cpp`
(class decl at `mapmakerigc.h:411-431`; enum value `c_mmBrawl = 6`,
`igc.h:983`).

### `GenerateLayout` (`cpp:1707-1719`)
```
SetTeamClustersPerTeam(0)
SetOtherClusters(1)
GenerateRingClusters()
LinkClusters()
```
Zero team clusters and exactly one "other" (neutral) cluster — the whole map is
that single neutral cluster. With `TeamClusters 0 + OtherClusters 1` the port's
`MapGenContext` derives `TotalClusters = 0·Teams + 0 + 1 = 1`, and the shared
`GenerateRingClusters` (unchanged from P397) produces exactly one neutral
cluster, id 0. Brawl pins these counts directly rather than through the
`SetTeamNeutral` MapSize helper, matching native's explicit setters.

### `LinkClusters` (`cpp:1732-1734`) — a real, confirmed empty no-op
The native body is **completely empty**. This is CORRECT, not an accidental
gap: with only one cluster in the whole map there is nothing to link. Rather
than silently omitting a link step, the port calls an explicit
`LinkClustersBrawl()` method (documented empty) so the no-op is provable — the
structural test asserts a Brawl map has exactly **zero warps**.

### `PopulateCluster` override (`cpp:1737-1798`) — the most substantial yet
Runs in place of the base population for the single shared cluster
(`PopulateClusters`, `cpp:952-967`; the cluster is not a home sector so
`GenerateStarbase` is not called):

1. **One station per side**, all in the single shared cluster, at
   `angle = 2π·sideID/teams`, radius `c_startRadius = 2500.0f`
   (`cpp:1744-1788`). Each station uses that side's own
   `GetCivilization()->GetInitialStationType()` (its civilization's starting
   station type — the port resolves the same per-side lookup as
   `GenerateStarbase`: `CivIds[sideID]` → civilization → `InitialStationTypeId`),
   full hull/shield (`bpShield = bpHull = 1.0f`), owned by that side
   (`ds.sideID = sideID`), `stationID = GenerateNewStationID()`, named after
   the station type.
2. **3-5 random asteroids** (`cpp:1790-1793`): `randomInt(3, 5)` — inclusive
   of both bounds (`zmath.h:125-127`, `min + rand()%(1+max-min)`) → 3, 4 or 5.
   Each is `IasteroidIGC::GetRandomType(0)` (`asteroidigc.cpp:238-240`) — a
   "huge" neutral asteroid (types 0/1, capabilities 0, ore 0) — placed via the
   shared `CreateAsteroid` port with 0 ore.
3. **The shared free function `::PopulateCluster`** (`common.cpp:2963-3109`),
   the same one every topology's population flows through, called as:
   ```cpp
   ::PopulateCluster(pmission, pmp, pcluster, amountHe3,
                   true, true,
                   1, -3 * (pMapData->GetTeams()/2));
   ```

#### Resolved: the 9th-argument default and the negative multiplier
Only **8 of 9** arguments are passed to the free function; the declaration in
`igc.h:4649-4657` supplies the default **`nAsteroidsMultiplier = 1`**. So
Brawl's effective multipliers are `(nMineable=1, nSpecial=-3·(teams/2),
nAsteroids=1)`.

The negative `nSpecialAsteroidsMultiplier` is the first time any ported
topology passes a negative value, and it is real, deliberate native behaviour:
the free function's multiplier branches (`common.cpp:3024-3032`,
`3042-3050`, `3063-3071`) treat a **non-negative** multiplier as a scalar on
the home-or-neutral base count, but a **negative** multiplier as an
**absolute count** — `n = -multiplier`, independent of the base:

```cpp
if (nSpecialAsteroidsMultiplier >= 0)
    nSpecialAsteroids = bHomeSector ? nPlayerSectorSpecialAsteroids
                                    : nNeutralSectorSpecialAsteroids;
    nSpecialAsteroids *= nSpecialAsteroidsMultiplier;
else
    nSpecialAsteroids = -nSpecialAsteroidsMultiplier;
```

So Brawl's single (neutral) cluster gets **exactly `3·(teams/2)` special
asteroids** regardless of the neutral-special base. `3·(teams/2)` uses integer
division, as native. The port previously had no occasion to represent the
negative branch (P400 stated it as unreachable because all then-ported
multipliers were non-negative); P401 adds it to the shared `PopulateCluster`
via an `ApplyMultiplier(base, mult)` helper (`mult >= 0 ? base·mult : -mult`),
which Brawl is the first to exercise. The generic/buildable multiplier stays 1
(the free-function default), so Brawl's generic count is the ordinary neutral
base.

Net population for a 2-team Brawl (default params: neutral mine/special/generic
= 8/2/8), all in the one shared sector:
  - 8 mineable (8 × 1) He3 rocks — carries the map's `AmountHe3` ore;
  - 3 special (−3·(2/2) absolute) rocks;
  - 8 generic (8 × 1) buildable rocks;
  - 3-5 random "huge" (capabilities 0) scatter rocks;
  - 2 stations (one per side), 0 warps, 1 cluster.
Total asteroids 22-24 (observed 23 in the boot log).

`GenerateNeutralClusterScreenPosition` (`cpp:1722-1729`, zeroes screenX/Y)
remains out of scope — the same screen-position/map-editor simplification every
prior task made.

## 2. `IsValid` — vestigial NULL stub (confirmed)

`CmapMakerBrawlIGC::IsValid` (`cpp:1695-1704`) has its **only real check
commented out** and returns NULL:

```cpp
// yp your_persona removed march 24 2006
//if (pmp->nMaxPlayersPerTeam * pmp->nTeams > 36)
//    return("Brawl maps must have less than 37 players; ...");
return(NULL);
```

Like PinWheel's and SplitBase's (P399/P400), it adds **no** constraint beyond
the base `nTeams >= 2`. It is ported faithfully — i.e. **not enforced** — and
this report states that plainly rather than fabricating a check native no
longer has. Verified by `Brawl_HasNoExtraValidityConstraint`: a 3-team config
is accepted and still yields one shared cluster / zero warps / one station per
side.

## 3. Dispatch / config wiring

- `MapGenType.Brawl = 6` (`igc.h:983`), so a config naming the native value
  maps onto the topology.
- `MissionMapGenerator.Generate` dispatches `Brawl` → `GenerateLayout` →
  `PlaceWarps` → `PopulateClusters` (the established P398 shape).
- `GenerateLayout` has a `Brawl` case; `LinkClustersBrawl` is the explicit
  no-op; `PopulateCluster` has the Brawl override.
- `Server.Lifecycle.ToCoreParams` resolves `MapGenConfig.Type` via
  `Enum.TryParse<MapGenType>`, so `Brawl` became config-selectable with no new
  wiring; only the "expected one of …" error text was refreshed to list all 15
  names.

## 4. Scope boundaries

- **Implemented:** `Brawl` end-to-end; `IsValid` vestigial-stub verdict; the
  negative (absolute-count) multiplier branch in the shared `PopulateCluster`;
  config-selectability; structural + live + boot + must-fail verification.
- **Deferred (unchanged from prior tasks):** screen-position/map-editor
  cosmetics; the `iRandomEncounters` warp-pruning (irrelevant — Brawl has no
  warps); treasure creation (still deferred per P397).
- Untouched: P368–P400, all other subsystems. The 14 prior topologies and the
  real map-file load path are regression-proven unchanged (§5).

## 5. Verification (real proof)

### Structural — `MapGenBrawlTests` (6)
Derived from the native C++ (cited in §1), not the port re-reading its own
logic:

| Assertion | Result |
|---|---|
| `Brawl_SingleSharedCluster_ZeroWarps` | exactly 1 cluster (id 0, neutral, not home) and **0 warps** — the empty `LinkClusters` is a real no-op, not a gap |
| `Brawl_TwoTeams_OneStationPerSide_AtCircularPositions` | 2 stations, both in cluster 0, at (2500,0,0) and (−2500,0,0) — radius 2500, angles 0 and π, z 0 |
| `Brawl_StationsUseCorrectInitialStationTypeAndOwner` | 4 teams → 4 stations, each `StationTypeId` = its side's civ `InitialStationTypeId`, owned by that side, named after the type, bpHull=bpShield=1 |
| `Brawl_ThreeToFiveScatterAsteroids` | exactly 3-5 capability-0 ("huge") rocks |
| `Brawl_NegativeSpecialMultiplier_GivesAbsoluteCount` | 8 mineable, **3** special (abs −3, NOT the base 2), 8 generic — negative multiplier = absolute count |
| `Brawl_HasNoExtraValidityConstraint` | 3-team accepted → 1 cluster / 0 warps / 3 stations / 3 special |

**Must-fail-if-removed (verified red, then restored):** temporarily disabling
the Brawl `PopulateCluster` branch made exactly the five override-dependent
tests fail (stations absent, scatter gone, special collapsed from 3 to the base
2), while `Brawl_SingleSharedCluster_ZeroWarps` stayed green — proving the
override is what produces the stations/scatter/special counts.

### Live proof — `MapGenBrawlLiveProofTests` (2)
Real in-process server booted with `GameConfig.MapGen = Brawl` (no map file),
real `FltBot` clients over the real TCP transport:

- `Brawl_BootHeadless_ConfigSelectable` — Brawl + a plain `Map`-file regression
  game boot through `ToCoreParams → Generate → Assemble`:
  ```
  [boot] game 'Brawl' generating map: topology=Brawl, 1 sectors / 0 warps / 2 stations / 23 asteroids
  [boot-regression] game 'PlainFileLoad' loaded file map: 13 sectors
  ```
- `Brawl_TwoSidesShareOneSector_EachDocksAtOwnHomeStation` — **two real
  clients on two different sides** (side 0 and side 1) join the generated Brawl
  map. Both ships are in the SAME single sector, and each docks at its OWN
  correctly-placed home station in that shared sector — combat and mining are
  immediately possible (no warps, no travel). Captured log:
  ```
  [initial-miner] side 0 spawned initial miner ... docked at station 0 'Garrison' in 'Louhi'
  [initial-miner] side 1 spawned initial miner ... docked at station 1 'Garrison' in 'Louhi'
  [spawn] ship 8098 seated on side 0 (Alpha) in its home sector 'Louhi' (id 0)
  [spawn] ship 8099 seated on side 1 (Bravo) in its home sector 'Louhi' (id 0)
  [dock] ship 8098 docked at station 0 'Garrison' (side 0) in sector 'Louhi'
  [dock] ship 8099 docked at station 1 'Garrison' (side 1) in sector 'Louhi'
  P401-BRAWL-LIVE: sides 0 and 1 both seated in the single shared sector 'Louhi' (id 0);
      side 0 docked at station 0 'Garrison', side 1 docked at station 1 'Garrison'
      — combat/mining immediately possible, no warps
  ```
  (The default spawner seats peer N → side N and P368 resolves each side's home
  to the sector holding its owned station — for Brawl that is the one sector.)

### Regression
- All **87 map-gen tests** across P397 (7), P398 (28), P399 (26), P400 (18)
  and P401 (8) pass together — all 14 prior topologies unaffected.
- Real map-file configs: none use `MapGen`; the file-load branch is untouched
  (re-proved in the boot test above — `PlainFileLoad` loads 13 sectors).
- Whole-solution build: **0 warnings / 0 errors.**
- Full `Allegiance.Core.Tests` suite run 2×: only the pre-existing documented
  live-wire timing flakes expected (rotating set, green in isolation), none
  touch map generation.

Files changed:
- `src/Allegiance.Core/MapGen/MapGenType.cs` (enum: Brawl = 6 + docs)
- `src/Allegiance.Core/MapGen/MissionMapGenerator.cs` (dispatch + GenerateLayout case + LinkClustersBrawl no-op + Brawl PopulateCluster override + ApplyMultiplier negative branch + PlaceBrawlStations + AddBrawlScatterAsteroids)
- `src/Allegiance.Server/Server.Lifecycle.cs` (error-message text only)
- `tests/Allegiance.Core.Tests/MapGenBrawlTests.cs` (new)
- `tests/Allegiance.Core.Tests/MapGenBrawlLiveProofTests.cs` (new)

---

## README summary (one paragraph, foldable into the map-generation status)

Procedural map generation supports all 15 native topologies, landed across
P397-P401: the ring family (`SingleRing`, `DoubleRing`, `BigRing`, `HiLo`,
`HiHigher`, `LargeSplit`, `InsideOut`), the radial family (`Star`, `PinWheel`,
`DiamondRing`, `SnowFlake`), the geographic family (`Grid`, `EastWest`,
`SplitBase`), and `Brawl`. Most build a multi-cluster map linked by warps;
`Brawl` is the exception — a single shared sector with every side's home
station co-located inside it and no warps, so all teams start in one arena
where combat and mining are immediately possible. Maps are generated
procedurally when a mission names a `MapGen` type instead of a map file, flow
through the same `Mission.Assemble` path as a file-loaded map, and every
topology is verified by structural unit tests (exact sector/home/warp graphs
traced from native), live client proofs (real bots transit generated warps and
dock at generated home stations), and a full regression against the prior
topologies and the file-load path.

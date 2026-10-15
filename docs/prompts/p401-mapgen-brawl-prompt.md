# P401 — procedural map generation: Brawl (task 5 of 5, final topology)

## Why this task exists

P397 (foundation + SingleRing), P398 (ring family, 6 topologies), P399
(radial family, 4 topologies), and P400 (Grid/EastWest/SplitBase) are
landed or landing — **read all four reports (`P397_MAPGEN_FOUNDATION.md`,
`P398_MAPGEN_RING_FAMILY.md`, `P399_MAPGEN_RADIAL_FAMILY.md`,
`P400_MAPGEN_GRID_FAMILY.md`, all in the repo root) before starting**.
14 of 15 native map types are done. `Brawl` is the last one, and it is
structurally the odd one out — every prior topology builds a
multi-cluster layout with team/neutral clusters linked by warps; `Brawl`
builds a **single shared sector** with every side's home station
co-located in it and zero inter-cluster links. Once this lands, all 15
native procedural map types are ported.

## Confirmed real native mechanic (already traced this session)

All in `~/Development/Allegiance/src/Igc/mapmakerigc.cpp` (class decl at
`mapmakerigc.h:411`):

- **`IsValid`** (`cpp:1695-1704`) — a vestigial NULL stub, same class as
  PinWheel's from P399: the real check (`nMaxPlayersPerTeam * nTeams >
  36` rejected) was commented out March 24 2006 (`"yp your_persona
  removed march 24 2006"`). Confirm this yourself against the current
  source (don't just trust this brief) and document it plainly the same
  way P399 documented PinWheel's — do not port a check that native itself
  no longer enforces.
- **`GenerateLayout`** (`cpp:1707-1719`):
  ```cpp
  pMapData->SetTeamClustersPerTeam(0);
  pMapData->SetOtherClusters(1);
  this->GenerateRingClusters(pMapData);
  this->LinkClusters(pMapData);
  ```
  Zero team clusters, exactly one "other" (neutral) cluster — i.e. the
  entire map is that single cluster. Reuses the shared `GenerateRingClusters`
  primitive from P397 (unchanged) with these params.
- **`LinkClusters`** (`cpp:1732-1734`) — **completely empty body**. With
  only one cluster in the whole map, there is nothing to link. Do not
  port this as a no-op that silently "does nothing wrong" without
  understanding why — confirm explicitly (via a test) that a Brawl map
  has zero warps and that's correct, not a bug.
- **`PopulateCluster` override** (`cpp:1737-1798`) — the real content of
  this topology, a full override (same category as EastWest's from
  P400 and PinWheel's from P399, but the most substantial one yet):
  1. **One station per side**, placed in the single shared cluster, at
     `angle = 2π * sideID / teams`, radius `2500.0` — i.e. evenly spaced
     around a circle centered on the cluster (`cpp:1744-1788`). Each
     station uses that side's own `GetInitialStationType()` (its
     civilization's starting station type — check what this port's
     equivalent lookup is; likely the same per-side civ/hull-tree data
     already used elsewhere for initial ship/station types, not the
     fixed `StationTypeId = 15` pattern from P379's debug topology,
     which is unrelated scope), full hull/shield (`bpHull = bpShield =
     1.0`), owned by that side (`ds.sideID = sideID`).
  2. **3-5 random asteroids** (`cpp:1790-1793`,
     `randomInt(3,5)` inclusive-ish — confirm the exact bound
     semantics against this port's existing `randomInt` helper usage
     elsewhere, e.g. how P397/P398 already handle native's inclusive
     random ranges), reusing the existing `CreateAsteroid`/
     `IasteroidIGC::GetRandomType` port from the shared primitives.
  3. A call into the **shared free-function `PopulateCluster`**
     (`common.cpp:2963-...`, the same one every other topology's
     population goes through — signature: `pmission, pmp, pcluster,
     amountHe3, bAsteroids, bTreasures, nMineableAsteroidsMultiplier,
     nSpecialAsteroidsMultiplier, nAsteroidsMultiplier`), called as:
     ```cpp
     ::PopulateCluster(pMapData->GetMission(), pMapData->GetMissionParams(), pcluster, amountHe3,
                     true, true,
                     1, -3 * (pMapData->GetTeams()/2));
     ```
     Only 8 of 9 args are passed — trace the header/call site yourself to
     find `nAsteroidsMultiplier`'s default (not resolved in this
     session's research pass) and confirm this port's already-ported
     shared `PopulateCluster` (from P397) either has a matching default
     or needs the same explicit multiplier values threaded through. The
     negative `nSpecialAsteroidsMultiplier` (`-3 * teams/2`) is a real,
     deliberate native value — port it exactly, don't round or clamp it
     without understanding what negative means in that param's existing
     ported semantics (check the P397-ported version of this shared
     function for what a negative multiplier does there).

Screen-position overrides (`GenerateNeutralClusterScreenPosition`,
`cpp:1722-1729` — just zeroes screenX/screenY) remain out of scope, same
simplification as every prior task.

## Scope

1. Add `Brawl` to `MapGenType` and wire it into
   `MissionMapGenerator`'s dispatch (P398's established shape).
2. Port `GenerateLayout` — zero team clusters, one shared cluster, no
   linking.
3. Port the empty `LinkClusters` explicitly (a real, confirmed no-op —
   not an accidental gap) with a test proving zero warps in the
   resulting map.
4. Port the full `PopulateCluster` override: per-side circularly-placed
   home stations in the one shared cluster, 3-5 random asteroids, and
   the shared-`PopulateCluster` call with its exact multiplier
   arguments.
5. Verify and document the `IsValid` vestigial-stub verdict.
6. Wire into `MapGenConfig`/`ToCoreParams` exactly as every prior task.

## Explicitly out of scope

- Screen-position/map-editor cosmetics.
- Treasure generation — still deferred per P397, unless trivially in
  scope (say so plainly if so).
- Any other topology — all 14 others are done as of P400.
- P368-P400 — unrelated, don't touch.

## Verification — real proof, not just build/test

- **Structural, required**: a test proving a Brawl map has exactly 1
  cluster, `teams` stations at the correct circular positions/radius,
  0 warps, 3-5 asteroids, each station owned by its correct side and
  using the correct initial station type. A must-fail-if-removed proof
  for the `PopulateCluster` override (removing it should leave stations
  absent/misplaced in a way the test catches).
- **`IsValid`, required**: confirm and document the real-vs-vestigial
  verdict the same way P399 did for PinWheel's.
- **Live proof, required**: connect real bots from at least 2 different
  sides into a Brawl-generated map, confirm each spawns/docks at its own
  correctly-placed home station in the single shared sector, and that
  combat/mining is immediately possible since all sides share the one
  sector (no travel needed) — this is the topology's whole point, so
  the live proof should specifically demonstrate it.
- **Regression, required**: all 14 prior topologies and every real
  map-file config remain unaffected.
- Whole-solution build: 0 warnings, 0 errors.
- Full test suite, run at least 2x: only the pre-existing documented
  live-wire timing flakes expected.

## Deliverables

1. `Brawl`, committed on `master` (not pushed — Domingo pushes). This
   completes all 15 native procedural map types.
2. Report doc `P401_MAPGEN_BRAWL.md` in the repo root: the traced
   algorithm, the `IsValid` verdict, the resolved default-arg question
   for the shared `PopulateCluster` call, and the verification
   performed. Note explicitly that this completes the full 15/15
   topology set, and give a one-paragraph summary suitable for folding
   into the README's map-generation status.

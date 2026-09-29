# P398 — Procedural mission-map generation: ring-family topologies (task 2 of ~5)

Follows P397, which landed the shared foundation + `SingleRing` and explicitly
deferred the rest of the dispatch as "a future task swaps in the layout/link
logic for its topology." This task is that future task: it introduces the real
per-topology dispatch and lands the remaining 6 ring-family map types —
`DoubleRing`, `BigRing`, `HiLo`, `HiHigher`, plus `LargeSplit` and `InsideOut`
(the two that come free as zero-override subclasses of `HiHigher`).

---

## 1. Dispatch shape chosen

The brief offered a hypothesis ("a switch over `MapGenType` calling
topology-specific private methods") and told me to read the real code and
decide, not assume. Reading the actual P397 `MissionMapGenerator` confirmed the
port has no virtual dispatch (a single `sealed` class with private methods) and
`Generate` already switches over `pmp.Type`. So the faithful, minimal shape is:

- `Generate` keeps switching over `MapGenType`, but the switch now lists all 7
  implemented values and calls the single `GenerateLayout()`.
- `GenerateLayout()` is a per-topology switch that (a) sets the cluster counts
  and calls the shared `GenerateRingClusters()`, then (b) calls that topology's
  `LinkClusters*` method — mirroring native exactly, where each topology's
  `GenerateLayout` sets counts, calls `GenerateRingClusters`, then calls its own
  virtual `LinkClusters`.
- Four new private methods port each topology's `LinkClusters` override:
  `LinkClustersDoubleRing`, `LinkClustersBigRing`, `LinkClustersHiLo`,
  `LinkClustersHiHigher` (the last shared by `HiHigher`/`LargeSplit`/`InsideOut`,
  matching native's single `CmapMakerHiHigherIGC::LinkClusters` that branches on
  `mMMID`).
- The topology is held in a new `_type` field (the port's analogue of native's
  `CmapMakerIGC::mMMID`), so the shared HiHigher layout/link code can branch on
  it exactly as native does.

This keeps every topology building its warp pairs through the same
3-arg `LinkClusters(short, short, int)` bidirectional warp-pair builder that
P397 proved (consecutive global warp ids → `Mission.Assemble`'s
destination-resolution works) — no topology hand-rolls a warp pair. `PlaceWarps`
and `PopulateClusters` remain shared and run after every topology, exactly as
native's shared `GenerateMission` does.

### Enum values

Added to `MapGenType` with the native `c_mm*` values so a config naming the
native value maps onto the same topology (values from `igc.h:977-991`):

```
SingleRing = 0   (P397)
DoubleRing = 1
BigRing    = 7
HiLo       = 8
HiHigher   = 9
InsideOut  = 11
LargeSplit = 14
```

Because `Server.Lifecycle.ToCoreParams` already resolves `MapGenConfig.Type`
via `Enum.TryParse<MapGenType>`, adding the enum values makes all six new
types selectable from server config with zero additional wiring — only the
help text / error message were refreshed to list the supported names.

---

## 2. Per-topology traced algorithms (native citations)

All against `~/Development/Allegiance/src/Igc/mapmakerigc.cpp` (and `.h` for
class shape). Screen-position overrides
(`Generate*ClusterScreenPosition`) are the 2D map-editor cosmetic placement —
out of scope, same as P397.

### DoubleRing — `mapmakerigc.h:282`, `mapmakerigc.cpp:1076-1155`
Overrides **only `LinkClusters`** (no `GenerateLayout` → uses the base counts:
team-per-team = `MapSize!=0 ? 2 : 1`, neutral-per-team = 1). Two concentric
rings: first each team's group links inward to its neutral + the wrapped
neighbour; then an outer ring chains the team (owned) sectors and an inner ring
chains the neutral sectors. Note the degenerate 2-team / iMapSize-0 case: with
2 sectors per "ring" the double ring collapses to a complete graph K4 (every
pair linked) — this is the genuine native output and is asserted as such.

### BigRing — `mapmakerigc.h:384`, `mapmakerigc.cpp:1160-1242`
Overrides `GenerateLayout` (sets neutral-per-team = 2 — the shape comment says
"2 neutral sectors between home sectors") and `LinkClusters`. Linking: a plain
ring (every sector links the previous one), then for >2 teams extra cross links
off each team's first neutral sector (+ one more when >3 teams).

### HiLo — `mapmakerigc.h:307`, `mapmakerigc.cpp:1246-1369`
Overrides `GenerateLayout` (neutral-per-team = 3, **other = 1**) and
`LinkClusters`. One "low" ring of 3 neutral sectors per team around a single
central (other) sector. The intra-team split link runs only when
team-per-team ≠ 1; each team's inner neutral links to the centre, its own home,
the next team's home, and its two outer neutrals chain onward.

### HiHigher — `mapmakerigc.h:332`, `mapmakerigc.cpp:1374-1544`
Overrides `GenerateLayout` (neutral-per-team = 5, other = 1) and `LinkClusters`
(read to the end: the function ends at line 1544). The richest ring-family
layout. Its `GenerateLayout` already branches on `mMMID` for team-per-team
(`mapmakerigc.cpp:1382-1388`) and its `LinkClusters` branches on `mMMID` in
three places (1385, 1412 screen-only, 1485, 1511) — the port replicates this by
branching on `_type`. The `iRandomEncounters != 0` tail link
(`mapmakerigc.cpp:1537-1541`) is skipped: `iRandomEncounters` is not modelled
(same documented P397 simplification as the warp-pruning pass).

### LargeSplit — `mapmakerigc.h:523`, `mapmakerigc.cpp:2243-2257`
**Free-reuse claim CONFIRMED by my own trace**: `class
CmapMakerLargeSplitIGC : public CmapMakerHiHigherIGC` with no member besides a
constructor and `IsValid`. Its `IsValid` (`mapmakerigc.cpp:2243-2251`) returns
NULL (the old "≥10 players/team" check is commented out, dated March 2006), and
the constructor (`2253-2257`) only sets `mMMID = c_mmLargeSplit` + a name.
There is **no** `GenerateLayout`/`LinkClusters` override — the inherited
HiHigher code runs, differing only via the `mMMID == c_mmLargeSplit` branches:
team-per-team pinned to **2** and the intra-team split link skipped.

### InsideOut — `mapmakerigc.h:531`, `mapmakerigc.cpp:2259-2263`
**Free-reuse claim CONFIRMED**: `class CmapMakerInsideOutIGC :
public CmapMakerHiHigherIGC`; `IsValid` returns NULL (inline, `.h:534-537`);
constructor (`2259-2263`) only sets `mMMID = c_mmInsideOut` + name. Inherited
HiHigher code, differing via the `mMMID == c_mmInsideOut` branches: team-per-team
pinned to **1**, the home cluster sits at the innermost position, and the
home→centre warp uses the **hidden-to-player aleph** kind
(`mapmakerigc.cpp:1521`).

### Deliberate simplifications (stated, not silent)
- Screen-position / map-editor cosmetics: out of scope (same as P397).
- `iRandomEncounters` tail links: skipped (not modelled; same basis as P397).
- Treasure generation: not touched — still deferred pending a real
  generated-treasure injection seam (P397 §3). This task neither added nor
  silently expanded into it.

---

## 3. Config wiring

`MapGenConfig.Type` was already resolved generically via
`Enum.TryParse<MapGenType>`, so all 7 names parse with no new code. Only
surface text updated: the `MapGenConfig` XML doc, `MapGenParams.Type` doc, and
the `ToCoreParams` "expected …" error message (now lists all seven names).
No real map-file config uses `MapGen`, so no shipped config changes — the
file-load branch is untouched.

---

## 4. Verification (real proof)

### Structural — `MapGenRingFamilyTests` (25 tests: 6 topologies × 4 theories + 1 fact)
All against a real corpus core, 2 teams, iMapSize 0 (LargeSplit/InsideOut pin
their own team-per-team regardless). For every topology: exact sector count
(from the real cluster-count formula), the **exact warp-adjacency graph**
(hardcoded from an independent trace+simulation of native, not from the port
reading its own logic), home starbases at team clusters only, and ≥1 genuinely
minable He3 rock in every sector.

| Topology | Sectors | Edges | Home sectors |
|---|---|---|---|
| DoubleRing | 4 | 6 (K4) | 0, 2 |
| BigRing | 6 | 6 | 0, 3 |
| HiLo | 9 | 12 | 0, 4 |
| HiHigher | 13 | 20 | 0, 6 |
| InsideOut | 13 | 20 | 0, 6 |
| LargeSplit | 15 | 20 | 0, 1, 7, 8 |

**Must-fail-if-removed proof (verified red):** suppressing the
`LinkClustersDoubleRing()` call made `DoubleRing_TwoTeam_IsFullK4` fail with 0
warps per sector (observed during development, then restored). This is the
technique P397 used, applied to the new dispatch.

### Live proof — `MapGenRingLiveProofTests` (3 tests, `HiLo`)
- `GeneratedHiLo_PeerFliesThroughWarp_ToTracedNeighbour`: a real `FltBot` over
  the real TCP transport joins a server booted with `MapGen = HiLo` (no map
  file), is spawned inside a generated warp's collision volume, transits
  (`ServerHost.HitWarpEvent`), and arrives in a sector that is a **genuine HiLo
  neighbour** of the origin per the traced adjacency — the generated nontrivial
  topology is traversable on the real wire path.
- `GeneratedHiLo_DefaultSpawnedPeer_DocksAtGeneratedHomeStation`: the default
  spawner seats a peer in its side's generated home sector; driving into the
  generated home station's dock radius docks it — the generated HiLo base is
  genuinely dockable.
- `AllRingTypes_BootHeadless_AreConfigSelectable`: one server, eight games
  (seven topologies + a plain `Map`-file game) boot through the real
  `ToCoreParams → Generate → Assemble` path. Captured boot log:
  ```
  [boot] game 'SingleRing'  generating map: topology=SingleRing,  4 sectors / 2 stations
  [boot] game 'DoubleRing'  generating map: topology=DoubleRing,  4 sectors / 2 stations
  [boot] game 'BigRing'     generating map: topology=BigRing,     6 sectors / 12 warps / 2 stations / 116 asteroids
  [boot] game 'HiLo'        generating map: topology=HiLo,        9 sectors / 24 warps / 2 stations / 170 asteroids
  [boot] game 'HiHigher'    generating map: topology=HiHigher,   13 sectors / 40 warps / 2 stations / 242 asteroids
  [boot] game 'InsideOut'   generating map: topology=InsideOut,  13 sectors / 40 warps / 2 stations / 242 asteroids
  [boot] game 'LargeSplit'  generating map: topology=LargeSplit, 15 sectors / 40 warps / 4 stations / 286 asteroids
  [boot-regression] game 'PlainFileLoad' loaded file map: 13 sectors
  ```
  (LargeSplit's 4 stations = its 4 home sectors; DoubleRing shows 12 warps = its
  6 K4 edges × 2 — all consistent with the traced graphs. The plain
  `Map`-file game loads `XXV_simple.igc` at 13 sectors via the untouched
  file-load branch.)

### Regression
- `SingleRing` (P397): all 7 P397 foundation/live tests pass unchanged.
- Real map-file configs: none use `MapGen`; the file-load branch is untouched
  (re-proved in the boot test above and in the P397 report).
- Whole-solution build: **0 warnings / 0 errors.**

### Full suite (run 2×)
`Allegiance.Core.Tests`: 3421 tests. Run 1: 6 failed / run 2: 4 failed — all in
`*Live*` wire/timing test classes (money, ripcord, turret gunner, conquest
territory, mining depletion, game-over wire, warp bomb), a rotating set that
passes on isolated re-run. These are the pre-existing documented live-wire
timing flakes; none touch map generation, and the set varies run-to-run
(6 → 4 with different members). All 35 map-gen tests (25 structural + 7 P397 +
3 new live/boot) pass consistently in every run.

---

## 5. Scope boundaries

- **Implemented:** `DoubleRing`, `BigRing`, `HiLo`, `HiHigher`, `InsideOut`,
  `LargeSplit`; the real per-topology dispatch in `MissionMapGenerator`;
  config-selectability of all 7 types; structural + live + boot verification.
- **Deferred (explicit follow-ups):** the other topology families
  (Star/PinWheel/DiamondRing/SnowFlake, Grid/EastWest/SplitBase, Brawl);
  screen-position cosmetics; the `iRandomEncounters` warp-pruning;
  treasure creation (unchanged from P397's deferral).
- Untouched: P368–P397, all other subsystems.

Files changed:
- `src/Allegiance.Core/MapGen/MapGenType.cs` (enum + docs)
- `src/Allegiance.Core/MapGen/MissionMapGenerator.cs` (dispatch + 4 link methods)
- `src/Allegiance.Core/MapGen/MapGenParams.cs` (doc only)
- `src/Allegiance.Server/ServerConfig.cs` (doc only)
- `src/Allegiance.Server/Server.Lifecycle.cs` (error-message text only)
- `tests/Allegiance.Core.Tests/MapGenRingFamilyTests.cs` (new)
- `tests/Allegiance.Core.Tests/MapGenRingLiveProofTests.cs` (new)

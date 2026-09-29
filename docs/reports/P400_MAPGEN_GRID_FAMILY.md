# P400 — Procedural mission-map generation: grid/geographic-family topologies (task 4 of ~5)

Follows P397 (foundation + `SingleRing`), P398 (the ring family: `DoubleRing`,
`BigRing`, `HiLo`, `HiHigher`, `LargeSplit`, `InsideOut`) and P399 (the radial
family: `Star`, `PinWheel`, `DiamondRing`, `SnowFlake`). This task lands the
final multi-cluster family — `Grid`, `EastWest`, `SplitBase` — bringing the
port to **14 of 15 native topologies**. Only `Brawl` remains (a separate,
final task: it is structurally the odd one out, a single shared sector rather
than a multi-cluster layout). It follows the exact dispatch shape P398/P399
established (each topology runs its own `GenerateLayout` + `LinkClusters`,
then the shared `PlaceWarps`/`PopulateClusters`), reuses the shared
3-arg `LinkClusters(short, short, int)` warp-pair builder, and layers
EastWest's real `PopulateCluster` override onto the shared method the same way
P399 layered PinWheel's.

---

## 1. Enum values (native `c_mm*` ids from `igc.h:977-992`)

Added to `MapGenType` with the native values so a config naming the native
value maps onto the same topology:

```
SplitBase = 5   (c_mmSplitBase, igc.h:982)
Grid      = 12  (c_mmGrid,      igc.h:989)
EastWest  = 13  (c_mmEastWest,  igc.h:990)
```

Because `Server.Lifecycle.ToCoreParams` resolves `MapGenConfig.Type` via
`Enum.TryParse<MapGenType>`, all three become config-selectable with no new
wiring — only the "expected one of …" error message was refreshed to list all
14 supported names.

---

## 2. Per-topology traced algorithms (native citations)

All against `~/Development/Allegiance/src/Igc/mapmakerigc.cpp` (and `.h` for
class shape). Screen-position overrides
(`Generate*ClusterScreenPosition`) remain out of scope (same P397-P399
simplification — no map-editor screen), and the `iRandomEncounters` tail links
are skipped for every topology (not modelled; same basis as P397/P398).

### Grid — `mapmakerigc.h:542-562`, `mapmakerigc.cpp:2272-2414`

`GenerateLayout` (`2272-2288`): team-clusters-per-team from `iMapSize`
(0 → 1, else 2), neutral-per-team = 4, no central cluster, then
`GenerateRingClusters`. `LinkClusters` (`2364-2414`): when a team has >1
sector the two team sectors are split-base linked; then per team `i` —
`sID0..sID3` are its 4 neutral sectors — the four neutrals form a closed
diamond (`0-1, 1-2, 2-3, 3-0`), the block cross-links to the next team's
block both at the neutral level (`sID2-sIDn0+1`, `sID3-sIDn0`) and the sector
level (`sID2-sIDn`, `sID3-sIDn+t-1`), and the team's home(s) link into the
diamond (`sID0` to the last team sector, `sID1` to the first). The net effect
at 2 teams / iMapSize 0 (the tested case) is a fully **4-regular, 10-sector
graph** with homes `{0, 5}` and 20 edges.

### EastWest — `mapmakerigc.h:564-584`, `mapmakerigc.cpp:2416-2553`

`GenerateLayout` (`2436-2450`): team-clusters-per-team = 2, neutral-per-team =
11, no central cluster — 13-sector blocks per team (26 total for its mandatory
2 teams). `LinkClusters` (`2489-2539`): each team's two home sectors anchor the
11 neutrals — home 0 links to neutrals 0..3, home 1 to neutrals 4..7; the
neutrals form two chains (`0-1-3-2-0` and `4-5-7-6-4`) joined by a bridge
(`4-1`); neutrals 8/9/10 connect inward, and the far ends cross the midline to
the other team's far end (`10-25, 11-24, 12-23` — the second team's block is
the mirror image). **`PopulateCluster` override** (`2541-2553`) — the one
genuine behavioural deviation; see §3.

### SplitBase — `mapmakerigc.h:586-602`, `mapmakerigc.cpp:2569-2625`

`GenerateLayout` (`2569-2580`): team-clusters-per-team = 2, neutral-per-team =
2, no central cluster — 4 sectors per team. `LinkClusters` (`2606-2625`): per
team `i` the two neutral sectors hang off the split home and chain toward the
next team's home (`sID+1-sID+2`, `sID+2-sID+3`, `sID+3-(sID+4)%tot`,
`sID+2-(tot+sID-1)%tot`), with extra cross links for >2 (>3) teams. At 2 teams:
8 sectors (homes `{0,1,4,5}`), 8 edges.

---

## 3. `IsValid` checks — what is real and what is vestigial

Following the P399 lesson (a class's own `IsValid` is not necessarily a real,
currently-enforced constraint), each of the three was traced against the
actual current source before porting:

- **EastWest — REAL.** `CmapMakerEastWestIGC::IsValid`
  (`mapmakerigc.cpp:2416-2427`) returns `"East West maps must have exactly two
  teams…"` when `pmp->nTeams != 2`. The secondary players-per-team check is
  commented out (dated March 2006), but the `nTeams == 2` gate is live. Ported
  in `ValidateTopology`: an EastWest config with anything but 2 teams throws
  `InvalidDataException` before any cluster is generated.
- **SplitBase — VESTIGIAL NULL STUB.** `CmapMakerSplitBaseIGC::IsValid`
  (`mapmakerigc.cpp:2555-2563`) has its only real check
  (`nTeams * nMinPlayersPerTeam < 30`) **commented out** ("yp your_persona
  removed march 24 2006") and returns NULL. Like PinWheel, it adds **no**
  constraint beyond the base — ported faithfully, i.e. not enforced, and this
  report states it plainly.
- **Grid — INLINE NULL.** `CmapMakerGridIGC::IsValid` returns NULL inline
  (`mapmakerigc.h:545-548`). No constraint.

Verified by tests: `EastWest_RequiresExactlyTwoTeams` (3 rejected, 2 accepted
→ 26 sectors) and `SplitBase_HasNoExtraValidityConstraint` (3 teams accepted
→ 12 sectors, proving no hidden team-count gate).

---

## 4. EastWest's `PopulateCluster` override (the new behavioural piece)

Native `CmapMakerEastWestIGC::PopulateCluster` (`mapmakerigc.cpp:2541-2553`)
does not multiply the base counts uniformly the way PinWheel's does — it hands
two **per-sector tables** to the free function
`::PopulateCluster(pmission, pmp, pcluster, amountHe3, true, true,
cMineable[localID], cSpecial[localID])` as the mineable and special
*multipliers*:

```
cMineable[13] = {2, 0, 2, 2, 2, 2, 0, 0, 0, 0, 1, 1, 1}   // cpp:2547
cSpecial[13]  = {0, 0, 0, 0, 0, 0, 2, 2, 2, 2, 0, 0, 0}   // cpp:2548
```

where `localID = objectID % clustersPerTeam` (13). Because every table value is
`>= 0`, the free function treats each as a multiplier
(`common.cpp:3024-3030`, `3042-3048`): `0` = no rocks of that kind, `1` = the
base count, `2` = double. The third (generic/buildable) multiplier is **not
passed**, so it falls back to the free function's default `nAsteroidsMultiplier
= 1` (`igc.h:4657`) — EastWest's buildable count is the ordinary base count.

Net per local-id (with default params home 10/2/10, neutral 8/2/8):

| local | kind   | mine | special | generic | total | He3 |
|-------|--------|------|---------|---------|-------|-----|
| 0     | home   | 2·10 | 0       | 10      | 30    | 20  |
| 1     | home   | 0    | 0       | 10      | 10    | 0   |
| 2..5  | neutral| 2·8  | 0       | 8       | 24    | 16  |
| 6..9  | neutral| 0    | 2·2     | 8       | 12    | 0   |
| 10..12| neutral| 1·8  | 0       | 8       | 16    | 8   |

Notable consequences, all real native behaviour: EastWest's **second home
sector (local 1) and neutral locals 6-9 carry no He3 at all**, while its first
home holds **double** the normal home He3. The port mirrors how P399 layered
PinWheel's multiplier onto the shared `PopulateCluster`: the shared method now
computes `(multMine, multSpecial, multGeneric)`, defaulting to `(1,1,1)`;
PinWheel sets all three to `k`; EastWest sets `multMine = cMineable[local]`,
`multSpecial = cSpecial[local]`, leaving generic at 1.

**Deliberate simplification, stated plainly:** the free function's
negative-multiplier branch (`common.cpp:3031-3032`/`3049-3050`, an absolute
count when the value is negative) is unreachable through any topology we port
(all multiplier values are non-negative), so it is not represented — the port
has no occasion to exercise it.

---

## 5. Verification (real proof)

### Structural — `MapGenGridFamilyTests` (15: 3 cases × 4 theories + 3 facts)
All expected sector counts / home sets / adjacency graphs were derived by
tracing native and **re-deriving each graph with an independent simulation**
(a throwaway Python script re-implementing the native `LinkClusters` bodies
from the C++ and recording the edges), not from the port reading its own
logic.

| Topology  | Teams | iMapSize | Sectors | Edges | Home sectors |
|-----------|-------|----------|---------|-------|--------------|
| Grid      | 2     | 0        | 10      | 20    | 0, 5         |
| EastWest  | 2     | 0        | 26      | 49    | 0, 1, 13, 14 |
| SplitBase | 2     | 0        | 8       | 8     | 0, 1, 4, 5   |

Per-case theories: exact sector count; exact home set; the **exact traced
warp-adjacency graph** + edge count (= Σdegree/2); home sectors carry exactly
one station and neutrals none; minable He3 rocks present (per-sector for Grid/
SplitBase, map-wide for EastWest — whose override legitimately leaves some
sectors He3-less). Facts: `EastWest_RequiresExactlyTwoTeams`,
`SplitBase_HasNoExtraValidityConstraint`, and the dedicated override proof
below.

**Must-fail-if-removed (verified red, then restored):**
1. Suppressing `LinkClustersGrid()` made exactly Grid's adjacency case fail
   (0 warps per sector) while EastWest/SplitBase stayed green.
2. Disabling EastWest's `PopulateCluster` branch made
   `EastWest_PopulateCluster_OverrideDrivesPerSectorCounts` fail (every sector
   collapsed toward the uniform base 22/18 totals).

**Override proof:** `EastWest_PopulateCluster_OverrideDrivesPerSectorCounts`
asserts the exact per-local-id counts above (e.g. sector 0 = 30 total / 20 He3
vs sector 1 = 10 total / 0 He3; neutral local 2 = 24 / 16 He3 vs local 6 = 12
total / 0 He3 / 4 special; symmetry in the mirrored second block). This can
only be green if the cMineable/cSpecial tables genuinely drive EastWest's
sectors.

**IsValid proofs:** `EastWest_RequiresExactlyTwoTeams` (reject 3, accept 2)
and `SplitBase_HasNoExtraValidityConstraint` (vestigial → accepts 3).

### Live proof — `MapGenGridLiveProofTests` (3, on `Grid`)
- `GeneratedGrid_PeerFliesThroughWarp_ToTracedNeighbour`: a real `FltBot` over
  the real TCP transport joins a server booted with `MapGen = Grid` (no map
  file), is spawned inside a generated warp's collision volume, transits
  (`ServerHost.HitWarpEvent`), and arrives in a genuine Grid neighbour per the
  traced 4-regular adjacency.
- `GeneratedGrid_DefaultSpawnedPeer_DocksAtGeneratedHomeStation`: the default
  spawner seats a peer in its side's generated home sector; driving into the
  generated home station's dock radius docks it — the generated Grid base is
  genuinely dockable.
- `AllGridTypes_BootHeadless_AreConfigSelectable`: one server, four games (the
  three new topologies + a plain `Map`-file regression game) boot through the
  real `ToCoreParams → Generate → Assemble` path. Captured boot log (all
  counts reconcile with the traced graphs and the populate tables — e.g.
  EastWest's 98 warps = 49 edges × 2, its 4 stations = its 4 home sectors, and
  its 464 asteroids = 232 per mirrored 13-sector block):
  ```
  [boot] game 'Grid'      generating map: topology=Grid,      10 sectors / 40 warps / 2 stations / 188 asteroids
  [boot] game 'EastWest'  generating map: topology=EastWest,  26 sectors / 98 warps / 4 stations / 464 asteroids
  [boot] game 'SplitBase' generating map: topology=SplitBase,  8 sectors / 16 warps / 4 stations / 160 asteroids
  [boot-regression] game 'PlainFileLoad' loaded file map: 13 sectors
  ```
  (Grid's 188 = 2 home sectors ×22 + 8 neutrals ×18; SplitBase's 160 = 4 homes
  ×22 + 4 neutrals ×18. The plain `Map`-file game loads `XXV_simple.igc` at 13
  sectors via the untouched file-load branch.)

### Regression
- All **79 map-gen tests** across P397 (7), P398 (28), P399 (26) and P400 (18)
  pass together.
- Real map-file configs: none use `MapGen`; the file-load branch is untouched
  (re-proved in the boot test above).
- Whole-solution build: **0 warnings / 0 errors.**

### Full suite (run 2×)
`Allegiance.Core.Tests` — see the run notes in §6; only the pre-existing
documented live-wire timing flakes expected (rotating set, green in isolation),
none touch map generation.

---

## 6. Scope boundaries

- **Implemented:** `Grid`, `EastWest`, `SplitBase`; the real
  `GenerateLayout`/`LinkClusters` for each; EastWest's real `PopulateCluster`
  override; EastWest's real `IsValid` (and the confirmed-vestigial nature of
  SplitBase's); config-selectability of all three; structural + live + boot +
  must-fail verification.
- **Deferred (explicit follow-ups):** `Brawl` (the final topology, a single
  shared sector rather than a multi-cluster layout); screen-position
  cosmetics; the `iRandomEncounters` warp-pruning; treasure creation (unchanged
  from P397's deferral).
- Untouched: P368–P399, all other subsystems.

Files changed:
- `src/Allegiance.Core/MapGen/MapGenType.cs` (enum: Grid/EastWest/SplitBase + docs)
- `src/Allegiance.Core/MapGen/MissionMapGenerator.cs` (ValidateTopology + 3 LinkClusters* + EastWest PopulateCluster override)
- `src/Allegiance.Server/Server.Lifecycle.cs` (error-message text only)
- `tests/Allegiance.Core.Tests/MapGenGridFamilyTests.cs` (new)
- `tests/Allegiance.Core.Tests/MapGenGridLiveProofTests.cs` (new)

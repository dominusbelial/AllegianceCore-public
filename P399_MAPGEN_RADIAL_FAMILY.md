# P399 — Procedural mission-map generation: radial/spoke-family topologies (task 3 of ~5)

Follows P397 (foundation + `SingleRing`) and P398 (the ring family:
`DoubleRing`, `BigRing`, `HiLo`, `HiHigher`, `LargeSplit`, `InsideOut`). This
task lands the 4 radial/spoke native map types — `Star`, `PinWheel`,
`DiamondRing`, `SnowFlake` — completing 11 of the 15 native topologies. It
follows the exact dispatch shape P398 established: each topology runs its own
`GenerateLayout` (cluster-count setup + `GenerateRingClusters`) followed by
its own `LinkClusters`, then the shared `PlaceWarps`/`PopulateClusters`.

---

## 1. Dispatch shape (unchanged from P398)

No new dispatch mechanism. `Generate` still switches over `MapGenType`;
`GenerateLayout()` is the per-topology switch that sets counts, calls the
shared `GenerateRingClusters()`, then calls that topology's `LinkClusters*`
method — the same shape P398 used for the ring family. The one genuinely new
piece is **PinWheel's `PopulateCluster` override** (§4), which the shared
`PopulateCluster` method now honours by applying PinWheel's multiplier.

### Enum values (added to `MapGenType`, native `c_mm*` ids from `igc.h:977-991`)

```
PinWheel    = 2   (c_mmPinWheel,    igc.h:979)
DiamondRing = 3   (c_mmDiamondRing, igc.h:980)
SnowFlake   = 4   (c_mmSnowFlake,   igc.h:981)
Star        = 10  (c_mmStar,        igc.h:987)
```

Because `Server.Lifecycle.ToCoreParams` resolves `MapGenConfig.Type` via
`Enum.TryParse<MapGenType>`, all four become config-selectable with no new
wiring — only the help text / error message were refreshed to list the 11
supported names (with `DiamondRing` requiring `Teams >= 3`).

---

## 2. Per-topology traced algorithms (native citations)

All against `~/Development/Allegiance/src/Igc/mapmakerigc.cpp` (and `.h` for
class shape). Screen-position overrides remain out of scope (same P397/P398
simplification — no map-editor screen).

### Star — `mapmakerigc.h:357`, `mapmakerigc.cpp:1549-1683`

`GenerateLayout` (`1555-1569`): team-per-team from `iMapSize` (0 → 1 else 2),
neutral-per-team = 5, no central cluster, then `GenerateRingClusters`.
`LinkClusters` (`1631-1683`): a spoked shape — each team's home at the hub with
5 neutral sectors radiating outward. Per team the two inner neutrals link to
the home, the neutral chain links inward-to-outward, the far-spoke neutrals
cross-link to the *previous* team's spoke, and an opposite-spoke link connects
to the halfway-opposite team (`j = (i + Teams/2) % Teams`; even teams link one
edge, odd teams two). **Faithful quirk:** for even team counts the opposite-
spoke link is issued twice (once per team iteration), creating a *duplicate
bidirectional warp pair* between the two far-spoke neutrals — native does not
dedupe, and the port reproduces this (observable as Star's 2-team map having 40
warps = 19 distinct edges + 1 duplicated edge). Its `IsValid` returns NULL
(inline, `mapmakerigc.h:360`) — no extra constraint.

### PinWheel — `mapmakerigc.h:439`, `mapmakerigc.cpp:1803-1917`

`GenerateLayout` (`1821-1838`): team-per-team pinned to **1**; if `iMapSize ==
0` a single central (**other**) sector, else one neutral per team in the
centre (`NeutralClustersPerTeam = 1`). `LinkClusters` (`1859-1899`): with a
multi-sector centre, each team cluster links to its following centre neutral
and the centre neutrals chain into a ring (closed when >2 teams, then
cross-linked); with a single centre sector every team cluster links straight
to the centre (last cluster id). **`PopulateCluster` override** (`1901-1917`)
is the one genuine behavioural deviation from every previously-ported topology
— see §4.

### DiamondRing — `mapmakerigc.h:465`, `mapmakerigc.cpp:1923-2051`

`GenerateLayout` (`1940-1953`): team-per-team from `iMapSize`, neutral-per-team
= 2 (one inner ring, one outer). `LinkClusters` (`2012-2051`): each team's
sector(s) link to both of its two neutral sectors; the neutral sectors chain
into the two rings; for 4 teams the centre sectors cross-link
(`CrossLinkClusters`). **Own `IsValid`** (`1930-1937`): requires `nTeams > 2`
— enforced (see §3).

### SnowFlake — `mapmakerigc.h:493`, `mapmakerigc.cpp:2054-2236`

`GenerateLayout` (`2061-2075`): team-per-team from `iMapSize`, neutral-per-team
= 3. `LinkClusters` (`2138-2236`): the session's earlier high-level characterisation
("triangle-per-team neutral clusters cross-connected into a ring") was checked
against the real code and is accurate: 3 neutral sectors per team form a
triangle (two sides + base links), each team's home sector(s) attach to the
triangle's first two neutrals, and the triangles chain into a (double) ring.
For <4 teams the centre cross-link is a no-op, so the third neutral of each
team is additionally ring-connected explicitly (making a full ring even at 2
teams). Its `IsValid` returns NULL (inline, `mapmakerigc.h:496`) — no extra
constraint.

### Deliberate simplifications (stated, not silent)
- Screen-position / map-editor cosmetics: out of scope (same as P397/P398).
- Treasure generation: not touched — still deferred per P397 §3.
- Star's duplicate-warp quirk is *kept* (it is real native behaviour, not a
  cosmetic omission).

---

## 3. `IsValid` checks — confirming what is real and what is vestigial

The brief asserted PinWheel *and* DiamondRing each have an extra `IsValid`
check. Tracing the real code (the "confirm" in the brief):

- **DiamondRing — REAL.** `CmapMakerDiamondRingIGC::IsValid`
  (`mapmakerigc.cpp:1930-1937`) returns `"Diamond ring maps must have more than
  2 teams..."` when `pmp->nTeams < 3`. Ported as `ValidateTopology`: a
  `DiamondRing` config with 2 teams throws `InvalidDataException` before any
  cluster is generated (mirroring native's `assert(NULL ==
  ImapMakerIGC::IsValid(pmp))` at `mapmakerigc.cpp:346`).
- **PinWheel — VESTIGIAL NULL STUB.** `CmapMakerPinWheelIGC::IsValid`
  (`mapmakerigc.cpp:1810-1818`) has its only real check (players-per-team > 200)
  **commented out** ("yp your_persona removed march 24 2006") and returns NULL.
  So PinWheel adds **no** constraint beyond the base. It is ported faithfully —
  i.e. not enforced — and the report states this plainly rather than fabricating
  a check the native doesn't have.
- Star / SnowFlake `IsValid` return NULL inline (`.h:360, 496`) — no constraints.

Verified by tests: `DiamondRing_RequiresMoreThanTwoTeams` (2-team rejected, 3-team
generates 9 sectors) and `PinWheel_HasNoExtraValidityConstraint` (2-team PinWheel
generates normally).

---

## 4. PinWheel's `PopulateCluster` override (the one new behavioural piece)

Native `CmapMakerPinWheelIGC::PopulateCluster` (`mapmakerigc.cpp:1901-1917`)
computes `k = 1` if the cluster is a home sector **or** the centre is
multi-sector (`NeutralClustersPerTeam != 0`), else `k = 2`, and passes
`(k, k, k)` as the three asteroid multipliers to the shared free function
`::PopulateCluster` (`common.cpp:2963-3109`), which multiplies the mineable /
special / generic counts for that cluster by `k`.

Net effect: in PinWheel's **single-centre** layout (iMapSize 0) the one
non-home central neutral sector gets **double** the asteroid counts of every
other sector; in the multi-centre layout (iMapSize != 0) every cluster has a
home or a multi-sector centre, so `k = 1` everywhere (identical to base).

The port's shared `PopulateCluster` now applies this: `mult = (home ||
NeutralClustersPerTeam != 0) ? 1 : 2` when `_type == PinWheel`, else 1 (the
base behaviour every other topology uses). With default counts (home 10/2/10,
neutral 8/2/8) a 2-team single-centre PinWheel therefore has the centre holding
`2·(8+2+8) = 36` asteroids and 16 He3 rocks, versus 22 / 10 for each home.

---

## 5. Verification (real proof)

### Structural — `MapGenRadialFamilyTests` (23: 5 cases × 4 theories + 3 facts)
All expected sector counts / home sets / adjacency graphs were derived by
tracing native and **re-deriving each graph with an independent simulation**
(a throwaway script re-implementing the native `LinkClusters` bodies from the
C++ and recording the edges), not from the port reading its own logic.

| Topology | Teams | iMapSize | Sectors | Edges | Home sectors |
|---|---|---|---|---|---|
| Star | 2 | 0 | 12 | 19 | 0, 6 |
| PinWheel (single centre) | 2 | 0 | 3 | 2 | 0, 1 |
| PinWheel (multi centre) | 2 | 1 | 4 | 3 | 0, 2 |
| DiamondRing | 3 | 0 | 9 | 12 | 0, 3, 6 |
| SnowFlake | 2 | 0 | 8 | 13 | 0, 4 |

Per-case theories: exact sector count; exact home set; the **exact traced
warp-adjacency graph** + edge count (= Σdegree/2); home sectors carry a station
and neutrals none; every sector has ≥1 genuinely minable He3 rock.

**Must-fail-if-removed (verified red):** suppressing the `LinkClustersStar()`
call made exactly Star's adjacency test fail with the unwired graph (observed,
then restored). Suppressing PinWheel's population multiplier made
`PinWheel_SingleCentreMode_DoublesCentreAsteroids` fail (centre dropped from 36
to 18; observed, then restored).

**Override proof:** `PinWheel_SingleCentreMode_DoublesCentreAsteroids` asserts
the centre holds exactly 36 asteroids / 16 He3 vs each home's 22 / 10 — this
can only be green if the k=2 multiplier genuinely reaches the centre.

**IsValid proofs:** `DiamondRing_RequiresMoreThanTwoTeams` (reject 2, accept 3)
and `PinWheel_HasNoExtraValidityConstraint` (vestigial → accepts 2).

### Live proof — `MapGenRadialLiveProofTests` (3, on `SnowFlake`)
- `GeneratedSnowFlake_PeerFliesThroughWarp_ToTracedNeighbour`: a real `FltBot`
  over the real TCP transport joins a server booted with `MapGen = SnowFlake`
  (no map file), is spawned inside a generated warp's collision volume,
  transits (`ServerHost.HitWarpEvent`), and arrives in a genuine SnowFlake
  neighbour per the traced adjacency.
- `GeneratedSnowFlake_DefaultSpawnedPeer_DocksAtGeneratedHomeStation`: the
  default spawner seats a peer in its side's generated home sector; driving
  into the generated home station's dock radius docks it — the generated
  SnowFlake base is genuinely dockable.
- `AllRadialTypes_BootHeadless_AreConfigSelectable`: one server, six games
  (four new topologies + PinWheel's multi-centre layout + a plain `Map`-file
  regression game) boot through the real `ToCoreParams → Generate → Assemble`
  path. Captured boot log (all counts reconcile with the traced graphs — e.g.
  Star's 40 warps = 19 distinct edges + the duplicated edge ×2):
  ```
  [boot] game 'Star'         generating map: topology=Star,        12 sectors / 40 warps / 2 stations / 224 asteroids
  [boot] game 'PinWheel'     generating map: topology=PinWheel,     3 sectors /  4 warps / 2 stations /  80 asteroids
  [boot] game 'PinWheelMulti'generating map: topology=PinWheel,     4 sectors /  6 warps / 2 stations /  80 asteroids
  [boot] game 'DiamondRing'  generating map: topology=DiamondRing,  9 sectors / 24 warps / 3 stations / 174 asteroids
  [boot] game 'SnowFlake'    generating map: topology=SnowFlake,    8 sectors / 26 warps / 2 stations / 152 asteroids
  [boot-regression] game 'PlainFileLoad' loaded file map: 13 sectors
  ```
  (DiamondRing's 3 stations = its 3 home sectors. The plain `Map`-file game
  loads `XXV_simple.igc` at 13 sectors via the untouched file-load branch.)

### Regression
- All 61 map-gen tests across P397 (7), P398 (28), P399 (26) pass together.
- Real map-file configs: none use `MapGen`; the file-load branch is untouched
  (re-proved in the boot test above).
- Whole-solution build: **0 warnings / 0 errors.**

### Full suite (run 2×)
`Allegiance.Core.Tests` — see below; only the pre-existing documented
live-wire timing flakes expected (rotating set, green in isolation), none
touch map generation.

---

## 6. Scope boundaries

- **Implemented:** `Star`, `PinWheel`, `DiamondRing`, `SnowFlake`; the real
  `GenerateLayout`/`LinkClusters` for each; PinWheel's real `PopulateCluster`
  override; DiamondRing's real `IsValid` (and the confirmed-vestigial nature of
  PinWheel's); config-selectability of all four; structural + live + boot +
  must-fail verification.
- **Deferred (explicit follow-ups):** the remaining topology families
  (Grid/EastWest/SplitBase, Brawl); screen-position cosmetics; the
  `iRandomEncounters` warp-pruning; treasure creation (unchanged from P397's
  deferral).
- Untouched: P368–P398, all other subsystems.

Files changed:
- `src/Allegiance.Core/MapGen/MapGenType.cs` (enum: Star/PinWheel/DiamondRing/SnowFlake + docs)
- `src/Allegiance.Core/MapGen/MissionMapGenerator.cs` (ValidateTopology + 4 LinkClusters* + PinWheel PopulateCluster multiplier)
- `src/Allegiance.Server/ServerConfig.cs` (doc only)
- `src/Allegiance.Server/Server.Lifecycle.cs` (error-message text only)
- `tests/Allegiance.Core.Tests/MapGenRadialFamilyTests.cs` (new)
- `tests/Allegiance.Core.Tests/MapGenRadialLiveProofTests.cs` (new)

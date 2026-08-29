# AllegianceCore

C# port of the Allegiance simulation data layer. Server-side only — no
rendering, no networking, no COM.

Scope decision: **headless server + bots first, no render client.**

## Build & test

```bash
export DOTNET_ROOT="$HOME/.dotnet"     # NOT /snap/bin/dotnet, which is a broken stub
export PATH="$DOTNET_ROOT:$PATH"
dotnet build
dotnet test
```

SDK 10.0.400. `dotnet --version` must print `10.0.400`; if you see
"Looks like you don't yet have a .NET SDK", your `PATH` is wrong.

```bash
dotnet run --project tools/igcdump -- /home/domingo/Development/Artwork/PCore001.igc
dotnet run --project tools/cvhdump -- /home/domingo/Development/Artwork/fig01.cvh
dotnet run --project tools/missiondump -- /home/domingo/Development/Artwork/PCore001.igc \
                                         /home/domingo/Development/Artwork/m2xwheel.igc
```

## Status

**Container frame: done and verified. Payload decoding: every shipped object
type is done — all fifteen static-core types (fixed- and variable-length) plus
the four map-file objects. Type universe: `StaticCore` + `MissionMap` + a
non-throwing `CrossRefValidator` connect the decoders into a loadable,
ID-memoized model (checklist item 4 of the boot path).**

All eleven fixed-size static payload structs, the four variable-length static
types (part subtypes, treasure sets), and the four fixed-size map-file objects
are decoded and corpus-verified. The standalone `.cvh` collision-hull format is
now decoded too — see **`.cvh collision hulls`** below.

| | | |
|---|---|---|
| corpus | **210 unique** `.igc` files (217 on disk; 7 are byte-identical cross-tree duplicates) |
| acceptance | **210/210 consumed exactly** — no trailing bytes, no overruns |
| wrappers | 99 static cores (`version`+`datasize`), 111 maps (`datasize`) |
| type universe | 99 `StaticCore` + 111 `MissionMap` load; 1370 tests passing |
| tests | **1370** passing (containers + payloads + `StaticCore`/`MissionMap`/`CrossRefValidator` + `.cvh`) |

### The acceptance property

There is no Windows here to diff against the original writer, so the oracle is
**exact byte consumption**: for every file the record stream must land precisely
on end-of-body. A wrong field width or offset anywhere desynchronises everything
after it, so this single property catches nearly every layout error without a
reference implementation.

It was first established by `../allegiance-tools/scan_igc_corpus.py`; this
library reproduces it.

## Implemented

- `IgcObjectType` — the 24 `ObjectType` values (`src/Igc/igc.h:114-160`)
- `IgcRecord` — the 6-byte header: `int16 type`, `int32 size` (payload excludes header)
- `IgcReader` — wrapper detection + record-stream walk
- `IgcFile` — records, wrapper form, version, exactness
- **`Wire/`** — the FEDMSG wire framing + logon handshake: `cbmsg`/`fmid`
  header, variable items, `FedPacket` (+CRC), and `LogonRequest`/`LogonAck`.
  See **`FEDMSG.md`** and **"The FEDMSG wire"** below.

**Payload decoders** (`src/Allegiance.Core/Data/`), each a `readonly record
struct` with a `Decode(ReadOnlySpan<byte>)` that throws on a wrong record size:

| type | struct | size | corpus count |
|---|---|---|---|
| 22 `OT_projectileType` | `DataProjectileTypeIGC` | 88 | 9369 |
| 23 `OT_missileType` | `DataMissileTypeIGC` | 524 | 5370 |
| 24 `OT_mineType` | `DataMineTypeIGC` | 480 | 782 |
| 25 `OT_probeType` | `DataProbeTypeIGC` | 492 | 4760 |
| 26 `OT_chaffType` | `DataChaffTypeIGC` | 468 | 619 |
| 27 `OT_civilization` | `DataCivilizationIGC` | 268 | 952 |
| 29 `OT_hullType` | `DataHullTypeIGC` | 540 + 30·maxWeapons | 38927 |
| 31 `OT_stationType` | `DataStationTypeIGC` | 524 | 12659 |
| 32 `OT_development` | `DataDevelopmentIGC` | 468 | 28500 |
| 33 `OT_droneType` | `DataDroneTypeIGC` | 384 | 6503 |
| 34 `OT_constants` | `DataConstantsIGC` | 1760 | 99 (one per core) |
| 1 `OT_station` | `DataStationIGC` | 88 | 289 |
| 13 `OT_cluster` | `DataClusterIGC` | 92 | 1506 |
| 7 `OT_warp` | `DataWarpIGC` | 108 | 5212 |
| 5 `OT_asteroid` | `DataAsteroidIGC` | 144 | 973 |
| 30 `OT_partType` | `DataPartTypeIGC` + `DataLauncherTypeIGC` | 24/400/412/416/424 | 22439 |
| 28 `OT_treasureSet` | `DataTreasureSetIGC` | 32 + 4·n | 99 |

The five `OT_expendableType`-derived types (missile/mine/probe/chaff, plus the
shared base read into each) embed a `LauncherDef`; their full flattened
layouts were verified per-record. `DataConstantsIGC` is a flat 40 + 20×20
matrix of floats (`c_fcidMax`/`c_dmgidMax`/`c_defidMax`, all 20 or 40).

`OT_partType` dispatches by record size to `ClauncherTypeIGC` (24 B) or
`CpartTypeIGC` (`src/Igc/missionigc.cpp:3344-3355`); the base part then carries a
subtype tail chosen by `equipmentType` (`DataPartTypeIGC` base is 396 bytes,
but no bare 396-byte record exists — every record carries a subtype tail).
Seven shapes occur: 24 (launcher), 400 (`DataPackTypeIGC`), 412
(`DataShieldTypeIGC`), 416 (`DataCloakTypeIGC` *and* `DataAfterburnerTypeIGC`
share the size, distinguished by `equipmentType`), 424 (`DataWeaponTypeIGC`).
The decoder models this as a base record + a discriminated `Subtype` so callers
can switch on equipment type; `DataLauncherTypeIGC` is a separate 24-byte
struct.

`OT_treasureSet` is read with the wiki's ⭐ recommended reader: entry count is
derived from the record size (`n = (size − 32) / 4`) and the header count is
ignored. Each entry is 4 bytes: `treasureID` (ushort) + `treasureCode` (byte) +
`chance` (byte).

Detection walks each candidate wrapper and takes whichever lands exactly on
end-of-body, rather than guessing from the filename. A wrong wrapper
desynchronises the very first header, so this is unambiguous in practice.

### Corpus-measured corrections to the wiki table

The wiki's `allegiance-igc-file-format` page carries a ⭐ corrections section,
but further field-level discrepancies surfaced while decoding (none in the
wiki's own corrections list; all are reflected in the decoders):

1. **`DataProjectileTypeIGC.width` is at offset 80, not 78.** After
   `bDirectional` at 76 the float needs 4-byte alignment, so there are three pad
   bytes 77-79. Verified: `width`@80 is a clean float in all 9369 records and
   `ambientSound` is `-1` in every one.
2. **`DataCivilizationIGC` has a pad byte at 159** pushing `gasBaseAttributes`
   to 160-259; the trailing `lifepod`/`civilizationID`/`initialStationTypeID`
   sit at 260/262/264 (the wiki's draft said gas@159 and IDs at 259/261/263).
   Verified: every `lifepod` and `initialStationTypeID` decodes to a real
   hull/station-type ID present in the same core.

3. **The expendable types and the station type carry the same
   narrow-to-wide padding pattern.** For missile/mine/probe/chaff
   (all `DataExpendableTypeIGC`-derived, `igc.h:1947`), the fully flattened
   layouts have pads at 50-51 (after `DataObjectIGC`), 426-427 (inside the
   embedded `LauncherDef` after its `DataBuyableIGC` base), and a single pad
   before the derived tail — 463 for the tail-start float. Concretely:
   `loadTime`@52, `launcherDef.signature`@428, `hitPoints`@440, and
   `expendabletypeID`@446 are common to all four; `iconName` ends at 463.
   The tails then diverge: missile `acceleration`@464 (…`ambientSound`@520,
   two trailing pads to 524), mine `radius`@464 (…`damageType`@476, three
   trailing pads to 480), chaff `chaffStrength`@464, probe `scannerRange`@464
   (…`dtRipcord`@488). The station type (`igc.h:2649`) pads after its base to
   `signature`@364 and has a single pad before `constructionDroneTypeID`@458
   (after the byte `classID`@456). Every float in every one of these records
   is a clean finite value across the corpus; these offsets match
   `tools/verify_layout.py`, which the last batch left in the tree.
4. **`DataConstantsIGC` has no padding** — it is entirely floats
   (40 `floatConstants`, then `20×20 damageConstants`, `igc.h:21`), so the
   1760-byte struct is trivially 4-aligned throughout.
5. **The shipped `DataAsteroidIGC` tail differs from current `igc.h`.** The
   files put `clusterID` at **116**, `name` at **118** and `fraction` at
   **143**, with **three uninterpreted bytes at 113-115** between `iconName`
   (ending at 112) and `clusterID`. Today's `igc.h:1891` struct (and the wiki
   table) would place clusterID at 114 (one pad at 113) and name at 115 — two
   bytes earlier — so the shipped maps were written by an older revision whose
   struct carried three extra bytes here. The cross-reference check is
   decisive: `clusterID`@116 resolves to a real sector in the same file in
   **973/973** asteroid records. The 113-115 bytes are overwhelmingly
   uninitialized (MSVC debug fill `0xCC`, or zero) and are deliberately **not**
   decoded.
6. **Part types are dispatched by `equipmentType`@376, a 2-byte `short`**
   (not the 4-byte enum one might assume — `igc.h:530` types `EquipmentType`
   as `short`), followed by `partMask`@378. In the 412-byte `DataShieldTypeIGC`
   tail, `defenseType` is a single byte at **404** (`DefenseTypeID` is
   `unsigned char`, `igc.h:557`) with an intra-struct pad at 405 before the
   short `activateSound`@406 — so it reads as one clean byte, never a
   two-byte sound pair. The cross-reference checks are decisive: every weapon
   `projectileTypeID` and every launcher `expendabletypeID` resolves to a real
   projectile/expendable in the same core (**0 mismatches in 22,439 records**),
   and every non-NA part `successorPartID` names a real part.
7. **Treasure-set entries are four bytes, and the header count is wrong.**
   The shipped files carry the *older* two-byte-id form of `TreasureData`
   (`treasureID` ushort + `treasureCode` byte + `chance` byte = 4 B), not
   today's `igc.h:2100` `ObjectID`-based 6→8-byte form. The header's two-byte
   count at offset 27 reads **`256 × entries`** in **every one of the 99
   records**, generation A and B alike — it is not `n`, and it is not random
   garbage either. Despite the regularity, it is deliberately **not** depended
   on: the decoder derives `n = (size − 32) / 4` from the record size, exactly
   as the wiki's ⭐ recommended reader prescribes. Validation is decisive:
   1693/1693 part-coded `treasureID`s resolve to real parts in the same core,
   96/96 powerup codes are valid PowerupCode bitmaps, and the 96 cash
   `treasureID`s are the dollar amounts (500…10000). Because the corpus shows
   no real generation split in the on-disk bytes, the reader does not branch
   on generation; the count-field anomaly remains recorded here and unsolved
   (an honest documented workaround beats a confident wrong theory).

## .cvh collision hulls

The second, previously-unread server-side asset format. Every mission asset
that participates in collision detection needs one: `HitTest.h` is included
by `Igc/shipIGC.cpp`, `Igc/stationIGC.cpp`, `Igc/clusterIGC.cpp`,
`Igc/modelIGC.cpp` and `_Utility/CollisionQueue.cpp`. The parser lives in
`HitTest::Load` (`src/_Utility/HitTest.cpp:676-807`).

Unlike `.igc` this is a **plain-text** format (opened with `fopen(path, "r")`,
parsed with `fgets`/`sscanf`/`fscanf`).

### Grammar

Physical lines are mostly free-form tokens; the exceptions are the frames
section (one name per line) and the adjacency indices, which may span lines.

```
line 1   radius  ee.x  ee.y  ee.z  erm                    5 floats
line 2   nHulls                                          1 int
per hull:
         nVertices  nAdjacencies  center.x center.y center.z   5 tokens
per vertex:
         x  y  z  n                                         4 tokens
         then exactly n adjacency indices, whitespace-free-form across lines
then, to EOF, zero or more frames, each three lines:
         name
         position.x  position.y  position.z
         forward.x  forward.y  forward.z
```

Semantics of the three extra header values — `ee` is the ellipse equation
terms (`(x/ee.x)² + (y/ee.y)² + (z/ee.z)² ≤ 1`), and `erm` is the ellipse radius
multiplier (`MultiHullBase`, `Utility.h:706`); both feed `HitTestShape`
selection. `radius` is the original bounding-sphere radius.

**Corrections to the task sketch** (the sketch was written from a partial
read; the C++ and 564 files win):
1. **There is a frame section after the hulls.** `HitTest.cpp:774-804` keeps
   reading to EOF: each frame is a name line then a 3-float position and a
   3-float forward. These are mount points (e.g. `fig01.cvh` has `cockpt`,
   `wepemt`, `missemt`). 414 of 564 files carry them. So "no trailing content"
   means after the frames too.
2. **`radius` does not enclose the vertices.** Across the corpus the ratio
   max-vertex-distance/radius is **0.51…1.55** (so vertices may poke up to 1.55×
   outside the declared radius). Plausibility is asserted as a bounded factor,
   not enclosure.
3. Adjacency indices are read **free-form across lines** (`fscanf`), so a
   vertex's indices may be split across multiple lines; each hull's indices sum
   to its `nAdjacencies`.

The C++ loads hulls by iterating `hullID = nHulls-1 → 0` (`HitTest.cpp:701`)
but still reads the file in order, so on-disk hull order is file order; the
storage reversal is internal and is **not** reproduced. The sign flips C++
applies (`center.x`, vertex `x`, frame `position.x`, `forward.y/z`;
`HitTest.cpp:714,737,797,800-801`) are render-time transforms for a world that
yaws models 180°, and are **not** applied to the parsed values here.

### Implementation

Under `src/Allegiance.Core/Collision/`, separate from `IgcReader`:

- `Vector3` — a local 3-vector (the byte-payload structs use `float[]`; the
  collider model uses a named record struct instead).
- `CvhVertex` — `(Position, AdjacentIndices)`.
- `CvhHull` — `Center`, `VertexCount`, `AdjacencyCount`, `Vertices`.
- `CvhFrame` — `(Name, Position, Forward)`.
- `CvhFile` — `Radius`, `EllipseEquation`, `EllipseRadiusMultiplier`,
  `Hulls`, `Frames`.
- `CvhReader.Parse(string)` — text parse; **throws on any violation of the
  file's own declared counts**.

### Acceptance (the oracle is the file's own counts)

There is no Windows/render client here to diff, so correctness is checked
against the declarations each file carries — a file that parses but whose
declared counts disagree is a **failure**, not a warning. The four-oracle
theory (`CvhParseTests`) over every unique file:

1. **Whole file consumed** — the parse walks to the last line (frames included).
2. **Declared counts match** — `nHulls` hulls; each hull exactly `nVertices`
   vertices; the per-vertex adjacency counts sum to `nAdjacencies`.
3. **Every adjacency index ∈ [0, nVertices)** of its hull.
4. **Finite floats; radius > 0** and within a bounded factor of the farthest
   vertex distance (corpus-measured 0.51…1.55).

| | |
|---|---|
| corpus | **564 unique** `.cvh` files (627 on disk; 63 are byte-identical cross-tree duplicates) |
| acceptance | **564/564** whole-file consumed, counts exact, indices in range, floats finite |
| frames | 414 files carry a trailing frame section; 150 do not |
| tools | `tools/cvhdump` prints hull/vertex/adjacency/radius per file |
| tests | **1364** passing (234 `.igc` + 1128 `.cvh` + 2 fixtures) |

Verified by `CvhReader`'s own strict checks and independently re-asserted by
the test. All measurements are corpus-measured 2026-08-28.

### Not verified

- The exact geometric meaning of `radius` vs the ellipse equation: `radius` is
  `m_originalRadius`, the scale base (`m_scale = desiredRadius / originalRadius`,
  `HitTest.cpp:571`); the ellipse branch multiplies by `erm`. Whether `ee`
  describes the same ellipsoid as `ee·erm` across all files was not checked
  (it is a physics-shaper selection, not a load constraint). `(unverified)`.
- Whether two vertices may legitimately share an identical adjacency set or an
  index that appears in both directions; the loader treats all as opaque graph
  edges and so does this reader (no symmetry or convexity is assumed).
- Any file whose end is ambiguous (an odd leftover line after the last frame)
  is rejected — no such file exists in the corpus.

## Collision detection — broad & narrow phase

This is where the parsed `.cvh` hulls pay off. `src/Allegiance.Core/Collision/`
now holds a detect-and-report-only pipeline (no damage, no explosions — out of
scope), traced against the C++ in **`COLLISION.md`**. Highlights:

- **`Collider`** — the `BoundingHull`/`BoundingSphere` port: position, velocity,
  orientation, the (multi-)hull, `desiredRadius/originalRadius` scale, and the
  per-step swept AABB (`HitTest.cpp:14-110`). Support functions use the parsed
  vertices and adjacency with a pre-computed octant warm start
  (`HitTest.cpp:311-357,495-549`).
- **`BroadPhase`** — deterministic swept-AABB candidate pairs (the C++ KD tree's
  candidate set; the tree's acceleration is a structural difference, documented in
  COLLISION.md Q1 — the wiki's "sweep-and-prune with KD-tree sectors" is corrected
  there).
- **`NarrowPhase`** — faithful port of `HitTest::Collide`: the analytic sphere-sweep
  closest-approach time, then `HullCollide`/`IntervalCollide` (separating-axis
  bisection) falling back to GJK/`Johnson` for hull pairs.
- **`CollisionQueue` + `CollisionDetector`** — gather once per sector step, sort by
  collision time, report. **`Model` gains an optional `Hull`** and
  `CreateCollider()`; `Ship` supplies its velocity/orientation.

Tests: `CollisionDetectionTests` — head-on analytic timing (spheres and real
`fig01` hulls at `t = 1.80633 s`, within 0.025), parallel near-miss rejection,
ship→station (`dgsup`), multi-hull (`p1_exp`, 6 sub-hulls), determinism, and
ascending-time ordering. Full suite green: **1444 tests**.

## Type universe: `StaticCore` + `MissionMap` (+ cross-reference survey)

Checklist item 4 of the server boot path — connect the decoders to a loadable
type universe memoized by ID. Both live in `src/Allegiance.Core/Model/`:

- **`StaticCore`** (`Model/StaticCore.cs`) — built from a `VersionAndDataSize`
  core (the 99 static cores). Each static object type that carries an ID is
  exposed as an **ID-keyed `IReadOnlyDictionary`** (`HullTypes`,
  `PartTypes`/`Launchers`, `StationTypes`, `Developments`, `DroneTypes`,
  `ProjectileTypes`, `MissileTypes`, `MineTypes`, `ProbeTypes`, `ChaffTypes`,
  `Civilizations`, `TreasureSets`). `OT_partType` dispatches into two buckets by
  record size exactly as the engine does (`missionigc.cpp:3344`): non-24-byte
  records → `PartTypes` (`DataPartTypeIGC`), 24-byte launchers →
  `Launchers` (`DataLauncherTypeIGC`), both keyed by `PartId`. The one
  ID-less object, `OT_constants`, is the single `Constants` member. A duplicate
  ID within one core **throws** (type + ID named); a record type that does not
  belong in a core also throws. **No object graph is built** — records keep raw
  ID fields, lookup is through the dictionary.
- **`MissionMap`** (`Model/MissionMap.cs`) — built from a `DataSize` map (the
  111 maps). Exposes `Clusters`/`Warps`/`Stations`/`Asteroids` as
  order-preserving **lists** (the design keeps a map an ordered scene, not a
  lookup table — duplicate `ClusterId` across sectors is legal and common).
  Map objects carry IDs but the design deliberately does not key them.
- **`CrossRefValidator.Validate(core, map?)`** — non-throwing, never called from
  `Load`. Returns `CrossRefIssue(Kind, Detail)` for every dangling reference
  found; an empty list is a clean bill.

`StaticCore.Load(IgcFile)` needs the payload bytes, which `IgcFile` now carries
via a zero-copy-ish `Body` property (the container type gained it; no decoder
changed). Load/parse leaves the payload reachable without re-reading the file.

### Cross-reference survey (measured 2026-08-28)

Running `Validate` over the whole corpus (all 99 cores internally; all 111 maps
internally; and the one decoded map→core link — station type — resolved against
the union of every core):

| reference | kind | examined | dangling |
|---|---|---|---|
| hull successor chain | `hull-successor` | 14,665 non-NA | **31** |
| part successor chain | `part-successor` | 9,977 non-NA (5,567 parts + 4,410 launchers) | **3** |
| civilization lifepod / initial station | — | 952 civs (1,904 refs) | 0 |
| weapon → projectile | — | all weapons | 0 |
| launcher → expendable | — | all 4,410 launchers | 0 |
| treasure entry targets | `treasure-part/-powerup/-cash/-development` | 1,885 entries | 0 |
| map-internal (station/warp/asteroid → sector; warp destination) | — | 7,980 objects (11,686 refs) | **0** |
| map→core station type | `station-type` | 289 stations | **0** (all resolve in ≥1 core) |

The **34 dangling successors are genuine data, not a decoder bug**: 14,665 hull
successors resolve cleanly, so the keying is right — only specific edges dangle.
They cluster in recognizable patterns:

- **31 hull successors**: `Assault Ship` (hull 140) points at hull 141 across
  several production cores, and core `990048043`'s `Advanced Miner` family
  (hull ids 1/236/336/536/636) points at the mining-upgrade hulls 39-43, none of
  which is present in that core. The engine tolerates a dangling successor (it is
  just a progression pointer), so these are **left as found — not fixed, and the
  check not loosened**.
- **3 part successors**: `launcher 86 → part 238` dangles in three cores
  (versions 990050639, 990050667, 990050901). Launcher 86 is the last magazine/
  dispenser launcher in each chain. Also left as found.

(Note: the payload-decoding section above records "every non-NA part
`successorPartID` names a real part (0 mismatches in 22,439)" — that held for
`DataPartTypeIGC` records only; launcher successors were not in that count. This
validator checks both buckets and surfaces the three launcher dangles.)

No map in the corpus dangles internally: every station/warp/asteroid names a
real sector, every warp destination is a warp or a sector, and every station
references a station type that exists in **at least one** core. Maps do not name
their governing core in the file, so the station-type check cannot be pinned to a
single core — it is reported as "resolves somewhere in the corpus", and the
server pairing is a boot-time choice.

### What is NOT done here

- **Boot-path wiring past the type universe.** The loader that picks *the* core
  and *the* map pair, wires them into a runnable world, and drives the 20 Hz sim
  is a later checklist item. The static, non-sim half of that — the live mission
  object graph — is now done (see **Live static mission graph** below); what
  remains is the runtime (site/tick) layer.
- **Core↔map pairing.** Nothing in the files says which core governs which map;
  choosing the pair is left to the caller (`Validate(core, map)` takes both).
- **The 34 dangling successors are intentionally not repaired.** They are real
  shipped data; fixing the *data* is out of scope and loosening the *check* would
  hide them. They stay flagged.
- **Resolver/graph layer.** By design the *records* keep raw IDs and lookup is
  through the dictionaries — but the static <b>`Mission`</b> graph (`Model/Mission.cs`)
  now resolves the map's cross-references into concrete objects: one
  `Sector` per `OT_cluster` with its warps/stations/asteroids, and warp
  destinations resolved to the target `Sector`. Runtime/savegame objects
  (`OT_ship`, …) still have no resolver.
- Runtime/savegame map objects (`OT_ship`, `OT_mine`, …) remain unimplemented —
  see the next section; they carry zero records in the corpus.

## Live static mission graph — `Mission.Assemble`

Checklist item on the road from "two documents" to "a runnable world": the
**static** half of a mission build, turned into a concrete object graph. The
runtime half (site + tick) is deliberately out of scope here. Trace and
rationale live in **`MISSION_INIT.md`** (traced against `src/Igc/missionigc.cpp`);
this section is the summary.

`Model/Mission.cs`:

```csharp
public sealed class Mission
{
    public StaticCore Core { get; }
    public MissionMap  Map  { get; }
    public IReadOnlyList<Sector>   Sectors { get; }   // one per OT_cluster
    public IReadOnlyList<Side>     Sides   { get; }
    public IReadOnlyList<ResolvedWarp> Warps { get; }
    public IReadOnlyList<ResolvedWarp> DanglingWarps { get; }   // reported, not thrown
    public static Mission Assemble(StaticCore core, MissionMap map, IReadOnlyList<SideSpec> sides);
}
```

- **`Sector`** wraps a `DataClusterIGC` and its *resolved* contents — the warps,
  stations and asteroids whose `ClusterId` equals the sector's id. Exactly how
  the engine builds `CclusterIGC` objects and aggregates their contents by
  `clusterID` (`warpIGC.cpp:29,49`).
- **Warp destinations** resolve to the `Sector` they point at: a warp's
  `DestinationId` names its partner warp, and the destination sector is that
  partner's owning cluster (or the sector named directly — the corpus validator
  accepts either). Unresolvable destinations are collected in
  `DanglingWarps`, never thrown.
- **`Side`** is minimal: id, name (if the host has one), and its civilization
  resolved from the core. **Sides are not derivable from core+map** — the engine
  synthesizes them from `MissionParams::nTeams`/`rgCivID[]` plus host-supplied
  names (`UpdateSides`, `missionigc.cpp:3734`), none of which the two documents
  carry. So `Assemble` takes `IReadOnlyList<SideSpec>(Id, Name?, CivilizationId)`
  as a parameter rather than inventing sides (this replaces the `int teams`
  sketch).

Measured across the whole corpus (2026-08-28): **111/111 maps assemble** against
a representative core; **all 5212 warp destinations resolve** to a sector inside
the same mission (0 dangling); **sector contents sum exactly** to the raw record
counts of each `MissionMap`. No map has a duplicate `ClusterId`, so contents
attribute unambiguously. `tools/missiondump` prints the assembled graph (sector →
warps with resolved destination sector names, stations, asteroids).

## The IIgcSite seam

Checklist item on the road from "two documents" to "a runnable world": the host
callback seam the mission binds before it can tick. Traced in `MISSION_INIT.md`
§1 (§10 cross-check: *"the site must be bound before anything"*, `missionigc.cpp:
3058`). Lives in `src/Allegiance.Core/Sim/`.

`IIgcSite` is a direct analogue of `class IIgcSite : public IObject`
(`src/Igc/igc.h:4757-4924`) — **77 virtual methods**. Ported as a C# interface
with **default implementations** for every member, so an implementor overrides
only what it cares about. This mirrors the C++ "base class with sensible
no-op defaults" pattern; it is deliberately **not** an abstract class and no
member is abstract. Of the 77: **67 are ported, 10 virtual methods are skipped**
(as listed below), and `SendChatf` — a non-virtual inline `printf` helper that
just forwards to `SendChat` — is not a seam member and is not ported.

### Decisions

- **`Time` → `double` seconds.** The C# codebase has no existing time type and
  no global clock; the C++ `Time` (`zlib/ztime.h`) is a uint32 *millisecond*
  accumulator. `double` seconds chosen because the tick math is float-seconds
  and a seconds-based time is the least-surprise representation for the coming
  tick. All `Time` parameters in the port are `double`, documented in
  `Sim/IIgcSite.cs` and `Sim/IgcEnums.cs`.
- **IGC object pointers → empty marker interfaces** in `Sim/IgcMarkers.cs`
  (`IModelIgc`, `IShipIgc`, `IStationIgc`, `IClusterIgc`, `IAsteroidIgc`,
  `ITreasureIgc`, `IPartIgc`, `IBucketIgc`, `IDevelopmentIgc`, `IStationTypeIgc`,
  `IHullTypeIgc`, `ISideIgc`, `IPartTypeIgc`, `IDroneTypeIgc`, `IProbeIgc`,
  `IMissileIgc`, `IBuildingEffectIgc`, `IMineIgc`, `IWarpIgc`, `IMagazineIgc`,
  `IDispenserIgc`, `ILauncherIgc`, `IExpendableTypeIgc`, `IMissionIgc`). They
  are **empty placeholders with no invented members**, so signatures are faithful
  now and can be filled by the object model later. Object-derived markers
  inherit `IModelIgc` exactly as the C++ model hierarchy does.
- **Scalar typedefs** map to primitives: IDs (`ObjectID`, `HullID`, `SectorID`,
  `SoundID`, `DamageTypeID`…) → `short`/`byte` per `igc.h:453,477,556`;
  `Money` → `int` (`igc.h:840`); `EquipmentType` is the existing
  `Data.EquipmentType` (`igc.h:530`) — reused, not duplicated. Genuine C++
  enums become C# enums (`Sim/IgcEnums.cs`): `ChatTarget` (`igc.h:2729`),
  `BucketChange` (`igc.h:4748`), `LoadoutChange` (`igc.h:4732`).
- `Vector` positions reuse the existing `Collision.Vector3`.

### Deviations from the C++ defaults (trust igc.h, say so)

Three C++ default bodies need members the **empty markers cannot provide**, so
they degrade to a safe value here and are called out:

- **`KillTreasureEvent`** — C++ default is `ptreasure->Terminate()`
  (`igc.h:4846`). Our default is a no-op because `ITreasureIgc` is an empty
  marker with no `Terminate()`; the terminate happens once the object model
  exists.
- **`GetCluster`** — C++ default computes visibility
  (`pship->CanSee(pmodel)`) and the model's cluster. Impossible on empty
  markers → returns `null`.
- **`GetRipcordCluster`** — C++ default consults the ship's base-hull
  capabilities (`igc.h:4915`). Degrades to `null`.

Everything else preserves the C++ default: `ServerTimeFromClientTime`/
`ClientTimeFromServerTime` are identity; `CreateThingSite`/`CreateClusterSite`
return a fresh `ThingSite`/`ClusterSite`; `DockWithStationEvent`/
`LandOnCarrierEvent`/`RescueShipEvent`/`HitTreasureEvent`/`ContinueRipcord`
return `true`; `UseRipcord`/`HandlePickDefaultOrder`/`Reload` return `false`.

### Skipped (purely client/UI or unrepresentable) — why

- **`MessageBox`** — client modal dialog (`igc.h:4835`). Named in the task.
- **`PlaySoundEffect` (both overloads)**, **`PlayNotificationSound`**,
  **`PlayFFEffect`**, **`PlayVisualEffect`** — client audio / force-feedback /
  visuals (`igc.h:4830-4834`). A headless server has no ears or eyes.
- **`LoadRadarIcon`** — returns a client `IObject*` radar icon (`igc.h:4894`).
  HUD, not simulation.
- **`PostText`**, **`PostNotificationText`** — variadic `printf`-style wrappers
  that forward to `PostPlainText` (`igc.h:4848-4849`); the headless server
  formats its own strings. `PostPlainText` (non-variadic, `igc.h:4847`) **is**
  ported. (`SendChatf`, same shape forwarding to `SendChat`, not ported either.)
- **`GetDroneFromShip`** — returns a raw internal `Drone*`, not an IGC
  interface (`igc.h:4872`). No faithful C# return type yet; deferred to the
  object model.

These omissions are all deliberate and listed rather than silent.

### Sites

- **`ThingSite`** (`Sim/ThingSite.cs`) and **`ClusterSite`**
  (`Sim/ClusterSite.cs`) — the per-object and per-sector host shims, analogous
  to the C++ render sites (`igc.h:2986`, `igc.h:4511`). **Minimal by design:**
  identity/owner only. The C++ classes are large render-site surfaces
  (visibility, flares, decals, geometry) with **no `Update` hook**, so nothing
  further is added here — built by `CreateThingSite`/`CreateClusterSite`, the
  "biggest structural piece" the boot trace calls out.
- **`NullIgcSite`** — takes every default. A mission bound to it is legal but
  hears nothing. This is the site used by the map-assembly tests and
  `missiondump`.
- **`RecordingIgcSite`** — records every call (name + salient args, in order)
  into `Calls`, delegating to the interface defaults so observed behavior
  matches a plain site. This is the harness the tick test will use. It
  delegates through an inner `NullIgcSite` because casting `this` to the
  interface re-dispatches to its own overrides (class members shadow interface
  defaults in dispatch) and would recurse.

### Wiring

`Mission.Assemble(core, map, sides, site)` now takes an **`IIgcSite`** and
exposes it as `Mission.Site`. Omitting it is a compile-time error (non-optional
parameter); passing `null` throws `ArgumentNullException`. The invariant — *a
mission with no site cannot tick* — is thus impossible to violate, not merely
documented. Static assembly itself performs **no** host callbacks: the C++ only
calls back during *runtime* object construction (`modelIGC.cpp:117` creates the
thing site, `clusterIGC.cpp:41` the cluster site), which is the next task. So a
`RecordingIgcSite` observes **zero calls from `Assemble` itself** — the
verification requirement is met by exercising the site directly, not by
manufacturing a call during a step that legitimately makes none.

Tests: **all 111 maps assemble against a `NullIgcSite`**, plus a suite asserting
the default behaviors, `RecordingIgcSite` observation (ordered, args, null-vs-
object), `NullIgcSite` parity, and that `Assemble` binds the site and rejects
null. **1,383 passing** (prior 1,375 + 8 new).

## The tick — `Mission.Update` (`CmissionIGC::Update`)

Checklist item on the road from "a runnable world" to "a world that moves": the
simulation cadence, reproduced from `CmissionIGC::Update`
(`missionigc.cpp:3127-3185`). Lives in `Model/Mission.cs`. This task
implements **the tick and only the tick** — no ships, no flight model, no
collision, no combat, no economy, no damage tracks, no networking (each
section below says what is still stubbed). The point proved is *cadence and
ordering*; objects come next.

### The increment constant — found

`c_fTimeIncrement = 0.25f` at `src/Igc/igc.h:5120` (a module-level const float,
four use sites: `missionigc.cpp:3156` plus three in `shipIGC.cpp:3273,3326,3371`).
So the "250 ms" comment is right, but the definition is real and quoted, not
assumed: **0.25**, ported as `Mission.TimeIncrement`.

### The arithmetic (reproduced exactly)

```csharp
double deltaT = now - LastUpdate;                    // missionigc.cpp:3149
int n = 1 + (int)(deltaT / TimeIncrement);           // :3156
for (int i = 1; i <= n; i++)
{
    double timeUpdate = startTime + deltaT * (i / n);// :3163
    foreach (var sector in Sectors) sector.Update(timeUpdate);
    LastUpdate = timeUpdate;                          // :3177
}
```

- **Chunks are NOT fixed 250 ms.** `n = 1 + floor(deltaT / 0.25)`, then the
  interval divides into **n even chunks of `deltaT/n`**, each *at most* the
  increment. `n` is always ≥ 1 for any `deltaT > 0`.
- **Boundary surprise reproduced and asserted:** because `(int)` truncates, at
  exactly one increment (`deltaT == 0.25`) the quotient `1.0` truncates to `1`,
  so `n == 2`, **not** 1. `n == 1` holds only for `deltaT < 0.25`. The test
  pins `{0.239 → n=1}` and `{0.250 → n=2}`.
- **Cadence:** sides update **once per `Update`**; clusters update **once per
  chunk**. All side updates precede the first cluster chunk.

### Decision: `deltaT >= 600` throws (does not clamp)

The C++ `assert (now - m_lastUpdate < 600.0f)` ("never 10 minutes at a
time", `missionigc.cpp:3148`) fails in debug and silently proceeds in release.
We **throw** `InvalidOperationException` because advancing ten minutes in one
call is always a caller bug here — there is no legitimate use to preserve — and
silently clamping would hide it behind a fabricated sim that skipped everything
between. Throwing reproduces the assert's *intent* (fail loudly) in a
language with no build-time asserts. Likewise `now < LastUpdate` (the
`assert (now >= m_lastUpdate)` at `:3129`) throws. Both are documented on the
method and covered by tests.

### The not-started path

Only `Started`/`Starting` missions run the chunked pass. Any other
`MissionStage` (`NotStarted`, `Over`) makes `LastUpdate` jump straight to `now`
(`:3180-3181`) with **zero** side and cluster ticks. `Update` starts the mission
in `NotStarted` (the C++ constructor, `missionigc.h:397`).

### Postcondition

`LastUpdate == now` on every path: the `now == LastUpdate` no-op, the
not-started jump, and the chunked pass (whose last chunk, `i == n`, reduces to
`startTime + deltaT == now` exactly). Asserted in each test; because a
fresh mission's `LastUpdate` starts at 0 and each row ticks from 0, the final
chunk lands on `now` with no floating-point drift.

### Site routing and the one deviation

The only site call the C++ tick makes is `ClusterUpdateEvent`
(`clusterIGC.cpp:357`); the port routes each sector's per-chunk update through
`IIgcSite.ClusterUpdateEvent(sector)` (Sector carries the `IClusterIgc` marker
to stand in for `CclusterIGC`). The C++ *side* update sends nothing to the site
(`sideigc.h:41`), so a recording site would see no side activity and could not
prove "all side updates precede the first cluster chunk". To make that ordering
observable we added **one** C#-only instrumentation hook,
`IIgcSite.SideUpdated(ISideIgc, double now)`, a no-op default that changes
nothing for a real host and is explicitly labelled **not part of the C++
seam** in the interface. `RecordingIgcSite` overrides it to log
`SideUpdated(Side, now)`, so the ordering test watches `SideUpdated(...)` —
`ClusterUpdateEvent(...)` through one `RecordingIgcSite.Calls` trace. This is
the sole deviation from the C++ seam and it is observational, not behavioral.

### Stubs (out of scope, documented on each)

- `Sector.Update(double)` records `UpdateCount` + `LastUpdate`; the fleet,
  collision, combat and explosion passes (`clusterIGC.cpp:97-358`) are future
  work.
- `Side.Update(double)` records `UpdateCount` + `LastUpdate`; bucket/tech work
  (`sideigc.h:41-51`) arrives with the economy task.
- No `DamageTrack`, no payday, no ships, no KD-tree / sweep-and-prune.

Tests: `MissionUpdateTests.cs` — a table-driven `[Theory]` over `deltaT`
pinning the expected chunk count (including the `0.25 → n=2` boundary and the
`deltaT ≥ 600` throw), plus the no-op, backwards-clock, not-started/over
advance, exact-landing/no-drift, and site-ordering cases. **1,412 passing**
(prior 1,383 + 29 new).

### What the tick needs next

With the cadence in place, the next task can:
1. Instantiate the runtime objects (`CreateClusterSite`/`CreateThingSite` are
   called during object construction — see `clusterIGC.cpp:41`/`modelIGC.cpp:117`).
2. Fill the markers with real IGC object implementations (ships, then the
   cluster passes the tick already dispatches into a real per-chunk body).

## The flight model — `Ship.ExecuteMove`

Checklist item after the tick: **make a ship move**. This slice ports the
kinematics of `CshipIGC::ExecuteShipMove` and the state it needs, nothing more.
It lives in `src/Allegiance.Core/Sim/`:

- `Vector3` — the repo's single vector type (below).
- `Orientation` — `zlib/orientation.{h,cpp}` (class doc carries the evidence).
- `ControlData` — `struct ControlData` (`igc.h:1662-1671`): the four joystick
  axes `jsValues[4]` (yaw, pitch, roll, throttle), `Reset()` at throttle −1.
- `ShipControlState` — the movement bits of `ShipControlStateIGC`
  (`igc.h:803-835`), values copied verbatim.
- `Ship` — id, hull type, position, velocity, orientation, controls, turn
  rates, state bits.
- `Ship.ExecuteMove(double timeStart, double timeStop)` — the port itself.

`ExecuteMove` reproduces `CshipIGC::ExecuteShipMove(timeStart, timeStop,
&vel, &ori)` (`shipIGC.cpp:2852-3054`) step for step: torque-ramped turn rates
(`:2885-2910`), `Yaw/Pitch/Roll` + `Renormalize` (`:2912-2917`), the drag term
with double intermediates (`:2927-2935`), the thrust branch — manual
direction buttons, coast/vector-lock, or throttle-cruise (`:2961-3006`) — the
engine clip with the asymmetric side/back multiplier (`:3008-3033`), and the
velocity increment (`:3037`). `GetTorqueMultiplier()` is
`shipIGC.h:542-549` (0.50 at rest, up to 2.50), read from the current speed.

### Vector: one type, not two

The C++ has a single `Vector`. The repo had already adopted
`Collision.Vector3` for positions (README "The IIgcSite seam"). Initially a
second `Sim.Vector3` was tried for the flight math, but because `Sim` is a
child namespace of `Allegiance.Core` and its members use unqualified `Vector3`,
the new type silently re-bound the seam's positional signatures from
`Collision` to `Sim` — changing meaning with no code change at the point of
definition. Rather than ship a latent, subtle rebinding, **the flight
arithmetic was added to the existing `Collision.Vector3`** (component ±, scalar
∗//, dot, cross, length), and the flight model uses that single type. So
`Sim/Vector3.cs` does not exist; `Orientation`/`Ship`/`ControlData` live in
`Sim` and reference `Collision.Vector3`, exactly as the seam does. This
deviates from the task's literal "build `Vector3` in `Sim/`" and is the
deliberate, documented choice.

Division-by-scalar multiplies by the reciprocal — `Vector::operator/`
(`vector.h:158-162`) — which is bit-faithful (a naive `v/s` is not).

### Decisions with evidence (the C++ wins)

- **Handedness: left-handed** when forward is the nose. The stored matrix rows
  are a proper rotation (det +1), so {right, up, backward} is right-handed
  among itself — `right × up = backward` (`orientation.h:83-104`,
  `orientation.cpp:358-390`). But `GetForward() = −row2`, so {right, up,
  forward} has det −1. Proofs: the identity orientation noses at `(0,0,-1)`,
  and `Set(forward, up)` computes `right = forward × up` which for identity
  gives `right×up = (0,0,1) = backward = −forward`. In a right-handed frame
  `right×up` would equal `forward`; it does not. Positive yaw is therefore a
  left-handed rotation about +up, swinging the nose from −z toward −x
  (`orientation.cpp:538`).
- **Rotation order:** three successive **world-axis pre-rotations** in the
  order yaw, then pitch, then roll, with pitch sign flipped
  (`shipIGC.cpp:2912-2914`: `Yaw(rate·dT)`, `Pitch(−rate·dT)`,
  `Roll(rate·dT)`). Each `Yaw/Pitch/Roll` left-multiplies the orientation by a
  fixed rotation about +y/+x/+z respectively, so the composite is
  world-axes yaw∘pitch∘roll with a negated pitch. `Renormalize()`
  (`orientation.h:156`) re-derives the basis from forward & up every step,
  which is what keeps it orthonormal across arbitrarily many steps.
- **No speed clamp.** There is no `if speed > maxSpeed: speed = maxSpeed`
  anywhere in `shipIGC.cpp`'s movement path; the throttle branch merely chooses
  a target of `min(speed, maxSpeed)` for cruise, and the engine clip limits
  acceleration, not instantaneous speed. Reproduced faithfully (a `"fix"`
  would be a wrong model). Measured consequence: sustained manual forward
  thrust at a coarse step (`dT=0.01`) eventually **exceeds** `hull.Speed`,
  because the forward-button force's terminal equilibrium sits above the hull
  number for large steps.
- **Asymmetric side/back multiplier** is real and reproduced: the engine clip
  divides lateral thrust by `sideMultiplier` and (only positive) rearward
  `z` thrust by `backMultiplier` (`shipIGC.cpp:3015-3017`). Backward thrust is
  thus scaled harder — a genuine quirk, kept.
- **Simplifications (documented, not hidden):** no ripcord (`m_pmodelRipcord`
  false, so both guarded blocks always run; drag is still computed and
  subtracted), and no afterburner part (so `thrustRatio == 0` and no AB drag;
  the `afterburnerButtonIGC` bit still gates the throttle branch, exactly as
  in the reference).

### Determinism verification (all in `ShipFlightModelTests.cs`)

- identical inputs ⇒ **bit-identical** outputs, two independently-constructed
  ships through 768 odd-sized steps, compared with no tolerance;
- zero control input + zero velocity ⇒ **nothing moves** (200 steps);
- pure forward thrust from rest ⇒ velocity grows **only** along the facing
  axis (laterals < 1e-4, speed well past 1);
- turn rates never exceed `maxTurnRates` (full-yaw 1000 steps) and converge to
  the target;
- no clamp: starting above `maxSpeed` and coasting stays above after a step;
- orientation orthonormal after **10,000** steps (`Renormalize` doing its job).

### Chunk divergence (measurement, not assertion)

Stepping one interval vs. the mission's even sub-steps legitimately diverges,
because turn rates, `GetTorqueMultiplier()` and the throttle are re-evaluated
per sub-step (this is the same phenomenon as the `dT`-dependent plateau above).
`Measure_single_vs_split_step_divergence` prints:

```
CHUNKDIV deltaV=0.0031761578 deltaForward=0.00088577415 (total=0.0950523 s, 3 chunks) hull.Speed=120
```

i.e. a 0.095 s interval run whole vs. split into 3 chunks differs by
**≈3.2e-3 m/s** in velocity and **≈8.9e-4** in the forward-axis direction on
a hull with `Speed = 120`. Small but nonzero — expected, and a good thing to
know before integration (the mission will run the split form).

### What the next slice needs

Done — see **Ships in sectors — the tick moves them** below. What it took, from
the earlier plan: a `Model` base, `Ship : Model`, `Sector` carrying a model
list walked per chunk, and position integration (`Δpos = velocity·ΔT`, which
`ExecuteShipMove` deliberately does not do — the mission/chunk driver owns it,
cf. `CalculateShip`, `shipIGC.cpp:3273-3286`). Collision, weapons, docking,
ripcord and the economy remain untouched.

## Ships in sectors — the tick moves them

Checklist item after the flight model: **connect the ship into the ticking
mission so a mission tick physically moves ships.** It lives in
`src/Allegiance.Core/` across `Sim` and `Model`:

- **`Sim/Model.cs`** — the `Model` base, the port of the C++
  `ImodelIGC`/`CmodelIGC` abstraction (`modelIGC.h:22`), kept to what this
  slice needs: `Id`, `Position`, and `Update(double now)`. **No class hierarchy
  for objects that do not exist yet** — `Ship` is the only concrete model.
- **`Ship : Model`** — becomes a sector-ticking model. Its `OnUpdate(now, dt)`
  runs `ExecuteMove(now - dt, now)` then integrates `Δpos = velocity·ΔT` (the
  step `ExecuteShipMove` deliberately omits; owner is the mission/chunk driver,
  cf. `CalculateShip`, `shipIGC.cpp:3273-3286`). Velocity/orientation are bit-
  identical to a direct `ExecuteMove` — the wrapper adds no physics.
- **`Sector`** gains a mutable model list, `Add`/`Remove`/`Models`, and its
  `Update` walks the models exactly as the C++ models loop does
  (`clusterIGC.cpp:241-253`). It also gained the **`now > m_lastUpdate`
  guard** (`clusterIGC.cpp:99`) it lacked as a stub.
- **`Mission.PlaceShip(Ship, Sector)`** / `PlaceShip(Ship, short sectorId)` —
  a way to place a ship in a sector at construction; validates the sector
  belongs to the mission, then routes to `Sector.Add`.

### The gate the task flagged (the C++ wins)

Reading `CclusterIGC::Update` (`clusterIGC.cpp:97-253`) directly: the body is
gated on `now > m_lastUpdate` (`:99`), and inside it the **station/ship/cost
block is gated on `STAGE_STARTED` (`:103-231`), but the models loop is NOT** —
`l->data()->Update(now)` at `:241-253` runs unconditionally for every tick of a
live cluster, started or not. So the port runs models on **every** sector tick
whenever `now > LastUpdate`, with no stage check. (A sector does not carry the
mission stage, and the tick only reaches sectors once the mission is
`Started`/`Starting`, so in practice the model loop is still confined to a
running mission — but the reproduction is faithful: the models loop itself has
no gate.) Note the *other* half of the C++ function — ship plot/execute
(`:162-228`), drone launch, cost, and the whole `activeF` collision block
(`:255-358`) — is **out of scope** and deliberately absent.

### Decisions with evidence

- **Where `dt` comes from.** The base `Model.Update` owns the per-model clock
  and guard (both `CmodelIGC::Update` and `CshipIGC::Update` recompute
  `dt = now − m_lastUpdate` and act only when `now > m_lastUpdate`,
  `modelIGC.cpp:79-80`, `shipIGC.cpp:271-275`); it hands the derived model its
  elapsed `dt` in `OnUpdate(now, dt)`, then stores `now`. A backwards or
  repeated `now` is a no-op on a model, matching the C++ guard.
- **Position integrated in `OnUpdate`, not a separate `Move()`.** The C++ keeps
  a separate `Move()` pass (`clusterIGC.cpp:338-346`) inside the out-of-scope
  `activeF` collision block; splitting position out again here would drag that
  block's gate in. Integrating in the model's tick is the minimal honest place
  — the physics (`ExecuteMove`) is untouched and called verbatim.
- **`Sector.Update` gained a guard.** As a recording stub it had none; the C++
  gates everything on `now > m_lastUpdate`. Added with the models walk so a
  repeated/earlier sector tick is a genuine no-op, exactly as in C++.
- **Loop is a plain forward walk.** The C++ captures the successor link
  (`lNext = l->next()`) before `Update` because models may self-terminate; no
  model self-terminates in this slice, so a straight `foreach` over the live
  list is correct and the list is not mutated during iteration.

### Verification (all in `ShipInSectorTests.cs`)

- **The tick does not change the physics:** a ship through a real
  `Mission.Update` (multi-chunk `0.908 → n=4`, and single-chunk `0.093 → n=1`)
  is bit-identical in velocity, orientation and position to a reference that
  replays the same chunked `ExecuteMove` + integration. The wrapper only
  decides cadence.
- **An empty mission ticks harmlessly** — sectors still tick (increment counts,
  advance `LastUpdate`), models list stays empty, nothing touched; a removed
  model is not re-ticked.
- **Sector isolation:** a ship in sector A is untouched when sector B ticks
  on its own (`B.Update`), including sector A's own tick count staying 0.
- **The chunked cadence reaches models:** a model sees **n** updates, not one
  (`deltaT = 1.077 → 5` counts via a test-only observer), and a travelling ship
  is the strongest witness — after `0.903 s` (n=4) it has accelerated and
  displaced along −z.
- **Guard:** a model is a no-op on backwards/repeated time; a forward call runs
  once.
- **⭐ Carry-forward divergence measurement** (report, not assertion) — a ship
  stepped by the mission over `0.905216 s` (4 chunks) vs one big `ExecuteMove`:

```
CHUNKDIV deltaV=1.6417305 deltaPos=3.449893 deltaForward=0.075840645 (total=0.905216 s, 4 chunks) hull.Speed=120
```

The split-vs-single gap is now much larger than the flight model's 0.095 s/3-chunk
figure (Δv≈3.2e-3) because this interval is ~9.5× longer and the divergence
compounds per chunk — Δv≈1.64 m/s, Δpos≈3.45 m, Δforward≈0.076 rad over 0.9 s on a
hull with `Speed = 120`. Expected, and the reason the mission runs the split form:
each sub-step re-evaluates turn rates, torque multiplier and throttle.

Tests: **1435 passing** (prior 1426 + 9 new). `MissionUpdateTests` cadence cases
(including the `0.25 → n=2` boundary) are untouched and still green.

## The FEDMSG wire — framing + logon handshake

The beginning of the bot-facing transport. WOPR/Pigs already speak this wire
protocol, so getting the framing right means a future server can accept those
existing bots **unchanged** — a free differential-parity harness against the
original. Detailed spec (layout tables, `file:line` references, deviations) in
**`FEDMSG.md`**; this is the summary.

### The wire format

- **Message** = `cbmsg`(u16) + `fmid`(u16) + fixed fields + variable blobs,
  all **little-endian**. `cbmsg` is total size incl. itself; `fmid` is the id
  (1 = `LOGONREQ`, 2 = `LOGONACK`). No `#pragma pack` on messages — MSVC default
  alignment (each member to its size, struct to its largest member).
- **Variable items** come first as `ib`/`cb` (u16,u16) pairs — `ib` = byte
  offset of the item from message byte 0, `cb` = length (0 = absent; includes
  the trailing NUL for strings). The blobs themselves are appended after the
  entire fixed region (`PFedMsgCreate`, `_Utility/Messages.cpp:338-439`).
  `CB_ZTS` strings are trailing-space-trimmed then NUL-terminated.
- **Packet** = concatenation of messages + a single trailing 4-byte CRC over
  the message bytes only. `cbmsg` walks the messages; the CRC is checked on
  receipt. Algorithm: reflected IEEE CRC-32 table, seed `0xFFFFFFFF`, **no
  final XOR**, result `0 → 1` (`_Utility/CRC.cpp`). Vendors calling it "plain
  CRC-32" are wrong about the final inversion; the C++ wins.
- **`MSGVER = 216`** is the client's `fedsrvVer`. The server accepts only
  `fedsrvVer == 216`; on any mismatch it replies with a `LOGONACK` carrying
  `fValidated = false` and a reason string (`FedSrv.CPP:5824, 6051-6082`) — not a
  socket drop.

### What is ported

`src/Allegiance.Core/Wire/` (byte-level encode/decode only — no sockets, no
DirectPlay, no async/DI/reflection, no new NuGet packages):

- `FedMessageHeader` — the 4-byte `cbmsg`/`fmid` base.
- `VarItem` — an `ib`/`cb` pair; generic variable-item encode/decode helper.
- `WireWriter`/`WireReader` — little-endian cursors reproducing the C++
  `PFedMsgCreate` placement (fixed region then blobs).
- `FedMsgCrc` — the exact CRC variant above.
- `FedPacket` — concatenate messages + append/verify the CRC; message walk.
- `LogonRequest` / `LogonAck` — the two handshake messages, round-trippable.

### Beyond these two, what was added and why

- **STEAM fixed fields** (`steamAuthTicket[1024]`, `steamAuthTicketLength`,
  `steamID`, `drmHash[50]`) are included in `LogonRequest` because modern
  clients populate them and they change the fixed size/offset math (1128 vs
  36 bytes). Decode is tolerant: the ticket is stripped of trailing NULs, so a
  server need not insist on exact steam payloads.
- `FedPacket` (concatenation + trailing CRC) because the handshake rides a
  packet and the CRC-over-correct-range guarantee lives here.
- No other `DEFINE_FEDMSG` of the 136 was touched.

### What a bot connection still needs (NOT done)

- Transport over a socket matching the DirectPlay session semantics; the
  handshake alone is byte framing, not connection establishment.
- The lobby / encryption / CD-key scrambling path — out of scope by design.
- Handling the **real client's stale fifth var item** that aliases
  `steamAuthTicket[0..3]` (FEDMSG.md §2). Our decoder follows the *declared*
  four-var-item layout; to accept an unpatched modern client verbatim we would
  need to tolerate that aliased pair.
- Authenticating the zone ticket / character (server-side auth is unimplemented;
  the current port only frames and round-trips the messages).

### Verification

19 new tests under `tests/Allegiance.Core.Tests/Wire/`: message round-trip
(including variable items), **byte-exact** checks against hand-built buffers
composed from the spec, the CRC against a published check value and an
independent block-vs-step equivalence, packet corruption → `FedCrcException`,
and a wrong `MSGVER` producing the rejected-ack it should.

### TCP transport over the wire — `src/Allegiance.Core/Transport/`

The loopback proof from TRANSPORT_SEAM.md §Recommendation: a real TCP socket
carrying the already-shipped `Wire` codec, one host + one client (bot role),
handshaking `LOGONREQ`/`LOGONACK`. No C++ edits. This section is the summary;
spec rationale and the C++ trace are in **`TRANSPORT_SEAM.md`**.

**Interfaces** (adapted, not transliterated — no `HRESULT`, no `DPID`):

- `ITransport` — `Host(port=0)`, `Join(address, port, playerName)`,
  `Shutdown()`, `IsConnected`, `LocalId`, `BoundPort`, and
  `Send(target, ReadOnlySpan<byte>, guaranteed)`. Group fan-out and CRC are
  deliberately NOT here (§2 of the seam): the transport deals only in ids and
  bytes; the caller packs/validates with `FedPacket`.
- `ITransportSite` — `OnPeerJoined(id, name)`, `OnPeerLeft(id)`,
  `OnSessionLost()`, `OnReceive(from, ReadOnlySpan<byte>)`. These fire on
  background threads; an implementor wanting `FedMessaging`' single-owner
  dispatch funnels them through a queue, exactly as the `m_csMsgList`
  serializer does today.
- Idiom choices (asked-for in the task): **exceptions** over result/`HRESULT`
  codes; `ConnectionId` as an immutable `readonly record struct` wrapping a
  `uint` (value equality, opaque). I kept it unsigned-32-bit to mirror `DWORD`.

**The framing decision (the crux)** — the transport adds its **own outer length
prefix** and does not rely on `cbmsg`. Reasons:

- FEDMSG.md §3 is explicit: *"there is no outer packet length; the
  socket/transport supplies the packet length."* The delimiter is ceded to the
  transport.
- `cbmsg` is per-**message**, not per-packet. A packet is a concatenation of
  zero-or-more messages + a trailing CRC, and nothing on the wire says how many
  messages precede the CRC. Without an outer length the receiver cannot know
  the packet boundary, so it cannot know where the CRC is or validate it.
  Relying on `cbmsg` would mean assuming exactly one message per packet and
  re-parsing message framing inside the transport.
- The prefix lives **outside** the CRC (which covers only message bytes), so a
  frame's body is byte-for-byte the codec's output and `FedPacket.Unpack` works
  on it unchanged.

Frame header = `type(1) + length(4, LE)`. `type` distinguishes a one-shot
**Greet** (the joining peer's name) from a **Data** frame (a `FedPacket`). The
name must ride in-band because TCP has no peer-metadata channel — this is the
TCP analog of the two `GetClientInfo` calls DirectPlay made at `CREATE_PLAYER`.

**OnPeerJoined before any OnReceive** (Recommendation constraint 1): the host
consumes the greeting, mints the id, stores the stream, and only then calls
`OnPeerJoined(id, name)`; datagram dispatch cannot start until that returns.

**Sync-not-async**: sockets were the one place async was permitted, and we chose
**not** to spend it — the transport runs blocking `Read` loops on background
threads (one accept loop, one per peer). A blocking loop is dramatically easier
to make correct for stream reassembly (partial reads) than an async state
machine, and this proof has no throughput demand. Async/`await` appears only in
`tools/loopback`'s top-level `TaskCompletionSource` waits, i.e. outside the
library. `guaranteed` is accepted and ignored — TCP is already reliable.

**Proof** (`tests/.../Transport/TcpLoopbackProofTests.cs`, +2) over a real
loopback socket: both directions of `LOGONREQ`/`LOGONACK`, CRC validated both
ways, corrupted byte → `FedCrcException`, `OnPeerJoined` sequenced before
`OnReceive`, and **partial reads** — a data frame driven byte-wise in two
writes separated by 150ms and reassembled. Plus `tools/loopback` (two OS
processes; `run.sh` orchestrates them): host binds an ephemeral port, prints
it, the client joins and they exchange the handshake, each process printing
its half. **1,465 tests green.**

### What the C++ side still needs (post-proof)

The proof settles the transport+codec seam; the C++ seam is separate work in
`Allegiance/` (read-only here, out of scope):

- Wrap DirectPlay behind the same `ITransport` (`DirectPlayTransport`) or do the
  in-place `Messages.cpp` rewrite (~30 functions per §5.1). The message framing,
  CRC, out-box, and public API stay untouched.
- **Server-side group fan-out** (§3.3): translate `SendTo(group)` into a
  per-member `Send` loop on the C# host. Not needed for a bot client, which
  only ever does reliable unicast to the server connection.
- **Session GUID in the connect hello** (§3.4) so the client's enum-on-join can
  be skipped. Our Greet currently carries only the name; a future revision adds
  the instance GUID.
- **Best-effort semantics** reduce to "disconnect = NAK" on reliable TCP
  (§3.2). The ping-loss heuristic changes but stays functional.

### Surprises worth recording

- `ManualResetEventSlim` signaling in the first cut of the test harness misled
  (the event reported set with no matching record); switching to plain
  list-polling waits removed the fragility entirely — worth remembering for any
  threaded test here.
- Flipping a byte then recomputing the CRC is **self-consistent** — you must
  flip inside the already-packed packet for a real corruption test. Obvious in
  hindsight, easy to get wrong.
- No NuGet packages were added (`System.Net.Sockets`/`System.Net` are BCL);
  `/snap/bin/dotnet` (a broken stub) was avoided, as was the read-only
  `Allegiance/` tree.

## Not implemented

The runtime map-object types `OT_ship` (0), `OT_treasure` (8), `OT_mine` (3),
`OT_probe` (4), `OT_buoy` (9) and `OT_bucket` (14) are **intentionally not
implemented** — they carry **zero records** across the whole 210-file corpus
(measured 2026-08-28), so a decoder for them would be an unfalsifiable guess
that compiles, passes, and silently rots. They are runtime/savegame objects
that never appear in a shipped `.igc` map. (This corrects an earlier draft of
this section that listed ship and treasure as pending work.)

Layouts are specified in the wiki page `allegiance-igc-file-format`.
⚠️ That page carries a **corpus-measured corrections** section; where it
contradicts an earlier section, the corrections win. Where the page and
`src/Igc/igc.h` disagree, `igc.h` wins (it is the actual writer).

## Facts worth keeping

- **Plaintext on disk.** The `munge` decrypt hook is wired only for the Zone
  Club download path; `ALLSRV_STANDALONE` passes NULL
  (`src/FedSrv/fsmission.cpp:164-166`). No decryptor needed.
- **Hull stride is 30**, not 29: `540 + 30 * maxWeapons`. Sizes observed are
  540/570/600/…/780, every delta exactly 30.
- **The version word is a Unix timestamp** — `PCore001.igc` carries
  `990051329` = 2001-05-17, matching
  `iStaticCoreVersion = static_cast<...>(time(0))` (`missionigc.cpp:2363`).
- **Artwork lives in two trees.** A search scoped to the code repo alone finds
  almost none of the corpus.

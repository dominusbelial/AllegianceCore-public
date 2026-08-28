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

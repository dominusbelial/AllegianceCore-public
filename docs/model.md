# Model

Extracted from README.md 2026-08-29 (it had reached 1,096 lines).

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

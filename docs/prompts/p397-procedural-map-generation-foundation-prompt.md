# P397 — procedural mission-map generation, foundation + SingleRing (task 1 of ~5)

## Why this task exists

Confirmed via a cross-repo structural graph comparison + native source reading
this session: native's real mission-setup code has a live fallback path this
port has never had any equivalent of. `missionigc.cpp:3902-3906`:
```cpp
if (pmp->szCustomMapFile[0] == '\0' || !LoadIGCFile(pmp->szCustomMapFile, this))
    ImapMakerIGC::Create(now, pmp, this);   // procedurally generate the whole mission
else
    // load the named custom map file
```
This port's boot path (`Server.Lifecycle.cs:666-670`) only ever does the
`else` branch — `ServerConfig.Map` is required, and a missing/invalid map
throws `InvalidDataException` at boot. There is no procedural-generation
fallback at all. This is a real, confirmed gap, not an out-of-scope
editor-only tool: `ImapMakerIGC::Create` is invoked from the live mission
setup path every real game can hit.

**This is a large feature (15 real registered map topologies in native) —
this is task 1 of an intentionally-split sequence**: shared infrastructure
+ the dispatch mechanism + exactly one topology (`SingleRing`, native's own
default/simplest — zero method overrides beyond the base class) as a real,
complete, end-to-end working proof. The other 14 topologies are explicitly
out of scope for this task and will follow as separate tasks once this
foundation lands and its real API shape is known.

## Confirmed native mechanics

**Dispatch** — `MissionParams.mmMapType`, a real enum with 15 values
(`c_mmSingleRing`, `c_mmDoubleRing`, `c_mmPinWheel`, `c_mmDiamondRing`,
`c_mmSnowFlake`, `c_mmSplitBase`, `c_mmBrawl`, `c_mmBigRing`, `c_mmHiLo`,
`c_mmHiHigher`, `c_mmStar`, `c_mmInsideOut`, `c_mmGrid`, `c_mmEastWest`,
`c_mmLargeSplit` — `mapmakerigc.cpp:129-166` `IsValid` switch,
`:182-201` `Create`'s dispatch switch). A mission config picks the
topology explicitly — not random. `ImapMakerIGC::Create(now, pmp, this)`
instantiates the matching subclass and calls `GenerateMission`.

**`CMapData`** (`mapmakerigc.h:40-175`) — a per-generation-run context
object, not persisted state: mission/params/time references, a 72-name
cluster-naming pool (`GetNewClusterName`, `.cpp:119-128`), per-cluster
storage arrays (`IclusterIGC* mpCluster[50]`, `SideID
msideIDCluster[50]`), team/neutral/other cluster counts derived from
`MissionParams`, and a monotonic `GetNextWarpID()` counter. No real
algorithm lives here — pure state plumbing, trivial to port as a plain C#
class.

**The shared algorithm** (`CmapMakerIGC::GenerateMission`,
`mapmakerigc.cpp:340-483`, runs identically for every topology — only 4
virtual hooks differ per variant, and `SingleRing` overrides none of
them beyond what the base class already does by default):
1. `GenerateLayout(&MapData)` (virtual; base impl `.cpp:879-893`): sets
   team-clusters-per-team (1 or 2, from `MissionParams.iMapSize`),
   neutral-clusters-per-team=1, calls `GenerateRingClusters` then
   `LinkClusters()`.
2. `GenerateRingClusters` (`.cpp:647-678`, **shared by every variant,
   never overridden**): walks every cluster slot, calls virtual
   `GenerateTeamCluster`/`GenerateNeutralCluster` per slot. Each
   (`.cpp:550-644`) builds a real `DataClusterIGC` (randomized
   planet/poster/debris/star cosmetics, a name from the pool) and calls
   `pMission->CreateObject(now, OT_cluster, &dc, sizeof(dc))` — creates a
   brand-new sector at runtime, not a decoded one.
3. `LinkClusters()` (no-arg, virtual; base impl `.cpp:858-876`, the real
   per-variant topology hook) — base/default = a plain ring (chain
   `i→i+1`, wrap last→first) plus `CrossLinkClusters` for >3 teams. Calls
   the shared 3-arg `LinkClusters(pMapData, sID1, sID2, alephType)`
   (`.cpp:705-835`, never overridden) which builds a real bidirectional
   `DataWarpIGC` pair via `GenerateWarp` → `CreateObject(OT_warp,...)`,
   picking aleph type/icon/texture/signature from the friend/enemy/
   neutral relationship between the two clusters' sides.
4. Back in `GenerateMission`: cosmetic warp-position randomization
   (screen-space UI cosmetics only, `.cpp:362-424` — **out of scope**, no
   analogous 2D map-editor screen in this port); optional
   "random encounters" warp-pruning that preserves 2-path redundancy
   (`.cpp:219-337` — **out of scope for this task**, note as a real
   simplification if skipped); `PopulateClusters` (`.cpp:944-967`) — for
   every home-sector cluster calls `GenerateStarbase` (`.cpp:896-941`,
   builds a real `DataStationIGC` from the side's civilization's initial
   station type, `CreateObject(OT_station,...)`) then the free function
   `PopulateCluster` (`common.cpp:2963-...`, signature: `(ImissionIGC*
   pmission, const MissionParams* pmp, IclusterIGC* pcluster, float
   amountHe3, bool bAsteroids, bool bTreasures, short
   nMineableAsteroidsMultiplier, short nSpecialAsteroidsMultiplier, short
   nAsteroidsMultiplier)` — real asteroid/treasure placement, trace this
   function yourself as part of this task, it's a real shared dependency
   every future topology task will also need); `RevealHomeClusters`/
   `RevealAlliedClusters`/`RevealMap` (`.cpp:969-1058`, visibility flags
   only); `ActivateSides` (`.cpp:1060-1070`, flips sides active).

**`SingleRing`'s own contribution**: confirmed it overrides nothing beyond
what every variant already gets from the base class's own default
`GenerateLayout`/`LinkClusters` — it IS the base-class default behavior,
registered as its own named map type. This is exactly why it's the right
first topology to port: zero topology-specific code, pure infrastructure
proof.

## Confirmed existing port infrastructure to reuse — do not reinvent

- **`Sector(DataClusterIGC cluster)`** (`Sector.cs:223`) — a real public
  constructor building a brand-new sector from a hand-built record,
  exactly matching native's `CreateObject(OT_cluster, &dc,...)` pattern.
- **`Mission.PlaceStation`/`PlaceShip`/`PlaceAsteroid`**
  (`Mission.cs:667,706,730`) and **`Sector.AttachWarp`**
  (`Sector.cs:286`, currently `internal`) and **`Sector.RemoveWarp`**
  (`Sector.cs:272`, P379) — the exact primitives this session's P379
  debug-topology work already proved out live (synthetic station
  injection, warp graph editing). Reuse these directly; widen visibility
  (`internal` → `public`, or add a same-assembly-friend seam) only if
  actually needed to call them from wherever this generator lives, and
  say so plainly in the report.

## Confirmed real gap to design around (this task's real architecture work)

`Mission.Sectors` is `IReadOnlyList<Sector>`, fixed at construction
(`Mission.cs:34,410`), and `Mission.Assemble(StaticCore, MissionMap,
IReadOnlyList<SideSpec>, IIgcSite)` (`Mission.cs:425`) — the real assembly
entry point — is always fed a file-decoded `MissionMap`
(`MissionMap.Load(mapFile)`). A generator needs `Mission` to accept a
**built-up** sector/warp/station list instead of (or as well as) one
decoded from a real file. **This is a real design decision for you to
make and document**, not something to guess from this brief — two
reasonable shapes:
(a) a new `Mission.Assemble` overload that takes generator-built
cluster/warp/station data directly, bypassing `MissionMap` entirely, or
(b) construct a synthetic in-memory `MissionMap`-shaped object that
flows through the existing `Assemble` path unchanged. Pick whichever is
more consistent with this codebase's existing conventions, and explain
the choice in the report — this decision shapes every follow-up
topology task, so get it right and document it clearly.

## Scope

1. Port `CMapData` as a plain C# class (e.g. `MapGenContext`) — the state
   plumbing described above, nothing more.
2. Add the map-type dispatch: a new config concept (e.g.
   `ServerConfig.MapGenType` or similar, nullable/unset by default) with
   exactly one real value implemented — `SingleRing`. `ServerConfig.Map`
   must remain fully backward-compatible: every existing config that
   names a real file continues to work byte-for-byte unchanged; the
   generator path is additive, reached only when no real map is
   configured (or a new explicit generate-mode field says so — design
   this the same way `debugMode` was added earlier this session: additive,
   opt-in, zero effect on existing configs).
3. Port the shared algorithm pieces: cluster generation
   (`GenerateRingClusters`-equivalent), the 3-arg link/warp-pair builder,
   `GenerateStarbase`-equivalent, and the `PopulateCluster`-equivalent
   (asteroid/treasure placement) — reusing `Mission.PlaceStation`/
   `PlaceAsteroid`/`Sector.AttachWarp` as the underlying primitives.
4. Port `SingleRing`'s layout (which is just the shared base-class
   default — a plain ring of team + neutral clusters).
5. Hook the boot path (`Server.Lifecycle.cs`, near the existing map-load
   branch at `:666-670`) to call the generator instead of
   `LoadIGCFile`/`MissionMap.Load` when generation is requested.

## Explicitly out of scope

- All 14 other map topologies — separate follow-up tasks, once this
  foundation's real API is known.
- Cosmetic 2D screen-position randomization for a map-editor UI — no
  analogous screen exists in this port.
- The "random encounters" warp-pruning/path-redundancy pass
  (`.cpp:219-337`) — note as a real, deliberate simplification if
  skipped, don't silently drop it without saying so.
- P368-P396 — unrelated, don't touch.

## Verification — real proof, not just build/test

- **Live proof, required**: boot a real server configured for generation
  (no real map file), confirm it generates a real `SingleRing` mission —
  real sector count matching `MissionParams`-derived team/neutral counts,
  real ring warp topology (each cluster connects to exactly its two ring
  neighbors), real home stations at team clusters, real asteroids at
  populated clusters. Connect a real client, confirm it can fly, warp
  between generated sectors along the real ring, and dock at a generated
  home station.
- **Regression, required**: every existing config that names a real map
  file still boots and behaves byte-for-byte identically — this is a
  pure-addition feature.
- Whole-solution build: 0 warnings, 0 errors.
- Full test suite, run at least 2x: only the pre-existing documented
  live-wire timing flakes expected.

## Deliverables

1. The foundation + `SingleRing` implementation, committed on `master`
   (not pushed — Domingo pushes).
2. Report doc `P397_MAPGEN_FOUNDATION.md` in the repo root: the
   `Mission`/`Sector` architecture decision and why, the real API shape
   (class/method names) so follow-up topology tasks can be scoped
   precisely against what actually exists, the `PopulateCluster`
   trace, and the live verification performed.

# P398 — procedural map generation: ring-family topologies (task 2 of ~5)

## Why this task exists

P397 landed the shared procedural-mission-generation foundation and exactly
one topology (`SingleRing`) as a working, live-proven end-to-end slice —
see `P397_MAPGEN_FOUNDATION.md` in the repo root for the full design and
API surface; **read it before starting**, it is the ground truth for what
already exists. This task adds the next 4 registered native map types —
`DoubleRing`, `BigRing`, `HiLo`, `HiHigher` — plus **2 more that come free**:
`LargeSplit` and `InsideOut` are native subclasses of `CmapMakerHiHigherIGC`
with **zero method overrides** beyond constructor/`IsValid`
(`mapmakerigc.h:523,531`) — once `HiHigher`'s real layout/link logic is
ported, register `LargeSplit`/`InsideOut` as additional `MapGenType` enum
values that dispatch to the exact same code path. Confirm this "zero
overrides" claim yourself against native before relying on it — don't take
it purely on this brief's word.

## Confirmed API to target (from P397, already landed — reuse, don't rebuild)

- `src/Allegiance.Core/MapGen/MapGenType.cs` — currently
  `enum MapGenType { SingleRing }`. Add `DoubleRing, BigRing, HiLo,
  HiHigher, LargeSplit, InsideOut`.
- `src/Allegiance.Core/MapGen/MissionMapGenerator.cs` — `public static
  MissionMap Generate(MapGenParams pmp, StaticCore core, double now)`,
  currently dispatching to SingleRing's hardcoded layout/link logic. **P397's
  own report states explicitly**: "the port has no virtual dispatch — a
  future task swaps in the layout/link logic for its topology." This task
  is that future task: introduce whatever real per-topology dispatch shape
  is cleanest given what's actually there now (a switch over `MapGenType`
  calling topology-specific private methods, e.g. `LinkClustersDoubleRing`/
  `LinkClustersHiHigher` alongside the existing SingleRing-shaped ones, is
  the most likely fit given P397's stated pattern — but read the real
  current code and decide, don't assume this brief's guess is exactly
  right).
- The shared primitives P397 already built and proved live — do not
  reimplement: `GenerateRingClusters`, `GenerateTeamCluster`/
  `GenerateNeutralCluster`, the 3-arg `LinkClusters(short, short, int)`
  warp-pair builder (native `mapmakerigc.cpp:705-835` — builds a
  bidirectional warp pair with **consecutive global warp ids**, which is
  what makes `Mission.Assemble`'s destination-resolution work; every new
  topology's linking code must keep using this exact primitive, never
  hand-roll a warp pair), `CrossLinkClusters`, `GenerateStarbase`,
  `PopulateClusters`/`PopulateCluster`/`AddAsteroid`, `PlaceWarps`.
- `MissionMap.FromLists(...)` (`MissionMap.cs:58`) — the assembly seam;
  nothing about it needs to change for a new topology.

## Confirmed native mechanics — exact citations, trace each yourself

All four in `~/Development/Allegiance/src/Igc/mapmakerigc.h` (class
declarations) and `mapmakerigc.cpp` (implementations):

- **`DoubleRing`** — `mapmakerigc.h:282`, impl
  `mapmakerigc.cpp:1076-1159` (`GenerateTeamClusterScreenPosition:1082`,
  `GenerateNeutralClusterScreenPosition:1093`, `LinkClusters:1107-1158`).
  Overrides `LinkClusters` only (no `GenerateLayout` override — uses the
  base team/neutral cluster counts).
- **`BigRing`** — `mapmakerigc.h:384`, impl `mapmakerigc.cpp:1160-1245`
  (`GenerateLayout:1166-1181`, screen positions `1182-1214`,
  `LinkClusters:1215-1244`). Overrides `GenerateLayout` too — check what it
  changes about cluster counts vs. the base.
- **`HiLo`** — `mapmakerigc.h:307`, impl `mapmakerigc.cpp:1246-1373`
  (`GenerateLayout:1252-1269`, screen positions `1270-1327`,
  `LinkClusters:1328-1372`).
- **`HiHigher`** — `mapmakerigc.h:332`, impl `mapmakerigc.cpp:1374-...`
  (`GenerateLayout:1380-1401`, screen positions `1402-1482`,
  `LinkClusters:1483-...` — read to the end of the function, the exact end
  line wasn't captured in this session's citation pass, find it yourself).
- **`LargeSplit`** — `mapmakerigc.h:523` — confirm it's `class
  CmapMakerLargeSplitIGC : public CmapMakerHiHigherIGC` with no body beyond
  constructor/`IsValid` before treating it as free.
- **`InsideOut`** — `mapmakerigc.h:531` — same check as `LargeSplit`.

For each of the 4 real topologies, trace: does it override `GenerateLayout`
(cluster-count changes) and/or `LinkClusters` (topology-specific linking)?
Screen-position overrides (`GenerateTeamClusterScreenPosition`/
`GenerateNeutralClusterScreenPosition`) are native's 2D map-editor cosmetic
placement — **out of scope**, same as P397's own stated simplification
(this port has no map-editor screen; `PlaceWarps`' deterministic ring
placement already covers the functionally-required warp world position).

## Scope

1. Trace each of the 4 real topologies' real `GenerateLayout`/
   `LinkClusters` C++ logic (cited above) and port each faithfully, reusing
   P397's shared primitives exactly as SingleRing does.
2. Introduce the real per-topology dispatch in `MissionMapGenerator`
   (see the API section above — your judgment call on exact shape, guided
   by P397's stated intent).
3. Register `LargeSplit`/`InsideOut` as thin aliases dispatching to
   `HiHigher`'s logic, once confirmed to genuinely have zero native
   overrides.
4. Extend `MapGenConfig`/`ToCoreParams` (`ServerConfig.cs`/
   `Server.Lifecycle.cs`, per P397) so all 6 new enum values are selectable
   from server config, matching the existing `SingleRing` wiring exactly.

## Explicitly out of scope

- Screen-position/map-editor cosmetics — see above.
- Any other topology family (Star/PinWheel/DiamondRing/SnowFlake,
  Grid/EastWest/SplitBase, Brawl) — separate follow-up tasks.
- Treasure generation — P397 documented this as deferred pending a real
  generated-treasure injection seam; don't build that here unless it's
  trivially in scope, and if you do, say so plainly rather than silently
  expanding scope.
- P368-P397 — unrelated, don't touch.

## Verification — real proof, not just build/test

- **Structural, required, per topology** (mirror P397's
  `MapGenFoundationTests` shape exactly): correct sector count for the
  topology's real cluster-count formula, correct ring/link neighbor
  structure (whatever each topology's real linking algorithm actually
  produces — verify against the traced native logic, not a guess), home
  stations at team clusters only, minable asteroids present. A
  must-fail-if-removed proof for at least one new topology's `LinkClusters`
  port (same technique P397 used).
- **Live proof, required, at least one new topology** (mirror P397's
  `MapGenLiveProofTests`): a real bot connects, spawns in a generated
  sector, warps to a real linked neighbor, and a default-spawned peer docks
  at a generated home station — proving the new topology's generated map is
  genuinely playable, not just structurally correct in isolation.
- **Regression, required**: `SingleRing` (P397) and every real map-file
  config remain byte-for-byte unaffected by whatever dispatch refactor this
  task introduces.
- Whole-solution build: 0 warnings, 0 errors.
- Full test suite, run at least 2x: only the pre-existing documented
  live-wire timing flakes expected.

## Deliverables

1. The 6 new topologies, committed on `master` (not pushed — Domingo
   pushes).
2. Report doc `P398_MAPGEN_RING_FAMILY.md` in the repo root: the dispatch
   refactor shape chosen, each topology's traced algorithm and any
   deliberate simplification, confirmation of the `LargeSplit`/`InsideOut`
   free-reuse claim, and the verification performed.

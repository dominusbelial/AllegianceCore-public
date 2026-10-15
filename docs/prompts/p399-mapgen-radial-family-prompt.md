# P399 — procedural map generation: radial/spoke-family topologies (task 3 of ~5)

## Why this task exists

P397 (foundation + SingleRing) and P398 (ring family: DoubleRing, BigRing,
HiLo, HiHigher, LargeSplit, InsideOut) are landed — **read both
`P397_MAPGEN_FOUNDATION.md` and `P398_MAPGEN_RING_FAMILY.md` in the repo
root before starting**, they are the ground truth for the real API surface
and the per-topology dispatch shape P398 already established in
`MissionMapGenerator`. This task adds 4 more registered native map types
with genuinely distinct linking algorithms: `Star`, `PinWheel`,
`DiamondRing`, `SnowFlake`. That's 11 of 15 topologies done once this
lands.

## Confirmed API to target (from P397/P398, already landed — reuse, don't rebuild)

- `src/Allegiance.Core/MapGen/MapGenType.cs` — add `Star, PinWheel,
  DiamondRing, SnowFlake` to the existing enum (native `c_mm*` numeric
  values — check `mapmakerigc.h`'s enum for the real values, matching
  P398's own convention of preserving native's numeric ids).
- `src/Allegiance.Core/MapGen/MissionMapGenerator.cs` — P398 already
  established the real per-topology dispatch shape (`GenerateLayout()`
  routing to `LinkClusters<Topology>` methods, per its own report). Follow
  that exact established pattern for these 4 — don't invent a third
  dispatch shape.
- Shared primitives from P397 (unchanged, reuse exactly): the 3-arg
  `LinkClusters(short, short, int)` consecutive-warp-pair builder,
  `GenerateStarbase`, `PopulateClusters`/`PopulateCluster`/`AddAsteroid`,
  `PlaceWarps`.

## Confirmed native mechanics — exact citations, trace each yourself

All in `~/Development/Allegiance/src/Igc/mapmakerigc.h` (declarations) /
`mapmakerigc.cpp` (implementations):

- **`Star`** — `mapmakerigc.h:357`, impl `mapmakerigc.cpp:1549-1802`
  (`GenerateLayout:1555-1570`, screen positions `1571-1630`,
  `LinkClusters:1631-...`).
- **`PinWheel`** — `mapmakerigc.h:439`, impl `mapmakerigc.cpp:1803-1922`
  (`IsValid:1810-1820` — has its own validity check beyond the base, check
  what it requires; `GenerateLayout:1821-1840`, screen position
  `1841-1858`, `LinkClusters:1859-1900`, **and a `PopulateCluster`
  override at `1901-1922`** — this is a real deviation from every topology
  ported so far (P397/P398's topologies only override layout/linking).
  Trace exactly what PinWheel's `PopulateCluster` does differently from
  the shared/base `PopulateCluster` P397 already ported, and port that
  real difference — don't silently reuse the shared one if PinWheel
  genuinely changes something.
- **`DiamondRing`** — `mapmakerigc.h:465`, impl
  `mapmakerigc.cpp:1923-1955+` (`IsValid:1930-1939` — also has its own
  validity check, `GenerateLayout:1940-1955`, screen position
  `1956-...`, find `LinkClusters` yourself — not fully cited in this
  session's research pass).
- **`SnowFlake`** — `mapmakerigc.h:493`, impl `mapmakerigc.cpp:2054-2243`
  (`GenerateLayout:2061-2077`, screen positions `2078-2137`,
  `LinkClusters:2138-2243`). This session's earlier research already
  characterized this one at a high level: "triangle-per-team neutral
  clusters cross-connected into a ring" — verify that characterization
  against the real code rather than trusting it blindly, it was a
  research summary, not a line-by-line port-ready trace.

Screen-position overrides remain out of scope for all four (P397's
established simplification — no map-editor screen in this port).

## Scope

1. Trace and port each of the 4 topologies' real `GenerateLayout`/
   `LinkClusters` logic, reusing P397's shared primitives exactly.
2. Port PinWheel's real `PopulateCluster` override faithfully — this is
   the one genuinely new piece of scope beyond "another layout/linking
   variant."
3. Confirm and port `PinWheel`'s and `DiamondRing`'s own extra `IsValid`
   checks (both have one beyond the base class's, per the citations above)
   — a config requesting one of these topologies with parameters that
   fail its real native validity check should be rejected the same way
   native would, not silently accepted.
4. Wire all 4 into `MapGenConfig`/`ToCoreParams` exactly as P398 did for
   its 6.

## Explicitly out of scope

- Screen-position/map-editor cosmetics.
- The remaining topology families (Grid/EastWest/SplitBase, Brawl) —
  separate follow-up tasks.
- Treasure generation — still deferred per P397, don't build it here
  unless trivially in scope (and say so plainly if you do).
- P368-P398 — unrelated, don't touch.

## Verification — real proof, not just build/test

- **Structural, required, per topology**: mirror P398's proof shape
  exactly — correct sector count, correct traced-native warp/link
  structure (verified against an independent read of native, not
  self-referential), home stations at team clusters, minable asteroids
  present. A must-fail-if-removed proof for at least one new topology's
  `LinkClusters` port.
- **PinWheel's `PopulateCluster` override, required**: a dedicated test
  proving its real difference from the shared/base population logic
  actually takes effect (whatever that difference turns out to be).
- **`IsValid` checks, required**: a test proving PinWheel's and
  DiamondRing's own extra validity conditions are actually enforced (a
  config that fails them is rejected, one that passes them succeeds).
- **Live proof, required, at least one new topology**: mirror P397/P398's
  live-bot-transit + dock proof.
- **Regression, required**: every prior topology (SingleRing + the 6 ring
  family) and every real map-file config remain unaffected.
- Whole-solution build: 0 warnings, 0 errors.
- Full test suite, run at least 2x: only the pre-existing documented
  live-wire timing flakes expected.

## Deliverables

1. The 4 new topologies, committed on `master` (not pushed — Domingo
   pushes).
2. Report doc `P399_MAPGEN_RADIAL_FAMILY.md` in the repo root: each
   topology's traced algorithm, PinWheel's `PopulateCluster` difference
   explained, the two extra `IsValid` checks, and the verification
   performed.

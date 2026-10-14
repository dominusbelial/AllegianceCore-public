# P400 — procedural map generation: grid/geographic-family topologies (task 4 of ~5)

## Why this task exists

P397 (foundation + SingleRing), P398 (ring family, 6 topologies), and P399
(radial family, 4 topologies) are landed — **read all three reports
(`P397_MAPGEN_FOUNDATION.md`, `P398_MAPGEN_RING_FAMILY.md`,
`P399_MAPGEN_RADIAL_FAMILY.md`, all in the repo root) before starting**,
they are the ground truth for the real API and dispatch shape. 11 of 15
native map types are done. This task adds `Grid`, `EastWest`,
`SplitBase` — 14 of 15 done once this lands. Only `Brawl` remains after
this (a separate, final task — it's structurally the odd one out, a
single shared sector rather than a multi-cluster layout, per this
session's original research).

**Important lesson from P399, apply it here too**: don't assume a class's
own `IsValid`/override is a real, currently-enforced constraint just
because it exists in the source — P399 found PinWheel's own `IsValid` was
a vestigial NULL stub (its real check was commented out in 2006), while
DiamondRing's was genuinely real. Verify each one against the actual
current code before porting it as a real constraint.

## Confirmed API to target (from P397/P398/P399, already landed)

- `src/Allegiance.Core/MapGen/MapGenType.cs` — add `Grid, EastWest,
  SplitBase` to the existing enum (native `c_mm*` numeric values).
- `src/Allegiance.Core/MapGen/MissionMapGenerator.cs` — follow the exact
  per-topology dispatch shape P398/P399 already established (`GenerateLayout()`
  routing to `LinkClusters<Topology>` methods). Don't invent a fourth
  dispatch shape.
- Shared primitives from P397 (reuse exactly): the 3-arg
  `LinkClusters(short, short, int)` consecutive-warp-pair builder,
  `GenerateStarbase`, `PopulateClusters`/`PopulateCluster`/`AddAsteroid`,
  `PlaceWarps`. P399 already showed how to layer a topology-specific
  `PopulateCluster` multiplier on top of the shared one (PinWheel's
  centre-doubling) — EastWest needs the same treatment for its own real
  override (see below).

## Confirmed native mechanics — exact citations, trace each yourself

All in `~/Development/Allegiance/src/Igc/mapmakerigc.h` (declarations) /
`mapmakerigc.cpp` (implementations):

- **`Grid`** — `mapmakerigc.h:542`, impl `mapmakerigc.cpp:2265-2415`
  (`GenerateLayout:2272-2289`, screen positions `2290-2363`,
  `LinkClusters:2364-2415`).
- **`EastWest`** — `mapmakerigc.h:564`, impl `mapmakerigc.cpp:2416-2554`
  (`IsValid:2416-2428` — verify whether this is a real, currently-enforced
  constraint or another vestigial stub before treating it as real;
  `GenerateLayout:2436-2451`, screen positions `2452-2488`,
  `LinkClusters:2489-2540`, **and a `PopulateCluster` override at
  `2541-2554`** — trace exactly what it changes from the shared/base
  population logic, same treatment P399 gave PinWheel's override).
- **`SplitBase`** — `mapmakerigc.h:586`, impl `mapmakerigc.cpp:2555-...`
  (`IsValid:2555-2563` — same verify-before-trusting treatment;
  `GenerateLayout:2569-2581`, screen position `2582-2605`,
  `LinkClusters:2606-...` — find the real end of the function yourself,
  not fully cited in this session's research pass).

Screen-position overrides remain out of scope for all three (the
established simplification carried through every prior task — no
map-editor screen in this port).

## Scope

1. Trace and port each of the 3 topologies' real `GenerateLayout`/
   `LinkClusters` logic, reusing the shared primitives exactly.
2. Port EastWest's real `PopulateCluster` override faithfully (mirror how
   P399 layered PinWheel's multiplier onto the shared `PopulateCluster`).
3. Verify EastWest's and SplitBase's own `IsValid` checks against the
   actual current native source — port whichever are real, document
   plainly (not silently) whichever turn out to be vestigial stubs like
   PinWheel's was.
4. Wire all 3 into `MapGenConfig`/`ToCoreParams` exactly as the prior
   tasks did.

## Explicitly out of scope

- Screen-position/map-editor cosmetics.
- `Brawl` — the final, separate follow-up task (structurally the odd one
  out: a single shared sector, not a multi-cluster layout).
- Treasure generation — still deferred per P397, don't build it here
  unless trivially in scope (and say so plainly if you do).
- P368-P399 — unrelated, don't touch.

## Verification — real proof, not just build/test

- **Structural, required, per topology**: mirror P398/P399's proof shape
  exactly — correct sector count, correct traced-native warp/link
  structure (independently re-derived against native, not
  self-referential — P399's Python-simulation technique is a good model
  to follow), home stations at team clusters, minable asteroids present.
  A must-fail-if-removed proof for at least one new topology's
  `LinkClusters` port.
- **EastWest's `PopulateCluster` override, required**: a dedicated test
  proving its real difference from the shared population logic actually
  takes effect.
- **`IsValid` checks**: for whichever of EastWest's/SplitBase's own checks
  turn out to be real (not vestigial), a test proving a config that fails
  it is rejected and one that passes it succeeds.
- **Live proof, required, at least one new topology**: mirror the prior
  tasks' live-bot-transit + dock proof.
- **Regression, required**: all 11 prior topologies and every real
  map-file config remain unaffected.
- Whole-solution build: 0 warnings, 0 errors.
- Full test suite, run at least 2x: only the pre-existing documented
  live-wire timing flakes expected.

## Deliverables

1. The 3 new topologies, committed on `master` (not pushed — Domingo
   pushes).
2. Report doc `P400_MAPGEN_GRID_FAMILY.md` in the repo root: each
   topology's traced algorithm, EastWest's `PopulateCluster` difference
   explained, the real-vs-vestigial verdict on both `IsValid` checks, and
   the verification performed.

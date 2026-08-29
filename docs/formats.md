# Formats

Extracted from README.md 2026-08-29 (it had reached 1,096 lines).

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

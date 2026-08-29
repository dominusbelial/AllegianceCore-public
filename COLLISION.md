# Collision detection — tracing the C++ reference

Answers traced against `src/_Utility/HitTest.cpp`, `src/_Utility/CollisionQueue.cpp`,
`src/_Utility/CollisionEntry.cpp`, `src/_Utility/KDroot.cpp`, `src/_Utility/KDnode.cpp`,
`src/_Utility/Utility.h`, and `src/Igc/clusterIGC.cpp` (the collision section of
`CclusterIGC::Update`). Where the wiki/summary and the C++ disagree, **the C++ wins**.

---

## Q1 — Broad phase: it is a KD-tree, not "sweep-and-prune with KD-tree sectors"

The wiki's "sweep-and-prune with KD-tree sectors" is **incorrect/imprecise and is
corrected here**. The broad phase is a **KD-tree space subdivision**
(`KDroot`/`KDnode`), and sweep-and-prune plays only the supporting role of keeping
the root's per-axis **endpoint lists sorted** — which is how the KD tree decides its
pivot planes, not how candidate pairs are found.

* `CclusterIGC` owns **two** KD trees, one for static objects and one for moving
  objects: `m_kdrStatic` / `m_kdrMoving` (`clusterIGC.h:33-34`).
  `AddModel` places each new hit-test into the tree selected by the model's
  `c_mtStatic` attribute (`clusterIGC.cpp:526-531`):
  ```cpp
  ((mt & c_mtStatic) ? m_kdrStatic : m_kdrMoving).addHitTest(ht);
  ```
* `KDroot::addHitTest` registers the test's per-axis endpoints
  (`KDroot.cpp:94-103`) and bumps a "changed" counter (`KDroot.cpp:108`) that
  decides, on the next `update()`, whether to long-sort or short-sort the endpoint
  lists (`KDroot.cpp:171-196`) — this is the sweep-and-prune bit. Then `pivot()`
  recursively builds the tree by splitting on the median endpoint of alternating
  axes (`KDnode.cpp:238-298`).
* Broad-phase pruning proper is **tree descent**: `KDnode::test` walks a query
  object's swept bounding-box interval into whichever children the interval
  straddles (`KDnode.cpp:300-312`), reaching terminal (leaf) nodes where it does a
  **pairwise axis-aligned bounding-box overlap check** against each stored test
  (`KDnode.cpp:317-329`). Only overlapping leaf candidates are forwarded to the
  narrow phase via `pHitTest1->Collide(pHitTest2, pQueue)` (`KDnode.cpp:328`).

The swept AABB itself is computed in `HitTest::SetBB`/`UpdateBB`
(`HitTest.cpp:14-110`): start-to-stop swept volume expanded by `m_radius` on every
axis — the "ray" cast by each `c_mtCastRay` model (`clusterIGC.cpp:272-287`)
through both trees.

**Net:** the pair-generation predicate is a swept-AABB overlap, and the KD tree only
accelerates reaching those pairs; it never changes *which* AABB-overlapping pairs are
presented to the narrow phase. A broad phase that yields exactly the swept-AABB-
overlapping pairs reproduces the reference's candidate set. That is what the C# port
does (deterministically), while COLLISION.md documents the structural difference.

References: `clusterIGC.cpp:528-530`, `KDroot.cpp:85-108,166-204`,
`KDnode.cpp:238-298,300-332`, `HitTest.cpp:14-110`.

---

## Q2 — Narrow phase: hulls enter via `BoundingHull`; `radius`/`ee`/`erm` explained

The narrow phase is `HitTest::Collide` (`HitTest.cpp:114-206`). After the AABB reject
it solves a **closed-form sphere-sweep closest-approach quadratic** for the first
contact time `tCollision` and horizon `tMax` (`HitTest.cpp:128-205`). If both shapes
are simple (`≤ c_htsSphere`), that analytic time is final; otherwise it calls
`HullCollide` → `IntervalCollide` → `DoGilbert` (GJK):

* `HullCollide` (`HitTest.cpp:921-1043`) iterates every sub-hull of A against every
  sub-hull of B (`do … while (++htsAcurrent < htsA)`), picking the earliest.
* `IntervalCollide` (`HitTest.cpp:1045-1136`) recursively bisects the time interval;
  at each sample it looks for a separating plane by comparing support points
  (`GetMinExtreme`/`GetMaxExtreme`), reflecting the direction up to 4 times, then
  falling back to `DoGilbert`.
* `DoGilbert` (`HitTest.cpp:1858-1948`) is GJK over the Minkowski difference using
  Johnson's simplex closest-point routines (`HitTest.cpp:1226-1856`).

The `.cvh` convex hulls enter here: `HitTest::Create(fileName, …)`
(`HitTest.cpp:827-863`) loads the cached `MultiHull` via `HitTest::Load`
(`HitTest.cpp:639-825`) and constructs a **`BoundingHull`** (`HitTest.cpp:435-615`)
whose shape index *is* the sub-hull selector (`c_htsConvexHullMin=0 …
c_htsConvexHullMax=127`, `Utility.h:691-701`). So for a hull-bodied model,
`ShapeTrue == nHulls` and each concrete test shape `0…nHulls-1` names one sub-hull.

`radius`, `ee`, `erm` (`CvhFile.Radius/EllipseEquation/EllipseRadiusMultiplier`):

* **`radius`** is `m_originalRadius`, the bounding-sphere base. It is **not** a tight
  enclosure (README: vertex-distance/radius ratio spans 0.51…1.55). It enters narrow
  phase as the scale denominator: `m_scale = m_desiredRadius / m_originalRadius`
  (`HitTest.cpp:571`), and every hull vertex read is multiplied by `m_scale`
  (`GetHullExtreme`, `HitTest.cpp:546`). With `desiredRadius == originalRadius`,
  `m_scale == 1` and vertices are used at file scale.
* **`ee`** (ellipse equation) is scaled by `m_scale` (`m_ellipseEquation =
  m_scale·ee`, `HitTest.cpp:572`) and used only by the `c_htsEllipse` shape: the
  analytic ellipse support `GetEllipseExtreme` (`HitTest.cpp:467-492`) and the
  point-vs-ellipse branch (`HitTest.cpp:939-997`).
* **`erm`** (ellipse radius multiplier) inflates the ellipse's bounding sphere:
  `m_radius = m_desiredRadius · erm` when the shape is ellipse
  (`HitTest.cpp:562,578`). Both `ee` and `erm` therefore feed the
  `HitTestShape` selection, not the hull-vs-hull GJK path.

References: `HitTest.cpp:114-206,435-615,639-863,921-1136,1858-1948`,
`Utility.h:688-701,706-761`.

---

## Q3 — Collision time and the ordering key

Two time quantities, distinct:

1. **`m_tCollision` (per pair)** — the first-contact time of the *pair*, relative to
   the start of the step. For a sphere-sweep it is the analytic first root of the
   closest-approach quadratic (`HitTest.cpp:155`; `0` if the spheres already overlap,
   `HitTest.cpp:155`); for a hull pair it is the same seed further tightened by
   `IntervalCollide`'s bisection, which snaps `tCollision` to the interval midpoint
   once the bracket is below `maxDeltaT` (`HitTest.cpp:1127-1132`). `tMax` is the
   closing horizon, clamped to `m_tStop` (`HitTest.cpp:159-160,182-183`).

2. **`m_tOffset` (per step, cumulative)** — `RecalculateCollisions` adds the time an
   in-progress collision has consumed (`m_tOffset += tOffset`, `clusterIGC.cpp:624`),
   shifting the *remaining* entries' absolute times. So an entry's absolute sim-time is
   `timeCollision = m_lastUpdate + (m_tOffset + entry.m_tCollision)`
   (`clusterIGC.cpp:302`).

The **ordering key** is `entry.m_tCollision` itself: `CollisionEntry::comp` compares
solely on `m_tCollision` (`CollisionEntry.cpp:31-39`), `CollisionQueue::sort(0)`
sorts the whole queue ascending (`CollisionQueue.cpp:40-55`), and the resolution loop
walks them in that order (`clusterIGC.cpp:293-318`), re-sorting a suffix after each
handle/`flush` (`clusterIGC.cpp:651-703`). `m_tOffset` is applied *after* the sort —
it staggers absolute times but does not re-order within an unaffected suffix.

References: `HitTest.cpp:143-205,1127-1132`, `CollisionEntry.cpp:31-39,42-192`,
`CollisionQueue.cpp:40-55`, `clusterIGC.cpp:295-318,592-705`.

---

## Q4 — Detection is per `Update` (one gather per sector step), not per-collision

Detection runs **once per `CclusterIGC::Update`** (`clusterIGC.cpp:272-321`): every
`c_mtCastRay` model is tested against both KD trees into the shared `m_collisions`
queue, then the queue is sorted and resolved — all within one sector tick. There is no
per-sub-sweep or per-collision-independent detection pass; the single gather covers
the whole step. (Mid-resolution, `RecalculateCollisions` re-tests only the two objects
that just collided against the rest and appends new candidates, `clusterIGC.cpp:
650-703` — an incremental repair, not a second full sweep.)

The `activeF && bStarted` gate (`clusterIGC.cpp:255`) plus the per-model
`SetBB(m_lastUpdate, now, dt)` call (`clusterIGC.cpp:263`) establish the
step-sized swept volumes that the tree descends.

References: `clusterIGC.cpp:255-288,290-321,650-703`.

---

## How this becomes the C# port

* **Broad phase** (separate piece): deterministic swept-AABB pair generation that
  yields exactly the AABB-overlapping candidate set the KD tree would (see Q1). The
  tree acceleration is noted as a structural difference, not reproduced bit-for-bit —
  pair content is identical.
* **Narrow phase** (separate piece): faithful port of `HitTest::Collide` —
  sphere-sweep quadratic seeded with `tCollision`/`tMax`, then
  `HullCollide`/`IntervalCollide`/GJK(`Johnson`) for hull pairs, using the parsed
  `.cvh` vertices and adjacency for the support functions, scaled by
  `m_scale = desiredRadius/originalRadius`.
* **Resolution**: a time-ordered queue (sorted by `m_tCollision`) walked in order;
  **report only** — no damage, no explosions, no destruction.
* **`Model` gains an optional hull** and exposes a `Collider` body built from its
  position/velocity/orientation; `Ship` supplies its velocity and orientation, a
  static station contributes a zero velocity and its station hull.

## Verification — implemented and passing

`CollisionDetectionTests` (9 tests, real corpus hulls `fig01.cvh`, `dgsup.cvh`,
`p1_exp.cvh`, which is byte-identical to `p1_sup.cvh`/`p1_tac.cvh` — the corpus
keeps one representative per content hash):

* **Head-on, analytic time.** Spheres at (±100,0,0) closing at 20 u/s collide at
  exactly `9.5 s` (surface gap 190/20). Asserted to 1e-2.
* **Head-on real hulls, analytic time.** Two `fig01` fighters head-on, centers at
  ±20, closing at 20 u/s. `fig01`'s leading vertex is `|x| = 1.936683`, so first
  contact is at `t = (40 − 2·1.936683)/20 = 1.80633 s`. The hull path refines the
  sphere seed by `IntervalCollide` bisection to `maxDeltaT = 0.25/20 = 0.0125 s`;
  asserted within 0.025. **Passes** — confirming the GJK/Johnson port lands on the
  true surface-contact time, not the sphere approximation (which would be 1.712 s).
* **Parallel non-intersecting courses** (two `fig01` flying `+x`, z-separated by 8 >
  hull z-extent sum 2·2.644) → no event.
* **Near-miss crossing** (perpendicular spheres, closest approach 12.7 > 10): the
  broad phase yields a candidate (swept AABBs overlap) and the narrow phase rejects
  it (no real closest-approach root) — asserted both.
* **Ship → station hull** (`fig01` into static `dgsup`, radius 48.9) collides; time in
  (0, 10 s].
* **Multi-hull** (`p1_exp`, 6 sub-hulls) vs `fig01` collides, exercising the sub-hull
  iteration loop of `HullCollide`.
* **Determinism**: identical setup detected twice (and a fresh detector) → identical
  list (times equal, same collider instances).
* **Ordering**: the returned list is ascending by collision time, with distinct values.

Full suite stays green: **1444/1444** (1435 pre-existing + 9 collision).

Structural deviations from the C++ (deliberate, recorded here): the broad phase is a
deterministic pair-wise swept-AABB test rather than a KD tree — it produces the same
candidate set (Q1); cone shapes are unimplemented (no corpus consumer); collision
<b>reporting only</b> — resolution does not apply damage/movement (out of scope).

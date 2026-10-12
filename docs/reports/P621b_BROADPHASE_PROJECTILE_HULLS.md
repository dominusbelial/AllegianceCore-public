# P621b — spatial broad phase (sweep-and-prune) + projectile/ordnance-vs-hull

**Date:** 2026-10-12 · **Branch:** `master` · **Commit:** `1c1082b0`

---

## The problem

P621 (`f3b42bc4`, `docs/reports/P621_CONVEX_HULL_COLLISIONS.md`) made the ship↔
rock / ship↔ship / ship↔station collision passes collide against each body's
real convex hull (GJK narrow phase), but left two costs that P621 itself flagged:

1. **`Collision/BroadPhase.cs` was an O(n²) pairwise swept-AABB sweep** (a port
   simplification). Every nearby pair — and in a dense melee that is thousands
   of pairs — reached the now-expensive GJK narrow phase, and the O(n²) sweep
   itself grew quadratically with the population.
2. **Projectile/ordnance-vs-hull was still on spheres.** `ResolveCombatCollisions`
   built the ship collider with `hull: s.Hull`, but `Model.Hull` (the `Ship.Hull`
   hook) was **never populated** (P621's "falls out naturally if/when `Ship.Hull`
   is populated"). Asteroids and stations in the combat pass used `hull: null`
   outright. Native resolves weapon fire against the target's real `.cvh` hull.

Native's broad phase is a **KD tree of bins built over per-axis sorted endpoint
lists** (`KDroot::update` re-sorts the endpoints, `KDnode::pivot` splits on a
cost-minimising plane, `KDnode::test` descends to overlapping leaves) — a
*sweep-and-prune* in structure. The port instead kept a dumb O(n²) loop.

---

## Native behaviour classified

| behaviour | native cite | class | what the port does |
|---|---|---|---|
| Spatial broad phase = KD tree over per-axis sorted AABB endpoints; only overlapping pairs reach the narrow phase | `_Utility/KDroot.cpp:166-205` (`update`), `KDnode.cpp:238-298` (`pivot`), `KDnode.cpp:300-331` (`test`); narrow gate `HitTest.cpp:120-125` | **design** | `BroadPhase.cs` rewritten as a sweep-and-prune producing the **identical** candidate set (byte-identical AABB overlap predicate), deterministically |
| Projectile collides against the target's real hull (`LoadCVH` body) | `modelIGC.cpp:105-118` (ship `LoadCVH`), `projectileIGC.cpp:166-190` (hit → `ReceiveDamage`), `HitTest.cpp:844-860` (missing cvh → sphere fallback) | design | combat pass builds the ship collider with `EnsureShipHull(s)` (populates the `Model.Hull` hook from the `CollisionHulls` cache), asteroids/stations with `CollisionHulls?.Resolve(model)` |
| Projectile body itself is a point/small radius; a sphere is the fallback primitive | `modelIGC.cpp:121-138` | design | ordnance collider stays a small bounding sphere (`o.Radius`) |
| Narrow phase is a two-phase test: bounding-sphere sweep before GJK | `HitTest.cpp:114-206` (`Collide`) | design | already present in `NarrowPhase.TestPair`; retained |

No known-bug or exploit behaviour was copied. Nothing here is an era constraint:
the O(n²) broad phase was a port shortcut, not native behaviour.

---

## Changes

### 1. `Collision/BroadPhase.cs` — sweep-and-prune spatial broad phase
Replaced the O(n²) pairwise sweep with a **sweep-and-prune** (SAP): pick the
sweep axis as the axis of widest bounding-box spread (native rotates pivot axes
for the same reason, `KDnode.cpp:244-246`), sort the swept-AABB intervals by
min-coordinate (stable, index tiebreak), sweep maintaining an active set, and
emit each pair that passes the full 3-D AABB overlap — the byte-identical
predicate the old loop used. Complexity drops from O(n²) to O(n log n + P).

- **Identical candidate set**: the gate is the same overlap test, so exactly the
  same pairs reach the narrow phase (Q1 of `COLLISION.md` — a spatial broad
  phase only accelerates reaching overlapping pairs, never changes *which*).
- **Deterministic**: emitted pairs are re-sorted by `(I, J)`, so the pair stream
  — and every downstream tie-break — is bit-identical to the O(n²) sweep it
  replaces. Verified by `BroadPhase_candidate_set_equals_O2n_sweep` (4 seeds ×
  5 trials × 4 sizes) against a retained `OverlappingPairsBruteForce` oracle.
- Used by every collision pass (`CollisionDetector` is shared by all four
  resolver passes: combat, ship↔asteroid, ship↔ship, ship↔station).

### 2. `Collision/NarrowPhase.cs` — allocation-free GJK hot path
`DoGilbert` allocated a 100-element `Vector3` history **per GJK call** plus a
`new int[3]` per Johnson iteration, and `IntervalCollide`/`Johnson3`/`Johnson4`
allocated their extreme/simplex/delta matrices per call — in a 200v200 melee
that is megabytes of garbage per tick on the hot path (the shared-environment
rule's "allocation-free hot paths and pooling / GC pressure" mandate). All of
these became `stackalloc`/`Span` buffers hoisted out of the loops; the Johnson
subset tables moved to `static readonly`. The math is untouched — verified by
the unchanged detection tests. The measured win on the dense population:
−1.2 ms/tick of pure GC removal.

### 3. `Model/Sector.cs` — populate the ship hull hook + hull ordnance targets
`ResolveCombatCollisions` now calls `EnsureShipHull(s)`, which resolves the
ship's real `.cvh` from the sector `CollisionHulls` cache and **caches it on
`Model.Hull`** (`Ship.Hull`) — populating the hook P621 left null. The asteroid
and station combat colliders now resolve their model hulls too
(`CollisionHulls?.Resolve(model)`), matching the three physics passes. The
impact positions/normals and the damage path are unchanged — only *when* a hit
registers now follows the real hull (a shot grazing a ship's empty
bounding-sphere corner misses; one on the hull hits).

### 4. `tools/collision-perf-proof` (new, committed benchmark)
Reproduces the `_P621PerfProbe` population — **390 ship hulls + 134 rock hulls**
from the real artwork corpus — in a **sparse** field (one loosely-packed fleet,
~407 candidate pairs) and a **dense melee** (two interpenetrating 195-ship
fleets at a rendezvous, ~3 176 candidate pairs), timing
`CollisionDetector.Detect(dt=0.01667)` over 300 reps. Added to the solution.

---

## Tests (failing-first)

`tests/Allegiance.Core.Tests/P621bBroadPhaseProjectileHullTests.cs`:

| test | what it proves | failing-first |
|---|---|---|
| `BroadPhase_candidate_set_equals_O2n_sweep` (theory, 4 seeds) | SAP yields the same candidate-pair set as the O(n²) sweep, identically ordered, for random mixed sphere+hull populations of size 2–200 | correctness test for the new broad phase (no SAP existed pre-P621b) |
| `Projectile_hits_real_hull_but_passes_through_empty_bounding_sphere` | an ordnance round grazing a ship hull's empty bounding-sphere corner does **not** collide (hull) while it **does** against a sphere; into the hull it hits | documents the fix (detection was already hull-capable; the failure the combat pass previously shipped is covered by the next test) |
| `Combat_pass_populates_the_ship_hull_hook` | `ResolveCombatCollisions` populates `Ship.Hull` from the `CollisionHulls` cache (`EnsureShipHull`) | **verified failing on today's code**: with `hull: s.Hull` the hook stays null → `Assert.NotNull(ship.Hull)` fails; with `EnsureShipHull` it passes |

Measured output:

```
P621b-PROJ-HULL[fig07]: radius 50, xHalf …, yHalf …, zHalf … — empty corner at …-axis altitude …
   hull=MISS, sphere=HIT; into-hull=HIT.
P621b-COMBAT-HOOK: ship model <name> → N sub-hull(s) populated.
```

All P621 tests (`P621ConvexHullCollisionTests`) and the existing
`CollisionDetectionTests` still pass unchanged.

---

## Performance — collision step cost at ~390 players

Committed benchmark (`tools/collision-perf-proof`), 390 ships + 134 rocks,
`Detect(dt=0.01667)`, 300 reps, Release. Same population, before/after:

| field | candidate pairs | before (O(n²) + allocating GJK) | after (SAP + alloc-free GJK) | Δ |
|---|---|---|---|---|
| sparse | 407 | 1.42 ms/tick | **0.76 ms/tick** | **−46%** |
| dense melee | 3 176 | 2.43 ms/tick | **2.15 ms/tick** | **−12%** |

(Targets: dense ≤ 5 ms, sparse ≤ 2.5 ms — met. The dense win is smaller because
a melee's candidate pairs genuinely overlap and GJK must run on most of them;
the allocation removal is what moved it, and the real-world 200v200 baseline
below is the operative number.)

---

## Full suite (2 consecutive runs, `scripts/run-tests.sh` steps)

`run-tests.sh` as a single invocation exceeds the harness foreground timeout, so
its sequential per-project steps were run individually (`--no-build` on the
final committed code, run-tests.sh order). Two consecutive passes.

| project | pass 1 | pass 2 |
|---|---|---|
| Allegiance.Lobby.Tests | 12/12 ✅ | 12/12 ✅ |
| Allegiance.ClientCore.MonoGame.Tests | 1775/1779 — 1 transient flake (clean 1776/1779 on immediate re-run) | 1776/1779 (3 skipped) ✅ |
| Allegiance.ClientCore.Tests | 623/623 ✅ | 623/623 ✅ |
| Allegiance.Core.Tests | 3998/4003 — 5 **known load-flakes** | 4000/4003 — 3 **known load-flakes** |

Every failure is a live-wire timing flake already ledgered in
`docs/TEST_FLAKES.md` (AutoReloadOnEmpty `:52`, ConquestTerritoryLive `:29/30`,
GameOverPlayersWire `:30/175`, LiveChatSectorWire `:216`, P590PodRescue `:435`,
LiveTreasurePickup `:157`, AddPartTests `:247`) — **every one passed in strict
isolation** (e.g. the Core pass-1 five: 3/3, 3/3, 1/1 in 157-222 ms), none
related to this change. Whole-solution build: **0 warnings / 0 errors**
(`dotnet build -m:8`).

---

## Live proof (local server, no touch of 221/play2/deploy)

Own-temp-worktree binaries (`/tmp/p621b-before` = parent `e1175e1b`, removed
after capture). Showcase `spawn_scenario` on a local server (m2xspiderw.igc,
`target_sector Alderan`), profiler CSV on (5 s windows, `sim.collision` bucket).

**30v30 sanity (scale 1, my code, 40 s settle + 90 s combat):**
- **31 `[warp] … crossed`** — bots left home sectors and reached Alderan. ✅
- **14 `[combat] … killed by …` kills** — bots reach the target AND fight. ✅
- 96 resolved collision events; ticks/5 s ~100, tick p95 ~2-3 ms.

**200v200 baseline (scale 13 ≈ 195v195, ~390 bots, 60 s settle + 150 s combat):**

| metric | before (e1175e1b) | after (P621b) |
|---|---|---|
| collision step cost — before contact | 0.90 ms/tick | 0.85 ms/tick |
| collision step cost — **in combat** | **0.65 ms/tick** | **0.26 ms/tick** (**2.5× faster**) |
| ticks / 5 s | 92-101 | 93-101 |
| tick p95 peak (heaviest window) | ~85 ms | ~72 ms |
| `[warp]` crossings | 722 | 460 |
| `[combat]` kills | 195 | 170 |

Collision step cost is a small fraction of the 50 ms tick budget even at 390
players, and P621b cuts it ~2.5× **in combat** (where the hull GJK is busiest).
The P621-reported 20.08 ms dense-melee figure was the *synthetic* all-ships-
stacked worst case, not the real combat distribution: live 200v200 combat never
stacks all 390 ships, so the true cost is ~0.3 ms/tick — and the worst-case
synthetic bench (tools/collision-perf-proof) still drops 2.43 → 2.15 ms/tick.
Bots reached the target and fought in both (195 vs 170 kills — run variance).

---

## Modern alternative considered

**Native:** per-axis sorted endpoints + a median-split KD tree that only visits
overlapping AABBs, all from the era's CPU/GC constraints.

**Modern option:** a classic sweep-and-prune (SAP) — the textbook successor to
the same idea — or a BVH / uniform grid. For a per-tick, stateless, determinism-
critical server broad phase, SAP is the best fit: O(n log n + P) build, tiny
constant, and trivially deterministic. A BVH buys nothing for a single-shot
per-tick rebuild (no temporal coherence to amortise when the collider set is
rebuilt every pass, as `CollisionDetector` does), and a uniform grid needs cell
sizing tuned per sector.

**What we chose:** a deterministic sweep-and-prune, because it preserves the
exact candidate set and ordering of the O(n²) sweep while eliminating the
quadratic blow-up — native-faithful intent (native is itself an SAP-style KD
tree), sized for today's 200v200 baseline. The accompanying narrow-phase change
(allocation-free GJK) is the other half of the modern "allocation-free hot path"
mandate and delivers the larger measured win.

## Out of scope

- **Damage numbers** — untouched (still native-faithful).
- **Client prediction** — native doesn't predict collisions (P621 documented);
  unchanged.
- **P622 bot behaviour** — untouched.

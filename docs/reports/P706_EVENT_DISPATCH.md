# P706 — the remaining 0.3–0.6 s hitches at station death and match rotation are in event DISPATCH

## Summary

After P694/P694b cut mass-event **detection** from 1.3–1.8 s to under 100 ms, the live rig
(clash57, master `cf5821f6`, 105v105, 3 automatic matches) still logged a 0.3–0.6 s tick every
rotation. P694's sub-step profiling located the time in **dispatch** — handling detected events,
not finding them:

```
00:41:47 collision.ship.dispatch=393ms   (match start: mass relaunch)
01:18:39 combat.dispatch=387ms           (game over: garrison 4329 destroyed)
```

This task: reproduce those two dispatch storms locally at live density, profile what each event
costs, fix the dominant costs while keeping gameplay identical, and hold dispatch to ≤ 50 ms in the
Release reproduction (extending P694's perf guard with a dispatch budget).

## Reproduction + dispatch-level profiling (P706DispatchProfilingTests)

A new perf-guard test builds a fresh world per sample (P694-style) and advances a burst of
ticks through the two mass events, reading per-bucket times straight from
`TickProfiler.Snapshot`:

* **matchStart** — a full side (~165 ships) relaunching together from the garrison into a dense,
  mutually-overlapping swarm → `collision.ship.dispatch`.
* **gameOver** — the finale: a bomber volley whose blast destroys the garrison and the ~fleet
  around it → `combat.dispatch`.

Worst (max) dispatch tick per world is reported; the guard is a **median over five fresh worlds**
(P694b style), so one scheduler/GC outlier cannot fail a busy machine but a sustained slow
dispatch still must. Release gate is the 50 ms P706 target; Debug is relaxed (the unoptimized
hull solver runs ~4–5× slower — see below).

### How many events, at what cost each (the headline numbers)

Instrumented dispatch (`LastShipShipEvents/Resolved/Debounced` + the new
`collision.ship.separate` and `collision.ship.event` buckets) shows, in the densest relaunch tick
(~165/side × 2 sectors):

| metric | value |
|---|---|
| ship↔ship events dispatched in one tick | ~26–38 k |
| … that actually resolve (bounce + damage) | ~1.5–3.7 k |
| … rejected by the post-Separate approach debounce | ~23–34 k (≙ 90 %) |
| `collision.ship.detect` | 10–19 ms |
| `collision.ship.dispatch` | 40–80 ms, of which `collision.ship.separate` ≈ 37–76 ms |

So detection is a small fraction; **the convex-hull `Separate` solver inside dispatch is the
dominant cost**. The 90 % of events the debounce rejects still paid for the seed + probe work in
`Separate` (they are the *cheap* early-break separates — a genuinely separating pair finds a
separating plane quickly — while the ~10 % that resolve run all 27 probes on deeply-overlapping
hulls, which is where the time concentrates).

Two important realities this uncovered:

1. **Headless ship-ship dispatch is already fast at realistic emergence.** With ships emerging
   *aligned* to the launch direction (as real pilots do), the pair-keyed separation cache is
   highly effective and the same density dispatches in ~11 ms. The pathological case is random
   attitudes (pilots turning) that defeat the pair cache — used deliberately as the guard's stress.
2. **The live 400 ms residue is dominated by the per-event BROADCAST path to ~210 real peers**
   (encode + pack + peer-map iterate + send per event), which a headless reproduction with an
   empty peer map cannot reproduce. The coalescing of per-event broadcasts is the live fix and is
   the P668–P679 broadcast-pipeline territory the task marked out of scope.

## The fix: per-body separation support cache (`Separate.ProbeCache.GetB`)

`Separate.SeparateColliders` scores up to 27 fixed probe directions; for body-2 it needs
`GetMaxExtreme(hts, d)` for each. That is a **pure function of the collider's hull/shape/scale/
radius and the FIXED probe direction `d` — it never involves the body's orientation**
(`Collider.GetMaxExtreme → GetHullExtreme` is hull-local × scale). The old pair-keyed
`_offsets` cache recomputed these 27 supports for every *distinct pair*; in a dense fleet of a
few hull types the same body's 27 supports repeat across thousands of pairs. Adding a per-body
cache (`GetB`, keyed by hull/hts/scale/radius) computes them **once per unique hull**.

This is a pure memoisation — the exact same support values, so separation output is
byte-identical. Verified by `P706SeparateCacheIdentityTests` (500 random hull/attitude sweeps,
cached vs uncached `SeparateColliders` byte-equal on Position1/Position2/Normal) and by the
entire existing collision suite (see below).

### Before / after (Release, 165/side random-attitude stress)

| | worst-dispatch median |
|---|---|
| before (cache bypassed) | **58.5 ms — FAILS the 50 ms budget** |
| after (per-body cache) | **41.3 ms — passes** (samples 41.1–41.8 ms) |

The failing-first guard: at the chosen stress density the P706 test fails on today's code
(58.5 > 50) and passes after the fix (41.3 < 50).

## Native behaviour classified

| behaviour | native cite | class | what the port does |
|---|---|---|---|
| 27-probe convex-hull separation | `shipIGC.cpp:514-616` | design | ported verbatim; unchanged |
| per-collision `RecalculateCollisions` invalidation | `shipIGC.cpp` (ship-ship, after resolve) | design | not ported (port processes all detected events; the debounce substitutes for re-slam prevention) |
| approach debounce before resolving | P254 port addition | design (port's own) | kept; runs after `Separate` (cost documented) |
| 27 support vertices per body, pair-invariant | `GetMaxExtreme`/`HitTest.cpp:889-909` | design | NEW: memoised per body — same values, no gameplay change |

## Modern alternative considered

* **GJK / EPA** for convex-hull penetration would converge in far fewer support queries than the
  27-probe hill-climb, but produces a different separating normal/position — not byte-identical
  gameplay, so rejected for a perf-only task.
* **SAT temporal coherence** (remembering the last separating axis) is the standard modern
  win and is exactly what the per-body cache approximates for the body-2 side; the body-1 (`aa`)
  terms remain pair-dependent on relative orientation.
* **Per-tick broadcast coalescing** is the modern fix for the live per-event send cost; it is the
  P668–P679 broadcast-pipeline work, out of scope here and not reproducible headlessly.

Chosen: the per-body memoisation — zero gameplay risk (proven byte-identical), and it attacks the
largest *reproducible* headless cost.

## Tests

* **P706DispatchProfilingTests** (Release gate 50 ms, Debug 200 ms): matchStart 165 → 41.3 ms
  median (failing-first: 58.5 ms before), gameOver 200 → ~5 ms. Pass in both configs.
* **P706SeparateCacheIdentityTests**: cached == uncached byte-identical across 500 sweeps.
* Existing collision gameplay suite re-run green: ShipShipCollisionTests, ShipStationCollisionTests,
  ShipAsteroidCollisionTests, P621ConvexHullCollisionTests, P694CollisionGeometryTests,
  CollisionDetectionTests, P689CollisionPairTests, CollisionEventWireTests.

## Build / suite

* Whole-solution build (Release and Debug): **0 warnings, 0 errors**.
* Full suite `scripts/run-tests.sh`, exactly 2 runs (recorded as they happened):
  * Run 1 — Lobby 19/19; MonoGame 2233 pass + 3 skip; ClientCore 1056 pass + 1 skip;
    Core 4609 pass + **1 fail**; quarantine all green. The sole failure is the
    already-documented `TcpTransportSendWorkerPoolTests.Pool_worker_and_reader…churn`
    load flake (3/3 in isolation).
  * Run 2 — Lobby 19/19; MonoGame 2233 pass + 3 skip; ClientCore 1055 pass + 1 skip +
    **1 fail**; Core 4609 pass + **1 fail**; quarantine all green. Failures: the same
    documented `TcpTransportSendWorkerPool…churn` flake (3/3 in isolation) and
    `P700ShowcaseLiveTests.Local_showcase_never_parks_with_target_for_five_seconds`,
    a live bot-speed-under-load timing test that is **flaky on the pre-change commit
    too** (verified at `060fc834` under the same concurrent-suite load). Both are
    outside the code P706 touched and are now in `docs/TEST_FLAKES.md`.
  * My P706 tests (dispatch guard + cache identity) passed in **both** runs.

## Files

* `src/Allegiance.Core/Collision/Separate.cs` — per-body `GetB` cache in `ProbeCache` (the fix).
* `src/Allegiance.Core/Model/Sector.cs` — `LastShipShipEvents/Resolved/Debounced` diagnostic
  seams + `collision.ship.separate` / `collision.ship.event` profiler buckets.
* `tests/Allegiance.Core.Tests/P706DispatchProfilingTests.cs` — the dispatch-budget guard.
* `tests/Allegiance.Core.Tests/P706SeparateCacheIdentityTests.cs` — the cache byte-identity proof.

## Recommended follow-on (out of scope here)

Coalesce per-event broadcasts (collision, victim-damaged, projectile/missile hit/destroy) per tick
and pool the per-event message/packet/audience allocations — this is the part of the live 400 ms
that is not reproducible headlessly and is the P668–P679 broadcast-pipeline work.

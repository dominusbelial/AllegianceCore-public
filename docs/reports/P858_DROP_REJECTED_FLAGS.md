# P858 — Drop the two rejected experimental server flags (visibility spotter cache, parallel sector cells)

**Date:** 2026-10-27 · **Base:** public master at dispatch · **Status:** complete (working tree, uncommitted)

## Why

Rig A/B 2026-10-27 (2 full 200v200 matches per batch, P835 build): b40
`ALLEGIANCE_VIS_SPOTTER_CACHE=1` and b41 `ALLEGIANCE_SIM_PARALLEL_CELLS=1` gave no
gain over control b39 (clash-peak tick 75/85 and 75/67 ms vs 71/80 ms; TiDi and spike
counts within match noise). House rule: an experimental flag stays only if the
200v200 A/B improves. Domingo: "drop the other two". b42 (snapshot + encode-once)
won and is untouched; interest tiers stays an off option.

## What changed

### 1. Parallel sector cells — removed end to end

- Deleted `src/Allegiance.Core/Sim/ParallelSectorSim.cs` (the static flag holder).
- `src/Allegiance.Core/Model/Sector.cs`: the four detector call sites
  (`combat.detect`, `collision.asteroid.detect`, `collision.ship.detect`,
  `collision.station.detect`) now call the serial `CollisionDetector.Detect(dt[, filter])`
  overload — the exact code path that ran with the flag OFF, so the serial path is
  byte-identical by construction. The P667 parallel rescue-detection branch and
  `ResolveRescueEventsParallel` are removed; `ResolveRescueEvents` runs its serial
  loop only.
- `src/Allegiance.Server/SimConfig.cs`: removed `ParallelSectorCells` and
  `ParallelSectorCellThreads`.
- `src/Allegiance.Server/Server.Lifecycle.cs`: removed `ResolveParallelSectorSim`
  (config + `ALLEGIANCE_SIM_PARALLEL_CELLS` env) and its boot call.
- `docker-compose.yml`, `local.env*`, README: no entries existed for the parallel
  env/config keys (only the spotter-cache env did); nothing to remove there beyond
  the spotter env (below).
- Deleted `tools/P667Bench/` (exists only for the flag; `tools/build-tools.sh` globs,
  re-verified: all remaining tool projects build).
- Tests: deleted `P667ParallelSectorSimDeterminismTests.cs` (exists only for the flag).
  `P694MassEventTickTests` / `P706DispatchProfilingTests`: removed the parallel flag
  dimension (SimConfig keys, thread pinning, `InlineData` parallel rows), **kept their
  serial-path assertions and budgets**. `P790PodRescueOnContactTests`: the
  serial-vs-parallel rescue-set gate is now a serial-only latch test
  (`Rescue_latch_books_each_pod_exactly_once_in_scan_order` — one rescue per pod, scan-order
  winner, enemy pod never rescued). `P797MapDiscoveryTests`: the serial-vs-parallel
  visibility-set gate is now a serial snapshot test. The `CollisionDetector` parallel
  API itself is **kept** (not part of the flag): `P689CollisionPairTests`,
  `P694CollisionGeometryTests` and `P694bCollisionWorkTests` exercise it directly at
  the detector level and still pass, including their serial==parallel equality proofs.

### 2. Visibility spotter cache — flag removed, native identity rule kept

- Deleted `src/Allegiance.Core/Sim/VisibilitySpotterCache.cs` (the static flag holder),
  the `sim.visibilitySpotterCache` config property, `ResolveVisibilitySpotterCache`
  (config + `ALLEGIANCE_VIS_SPOTTER_CACHE` env) in `Server.Lifecycle.cs`, and the
  `ALLEGIANCE_VIS_SPOTTER_CACHE` env entry in `docker-compose.yml`.
- `tools/P835Bench`: removed the `[4]=1` spotterCache option; the bench (LOS
  pre-filter, `vis.*` sub-timers, GC attribution) is untouched otherwise.
- **Kept** P835's exact LOS occlusion pre-filter and the `vis.enumerate` / `vis.scan` /
  `vis.write` sub-timers — those are not the flag.
- `Sector.ResolveCrossSideVisibility` now runs the native last-spotter short-circuit
  **unconditionally** — see §3 for why this is not the rejected flag.

### 3. Parity row SIM-0840 — consumers audited, identity rule kept (unconditional)

Native audit (`~/Development/Allegiance/src/`, re-grepped `pLastSpotter` / `LastSpotter`):

- The short-circuit itself: `FedSrv/FedSrv.CPP:10895-10903`
  (`sv.pLastSpotter() && sv.pLastSpotter()->InScannerRange(pmodel)`) — **unconditional
  in native**; there is no flag in the native code at all.
- The recorded identity is written only from the full scanner loop
  (`FedSrv.CPP:11026`, "Keep track of who spotted it", inside the `InScannerRange`
  branch, then `break`) and cleared on hide (`FedSrv.CPP:11119-11120`).
- Behaviour-relevant consumers of the recorded spotter **do exist**:
  - `OBJECT_SPOTTED(6)`'s `otSpotter`/`oidSpotter` — both the ally-shared leg
    (`FedSrv.CPP:11011-11026`) and the first-seen export (`FedSrv.CPP:11055-11062`).
    In the port: `Server.Broadcast.cs:2218` and `:2248` (warp/rock discovery sweep)
    and `Server.Research.cs:36` (station), all reading
    `SideVisibilityState.LastSpotter` — wire-observable.
  - Native scoring/achievements on the same ShowObject path (high-value-target
    `RecentlySpotted` credit, garrison-spot `c_achmGarrSpot`, `SpotWarp` /
    `SpotSpecialAsteroid`) read the **spotting scanner** of the current sweep, not
    the cached identity — the port's `ShowcaseSkill.Scouting` credit
    (`Sector.cs`, vis.write phase) is likewise per-side on the false→true edge.

So per the brief: something **does** consume the identity → the native identity rule
is kept, **without the flag**, in its cheapest correct form: the short-circuit native
always runs, shipped unconditionally. Sticky identity falls out naturally — when the
recorded spotter still sees the target the loop is skipped and the identity is not
overwritten (`SetLastSpotter(spotter.Ot, spotter.Oid)` re-writes the same pair);
when it does not, the full loop re-picks and records the new one, exactly
`sv.pLastSpotter(s)` / `sv.pLastSpotter(NULL)`.

`docs/parity/sim-rules-matrix.csv` SIM-0840 updated: `implemented`, reachability
`production`, cite `FedSrv.CPP:10896-10903; :11026`, evidence
`P835VisibilityCostTests (Spotter_short_circuit_seen_flags_match_the_full_loop;
Spotter_identity_is_sticky_like_native)`. `python3 scripts/parity-scorecard.py`
validates.

### 4. Docs

- `GLOSSARY.md`: visibility-sweep entry rewritten (short-circuit unconditional,
  flag removed, links the report).
- `docs/architecture/SERVER_PIPELINE.md`: Ph.6 marked TRIED AND REMOVED with the A/B
  verdict; notes the detector-level parallel API remains.
- `docs/TEST_FLAKES.md`: no entries name the removed tests (checked) — unchanged.
- Historical reports (`P667_PARALLEL_SECTOR_SIM.md`, `P835_CLASH_PEAK_COST.md`, …)
  left as history.

## Verification (light gate)

| Gate | Result |
|---|---|
| `~/.dotnet/dotnet build AllegianceCore.slnx` | **0 warnings / 0 errors** |
| `tools/build-tools.sh` | all tool projects build, 0/0 |
| Rig AOT publish (`dotnet publish -c Release -r linux-x64 --self-contained -p:PublishAot=true`) | clean, no IL2026/IL3050 |
| Affected tests 3× alone (P835VisibilityCost, P797MapDiscovery, P790PodRescue, P694MassEventTick, P706DispatchProfiling) | **3/3 PASS** (37 tests/run, Debug) |
| Determinism/replay proof (MapGenSeedReproTests, CollisionDetectionTests, P689, P694b, P694CollisionGeometry) | PASS — serial detector path is the pre-P667 code path called directly; detector-level serial==parallel equality tests still green |
| ONE `tools/run-tests.sh --tier fast` | **PASS — 11033 tests, 308 s** |
| Showcase never-parks 5× vs base | **5/5 PASS** (4m20s–5m48s, within the load-sensitive norm on an idle host) |
| `python3 scripts/parity-scorecard.py` | validates (SIM-0840 now `production`) |
| `docker compose config` | validates |
| `grep -rn` both env names + both config keys across src/tests/tools/docker/scripts/.github/docs/GLOSSARY | only removal-history comments, the parity row and this report — no live wiring |
| `tools/check-glossary-links.py` | PASS |
| LF check on rewritten CSV/MD | 0 CR bytes |

Raw TRX/log evidence: `~/allegiance-evidence/P858/` (outside the repo).

## Serial-path byte-identity argument

With the flag removed, `Sector` calls `CollisionDetector.Detect(dt, filter)` — the
overload that delegates to `Detect(dt, parallel: false, threads: 1, filter)`, i.e. the
exact serial branch P667's determinism gate proved byte-identical to every parallel
configuration. No sim-visible code path changed: the rescue pass runs the identical
serial scan/latch loop (the parallel branch is deleted), and the visibility sweep's
only behavioural delta is the spotter *identity* on multi-scanner targets
(seen flags are provably identical: the short-circuit can only answer "seen" when the
full loop would — FedSrv.CPP:10896-10903), which is the native-intended wire form.

## Native-intent classification

- **Parallel sector cells**: no native counterpart at all — an invented optimisation
  (native `CclusterIGC::Update` collision block, `Igc/clusterIGC.cpp:255-321`, is
  serial). Rejected by A/B → removed outright. Serial path = native behaviour.
- **Visibility last-spotter short-circuit**: native behaviour, unconditional
  (`FedSrv.CPP:10895-10903`, record `:11026`, clear `:11119-11120`). The rejected
  experiment was only the *on/off switch* around it. Removed the switch; kept the
  native rule, now always on and parity-exact (SIM-0840 → `production`).

## Modern/better-approach note

Both flags were the classic "optimise first, measure later" shape: a static mutable
holder consulted deep in the sim, threaded through config/env/compose, with the
default path preserved byte-for-byte behind `if (Enabled)`. The rig A/B showed neither
earned its complexity. The better default, now in place: make the sim shape match
native exactly (unconditional short-circuit, serial detection), keep optimisation
experiments *outside* the shipping surface until a 200v200 A/B promotes them — and
when an optimisation also carries semantics (the sticky spotter identity), separate
the semantics from the perf knob: the semantics ship, the knob dies. If parallel
detection is ever revisited, the detector-level API and its equality tests remain the
starting point; the lesson from P667 is that the 200v200 bottleneck was never
collision detection at the measured load.

## Unfinished / handed to Claude

- Nothing code-side. Rig config: the rig's compose/env for b40/b41 carried the two
  env vars; if the rig host still sets them, they are now inert (unknown env vars are
  ignored) but can be deleted for hygiene.
- Historical reports still mention the flags (history, intentionally untouched).

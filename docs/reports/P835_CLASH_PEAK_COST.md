# P835 — server cost at the 200v200 clash peak: profile, attribute, and cut the biggest grower

Task: profile which tick phases grow at the 200v200 clash peak (evidence batches 36–38), reproduce
locally, then cut the biggest one behaviour-preservingly. Scope 1–4 of `P835_BRIEF.md`, in order.

Raw evidence: `~/allegiance-evidence/P835/` (filtered rig logs `p835-*.txt.gz`; local showcase log
`p835-local-run-b-server.log`; bench outputs `bench/*.txt`). Summaries: `docs/reports/P835_assets/`.

## 1. Profile (rig evidence)

Parsed every `[tickprof] window 5s … tick-wall mean/p95/max` header with its `step:` breakdown from
the three filtered logs (7 matches: b36 ×3, b37 ×3, b38 ×1; 5,664 windows). Windows were split per
match into **baseline** (lower half by tick-wall mean) and **clash peak** (top decile). Analysis
script kept in the task lane; per-match table in `P835_assets/phase-summary.json`.

### Phase growth, baseline → peak (mean over the 7 matches)

| step | baseline ms | peak ms | Δms | × | share at peak |
|---|---|---|---|---|---|
| **`step:update`** (Mission.Update) | 6.5 | 25.8 | **+19.3** | **4.0** | 17 % → **38 %** |
| **`step:broadcastShipsUpdate`** | 8.1 | 15.1 | +6.9 | 1.9 | 18 % → 19 % |
| `step:crossSideShipStatusSweep` | 2.1 | 4.2 | +2.1 | 2.0 | 5 % → 6 % |
| `step:broadcastMissiles` | 0.3 | 2.0 | +1.7 | 6.3 | 1 % → 3 % |
| `step:crossSideExportSweep` | 2.5 | 3.7 | +1.3 | 1.5 | 6 % → 5 % |
| everything else (≈35 buckets) | — | — | ≤ +0.3 each | ≈1.0 | — |

Per-match peak walls: 71.2 / 92.0 / 78.6 / 85.3 / 75.3 / 53.7 / 79.1 ms (baselines 36.6–47.3);
`sim.timeScale` 0.55–0.72 at the peaks (auto time dilation engaged), 0.90–1.00 at baseline.

**Coverage gap: ≈ 0 ms in every window of every match** (tick-wall − Σsteps ≤ ±0.1 ms). No
unaccounted time — no additional transport/physics timers are needed at the `step:` level.

**Ships-per-sector correlation:** the profiler already samples per-sector ship/object populations
every window (`[sector populations]`, `TickProfiler.RecordSectorCounts` — `Mission.cs:879-884`) —
nothing was missing, so no new sampler was added. The filtered rig logs don't carry those lines
(they predate the filter's current shape), so population correlation comes from the local run
(below).

### What grows inside `step:update`

The rig logs are filtered to `step:` lines, so the inner attribution comes from the local runs
(§2). The rig's own signature corroborates it: at peak the tick-wall **max** reaches 169.3 ms
against an 85 ms mean (b37), i.e. a periodic spike ~2× the mean, and `[overrun]` fires ~800–900
ticks/minute — a periodic sub-step, not uniform growth.

## 2. Local reproduction

### 2a. Real-server showcase (server + MCP bot host, this workstation)

`scripts`-style run (`.scratch/p835-showcase-local.py`, raw log
`~/allegiance-evidence/P835/p835-local-run-b-server.log`): showcase game on the rig's map
`m2xspiderw.igc`, IC vs Bios, **120 bots** (60/side — the most this workstation sustains while
staying responsive; spawn of 160 was tried and outlasted the RPC window), profiler on (5 s windows),
bots staged and marched into ONE sector (Alderan 1052 — the rig's clash sector). The fight peaks at
**~24–37 ships simultaneously in 1052** (bots die/rebuy/march; a rig-density 200v200 single-sector
clash is beyond what a workstation bot-host sustains in one match).

Per-window correlation over 182 windows (amortised ms/tick, low-pop = windows with ≤8 ships in 1052,
high-pop = ≥15):

| bucket | ≤8 ships | ≥15 ships (mean 23.8) |
|---|---|---|
| `step:update` | 1.09 | 2.17 (**×2.0**) |
| `sim.visibility` | 0.30 | 2.57 (**×8.7**) |
| `sim.combat` (incl. detect) | 0.00 | 0.34 |
| `sim.mining` | 0.09 | 0.19 |
| `sim.collision` | 0.33 | 0.36 |
| `step:broadcastShipsUpdate` | 1.19 | 1.04 |

**The same phase that grows at the rig peak (`step:update`) grows locally, and inside it the
every-10th-pass cross-side visibility sweep (`sim.visibility`) is by far the fastest-growing pass.**
At the 24-ship clash it already costs ~25.7 ms on each sweep (amortised 2.57 ms/tick).

### 2b. `tools/P835Bench` — rig-scale density attribution

New one-off bench (built by `tools/build-tools.sh`): reconstructs the clash-peak mix in ONE sector
of `m2xspiderw.igc` (Alderan, richest asteroid field), two sides of moving fighter hulls, a
sustained 240-projectile salvo, 60 mines, profiler on; measures `Mission.Update` (= `step:update`)
at 200 and 400 ships. At 400 ships: mean 2.0→3.6 ms (`step:update`-scale), inner buckets amortised:

| bucket | 400 ships, amortised ms/tick |
|---|---|
| combat (build+detect+dispatch) | 1.05 |
| mining | 0.75 |
| blast resolve/apply | 0.73 |
| **visibility (sweep ×1/10 ticks)** | 0.66 |
| collision + asteroid detect | 0.31 |
| models loop (ships, unbucketed) | ~0.7 |

The bench scene (ships spread over a 2200 u sphere) **understates** the sweep relative to a real
clash: real ships cluster near stations/rocks and miners/stations carry 2600+ scanner ranges —
hence the 2a numbers being ~4× the bench per firing. The sweep scales with
targets × scanners × occluder checks and is the only O(n²)-shaped pass inside `update`.

## 3. The cut (behaviour-preserving)

Target: the visibility sweep — the fastest-growing pass inside the biggest-growing phase.
Three changes in `src/Allegiance.Core` (+ server wiring):

1. **Conservative occlusion pre-filter in `LineOfSightExist`** (`Sim/VisibilityMath.cs`) —
   *default-on, exact-result-preserving.* Occlusion requires
   `Acos(cosSep) + visibleAngle < Asin(radius3·overLen13)` (`Igc/common.cpp:2376-2378`). Using
   `Acos(c) ≥ 1 − c` and `Asin(x) ≤ (π/2)x` on [0,1], a candidate asteroid whose lower-bound left
   side already reaches its upper-bound right side (with 1e-3 slack for the bounds' own float
   rounding) can never satisfy the exact condition; the two transcendentals per asteroid are
   skipped. Every boundary-adjacent pair falls through to the unchanged exact comparison, so the
   boolean — and therefore the simulation — is identical for every input.
2. **Bounded sub-step timers + scan/write split** (`Model/Sector.cs`): the sweep now reports
   `vis.enumerate` / `vis.scan` / `vis.write` (zero cost when the profiler is off), and each
   target's per-side scans complete before any flag write (scan reads only geometry, never
   visibility flags → identical flag sequence; one profiler scope per target instead of per pair).
3. **The native last-spotter short-circuit** — `sim.visibilitySpotterCache` /
   `ALLEGIANCE_VIS_SPOTTER_CACHE`, **default OFF** (`Sim/VisibilitySpotterCache.cs`, wired in
   `Server.Lifecycle.ResolveVisibilitySpotterCache`, docker-compose entry added). Native runs this
   short-circuit *before* its full scanner loop (`FedSrv.CPP:10896-10903`): if the target's recorded
   spotter for the side still sees it, skip the O(scanners) loop. Seen flags are identical to the
   full loop for every input; the recorded **spotter identity** becomes the native sticky one
   (`FedSrv.CPP:11026`) instead of the pre-P835 re-pick of the first scanner in enumeration order —
   a wire-observable change in `OBJECT_SPOTTED(6)`'s otSpotter/oidSpotter when several scanners see
   one target — hence the default-off flag and a 221 recipe: run the rig A/B with
   `ALLEGIANCE_VIS_SPOTTER_CACHE=1` and compare `[tidi] cost`, `step:update` mean/p95/max and the
   ~170 ms spike frequency before flipping.

### Measured (this workstation, `tools/P835Bench`, 400 ships × 500 ticks, 3 runs each)

| config | Mission.Update mean | p95 |
|---|---|---|
| pre-P835 (baseline binary) | 3.60 / 3.87 / 4.09 ms | 9.22 / 9.33 / 9.13 |
| P835 flag off (default) | 3.63 / 3.74 / 3.95 ms | 8.94 / 9.02 / 9.16 |
| P835 + `visibilitySpotterCache` | 3.53 / 3.55 / 3.83 ms | **7.76 / 7.78 / 8.05** |

Inner buckets (400 ships, per firing of the sweep): `vis.scan` 6.42 → 6.26 ms (pre-filter) →
**5.09 ms** (short-circuit), `vis.write` 0.06 → 0.08, `vis.enumerate` 0.12 → 0.13. The bench scene
has shallow scans (spread ships), so it only shows part of the win; the real-server scene (§2a,
clustered fight, long scanner lists) is where the short-circuit bites — there the sweep's dominant
cost is exactly the deep scanner loop the cache removes. **The decisive before/after is the rig
A/B** (recipe above); on the rig evidence the sweep is the best candidate for the bulk of the
+19.3 ms peak growth, and the short-circuit reduces its per-pair cost from O(scanners) to O(1).

### Byte-identity / parity proof

- `tests/Allegiance.Core.Tests/P835VisibilityCostTests.cs` (new, 5 tests, run 3× alone, all green):
  6000 randomized differential scenes + 3000 boundary-adjacent scenes prove the pre-filter returns
  the unfiltered reference result every time (blocked=3291 / clear=2709 — both outcomes exercised);
  the spotter cache defaults off; seen flags are identical (cache on vs off) at every sweep of a
  two-scanner scene; the cached spotter is sticky like native and the full loop re-picks (stage-2
  spotter 9601 vs 9602); spotter cleared when nobody sees.
- Existing affected suites (CrossSideVisibilitySweepTests, MineVisibilitySweepTests,
  P476SideGaVisibilityTests, P667ParallelSectorSimDeterminismTests incl.
  `Crowded_sector_tick_output_is_byte_identical_*`) — 3× alone, all green (default path unchanged).
- Parity row **SIM-0840** appended to `docs/parity/sim-rules-matrix.csv`; the "deliberately
  omitted" remark in `Sector.cs` updated to point at the flag.

## 4. Remaining hot phases, ranked (follow-up tasks)

| rank | phase | peak cost | estimated win | how |
|---|---|---|---|---|
| 1 | `step:update` → visibility sweep (remaining) | part of +19.3 ms | rig A/B of `sim.visibilitySpotterCache` (recipe §3) | flag already shipped |
| 2 | `step:update` → collision/combat detection | part of +19.3 ms | up to ~60 % of `combat.detect`/`collision.*.detect` | `sim.parallelSectorCells=true` rig A/B (built, deterministic, P667) |
| 3 | `step:update` → per-ship models loop (unbucketed ~0.7 ms local) | unmeasured on rig | unknown — measure first | add bounded per-model-class timers (`sim.ship`, `sim.missile`) where the profiler already walks `_models` |
| 4 | `step:broadcastShipsUpdate` | +6.9 ms (1.9×) | ~30–50 % of the step | rig A/B of the built `broadcast.immutableSnapshot` + `broadcast.encodeOnce` (off on the rig today) |
| 5 | crossSide status/export/discovery sweeps | +3.4 ms combined | ~⅓ | `broadcast.interestTiers=true` rig A/B (built) |
| 6 | `step:broadcastMissiles` | +1.7 ms (6.3×) | small | same interest-tier cadence |

## Verification (light gate)

- `dotnet build AllegianceCore.slnx` — 0 warnings / 0 errors.
- `tools/build-tools.sh` — all 88 tool projects (incl. new `tools/P835Bench`).
- Rig AOT publish (`dotnet publish … PublishAot=true`) — clean, no IL2026/IL3050.
- New/affected tests 3× alone — green (§3); showcase never-parks (Live) — 5 passes + 1 load flake
  that passed on an isolated re-run (the established P700 load pattern, `docs/TEST_FLAKES.md:978`).
- ONE `tools/run-tests.sh --tier fast` — **PASS, 10851 tests**.
- `docker compose config` — valid (new env var `ALLEGIANCE_VIS_SPOTTER_CACHE` wired, default 0).

## Native-intent classification

**Native-intent.** The sweep is a faithful port of native's every-10th-pass `UpdateSeenBySide`
block (`clusterIGC.cpp:347-357`, `FedSrv.CPP:10876-10945`); the pre-filter does not alter any
result (`common.cpp:2324-2398` semantics untouched), and the spotter cache *restores* a native
behaviour the port had previously documented as deliberately omitted (`FedSrv.CPP:10896-10903`,
`FedSrv.CPP:11026`) — flagged default-off because spotter identity is wire-observable, pending the
rig A/B. No rule, cadence or time-dilation policy was changed.

**Modern/better-approach note.** Native's O(scanners×targets) re-sweep with a sticky spotter is a
1990s-flavoured cache; the cleaner modern shape would be an incremental visibility index (per-side
scanner broadphase over a uniform grid, dirty-region re-evaluation, event-driven
true→false edges) or — closest to native's own structure — the registered per-side scanner lists
native maintains (`ClusterSite::GetScanners`, `FedSrv.CPP:10909`) instead of the port's
re-enumeration query. Both would change update *frequency* semantics unless proven sample-identical,
so the native short-circuit (already proven flag-identical on seen flags) is the right first step;
the registered-scanner-list substitution is the recommended follow-up (it also removes the per-sweep
`targets`/`scannersBySide` allocations).

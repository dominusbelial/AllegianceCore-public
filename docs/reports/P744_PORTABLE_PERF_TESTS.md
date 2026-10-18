# P744 — performance-budget tests that hold on any machine (fix the mini runner's baseline)

## Summary

Three live perf/timing tests held ABSOLUTE wall-clock budgets that were tuned on this
workstation. On the dedicated test runner (UM690 mini PC, Ryzen 9 6900HX, Docker, suites
sequential) the same work takes longer wall time, so all three failed on **every** run —
master and branches alike — while passing here. This task makes those budgets
**hardware-relative**: each budget/timeout is now scaled by a machine speed factor measured
in the SAME run against a fixed reference workload, so a slower runner neither false-fails
nor gets a free pass, and a genuine algorithmic regression still trips the budget.

Scope is strictly tests + one shared test helper. **No production code changed.**

## Root causes

| Test | Budget today | Root cause on the mini |
|---|---|---|
| `P706DispatchProfilingTests.Dispatch_budget("matchStart",165)` | absolute 50 ms (Release) / 200 ms (Debug) | absolute-ms budget vs CPU speed; on this rig matchStart ≈166 ms Debug leaves only ~20 % headroom, so a ~1.3× slower Docker runner crosses 200 ms |
| `BroadcastSnapshotLoopbackTests.Crowded_real_TCP_session_…` | 30 s join + 30 s drain barriers | wall-clock join/drain deadlines for 256 real TCP peers are too tight on a slower machine |
| `P705TechModeLiveTests.Research_upgrades_…(civA:4,civB:35,size:3)` | fixed `Wait()` deadlines (300/90/60/30 s) | a two-match research/sim run whose wall-clock rate follows CPU speed; the research-completion `Wait` (~240 s on the rig) hits its 300 s deadline when the machine is ~1.3× slower |

All three share the same defect: an absolute duration that only makes sense on the machine
it was tuned on.

## Design: `PerfCalibration` (new shared test helper, `tests/_shared/PerfCalibration.cs`)

`Allegiance.TestInfra.PerfCalibration` runs a **fixed, deterministic batch** of scalar-FP /
vector maths plus strided array churn — the same flavour of work the guarded dispatch / sim
hot paths do — and turns it into a machine speed factor:

    factor = measured_reference / nominal_reference
    scaled_budget = reference_budget * factor

* **Reference workload** (`ReferenceWorkload`): fixed iteration count, allocation-light,
  deterministic, and resistant to JIT elimination (observable array stores + a sink XOR).
* **Robustness**: the fastest of 7 repeats is used, so a single GC/scheduler hiccup cannot
  inflate the factor; `CpuSpeedFactor` is a lazy singleton, computed once per process,
  outside the guarded timing region.
* **Config anchoring**: the reference is ordinary scalar-FP C#, so the JIT scales it between
  Debug and Release exactly like it does the guarded dispatch. The nominal anchor is
  therefore config-specific (`#if DEBUG`), so factor ≈ 1.0 on the reference workstation in
  BOTH configs and the scaled budget keeps its pre-P744 meaning in the config it runs in.
  Anchors measured on this workstation: **Debug 3.770 ms, Release 1.885 ms**.
* **Regression proof knob**: `P744_SIMULATE_REGRESSION` (>1) inflates ONLY the guarded
  measurement (never the reference), letting a runner demonstrate the budget still catches a
  real slowdown without disturbing the calibration.

Why this satisfies "calibrate against a quick reference workload measured in the same run":
the reference and the guarded measurement share the same machine, process and config, so
their ratio is hardware-relative by construction. A machine N× slower measures N× on the
reference too → scaled budgets identical to the reference machine's. A dispatch/sim/transport
regression raises only the guarded measurement → the scaled budget still trips.

## Changes per test

### P706 dispatch budget — `P706DispatchProfilingTests.cs`
* The absolute `DispatchBudgetMs` (50/200) is kept as the reference-anchored base and scaled:
  `budget = PerfCalibration.Scale(DispatchBudgetMs)`. Calibration is forced up-front and the
  raw reference + factor are printed for diagnosis.
* Added `[Trait("Category","Perf")]`.
* Regression demo: the worst-dispatch measurement is multiplied by
  `PerfCalibration.SimulateRegressionFactor` (test-only, when set), modelling extra work per
  event — a real dispatch regression.

Verified (workstation): **3/3 pass** in Debug (factor ≈0.976, scaled budget ≈195 ms,
matchStart median ≈166 ms, gameOver ≈10 ms); Release factor ≈1.002, scaled budget ≈50 ms,
matchStart ≈37.5 ms (matches the original P706 gate of 41 < 50). Failing-first / regression
power: with `P744_SIMULATE_REGRESSION=3` the matchStart case FAILS (498 > 195) while gameOver
still passes — the guard catches a 3× dispatch slowdown on the exact workload that matters.

### Crowded broadcast loopback — `BroadcastSnapshotLoopbackTests.cs`
* The 30 s join deadline and the 30 s transport-drain `SpinWait.SpinUntil` are scaled by the
  measured factor (`30 * speed`, `30 * speed / regr`).
* Added `[Trait("Category","Perf")]` to all three crowded measurement tests.

Verified (workstation): **3/3 pass** (~22 s each). Regression power: the drain barrier is
generous on loopback (a sub-second drain even at ×1000 simulated slowdown), but at a severe
pipeline slowdown (`P744_SIMULATE_REGRESSION=100000`, budget 0.3 ms) it FAILS in 4 s — the
drain assertion remains load-bearing; the generous default is correct for a measurement
harness whose primary job is recording tick/projection/publish metrics.

### P705 tech-mode research — `P705TechModeLiveTests.cs`
* Every `Wait(done, reason, seconds)` deadline is now `seconds * PerfCalibration.CpuSpeedFactor`
  (wall-clock budget scaled by measured machine speed), and the loop compares
  `elapsed * regr` against it so a simulated regression still fires the deadline.
* Added `[Trait("Category","Perf")]`.

Verified (workstation): **3/3 pass** on the named failing case (civA=4, civB=35, size=3) —
details in the verification log below. Regression power: with
`P744_SIMULATE_REGRESSION=10` (a machine 10× slower than its calibrated budget), the research
`Wait` trips in ~30 s real time — the deadline mechanism still fires on a sim-progression
slowdown.

## Native behaviour classified

No production or native code was ported in this task — it is test/timing only. For
completeness, the guarded behaviours and their provenance:

| behaviour | native cite | class | what the port does |
|---|---|---|---|
| ≤50 ms mass-event dispatch budget | P706 report (native hull `Separate`, `shipIGC.cpp:514-616`, `HitTest.cpp:889-909`) | design (perf gate) | unchanged; budget now scaled by measured machine speed |
| convex-hull separation is the dominant dispatch cost | P706 report | design | used as the shape of the calibration reference workload |
| 256-peer real-TCP broadcast drain | P668–P679 broadcast pipeline (port) | port design | barrier deadline scaled by machine speed |

This task deliberately touched only tests/test-helper code; the production dispatch,
broadcast and research paths are byte-for-byte identical to `master`.

## Modern alternative considered

* **Hardware-relative via in-run ratio** (dispatch ≤ X% of tick): rejected because dispatch
  *is* most of the tick here, making the ratio self-referential and unable to distinguish a
  real dispatch regression from normal tick cost.
* **BenchmarkDotNet-style percentiles / statistical budgets**: heavier and still needs a
  machine anchor; overkill for a regression guard.
* **Absolute budgets tuned per-host via config/env**: maintains per-machine magic numbers
  that drift; the single reference workload keeps one anchor per config.
* Chosen: **normalize the absolute budget by a same-run reference workload** (the option the
  brief explicitly offers). It is lightweight, deterministic, config-anchored, and provably
  still catches regressions (see the ×3/×10/×100000 demos above).

## Build / suite

* Whole-solution build (Debug and Release): **0 warnings, 0 errors**.
* Affected-test isolation (see verification log): P706 ×3, Broadcast ×3, P705 ×3 — all pass.
* Full suite `scripts/run-tests.sh`: **exactly one** run on the final code (numbers recorded
  in the verification log below), per the P639 light gate.
* `P700ShowcaseLiveTests.Local_showcase_never_parks_with_target_for_five_seconds` ×5 — this
  task changes no flight/sim/bot production code (test-side only), so it is unaffected, but
  the light gate requires the run; numbers in the log.

## Verification log (this workstation)

All runs below are Debug (the suite config) unless noted. `ref` = fastest reference-batch
time, `factor` = ref ÷ nominal (≈1.0 on this workstation; a slower runner measures >1).

Affected tests, 3× alone:

| test | runs | result |
|---|---|---|
| `P706 Dispatch_budget` (matchStart+gameOver) | ×3 | 3/3 pass; ref ≈3.68 ms, factor ≈0.976, scaled budget ≈195 ms (Debug); matchStart median ≈166 ms, gameOver ≈10 ms |
| `P706 Dispatch_budget` Release | ×1 | pass; ref ≈1.89 ms, factor ≈1.002, scaled budget ≈50 ms, matchStart ≈37.5 ms |
| `Broadcast … Crowded_real_TCP_session` | ×3 | 3/3 pass (~22 s each) |
| `P705 … (civA:4,civB:35,size:3)` | ×3 | 3/3 pass (8 m 14 s / 8 m 11 s / 7 m 59 s) |

Regression-catch (each guard still fails on a deliberately slowed path, without touching the
calibration reference):

| test | slowdown knob | result |
|---|---|---|
| `P706` | `P744_SIMULATE_REGRESSION=3` (dispatch ×3) | FAIL — matchStart 498 > 195 ms; gameOver still passes |
| `Broadcast` | `P744_SIMULATE_REGRESSION=100000` (drain budget 0.3 ms) | FAIL in 4 s; passes at ×1000 (drain is sub-second on loopback — generous by design) |
| `P705` | `P744_SIMULATE_REGRESSION=10` (machine 10× slower than calibrated) | FAIL in 34 s — research `Wait` deadline fires |

Whole-solution build (Debug + Release): 0 warnings, 0 errors.

Full suite `scripts/run-tests.sh` on the final code (single run, per the P639 light gate):

| assembly (MAIN) | result |
|---|---|
| Lobby | 19/19 pass |
| ClientCore.MonoGame | 2321 pass + 3 skip |
| ClientCore | 1197 pass + 1 skip |
| Core | 4757 pass |

Quarantine (`Lane=LiveTiming`): ClientCore 3/3, Core 18/18 — all cleared.
**`ALL TEST PROJECTS PASSED` (SUITE_EXIT=0)** — a fully clean single run, including P706,
the crowded broadcast test and P705 in the MAIN pass.

`P700ShowcaseLiveTests.Local_showcase_never_parks_with_target_for_five_seconds` ×5:
**5/5 pass** (1 m 35 s / 1 m 37 s / 1 m 40 s / 1 m 35 s / 1 m 35 s). Base rate unchanged —
this task alters no flight/sim/bot production code (test-only), so the park-timer rate is
expected identical to master.

## Files

* `tests/_shared/PerfCalibration.cs` — NEW shared hardware-relative calibration helper.
* `tests/Allegiance.Core.Tests/P706DispatchProfilingTests.cs` — scaled dispatch budget + Perf trait + regression knob.
* `tests/Allegiance.Core.Tests/BroadcastSnapshotLoopbackTests.cs` — scaled join/drain deadlines + Perf traits.
* `tests/Allegiance.ClientCore.Tests/P705TechModeLiveTests.cs` — scaled Wait deadlines + Perf trait.
* `tests/Allegiance.Core.Tests/Allegiance.Core.Tests.csproj`, `tests/Allegiance.ClientCore.Tests/Allegiance.ClientCore.Tests.csproj` — include the new shared helper.

## Instructions for Claude to verify on the mini runner

On the UM690 mini (Docker, suites sequential), from a fresh checkout of this commit:

1. Confirm the three formerly-red tests are green **in Debug** (the suite config):
   ```
   cd <repo>
   dotnet build -m:8
   dotnet test tests/Allegiance.Core.Tests --no-build --filter \
     "FullyQualifiedName~P706DispatchProfilingTests.Dispatch_budget"
   dotnet test tests/Allegiance.Core.Tests --no-build --filter \
     "FullyQualifiedName~BroadcastSnapshotLoopbackTests.Crowded_real_TCP_session_measures_sim_projection_and_publish_pool"
   dotnet test tests/Allegiance.ClientCore.Tests --no-build --filter \
     "FullyQualifiedName~P705TechModeLiveTests.Research_upgrades&DisplayName~civA: 4"
   ```
   Expect a `P744 calibration: ref=… speedFactor=…` line with speedFactor > 1 (mini is slower
   than the workstation) and all three PASS. Record the speedFactor in the report.
2. Sanity-check the budgets are actually scaling: a mini speedFactor of ~1.3 should stretch
   the P706 Debug budget to ~260 ms, the broadcast barriers to ~39 s, and the P705 deadlines
   by ~1.3× — none of which should be hit on the mini.
3. Prove the guards still bite on the mini (regression demo):
   ```
   P744_SIMULATE_REGRESSION=3 dotnet test tests/Allegiance.Core.Tests --no-build --filter \
     "FullyQualifiedName~P706DispatchProfilingTests.Dispatch_budget&DisplayName~matchStart"   # expect FAIL
   P744_SIMULATE_REGRESSION=100000 dotnet test tests/Allegiance.Core.Tests --no-build --filter \
     "FullyQualifiedName~BroadcastSnapshotLoopbackTests.Crowded_real_TCP_session_measures_sim_projection_and_publish_pool"  # expect FAIL (drain)
   ```
4. If any test STILL fails on the mini with the scaling, do NOT loosen budgets — capture the
   failing assert (barrier / seating / peer error / which Wait) and the `speedFactor`, add it
   to this report, and treat it as a genuine remaining defect (per the shared light gate:
   a fail in code you did not touch → run the class 3× in isolation, else list in
   `docs/TEST_FLAKES.md`).
5. Run the one full `scripts/run-tests.sh` on the merged batch, then note the numbers here.

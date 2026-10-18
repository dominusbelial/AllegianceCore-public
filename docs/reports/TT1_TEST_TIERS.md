# TT-1 — Fast / Live / Perf

Implemented in the working tree. The final build is 0 warnings / 0 errors.
One full sweep covered all 8,613 .NET cases (8,605 original + 8 new). Live and
Perf passed; three Fast failures qualified 3/3 in isolation as load flakes.
The timing guard and deferred-theory accounting repairs pass the final audit.
No second full sweep, commits or pushes were performed.

## Implementation

All four projects use xUnit. Their shared discovery framework assigns Fast only
when Tier is absent, preserves Requires/Category/Lane traits, and rejects invalid
or multiple tiers. The framework covers ordinary facts, skippable facts and each
theory row. Real discovery errors are recorded in the runner manifest as well as
reported through xUnit, so an adapter returning success for the remaining tests
cannot silently omit an invalid case.

Explicit Live annotations cover the match/flight/keep-alive scenarios and slow
corpus matrices. Perf includes P694, P706, crowded BroadcastSnapshot tests,
PipelineCost, UpdateCadenceLoopback, P689 station-death budgets, profiler overhead,
transport nonblocking budgets, snapshot allocation and P643 GPU checks. P643 also
has Requires=Gpu. P705 uses PerfCalibration for functional match deadlines and
therefore belongs
to Perf under the brief's explicit rule. Its Category=Perf metadata is preserved.

`tools/run-tests.sh` is the shared entry point. Fast projects run concurrently
(default four, configurable with `--parallel`), preserving existing xUnit safe
collection behavior. Live and Perf disable collection parallelization and run
projects sequentially. Perf takes two idle load samples, then runs P744's same
calibration workload. An exclusive runner lock prevents overlapping invocations
across worktrees. The runner builds once, records discovered cases and traits,
reads actual TRX results, checks coverage and counts, lists slow Fast cases, and
writes a summary per tier plus an overall summary. Missing results/process errors
fail the gate. The scaled Fast limit is `max(10, PerfCalibration.Scale(10))` seconds.

P647 quarantine remains a separate sequential pass within its tier, with its
existing one-isolated-retry policy and visible flake reporting. Retry results
are separate artifacts and do not inflate the unique test count. The compatibility
`scripts/run-tests.sh` forwards to the new runner. Public PR/push CI runs Fast
with its corpus/quarantine exclusions, plus GPU/MeshyPack exclusions, and uploads
artifacts on failure. `release.yml` is unchanged.

## Coverage baseline

An unmodified solution build passed 0 warnings / 0 errors. Unfiltered VSTest
list discovery before editing found 8,561 discovery entries: Lobby 19, MonoGame 2,433,
ClientCore 1,299, Core 4,810. Twelve Core entries contain non-serializable theory
data and expand into 56 runtime rows: the actual original runtime total is
8,605 (Core 4,854). The corrected manifest enumerates their expected display
names without running tests, and matches every expanded row in the TRX.
This checked-out base contains more cases than the older mini snapshot in the brief. Comparing the complete before/after name
multisets preserves every original row, including two long-argument theory rows
that xUnit displays with the same truncated name. Eight TierConvention cases
are added; no original test or assertion is removed.

| Project | Fast | Live | Perf | Union | Original |
| --- | ---: | ---: | ---: | ---: | ---: |
| Lobby | 19 | 0 | 0 | 19 | 19 |
| MonoGame | 2,415 | 9 | 9 | 2,433 | 2,433 |
| ClientCore | 1,265 | 37 | 5 | 1,307 | 1,299 |
| Core | 4,793 | 12 | 49 | 4,854 | 4,854 |
| Total | 8,492 | 58 | 63 | 8,613 | 8,605 |

## Cheaper research proofs and preserved long scenarios

Before measurements use existing P738 workstation suite TRX at
`/tmp/p738-hd2-validation/suite-{ClientCore,MonoGame}.trx`; these are historical
measurements, not an additional full sweep for TT-1. The mini measurements from
the brief are identified separately. After times come from this task's TRX.

P705 sets the existing P733 researchTimeScale to 16. It retains all four
civilization/fleet-size combinations and both complete matches in every row.
Every existing assertion remains: initial/reset development state; both sides'
wire-observed developments; newly legal upgraded hulls; bought-everything launch
acks; exact server inventory; normal conquest, GAME_OVER, reseating and epoch
reset; and zero sim-pass failures. Research duration is accelerated; construction
travel, purchase/wallet/tech checks and network paths are unchanged. The first
four-case repeat took 187.6 seconds versus 959.7 seconds in the historical
workstation suite (the brief's mini total was 17.4 minutes).

P703 enables showcase research mode and sets researchTimeScale=16 so the existing
knob actually applies (Server.Lifecycle.cs applies it only in research mode).
It keeps the real wallets and production buckets, constructor travel, first
branch stations on both sides, completed developments, plan selection and MCP
research status assertions. The two remote-constructor lifecycle rows are
unchanged apart from their class tier.

P695 is deliberately unchanged apart from Tier=Live. Its 605-second soak explicitly
protects a stale-anchor failure that short flight missed. It exercises the real
Game1 clock pump/send/correction paths, Environment.TickCount64 receive stamps,
the actual session-owned six-second keep-alive thread, and three time-dilation
phases. Server.TickSim alone advances only the simulation; it cannot advance
these real client/session clocks. Shortening this loop or replacing timestamps
with test-supplied simulation time would weaken its claim. The brief permits a
scenario that needs the long run to remain Live unchanged. Consequently the
requested two-minute P695 target is not claimed. Historical workstation total
for its five rows is about 10.8 minutes.

P540b likewise remains unchanged apart from Tier=Live: its production autopilot,
real ping-clock pose stamps, two warp transits and zero pose rejects/SHIP_RESETs
are checked through paced flight. Dropping pacing alone would invalidate that
clock/pose proof. Historical workstation time is 238.8 seconds. No production
flight, bot, simulation or clock code is changed by TT-1.

Native references for the preserved clock rules: the six-second lag-check interval
is `~/Development/Allegiance/src/clintlib/clintlib.cpp:2052` (used at
`~/Development/Allegiance/src/clintlib/clintlib.cpp:2059`);
client pose timestamps convert to server time at
`~/Development/Allegiance/src/clintlib/clintlib.cpp:1863`. The server's ±1.5-second
pose timestamp gate is `~/Development/Allegiance/src/Igc/shipIGC.cpp:3146`.
These rules are context for retaining the clock-sensitive proofs, with no rule
changes in this task.

The Fast timing guard also identified `MeshyGuardTests.NoTrackedMeshyOutputRemainsAnywhere`:
it repeatedly ran `git ls-files` for individual inventory entries, taking more
than ten seconds in the sweep. It now reuses one repo-wide list and an ordinal
hash set for the extension/name/inventory checks. Its existing location queries
and every assertion are preserved. Final isolated timings are recorded below;
the full-sweep artifacts retain the original guard failure honestly.

The single sweep measured P695's five unchanged rows at 646.19 seconds total
(including the full 605.22-second soak), and P540b at 238.59 seconds; all passed.
These are intentionally preserved Live costs, with no claimed speedup.

Tier separation also exposed console-test cleanup contamination. The Live
`Installed_plain_WriteLine_never_blocks_a_stalled_consumer_and_drops` test changes
the queued writer's passthrough to a 20 ms stalling sink. `Uninstall` restores
that passthrough, so selecting only this method leaves later Live tests writing
synchronously to the slow sink. Its finally block now restores the original
Console.Out and Console.Error before resetting the logger. The 200,000-line
nonblocking/drop assertions are unchanged. This is test cleanup only; production
ConsoleLog is untouched. The original sweep retains the slower observed Core
Live timings; there is no claimed second measurement of the full Live tier.

## Focused speedup measurements

| Scenario | Historical workstation before | After, mean of 3 isolated repeats |
| --- | ---: | ---: |
| P705 civ 25 / 3, 10v10, two matches | 175.38 s | 38.25 s |
| P705 civ 1 / 2, 2v2, two matches | 162.46 s | 40.42 s |
| P705 civ 4 / 35, 3v3, two matches | 467.60 s | 65.56 s |
| P705 civ 101 / 1, 5v5, two matches | 154.25 s | 43.01 s |
| P705 total | 959.69 s | 187.25 s |
| P703 branch, civ 25 / 3 | 89.62 s | 21.30 s |
| P703 branch, civ 1 / 2 | 176.34 s | 31.77 s |

P705's three four-row totals were 187.57 / 186.55 / 187.64 seconds (all pass).
P703's three four-row totals, including the two unmodified remote-constructor
rows, were 58.43 / 57.46 / 58.77 seconds (all pass). Historical timings are
provided as measured evidence, with no claim of a controlled same-load A/B run.
P705 meets the four-minute total target on this workstation; a new mini timing
is still required. P695's two-minute target is intentionally not met, for the
preserved real-clock regression described above.

## Validation

One full `tools/run-tests.sh --tier all --no-build` invocation ran after the
whole-solution 0/0 build. No second full sweep was run. Observed workstation
results from the raw TRX are:

| Tier | Unique cases | Passed | Failed | Skipped | Wall time |
| --- | ---: | ---: | ---: | ---: | ---: |
| Fast (projects concurrent) | 8,492 | 8,486 | 3 | 3 | 5 m 02 s |
| Live (sequential) | 62 | 61 | 0 | 1 | 46 m 25 s |
| Perf (sequential) | 59 | 59 | 0 | 0 | 6 m 51 s |
| Union | 8,613 | 8,606 | 3 | 4 | |

P705 was initially placed in Live because its calibration guards functional
progression deadlines. Final review corrected it to Perf to honor the brief's
explicit rule for all PerfCalibration users. Its four scenarios were then
rerun through `--tier perf` with the idle check and calibration: **4/4 pass**,
186.06 seconds in test bodies / 190.69 seconds for the targeted Perf tier,
exit **0**. Idle load/CPU was 0.117 and 0.108; P744 factor was 0.977.
[Targeted Perf summary](TT1_assets/p705-perf-recheck/perf/summary.json). The table above
retains the observed original sweep layout (62 Live / 59 Perf); final declarations
are **58 Live / 63 Perf**, as shown in the coverage table. The targeted Perf timing
is reported separately; no timing for a second complete final-layout sweep is claimed.

The original full invocation returned **1**. Its original summaries and raw TRX
retain the observed Fast failures and the deferred-theory accounting error.
The raw Fast TRX also showed the 12.39-second hygiene guard. The accounting and hygiene
issues were repaired without repeating the full sweep; final isolated outcomes
and the current timing audit are recorded below. All three failures subsequently
passed 3/3 isolated repeats, qualifying them
as load flakes under the lane's explicit rule; their details and raw results
are retained in `docs/TEST_FLAKES.md` and the qualification ledger.

Perf's two idle samples measured load/CPU 0.304 and 0.307 on 24 available CPUs;
P744 measured factor 1.092 (4.1153 ms reference, 3.770 ms nominal). All 59 Perf
checks passed. Fast's original factor was 0.977, so its limit was 10 seconds.
The Live wall time includes the old console cleanup contamination described
above; no lower post-fix Live tier time is claimed.

The three Fast skips require the absent optional Meshy pack. The Live skip was
the existing P660 15v15 opt-in (`ALLEGIANCE_LIVE_P660_SHOWCASE` unset); it is
explicitly enabled for supplemental isolated checks and in the mini instructions.

The first sweep's coverage checker initially compared deferred-theory containers
with their expanded rows. All raw TRX results were retained. The corrected
framework records the expected runtime row names, and Core's 4,793 actual Fast
rows match that inventory exactly. The post-run evidence audit reconstructs
accurate tier counts from the original TRX without a second full execution.
The original summary/errors are retained as original observations.

The corrected discovery path also succeeds with artwork/native-source paths
absent and CI's resource/quarantine exclusions applied (Core: 1,198 Fast entries
selected). This is a discovery check, not another full suite execution.

Final verification after the repairs:

- Whole solution: **0 warnings / 0 errors** ([build log](TT1_assets/build.log)).
- Changed P705 and P703 scenario rows: **3/3 isolated repeats**, then pass in the
  full sweep (P705 188.05 s total; P703 57.74 s total in that sweep).
- Meshy hygiene guard: **0.049 / 0.047 / 0.048 s**, all pass,
  versus **12.392 s** before; same tracked-file checks and assertions.
- Console stress cleanup: **0.197 / 0.184 / 0.198 s**, all pass,
  versus **8.177 s** before; same 200,000-line/drop assertions.
- Eight convention cases and 56 deferred topology rows: **3/3** after the final
  build; each runtime name and count matches discovery.
- P660 local 15v15 opt-in: **3/3**, explicitly enabled; about 22 s per repeat.
- Python infrastructure checks: **9/9, three times**; declared Fast and now run
  by Fast/all entry-point invocations. They are recorded separately from the four
  .NET game-project counts above; the suite also has the calibrated duration guard.
- Focused Fast entry-point gate: **65/65** (changed hygiene/convention/topology
  checks), exit **0**. The final entry-point smoke check also passes its Python
  infrastructure checks and selected convention cases.
- Current timing audit: **0 offenders**, using the original Fast TRX and the
  **worst** of the hygiene test's three repaired measurements. Original timing
  failures/outcomes remain visible separately in the audit.
- Showcase never-parks: **5/5**, times 100.89 / 95.42 / 95.83 / 95.59 / 101.16 s.
  The P744 base report also recorded **5/5**; no increase in the observed failure
  rate. Its full-sweep occurrence passed too.

The native-clock P695 two-minute target remains intentionally unmet, as explained
above. Mini timings still need measurement; the workstation Fast gate meets the
requested ten-minute target. No lower full-tier timings are claimed after repairs.

[Coverage and timing audit](TT1_assets/coverage-audit.json),
[isolated qualification ledger](TT1_assets/qualification.json),
[focused research timings](TT1_assets/focused-timings.json), and
[showcase results](TT1_assets/showcase.json) retain the evidence. Original TRX and
summaries are compressed under `TT1_assets/sweep/`; final inventories are under
`TT1_assets/inventory/`; focused/retry TRX are under `TT1_assets/focused/`.
The immutable original summary is `TT1_assets/sweep/original-summary.json.gz`.

The evidence audit can be reproduced from the archived artifacts without running
any test:

```bash
python3 docs/reports/TT1_assets/audit.py \
  --results docs/reports/TT1_assets/sweep \
  --inventory docs/reports/TT1_assets/inventory \
  --repairs docs/reports/TT1_assets/guard-repairs.json \
  --perf-recheck docs/reports/TT1_assets/p705-perf-recheck \
  --output /tmp/tt1-replayed-audit.json
```

The first sandbox restore attempted to write NuGet's HTTP cache outside the
writable roots and failed with NU1900. Pointing NUGET_HTTP_CACHE_PATH at
`/tmp/tt1-nuget-http` resolved it; package auditing and warnings-as-errors remain
enabled. Subsequent whole-solution builds pass with zero warnings and zero errors.

## Mini runner change for Claude

There is no `tools/test-runner/` stack in this repository, so no external mini
configuration was edited. Apply this exact setting in the mini stack's `.env`:

```dotenv
TIERS=fast,live,perf
ALLEGIANCE_LIVE_P660_SHOWCASE=1
```

Replace the queued job's individual project-test loop with this one invocation
from the checked-out repository (RUN_DIR is that job's artifact directory):

```bash
./tools/run-tests.sh --tier "${TIER:-all}" --results-dir "$RUN_DIR/test-tiers"
```

The second setting enables the existing opt-in local 15v15 P660 proof in Live;
its skip guard is preserved. Do not wrap the runner in another per-project or
per-tier parallel loop. With no explicit queued TIER override it defaults to all. Keep the existing artwork/native path
environment available. Publish `test-tiers/summary.{json,txt}` and
`test-tiers/{fast,live,perf}/summary.{json,txt}` plus the TRX/log artifacts.
The runner's exit code is the job result. Post-merge and nightly runs use all.

The new delegate gate is documented in `docs/testing/TIERS.md`: build 0/0 →
new/affected tests three times in isolation → Fast once → showcase never-parks
five times when showcase/bots/tech are touched. Claude must apply that gate to
future briefs and the hermes footer, as requested; those external instructions
are not part of this checkout.

No commits or pushes were made. Changes remain in the working tree.

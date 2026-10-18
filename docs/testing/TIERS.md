# Test tiers

Use `tools/run-tests.sh --tier fast|live|perf|all [--parallel N]`. The default is
`all`; `scripts/run-tests.sh` forwards to the same entry point. The runner loads
`local.env`, builds the solution once, audits discovery, then writes TRX, logs,
P744 calibration, and `summary.json` / `summary.txt` under
`TestResults/tiers-<UTC timestamp>/`. Use `--results-dir <new-directory>` to choose
an artifact directory, or `--no-build` after an explicit solution build.
Fast also runs the Python runner's infrastructure self-checks (declared Fast),
with their log and timing result recorded separately from the four game assembly
counts. The suite itself must fit the same calibrated Fast limit.
Python 3, .NET 10 and a POSIX host are required. `DOTNET` can select the SDK binary.

| Tier | Purpose | Scheduling |
| --- | --- | --- |
| Fast | Unit, rules and short loopback tests; each stays below the calibrated 10-second limit | Four projects concurrently by default; `--parallel N` limits project concurrency. Existing xUnit collection rules remain in force. |
| Live | Full matches, research scenarios, real-time flight/keep-alive soaks and expensive corpus scenarios | Projects and xUnit collections sequentially |
| Perf | Timing/allocation budgets, crowded pipeline/broadcast measurements and GPU budgets | Sequentially, after an idle check and P744 calibration |

Every discovered xUnit case, including each theory row and SkippableFact, has
exactly one `Tier` trait. The shared discovery framework in
`tests/_shared/TierTestFramework.cs`, included through `tests/Directory.Build.targets`,
assigns `Tier=Fast` only when there is no explicit tier. Ordinary `[Fact]` and
`[Theory]` tests therefore require no additional annotation. Default traits are
visible to `dotnet test --filter 'Tier=Fast'` as well as the runner.

Tag longer scenarios or budgets explicitly:

```csharp
[Trait("Tier", "Live")]
[SkippableFact]
public void Whole_match_rotates_and_preserves_the_roster() { /* ... */ }

[Trait("Tier", "Perf")]
[Trait("Requires", "Gpu")]
[Fact]
public void Render_budget() { /* ... */ }
```

A class may carry a tier when all its tests belong together. Do not combine a
class tier with a different method tier: discovery rejects multiple values,
unknown values, and incorrect case. Split mixed classes by tagging methods.
`Requires=Artwork|NativeSource|MeshyPack|Gpu`, `Category`, and `Lane=LiveTiming`
remain independent. Tests using P744 PerfCalibration belong to Perf, including the P705 functional
match proof with calibrated progression deadlines.

The runner reads TRX durations and fails for every Fast case above
`max(10, PerfCalibration.Scale(10))` seconds, listing its name and duration in the
tier summary. Calibration uses the same shared workload/configuration as P744,
measured before launching concurrent Fast projects. The floor prevents faster
CPUs from imposing a limit below ten seconds. Failed and skipped cases are also
checked. Missing TRX, unrecognized results, missing/duplicate results, invalid
traits and process errors fail the run. The discovered baseline is partitioned
by tier, and execution must cover every selected case exactly once. Tests are
never reassigned automatically after a slow run: investigate the cause and
explicitly tag a long scenario, or fix a performance regression.

The P647 quarantine still runs at the end of each tier, sequentially. A failed
`Lane=LiveTiming` method is retried once in isolation, retaining the existing
policy: a passing retry is reported as a flake; a failing retry fails the run.
Retries have separate TRX and do not count toward the tier's unique case count.
Other failures are never automatically retried or hidden. An `all` run proceeds
through Fast, Live and Perf even if an earlier tier fails, and returns nonzero
if any tier fails.

Runner invocations share an exclusive lock at `/tmp/allegiance-test-tiers.lock`
(`TT1_LOCK_PATH` overrides it). This prevents overlapping runner invocations,
including different worktrees, from contaminating Perf. Direct `dotnet test`
commands cannot participate in this lock: keep the machine quiet for Perf.
The idle check requires two samples three seconds apart with one-minute load
average / available CPU count at most 0.8. It waits at most 60 seconds, then
fails visibly if the machine stays busy. `TT1_MAX_LOAD_PER_CPU` sets the threshold
for runner environments with a documented alternative idle policy.

`TEST_FILTER` appends a VSTest filter to every tier and quarantine/retry selection.
Public CI runs Fast with the existing corpus/quarantine exclusions plus GPU and
MeshyPack exclusions, and uploads the results even on failure. Local and mini
runs include those resource-backed tests when their resources are available;
normal corpus skip rules still apply when they are absent. The existing local
15v15 proof additionally needs `ALLEGIANCE_LIVE_P660_SHOWCASE=1`; set it for
complete mini/nightly Live coverage. Release checks are unchanged.

## Delegate gate

Build with zero warnings/errors → run new/affected tests three times in isolation
(any tier) → `tools/run-tests.sh --tier fast` once → run showcase never-parks five
times when showcase/bots/tech are touched. Live and Perf run on the mini after
merge and nightly. Follow the lane's existing flake policy when a suite failure
passes three isolated repeats; record it in `docs/TEST_FLAKES.md`.

Mini queued jobs default to `all`; its external stack should set
`TIERS=fast,live,perf` in `.env`, invoke the shared runner once, and expose each
tier's summary. This checkout has no `tools/test-runner/` stack; the exact adapter
command is recorded in `docs/reports/TT1_TEST_TIERS.md`.

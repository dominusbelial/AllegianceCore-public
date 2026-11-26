# P1087 — Rescue pairs and ranked ship updates

Base `c72a803d`. Work began at 23:05 UTC on November 26, 2026. Approximate elapsed times overlap where items share a build or proof.

## Brief items

| Item | Status | Elapsed | Evidence |
|---|---|---|---|
| 1. Rescue precondition and scratch lifetime | done | ~4 min | Capability and ordered callback tests, nested/throwing site proofs and P790 pass. |
| 2. Ranked prefix order | done | ~4 min | Reverse-drain proof, 1,800 differential enumerations per run and forward-walk deliberate break. |
| 3. Regression and differential tests | not done | ~9 min including base comparison and repeated classes | All seven deliberate breaks detected. Supplemental P928 golden already fails on base. |
| 4. Release measurements | done | 2 min of builds and timed processes | All 12 P835 processes passed their population guard. Both P863b processes completed three runs per arm. |
| 5. Report | done | ~30 min elapsed alongside the gates | Equivalence, deliberate breaks, all per-run measurements and flake triage below. |
| Light gate | not done | ~25 min including isolation | Builds green. Full Fast sweep and its isolated follow-ups completed. See exceptions below. |

## Rescue equivalence and list ownership

The gather still visits sector models in insertion order and admits a ship only
when it has a mission and side, is alive and has no station. It records the
lifepod hull bit and, on non-pods only, the rescue hull bit. A pod carrying
both bits counts only as a pod. These are necessary conditions for the current
`IsRescueCapablePair` XOR and partner-capability checks. The native friendly
ship capability branch is in `Igc/shipIGC.cpp:1023–1042`, including the first
pod/rescuer branch at lines 1023–1035. With a role absent, the old loop
could neither call the site nor change the shared latch. With both present,
the original pair loop still decides side, distance, callback acceptance and
both latches in (i, j) order, reading live ship state on each pair. Hull
contact, station, probe and carrier passes keep their existing paths.

The server wires `ServerHost.RescueShipEvent` to the synchronous
`FindFriendlyStation` lookup in `Server.Lifecycle.cs`. Other implementations
of `IIgcSite` can call back into the sector. Each invocation takes the spare
list out of its sector for the duration of the scan. A nested invocation
can gather its own membership even when the callback has removed ships.
A `finally` clears ship references and returns the list on early return,
ordinary completion or exception. The fewer-than-two-models exit precedes
the loan.

## Ranking equivalence

The default tuple comparer
compares the stored float deviation first and the negative insertion ordinal
second. An ascending drain therefore visits the lowest deviation first and
the latest ordinal first within each tie. All ordinals are distinct, even for
multiple occurrences of one ship. Reversing the drain gives the same descending
deviation and ascending ordinal as the old sort. Both use `Single.CompareTo`,
which orders all NaNs together below negative infinity and treats signed zeros
as equal. The ordinal breaks each such tie.

A zero limit retains nothing and visits all candidates in their original
order. One retains the earliest maximum. At or above the candidate count,
the whole input follows the prefix order and the tail is empty. Empty input
yields nothing. Each yielded prefix occurrence marks its own ordinal, so an
unselected occurrence of the same ship still receives its tail visit.
Both versions start work at the first iterator advance, drain the whole heap
before yielding, and never consult a deviation again during that enumeration.
The caller can reset a HEAVY ship or increment a LIGHT or skipped ship before
requesting the next one. Subsequent viewers see those writes in the same order.

## Workstation measurements

Intel Core Ultra 9 285HX, 24 logical CPUs, Ubuntu 26.04.1 x64, SDK 10.0.403.
Both revisions used the same local artwork, Release build, server GC and
disabled tiered compilation. Each benchmark ran serially. The base binaries
were built before editing the two production files. The following commands
were run on each revision with the repository's local artwork configuration.

```bash
export NUGET_PACKAGES="$PWD/.scratch/packages"
export NUGET_HTTP_CACHE_PATH="$PWD/.scratch/http"
export XDG_CONFIG_HOME="$PWD/.scratch/config"
dotnet build tools/P835Bench -c Release
dotnet build tests/Allegiance.Core.Tests -c Release
export DOTNET_gcServer=1 DOTNET_TieredCompilation=0
for run in 1 2 3; do
    for ships in 200 400; do
        dotnet tools/P835Bench/bin/Release/net10.0/P835Bench.dll "$ships" 240 60
    done
done
dotnet test tests/Allegiance.Core.Tests -c Release --no-build \
    --filter FullyQualifiedName~Surge_profiles_three_alternating_runs_per_arm \
    --logger 'console;verbosity=detailed'
```

### Living-fleet simulation

Each process retained all 200 or 400 ships throughout the sustained phase.
Times are ms per pass over the same 500 measured updates after warm-up.
The rescue bucket includes the sector's other rescue/landing work. A displayed
0.00 is below the benchmark's two-decimal reporting precision. Allocation is
the process-wide delta in KiB/pass including fixture and profiler upkeep.

| Ships | Run | Base rescue mean | Base update mean | Base update p95 | Base KiB/pass | Changed rescue mean | Changed update mean | Changed update p95 | Changed KiB/pass |
|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|
| 200 | 1 | 0.09 | 1.19 | 1.78 | 59.6 | 0.00 | 1.09 | 1.70 | 55.5 |
| 200 | 2 | 0.09 | 1.18 | 1.77 | 59.6 | 0.00 | 1.08 | 1.68 | 55.5 |
| 200 | 3 | 0.09 | 1.18 | 1.79 | 59.6 | 0.00 | 1.08 | 1.68 | 55.5 |
| 400 | 1 | 0.30 | 1.97 | 2.75 | 88.7 | 0.00 | 1.68 | 2.39 | 80.5 |
| 400 | 2 | 0.33 | 2.00 | 2.76 | 88.7 | 0.00 | 1.66 | 2.37 | 80.5 |
| 400 | 3 | 0.30 | 1.97 | 2.71 | 88.7 | 0.00 | 1.67 | 2.42 | 80.5 |

Across-run medians at 200 ships fell by 0.10 ms for the update mean and
0.10 ms for p95. At 400 they fell by 0.30 ms and 0.36 ms, respectively.
The mixed-role fallback retains its original worst-case pair count. This
all-fighter workload skips 19,900 and 79,800 potential pairs per pass.

### Surge projection

Each revision ran the unchanged 400-viewer surge test. It warms 30 ticks,
measures 200, flushes the publisher each tick and alternates its historical
reference and production arms three times. The table compares the production
arm (`arm=after`) across revisions. The ranked column is the mean of
`step:broadcastShipsUpdate`. Projection includes flush. All figures are ms.

| Run | Base ranked mean | Base projection mean | Base projection p95 | Changed ranked mean | Changed projection mean | Changed projection p95 |
|---:|---:|---:|---:|---:|---:|---:|
| 1 | 15.7858 | 19.3517 | 20.0962 | 15.5979 | 19.0274 | 19.9476 |
| 2 | 15.9004 | 19.4411 | 20.2728 | 15.3950 | 18.8280 | 19.5175 |
| 3 | 15.8982 | 19.4255 | 21.5511 | 15.5089 | 19.0319 | 19.7915 |

The median ranked mean fell 0.39 ms (2.4%). The median projection mean fell
from 19.43 to 19.03 ms and its p95 from 20.27 to 19.79 ms. These are
workstation replay measurements at fixed population.

## Regression tests and deliberate breaks

Seven new Fast cases passed on the restored code. The rescue tests call the
same pass as the sector update and use its return value to observe entry into
the ordered scan. This distinguishes skipping 79,800 fighter pairs from
visiting and rejecting them. The no-rescuer case includes a dual-bit pod
and docked, unsided, dead and unplaced rescue-capable ships.

| Test | Temporary break | Observed failure |
|---|---|---|
| Fighter fleet | Return only if both roles are missing | Reported a pair scan. |
| Pod with no eligible rescuer | Same weakened guard | Reported a pair scan. |
| In-range rescue | Exclude exact sphere-radius equality | No queued rescue. |
| Competing rescuers | End the inner walk on the site's first refusal | Ordered callbacks and winners diverged. |
| Nested pass | Leave the active list available to a nested gather | The outer scan lost its second pair. |
| Throwing site | Omit the list clear on return | A departed pod displaced its replacement in the next rescue. |
| Ranked prefix and tail | Walk the drained list forwards | The emitted sequence diverged from the old algorithm. |

The ranking oracle keeps the original heap, sort and ordinal marking in the
test project. It checks 100 seeds, six limits and three successive viewers
per seed/limit, comparing the full ID sequence and each ship's resulting
deviation bits. The viewers mutate deviations between yields. Every list
contains a repeated reference. Each first viewer starts with ties, signed
zeros, two NaN bit patterns and both infinities. Later viewers inherit the
previous updates. Each deliberate-break run was followed by restoration of
the production code.

### P928 supplemental golden

The ordinary 500-tick golden passes. The supplemental golden first fails on
the event digest at tick 33, with equal state, RNG and event counters. The
committed digest encodes a station-damage callback for station 4000 from
ship 1120, damage type 2. The actual event buffer is empty. Both belong to
side zero in this fixture. The golden dates to P951. P1007 moved the
station callback below the immunity and shield checks in
`src/Allegiance.Core/Sim/Station.cs` (`f612f1d9`).

Three isolated Release runs at a fresh export of `c72a803d` reproduce this
mismatch. The patch reproduces it on the first Release run, all three
isolated retries, and three Debug runs. A full comparison using the existing
proof tool returned identical supplemental CSVs across all 500 ticks, all
columns, including the header. Both SHA-256 digests are
`6d613898eac58a6a9d0433a27cebcc19ca5da40022c5d478f03fdbc74f48b65b`.
Compared with the historical golden, the only differing column is the event
digest (153 of 500 ticks). The ledger lists the supplemental case under the P1055 master
gate. Making that historical golden green would require an out-of-scope
station-rule or golden change. [Flake ledger](../TEST_FLAKES.md)

```bash
dotnet test tests/Allegiance.Core.Tests -c Release --no-build \
    --filter FullyQualifiedName~P928AllocationTests
dotnet run -c Release --project tools/p928-allocation-proof -- --supplemental "$trace"
```

## Verification

The seven new tests each passed in three separate Debug test processes with
an exact-method filter. All 21 executions ran without skips. The following
existing classes also passed each of their three class-filtered runs.

| Coverage | Classes | Cases per run |
|---|---|---:|
| Contact, callbacks and snapshots | P790PodRescueOnContactTests, P754CallbackTests, P928CollisionScratchTests, P928TraversalTests | 21 |
| Live rescue, ejection, pickup and collision | LiveRescueTests, LiveInSpaceEjectPodAliveTests, P590PodRescueTests, ShipShipCollisionTests | 20 |
| Wire goldens and rescue messages | FedPacketGoldenBytesTests, ObjectSpottedPlayerRescuedWireTests | 16 |
| Cadence, clash and surge budgets | UpdateCadenceTests, P863ClashBudgetTests, P863bSurgeBudgetTests | 30 |

The ordinary and supplemental P928 goldens ran together. Their separate
results appear above. Class filters included all theory rows and tiers.

Both Debug and Release solution builds reported zero warnings and zero errors.
All 156 projects built by the tools gate also reported zero warnings and zero
errors. NuGet restored from local scratch caches and `XDG_CONFIG_HOME`
pointed to writable scratch. The following commands reproduce the gate.

```bash
dotnet build AllegianceCore.slnx
dotnet build AllegianceCore.slnx -c Release
tools/build-tools.sh
tools/run-tests.sh --tier fast --parallel 1 --results-dir .scratch/fast
```

The Fast runner selected all seven projects and discovered 16,155 Fast cases,
including all seven P1087 tests. Projects ran serially to reduce contention
for the calibrated 10-second case limit.


### Fast sweep and isolation

The run finished in 1,057.5 seconds with 16,145 passes, five skips and five
assertion failures. Four cases exceeded the calibrated 10.000 s limit,
including the failing station-missile case. All 19 quarantine cases and all
nine runner infrastructure checks passed. Each of the eight flagged methods
then ran in three fresh, method-filtered Debug processes with every theory row
included. Each cell below gives passes/total and the maximum case duration
for that process. All eight methods have matching entries in the
[flake ledger](../TEST_FLAKES.md).

| Method | Sweep observation | Three isolated runs |
|---|---|---|
| `P890OwnAimLoopbackTests.Turning_and_firing_preserve_client_aim` | Native skirmish sustained aim drift. | 4/4 (0.548 s) / 4/4 (0.556 s) / 4/4 (0.546 s) |
| `SpectatorFrameLiveTests.Translates_ARealWorldAndARealKillShip` | Victim survived the 4,800-tick window. | 1/1 (1.882 s) / 1/1 (1.922 s) / 1/1 (1.911 s) |
| `MiningStateWireTests.Loopback_own_mining_state_reaches_client_and_clears_on_departure` | Departure update missing from mining receipts. | 1/1 (0.558 s) / 1/1 (0.560 s) / 1/1 (0.551 s) |
| `P750NativeAsteroidTypesTests.Research_showcase_rocks_carry_native_models_and_no_rock_names` | Passed in 10.085 s. | 1/1 (1.559 s) / 1/1 (1.419 s) / 1/1 (1.403 s) |
| `P777WarpExitWobbleTests.Npc_exit_touch_rate_is_measured` | Passed in 10.235 s. | 1/1 (9.245 s) / 1/1 (9.249 s) / 1/1 (9.268 s) |
| `P999CivStrategyTests.Every_profile_is_reachable_in_catalog_and_actual_side_buy_gates` | Civilization 5 passed in 14.706 s. | 10/10 (4.184 s) / 10/10 (4.267 s) / 10/10 (4.178 s) |
| `StationCombatLiveTests.Cross_side_missile_hits_station_shield_exact_fraction_armor_untouched` | Cross-side missile missed the station, 17.571 s. | 1/1 (0.510 s) / 1/1 (0.500 s) / 1/1 (0.482 s) |
| `P1051ResumeAuthenticationTests.Valid_WS_resume_retires_both_prelogon_deadlines_and_restores_human_classification` | Resume returned a null ship ID instead of 8096. | 1/1 (3.592 s) / 1/1 (3.575 s) / 1/1 (3.633 s) |

Isolated total: 60 passed, 0 failed. The full sweep returned exit code 1. The logs and TRX files are in the local scratch directory.

# P659 — per-viewer ship pose cadence

Implemented on `codex/p659`, based on `integ/rig-next-match` at
`385fbb25f0a7a197c76dcb2d02ec9c7dffc563d4`. Changes remain in the working tree;
no commits or pushes. No rig/221/200v200 sessions were run.

## Design and native evidence

`broadcast.minUpdateHz` defaults to **2 Hz**. Set it to **0** to retain the
pre-P659 ranked budget, including P632's density scaling. It applies in both
`broadcast.shipUpdateModel` modes and both serial/parallel builders. Negative
and non-finite rates fail before other subsystems register profiler callbacks;
rates above the configured broadcast tick rate are capped to that rate. The resolved rate is printed at boot.

| Rule checked in the native checkout | Source under `~/Development/Allegiance/src/` | Treatment |
| --- | --- | --- |
| Fixed heavy/light quotas of 2; bandwidth-adaptive code disabled | `FedSrv/FedSrv.CPP:3836-3846` | Existing ranked path retained |
| At most two candidates take the all-heavy fast path | `FedSrv/FedSrv.CPP:3860-3864` | Retained |
| Sort by deviation, reset heavy deviation, accumulate light/skipped deviation | `FedSrv/FedSrv.CPP:3918-3950` | Retained; floor serves never modify deviation |
| Target promoted first; self and promoted target excluded from remaining slots | `FedSrv/FedSrv.CPP:4141-4165` | Retained |
| LIGHT contains only ID, state and controls; HEAVY adds time, position, orientation and motion | `Igc/igc.h:2423-2442`; `Igc/shipIGC.cpp:3097-3118` | Only HEAVY resets pose age or satisfies the floor |
| Compact fractions have six bytes, including ore | `Igc/igc.h:2352-2360` | HEAVY is 39 bytes; the native 38-byte comment predates ore |
| Heavy and light are combined and sent without a delivery guarantee | `FedSrv/FedSrv.CPP:4179-4195` | Same packet/coalescing key and send policy |
| Native heavy update accepts a ±1.5 s simulation-time window | `Igc/shipIGC.cpp:3110-3115` | Background to the era constraint, not a wall-time cadence guarantee |

The floor is an intentional compensation for sparse poses on the port's
renderer. Each viewer owns a stable round-robin cursor over currently visible,
flying remote ships in the viewed sector. Same-side ships qualify; enemies
must have `SeenBySide(viewerSide)`; admin spectators retain their existing
all-ships visibility. Candidate selection already excludes dead, docked and
boarded ships. Existing legacy ranked visibility rules remain unchanged.

For `V` visible remote ships, rate `N`, and broadcast rate `T`, each tick visits
`ceil(V / max(1, floor(T/N)))` successive cursor slots. A ship already receiving
a ranked HEAVY that tick consumes its slot without a duplicate. Remaining
slots append HEAVY records after the ranked entries. Native LIGHT records are
kept, even if that ship also receives a floor HEAVY; this preserves ranked
bytes/order/deviation. A full sweep completes within `floor(T/N)` ticks.
An additional deadline check catches up overdue ships after population churn
or irregular ticks. The cursor advances independently of ranked serves.

The clock is monotonic wall time, independent of simulation time dilation.
No scheduler can deliver during a stalled sim thread or through a blocked
connection: the bound is `1/N` plus tick/transport scheduling tolerance on a
healthy connection. Catch-up happens on the next broadcast. Server age measures
built/enqueued HEAVY snapshots, not TCP acknowledgements. Existing latest-wins
coalescing can supersede poses for a slow receiver; the loopback check also
measures decoded received snapshots to cover the healthy transport path.

## Metrics

Enable the existing tick profiler, for example `profiler.enabled=true`.
Exactly one `[update-age]` line is emitted per profiler window, after that
window's tick report. It contains:

- Per-band `n/p50/p95/max` seconds for 0–1, 1–3, 3–6 and 6+ km. Samples are
  visible viewer/remote-ship pairs, taken **before** the current tick's sends.
- `visiblePairs`, `older1s`, `older3s`: counts at the last broadcast snapshot.
  The same subject seen by two viewers is two pairs.
- `older1sSamples`, `older3sSamples`: threshold-exceeding observations across
  the entire window; `neverPose` counts observations awaiting their first pose.
  Newly visible subjects start their waiting clock at first visibility.
- `shipsBytes/shipsBps`: packed HEAVY/LIGHT stream bytes enqueued, including
  FedPacket framing. `floorExtraBytes/floorExtraBps`: exactly 39 bytes per added
  HEAVY record; existing packet headers and LIGHT are unchanged. TCP/IP/link
  overhead and other streams are excluded; `[egress]` remains the wider counter.

Histograms use fixed 50 ms upper-bound bins through 10 seconds, then an overflow
bin; overflow percentiles use the exact maximum, which is always preserved.
Sampling and warmed viewer tracking allocate **zero managed bytes**, verified
with allocation counters over 10,000 iterations. Viewer dictionaries/lists grow
only with population capacity; window formatting allocates at report time.
Visibility loss/departure prunes subject state; sector/side/viewer-identity
changes reset tracking, and ship ID reuse starts a new clock. Disconnect,
resignation and shutdown release viewer state. Departing viewers' window
histograms/bytes are merged into a fixed accumulator before their ship caches
are freed, so a disconnect cannot erase traffic from the current window.
Parallel workers own separate
viewer state; reporting happens on the sim thread after workers finish.

## Local load measurements

Three isolated repetitions of each combination, real loopback TCP, 100 ships
and four viewers, at 20 broadcasts/s. These are worst-viewer receive-age
percentiles/maxima; byte rates cover all four viewers and use decimal kB/s.
The 96 unseated subjects are enemies visible through real scanner checks, so
the port's same-side position stream cannot mask their sparse HEAVY poses.

| Model | Floor | Received p50 | Received p95 | Received max | HEAVY/LIGHT kB/s | Added floor kB/s |
| --- | ---: | ---: | ---: | ---: | ---: | ---: |
| native | 0 | 0.750 s | 1.500 s | 7.144–7.190 s | 17.76–17.80 | 0 |
| native | 2 Hz | 0.250 s | 0.497–0.498 s | 0.497–0.498 s | 47.57–47.71 | 29.79–29.93 |
| port | 0 | 0.600 s | 5.100–5.150 s | 7.738–7.785 s | 21.72–21.78 | 0 |
| port | 2 Hz | 0.200 s | 0.498–0.500 s | 0.498–0.510 s | 50.60–50.63 | 28.80–28.90 |

All floor-on viewers received all 99 remote ships; the largest received
inter-pose tick gap was **10 ticks** in every floor-on run. Every server-side
band's maximum was at most 0.501 s, and the oldest received pose across all
floor-on runs was 0.510 s. Largest measured tick spacing was approximately
50–52 ms. The floor-off native runs reached 145 ticks of pose age; port runs
reached 156–157 ticks. In each port floor-off repetition, at least one viewer
had received only 93 of its 99 remote ships even after warmup plus measurement.

At the last server sample, both floor-on modes had **0/396** pairs over 1 s
and **0/396** over 3 s. Native floor-off had 117–118 over 1 s / 2 over 3 s;
port floor-off had 97–112 over 1 s / 96 over 3 s. The local starvation was
especially severe in the nearer bands, demonstrating that shared deviation
state can starve a particular viewer independently of distance. The floor
covers all distance bands.

Representative server histograms (first isolated repetition; seconds):

| Model/floor | 0–1 km p50/p95/max | 1–3 km p50/p95/max | 3–6 km p50/p95/max | 6+ km p50/p95/max |
| --- | --- | --- | --- | --- |
| native / 0 | .8 / 2.3 / 7.164 | .75 / 1.5 / 2.427 | .75 / 1.5 / 1.836 | .75 / 1.5 / 1.831 |
| native / 2 | .2 / .497 / .497 | .2 / .496 / .496 | .25 / .496 / .496 | .25 / .496 / .496 |
| port / 0 | 2.65 / 7.1 / 7.589 | 1.3 / 5.7 / 7.589 | .5 / 1.05 / 1.640 | .5 / 1 / 1.232 |
| port / 2 | .2 / .5 / .501 | .2 / .5 / .500 | .2 / .5 / .500 | .2 / .45 / .501 |

The added payload is about **7.2–7.5 kB/s per viewer** at this population.
This addresses the pose-starvation symptom in a controlled 100-ship sector;
it does not measure combat, rendering quality or the production rig's capacity.
All twelve `RESULT_P659` and `[update-age]` pairs are preserved in
[P659_UPDATE_CADENCE_LOAD.txt](P659_UPDATE_CADENCE_LOAD.txt).

## Cost and recommended default

Keep **2 Hz** as the default. It gives each visible ship a new authoritative
pose every half second, while the existing ranking continues to provide faster
updates where it selects them. A zero rate is available for exact ranked-budget
comparisons. Fractional and other positive rates are supported; rounding the
sweep to whole broadcast ticks may slightly exceed the requested rate.

An upper bound for **added scheduled** payload at steady population and cadence is
`39 * T * ceil(V / floor(T/N))` bytes/s/viewer, before subtracting overlaps
with ranked HEAVY. Deadline catch-up can briefly add more records after a
stall. At 20 ticks/s, 389 remote ships and 2 Hz, this is
30,420 bytes/s/viewer, or 11.864 MB/s across 390 viewers (94.9 Mbit/s), in
addition to existing traffic. This is an analytic capacity estimate, not a
rig measurement. The cost grows with visible viewer/ship pairs, so keep the
window byte/queue counters visible when deploying at larger density.

## Verification and reproduction

Final-tree solution build: **0 warnings, 0 errors**.

Every new or directly affected test method ran **three times alone** after the
build: **114 invocations, 138 test cases, zero failures**. This includes all
four loopback model/rate combinations in each repetition.

| Isolated group | Cases per repetition | Result |
| --- | ---: | --- |
| New age, cadence, allocation and loopback tests | 18 | 3/3 each |
| Existing native-model and budget-live tests | 9 | 3/3 each |
| Existing parallel byte-identity/churn and ship-update live tests | 10 | 3/3 each |
| Existing tick-profiler tests (including report callbacks) | 9 | 3/3 each |

Final full-suite pass (assemblies run sequentially, without lane filters):

| Suite | Passed | Failed | Skipped | Result |
| --- | ---: | ---: | ---: | --- |
| Core | 4,187 | 1 | 0 | Existing conquest wire-timing flake; 3/3 alone |
| ClientCore | 893 | 0 | 1 | Passed; existing opt-in P660 showcase skip |
| ClientCore.MonoGame | 2,120 | 0 | 3 | Passed; existing optional Meshy-pack skips |
| Lobby | 12 | 0 | 0 | Passed |

Core's one failure was
`ConquestTerritoryLiveTests.Conquest_win_in_one_mission_does_not_leak_to_another`:
a delayed `GAME_STATE` packet violated the test's empty-receipt assertion.
It passed **three consecutive method-filtered runs** on the same final tree,
meeting the brief's load-flake rule. All P659 tests passed in the full Core run. The other three suites finished
with zero failures. The four existing skips are the explicitly opt-in P660
15v15 showcase and three optional Meshy content-pack checks; no P659 tests
were skipped.
The earlier full pass also surfaced three existing wire-timing tests (two
`LiveRipcordTests` and one `P590PodRescueTests`); each passed 3/3 alone. Details
are recorded in [TEST_FLAKES.md](../TEST_FLAKES.md). No new quarantine or test
skips were introduced.

The first Core pass caught a real rejected-boot callback leak during development.
Cadence validation now precedes other profiler callback registrations, and the
invalid-config test forces a profiler report after rejection to reproduce that
interaction in isolation. Both the new regression and all nine existing profiler
tests passed 3/3 alone and in the final full Core pass.

The new tests cover synthetic crowded histograms, threshold counts, overflow,
zero allocations, adversarial ranked starvation at 2/3/5 Hz, exact byte deltas,
unchanged ranked entries/deviation, zero-floor byte identity with diagnostics
on/off, visibility loss, ID reuse, cache pruning, departed-viewer window
accounting, profiler window resets, and reports after rejected boots.
The existing native-budget and budget-live tests now explicitly select
`MinUpdateHz=0`, preserving their original budget contracts. Existing serial/
parallel byte-identity, join/leave churn and live ship-update tests are also
included in the isolated checks.

The loopback test is **not environment gated**: it runs through real TCP with
four viewers plus 96 stationary, unarmed enemy ships in one sector, after a
2-second warmup and for a 6-second measurement per mode/rate. The viewers use
real scanner visibility; all 99 remote ships per viewer must be visible.
It checks received HEAVY tick gaps and wall-time age, including ships that have
not yet received a pose. At 2 Hz the tick bound is 12 ticks (10 expected plus
2 ticks tolerance), and the wall bound is 0.5 s plus the measured largest tick
gap and 100 ms for asynchronous local TCP scheduling. Floor-off runs measure
starvation without requiring the starved ships to have been updated.

Use the workstation path overrides and a writable NuGet HTTP cache:

```bash
set -a
. ./local.env
set +a
export NUGET_HTTP_CACHE_PATH=/tmp/p659-nuget-cache
dotnet build -m:8 -v:q
dotnet test tests/Allegiance.Core.Tests --no-build --filter 'FullyQualifiedName~UpdateCadenceLoopbackTests'
```

The full-suite commands used the same environment, sequentially:

```bash
dotnet test tests/Allegiance.Core.Tests --no-build --nologo -v:m
dotnet test tests/Allegiance.ClientCore.Tests --no-build --nologo -v:m
dotnet test tests/Allegiance.ClientCore.MonoGame.Tests --no-build --nologo -v:m
dotnet test tests/Allegiance.Lobby.Tests --no-build --nologo -v:m
```

Raw per-mode loopback results are written to `/tmp/p659-{native,port}-{0,2}.log`.
The default NuGet HTTP cache is read-only in this lane; redirecting its cache
allows the normal package restore/audit and build to complete.

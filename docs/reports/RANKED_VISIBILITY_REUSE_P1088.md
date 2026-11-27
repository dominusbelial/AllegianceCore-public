# P1088 — Ranked ship update visibility reuse

Base `83703eac`. Work began at 23:59 UTC on November 26, 2026. Elapsed times overlap where items share builds or evidence.

## Brief items

| Item | Status | Elapsed | Evidence |
|---|---|---|---|
| 1. Profile first | done | ~2 min | Release surge profile, 80,000 viewer passes (below). |
| 2. Fuse candidate walk | done | ~1 min | Pooled projection consumes Begin’s ordered visibility. Four differential cases passed three isolated runs. |
| 3. Stamp access | done | ~1 min alongside item 2 | Short-lived dictionary refs in Begin, MarkPose and SelectFloor. Clock and identity comparisons passed three isolated runs. |
| 4. Exactness | done | ~7 min alongside tests | Per-viewer differential and matching independent base/candidate surge transcript. |
| 5. Tests | done | ~8 min including implementation and isolated runs | All four Fast theory rows, nine deliberate breaks and three passes of every requested regression class. |
| 6. Numbers | done | ~1 min of benchmark runtime | Three uninstrumented production runs per revision. Only server projection changed. |
| 7. Report | done | ~33 min elapsed, overlapping other items | Profile, equivalence, deliberate faults, six benchmark runs, gate exceptions and follow-up below. |
| Light gate | not done | ~30 min including triage | Builds and tools green. The one Fast sweep returned 1. All three flagged methods passed three isolated runs. |

## Baseline profile

The Release P863b surge production arm ran with 400 viewers, server GC
(asserted inside the test process), tiered compilation disabled, a 2 Hz
floor, 30 warmups and 200 measured ticks. Temporary stopwatch probes
accumulated at viewer boundaries. Times below are per viewer, averaged
over 80,000 viewer passes including the death/relaunch ticks.

| Stage | Mean µs/viewer | Visits over 200 ticks |
|---|---:|---:|
| Begin, including filter, stamp read/write, pre-send age sample and prune | 26.10 | 31,976,040 candidates, 31,896,080 visible |
| Pruning alone (included above) | 4.93 | 31,899,268 dictionary entries |
| Second visibility filter and ranking-list construction | 2.60 | 31,976,040 candidates, 31,816,140 retained |
| Heap selection, LIGHT capture and ordered deviation writes | 5.72 | 31,816,140 ranked candidates, 159,960 LIGHT records |
| Parent check, ranked HEAVY marking and floor selection | 3.96 | 31,896,080 floor candidates |
| HEAVY capture/cache lookup and relative record write | 2.42 | 4,291,635 records |

Within Begin, a nested timer around stamp lookup, identity check, generation
write and dictionary assignment sampled every fortieth ship ID. It recorded
38.29 ms across 797,600 updates (48.0 ns per update, including stopwatch
overhead). This and the measured Begin cost warranted testing in-place access.
The uninstrumented before/after runs below measure the combined change.

## Local measurements

Uninstrumented production arm of the unchanged P863b surge perf test. Each
revision ran the fixture's alternating historical DTO and pooled replays.
Rows use only its pooled arm (labeled `after` inside each revision).
Same local artwork, SDK 10.0.403, server GC, tiered compilation disabled,
400 ships and viewers in one sector, 15 configured builders, 2 Hz floor,
30 warmup and 200 measured projection/flush ticks.

| Revision | Run | Ranked mean ms | Projection mean ms | Projection p95 ms | Process MiB / 200 ticks |
|---|---:|---:|---:|---:|---:|
| Base | 1 | 15.6434 | 19.1897 | 20.1114 | 132.090 |
| Base | 2 | 15.7027 | 19.1735 | 19.9816 | 132.062 |
| Base | 3 | 15.5046 | 18.9976 | 19.9374 | 132.027 |
| Change | 1 | 10.2028 | 13.7923 | 15.4930 | 129.048 |
| Change | 2 | 10.1156 | 13.6588 | 15.4466 | 128.946 |
| Change | 3 | 10.1680 | 13.6395 | 14.5869 | 128.981 |

The median ranked mean fell from 15.6434 to 10.1680 ms (35.0%). Median
projection mean fell from 19.1735 to 13.6588 ms (28.8%), projection p95
from 19.9816 to 15.4466 ms (22.7%), and allocation from 132.062 to
128.981 MiB (2.3%). Allocation is process-wide. The 3.081 MiB difference
is consistent with removing one interface enumerator per viewer per tick.
The measurement includes publisher flush and excludes simulation and sockets.
The production diff is confined to the server's broadcast projection, so
the conditional P835 simulation benchmark does not apply.

## Equivalence

`Sector.GetShipUpdateCandidates` supplies a stable sector insertion order.
`ViewerPoseCadence.Begin` keeps that order while excluding self and applying
the same `ShipPoseVisible(self, subject, admin)` predicate as the ranked
sender. The read-only span covers exactly the list for this viewer and pass.
The sender still promotes a visible target first, reserves its quota even
when a resolved target is hidden, and excludes that target only when
building its ranking input. Begin continues to track a visible target for
age sampling. The small-sector shortcut still uses the original sector
count. With tracking disabled, the sender still scans the original list.

Equal ranking inputs give the same heap ordinals, HEAVY resets, LIGHT
increments and skipped-ship increments. Same-sector viewers execute in
order under either builder. Parent insertion and LIGHT removal precede
ranked HEAVY marking. The fair sweep reads the same visible list, quota,
cursor and wall time, appending the same identities. The selected ships
feed the existing encoders, byte counters and broadcast telemetry in the
same order. These are existing adaptations of `FedSrv/FedSrv.CPP:3923–3950,
4141–4188` and the P891 source slot (`FedSrv/fsship.cpp:918–929`,
`clintlib/appmsg.cpp:805–814`).

The stamp key is a 16-bit ship ID. Begin acquires one dictionary slot for
each visible identity. A missing entry or a different Ship object at that ID
initializes the whole stamp, including optional tier fields. The ref ends
before the next insertion and before generation pruning. The optional
classification delegate runs before acquiring the ref. Cached interest
classification only reads server interest state and updates the stamp's
membership fields. MarkPose modifies an existing slot only. Each floor
candidate already has a stamp from Begin, so selection records its HEAVY
pose through that same slot. Pre-send sampling, view/side resets and stale
generation removal retain their original boundaries.

## Differential and verification

For each viewer the differential fixture runs the legacy DTO sender with the
original full visibility scan and the floor disabled. It saves the resulting
packet and deviation vector, restores the input deviations, then runs the
pooled sender. A test-only copy of the base cadence logic independently
computes membership, clocks, generation, pre-send age bands and the floor
suffix. Appending that suffix to the reference packet through the existing
codec supplies the expected complete bytes, including the CRC. The test
compares both ordered ID lists, the floor suffix, stamps, age and byte
counters, and the full deviation vector at every viewer boundary.

A seeded shuffle changes viewer order within each tick. Two sectors include
cloaked enemies, a gunner, a low-deviation parent, a spectator camera,
out-of-sector and hidden targets, a side change, an immediate identity
replacement and a replacement after two absent passes. Population crosses
the small-sector boundary and ships move between sectors. Equal times, exact
half-second deadlines and stalls exceeding three seconds exercise the clocks.
Each of the four floor/profiler combinations runs 144 viewer comparisons and
uses both builder modes.

The unmodified surge test independently compared every viewer's frames,
delivery keys, guarantee bits, order and per-tick deviations. A separate
server compiled from the base revision produced the same transcript
digest as the changed build. Each capture covered 1,153,729 packets,
312,723,854 bytes and 80,000 deviations, including all 40 kills and
relaunches and 64 missile lifecycles. SHA-256 of delivery metadata and
payloads was `555F054F53BAD17DC4C5643C2B1B6D74C25DAD4BCEB6B5377EC4A8CB9F6A89CA`.
The base capture is in `base-transcript.log`.

### Deliberate breaks

Each scratch server build changed one rule. The same four differential
cases ran against each build. Every build compiled and every injected
fault produced an assertion failure.

| Fault | Failing cases / 4 | First observed difference |
|---|---:|---|
| Put the target back in the ranked list | 3 | Deviation vector |
| Choose the small-sector branch by visible count | 3 | HEAVY reset versus LIGHT increment |
| Admit self with cadence off | 1 | Deviation vector |
| Suppress the turret-parent slot | 4 | Missing final ranked HEAVY ID |
| Reuse a stamp solely by numeric ID | 3 | Replacement object's stamp |
| Retain stale generations | 3 | Tracked count |
| Keep stamps and cursor across a side change | 3 | Pose clocks |
| Sample every pre-send age as zero | 3 | Age band or threshold count |
| Reset the fair cursor to zero each pass | 2 | Served stamps |

The isolated builds and fault logs are under `.scratch/p1088/`.
The product builds contain the tested implementation.

### Isolated runs

Each class or named telemetry method below passed in three fresh Release
test processes. All runs had local artwork. All four P1088 theory rows were
also discovered in Fast. The original regression classes are unchanged.

| Coverage | Cases in each of three runs |
|---|---:|
| `P1088RankedVisibilityTests` | 4 |
| `UpdateCadenceTests` | 12 |
| `UpdateAgeMetricsTests` | 2 |
| `P1087RankingTests` | 1 |
| `FedPacketGoldenBytesTests` | 4 |
| `P891BoardedPoseTests` | 6 |
| `InterestTierTests` | 18 |
| `InterestTierCombatTests` | 1 |
| `P813aDistanceTierTests` | 4 |
| `P990BroadcastLaneTelemetryTests` | 15 |
| `P863ClashBudgetTests` | 12 |
| `P863bSurgeBudgetTests` | 6 |
| `LightHeavyShipsUpdateCodecTests` | 5 |
| `Wire_transcript_is_byte_identical_and_counters_equal_transport_bytes` | 1 |
| `Update_age_reports_sim_ms_percentiles_against_the_recorded_transcript` | 1 |
| Total | 92 |

The three changed-build surge captures matched the independent base digest.
The class-filtered logs are `iso-*-{1,2,3}.log`; the supplemental codec and
telemetry logs have the `extra-` prefix.

### Reproduction

Use the repository's local artwork settings and local NuGet sources with
`NUGET_PACKAGES`, `NUGET_HTTP_CACHE_PATH` and `XDG_CONFIG_HOME` under
`.scratch/`. The benchmark command on each revision was

```bash
DOTNET_gcServer=1 DOTNET_TieredCompilation=0 dotnet test tests/Allegiance.Core.Tests \
  -c Release --no-build --filter FullyQualifiedName~Surge_profiles_three_alternating_runs_per_arm \
  --logger 'console;verbosity=detailed'
```

The instrumented baseline used a separate build of that same replay,
running one production arm. Its counters bracketed Begin, rank-list fill,
heap consumption, ranked marking plus floor selection, and the HEAVY
record loop. Pruning had a nested timer. The timestamps and visit counts
were accumulated after each viewer or loop, with nested stamp timing on
the sampled viewer IDs. `profile.py`, `profile.log`, `bench-base.log`
and `bench-change.log` are in the local scratch directory.

## Light gate

One local-only instruction was breached. Despite the local project restore
source, the initial solution build's nested SDK tool restore consulted
NuGet for tool version metadata. Its log reported an available update, and
the initially empty scratch HTTP cache gained a service index. The CLI
configuration was then restricted to the local feed and outbound HTTP
proxies were directed to an unused loopback port. All replay, verification
and mutation workloads ran on this workstation. The rig, VPS and lanes
were never contacted.

Debug and Release solution builds each completed with zero warnings and
zero errors. The first tools pass overlapped scratch compilation through
shared project references. One compiler temporarily lost the Update reference
assembly. After switching scratch builds to detached assembly references,
a fresh `tools/build-tools.sh` built all 156 projects with zero warnings
and zero errors. Final solution rebuilds also passed with zero warnings and errors
(`final-debug.log` and `final-release.log`). Earlier build logs are `build-debug.log`, `build-release.log`,
`tools.log` and `tools-final.log`. The glossary link check validated
1,173 paths. The parity scorecard and whitespace checks passed.

The single Fast sweep used the Debug solution build, serial projects
(`--parallel 1`) and the tier runner's usual quarantine and timing checks.
Discovery found 16,159 Fast cases, including all four new theory rows.
The known P928 supplemental simulation golden remains outside this change.

The sweep completed in 1,025.5 s with **16,153 passed, one failed and
five skipped**. All nine infrastructure checks and all 19 quarantine cases
passed. Two passing cases exceeded the calibrated 10.000 s limit. The
runner returned exit code 1. Each flagged method then ran in three fresh
Debug processes with its full set of theory rows.

| Method | Fast observation | Three isolated runs |
|---|---|---|
| `WarpTransitServerTests.ShipInsideWarp_GetsTransited_AndPeerSeesSectorChange` | Only old-sector SHIP_STATUS receipts at the destination assertion (28 ms) | 1/1 each, 299 / 291 / 302 ms |
| `P999CivStrategyTests.Every_profile_is_reachable_in_catalog_and_actual_side_buy_gates` | Civilization 5 passed in 13.772 s | 10/10 each, civilization 5 displayed as 2 s each time |
| `P777WarpExitWobbleTests.Npc_exit_touch_rate_is_measured` | Passed in 10.099 s | 1/1 each, displayed as 9 / 8 / 9 s |

All three signatures have existing entries in the [flake ledger](../TEST_FLAKES.md).
The brief permits one Fast sweep, so its original result stands. Gate
receipts are in `.scratch/p1088/fast-run/summary.json` and `retry-*.log`.

## Follow-up on stamp layout

Measure resident bytes and cache misses for the current dictionary with
tiers both off and on. In particular, profile the remaining generation scan
separately from lookups and histogram sampling under steady visibility
and churn. The baseline pruning pass visited about 159,500 entries per
tick even though few expired. A compact floor-only record and separate
optional tier storage are candidates for a later paired experiment. Compare
retained capacity after departures, ID replacement clocks, identical age
histograms and packet transcripts on each workload.

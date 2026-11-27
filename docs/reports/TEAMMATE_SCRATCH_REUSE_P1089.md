# P1089 teammate scratch reuse

Base `83703eac`. Work ran from 00:32 to 01:13 UTC on November 27, 2026. Approximate item times overlap.

## Brief items

| Item | Status | Evidence and elapsed time |
|---|---|---|
| 1. Measure first | done | ~2 min. Instrumented 400-viewer join and two steady ticks, below |
| 2. Reuse scratch | done | ~5 min. Reuse, empty slots, burst capacity and exception cleanup passed in Debug |
| 3. Exactness | done | ~6 min. Four full serial/parallel comparisons and four pre-change SHA-256 pins passed |
| 4. Tests | done | ~9 min of deliberate breaks and focused checks. New tests, P991, both P863 classes and wire goldens passed three times |
| 5. Numbers | done | ~3 min of Release builds and timed runs across the work interval. All six production-arm measurements below |
| 6. Report | done | 41 min elapsed alongside implementation and gates. Measurement, equivalence, mutations and flake triage recorded below |
| Light gate | not done | ~32 min. Builds and required focused tests green. Full Fast exited 1. All three flagged Fast methods passed three isolated retries each |

## Base allocation by sub-step

Release, server GC confirmed, tiered compilation disabled. A temporary instrumented
copy of the P863b production arm used the same two sides of 200 and stopped after
its 30 warm-up ticks. Each construction or group of writes was bracketed with
`GC.GetAllocatedBytesForCurrentThread` on the thread doing the work. Worker
deltas were summed with interlocked adds. The result-shell row subtracts field
initializers timed inside its constructor. Units are bytes for the entire tick.

| Sub-step | All 400 join, tick 0 | Steady, each of ticks 28 and 29 |
|---|---:|---:|
| Peer snapshot, grouping and prewarm | 16,984 | 16,960 |
| Unused native position dictionary and backing arrays | 12,200 | 12,200 |
| Outer result array | 3,224 | 3,224 |
| 400 result objects | 54,400 | 54,400 |
| Three list objects per viewer | 38,424 | 38,400 |
| Three effect set objects per viewer | 76,800 | 76,800 |
| Three effect dictionaries per viewer | 96,000 | 96,000 |
| Two local player overlay sets per viewer | 51,200 | 51,200 |
| Own frames, status and cluster data in worker | 284,800 | 0 |
| Same-side player walk, payloads and collection growth | 60,137,600 | 0 |
| NPC walk (no seeded NPCs) | 0 | 0 |
| Ordered apply, including shared latch and delivery growth | 22,982,336 | 0 |

The steady constructor group alone costs 316,800 bytes per tick before the outer
array or position dictionary. The sub-step totals exclude task scheduling,
publisher encoding and profiler overhead. Whole-stage and process allocation
in the uninstrumented surge are reported separately below.

## Ownership, ordering and retention

The sim thread snapshots the same peers, mission/side groups and loadout cache.
Worker index `i` owns one reusable result for the duration of that phase.
The outer array follows the total seated roster size, even when native broadcast
order takes two differently sized snapshots in a tick. Only the active prefix
is read or written. A roster resize preserves the reusable prefix. A rotation
drops the array and the next broadcast after the last seat leaves has zero slots.

Each result allocates a packet list, port position batch, verbose log or effect
collection on its first write. The unconditional log list had no writers.
The export, position and NPC effect sets contain exactly the IDs the previous
builder added simultaneously to separate local overlays. The global latches are
read-only throughout the parallel loop. For a player export, the new local
`exported` value starts with global-or-pending membership and becomes true
only after that worker appends the identity packet. A missing name cannot open
the status gate. Position and NPC checks still consult their respective pending
sets. The loadout version lookup preserves its original global-then-pending
precedence. The port position dictionary is created only when the port batch
is enabled and interest tiers are disabled.

After the workers join, the sim thread sends each result's packets in insertion
order, emits its logs and applies the original effect sequence. The position
batch is safe to clear because `PackBroadcast` copies its message span.
The publisher owns each submitted packet independently. A `finally` clears the
entire active prefix, including partially built slots if a worker throws and
the unapplied suffix if a send throws. A failed build never reaches apply.
An apply failure keeps the previously applied prefix, as on base.

Each retained slot has at most nine collections, each capped at 64 entries of
backing capacity (including actual hash-table prime sizes). A larger collection
is released after that pass. Smaller lists and tables are cleared, as are the
live ship reference, status/cluster write flags and optional refresh tick.
The maximum retained slot count on the next broadcast is the seated population.
The serial builder and P991 comparison are unchanged. This ports no new native
rule. Existing status change and join rules come from `FedSrv/FedSrv.CPP:3690–3699`
and `FedSrv/fsmission.cpp:4303–4323`.

## A future change-list builder

The current builder still checks every same-side pair. At two sides of 200
that is 79,600 non-self player pairs per tick before NPCs. Measure how many
produce no packet, per-recipient gaps in roster knowledge, loadout and side
churn, NPC changes, and refreshes with P991 off. A change list would need
mission-local subject identity, stable subject order, recipient-specific initial
exports, a catch-up path for joins and reconnects, pending loadout repair and
the original staggered refresh schedule. Merge those inputs in the original
own/export/status/loadout/position-batch/NPC order before ordered delivery.
Cold joins still require the roster. Compare full per-peer transcripts through
cloak, ejection, restart, side switch, rotation and object-ID reuse before
evaluating any quiet-tick speedup.

## Transcript and memory proofs

The four Fast theory rows cross native/port poses with the P991 gate on/off.
The test serializes tick boundaries followed by the recipient ID, lane key,
guarantee bit, length and every payload byte in actual emission order.
It directly compares the complete changed parallel stream with the independent
serial stream. The second assertion compares its SHA-256 with the original
parallel builder on `83703eac`. The base also matched serial in all four
cases. The four expected hashes are in `P1089TeammateScratchTests.cs`.

Eight seats on two sides and a same-side NPC exercise cold exports, quiet ticks,
refresh deadlines, actual part replacement, a side switch, an NPC docking
and position updates. On the first publication of tick 103 the fixture queues a
peer departure through the transport site's real ingress callback. It drains
that event after the broadcast, verifies the seat was removed, and seats a
different connection and ship before tick 105. This preserves the production
timing rule that the sim thread changes the roster at drain.

Before the initial parallel build, one seeded live record has a missing sector.
Its ship and inventory are valid for snapshot/prewarm. The worker throws while
consulting the live sector after preparing the own-ship position. The test
requires the aggregate worker exception, zero published bytes, and empty
scratch, then repairs the live record and retries the same tick. All retry and
subsequent bytes are included in the base/serial comparisons.

The memory case inspects storage reachable from the actual server slots after
an 80-viewer all-allied port-mode cold burst. It checks every retained backing
capacity, empty collections and cleared ship references, distinct slots per
viewer, outer-array and result identity on a steady tick, then shrinks the
roster to two and zero. Direct capacity checks avoid including unrelated shared
latches and publisher memory in the retention limit.

### Deliberate breaks

| Temporary production edit | Observed regression |
|---|---|
| Leave the local export decision false after queuing an initial export | All four transcript rows differed from serial. |
| Omit the cleanup loop in the build/apply finally | All four exception paths retained a live ship in the failed build. |
| Raise the per-collection capacity limit to 1,024 | The 80-to-two mass-leave case found a packet list of capacity 256 after apply. |

Each break was built and tested separately and then removed.

## Workstation measurements

Intel Core Ultra 9 285HX, 24 logical CPUs, .NET SDK 10.0.403. Each revision
uses the unchanged P863b Release fixture with server GC, tiered compilation
disabled, 15 build workers, 400 viewers, two sides, 30 warm-up ticks and 200
measured ticks. The fixture alternates its historical and production arms
three times. Compare only the production arm (`arm=after`) across revisions.
The whole-stage allocation is the profiler's approximate all-thread delta.
Total allocation uses the fixture's precise process-wide delta. Projection
mean and p95 include the per-tick publisher flush. These are replay wall times.

```bash
export NUGET_PACKAGES="$PWD/.scratch/packages" NUGET_HTTP_CACHE_PATH="$PWD/.scratch/http"
export XDG_CONFIG_HOME="$PWD/.scratch/config"
dotnet build tests/Allegiance.Core.Tests -c Release
DOTNET_gcServer=1 DOTNET_TieredCompilation=0 dotnet test tests/Allegiance.Core.Tests \
    -c Release --no-build --filter FullyQualifiedName~Surge_profiles_three_alternating_runs_per_arm \
    --logger 'console;verbosity=detailed'
```

| Revision | Run | Teammate mean, ms | Teammate MiB/200 ticks | Projection mean, ms | Projection p95, ms | Total MiB/200 ticks | GC 0/1/2 |
|---|---:|---:|---:|---:|---:|---:|---|
| Base | 1 | 1.2826 | 72.3756 | 19.0300 | 19.6224 | 132.157 | 2/1/1 |
| Base | 2 | 1.3132 | 72.3289 | 19.0241 | 19.6533 | 132.025 | 0/0/0 |
| Base | 3 | 1.2781 | 72.3303 | 18.9244 | 19.8973 | 132.038 | 0/0/0 |
| P1089 | 1 | 1.2465 | 6.4387 | 19.0279 | 20.1602 | 66.322 | 0/0/0 |
| P1089 | 2 | 1.2079 | 6.4997 | 19.0857 | 20.1448 | 66.324 | 0/0/0 |
| P1089 | 3 | 1.3041 | 6.5776 | 19.4424 | 20.7991 | 66.341 | 0/0/0 |

Across-run medians, teammate allocation fell from 72.33 to 6.50 MiB
per 200 ticks (91.0%), and whole-replay allocation from 132.04
to 66.32 MiB (49.8%). The latter saves about
336 KiB per tick. The teammate mean moved from 1.283 to
1.246 ms. Projection mean moved from 19.02 to 19.09 ms and
p95 from 19.65 to 20.16 ms. The replay shows a clear allocation reduction
and no end-to-end speedup. Only the first base production-arm run collected.
These short collection windows do not establish a sustained GC rate.

## Verification

Both final solution builds (Debug and Release) finished with zero warnings and zero errors. All 156 projects in `tools/build-tools.sh` built successfully. The glossary link check passed. The focused runs used a new Release test process per filter per repetition and included every theory row.

| Filter | Passing cases per run (three runs) |
|---|---:|
| `FedPacketGoldenBytesTests` | 4, 4, 4 |
| `OwnShipCoalesceKeyTests` | 2, 2, 2 |
| `P1089TeammateScratchTests.Mass_leave` | 1, 1, 1 |
| `P1089TeammateScratchTests.Teammate_transcript` | 4, 4, 4 |
| `P863ClashBudgetTests` | 12, 12, 12 |
| `P863bSurgeBudgetTests` | 6, 6, 6 |
| `P991FlagOffWirePinTests` | 2, 2, 2 |
| `P991ShipStatusChangeGateTests` | 19, 19, 19 |
| `ParallelBroadcastByteIdentityTests` | 7, 7, 7 |
| `ShipStatusServerTests` | 6, 6, 6 |
| `TeammateLoadoutBroadcastTests` | 1, 1, 1 |
| `TeammateStatusStaggerTests` | 2, 2, 2 |

### Full Fast sweep

`tools/run-tests.sh --tier fast --parallel 1 --no-build --results-dir .scratch/fast`
ran all seven Debug projects against the final build. The runner discovered
16,160 Fast cases. In 1,045.3 seconds, 16,153 passed, five skipped and two
failed an assertion. All five P1089 cases passed. The nine infrastructure
checks passed, and the quarantine lane needed zero retries.

The wake-mine test never saw a chased bot deploy its mine (22.845 s). The
resignation ballot test observed the passing announcement but missed the
expected matching chat receipt. The warp-exit NPC measurement passed in
10.729 s. It and the wake-mine case exceeded the calibrated 10.000 s limit.
The whole sweep exited 1. All three signatures have entries in the
[flake ledger](../TEST_FLAKES.md).

Each flagged method then ran three times alone in a fresh Debug process.

| Method | Isolated case times, seconds | Outcome |
|---|---|---|
| `P877MineDamageLiveTests.Chased_bot_wake_mine_damages_pursuer` | 1.785 / 1.696 / 1.678 | 3/3 passed |
| `BallotVotingLiveTests.Resign_passing_deactivates_the_side_and_ends_the_mission` | 0.611 / 0.640 / 0.708 | 3/3 passed |
| `P777WarpExitWobbleTests.Npc_exit_touch_rate_is_measured` | 9.342 / 9.323 / 9.279 | 3/3 passed, all below the limit |

The full sweep retains its exit status. The P928 supplemental golden was outside
the requested focused runs. Its existing base mismatch is documented in the
[P1087 report](RESCUE_RANK_SPEEDUPS_P1087.md).

### Extra live position suite

The moving-teammate case passed all three isolated Release class runs. The
stationary case passed once and twice reached line 201 with an empty observation
list after the teammate identity had arrived. The boot receipt confirmed the
fixture's explicit port mode. Earlier ledger entries also describe an
environment override that selected the native model.

For a matched check, the base Server was rebuilt from a local export of
`83703eac` and substituted into a private copy of the same Release test output.
Three method-only runs per revision alternated with the same server GC and
tiered-compilation settings. Base failed all three times at line 201.
P1089 failed twice at the same assertion and passed once. By tick 100, the
failed base runs already showed 32 dropped snapshot streams and zero encoding
failures. The two failed changed runs showed 44 and 32 dropped streams with
zero encoding failures. The counters do not identify which specific packet
was dropped. The complete unchanged class then passed 2/2 in each of three
isolated Debug runs. The port-mode published transcript still matches base
in all deterministic cases. The live Release signature is recorded in the
[flake ledger](../TEST_FLAKES.md).

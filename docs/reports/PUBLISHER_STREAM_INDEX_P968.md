# P968 — Publisher stale-stream index

The quadratic scan is confirmed. `broadcast.pendingStreamIndex` adds an optional
pending `(ConnectionId, Key)` index and bypasses freshness checks for key-zero
events. **Default remains off.** Changes are uncommitted; no push or access to the
rig, mini, Mac or other game hosts was performed. Source review: P963-06, copied locally into
`.scratch/p968/SERVER_REVIEW_P963.md` and checked against this checkout.

## Measurement first

Before introducing the flag, the original algorithm was measured with a blocked
publisher at 100 and 400 peers. Each active/pending snapshot contains eight
ordered reliable events and one guaranteed own-pose stream per peer. Active tick
0 waits before encoding while tick 3 becomes pending. Releasing the worker makes
all freshness decisions deterministic. Counts are asserted, rather than inferred
from wall time:

| Peers | Comparisons | Freshness gate held, ms | Drain lag, ms, probes disabled |
|---:|---:|---:|---:|
| 100 | 402,650 | 3.943 | 4.123 |
| 400 | 6,470,600 | 64.914 | 61.724 |

These initial measurements use Debug, server GC, four encode workers, two warmups
and seven measured repetitions. Work grows **16.07×** for 4× the peers. The exact
count is `(81·P² − 47·P)/2`: even key-zero traffic scans until the first matching
peer/event. This justified continuing with the experiment.

## Design and configuration

- JSON: `{"broadcast":{"pendingStreamIndex":true}}`.
- Environment: `ALLEGIANCE_BROADCAST_PENDING_STREAM_INDEX=1` (or `true`).
  `0`/`false` disables it; unset/empty preserves JSON; malformed values fail boot.
- Constructor/config default is false. The flag affects only immutable snapshots;
  it does not enable that pipeline itself. Boot logs the resolved setting.
- A publisher-owned hash set is allocated only in the enabled arm. Under `_gate`,
  `Submit` first performs the existing coalescing operation unchanged, then clears
  and rebuilds membership from the **final pending list**, including retained
  guaranteed streams and both delivery lanes. Key-zero entries are excluded.
- The coordinator clears membership when it takes the pending snapshot. Every
  nonzero delivery still acquires `_gate` and tests the current pending tick;
  guaranteed streams consult membership separately. No freshness decision is cached across deliveries.
- Enabled key-zero deliveries bypass the lock/check. Delivery lists, wire bytes,
  tick-gap predicate, unreliable shedding, reliable order, packet ownership,
  pool returns, and after-publication actions retain their existing rules.

Lookup has expected O(1) hash-set cost. Building the index is O(pending deliveries)
and stores O(distinct nonzero streams), adding work to `Submit` in exchange for
removing repeated pending-list scans. It is not an unbounded history/cache. The
control arm retains its linear scan and key-zero lock path.

`BroadcastPublisherDiagnostics` is an optional internal measurement seam: normal
production leaves it null. Probes count comparisons, lookups, index visits/adds,
and freshness/Submit critical-section ticks. The uninstrumented scan has no
per-comparison probe. No new production reflection or serialization mechanism
was added; the existing source-generated server JSON contract binds the flag.
Compose passes an empty environment default so configured JSON remains effective.

## Before/after numbers

The optimized-build comparison uses Release, server GC and
`DOTNET_TieredCompilation=0` to remove JIT tier transitions from short samples
(the production AOT build has no tiered JIT). Four workers, two warmups, seven
samples per cell; medians below. Packets are pre-encoded immutable buffers and
use the production memory-sink publisher overload. This isolates the publisher
from simulation/projection/transport cost. The recorded production fixture pins
immutableSnapshot + encodeOnce, parallelBuild, native ship updates, pool transport,
nativeGotoPlan and tiers off; P863 scheduling is unchanged. No old port/serial/
thread-per-peer path is used as the optimization baseline.

“Coalesced” also submits tick 1 before tick 3, retaining its ordered events and
replacing its own poses. Every visit to build the index is counted, including
key-zero visits. Full samples and medians:
[measurements.json](P968_assets/measurements.json).

| Peers | Pending workload | Flag | Comparisons | Lookups | Index visits / adds | Freshness locks |
|---:|---|---|---:|---:|---:|---:|
| 100 | replacement | off | 402,650 | 0 | 0 / 0 | 1,800 |
| 100 | replacement | on | 0 | 100 | 1,800 / 200 | 200 |
| 400 | replacement | off | 6,470,600 | 0 | 0 / 0 | 7,200 |
| 400 | replacement | on | 0 | 400 | 7,200 / 800 | 800 |
| 100 | coalesced | off | 443,050 | 0 | 0 / 0 | 2,600 |
| 100 | coalesced | on | 0 | 100 | 3,500 / 300 | 200 |
| 400 | coalesced | off | 7,112,200 | 0 | 0 / 0 | 10,400 |
| 400 | coalesced | on | 0 | 400 | 14,000 / 1,200 | 800 |

| Peers | Workload | Flag | Freshness gate ms | Submit gate ms | Drain lag ms, probes disabled |
|---:|---|---|---:|---:|---:|
| 100 | replacement | off | 0.2530 | 0.0001 | 0.2858 |
| 100 | replacement | on | 0.0041 | 0.0085 | 0.0858 |
| 400 | replacement | off | 4.0018 | 0.0001 | 3.8556 |
| 400 | replacement | on | 0.0169 | 0.0259 | 0.2617 |
| 100 | coalesced | off | 0.2793 | 0.0348 | 0.3696 |
| 100 | coalesced | on | 0.0045 | 0.0453 | 0.1169 |
| 400 | coalesced | off | 4.5705 | 0.0839 | 4.3539 |
| 400 | coalesced | on | 0.0190 | 0.1147 | 0.4039 |

Gate times are cumulative time *inside* the named critical sections, excluding
lock acquisition/wait, not the maximum duration of an individual acquisition.
Drain lag runs from releasing the intentional stall until both active and
pending snapshots complete; it excludes the imposed stall and does not claim
end-to-end network latency. The baseline/control gap includes the key-zero
shortcut and index together, not separate ablations. Submit's extra index cost
is visible and is deliberately included. This shared 24-CPU development host
had load averages around 5–9; these are local algorithm measurements, not rig
performance claims. Initial Debug and final Release numbers are separate runs.

Reproduce the stable measurement after building the Release test project:

```sh
# Load local.env, then use the installed SDK, with server GC enabled.
DOTNET_gcServer=1 DOTNET_TieredCompilation=0 "$HOME/.dotnet/dotnet" test \
  tests/Allegiance.Core.Tests/Allegiance.Core.Tests.csproj -c Release --no-build \
  --filter FullyQualifiedName~P968PublisherStreamIndexTests \
  --logger 'console;verbosity=detailed'
```

## Equivalence proof

`P968PublisherStreamIndexTests.cs` records **one** production 24-peer, two-side,
16-tick workload, with real kill/eject events, discovery/status traffic and owned
poses. Each arm replays that same detached tape through the publisher. Four
controlled backlog epochs include same-tick split phases, later-tick replacement,
an empty incoming phase, callbacks and recycled snapshot containers.

Both arms publish **2,456 frames / 100,661 bytes**, from **3,608 recorded frames**,
with **1,152 drops and 24 coalesces**. Every frame compares peer, key, guaranteed
lane, bytes and global sequence position directly; ordered reliable bytes and
callbacks are checked independently. Serialized output SHA-256 (including
peer/key/lane/length metadata):
`C36232B8629D39E1868D628ACCE4A90CEB265EC7CC19637DE039D3EFC8570CFE`.

Additional asserted-output regressions cover a `Submit` between individual sends,
the exact one-tick boundary, guaranteed streams retained from older pending
snapshots, unreliable streams removed on tick advance, complementary same-tick
phases, empty submissions, same-key/different-peer isolation, guaranteed active
versus unreliable pending membership, and key-zero deliveries in either lane.
Existing publisher lifecycle/failure tests now run both flag values, covering
encode/send/callback failures, packet/container return failures, disposal and
coalescing. Config default, source-generated JSON binding, environment precedence
and actual production boot wiring are tested.

The finite transcript is evidence for these schedules; it does not assert byte
identity across unrelated wall-clock schedules whose publication timing differs.
The per-delivery locking contract is retained for that reason.

## Gate and A/B handoff

Implementation checks before the gate: all **23 new test cases pass**, and the
publisher/control/failure subset passes **51/51**; zero skips with `local.env`.
Qualification is complete. The original Fast gate remains **FAIL** on the
non-task issues below; no new or changed test failed:

| Check | Result |
|---|---|
| Solution build | 0 warnings / 0 errors |
| `tools/build-tools.sh` | All 121 tool projects built, no warnings/errors |
| linux-x64 self-contained Native AOT | Passed; no IL2026/IL3050 diagnostics |
| Every new/changed test method, 3× alone | 129 passed / 0 failed / 0 skipped |
| Broadcast/snapshot families, flag on, 3× | 156/1/0, 157/0/0, 157/0/0 (pass/fail/skip) |
| One complete Fast suite, flag off | 13,562 passed / 7 failed / 3 skipped; six passing executions over the calibrated time limit |
| Showcase, five alternating serial runs per flag | Off 5/5, on 5/5; 0 failures / 0 skips |
| Non-task failure/overrun isolation, 3× | 36 isolated method runs / 60 cases: 45 passed, 15 failed (the five known path cases ×3); no skips |
| Compose, glossary links, parity validation, whitespace | Passed |

The 157-case family filter is
`FullyQualifiedName~Broadcast|FullyQualifiedName~P899PublisherTests|FullyQualifiedName~P968Publisher`.
It includes real TCP, encoded/recorded snapshots, direct publisher lifecycle and
fault containment, serial/parallel byte identity, and other broadcast regression
families. Direct publisher theories deliberately exercise both flag values;
production server boots in this cohort resolve the flag on.

The Fast suite took 528.7 seconds; infrastructure checks and discovery coverage
passed. The seven failures are five existing `PlayerBundlePathsTests` cases
(worktree-local TMPDIR discovers ancestor `local.env`), the already-ledgered
`LiveCombatScoringTests.Real_two_peer_kill_updates_both_player_scores` missing-kill
receipt timeout, and the existing timer floating upper-bound signature at
`P739TimerLiveTests.Warp_bomb_notice_matches_captured_blast_deadline_and_stays_in_mission`
(`5.0000000000000009 > 5`). No new or changed test failed. Six timing overruns
(10.284–13.791 seconds against 10.081 seconds) are in existing P800, SC2Nanite,
P814, P796 and P777 tests. All received three isolated method reruns; this
does not turn the original Fast result green. The three skips are the optional absent Meshy-pack
cases; new/changed and broadcast cohorts have **zero skips** with `local.env`.
Full suite means the lane's Fast suite, as specified in its light-gate rules;
no training code changes require the Live lesson gate.

The broadcast cohort's one failure was the existing
`AsteroidMinedLiveTests.No_broadcast_fires_once_miner_hits_capacity` queued-report
assertion. It passes **3/3 alone with the flag on**. Combat scoring and all timer
theory rows also pass 3/3 alone. The five path cases fail in every repetition,
with the same known scratch-ancestry obstruction and six passing companion cases
across their four selected methods. Path resolution, mining, scoring, timers and
flight code were not changed.

| Isolated method/family | Three repetitions | Maximum individual case seconds per repetition |
|---|---|---|
| Mining capacity receipt, flag on | 1/0/0 each | 1.794 / 1.828 / 1.806 |
| Two-peer combat scoring | 1/0/0 each | 0.442 / 0.471 / 0.458 |
| P739 warp-bomb timer, all delay rows | 3/0/0 each | 0.471 / 0.462 / 0.443 |
| P800 GPU hull proof | 1/0/0 each | 3.855 / 3.850 / 3.765 |
| P796 varied loadout, all civ rows | 3/0/0 each | 5.779 / 5.839 / 5.868 |
| P814 turret carrier preference | 1/0/0 each | 3.948 / 3.600 / 3.680 |
| SC2 Nanite support, both civ rows | 2/0/0 each | 4.489 / 4.482 / 4.426 |
| P777 NPC exit measurement | 1/0/0 each | 9.827 / 10.148 / 10.258 |
| Four path methods, five failed cases plus two companions | 2/5/0 each | ≤0.016 |

Counts are pass/fail/skip. All isolated timing cases fall below the original
10.081-second calibrated limit except P777 repetitions 2 and 3, which remain an
open timing issue despite passing their assertions. These observations are
ledgered in [TEST_FLAKES.md](../TEST_FLAKES.md); no unrelated test was weakened,
retagged, or skipped. No second full-suite run was used to replace the red gate.

Showcase comparison uses the same normal-home `P700ShowcaseLiveTests` method,
serially alternating off/on at the explicit rig baseline settings. Every run
observes 24/30 arrivals and real shots/hits. Durations below are the fixture's
observation window, excluding test startup/teardown; these unscripted matches
are regression checks, not a controlled performance A/B.

| Run | Off seconds | Off maximum idle, seconds | On seconds | On maximum idle, seconds |
|---:|---:|---:|---:|---:|
| 1 | 325.8 | 1.912 | 331.6 | 0.106 |
| 2 | 182.7 | 2.510 | 315.5 | 2.009 |
| 3 | 270.8 | 0.106 | 270.2 | 0.305 |
| 4 | 186.2 | 2.004 | 338.0 | 1.311 |
| 5 | 268.4 | 1.910 | 191.5 | 1.911 |

Off and on both pass **5/5**, with no stopped-with-target interval reaching
5 seconds. Full test duration ranges are 187.244–330.988 seconds off and
196.231–342.836 seconds on.

Reviewable evidence: [gate counts and flight receipts](P968_assets/gate-summary.json),
[Fast runner summary](P968_assets/fast-summary.json), and
[isolated rerun details](P968_assets/triage-summary.json). Raw logs, TRX,
measurement/qualification scripts and the copied review remain in
[the local scratch evidence](../../.scratch/p968/); the
[Native AOT output](../../.scratch/p968/aot/) is retained locally. Temporary
fixtures, NuGet HTTP cache and writable profile scratch are removed after tests.
Only small JSON summaries/samples accompany this report; no new media was added.

The first SDK restore attempted the standard NuGet vulnerability-metadata refresh
before offline settings were applied, and failed writing the read-only shared
HTTP cache. That initial tooling attempt did not honor the requested no-public-host
boundary. All subsequent restores use a scratch HTTP cache and cached packages
as the sole restore source, with network auditing disabled. No package versions
or project warning policy changed. No rig, mini, Mac or remote game service was
accessed. All generated logs, TRX and Native AOT output stayed in `.scratch/p968/`.

Proposed Claude rig A/B: retain the rig baseline configuration and executable
build settings, run the same seeded 400-peer join/death surge and normal
showcase matchup with only `broadcast.pendingStreamIndex` toggled. Alternate
control/treatment and compare `broadcast.snapshotPublish` simulation cost,
publish duration/lag, tick p95/p99, coalesces/dropped streams, egress bytes,
allocation/GC, and packet order/lanes. Include an ordinary unbacklogged control
to price index rebuilding when scans are uncommon. Keep the default off until
that A/B decision. Diagnostics stay disabled for primary timings; use a separate
instrumented run only if attribution is needed. No rig configuration was edited.

## Native intent

Native uses guaranteed event sends (for example the RIPCORD_ABORTED send at
`FedSrv/FedSrv.CPP:13460`) and has its own ship-update scheduling
(`FedSrv/FedSrv.CPP:4238,4247,4253`, re-grepped in the read-only native tree).
This task ports no new native rule, so no parity row/status is changed or claimed.
Existing reliable delivery and stream freshness semantics remain the contract.

## Modern approach

A default-off hash membership index replaces repeated pending-list scans while
keeping the ordered delivery list and per-delivery synchronization. Local
measurements establish the scaling change; Claude's baseline rig A/B decides
whether its extra Submit work is worthwhile in deployment.

# P668 — immutable broadcast snapshot

Implementation base: `6c3e54ad` (`integ3` merged through P659), working branch
`codex/pipeline`. Changes are left uncommitted. The P631 study and
`docs/architecture/SERVER_PIPELINE.md` named in the brief are absent on this base.

## Design

`broadcast.immutableSnapshot` defaults to **false**. The environment override
`ALLEGIANCE_BROADCAST_SNAPSHOT=1|true|0|false` wins over JSON; invalid values fail
before boot registers callbacks or starts transport threads. The resolved flag
appears in the boot banner and profiler label.

The snapshot is an **owned wire projection**, rather than a cloned simulation
world. On the sim thread, the existing broadcast passes resolve visibility,
audiences, targets, per-viewer ranking, P632 density budgets, P659 floor scheduling,
dirty predicates and delivery latches in their original order. Their results are
captured as message data and ordered `(peer, packet, coalesce-key, guaranteed)`
deliveries. The cross-side ship/mine export and status sweeps use the same boundary.
This deliberately preserves the shared, order-dependent deviation updates and
per-viewer cadence ownership on the sim thread.

Single-ship and heavy poses additionally capture raw values: controls, position,
velocity, all three turn rates, fractions, references and an **orientation copy**.
Their compact-byte construction is lazy, so it runs in the encode pool rather
than during projection. Other message DTOs contain copied wire values/owned
arrays; loadout arrays and cached cluster-info data are immutable shared values.
No worker receives a Ship, Sector, Mission, visibility object, live dictionary,
or getter/callback into the simulation.

Capture starts before the tick's transport drain so reliable responses and game
events cannot overtake an older tick's deferred broadcasts. The snapshot is sealed
and submitted after the last broadcast pass. A dedicated publishing coordinator
uses a bounded `Parallel.For` encode pool, then enqueues packets in captured order.
Shared teammate messages and per-side packets encode once. The pool degree uses
`broadcast.maxDegreeOfParallelism`'s existing resolved limit; the P605 transport
send pool remains a separate, unchanged queue-draining layer. Capture output and
egress accounting are safe on the publishing thread. Shutdown drains publication
and joins the coordinator before closing transport/capture resources.

There are three recycled snapshot containers: capture, active publication, and
one pending snapshot. A newer submission replaces the pending streaming state;
ordered reliable deliveries are carried forward in order because their sim-side
latches have already advanced. Guaranteed own-ship poses retain their latest
value even if the next tick has no dirty own pose. Active streaming publications
more than one tick old are suppressed when a newer snapshot is pending; a
superseded guaranteed own pose is suppressed too. Counters record coalesced
snapshots, dropped stream frames and encode/publish failures. Reliable event
history is retained separately from stale streaming state: the one-pending limit
bounds snapshots, not the number of distinct reliable events during a stalled
publisher.

`[broadcast-snapshot]` logs cumulative build/projection time, producer allocation,
publish wall time, publication/coalescing/drop/failure counts and container count.
`broadcast.snapshotPublish` profiles submission separately. Existing `step:broadcast*`
buckets now measure projection when enabled.

## Byte identity and threading proof

The recorded input fixture is generated once and replayed with the flag off and
on in the **same server session**, resetting broadcast latches/cadence between
replays. It covers 105 ticks, 144 peers, both sides, a promoted target per viewer,
moving/rotating ships, docking transitions, varying shields and visibility,
real loadout data, a same-side NPC, station/asteroid/probe updates, mines and
missiles. Four configurations cover port/native updates, serial/parallel
projection and floor rates 0, 2 and 3 Hz. Comparisons include every packet byte,
CRC, per-peer order, tick, coalescing key and guaranteed flag. Hashes and counts
are recorded in [P668_RESULTS.json](P668_RESULTS.json).

| Update model | Parallel projection | Floor Hz | Packets per replay | Bytes per replay | Matching SHA-256 prefix |
| --- | --- | --- | --- | --- | --- |
| port | false | 0 | 276,184 | 77,143,800 | `E9818924260ED254` |
| port | true | 2 | 276,184 | 83,832,573 | `1C4519215B2B37DF` |
| native | false | 2 | 261,064 | 22,996,941 | `3C2919674F7E403C` |
| native | true | 3 | 261,064 | 27,521,994 | `27B42CC5C6DE7E55` |


A second recorded-session test uses 12 actual TCP clients and 96 ships for
30 ticks. Each tick drains through a guaranteed marker in the **stream lane**,
which follows both previously queued lanes. Live ship positions are changed
immediately after snapshot submission, before waiting for publication. Received
packets are compared byte-for-byte and in order within each existing lane.

**Ordering scope:** P634's transport prioritizes its ordered queue over its stream
queue (`TcpTransport.cs`, `PeerSender.Enqueue`/drain). Cross-lane socket
interleaving therefore depends on when socket workers run, including with the
flag off. Exact global per-peer order is proved at the encoder/enqueue boundary;
real TCP receipt order is proved per lane. A promise of identical cross-lane TCP
interleaving, or delivery of every best-effort snapshot under overload, would
require a different transport contract. This change preserves the current one.

The Debug guard recursively inspects captured message fields and fails closed on
unknown references or live sim types. A Release-callable version is exercised by
tests. Pose tests verify compact buffers are unmaterialized at capture, mutate the
source ship's position, attitude, controls and turn rates, then encode on another
thread and compare against the original eager bytes. The stalled-worker test
submits 101 ticks without releasing the worker: exactly two snapshots publish,
99 pending snapshots coalesce, stale streams are counted, all 101 reliable events
retain order, the last guaranteed own pose survives, and only three snapshot
containers are created. Another test verifies shutdown drains the final snapshot.

## Native rules retained

These are inherited wire/selection rules, not a claim that native used this new
pipeline. The density scaling and per-viewer floor remain port adaptations.

| Rule | Verified native source |
| --- | --- |
| Fixed heavy/light quotas, deviation sort and resets/bumps | `~/Development/Allegiance/src/FedSrv/FedSrv.CPP:3844`, `~/Development/Allegiance/src/FedSrv/FedSrv.CPP:3923`, `~/Development/Allegiance/src/FedSrv/FedSrv.CPP:3937` |
| Target promoted ahead of the remaining ship updates | `~/Development/Allegiance/src/FedSrv/FedSrv.CPP:4141` |
| HEAVY followed by LIGHT in one non-guaranteed send | `~/Development/Allegiance/src/FedSrv/FedSrv.CPP:4183`, `~/Development/Allegiance/src/FedSrv/FedSrv.CPP:4189` |
| LIGHT has controls/state; HEAVY adds pose | `~/Development/Allegiance/src/Igc/igc.h:2423`, `~/Development/Allegiance/src/Igc/igc.h:2431` |
| SINGLE_SHIP_UPDATE broadcast is guaranteed | `~/Development/Allegiance/src/FedSrv/fsship.cpp:523`, `~/Development/Allegiance/src/FedSrv/fsship.cpp:544` |
| Packet CRC appended after concatenated message bytes | `~/Development/Allegiance/src/_Utility/Messages.cpp:574` |
| Guaranteed sends use coalescing and no timeout | `~/Development/Allegiance/src/_Utility/Messages.cpp:557`, `~/Development/Allegiance/src/_Utility/Messages.cpp:563` |
| Side visibility/current-eye state and visibility transitions | `~/Development/Allegiance/src/FedSrv/FedSrv.CPP:11133`, `~/Development/Allegiance/src/FedSrv/FedSrv.CPP:11140`, `~/Development/Allegiance/src/FedSrv/FedSrv.CPP:11155`, `~/Development/Allegiance/src/FedSrv/FedSrv.CPP:11163` |

## Measurements and validation

Environment: Ubuntu 26.04 x86-64, Intel Core Ultra 9 285HX (24 CPUs), .NET SDK
10.0.400/runtime 10.0.11. The loopback test seats real TCP players in one sector,
with two sides, no weapons/AI, port ship updates, a 2 Hz floor, four encoder
workers and four P605 send workers. Each mode warms for 20 ticks, **drains both
transport lanes through a stream-tail barrier**, then records 120 ticks at a
requested 20 Hz. A final barrier accounts for received measurement frames. Both modes use the
same connected server through a test seam; the disabled interval retains an idle
publisher and the send path's drain checks. These are not fresh-process boot-mode
benchmarks, so that small disabled-path overhead is included in the comparison.

<!-- P668 measurement tables -->

| Players | Snapshot | Sim tick mean / p95 ms | `step:broadcast*` ms / share | Export sweep mean ms | Publish pool mean ms |
| --- | --- | --- | --- | --- | --- |
| 256 | off | 24.05 / 34.98 | 20.36 / 84.6% | 1.40 | 0.00 |
| 256 | on | 12.02 / 17.98 | 10.78 / 89.7% | 0.56 | 4.07 |
| 512 | off | 66.27 / 117.60 | 59.30 / 89.5% | 2.63 | 0.00 |
| 512 | on | 29.09 / 38.77 | 26.90 / 92.5% | 1.39 | 18.95 |

| Players | Snapshot projection mean ms | Projection allocations MiB/tick | Whole producer allocations off → on MiB/tick |
| --- | --- | --- | --- |
| 256 | 11.44 | 3.09 | 11.18 → 3.36 |
| 512 | 28.42 | 6.32 | 30.27 → 6.64 |

Pool time is coordinator wall time, including worker scheduling, encoding and
queue publication. It overlaps sim execution. Projection time covers the entire
broadcast suffix, including visibility/audience selection, ranking and snapshot
construction. Allocation is producer-thread allocation; output buffers allocated
by workers still contribute to process GC pressure. The producer measurement also
includes the small harness loop overhead. Each mode records 120 ticks; raw bucket
windows are preserved in [P668_assets](P668_assets/).

Broadcast still occupies most of the shorter sim tick because projection and
ranking remain on the sim thread.

Both crowds retained all seated players. Publisher failures and snapshot
coalescings were **zero**, with **three** snapshot containers throughout. The
512-player baseline exceeded its 50 ms tick budget; the enabled run stayed below
50 ms at p95 in this sample. This is the largest crowd exercised here.

Transport delivery remains timing-dependent: the measured 256-player receive
counts were 567,113 off / 373,162 on, and the 512-player counts were 1,372,463 off /
678,446 on. These windows exercise the existing reliable-priority/coalescing
transport, so they are not wire-identity trials. The recorded-tick proofs above
drain between ticks and verify bytes/order separately from overload shedding.

<!-- /P668 measurement tables -->

Earlier preliminary measurements without the transport-drain barrier are
superseded. Timings are local observations on a shared machine, not a rig
throughput or combat claim.

The zero-allocation aspiration is **not achieved**: snapshot containers and their
lists are recycled, but per-tick DTOs, orientation copies, pose snapshots,
recipient arrays and encoder output buffers still allocate. The implementation
moves encoding off-thread; it does not move audience/ranking/export-decision work
off-thread. These remaining costs are visible in the projection measurements.

Recommended default: **off**, as requested for phase 1. Use the flag for measured
opt-in runs; retain the guard/byte proofs and finish DTO/buffer pooling before
considering a default change. Rig/221/200v200 work is reserved for the other lane.

## Validation (2026-10-14/15)

Final Debug solution and Release Core-test builds: **0 warnings, 0 errors**.
NuGet's HTTP cache was redirected to `/tmp/p668-nuget-http` to respect the
sandbox's read-only home directory; package auditing was not disabled.

All **16 new test cases** passed three method-filtered isolated repetitions.
The 41 selected new/affected test methods were each run three times alone
(159 case executions). Forty methods passed all initial repetitions. The
mine-visibility live test passed its first two, timed out waiting for an enemy
mine EXPORT on the third, then passed **3/3 fresh individual runs** and both full
Core passes. This is recorded in [TEST_FLAKES.md](../TEST_FLAKES.md); no test was
weakened, skipped or newly quarantined. After adding the transport-drain barrier,
the revised crowded-session test passed another **3/3 isolated runs**.

The assemblies ran sequentially, unfiltered, including their live/timing lanes:

| Suite | Passed | Failed | Existing skips |
| --- | --- | --- | --- |
| Core, first complete pass | 4,318 | 0 | 0 |
| Core, final complete pass after benchmark refinement | 4,318 | 0 | 0 |
| ClientCore | 933 | 0 | 1 gated showcase, executed separately below |
| MonoGame | 2,125 | 0 | 3 optional private Meshy-pack checks; pack unconfigured |

With `ALLEGIANCE_BROADCAST_SNAPSHOT=1`, the existing real-socket join/leave churn
test passed **3/3 alone**. The gated P660 local **15v15 MCP showcase plus two human
clients** was explicitly enabled with `ALLEGIANCE_LIVE_P660_SHOWCASE=1`; it also
passed **3/3 alone with snapshots enabled**, including mission rotation, client
migration and finale rearming. Thus no available local live scenario was left
unexecuted because of its default gate. No rig/221/200v200 session was run.

The durable [results file](P668_RESULTS.json) includes the identity hashes,
per-method repeat results, suite counters/skip reasons, refined performance
measurements and the enabled lifecycle checks. Its CSVs contain only the final
measurement window for each configuration, extracted from the profiler's
append-only output.

Reproduction (load the workstation's `local.env` first):

```sh
NUGET_HTTP_CACHE_PATH=/tmp/p668-nuget-http dotnet build -m:8 -v:q
dotnet test tests/Allegiance.Core.Tests --no-build --filter 'FullyQualifiedName~BroadcastPublisherTests|FullyQualifiedName~BroadcastSnapshotTests|FullyQualifiedName~BroadcastSnapshotLoopbackTests'
dotnet build tests/Allegiance.Core.Tests -c Release --no-restore -v:q
P668_PEERS=512 dotnet test tests/Allegiance.Core.Tests -c Release --no-build --filter 'FullyQualifiedName~Crowded_real_TCP_session'
ALLEGIANCE_BROADCAST_SNAPSHOT=1 ALLEGIANCE_LIVE_P660_SHOWCASE=1 dotnet test tests/Allegiance.ClientCore.Tests --no-build --filter 'FullyQualifiedName~Showcase_bots_and_humans_rotate_without_rejoin_and_finale_rearms'
```

Full suite commands were `dotnet test tests/Allegiance.Core.Tests --no-build`,
then `tests/Allegiance.ClientCore.Tests`, then
`tests/Allegiance.ClientCore.MonoGame.Tests`, each without a test filter.
All changes and this report remain in the working tree; **no commit or push**.

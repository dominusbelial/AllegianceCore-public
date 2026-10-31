# P899 — broadcast pool crash containment and restart investigation

Changes are left in the working tree; no commits, pushes or remote-host access.

## Finding

**The exact cause of the recorded rig crash is not established.** The checked
operation and a reachable small-pool overflow mechanism are established; the
particular bag and its counters at the crash are not available. The 11-hour
snapshot-count explanation alone does not fit.

The local SDK is 10.0.400. Its restored server AOT compiler/runtime packs are
10.0.11, runtime commit `e2f47b0110ed922f21a1522da67279133ce28f32`.
`src/Allegiance.Server/Dockerfile` pins that SDK too. This identifies the current
publish, not independently the runtime embedded in the saved b59 executable;
Claude must confirm that artifact's build provenance.

`tools/p899-runtime-proof` reads the shipped managed assembly's IL. On 10.0.11,
`WorkStealingQueue.LocalPush` contains one checked addition: `IL_01fa: add.ovf`,
corresponding to `_addTakeCount++` in
[the matching runtime source, lines 723–839](https://github.com/dotnet/runtime/blob/v10.0.11/src/libraries/System.Collections.Concurrent/src/System/Collections/Concurrent/ConcurrentBag.cs#L723).
It throws when that **per-thread integer is already 2,147,483,647**. It is not
the bag's 64-bit empty/nonempty-transition counter. Tail-index rollover is
handled separately. The slow push path subtracts completed steals; the fast
path does not. A producer whose queue retains spare capacity and at least two
elements can keep accumulating historical adds while another thread steals.
Thus a small bag can reach this boundary after billions of suitable operations,
without caller double-return or memory corruption. That is a runtime algorithm
corner case, not evidence that it happened here.

The diagnostic's normal run performs 100 paced cross-thread transfers with three
retained objects and does not overflow. Its explicit
`--seed-counter-boundary` mode seeds coherent counters/ring occupancy representing
that historical boundary. Two pushes and one foreign steal produce the same
exception, with only a few objects. **This is an injected boundary demonstration,
not a natural reproduction of the incident.** The real 200-client before/after
sessions also did not reproduce the crash.

[dotnet/runtime issue 114817](https://github.com/dotnet/runtime/issues/114817)
reports the same exception in a buffer pool. It was closed for inactivity, not
with a confirmed fix; its discussion did not establish the cause. The related
[optimization PR 122956](https://github.com/dotnet/runtime/pull/122956) was closed
unmerged. Neither establishes that upgrading the runtime fixes this incident.

## Ownership audit and fix

`BroadcastPublisher` starts with three snapshot containers. Under its production
single-capture-producer contract, there is at most one capture, one pending
snapshot and one active snapshot. The coordinator exclusively owns an active
snapshot; `Submit` owns pending/coalescing transitions under `_gate`. Coalescing
moves deliveries/actions into the previous pending snapshot and clears the
incoming snapshot before returning it. The coordinator returns each active
snapshot once, in `finally`. No production double-return path was found.
Arbitrary callers holding extra rented containers can grow the pool; production
does not do that. Across the local sessions the container count stayed **three**.
All 32 pre-crash snapshot metric rows in the supplied server tail also report
three containers and zero failures; the last reports 823,100 built snapshots and
822,974 published.

`Return` can fail **before** its final snapshot-pool push: it first invokes
`BroadcastPacket.Release`, and `PooledPosePacket.Release` used another
`ConcurrentBag`, per packet-size bucket. Native AOT may inline that call, so the
saved `LocalPush → BroadcastPublisher.Return` stack cannot identify which bag
failed. The actual pre-crash count of 823,100 built snapshots (and roughly 810,000
returns from the brief's approximate uptime/rate) is far below the checked
boundary. An overflow of the snapshot bag at that operation count would require
an additional runtime defect or corruption, not ordinary historical adds. Packet returns are much more frequent, including
per-recipient teammate status traffic. Their historical per-thread/bucket counts
were not logged. Reaching the integer boundary within 11.25 hours would
require about 53,024 historical adds/second on one owner queue without count
folding or compensating local pops. That rate was not established. Packet
returns remain a candidate to inspect, not a diagnosed cause. The supplied
logs do establish substantial packet volume: the last pre-crash
`[server-counters]` line reports **4,121,090,043 `cPacketsOut`**, versus only
823,100 snapshots. This makes packet-pool historical-counter overflow consistent
with total volume; it does not establish any one pool bucket/owner count or
exclude slow-path count folding. See [rig counter observations](P899_assets/rig-pool-observations.json).

Game-over resets world/peer caches, not these pools. The snapshot publisher lasts
until server shutdown; packet pools are static. Shutdown completes the current
capture, drains/joins the publisher, then tears down transport. No game-over
mutation of `_pool` or snapshot-container lifetime was found.

`BroadcastPool<T>` replaces **both** the snapshot bag and the packet-size bags
with small explicitly locked `Stack<T>` instances. Snapshot preallocation,
fallback creation and `SnapshotContainersCreated` accounting are unchanged;
packet buffers still retain their size buckets and reuse storage. Stack order
is unobservable for empty reset containers and exclusive packet leases. There
is no gameplay, protocol or feature-flag change.

Cleanup now catches each packet-release error separately, calls `Failed`, and
continues releasing all remaining packets. It never retries a release: the old
bag could insert the packet before throwing, making a retry a double return.
Actions and delivery references are cleared before attempting to pool a
container. A failed container push is reported and its empty container can be
collected. The success path adds no per-return allocations.

The publisher's exception boundary also covers pending-work acquisition and
`Monitor.Wait`. Completion notification runs in `finally`, so `Flush` and
shutdown are not stranded by cleanup failures. Encoding, transport sends,
after-publication callbacks and diagnostics already have exception guards;
the new tests exercise these plus interrupted waits and a throwing error
logger. There is no newly introduced fatal-stop policy. CLR-fatal failures such
as an uncatchable stack overflow remain outside managed exception containment.
Recoverable failures retain the existing `[broadcast-publish]` error log and
`EncodeFailures` counter (whose historical name covers publication failures).

## Loopback measurements and verification

The new 200-client test uses real TCP, a crowded single sector, 20 Hz pacing,
40 warm-up ticks and 200 measured ticks per run. Before/after order was
bag / stack / stack / bag / bag / stack. The unchanged baseline server assembly
was built from HEAD in an isolated scratch source tree and swapped only into
the test output between processes; the modified assembly was restored afterwards.
Both sides used Debug JIT .NET 10.0.11 with **server GC confirmed by the running
process**, pooled transport (four workers), immutable snapshots, encode-once,
parallel build (four workers), native ship updates and native GotoPlan. No other
lane was driven or synthetic load generated. Host load averages sampled nearby
were 4.95–5.83 on 24 logical CPUs. These are local checks, not the rig A/B.

| Median of three runs | Original bags | Locked stacks |
|---|---:|---:|
| Tick mean | 15.732 ms | 16.297 ms |
| Tick p95 | 19.959 ms | 20.484 ms |
| Process allocation / tick, including TCP receivers | 1,793,788 B | 1,793,765 B |
| Publisher allocation / tick | 78,486 B | 78,253 B |
| Snapshot containers | 3 | 3 |
| Publication failures | 0 | 0 |

Run ranges overlap: original tick means 15.673–16.944 ms versus
16.137–16.630 ms; original p95 19.784–20.117 ms versus 19.617–20.653 ms.
The median mean/p95 differences are +0.565/+0.525 ms (+3.6%/+2.6%), below
the observed run-to-run spread; these samples do not resolve a regression at
that scale. There is no allocation increase or container growth. Do not read
this as proof of an exact zero-cost change: Claude's AOT rig A/B remains the
performance authority. Each run verified all 200 TCP drain barriers and zero
peer errors. Preliminary workstation-GC runs were excluded from this table.

Machine-readable [measurements](P899_assets/loopback.json),
[runtime probe](P899_assets/runtime-proof.log),
[incident counts](P899_assets/incident-summary.json) and
[three real restart observations](P899_assets/restart.json) are retained here.
The supplied compressed logs and raw build/TRX outputs remain in
[the local evidence directory](../../.scratch/p899/).

Completed checks so far:

- Solution build: **0 warnings, 0 errors**.
- `tools/build-tools.sh`: **all 99 projects built**, no warnings.
- Linux x64 self-contained Native AOT publish: **clean**, including no
  IL2026/IL3050 diagnostics. Resolved compiler versions are retained in
  [AOT metadata](P899_assets/aot-runtime.json); the local executable is in
  [the scratch publish output](../../.scratch/p899/aot/).
- Five new containment tests: **5/5, three standalone class runs**. The
  200-client measurement test passed three times per server-GC variant.
- Broadcast/transport filter: **335/335 passed**, 4 min 46 s. This includes
  recorded bytes/order, real TCP, coalescing and pooled transport coverage.
- Runtime probe: normal and seeded modes completed **three times each**.
- Actual process restart investigation: **3/3** with JIT server and an
  additional successful run using the newly published **AOT server**. This
  validates the observed lack of fleet recovery, not an implemented repair.
- Glossary links, parity scorecard validation and whitespace checks passed.

The **single Fast-tier run remains FAIL**: **12,686 passed, 10 failed, three
optional skips**, 12,699 tests in 527.8 s; all nine infrastructure checks passed.
The mine-join failure and passing warp-touch case exceeded the 10 s limit.
No second full sweep was run. Results from the runner's default destination
were moved into the task scratch tree:
[raw Fast receipts](../../.scratch/p899/fast-run/),
[compact summary](P899_assets/fast-summary.json),
[three-run isolations](P899_assets/isolations.json).

The chat, mine, timer and warp-touch methods pass all three isolated runs.
The profile test fails all three with the read-only user config root and passes
all three with writable scratch `XDG_CONFIG_HOME`. Five bundle-path cases fail
all three because task-local temporary fixtures sit under the actual checkout
and find its `local.env`. The pre-existing wingman null-target assertion fails
all three, as already established on earlier unmodified baselines. These
signatures and outcomes are recorded in [the flake ledger](../TEST_FLAKES.md#p899-verification-2026-10-31-utc).
No profile, path resolver, chat, mine, timer, warp or AI logic was changed.

Showcase never-parks: **5/5 passed**, each run alone, with artwork, after the
Fast sweep and all isolation runs. This matches the base lane's documented
5/5 result (`P898_REPORT.md`, verification table). Every repetition reached
24/30 objectives, observed real hits and completed with zero sim-stage failures.
The maximum stopped-with-target interval was **2.013 s**, below the
five-second assertion. [Showcase receipts](P899_assets/showcase.json):

| Run | Observation | Reached objectives | Longest stopped with target | Hits |
|---|---:|---:|---:|---:|
| 1 | 209.4 s | 24/30 | 0.306 s | 198 |
| 2 | 373.1 s | 24/30 | 2.013 s | 993 |
| 3 | 199.2 s | 24/30 | 0.406 s | 164 |
| 4 | 346.0 s | 24/30 | 1.109 s | 421 |
| 5 | 200.1 s | 24/30 | 0.000 s | 2 |

All task-specific tests and targeted suites pass. The whole Fast gate remains
failed for the recorded existing/environment failures; it is not reported as
green. No rig validation was performed. The temporary isolated baseline build
tree was removed; its measured baseline server assembly and build log remain
in scratch alongside the publish output and raw evidence.

## Why bots stayed gone

The supplied server log records the crash at **2026-10-31 01:13:14 UTC** (the
brief's evidence heading is October 30). Worker logs show 194 fleet transport
losses on each worker plus each worker's probe. Worker 1 additionally lost its
two research commanders and logged 27 refused commander connection attempts.
The first two successful post-restart logons, at 01:13:23 UTC, are **research
commanders**, one per side. This accounts for the observed two peers; it was not
two fleet bots successfully recovering.

The code trace is:

1. `ObservedSession.OnSessionLost` logs the loss, removes watchdog tracking and
   forwards the loss to the actual client. A plain bot becomes disconnected;
   a normal `ConnectionSession` latches `Lost` unless its optional grace loop
   was armed.
2. `BotManager.IsParticipating` becomes false. `Loop` calls
   `RemoveInactiveBot("connection lost")`, removes the roster entry, clears
   orders and disposes the peer. It retains bounded removal history, not a
   desired fleet slot or a retry plan. The dead probe is not recreated either.
3. `StallWatchdog.Connect` attempts to arm optional grace only when its `client`
   argument is an `ObservedSession`. All its callers pass actual
   `ConnectionSession` or plain bot objects. `Track` creates the decorator
   separately. Consequently that type test is false, even with
   `ALLEGIANCE_RECONNECT_GRACE_SECONDS` configured.
4. Even fixing that test would not implement restart recovery. The existing
   session retry has a bounded window, needs an endpoint/request, stops after a
   validated LOGONACK, and permanently latches loss on exhaustion. A restarted
   server cannot preserve the old seat; a validated logon alone is not a fresh
   side choice, purchase and launch. Its replacement transport also bypasses the
   original watchdog decorator. Plain bots do not use this session retry.
5. The behavior/watchdog/logger threads continue on their existing cadence.
   Worker RPC and MCP stdin serving remain alive until canceled or EOF.
   `WorkerSupervisor.Healthy` and `ProcessWorker.Alive` test process/pipe
   liveness, not participating bot count. Coordination and stats can therefore
   keep succeeding with an empty fleet.
6. Worker zero's separate `ResearchLoop` repeatedly calls `EnsureCommander`.
   That helper explicitly recreates a disconnected commander; this is the retry
   path that did recover. It can continue using the old probe briefing to enter
   the loop, explaining the ongoing research/intel/gallery logs.

A real loopback process test killed the local server, started a new server at
the same endpoint and queried the same real MCP worker. One combat bot was
active before the kill; after restart it was `removed: connection lost`, with
zero active bots. The worker answered RPC with the same PID eight seconds after
restart. The experiment passed all three repetitions. This reproduces the
recovery failure without any rig access.

**Proposed harness fix, deferred because it is not small/scoped enough to safely
complete as a grace-branch correction:** retain an explicit desired preset and
its per-side slot counts at the supervisor. On probe loss, enter a visible
recovering state; quiesce and dispose the old fleet/commanders/probes; retry
creating fresh managers with bounded exponential backoff and jitter until the
operator stops or a configured deadline expires; then replay the preset once,
using the fresh core, mission, side roster, legal loadouts and map objectives.
Use a recovery epoch so stale decisions/late spawn results cannot re-register.
Stop/despawn/clear must cancel affected desired slots before a retry. Expose
requested/active/recovering/failed counts separately from process health, and
log exhaustion as a fleet failure. Do not blindly refill a deliberately
stopped or eliminated bot, reuse stale map targets or reset the human client's
session. Follow-up tests must cover repeated restarts, prolonged downtime,
changed maps/civs, partial respawns and stop during recovery. No bot, preset,
human-client or server reconnect behavior was changed in P899.

## Claude's rig checks

- Confirm the exact SDK/runtime/compiler embedded in b59 and preserve the
  executable/symbols. Symbolize `BroadcastPublisher.Return + 0xd7` to distinguish
  packet release from the final container push; obtain queue counter state if
  a dump exists. Do not infer the bag from the abbreviated stack alone.
- Run the new AOT build against the stated winning baseline: pooled transport,
  immutable snapshots, encode-once, parallel build, native ship updates,
  native GotoPlan and server GC. Keep interest tiers, native broadcast order and
  receive decoupling off. Compare tick p95/overruns, allocation/GC, egress,
  publication errors/coalescing and RSS through finale and multiple rotations.
- Soak beyond the prior 11.25-hour failure window. Local bounded checks prove
  containment and reuse, not long-duration rig stability.
- Until a supervisor recovery feature is implemented, a server restart still
  needs an explicit worker/supervisor restart and preset replay. Worker process
  liveness alone is not evidence that the fleet has rejoined.

## Native intent

**Design:** this is modern asynchronous-broadcast storage and fault containment;
no native gameplay rule was ported, so no parity CSV rule is claimed.
**Era constraint:** native client disconnection follows the loss path rather than
silently replaying a fleet preset (`clintlib/clintlib.cpp:2063–2068`;
`FedSrv/FedSrv.CPP:4549–4566`, `FedSrv/fsship.cpp:995`).
**Bug:** an unguarded background-thread cleanup exception could kill this server;
the harness also mistook surviving worker processes for useful fleet liveness.
**Modern better approach:** explicit ownership and simple synchronized reuse,
fault-contained cleanup, and a separate observable desired-fleet recovery state
machine for unattended bot harnesses.

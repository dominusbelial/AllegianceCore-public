# P907 — thread-pool and UDP receive-delay metrics

Added profiler-gated `[thread-pool]` and `[udp-receive]` diagnostics shared by
dedicated and embedded hosts. The bounded proof detects an approximately 800 ms
pool stall and recovery; the 200-client UDP cost comparison stays within observed
noise. Kernel arrival time remains explicitly unavailable.

Dispatch base `0b939613`, branch `codex/p907`. Changes remain in the working tree;
no commits, pushes, remote hosts, pool tuning, completion inlining, or receive-model
changes. Measurements below use local loopback, SDK 10.0.400 and the ordinary
external artwork. This is modern diagnostic infrastructure; no native parity
row is claimed complete by adding these counters.

## Instrumentation and interpretation

`ALLEGIANCE_TICK_PROFILER=1` or `profiler.enabled: true` enables the additions.
`profiler.schedulingMetrics` defaults true; false disables only P907 for the
profiler-on cost comparison. No new environment variable or listening port is
introduced. Sampling and reporting use the server's simulation thread and
existing profiler window callback.

| Metric | Meaning |
|---|---|
| `[thread-pool] scope=process windowSecs` | Actual monotonic elapsed window. Every pool user in this process contributes. |
| `threads` | `ThreadPool.ThreadCount`, sampled at reporting. |
| `pendingItems` | `ThreadPool.PendingWorkItemCount`, sampled at reporting; a gauge, not a window maximum. |
| `completedPerSecond` | Delta of `ThreadPool.CompletedWorkItemCount` / actual elapsed seconds. |
| `probeSamples` | Completed queue-to-execution probe samples harvested in this window. |
| `probeMeanMs`, `probeMaxMs` | Exact mean and maximum stopwatch delays from enqueue to worker entry. |
| `probeP95UpperMs` | Nearest-rank p95 upper bound from power-of-two microsecond histogram buckets, capped at observed max. |
| `probePendingMs` | Age of an unfinished probe. Zero samples with a large pending age still means a current stall. |
| `[udp-receive] asyncSamples` | Successful async socket completions consumed by the host worker in this window. |
| `syncReceives` | Successful synchronous completions, counted separately; they do not dilute async delay with zero samples. |
| `completionToWorkerMeanMs`, `completionToWorkerMaxMs` | Exact managed completion callback entry → existing dedicated worker processing delay, including scheduling and existing state-gate contention. |
| `completionToWorkerP95UpperMs` | Same bounded histogram interpretation as the probe. |
| `kernelArrivalToCompletion=unavailable` | There is no actual arrival-to-completion measurement. |
| `schedulingProxy=thread-pool-probe` | The independent probe is a proxy for completion-dispatch scheduling pressure, not a per-packet arrival estimate. |

Only one reusable probe may be outstanding, and it is enqueued at most once
per second. It uses `UnsafeQueueUserWorkItem` without capturing execution
context and needs no timer. A stalled pool cannot accumulate a backlog of probes.
The completion callback publishes its elapsed sample before the simulation
thread reuses the object. Disposal prevents more probes; an outstanding probe
retains only its small telemetry object, never a server, socket or client.

UDP stamps callback entry before publishing the existing volatile ready flag.
The receive worker records the other timestamp under its already-held state
gate. No new per-datagram allocation, lock, delegate, timer or queue item is
introduced. Fixed histograms allocate once; reporting drains UDP counters under
the existing gate once per window. The histogram's zero-allocation recording is
also tested. Empty distributions have zero count and zero summary values.

The current .NET 10 `SocketAsyncEventArgs.ReceiveFromAsync` path exposes no
`SO_TIMESTAMP` ancillary arrival timestamp. Local reference API inspection
confirmed no arrival field in its result; even `IPPacketInformation` describes
address/interface, not arrival time. Receive-post-to-completion timing would
include legitimate idle time, so it is deliberately not presented as an arrival
estimate. The real measured endpoint starts **after managed callback dispatch**.
A delayed completion can therefore have an excellent callback-to-worker result.
OS tracing or a platform-specific timestamped `recvmsg` path would be needed
for true kernel queue age; neither was introduced in this measurement-only task.

## Dedicated and embedded hosting

Both use the same `Server.SchedulingMetrics.cs` implementation. A dedicated
server writes the lines with its ordinary profiler output. The game installs
`CrashLog.Install` before hosting; its existing console tee copies exactly these
lines into `<data-dir>/logs/client.log`. Set the profiler variable on the game
process to observe the pool shared by rendering/client work and the embedded
server. Skirmish has the pool line; Host LAN's additional UDP listener also has
the receive line. No HUD or rendering code is changed.
Reporting follows the existing profiler: paused skirmish windows flush after
simulation ticks resume.

`P907SchedulingMetricsTests` covers dedicated/shared profiles, profiler off,
the P907-only off switch, histogram count/mean/max/bounds and allocation, and
probe cadence/window consumption/disposal. The separate local-host proof starts
real `SkirmishHost` and Host LAN lifecycles with the actual client console tee,
checks owned bot liveness, and reads the resulting rolling log. Both real host
modes passed **3/3**; all six logs report `botsAlive=True`. Skirmish's process
probe maxima were 0.654/0.455/0.643 ms, and Host LAN also emitted the UDP line
(its owned bots used TCP, so those particular receive distributions are empty).
[Actual client-log receipts](P907_assets/local-host-summary.json). The AOT smoke
also validated a real UDP game logon and measured nonempty receive windows:
[AOT metric output](P907_assets/aot-metrics.log).

## Bounded stall proof

`tools/p907-scheduling-proof` creates a real UDP address-validated session and
delivers reliable payloads. In a separate four-effective-CPU process it queues
32 work items blocked on an event, queues a probe behind them and sends another
reliable UDP frame. Its dedicated main thread releases the event after 800 ms.
Every wait also has a five-second timeout; `finally` releases and joins all work.
There are no busy loops, pool min/max changes, or synthetic CPU work. The UDP
receive timestamp is taken in the actual receiver callback, not when the main
thread later observes its event.

```bash
DOTNET_PROCESSOR_COUNT=4 ~/.dotnet/dotnet \
  tools/p907-scheduling-proof/bin/Debug/net10.0/p907-scheduling-proof.dll
```

All three isolated final runs passed:

| Run | Pending probe ms | Released probe max ms | Recovered probe max ms | Actual UDP delivery ms |
|---|---:|---:|---:|---:|
| 1 | 800.175 | 800.447 | 0.141 | 800.605 |
| 2 | 800.179 | 800.400 | 0.142 | 800.489 |
| 3 | 800.286 | 800.602 | 0.166 | 800.723 |

Run 1's baseline probe was **0.055 ms** and its released-window UDP
callback-to-worker max only **0.122 ms**: the missing 800 ms was before the
managed callback began. During each stall `pendingItems=31` and no new async
receive sample completed. [Stall receipts](P907_assets/stall-summary.json).
This proves visibility of a bounded pool stall, not a claim that ordinary
gameplay currently starves the pool.

## Cost at 200 real clients

The proof reuses P895's real socket clients with a separate server process.
Both arms retain profiler output, immutable snapshots, encode-once, parallel
build, native ship budget, pooled TCP, native GotoPlan and server GC. Interest
tiers, native broadcast ordering and receive decoupling remain off. Identical
crowding suppression keeps all 200 fixture ships alive. No builds or other
tests from this lane run alongside cost measurements. Order alternates across
repeats to reduce temporal bias. Load averages accompany each receipt.

Tick samples include the proof's normal send-window reporting. Allocation
includes the whole server process, including workers, diagnostic formatting and
logging; the separate client process is excluded. These are bounded loopback
samples, not rig performance certification or confidence intervals.
The cost harness runs the Release managed server; Native AOT is separately
functionally exercised, not performance-certified by this harness.

The original process-wide `GC.GetTotalAllocatedBytes(true)` endpoint is retained
but **does not provide a usable allocation cost result on this local runtime**.
Both metrics-off and metrics-on runs recorded negative deltas in the existing
`[gcprof]` line, including -272.8 MB over a TCP window. This contradicts the
API's intended [cumulative allocation semantics](https://github.com/dotnet/runtime/blob/main/src/coreclr/System.Private.CoreLib/src/System/GC.CoreCLR.cs).
The cause is not diagnosed or attributed to a particular GC mechanism here.
Some gen2 windows also have small positive deltas, so simply excluding or
clamping negative windows would not repair the estimate.
[Preserved counter anomalies](P907_assets/allocation-counter-anomalies.json).

The independent cost cross-check enables a **proof-only** runtime event listener
in both arms and sums `GCAllocationTick.AllocationAmount64`. This reports sampled
allocation chunks (roughly 100 KB per-thread boundaries), not an exact byte
census. Event dispatch/formatting overhead is included equally in both arms;
no production event listener or GC setting is added. A four-client smoke check
received 119 samples and measured 6.310 MB/s versus 6.305 MB/s from the cumulative
counter in that short, non-decreasing interval. The proof fails if allocation
events are unavailable. Final event-based 200-client results appear below.

The initial 1.5-second-warmup, three × 20-second comparison retained 200/200
clients in all twelve runs. Median-of-run results:

| Protocol / P907 | Tick mean ms | Tick p95 ms | Raw counter endpoint MB/s (unreliable) | CPU cores |
|---|---:|---:|---:|---:|
| TCP / off | 5.048 | 5.991 | 31.444 | 0.423 |
| TCP / on | 5.235 | 6.093 | 31.461 | 0.431 |
| UDP / off | 5.363 | 6.636 | 30.832 | 0.644 |
| UDP / on | 5.477 | 6.852 | 33.608 | 0.646 |

The short UDP counter endpoints differ by 9% at their medians. A ten-second
warmup and three × 30-second repeat reduced tick means to TCP off/on
5.083/5.065 ms and UDP off/on 5.102/4.791 ms, but still exposed cumulative-counter
backsteps. Neither set of counter endpoints is used to certify allocation cost.
[Initial raw runs](P907_assets/cost-initial-runs.json),
[longer raw runs](P907_assets/cost-steady-runs.json).
Reproduce the independent event-based comparison:

```bash
~/.dotnet/dotnet build tools/p895-transport-proof -c Release
python3 tools/p907-scheduling-proof/run-cost.py \
  --allocation-events --warmup-ms 10000 --seconds 20 --output .scratch/p907/cost-events
```

The final twelve event-instrumented runs again retained **200/200** connections.
Each arm had three runs after ten seconds of warmup, about 404 measured ticks
per run. Median-of-run results (allocation uses the independent event sum):

| Protocol / P907 | Tick mean ms | Tick p95 ms | Sampled allocation MB/s | CPU cores |
|---|---:|---:|---:|---:|
| TCP / off | 5.175 | 6.130 | 33.680 | 0.423 |
| TCP / on | 5.257 | 6.536 | 35.479 | 0.430 |
| UDP / off | 5.110 | 6.536 | 65.778 | 0.631 |
| UDP / on | 5.125 | 6.610 | 65.480 | 0.622 |

For the primary UDP path, mean tick changed **+0.28%**, tick p95 **+1.14%**,
sampled allocation **-0.45%**, and CPU **-1.39%**: within observed run-to-run
noise. TCP's median mean tick changed +1.57%; its sampled allocation changed
+5.34%, within the combined observed sampling/run spread (off 33.368–33.719 MB/s,
on 33.490–36.749 MB/s). TCP p95 ranges also overlap (off 6.016–6.419 ms,
on 6.349–6.868 ms). Small TCP differences cannot be resolved confidently with
these sampled allocation boundaries and three short runs; this is not a
statistical equivalence claim. The allocation-free per-datagram recording check
and stable UDP cost support retaining the measurement additions. No tuning was
made to achieve these results. Initial load averages for these runs ranged
0.894–2.335 on the shared 24-logical-CPU host.

[Final runs](P907_assets/cost-events-runs.json),
[median summary](P907_assets/cost-events-summary.json),
[ranges and percentage comparisons](P907_assets/cost-comparison.json).

## Mixed-fleet rig plan for Claude

UDP is already usable by the MCP fleet. The trace is:
`StallWatchdog.Track` → `ObservedSession` constructor →
`TransportFactory.CreateClient(this)` → process environment protocol selection
→ bounded `FallbackTransport.Join`. `ObservedSession.Attach` installs that
transport in a fresh `ConnectionSession` or plain `Bot`. Worker processes inherit
their supervisor environment through `ProcessStartInfo`; no worker override
replaces these variables. The Bot/ConnectionSession property override applies to
their ordinary `Connect` paths; the MCP decoration specifically uses the factory
environment defaults. No bots-only code change is needed or built.
Source trace: `tools/allegiance-mcp/ObservedSession.cs:28–57`,
`tools/allegiance-mcp/StallWatchdog.cs:45–79`,
`tools/allegiance-mcp/WorkerRpc.cs:46–53`, and
`src/Allegiance.Core/Transport/TransportFactory.cs:6–17,44–63`.

Keep the winning server baseline and add this fragment to Claude's private
server JSON (ordinary TCP remains available):

```json
{
  "transport": { "mode": "pool", "protocol": "udp", "udpPort": 6073 },
  "profiler": { "enabled": true, "schedulingMetrics": true, "reportIntervalSeconds": 5 }
}
```

`ALLEGIANCE_TRANSPORT_PROTOCOL=udp` must also be used on that server if an
existing environment override would otherwise force TCP. A distinct UDP port
is possible but must be opened and advertised separately. Preserve the rig's
other broadcast/sim/GC settings; do not enable experimental ordering/decoupling.
Retain its existing fleet-sized admission limits: for a single-machine 200v200
test, `transport.maxConnections` and `transport.maxConnectionsPerIp` of 1024
leave room for both protocol populations and helper sessions. Keep each game's
`maxPlayersPerTeam` at least 200 (256 leaves room for seated helpers). Check
the corresponding `ALLEGIANCE_TRANSPORT_MAX_CONNECTIONS` /
`ALLEGIANCE_TRANSPORT_MAX_PER_IP` overrides, which otherwise win over JSON.

For a mixed match today, use **two independent MCP supervisors** against the same
ordinary TCP endpoint. Use separate working directories for their worker stderr
files and independent MCP connections. Their child workers inherit each choice:

```bash
env ALLEGIANCE_TRANSPORT_PROTOCOL=tcp \
  ~/.dotnet/dotnet <build>/allegiance-mcp.dll <server-address> 6073 --workers 4

env ALLEGIANCE_TRANSPORT_PROTOCOL=udp ALLEGIANCE_TRANSPORT_UDP_PORT=6073 \
  ~/.dotnet/dotnet <build>/allegiance-mcp.dll <server-address> 6073 --workers 4
```

Submit `spawn_scenario` separately to each supervisor with
`{"preset":"showcase","count_a":100,"count_b":100,"finale_after_minutes":0}`
for a combined 200v200, half TCP/half UDP on each side. For a smaller 100v100
diagnostic match use 50/50 per supervisor. Select the same rendezvous/route and
staging settings on both. The supervisors coordinate their own fleets only;
this is a practical existing mixed-fleet route, not unified cross-supervisor
wing/staging coordination. Probe/commander helper sessions may add a few peers.

The protocol preference is process-wide in this MCP path, so a single supervisor
cannot assign individual bots different protocols today. Changing the
environment after sessions exist does not migrate them. Confirm UDP actually
joined using **server peer IDs with bit `0x80000000` set**; a requested UDP session
can fall back to TCP after its three-second handshake. An idle UDP listener and
the mere presence of `[udp-receive]` do not prove the requested fleet used UDP.

Record the ordinary TCP control first, then mixed, then all-UDP with identical
match/settings/observation length and at least two matches per arm. Retain
`[tickprof]`, `[gcprof]`, `[thread-pool]`, `[udp-receive]`, egress and client cadence
lines, connection counts and fallback evidence. Start with the profiler's default
five-second windows and exclude boot/join warmup. This lane contacted no rig,
mini, Mac or other remote host.

## Investigation thresholds

These are initial diagnostic heuristics for Claude, not native rules or automatic
tuning triggers. Compare against same-host/process baseline and the client frame
log when using an embedded host:

* Investigate pool probe p95 upper bounds above **10 ms for three windows**,
  repeated maxima above **50 ms**, or an unfinished probe above **100 ms**.
  A pending age near/above **500 ms** deserves immediate correlation with UDP
  handshake/receive progress. Zero completed probe samples is not a healthy
  signal while `probePendingMs` grows.
* Investigate callback-to-worker p95 bounds above **5 ms** or repeated maxima
  above **50 ms**, especially if pool probe latency remains low. Look at the
  receive worker, existing gate contention and OS scheduling rather than
  attributing that handoff automatically to the pool.
* A nonzero pending-work gauge alone is not a fault. Correlate backlog growth,
  completed rate, delay, GC pauses, tick overruns and actual UDP peer progress.
  At 20 Hz, 50 ms consumes a complete simulation tick. Stable low handoff delay
  does not rule out pre-callback delay, loss or a client-side problem.

## Validation and receipts

* Solution build: **0 warnings, 0 errors**. All **103** tool projects built;
  the subsequently extended allocation proof also rebuilt with 0 warnings/errors.
* Server `linux-x64`, self-contained Native AOT publish: clean, no IL2026/IL3050.
  A real UDP game logon against the native binary validated and emitted both
  metric lines, including nonempty async receive samples.
* New regression cohort: **6/6 × 3** alone, and all six passed again in Fast.
* Stall proof: **3/3**. Real skirmish/client-log proof: **3/3**. Real Host LAN
  client-log proof: **3/3**.
* Showcase never-parks: **5/5**, serially, with no other build/test from this
  lane alongside them. Test durations: **381.788 / 232.318 / 241.167 / 256.020 /
  348.726 s**. Coverage reached 26/30 in the first run and 24/30 in each other
  run; every run recorded real shots and hits and asserted zero simulation-stage
  failures. Worst stopped-with-target interval: **1.111 s**, below five seconds.
  This matches P906's saved **5/5** pass-rate control; no showcase performance
  improvement is claimed. [Showcase receipts](P907_assets/showcase-summary.json).
* UDP/loopback cohort: **36/36 × 3**. The first full transport run was
  212/214: an overly broad harness `ALLEGIANCE_TRANSPORT_MODE=pool` overrode two
  tests' explicit legacy configurations. The complete corrected run passes
  **214/214**, and each legacy case passes **3/3** with that override cleared.
* Exactly **one** Fast sweep: **12,789 passed / 9 failed / 4 skipped**, 12,802
  executions in **536.045 s**. All nine infrastructure checks passed. Three
  executions exceeded ten seconds. No Fast rerun or weakened assertion.
  [Unmodified Fast summary](P907_assets/fast-summary.json).
* Glossary links, parity CSV validation, `docker compose config`, diff whitespace
  and working-tree publish-hygiene scans pass. No new media was created.

Four Fast failures came from this lane's native-source harness pointing at
`src` instead of the native repository root; the optional tester-core case also
skipped for that reason. The isolated controls use the correct repository root.
The other failures were the existing bundle-path fixture topology, own-ship
join-burst status receipt, probe rescue, wingman defend and stale dock-status
families. Slow Fast cases were the known real-hull GPU proof, gunner carrier
purchase and warp-exit touch-rate sample; all three pass 3/3 alone below ten
seconds. The four native-path failures and tester-core skip recover 3/3 with
the correct root, and probe rescue and the bundle-path control also pass 3/3.

The existing wingman-defend and own-ship-status cases still fail **3/3**; the
docking receipt is **fail/pass/pass**. Their exact signatures and prior baseline
history remain open in [the P907 flake-ledger section](../TEST_FLAKES.md#p907-verification-2026-10-31-utc).
The original Fast result remains **FAIL**. No unrelated simulation/status fix or
weakened assertion was added to obtain a green headline.

There are **45** final individual isolation executions across 15 methods,
plus the complete 214-test corrected transport run. An interleaved follow-up
harness attempt was discarded; only the fresh serial `isolation-final` receipts
are credited. [Isolation results](P907_assets/isolation-summary.json),
[build/new-test/transport receipts](P907_assets/gates-summary.json).

Raw logs/TRX remain in [local task scratch](../../.scratch/p907/); only small
summaries are retained in `docs/reports/P907_assets/`.

## Native-intent footer

**Design:** native sends guaranteed game flow without expiry and nonsequential
updates with a 500 ms lifetime (`_Utility/Messages.cpp:548–563`). This task changes
neither delivery contract nor game behavior. Native named server counters
(`FedSrv/counters.h:10–21`) remain separate from these process-runtime counters.

**Era constraint:** DirectPlay owns native transport scheduling. The modern port
uses .NET socket completion dispatch and a dedicated UDP protocol worker; those
runtime scheduling boundaries have no native rule to certify through a parity
row. No native default or sim/bot rule is changed.

**Bug assessment:** the CI handshake timeout under pool pressure is a real
observed test symptom. This task does not assert a real-play starvation bug,
increase pool minimums or alter completions to make that symptom disappear.

**Modern better approach:** measure queue delay, completion handoff, backlog and
actual UDP fleet progress first. If those implicate pre-callback delay, use OS
tracing or timestamped receive APIs in a separately reviewed experiment before
choosing a receive architecture or pool policy.

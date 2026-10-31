# P658 — bot driver supervisor

P900 update: multiplayer invocation now uses a supervisor even with one worker.
Showcase recovery, cancellation, and fleet health are documented in
[MCP fleet recovery](../MCP_FLEET_RECOVERY.md). The single-manager invocation
description below records P658's behavior before P900.

Implemented on `codex/p658`, with P657/P657b already present at `d8852bc7`.
Changes are confined to `tools/allegiance-mcp`, the requested ClientCore tests,
and this report. No commits or pushes were made.

## Running it

Direct invocation:

```sh
cd "$HOME/allegiance-bots"
./allegiance-mcp <host> <port> --workers 2
```

Omitting `--workers`, or specifying `--workers 1`, uses the original single
`BotManager`, probe and decision loop. It launches no children, runs no
supervisor coordination polling, and advertises the original eleven tools.
Existing single-process grant response fields are preserved.

With `--workers 2`, the externally visible process serves the same MCP
initialize/tools protocol, and launches two copies of itself with internal
`--worker 1/2` and `--worker 2/2` options. Each child owns a probe, bot
connections, GC heaps, thread pool, decision loop and P657b stall watchdog.
The supervisor has no game connection or bot decision loop. Children inherit
the current GC/runtime/environment settings; this change does not silently
switch GC modes.

### Mac `run-bots.command`

If the Mac launcher starts the binary directly, append `--workers 2` to that
binary's arguments. If it uses the existing Python MCP driver, that driver's
launch list is `[EXE, HOST, PORT]` and does not forward extra binary flags.
Use a small executable wrapper as its `EXE` argument:

```sh
cat > "$HOME/allegiance-bots/allegiance-mcp-workers" <<'SH'
#!/bin/sh
cd "$HOME/allegiance-bots" || exit 1
exec ./allegiance-mcp "$@" --workers 2
SH
chmod +x "$HOME/allegiance-bots/allegiance-mcp-workers"
```

In `run-bots.command`, change only the MCP executable argument passed to its
Python driver from `.../allegiance-mcp` to
`"$HOME/allegiance-bots/allegiance-mcp-workers"`. Keep its existing host, port,
command file, output file and error file arguments. There is still one launcher,
one driver and one command/output stream. The wrapper uses `exec`, so driver
termination reaches the supervisor itself. The Mac-local launcher was not
available in this worktree and has not been edited.

Worker diagnostics go to `err.worker-1.log`, `err.worker-2.log`, etc., in the
supervisor's working directory. The wrapper selects the existing bot directory.
Logs are replaced at startup. Each includes, before probe connection:

```text
allegiance-mcp worker 1/2: gc = server; pid = <actual child pid>
```

The actual GC mode is reported; it can instead be `workstation`. The original
driver's stderr file contains the supervisor's PID/startup and coordination
errors. Inspect each worker file for its existing `[stall]`, `[stall-minute]`,
`[session-loss]` and spawn-failure diagnostics.

## IPC and ownership

Each child has one redirected stdin/stdout pipe pair carrying newline-delimited
JSON-RPC 2.0 requests and responses, with numeric request IDs. No listener,
socket discovery, authentication configuration or named-pipe cleanup is needed.
This works with both a published apphost and `dotnet allegiance-mcp.dll` and
keeps IPC independent of the game server's network connections.

Writes are serialized; a dedicated asynchronous response reader matches IDs to
pending requests. EOF, broken pipes and malformed protocol fail the pending
calls and mark the child lost. A protocol failure also kills that child, so
untracked bots cannot keep fighting. Tool errors remain tool errors and do not
kill a healthy worker. All worker console diagnostics use stderr; protocol
responses use raw stdout.

Worker control requests can execute during a long tool call. Mutating game
tools are serialized in each worker, while coordination/inventory requests
remain available. The supervisor serializes mutating MCP calls and fans each
scenario's worker calls out concurrently. Children are disposed on normal MCP
EOF, SIGTERM/SIGINT, startup failure and supervisor process exit. A child also
watches its parent PID from before probe startup and exits if that parent dies,
covering an abrupt SIGKILL. The supervisor kills child process trees and drains
stderr during disposal.

## Scenario splitting and honest merging

Every worker resolves the **complete original scenario plan**, including
homogeneous, mixed-fleet and showcase modes, per-side tech/loadouts, bomber
objectives, route assignment, mine slicing and mirrored arena positions. It
then selects every Nth slot independently within each side. This avoids
replanning each fragment as a smaller scenario, which could overlap arena
positions or change route assignments and composition.

For a side requesting T bots, worker i receives `T / N + (i < T % N ? 1 : 0)`
slots, using zero-based i. Thus 13 per side becomes 7+6 per side; 195 per side
becomes 98+97 per side, for worker totals 196 and 194. Empty partitions are valid.
Original slot indices are preserved in failure reports. Worker bot names include
`mcp-w<i>-` to identify ownership and avoid cross-process name collisions.

Preflight resolves every worker plan without spawning or laying minefields.
The supervisor holds staging on all children, starts their scenario calls in
parallel, then checks their inventories before returning. P657's own-session
launch confirmation and bounded refill remain the actual spawn path. Counts
sum requested slots and count only grants whose names are still active at the
final inventory checks. Disconnected, exited or unresponsive partitions count
as failed slots. Every failed attempt is retained, and an additional named
failure explains a bot lost while another worker was still spawning. Refilled
counts follow the surviving individual replacement bots.

The report retains the original global scenario metadata and full fleet
composition, and merges per-side requested/spawned/failed/refilled counts,
grants, warnings, failures and minefield results. Worker 1 alone lays requested
minefields, preventing duplicate investments/build orders. Staging stays held
until the parallel spawn and final checks complete; existing staging timeouts
still apply after that hold is lifted.

After a crash, later scenarios still spawn the original partitions on healthy
workers and explicitly fail the lost worker's requested slots. There is no
automatic respawn or redistribution; restart the supervisor to restore N live
workers. This preserves ownership, composition and truthful counts.

## Fleet-wide coordination

The supervisor aggregates coordination snapshots published by each decision
loop at most five times per second. It sends decisions back over the control
pipe. Workers execute movement, firing and visible-threat handling locally.

**Staging:** groups are identified by mission cookie, side, objective sector and
route index. Living/staged member counts are summed across all workers. A group
releases when its entire wing is assembled or its existing hold timeout expires.
`ALLEGIANCE_BOT_SYNC_WINGS=on` additionally requires the other route groups for
that side/objective to assemble. The existing route-hop timeout offset is
preserved. Distinct bomber objectives and missions do not share a barrier.
Lost workers' members leave the barrier. A live worker with missing or stale
state blocks releases; received decisions also expire after two seconds.
Spawn hold transitions also advance a per-worker epoch: snapshots must come
from a decision pass in that epoch, and decisions must match it. Thus an old
response in flight cannot release new members after spawning completes.
A worker with no started loop has a valid empty snapshot.

**Base defence:** each worker reports local alerts/threat ages, live friendly
bases, the eligible side population and eligible fighter/interceptor distances.
The supervisor computes `min(cap, ceil(globalSideCount * share))`, subtracts
existing deployments on that mission/side, ranks all workers' candidates by
hops, local distance and ship ID, and assigns one bounded deployment per
incident. Repeated alerts extend that incident without recruiting another wing.
The existing quiet/minimum deployment intervals remain 20/30 seconds. Workers
apply the named recalls and restore original objectives/routes on release;
local visible attackers still interrupt travel immediately. Remote deployments
do not become new locally observed threats, so decisions cannot keep an incident
alive through feedback. Defence decisions expire with the control heartbeat.

**Finale/observed scenarios:** there is no finale trigger in
`tools/allegiance-mcp`. The existing `ScPhase.Finale` belongs to
`tools/vizfight/Program.cs`, outside the permitted scope, and governs that
separate tool's own fleet. No worker-local finale trigger was introduced.
`run_scenario` and `run_hull_matrix` retain one owning worker for their complete
choreography, observation and cleanup. Their outcomes therefore retain the
full scenario roster. Fleet queries used by external orchestration merge all
workers, including lost-bot status, so external population/outcome decisions
have a global view.

## Tool routing and telemetry

- `spawn_bot`: round robin across healthy workers.
- `spawn_scenario`: parallel partitions with the merged global report above.
- `list_active_bots`: combined live/removal inventories; known bots of a crashed
  worker are marked `status: "lost"`. A worker loss row is included even if its
  last inventory was empty. Slots without an observed bot identity are accounted
  for as failed scenario slots.
- `get_perf_stats`: complete per-worker reports plus global aggregation. Cadence
  means, nearest-rank p95 and worst offenders use pooled real rolling samples,
  rather than averaging worker percentiles. Combat rows/counts, resources,
  throughput, GC counters/pause totals and stall incident/session counters sum.
  Hit rates and time-to-kill means are recomputed with their denominators.
  Route arrival counts are grouped by side/route.
- Latency maxima and decision-pass percentiles are explicitly labelled worker
  envelopes; per-bot decision rate reports the slowest worker. Percentile
  envelopes are not represented as a measured pooled decision-pass p95.
  Every original P657b current/completed-minute and live stall field remains
  available in each worker report; the merged `stall` sums additive counters
  and takes maximum latency/peak envelopes.
- `stop_bots`, `clear_bots`, `despawn_bot`: additive supervisor tools that fan out
  to all workers, sum actual affected bots, and report worker failures.
  `clear_bots` also clears removal/lost-bot inventory history. Probes remain
  running. The original single-worker tool list is unchanged.
- `list_drones`: merged and deduplicated. Catalog queries and individual
  investment/drone commands use one healthy worker; global investments are not
  repeated N times. Observed scenario/matrix reports come from their owning
  worker. Unknown tools still return errors.

## Validation

Required build:

```sh
dotnet build AllegianceCore.slnx --no-restore -m:1
```

Result: **0 warnings, 0 errors**. The fresh worktree initially lacked restore
assets; restore succeeded using the cached packages with `-m:1` and
`-p:NuGetAudit=false` before the required no-restore build.

**150 tests passed, 0 failed, 0 skipped**, across `P658SupervisorTests`,
`BotFixRegressionTests` (including base defence and worker integration),
`P657SpawnReliabilityTests`, `P657bStallTelemetryTests`,
`P637BomberRoutingAndStagingTests`, `P635MultiRouteTests`, and
`PerfStatsAggregationTests`.

P658 coverage includes odd/empty/N=1 splits, independent asymmetric sides,
unchanged default options/tool list, parallel fake-worker spawning, result and
bot-list merging, late disconnects, surviving replacement counts, stop/clear/
despawn fan-out, lost partitions and subsequent scenarios, a real fake child
exiting while the supervisor remains usable, stderr capture and child cleanup,
pooled cadence/stall aggregation, cross-worker wing release, sync barriers,
mission/objective isolation, stale snapshots/decision expiry, and global ranked
recall quotas with incident expiry. Socket-free controller tests also prove the
workers actually apply supervisor staging/defence decisions and disconnect
peers on stop/clear.

The normal `dotnet test` runner was attempted but could not initialize because
this managed environment denies its localhost communication socket
(`SocketException (13): Permission denied`). Tests were instead discovered and
executed through the actual xUnit `XunitTestFramework` in a temporary in-process
runner under `/tmp/p658-test-runner`, with test parallelization disabled. This
was not a hand invocation of test methods. Build/test evidence is in
`/tmp/p658-build.log` and `/tmp/p658-test-results.log` for this session.

On a host permitting VSTest sockets, the same selected tests can be run with:

```sh
dotnet test tests/Allegiance.ClientCore.Tests/Allegiance.ClientCore.Tests.csproj \
  --no-restore -m:1 \
  --filter 'FullyQualifiedName~P658SupervisorTests|FullyQualifiedName~BotFixRegressionTests|FullyQualifiedName~P657SpawnReliabilityTests|FullyQualifiedName~P657bStallTelemetryTests|FullyQualifiedName~P637BomberRoutingAndStagingTests|FullyQualifiedName~P635MultiRouteTests|FullyQualifiedName~PerfStatsAggregationTests'
```

The Mac's 390-bot live run and performance comparison were not executed here.
The implementation and process/coordination tests establish behavior; actual
Mac load and spawn-time improvement still require measurement on that host.

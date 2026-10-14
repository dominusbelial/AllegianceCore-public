# P657 — spawn reliability

Implemented on `codex/p657`. Changes are left in the working tree; no commits or pushes.

## Launch confirmation

Removed both mandatory four-second probe-streaming checks. Every spawn, including a bot on the side opposite the probe, now requires its own purchase acknowledgement and fresh launch evidence from its own connection.

- Combat clients read `ClientWorld` under the session gate: the assigned own ship must be alive, present, in a sector, and undocked, with a fresh pose or status/undock transition since the purchase baseline.
- Plain idle/weave clients do not expose a `ClientWorld`. They use their own locked wire snapshots: a fresh `SET_CLUSTER`, own flying `SHIP_STATUS`, undock notification, or fresh own pose when already in space. Another ship's status and the original seating assignment cannot confirm launch.
- Launch waits **15 seconds per attempt** by default. `ALLEGIANCE_MCP_SPAWN_TIMEOUT_MS` accepts 1–120000 milliseconds; absent/invalid values use 15000. After a timeout, the same session re-sends the same buy/launch request once. No further launch retries occur in that session. Disconnects and explicit hull refusals fail with a specific reason.
- Requested/successor hull matching still ignores a late seating acknowledgement. Existing refusal, successor-upgrade, and partial-grant reporting is retained.
- `ALLEGIANCE_MCP_SPAWN_PROBE_DIAGNOSTICS=1` enables an immediate probe-visibility diagnostic after own launch confirmation. It does not wait for the probe or require visibility.

The existing `PendingOwnership` guards retain ownership until registration succeeds. Timeout, disconnection, and post-registration failures roll back and dispose the pending peer. A final connectivity check precedes ownership transfer. Plain clients also record their confirmed ship ID and launch state in the registry.

## Reporting and refill

Both homogeneous and mixed-fleet/showcase scenarios now use the same slot accounting. Each side reports `requested`, `spawned`, `failed`, and `refilled`. `spawned` counts successful slots whose registered bots are still connected at scenario completion; `failed = requested - spawned`. Mixed-fleet reports no longer substitute planned fleet totals for successful spawns.

Scenario argument **`refill` defaults to `true`**. After the initial pass, each failed slot gets at most one new-session retry, retaining its loadout, role, route, staging, and arena placement. Bots disconnected/disposed while later slots were spawning are also eligible for refill. A final reconciliation excludes bots lost during refill. Refill improves balance but cannot guarantee it if a retry also fails.

`failures` retains every failed attempt, including recovered slots, with slot index, side, hull, attempt number, and reason. Thus its length can exceed the final `failed` count. Grants and warning counts include only final successful slots. Complete scenario failure remains an MCP error; partial success returns the honest summary.

`spawn_bot` returns requested/spawned/failed totals and per-side counts on both success and error, with error reasons in `failures`. Its existing successful ship/loadout fields remain available.

Each failed spawn attempt emits one `[p657]` line to stderr, which the existing harness captures in `err.log`. Newlines in diagnostic values are flattened. Scenarios log their own slot failures and suppress duplicate manager-level logging. Connections lost during a scenario also receive a reason line.

## Validation

The checkout initially lacked NuGet restore assets. Restored cached dependencies with:

```sh
dotnet restore AllegianceCore.slnx --ignore-failed-sources --disable-parallel -m:1 -p:NuGetAudit=false
dotnet build AllegianceCore.slnx --no-restore -m:1
```

Final solution build: **0 warnings, 0 errors**. Build log: `/tmp/p657-build.log`.

Added `tests/Allegiance.ClientCore.Tests/P657SpawnReliabilityTests.cs`: **22 cases passed**. Tests feed encoded wire messages directly to the real combat and plain receive dispatchers, without sockets. They cover own launch with an empty probe, all three launch signals, stale seating state, other-ship status, bounded timeout/retry, disposal and failure accounting, disconnection, refusal, late seating acknowledgements, refill success/exhaustion, bots disposed during later spawns, and the `spawn_bot` error response.

The usual command was attempted:

```sh
dotnet test tests/Allegiance.ClientCore.Tests/Allegiance.ClientCore.Tests.csproj --no-restore -m:1 --filter FullyQualifiedName~P657SpawnReliabilityTests
```

VSTest aborted before executing tests because this sandbox denies the local TCP listener used for test-host communication (`SocketException (13): Permission denied`). Tests were instead discovered and executed through the installed xUnit framework's in-process discoverer/executor, preserving xUnit theory expansion and assertions. The temporary runner is `/tmp/p657-runner/Program.cs`.

```sh
dotnet /tmp/p657-runner/bin/Debug/net10.0/p657-runner.dll
dotnet /tmp/p657-runner/bin/Debug/net10.0/p657-runner.dll P657SpawnReliabilityTests BotFixRegressionTests BotSpawnerResolutionTests P557ShowcaseFleetTests ClientWorldDockLoadoutTests
```

- P657 suite: **22 passed, 0 failed, 0 skipped** (`/tmp/p657-inprocess.log`).
- Related regression selection, including P657: **127 passed, 0 failed, 0 skipped** (`/tmp/p657-regressions.log`).
- `git diff --check`: passed.

No live 200v200 battle was run; validation establishes the socket-free launch, cleanup, accounting, and refill behavior, not a live stress-test outcome.

## Changed files

- `tools/allegiance-mcp/BotManager.cs`
- `tools/allegiance-mcp/McpTools.cs`
- `tools/allegiance-mcp/SpawnLaunch.cs`
- `tools/allegiance-mcp/SpawnBatch.cs`
- `tests/Allegiance.ClientCore.Tests/P657SpawnReliabilityTests.cs`
- This requested report.

No server or client-core source was modified.

## P657b stall telemetry

Added bot-host telemetry on top of the P657 working-tree changes. All implementation changes remain in `tools/allegiance-mcp`, with socket-free tests in `tests/Allegiance.ClientCore.Tests`; no commits or pushes were made.

### What is measured

The host starts a dedicated background thread named `allegiance-mcp-stall-watchdog`, with `ThreadPriority.Highest`, as soon as the manager starts (including probe startup and spawning). It wakes approximately every **100 ms**, measures lateness against its next monotonic deadline, and drops missed ticks rather than running catch-up passes. It uses neither the ThreadPool nor the decision-loop, roster, or session gates to sample sessions. Priority is a runtime/OS scheduling request, not a guarantee against GC suspension or CPU starvation.

Combat, idle/weave, probe, and commander connections are all tracked, including sessions still spawning. `ObservedSession` decorates the normal `TcpTransport` callback: a timestamp is updated immediately after each **complete data-frame read**, before client decode takes its gate. This distinguishes socket-read progress from ship-pose freshness and from a callback blocked on decoding. Before the first frame, age is measured from the connection attempt. Counts use strict **> 2000 ms** and **> 5000 ms** comparisons. A quiet server or network delay can also produce old reads; these metrics alone do not prove receive-thread starvation.

Both upstream client types are sealed and expose no transport injection API. Four narrow .NET `UnsafeAccessor` field bindings in `ObservedSession.cs` install the decorated transport on a **fresh** client before `Join` starts threads and read the existing transport's loss detail/shutdown flag. Combat initialization mirrors the normal initial world reset. All codecs, world updates, sends, TCP framing, and loss callbacks still run through the existing implementations. Socket-free tests exercise all four bindings and both real receive dispatchers. Future upstream changes to these private field names/types require updating these bindings.

Decision telemetry records every completed loop pass, including failed passes and resource-sampling work, and reports the age of a pass still in progress. GC total pause time is the delta of `GC.GetTotalPauseDuration`; generation counts are per-window deltas of `GC.CollectionCount(0/1/2)`. The longest observed individual pause is taken from new `GC.GetGCMemoryInfo(GCKind.Any).PauseDurations`, supplemented by the latest ephemeral, full-blocking, and background collections. Collection indices prevent stale pauses from being counted again after a minute reset. Because these APIs expose the latest collection, intermediate collections between samples can be missed by the **maximum**; total pause time still comes from the cumulative runtime counter. See Microsoft's [GC memory-info explanation](https://devblogs.microsoft.com/dotnet/the-updated-getgcmemoryinfo-api-in-net-5-0-and-how-it-can-help-you/).

ThreadPool observations read `ThreadCount`, `PendingWorkItemCount`, and `CompletedWorkItemCount`. `threadPoolStarvationSuspected` means queued work has been observed with **no completed-work counter progress for more than one second**. This is a diagnostic heuristic, not a definitive runtime starvation event; long-running work can produce it too. Progress or an empty queue clears the duration. Microsoft's [ThreadPool starvation guide](https://learn.microsoft.com/dotnet/core/diagnostics/debug-threadpool-starvation) describes the queue/completion signals used here.

### `get_perf_stats` fields

The new top-level **`stall`** object always describes the entire host, even when the existing `bot` filter restricts cadence results. All duration/age values below are milliseconds. `currentMinute` is the window accumulating now; `lastCompletedMinute` is `null` until the first minute completes, then retains the latest completed window. Reading the tool does **not** reset either window. The watchdog closes a window after at least 60000 ms; following a long stall, `durationMs` reports the actual elapsed interval and there is one summary, without synthetic empty catch-up windows. Completed pass durations are attributed to the window in which the pass ends; the live in-progress age identifies a pass spanning a boundary.

Both minute objects have these fields:

| Field | Meaning |
| --- | --- |
| `startMonotonicMs`, `durationMs` | Monotonic window start and actual elapsed duration; start is not a UTC timestamp. |
| `decisionPassMaxMs` | Longest completed decision-loop pass in this window. |
| `gcPauseMaxMs`, `gcPauseTotalMs` | Largest observed individual GC pause and cumulative-pause delta. |
| `gcGen0`, `gcGen1`, `gcGen2` | Number of collections per generation during the window. |
| `threadPoolThreadsMax`, `threadPoolPendingMax` | Peak observed worker count and queued work count. |
| `threadPoolNoProgressMaxMs` | Longest observed backlog-without-completions age. |
| `watchdogLateMaxMs` | Largest watchdog wakeup lateness. |
| `sessionsReadOver2sMax`, `sessionsReadOver5sMax` | Peak numbers of sessions with old completed reads. |

The remaining fields in `stall` are:

| Field | Meaning |
| --- | --- |
| `threadPoolThreads`, `threadPoolPending` | Latest worker count and queue length. |
| `threadPoolNoProgressMs`, `threadPoolStarvationSuspected` | Current sampled backlog age and the >1-second heuristic. |
| `watchdogLateMs`, `watchdogSampleAgeMs` | Last wakeup's lateness and elapsed time since the watchdog sampled. A loss callback does not refresh that watchdog timestamp. |
| `sessions`, `sessionsReadOver2s`, `sessionsReadOver5s` | Latest tracked session total and counts above each read-age threshold. |
| `receiveAgeMaxMs` | Largest sampled age of a session's last completed frame read. |
| `decisionPassInProgressMs` | Age of the current decision pass, or zero if none is running. |
| `incidentCount` | Lifetime count of emitted incident snapshots. |
| `logDroppedLines` | Lifetime telemetry lines dropped because the bounded log queue filled, shut down, or stderr failed. |

### Reading the logs

Telemetry writes one-line records to **stderr**, captured by the existing harness in `err.log`. A separate dedicated normal-priority thread named `allegiance-mcp-stall-log` drains a bounded 2048-line queue, so stderr I/O cannot park the high-priority watchdog. Normal shutdown closes tracked sessions, stops the watchdog, and gives the logger a bounded flush wait. A blocked stderr writer can outlive that wait; `logDroppedLines` makes queue/write losses visible while the host is running. No stack dumps or forced GC collections are performed.

- **`[stall-minute] utc=... window={...} snapshot={...}`** appears once per completed minute. `window` is the completed minute's maxima/deltas, and `snapshot` has the same fields as `stall` in the tool (captured just before that window resets).
- **`[stall] utc=... trigger=watchdog-late snapshot={...}`** means the watchdog woke **more than 1000 ms late**. `trigger=receive-starvation` means **more than 10 sessions** had no completed frame read for **more than two seconds**. When both conditions hold, the trigger is `watchdog-late`; both measurements remain in the JSON. Incidents are emitted once at episode onset, then at most once every ten seconds while the condition persists. Recovery re-arms immediate onset logging; minute resets do not re-arm it.
- **`[session-loss] utc=... name="..." reason=... detail="..." readAgeMs=... snapshot={...}`** captures the state at loss, including fresh runtime and receive counts **before removing that session**, its individual read age, and the watchdog's last state/sample age. There is exactly one loss record per tracked session. Names and exception details are JSON-escaped, so embedded newlines cannot split a record.

Loss reasons are `server-eof` for the transport's explicit server EOF; `local-exception` for a client receive-dispatch exception (with type and message); `local-transport-error` for the transport's recorded read/socket/frame failure; `connect-exception` for a failed connection attempt; and `our-disposal` immediately before manager-initiated disposal, including failed-spawn cleanup and plain-to-combat upgrades. The local transport reports coarse failure strings, so a read I/O error does not by itself establish a local code defect or reveal the server's timeout reason. Later disposal of an already-lost session preserves its original loss reason.

To correlate a drop burst, match the captured UTC times of `[session-loss]` records with nearby `[stall]` and server disconnect records. A large `watchdogLateMs`/`watchdogLateMaxMs` together with many old reads suggests the whole managed host paused or was descheduled. Large `gcPauseMaxMs`/`gcPauseTotalMs` in that window strengthens a GC explanation. A fresh watchdog with many old reads and a long `decisionPassInProgressMs` points toward receive contention or decision work; a growing ThreadPool queue with no completion progress adds evidence of pool pressure. `watchdogSampleAgeMs` remains useful when the receive thread reports a loss before the stalled watchdog resumes. These are diagnostic correlations, not proof of the root cause.

### P657b validation

Final solution build command: `dotnet build AllegianceCore.slnx --no-restore -m:1`, **0 warnings, 0 errors** (`/tmp/p657b-build.log`).

Added `P657bStallTelemetryTests.cs`, covering minute aggregation/reset and retained completed windows; late/long windows; strict incident thresholds, episodes, cooldown, and recovery; pool no-progress detection; fresh loss snapshots without resetting watchdog state; real combat/plain dispatch and accessor binding; EOF, transport errors, local decode exceptions, disposal and failed-spawn cleanup; unavailable log writers; the dedicated thread's priority/lifecycle and logger flush; and the actual `get_perf_stats` JSON fields with process-wide scope under a bot filter.

VSTest was attempted with the P657b filter and aborted before execution because the sandbox denies its test-host TCP listener (`SocketException (13): Permission denied`; `/tmp/p657b-vstest.log`). The existing `/tmp/p657-runner` in-process xUnit discoverer/executor was used instead, preserving theory expansion and assertions. No live 200v200 battle was run; the next live incident is needed to identify the stall's cause.

```sh
dotnet /tmp/p657-runner/bin/Debug/net10.0/p657-runner.dll P657bStallTelemetryTests
dotnet /tmp/p657-runner/bin/Debug/net10.0/p657-runner.dll P657bStallTelemetryTests P657SpawnReliabilityTests BotFixRegressionTests BotSpawnerResolutionTests P557ShowcaseFleetTests ClientWorldDockLoadoutTests P633DecisionLoopTests P636bCadenceSnapshotTests
```

- P657b telemetry suite: **21 passed, 0 failed, 0 skipped** (`/tmp/p657b-inprocess.log`).
- Combined P657/P657b and related regression selection: **167 passed, 0 failed, 0 skipped** (`/tmp/p657b-regressions.log`).
- `git diff --check`: passed.

P657b adds `ObservedSession.cs`, `StallMetrics.cs`, and `StallWatchdog.cs`; integrates them in `BotManager.cs`, `BotManager.Lifecycle.cs`, `PendingOwnership.cs`, and `McpTools.cs`; adds `P657bStallTelemetryTests.cs`; and appends this report section. Existing P657 spawn confirmation, failure accounting, refill, and tests are retained. No server or client-core source was changed.

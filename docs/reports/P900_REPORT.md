# P900 — desired fleet recovery after server loss

Worktree base: `9cb2c9c7`, branch `codex/p900`. Changes remain uncommitted.
Production changes are confined to `tools/allegiance-mcp`; the remaining changes
are tests and documentation. No server, wire, human `ConnectionSession`, or sim
behavior changed. No rig, mini, Mac, or other remote host was contacted.

## Reproduction and loss evidence

I read P899's reconnect investigation and re-ran its real loopback process
experiment from its retained script. A direct `--worker 1/1` started one combat
bot; killing and restarting the real server left zero active bots eight seconds
later, while the same worker PID still answered RPC. The recorded removal reason
was `connection lost`. That direct worker/manual-bot path remains unchanged.

The citations were re-grepped against this tree:

- `ObservedSession.cs:75–81` forwards transport loss to the underlying client.
- `BotManager.Lifecycle.cs:33` defines participation; `BotManager.cs:1880`
  removes a disconnected bot, clears orders, and disposes its peer.
- `StallWatchdog.cs:65–76` calls `Track(client, name)` and then tests the
  **original** `client` for `ObservedSession`. All four actual call sites pass
  an underlying probe, plain bot, or combat/commander session
  (`BotManager.cs:284,572,727,1140`). The test is false. P899 was correct.
- `WorkerSupervisor.cs:120` and `WorkerRpc.cs:43` measure process/pipe liveness.
- `BotManager.Research.cs:52–71` independently calls `EnsureCommander`
  (`BotManager.cs:1128`), explaining why commanders could return without a fleet.

The recovery trigger uses transport evidence rather than mission cookies, which
can be reused by a fresh server. It requires a lost probe for at least 500 ms
plus either a lost **desired** fleet connection, or loss of all configured probes
when there are at least two workers. A lone bot or lone probe loss cannot replay
the fleet. Network outages affecting this combination are intentionally treated
like server disappearance; this does not claim to identify a new server boot.

The manager's existing lone-bot behavior is unchanged: it removes and disposes
the connection. The supervisor retains that slot for two seconds while checking
for independently delivered probe-loss callbacks, then cancels it if probes
remain healthy. A later real server restart does not refill the canceled slot.
Normal connected mission rotation continues through the existing rotation path.

## Design as built

`WorkerSupervisor.Recovery.cs` retains each scenario's original arguments and
per-worker, per-side slots. Slot keys contain scenario ID, side position, and
ordinal. They do not carry old ship IDs, sector IDs, targets, or civ identities.
Multiple scenario requests remain separate. Initial failed slots remain requested
and visible; explicit stop/clear/despawn removes the affected desired slots.

Multiplayer invocation now supervises one child even when `--workers` is omitted.
`preset: "showcase"` opts in by default; other scenarios use `recover: true`.
`recover: false` opts out. Individual `spawn_bot` calls do not gain automatic
refill. `SkirmishDriver` explicitly passes `recovery: false`, overriding preset
defaults and explicit recovery arguments. This avoids any retry after the
game-owned server/client exits.

| State | Transition/action |
| --- | --- |
| `idle` → `starting` | Validated initial plan registers requested slots. |
| `starting` → `active` / `degraded` | Inventory determines live and failed slots. |
| `active` / `degraded` → `recovering` | Corroborated probe/fleet loss advances the epoch. |
| `recovering` | Kill and reap old workers/process trees, including probes, bots and commanders; create a fresh coordinator. |
| `recovering` → `replaying` | Fresh workers validate and seat probes against the available server. |
| `replaying` → `active` / `degraded` | Replay each remaining requested preset once, then commit its final inventory. |
| recovery → `failed` | Overall deadline expires; reap workers, mark remaining slots failed, stop retrying, log explicit failure. |
| any → `stopped` | Operator stop/clear or owner shutdown cancels work; named despawn cancels its slot and invalidates an in-flight attempt. |

Backoff bases are 1, 2, 4, 8, 16, then 30 seconds with 0.8–1.2 jitter.
The startup phase has a 35-second limit within the overall deadline. The CLI
`--recovery-deadline-seconds` defaults to **900 seconds**, includes replay, and
accepts values greater than zero and at most 86400. Exhaustion is terminal until
the operator clears/stops and requests new work. There is no new environment
variable, service, port, or Compose setting.

Fresh processes are the ownership boundary. They create new managers, load cores,
receive the new mission and sides, select legal purchases against the new civs,
and derive objectives from their new worlds. Worker zero alone runs research;
its new commander cache cannot contain an old `EnsureCommander` instance.
Joins follow the normal sequential logon/seat/purchase/launch path within each
worker, with the workers operating in parallel.

Original map overrides are retained as request history but are not replayed as
identities: a numeric `target_sector` is cleared (Claude's review keeps a sector name when the new map has it, as the rig's `Alderan`) and ordinary discovery derives new
objectives. Requested minefield counts/sides are replayed with automatic fresh
placement; stale coordinate/aleph overrides are discarded. If a clash is not yet
known, placement uses the freshly observed friendly home. Existing layer-build
errors remain diagnostics and do not abort fleet spawning.

The supervisor's recovery epoch rejects old inventory and spawn completions.
Old RPC requests cannot be transferred to a replacement process's pipe pair.
Cancellation is checked again inside the final inventory commit. Closing old
pipes before waiting for the mutation semaphore prevents a large failed initial
spawn from blocking recovery. Stop/clear also fence a preflight or initial spawn;
requests during recovery cancel desired slots before acknowledging the command.
Named despawn can resolve a new replay name through a fresh worker slot snapshot;
it does not require the old pre-restart name. Inventory reads stay available while
a worker is spawning the rest of its preset.
Existing manager coordination epochs fence delayed decisions within a process;
the supervisor discards previous coordinator decisions when recovery finishes.

`get_perf_stats.fleet` and per-worker status rows separate `requested`, `active`,
`recovering`, and `failed`. They expose `ok`, recovery state/epoch, retry attempt,
deadline, and failure reason. Slots count as recovering until replay's final
inventory check commits them. `processHealthy` and `WorkerSupervisor.Healthy`
retain process-liveness meaning. Failed requested slots make fleet health false
even when worker RPC remains alive. Stats remain readable during replay, and
unavailable worker reports have a 1.5-second bound. `[fleet]` logs record state
changes rather than one retry line per bot.

The `StallWatchdog.Connect` grace type check was **confirmed and left unchanged**.
Recovery does not use session grace. Fixing it would change lone-session behavior
without supplying fresh side selection, purchases, and launch after a restart.

## Real process proofs

All ten new tests are in `P900FleetRecoveryLiveTests`, marked Live. Their
loopback servers and supervisors are real OS processes. Tests kill the actual
server process and start a replacement at the same endpoint. A byte-for-byte
TCP forwarder selectively closes or holds real sockets for the lone-loss,
partial-spawn and late-transport cases; it does not emulate server restart,
seating, purchases, launch, or world data. These proofs launch the built .NET
server DLL; the AOT executable was published cleanly but was not used for the
local restart timing runs.

The process configurations use the winning baseline: immutable snapshots,
encode-once, parallel build, pooled transport, native ship updates, native
GotoPlan, and server GC. Interest tiers, native broadcast order and decoupled
receive remain off. Research mode uses ordinary legal purchases, not harness
purchase grants. The proof processes limit their processor count to four to
bound local resource use; measurements are local connection recovery timings,
not rig throughput claims.

| Test | Evidence checked |
| --- | --- |
| Two real restarts | Two sequential kills/restarts; eight seconds down across backoff steps; map `XXV_simple` → `m2xspiderw` → `XXV_simple`; civs `[1,3]` → `[25,3]` → `[1,18]`; initial sector override 1063 is absent after replay onto the new map; canceled slots stay canceled; exactly one commander per side and one probe per worker. |
| Deadline exhaustion | Four-second deadline; requested slots become failed, failure text is explicit, and returning the server causes no further retries. |
| Partial respawn | One real bot socket is refused; three slots become active, one stays failed; successful slots are not replayed again. |
| Stop and clear / late transport | Hold a real replay handshake, stop/clear while it is in flight, release it afterward; zero slots return. Both operations are exercised. |
| Lone bot drop | Close one actual bot socket with probes healthy; no epoch advance or fleet rebuild; a later real restart restores only the remaining three slots. |
| Fresh-name despawn during replay | Hold the second real replay join, read the first new bot from live inventory, despawn its new name, then verify only the other three slots return. |
| Lone probe drop | Close the only probe while fleet connections remain live; no replay. Subsequent real server loss then triggers recovery. |
| Multiple presets | Two separately requested presets return once each, preserving four desired slots. |
| Skirmish exit | Kill the local server, close the game-owned stdin, and verify prompt shutdown and no retry connections afterward. |
| Bounded size proof | 100 showcase bots, two workers, actual kill/restart, return to all 100 requested slots; record time and join spread. Showcase default opt-in is exercised without `recover: true`. |

Every new test passed **3/3 individually on the final code** (30 separate test-process invocations).
The 100-bot proof used the showcase default recovery opt-in:

| Run | Time from restart start to all 100 active | First-to-last bot greeting | Peak joins / 100 ms |
| --- | ---: | ---: | ---: |
| 1 | 12.78 s | 8.74 s | 2 |
| 2 | 12.43 s | 8.60 s | 2 |
| 3 | 12.11 s | 8.58 s | 2 |

[Size receipts](P900_assets/size-proof.json) and
[original P899 reproduction](P900_assets/p899-reproduction.json) are retained.
The full-fleet timer includes server process startup and recovery detection/
backoff, probe seating, and replay. It ends when fleet health commits all 100
active slots. Greeting spread measures actual TCP greetings through the proof's
forwarder, rather than a configured delay or an estimate.

## Validation

- Solution build: **0 warnings, 0 errors** (SDK 10.0.400).
- `tools/build-tools.sh`: **all 99 projects**, no warnings/errors.
- Linux x64 self-contained Native AOT publish: **clean**, including no IL2026 or
  IL3050 diagnostics. Local output: [server-aot](../../.scratch/p900/server-aot/).
- Ten new Live tests: **30/30 separate individual runs on the final code**.
- Existing supervisor, probe/worker, spawn-reliability and stall-telemetry classes:
  **three standalone class runs each**, all passed.
- The **single Fast run remains FAIL**: **12,686 passed, 10 failed, 3 optional
  skips**, 12,699 executions in 543.3 seconds; all nine infrastructure checks
  passed. Five executions exceeded the calibrated 10-second limit.
  [Fast summary](P900_assets/fast-summary.json). No second Fast sweep was run.

- Showcase never-parks: **5/5**, sequential, with parallel broadcast and native
  GotoPlan selected, matching P899's recorded 5/5 baseline. Observation windows
  were 252.5 / 232.4 / 237.8 / 196.3 / 199.9 seconds; every run reached 24/30
  objectives. The longest low-speed interval with a target was 1.317 seconds,
  below the five-second assertion. [Showcase receipts](P900_assets/showcase.json).
- Each non-task Fast failure and timing offender ran **three separate isolated
  method invocations**. All cases/results and durations are in
  [isolation receipts](P900_assets/isolations.json) and `docs/TEST_FLAKES.md`.
  The profile test fails 3/3 in the original read-only environment and passes
  3/3 with writable scratch configuration. The five path cases retain their
  documented checkout/TMPDIR obstruction. NPC defense and missile-lock remain
  0/3; auto-seat is fail/pass/fail; the warp timer has one floating-boundary
  failure among three method runs. The four passing timing offenders are below
  ten seconds in isolation.
- Final solution/tool builds and all ten recovery tests were repeated after the
  fresh-name despawn correction. The final affected regression classes and the
  existing stop/clear fixture test also passed three standalone runs each.
  The single Fast sweep preceded that final MCP cancellation correction and
  was not rerun, honoring the one-sweep instruction. Never-parks exercises the
  unchanged flight/manager behavior; the final correction is in worker RPC
  inventory/despawn control.
- Glossary links, parity CSV validation, whitespace, and added-content hygiene
  checks passed. No new media or native parity claim was introduced.

[Final validation receipts](P900_assets/validation.json) and
[configuration control receipts](P900_assets/controls.json) are retained.
Raw TRX/logs, loopback process evidence, commands, and AOT output remain under
[task-local scratch](../../.scratch/p900/), including the final build/test logs in
[final](../../.scratch/p900/final/). Public JSON receipts omit personal paths and
hostnames. Evidence added to the report is well below the 2 MB task limit.
The Fast gate is explicitly **not green**; its unchanged failures are ledgered.

## Scope and rig follow-up

Recovery is an in-memory supervisor policy, not persistence across a supervisor
crash. It does not restore manually spawned individual bots, opt-out presets, or
eliminated/canceled slots. A server application that keeps all TCP connections
open while ceasing to simulate does not meet this transport-loss trigger.
Worker-process crashes are still reported as process failures; this feature does
not silently replace them without corroborating server-loss evidence. The existing
MCP loadout catalog is still `PCore001` (`Catalog.cs:21–25` and
`BotManager.TechLoadouts.cs:47`). Recovery reloads that catalog in a fresh process;
it does not add support for switching to an arbitrary differently named static
core. The changed-map/civ proofs use the supported `PCore001` contract.

No human reconnect grace, server resume protocol, simulation rule, transport
implementation, or gameplay behavior was changed. No new native parity row is
claimed. No HUD or media changed, and no remote deployment was performed.

Claude must check on the rig:

1. Restart the real container with the full 390-bot requested showcase fleet,
   leaving the supervisors running. Verify requested/active/recovering/failed
   counts, full return, two probes, and one commander per side.
2. Repeat the restart, including a different generated map and civ tuple. Check
   fresh objectives, legal purchases, ordinary join staggering, and no duplicate
   commander, fleet, or old-epoch registrations.
3. Stop/despawn while recovery is pending, and check a longer outage plus the
   configured terminal deadline. Stop the owning launcher and confirm no retry
   process survives.
4. Keep the winning baseline settings; compare tick p95/overruns, connection
   spread, CPU/RSS/GC and coordination overhead against the existing full fleet.
   Local 100-bot timing does not certify full-rig performance or long-duration
   stability.

## Native intent

**Design:** desired-state fleet replay is modern test/showcase infrastructure,
not a native gameplay rule. **Era constraint:** native client transport loss
disconnects (`clintlib/clintlib.cpp:2120–2134`); the native server deletes a
dropped player (`FedSrv/FedSrv.CPP:4549–4564`). Those paths do not retain a desired
automated fleet. **Bug:** the modern harness had discarded its requested roster
while continuing to report healthy worker processes, so a server restart left
the fleet empty. **Modern better approach:** retain explicit desired slots, own
recovery at the supervisor, fence epochs, bound and jitter retries, make
cancellation authoritative, and report fleet health separately from process
liveness. The human/native disconnection contract remains intact.

# MCP fleet recovery

The multiplayer MCP driver supervises one or more child workers, including when
`--workers` is omitted. `spawn_scenario` with `preset: "showcase"` enables recovery
by default. Other scenario presets opt in with `recover: true`; `recover: false`
disables it. Individual `spawn_bot` calls have their existing loss behavior.
Game-owned `--skirmish` always disables recovery, even for showcase presets.

```sh
./allegiance-mcp <host> <port> --workers 2 --recovery-deadline-seconds 900
```

The deadline defaults to 900 seconds (15 minutes), including reconnect and preset
replay. The argument accepts seconds greater than zero and at most 86400.
There is no new environment variable, network listener, or server setting.

The supervisor retains each accepted scenario's arguments, seed, side slot
counts, and worker partition. A slot is identified by scenario, side position,
and ordinal, rather than an old ship, hull, side ID, or sector. Stop and clear
cancel all desired slots; despawn cancels the named slot. Cancellation applies
during recovery as well. A lone bot connection drop with healthy probes follows
the existing removal path; it is canceled from the desired fleet and will not
be recreated by a later server restart. Failed spawn slots remain visibly
failed until an operator cancels them or a subsequent server loss replays them.

Recovery requires a lost probe and either loss of every configured worker's
probe (with at least two workers) or a lost desired fleet connection, observed
for at least half a second. This is
evidence that the endpoint/session has gone away; it does not try to distinguish
a server process crash from a network outage affecting the same fleet. A lone
fleet bot or single probe drop cannot trigger recovery. A dropped fleet slot is
retained for two seconds before cancellation so independently delivered probe
loss callbacks can corroborate a real outage. A healthy server mission rotation uses
the existing client rotation behavior and does not replay the preset.

| State | Meaning |
| --- | --- |
| `idle` | No scenario has been requested. |
| `starting` | The initial requested scenario is spawning. |
| `active` | The remaining requested slots are active. |
| `recovering` | Old workers have been quiesced; the supervisor is reconnecting. |
| `replaying` | Fresh workers are spawning the remaining requested slots. |
| `degraded` | Some requested slots failed; successful slots keep running. |
| `failed` | The recovery deadline expired; background retries have ended. |
| `stopped` | Operator stop/clear or process shutdown canceled the fleet. |

Recovery closes the old workers and their process trees before reconnecting.
This disposes the old bot managers, probes, commanders, targets, cached cores,
and decisions together. New workers resolve the new mission, sides, civ techs,
legal purchases, and map objectives through the ordinary spawn path. Joins are
sequential within each worker and spread across the normal logon/seat/purchase/
launch handshakes; workers operate in parallel. There is one fresh commander
cache on worker zero, so `EnsureCommander` cannot retain old commanders. The
driver retains its existing `PCore001` catalog contract; this feature does not add
arbitrary static-core switching.

A numeric `target_sector` is dropped on replay. A sector name is kept and holds if
the new map has that sector; otherwise bots derive new targets from the new map. Requested minefield counts/sides are
replayed with fresh automatic placement; old coordinate/aleph overrides are
discarded. Before a new clash is known, placement uses the freshly observed
friendly home. Ordinary layer build/placement failures remain visible in worker
diagnostics and do not prevent fleet spawning.

Reconnect delays use an exponential base of 1, 2, 4, 8, 16, then 30 seconds,
with independent 0.8–1.2 jitter. Each attempt creates fresh workers and waits at
most 35 seconds for their validated, seated probes, within the overall deadline.
Once probes answer, the preset is replayed once with the original `refill`
policy. Partial purchases are reported rather than retrying the entire preset.
Stop/clear/despawn removes the desired slots and cancels an in-flight attempt;
only the remaining slots can enter a subsequent attempt. Inventory remains
readable during replay. Named despawn resolves a newly spawned bot through the
worker's stable slot identity, even before the final replay inventory commits.

`get_perf_stats.fleet` reports `requested`, `active`, `recovering`, `failed`,
`ok`, `state`, `epoch`, `retryAttempt`, `deadlineSeconds`, and failure text.
During recovery/replay, slots are counted as recovering until the replay's final
inventory check commits them. Per-worker status rows contain the same four slot
counts. `processHealthy` and `WorkerSupervisor.Healthy` still mean process/pipe
liveness. A running worker with failed requested slots has `fleet.ok: false`.
Stats remain available while replay runs; unavailable worker reports time out
after 1.5 seconds. A terminal deadline failure emits an explicit `[fleet]` line.
Fleet diagnostics log state transitions, rather than one retry line per bot.

The recovery epoch fences old inventories and spawn completions. Old requests
cannot migrate between worker pipe pairs; replacement kills the process owning
them. Coordination resumes with a fresh coordinator after replay, and the
existing manager coordination epoch continues to fence delayed decisions within
each worker. EOF, signals, and owner exit cancel retries and reap workers.

After a deadline failure, stop/clear acknowledges cancellation even if workers
are already gone. An explicit new scenario after stop can start fresh workers;
there is no automatic retry after the terminal failure or an operator stop.

The experimental `StallWatchdog.Connect` reconnect-grace type check remains
unchanged: its callers pass the underlying clients, so its `ObservedSession`
test is false. Fleet recovery uses fresh connections and does not depend on
session grace. Human-client reconnect behavior and the server are unchanged.

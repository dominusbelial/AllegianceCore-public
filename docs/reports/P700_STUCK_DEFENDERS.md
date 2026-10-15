# P700 — stuck defenders

Base: master `5b046d80`. Changes remain in the working tree; no commits or pushes.
Production changes are confined to `tools/allegiance-mcp`.

## Findings and reproduction

**The battle-sector state came from local ship defence, not a recall to a base.**
The old `TryDriveDefense` treated a received hit on the bot or any friendly ship
as defence even without a station. `ActiveBots()` displayed the current sector
as `defenseSector` whenever `DefendingInPlace` was true, even when the actual
recall field `DefenseSector` was null. Thus `defenseSector:1052` was not evidence
of a station alert or a friendly station in Alderan. Continuing ship hits could
keep that local state active throughout an ordinary battle.

The real `m2xspiderw.igc` dump confirms exactly two stations: side 0's Garrison
4328 in Ganymede (1050), and side 1's Garrison 4329 in Organia (1051). Alderan
(1052) contains no station. See [map evidence](P700_assets/map-summary.txt).
The supplied capture is itself truncated at 6,001 bytes; it contains twelve
complete rows followed by a partial row. Eight complete rows have both zero
speed and a target. It contains no hit history, alert history, orientation or
sent controls, so it cannot distinguish the original self-hit from a hit on a
teammate, or establish every individual bot's firing obstruction.

**A real control deadlock was reproducible.** The native close attack band
matches the target's forward speed, including zero. Our additional friendly-line
gate could suppress fire because of a friendly ship *behind the target*, since
it tested the entire weapon range. A real friendly obstruction also caused a
stationary attacker to wait without moving out of the line. Opposing stationary
fleets could therefore hold targets indefinitely without advancing or firing.
The defence duty path also passed zero engager count/rank, bypassing approach
spacing and explaining the zero-engager symptom in the capture. Defence ran
before the existing warp-exit clearance, allowing an engagement to stop arrivals
in the mouth of the aleph. A further reproduced aiming singularity produced no
turn at all for a target directly behind the ship.

**The all-clear could be starved.** For genuine base incidents, merely seeing
an enemy near a friendly station refreshed `LastThreat` every pass, even after
all damage and alerts stopped. That made the quiet window impossible to satisfy
while a passive enemy remained nearby. Local self-defence had a separate
eight-second hit window; it was not governed by the recall's quiet/minimum timers.

The corrected fixtures were replayed against master's actual controller sources:
all **nine selected cases failed**. The controller files were restored and the
solution rebuilt before final verification. These are real xUnit/VSTest runs,
including decoding the outgoing `SHIP_UPDATE` controls in socket-free tests.
See `master-replay` in the [invocation ledger](P700_assets/results.json).

## Changes

| Change | Implementation |
| --- | --- |
| Defence requires an exported friendly station and an actual station alert or observed station hit. Hits on ships remain ordinary combat and travelling fighters can still answer them. Mere proximity never starts or renews an incident. | `BotManager.Defense.cs:49`, `:161`, `:182`, `:192`; `BotManager.cs:3158`, `:4430`. |
| Preserve the original timestamp of a received station hit. Duplicate observations, including worker publications, cannot extend it. The hit extension has no object-type discriminator, so ambiguous ship/station IDs rely on the station alert; wrong-sector hits are rejected. | `BotManager.Defense.cs:79`, `:130`; `BotManager.cs:3158`. |
| Release after both 20 seconds without new station evidence and the 30-second minimum deployment. Station removal, mission/side/life changes and stale worker decisions retain their cancellation paths. Preserve the objective, route, staging and arrival history for resumption. Workers reject deployments without a friendly-station key. | `BotManager.Defense.cs:94`, `:151`; `BotManager.Coordination.cs:43`; `BotManager.Lifecycle.cs:78`. |
| Clear the arrival volume before defensive or ordinary engagement. Use the same existing exit steering, radius and margin. | `BotManager.cs:4416`. |
| Register defence duty engagers and pass their count/rank to the existing approach-spacing control. | `BotManager.cs:2641`. |
| Limit friendly obstruction checks to the segment before the target surface; exclude dead/gone ships from both the cached and fallback friendly lists. | `BotManager.cs:2968`, `:4062`, `:4074`. |
| Keep the native approach/fire bands. When nearly stationary and blocked by a friend, move laterally immediately; otherwise add lateral movement after two seconds without observed motion. This applies to combat generally, so removing a misleading defence state cannot leave the same parked attack behind. | `BotManager.cs:2972`. |
| Resolve the directly-behind target singularity with native's full-yaw branch. | `BotManager.cs:3009`. |

The two-second lateral recovery is an explicit MCP liveness policy. Native can
legitimately match a stationary target's zero speed while firing; it does not
provide the requested five-second fleet-motion guarantee. This change retains
its ballistic aim, approach bands and fire gate, and adds movement without
forcing shots through a friendly ship or overriding obstacle avoidance.

Friendly outposts remain eligible: defence uses station ownership and received
attack evidence, without requiring a home-sector flag.

Two existing base-defence fixtures were corrected to express the new contract:
the relaunch/base-attacker fixture now supplies a station alert; the self/friendly
ship-hit fixture expects ordinary combat rather than station defence. Their
target, travel and objective-resumption assertions remain. The constructor-free
stall-telemetry fixture also initializes the new metric object and manager clock
and asserts the new JSON fields. Its existing stall counters and repeated-read
assertions remain intact.

## Native reference

Paths below are relative to `~/Development/Allegiance/src/`, inspected directly
on this machine. The MCP incident timers and lateral recovery are harness policy,
not claimed native rules.

| Native rule | Native file:line |
| --- | --- |
| Station warning comes from actual station damage by a different side, with a ten-second report interval and normal/critical variants. | `FedSrv/FedSrv.CPP:12183`, `:12190`, `:12196`, `:12203`, `:12210`. |
| Defending wingmen attack threats and return to their protectee when the defensive pursuit exceeds its bounds. | `Igc/shipIGC.cpp:2373`, `:2380`, `:2388`, `:2400`. |
| Attack turns toward the solved lead; base attack throttle is 0.5–1.0. | `Igc/shipIGC.cpp:2463`, `:2470`, `:2474`. |
| The close band matches target speed, including throttle -1 for a stopped target; farther bands advance/burn. A zero-speed hold alone is therefore not proof of a native attack-law bug. | `Igc/shipIGC.cpp:2493`, `:2504`, `:2507`, `:2514`, `:2529`, `:2570`. |
| Fire requires lead angle at most 0.10 radians and intercept time at most 0.95 projectile lifespan. | `Igc/shipIGC.cpp:2476`, `:2599`. |
| Lateral combat movement uses the strafe button; the added two-second trigger is MCP policy. | `Igc/shipIGC.cpp:2589`. |
| A target directly behind the ship requires an explicit yaw direction. | `Igc/common.cpp:168`. |
| Native warp exit supplies outward velocity and offsets the ship from the arrival warp. The MCP clearance priority is its existing controller policy. | `FedSrv/fsship.cpp:126`, `:128`. |
| Native suppresses same-side/allied damage when friendly fire is disabled. The geometric friendly-line firing gate is an MCP addition, not a native hold rule. | `Igc/shipIGC.cpp:1221`. |

## Live metric

`get_perf_stats` now includes:

```json
"idleWithTarget": {
  "currentCount": 0,
  "botsLastMinute": 0,
  "sampleAgeSeconds": 0.4,
  "units": "Distinct active undocked bots idle with a target, sampled each second; botsLastMinute counts each bot once over 60 seconds. Idle means speed < 1 m/s and no movement for > 5 seconds."
}
```

The decision loop samples once per second independently of MCP queries.
`currentCount` is the last sample; `botsLastMinute` counts distinct bot names
observed in that condition during the rolling sixty seconds, so a recovered or
removed bot remains visible until its observation ages out. Polling does not
drain the count. Filters apply by bot name. Docked, disconnected, pending and
targetless bots do not contribute. Sample age exposes stale sampling rather
than presenting it as a fresh zero. The supervisor sums worker counts and takes
the oldest sample age. Implementation: `IdleTargetMetrics.cs:5`,
`BotManager.cs:4784`, `McpTools.cs:1099`, `SupervisorResults.cs:95`.

## Verification

The final solution build passed with **zero warnings and zero errors**.
All **173 affected methods passed three exact-method isolated invocations**:
**519 invocations / 642 case executions, zero failures and zero skips**. This
includes every new case, the existing base-defence cases, native attack, dodge,
friendly-fire, target selection, routing, lifecycle, finale and worker checks.
Raw commands, outcomes, counts, durations, failures and TRX paths are preserved in
[results.json](P700_assets/results.json); the exact affected-method inventory is
[focused-methods.json](P700_assets/focused-methods.json).

The final isolated local showcase repetitions passed **3/3**:

| Run | Observed seconds | Reached objectives | Longest speed < 1 with target | Shots | Hits | Kills |
| --- | ---: | ---: | ---: | ---: | ---: | ---: |
| 1 | 94.8 | 25/30 | 3.206 s | 3,982 | 2,288 | 14 |
| 2 | 94.8 | 25/30 | 4.910 s | 4,019 | 2,419 | 16 |
| 3 | 94.2 | 24/30 | 3.408 s | 4,174 | 2,064 | 13 |

Every run reported zero current and rolling-minute idle-with-target bots.
Structured summaries, measured decision rates and sample ages are in
[showcase-results.json](P700_assets/showcase-results.json).
Unfiltered suites ran sequentially after the common build, with
`ALLEGIANCE_LIVE_P660_SHOWCASE=1`:

| Assembly/pass | Passed | Failed | Skipped |
| --- | ---: | ---: | ---: |
| Core | 4,557 | 1 | 0 |
| ClientCore, first pass | 1,041 | 1 | 0 |
| ClientCore, after fixture correction | 1,042 | 0 | 0 |
| MonoGame | 2,206 | 0 | 3 |

The Core failure was
`Transport.TcpTransportSendWorkerPoolTests.Pool_worker_and_reader_counts_stay_fixed_across_join_leave_churn`:
`TimeoutException: no OnPeerJoined for raw peer`. After all three assemblies
finished it passed **3/3 exact-method isolated retries**, qualifying as a load
flake under this brief's rule. No transport source, assertion, deadline or
quarantine setting was changed.

The first ClientCore failure was deterministic and **is not a load flake**:
`P657bStallTelemetryTests.GetPerfStatsExposesMinuteAndLiveStallFields_AndReadsDoNotResetThem`
bypassed the manager constructor and omitted its new metric object and clock.
It failed 3/3 against that initial assembly. The fixture now supplies both,
adds JSON assertions, and passed **3/3** after another complete solution build
with zero warnings/errors. The final unfiltered ClientCore rerun passed
**1,042/1,042**, with zero skips. The showcase also passed inside both full
ClientCore runs. The first recorded 3.909 seconds maximum stationary target-holding,
3,701 shots, 1,879 hits and 10 kills. The final recorded **4.208 seconds**,
4,516 shots, 2,589 hits and 14 kills, with both idle-with-target counts zero.
Both full-pass outcomes are retained in the structured showcase results.

The three MonoGame skips are the pre-existing optional private Meshy-pack cases;
the pack is not configured. No requested available socket/loopback case was
replaced by a reflection runner. The initial build's read-only NuGet HTTP-cache
error was resolved by placing CLI/HTTP/scratch caches under `/tmp`, without
changing package versions or audit policy. Preliminary development outcomes,
including early failing probes, are preserved in
[diagnostics.json](P700_assets/diagnostics.json).

The initial diagnostic local 15v15 showcase also passed: 96.3 seconds observed,
962 samples, 24/30 ships observed reaching their role-specific objectives,
3,561 shots, 1,959 damaging ship hits and 14 kills. Its longest continuous
speed-below-1 interval while holding a target was **3.911 seconds**, below
**N = 5 seconds**. Both current and rolling-minute idle-with-target counts were
zero. The test uses the real MCP showcase preset, normal launches, staging,
warp travel, server physics and wire gunfire; it requires at least 45 seconds
after combat starts, substantial objective arrival, real shots/hits, no parked
target holder beyond five seconds, and no spurious Alderan defence state.

No rig/221/200v200 work was performed, as directed by the brief. The local
showcase is evidence for this controller change, not a claim of a fleet-scale
rig result.

`git diff --check` passed. HEAD remains `5b046d80`; no commits, pushes or
staged changes were made. All Codex-lane work requested in this brief is complete.

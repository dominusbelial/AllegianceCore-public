# P905 — ordered miner and Piglet bot routines

Base: `47fc17d6`, branch `codex/p905`. Changes remain in the working tree;
no commit, push, remote host access, or human-client changes.

Bots move from **81.82% (45/55)** to **92.73% (51/55)**. BOT-0009,
BOT-0012, BOT-0022 and BOT-0023 have executable tool paths and regression
tests. BOT-0016 and BOT-0018 remain **partial**: the replacement reasons below
are proposals for Domingo's ruling, with status changes reserved for Claude.
Approving both would add four points; this report does not count them.

## Delivered behavior and row verdicts

| Row | Verdict and native source | Production port | Regression evidence |
|---|---|---|---|
| BOT-0012, weight 3 | Implemented. `Wopr/Wopr/Strategies/Miner.cs:38,64-113,141-238,244-284`: wait for dispatch, obey the cluster buoy, suppress identical flying dispatches, return at **ore > 80**, select the nearest friendly station. | `tools/allegiance-mcp/BotManager.Routines.cs:92-184`, `DriveOrderedMiner` / `DispatchMiner`; `NativeBotRoutines.cs:68-84`, `OrderedMinerRoutine`; `spawn_bot` behavior `miner`. | `P905BotRoutinesTests.Ordered_miner_waits_ignores_duplicate_flying_dispatch_and_accepts_after_docking`, `Wopr_absolute_ore_threshold_is_strict`; `P905BotRoutinesLiveTests.Ordered_peer_mines_only_after_dispatch_then_returns_and_sells_real_He3`. |
| BOT-0022, weight 1 | Implemented. `Pigs/Piglets/docker.pig:36-90`: wing 8, wait 3 seconds, face a random asteroid, throttle 80%, stop after `80-Random()%60` seconds, send the communal docking request, repeat after `120-Random()%15` seconds. Face completion/drift: `Pigs/PigSrv/PigShipEvent.cpp:234-296`, `Igc/common.cpp:152-251`; default 20 Hz: `Pig.cpp:172`. | `BotManager.Routines.cs:186-223`, `DriveDocker`; `NativeBotRoutines.cs:87-146`, `DockerRoutine` / `PigFaceRoutine`; `spawn_bot` behavior `docker`. Uses real chat, queued orders and the existing station-bay approach. | `Docker_waits_faces_before_throttle_stops_then_repeats`, `Face_uses_native_angular_drift_and_quarter_radius_completion`; live `Docker_faces_and_communal_chat_queues_a_real_station_order_and_stack_resumes` observes an actual docking. |
| BOT-0023, weight 1 | Implemented targeting. `Pigs/Piglets/dm.pig:65-174`, `include/common.js:20-35,146-177`: initial enumeration target, 3-second search, 1-second target updates, retain a valid close target, replace missing/pod/far targets with the nearest eligible enemy; strict 1500 m boundary. | `NativeBotRoutines.cs:149-200`, `DeathmatchRoutine`; `BotManager.Routines.cs:224-250`, `DriveDeathmatch`; explicit behavior `deathmatch`. Fleet `CombatTargetSelector` retains its existing cap/threat policy. | `Deathmatch_retains_close_target_replaces_far_missing_or_pod_without_fleet_caps`, `Deathmatch_search_and_rearm_use_their_own_native_cadences`; live `Deathmatch_searches_real_visible_enemies_and_retargets_after_a_kill`. |
| BOT-0009, weight 1 | Implemented the real native script. `Pigs/Piglets/chatter.pig:217-244` constructs a repeated alphabet and starts/stops a chat timer. **`include/eliza.js:1` is a placeholder**, with no conversation engine to port. | Explicit `chatter` behavior in `BotManager.Routines.cs:81-87`; `ScriptedBotChat` in `NativeBotRoutines.cs:205-215`. Sends the native 62-character alphabet; one message per 30 seconds per worker even with 400 opted-in peers. The ordinary showcase creates no chatter peers. | `Four_hundred_opted_in_chatters_share_one_bounded_chat_budget`; live `Chatter_is_explicit_and_uses_real_chat_with_a_worker_budget` receives the real message over TCP. |
| BOT-0016, weight 2 | **Proposal only; status partial.** `Pigs/PigSrv/PigBehaviorStack.cpp:64-102`, `Pig.cpp:587-659`: named nested overrides, deactivate current instance, retain it, activate the new top, pop and reactivate the previous instance; refuse to pop the base. | `NativeBotRoutines.cs:9-27`, `BotManager.Routines.cs:16-36`, `McpTools.cs:46-53,296-302`; worker routing in `WorkerSupervisor.cs:159-180`. Tool `bot_behavior`. Retains typed routine state, timers and suspended orders/targets. | `Typed_stack_preserves_suspended_state_and_never_pops_the_base`; the live docker test pushes a miner role, pops and resumes the docker, and verifies the base remains; `P658SupervisorTests.Behavior_override_routes_to_the_named_bots_worker` covers worker ownership. |
| BOT-0018, weight 2 | **Proposal only; status partial.** `PigEvent.cpp:18-42`, `PigTimer.cpp:37-95,145-150`, `PigEventOwner.cpp:63-115`, `Pig.cpp:473-478`: event ownership, expiration, repetition, cancellation, rescheduling, coalesced missed intervals, and active-behavior dispatch. | `NativeBotRoutines.cs:49-65`, behavior-owned C# fields and the existing lifecycle/wire consumers, with concrete miner/docker/deathmatch/chatter consumers. | `Timer_coalesces_missed_intervals_repeats_reschedules_and_cancels`, stack timer retention, docker sequencing, target invalidation, and four real-wire routine tests. |

Test files: `tests/Allegiance.ClientCore.Tests/P905BotRoutinesTests.cs` and
`tests/Allegiance.ClientCore.Tests/P905BotRoutinesLiveTests.cs`. The live tests
use artwork, ordinary bot sessions and real TCP traffic; fixtures place test
rocks/targets but do not substitute for command handling, mining, ore sales,
turning, chat relay or docking.

BOT-0009 now has a shipped, explicitly selectable production tool path rather
than a placeholder `config-only` claim. Its default remains off. No native body,
rule, weight or row outside the six dispatched rows was changed to gain credit.

## Why both kinds of miner exist

Engine `PilotTypeMiner` drones are autonomous economic units: they choose
resources and complete repeated mine/sell cycles through the native sim AI.
`ResearchCommander.Prosperity.cs:24-60` funds or replaces those drones and
reserves money for defense/economic victory. It does not implement an ordered
player-peer strategy or listen for cluster-buoy dispatches.

The Wopr strategy is an obedient peer. It waits for its commander and has a
different return policy: an absolute threshold of 80 ore, not the engine's
capacity/depletion policy. The added MCP `miner` preserves that distinction.
It uses an ordinary legal peer hull with the port's existing geometric mining
and guarded dock-sale paths (`Sector.ResolveMining`, `Ship.IsReadyToMine`,
`ServerHost.OnDock`). It does not bypass the drone-only purchase restriction,
invent an NPC pilot for a player, replace any initial drone, change prosperity
funding or add miners to the showcase preset. Both should exist: engine drones
keep the normal economy running; explicit peers exercise commander dispatch.

The wire test observes an actual full **85.5-unit** delivery worth **9,234
credits** at the loaded side's 108 credits/unit. Its final gate receipts below
record repeated runs. Native's hard-coded threshold cannot trigger for a hold
with capacity at or below 80; this limitation is preserved rather than silently
converted to an 80% rule.

## Proposed stack and event rulings

**BOT-0016 — proposed reason:** `REPLACED: compiled named C# routines preserve
nested overrides, retained suspended state/timers and orders, restoration and
an unpoppable base; typed tool parameters replace ActiveScript command lines.`

**BOT-0018 — proposed reason:** `REPLACED: compiled event consumers and
behavior-owned typed timers preserve expiry, repetition, missed-interval
coalescing, cancellation, active-role delivery and lifecycle/chat/target
effects; script expressions and COM event objects are replaced by C# calls.`

If approved, Claude can set each row's `native_class` to `design-replaced`,
`port_status` to `implemented`, and put the corresponding `REPLACED:` reason at
the start of `port_cite_or_reason`, retaining weights and native bodies.

These proposals cover player-visible behavior, not a claim to execute arbitrary
JScript or expose COM/OLE objects. No scripting host was built.

| Native event/stack effect | C# path and evidence |
|---|---|
| Activate, deactivate, nested role change, resume prior role | `bot_behavior` → `ChangeBehavior` → `BotRoutineStack`; model and real-wire docker override tests. Only the top routine runs; an inactive timer stays owned by its suspended frame. |
| Logon, team selection, mission readiness, launch, shutdown | Existing `BotManager` seat/ready methods, `ReReadyBot`, `LaunchNextMission`, `RemoveInactiveBot`; new live tests traverse real logon/seat/purchase/launch/dispose. Existing `P660NextMatchLiveTests` covers rotation. |
| Dock/launch transitions and per-life timer restart | `DriveRoutine` observes authoritative station/pod state; `ResetNewLife` discards obsolete mission state; docker live test docks, miner live test waits docked for new dispatch. |
| Commander chat and cluster dispatch | `DriveOrderedMiner`, `HandleOrders`, `CommandBuoys`; real CHATBUOY and communal station-order tests. |
| Timed face completion followed by flight/dock actions | `DockerRoutine`, `PigFaceRoutine`, `DriveDocker`; native angular margin/drift and timer boundary tests. |
| Periodic target search, update, disappearance/death | `DeathmatchRoutine`, live `ClientWorld` ship/death state; real KILL_SHIP replacement test. |
| Damage, kills, sector/aleph changes and pod recovery in the ordinary fleet | Existing `SampleCombatStats`, `UpdatePodState` / `DrivePodHome`, `RecordCombatSectorTransition`, routing and exit-clear paths. The default showcase continues to use these shared production consumers. |
| Named timer ownership; change interval, cancel, finite/infinite repeat | Typed fields (`Chat`, docker phase timer, deathmatch search/update/rearm) and `RoutineTimer.Set/Kill/Pulse`; tests cover strict expiry, coalescing, rescheduling and cancellation. |

The native method table is `PigBehaviorScriptMethods.h:28-49`. It does not
define an incoming-missile script callback; the missile/ECM suggestions in
`dm.pig:113-142` are TODO comments. No missing implementation was invented from
those comments. General script authoring, expression evaluation, arbitrary
command-line parsing and COM introspection are the proposed era replacements,
not unimplemented game rules credited as done. The new concrete behavior gaps
were the ordered peer miner, docker sequence, deathmatch retention and timed
chatter; each now has a C# path and tests.

## Local measurement and gates

The original MCP sources were built separately from `47fc17d6` under task
scratch for comparison. All bot/performance runs use immutable snapshots and
encode-on, parallel builds, pooled transport, native ship updates, native
GotoPlan and server GC. Interest tiers, native broadcast ordering and decoupled
receive stay off. These measurements are local; **Claude owns the 200v200 rig
health/performance ruling**. No rig, mini or Mac was contacted.

The income probe, `tools/p905-local-income-proof.py`, runs the real 15v15
showcase with the existing initial miners for a fixed 600 simulation seconds.
It measures actual ore decreases on living miners, credits at the loaded
yield, combat statistics and active peers. Control uses `--reference
47fc17d6`; treatment uses current working-tree bot code. It preserves the
ordinary showcase policy and uses no synthetic CPU load. It inherits P700's
seeded player wallets/test purchase setup; these are local regression receipts,
not a fresh production-economy benchmark. It removes P700's
station-only defense assertion because miner support calls can protect a
sector without a station. It records the general idle telemetry without the
no-miners fixture's zero-hold assumption, retains the >5-second parking guard,
and explicitly measures non-pod dock holds lasting at least five wall seconds
after Started. The original never-parks test runs separately.

Two GC-pinned pairs completed (each nominally 600 simulation seconds):

| Observation | Base `47fc17d6` | Working tree |
|---|---:|---:|
| He3 units per run, side 0 / side 1 | 113.840668 / 113.880371 | 113.840668 / 113.880371 |
| Integral He3 treasury credits per run, side 0 / side 1 | 9,106 / 9,110 | 9,106 / 9,110 |
| Deliveries per run, side 0 / side 1 | 2 / 2 | 2 / 2 |
| Kills in the two runs | 3 + 5 = 8 | 3 + 7 = 10 |
| Aggregate kills per wall minute | 0.909 | 1.135 |
| Active bots at each final observation | 30 / 30 | 30 / 30 |
| REFUSED/refused lines, both runs | 0 | 0 |
| Final idle-with-target counters, first pair | 0 current / 0 last minute | 0 current / 0 last minute |
| Non-pod dock holds ≥5 wall seconds, dock-instrumented pair | 0 | 0 |
| Docked non-pod peers at end, dock-instrumented pair | 0 | 0 |

There is no observed He3 reduction. The two-run local kill rate did not fall;
these small samples do not establish a 200v200 performance guarantee. The first
pair alone had three kills on each side of the comparison; its 265.52 / 266.37
wall seconds give 0.678 / 0.676 kills/min, illustrating why sub-second elapsed
jitter is not a lethality ruling. Both valid pairs are included, without dropping
that result. Preliminary runs that did not pin server GC are retained as setup
evidence and excluded from this baseline comparison.

The probe's `credits` field is the **gross He3 value before integer conversion**
(9,107.253418 / 9,110.429688). The table uses the actual integral treasury credit
at each offload, following `ServerHost.cs:1337-1343`; all printed sale values are
far enough from integer boundaries to determine that conversion without rounding
ambiguity. The raw receipts and derivation are in
[`P905_assets/local-comparison.json`](P905_assets/local-comparison.json).

Validation:

- `dotnet build AllegianceCore.slnx`: **0 warnings, 0 errors**.
- `tools/build-tools.sh`: **101/101 projects**, zero warnings/errors.
- Server Release/linux-x64 Native AOT publish: **clean**, no IL2026/IL3050.
- All **11 P905 model cases** and **four P905 Live tests** passed **3× alone**;
  Live was additionally repeated with server GC explicitly enabled. The new
  supervisor routing test and the updated tool-count contract passed **3× alone**.
- One Fast sweep: **12,764 executions** in 540.4 seconds, **12,755 passed,
  six failed, three skipped**. The tool-count failure was corrected and isolated
  3/3 afterward. Five unrelated failures and the 11.265-second warp timing
  offender were isolated three times and recorded in `docs/TEST_FLAKES.md`.
  The NPC defend assertion still fails 3/3 on its unchanged baseline path;
  the profile test needs writable task-local configuration. The sweep remains
  recorded as **FAIL** and was not rerun.
- Glossary links, parity validation, LF CSV endings and `git diff --check`: pass.
- GC-pinned original showcase never-parks: **5/5 pass**; TRX durations
  **424.54 / 237.61 / 234.29 / 426.41 / 202.54 s**, versus the original-code
  control's 1/1 pass. The longest stationary-with-target interval was **1.925 s**
  (limit 5 s); each run reached at least 24/30 objectives and observed real hits.
  Receipts: [`P905_assets/showcase-gates.json`](P905_assets/showcase-gates.json).

The Fast sweep ran its existing fixture choices rather than globally overriding
fixtures that explicitly select older transport/sim paths. Bot measurements use
the full required baseline. The default test host does not inherit server GC
from an in-process server project; the probe asserts `GCSettings.IsServerGC`
and the bot repetition commands pin `DOTNET_gcServer=1`.

The original-code GC-pinned never-parks control passed **1/1** in 210.73 seconds
(24/30 reached objectives, longest stationary-with-target interval 0.000 s).
See [`P905_assets/gate-summary.json`](P905_assets/gate-summary.json) for the gate
summary and [`P905_assets/focused-tests.json`](P905_assets/focused-tests.json) for
focused/isolation receipts. Scorecards:
[`before`](P905_assets/scorecard-before.txt), [`after`](P905_assets/scorecard-after.txt). Raw local
logs/TRX are retained under `.scratch/p905/`; generated build copies are disposable.

## Deliberate limits

- No scripting host, arbitrary JScript execution or ELIZA engine. Native
  `eliza.js` supplies no such behavior.
- BOT-0016/BOT-0018 status remains partial for the requested ruling.
- Native chatter's zero-interval flood is capped as explicitly required by the
  brief. Chat is disabled unless the operator selects that role.
- Default fleet threat tiers/engager caps remain the showcase policy;
  native deathmatch target retention is an explicitly selected role.
- The deathmatch row certifies the dispatched targeting routine, not the
  entire unrelated DM game's custom server creation, chat taunts or adaptive
  skill tuning elsewhere in that script.
- No changes to engine drones, prosperity purchase policy, human clients,
  services, ports, deployment environment variables or media.
- Local receipts cannot certify 200v200 REFUSED, idle or kills/min behavior on
  the rig; that scheduled check belongs to Claude.

## Native-intent footer

**Design:** ordered peer mining, communal docking demos, close-target retention
and timer-driven actions are observable behavior and have concrete C# routines.
**Era constraint:** COM/OLE/ActiveScript objects, script expression strings and
behavior command lines are proposed replacements with typed C# state and tool
arguments. **Bug/limitation:** the Wopr miner's absolute 80-unit threshold can
exceed a civilization's capacity; it remains exact. Native chatter is a load
generator, and its ELIZA include is only a placeholder. A modern alternative
would use a capacity-relative unload policy with explicit commander assignments,
typed events and a bounded social message queue. Capacity-policy changes need a
separate decision and were not used to inflate this task's parity score.

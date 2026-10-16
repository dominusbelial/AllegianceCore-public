# TEST_FLAKES.md — current state of the AllegianceCore test suite

Authoritative, current-state ledger for `AllegianceCore` suite stability (rewritten
by **P647**; the full historical ledger is preserved verbatim in the History section
at the end).

## P699c observation (2026-10-16)

The final sequential unfiltered runs reported Core **4,660 pass / 0 fail / 0 skip**,
ClientCore **1,060 pass / 1 fail / 0 skip**, MonoGame
**2,234 pass / 0 fail / 3 existing optional Meshy-pack skips**,
and Lobby **19 pass / 0 fail**. Local P542 and P660 opt-ins were enabled.

`CrossSideCombatVisibilityLiveTests.Fire_is_not_relayed_to_peers_in_other_sectors`
failed its setup `Assert.NotSame` because the shooter and intended elsewhere
teammate occupied the same sector. The fixture assigns sectors by asynchronous
spawn order. After all suites, that exact method passed **3/3 isolated runs** on
the same binaries, satisfying P699c's load-flake rule. No assertion, timeout,
quarantine setting or seating/transport implementation was changed for it.

See the [suite ledger](reports/P699c_assets/accepted-suites-results.json),
[isolated retry triplet](reports/P699c_assets/isolate-accepted-suite-failures-results.json),
and [P699c report](reports/P699_POD_DOCKING.md). Repeated docking staging failures,
JSON-default regression and repository path-guard failures were deterministic
and corrected; they are not classified as flakes. The earlier complete passing
sequence before the human simulation-time correction is superseded by these
final results.

## P702 observation (2026-10-15)

The first unfiltered Core run reported **4,570 pass / 5 fail / 0 skip**.
Three network failures passed **3/3 exact-method isolated retries** on the
rebuilt working tree:

- `Transport.TcpTransportSendWorkerPoolTests.Pool_worker_and_reader_counts_stay_fixed_across_join_leave_churn`: raw-peer join timeout.
- `Transport.TcpTransportPriorityLaneThreadPerPeerTests.Ordered_join_frames_are_never_shed_when_the_stream_overflow_collapses`: expected 50 ordered frames, observed 35 under load.
- `LoadoutChangeLiveTests.Mount_and_remove_part_broadcast_loadout_change_to_owner_and_teammate_but_not_enemy`: empty notification receipt collection.

The rebuilt Core pass reported **4,574 pass / 1 fail / 0 skip**, with a single empty-receipt failure in
`MissionRotationLiveTests.Finished_mission_rotates_to_fresh_notstarted_and_new_joiner_starts`;
it passed **3/3 exact-method isolated retries**, recorded in
[rotation-retries.json](reports/P702_assets/rotation-retries.json).

The ClientCore run reported **1,059 pass / 1 fail / 0 skip**. Its
`ClientWarpApproachSlowHullLiveTests.Legacy_200_450_arrival_parks_slow_board_hull_26_outside_the_transit_volume`
sector assertion passed **3/3 isolated retries**; see
[client-retries.json](reports/P702_assets/client-retries.json).

The MonoGame pod-docking gather timeout failed **3/3** and is **not** a flake.
The fixture used a station-center placement rather than a native landing bay.
It now uses `DockTestPlacement.Inside`, retaining the original timeout and gather
assertion. All three scenarios passed in each of three isolated reruns; see
[mono-final-focused.json](reports/P702_assets/mono-final-focused.json).

The final standalone MonoGame run completed with **2,208 pass / 0 fail / 3
existing optional Meshy-pack skips**, including its 605s human-flight soak.
The earlier MonoGame invocation had no TRX summary and is treated as incomplete.

These satisfy P702's load-flake rule. Assertions, timeouts and transport code
were not relaxed. The other two first-run failures were deterministic and
fixed: the constructor fixture now chooses a currently buyable station bucket,
and join sync sends the actual stable initial-tech snapshot, including native
path permissions. They are **not** classified as flakes. The JSON-default guard
also needed a numeric-key fixture correction before the full run.

See the [P702 report](reports/P702_TECH_TREE_PLANS.md),
[isolated retries](reports/P702_assets/retries.json), and
[verification artifacts](reports/P702_assets/).

## P697 observation (2026-10-15)

The final unfiltered runs reported Core **4555 pass / 3 fail**, ClientCore
**1031 pass / 0 fail / 0 skip**, MonoGame **2199 pass / 0 fail / 3 existing
optional Meshy-pack skips**, and Lobby **19 pass / 0 fail**. P542's local live
proof and P660's local 15v15 showcase were explicitly enabled. Each of the three
Core failures then passed **3/3 exact-method isolated reruns**:

- `AutoReloadOnEmptyTests.Auto_reload_relays_RELOAD129_sector_wide`: no same-side observer in `SeatThree` setup.
- `LoadoutChangeLiveTests.Mount_and_remove_part_broadcast_loadout_change_to_owner_and_teammate_but_not_enemy`: owner receipt collection empty.
- `LiveRipcordTests.First_activation_broadcasts_to_everyone_flying_in_sector`: bystander activation receipt collection empty.

An earlier unfiltered ClientCore run had a match-2 credited-kill timeout in
`P682NextMatchCombatLiveTests.Mcp_bots_buy_launch_see_enemies_and_kill_in_both_matches`
(native model, pipeline disabled). After the final clean ClientCore suite,
all four cases of that method passed **3/3 isolated runs**. Earlier focused
single failures in these methods also had three subsequent isolated passes:

- `MineCrossSideVisibilityLiveTests.Enemy_ship_spots_a_laid_mine_once_in_range_and_learns_its_destruction`
- `CrossSideCombatVisibilityLiveTests.CrossSide_sector_stays_correct_across_warp_return`
- `CrossSideExportLiveTests.Same_side_teammate_still_gets_teamexport_no_cross_duplicate`

These satisfy P697's load-flake rule. No deadlines, receipt assertions or
quarantine settings were relaxed. Repeated deterministic failures in old
ordnance fixtures were corrected by exercising real undock/launch state,
clearing the station on missile expiry, notifying creation for a manually
placed warp missile, and choosing an actually armed hull in the opt-in P542
proof. Those corrections and the intentional golden transcript changes are
not classified as load flakes.

See [P697 report](reports/P697_GHOST_BATTLES.md),
[complete invocation ledger](reports/P697_assets/results.json), and
[development iterations](reports/P697_assets/development_results.json).
## P700 observation (2026-10-15)

The unfiltered Core pass reported **4,557 pass / 1 fail / 0 skip**.
`Transport.TcpTransportSendWorkerPoolTests.Pool_worker_and_reader_counts_stay_fixed_across_join_leave_churn`
failed with `TimeoutException: no OnPeerJoined for raw peer`. After Core,
ClientCore and MonoGame finished, it passed **3/3 exact-method isolated retries**,
meeting P700's load-flake rule. No transport implementation, assertion, timeout
or quarantine setting was changed; these retries do not claim a transport fix.

The first ClientCore pass's constructor-free `get_perf_stats` fixture failure
was deterministic, not a flake. The fixture omitted the new idle metric and
manager clock. It was corrected and passed 3/3 after a clean build; the final
unfiltered ClientCore rerun passed **1,042/1,042**. Complete outcomes are recorded in the
[P700 report](reports/P700_STUCK_DEFENDERS.md) and
[invocation ledger](reports/P700_assets/results.json).

## P701 observation (2026-10-15)

The unfiltered sequential suites reported Core **4566/4566**, ClientCore
**1017 passed / 1 failed / 1 explicit-opt-in showcase skip**, and MonoGame
**2225 passed / 0 failed / 3 unavailable private Meshy-pack skips**.

The unchanged ClientCore check
`ThreePeerSeededSessionTests.SeedThreePeers_TwoSameSide_OneCrossSide_DeterministicVisibility`
failed because asynchronous logon drain order produced sides `(0, 1, 0)`.
After all three suites finished, that exact method passed **3/3 isolated runs**.
This satisfies P701's load-flake rule and matches the existing join-order entries
below. No assertion, deadline, quarantine trait or seating/transport code changed.
See [P701 verification](reports/P701_STATION_BAY_VISUALS.md) and its result ledger.

## P695 observation (2026-10-15)

The final unfiltered workspace-output suites reported Core **4557 pass / 1 fail**,
ClientCore **1012 pass / 2 fail / 1 default opt-in skip**, and MonoGame **2199 pass /
0 fail / 3 existing optional Meshy-pack skips**. The local 15v15 P660 showcase was
subsequently enabled explicitly and passed **3/3**. All P695 clock/flight/warp/
reconnect/summary regressions and affected focused checks passed three isolated
repetitions (**111 invocations / 126 case executions**).

After the suites, both ClientCore failures passed **3/3 exact-method isolated
reruns**, satisfying P695's load-flake rule:

- `ClientWarpApproachSlowHullLiveTests.Legacy_200_450_arrival_parks_slow_board_hull_26_outside_the_transit_volume`
- `CrossSideCombatVisibilityLiveTests.CrossSide_sector_stays_correct_across_warp_return`

A `LiveTeamLobbyToggleTests.Sides_lock_from_owner_relays_and_non_owner_refused`
relay-receipt failure in a discarded temporary-output diagnostic run also passed
3/3 individually. The temporary output layout itself failed repository-root
lookup guards; normal workspace output corrected that harness error, and the
root guards passed three repetitions. Those layout failures are not load flakes.

**Unresolved intermittent check:**
`StationBucketSpawnsConstructorLiveTests.Completing_station_type_bucket_spawns_waiting_constructor_and_build_order_builds`
failed the full Core run with the previously recorded P425 construction-start
timeout (see P655b below). Its first isolated batch passed **2/3** and a fresh
second batch passed **1/3**: **3 passes / 3 failures in six isolated runs**. This
observation does not satisfy the three-clean-isolated-passes rule and is not
classified as a verified load flake. No batch failed all three runs. The existing
fixture, deadline, assertion, quarantine traits and server/bot construction code
were unchanged; P695 changes only the human client's clock and flight timing.

See [P695 report](reports/P695_CLIENT_UPDATES_OUT_OF_DATE.md) and the
[complete invocation ledger](reports/P695_assets/results.json), including every
failed rerun. No full-suite failure was silently discarded or assertion relaxed.
## P694 observation (2026-10-15)

The prepared Core suite had two existing asynchronous transport failures under
whole-suite load. Each failed method then passed **3/3 fresh method-filtered runs**
with its original assertions and timeouts:

- `TcpTransportSendWorkerPoolTests.Pool_worker_and_reader_counts_stay_fixed_across_join_leave_churn`:
  `TimeoutException: no OnPeerJoined for raw peer` (five-second wait).
- `ConsoleVerbosityLiveTests.Plain_WriteLine_on_the_sim_path_does_not_block_a_stalled_stdout`:
  healthy peers had not yet received updates when the fast tick loop ended.

The unfiltered ClientCore run also hit two existing live-wire timing failures;
both passed **3/3 fresh isolated reruns**, unchanged:

- `SessionLossLiveTests.EstablishedSession_ServerKilled_RaisesLost_EventAndControlsGoSilent`:
  observed `Lost` before its subsequent callback flag was set (already recorded
  in this ledger's historical observations).
- `ClientWarpApproachLiveTests.Client_bot_steered_by_warp_approach_law_transits_two_hops_to_target`:
  its bounded loop ended in sector 1053 rather than target 1060.

These are recorded as load flakes under the lane's isolated-rerun rule. The prior
unfiltered Core run passed **4584/4584** with the same production changes. No
transport assertion, timeout, scheduling rule, or quarantine was changed.

Exploratory P694 100 ms guards also caught fixture-induced timing outliers: the
fixture repeatedly imported servers/cores and queued megabytes of unmeasured
warmup output immediately before timing. The final benchmark silences unmeasured
output and collects unreachable setup garbage **before** the stopwatch; measured
callbacks, allocations, GC pauses and the **100 ms wall-time assertion** remain
inside/unchanged. All 14 cases passed three fresh isolated invocations: **126 timed
updates, maximum 50.324 ms**, with zero measured collections/GC pauses. The final
unfiltered Core run's 42 corresponding samples also passed (maximum 27.522 ms).
This is a correction of benchmark preparation, not a claim that live GC/scheduler
pauses cannot occur.

The deterministic missing-runtime synthetic-station fixture failure and the old
flight test's global -Z assumption were fixed and revalidated; they are **not**
classified as flakes. Commands, complete attempt history, profiles and native
citations: [P694 report](reports/P694_MASS_EVENT_TICKS.md).
## P691 observation (2026-10-15)

Final complete unfiltered sequential runs: Core **4,581 passed / 0 failed**,
ClientCore **1,006 passed / 0 failed** (local P660 next-match showcase enabled),
and MonoGame **2,186 passed / 0 failed / 3 existing optional Meshy-pack skips**.
All 62 affected methods passed three individual invocations each; the local
15v15 battle recorded **10 kills** with **30/30 bots reached**, both real MCP
relaunch cases passed 3/3, and mining/ore sale passed 3/3.

The first MonoGame attempt exited 1 after 25.294 seconds without a TRX file,
final counters, or a named assertion failure. Its detailed console log contains
2,162 pass entries and ends during the P540 eject fixture. The exact
`P540EjectLiveProofTests.Client_applies_Eject_and_picks_nearest_friendly_rescue_target_and_pod_docks`
method then passed **3/3 alone**; a subsequent unfiltered assembly run with the
standard console logger completed cleanly. The interruption's cause was not
established. These observations do **not** attribute a failed assertion or a
load-flake cause to P540. No product code, assertions, deadlines, or quarantine
traits changed for this observation. See the [P691 report](reports/P691_PARITY_WAVE9.md)
and [invocation ledger](reports/P691_RESULTS.json), including the incomplete run.

## P685 observation (2026-10-15)

The merged unfiltered sequential suites passed cleanly: Lobby **19/19**, Core
**4539/4539**, ClientCore **996/996**, and MonoGame **2185 passed / 0 failed /
3 existing optional Meshy-pack skips**. No isolated reruns were needed. The
P682 two-match combat and reset group passed **8/8 ×3**, the enabled-pipeline
15v15 next-match showcase passed, and the separate local 15v15 battle recorded
**12 kills** with **30/30 bots reached**. Both MCP relaunch cases and all nine
mining checks passed. No timing thresholds, quarantine traits, skip conditions,
or assertions were relaxed. See [P685 report](reports/P685_MERGE_WAVES.md) and
[invocation ledger](reports/P685_assets/results.json).

## P674 observation (2026-10-14)

The parity audit's sequential, unfiltered suites reported Core **4309 pass /
1 fail**, ClientCore **933 pass / 0 fail / 1 opt-in skip**, and MonoGame
**2125 pass / 0 fail / 3 optional Meshy-pack skips**. The sole failure was
`Transport.P661EgressAccountingTests.Hot_path_allocates_zero_bytes_for_all_events`:
its full-suite allocation sample observed 1712 bytes instead of zero. After all
three assemblies completed, it passed **3/3 exact-method isolated runs**, each
retaining the zero-allocation assertion. This meets P674's load-flake rule;
the allocation's source was not established. No product/test behavior or
quarantine traits changed. All eight new P674 validator cases passed three
focused repetitions and the full Core run. See
[P674_PARITY_RECONCILE](reports/P674_PARITY_RECONCILE.md) for the complete results;
raw failure/isolation TRX files are under `/tmp/p674/final/` and
`/tmp/p674/rechecks/`. The opted-in local P660 15v15 showcase subsequently
passed (1/1), resolving its default skip; the optional Meshy pack is absent.

## The two lanes (P647)

`scripts/run-tests.sh` (P471 sequential runner) now executes every test in one of two
LANES, selected by the `Lane` xunit trait:

- **MAIN** (`Lane != LiveTiming`) — the *trustworthy* suite. Live-socket tests are
  already serialized within each assembly by the xunit `LiveNetwork` collection
  (`DisableParallelization = true`); the cross-assembly contention that drove most
  historic flakes is removed by the P471 sequential-assembly runner. **The MAIN pass
  is expected to run fully clean.**
- **QUARANTINE** (`Lane == LiveTiming`) — residual live-timing wire-receipt tests that
  could not be made deterministic in P647 (each flaked at ~10–30 % even in isolation,
  so a condition-wait/deadline fix cannot guarantee them). They run in a separate final
  pass: a failure there is re-run ONCE in isolation and fails the run only if it also
  fails in isolation (otherwise it is reported as a flake and the run stays green).
  Nothing is skipped — quarantined tests still run, they are just evaluated with the
  isolation re-run so load-driven timing noise cannot poison the trustworthy MAIN pass.

CI (`ci.yml`) keeps the existing `Requires!=Artwork&Requires!=NativeSource` gate and now
also excludes `Lane!=LiveTiming` (quarantined tests are evaluated by `run-tests.sh`'s
quarantine pass, not the parallel CI run).

## Measured clean-run rate (P647)

`scripts/run-tests.sh` full runs, before vs after the P647 fixes:

| phase | MAIN clean | QUARANTINE clean | notes |
|---|---|---|---|
| Before (4 measurement runs, master 22d024b8) | **0/4** (no clean run; 3/7/7/6 Core fails + 0/2/2/0 ClientCore) | n/a (no lane existed) | ~20 unique failing tests |
| After (verification on final tree) | **3/3 clean MAIN runs** (V4, V7, V11; V11 EXIT 0) | not expected 4/4 (residual isolation-flaky live-timing tests) | 21 quarantined; each of V5–V10 surfaced 1–2 new load-flakes, quarantined on the spot |

## P684 observation (2026-10-15)

The unfiltered sequential suites reported Lobby **19/19**, Core **4445 pass /
1 fail**, ClientCore **964/964**, and MonoGame **2143 pass / 0 fail / 3 existing
optional Meshy-pack skips**. After all four assemblies completed,
`Transport.P661EgressAccountingTests.Hot_path_allocates_zero_bytes_for_all_events`
passed **3/3 fresh exact-method isolated runs**. Its full-run failure measured
6,672 allocated bytes against the zero-byte assertion. The accounting
implementation, test, warmup, assertion and traits are unchanged from integ7's
pre-merge side. This is a full-run-only allocation observation under the
three-isolated-passes rule, not a relaxed allocation budget.

All pipeline byte-identity/determinism, P677/P679 and roster integration cases
passed **43/43 ×3**; the expanded P682 off/on rotation/combat group passed
**8/8 ×3**, including both port/native modes. The separately enabled 15v15
showcase also passed. The newly added telemetry check's initial undersized
worker buffer was a concrete fixture error, fixed to use the configured worker
count; it is not classified as a flake. See the
[P684 report](reports/P684_MERGE_PIPELINE.md) and
[per-run evidence](reports/P684_assets/final/results.json).

## P682 observation (2026-10-15)

The unfiltered Core run reported **4355 pass / 1 fail**. The existing quarantined
`Transport.TcpTransportGreetTimeoutPoolTests.Silent_connection_does_not_block_a_second_client_join`
failed during cleanup with `ThreadStateException: Thread has not been started`
at `TcpTransport.Shutdown` (`TcpTransport.cs:526`). The exact method then passed
**3/3 separate isolated runs**. Transport implementation, timeout thresholds and
traits were unchanged. This meets P682's load-flake rule.

An intermediate ClientCore run reported **952 pass / 1 fail** in
`CrossSideCombatVisibilityLiveTests.CrossSide_sector_stays_correct_across_warp_return`
(`bravo transited to another sector`). That exact method passed **3/3 separate
isolated reruns** on the final tree, and the final unfiltered ClientCore run
passed **953/953**. Its test, timeouts and trait were unchanged.

ClientCore's initial finale fixture failure was reproducible **3/3** and was
**not** classified as a flake: its route assertion skipped the newly required
next-match purchase/launch. The fixture now verifies that routing waits for the
purchase and resumes after a successful ACK plus SET_CLUSTER. See the
[P682 report](reports/P682_NEXT_MATCH_VISIBILITY.md) and its verification record.

## P656c observation (2026-10-14)

The unfiltered sequential suites reported Core **4301 pass / 1 fail**,
ClientCore **939 pass / 0 fail / 1 opt-in showcase skip**, and MonoGame
**2125 pass / 0 fail / 3 optional Meshy-pack skips**. Core's
`Transport.InboundFloodSafetyLiveTests.Unsupported_fmid_flood_log_is_rate_limited_others_keep_ticking`
failed with the healthy peer still at zero received updates when the flood-log
condition completed, matching the P666 observation below. After all three
assemblies finished, the exact method passed **3/3 isolated reruns**. No
transport code, timing threshold, assertion or trait changed. See the
[P656c report](reports/P656_SHOWCASE_FINALE.md#p656c--restore-the-servers-actual-buyability-at-join)
for the six new allTech/finale regressions and focused verification record.

## P679 observation (2026-10-15)

An exploratory isolation batch hit one timing failure in
`TcpTransportCoalesceAndReasonPoolTests.Coalesce_distinct_keys_remain_independent`.
The next two initial invocations passed, then **3/3 fresh method-isolated audits**
passed. The final P679 validation also passed this method **3/3**. Per the lane
rule this is a load/timing flake; no assertion, deadline, skip or quarantine was
changed. See [the audit evidence](reports/P679_assets/exploratory-flake.json)
and [the final report](reports/P679_PIPELINE_COST.md).

## P668 observation (2026-10-14)

`MineCrossSideVisibilityLiveTests.Enemy_ship_spots_a_laid_mine_once_in_range_and_learns_its_destruction`
missed the enemy mine EXPORT on the third initial isolated run (the first two
passed). It then passed **3/3 fresh individual runs**, and passed the complete
Core suite. Under P668's lane rule this is recorded as a load/timing flake;
no assertion, product visibility rule, skip, or quarantine was changed.
See [P668's report](reports/P668_BROADCAST_SNAPSHOT.md) and
[per-run evidence](reports/P668_RESULTS.json).

## P659 observation (2026-10-14)

The initial Core suite pass during the cadence work hit three existing wire-timing
failures. Each passed **3/3 in separate method-filtered runs** on the corrected
P659 tree:

| Test | Isolated result |
| --- | --- |
| `LiveRipcordTests.First_activation_broadcasts_to_everyone_flying_in_sector` | 3/3 pass |
| `LiveRipcordTests.Re_target_activates_to_requester_only` | 3/3 pass |
| `P590PodRescueTests.Pod_pickup_order_streams_queued_order_to_real_player_rescuer` | 3/3 pass |

The final Core pass had one failure:
`ConquestTerritoryLiveTests.Conquest_win_in_one_mission_does_not_leak_to_another`
(a delayed `GAME_STATE` violated its empty-receipt assertion). It also passed
**3/3 alone** on the final tree. All P659 tests passed in that suite run.

P590 also failed 1/3 in an earlier loaded isolation batch, then passed all three
consecutive final-tree checks. These remain timing observations, with no new
quarantine or test skips. The initial P659 rejected-boot profiler callback leak
was a real integration defect, fixed by validating cadence before registering
other subsystem callbacks; it is **not** classified as a flake. See
[the P659 report](reports/P659_UPDATE_CADENCE.md) for final-suite outcomes.

## P648 observation (2026-10-14)

Two `scripts/run-tests.sh` runs on the P648 final tree (dead-latch / relaunch-visibility
work). Run A (V1) had 11 failures, Run B (V2) was **fully clean (EXIT 0)**. Every
Run-A failure was re-run 3× in its class in isolation and confirmed flake (all pass
in isolation except the one noted):

| test (Run A, final tree) | isolation | verdict |
|---|---|---|
| `BotFixRegressionTests.Defense{Alert,AfterArrival,Detour,DisableSwitch,Relaunch,TravellingFighter x2}` (7) | 3/3 pass | load/parallelism flake (fast unit tests, fail only in the assembly-parallel MAIN burst; not touched by P648) |
| `ThreePeerSeededSessionTests.SeedThreePeers_TwoSameSide_OneCrossSide_DeterministicVisibility` | 3/3 pass | load flake (logon-drain ordering vs connection order) |
| `ClientReadyUpUnfreezesNormalMapLiveTests.Harness_ready_up_starts_normal_map_mission_and_opposing_combat_bot_moves` | 3/3 pass | load flake (live) |
| `BotAcceptsAndEngagesOrderLiveTests.ScenarioBot_AutoAcceptsAndEngagesTheSpecificCommandedTarget` | 3/3 pass | load flake (live) |
| `LiveCountdownStartPingTests.StartGame_owner_all_ready_goes_through_real_countdown` | **2/3** | isolation-flaky live-timing sibling of the already-quarantined `Multi_mission_countdown_scopes_per_mission` — **recommend moving to the QUARANTINE lane (Lane=LiveTiming)** in a follow-up |

Run A (V1) also surfaced the **P648 regression** (`DeadLatchRespawnLiveTests` x2,
`SuicideLiveTests` x1): the initial LOADOUT_CHANGE revive un-latched the death's
lifepod hull-change. Fixed by gating the revive on a confirmed NON-lifepod hull;
after the fix all three pass in isolation AND in the clean Run B.


## P653c observation (2026-10-14)

The economy/loadout follow-up resolved all **54 observed baseline failures**.
Final unfiltered suites: Core **4178 pass / 1 fail**, ClientCore **869 pass / 0
fail**, MonoGame **1969 pass / 0 fail / 3 existing optional private-pack skips**.
See [P653C_FINISH.md](reports/P653C_FINISH.md) and the durable
[per-case evidence](reports/P653C_RESULTS.json).

The sole final-suite failure was
`ShipUpdateBudgetLiveTests.Heavy_updates_are_bounded_to_the_budget_not_proportional_to_ship_count`
(the sampled window included an older empty HEAVY update). After all complete
runs finished, it passed **3/3 individual runs**. No budget assertion or product
code was changed and no quarantine trait was added.

Additional earlier-snapshot receipt failures in `AsteroidMinedLiveTests.No_broadcast_fires_once_miner_hits_capacity`,
`LiveCountdownStartPingTests.Multi_mission_countdown_scopes_per_mission`, and
`LiveRipcordTests.First_activation_broadcasts_to_everyone_flying_in_sector`
also passed **3/3 individually**. The existing quarantined
`GameOverPlayersWireTests.Conquest_win_in_one_mission_does_not_leak_game_over_players_to_another`
and `LiveWinConditionTests.Board_refused_once_mission_over` passed the final
full suite but remained **2/3 in isolation**, consistent with their existing
entries below. Their quarantine status is unchanged.

P653c also corrected arrival-order fixtures (pickup, pod rescue, station-alert
peer roles, and ship-delete watcher/departer seating), waited for an actual
encoded ore decline, and made the legacy slow-hull negative control compare
against the decoded transit sphere rather than an arbitrary 150-unit margin.
Those checks retain their behavioral assertions; production steering is unchanged.

## P665 integration observation (2026-10-14)

The first unfiltered integration pass reported these existing cases:

| Test | Individual reruns after all suites | Disposition |
| --- | --- | --- |
| `StationsUpdateLiveTests.Damaged_station_fraction_reflected_in_next_update` | 3/3 pass | Load/receipt flake: full run read shield byte 0 instead of 144. No assertion or quarantine change. |
| `ChatTargetComposeLiveTests.ComposeDefault_Team_SendsToTeammate_NotEnemy` (MonoGame) | 3/3 pass | Existing seating-order flake: full run's named sender had no same-side teammate. No test or product change. |
| `LiveChatSectorWireTests.Individual_AllSector_and_FriendlySector_relay_to_real_peers_over_the_wire` | fail / fail / pass before correction; 3/3 pass after | A concrete fixture role bug was found inside this existing quarantine: the first Farout peer could be Beta, so selecting its opponent as the lone Beta inverted the roles. P665 binds roles to the layout's declared mission side IDs, retains all routing assertions, and leaves the existing LiveTiming trait in place. |

The affected-test isolation matrix also saw the already-documented
`LiveWinConditionTests.Board_refused_once_mission_over` pass / pass / fail,
then pass 3/3 on final reruns. The P641 launcher-reload capacity mismatch was a
fixture defect, corrected by providing legal capacity for its ten-round setup;
it is not classified as a flake. See [P665_MERGE](reports/P665_MERGE.md) for final
suite numbers and [P665_RESULTS](reports/P665_RESULTS.json) for per-case outcomes.

## P651 observation (2026-10-14)

The required unfiltered sequential assembly passes on the P651 equipment tree
reported Core **4189 pass / 1 fail**, ClientCore **873 pass / 2 fail**, and
MonoGame **1974 pass / 0 fail / 3 optional Meshy-pack skips**. Each failure was
rerun by exact method filter **3× alone**, after the assembly passes; all passed
3/3, so they are load flakes under the task lane rule:

| test | full-pass symptom | isolation |
|---|---|---|
| `LiveRipcordTests.First_activation_broadcasts_to_everyone_flying_in_sector` | bystander activation receipt collection empty | 3/3 pass |
| `ThreePeerSeededSessionTests.SeedThreePeers_TwoSameSide_OneCrossSide_DeterministicVisibility` | async logon drain order differs from connection order | 3/3 pass |
| `ShipDeleteGoneLiveTests.DockOut_WatcherLatchesGone_UndockReturnsAndClears` | docker's own `StationId` receipt failed to latch | 3/3 pass |

The last symptom occurs during the test's docking setup, before its watcher
latch assertion; it is distinct from the historical P472/P482 product fix.
P651's new equipment regressions and affected classes passed 3× focused runs.
See [P651 report](reports/P651_EQUIPMENT_RULES.md) for native citations and the
complete verification record. No new quarantine tags or timing-only product
changes were introduced.

## P629 observation (2026-10-14)

The required unfiltered sequential assembly passes on the P629 sector-penalty
tree reported Core **4208 pass / 2 fail**, ClientCore **886 pass / 0 fail**, and
MonoGame **1974 pass / 0 fail / 3 optional Meshy-pack skips**. After all three
assemblies finished, each failure passed **3/3 exact-method isolated reruns**,
qualifying as a load flake under the task lane rule:

| test | full-pass symptom | isolation |
|---|---|---|
| `Transport.InboundFloodSafetyLiveTests.Oversized_packet_is_rejected_not_dispatched_others_keep_ticking` | healthy bot receipt count still zero in the short packet-rejection window | 3/3 pass |
| `LiveWinConditionTests.Board_refused_once_mission_over` | Started-stage control boarding did not complete; already `Lane=LiveTiming` | 3/3 pass |

All 31 new P629 test cases and 15 affected existing classes passed three focused
repetitions. No tags or timing-only product changes were made. See the
[P629 report](reports/P629_NATIVE_SECTOR_PENALTIES.md) for native references,
local TCP/bot proof, real-window HUD/audio evidence and durable verification ledgers.

## P666 simulation merge observation (2026-10-14)

Final unfiltered sequential suites: Core **4271 pass / 3 fail**, ClientCore
**918 pass / 1 fail**, MonoGame **2125 pass / 0 fail / 3 existing optional-pack
skips**. The opt-in 15v15 next-match showcase ran and passed; ClientCore skipped
nothing. After all three final assemblies finished, every full-run failure
passed **3/3 exact-method isolated runs**:

| Test | Full-run symptom | Isolation |
| --- | --- | --- |
| `LoadoutChangeLiveTests.Mount_and_remove_part_broadcast_loadout_change_to_owner_and_teammate_but_not_enemy` | Owner's mount receipt not yet collected after observer receipt | 3/3 pass |
| `Transport.InboundFloodSafetyLiveTests.Unsupported_fmid_flood_log_is_rate_limited_others_keep_ticking` | Healthy bot's first heavy update not yet collected when flood suppression completed | 3/3 pass |
| `Transport.TcpTransportSendWorkerPoolTests.Pool_worker_and_reader_counts_stay_fixed_across_join_leave_churn` | Raw peer join callback timed out | 3/3 pass |
| `ClientCore: CrossSideCombatVisibilityLiveTests.CrossSide_sector_stays_correct_across_warp_return` | Initial warp-out setup timed out; existing LiveTiming case | 3/3 pass |

No tags, deadlines or assertions changed for these four load flakes. Separately,
P666 corrected concrete fixture roles in the existing quarantined boarding and
multi-mission conquest tests: actual assigned ships/missions now determine the
peers, and the game-over-player case waits for its asserted wire receipt. Those
three corrected methods passed 3/3 individually and passed the final full suite;
their existing traits remain. Native readiness/prediction and legal-capacity
fixture corrections are described in the [P666 report](reports/P666_MERGE_SIM.md),
with [durable verification evidence](reports/P666_RESULTS.json).

## P670 send-stall observation (2026-10-14)

Unfiltered sequential suites on the P670 final tree: Core **4309 pass / 0 fail**,
ClientCore **933 pass / 1 fail**, MonoGame **2125 pass / 0 fail / 3 existing
optional Meshy-pack skips**. All 86 new/affected transport method filters passed
three isolated invocations each (264 cases); the optional local 15v15 showcase
also ran and passed.

| Test | Full-run symptom | Exact-method isolation after all suites |
| --- | --- | --- |
| `ClientCore: ThreePeerSeededSessionTests.SeedThreePeers_TwoSameSide_OneCrossSide_DeterministicVisibility` | Async logon drain order differed from connection order; sides `(0, 1, 0)` left peer2 on the same side. | 3/3 pass; existing seating-order load flake under the P670 lane rule. |

No assertion, timeout, or quarantine trait changed. See the
[P670 report](reports/P670_SEND_STALL.md) and
[verification ledger](reports/P670_RESULTS.json) for exact outcomes, baseline
loopback evidence and native references.
## P672 scoreboard traffic observation (2026-10-14)

The unfiltered sequential assembly runs on P672 reported Core **4314 pass / 1
fail**, ClientCore **937 pass / 0 fail / 0 skip**, and MonoGame **2125 pass / 0
fail / 3 existing optional Meshy-pack skips**. The opt-in 15v15 MCP showcase and
all new scoreboard tests ran and passed. Forty affected/new methods passed
three separate method-filtered runs each (120 runs, 147 executed cases).

The sole full-run failure was the already-documented
`LiveCountdownStartPingTests.StartGame_owner_all_ready_goes_through_real_countdown`:
line 212 asserted the second peer's SET_START_TIME immediately after a wait
that observes only the first peer. After all suites completed, exact-method
isolation returned **fail / pass / pass**, followed by a fresh **pass / pass /
pass** batch. This repeats the existing isolation-flaky receipt observation;
there was no assertion, timeout, production countdown or quarantine change.
See [P672_SCOREBOARD_TRAFFIC](reports/P672_SCOREBOARD_TRAFFIC.md) and the
[durable results](reports/P672_RESULTS.json) for both isolation batches.
## P673 receive-decouple observation (2026-10-14)

All three unfiltered assemblies ran sequentially with
`ALLEGIANCE_RECEIVE_DECOUPLE=1`; the opt-in local 15v15 showcase was also enabled
and passed. Core **4312 pass / 0 fail**, ClientCore **932 pass / 2 fail**,
MonoGame **2125 pass / 0 fail / 3 existing optional-pack skips**. After all
assemblies finished, both ClientCore failures passed **3/3 exact-method isolated
reruns**, qualifying as load flakes under the task lane rule:

| Test | Full-run symptom | Isolation |
| --- | --- | --- |
| `SessionLossLiveTests.EstablishedSession_ServerKilled_RaisesLost_EventAndControlsGoSilent` | The test observed `Lost` before its subsequent `SessionLost` callback flag was set. This method is already in the historical ledger. | 3/3 pass |
| `RetriedConnectLiveTests.RetriedConnect_StartsFromEmptyWorld_AndReseatsWithNewAssignment` | `Assert.Contains` enumerated the client world's dictionary keys while the receive thread modified the dictionary. The reconnect/reseat method is already quarantined; its actual class is `RetriedConnectLiveTests` in `RetryCleanupTests.cs`. | 3/3 pass |

No product changes, assertions, deadlines or quarantine tags were added for
these two cases. P673's new and affected focused cases passed **183/183 case
runs**, each method run three times alone, including real sockets in both
transport modes and hostile-input regressions. See the
[P673 report](reports/P673_RECEIVE_DECOUPLE.md) and
[verification ledger](reports/P673_RESULTS.json).

## P655 observation (2026-10-14)

During movement/state integrity verification, these existing receipt tests
failed in an earlier integration/isolation attempt and then passed **3/3
individually**:

| Test | Observation |
| --- | --- |
| `LiveRipcordTests.Re_target_activates_to_requester_only` | First isolated run missed the requester receipt; two subsequent passes, then a fresh 3/3. |
| `LiveCountdownStartPingTests.StartGame_owner_all_ready_goes_through_real_countdown` | Earlier full-run countdown receipt failure; fresh 3/3. |
| `GameOverPlayersWireTests.Win_by_elimination_broadcasts_game_over_players_to_surviving_peer` | Earlier full-run roster receipt failure; fresh 3/3. |
| `ConquestTerritoryLiveTests.Conquest_win_in_one_mission_does_not_leak_to_another` | Earlier integration receipt failure; fresh 3/3. |

The final sequential Core/ClientCore/MonoGame/lobby pass reported five failures.
After **all four suites finished**, each passed a fresh **3/3 individual runs**:

| Test | Full-run failure |
| --- | --- |
| `Transport.TcpLoopbackProofTests.ClientHalf_RaisesOnSessionLost_AndClearsConnected_WhenServerDies` | Shutdown called Thread.Join before the thread started (ThreadStateException). |
| `LoadoutChangeLiveTests.Mount_and_remove_part_broadcast_loadout_change_to_owner_and_teammate_but_not_enemy` | Owner's loadout-change receipt was still empty after the teammate's receipt arrived. |
| `LiveWinConditionTests.Board_refused_once_mission_over` | Existing LiveTiming control-leg board handshake timed out. |
| `ThreePeerSeededSessionTests.SeedThreePeers_TwoSameSide_OneCrossSide_DeterministicVisibility` | Logon drain order differed from connection order, yielding sides 0/1/0. |
| `BotAcceptsAndEngagesOrderLiveTests.ScenarioBot_AutoAcceptsAndEngagesTheSpecificCommandedTarget` | Bot's accepted-target slot was not observed before the deadline, despite the visible commanded target. |

Classification follows P655's explicit full-failure/3-clean-isolations lane rule;
it does not establish a product-level root cause for the shutdown race.

**P655b correction:** the bot-order entry above is a confirmed P655 regression,
not a load flake. Claude reproduced it 3/3 on P655 and observed 2/2 passes on
the base. P655 applied current visibility to ORDER_CHANGE acceptance; the MCP
bot sends that acceptance once, so detection arriving later could not recover
the accepted slot. A deterministic packet regression reproduces the failure
before the fix, independent of load. P655b restores native command-slot writes
without that visibility gate and retains ownership and missile-visibility
checks. See the report's [P655b section](reports/P655_GAME_STATE_INTEGRITY.md#p655b).
The earlier three successful isolations did not establish that this path was
correct, and the prior classification is withdrawn.

No assertion was relaxed and no quarantine trait was added for these observations.
Deterministic failures caused by fixtures flying while docked, joining a baseless
third side after Started, or assuming unauthenticated harness grants were corrected
and verified 3/3; those are fixture changes, not load flakes. Flood fixtures now
start and launch their healthy peers, and wait for the asserted wire receipt.
See [P655_GAME_STATE_INTEGRITY.md](reports/P655_GAME_STATE_INTEGRITY.md) and
[P655_RESULTS.json](reports/P655_RESULTS.json) for verification evidence.

## P655b observation (2026-10-14)

The commanded-bot regression is corrected as described above; its previous
load-flake classification is withdrawn. It passed 3/3 alone and in all three
full ClientCore runs after the fix.

The unfiltered Core run reported two other failures. After all complete suites
finished, both passed **3/3 individually**, meeting the brief's load-flake rule:

| Test | Full-run observation |
| --- | --- |
| `AutoReloadOnEmptyTests.Auto_reload_relays_RELOAD129_sector_wide` | `SeatThree` could not find a same-side observer for the first connection's assigned ship; setup failed before reload. |
| `StationBucketSpawnsConstructorLiveTests.Completing_station_type_bucket_spawns_waiting_constructor_and_build_order_builds` | Timed out waiting for the Build order to start construction. |

No assertion, deadline, fixture or quarantine trait was changed for these two
observations. No additional test failed 3/3 alone. See the
[P655b report](reports/P655_GAME_STATE_INTEGRITY.md#p655b) and
[per-run evidence](reports/P655B_RESULTS.json).
## P643 observation (2026-10-14)

After a clean desktop build, Core, ClientCore and MonoGame were run sequentially,
including every live/loopback test (no LiveTiming filter). Core: 4167 passed / 2
failed; ClientCore: 868 passed / 1 failed; MonoGame: 1986 passed / 3 optional
private-Meshy-pack skips / 0 failed. Every failure then passed **3/3 in isolation**:

| test | isolation | verdict |
|---|---|---|
| `LiveRipcordTests.Cancel_while_active_aborts_sector_wide` | 3/3 pass | existing ripcord-abort receipt timing flake |
| `GameOverPlayersWireTests.Win_by_elimination_broadcasts_game_over_players_to_surviving_peer` | 3/3 pass | existing surviving-peer game-over receipt flake |
| `ShipDeleteGoneLiveTests.CrossSide_WarperLeavesSector_WatcherLatchesGone` | 3/3 pass | cross-side sector-transition receipt load flake (`bravo ship transited` assertion) |

P643 changes the client renderer/settings/telemetry, not these Core/ClientCore
wire paths. No timing test or deadline was changed to hide these failures. The
report and local measurement evidence are in
[`P643_GPU_MEASURE_FSR1.md`](reports/P643_GPU_MEASURE_FSR1.md).

## Quarantined tests (Lane = LiveTiming), with root cause

**P655c update:** the game-over roster, conquest scope and side-only elimination
rows below have concrete peer-role/receipt fixture fixes. Their earlier
"inherently timing-sensitive" diagnosis is superseded by the
[P655c measurements and controls](reports/P655_GAME_STATE_INTEGRITY.md#p655c).
The existing traits remain for this change.

Tagged in P647. Each flaked in **isolation** (10× class runs), so no deadline/condition
wait makes it deterministic — the wire receipt it waits on is inherently
timing-sensitive at the margins.

| test (suffix) | flaked-in-isolation rate | root cause class |
|---|---|---|
| `AddPartTests.Part_pickup_accept_into_empty_mount_mounts_fresh_part` | 2/10 | live-wire part-mount receipt, marginal timing |
| `AddPartTests.Part_pickup_decline_recreates_treasure_at_current_position` | load-flake | pickup-acquire touch/physics receipt |
| `LiveTreasurePickupTests.Rescue_ship_picking_cash_treasure_credits_side_broadcasts_moneychange_and_destroys_it` | 1/3 under load | pickup-acquire cash receipt (same family); passes in clean isolation |
| `ShipDeleteGoneLiveTests.SameSide_WarperLeavesSector_WatcherLatchesGone_OwnPeerNotHidden` | 2/3 under load | ship-delete watch-latch live receipt (same timing-sensitive class as the documented `DockOut`) |
| `MineCrossSideVisibilityLiveTests.Enemy_ship_spots_a_laid_mine_once_in_range_and_learns_its_destruction` | load-flake | mine cross-side visibility live receipt (passes 3/3 isolated) |
| `ConquestTerritoryLiveTests.Conquest_win_in_one_mission_does_not_leak_to_another` | 2/10 | live-wire GAME_STATE/GAME_OVER receipt after stage flip |
| `GameOverPlayersWireTests.Conquest_win_lists_every_connected_player_including_losing_side` | 2/10 | live-wire game-over roster receipt |
| `GameOverPlayersWireTests.Conquest_win_in_one_mission_does_not_leak_game_over_players_to_another` | 2/10 | live-wire game-over scope receipt |
| `LiveRelaunchTests.Carrier_without_launch_slots_uses_backward_fallback` | load-flake | positional relaunch-fallback drift during wire round-trip |
| `LiveRipcordTests.Ship_anchor_insufficient_energy_aborts_consumed_not_retried` | 1/10 | live-wire ripcord-energy receipt |
| `LiveRipcordTests.Cancel_while_active_aborts_sector_wide` | load-flake | live-wire ripcord abort receipt |
| `LiveRipcordTests.Re_target_activates_to_requester_only` | 2/3 under load | live-wire ripcord re-target receipt (P667 run 2; same ripcord-wire family) |
| `StationUnderAttackAlertLiveTests.Sub_30_percent_fraction_produces_critical_alert_once_throttle_reopens` | 2/3 under load (cold-start) | live-wire station under-attack alert throttle receipt (P667 run 2; same family as the `Defending_side…` entry) |
| `LiveWinConditionTests.Board_refused_once_mission_over` | 1/10 | live-wire board-handshake control leg |
| `SideEliminationLiveTests.Eliminating_side2_sends_side_only_gameover_to_it_alone` | 2/10 | live-wire side-only game-over receipt |
| `LiveChatSectorWireTests.Individual_AllSector_and_FriendlySector_relay_to_real_peers_over_the_wire` | 2/10 | live-wire chat sector relay (flaky even isolated) |
| `Transport.TcpTransportPriorityLane{Pool,ThreadPerPeer}Tests.Ordered_world_data_is_next_drained_even_after_a_stream_backlog` | 0/10 (load-flake) | live-socket priority-lane ordering |
| `Transport.TcpTransportGreetTimeoutPoolTests.Silent_connection_does_not_block_a_second_client_join` | 0/10 (load-flake) | live-socket greet-timeout join |
| `Transport.InboundFloodSafetyLiveTests.Oversized_packet_is_rejected_not_dispatched_others_keep_ticking` | 0/10 (load-flake) | live-socket flood-safety receipt |
| `LiveCountdownStartPingTests.Multi_mission_countdown_scopes_per_mission` | load-flake | live-wire countdown scope receipt |
| `LiveTeamLobbyTests.Unready_broadcasts_team_ready_false` | load-flake | live-wire team-ready receipt |
| `CrossSideCombatVisibilityLiveTests.CrossSide_sector_stays_correct_across_warp_return` | 1/10 | live-wire warp-transit/visibility state (ClientCore) |
| `ClientWarpApproachSlowHullLiveTests.Legacy_200_450_arrival_parks_slow_board_hull_26_outside_the_transit_volume` | load-flake | live-wire warp-approach arrival (ClientCore) |
| `RetryCleanupTests.RetriedConnect_StartsFromEmptyWorld_AndReseatsWithNewAssignment` | load-flake | live-wire reconnect/reseat (ClientCore) |

## Fixed in P647 (were failing, now pass 10× isolated AND in MAIN)

All observed failures across the 4 measurement runs were root-caused as **test-side** —
no product code changed. Classes of fix:

1. **Pump-stops-at-first-peer, then asserts a second peer's in-flight receipt** →
   converted to a condition-wait for the FULL asserted state with a generous deadline:
   `WarpBombTests` (all 3 peers), `LiveInSpaceEjectTests` (victim+observer),
   `LiveRipcordTests.Mid_countdown` (ripper+bystander), `ShipsUpdateLiveTests.Locked_target`
   (grab a target-carrying update, not stale `[0]`), `P591dMinefieldExportAudienceTests`
   (wait for the export after the layer dies), `ConquestTerritoryLiveTests`,
   `LoadoutChangeLiveTests`, `AsteroidMinedLiveTests`.
2. **Too-tight fixed pump budgets under load** → widened to generous condition-waits:
   `LiveWinConditionTests`, `MineCrossSideVisibilityLiveTests`, `AddPartTests`,
   `StationUnderAttackAlertLiveTests`, `AutoReloadOnEmptyTests`, `SideEliminationLiveTests`,
   `AsteroidExportTests`, `CrossSideCombatVisibilityLiveTests`, `ShipIdAllocationLiveTests.
   Valid_logon_advances_nextShipId`, `ShipsUpdateLiveTests.Cross_side_ships_see_each_other_and_not_self`
   (12 ms fail = the pump did not reach the cross-side HEAVY_SHIPS_UPDATE before asserting; passes 3/3 isolated).
3. **Structural seating / leader / premise assumptions** → made deterministic:
   `CargoBayReloadTests.SeatThree` (select subject+observer from the majority side —
   robust to async seat order), `ReadyCheckLiveTests.Only_mission_owner` (seat the leader
   before the challenger so it is deterministically the leader),
   `LiveStationRescueTests` (exit the setup pump on pod-spawn so the lifepod cannot dock
   during the seat-wait), `GameOverPlayersWireTests.Win_by_elimination` (advance sim so
   `PlayerScore.TimePlayed > 0` — `CalculateScore` early-returns on `TimePlayed==0`,
   native `common.cpp:2669`, leaving Score 0).

**Native behaviour classified** (P647): the `CalculateScore` `TimePlayed==0` early return
is **intended design** (native `common.cpp:2669`), ported faithfully; the test now ensures
non-zero play time instead of fighting it.

**Real product races found: none.** Every observed failure was test-side. The task's
prioritised `ShipIdAllocationLiveTests.Valid_logon_advances_nextShipId` was root-caused:
a valid logon with missions present ALWAYS calls `AllocateShipId()` before its ack, and
`Boot()` assembles missions synchronously (`Server.Lifecycle.cs:167-168`) — no product
race; the flake was the too-tight 1000-iteration pump deadline under load.

## Known shared-static console class (NOT observed in P647's 4 measurement runs)

`ConsoleLogNonBlockingTests` / `ConsoleVerbosityLiveTests` mutate the process-global
console (`ConsoleLog` static + `Console.Out`). The documented residual risk is a parallel
unit test writing to console during an `Install()` window. These did not fail in any of
P647's 4 measurement runs and are not quarantined; if they reappear, the fix is to make
their exact-content assertions marker-filtered (count only the lines the test itself
produces) rather than quarantining.

## P664 verification observation (2026-10-14)

P664 ran the full Core/ClientCore/MonoGame/Lobby suites sequentially, unfiltered,
including the existing LiveTiming lane. Core was **4197 passed / 2 failed**;
ClientCore, MonoGame and Lobby had no failures. Both Core failures are existing
LiveTiming tests already listed above; this is an additional observation, not a
new quarantine assignment:

| Test | Full-run failure | Exact isolated retries |
|---|---|---|
| `Transport.TcpTransportGreetTimeoutPoolTests.Silent_connection_does_not_block_a_second_client_join` | `ThreadStateException: Thread has not been started` from `TcpTransport.Shutdown`, line 447 | **pass/pass/pass**; load flake under the P664 brief's 3/3 rule |
| `ConquestTerritoryLiveTests.Conquest_win_in_one_mission_does_not_leak_to_another` | Test line 161 expected mission B's `GameStates` to be empty; saw conquest `[80,20,...]` | **fail/pass/pass**, then **pass/pass/fail** (4/6 passed); existing intermittent quarantine behavior remains, including isolated failures |

No clean full Core result or 3/3 isolation stability is claimed for the second
case. Neither test failed three isolated attempts in a row. P664 modifies only
client station sound processing/content; Core.Tests and its Core/Server/Bot/Lobby
dependency graph are unchanged and do not reference the modified ClientCore/Game
assemblies. No transport, conquest, test, or lane changes were made in response.
Evidence: `/tmp/p664-suite-Core.log`, `/tmp/p664-rerun-*.log`,
`/tmp/p664-conquest-followup*.log`; task report
[`P664_PARITY_WAVE3.md`](reports/P664_PARITY_WAVE3.md).

---

## P663 observation (2026-10-14)

The P663 full-suite passes exposed the following failures; each passed
**3/3 as an individually filtered test** afterward, so these are load flakes
under the P663 lane rule. No assertions were relaxed and no new quarantine
traits were added.

| Test | Isolated result | Full-pass symptom |
|---|---|---|
| `AutoReloadOnEmptyTests.Auto_reload_relays_RELOAD129_sector_wide` | 3/3 | Reload relay lookup had no matching element |
| `ShipUpdateBudgetLiveTests.Ship_dropped_one_tick_is_served_the_next_no_permanent_starvation` | 3/3 | Sequence lookup had more than one matching element |
| `ShipsUpdateLiveTests.Cross_side_ships_see_each_other_and_not_self` | 3/3 | B had not received HEAVY_SHIPS_UPDATE |
| `LiveWinConditionTests.Board_refused_once_mission_over` | 3/3 | Started-state control boarding handshake did not complete; existing LiveTiming case |
| `ThreePeerSeededSessionTests.SeedThreePeers_TwoSameSide_OneCrossSide_DeterministicVisibility` | 3/3 | Seeded peer visibility / join order; also observed by P648 |
| `RipcordRequestLiveTests.RipcordKey_Initiate_Countdown_Teleport_Deny_Cancel_EndToEnd` | 3/3 | Final MonoGame pass: client state did not clear on RIPCORD_ABORTED before the check |

The PING echo and P522 undock-clock failures in that initial run were real P663
regressions from unsolicited clock samples during empty/static treasure
snapshots. P663 fixed those sends and verified both tests 3/3; they are not
classified as flakes. See `docs/reports/P663_TREASURE_PARITY.md` for final
full-suite results and the long pod traversal checks.
## P628 run A/B load flakes (confirmed in isolation, not P628 code)

`scripts/run-tests.sh` runs A and B on the P628 final tree (client render LOD). Every
failure was a live-wire load flake in test code P628 did not touch; each was re-run 3×
in isolation (specific method):

| test (main-pass flake) | isolation | verdict |
|---|---|---|
| `LiveShipDeathFinalizeTests.Oxygen_dead_lifepod_with_no_friendly_station_is_finalized_server_side` | 3/3 pass | load flake (live-wire lifepod finalize receipt) |
| `GameOverPlayersWireTests.Win_by_elimination_broadcasts_game_over_players_to_surviving_peer` | 3/3 pass | load flake (live-wire game-over surviving-peer receipt) |
| `P590PodRescueTests.Pod_pickup_order_streams_queued_order_to_real_player_rescuer` | 2/3 (one intermittent) | load flake (live-wire pod-pickup queued-order receipt) — already listed below in history |
| `RetriedConnectLiveTests.RepeatedConnects_LeaveNoAccumulatedSocketsOrThreads` | 3/3 pass | load flake (live-wire socket-cleanup count, expected 10/actual 9 race) |

The MonoGame suite (which contains the P628 LOD tests) was **fully clean** in both runs
(A: 1977 passed / 0 failed; B: no failures).

---

## History (preserved verbatim from before P647)

The ledger below is the cumulative pre-P647 record. Post-P647, the suite's current,
authoritative state is the short section above; this history is retained for attribution
and shows the progression (P471 → P472 → P482 → P487 … P646b) that led to the P647
systemic change.

> NOTE: the majority of entries below are now obsolete — the tests named were either
> fixed in their P472–P646b tasks or fixed/quarantined in P647. Treat this as historical
> context, not the current status.

---

### Before (pristine tree, 3 parallel full-suite runs, P471 era)

| test | failures/runs | fails-in-isolation? | suspected cause | status |
|---|---|---|---|---|
| `ClientCore…ShipDeleteGoneLiveTests.DockOut_WatcherLatchesGone_UndockReturnsAndClears` | 3/3 | **Yes** | session-view watch latch misses the undock-return | real bug (P472, fixed P482) |
| `ClientCore…JoinFlowLiveTests.EntityTable_TracksARealTickingServer` | 3/3 | **Yes** | dup entity | real bug (P472) → stale test, fixed |
| `Core…LiveTurretGunnerBindingTests.Board_binds_gunner_…roundtrips_live` | 3/3 | **Yes** | orientation-only SINGLE_SHIP_UPDATE | real bug (P472) → stale test, fixed |
| `Core…ConquestTerritoryLiveTests.Flag_capture_tips_conquest_…game_over` | 3/3 | **Yes** | shield-less base capture gate | **fixed** |
| `Core…ConquestTerritoryLiveTests.Flags_disabled_capture_…does_not_end` | 3/3 | **Yes** | same gate | **fixed** |
| `Core…ConquestTerritoryLiveTests.Conquest_win_in_one_mission_does_not_leak_to_another` | 3/3 | **Yes** | shield-less gate + rotation enumerator | **fixed** (P647: main-pass-able, not quarantined) |
| `Core…GameOverPlayersWireTests.Conquest_win_in_one_mission_does_not_leak_…another` | 3/3 | **Yes** | same | **fixed** (P647: quarantined) |
| `Core…GameOverPlayersWireTests.Conquest_win_lists_every_connected_player_…side` | 3/3 | **Yes** | shield-less gate | **fixed** (P647: quarantined) |
| `Core…LiveMoneyTests.Board_refund_broadcasts_money_change_sidewide_not_cross_side` | 2/3 | No | fixed-sleep MONEY_CHANGE under load | load-flake (P472) |
| `ClientCore…FullSessionKillIntegrationTests.FullSession_ThreePeers_…CrossSideKill` | 1/3 → 4/5 | **Yes** | test-seeding bug | **fixed (P596)** |
| `Core…LiveWinConditionTests.Board_refused_once_mission_over` | 1/3 | No | 1200ms board latch | stable 3/3 after → **P647: quarantined** |
| `Core…LiveWinConditionTests.Multi_mission_disconnect_scopes_per_mission` | 1/3 | **Yes** | pump to Over not GameOvers | improved (P472) |
| `Core…LiveRipcordTests.First_activation_broadcasts_…flying_in_sector` | 1/3 | not run | live-wire timing | suspect (P472) |
| `Core…LiveCountdownStartPingTests.Multi_mission_countdown_scopes_per_mission` | 1/3 | not run | live-wire timing | suspect |
| `ClientCore…ThreePeerSeededSessionTests.SeedThreePeers_…DeterministicVisibility` | 1/3 | not run | live-wire timing | suspect |
| `Core…SideEliminationLiveTests.Eliminating_side2_sends_side_only_gameover_…` | 1/3 | not run | live-wire timing | suspect → **P647: quarantined** |
| `Core…LiveRelaunchTests.Carrier_without_launch_slots_uses_backward_fallback` | 1/3 | not run | live-wire timing | suspect |

### Later observations (P482, P487, P495, P498, P499, P500, P509, P513, P516, P521, P578, P597, P604b, P645, P646, P646b)

Across these tasks the same rotating live-wire load-flake class recurred (fixed-sleep
wire receipt under full-suite load), passing in isolation. Tests newly added to the ledger
during those sessions (all the same class unless noted): `Transport.TcpLoopbackProofTests…
ClientHalf_RaisesOnSessionLost`, `CrossSideCombatVisibilityLiveTests.CrossSide_sector_stays…`,
`LiveTeamLobbyTests.Unready_broadcasts…`, `LiveCombatWireTests.Moving_target_streams…`,
`AsteroidMinedLiveTests.Mining_broadcasts_ASTEROID_MINED…`, `P366LeaderBotCommandLiveTests…`,
`ShipAsteroidCollisionTests.Loopback_bounced…`, `CloakVisibilityGateLiveTests…`,
`DispenserEquipFireLiveTests.BuyMountAndFireARealDispenser_Live`, `StationUnderAttackAlertLiveTests…
Defending_side…`, `P590PodRescueTests.Pod_pickup_order_streams…`,
`P591dMinefieldExportAudienceTests.Npc_layer_drone_minefield…`,
`SessionLossLiveTests.EstablishedSession_ServerKilled…`,
`ChatTargetComposeLiveTests.ComposeDefault_Team_SendsToTeammate_NotEnemy`,
`LiveCountdownStartPingTests.StartGame_owner_all_ready…`,
`ConsoleLogNonBlockingTests.Verbose_gate…` + `…Queued_writer_splits_multiline…`,
`ShipIdAllocationLiveTests.Valid_logon_advances_nextShipId`,
`Transport.TcpTransportSendWorkerPoolTests.Pool_worker_and_reader_counts_stay_fixed_across_join_leave_churn` (P687 full-suite run 1; 3/3 in isolation = live-wire pool-churn load flake),
`ClientCore…ShipDeleteGoneLiveTests.DockOut_WatcherLatchesGone_UndockReturnsAndClears` (P687 run 2; 3/3 in isolation — the fixed-P482 dock latch, live-wire),
`Core…LiveRipcordTests.First_activation_broadcasts_to_everyone_flying_in_sector` (P687 run 2; 3/3 in isolation — live-wire ripcord receipt).

Added during P706: `ClientCore…P700ShowcaseLiveTests.Local_showcase_never_parks_with_target_for_five_seconds` — a live 15v15 showcase bot test that asserts no bot stays below idle speed with a target for >5 s (line `Assert.True(now - since <= 5, "Parked >5s")`). It is a bot-speed-under-machine-load timing flake, not the fixed-sleep wire-receipt class: under concurrent full-suite load (load avg 3+, other worktrees running suites) bots tick slowly enough to appear "parked". Confirmed pre-existing by running it on the pre-change commit `060fc834` under the same load: it failed there too (P706 post-change 1/3 pass, pre-change 2/3 pass — both intermittent). Independent of the P706 collision change (the change is proven byte-identical by P706SeparateCacheIdentityTests and the collision gameplay suite stays green).

**Regression caught by the suite (P646, FIXED):** `TcpTransportGreetTimeoutPoolTests.
Silent_connection_is_eventually_dropped` / `…ThreadPerPeer…` failed because the first P646
`ReadExactly` applied `Socket.Poll` to blocking sockets, bypassing `ReceiveTimeout`; fixed
by restricting Poll to `Blocking:false` sockets.

## Follow-ups (historical)

1. **DockOut production bug (closed P482)** — same-tick `HEAVY_SHIPS_UPDATE` defeating the
   `SDR_DOCKED` `IsGone` latch; fixed by invalidating the ship-update candidate cache on dock.
2. **(closed P472)** `JoinFlowLiveTests` / `LiveTurretGunnerBindingTests` corrected;
   `ShipDeleteGoneLiveTests.DockOut` was the production bug closed by #1.
3. **(closed P472)** Surviving live tests' waits converted where flaky (`Board_refund`
   dock-precondition; `Multi_mission` seating-order hardened).
4. Residual load-flakes ranked in the After table; swept by P647.

## P655c correction (2026-10-15)

Repeated game-over/conquest failures are not dismissed on the basis of three
clean isolations. The starting P655 branch's roster class passed 11/15 here,
versus 14/15 on `integ3`; its immediate pre-P655 parent also passed 11/15.
`integ3` has different cross-mission fixtures: it resolves recipients from
actual mission assignments and waits for roster receipt. The P655 branch
lacked those changes. Its named spawn-slot assumptions also affected the
single-mission winner/loser assertion and three-side elimination fixture.
These are concrete fixture defects, not proof of dropped game-over messages
or evidence that a P655 authority check should be removed.

P655c binds named roles to completed joins and waits for all asserted peer
notifications. A separate native-parity change batches GAME_OVER and roster
chunks before reset; the client now accumulates native 50-player chunks.
The original server passes the corrected roster/conquest fixtures 15/15 each;
the batching change alone still passes the original roster fixture only 11/15.
See the [P655c report](reports/P655_GAME_STATE_INTEGRITY.md#p655c) and its
per-run evidence for final-tree rates, side elimination, P660 repetitions,
and complete suite results. Existing quarantine traits are unchanged.


## P676 observation (2026-10-15)

Final unfiltered sequential suites: Core **4352 pass / 2 fail / 0 skip**, ClientCore **945 pass / 1 fail / 0 skip**, MonoGame **2151 pass / 0 fail / 3 skip**. The opt-in local 15v15 next-match showcase passed. The final local 15v15 battle recorded 5 kills; the real MCP purchase/death/pod/relaunch/refire check passed 3/3 alone.

All observed receipt failures passed 3/3 exact-method isolated retries after all three final suites. These are load/receipt flakes under the task lane rule. No timing-only product changes, assertion relaxations, or quarantine changes were made.

| Test | Symptom | Final isolation |
| --- | --- | --- |
| `Allegiance.ClientCore.Tests.ThreePeerSeededSessionTests.SeedThreePeers_TwoSameSide_OneCrossSide_DeterministicVisibility` | System.InvalidOperationException : logon drain order diverged from connection order — peer2 side 0 not cross-side (0, 1, 0); determinism broken | 3/3 pass |
| `Allegiance.Core.Tests.LiveCountdownStartPingTests.StartGame_owner_all_ready_goes_through_real_countdown` | Assert.Single() Failure: The collection was empty | 3/3 pass |
| `Allegiance.Core.Tests.LiveRipcordTests.Re_target_activates_to_requester_only` | Assert.Single() Failure: The collection was empty | 3/3 pass |
| `Allegiance.Core.Tests.LiveRipcordTests.Ship_anchor_insufficient_energy_aborts_consumed_not_retried` | Assert.Single() Failure: The collection was empty | 3/3 pass |
| `Allegiance.Core.Tests.LiveTreasurePickupTests.Rescue_ship_picking_cash_treasure_credits_side_broadcasts_moneychange_and_destroys_it` | Assert.Single() Failure: The collection was empty | 3/3 pass |
| `Allegiance.Core.Tests.SideEliminationLiveTests.Eliminating_side2_sends_side_only_gameover_to_it_alone` | Assert.Single() Failure: The collection was empty | 3/3 pass |

See [P676 report](reports/P676_PARITY_WAVE4B.md) and [invocation ledger](reports/P676_RESULTS.json). The report separately explains the native-law fixture corrections; those are not classified as load flakes.

## P678 observation (2026-10-15)

The final build completed with zero warnings/errors; 71 affected methods (86 cases) passed three exact-method isolated invocations each. Final full-suite raw counts and the additional overlapping verification pass are preserved in [P678 report](reports/P678_PARITY_WAVE5.md) and [invocation ledger](reports/P678_RESULTS.json). Every failure below passed 3/3 alone after all full-suite activity finished. Per the P678 Codex lane rule these are load/receipt flakes. No assertion, timeout, quarantine, or product change was made for them.

| Test | Symptom | Final isolation |
| --- | --- | --- |
| `Allegiance.Core.Tests.LiveRipcordTests.Cancel_while_active_aborts_sector_wide` | Assert.Single() Failure: The collection was empty | 3/3 pass |
| `Allegiance.Core.Tests.LiveTreasurePickupTests.Rescue_ship_picking_cash_treasure_credits_side_broadcasts_moneychange_and_destroys_it` | Assert.Single() Failure: The collection was empty | 3/3 pass |
| `Allegiance.Core.Tests.P493EnemySpottingLiveTests.Probe_spot_of_cross_side_ship_delivers_status_and_hidden_to_side0_peer` | Assert.DoesNotContain() Failure: Filter matched in collection | 3/3 pass |
| `Allegiance.Core.Tests.Transport.InboundFloodSafetyLiveTests.Unsupported_fmid_flood_log_is_rate_limited_others_keep_ticking` | healthy bot 0 must keep receiving per-tick updates during the flood (had 0, now 0) | 3/3 pass |

The treasure-pickup check initially passed 2/3 alone and then passed 3/3 in the final triplet; both triplets are recorded.


## P680 observation (2026-10-15)

The simulation countdown sound bundle completed 53 affected methods (80 data cases), each **3/3 individual runs**. The final unfiltered Core, ClientCore (including the opt-in local P660 15v15 showcase), and MonoGame passes reported the raw counts in [P680 report](reports/P680_PARITY_WAVE6.md); [P680_RESULTS.json](reports/P680_RESULTS.json) preserves every invocation.

Every failure below passed **3/3 exact-method isolated reruns after all three full suites finished**, qualifying as a load/receipt flake under the Codex lane rule. No assertion, timeout, quarantine, or product change was made for it.

| Test | Full-pass symptom | Final isolation |
| --- | --- | --- |
| `Allegiance.Core.Tests.LiveRipcordTests.Re_target_activates_to_requester_only` | Assert.Single() Failure: The collection was empty | 3/3 pass |
| `Allegiance.ClientCore.Tests.ThreePeerSeededSessionTests.SeedThreePeers_TwoSameSide_OneCrossSide_DeterministicVisibility` | System.InvalidOperationException : logon drain order diverged from connection order — peer2 side 0 not cross-side (0, 1, 0); determinism broken | 3/3 pass |


## P681 observation (2026-10-15)

The final unfiltered sequential Core, ClientCore (including the opt-in local 15v15 next-match showcase), and MonoGame suites produced the raw counts in [P681 report](reports/P681_PARITY_WAVE7.md). All 2 full-pass failures passed **3/3 exact-method isolated reruns after all three suites completed**, qualifying as load/receipt flakes under the Codex lane rule. No assertions, deadlines, quarantine tags, or product behavior were changed for them. The affected cohort passed three individual invocations each; exact counts are in the report.

| Test | Full-pass symptom | Isolation |
| --- | --- | --- |
| Allegiance.Core.Tests.Transport.InboundFloodSafetyLiveTests.Unsupported_fmid_flood_log_is_rate_limited_others_keep_ticking | healthy bot 0 must keep receiving per-tick updates during the flood (had 0, now 0) | 3/3 pass |
| Allegiance.Core.Tests.LoadoutChangeLiveTests.Mount_and_remove_part_broadcast_loadout_change_to_owner_and_teammate_but_not_enemy | Assert.Contains() Failure: Filter not matched in collection; Collection: [] | 3/3 pass |

See [P681_RESULTS.json](reports/P681_RESULTS.json) for the complete invocation ledger.

## P655d observation (2026-10-15)

Full unfiltered suites after the P655d build: Core **4,242 pass / 2 fail**,
ClientCore **900 pass / 0 fail / 1 existing opt-in skip**, MonoGame
**2,120 pass / 0 fail / 3 existing private-pack skips**, Lobby **19 pass / 0 fail**.
After all four suites completed, both failed methods passed **3/3 alone**:

| Test | Full-run observation | Isolated result |
| --- | --- | --- |
| `Transport.TcpTransportGreetTimeoutPoolTests.Silent_connection_does_not_block_a_second_client_join` | `TcpTransport.Shutdown` called `Thread.Join` before the thread started (`ThreadStateException`). Same shutdown symptom as the earlier P655 entry, in a different transport test. | 3/3 pass |
| `LoadoutChangeLiveTests.Mount_and_remove_part_broadcast_loadout_change_to_owner_and_teammate_but_not_enemy` | Expected loadout-change receipt collection was empty at the assertion. | 3/3 pass |

These satisfy the brief's explicit load-flake classification rule. Neither
assertions nor quarantine traits were changed. This classification does not
claim that the underlying transport shutdown race has been fixed.

The P655d startup regression is **not a flake**: omitted JSON limit fields
became zero through the generated init-only constructor, reducing effective
capacity to one. The original two-worker executable fails after 15 s even with
a correct bearer; adding just the two explicit limit values seats both original
same-name probes. See the [P655d report](reports/P655_GAME_STATE_INTEGRITY.md#p655d)
and [results](reports/P655D_RESULTS.json) for the failing-first evidence,
native duplicate-login decision, config fix and real two-worker MCP checks.

The routine ClientCore showcase skip was subsequently enabled with
`ALLEGIANCE_LIVE_P660_SHOWCASE=1` and passed **3/3 alone** (local 15v15 MCP,
30/30 bots and 2/2 humans migrated each time). Only the three unavailable
private Meshy-pack cases remain unexecuted.


## P688 observation (2026-10-15)

The P655d/integ8 merge's unfiltered sequential suites reported Core **4552 pass / 3 fail / 0 skip**, ClientCore **1001 pass / 0 fail / 0 skip**, MonoGame **2186 pass / 0 fail / 3 existing private-pack skips**, and Lobby **19 pass / 0 fail / 0 skip**. The opt-in local P660 showcase passed. All 82 focused case executions passed, including the rig-style JSON boot and three repetitions of the P682 two-match and P655d identity/worker scenarios.

After all four full suites completed, every failed Core method passed **3/3 exact-method isolated reruns**:

| Test | Full-run symptom | Isolation |
| --- | --- | --- |
| `LiveCountdownStartPingTests.Multi_mission_countdown_scopes_per_mission` | Peer a1's SET_START_TIME collection was still empty at the immediate assertion. | 3/3 pass |
| `ShipsUpdateLiveTests.Cross_side_ships_see_each_other_and_not_self` | B had not received HEAVY_SHIPS_UPDATE. | 3/3 pass |
| `Transport.P670SendStallTests.Diagnostic_updates_and_samples_allocate_zero_bytes_after_warmup` | Cleanup hit `ThreadStateException: Thread has not been started` in `TcpTransport.Shutdown`, line 533. The allocation assertion did not fail. | 3/3 pass |

These match previously recorded receipt/shutdown flake symptoms; the isolated passes do not establish a fix for the underlying races. No assertions, deadlines, quarantine traits, or transport behavior were changed for these observations. See the [P688 report](reports/P688_MERGE_P655D.md) and [invocation ledger](reports/P688_RESULTS.json) for raw full-suite counts, filters, stack traces and individual rerun results.


## P699 observation (2026-10-15)

The initial unfiltered P699 diagnostic suites exposed two unchanged receipt tests that passed **3/3 exact-method isolated invocations** after all three suites completed. The other initial failures were docking-fixture incompatibilities and were corrected; they are not classified as flakes. See the [P699 report](reports/P699_POD_DOCKING.md) and [isolated invocation ledger](reports/P699_assets/affected-results.json).

| Test | Initial full-suite symptom | Isolation |
| --- | --- | --- |
| `LiveRipcordTests.Cancel_while_active_aborts_sector_wide` | Bystander abort receipt collection was empty at `Assert.Single`. | 3/3 pass |
| `LiveLifepodEnduranceDisplayTests.Driver_sees_countdown_others_do_not_and_dock_cancels` | The driver still had a queued `0.100` countdown update immediately after manual docking. | 3/3 pass |

No assertions, receipt deadlines, quarantine traits, or product code were changed for these two observations. The isolated passes do not establish a fix for the underlying receipt races.


P699's final rebuilt sequential suites reported Core **4562 pass / 2 fail / 0 skip**, ClientCore **1019 pass / 0 fail / 0 skip** (local P660 showcase enabled), MonoGame **2200 pass / 0 fail / 3 existing private-pack skips**, and Lobby **19 pass / 0 fail / 0 skip**. After all four finished, every method below passed **3/3 exact-method isolated invocations**:

| Test | Full-run symptom | Final isolation |
| --- | --- | --- |
| `LiveRipcordTests.Cancel_while_active_aborts_sector_wide` | Same empty abort receipt collection as the initial diagnostic run. | 3/3 pass |
| `LoadoutChangeLiveTests.Mount_and_remove_part_broadcast_loadout_change_to_owner_and_teammate_but_not_enemy` | Expected loadout-change receipt collection was empty. | 3/3 pass |
| `Transport.TcpTransportSendWorkerPoolTests.Pool_worker_and_reader_counts_stay_fixed_across_join_leave_churn` | An additional earlier suite attempt timed out waiting for `OnPeerJoined` for a raw peer. | 3/3 pass |

The [final isolated ledger](reports/P699_assets/post-final-results.json) preserves all nine passes; [validation-summary.json](reports/P699_assets/validation-summary.json) records final suite timestamps and portable TRX hashes. No source, assertion, timeout or quarantine changes were made for these failures. These observations classify intermittent receipt/join failures; they do not claim an underlying transport fix.
## P694b observation (2026-10-15)

Full unfiltered Debug suite counters (every completed attempt):

- `suite-Allegiance.Core.Tests`: 4588 pass / 2 fail / 0 skip.
- `suite-Allegiance.ClientCore.Tests`: 1010 pass / 0 fail / 0 skip.
- `suite-Allegiance.ClientCore.MonoGame.Tests-retry`: 2195 pass / 0 fail / 3 skip.
- `suite-Allegiance.Core.Tests-final`: 4590 pass / 0 fail / 0 skip.

The initial MonoGame attempt ended without a summary/TRX; its complete diagnostic
retry passed. No method failure or cause was established for the interrupted
attempt. All 169 affected Core methods and five ClientCore methods passed three
isolated Release runs each; the corrected mass-event fixture also passed both
critical single rows three times in each configuration. The full P539 recordings
and opt-in P660 local showcase were enabled.

After the final suite, every full-suite failure passed **3/3 fresh exact-method
isolated Debug reruns**:

| Test | Full-suite symptom | Final isolation |
| --- | --- | --- |
| `Allegiance.Core.Tests.AsteroidMinedLiveTests.No_broadcast_fires_once_miner_hits_capacity` | Assert.Equal() Failure: Values differ; Expected: 11; Actual:   26 | 3/3 pass |
| `Allegiance.Core.Tests.Transport.TcpTransportSendWorkerPoolTests.Pool_worker_and_reader_counts_stay_fixed_across_join_leave_churn` | System.TimeoutException : no OnPeerJoined for raw peer | 3/3 pass |

These are load/receipt flakes under the P694b brief's explicit rule. The isolated
passes do not establish a fix for the delayed mining receipts or raw-peer join
timeout. No assertions, receipt deadlines, quarantine traits, or transport/mining
behavior were changed for them. The intermediate single-row P694 timing failure
was a fixture warmup defect and was corrected separately; it is not classified as
a load flake. See the [report](reports/P694_MASS_EVENT_TICKS.md) and
[ledger](reports/P694b_assets/verification.json) for raw counters, errors, stack
traces, timing profiles and all isolated attempts.
## P690 observation (2026-10-15)

The final unfiltered Core, ClientCore (including the enabled local 15v15 next-match showcase), and MonoGame suites produced the raw counts in [P690 report](reports/P690_PARITY_WAVE8.md). After all three suites finished, every failed method below passed **3/3 exact-method isolated runs**, satisfying the task's load-flake rule. No assertions, deadlines, quarantine traits, or product behavior were changed for these observations.

| Test | Full-pass symptom | Isolation |
| --- | --- | --- |
| `Allegiance.Core.Tests.LiveRipcordTests.Ship_anchor_insufficient_energy_aborts_consumed_not_retried` | Assert.Single() Failure: The collection was empty | 3/3 pass |
| `Allegiance.Core.Tests.P493EnemySpottingLiveTests.Probe_spot_of_cross_side_ship_delivers_status_and_hidden_to_side0_peer` | Assert.DoesNotContain() Failure: Filter matched in collection | 3/3 pass |

See [P690_RESULTS.json](reports/P690_RESULTS.json) for commands, raw counts, messages, and artifact paths. Isolated passes do not establish a fix for the underlying timing/transport behavior.


## P692 observation (2026-10-15)

The final unfiltered sequential suites reported Core **4,615 pass / 2 fail / 0 skip**, ClientCore **1,014 pass / 0 fail / 0 skip**, and MonoGame **2,187 pass / 0 fail / 3 existing private Meshy-pack skips**. The enabled local P660 15v15 bots/humans next-match showcase passed (21.753 seconds). The affected cohort passed **399 individual exact-method invocations / 651 case executions**, with zero failures or skips.

After all three full suites completed, both failed Core methods passed **3/3 exact-method isolated reruns**, satisfying P692's explicit load-flake classification rule:

| Test | Full-suite symptom | Final isolation |
| --- | --- | --- |
| `Allegiance.Core.Tests.LiveRipcordTests.Cancel_while_active_aborts_sector_wide` | `Assert.Single`: abort receipt collection was empty. | 3/3 pass |
| `Allegiance.Core.Tests.LiveP388DebugNoGameOverTests.Debug_mode_disconnect_does_not_end_mission_and_miners_keep_cycling` | Side-0 miner 8096 completed only one counted mine/sell cycle within 240,000 ticks. | 3/3 pass |

No assertions, deadlines, quarantine traits, or product behavior were changed for these observations. The isolated passes do not establish the underlying cause or a fix for the receipt/cycle observation. The real loopback mining/sale and MCP purchase/death/pod/relaunch/refire checks separately passed 3/3 alone, and the controlled real bot TCP battle recorded one credited kill per isolated run.

See the [P692 report](reports/P692_PARITY_WAVE10.md) and [invocation ledger](reports/P692_RESULTS.json) for raw counts, exact filters, failure messages/stacks, and per-run TRX/log paths. Preliminary overlapping runs were discarded from certification; the complete affected cohort was subsequently rerun serially under a verifier lock.

## P707 observation (2026-10-16)

The corrected P707 revision passed its 74-method affected cohort three times
alone (222 invocations / 363 case passes), ten standalone parking-showcase runs,
and both final ClientCore sweeps, including the parking test. The final two
complete solution sweeps recorded **16,037 passes / 1 failure / 6 existing
optional Meshy-pack skips**.

The single final-suite failure was
`Allegiance.Core.Tests.Transport.TcpTransportSendWorkerPoolTests.Pool_worker_and_reader_counts_stay_fixed_across_join_leave_churn`
in the first Core sweep: `TimeoutException: no OnPeerJoined for raw peer`.
The second Core sweep passed. After all final suites finished, the failed
method passed **3/3 fresh exact-method isolated runs**, with unchanged
assertions and deadlines, satisfying P707's explicit load-flake rule. This
observation does not establish the timeout's underlying cause.

The rejected clock-only candidate's parking failure is **not** classified as a
load flake: its isolated reruns passed only 2/3. A separate deterministic
moving-echo cancellation regression then reproduced the remaining defect, and
the final revision added a sustained escape hold. The candidate's failed runs
and its two diagnostic suite sweeps remain recorded separately from final-code
certification.

See the [P707 report](reports/P707_WAVES_PARKING.md) and
[complete invocation ledger](reports/P707_assets/results.json) for every
command, raw counter, failure message/stack, source fingerprint and artifact
path. No failed attempt was discarded or assertion relaxed.

## P718 — FEDMSG interface wave (2026-10-16)

The two unfiltered Core invocations each reported one existing receipt/join
failure. After all full-suite activity finished, both exact methods passed
**3/3 isolated invocations**, qualifying as load flakes under the P718 Codex
lane rule. No assertion, deadline, quarantine, or transport/loadout product
behavior was changed for these results.

| Test | Full-run symptom | Post-suite isolation |
| --- | --- | --- |
| `Allegiance.Core.Tests.Transport.TcpTransportSendWorkerPoolTests.Pool_worker_and_reader_counts_stay_fixed_across_join_leave_churn` | `System.TimeoutException: no OnPeerJoined for raw peer` (Core run 1) | 3/3 pass |
| `Allegiance.Core.Tests.LoadoutChangeLiveTests.Mount_and_remove_part_broadcast_loadout_change_to_owner_and_teammate_but_not_enemy` | `Assert.Contains` on an empty notification collection (Core run 2) | 3/3 pass |

Both ClientCore runs, all three MonoGame invocations, and both Lobby runs had
zero failures. The extra MonoGame invocation and overlapping ClientCore/MonoGame
runs resulted from a coordinator restart error; this exceeded the requested
two-run MonoGame limit and is explicitly retained as a validation exception.
All 56 affected methods passed 3/3 alone (249 case passes), and the standalone
P700 never-parks gate passed 10/10. See the [P718 report](reports/P718_INTERFACE_WAVE.md)
and [invocation ledger](reports/P718_RESULTS.json) for raw counts and errors.

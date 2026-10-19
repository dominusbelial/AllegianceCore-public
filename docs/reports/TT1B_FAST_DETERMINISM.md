# TT-1b — Fast determinism under parallel CPU load

Implementation and validation are in progress. This report will be finalized with
all five complete contention runs, the one broad sweep, and focused evidence.
No commits or pushes are made in this lane.

## Causes and repairs

The supplied TT-1 and dispatcher TRX identify thirteen distinct methods. The
third dispatcher run contains nine failures, including the otherwise unnamed
`CrossSideExportLiveTests.Same_side_teammate_still_gets_teamexport_no_cross_duplicate`,
`GameOverPlayersWireTests.Win_by_elimination_broadcasts_game_over_players_to_surviving_peer`,
and `LiveRelaunchTests.Carrier_without_launch_slots_uses_backward_fallback`.

| Test / family | Actual load-sensitive assumption | Repair |
| --- | --- | --- |
| `DeadLatchRespawnLiveTests.Own_ship_sim_resumes_and_accepted_climbs_after_death_respawn_undock` | Real PING clock extrapolates wall time; the fixture advances `TickSim` by fixed 50 ms and paces with sleeps. CPU descheduling and unpaced respawn pumping invalidate that clock relationship. | Explicit `Tier=Live`; retain the real clock, both death/respawn arcs, accepted-count increments and zero-rejection assertion. Dispose both fixtures' servers. |
| `ShipsUpdateLiveTests.Cross_side_ships_see_each_other_and_not_self` | Receipt by A was treated as evidence that B had decoded its independent stream. | Wait for both peers' updates containing the other ship. The departure sibling also selects an update with the requested absence, allowing older in-flight frames. |
| `ShipStatusServerTests.Teammate_ship_playerinfo_is_broadcast_once_after_export_before_status` | Concurrent seating can make the observer a late joiner; its lobby roster also contains the subject's PLAYERINFO, independently of the teammate one-shot. `Single` then legitimately sees two. | Seat the observer and decode its own PLAYERINFO before joining the subject; also await the subject's own join burst. Preserve the teammate one-shot and ordering assertions. The P62 sibling reads the later status before prerequisite exports, preventing a torn cross-message snapshot. |
| `TcpTransportSendWorkerPoolTests.Pool_worker_and_reader_counts_stay_fixed_across_join_leave_churn` | The callback wait allowed only five seconds of scheduling delay, and thread-list pruning was used as a proxy for all resource release. Production inspection also found concurrent departures mutating `_peerIpById` and `_activeByIp` without the accept-loop lock. | Synchronize accounting under `_gate`; wait for both zero live connections and the baseline tracked threads between waves. Pin the fixture's per-IP cap to eight so each eight-peer wave must release all slots; restore it afterward. Give the raw join callback a 30-second failure bound. |
| `TcpTransportPriorityLane{Pool,ThreadPerPeer}Tests.Ordered_join_frames_are_never_shed_when_the_stream_overflow_collapses` | Flooding one coalescing key and sleeping 80 ms does not establish a blocked writer. A legitimate drain makes the expected queue count smaller. The old payload did not even force the normal 8 MiB capacity boundary. | Hold the real sender's existing queue lock while enqueuing and inspecting; temporarily use 16 KiB capacity and distinct best-effort keys to force actual shedding. Assert all fifty ordered frames remain and ordered drain priority survives. |
| `TcpTransportCoalesceAndReason{ThreadPerPeer,Pool}Tests.Guaranteed_coalesced_frames_are_never_shed_but_still_bounded` | The guaranteed frame could legitimately leave the queue while the test slept; the two coalesced keys did not exceed the production byte cap. | Pause the queue consumer under the same lock, interleave four best-effort keys at a 16 KiB cap, and require exactly one guaranteed snapshot plus bounded, actually shed best-effort traffic. |
| `MissileBroadcastTests.Directly_fired_missile_broadcasts_create_and_destroy_to_teammate_but_not_enemy` | Compare the launch message's historical spawn position with the shooter's later, moving position after receive pumping. | Capture launch-time shooter position before firing; retain the distance, direction, distinct IDs, destruction and enemy-exclusion assertions. |
| `LiveRipcordTests.First_activation_broadcasts_to_everyone_flying_in_sector` and sibling first-activation/abort waits | Requester receipt does not synchronize the bystander's reader; resetting between phases could also erase a still-arriving activation. | Wait for both intended recipients before asserting or starting the next phase. Keep re-target delivery requester-only. |
| `LiveLogoffTests.Logoff_echoes_releases_and_retires_the_ship` | Production queues a guaranteed echo, then immediately closes the sender and discards its queue. Scheduling decides whether the writer wins. | Retire the peer logically, reject further outbound/inbound traffic and already-decoded replay commands, and drain the queued reply on the existing writer before closing. Keep physical connection-cap accounting charged until closure; bound an unresponsive drain by the existing send timeout. |
| `LiveLifepodEnduranceDisplayTests.Driver_sees_countdown_others_do_not_and_dock_cancels` | Receive time was used as a substitute for generation time: a pre-dock depleted frame arriving late was asserted to be docked. Tight tick loops could also coalesce away the full/low countdown samples. | Decode each sampled wire tick before advancing; classify post-dock frames by their embedded tick. Force one dirty post-dock pose and await its receipt so the inactive-wire assertion is non-vacuous. Retain the owner-only countdown and watcher checks. |
| `CrossSideExportLiveTests.Same_side_teammate_still_gets_teamexport_no_cross_duplicate` | The fixture reused the cross-side 900 km separation for same-side peers. That is outside the native universe boundary: one extra post-seat tick can apply fatal bounds damage and eject hull 92 into lifepod 33 while the original export is still in flight. | Derive same-side positions from the core's universe radius and boundary constant; keep them well inside that boundary. Preserve cross-side spawn geometry, exact export counts, name and real-hull assertions. |
| `GameOverPlayersWireTests.Win_by_elimination_broadcasts_game_over_players_to_surviving_peer` | Automatic rotation can replace `ShipAssignment` before the old match assertion looks that ID up in `server.Ships`. | Capture the survivor's old-match side before triggering elimination; validate the immutable game-over roster against it. |

Additional failures observed in the first contention diagnostic are repaired too:

| Test | Cause | Repair |
| --- | --- | --- |
| `TcpTransportCoalesceAndReasonThreadPerPeerTests.Coalesce_distinct_keys_remain_independent` | The real writer can drain every key during the 80 ms sleep. | Pause the consumer and assert exactly three independent keys/map entries; cover both modes. Also pause the same-key bound and ordered-priority sibling tests; remove their obsolete socket-saturation assumptions. |
| `StationsUpdateLiveTests.Damaged_station_fraction_reflected_in_next_update` | `EntryOf` returns a zero-filled default for an update lacking this station; zero passed the `!= 240` wait. | Require the station ID and expected damaged shield byte, then select that immutable matching update. |
| `DiscoveryTests.Unicast_probe_finds_a_loopback_responder` | The production discovery response deadline is a UI latency choice, too short as a scheduler allowance for a loaded test host. | Give this positive functional probe a 30-second failure bound; leave production discovery deadlines unchanged. |
| `P653DPodPurchaseLiveTests.Hangar_launch_after_ejection_or_pod_death_buys_real_hull` | Ejection's independent pod-sync ACK (`FLaunch=false`, pod hull) can arrive after resetting ACK history and satisfy “any ACK”. | Await and select the ACK naming the requested real hull; retain purchase success and launch assertions, in both theory cases. |
| `ClientWarpApproachLiveTests.Client_bot_steered_by_warp_approach_law_transits_two_hops_to_target` | Thirty thousand tight simulation ticks race asynchronous control/pose feedback, exhausting the flight budget before a useful feedback cycle under saturated CPU. | Await the actual generated sector/pose before advancing the next tick; apply the same barrier to both slow-hull comparisons. Keep all three in Fast, retain both hops, the legacy outside-transit assertion, and exact final-sector assertions. |
| `LivePositionRequestTests.Non_leader_ack_refused` | Resetting C's history after only A decoded the pending-request broadcast lets C's late legitimate POSITIONREQ appear in the negative phase. | Await the pending broadcast on A, B and C before reset; use C's ordered PING echo as a dispatch barrier after its forbidden ACK, then assert the same silence and pending-request behavior. |

The diagnostic also caught both `LiveCountdownStartPingTests` owner-start
and mission-scope checks, and `LiveTeamLobbyTests.Unready_broadcasts_team_ready_false`.
They waited for one recipient and asserted the other immediately. Countdown
checks now stop ticking once Starting is established and await both receive
threads without consuming the countdown's simulation budget. Unready waits for
both whole-mission recipients. Their broadcast and mission-scope assertions are
unchanged.

Fixed carrier-position and ripcord-cancel checks leave the `LiveTiming`
quarantine. Both ordered-world queue-priority variants also leave quarantine
now that their consumers are paused deterministically. The slow-hull legacy comparison also leaves quarantine after its pose barrier.
Readiness and mission-countdown checks await both intended recipients and leave
quarantine as well.

The next contention attempt also caught the station-visibility sibling reading
an in-flight join update for station 1051, rather than the fixture's station 9401.
It now waits for, and selects, an update containing each intended station; both
sides' real field and cross-side absence assertions remain intact.

A subsequent full contention attempt exposed
`P657bStallTelemetryTests.DedicatedWatchdogHasHighestPriority_AndLoggerFlushesOnShutdown`:
the test asserted that the logger had exited immediately after `Dispose`, but
`StallWatchdog.Dispose` deliberately bounds each join to 500 ms
(`tools/allegiance-mcp/StallWatchdog.cs:147–156`). The fixture now awaits the
logged event and both dedicated threads with 30-second upper bounds, retaining
the thread-name, priority, non-pool and eventual-exit assertions. Production
watchdog behavior is unchanged.

The same attempt exposed a real missile lifetime race in
`MissileTrackingLiveTests.FiredMissile_AppearsMovesAndDisappears_InClientWorld_OverTheWire`.
The server log confirms destruction was emitted, but a pending best-effort
MISSILE_UPDATE could follow the higher-priority ordered MISSILE_DESTROYED and
rehydrate that ID in the late-viewer upsert path. `BroadcastMissileDestroyed`
now replaces the pending missile-position batch with an empty snapshot **before**
enqueuing destruction. A previously promoted frame still completes before the
ordered event; a pending stale frame is superseded. Other live missiles resume
with their normal next snapshot. No client tombstone or late-viewer restriction
is introduced.

`MissileLifetimeQueueTests` freezes a real sender with its existing controlled
sink, queues a live missile update, expires the missile, then sends a marker in
a later stream key. Once the client consumes that marker, destruction must
still be final. Both transport modes and both immediate/asynchronous publishing
are covered. This preserves native termination at
`clintlib/appmsg.cpp:1790–1803`; message 205 itself is a port-only extension.

The next attempt caught
`ShipUpdateBudgetLiveTests.Ship_dropped_one_tick_is_served_the_next_no_permanent_starvation`.
It treated six received latest-wins snapshots as six consecutive simulation
ticks. Coalescing can legitimately skip a tick and invalidate that inference.
The fixture now generates six ticks individually and awaits each exact
`TimeReference` before advancing, preserving its strict next-tick fairness and
all-NPC coverage assertions. The failed attempt was stopped after this logged
failure and is excluded from the five complete clean proof runs.

The bounded sibling later caught an in-flight join snapshot (zero NPC entries)
in its eight-message sample. All three budget checks now share `ReadTicks`, which
selects only frames generated after NPC creation / target selection and decodes
each exact tick before advancing. The bounded check still asserts four entries
in eight ticks, fairness still checks six consecutive ticks, and the target
check still requires the target once in slot zero across ten ticks. All three
are requalified three times individually after this shared-helper change.

The following contention attempt exposed two more setup assumptions:
`AutoReloadOnEmptyTests` treated bot array order as asynchronous join order,
so its designated subject could be the lone opposing-side peer with no
same-side observer. Its live seat now joins peers in role order, and the class
uses the existing LiveNetwork collection to isolate server-global settings.
`RadarTargetHighlightLiveTests` waited for identity then dereferenced a pose
that arrives independently; it now requires both own and enemy poses as well
as real enemy identity before gathering radar candidates.

`MissionRotationLiveTests.Finished_mission_rotates_to_fresh_notstarted_and_new_joiner_starts`
then exposed the same distinction between server state and client receipt:
`Starting` was set on the server while the new joiner had decoded only
`NotStarted`. The test now awaits each already-generated `Starting` / `Started`
frame without advancing simulation. It still drives the real countdown and
checks pristine rotation, migrated peers, and all three wire stages.

The next run caught the `idle` case of
`P693TeamLimitsLiveTests.Batched_bots_consume_native_balance_refusal_and_choose_an_eligible_side`.
The server had undocked the bot, but `SpawnLaunch.Observe(Bot)` always preferred
any newly received DOCKED over all subsequent SHIP_STATUS messages. Installing
the observer between SET_CLUSTER and the join burst's DOCKED made that old
dock permanently mask newer flying status. The plain Bot now exposes an atomic
receive-ordered own launch signal; the MCP observer uses its revision and state.
No launch deadline, purchase validation, team-balance rule, or server state is
changed. A deterministic late-seat-dock → flying-status → redock regression
checks both client types, including that a real later redock revokes confirmation.
It reproduces the plain-bot failure against the original observer and passes
with the repair. The fixture uses a new status timestamp for its combat-client
control case, matching a real post-seat status rather than reusing tick zero.

The one unfiltered sweep additionally caught the Fast
`MineCrossSideVisibilityLiveTests.Enemy_ship_spots_a_laid_mine_once_in_range_and_learns_its_destruction`.
Its spawn callback assigned near/far geometry in join order, while later phases
assumed bot-array order. When B joined first, A laid the mine at the far point
and the fixture moved B near the unrelated base point. The fixture now anchors
A at the base and B far away after resolving both assignments. Scanner range,
line of sight, exact-one export, owner/type fields and destruction checks remain
unchanged. The earlier incomplete Fast attempt had passed this method, but is
excluded because this later repair changes the candidate.

A later complete contention gate recorded four additional Fast failures, all
repaired before restarting the five-run proof:

- `MiscKeyBindingLiveTests.Live_own_ship_is_real_and_center_ranking_selects_a_real_ship`
  waited for own identity but ranked candidates before either NPC pose arrived;
  only warp candidates existed. It now awaits own and both NPC identities/poses.
  The analogous `FilteredTargetingLiveTests` receive wait is repaired too, with
  the related radar case requalified three times.
- `P493EnemySpottingLiveTests.Probe_spot_of_cross_side_ship_delivers_status_and_hidden_to_side0_peer`
  assigned side-0 observer / side-1 intruder in callback order but consumed bot
  variables in connection-call order. If the second connection won, the negative
  assertion inspected the intruder's own status. Seat the observer first.
- `LoadoutChangeLiveTests.Mount_and_remove_part_broadcast_loadout_change_to_owner_and_teammate_but_not_enemy`
  awaited only the teammate and immediately asserted the owner's receipt. Both
  receive paths now complete before the phase advances. Mount/remove and
  boarding/leaving clear recipient histories between phases and require both
  recipients' new mutation frames; enemy silence remains asserted.
- `CloakVisibilityGateLiveTests.Detectable_enemy_still_receives_the_cloaked_ship_inside_the_collapsed_radius`
  counted a late pre-cloak snapshot as data generated after the visibility gate
  closed. Both cloak checks now classify frames by generated tick, require
  non-vacuous post-transition samples from each intended reader, and await a
  newly generated cloaked entry after moving into detection range. The scanner
  radius, visibility sweep, energy fixture and cloak bits are unchanged.

The next full gate identified two remaining reader-order assumptions:
`LiveP548GameOverStageGateTests` observed server Stage Over before the survivor
had decoded GAME_OVER, and `LiveCombatWireTests.Real_Bot_class_observes_its_own_kill_over_the_wire`
observed the victim's kill before the shooter decoded its independent credit.
All three P548 positive GAME_OVER checks now await the generated wire event
without advancing simulation; both combat-client variants await the shooter as
well as the victim. Exact-one, native stage-gate, credit and death assertions
remain unchanged.

That gate also passed, but timed, the exhaustive
`P656ShowcaseFinaleTests.P656b_AllRealStationTargetsSelectOnlyBombersWithAllTech`
at 26.176 s, beyond its calibrated Fast allowance of 19.499 s. It sweeps every
real station type through the full hull/part selector. It now explicitly belongs
to **Live**, following [the established expensive-corpus classification](../testing/TIERS.md).
Every station, bomber-role, non-carrier and non-flagship assertion remains;
this is a functional corpus scenario rather than a performance-budget test.
It is requalified three times alone and is not skipped.

A subsequent full gate caught the ownerless-NPC foundation test accepting the
first HEAVY frame after spawn; it could still be an in-flight join frame with no
NPC. It now awaits and selects an update containing the actual NPC ID. The
ship-departure sibling now establishes the matching presence baseline, generates
one post-removal tick, and awaits that exact frame before asserting absence.

The timing audit also covered **all retained diagnostic summaries**, not just
failed xUnit rows. It found several corpus/scenario cases that passed their
assertions but exceeded the calibrated Fast allowance. Repairs follow the
existing [tier definitions](../testing/TIERS.md):

| Tests / fixture | Cause and disposition |
| --- | --- |
| `MapGenBrawlTests`, `MapGenGridFamilyTests`, `MapGenRadialFamilyTests` | Their lazy setup scanned all 98 cores just to choose one, charging the scan to whichever test ran first. All 20 structural methods remain Fast; each family now loads `PCore001.igc` directly and requires six usable civilizations. Native topology, ownership, population and traced-edge assertions are unchanged. All methods/theory variants passed three fresh exact-method invocations. |
| `NpcCrossClusterLiveTests.Cross_cluster_npc_paths_through_warp_and_continues_to_station_in_new_sector` | The second phase always ran 60,000 ticks even after both asserted approach thresholds were true. The minimum-distance conditions are monotone, so the loop now stops once both hold, preserving the same 60,000-tick upper bound and every assertion. It remains Fast. |
| `CorpusValidateTests.Aggregate_matches_the_recorded_baseline`, `CrossRefSurveyTests.Survey_aggregates_and_writes_report`, all `StaticCoreTests` and `MissionAssemblyTests`, `P701StationBayVisualTests.Every_station_group_texture_and_light_matches_independent_binary_oracle` | Whole-core/map/model corpus validation belongs to Live under the documented expensive-corpus rule. Every core, map, record, texture and oracle comparison is retained. Class-level corpus fixtures are classified together so cold setup does not migrate to an arbitrary remaining Fast method. |
| `P528bMinerSellRegressionLiveTests.Each_initial_miner_completes_two_full_mine_sell_cycles_at_native_rates`, `LiveP388DebugNoGameOverTests.Debug_mode_disconnect_does_not_end_mission_and_miners_keep_cycling`, `P703EconomyTests.Lost_base_empties_funded_research_before_it_can_finish_and_rebuild_requires_new_money`, `P733ResearchTests.No_bomber_tech_finale_reaches_stage_over_and_automatic_next_match_twice` | Complete economic/research/match scenarios belong to Live. Native rates, two-cycle/two-match coverage, money, tech loss, rebuild and progression assertions are retained. |
| `P694bCollisionWorkTests.Repeated_geometry_keeps_all_identities_with_direct_and_sparse_result_caches`, `P694CollisionGeometryTests.Shared_geometry_matches_uncached_narrow_phase_in_both_paths_and_after_mutation` | Dense collision pipeline and geometric-work budgets belong to Perf. Both theory sizes, all 0/1/2/4/8-thread paths, mutation checks, ordering and work-reduction assertions remain. |

Every moved or edited method was executed three times in a fresh exact-method
process, including all theory variants. No new skip or budget inflation is used.
The three map-generator fixture repairs also remove unnecessary corpus work from
the parallel Fast startup; full-corpus coverage still runs in Live.

The transport also tracked a new `Thread` before calling `Start` outside the
lock. Shutdown could snapshot it in that interval and `Join` an unstarted thread
(the previously recorded `ThreadStateException`). Registration and start now
share `_gate`. No simulation, pose gate, broadcast-scope, coalescing, capacity,
or connection-limit rule is relaxed.

The inherited proof passed its first two runs, then failed run three in
`MiningTests.Loopback_synthetic_rock_mines_and_ore_reaches_the_wire`: the received
ore fraction was 0.27917 while the server had already advanced to 0.33333.
An older in-flight nonzero observation satisfied the wait while the fixture
continued mining. The fixture now generates a single dirty-pose tick, captures
its expected cargo fraction, and awaits that exact tick without advancing the
simulation. The positive ore and fraction agreement assertions remain intact.
All three inherited runs remain diagnostic evidence; the five-run proof is
restarted on the repaired candidate.

The first continuation gate exposed
`LiveShipDeathFinalizeTests.Oxygen_dead_lifepod_with_no_friendly_station_is_finalized_server_side`:
server-side `Dead` did not imply the victim had decoded KILL_SHIP or SHIP_DELETE.
The fixture now awaits both ship-specific wire events without advancing
simulation. Killer credit, deletion reason, no-respawn, removal from every
sector/candidate list, and no-resurrection assertions remain unchanged. The
complete failed gate is retained as diagnostic evidence and the proof restarts.

A subsequent complete gate found two more fixture phase failures. The flag
secure check accepted a late held-flag join-sync as its “any GAIN_FLAG” condition
before docking. It now decodes that join-sync before resetting, waits for real
server docking, and awaits the matching secure announcement (`SideidFlag=-1`)
with time fixed. Exact-one event, flag clearing and both sides' counters remain.

The four generated warp fixtures now seat with the mission paused, await initial
assignment/status, warm the model clock outside all warp volumes, then enter a
clear real warp volume for the first transit tick. They retain the full generated
asteroid fields, topology, real warp, server/client sector agreement and native
traced-neighbor assertions. Twenty isolated passes of the old SnowFlake fixture
did not establish a repair: a later complete gate still failed both SnowFlake
and SingleRing. All earlier attempts are excluded from the final five.

Investigation then exposed an actual production omission: generated asteroid
positions ignored existing models. In a paired headless diagnostic of 500 maps
per family, the old SnowFlake pose failed transit on 4 maps and SingleRing on 3;
all seven had an obstructing asteroid, whereas every unobstructed old pose and
every deliberately clear fixture pose transited. Repeating the paired diagnostic
against the repaired generator produced zero obstructed poses and zero missed
transits across 1,000 new maps (500 per family). These diagnostics are evidence
of the cause, not substitutes for full Fast gates.

`MissionMapGenerator.RandomAsteroidPosition` now implements native
[`common.cpp:2803–2841`](/home/domingo/Development/Allegiance/src/Igc/common.cpp:2803):
floor both model radii at 550, require distance squared at least twice their
summed radii squared, and expand the sampling radius by 1.02 after each rejection.
It checks the warps, stations and already-created asteroids in that cluster, in
the native creation sequence; no asteroid counts, warp volumes or collision rules
are reduced. Native `CreateAsteroid` calls that overload at
[`common.cpp:2882–2884`](/home/domingo/Development/Allegiance/src/Igc/common.cpp:2882).
The new five-family regression failed all five rows against the old generator;
it and all affected map-generation methods receive three fresh invocations after
the repair. P676's old assertion that every asteroid stayed inside the initial
0.75-radius sampling volume contradicted native rejection expansion; it now
requires finite positions plus the complete native model-clearance contract,
including its very thin lens case. [Placement diagnostic and regression](TT1B_assets/warp-clearance.json).

The following complete gate passed Core (including the four repaired fixtures)
and failed only the MCP scenario-order check: the server had accepted/engaged
the requested ship, but `ActiveBots().OrderedTarget` was null. The fixture's
5 ms ticker advanced its 20 Hz server roughly ten times faster than the MCP's
wall-clock loop; the log also shows the native 15-second queued-order expiry.
Server placement was used without first awaiting the target's wire identity/
pose, and server engagement was used without awaiting manager adoption. This
fixture now ticks at 20 Hz, awaits the co-located target in the obeyer's world,
waits for both server engagement and manager ordered-target state, then stops
its ticker before final inspection. Exact commanded target, accepted/cleared
slots and the manager's reported target remain asserted. Both users of the
shared ticker are qualified three times, including both lifecycle theory rows.

The corrected 20 Hz pacing exposed a **real MCP production bug**: the scenario
passed only after approximately 15 simulation seconds. Both auto-accept sender
paths wrote `CmdAccepted`, which updates the accepted slot directly but does not
acknowledge the port's authoritative queued slot; that slot cleared only when
the native timeout expired. Both paths now use the existing `CmdQueued`
acknowledgement, which promotes and clears the queued command together. This is
a port protocol adaptation preserving native `AcceptQueuedCommand`'s immediate
queued-slot clearing (`WinTrek/WinTrek.cpp:10680–10697`); no server acceptance,
legality or expiry rule changes. New real TCP idle/combat regression rows both
failed with the old sender. They require actual promotion and clearance before
expiry, and the full scenario now has the same before-expiry assertion. Setup
mutations are serialized with the ticker. Never-parks is rerun five times after
this MCP change rather than reusing earlier showcase passes.

The next full gate passed ClientCore, including the new queued-accept rows,
but exposed `LivePlayerDeathRespawnTests` reading an empty observer kill history
after the owner had decoded ENTER_LIFEPOD. The three live death checks now await
all asserted kill/deletion recipients; their no-respawn assertions follow an
ordered per-peer PING echo, and the original post-death simulation remains.
Inspection found the same owner/observer assumption in the oxygen-expiry and
weapon-kill siblings; these were repaired proactively, not reported as additional
observed failures. All five methods are requalified three times. These changes
affect tests only; all production source hashes still match the five completed
parking passes.

A later complete gate exposed the hostile-station flag sibling's late held-flag
join-sync; it now uses the same initial-announcement barrier as the secure case.
The same gate exposed the dodge-field flight fixture. Its unpaced 60,000-tick
loop raced pose feedback and still used the obsolete 200/450 approach: the
failed run parked in the first sector at 116.8 units from its hop warp. This
fixture now awaits each generated authoritative pose and uses the existing MCP
`WarpApproachParams` with the real warp/hull radii. The complete Alderan rock
field, shared dodge law, both hops, minimum-distance assertion and original
tick bound remain. No production flight or collision code changes. Both methods
are qualified three times before restarting the proof.

A subsequent gate passed ClientCore but **aborted the Core test host**, rather
than failing a test assertion. `Shutdown` stopped the listener between the
accept loop's running check and `AcceptTcpClient`, which then threw an uncaught
`InvalidOperationException: Not listening`. The loop now retains its own listener
instance and handles that exception only after intentional stop or replacement;
unexpected invalid operations on the live listener remain visible. Native
shutdown wakes/joins its receive thread and closes messaging
(`FedSrv/FedSrv.CPP:333–360`); the C# TCP primitive is a port implementation
detail. A deterministic hook pauses exactly after the running check. The new
regression covers both transport modes, with and without restart before the old
thread resumes, requiring old-thread exit and a surviving new host. Existing
shutdown join bounds and all active-session rules remain unchanged. A fresh AOT
publish and five new parking checks qualify this Core change. The aborted gate's
partial TRX and process/coverage errors are retained and excluded from proof.

The following complete loaded gate exposed two more receipt boundaries:
`RipcordRequestLiveTests.RipcordKey_Initiate_Countdown_Teleport_Deny_Cancel_EndToEnd`
checked client cancellation as soon as the server countdown cleared, and
`LiveTeamLobbyToggleTests.Sides_lock_from_owner_relays_and_non_owner_refused`
checked the other peer's relay as soon as the server flag changed. The former
now awaits decoded client state under its gate with simulation fixed and
disposes its server. The latter awaits both recipients before reset and uses
the non-owner's ordered PING echo before asserting refused-unlock silence.
The lobby-lock sibling receives the same positive receipt repair proactively.
All original ripcord state, sound, transit, denial, ownership and relay checks
remain. Native cancellation emits its guaranteed sector broadcast after
clearing the model (`FedSrv/FedSrv.CPP:13329–13338`); no ripcord rule changes.

A related receipt audit repaired four additional methods proactively: both
cash-pickup cases in `LiveTreasurePickupTests`, small donation in
`LiveBucketTests`, and already-a-lifepod death in `LiveInSpaceEjectTests`.
They now await every asserted cash/deletion/respawn recipient with time fixed;
donation also drains the initial catalog before resetting histories. Cash roles
come from assigned ship IDs, since concurrent connection order is not spawn
order. Wallet deltas, exact counts, payload fields, side scope, no-second-eject,
and station relocation/docking assertions remain. The ordinary cash receipt
failure was already recorded in the P738 ledger. These are fixture repairs,
not additional failures claimed for the latest gate. All seven methods receive
three fresh exact-method qualifications. Production hashes still match the
latest AOT and five parking passes.

A further complete gate failed only
`AsteroidMinedLiveTests.No_broadcast_fires_once_miner_hits_capacity`: it sampled
14 reports at the start of the idle phase and later received 26. Capacity on
the server did not drain earlier reports from the readers. The fixture now
keeps mining paused through seating, fills to actual capacity, and uses ordered
echoes for all three recipients before sampling and after the original 150 idle
ticks. No additional reports may arrive between these boundaries. The positive
whole-mission sibling now generates its first real threshold crossing, freezes
physics, and awaits all three readers before its unchanged fraction comparison.
Both methods are qualified three times individually. The native reporting
threshold remains strictly greater than 3.0 ore change
(`Igc/asteroidigc.h:246–251`); no mining or reporting production rule changes.

The next candidate passed two complete gates, then failed its third only on
`ClientWalletLiveTests.BucketDonation_DecrementsDonorClientsMyMoney_ExactlyOnce_Live`.
All three remain diagnostic evidence and are excluded from the final five.
The fixture sampled a zero baseline as soon as the server reached Started,
before the client decoded its 18,400-credit start payday. Its tight wait for
an impossible -11 balance then advanced through further periodic paydays,
eventually observing 20,414. The fixture now awaits decoded Started and the
matching initial wallet before sampling. Request dispatch is paced at the
configured 20 Hz, then physics freezes after the authoritative server debit
while the client decodes MONEY_CHANGE. The exact once-only 11-credit delta
and 100,000-minus-donation server-wallet assertion remain. This method receives
three fresh qualifications; production money and payday rules are unchanged.

The next complete gate passed ClientCore and failed only
`PaydayTests.ZeroPlayerSide_does_not_crash_and_accrues_without_paying` at
"bot must seat" after 138 ms. Its helper pumped only to absolute simulation
time 0.5, slept 60 ms and assumed the login handshake had finished. Both wire
payday tests now wait for real assignments with the mission paused, then arm
the payday clock and sample relative 50/65/125-second boundaries. At each
sample they flush pending publications and enqueue ordered server markers;
every reader must decode its marker with simulation time fixed. These are
test markers, with no request/reply throttle or gameplay clock changes.
Early silence, exact counts, income formula, side audience and zero-player
treasury assertions remain. Both methods receive three fresh qualifications.

A receipt audit found the same 60 ms assumption in all three
`StartingMoneyStationIncomeLiveTests` methods. An incomplete candidate was
stopped and retained separately so these related cases could be repaired before
the final proof. They now use ordered server markers with time fixed for the
initial start grant, early-silence check, periodic payout and captured-station
income. Periodic-only scenarios seat and finish station setup before arming
their relative clocks. Native seeds, wallet deltas, station-income terms and
capture ownership assertions remain; explicit exact counts strengthen the marked
grant and capture boundaries. All three receive three fresh qualifications.
This is a proactive repair, not an additional observed assertion failure.

The following candidate passed three complete gates and failed its fourth only
on `BallotVotingLiveTests.Multi_mission_ballot_scopes_per_mission_and_per_side`:
the proposer had decoded its ballot, but the other voter's independent stream
was still empty. All four gates are retained as diagnostic evidence and excluded
from the final five. The test now marks the ordered publications and awaits all
four peers with time fixed before checking both positive audience and negative
mission/side scope. The draw-chain sibling had the same other-recipient
assumption and gets the same barrier. Shared setup keeps its original 40 ticks,
then awaits each intended reader before resetting histories; all eight affected
live methods receive three fresh qualifications. Native audiences, majority,
leadership, resign and game-over assertions remain unchanged.

A later complete gate failed only
`LiveCombatWireTests.Moving_target_streams_its_falling_hull_over_the_wire`,
before combat began: setup waited for the target's assignment but immediately
dereferenced the independent shooter's still-empty assignment. It now waits for
both with a 30-second failure bound before issuing controls. The two other
combat variants already waited for both. Role resolution, weapon/target setup,
600-tick limit and falling-hull assertion remain unchanged. This method receives
three fresh qualifications; no production combat or movement rule changes.

## Native references

Native paths below are relative to the configured native source tree,
`~/Development/Allegiance/src/`:

- `FedSrv/FedSrv.CPP:3844–3845,3918–3933`: two heavy plus two light slots and deviation ordering; the existing port folds the light slots into its four-entry heavy budget. Quota, fairness and target checks remain intact.
- `Igc/shipIGC.cpp:368–408`: universe-boundary damage, preserved unchanged; same-side fixture seats must be inside it.
- `Igc/shipIGC.cpp:3145–3146`: strict ±1.5-second client-pose time gate. The gate
  remains unchanged; the wall-paced integration belongs in Live.
- `FedSrv/FedSrv.CPP:5787–5800`: LOGOFF forwards the original message guaranteed
  before deleting the player. The C# queue must not discard that final reply.
- `FedSrv/FedSrv.CPP:4832`: a seated player cannot LOGONREQ again. The close path
  rejects a second command even inside an already-decoded LOGOFF packet.
- `FedSrv/fsmission.cpp:1147–1152`: side deactivation on departure is gated out of NotStarted. The P548 stage assertions are retained.
- `FedSrv/FedSrv.CPP:11793–11817`: KILL_SHIP identifies the dead ship, death credit, killer side and hostile-ship kill credit. Both peer assertions are retained.
- `FedSrv/FedSrv.CPP:12045`: dying player ships leave their cluster before respawn selection. The oxygen finalization fixture retains its removal and no-respawn checks.
- `FedSrv/FedSrv.CPP:13113–13147`: securing a carried flag at the own-side station increments its counter, clears the flag, and announces GAIN_FLAG with flag side NA. Join-sync is a separate event.
- `FedSrv/fsship.cpp:71–137`: actual warp transit changes position, velocity and cluster. The generated-map fixture drives one physical transit tick and retains the traced-neighbor check.
- `WinTrek/WinTrek.cpp:7685–7687,10664–10683`: queued-order expiration and promotion to current/accepted target. The scenario fixture preserves these production rules and uses the configured 20 Hz cadence.
- `FedSrv/FedSrv.CPP:12026–12074,4897–4911`: player respawn sends ENTER_LIFEPOD only to its owner, and PING echoes to its sender. The death fixtures preserve owner-only respawn and await independent observer receipt.
- `FedSrv/FedSrv.CPP:333–360`: termination wakes and joins the receive thread before closing messaging. The C# accept-loop fix preserves shutdown intent across the stopped-listener race.
- `FedSrv/FedSrv.CPP:13329–13338`: cancellation clears the ripcord model before sending the guaranteed sector-wide abort announcement; client decode is a separate boundary.
- `FedSrv/FedSrv.CPP:6938–6962`: lobby/sides locks require the mission owner and relay guaranteed to the mission; receipt assertions preserve both scopes and non-owner refusal.
- `FedSrv/FedSrv.CPP:13592–13611,7151–7168`: player cash credits the picker or autodonation target and announces to its side; bucket spending debits the donor and announces the negative delta to its side. Wallet and audience assertions are preserved.
- `FedSrv/FedSrv.CPP:11770–11787`: a ship already in a lifepod takes the true-death branch. The fixture still excludes a second ejection and requires deletion plus owner respawn.
- `Igc/asteroidigc.h:246–251`: ASTEROID_MINED reporting follows a strictly greater than 3.0 ore change. The existing port's whole-mission audience is retained; native itself gates mining reports per-side at `FedSrv/FedSrv.CPP:12453–12468`.
- `FedSrv/fsmission.cpp:1992–2001,2278`: game start seeds starting money and runs an initial payday. Client-wallet assertions must sample after its wire receipt.
- `FedSrv/fsmission.cpp:2253,2298–2309,2536`: payday deadline, zero-player guard, side-wide payout/remainder and income formula. The repaired wire fixtures preserve these assertions.
- `FedSrv/fsmission.cpp:2538–2546`: station income follows current side ownership. Station-income and captured-owner payout assertions are preserved.
- `FedSrv/fsmission.cpp:5881–5937,5948–5995`: player ballots use their mission's side group; accept-draw ballots use the mission group, with eligibility excluding the offering side. The wire-scope assertions remain intact.
- `FedSrv/fsmission.cpp:3943–3974`: joining-player roster context, already ported
  by `Server.Lobby.cs`; a roster PLAYERINFO is distinct from a teammate refresh.
- `FedSrv/FedSrv.CPP:13345–13368`: first ripcord activation goes sector-wide;
  re-target goes only to the requester. Both scopes remain asserted.
- `FedSrv/FedSrv.CPP:12960–12977`: carrier fallback launch pose and velocity are
  launch-time values; later motion during wire delivery is legitimate.
- `clintlib/appmsg.cpp:991–1115,2711–2736`: SET_CLUSTER, DOCKED and SHIP_STATUS are processed in receive order; old docking history does not permanently outrank a newer state signal. The lightweight Bot observer now retains that order.
- `Igc/shipIGC.h:341–370`, `Igc/probeigc.h:163–183`: ship/probe detection uses radius, target signature and line of sight. These production predicates are unchanged; repaired fixtures establish observer identity and snapshot generation time.
- `FedSrv/fsship.cpp:708–734`: loadout-change payload exports the actual loadout and child ship/turret pairs. Payload assertions remain intact.
- `Igc/shipIGC.h:2473–2491`: lifepod endurance is depleted only while clustered;
  docked/inactive returns 1.0. The fixture distinguishes generated wire ticks.

The C# TCP worker lifecycle, per-IP admission accounting and queue locks are port
implementation details, with no claimed native TCP equivalent.

## Validation

The final candidate has a whole-solution build with **0 warnings / 0 errors**
and a successful linux-x64 self-contained native-AOT publish with no IL2026 or
IL3050 warnings. The final Core/Server production changes are included in that
publish; later changes affect the Bot observer, MCP tooling and tests.

The affected cohort has **137 methods**, each invoked at least three times alone:
**432 fresh invocations / 567 passing cases**. The final never-parks gate passed
**5/5**, with invocation wall times 102.09 / 101.09 / 102.53 / 103.36 / 98.45 s.
TT-1's recorded base also passed 5/5 (case durations 100.89 / 95.42 / 95.83 /
95.59 / 101.16 s); compare case durations in the final machine-readable ledger
rather than treating process startup as flight time.

The one unfiltered Core / ClientCore / MonoGame sweep and five-run contention
proof are still running. Raw diagnostic failures and interrupted attempts are
retained separately and do not count as passing proof.

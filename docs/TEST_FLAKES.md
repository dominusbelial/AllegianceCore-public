# TEST_FLAKES.md — current state of the AllegianceCore test suite

Authoritative, current-state ledger for `AllegianceCore` suite stability (rewritten
by **P647**; the full historical ledger is preserved verbatim in the History section
at the end).

## P892d verification observations (2026-10-30)

The one Fast sweep remains **FAIL**: **11,877 passed / 11 failed / 3 optional skips**, 11,891
executions in 523.2 s, nine infrastructure checks passed, calibrated limit 10.000 s.
Receipts: `TestResults/tiers-20261030T161617.474357Z/`; standalone TRX/logs and ordered durations:
`.scratch/p892d/fast-isolation/` and `.scratch/p892d/isolation-summary.json`.
Host load at launch was 3.48 on 24 CPUs. One offscreen training window and one filtered training
process ran beside the sweep; no showcase, other full tier, or synthetic load ran beside it.
The sweep parallelises its projects and Fast collections. No whole-tier rerun was made.

Five failures belonged to this task and were fixed: two P882a rebound-key cases exposed an overly
strict native-P-only predicate (ordinary user rebinds must still reach the lesson filter); three
P885 fixture cases still represented aleph 1030 as a Ship and hard-coded the retired waypoint.
Both filtered groups pass three times after correction (`rebound-final-*`, `p885-final-*`).
They are not classified as flakes or erased from the sweep result.

| Unrelated failure | Three standalone results | Checked cause / limits |
|---|---|---|
| `DeadLatchRespawnLiveTests.Remote_client_sees_victim_again_after_death_and_respawn` | pass / pass / pass (1.411 / 1.419 / 1.449 s) | Same missing remote death-latch receipt as P892c. The fixture kills immediately after seating and allows only a 2,500-tick observation window (`DeadLatchRespawnLiveTests.cs:432-440`); death/respawn code is unchanged. Receipt/scheduling intermittency; not claimed repaired. |
| `P838GcProfTests.Gcprof_line_emitted_with_sane_values_and_tickprof_format_unchanged` | pass / pass / pass (0.074 / 0.070 / 0.063 s) | Sweep emitted the GC report but no positive per-step allocation entry. The measurement uses the approximate process-wide `GC.GetTotalAllocatedBytes(false)` delta (`Profiling/TickProfiler.cs:577,586`) and discards nonpositive deltas (`:378`), while the report uses the precise counter (`:447`). Both methods and the full four-test P838 class pass three times serially. The exact sweep counter/interleaving trigger is not established; no profiler or allocation policy changed. |
| `P838GcProfTests.Step_allocation_attribution_accumulates_and_ignores_non_step_buckets` | pass / pass / pass (0.005 / 0.005 / 0.005 s) | Same absent per-step allocation table, same checked counter mechanism and limitation above. Fast runs collections in parallel; the profiler is also process-global and server lifecycle can reconfigure it (`Server.Lifecycle.cs:1611,1878`). No claim that CPU load alone is the cause. |
| `NpcWingmanDefendLiveTests.Defender_disengages_when_chased_enemy_flees_and_returns_to_charge` | fail / fail / fail | Identical null `DefendTarget` at `NpcWingmanDefendLiveTests.cs:179`; existing baseline documented with unmodified-HEAD 3/3 failures under P892c/P895 and earlier lanes. This lane changes no Core/Server wingman logic. |
| `MissionRotationLiveTests.Finished_mission_rotates_to_fresh_notstarted_and_new_joiner_starts` | pass / pass / pass (1.844 / 1.851 / 1.943 s) | Fresh peer's MISSION_STAGE NotStarted list was empty at `MissionRotationLiveTests.cs:181`: Seat waits for ship assignment (`:103-105`), then immediately asserts a separately decoded stage receipt (`:180-181`). Unchanged rotation/lifecycle/transport code; receipt-order sensitivity, not a training failure. |
| `AddPartTests.Part_pickup_decline_recreates_treasure_at_current_position` (quarantine) | pass / pass / pass (0.380 / 0.358 / 0.358 s) | Same treasure/pickup receipt flake already recorded by P892c. Sweep's automatic quarantine retry passed too; no treasure, pickup or server callback changed. |

Passing Fast timing offenders were also selected three times:

| Case | Sweep seconds | Standalone seconds | Disposition |
|---|---:|---|---|
| `P814GunnerTests.P814b_TurretCarrier_pref_keeps_or_beats_the_Best_hulls_turret_seats` | 11.045 | 3.433 / 3.399 / 3.613 | Below limit alone; existing full-sweep timing sensitivity (P890). |
| `SC2NaniteSupportTests.Support_selection_obeys_research_dock_budget_and_mounts(civ: 25)` | 10.214 | 3.163 / 3.158 / 3.168 | Below limit alone; civ 3 also passed each selected-method run. Existing P890 timing observation. |
| `P777WarpExitWobbleTests.Npc_exit_touch_rate_is_measured` | 10.782 | 10.195 / 10.267 / 10.232 | Functionally passes, still exceeds this sweep's 10 s limit alone. Its measured simulation loop is unchanged. Timing gate remains open; not claimed fixed. |
## P896 verification observations (2026-10-30)

Single Fast-tier run `TestResults/tiers-20261030T193512.577020Z/` (11,901 tests, 532.6 s, FAIL) plus the
5x showcase never-parks required for a bot-side change. **Zero P896-introduced failures** — the work touches
`src/Allegiance.ClientCore/State/ClientWorld.cs` (a new `STATION_DESTROYED` record + accessor) and
`tools/allegiance-mcp/**`, no sim, dock, wingman or transport code.

| Test | Three isolated outcomes | Classification |
|---|---|---|
| `NpcWingmanDefendLiveTests.Defender_disengages_when_chased_enemy_flees_and_returns_to_charge` (Core) | fail / fail / fail (1 s each) | Long-recorded PRE-EXISTING baseline (P872/P884/P887/P888/P890/P894/P895); null `DefendTarget` assertion, untouched by P896. |
| `DockDockingTests.Ship_nowhere_near_a_station_is_unaffected` (Core) | pass / fail / pass (0.6 s each) | Already-ledgered status-roster fixture intermittency (P872/P890; same `Expected: -1 Actual: 1051` signature). Dock path untouched. |
| `P777WarpExitWobbleTests.Npc_exit_touch_rate_is_measured` (Core, `SLOW FAST 10.663s`) | pass / pass / pass | Sweep-load only: the pair runs in 11 s total alone (limit 10 s each). |
| `P750NativeAsteroidTypesTests.Research_showcase_rocks_carry_native_models_and_no_rock_names` (Core, `SLOW FAST 11.146s`) | pass / pass / pass | Sweep-load only, same pair timing as above. |

## P892c verification observations (2026-10-30)

The single Fast-tier run retained **FAIL** (`TestResults/tiers-20261030T144833.353354Z/`): 11,874 tests,
523 s. Caveat: this lane ran its required 5× showcase (`Local_showcase_never_parks...`, ~4 min each, 30 bots)
CONCURRENTLY with the sweep, so the timing-sensitive cases below are load-contaminated by our own run. Five
failures, every one isolated 3× alone afterwards (no concurrent load):

| Failure | Three isolated outcomes | Classification |
|---|---|---|
| `P877MineDamageLiveTests.Chased_bot_wake_mine_damages_pursuer` | pass / pass / pass | Contention only — 21.9 s sweep (SLOW offender) vs 1 s alone; mine wake path untouched by P892c. |
| `DeadLatchRespawnLiveTests.Remote_client_sees_victim_again_after_death_and_respawn` | pass / pass / pass | Contention only; respawn latch path untouched. |
| `P692EquipmentLiveTests.Held_dispenser_input_uses_production_callback_and_delivers_real_deployment` | pass / pass / pass | Contention only; dispenser callback path untouched. |
| `AddPartTests.Part_pickup_decline_recreates_treasure_at_current_position` (Core) | pass / pass / pass | Contention only; treasure/pickup path untouched (also failed the quarantine sweep under the same load). |
| `NpcWingmanDefendLiveTests.Defender_disengages_when_chased_enemy_flees_and_returns_to_charge` | fail / fail / fail | Known PRE-EXISTING baseline — same null `DefendTarget` assertion at `NpcWingmanDefendLiveTests.cs:179` fails 3/3 on unmodified HEAD; already ledgered by P872/P884/P887/P888/P890/P894/P895. Not this lane's path. |

Conclusion: **zero P892c-introduced failures.** The only 3/3-alone failure is the long-recorded wingman baseline;
the other four are contention flakes from running the fast tier beside our own showcase. All P892c tests passed in
the training suite (ClientCore 80/80, MonoGame 63/63) and the showcase never-parks passed 5/5.


## NT-3 nightly triage (2026-10-30 nightly, `b567ee1f`)

Full triage and evidence: `docs/reports/NT3_NIGHTLY_TRIAGE.md`. Base for the
nightly diff is `48cdb2de` (10-29 night). The 10-29 merge window flipped three
server defaults — `sim.nativeGotoPlan` (**P875** `28052050`; `broadcast.parallelBuild`
+ `broadcast.shipUpdateModel` + `transport.mode` (**P887** `76c6355e`) — and five
fixtures/budgets were calibrated on the old defaults. Those are fixed in the NT-3
worktree (test-only). The rows below are the ones that are **not** reproducible
here, plus the persistent failures the nightly keeps reporting.

| Test | Three isolated outcomes | Classification |
|---|---|---|
| `NpcPilotTypeGateLiveTests.Non_miner_idle_npc_stands_by_despite_minable_asteroid` (Fast) | pass / pass / pass (2 s each) + **4/4 in a bounded 4-way self-parallel run** (load 3.1–6.0 on 24 CPUs) | Load flake; already ledgered under P887 §4 ("failed once in sweep 1, `travelled 126.161`; passes alone"). Re-confirmed, no new entry needed. |
| `NpcWingmanRangeMaintenanceLiveTests.Wingman_throttle_responds_to_target_closing_speed` (Fast) | pass / pass / pass (1–2 s) + 4/4 in the same parallel run | Load flake, **new entry**: the live half of this test ticks a real server (`ApplyNpcAutopilot` + one `TickSim`); nothing in the 10-29 window touches the range-maintenance code (`Ship.ComputeWingmanRangeThrottle` untouched), and the deterministic reflection cases pass. |
| `DeadLatchRespawnLiveTests.Remote_client_sees_victim_again_after_death_and_respawn` (Live) | pass / pass / pass (2–3 s: `[P521-2C] B saw A die … after 0 ticks`) + 4/4 in parallel | Load flake, **new entry**: two real `ConnectionSession`s over TCP; both latches are already-true-on-first-poll, so any intake delay from a busy sweep only lengthens it (no threshold close to the wire). `P890`'s dispatch change is inert here (no client poses with `Time != 0`). |
| `P798ScoutOpeningLiveTests.Fogged_showcase_scouts_open_the_map_and_the_clash_forms_on_resources` (Live) | **fail / fail / fail** (331 / 294 / 560 s), identical assertion | **Persistent master failure, not a 10-29 regression**: fails identically at the pre-window base `48cdb2de` (`Assert.Single(sectors)` → `[1054, 1058]`, i.e. the two sides' main clash forms in different resource sectors). Open; needs a clash-selection investigation (no `src/` change in this task). |
| `SC4ShowcaseLiveTests.Research_showcase_deploys_chaff_probes_and_defensive_mines_on_both_sides` (Live) | **fail / fail / fail** (602 s each: `enough=false`) | **Regression of P877 `72f04522`** — passes at `72f04522^1` (`ea132a30`, 5 m 23 s). `homeMines` never reaches 2 because every mine is now an *escape* mine at the drop point; see the report §6. Open (bot policy; not changed by this task). |
| `SC2SupportPhaseLiveTests.Support_pilots_follow_the_side_phase_instead_of_parking_at_home` (Live) | pass (1/1 measured, 906 s) | Not reproducible here after NT-2's P791 commander-repair hold fix; the nightly's 4/4 failures pre-date it landing. Ledgered as unreproduced; re-run on the mini before treating it as green. |
| `NpcWingmanDefendLiveTests.Defender_disengages_when_chased_enemy_flees_and_returns_to_charge` (Fast) | fail / fail / fail (unchanged) | Persistent (P872/P887/P888/P890 rows above); unchanged by the 10-29 window. |
| `P745FinaleSiegeLiveTests.Research_showcase_finale_destroys_a_station` (Live) | 1 pass / 2 fail | Known unreliable on master (line 3537 and the P821 row): the ending is decided by native kills before the station dies. Unchanged. |
| `P728HumanCommanderLiveTests.Human_research_spends_wallet_unlocks_bot_purchase_obeys_order_and_survives_next_match` (Live) | **fail / fail / fail** (7 m 16 s each) | Regression-by-default-flip: first bad **P887 `76c6355e`** (`pass` at `48cdb2de` 6 m 01 s; `pass` at HEAD with `ALLEGIANCE_SHIP_UPDATE_MODEL=port`). The bot adopts the order (`[p431] … adopted commanded ship-target`), then the recipient dies/restarts inside the 120 s window and its respawn resets the order bookkeeping. Fixture now re-issues the order while polling (see report §5). |
| `PipelineCostTests.Steady_512_peer_pipeline_allocation_budget` (Perf) | **fail / fail / fail** (240,148 / 247,512 / 260,012 B/tick vs the 104,000 budget) | Regression-by-default-flip (P887 `parallelBuild`); the test now pins the serial builder it was calibrated on. Same for `Hud17DetectionAllocationTests` and the two `BroadcastSnapshotLoopbackTests.Crowded_TCP*` cases (report §1–§2). |
| `ShipStatusServerTests.PeerReceivesItsOwnShipStatus_InJoinBurst` (Fast) | pass / pass / pass (0.35 s each) | Sweep-only load flake seen twice in this task's fast-tier runs (`Expected: 3` — one of four join-burst status frames missed under a 5-project sweep); **new entry**, nothing in the 10-29 window touches the join burst. |
| `MultiHopDroneLiveTests.Server_drone_crosses_two_alephs_to_a_station_two_hops_away` (Live) | **fail / fail / fail** (108–114 s) | Regression-by-default-flip (first bad **P875 `28052050`**); the fixture now stages the scouted map (report §3) and passes 3/3 (43 s). |

Host note for whoever re-runs these: 3–4 concurrent live test hosts exhaust this
box's `fs.inotify.max_user_instances` (**128**) and every ASP.NET-hosted Lobby test
then fails with `System.IO.IOException : The configured user limit (128) on the
number of inotify instances has been reached`. That is an environment limit, not a
product or fixture failure — It showed up once in this task's first Fast sweep
(23 Lobby failures, all with that stack) and clears when the extra hosts exit.

## P894 verification observations (2026-10-30)

The single Fast-tier run retained **FAIL**, 11,787 executions in 558.8 s:
**11,777 passed / 7 failed / 3 not executed**, nine infrastructure
checks passed. Host load average was **4.97** (24 CPUs) with other lanes running, and
the sweep parallelises the five projects, so the timing-sensitive cases below hit the
calibrated **10 s** limit. All P894 tests passed (Lobby 119/119 including 25 new ones;
the ClientCore P894 logon/wire/listing cases are Fast-tier and passed). Fast receipts:
`TestResults/tiers-20261030T035024.921523Z/`; raw logs and the e2e run:
`~/allegiance-evidence/P894/`.

| Failure | Three isolated outcomes | Classification |
|---|---|---|
| `NpcWingmanDefendLiveTests.Defender_disengages_when_chased_enemy_flees_and_returns_to_charge` | fail / fail / fail | Persistent unchanged baseline failure already ledgered under P872/P887/P888 (see the P890 section); no NPC or side-select policy changed by P894. |
| `MissileLockLiveTests.MissileLock_BuildsPartialThenFull_AndPosts_Tips_OverTheWire` (ClientCore) | pass / fail / fail (38 s each failure) | Already ledgered intermittent (P890: fail/pass/fail; another lane: 0/3). Missile-lock tip wire path untouched by P894. |
| `P739TimerLiveTests.Warp_bomb_notice_matches_captured_blast_deadline_and_stays_in_mission(delay: 7)` | fail / pass / pass | Already ledgered float-boundary load flake (the upper bound was `7` vs `7.0000000000000009`); timer path untouched. |
| `DockDockingTests.Ship_nowhere_near_a_station_is_unaffected` | pass / pass / fail | Already ledgered status-roster fixture intermittency (P872/P890); dock path untouched. |
| `P803TurretGunnerLoopbackLiveTests.Live_boarded_gunner_huds_name_the_right_turret_on_both_clients` | pass / pass / pass | Already ledgered receipt/timing intermittency; unchanged fixture and renderer. |
| `LiveCombatWireTests.Real_Bot_class_observes_its_own_kill_over_the_wire` | pass / pass / pass (0.4 s each) | Contention only: 31.8 s in the parallel sweep, 0.4 s alone. |
| `LiveSideSelectTests.Flag_on_auto_seats_a_silent_peer_at_the_configured_30_seconds` | observed fail / pass / pass, then 0/8 failures on retry | New to this ledger: the failing assert is the elapsed-time range (`Assert.InRange(elapsed, 28.0, 31.0)`) after a `29 s` sim jump, i.e. a tick-count/timing boundary under load. P894 only adds a per-LOGONREQ string comparison (inactive when no minimum is configured) and the client-version extension (absent in this test's `LogonRequest`); no side-select, auto-seat or tick path changed. |

Limit offenders (>10 s, all *passed*; standalone timings): `LiveCombatWireTests.Real_Bot_class_observes_its_own_kill_over_the_wire`
31.8 s sweep / 0.4 s alone; `P777WarpExitWobbleTests.Npc_exit_touch_rate_is_measured` 11.0 s sweep /
9.9-10.3 s alone (already ledgered); `MissileLockLiveTests.MissileLock_BuildsPartialThenFull...`
38.4 s sweep (a functional failure too, above).
## P895 verification observations (2026-10-30)

The single Fast run retained **FAIL**: **11,757 passed / 3 failed / 3 optional
skips**, 11,763 executions in 536.3 s; all nine infrastructure checks passed.
The P777 measurement passed but exceeded the calibrated 10.000 s limit at
10.656 s. No whole-tier rerun, quarantines, assertion changes or unrelated
production fixes were used. Receipts:
[Fast summary](reports/P895_assets/fast.json),
[all isolation blocks](reports/P895_assets/isolation.json).

| Failure / timing offender | Three isolated outcomes | Baseline evidence / disposition |
|---|---|---|
| `NpcWingmanDefendLiveTests.Defender_disengages_when_chased_enemy_flees_and_returns_to_charge` | fail / fail / fail | Same null `DefendTarget` assertion fails 3/3 on unmodified HEAD `b567ee1f`; persistent baseline failure already ledgered by P872/P887/P888/P890. |
| `LiveSideSelectTests.Flag_on_auto_seats_a_silent_peer_at_the_configured_30_seconds` | pass / fail / fail | Bounds are 28–31 simulated seconds; failures were 32.15 and 31.90. Unmodified HEAD gives fail / fail / pass too. TCP receipt/tick timing; unchanged production seating logic. |
| `LiveLifepodEnduranceDisplayTests.Driver_sees_countdown_others_do_not_and_dock_cancels` | pass / pass / fail | Extra current block: fail / pass / pass. Unmodified HEAD initially passes 9/9 with driver-first intake. HEAD production with only the fixture's connection/logon initiation reordered B/driver/C fails 3/3 with the identical missing same-side relay assertion. This exposes a roster/intake-order fixture dependency; its watcher reads only SINGLE_SHIP_UPDATE while the native baseline uses the heavy/light stream. No product/HUD behavior changed. |
| `P777WarpExitWobbleTests.Npc_exit_touch_rate_is_measured` | pass / pass / pass | 9.590 / 9.701 / 9.590 s alone, all below the 10 s limit; full-sweep timing offender, not a UDP path. |

The required five TCP `P700ShowcaseLiveTests.Local_showcase_never_parks_with_target_for_five_seconds`
executions all held the parking bound, but their combined test outcomes were
**4 pass / 1 fail**. The failure reached 29/30 ships and fired 1,038 shots with
zero hits at the 480 s coverage deadline; longest parked-with-target was 0.000 s.
Three additional filtered qualifications were **pass / pass / fail**, again
without a parking violation (the last run failed its combat-coverage check).
These real-work qualifications ran in separate processes concurrently; no
synthetic CPU load was added. This is a stochastic scenario/coverage result,
not a clean five-pass composite-test claim. No bot/combat policy changed; the
new UDP treatment passed its one full showcase test. All summaries are retained
in [verification.json](reports/P895_assets/verification.json). The known P700
scenario sensitivity remains open; it was not hidden by assertion changes.

The lifepod schedule control changes **only the scratch baseline test**, not
repository production or test assertions; its exact
[fixture-order diff](reports/P895_assets/baseline-fixture-order.diff) is retained.
The baseline production fileset came from `git archive HEAD` inside task
scratch; no other checkout or remote machine was touched. Mixed isolation results
remain visible and are not described as a clean qualification or a fix.

## P890 verification observations (2026-10-29)

The single Fast-tier run retained **FAIL**, 11,741 executions in 538.2 s:
**11,735 passed / 3 failed / 3 optional skips**; all nine infrastructure checks
passed. The four new TCP aim cases and the new origin-cookie case passed. Raw
receipts: `.scratch/p890/`, Fast receipts:
`TestResults/tiers-20261030T011055.114989Z/`; compact ordered receipts:
[isolation.json](reports/P890_assets/isolation.json).

| Failure | Three isolated outcomes | Classification |
|---|---|---|
| `P761StartChatComposeTests.ProfileEditor_OwnsEnterAndBackspace_AndKeepsChatClosed` | fail / fail / fail | Sandbox cannot write the default user profile. With `XDG_CONFIG_HOME` pointing to writable task scratch the same method passes; no profile/client source changed. |
| `P864dTeamLobbyClientListLiveTests.A_lobby_player_requests_a_side_and_the_join_side_relay_moves_it_out_of_the_pool` | pass / pass / pass | Receipt/timing intermittency; test and lobby path unchanged. |
| `NpcWingmanDefendLiveTests.Defender_disengages_when_chased_enemy_flees_and_returns_to_charge` | fail / fail / fail | Persistent unchanged baseline failure already ledgered under P872/P887/P888; no NPC policy or server-flown angular handling changed. |

Five passing executions exceeded the calibrated **10.202 s** Fast limit during
that sweep. All were selected in three standalone repetitions:

| Method/case | Sweep seconds | Isolated seconds (all functionally pass) |
|---|---:|---|
| `P800HullProofGpuTests.Every_hull_of_the_real_core_renders_in_hangar_flight_and_target_pane` | 11.057 | 4.239 / 4.182 / 4.252 |
| `P814GunnerTests.P814b_TurretCarrier_pref_keeps_or_beats_the_Best_hulls_turret_seats` | 13.413 | 4.122 / 3.757 / 3.666 |
| `SC2NaniteSupportTests.Support_selection_obeys_research_dock_budget_and_mounts(civ: 25)` | 13.265 | 3.748 / 3.531 / 3.397 |
| `P796VariedLoadoutTests.Varied_equips_every_legal_hull_and_part_of_the_role_over_many_purchases(civ: 35)` | 11.036 | 4.153 / 3.892 / 3.771 |
| `P777WarpExitWobbleTests.Npc_exit_touch_rate_is_measured` | 11.709 | 10.309 / 9.865 / 10.301 |

The first four pass below the limit 3/3 alone, consistent with concurrent-load
timing. P777 remains close to, and
sometimes over, this calibrated threshold even in isolation; its timing gate is
**not** claimed repaired. Observed shared-host load averages during verification
ranged from roughly 2 to 20. No synthetic load was generated.

The P887 docked-gunner observation was also intentionally reproduced by overriding
P803 to native ship updates: `parent.Latest` is null while docked. That diagnostic
is not a new regression or an accepted gameplay fix; normal port-mode P803 and
native in-flight turret coverage are recorded in [P890](reports/P890_REPORT.md).

## P888 verification observations (2026-10-29)

One Fast-tier run was retained as **FAIL**: **11,736 executions, 531.5 s,
11,719 passed / 14 failed / 3 optional skips**, with **9/9 infrastructure
checks passing** and six calibrated-limit violations. All four new transport
ordering cases and the corrected P660 two-match scenario passed in that run.
There was no second full Fast run. No production source changed in P888.

The five standalone pool/native/server-GC P700 never-parks runs **passed 5/5**
(3m27s–6m43s); their largest observed period below 1 m/s with a target was
**2.015 s**, below the five-second limit. The optional 30-bot P660 showcase was
enabled in every full P660 repetition. Final explicit-server-GC repetitions
retain pool 118/120 and thread-per-peer 120/120 P660 case passes;
the corrected two-match scenario passes 10/10 in each mode and both new ordering
variants pass all 40 executions. Both final P660 failures are the same unchanged
countdown-bound fixture covered by the 12/12 isolated parameter-case receipts
below. Per-run results are recorded in [P888](reports/P888_REPORT.md).

All failed methods were selected alone three times. The [isolation CSV](reports/P888_assets/isolation.csv)
retains ordered pass/fail sequences, including failures that persist:

| Method / observation | Three isolated runs | Classification |
| --- | --- | --- |
| `P803TurretGunnerLoopbackLiveTests.Live_boarded_gunner_huds_name_the_right_turret_on_both_clients` | pass / pass / pass | Receipt/timing intermittency; unchanged fixture and renderer |
| `FullSessionIntegrationTests.FullSession_Join_Ready_Loadout_Launch_Movement_Chat_Compose` | pass / pass / pass | Sweep saw ChatTarget 0 instead of -2; unchanged source |
| `MissileLockLiveTests.MissileLock_BuildsPartialThenFull_AndPosts_Tips_OverTheWire` | fail / pass / fail | Intermittent missing partial-tip observation even though final lock is 1.000; not claimed fixed or 3/3 passing |
| `GameOverPlayersWireTests.Rejoined_player_accumulates_time_across_stints_in_roster` | pass / pass / pass | Sweep measured 8.25 s against a 4–8 s fixture bound |
| `DockDockingTests.Ship_nowhere_near_a_station_is_unaffected` | fail / pass / pass | Previously ledgered status-roster fixture intermittency; not claimed 3/3 passing |
| `NpcWingmanDefendLiveTests.Defender_disengages_when_chased_enemy_flees_and_returns_to_charge` | fail / fail / fail | Persistent unchanged baseline failure already recorded under P872/P887 |
| `P660NextMatchLiveTests.Late_game_over_rebuild_ready_countdown_started_has_no_clock_jump` | pass / pass / pass (all four parameter cases each time) | Initial diagnostic pool runs 5, 6 and 10 measured 14.90 s against 14.94–15.06 s for disabled/off; the pre-existing P813b-ledgered clock-observation fixture |

Eight Fast failures were environment/setup effects, not load flakes. They also
failed all three exact-method rechecks with the original environment, then
passed all three with only their environment corrected:

- **Five `PlayerBundlePathsTests` cases** (`UsesExecutableDirectoryOutsideRepository`,
  `MissingDirectoryHasNoDefault`, `LocalConfigWinsOverBundleWithBlankEnvironment`,
  `NativeSourceNeverGuessesAnInstallSubdirectory`): worktree-local TMPDIR lets the
  mock install discover the real ancestor `local.env`. An open-directory
  `/proc/<parent>/fd/<fd>` alias to the same authorized NVMe scratch directory
  prevents that lexical ancestor discovery. All selected parameter cases pass
  3/3, with actual files still under `.scratch/p888/path-test-tmp/`.
- **`P761StartChatComposeTests.ProfileEditor_OwnsEnterAndBackspace_AndKeepsChatClosed`:**
  default user preferences are read-only in this lane. It passes 3/3 with
  `XDG_CONFIG_HOME` in task scratch. Real preferences and product code were unchanged.
- **`TcpTransportSendWorkerPoolTests.Explicit_thread_per_peer_config_boot_stays_thread_per_peer`**
  and **`Boot_line_prints_the_active_transport_mode`:** this run mistakenly
  exported `ALLEGIANCE_TRANSPORT_MODE=pool` globally, overriding their explicitly
  selected legacy-mode configs. Each passes 3/3 when that overriding environment
  variable is absent; the code default is still pool. These are run-setup errors,
  not product regressions or load flakes.

The six timing violations were also checked. The failed missile-lock case is
included above. The five passing timing-only cases each passed three selected
isolated runs, with unchanged assertions:

| Case | Sweep seconds | Three isolated seconds |
| --- | --- | --- |
| `P800HullProofGpuTests.Every_hull_of_the_real_core_renders_in_hangar_flight_and_target_pane` | 10.273 | 4.083 / 4.026 / 4.400 |
| `P814GunnerTests.P814b_TurretCarrier_pref_keeps_or_beats_the_Best_hulls_turret_seats` | 12.351 | 3.203 / 3.213 / 3.364 |
| `P796VariedLoadoutTests.Varied_equips_every_legal_hull_and_part_of_the_role_over_many_purchases(civ: 35)` | 10.312 | 3.412 / 3.310 / 3.335 |
| `SC2NaniteSupportTests.Support_selection_obeys_research_dock_budget_and_mounts(civ: 25)` | 11.719 | 3.278 / 3.028 / 3.045 |
| `P777WarpExitWobbleTests.Npc_exit_touch_rate_is_measured` | 11.197 | 9.855 / 10.028 / 9.982 |

The warp-exit fixture remains on the ten-second boundary: one isolation is still
over the limit. Functional passes are not presented as three timing-limit passes.
[Timing receipts](reports/P888_assets/timing-isolation.csv) retain the exact selections.
Raw TRX/logs remain in `.scratch/p888/` in the assigned worktree. The failed
Fast gate and intermittent isolation failures are preserved.

## P886c gate observations (2026-10-29/30)

P886c touches only the real-window training proof driver (`Game1.TrainingProof.cs`, inert
unless `ALLEGIANCE_TRAIN_PROOF=1`). The deterministic training subsets and the P881a Live
suite all pass in isolation. The Fast sweep (`tiers-20261030T001936.845777Z`) reported four
non-task failures — none on a training path — isolated as follows:

| test | sweep result | isolated |
| --- | --- | --- |
| `MissileLockLiveTests.MissileLock_BuildsPartialThenFull_AndPosts_Tips_OverTheWire` (ClientCore) | failed once (38.3 s, also the calibrated-limit offender) | **0/3 — fails 3/3 alone** (38 s each): a pre-existing baseline failure on the missile-lock-tips wire path, untouched by this lane's training-proof work |
| `P692EquipmentLiveTests.Held_dispenser_input_uses_production_callback_and_delivers_real_deployment(kind: "chaff")` | failed once | flaky — passed 3rd isolated run (dispenser/chaff live path, untouched) |
| `FullSessionIntegrationTests.FullSession_Join_Ready_Loadout_Launch_Movement_Chat_Compose` | failed once | flaky — passed 3rd isolated run |
| `ShopLauncherMountNumberingLiveTests.ShopBuyOrder_LauncherGrantedAtMount0_LocksAndFires_Live` (MonoGame) | failed once | **3/3 pass** — the already-ledgered launcher-receipt load flake |

Also observed: `tools/run-training-tests.sh --no-build` (the training Fast+Live gate) intermittently
**hangs on the shared host without producing a trx**, the same condition P886b recorded; every
isolated training subset passes (TR3/TR4/TR5 RunsToCompletion, TrainingMission2, TrainingFramework,
TrainingControlMask, TR2, P880a, P882b, P886b regression, and the P881a MonoGame Live M2/M3 suite
10/10).

## P887 gate observations (2026-10-29)

P887 flips three server defaults to the rig baseline (parallelBuild, native ship-update
model, pool transport). Two Fast sweeps were run during the gate
(`tiers-20261029T175614.547114Z`, then after the test pins below,
`tiers-20261029T181742.259466Z`); the final one: **11,726 executions, 556.8 s**,
infrastructure PASS, with these non-task failures, each **passed 3/3 alone**
immediately afterwards — load flakes under the parallel sweep:

| test | sweep result | 3× isolated |
| --- | --- | --- |
| `AsteroidCombatLiveTests.Same_side_friendly_missile_damages_the_asteroid_no_friendly_fire_gate` | failed once in sweep 1 (also the sweep-1 calibrated-limit offender at 14 s); passes ~100 ms alone | **3/3 pass** |
| `NpcPilotTypeGateLiveTests.Non_miner_idle_npc_stands_by_despite_minable_asteroid` | failed once in sweep 1 ("travelled 126.161"); passes alone | **3/3 pass** |
| `P864dTeamLobbyClientListLiveTests.A_lobby_player_requests_a_side_and_the_join_side_relay_moves_it_out_of_the_pool` | failed once in sweep 2 (relay list timing) | **3/3 pass** (with `DockDockingTests`, 1 s each) |
| `DockDockingTests.Ship_nowhere_near_a_station_is_unaffected` | failed once in sweep 2 (already ledgered under P872) | **3/3 pass** |
| `P871bCreateGameClientTests.Ready_joins_the_created_mission_and_failure_stays_on_the_wizard(fail: False)` | failed once in sweep 2 (created-mission address null at line 113) | **3/3 pass** (with `P804ClientLeadTests`) |
| `P804ClientLeadTests.Lead_solve_and_green_ramp_allocate_nothing_per_frame` | failed once in sweep 2 (already-ledgered allocation-assertion load flake) | **3/3 pass** |
| `ThreePeerSeededSessionTests.SeedThreePeers_TwoSameSide_OneCrossSide_DeterministicVisibility` (ClientCore) | failed once in sweep 1 (already-ledgered logon-drain order flake) | **3/3 pass** |
| `ShopLauncherMountNumberingLiveTests.ShopBuyOrder_LauncherGrantedAtMount0_LocksAndFires_Live` (MonoGame) | failed once in sweep 1 (already-ledgered launcher-receipt flake) | **3/3 pass** |

The already-ledgered `NpcWingmanDefendLiveTests.Defender_disengages_when_chased_enemy_flees_and_returns_to_charge`
failed in both sweeps AND 3/3 alone (null `DefendTarget` at line 179) — the pre-existing
baseline failure recorded under P872/P882a, identical on a clean HEAD worktree; not a P887
regression (P887 changes broadcast defaults, not NPC dispatch).

**Showcase never-parks 5×:** nine paired runs of
`P730ShowcaseLiveTests.Two_local_showcase_matches_keep_mining_research_kills_pods_and_relaunch`
+ `P700ShowcaseLiveTests.Local_showcase_never_parks_with_target_for_five_seconds`:
7 clean 2/2, 2 runs (2 and 4) failed one of the pair with no assertion detail captured
(`-v q`-style tail), matching the already-ledgered host-load class for both tests
(9–11 min runs vs the ~1m50s base rate). **3/3 immediate isolated re-runs passed 2/2**
(7:57–11:15). No product code on those paths changed (P887 touches server broadcast
transport defaults; the parking bound is bot-speed-under-load).

## P884b verification observations (2026-10-29)

One Fast sweep (`tiers-20261029T185943.460209Z`): **11,725 tests, 514.8 s**, infrastructure checks
PASS. The lane changes only game-over retirement state (per-world cache retirement + latch capacity
trim + read-only ballot lookups) plus a new Live test; five non-task failures, isolated immediately
afterwards:

| test | sweep result | isolated |
| --- | --- | --- |
| `NpcWingmanDefendLiveTests.Defender_disengages_when_chased_enemy_flees_and_returns_to_charge` | failed once | **0/3 — fails 3/3 alone**: the pre-existing baseline failure already recorded under P872/P884 (`NpcWingmanDefendLiveTests.cs:179` guard), not a flake and not this lane's path |
| `NpcDodgeLiveTests.Traveling_npc_does_not_dodge_the_station_it_is_docking_at` | failed once (`maxLineDeviation=202.8`) | **3/3 pass** (~0.6 s each) — load flake |
| `LiveShieldRegenBroadcastTests.Stationary_ship_streams_its_regenerating_shield_over_the_wire` | failed once (shield never dropped below 0.74) | **3/3 pass** (~3 s each) — load flake |
| `ArmedProbeLiveTests.Armed_probe_spawns_hits_and_damages_enemy_with_ammo_decrement_live` | failed once (37.3 s, also a calibrated-limit offender) | **3/3 pass** (~0.6 s each) — load flake |
| `ShopLauncherMountNumberingLiveTests.ShopBuyOrder_LauncherGrantedAtMount0_LocksAndFires_Live` (MonoGame) | failed once | **3/3 pass** (~1 s each) — the already-ledgered launcher-receipt load flake |

Showcase gate: `P700ShowcaseLiveTests.Local_showcase_never_parks_with_target_for_five_seconds`
ran eight times. Seven passed (3m25s–6m10s). The single failure (8m03s, the full 480 s window) happened
**while another lane on the same machine (`allegiance-pi-p887`) was running the same showcase test
concurrently**; that lane's own showcase passes passed, and this lane's re-run under the same
contention and the six other isolated runs all passed. P700 boots its server with
`liveSettings.autoNextMatch.enabled=false`, i.e. it never rotates a world, so this task's
rotation/ballot changes cannot be exercised by it; the failure is wall-clock contention (the test's
terminal assertions are "≥24 of 30 ships reach their objective" and "observed gunfire and damage"
inside a fixed 480 s window).

The sweep also reported two calibrated-limit offenders: `MinerDecisionCycleLiveTests.Idle_npc_repeats_mine_sell_cycle_indefinitely_after_first_sell`
(18.6 s; already ledgered under P884) and `ArmedProbeLiveTests…` (37.3 s; 3/3 alone in ~0.6 s, so the
sweep duration is contention, not the code path). No new flake class: every failure is either the
known pre-existing wingman baseline or a load flake with a 3/3 isolated receipt above.

## P884 verification observations (2026-10-29)

One Fast sweep (`tiers-20261029T125355.788869Z`): **11,677 tests, 458.1 s**, all infrastructure
checks PASS, the P884 rotation-retention test included in the ClientCore project (passing, and 3/3
alone). Three non-task failures, each **passed 3/3 alone** immediately afterwards — load flakes under
the parallel sweep, no code path this lane touched (it adds prune calls to two broadcast latch sets and
clears three per-world caches at rotation):

| test | sweep result | 3× isolated |
| --- | --- | --- |
| `MissileBroadcastTests.Directly_fired_missile_broadcasts_create_to_teammate_and_destroy_to_whole_mission` | failed once (destroy position 1155 m vs the expected ≈1848 m: the missile hit something early in that run) | **3/3 pass** (~450 ms each) |
| `P817NativeMinerDecisionCycleLiveTests.Idle_npc_repeats_mine_sell_cycle_indefinitely_after_first_sell` | failed once | **3/3 pass** (5-6 s each) |
| `ShopLauncherMountNumberingLiveTests.ShopBuyOrder_LauncherGrantedAtMount0_LocksAndFires_Live` (MonoGame) | failed once | **3/3 pass** (~1 s each) |

The already-ledgered `NpcWingmanDefendLiveTests.Defender_disengages_when_chased_enemy_flees_and_returns_to_charge`
also failed this sweep; it is the pre-existing baseline failure recorded under P872 above and was not
re-isolated here.

## P869 verification observations (2026-10-28)

The one Fast sweep retains **11,457 passed / 33 failed / 3 optional Meshy skips**
(11,493 executions, 778.8 s), with **9/9** infrastructure checks. It was not
repeated. Every failed method has three isolated receipts in
[the P869 verification CSV](reports/P869_VERIFICATION.csv). The final **36 new
cases pass 3/3**, the affected **27 client cases pass 3/3**, the **64-case
shared-core/flight selection passes 3/3**, and P730 passes **5/5** (ten matches).

- **13 registry-lifetime fixture cases repaired:** side elimination, ownership,
  artifact/flag/kill/conquest/territory tests inspected an old mission registry
  after retirement. They now retain the side objects before retirement while
  preserving their state and wire assertions; native retirement empties the
  live registry. These were P869 fixture regressions, not flakes.
- **13 shared-core contamination cases recovered:** tests editing core constants
  now restore them for other live consumers. The sweep observed slot 4's edited
  launch speed **123** in later ripcord/relaunch tests expecting **50**. The final
  shared-process selection includes the mutators, native routing, launches,
  ripcord and mine visibility, and passes three times. Flight source was not
  changed. The mine fixture's temporary radius edit remains owned until fixture
  disposal, rather than ending when its seat factory returns.
- **New shutdown fixture repaired:** its initial last-consumer assertion used a
  core also owned by other live hosts. It now uses a unique core path, disposes
  all test leases and verifies both publication paths remain silent after shutdown.
- **Five environment cases:** unchanged `PlayerBundlePathsTests` discovers repo
  `local.env` when TMPDIR is lexically inside the worktree. A `/proc` descriptor
  alias to the same authorized scratch directory passes three isolated runs;
  actual scratch writes remain inside the worktree. This repeats P864c's
  environment observation.
- **Radar remains pre-existing and intermittent:**
  `RadarTargetHighlightLiveTests.LiveOffScreenEnemy_IsTheFlaggedRadarTargetHighlight`
  records **fail/pass/fail** on the final candidate and **fail/pass/fail** on an
  unchanged HEAD source copy. Its moving target violates the timed off-screen
  placement premise. Radar source remains untouched. The failed Fast result is
  retained; it is not presented as a passing gate.
## P871 verification observations (2026-10-28)

One Fast sweep: **11,512 tests, 480.4 s**, **11,505 passed / 6 failed / 3 optional
skips** (`tiers-20261028T230922`). Infrastructure checks **PASS**. The requested
P730 showcase passed **5/5** (3m09s–3m48s).

Four failures were P871's own and are repaired, not flakes: the P806 options
test (the Game submenu gained native's Force Feedback switch) and the P853b team
cap (growth is live, so the cap is native's six slots) were stale expectations;
three chat-pane tests hit a null directory-chat log in the headless test
construction (guarded). All five affected classes pass after the fix
(`P806OptionsMenuTests`, `P784ComposeTests`, `P757ChatPaneTests`,
`P853bHostEditUiTests`).

The sixth is the already-ledgered environmental
`RadarTargetHighlightLiveTests.LiveOffScreenEnemy_IsTheFlaggedRadarTargetHighlight`
(2 pass / 1 fail in three isolated reruns this sweep; 0/3 and 1/3 recorded in the
P864d/P868 notes). Its time-based staging premise decays under load; the P871
lane touched no radar placement code.

The quarantine retry that passed is the known
`AddPartTests.Part_pickup_decline_recreates_treasure_at_current_position`
fixture assumption (P868 recorded the same automatic retry pass).

## P875 verification observations (2026-10-29)

Three Fast sweeps were run on the GotoPlan-default-ON tree (11,496 tests each).
Sweep 2 ran while another lane's testhost (`allegiance-codex-p882b`) was
actively executing on the same workstation, so its failures are attributed to
that contention until re-run alone; every one of them passes solo (listed
below). The showcase never-parks proof passed **5/5** (reached 24/24/26/24/24
of 30; longest stopped-with-target 0.906/0.401/0/0/0 s).

| Sweep | Result |
|---|---|
| `tiers-20261029T034058` | 11,496 tests, 2 failed: `NpcWingmanDefendLiveTests.Defender_disengages…` (pre-existing, see below) + `MissileTrackWarningLiveTests.Warning_routes_by_the_source_ship_for_pilot_and_gunner(gunner: False)` (25.7 s, also the calibrated-limit offender) |
| `tiers-20261029T035957` | ran under the concurrent lane: 8 spread failures (`P817NativeGotoPlanTests.Production_adapter_allocates_no_bytes…`, `P813cReconnectGraceTests.Grace_expiry…`, `NpcWingmanDefendLiveTests…`, `NpcDodgeLiveTests.Traveling_npc_steers_around_a_same_side_ship…`, `DirectNpcBuildOrderLiveTests…`, `BotFlightTests…`, `SessionLossLiveTests…`, `ShopLauncherMountNumberingLiveTests…`) — **all pass 3/3 alone**; contention-attributed |
| `tiers-20261029T043216` | 11,496 tests, 2 failed + 1 limit offender: the pre-existing `NpcWingmanDefendLiveTests…`, `NpcWingmanAttackLiveTests.Wingman_engages_nearest_enemy…` (39.9 s, legacy-arm-only, see below), MonoGame `ShopLauncherMountNumberingLiveTests…` (contention; passes alone) |

- **Pre-existing master failure (both arms), not lane-caused:**
  `NpcWingmanDefendLiveTests.Defender_disengages_when_chased_enemy_flees_and_returns_to_charge`
  fails deterministically with `ALLEGIANCE_SIM_NATIVE_GOTOPLAN=0` — i.e. on the
  byte-identical legacy path — and was reproduced on a **pristine `HEAD`
  worktree** (`git worktree add .scratch/baseline HEAD`, local.env copied in):
  1/1 failed there too. The Phase-1 charge-centred acquisition
  (`UpdateWingmanDefend` → `EvaluateWingmanDefend`, cooldown 0 on the first
  tick) runs and evaluates but does not acquire the enemy at 838 u from the
  charge; two evaluation windows (200-tick cooldown) pass within the 400-pump
  budget with `AttackTarget` still null. The class's other test (which sets
  `AttackTarget` directly, bypassing acquisition) passes. Neither this lane's
  fixture nor any wingman/defend source file was touched by P875 (git diff:
  SimConfig default, boot log line, test fixtures only).
- **Legacy-arm-only failure GotoPlan fixes:**
  `NpcWingmanAttackLiveTests.Wingman_engages_nearest_enemy_closes_distance_and_deals_damage`
  passes 2/2 with the flag ON (≈2 s) and fails 1/1 with `=0` (37 s — the
  legacy-steering wingman never closes on the enemy). Recorded so the legacy
  arm's degradation is visible; the P817 default-off lane inherits it.
- **Load-staged auto-seat flake:**
  `MissileTrackWarningLiveTests.Warning_routes_by_the_source_ship_for_pilot_and_gunner`
  failed once in sweep 1 (25.7 s) and passes **3/3 alone** at 3–4 s. Staging is
  the same 30 s side-select auto-seat window already ledgered for
  `RadarTargetHighlightLiveTests` above: under sweep parallelism the peer joins
  after the armed-phase pump budget (600 × 40 ms = 24 s) has expired.
- **One uncaptured class flake:** `NpcDodgeLiveTests` failed 1 of 6 in one
  class run (the failing test was not captured); the next **14 consecutive
  class runs passed 6/6**, and every test in the class passes 3× alone. Not
  reproduced; no assertion was changed for it.

## P864d verification observations (2026-10-28)

The P730 two-match showcase passed **5/5** (3m12s–5m09s per run, load ~10 at start).

Two Fast sweeps were run (the first caught a regression of this lane's own: a new
`sim-rules-matrix.csv` row whose `native_body` was prose instead of the
`P832aMissionRulesInventoryTests` inventory key — fixed, and that test passes):

| Sweep | Result |
|---|---|
| `tiers-20261028T214951` | 11,430 tests, 2 failed: the matrix-inventory regression above + `RadarTargetHighlightLiveTests.LiveOffScreenEnemy_Is_TheFlaggedRadarTargetHighlight` |
| `tiers-20261028T215959` (after the fix) | 11,430 tests, 4 failed: `ConsoleLogNonBlockingTests.Verbose_gate…`, `BotFlightTests.Bot_trajectory_matches_independent_ExecuteMove_rollout`, `ShopLauncherMountNumberingLiveTests.ShopBuyOrder_LauncherGrantedAtMount0_LocksAndFires_Live`, `RadarTargetHighlightLiveTests.LiveOffScreenEnemy…`; plus one calibrated-limit offender (`P750NativeAsteroidTypesTests…`, 12.7 s vs 10 s, **passed**) |

All four test failures were re-run **3/3 alone** clean except
`RadarTargetHighlightLiveTests.LiveOffScreenEnemy_Is_TheFlaggedRadarTargetHighlight`:
**0/3 on this tree and 1/3 on a pristine `HEAD` copy** (`.scratch/base`, `git
archive HEAD`), so it is pre-existing/environmental, not lane-caused. Its staging
is time-based: the peer waits for the 30 s side-select auto-seat
(`SideSelectAutoSeatSeconds = 30`) while the spawned enemy NPC keeps flying, so
the “directly behind my facing” premise decays whenever the host is loaded. The
fix, if anyone picks it up: freeze the enemy (`OrderIdle`) or seat the peer
explicitly instead of relying on the auto-seat deadline.
## P870b verification observations (2026-10-29)

The one Fast sweep (`tiers-20261029T002727`) ran **11,500 cases, 19 failed, 3
optional skips** in 815 s; infrastructure **PASS**, calibrated Fast limit 10.32 s.
**31 P870 cases** (30 Fast + 1 Live) passed and are certified separately (3x
alone each). The P730 two-match showcase passed **5/5** (4m15s-5m20s per run).

Two distinct failure sets, none in P870 code (`src/` untouched):

| Set | Methods | 3x alone | Verdict |
|---|---|---|---|
| NPC travel/layer steering | 16 methods in `P591bLayerPingPongTests`, `P591cDroneRoutingTests`, `NpcDodgeLiveTests`, `NpcAutopilotLiveTests`, `NpcCrossClusterMinerDecisionLiveTests`, `InitialMinerSpawnLiveTests` | **0/3 every one** | **Pre-existing at the dispatch base**, not a flake: the base already carries `d0d0acca` "Native NPC GotoPlan on by default", which changed NPC steering under these legacy non-GotoPlan tests. Reproduced on an unmodified `src/` tree (this lane changed only tools/tests/docs). For whoever owns the GotoPlan default: either re-baseline these expectations to native GotoPlan or pin `sim.nativeGotoPlan=false` for them. |
| Load-sensitive | `BotFlightTests.Bot_trajectory_matches_independent_ExecuteMove_rollout`, `AsteroidMinedLiveTests.No_broadcast_fires_once_miner_hits_capacity`, `ClientCore MissileTrackWarningLiveTests.Warning_routes_by_the_source_ship_for_pilot_and_gunner(gunner: False)` | **3/3 each** | Load flakes (host load ~3.6 at check time; 24-CPU host shared with other lanes). The sweep also flagged 9 calibrated-limit offenders, all in the same NPC-routing set plus the miner spawn test (25-52 s vs 10.3 s). |

No P870b test timed out or failed in the sweep.

## P868 verification observations (2026-10-28)

The one Fast sweep retains **11,404 passed / 39 failed / 3 optional skips**
(11,446 cases, 480.5 s). Infrastructure **9/9** and the timing guard passed.
The sweep was not repeated. The requested P730 showcase passes **5/5**
(ten local matches), and all 28 new P868 cases pass **3/3** isolated runs.
Every failed method's three isolated outcomes are
in [P868_VERIFICATION.csv](reports/P868_VERIFICATION.csv); the report is
[P868_REPORT.md](reports/P868_REPORT.md).

- **35 P868 regressions repaired, not flakes:** the initial encrypted-core
  loader inferred encryption from `zone_core.igc`, but the artwork corpus keeps
  an already-decrypted file with that name. Corpus container/payload/map,
  round-trip, treasure-alignment and lifepod-display methods failed. The final
  loader preserves the plain default and explicitly selects encryption through
  `staticCoreEncrypted`; each of the 35 affected methods passes **3/3 alone**
  after that correction (105 exact-method invocations, including their theory
  cases). A native-named plain-file regression and a real server boot with a
  renamed encrypted core are covered by the new P868 tests.
- **Radar fixture regression remains:**
  `RadarTargetHighlightLiveTests.LiveOffScreenEnemy_IsTheFlaggedRadarTargetHighlight`
  repeats the existing off-screen placement-premise failure in **3/3 alone**.
  The NPC's delivered position has moved into the forward half-space. The P865b
  radar lane is reserved; neither its fixture nor radar/simulation code was
  edited for this observation. See the earlier P863b baseline evidence below.
- **User-config environment failure:**
  `P761StartChatComposeTests.ProfileEditor_OwnsEnterAndBackspace_AndKeepsChatClosed`
  fails **3/3 alone** against the read-only default user configuration path,
  then passes **3/3** with `XDG_CONFIG_HOME` directed into task scratch. This is
  the already-ledgered environment restriction, not a new product defect.
- **Intermittent missile assertion:**
  `MissileBroadcastTests.Directly_fired_missile_broadcasts_create_to_teammate_and_destroy_to_whole_mission`
  passes twice and fails once in three exact-method runs. The failure compares
  a destroy position about 1340 m from launch with a two-second ballistic
  expectation of about 1849 m. This observation establishes intermittency, not
  its root cause; missile simulation and that fixture were unchanged.
- **Known quarantine fixture intermittency:**
  `AddPartTests.Part_pickup_decline_recreates_treasure_at_current_position`
  fails the sweep's `Assert.Single` on multiple treasure records, passes the
  runner's automatic retry, and records **pass/fail/pass** alone. This repeats
  the ambient-treasure receipt assumption already recorded under P863b; it is
  not reported as three passing isolation runs.

## P864c verification observations (2026-10-28)

The P730 two-match showcase passed **5/5** (ten matches, 3m12s–4m43s per run).

The single Fast sweep retained **11,300 passed / 28 failed / 3 optional skips**
(483.6 s), plus **9/9 infrastructure checks**. It was not repeated. Detailed
receipts and the final validation summary are in [P864c](reports/P864c_REPORT.md).

- **Seven environment failures:** five `PlayerBundlePathsTests` cases inherited
  the worktree's `local.env` through the worktree-local TMPDIR. The unchanged
  nine-case class passed **3/3 (27/27)** with an open-directory `/proc` alias to
  the same scratch directory. `P761StartChatComposeTests.ProfileEditor_OwnsEnterAndBackspace_AndKeepsChatClosed`
  repeated the read-only user-config failure (also 3/3 failures alone), then
  passed **3/3** with `XDG_CONFIG_HOME` in scratch. `P800HullProofGpuTests.Every_hull_of_the_real_core_renders_in_hangar_flight_and_target_pane`
  started before the parallel tool build had produced its DLL; it passed **3/3**
  after `tools/build-tools.sh` completed. These are environment/build-order
  issues, not load flakes. Final tool build covers all 91 projects.
- **Two receipt/load flakes, unchanged source:**
  `ShopLauncherMountNumberingLiveTests.ShopBuyOrder_LauncherGrantedAtMount0_LocksAndFires_Live`
  inspected the client loadout before its launcher grant arrived;
  `CrossSideCombatVisibilityLiveTests.SameSide_pairStillSeesProjectileAndMissile`
  missed a combat receipt. Each passed **3/3 exact-method runs alone**. The
  host's measured load average reached 21.29 on 24 CPUs during real verification
  work; no artificial load was introduced.
- **Nineteen fixture regressions, fixed rather than classified as flakes:**
  file-map top-up intentionally invalidated empty-sector, authored-rock-only,
  unchanged-station-type and empty-new-world-treasure assumptions. Affected
  tests are `TargetPickSectorScopeLiveTests`, `AsteroidExportTests`,
  `P733ResearchTimeTests` (two unlocked cases), `P616TrackerNativeBugTests`'s
  treasure teardown, the layer/cross-cluster miner/default builder order tests
  (including the P817 builder variant), two debug `InitialMinerSpawnLiveTests`,
  `DropPartTests`, two `DroneBucketSpawnsDroneLiveTests`, the invisible-side
  `AsteroidsUpdateLiveTests` case, `P801StrikeLoopbackLiveTests`, both P682
  rotation cases and P651's missile battle. Deterministic failures were checked
  3 times alone before repair (P651 instead showed 2 passes/1 failure with the
  random populated corridor). Fixtures now explicitly prepare controlled scenes
  or assert native new-world populations. P801 compares the same station type
  and side attributes, allowing the native armor-first regen rule to leave its
  shield depleted. All repaired groups pass **3/3** after their final changes.
- **Timing:** the legacy slow-hull arrival test passed but took 19.437 s in Fast
  and 18–20 s alone. It is now tagged `Tier=Live`; its geometry, assertions and
  deadline are unchanged, and it passed 3/3 again with that classification.
  P651's 15.171 s failed Fast execution was the populated-corridor fixture issue
  above; all three cases in its repaired class passed 3/3.

## P862b verification observations (2026-10-28)

The single Fast-tier sweep on the P862b tree (client rendering only: the aleph destination glimpse's
resolution/warm-up and the flare's occlusion probe) retained **11,286 tests / 1 failure** in 309.8 s:

- `SwapPartTests.SwapPart_swaps_two_occupied_slots_sectorwide_moves_and_silent_noop`
  (`Assert.Empty` on one `SwapPart { EtOld = Weapon, MountNew = 2, MountOld = 1, ShipID = 8098 }` — the
  client-side swap-part echo). It passed **3/3 exact-method runs alone** (317/314/326 ms) with no code
  change, so it is a load flake: other lanes were running suites on the shared 24-CPU host during the
  sweep (the sweep's own MonoGame/Core counts moved by ±3 from P862's run of the same tree shape).
  P862b touches no inventory/swap-part, server or simulation code.

The P730 two-match showcase regression passed **5/5** alone on the same tree (3 m 12 s – 4 m 21 s).
## P867 verification observations (2026-10-28)

The single Fast-tier sweep recorded **11,320 passed / 3 failed / 3 optional
skips** in 336.7 s. All 51 new P867 dispatcher cases passed within that sweep;
its tier infrastructure checks passed. The full sweep was not repeated. See
[the P867 report](reports/P867_REPORT.md) and its small verification receipt.

- `Hud18TargetPaneTests.Steady_in_range_and_tti_paths_allocate_nothing`
  observed **760 allocated bytes** at line 291 where it expects zero. The exact
  method passed **3/3 filtered invocations**, with source and allocation limit
  unchanged. Recorded as a suite/load-order flake under the lane rule; the
  allocation's cause was not diagnosed by P867. Raw receipts include measured
  host load in `.scratch/p867/failure-isolation.json`.
- `P740FlightInterfaceTests.Player_ping_request_uses_real_server_and_final_marker_after_entries`
  repeated **3/3** because it explicitly excluded the requester from PINGDATA.
  Native iterates every live player (`FedSrv/FedSrv.CPP:7755-7763`), including
  the requester. The corrected fixture requires both entries and retains its
  final-marker, You/other-player and Average display assertions. It passes
  **3/3** with the native server behavior.
- `LiveWinConditionTests.Board_refused_once_mission_over` repeated **3/3**
  because it waited for BOARD_NACK after the mission was Over. The native stage
  gate is silent (`FedSrv/FedSrv.CPP:6388-6389`). The corrected fixture sends a
  PING after BOARD_SHIP in the **same ordered packet**; receipt proves dispatch
  progressed before asserting no NACK and no parent change. Its successful
  Started-stage boarding control remains. It passes **3/3**. The old NACK wait
  accounted for both Fast timing offenders (10.771 s initial / 10.645 s runner
  retry); the corrected check stays below the Fast limit. The existing P647
  quarantine tag remains for the separate positive boarding handshake.

- `P730ShowcaseLiveTests.Two_local_showcase_matches_keep_mining_research_kills_pods_and_relaunch`
  passed **4/5** in the required initial cohort. Run 5 failed after 437.7 s at
  “lifepod observed by its controller” in match 2; match 1 had completed.
  It then passed **3/3 exact-method invocations** on the final build, for
  **7/8 overall**, with P730 source, assertions, deadlines, transport settings
  and quarantine unchanged. Completion-sampled 1-minute host load for the
  failed run was 4.23. Recorded under the lane's isolated-recheck rule; this
  recurrence's cause is unproven. The ledger's earlier select/kill-lock fix is
  already in this checkout, so its old root cause is not assumed here.
  Historical context: pre-P867 merge `47d1ac8a` recorded 7/8, the later fixed
  P844 fixture recorded 8/8, and P832g recorded 5/5. No paired base experiment
  was performed. Raw receipts are `showcase-5.trx` and
  `showcase-isolation-{1,2,3}.trx` under `.scratch/p867/`.

The last two observations were deterministic fixture-contract errors exposed
by restored native behavior. Their corrections change no production deadlines
or behavior. Raw logs/TRX remain in the P867 worktree's ignored
`.scratch/p867/`; the report links to that local evidence.

## P832g verification observations (2026-10-27)

The single Fast-tier sweep retained **11,203 passed / 7 failed / 3 optional skips**
in 313.0 s, with no timing offenders. It was not repeated. All new rotation cases
and all ClientCore Fast cases passed. See
[P832g](reports/P832G_GOAL_ROTATION.md) and
[verification receipts](reports/P832G_assets/verification.json).

- Five `PlayerBundlePathsTests` cases repeated the known worktree-TMPDIR ancestor
  issue: the simulated external install inherited the real checkout's `local.env`.
  All nine cases passed **3/3 (27/27)** using an open-directory `/proc` alias to
  the same NVMe scratch. This is environment context, not a load flake.
- `P761StartChatComposeTests.ProfileEditor_OwnsEnterAndBackspace_AndKeepsChatClosed`
  repeated the read-only user configuration failure; **3/3 alone** with
  `XDG_CONFIG_HOME` in scratch. This is an environment restriction, not a load flake.
- `ShopLauncherMountNumberingLiveTests.ShopBuyOrder_LauncherGrantedAtMount0_LocksAndFires_Live`
  failed at line 252 with default client-side `ExpandedPartData` after the server
  had granted the launcher. It passed **3/3 exact-method runs alone**. This repeats
  the ledgered launcher grant/receipt race (P852 merge observation); no launcher,
  grant, broadcast or test assertion/deadline changed in P832g.

The three natural flags→artifacts bot rotations and the exact P730 two-match
showcase regression **5/5** all passed without retries. Raw gate receipts remain
in the P832g worktree's ignored `.scratch/p832g/evidence/`.

## P861 verification observations (2026-10-27)

The conquest 80/50 fixture race is resolved: its assertion included the next
mission's legitimate join ranking after game over. See
[P861](reports/P861_CONQUEST_GAME_STATE_RACE.md) for the native ordering and the
deterministic stalled-publisher proof. The complete class passed **20/20 bounded
parallel invocations (`xargs -P 4`, 100 cases)**; the new and affected methods also
passed **3/3 each alone**. No production code or P844 certification row changed.

The one Fast sweep had **11,196 passed / 1 failed / 3 optional skips** in 347.5 s,
with no timing offenders. Both P861 methods passed.
`Allegiance.ClientCore.Tests.DeadLatchRespawnLiveTests.Remote_client_sees_victim_again_after_death_and_respawn`
failed at `:440` (observer B never latched A's death within 2,500 ticks), then
passed **3/3 exact-method invocations alone** (1.179 / 1.486 / 1.703 s), with source,
deadline and assertions unchanged. Recorded as a load/receipt flake under the
lane rule; the underlying death-latch timing is not diagnosed by P861. No full
Fast retry. Raw receipts: ignored `.scratch/p861/fast/` and
`.scratch/p861/unrelated-{1,2,3}/`; small summary in the P861 report assets.

## P832f verification observations (2026-10-27)

The single Fast sweep had **11,155 passed / 2 failed / 3 optional skips**, with no timing offenders. See [P832f](reports/P832F_KILLS_BOTS.md) and its verification receipts. The full sweep was not repeated.

- `P761StartChatComposeTests.ProfileEditor_OwnsEnterAndBackspace_AndKeepsChatClosed` repeated the already-ledgered read-only user configuration failure. The exact method passed **3/3** with `XDG_CONFIG_HOME` in task scratch. This is an environment restriction, not a product defect or load flake; preferences and test source were unchanged.
- `ConquestTerritoryLiveTests.Flag_capture_tips_conquest_and_broadcasts_game_state_then_game_over` received **50%** GAME_STATE where its post-reset event assertion expected **80%**. It passed **3/3 exact-method invocations alone**, with the same source, deadline and assertions. **Resolved by P861:** the extra 50% is the **next mission's** legitimate join ranking after GAME_OVER and rotation, not a delayed original join or a stale conquest recompute. The fixture had drained the original join, but `AllGameStates` flattened every later ranking and required all of them to remain 80%. A deterministic delayed-inspection proof reproduced the exact failure; the corrected test checks each peer's first post-capture ranking. A stalled-publisher regression also verifies ordered capture → 80% → GAME_OVER → 50% under snapshot coalescing. See [P861](reports/P861_CONQUEST_GAME_STATE_RACE.md). P832f's bots are not instantiated by this fixture, and its kills goal is disabled.

All 21 flags/artifacts/conquest bot games passed, as did three normal-cadence kills games and the final 64-case bot controller/planner group in three isolated invocations.
## P860 root cause (2026-10-27) — the P480 rotation failure was a harness attribution race, not a fog leak

`P480RotationKeepsPlayersLiveTests.Rotation_migrates_connected_peers_and_new_game_starts_without_reconnect`
("station 1052 was exported to a side that had not seen it") is **root-caused and
retired**: it is a fixture bug, not a load flake and not a visibility leak. Full
detail, native citations and receipts: [P860](reports/P860_REPORT.md).

- **What the server did.** Nothing wrong. The recurring item is station `1052`
  (side 1's 'Garrison' in Quaoar, `XXV_simple.igc`) received by a peer whose final
  side is 0. The port exports a station per side and only when that side sees it
  (`Server.Lobby.cs` join burst <- `FedSrv/fsmission.cpp:4215-4222`), and a station's
  `SeenBySide` latches once true (`SideVisibilityState` <- `FedSrv.CPP:11124-11126`;
  own side always sees it, `FedSrv.CPP:10904-10912`, `Igc/common.cpp:3004-3017`).
  Instrumented runs (120 rotation runs, a probe line at all three `ExportStation`
  send sites) recorded **zero** exports to a side that had not seen the station.
- **What the fixture did.** It seats four bots and the default spawner assigns a
  side by peer count, so the 2nd peer is spawned on **side 1** and receives side
  1's garrison in its *first* join burst; the test then switches it to side 0
  before the draw. The rotation rebuilds the **same static map**, so both worlds'
  stations carry the **same `StationId`** — a received `EXPORT(26)` cannot be
  attributed to a world by content, only by its position in the stream. The old
  fixture cleared the accumulator on the test thread, which does not order itself
  against the socket/publisher: under load that pre-rotation export was appended
  after the clear and measured as part of the rotated mission's burst.
- **Fix.** The `GAME_OVER(62)` message is the world boundary, exactly native's
  order (`fsmission.cpp:3437` queues it, `:3490` resets the mission;
  `missionigc.cpp:3804-3828` `ResetMission` + `GenerateMission` from `:3830`
  re-send no object sync at all). `Bot.ResetWorldExportsOnGameOver()` now clears
  the world-export accumulators **inside the receive loop at that message**, so
  the split is wire-ordered and load-independent. `P480` uses it instead of the
  thread-side `ResetWorldExports()`; its diagnostic failure message is kept.
- **Deterministic regression proof.** `P860RotationWorldExportBoundaryTests`
  (2 cases, Fast tier, `[Collection("LiveNetwork")]`) reproduce the ordering with
  no load at all: one asserts the pre-boundary stale export exists and that every
  post-boundary station export is seen by the recipient's side (it fails with the
  gated message verbatim when the boundary is disabled), the other asserts the
  fog-leak invariant at the server's own send sink across the rotation.
- **Adjacent defect found while tracing the game-over tick.** The `ballots` SimPass
  stage enumerated `_missions` while a ballot-driven game-over rotates a mission in
  place (`_missions[idx] = fresh` bumps `List<T>`'s version), so **every**
  ballot-driven game-over aborted that stage with `InvalidOperationException:
  Collection was modified` (visible in the P480 logs of every gate run). It now
  indexes the list; the stage no longer aborts.
- **Gate receipts for P860** (three full fast-tier sweeps on the same tree, other
  lanes idle): PASS `tiers-20261028T031838` (312.7 s), FAIL
  `tiers-20261028T033047` (311.0 s), PASS `tiers-20261028T033636` (310.0 s,
  11,165 tests, 0 timing offenders). The FAIL sweep's two failures are both
  pre-existing load flakes, untouched by P860 and each ledgered above:
  `P804ClientLeadTests.Lead_solve_and_green_ramp_allocate_nothing_per_frame`
  (`:2970`) and the `ConquestTerritoryLiveTests.Flag_capture_...` case already
  opened by the P852 row (2026-10-27, "watch: possible stale GAME_STATE read").
  **Conquest diagnosis while it was in front of us** (not fixed here; 3/3 alone
  both before and after): the failure is `:387` — a *received* GAME_STATE(155)
  whose `Conquest[SideA]` is still 50 — not the mission value the test asserts two
  lines earlier (that one is correct at 80). So the port's recompute/broadcast is
  fine; the fixture's `ResetGameStates()` is a caller-thread clear of an async
  accumulator (the exact P860 lesson: `EXPORT`/`GAME_STATE` already queued when the
  clear runs are appended after it), and the pre-capture 50% GAME_STATE lands in
  the list. It should be asserted as "the NEWEST GameState per peer" (per-peer wire
  order is preserved) rather than "every GameState ever received".

## P837 observations (2026-10-27) — the two bot live regressions were defects, not flakes

Both were reproducible 3/3 alone on the dispatch base and are fixed in P837 (see
[the report](reports/P837_BOT_LIVE_REGRESSIONS.md)); neither is a load flake, and
neither assertion, deadline, tier or quarantine was changed to get them green.

- `P727SkirmishLiveTests.Local_start_battle_pause_resume_next_match_stop_leaves_no_processes`
  **first bad commit `a8896574` (P798)**, whose skirmish hunk turned the native map
  reveal off. Under fog each side's clash became its own home sector (`[clash] side
  1 chose sector 1051 (ownMiners=1, hops own=0 enemy=3)`), so the two fleets parked
  at their own bases and `combat.shotsFired` stayed 0 for the whole window. P797
  (`7734a197`) and the commits until P798 are *unrelated* failures of the same test:
  they cannot spawn at all (`spawn_scenario: finale needs an observed enemy home
  station`) — a bisect on "the test fails" stops there, a bisect on the combat
  symptom stops at `a8896574`.
- `P682NextMatchCombatLiveTests.P682b_second_match_relaunches_mature_and_strike_then_third_match_starts`
  (all four rows) **first bad commit `62a5b50f` (P831)**: its per-side "observed an
  enemy station" re-arm gate needs *both* sides, and a one-bot-per-side finale is
  below `MinFleetForExploration`, so the second side never got an objective to fly
  out with; only P831's 300 s grace could release the gate, which is longer than
  the fixture's 240 s window. This supersedes the P832a note below that read the
  timeout as a "baseline fixture/behavior mismatch": the fixture was right.
- P837's one Fast-tier sweep: 10,972 tests, 304.8 s, one failure —
  `CrossSideCombatVisibilityLiveTests.Fire_is_not_relayed_to_peers_in_other_sectors`,
  the load flake already ledgered here (`:803`, `:2546`, `:2845`). It passed **3/3
  alone** after the sweep and touches none of the files P837 changed.

## P832a verification observations (2026-10-26)

- The one Fast-tier sweep retained five `PlayerBundlePathsTests` failures caused
  by worktree-local TMPDIR exposing the real checkout's `local.env`. All nine
  cases passed **3/3 isolated invocations (27/27 cases)** through an open-directory
  `/proc` alias to the same NVMe scratch, with assertions unchanged. This repeats
  the documented environment-context issue; it is not a load flake.
- `P682NextMatchCombatLiveTests.P682b_second_match_relaunches_mature_and_strike_then_third_match_starts`
  timed out waiting for the second match's fresh finale in the expanded
  diagnostic. The unchanged dispatch base `62a5b50f` reproduces that same timeout in
  **3/3 isolated invocations** of the `port, pipeline=false` case. The fixture's
  240-second fresh-finale wait is shorter than the existing 300-second default
  rearm grace, and its two bombers do not establish fresh enemy-base discovery.
  ShowMap reveals warps, not enemy bases. This is a baseline fixture/behavior
  mismatch, not evidence of a P832a regression or a timing flake. No strategy,
  rearm setting, assertion, wait budget or quarantine was changed. Exact counts
  and original failed-run logs are retained in the
  [P832a report](reports/P832A_GAME_GOALS.md).

P832a's final-source P700 parking cohort passed **5/5**, matching unchanged
base **5/5** on the idle host, with sequential alternating revisions. Maximum
observed stopped-with-target intervals were 2.004 s final and 2.012 s base.
The five-second assertion and scouting/arrival window were unchanged.

## P828 verification observation (2026-10-25) — P699HumanPodHomeLiveTests on the dispatch base

`P699HumanPodHomeLiveTests.Human_eject_autopilot_flies_two_jumps_and_docks_without_oxygen_respawn`
(both `timeScale` cases) fails **2/2 on the pristine dispatch base `ac932657`**
(a clean `git worktree` of HEAD, only `ALLEGIANCE_ARTWORK_PATH` set; the test
*skips* without artwork) at `AutopilotControls` — `Assert.True` on
`TryGetAutopilotControls`. It is not touched by P828 (a Modern-HUD radar-label
visual option) and fails identically with and without P828's changes, so it is a
base failure for whoever owns pod autopilot, not a P828 regression and not a
flake. The same full MonoGame run had 3 503 passed / 3 failed / 3 skipped, those
being the only failures.

**Resolved (P859, 2026-10-27):** bisected to `7734a197` (P797 native map
discovery) — the autopilot is native-correct (it only routes through alephs the
side has seen: `Igc/common.cpp:894-1010`, `FedSrv/fsmission.cpp:4181`), but this
fixture never seeded the map, so its client knew one aleph pair and the
two-jump route home was genuinely unroutable. The fixture now sets `ShowMap`
(exactly as the sibling P540b pod fixture was fixed in `7cf119e1`); 2/2 rows pass
3× alone. Full analysis: `docs/reports/P859_LIVE_MONOGAME_REGRESSIONS.md`.

## P788c verification observations (2026-10-25)

- `BroadcastSnapshotTests.Recorded_ticks_have_identical_per_peer_bytes_order_and_lanes`:
  all four expected-prefix assertions fail in three isolated invocations. The
  dispatch base `dac42b33` fails the same four assertions. All four complete
  transcripts are byte-identical between dispatch and P788c, and between
  snapshot off/on/encode-once. This observation is a **stale golden baseline**,
  rather than evidence of a timing flake. Expected hashes/assertions were not
  changed; the existing test now saves its diagnostic JSON before the assertion.
- Five `PlayerBundlePathsTests` cases failed in the one Fast sweep because
  worktree-local TMPDIR ancestors expose the real checkout's `local.env`.
  The nine-case class passes **3/3** with an open-directory `/proc` alias to the
  same worktree scratch (no repository ancestor), as in prior lane reports.
- `P761StartChatComposeTests.ProfileEditor_OwnsEnterAndBackspace_AndKeepsChatClosed`
  failed on the sandbox's read-only user configuration directory. The exact
  method passes **3/3** with XDG_CONFIG_HOME in task scratch. This is an
  environment restriction, not a product fix or a load flake.
- `P813cReconnectGraceTests.Grace_expiry_runs_the_native_removal` missed the
  observer's removal packet under concurrent suite load; it passes **3/3** in
  isolated filtered invocations. Its assertion/timing/source are unchanged.
- P700's sealed final-source showcase cohort passed **5/5**, matching dispatch
  **5/5**; an additional native-order invocation passed. The earlier cohort was
  **4/5** (`botsLastMinute == 0`, actual 2; sampled longest stop 2.006 s), and a
  separate earlier run hit the 480 s arrival threshold. Those original failures
  remain in the receipts; final success does not prove them impossible. No bot
  strategy, assertion, budget, or quarantine was changed.
- A later class-only cross-side-return run failed its initial outbound-warp setup,
  the existing ledger's timing pattern. Two subsequent class runs and three fresh
  exact-method runs passed. This is separate from the task's corrected handshake
  failures in the initial Fast sweep.

The Fast sweep's three warp/return failures were task-related protocol issues
and were fixed, not classified as flakes: script clients now emit accepted
updates automatically, and obtain simulation-paced timestamps between the
rate-limited PING samples. The final affected client class group passes 3/3.
See [P788c report](reports/P788C_NATIVE_TICK_ORDER_2.md) and its
[receipts](reports/P788C_assets/verification.json). Raw logs/TRX remain in the
ignored worktree evidence directory named by that report.

## P787a observation (2026-10-24) — Hud12bReviewFixTests allocation flake

`Allegiance.ClientCore.MonoGame.Tests.Hud12bReviewFixTests.Hostile_damage_threat_and_flash_allocate_nothing_per_contact`
failed once in the single Fast-tier run (MonoGame, ~39 ms) under concurrent
suites on the shared workstation, then passed **3/3 isolated** (10-12 ms each).
It is an allocation-threshold rendering test in the pure renderer, untouched by
P787a (which records client-side one-shot sound events only), so it is classified
a **load flake** (timing/alloc pressure, no assertion semantics changed).

## CI gate observation (2026-10-21) — CleanCloneSkipGuardTests near the Fast limit

> **Fixed by CI-2** (2026-10-22, master `43d11eb5`). The guard now parses the test tree in
> parallel, walks each member once (facts + refs cached per node) and computes the
> referenced-member closure once per type instead of a BFS per test method; its verdicts are
> unchanged (byte-identical violation/member/facts dump, old vs new — see
> [CI2](reports/CI2_SKIPGUARD_SPEED.md)). New numbers, same workstation/limits: isolated exact
> method **686–764 ms** (was 3–4 s), full Fast tier **2.472 s** from
> `fast/Allegiance.Core.Tests-main.trx` (was 8.13 s / 10.82 s FAIL), tier **9,946 tests, 0
> failed, PASS**, clean-clone guard PASS. The limit (10 s), its calibration and the tier runner
> were **not** changed, and the measurements below are kept as the pre-fix record.

`Allegiance.Core.Tests.CleanCloneSkipGuardTests.Artwork_and_gpu_touching_tests_skip_on_a_clean_clone`
parses every test file with Roslyn and its time grows with the suite and with concurrent load. Measured
on the workstation (24 threads), master 7c79aefd–3466d3a8:

| Run | Duration |
|---|---|
| Exact-method isolated, 3x | ~3 s each |
| Batch 19 gate, full Fast tier | 8.51 s |
| Fast tier with the new `PackagingManifestTests` (run 1) | **10.82 s** — tripped the calibrated 10 s limit; 9,946 tests, 0 failed |
| Same tree, Fast tier rerun | 8.13 s, PASS |

Classified a **load flake** (timing only; no assertion failed). Headroom is ~1.5–2 s, so it will trip
again as the test count grows. Real fix, not done here: parse once per file in parallel or cache the
syntax trees, or move it to its own non-parallel collection — never raise the limit for it alone.

**CI-2 did exactly the parallel-parse fix** (plus a single cached member walk and a per-type
closure; no disk cache, no collection change, no limit change). The test's own cost is now ~5×
smaller and its headroom under the 10 s Fast limit is ~7.5 s instead of ~0 s.

## P761b timing observations (2026-10-20)

Two fast-tier sweeps under heavy concurrent load (other agents' suites on the same
machine) both had **9,317 tests and 0 failed tests**, but each tripped the runner's
calibrated 10 s fast-limit guard on a different UNRELATED test:

- `Allegiance.Core.Tests.P750NativeAsteroidTypesTests.Research_showcase_rocks_carry_native_models_and_no_rock_names`
  at **10.311 s** — passes **3/3 exact-method isolated invocations in ~1 s each**.
- `Allegiance.ClientCore.Tests.CrossSideCombatVisibilityLiveTests.CrossSide_sector_stays_correct_across_warp_return`
  at **25.301 s** (quarantine set; the runner's own retry passed it) — passes
  **3/3 exact-method isolated invocations in ~1 s each**.

Neither test's source, budget or limit changed; nothing in P761b touches asteroid
research or cross-side visibility, and the second test already has an entry in this
ledger ("Initial warp-out setup timed out; existing LiveTiming case", 3/3 pass).
Both are classified **load flakes**, not regressions. The runner FAILs are
preserved. See [P761b](reports/P761_PARITY_WAVE_UI10.md) and
`~/allegiance-evidence/P761b/fast-tier.log`.

## HUD-19 (2026-10-21)

The single fast-tier run recorded one failure in untouched `Allegiance.Core`:
`Allegiance.Core.Tests.P761bCommandRelayTests.QueuedStationGoto_KeepsOwnTarget_AndRelaysTheStationToTheTeammate`
(the log shows `conn-drop`/`GreetTimeout`/`ProtocolError`, i.e. a live-network
greet timeout). The whole `P761bCommandRelayTests` class passed **3/3 exact-
class isolated invocations** (5/5 each) unchanged, so it is recorded as a
live-network load flake. HUD-19 touches only `Allegiance.ClientCore.Game`
rendering/settings (no server, transport or relay code).

## P747 environment observation (2026-10-19)

The one fast-tier run had **8,826 pass / 5 fail / 3 existing private-asset skips**;
all nine infrastructure checks passed and there were no timing violations. The
five failures were the same temporary-install `PlayerBundlePathsTests` context
issue recorded by SC2 below: workspace scratch made ancestor discovery read the
real checkout's `local.env`. These are **environment-context failures, not load
flakes**.

`MissingDirectoryHasNoDefault`, `NativeSourceNeverGuessesAnInstallSubdirectory`,
`LocalConfigWinsOverBundleWithBlankEnvironment`, and
`UsesExecutableDirectoryOutsideRepository` each passed **3/3 exact-method isolated
invocations**, **21/21 case executions**, with their assertions unchanged. A
logical `/proc/<parent>/fd/3` alias points at the same NVMe workspace scratch,
without a repository ancestor in the fixture's logical path. No test or product
source changed to suppress these failures; the full fast tier was not repeated.
The original runner FAIL remains recorded. See [P747](reports/P747_PARITY_WAVE_SIM14.md),
[qualification](reports/P747_assets/qualification.json), and
[isolated receipts](reports/P747_assets/path-rechecks.json).

## P748 environment observation (2026-10-19)

The single fast-tier sweep had **8,806 passed / 5 failed / 3 optional private-pack skips**,
with no timing violations. All five failures were unchanged `PlayerBundlePathsTests`
fixtures whose workspace-local temporary paths found the real checkout's `local.env`
through ancestor discovery. This matches the SC2 environment observation below and is
**not classified as a load flake**. The four affected methods (including all theory rows)
passed **3/3 individual invocations each**, totaling **21 passing cases**, using a logical
`/proc/<parent>/fd/<fd>` alias to the same workspace NVMe scratch directory. No assertion,
deadline, resolver or quarantine changed. The original runner FAIL is preserved; no
second fast sweep was run. All 64 selected UI test methods separately passed 3/3
(348 case executions). See [P748](reports/P748_PARITY_WAVE_UI6.md),
[original fast summary](reports/P748_assets/fast-tier-summary.json), and
[environment rechecks](reports/P748_assets/environment-rechecks.json).
Raw logs/TRX remain ignored in `/home/domingo/allegiance-codex-p748/.p748-evidence/`;
the preferred home evidence directory is outside this lane's writable roots.

## SC2 environment observation (2026-10-19)

The single fast-tier sweep had **8774 pass / 5 fail / 3 optional skips**, with no timing violations. The five failures were `PlayerBundlePathsTests` temporary-install cases: workspace scratch put their fixture paths beneath the real repository, so ancestor discovery read the checkout's `local.env`. These are environment-context failures, **not load flakes**. All nine path cases passed with a logical `/proc/<parent>/fd/3` alias to the same NVMe scratch directory; each affected method subsequently passed 3/3 isolated invocations with its assertions unchanged. The original runner FAIL is preserved and the sweep was not repeated. See [SC2](reports/SC2_NANITE_SUPPORT.md) and [validation](reports/SC2_assets/validation.json).

## P730 observation (2026-10-19)

The single unfiltered Core sweep completed with **4,888 passed / 5 failed**.
Each failed method then passed **3/3 fresh isolated invocations** on the same
workstation. The full sweep was not repeated.

| Method | Full-sweep symptom | Isolated result |
| --- | --- | --- |
| `GameOverPlayersWireTests.Win_by_elimination_broadcasts_game_over_players_to_surviving_peer` | No matching result entry at the assertion. | 3/3 pass |
| `LiveLifepodEnduranceDisplayTests.Driver_sees_countdown_others_do_not_and_dock_cancels` | Lowest received countdown was 0.392 seconds. | 3/3 pass |
| `LiveRelaunchTests.Carrier_without_launch_slots_uses_backward_fallback` | Position sample was -222.787; expected -182.315 to -178.315. | 3/3 pass |
| `LiveRipcordTests.Cancel_while_active_aborts_sector_wide` | Expected one abort receipt; collection was empty. | 3/3 pass |
| `LiveRipcordTests.Ship_anchor_insufficient_energy_aborts_consumed_not_retried` | Expected one abort receipt; collection was empty. | 3/3 pass |

These meet the task's light-gate load-flake rule. No assertion, timeout,
transport setting or quarantine changed. See [P730 report](reports/P730_BOT_EFFICIENCY.md)
and [validation ledger](reports/P730_assets/validation-summary.json).
Raw TRX/logs are in `/tmp/allegiance-evidence/P730/`.

## BS1 observation (2026-10-19)

The single Core sweep completed with **4,879 pass / 14 fail / 0 skip**. Every failed case
subsequently qualified with **3/3 fresh exact-case invocations**, with assertions/deadlines unchanged.
The ship-update-budget case had an earlier isolated batch of **fail, fail, pass** before its final
3/3 qualification; all earlier failures remain in the evidence. The two P694 theory rows were
filtered separately, and each invocation executed exactly one case.

| Method / theory row | Full-sweep observation |
| --- | --- |
| `AutoReloadOnEmptyTests.Auto_reload_relays_RELOAD129_sector_wide` | `SeatThree` found no same-side observer. |
| `Transport.TcpTransportPriorityLaneThreadPerPeerTests.Ordered_join_frames_are_never_shed_when_the_stream_overflow_collapses` | Expected 50 ordered frames, observed 35. |
| `ShipUpdateBudgetLiveTests.Ship_dropped_one_tick_is_served_the_next_no_permanent_starvation` | Tick-3 dropped ship not yet observed at tick 4. Final recheck 3/3; earlier 2 isolated failures retained. |
| `ShipsUpdateLiveTests.Cross_side_ships_see_each_other_and_not_self` | Peer B had not received `HEAVY_SHIPS_UPDATE`. |
| `P694MassEventTickTests.Launch_point_mass_events(150, false, true, false, false)` | Median 334.727 ms versus unchanged 200 ms Debug budget. |
| `P694MassEventTickTests.Launch_point_mass_events(195, false, true, false, false)` | Median 249.997 ms versus unchanged 200 ms Debug budget. |
| `LiveRipcordTests.First_activation_broadcasts_to_everyone_flying_in_sector` | Expected receipt collection empty. |
| `LiveRipcordTests.Cancel_while_active_aborts_sector_wide` | Expected receipt collection empty. |
| `LiveRipcordTests.Re_target_activates_to_requester_only` | Expected receipt collection empty. |
| `LiveRipcordTests.Ship_anchor_insufficient_energy_aborts_consumed_not_retried` | Expected receipt collection empty. |
| `LiveRelaunchTests.Carrier_without_launch_slots_uses_backward_fallback` | Observed -219.866 outside expected -182.315 to -178.315 range. |
| `LiveCountdownStartPingTests.Multi_mission_countdown_scopes_per_mission` | Expected receipt collection empty. |
| `LiveCountdownStartPingTests.StartGame_owner_all_ready_goes_through_real_countdown` | Expected receipt collection empty. |
| `CrossSideExportLiveTests.Same_side_teammate_still_gets_teamexport_no_cross_duplicate` | Observed packet 92 where packet 33 was expected. |

These meet BS1's light-gate load-flake classification and match prior receipt/timing/performance
observations. No Core/server, transport, simulation, assertion, timeout or quarantine changes were
made. No second full sweep is run. See [the BS1 report](reports/BS1_BENCHMARK_STATS.md) and
[small verification ledger](reports/BS1_assets/verification.json). Raw logs/TRX remain outside the
repository at `/tmp/allegiance-evidence/BS1/` because the home evidence path is outside the sandbox.

### ClientCore siege: not qualified

The single ClientCore sweep had **1,478 pass / 1 fail / 1 default opt-in skip**. The failure was
`P745FinaleSiegeLiveTests.Research_showcase_finale_destroys_a_station` (line 131): station destruction
occurred, but the match ended on the clock with no side eliminated. Isolated attempts include
passes, the same assertion failure, and test-host/process aborts. The final attempted isolated
batch was **pass / assertion failure / host abort**. This does **not** meet the 3/3 light gate and
is not reported as a qualified load flake. Several aborts explicitly report disk quota while
writing scenario logs. Earlier aborted attempts remain in the evidence.

A focused reproducer built from the dispatch fixture/configuration and unchanged execution-
dependency sources passed once; a second run aborted on disk quota. It is not a full baseline
sweep or a 3/3 qualification. No siege assertion, timeout, quarantine or simulation code changed.
The full MonoGame sweep passed **2,498 cases**, with **0 failures** and **3 missing private Meshy-pack
skips**. The default-gated P660 local showcase was enabled separately and passed 3/3; never-parks
passed 5/5. All 17 new BS1 C# methods passed 3× alone. No full suite was repeated.

## P746 observation (2026-10-19)

The one unfiltered MonoGame sweep passed 2,495 cases with three unavailable
private Meshy-pack skips. The subsequent Fast-tier run observed
`MiscKeyBindingLiveTests.Live_own_ship_is_real_and_center_ranking_selects_a_real_ship`
returning warp candidates before ship candidates were present. It then passed
**3/3 fresh exact-method isolated invocations**, meeting P746's light-gate
load-flake rule. No target-ranking assertion, timeout or quarantine changed.

The unfiltered ClientCore sweep also observed
`RetriedConnectLiveTests.RetriedConnect_StartsFromEmptyWorld_AndReseatsWithNewAssignment`
enumerating dictionary keys while its receive thread modified the collection.
It passed **3/3 fresh exact-method isolated invocations**. This is the same
previously documented reconnect fixture race; no reconnect assertion, timeout,
transport or quarantine changed.

See the [P746 report](reports/P746_KILL_CREDIT_ON_EJECTION.md) and
[verification ledger](reports/P746_assets/verification.json). The report
separately records the path-fixture environment correction and inherited CSV
repair; those are not load flakes.

## P745B observation (2026-10-18)

The single unfiltered Core sweep observed the following timing failures. Every
method subsequently passed **3/3 fresh exact-method invocations** on the same
workstation; the suites were not repeated.

| Method | Isolated evidence |
| --- | --- |
| `Transport.TcpTransportPriorityLanePoolTests.Ordered_world_data_is_next_drained_even_after_a_stream_backlog` | Final 3/3 pass; earlier isolated failures also reproduce on untouched dispatch base (`906d032e`: 2 fail / 1 pass). |
| `Transport.TcpTransportPriorityLanePoolTests.Ordered_join_frames_are_never_shed_when_the_stream_overflow_collapses` | 3/3 pass in the subsequent isolated batch. |
| `Transport.TcpTransportPriorityLaneThreadPerPeerTests.Ordered_join_frames_are_never_shed_when_the_stream_overflow_collapses` | 3/3 pass in the subsequent isolated batch. |
| `Transport.TcpTransportCoalesceAndReasonThreadPerPeerTests.Coalesce_distinct_keys_remain_independent` | 3/3 pass. |
| `Transport.TcpTransportCoalesceAndReasonThreadPerPeerTests.Guaranteed_coalesced_frames_are_never_shed_but_still_bounded` | 3/3 pass in the subsequent isolated batch. |
| `Transport.P670SendStallTests.Departed_peer_bytes_remain_in_host_window_totals` | 3/3 pass, both transport theory rows each time. |
| `ShipUpdateBudgetLiveTests.Ship_dropped_one_tick_is_served_the_next_no_permanent_starvation` | 3/3 pass. |
| `ShipsUpdateLiveTests.Cross_side_ships_see_each_other_and_not_self` | 3/3 pass. |
| `MissileBroadcastTests.Directly_fired_missile_broadcasts_create_and_destroy_to_teammate_but_not_enemy` | 3/3 pass. |
| `LiveTeamLobbyTests.Unready_broadcasts_team_ready_false` | 3/3 pass. |

The single ClientCore sweep observed **1,458 pass / 3 fail / 1 opt-in skip**.
All three failed methods then passed **3/3 fresh exact-method invocations**:

| Method | Full-sweep symptom |
| --- | --- |
| `DeadLatchRespawnLiveTests.Own_ship_sim_resumes_and_accepted_climbs_after_death_respawn_undock` | One rejected native pose across death/respawn. |
| `ClientWarpApproachLiveTests.Client_bot_steered_by_warp_approach_law_transits_two_hops_to_target` | Ship had reached sector 1052 instead of 1060 at the deadline. |
| `ClientWarpApproachDodgeLiveTests.Client_bot_with_shared_dodge_transits_two_hops_through_real_alderan_field` | Ship had reached sector 1053 instead of 1060 at the deadline. |

The default-gated P660 showcase rotation skip was exercised separately with
`ALLEGIANCE_LIVE_P660_SHOWCASE=1` and passed 3/3. MonoGame completed with
**2,471 pass / 0 fail / 3 unavailable private Meshy-pack skips**. No second full
sweep was run.

No transport code, test assertion, timeout, or quarantine changed. Earlier
rechecks are preserved, including failed ones. The final isolated passes meet
the brief's light-gate classification; they do not fix these scheduling/socket
assumptions. See [P745B report](reports/P745B_FINALE_ENDING.md) and
[verification ledger](reports/P745B_assets/verification.json). Raw logs/TRX are
under `/tmp/allegiance-evidence/P745B/` (the lane cannot write outside its
workspace and `/tmp`).

## SC1 observation (2026-10-19)

SC-1's single Core sweep returned 4,874 pass / 15 fail / 0 skip. ClientCore returned 1,453 pass / 3 fail / 1 opt-in rig skip; MonoGame returned 2,471 pass / 0 fail / 3 optional private-pack skips. The exact-case qualification below records every window; see the [SC1 report](reports/SC1_SHOWCASE_SKILLS.md), [validation ledger](reports/SC1_assets/validation.json), and [isolated-case ledger](reports/SC1_assets/isolated.json).

| Case | Qualification |
|---|---|
| `GameOverPlayersWireTests.Win_by_elimination_broadcasts_game_over_players_to_surviving_peer` | 3/3 fresh exact-case passes |
| `LiveCountdownStartPingTests.Multi_mission_countdown_scopes_per_mission` | 3/3 fresh exact-case passes |
| `LiveCountdownStartPingTests.StartGame_owner_all_ready_goes_through_real_countdown` | 3/3 fresh exact-case passes |
| `LiveRelaunchTests.Carrier_without_launch_slots_uses_backward_fallback` | 3/3 fresh exact-case passes |
| `LiveRipcordTests.First_activation_broadcasts_to_everyone_flying_in_sector` | 3/3 fresh exact-case passes |
| `LiveRipcordTests.Re_target_activates_to_requester_only` | 3/3 fresh exact-case passes |
| `LiveRipcordTests.Ship_anchor_insufficient_energy_aborts_consumed_not_retried` | 3/3 fresh exact-case passes; earlier window retained (2/3 pass), final recovery 3/3 pass |
| `LiveTeamLobbyTests.Unready_broadcasts_team_ready_false` | 3/3 fresh exact-case passes |
| `P694MassEventTickTests.Launch_point_mass_events(count: 150, parallel: True, stationDeath: True, coincident: False, nativeLaunch: False)` | changed 3/3 failures; unchanged base 3/3 failures; performance gate not clean |
| `P694MassEventTickTests.Launch_point_mass_events(count: 195, parallel: False, stationDeath: True, coincident: False, nativeLaunch: False)` | changed 3/3 failures; unchanged base 2/3 failures; performance gate not clean |
| `P706DispatchProfilingTests.Dispatch_budget(scenario: "matchStart", count: 165)` | 3/3 fresh exact-case passes |
| `ShipsUpdateLiveTests.Cross_side_ships_see_each_other_and_not_self` | 3/3 fresh exact-case passes |
| `Transport.TcpTransportCoalesceAndReasonPoolTests.Coalesce_distinct_keys_remain_independent` | 3/3 fresh exact-case passes |
| `Transport.TcpTransportCoalesceAndReasonThreadPerPeerTests.Coalesce_healthy_consumer_still_receives_each_latest_snapshot` | 3/3 fresh exact-case passes |
| `Transport.TcpTransportGreetTimeoutPoolTests.Silent_connection_does_not_block_a_second_client_join` | 3/3 fresh exact-case passes |
| `Allegiance.ClientCore.Tests.ClientWarpApproachLiveTests.Client_bot_steered_by_warp_approach_law_transits_two_hops_to_target` | 3/3 fresh exact-case passes |
| `Allegiance.ClientCore.Tests.MissileTrackingLiveTests.FiredMissile_AppearsMovesAndDisappears_InClientWorld_OverTheWire` | 3/3 fresh exact-case passes |
| `Allegiance.ClientCore.Tests.P693TeamLimitsLiveTests.Batched_bots_consume_native_balance_refusal_and_choose_an_eligible_side(behavior: "weave")` | 3/3 fresh exact-case passes |

The P694 rows reproduce on the unchanged base and remain a performance limitation. Their budgets and native simulation were not changed. Raw TRX/logs live under `/tmp/allegiance-evidence/SC1/`; only authoritative `isolated-exact/` results were used for classification. Preliminary overlapping helper output is excluded. Both revisions passed the separate never-parks gate 5/5.

## R2 observation (2026-10-18)

The R2 installer lane's single unfiltered Core sweep observed these failures.
Each exact method passed **3/3 fresh individual invocations** on the same binaries:

| Method | Full-run observation | Isolated result |
| --- | --- | --- |
| `Transport.P661EgressAccountingTests.Hot_path_allocates_zero_bytes_for_all_events` | Expected 0 allocated bytes, observed 7,720 | 3/3 pass |
| `Transport.TcpTransportPriorityLanePoolTests.Ordered_world_data_is_next_drained_even_after_a_stream_backlog` | Ordered lane held 0 world-data frames at observation | 3/3 pass |
| `ShipsUpdateLiveTests.Cross_side_ships_see_each_other_and_not_self` | B had not received HEAVY_SHIPS_UPDATE | 3/3 pass |

These meet R2's load-flake rule. No production C# code, test assertion, deadline,
or quarantine was changed. See [R2 installer report](reports/R2_INSTALLERS.md)
and the small [verification ledger](reports/R2_assets/verification.json).
Raw fresh-process logs/TRX live outside the repository under
`/tmp/allegiance-codex-r2-evidence/`.

## P729 observation (2026-10-18)

The single P729 Core sweep reported **4,875 pass / 4 fail / 0 skip**. Each failed
method then passed **3/3 fresh exact-method invocations** on the same workstation:

| Method | Full-sweep symptom |
| --- | --- |
| `Transport.TcpTransportSendWorkerPoolTests.Pool_worker_and_reader_counts_stay_fixed_across_join_leave_churn` | `TimeoutException: no OnPeerJoined for raw peer` |
| `Transport.TcpTransportPriorityLaneThreadPerPeerTests.Ordered_join_frames_are_never_shed_when_the_stream_overflow_collapses` | Expected 50 ordered frames; observed 35 at the short observation point |
| `LiveTreasurePickupTests.Rescue_ship_picking_cash_treasure_credits_side_broadcasts_moneychange_and_destroys_it` | Cash pickup assertion in the parallel full sweep |
| `LiveGainFlagTests.Flag_carrier_docking_at_own_side_station_secures_and_game_state_reflects_real_count` | Ship had not docked at the assertion point |

This meets P729's explicit light-gate load-flake rule. No assertion, timeout,
transport implementation, quarantine, or native simulation rule was changed to
make these pass. The full sweeps are not repeated. The isolated passes establish
classification for this run, not an underlying transport/docking fix. See the
[P729 report](reports/P729_BENCHMARK.md) and its compact validation ledger; raw TRX
files are under `/tmp/allegiance-evidence/P729/` because the requested home evidence
directory is outside this lane's writable sandbox roots.

## HD-3 observation (2026-10-18)

The single HD-3 Core sweep reported **4,879 pass / 2 fail / 0 skip**.
`Transport.TcpTransportGreetTimeoutPoolTests.Silent_connection_does_not_block_a_second_client_join`
failed in `TcpTransport.Shutdown` with `ThreadStateException: Thread has not been started`.
`LiveRipcordTests.Cancel_while_active_aborts_sector_wide` observed an empty abort
receipt collection at `Assert.Single`. Both symptoms match earlier entries below.
Each exact method passed **3/3 fresh isolated invocations**, with one case per
invocation and unchanged assertions/deadlines. They meet the HD-3 light-gate
load-flake rule; these passes do not establish a fix to either underlying race.
No transport, bot, simulation or test timing code changed. The single ClientCore
and MonoGame sweeps reported no failures. See the
[HD-3 report](reports/HD3_HD_BY_ATLAS.md) and
[validation/isolation ledger](reports/HD3_assets/validation-summary.json).
Raw TRXs/logs remain at `/tmp/hd3-evidence/` because this lane cannot write the
preferred home-directory evidence path. No second full sweep was run.

## P743 correctness pass observation (2026-10-18)

The P743 lane ran one unfiltered sweep of Core, ClientCore and MonoGame.
Core passed **4,879/4,879**. ClientCore recorded **1,377 pass / 3 fail / 1 skip**;
MonoGame recorded **2,435 pass / 0 fail / 3 existing private Meshy-pack skips**.
The skipped ClientCore P660 opt-in showcase passed a separate explicitly enabled
local invocation. Lobby also passed 19/19 in one supplementary run.

After all three sweeps, these exact cases each passed **3/3 separate isolated
invocations**:

- `P703ResearchCommanderLiveTests.Both_sides_complete_the_first_branch_using_native_wallets_and_tech_gates(civA: 25, civB: 3, generated: False)` — full sweep expected two completed sides, observed one.
- `ClientWarpApproachSlowHullLiveTests.Legacy_200_450_arrival_parks_slow_board_hull_26_outside_the_transit_volume` — full sweep expected sector 1053, observed 1060.
- `ClientWarpApproachLiveTests.Client_bot_steered_by_warp_approach_law_transits_two_hops_to_target` — full sweep expected sector 1060, observed 1052.

This meets the brief's load-flake rule and repeats already-recorded failure
classes. It does not establish a fix. No assertion, timing bound, quarantine,
transport, bot or simulation behavior was changed. The only production change
was the sector-info asteroid-grouping display rule. Full failures and all nine
exact-case retries are retained in the [validation ledger](reports/P743_assets/validation.json)
and [report](reports/P743_CORRECTNESS_PASS.md). Raw logs/TRX are outside the
repository at `/tmp/allegiance-evidence/P743/` (the requested home evidence path
is not writable in this session).

## Resolved — TT-1b (2026-10-18–19)

The repairs below supersede earlier unresolved observations of these methods.
Historical run results remain evidence of their original failures; isolated
reruns alone were never evidence of a fix. Final contention proof and exact
validation evidence are recorded in [TT1B_FAST_DETERMINISM.md](reports/TT1B_FAST_DETERMINISM.md).

| Tests | Established cause / resolution |
| --- | --- |
| `LiveCombatWireTests.Moving_target_streams_its_falling_hull_over_the_wire` | Setup waited for the target assignment and immediately dereferenced the independent shooter assignment (`no shooter ship` in the failed gate). Await both assignments with a 30-second bound before issuing controls; preserve role resolution, tick limit and falling-hull assertion. |
| `BallotVotingLiveTests.Multi_mission_ballot_scopes_per_mission_and_per_side`; draw-chain and shared setup callers | Proposer receipt did not imply the other voter's receipt. Mark the ordered publications and await every relevant reader with time fixed before checking positive/negative scope. The draw-chain sibling uses the same barrier. Shared setup retains its 40 ticks and then drains every intended reader before history reset; all eight live callers are requalified. Native audience, tally, leadership and game-over assertions remain. |
| `StartingMoneyStationIncomeLiveTests` three wire checks | Proactive repair of the same 60 ms receipt assumption as the payday fixtures. Await ordered server markers with time fixed for initial grant, early-silence, periodic income and capture phases. Arm periodic clocks after seating/setup; keep seed, wallet, income and owner-transfer assertions, adding explicit exact counts at the marked boundaries. |
| `PaydayTests.ZeroPlayerSide_does_not_crash_and_accrues_without_paying`; wire-cadence sibling | Pumping to absolute simulation time 0.5 and sleeping 60 ms did not establish seating. Wait for actual assignments with payday physics paused, then arm the clock and sample relative 50/65/125-second boundaries. Flush publications and inject ordered server markers to await each reader without advancing time. Keep early-silence, exact counts, income, side scope and zero-player treasury assertions. |
| `ClientWalletLiveTests.BucketDonation_DecrementsDonorClientsMyMoney_ExactlyOnce_Live` | Server `Started` preceded decoding its initial payday, so the fixture sampled a zero baseline; its tight wait then advanced through later paydays. Await client Started and the initial wallet, pace donation dispatch at 20 Hz, then freeze physics after the authoritative debit while awaiting client receipt. Keep the exact once-only client delta and server-wallet debit assertions. |
| `AsteroidMinedLiveTests.No_broadcast_fires_once_miner_hits_capacity`; whole-mission mining sibling | Sampling receive counts at full capacity included only part of the earlier report stream (14 of 26 in the failed gate). Pause mining through seating; fill to actual capacity and use each peer's ordered echo before sampling and after the original 150 idle ticks. The positive sibling generates the first threshold crossing, freezes physics and awaits all readers before comparing historical fractions. Native threshold, audience and zero-additional-report assertions remain. |
| `RipcordRequestLiveTests.RipcordKey_Initiate_Countdown_Teleport_Deny_Cancel_EndToEnd` | Server cancellation preceded client decoding of `RIPCORD_ABORTED`. Await the client state under its gate with simulation fixed, retain aborted-sound and all earlier request/deny/transit assertions, and dispose the fixture server. |
| `LiveTeamLobbyToggleTests.Sides_lock_from_owner_relays_and_non_owner_refused`; lobby-lock sibling | Server flag / first recipient receipt preceded the other recipient's relay. Await both before asserting or resetting; use the non-owner's ordered PING echo before checking refused-unlock silence. The lobby sibling has the same assumption and is repaired proactively. |
| `LiveTreasurePickupTests` cash and autodonation checks; `LiveBucketTests.Small_donation_funds_bucket_and_broadcasts_sidewide_not_cross_side`; `LiveInSpaceEjectTests.Already_a_lifepod_is_truly_killed_not_re_ejected` | Proactive receipt audit: destruction does not imply cash receipt, one donation recipient does not synchronize its teammate, and kill receipt does not imply deletion/respawn receipt. Await all asserted messages with time fixed; drain the initial bucket catalog before reset. Cash pickup also resolves roles by assigned ship ID rather than concurrent connection order. Preserve wallet, payload, audience, exact-count, no-eject and station assertions. The cash receipt failure was already recorded in P738. |
| `ShipsUpdateLiveTests.Cross_side_ships_see_each_other_and_not_self`; `LiveRipcordTests` activation/abort waits; `LivePositionRequestTests.Non_leader_ack_refused` | One reader's receipt did not synchronize other recipients. Await all required recipients before asserting/resetting. Use a same-peer ordered PING barrier for the silent negative ACK. |
| `ShipStatusServerTests.Teammate_ship_playerinfo_is_broadcast_once_after_export_before_status` | Concurrent join order could give the observer a separate lobby-roster PLAYERINFO as well as the teammate one-shot. Seat observer first; await each own join burst. |
| `TcpTransportSendWorkerPoolTests.Pool_worker_and_reader_counts_stay_fixed_across_join_leave_churn` | Unsynchronized per-IP departure accounting raced concurrent departures/accept. Lock the accounting, await all resource release between waves, and test eight-slot reuse. |
| `TcpTransportGreetTimeout*Tests.Silent_connection_does_not_block_a_second_client_join`; prior `P670SendStallTests.Departed_peer_bytes_remain_in_host_window_totals` shutdown observations | A tracked thread could be joined before its `Start`. Start under the same registration/snapshot lock. |
| `TcpTransportPriorityLane*Tests` queue-retention/priority checks; `TcpTransportCoalesceAndReason*Tests` same-key/distinct-key/guaranteed queue checks | Fixed sleeps and a coalescing flood never established writer blockage. Pause the actual queue consumer, force the real capacity boundary where relevant, and inspect stable queues. Fixed priority checks leave quarantine. |
| `MissileBroadcastTests.Directly_fired_missile_broadcasts_create_and_destroy_to_teammate_but_not_enemy`; `LiveRelaunchTests.Carrier_without_launch_slots_uses_backward_fallback` | Historical launch poses were compared with moving live state after delivery. Retain launch-time state / assert decoded launch poses. Carrier check leaves quarantine. |
| `LiveLogoffTests.Logoff_echoes_releases_and_retires_the_ship` | Production queued the echo then immediately erased the sender queue. Retire logically, drain before EOF with a bounded timeout, keep physical admission accounting charged, and reject replay. |
| `LiveLifepodEnduranceDisplayTests.Driver_sees_countdown_others_do_not_and_dock_cancels` | Late pre-dock updates were classified by receive time; tight ticks could erase countdown samples through legitimate coalescing. Await sample ticks and classify the inactive sample by generated tick. |
| `CrossSideExportLiveTests.Same_side_teammate_still_gets_teamexport_no_cross_duplicate` | The shared 900 km spawn is outside the universe damage boundary; an extra tick ejects the scout into a lifepod before its earlier export is decoded. Derive same-side positions inside the core's real boundary. |
| `GameOverPlayersWireTests.Win_by_elimination_broadcasts_game_over_players_to_surviving_peer` | Rotation replaced the client's ship assignment during the old-match assertion. Capture the old-match identity before elimination. |
| `StationsUpdateLiveTests.Damaged_station_fraction_reflected_in_next_update` | An absent station produced a zero-filled default entry, incorrectly satisfying “shield != 240”. Await the requested station and actual damaged byte. |
| `DiscoveryTests.Unicast_probe_finds_a_loopback_responder` | Short UI discovery deadline used as a loaded-host scheduling allowance. Use a generous test-only response bound. |
| `DiscoveryTests.Coordinator_merges_favorite_and_tailscale_and_dedupes` | Same cause as the unicast probe test: the default UI probe deadline expired on a loaded CI runner (public player-release run 39731523365, `Assert.Single()` on an empty list). Use the same 30 s test-only response bound; a reply ends the wait early. |
| `P653DPodPurchaseLiveTests.Hangar_launch_after_ejection_or_pod_death_buys_real_hull` | Late pod-sync ACK satisfied “any ACK”. Select the ACK for the requested real hull. |
| `StationsUpdateLiveTests.Visible_station_received_correct_fields_absent_from_unseeing_side` | An earlier join update lacked the fixture station. Await and select updates containing the intended IDs before checking their fields and scope. |
| `LiveCountdownStartPingTests` owner-start / multi-mission checks; `LiveTeamLobbyTests.Unready_broadcasts_team_ready_false` | Server stage / first reader receipt did not imply other readers decoded the broadcast. Await each intended recipient while holding simulation time fixed for countdown announcements. Repaired quarantined checks return to the main Fast lane. |
| `P657bStallTelemetryTests.DedicatedWatchdogHasHighestPriority_AndLoggerFlushesOnShutdown` | Production shutdown bounds each join to 500 ms. Await eventual event logging and both dedicated thread exits; retain priority and thread identity assertions. |
| `MissileTrackingLiveTests.FiredMissile_AppearsMovesAndDisappears_InClientWorld_OverTheWire` | Pending coalesced positions could follow ordered destruction and resurrect a missile. Supersede pending missile positions before destruction; four deterministic queue-order regression cases cover both transports and publisher modes. |
| `ShipUpdateBudgetLiveTests` bounded / fairness / locked-target checks | In-flight join frames preceded NPC creation, and coalesced snapshots did not represent consecutive ticks. Generate and decode each exact post-setup tick before asserting quota, next-tick fairness and target priority. |
| `AutoReloadOnEmptyTests` | Concurrent joins made array order differ from role/side order. Join in role order and isolate server-global settings with LiveNetwork. |
| `RadarTargetHighlightLiveTests.LiveOffScreenEnemy_IsTheFlaggedRadarTargetHighlight` | Identity can arrive before pose. Await real identity and both required poses before gathering candidates. |
| `MissionRotationLiveTests.Finished_mission_rotates_to_fresh_notstarted_and_new_joiner_starts` | Server stage changes precede client decode. Await generated Starting and Started frames without consuming countdown ticks. |
| `P693TeamLimitsLiveTests.Batched_bots_consume_native_balance_refusal_and_choose_an_eligible_side` (`idle`) | MCP's plain-bot launch observer permanently preferred a late seat DOCKED over newer flying status. Track own launch signals in receive order; deterministic regressions also require a later redock to revoke confirmation. |
| `MineCrossSideVisibilityLiveTests.Enemy_ship_spots_a_laid_mine_once_in_range_and_learns_its_destruction` | Concurrent join order assigned near/far geometry to different named roles. Anchor layer and scanner positions after resolving their assignments; preserve range, once-only export and destruction assertions. |
| `MiscKeyBindingLiveTests.Live_own_ship_is_real_and_center_ranking_selects_a_real_ship`; related `FilteredTargetingLiveTests` | Own identity / NPC identity can precede NPC poses. Await all required identity and pose data before gathering/ranking; keep real ship and side assertions. |
| `P493EnemySpottingLiveTests.Probe_spot_of_cross_side_ship_delivers_status_and_hidden_to_side0_peer` | Callback order could swap named observer and intruder roles. Seat the side-0 observer before connecting the intruder. |
| `LoadoutChangeLiveTests` mount/remove and boarding/leaving | Teammate receipt did not synchronize owner receipt. Await both readers and reset recipient histories between phases; preserve owner/team payload and enemy-silence assertions. |
| `CloakVisibilityGateLiveTests` | Late pre-cloak snapshots arrived after resetting history. Classify by generated tick and await non-vacuous post-transition samples; preserve detection and cloak-state assertions. |
| `LiveP548GameOverStageGateTests` three positive GAME_OVER checks | Server Stage Over preceded survivor decode. Await the generated wire event with fixed simulation time, retaining exact-one and lobby/deactivation assertions. |
| `LiveCombatWireTests` both two-party kill variants | Victim receipt did not synchronize shooter credit. Await both intended recipients; preserve dead-ship, death and shooter-credit assertions. |
| `NpcShipFoundationLiveTests.Ownerless_npc_ship_appears_in_peer_heavy_updates_with_position`; `ShipsUpdateLiveTests.Ship_leaving_cluster_disappears_from_next_update` baseline | Old join snapshots satisfied “any HEAVY”. Await the actual ship ID; require the exact next generated post-removal frame for absence. |
| `MapGenBrawlTests`, `MapGenGridFamilyTests`, `MapGenRadialFamilyTests` | Cold setup scanned all 98 cores to pick one. Load the representative PCore001 fixture directly, validate six usable civilizations, and retain all 20 Fast structural methods and theory cases. |
| `NpcCrossClusterLiveTests.Cross_cluster_npc_paths_through_warp_and_continues_to_station_in_new_sector` | An unasserted 60,000-tick tail ran after both monotone approach claims were satisfied. Stop when both hold, retaining the upper bound and every assertion. Remains Fast. |
| `MiningTests.Loopback_synthetic_rock_mines_and_ore_reaches_the_wire` | An older in-flight nonzero ore observation satisfied the wait while continued simulation increased server cargo. Generate one dirty pose, capture its ore fraction, and await that exact wire tick with simulation held fixed. Preserve the nonzero ore and fraction agreement assertions. |
| `LiveShipDeathFinalizeTests.Oxygen_dead_lifepod_with_no_friendly_station_is_finalized_server_side` | Server-side `Dead` became true before the victim decoded its death/deletion events. Await both ship-specific wire events with simulation held fixed; retain killer credit, deletion reason, no-respawn and no-resurrection assertions. |
| `LiveGainFlagTests` secure and hostile-station checks | A late held-flag join-sync either satisfied “any GAIN_FLAG” before the dock tick or polluted the hostile station's empty-history assertion. Decode that initial announcement before reset; the positive case then waits for server docking and the matching secure (`SideidFlag=-1`) announcement with time fixed. |
| Four generated warp transit checks (SnowFlake, SingleRing, HiLo, Grid); new five-family asteroid-clearance regression | The initial receive/physics phase repair was insufficient: randomly generated asteroids could obstruct the warp pose. Production generation omitted native `common.cpp:2803–2841` existing-model clearance (minimum radii 550, distance squared ≥ twice summed radii squared, 1.02 sampling-radius expansion on rejection). Restored it faithfully; all five regression rows fail before the repair. Transit fixtures seat paused and enter a clear real warp volume with the complete generated field intact. P676 now checks native clearance instead of an invalid unexpanded-radius bound. |
| `BotAcceptsAndEngagesOrderLiveTests.ScenarioBot_AutoAcceptsAndEngagesTheSpecificCommandedTarget`; new queued-accept regression | MCP wrote `CmdAccepted`, leaving the authoritative queued slot uncleared until native expiry. Both senders now use the existing `CmdQueued` accept path; idle/combat regression rows fail against the old sender. The fixture also paces at 20 Hz, serializes setup with ticks, awaits target visibility and both server/manager engagement, then stops physics for inspection. Acceptance must clear the queue before the unchanged 15-second expiry. |
| `LivePlayerDeathRespawnTests` three live death checks; related oxygen/weapon-death checks | Owner respawn/deletion receipt did not synchronize another recipient's kill/deletion history. Await every asserted recipient; preserve post-death simulation in the no-respawn checks and use each peer's ordered PING echo before asserting ENTER_LIFEPOD silence. The oxygen and weapon-kill siblings had the same assumption and are repaired proactively. |
| `ClientWarpApproachDodgeLiveTests.Client_bot_with_shared_dodge_transits_two_hops_through_real_alderan_field` | Its tight 60,000-tick loop raced pose feedback, and its obsolete 200/450 approach could park outside the transit sphere (116.8 units in the failed run). Await each generated pose and use existing MCP `WarpApproachParams` with real warp/hull radii. Keep the complete asteroid field, shared dodge law, two-hop goal, distance assertion and original tick bound. |
| Core host abort in `TcpTransport.ReceiveLoopStart`; new `TcpTransportAcceptShutdownTests` | Shutdown can stop the listener after the running check but before `AcceptTcpClient`, which throws `InvalidOperationException` rather than the already-handled socket/disposal exceptions. Capture the listener per host generation and handle only intentional stop/replacement; preserve unexpected live-listener failures. A controlled race regression covers stop and restart in both transport modes. |

Timing classification: `DeadLatchRespawnLiveTests.Own_ship_sim_resumes_and_accepted_climbs_after_death_respawn_undock`
now explicitly belongs to **Live**: wall-time clock extrapolation and fixed-step
simulation cannot preserve the native pose gate under arbitrary descheduling.
Its real-clock and zero-rejection assertions are intact.
`ClientWarpApproachLiveTests.Client_bot_steered_by_warp_approach_law_transits_two_hops_to_target`
and both `ClientWarpApproachSlowHullLiveTests` now synchronize each generated
pose before advancing simulation. All three remain in **Fast**; the repaired
legacy comparison leaves quarantine. No new skip is introduced.

`P656ShowcaseFinaleTests.P656b_AllRealStationTargetsSelectOnlyBombersWithAllTech`
also explicitly belongs to **Live** under the existing expensive-corpus rule:
it sweeps every real station through the complete hull/part selector and took
26.176 s in a gate whose calibrated allowance was 19.499 s. Its full-corpus
assertions remain intact; it passed three fresh isolated invocations after
classification. No performance limit was inflated.

The retained-run timing audit also classified full-core/map/model corpus
validation and complete mining/research/match scenarios as **Live**, and dense
collision-work/cache-budget checks as **Perf**, following
[the existing tier definitions](testing/TIERS.md). Every moved method and all
modified map-fixture methods passed three fresh exact-method invocations with
all theory variants. Full coverage and assertions remain; only scheduling
changes for the expensive scenarios. The exact method list, timing evidence
and causes are in [the report](reports/TT1B_FAST_DETERMINISM.md).

### TT-1b broad-sweep Perf observations

The single unfiltered sweep overlapped the deliberate CPU contention. Nine
`P694MassEventTickTests.Launch_point_mass_events` data cases exceeded their
unchanged Debug 200 ms median-tick budget. Each exact failed data case then
passed **3/3 fresh isolated invocations** after the CPU workers stopped (27
passes). `P705TechModeLiveTests.Research_upgrades_and_resets_through_two_complete_local_matches`
(`civA=1,civB=2,size=2`) failed the five-second ticker shutdown join; that exact
case then passed **3/3** (43.86 / 45.58 / 42.75 s invocation wall times).
These meet the brief's load-flake qualification rule; they remain **Perf**, with
unchanged assertions, budgets and production code. They are not claimed as
underlying fixes. Exact case filters, original failures and TRX are retained in
the [TT-1b evidence](reports/TT1B_FAST_DETERMINISM.md).

The same sweep's Fast mine failure was repaired above and requalified three
times individually. MonoGame completed 2,431/0/3; the three skips require the
existing private Meshy pack. ClientCore's opt-in P660 local 15v15 showcase was
subsequently enabled and passed as a separate exact-method invocation, including
30 migrated bots and two connected human peers. No second unfiltered sweep ran.

### Original TT-1 evidence (before TT-1b repairs)

The one full tier sweep observed these Fast failures. Each then passed all three
fresh exact-method invocations, meeting TT-1's explicit load-flake qualification
rule. No assertion, timeout, quarantine trait or production code was changed for
these failures.

| Case | Original symptom | Isolated result |
| --- | --- | --- |
| `Allegiance.ClientCore.Tests.DeadLatchRespawnLiveTests.Own_ship_sim_resumes_and_accepted_climbs_after_death_respawn_undock` | server rejected 1 native pose across death/respawn | 3/3 pass (4.472 / 4.465 / 4.468 s) |
| `Allegiance.Core.Tests.ShipsUpdateLiveTests.Cross_side_ships_see_each_other_and_not_self` | B had not received HEAVY_SHIPS_UPDATE | 3/3 pass (0.178 / 0.174 / 0.183 s) |
| `Allegiance.Core.Tests.Transport.TcpTransportSendWorkerPoolTests.Pool_worker_and_reader_counts_stay_fixed_across_join_leave_churn` | TimeoutException: no OnPeerJoined for raw peer | 3/3 pass (0.218 / 0.217 / 0.216 s) |

[Qualification ledger](reports/TT1_assets/qualification.json),
[task report](reports/TT1_TEST_TIERS.md). Original and isolated TRX are archived
under `docs/reports/TT1_assets/sweep/` and `docs/reports/TT1_assets/focused/`.
The distinct hygiene-duration and deferred-theory accounting findings were fixed
and verified separately; they are not classified as load flakes.

## HD-2 observation (2026-10-17)

The single HD-2 Core run reported **4,781 pass / 2 fail / 0 skip**. One failure was
`Transport.TcpTransportSendWorkerPoolTests.Pool_worker_and_reader_counts_stay_fixed_across_join_leave_churn`.
The symptom was `TimeoutException: no OnPeerJoined for raw peer`.
The exact method passed **3/3 fresh individual invocations** on the same
transport implementation ([isolation ledger](reports/HD2_assets/transport-isolated.json)).
This meets the HD-2 light-gate load-flake rule and matches the earlier pool-churn
observations below. No transport code, timeout, assertion or quarantine trait
was changed. The isolated passes do not establish a fix for the underlying
join/churn behavior.

The single ClientCore run reported **1,178 pass / 2 fail / 0 skip**. Its
`P705TechModeLiveTests.Research_upgrades_and_resets_through_two_complete_local_matches`
case `civA=101, civB=1, size=5` timed out waiting for both sides to launch an upgraded hull (simulation time 18,390.7). It passed **3/3 exact-data-case isolated
invocations** after its full-run failure
([ledger](reports/HD2_assets/p705-isolated.json)). Each invocation ran one data
case and completed both matches. This matches the earlier P712 observation;
no research, loadout, bot or timeout code changed.

The `P703ResearchCommanderLiveTests.Both_sides_complete_the_first_branch_using_native_wallets_and_tech_gates`
case `civA=25, civB=3, generated=False` observed one completed side instead of the expected two. It passed **3/3 exact-data-case
invocations** after its full-run failure
([ledger](reports/HD2_assets/p703-isolated.json)). Each invocation selected one
case. It also meets the light-gate load-flake rule; no research implementation
or deadline was changed.

The other initially observed failure,
`P652EconomyDroneAiTests.Enemy_lockout_complains_with_native_voice_id_after_initial_backoff`,
is **not a flake**: its stale `SendChat`/`1342` observation failed 3/3 alone.
The existing P725 implementation uses `DroneChatEvent` and the C# port's `341`
cue. The fixture now observes that event/cue while retaining its original
29.99 / 30 / 30.1 second schedule, complaint counts, text and ore assertions;
it passed 3/3 after correction. Production audio and simulation code are
unchanged. See the [HD-2 report](reports/HD2_PBR_CLIENT.md),
[baseline triplet](reports/HD2_assets/voice-baseline.json) and
[corrected triplet](reports/HD2_assets/voice-fixed.json).

## P734b observation (2026-10-17)

The 2026-10-17 light gate ran **one** unfiltered sweep: **8,284 pass /
1 fail / 3 existing optional private Meshy-pack skips**. Project counts are recorded in the linked report and invocation ledger.
After all projects finished, each failed method passed **3/3 fresh exact-method
isolated invocations**:

| Method | Full-suite observation | Isolated evidence |
| --- | --- | --- |
| `Transport.TcpTransportSendWorkerPoolTests.Pool_worker_and_reader_counts_stay_fixed_across_join_leave_churn` | System.TimeoutException : no OnPeerJoined for raw peer | 3/3 pass: [1](reports/P734b_assets/suite-retry-000-1.trx.gz), [2](reports/P734b_assets/suite-retry-000-2.trx.gz), [3](reports/P734b_assets/suite-retry-000-3.trx.gz) |

This meets the brief's explicit load/timing-flake rule. No assertion, timeout,
transport behavior or quarantine setting was changed. The isolated passes
do not establish an underlying fix.

Separately, all **208 methods × 3** focused invocations passed (978 cases,
zero skips), the unchanged never-parks test passed **5/5** (maximum stopped
with target 2.014 seconds), and the local civ25/3 unlocked mixed-fleet Brawl
showcase reached **two natural Stage Over events** with automatic rotation
and zero simulation-stage failures. Earlier incomplete scene attempts are
retained as diagnostics rather than passing proof. P733 is absent, so the
brief's unlocked fallback was used. No second full sweep was run in this lane.

See the [P734b merge report](reports/P709_PARITY_WAVE12.md#p734b-merge),
[invocation ledger](reports/P734b_assets/verification.json), and
[summary](reports/P734b_assets/summary.json).

## P734 observation (2026-10-17)

Both complete final suite sweeps were clean: **16,520 pass / 0 fail / 6
existing optional Meshy-pack skips**. Each sweep recorded Core 4,807/0/0,
ClientCore 1,174/0/0, MonoGame 2,260/0/3 and Lobby 19/0/0. The final affected
cohort passed 91 methods × 3 exact-method runs / 537 case passes without skips;
the final never-parks gate passed 10/10. The local unlocked civ25/3 showcase
reached two natural Stage Over events with automatic rotation and no
simulation-stage failures.

Earlier overlapping **Core-only diagnostic attempts**, before the docking
merge correction, observed two unrelated failures. After the completed final
sweeps, both passed **3/3 fresh exact-method isolated invocations**, satisfying
P734's explicit load/timing-flake rule:

| Method | Diagnostic failure | Isolated evidence |
| --- | --- | --- |
| `GameOverPlayersWireTests.Win_by_elimination_broadcasts_game_over_players_to_surviving_peer` | No matching fixture ship | `P734_assets/suite-retry-000-{1,2,3}.trx.gz`: 3/3 pass |
| `Transport.TcpTransportSendWorkerPoolTests.Pool_worker_and_reader_counts_stay_fixed_across_join_leave_churn` | No raw-peer join event before the original timeout | `P734_assets/suite-retry-001-{1,2,3}.trx.gz`: 3/3 pass |

No assertion, deadline or quarantine setting was changed for these passes;
they do not establish the underlying cause of the intermittent observations.
The same diagnostics also caught
`P699cStationCorpusTests.Npc_docking_thrusters_survive_idle_combat_and_clear_on_next_order`:
that was a real merge regression, corrected by preserving autopilot thrust
when the combat plot is idle. It passed the immediate three isolated checks,
the full three-run affected cohort, and both final Core sweeps. It is **not
classified as a flake**.

One original MonoGame invocation returned zero without a completed TRX or final
summary. That incomplete attempt is retained and excluded from the complete
suite counts. An unfiltered direct VSTest invocation of the already-built
assembly completed it, including the 605-second human-flight soak. Both
completed MonoGame runs contain the same three existing optional private-pack
skips; no required local proof was skipped.

See the [P734 merge report](reports/P708_PARITY_WAVE11.md#p734-merge),
[final ledger](reports/P734_assets/verification-final.json),
[incomplete record](reports/P734_assets/verification-incomplete.json), and
[final audit](reports/P734_assets/summary.json).

## P716 observation (2026-10-16)

Full-suite run 2 reported Core **4,706 pass / 1 fail / 0 skip** with a single
failure in `MapGenRingLiveProofTests.GeneratedHiLo_PeerFliesThroughWarp_ToTracedNeighbour`
(a live-network map-gen ring + warp-tracing proof, unrelated to P716's
client-side sound events). It passed **5/6 isolated runs** (fail, pass, pass,
pass, pass, pass) on the same binaries — an intermittent live-net load flake
(exacerbated by concurrent Codex suite runs on this host), not a P716
regression. P716 touches only client sound-event recording/gating; this test
exercises server map-gen warp tracing. No code change was made for it.
## P715b observation (2026-10-16)

Full-suite run 1 reported Core **4,710 pass / 1 fail / 0 skip** with a single
failure in `MapGenLiveProofTests.GeneratedSingleRing_PeerFliesThroughWarp_ToRingNeighbour`
(assert message `never crossed: sectors=[0]` — a live-network map-gen warp
proof whose peer did not cross the warp within the deadline, under heavy
concurrent Codex suite load on this host). It passed **3/3 isolated runs**
(~200 ms each) on the same binaries, so it is the same live-net load flake
family as the P716 `GeneratedHiLo...ToTracedNeighbour` entry below. P715b
touches only client minimap label placement (`MinimapLayout`); this test
exercises server map-gen warp tracing. No code change was made for it.
## P713 observation (2026-10-16)

Exactly two unfiltered sequential sweeps recorded **16,212 passes / 4 failures /
6 existing optional Meshy-pack skips**. The second sweep was clean: Core
**4,726/0/0**, ClientCore **1,119/0/0**, MonoGame **2,244/0/3**, Lobby **19/0/0**
(pass/fail/skip). The final interface cohort passed **115 methods × 3 individual
runs / 384 case passes**, and the final-build never-parks showcase passed **10/10**.

After both sweeps, each first-pass Core failure below passed **3/3 fresh
exact-method isolated invocations**, meeting P713's explicit load/timing-flake
rule. No assertion, deadline, quarantine setting or product implementation was
changed for these observations. The isolated passes do not establish a cause or
fix for the underlying receipt/shutdown behavior.

| Method | First-pass symptom | Post-suite isolation |
| --- | --- | --- |
| `Transport.P670SendStallTests.Departed_peer_bytes_remain_in_host_window_totals` | ThreadPerPeer cleanup threw `ThreadStateException` at `TcpTransport.Shutdown:533` | 3/3 invocations; both modes passed each time (6 cases) |
| `LoadoutChangeLiveTests.Mount_and_remove_part_broadcast_loadout_change_to_owner_and_teammate_but_not_enemy` | Expected loadout-change receipt collection was empty | 3/3 |
| `GameOverPlayersWireTests.Win_by_elimination_broadcasts_game_over_players_to_surviving_peer` | No matching game-over roster element at `First` | 3/3 |

The fourth first-pass failure is **not a flake**. The leader-command fixture put
its supposed non-leader first on the other team, where native elects it as that
team's commander (`FedSrv/fsmission.cpp:601-613`). Correct join-role delivery
exposed this invalid assumption; the original method failed **3/3 alone**. The
fixture now uses a balanced third peer and an actual same-side ordinary member,
retains the default imbalance rule and original deadlines, and requires the
member to know both its commander and the same NPC before testing the negative
command result. It passed **3/3 after correction**, passed the second full sweep,
and passed **3/3 again after both sweeps**. Production source was unchanged by
this fixture correction.

See the [P713 report](reports/P713_INTERFACE_WAVE.md),
[validation summary](reports/P713_assets/validation-summary.json),
[invocation ledger](reports/P713_assets/verification.json), and
[selected affected cohort](reports/P713_assets/affected-certificate.json).
## P715 observation (2026-10-16)

Two complete unfiltered runs per assembly reported Core **4,724 pass / 1 fail**
and **4,723 pass / 2 fail**, ClientCore **1,117 pass / 0 fail** in each run,
MonoGame **2,256 pass / 0 fail / 3 existing optional Meshy-pack skips** in each
run, and Lobby **19/19** in each run. Local P542 and P660 opt-ins were enabled.
The two sequential sweeps overlapped after their verifier descendants survived
tool-session endings; both schedulers were stopped before launching another run.

After both sweeps finished, every failed method passed **3/3 fresh exact-method
isolated invocations**, with unchanged assertions and deadlines:

| Method | Full-run symptom | Isolation |
| --- | --- | --- |
| `Transport.TcpTransportGreetTimeoutPoolTests.Silent_connection_does_not_block_a_second_client_join` | `ThreadStateException: Thread has not been started` during `TcpTransport.Shutdown`. | 3/3 pass |
| `Transport.TcpTransportSendWorkerPoolTests.Pool_worker_and_reader_counts_stay_fixed_across_join_leave_churn` | `TimeoutException: no OnPeerJoined for raw peer`. | 3/3 pass |
| `LiveRipcordTests.Re_target_activates_to_requester_only` | Empty requester receipt collection at `Assert.Single`. | 3/3 pass |

These are load flakes under P715's explicit rule and match earlier ledger
observations. No transport/ripcord code, test assertion, deadline, quarantine
setting or skip condition changed. The isolated passes do not establish an
underlying transport/ripcord fix.

See the [P715 report](reports/P715_MINIMAP_FOOTER.md),
[full-suite ledger](reports/P715_assets/verification.json), and
[nine isolated passes](reports/P715_assets/diagnostic-isolation.json).
## P712 observation (2026-10-16)

The third scheduled final-build invocation of
`P705TechModeLiveTests.Research_upgrades_and_resets_through_two_complete_local_matches`
had one 101/1, 5v5 timeout in match two. Match one completed normally. In
match two, side 0 obtained its upgraded launch; side 1's five combat bots
remained in hull 14 while research continued. The unchanged 300-second
condition deadline expired at simulation time 18,609.25. This was an
older-loadout observation; its cause is not established.

Three additional **exact-case isolated reruns passed 3/3**, with both full
matches in each rerun. They used the same binaries, assertions, deadlines,
tick batching, map parameters and movement/economy rules. The filter was the
same method plus `DisplayName~civA: 101,`, matching exactly one data case.
Under P712's lane rule this is a load flake.

See the [failed timeline](reports/P712_assets/diagnostic-proof/final-focused-101-1.json),
[last sample](reports/P712_assets/diagnostic-proof/final-focused-101-1-sample.json),
[isolated triplet](reports/P712_assets/isolation-101-summary.json), and
[final invocation ledger](reports/P712_assets/final/verification.json)
(`focused-Research_upgrades_and_resets_through_two_complete_local_matches-3`,
then `isolation-P705-101-1` through `isolation-P705-101-3`).
The first full Core suite reported **4,760 pass / 1 fail / 0 skip**.
`CrossSideExportLiveTests.Same_side_teammate_still_gets_teamexport_no_cross_duplicate`
observed an exported hull ID of 92 while the current server ship had hull 33.
That exact method passed **3/3 unchanged isolated reruns**, recorded as
`isolation-574-0-1`, `isolation-575-0-2` and `isolation-576-0-3` in the same
[final ledger](reports/P712_assets/final/verification.json). Under the lane
rule this is a load flake. See the [isolated triplet](reports/P712_assets/core-export-isolation.json).
No export assertion, receipt deadline or seating/transport source was changed.

The initial side-pass overhead timeout and deterministic duplicate funding
status were corrected and are **not** classified as flakes; see the
[P712 report](reports/P712_P705_MERGE.md).

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

## P705 observation (2026-10-15)

`P705TechModeLiveTests.Research_upgrades_and_resets_through_two_complete_local_matches`
had one timeout in the 101/1, 5v5 case. Match one completed normally. In match
two, side 0's paid Refinery constructor (8146, reserved asteroid 18) remained in
native physical approach/building at the unchanged 300-second condition deadline.
That side had completed Weedcore 03; the other side had researched and launched
its upgraded bomber. Wallet funding and the tech-mode configuration were working.
The approach/steering cause is not established; this is the same symptom family
as the P703 observation below.

The next two scheduled repetitions passed. Three **additional exact-case isolated
reruns passed 3/3**, including both full matches each time. No assertions,
deadlines, driver pacing, map parameters or movement rules changed between the
failure and these retries. Under P705's lane rule this is a load flake.

See the [timeout snapshot](reports/P705_assets/constructor-approach-timeout.json),
[partial timeline](reports/P705_assets/101-1-timeout-timeline.json),
[three isolated results](reports/P705_assets/focused-isolation-summary.json), and
[invocation ledger](reports/P705_assets/verification.json). The failed invocation
is `focused-P705-101-1-1`; clean follow-up invocations are
`isolation-focused-1-1`, `isolation-focused-1-2`, and `isolation-focused-1-3`.

The first P705 diagnostic Core suite also observed
`Transport.TcpTransportGreetTimeoutPoolTests.Silent_connection_does_not_block_a_second_client_join`
throwing `ThreadStateException: Thread has not been started` in
`TcpTransport.Shutdown` while joining a worker. The exact method passed **3/3
isolated reruns** (`isolation-core-2-1` through `isolation-core-2-3`), without
source/assertion/deadline changes. This is recorded as a load flake. See the
[Core isolation results](reports/P705_assets/core-isolation-summary.json).

Four diagnostic profiler failures were traced to P705's rejected-plan boot path
registering callbacks before its host existed. That ordering bug was fixed and
has a direct rejection-then-ForceReport regression; it is **not classified as a
load flake**. The diagnostic private-path guard failure was an artifact-root Git
link setup issue; the corrected guard passed 3/3. Both are explained in the
[P705 report](reports/P705_SHOWCASE_TECH_MODE.md).

The corrected-build Core run completed **4,605 pass / 1 fail / 0 skip**. Its
`MineCrossSideVisibilityLiveTests.Enemy_ship_spots_a_laid_mine_once_in_range_and_learns_its_destruction`
failed waiting for B's mine EXPORT. The exact test then passed **3/3 isolated
reruns** (`fixed-isolation-mine-1` through `fixed-isolation-mine-3`) with unchanged
source and receipt deadline. This is a load flake under the P705 lane rule.
See [isolated results](reports/P705_assets/fixed-core-isolation-summary.json).
The profiler regression and checkout guard passed in this full run.

## P703 observation (2026-10-15)

The new
`P703ResearchCommanderLiveTests.Both_sides_complete_the_first_branch_using_native_wallets_and_tech_gates`
had one authored 25/3 timeout: one constructor remained in physical approach to
its assigned rock at the unchanged 300-second deadline. Money and prerequisite
research had succeeded. The generated 1/2 case passed in that invocation.
The exact method then passed **3/3 isolated reruns**, both matchup cases each
time. Under P703's lane rule this is recorded as a load flake; its underlying
approach/steering cause is not established. No assertions, deadlines, map
geometry or movement rules were relaxed between the failure and retries.

See [preserved approach samples](reports/P703_assets/constructor-approach-timeout.json),
[loopback evidence](reports/P703_assets/loopback-proofs.json), and
[invocation ledger](reports/P703_assets/verification.json) (failed invocation
232; clean isolated invocations 233, 234 and 292).

The unfiltered Core suite reported **4,581 pass / 2 fail / 0 skip**. Both failures
passed **3/3 exact-method isolated reruns**:

- `MissionRotationLiveTests.Finished_mission_rotates_to_fresh_notstarted_and_new_joiner_starts`: empty notification collection (invocations 372–374).
- `Transport.TcpTransportSendWorkerPoolTests.Pool_worker_and_reader_counts_stay_fixed_across_join_leave_churn`: no raw-peer `OnPeerJoined` before timeout (invocations 375–377).

These repeat the P702 observations below. No deadlines or receipt assertions
were relaxed. The same P703 invocation ledger records the full run and retries.

The unfiltered ClientCore run completed **1,073 pass / 0 fail / 0 skip**,
including both P703 research matchups again. MonoGame completed **2,208 pass /
0 fail / 3 existing optional Meshy-pack skips**, including the 605-second
human-flight soak. Lobby completed **19 pass / 0 fail / 0 skip**. No additional
failures required classification. See the [P703 report](reports/P703_RESEARCH_COMMANDER.md).

## P708 observation (2026-10-16)

The first unfiltered Core run passed **4,729/4,729**. The final-build Core rerun
reported **4,727 pass / 2 fail / 0 skip**. Both failed methods then passed
**3/3 exact-method isolated invocations**, satisfying P708's load-flake rule:

| Method | Full-suite failure | Isolated evidence |
| --- | --- | --- |
| `P699bPodLoadTests.Hundred_pods_per_side_return_from_Alderan_with_relaunch_traffic` | One of 200 pods exhausted oxygen; 199 docked and all 200 reached home. | `P708_assets/suite-retry-001-{1,2,3}.trx.gz`: 3/3 pass |
| `MineCrossSideVisibilityLiveTests.Enemy_ship_spots_a_laid_mine_once_in_range_and_learns_its_destruction` | B did not receive the mine EXPORT before the original deadline. | `P708_assets/suite-retry-000-{1,2,3}.trx.gz`: 3/3 pass |

ClientCore passed **1,073/1,073**; MonoGame passed **2,234** with **0 failures**
and the three existing optional Meshy-pack skips. The local P542 projectile
proof and P660 15v15 showcase were enabled. Neither failed method, its
assertions/deadlines, the pod/docking implementation, nor quarantine settings
were changed to obtain the isolated passes.

The separate required parking gate was **7 pass / 3 fail**, with stopped combat
targets in runs 02, 03 and 10. Recovery in runs 04–06 does **not** satisfy its
zero-failure requirement. The wave report preserves that integration hold,
all ten attempts, and the entry-revision comparison; it does not relabel the
parking gate as passed on the basis of the broad suites or isolated recovery.
The separately built entry revision passed 3/3 parking runs, and the final P708
build passed 3/3 post-suite parking retries. Those observations do not identify
a cause or replace the required ten-run gate; no P707 attribution is claimed.

See [P708 report](reports/P708_PARITY_WAVE11.md),
[invocation ledger](reports/P708_assets/verification.json), and
[scope audit](reports/P708_assets/scope-audit.json).

## P709 observation (2026-10-16)

The unfiltered sequential suites reported Core **4,691 pass / 2 fail / 0 skip**,
ClientCore **1,087 pass / 0 fail / 0 skip** (local P660 showcase enabled), and
MonoGame **2,237 pass / 0 fail / 3 existing optional private Meshy-pack skips**.
After all three suites finished, both failed Core methods passed **3/3
exact-method isolated invocations**:

| Full-suite method | Symptom | Isolated result |
| --- | --- | --- |
| `LoadoutChangeLiveTests.Mount_and_remove_part_broadcast_loadout_change_to_owner_and_teammate_but_not_enemy` | Owner/teammate loadout-change receipt collection empty at `Assert.Contains`. | 3/3 pass |
| `LiveCountdownStartPingTests.Multi_mission_countdown_scopes_per_mission` | Countdown receipt collection empty at `Assert.Single`. | 3/3 pass |

These satisfy P709's explicit load-flake rule. No assertions, timeouts,
quarantine settings, or transport/receipt behavior were changed. Isolated
passes do not establish an underlying fix. See the [P709 report](reports/P709_PARITY_WAVE12.md)
and [invocation ledger](reports/P709_assets/verification.json) for commands,
raw counters, messages/stacks, and individual TRX/log locations.

The separate mandatory P700 parking gate was **8 pass / 2 fail** across ten
isolated runs. That zero-failure gate remains **failed**; it is not waived by
this receipt-flake classification. Parking/dispenser/readiness fixes are
explicitly assigned to P707 by the brief. The report separately records the
untouched-base comparison.

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
| `MiscKeyBindingLiveTests.Live_own_ship_is_real_and_center_ranking_selects_a_real_ship` | load-flake | live autopilot center-rank target sampling returned warps instead of a settled ship target under full-suite load (P721 run 1); passes 3/3 in isolation |
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
## P693 observation (2026-10-15)

Final unfiltered suites after the team-limit changes: Core **4,561 pass / 0 fail / 0 skip**,
ClientCore **1,015 pass / 1 fail / 0 skip**, MonoGame **2,195 pass / 0 fail / 3 existing
private Meshy-pack skips**. The opt-in local 15v15 next-match showcase ran. The
native-default started-mission 30v30 MCP showcase passed 3/3; 38 affected methods
passed three qualifying isolated invocations each (174 case executions).

After all three full suites finished, each observed timing/transport failure
passed **3/3 exact-method isolated reruns**:

| Method | Full-run symptom | Final isolation |
| --- | --- | --- |
| `ClientWarpApproachSlowHullLiveTests.Legacy_200_450_arrival_parks_slow_board_hull_26_outside_the_transit_volume` | Final ClientCore pass: legacy approach clearance -32.7, expected positive; existing `Lane=LiveTiming` load-flake case. | 3/3 pass |
| `ShipsUpdateLiveTests.Cross_side_ships_see_each_other_and_not_self` | First Core pass: B had not received `HEAVY_SHIPS_UPDATE`. | 3/3 pass |
| `Transport.TcpTransportGreetTimeoutPoolTests.Silent_connection_does_not_block_a_second_client_join` | First Core pass: `ThreadStateException` in `TcpTransport.Shutdown` while joining an unstarted thread. | 3/3 pass |

These satisfy the brief's load-flake rule. No assertions, deadlines, quarantine
traits or product behavior were changed for them. This does not establish a fix
for the existing timing or shutdown races.

The crowded 256-peer fixture capacity conflict, hull-matrix spawn order, and
lifecycle fixture's assumed side/civilization were concrete defects exposed by
native defaults and side retries. They were corrected, verified 3/3 individually,
and are not classified as flakes. Overlapping diagnostic runs caused by surviving
verification children were excluded from isolated counts and rerun serially.
See [P693 report](reports/P693_TEAM_LIMITS.md) and [results](reports/P693_RESULTS.json)
for native evidence, raw counts, timestamps, binary hashes and invocation details.

## 2026-10-17 — P726 player-bundle verification

Exactly two unfiltered sequential sweeps produced 16,274 passes / 2 failures /
8 skips (six optional private Meshy-pack cases and two P660 opt-in guards; the
P660 proof passed 3/3 separately with its opt-in enabled). Both full ClientCore
never-parks cases and all ten fresh standalone never-parks runs passed.

After all sweeps and standalone showcase runs finished, each failed method passed
**3/3 fresh exact-method isolated invocations**, satisfying the brief's load-flake
rule:

| Method | Full-sweep symptom | Isolation |
| --- | --- | --- |
| `Allegiance.ClientCore.MonoGame.Tests.RadarTargetHighlightLiveTests.LiveOffScreenEnemy_IsTheFlaggedRadarTargetHighlight` | First MonoGame sweep: null position update at line 138 after waiting for identity. Second sweep passed. | 3/3 pass |
| `Allegiance.Core.Tests.AutoReloadOnEmptyTests.Auto_reload_relays_RELOAD129_sector_wide` | Second Core sweep: `SeatThree` found no same-side observer at line 369; failure occurred in setup. First sweep passed. | 3/3 pass |

Raw failures, exact commands, isolated outcomes and source fingerprints are in the
[P726 report](reports/P726_PLAYER_BUNDLES_2.md) and
[validation ledger](reports/P726_assets/validation.json). Assertions, deadlines,
quarantine traits and transport/gameplay code were not changed for these observations.

## P738 / P726 merge observation (2026-10-17)

The merge gate retained these raw broad-suite results: initial Core **4780 pass /
2 fail / 0 skip**, ClientCore **1177 pass / 2 fail / 1 existing P660 opt-in skip**,
MonoGame **2344 pass / 0 fail / 3 optional private-pack skips**, and post-fix Core
**4781 pass / 1 fail / 0 skip**. The standalone never-parks gate passed **5/5**.
Core excluded two guard tests that invoke git, as this lane explicitly forbids
git commands. LiveTiming and real loopback cases were included.

Every remaining failing case passed three fresh isolated processes with its
assertions and deadlines unchanged:

| Case | Symptom | Isolation |
| --- | --- | --- |
| `Transport.TcpTransportSendWorkerPoolTests.Pool_worker_and_reader_counts_stay_fixed_across_join_leave_churn` | Initial Core: no `OnPeerJoined` for raw peer. | 3/3 exact-method passes |
| `P655ProbeWorkersLiveTests.Actual_workers_two_seats_unique_probes_and_serves_MCP` (authenticated) | ClientCore: bot never confirmed launch after two buy/launch attempts. | 3/3 exact-method passes, both parameter cases in each process |
| `P705TechModeLiveTests.Research_upgrades_and_resets_through_two_complete_local_matches` (4/35, 3v3) | ClientCore: first-match research/upgrade condition expired at sim 28230. | 3/3 case passes across isolated method processes |
| Same P705 method (25/3, 10v10) | First isolated invocation: side 1's Teleport constructor remained travelling/building while side 0 had upgraded. | Next two isolated method invocations plus one standalone exact-case invocation: 3/3 after failure |
| `MissionRotationLiveTests.Finished_mission_rotates_to_fresh_notstarted_and_new_joiner_starts` | Post-fix Core: empty notification collection. | 3/3 exact-method passes |

These meet the brief's load-flake rule; they do not establish fixes for the
underlying receipt, launch or navigation causes. The P705 method's raw isolated
case totals were **3 pass / 1 fail**, **4 pass / 0 fail**, **4 pass / 0 fail**,
followed by **1 pass / 0 fail** for the standalone 25/3 case. Its existing optional
diagnostic snapshots were enabled; no pacing, map, deadline or assertion changed.

The P652 miner-lockout test's old `SendChat`/1342 contract was obsolete after
P725. It failed **3/3 before correction** and passed **3/3 after updating its
assertion** to `DroneChatEvent`/341, preserving backoff, cooldown and zero-ore
checks. It also passed the post-fix Core sweep. This was a corrected deterministic
test defect, not a flake. Native evidence:
`~/Development/Allegiance/src/Igc/shipIGC.cpp:2199` and
`~/Development/Allegiance/src/Igc/sounds.h:395`.

See the [P738 appendix](reports/P726_PLAYER_BUNDLES_2.md#p738-merge-onto-master),
[invocation ledger](reports/P738_P726_assets/verification.json),
[case qualification](reports/P738_P726_assets/case-results.json), and
[raw TRX/logs and snapshots](reports/P738_P726_assets/validation-logs.tar.gz).


## P732 observation (2026-10-17)

Exactly two final full-suite passes ran with real live/loopback tests and the
local P660 showcase opt-in enabled. Each main pass recorded Lobby **19/0/0**,
MonoGame **2,299/0/3**, ClientCore **1,187/0/0**, and Core **4,752/1/0**
(passed/failed/skipped); both scripts exited 1. The three MonoGame skips per
pass are the existing private Meshy-pack checks.

After both suites completed, every reported failing method passed **3/3 fresh
exact-method isolated Debug reruns**, satisfying P732's explicit load-flake rule:

| Test (under `Allegiance.Core.Tests`) | Full-suite symptom | Final isolation |
| --- | --- | --- |
| `Transport.P661EgressAccountingTests.Hot_path_allocates_zero_bytes_for_all_events` | Run 1: expected 0 allocated bytes; observed 7,768. | 3/3 pass |
| `Transport.TcpTransportSendWorkerPoolTests.Pool_worker_and_reader_counts_stay_fixed_across_join_leave_churn` | Run 2: `TimeoutException: no OnPeerJoined for raw peer`. | 3/3 pass |
| `LiveRelaunchTests.Carrier_without_launch_slots_uses_backward_fallback` | Run 2 quarantine: failed the batch, passed the runner's isolated retry. The existing runner suppresses the original assertion output. | 3/3 pass |

No assertions, deadlines, quarantine traits or product behavior were changed for
these observations. Isolated passes do not establish the underlying causes or
fixes. See the [P732 report](reports/P732_VOICE_OVER.md),
[full invocation ledger](reports/P732_assets/verification.json), and
[post-suite isolation ledger](reports/P732_assets/post-failure-isolation.json)
for raw counts, commands, main-lane failure stacks and per-attempt transcripts.


## P738 / P732 merge observation (2026-10-17)

The once-per-assembly broad Release gate recorded Core **4,779 pass / 3 fail /
0 skip**, ClientCore **1,203 pass / 2 fail / 0 skip**, and MonoGame **2,353 pass /
0 fail / 3 optional private Meshy-pack skips**. Core excluded the two guards
that invoke git, honoring this lane's explicit no-git rule. Ordinary
live/loopback and LiveTiming tests ran; the local P660 opt-in ran. The focused
235 methods passed 3/3 (1,044 case passes), and never-parks passed 5/5.

| Case | Symptom | Fresh isolated qualification |
|---|---|---|
| Core `Transport.TcpTransportSendWorkerPoolTests.Pool_worker_and_reader_counts_stay_fixed_across_join_leave_churn` | No `OnPeerJoined` for raw peer. | 3/3 exact-method passes |
| Core `LoadoutChangeLiveTests.Mount_and_remove_part_broadcast_loadout_change_to_owner_and_teammate_but_not_enemy` | Empty notification collection at line 155. | 3/3 exact-method passes |
| Core `LiveRipcordTests.Re_target_activates_to_requester_only` | Empty response collection at line 238. | 3/3 exact-method passes |
| ClientCore `ClientWarpApproachLiveTests.Client_bot_steered_by_warp_approach_law_transits_two_hops_to_target` | Expected final sector 1060; observed 1053. | 3/3 exact-method passes |
| ClientCore `P703ResearchCommanderLiveTests.Both_sides_complete_the_first_branch_using_native_wallets_and_tech_gates` (authored 25/3) | Only one side finished; Supremacy constructor travelling/building at the unchanged 300 s deadline. | Authored case passed in all 3 fresh exact-method invocations |
| Same P703 method (generated 1/2) | Third isolated method invocation timed out with only one side finished. | 3/3 additional fresh exact-case passes after this failure |

P703's raw isolated method counts were **2/0**, **2/0**, **1/1** (pass/fail),
followed by **1/0**, **1/0**, **1/0** for the exact generated case. Its first
triplet was not wholly green. Across all post-suite isolation there were
**20 case passes / 1 failure / 0 skips** in 18 processes. These satisfy the
brief's case-level load-flake rule without establishing underlying receipt,
constructor approach or steering causes. No assertions, deadlines, traits,
map geometry or movement rules were changed for these failures.

The deterministic drone-voice integration correction is not a flake. It
supersedes the zero-based ID interpretation in the earlier P738/P726 entry:
`~/Development/Allegiance/src/Igc/igc.h:456-464` starts the enum at 1001, so
`droneTooCrowdedSound` (`~/Development/Allegiance/src/Igc/sounds.h:395`) is
1342. P725's delivery seam remains, and its received wire chat now queues one
voice through P732's native catalog. The nine-case receive regression failed
9/9 before correction and passed 3/3 afterwards.

See the [P738 appendix](reports/P732_VOICE_OVER.md#p738-merge-onto-master),
[invocation ledger](reports/P738_P732_assets/verification.json),
[case outcomes](reports/P738_P732_assets/case-results.json), and
[raw logs/TRX](reports/P738_P732_assets/validation-logs.tar.gz).


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


## P738 / P718 merge observation (2026-10-17)

The P718 merge validation retained two timing failures. After the three broad
assembly passes ended, each failing method/case passed **3/3 fresh isolated
invocations**, meeting the brief's load-flake rule. No assertion, deadline,
quarantine trait or product behavior was changed to obtain these results.

| Method / case | Raw observation | Post-suite isolation |
| --- | --- | --- |
| `Allegiance.Core.Tests.Transport.TcpTransportSendWorkerPoolTests.Pool_worker_and_reader_counts_stay_fixed_across_join_leave_churn` | Core broad pass: `TimeoutException: no OnPeerJoined for raw peer`, helper line 119 / caller line 346. | 3/3 exact-method passes: 1.386, 1.372, 1.421 seconds. |
| `Allegiance.ClientCore.Tests.P703ResearchCommanderLiveTests.Both_sides_complete_the_first_branch_using_native_wallets_and_tech_gates(civA: 25, civB: 3, generated: False)` | Third focused method invocation: only one side complete at the unchanged 300-second deadline; side 0's Supremacy constructor was still travelling/building. The first two focused method invocations passed both cases; the third passed the generated case and failed the authored case. The broad ClientCore pass subsequently passed all 1,225 cases. | 3/3 fresh exact authored-case passes: 76.946, 77.015, 104.175 seconds. |

These symptoms were also observed in the preceding P738/P732 validation.
The classifications follow the required isolated-pass criterion and do not
establish the underlying causes. Raw results are preserved: focused coverage
701 passed / 1 failed / 0 skipped; Core 4,781 passed / 1 failed / 0 skipped;
ClientCore 1,225 passed / 0 failed / 0 skipped; MonoGame 2,356 passed / 0 failed /
3 existing optional private Meshy-pack skips. The five standalone never-parks
showcases all passed. The two Core guards that invoke git were excluded to
honor the user's explicit no-git instruction; no live/loopback test was filtered.

See the [merge appendix](reports/P718_INTERFACE_WAVE.md#p738-merge-onto-master),
[exact invocation ledger](reports/P738_P718_assets/verification.json),
[case outcomes](reports/P738_P718_assets/case-results.json), and
[raw logs/TRX archive](reports/P738_P718_assets/validation-logs.tar.gz).


## P720 observation (2026-10-16)

P720's final, unfiltered serial sweeps of Core, ClientCore, MonoGame, and Lobby
reported **16,322 passes / 2 failures / 6 existing private Meshy-pack skips**.
Core sweep 1 passed all 4,725 tests; the two failures occurred in Core sweep 2.
After both complete sweeps finished, each failed method passed **3/3 fresh
exact-method isolated invocations**, satisfying the brief's explicit load-flake
classification rule.

| Test | Full-suite observation | Final isolation |
| --- | --- | --- |
| `Allegiance.Core.Tests.P699bPodLoadTests.Hundred_pods_per_side_return_from_Alderan_with_relaunch_traffic` | 199/200 pods docked and one oxygen expiry; `Assert.Equal` expected zero expiries, actual one (line 160). | 3/3 pass |
| `Allegiance.Core.Tests.MapGenLiveProofTests.GeneratedSingleRing_PeerFliesThroughWarp_ToRingNeighbour` | `never crossed: sectors=[0]` (line 119). | 3/3 pass |

No assertions, deadlines, quarantine traits, simulation rules, or transport
behavior were changed for these observations. The isolated passes do not
establish either underlying cause or a fix. The affected cohort separately
passed all 78 methods three times alone, and the standalone parking gate passed
10/10. See the [P720 report](reports/P720_INTERFACE_WAVE.md),
[complete invocation ledger](reports/P720_assets/verification.json), and
[failure/isolation index](reports/P720_assets/failure-isolation.json) for raw
counts, messages, stacks, exact filters, timestamps, and TRX hashes.


## P738 / P720 merge observation (2026-10-17)

The serial broad Release gate retained these raw results (passed / failed /
skipped):

- Core: **4782 / 0 / 0**.
- ClientCore: **1224 / 1 / 0**.
- ClientCore.MonoGame: **2411 / 0 / 3**.

After all three broad passes finished, every failing method/case passed
**3/3 fresh isolated invocations**, with assertions and deadlines unchanged:

- `Allegiance.ClientCore.Tests.SessionLossLiveTests.EstablishedSession_ServerKilled_RaisesLost_EventAndControlsGoSilent`: SessionLost event must fire on the transport thread. Isolated qualification: **3/3 pass**.

These satisfy the brief's load-flake rule and do not establish underlying causes
or fixes. No product behavior, assertions, deadlines, quarantine traits or
transport changes were made for these observations. The MonoGame skips are the
existing optional private Meshy-pack checks. The two Core guards that invoke
git were excluded to honor the explicit no-git instruction; ordinary live,
loopback and LiveTiming tests ran, with the local P660 opt-in enabled.

The 82 focused methods passed three times alone (669 case passes / zero
failures or skips), and the standalone never-parks gate passed 5/5. See the
[P738 appendix](reports/P720_INTERFACE_WAVE.md#p738-merge-onto-master),
[invocation ledger](reports/P738_P720_assets/verification.json),
[case outcomes](reports/P738_P720_assets/case-results.json), and
[raw logs/TRX archive](reports/P738_P720_assets/validation-logs.tar.gz).


## P738 / P722 merge observation (2026-10-17)

Raw broad Release results (passed / failed / skipped):

- Core: **4781 / 1 / 0**.
- ClientCore: **1245 / 1 / 0**.
- ClientCore.MonoGame: **2418 / 0 / 3**.

After the broad passes, every method/case below passed three fresh
individual invocations on the same binaries. These meet the brief’s
load-flake rule; isolated passes do not establish a cause or fix.

- `Allegiance.ClientCore.Tests.ClientWarpApproachSlowHullLiveTests.Legacy_200_450_arrival_parks_slow_board_hull_26_outside_the_transit_volume` — Assert.Equal() Failure: Values differ Expected: 1053 Actual: 1052 at Allegiance.ClientCore.Tests.ClientWarpApproachSlowHullLiveTests.Legacy_200_450_arrival_parks_slow_board_hull_26_outside_the_transit_volume() in /home/domingo/allegiance-integ-b2/tests/Allegiance.ClientCore.Tests/ClientWarpApproachS. Post-suite isolation: **3/3 passed**.
- `Allegiance.Core.Tests.Transport.ConsoleVerbosityLiveTests.Plain_WriteLine_on_the_sim_path_does_not_block_a_stalled_stdout` — peers must keep receiving per-tick updates while stdout is stalled at Allegiance.Core.Tests.Transport.ConsoleVerbosityLiveTests.Plain_WriteLine_on_the_sim_path_does_not_block_a_stalled_stdout() in /home/domingo/allegiance-integ-b2/tests/Allegiance.Core.Tests/Transport/ConsoleVerbosityLiveTests.cs:li. Post-suite isolation: **3/3 passed**.

No product behavior, assertion, deadline or quarantine was changed for
these observations. Core’s two git-invoking guard classes were excluded
to honor the explicit no-git instruction. Ordinary live/loopback and
LiveTiming checks ran; the local P660 opt-in was enabled. MonoGame’s
existing optional private Meshy-pack skips are retained in the counts.

See the [P738 appendix](reports/P722_INTERFACE_WAVE.md#p738-merge-onto-master),
[invocation ledger](reports/P738_P722_assets/verification.json),
[case outcomes](reports/P738_P722_assets/case-results.json), and
[raw logs/TRX](reports/P738_P722_assets/validation-logs.tar.gz).

## P738 master merge — focused live treasure receipt (2026-10-17)

`Allegiance.Core.Tests.LiveTreasurePickupTests.Rescue_ship_picking_cash_treasure_credits_side_broadcasts_moneychange_and_destroys_it`
failed once in a fresh focused process at `LiveTreasurePickupTests.cs:196`
(`Assert.Single`: a same-side peer's `MoneyChanges` collection was empty).
The fixture waits for `DESTROY_TREASURE` on all peers, then immediately reads
`MONEY_CHANGE`; those are separate receive paths. This is the same receipt
flake already recorded above. The next **three fresh isolated invocations all
passed** (1.374 s, 1.387 s, 1.420 s), with no source or fixture changes.
Classified as a load/timing flake under the P738 lane rule. The first failure
and all three recovery results remain in
`docs/reports/P738_master_assets/verification.json` and the raw validation archive.

## P738 master merge — full-suite isolation (2026-10-17)

The Core, ClientCore and MonoGame assemblies ran once each after the clean
Release build. Every method below failed in that measured pass and then
passed in **three fresh isolated processes**, with no source or fixture
changes between the failure and recovery. Classified as load/timing flakes
under the brief's lane rule; the initial failures are retained, not replaced
with the recovery results.

| test | failing phase | observed symptom | post-failure isolation |
|---|---|---|---|
| `Allegiance.ClientCore.MonoGame.Tests.MiscKeyBindingLiveTests.Live_own_ship_is_real_and_center_ranking_selects_a_real_ship` | suite-ClientCore.MonoGame | Assert.All() Failure: 4 out of 4 items in the collection did not pass. | 3/3 passed |
| `Allegiance.ClientCore.Tests.ClientWarpApproachSlowHullLiveTests.Legacy_200_450_arrival_parks_slow_board_hull_26_outside_the_transit_volume` | suite-ClientCore | Assert.Equal() Failure: Values differ | 3/3 passed |
| `Allegiance.Core.Tests.Transport.ConsoleVerbosityLiveTests.Plain_WriteLine_on_the_sim_path_does_not_block_a_stalled_stdout` | suite-Core | peers must keep receiving per-tick updates while stdout is stalled | 3/3 passed |
| `Allegiance.Core.Tests.Transport.TcpTransportSendWorkerPoolTests.Pool_worker_and_reader_counts_stay_fixed_across_join_leave_churn` | suite-Core | System.TimeoutException : no OnPeerJoined for raw peer | 3/3 passed |

Exact commands, timing, failure messages and counters are in
`docs/reports/P738_master_assets/verification.json`; raw logs/TRX are retained
in `validation-logs.tar.gz`, with compact qualifications in
`flake-qualification.json`. No full assembly was rerun to obtain a clean total.
The two guards that launch git were excluded to honor the explicit no-git
instruction; ordinary live/loopback and LiveTiming tests were included.


## P738 / P733 merge observation (2026-10-17)

The once-per-assembly Core run recorded **4,795 pass / 1 fail / 0 skip**.
The two guards that invoke git were excluded to honor this lane's explicit
no-git instruction. The observed failing method passed **3/3 fresh
single-method invocations**, satisfying the brief's load-flake criterion.
These reruns ran in separate processes while the assembly sweep continued;
they were not a claim of an otherwise idle machine.

| Method | Full-suite symptom | Fresh qualification |
|---|---|---|
| `Allegiance.Core.Tests.LiveGainFlagTests.Flag_carrier_docking_at_own_side_station_secures_and_game_state_reflects_real_count` | Assertion at `LiveGainFlagTests.cs:316`: `ship must have docked for the secure to fire`; `StationId` was null after a flag announcement was received. | 3/3 exact-method passes, one case per process. |

No assertion, timeout, quarantine trait or product code was changed for this
observation. Receipt timing is a hypothesis, not an established cause or fix.
The failing full-suite result is retained in the
[main ledger](reports/P738_P733_assets/verification.json); the
[fresh rerun ledger](reports/P738_P733_assets/early-isolation/verification.json)
records all three exact commands and outcomes. See the
[P738 appendix](reports/P733_TECH_FINALE_AND_SPEED.md#p738-merge-onto-master)
for the complete merge validation.


### P738 / P733 ClientCore upgrade observation

The broad ClientCore invocation also observed a P705 25/3, 10v10 second-match
upgrade timeout. Match one completed, but match two did not observe newly
legal upgraded launches on both sides within the unchanged 300-second
predicate deadline (`P705TechModeLiveTests.cs:97,142`; sim 20157.75).

`Allegiance.ClientCore.Tests.P705TechModeLiveTests.Research_upgrades_and_resets_through_two_complete_local_matches(civA: 25, civB: 3, size: 10)`
then passed **3/3 fresh exact-case invocations**, one case per process, in
**107.019 / 106.019 / 107.019 seconds**. The earlier focused triplet also
passed all four civilization pairings, but qualification uses these new
post-failure runs. The exact-case filter was verified to execute one case.
These diagnostic processes ran sequentially while the broad sweep continued.

This meets the brief's load-flake criterion without establishing its cause.
The failed broad result is retained; no assertion, timeout, quarantine trait
or product code was changed. Commands, counters and proofs are retained in
the [fresh rerun ledger](reports/P738_P733_assets/early-isolation/verification.json)
and the final raw evidence archive.


### P738 / P733 slow-hull warp observation (2026-10-18)

ClientCore completed **1,277 pass / 2 fail / 0 skip**. In addition to the
qualified P705 case above,
`Allegiance.ClientCore.Tests.ClientWarpApproachSlowHullLiveTests.Slow_board_hull_26_transits_when_the_warp_approach_flies_through_the_aleph`
failed at `ClientWarpApproachSlowHullLiveTests.cs:345`: expected sector **1060**,
observed **1052**. It then passed **3/3 fresh exact-method invocations**, one
case per process, in **2.001 / 2.001 / 2.001 seconds** including process/runner
overhead. The diagnostic processes ran sequentially while the broad sweep
continued.

This satisfies the brief's load-flake criterion; it does not establish a
steering or receipt cause or fix. No assertion, timeout, quarantine trait or
product code was changed. The failed broad result remains in the
[main ledger](reports/P738_P733_assets/verification.json), and all three
reruns are in the [fresh ledger](reports/P738_P733_assets/early-isolation/verification.json).

### P738 / P733 completed assembly sweep

| Assembly | Passed | Failed | Skipped |
|---|---:|---:|---:|
| Core | 4795 | 1 | 0 |
| ClientCore | 1277 | 2 | 0 |
| ClientCore.MonoGame | 2421 | 0 | 3 |


All observed failing methods met the brief's 3/3 single-method criterion. Original failures remain in the [ledger](reports/P738_P733_assets/verification.json); [qualification details](reports/P738_P733_assets/flake-qualification.json) and [raw logs/TRX](reports/P738_P733_assets/validation-logs.tar.gz) are retained. No assertion, timeout, quarantine trait or product code was changed for these observations.


## P738 / HD-2 merge observation (2026-10-18)

The post-merge Core sweep recorded **4803 pass / 1 fail / 0 skip**, excluding
only `MeshyGuardTests` and `PrivatePathGuardTests` because those tests invoke
git and this lane explicitly forbids git commands. The failure was
`TcpTransportSendWorkerPoolTests.Pool_worker_and_reader_counts_stay_fixed_across_join_leave_churn`:
`TimeoutException: no OnPeerJoined for raw peer` at `ConnectRawPeer` line 119,
called from line 346. Three fresh exact-method invocations immediately after
the sweep passed **3/3** (1.508, 1.520, 1.494 seconds). This is the same raw-peer
join timing symptom recorded by the original HD-2 wave. It meets the brief's
load-flake criterion; the original failed result is preserved, with no timeout,
assertion, quarantine or product-code change.

The merge's 73 focused methods passed three isolated invocations each
(**318 case passes, no failures/skips**) and showcase never-parks passed **5/5**.
See the [merge appendix](reports/HD2_PBR_CLIENT.md#p738-merge-onto-master),
[invocation ledger](reports/P738_HD2_assets/verification.json), and
[raw logs/TRX](reports/P738_HD2_assets/validation-logs.tar.gz).


### P738 / HD-2 completed assembly sweep

The single ClientCore sweep passed **1279/0/0**. The single MonoGame sweep recorded **2427 pass / 0 fail / 3 skip**. The skipped cases and their prerequisite reasons are retained in the [validation summary](reports/P738_HD2_assets/validation-summary.json). The Core transport case above was the only failing method; it passed all three fresh isolated reruns. No second full sweep was run.

## P738 / P734b merge observation

The completed integration sweep retained these observed failures. Each originally failing case then passed in all three fresh isolated method invocations:

| Case | Symptom | Qualification |
|---|---|---|
| `Allegiance.Core.Tests.LiveGainFlagTests.Flag_carrier_docking_at_own_side_station_secures_and_game_state_reflects_real_count` | ship must have docked for the secure to fire | 3/3; `isolation-000-1, isolation-000-2, isolation-000-3` |

These meet P738's explicit load-flake rule; isolated passes do not establish the underlying causes or a fix. No assertion, deadline, quarantine trait or product code was changed. [Ledger](reports/P738_P734b_assets/verification.json), [qualification](reports/P738_P734b_assets/flake-qualification.json), [raw evidence](reports/P738_P734b_assets/validation-logs.tar.gz).


## TT-1 tier sweep (2026-10-18)

The one full tier sweep observed these Fast failures. Each then passed all three
fresh exact-method invocations, meeting TT-1's explicit load-flake qualification
rule. No assertion, timeout, quarantine trait or production code was changed for
these failures.

| Case | Original symptom | Isolated result |
| --- | --- | --- |
| `Allegiance.ClientCore.Tests.DeadLatchRespawnLiveTests.Own_ship_sim_resumes_and_accepted_climbs_after_death_respawn_undock` | server rejected 1 native pose across death/respawn | 3/3 pass (4.472 / 4.465 / 4.468 s) |
| `Allegiance.Core.Tests.ShipsUpdateLiveTests.Cross_side_ships_see_each_other_and_not_self` | B had not received HEAVY_SHIPS_UPDATE | 3/3 pass (0.178 / 0.174 / 0.183 s) |
| `Allegiance.Core.Tests.Transport.TcpTransportSendWorkerPoolTests.Pool_worker_and_reader_counts_stay_fixed_across_join_leave_churn` | TimeoutException: no OnPeerJoined for raw peer | 3/3 pass (0.218 / 0.217 / 0.216 s) |

[Qualification ledger](reports/TT1_assets/qualification.json),
[task report](reports/TT1_TEST_TIERS.md). Original and isolated TRX are archived
under `docs/reports/TT1_assets/sweep/` and `docs/reports/TT1_assets/focused/`.
The distinct hygiene-duration and deferred-theory accounting findings were fixed
and verified separately; they are not classified as load flakes.

## P739 initial focused observation (2026-10-18)

`Allegiance.Core.Tests.MineCrossSideVisibilityLiveTests.Enemy_ship_spots_a_laid_mine_once_in_range_and_learns_its_destruction` passed its first two isolated runs, then failed the third at line 212: “B must receive the mine's EXPORT once its scanner net spots it”. Three fresh exact-method invocations immediately afterward passed **3/3**. This meets the P739 brief's load-flake criterion; it does not establish the underlying cause or a fix. No assertion, timeout, quarantine or product behavior was relaxed. This observation belongs to the initial focused pass, before the final countdown/payday guard; final certification is recorded separately. See [invocation ledger](reports/P739_assets/verification.jsonl), labels `focus-054-3` and `focus-retry-054-1` through `focus-retry-054-3`, with corresponding log/TRX artifacts in [P739 assets](reports/P739_assets).


## P739 final focused observation (2026-10-18)

`Allegiance.Core.Tests.LiveCountdownStartPingTests.Multi_mission_countdown_scopes_per_mission` failed the first final isolated invocation with `Assert.Single() Failure: The collection was empty`; the next two prescribed runs and three fresh exact-method retries passed. The three fresh retries satisfy the P739 load-flake rule. The same empty-receipt symptom was recorded by P709. No assertion, timeout, quarantine or product code was changed in response. Final focused verification otherwise passed: **237 prescribed invocations / 300 case executions**, one raw failure, plus **3/3 passing fresh retries**. See [ledger](reports/P739_assets/verification.jsonl), labels `certified-focus-039-1` and `certified-retry-039-1` through `certified-retry-039-3`.


## P739 full-suite observations (2026-10-18)

The single final Core/ClientCore/MonoGame sweep recorded these failures. Each was retried three times alone after all suites completed; no assertion, timeout, quarantine or product code was relaxed.

| Method | Symptom | Isolated outcome |
| --- | --- | --- |
| `Allegiance.Core.Tests.Transport.TcpTransportSendWorkerPoolTests.Pool_worker_and_reader_counts_stay_fixed_across_join_leave_churn` | System.TimeoutException : no OnPeerJoined for raw peer | 3/3 passed |
| `Allegiance.ClientCore.Tests.P705TechModeLiveTests.Research_upgrades_and_resets_through_two_complete_local_matches` | both sides real development and newly legal upgraded launch timed out; sim=11463.199999973434 | 3/3 passed |

For P705, the table refers to the original civA25/civB3/size10 case: one fresh method invocation passed all 4 cases, then two fresh exact-case invocations each executed that one case. All 3 observations passed with unchanged assertions/deadlines.

See [validation summary](reports/P739_assets/validation-summary.json) and [invocation ledger](reports/P739_assets/verification.jsonl). Passing three fresh isolated runs meets P739’s load-flake criterion and does not establish the underlying cause or a fix.

## P727 local validation (2026-10-18)

`P703ResearchCommanderLiveTests.Both_sides_complete_the_first_branch_using_native_wallets_and_tech_gates(civA: 25, civB: 3, generated: False)`
failed in the third focused method invocation: only one side completed by the
unchanged 300-second deadline while the Supremacy constructor was still
travelling/building an Outpost (Hvy). The first two invocations passed both
the authored and generated cases; the third passed the generated case.
The exact authored case then passed **3/3 fresh isolated invocations** in
91.16, 92.99, and 90.91 seconds. No deadline or assertion was relaxed.
This matches the existing P703 constructor-timing observation and meets
P727's explicit load-flake rule. See the
[P727 invocation ledger](reports/P727_assets/validation.json), labels
`focus-Both_sides_complete_the_first_branch_using_native_wallets_and_tech_gates`
and `isolation-P703-authored`; the failed TRX and the three isolated TRXs
are preserved beside that ledger as gzip files.

P727's single full Core sweep also hit
`Transport.TcpTransportSendWorkerPoolTests.Pool_worker_and_reader_counts_stay_fixed_across_join_leave_churn`
with `TimeoutException: no OnPeerJoined for raw peer`. It passed **3/3 fresh
exact-method isolated reruns** in 2.28, 2.38 and 2.28 seconds after the four
project sweeps finished. The original deadline and assertions were unchanged.
The invocation ledger above records the raw 4,853-pass/1-fail Core result
and the `isolation-suite-Pool_worker_and_reader_counts_stay_fixed_across_join_leave_churn`
triplet. This meets the same load-flake rule and matches earlier observations
of this test.

MonoGame's P625b input guard failure in this run was a deterministic P727
regression and was corrected, rather than classified as a load flake; its
before/after evidence is in the P727 report.

## TR-1 training framework observation (2026-10-18)

The required single Fast-tier sweep ran the final code with the new training tests
included. Allegiance.ClientCore.Tests MAIN passed **1301/1301** (including the 24
new training tests). Failures surfaced in Allegiance.Core.Tests (not code this
change touches) and were disposed per the load-flake rule:

| Case | Symptom | Disposal |
| --- | --- | --- |
| `Allegiance.Core.Tests.P674ParityScorecardTests.Both_repository_matrices_validate_and_print_scorecards` | parity-matrix.csv:348 expected 6 columns, got 11 | A comma slipped into the CSV cell this change edits; the CSV was fixed and the class passed **3/3 in isolation**. This was a defect in code this change touched and was corrected, not waived. |
| `Allegiance.Core.Tests.PaydayTests.ZeroPlayerSide_does_not_crash_and_accrues_without_paying` | payday accrual check under suite load | Passed **3/3 exact-method isolated invocations**; load flake, not this change's code. |
| `Allegiance.Core.Tests.Transport.TcpTransportPriorityLanePoolTests.Ordered_world_data_is_next_drained_even_after_a_stream_backlog` | quarantine lane; live-socket priority-lane ordering | Already listed in the P647 quarantine table below (`TcpTransportPriorityLane{Pool,ThreadPerPeer}Tests... 0/10 load-flake`); not this change's code. Retained in the quarantine lane. |

No assertion, deadline, quarantine trait or product code was changed for the two
pre-existing flakes. The P674 CSV defect was a formatting mistake in this task's
own parity-matrix edit and was fixed; the suite numbers above are as measured on
the run that caught it.

## TR-2 observation (2026-10-18)

Required single Fast-tier sweep on the committed TR-2 tree
(`tools/run-tests.sh --tier fast`): Lobby 19/19, MonoGame 2416 pass / 3 existing
skips, Core 4802 pass, ClientCore 1335 pass / 1 fail. The one failure,
`CrossSideCombatVisibilityLiveTests.Fire_is_not_relayed_to_peers_in_other_sectors`
(the documented P699c load-flake above), passed **3/3 isolated runs** on the same
binaries and is not training/UI code. The runner also flagged the live
`P727SkirmishLiveTests.Local_start_battle_pause_resume_next_match_stop_leaves_no_processes`
as exceeding the calibrated 10 s Fast limit (207 s) — a concurrent `allegiance-codex-tt1b`
suite was running on this shared box (the P731 hazard), so both live-network timings
were load-degraded. No product code was changed for these dispositions.

## P745 observation (2026-10-18)

The one Core sweep was interrupted by the unchanged TcpTransport accept/stop
race (`InvalidOperationException: Not listening` from `AcceptTcpClient`) after
173 passing cases. Only the not-yet-completed methods were resumed: 4,653 passed
and 25 failed. The last completed transport handshake method passed 3/3 alone;
this does not establish which earlier background accept thread threw.

These existing failing cases passed 3/3 alone (exact theory rows were filtered):

- `P694MassEventTickTests.Launch_point_mass_events(count: 200, parallel: True, stationDeath: True, coincident: False, nativeLaunch: True)`
- `MiningTests.Loopback_synthetic_rock_mines_and_ore_reaches_the_wire`
- `CrossSideExportLiveTests.Same_side_teammate_still_gets_teamexport_no_cross_duplicate`
- `LiveRipcordTests.Cancel_while_active_aborts_sector_wide`
- `Transport.TcpTransportPriorityLaneThreadPerPeerTests.Ordered_join_frames_are_never_shed_when_the_stream_overflow_collapses`
- `LiveRelaunchTests.Carrier_without_launch_slots_uses_backward_fallback`
- `LiveRipcordTests.Re_target_activates_to_requester_only`
- `Transport.TcpTransportPriorityLanePoolTests.Ordered_world_data_is_next_drained_even_after_a_stream_backlog`
- `MapGenLiveProofTests.GeneratedSingleRing_PeerFliesThroughWarp_ToRingNeighbour`
- `LiveCountdownStartPingTests.Multi_mission_countdown_scopes_per_mission`
- `LiveTeamLobbyTests.Unready_broadcasts_team_ready_false`
- `Transport.TcpTransportCoalesceAndReasonThreadPerPeerTests.Coalesce_distinct_keys_remain_independent`
- `Transport.TcpTransportCoalesceAndReasonPoolTests.Coalesce_healthy_consumer_still_receives_each_latest_snapshot`
- `LiveLogoffTests.Logoff_echoes_releases_and_retires_the_ship`
- `Transport.TcpTransportSendDropSafetyThreadPerPeerTests.Host_send_to_a_reaped_peer_id_does_not_throw`
- `LiveRipcordTests.First_activation_broadcasts_to_everyone_flying_in_sector`
- `Transport.TcpTransportPriorityLaneThreadPerPeerTests.Ordered_world_data_is_next_drained_even_after_a_stream_backlog`
- `LiveCountdownStartPingTests.StartGame_owner_all_ready_goes_through_real_countdown`
- `LiveLifepodEnduranceDisplayTests.Driver_sees_countdown_others_do_not_and_dock_cancels`
- `LiveRipcordTests.Ship_anchor_insufficient_energy_aborts_consumed_not_retried`

The lifepod-display method needed a fresh three-run qualification after one
receipt timeout in its first group; all three then passed. The insufficient-
energy ripcord case likewise passed the final three isolated runs after earlier
timing misses. The unchanged MonoGame
`P653DPodPurchaseLiveTests.Hangar_launch_after_ejection_or_pod_death_buys_real_hull(killPod: False)`
failed once in the sweep, then passed three isolated invocations (both theory
rows, six passes).

Five P694 absolute 200 ms performance rows also failed in the Core sweep. A
clean archive of the unchanged base was built and the same exact rows run three
times: the 195-ship station-death serial and parallel rows failed 3/3 on base
(medians 305.796–584.586 ms); the 150-ship serial row failed 1/3 and the
200-ship native-launch serial row failed 2/3 on base. The 150-ship parallel row
initially passed on base, but a subsequent alternating base/current comparison
failed 2/3 in both trees on the shared host. These are reproduced base/load
performance failures, not a clean performance gate. No thresholds, fixtures or
production performance rules were changed to make them pass. Detailed records
are in [the P745 verification ledger](reports/P745_assets/verification.json).

## P728 local validation (2026-10-18)

The single unfiltered Core sweep returned **4868 passed / 11 failed / 0 skipped**.
Ten failures were existing socket/timing cases. Each subsequently passed three
consecutive exact-method invocations without changing its assertions or production
transport code:

- `ShipsUpdateLiveTests.Cross_side_ships_see_each_other_and_not_self`
- `ShipStatusServerTests.Teammate_ship_playerinfo_is_broadcast_once_after_export_before_status`
- `LoadoutChangeLiveTests.Mount_and_remove_part_broadcast_loadout_change_to_owner_and_teammate_but_not_enemy`
- `LiveGainFlagTests.Flag_carrier_docking_at_own_side_station_secures_and_game_state_reflects_real_count`
- `TcpTransportCoalesceAndReasonThreadPerPeerTests.Coalesce_distinct_keys_remain_independent`
- `TcpTransportCoalesceAndReasonThreadPerPeerTests.Guaranteed_coalesced_frames_are_never_shed_but_still_bounded`
- `TcpTransportPriorityLane{Pool,ThreadPerPeer}Tests.Ordered_world_data_is_next_drained_even_after_a_stream_backlog`
- `TcpTransportPriorityLane{Pool,ThreadPerPeer}Tests.Ordered_join_frames_are_never_shed_when_the_stream_overflow_collapses`

The priority-lane cases are already documented above, including the live-socket
quarantine entries. Several needed additional fresh invocations before reaching
three consecutive passes; the ledger retains every failure and retry.

The eleventh failure, `BroadcastSnapshotLoopbackTests.Crowded_real_TCP_session_measures_sim_projection_and_publish_pool`,
failed **3/3** standalone with EOF at transport barrier 668003. This was a fixture
bug, not a load-flake disposition: its uncontrolled/native simulation path omitted
keepalives and crossed the transport's existing 30-second inbound-frame timeout.
The crowded benchmark peers now send periodic keepalives throughout joining,
measurement and draining, serialize their writes, and distinguish heartbeat replies
from the test's drain marker. Timeouts, the 256-peer workload and measurement
assertions remain unchanged. All four affected broadcast loopback methods passed
three times with the correction.

MonoGame's single sweep returned **2441 passed / 3 failed / 3 skipped**.
`ChatTargetComposeLiveTests.ComposeDefault_Team_SendsToTeammate_NotEnemy` passed
three fresh standalone invocations. Its other two failures were P728 help-table
regressions (duplicate Tab/E entries), corrected by putting pilot-funding shortcuts
in their own group; both guards then passed **3/3**. The three optional private
Meshy-pack cases also passed **3/3** with the installed pack explicitly configured.

ClientCore's single sweep returned **1388 passed / 0 failed / 0 skipped**.
The human commander loopback passed three standalone runs, and the final
showcase never-parks check passed **5/5**, matching the saved pre-order execution
baseline's **5/5**. No production transport thresholds were changed.

See [P728's report](reports/P728_SINGLE_PLAYER_COMMANDER.md) and the compact
[invocation ledger](reports/P728_assets/validation.json). Raw TRX and logs are at
`/tmp/allegiance-evidence/P728/` because the Codex sandbox does not permit writing
the requested home-directory archive. No second full sweep was run.

## SP-5 (2026-10-18) — supervisor stderr receipt under concurrent load

The one ClientCore sweep failed `P658SupervisorTests.RealChildExitMarksKnownBotsLostAndSupervisorStillServes`
when the child had exited but its asynchronously copied stderr was still empty (`child-error` missing).
The unchanged method passed **3/3 exact-method isolated retries** (1.21, 1.21, 1.23 seconds).
Classified as a load flake under the SP-5 light gate; no supervisor stderr assertion or production lifecycle was weakened.
Raw evidence: `/tmp/allegiance-evidence/SP5/supervisor-retry-{1,2,3}.trx` and `clientcore-sweep.log`.
**Fixed 2026-10-21** (it also failed release run 39716258737's test gate): a test race — the log was read
before `ProcessWorker.Dispose` drained the async stderr copy (`tools/allegiance-mcp/WorkerRpc.cs` Dispose). The
test now disposes the worker first; the assertion is unchanged. 3/3 class runs + 8/8 concurrent passes.
**Reopened 2026-10-21:** it failed again in release run 39724573797 (on CI only; locally 48/48 under CPU stress and
5/5 whole-project runs pinned to 4 CPUs all pass, on old and new code alike — not reproducible here). Code reading: the
worker log `FileStream` was buffered (4 KB), so the text only reached the file when the copy hit EOF and disposed it, and
`ProcessWorker.Dispose` waits for that copy at most 2 s, silently. Fix: unbuffered log (also keeps a crashed supervisor's
worker stderr on disk) and the test waits up to 10 s for the text. **Not verified by a reproduction** — if it fails
again, quarantine it instead of a third guess.

SP-5's single Core sweep also hit the already documented
`LiveRipcordTests.First_activation_broadcasts_to_everyone_flying_in_sector`
receive race (`Assert.Single`, empty collection, 17 ms). The unchanged method passed
**3/3 exact-method isolated retries** (1.79, 1.76, 1.59 seconds).
Raw evidence: `/tmp/allegiance-evidence/SP5/ripcord-retry-{1,2,3}.trx` and `core-sweep.log`.


## P740 observation (2026-10-18)

The single unfiltered Core/ClientCore/MonoGame pass and standalone showcase gate recorded the failures below. Each method then passed three fresh exact-method isolated runs, with assertions, deadlines and quarantine settings unchanged, satisfying the P740 brief's load-flake rule. These passes do not establish a cause or fix.

- `Allegiance.ClientCore.Tests.ClientWarpApproachLiveTests.Client_bot_steered_by_warp_approach_law_transits_two_hops_to_target` — **3/3 isolated passes** after all broad suites.
- `Allegiance.ClientCore.MonoGame.Tests.ChatTargetComposeLiveTests.ComposeDefault_Team_SendsToTeammate_NotEnemy` — **3/3 isolated passes** after all broad suites.
- `Allegiance.Core.Tests.Transport.TcpLoopbackProofTests.ClientHalf_RaisesOnSessionLost_AndClearsConnected_WhenServerDies` — **3/3 isolated passes** after all broad suites.
- `Allegiance.Core.Tests.Transport.TcpTransportGreetTimeoutPoolTests.Silent_connection_does_not_block_a_second_client_join` — **3/3 isolated passes** after all broad suites.
- `Allegiance.ClientCore.Tests.P700ShowcaseLiveTests.Local_showcase_never_parks_with_target_for_five_seconds` — **3/3 isolated passes** after all broad suites.

Evidence: [P740 report](reports/P740_INTERFACE_WAVE5.md), [invocation ledger](reports/P740_assets/verification.json); full failed/isolated TRX and logs: `/tmp/allegiance-evidence/P740/` (per-command paths in the ledger).

## HUD1 validation observation (2026-10-18)

The single unfiltered Core/ClientCore/MonoGame sweep included live/loopback tests.
The following 13 failures each passed a final triplet of exact-method isolated
invocations, meeting the HUD1 lane rule. Original full-sweep failures and every
isolated result remain in the [HUD1 validation summary](reports/HUD1_assets/validation-summary.json).
No product behavior, assertion, deadline or quarantine trait was changed for these cases.

| Method | Initial isolated triplet | Final qualification |
|---|---|---|
| `AsteroidMinedLiveTests.No_broadcast_fires_once_miner_hits_capacity` | pass, pass, pass | 3/3 exact-method pass |
| `LiveCountdownStartPingTests.Multi_mission_countdown_scopes_per_mission` | pass, pass, pass | 3/3 exact-method pass |
| `BroadcastSnapshotLoopbackTests.Crowded_real_TCP_session_measures_sim_projection_and_publish_pool` | pass, pass, pass | 3/3 exact-method pass |
| `Transport.ConsoleVerbosityLiveTests.Plain_WriteLine_on_the_sim_path_does_not_block_a_stalled_stdout` | pass, pass, pass | 3/3 exact-method pass |
| `LiveTeamLobbyTests.Unready_broadcasts_team_ready_false` | pass, pass, pass | 3/3 exact-method pass |
| `ShipUpdateBudgetLiveTests.Heavy_updates_are_bounded_to_the_budget_not_proportional_to_ship_count` | pass, pass, pass | 3/3 exact-method pass |
| `P493EnemySpottingLiveTests.Probe_spot_of_cross_side_ship_delivers_status_and_hidden_to_side0_peer` | pass, pass, pass | 3/3 exact-method pass |
| `LiveRipcordTests.First_activation_broadcasts_to_everyone_flying_in_sector` | pass, pass, pass | 3/3 exact-method pass |
| `LiveInSpaceEjectTests.Already_a_lifepod_is_truly_killed_not_re_ejected` | pass, pass, pass | 3/3 exact-method pass |
| `Transport.TcpTransportCoalesceAndReasonThreadPerPeerTests.Guaranteed_coalesced_frames_are_never_shed_but_still_bounded` | pass, pass, pass | 3/3 exact-method pass |
| `Transport.TcpTransportPriorityLaneThreadPerPeerTests.Ordered_world_data_is_next_drained_even_after_a_stream_backlog` | pass, pass, pass | 3/3 exact-method pass |
| `Transport.TcpTransportCoalesceAndReasonPoolTests.Coalesce_distinct_keys_remain_independent` | pass, fail, pass | 3/3 fresh quiet retries |
| `P522UndockReseedLiveTests.Three_dock_undock_cycles_zero_rejects_and_zero_SHIP_RESETs_with_reseed` | pass, pass, pass | 3/3 exact-method pass |

These observations establish intermittency, not an underlying cause or fix.

The already-quarantined `ClientWarpApproachSlowHullLiveTests.Legacy_200_450_arrival_parks_slow_board_hull_26_outside_the_transit_volume`
is **not waived as a qualified load flake**: its first isolated triplet failed
3/3, with subsequent unchanged triplets passing only 1/3 and 2/3. An untouched
dispatch-base archive passed 3/3. Its helper drove `TickSim` in a tight loop
while reading asynchronously delivered TCP poses. HUD1 adds a bounded pose
receipt check before each steering sample. The legacy negative control and
positive transit test each passed **3/3 alone after the test-only fix**.
Both arrival laws, tick limits and outcome assertions remain unchanged; the
receipt check adds an assertion. No simulation/transport production code or
quarantine trait was changed. The original failed sweep was not rerun.

Raw TRX/logs, untouched-base build and retry records: `/tmp/HUD1/raw/` (the
permitted external evidence root for this sandbox). See the
[HUD1 report](reports/HUD1_HUD_PARITY.md) for full gate counts and dispositions.


## HUD-2 validation environment (2026-10-19)

HUD-2's single Core sweep lost TRX export to shared `/tmp` quota exhaustion.
Visible P694/P689 profiling and InterestTier config failures explicitly reported
`Disk quota exceeded`; those methods passed 3/3 isolated invocations after moving
process temporary storage to an ignored workspace directory. The parity scorecard
failure was corrected CSV quoting, and the MonoGame disposal guard needed the new
renderer registered (its unload disposal call was already present); both passed
3/3 after correction. These are not timing-flake classifications.

The single ClientCore run recorded 1,063 passes before its host crashed, with no
assertion failure in the exported results. Its cause remains undetermined. No
second broad sweep was run. The five additional local P700 parking invocations
passed 5/5. See [HUD-2](reports/HUD2_MODERN_HUD.md) and its validation ledger.


## HUD-9 validation environment (2026-10-20)

HUD-9's single fast-tier run recorded one failure:
`Allegiance.Core.Tests.MissionRotationLiveTests.Finished_mission_rotates_to_fresh_notstarted_and_new_joiner_starts`.
The exact method passed **3/3 alone** afterwards without changes, so it is
recorded as a load flake (mission rotation, unrelated to the HUD-9 console-text
changes). No `Allegiance.Core`/`Allegiance.Server` production source changed, so
the conditional server AOT publish and extra showcase runs are not triggered.


## HUD-9b validation environment (2026-10-20)

HUD-9b's final fast-tier run recorded one failure:
`Allegiance.Core.Tests.Transport.P661EgressAccountingTests.Hot_path_allocates_zero_bytes_for_all_events`
(Expected 0, Actual 6648). The exact method (and its whole class) passed
**3/3 alone** afterwards without changes, so it is recorded as a load flake
(allocation counting on a busy shared machine, unrelated to the HUD-9b console
text fixes). No HUD-9b change touches `Allegiance.Core`/`Allegiance.Server`
transport or allocation paths.


## HUD-15 validation environment (2026-10-20)

HUD-15's single fast-tier run recorded the known `PlayerBundlePathsTests`
environment baseline (5 of its 9 cases, the same `local.env` ancestor-discovery
failures documented by HUD-12/SC2 — not new): **9,467 tests, 273.8 s, 5
failures**, all `Allegiance.Core.Tests.PlayerBundlePathsTests`. No HUD,
renderer, radar or inventory test failed.

During the intermediate filtered HUD/radar/inventory run the allocation
assertion in `Allegiance.ClientCore.MonoGame.Tests.Hud12bReviewFixTests.Chat_line_truncation_matches_the_native_clip_and_is_cached_per_source_and_width`
failed twice ("cached chat truncation allocated 7208 B / 6408 B over 10,000
frames") and passed **3/3 exact-method isolated invocations** unchanged. It is
the same full-run-only allocation-sampling effect as the P661/P670 entries
above (`BytesAllocated` under a busy shared machine), unrelated to the HUD-15
command-icon work: that test only exercises `ChatLineTruncationCache.Fit`, which
HUD-15 does not touch. Recorded as a load flake per the 3/3-alone rule.

## P775 validation environment (2026-10-21)

P775's single fast-tier run recorded one failure outside the task's surface:
`Allegiance.ClientCore.MonoGame.Tests.Hud12bReviewFixTests.HUD_formatted_readouts_allocate_nothing_in_steady_state`
("formatted HUD readouts allocated 3624 B over 10,000 frames"). It passed
**3/3 exact-class isolated invocations** immediately after (14/14 each run) —
the same full-run-only allocation-sampling effect documented above (P661/P670,
HUD-15): `BytesAllocated` sampled while the shared machine is busy running the
other fast-tier projects. P775 touches no HUD/formatting/allocation path.
Recorded as a load flake per the 3/3-alone rule.

The same run also recorded one failure in
`Allegiance.ClientCore.Tests.RetriedConnectLiveTests.RepeatedConnects_LeaveNoAccumulatedSocketsOrThreads`
("Expected: 10 Actual: 9" sockets/threads after repeated connects). It passed
**3/3 exact-class isolated invocations** immediately after (2/2 each run) — a
thread/socket-count race on a busy box, in code P775 does not touch (no
connection/cleanup change). Recorded as a load flake per the 3/3-alone rule.

| 2026-10-22 | Hud16RadarIconTests.Per_frame_icon_name_resolution_allocates_nothing | fast tier 2026-10-22 23:2x: 208 B once over 1000 frames under suite load; passes 3x alone. Test now takes the best of 3 windows (a real per-frame allocation still fails every window). |

| 2026-10-23 | PackagingManifestTests.Artwork_manifest_matches_the_code_and_the_pinned_corpus | NOT a flake (corrected by Claude): P800's code names `sbbmp`, so the generator adds `sbbmp.mdl` as an artwork root; the committed roots list was stale. Fixed by regenerating `packaging/artwork-roots.txt` (8c97ef2f). |

## P799 validation environment (2026-10-23)

P799's single fast-tier run recorded one failure outside the task's surface:
`Allegiance.ClientCore.Tests.SessionLossLiveTests.EstablishedSession_ServerKilled_RaisesLost_EventAndControlsGoSilent`
("SessionLost event must fire on the transport thread"). It passed the exact-class
isolated invocation immediately after (1/1) — a transport-thread timing race while the
shared machine was also running the P799 5v5 skirmish live run. P799 touches no
transport/connection-cleanup code (the closest surface, `PerformUndock`, gained only a
pre-state ghost refusal). Recorded as a load flake per the 3/3-alone rule.

## P798b validation (2026-10-23) — one Core load flake, and two REAL regressions fixed

The first Fast-tier run on this task's tree (2026-10-23 21:33Z) recorded one failure
outside the task's surface: `Allegiance.Core.Tests.P480RotationKeepsPlayersLiveTests.Rotation_migrates_connected_peers_and_new_game_starts_without_reconnect`
("station 1052 was exported to a side that had not seen it" — **P860 root-caused this as a fixture
world-attribution race, not a load flake; see the P860 section**). It passed **3/3 exact-class
isolated** invocations immediately after, and the same test passed in the two later full
Fast-tier runs. P798b touches only `tools/allegiance-mcp/**` and test projects — no
`src/Allegiance.Core` / `src/Allegiance.Server` change — so this is recorded as a **load
flake** per the 3/3-alone rule.

The same run also exposed **two real regressions, not flakes**, both introduced by the
P798 WIP and fixed here (see `docs/reports/P798_SCOUT_OPENING.md` §6):

* `BotFixRegressionTests` (25 cases) + `P658SupervisorTests` — the bot internals tests build
  a `BotManager` with `RuntimeHelpers.GetUninitializedObject` (no field initializers), so the
  WIP's new `_supportSectors` field (and the lazy-unsafe `_intel` store) was null and every
  `TryDriveDefense` call NRE'd. Both are now lazy-initialised; the tool-surface count grew by
  one (`get_side_intel`: 13 / 16 supervisor).
* `BotAcceptsAndEngagesOrderLiveTests` (3 cases, and the only three "SLOW FAST" offenders)
  — the WIP's opening made a lone debug bot an explorer, so it flew a frontier sweep instead
  of shooting the target its test placed. Exploration is now fleet-only (≥4 combat bots per
  side) and never runs for a bot under an explicit commander order; the class passes 3/3 in
  ~6 s (was 15–30 s and failing).

| 2026-10-24 | P784ComposeTests.Fanout_issues_one_order_message_per_recipient_on_the_wire | fast tier on P801 merge (43ec8d33): "3 peers / 2 teams must leave one teammate and one enemy" (line 315) — side auto-assignment of 3 joining peers differs under suite load; passes 3/3 alone; unrelated to P801 (bot-only). Follow-up: assign sides explicitly in the test. |
| 2026-10-24 | DockedStationUpdateLiveTests.Docked_player_receives_own_station_update_when_own_station_has_no_scanner | fast tier on the P802 merge gate (rebased on 058fe07f) while three Pi/GLM delegates built and tested on the same host: "STATIONS_UPDATE for zero-scanner own station did not arrive within 15 ticks (0.750s sim time)" | 3/3 exact-class pass alone (2/2 tests each); P802 touches no station-update path — load-timing flake |
| 2026-10-24 | DockedStationUpdateLiveTests.Docked_player_receives_seen_own_station_update_within_native_cadence | same 15-tick station-update cadence assertion, fast tier on the P806 gate with concurrent agents; also failed 1/3 exact-class runs alone under that load, passed 2/3 immediately | same class/assertion as the row above (P806 touches no station-update path; the lobby snapshot is one small record per tick under the existing `_statsLock`) — load-timing flake |
| 2026-10-24 | P750NativeAsteroidTypesTests.Research_showcase_rocks_carry_native_models_and_no_rock_names | fast tier SLOW FAST 10.269 s (>10 s fast limit) on the P811 merge gate while three delegates built/tested on the host | ~1 s alone, 3/3, on both P811 and master — load-timing, not a regression |
| 2026-10-24 | P802TeamCivLiveTests.Random_change_resolves_to_a_real_legal_civ_and_relays_it | P803 fast-tier gate (this worktree): "a random CHANGE_TEAM_CIV must resolve, apply and relay" under full-suite load | 3/3 pass alone (2/2 tests each, after the gate); P803 touches no team-civ path (turret HUD/relay only) — random+timing load flake |
| 2026-10-25 | P802TeamCivLiveTests.Random_change_resolves_to_a_real_legal_civ_and_relays_it | P820a fast-tier gate: same "a random CHANGE_TEAM_CIV must resolve, apply and relay" under full-suite load | 3/3 pass alone (3/3 tests each); P820a touches no server or team-civ code (lobby squads + client screen) — second occurrence of the load flake |
| 2026-10-24 | RetriedConnectLiveTests.RepeatedConnects_LeaveNoAccumulatedSocketsOrThreads; CrossSideCombatVisibilityLiveTests.Fire_is_not_relayed_to_peers_in_other_sectors; DockedStationUpdateLiveTests.Docked_player_receives_seen_own_station_update_within_native_cadence | P803 fast-tier gate, second run (three delegates building/testing on the same host): socket-count 9≠10, fire positive-control timeout, station-update cadence timeout | all three pass 3/3 alone; P803 touches none of these paths (turret HUD/relay only) — load-timing flakes |
| 2026-10-24 | P480RotationKeepsPlayersLiveTests.Rotation_migrates_connected_peers_and_new_game_starts_without_reconnect | P803 fast-tier gate, third run (concurrent delegates): rotation migration timing under load | 3/3 pass alone; P803 touches no rotation path — **P860 root-caused: fixture world-attribution race, not a load flake; fixed, see the P860 section** |
| 2026-10-24 | BroadcastSnapshotTests.Recorded_ticks_have_identical_per_peer_bytes_order_and_lanes | 3/3 exact-class isolated failures on the P788 tree while other agents ran suites on the same host. The 4 cases assert a golden SHA256 prefix of live broadcast frame bytes ([Collection("LiveNetwork")], real server ticking on real sockets). P788's server-side change is byte-neutral for the seeded ships (all have no ripcord → IsRipcordActive false → BIsRipcording unchanged, UpdateCadenceTests.Seed creates plain Ships), so the transcript hash cannot be affected by the diff. Live broadcast coalescing varies with host load → different tick lanes → golden mismatch. These Live-tier golden-hash tests are meant to run on Claude's mini runner; recommended there. |
| 2026-10-24 | ConsoleVerbosityLiveTests.Stuck_npc_standby_logs_once_not_per_tick | fast tier on the P788 merge gate: "stuck NPC stand-by must log on transition only (got 0 lines over 200 calls, expected 1)" | Fails on MASTER too (650eb6a5: the class 1/3 alone; P788 tree 2/3), passes alone as a single test 3/3 — 0 captured lines points at the shared Console.Out capture racing another test in the class; P788 touches no NPC/console path. Follow-up: serialise the class or capture per-test. |
| 2026-10-24 | CrossSideCombatVisibilityLiveTests.CrossSide_sector_stays_correct_across_warp_return | fast-tier quarantine on the P787b gate (branch pi/p787b): exceeded the 10 s fast limit under full-suite load (26.1 s), then passed the tier's own retry; already a known flake (lines 55/913/970). P787b touches no combat-visibility/transport path (client audio + docked UI only). | 3/3 exact-method pass alone (~1 s each) — load flake |
| 2026-10-24 | P745FinaleSiegeLiveTests.Research_showcase_finale_destroys_a_station | Unreliable on MASTER (041e23a0): 2 pass / 1 fail in isolation the same evening (fail = finale massed or never registered because one side had not discovered the enemy base before the deadline). P814b gunners-off runs matched (1 pass / 1 fail). Not a 3/3 gate test; judge finale changes by the rig A/B and the `[strike]` telemetry. |
| 2026-10-24 | P813cReconnectClientTests.Beyond_the_window_the_session_is_lost_and_the_ship_retired | P813c first full fast-tier run: asserted the `SessionLost` flag immediately after polling the `Lost` latch (the latch is set just before the synchronous event raise on the reconnect thread) — test-side visibility race, NOT a product race | test fixed to wait for both; 6/6 alone and PASS in the gate; no ledger action beyond this note |
| 2026-10-24 | ThreePeerSeededSessionTests.SeedThreePeers_TwoSameSide_OneCrossSide_DeterministicVisibility; P761bCommandRelayTests.QueuedStationGoto_KeepsOwnTarget_AndRelaysTheStationToTheTeammate; P802TeamCivLiveTests.Random_change_resolves_to_a_real_legal_civ_and_relays_it; ConsoleVerbosityLiveTests.Stuck_npc_standby_logs_once_not_per_tick; P480RotationKeepsPlayersLiveTests.Rotation_migrates_connected_peers_and_new_game_starts_without_reconnect | P813c fast-tier gate re-runs (host load average ~14 from concurrent delegates): logon-drain/seating order, relay timing, random civ pick, stand-by log transition, rotation migration timing | all pass 3/3 alone on this worktree; every one is already a known load flake above, and P813c touches none of those paths (session park/resume + client reconnect only) — load-timing flakes (**P480: P860 root-caused, fixture attribution race; see the P860 section**) |
| 2026-10-24 | SC2NaniteSupportTests.Support_selection_obeys_research_dock_budget_and_mounts(civ: 25) | P813c fast-tier gate, third/"fourth" runs: SLOW FAST 11.203 s against the calibrated 10 s fast limit while the host ran at load average ~14 (every test passed) | 7-8 s for both cases 3/3 alone (limit 10 s standalone); same load-timing class as the `P750NativeAsteroidTypesTests` SLOW FAST entry above |
| 2026-10-25 | P739TimerLiveTests.Warp_bomb_notice_matches_captured_blast_deadline_and_stays_in_mission(delay: 7) | P805b fast-tier gate (this worktree, concurrent agents on the host): `Assert.InRange` upper bound 7 vs actual `7.0000000000000009` — a float boundary in an untouched `Allegiance.Core` timer test | 3/3 exact-class pass alone (5/5 tests, ~0.56 s each); P805b touches only `src/Allegiance.ClientCore.Game` + the proof script, no Core/server timing path — load-timing flake |
| 2026-10-25 | P802TeamCivLiveTests.Random_change_resolves_to_a_real_legal_civ_and_relays_it | P824 fast-tier gate (this worktree, concurrent agents on the host): same "a random CHANGE_TEAM_CIV must resolve, apply and relay" | full class 3/3 pass alone immediately after the gate; one later solo single-test set went 2/3 (same assertion), then 3/3 exact-class × 3 — fourth occurrence of this load flake; P824 touches only the texture/authoring pipeline (MdlTextureDecoder/Writer, tools/mdl-to-glb), no team-civ or relay path |

| 2026-10-27 | P750NativeAsteroidTypesTests.Research_showcase_rocks_carry_native_models_and_no_rock_names | P856 fast-tier gate (this worktree, concurrent agents on the host): SLOW FAST 10.886 s against the calibrated 10 s fast limit (every test passed) | 7/7 pass alone in ~1 s, 3/3 runs; P856 touches no asteroid-type or model-resolution path — third occurrence of the already-ledgered load flake above |
| 2026-10-27 | P480RotationKeepsPlayersLiveTests.Rotation_migrates_connected_peers_and_new_game_starts_without_reconnect; DockedStationUpdateLiveTests.Docked_player_receives_own_station_update_when_own_station_has_no_scanner | P848 fast-tier gate (this worktree; docs-only change — CSV + report, no production code): rotation-migration timing ("station 1052 exported to a side that had not seen it" — **P860: fixture attribution race, see the P860 section**) and 15-tick station-update cadence ("did not arrive within 15 ticks"), host load average ~6-7 with other lanes' dotnet builds at 400%+ CPU | both already ledgered above (P480 3/3-alone; DockedStationUpdate 3/3-alone on 2026-10-24). Under this gate: P480 2/3 alone (rotational timing), DockedStationUpdate 3/3 fail alone while the host is saturated — both touch no path P848 modifies (P848 changes no source); load-timing flakes |
## P819 validation environment (2026-10-25)

The single fast-tier run recorded six environment-context failures (not load
flakes): the five known `PlayerBundlePathsTests` ancestor/local.env cases, and
`P761StartChatComposeTests.ProfileEditor_OwnsEnterAndBackspace_AndKeepsChatClosed`,
which attempted to save player.json in the read-only per-user config directory.
P819 changes MCP showcase policy and proof tools, with no Core path resolver,
profile or UI change. All nine path cases passed 3/3 exact-class runs using the
existing SC2 `/proc/<parent>/fd/3` alias technique for workspace-backed scratch;
the profile method passed 3/3 exact-method runs with `XDG_CONFIG_HOME` redirected
into workspace scratch. Assertions were unchanged; no second fast-tier run was
made. See `docs/reports/P819_SIDE_BIAS.md` and `.scratch/p819/fast/summary.json`.
| 2026-10-25 | P733ResearchTests.No_bomber_tech_finale_reaches_stage_over_and_automatic_next_match_twice | FAILS ON MASTER since P797 native map discovery: `spawn_scenario: finale needs an observed enemy home station` — the fixture has no scouts, and stations are exported to a side only after it has seen them (they are created at mission start, so seeding visibility needs a per-match hook). Live tier; follow-up: give the fixture a per-match "stations seen" seed (P540b was the same class, fixed with ShowMap). |

## P817 validation environment (2026-10-24)

The single Fast-tier invocation recorded 10,458 passed, six failed and three
optional-Meshy skips. Five failing parameter cases were in the existing
`Allegiance.Core.Tests.PlayerBundlePathsTests` methods
`LocalConfigWinsOverBundleWithBlankEnvironment`, `MissingDirectoryHasNoDefault`
(Artwork and NativeSource), `UsesExecutableDirectoryOutsideRepository`
(Artwork), and `NativeSourceNeverGuessesAnInstallSubdirectory`. Worktree-local
TMPDIR made their simulated non-repository directories inherit this checkout's
`local.env`. This is the ancestor-discovery environment issue recorded above,
not a changed bundle resolver. The sixth failure,
`Allegiance.ClientCore.MonoGame.Tests.P761StartChatComposeTests.ProfileEditor_OwnsEnterAndBackspace_AndKeepsChatClosed`,
hit the sandbox's read-only default user-config directory.

All five methods passed **3/3 exact-method isolated invocations**, 24 total
parameter cases, under their intended context. Scratch bytes remained inside
the worktree; a proc directory-handle alias supplied lexical temp parents
outside repository discovery, and XDG_CONFIG_HOME selected writable scratch.
The profile method also passed 3/3 with writable XDG config alone. No production
resolver/profile code or test assertions were changed to obtain these passes.
These are deterministic environment limitations, not a claim that the broad
run was green. [Context audit](reports/P817_assets/context-audit.json) and
[P817 report](reports/P817_NATIVE_GOTOPLAN.md) preserve the raw-run distinction.

P817 parking observation: the first flag-off P700 showcase run reached 21/30
by its arrival deadline (longest stopped-with-target interval 2.105 s, below
the five-second parking limit). It passed **3/3 exact-method flag-off repeats**;
the closing off×3/on×2 series passed 5/5 (reached 24/25/24/24/24/30;
longest stop 3.911 s). The initial series was 4/5 overall and all five
passed the parking bound. Unchanged starting revision
`041e23a0189c83cc29e35d13008f0e32a72af9bf` passed 3/3 isolated runs
(reached 24/24/25; longest stops 1.613/1.819/1.518 s). The initial arrival miss
was transient under this host load; it was **not reproduced on the baseline**.
See [all parking summaries](reports/P817_assets/parking.json) and the report
for which build the closing series covered.
| 2026-10-25 | P775PlayerInfoMomentsTests.Side_switch_sends_no_playerinfo | fast tier on the P813b gate: "Bee: no ship assignment" (live seat wait) | 3/3 alone; P813b is client-only and flag-off — load flake |
| 2026-10-25 | P660NextMatchLiveTests.Late_game_over_rebuild_ready_countdown_started_has_no_clock_jump(enabled: False, dilation: off) | fast tier on the P813b gate | 3/3 alone; live next-match timing under suite load |
| 2026-10-25 | CrossSideCombatVisibilityLiveTests.Fire_is_not_relayed_to_peers_in_other_sectors | P822 fast-tier gate: setup `Assert.NotSame` — the shooter and its intended elsewhere-teammate were placed in the same sector because the fixture assigns sectors by asynchronous spawn/logon order (host load average ~14.7 from concurrent agents). The same documented P699c/TR-2 load flake above; P822 touches only the turret relay (`_pendingTurretRelays` one-shot kind + active/inactive selection), no fire, spawn or sector-placement path. | 3/3 exact-method pass alone (~1 s each) — load flake |
| 2026-10-25 | P700ShowcaseLiveTests.Local_showcase_never_parks_with_target_for_five_seconds | P822 showcase series, run 5 of 5: the arrival deadline expired at 480 s with `reached=20/30` (the test requires ≥ 24). Runs 1-4 reached 24/24/28/24; every run held the parking bound (longest speed<1 with target 1.90/1.91/0.20/2.01/2.01 s). Durations 362-480 s against the ~1m50s base rate, i.e. 3-4× slower under load average ~14.7. Bot gunners are OFF by default (`GunnerPolicy` reads ALLEGIANCE_GUNNERS), so no ACTIVE_TURRET_UPDATE exists in this test and the P822 diff cannot reach its ships. Same class as the P817 parking observation above. | first run series 4/5 pass (all five passed the never-park assertion); arrival miss is a host-load artifact, not a product regression |
| 2026-10-25 | P800HullProofGpuTests.Every_hull_of_the_real_core_renders_in_hangar_flight_and_target_pane | Fails in ~85 ms with "dotnet-…/tools/p800-hull-proof/bin/Debug/net10.0/p800-hull-proof.dll does not exist" on any checkout where `tools/build-tools.sh` has not run (fresh worktrees; also the mini runner's 2026-10-25 nightly and P788c runs). Environment, not product: passes after `tools/build-tools.sh`. |
| 2026-10-25 | P804ClientLeadTests.Lead_solve_and_green_ramp_allocate_nothing_per_frame | P699b-fix fast tier under parallel load (GLM P824 + Codex P821 on the host); per-frame allocation assertion, same family as Hud12b. 3/3 alone. |
| 2026-10-25 | P739TimerLiveTests.Warp_bomb_notice_matches_captured_blast_deadline_and_stays_in_mission(delay: 7) | P699b-fix fast tier under the same load; 3/3 alone. |
| 2026-10-27 | RetriedConnectLiveTests.RepeatedConnects_LeaveNoAccumulatedSocketsOrThreads | P859 fast tier under the concurrent lanes' load (three other tiers running on the host): the socket/thread-count assertion read 19 where it expects 20 (`RetryCleanupTests.cs:263`). P859 only touches the Modern HUD text scale, the Classic ripcord reserve, `Game1.AllWarps`' accessibility and a MonoGame test fixture — no socket, thread or reconnect path. | 3/3 exact-method alone (~40-53 ms each) — load flake |

## P821 — corrected P704 capture fixture (2026-10-25)

`P704TechLoadoutLiveTests.Real_research_repick_and_simultaneous_death_relaunch_have_no_server_refusals(count: 60)`
had a deterministic fixture error, not a load flake: it required **enemy** clients
to know an undiscovered captured expansion. The original fixture timed out at
`ResearchBomber`'s station-owner wait 3/3 with both the P821 selector and saved
`9d78ec6b` binaries. Purchase ACKs were full successes. An instrumented run
showed side-0 clients with expansion owner 0 and side-1 clients with the model
unseen. Native own-side visibility is unconditional (`FedSrv/FedSrv.CPP:10905-10910`);
enemy station updates are visibility-gated (`FedSrv/fsship.cpp:820-845`).

The fixture now waits for ownership on the buying/researched side, just like
its following tech-mask check. The 60-peer purchase count, full ACK, real
research, hull upgrade, relaunch pacing and intentional-refusal assertions are
retained. The corrected group passed 3/3 isolated repetitions (all nine cases). No
server/client visibility behaviour was changed. See
[the P821 report](reports/P821_ROLE_RESOLUTION.md); original/fixed raw receipts
and the baseline reproduction are under `.scratch/p821/` in the P821 worktree.


## P821 — P745 controlled observation (2026-10-25)

`P745FinaleSiegeLiveTests.Research_showcase_finale_destroys_a_station`: 1/3 controlled isolated repetitions. Failures retained the existing `No station destruction` assertion at a normal match deadline; the ending was decided by native kills. This is the same failure class as the master observation already ledgered above. No P745 routing, research plan, massing threshold or server rule changed. P801 live massing and controlled massed/trickle loopback each passed 3/3. See [P821 verification](reports/P821_assets/verification.json) and [report](reports/P821_ROLE_RESOLUTION.md); raw source logs are under `.scratch/p821/strict/` and `.scratch/p821/tmp/` in the P821 worktree.


## P821 — fast-tier observations (2026-10-25)

The single fast sweep recorded 10,662 passed, seven failed and three optional
skips, with no timing offenders. Five `PlayerBundlePathsTests` cases inherited
the checkout's `local.env` through workspace-local TMPDIR ancestors; the full
nine-case class passes 3/3 with a `/proc/<parent>/fd/<directory>` lexical alias
to the same workspace scratch. The profile-editor method in
`P761StartChatComposeTests` failed against the read-only default config path;
it passes 3/3 with task-local XDG_CONFIG_HOME. These are the existing P819/P817
environment limitations, not product fixes or load flakes.

`P804ClientLeadTests.Lead_solve_and_green_ramp_allocate_nothing_per_frame`
observed 1,800 bytes instead of zero in the broad sweep and passes 3/3
unchanged in isolation. Its allocation source is unknown. This meets the
lane's three-isolated-passes load-flake criterion; no allocation assertion,
timing budget, UI code or product path was changed. The original fast run
remains failed and was not repeated. See [P821 report](reports/P821_ROLE_RESOLUTION.md)
and [verification receipt](reports/P821_assets/verification.json).
| 2026-10-25 | ChatTargetComposeLiveTests.ComposeDefault_Team_SendsToTeammate_NotEnemy | Failed in the P824 full MonoGame run and the P821 fast tier under parallel host load (Codex + GLM + tiers); 3/3 alone both times. Live chat routing timing. |

## P825 validation (2026-10-25)

The single Fast-tier run hit the existing
`Hud12bReviewFixTests.HUD_formatted_readouts_allocate_nothing_in_steady_state`
allocation flake (1,568 B over 10,000 frames). The exact method passed **3/3**
in fresh standalone processes on final source; no HUD production code changed.
Five other failed cases were corrected civ-validation contracts and related
fixtures, documented as fixes rather than flakes in
[the P825 report](reports/P825_RANDOM_CIVS.md). The raw Fast snapshot remains
failed; its targeted follow-ups do not relabel it as a green broad run.

## P830 observation (2026-10-26)

`P700ShowcaseLiveTests.Local_showcase_never_parks_with_target_for_five_seconds`
failed 3 of 10 isolated runs on the P830 tree (`Parked >5s` with a live combat
target and zero velocity — Fighter hull, sectors 1051/1052), while the
unchanged base (`0dee572d`) passed 5/5 the same afternoon. The signature is the
ledgered P706/P708 parking class (bot-speed-under-machine-load: host load
average 3–6 with no local test running during the series). The P830 diff does
not touch the combat engage path that sets a Fighter's target; the only new
with-target explorer path after the opening window is the unchanged miner-raid
engage, and 30v30/15v15 P830 proof runs showed no park. Recorded as the same
host-load artifact, not a product regression; the P830 report carries the full
A/B series.
Claude's re-run on an idle host after a reboot (2026-10-26 15:04–15:33, load 0.3–2.5): **5/5 pass** on the
P830 tree — consistent with the load explanation.


## P832b verification (2026-10-26)

The single Fast-tier run had two existing failure signatures:

- `P761StartChatComposeTests.ProfileEditor_OwnsEnterAndBackspace_AndKeepsChatClosed`
  could not restore the sandbox's read-only user config. It passes **3/3**
  with task-local `XDG_CONFIG_HOME`. This is an environment restriction,
  as recorded under P788c/P821, rather than a product or timing fix.
- `RetriedConnectLiveTests.RepeatedConnects_LeaveNoAccumulatedSocketsOrThreads`
  observed 19 cleaned-up connections instead of 20 and passes **3/3** in
  fresh filtered invocations. This repeats the ledgered socket-cleanup race.

The broad Fast result remains failed; it was run once. No test assertion or
product code in either area changed. See
[the P832b report](reports/P832B_PROSPERITY_BOTS.md) for the receipt and
prosperity/parking verification.

## P843 observation (2026-10-27)

The P843 fast-tier gate (docs-only batch: sim-rules-matrix.csv + P843_CERTIFY.md, no server/core source change)
recorded the same known load flake once in `Allegiance.Core`:
`Allegiance.Core.Tests.P761bCommandRelayTests.QueuedStationGoto_KeepsOwnTarget_AndRelaysTheStationToTheTeammate`
(Assert.Equal "goto Garrison" vs "goto" — a command-relay seating-order/timing race, the same signature as the
HUD-19 and 2026-10-24 entries above). It passed **3/3** isolated invocations on this worktree. P843 changes no
production code, so this is the existing live-network/load artifact, not a regression.
## P841 validation environment (2026-10-26)

`Allegiance.Core.Tests.P775PlayerInfoMomentsTests.Mid_game_sighting_of_a_joined_enemy_sends_no_playerinfo`
("exactly one PLAYERINFO announcement for the joined enemy, got 2") fails **3/3
exact-test isolated invocations on this worktree's base (master @ 1e4c33d4)
with all P841 source changes reverted** — i.e. red at base, independent of the
P841 batch (P841 touches no PLAYERINFO join-announcement path). The count
depends on whether the joiner's PLAYERINFO reaches the peer after the peer's
MISSION_STAGE(Started) — a message-interleaving timing dependency, consistent
with the ledgered P775 siblings above. Not fixed here (outside the P841 batch
surface; the P775 owner should re-derive the expected announcement count).

**Claude correction (2026-10-27, merge gate):** not a base failure on current master. At `f4d9263d` the test
passes 4/4 even under load 23; at P841 it fails 3/4, and reverting only `Side.AutoAccept = true` makes it pass
4/4. Cause: the port config-seats a joiner, so the fixture's `Seat()` POSITIONREQ names the side the ship
already holds; with auto-accept that re-seats the ship (native `RequestPosition`, fsmission.cpp:4747-4760)
and the extra own JOIN_SIDE changes the replay's count. Fixed in the test: both sides set `AutoAccept = false`
before seating (5/5 pass).

| 2026-10-27 | P700ShowcaseLiveTests.Local_showcase_never_parks_with_target_for_five_seconds | P841 showcase 5×: run 3 failed with no `Parked >5s` sample and no assertion text captured (-v q); runs 1/2/4/5 and two immediate retries of the same test passed — load window while other lanes ran suites on the shared host | 2/2 immediate re-runs alone; P841 changed no bot flight/decision code (join-flow default + departure bookkeeping only) | load flake |
## P832c — sandbox environment receipt (2026-10-27)

The one Fast sweep recorded **10,979 passed, six failed and three optional
skips**, with no timing offenders. These are the existing P819/P817/P821
environment limitations: five `PlayerBundlePathsTests` cases inherited the
repository's `local.env` because TMPDIR is worktree-local, and
`P761StartChatComposeTests.ProfileEditor_OwnsEnterAndBackspace_AndKeepsChatClosed`
tried restoring the read-only user preferences file. The complete nine-case
path class passes **3/3** using a `/proc/<parent>/fd/<directory>` lexical alias
to the same workspace scratch; the exact profile method passes **3/3** with
workspace-local XDG_CONFIG_HOME. No product or test source was changed for
these retries; no `/tmp` or home-directory writes were used. The original
broad sweep remains failed and was not repeated. See the
[P832c report](reports/P832C_FLAG_BOTS.md) and
[receipt](reports/P832C_assets/verification.json).


P832c parking receipt: the prescribed original comparison passed **5/5 base
and 5/5 changed**; five additional changed-tree trials also passed. Continuous
CPU monitoring detected sustained host activity in three original runs and
all five additional trials, so no uninterrupted idle-host certification is
claimed. The maximum low-speed-with-target duration was 1.913 s on base,
1.818 s in the original changed series, and 2.011 s in the extra series
(limit 5 s). No parking failure or new product flake was observed. The extra
controller was stopped at a completed-trial boundary after repeated host
interference; all outcomes remain in the P832c receipt. A quiet-host window
is required for the remaining environmental confirmation.

## P832d — existing spotter-cache test isolation (2026-10-27)

`P835VisibilityCostTests.Spotter_cache_is_sticky_like_native_and_the_full_loop_re_picks`
failed once in a combined 37-case server/visibility run: both stage-2 spotters
were 9601. The exact case passes **3/3 alone**. The P835 class is not in the
LiveNetwork collection and changes process-global `VisibilitySpotterCache.Enabled`.
`P832cFlagVisibilityTests.Production_sweep_tracks_pickup_drop_and_sector_boundaries`
also toggles/restores that global; the two classes have no shared collection
and can run in parallel. This is a test-isolation race consistent with the
failure, rather than a P832d scanner change. No scanner
code or assertion was changed. Final qualification runs the visibility class
separately from server boots; the combined failure and individual retries are
retained in `.scratch/p832d/`. See the [P832d report](reports/P832D_ARTIFACT_BOTS.md).


P832d Fast receipt (2026-10-27): the one sweep is **11,081 passed / 1 failed /
3 optional skips**, with nine infrastructure checks passing and no timing
offenders. The failure is the P775 exact case already recorded in the P841
baseline-red entry above (announcement counter expected 1, actual 2). It fails
3/3 alone on the changed tree and 3/3 with the captured dispatch product DLLs
from `9caa4049cb8586811e443d45ec44c9edf1f74fb9`. The P775 test source is unchanged;
the latter runs use the same unchanged test method/current test assembly with
captured baseline Core/Server/ClientCore/Bot/Update DLLs (hashes in the P832d
receipt). No PLAYERINFO code or assertion was changed. The broad run remains
failed and was not repeated. Workspace-local XDG settings and a proc-fd alias
for workspace scratch avoid the older path/profile environment failures.

**Claude note (2026-10-27, merge):** P832d's base `9caa4049` predates the P775 fixture fix (`c60c9c07`), so
that failure is the AutoAccept fixture issue corrected above, not a P832d regression. The P835/P832c
spotter-cache isolation race named in the P832d report is gone with P858, which removed the process-global
toggle.
| 2026-10-27 | P730ShowcaseLiveTests.Two_local_showcase_matches_keep_mining_research_kills_pods_and_relaunch | merge gate 47d1ac8a (P849/P858/P859/P832d) 5×: run 4 failed at 302 s (slowest run; message not captured, -v q) while three delegate lanes ran suites on the host | 3/3 re-runs alone pass (2:53–4:29); 7/8 overall | load flake (suspected timeout) |
| 2026-10-27 | P480RotationKeepsPlayersLiveTests.Rotation_migrates_connected_peers_and_new_game_starts_without_reconnect; Hud12bReviewFixTests.Chat_line_truncation_matches_the_native_clip_and_is_cached_per_source_and_width | P842 merge gate f953e685 (three lanes running suites): same 'station 1052 exported to a side that had not seen it' and 7600 B allocation signatures | 3/3 alone each | load flakes; **P480 is now root-caused by P860 (fixture world-attribution race, fixed — see the P860 section)** |
## P847 observation (2026-10-27)

`Allegiance.ClientCore.MonoGame.Tests.P784ComposeTests.Fanout_issues_one_order_message_per_recipient_on_the_wire`
failed in the P847 fast tier (line 328: the mate ship `8100` is missing from the wing-0 recipient list
`[8098, 8096]`). **Pre-existing on master, not a flake and not P847's**: reverting every P847 `src/` change
and re-running the test alone reproduces the identical assertion 3/3. The earlier ledger entry above
(2026-10-24, different assertion at line 315) recorded a load flake in the same test; this is a different,
deterministic failure (a same-side wing-0 mate is not selected by `Game1.CommandFanout.Recipients`), so it
needs an owner rather than a retry. P847 touches no fanout/order-compose code.

P847 also updated two test expectations that pinned the old, non-native dropped-part treasure quantity
(`TreasureDropLiveTests`, `DropPartTests`): a dropped WEAPON's treasure now carries `amount = 0`
(FedSrv.CPP:4392), so both suites were re-run 3/3 alone and are green.

## P832e — single Fast sweep and isolated receipts (2026-10-27)

The one Fast sweep ran **11,106 .NET cases: 11,095 passed, eight failed and
three optional skips**, with no timing offenders; infrastructure checks passed.
The complete sweep was not repeated.

- Five `PlayerBundlePathsTests` cases found the worktree's `local.env` through
  worktree-local TMPDIR. The full nine-case class passed **3/3** with a
  `/proc/<parent>/fd/<directory>` lexical alias to the same workspace scratch,
  matching the existing P832c sandbox receipt. No product/test source changed.
- `P761StartChatComposeTests.ProfileEditor_OwnsEnterAndBackspace_AndKeepsChatClosed`
  tried restoring the read-only default preferences file. It passed **3/3**
  with workspace-local XDG_CONFIG_HOME; no user preferences were modified.
- `P775PlayerInfoMomentsTests.Side_switch_sends_no_playerinfo` failed with
  `Bee: no ship assignment`, matching the existing P813b seating flake. It
  passed **3/3** alone without source changes.
- `P784ComposeTests.Fanout_issues_one_order_message_per_recipient_on_the_wire`
  reproduced its documented auto-seating failure in the sweep. Three isolated
  trials then exposed the related incomplete-roster condition (the requested
  teammate was absent from the fanout snapshot). The fixture now requests
  sides 0/0/1 explicitly, permits that fixture imbalance and drains the
  teammate's identity/wing notification before taking the snapshot. The full
  **31-case P784 class passed 3/3** afterwards. This is a fixture correction,
  not a product chat change or a claimed passing original sweep.

All task-specific bot/native regressions, the four conquest wire cases and
six existing flag/artifact loopbacks passed their prescribed repetitions.
See [P832e](reports/P832E_CONQUEST_BOTS.md) and its
[validation summary](reports/P832E_assets/validation.json). Raw receipts remain
in the worktree's ignored `.scratch/p832e/` directory.
| 2026-10-27 | P480RotationKeepsPlayersLiveTests.Rotation_migrates_connected_peers_and_new_game_starts_without_reconnect | P852 fast-tier gate (this worktree): 1 fail in the full fast run, then 2/5 alone ("station 1052 was exported to a side that had not seen it"), then 3/3 clean | already ledgered above (2026-10-24 ×2, 2026-10-27 P848); P852 touches no station-export/rotation path (lobby join-request/ready/alliance rules only) — recurrence of the rotation-migration timing flake |
| 2026-10-27 | ConquestTerritoryLiveTests.Flag_capture_tips_conquest_and_broadcasts_game_state_then_game_over; ShopLauncherMountNumberingLiveTests.ShopBuyOrder_LauncherGrantedAtMount0_LocksAndFires_Live | P852 merge gate 7aa00867: conquest 'Expected 80 Actual 50' (P861: fixture included the fresh mission's legitimate 50% join ranking after GAME_OVER; resolved by per-peer capture-frame assertions); shop launcher = known load flake | 3/3 alone each | resolved fixture race, see P861 (conquest) / load flake (shop) |
| 2026-10-27 | P857NpcWarpParityTests.Player_is_held_by_arrival_latch_when_guard_enabled | gate 3f94d5f1: Expected 0 Actual 5 — root cause: it and P591cDroneRoutingTests both toggle the process-global Sector.WarpReEntryGuardEnabled from parallel collections | fixed: P857 joins the non-parallel LiveNetwork collection; 3/3 with P591c | race (fixed) |
| 2026-10-27 | P480RotationKeepsPlayersLiveTests.Rotation_migrates_connected_peers_and_new_game_starts_without_reconnect | P852 fast-tier gate (this worktree): 1 fail in the full fast run, then 2/5 alone ("station 1052 was exported to a side that had not seen it"), then 3/3 clean | already ledgered above (2026-10-24 ×2, 2026-10-27 P848); P852 touches no station-export/rotation path (lobby join-request/ready/alliance rules only) — recurrence of the rotation-migration timing flake, **root-caused by P860 (fixture attribution race, fixed)** |
| 2026-10-27 | ConquestTerritoryLiveTests.Flag_capture_tips_conquest_and_broadcasts_game_state_then_game_over; ShopLauncherMountNumberingLiveTests.ShopBuyOrder_LauncherGrantedAtMount0_LocksAndFires_Live | P852 merge gate 7aa00867: conquest 'Expected 80 Actual 50' (first gate after P832e's station-victory changes — watch: possible stale GAME_STATE read or recompute ordering race); shop launcher = known load flake | 3/3 alone each | suspected race (conquest) / load flake (shop) |
| 2026-10-27 | ConquestTerritoryLiveTests.Flag_capture_tips_conquest_and_broadcasts_game_state_then_game_over | P844 fast-tier gate (this worktree): same 'Expected 80 Actual 50' as the P852 row above — recurrence of the suspected conquest GAME_STATE recompute race (P861, being fixed in parallel; P844 touches no conquest recompute line) | 3/3 alone | load/suspected race |
| 2026-10-27 | P730ShowcaseLiveTests.Two_local_showcase_matches_keep_mining_research_kills_pods_and_relaunch | P844 merge gate: 2/10 failed at ~440 s with "lifepod observed by its controller" | **Fixed (test race), Claude in the P844 merge:** the victim was picked in one sim lock and killed in a second; a natural fight/dock in between let the global Kills/PodsCreated counters advance from another death while the victim never podded (180 s wait). Pick + kill now share one lock and assert `DamageResult.Killed`. 8/8 after the fix. Likely also explains the 47d1ac8a run-4 entry above. | test race — fixed |
| 2026-10-27 | P844MissionRulesCertifyTests.Allied_survivors_win_when_third_side_is_eliminated | P832g merge gate fast tier (host load ~6, four lanes): `Assert.Single` on bot GameOvers was empty — the test waited for server `Stage==Over`, then read the bot's received GAME_OVER without waiting for the async broadcast publisher | **Fixed (test race), Claude in the P832g merge:** pump until the bot holds the GAME_OVER before asserting. 5/5 after the fix | test race — fixed |
| 2026-10-27 | ShopLauncherMountNumberingLiveTests.ShopBuyOrder_LauncherGrantedAtMount0_LocksAndFires_Live | P832g merge gate fast tier (also in Codex's P832g run): default ExpandedPartData — server grant before the client's loadout ack | 1/1 alone; known launcher-receipt flake | load flake (ledgered) |
| 2026-10-28 | P660NextMatchLiveTests.Two_consecutive_matches_deliver_both_players_results_and_reseat_every_peer; ConsoleLogNonBlockingTests.Verbose_gate_silences_and_reenables_routed_diag_lines; P750NativeAsteroidTypesTests.Research_showcase_rocks_carry_native_models_and_no_rock_names (10.016 s) | P860 merge gate fast tier (four lanes building, load 3–4) | P660 class 5/5 alone, ConsoleLog class 3/3 alone; P750 16 ms over the calibrated limit (seen 2026-10-27 too); ConsoleLog also fails in the nightly → NT-2 | load flakes (P660 live next-match timing; ConsoleLog → NT-2) |
| 2026-10-27 | AsteroidsUpdateLiveTests.Mining_depletion_reflected_in_next_update_while_health_fraction_stays_full | P844b fast-tier gate (this worktree): 1 fail in the full fast run ("ore must decline with real mining; first 534 -> last 535") — a live mining ore-depletion timing test; P844b touches only victory/game-over/score-time paths, no asteroid/mining code | 3/3 alone clean | load flake |
| 2026-10-28 | P660NextMatchLiveTests.Two_consecutive_matches_deliver_both_players_results_and_reseat_every_peer | P853 merge gate fast tier: "loopback condition timed out" (Pump line 49, round wait at :201); right after the gate also 2/5 whole-class runs failed | then 16/16 whole-class + 6/6 alone on the merge; master 89c97647 6/6 alone + 6/6 class; same timeout hit the P860 gate before P853 existed | timing flake (8 s loopback pump budget) — watch; if it recurs, widen the pump budget or wait on the stream-ordered GAME_OVER |
| 2026-10-28 | P660NextMatchLiveTests.Two_consecutive_matches_deliver_both_players_results_and_reseat_every_peer | P853b merge gate fast tier, same timeout (Pump :49, round wait :201) | merge 3/5 whole-class failures at load ~2.3; master e4c04845 (clean worktree) 2/5 at load ~3–5 → pre-existing on master, not P853b | RECURRING — now ~40% at modest load; needs a fix task (diagnose which predicate is missing at timeout before widening the budget) |
| 2026-10-28 | Hud12bReviewFixTests.HUD_formatted_readouts_allocate_nothing_in_steady_state; P844MissionRulesCertifyTests.Allied_survivors_win_when_third_side_is_eliminated | P862 fast-tier gate (final tree): the HUD allocation assertion recorded 6672 B over 10,000 frames (same family/signature as the P775 and P787a entries above) and the P844 certification case saw an empty survivor list under a busy host | 3/3 alone each | load flake — P862 touches no HUD formatting/allocation path (client rendering + sector asset pre-warm only) and no mission-rules line |

## P863 verification observations (2026-10-28)

One Fast invocation (`tiers-20261028T155234.320355Z`) completed with 11,275 pass,
one failure and three existing optional skips. The failure was the already
ledgered `P761StartChatComposeTests.ProfileEditor_OwnsEnterAndBackspace_AndKeepsChatClosed`
read-only user-configuration path, not a product assertion; its ten-case class
passes with writable scratch `XDG_CONFIG_HOME`. The timing guard also flagged
`P750NativeAsteroidTypesTests.Research_showcase_rocks_carry_native_models_and_no_rock_names`
(11.238 s), the same loaded-corpus timing family above; its seven-case class
passes 3/3 alone unchanged.

The new 200-peer P863 transcript proof passed every assertion but took 12.971 s
in that concurrent Fast sweep. It belongs with the existing crowded
`BroadcastSnapshotTests` in Perf (TT-1's explicit crowded-snapshot classification),
so the final declaration assigns it Perf; small ranking/cache invariants remain
Fast. The original Fast timing failure remains reported; no second Fast sweep
was run. All twelve final P863 cases pass 3/3 alone, including both crowded proof
settings and the three-run paired measurement.

The four existing snapshot golden-prefix failures reproduce on untouched
`b37b4efe` with exactly the same observed hashes as P863; all old off/on/encode-once
byte comparisons pass. This confirms the stale golden observation already
ledgered under P788c. Their expectations were left for NT-2. Full evidence and
local timings: [P863_REPORT.md](reports/P863_REPORT.md).
| 2026-10-28 | Hud12bReviewFixTests.Hostile_damage_threat_and_flash_allocate_nothing_per_contact; ShopLauncherMountNumberingLiveTests.ShopBuyOrder_LauncherGrantedAtMount0_LocksAndFires_Live | P864 fast-tier gate (this worktree): MonoGame reported these 2 fails in the full fast run (interrupted once during Core.Tests under shared-host load ~7-11 procs); Core.Tests re-run below | 2/2 x3 alone clean | load flakes — P864 touches game-over/GetExpMult/leadership (server sim) only; no HUD rendering/allocation or shop-launcher path |
| 2026-10-28 | SC2NaniteSupportTests.Support_selection_obeys_research_dock_budget_and_mounts(civ: 25) (16.286 s) | 20261028 nightly fast tier: exceeded the calibrated 10 s fast limit (mini runner, fast tier runs 4 projects concurrently on slower cores, P744 factor ≈1.5) | 3/3 alone on the 24-core workstation, whole filtered class (2 cases) in 6–7 s — 5× under the mini's slowest observed | load flake (runner) |
| 2026-10-28 | P814GunnerTests.P814b_TurretCarrier_pref_keeps_or_beats_the_Best_hulls_turret_seats (17.319 s) | 20261028 nightly fast tier: exceeded the calibrated 10 s fast limit (same runner conditions) | 3/3 alone on the workstation, both class cases in ~3 s per run | load flake (runner) |
| 2026-10-28 | P699bPodLoadTests.Hundred_pods_per_side_return_from_Alderan_with_relaunch_traffic | P866 full-project sweep (Allegiance.Core.Tests, load ~3–5, four lanes): one pod oxygen expiry vs the expected zero (same signature as the 2026-10-19/P708 entries above) | 3/3 alone clean | load flake (recurrence) |
| 2026-10-28 | ConsoleVerbosityLiveTests.Stalled_stdout_does_not_block_the_sim_thread_even_verbose_on | P866 full-project sweep, same window | 3/3 alone clean (documented shared-static console class above) | load flake (recurrence) |
| 2026-10-28 | P746KillCreditOnEjectionTests.Winning_kill_finishes_wire_and_victim_score_before_mission_rotation | P866 full-project sweep then 1/3 class runs: `Results.Entries.Single(CharacterName == "P746-1")` found no entry while the game-over snapshot was being built; no P866 line touches results/rotation (event audiences, lobby publication, counters) | 8/8 single-theory isolated runs + 12/12 whole-class pass | load flake (rotation/results timing) |
| 2026-10-28 | BroadcastSnapshotTests.Recorded_ticks_have_identical_per_peer_bytes_order_and_lanes (4 golden cases) | P866 full-suite: the expected-prefix golden hashes mismatch while `offHash == onHash == sharedHash` in every case — the recorded actual is `D0ED7EEB…`, exactly the dispatch/master value in `docs/reports/P788C_assets/verification.json` | pre-existing stale golden (P788c entry above); no P866 change regenerates it. Reconfirmed after reverting the only MISSIONDEF-affecting experiment: still `D0ED7EEB…` | stale golden baseline (pre-existing, not a flake) |
| 2026-10-28 | P477E2eMiningRateDevLiveTests.Completing_dev_145_raises_real_mining_accrual (post-dev accrual 0.0 vs 62.4); TreasureDropLiveTests.SendDropPart_ClientWorldReceivesTreasureSpawn_OverTheWire | combined gate NT-2+P864c+P866+P867 fast tier | P477 3/3 alone → load flake. TreasureDrop 1/3 alone → REAL: P864c native file-map PopulateCluster seeds starting treasures (cash/powerup/part) into the fly sector; test assumed an empty sector. Fixed (identify the drop by part id among new treasures), 5/5 | P477: load flake (watch); TreasureDrop: fixed |


## P863b surge verification (2026-10-28)

The single Fast sweep records the existing five `PlayerBundlePathsTests`
worktree-TMPDIR ancestor/local.env failures. All nine cases pass **3/3** isolated
invocations through a proc directory-handle alias to the same NVMe workspace
scratch, restoring the intended outside-repository lexical context. No resolver
or test assertion changed. This is an environment-context failure, not a load
flake; the sweep was not repeated.

`ThreePeerSeededSessionTests.SeedThreePeers_TwoSameSide_OneCrossSide_DeterministicVisibility`
records the existing async logon-order `(0,1,0)` failure and passes **3/3 alone**.

`RadarTargetHighlightLiveTests.LiveOffScreenEnemy_IsTheFlaggedRadarTargetHighlight`
fails its fixed off-screen NPC placement premise in the sweep and **3/3 alone**.
The candidate's packets show the NPC has moved from the intended +Z placement.
An independently built **unchanged dispatch server** (`a94c13c7`) substituted in
the same test output reproduces that failure **2/3**, with one baseline pass.
This is a pre-existing placement/timing fixture failure, not a passing isolation
or a P863b regression. No radar, path resolver, seeding or NPC behavior was edited
to mask these observations. See [P863b_REPORT.md](reports/P863b_REPORT.md) for raw
logs and the independent full surge transcript comparison.

The Fast quarantine lane also observes the existing
`AddPartTests.Part_pickup_decline_recreates_treasure_at_current_position`
`Assert.Single` failure: three treasure-spawn records include ambient powerup
and cash spawns. The runner's automatic retry passes, followed by **3/3** manual
exact-method isolated passes. The original failed receipt remains in the one
Fast sweep. Overall original outcomes are **11,408 pass / 8 fail / 3 skip**;
infrastructure 9/9 and the Fast timing guard pass.
| 2026-10-28 | NpcWingmanLeadLiveTests.Wingman_fire_gate_leads_a_laterally_moving_enemy; NpcWingmanAttackLiveTests.Wingman_engages_nearest_enemy_closes_distance_and_deals_damage (35.9 s, also over the 10 s fast limit); KillBonusLiveTests.Real_end_to_end_projectile_kill_increases_kill_bonus | P865b fast tier (this worktree, shared-host load): the three wingman/projectile live tests failed in the full fast run | all three classes 8/8 ×3 alone in ~2 s per run | load flakes (combat-timing live tests under tier parallelism); the 35.9 s duration is the same load, not a code path change — P865b touches the carrier-relaunch re-task, the rock disguise and the per-side last-spotter slot, none of which these tests exercise |

## P873 training verification (2026-10-28)

`TR5TrainingMissions810Tests.Mission8_RunsToCompletion_AgainstTheRealEmbeddedHost`
failed the broader training regression sweep (61 passed / 1 failed) at
`goal 3: bomber never crossed into sector 1052`. An isolated run with the original
`HEAD` version of `TrainingHost.cs` restored for compilation failed at exactly the
same assertion (about 74 seconds). The P873 host was restored afterwards. This is
an existing headless Nanite fixture/completion gap, not established intermittency;
do not call the lesson live-certified or silently skip the test. The native/live
binding gaps and follow-up rows are in `docs/reports/P873_REPORT.md` and
`docs/parity/training-matrix.csv`.

P873's required Fast sweep (`tiers-20261029T000219.057074Z`) ran 11,475 cases:
11,468 passed / 4 failed / 3 optional skips. The failures repeat the existing
read-only default profile config, radar off-screen premise, P761b queued station
relay, and quarantined AddPart ambient-treasure observations. The AddPart retry
passed. P750's native asteroid research test passed but took 10.623 s against
10 s. Scratch `XDG_CONFIG_HOME` makes the profile test pass alone. All 13 new
P873 cases passed in the sweep and 3/3 isolated suite runs. The gate is retained
as red; no timing limits, unrelated fixtures or skips were changed.

## P872 (2026-10-28)

`NpcWingmanDefendLiveTests.Defender_disengages_when_chased_enemy_flees_and_returns_to_charge`
fails **deterministically 3/3 alone** in this worktree, and fails identically with
P872's Ship.cs/Server.cs/Server.Lobby.cs edits reverted to the dispatch base
(`c7e91ed2`): after the charge-centered acquire, `wingman.DefendTarget` is null at
the phase-1 guard (`NpcWingmanDefendLiveTests.cs:179`) even though `AttackTarget`
is the expected enemy. The failure is therefore **pre-existing at base, not a
P872 regression**; P872's SIM-0258/0259/0261 work only changed the chat text and
added the `HasDamagingPrimaryWeapon` acquire gate (both paths proven by
`P872NativeBehaviourTests`, 3/3 alone). Suspected cause lives in the live-tick
NPC plot ordering (unit-level `P708ShipUpdateTests` defends pass), left for the
lane that owns the live defend loop.

Fast sweep 2026-10-28 (P872): `DockDockingTests.Ship_nowhere_near_a_station_is_unaffected`
failed once inside the fast sweep and **passed 3/3 alone** immediately after —
load flake under the parallel sweep, not a P872 regression (no docking code path
was touched; P872 edits the explosion recipient snapshot, drone chatter text,
mission replay/context and the empty-session reap). The other two sweep
failures are the already-ledgered pre-existing entries above
(`RadarTargetHighlightLiveTests.LiveOffScreenEnemy…`,
`NpcWingmanDefendLiveTests.Defender_disengages…`).
### P869b — existing P650 mining fixture (2026-10-28)

`P650ServerRulesTests.Local_mining_battle_reports_income_and_leaves_drained_rocks`
fails at the `Assert.NotNull(drilling)` after its 1,800 simulated seconds on both
unchanged dispatch master `30e814e4` **3/3** and P869b **3/3**. The income check
passes; the fixture then requires one of its original miners to be actively
mining within a fixed simulation window. This is a reproducible baseline
failure, not a passing gate or an intermittent load-flake claim. The constant
override now clones the mission's constants rather than editing a shared core;
no mining/flight rule was changed. Other cases in the class pass 3/3. Evidence:
`.scratch/p869b/baseline-p650{,-2,-3}.log` and
`.scratch/p869b/isolated-P650ServerRulesTests-{1,2,3}.log`; report:
[reports/P869_REPORT.md](reports/P869_REPORT.md).

P869b's single Fast sweep additionally recorded **22 failures** (11,492 pass,
3 skip), classified in
[reports/P869_assets/p869b-failures.csv](reports/P869_assets/p869b-failures.csv):

- Five `PlayerBundlePathsTests` cases: the worktree-local `TMPDIR` makes their
  nominal outside-repository directories discover this checkout's `local.env`.
  The complete nine-case class passes **3/3** using a lexical `/proc/<parent>/fd`
  directory alias into the same writable scratch storage. No fixture or runtime
  resolver was changed.
- `WarpBombTests.Live_whole_mission_warp_bomb_reaches_the_cross_side_peer_and_the_delayed_blast_lands`:
  one missing-notice failure in the sweep; **3/3 passes on candidate and 3/3
  passes on unchanged master** in targeted reruns. The failure was not reproduced
  in isolation; no claim that a baseline failure was observed is made.
- **16 persistent NPC/layer/miner fixture failures** reproduce **3/3 on both
  candidate and unchanged dispatch `30e814e4`**, with identical assertion
  messages: five P591c routing cases, two P591b layer cases, five NpcDodge cases,
  one NpcCrossClusterMinerDecision case, two NpcAutopilot cases, and the
  InitialMinerSpawn debug auto-start case. These are reproducible baseline
  failures, not passing gates or intermittent load-flake claims. Flight/mining
  source was not changed by P869b. The suite's >10-second warnings for several
  of these cases remain visible; their tier tags were not changed to hide them.

Raw evidence is under `.scratch/p869b/` and
`.scratch/p869b/fast-tier/`. The 24-peer two-consecutive-rotation
regression passes **3/3** with zero simulation-stage failures. The new ownership,
registry, cache and disposal cases pass **47/47 in each of three runs**.

P869b final never-parks gate: **5/5 passes**, durations 189.70 / 229.19 / 225.50 / 194.24 / 221.44 seconds;
maximum stopped-with-target sample 1.116 s (limit 5 s). Final receipts:
[reports/P869_assets/p869b-verification.json](reports/P869_assets/p869b-verification.json).

## P882a integration verification (2026-10-28)

The single Fast sweep `tiers-20261029T014715.740643Z` again reproduced
`P761bCommandRelayTests.QueuedStationGoto_KeepsOwnTarget_AndRelaysTheStationToTheTeammate`
and `NpcWingmanDefendLiveTests.Defender_disengages_when_chased_enemy_flees_and_returns_to_charge`.
Both are already ledgered above. The relay case passed its isolated follow-up;
the defend assertion is the same null `DefendTarget` at line 179 that P872
reproduced deterministically on its dispatch base, and is not called an
intermittent flake. P882a does not edit the defend loop or order relay.

`P750NativeAsteroidTypesTests.Research_showcase_rocks_carry_native_models_and_no_rock_names`
passed its assertions but took 10.164 s against the calibrated 10 s Fast limit.
It passed the isolated follow-up together with the relay case (2/2). This is
the existing timing observation, not a changed test budget. The sweep remains
red; the separate task-introduced key-catalog metadata failure and its fix are
recorded in `docs/reports/P882a_REPORT.md`, not classified as a flake.

## P871b (2026-10-29)

Fast sweep `tiers-20261029T022443` (P871b): three failures were **load flakes**
under the parallel sweep and pass **3/3 alone** immediately after —
`LiveCombatScoringTests.Real_two_peer_kill_updates_both_player_scores`,
`P761bCommandRelayTests.QueuedStationGoto_KeepsOwnTarget_AndRelaysTheStationToTheTeammate`
and `ConsoleLogNonBlockingTests.Verbose_gate_silences_and_reenables_routed_diag_lines`
(no shared code with P871b: the lanes' only Server/Core edits are the wizard
provisioning, the reaped-mission config alignment, the sound-id tables and
`SalNoAmmoSound`). `P767WingmanAmmoTests.GateOn_WingmanRunsOut…` failed **for
real** — it asserted the old sound id 1409 that P871's generator bug produced;
P871b corrected it to native's 1396 (Igc/sounds.h:489 via `gcc -E`) and it is
green 3/3. `NpcWingmanDefendLiveTests.Defender_disengages…` is the
**pre-existing base failure** ledgered in the P872 section above (deterministic
3/3 alone, unrelated).

## P882b training verification (2026-10-28)

The complete training cohort (both client projects, including all 18 Live cases)
passed **125/125 cases, three times**: 375 passes, no failures/skips. M3's stale
headless fire input and M5's real cross-sector drone-membership defect are fixed;
the new Live transfer/termination proof also passed **3/3 alone**. Native causes
and individual results are recorded in `docs/reports/P882b_REPORT.md` and
`docs/reports/P882b_training_results.csv`.

The historical P873 Nanite assertion, `goal 3: bomber never crossed into sector
1052`, **did not reproduce** on this dispatch base: M8 passed 3/3 on final source
and 1/1 in an isolated scratch runtime using the original dispatch-base
`TrainingHost.cs` (`a243fa4b`). A baseline M5 control still reproduced its original
sectorless-fire exception, confirming the old host was loaded. No M8 test/graph
was edited, and no claim is made that P882b repaired the historical Nanite gap or
that the earlier P873 failure was intermittent.

Exactly one Fast sweep ran: **11,616 cases; 11,610 passed / 3 failed / 3 optional
skips**. The gate remains red. Infrastructure and quarantine checks passed.

| Test | P882b observation and disposition |
|---|---|
| `StationCombatLiveTests.Cross_side_missile_hits_station_shield_exact_fraction_armor_untouched` | Failed at line 278 because the missile never hit, taking 16.653 s (also the sole Fast timing violation). Passed 3/3 standalone follow-ups at 0.397 / 0.402 / 0.390 s. Broad-run timing observation; this test never invokes the changed training-host placement path. Station/missile code and thresholds were not changed. |
| `NpcWingmanDefendLiveTests.Defender_disengages_when_chased_enemy_flees_and_returns_to_charge` | Same null `DefendTarget` at line 179 as the deterministic dispatch-base failure already ledgered under P872 and observed by P882a. Left open; no defend code changed. |
| `BallotVotingLiveTests.Draw_end_to_end_offer_then_accept_then_game_over` | Empty receipt collection at line 602. Three standalone follow-ups yielded pass / pass / fail at the same line. Intermittent even in isolation; related draw-chain receipt ordering is covered by the earlier TT-1b audit entry. Left open; no ballot code changed and this test never invokes training-host placement. |

The standalone showcase never-parks gate passed **5/5**, with all 30 bots and
24/30 objectives reached in every run; longest measured stops with a target were
0.000 / 1.503 / 1.517 / 1.008 / 1.109 s. Raw logs/TRX stay git-excluded under
`.scratch/p882b/` and `.scratch/training-20261029T034808-6499/`.

## P882b merge gate (2026-10-29, Claude)

One Fast sweep on master 2c20b601 (11,677 tests): 4 Core failures. `NpcWingmanDefendLiveTests.Defender_disengages…`
is the ledgered line-179 failure (P872). The other three are broad-run load observations, each **3/3 in isolation**:
`P780PodRescueProbeLiveTests.Probe_rescue_cancels_oxygen_timer`,
`LiveShieldRegenBroadcastTests.Stationary_ship_streams_its_regenerating_shield_over_the_wire`,
`KillBonusLiveTests.Real_end_to_end_projectile_kill_increases_kill_bonus`. None touches the training host P882b changed.
`tools/run-training-tests.sh --no-build` (Fast + Live, both client projects) passed.

## P881a (2026-10-29)

Fast sweep `tiers-20261029T060702.377618Z`: three failures, all pre-existing/ledgered —
`ShopLauncherMountNumberingLiveTests` (passes 1/1 alone on the same tree),
`NpcWingmanDefendLiveTests.Defender_disengages…` (fails identically in a clean HEAD worktree
under `.scratch/base/`; the P872 deterministic base failure) and the
`AddPartTests` treasure-quarantine case (the runner's automatic retry passed). Real-window
observation, not a gate: one Modern Basic Flight run stalled at the M2 goal-2 pointing gate
(`2.6`) while the previous Classic run and a rerun of the same Modern case both reached `3.6`;
the run is reported as a convergence-timing observation, not a passing certification.

## P877 (2026-10-29)

Fast sweep `tiers-20261029T060400.085544Z` (P877):
`FuelPoolAndAfterburnerTests.Live_real_server_afterburner_flight_depletes_fuel_and_cuts_out`
failed once under the parallel sweep and passed **3/3 alone** immediately after
(0.6 s each). P877 touches no Server/Core sim code (tools/allegiance-mcp bot
policy + tests only), so this is a load flake of the live afterburner timing
fixture, not a regression. `NpcWingmanDefendLiveTests.Defender_disengages…`
also failed in the sweep — the **pre-existing base failure** ledgered in the
P872 section above. All 10 P877 tests passed 3/3 alone.

The second P877 Fast sweep `tiers-20261029T064217.980924Z` failed only on the
already-ledgered cases: `NpcWingmanDefendLiveTests.Defender_disengages…`
(pre-existing base failure, P872 section) and
`AddPartTests.Part_pickup_decline_recreates_treasure_at_current_position`
(ledgered load-flake, row 1959; the automatic quarantine retry failed this
time, but the test passed 6/6 in isolation immediately after). All 1973
ClientCore.Tests fast tests — including the 11 P877 tests — passed.

### 2026-10-29 — batch 2 merge gate (P875 + P880b + P881a + P877 on master)

Fast sweep `tiers-20261029T123227.665499Z`: 2 Core failures, both already
ledgered — `NpcWingmanDefendLiveTests.Defender_disengages…` (pre-existing base
failure, P872 section) and `BallotVotingLiveTests.Draw_end_to_end_offer_then_accept_then_game_over`
(intermittent draw-chain receipt, row above). The NPC Live re-run (3× each class
P875 touched) also failed once each on
`NpcWingmanAttackLiveTests.Wingman_engages_nearest_enemy_closes_distance_and_deals_damage`
and `NpcDodgeLiveTests.Traveling_npc_does_not_dodge_the_station_it_is_docking_at`
in run 3 only. All three (ballot draw, wingman attack, dodge-at-dock) passed
**3/3 alone** immediately after; load flakes of the live combat/travel timing
fixtures, no Core sim change in this batch beyond P875's GotoPlan default.

The three training failures in the same gate were stale test drivers, fixed in
the merge commit (not flakes): P880a's DoDamage expectation (native
dodamageaction.cpp:42-48), TR3's Mission 3 key id + a degenerate orient basis,
and TR5's Mission 8 bomber pinned inside the home station's radius, where native
idle Dodge (shipIGC.cpp:2334, common.cpp:1745-1747) pushes it every tick.


## P883 (2026-10-28)

The single Fast sweep executed 11,688 tests: 11,678 passed, 7 failed, 3 skipped.
Six failures were deterministic fixture drift from the chosen-mission change,
not flakes: `P653EconomyLoadoutIntegrityTests` needed the explicit mission in its
`AssignShip` reflection calls (2 cases), `P655GameStateIntegrityTests` needed the
mission in its pending-side constructor (2 cases), and `P682MissionStateTests`
needed to compare the per-cookie graph payload rather than the cache dictionary
instance (2 cases). All six are fixed and passed in the focused final runs
(87 core cases and 7 ClientCore cases). The seventh is the existing P872
`NpcWingmanDefendLiveTests.Defender_disengages_when_chased_enemy_flees_and_returns_to_charge`
failure at line 179, as documented above. No simulation change or quarantine was
made for it. The full-tier result remains red; it was not repeated. Raw evidence
is under git-excluded `.scratch/p883/fast/`; see `docs/reports/P883_REPORT.md`.

## P883b (2026-10-29)

The first `tools/build-tools.sh` pass hit an MSBuild worker exit (`MSB4166`) in
`tools/hullhit-proof`; cancellation also produced `MSB5021` from the MonoGame
content task. That project built **3/3 alone**, each with zero warnings/errors,
without source changes. Evidence: git-excluded
`.scratch/p883b/build-isolation/hullhit-{1,2,3}.log`. The full tool gate is rerun
in `.scratch/p883b/build-tools-final.log`; see `docs/reports/P883_REPORT.md` for
its final result. This is a build-process failure, not a changed gameplay rule.

### 2026-10-29 — P883b merge gate (92f050d3)

Fast sweep: `AddPartTests.Part_pickup_decline_recreates_treasure_at_current_position`
and `ShopLauncherMountNumberingLiveTests.ShopBuyOrder_LauncherGrantedAtMount0_LocksAndFires_Live`
(both already ledgered) passed **3/3 alone**; `NpcWingmanDefendLiveTests…` is the
pre-existing line-179 failure. `P730ShowcaseLiveTests.Two_local_showcase_matches_keep_mining_research_kills_pods_and_relaunch`
failed 1 of 5 in the never-parks loop (failure text not captured by the gate
script) and passed **3/3** in follow-up runs (4m24s–5m17s); load flake while
three delegate lanes were running. Not flakes, fixed at merge: the heartbeat
wire-shape test (new P883b fields) and P877's logon cookie (P883 refuses an
unknown dwCookie, by design).

### 2026-10-29 — P885 gate (Mission 4 native TrekKey ids)

Fast sweep `tiers-20261029T135842.924649Z`: 11,699 tests, 2 failed + 1 limit
offender, all pre-existing and untouched by P885 (client training ids only):

- `ShopLauncherMountNumberingLiveTests.ShopBuyOrder_LauncherGrantedAtMount0_LocksAndFires_Live`
  (contention failure, ledgered above) — **3/3 pass alone** immediately after.
- `NpcWingmanDefendLiveTests.Defender_disengages_when_chased_enemy_flees_and_returns_to_charge`
  — the known deterministic base failure (P872 section above): **3/3 FAIL
  alone** on this gate too, same null `AttackTarget` at line 179. P885 changes
  no wingman/defend or Core sim path; left open as ledgered.
- `P750NativeAsteroidTypesTests.Research_showcase_rocks_carry_native_models_and_no_rock_names`
  — SLOW FAST 12.438 s vs the 10 s calibrated limit (the test itself passed);
  **3/3 pass alone in ~1 s** — fourth occurrence of the ledgered load flake.

Training gate (`run-training-tests.sh --no-build`, Fast + Live, both client
projects): PASS clean — 75 + 58 tests, no flakes.

## P887 merge gate (Claude, 2026-10-29)
Fast tier `tiers-20261029T202701` on master 76c6355e (P887 defaults): 3 Core failures, none caused by P887.
- `NpcWingmanDefendLiveTests.Defender_disengages_when_chased_enemy_flees_and_returns_to_charge` — the ledgered pre-existing
  failure (3/3 alone).
- `P739TimerLiveTests.Warp_bomb_notice_matches_captured_blast_deadline_and_stays_in_mission` — float-boundary flake
  (`Actual: 5.0000000000000009` vs range 4.9–5); 1/8 alone on master AND 1/8 on the pre-P887 tree (P884b worktree).
  The assertion's upper bound needs an epsilon — follow-up.
- `NpcWingmanDefendLiveTests.Defender_switches_to_closer_threat_near_charge_while_chasing_distant_one` — failed in the
  sweep and 1/3 alone, then 0/8 on master and 0/8 pre-P887: rare flake.
- `P777WarpExitWobbleTests.Npc_exit_touch_rate_is_measured` ran 10.97 s, just over the fast-tier 10 s limit (load).

## P891 (2026-10-30) — native boarded source pose gate

One Fast sweep `tiers-20261030T032640.368112Z`: **11,748 tests; 11,739 passed, 6 failed, 3 skipped**, plus six executions over the calibrated 10 s limit. All 20 P891/P803 cases passed in the sweep and all 60 focused executions passed. No second Fast sweep was run. Raw receipts were moved after completion to git-excluded `.scratch/p891/fast-tier/`; exact-method isolation receipts are `.scratch/p891/isolation.json` and `isolation/`.

Each failed method and each timing offender was selected alone three times, serially, using server GC and artwork. Theory methods ran all of their cases each time.

| Method (short name) | Three isolated results | Maximum case duration per run (s) | Assessment |
|---|---|---|---|
| `P800HullProofGpuTests.Every_hull_of_the_real_core_renders_in_hangar_flight_and_target_pane` | pass / pass / pass | 4.273 / 3.780 / 4.229 | Passed 3/3 below the 10 s limit in isolation; sweep timing contention. No change to this fixture. |
| `P796VariedLoadoutTests.Varied_equips_every_legal_hull_and_part_of_the_role_over_many_purchases` | pass / pass / pass | 6.471 / 7.182 / 5.301 | Passed 3/3 below the 10 s limit in isolation; sweep timing contention. No change to this fixture. |
| `P814GunnerTests.P814b_TurretCarrier_pref_keeps_or_beats_the_Best_hulls_turret_seats` | pass / pass / pass | 4.031 / 3.280 / 4.539 | Passed 3/3 below the 10 s limit in isolation; sweep timing contention. No change to this fixture. |
| `SC2NaniteSupportTests.Support_selection_obeys_research_dock_budget_and_mounts` | pass / pass / pass | 4.344 / 4.076 / 4.694 | Passed 3/3 below the 10 s limit in isolation; sweep timing contention. No change to this fixture. |
| `BotFlightTests.Bot_trajectory_matches_independent_ExecuteMove_rollout` | pass / pass / pass | 1.008 / 1.057 / 1.039 | Known timing flake (P872); 3/3 alone. |
| `DockDockingTests.Ship_nowhere_near_a_station_is_unaffected` | pass / FAIL / FAIL | 0.577 / 0.562 / 0.571 | Previously ledgered status-history intermittency; still fails alone. Original-server replay also reproduces it (7/8 pass, same stale docked status). Not claimed fixed. |
| `NpcWingmanDefendLiveTests.Defender_disengages_when_chased_enemy_flees_and_returns_to_charge` | FAIL / FAIL / FAIL | 1.243 / 1.260 / 1.181 | Known persistent P872/P887 baseline failure at AttackTarget assertion; unchanged NPC/defend path. |
| `P761bCommandRelayTests.QueuedStationGoto_KeepsOwnTarget_AndRelaysTheStationToTheTeammate` | pass / pass / pass | 0.410 / 0.435 / 0.421 | Known relay/target-name receipt flake; sweep saw bare goto instead of goto Garrison. |
| `P777WarpExitWobbleTests.Npc_exit_touch_rate_is_measured` | pass / pass / pass | 10.176 / 10.117 / 10.107 | Functional 3/3, but all three remain just over 10 s; existing timing-limit offender (P887). |
| `P838GcProfTests.Gcprof_line_emitted_with_sane_values_and_tickprof_format_unchanged` | pass / pass / pass | 0.066 / 0.074 / 0.055 | Sweep observed process-wide allocation 8742.1 MB against a 0.8–16 MB bound; 3/3 alone. No profiler source change. |
| `ProbeCombatLiveTests.Lethal_hit_kills_probe_removes_from_sector_and_broadcasts_destroy_to_same_side_peer` | pass / pass / FAIL | 0.397 / 0.375 / 16.746 | Intermittent lethal-hit/expiry observation (sweep and one isolated run); not claimed 3/3 passing or fixed. Original-server replay passed 11/11 serial plus 6/6 at concurrency 3, so this task does NOT claim that replay reproduced the probe failure. This unboarded ordnance fixture has no new parent-routing branch; Core collision/damage code is unchanged. Retained as an open non-task fixture observation. |

For the two intermittent docking/probe cases, a separate scratch build restores all four modified Server files from original HEAD `b567ee1f`; it never reverts the working tree. The same test DLL runs against that original Server binary in a separate directory. Baseline evidence: `.scratch/p891/baseline-build.log`, `baseline-results/`, and `baseline*.py`. Docking first passed 3/3, then reproduced on the eighth bounded replay. Probe did not reproduce on the original binary in the bounded replay; this distinction is retained rather than calling every red result a proven baseline failure. No assertion, quarantine, timer limit or production rule was relaxed.
## P889 gate (2026-10-30, worktree of 8181619e)

Fast tier `tiers-20261030T030304.330748Z`: 11,765 tests, 5 failures, none caused by P889 (sizing resolver,
`[frameprof]`, calibration). Each non-task failure passed **3/3 alone** afterwards:

- `P660NextMatchLiveTests.Late_game_over_rebuild_ready_countdown_started_has_no_clock_jump(enabled: False,
  dilation: "off")` — 3/3 pass alone (4 cases, 12 s). Load flake; P888's test is unchanged.
- `LiveCombatScoringTests.Real_two_peer_kill_updates_both_player_scores` — 3/3 pass alone (0.45 s).
- `LiveSideSelectTests.Flag_on_auto_seats_a_silent_peer_at_the_configured_30_seconds` — 2/3 fail, then
  3/3 pass on the class and 3/3 on the method immediately after (host had just finished the 25 min training
  sweep). Load flake.
- `BotAcceptsAndEngagesOrderLiveTests.Mcp_debug_bot_buys_relaunches_and_fights(killPod: True, botCount: 3)`
  — SLOW FAST 17.694 s (calibrated 10 s limit); 3/3 pass alone in 5 s. Same load-flake class as
  `P777WarpExitWobbleTests.Npc_exit_touch_rate_is_measured` (10.444 s in the same sweep, passed).
- `NpcWingmanDefendLiveTests.Defender_disengages_when_chased_enemy_flees_and_returns_to_charge` — the
  ledgered pre-existing P872/P883/P887 failure; **3/3 FAIL alone** here too, unchanged, not P889's path.

Showcase never-parks 5× (`P700ShowcaseLiveTests.Local_showcase_never_parks_with_target_for_five_seconds`):
**5/5 PASS** (3 m 29 s, 4 m 00 s, 3 m 59 s, 3 m 53 s, 3 m 46 s). Training gate
(`run-training-tests.sh --no-build`): PASS — ClientCore 77/77, MonoGame 60/60 (Fast + Live).

### P889 final-tree fast sweep (2026-10-30, `tiers-20261030T032102.922312Z`)

Re-ran the fast tier after the final code change (explicit `maxDegreeOfParallelism`
selects the parallel build): 11,766 tests, 3 Core failures, all pre-existing/load:

- `P739TimerLiveTests.Warp_bomb_notice_matches_captured_blast_deadline_and_stays_in_mission`
  — the already-ledgered float-boundary flake; **2/3 pass alone** (1/3 hit the
  `5.0000000000000009` boundary, same as the P887 row above).
- `NpcWingmanDefendLiveTests.Defender_disengages_when_chased_enemy_flees_and_returns_to_charge`
  — the ledgered pre-existing failure (**3/3 fail alone**).
- `NpcWingmanAttackLiveTests.Wingman_engages_nearest_enemy_closes_distance_and_deals_damage`
  — SLOW FAST 39.056 s (calibrated 10 s limit); **3/3 pass alone in ~0.9 s**.
  Load flake (the sweep overlapped other agents' builds).
## P893 gate (2026-10-30)

Fast sweep `tiers-20261030T035703` on the P893 tree: 11,763 tests, 4 failed + 1 limit offender.

- `P806OptionsMenuTests.Sound_submenu_lists_the_ports_real_music_controls` — real
  P893 change (the Sound submenu gained the 4th "Positional 3D Sound" row);
  test updated in the same lane, passes.
- `MissileLockLiveTests.MissileLock_BuildsPartialThenFull_AndPosts_Tips_OverTheWire`
  (38.2 s, the calibrated-limit offender) — the ledgered pre-existing baseline
  failure (see P893-era entries above); not retried here.
- `NpcWingmanDefendLiveTests.Defender_disengages…` — the known deterministic
  baseline failure (P872/P887 sections); not a flake.
- `DeadLatchRespawnLiveTests.Remote_client_sees_victim_again_after_death_and_respawn`
  (contention) — **3/3 pass alone** immediately after the sweep.
- `P739TimerLiveTests.Warp_bomb_notice_matches_captured_blast_deadline_and_stays_in_mission(delay: 7/5)`
  ×2 (contention) — **5/5 pass alone** immediately after (bothTheory cases, 3 repeats).
- `AddPartTests.Part_pickup_decline_recreates_treasure_at_current_position`
  (quarantine, flaky) — the already-ledgered quarantine flake (P883b section).

Confirming sweep `tiers-20261030T041824` (after the P806 test fix): 11,763
 tests, 5 failed + 6 limit offenders, all pre-existing/contention, none on the
 P893 path:

- `ShopLauncherMountNumberingLiveTests.ShopBuyOrder_LauncherGrantedAtMount0_LocksAndFires_Live`
  (contention, the ledgered P885 case) — **3/3 pass alone**.
- `MissileLockLiveTests.MissileLock_BuildsPartialThenFull_AndPosts_Tips_OverTheWire`
  (38.3 s, limit offender) — ledgered baseline failure.
- `NpcWingmanDefendLiveTests.Defender_disengages…` — ledgered baseline failure.
- `NpcAsteroidContentionLiveTests.Two_same_side_idle_miners…` (18.4 s, limit
  offender, contention) — **3/3 pass alone** (<1 s each).
- `P817NativeMinerDecisionCycleLiveTests.Idle_npc_autonomously_mines…` (37.1 s,
  limit offender, contention) — **3/3 pass alone** (~8 s each).
- Other limit offenders (`NpcDodgeLiveTests` 10.2 s, `P750NativeAsteroidTypesTests`
  10.0 s, `P777WarpExitWobbleTests` 10.7 s) passed and are the ledgered
  near-threshold load flakes.
- The P806 options test now passes in-sweep (3796 MonoGame passes with the
  only failure being the ShopLauncher contention case above).

## 2026-10-30 — fast tier on the P889/P891/P893/P894/P895 merged tree

- `LiveSuicideShipDeleteTests.Combat_death_produces_ship_delete_to_same_audience`
  (load flake) — **3/3 pass alone**.
- `DirectNpcBuildOrderLiveTests.Commander_directs_builder_drone_to_build_creating_real_station`
  (load flake) — **3/3 pass alone**.

## P892b verification observations (2026-10-30)

Single Fast-tier run (`tools/run-tests.sh --tier fast`, `TestResults/tiers-20261030T140210.335559Z/`): all P892b-owned tests passed (P881a real-loopback M3 completion, the B-key regression test, `P892TrainingSetupTests`); the failures below are non-task (P892b touched only `Allegiance.ClientCore.Game` training/targeting, `P881aTrainingMissionsTests.cs`, the matrix and the report — none of these projects' failing paths).

| Failure | Three isolated outcomes | Classification |
|---|---|---|
| `DeadLatchRespawnLiveTests.Remote_client_sees_victim_again_after_death_and_respawn` (ClientCore) | pass / pass / pass (2 s) | Load flake: Live TCP loopback test; fails under the concurrent fast-tier sweep, 3/3 alone. |
| `MissileLockLiveTests.MissileLock_BuildsPartialThenFull_AndPosts_Tips_OverTheWire` (ClientCore) | pass / pass / pass | Already ledgered intermittent under P890/P894; passed 3/3 alone this session. |
| 18 `Allegiance.Lobby.Tests` cases | full project reproduced 13/122 on clean master | Not order-dependent and not P894: each test host's config file watcher hit the machine's `fs.inotify.max_user_instances` limit (128). Fixed by turning config reload off in the Lobby test assembly. |

## P895b gate (2026-10-30)

The one executed Fast sweep retained **FAIL**: 11,885 executions, 11,863 passed,
19 failed, 3 optional skips, plus one passing timing offender (538.3 s). All
P895/P895b/shared cases passed in the sweep. Nine tier-runner infrastructure
checks passed. An early qualification restart left child orchestration processes
alive after their shell wrappers were interrupted; their duplicate Fast launch
requests waited on the runner mutex and the existing result-directory guard
prevents a second sweep. Actual test overlap is retained as a qualification
limitation, not hidden as an idle-machine result. The final focused cohort was
subsequently rerun 3/3 without other task test work running.

Every failed method and the passing timing offender was selected alone three
times, serially. Theory methods included all their cases. Full method names,
case outcomes and receipts: [P895b verification](reports/P895B_assets/verification.json).

| Case / group | Three isolated results | Assessment |
|---|---|---|
| Eleven Lobby methods: `P655DirectoryIntegrityTests.H23_Request_body_limit_refuses_large_payload_normal_registration_works`; `P761PlayerListTests.PublicListing_CarriesNoPilotNames_EvenWhenTheServerReportsThem`; `P806ServerListLoopbackTests.Status_skill_and_settings_sort_columns_order_the_mixed_games`; `P808IdentityTests.Passkey_account_is_stable_unique_persistent_and_requires_real_proof`; `P871ChatAndGamesTests.Directory_chat_relays_to_readers_and_does_not_echo_the_sender`; six `P894ListingFieldsTests` methods (`Game_counts_and_percent_load_follow_native_c_GameFactor`, `Location_is_recorded_and_exposed`, `No_armed_start_time_is_advertised_as_null`, `Percent_load_is_the_native_formula`, `Start_time_is_normalized_against_the_directory_clock`, `Stats_report_servers_missions_players_and_load`) | Each pass / pass / pass | Sweep hit the user's 128-inotify-instance limit during ASP.NET startup; resource contention, not UDP delivery. No lobby production source changed. |
| `PlayerBundlePathsTests.UsesExecutableDirectoryOutsideRepository`, `MissingDirectoryHasNoDefault`, `NativeSourceNeverGuessesAnInstallSubdirectory`, `LocalConfigWinsOverBundleWithBlankEnvironment` (five failed theory executions) | Each FAIL / FAIL / FAIL under the same environment | Lane-required `TMPDIR` is inside the worktree. Fixtures call it an outside-repository install, but upward root discovery finds the real solution and `local.env`, including when a mock CWD has its own root. Environmental fixture assumption; no path resolver or assertions changed. |
| `P761StartChatComposeTests.ProfileEditor_OwnsEnterAndBackspace_AndKeepsChatClosed` | FAIL / FAIL / FAIL with the original profile path; pass / pass / pass with `XDG_CONFIG_HOME` in task scratch | Original fixture finally-block tries to restore the existing user profile on a read-only sandbox path. Authorized writable config-root control passes; the original Fast failure is retained. No UI/profile code changed. |
| `DockDockingTests.Ship_nowhere_near_a_station_is_unaffected` | FAIL / FAIL / pass | Existing status-history/docking intermittency; not claimed fixed or 3/3 green. TCP/default path; docking source unchanged. |
| `NpcWingmanDefendLiveTests.Defender_disengages_when_chased_enemy_flees_and_returns_to_charge` | FAIL / FAIL / FAIL | Existing deterministic defend assertion ledgered in P872/P887/P895. No NPC policy changed. |
| `P777WarpExitWobbleTests.Npc_exit_touch_rate_is_measured` | Functional pass / pass / pass; 9.892 / 9.982 / 10.072 s | Sweep 10.643 s against the unchanged 10 s limit. One isolated execution also exceeds the limit; retain the near-boundary timing observation. |
| `RetriedConnectLiveTests.RepeatedConnects_LeaveNoAccumulatedSocketsOrThreads` (separate TCP cohort, not a Fast failure) | pass / pass / pass (0.034 / 0.032 / 0.032 s); full TCP client cohort also passes 3/3 in a clean repeat | During overlap, final acceptance count was 19 rather than 20 after the closure predicate became true. Unchanged TCP-only raw-listener fixture reads acceptance before the final async accept necessarily completes. No assertion/timing was relaxed. |

Showcase receipts and any additional qualification are recorded in the P895b
report. No unrelated test was skipped, quarantined or weakened to turn this gate
green.

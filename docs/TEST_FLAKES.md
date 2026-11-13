# TEST_FLAKES.md — current state of the AllegianceCore test suite

Authoritative, current-state ledger for `AllegianceCore` suite stability (rewritten
by **P647**; the full historical ledger is preserved verbatim in the History section
at the end).

## P1004 lobby listing qualification — 2026-11-13 UTC

The single full Fast sweep ran **14,167 cases in 561.9 s: 14,139 passed / 8 failed /
20 skipped**. All 18 new P1004 cases and all nine infrastructure checks passed.
Four existing browser cases had stale row expectations or incomplete
constructor-bypass fixtures for the changed UI; these were corrected, and all
**22 new/updated cases passed three isolated repetitions (66/0/0)**. Those four
cases are task-related fixture updates, not flakes. The full sweep is retained
as **FAIL** and was not repeated.

The remaining four failures and two additional timing-only methods were each
run **three times alone** on the final working tree. The calibrated Fast limit
was **11.503 s**; theories below include all ten parameter cases per repetition.

| Method | Sweep observation | Isolated Passed/Failed/Skipped, three runs | Longest case seconds per run |
|---|---|---|---|
| `P994GunfireBatchTests.Record_build_has_no_steady_path_allocations` | Expected zero allocated bytes; observed 3024. Not reproduced alone; cause unconfirmed. | **1/0/0; 1/0/0; 1/0/0** | 0.009 / 0.009 / 0.009 |
| `MissileLockLiveTests.MissileLock_BuildsPartialThenFull_AndPosts_Tips_OverTheWire` | Missing partial tip at lock 1.000; 37.835 s. Existing receipt family; fails again in one isolated run. | **1/0/0; 0/1/0; 1/0/0** | 1.491 / 38.575 / 1.767 |
| `DeadLatchRespawnLiveTests.Remote_client_sees_victim_again_after_death_and_respawn` | B never saw A's death/IsDead latch. Existing receipt family; not reproduced alone. | **1/0/0; 1/0/0; 1/0/0** | 1.316 / 1.309 / 1.349 |
| `NpcAutoLaunchLiveTests.Docked_npc_miner_auto_undocks_after_cooldown_then_mines` | Ore remained zero after auto-undock; 31.274 s. Previously ledgered family; not reproduced alone. | **1/0/0; 1/0/0; 1/0/0** | 3.441 / 2.827 / 3.430 |
| `P1000CivProfileTests.Every_loaded_civ_has_immutable_specs_and_legal_routes` | Passed, but civ 3 took 12.261 s in the sweep. All ten theory cases run per isolated repetition. | **10/0/0; 10/0/0; 10/0/0** | 1.834 / 1.843 / 1.800 |
| `P999CivStrategyTests.Every_profile_is_reachable_in_catalog_and_actual_side_buy_gates` | Passed, but civ 5 took 15.503 s in the sweep. All ten theory cases run per isolated repetition. | **10/0/0; 10/0/0; 10/0/0** | 4.483 / 4.462 / 4.469 |

Passing isolated repeats do not clear a failed sweep. No gunfire, missile-lock,
remote-death, miner movement or strategy algorithm was changed for P1004.
There is no baseline reproduction claim for the new gunfire allocation
observation; its cause remains unconfirmed. The existing missile receipt failure
still reproduces alone.

Raw results: `.scratch/p1004/fast/`, `.scratch/p1004/tests/qualified-*.trx` and
`.scratch/p1004/reproductions/`. Portable evidence:
[qualification](reports/P1004_assets/qualification.json),
[isolated unrelated methods](reports/P1004_assets/reproductions.json).
The [P1004 report](reports/LOBBY_LISTING_SLOTS_P1004.md) also records that the
required real-window captures are blocked by this lane's missing OpenGL/EGL
runtime; this is not presented as a completed visual gate.

## P1007 skirmish alert qualification — 2026-11-13 UTC

Exactly one full Fast sweep: **14,140 Passed / 5 Failed / 20 Skipped**, 582.240 s;
all nine infrastructure checks passed (0.059 s). The calibrated Fast limit was
**14.562 s** (the runner's generic error text still says “10 second limit”).
Four executions exceeded it: P796 varied fits civ 35 **18.319 s**, SC2 support
civ 3 **18.181 s**, missile-lock tip **37.861 s**, P999 profile reachability
civ 5 **26.995 s**. Original gate result remains **FAIL**.

One failure was task-related fixture maintenance, not a flake:
`P906TickOrderTests.Bounce_recalculates_tail_and_discovers_previously_absent_projectile_contact`
expected collision type 0; native NoWarn/NoDebris/NoFlare now correctly reach
the callback as 224. Only that expected byte changed; movement, event order,
contact timing and projectile assertions remain. The entire corrected P906
class passed **24/0/0 ×3**. Alert cases passed **19/0/0 ×3** and ship/station
collision cases **5/0/0 ×3** (144 task case executions, zero failed/skipped).

The four non-task failure signatures are the existing missing partial-lock tip
at lock 1.000; remote death latch never arriving after 2500 ticks; warp-bomb
deadline one ULP above the inclusive bound (`5.0000000000000009 > 5`); and
silent-peer seating measured at 32.100 s outside [28,31]. Their lock, respawn,
timer and seating implementations/fixtures are unchanged. Every distinct failed
or slow non-task method was run three times alone, with all its theory cases:

| Method | Isolated P/F/S, repeats 1–3 | Longest case seconds, repeats 1–3 |
| --- | --- | --- |
| `DeadLatchRespawnLiveTests.Remote_client_sees_victim_again_after_death_and_respawn` | 1/0/0; 1/0/0; 1/0/0 | 1.334 / 1.343 / 1.303 |
| `MissileLockLiveTests.MissileLock_BuildsPartialThenFull_AndPosts_Tips_OverTheWire` | 1/0/0; 1/0/0; 0/1/0 | 1.508 / 1.545 / 38.537 |
| `P796VariedLoadoutTests.Varied_equips_every_legal_hull_and_part_of_the_role_over_many_purchases` | 3/0/0; 3/0/0; 3/0/0 | 5.582 / 5.528 / 5.461 |
| `SC2NaniteSupportTests.Support_selection_obeys_research_dock_budget_and_mounts` | 2/0/0; 2/0/0; 2/0/0 | 4.183 / 4.159 / 4.168 |
| `LiveSideSelectTests.Flag_on_auto_seats_a_silent_peer_at_the_configured_30_seconds` | 0/1/0; 0/1/0; 0/1/0 | 1.204 / 1.052 / 1.036 |
| `P739TimerLiveTests.Warp_bomb_notice_matches_captured_blast_deadline_and_stays_in_mission` | 3/0/0; 2/1/0; 3/0/0 | 0.971 / 0.936 / 0.918 |
| `P999CivStrategyTests.Every_profile_is_reachable_in_catalog_and_actual_side_buy_gates` | 10/0/0; 10/0/0; 10/0/0 | 5.653 / 4.611 / 4.627 |

Missile-lock and the timer boundary remain intermittent in isolation. Seating
failed all three isolated runs: 32.150000000000297, 31.000000000000309 and
32.200000000000294 s against [28,31]. Respawn passed all three; the passing
but slow sweep methods rechecked below the calibrated limit. This does not waive the original sweep failures or declare the full
gate green. No second full Fast sweep was run. Details and compact evidence:
[SKIRMISH_GARRISON_ALERT_P1007](reports/SKIRMISH_GARRISON_ALERT_P1007.md),
[validation](reports/P1007_assets/validation.json). Raw logs/TRX are under
ignored `.scratch/p1007/`.

## P1001 commander/fleet Fast qualification — 2026-11-12 UTC

The one full Fast sweep ran **14,149 cases in 560.3 s: 14,123 passed / 6 failed /
20 skipped**. All 33 new P1001 Fast cases and all nine infrastructure checks
passed. Four executions exceeded the calibrated **11.517 s** limit. The gate
remains **FAIL**. Every distinct failed or slow method was run three times alone
on current and archived dispatch-base `16e166a1`, alternating trees. Theory
filters include every parameter case; counts below are Passed / Failed / Skipped
per repetition, followed by the longest case duration per repetition.

| Method | Current, three isolated runs | Dispatch base, three isolated runs |
|---|---|---|
| MissileLockLiveTests.MissileLock_BuildsPartialThenFull_AndPosts_Tips_OverTheWire | **0/1/0; 0/1/0; 1/0/0**; max s: 38.200 / 38.195 / 1.554 | **0/1/0; 0/1/0; 0/1/0**; max s: 38.189 / 38.177 / 38.191 |
| P814GunnerTests.P814b_TurretCarrier_pref_keeps_or_beats_the_Best_hulls_turret_seats | **1/0/0; 1/0/0; 1/0/0**; max s: 3.334 / 3.418 / 3.500 | **1/0/0; 1/0/0; 1/0/0**; max s: 3.451 / 3.432 / 3.431 |
| SC2NaniteSupportTests.Support_selection_obeys_research_dock_budget_and_mounts | **2/0/0; 2/0/0; 2/0/0**; max s: 4.329 / 4.320 / 4.277 | **2/0/0; 2/0/0; 2/0/0**; max s: 4.442 / 4.478 / 4.326 |
| GameOverPlayersWireTests.Rejoined_player_accumulates_time_across_stints_in_roster | **1/0/0; 1/0/0; 1/0/0**; max s: 0.557 / 0.547 / 0.562 | **1/0/0; 1/0/0; 1/0/0**; max s: 0.565 / 0.572 / 0.521 |
| LiveSideSelectTests.Flag_on_auto_seats_a_silent_peer_at_the_configured_30_seconds | **0/1/0; 1/0/0; 0/1/0**; max s: 1.002 / 0.985 / 0.999 | **0/1/0; 1/0/0; 0/1/0**; max s: 0.995 / 0.958 / 0.996 |
| NpcWingmanDefendLiveTests.Defender_disengages_when_chased_enemy_flees_and_returns_to_charge | **1/0/0; 1/0/0; 1/0/0**; max s: 1.436 / 1.434 / 1.442 | **1/0/0; 1/0/0; 1/0/0**; max s: 1.430 / 1.409 / 1.351 |
| P739TimerLiveTests.Warp_bomb_notice_matches_captured_blast_deadline_and_stays_in_mission | **1/2/0; 2/1/0; 2/1/0**; max s: 0.473 / 0.551 / 0.528 | **3/0/0; 3/0/0; 3/0/0**; max s: 0.510 / 0.549 / 0.527 |
| P968PublisherStreamIndexTests.Reliable_backlog_measures_pending_scan_work | **8/0/0; 8/0/0; 8/0/0**; max s: 1.478 / 1.487 / 1.468 | **8/0/0; 8/0/0; 8/0/0**; max s: 1.482 / 1.482 / 1.478 |
| WarpTransitServerTests.ShipInsideWarp_GetsTransited_AndPeerSeesSectorChange | **1/0/0; 1/0/0; 1/0/0**; max s: 0.307 / 0.305 / 0.312 | **1/0/0; 1/0/0; 1/0/0**; max s: 0.304 / 0.295 / 0.295 |

Sweep observations: missile lock lacked its partial tip at full lock; warp transit
collected initial-miner statuses instead of the expected own-ship status; the
warp-bomb deadline was `5.0000000000000009` against an upper bound of 5; NPC
defender distance increased from 163.2 to 218.8; auto-seat elapsed time was
31.500000000000313 against [28,31]; roster stint elapsed time was 8.14999962
against [4,8].
These existing native receipt/timing/NPC families remain qualified as shown,
including failures reproduced alone and any boundary not reproduced on the three base
runs. Passing repeats do not erase sweep failures or establish that they are cleared.
The other overruns were support selection civ 25 (13.594 s), turret-carrier
selection (13.054 s), and 400-peer publisher scan measurement (11.641 s).
No publisher, timer, native NPC, missile-lock, roster or auto-seat behavior was
changed for this task. Native bot movement changes are confined to the ON marker.

Raw receipts: `.scratch/p1001/fast/` and `.scratch/p1001/fast-reproductions/`.
Counts, durations and current/base case rows are also in
[P1001 qualification evidence](reports/P1001_assets/qualification.json). The
[P1001 report](reports/CIV_STRATEGY_COMMANDER_P1001.md) separately retains OFF
arrival observations and task-development failures.

## P1001 OFF comparison qualification — 2026-11-12 UTC

An attempted new G/P/O comparison stopped in its **OFF** arm at the unchanged
five-second stationary-target predicate: an Omicron bomber had a station target
and zero velocity. The ON arm had not run. An OFF-only copy of that exact guard
was then run three times alone on each tree, alternating current and archived
dispatch base `16e166a1`, with the same map/loadout seed 1001 and artwork.

| OFF-only original guard | Passed / Failed / Skipped per run |
|---|---|
| Current tree | **1/0/0; 1/0/0; 0/1/0** |
| Dispatch base | **1/0/0; 1/0/0; 1/0/0** |

The current-tree failure was a defending Phoenix scout with a ship target and
zero velocity. The base controls did not reproduce it; this remains an
**unresolved intermittent OFF observation**, not a confirmed base reproduction
or a cleared flake. No OFF movement code or threshold was changed. The new
comparative fixture records OFF idle duration and asserts the five-second bound
for ON. Existing P700/P707 tests remain unchanged and strict for both arms.
The temporary control class was removed before the final build.

Raw evidence: `.scratch/p1001/off-parking-controls/` and
`.scratch/p1001/quorum-pre-off-fix/`. Final gate and never-parks results are in
[the P1001 report](reports/CIV_STRATEGY_COMMANDER_P1001.md).

## P1001 OFF showcase arrival qualification — 2026-11-12 UTC

The first original default OFF showcase trial failed its existing arrival guard:
**20/30 reached** against 24 required after 480.1 wall seconds. It still recorded
3,416 shots / 1,211 hits / 13 kills, and its longest stationary-target interval
was **1.903 s**. The unchanged five-second parking guard did not fail.

The exact default method was then run three times alone on current and archived
dispatch-base `16e166a1`. Current's next three original trials supply its isolated
repetitions; none of the original five was discarded.

| `P700ShowcaseLiveTests.Local_showcase_never_parks_with_target_for_five_seconds` | Passed / Failed / Skipped per isolated run |
|---|---|
| Current, original trials 2–4 | **1/0/0; 1/0/0; 1/0/0** |
| Dispatch-base follow-ups | **1/0/0; 1/0/0; 1/0/0** |

Current's fifth original OFF trial also passed: the original OFF set is
**4 passed / 1 failed / 0 skipped**, with no parking-limit violation. Initial base
qualification was **5/0/0**. The follow-ups did not reproduce the arrival miss on
base; the original remains an **unresolved intermittent OFF arrival observation**,
not a confirmed base reproduction or a waived gate. Home-swapped/batched variants
and all six P707 cases passed in OFF's first cohort. Test source and limits are
unchanged. Evidence: `.scratch/p1001/{neverparks,arrival-controls}/`.

## P997 construction-effect gate qualification — 2026-11-11 UTC

The single complete Fast sweep ran **14,044 cases: 14,035 passed, 6 failed,
3 skipped** in 576.7 s (calibrated Fast limit 11.021 s). The three skips are
existing optional private Meshy-pack tests; artwork is configured. Every new
P997 case passed, including all 37 new tests repeated three times alone
(**111 passed / 0 failed / 0 skipped**). Solution build: 0 warnings/errors;
all 132 tool projects built. The additional headless artwork decoder case was added after the one sweep and
is included in the final three-run focused cohort. No second Fast sweep was run.
No physics, missile, profile, seating, mission
rotation, bot decision or server production source changed.

| Sweep failure or slow method | Three isolated repeats | Qualification |
|---|---|---|
| `P761StartChatComposeTests.ProfileEditor_OwnsEnterAndBackspace_AndKeepsChatClosed` | fail/fail/fail in original environment; **pass/pass/pass** with writable scratch `XDG_CONFIG_HOME` (0.070/0.070/0.058 s) | Existing sandbox obstruction restoring the read-only real user profile. No real profile or product/profile source was changed. |
| `ChatTargetComposeLiveTests.ComposeDefault_Team_SendsToTeammate_NotEnemy` | **pass/pass/pass**, 1.136/1.132/1.136 s | Existing named-sender seating-order race; sweep asserted that the sender had no teammate. |
| `MissileLockLiveTests.MissileLock_BuildsPartialThenFull_AndPosts_Tips_OverTheWire` | **fail/fail/fail**, 38.374/38.319/38.393 s | Known missing partial-tip receipt despite lock 1.000. Persistent alone; remains open and also exceeds Fast's time limit. Not described as cleared or solely load-induced. |
| `NpcDodgeLiveTests.Traveling_npc_does_not_dodge_the_station_it_is_docking_at` | **pass/pass/pass**, 0.668/0.661/0.660 s | Sweep's existing docking-deviation intermittent: `maxLineDeviation=241.8` against a 200 bound. No dodge or movement source changed. |
| `BotFlightTests.Bot_trajectory_matches_independent_ExecuteMove_rollout` | **pass/pass/pass**, 1.080/1.095/1.079 s | Existing independent-rollout intermittent: sweep drift 13.37 units at tick 147. |
| `MissionRotationLiveTests.Finished_mission_rotates_to_fresh_notstarted_and_new_joiner_starts` | **pass/pass/pass**, 1.899/1.984/1.962 s | Known empty decoded stage-notification collection at line 181; rotation source unchanged. |
| `P814GunnerTests.P814b_TurretCarrier_pref_keeps_or_beats_the_Best_hulls_turret_seats` | **pass/pass/pass**, 3.199/3.122/3.149 s | Sweep passed at 12.756 s, over the calibrated limit; below it alone. |
| `SC2NaniteSupportTests.Support_selection_obeys_research_dock_budget_and_mounts` | **2/0/0 each repeat**, longest case 3.992/4.018/4.045 s | Sweep's civ-25 case passed at 12.583 s; both civilization cases pass below the limit alone. No training/Nanite source changed. |

The Fast sweep remains **qualified FAIL**, not a green full-suite result.
Raw receipt files: `.scratch/p997/fast/`, `.scratch/p997/isolated/`.
Construction effect report: [P997](reports/CONSTRUCTION_EFFECT_P997.md).
The final showcase comparison passed **5/5 base and 5/5 changed**, no skips or parking failures; detailed observations are in the report.

## P988 golden qualification — existing P980 baseline drift, 2026-11-09 UTC

Before the P988 adapter was built into the proof tool, the untouched HUD-2
binary compared the full P980 matrix: 1,848 cases, **1,764 pixel differences,
42 semantic differences, 0 corrupt goldens**. The original binary was retained.
Its `flight/800x480/s0.75/dark` pair reproduced **3/3 alone**, each with exactly
11,951 changed Classic pixels and 12,254 changed Modern pixels, no semantic
difference. This is repeatable baseline/environment drift, **not a load flake**
and not introduced by P988. The full initial comparison also reports target
stat text differences (`SHLD 30%` versus `SHLD 30 %`). Existing P980 goldens are
untouched. P988 uses a separate capture of that original dispatch binary to
isolate this task's before/after result; see
[the P988 report](reports/SKINS_1C_SWITCHING_P988.md). Raw evidence is under
`.scratch/p988/{before,p980-repro-1,p980-repro-2,p980-repro-3}/` in its worktree.

P988's single Fast sweep also hit the known ephemeral dual-transport bind
collision in both `P897ClientTransportTests.Reconnect_reuses_initial_protocol_even_after_preference_changes`
protocols. Both cases passed **3/3 alone (2/0/0 each run)**. No transport/server
code changed. An adapter-related source guard,
`P804ClientLeadTests.Round_border_is_drawn_for_both_styles`, still expected the
Classic radar call in `Game1.Rendering.cs`; it was updated to check the explicit
resource binding and adapter route. The complete affected set, including all
P804 tests and the final skin/training changes, then passed **181/0/0 × 3**.
Raw follow-ups: `.scratch/p988/retries/` and `.scratch/p988/latest-targeted/`.
After the final allocation-only input-poll optimization, the expanded affected
class filter passed **212/0/0 ×3**, including all 30 new switching cases and
both preset allocation cases (**0 B / 100,000 frames** each). The complete
existing training-integration class is included. Final receipts:
`.scratch/p988/final-affected/`.

The completed single Fast invocation executed **13,886 cases in 987.93 s**:
**13,877 passed / 6 failed / 3 optional private-pack skips**; nine infrastructure
checks passed. Remaining unchanged failure/timing families were repeated in
three fresh filtered processes:

| Family | Isolated Passed/Failed/Skipped | Classification |
|---|---|---|
| `P739TimerLiveTests.Warp_bomb_notice_matches_captured_blast_deadline_and_stays_in_mission` | **2/1/0, 3/0/0, 3/0/0** | Existing floating upper-bound signature; sweep delay 5/7 values exceeded the inclusive bound by floating-point rounding. Mixed outcome remains unresolved; no timer/Core code changed. |
| `NpcPilotTypeGateLiveTests.Non_miner_idle_npc_stands_by_despite_minable_asteroid` | **1/0/0 ×3**, 2.449/2.449/2.456 s | Existing idle-NPC movement signature (sweep travelled 169.868; previously led P887/P947 family); no NPC/sim code changed. |
| `P777WarpExitWobbleTests.Npc_exit_touch_rate_is_measured` | **1/0/0 ×3**, 10.385/10.440/10.638 s | Assertions pass, but all three remain above the unchanged 10 s Fast limit; timing issue remains open. Sweep 12.285 s. |
| `ClientWarpApproachSlowHullLiveTests.Slow_board_hull_26_transits_when_the_warp_approach_flies_through_the_aleph` | **1/0/0 ×3**, 7.816/7.687/8.174 s | Sweep assertions passed at 12.858 s; isolated runs are below the same Fast limit. |

P988 never-parks trials passed **base 5/5 and changed 5/5**, no skips. These
unseeded functional controls do not establish a performance effect. Raw gate,
follow-ups and receipts are retained under `.scratch/p988/{fast,retries,base-parks,changed-parks}/`;
summary: `docs/reports/P988_assets/qualification.json`. Training qualification
and any window failures are recorded in the P988 report.

The exact Classic/reminders window case in
`P912TrainingWindowTests.Basic_flight_completes_with_muted_missing_or_repeated_narration`
reproduced **0/1/0 ×3 alone** at the same `M2 9.2: native prompts missing or out
of order` guard. This is the previously recorded P945/P974 reminder-proof
signature: the lesson actually docks/completes before the prompt snapshot
fails. P988 changes no narration, prompt collection, flight control or lesson
conditions. The current full window gate retains its failures; neither this
guard nor a time limit was relaxed. The exact dispatch control (original Program.cs rebuilt against the retained
pre-adapter Allegiance assemblies) also failed at **the same M2 9.2 prompt
snapshot**, after native mission completion. The baseline Game assembly hash
matches the retained original binary. Modern's exact reminder case also reproduced **0/1/0 ×3 alone**, at the same
prompt-order guard. One broad Classic/reminders run hit the earlier
`P940 6.13 all-axes/mouse-turn` control assertion; that assertion passed in all
three isolated Classic replays before the later prompt snapshot failed. The
single earlier-control occurrence is preserved as unresolved window flakiness,
not promoted to a passing result. Repetitions are under
`.scratch/p988/reminder-retries/`; the control is in
`.scratch/p988/base-training-control/` and its adjacent log/result JSON.

One full training pass timed out in Modern Command View (`mission: 5`) at
goal `4.3`: three probes existed, but the three layer ships were dead and the
observer never advanced. The exact case then passed **1/0/0 ×3 alone**, with the renderer-id
assertion enabled; host load during follow-ups was roughly 18–30. This is
recorded as a window flake under the lane's isolated-replay rule. No earlier
matching signature or original-baseline reproduction was established, so it
is not claimed as a confirmed pre-existing defect. No guard or timeout changed.
The fresh-window `Assigned_lesson` case for mission 5 / lesson 5 in the
later excluded interrupted run also stalled
at `4.3`; that exact case passed **1/0/0 ×3 alone**. A control using the
original dispatch proof/assemblies and complete unchanged Content tree passed
lesson 5, so the stall remains unconfirmed on the original baseline. An
incomplete-content preliminary control is excluded as a fixture error, not
classified as a product regression. Raw evidence:
`.scratch/p988/{m5-retries,m5-prefix-retries}/`, the complete training TRXs,
and `.scratch/p988/base-command-view-control.log`.

The mission-5/lesson-4 fresh prefix also timed out at the same goal in a full
training pass. Its exact isolated cases were **1/0/0, 1/0/0, 0/1/0** (3m13s,
3m07s, 20m00s). The last replay again had three probes and dead layer ships.
**This case remains OPEN**, not cleared by the passing full-course and
lesson-5 replays. Three controls using original dispatch proof/assemblies,
complete matching Content, identical lesson-4 arguments and the same software
renderer all passed. The stall is therefore **not confirmed on the original
baseline**. No probe/AI/lesson condition or timeout was altered by P988.
Raw: `.scratch/p988/m5-lesson4-retries/` and
`.scratch/p988/base-lesson4-controls/`; control outcomes are in
`.scratch/p988/base-lesson4-controls-result.json`.
The final Game binary subsequently passed the exact lesson-4 prefix **3/0/0**
in separate real-window replays, and its audited replacement training pass
also passed that prefix. The replacement's full Modern Command View course
timed out at `4.3`; its exact full-course final-binary replays then passed
**3/0/0**. Under the lane's isolated-replay rule this is a load-sensitive
window observation; the earlier mixed result is retained, and no causal fix
or original-baseline reproduction is claimed. Final receipts:
`.scratch/p988/final-lesson4-controls-result.json` and
`.scratch/p988/final-course5-controls-result.json`, with adjacent window logs.

**Claude review (2026-11-10): cause found, not P988.** Goal 4.3 is
`GetCommandCondition(stealth fighter, Attack)` (`Mission5.cs:343`, native
mission5.cpp:535-573), not the scout-destroyed goal. The p912 proof issues the
attack order early, during goal 4.1's `tm_5_15` voice line. If the stealth
fighter kills the scout before goal 4.3 starts, the attack command is already
cleared when 4.3 begins checking, and the order cannot be repeated against a
dead target, so the goal waits until the 20-minute timeout. Stalled replay:
kill `run.log:1067` before goal 4.2 starts (`:1086`). Passing replays: kill
after 4.2 starts (for example `:1057` after `:1009`). The margin is about 1 s of
combat speed. Paired review runs (both trees in parallel, same host load):
P988 tree 2/3, clean 6659cb91 base 3/3. The mechanism is in the proof driver's
order timing against sim combat, not in any HUD/skin code. Candidate fix
(separate task): issue the attack order only after goal 4.3 starts, as a
player following the prompt would. The M2 reminder guard (`M2 9.2: native
prompts missing or out of order`) failed **0/3 on both trees** in the same
paired runs; it is the existing P945/P974 family above, not P988.
Separately, a narrow filter of the visuals/HUD/training-integration classes
(`Tier=Fast` and VisualFlags, P979, P982, HdrFoundation, SimPalette, P757,
TrainingIntegration, ModernHudSettingsInput, P804) fails
`P910TrainingIntegrationTests.Every_exit_restores_preferences_keymap_and_overlay(style: Modern, route: "window-close")`
**6/6 on master 97a1c19d** (restored keymap `FireWeapon` 119 vs 120). This is an
ordering/shared-keymap dependency in that subset, not P988; the full MonoGame
`Tier=Fast` project passes 5,129/0 on the merged P988 branch.
Three valid training groups completed **1,245 passed / 9 failed / 6 skipped**
out of 1,260 executions. Each Core group is 126/0/0. MonoGame groups are
**290/2/2**, **288/4/2**, and **289/3/2**. The final group uses thirteen
disjoint partitions; both discovery and the executed case-name multiset match
the serial 294-case run exactly. All mandatory Nanite windows complete.
The original interrupted qualification group is excluded after a controller
error while replacing its outer timeout; it has no final MonoGame TRX.
No condition was relaxed, and the full training gate remains **FAIL**.
Raw valid groups: `.scratch/p988/{training-pass-2,training-pass-3,training-replacement}/`.

## P987 qualification — recorded bot decisions, 2026-11-09 UTC

The single Fast invocation completed **13,862 cases in 609.9 s**: **13,858
passed / 1 failed / 3 not executed** (the optional private Meshy pack). The
failure and all three timing offenders passed **3/3 alone**. P987 changes only
the MCP recorder and small pure bot-policy cores; no server timer, physics,
turret or repair rule changed.

| Sweep failure / timing family | Three isolated runs | Classification |
|---|---|---|
| `P739TimerLiveTests.Warp_bomb_notice_matches_captured_blast_deadline_and_stays_in_mission(delay: 5)` (one-ULP upper bound) | **3/3 pass**, all five theory cases each time | Existing floating deadline-boundary family; timer code unchanged. |
| `P814GunnerTests.P814b_TurretCarrier_pref_keeps_or_beats_the_Best_hulls_turret_seats` (10.856 s, assertion passed) | **3/3 pass**, about 3 s | Existing sweep-only timing family. |
| `SC2NaniteSupportTests.Support_selection_obeys_research_dock_budget_and_mounts` (civ 25, 11.413 s, assertion passed) | **3/3 pass**, both civ cases each time | Existing sweep-only timing family; isolated method runs about 7 s. |
| `P777WarpExitWobbleTests.Npc_exit_touch_rate_is_measured` (12.244 s, assertion passed) | **3/3 pass**, about 10 s | Existing near-limit native-physics family. |

The first recorder-off never-parks cohort was **5/5**. After a behaviour-neutral
review cleanup (preserving defence mutation order, filtering ineligible factions
before topology work, and removing a recorder-off closure), the required final
cohort was **3/5** at load average about 24: three passes, one arrival-only
18/30 failure whose 2.209 s parking bound still passed, and one support-idle
failure. Three isolated retries for **each** failed run passed **6/6 total**;
all six reached 24/30 with real hits and longest speed-below-one-with-target
intervals of 1.215–3.523 s. The unchanged P985 baseline is 4/5 initial with 3/3
retries, so this is retained as a load flake, not hidden as a 5/5 final cohort.
Raw evidence: `~/allegiance-evidence/P987/`.

## P985 qualification — remote-ship visual timeline, 2026-11-09 UTC

The required single Fast invocation completed **13,779 cases in 586.3 s**: **13,774
passed / 2 failed / 3 not executed** (the optional private Meshy pack). Nine
infrastructure checks passed. No P985 timeline, P813b, cache-retirement, dead-reckon
or visual-flag test failed. The two unrelated failures and the four timing-only
exceedances received three isolated repetitions on the changed tree:

| Sweep failure / timing family | Three isolated runs | Classification |
|---|---|---|
| `ThreePeerSeededSessionTests.SeedThreePeers_TwoSameSide_OneCrossSide_DeterministicVisibility` (expected side 1, saw lobby side -2) | **2 pass / 1 fail**, same lobby/logon-drain signature | Existing seating/logon timing family; the P974/P967-era entries include the same signature and changing isolated outcomes. No server/session/seed path changed by P985. |
| `MissileLockLiveTests.MissileLock_BuildsPartialThenFull_AndPosts_Tips_OverTheWire` (38.369 s timeout in sweep) | **3/3 pass**, about 1 s each | Existing missing-partial-tip receipt family under sweep load; no missile/lock/tip path changed. |
| `NpcDodgeLiveTests.Traveling_npc_does_not_dodge_an_enemy_side_ship_on_its_path` and `Traveling_npc_steers_around_an_asteroid_on_its_path_and_resumes` (passed at 10.183/11.194 s) | **3/3 pass each**, about 7–8 s | Existing timing-only exceedances under sweep load. |
| `P777WarpExitWobbleTests.Npc_exit_touch_rate_is_measured` (passed at 10.127 s) | **3/3 pass**, about 9 s | Existing near-limit native-physics timing family; no warp/sim rule or duration limit changed. |

The initial serial showcase never-parks gate was **4/5**: runs 1/2/3/5 passed at
176.5/151.3/381.6/173.7 s, reaching 24/30 with real combat and longest
speed-below-one-with-target intervals of 1.906/2.022/2.011/2.006 s. Run 4 reached
19/30 at the separate 480 s arrival deadline (shots 1830, hits 697), but its parking
bound still passed at 2.006 s. Three exact isolated retries passed **3/3**
(456.5/153.5/182.9 s, all reached 24/30; longest intervals 0.601/2.006/1.716 s).
The unchanged P975 baseline is 5/5; P967 already records the same 4/5 arrival-only
outcome. P985 changes no bot, server, physics or showcase decision path. Raw TRX/logs:
`.scratch/p985/fast/` and `.scratch/p985/showcase/` in the task worktree.

## P974 qualification — client ownership, 2026-11-09 UTC

The targeted P813c family passed twice, then two existing ACK-receipt assertions
failed in its third run: `Name_collision_keeps_the_seat_parked_and_the_original_resumes`
read ship ID 0 instead of 8098, and
`Wrong_token_refuses_only_the_claimant_and_the_original_resumes` read a null ACK.
Both fixtures wait for the server's `ReconnectResumedTotal` and immediately inspect
the client's ACK (`P813cReconnectGraceTests.cs:471-473,541-543`); that server-side
counter can precede delivery on the receive thread. Each method then passed
**3/3 alone (1 passed / 0 failed / 0 skipped per run)**. No server/reconnect-grace
rule changed. Raw evidence: `.scratch/p974/evidence/repeat-final/` and
`.scratch/p974/evidence/flakes/`; final qualification is recorded in
[the P974 report](reports/CLIENT_SESSION_OWNERSHIP_P974.md).


The completed Fast gate has **13,635 passed / 3 failed / 3 skipped** across
13,641 executions (964.5 s, one project at a time). The three skips are the
optional private Meshy pack. The default shared lock blocked the bounded queued
attempts; the completed gate used the runner's task-local lock override.

| Completed Fast failure/timing family | Three isolated runs (passed/failed/skipped) | Classification |
|---|---|---|
| `MissileLockLiveTests.MissileLock_BuildsPartialThenFull_AndPosts_Tips_OverTheWire` | **0/3/0**, same ~38 s timeout | Existing missing partial-tip receipt; the P966 entry below includes a failing clean-base control. It also accounts for the 38.468 s slow-Fast execution. |
| `P813cReconnectGraceTests.Voluntary_logoff_is_immediate_and_reports_quit` | **3/0/0** | Existing quit-receipt race (`Assert.Contains` over an empty list), already recorded in this ledger. |
| `LiveSideSelectTests.Flag_on_auto_seats_a_silent_peer_at_the_configured_30_seconds` | **2/1/0**; isolated failure 31.60 s, sweep 31.75 s, fixture range [28,31] | Existing server-deadline/client-receipt clock boundary; P966 has clean-base controls. |
| `P777WarpExitWobbleTests.Npc_exit_touch_rate_is_measured` (assertions passed; sweep 11.264 s > 10 s) | **3/0/0**, 9.394/9.424/9.452 s | Existing timing family, also recorded by P966. |

An earlier, incomplete sweep saw
`Hud12bReviewFixTests.Chat_line_truncation_matches_the_native_clip_and_is_cached_per_source_and_width`
allocate 6,072 B; it then passed **3/3 alone** and passed in the completed sweep.
Temporary-path fixture failures were environmental: scratch beneath this checkout
let `PlayerBundlePathsTests` discover the checkout's `local.env`. A directory-FD
alias preserves logically outside-repository paths with all physical writes still
inside workspace scratch; all nine fixture cases pass **3/3 (27/0/0)**. A writable
scratch XDG profile also resolves the earlier read-only default-profile failure.

The supplementary training-window attempt reproduced the already-recorded P945
`P912TrainingWindowTests.Basic_flight_completes_with_muted_missing_or_repeated_narration(Classic, reminders)`
failure. It then reproduced **0/3/0 alone**, about nine minutes each: the actual
lesson docks and completes, but the proof's five-entry prompt snapshot rejects a
repeated booster prompt. Training scripts, host, input and proof source are
unchanged. This is a known pre-existing reminder-proof mismatch; no complete
training-window gate is claimed for P974. The client-core training filter passed
126/0/0. Raw training follow-ups are in `.scratch/p974/evidence/training-flakes/`.

All **22 new P974 cases pass 3/3 (66/0/0)**, updated transport/lobby reflection
callers pass **150/0/0**, and the final departure/ownership/reconnect group passes
**90/0/0**. Showcase never-parks is **5/0/0** on both the base and changed tree.

## P977 qualification — eject targeting/log redaction, 2026-11-09 UTC

One complete Fast invocation (`TestResults/tiers-20261109T082904.709886Z/`):
**13,624 passed / 5 failed / 3 skipped** out of 13,632 cases (the skips are the
optional absent Meshy pack); nine infrastructure checks passed. One failure was
P540's outdated expectation that an enemy RescueAny probe is ignored, directly
contrary to the native rule P977 adds. Its test now expects the nearest RescueAny
probe regardless of side and passes **3/3** (`EjectRescueTargetTests`, four cases
per run). The other four failures received three isolated exact-method runs:

| Sweep failure | Three isolated runs | Disposition |
|---|---|---|
| `Hud12bReviewFixTests.HUD_formatted_readouts_allocate_nothing_in_steady_state` (1,208 B over 10,000 frames in the sweep) | **pass / pass / pass**, 15 ms each | Existing load-sensitive allocation measurement; no HUD code changed. |
| `P817NativeMinerDecisionCycleLiveTests.Idle_npc_repeats_mine_sell_cycle_indefinitely_after_first_sell` (54.167 s sweep timeout; also SLOW FAST) | **pass / pass / pass**, 3–4 s each | Existing miner receipt/timing family; no NPC/miner/sim code changed. |
| `LiveSideSelectTests.Flag_on_auto_seats_a_silent_peer_at_the_configured_30_seconds` (32.35 s against [28,31]) | **fail / fail / fail**, same 32.x boundary signature | Already-ledgered auto-seat receipt/simulation-clock boundary (P945/P948/P953/P960/P966/P967 entries); still failing alone and not cleared by P977. No side-select/auto-seat code changed. |
| `KillBonusLiveTests.Real_end_to_end_projectile_kill_increases_kill_bonus` (missing kill receipt in the sweep) | **pass / pass / pass**, 0.4–0.5 s each | Load-sensitive kill receipt; no combat/projectile code changed. |

The sweep's other SLOW FAST entries were P750 native asteroid types (11.706 s),
P942 research ordering (10.662 s), and P777 warp-exit timing (10.522 s), all
assertion-passing known timing families.

Showcase never-parks (`P700ShowcaseLiveTests.Local_showcase_never_parks_with_target_for_five_seconds`),
five isolated runs after the allied bay-approach change: **5 pass / 0 fail /
0 skipped**, matching P961's recorded unchanged-base 5/5 rate. Runs took
168.5/466.9/151.8/172.4/169.4 s, each reached 24/30 with real combat, and the
longest speed-below-one intervals with a target were
2.004/1.812/2.007/2.205/1.905 s. No parking-bound failure occurred.

## P979 validation — visual flags, two Fast sweeps, 2026-11-09 UTC

**Sweep 1** on the P979 tree (`tools/run-tests.sh --tier fast`, 13,664 cases, 565.7 s, run
while another lane's `tools/hud2-proof` golden captures and diffs held one core: load
average 3.2–4.3 on 24 CPUs): **13,660 passed / 1 failed / 3 not executed** (the MonoGame
project's own skip markers). **Sweep 2** on the final tree (13,664 cases, 551.6 s, load
1.6–3.0): **13,658 passed / 3 failed / 3 not executed**. Fast infrastructure checks: PASS
in both. Every failure is an unrelated `Allegiance.Core.Tests` / `Allegiance.ClientCore.Tests`
server-wire/NPC case and belongs to an already-ledgered family:

| test | suite result | isolated |
| --- | --- | --- |
| `MinerDecisionCycleLiveTests.Idle_npc_autonomously_mines_full_bag_and_sells_at_friendly_station` (sweep 1) | fail 13.1 s (`NPC should have docked and sold within the tick budget`), also over the 10 s Fast limit; its P817 twin passed in 0.7 s in the same run | **3/3 pass** (12 s per 3-test run) |
| `P777WarpExitWobbleTests.Npc_exit_touch_rate_is_measured` (sweep 1) | pass, 10.6 s (over the limit; ledgered timing family since P966) | **3/3 pass** |
| `P750NativeAsteroidTypesTests.Research_showcase_rocks_carry_native_models_and_no_rock_names` (both sweeps) | pass, 11.1 s / 11.4 s (over the limit) | **pass** (1 s) alone |
| `MissileLockLiveTests.MissileLock_BuildsPartialThenFull_AndPosts_Tips_OverTheWire` (sweep 2) | fail 38.4 s (`lock never reached full + partial tip (lock 1.000)`) — the ledgered 38.7–38.8 s timeout signature | **1 pass / 2 fail** (pass 1 s, fails 38 s), i.e. the recorded **clean-base** rate (P966 row: 1 pass / 2 fail) |
| `LiveSideSelectTests.Flag_on_auto_seats_a_silent_peer_at_the_configured_30_seconds` (sweep 2) | `31.05` against the fixture's [28, 31] — the ledgered receipt/simulation-clock boundary (recorded clean-base rate 2 pass / 3 fail) | **2 pass / 3 fail** over the mixed cohorts, same one-second signature |
| `LiveShieldRegenBroadcastTests.Stationary_ship_streams_its_regenerating_shield_over_the_wire` (sweep 2) | fail (`target shield never dropped below 0.74 (ended 1)`) | **3/3 pass** (3 s each, load 2.8–4.0) |

No task test failed: the P979 files (`Allegiance.ClientCore.MonoGame.Tests`
`RadialVisibilityTests`, `VisualFlagsTests`, `P979VisualsOptionsTests`,
`P806OptionsMenuTests`, `FlightFarClipParityTests`) are 52/52 in each of three isolated
repetitions, and the MonoGame project passed 4,961/4,964 (3 not executed) in both sweeps.
P979 touches only client rendering, the intro options model and player preferences — no
server/NPC/sim/wire path — so these are shared-host and pre-existing flakes.

## P980 validation — HUD golden baseline (tools/tests only), one Fast sweep, 2026-11-09 UTC

P980 changes no file under `src/`: `tools/hud2-proof` (matrix/baseline/compare), one Live-tier
proof-harness test timeout (`tests/Allegiance.ClientCore.MonoGame.Tests/ModernHudTests.cs`) and
docs. The one Fast invocation reported **13,628 cases, 553.3 s, FAIL** with two unrelated,
already-ledgered intermittent families plus three `SLOW FAST` exceedances of the calibrated
10 s limit under shared-host load (load avg 3.7 on 24 CPUs):

| Sweep failure | First run | Standalone re-runs (same tree) | Classification |
|---|---|---|---|
| `BallotVotingLiveTests.Resign_passing_deactivates_the_side_and_ends_the_mission` | fail, 253 ms — `Assert.Contains()` found the "proposal … has passed (2 for)" announcement but not the expected second line | **3 pass / 3** (0.7 s each) | Message-ordering/timing under load; no ballot or wire source changed by P980 |
| `P877MineDamageLiveTests.Chased_bot_wake_mine_damages_pursuer` | fail, 24 s — `P877 loopback join condition timed out` (also the sweep's slowest test) | **3 pass / 3** (1 s each) | Loopback join under load (the ledgered P877 family); no mine/damage/bot source changed by P980 |

No new test was added to the Fast tier by this slice; the Live-tier proof-harness test
(`ModernHudTests.Both_skins_render_Hud1_scenarios_without_overlapping_text_and_modern_draws_all_visible_elements`)
passes **3/3 alone** (1 m 40 s each) against the enlarged 1848-combination matrix.

Report: [SKINS_1A_BASELINE_P980.md](reports/SKINS_1A_BASELINE_P980.md).

## P975 validation — client robustness, one Fast sweep, 2026-11-08 UTC

One complete Fast invocation on the P975 tree (per-message receive containment,
per-world ship caches, bounded game-over roster): **13,626 passed / 2 failed /
3 not executed**, 13,632 cases in 548.4 s after the shared runner lock wait. The
run shared the host with two concurrent lane Fast sweeps (measured load average
4.3–19.6 during it). Fast infrastructure checks: PASS.

| Sweep failure / timing family | Standalone on this tree | Classification |
|---|---|---|
| `MissileLockLiveTests.MissileLock_BuildsPartialThenFull_AndPosts_Tips_OverTheWire` (38.1 s; "lock never reached full + partial tip (lock 1.000)") | **1 pass / 2 fail** (pass 1 s; fails 38 s) | The open partial-tip receipt failure ledgered since the P933/P935 cohorts (P966 entry: clean base 1/2, changed 0/3, same 38 s signature). No missile-lock/lock-tip source is on the P975 path. |
| `NpcWingmanDefendLiveTests.Defender_disengages_when_chased_enemy_flees_and_returns_to_charge` (`Assert.Same`: target null) | **3 pass / 0 fail** | The ledgered null-target family (P945/P966: fail/fail/fail in both changed and base cohorts); load flake in this sweep, which ran at load 12–19 behind two other lane sweeps. |
| SLOW FAST `P750NativeAsteroidTypesTests.Research_showcase_rocks_carry_native_models_and_no_rock_names` 10.48 s, `P777WarpExitWobbleTests.Npc_exit_touch_rate_is_measured` 10.04 s | not re-run (both PASSED in the sweep) | Ledgered timing-only exceedances of the calibrated 10 s guard; assertions passed. |

P807's 394-peer budget family (ledgered under P960/P965) is process-neighbour
sensitive: inside the combined 29-case affected run it failed **2/3** on the phase
slice (97.8 / 80.5 ms vs the scaled 78.2 ms), and alone on this tree it is
**2 pass / 1 fail** (pass 33–38 s; the failure is the ledgered "declare sim work
157.6 ms over the scaled 150 ms budget" signature at load 27.1). Every P975 change
is client-side (receive containment, per-ship caches, game-over roster); the
measured phase is the server's handoff tick.

New/affected P975 cases: **12/12 pass in each of three isolated runs**. The affected
existing families (remote dead-reckon, P781 ripcord reckon, P813b Hermite, P660
countdown, P636c/P636d snapshot, DecisionSnapshot 200v200 allocation) pass **3/3**;
with P807 excluded the combined set is **28/28 in each of three runs**.

Showcase never-parks (`P700ShowcaseLiveTests.Local_showcase_never_parks_with_target_for_five_seconds`),
five sequential isolated runs on the P975 tree: **5 pass / 0 fail**, walls
147/183/149/187/304 s at load avg 5.9–13.0 — at the recorded base rate (P945/P965: 5/5;
P967: 4/5 with an arrival-assertion failure). Zero parking violations; no bots/flight/sim
path was changed by P975.

## P966 validation — server trust boundaries, one Fast sweep, 2026-11-08 UTC

One complete Fast invocation on the P966 tree (logon re-send bound, claimed-map
catalog gate, reconnect-grace refusals): **13,583 passed / 2 failed / 0 skipped,
3 not executed** (the MonoGame project's own skip markers), 13,586 cases in 551.6 s
after the shared runner lock wait. Fast infrastructure checks: PASS. Both failures
are **pre-existing intermittent families, proven by clean-base controls**: each was
re-run standalone on both the changed tree and an unmodified checkout of the same
base commit (`1f6e1250`) built in a scratch worktree — the failure rates match, and
no P966 source is on either path.

| Sweep failure / timing family | Changed tree, standalone | Clean base `1f6e1250`, standalone | Classification |
|---|---|---|---|
| `LiveSideSelectTests.Flag_on_auto_seats_a_silent_peer_at_the_configured_30_seconds` | **3 pass / 4 fail** over 7 runs (values 32.15–32.40 against the fixture's [28, 31]; 1 s each) | **2 pass / 3 fail** over 5 runs, same 32.x signature | Existing receipt/simulation-clock boundary (also P945/P948/P953/P967 entries): the auto-seat fires at the 30 s deadline but the test's `elapsed` is measured when the FltBot receive thread surfaces `ShipAssignment`, which lags by 1–2 sim-seconds under host load (load avg 0.9–2.9 during validation). No side-select/auto-seat source changed by P966. |
| `MissileLockLiveTests.MissileLock_BuildsPartialThenFull_AndPosts_Tips_OverTheWire` | **0 pass / 3 fail** (38 s each — the timeout signature) | **1 pass / 2 fail** (pass 1 s; fails 38 s) | Known missing partial-tip receipt (ledgered since P935-era; same 38.7–38.8 s durations). No missile-lock/lock-tip source changed by P966. |
| `MissileLockLiveTests` (SLOW FAST 38.299 s) and `P777WarpExitWobbleTests.Npc_exit_touch_rate_is_measured` (SLOW FAST 10.246 s vs the 10.000 s limit) | — | — | Timing exceedances of the same two led families under the shared-host load; assertions pass when the box is quieter. |

Showcase never-parks (`P700ShowcaseLiveTests.Local_showcase_never_parks_with_target_for_five_seconds`),
five sequential isolated runs on the P966 tree: **5 pass / 0 fail**, walls
287/184/357/253/269 s at load avg 1.3–3.3 — at or better than the recorded base
rate (P967: 4/5 with an arrival-assertion failure). Zero parking violations;
no bots/flight/sim path was changed by P966.

All 52 new/affected P966 test cases pass **3/3 standalone** (P966ServerTrustBoundary
+ P813cReconnectGrace + LiveDuplicateLogon + P883MissionCookie + P914StationDeadline
+ P871bCreateGameProvisioning). Exact sweep logs are under `TestResults/tiers-20261109T013853.881216Z/`;
the gate summary is in
[the P966 report](reports/SERVER_TRUST_BOUNDARIES_P966.md).

## P968 qualification — publisher index, 2026-11-08 UTC

The new default-off publisher index has **129/129 new/changed cases passing**
across three isolated repetitions of every affected method. The three flag-on
broadcast cohorts yielded **156/1/0, 157/0/0, 157/0/0** (pass/fail/skip); the
single Fast suite with the server flag off yielded **13,562 passed / 7 failed /
3 skipped** in 528.7 s. Infrastructure and coverage checks passed. The three
skips are the optional absent Meshy pack. Six existing passing executions also
exceeded the calibrated **10.081 s** Fast limit.

Every non-task failing or over-limit method received three standalone runs,
including all theory rows. The sources for these fixtures and the systems they
test were not changed. Exact logs/TRX are under `.scratch/p968/`; concise data
and the gate are in [the P968 report](reports/PUBLISHER_STREAM_INDEX_P968.md).

| Method/family | Three isolated repetitions | Observation |
|---|---|---|
| `AsteroidMinedLiveTests.No_broadcast_fires_once_miner_hits_capacity` | **pass/pass/pass**, flag on; 1.794/1.828/1.806 s | First broadcast sweep received two queued ASTEROID_MINED reports after the empty-receipt check. Existing mining receipt intermittency; both later sweeps pass. |
| `LiveCombatScoringTests.Real_two_peer_kill_updates_both_player_scores` | **pass/pass/pass**, 0.442/0.471/0.458 s | Fast sweep missed KILL_SHIP within its observation budget; existing receipt timeout. |
| `P739TimerLiveTests.Warp_bomb_notice_matches_captured_blast_deadline_and_stays_in_mission` | **3/3 theory cases pass in each repetition**; case maxima 0.471/0.462/0.443 s | Same existing floating upper-bound signature: sweep delay 5 computed `5.0000000000000009 > 5`. |
| Five `PlayerBundlePathsTests` cases across four methods | Same five **fail/fail/fail**; two companion cases pass in each repetition | Required worktree-local TMPDIR lets ostensibly external fixtures discover ancestor `local.env`. Persistent known fixture obstruction; no resolver change. Methods: `UsesExecutableDirectoryOutsideRepository` (Artwork), `MissingDirectoryHasNoDefault` (Artwork/NativeSource), `LocalConfigWinsOverBundleWithBlankEnvironment`, `NativeSourceNeverGuessesAnInstallSubdirectory`. |
| `P800HullProofGpuTests.Every_hull_of_the_real_core_renders_in_hangar_flight_and_target_pane` | **pass/pass/pass**, 3.855/3.850/3.765 s | Sweep 10.697 s; isolated executions below original calibrated limit. |
| `P796VariedLoadoutTests.Varied_equips_every_legal_hull_and_part_of_the_role_over_many_purchases` | All three civ cases **pass in each repetition**; case maxima 5.779/5.839/5.868 s | Sweep civ 35: 11.512 s; isolated executions below original limit. |
| `P814GunnerTests.P814b_TurretCarrier_pref_keeps_or_beats_the_Best_hulls_turret_seats` | **pass/pass/pass**, 3.948/3.600/3.680 s | Sweep 13.791 s; isolated executions below original limit. |
| `SC2NaniteSupportTests.Support_selection_obeys_research_dock_budget_and_mounts` | Both civ cases **pass in each repetition**; case maxima 4.489/4.482/4.426 s | Sweep civs 3/25: 10.542/12.493 s; isolated executions below original limit. |
| `P777WarpExitWobbleTests.Npc_exit_touch_rate_is_measured` | Assertions **pass/pass/pass**, 9.827/10.148/10.258 s | Sweep 10.284 s. Repetitions 2/3 remain above the original 10.081 s limit: **open timing issue**, not cleared by passing assertions. |

Isolation totals: **45 passed / 15 failed / 0 skipped** across 60 theory cases
in 36 method invocations; all 15 failed cases are the five path fixtures ×3.
The original Fast gate remains **FAIL**. No test was weakened, retagged, skipped,
or removed to alter that result.

Showcase never-parks, alternating serial controls at the requested baseline:
**flag off 5/5, flag on 5/5**, no failures/skips. All ten runs reached 24/30 and
observed real shots/hits. Maximum stopped-with-target interval: **2.510 s off /
2.009 s on**, below the unchanged 5 s limit. Full test durations: off
330.988/187.244/275.487/191.607/273.074 s; on
337.032/320.404/275.245/342.836/196.231 s. These are regression checks, not a rig
performance A/B. No synthetic CPU load or remote game hosts were used.

## P970 sweeps — non-task live-tier failures, 2026-11-08/09 UTC

Two `tools/run-tests.sh --tier fast` sweeps on the P970 tree discovered **13,568** cases each
(13,563 passed / 2 failed / 3 not executed on the final tree; the three not-executed cases are the
optional Meshy-pack skips that always skip). Every failure was qualified alone; none touches join,
password, listing or lobby code, and P970's own suites (6 + 5 + 18 cases) passed in three
consecutive isolated runs.

| Failure / timing signature | Three isolated repeats | Attribution |
| --- | --- | --- |
| `KillBonusLiveTests.Real_end_to_end_projectile_kill_increases_kill_bonus` ("no KILL_SHIP observed by the real Bot within budget") | **pass / pass / pass**, 0.6 s per run | The ledgered combat-timing family (same signature as the P865b entry: load flakes under tier parallelism). |
| `SideEliminationLiveTests.Side_with_two_restart_stations_survives_losing_one` ("first station must be destroyed", 20.6 s — also over the Fast guard) | **pass / pass / pass**, 0.6 s per run | Live-wire elimination timing; same class as the ledgered side-elimination family. |
| `MissileLockLiveTests.MissileLock_BuildsPartialThenFull_AndPosts_Tips_OverTheWire` (38 s, "lock never reached full + partial tip (lock 1.000)") | **fail / fail / fail**, 38 s each, host load 0.66 (idle) | The known open partial-tip receipt failure (P933/P935/P938 cohorts, P961 entry above) — fails in isolated cohorts too. No lock/missile/tip code changed. |
| `BallotVotingLiveTests.Resign_passing_deactivates_the_side_and_ends_the_mission` (resign chat present, filter not matched) | **pass / pass / pass**, 0.6 s each | Ledgered ballot-receipt timing family (same as the P961 sweep). |
| `P777WarpExitWobbleTests.Npc_exit_touch_rate_is_measured` (body **passed**, 10.58 s over the Fast guard) | pass / pass / pass in earlier sweeps | Existing timing-only Fast offender; no tier or limit relaxed, no warp code changed. |

The failure set differs between the two sweeps (load-driven), which is why each is listed with its
own isolated qualification.

## P973 resolution — P959's two regressions: the P733 fallback duty and the P798 fog audit, 2026-11-08 UTC

Both P959 regressions are resolved; details and evidence are in
[reports/P959_REGRESSIONS_P973.md](reports/P959_REGRESSIONS_P973.md).

**1. `P733ResearchTests.No_bomber_tech_finale_reaches_stage_over_and_automatic_next_match_twice`
(Live, ~4 s).** Fixed — real regression of P959 `cbd96ade`, reproduced here **1/1** before the fix
(`Assert.True(Get<bool>(bots[0], "FinaleBomberActive"))`, `P733ResearchTests.cs:103`, with the
purchase log showing the *ordinary* role fallback instead of P733's "research finale" reason).
P959 gated the finale DUTY flag on strike hardware
(`bot.FinaleBomberActive = armed && carriesStrike`, then `BotManager.Finale.cs:1153`), and
`BotManager.TechLoadouts.cs:185-195` keys P733's fallback loadout **and its reason** off that flag,
so a side with no bomber tech and no station-damaging weapon never reached the fallback at all.
The flag is duty again (`BotManager.Finale.cs:1157`); P959's "never count or commit a hull that
cannot hurt the station" rule moved to the strike predicate
`FinaleCarriesStrikeHardware` (`BotManager.ReachableStrike.cs:49`), which the strike mass uses to
build its `armed` set (`BotManager.StrikeMass.cs:285-297`). The arm's refit branch no longer clears
the duty of a member that already holds it (`BotManager.Finale.cs:499-509`). After: the test passes
**10/10 alone**, and the finale/affected unit suites pass **67/67 ×3** (0 skipped).

**2. `P798FullLoopLiveTests.Fogged_showcase_runs_the_whole_loop_unattended_and_starts_the_next_match`
(Live, ~3.5 min).** Fixed at the source of the *build target*: the research commander picked the
constructor's rock from **its own** `ClientWorld.Asteroids`, while the side's knowledge record (the
P797/P798 per-side intel that the fog audit reads) is a 1 Hz sample of the side's worlds. The
showcase research map preparation adds buildable rocks 3500-4200 u out in the home sector
(`Server.Showcase.cs:85-135`), and with the research timescale and this test's fast tick loop a rock
can be discovered and then consumed by the build in ~1-2 wall seconds — shorter than one intel
sample — so the build landed on a rock no recorded knowledge named (`rock 12`, sector 2080).
Reproduced **1/7** with the new diagnostic on master's bot code (`diag3` run 3: `commander(mySide=0)
known=False … lowIds=[0..11,13]`, i.e. not even the commander's own world still has the consumed
rock; 1/21 over all of this task's runs, ~5% — it needs the build to land on a far research rock, which
is why the plain master 10× sweep hit only the *other* failure mode, below).
`ResearchSnapshot(peer, side, recordedRocks)` now filters the commander's rock candidates to that
side's recorded intel (`BotManager.Research.cs:211-241`, caller `:76-111`), so a constructor can only
be ordered to a rock the side's own knowledge holds — the fog rule is unchanged and the audit can no
longer race. The test also attributes every `get_side_intel` row to its mission (one row per
`(Mission.Cookie, side)`, and the manager keeps the finished match's row after the rotation) and
prints the rock's per-mission discovery state plus the commander/bot client views on failure
(`intelRows=[…] clientWorlds=[commander(…) known=… | bot(…) known=…]`).

Also found and fixed on the way: `discovered[side]` was assigned *per intel row*, i.e. "the last row
for this side", so a first-match build could be audited against the next match's fresh knowledge. It
now uses the row of the match the build happened in; `P973FogAuditTests.Intel_is_per_match_so_a_build_is_audited_against_its_own_match`
(Fast, 7 ms) pins that invariant.

| test | master `78c4f4e6` (before) | this tree (after) | verdict |
| --- | --- | --- | --- |
| `P733ResearchTests.No_bomber_tech_finale_…_twice` (Live) | **fail 1/1** (3/3 in the P959 sweep) | **pass 10/10** | Real P959 regression, fixed. |
| `P798FullLoopLiveTests.Fogged_showcase_runs_the_whole_loop_…` (Live) | **8 pass / 2 fail**, both `at least one station must be destroyed`; the knowledge-leak case reproduced **1/7** in the instrumented rerun (`rock 12`, sector 2080) | see the gate in the report | Leak fixed at the build-target source + the audit attribution defect; the no-station-kill mode is a separate pre-existing flake (entry below). |

## P973 fast-tier observation — four Core live cases failed in the suite, all 3/3 alone, 2026-11-08 UTC

One `tools/run-tests.sh --tier fast` (13,540 tests, 564.7 s) on the P973 tree ran while other lanes
carried the shared host and reported **4 `Allegiance.Core.Tests` failures**, every one of them **pass
3/3 alone** on the same binary (and 3 Fast executions over the calibrated 10 s limit, two of them the
same cases):

| test | suite result | isolated |
| --- | --- | --- |
| `NpcWingmanAttackLiveTests.Wingman_with_no_enemy_in_sector_stands_by` | fail 1 s (`wingman with no enemy must not move: travelled 161.517`) | **3/3 pass** |
| `MinerDecisionCycleLiveTests.Idle_npc_repeats_mine_sell_cycle_indefinitely_after_first_sell` | fail 19.1 s (`cycle 1: miner should have docked and sold (sells 0)`), also over the 10 s Fast limit | **3/3 pass** |
| `DirectNpcBuildOrderLiveTests.Commander_directs_builder_drone_to_build_creating_real_station` | fail 3 s (`Assert.Single` empty — no building effect) | **3/3 pass** |
| `AsteroidCombatLiveTests.Cross_side_missile_hits_asteroid_with_exact_computed_damage_and_it_survives` | fail 11.1 s (`cross-side missile must actually hit the asteroid`), also over the limit | **3/3 pass** |

All four are Core server/NPC/flight cases; P973 touches only `tools/allegiance-mcp/**` (bot policy) and
two `Allegiance.ClientCore.Tests` files, and none of them exercises the research commander or the
finale duty. `DirectNpcBuildOrderLiveTests` is already ledgered as a contention flake (see its
historical rows); `NpcWingmanAttackLiveTests`, `MinerDecisionCycleLiveTests` and
`AsteroidCombatLiveTests` have historical sweep-only rows too. **No task test failed** in the suite
(`Allegiance.ClientCore.Tests` 2315/2315, 0 skipped).

## P973 base rate — P798 full loop, no station kill before the match cap (pre-existing), 2026-11-08 UTC

`P798FullLoopLiveTests.Fogged_showcase_runs_the_whole_loop_unattended_and_starts_the_next_match`
requires a station kill inside one 600 s wall window. On a quiet lane (load average 0.9-3.8) the
unmodified master test failed **2/10** with `Assert.True(kill is not null, "at least one station must
be destroyed")` after ~207 s wall (= the 3600 sim-second match cap; the test ticks ~20× real time).
Both logs show the same shape: `[finale] waiting t=…s: side 0 combat=15 relaunched=15 share=1.00
kills=0; side 1 … kills=0` — no ship kill ever happened, so the finale's P801 maturity gate
(`kills>=1` in the last 5 min) never armed, the ordinary 1-2 hull strike waves only scratched the
station shield (0.679 → 1.000 regenerated) and the cap ended the match with 0 station kills.
Four concurrent copies of the same binary (deliberate self-load for the P959 leak repro, load
average 15.8) all passed 4/4, so this is not purely load. Nothing in P959/P961 touches the ordinary
strike/clash policy (`git diff 73eacf4d cbd96ade --stat`), and the finale never armed in the failing
runs. Not fixed here (no task change to ordinary engagement policy); it stays a known base rate for
this Live test — treat one failure per five runs as expected and re-run before recording a regression.

## P962 resolution — stale spawn-docked SHIP_STATUS behind the test's status reset, 2026-11-08 UTC

`DockDockingTests.Ship_nowhere_near_a_station_is_unaffected` is **fixed**, and it was a test race,
not a server bug. Since P653 H-06 a fresh default spawn is DOCKED at the side's base hangar
(`FedSrv/fsmission.cpp:791-796`, `FedSrv/fsship.cpp:437-445`), and the join burst puts that DOCKED
`SHIP_STATUS(90)` on the wire (`Server.Lobby.cs:5451-5457`). The test cleared the bot's status
accumulator *before* sending the launch, so whenever the receive thread had not yet decoded that
frame the decode appended it after the clear: item `[0]`, docked at station 1051, the ledgered
`Expected: -1 / Actual: 1051`. The earlier diagnosis ("the all-status assertion includes another
ship") was wrong — the assertion is already scoped to `shipId` and the failing item is the probe's
own ship. The stale doc comment claiming a station-less first sector is corrected too.

Fresh evidence: the unmodified test failed **6/10** and **12/30** isolated runs; in all 12
diagnostic failures the own docked status was absent at the reset and landed immediately afterwards
as item `[0]` (first decoded 1–2 pumps later, once after the server had already undocked). No
docked status is emitted after the launch: `MakeOwnShipStatus` derives the docked bit from
`StationId` at build time (`Server.Broadcast.cs:1680,1706-1708`), the undock clears it inside the
`BUY_LOADOUT` tick (`Server.ShipCommands.cs:2950-2953`, `Server.Docking.cs:2286-2320`),
`PerformUndock` sends only `SET_CLUSTER` (`Server.Docking.cs:2369-2394`), and native's
`ShipStatusLaunched` overwrites the same per-side latch before the periodic export
(`FedSrv/FedSrv.CPP:6282-6292,13184-13191`, `FedSrv/fsship.cpp:175-184,427-435`,
`FedSrv/fsmission.cpp:4303-4322`). Production code is unchanged.

Resolution: the test launches first and clears only after the **first post-launch flying own
status** — a frame that can only be built once `StationId` is cleared, and connection order proves
every earlier frame (the docked spawn) is already decoded. It still requires ≥3 flying statuses with
`StationID` NA after the launch, every own status flying/NA, and a null server-side `StationId`.
The same barrier is applied to the two siblings with the identical clear-before-launch shape:
`MiningTests.Loopback_default_spawn_is_unaffected_and_statuses_stay_normal` and
`ShipStatusServerTests.PeerReceivesItsOwnShipStatus_InJoinBurst` (the P960 "state-3/state-2 join
receipt intermittency" is this same race; the unmodified test failed 1/20 alone). Each fixed method
passes **20/20 alone** plus **2 rounds of two concurrent processes**; one full
`Allegiance.Core.Tests` run and the Fast gate are in
[reports/DOCK_STATUS_RACE_P962.md](reports/DOCK_STATUS_RACE_P962.md).

Fast gate (`tools/run-tests.sh --tier fast`): **13,518 tests, 533.0 s, FAIL** — three non-task
failures and one Fast execution over the calibrated 10.244 s limit, all of which pass **3/3 alone**
(load flakes on a host carrying load average 6–9 from other lanes):
`MissionRotationLiveTests.Finished_mission_rotates_to_fresh_notstarted_and_new_joiner_starts`
(empty receipt collection), `BotFlightTests.Bot_trajectory_matches_independent_ExecuteMove_rollout`
(tick-78 Δ 7.62), `P942ResearchOrderingTests.Generated_research_never_waits_for_a_drone_or_utility_base_while_a_development_is_buyable`
(10.338 s vs 10.244 s, 10/10 rows pass in each isolated run), and
`DeadLatchRespawnLiveTests.Remote_client_sees_victim_again_after_death_and_respawn` (ClientCore; the
already-ledgered loopback death-latch timing case). The fixed dock/status methods are inside
`Allegiance.Core.Tests` (6152 passed, 2 failed — both the non-task ones above, 0 skipped) and
passed in-suite.

Separately, the required **full `Allegiance.Core.Tests` project run aborted on this box (host
OOM)** after **5557 passed / 0 failed / 0 skipped** in 18 m 27 s: the pre-existing Perf-tier
`P694MassEventTickTests.Launch_point_mass_events` (195 ships, `coincident: true`) grows
`CollisionQueue.Add` until `List.AddWithResize` fails, and a Perf-tier-only diagnostic run
reproduced the same `System.OutOfMemoryException` (stack: `CollisionQueue.cs:18` ←
`CollisionDetector.Detect` ← `Sector.ResolveShipShipCollisions` ← `P694MassEventTickTests.RunWorld:221`;
ten budget/`Assert.Equal(0 vs 7)` misses preceded it under load). It is unrelated to docking/status
and stays open; it was not repeated three times because each repeat kills the shared host process.

## P964 — `ConsoleLogNonBlockingTests.Verbose_gate_silences_and_reenables_routed_diag_lines` **root-caused and fixed**, 2026-11-08

**The old label "process-global gate under load" was wrong.** The `LiveNetwork`
collection is `[CollectionDefinition("LiveNetwork", DisableParallelization = true)]`
(`tests/Allegiance.Core.Tests/LiveCombatWireTests.cs:28`), so xunit runs it
**exclusively**: no other test in the process can write to `ConsoleLog`, capture
`Console.Out` or flip the gate while this test runs (measured: 0 samples of
`Verbose`/`IsInstalled` in 7.4 M samples from a parallel noise collection, while a
control pair of ordinary collections did overlap).

**Real cause — `ConsoleLog.Flush()` was a dequeue barrier, not a delivery barrier.**
The drain thread dequeues a line and then performs the I/O; `Flush` waited on
`_queue.Count == 0`, which happens at the **dequeue**, so it returned with the last
line still in flight and the test then read an empty sink — while the preceding
`Assert.Equal(1, EmittedWrites - emBefore2)` had already passed. Reproduced
**4/70 isolated invocations** (process restart per run, including 1/20 while a second
Core test process ran) with `Assert.Contains() Failure: Sub-string not found /
String: "" / Not found: "[p433-diag]"`; that also matches the sweep, nightly and
v0.1.5 release-gate sightings, where preemption widens the dequeue→write window.

**Resolution (fixed, this task).** `ConsoleLog` now gives the sink a *generation*
(queue + `Enqueued`/`Written` counters), `Flush` waits until the drain has **written**
everything enqueued, a superseded drain stops instead of replaying a stale backlog
into the next test's sink, and a new per-thread counting scope
(`ConsoleLog.OpenProbeForTest`) lets a test attribute `Write` attempts/emissions to
its own lines. The test asserts on a per-run marker in the capture sink plus that
scope (all counts exact, no `≥`); the same hazard in
`P655GameStateIntegrityTests.H31_Verbose_turret_flood_has_bounded_logs_and_still_applies_valid_input`
(the only other global `TotalWrites` delta in `tests/`) is fixed the same way. No
production path changes. Evidence: the two fixed tests **50/50 alone**, three whole
`Allegiance.Core.Tests` assembly runs with parallel Core processes **0 failures**, and
the solution CI-filter run green — see
[reports/VERBOSE_GATE_FLAKE_P964.md](reports/VERBOSE_GATE_FLAKE_P964.md). Older rows
below that list this method as a load flake (2026-10-28 P860, 2026-10-29 P871b,
2026-11-08 P959) are superseded by this entry: they were the same dequeue race.

**P964 gate observations (non-task failures).** The three whole-assembly rounds
(each with two more Core test processes running in parallel, load 4-11) failed **8
methods once each, none of them the fixed test**. Every one is already ledgered above
and none is touched by this change (`src/Allegiance.Core/ConsoleLog.cs` test seams +
two test files); each ran three times alone:

| Method | Round | Message | 3x alone |
| --- | --- | --- | --- |
| `P739TimerLiveTests.Warp_bomb_notice_matches_captured_blast_deadline_and_stays_in_mission(delay: 7)` | 1 | 7.0000000000000009 > 7 | 3/3 pass (also the one failure of the pre-fix full run) |
| `P590PodRescueTests.Pod_pickup_order_arms_NPC_diverter_and_rescues` | 1 | "NPC diverter did not reach and rescue the pod" | 3/3 pass |
| `LiveSideSelectTests.Flag_on_auto_seats_a_silent_peer_at_the_configured_30_seconds` | 1 | 31.65 outside (28,31) | 3/3 pass |
| `BallotVotingLiveTests.Mutiny_passing_transfers_leadership_failing_keeps_it` | 1 | empty receipt collection | 3/3 pass |
| `AsteroidMinedLiveTests.No_broadcast_fires_once_miner_hits_capacity` | 1 | one AsteroidMined arrived | 3/3 pass |
| `LiveP388DebugNoGameOverTests.Debug_mode_disconnect_does_not_end_mission_and_miners_keep_cycling` | 2 | 0 cycles in 240,000 ticks | 3/3 pass |
| `DockDockingTests.Ship_nowhere_near_a_station_is_unaffected` | 2 | unscoped status history: ship 8098 docked at 1051 | fail/pass/pass (the existing unscoped-assertion entry) |
| `NpcWingmanDefendLiveTests.Defender_disengages_when_chased_enemy_flees_and_returns_to_charge` | 3 | d0=190.3 -> d1=221.7 | 3/3 pass |

The solution-wide CI-filter run (same gate the v0.1.5 release used) added one more:
`Allegiance.ClientCore.MonoGame.Tests.P695HumanShipUpdatesLiveTests.Human_flies_for_ten_minutes_with_turns_boost_tidi_and_recovers_from_correction`
(2 m 43 s) threw `NullReferenceException` at `Game1.EngageAutopilotToward`
(`src/Allegiance.ClientCore.Game/Game1.Eject.cs:165`) from `HandleOwnShipEject`. **New
observation, 3/3 pass alone** (10 m 5 s each under the gate's own load) — a load-only
observation of the eject/autopilot path, which P964 does not touch; it is entered here
rather than fixed by this task.

## P967 validation — transport/server lifecycle cleanup, one Fast sweep, 2026-11-08 UTC

P963-04/07/08/09 are repaired (transport greeting ownership at shutdown, chat-budget
retirement on every release path, a bounded receive-drained ended-peer window, and a
bounded per-mission bucket-completion history). One Fast sweep: **13,528 tests,
538.7 s** → 13,522 passed / 3 failed / 3 not executed; 9/9 infrastructure checks
passed and **no timing offenders**. All three failures are previously ledgered flake
families and each passed three isolated invocations:

| Method | Sweep observation | Alone, 3× |
| --- | --- | --- |
| `P590PodRescueTests.Proactive_assignment_diverts_nearest_idle_diverter_and_rescues` | "proactively-assigned diverter did not rescue the pod", 7 s | 3/3 pass (0.86/0.92/0.91 s) — existing rescue-tick-budget family |
| `MiningStateWireTests.Loopback_own_mining_state_reaches_client_and_clears_on_departure` | client queue held `IsMining=True, AsteroidId=0` when the assert ran, 837 ms | 3/3 pass (0.54/0.56/0.54 s) — first ledger entry for this method: bounded 400-tick observation window, mining/placement path unchanged |
| `LiveSideSelectTests.Flag_on_auto_seats_a_silent_peer_at_the_configured_30_seconds` | 32.45 simulated s against the fixture's [28, 31] window | 3/3 pass (1 s each) — existing receipt/simulation-clock boundary |

The P967 work changes no mining, seating, rescue or tick path, and the three isolated
rechecks reproduce the already-recorded signatures. A discarded earlier sweep of the
same day (13,526 tests) is **not** part of the gate: it ran against an intermediate
P967 design (an `IsPeerClosed` derived from live-peer membership) that made the
in-process dispatch harnesses drop injected datagrams for synthetic peers (148
Core.Tests + 1 ClientCore.Tests failures); the final design keeps the ended-peer
predicate and bounds it instead, and `P869DispatchTests` / `P867DispatchCertificationTests`
/ `P653EconomyLoadoutIntegrityTests` pass 99/99.

## P953 validation — resource opening and defensive-field fixture, 2026-11-08 UTC


P798's null-clash and split-frontier mechanisms are repaired in the existing
showcase opening. SC4 now issues ordinary Build orders to its normally produced
mine layers and requires actual long-lived home fields. Original resource,
visibility and per-side deployment coverage remains. P798 passes **3/3 alone on
each map**, SC4 **3/3 alone**; ten new policy cases **10/0/0 ×3** and 43 affected
cases **43/0/0 ×3**, with no skips. All final Live receipts show server/concurrent
GC and the required performance baseline. Historical Live-bracket limitations,
independent causal replay and native citations are in the report.

One Fast sweep: **13,475 Passed / 3 Failed / 3 Skipped**,
944.1 s; 4 executions over the calibrated
10.000 s limit. Infrastructure:
9 checks, exit 0.
The gate is **FAIL** and was not rerun or waived.

Every failed or over-time method ran three isolated invocations (6
methods; 18 invocations): **25/2/0** case executions across 27 parameter rows.
Original timing and assertion failures remain visible in the JSON;
passing isolated repeats do not erase the original failed sweep.

The full-session chat failure repeats **fail/pass/fail** with the previously
ledgered `ChatTarget` receipt race (`-2` expected, `0` received); it remains
intermittent. The side-seat failure is the existing simulated-clock/receipt
boundary (a ship already exists when the fixture expects none); all three
isolated rechecks pass. The timer failure is the existing delay-7 floating upper
bound (`7.0000000000000009 > 7`); every delay row passes in all three rechecks.
No chat, seating or timer implementation/fixture is changed. All three slow
methods pass three isolated rechecks below this sweep's 10-second limit.

| Method | Sweep trigger | P/F/S in three isolated runs | Max case seconds, each run |
| --- | --- | --- | --- |
| `Allegiance.ClientCore.Tests.FullSessionIntegrationTests.FullSession_Join_Ready_Loadout_Launch_Movement_Chat_Compose` | failed | 0/1/0, 1/0/0, 0/1/0 | 1.064/1.874/1.076 |
| `Allegiance.ClientCore.Tests.P814GunnerTests.P814b_TurretCarrier_pref_keeps_or_beats_the_Best_hulls_turret_seats` | slow | 1/0/0, 1/0/0, 1/0/0 | 3.625/3.657/3.712 |
| `Allegiance.ClientCore.Tests.SC2NaniteSupportTests.Support_selection_obeys_research_dock_budget_and_mounts` | slow | 2/0/0, 2/0/0, 2/0/0 | 4.542/4.482/4.680 |
| `Allegiance.Core.Tests.LiveSideSelectTests.Flag_on_auto_seats_a_silent_peer_at_the_configured_30_seconds` | failed | 1/0/0, 1/0/0, 1/0/0 | 1.051/0.994/1.026 |
| `Allegiance.Core.Tests.P739TimerLiveTests.Warp_bomb_notice_matches_captured_blast_deadline_and_stays_in_mission` | failed | 3/0/0, 3/0/0, 3/0/0 | 0.479/0.500/0.520 |
| `Allegiance.Core.Tests.P777WarpExitWobbleTests.Npc_exit_touch_rate_is_measured` | slow | 1/0/0, 1/0/0, 1/0/0 | 9.847/9.846/9.791 |

Parking comparison: **base 5/0/0, fixed 4/1/0**; maximum target idle
2.508/1.914 seconds respectively. The fixed failure is the arrival assertion:
20/30 reached at 480.1 seconds, longest target idle 1.914 seconds. Its three
required isolated rechecks pass **1/0/0 ×3**, with observation durations
233.2/261.1/463.9 seconds and longest idles 2.311/2.306/1.815 seconds.
All five runs per tree use the same baseline and actual server GC; starting load,
arrival counts, durations and individual idle measurements are retained.
Parking-limit violations are **0/5 on both trees**. Overall assertion outcomes
differ because of the arrival failure; equal arrival performance is not claimed.
The original arrival failure remains recorded, as does P952's earlier occurrence.

The Fast invocation honors explicit legacy-path fixture configurations instead
of overriding them globally. Scratch temporary paths use a `/proc` descriptor
alias into workspace storage, avoiding ancestor `local.env` discovery; the writable
profile stays in workspace scratch. No production configuration is altered by
these invocation controls. Optional-pack skip details and all non-task outcomes
are recorded in the report and compact JSON.

[P953 report](reports/SHOWCASE_REGRESSIONS_P953.md) ·
[Validation JSON](reports/P953_assets/validation.json).
Raw evidence remains in `.scratch/p953/`; no commits or pushes.

## P958 resolution — seeded pod load and complete native controls, 2026-11-08 UTC

The P699b entries below (P708/P866/P912 and recurrences) are resolved as a
**nondeterministic fixture with incomplete native steering**, not a wall-clock
load flake. A shared optional simulation RNG is installed before assembly,
including previously unseeded file-map asteroid top-up, launch jitter and warp
exits. Default production randomness remains unseeded.

The original 30-seed-per-case sweep reproduced **14/30 ordinary failures** and
**2/30 rescue-traffic failures**. Worst seed **23** lost **50 / 36** pods. Its
large loss was repeated ramming of generated rock 3610 during the 1062 leg, not
bay-slot occupancy. Separately, traced launch traffic reversed a homed pod into
the arrival aleph; the harness kept selecting the station. It had ignored native
Dodge/diversion and combined partial docking controls with MCP pursuit.

The corrected harness uses production `NativeGotoPlan.Execute` and native
Ignore-filtered obstacles for both station and warp targets. Seed **23**, the
worst old failure in both cases, now completes **200 docked / 159 docked + 41
rescued**, with **zero expiries**. It is pinned permanently. **Every existing
assertion is retained**, including 300 s allowance, zero expiries, the radius/bay
checks, all outcomes and sim-stage failures. The 100-pods/one-bay/one-tick proof
is unchanged. Broader native crowding can still consume the finite allowance;
this fixed-seed regression does not claim an all-seed completion guarantee.

Serial qualification: affected RNG/physics/navigation cohort **220/0/0 three
times**, **660/0/0 total** (Passed/Failed/Skipped); strict pinned P699b **9/0/0**
over three invocations, with identical per-pod telemetry; complete same/different
seed determinism **6/0/0** over three invocations. The five alternating normal-home showcase pairs pass **base 5/0/0, current
5/0/0**. The bounded two-process pod proof passes **4/0/0**, with every complete per-pod
telemetry record identical to the serial reference (load 9.23–9.60). The single Fast gate remains **FAIL: 13,485 passed / 6 failed / 3 skipped**, with
**20 timing violations**. All 9 infrastructure checks pass. The skips are the
existing optional private Meshy-pack cases; no P958/artwork proof was skipped.

All failed/timing-offender families ran three times alone (**19 families / 57
invocations**). The five bundle-path executions remain reproducible ancestor
`local.env` / in-repo `TMPDIR` fixture failures. Missile lock is **pass/fail/pass**,
with full lock 1.000 but missing partial-tip receipt on the failed run; it remains
open. Twelve timing families recover below 10 s. Warp-exit sampling still exceeds
budget in 2/3 isolated runs (10.288/10.052/9.789 s), and treasure-offset corpus
decoding in 3/3 (11.997/10.695/10.643 s); both pass assertions and remain open
as timing failures. No unrelated
source, assertion, tier or budget changed. The isolated totals and evidence do
not turn the Fast sweep green.

| Non-task method / theory family | Three isolated invocations | P/F/S | Max case (s) | Disposition |
| --- | --- | --- | --- | --- |
| `P800HullProofGpuTests.Every_hull_of_the_real_core_renders_in_hangar_flight_and_target_pane` | pass / pass / pass | 3/0/0 | 5.587 | Assertions and timing pass in all isolated repeats |
| `MissileLockLiveTests.MissileLock_BuildsPartialThenFull_AndPosts_Tips_OverTheWire` | pass / fail / pass | 2/1/0 | 38.779 | Known missing partial-tip receipt; remains intermittent |
| `P704TechLoadoutTests.Dock_local_masks_are_never_combined_and_finale_uses_the_same_bomber_selection` | pass / pass / pass | 3/0/0 | 4.228 | Assertions and timing pass in all isolated repeats |
| `P796VariedLoadoutTests.Varied_equips_every_legal_hull_and_part_of_the_role_over_many_purchases` | pass / pass / pass | 9/0/0 | 7.897 | Assertions and timing pass in all isolated repeats |
| `P814GunnerTests.P814b_TurretCarrier_pref_keeps_or_beats_the_Best_hulls_turret_seats` | pass / pass / pass | 3/0/0 | 3.663 | Assertions and timing pass in all isolated repeats |
| `P832bProsperityTests.No_goal_decision_trace_matches_dispatch_base` | pass / pass / pass | 3/0/0 | 2.055 | Assertions and timing pass in all isolated repeats |
| `P832bProsperityTests.Observed_He3_grows_miners_first_and_losses_trigger_replacement` | pass / pass / pass | 3/0/0 | 4.298 | Assertions and timing pass in all isolated repeats |
| `P832bProsperityTests.Other_goals_keep_the_same_commander_trace_as_absent_goals` | pass / pass / pass | 3/0/0 | 8.437 | Assertions and timing pass in all isolated repeats |
| `P877MineDamageLiveTests.Chased_bot_wake_mine_damages_pursuer` | pass / pass / pass | 3/0/0 | 1.800 | Assertions and timing pass in all isolated repeats |
| `P942ResearchCommanderTests.Generated_commander_funds_buyable_research_before_optional_production` | pass / pass / pass | 9/0/0 | 3.226 | Assertions and timing pass in all isolated repeats |
| `SC2NaniteSupportTests.Support_selection_obeys_research_dock_budget_and_mounts` | pass / pass / pass | 6/0/0 | 4.633 | Assertions and timing pass in all isolated repeats |
| `SC3CloakPolicyTests.Research_loadouts_offer_real_legal_cloaks_without_unlock_bypass` | pass / pass / pass | 3/0/0 | 1.497 | Assertions and timing pass in all isolated repeats |
| `P777WarpExitWobbleTests.Npc_exit_touch_rate_is_measured` | pass / pass / pass | 3/0/0 | 10.288 | Assertions pass 3/3; 2 isolated case executions still exceed 10 s |
| `P817NativeMinerDecisionCycleLiveTests.Idle_npc_repeats_mine_sell_cycle_indefinitely_after_first_sell` | pass / pass / pass | 3/0/0 | 7.088 | Assertions and timing pass in all isolated repeats |
| `PayloadDecodeTests.TreasureSet_offsets_cross_reference_same_core` | pass / pass / pass | 3/0/0 | 11.997 | Assertions pass 3/3; 3 isolated case executions still exceed 10 s |
| `PlayerBundlePathsTests.LocalConfigWinsOverBundleWithBlankEnvironment` | fail / fail / fail | 0/3/0 | 0.015 | Known ancestor-local.env fixture obstruction |
| `PlayerBundlePathsTests.MissingDirectoryHasNoDefault` | fail / fail / fail | 3/6/0 | 0.005 | Known ancestor-local.env fixture obstruction |
| `PlayerBundlePathsTests.NativeSourceNeverGuessesAnInstallSubdirectory` | fail / fail / fail | 0/3/0 | 0.005 | Known ancestor-local.env fixture obstruction |
| `PlayerBundlePathsTests.UsesExecutableDirectoryOutsideRepository` | fail / fail / fail | 3/3/0 | 0.005 | Known ancestor-local.env fixture obstruction |

Full counts and compact isolation evidence are in the linked P958 report and
`reports/P958_assets/non-task-isolation-summary.json`.

Evidence, native citations, 120 seeded runs and traces:
[report](reports/POD_LOAD_P958.md), [legacy sweep](reports/P958_assets/legacy-sweep.csv),
[native sweep](reports/P958_assets/native-sweep.csv). No commits or pushes.

## P952 validation — defender fixture correction, 2026-11-07 UTC

The persistent defender null-acquisition failure is **resolved by correcting
its fixture**: bisect identifies P872 `0d714031` (master merge `e13e22b5`),
whose damaging-primary guard matches native `Igc/shipIGC.cpp:2680`.
`SpawnNpcShip` provides a bare hull; the test now mounts a compatible damaging
primary before expecting acquisition. Production behavior/defaults are unchanged.
The fixed defender passes 3/3 alone on the lane baseline and passes in-tier;
P865b 8/8, P869b 47/47 and P872 11/11 all pass ×3 with zero skips.

One Fast tier: **13,383 Passed / 25 Failed / 3 Skipped**, 13,411 cases,
1136.3 s; all nine infrastructure checks passed. The three skips are optional
Meshy-pack checks. Eighteen executions exceeded the calibrated 11.138 s limit.
The gate remains **FAIL**; it was not rerun or waived.

The globally pinned baseline overrides used for this invocation conflicted
with twelve unchanged tests that deliberately select legacy ship updates,
snapshot-off, serial builds or thread-per-peer transport. All twelve reproduce
under those overrides and pass three controls honoring their explicit fixture
configuration (39/0/0 across 36 control invocations). These are invocation
environment artifacts, not new production defects. No test/source defaults
were changed for the controls.

Every failed or over-time method ran three isolated invocations (35 methods;
105 invocations, 80/52/0 case executions under the pinned environment). The
52 failures include repeated intentional configuration conflicts and the
already-ledgered bundle-path obstruction, not 52 distinct defects:

- The five `PlayerBundlePathsTests` parameter cases across four methods still
  fail because mandatory scratch TMPDIR is inside the repository and ancestor
  `local.env` is discovered. Passing parameter cases are retained in the counts.
- `MissileLockLiveTests.MissileLock_BuildsPartialThenFull_AndPosts_Tips_OverTheWire`
  is **pass/pass/fail** alone (failure: full lock reached, partial-tip condition
  never satisfied); remains intermittent, not cleared as a load-only flake.
- The six other assertion-failure methods pass three isolated runs. The profile
  method also passes ×3 with writable scratch `XDG_CONFIG_HOME`; its sweep
  failed restoring the read-only real profile, as already ledgered.
- `P777WarpExitWobbleTests.Npc_exit_touch_rate_is_measured` passes assertions
  ×3 but takes 11.206/10.945/11.383 s; two runs remain over this sweep’s limit.
- The unchanged defender closer-threat switch failed once in an early
  non-pinned control, then passed three isolated rechecks, all three pinned
  runs and the Fast tier. Record the intermittent retarget observation; only
  the acquisition fixture is corrected by P952.

Counts below are **Passed/Failed/Skipped** per isolated invocation; theory
filters retain every parameter row. Explicit-config controls remove conflicting
process overrides and let the fixture’s own deliberate configuration apply.

| Method | P/F/S in three baseline invocations | Max case seconds, each run | Explicit-config controls |
| --- | --- | --- | --- |
| `Hud12bReviewFixTests.HUD_formatted_readouts_allocate_nothing_in_steady_state` | 1/0/0 ×3 | 0.009/0.009/0.009 | — |
| `P761StartChatComposeTests.ProfileEditor_OwnsEnterAndBackspace_AndKeepsChatClosed` | 1/0/0 ×3 | 0.058/0.071/0.081 | — |
| `SC3CloakPolicyTests.Research_loadouts_offer_real_legal_cloaks_without_unlock_bypass` | 1/0/0 ×3 | 1.648/1.582/1.592 | — |
| `MissileLockLiveTests.MissileLock_BuildsPartialThenFull_AndPosts_Tips_OverTheWire` | 1/0/0, 1/0/0, 0/1/0 | 1.722/1.820/38.853 | — |
| `P796VariedLoadoutTests.Varied_equips_every_legal_hull_and_part_of_the_role_over_many_purchases` | 3/0/0 ×3 | 6.598/6.748/9.121 | — |
| `P942ResearchCommanderTests.Generated_commander_funds_buyable_research_before_optional_production` | 3/0/0 ×3 | 3.705/4.942/4.676 | — |
| `P832bProsperityTests.No_goal_decision_trace_matches_dispatch_base` | 1/0/0 ×3 | 2.292/2.662/2.505 | — |
| `P877MineDamageLiveTests.Chased_bot_wake_mine_damages_pursuer` | 1/0/0 ×3 | 1.851/1.812/1.873 | — |
| `P814GunnerTests.P814b_TurretCarrier_pref_keeps_or_beats_the_Best_hulls_turret_seats` | 1/0/0 ×3 | 4.178/4.113/4.154 | — |
| `P832bProsperityTests.Other_goals_keep_the_same_commander_trace_as_absent_goals` | 1/0/0 ×3 | 8.033/6.985/8.127 | — |
| `P889PoolSizingTests.Explicit_config_still_wins_over_the_resolver_at_boot` | 0/1/0 ×3 | 0.171/0.203/0.158 | 1/0/0 ×3 |
| `SC2NaniteSupportTests.Support_selection_obeys_research_dock_budget_and_mounts` | 2/0/0 ×3 | 5.012/5.232/5.113 | — |
| `SpectatorFrameLiveTests.Translates_ARealWorldAndARealKillShip` | 1/0/0 ×3 | 2.064/1.976/2.057 | — |
| `P704TechLoadoutTests.Dock_local_masks_are_never_combined_and_finale_uses_the_same_bomber_selection` | 1/0/0 ×3 | 4.606/5.285/4.841 | — |
| `P832bProsperityTests.Observed_He3_grows_miners_first_and_losses_trigger_replacement` | 1/0/0 ×3 | 3.718/4.748/3.710 | — |
| `OwnShipCoalesceKeyTests.Owners_own_ship_update_has_a_distinct_guaranteed_coalesce_key_parallel_builder` | 0/1/0 ×3 | 0.192/0.272/0.204 | 1/0/0 ×3 |
| `PlayerBundlePathsTests.LocalConfigWinsOverBundleWithBlankEnvironment` | 0/1/0 ×3 | 0.015/0.015/0.015 | — |
| `P780PodRescueProbeTests.Probe_rescue_pass_allocates_nothing` | 1/0/0 ×3 | 0.158/0.088/0.096 | — |
| `ShipUpdateBudgetLiveTests.Locked_target_is_always_included_regardless_of_its_deviation` | 0/1/0 ×3 | 0.385/0.426/0.404 | 1/0/0 ×3 |
| `P864dTeamLobbyClientListLiveTests.A_lobby_player_requests_a_side_and_the_join_side_relay_moves_it_out_of_the_pool` | 1/0/0 ×3 | 0.402/0.360/0.393 | — |
| `PlayerBundlePathsTests.MissingDirectoryHasNoDefault` | 1/2/0 ×3 | 0.005/0.005/0.004 | — |
| `TeammatePositionBroadcastTests.Teammates_commanded_movement_is_broadcast_to_observers_but_enemy_movement_is_not` | 0/1/0 ×3 | 9.344/9.135/9.148 | 1/0/0 ×3 |
| `LiveTurretGunnerBindingTests.Board_binds_gunner_and_active_turret_update_roundtrips_live` | 0/1/0 ×3 | 3.182/3.297/2.835 | 1/0/0 ×3 |
| `SimPassExceptionIsolationTests.SimPass_contains_a_throwing_export_stage_and_keeps_running` | 1/1/0 ×3 | 0.691/0.835/0.619 | 2/0/0 ×3 |
| `ShipStatusServerTests.PeerReceivesItsOwnShipStatus_InJoinBurst` | 1/0/0 ×3 | 0.413/0.401/0.505 | — |
| `PlayerBundlePathsTests.UsesExecutableDirectoryOutsideRepository` | 1/1/0 ×3 | 0.004/0.005/0.005 | — |
| `P761bCommandRelayTests.QueuedStationGoto_KeepsOwnTarget_AndRelaysTheStationToTheTeammate` | 1/0/0 ×3 | 0.575/0.714/0.705 | — |
| `Transport.TcpTransportSendWorkerPoolTests.Explicit_thread_per_peer_config_boot_stays_thread_per_peer` | 0/1/0 ×3 | 0.188/0.184/0.175 | 1/0/0 ×3 |
| `Transport.TcpTransportSendWorkerPoolTests.Boot_line_prints_the_active_transport_mode` | 0/1/0 ×3 | 0.199/0.207/0.236 | 1/0/0 ×3 |
| `PlayerBundlePathsTests.NativeSourceNeverGuessesAnInstallSubdirectory` | 0/1/0 ×3 | 0.005/0.004/0.022 | — |
| `ShipUpdateBudgetLiveTests.Heavy_updates_are_bounded_to_the_budget_not_proportional_to_ship_count` | 0/1/0 ×3 | 0.357/0.349/0.375 | 1/0/0 ×3 |
| `TeammatePositionBroadcastTests.Stationary_teammate_is_still_broadcast_to_observer_once` | 0/1/0 ×3 | 15.698/16.199/16.067 | 1/0/0 ×3 |
| `OwnShipCoalesceKeyTests.Owners_own_ship_update_has_a_distinct_guaranteed_coalesce_key_serial_builder` | 0/1/0 ×3 | 0.244/0.184/0.212 | 1/0/0 ×3 |
| `P777WarpExitWobbleTests.Npc_exit_touch_rate_is_measured` | 1/0/0 ×3 | 11.206/10.945/11.383 | — |
| `ShipUpdateBudgetLiveTests.Ship_dropped_one_tick_is_served_the_next_no_permanent_starvation` | 0/1/0 ×3 | 0.346/0.462/0.421 | 1/0/0 ×3 |

Showcase never-parks: **base 5/0/0, changed tree 4/1/0** (Passed/Failed/Skipped), five isolated runs each. Parking-limit failures: base 0/5, changed tree 0/5. The comparison was serial and alternated base/fixed using identical settings.
The changed-tree failure was the 480 s arrival assertion (21/30 reached), with
longest target idle 1.905 s; no production/P700 source differs. See the report
and JSON for individual outcomes, starting load and idle measurements.

Raw evidence stays in `.scratch/p952/`; compact public counts and measurements:
[P952 validation JSON](reports/P952_assets/validation.json). Bisect/native
analysis and P798 parent/culprit passes:
[P952 report](reports/NPC_DEFEND_BISECT_P952.md). No commits or pushes.
## P948 validation — two Fast sweeps, 2026-11-07 UTC

Two Fast sweeps of the persisted-mouse-options change (invert-Y + native 0.6 sensitivity in
`player.json`; no Core/sim/NPC/discovery/flight-model change).

* Sweep 1 (before the two test-harness fixes below): **13,440 passed / 6 failed / 3 NotExecuted**,
  13,449 executions in 598.0 s; infrastructure checks passed; **7 executions over the calibrated
  10 s limit** (P744 factor 1.072 — the P947 sweep measured 7 then 4 on the same base).
* Sweep 2 (intermediate): **13,444 passed / 2 failed / 3 NotExecuted**, 13,449 executions in 624.5 s;
  infrastructure checks passed; **4 executions over the calibrated 10 s limit** (P744 factor 0.980).
* Sweep 3 (**final source**, after the notice-tick refactor): **13,444 passed / 2 failed / 3 NotExecuted**,
  13,449 executions in 576.4 s; infrastructure checks passed; **4 executions over the calibrated 10 s limit**.
  The 3 NotExecuted are the optional Meshy-pack skips (`ALLEGIANCE_MESHY_PACK_PATH` unset; the
  worktree's `local.env` supplies artwork/native-source paths only).

| Method | Sweep observation | Isolation (P948) |
| --- | --- | --- |
| `P931TrainingPilotTests.Actual_training_hulls_track_a_nearby_crossing_contact(mission: 4, sample: 0.5)` | Sweep 1 only: tracking error above the 1.5° bound | **Ours, fixed**: the test divided the wanted stick value by `mouse.Sensitivity` alone, which was the effective scale only while the port's knob defaulted to 1.0; P948's native 0.6 made it under-command. Now divides by `mouse.PixelScale` (`Sensitivity × MouseSensitivity`), exactly like the production training pilot. **pass / pass / pass** after the fix. |
| `P740FlightInputTests.Rebound_preset_runs_production_handler_persists_and_suppresses_held_composed_and_primed_edges` (Throttle33/66) | Sweeps 1: `NullReferenceException` | **Ours, fixed**: the test builds `Game1` with `RuntimeHelpers.GetUninitializedObject` and calls `PrimeInFlightKeyLatches`, which now primes the sensitivity key-repeat state; the repeat/notice fields are created lazily (`??=`) so the headless harness cannot hit a null. **pass / pass / pass** after the fix. |
| `DeadLatchRespawnLiveTests.Remote_client_sees_victim_again_after_death_and_respawn` | Sweep 1 only, "B never saw A's death" | **pass / pass / pass** alone (~5 s each); load-only |
| `DiscoveryTests.Coordinator_is_silent_when_tailscale_absent` | Sweep 1 only (passed in sweep 2): the live tailnet advertised a LAN game | Environment-dependent, already led; no discovery code changed |
| `NpcWingmanDefendLiveTests.Defender_disengages_when_chased_enemy_flees_and_returns_to_charge` | Both sweeps, expected-ship/actual-null | **fail / fail / fail** alone; the already-led persistent base failure |
| `P739TimerLiveTests.Warp_bomb_notice_matches_captured_blast_deadline_and_stays_in_mission(delay: 7)` | Sweep 2 only: `7.0000000000000009` against an inclusive 7 s upper bound | Already led floating-point upper-bound signature (P947 recorded the same row); no timer/bomb code changed |
| `FullSessionIntegrationTests.FullSession_Join_Ready_Loadout_Launch_Movement_Chat_Compose` | Sweep 3 only: `Assert.Equal` expected -2, actual 0 (the chat-receipt ordering at line 229) | **fail / pass / pass / fail / pass / pass** across six isolated runs — the ledgered intermittent signature (led as pass/fail/pass); no chat/session code changed |
| Slow Fast executions (>10 s) | Sweep 2: P800 hull proof 13.08 s, P814 gunner 10.33 s, P777 warp-exit 11.42 s, P750 asteroid 11.93 s — all passed | Led timing-only families, also over the limit on the P947 base sweep |

Showcase never-parks (`P700ShowcaseLiveTests`, both cases per run): the required five sequential runs
were **4 fully green + 1 run with a single case failing** (the failing run's assertion line was not
captured — its two immediate follow-up runs were **pass / pass**, both cases, 7 m 48 s and 11 m 32 s).
Across every run whose output was inspected, each case reported `longest speed<1 with target` well
inside the 5 s bound (no `Parked >5s`); the one failure is the already-led load-sensitive
arrival/never-park intermittency (base rate previously led as 4/5 and 5/5). No parking-bound failure.

## P947 validation — two Fast sweeps, 2026-11-07 UTC

Two Fast sweeps (the second after a final Resolve edge-case fix). Sweep 1:
**13,341 executions, 6 failed / 13,335 passed / 3 NotExecuted**, 613.3 s; sweep
2: **13,342 executions, 4 failed / 13,335 passed / 3 NotExecuted** (3 optional
Meshy-pack skips), 629.9 s; both infrastructure-clean, 7 then 4 executions over
the calibrated 10-second limit (P744 factor 1.038). None of the failures touch
P947's scope (client lobby-URL precedence only; no Core, sim, NPC, discovery or
flight code changed).

| Method | Sweep observation | Isolation |
| --- | --- | --- |
| `MissileTrackWarningLiveTests.EnemyMissileHomingOnLocalShip_ArmsWarning_ThenClearsOnDestruction_OverTheWire` | Sweep 1 only, after 26 s (also the sweep's slowest execution) | **pass / pass / pass** alone (~1–2 s each); load-only |
| `P813cReconnectGraceTests.Voluntary_logoff_is_immediate_and_reports_quit` | Sweep 1 only, under parallel load | **pass / pass / pass** alone; load-only |
| `P739TimerLiveTests.Warp_bomb_notice_matches_captured_blast_deadline_and_stays_in_mission` (delay 7) | Sweep 1 only, floating upper-bound signature, as led | **pass / pass / pass** (3 theory rows each); load-only |
| `LiveLifepodEnduranceDisplayTests.Driver_sees_countdown_others_do_not_and_dock_cancels` | Sweep 1 only, under parallel load | **pass / pass / pass** alone; load-only |
| `P660NextMatchLiveTests.Late_game_over_rebuild_ready_countdown_started_has_no_clock_jump` (enabled: False) | Sweep 2 only, under parallel load | **pass ×3** alone (all 4 theory rows each); load-only |
| `NaniteThreatBonusLiveTests.Threat_target_earns_2x_repair_credit_over_wire` | Sweep 2 only, under parallel load | **pass / pass / pass** alone (~3 s each); load-only |
| `NpcWingmanDefendLiveTests.Defender_disengages_when_chased_enemy_flees_and_returns_to_charge` | Both sweeps, existing null-target assertion | **fail / fail / fail** alone, exactly as already led (fails on the unmodified base too) |
| `DiscoveryTests.Coordinator_is_silent_when_tailscale_absent` | Both sweeps; **fail / fail / fail** alone, 39–83 ms: the test expects an empty discovery set but this workstation runs a live Tailscale coordinator advertising a server (<tailnet address>:6073, "LAN game"). Environment-dependent, not load — it can only pass on a host without an active tailnet advertising games. P947 changes no discovery code. | Environment-dependent; needs a skip guard when a tailnet coordinator is actually reachable |
| `MissionRotationLiveTests.Finished_mission_rotates_to_fresh_notstarted_and_new_joiner_starts` | Sweep 2, `Assert.Contains` empty collection (`MissionRotationLiveTests.cs:181`) | **genuine intermittent, not load**: 3 failures across 26 isolated single-test runs (~12%), two more 10-run chains fully green; unrelated to P947 (Core mission rotation; no Core source changed). New intermittent — track |

Quarantine tier (separate from the gate): sweep 2 flagged `AddPartTests.Part_pickup_decline_recreates_treasure_at_current_position` as a quarantine flake; its in-tier retry passed.


## P944 validation — Join links, 2026-11-07 UTC

Exactly one Fast sweep: **13,370 passed / 9 failed / 3 optional Meshy-pack
skips**, 13,382 executions in **597.2 s**; all nine infrastructure checks passed.
Eight executions exceeded the calibrated ten-second Fast limit. All P944 tests
passed. The sweep remains **FAIL**; no second sweep or relaxed checks were used.
Final source build was 0 warnings/errors, all 118 tool projects built, and the
75-case affected C# cohort passed three times with zero skips. The final
ordinary-lobby-switch and full-host-display refinements were built and checked
in those three cohorts after the sweep. Packaging's 17-case unittest suite and
DOM tests passed three times; pytest itself is absent from this offline lane.

The fifteen failed/slow methods each ran three times in separate filtered
processes after all other test processes from this lane had finished:

| Control | Three isolated results | Maximum case duration in each run |
|---|---|---|
| `P800HullProofGpuTests.Every_hull_of_the_real_core_renders_in_hangar_flight_and_target_pane` | pass/pass/pass | 5.349/5.451/6.212 s |
| `P660NextMatchLiveTests.Late_game_over_rebuild_ready_countdown_started_has_no_clock_jump` | pass/fail/pass | 4.446/4.511/4.525 s |
| `P796VariedLoadoutTests.Varied_equips_every_legal_hull_and_part_of_the_role_over_many_purchases` | pass/pass/pass | 5.425/5.410/5.341 s |
| `P814GunnerTests.P814b_TurretCarrier_pref_keeps_or_beats_the_Best_hulls_turret_seats` | pass/pass/pass | 3.245/3.193/3.283 s |
| `P877MineDamageLiveTests.Chased_bot_wake_mine_damages_pursuer` | pass/pass/pass | 1.656/1.620/1.663 s |
| `SC2NaniteSupportTests.Support_selection_obeys_research_dock_budget_and_mounts` | pass/pass/pass | 4.192/4.146/4.131 s |
| `LiveSideSelectTests.Flag_on_auto_seats_a_silent_peer_at_the_configured_30_seconds` | pass/pass/pass | 1.021/1.116/1.046 s |
| `NpcWingmanDefendLiveTests.Defender_disengages_when_chased_enemy_flees_and_returns_to_charge` | fail/fail/fail | 1.239/1.186/1.236 s |
| `P750NativeAsteroidTypesTests.Research_showcase_rocks_carry_native_models_and_no_rock_names` | pass/pass/pass | 1.722/1.675/1.812 s |
| `P777WarpExitWobbleTests.Npc_exit_touch_rate_is_measured` | pass/pass/pass | 9.389/9.340/9.525 s |
| `P942ResearchOrderingTests.Generated_research_never_waits_for_a_drone_or_utility_base_while_a_development_is_buyable` | pass/pass/pass | 1.064/1.009/1.017 s |
| `PlayerBundlePathsTests.LocalConfigWinsOverBundleWithBlankEnvironment` | fail/fail/fail | 0.015/0.015/0.015 s |
| `PlayerBundlePathsTests.MissingDirectoryHasNoDefault` | fail/fail/fail | 0.004/0.004/0.004 s |
| `PlayerBundlePathsTests.NativeSourceNeverGuessesAnInstallSubdirectory` | fail/fail/fail | 0.004/0.004/0.003 s |
| `PlayerBundlePathsTests.UsesExecutableDirectoryOutsideRepository` | fail/fail/fail | 0.004/0.004/0.005 s |

- `PlayerBundlePathsTests`: the same five cases failed in every run, while the
  other parameter cases passed. Mandatory task TMPDIR is inside the worktree and
  ancestor `local.env` is found. This is the existing P942/earlier path-fixture
  obstruction, already reproduced on unchanged libraries; no path resolver changed.
- `NpcWingmanDefendLiveTests`: existing expected-ship/actual-null assertion,
  persistent in prior dispatch controls; no NPC or simulation source changed.
- `LiveSideSelectTests`: sweep 31.75 against [28,31], then 3/3 pass alone; existing
  receipt/simulation-clock boundary. No seating policy or threshold changed.
- `P660NextMatchLiveTests`: sweep disabled/off saw 14.65 against [14.94,15.06];
  isolated disabled/off failed in attempt 2 at 14.85. Remains intermittent alone.
- `P877MineDamageLiveTests`: existing loopback/loadout wait timed out at 31.704 s
  in the sweep, then passed 3/3 in 1.620–1.663 s. No mine or bot code changed.
- All seven other timing-only controls passed 3/3 with every isolated case below
  ten seconds. This does not replace the sweep's timing failures.

Raw evidence: `.scratch/p944/fast/`, `.scratch/p944/isolation/` (including load
averages); concise public results and four reviewed-by-agent screenshots:
`docs/reports/P944_assets/`. Media still needs Domingo's review before push.
Report: `docs/reports/P944_REPORT.md`. No commits or pushes were made.

## P946 validation — one Fast sweep, 2026-11-07 UTC

One Fast sweep: **13,337 executions — 13,325 passed / 9 failed / 3 optional Meshy-pack skips**, infrastructure checks
passed, 664.7 s, 11 executions over the calibrated 10 s limit, shared-host load average 10–16 while it ran. None of the
nine failures touch P946's scope (intro keyboard mapping, focus drawing, intro layout; no Core, Server, sim, bot,
miner, docking or network code changed), and each matches an existing ledger signature. Isolation ran in filtered
processes after the sweep.

| Sweep exception | Observation in the sweep | Isolated outcome |
| --- | --- | --- |
| `DiscoveryTests.Coordinator_is_silent_when_tailscale_absent` | `Assert.Empty` got a real Tailscale node (`<tailnet address>`, `<tailnet host>`, LAN game on 6073) although the injected `TailscaleStatus` returned null | **fail / fail / fail** (61/55/60 ms) — environmental: this host's Tailscale interface is up, so the "absent" scan finds it; no discovery code changed |
| `MissileLockLiveTests.MissileLock_BuildsPartialThenFull_AndPosts_Tips_OverTheWire` (38.7 s, also over the guard) | "lock never reached full + partial tip (lock 1.000)" | **fail / fail / fail** in the isolated cohort (38–40 s, same 1.000 signature), then a further isolated run **passed** in 1 s — the existing intermittent partial-tip receipt failure; remains open |
| `DeadLatchRespawnLiveTests.Remote_client_sees_victim_again_after_death_and_respawn` | "B never saw A's death (IsDead never latched)" | **pass / pass / pass** (~1 s) — existing asynchronous death-receipt intermittency (also P903/P899/P896) |
| `P864dTeamLobbyClientListLiveTests.A_lobby_player_requests_a_side_and_the_join_side_relay_moves_it_out_of_the_pool` | `Assert.Contains` missing the moved ship in the relay list | **pass / pass / pass** |
| `NpcWingmanDefendLiveTests.Defender_disengages_when_chased_enemy_flees_and_returns_to_charge` | `Assert.Same` expected ship, actual null | **fail / fail / fail** — the already-led persistent NPC wingman failure (unchanged code) |
| `NpcPilotTypeGateLiveTests.Miner_idle_npc_still_picks_the_rock` (12 s) | "miner must travel toward the rock: travelled 0.0" | **pass / pass / pass** |
| `NpcPilotTypeGateLiveTests.Non_miner_idle_npc_stands_by_despite_minable_asteroid` | "non-miner must not move from spawn: travelled 199.140" | **pass / pass / pass** |
| `MiningTests.Loopback_synthetic_rock_mines_and_ore_reaches_the_wire` | "pool draw 0 should equal ship.Ore 20" | **pass / pass / pass** |
| `LiveLifepodEnduranceDisplayTests.Driver_sees_countdown_others_do_not_and_dock_cancels` | "same-side watcher never received the driver pod's relayed update" | **pass / pass / pass** |

Timing-only sweep exceptions (all previously led families, pass alone): P800 hull proof 11.633 s, MissileLock 38.733 s,
P796 loadout 10.069 s, P814 gunner 11.181 s, SC2 Nanite 10.951 s, P777 warp-exit 12.787 s, NpcDodge ×3
(10.552/10.510/11.137 s), NpcPilotTypeGate miner 12.549 s, P942 research ordering 10.257 s.

Showcase never-parks (`P700ShowcaseLiveTests.Local_showcase_never_parks_with_target_for_five_seconds`), five
sequential attempts of only that method: **4 pass / 1 fail**. The failure is the known arrival assertion (19/30 inside
the 480 s window, longest idle with target 1.958 s) — no attempt reported `Parked >5s`, and the `idleWithTarget`
assertions passed. Same arrival-sensitive signature as the existing P942 cohort (patched 4/5, dispatch 5/5); parking
behaviour unchanged.

## P945 validation — one Fast sweep + isolated follow-ups, 2026-11-07 UTC

One Fast sweep (and an earlier one taken before P945's own two findings were
fixed): the P945 additions themselves are green — all 16 new
`P945HangarArtTests` pass three times alone, and 133 affected
hangar/docked/training tests pass. The sweep's failures are the standing
ledger entries plus one new host condition:

| Test | Sweet | Alone | Notes |
|---|---|---|---|
| `DiscoveryTests.Coordinator_is_silent_when_tailscale_absent` | failed | **fail / fail / fail** (53/70/74 ms) | Not P945: the injected `TailscaleStatus = () => null` is bypassed by the engine's own tailnet scan, which finds a live Allegiance server on this workstation's tailnet (`<tailnet address>:6073`, 14 players). Environmental — the host has a running tailnet game. No discovery code changed by P945. |
| `P902SoundTemplateTests.Asr_and_cache_hit_mix_math_allocate_nothing_after_warmup` | failed | **pass / pass / pass** | Already ledged allocation intermittency. |
| `BallotVotingLiveTests.Resign_passing_deactivates_the_side_and_ends_the_mission` | failed | **pass / pass / pass** | Already ledged ballot-receipt family. |
| `MissileLockLiveTests.MissileLock_BuildsPartialThenFull_AndPosts_Tips_OverTheWire` | failed (38.8 s) | known open failure | Persistent ledger entry (P933/P935/P938 cohorts); no lock/tip code changed. |
| `NpcWingmanDefendLiveTests.Defender_disengages_when_chased_enemy_flees_and_returns_to_charge` | failed | known baseline failure | Persistent null-target assertion, fails on the unmodified base too. |
| `P912TrainingWindowTests.Basic_flight_completes_with_muted_missing_or_repeated_narration(Classic, reminders)` | failed (9 m 3 s) | not re-run (20 min budget) | M2 prompt-sequence snapshot ("native prompts missing or out of order") taken from `ClientTrainingSite.Messages` — instructor messages posted by the training host; P945 changed only client presentation (docked view flag, HUD draw path). Same full-gate-only real-window family already ledged above. |
| `P800HullProofGpuTests`, `SC2NaniteSupportTests`, `P814GunnerTests`, `P777WarpExitWobbleTests`, `P750NativeAsteroidTypesTests`, `WarpTransitServerTests`, `P942ResearchOrderingTests` | 8 executions over the calibrated 10 s Fast limit (10.5–38.8 s) | n/a | Host load average 11–14 on 24 CPUs (other lanes running); the same executions were 8–17 s in the earlier sweep. |

Two real P945 regressions were found by the first sweep and fixed before the
second: `RendererDisposalGuardTests` (the new `_hangarScreenRenderer` must be
disposed in `UnloadContent`) and `PackagingManifestTests`
(`packaging/artwork-{roots,manifest}.txt` regenerated for the hangar art stems).

## P942 validation — one Fast sweep and dispatch controls, 2026-11-07 UTC

Exactly one Fast sweep: **13,287 passed / 10 failed / 3 optional Meshy-pack
skips**, 13,300 executions in 531.4 s. All nine infrastructure checks passed.
No new P942 test failed. Six executions exceeded the calibrated 10-second
limit. The sweep remains **FAIL**; no second Fast sweep ran, and no assertion,
timeout or tier was relaxed.

All failing methods and timing offenders ran three times in filtered processes
after the live qualification cohort finished. The new P942 unit methods and
P702 tree cohort also passed their final three isolated runs with no overlapping
test process from this lane. The profile control alone redirects
`XDG_CONFIG_HOME` into writable task scratch.

| Sweep exception | Three isolated outcomes | Dispatch-library control / qualification |
|---|---|---|
| Five `PlayerBundlePathsTests` cases (Artwork executable fallback; Artwork/NativeSource missing defaults; local configuration precedence; native install-subdirectory guessing) | Each **fail/fail/fail** | The unchanged nine-case class on dispatch Core/Server libraries gives **5 failed / 4 passed ×3**. Existing mandatory in-worktree TMPDIR discovers ancestor `local.env`; no path-resolution source changed. |
| `NpcWingmanDefendLiveTests.Defender_disengages_when_chased_enemy_flees_and_returns_to_charge` | **fail/fail/fail**, 1.180/1.154/1.149 s | **fail/fail/fail** on dispatch libraries, same expected-ship/actual-null assertion. Existing persistent baseline failure; no NPC movement/targeting code changed. |
| `MissileLockLiveTests.MissileLock_BuildsPartialThenFull_AndPosts_Tips_OverTheWire` | **fail/fail/fail**, 38.336/38.275/38.289 s | **fail/fail/fail** on the dispatch client test/runtime snapshot, same final lock 1.000 without required partial tip. Remains open and is also a Fast timing failure; not classified as cleared or solely load-induced. |
| `P761StartChatComposeTests.ProfileEditor_OwnsEnterAndBackspace_AndKeepsChatClosed` | **pass/pass/pass**, 0.071/0.070/0.059 s with writable scratch profile | Existing sandbox obstruction: sweep cannot restore the read-only real user profile. No product/profile edit or write to the real profile was made. |
| `Hud12bReviewFixTests.HUD_formatted_readouts_allocate_nothing_in_steady_state` | **pass/pass/pass**, 0.006/0.006/0.008 s | Sweep observed 2,360 allocated bytes over 10,000 frames. Existing allocation-under-parallel-load family; no HUD code changed. |
| `P660NextMatchLiveTests.Late_game_over_rebuild_ready_countdown_started_has_no_clock_jump` | All four parameter cases **pass/pass/pass** | Disabled/off sweep observed 14.90 against [14.94,15.06], the existing P813b/P887 clock-observation boundary signature. No clock or countdown code changed. |
| `P800HullProofGpuTests.Every_hull_of_the_real_core_renders_in_hangar_flight_and_target_pane` (10.574 s in sweep) | **pass/pass/pass**, 3.762/4.262/4.231 s | Timing-only sweep exception; below ten seconds in each isolation. |
| `P796VariedLoadoutTests.Varied_equips_every_legal_hull_and_part_of_the_role_over_many_purchases` (Bios 11.509 s) | All three civ cases **pass/pass/pass**; per-run maximum 5.175/5.283/5.523 s | Timing-only sweep exception; no loadout selector/ranking change. |
| `SC2NaniteSupportTests.Support_selection_obeys_research_dock_budget_and_mounts` (Iron Coalition 12.318 s) | Both civ cases **pass/pass/pass**; maximum 4.033/4.001/4.042 s | Timing-only sweep exception; support selection unchanged. |
| `P814GunnerTests.P814b_TurretCarrier_pref_keeps_or_beats_the_Best_hulls_turret_seats` (13.115 s) | **pass/pass/pass**, 3.159/3.149/3.298 s | Timing-only sweep exception; turret policy unchanged. |
| `P777WarpExitWobbleTests.Npc_exit_touch_rate_is_measured` (10.069 s) | **pass/pass/pass**, 9.134/9.091/9.141 s | Timing-only sweep exception; no warp/collision code changed. |

### P942 original P700 parking/arrival control

Required five-attempt cohorts: dispatch **5/5**, patched **4/5**. Patched
attempt 4 reached 18/30 at 480.1 seconds against the unchanged 24-arrival
requirement; its longest idle with target was 1.813 seconds. Every required
attempt held the original five-second parking bound. No movement, scouting,
arrival threshold or timer was edited.

Three supplementary filtered runs per build, after all other lane tests,
gave patched **3/3** and dispatch **2/3**. The third dispatch sample reproduced
the arrival failure: 20/30 at 480.1 seconds, longest idle 1.311 seconds,
4,428 shots / 1,483 hits / 14 kills. Patched observation windows were
189.1/425.8/391.3 seconds (24 arrivals each); dispatch 244.6/276.5/480.1.
This supports an existing arrival/trajectory-sensitive fixture flake. The
exact route cause remains unestablished; the required patched gate remains
**FAIL**, and the supplementary cohort does not replace it. Across all
sixteen observations the parking bound held; each build passed 7/8 overall.

The original method retains civs 25/3, unlocked tech and its existing test
harness, with the required GC/broadcast/transport/native-update/GotoPlan
performance baseline. Research-mode behavior is covered separately by P942's
paired combat and full-tree proofs. Parking raw evidence and shared-host load
receipts are `.scratch/p942/{before,after}-parking-*` and
`.scratch/p942/{after,base}-isolation-parking-*`.

Raw evidence: `.scratch/p942/fast/`, `isolate-*.{log,trx}`,
`final-*.{log,trx}`, and `baseline-control-*.{log,trx}` under `.scratch/p942/`.
The dispatch controls use the untouched failed methods with the dispatch
product assemblies, not a modified implementation. Small receipts and the
complete qualification/parking account are in
[the P942 report](reports/P942_REPORT.md) and
[verification.json](reports/P942_assets/verification.json).

## P943 validation — one Fast sweep, 2026-11-07 UTC

One Fast sweep: **13,269 passed / 5 failed / 3 optional Meshy-pack skips**, infrastructure checks passed, 587.8 s.
None of the five touch P943's scope (training slideshow rendering/captions; no Core, sim, miner, NPC, docking or audio
code changed), and each matches its existing ledger signature:

| Method | Sweep observation | Isolation |
| --- | --- | --- |
| `P902SoundTemplateTests.Asr_and_cache_hit_mix_math_allocate_nothing_after_warmup` | `Assert.Equal` 0 vs 6,552 bytes — the known allocation intermittency | **pass / pass / pass** alone |
| `P739TimerLiveTests.Warp_bomb_notice_matches_captured_blast_deadline_and_stays_in_mission` (delay 7) | Floating upper-bound signature | **pass / pass / pass** (3 theory rows each) |
| `NpcWingmanDefendLiveTests.Defender_disengages_when_chased_enemy_flees_and_returns_to_charge` | Existing null-target assertion | **fail / fail / fail** alone, exactly as already led (fails on the unmodified base too) |
| `MinerDecisionCycleLiveTests.Accepted_return_to_base_slot_cleared_on_dock` | Miner-cycle intermittency (null base slot) | **pass / pass / pass** alone |
| `DockDockingTests.Ship_nowhere_near_a_station_is_unaffected` | Stale dock-status history (station 1051 vs −1) | **pass / fail / pass** alone, as led |

Two existing executions also exceeded Fast's ten-second limit: `P817NativeMinerDecisionCycleLiveTests.Idle_npc_repeats_mine_sell_cycle_indefinitely_after_first_sell` (46.863 s) and
`MinerDecisionCycleLiveTests.Accepted_return_to_base_slot_cleared_on_dock` (14.783 s) — both previously ledgered
timing offenders on this shared host.

## P941 validation — one Fast sweep, 2026-11-06 UTC

One Fast sweep (Claude, after Hermes's session ended): 13,241 tests, **FAIL**. Task-caused and fixed:
`P625KeyMapTests.Overrides_IgnoreUnknownActionsAndBadCodes` (legacy keymap migration). Not task-caused (P941 touches
no Core code): `WarpTransitServerTests.ShipInsideWarp_GetsTransited_AndPeerSeesSectorChange` **pass/pass/pass**
isolated; `NpcWingmanDefendLiveTests.Defender_disengages_when_chased_enemy_flees_and_returns_to_charge` and
`NpcWingmanAttackLiveTests.Wingman_engages_nearest_enemy_closes_distance_and_deals_damage` (36 s, also over the
10 s Fast limit) are already ledgered above.

## P936 validation — one Fast sweep and isolated controls, 2026-11-06 UTC

One Fast sweep: **13,194 passed / 11 failed / 3 optional Meshy-pack skips**,
13,208 executions in 580.3 s. Infrastructure checks passed. Two existing cases
also exceeded Fast's ten-second limit (attack 36.844 s; warp exit 10.892 s).
The sweep remains **FAIL** and was not repeated.

The task-owned failure was `P625bActionMapGuardTests`: the new browser password
handler used raw Tab/Enter. It now uses the catalogued `LobbyPassword` action and
existing `LobbyJoin`; the guard and affected action-map/client group passed
**1,005/1,005 ×3** after the final core/lobby follow-ups, including new
Classic/Modern rebind/finish-key tests. All 30 final P936 cases passed in each
repetition, with zero skips. No guard allow-list,
assertion, timeout or tier was relaxed.

| Non-task sweep failure / timing offender | Three isolated outcomes | Qualification |
|---|---|---|
| Five `PlayerBundlePathsTests` cases | Same five **fail/fail/fail**; full class **4 passed / 5 failed** each time | Existing fixture ancestry issue: mandated in-worktree TMPDIR finds the ancestor local.env. Same P932/P939/P901 family; resolver and path fixtures unchanged. |
| `P761StartChatComposeTests.ProfileEditor_OwnsEnterAndBackspace_AndKeepsChatClosed` | **pass/pass/pass**, 0.057/0.055/0.066 s, with scratch `XDG_CONFIG_HOME` | Existing sandbox obstruction: sweep cannot restore the read-only real user profile. The writable-profile control changes no product or fixture code. |
| `P739TimerLiveTests.Warp_bomb_notice_matches_captured_blast_deadline_and_stays_in_mission` | All three delay rows **pass/pass/pass** | Existing floating upper-bound signature on delay 7 in the sweep. Timer/warp-bomb code unchanged. |
| `NpcWingmanDefendLiveTests.Defender_disengages_when_chased_enemy_flees_and_returns_to_charge` | **fail/fail/fail**, 1.267/1.209/1.263 s; unmodified base also **fail/fail/fail** | Existing null-target assertion. Baseline archive is `bf1978d7d5c7b8602bfd581478d5e77b0c1b08f0`; no NPC/flight policy change. |
| `LiveSideSelectTests.Flag_on_auto_seats_a_silent_peer_at_the_configured_30_seconds` | **fail/fail/fail**, 1.048/1.032/1.063 s; unmodified base **fail/pass/fail** | Existing simulated-clock/receipt boundary against [28,31]; first isolated value 31.85. No seating policy or limit changed. |
| `NpcWingmanAttackLiveTests.Wingman_engages_nearest_enemy_closes_distance_and_deals_damage` | **pass/pass/pass**, 1.099/0.665/0.734 s | Existing sweep-only damage/timing signature; NPC, damage and flight code unchanged. |
| `P777WarpExitWobbleTests.Npc_exit_touch_rate_is_measured` (passed but 10.892 s) | **pass/pass/pass**, 9.024/9.184/9.154 s | Existing timing-only Fast offender; no limit relaxed. |

Evidence: `.scratch/p936/fast-tier/`, `.scratch/p936/isolations/`; small final
summary in `.scratch/p936/validation.json` (not published). P936 report:
`NAT_TRAVERSAL_P936.md`. Raw evidence remains ignored inside the permitted
worktree scratch directory. These results do not claim a green full Fast gate.

## P932 validation — one Fast sweep and three isolated reruns, 2026-11-06 UTC

Exactly one Fast sweep: **13,129 passed / 10 failed / 3 optional Meshy-pack
skips**, 13,142 executions in 538.8 s. All nine infrastructure checks passed.
The passing warp-exit case also exceeded Fast's ten-second limit (10.519 s).
The sweep remains **FAIL**; it was not rerun. All new P932 cases passed, and
the six new methods plus the existing no-station regression passed 48/48
executions in three isolated repeats per method, with zero skips.

Each failing method was rerun three times alone with serial collection
settings; the path-fixture family ran as one serial class. No unrelated
implementation, timeout, tier or assertion was changed.

| Sweep failure / timing offender | Three isolated outcomes | Qualification |
|---|---|---|
| Five `PlayerBundlePathsTests` cases (Artwork executable fallback; Artwork/NativeSource missing defaults; local configuration precedence; native-source install-subdirectory guessing) | Each **fail/fail/fail**; whole class **5 failed / 4 passed** in each run | Existing fixture ancestry conflict: mandated in-worktree TMPDIR finds ancestor `local.env`. P928 and P939 already record the same baseline family. |
| `P761bCommandRelayTests.QueuedStationGoto_KeepsOwnTarget_AndRelaysTheStationToTheTeammate` | **pass/pass/pass**, 0.430/0.452/0.420 s | Existing relay/lookup timing family: sweep got `goto` rather than `goto Garrison`. No command, station lookup or server code changed. |
| `ShipStatusServerTests.PeerReceivesItsOwnShipStatus_InJoinBurst` | **pass/pass/pass**, 0.344/0.332/0.335 s | Existing join-status receipt race: sweep expected flying=3, saw docked=2. Previously recorded with baseline controls in P928. |
| `NpcWingmanDefendLiveTests.Defender_disengages_when_chased_enemy_flees_and_returns_to_charge` | **fail/fail/fail**, 1.252/1.248/1.255 s | Existing persistent null-target assertion, including prior unmodified-source P928 controls. P932 changes no NPC/flight policy or server simulation code. |
| `MissileBroadcastTests.Directly_fired_missile_broadcasts_create_to_teammate_and_destroy_to_whole_mission` | **pass/pass/pass**, 0.491/0.489/0.441 s | Existing earlier-destruction signature: sweep ended about 1062.9 m from launch against the fixture's approximately 1848.6 m full-lifespan expectation. The same signature is already recorded below; no missile code changed. |
| `LiveSideSelectTests.Flag_on_auto_seats_a_silent_peer_at_the_configured_30_seconds` | **fail/pass/pass**, 1.068/0.992/1.000 s | Existing simulated-clock/receipt boundary against [28,31]. It remains intermittent alone; no seating policy changed. |
| `P777WarpExitWobbleTests.Npc_exit_touch_rate_is_measured` (passed but 10.519 s) | **pass/pass/pass**, 9.212/9.345/9.302 s | Timing-only Fast guard failure, below ten seconds in all isolated runs. No limit or tier was relaxed. |

Raw receipts remain in `.scratch/p932/fast/` and
`.scratch/p932/flake-isolation/` in the task worktree; a compact persisted
summary and task report are under `docs/reports/P932_assets/` and
[the P932 report](reports/P932_REPORT.md). The artwork-enabled training gate
has its own complete outcome in that report; isolated passes do not clear
the failed Fast sweep.

### P932 artwork-enabled training gate

`tools/run-training-tests.sh --no-build --repeat 1` completed with **341 passed /
1 failed / 4 skipped** (ClientCore 100/0/0; MonoGame 241/1/4). All P932 cases,
both P918 windows, both early-Nanite windows and all 20 lesson-prefix windows
passed. The four skips are the optional miner-loss and combined-loss branch
proofs in both styles; artwork was present, with zero missing-artwork skips.

The sole failure was
`P912TrainingWindowTests.Every_lesson_completes_in_one_real_window_session(mission: 4, style: "Modern", policy: null)`:
its observer timed out at M4 goal 9.3, waiting for the goto-command acceptance
after construction. The trace still held destroyed scout 1100 as the current
target. No purchase was reached. Classic default, Modern departure and Modern
lesson-9/10 prefix proofs passed in the same gate. Exact-case isolation passed **3/3 complete courses**, with case durations
**00:11:12/00:11:06/00:09:46**. This was a full-gate-only window failure in these samples;
the command-delivery cause was not isolated. The failed full gate remains failed.

Receipts: `.scratch/p932/training/1/`,
`.scratch/p932/training-isolation/` and
`.scratch/p932/p917/windows/m4-Modern-default-a85b4e53c7a74056978ec88be6a1d3a8/`.
No unrelated M4 condition, proof driver, timeout or assertion was changed.

## P931 validation — one Fast sweep, 2026-11-05–06

Exactly one Fast sweep: **13,113 passed / 9 failed / 3 optional-pack skips**,
13,125 executions in 533.4 s. All nine infrastructure checks pass. The sweep
remains **FAIL**; the runner's existing quarantine retry passed its AddPart
case, but no second tier sweep, timeout increase or test exclusion was used.
Task-owned real-input training failures are reported in the P931 report and
are not classified as unrelated flakes.

| Failure / timing offender | Three serial filtered outcomes | Evidence / classification |
|---|---|---|
| `P804ClientLeadTests.Lead_solve_and_green_ramp_allocate_nothing_per_frame` | **pass/pass/pass**; 625/663/595 ns per solve | Sweep's allocation assertion passed; its **20,313 ns** measurement exceeded the unchanged 20,000 ns time budget. Lead implementation unchanged. Timing flake under the overlapping real training windows. |
| Five `PlayerBundlePathsTests` cases (executable Artwork fallback; missing Artwork/NativeSource defaults; local config; native subdirectory guessing) | Each **fail/fail/fail**; two selected SkyPack companions pass 3/3 | Same mandated in-worktree TMPDIR / ancestor `local.env` signature documented in P928/P924; resolver unchanged. |
| `NpcWingmanDefendLiveTests.Defender_disengages_when_chased_enemy_flees_and_returns_to_charge` | **fail/fail/fail**; 0.875/0.853/0.859 s | Same existing null target/reference assertion and prior dispatch-base controls documented in P928; NPC code unchanged. |
| `LiveSideSelectTests.Flag_on_auto_seats_a_silent_peer_at_the_configured_30_seconds` | **pass/pass/pass**; 0.711/0.709/0.728 s | Sweep observed **31.35** against [28,31]; existing receipt/clock boundary, also recorded in P928. |
| `P777WarpExitWobbleTests.Npc_exit_touch_rate_is_measured` | **pass/pass/pass**; 9.590/9.609/9.401 s | Sweep passed its assertions in **10.747 s**, above the calibrated **10.180 s** Fast guard. Warp implementation unchanged. |
| `AddPartTests.Part_pickup_decline_recreates_treasure_at_current_position` (existing quarantine) | **pass/pass/pass**; 0.058/0.057/0.162 s | Sweep's `Assert.Single` saw the expected part plus a random powerup. The runner's existing one-case retry also passed. Pickup/world code unchanged. |

The filtered Core cohort contains eleven cases per run: **5 passed / 6 failed /
0 skipped** each time, including the five bundle failures and one defender
failure. It runs with serial collection settings while the real training gate
and proof windows remain active; this is method isolation, not an exclusive-host
claim. Raw receipts: `.scratch/p931/non-task/` in the task worktree. Compact
results and task verdicts: [P931 report](reports/P931_REPORT.md).

One incremental parallel solution build also hit MSB4166 worker exits without
a diagnostic file. Three subsequent whole-solution builds using `-m:1
-nr:false` and disabled node reuse each passed with **0 warnings / 0 errors**
(`.scratch/p931/build-serial-{1,2,3}.log`); the retained Debug build uses the same
process settings. No product change or check exclusion was used for this.

The one-repeat training sweep also exposed an existing **observer read race**
in `Every_lesson_completes_in_one_real_window_session(8, Modern, loss)`:
the bomber died after `TrainingShipExists` released the host lock but before
its accepted order was read, producing “input replay replaced the authored
bomber attack order.” P931 captures the order first and performs the same
locked liveness/live-outpost checks afterward. The order predicate, actual
HP/death checks and messages are unchanged. **Three full replays of the exact
failed window policy pass**, with the native `tm_8_15` loss voice and final
completion marker checked, **zero skips** (`.scratch/p931/loss-isolation/`).
This is a corrected harness race, not a remaining policy flake; the original
sweep failure remains recorded. P931's task-owned hunt failures remain in its
report and retain partial matrix status.

The requested P931 one-repeat training command reached its owned **140-minute
bound** at 01:45:40 UTC on November 6 and was terminated (exit -15). ClientCore
completed **102/102, zero skips**; MonoGame produced no final TRX. Its 36 P912
window observations comprise **32 completed proofs, three failures and one
interrupted Modern early-Nanite window**. The failures are the original observer
race above and both task-owned hunt cases; both combined-ending cases passed.
A separate fresh early-Nanite invocation passes **2/2, zero skips**. The other
training classes pass **219/219, zero skips**, in a separately disclosed
continuation (`.scratch/p931/training-continuation-nonwindow/`). Owner-controlled interruption is not relabeled a test flake, and these
follow-ups do not make the original broad gate green.

## P928 validation — one Fast sweep, serial Live runs and base controls, 2026-11-04

Exactly one Fast sweep: **13,060 passed / 10 failed / 3 optional Meshy-pack
skips**, 13,073 executions in 882.0 s. All nine infrastructure checks passed.
The missile-tip failure also exceeded the 10 s guard (38.498 s). The sweep
remains **FAIL**; no full-tier retry, timeout relaxation, tier change or unrelated
implementation fix was made. Artwork and worktree `local.env` were available;
all task-specific tests and the 126-case affected cohort had zero skips.

Each affected test family was then run three times with serial collection
settings. To distinguish baseline defects, the exact dispatch-base Core source
files were temporarily restored, the new scratch partial and scratch-API test
were removed for compilation, and the same failed methods were run three times.
All optimized sources/tests were restored afterward and requalified.

| Sweep failure | Optimized tree: three filtered runs | Dispatch base: three controls | Qualification |
|---|---|---|---|
| Five `PlayerBundlePathsTests` cases (Artwork executable fallback; Artwork/NativeSource missing defaults; local config precedence; native-source subdirectory guessing) | Each **fail/fail/fail**; selected SkyPack companion cases pass 3/3 | Same five cases **fail/fail/fail**, companions pass 3/3 | Existing mandated in-worktree TMPDIR discovers ancestor `local.env`; no resolver/fixture change. |
| `NpcWingmanDefendLiveTests.Defender_disengages_when_chased_enemy_flees_and_returns_to_charge` | **fail/fail/fail** | **fail/fail/fail** | Existing null target assertion; same dispatch-base behavior. |
| `ShipStatusServerTests.PeerReceivesItsOwnShipStatus_InJoinBurst` | **fail/fail/fail** | **fail/fail/fail** | Existing expected flying=3 / observed docked=2 history. |
| `MissileLockLiveTests.MissileLock_BuildsPartialThenFull_AndPosts_Tips_OverTheWire` | **fail/fail/fail** | **pass/fail/pass** | Existing partial-tip receipt failure with final lock 1.000, sensitive to receipt timing. Not classified as cleared or solely load-induced. |
| `LiveSideSelectTests.Flag_on_auto_seats_a_silent_peer_at_the_configured_30_seconds` | **pass/pass/pass** | **pass/fail/fail** | Sweep observed 31.2 simulated seconds against [28,31]; existing clock/receipt boundary. |
| `AsteroidMinedLiveTests.No_broadcast_fires_once_miner_hits_capacity` | **pass/pass/fail** | **pass/pass/pass** | Existing mining receipt family (also ledgered in P903/P926): sweep saw one queued report after the asserted drain. Intermittent here; this small control sample does not establish its cause or clear the failure. |

The required normal-home showcase never-parks case passes **5/5**. The additional
swapped-home case is **pass/fail/pass/pass/pass**: the failed second run reached
17/30 objectives at 480 s, with **1.705 s** maximum parked-with-target, 3,508
shots and 1,448 hits. This is the exact pre-existing swapped arrival-deadline
failure ledgered under the P925+P926 merge below (2/3 in standalone base-era
runs). The final three serial repetitions pass; every one of the ten normal /
swapped executions holds the actual five-second parking bound (maximum 2.006 s).
Do not relabel the composite swapped gate as wholly green.

P928's original and supplemental 500-tick fixtures pass after all allocation
cuts; the collision-order negative control fails on tick 1. A final regression
covers cleanup when hull-support construction throws. Evidence and qualification
counts: [P928 report](reports/P928_REPORT.md),
[P928 verification](reports/P928_assets/verification.json). Raw logs/TRX remain
under `.scratch/p928/` in the task worktree. No commits or pushes were made.

## P939 verification — one Fast sweep and 3× isolation, 2026-11-05

**One Fast sweep**: 13,129 executions, **13,123 passed / 3 failed / 3
not-executed (optional Meshy pack) / 2 slow**, 534.4 s. All infrastructure checks
passed; worktree `local.env` was present (artwork corpus configured), so the
map-gen cohort ran rather than skipped. The sweep is **FAIL**; each failure was
re-run three times alone, serially, and none is in a file P939 touched (no map,
asteroid, class-selection or parity test failed).

| Sweep failure | Three runs alone | Qualification |
|---|---|---|
| `MissileLockLiveTests.MissileLock_BuildsPartialThenFull_AndPosts_Tips_OverTheWire` (38.3 s, also over the 10 s guard) | **pass / fail / pass** | Existing ledgered partial-tip receipt timing failure (P928: optimized fail/fail/fail, dispatch base pass/fail/pass). Intermittent; unrelated to map generation. Not cleared. |
| `NpcWingmanDefendLiveTests.Defender_disengages_when_chased_enemy_flees_and_returns_to_charge` | **fail / fail / fail** | Existing baseline defect recorded by P928 (pristine dispatch-base tree also fail/fail/fail): null target where the test expects the chasing ship. Not P939's to fix; unchanged by this task. |
| `LiveSideSelectTests.Flag_on_auto_seats_a_silent_peer_at_the_configured_30_seconds` | **pass / pass / pass** | Load/timing flake — the sweep saw 31.1 simulated seconds against the [28, 31] window (P928 already lists the clock/receipt boundary). |

Slow-but-passing Fast executions (over the calibrated 10 s limit) beyond the
missile-lock case: `P777WarpExitWobbleTests.Npc_exit_touch_rate_is_measured`
(10.49 s, passed). No tier change, timeout relaxation or unrelated fix was made.

The showcase never-parks pair (`P700ShowcaseLiveTests`) was then run five times
serially (both cases per run): normal home **pass/fail/pass/pass/fail**, swapped
home **pass/pass/pass/pass/pass**. The two failures ran the whole 480 s window and
failed the end-of-window `reached >= 24` assertion, not the 5 s parking bound
(parking never exceeded that bound in any run). Three further runs of the failing
normal case **alone** were **pass/pass/pass**. A base control pair on
`HEAD:src/Allegiance.Core/MapGen/MissionMapGenerator.cs` failed too — the swapped
case, `reached=17/30` at 480.1 s, `longest speed<1 with target=0.601s` — i.e. the
P928-ledgered arrival-deadline shape, on a file map (`m2xspiderw.igc`, no MapGen,
`techMode` unlocked) that P939's changed code never reaches. Host load 4.5–5.5 on
24 CPUs. Raw logs are in `~/allegiance-evidence/P939/`; the summary is under
`docs/reports/P939_assets/`.

## P924 verification

Exactly **one Fast sweep**: **12,983 passed / 12 failed / 3 optional-pack skips**,
12,998 executions in 555.1 s. All nine infrastructure checks passed. Two
executions exceeded the ten-second guard: missile tips (38.317 s, also failed)
and native warp-exit wobble (10.772 s). The sweep remains **FAIL** and was not
rerun. All new task-specific cases pass; no HUD, transport, combat, pathing, voting
or profile implementation changed.

The final synthetic-debug/home-swap compatibility check was added after the
sweep. The complete solution/tool/AOT gate and all new P924 core/loopback tests
were requalified afterward. The installed SDK's implicit ILCompiler 10.0.11
also produced NU1903 during the later restore; qualification used official
compiler/runtime 10.0.14 in task-local scratch with auditing enabled, without
repository toolchain edits. Details and the advisory are in the P924 report.

| Failure / timing offender | Three isolated outcomes | Classification |
|---|---|---|
| `PlayerBundlePathsTests` (four methods, five failed cases) | **5 failed / 4 passed** in each of three complete family runs | Existing fixture-ancestry conflict: required worktree-local `TMPDIR` makes an allegedly external directory discover the checkout's `local.env`. Same persistent P915/P914/P913 signature. No resolver change. |
| `P761StartChatComposeTests.ProfileEditor_OwnsEnterAndBackspace_AndKeepsChatClosed` | **fail/fail/fail**, 0.049/0.048/0.060 s; **pass/pass/pass** with scratch `XDG_CONFIG_HOME`, 0.065/0.065/0.054 s | Existing sandbox blocks restoring the real user profile. Writable scratch configuration resolves the obstruction; profile/UI code unchanged. |
| `P897ClientTransportTests.Reconnect_reuses_initial_protocol_even_after_preference_changes` | **pass/pass/pass**, both TCP and UDP each time (6/6 cases) | Sweep TCP setup threw `Address already in use`. Standalone reconnect succeeds; no transport code change. |
| `MissileLockLiveTests.MissileLock_BuildsPartialThenFull_AndPosts_Tips_OverTheWire` | **fail/fail/fail**, 38.372/38.325/38.287 s | Known missing partial-tip receipt despite final lock 1.000; persistent alone, not classified as solely load-induced. Also exceeds Fast's time guard. |
| `NpcWingmanDefendLiveTests.Defender_disengages_when_chased_enemy_flees_and_returns_to_charge` | **fail/fail/fail**, 1.234/1.245/1.229 s | Known null `DefendTarget` / reference assertion, persistent under prior baseline controls. No NPC/flight policy change. |
| `ShipStatusServerTests.PeerReceivesItsOwnShipStatus_InJoinBurst` | **pass/pass/fail**, 0.321/0.326/0.367 s | Known expected flying=3 / actual docked=2 join-burst history; intermittent even alone. |
| `LiveLifepodEnduranceDisplayTests.Driver_sees_countdown_others_do_not_and_dock_cancels` | **pass/pass/pass**, 1.664/1.726/1.655 s | Sweep missed the same-side driver's relayed pod update. Existing intake/receipt-order fixture family; no pod update code change. |
| `BallotVotingLiveTests.Resign_passing_deactivates_the_side_and_ends_the_mission` | **pass/pass/pass**, 0.616/0.606/0.571 s | Sweep received the proposal-passed chat but missed the terminal expected receipt. Existing ballot receipt family; no voting code change. |
| `P777WarpExitWobbleTests.Npc_exit_touch_rate_is_measured` | **pass/pass/pass**, 9.529/9.514/9.511 s | Timing only: 10.772 s in sweep, below the ten-second limit in every isolated run. |

The five required switch-off showcase executions are **4 pass / 1 fail**.
All five held the parking bound. The failed composite run reached 27/30, fired
144 shots with zero hits at 480 s, and had 0.000 s parked-with-target. The next
three isolated off executions pass 3/3, and the fifth also passes. The same
zero-hit combat-coverage signature is recorded under P895. An unmodified
`8a2f35f3` control built from `git archive HEAD` passes in 213.851 s; this is
context, not a deterministic replay or proof of win balance. The new switch-on
showcase cohort passes **3/3**, 184.250/305.193/455.669 s, with real hits and a
maximum 2.006 s stopped-with-target interval. No assertion, timeout or tier was
weakened to obtain these results.

Final P924 qualification: **18/18 core cases**, **3/3 real 15v15 native-kill
completions**, **9/9 evidence-parser** and **9/9 matchup-tally Python cases**
pass across three isolated repetitions. P825 compatibility passes **27/27**;
focused cohorts have zero skips. Serial isolation's passive one-minute load
samples range 0.26–1.17; no synthetic load was created.

Small results: [P924 validation](reports/P924_assets/validation.json).
Raw TRX/logs are retained in `.scratch/p924/` in the P924 worktree. Full audit,
evidence limits, toolchain qualification and rig plan: [P924 report](reports/P924_REPORT.md).

## P915 verification

Exactly one Fast sweep: **12,935 passed / 12 failed / 3 optional-pack skips**,
**12,950 executions in 542.831 s**. All **nine infrastructure checks** passed.
Six executions exceeded the ten-second limit (one was also a failed missile
receipt test). All **35 new P915 cases passed**. The tier remains **FAIL**;
it was not rerun.

Failures and timing methods were isolated three times with artwork and
worktree-local temporary/configuration directories. No Core, Server, transport,
bot policy, binding or shop implementation changed in this task.

| Method / family | Isolation result | Classification |
|---|---|---|
| `PlayerBundlePathsTests` (four failed methods, five failed theory cases) | **5 failed / 4 passed** in each of three family runs | Existing repository-ancestry fixture conflict: worktree-local `TMPDIR` makes supposedly external fixtures discover this checkout's `local.env`. Same five failures as P914/P913; not a resolver change. |
| `HelpOverlayTests.Table_ContainsCoreBindings` | All 13 theory cases **pass/pass/pass** | Sweep-only Space / "Fire weapons" binding failure. No help or keybinding change. |
| `ShopLauncherMountNumberingLiveTests.ShopBuyOrder_LauncherGrantedAtMount0_LocksAndFires_Live` | **pass/pass/pass** | Known launcher-receipt intermittency; same historical signature. |
| `ShipStatusServerTests.PeerReceivesItsOwnShipStatus_InJoinBurst` | **pass/pass/fail**, 356/359/391 ms | Known expected flying=3 / actual docked=2 join-burst history; intermittent even alone, remains open. |
| `P838GcProfTests.Gcprof_line_emitted_with_sane_values_and_tickprof_format_unchanged` | **pass/pass/pass**, 86/86/76 ms | Sweep allocation value 3827.1 outside 0.8–16; process-level allocation/load sensitivity. Profiler unchanged. |
| `NpcWingmanDefendLiveTests.Defender_disengages_when_chased_enemy_flees_and_returns_to_charge` | **fail/fail/fail** | Existing null `DefendTarget` / reference assertion at line 179, already persistent under P914 and earlier base controls. Remains open. |
| `DockDockingTests.Ship_nowhere_near_a_station_is_unaffected` | **pass/fail/pass**, 627/600/584 ms | Known stale dock-status history (station 1051 vs -1); intermittent even alone, remains open. |
| `MissileLockLiveTests.MissileLock_BuildsPartialThenFull_AndPosts_Tips_OverTheWire` | **pass/fail/pass** | Known missing partial-tip receipt despite lock reaching 1.000; failed isolation is also ~38 s. Not solely a sweep/load flake; remains open. |
| `P800HullProofGpuTests.Every_hull_of_the_real_core_renders_in_hangar_flight_and_target_pane` | **pass/pass/pass**, approximately 3/3/4 s | Timing only: sweep 15.108 s; all-hull GPU proof passes alone. |
| `SC2NaniteSupportTests.Support_selection_obeys_research_dock_budget_and_mounts` | Both civ cases **pass/pass/pass** (6/6) | Timing only: civ 25 sweep 11.569 s; each complete two-case isolation took ~7 s. |
| `P796VariedLoadoutTests.Varied_equips_every_legal_hull_and_part_of_the_role_over_many_purchases` | All three civ cases **pass/pass/pass** (9/9) | Timing only: civ 35 sweep 10.532 s; complete three-case isolation runs took ~12 s. |
| `P814GunnerTests.P814b_TurretCarrier_pref_keeps_or_beats_the_Best_hulls_turret_seats` | **pass/pass/pass**, ~3 s each | Timing only: sweep 12.318 s; same earlier fixture sensitivity. |
| `P777WarpExitWobbleTests.Npc_exit_touch_rate_is_measured` | **pass/pass/pass**, CLI-rounded ~10 s each | Timing only: sweep 10.790 s; remains a near-limit native-physics fixture. |

Raw results: `.scratch/p915/evidence/fast/` and
`.scratch/p915/evidence/isolation-*.log` in the P915 worktree. Summary and visual
proof: [P915_REPORT.md](reports/P915_REPORT.md). The final UV-scope narrowing,
which excludes every ordinary core hull, was subsequently checked by the
solution/tool builds, three focused runs and the visual comparison; no second
Fast sweep was run.

## P914 verification (2026-11-02 UTC)

Exactly one Fast sweep: **12,904 passed / 8 failed / 3 optional private-pack
skips**, **12,915 executions in 532.542 s**. All **nine infrastructure checks**
passed. Two other executions exceeded the ten-second timing limit. All **26
new P914 cases passed**; the tier remains **FAIL** and was not rerun.

All nine failed/slow methods ran **three times alone** in fresh processes with
artwork, server GC, and the lane's required worktree-local temporary directory:
**36 case executions: 14 passed / 22 failed / 0 skipped**.

| Method / family | Isolation result | Classification |
|---|---|---|
| `PlayerBundlePathsTests.UsesExecutableDirectoryOutsideRepository` | Artwork **fail/fail/fail**; SkyPack **pass/pass/pass** | Environment/fixture ancestry: `Path.GetTempPath()` is inside this checkout under the lane scratch rule, so the resolver sees the real repository configuration. Resolver unchanged. |
| `PlayerBundlePathsTests.MissingDirectoryHasNoDefault` | Artwork and NativeSource each **fail/fail/fail**; SkyPack **pass/pass/pass** | Same worktree-local temp ancestry conflict. |
| `PlayerBundlePathsTests.LocalConfigWinsOverBundleWithBlankEnvironment` | **fail/fail/fail** | Same ancestry conflict: the executable-side lookup reaches the real checkout before the fixture's working-directory config. |
| `PlayerBundlePathsTests.NativeSourceNeverGuessesAnInstallSubdirectory` | **fail/fail/fail** | Same ancestry conflict; the configured native source is found. |
| `P761StartChatComposeTests.ProfileEditor_OwnsEnterAndBackspace_AndKeepsChatClosed` | **fail/fail/fail**, 0.049/0.060/0.060 s; then **pass/pass/pass** with scratch `XDG_CONFIG_HOME` | Existing user profile is read-only in the sandbox; the fixture's restore write fails. Profile implementation unchanged. Writable scratch configuration resolves it. |
| `NpcWingmanDefendLiveTests.Defender_disengages_when_chased_enemy_flees_and_returns_to_charge` | **fail/fail/fail**, 1.174/1.216/1.144 s | Existing null `DefendTarget` at line 179, same P913/P908/P907/P906/P904 failure. Remains open. |
| `DockDockingTests.Ship_nowhere_near_a_station_is_unaffected` | **pass/fail/pass**, 0.580/0.580/0.571 s | Existing stale dock-status history, station 1051 versus -1. Still intermittent alone; remains open. |
| `P814GunnerTests.P814b_TurretCarrier_pref_keeps_or_beats_the_Best_hulls_turret_seats` | **pass/pass/pass**, 3.209/3.228/3.297 s | Timing only: sweep 10.319 s; same P913 family. |
| `P777WarpExitWobbleTests.Npc_exit_touch_rate_is_measured` | **pass/pass/pass**, 9.539/9.979/9.918 s | Timing only: sweep 10.464 s; near-limit fixture, same P913 family. |

The final affected Core cohort passed **180/180** (60 ×3), including the new
station accounting/deadline/feed assertions; new client cases passed **15/15**
(5 ×3), and existing HUD result models passed **60/60** (20 ×3), all with zero
skips. A final spectator-feed notification/assertion added after Fast discovery
was covered by those final filtered runs and the clean solution/AOT builds.

The serial showcase never-parks cohort passed **5/5**, matching P913's saved
**5/5** baseline: **244.063 / 481.974 / 252.780 / 197.342 / 227.874 s**.
All reached 24/30, recorded real combat hits and passed the fixture's zero
simulation-stage-failure assertion. Maximum stopped-with-target interval:
**1.905 s**, versus the saved baseline's **1.913 s**. No performance A/B claim.

Evidence: [P914 report](reports/P914_REPORT.md),
[compact gate and isolation receipts](reports/P914_assets/validation.json).
Raw logs/TRX remain under the worktree's `.scratch/p914/`.

## P913 verification (2026-11-02 UTC)

Exactly one Fast sweep: **12,878 passed / 8 failed / 3 optional private-pack
skips**, **12,889 executions in 600.132 s**. All **nine infrastructure checks**
passed. Eight executions exceeded the calibrated ten-second limit. The whole
Fast tier was not rerun and its result remains **FAIL**.

Every failed or slow method was repeated **three times alone**, serially, with
artwork and server GC. Thirteen methods produced **48 case executions: 42
passed / 6 failed / 0 skipped** (the loadout and support methods are theories).
No failed assertion or unrelated production rule was changed. Physical temp
files and XDG settings stayed in task scratch; a directory-descriptor alias
preserved the existing bundle fixtures' outside-repository lexical temp path.

| Method / family | Three standalone results | Finding |
|---|---|---|
| `MissileLockLiveTests.MissileLock_BuildsPartialThenFull_AndPosts_Tips_OverTheWire` | **pass/pass/pass**, 1.808/1.820/1.767 s | Sweep missed the partial tip despite lock 1.000 (38.225 s); known wire-receipt intermittency. Below 2 s alone. |
| `DockDockingTests.Ship_nowhere_near_a_station_is_unaffected` | **pass/fail/pass**, 0.621/0.600/0.620 s | Known stale dock-status history (station 1051 versus -1); still intermittent alone, remains open. |
| `ArmedProbeLiveTests.Armed_probe_spawns_hits_and_damages_enemy_with_ammo_decrement_live` | **pass/pass/pass**, 0.599/0.612/0.621 s | Sweep saw no enemy damage and took 40.018 s; same load/receipt family already ledgered under P887. Passes alone. |
| `NpcWingmanDefendLiveTests.Defender_disengages_when_chased_enemy_flees_and_returns_to_charge` | **fail/fail/fail**, 1.266/1.232/1.176 s | Same null DefendTarget at line 179 as P908/P907/P906/P904; persistent existing failure, remains open. |
| `NpcWingmanAttackLiveTests.Wingman_with_no_enemy_in_sector_stands_by` | **pass/pass/pass**, 2.272/2.385/2.263 s | Sweep-only standby displacement 252.377; this exact method is newly itemized here. All three fresh processes pass. NPC/flight/collision rules unchanged. |
| `LiveSideSelectTests.Flag_on_auto_seats_a_silent_peer_at_the_configured_30_seconds` | **fail/pass/fail**, 0.989/1.002/1.047 s | Known receipt/simulation-clock boundary; sweep 31.85 against [28,31]. Still intermittent alone, remains open. |
| `ShipStatusServerTests.PeerReceivesItsOwnShipStatus_InJoinBurst` | **pass/pass/pass**, 0.327/0.328/0.310 s | Known join-burst flying=3 versus docked=2 receipt signature; passes alone. |
| `AsteroidCombatLiveTests.Lethal_hit_kills_asteroid_removes_from_mineable_and_broadcasts_destroy_to_whole_mission` | **pass/pass/pass**, 0.429/0.436/0.428 s | Sweep-only lethal-hit receipt failure (18.953 s); all three fresh processes pass. Damage/collision rules unchanged. |
| `P800HullProofGpuTests.Every_hull_of_the_real_core_renders_in_hangar_flight_and_target_pane` | **pass/pass/pass**, 3.819/4.343/4.041 s | Timing only: sweep 10.955 s; renderer unchanged. |
| `P796VariedLoadoutTests.Varied_equips_every_legal_hull_and_part_of_the_role_over_many_purchases` | **pass/pass/pass**, 3.271–5.314 s/case | Timing only: civ 35 sweep 10.126 s. All three civ cases repeated per method run (9/9 pass). |
| `P814GunnerTests.P814b_TurretCarrier_pref_keeps_or_beats_the_Best_hulls_turret_seats` | **pass/pass/pass**, 3.276/3.198/3.267 s | Timing only: sweep 11.292 s; turret/hull selection unchanged. |
| `SC2NaniteSupportTests.Support_selection_obeys_research_dock_budget_and_mounts` | **pass/pass/pass**, 2.991–4.068 s/case | Timing only: civ 25 sweep 11.614 s. Both civ cases repeated per method run (6/6 pass). |
| `P777WarpExitWobbleTests.Npc_exit_touch_rate_is_measured` | **pass/pass/pass**, 9.800/9.894/9.921 s | Timing only: sweep 10.018 s; near-limit native-physics fixture unchanged. |

The five P913 console tests passed **15/15** as three isolated repetitions of
each method, with **zero skips**. The affected moderation/callback cohort
passed **81/81** (27 ×3), also with zero skips. The required serial showcase
never-parks cohort passed **5/5**, matching the saved P908/P907 baseline pass
rate: **387.934 / 436.559 / 221.535 / 237.196 / 420.422 s**, all 24/30 reached,
real combat hits, zero sim-stage failures, and maximum stopped-with-target
interval **1.913 s**. Boot logs confirm the required snapshot/encode-once,
parallel native broadcast, pool transport, native GotoPlan and server GC.
No end-to-end performance A/B is claimed.

Evidence: [P913 report](reports/P913_REPORT.md),
[gate and three-run receipts](reports/P913_assets/validation.json).

## P908 verification (2026-10-31 UTC)

Exactly one Fast sweep: **12,805 passed / 4 failed / 3 optional private-pack
skips**, 12,812 executions in **541.700 s**. All nine infrastructure checks
passed. Three passing executions exceeded the calibrated ten-second limit.
All ten P908 Fast cases passed; all eleven new cases also passed **33/33** in
three standalone cohort runs, including the Live client join. The affected
P870/P869/P904 cohort passed **186/186** across three runs, including all 100
shipped static cores byte-identical each time. The Fast sweep was not rerun.

Every failed or slow method was repeated three times alone, serially, after the
sweep. Physical temporary data stays in worktree scratch; a directory-descriptor
alias preserves the bundle-path fixtures' outside-repository lexical paths, and
XDG configuration uses scratch. Server GC is enabled; legacy tests keep their
explicit config choices rather than receiving a global pool-mode override.
No unrelated production rule or assertion was changed.

| Method / family | Three standalone results | Finding |
|---|---|---|
| `ShopLauncherMountNumberingLiveTests.ShopBuyOrder_LauncherGrantedAtMount0_LocksAndFires_Live` (MonoGame) | **pass/pass/pass**, 1.503/1.558/1.547 s | Existing launcher receipt flake, already recorded under multiple earlier lanes. Sweep saw the empty `ExpandedPartData` default at line 252. |
| `AsteroidMinedLiveTests.No_broadcast_fires_once_miner_hits_capacity` | **pass/pass/pass**, 1.799/1.793/1.763 s | Existing mining receipt intermittency (P903): sweep expected one event, received two. Mining and broadcast behavior unchanged. |
| `ShipStatusServerTests.PeerReceivesItsOwnShipStatus_InJoinBurst` | **fail/pass/pass**, 0.360/0.308/0.313 s | Same expected flying state 3 / actual docked state 2 as P907/P896. Intermittent even alone; remains open. |
| `NpcWingmanDefendLiveTests.Defender_disengages_when_chased_enemy_flees_and_returns_to_charge` | **fail/fail/fail**, 1.178/1.141/1.238 s | Same null `DefendTarget` at line 179 as P907/P906/P904 and earlier baseline controls. Persistent existing failure; remains open. |
| `SC2NaniteSupportTests.Support_selection_obeys_research_dock_budget_and_mounts` | Both civ cases **pass/pass/pass**, 2.997–4.105 s each | Sweep-only timing: civ 25 took 10.073 s. Existing P904 selection fixture sensitivity; support/selection code unchanged. |
| `P814GunnerTests.P814b_TurretCarrier_pref_keeps_or_beats_the_Best_hulls_turret_seats` | **pass/pass/pass**, 3.214/3.432/3.143 s | Sweep-only timing: 10.512 s. Same P907/P904 fixture sensitivity; bot purchase code unchanged. |
| `P777WarpExitWobbleTests.Npc_exit_touch_rate_is_measured` | **pass/pass/pass**, 9.786/9.936/9.607 s | Sweep timing: 10.791 s. Existing near-limit native-physics fixture; no warp/physics rule changed. |

The single Fast result remains **FAIL**. Standalone executions total **20 passes /
4 failures** across seven methods (one is a two-case theory); the persistent and
intermittent failures above are not reclassified as passing. All use the ordinary
binary core path, whose loaded data is unchanged by P908.

The five required showcase never-parks repetitions passed **5/5**, matching the
saved P907/P906/P904 baseline pass rate. Durations: **193.123 / 203.466 / 221.938 /
231.264 / 240.802 s**. All reached 24/30 objectives, observed real combat hits and
asserted zero simulation-stage failures. Maximum stopped-with-target interval:
**1.007 s**. Retained boot logs confirm server/concurrent GC, pool transport,
parallel broadcast and native GotoPlan. One additional queued control
also passed in **300.912 s** (6/6 total); it is recorded separately
from the required five-run sample. No performance A/B is claimed.

Evidence: [P908 report](reports/P908_REPORT.md),
[Fast receipt](reports/P908_assets/fast-summary.json),
[three-run controls](reports/P908_assets/isolation-summary.json),
[targeted checks](reports/P908_assets/targeted-summary.json),
[showcase receipts](reports/P908_assets/showcase-summary.json).
Raw logs/TRX remain in `.scratch/p908/fast-tier/`, `.scratch/p908/isolation/`
and `.scratch/p908/results/` in the task worktree.

## P907 verification (2026-10-31 UTC)

Exactly one Fast sweep: **12,789 passed / 9 failed / 4 skipped**, 12,802
executions in **536.045 s**. All nine infrastructure checks passed. Three
executions exceeded ten seconds. All six P907 regressions passed in the sweep
and **18/18** across three standalone cohort runs. The Fast sweep was not rerun.

Four sound/source failures were harness errors: this lane supplied the native
`src` directory where those tests expect the repository root. The tester-core
case also skipped for that reason; the other three skips were the optional
Meshy pack. The isolated controls use the correct native root. Physical scratch
stays inside the worktree; the bundle-path control uses a `/proc/<parent>/fd/`
directory alias to restore its outside-repository fixture assumption. Baseline
code defaults remain pool/native/parallel/default-on GotoPlan with server GC;
legacy tests retain their explicit configuration choices.

The final controls ran serially after all five showcases. An interleaved
follow-up harness attempt was discarded; only the fresh `isolation-final`
receipts are used below. No production behavior or test assertion was changed.

| Method / family | Three standalone results | Finding |
|---|---|---|
| `P676WeaponReadyCueTests.Ready_cue_matches_native_sound_ordinal_and_has_the_real_bundled_wave_and_pipeline_entry`; `P678LobbyStatusTests.Player_waiting_voice_uses_the_real_native_wave_and_content_pipeline`; `P692NotificationCueTests.Teleport_feedback_uses_native_critical_and_noncritical_ordinals_and_bundled_assets`; `P732VoiceCatalogTests.Generated_sound_ids_and_templates_match_native_enum_and_verbatim_mdl` | Each **pass/pass/pass**, 12/12 total | Correcting the native repository root fixes all four source-path failures; no sound, asset or client code edit. |
| `NativeTesterCoreTests.Tester_core_parses_exactly_and_loads` | **pass/pass/pass**, 0.019/0.028/0.023 s | The extra native tester core is present and passes after the same harness correction; the sweep skip is retained in its original receipt. |
| `TcpTransportSendWorkerPoolTests.Explicit_thread_per_peer_config_boot_stays_thread_per_peer`; `Boot_line_prints_the_active_transport_mode` | Each **pass/pass/pass**, 6/6 total | Initial broad-suite pool environment pin overrode explicit legacy-mode config. Clearing that override restores both tests. The complete corrected transport/loopback suite passes **214/214**; the UDP/loopback cohort passes **36/36 × 3**. |
| `P800HullProofGpuTests.Every_hull_of_the_real_core_renders_in_hangar_flight_and_target_pane` | **pass/pass/pass**, 4.144/3.938/4.225 s | Sweep-only timing: 10.033 s. Existing real-hull GPU proof sensitivity; renderer unchanged. |
| `P814GunnerTests.P814b_TurretCarrier_pref_keeps_or_beats_the_Best_hulls_turret_seats` | **pass/pass/pass**, 3.293/3.199/3.270 s | Sweep-only timing: 10.030 s. Existing purchase/selection fixture sensitivity; no bot code change. |
| `P777WarpExitWobbleTests.Npc_exit_touch_rate_is_measured` | **pass/pass/pass**, 9.699/9.649/9.709 s | Sweep timing: 11.208 s. Same native-physics timing fixture remains close to the ten-second limit. |
| `P780PodRescueProbeLiveTests.Rescue_probe_deployed_via_FIRE_EXPENDABLE_rescues_a_pod` | **pass/pass/pass**, 0.336/0.336/0.324 s | Sweep missed the rescue receipt; the real deployment/rescue path passes alone. No rescue or simulation rule changed. |
| `PlayerBundlePathsTests.LocalConfigWinsOverBundleWithBlankEnvironment` | **pass/pass/pass**, 0.015/0.015/0.015 s with descriptor alias | Same worktree-local TMPDIR/root-discovery fixture obstruction recorded under P906/P904 and earlier lanes. No resolver or fixture edit. |
| `DockDockingTests.Ship_nowhere_near_a_station_is_unaffected` | **fail/pass/pass**, 0.591/0.559/0.557 s | Existing stale dock-status receipt: expected station -1, actual 1051, matching P906/P904. Intermittent even alone; remains open. |
| `NpcWingmanDefendLiveTests.Defender_disengages_when_chased_enemy_flees_and_returns_to_charge` | **fail/fail/fail**, 1.192/1.212/1.179 s | Existing null `DefendTarget`, with earlier unmodified-HEAD controls already ledgered under P901/P899/P898 and repeated under P906/P904. Remains open; no wingman/steering rule changed. |
| `ShipStatusServerTests.PeerReceivesItsOwnShipStatus_InJoinBurst` | **fail/fail/fail**, 0.372/0.355/0.366 s | Expected flying state 3, actual docked state 2. Same receipt signature previously ledgered on `c395ed6a` under P896 (fail/fail/pass alone). Remains open; no status/broadcast rule changed. |

Showcase never-parks passes **5/5**, matching P906's saved baseline pass rate.
Test durations: **381.788 / 232.318 / 241.167 / 256.020 / 348.726 s**. Every run
reached at least 24/30 objectives, recorded real shots/hits and asserted zero
simulation-stage failures. Maximum stopped-with-target interval: **1.111 s**.
There were no concurrent tests/builds from this lane during those runs.

The single Fast result remains **FAIL**; the persistent/intermittent cases above
are not reclassified as passing. See [P907 report](reports/P907_REPORT.md),
[Fast receipt](reports/P907_assets/fast-summary.json),
[three-run controls](reports/P907_assets/isolation-summary.json), and
[showcase receipts](reports/P907_assets/showcase-summary.json). Raw final
controls remain under `.scratch/p907/isolation-final/`.

## P906 verification (2026-10-31 UTC)

One Fast sweep: **12,778 passed / 15 failed / 3 optional private-pack skips**,
12,796 executions in 540.1 s. All nine infrastructure checks passed; six
executions exceeded ten seconds. The Fast sweep was not rerun.

One task regression was found and fixed: extending a private station-rescue
method with optional parameters broke P754's reflection invocation. The original
private signatures are restored through separate core helpers. The complete
`P754CallbackTests` cohort then passed **24/24** across three isolated runs. This
is a repaired task regression, not a flake.

All other failing or slow methods were repeated three times alone with server
GC, pool/native/parallel/default-on GotoPlan and artwork. Environment controls
keep physical scratch in this worktree; no unrelated production behavior or
assertion was changed.

| Method / family | Isolation | Finding |
|---|---|---|
| `P761StartChatComposeTests.ProfileEditor_OwnsEnterAndBackspace_AndKeepsChatClosed` | fail/fail/fail normally; pass/pass/pass with writable `XDG_CONFIG_HOME` | Sandbox rejects the normal profile write outside the worktree. Redirecting the profile/cache into task scratch passes; no product edit. |
| `P800HullProofGpuTests.Every_hull_of_the_real_core_renders_in_hangar_flight_and_target_pane` | pass/pass/pass; 3.741/3.875/4.324 s | Fast timing only (10.937 s); same family recorded under P904. |
| `P814GunnerTests.P814b_TurretCarrier_pref_keeps_or_beats_the_Best_hulls_turret_seats` | pass/pass/pass; 3.276/3.271/3.308 s | Fast timing only (12.119 s), previously ledgered. |
| `P877MineDamageLiveTests.Chased_bot_wake_mine_damages_pursuer` | pass/pass/pass; 1.577/1.593/1.621 s | Parallel sweep join timeout (28.960 s); same known family. |
| `SC2NaniteSupportTests.Support_selection_obeys_research_dock_budget_and_mounts` | 6/6 pass across three method runs; 2.998–4.036 s/case | Fast timing for civ 25 (11.948 s); previously ledgered. |
| `P796VariedLoadoutTests.Varied_equips_every_legal_hull_and_part_of_the_role_over_many_purchases` | 9/9 pass across three method runs; 3.412–5.433 s/case | Fast timing for civ 35 (10.116 s); purchase assertions pass alone. |
| `DockDockingTests.Ship_nowhere_near_a_station_is_unaffected` | pass/pass/pass; 0.588/0.601/0.581 s | Known stale dock-status receipt (expected -1, actual 1051), matching P904. |
| `LiveSideSelectTests.Flag_on_auto_seats_a_silent_peer_at_the_configured_30_seconds` | fail/fail/fail; 0.994/1.043/1.062 s | Existing receipt/simulation-clock boundary: 31.000000000000309, 31.30 and 32.20 against [28,31]. The same family has unmodified-HEAD failures documented below. Remains open. |
| `NpcWingmanDefendLiveTests.Defender_disengages_when_chased_enemy_flees_and_returns_to_charge` | fail/fail/fail; 1.191/1.247/1.251 s | Existing null DefendTarget signature, matching P904/P901 and their base controls. Remains open. |
| `P590PodRescueTests.Diverter_intercepts_and_rescues_a_moving_pod` | pass/pass/pass; 0.344/0.412/0.350 s | Sweep missed the rescue tick budget; assertions pass alone. |
| `P590PodRescueTests.Proactive_assignment_diverts_nearest_idle_diverter_and_rescues` | pass/pass/pass; 0.804/0.906/0.928 s | Same rescue-budget family already recorded below; no default rescue/Goto policy change. |
| `P777WarpExitWobbleTests.Npc_exit_touch_rate_is_measured` | pass/pass/pass; 9.565/10.035/9.649 s | Sweep 10.536 s; timing remains marginal and one isolated pass also exceeds ten seconds. |
| `P838GcProfTests.Gcprof_line_emitted_with_sane_values_and_tickprof_format_unchanged` | pass/pass/pass; 0.069/0.060/0.074 s | Sweep missed the global profiler's top-allocation line during concurrent tests; profiler code unchanged. |
| `P838GcProfTests.Step_allocation_attribution_accumulates_and_ignores_non_step_buckets` | pass/pass/pass; 0.006/0.007/0.006 s | Sweep's global step-attribution sample was empty; standalone assertions pass. |
| `PlayerBundlePathsTests` | same five failures in each normal 9-case class run; 27/27 pass across three descriptor-control runs | Worktree TMPDIR places the synthetic executable under this checkout's local.env. A `/proc/<controller>/fd/<directory>` alias removes that lexical ancestor while retaining physical files in scratch. Same environment issue documented under P904/P903. |

Showcase never-parks passed **5/5**, serially with artwork and all experimental
flags off. Each run reached 24/30 objectives, recorded real shots/hits and asserted
zero simulation-stage failures. Test durations: **215.204 / 460.195 / 221.579 /
228.158 / 196.831 s**; maximum stopped-with-target interval **0.907 s**. This
matches P904/P903's saved 5/5 baseline pass rate. The long second observation
needed 456.5 s to meet the unchanged coverage conditions; no controlled showcase
performance A/B is claimed and no assertion was weakened. See the
[showcase receipts](reports/P906_assets/showcase-summary.json).

See [P906 report](reports/P906_REPORT.md),
[Fast receipt](reports/P906_assets/fast-summary.json), and
[isolation receipts](reports/P906_assets/failures-summary.json). Raw logs/TRX
remain under `.scratch/p906/fast/` and `.scratch/p906/failures/`.

## P904 verification (2026-10-31 UTC)

One Fast sweep: **12,737 passed / 12 failed / 3 optional private-pack skips**, 12,752 executions in 538.7 s (including the runner’s scheduled quarantine executions). All nine infrastructure checks passed. Five Fast executions exceeded ten seconds; one was the failing missile-lock case. The Fast sweep was not rerun.

Every failing or slow method, plus the affected ballot receipt failure, was repeated **three times alone** on pool/native/parallel/default-on GotoPlan with server GC and artwork. No unrelated production behavior or test assertion was changed.

| Method / family | Isolation | Finding |
|---|---|---|
| `P800HullProofGpuTests.Every_hull_of_the_real_core_renders_in_hangar_flight_and_target_pane` | pass / pass / pass; 4.276 / 4.001 / 4.069 s | Fast timing only (10.470 s); real GPU proof passes in 4.0–4.3 s alone. |
| `DeadLatchRespawnLiveTests.Remote_client_sees_victim_again_after_death_and_respawn` | pass / pass / pass; 1.480 / 1.439 / 1.433 s | Known asynchronous remote death/respawn receipt intermittency (also P903/P899/P896). |
| `MissileLockLiveTests.MissileLock_BuildsPartialThenFull_AndPosts_Tips_OverTheWire` | FAIL / FAIL / FAIL; 38.375 / 38.390 / 38.345 s | Persistent existing missing partial-tip receipt with final lock 1.000; same 0/3 signature recorded under P901. Remains open and remains a Fast timing failure. |
| `P814GunnerTests.P814b_TurretCarrier_pref_keeps_or_beats_the_Best_hulls_turret_seats` | pass / pass / pass; 3.222 / 3.272 / 3.172 s | Fast timing only (10.735 s); 3.2–3.3 s alone. Turret selection code unchanged. |
| `SC2NaniteSupportTests.Support_selection_obeys_research_dock_budget_and_mounts` | pass / pass / pass; 4.095 + 2.990 / 2.972 + 4.120 / 4.019 + 2.957 s | Fast timing offender for civ 25 (10.341 s); both cases pass each run, 2.96–4.12 s alone. Selection/repair logic unchanged. |
| `BallotVotingLiveTests.Resign_passing_deactivates_the_side_and_ends_the_mission` | pass / pass / pass; 0.514 / 0.550 / 0.497 s | One affected-class run stopped after SIDE_INACTIVE before the later “is no more” chat had decoded; two further class runs and these exact-method runs pass. No assertion weakened. |
| `DockDockingTests.Ship_nowhere_near_a_station_is_unaffected` | pass / pass / pass; 0.553 / 0.593 / 0.575 s | Known status-history intermittency (Expected -1 / Actual 1051), already reproduced with the original server under P890/P872. |
| `LivePositionRequestTests.Empty_side_request_auto_accepts` | pass / pass / pass; 0.301 / 0.260 / 0.280 s | Side-selection/launch receipt timing failure in the parallel sweep; passes alone. |
| `LiveSuicideShipDeleteTests.Combat_death_produces_ship_delete_to_same_audience` | pass / pass / pass; 1.473 / 1.501 / 1.417 s | Guaranteed SHIP_DELETE receipt timing; passes alone. Same family already ledgered under P899. |
| `NpcWingmanDefendLiveTests.Defender_disengages_when_chased_enemy_flees_and_returns_to_charge` | FAIL / FAIL / FAIL; 1.224 / 1.225 / 1.300 s | Persistent existing null DefendTarget at line 179, identical to the unmodified-HEAD baseline documented under P901/P899/P898. Remains open. |
| `P777WarpExitWobbleTests.Npc_exit_touch_rate_is_measured` | pass / pass / pass; 9.967 / 10.078 / 9.792 s | Timing remains marginal: Fast 10.973 s; isolated 9.967 / 10.078 / 9.792 s. All assertions pass, but one standalone run still exceeds the Fast limit. |
| `P838GcProfTests.Gcprof_line_emitted_with_sane_values_and_tickprof_format_unchanged` | pass / pass / pass; 0.063 / 0.067 / 0.053 s | Suite sample reported 10704 MB against the 0.8–16 MB range; process-wide allocation sampling under concurrent test load. Profiler unchanged; 0.053–0.067 s alone. |
| `PlayerBundlePathsTests` — four methods / seven cases | Same five case failures on each of three standalone runs; **7/7 pass ×3** with the directory-descriptor control | Required TMPDIR inside the worktree makes the synthetic “outside repository” executable find this checkout’s local.env. A `/proc/<controller>/fd/<directory>` alias keeps physical files in worktree scratch while placing the synthetic executable outside the repository’s lexical parent chain. This is the already documented P903/P901 environment issue, not a production resolver change. |

The new P904 cases passed **42/42** across three isolated repetitions of each method. Other affected cohorts produced **314 passes / one ballot receipt failure**, followed by the exact ballot method’s 3/3 passes above; the three additional payday methods passed **9/9** standalone. Showcase never-parks passed **5/5**, with 24/30 reached in every run, real shots/hits and zero sim-pass failures. Test durations: 232.001 / 232.660 / 209.112 / 204.128 / 242.163 s; maximum stopped-with-target interval 1.919 s. This matches the saved P903 baseline’s 5/5 pass rate; no performance A/B is claimed.

Evidence: [P904 report](reports/P904_REPORT.md), [Fast receipt](reports/P904_assets/fast-summary.json), [standalone checks](reports/P904_assets/isolation-summary.json), [showcase receipts](reports/P904_assets/showcase-summary.json). Raw logs/TRX remain in `.scratch/p904/fast/`, `.scratch/p904/results/`, `.scratch/p904/post-results/` and `.scratch/p904/gates/` in the task worktree.

## P903 verification (2026-10-31 UTC)

One Fast sweep: **12,724 passed / 11 failed / 3 optional skips**, 12,738 executions
in 518.6 s. All nine infrastructure checks passed. Four passing executions exceeded
the calibrated 10 s Fast limit. The failed sweep was not rerun.

Every failing method and timing offender was repeated three times in isolation:

| Test / group | Isolation result | Classification |
|---|---|---|
| `P761StartChatComposeTests.ProfileEditor_OwnsEnterAndBackspace_AndKeepsChatClosed` | fail / fail / fail (read-only user profile); **pass / pass / pass** with scratch `XDG_CONFIG_HOME` | Existing sandbox profile obstruction, as in P901/P899/P898. Profile/UI code unchanged. |
| Five `PlayerBundlePathsTests` cases: executable Artwork fallback, missing Artwork/NativeSource, local-config precedence, native-source subdirectory | Same five cases fail in each of three method runs with worktree `TMPDIR`; whole class **9/9 passes ×3** with a `/proc` directory-descriptor alias to the same physical scratch storage | Existing fixture topology obstruction: ancestor lookup discovers the checkout's `local.env`. The alias restores the intended outside-checkout pathname while all writes remain inside the authorized worktree. Resolver/fixtures unchanged. |
| `P780PodRescueProbeTests.Probe_rescue_pass_allocates_nothing` | **3/3 pass**, 0.073 / 0.067 / 0.094 s | Sweep measured 3,488 bytes against zero; isolated allocation check succeeds. Rescue/simulation code unchanged. |
| `DeadLatchRespawnLiveTests.Remote_client_sees_victim_again_after_death_and_respawn` | **3/3 pass**, 1.432 / 1.453 / 1.429 s | Existing death-receipt timing intermittency, also recorded under P899/P896/P892c; death/respawn code unchanged. |
| `AsteroidMinedLiveTests.No_broadcast_fires_once_miner_hits_capacity` | **3/3 pass**, 1.768 / 1.767 / 1.743 s | Sweep receipt/timing failure; mining/broadcast code unchanged. |
| `LiveLifepodEnduranceDisplayTests.Driver_sees_countdown_others_do_not_and_dock_cancels` | **3/3 pass**, 1.604 / 1.619 / 1.563 s | Sweep receipt/timing failure; lifepod/endurance/HUD behavior unchanged. |
| `NpcWingmanDefendLiveTests.Defender_disengages_when_chased_enemy_flees_and_returns_to_charge` | **fail / fail / fail**, 1.215 / 1.239 / 1.170 s | Persistent pre-existing null `DefendTarget` at line 179, identical to the open P901/P899/P898 baseline issue. No wingman steering change; remains open. |
| `P800HullProofGpuTests.Every_hull_of_the_real_core_renders_in_hangar_flight_and_target_pane` | **3/3 pass**, 3.738 / 3.853 / 3.720 s | Sweep passed at 10.978 s; timing offender only. |
| `P814GunnerTests.P814b_TurretCarrier_pref_keeps_or_beats_the_Best_hulls_turret_seats` | **3/3 pass**, 3.341 / 3.210 / 3.220 s | Sweep passed at 11.264 s; timing offender only. |
| `SC2NaniteSupportTests.Support_selection_obeys_research_dock_budget_and_mounts` | Both civ cases pass **3/3**; all six case durations ≤4.216 s | Sweep civ 25 passed at 10.979 s; timing offender only. |
| `P777WarpExitWobbleTests.Npc_exit_touch_rate_is_measured` | **3/3 pass**, 9.922 / 9.807 / 9.716 s | Sweep passed at 10.934 s; timing offender only. |

The new P903 server replay/result regression passed three baseline-setting runs,
the new Python audit tests passed **3/3 ×3**, and the existing P817 (43 cases),
P870 text-core (29 cases), two P872 replay/context tests and ballot-discard test
all passed three isolated runs. The new regression failed before the fix because
boot left `HasGenerated=false`; that deliberate red receipt is not a flake.

Compact receipts: [P903 isolated checks](reports/P903_assets/isolated-summary.json),
[failure/timing controls](reports/P903_assets/qualification-summary.json).
Raw Fast/isolation receipts: `.scratch/p903/fast/`, `.scratch/p903/isolated/` and
`.scratch/p903/qualification/`. The serial showcase never-parks gate passed **5/5** (335.425 / 247.538 / 347.380 / 236.239 / 214.849 s), compared with the recent P901 baseline's 5/5; every run observed real combat and zero sim-pass stage failures.
[Showcase metrics](reports/P903_assets/showcase-summary.json) include all five load snapshots.

## P901 verification (2026-10-31 UTC)

One Fast sweep: **12,691 passed / 11 failed / 3 optional skips**, 12,705 executions
in 544.3 s. All nine infrastructure checks passed. Two passing executions exceeded
the calibrated 10 s Fast limit. The full sweep was not rerun.

One task-related assertion expected Career to remain the last menu item. P901
appends Host LAN while preserving Career at slot 10; that assertion now pins slot
10. The complete affected intro/layout/career selection passes **30/30, three
isolated runs**. This was a corrected menu-test assumption, not a flake.

All remaining failing methods and timing offenders were repeated three times:

| Test / group | Isolation result | Classification |
|---|---|---|
| Five `PlayerBundlePathsTests` cases: executable Artwork fallback, missing Artwork/NativeSource, local-config precedence, native-source subdirectory | Same five fail in each of 3 runs with worktree-local `TMPDIR`; all **9/9 pass in each of 3 controls** using a `/proc` directory-descriptor alias to the same physical scratch directory | Existing fixture topology obstruction: ancestor lookup finds this checkout's `local.env`. No resolver or fixture changes; physical writes remain inside the authorized worktree. |
| `P761StartChatComposeTests.ProfileEditor_OwnsEnterAndBackspace_AndKeepsChatClosed` | **3/3 pass** with writable scratch `XDG_CONFIG_HOME` | Sweep tried to restore the read-only real user profile. Same sandbox obstruction recorded in P898/P899. |
| `SpectatorFrameLiveTests.Translates_ARealWorldAndARealKillShip` | **3/3 pass**, 1.842 / 1.853 / 1.875 s | Sweep's victim never died in 4,800 ticks; isolated gameplay succeeds. No combat/spectator translation code changed. |
| `NpcWingmanDefendLiveTests.Defender_disengages_when_chased_enemy_flees_and_returns_to_charge` | **fail / fail / fail**, 1.188 / 1.186 / 1.170 s | Persistent pre-existing null `DefendTarget` assertion at line 179; identical unmodified-HEAD evidence and 3/3 failures are already recorded under P899/P898/P895/P892c. No wingman/simulation code changed; remains open. |
| `LiveSideSelectTests.Flag_on_auto_seats_a_silent_peer_at_the_configured_30_seconds` | **fail / fail / pass** | Failed samples reached sim time 32.2 outside the fixture's [28,31] window. Existing asynchronous receipt/clock sensitivity; LAN hosts use explicit POSITIONREQ, with auto-seat disabled. No auto-seat policy change. |
| `P739TimerLiveTests.Warp_bomb_notice_matches_captured_blast_deadline_and_stays_in_mission` | **pass / fail / fail** across three complete theory runs (3 / 2 / 2 cases passed) | Failures are inclusive-upper-bound floating-point assertions: 7.0000000000000009 > 7 and 5.0000000000000009 > 5. No timer/warp-bomb source changed; remains open. |
| `P777WarpExitWobbleTests.Npc_exit_touch_rate_is_measured` | **3/3 pass**, 9.346 / 9.247 / 9.343 s | Sweep passed at 10.882 s, exceeding the Fast time budget. Timing offender only. |
| `P750NativeAsteroidTypesTests.Research_showcase_rocks_carry_native_models_and_no_rock_names` | **3/3 pass**, 1.597 / 1.600 / 1.596 s | Sweep passed at 10.624 s, exceeding the Fast time budget. Timing offender only. |

Task checks: **10/10 P901 cases ×3**, including four independent TCP/UDP guest
processes, native countdown game over/next match, safety and teardown; **29/29
affected offline-skirmish cases ×3**. Showcase never-parks: **5/5 passed**, serial,
with artwork, pool/native defaults and server GC (206.9 / 217.0 / 270.2 / 220.3 /
466.5 s). Passive load snapshots on 24 available CPUs were 3.33 / 2.39 / 2.19
(1/5/15-minute averages) during the last showcase and 2.81 / 2.61 / 2.34 early in
the final lifecycle/isolation batch; no artificial contention was introduced.

Compact receipts: [P901 validation](reports/P901_assets/validation.json).
Raw receipts: `.scratch/p901/fast/`, `.scratch/p901/results/` and
`.scratch/p901/isolation-methods.json`. The failed Fast sweep remains a failed
gate; neither its receipts nor the unrelated assertions/timeouts were relaxed.

## 2026-10-31 Claude triage: public CI without artwork

CI's Fast tier was red on 21 tests. 19 needed the mission core and did not skip: `P896` x7, `P853bHostEditUiTests` x11 (JOINED_MISSION
loads the core, so the seat never formed), `P787BShopHoverAndPickTests.A_resting_pointer...`. `P889PoolSizingTests.Calibration_entry...`
read the runner's core count and the small budget clamped the calibrated 6; now fixed at 16 cores. `P629ConfigTests.Invalid_mode_fails_boot...`
found a real order bug: a mistyped `sim.crowdingPenalty` failed only after the artwork check, now rejected first. CI's filter run locally with
artwork hidden: 7,762 tests, 0 failed. `TcpLoopbackProofTests.Logon_Handshake...(udp: True)` then timed out on CI twice, at different steps; both overlapped `CleanCloneSkipGuardTests`, whose `Parallel.ForEach` fills the pool that UDP receive completions run on, while passing runs took 0.05 s with no overlap. Not reproduced locally (90 isolated runs, 4 Core project runs on 4 cores). Wait bounds raised to 30 s. `P777WarpExitWobbleTests.Npc_exit_touch_rate_is_measured` ran 10.8 s over the 10 s limit with Codex on the host.
## P900 verification (2026-10-31 UTC)

One Fast sweep: **12,686 passed / 10 failed / 3 optional skips**, 12,699
executions in 543.3 seconds. All nine infrastructure checks passed. Five
executions exceeded the 10-second Fast limit. The Fast sweep was not rerun.
Changes are in the MCP fleet supervisor, its bot-host bookkeeping, and tests/docs;
none of the failure/timing paths below changed. Each selected method ran three
separate times after the five showcase runs, without overlapping this lane's
other tests. Receipts: `docs/reports/P900_assets/fast-summary.json` and
`docs/reports/P900_assets/isolations.json`.

| Failure / timing offender | Three standalone results | Disposition |
| --- | --- | --- |
| `PlayerBundlePathsTests`: `UsesExecutableDirectoryOutsideRepository(Artwork)`, `MissingDirectoryHasNoDefault(Artwork/NativeSource)`, `LocalConfigWinsOverBundleWithBlankEnvironment`, `NativeSourceNeverGuessesAnInstallSubdirectory` (five cases) | Each affected case fails 3/3; unaffected theory rows pass | Same existing worktree-local `TMPDIR` / checkout `local.env` discovery obstruction recorded under P899/P898. No resolver or path fixture changed. |
| `P761StartChatComposeTests.ProfileEditor_OwnsEnterAndBackspace_AndKeepsChatClosed` | fail / fail / fail; pass / pass / pass with scratch `XDG_CONFIG_HOME` | Same read-only default user-profile obstruction. No profile/UI source changed. |
| `NpcWingmanDefendLiveTests.Defender_disengages_when_chased_enemy_flees_and_returns_to_charge` | fail / fail / fail (1.210 / 1.259 / 1.259 s) | Existing null `DefendTarget` assertion at line 179, also 0/3 under P899. Remains open; no native NPC/sim logic changed. |
| `LiveSideSelectTests.Flag_on_auto_seats_a_silent_peer_at_the_configured_30_seconds` | fail / pass / fail | Existing receipt/tick boundary: sweep observed 32.5 against upper bound 31. The auto-seat implementation/fixture is unchanged. |
| `P739TimerLiveTests.Warp_bomb_notice_matches_captured_blast_deadline_and_stays_in_mission` | All delay rows pass / delay 7 fails / all delay rows pass | Same floating boundary as P899/P898: `7.0000000000000009` exceeds 7. No timer source changed. |
| `MissileLockLiveTests.MissileLock_BuildsPartialThenFull_AndPosts_Tips_OverTheWire` | fail / fail / fail (38.461 / 38.492 / 38.487 s) | Existing missing partial-tip receipt with final lock 1.000. Previously ledgered with 0/3 and intermittent results; remains open, not attributed solely to load. Also a Fast timing offender. |
| `P800HullProofGpuTests.Every_hull_of_the_real_core_renders_in_hangar_flight_and_target_pane` (passing timing offender) | pass / pass / pass (3.890 / 3.852 / 4.000 s) | Sweep passed in 11.375 s; below limit alone. Existing sweep timing sensitivity; renderer unchanged. |
| `SC2NaniteSupportTests.Support_selection_obeys_research_dock_budget_and_mounts(civ: 25)` (passing timing offender) | pass / pass / pass (3.038 / 3.000 / 3.031 s); civ 3 also passes each time | Sweep passed in 11.080 s; below limit alone. Existing selector fixture timing sensitivity. |
| `P814GunnerTests.P814b_TurretCarrier_pref_keeps_or_beats_the_Best_hulls_turret_seats` (passing timing offender) | pass / pass / pass (3.197 / 3.258 / 3.236 s) | Sweep passed in 10.965 s; below limit alone. Existing selection fixture timing sensitivity. |
| `P777WarpExitWobbleTests.Npc_exit_touch_rate_is_measured` (passing timing offender) | pass / pass / pass (9.750 / 9.884 / 9.571 s) | Sweep passed in 10.327 s; below limit alone. Same native-physics timing fixture recorded under P899 and earlier lanes. |

The single Fast result remains **FAIL**. The five serial showcase never-parks
runs pass **5/5**, matching P899's recorded base 5/5. P900's actual server-restart
proofs and final rebuild/repetition receipts are recorded in `docs/reports/P900_REPORT.md`.

## 2026-10-31 Claude triage: final tree sweep `c6c0d51e` (P897, P898, P899)

9 failures, `P682b` and `P807` passed. `MissileLock`, `NpcWingmanDefend`, `LiveSideSelect`, `SC4`, `P798`, `P796Coverage`
and `P745` are the known set. New to this sweep:

| test | sweep result | isolated, master `ff075743` | verdict |
| --- | --- | --- | --- |
| `TurretServerClockLiveTests.Turret_gunner_updates_stamped_from_the_ping_clock_are_accepted_and_fire` | no turret projectiles (13 s), first time in 14 mini sweeps | 3/3 pass | Load flake, watch. |
| `P699bPodLoadTests.Hundred_pods_per_side_return_from_Alderan_with_relaunch_traffic` | one of 200 pods ran out of oxygen | 2/3 pass | Historical P699b classification; resolved by P958 seeded fixture/native-controller correction (see current entry), not a broadcast failure. |

## 2026-10-31 Claude triage: batch sweep `5ebf01c4` (P897, P898)

18 failures. `P682b` x4 and `P745` were already fixed on master. `SC4`, `P798`, `P796Coverage`, `MissileLock`,
`NpcWingmanDefend` and `LiveSideSelect` are ledgered above and below. The rest:

| test | sweep result | isolated, master `2d462d72` | verdict |
| --- | --- | --- | --- |
| `P692EquipmentLiveTests.Held_dispenser...(kind: "chaff")` | line 47, chaff gone from the server sector | 9/13 failed; 0/20 at `102f8940`, 7/20 at `cc7781a7`, bisect first bad `76c6355e` (P887 pool transport changed delivery timing) | **Test bug, fixed.** A docked ship's clock stops, so the test deployed at ship time 30.06 with the mission at 32.30 and the 2 s chaff had already expired; one tick before the client read removed it. Now steps from the mission clock, class 30/30. |
| `P807GameOverHandoffTests.P807_394_peer_handoff...` | phase slice 158.5 ms over 135.9 ms | 6/8 passed; one failure was declare sim work 160.9 ms over its scaled 150 ms budget, a different budget from the sweep | Timing flake on single max samples. Sweep commit predates P899, so the broadcast lock is not the cause. |
| `StationCombatLiveTests.Lethal_hit...`, `Shield_regenerates...` | shot never landed (24.6 s, 18.6 s) | 3/3 pass; passed in the `c6c0d51e` Fast tier | Load, first time in 14 mini sweeps. |
| `KillShipLiveTests.VictimShipEntity_MarkedDead...` | victim never died in 4800 ticks | passed in every isolated run; passed in the `c6c0d51e` Fast tier | Load. |
| `P877MineDamageLiveTests.Chased_bot...`, `NpcWingmanAttackLiveTests.Wingman_engages...` | join timeout; target null | 3/3 pass; passed in the `c6c0d51e` Fast tier | Known load flakes. |

## P899 verification (2026-10-31 UTC)

One Fast sweep: **12,686 passed / 10 failed / 3 optional skips**, 12,699
executions in 527.8 s. All nine infrastructure checks passed. Two executions
exceeded the 10 s Fast limit. The full sweep was not rerun. Raw receipts were
moved from the runner's default `TestResults/` destination into
`.scratch/p899/fast-run/`; compact receipts are in
`docs/reports/P899_assets/fast-summary.json` and
`docs/reports/P899_assets/isolations.json`. Isolations ran serially after the
sweep and before showcase; none overlapped this lane's showcase workload.

| Failure / timing offender | Three standalone results | Checked cause / disposition |
|---|---|---|
| `P761StartChatComposeTests.ProfileEditor_OwnsEnterAndBackspace_AndKeepsChatClosed` | fail / fail / fail with the original environment; pass / pass / pass with scratch `XDG_CONFIG_HOME` (0.044 / 0.053 / 0.053 s) | Existing sandbox obstruction: the user profile is read-only. No profile/UI source changed. Same signature and writable-root control as P898. |
| `PlayerBundlePathsTests`: `UsesExecutableDirectoryOutsideRepository(Artwork)`, `MissingDirectoryHasNoDefault(Artwork/NativeSource)`, `LocalConfigWinsOverBundleWithBlankEnvironment`, `NativeSourceNeverGuessesAnInstallSubdirectory` (five cases) | Each affected case fails 3/3; unaffected theory rows pass | Existing fixture topology obstruction: required worktree-local `TMPDIR` puts the fake executable under the real checkout, so `ExternalPaths.FindRepoRoot` finds the checkout's `local.env`. Same five cases as P898; resolver/fixtures unchanged. P898 records an outside-checkout container-mount control. |
| `FullSessionIntegrationTests.FullSession_Join_Ready_Loadout_Launch_Movement_Chat_Compose` | pass / pass / pass (1.751 / 1.794 / 1.758 s) | Existing chat receipt-order signature: expected ChatTarget -2, actual 0 at line 229. No chat logic changed; 335 broadcast/transport tests also pass. |
| `P877MineDamageLiveTests.Chased_bot_wake_mine_damages_pursuer` | pass / pass / pass (1.692 / 1.636 / 1.620 s) | Sweep's loopback join condition timed out at line 116 (21.293 s, also a timing offender). Existing join/timing intermittency; no mine or bot logic changed. CPU load alone is not established as the trigger. |
| `P739TimerLiveTests.Warp_bomb_notice_matches_captured_blast_deadline_and_stays_in_mission(delay: 7)` | All three delay rows pass in each of three isolated method runs | Existing floating boundary: sweep observed `7.0000000000000009` against upper bound 7, at line 81. Timer code unchanged. |
| `NpcWingmanDefendLiveTests.Defender_disengages_when_chased_enemy_flees_and_returns_to_charge` | fail / fail / fail (1.203 / 1.253 / 1.192 s) | Long-recorded baseline null `DefendTarget` assertion at line 179, including unmodified-HEAD evidence in earlier lanes and P898. No wingman/simulation logic changed; failure remains open. |
| `P777WarpExitWobbleTests.Npc_exit_touch_rate_is_measured` (passing timing offender) | pass / pass / pass (9.433 / 9.615 / 9.737 s) | Passed sweep in 10.401 s; below the 10 s limit alone. Existing native-physics fixture timing sensitivity, no warp code changed. |

The single Fast result remains **FAIL**. P899's five new containment tests pass
3/3 standalone runs; the 200-client measurement and snapshot reuse checks pass
3/3 per server-GC variant, and the full broadcast/transport filter passes
335/335. The five serial showcase never-parks repetitions also pass **5/5**,
matching the base lane's recorded 5/5; receipts are in
`docs/reports/P899_assets/showcase.json`.

## P898 verification (2026-10-30)

One Fast run on base `b4a4721a` plus the default-off TCP batching experiment:
**11,903 passed / 10 failed / 3 optional skips**, 11,916 executions in 535.8 s.
All nine infrastructure checks passed. Three executions exceeded the 10 s Fast
limit. The complete sweep was not rerun. Receipts:
`.scratch/p898/fast/summary.json` and `.scratch/p898/tests/`; compact isolation
receipts are included with `docs/reports/P898_REPORT.md`.

| Failure / timing offender | Three standalone results | Checked cause / disposition |
|---|---|---|
| `P761StartChatComposeTests.ProfileEditor_OwnsEnterAndBackspace_AndKeepsChatClosed` | fail/fail/fail with original environment; pass/pass/pass with scratch `XDG_CONFIG_HOME` (0.065 s each) | Filesystem sandbox makes the real `~/.config/AllegianceCore/player.json` read-only. The unchanged test writes/restores it at line 237. This is an environment obstruction, not a transport failure. No product/profile code changed. |
| `PlayerBundlePathsTests`: `UsesExecutableDirectoryOutsideRepository(Artwork)`, `MissingDirectoryHasNoDefault(Artwork/NativeSource)`, `LocalConfigWinsOverBundleWithBlankEnvironment`, `NativeSourceNeverGuessesAnInstallSubdirectory` (five cases) | all five fail 3/3 with worktree-local `TMPDIR`; all nine class cases pass 3/3 when the same scratch storage is mounted outside `/work` in a local container | Lane rules place temporary fixtures under the real checkout. `ExternalPaths.FindRepoRoot` walks from the fake executable directory into the actual checkout and reads its `local.env`, defeating the tests' outside-repository premise. The container bind keeps physical scratch storage in the worktree while restoring the intended path topology. No resolver/test code changed. |
| `BotFixRegressionTests.P896_A_kill_needs_observed_then_lost_or_a_destroyed_broadcast` | pass/pass/pass (0.193/0.192/0.177 s) | Sweep saw null `KilledEvidence` instead of `observed-lost` at `P896FinaleEvidenceTests.cs:244`. Unchanged bot/model fixture; specific shared-state or scheduling trigger not established. |
| `MissileLockLiveTests.MissileLock_BuildsPartialThenFull_AndPosts_Tips_OverTheWire` | pass/pass/pass (1.797/1.736/1.844 s) | Sweep timed out after 38.179 s with full lock but without the partial tip. Also a Fast timing offender. No missile/lock/tip code changed; receipt timing intermittency. |
| `P590PodRescueTests.Proactive_assignment_diverts_nearest_idle_diverter_and_rescues` | pass/pass/pass (0.901/1.762/0.866 s) | Sweep's diverter failed to rescue before its tick budget; unchanged rescue/Goto logic. No claim that CPU load alone is the cause. |
| `NpcWingmanDefendLiveTests.Defender_disengages_when_chased_enemy_flees_and_returns_to_charge` | fail/fail/fail (0.830/0.805/0.809 s) | Same null `DefendTarget` failure previously reproduced on unmodified HEAD and recorded under P892c/P892d/P895. TCP batching defaults off and no wingman/simulation logic changed; existing failure remains open. |
| `P777WarpExitWobbleTests.Npc_exit_touch_rate_is_measured` (passing timing offender) | pass/pass/pass (9.863/9.882/9.675 s) | Sweep passed in 10.510 s, just over its 10 s limit. Existing slow native-physics fixture; no warp code changed. |
| `P750NativeAsteroidTypesTests.Research_showcase_rocks_carry_native_models_and_no_rock_names` (passing timing offender) | pass/pass/pass (1.628/1.634/1.586 s) | Sweep passed in 10.570 s. Unchanged artwork/native-map fixture; passing outside the concurrent sweep. |

The final broad transport filter (including existing Perf-tagged cases) had
**216 passed / 1 failed** out of 217. The earlier 215-case pass is also retained.
`ConsoleVerbosityLiveTests.Plain_WriteLine_on_the_sim_path_does_not_block_a_stalled_stdout`
failed its immediate `HeavyShipsUpdates.Count > 0` receipt assertion at line 283
in 0.170 s, then passed **3/3 alone** (0.450/0.429/0.433 s). This exact assertion
is already ledgered in earlier transport/integration sweeps; it does not wait for
asynchronous receipts after its tight 300-tick loop. No stdout or broadcast
behavior changed in P898. Receipts: `transport-complete.trx` and
`stdout-isolation-{1,2,3}.trx` under `.scratch/p898/tests/`. This is a separate
transport-filter observation, not an extra failure in the one Fast sweep above.

The measured TCP sweep and the required showcase repetitions also ran on this
shared host. Load averages accompany every performance run in the P898 JSON;
no synthetic CPU load was used. These observations do not reclassify persistent
failures or erase the failed Fast result.

## 2026-10-30 Claude triage: batch sweep `73b375bc`, merged master `c395ed6a`

| Test | Evidence | Verdict |
| --- | --- | --- |
| `P801StrikeMassLiveTests.Fogged_showcase_masses_strikes_and_at_least_one_station_dies` | Alternating on one host: `a57fc22b` (before P896) 4/5 pass, `c395ed6a` 2/5 pass, all failures at the `kills` assert after 600 s. No failed run reached the finale trigger: side 1 never observed an enemy station (one base, two merged runs) or kills stayed 0 so maturity never passed (one merged run). No `finale holding` line in any run. Passed 8/8 on the runner before this. | Intermittent fog-discovery stall, also before P896. The rate difference is not significant at n=5; recheck on the runner. |
| `SC3CloakLiveTests.Research_showcase_cloaks_on_both_sides_and_bomber_decloaks_to_strike` | Sweep fail, then fail / pass / pass alone on `a57fc22b`. | Intermittent, same 15 min budget failure. |
| `SC5CloakLiveTests.Rig_civs_at_research_eight_use_real_cloaks_on_both_sides` | Sweep fail, 3/3 pass alone. Also failed in the 10-28 nightly. | Load or budget intermittent. |
| `P832dArtifactBotLiveTests.Artifacts_preset_searches_collects_returns_and_wins_under_fog` | Sweep fail (Brawl, 2 captures), 3/3 pass alone. Also failed in the 10-29 nightly. | Load or budget intermittent. |
| `ShipStatusServerTests.PeerReceivesItsOwnShipStatus_InJoinBurst` | Fast fail on `c395ed6a`, then fail / fail / pass alone. No Core or Server file changed in P896. | Intermittent alone, needs its own look. |
| `P877MineDamageLiveTests.Chased_bot_wake_mine_damages_pursuer` | Fast fail, 3/3 pass alone. | Load flake. |
| `NpcWingmanDefendLiveTests.Defender_switches_to_closer_threat_near_charge_while_chasing_distant_one` | Fast fail, 3/3 pass alone. | Load flake. |

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
Raw logs/TRX remain ignored in `<worktree>/.p748-evidence/`;
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
| `DiscoveryTests.Broadcast_discovery_collects_a_loopback_responder` | Same family: the 700 ms LAN collect window heard no loopback broadcast reply on a loaded GitHub runner (public CI run 42688466867, `Assert.NotEmpty` on an empty list; passed on the previous runs). Window widened to 3000 ms for the test only; production window unchanged. |
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
| `P699bPodLoadTests.Hundred_pods_per_side_return_from_Alderan_with_relaunch_traffic` | One of 200 pods exhausted oxygen; 199 docked and all 200 reached home. Resolved by P958 (current entry). | `P708_assets/suite-retry-001-{1,2,3}.trx.gz`: 3/3 pass |
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
| `Allegiance.Core.Tests.P699bPodLoadTests.Hundred_pods_per_side_return_from_Alderan_with_relaunch_traffic` | 199/200 pods docked and one oxygen expiry; `Assert.Equal` expected zero expiries, actual one (line 160). Resolved by P958 (current entry). | 3/3 pass |
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

- `Allegiance.ClientCore.Tests.ClientWarpApproachSlowHullLiveTests.Legacy_200_450_arrival_parks_slow_board_hull_26_outside_the_transit_volume` — Assert.Equal() Failure: Values differ Expected: 1053 Actual: 1052 at Allegiance.ClientCore.Tests.ClientWarpApproachSlowHullLiveTests.Legacy_200_450_arrival_parks_slow_board_hull_26_outside_the_transit_volume() in <worktree>/tests/Allegiance.ClientCore.Tests/ClientWarpApproachS. Post-suite isolation: **3/3 passed**.
- `Allegiance.Core.Tests.Transport.ConsoleVerbosityLiveTests.Plain_WriteLine_on_the_sim_path_does_not_block_a_stalled_stdout` — peers must keep receiving per-tick updates while stdout is stalled at Allegiance.Core.Tests.Transport.ConsoleVerbosityLiveTests.Plain_WriteLine_on_the_sim_path_does_not_block_a_stalled_stdout() in <worktree>/tests/Allegiance.Core.Tests/Transport/ConsoleVerbosityLiveTests.cs:li. Post-suite isolation: **3/3 passed**.

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
| 2026-10-28 | P699bPodLoadTests.Hundred_pods_per_side_return_from_Alderan_with_relaunch_traffic | P866 full-project sweep (Allegiance.Core.Tests, load ~3–5, four lanes): one pod oxygen expiry vs the expected zero (same signature as the 2026-10-19/P708 entries above) | 3/3 alone clean | Historical load-flake classification superseded by P958: seeded fixture and complete native controls; see current resolution above. |
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

### P897 Codex gate — 2026-10-30

One Fast sweep (`tiers-20261030T230601.826574Z`): **12,679 executions,
12,662 pass / 14 fail / 3 optional skips**, plus one 10 s timing offender.
Raw results were moved into the gitignored `.scratch/p897/fast/` after the run.
The sweep was not repeated. Host load averages sampled during the work were
6.74 / 4.89 / 3.03 and 11.17 / 5.96 / 3.52; no synthetic load was created.

Two task-owned source assertions (`P836HudLevelTests.The_options_panel_carries_the_level_row_and_case`
and `Hud19CentreHudTests.Graphics_overlay_has_the_joystick_row_and_it_toggles`)
still expected 21 Settings rows after adding the transport row. Updated to 22;
both pass **3/3** in isolation, alongside the unchanged HUD input test.
These were fixed assertions, not classified as flakes.

| Non-task failure / timing observation | Three isolated results | Classification |
|---|---|---|
| `P761StartChatComposeTests.ProfileEditor_OwnsEnterAndBackspace_AndKeepsChatClosed` | pass / pass / pass with writable task-local `XDG_CONFIG_HOME` | Sandbox environment: the sweep tried writing the read-only user `player.json`; profile/chat code unchanged. |
| Five `PlayerBundlePathsTests` executions (`UsesExecutableDirectoryOutsideRepository` Artwork, `MissingDirectoryHasNoDefault` Artwork/NativeSource, `NativeSourceNeverGuessesAnInstallSubdirectory`, `LocalConfigWinsOverBundleWithBlankEnvironment`) | fail / fail / fail with worktree-local TMPDIR; all nine class cases pass / pass / pass via a `/proc/<parent>/fd/3` logical alias to that same NVMe scratch directory | Existing fixture environment assumption: an allegedly outside-repository install inherits ancestor `local.env`. No files written outside the worktree, and no resolver/assertion changed. |
| `DeadLatchRespawnLiveTests.Remote_client_sees_victim_again_after_death_and_respawn` | pass / pass / pass | Existing loopback timing flake: sweep missed the victim's death latch after 2,500 ticks. |
| `FullSessionIntegrationTests.FullSession_Join_Ready_Loadout_Launch_Movement_Chat_Compose` | pass / fail / pass | Existing intermittent chat-receipt ordering: ChatTarget 0 rather than -2, same assertion in isolation. No chat code changed. |
| `P813cReconnectGraceTests.Grace_expiry_runs_the_native_removal` | pass / pass / pass | Missing removal receipt under sweep load; all existing UDP/reconnect suites also passed 46/46 three times. |
| `P739TimerLiveTests.Warp_bomb_notice_matches_captured_blast_deadline_and_stays_in_mission` | all three delay cases pass / pass / pass | Existing floating boundary: delay 5 observed `5.0000000000000009` against upper bound 5. |
| `LiveSideSelectTests.Flag_on_auto_seats_a_silent_peer_at_the_configured_30_seconds` | pass / pass / pass | Existing receipt/simulation-clock timing flake: sweep saw 32.1 seconds against 28–31. |
| `NpcWingmanDefendLiveTests.Defender_disengages_when_chased_enemy_flees_and_returns_to_charge` | fail / fail / fail | Previously ledgered deterministic null target at line 179; NPC defend/flight code untouched. Remains open. |
| `P777WarpExitWobbleTests.Npc_exit_touch_rate_is_measured` | pass / pass / pass at 9.742 / 9.738 / 9.719 s | Passed in the sweep but took 10.597 s, exceeding the calibrated 10 s Fast limit. Existing timing class. |

The 774 new P897 tests pass **3/3**, including actual discovery/session TCP and
UDP, blocked UDP fallback within the handshake bound, TCP/UDP reconnect with
changed preferences, default TCP bots/sessions, and both HUD styles' persisted
Settings input. No existing transport assertions or timing bounds were changed.

P897's required showcase never-parks repetitions passed **5/5**. The full
training runner (`--no-build --repeat 3`, Fast + Live with artwork) passed
**80/80 ClientCore + 67/67 MonoGame** in each repetition, with no skipped
lessons. Raw logs/TRX are retained in `.scratch/p897/`; compact results are in
[the P897 validation artifact](reports/P897_assets/validation.json).

## Batch P896 sweep triage (2026-10-31)

| Test | Isolated result | Disposition |
|---|---|---|
| `P682NextMatchCombatLiveTests.P682b_…` (4 cases) | 4/4 after the fixture fix | Deterministic since P896: a side targets only stations its own worlds hold, and `ShowMap` reveals warps only, so side 0 armed with no target in match 2. Test now seeds station visibility on the live mission. |
| `P745FinaleSiegeLiveTests.Research_showcase_finale_destroys_a_station` | Station killed in 4 of 9 runs, 1 of 3 with the fixture fix | Still unreliable, same `No station destruction` at a kills deadline as the earlier rows. Fixed one test bug: it ranked `mission.Sides` after retirement cleared them (`Mission.Lifetime.cs`), throwing `IndexOutOfRangeException` after a real kill. Failure counts include load from a parallel lane. |
| `NpcAutopilotLiveTests.Damaged_npc_take_cover_now_produces_observable_movement` | 3/3 | Load flake, new to this ledger. |
| `P778ABomberPushLiveTests.Staged_finale_pushers_contact_the_bomber_and_do_not_slow_the_strike` | 3/3 | Sweep miss 24.1 s vs 23.4 s control. |
| `P796CoverageLiveTests.Loopback_showcase_with_varied_loadouts_builds_weapon_coverage` | 3/3 | Sweep saw 62% fired. |

## P902 Codex gate — 2026-10-31

The single Fast sweep (`tiers-20261031T143154.448549Z`) again hit the existing
sandbox/profile-path failure in
`P761StartChatComposeTests.ProfileEditor_OwnsEnterAndBackspace_AndKeepsChatClosed`.
Its fixture finally-block restores the default user `player.json` on a read-only
configuration directory. This is unrelated to the audio-template/quality work.
Three isolated runs with `XDG_CONFIG_HOME` pointing to `.scratch/p902/config`
passed **3/3** (61 / 76 / 74 ms). No profile/chat test or handler was changed.
Evidence: `.scratch/p902/profile-isolation-{1,2,3}.log` and
`.scratch/p902/isolated/p761-profile-{1,2,3}.trx`; retain the original Fast result.

Additional P902 Fast observations, with each failed method isolated three times:

| Test | Three isolation outcomes | Observation |
|---|---|---|
| `P890OwnAimLoopbackTests.Turning_and_firing_preserve_client_aim(training: False, model: "port")` | pass / fail / pass (all four theory cases run each time; the other three pass throughout) | Same skirmish/port sustained-aim assertion as the sweep. The failure includes large forward/shot drift. The P902 changes are in the Game/audio layer, which this client-core test does not execute; no prediction, server, sim or test code changed. |
| `MissileLockLiveTests.MissileLock_BuildsPartialThenFull_AndPosts_Tips_OverTheWire` | pass / fail / pass | Existing intermittent missing partial-lock tip, despite final lock 1.000. The failed isolation runs for 38 s; earlier gates already ledger this method. No wire/lock code changed. |
| `NpcWingmanDefendLiveTests.Defender_disengages_when_chased_enemy_flees_and_returns_to_charge` | fail / fail / fail | Existing persistent baseline failure: expected charge ship, actual null at line 179. Previous ledger entries already reproduce it on unmodified HEAD; P902 changes no sim or NPC code. |

Evidence: `.scratch/p902/{aim,missile,wingman}-isolation-{1,2,3}.log` and
`.scratch/p902/isolated/`. These outcomes are retained without changing the
unrelated fixtures or parity rows. Host load sampled during triage was
10.10 / 5.93 / 3.91 on 24 logical CPUs; this was not a synthetic-load experiment.

The same P902 sweep also flagged the already-passing
`P777WarpExitWobbleTests.Npc_exit_touch_rate_is_measured` for its calibrated Fast
execution time (10.278 s versus a 10.000 s limit). Three isolated runs **pass
3/3** at **10.077 / 10.114 / 10.071 s**; the timing classification remains over
the limit. Evidence: `.scratch/p902/warp-isolation-{1,2,3}.log` and
`.scratch/p902/isolated/p777-warp-{1,2,3}.trx`. No warp/sim/test-tier code changed.
The complete sweep ran 12,737 cases in 544.3 s: 12,730 passed, 4 failed,
3 skipped, and 2 timing offenders. All P902 cases passed in the sweep.


## P905 Codex gate — 2026-10-31

The single Fast sweep retained the existing/non-task failures below in its
original results under `.scratch/p905/fast/`. Their source files were unchanged.

| Test | Isolation result (three separate invocations) | Classification |
|---|---|---|
| `P761StartChatComposeTests.ProfileEditor_OwnsEnterAndBackspace_AndKeepsChatClosed` | pass / pass / pass with writable `.scratch/p905/config` as `XDG_CONFIG_HOME` | Existing sandbox/config-path failure: the fixture finally-block restores the user profile on a read-only path. No profile or chat code changed. Original sweep failure retained. |
| `ThreePeerSeededSessionTests.SeedThreePeers_TwoSameSide_OneCrossSide_DeterministicVisibility` | pass / pass / pass | Existing seeded-join/lobby timing failure; sweep expected side 1 but saw lobby side -2. The isolated runs use the required native/pooled runtime settings and server GC. No server, transport, client-world or seed fixture code changed. |

Additional Core isolation receipts (three separate invocations each):

| Test | Isolation outcomes | Observation |
|---|---|---|
| `P817NativeGotoPlanTests.Production_adapter_allocates_no_bytes_and_excludes_player_pilots` | pass / pass / pass | Sweep measured 4,272 bytes against zero; isolated allocation assertions pass. No Core adapter or allocation code changed. |
| `NpcWingmanDefendLiveTests.Defender_disengages_when_chased_enemy_flees_and_returns_to_charge` | fail / fail / fail | Same persistent null-defend-target baseline failure recorded under P872/P887/P895/P902. Remains open; no sim/NPC code changed. |
| `LiveCombatWireTests.Moving_target_streams_its_falling_hull_over_the_wire` | pass / pass / pass | Sweep saw neither hull drop nor kill; isolated real-wire runs pass. No projectile, wire or client prediction code changed. |
| `P777WarpExitWobbleTests.Npc_exit_touch_rate_is_measured` | functional pass / pass / pass; 10.270 / 10.009 / 10.271 s | Sweep passed functionally in 11.265 s but exceeded the calibrated 10 s Fast limit. Existing near-boundary timing offender; no warp/flight code or timing threshold changed. |

Receipts: `.scratch/p905/profile-isolation-summary.json`,
`.scratch/p905/supervisor-and-seed-summary.json`,
`.scratch/p905/core-isolation-summary.json` and `.scratch/p905/isolation-results/`.

The sweep also exposed P905's tool-count contract assertion (13 expected, 14
actual). That task regression was corrected for the added `bot_behavior` tool;
the updated count test and the new named-worker routing test each passed three
times alone. It is not classified as a flake. The Fast sweep was not repeated.


## Mini sweep `batch-p900-p901` — 2026-10-31

Snapshot of `4c25346f` without media. Fast 12,702 tests, Live 232, Perf 73 (pass).

| Test | Result | Classification |
|---|---|---|
| `P900FleetRecoveryLiveTests` (11 tests) | Failed in 1-15 ms starting `~/.dotnet/dotnet` in the runner container | Test harness path. Now uses `DOTNET_HOST_PATH`; 2 sampled tests pass 3/3 on the workstation, the runner has not re-run them. |
| `P799LivesLimitLiveTests.Ghost_only_side_is_eliminated_and_last_side_wins` | `Sides.First` threw after game over | Test race with post-game rotation. The side state is now sampled while pumping; 3/3 alone. |
| `P798ScoutOpeningLiveTests.Fogged_showcase_scouts_open_the_map_and_the_clash_forms_on_resources` | `Assert.Single` saw clusters 1054 and 1055 | Known open, see the persistent entry above. |
| `SC3CloakLiveTests`, `SC4ShowcaseLiveTests`, `P745FinaleSiegeLiveTests` | Long showcase assertions failed | Known open, see earlier entries. |
| `NpcWingmanDefendLiveTests.Defender_disengages_when_chased_enemy_flees_and_returns_to_charge` | Null defend target | Known open, fails 3/3 on baseline. |
| `DockDockingTests.Ship_nowhere_near_a_station_is_unaffected` | Status parent 1051, expected -1 | Known intermittent. |

Fast budget offenders on the mini: `P796VariedLoadoutTests` (civ 35) 15.8 s, `P814GunnerTests.P814b_TurretCarrier_pref_keeps_or_beats_the_Best_hulls_turret_seats` 20.9 s, `SC2NaniteSupportTests` (civ 25) 19.8 s.

`P905BotRoutinesLiveTests.Ordered_peer_mines_only_after_dispatch_then_returns_and_sells_real_He3` failed once in 11 runs on the workstation, then passed 8/8, after the chat-cursor and `TypeOf` review fixes. The failure message was not captured and the cause is unknown. The fixture teleports the ship right after sending the dispatch, which is the first place to look.

## Mini sweep batch-p902-p905 (2026-10-31, snapshot of 84597d2c)

Perf passed. Fast and Live failed. The P900 tests and P799 no longer fail on the runner. Reruns on merged
master `b2e8b196`:

| Method / family | Rerun | Finding |
|---|---|---|
| `MusicLoadingTests`, `Hud12bReviewFixTests.HUD_formatted_readouts_allocate_nothing_in_steady_state` | 3 × pass | Sweep-only. |
| `MissionRotationLiveTests.Finished_...`, `P817NativeMinerDecisionCycleLiveTests`, `LiveSuicideShipDeleteTests`, `ConsoleLogNonBlockingTests` | 3 × pass | Sweep-only timing or shared console state, as ledgered before. |
| `P682NextMatchCombatLiveTests.Mcp_bots_buy...`, `P730ShowcaseLiveTests.Two_local...` | 3 × pass | Sweep-only on the mini. Cause of the P730 `natural mining` miss not established. |
| `MissileLockLiveTests`, `SC2SupportPhaseLiveTests`, `SC5CloakLiveTests`, `P832dArtifactBotLiveTests`, `SC4`, `P745`, `P798`, `NpcWingmanDefendLiveTests`, `DockDockingTests` | Not rerun | Seen in earlier sweeps or ledgered; remain open. |

## P909 — Fast sweep, 2026-10-31

The one required Fast sweep ran with local artwork. Unrelated failures below
were isolated three times; training task tests passed separately. Raw sweep and
isolation evidence is in `.scratch/p909/fast/` and `.scratch/p909/tests/`.

| Test | Three-run isolation | Classification |
|---|---|---|
| `P761StartChatComposeTests.ProfileEditor_OwnsEnterAndBackspace_AndKeepsChatClosed` | fail / fail / fail at the default read-only profile; pass / pass / pass with task-local writable `XDG_CONFIG_HOME` | Existing sandbox profile-restoration failure in the fixture finally block. Original failure retained; no profile implementation changed. |
| `DeadLatchRespawnLiveTests.Remote_client_sees_victim_again_after_death_and_respawn` | pass / pass / pass | Existing loopback timing flake: sweep missed the death latch after 2,500 ticks. P909's server changes are guarded by LocalTraining; this test uses the multiplayer path. |
| `P739TimerLiveTests.Warp_bomb_notice_matches_captured_blast_deadline_and_stays_in_mission` | all three delay cases pass / pass / pass | Existing floating boundary: sweep delay 7 observed `7.0000000000000009` against upper bound 7. Core timer code is unchanged. |
| `NpcWingmanDefendLiveTests.Defender_disengages_when_chased_enemy_flees_and_returns_to_charge` | fail / fail / fail | Existing deterministic baseline: null defend target at line 179, already ledgered under P872/P887/P895/P902/P905. Core NPC code is unchanged; remains open. |
| `NpcAsteroidContentionLiveTests.Two_same_side_idle_miners_lower_ore_keeps_rock_higher_ore_redirected_to_unload` | pass / pass / pass | Existing load-sensitive miner progress case: sweep ore stayed at 10, and 16.747 s exceeded the 10 s Fast budget. |
| `DockDockingTests.Ship_nowhere_near_a_station_is_unaffected` | pass / pass / fail | Sweep included a station-bearing ShipStatus for another ship in its all-status assertion; tested docking code is unchanged. |
| `P777WarpExitWobbleTests.Npc_exit_touch_rate_is_measured` | pass / pass / pass | Sweep passed but took 10.644 s, above its calibrated 10 s Fast budget; isolation evidence is `slow-isolation-{1,2,3}.trx`. No warp sim code changed. |
| `SideEliminationLiveTests.Eliminating_side2_sends_side_only_gameover_to_it_alone` | pass / pass / pass | Quarantine sweep failed its lethal restart-station hit after 20.765 s; runner retry passed, then the three isolation runs passed. Also a Fast timing overrun. No elimination code changed. |

## Mini sweep `batch-p906-p907` (snapshot of `d2d1aed5`, P906 and P907 included)

Perf passed; Fast (12,799) and Live (236) failed. `Ordered_peer_mines_only_after_dispatch` did not recur. Six Fast
tests overran the 10 second limit: SC2 nanite, P814, P796, `MissileLock`, P777, and P750 (new to the budget list).

| Test | Isolation | Reading |
|---|---|---|
| `P900FleetRecoveryLiveTests.Skirmish_shaped_exit_disables_recovery_and_reaps_workers` | fail / pass / fail on P909 base, Expected 5, Actual 6 at `Proxy.Attempts` | **New, not load-only, cause not established.** One extra connection attempt appears between `KillServer` and driver exit. Ruled out: recovery (`recovery: false`), reconnect grace (off by default), the worker probe (never re-dials), initial spawn (ends before `ready`). Unproven lead: showcase refill. |
| `MusicLoading`, `Hud12bReviewFix`, `ConsoleLogNonBlocking`, `ThreePeerSeededSession`, `LiveSideSelect`, `DockDocking`, `NpcWingmanDefend`, `MissileLock`, `SC2SupportPhase`, `SC4`, `P745`, `P798` | not rerun this batch | Already ledgered above. |

## P910 Codex gates — 2026-11-01

The final Fast sweep ran 12,863 cases in 544.284 s: **12,852 passed,
8 failed, 3 skipped**, plus two calibrated 10 s timing overruns. P910's
64 new cases pass three isolated invocations, and the complete artwork
training gate passes all three repetitions (675 passed, zero skipped).
No unrelated test, resolver, docking, missile, NPC or timing-threshold code
was changed to hide the failures below.

Each method below ran in **three separate method-filtered invocations** with
native ship updates, pooled transport, parallel broadcast construction,
native GotoPlan and server GC. Evidence:
`.scratch/p910/final-validation/fast/`,
`.scratch/p910/final-validation/isolations/summary.json` and the adjacent
`case-<method-index>-<run>.trx` / `.log` files. The summary records full method
names and all theory-case outcomes.

| Test / family | Three-run isolation | Finding |
|---|---|---|
| `PlayerBundlePathsTests`: `UsesExecutableDirectoryOutsideRepository` (Artwork), `MissingDirectoryHasNoDefault` (Artwork / NativeSource), `NativeSourceNeverGuessesAnInstallSubdirectory`, `LocalConfigWinsOverBundleWithBlankEnvironment` | Each of the five failing cases fails 3/3; the other theory cases pass | Scratch-path harness constraint, not load-only. Lane rules place `TMPDIR` below the checkout. These fixtures assume their temporary base directory is outside any repository; `ExternalPaths.FindRepoRoot` therefore finds the real ancestor repository before considering the synthetic current-directory context. Real artwork/native config shadows their expected empty/bundled results; the synthetic sky config loses to the executable bundle. Resolver and fixtures are unchanged. Original failures retained. |
| `MissileLockLiveTests.MissileLock_BuildsPartialThenFull_AndPosts_Tips_OverTheWire` | fail / fail / fail; 38.356 / 38.325 / 38.335 s | Previously ledgered missile-lock test remains failing here: full lock reaches 1.000 but the partial-tip edge is absent. Also a Fast budget offender (38.308 s in the sweep). This is not classified as a load flake. P910 chat changes require local training delegates, which are null in this normal multiplayer fixture; missile lock/feedback code is unchanged. Remains open. |
| `DockDockingTests.Ship_nowhere_near_a_station_is_unaffected` | fail / fail / fail | Previously ledgered all-status assertion includes another ship (8098) docked at station 1051; it is not scoped to the trainee/player. Reproduces under the baseline settings here; docking/status source is unchanged. Not classified as load-only. Remains open. |
| `NpcWingmanDefendLiveTests.Defender_disengages_when_chased_enemy_flees_and_returns_to_charge` | fail / fail / fail | Same persistent null defend-target failure already recorded under P872/P887/P895/P902/P905/P909. Core NPC code is unchanged. Remains open. |
| `P777WarpExitWobbleTests.Npc_exit_touch_rate_is_measured` | functional pass / pass / pass; 9.823 / 9.932 / 9.674 s | Sweep passed functionally at 10.524 s, above its calibrated 10 s limit. Existing near-boundary timing offender; no warp or timing-threshold changes. |

Two unintended superseded Fast sweeps also completed after Ctrl-C failed to
stop child scripts. This exceeded the requested ONE sweep. Shared old paths
reliably retain only the last superseded sweep (10 failed cases, two timing
offenders); original outer logs and that TRX remain under `.scratch/p910/`.
All remaining task children were stopped before the final evidence run. The
following failures occurred only in that retained superseded sweep and were
also isolated three times:

| Test | Isolation | Finding |
|---|---|---|
| `AsteroidCombatLiveTests.Lethal_hit_kills_asteroid_removes_from_mineable_and_broadcasts_destroy_to_whole_mission` | pass / pass / pass | Superseded sweep missed the lethal cross-side hit and exceeded the Fast budget (17.622 s). Final sweep passes. Asteroid/collision code is unchanged. |
| `MissionRotationLiveTests.Finished_mission_rotates_to_fresh_notstarted_and_new_joiner_starts` | pass / pass / pass | Superseded sweep missed the new joiner's expected message; final sweep passes. Rotation source is unchanged. |
| `P907SchedulingMetricsTests.Production_server_logs_metrics_for_both_host_profiles_only_when_enabled` | all four profile/flag cases pass / pass / pass | Superseded sweep saw Address already in use; final sweep passes. This occurred while unintended test children overlapped, so no independent root cause or host-load-only claim is made. |
| `ShipStatusServerTests.PeerReceivesItsOwnShipStatus_InJoinBurst` | pass / pass / pass | Superseded sweep observed 2 statuses against expected 3; final sweep passes. Join/status source is unchanged. |

The persistent PlayerBundlePaths and NPC failures and the P777 timing issue
also occurred in the retained superseded sweep. These outcomes do not replace
the final Fast failures, and none of the interrupted training wrappers is
counted as a passing training gate. Full workflow details are in
`docs/reports/P910_REPORT.md`.

Claude's review of P910: `MissileLock` is intermittent with and without P910 (alternated runs: 3 of 3 failed on the
P909 tree, 2 of 3 failed on P910, mostly while other tests ran), and `DockDocking` also fails on the P909 tree. Neither is a P910
regression.

## Mini sweep `batch-p908-p909` — 2026-11-01

Snapshot `25861102`. Perf 73 pass; Fast 12,825 FAIL; Live 237 FAIL. No failure is new to this ledger. None of the Live
failures below was isolated this batch, so this sweep does not clear P908 or P909 of them.

| Test | Result | Finding |
|---|---|---|
| `ShopLauncherMountNumberingLiveTests.ShopBuyOrder_LauncherGrantedAtMount0_LocksAndFires_Live` | sweep fail, pass 3/3 alone | Already ledgered intermittent. |
| `MusicLoadingTests.Missing_beds_stay_silent_even_when_music_is_enabled_and_ship_docks` | sweep fail, pass 3/3 alone | Already ledgered as sweep-only. |
| `LiveSideSelectTests.Flag_on_auto_seats_a_silent_peer_at_the_configured_30_seconds` | 31.1 s against a 31 s limit, pass 3/3 alone | Already ledgered timing margin. |
| `MissileLockLiveTests`, `NpcWingmanDefendLiveTests` | sweep fail | See the P910 section. |
| `P900FleetRecoveryLiveTests.Skirmish_shaped_exit_disables_recovery_and_reaps_workers` | Expected 5, Actual 6 again | Second sweep in a row. Still open, cause not established. |
| `SC2SupportPhase`, `SC4`, `P745`, `P798` | sweep fail, not rerun | Already ledgered showcase outcomes. |

## P911 Codex gates — 2026-11-01

The single Fast-tier sweep retained its failures; isolated controls do not turn
it into a green sweep. Full results and remaining arena parity gaps are in
`docs/reports/P911_REPORT.md`. Source for chat/profile behavior was unchanged.

| Method | Three isolated invocations | Classification |
|---|---|---|
| `P761StartChatComposeTests.StartChatEdge_OpensCompose_WhileNothingModalOwnsTheKeys` | pass / pass / pass | Sweep-only compose assertion at line 86; isolated method keeps its original environment. No chat/profile implementation change. |
| `P761StartChatComposeTests.ProfileEditor_OwnsEnterAndBackspace_AndKeepsChatClosed` | fail / fail / fail at the original default profile; pass / pass / pass with writable task `XDG_CONFIG_HOME` | Already-ledgered sandbox obstruction: fixture restoration at line 237 cannot write the default user profile. The writable-root control uses the same test and source. |

Raw method-filtered results are retained under `.scratch/p911/isolation-*`.
The sweep finished with **12,859 passed, 11 failed, 3 existing optional skips**,
all nine infrastructure checks passing, and no timing offenders. Quarantine
cases passed and no quarantine flakes were reported. The additional failures
received separate method-filtered invocations:

| Method / cases | Three isolated invocations | Classification |
|---|---|---|
| `PlayerBundlePathsTests.UsesExecutableDirectoryOutsideRepository` (Artwork case) | fail / fail / fail (the SkyPack case passes) | Mandated in-checkout `TMPDIR` lets ancestor `local.env`/config discovery win over the simulated bundle. Same five-case obstruction as P909/P910. |
| `PlayerBundlePathsTests.MissingDirectoryHasNoDefault` (Artwork and NativeSource cases) | fail / fail / fail for both (the SkyPack case passes) | Same in-checkout ancestor-config discovery. |
| `PlayerBundlePathsTests.LocalConfigWinsOverBundleWithBlankEnvironment` | fail / fail / fail | Same ancestor-config obstruction. |
| `PlayerBundlePathsTests.NativeSourceNeverGuessesAnInstallSubdirectory` | fail / fail / fail | Same ancestor-config obstruction. |
| `SwapPartTests.SwapPart_swaps_two_occupied_slots_sectorwide_moves_and_silent_noop` | pass / pass / pass | Sweep saw a late prior `SWAP_PART` during the silent-noop assertion at line 270; isolated delivery controls pass. No swap/transport source changed. |
| `NpcWingmanDefendLiveTests.Defender_disengages_when_chased_enemy_flees_and_returns_to_charge` | fail / fail / fail | Existing baseline null defend target at line 179, documented before P911. Ordinary defend behavior remains unchanged; new buoy resolution is gated to training. |
| `MissileBroadcastTests.Directly_fired_missile_broadcasts_create_to_teammate_and_destroy_to_whole_mission` | pass / pass / pass | Sweep terminal position was 1756.13 m from launch rather than 1848.56 m. The earlier-destruction signature was already ledgered; no missile code changed. |
| `BallotVotingLiveTests.Resign_passing_deactivates_the_side_and_ends_the_mission` | pass / pass / pass | Sweep-only resignation timing/state assertion. No ballot/economy/server-lifecycle source changed. |

There was exactly one actual Fast sweep (`tiers-20261101T063655.304067Z`). An
older training wrapper continued after an interrupt; a temporary launch guard
prevented its queued duplicate Fast command, and was restored before handoff.
The superseded development training run's two arena-leak regressions were fixed
by restricting native arena order handling to Missions 6/10, then verified three
times in isolation and by the complete training gate. Superseded results do not
count as final green gates. The retained Fast sweep remains **failed**.


## P912 qualification (2026-11-01)

The single baseline Fast sweep recorded 12,863 passed, 13 failed, 3 skipped
(the optional Meshy pack cases), plus four calibrated timing violations.
Artwork-dependent P912 tests did not skip. Raw sweep: `.scratch/p912/fast-tier/`;
three-run isolation: `.scratch/p912/fast-isolation/summary.json`. Isolation
load average (one minute) ranged 2.39–3.91; no synthetic load was used.

The task-owned P708 private-field reflection reference was updated after the
combat slot generalized from probes to static models. All 42 P708 tests then
passed in each of three isolated repeats; this is a corrected task test, not a
new flake. Final training idle/accepted-order, camera/chat/status and proof-pilot changes
were checked after the sweep in targeted cohorts and the completed first training pass;
the repeat gate was interrupted as recorded below. The sweep was not repeated.

| Non-task failure / timing check | Three isolated repeats | Disposition |
|---|---|---|
| Five `PlayerBundlePathsTests` executions: Artwork executable-directory fallback, Artwork/NativeSource missing-directory defaults, native-source subdirectory guessing, and synthetic sky config precedence | Each fails 3/3; the other four class cases pass each run | Existing fixture assumption: mandated in-checkout `TMPDIR` makes ancestor `local.env` visible. Same P909/P910 obstruction; resolver and assertions unchanged. |
| `TcpTransportSendWorkerPoolTests.Explicit_thread_per_peer_config_boot_stays_thread_per_peer` and `Boot_line_prints_the_active_transport_mode` | Both fail 3/3 with the mandated pool override; both pass 3/3 when the explicit compatibility test runs with transport/build overrides unset | Baseline environment overrides the test's requested legacy transport. Only these isolation controls remove the two overrides; ordinary qualification keeps pool/parallel/native settings. |
| `P889PoolSizingTests.Explicit_config_still_wins_over_the_resolver_at_boot` | Fails 3/3 with baseline overrides; passes 3/3 with the explicit compatibility overrides unset | Same explicit-config/environment conflict; no sizing code or assertion changed. |
| `NpcWingmanDefendLiveTests.Defender_disengages_when_chased_enemy_flees_and_returns_to_charge` | Fails 3/3 with expected charge / actual null | Existing persistent defend-target failure, already reproduced before P912 under P872/P887/P895/P902/P905/P909/P910/P911. P912's new station-target branch does not execute this ship-only defend case. Remains open. |
| `AsteroidMinedLiveTests.No_broadcast_fires_once_miner_hits_capacity` | Pass / pass / pass | Sweep-only queued ore update; original sweep received one `AsteroidMined` packet after capacity. |
| `BotAcceptsAndEngagesOrderLiveTests.Mcp_debug_bot_buys_relaunches_and_fights(killPod: True, botCount: 3)` | Both method data cases pass in each of three repeats | Sweep-only empty selection during the pod lifecycle. |
| `Hud12bReviewFixTests.HUD_formatted_readouts_allocate_nothing_in_steady_state` | Pass / pass / pass | Existing allocation timing family: sweep measured 2,480 B over 10,000 frames. |
| `P800HullProofGpuTests.Every_hull_of_the_real_core_renders_in_hangar_flight_and_target_pane` | Pass at 5.38 / 5.45 / 5.16 s process wall time | Sweep passed but exceeded the calibrated ten-second limit at 10.045 s. |
| `SC2NaniteSupportTests.Support_selection_obeys_research_dock_budget_and_mounts(civ: 25)` | Both civ cases pass each repeat; process wall time 8.36 / 8.51 / 8.48 s | Sweep passed but took 10.549 s. |
| `P814GunnerTests.P814b_TurretCarrier_pref_keeps_or_beats_the_Best_hulls_turret_seats` | Pass at 4.43 / 4.36 / 4.43 s process wall time | Sweep passed but took 11.198 s. |
| `P777WarpExitWobbleTests.Npc_exit_touch_rate_is_measured` | Assertion passes each repeat; process wall time 11.68 / 11.42 / 11.54 s | Still above the Fast limit in isolation. Existing test duration/tier limitation remains; no timing assertion, tier, or warp rule was changed. |

P912 targeted-cohort follow-up: one `P873TrainingJoinTests.Logon_replicates_the_native_trainee_before_the_real_update_starts_script(id: 3)` execution reached `Mission3.CreateMission` without a replicated player pose (`ClientTrainingSite.PlayerShip`). All 13 join cases passed in each of three fresh isolated processes, with zero skips (`.scratch/p912/join-isolation/{1,2,3}/result.trx`). A second occurrence during the expanded P912 cohort exposed a task-relevant startup race: readiness and complete goal construction read different replicated-world snapshots. P912 now holds the session gate across both operations, with a regression that asserts guarded reads during readiness and SetupTraining. The resume reused the failed cohort result path; the original failing TRX was overwritten. Its observed stack/count are transcribed in `.scratch/p912/join-isolation/startup-race-observation.txt`; the three earlier isolation TRX files remain. Future retries archive prior directories. The final expanded cohort verifies the correction.

P912 native warp-handoff follow-up: the 390-case affected warp cohort hit the existing `P739TimerLiveTests.Warp_bomb_notice_matches_captured_blast_deadline_and_stays_in_mission` floating-boundary family (delay 7: `7.0000000000000009`). Three exact-class processes returned fail (delays 5/7 at one ULP above the integer upper bound), pass (5/5), fail (one boundary case); see `.scratch/p912/timer-isolation/{1,2,3}/result.trx`. Timer/missile scheduling code and its assertion are unchanged. The final affected warp repeats explicitly exclude this ledgered class after preserving the failing initial cohort in `.scratch/p912/warp-initial-fail/`; its separate isolation remains part of qualification.

P912 stopped training gate: `.scratch/p912/training-20261101T151134-14550/` completed pass 1 (ClientCore 95/95, MonoGame 208/208, zero skips), then pass 2 ClientCore 95/95. Claude stopped pass 2 MonoGame at about 54 windows with one failure; pass 3 never ran. The failing `m8-Modern-early-656863533dc742cb8f85c4db4a7e2c02/run.log:186,191` records a non-greet first frame and LOGONACK timeout before lesson startup. The merged `TcpTransport.Join` fix queues the greeting before publishing `IsConnected` (`fb040bb5`, regression `TcpClientGreetOrderTests`). After Debug and Release rebuilds, the exact M8 Modern early window passed three isolated invocations with zero skips (`.scratch/p912/resume-final/early-{1,2,3}/result.trx`). This is a corrected transport race, not a waived lesson failure. The full three-pass gate remains incomplete; Claude runs it after handoff.
### Sweep batch-p910 (snapshot 1503d0f4, mini, 2026-11-01)

Perf 73 pass; Fast 12,860 FAIL; Live 266 FAIL. Rerun alone on master `9816dfa4`, Debug, 3 of 3 each:

| Test | Sweep symptom | Alone |
| --- | --- | --- |
| `P761bCommandRelayTests.AcceptedOrder_OnOnePlayer_ReachesTheTeammate_WithItsCommandText` | `attack` instead of `attack relayB` | pass / pass / pass |
| `P764UiSoundSeamTests.TeamPaneSelectionCycle_PlaysThePlayerListClickCue` | no `mouseclick` recorded | pass / pass / pass |
| `P902SoundTemplateTests.Asr_and_cache_hit_mix_math_allocate_nothing_after_warmup` | 1,496 bytes, expected 0 | pass / pass / pass |
| `KillBonusLiveTests.Real_end_to_end_projectile_kill_increases_kill_bonus` | no KILL_SHIP within budget | pass / pass / pass |
| `MissileTrackWarningLiveTests.Warning_routes_by_the_source_ship_for_pilot_and_gunner` | gunner not warned | pass / pass / pass |
| `MusicLoadingTests.Missing_beds_stay_silent_even_when_music_is_enabled_and_ship_docks` | empty load list | pass / pass / pass |
| `LiveSideSelectTests.Flag_on_auto_seats_a_silent_peer_at_the_configured_30_seconds` | 31.65 s, limit 31 | pass / pass / pass |

Causes not established; all fail only under the full parallel sweep. `P900FleetRecoveryLiveTests.Skirmish_shaped_exit_disables_recovery_and_reaps_workers`
passed this sweep after failing the previous two, so it is intermittent, cause unknown. `DockDocking`, `MissileLock`, `NpcWingmanDefend`,
`SC2SupportPhase`, `SC4`, `P745` and `P798` also failed; not rerun here, not cleared for P910.

## 2026-11-01: batch-p911 sweep on the runner

Snapshot `a63dd6fc`, master at the P911 merge without media. Fast FAIL (12,872 tests), Live FAIL (273 tests), Perf PASS (73 tests).
Compared with batch-p910, `P761b`, `P764`, `P902SoundTemplate`, `KillBonusLive` and `MissileTrackWarning` passed this sweep. Two
failures are new, rerun 3× alone on master plus the greet fix below:

| Test | Sweep failure | Alone |
| --- | --- | --- |
| `P877MineDamageLiveTests.Chased_bot_wake_mine_damages_pursuer` | timed out at line 116 waiting for the bot's own loadout (the helper's message says "join") | pass / pass / pass |
| `DirectNpcBuildOrderLiveTests.Commander_directs_builder_drone_to_build_creating_real_station` | no building effect at line 212 | pass / pass / pass |

Causes not established; both fail only under the full parallel sweep. `MusicLoading`, `LiveSideSelect`, `DockDocking`, `MissileLock`,
`NpcWingmanDefend`, `SC2SupportPhase`, `SC4`, `P745` and `P798` failed again; not rerun here. The sweep logs contain no `non-greet` drop.

## 2026-11-01: client data frame could overtake the TCP greeting (fixed)

P912's training gate lost one window (`m8-Modern-early`) with `peer sent non-greet frame` and `timed out waiting for LOGONACK`.
`TcpTransport.Join` set `IsConnected` and published `_clientSender` before queuing the greeting, and the session keep-alive thread runs
before Connect, so a ping in that gap went first and the server closed the connection. `TcpClientGreetOrderTests` reproduced it on the
unfixed code (5 of 2,000 joins with a thread sending as soon as `IsConnected` is true) and passes after the fix. That the keep-alive
thread was the sender in the failed window is inferred: it is the only concurrent sender found, and the window log does not name it.

## 2026-11-01: P912 M8 unforced windows can lose the bomber (proof check corrected)

Claude's `tools/run-training-tests.sh --repeat 3` after P912: ClientCore 95/95 three times; MonoGame 206, 208 and 207 of 208. All three
failures were `repair escort did not destroy the outpost` (`m8-Classic-default` once, M8 Modern lesson 3 twice). In 3 of 12 windows not
forced to lose, the bomber died and the outpost survived. That is a native ending and the traced window reached it with the native prompt.
The proof now accepts either ending on unforced runs; `early`, `loss` and `sacrifice` still pin their branch. All 11 M8 cases then passed
once (not 3×). `early` is not forced to win and has won every observed run, so it may flake the same way.

## P916 texture-orientation gates — 2026-11-01

One actual Fast sweep: **12,933 passed / 13 failed / 3 skipped**, nine
infrastructure checks passed, three timing violations. The sweep remains
**FAIL**. Its two task-owned HD synthetic-source failures were fixed by accepting
short non-MDL source streams without an orientation reflection; both complete
HD test classes and the new guards then passed in the final **125-case cohort
three times**, with zero skips. They are corrected regressions, not flakes.
The three optional skips were the existing private `MeshyPackTests`.

Every non-task failed method/theory family and timing offender was run through
three isolated method filters. These results do not turn the sweep green.
Raw evidence: `.scratch/p916/evidence/isolation/`; compact results:
`docs/reports/P916_assets/isolation-summary.json`. The measured one-minute load
ranged **2.51–4.02**; no synthetic load was used. No server/sim/wire logic or
chat/profile behavior changed.

| Non-task failure or timing check | Three isolated invocations | Disposition |
|---|---|---|
| Five `PlayerBundlePathsTests` executions: Artwork executable fallback, Artwork/NativeSource missing defaults, native subdirectory guessing, and sky config precedence | Each failing case fails 3/3; companion SkyPack cases pass 3/3 | Existing in-checkout `TMPDIR` obstruction: ancestor `local.env` is visible to the simulated bundle. Same fixture assumption as P909–P912. |
| `P761StartChatComposeTests.HeldEnter_DoesNotReFire` | pass / pass / pass | Sweep-only chat-state assertion; cause not established. Chat source unchanged. |
| `P761StartChatComposeTests.ProfileEditor_OwnsEnterAndBackspace_AndKeepsChatClosed` | fail / fail / fail with default profile; pass / pass / pass with writable task `XDG_CONFIG_HOME` | Existing sandbox obstruction: the fixture's `finally` restoration at line 237 attempts to write the read-only default player profile. |
| `LiveSideSelectTests.Flag_on_auto_seats_a_silent_peer_at_the_configured_30_seconds` | fail / pass / pass | Existing intermittent timeout boundary: sweep measured 31.10 s against the 31 s maximum; first isolation measured 32.15 s. Remains intermittent. |
| `NpcWingmanDefendLiveTests.Defender_disengages_when_chased_enemy_flees_and_returns_to_charge` | fail / fail / fail | Existing persistent expected-charge / actual-null defend-target assertion, already ledgered before P916. |
| `ShipStatusServerTests.PeerReceivesItsOwnShipStatus_InJoinBurst` | fail / fail / pass | Intermittent join-burst state: sweep expected 3, got 2. First two isolated invocations also fail; no transport or join logic changed. |
| `SideEliminationLiveTests.Destroying_last_restart_station_eliminates_side_and_declares_winner` | pass / pass / pass; 0.504 / 0.538 / 0.540 s | Sweep's lethal-hit assertion timed out at 20.926 s, also exceeding the Fast limit. Not reproduced in isolation. |
| `P800HullProofGpuTests.Every_hull_of_the_real_core_renders_in_hangar_flight_and_target_pane` | pass at 3.632 / 4.021 / 3.952 s | Sweep assertion passed but took 10.299 s, above the calibrated 10 s limit. |
| `P777WarpExitWobbleTests.Npc_exit_touch_rate_is_measured` | pass at 9.724 / 9.525 / 9.705 s | Sweep assertion passed but took 10.721 s. Existing near-limit timing family; no tier or warp rule changed. |

## 2026-11-01: batch-p912 sweep on the runner

Snapshot `ee2d6635`, master at the P912 merge without media, before `99c2f5cf`. Fast FAIL (12,886 tests), Live FAIL (319 tests), Perf PASS (73 tests).
All 31 `P912TrainingWindowTests` failed to start: the test looked for `dotnet` under the home directory, which the runner lacks. Fixed in `99c2f5cf`;
whether windows run in the runner container is still unproven. Rerun 3× alone on master:

| Test | Sweep failure | Alone |
| --- | --- | --- |
| `LiveRipcordTests.Ship_anchor_reaching_zero_relocates_near_target_and_pays_energy` | anchor distance 68.453, expected 37 | pass / pass / pass |
| `WarpBombTests.Live_whole_mission_warp_bomb_reaches_the_cross_side_peer_and_the_delayed_blast_lands` | empty collection | pass / pass / pass |
| `AsteroidCombatLiveTests.Cross_side_missile_hits_asteroid_with_exact_computed_damage_and_it_survives` | missile did not hit | pass / pass / pass |
| `NaniteThreatBonusLiveTests.Threat_target_earns_2x_repair_credit_over_wire` | credit 0.000 | pass / pass / pass |
| `P699bPodLoadTests.Hundred_pods_per_side_return_from_Alderan_with_relaunch_traffic` | 24 oxygen expiries, expected 0 | fail / fail / fail |

P699b is intermittent, not a P912 regression. Alternated alone over 13 runs: 10 passes before P912 (`fb040bb5`), 8 on master. Both trees show the same
all-or-part outcome; the worst run, 43 expiries, was on the pre-P912 tree. Pods reached home and then ran out of oxygen queued near the station.
Historical note, superseded by P958: the bimodal loss is reproduced by seeded
file-map rocks obstructing the long return leg while the fixture omits native
Dodge/diversion. P958 pins the worst old seed and retains all strict assertions;
see the current resolution and report above. `SC3Cloak`, `SC2SupportPhase`, `P745`, `P798`, `MissileLock`, `NpcWingmanDefend`, `MusicLoading` and
`LiveSideSelect` also failed; not rerun here, not cleared.

## P917 training-branch gates — 2026-11-02

One actual Fast sweep: **12,940 passed / 13 failed / 3 skipped**, nine
infrastructure checks passed, five timing violations. The sweep remains
**FAIL**. The optional skips are the existing private `MeshyPackTests`.
All task-owned Fast regressions passed. Every non-task failed method/family
and timing offender received three isolated filtered invocations.
Raw receipts: `.scratch/p917/isolation/`; compact results:
`docs/reports/P917_assets/isolation-summary.json` and `fast-summary.json`.
Isolation one-minute load ranged **3.58–9.59** on the shared host; no synthetic
load was used. Window qualifications and some showcase runs overlapped in
separate processes. The fixed sweep timing limit was **10.3753 seconds**.

| Non-task failure or timing check | Three isolated invocations | Disposition |
| --- | --- | --- |
| Five `PlayerBundlePathsTests` cases: Artwork executable fallback, Artwork/NativeSource missing defaults, native subdirectory guessing, synthetic sky precedence | Each fails 3/3; the other four class cases pass 3/3 | Existing in-checkout `TMPDIR` assumption: ancestor `local.env` is visible. Same P909–P916 obstruction; resolver and fixtures unchanged. |
| `P889PoolSizingTests.Explicit_config_still_wins_over_the_resolver_at_boot` | fail / fail / fail with forced baseline environment; pass / pass / pass with those two environment overrides unset | Harness conflict: `ALLEGIANCE_BROADCAST_PARALLEL_BUILD=1` / `ALLEGIANCE_TRANSPORT_MODE=pool` override the fixture's explicitly serial, thread-per-peer configuration. Control keeps that legacy path pinned in the existing fixture. No product configuration rule changed. |
| `TcpTransportSendWorkerPoolTests.Explicit_thread_per_peer_config_boot_stays_thread_per_peer` and `Boot_line_prints_the_active_transport_mode` | Both fail 3/3 under the forced pool environment; both pass 3/3 with the environment overrides unset | Same explicit legacy-config versus forced environment conflict. Assertions and transport code unchanged. |
| `P761bCommandRelayTests.QueuedStationGoto_KeepsOwnTarget_AndRelaysTheStationToTheTeammate` | pass / pass / pass | Previously ledgered bare `goto` versus `goto Garrison` receipt intermittency. Cause not established here; relay code unchanged. |
| `P590PodRescueTests.Diverter_intercepts_and_rescues_a_moving_pod` | pass / pass / pass | Existing rescue tick-budget family; sweep did not intercept within 800 ticks. Rescue/Goto code unchanged. |
| `P739TimerLiveTests.Warp_bomb_notice_matches_captured_blast_deadline_and_stays_in_mission` | fail / pass / pass across all three delay cases | Existing one-ULP integer deadline boundary; one isolation also reproduced it. Timer and missile scheduling unchanged. |
| `NpcWingmanDefendLiveTests.Defender_disengages_when_chased_enemy_flees_and_returns_to_charge` | fail / fail / fail | Existing expected-charge / actual-null defend-target assertion. No defend/rescue change in this lane. |
| `DockDockingTests.Ship_nowhere_near_a_station_is_unaffected` | pass / pass / fail | Existing all-status history assertion includes another ship docked at station 1051. Docking/status code unchanged. |
| `P800HullProofGpuTests.Every_hull_of_the_real_core_renders_in_hangar_flight_and_target_pane` | pass at 4.527 / 4.467 / 4.746 s | Sweep passed at 10.683 s, above the calibrated limit; isolated timings stay below it. |
| `P814GunnerTests.P814b_TurretCarrier_pref_keeps_or_beats_the_Best_hulls_turret_seats` | pass at 4.214 / 4.172 / 4.220 s | Sweep passed at 12.287 s; isolated timings stay below the limit. |
| `SC2NaniteSupportTests.Support_selection_obeys_research_dock_budget_and_mounts` | Both civ cases pass each run; all case times 3.178–5.170 s | Sweep civ 25 passed at 11.386 s; isolated timings stay below the limit. |
| `P777WarpExitWobbleTests.Npc_exit_touch_rate_is_measured` | Assertions pass at 10.254 / 10.472 / 10.724 s | Two isolated executions still exceed 10.3753 s. Timing remains open; no tier or assertion changed. |
| `NpcDodgeLiveTests.Traveling_npc_does_not_dodge_an_enemy_side_ship_on_its_path` | pass at 8.099 / 8.141 / 8.184 s | Sweep passed at 10.435 s; isolated timings stay below the limit. |

These isolations do not turn the original Fast sweep green. The server's
normal multiplayer station-elimination control separately passed 3/3, and
the five baseline showcase invocations passed 5/5, zero skips.
## 2026-11-02: batch-p916 sweep on the runner

Snapshot `719fe7aa`, master at `d7c7dc45` without media. Fast FAIL (12,950 tests), Live FAIL (325 tests), Perf PASS (73 tests). Rerun 3× alone on `57190c43`:

| Test | Sweep failure | Alone |
| --- | --- | --- |
| `P832fKillBotLiveTests.Kills_preset_wins_by_native_kill_rule_while_miners_and_research_continue` | Stale test: P914 appends the preset station tally to the reason | Fixed, first line compared; pass / pass / pass |
| `P784ComposeTests.Typing_bang_on_a_fresh_compose_switches_to_the_shell_state` | compose state NotComposing | class pass / pass / pass |
| `P909TrainingBindingsTests.Key_gate_commander_messages_quick_chat_ack_and_eliminated_target_use_real_client(Classic)` | 990 expected, -1 actual | class pass / pass / pass |
| `MusicLoadingTests.Missing_beds_stay_silent_even_when_music_is_enabled_and_ship_docks` | empty load list | pass / pass / pass |
| `P900FleetRecoveryLiveTests.Skirmish_shaped_exit_disables_recovery_and_reaps_workers` | Expected 5, Actual 6 at `Proxy.Attempts` | fail / fail / pass; still open, cause not established |

The P784, P909 and MusicLoading failures are sweep-only, cause not established. `P832d`, `P745`, `P796`, `P798`, `SC4`, `P699b` (1 oxygen expiry, other case),
`NpcWingmanDefend`, `DockDocking` and `ConsoleLogNonBlocking` failed too; seen in earlier sweeps, not rerun here, not cleared. Windows skip on the runner
(no usable GL device), so it never covers training windows; only the workstation gate does.

## P921 offline LOD bridge gates — 2026-11-02

One Fast-tier run completed: **12,964 passed / 12 failed / 3 skipped**,
**9 infrastructure checks passed**, **2 timing violations**. The Fast sweep
remains **FAIL**. All 105 affected offline model-tool tests passed in each of
three isolated cohorts with zero skips; the production-renderer LOD/pixel test
passed 3/3 with zero skips. The three Fast skips are the existing optional
private `MeshyPackTests`, not artwork skips.

Every non-task failure method/theory family and timing offender was run three
times through an exact method filter. Raw local evidence is under
`.scratch/p921/isolation/`; compact public results are in
`docs/reports/P921_assets/validation-summary.json`. Isolation's one-minute
load ranged **0.93–2.79**. No synthetic load was generated. P921 changes only
the offline model bridge, its tests/docs and a local rendering proof; none of
the paths below were changed.

| Non-task failure / timing check | Three isolated invocations | Disposition |
|---|---|---|
| Five failing `PlayerBundlePathsTests` cases: Artwork executable fallback, Artwork/NativeSource missing defaults, native subdirectory guessing, local config precedence | Each failing case fails 3/3; companion cases pass | Existing in-worktree `TMPDIR` fixture obstruction: ancestor `local.env` becomes visible to the simulated bundle. Same family as P916. |
| `P761StartChatComposeTests.HeldEnter_DoesNotReFire` | pass / pass / pass | Sweep-only chat-state assertion; cause not established. Also observed in P916. |
| `P761StartChatComposeTests.ProfileEditor_OwnsEnterAndBackspace_AndKeepsChatClosed` | fail / fail / fail with default profile; pass / pass / pass with writable task `XDG_CONFIG_HOME` | Existing sandbox obstruction: fixture cleanup tries to restore a read-only default player profile. |
| `LiveCombatWireTests.Real_two_party_combat_produces_a_kill_ship_over_the_wire` | pass at 1.510 / 1.466 / 1.487 s | Sweep timed out waiting for the shooter's decoded kill credit at 33.197 s, also above the Fast limit. Cause not established; not reproduced in isolation. |
| `LiveSideSelectTests.Flag_on_auto_seats_a_silent_peer_at_the_configured_30_seconds` | pass / fail / pass | Existing intermittent timeout boundary: sweep observed 32.15 s against a 31 s maximum. |
| `NpcWingmanDefendLiveTests.Defender_disengages_when_chased_enemy_flees_and_returns_to_charge` | fail / fail / fail | Existing persistent expected-charge / actual-null defend-target assertion, ledgered before P921. |
| `P739TimerLiveTests.Warp_bomb_notice_matches_captured_blast_deadline_and_stays_in_mission` | 3/3 pass, 3/3 pass, 2/3 pass | Sweep delays 5 and 7 exceeded the integer upper bound by one float step (`5.0000000000000009`, `7.0000000000000009`). Existing P912 floating-boundary family; intermittent in isolation. |
| `P777WarpExitWobbleTests.Npc_exit_touch_rate_is_measured` | pass at 9.584 / 9.506 / 9.511 s | Sweep assertion passed at 11.192 s, above the calibrated 10 s limit. Existing near-limit timing family; no tier change. |
## 2026-11-02: P922 target-material lane

Exactly one Fast-tier run; rendering-only production changes. Isolated filters were rerun three times each with artwork. Raw evidence is retained in the lane's ignored `.scratch/p922/` (sandbox cannot write the usual external evidence store).

| Test | Fast failure | Three isolated runs |
| --- | --- | --- |
| `P761StartChatComposeTests.ProfileEditor_OwnsEnterAndBackspace_AndKeepsChatClosed` | Writes the default user `player.json` outside the writable sandbox | fail / fail / fail under the original environment; pass / pass / pass with `XDG_CONFIG_HOME` set to task-local scratch. Environment restriction, not a rendering regression. |
| `P739TimerLiveTests.Warp_bomb_notice_matches_captured_blast_deadline_and_stays_in_mission(delay: 7)` | Floating-point boundary: `7.0000000000000009` exceeds upper bound `7` | pass / pass / pass (all three delay cases in each isolated filter). Cause of sweep-only boundary exposure not established. |
| `NpcWingmanDefendLiveTests.Defender_disengages_when_chased_enemy_flees_and_returns_to_charge` | Phase-one `AttackTarget` is null at line 179 | fail / fail / fail, same assertion. Previously seen in runner sweeps; remains open, not cleared as a load flake. P922 changes no simulation/server code. |
| `LiveSideSelectTests.Flag_on_auto_seats_a_silent_peer_at_the_configured_30_seconds` | Observed 31.45 simulated seconds, allowed 28–31 | pass / pass / pass. Sweep-only; cause not established. |
| `DeadLatchRespawnLiveTests.Remote_client_sees_victim_again_after_death_and_respawn` | Observer never latches victim death after 2,500 ticks | pass / pass / pass. Sweep-only; cause not established. |

P922's two new Live GPU cases (pack present and absent, each covering 24 kind/style/mode combinations) pass 3/3 each with 0 skipped. The optional Meshy pack is unconfigured; the Fast skip count and final cohort totals are in `docs/reports/P922_REPORT.md`.

The six passing Fast cases that exceeded the calibrated duration limit were also rerun 3× individually. All passed and all isolated durations were below 10 seconds; the sweep-only overruns remain ledgered rather than changing unrelated tier metadata.

| Test | Fast seconds | Isolated seconds (three passes) |
| --- | ---: | --- |
| `NpcDodgeLiveTests.Traveling_npc_does_not_dodge_an_enemy_side_ship_on_its_path` | 10.065 | 8.650 / 8.703 / 8.654 |
| `NpcDodgeLiveTests.Traveling_npc_steers_around_a_same_side_ship_on_its_path_and_resumes` | 10.252 | 8.636 / 8.595 / 8.469 |
| `NpcDodgeLiveTests.Traveling_npc_steers_around_a_station_obstacle_and_reaches_its_own_station_goal` | 10.232 | 8.526 / 8.300 / 8.449 |
| `NpcDodgeLiveTests.Traveling_npc_steers_around_an_asteroid_on_its_path_and_resumes` | 10.078 | 8.427 / 8.427 / 8.402 |
| `P750NativeAsteroidTypesTests.Research_showcase_rocks_carry_native_models_and_no_rock_names` | 11.063 | 1.915 / 1.805 / 1.850 |
| `P777WarpExitWobbleTests.Npc_exit_touch_rate_is_measured` | 11.342 | 9.686 / 9.530 / 9.562 |

## P918 (2026-11-02) — sole Fast sweep and isolated controls

The one required Fast sweep collected 12,989 cases: 12,984 passed, two failed,
three optional artwork-pack cases skipped. Nine infrastructure cases passed.
The sweep took 572.691 seconds and returned failure; it is not recorded as a
clean gate. Raw receipts: `.scratch/p918/fast/summary.json`,
`.scratch/p918/fast-isolation/summary.json`.

| Test | Fast result | Three isolated invocations on final P918 source |
| --- | --- | --- |
| `NpcWingmanDefendLiveTests.Defender_disengages_when_chased_enemy_flees_and_returns_to_charge` | Expected charge / actual null, line 179 | fail / fail / fail, same assertion. A control using the saved dispatch Core assembly also fails 3/3 at the same line (`.scratch/p918/baseline-defend/summary.json`). Persistent baseline failure already recorded under P872 through P922; remains open. |
| `NpcWingmanAttackLiveTests.Wingman_engages_nearest_enemy_closes_distance_and_deals_damage` | Enemy fraction remains 1 after 20,000 ticks; 36.888 s, also a duration offender | pass / pass / pass, 0.715 / 0.654 / 0.663 s. Sweep-only failure, consistent with prior ledger entries; cause not established. |
| `P777WarpExitWobbleTests.Npc_exit_touch_rate_is_measured` | Pass, 10.643 s, above calibrated Fast duration limit | pass / pass / pass, 10.231 / 10.296 / 10.146 s. Functional pass with a persistent borderline timing overrun in these invocations; not cleared as a load flake. |

All isolated cases executed with zero skips, one method per test invocation.
Recorded shared-host load averages were 2.48–10.05 (one-minute average).
Training-window and showcase qualification were also active; no synthetic load
was added. No unrelated NPC behavior, fixture expectations or tier metadata
were changed. The sweep preceded P918's final training-only execution-plan
correction; final training and affected-test receipts cover that correction.

P918's earlier qualification snapshot also had one window-child exit in
`P912TrainingWindowTests.Every_lesson_completes_in_one_real_window_session`
(Mission 4, Modern, default policy), at goal 5.6 around the first warp. The
captured log ends without a managed exception. The exact parameter case passes
**3/3 isolated invocations**, zero skips (wall times 712.735 / 662.156 / 655.495 s).
Cause is not established; no M4 pilot or graphics change was made for this exit.
Raw receipts: `.scratch/p918/m4-isolation/summary.json` and the failed window
under `.scratch/p918/final-source/.scratch/p917/windows/m4-Modern-default-f75769e67cc140b58a7ce8354b70d528/`.
One-minute load during isolation ranged 1.04–1.89. The final training pass uses
the later frozen snapshot; this isolated result does not make the earlier
failed gate green.

An intermediate P918 window gate also stalled in Mission 3, Classic, at goal
9.7 after 99.1 simulation seconds; its 21-minute wrapper timeout ends the case.
The same case passes **3/3 isolated invocations**, zero skips, on the final
feature source (174.149 / 195.932 / 188.547 s wall). The fresh final gate also
passes this course. Cause remains unestablished; no Mission3 pilot or sim rule
was changed for the stall. One-minute load during isolation ranged 0.72–1.97.
Receipts: `.scratch/p918/m3-isolation/summary.json`; stalled window
`.scratch/p918/legacy-p917/windows/m3-Classic-default-a0f869cbf6a44a158228ecbd9cfc90b5/`.

A separate intermediate P918 gate timed out in Mission 4, Modern,
`Every_lesson_completes_in_one_real_window_session`, policy `departure`, at
goal 8.7 after 226.1 simulation seconds. The exact parameter case passes
**3/3 fresh filtered invocations**, zero skips, on the final source:
658.793 / 671.724 / 668.911 s wall. These three separate test processes ran
concurrently, one parameter case per invocation; one-minute host load ranged
0.55–0.76. The full v2 and final v5 training gates also pass this case.
Cause remains unestablished; no Mission4 pilot or native rule was changed for
this timeout. Receipts: `.scratch/p918/m4-departure-isolation/summary.json`;
failed window `.scratch/p918/legacy-p917/windows/m4-Modern-departure-f7d16923b69e412aa43c1d19239c18a9/`;
failed gate `.scratch/p918/training-final-v4/`.

### 2026-11-03 — P918 review gate (Claude)

`tools/run-training-tests.sh --no-build --repeat 3` on `17302b89`: repeats 1 and 2 passed in full (headless 100/100,
MonoGame 227 passed / 4 opt-in skips). Repeat 3 failed one case:

| Test | Gate result | Three isolated invocations |
| --- | --- | --- |
| `P912TrainingWindowTests.Every_lesson_completes_in_one_real_window_session(mission: 4, style: "Classic", policy: null)` | Window child exited non-zero after 4 m 22 s at goal 8.7, chasing the Enemy Scout | pass / pass / pass, 637 / 627 / 506 s, load 0.31–0.67 |

The window log ends mid-frame with no managed exception and no "exited before the requested proof completed" message,
so the process died outside managed code. The likely layer is software GL or SDL; that is not established. This is the
third Mission 4 window incident in P918 runs (Modern goal 5.6 exit and Modern goal 8.7 timeout above); none appear in
earlier ledgers. If it recurs on master, capture the child's exit code and a core dump before changing pilots or sim
rules. Receipts: `.scratch/claude-gate/` in the P918 worktree.

### 2026-11-03 — batch-p918-final mini sweep (`7d60eb11`, media-free snapshot of `3b51b7c1`)

Fast 12,988, Live 354, Perf 73 (pass). 13 failures. Two were a real P918 defect, fixed in the next commit:
`P918TrainingWindowTests.Buoy_decal_visibility_and_retirement_use_the_actual_training_window` (both styles) failed in
3 ms with "Build tools/p918-training-proof before running the window tests." The test project did not reference the
new proof tool, so a clean checkout never built it; the P918 lane passed only with a hand-built tool. With the
`ProjectReference` added, a clean build produces the tool and both cases pass 3/3 (34 s each run).

`P590PodRescueTests.Proactive_assignment_diverts_nearest_idle_diverter_and_rescues` ("proactively-assigned diverter
did not rescue the pod", 8 s) passes 3/3 alone (883 / 912 / 940 ms), as in its earlier entries.

The rest were not rerun. Each also failed in the 2026-11-03 nightly on origin `4c25346f`, which predates this batch,
or in earlier ledgered sweeps: `MusicLoading`, `ConsoleLogNonBlocking`, `NpcWingmanDefend`, `LiveSideSelect`,
`SC4Showcase`, `SC5Cloak`, `P832dArtifactBot` (Brawl), `P798ScoutOpening` (m2xspiderw), `P796Coverage` and
`P745FinaleSiege`.

## 2026-11-03 — `TcpLoopbackProofTests` moved to the LiveTiming lane (Claude)

CI fast tier failed on a real-socket UDP case in 3 of 4 runs today: `Logon_Handshake_RoundTrips_BothDirections_OverRealSocket(udp: True)`
(30 s timeout, `3ef860cd`) and `ClientHalf_RaisesOnSessionLost_AndClearsConnected_WhenServerDies(udp: True)` (`456c071b`). The class passes 7/7 locally.
The class now carries `Lane=LiveTiming`: it runs serially in the P647 quarantine lane with one isolated retry instead of blocking the fast gate.
Cause not established; same CI-only pattern as the earlier entry at the top of this file.

## P923 validation — one Fast sweep, 2026-11-03

The single Fast invocation recorded **12,998 passed, 13 failed, three skipped**
(optional private Meshy-pack cases), plus six timing offenders against a calibrated
10.1698 s limit. P923's 20 new cases and the 63-case affected cohort passed three
filtered repetitions with no skips. No full-tier rerun was made.

Eleven failures came from the invocation's inherited rig-baseline environment
forcing settings over tests that explicitly pin older behavior. Removing the
parallel-build, transport-mode, ship-update-model and native-GotoPlan overrides
for those filtered tests gives **3/3 passes per case**. Defaults and each test's
explicit config then apply. These are environment conflicts, not load flakes;
no production setting or test assertion was changed:

- `P889PoolSizingTests.Explicit_config_still_wins_over_the_resolver_at_boot`
  (serial/thread-per-peer config).
- `Transport.TcpTransportSendWorkerPoolTests.Explicit_thread_per_peer_config_boot_stays_thread_per_peer`
  and `Boot_line_prints_the_active_transport_mode`.
- Both `TeammatePositionBroadcastTests` cases (port SINGLE_SHIP_UPDATE stream).
- All three `ShipUpdateBudgetLiveTests` cases (port folded four-entry budget).
- Both `OwnShipCoalesceKeyTests` cases (port position batch).
- `LiveTurretGunnerBindingTests.Board_binds_gunner_and_active_turret_update_roundtrips_live`
  (port orientation position batch).

| Other sweep failure | Three filtered repetitions | Qualification |
|---|---|---|
| `P739TimerLiveTests.Warp_bomb_notice_matches_captured_blast_deadline_and_stays_in_mission(delay: 7)` | pass / pass / fail; all other delay cases pass each time | Same previously ledgered one-ULP deadline (`7.0000000000000009 > 7`). The unchanged dispatch-base sources pass all delay cases in three separate runs. Boundary remains intermittent; not claimed cleared. |
| `NpcWingmanDefendLiveTests.Defender_disengages_when_chased_enemy_flees_and_returns_to_charge` | fail / fail / fail at line 179, expected AttackTarget but null | **Dispatch-base reproduction: fail / fail / fail at the same assertion**, using isolated HEAD Core/Server sources (`21747c7b`). Confirmed pre-existing behavior, not classified as a load flake. |

The five functionally passing sweep timing offenders were also repeated three
times in filtered processes with baseline settings. The sixth timing offender,
`TeammatePositionBroadcastTests.Stationary_teammate_is_still_broadcast_to_observer_once`,
was the environment-conflict failure above and passes all three corrected-env
isolations.

| Timing case | Sweep seconds | Isolation seconds (all assertions pass) |
|---|---:|---|
| `P800HullProofGpuTests.Every_hull_of_the_real_core_renders_in_hangar_flight_and_target_pane` | 11.692 | 4.228 / 4.285 / 4.278 |
| `P796VariedLoadoutTests.Varied_equips_every_legal_hull_and_part_of_the_role_over_many_purchases(civ: 35)` | 13.626 | 4.729 / 4.473 / 4.137 |
| `SC2NaniteSupportTests.Support_selection_obeys_research_dock_budget_and_mounts(civ: 25)` | 16.099 | 4.259 / 4.052 / 3.711 |
| `P814GunnerTests.P814b_TurretCarrier_pref_keeps_or_beats_the_Best_hulls_turret_seats` | 16.274 | 4.558 / 4.367 / 4.065 |
| `P777WarpExitWobbleTests.Npc_exit_touch_rate_is_measured` | 11.039 | 10.194 / 10.253 / 10.267 |

The first four are sweep-only timing overruns in these invocations. P777 remains
slightly above this sweep's calibrated limit even in isolation; its existing
borderline timing/tier issue remains open. No tier, limit, timer, turret, budget
or wingman logic was changed. The forced-baseline sweep qualification is
preserved rather than presented as a green Fast gate. Host load sampled during
verification ranged from about 5.9 to 10.3 (one/five-minute averages), without
synthetic load.

See [P923 report](reports/P923_REPORT.md) and
[verification receipt](reports/P923_assets/verification.json). Raw Fast and
three-run isolation TRX/logs remain in `.scratch/p923/` in the task worktree;
the base-source reproduction is under `.scratch/p923/base-failure/`.

## P925 — home geometry audit gate (2026-11-04 UTC)

One Fast invocation with artwork/local.env: **13,005 passed, 11 failed, 3 skipped**
(13,019 total), plus three calibrated Fast timing offenders. No Core or Server
simulation/transport source changed. New P925 tests pass 3/3 runs with zero skips.
The failures below remain visible in the gate; the suite was not repeated.

| Test / group | Three isolated executions | Classification / observed failure |
|---|---|---|
| `P761StartChatComposeTests.ProfileEditor_OwnsEnterAndBackspace_AndKeepsChatClosed` | fail / fail / fail in original environment; pass / pass / pass with task-local `XDG_CONFIG_HOME` | Existing read-only default-profile restoration obstruction at line 237. |
| Five failing `PlayerBundlePathsTests` cases: Artwork executable fallback, Artwork/NativeSource missing defaults, native subdirectory guessing, local config precedence | Each fails 3/3; four companion cases pass each run | Existing mandated in-checkout `TMPDIR` ancestor `local.env` discovery. Resolver and fixtures unchanged. |
| `MissileLockLiveTests.MissileLock_BuildsPartialThenFull_AndPosts_Tips_OverTheWire` | pass / fail / pass | Existing intermittent missing partial-lock tip at final lock 1.000; failed isolated run lasts 38 s. Not cleared as a load-only flake. |
| `ShipStatusServerTests.PeerReceivesItsOwnShipStatus_InJoinBurst` | pass / pass / pass | Sweep expected state 3, received 2; isolated durations 0.355 / 0.370 / 0.360 s. |
| `NpcWingmanDefendLiveTests.Defender_disengages_when_chased_enemy_flees_and_returns_to_charge` | fail / fail / fail | Previously ledgered expected-charge / actual-null `AttackTarget`, line 179; remains open. |
| `NpcDodgeLiveTests.Traveling_npc_steers_around_a_station_obstacle_and_reaches_its_own_station_goal` | pass / pass / pass | Sweep clearance assertion: minObsDist 75.2, obstacle radius 60. No dodge/physics change. |
| `LiveLifepodEnduranceDisplayTests.Driver_sees_countdown_others_do_not_and_dock_cancels` | pass / pass / pass | Sweep-only display/wire assertion; no server countdown or wire change. |
| `P777WarpExitWobbleTests.Npc_exit_touch_rate_is_measured` | pass at 9.678 / 9.740 / 10.064 s | Sweep passed at 11.341 s; final isolation still marginally exceeds the 10 s Fast limit. |
| `P750NativeAsteroidTypesTests.Research_showcase_rocks_carry_native_models_and_no_rock_names` | pass at 1.616 / 1.773 / 1.627 s | Sweep passed at 11.334 s; timing-only offender in the sweep. |

The three skips are the unavailable optional Meshy-pack catalogue/content tests,
not missing native artwork. Sampled host load was about 3.4–4.1 (one/five-minute
averages), with real local proof scenarios and no synthetic load. Isolation
receipts: [P925 isolations](reports/P925_assets/isolations.json); exact gate result:
[Fast summary](reports/P925_assets/fast-summary.json). Raw logs/TRX remain in the
ignored task-worktree `.scratch/p925/`, as linked by the
[P925 report](reports/P925_REPORT.md).

## P926 validation — showcase arrival deadline, 2026-11-04

The required rotation-off P700 series finished **4/5 overall assertions**, with
**5/5 holding the never-park bound**. Run 4 expired at the separate 480-second
arrival deadline: **18/30** ships reached their objectives (requires at least
24), while real combat continued (3,769 shots, 1,243 hits, eight kills). Its
longest speed below 1 with a target was **2.005 seconds**, with no `Parked >5s`
violation. This is the same arrival-limit failure class previously recorded for
P817/P822/P841; its exact cause in this invocation is not established.

The next three filtered, serial invocations of
`P700ShowcaseLiveTests.Local_showcase_never_parks_with_target_for_five_seconds`
(runs 5–7, including the required fifth attempt) passed **3/3**, with 24/30
arrivals each:

| Isolation | Observation seconds | Longest idle with target |
|---|---:|---:|
| 5 | 256.7 | 1.812 s |
| 6 | 277.0 | 2.908 s |
| 7 | 398.1 | 4.007 s |

No assertions, timeouts, movement law or standoff distances were weakened.
The initial run used the test host's workstation GC; subsequent off runs and
the final rotating research proofs use server GC. The off series uses native
ship updates/nativeGotoPlan, pool transport and parallel immutable broadcasts.
The final fogged research proof separately checks real impacts in each derived
battle sector and no stale sector ids after rotation. No rig A/B or fresh
pre-change five-run rate is claimed.

Raw TRX/logs: `.scratch/p926/never-parks-{1..7}.*` in the task worktree; compact
results: [P926 verification](reports/P926_assets/verification.json). The arrival
limit remains a qualified gate result, rather than being reported as 5/5 clean.

## P926 validation — single Fast sweep and filtered isolations, 2026-11-04

Exactly one Fast invocation ran: **13,030 passed, nine failed, three skipped**
(optional private Meshy-pack cases), with one timing offender against the
calibrated **10.000 s** limit. Infrastructure passed all nine checks. All 26 new
P926 Fast executions passed; the 29-case focused cohort (including three Live
assembly/replay cases) passed three filtered repetitions, with zero skips.
The three fogged rotating research proofs also passed, with zero skips.
No full-tier retry was run and no unrelated implementation was changed.

| Sweep failure / timing case | Three filtered repetitions | Qualification |
|---|---|---|
| `NpcWingmanDefendLiveTests.Defender_disengages_when_chased_enemy_flees_and_returns_to_charge` | fail / fail / fail at line 179 (expected AttackTarget, actual null) | Same unchanged behavior already reproduced 3/3 on dispatch-base sources in P923. Not claimed cleared. |
| Five `PlayerBundlePathsTests` cases: Artwork executable fallback, Artwork/NativeSource missing defaults, native-source subdirectory guessing, and synthetic sky config precedence | Each fails 3/3; the other four class cases pass each run | Existing in-checkout TMPDIR obstruction: the simulated outside-repo bundle can discover the worktree's ancestor `local.env`. Same fixture assumption ledgered in P909–P916; resolver/fixtures unchanged. |
| `LiveLifepodEnduranceDisplayTests.Driver_sees_countdown_others_do_not_and_dock_cancels` | pass / pass / pass (1.263 / 1.269 / 1.265 s) | Sweep-only missing same-side relay; cause not established. Lifepod/broadcast code unchanged. |
| `WorldPopulationLiveTests.PeersLearnSameSideTeammateShip_AndWorldPopulation_FromRealServer` | pass / pass / pass | Sweep observed the teammate's lobby side (-2) before expected side 0 at line 167. Population/side-assignment code unchanged. |
| `P761StartChatComposeTests.ProfileEditor_OwnsEnterAndBackspace_AndKeepsChatClosed` | pass / pass / pass with writable task-local `XDG_CONFIG_HOME` | Already-ledgered sandbox profile-restoration failure: the original fixture attempted to write the read-only default player profile. The control uses the same source and test. |
| `P777WarpExitWobbleTests.Npc_exit_touch_rate_is_measured` (functionally passed the sweep in 10.745 s) | pass / pass / pass (9.344 / 9.531 / 9.301 s) | Sweep-only timing overrun; isolated samples meet this run's 10 s guard. No limit, tier or flight law changed. |

These qualifications retain the original failed Fast gate. Raw sweep and
filtered TRX/logs remain under `.scratch/p926/fast/` and
`.scratch/p926/*-isolation-*` in the task worktree. Small counters and per-case
outcomes: [P926 verification](reports/P926_assets/verification.json).

## P925+P926 merge — swapped showcase arrival deadline, 2026-11-04

On merged master (P925 + P926 on `0ebb5c76`),
`P700ShowcaseLiveTests.P924_swapped_showcase_never_parks_with_target_for_five_seconds`
failed "Most showcase ships must reach their objectives" in both runs of a 30-test
focused cohort (8 min each), then went **2/3** in serial isolation (pass 2 m 46 s, pass
6 m 14 s, fail 8 m 4 s). The P925-only worktree passed 1/1 (6 m 17 s). Same arrival-limit
class as P817/P822/P841/P926 above; whether P926's per-generation scout reset makes the
swapped layout slower to arrive is not established. Watch the b64 rig run's arrival times.

## P927 validation — one Fast sweep and filtered isolations, 2026-11-04

Exactly one Fast sweep ran: **13,055 passed, eight failed, three skipped**;
all nine infrastructure checks passed. The calibrated limit was **10.000 s**
and five executions exceeded it. No server/Core runtime source, unrelated test,
timeout, tier or calibration threshold was changed, and no full-tier retry ran.
The three skips are the existing optional private Meshy-pack cases; the native
artwork and `local.env` were available. A writable task-local `XDG_CONFIG_HOME`
was used throughout this sweep and the isolations.

| Sweep failure / timing case | Three filtered repetitions | Qualification |
|---|---|---|
| Five `PlayerBundlePathsTests`: Artwork executable fallback; Artwork/NativeSource missing defaults; native-source install-subdirectory guessing; local sky config precedence | Each fails 3/3; four companion class cases pass each run | Existing mandated in-worktree TMPDIR permits ancestor `local.env` discovery, as ledgered in P926 and earlier. Resolver and fixtures unchanged. |
| `NpcWingmanDefendLiveTests.Defender_disengages_when_chased_enemy_flees_and_returns_to_charge` | fail / fail / fail (expected AttackTarget, actual null) | Existing failure already reproduced on dispatch-base sources in P923; still open. |
| `P817NativeMinerDecisionCycleLiveTests.Idle_npc_repeats_mine_sell_cycle_indefinitely_after_first_sell` | pass / pass / pass (5.594 / 6.630 / 5.455 s) | Sweep missed the first mining transition at 54.355 s (cycle 1; zero sells). No native miner/sim code changed; isolated cause not established. The similarly named legacy-path companion also passes 3/3. |
| `BallotVotingLiveTests.Draw_end_to_end_offer_then_accept_then_game_over` | pass / pass / pass (0.239 / 0.236 / 0.257 s) | Sweep-only empty offer/accept collection assertion at BallotVotingLiveTests.cs:602; no ballot or server code changed. |
| `P800HullProofGpuTests.Every_hull_of_the_real_core_renders_in_hangar_flight_and_target_pane` (passed at 10.772 s) | pass / pass / pass (4.764 / 4.390 / 4.559 s) | Sweep timing overrun only; no renderer/HUD changes or new media. |
| `P814GunnerTests.P814b_TurretCarrier_pref_keeps_or_beats_the_Best_hulls_turret_seats` (passed at 11.489 s) | pass / pass / pass (4.117 / 4.253 / 4.123 s) | Sweep timing overrun only. |
| `SC2NaniteSupportTests.Support_selection_obeys_research_dock_budget_and_mounts(civ:25)` (passed at 11.011 s) | pass / pass / pass (3.726 / 3.890 / 3.720 s); civ 3 companion also passes 3/3 | Sweep timing overrun only. No training code changed. |
| `P777WarpExitWobbleTests.Npc_exit_touch_rate_is_measured` (passed at 10.665 s) | functionally passes 3/3 (11.006 / 10.591 / 10.372 s) | The timing guard remains exceeded in isolation; no clearance threshold, tier, flight law or time limit changed. |

Other real local proof processes shared the host; sampled load averages were
about 3.9–7.8 on 24 cores, with no synthetic load. These qualifications retain
the failed Fast gate. Raw logs/TRX are in task-worktree `.scratch/p927/fast/`
and `.scratch/p927/*-isolation-*`; compact outcomes are in
[P927 verification](reports/P927_assets/verification.json). The
[P927 report](reports/P927_REPORT.md) separates these results from the new
focused tests and N-side showcase proof.


## P927 validation — two-side parking gate, 2026-11-04

The required five unchanged P700 invocations finished **4/5 full passes**.
Attempt 5 hit the actual `Parked >5s` assertion at P700ShowcaseLiveTests.cs:78,
after 377 s of test execution. Side-0 bot `mcp-b028-9447`, role Bomber but
currently on a light interceptor, stopped in sector 1051 while holding live
ship target 8117. The trace reaches **5.01 s** below the idle threshold; the
server has zero velocity, throttle −1 and continuing AI decisions. This was
**not** an arrival-limit failure. The exact cause in this run is not established.

Three subsequent serial invocations of the same exact-method filter (attempts
6–8), with the same source, assertions and GC/transport/simulation settings,
passed **3/3**:

| Isolation | Observation seconds | Arrivals | Longest idle with target |
|---|---:|---:|---:|
| 6 | 262.1 | 24/30 | 0.000 s |
| 7 | 242.9 | 24/30 | 1.914 s |
| 8 | 471.3 | 24/30 | 1.904 s |

The P706/P708/P709/P830 entries already record this stopped-combat-target
parking signature, including failures on older unchanged-base controls. P927
adds no fresh dispatch-base five-run A/B; it does not establish a new cause or
claim that three successful repetitions fix the issue. The required five-run
gate remains **qualified/failed**, rather than being relabeled 5/5 green.
No P700 source, timeout, movement law, combat distance or two-side policy was
changed. No synthetic load was introduced. The later isolated run window had
sampled host load about 1.0–1.6 on 24 cores.

Raw TRX/logs: task-worktree `.scratch/p927/p927-never-parks-{1..8}.trx` and
`.scratch/p927/never-parks-{1..8}.log`. Counters, the failed bot snapshot and
successful timing samples are retained in
[P927 verification](reports/P927_assets/verification.json) and the
[P927 report](reports/P927_REPORT.md).

## P930 validation — local scratch and seating, 2026-11-05

The final P930 Fast-tier run reproduced the existing fixture-ancestry failures
with mandatory worktree-local `TMPDIR`. Each of three separate
`PlayerBundlePathsTests` family runs had **5 failed / 4 passed / 0 skipped**:
Artwork executable fallback, Artwork/NativeSource missing defaults, local sky
config precedence, and native-source install-subdirectory guessing failed.
The fake outside-repository executable directory discovers the real checkout's
ancestor `local.env`; no resolver or fixture source changed. These are persistent
environment/fixture failures already recorded above, not new cap behavior.

`ThreePeerSeededSessionTests.SeedThreePeers_TwoSameSide_OneCrossSide_DeterministicVisibility`
failed in the Fast run at its side assertion (expected `1`, observed `-2`), then
passed **3/3** exact-method isolation invocations. This retains the existing
asynchronous seating/logon-drain flake qualification; no seating, visibility,
transport or assertion code changed.

An earlier interrupted attempt also hit the already-known read-only default
profile restoration in
`P761StartChatComposeTests.ProfileEditor_OwnsEnterAndBackspace_AndKeepsChatClosed`.
The final run uses task-local `XDG_CONFIG_HOME`; that test and all 4712 executable
MonoGame cases passed, with three optional private-pack cases skipped.
The interrupted attempt is not reported as a completed full-suite gate.

Raw evidence: task-worktree `.scratch/p930/fast-final/`,
`.scratch/p930/{visibility,paths}-isolation-{1..3}.trx`; compact outcomes and
parking controls: [P930 verification](reports/P930_assets/verification.json).
Final gate qualifications and parking counts are in
[P930 report](../MATCH_CAP_P930.md).

The complete final Fast gate covered **13,100 cases: 13,087 passed, 10 failed,
3 skipped**, with four additional timing-limit violations. Other final failures
and their separate three-run follow-ups:

| Test | Follow-up outcome | Qualification |
|---|---|---|
| `AsteroidMinedLiveTests.No_broadcast_fires_once_miner_hits_capacity` | pass/pass/pass (~1.42 s) | Sweep received one extra queued mining report; existing mining receipt intermittency. |
| `P838GcProfTests.Gcprof_line_emitted_with_sane_values_and_tickprof_format_unchanged` and `Step_allocation_attribution_accumulates_and_ignores_non_step_buckets` | Both pass/pass/pass; all four profiler-family cases pass each invocation | Sweep missed step allocation values. Profiler implementation unchanged. |
| `NpcWingmanDefendLiveTests.Defender_disengages_when_chased_enemy_flees_and_returns_to_charge` | fail/fail/fail (0.861/0.891/0.848 s) | Existing null target assertion at line 179, also persistent in the prior dispatch-base controls recorded above. Remains open. |
| `P800HullProofGpuTests.Every_hull_of_the_real_core_renders_in_hangar_flight_and_target_pane` | pass/pass/pass (3.865/4.250/4.066 s) | Timing only: 10.306 s in sweep. |
| `P814GunnerTests.P814b_TurretCarrier_pref_keeps_or_beats_the_Best_hulls_turret_seats` | pass/pass/pass (3.713/3.771/3.854 s) | Timing only: 10.825 s in sweep. |
| `SC2NaniteSupportTests.Support_selection_obeys_research_dock_budget_and_mounts` | Both civ cases pass/pass/pass, 3.430–4.433 s | Timing only: civ 25 took 10.630 s in sweep. |
| `P777WarpExitWobbleTests.Npc_exit_touch_rate_is_measured` | pass/pass/pass (9.275/9.356/9.236 s) | Timing only: 10.197 s in sweep. |

Raw follow-ups: `.scratch/p930/other-core-isolation-{1..3}.trx`,
`.scratch/p930/timing-{client,mono}-isolation-{1..3}.trx`. No assertion, timing
limit, tier, simulation law or bot policy was changed to obtain these outcomes.
The full-suite gate remains failed/qualified.

P930 final-binary parking controls (runs 3–7): **5/5 base and 5/5 changed passes**, zero skips.
Early exploratory runs 1–2 are retained separately. Samples overlap on the
shared host; no synthetic load was generated. See the P930 report/evidence
for sample IDs, server binary hashes and observed idle durations.

## 2026-11-05 — P938 fast suite (concurrent with the Codex P931 training gate)

One fast run: 13,100 tests, 6 main failures, none in showcase research/rotation code. Isolation 3×:
`NpcWingmanDefendLiveTests.Defender_disengages_when_chased_enemy_flees_and_returns_to_charge` failed 3/3 (the
pre-existing fixture failure, also failing on base in P930); `MissileLockLiveTests.MissileLock_BuildsPartialThenFull_AndPosts_Tips_OverTheWire`
2/3 (known flaky); `DeadLatchRespawnLiveTests.Remote_client_sees_victim_again_after_death_and_respawn`,
`FullSessionIntegrationTests.FullSession_Join_Ready_Loadout_Launch_Movement_Chat_Compose`,
`MinerDecisionCycleLiveTests.Idle_npc_repeats_mine_sell_cycle_indefinitely_after_first_sell` and
`WarpTransitServerTests.ShipInsideWarp_GetsTransited_AndPeerSeesSectorChange` passed 3/3 (load during the shared run).

## 2026-11-06 — P934 Fast qualification (local training visibility/support)

One complete Fast-tier invocation: **13,174 cases: 13,158 passed, 13 failed,
3 skipped**, plus nine passing infrastructure checks. The three skips are the
optional Meshy pack, not artwork. Four executions exceeded the Fast limit:
missile lock 38.187 s, missile-track warning 25.839 s, miner auto-launch 36.109 s,
and warp-exit wobble 10.128 s. No test limits, assertions, tiers or non-training
simulation paths were changed. Separate exact-method isolation ran three times
per method (39 invocations, including theory companions):

| Failure / timing case | Isolation 3× | Qualification |
| --- | --- | --- |
| Five `PlayerBundlePathsTests` cases: Artwork executable fallback; Artwork/NativeSource missing defaults; native install-subdirectory guessing; synthetic sky config precedence | Each failing case fails 3/3; included SkyPack companions pass | Existing in-checkout `TMPDIR` fixture obstruction: these simulated outside-repository installs discover the real ancestor `local.env`. Same constraint as P909–P930. Resolver and fixtures unchanged. |
| `NpcWingmanDefendLiveTests.Defender_disengages_when_chased_enemy_flees_and_returns_to_charge` | fail/fail/fail (1.161/1.176/1.247 s) | Existing line-179 null-target assertion; already reproduced on dispatch/base in P918/P930 above. The P934 training guard is inactive in this fixture. |
| `MissileLockLiveTests.MissileLock_BuildsPartialThenFull_AndPosts_Tips_OverTheWire` | fail/fail/fail (38.525/38.336/38.394 s); **dispatch Core control also fails 3/3** | Lock reaches 1.000 but expected partial-lock tip is absent, line 150. The same assertion reproduces with the four changed Core files replaced at compile time by HEAD originals and the new training helper excluded, using separate artifacts without altering the working tree. Existing missile feedback failure, not introduced by training. |
| `MissileTrackWarningLiveTests.EnemyMissileHomingOnLocalShip_ArmsWarning_ThenClearsOnDestruction_OverTheWire` | pass/pass/pass (1.563/1.489/1.479 s) | Sweep timed out; warning/transport code unchanged. |
| `WorldPopulationLiveTests.PeersLearnSameSideTeammateShip_AndWorldPopulation_FromRealServer` | pass/pass/pass (1.714/1.618/1.697 s) | Sweep read lobby side -2 before expected side 0 at line 167; existing side-seating race. |
| `LiveSideSelectTests.Flag_on_auto_seats_a_silent_peer_at_the_configured_30_seconds` | pass/pass/pass (1.003/0.999/0.996 s) | Existing auto-seat timing fixture; no side-selection change. |
| `NpcAutoLaunchLiveTests.Docked_npc_miner_auto_undocks_after_cooldown_then_mines` | pass/pass/pass (3.443/3.437/3.271 s) | Sweep waited 36 s; ordinary NPC auto-launch unchanged. |
| `P777WarpExitWobbleTests.Npc_exit_touch_rate_is_measured` | pass/pass/pass (9.458/9.311/9.196 s) | Timing-only violation in the sweep; isolated case durations stay below 10 s. |
| `P761StartChatComposeTests.ProfileEditor_OwnsEnterAndBackspace_AndKeepsChatClosed` | pass/pass/pass with task-local `XDG_CONFIG_HOME` | The sweep attempted to restore an existing read-only home profile. Existing sandbox/profile fixture; the sole sweep did not redirect XDG. No profile code changed. |
| `P871bCreateGameClientTests.Ready_joins_the_created_mission_and_failure_stays_on_the_wizard` | Both theory cases pass/pass/pass with task-local `XDG_CONFIG_HOME` | Sweep success branch observed null pending join; writable isolated profile configuration avoids it. Wizard/client code unchanged. |

Raw receipts: `.scratch/p934/fast/summary.json`,
`.scratch/p934/fast-isolation/summary.json`, `.scratch/p934/base-missile/summary.json`.
Dispatch source/build and Core binary hashes: `.scratch/p934/base-head.txt`,
`.scratch/p934/baseline.targets`, `.scratch/p934/build-baseline.log`,
`.scratch/p934/showcase-binaries.json`. No synthetic load was generated.
The single full Fast gate remains failed/qualified; no second sweep was run.

P934 showcase qualification: five interleaved dispatch controls passed **5/5**;
the five planned P934 trials passed **4/5 overall**. P934 trial 4 observed only
18/30 arrivals by the existing 480-second horizon (required 24), with real
combat and a maximum target stop of **1.904 s**. Every parking assertion passed;
no five-second parked target was observed. Three subsequent exact-method
invocations passed **3/3** (scenario times 401.0 / 171.8 / 473.9 s, maximum target
stops 1.906 / 1.003 / 2.014 s). This qualifies the arrival-horizon fluctuation;
the original failed scenario remains recorded. Across all 13 trials, the
parking checks pass 13/13; full-scenario outcomes are dispatch 5/5 and P934 7/8.
No limit, assertion, tier, routing policy or ordinary multiplayer code was
changed. Each trial ran in its own process on the prescribed baseline; trials
interleaved and overlapped the real training qualification on the shared host.
Load and binary hashes are included in `.scratch/p934/showcase/summary.json`
and `.scratch/p934/showcase-followup/summary.json`.

## P933 Fast qualification — 2026-11-06

The single requested Fast sweep completed **13,156 passed / 12 failed / 3 skipped**
(13,171 total), with two timing-limit violations. All nine infrastructure checks
passed. The skips are the existing optional private-pack cases. No P933 test
failed. Each failing method and the additional timing offender received three
separate filtered invocations after the sweep, with no other task tests running.

| Test / family | Three filtered repetitions | Qualification |
|---|---|---|
| `PlayerBundlePathsTests` five failing cases: Artwork executable fallback, Artwork/NativeSource missing defaults, local config precedence, NativeSource install-subdirectory guessing | Every failing case fails 3/3; companion passing theory cases remain passing | Existing fixture-ancestry issue with mandatory worktree-local `TMPDIR`: fake executable folders discover the checkout's ancestor `local.env`. Same signature documented in P930 and earlier entries. No resolver changed. |
| `NpcWingmanDefendLiveTests.Defender_disengages_when_chased_enemy_flees_and_returns_to_charge` | fail / fail / fail | Same null charge-target assertion already reproduced on unchanged-base controls in P930 and earlier entries; remains open. |
| `MissileLockLiveTests.MissileLock_BuildsPartialThenFull_AndPosts_Tips_OverTheWire` | fail / fail / fail, 38.239 / 38.260 / 38.229 s | Lock reaches 1.000 but the expected full/partial tips do not both arrive. Existing intermittent failure recorded in P938; persistent in this cohort. Also the sweep's 38.185 s timing offender. No lock, tip or assertion code changed. |
| `P660NextMatchLiveTests.Late_game_over_rebuild_ready_countdown_started_has_no_clock_jump` | first invocation 3 passed / 1 failed; next two 4/4 passed | The disabled/off case reports 14.90 against [14.94, 15.06]. Mixed outcomes remain qualified; not relabeled three clean passes. No clock/countdown code changed. |
| `MissileBroadcastTests.Directly_fired_missile_broadcasts_create_to_teammate_and_destroy_to_whole_mission` | pass / pass / pass | Sweep destruction receipt reports roughly 1.5 s of travel for a 2 s missile. Same terminal-position signature was qualified in P911; collision/broadcast source unchanged. |
| `GameOverPlayersWireTests.Rejoined_player_accumulates_time_across_stints_in_roster` | pass / pass / pass | Sweep time 8.20 exceeds the fixture's upper bound 8.00. No roster/time accounting changed. |
| `BallotVotingLiveTests.Resign_passing_deactivates_the_side_and_ends_the_mission` | pass / pass / pass | Sweep receives the passed-ballot chat without the expected matching collection/state observation; existing ballot delivery intermittency. |
| `P467E2eGunDamageDevLiveTests.Completing_dev_143_raises_gun_damage_in_combat` | pass / pass / pass | Sweep misses the expected single-round damage observation; no gun/development rule changed. |
| `P777WarpExitWobbleTests.Npc_exit_touch_rate_is_measured` | pass / pass / pass, 9.136 / 9.113 / 9.098 s | Timing only: 10.170 s in sweep. No timing limit, tier, clearance or flight law changed. |

The original Fast gate remains **FAIL**. Raw receipts are in task-worktree
`.scratch/p933/fast/` and `.scratch/p933/isolation/`; the exact method/run mapping
is `.scratch/p933/fast-isolations.json`. Compact outcomes are in
[P933 verification](reports/P933_assets/verification.json). The separate long
training-window qualification is reported in [P933](reports/P933_REPORT.md).

## P933 training-window qualification — 2026-11-06

The single artwork-backed training gate finished **367 passed / 4 failed /
2 skipped**. Both skips were existing opt-in M4 hunt branches; zero artwork
cases skipped. All ten full-course windows passed. Four existing P912 proofs
failed in that run, then each passed all three isolated repetitions:

| Case | Broad-run signature | Three serial follow-ups |
|---|---|---|
| `Assigned_lesson_completes_in_its_own_fresh_window(mission: 4, lesson: 5)` | 20-minute timeout, `M4 stalled at 5.7`; Mars warp traversal with repeated trainee death/restoration | pass / pass / pass, 109.6 / 109.5 / 110.3 s |
| Same method, `(mission: 4, lesson: 10)` | 20-minute timeout, `M4 stalled at 8.7`; waiting for command acceptance, queued target empty | pass / pass / pass, 457.5 / 571.3 / 617.5 s |
| Same method, `(mission: 3, lesson: 9)` | 20-minute timeout, `M3 stalled at 9.7` | pass / pass / pass, 123.2 / 156.3 / 118.4 s |
| `Early_nanite_mounting_wins_through_real_input(style: "Modern")` | M8 goal 3.1: early mounting/conserved repair energy did not exercise miner survival; survived=false, deathMessage=false | pass / pass / pass, 279.0 / 292.5 / 268.5 s |

Each of the three filtered invocations ran these four cases serially with the
sequential runsettings and no other task tests alongside it: **12 passes,
0 failures, 0 skips**. No controller, assertion, timeout or production source
changed between the full gate and these follow-ups. Under lane rules these
are intermittent proof failures; the full gate remains **FAIL**, and their
root cause is not established.

Four unchanged-dispatch-base controls also passed once each. They intentionally
ran concurrently in separate processes, so they do not reproduce the full-run
conditions or provide a matched-load comparison. Base/current assembly hashes
and code-marker checks are in the compact evidence. Do not claim that base
reproduced any of these failures or that load alone explains them.

Raw evidence: `.scratch/p933/training/`, `.scratch/p933/window-isolation/`,
`.scratch/p933/base-window-controls/`, and
`.scratch/p933/inherited-p931/windows/`. Compact results:
[P933 verification](reports/P933_assets/verification.json).

## P933 baseline parking qualification — 2026-11-06

With `DOTNET_gcServer=1` and the native/pooled/parallel/immutable/encode-once
baseline, the first unchanged-dispatch-base
`P700ShowcaseLiveTests.Local_showcase_never_parks_with_target_for_five_seconds`
run failed **arrival coverage**, not its five-second idle assertion: after
480.0 seconds, 19/30 ships had reached their objectives; longest speed below
1 with a target was 1.913 seconds. The assertion was
`Most showcase ships must reach their objectives`.

The next three serial filtered base invocations passed **3/3, zero skips**:
241.8 / 302.5 / 193.1 observation seconds, each 24/30 arrivals, with longest
idle periods 1.804 / 1.612 / 1.904 seconds. No source, assertion, timeout or bot
policy changed. This establishes unchanged-base arrival intermittency; it does
not establish its cause or a five-second parking violation. Full five-pair
base/working-tree results are recorded in the [P933 report](reports/P933_REPORT.md).
Raw receipts: `.scratch/p933/baseline-parking/`.

The complete five alternating base/working-tree pairs finished **4/5 passes
in each lane, zero skips**. Working-tree run 4 failed the same arrival assertion
at 480.1 seconds (17/30 reached, longest idle only 0.802 seconds). Its next
three serial invocations, runs 5–7, passed **3/3**: 266.2 / 216.1 / 209.1
observation seconds, 24/30 arrivals each, longest idle 1.212 / 2.003 / 0.000
seconds. No five-second parking assertion failed in any of the twelve pinned
observations. Runs 6–7 are follow-ups, excluded from the original five-pair
rate comparison. Equal observed rates in this small sample do not establish
a causal performance result; the arrival failure remains qualified.

## P935 — gate isolation (2026-11-06)

The single Fast sweep encountered the already-ledgered
`P761StartChatComposeTests.ProfileEditor_OwnsEnterAndBackspace_AndKeepsChatClosed`
profile-restoration obstruction: the fixture's `finally` block (line 237) tries
to write the default user profile outside the lane's writable roots.
Isolation in the original environment reproduced the same I/O failure **3/3**;
the same source/test passed **3/3** with `XDG_CONFIG_HOME` pointing at task-local
scratch. This is an environment restriction; P935 changes no profile or chat
implementation. Evidence: `.scratch/p935/isolated/P761-default-{1,2,3}.trx`
and `P761-writable-{1,2,3}.trx`. Report: `docs/reports/P935_REPORT.md`.

Additional P935 Fast-sweep non-task isolations:

| Case | Three isolated trials | Finding |
|---|---|---|
| `PlayerBundlePathsTests`: Artwork executable fallback; Artwork/NativeSource missing default; local config precedence; NativeSource install-subdirectory guessing | Each failing case fails 3/3; four companion cases pass each time | Existing fixture-ancestry obstruction: mandatory in-worktree `TMPDIR` lets fake outside-repo bundles discover the ancestor `local.env`. Resolver and fixtures unchanged. |
| `NpcWingmanDefendLiveTests.Defender_disengages_when_chased_enemy_flees_and_returns_to_charge` | fail / fail / fail | Existing expected charge target / actual null at line 179, previously reproduced against dispatch-base sources. Remains open. |
| `NaniteThreatBonusLiveTests.Threat_target_earns_2x_repair_credit_over_wire` | pass / pass / pass | Sweep-only repair-credit ratio failure (0.083); same intermittent family already ledgered. No repair/credit implementation changed. |
| `MissileLockLiveTests.MissileLock_BuildsPartialThenFull_AndPosts_Tips_OverTheWire` | fail / fail / fail | Lock reaches 1.000 but the expected partial/full tip pair is absent. The approximately 38-second failure also exceeds the Fast budget; already persistent in the prior cohort. No lock/tip implementation changed. |

Raw isolation files are `.scratch/p935/isolated/non-task-<method>-{1,2,3}.trx`.
P935's raw timestamp compatibility checks and mission-assembly reflection check
are task regressions, corrected and rechecked separately; they are not classified
as flakes here.

| Additional P935 sweep case | Three isolated trials | Finding |
|---|---|---|
| `MinerDecisionCycleLiveTests.Idle_npc_repeats_mine_sell_cycle_indefinitely_after_first_sell` | pass / pass / pass (4.023 / 3.990 / 5.477 s) | Sweep missed the first mine/sell transition after 51.676 s. Existing miner-cycle intermittency; also a sweep timing offender. |
| `LivePositionRequestTests.Empty_side_request_auto_accepts` | pass / fail / pass | Existing early JOIN_SIDE receipt assertion intermittency; not claimed cleared. |
| `DockDockingTests.Ship_nowhere_near_a_station_is_unaffected` | fail / pass / pass | Existing unscoped status-history assertion includes another docked ship. Docking/status code unchanged. |
| `SC2NaniteSupportTests.Support_selection_obeys_research_dock_budget_and_mounts` | Both civ cases pass 3/3 (3.215–5.463 s) | Passed in the sweep; civ 25 took 10.679 s, over the Fast limit. Timing only. |
| `P814GunnerTests.P814b_TurretCarrier_pref_keeps_or_beats_the_Best_hulls_turret_seats` | pass / pass / pass (3.605 / 3.469 / 3.521 s) | Passed in the sweep at 10.700 s, over the Fast limit. Timing only. |

## P940 Fast sweep — 2026-11-06

The one Fast sweep finished **13,210 passed / 16 failed / 3 optional private-Meshy-pack
skips**, with 15 executions exceeding the calibrated 10.941-second limit and
all nine infrastructure checks passing. Real training windows were running
concurrently; load averaged 30–45 around qualification. No synthetic load was
used. The observations below do not establish load as the cause. Each failure
family received three fresh, filtered processes; no broad Fast rerun was used.

| Method / family | Sweep observation | Three filtered isolation results |
| --- | --- | --- |
| `P902SoundTemplateTests.Asr_and_cache_hit_mix_math_allocate_nothing_after_warmup` | Expected zero allocation; observed 6,504 bytes. Existing P918 allocation intermittency. | **pass / pass / pass**, zero skips |
| `ShopLauncherMountNumberingLiveTests.ShopBuyOrder_LauncherGrantedAtMount0_LocksAndFires_Live` | Empty default `ExpandedPartData` instead of the granted launcher; existing receipt intermittency. | **fail / pass / pass**, zero skips; not a clean three-pass isolation |
| `P877MineDamageLiveTests.Chased_bot_wake_mine_damages_pursuer` | Timeout at line 116 waiting for the bot's own loadout; the helper's message says join. Same previously ledgered signature. | **pass / pass / pass**, zero skips |
| `MissileLockLiveTests.MissileLock_BuildsPartialThenFull_AndPosts_Tips_OverTheWire` | Full lock 1.000 but the expected partial/full tip pair is absent. Persistent in P933/P935 as well. | **fail / fail / fail**, zero skips; remains open |
| `P889PoolSizingTests.Explicit_config_still_wins_over_the_resolver_at_boot` | The lane runner's baseline environment overrode this fixture's explicit serial/thread-per-peer configuration. | **fail / fail / fail** with only the parallel-build override cleared; **pass / pass / pass** with both build and transport overrides cleared, zero skips |
| `MissileBroadcastTests.Directly_fired_missile_broadcasts_create_to_teammate_and_destroy_to_whole_mission` | Terminal travel 1247.78 m versus the full-lifespan expectation 1848.56 m. Existing earlier-destruction signature. | **pass / pass / pass**, zero skips |
| `WarpBombTests.Live_whole_mission_warp_bomb_reaches_the_cross_side_peer_and_the_delayed_blast_lands` | Expected delayed-blast receipt collection was empty. | **pass / pass / pass**, zero skips |
| `NpcDodgeLiveTests.Traveling_npc_steers_around_a_station_obstacle_and_reaches_its_own_station_goal` | Clearance assertion, minimum distance 73.1 with obstacle radius 60. Same prior qualification family. | **pass / pass / pass**, zero skips |
| `NpcWingmanDefendLiveTests.Defender_disengages_when_chased_enemy_flees_and_returns_to_charge` | Expected charge target, actual null. Persistent in P933/P935 and previously reproduced on dispatch-base sources. | **fail / fail / fail**, zero skips; remains open |
| `TcpTransportSendWorkerPoolTests.Boot_line_prints_the_active_transport_mode`; `Explicit_thread_per_peer_config_boot_stays_thread_per_peer` | Forced pool environment overrode the tests' explicit legacy path. | Each **pass / pass / pass** with the transport override unset, zero skips; ordinary qualification retains pool/parallel settings |
| `PlayerBundlePathsTests` five failed executions: Artwork executable fallback; Artwork/NativeSource missing defaults; NativeSource install-subdirectory guessing; local config precedence | Mandatory scratch-local `TMPDIR` makes fake outside-repo directories inherit the checkout's ancestor `local.env`. Same P935 fixture obstruction. | With a task-local empty solution-marker fence: first four executions **pass / pass / pass**; `LocalConfigWinsOverBundleWithBlankEnvironment` **fail / fail / fail**; all four companion executions pass each time, zero skips |

The bundle-path fence shields the fake executable tree from the real checkout,
but the resolver searches that executable tree before the fixture's configured
working directory, so it still masks the latter's local config. This is an
explicit environment control, not a product fix or a claim of a green path
suite. Neither the fixtures nor `ExternalPaths` changed. P940 also changes no
ordinary shop, missile/tip, ASR cursor/cache, Server or Core implementation.
The two compatibility controls retain the tests' explicitly requested old path;
they do not change production environment precedence.

Raw receipts: `.scratch/p940/fast-results/`, `non-task-isolation/`,
`non-task-client-isolation/`, `p889-intended-environment/` and
`core-and-timing-isolation/` beneath `.scratch/p940/`.

Passed sweep cases that exceeded the time limit also received three filtered
invocations (all theory variants selected, all passed, zero skips). Times below
are seconds; the three failed timing offenders are included in the failure table.

| Passed timing offender | Sweep seconds | Isolated seconds, three runs |
| --- | --- | --- |
| `P796VariedLoadoutTests.Varied_equips_every_legal_hull_and_part_of_the_role_over_many_purchases(civ: 25)` | 15.870 | 7.430 / 7.069 / 7.657 |
| `P814GunnerTests.P814b_TurretCarrier_pref_keeps_or_beats_the_Best_hulls_turret_seats` | 18.393 | 5.213 / 5.758 / 5.039 |
| `SC2NaniteSupportTests.Support_selection_obeys_research_dock_budget_and_mounts(civ: 3)` | 17.348 | 6.545 / 6.836 / 6.633 |
| `P796VariedLoadoutTests.Varied_equips_every_legal_hull_and_part_of_the_role_over_many_purchases(civ: 35)` | 17.006 | 5.499 / 5.619 / 5.592 |
| `P709CallbacksLiveTests.Money_request_arrives_without_payment_then_approval_uses_real_player_donate` | 12.025 | 2.185 / 2.283 / 2.225 |
| `SC2NaniteSupportTests.Support_selection_obeys_research_dock_budget_and_mounts(civ: 25)` | 17.464 | 4.782 / 4.816 / 4.614 |
| `WarpTransitServerTests.ShipBetweenRawAndVisualRadius_DoesNotPrematurelyTransit_AndPeerSeesNoSectorChange` | 14.481 | 8.103 / 8.376 / 8.541 |
| `P817NativeMinerDecisionCycleLiveTests.Idle_npc_repeats_mine_sell_cycle_indefinitely_after_first_sell` | 14.071 | 8.540 / 8.714 / 7.490 |
| `P750NativeAsteroidTypesTests.Research_showcase_rocks_carry_native_models_and_no_rock_names` | 11.817 | 2.232 / 2.533 / 2.829 |
| `TechTreeDevelopmentLiveTests.Side_attribute_change_broadcasts_whole_mission_but_not_across_missions` | 11.652 | 9.667 / 9.966 / 13.889 |
| `StationUnderAttackAlertLiveTests.Second_hit_within_throttle_window_produces_no_second_alert` | 12.165 | 6.976 / 6.840 / 6.529 |
| `ProsperityArtifactLiveTests.Prosperity_disabled_team_money_completion_does_not_end_game` | 11.004 | 9.283 / 8.902 / 8.799 |

`TechTreeDevelopmentLiveTests` still took 13.889 seconds in the third isolated
run; the functional assertions passed. No tiers, deadlines, timeouts or
exclusions were weakened. A fresh checkout of the exact dispatch HEAD also reproduced the missile-tip
failure, null defender target and all five bundle-path failures in one control
per family (`.scratch/p940/base-failure-controls/`). The original Fast gate
remains failed.

## P940 showcase qualification — 2026-11-06

Five alternating dispatch-base/working-tree pairs used the unchanged
`P700ShowcaseLiveTests.Local_showcase_never_parks_with_target_for_five_seconds`
with server GC, immutable snapshots, encode-once, parallel build, pooled
transport, native ship updates and native GotoPlan. The full test passed
**base 4/5, working tree 3/5**, zero skips. All three failures were
`Most showcase ships must reach their objectives` at the eight-minute limit:
base run 3 reached 15/30; working runs 2 and 5 reached 18/30 and 16/30.
No five-second parking assertion failed in any observation.

Three additional fresh, filtered invocations per tree were then run serially,
separate from the original five-pair rate comparison:

| Tree | Three filtered outcomes | Observation seconds | Longest idle with target, seconds |
| --- | --- | --- | --- |
| Dispatch base | pass / pass / pass | 332.3 / 222.5 / 251.0 | 1.625 / 2.015 / 1.928 |
| Working tree | pass / pass / **fail** | 299.3 / 415.7 / 480.0 | 1.818 / 1.415 / 2.011 |

The third working-tree follow-up reached 23/30 and failed the same arrival
assertion; its longest idle with a target was 2.011 seconds. Across all sixteen
observations, the largest idle with a target was 3.432 seconds. Arrival coverage
therefore remains qualified, **not cleared by three passing working-tree
isolations**. This matches the existing P933 arrival signature and occurs on
the exact P940 dispatch base too. P940 changes no ordinary bot/Server/Core
implementation; its movement change is confined to connected training hosts.
These unseeded functional controls on a shared host do not establish a causal
performance effect or explain the unequal small-sample rates. No assertion,
scenario, deadline, tier or exclusion changed, and no further attempts were
used to seek a clean three-pass result.

Raw receipts: `.scratch/p940/parking-control/`,
`.scratch/p940/parking-isolation/`; original dispatch HEAD
`4e659544639571d7be42039c5a23f125156b91b7`.

## P940 narration gate — resolved proof-pilot defect

Restoring the installed Ogg narration exposed the existing M3 pilot's coarse
firing aim and its continuous firing during spoken lessons. The original
all-lesson invocation failed the Modern lesson-10 and lesson-11 prefixes at
goal 8.9; the lesson-10
backbuffer showed Gat Gun ammo 0/32767, “Ammo depleted,” and an undamaged
Derelict03. Three fresh, filtered old-pilot replays all reached the unchanged
20-minute guard: **fail / fail / fail**, ending at 8.9, 7.9 and 9.1. The last
replay reached 9.1 only at elapsed 1190.1 seconds; that ending alone is not
evidence of a stuck sound condition. This is a task-related proof defect,
not a load flake to waive.

The pilot repair uses real mouse deltas for fine aim, fires only at the native
demonstration/destruction gates, and waits for the destruction condition before
retargeting. It changes no actor, ammunition, condition, clock or deadline.
The exact dispatch-base lesson-10 control passed in 183.4 seconds with the old
silent audio loader; that is not a matched-audio timing comparison. Old and
repaired-pilot results are kept separately, and the original training run's
failures remain in its TRX.

The repaired pilot passed **3/3 complete M3 courses per HUD style** and
**3/3 of each Modern lesson-9/10/11 prefix**, fifteen passing filtered tests
with zero skips. Three additional Classic lesson-10 real-window replays also
passed. These outcomes resolve the pilot defect; the original all-lesson
invocation retains its two old-pilot failures.

Raw receipts: `.scratch/p940/training-failure-isolation/`,
`.scratch/p940/base-training-control/`, `.scratch/p940/training-failure-green/`,
`.scratch/p940/precision-m3-classic/`, `.scratch/p940/precision-m3-whole/` and
`.scratch/p940/precision-m3-prefixes/`; final counts are in
`docs/reports/P940_REPORT.md`.

## P940 older loopback fixture repairs — 2026-11-06

The original artwork-backed training invocation also failed two existing P881a
completion cases at their unchanged four-minute limit: M2 at **8.6**, M3 at
**13.2**. Both are task-related fixture failures, not load-flake waivers.

M2's simulated client supplied `OwnAutopilot` only for M4; the old host movement
adapter concealed that omission. Its fixture now consumes the M2 return order
and sends its existing real bay-entry controls. The first corrected source
passed **3/3**, zero skips (114.44 / 114.25 / 114.34 seconds), with six passing
facility/target checks. The test now also requires evidence that its own
client autopilot callback was used; the real server must still dock it.

M3's existing digital bay steering failed **3/3** in isolation at 13.2
(241.48 / 241.52 / 241.47 seconds including test startup). The exact dispatch
base passed in 142.27 seconds; the broad failure therefore was not dismissed
as pre-existing. A read-only trace showed repeated full yaw/pitch reversals
on the approach. Its input driver now uses pose/rate damping while retaining
the same waypoints, thrust policy, wire controls, landing condition and time
limit. The first corrected trial passed in 146.69 seconds. Final-source
repetition counts are recorded in `docs/reports/P940_REPORT.md` and the compact
verification receipt; the original broad invocation retains all four failures.

Final-root-source checks passed **M2 3/3 and M3 3/3**, zero skips.
M2 took 114.46 / 114.45 / 114.41 seconds; M3 took
146.70 / 147.25 / 147.09 seconds. These filtered checks resolve the
task-related fixture failures without changing the historical broad result.

Receipts: `.scratch/p940/fixture-return/`, `fixture-facilities/`,
`legacy-m3-isolation/`, `base-legacy-m3-control/`, `legacy-m3-diagnostic/`,
`legacy-m3-damped-bay/`, `final-fixture-m2/` and `final-fixture-m3/`, all beneath
`.scratch/p940/`. Failed discovery/diagnostic setups and rejected controller
experiments are retained separately and earn no qualification credit.

## Mini runner sweep of P940 (f14a5abe) — 2026-11-06

| Method | Sweep observation | Isolation |
| --- | --- | --- |
| `ChaffTests.Live_firing_mounted_mine_launcher_creates_mine_decrements_ammo_and_broadcasts` | `Assert.Contains` filter not matched (`ChaffTests.cs:651`); first failure in the last eight mini runs | **pass / pass / pass** on the workstation; ledgered as a one-off |
| `P939SpecialAsteroidTests.Single_path_maps_keep_the_base_map_exactly` | Failed on every mini run since P939 (2/2): the generated rocks' positions differ by ~0.3 mm between the Docker runner and the workstation (single-float generation maths), which changed the pinned hash | **Fixed, not a flake:** the pinned hash now omits positions and keeps id/cluster/class/radius/ore/model; same hash on both machines; an injected extra RNG draw still fails it |
| `P940BasicFlightInputTests.Native_training_ogg_decodes_to_non_silent_pcm_with_the_original_duration` | `NoAudioHardwareException` instead of `ArgumentException` on the headless runner | **Fixed, not a flake:** the WAVE-only oracle is skipped only when MonoGame reports no audio hardware |

## Mini runner nightly d8a1b89f — 2026-11-07

Four failures were new against the 2026-11-06 nightly. Each was rerun 3x alone on the workstation at `14b74a24`.

| Test | Nightly failure | 3x alone |
| --- | --- | --- |
| `ArmedProbeLiveTests.Armed_probe_spawns_hits_and_damages_enemy_with_ammo_decrement_live` | Failed after 41 s in the parallel live sweep | **pass / pass / pass** (~0.6 s each); load-only |
| `BallotVotingLiveTests.Resign_passing_deactivates_the_side_and_ends_the_mission` | Failed in the parallel live sweep | **pass / pass / pass**; load-only |
| `BotFlightTests.Bot_trajectory_matches_independent_ExecuteMove_rollout` | Failed in the fast sweep | **pass / pass / pass**; load-only |
| `P778ABomberPushLiveTests.Staged_finale_pushers_contact_the_bomber_and_do_not_slow_the_strike` | Failed after 77 s in the parallel live sweep | **pass / pass / pass** (~57 s each); load-only |

The six failures present in all three nightlies (2026-11-05..07) are older and were not re-triaged here: P700/P924 never-parks,
P745 finale siege, P798 scout opening, SC4 research showcase, NpcWingmanDefend disengage, P928 crowded-sector golden.

## Recurring nightly failures — isolation 2026-11-07

The six failures present in every nightly 2026-11-05..07 were rerun 3x alone on the workstation at `9e51530a`
(other delegate lanes were building/testing concurrently). First failing nightly from the mini runner history.

| Test | 3x alone | First failing nightly | Verdict |
| --- | --- | --- | --- |
| `P928AllocationTests.Crowded_sector_matches_pre_refactor_500_tick_golden` (both cases) | **pass / pass / pass** | 11-05 (`826a974d`, the P928 commit itself) | **FIXED by P951 (working tree, 2026-11-07):** v1 hashed raw float bits; the runner image reproduced locally diverged from tick 4 (goto-planner steering floats differ by libm ULPs, chaos spreads to ~5 m; one planner-ship `StateM` bit and grazing damage events also flip). Fixture format v2 hashes discrete state/event identity + quantized continuous state (planner ships' continuous state and per-tick damage counts excluded/with tolerance); regenerated from the pre-refactor base `5ca9f37a`, byte-identical to master, and passes 3× alone in the workstation and 3× inside the locally built runner image. See `docs/reports/P928_GOLDEN_PORTABLE_P951.md` |
| `P700ShowcaseLiveTests.P924_swapped_showcase_never_parks_with_target_for_five_seconds` | **pass / pass / pass** (3m29–5m10) | 11-05 | load-only on the runner |
| `NpcWingmanDefendLiveTests.Defender_disengages_when_chased_enemy_flees_and_returns_to_charge` | **fail / fail / fail** (`Assert.Same` expected ship, actual null) | 10-29 (`48cdb2de`; passed 10-28 `a2f73c13`) | **Real regression.** Window `a2f73c13..48cdb2de` (55 commits) includes P865b SIM-0663 carrier-relaunch Goto→Defend re-task (`30e814e4`) and P869b lazy ThingSite/registry (`5e23df09`) |
| `P798ScoutOpeningLiveTests.Fogged_showcase_scouts_open_the_map_and_the_clash_forms_on_resources` (both maps) | **fail / fail / fail** (`Clash` null on both sides) | 10-29 (same window) | **Real regression** (no resource clash forms) |
| `SC4ShowcaseLiveTests.Research_showcase_deploys_chaff_probes_and_defensive_mines_on_both_sides` | **fail / fail / fail** (SC-1 minimums not met) | 10-30 (once on 10-27) | **Real regression** |
| `P745FinaleSiegeLiveTests.Research_showcase_finale_destroys_a_station` | **fail / fail / fail**, three different ways: no station destruction; `IndexOutOfRangeException` at `:119`; finale string now has `\nStation kills: …` (`Mission.StationKills.cs:36`, P924 `ad5c48f8`) vs the expected text at `:145` | 10-22 steady (once 10-19) | **Real:** stale string expectation plus a parse crash plus a missed station kill |


## P950 mouse-capture qualification — 2026-11-07

The single complete Fast run recorded **13,307 passed / 11 failed / 3 skipped**,
plus **10 timing offenders** against its calibrated **10.227 s** ceiling. The
three skips are the optional private Meshy-pack cases. No simulation, server,
bot, discovery, bundle resolver or profile implementation changed in P950.

| Failure group | Three isolated repetitions | Dispatch-base control / classification |
|---|---|---|
| `NpcWingmanDefendLiveTests.Defender_disengages_when_chased_enemy_flees_and_returns_to_charge` | fail / fail / fail, expected charge target / actual null | Same failure 3/3 on dispatch `9e51530a`; existing open assertion |
| Five `PlayerBundlePathsTests` executions: Artwork executable fallback; Artwork/NativeSource missing defaults; native install-subdirectory guessing; local sky-config precedence | Every failing case fails 3/3; included companion theory cases pass | Same failures 3/3 on dispatch. Mandated scratch-local TMPDIR lets simulated outside-repository installs discover the ancestor `local.env`; same fixture obstruction ledgered in earlier lanes |
| `NpcWingmanLeadLiveTests.Wingman_fire_gate_leads_a_laterally_moving_enemy` | pass / pass / pass | Existing shared-state/load signature; no flight/AI rule changed |
| `AsteroidCombatLiveTests.Same_side_friendly_missile_damages_the_asteroid_no_friendly_fire_gate` | pass / pass / pass | Existing live receipt/timing signature |
| `LiveSideSelectTests.Flag_on_auto_seats_a_silent_peer_at_the_configured_30_seconds` | pass / pass / pass | Sweep observed 31.4 simulated seconds versus the unchanged 28–31 assertion |
| `P761StartChatComposeTests.ProfileEditor_OwnsEnterAndBackspace_AndKeepsChatClosed` | pass / pass / pass with writable task-local XDG | Sweep tried to restore the existing read-only default profile; same sandbox fixture signature as earlier lanes |
| `DiscoveryTests.Coordinator_is_silent_when_tailscale_absent` | pass / pass / pass with a task-local absent-CLI stub | The null-returning status callback falls back to the real local CLI. Its unexpected non-loopback discovery stays only in private scratch logs; no remote proof was requested or intentionally run |

Every failed assertion and slow method was selected individually in a fresh
process three times. Timing selections additionally covered `P800HullProofGpuTests`,
`SC2NaniteSupportTests.Support_selection_obeys_research_dock_budget_and_mounts`,
`P814GunnerTests.P814b_TurretCarrier_pref_keeps_or_beats_the_Best_hulls_turret_seats`,
`P777WarpExitWobbleTests.Npc_exit_touch_rate_is_measured`,
`WarpTransitServerTests.ShipBetweenRawAndVisualRadius_DoesNotPrematurelyTransit_AndPeerSeesNoSectorChange`,
`P750NativeAsteroidTypesTests.Research_showcase_rocks_carry_native_models_and_no_rock_names`,
`P942ResearchOrderingTests.Generated_research_never_waits_for_a_drone_or_utility_base_while_a_development_is_buyable`,
`NpcDodgeLiveTests.Traveling_npc_steers_around_an_asteroid_on_its_path_and_resumes`,
and `TechTreeDevelopmentLiveTests.Side_attribute_change_broadcasts_whole_mission_but_not_across_missions`.
They all passed their assertions. P777's actual TRX durations were
**10.162 / 10.231 / 9.716 s**: its second isolated execution remains slightly
over the original calibrated ceiling. Process elapsed time is not substituted
for TRX test duration. One-minute load at isolated-process start ranged from
**5.27 to 23.86** on the shared host; no artificial load was created. The Fast
run remains **FAIL**, with no timing threshold or assertion relaxed.

The final P950 new input methods and existing controller/response cohort pass
**156/156 across three isolated repetitions**, zero skips. Compact evidence is
in `reports/P950_assets/qualification.json`; raw TRX/logs stay in the task
worktree's `.scratch/p950/`. Showcase results are recorded in the task report.

### P950 showcase control — 2026-11-07

Five authoritative alternating pinned-baseline pairs produced **dispatch 5/5 / working tree 3/5 full-test passes**, zero skips. Working-tree attempts 1 and 5 missed the unchanged arrival threshold: **23/30** and **18/30** at 480 seconds. All five working-tree attempts and all five dispatch attempts satisfied the five-second parking bound; working-tree maximum **3.614 s**. Same unseeded arrival signature already recorded for P940; no AI/server/simulation/test-fixture source changed in P950. The P700 path creates Server/BotManager and does not instantiate Game1 or the mouse capture helper.

The three fresh isolated working-tree follow-ups passed **3/3**, zero skips, with maximum stopped interval **1.813 s**. They do not replace either failure in the original cohort. Exact arrivals, combat receipts and TRX durations are in [qualification.json](reports/P950_assets/qualification.json).

All receipt values and original outcomes are retained in `reports/P950_assets/qualification.json`; raw TRX/logs are under the task worktree's `.scratch/p950/{showcase,showcase-retries}/`. No arrival or parking assertion, timeout or tuning value changed, and the original full-fixture cohort remains **FAIL**.
## P949 gate — 2026-11-07

`tools/run-tests.sh --tier fast` (13,392 tests, 628.8 s, load average 30.08 on 24 CPUs) — one failure plus nine
Fast tests over the calibrated 10 s limit (all nine passed):

| Test | Outcome | Verdict |
| --- | --- | --- |
| `NpcWingmanDefendLiveTests.Defender_disengages_when_chased_enemy_flees_and_returns_to_charge` | fail in the fast sweep; **fail / fail / fail** alone (`Assert.Same` expected ship, actual null) | **Already-open real regression**, first seen 10-29 (`48cdb2de`) and re-triaged 2026-11-07 above; P949 changes no NPC/sim/bot code (input defaults + HUD hint only) |

The nine SLOW FAST entries (P800 hull-proof 11.6 s, P814 gunner 13.7 s, P796 varied-loadout 11.8 s, SC2 nanite
14.1 s, P777 warp-exit 11.8 s, NpcDodge 10.7 s, P942 research 13.2 s, P750 asteroid types 17.0 s, P808 enrollment
10.9 s) are the calibrated-limit timing guard under that load; every one passed.
## P951 fast-sweep non-task failures — 2026-11-07

P951's fast sweep (13,363 passed / 4 failed / 3 skipped) failed four unrelated
tests; each was re-run 3× alone per the ledger rule. No product code changed
(`git diff -- src/` empty; only the P928 golden fixture format and its test).

- `DiscoveryTests.Coordinator_is_silent_when_tailscale_absent`
  (ClientCore.MonoGame): **fail / fail / fail**, 56 ms — environmental: a
  Tailscale host (`<tailnet host>`, LAN game, 13 players) was
  discoverable on this workstation's tailnet during the run, so the
  assert-empty discovery result cannot hold. The test needs either a
  tailscale-absent precondition check or a skip marker when a tailnet is
  present. No discovery code changed in P951.
- `NpcWingmanDefendLiveTests.Defender_disengages…`: **fail / fail / fail** —
  the known real regression recorded in "Recurring nightly failures —
  isolation 2026-11-07" (also reproduced on the pristine tree there).
- `LiveSideSelectTests.Flag_on_auto_seats_a_silent_peer_at_the_configured_30_seconds`:
  pass / pass / fail — the known intermittent receipt/clock boundary [28,31]
  (recorded in P928/P931 sections).
- `BallotVotingLiveTests.Draw_end_to_end_offer_then_accept_then_game_over`:
  pass / pass / pass alone — the known receipt-ordering intermittency
  (line ~5043 above); it failed only inside the parallel sweep.

Six Fast executions exceeded the calibrated 10 s guard (11.1–15.2 s: P777
warp-exit, P814b turret seats, SC2 support, NpcDodge, ClientWarpApproach,
P800 hull GPU) — timing-only, other lanes were building/testing concurrently
(load average 6.3–8.8 during the later isolation runs). P777's guard exceeds
are already recorded; the others are new one-off samples of the same
load-only family.

Showcase never-parks gate (`P924_swapped_showcase_never_parks…`): **6 pass /
2 fail** over 8 single-case runs; both failures are the two slowest runs
(8 m 3 s / 8 m 4 s), every faster run passed (3 m 43 s–7 m 38 s), confirming
the recorded load-only sensitivity now also visible on the workstation under
concurrent lanes (P928's base rate was 3/3 at lower load).
## P955 local gate — 2026-11-07

The single Fast invocation finished **13,422 passed / 10 failed / 3 skipped**
(optional private Meshy-pack cases), plus **9 infrastructure checks passed**.
Three passing executions exceeded the calibrated **10.288 s** Fast limit.
Each failed/slow method received three fresh filtered processes, including all
its theory rows. Qualification totals: **30 passed / 18 failed / 0 skipped**.

| Method / family | Sweep observation | Three isolated invocations |
| --- | --- | --- |
| `PlayerBundlePathsTests`: Artwork executable fallback; Artwork/NativeSource missing defaults; NativeSource install-subdirectory guessing; local sky-config precedence | Five failing cases: fake outside-repo installs discover the mandatory worktree ancestor `local.env` through scratch-local `TMPDIR`. Same fixture obstruction as P909–P940. | Each failing case **fail / fail / fail**; passing theory companions still pass. Unchanged base Core also yields **4 passed / 5 failed / 0 skipped**, each of three whole-class controls. Resolver and fixtures remain unchanged. |
| `NpcWingmanDefendLiveTests.Defender_disengages_when_chased_enemy_flees_and_returns_to_charge` | Expected charge target; actual null. Existing P933/P935/P940 family. | **fail / fail / fail**; unchanged dispatch-base Core also **fail / fail / fail**. Remains open outside P955 pod lifetime. |
| `FullSessionIntegrationTests.FullSession_Join_Ready_Loadout_Launch_Movement_Chat_Compose` | Initial side expected team lobby (-2), observed side 0. | **pass / pass / pass**, zero skips. |
| `ShipStatusServerTests.PeerReceivesItsOwnShipStatus_InJoinBurst` | Join status expected docked (3), observed flying (2). | **pass / pass / pass**, zero skips. |
| `P804ClientLeadTests.Lead_solve_and_green_ramp_allocate_nothing_per_frame` | 39,748 ns versus its 20,000 ns per-frame threshold. | **pass / pass / pass**, zero skips; no threshold change. |
| `P814GunnerTests.P814b_TurretCarrier_pref_keeps_or_beats_the_Best_hulls_turret_seats` | Body passed; 12.142 s exceeded Fast budget. | **pass / pass / pass**, 4.156 / 3.972 / 3.762 s. |
| `SC2NaniteSupportTests.Support_selection_obeys_research_dock_budget_and_mounts` | Body passed; civ 25 took 11.097 s. | Both civ rows **pass / pass / pass**, 3.217–4.470 s. |
| `P777WarpExitWobbleTests.Npc_exit_touch_rate_is_measured` | Body passed; 11.075 s exceeded Fast budget. | Body **pass / pass / pass**, 10.148 / 10.309 / 10.100 s; the middle isolation still exceeds the calibrated limit. Timing qualification remains non-green. |
| `DiscoveryTests.Coordinator_is_silent_when_tailscale_absent` | Injected null status fell back to the installed CLI and unexpectedly probed a real Tailscale peer, breaching P955's local-only constraint. | Test-only fixture now supplies an empty status; **pass / pass / pass**, zero skips. Production discovery is unchanged. Endpoint details remain only in ignored raw logs. |

The affected oxygen-display fixture originally passed 1/3 and failed 2/3 alone
because it only decoded SINGLE_SHIP_UPDATE, while the native broadcast default
relays remote ships through HEAVY_SHIPS_UPDATE. Its recorder now includes those
fuel fractions; final qualification **pass / pass / pass**, zero skips. The
same-side watcher actually received 90 full-fuel relays while the owner's
91 oxygen readings spanned 1.0 down to 0.1. P955's 76 targeted methods qualify
at **297 passed / 0 failed / 0 skipped** across their final three invocations.

Five unchanged-base `P700ShowcaseLiveTests.Local_showcase_never_parks_with_target_for_five_seconds`
runs returned **4 passed / 1 failed / 0 skipped**. The failure reached 23/30 ships
versus the required 24, after 480 s; its longest speed-below-one interval with a
target was 3.511 s. None of the five base runs failed the parking assertion.
The five fixed-version runs also returned **4 passed / 1 failed / 0 skipped**.
Fixed run 5 reached 21/30 versus the required 24 after 480 s; its longest
speed-below-one interval with a target was 1.714 s. Both batches passed the
parking assertion 5/5. Shared-host load varied; the five-run samples establish
the recorded local comparison without a broader rate claim.

Evidence: [P955 report](reports/PLAYER_POD_GHOST_P955.md),
[portable qualification](reports/P955_assets/validation.json); ignored raw
receipts under `.scratch/p955/` include `fast/`, `fast-retry-*.trx`,
`base-npc-*.trx` and `base-bundle-*.trx`. This is a non-green broad gate with
explicitly qualified existing failures, not a claim that the full suite passed.

## P954 gate — 2026-11-07

`tools/run-tests.sh --tier fast` (13,473 tests, 586.0 s, shared 24-CPU host with other lanes' suites
running) — three failures, none in P954's changed code (test files + `tools/allegiance-mcp` finale arm
grace only):

| Test | Fast-sweep symptom | Isolated outcome | Verdict |
| --- | --- | --- | --- |
| `NpcWingmanDefendLiveTests.Defender_disengages_when_chased_enemy_flees_and_returns_to_charge` | fail | **fail / fail / fail** alone (1 s, expected charge target / actual null) | **Already-open real regression**, first seen 10-29 (`48cdb2de`), re-triaged at the P949 gate above; P954 changes no NPC/sim/bot policy code |
| `MissileTrackWarningLiveTests.Warning_routes_by_the_source_ship_for_pilot_and_gunner(gunner: True)` | fail, 25.9 s (also a SLOW FAST timing offender) | **pass / pass / pass / pass** alone (2 s each) | Sweep load flake |
| `MinerDecisionCycleLiveTests.Idle_npc_repeats_mine_sell_cycle_indefinitely_after_first_sell` | fail, 21.7 s (also a SLOW FAST timing offender) | **pass / pass / pass** alone (10 s each) | Sweep load flake, same intermittency as the P934/P940 entries above |


## P956 gate — 2026-11-07

`tools/run-tests.sh --tier fast` (13,474 cases, 579.2 s fast-tier clock across
the five projects: Update 28, Lobby 139, MonoGame 4,885 passed + 3 optional-pack
skips, ClientCore 2,271, Core 6,145 passed + 3 failed), host load average 7–9 with other lanes'
suites running. The three Core failures are the load-only families already
recorded above; every one passed **3/3 alone** (19/19 cases in the three
classes, 15 s each):

| Test | Sweep signature | Alone | Verdict |
| --- | --- | --- | --- |
| `WarpTransitServerTests.ShipInsideWarp_GetsTransited_AndPeerSeesSectorChange` | `Assert.Contains` missed the own-ship `SHIP_STATUS`; the collection held an initial miner's status (ShipId 8098, hull 1) instead | **3/3 pass** | Ledgered load/timing family (2026-11-07 rows above); P956 touches no warp/join-burst path |
| `P813cReconnectGraceTests.Voluntary_logoff_is_immediate_and_reports_quit` | `Assert.Contains` over an empty collection (missing receipt) | **3/3 pass** | Ledgered load-only family; no reconnect/grace code changed |
| `DockedStationUpdateLiveTests.Docked_player_receives_own_station_update_when_own_station_has_no_scanner` | station-update cadence value differed under load | **3/3 pass** | Ledgered 15-tick station-update cadence family (2026-10-24/27 rows) |

The four SLOW FAST entries (P800 hull proof 12.3 s, SC2 nanite 10.8 s, P814
gunner 10.6 s, P777 warp-exit 11.3 s) are the calibrated-limit timing guard
under that load; every one passed. No drone-loadout, sim, spawn or broadcast
failure appeared in the sweep; the P956 carts (`DroneSpawnLoadoutTests`, the
armed-Wingman defend/attack fixtures, the miner/constructor/layer cohorts)
passed 3/3 isolated runs and are clean in the sweep. Raw logs:
`~/allegiance-evidence/P956/fast3/` and `~/allegiance-evidence/P956/nontask/`
(TRX/log per project; fast-tier clock 579.2 s, `summary.json`).

### P959 fast-tier sweep (`tiers-20261108T075742`)

13,514 tests, 3 failures — all three are already-ledgered Core flakes, none touched by this task (no
`src/Allegiance.Core` file changed):

| Test | Sweep | 3x alone | Ledger |
| --- | --- | --- | --- |
| `Allegiance.Core.Tests.Transport.ConsoleLogNonBlockingTests.Verbose_gate_silences_and_reenables_routed_diag_lines` | failed (process-global gate under load) | **pass/pass/pass** | existing `ConsoleLogNonBlockingTests` family |
| `Allegiance.Core.Tests.LiveSideSelectTests.Flag_on_auto_seats_a_silent_peer_at_the_configured_30_seconds` | failed | **pass/pass/pass** | existing clock/receipt boundary vs [28,31] |
| `Allegiance.Core.Tests.BallotVotingLiveTests.Resign_passing_deactivates_the_side_and_ends_the_mission` | failed | **pass/pass/pass** | existing ballot-receipt family; unmodified base fails it too |

## 2026-11-08 — P960 P807 handoff investigation

`P807GameOverHandoffTests.P807_two_consecutive_handoffs_still_work`'s historical
workstation-only two-hour timeout remains **unreproduced / trigger unconfirmed**.
Do not mark it resolved solely from passing reruns. Original-flow controls pass
168 full method invocations; final qualification passes 10/10 alone, 3/3 at 16
processors, and 4/4 in two paired-process batches, zero skips.

The shared old helper does reproduce a different delivery stall at 394 peers:
pending rotations are empty and the fresh server mission has 197 peers per
side, while all clients remain Over with null ship IDs. Both 16- and 24-CPU
controls hit its client-receipt wait at the 30-second diagnostic cap. P960 stops
advancing simulation during pure receipt waits, bounds the helper, prints the
condition/match plus full state and captured console on failure, locks identity
reads, and disposes the sessions it owns. No production behavior changes.

The 394-peer **timing** family remains open and its budgets are unchanged:
the final three standalone runs give **fail / fail / fail** (phase
116.2 / 98.8 / 91.0 ms against 76.5 / 77.4 / 76.9 ms; 164 migration ticks each).
All correctness checks pass before those timing assertions. An earlier round
was pass / pass / fail (11.7 / 12.2 / 115.6 ms); its failed burst coincided with
101 ms of GC pause. Final qualification was repeated after synchronizing console
snapshots and moving construction before redirection; its totals are
**17 passed / 3 failed / 0 skipped**, and the earlier passes do not waive the
final timing failures. A loaded candidate similarly fails phase
96.0 > 77.3 ms. The dispatcher's separate receipt was declare sim work 160.8 ms
(declare 229.7 minus GC 68.9), a different existing budget failure.

Report and compact receipts: [P807_HANDOFF_HANG_P960.md](reports/P807_HANDOFF_HANG_P960.md),
[P960 validation](reports/P960_assets/validation.json). The one Fast gate
finishes **13,510 passed / 5 failed / 3 optional Meshy skips**, 604.935 s.
All nine infrastructure checks pass; nine Fast executions exceed 10 s.
The failed/timing methods were each repeated three times alone (42 case
executions: **36 passed / 6 failed / 0 skipped**).


| P960 Fast failure | Three exact-method standalone runs | Current disposition |
|---|---|---|
| `ShopLauncherMountNumberingLiveTests.ShopBuyOrder_LauncherGrantedAtMount0_LocksAndFires_Live` | pass / pass / pass | Existing launcher-grant receipt flake. |
| `MissileLockLiveTests.MissileLock_BuildsPartialThenFull_AndPosts_Tips_OverTheWire` | pass / fail / fail | Existing final lock 1.000 without the expected partial-tip receipt; still intermittent alone. |
| `ShipStatusServerTests.PeerReceivesItsOwnShipStatus_InJoinBurst` | pass / fail / fail | Existing state-3/state-2 join receipt intermittency; not cleared. Wire constants are flying=3, docked=2. |
| `LiveSideSelectTests.Flag_on_auto_seats_a_silent_peer_at_the_configured_30_seconds` | fail / fail / pass | Existing auto-seat clock/receipt boundary; one isolation saw 32.0, another missed the ready receipt. |
| `BallotVotingLiveTests.Mutiny_passing_transfers_leadership_failing_keeps_it` | pass / pass / pass | **New exact-method entry**: empty receipt collection in sweep; existing ballot family. No ballot code changed. |

All seven Fast timing-offender methods also ran three times alone. `P796VariedLoadoutTests`
(3 theory cases), `SC2NaniteSupportTests` (2 cases), `P814GunnerTests`, and
`NpcDodgeLiveTests.Traveling_npc...` pass all repeats below 10 s per case.
`P800HullProofGpuTests.Every_hull...` passes its assertions but takes
9.519–16.322 s alone; `P777WarpExitWobbleTests.Npc_exit_touch_rate_is_measured`
passes but takes 9.601–11.393 s. Those timing offenders are not cleared merely
by successful assertions. Missile lock is the overlapping failure/timing method.
Full names, durations and cases are in the report/validation JSON.

The supplementary Live-order probe was intentionally capped at 180 s per test
and aborted in the unrelated `SC2SupportPhaseLiveTests` before P807. Its authored
observation is 900 s; the abort is a diagnostic-cap mismatch, not an SC2
assertion failure or a passing/completed Live suite. The historical two-handoff
trigger and the full Fast gate therefore remain open; no production file or
budget was changed.

## P961 local qualification — 2026-11-08

`P700ShowcaseLiveTests.Local_showcase_never_parks_with_target_for_five_seconds`:
unchanged-base **5 passed / 0 failed / 0 skipped**; changed **4 passed / 1 failed /
0 skipped** over the required five attempts. Changed attempt 3 missed only the
existing arrival quota: **20/30** versus 24 at 480 seconds. Its longest
speed-below-one interval with a target was **0.501 s**; both idle-with-target
counters were zero. All ten original attempts passed the five-second parking
bound. Three subsequent isolated repeats were **pass / pass / pass**, zero
skips (2m36, 3m20, 4m08). This is the same unseeded arrival signature previously
recorded for P940/P950/P955, not evidence that the finale-free fixture parked a
wing. The fixture explicitly disables finales.

A repeated all-tools gate also had an MSBuild infrastructure failure at
`tools/strobe-proof`: child nodes 6 and 2 exited prematurely (`MSB4166`), without
compiler diagnostics or surviving failure files at the advertised temporary
path. That project built **pass / pass / pass** alone, with zero warnings and
errors (about 1.2 s each). Earlier all-121-tools invocations passed. No source
change was needed for this failure; the final qualification uses one MSBuild
node to avoid depending on child-node reuse. The cause of the premature exits
was not established.

Evidence: [P961 report](reports/FINALE_REACHABLE_STRIKE_P961.md); ignored raw
TRX/logs under `.scratch/p961/test-results/` (`neverparks-before-*`,
`neverparks-after-*`, `neverparks-isolate-*`), and
`.scratch/p961/build-tools-prearm-final.log`, `strobe-isolate-*.log`,
`build-tools-qualification.log`.

The one P961 Fast sweep discovered **13,538** cases: **13,527 passed / 8 failed /
3 skipped**, plus 9 passing infrastructure checks. Three optional Meshy-pack
cases skipped; artwork-dependent task coverage did not skip. The non-task
failures were qualified without changing fixtures, resolver code or tiers:

| Failure / timing signature | Three isolated repeats | Attribution |
| --- | --- | --- |
| `ThreePeerSeededSessionTests.SeedThreePeers_TwoSameSide_OneCrossSide_DeterministicVisibility` (expected side 1, saw lobby side -2) | pass / fail / pass; the middle repeat assigned peer 2 to side 0 after logon drain reordering | Existing seating/logon timing signature. A further three runs of this method alone passed 1/0/0 each; the initial middle failure remains part of the qualification record. |
| `BotAcceptsAndEngagesOrderLiveTests.Mcp_debug_bot_buys_relaunches_and_fights` (pod case did not fight again; 16.323 s) | Both data cases pass in each repeat; combined counts with the seeded case were **3/0/0, 2/1/0, 3/0/0**, 6–7 s total | Existing pod/relaunch timing signature; no finale is configured. |
| `P761StartChatComposeTests.ProfileEditor_OwnsEnterAndBackspace_AndKeepsChatClosed` (default profile restoration is read-only) | pass / pass / pass with task-local `XDG_CONFIG_HOME`, zero skips | Existing sandbox profile-restoration obstruction. |
| Five `PlayerBundlePathsTests` failures | Whole class **4 passed / 5 failed / 0 skipped** in each repeat | Existing scratch-local `TMPDIR`/ancestor `local.env` fixture obstruction, as in P909–P955; Core runtime bytes match the archived baseline exactly. |
| `P777WarpExitWobbleTests.Npc_exit_touch_rate_is_measured` (body passed; 10.213 s exceeded Fast limit) | pass / pass / pass, **9.899 / 9.831 / 9.911 s** | Existing timing-only signature; no tier or limit changed. |

Raw sweep summary is `.scratch/p961/fast/summary.json`; qualification receipts
are `fast-client-isolate-*`, `profile-isolate-*`, `paths-isolate-*`, and
`wobble-isolate-*` under `.scratch/p961/test-results/`. The broad gate remains
non-green with these explicit qualifications; no task-specific test failed.

## 2026-11-08 — P965 restart migration barrier qualification

One complete Fast invocation: **13,508 passed / 6 failed / 4 skipped**,
13,518 cases in 525.4 seconds after the shared runner lock wait. All nine
infrastructure checks pass. Four passing executions exceed the calibrated
10-second guard. Every failed/slow method received three standalone invocations
(45 case results: **30 passed / 15 failed / 0 skipped**, including companions).

| Sweep failure / timing family | Three isolated invocations | Classification |
|---|---|---|
| Five `PlayerBundlePathsTests` cases: Artwork executable fallback; Artwork/NativeSource missing defaults; native install-subdirectory guessing; local sky-config precedence | Each failing case fails 3/3; six companion case executions pass | Existing mandated scratch-local TMPDIR exposes the checkout's ancestor `local.env` to the simulated outside-repository installs. Same fixture obstruction as P909–P960; resolver and fixtures unchanged. |
| `NaniteThreatBonusLiveTests.Threat_target_earns_2x_repair_credit_over_wire` | 3/3 pass, no skips | Sweep saw bomber credit 0.000 instead of 1.000. Existing wire-credit timing family (also P935); no repair/score implementation changed. |
| P800 hull GPU, P796 varied purchases, SC2 nanite selection, P814b gunner preferences | All cases pass 3/3; maximum individual durations 4.000 / 5.945 / 4.591 / 3.603 s, respectively | Sweep-only calibrated timing exceedances (11.031 / 11.742 / 12.703 / 13.099 s), below the 10-second limit alone. |

The four skips are three optional private Meshy-pack cases and P940 installed
voice playback without an audio device. Every P965/P807/P660/P765 targeted
handoff invocation has zero skips.

P807's unchanged 394-peer test passes correctness before its budget assertions
in all nine changed/base invocations. Initial changed runs: **1 passed / 2 failed**,
max phase 104.5 / 86.4 / 41.0 ms versus scaled 76.5 / 78.0 / 78.1 ms budgets.
An alternating dispatch-base/changed comparison gives **base 2 passed / 1 failed**,
**changed 0 passed / 3 failed**. The base failure's 89.4 ms phase falls in a tick
with 76 ms GC pause; changed follow-up phases 90.2 / 109.3 / 93.4 ms fall in ticks
with 79 / 97 / 81 ms pauses. Initial changed failures record 92 / 75 ms pauses.
This reproduces the existing P807/P960 phase-budget family; it is not a claim
of equal failure rates or a rig performance A/B. No timing budget, migration
batch size, join-burst work or test wait changed. The 24-peer two-handoff method passes 3/3;
no wait hang or sim-pass failure occurred.

New P965 cases pass **21/21**; other affected cohorts pass **144/144**
(P660 33, P765 3, P598/P739/P762/P480 108). Normal-home never-parks passes **5/5**,
matching the saved P956 5/5 cohort; every run reaches 24/30, observes combat,
has zero sim-pass failures, and holds the parking limit (maximum 1.915 s).

Report: [RESTART_MIGRATION_BARRIER_P965.md](reports/RESTART_MIGRATION_BARRIER_P965.md).
Compact receipts: [validation.json](reports/P965_assets/validation.json).
Raw ignored evidence: `.scratch/p965/` in the task worktree.

## P976 gate observation (2026-11-09) — Update apply transaction

The one Fast-tier run in the P976 gate (`tools/run-tests.sh --tier fast`, host load
average 3.7 at start, 10-16 earlier; other lanes running) finished 13,642 tests with
two failures, both in `Allegiance.ClientCore.MonoGame.Tests` and both in the existing
allocation-under-parallel-load family — no client, HUD or sound code was touched by
P976 (the task changes `src/Allegiance.Update/`, its tests and docs):

| Sweep failure | Three isolated invocations | Classification |
|---|---|---|
| `P902SoundTemplateTests.Asr_and_cache_hit_mix_math_allocate_nothing_after_warmup` (1,232 B allocated) | 3/3 pass | Already ledged allocation intermittency (see the P902/P787a entries above). |
| `Hud12bReviewFixTests.Hostile_damage_threat_and_flash_allocate_nothing_per_contact` (7,432 B over 10k calls) | 3/3 pass | Same allocation-under-load family as the P787a observation (line 2439); HUD code unchanged. |
| Second sweep (host load 14.4): `NpcWingmanAttackLiveTests.Wingman_with_no_enemy_in_sector_stands_by` (travelled 170.718) | 3/3 pass | Already itemized above (lines 154, 1158): sweep-only standby displacement, NPC/flight unchanged. |
| Second sweep: `AddPartTests.Part_pickup_decline_recreates_treasure_at_current_position` (quarantine tier) | runner retry passed | Existing quarantine flake (lines 609, 904); pickup/world code unchanged. |
| Second sweep: `P750NativeAsteroidTypesTests.Research_showcase_rocks_carry_native_models_and_no_rock_names` 10.037 s, `P777WarpExitWobbleTests.Npc_exit_touch_rate_is_measured` 10.750 s | — | Calibrated 10 s timing limit under load; both are runtime-only overruns, not assertions. |
| Third sweep (final tree, host load 3.9→8): `ShopLauncherMountNumberingLiveTests.ShopBuyOrder_LauncherGrantedAtMount0_LocksAndFires_Live`, `DeadLatchRespawnLiveTests.Remote_client_sees_victim_again_after_death_and_respawn`, `P761bCommandRelayTests.QueuedStationGoto_KeepsOwnTarget_AndRelaysTheStationToTheTeammate`, `P739TimerLiveTests.Warp_bomb_notice_matches_captured_blast_deadline_and_stays_in_mission(delay: 7)`, `GameOverPlayersWireTests.Rejoined_player_accumulates_time_across_stints_in_roster` | each 3/3 pass (all cases; 5 cases × 3 for the Core filter) | All are existing ledgered Live-tier receipt/timing families (see the entries above); none touches the updater. |
| Third sweep: `MissileLockLiveTests.MissileLock_BuildsPartialThenFull_AndPosts_Tips_OverTheWire` (38.6 s "lock never reached full + partial tip (lock 1.000)") | **1 pass / 2 fail**, 38 s signature, plus 1/1 pass alone | The known OPEN partial-tip receipt failure (P933/P935/P938/P961 entries; fails isolated cohorts too). No lock/missile/tip code changed by P976; not cleared. |

P976 also updates `tests/Allegiance.ClientCore.MonoGame.Tests/P944JoinLinkUiTests.cs`:
the source-order guard now asserts the new branch (a link launch must not reach the
feed check but must still run the apply/recovery). That class passes 10/10 alone.

The same run also flagged two calibrated timing exceedances outside this task
(`CrossSideCombatVisibilityLiveTests.CrossSide_sector_stays_correct_across_warp_return` 36.8 s and
`P777WarpExitWobbleTests.Npc_exit_touch_rate_is_measured` 11.4 s) and quarantined the first
(its retry passed). `Allegiance.Update.Tests` passed 54/54 in the sweep and 3/3 alone
before it. Report: [reports/UPDATE_ATOMIC_APPLY_P976.md](reports/UPDATE_ATOMIC_APPLY_P976.md).

## P982 — sandbox profile obstruction (2026-11-09)

An interrupted preliminary Fast sweep hit the already-ledgered
`P761StartChatComposeTests.ProfileEditor_OwnsEnterAndBackspace_AndKeepsChatClosed`:
its `finally` restores the existing real user profile at test line 237, outside
this lane's writable filesystem. The exception is `IOException: Read-only file
system`; P982 changes no profile editor, chat handler, or persistence code.

| Isolated control | Passed / Failed / Skipped | Result |
|---|---:|---|
| Original environment, three serial runs | 0 / 3 / 0 | Same read-only profile restoration failure in all three runs. |
| Existing `ALLEGIANCE_PLAYER_SETTINGS` redirected to task scratch, three serial runs | 3 / 0 / 0 | All profile-editing and chat-focus assertions pass. |

The final full Fast sweep uses that supported writable-profile override. The
worktree's original `local.env` is restored after the runner reads the override;
no real user settings are changed. Raw evidence:
`.scratch/p982/p761-readonly-{1,2,3}.trx` and
`.scratch/p982/p761-scratch-{1,2,3}.trx`.

### P982 final Fast and isolated controls

The final source sweep ran 13,802 cases: **13,794 passed / 5 failed / 3 skipped**,
plus nine passing runner-infrastructure checks. The skips are the optional private
Meshy-pack cases. The five failures and calibrated-limit offender match existing
entries; P982 changes no audio, repair, missile, bot, collision, simulation or wire
implementation. Full Fast remains **FAIL**.

| Case | Sweep signature | Three serial isolated executions |
|---|---|---|
| `P902SoundTemplateTests.Asr_and_cache_hit_mix_math_allocate_nothing_after_warmup` | 1,384 bytes allocated, expected zero | **pass / pass / pass**, 0.009 / 0.009 / 0.012 s |
| `NaniteThreatBonusLiveTests.Threat_target_earns_2x_repair_credit_over_wire` | fighter credit 0.222 versus 0.500 | **pass / pass / pass**, 3.434 / 3.435 / 3.392 s |
| `MissileLockLiveTests.MissileLock_BuildsPartialThenFull_AndPosts_Tips_OverTheWire` | lock 1.000 without the required partial/full tip pair; 38.325 s Fast budget offender | **fail / pass / fail**, 38.517 / 1.666 / 38.456 s; known OPEN issue, not cleared |
| `MissileBroadcastTests.Directly_fired_missile_broadcasts_create_to_teammate_and_destroy_to_whole_mission` | terminal travel 1663.70 m versus full-lifespan 1848.56 m | **pass / pass / pass**, 0.508 / 0.480 / 0.526 s |
| `BotFlightTests.Bot_trajectory_matches_independent_ExecuteMove_rollout` | tick 139 drift 18.57 m | **pass / pass / pass**, 1.092 / 1.061 / 1.097 s |
| `P777WarpExitWobbleTests.Npc_exit_touch_rate_is_measured` | passing test, 10.378 s versus calibrated 10 s limit | **pass / pass / pass**, 9.309 / 9.883 / 9.941 s, all below the limit |
| `CrossSideCombatVisibilityLiveTests.CrossSide_sector_stays_correct_across_warp_return` | preliminary quarantine timeout, immediate managed retry passed; final quarantine passes | **pass / pass / pass**, 1.577 / 1.580 / 1.935 s |

All isolated controls skipped zero cases. Raw logs/TRX are under
`.scratch/p982/*-isolated-{1,2,3}.*`; compact outcomes are retained in
`docs/reports/P982_assets/triage.json`.

Earlier interrupted drivers survived and overlapped showcase loops; those runs
are excluded from isolated-rate evidence. Both stale drivers were stopped, the
temporary cleanup hook was removed, and the original configuration bytes were
restored. The subsequent fresh serial showcase cohort passed **5/5**, zero skips,
with test durations **243.3 / 235.9 / 194.8 / 181.8 / 162.2 s**. Each reached
24/30; longest low-speed-with-target intervals were **1.915 / 1.413 / 2.808 /
2.707 / 2.516 s**. Documented P979 base rate: 4/5. This headless fixture does not
exercise visual flags, so no bot improvement is attributed to P982. Receipts:
`.scratch/p982/serial-showcase/` and `docs/reports/P982_assets/showcase.json`.

## P990 (broadcast lane/age telemetry) — Fast-tier triage

Fast sweep on this tree (`tools/run-tests.sh --tier fast --no-build`): **13,898 tests, 598.3 s, FAIL** — 4 case
failures (3 in `Allegiance.Core.Tests`, 1 in `Allegiance.ClientCore.Tests`) plus 6 executions over the calibrated 10 s
limit. P990 touches only the default-off `profiler.broadcastBudget` diagnostic (its report rows and its own
bookkeeping data structure); no send site's payload, order, cadence or budget changed, and the P983 wire-identity and
zero-allocation pins still pass, so none of these is on the P990 path.

| Case | Sweep signature | Three isolated executions |
|---|---|---|
| `LiveCombatScoringTests.Real_two_peer_kill_updates_both_player_scores` | Fast sweep missed `KILL_SHIP` inside its observation budget | **pass / pass / pass** (in the batch with the P739/LiveSideSelect filters, 2 s total) — the ledgered receipt timeout |
| `P739TimerLiveTests.Warp_bomb_notice_matches_captured_blast_deadline_and_stays_in_mission(delay: 7)` | one-ULP upper bound `7.0000000000000009 > 7` | **pass / pass / pass** — the ledgered floating deadline-boundary family |
| `LiveSideSelectTests.Flag_on_auto_seats_a_silent_peer_at_the_configured_30_seconds` | `31.x` outside the fixture's [28, 31] window | **pass / fail / pass** — the ledgered receipt/simulation-clock boundary (recorded clean-base rate 2 pass / 3 fail) |
| `MissileLockLiveTests.MissileLock_BuildsPartialThenFull_AndPosts_Tips_OverTheWire` | `lock never reached full + partial tip (lock 1.000)`, 38 s (also a slow-Fast offender) | **pass / fail (38 s) / pass** — the long-ledgered OPEN partial-tip receipt family (clean-base rate 1 pass / 2 fail, same 38 s signature) |
| `SC2NaniteSupportTests.Support_selection_obeys_research_dock_budget_and_mounts` (civ 25, 10.264 s), `P814GunnerTests.P814b_…` (10.104 s), `P777WarpExitWobbleTests.Npc_exit_touch_rate_is_measured` (11.313 s), `P942ResearchOrderingTests.Generated_research_never_waits_…` (10.029 s), `P750NativeAsteroidTypesTests.Research_showcase_rocks_…` (13.438 s) | slow-Fast executions, assertions passed | the same ledgered near-limit families the P989/P987 cohorts carry (isolated runs below 10 s in those cohorts) |

Second Fast invocation on the final tree (after the last cosmetic `PoseStampTable` simplification, `--no-build`):
**13,898 tests, 573.8 s, FAIL** — 3 failures, 4 slow-Fast offenders, host load 1.2-5.4 (quiet).

| Case | Sweep signature | Three isolated executions |
|---|---|---|
| `MissionRotationLiveTests.Finished_mission_rotates_to_fresh_notstarted_and_new_joiner_starts` | `Assert.Contains` on an empty decoded NotStarted list (`MissionRotationLiveTests.cs:181`) | **pass / pass / pass** (with the `LiveCombatWireTests` filter below) — the ledgered ~12 % receipt-order intermittent (stage precedes client decode), unchanged rotation/transport code |
| `LiveCombatWireTests.Moving_target_streams_its_falling_hull_over_the_wire` | receipt order (`no shooter ship` family) | **pass / pass / pass** — ledgered receipt-order family |
| `MissileLockLiveTests.MissileLock_BuildsPartialThenFull_AndPosts_Tips_OverTheWire` | `lock never reached full + partial tip (lock 1.000)`, 38.302 s | **pass / fail (38 s) / pass** in the earlier isolated cohort — ledgered OPEN partial-tip family |
| slow-Fast: `P750NativeAsteroidTypesTests…` 11.727 s, `P777WarpExitWobbleTests…` 10.685 s, `P942ResearchOrderingTests…` 10.348 s, `MissileLockLiveTests…` 38.302 s | assertions passed | the same ledgered near-limit families |

Showcase never-parks on the final tree: **5/5 pass** (durations 3 m 11 s, 2 m 20 s, 2 m 40 s, 2 m 27 s, 6 m 03 s);
the P990 change cannot reach it (the diagnostic is off by default in that fixture and only its own null-checked
bookkeeping changed).

## P989 (sim palette) — Fast-tier triage

Fast sweep on this tree: **13,877 tests, 672.3 s, FAIL**. One test failed and three executions exceeded the calibrated
10 s limit; none of them touches the P989 change (a client-render flag that only retunes two existing post passes).
`git diff 6659cb91 --name-only` contains no missile, lock/tip, warp, gunner or bot source.

| Case | Sweep signature | Three serial isolated executions |
|---|---|---|
| `MissileLockLiveTests.MissileLock_BuildsPartialThenFull_AndPosts_Tips_OverTheWire` | `lock never reached full + partial tip in the client world (lock 1.000)`, 38.9 s (slow-Fast offender) | **fail / fail / fail**, 38.6 / 38.5 / 38.4 s — the long-ledgered OPEN partial-tip receipt family (P966/P982 rows above: clean-base rate 1 pass / 2 fail, same 38 s signature); not cleared by this task and not caused by it |
| `P814GunnerTests.P814b_TurretCarrier_pref_keeps_or_beats_the_Best_hulls_turret_seats` | 11.496 s versus the 10 s limit | **pass / pass / pass**, 3 s each (isolated, after the sweep) |
| `P777WarpExitWobbleTests.Npc_exit_touch_rate_is_measured` | 12.541 s versus the 10 s limit | **pass / pass / pass**, 11 s per class run (8 cases each), after the sweep |

### P990 review (Claude, 2026-11-10) — merged branch, Core Fast tier
Core.Tests `Tier=Fast` on `pi/p990` + master 724d1cdf: 6239 passed / 1 failed. The failure,
`WarpTransitServerTests.ShipInsideWarp_GetsTransited_AndPeerSeesSectorChange`, passed **3/3 alone**; it is the ledgered
load/timing family above (P990 touches only telemetry behind `profiler.broadcastBudget`, default off).

## P991 (SHIP_STATUS change gate) — Fast-tier and showcase triage

Fast sweep on this tree (`tools/run-tests.sh --tier fast --no-build`): **13,947 tests, 625.5 s, FAIL** — 3 failures in
`Allegiance.Core.Tests`, 1 in `Allegiance.ClientCore.Tests`, 4 slow-Fast offenders, host shared with other lanes
(load average ≈ 9). The flag is **off by default**, so the swept code path is the pre-P991 wire; the only
non-doc change on the flag-off path is that the status encoders now build from one shared value-struct.

| Case | Sweep signature | Isolated re-runs |
|---|---|---|
| `ProbeCombatLiveTests.Lethal_hit_kills_probe_removes_from_sector_and_broadcasts_destroy_to_same_side_peer` | 17.064 s (slow-Fast offender) and the destroy-broadcast receipt missing | **3/3 pass** (1 s each) — load-sensitive receipt family |
| `ArmedProbeLiveTests.Armed_probe_spawns_hits_and_damages_enemy_with_ammo_decrement_live` | 37.396 s (slow-Fast offender) and the ammo/damage receipt missing | **3/3 pass** (1 s each) — same receipt family |
| `LiveSideSelectTests.Flag_on_auto_seats_a_silent_peer_at_the_configured_30_seconds` | 824 ms; the ledgered receipt/simulation-clock boundary (clean-base rate 2 pass / 3 fail) | **1 pass / 2 fail** alone — the long-ledgered auto-seat row above, no side-select/auto-seat code changed |
| `P890OwnAimLoopbackTests.Turning_and_firing_preserve_client_aim(training: False, model: "native")` | 324 ms; aim/turn receipt | **3/3 pass** (4 cases each) — load flake |
| slow-Fast: `P750NativeAsteroidTypesTests…` 11.173 s, `ArmedProbeLiveTests…` 37.396 s, `P777WarpExitWobbleTests…` 10.106 s, `ProbeCombatLiveTests…` 17.064 s | assertions passed in the isolated re-runs | the ledgered near-limit families |

Showcase never-parks (`P700ShowcaseLiveTests.Local_showcase_never_parks_with_target_for_five_seconds`), five
sequential runs per arm on this tree (`ALLEGIANCE_BROADCAST_SHIP_STATUS_CHANGE_GATE=0` / `=1`), one isolated
recheck per arm after the shared failure:

| Arm | Runs (wall) | Outcome |
|---|---|---|
| flag OFF (base rate) | 326 s, **486 s FAIL**, 339 s, 144 s, 183 s + recheck 294 s | **5 pass / 1 fail**; park-limit violations 0 in every run |
| flag ON | 199 s, **485 s FAIL**, 214 s, 387 s, 251 s + recheck 437 s | **5 pass / 1 fail**; park-limit violations 0 in every run |

The one failure landed on the **same** run in **both** arms (8 m 04 s / 8 m 05 s): the arrival assertion at the end
of the fixed 480 s observation window, i.e. the already-recorded P700 arrival-time flake ("20/30 reached at
480.1 seconds", the row above), not a parking violation and not a difference between the arms. Both rechecks pass.
The two-match 2-side showcase `P730ShowcaseLiveTests.Two_local_showcase_matches_keep_mining_research_kills_pods_and_relaunch`
(30 bots, kills/pods/relaunch/mining/research) passes with the gate **ON** in 4 m 33 s.

### P991 review sweep (Claude, Core.Tests `Tier=Fast`, run alongside `tools/build-tools.sh`)

| Test | 3x alone | Classification |
|---|---|---|
| `P739TimerLiveTests.Warp_bomb_notice_matches_captured_blast_deadline_and_stays_in_mission(delay: 7)` | 3/0, 1/2, 3/0 (theory cases) | Existing floating deadline-boundary family (entries above); timer code unchanged, gate off in this fixture. |
| `LiveSideSelectTests.Flag_on_auto_seats_a_silent_peer_at_the_configured_30_seconds` | fail / pass / fail | Existing auto-seat receipt/simulation-clock boundary (clean-base 2 pass / 3 fail recorded above). |
| `NpcWingmanLeadLiveTests.Wingman_fire_gate_leads_a_laterally_moving_enemy`, `NpcDodgeLiveTests.Traveling_npc_does_not_dodge_the_station_it_is_docking_at`, `GameOverPlayersWireTests.Rejoined_player_accumulates_time_across_stints_in_roster` | 3/3 pass each | Load-only in a sweep that shared the host with the tools build; none touches SHIP_STATUS with the gate on (all default off, wire pinned byte-identical). |

### P996 sweep (Core + ClientCore + MonoGame Fast tiers, one run each, shared host)

`tools/run-tests.sh --tier fast --no-build` (13966 tests, 572.9 s, exit 1). Three main-lane failures, all
non-task; each re-run alone 3×:

| Test | Sweep signature | 3× alone | Classification |
|---|---|---|---|
| `MissileLockLiveTests.MissileLock_BuildsPartialThenFull_AndPosts_Tips_OverTheWire` | 38.4 s, and a SLOW FAST offender | **0 pass / 3 fail**, 38 s each — the ledgered partial-tip receipt (clean base 1 pass / 2 fail, P966 row) | Existing, no missile/lock/tip source on the P996 path |
| `LiveSideSelectTests.Flag_on_auto_seats_a_silent_peer_at_the_configured_30_seconds` | 802 ms; `32.1000000000003` against the fixture's [28, 31] | **0 pass / 3 fail**, same 32.1 s one-second boundary (clean base 2 pass / 3 fail, P966/P991 rows) | Existing auto-seat receipt/simulation-clock boundary; no side-select/auto-seat source changed |
| `P832aMissionRulesInventoryTests.Every_native_mission_body_handler_and_local_rule_helper_has_a_matrix_row` | "stale bodies: [CshipIGC::SetBaseHullType …, CshipIGC::PurchaseShipLoadout …]" | **task failure, fixed**: the two new parity rows were in the audited `FedSrv mission rules` family with prose `native_body` values; SIM-0859 moved to the `updates` family with its real body key and SIM-0860 moved to `p977-eject-rescue.csv`. Re-checked: `p832a-mission-rules-audit.py --check` clean, the test passes 1/1 | mine |

SLOW FAST offenders of the same led families: `MissileLockLiveTests` (38.369 s) and
`P777WarpExitWobbleTests.Npc_exit_touch_rate_is_measured` (10.055 s vs the 10.000 s limit). No P996 test was a
timing offender, and no P996 test failed in the sweep.

**P996 sweep 2** (`tools/run-tests.sh --tier fast --no-build`, same tree, after the parity-row fix: 13 966 tests,
575.6 s, exit 1). Two main-lane failures, both already-ledgered load-sensitive families; each re-run 3× alone:

| Test | Sweep signature | 3× alone | Classification |
|---|---|---|---|
| `P902SoundTemplateTests.Asr_and_cache_hit_mix_math_allocate_nothing_after_warmup` | `Assert.Equal` 0 vs **1 264** allocated bytes | **3/3 pass** (11-14 ms each) | Ledgered allocation intermittency (two earlier entries, both 3/3 pass alone); no sound/mix code on the P996 path |
| `LiveSideSelectTests.Flag_on_auto_seats_a_silent_peer_at_the_configured_30_seconds` | 32.45 s against the fixture's [28, 31] | **0/3** at 32.1 s (sweep 1 cohort) | Same long-ledgered auto-seat receipt/simulation-clock boundary as sweep 1 above |

`MissileLockLiveTests` passed in this sweep (1 s); the only slow-Fast offender was
`P777WarpExitWobbleTests.Npc_exit_touch_rate_is_measured` (10.477 s vs 10.000 s). No P996 test failed in either
sweep.

## P994 — compact gunfire: single Fast sweep qualification

The single Fast invocation completed **13,994 cases in 582.7 s**:
**13,985 passed / 6 failed / 3 optional private-pack skips**; nine infrastructure
checks passed. Gunfire batching was default OFF during the sweep, with its
legacy wire byte-pinned. The task-owned dispatch-registry count failure was a
missed audit update (13/113 → 14/114 after adding fmid 221), not a flake; it is
fixed, and the full three-test dispatch coverage class passed **3/0/0 ×3**.

The remaining failures and timing offender were repeated in three fresh,
isolated processes:

| Case | Sweep signature | Isolated outcome |
|---|---|---|
| `P761StartChatComposeTests.ProfileEditor_OwnsEnterAndBackspace_AndKeepsChatClosed` | Default profile restoration hits the sandbox's read-only home config | **pass / pass / pass**, with writable task-local `XDG_CONFIG_HOME`; existing sandbox fixture signature, no profile code changed |
| `MissileLockLiveTests.MissileLock_BuildsPartialThenFull_AndPosts_Tips_OverTheWire` | lock 1.000 with no partial tip, 39.126 s | **fail / fail / fail**, 38.380 / 38.342 / 38.309 s; existing OPEN receipt family, no missile/lock code changed |
| `LiveSideSelectTests.Flag_on_auto_seats_a_silent_peer_at_the_configured_30_seconds` | 32.55 sim seconds versus [28,31] bound | **fail / fail / fail**; existing OPEN auto-seat boundary family, no side-select code changed |
| `NpcPilotTypeGateLiveTests.Non_miner_idle_npc_stands_by_despite_minable_asteroid` | travelled 83.531 units | **pass / pass / pass**, 2.383 / 2.313 / 2.300 s; existing load-sensitive idle-NPC family |
| `GameOverPlayersWireTests.Rejoined_player_accumulates_time_across_stints_in_roster` | 8.04999924 versus upper bound 8 | **pass / pass / pass**, 0.610 / 0.561 / 0.638 s; existing stint-timing family, no scoring code changed |
| `P777WarpExitWobbleTests.Npc_exit_touch_rate_is_measured` | Body passes; 10.408 s exceeds calibrated 10 s | **pass / pass / pass**, 9.131 / 9.186 / 9.170 s, all below the unchanged limit |

The three skips are the optional private Meshy pack checks. All 45 new Core
cases passed **45/0/0 ×3**; the MCP metric/attacker sampler and seeded live
showcase passed **3/0/0 ×3**, including nonzero shots, hits and kills. Native
artwork and source paths were supplied through the worktree's `local.env`.
Evidence: `.scratch/p994/{fast,qualifications,neverparks}/`; task report:
`docs/reports/GUNFIRE_BATCH_P994.md`. Never-parks completed **OFF 5/5 and ON 5/5**,
zero skips and zero parking violations. Longest low-speed-with-target intervals
were **3.306 s OFF / 3.009 s ON**, below the unchanged five-second limit. The
per-run arrival, shot/hit/kill and wall-time receipts are in the task report.


### P993 sweep (distance-cull the `PROJECTILE_INSTANCE(12)` relay; `broadcast.fireAudienceCull` default off)

One full Fast tier (`tools/run-tests.sh --tier fast --no-build`, 13 969 tests, 569 s, load avg ~1) after
`dotnet build AllegianceCore.slnx` 0/0 and `tools/build-tools.sh` (130/130). Three non-task failures + the
`SLOW FAST` overruns, each re-run alone and each already ledgered above with the same signature:

| Test | Sweep | 3× alone (this tree) | Control | Classification |
|---|---|---|---|---|
| `MissileLockLiveTests.MissileLock_BuildsPartialThenFull_AndPosts_Tips_OverTheWire` (38.1 s, `lock never reached full + partial tip (lock 1.000)`) | fail | **0 pass / 3 fail**, 38 s each | **pristine base commit (`git archive bdbf8423`, same test file): 0 pass / 2 fail, 38 s each** | the ledgered P933/P935/P966 partial-tip receipt family (clean-base 1/2 recorded above); no missile/lock/tip source on the P993 path and the flag is off in the sweep |
| `P739TimerLiveTests.Warp_bomb_notice_matches_captured_blast_deadline_and_stays_in_mission(delay: 5,7)` | fail | **0/4 theory cases pass** on the widest run, values `5.0000000000000009` / `7.0000000000000009` against the inclusive `(delay−0.1, delay]` bound | base commit 4/4 pass in one run, the same one-ULP boundary is recorded in the entries above | the existing floating upper-bound family (P966/P977 rows: `5.0000000000000009 > 5`); no timer/Core code from P993 is on the deadline path |
| `LiveSideSelectTests.Flag_on_auto_seats_a_silent_peer_at_the_configured_30_seconds` | fail (32.5 s vs `[28,31]`) | 3 runs alone: 32.15–32.60 s, the ledgered receipt/clock boundary | base commit: same failure (32.45 s) in one of two runs | the existing auto-seat receipt/simulation-clock boundary (P945/P948/P953/P960/P966/P967/P977 entries); no side-select/auto-seat source changed |
| `SLOW FAST` 38.149 s `MissileLock…`, 10.357 s `P777WarpExitWobbleTests…`, 10.012 s `P942ResearchOrderingTests…(civ 201)` | 3 executions over the calibrated 10 s limit | the missile-lock one is the ledgered family above; the P777/P942 near-limit rows are already ledgered (P750 cohort) | — | the shared sweep runner was the only load (other lanes idle); re-running the whole tier is the only way past the fixed 10 s limit — these are the ledgered near-limit families, all < 1 s alone |

Showcase never-parks (`P700ShowcaseLiveTests.Local_showcase_never_parks_with_target_for_five_seconds`),
five sequential runs per arm on this tree, one isolated recheck for the failing arm (load avg 1.5–2.3):

| Arm | Runs (wall) | Outcome |
|---|---|---|
| flag OFF (`broadcast.fireAudienceCull` default) | 273 s, 193 s, 254 s, 185 s, 199 s | **5 pass / 0 fail**; zero park-limit violations |
| flag ON (`ALLEGIANCE_BROADCAST_FIRE_AUDIENCE_CULL=1`) | 485 s, 463 s, 184 s, 299 s, 356 s | **1 fail / 4 pass** in the sweep — the 8 m 04 s run is the already-recorded P700 arrival-time flake (the row above: "20/30 reached at 480.1 seconds", same 8 m 04 s signature as the P991 cohort, both arms there); recheck **pass 214 s**; zero park-limit violations in every run |

No new flake family from P993: the flag changes only which (viewer, shooter) pairs receive `12` frames, and
both arms log the same bot hits/kills, the same `[proj-diag]` shape and zero parks.

### 2026-11-11 — gunfire batch default ON (Claude), fast suite after the flip

| Test | Result | Isolation |
|---|---|---|
| `LiveSideSelectTests.Flag_on_auto_seats_a_silent_peer_at_the_configured_30_seconds` | 32.3 s against [28,31], also when co-run with `MissileBroadcastTests`/`P994GunfireBatchTests` | passes alone with batching on and off; server log seats at 30 s — the ledgered FltBot receipt/simulation-clock boundary under co-running load |
| `MissileLockLiveTests.MissileLock_BuildsPartialThenFull_AndPosts_Tips_OverTheWire` | failed, 38 s | already ledgered by P997 (fails alone on the clean base) |
| `ThreePeerSeededSessionTests.SeedThreePeers_TwoSameSide_OneCrossSide_DeterministicVisibility`, `ShopLauncherMountNumberingLiveTests.ShopBuyOrder_LauncherGrantedAtMount0_LocksAndFires_Live`, `MissileBroadcastTests.Directly_fired_missile_broadcasts_create_to_teammate_and_destroy_to_whole_mission` | failed once in the sweep | 3/3 pass alone with batching default ON |

### 2026-11-11 — P998 dead-ship part strip, fast suite (`tools/test-tiers/runner.py --tier fast --no-build`)

One full Fast tier on the P998 tree (14 072 tests, 571.3 s, load avg ~1.5–2 with other lanes active):
14 066 passed, 4 failed, 3 `NotExecuted` (the private-Meshy-pack skips), 2 `SLOW FAST` executions over the
calibrated 10 s limit. Every failure is already ledgered above with the same signature; each was re-run alone
on this tree:

| Test | Sweep | 3× alone (this tree) | Classification |
|---|---|---|---|
| `DeadLatchRespawnLiveTests.Remote_client_sees_victim_again_after_death_and_respawn` (5 s, "B never saw A's death (IsDead never latched on B)") | fail | **3 pass / 0 fail**, ~1 s each | the long-ledgered asynchronous remote death-receipt intermittency (P892c/P896/P899/P903/P996 rows); the fixture kills immediately after seating and allows only a 2,500-tick window (`DeadLatchRespawnLiveTests.cs:432-440`), and P998 does not touch the death latch, KILL_SHIP or EXPORT paths |
| `P739TimerLiveTests.Warp_bomb_notice_matches_captured_blast_deadline_and_stays_in_mission(delay: 5)` | fail (`5.0000000000000009` vs the inclusive `(4.9, 5]` bound) | **2 runs 3/3 pass, 1 run 2/3** (same delay:5 one-ULP value) | the existing floating upper-bound family (P966/P977/P993 rows); no timer/constant code on the P998 path |
| `LiveSideSelectTests.Flag_on_auto_seats_a_silent_peer_at_the_configured_30_seconds` | fail (32.4 s vs `[28,31]`) | **1 pass / 2 fail**, 32.15 / 32.20 s — the exact ledgered boundary | the existing auto-seat receipt/simulation-clock boundary (P945…P997 rows); no side-select/auto-seat code changed |
| `MissileLockLiveTests.MissileLock_BuildsPartialThenFull_AndPosts_Tips_OverTheWire` (38.2 s) | fail | **0 pass / 3 fail**, 38 s each | the ledgered persistent partial-tip receipt family (P933/P935/P966/P993/P997 rows: fails alone on the clean base too); it is also one of the two `SLOW FAST` executions (38.178 s), the other being the ledgered near-limit `P777WarpExitWobbleTests.Npc_exit_touch_rate_is_measured` (10.566 s) |

Showcase never-parks (`P700ShowcaseLiveTests.Local_showcase_never_parks_with_target_for_five_seconds`) on this
tree: **five sequential runs, 5 pass / 0 fail** (278 s, 176 s, 165 s, 188 s, 360 s), plus two instrumented
re-runs logging `P700 local 15v15: 171.3s, reached=24/30, longest speed<1 with target=1.907s` and
`261.4s, reached=24/30, longest=0.407s` — zero park-limit violations (limit 5 s), which is at or above the
ledgered base rate. Extra Live check for the bots path P998 touches: `P996BotsLifepodNoOxygenLoopLiveTests`
**pass** (seed 996, pods drifting 13, pod deaths 53, oxygen deaths 19, minimum launch→oxygen-death gap 14.55 s).


### 2026-11-12 — P1000 observation-only civilization profiles

The corrected full Fast sweep ran 14,116 cases in 541.1s: **14,090 passed,
6 failed, 20 skipped**, plus four calibrated duration overruns (limit 11.554s).
An earlier diagnostic sweep was 14,077/19/20; twelve task regressions were fixed
before the final build and all final qualifications (unbooted-server config
access and a strategy-receipt JSON default). Both runs remain in the task report.
Every non-task failure or slow method from either sweep was then run **three
times alone on each tree**, alternating a `git archive 67c950eb` base and this
tree, with artwork and the new flag OFF. Each cell below lists all three runs as
**Passed/Failed/Skipped**; theory methods include every parameter case.

| Case | Base: passed/failed/skipped per run | Changed: passed/failed/skipped per run |
|---|---|---|
| `WorldPopulationLiveTests.PeersLearnSameSideTeammateShip_AndWorldPopulation_FromRealServer` | 1/0/0; 1/0/0; 1/0/0 | 1/0/0; 1/0/0; 1/0/0 |
| `P660NextMatchLiveTests.Late_game_over_rebuild_ready_countdown_started_has_no_clock_jump` | 4/0/0; 4/0/0; 3/1/0 | 3/1/0; 4/0/0; 3/1/0 |
| `MissileLockLiveTests.MissileLock_BuildsPartialThenFull_AndPosts_Tips_OverTheWire` | 0/1/0; 0/1/0; 0/1/0 | 0/1/0; 0/1/0; 0/1/0 |
| `GameOverPlayersWireTests.Rejoined_player_accumulates_time_across_stints_in_roster` | 0/1/0; 0/1/0; 1/0/0 | 0/1/0; 1/0/0; 0/1/0 |
| `MiningStateWireTests.Loopback_own_mining_state_reaches_client_and_clears_on_departure` | 1/0/0; 1/0/0; 1/0/0 | 1/0/0; 1/0/0; 1/0/0 |
| `LiveShieldRegenBroadcastTests.Stationary_ship_streams_its_regenerating_shield_over_the_wire` | 1/0/0; 1/0/0; 1/0/0 | 1/0/0; 1/0/0; 1/0/0 |
| `LiveSideSelectTests.Flag_on_auto_seats_a_silent_peer_at_the_configured_30_seconds` | 0/1/0; 0/1/0; 0/1/0 | 0/1/0; 1/0/0; 0/1/0 |
| `SC2NaniteSupportTests.Support_selection_obeys_research_dock_budget_and_mounts` | 2/0/0; 2/0/0; 2/0/0 | 2/0/0; 2/0/0; 2/0/0 |
| `ShopLauncherMountNumberingLiveTests.ShopBuyOrder_LauncherGrantedAtMount0_LocksAndFires_Live` | 1/0/0; 1/0/0; 1/0/0 | 1/0/0; 1/0/0; 1/0/0 |
| `P877MineDamageLiveTests.Chased_bot_wake_mine_damages_pursuer` | 1/0/0; 1/0/0; 1/0/0 | 1/0/0; 1/0/0; 1/0/0 |
| `DeadLatchRespawnLiveTests.Remote_client_sees_victim_again_after_death_and_respawn` | 1/0/0; 1/0/0; 1/0/0 | 1/0/0; 1/0/0; 1/0/0 |
| `WarpTransitServerTests.ShipInsideWarp_GetsTransited_AndPeerSeesSectorChange` | 1/0/0; 1/0/0; 1/0/0 | 1/0/0; 1/0/0; 1/0/0 |
| `P796VariedLoadoutTests.Varied_equips_every_legal_hull_and_part_of_the_role_over_many_purchases` | 3/0/0; 3/0/0; 3/0/0 | 3/0/0; 3/0/0; 3/0/0 |
| `P999CivStrategyTests.Every_profile_is_reachable_in_catalog_and_actual_side_buy_gates` | 10/0/0; 10/0/0; 10/0/0 | 10/0/0; 10/0/0; 10/0/0 |

Missile-lock reproduces the existing full-lock-without-partial-tip failure on
both trees (38s each). Roster accumulated time exceeds the fixture's [4,8]s
window intermittently on both (base failures 8.10s/8.35s; changed 8.30s/8.45s).
Auto-seat similarly reproduces the existing [28,31]s receipt boundary on both;
P660's reset countdown can arrive at 14.85–14.90s against [14.94,15.06]s on
both. These are reproduced base failures, not new strategy-dependent behavior.

The other sweep failures pass all isolated base/current runs: teammate population
(side -2 before admission settles), mining-state receipt, live shield receipt,
launcher mount's default ExpandedPartData before its acknowledgement, P877 mine
loadout receipt timeout, remote death latch (2,500-tick window) and warp own-status
receipt. They match the asynchronous receipt families already recorded above.
The slow Nanite support, varied-loadout and P999 reachability methods pass every
isolated parameter case. Their individual durations and all assertions are kept
in task-local TRX evidence; they are not reclassified as failed assertions.
The corrected sweep's four overruns were missile lock 38.132s, P877 mine 30.664s,
varied-loadout civ 35 12.207s and P999 civ 5 16.603s.

The Fast gate remains failed; isolation documents it and does not waive it.
Autonomous never-parks base/OFF/ON qualification: **5/5 pass per arm**, fifteen
original trials with no retries and zero parking-limit violations. Longest parked
intervals: base 2.804s, OFF 2.005s, ON 4.007s, all below 5s. The randomized fixture
is a base-rate check; the separately seeded P1000 replay pins exact wire bytes.
Per-trial arrival/combat/time receipts are in the P1000 report.

### 2026-11-12 — `P700ShowcaseLiveTests.P994_seeded_batched_showcase_still_hits_and_kills` (Live) — arrival miss on base

Failed once in the v0.1.6 release sweep (`232a0c87`, lane-runner, 8 m 3 s): *"Most showcase ships must reach their
objectives"* (`P700ShowcaseLiveTests.cs:135`). Three alternating isolated runs per tree on an idle 8-core lane:

| Tree | Run 1 | Run 2 | Run 3 |
|---|---|---|---|
| `16e166a1` (before P1001) | Failed (arrival) | Failed (arrival) | Passed |
| `232a0c87` (release candidate) | Passed | Passed | Passed |

The miss reproduces on the pre-P1001 base (2/3), so it is an existing intermittent arrival outcome of the seeded batched
showcase, not a P1001 regression; same family as P1001's ledgered OFF arrival observation. Kept as a known Live flake;
a dedicated arrival-robustness task can pick it up.

### 2026-11-12 — gunfire/broadcast fixtures failing since gunfire batching became the default

`P992GunfireTrafficTests.Gunfire_fixture_measurement_from_environment`,
`P992GunfireTrafficTests.Loopback_showcase_measures_gunfire_traffic_and_the_audience_radius` and
`P990BroadcastLaneFixtureTests.Mixed_lane_overhead_stays_within_30ns_per_call_and_three_percent_of_the_p95_tick` fail on
every host and tree since `broadcast.gunfireBatch` default ON (`1f1ebe3a`): the 2026-11-12 03:01 nightly on
`67c950eb` (before P1000), the four-host benchmark the same day, and the v0.1.6 release sweep (`232a0c87`). Not caused by
P1000/P1001.

P1002 isolated controls on base `61a50173`: both P992 fixtures failed **3/3 ON**
and passed **3/3 OFF**. Resolved by measuring delivered shot/hit records inside
ID-221 batches separately from transport messages, and running the simulation
inside the recorded tick (the old fixture exercised singleton admin sends).
The original shot/hit, radius, legacy-stock dependency and reconciliation bounds
are retained. No production behavior/default change. Corrected rig-shaped ON
traffic: 241,668 shot + 197,280 hit deliveries in 36,579 batches, 14,363,628
gunfire bytes versus 23,486,184 legacy bytes (−38.84%).

**P990 remains open; not a batching re-baseline.** It failed **3/3 ON and 3/3
OFF** before edits. ON per-call cost was 31.6/32.2/32.4 ns (p95 share
4.783/4.224/4.759%); OFF was 33.7/33.2/32.3 ns (5.240/5.215/4.791%). Its timed
49-call/peer workload contains status/export frames and pose stamps, no gunfire,
and its preliminary projection uses unarmed ships. Both original bounds
(30 ns/call, 3% of tick p95) remain unchanged. Disabling JIT tiering in an extra
OFF diagnostic still failed the share bound (0.2116 ms, 4.242%); no production
regression or safe small fix was established. Investigate stable warm-up and
host/build comparability separately. Full evidence and final gate:
`docs/reports/GUNFIRE_FIXTURE_REBASELINE_P1002.md`.

### 2026-11-13 — P1002 Fast qualification (fixture-only changes)

One full Fast run on `61a50173` plus P1002 test/documentation changes:
**14,125 passed / 4 failed / 20 skipped**, 523.847 s. All 14,149 discovered
cases ran; 9/9 infrastructure checks and both quarantine lanes passed.
The runner remains **FAIL**, including four timing offenders against its
11.497082 s calibrated limit. The changed P992 measurement methods are Live
and P990 is Perf; neither runs in Fast. No production source changed.

Every failed case and additional slow case was selected in a fresh process
three times after the sweep. P660 ran its whole four-case method per invocation
so the disabled/off case was covered each time. Other selections ran exactly
one case per invocation. Durations below are TRX test times, not process times.

| Case | Fast failure / timing signature | Three isolated repetitions |
|---|---|---|
| `P660NextMatchLiveTests.Late_game_over_rebuild_ready_countdown_started_has_no_clock_jump` (disabled/off) | 14.900000000054206 s against [14.94, 15.06], at line 134; recurring countdown receipt family | 4/0/0; 4/0/0; 4/0/0 (Passed/Failed/Skipped), all four cases per invocation |
| `MissileLockLiveTests.MissileLock_BuildsPartialThenFull_AndPosts_Tips_OverTheWire` | lock 1.000 without required partial tip, 37.849 s; also slow-Fast | **fail / fail / fail**, 38.652 / 38.593 / 38.587 s; existing OPEN partial-tip issue remains open |
| `NpcDodgeLiveTests.Traveling_npc_steers_around_an_asteroid_on_its_path_and_resumes` | clearance assertion at line 258, minRockDist=70.2 | **pass / pass / fail**, 5.874 / 5.808 / 5.775 s; failed repeat minRockDist=68.6; clearance issue remains OPEN, not just suite load |
| `DropPartTests.DropPart_drops_weapon_sectorwide_with_treasure_then_empty_pack_skips` | line 340 expected empty subject treasure list; one weapon treasure (TreasureId=3, Amount=0) remained after empty-pack phase | **pass / pass / pass**, 0.453 / 0.453 / 0.451 s; receive/reset race suspected: phase one waits for observer/enemy treasure, but not the subject's own treasure before clearing its list (lines 254–261, 325, 340). No drop behavior changed |
| `P796VariedLoadoutTests.Varied_equips_every_legal_hull_and_part_of_the_role_over_many_purchases` (civ 35) | assertions passed, 11.725 s | **pass / pass / pass**, 3.476 / 3.286 / 3.286 s, below the original limit |
| `SC2NaniteSupportTests.Support_selection_obeys_research_dock_budget_and_mounts` (civ 25) | assertions passed, 11.637 s | **pass / pass / pass**, 3.032 / 3.004 / 2.987 s, below the original limit |
| `P999CivStrategyTests.Every_profile_is_reachable_in_catalog_and_actual_side_buy_gates` (civ 5) | assertions passed, 13.456 s | **pass / pass / pass**, 2.916 / 2.962 / 2.971 s, below the original limit |

Follow-up total: **26 passed / 4 failed / 0 skipped**, 21 process invocations.
This does not turn the Fast run green or clear the missile-lock/NPC failures.
P1002 report: `docs/reports/GUNFIRE_FIXTURE_REBASELINE_P1002.md`.

### 2026-11-13 — P1005 Fast qualification (client asteroid-spin clock)

Two full Fast runs on the P1005 tree (base `a87b00f3`, client asteroid-spin clock
change only — no server, sim, wire or HUD path):

- run A (pre-final replay-clock line): **14,140 passed / 2 failed / 20 skipped**
  of 14,162 cases, 536.962 s;
- run B (final tree): **14,137 passed / 5 failed / 20 skipped**, 608.589 s.

9/9 infrastructure checks and both quarantine lanes passed in both runs. The
runner stays **FAIL**, on the ledgered families below and on timing offenders
against the 11.535 s calibrated limit; host load average was 11–21 on the shared
24-CPU host (run B peaked at 20.7). Every case run in isolation below used a
fresh process, one case per invocation unless stated.

| Case | Runs A / B signature | Isolated repetitions |
|---|---|---|
| `MissileLockLiveTests.MissileLock_BuildsPartialThenFull_AndPosts_Tips_OverTheWire` | fail / fail — lock 1.000 without the required partial tip, 37.8 / 38.2 s | **1 pass / 2 fail** (38 s, 38 s, 1 s): the OPEN partial-tip receipt failure ledgered since P933/P935/P966, not load |
| `LiveSideSelectTests.Flag_on_auto_seats_a_silent_peer_at_the_configured_30_seconds` | pass / fail — the 32 s auto-seat boundary signature | **1 pass / 5 fail** over 6 isolated runs at load 11–21; the ledgered auto-seat receipt/simulation-clock boundary family (42 prior entries, also `fail/fail/fail` cohorts) |
| `GameOverPlayersWireTests.Rejoined_player_accumulates_time_across_stints_in_roster` | pass / fail | **1 pass / 2 fail** (~0.6 s); ledgered roster-stint recommendation intermittency |
| `P739TimerLiveTests.Warp_bomb_notice_matches_captured_blast_deadline_and_stays_in_mission` | pass / fail | **2 pass / 1 fail** (~1 s, 3 cases each); ledgered floating upper-bound signature (ledger records 1/2/0 and 2/1/0 cohorts) |
| `AsteroidCombatLiveTests.Same_side_friendly_missile_damages_the_asteroid_no_friendly_fire_gate` | pass / fail (no rendering involved) | **pass / pass / pass** (~0.5 s) |
| `DeadLatchRespawnLiveTests.Remote_client_sees_victim_again_after_death_and_respawn` | fail / pass | **pass / pass / pass** (~5 s) — ledgered load-only asynchronous death-receipt intermittency |
| `P796VariedLoadoutTests.Varied_equips_every_legal_hull_and_part_of_the_role_over_many_purchases` (civ 35) | passed, 13.5 / 15.9 s | pass / pass / pass |
| `SC2NaniteSupportTests.Support_selection_obeys_research_dock_budget_and_mounts` (civ 25) | passed, 12.1 / 14.5 s | pass / pass / pass |
| `P814GunnerTests.P814b_TurretCarrier_pref_keeps_or_beats_the_Best_hulls_turret_seats` | passed, 14.4 / 15.7 s | pass / pass / pass |
| `P999CivStrategyTests.Every_profile_is_reachable_in_catalog_and_actual_side_buy_gates` (civ 5) | passed, 19.6 / 25.6 s | pass / pass / pass (10 cases, 32–37 s per invocation) |

No failed or slow case touches the asteroid spin path; the P1005 client change
covers rendering only and its new tests are Fast (13 cases, 20–35 ms). Report:
`docs/reports/ASTEROID_SPIN_RATE_P1005.md`.

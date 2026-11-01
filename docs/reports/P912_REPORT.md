# P912 — training universes and real-window lessons

Working-tree delivery; no commit, push, stash, or remote-host access. Final validation is recorded below. Native references were re-read in the local read-only native source. Raw receipts remain under `.scratch/p912/` because the sandbox permits evidence writes only inside this worktree.

## Changes

M2 receives a private copy of its enlarged universe constants. M8 receives the authored start position, Nanite 2 cargo, proximity-mine loadout and side tech. M5 uses the actual Mars station, native cube placement and warp-relative scout geometry. Explicit training Do Nothing commands survive the NPC default-order pass.

Connected M5/M8 drones now execute real simulation plans. Local Defend chat arms the live escort plan and replaces a prior Goto, matching clintlib/clintlib.cpp:2374-2417 and Igc/shipIGC.h:766-772. Tower layers deploy actual probes; miners mine and travel; fighters and bombers attack real targets. Station combat supports the same projectile path as ship combat. Predictable map objects receive native local-client visibility (WinTrek/trekigc.cpp:1996-2010), and ship-targeted Goto retains a live model target (Igc/shipIGC.cpp:3995-4010) instead of freezing an escort waypoint. M8’s first bomber waypoint is in the home sector, as authored. Cluster transfers rebase the model clock and clear weapon fire exactly as `Igc/shipIGC.h:235-242` specifies. The arrival-sector update then integrates exit motion before testing the next swept collision. A real-aleph escort regression fails in the old handoff (returns to sector 1051) and passes with the native handoff (one crossing, reaches the bomber in 1052). Red/green receipts: `.scratch/p912/escort-{red,green}/result.trx`.

The command-view client uses the ship’s real docked state even when training hides the hangar. Confirmed remote-sector previews stream actual loopback poses without moving the trainee. Pointer picks retain native y-up coordinates, empty-space clicks and height drags create ordinary waypoint orders, Ctrl+1 and tilde follow the native training swap/quickcomm shortcuts, and the shared team pane includes friendly/spotted drones while the scoreboard retains its human roster.

Quickcomm consumes the numeric selection frame after closing its menu, preventing the selection from changing weapon mode. M3 restores mount zero with single fire (training/mission3.cpp:184-186); M4/M8 restore native group fire on setup while leaving the simulation trigger released (Igc/shipIGC.cpp:30; mission4.cpp:127-128; mission8.cpp:90). Explicit F3 command-view input also restores the command bar after a scripted cockpit mask (WinTrek/WinTrek.cpp:6833-6843,9894-9932); both styles have a regression for this transition. Probe-count observation is synchronized with the host ticker. Mission startup also holds a consistent replicated world through readiness and native goal construction (training/trainingmission.cpp:153-164).

The M8 replay uses ordinary command-view Goto orders to bring the existing friendly fighters along, then Defend orders after the bomber takes its required first hull hit. Native step 3.18 waits for that hit (training/mission8.cpp:581-593): an early-defense diagnostic killed all three attackers while the bomber remained full and left that condition waiting. The native condition is preserved.

A qualification M5 Modern replay exposed a pilot click landing under the minimap: the first randomly placed He3 asteroid was occluded by the interface. The pilot now chooses an on-screen, unobstructed He3 rock through the same real picking path, zooming out with normal input when necessary. The interrupted attempt remains in `.scratch/p912/training-20261101T142307-22650/`; it is not counted as a green training pass. All three isolated M5 Modern correction runs passed (`.scratch/p912/qualification/window-m5-Modern-map-{1,2,3}/result.trx`).

The opt-in proof driver feeds keys, pointer presses/releases and text through Game1 input. Its observer reads state and records lesson boundaries and real outcomes. It does not complete goals, inject damage, move actors, create stations or force docking. Universe/binding regressions separately arrange model state to test geometry and constants.

The native magazine finding comes from `training/trainingmission.cpp:819-820` (default loadout), `clintlib/clintlib.h:2233-2245` (five cargo purchases), `Igc/igc.h:1002` (PCore014), and `training/mission3.cpp:177-180` (mounted magazine removal). M8’s part-200-only nerf is `training/CreateDroneAction.cpp:125-130`; the actual available-tech loadout carries part 201. The native helper search found only `training/trainingmission.h:80` and the definition at `training/trainingmission.cpp:905`.

## Row audit

W = `P912TrainingWindowTests.Every_lesson_completes_in_one_real_window_session`; U = `P912TrainingUniverseTests`. All source paths below are repository-relative. Each W session starts with the first goal and retains state through the final goal.

| Row | Verdict / exact remaining gap | Native | Production port | Regression / receipt |
|---|---|---|---|---|
| TRN-0106 | Partial — native PCore014 fills all five cargo slots before removing the mounted magazine. The authored lesson-9 swap has no magazine to mount. Missile launch/kill is therefore unavailable in the native starting state; the window uses the native-legal gunfire ending. Preserve this native bug. | `training/mission3.cpp:905-991` | `src/Allegiance.ClientCore/Training/Mission3.cs:629` | W M3; U.Mission2_bounds_are_private_and_Mission3_preserves_native_full_cargo_swap_bug |
| TRN-0107 | Validation pending | `training/mission3.cpp:992-1062` | `src/Allegiance.ClientCore/Training/Mission3.cs:688` | W M3; U.Mission2_bounds_are_private_and_Mission3_preserves_native_full_cargo_swap_bug |
| TRN-0108 | Validation pending | `training/mission3.cpp:1063-1104` | `src/Allegiance.ClientCore/Training/Mission3.cs:733` | W M3; U.Mission2_bounds_are_private_and_Mission3_preserves_native_full_cargo_swap_bug |
| TRN-0111 | Validation pending | `training/mission4.cpp:141` | `src/Allegiance.ClientCore/Training/Mission4.cs:148` | W M4 (all ten lesson boundaries; native miner kill/departure receipt) |
| TRN-0112 | Validation pending | `training/mission4.cpp:171-215` | `src/Allegiance.ClientCore/Training/Mission4.cs:177` | W M4 (all ten lesson boundaries; native miner kill/departure receipt) |
| TRN-0113 | Validation pending | `training/mission4.cpp:216-312` | `src/Allegiance.ClientCore/Training/Mission4.cs:209` | W M4 (all ten lesson boundaries; native miner kill/departure receipt) |
| TRN-0114 | Validation pending | `training/mission4.cpp:313-377` | `src/Allegiance.ClientCore/Training/Mission4.cs:273` | W M4 (all ten lesson boundaries; native miner kill/departure receipt) |
| TRN-0115 | Validation pending | `training/mission4.cpp:378-462` | `src/Allegiance.ClientCore/Training/Mission4.cs:312` | W M4 (all ten lesson boundaries; native miner kill/departure receipt) |
| TRN-0116 | Validation pending | `training/mission4.cpp:463-616` | `src/Allegiance.ClientCore/Training/Mission4.cs:364` | W M4 (all ten lesson boundaries; native miner kill/departure receipt) |
| TRN-0118 | Validation pending | `training/mission4.cpp:761-849` | `src/Allegiance.ClientCore/Training/Mission4.cs:561` | W M4 (all ten lesson boundaries; native miner kill/departure receipt) |
| TRN-0119 | Validation pending | `training/mission4.cpp:850-974` | `src/Allegiance.ClientCore/Training/Mission4.cs:634` | W M4 (all ten lesson boundaries; native miner kill/departure receipt) |
| TRN-0120 | Validation pending | `training/mission4.cpp:975-1016` | `src/Allegiance.ClientCore/Training/Mission4.cs:750` | W M4 (all ten lesson boundaries; native miner kill/departure receipt) |
| TRN-0121 | Validation pending | `training/mission4.cpp:1017-1043` | `src/Allegiance.ClientCore/Training/Mission4.cs:783` | W M4 (all ten lesson boundaries; native miner kill/departure receipt) |
| TRN-0122 | Validation pending | `training/mission5.cpp:149` | `src/Allegiance.ClientCore/Training/Mission5.cs:77` | W M5; U.Docked_training_view_uses_confirmed_remote_sector_without_a_hangar_UI |
| TRN-0123 | Validation pending | `training/mission5.cpp:252-357` | `src/Allegiance.ClientCore/Training/Mission5.cs:140` | W M5; U.Docked_training_view_uses_confirmed_remote_sector_without_a_hangar_UI |
| TRN-0124 | Validation pending | `training/mission5.cpp:358-467` | `src/Allegiance.ClientCore/Training/Mission5.cs:217` | W M5; U.Docked_training_view_uses_confirmed_remote_sector_without_a_hangar_UI |
| TRN-0125 | Validation pending | `training/mission5.cpp:468-534` | `src/Allegiance.ClientCore/Training/Mission5.cs:291` | W M5; U.Docked_training_view_uses_confirmed_remote_sector_without_a_hangar_UI |
| TRN-0126 | Validation pending | `training/mission5.cpp:535-576` | `src/Allegiance.ClientCore/Training/Mission5.cs:335` | W M5; U.Docked_training_view_uses_confirmed_remote_sector_without_a_hangar_UI |
| TRN-0127 | Validation pending | `training/mission5.cpp:577-634` | `src/Allegiance.ClientCore/Training/Mission5.cs:360` | W M5; U.Docked_training_view_uses_confirmed_remote_sector_without_a_hangar_UI |
| TRN-0128 | Validation pending | `training/mission8.cpp:148` | `src/Allegiance.ClientCore/Training/Mission8.cs:85` | W M8; U.Mission8_bomber_attacks_a_real_outpost_with_native_projectiles |
| TRN-0129 | Validation pending | `training/mission8.cpp:162-239` | `src/Allegiance.ClientCore/Training/Mission8.cs:107` | W M8; U.Mission8_bomber_attacks_a_real_outpost_with_native_projectiles |
| TRN-0130 | Validation pending | `training/mission8.cpp:240-406` | `src/Allegiance.ClientCore/Training/Mission8.cs:185` | W M8; U.Mission8_bomber_attacks_a_real_outpost_with_native_projectiles |
| TRN-0131 | Validation pending | `training/mission8.cpp:407-654` | `src/Allegiance.ClientCore/Training/Mission8.cs:327` | W M8; U.Mission8_bomber_attacks_a_real_outpost_with_native_projectiles |
| TRN-0161 | Implemented — both styles restore the docked station anchor, clear prior displacement, and stream a real remote view without moving the trainee. | `training/mission5.cpp:84-106` | `src/Allegiance.ClientCore.Game/Game1.Training.cs:661` | U.Docked_training_view_uses_confirmed_remote_sector_without_a_hangar_UI (both styles); W M5 |
| TRN-0216 | Implemented — private mission constants and the real bounds-damage consumer are verified; shared cached core remains unchanged. | `training/mission2.cpp:102-103` | `src/Allegiance.SinglePlayer/Training/TrainingHost.cs:261` | U.Mission2_bounds_are_private_and_Mission3_preserves_native_full_cargo_swap_bug |
| TRN-0219 | Implemented — real station/trainee geometry exercises the native 7500/7000 thresholds. | `training/mission3.cpp:239-247` | `src/Allegiance.ClientCore/Training/Mission3.cs:205` | U.Mission3_guard_turns_outside_7500_and_clears_only_in_the_7000_to_7500_band |
| TRN-0221 | Implemented — native tower recipes, station/warp positions, cube distribution bounds and explicit idle orders are verified. | `training/mission5.cpp:180-247` | `src/Allegiance.ClientCore/Training/Mission5.cs:100` | U.Mission5_tableau_uses_native_station_warp_geometry_and_loadouts |
| TRN-0222 | Implemented — native pose, proximity loadout, cargo, tech, released trigger and group-fire UI setup are verified. | `training/mission8.cpp:109-145` | `src/Allegiance.SinglePlayer/Training/TrainingHost.cs:428` | U.Mission8_has_native_position_proximity_cargo_and_enemy_sniper_tech; W M8 |
| TRN-0233 | Implemented helper, test-only reachability — six native messages and range rounding; no executable native mission calls CreateTooCloseCondition. No invented production caller or completion credit. | `training/trainingmission.cpp:905-924` | `src/Allegiance.ClientCore/Training/TrainingMission.cs:286` | U.Too_close_warnings_rotate_with_native_range_rounding_and_delayed_two_second_cadence |

## Inspected captures

[Classic team pane](P912_assets/m5-classic.png) and [Modern team pane](P912_assets/m5-modern.png) show the real drone roster, the docked commander and Miner 01 in Neptune. The 800×480 captures clip part of the team-pane footer; these are behavior evidence, not a claim of pixel-identical layout. Both images were opened and inspected.

M3’s full-window inventory observation and the cargo fixture both show no spare magazine. Paired inventory captures were inspected in the raw local window evidence; they are omitted from the small report-asset bundle.

[Classic repair](P912_assets/m8-classic-repair.png) and [Modern repair](P912_assets/m8-modern-repair.png) show the mounted Nanite 2, firing indicator and repair instruction. The nearby station mesh fills much of the viewport; these images establish the repair interaction, while the raw state trace establishes full repair and later combat outcomes. Both images were opened and inspected.

[Classic quickcomm](P912_assets/m4-classic-quickcomm.png) and [Modern quickcomm](P912_assets/m4-modern-quickcomm.png) show the sonic menu over their cockpit HUDs with Everyone selected and group fire retained. The tall menu clips its first entry at 800×480; the remaining numeric choices and category labels are visible. Both images were opened and inspected.

[Classic command bar](P912_assets/m8-classic-command-bar.png) and [Modern command bar](P912_assets/m8-modern-command-bar.png) show F3 exposing the recipient/verb controls after the M8 cockpit setup. The lower console overlaps nearby HUD text at this capture size; the bar itself is visible in both. Both images were opened and inspected.

## Qualification

- Baseline: parallel broadcast build, pool transport, native ship updates and goto, server GC. Artwork is present; skip counts are reported explicitly.
- Solution build: 0 warnings, 0 errors (latest `.scratch/p912/qualification/build/output.log`).
- Universe + affected join/wire/binding cohort: 149 passed, 0 failed, 0 skipped in each of three final serial repeats (`.scratch/p912/qualification/bindings-{1,2,3}/result.trx`). Earlier M3 join-readiness failures are retained in the ledger. Recurrence exposed the readiness/goal-construction race; the port now holds the session gate across both operations, and U.Training_start_checks_readiness_and_builds_goals_under_one_replicated_world_lock covers the correction.
- Camera, chat and team-pane cohort: 121 passed, 0 failed, 0 skipped in each of three repeats (`.scratch/p912/qualification/camera-chat-{1,2,3}/result.trx`).
- Affected warp / aleph / NPC-update cohort: 387 passed, 0 failed, 0 skipped in each of three serial repeats (`.scratch/p912/qualification/warp-handoff-{1,2,3}/result.trx`). The existing P739 floating-deadline boundary family was isolated separately 3× and is ledgered. Two exit-pose goldens now sample the existing ShipWarped event, retaining their exact native exit expectations before subsequent destination-cluster motion.
- The five extra layer/miner crossing cases pass in each of three repeats (`.scratch/p912/qualification/warp-extra-{1,2,3}/result.trx`).
- Full-window and lesson repetitions: the three serial training passes run each P912 case in a fresh Game1 child process, one at a time. This supplies three isolated executions of all 11 full-session/branch cases and all 20 lesson-prefix cases. The separate unit/binding cohort runs three filtered processes. Results pending.
- Training Fast + Live, three repeats: pending.
- Fast tier (one invocation): 12,863 passed, 13 failed, 3 skipped optional Meshy-pack cases; four timing violations. Task-owned P708 reflection was corrected and all 42 tests passed 3×. Other failures/timing cases have 3× isolation receipts and dispositions in `docs/TEST_FLAKES.md`. Raw sweep: `.scratch/p912/fast-tier/`; isolation: `.scratch/p912/fast-isolation/summary.json`. This sweep preceded the final training idle/accepted-order, camera/chat/status and proof-pilot changes; it was not repeated. Final targeted and training checks follow those changes.
- Showcase: **5/5 passed, 0 skipped**, serial baseline runs at 207.035 / 228.034 / 373.057 / 213.034 / 376.057 s. One-minute load ranged 0.66–2.66 (`.scratch/p912/showcase-load.jsonl`). Dispatch-base P911 also passed 5/5 (207–322 s; `docs/reports/P911_REPORT.md:141,170`). Two P912 runs took longer than that range; this is behavior evidence, with no throughput claim.
- Tools build: all 107 projects pass (`.scratch/p912/qualification/tools/output.log`). Server NativeAOT publish passes with no IL2026/IL3050 diagnostics (`.scratch/p912/qualification/aot/output.log`). Source hygiene remains pending final audit.

## Scorecard

Before: training **677/901 (75.14%)**, production rows **176/235 (74.89%)**. Framework 227/298; integration 279/326; missions 168/274; tools 3/3. Source: `.scratch/p912/scorecard-before.txt`.

After: pending final row dispositions. Only the 29 assigned matrix rows may change.

## Native intent

- **Design:** the lessons deliberately constrain controls, use real command slots, require actual repairs/combat/docking, and carry state between lessons. A modern implementation would retain these rules while sharing typed objectives and explicit capability checks with the live UI.
- **Era constraint:** fixed object IDs, authored maps, imperative goal lists and hardcoded keyboard prompts assume one static core and input map. A modern approach would declare scenario data and resolve prompts from bindings, with replayable input and outcome traces.
- **Native bugs:** M3’s authored default cargo supplies no magazine for its swap instruction. M8’s tech-level-zero removal tests part 200 after available tech has upgraded it to 201, so the intended one-gun nerf does not apply. Preserve these behaviors; a modern tutorial would validate inventory at authoring time and transform loadouts explicitly.
- **Unused helper:** the rotating warning helper has no executable native mission caller. Keep test-only reachability; a modern scenario validator would report unused lesson helpers.


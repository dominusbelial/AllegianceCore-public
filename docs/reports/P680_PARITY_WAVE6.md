# P680 — parity wave 6: simulation countdown sounds

Completed **SIM-0304–SIM-0313**, ten simulation-matrix sound rows worth **30 weight points**, in the working tree. Simulation weighted parity rises **38.26% → 40.51%**; sounds rise **15.99% → 21.04%**. No commits or pushes.

## Bundle and scope

Ran `python3 scripts/parity-scorecard.py` before editing. The baseline contains **300 missing/partial simulation rows**, ordered by weight in the inventory below: weight 5: 1; weight 4: 42; weight 3: 137; weight 2: 119; weight 1: 1. There are **290 remaining** after this bundle (ten fewer weight-3 partial rows). Only `docs/parity/sim-rules-matrix.csv` was changed; the interface matrix was left unchanged.

These ten rows share the same native countdown switch, integer clock, client receive state, wave catalogue, and real audio player. Each already had briefing audio, but remained partial because **Started timed games never reached that audio path**. Implementing the missing duration/epoch receive fields and shared countdown detector closes all ten together. This is a coherent sound-rule bundle, despite its client implementation: the selected rows belong to the simulation matrix's `sounds` area.

Exclusions respected: SIM-0063 and all `Sector.Update` structure (P667); broadcast pipeline/interest tiers (P677); security (P653–P655); rendering LOD/FSR (P628/P643); Android; installer (P644). No server, bot, drone, wingman, NPC, mining, or economy implementation was edited. The conditional kills/relaunch/mining battle requirement is therefore not triggered. Full loopback suites still run, including the opt-in local P660 15v15 showcase. No rig / 221 / 200v200 action was attempted.

## Native rules and implementation

All native citations are relative to **`~/Development/Allegiance/src/`**; the source files were read locally.

- `WinTrek/WinTrek.cpp:11135-11148`: audio runs in Starting, or in Started when `IsCountdownGame()` is true. Starting uses `max(0, int(start-now)+1)`; Started uses `max(0, int(duration-(now-start)+1))`. The placement of truncation is intentional, including exact-second and post-expiry boundaries.
- `WinTrek/WinTrek.cpp:11150-11154`: only changed integer values reach the cue switch. Slow frames emit the current value and do not queue missed digits.
- `Igc/igc.h:1162,1169,1526-1529,1557-1560`: `dtGameLength` is seconds, zero disables the timed game, and `timeStart` is its start epoch. `clintlib/appmsg.cpp:2412-2422` consumes the definition's server-time epoch; `clintlib/appmsg.cpp:2702-2711` and `clintlib/clintlib.cpp:817-820` replace it on SET_START_TIME.
- `clintlib/appmsg.cpp:2330-2334` and `WinTrek/WinTrek.cpp:11127-11131`: Starting notification resets the native change detector. A SET_START_TIME epoch correction alone does not reset it.

`src/Allegiance.Core/Wire/MissionDef.cs:72` adds duration at embedded offset 132 (absolute 136) and start time at embedded offset 156 (absolute 160). The existing P29 MSVC probe in `docs/P29_MISSIONDEF_JOINED_MISSION.md:43` confirms `timeStart`; the preceding duration placement follows the contiguous native version/two-float-rate/duration declarations at `Igc/igc.h:1159-1162`. A byte-level regression independently writes and reads these cells and checks neighboring fields.

`src/Allegiance.ClientCore/State/ClientWorld.cs:1522` derives the audio integer from the existing server-time sample and simulation-scale extrapolation. It preserves the mission start epoch through Started, supports late join via MISSIONDEF without a prior SET_START_TIME, and clears that epoch on reconnect/next match. The existing briefing HUD countdown retains its own ceiling arithmetic and ends at Started. Mission configuration persists until replaced by the next definition, matching the existing snapshot lifecycle.

`src/Allegiance.ClientCore.Game/Audio/MissionCountdownSound.cs:29` implements the change detector used by `Game1.FlightInput.cs:2993`; `Game1` samples it under the session gate before its lobby/flight dispatch and sends each emitted digit to `Audio/SoundManager.cs:749`. The same bundled `countdown10.ogg` through `countdown1.ogg` assets and MGCB entries are used. `CountdownSound.cs` no longer documents the incorrect claim that native lacks a countdown-start cue.

The clock/state, sound detector, byte layout, and bundled-content tests cover the complete predicate and each selected digit:

| Row | Weight | Native cue call | Wave binding | Per-row regression |
| --- | ---: | --- | --- | --- |
| SIM-0304 | 3 | WinTrek/WinTrek.cpp:11173 | `Content/sounddef.mdl:332` → `countdown10.ogg` | `P680CountdownSoundTests.Each_native_wave_is_emitted_once_for_briefing_and_timed_game(10)`, `tests/Allegiance.ClientCore.MonoGame.Tests/P680CountdownSoundTests.cs:22` |
| SIM-0305 | 3 | WinTrek/WinTrek.cpp:11177 | `Content/sounddef.mdl:333` → `countdown9.ogg` | `P680CountdownSoundTests.Each_native_wave_is_emitted_once_for_briefing_and_timed_game(9)`, `tests/Allegiance.ClientCore.MonoGame.Tests/P680CountdownSoundTests.cs:22` |
| SIM-0306 | 3 | WinTrek/WinTrek.cpp:11181 | `Content/sounddef.mdl:334` → `countdown8.ogg` | `P680CountdownSoundTests.Each_native_wave_is_emitted_once_for_briefing_and_timed_game(8)`, `tests/Allegiance.ClientCore.MonoGame.Tests/P680CountdownSoundTests.cs:22` |
| SIM-0307 | 3 | WinTrek/WinTrek.cpp:11185 | `Content/sounddef.mdl:335` → `countdown7.ogg` | `P680CountdownSoundTests.Each_native_wave_is_emitted_once_for_briefing_and_timed_game(7)`, `tests/Allegiance.ClientCore.MonoGame.Tests/P680CountdownSoundTests.cs:22` |
| SIM-0308 | 3 | WinTrek/WinTrek.cpp:11189 | `Content/sounddef.mdl:336` → `countdown6.ogg` | `P680CountdownSoundTests.Each_native_wave_is_emitted_once_for_briefing_and_timed_game(6)`, `tests/Allegiance.ClientCore.MonoGame.Tests/P680CountdownSoundTests.cs:22` |
| SIM-0309 | 3 | WinTrek/WinTrek.cpp:11193 | `Content/sounddef.mdl:337` → `countdown5.ogg` | `P680CountdownSoundTests.Each_native_wave_is_emitted_once_for_briefing_and_timed_game(5)`, `tests/Allegiance.ClientCore.MonoGame.Tests/P680CountdownSoundTests.cs:22` |
| SIM-0310 | 3 | WinTrek/WinTrek.cpp:11197 | `Content/sounddef.mdl:338` → `countdown4.ogg` | `P680CountdownSoundTests.Each_native_wave_is_emitted_once_for_briefing_and_timed_game(4)`, `tests/Allegiance.ClientCore.MonoGame.Tests/P680CountdownSoundTests.cs:22` |
| SIM-0311 | 3 | WinTrek/WinTrek.cpp:11201 | `Content/sounddef.mdl:339` → `countdown3.ogg` | `P680CountdownSoundTests.Each_native_wave_is_emitted_once_for_briefing_and_timed_game(3)`, `tests/Allegiance.ClientCore.MonoGame.Tests/P680CountdownSoundTests.cs:22` |
| SIM-0312 | 3 | WinTrek/WinTrek.cpp:11205 | `Content/sounddef.mdl:340` → `countdown2.ogg` | `P680CountdownSoundTests.Each_native_wave_is_emitted_once_for_briefing_and_timed_game(2)`, `tests/Allegiance.ClientCore.MonoGame.Tests/P680CountdownSoundTests.cs:22` |
| SIM-0313 | 3 | WinTrek/WinTrek.cpp:11209 | `Content/sounddef.mdl:341` → `countdown1.ogg` | `P680CountdownSoundTests.Each_native_wave_is_emitted_once_for_briefing_and_timed_game(1)`, `tests/Allegiance.ClientCore.MonoGame.Tests/P680CountdownSoundTests.cs:22` |

Shared regressions: `tests/Allegiance.Core.Tests/Wire/P680CountdownWireTests.cs:9`; `tests/Allegiance.ClientCore.Tests/P680CountdownClockTests.cs:15,42,64,73`; `tests/Allegiance.ClientCore.MonoGame.Tests/P680CountdownSoundTests.cs:44,61,83`. The digit theory exercises encoded MISSIONDEF/MISSION_STAGE through the real `ConnectionSession` receive dispatcher and the exact planner consumed by `Game1`. Audio hardware is not required by these deterministic checks; existing content is loaded by the normal SoundManager.

This completes **cue conditions and delivery**, with no new timeout-winner implementation. The port server still does not select a winner at timed expiry (SIM-0133 remains missing), and its current default MISSIONDEF duration remains zero. Nonzero native definitions are supported and verified through encoded receive replay. Coarse 10-minute/5-minute/1-minute/30-second sounds (SIM-0300–SIM-0303) remain missing. No credit is claimed for those separate rules or the countdown-start row SIM-0299.

## Scorecard before → after

Partial earns zero credit. Both invocations of `scripts/parity-scorecard.py` validate all 473 simulation rows; eligible denominators do not change.

| Simulation area | Weighted before → after | Complete / eligible before → after |
| --- | --- | --- |
| callbacks | 33.33% (66/198) → 33.33% (66/198) | 22/66 → 22/66 |
| constants | 95.56% (129/135) → 95.56% (129/135) | 37/39 → 37/39 |
| periodic | 17.02% (8/47) → 17.02% (8/47) | 2/15 → 2/15 |
| sounds | **15.99% (95/594) → 21.04% (125/594)** | **28/228 → 38/228** |
| updates | 57.88% (202/349) → 57.88% (202/349) | 46/87 → 46/87 |
| warnings | 100.00% (10/10) → 100.00% (10/10) | 2/2 → 2/2 |
| **TOTAL** | **38.26% (510/1333) → 40.51% (540/1333)** | **137/437 → 147/437** |

## Verification

`dotnet build AllegianceCore.slnx --nologo -m:1 -v:q` completed with **0 warnings / 0 errors**, including desktop content compilation. All executable source/test changes preceded that build; a subsequent source comment correction only corrected the native field citation. Source/test fingerprints were checked after verification. `git diff --check` is clean, and the scorecard validates the final matrix.

**53 affected methods / 80 distinct data cases**, including **9 new methods / 20 new cases**, passed **3/3 exact-method individual invocations** after the build. Theory invocations execute every data row. Coverage includes the new byte layout, encoded client receive/clock rules, every digit in both stages, audio assets, existing mission-definition wire tests, live mission countdown/ping, reconnect/reset, live next-match transitions, game-over state, and the shared ripcord waves. The complete invocation evidence is in [P680_RESULTS.json](P680_RESULTS.json).

The three required **unfiltered** suites then ran sequentially with `dotnet test tests/<assembly> --no-build --no-restore --logger trx --results-directory /tmp/p680/results`. The runner sourced `local.env`; ClientCore additionally enabled `ALLEGIANCE_LIVE_P660_SHOWCASE=1`, so its real local 15v15 next-match showcase ran. No live or loopback tests were filtered out.

| Assembly | Passed | Failed in full pass | Skipped | Total |
| --- | ---: | ---: | ---: | ---: |
| Allegiance.Core.Tests | 4354 | 1 | 0 | 4355 |
| Allegiance.ClientCore.Tests | 965 | 1 | 0 | 966 |
| Allegiance.ClientCore.MonoGame.Tests | 2167 | 0 | 3 | 2170 |

All **2 observed full-pass failures** passed **3/3 exact-method runs alone** after all full suites finished. They are load/receipt flakes under the brief's lane rule and are recorded in [TEST_FLAKES.md](../TEST_FLAKES.md). Their full-pass failures remain visible above. No assertions, timeouts, quarantine tags, or product behavior were changed for these failures.

| Test | Full-pass symptom | Final isolated result |
| --- | --- | --- |
| `Allegiance.Core.Tests.LiveRipcordTests.Re_target_activates_to_requester_only` | Assert.Single() Failure: The collection was empty | 3/3 pass |
| `Allegiance.ClientCore.Tests.ThreePeerSeededSessionTests.SeedThreePeers_TwoSameSide_OneCrossSide_DeterministicVisibility` | System.InvalidOperationException : logon drain order diverged from connection order — peer2 side 0 not cross-side (0, 1, 0); determinism broken | 3/3 pass |

Existing skips: `Allegiance.ClientCore.MonoGame.Tests.MeshyPackTests.Pack_built_content_directory_exists`, `Allegiance.ClientCore.MonoGame.Tests.MeshyPackTests.Pack_catalogue_merge_yields_identical_resolution_to_before`, `Allegiance.ClientCore.MonoGame.Tests.MeshyPackTests.Pack_catalogue_has_the_full_meshy_set`. These are optional private Meshy pack cases. Required local artwork/native-source checks and loopback tests executed.


## Missing / partial simulation inventory by weight

This is the complete **initial** simulation-only gap list, including excluded rows for visibility. `partial → implemented` identifies the ten completed rows; every other entry is still missing or partial at completion. Native citations use the source root stated above. No interface rows are included.

| Weight | Row | Area | Native item | Before → after | Native citation |
| ---: | --- | --- | --- | --- | --- |
| 5 | SIM-0063 | updates | swept_collision_time_order | partial | Igc/clusterIGC.cpp:255 |
| 4 | SIM-0033 | constants | c_fcidWarpBombDelay | partial | Igc/igc.h:98; FedSrv/FedSrv.CPP:12551 |
| 4 | SIM-0044 | updates | ore_seen_by_side | partial | Igc/asteroidigc.h:58 |
| 4 | SIM-0045 | updates | asteroid_build_completion | partial | Igc/asteroidigc.h:90 |
| 4 | SIM-0048 | updates | build_effect_phase_animation | partial | Igc/buildingeffectigc.h:160 |
| 4 | SIM-0059 | updates | docked_npc_launch_rules | partial | Igc/clusterIGC.cpp:116 |
| 4 | SIM-0060 | updates | station_consumed_rock_fog | missing | Igc/clusterIGC.cpp:141 |
| 4 | SIM-0062 | updates | global_preplot_plot_execute_order | partial | Igc/clusterIGC.cpp:170 |
| 4 | SIM-0064 | updates | mine_attrition | partial | Igc/clusterIGC.cpp:325 |
| 4 | SIM-0065 | updates | visibility_every_tenth_pass | partial | Igc/clusterIGC.cpp:348 |
| 4 | SIM-0069 | updates | dispenser_mount_and_fire | partial | Igc/dispenserigc.cpp:84 |
| 4 | SIM-0075 | updates | missile_delayed_steering | partial | Igc/missileigc.cpp:364 |
| 4 | SIM-0076 | updates | missile_burnout | partial | Igc/missileigc.cpp:356 |
| 4 | SIM-0081 | updates | teleport_probe_activation_edge | partial | Igc/probeigc.cpp:340 |
| 4 | SIM-0082 | updates | probe_target_priority | partial | Igc/probeigc.cpp:388 |
| 4 | SIM-0083 | updates | probe_first_shot_reveal | missing | Igc/probeigc.cpp:448 |
| 4 | SIM-0088 | updates | shield_mount_gate | partial | Igc/shieldIGC.cpp:72 |
| 4 | SIM-0101 | updates | npc_plan_legality | partial | Igc/shipIGC.cpp:1938 |
| 4 | SIM-0107 | updates | miner_capacity_and_depletion | partial | Igc/shipIGC.cpp:2208 |
| 4 | SIM-0109 | updates | constructor_drill_alignment | partial | Igc/shipIGC.cpp:2272 |
| 4 | SIM-0110 | updates | idle_npc_complaint_backoff | partial | Igc/shipIGC.cpp:2308 |
| 4 | SIM-0112 | updates | wingman_defend_acquire_return | partial | Igc/shipIGC.cpp:2374 |
| 4 | SIM-0113 | updates | wingman_unseen_target_memory | missing | Igc/shipIGC.cpp:2410 |
| 4 | SIM-0114 | updates | wingman_range_bands | partial | Igc/shipIGC.cpp:2483 |
| 4 | SIM-0115 | updates | wingman_missile_evasion_gate | missing | Igc/shipIGC.cpp:2578 |
| 4 | SIM-0119 | updates | goto_completion_and_layer_lay | partial | Igc/shipIGC.cpp:2802 |
| 4 | SIM-0129 | updates | treasure_deceleration | missing | Igc/treasureIGC.cpp:221 |
| 4 | SIM-0133 | periodic | countdown_game_winner | missing | FedSrv/fsmission.cpp:2335 |
| 4 | SIM-0135 | periodic | ambient_treasure_regeneration | missing | FedSrv/fsmission.cpp:2554 |
| 4 | SIM-0142 | periodic | auto_start_restart_ready_rules | partial | FedSrv/FedSrv.CPP:8178 |
| 4 | SIM-0143 | periodic | start_countdown_expiry | partial | FedSrv/FedSrv.CPP:8237 |
| 4 | SIM-0253 | sounds | Complain@Igc/shipIGC.cpp:2199 droneTooCrowdedSound | partial | Igc/shipIGC.cpp:2199 |
| 4 | SIM-0254 | sounds | Complain@Igc/shipIGC.cpp:2315 droneWhereToSound | partial | Igc/shipIGC.cpp:2315 |
| 4 | SIM-0255 | sounds | Complain@Igc/shipIGC.cpp:2319 GetConstructorNeedRockSound | partial | Igc/shipIGC.cpp:2319 |
| 4 | SIM-0256 | sounds | Complain@Igc/shipIGC.cpp:2326 droneWhereToLayMinefieldSound | partial | Igc/shipIGC.cpp:2326 |
| 4 | SIM-0257 | sounds | Complain@Igc/shipIGC.cpp:2328 droneWhereToLayTowerSound | partial | Igc/shipIGC.cpp:2328 |
| 4 | SIM-0396 | sounds | PlaySoundEffect@WinTrek/trekigc.cpp:4335 GetDestroyedSound | partial | WinTrek/trekigc.cpp:4335 |
| 4 | SIM-0397 | sounds | PlaySoundEffect@WinTrek/trekigc.cpp:4337 GetEnemyDestroyedSound | partial | WinTrek/trekigc.cpp:4337 |
| 4 | SIM-0421 | sounds | PlaySoundEffect@clintlib/appmsg.cpp:549 GetDestroyedSound | partial | clintlib/appmsg.cpp:549 |
| 4 | SIM-0422 | sounds | PlaySoundEffect@clintlib/appmsg.cpp:551 GetEnemyDestroyedSound | partial | clintlib/appmsg.cpp:551 |
| 4 | SIM-0423 | sounds | PlaySoundEffect@clintlib/appmsg.cpp:675 GetCapturedSound | partial | clintlib/appmsg.cpp:675 |
| 4 | SIM-0424 | sounds | PlaySoundEffect@clintlib/appmsg.cpp:677 GetEnemyCapturedSound | partial | clintlib/appmsg.cpp:677 |
| 4 | SIM-0442 | sounds | PlaySoundEffect@clintlib/clintlib.cpp:3103 GetLaunchSound | partial | clintlib/clintlib.cpp:3103 |
| 3 | SIM-0047 | updates | bucket_percent_notification | partial | Igc/bucketigc.h:65 |
| 3 | SIM-0058 | updates | cloak_mount_readiness | partial | Igc/cloakigc.cpp:68 |
| 3 | SIM-0067 | updates | explosion_mine_strength | partial | Igc/clusterIGC.cpp:443 |
| 3 | SIM-0070 | updates | damage_credit_slot_decay | partial | Igc/igc.h:5081 |
| 3 | SIM-0085 | updates | probe_projectile_emission | partial | Igc/probeigc.cpp:479 |
| 3 | SIM-0086 | updates | projectile_burnout_blast | partial | Igc/projectileIGC.cpp:120 |
| 3 | SIM-0089 | updates | shield_five_percent_activation_sound | missing | Igc/shieldIGC.cpp:93 |
| 3 | SIM-0100 | updates | npc_ammo_goal_recovery | partial | Igc/shipIGC.cpp:1715 |
| 3 | SIM-0104 | updates | npc_ripcord_retry | partial | Igc/shipIGC.cpp:2067 |
| 3 | SIM-0117 | updates | wingman_cross_sector_pursuit | partial | Igc/shipIGC.cpp:2707 |
| 3 | SIM-0118 | updates | wingman_goto_opportunism | partial | Igc/shipIGC.cpp:2747 |
| 3 | SIM-0138 | periodic | ballot_resolution_boundary | partial | FedSrv/fsmission.cpp:5817 |
| 3 | SIM-0141 | periodic | server_sim_broadcast_cadence | partial | FedSrv/FedSrv.CPP:4238 |
| 3 | SIM-0144 | periodic | station_state_docked_broadcast | partial | FedSrv/FedSrv.CPP:8253 |
| 3 | SIM-0150 | periodic | warp_reentry_timer | partial | FedSrv/fsship.cpp:1185; FedSrv/fsship.cpp:1190 |
| 3 | SIM-0154 | updates | afterburner_mount_fraction_persistence | partial | Igc/afterburnerIGC.cpp:111; Igc/afterburnerIGC.cpp:117 |
| 3 | SIM-0160 | callbacks | HullTypeCompleted@4771 | partial | Igc/igc.h:4771 ; Igc/bucketigc.h:245 |
| 3 | SIM-0161 | callbacks | PartTypeCompleted@4772 | partial | Igc/igc.h:4772 ; Igc/bucketigc.h:253 |
| 3 | SIM-0163 | callbacks | BucketChangeEvent@4775 | partial | Igc/igc.h:4775 ; Igc/bucketigc.cpp:98; Igc/bucketigc.h:67; Igc/bucketigc.h:279; Igc/bucketigc.h:290; Igc/bucketigc.h:299 |
| 3 | SIM-0164 | callbacks | SideBuildingTechChange@4776 | partial | Igc/igc.h:4776 ; Igc/sideigc.h:166 |
| 3 | SIM-0167 | callbacks | StationTypeChange@4779 | partial | Igc/igc.h:4779 ; Igc/stationIGC.cpp:75; Igc/stationIGC.cpp:276 |
| 3 | SIM-0168 | callbacks | ClusterUpdateEvent@4781 | partial | Igc/igc.h:4781 ; Igc/clusterIGC.cpp:357 |
| 3 | SIM-0169 | callbacks | BuildStation@4783 | partial | Igc/igc.h:4783 ; Igc/buildingeffectigc.h:348 |
| 3 | SIM-0172 | callbacks | ReceiveChat@4792 | partial | Igc/igc.h:4792 |
| 3 | SIM-0173 | callbacks | SendChat@4801 | partial | Igc/igc.h:4801 ; Igc/shipIGC.cpp:1895; Igc/shipIGC.cpp:1898; Igc/shipIGC.cpp:1901; Igc/shipIGC.cpp:2171; Igc/shipIGC.h:1732 |
| 3 | SIM-0174 | callbacks | SendChatf@4812 | partial | Igc/igc.h:4812 ; Igc/buildingeffectigc.h:320; Igc/shipIGC.cpp:2386; Igc/shipIGC.cpp:2401; Igc/shipIGC.cpp:2561; Igc/shipIGC.cpp:2692; Igc/shipIGC.cpp:2779; Igc/shipIGC.h:2408; Igc/shipIGC.h:2449 |
| 3 | SIM-0175 | callbacks | GetMoneyRequest@4828 | partial | Igc/igc.h:4828 |
| 3 | SIM-0176 | callbacks | PlaySoundEffect@4830 | partial | Igc/igc.h:4830 ; Igc/asteroidigc.h:154; Igc/shieldIGC.cpp:94; Igc/shieldIGC.cpp:185; Igc/shipIGC.cpp:834; Igc/shipIGC.cpp:1004; Igc/shipIGC.cpp:1189; Igc/shipIGC.cpp:1193 |
| 3 | SIM-0177 | callbacks | PlaySoundEffect@4831 | partial | Igc/igc.h:4831 ; Igc/asteroidigc.h:154; Igc/shieldIGC.cpp:94; Igc/shieldIGC.cpp:185; Igc/shipIGC.cpp:834; Igc/shipIGC.cpp:1004; Igc/shipIGC.cpp:1189; Igc/shipIGC.cpp:1193 |
| 3 | SIM-0178 | callbacks | PlayNotificationSound@4832 | partial | Igc/igc.h:4832 ; Igc/afterburnerIGC.cpp:119; Igc/cloakigc.cpp:75; Igc/dispenserigc.cpp:98; Igc/magazineigc.cpp:96; Igc/shieldIGC.cpp:82; Igc/shieldIGC.cpp:186; Igc/shipIGC.cpp:1608; Igc/shipIGC.h:616; Igc/shipIGC.h:617; Igc/shipIGC.h:646; Igc/shipIGC.h:647; Igc/shipIGC.h:1302; Igc/shipIGC.h:1307; Igc/weaponIGC.h:46 |
| 3 | SIM-0180 | callbacks | PlayVisualEffect@4834 | partial | Igc/igc.h:4834 ; Igc/shipIGC.cpp:836; Igc/shipIGC.cpp:1006; Igc/shipIGC.cpp:1191; Igc/shipIGC.cpp:1195 |
| 3 | SIM-0182 | callbacks | TerminateModelEvent@4836 | partial | Igc/igc.h:4836 ; Igc/modelIGC.cpp:61 |
| 3 | SIM-0183 | callbacks | TerminateMissionEvent@4837 | partial | Igc/igc.h:4837 ; Igc/missionigc.cpp:3082 |
| 3 | SIM-0185 | callbacks | KillAsteroidEvent@4839 | missing | Igc/igc.h:4839 ; Igc/asteroidigc.h:162; Igc/clusterIGC.cpp:155 |
| 3 | SIM-0186 | callbacks | DrainAsteroidEvent@4840 | partial | Igc/igc.h:4840 |
| 3 | SIM-0190 | callbacks | KillBuildingEffectEvent@4844 | partial | Igc/igc.h:4844 ; Igc/shipIGC.cpp:218 |
| 3 | SIM-0193 | callbacks | PostPlainText@4847 | partial | Igc/igc.h:4847 |
| 3 | SIM-0194 | callbacks | PostText@4848 | partial | Igc/igc.h:4848 |
| 3 | SIM-0195 | callbacks | PostNotificationText@4849 | partial | Igc/igc.h:4849 ; Igc/afterburnerIGC.cpp:120; Igc/cloakigc.cpp:76; Igc/dispenserigc.cpp:99; Igc/magazineigc.cpp:97; Igc/shieldIGC.cpp:83; Igc/shipIGC.h:612; Igc/shipIGC.h:618; Igc/shipIGC.h:642; Igc/shipIGC.h:648; Igc/weaponIGC.h:47 |
| 3 | SIM-0197 | callbacks | TargetKilled@4851 | partial | Igc/igc.h:4851 |
| 3 | SIM-0199 | callbacks | DamageShipEvent@4853 | partial | Igc/igc.h:4853 ; Igc/shipIGC.cpp:398; Igc/shipIGC.cpp:1312 |
| 3 | SIM-0205 | callbacks | LandOnCarrierEvent@4859 | partial | Igc/igc.h:4859 ; Igc/shipIGC.cpp:1050; Igc/shipIGC.cpp:1057 |
| 3 | SIM-0206 | callbacks | RescueShipEvent@4860 | partial | Igc/igc.h:4860 ; Igc/shipIGC.cpp:865; Igc/shipIGC.cpp:907; Igc/shipIGC.cpp:1035; Igc/shipIGC.cpp:1040 |
| 3 | SIM-0207 | callbacks | WarpBombEvent@4861 | partial | Igc/igc.h:4861 ; Igc/missileigc.cpp:466 |
| 3 | SIM-0208 | callbacks | HitWarpEvent@4862 | partial | Igc/igc.h:4862 ; Igc/shipIGC.cpp:636 |
| 3 | SIM-0209 | callbacks | HitTreasureEvent@4863 | partial | Igc/igc.h:4863 ; Igc/treasureIGC.h:92 |
| 3 | SIM-0212 | callbacks | ContinueRipcord@4866 | partial | Igc/igc.h:4866 ; Igc/shipIGC.cpp:280 |
| 3 | SIM-0213 | callbacks | UseRipcord@4867 | partial | Igc/igc.h:4867 ; Igc/shipIGC.cpp:286 |
| 3 | SIM-0214 | callbacks | FireMissile@4868 | partial | Igc/igc.h:4868 ; Igc/magazineigc.cpp:118 |
| 3 | SIM-0215 | callbacks | FireExpendable@4870 | partial | Igc/igc.h:4870 ; Igc/dispenserigc.cpp:121 |
| 3 | SIM-0219 | callbacks | ChangeStation@4876 | partial | Igc/igc.h:4876 ; Igc/shipIGC.h:483 |
| 3 | SIM-0220 | callbacks | ChangeCluster@4879 | partial | Igc/igc.h:4879 ; Igc/shipIGC.h:254 |
| 3 | SIM-0221 | callbacks | CommandChangedEvent@4882 | partial | Igc/igc.h:4882 ; Igc/shipIGC.h:788 |
| 3 | SIM-0224 | callbacks | ActivateTeleportProbe@4888 | partial | Igc/igc.h:4888 ; Igc/probeigc.cpp:153; Igc/probeigc.cpp:347 |
| 3 | SIM-0225 | callbacks | DestroyTeleportProbe@4890 | partial | Igc/igc.h:4890 ; Igc/probeigc.cpp:230 |
| 3 | SIM-0228 | callbacks | ChangeGameState@4899 | partial | Igc/igc.h:4899 |
| 3 | SIM-0229 | callbacks | CreateBuildingEffect@4901 | partial | Igc/igc.h:4901 ; Igc/shipIGC.cpp:884 |
| 3 | SIM-0231 | callbacks | GetCluster@4909 | partial | Igc/igc.h:4909 ; Igc/asteroidigc.cpp:29; Igc/buildingeffectigc.h:84; Igc/buoyigc.cpp:60; Igc/clusterIGC.cpp:312; Igc/clusterIGC.cpp:313; Igc/clusterIGC.cpp:416; Igc/clusterIGC.cpp:444; Igc/clusterIGC.h:418; Igc/clusterIGC.h:436; Igc/common.cpp:481; Igc/common.cpp:687; Igc/common.cpp:726; Igc/common.cpp:772; Igc/common.cpp:775; Igc/common.cpp:785; Igc/common.cpp:788; Igc/common.cpp:871; Igc/common.cpp:901; Igc/common.cpp:923; Igc/common.cpp:972; Igc/common.cpp:989; Igc/common.cpp:1021; Igc/common.cpp:1034; Igc/common.cpp:1699; Igc/common.cpp:1841; Igc/common.cpp:1854; Igc/common.cpp:1873; Igc/common.cpp:1925; Igc/common.cpp:2244; Igc/common.cpp:2332; Igc/common.cpp:2333; Igc/common.cpp:2753; Igc/common.cpp:2782; Igc/igc.h:4912; Igc/igc.h:4919; Igc/igc.h:5333; Igc/igc.h:5334; Igc/magazineigc.cpp:127; Igc/mapmakerigc.cpp:239; Igc/mapmakerigc.cpp:263; Igc/mapmakerigc.cpp:264; Igc/mapmakerigc.cpp:279; Igc/mapmakerigc.cpp:335; Igc/mapmakerigc.cpp:816; Igc/mapmakerigc.cpp:822; Igc/mapmakerigc.cpp:828; Igc/mapmakerigc.cpp:834; Igc/mapmakerigc.cpp:989; Igc/mapmakerigc.cpp:1039; Igc/mineigc.cpp:62; Igc/missileigc.cpp:108; Igc/missileigc.cpp:371; Igc/missionigc.cpp:3661; Igc/modelIGC.h:589; Igc/probeigc.cpp:66; Igc/probeigc.cpp:392; Igc/probeigc.h:169; Igc/projectileIGC.cpp:193; Igc/shipIGC.cpp:1765; Igc/shipIGC.cpp:1922; Igc/shipIGC.cpp:1950; Igc/shipIGC.cpp:2036; Igc/shipIGC.cpp:2354; Igc/shipIGC.cpp:2376; Igc/shipIGC.cpp:2714; Igc/shipIGC.cpp:2720; Igc/shipIGC.cpp:2727; Igc/shipIGC.cpp:3785; Igc/shipIGC.cpp:3949; Igc/shipIGC.cpp:3970; Igc/shipIGC.cpp:4101; Igc/shipIGC.cpp:4156; Igc/shipIGC.cpp:4347; Igc/shipIGC.cpp:4378; Igc/shipIGC.h:349; Igc/shipIGC.h:1614; Igc/shipIGC.h:1644; Igc/shipIGC.h:1917; Igc/shipIGC.h:1929; Igc/shipIGC.h:2207; Igc/shipIGC.h:2252; Igc/shipIGC.h:2284; Igc/shipIGC.h:2307; Igc/shipIGC.h:2355; Igc/shipIGC.h:2356; Igc/sideigc.cpp:342; Igc/sideigc.cpp:367; Igc/sideigc.cpp:371; Igc/sideigc.cpp:373; Igc/sideigc.cpp:375; Igc/sideigc.cpp:438; Igc/stationIGC.cpp:45; Igc/stationIGC.cpp:68; Igc/stationIGC.h:314; Igc/treasureIGC.cpp:201; Igc/warpIGC.cpp:29; Igc/warpIGC.h:80; Igc/warpIGC.h:155; Igc/weaponIGC.cpp:177; Igc/weaponIGC.cpp:235 |
| 3 | SIM-0232 | callbacks | GetRipcordCluster@4915 | partial | Igc/igc.h:4915 ; Igc/common.cpp:811; Igc/common.cpp:824; Igc/shipIGC.cpp:3741; Igc/shipIGC.cpp:3760; Igc/shipIGC.cpp:4262; Igc/shipIGC.cpp:4281 |
| 3 | SIM-0233 | callbacks | Reload@4923 | partial | Igc/igc.h:4923 ; Igc/shipIGC.h:610; Igc/shipIGC.h:640 |
| 3 | SIM-0235 | sounds | PlaySoundEffect@Igc/asteroidigc.h:154 rockHitSound | partial | Igc/asteroidigc.h:154 |
| 3 | SIM-0236 | sounds | SendChatf@Igc/buildingeffectigc.h:320 GetCompletionSound | partial | Igc/buildingeffectigc.h:320 |
| 3 | SIM-0244 | sounds | PlaySoundEffect@Igc/shipIGC.cpp:834 collisionSound | partial | Igc/shipIGC.cpp:834 |
| 3 | SIM-0245 | sounds | PlaySoundEffect@Igc/shipIGC.cpp:1004 collisionSound | partial | Igc/shipIGC.cpp:1004 |
| 3 | SIM-0246 | sounds | PlaySoundEffect@Igc/shipIGC.cpp:1189 collisionSound | partial | Igc/shipIGC.cpp:1189 |
| 3 | SIM-0247 | sounds | PlaySoundEffect@Igc/shipIGC.cpp:1193 collisionSound | partial | Igc/shipIGC.cpp:1193 |
| 3 | SIM-0249 | sounds | SendChat@Igc/shipIGC.cpp:1895 constructorRunningSound | partial | Igc/shipIGC.cpp:1895 |
| 3 | SIM-0250 | sounds | SendChat@Igc/shipIGC.cpp:1898 droneTooMuchDamageSound | partial | Igc/shipIGC.cpp:1898 |
| 3 | SIM-0251 | sounds | SendChat@Igc/shipIGC.cpp:1901 droneEnemyOnScopeSound | missing | Igc/shipIGC.cpp:1901 |
| 3 | SIM-0252 | sounds | SendChat@Igc/shipIGC.cpp:2171 droneComingHomeEmptySound | partial | Igc/shipIGC.cpp:2171 |
| 3 | SIM-0258 | sounds | SendChatf@Igc/shipIGC.cpp:2386 droneInTransitSound | missing | Igc/shipIGC.cpp:2386 |
| 3 | SIM-0259 | sounds | SendChatf@Igc/shipIGC.cpp:2401 droneInTransitSound | missing | Igc/shipIGC.cpp:2401 |
| 3 | SIM-0260 | sounds | SendChatf@Igc/shipIGC.cpp:2561 droneInTransitSound | missing | Igc/shipIGC.cpp:2561 |
| 3 | SIM-0261 | sounds | SendChatf@Igc/shipIGC.cpp:2692 droneInTransitSound | missing | Igc/shipIGC.cpp:2692 |
| 3 | SIM-0262 | sounds | SendChatf@Igc/shipIGC.cpp:2779 droneInTransitSound | missing | Igc/shipIGC.cpp:2779 |
| 3 | SIM-0269 | sounds | SendChatf@Igc/shipIGC.h:2408 droneInTransitSound | missing | Igc/shipIGC.h:2408 |
| 3 | SIM-0270 | sounds | SendChatf@Igc/shipIGC.h:2449 droneInTransitSound | missing | Igc/shipIGC.h:2449 |
| 3 | SIM-0276 | sounds | PlaySoundEffect@WinTrek/WinTrek.cpp:8315 salAutopilotDisengageSound | partial | WinTrek/WinTrek.cpp:8315 |
| 3 | SIM-0292 | sounds | PlaySoundEffect@WinTrek/WinTrek.cpp:10365 salAutopilotDisengageSound | partial | WinTrek/WinTrek.cpp:10365 |
| 3 | SIM-0293 | sounds | PlaySoundEffect@WinTrek/WinTrek.cpp:10372 salAutopilotEngageSound | partial | WinTrek/WinTrek.cpp:10372 |
| 3 | SIM-0294 | sounds | SendChat@WinTrek/WinTrek.cpp:10596 voNegativeSound | partial | WinTrek/WinTrek.cpp:10596 |
| 3 | SIM-0295 | sounds | SendChat@WinTrek/WinTrek.cpp:10615 voNegativeSound | partial | WinTrek/WinTrek.cpp:10615 |
| 3 | SIM-0297 | sounds | PlaySoundEffect@WinTrek/WinTrek.cpp:10692 salAutopilotEngageSound | partial | WinTrek/WinTrek.cpp:10692 |
| 3 | SIM-0298 | sounds | SendChat@WinTrek/WinTrek.cpp:10705 voAffirmativeSound | partial | WinTrek/WinTrek.cpp:10705 |
| 3 | SIM-0299 | sounds | PlaySoundEffect@WinTrek/WinTrek.cpp:11130 countdownStartingSound | partial | WinTrek/WinTrek.cpp:11130 |
| 3 | SIM-0304 | sounds | PlaySoundEffect@WinTrek/WinTrek.cpp:11173 countdown10Sound | partial → implemented | WinTrek/WinTrek.cpp:11173 |
| 3 | SIM-0305 | sounds | PlaySoundEffect@WinTrek/WinTrek.cpp:11177 countdown9Sound | partial → implemented | WinTrek/WinTrek.cpp:11177 |
| 3 | SIM-0306 | sounds | PlaySoundEffect@WinTrek/WinTrek.cpp:11181 countdown8Sound | partial → implemented | WinTrek/WinTrek.cpp:11181 |
| 3 | SIM-0307 | sounds | PlaySoundEffect@WinTrek/WinTrek.cpp:11185 countdown7Sound | partial → implemented | WinTrek/WinTrek.cpp:11185 |
| 3 | SIM-0308 | sounds | PlaySoundEffect@WinTrek/WinTrek.cpp:11189 countdown6Sound | partial → implemented | WinTrek/WinTrek.cpp:11189 |
| 3 | SIM-0309 | sounds | PlaySoundEffect@WinTrek/WinTrek.cpp:11193 countdown5Sound | partial → implemented | WinTrek/WinTrek.cpp:11193 |
| 3 | SIM-0310 | sounds | PlaySoundEffect@WinTrek/WinTrek.cpp:11197 countdown4Sound | partial → implemented | WinTrek/WinTrek.cpp:11197 |
| 3 | SIM-0311 | sounds | PlaySoundEffect@WinTrek/WinTrek.cpp:11201 countdown3Sound | partial → implemented | WinTrek/WinTrek.cpp:11201 |
| 3 | SIM-0312 | sounds | PlaySoundEffect@WinTrek/WinTrek.cpp:11205 countdown2Sound | partial → implemented | WinTrek/WinTrek.cpp:11205 |
| 3 | SIM-0313 | sounds | PlaySoundEffect@WinTrek/WinTrek.cpp:11209 countdown1Sound | partial → implemented | WinTrek/WinTrek.cpp:11209 |
| 3 | SIM-0347 | sounds | PlaySoundIf@WinTrek/trekigc.cpp:984 GetActivateSound | partial | WinTrek/trekigc.cpp:984 |
| 3 | SIM-0349 | sounds | PlaySoundIf@WinTrek/trekigc.cpp:1001 m_vpBurstSound[mount] | partial | WinTrek/trekigc.cpp:1001 |
| 3 | SIM-0368 | sounds | PlaySoundIf@WinTrek/trekigc.cpp:1287 ripcordOnLoopSound | missing | WinTrek/trekigc.cpp:1287 |
| 3 | SIM-0370 | sounds | PlaySoundIf@WinTrek/trekigc.cpp:1293 miningSound | missing | WinTrek/trekigc.cpp:1293 |
| 3 | SIM-0372 | sounds | PlaySoundIf@WinTrek/trekigc.cpp:1312 GetInteriorSound | partial | WinTrek/trekigc.cpp:1312 |
| 3 | SIM-0373 | sounds | PlaySoundIf@WinTrek/trekigc.cpp:1314 GetMainThrusterInteriorSound | partial | WinTrek/trekigc.cpp:1314 |
| 3 | SIM-0374 | sounds | PlaySoundIf@WinTrek/trekigc.cpp:1317 GetManuveringThrusterInteriorSound | partial | WinTrek/trekigc.cpp:1317 |
| 3 | SIM-0375 | sounds | PlaySoundIf@WinTrek/trekigc.cpp:1320 m_pAfterburnerInteriorSound | partial | WinTrek/trekigc.cpp:1320 |
| 3 | SIM-0378 | sounds | PlaySoundIf@WinTrek/trekigc.cpp:1337 GetExteriorSound | partial | WinTrek/trekigc.cpp:1337 |
| 3 | SIM-0379 | sounds | PlaySoundIf@WinTrek/trekigc.cpp:1338 GetMainThrusterExteriorSound | partial | WinTrek/trekigc.cpp:1338 |
| 3 | SIM-0380 | sounds | PlaySoundIf@WinTrek/trekigc.cpp:1341 GetManuveringThrusterExteriorSound | partial | WinTrek/trekigc.cpp:1341 |
| 3 | SIM-0381 | sounds | PlaySoundIf@WinTrek/trekigc.cpp:1344 m_pAfterburnerExteriorSound | partial | WinTrek/trekigc.cpp:1344 |
| 3 | SIM-0384 | sounds | PlaySoundEffect@WinTrek/trekigc.cpp:2089 newShipSound | partial | WinTrek/trekigc.cpp:2089 |
| 3 | SIM-0385 | sounds | PlaySoundEffect@WinTrek/trekigc.cpp:2093 newEnemySound | partial | WinTrek/trekigc.cpp:2093 |
| 3 | SIM-0386 | sounds | PlaySoundEffect@WinTrek/trekigc.cpp:2114 newShipSound | partial | WinTrek/trekigc.cpp:2114 |
| 3 | SIM-0390 | sounds | PlaySoundEffect@WinTrek/trekigc.cpp:3009 newCriticalMsgSound | partial | WinTrek/trekigc.cpp:3009 |
| 3 | SIM-0391 | sounds | PlaySoundEffect@WinTrek/trekigc.cpp:3014 newNonCriticalMsgSound | partial | WinTrek/trekigc.cpp:3014 |
| 3 | SIM-0392 | sounds | PlaySoundEffect@WinTrek/trekigc.cpp:3075 newCriticalMsgSound | partial | WinTrek/trekigc.cpp:3075 |
| 3 | SIM-0393 | sounds | PlaySoundEffect@WinTrek/trekigc.cpp:3080 newNonCriticalMsgSound | partial | WinTrek/trekigc.cpp:3080 |
| 3 | SIM-0394 | sounds | PlaySoundEffect@WinTrek/trekigc.cpp:3998 jumpSound | partial | WinTrek/trekigc.cpp:3998 |
| 3 | SIM-0398 | sounds | PlaySoundEffect@WinTrek/trekigc.cpp:4415 jumpSound | partial | WinTrek/trekigc.cpp:4415 |
| 3 | SIM-0406 | sounds | PlaySoundEffect@WinTrek/trekigc.cpp:4882 salAutopilotEngageSound | partial | WinTrek/trekigc.cpp:4882 |
| 3 | SIM-0414 | sounds | PlayNotificationSound@clintlib/appmsg.cpp:237 salAutopilotEngageSound | partial | clintlib/appmsg.cpp:237 |
| 3 | SIM-0420 | sounds | PlaySoundEffect@clintlib/appmsg.cpp:339 jumpSound | partial | clintlib/appmsg.cpp:339 |
| 3 | SIM-0427 | sounds | PlaySoundEffect@clintlib/appmsg.cpp:1057 jumpSound | partial | clintlib/appmsg.cpp:1057 |
| 3 | SIM-0439 | sounds | PlaySoundEffect@clintlib/appmsg.cpp:3326 GetCompletionSound | partial | clintlib/appmsg.cpp:3326 |
| 3 | SIM-0448 | sounds | PlaySoundEffect@clintlib/clintlib.cpp:3257 deployProbeSound | partial | clintlib/clintlib.cpp:3257 |
| 3 | SIM-0454 | sounds | ReceiveChat@clintlib/clintlib.cpp:3934 salQuitSound | missing | clintlib/clintlib.cpp:3934 |
| 3 | SIM-0455 | sounds | ReceiveChat@clintlib/clintlib.cpp:3939 salQuitSound | missing | clintlib/clintlib.cpp:3939 |
| 3 | SIM-0456 | sounds | ReceiveChat@clintlib/clintlib.cpp:3944 salQuitSound | missing | clintlib/clintlib.cpp:3944 |
| 3 | SIM-0457 | sounds | ReceiveChat@clintlib/clintlib.cpp:3952 salQuitSound | missing | clintlib/clintlib.cpp:3952 |
| 3 | SIM-0458 | sounds | ReceiveChat@clintlib/clintlib.cpp:3960 salQuitSound | missing | clintlib/clintlib.cpp:3960 |
| 3 | SIM-0459 | sounds | ReceiveChat@clintlib/clintlib.cpp:3965 salQuitSound | missing | clintlib/clintlib.cpp:3965 |
| 3 | SIM-0460 | sounds | ReceiveChat@clintlib/clintlib.cpp:3970 salQuitSound | missing | clintlib/clintlib.cpp:3970 |
| 3 | SIM-0461 | sounds | ReceiveChat@clintlib/clintlib.cpp:3975 salChangeSidesSound | missing | clintlib/clintlib.cpp:3975 |
| 3 | SIM-0462 | sounds | ReceiveChat@clintlib/clintlib.cpp:3991 salQuitSound | missing | clintlib/clintlib.cpp:3991 |
| 3 | SIM-0463 | sounds | ReceiveChat@clintlib/clintlib.cpp:3999 salMIASound | missing | clintlib/clintlib.cpp:3999 |
| 3 | SIM-0464 | sounds | ReceiveChat@clintlib/clintlib.cpp:4155 salRecruitsArrivedSound | missing | clintlib/clintlib.cpp:4155 |
| 3 | SIM-0465 | sounds | ReceiveChat@clintlib/clintlib.cpp:4162 salEnemyJoinersSound | missing | clintlib/clintlib.cpp:4162 |
| 3 | SIM-0468 | sounds | dynamic_leave_voice@4006 | missing | clintlib/clintlib.cpp:4006; clintlib/clintlib.cpp:4009 |
| 3 | SIM-0470 | periodic | ship_status_delta_and_turret_updates | partial | FedSrv/FedSrv.CPP:3693 |
| 3 | SIM-0471 | periodic | target_hull_shield_fraction_updates | partial | FedSrv/FedSrv.CPP:4079 |
| 2 | SIM-0014 | constants | c_fcidRipcordTime | partial | Igc/igc.h:73; FedSrv/FedSrv.CPP:1676 |
| 2 | SIM-0074 | updates | mine_expiry_boundary | partial | Igc/mineigc.cpp:150 |
| 2 | SIM-0078 | updates | model_spin | partial | Igc/modelIGC.cpp:76 |
| 2 | SIM-0108 | updates | miner_drilling_controls | partial | Igc/shipIGC.cpp:2254 |
| 2 | SIM-0145 | periodic | chat_budget_recovery | partial | FedSrv/FedSrv.CPP:8282; FedSrv/fsship.h:449-468 |
| 2 | SIM-0147 | periodic | network_roll_call_and_disconnect | partial | FedSrv/FedSrv.CPP:7942 |
| 2 | SIM-0234 | sounds | PlayNotificationSound@Igc/afterburnerIGC.cpp:119 mountedSound | missing | Igc/afterburnerIGC.cpp:119 |
| 2 | SIM-0237 | sounds | PlayNotificationSound@Igc/cloakigc.cpp:75 mountedSound | missing | Igc/cloakigc.cpp:75 |
| 2 | SIM-0238 | sounds | PlayNotificationSound@Igc/dispenserigc.cpp:98 mountedSound | missing | Igc/dispenserigc.cpp:98 |
| 2 | SIM-0239 | sounds | PlayNotificationSound@Igc/magazineigc.cpp:96 mountedSound | missing | Igc/magazineigc.cpp:96 |
| 2 | SIM-0240 | sounds | PlayNotificationSound@Igc/shieldIGC.cpp:82 mountedSound | missing | Igc/shieldIGC.cpp:82 |
| 2 | SIM-0241 | sounds | PlaySoundEffect@Igc/shieldIGC.cpp:94 activateSound | missing | Igc/shieldIGC.cpp:94 |
| 2 | SIM-0242 | sounds | PlaySoundEffect@Igc/shieldIGC.cpp:185 deactivateSound | missing | Igc/shieldIGC.cpp:185 |
| 2 | SIM-0243 | sounds | PlayNotificationSound@Igc/shieldIGC.cpp:186 salShieldsOfflineSound | missing | Igc/shieldIGC.cpp:186 |
| 2 | SIM-0248 | sounds | PlayNotificationSound@Igc/shipIGC.cpp:1608 salCloakEngageSound | missing | Igc/shipIGC.cpp:1608 |
| 2 | SIM-0263 | sounds | PlayNotificationSound@Igc/shipIGC.h:616 salReloadingAmmoSound | missing | Igc/shipIGC.h:616 |
| 2 | SIM-0264 | sounds | PlayNotificationSound@Igc/shipIGC.h:617 startReloadSound | missing | Igc/shipIGC.h:617 |
| 2 | SIM-0265 | sounds | PlayNotificationSound@Igc/shipIGC.h:646 salReloadingFuelSound | missing | Igc/shipIGC.h:646 |
| 2 | SIM-0266 | sounds | PlayNotificationSound@Igc/shipIGC.h:647 startReloadSound | missing | Igc/shipIGC.h:647 |
| 2 | SIM-0267 | sounds | PlayNotificationSound@Igc/shipIGC.h:1302 salCargoFullSound | missing | Igc/shipIGC.h:1302 |
| 2 | SIM-0268 | sounds | PlayNotificationSound@Igc/shipIGC.h:1307 pickUpPartSound | missing | Igc/shipIGC.h:1307 |
| 2 | SIM-0272 | sounds | StartSound@WinTrek/Slideshow.cpp:105 soundID | missing | WinTrek/Slideshow.cpp:105 |
| 2 | SIM-0273 | sounds | StartSound@WinTrek/WinTrek.cpp:1108 GetSound | missing | WinTrek/WinTrek.cpp:1108 |
| 2 | SIM-0274 | sounds | PlaySoundEffect@WinTrek/WinTrek.cpp:1110 id | missing | WinTrek/WinTrek.cpp:1110 |
| 2 | SIM-0275 | sounds | StartSound@WinTrek/WinTrek.cpp:6567 personalJumpSound | missing | WinTrek/WinTrek.cpp:6567 |
| 2 | SIM-0279 | sounds | StartSound@WinTrek/WinTrek.cpp:8827 newTargetSound | missing | WinTrek/WinTrek.cpp:8827 |
| 2 | SIM-0280 | sounds | PlaySoundEffect@WinTrek/WinTrek.cpp:9047 ungroupWeaponsSound | missing | WinTrek/WinTrek.cpp:9047 |
| 2 | SIM-0281 | sounds | PlaySoundEffect@WinTrek/WinTrek.cpp:9053 groupWeaponsSound | missing | WinTrek/WinTrek.cpp:9053 |
| 2 | SIM-0282 | sounds | PlaySoundEffect@WinTrek/WinTrek.cpp:9098 mountSound | missing | WinTrek/WinTrek.cpp:9098 |
| 2 | SIM-0283 | sounds | PlaySoundEffect@WinTrek/WinTrek.cpp:9102 unmountSound | missing | WinTrek/WinTrek.cpp:9102 |
| 2 | SIM-0284 | sounds | PlaySoundEffect@WinTrek/WinTrek.cpp:9114 salCargoFullSound | missing | WinTrek/WinTrek.cpp:9114 |
| 2 | SIM-0285 | sounds | PlaySoundEffect@WinTrek/WinTrek.cpp:9269 positiveButtonClickSound | missing | WinTrek/WinTrek.cpp:9269 |
| 2 | SIM-0286 | sounds | PlaySoundEffect@WinTrek/WinTrek.cpp:9463 vectorLockSound | missing | WinTrek/WinTrek.cpp:9463 |
| 2 | SIM-0287 | sounds | PlayNotificationSound@WinTrek/WinTrek.cpp:9484 salShipCantRipcordSound | missing | WinTrek/WinTrek.cpp:9484 |
| 2 | SIM-0288 | sounds | PlaySoundEffect@WinTrek/WinTrek.cpp:9529 groupWeaponsSound | missing | WinTrek/WinTrek.cpp:9529 |
| 2 | SIM-0289 | sounds | PlaySoundEffect@WinTrek/WinTrek.cpp:9531 ungroupWeaponsSound | missing | WinTrek/WinTrek.cpp:9531 |
| 2 | SIM-0290 | sounds | PlayNotificationSound@WinTrek/WinTrek.cpp:9693 salReloadingSound | missing | WinTrek/WinTrek.cpp:9693 |
| 2 | SIM-0291 | sounds | PlaySoundEffect@WinTrek/WinTrek.cpp:9719 dropSound | missing | WinTrek/WinTrek.cpp:9719 |
| 2 | SIM-0296 | sounds | PlaySoundEffect@WinTrek/WinTrek.cpp:10658 acceptCommandSound | missing | WinTrek/WinTrek.cpp:10658 |
| 2 | SIM-0300 | sounds | PlaySoundEffect@WinTrek/WinTrek.cpp:11157 countdown10minSound | missing | WinTrek/WinTrek.cpp:11157 |
| 2 | SIM-0301 | sounds | PlaySoundEffect@WinTrek/WinTrek.cpp:11161 countdown5minSound | missing | WinTrek/WinTrek.cpp:11161 |
| 2 | SIM-0302 | sounds | PlaySoundEffect@WinTrek/WinTrek.cpp:11165 countdown1minSound | missing | WinTrek/WinTrek.cpp:11165 |
| 2 | SIM-0303 | sounds | PlaySoundEffect@WinTrek/WinTrek.cpp:11169 countdown30Sound | missing | WinTrek/WinTrek.cpp:11169 |
| 2 | SIM-0317 | sounds | PlaySoundEffect@WinTrek/console.cpp:1268 chatKeySound | missing | WinTrek/console.cpp:1268 |
| 2 | SIM-0318 | sounds | PlaySoundEffect@WinTrek/console.cpp:2339 chatKeySound | missing | WinTrek/console.cpp:2339 |
| 2 | SIM-0319 | sounds | PlaySoundEffect@WinTrek/console.cpp:2424 chatKeySound | missing | WinTrek/console.cpp:2424 |
| 2 | SIM-0320 | sounds | PlaySoundEffect@WinTrek/console.cpp:3274 paneSlideSound | missing | WinTrek/console.cpp:3274 |
| 2 | SIM-0321 | sounds | PlaySoundEffect@WinTrek/load.cpp:236 mouseoverSound | missing | WinTrek/load.cpp:236 |
| 2 | SIM-0322 | sounds | PlaySoundEffect@WinTrek/load.cpp:1487 mountSound | missing | WinTrek/load.cpp:1487 |
| 2 | SIM-0323 | sounds | PlaySoundEffect@WinTrek/load.cpp:1887 mountSound | missing | WinTrek/load.cpp:1887 |
| 2 | SIM-0324 | sounds | PlaySoundEffect@WinTrek/load.cpp:1889 unmountSound | missing | WinTrek/load.cpp:1889 |
| 2 | SIM-0325 | sounds | PlaySoundEffect@WinTrek/load.cpp:2235 positiveButtonClickSound | missing | WinTrek/load.cpp:2235 |
| 2 | SIM-0326 | sounds | PlaySoundEffect@WinTrek/load.cpp:2247 mountSound | missing | WinTrek/load.cpp:2247 |
| 2 | SIM-0327 | sounds | PlaySoundEffect@WinTrek/loadout.cpp:1178 mouseoverSound | missing | WinTrek/loadout.cpp:1178 |
| 2 | SIM-0328 | sounds | PlaySoundEffect@WinTrek/sectormap.cpp:1362 errorSound | missing | WinTrek/sectormap.cpp:1362 |
| 2 | SIM-0329 | sounds | PlaySoundEffect@WinTrek/sectormap.cpp:1367 errorSound | missing | WinTrek/sectormap.cpp:1367 |
| 2 | SIM-0330 | sounds | PlaySoundEffect@WinTrek/sectormap.cpp:1442 mouseoverSound | missing | WinTrek/sectormap.cpp:1442 |
| 2 | SIM-0331 | sounds | PlaySoundEffect@WinTrek/sectormap.cpp:1523 mouseclickSound | missing | WinTrek/sectormap.cpp:1523 |
| 2 | SIM-0332 | sounds | PlaySoundEffect@WinTrek/sectormap.cpp:1586 mouseclickSound | missing | WinTrek/sectormap.cpp:1586 |
| 2 | SIM-0333 | sounds | PlaySoundEffect@WinTrek/teampane.cpp:1177 positiveButtonClickSound | missing | WinTrek/teampane.cpp:1177 |
| 2 | SIM-0334 | sounds | PlaySoundEffect@WinTrek/teamscreen.cpp:2209 mouseclickSound | missing | WinTrek/teamscreen.cpp:2209 |
| 2 | SIM-0335 | sounds | PlaySoundEffect@WinTrek/teamscreen.cpp:2217 mouseclickSound | missing | WinTrek/teamscreen.cpp:2217 |
| 2 | SIM-0336 | sounds | PlaySoundEffect@WinTrek/teamscreen.cpp:2438 mouseclickSound | missing | WinTrek/teamscreen.cpp:2438 |
| 2 | SIM-0337 | sounds | PlaySoundEffect@WinTrek/teleportpane.cpp:285 noTurretsSound | missing | WinTrek/teleportpane.cpp:285 |
| 2 | SIM-0338 | sounds | PlaySoundEffect@WinTrek/teleportpane.cpp:320 personalJumpSound | missing | WinTrek/teleportpane.cpp:320 |
| 2 | SIM-0345 | sounds | StartSound@WinTrek/trekigc.cpp:714 m_idAmbientSound | partial | WinTrek/trekigc.cpp:714 |
| 2 | SIM-0348 | sounds | PlaySoundIf@WinTrek/trekigc.cpp:990 turnSound | missing | WinTrek/trekigc.cpp:990 |
| 2 | SIM-0361 | sounds | StartSound@WinTrek/trekigc.cpp:1264 hullLowSound | missing | WinTrek/trekigc.cpp:1264 |
| 2 | SIM-0362 | sounds | StartSound@WinTrek/trekigc.cpp:1266 ammoLowSound | missing | WinTrek/trekigc.cpp:1266 |
| 2 | SIM-0363 | sounds | StartSound@WinTrek/trekigc.cpp:1268 fuelLowSound | missing | WinTrek/trekigc.cpp:1268 |
| 2 | SIM-0364 | sounds | StartSound@WinTrek/trekigc.cpp:1270 energyLowSound | missing | WinTrek/trekigc.cpp:1270 |
| 2 | SIM-0365 | sounds | StartSound@WinTrek/trekigc.cpp:1273 salNoAmmoSound | missing | WinTrek/trekigc.cpp:1273 |
| 2 | SIM-0366 | sounds | StartSound@WinTrek/trekigc.cpp:1275 salNoFuelSound | missing | WinTrek/trekigc.cpp:1275 |
| 2 | SIM-0371 | sounds | PlaySoundIf@WinTrek/trekigc.cpp:1310 m_pGunnerInteriorSound | missing | WinTrek/trekigc.cpp:1310 |
| 2 | SIM-0376 | sounds | PlaySoundIf@WinTrek/trekigc.cpp:1326 SidethrustInteriorSound | missing | WinTrek/trekigc.cpp:1326 |
| 2 | SIM-0377 | sounds | PlaySoundIf@WinTrek/trekigc.cpp:1329 CapSidethrustInteriorSound | missing | WinTrek/trekigc.cpp:1329 |
| 2 | SIM-0382 | sounds | PlaySoundIf@WinTrek/trekigc.cpp:1351 SidethrustExteriorSound | missing | WinTrek/trekigc.cpp:1351 |
| 2 | SIM-0383 | sounds | PlaySoundIf@WinTrek/trekigc.cpp:1354 CapSidethrustExteriorSound | missing | WinTrek/trekigc.cpp:1354 |
| 2 | SIM-0388 | sounds | PlaySoundEffect@WinTrek/trekigc.cpp:2859 dockedSound | missing | WinTrek/trekigc.cpp:2859 |
| 2 | SIM-0389 | sounds | PlaySoundEffect@WinTrek/trekigc.cpp:2860 salWelcomeHomeSound | missing | WinTrek/trekigc.cpp:2860 |
| 2 | SIM-0395 | sounds | PlayNotificationSound@WinTrek/trekigc.cpp:4237 salRepairedAtCarrierSound | missing | WinTrek/trekigc.cpp:4237 |
| 2 | SIM-0399 | sounds | PlaySoundEffect@WinTrek/trekigc.cpp:4536 idSonicChat | missing | WinTrek/trekigc.cpp:4536 |
| 2 | SIM-0400 | sounds | PlaySoundEffect@WinTrek/trekigc.cpp:4540 newCommandMsgSound | missing | WinTrek/trekigc.cpp:4540 |
| 2 | SIM-0401 | sounds | PlaySoundEffect@WinTrek/trekigc.cpp:4546 newChatMsgFromCommanderSound | missing | WinTrek/trekigc.cpp:4546 |
| 2 | SIM-0402 | sounds | PlaySoundEffect@WinTrek/trekigc.cpp:4548 newPersonalMsgSound | missing | WinTrek/trekigc.cpp:4548 |
| 2 | SIM-0403 | sounds | PlaySoundEffect@WinTrek/trekigc.cpp:4555 newChatMsgFromCommanderSound | missing | WinTrek/trekigc.cpp:4555 |
| 2 | SIM-0404 | sounds | PlaySoundEffect@WinTrek/trekigc.cpp:4557 newChatMsgSound | missing | WinTrek/trekigc.cpp:4557 |
| 2 | SIM-0405 | sounds | PlaySoundEffect@WinTrek/trekigc.cpp:4560 newOfflineChatMsgSound | missing | WinTrek/trekigc.cpp:4560 |
| 2 | SIM-0410 | sounds | StartSound@WinTrek/trekigc.cpp:5061 missileLockSound | missing | WinTrek/trekigc.cpp:5061 |
| 2 | SIM-0411 | sounds | StartSound@WinTrek/trekigc.cpp:5085 m_idAmbient | partial | WinTrek/trekigc.cpp:5085 |
| 2 | SIM-0412 | sounds | StartSound@WinTrek/trekmdl.cpp:471 m_soundId | missing | WinTrek/trekmdl.cpp:471 |
| 2 | SIM-0413 | sounds | PlayNotificationSound@clintlib/appmsg.cpp:163 salRepairedAtCarrierSound | missing | clintlib/appmsg.cpp:163 |
| 2 | SIM-0415 | sounds | PlaySoundEffect@clintlib/appmsg.cpp:282 pickUpPowerupSound | missing | clintlib/appmsg.cpp:282 |
| 2 | SIM-0416 | sounds | PlaySoundEffect@clintlib/appmsg.cpp:286 pickUpDevelopmentSound | missing | clintlib/appmsg.cpp:286 |
| 2 | SIM-0417 | sounds | PlaySoundEffect@clintlib/appmsg.cpp:291 pickUpCashSound | missing | clintlib/appmsg.cpp:291 |
| 2 | SIM-0418 | sounds | PlaySoundEffect@clintlib/appmsg.cpp:303 pickUpDevelopmentSound | missing | clintlib/appmsg.cpp:303 |
| 2 | SIM-0425 | sounds | PlayNotificationSound@clintlib/appmsg.cpp:970 salNoRipcordSound | missing | clintlib/appmsg.cpp:970 |
| 2 | SIM-0428 | sounds | PlaySoundEffect@clintlib/appmsg.cpp:1238 investorSound | missing | clintlib/appmsg.cpp:1238 |
| 2 | SIM-0429 | sounds | PlaySoundEffect@clintlib/appmsg.cpp:1313 paydaySound | missing | clintlib/appmsg.cpp:1313 |
| 2 | SIM-0430 | sounds | PlaySoundEffect@clintlib/appmsg.cpp:1757 newCommandMsgSound | missing | clintlib/appmsg.cpp:1757 |
| 2 | SIM-0431 | sounds | PlaySoundEffect@clintlib/appmsg.cpp:1894 personalJumpSound | missing | clintlib/appmsg.cpp:1894 |
| 2 | SIM-0432 | sounds | PlaySoundEffect@clintlib/appmsg.cpp:2071 boardSound | missing | clintlib/appmsg.cpp:2071 |
| 2 | SIM-0433 | sounds | PlaySoundEffect@clintlib/appmsg.cpp:2481 salPlayerWaitingSound | missing | clintlib/appmsg.cpp:2481 |
| 2 | SIM-0434 | sounds | PlaySoundEffect@clintlib/appmsg.cpp:2896 destroyedSound | missing | clintlib/appmsg.cpp:2896 |
| 2 | SIM-0435 | sounds | PlaySoundEffect@clintlib/appmsg.cpp:2965 commanderSound | missing | clintlib/appmsg.cpp:2965 |
| 2 | SIM-0436 | sounds | PlaySoundEffect@clintlib/appmsg.cpp:3149 enemyFlagLostSound | missing | clintlib/appmsg.cpp:3149 |
| 2 | SIM-0437 | sounds | PlaySoundEffect@clintlib/appmsg.cpp:3156 artifactFoundSound | missing | clintlib/appmsg.cpp:3156 |
| 2 | SIM-0438 | sounds | PlaySoundEffect@clintlib/appmsg.cpp:3169 flagLostSound | missing | clintlib/appmsg.cpp:3169 |
| 2 | SIM-0443 | sounds | PlayNotificationSound@clintlib/clintlib.cpp:3216 salReloadingMissilesSound | missing | clintlib/clintlib.cpp:3216 |
| 2 | SIM-0444 | sounds | PlaySoundEffect@clintlib/clintlib.cpp:3217 startReloadSound | missing | clintlib/clintlib.cpp:3217 |
| 2 | SIM-0445 | sounds | PlayNotificationSound@clintlib/clintlib.cpp:3224 salMissilesDepletedSound | missing | clintlib/clintlib.cpp:3224 |
| 2 | SIM-0446 | sounds | PlaySoundEffect@clintlib/clintlib.cpp:3249 deployChaffSound | missing | clintlib/clintlib.cpp:3249 |
| 2 | SIM-0447 | sounds | PlaySoundEffect@clintlib/clintlib.cpp:3253 deployMineSound | missing | clintlib/clintlib.cpp:3253 |
| 2 | SIM-0449 | sounds | PlayNotificationSound@clintlib/clintlib.cpp:3394 salReloadingChaffSound | missing | clintlib/clintlib.cpp:3394 |
| 2 | SIM-0450 | sounds | PlayNotificationSound@clintlib/clintlib.cpp:3399 salReloadingDispenserSound | missing | clintlib/clintlib.cpp:3399 |
| 2 | SIM-0451 | sounds | PlaySoundEffect@clintlib/clintlib.cpp:3404 startReloadSound | missing | clintlib/clintlib.cpp:3404 |
| 2 | SIM-0452 | sounds | PlayNotificationSound@clintlib/clintlib.cpp:3412 salChaffDepletedSound | missing | clintlib/clintlib.cpp:3412 |
| 2 | SIM-0453 | sounds | PlayNotificationSound@clintlib/clintlib.cpp:3417 salDispenserEmptySound | missing | clintlib/clintlib.cpp:3417 |
| 2 | SIM-0467 | sounds | PlayNotificationSound@clintlib/clintlib.h:1175 salNoRipcordSound | missing | clintlib/clintlib.h:1175 |
| 1 | SIM-0137 | periodic | lobby_dirty_rate_limit | partial | FedSrv/fsmission.cpp:5606 |

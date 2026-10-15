# P678 — parity wave 5: client lobby control messages

Completed six rows (23 weight points) in the working tree. No commits or pushes. Baseline was captured before edits with `python3 scripts/parity-scorecard.py`; the same command validates both final matrices.

## Bundle and exclusions

The six selected rows share `ConnectionSession` receive dispatch, `ClientWorld` lobby state, the preflight UI, and existing server lobby relays. Five weigh 4 and DELPOSITIONREQ weighs 3. Completing the receive effects closes a coherent high-weight bundle without changing simulation scheduling, broadcasting policy, AI, or economy. The full initial gap inventory, sorted across both matrices by descending weight, is below.

Excluded: SIM-0063 and all `Sector.Update` structure (P667); broadcast pipeline / interest tiers (P677); security (P653–P655); rendering LOD / FSR (P628/P643); Android; installer (P644). No Core or Server production code was edited, and the simulation matrix is unchanged. CHANGE_TEAM_CIV was left partial because faithful civilization changes also reset technologies and station types (`~/Development/Allegiance/src/Igc/sideigc.h:76-103`), outside this client-notice bundle.

## Native rules and implementation evidence

Native citations below are relative to `~/Development/Allegiance/src/`; sources were read locally. Each selected main-matrix row now includes handler, implementation, and test file:line evidence. `docs/parity/fedmsg-wiring.csv` and the executable dispatch registry agree for these six messages.

| Row | Weight | Native rule | Port and tests |
| --- | ---: | --- | --- |
| 18 CANCEL_BALLOT | 4 | Inc/Messages.h:179; clintlib/appmsg.cpp:377-392; clintlib/clintlib.cpp:2497-2522 | src/Allegiance.ClientCore/Session/ConnectionSession.cs:911; src/Allegiance.ClientCore/State/ClientWorld.cs:1009; src/Allegiance.ClientCore/State/ClientWorld.cs:1011; src/Allegiance.ClientCore.Game/Game1.Rendering.cs:1680; tests/Allegiance.ClientCore.Tests/P678LobbyMessageTests.cs:100; tests/Allegiance.ClientCore.Tests/P678LobbyMessageTests.cs:115; P678: Remove all matching queued ballots; preserve unrelated ballots; native FIFO/strict expiry and vote dequeue. No native or port production sender found; real encoded receive replay covers cancellation. |
| 40 POSITIONREQ | 4 | Inc/Messages.h:325; clintlib/appmsg.cpp:2470-2483; clintlib/clintlib.cpp:943-954 | src/Allegiance.ClientCore/Session/ConnectionSession.cs:914; src/Allegiance.ClientCore/State/ClientWorld.Lobby.cs:20; tests/Allegiance.ClientCore.Tests/P678LobbyMessageTests.cs:50; src/Allegiance.ClientCore.Game/Rendering/LobbyStatusLayout.cs:14; tests/Allegiance.ClientCore.MonoGame.Tests/P678LobbyStatusTests.cs:13; tests/Allegiance.ClientCore.Tests/P678LobbyRelayLiveTests.cs:45; P678: Side/ship request membership, duplicate/member guard, own-leader critical notice and native player-waiting voice; live receipt on both peers. |
| 42 DELPOSITIONREQ | 3 | Inc/Messages.h:366; clintlib/appmsg.cpp:2486-2499; WinTrek/teamscreen.cpp:3545-3658; clintlib/clintlib.cpp:4133-4135 | src/Allegiance.ClientCore/Session/ConnectionSession.cs:917; src/Allegiance.ClientCore/State/ClientWorld.Lobby.cs:39; tests/Allegiance.ClientCore.Tests/P678LobbyMessageTests.cs:74; src/Allegiance.ClientCore.Game/Rendering/LobbyStatusLayout.cs:14; tests/Allegiance.ClientCore.MonoGame.Tests/P678LobbyStatusTests.cs:13; tests/Allegiance.ClientCore.Tests/P678LobbyRelayLiveTests.cs:45; P678: Scoped removal plus bounded result events even for absent players/requests; own rejection UI, quiet cancellation; JOIN_SIDE satisfies membership. |
| 43 LOCK_LOBBY | 4 | Inc/Messages.h:372; clintlib/appmsg.cpp:2611-2617 | src/Allegiance.ClientCore/Session/ConnectionSession.cs:920; src/Allegiance.ClientCore/State/ClientWorld.Lobby.cs:49; tests/Allegiance.ClientCore.Tests/P678LobbyMessageTests.cs:27; src/Allegiance.ClientCore.Game/Rendering/LobbyStatusLayout.cs:14; tests/Allegiance.ClientCore.MonoGame.Tests/P678LobbyStatusTests.cs:13; tests/Allegiance.ClientCore.Tests/P678LobbyRelayLiveTests.cs:45; P678: Authoritative mission lobby lock state, echo-only mutation, shown in side-select/ready-check; live on/off relay. |
| 44 LOCK_SIDES | 4 | Inc/Messages.h:376; clintlib/appmsg.cpp:2620-2627 | src/Allegiance.ClientCore/Session/ConnectionSession.cs:923; src/Allegiance.ClientCore/State/ClientWorld.Lobby.cs:50; tests/Allegiance.ClientCore.Tests/P678LobbyMessageTests.cs:27; src/Allegiance.ClientCore.Game/Rendering/LobbyStatusLayout.cs:14; tests/Allegiance.ClientCore.MonoGame.Tests/P678LobbyStatusTests.cs:13; tests/Allegiance.ClientCore.Tests/P678LobbyRelayLiveTests.cs:45; P678: Authoritative side lock state independent of lobby lock, shown in lobby screens; live locked refusal/unlock receipt. |
| 52 AUTO_ACCEPT | 4 | Inc/Messages.h:413; clintlib/appmsg.cpp:2602-2609 | src/Allegiance.ClientCore/Session/ConnectionSession.cs:926; src/Allegiance.ClientCore/State/ClientWorld.Lobby.cs:48; tests/Allegiance.ClientCore.Tests/P678LobbyMessageTests.cs:27; src/Allegiance.ClientCore.Game/Rendering/LobbyStatusLayout.cs:14; tests/Allegiance.ClientCore.MonoGame.Tests/P678LobbyStatusTests.cs:13; tests/Allegiance.ClientCore.Tests/P678LobbyRelayLiveTests.cs:45; P678: Per-side authoritative auto-accept state and lobby display; live on/off relay and pending-request acceptance sweep. |

POSITIONREQ stores a set of `(side, ship)` pairs, suppresses duplicate/member entries, posts critical feedback only to the requested side's own leader, and queues the native `salPlayerWaitingSound` for each arrival (`clintlib/appmsg.cpp:2470-2483`; `clintlib/clintlib.cpp:943-954`). The real bundled `vo_sal_playerwaiting.ogg` comes from the local artwork corpus; its native identifier is `Igc/sounds.h:501`, and the wave binding is `~/Development/Artwork/sounddef.mdl:824`. Names are resolved from known player data, with a pilot-id fallback for late/absent identity exports.

DELPOSITIONREQ removes only the addressed pair and records the reason even without a player/request (`clintlib/appmsg.cpp:2486-2499`). The UI retains the local player's result independently of other players' requests and suppresses rejection text for cancellation (`WinTrek/teamscreen.cpp:3545-3658`). JOIN_SIDE satisfies the corresponding request (`clintlib/clintlib.cpp:4133-4135`). Reconnect and next-mission reset drop settings, requests, result events, and ballots.

CANCEL_BALLOT removes **all** matching queued IDs, preserving unrelated ballots (`clintlib/appmsg.cpp:377-392`). The old single-ballot latch could not satisfy this rule. BALLOT now queues in arrival order; the current vote is the oldest unexpired entry, expiration is strictly `< now`, and voting dequeues the answered ballot (`clintlib/clintlib.cpp:2497-2522`). The port's existing server clock sample/scale supplies the expiration epoch. Native and port production code have no CANCEL_BALLOT sender; cancellation is therefore verified by encoded packets through the real receive dispatcher, without inventing a server emission.

LOCK_LOBBY and LOCK_SIDES update independent mission flags; AUTO_ACCEPT updates only the addressed side (`clintlib/appmsg.cpp:2602-2627`). The client applies them on server receipt. Side-select and ready-check display these values and pending/rejected requests under the session gate. The added send methods use the existing codecs and server authority checks.

## Scorecard before → after

Partial earns zero credit, as defined by the repository scorecard. Eligible denominators and row counts are unchanged.

| Matrix / area | Weighted before → after | Complete / eligible before → after |
| --- | --- | --- |
| parity-matrix.csv / commander | 100.00% (81/81) → 100.00% (81/81) | 19/19 → 19/19 |
| parity-matrix.csv / commands | 90.19% (533/591) → 90.19% (533/591) | 162/190 → 162/190 |
| parity-matrix.csv / fedmsg | 81.55% (420/515) → 86.02% (443/515) | 101/125 → 107/125 |
| parity-matrix.csv / lobby | 76.47% (39/51) → 76.47% (39/51) | 9/12 → 9/12 |
| parity-matrix.csv / parts | 88.24% (135/153) → 88.24% (135/153) | 34/40 → 34/40 |
| parity-matrix.csv / screens | 86.81% (79/91) → 86.81% (79/91) | 19/24 → 19/24 |
| parity-matrix.csv / TOTAL | 86.84% (1287/1482) → 88.39% (1310/1482) | 344/410 → 350/410 |
| sim-rules-matrix.csv / callbacks | 33.33% (66/198) → 33.33% (66/198) | 22/66 → 22/66 |
| sim-rules-matrix.csv / constants | 95.56% (129/135) → 95.56% (129/135) | 37/39 → 37/39 |
| sim-rules-matrix.csv / periodic | 17.02% (8/47) → 17.02% (8/47) | 2/15 → 2/15 |
| sim-rules-matrix.csv / sounds | 15.99% (95/594) → 15.99% (95/594) | 28/228 → 28/228 |
| sim-rules-matrix.csv / updates | 57.88% (202/349) → 57.88% (202/349) | 46/87 → 46/87 |
| sim-rules-matrix.csv / warnings | 100.00% (10/10) → 100.00% (10/10) | 2/2 → 2/2 |
| sim-rules-matrix.csv / TOTAL | 38.26% (510/1333) → 38.26% (510/1333) | 137/437 → 137/437 |

Main-matrix credit increases by **23**, from 1287 to 1310; completed rows increase from 344 to 350. Fedmsg rises from **81.55% to 86.02%**; the main total rises from **86.84% to 88.39%**. Simulation stays **38.26%**.

## Verification

`dotnet build AllegianceCore.slnx --nologo -m:1 -v:q` completed with **0 warnings / 0 errors**, including the desktop content pipeline. All final production/test changes preceded this build; only reports and matrix evidence were edited afterward. Source/test fingerprints were checked after the suites.

**71 affected methods / 86 distinct data cases**, including **11 new methods / 17 new cases**, passed **3/3 exact-method isolated invocations**. Each theory invocation executes all its data rows. The triplets cover the new receive/state/UI/loopback behavior, ballot regression, dispatch completeness, reconnect/reset, existing lobby request/toggle paths, lobby/team layouts, and scorecard validation. The latest three successful invocations for each method are recorded in [P678_RESULTS.json](P678_RESULTS.json).

Full suites ran with `dotnet test tests/<assembly> --no-build --no-restore --logger trx --results-directory /tmp/p678/results`. ClientCore additionally used `ALLEGIANCE_LIVE_P660_SHOWCASE=1`. No live/loopback tests were filtered out.

| Assembly | Passed | Failed in full pass | Skipped | Total |
| --- | ---: | ---: | ---: | ---: |
| Allegiance.Core.Tests | 4353 | 1 | 0 | 4354 |
| Allegiance.ClientCore.Tests | 960 | 0 | 0 | 960 |
| Allegiance.ClientCore.MonoGame.Tests | 2154 | 0 | 3 | 2157 |

An earlier verification runner survived its shell interrupt and completed an overlapping pass. Its results are recorded separately as `additional_full_suites`; the table above uses only the final-build run IDs 255–257. Its Core pass reported 4351 passed / 3 failed; its failures are included in the isolated checks below. No assertions, timing budgets, quarantine tags, or product behavior were changed to accommodate these failures.

All **4 unique failures** observed across the completed passes subsequently passed **3/3 alone**, after both full-suite runners finished. They are load/receipt flakes under the brief's rule and are recorded in [TEST_FLAKES.md](../TEST_FLAKES.md). The full-pass failures remain visible above.

| Test | Full-pass symptom | Final isolated result |
| --- | --- | --- |
| `Allegiance.Core.Tests.LiveRipcordTests.Cancel_while_active_aborts_sector_wide` | Assert.Single() Failure: The collection was empty | 3/3 passed |
| `Allegiance.Core.Tests.LiveTreasurePickupTests.Rescue_ship_picking_cash_treasure_credits_side_broadcasts_moneychange_and_destroys_it` | Assert.Single() Failure: The collection was empty | 3/3 passed |
| `Allegiance.Core.Tests.P493EnemySpottingLiveTests.Probe_spot_of_cross_side_ship_delivers_status_and_hidden_to_side0_peer` | Assert.DoesNotContain() Failure: Filter matched in collection | 3/3 passed |
| `Allegiance.Core.Tests.Transport.InboundFloodSafetyLiveTests.Unsupported_fmid_flood_log_is_rate_limited_others_keep_ticking` | healthy bot 0 must keep receiving per-tick updates during the flood (had 0, now 0) | 3/3 passed |

The treasure-pickup receipt check passed 2/3 in its first isolated triplet, then 3/3 in the final triplet. Both triplets are preserved in the invocation ledger.

Skipped cases: `Allegiance.ClientCore.MonoGame.Tests.MeshyPackTests.Pack_built_content_directory_exists`, `Allegiance.ClientCore.MonoGame.Tests.MeshyPackTests.Pack_catalogue_has_the_full_meshy_set`, `Allegiance.ClientCore.MonoGame.Tests.MeshyPackTests.Pack_catalogue_merge_yields_identical_resolution_to_before`. These are the existing optional private Meshy pack cases. Required artwork/native-source checks and loopback tests executed.

The new loopback test uses a real ephemeral TCP server and two real `ConnectionSession` clients. It confirms request receipt on both peers, leader critical feedback, leader rejection, own cancellation, both lock toggles, locked-side refusal, auto-accept on/off, and the real pending-request acceptance sweep. Initial leadership in the existing join burst is emitted before the first peer is registered; the fixture obtains a fresh announcement through real auto-accept/rejoin messages before asserting leader feedback. No server or client state is injected to conceal that existing join-order limitation.

No bot, drone, wingman, mining, or economy implementation was changed. The brief's conditional battle requirement therefore does not apply to this bundle. Normal live/loopback suites run; no rig / 221 / 200v200 live deployment was attempted.

## Initial missing / partial inventory by weight

Captured **366** gaps across both matrices: weight 5: 1, weight 4: 69, weight 3: 149, weight 2: 143, weight 1: 4. Status is the entry baseline; selected rows are marked `partial → done`. All other listed rows remain open: **360 gaps remain** (60 main, 300 sim).

| Weight | Matrix | Area | ID / native item | Baseline → final | Native citation |
| ---: | --- | --- | --- | --- | --- |
| 5 | sim | updates | SIM-0063 swept_collision_time_order | partial | Igc/clusterIGC.cpp:255 |
| 4 | main | commands | TK_ViewTurret | partial | WinTrek/WinTrek.cpp:9858 |
| 4 | main | fedmsg | 106 SET_WINGID | partial | Inc/Messages.h:705 |
| 4 | main | fedmsg | 124 QUIT_MISSION | partial | Inc/Messages.h:812 |
| 4 | main | fedmsg | 126 QUIT_SIDE | partial | Inc/Messages.h:825 |
| 4 | main | fedmsg | 128 ENTER_GAME | partial | Inc/Messages.h:831 |
| 4 | main | fedmsg | 155 GAME_STATE | partial | Inc/Messages.h:854 |
| 4 | main | fedmsg | 156 GAIN_FLAG | partial | Inc/Messages.h:861 |
| 4 | main | fedmsg | 162 ACQUIRE_TREASURE | partial | Inc/Messages.h:871 |
| 4 | main | fedmsg | 164 ADD_PART | partial | Inc/Messages.h:881 |
| 4 | main | fedmsg | 18 CANCEL_BALLOT | partial → done | Inc/Messages.h:179 |
| 4 | main | fedmsg | 182 REQUEST_MONEY | partial | Inc/Messages.h:940 |
| 4 | main | fedmsg | 188 WARP_BOMB | partial | Inc/Messages.h:969 |
| 4 | main | fedmsg | 192 RELAUNCH_SHIP | partial | Inc/Messages.h:988 |
| 4 | main | fedmsg | 194 PINGDATA | partial | Inc/Messages.h:1006 |
| 4 | main | fedmsg | 31 NEW_SHIP_INSTANCE | partial | Inc/Messages.h:262 |
| 4 | main | fedmsg | 40 POSITIONREQ | partial → done | Inc/Messages.h:325 |
| 4 | main | fedmsg | 43 LOCK_LOBBY | partial → done | Inc/Messages.h:372 |
| 4 | main | fedmsg | 44 LOCK_SIDES | partial → done | Inc/Messages.h:376 |
| 4 | main | fedmsg | 51 CHANGE_TEAM_CIV | partial | Inc/Messages.h:407 |
| 4 | main | fedmsg | 52 AUTO_ACCEPT | partial → done | Inc/Messages.h:413 |
| 4 | main | fedmsg | 86 ASTEROID_DESTROYED | partial | Inc/Messages.h:613 |
| 4 | main | fedmsg | 87 FIRE_EXPENDABLE | partial | Inc/Messages.h:619 |
| 4 | main | fedmsg | 89 TREASURE_SETS | partial | Inc/Messages.h:624 |
| 4 | main | fedmsg | 92 PROBES_UPDATE | partial | Inc/Messages.h:644 |
| 4 | main | lobby | Create game / new game wizard | partial | WinTrek/newgamescreen.cpp |
| 4 | main | lobby | Kick / ban / mute (moderation) | partial | moderation |
| 4 | main | lobby | Training missions (tutorial) | partial | WinTrek/trainingscreen.cpp |
| 4 | sim | constants | SIM-0033 c_fcidWarpBombDelay | partial | Igc/igc.h:98; FedSrv/FedSrv.CPP:12551 |
| 4 | sim | periodic | SIM-0133 countdown_game_winner | missing | FedSrv/fsmission.cpp:2335 |
| 4 | sim | periodic | SIM-0135 ambient_treasure_regeneration | missing | FedSrv/fsmission.cpp:2554 |
| 4 | sim | periodic | SIM-0142 auto_start_restart_ready_rules | partial | FedSrv/FedSrv.CPP:8178 |
| 4 | sim | periodic | SIM-0143 start_countdown_expiry | partial | FedSrv/FedSrv.CPP:8237 |
| 4 | sim | sounds | SIM-0253 Complain@Igc/shipIGC.cpp:2199 droneTooCrowdedSound | partial | Igc/shipIGC.cpp:2199 |
| 4 | sim | sounds | SIM-0254 Complain@Igc/shipIGC.cpp:2315 droneWhereToSound | partial | Igc/shipIGC.cpp:2315 |
| 4 | sim | sounds | SIM-0255 Complain@Igc/shipIGC.cpp:2319 GetConstructorNeedRockSound | partial | Igc/shipIGC.cpp:2319 |
| 4 | sim | sounds | SIM-0256 Complain@Igc/shipIGC.cpp:2326 droneWhereToLayMinefieldSound | partial | Igc/shipIGC.cpp:2326 |
| 4 | sim | sounds | SIM-0257 Complain@Igc/shipIGC.cpp:2328 droneWhereToLayTowerSound | partial | Igc/shipIGC.cpp:2328 |
| 4 | sim | sounds | SIM-0396 PlaySoundEffect@WinTrek/trekigc.cpp:4335 GetDestroyedSound | partial | WinTrek/trekigc.cpp:4335 |
| 4 | sim | sounds | SIM-0397 PlaySoundEffect@WinTrek/trekigc.cpp:4337 GetEnemyDestroyedSound | partial | WinTrek/trekigc.cpp:4337 |
| 4 | sim | sounds | SIM-0421 PlaySoundEffect@clintlib/appmsg.cpp:549 GetDestroyedSound | partial | clintlib/appmsg.cpp:549 |
| 4 | sim | sounds | SIM-0422 PlaySoundEffect@clintlib/appmsg.cpp:551 GetEnemyDestroyedSound | partial | clintlib/appmsg.cpp:551 |
| 4 | sim | sounds | SIM-0423 PlaySoundEffect@clintlib/appmsg.cpp:675 GetCapturedSound | partial | clintlib/appmsg.cpp:675 |
| 4 | sim | sounds | SIM-0424 PlaySoundEffect@clintlib/appmsg.cpp:677 GetEnemyCapturedSound | partial | clintlib/appmsg.cpp:677 |
| 4 | sim | sounds | SIM-0442 PlaySoundEffect@clintlib/clintlib.cpp:3103 GetLaunchSound | partial | clintlib/clintlib.cpp:3103 |
| 4 | sim | updates | SIM-0044 ore_seen_by_side | partial | Igc/asteroidigc.h:58 |
| 4 | sim | updates | SIM-0045 asteroid_build_completion | partial | Igc/asteroidigc.h:90 |
| 4 | sim | updates | SIM-0048 build_effect_phase_animation | partial | Igc/buildingeffectigc.h:160 |
| 4 | sim | updates | SIM-0059 docked_npc_launch_rules | partial | Igc/clusterIGC.cpp:116 |
| 4 | sim | updates | SIM-0060 station_consumed_rock_fog | missing | Igc/clusterIGC.cpp:141 |
| 4 | sim | updates | SIM-0062 global_preplot_plot_execute_order | partial | Igc/clusterIGC.cpp:170 |
| 4 | sim | updates | SIM-0064 mine_attrition | partial | Igc/clusterIGC.cpp:325 |
| 4 | sim | updates | SIM-0065 visibility_every_tenth_pass | partial | Igc/clusterIGC.cpp:348 |
| 4 | sim | updates | SIM-0069 dispenser_mount_and_fire | partial | Igc/dispenserigc.cpp:84 |
| 4 | sim | updates | SIM-0075 missile_delayed_steering | partial | Igc/missileigc.cpp:364 |
| 4 | sim | updates | SIM-0076 missile_burnout | partial | Igc/missileigc.cpp:356 |
| 4 | sim | updates | SIM-0081 teleport_probe_activation_edge | partial | Igc/probeigc.cpp:340 |
| 4 | sim | updates | SIM-0082 probe_target_priority | partial | Igc/probeigc.cpp:388 |
| 4 | sim | updates | SIM-0083 probe_first_shot_reveal | missing | Igc/probeigc.cpp:448 |
| 4 | sim | updates | SIM-0088 shield_mount_gate | partial | Igc/shieldIGC.cpp:72 |
| 4 | sim | updates | SIM-0101 npc_plan_legality | partial | Igc/shipIGC.cpp:1938 |
| 4 | sim | updates | SIM-0107 miner_capacity_and_depletion | partial | Igc/shipIGC.cpp:2208 |
| 4 | sim | updates | SIM-0109 constructor_drill_alignment | partial | Igc/shipIGC.cpp:2272 |
| 4 | sim | updates | SIM-0110 idle_npc_complaint_backoff | partial | Igc/shipIGC.cpp:2308 |
| 4 | sim | updates | SIM-0112 wingman_defend_acquire_return | partial | Igc/shipIGC.cpp:2374 |
| 4 | sim | updates | SIM-0113 wingman_unseen_target_memory | missing | Igc/shipIGC.cpp:2410 |
| 4 | sim | updates | SIM-0114 wingman_range_bands | partial | Igc/shipIGC.cpp:2483 |
| 4 | sim | updates | SIM-0115 wingman_missile_evasion_gate | missing | Igc/shipIGC.cpp:2578 |
| 4 | sim | updates | SIM-0119 goto_completion_and_layer_lay | partial | Igc/shipIGC.cpp:2802 |
| 4 | sim | updates | SIM-0129 treasure_deceleration | missing | Igc/treasureIGC.cpp:221 |
| 3 | main | commands | TK_ConModeInvest | partial | WinTrek/WinTrek.cpp:10025 |
| 3 | main | commands | TK_Throttle33 | partial | WinTrek/WinTrek.cpp:10945 |
| 3 | main | commands | TK_Throttle66 | partial | WinTrek/WinTrek.cpp:10951 |
| 3 | main | fedmsg | 42 DELPOSITIONREQ | partial → done | Inc/Messages.h:366 |
| 3 | main | parts | c_eabmCapture | partial | Igc equip ability |
| 3 | main | parts | c_habmCanLtRipcord | partial | Igc/igc.h:739 |
| 3 | main | parts | c_habmCaptureThreat | partial | Igc/igc.h:729 |
| 3 | main | parts | c_habmIsLtRipcordTarget | partial | Igc/igc.h:738 |
| 3 | main | parts | c_habmRemoteLeadIndicator | partial | Igc/igc.h:734 |
| 3 | main | parts | c_habmThreatToStation | partial | Igc/igc.h:735; Igc/igc.h:5390 |
| 3 | main | screens | Selection pane | partial | WinTrek/selpane.cpp |
| 3 | main | screens | Training screen | partial | WinTrek/trainingscreen.cpp |
| 3 | sim | callbacks | SIM-0160 HullTypeCompleted@4771 | partial | Igc/igc.h:4771 ; Igc/bucketigc.h:245 |
| 3 | sim | callbacks | SIM-0161 PartTypeCompleted@4772 | partial | Igc/igc.h:4772 ; Igc/bucketigc.h:253 |
| 3 | sim | callbacks | SIM-0163 BucketChangeEvent@4775 | partial | Igc/igc.h:4775 ; Igc/bucketigc.cpp:98; Igc/bucketigc.h:67; Igc/bucketigc.h:279; Igc/bucketigc.h:290; Igc/bucketigc.h:299 |
| 3 | sim | callbacks | SIM-0164 SideBuildingTechChange@4776 | partial | Igc/igc.h:4776 ; Igc/sideigc.h:166 |
| 3 | sim | callbacks | SIM-0167 StationTypeChange@4779 | partial | Igc/igc.h:4779 ; Igc/stationIGC.cpp:75; Igc/stationIGC.cpp:276 |
| 3 | sim | callbacks | SIM-0168 ClusterUpdateEvent@4781 | partial | Igc/igc.h:4781 ; Igc/clusterIGC.cpp:357 |
| 3 | sim | callbacks | SIM-0169 BuildStation@4783 | partial | Igc/igc.h:4783 ; Igc/buildingeffectigc.h:348 |
| 3 | sim | callbacks | SIM-0172 ReceiveChat@4792 | partial | Igc/igc.h:4792 |
| 3 | sim | callbacks | SIM-0173 SendChat@4801 | partial | Igc/igc.h:4801 ; Igc/shipIGC.cpp:1895; Igc/shipIGC.cpp:1898; Igc/shipIGC.cpp:1901; Igc/shipIGC.cpp:2171; Igc/shipIGC.h:1732 |
| 3 | sim | callbacks | SIM-0174 SendChatf@4812 | partial | Igc/igc.h:4812 ; Igc/buildingeffectigc.h:320; Igc/shipIGC.cpp:2386; Igc/shipIGC.cpp:2401; Igc/shipIGC.cpp:2561; Igc/shipIGC.cpp:2692; Igc/shipIGC.cpp:2779; Igc/shipIGC.h:2408; Igc/shipIGC.h:2449 |
| 3 | sim | callbacks | SIM-0175 GetMoneyRequest@4828 | partial | Igc/igc.h:4828 |
| 3 | sim | callbacks | SIM-0176 PlaySoundEffect@4830 | partial | Igc/igc.h:4830 ; Igc/asteroidigc.h:154; Igc/shieldIGC.cpp:94; Igc/shieldIGC.cpp:185; Igc/shipIGC.cpp:834; Igc/shipIGC.cpp:1004; Igc/shipIGC.cpp:1189; Igc/shipIGC.cpp:1193 |
| 3 | sim | callbacks | SIM-0177 PlaySoundEffect@4831 | partial | Igc/igc.h:4831 ; Igc/asteroidigc.h:154; Igc/shieldIGC.cpp:94; Igc/shieldIGC.cpp:185; Igc/shipIGC.cpp:834; Igc/shipIGC.cpp:1004; Igc/shipIGC.cpp:1189; Igc/shipIGC.cpp:1193 |
| 3 | sim | callbacks | SIM-0178 PlayNotificationSound@4832 | partial | Igc/igc.h:4832 ; Igc/afterburnerIGC.cpp:119; Igc/cloakigc.cpp:75; Igc/dispenserigc.cpp:98; Igc/magazineigc.cpp:96; Igc/shieldIGC.cpp:82; Igc/shieldIGC.cpp:186; Igc/shipIGC.cpp:1608; Igc/shipIGC.h:616; Igc/shipIGC.h:617; Igc/shipIGC.h:646; Igc/shipIGC.h:647; Igc/shipIGC.h:1302; Igc/shipIGC.h:1307; Igc/weaponIGC.h:46 |
| 3 | sim | callbacks | SIM-0180 PlayVisualEffect@4834 | partial | Igc/igc.h:4834 ; Igc/shipIGC.cpp:836; Igc/shipIGC.cpp:1006; Igc/shipIGC.cpp:1191; Igc/shipIGC.cpp:1195 |
| 3 | sim | callbacks | SIM-0182 TerminateModelEvent@4836 | partial | Igc/igc.h:4836 ; Igc/modelIGC.cpp:61 |
| 3 | sim | callbacks | SIM-0183 TerminateMissionEvent@4837 | partial | Igc/igc.h:4837 ; Igc/missionigc.cpp:3082 |
| 3 | sim | callbacks | SIM-0185 KillAsteroidEvent@4839 | missing | Igc/igc.h:4839 ; Igc/asteroidigc.h:162; Igc/clusterIGC.cpp:155 |
| 3 | sim | callbacks | SIM-0186 DrainAsteroidEvent@4840 | partial | Igc/igc.h:4840 |
| 3 | sim | callbacks | SIM-0190 KillBuildingEffectEvent@4844 | partial | Igc/igc.h:4844 ; Igc/shipIGC.cpp:218 |
| 3 | sim | callbacks | SIM-0193 PostPlainText@4847 | partial | Igc/igc.h:4847 |
| 3 | sim | callbacks | SIM-0194 PostText@4848 | partial | Igc/igc.h:4848 |
| 3 | sim | callbacks | SIM-0195 PostNotificationText@4849 | partial | Igc/igc.h:4849 ; Igc/afterburnerIGC.cpp:120; Igc/cloakigc.cpp:76; Igc/dispenserigc.cpp:99; Igc/magazineigc.cpp:97; Igc/shieldIGC.cpp:83; Igc/shipIGC.h:612; Igc/shipIGC.h:618; Igc/shipIGC.h:642; Igc/shipIGC.h:648; Igc/weaponIGC.h:47 |
| 3 | sim | callbacks | SIM-0197 TargetKilled@4851 | partial | Igc/igc.h:4851 |
| 3 | sim | callbacks | SIM-0199 DamageShipEvent@4853 | partial | Igc/igc.h:4853 ; Igc/shipIGC.cpp:398; Igc/shipIGC.cpp:1312 |
| 3 | sim | callbacks | SIM-0205 LandOnCarrierEvent@4859 | partial | Igc/igc.h:4859 ; Igc/shipIGC.cpp:1050; Igc/shipIGC.cpp:1057 |
| 3 | sim | callbacks | SIM-0206 RescueShipEvent@4860 | partial | Igc/igc.h:4860 ; Igc/shipIGC.cpp:865; Igc/shipIGC.cpp:907; Igc/shipIGC.cpp:1035; Igc/shipIGC.cpp:1040 |
| 3 | sim | callbacks | SIM-0207 WarpBombEvent@4861 | partial | Igc/igc.h:4861 ; Igc/missileigc.cpp:466 |
| 3 | sim | callbacks | SIM-0208 HitWarpEvent@4862 | partial | Igc/igc.h:4862 ; Igc/shipIGC.cpp:636 |
| 3 | sim | callbacks | SIM-0209 HitTreasureEvent@4863 | partial | Igc/igc.h:4863 ; Igc/treasureIGC.h:92 |
| 3 | sim | callbacks | SIM-0212 ContinueRipcord@4866 | partial | Igc/igc.h:4866 ; Igc/shipIGC.cpp:280 |
| 3 | sim | callbacks | SIM-0213 UseRipcord@4867 | partial | Igc/igc.h:4867 ; Igc/shipIGC.cpp:286 |
| 3 | sim | callbacks | SIM-0214 FireMissile@4868 | partial | Igc/igc.h:4868 ; Igc/magazineigc.cpp:118 |
| 3 | sim | callbacks | SIM-0215 FireExpendable@4870 | partial | Igc/igc.h:4870 ; Igc/dispenserigc.cpp:121 |
| 3 | sim | callbacks | SIM-0219 ChangeStation@4876 | partial | Igc/igc.h:4876 ; Igc/shipIGC.h:483 |
| 3 | sim | callbacks | SIM-0220 ChangeCluster@4879 | partial | Igc/igc.h:4879 ; Igc/shipIGC.h:254 |
| 3 | sim | callbacks | SIM-0221 CommandChangedEvent@4882 | partial | Igc/igc.h:4882 ; Igc/shipIGC.h:788 |
| 3 | sim | callbacks | SIM-0224 ActivateTeleportProbe@4888 | partial | Igc/igc.h:4888 ; Igc/probeigc.cpp:153; Igc/probeigc.cpp:347 |
| 3 | sim | callbacks | SIM-0225 DestroyTeleportProbe@4890 | partial | Igc/igc.h:4890 ; Igc/probeigc.cpp:230 |
| 3 | sim | callbacks | SIM-0228 ChangeGameState@4899 | partial | Igc/igc.h:4899 |
| 3 | sim | callbacks | SIM-0229 CreateBuildingEffect@4901 | partial | Igc/igc.h:4901 ; Igc/shipIGC.cpp:884 |
| 3 | sim | callbacks | SIM-0231 GetCluster@4909 | partial | Igc/igc.h:4909 ; Igc/asteroidigc.cpp:29; Igc/buildingeffectigc.h:84; Igc/buoyigc.cpp:60; Igc/clusterIGC.cpp:312; Igc/clusterIGC.cpp:313; Igc/clusterIGC.cpp:416; Igc/clusterIGC.cpp:444; Igc/clusterIGC.h:418; Igc/clusterIGC.h:436; Igc/common.cpp:481; Igc/common.cpp:687; Igc/common.cpp:726; Igc/common.cpp:772; Igc/common.cpp:775; Igc/common.cpp:785; Igc/common.cpp:788; Igc/common.cpp:871; Igc/common.cpp:901; Igc/common.cpp:923; Igc/common.cpp:972; Igc/common.cpp:989; Igc/common.cpp:1021; Igc/common.cpp:1034; Igc/common.cpp:1699; Igc/common.cpp:1841; Igc/common.cpp:1854; Igc/common.cpp:1873; Igc/common.cpp:1925; Igc/common.cpp:2244; Igc/common.cpp:2332; Igc/common.cpp:2333; Igc/common.cpp:2753; Igc/common.cpp:2782; Igc/igc.h:4912; Igc/igc.h:4919; Igc/igc.h:5333; Igc/igc.h:5334; Igc/magazineigc.cpp:127; Igc/mapmakerigc.cpp:239; Igc/mapmakerigc.cpp:263; Igc/mapmakerigc.cpp:264; Igc/mapmakerigc.cpp:279; Igc/mapmakerigc.cpp:335; Igc/mapmakerigc.cpp:816; Igc/mapmakerigc.cpp:822; Igc/mapmakerigc.cpp:828; Igc/mapmakerigc.cpp:834; Igc/mapmakerigc.cpp:989; Igc/mapmakerigc.cpp:1039; Igc/mineigc.cpp:62; Igc/missileigc.cpp:108; Igc/missileigc.cpp:371; Igc/missionigc.cpp:3661; Igc/modelIGC.h:589; Igc/probeigc.cpp:66; Igc/probeigc.cpp:392; Igc/probeigc.h:169; Igc/projectileIGC.cpp:193; Igc/shipIGC.cpp:1765; Igc/shipIGC.cpp:1922; Igc/shipIGC.cpp:1950; Igc/shipIGC.cpp:2036; Igc/shipIGC.cpp:2354; Igc/shipIGC.cpp:2376; Igc/shipIGC.cpp:2714; Igc/shipIGC.cpp:2720; Igc/shipIGC.cpp:2727; Igc/shipIGC.cpp:3785; Igc/shipIGC.cpp:3949; Igc/shipIGC.cpp:3970; Igc/shipIGC.cpp:4101; Igc/shipIGC.cpp:4156; Igc/shipIGC.cpp:4347; Igc/shipIGC.cpp:4378; Igc/shipIGC.h:349; Igc/shipIGC.h:1614; Igc/shipIGC.h:1644; Igc/shipIGC.h:1917; Igc/shipIGC.h:1929; Igc/shipIGC.h:2207; Igc/shipIGC.h:2252; Igc/shipIGC.h:2284; Igc/shipIGC.h:2307; Igc/shipIGC.h:2355; Igc/shipIGC.h:2356; Igc/sideigc.cpp:342; Igc/sideigc.cpp:367; Igc/sideigc.cpp:371; Igc/sideigc.cpp:373; Igc/sideigc.cpp:375; Igc/sideigc.cpp:438; Igc/stationIGC.cpp:45; Igc/stationIGC.cpp:68; Igc/stationIGC.h:314; Igc/treasureIGC.cpp:201; Igc/warpIGC.cpp:29; Igc/warpIGC.h:80; Igc/warpIGC.h:155; Igc/weaponIGC.cpp:177; Igc/weaponIGC.cpp:235 |
| 3 | sim | callbacks | SIM-0232 GetRipcordCluster@4915 | partial | Igc/igc.h:4915 ; Igc/common.cpp:811; Igc/common.cpp:824; Igc/shipIGC.cpp:3741; Igc/shipIGC.cpp:3760; Igc/shipIGC.cpp:4262; Igc/shipIGC.cpp:4281 |
| 3 | sim | callbacks | SIM-0233 Reload@4923 | partial | Igc/igc.h:4923 ; Igc/shipIGC.h:610; Igc/shipIGC.h:640 |
| 3 | sim | periodic | SIM-0138 ballot_resolution_boundary | partial | FedSrv/fsmission.cpp:5817 |
| 3 | sim | periodic | SIM-0141 server_sim_broadcast_cadence | partial | FedSrv/FedSrv.CPP:4238 |
| 3 | sim | periodic | SIM-0144 station_state_docked_broadcast | partial | FedSrv/FedSrv.CPP:8253 |
| 3 | sim | periodic | SIM-0150 warp_reentry_timer | partial | FedSrv/fsship.cpp:1185; FedSrv/fsship.cpp:1190 |
| 3 | sim | periodic | SIM-0470 ship_status_delta_and_turret_updates | partial | FedSrv/FedSrv.CPP:3693 |
| 3 | sim | periodic | SIM-0471 target_hull_shield_fraction_updates | partial | FedSrv/FedSrv.CPP:4079 |
| 3 | sim | sounds | SIM-0235 PlaySoundEffect@Igc/asteroidigc.h:154 rockHitSound | partial | Igc/asteroidigc.h:154 |
| 3 | sim | sounds | SIM-0236 SendChatf@Igc/buildingeffectigc.h:320 GetCompletionSound | partial | Igc/buildingeffectigc.h:320 |
| 3 | sim | sounds | SIM-0244 PlaySoundEffect@Igc/shipIGC.cpp:834 collisionSound | partial | Igc/shipIGC.cpp:834 |
| 3 | sim | sounds | SIM-0245 PlaySoundEffect@Igc/shipIGC.cpp:1004 collisionSound | partial | Igc/shipIGC.cpp:1004 |
| 3 | sim | sounds | SIM-0246 PlaySoundEffect@Igc/shipIGC.cpp:1189 collisionSound | partial | Igc/shipIGC.cpp:1189 |
| 3 | sim | sounds | SIM-0247 PlaySoundEffect@Igc/shipIGC.cpp:1193 collisionSound | partial | Igc/shipIGC.cpp:1193 |
| 3 | sim | sounds | SIM-0249 SendChat@Igc/shipIGC.cpp:1895 constructorRunningSound | partial | Igc/shipIGC.cpp:1895 |
| 3 | sim | sounds | SIM-0250 SendChat@Igc/shipIGC.cpp:1898 droneTooMuchDamageSound | partial | Igc/shipIGC.cpp:1898 |
| 3 | sim | sounds | SIM-0251 SendChat@Igc/shipIGC.cpp:1901 droneEnemyOnScopeSound | missing | Igc/shipIGC.cpp:1901 |
| 3 | sim | sounds | SIM-0252 SendChat@Igc/shipIGC.cpp:2171 droneComingHomeEmptySound | partial | Igc/shipIGC.cpp:2171 |
| 3 | sim | sounds | SIM-0258 SendChatf@Igc/shipIGC.cpp:2386 droneInTransitSound | missing | Igc/shipIGC.cpp:2386 |
| 3 | sim | sounds | SIM-0259 SendChatf@Igc/shipIGC.cpp:2401 droneInTransitSound | missing | Igc/shipIGC.cpp:2401 |
| 3 | sim | sounds | SIM-0260 SendChatf@Igc/shipIGC.cpp:2561 droneInTransitSound | missing | Igc/shipIGC.cpp:2561 |
| 3 | sim | sounds | SIM-0261 SendChatf@Igc/shipIGC.cpp:2692 droneInTransitSound | missing | Igc/shipIGC.cpp:2692 |
| 3 | sim | sounds | SIM-0262 SendChatf@Igc/shipIGC.cpp:2779 droneInTransitSound | missing | Igc/shipIGC.cpp:2779 |
| 3 | sim | sounds | SIM-0269 SendChatf@Igc/shipIGC.h:2408 droneInTransitSound | missing | Igc/shipIGC.h:2408 |
| 3 | sim | sounds | SIM-0270 SendChatf@Igc/shipIGC.h:2449 droneInTransitSound | missing | Igc/shipIGC.h:2449 |
| 3 | sim | sounds | SIM-0276 PlaySoundEffect@WinTrek/WinTrek.cpp:8315 salAutopilotDisengageSound | partial | WinTrek/WinTrek.cpp:8315 |
| 3 | sim | sounds | SIM-0292 PlaySoundEffect@WinTrek/WinTrek.cpp:10365 salAutopilotDisengageSound | partial | WinTrek/WinTrek.cpp:10365 |
| 3 | sim | sounds | SIM-0293 PlaySoundEffect@WinTrek/WinTrek.cpp:10372 salAutopilotEngageSound | partial | WinTrek/WinTrek.cpp:10372 |
| 3 | sim | sounds | SIM-0294 SendChat@WinTrek/WinTrek.cpp:10596 voNegativeSound | partial | WinTrek/WinTrek.cpp:10596 |
| 3 | sim | sounds | SIM-0295 SendChat@WinTrek/WinTrek.cpp:10615 voNegativeSound | partial | WinTrek/WinTrek.cpp:10615 |
| 3 | sim | sounds | SIM-0297 PlaySoundEffect@WinTrek/WinTrek.cpp:10692 salAutopilotEngageSound | partial | WinTrek/WinTrek.cpp:10692 |
| 3 | sim | sounds | SIM-0298 SendChat@WinTrek/WinTrek.cpp:10705 voAffirmativeSound | partial | WinTrek/WinTrek.cpp:10705 |
| 3 | sim | sounds | SIM-0299 PlaySoundEffect@WinTrek/WinTrek.cpp:11130 countdownStartingSound | partial | WinTrek/WinTrek.cpp:11130 |
| 3 | sim | sounds | SIM-0304 PlaySoundEffect@WinTrek/WinTrek.cpp:11173 countdown10Sound | partial | WinTrek/WinTrek.cpp:11173 |
| 3 | sim | sounds | SIM-0305 PlaySoundEffect@WinTrek/WinTrek.cpp:11177 countdown9Sound | partial | WinTrek/WinTrek.cpp:11177 |
| 3 | sim | sounds | SIM-0306 PlaySoundEffect@WinTrek/WinTrek.cpp:11181 countdown8Sound | partial | WinTrek/WinTrek.cpp:11181 |
| 3 | sim | sounds | SIM-0307 PlaySoundEffect@WinTrek/WinTrek.cpp:11185 countdown7Sound | partial | WinTrek/WinTrek.cpp:11185 |
| 3 | sim | sounds | SIM-0308 PlaySoundEffect@WinTrek/WinTrek.cpp:11189 countdown6Sound | partial | WinTrek/WinTrek.cpp:11189 |
| 3 | sim | sounds | SIM-0309 PlaySoundEffect@WinTrek/WinTrek.cpp:11193 countdown5Sound | partial | WinTrek/WinTrek.cpp:11193 |
| 3 | sim | sounds | SIM-0310 PlaySoundEffect@WinTrek/WinTrek.cpp:11197 countdown4Sound | partial | WinTrek/WinTrek.cpp:11197 |
| 3 | sim | sounds | SIM-0311 PlaySoundEffect@WinTrek/WinTrek.cpp:11201 countdown3Sound | partial | WinTrek/WinTrek.cpp:11201 |
| 3 | sim | sounds | SIM-0312 PlaySoundEffect@WinTrek/WinTrek.cpp:11205 countdown2Sound | partial | WinTrek/WinTrek.cpp:11205 |
| 3 | sim | sounds | SIM-0313 PlaySoundEffect@WinTrek/WinTrek.cpp:11209 countdown1Sound | partial | WinTrek/WinTrek.cpp:11209 |
| 3 | sim | sounds | SIM-0347 PlaySoundIf@WinTrek/trekigc.cpp:984 GetActivateSound | partial | WinTrek/trekigc.cpp:984 |
| 3 | sim | sounds | SIM-0349 PlaySoundIf@WinTrek/trekigc.cpp:1001 m_vpBurstSound[mount] | partial | WinTrek/trekigc.cpp:1001 |
| 3 | sim | sounds | SIM-0368 PlaySoundIf@WinTrek/trekigc.cpp:1287 ripcordOnLoopSound | missing | WinTrek/trekigc.cpp:1287 |
| 3 | sim | sounds | SIM-0370 PlaySoundIf@WinTrek/trekigc.cpp:1293 miningSound | missing | WinTrek/trekigc.cpp:1293 |
| 3 | sim | sounds | SIM-0372 PlaySoundIf@WinTrek/trekigc.cpp:1312 GetInteriorSound | partial | WinTrek/trekigc.cpp:1312 |
| 3 | sim | sounds | SIM-0373 PlaySoundIf@WinTrek/trekigc.cpp:1314 GetMainThrusterInteriorSound | partial | WinTrek/trekigc.cpp:1314 |
| 3 | sim | sounds | SIM-0374 PlaySoundIf@WinTrek/trekigc.cpp:1317 GetManuveringThrusterInteriorSound | partial | WinTrek/trekigc.cpp:1317 |
| 3 | sim | sounds | SIM-0375 PlaySoundIf@WinTrek/trekigc.cpp:1320 m_pAfterburnerInteriorSound | partial | WinTrek/trekigc.cpp:1320 |
| 3 | sim | sounds | SIM-0378 PlaySoundIf@WinTrek/trekigc.cpp:1337 GetExteriorSound | partial | WinTrek/trekigc.cpp:1337 |
| 3 | sim | sounds | SIM-0379 PlaySoundIf@WinTrek/trekigc.cpp:1338 GetMainThrusterExteriorSound | partial | WinTrek/trekigc.cpp:1338 |
| 3 | sim | sounds | SIM-0380 PlaySoundIf@WinTrek/trekigc.cpp:1341 GetManuveringThrusterExteriorSound | partial | WinTrek/trekigc.cpp:1341 |
| 3 | sim | sounds | SIM-0381 PlaySoundIf@WinTrek/trekigc.cpp:1344 m_pAfterburnerExteriorSound | partial | WinTrek/trekigc.cpp:1344 |
| 3 | sim | sounds | SIM-0384 PlaySoundEffect@WinTrek/trekigc.cpp:2089 newShipSound | partial | WinTrek/trekigc.cpp:2089 |
| 3 | sim | sounds | SIM-0385 PlaySoundEffect@WinTrek/trekigc.cpp:2093 newEnemySound | partial | WinTrek/trekigc.cpp:2093 |
| 3 | sim | sounds | SIM-0386 PlaySoundEffect@WinTrek/trekigc.cpp:2114 newShipSound | partial | WinTrek/trekigc.cpp:2114 |
| 3 | sim | sounds | SIM-0390 PlaySoundEffect@WinTrek/trekigc.cpp:3009 newCriticalMsgSound | partial | WinTrek/trekigc.cpp:3009 |
| 3 | sim | sounds | SIM-0391 PlaySoundEffect@WinTrek/trekigc.cpp:3014 newNonCriticalMsgSound | partial | WinTrek/trekigc.cpp:3014 |
| 3 | sim | sounds | SIM-0392 PlaySoundEffect@WinTrek/trekigc.cpp:3075 newCriticalMsgSound | partial | WinTrek/trekigc.cpp:3075 |
| 3 | sim | sounds | SIM-0393 PlaySoundEffect@WinTrek/trekigc.cpp:3080 newNonCriticalMsgSound | partial | WinTrek/trekigc.cpp:3080 |
| 3 | sim | sounds | SIM-0394 PlaySoundEffect@WinTrek/trekigc.cpp:3998 jumpSound | partial | WinTrek/trekigc.cpp:3998 |
| 3 | sim | sounds | SIM-0398 PlaySoundEffect@WinTrek/trekigc.cpp:4415 jumpSound | partial | WinTrek/trekigc.cpp:4415 |
| 3 | sim | sounds | SIM-0406 PlaySoundEffect@WinTrek/trekigc.cpp:4882 salAutopilotEngageSound | partial | WinTrek/trekigc.cpp:4882 |
| 3 | sim | sounds | SIM-0414 PlayNotificationSound@clintlib/appmsg.cpp:237 salAutopilotEngageSound | partial | clintlib/appmsg.cpp:237 |
| 3 | sim | sounds | SIM-0420 PlaySoundEffect@clintlib/appmsg.cpp:339 jumpSound | partial | clintlib/appmsg.cpp:339 |
| 3 | sim | sounds | SIM-0427 PlaySoundEffect@clintlib/appmsg.cpp:1057 jumpSound | partial | clintlib/appmsg.cpp:1057 |
| 3 | sim | sounds | SIM-0439 PlaySoundEffect@clintlib/appmsg.cpp:3326 GetCompletionSound | partial | clintlib/appmsg.cpp:3326 |
| 3 | sim | sounds | SIM-0448 PlaySoundEffect@clintlib/clintlib.cpp:3257 deployProbeSound | partial | clintlib/clintlib.cpp:3257 |
| 3 | sim | sounds | SIM-0454 ReceiveChat@clintlib/clintlib.cpp:3934 salQuitSound | missing | clintlib/clintlib.cpp:3934 |
| 3 | sim | sounds | SIM-0455 ReceiveChat@clintlib/clintlib.cpp:3939 salQuitSound | missing | clintlib/clintlib.cpp:3939 |
| 3 | sim | sounds | SIM-0456 ReceiveChat@clintlib/clintlib.cpp:3944 salQuitSound | missing | clintlib/clintlib.cpp:3944 |
| 3 | sim | sounds | SIM-0457 ReceiveChat@clintlib/clintlib.cpp:3952 salQuitSound | missing | clintlib/clintlib.cpp:3952 |
| 3 | sim | sounds | SIM-0458 ReceiveChat@clintlib/clintlib.cpp:3960 salQuitSound | missing | clintlib/clintlib.cpp:3960 |
| 3 | sim | sounds | SIM-0459 ReceiveChat@clintlib/clintlib.cpp:3965 salQuitSound | missing | clintlib/clintlib.cpp:3965 |
| 3 | sim | sounds | SIM-0460 ReceiveChat@clintlib/clintlib.cpp:3970 salQuitSound | missing | clintlib/clintlib.cpp:3970 |
| 3 | sim | sounds | SIM-0461 ReceiveChat@clintlib/clintlib.cpp:3975 salChangeSidesSound | missing | clintlib/clintlib.cpp:3975 |
| 3 | sim | sounds | SIM-0462 ReceiveChat@clintlib/clintlib.cpp:3991 salQuitSound | missing | clintlib/clintlib.cpp:3991 |
| 3 | sim | sounds | SIM-0463 ReceiveChat@clintlib/clintlib.cpp:3999 salMIASound | missing | clintlib/clintlib.cpp:3999 |
| 3 | sim | sounds | SIM-0464 ReceiveChat@clintlib/clintlib.cpp:4155 salRecruitsArrivedSound | missing | clintlib/clintlib.cpp:4155 |
| 3 | sim | sounds | SIM-0465 ReceiveChat@clintlib/clintlib.cpp:4162 salEnemyJoinersSound | missing | clintlib/clintlib.cpp:4162 |
| 3 | sim | sounds | SIM-0468 dynamic_leave_voice@4006 | missing | clintlib/clintlib.cpp:4006; clintlib/clintlib.cpp:4009 |
| 3 | sim | updates | SIM-0047 bucket_percent_notification | partial | Igc/bucketigc.h:65 |
| 3 | sim | updates | SIM-0058 cloak_mount_readiness | partial | Igc/cloakigc.cpp:68 |
| 3 | sim | updates | SIM-0067 explosion_mine_strength | partial | Igc/clusterIGC.cpp:443 |
| 3 | sim | updates | SIM-0070 damage_credit_slot_decay | partial | Igc/igc.h:5081 |
| 3 | sim | updates | SIM-0085 probe_projectile_emission | partial | Igc/probeigc.cpp:479 |
| 3 | sim | updates | SIM-0086 projectile_burnout_blast | partial | Igc/projectileIGC.cpp:120 |
| 3 | sim | updates | SIM-0089 shield_five_percent_activation_sound | missing | Igc/shieldIGC.cpp:93 |
| 3 | sim | updates | SIM-0100 npc_ammo_goal_recovery | partial | Igc/shipIGC.cpp:1715 |
| 3 | sim | updates | SIM-0104 npc_ripcord_retry | partial | Igc/shipIGC.cpp:2067 |
| 3 | sim | updates | SIM-0117 wingman_cross_sector_pursuit | partial | Igc/shipIGC.cpp:2707 |
| 3 | sim | updates | SIM-0118 wingman_goto_opportunism | partial | Igc/shipIGC.cpp:2747 |
| 3 | sim | updates | SIM-0154 afterburner_mount_fraction_persistence | partial | Igc/afterburnerIGC.cpp:111; Igc/afterburnerIGC.cpp:117 |
| 2 | main | commands | TK_CommandFinishTM | missing | Igc/igc.h:433 |
| 2 | main | commands | TK_LODDown | missing | WinTrek/WinTrek.cpp:10421 |
| 2 | main | commands | TK_LODUp | missing | WinTrek/WinTrek.cpp:10415 |
| 2 | main | commands | TK_PauseTM | missing | Igc/igc.h:375 |
| 2 | main | commands | TK_RollModifier | missing | Igc/igc.h:434 |
| 2 | main | commands | TK_SkipTMGoal | missing | Igc/igc.h:424 |
| 2 | main | commands | TK_SwapAfterburner | missing | WinTrek/WinTrek.cpp:9628 |
| 2 | main | commands | TK_SwapChaff | missing | WinTrek/WinTrek.cpp:9610 |
| 2 | main | commands | TK_SwapCloak | missing | WinTrek/WinTrek.cpp:9622 |
| 2 | main | commands | TK_SwapMine | missing | WinTrek/WinTrek.cpp:9634 |
| 2 | main | commands | TK_SwapMissile | missing | WinTrek/WinTrek.cpp:9604 |
| 2 | main | commands | TK_SwapShield | missing | WinTrek/WinTrek.cpp:9616 |
| 2 | main | commands | TK_SwapTurret1 | missing | WinTrek/WinTrek.cpp:9559 |
| 2 | main | commands | TK_SwapTurret2 | missing | WinTrek/WinTrek.cpp:9565 |
| 2 | main | commands | TK_SwapTurret3 | missing | WinTrek/WinTrek.cpp:9571 |
| 2 | main | commands | TK_SwapTurret4 | missing | WinTrek/WinTrek.cpp:9577 |
| 2 | main | commands | TK_SwapWeapon1 | missing | WinTrek/WinTrek.cpp:9535 |
| 2 | main | commands | TK_SwapWeapon2 | missing | WinTrek/WinTrek.cpp:9541 |
| 2 | main | commands | TK_SwapWeapon3 | missing | WinTrek/WinTrek.cpp:9547 |
| 2 | main | commands | TK_SwapWeapon4 | missing | WinTrek/WinTrek.cpp:9553 |
| 2 | main | commands | TK_ToggleLODSlider | missing | WinTrek/WinTrek.cpp:9448 |
| 2 | main | screens | Intro screen | partial | WinTrek/introscreen.cpp |
| 2 | main | screens | Splash / loading screen | partial | WinTrek/splashscreen.cpp |
| 2 | main | screens | Video screen (cinematic) | missing | WinTrek/videoscreen.cpp |
| 2 | sim | constants | SIM-0014 c_fcidRipcordTime | partial | Igc/igc.h:73; FedSrv/FedSrv.CPP:1676 |
| 2 | sim | periodic | SIM-0145 chat_budget_recovery | partial | FedSrv/FedSrv.CPP:8282; FedSrv/fsship.h:449-468 |
| 2 | sim | periodic | SIM-0147 network_roll_call_and_disconnect | partial | FedSrv/FedSrv.CPP:7942 |
| 2 | sim | sounds | SIM-0234 PlayNotificationSound@Igc/afterburnerIGC.cpp:119 mountedSound | missing | Igc/afterburnerIGC.cpp:119 |
| 2 | sim | sounds | SIM-0237 PlayNotificationSound@Igc/cloakigc.cpp:75 mountedSound | missing | Igc/cloakigc.cpp:75 |
| 2 | sim | sounds | SIM-0238 PlayNotificationSound@Igc/dispenserigc.cpp:98 mountedSound | missing | Igc/dispenserigc.cpp:98 |
| 2 | sim | sounds | SIM-0239 PlayNotificationSound@Igc/magazineigc.cpp:96 mountedSound | missing | Igc/magazineigc.cpp:96 |
| 2 | sim | sounds | SIM-0240 PlayNotificationSound@Igc/shieldIGC.cpp:82 mountedSound | missing | Igc/shieldIGC.cpp:82 |
| 2 | sim | sounds | SIM-0241 PlaySoundEffect@Igc/shieldIGC.cpp:94 activateSound | missing | Igc/shieldIGC.cpp:94 |
| 2 | sim | sounds | SIM-0242 PlaySoundEffect@Igc/shieldIGC.cpp:185 deactivateSound | missing | Igc/shieldIGC.cpp:185 |
| 2 | sim | sounds | SIM-0243 PlayNotificationSound@Igc/shieldIGC.cpp:186 salShieldsOfflineSound | missing | Igc/shieldIGC.cpp:186 |
| 2 | sim | sounds | SIM-0248 PlayNotificationSound@Igc/shipIGC.cpp:1608 salCloakEngageSound | missing | Igc/shipIGC.cpp:1608 |
| 2 | sim | sounds | SIM-0263 PlayNotificationSound@Igc/shipIGC.h:616 salReloadingAmmoSound | missing | Igc/shipIGC.h:616 |
| 2 | sim | sounds | SIM-0264 PlayNotificationSound@Igc/shipIGC.h:617 startReloadSound | missing | Igc/shipIGC.h:617 |
| 2 | sim | sounds | SIM-0265 PlayNotificationSound@Igc/shipIGC.h:646 salReloadingFuelSound | missing | Igc/shipIGC.h:646 |
| 2 | sim | sounds | SIM-0266 PlayNotificationSound@Igc/shipIGC.h:647 startReloadSound | missing | Igc/shipIGC.h:647 |
| 2 | sim | sounds | SIM-0267 PlayNotificationSound@Igc/shipIGC.h:1302 salCargoFullSound | missing | Igc/shipIGC.h:1302 |
| 2 | sim | sounds | SIM-0268 PlayNotificationSound@Igc/shipIGC.h:1307 pickUpPartSound | missing | Igc/shipIGC.h:1307 |
| 2 | sim | sounds | SIM-0272 StartSound@WinTrek/Slideshow.cpp:105 soundID | missing | WinTrek/Slideshow.cpp:105 |
| 2 | sim | sounds | SIM-0273 StartSound@WinTrek/WinTrek.cpp:1108 GetSound | missing | WinTrek/WinTrek.cpp:1108 |
| 2 | sim | sounds | SIM-0274 PlaySoundEffect@WinTrek/WinTrek.cpp:1110 id | missing | WinTrek/WinTrek.cpp:1110 |
| 2 | sim | sounds | SIM-0275 StartSound@WinTrek/WinTrek.cpp:6567 personalJumpSound | missing | WinTrek/WinTrek.cpp:6567 |
| 2 | sim | sounds | SIM-0279 StartSound@WinTrek/WinTrek.cpp:8827 newTargetSound | missing | WinTrek/WinTrek.cpp:8827 |
| 2 | sim | sounds | SIM-0280 PlaySoundEffect@WinTrek/WinTrek.cpp:9047 ungroupWeaponsSound | missing | WinTrek/WinTrek.cpp:9047 |
| 2 | sim | sounds | SIM-0281 PlaySoundEffect@WinTrek/WinTrek.cpp:9053 groupWeaponsSound | missing | WinTrek/WinTrek.cpp:9053 |
| 2 | sim | sounds | SIM-0282 PlaySoundEffect@WinTrek/WinTrek.cpp:9098 mountSound | missing | WinTrek/WinTrek.cpp:9098 |
| 2 | sim | sounds | SIM-0283 PlaySoundEffect@WinTrek/WinTrek.cpp:9102 unmountSound | missing | WinTrek/WinTrek.cpp:9102 |
| 2 | sim | sounds | SIM-0284 PlaySoundEffect@WinTrek/WinTrek.cpp:9114 salCargoFullSound | missing | WinTrek/WinTrek.cpp:9114 |
| 2 | sim | sounds | SIM-0285 PlaySoundEffect@WinTrek/WinTrek.cpp:9269 positiveButtonClickSound | missing | WinTrek/WinTrek.cpp:9269 |
| 2 | sim | sounds | SIM-0286 PlaySoundEffect@WinTrek/WinTrek.cpp:9463 vectorLockSound | missing | WinTrek/WinTrek.cpp:9463 |
| 2 | sim | sounds | SIM-0287 PlayNotificationSound@WinTrek/WinTrek.cpp:9484 salShipCantRipcordSound | missing | WinTrek/WinTrek.cpp:9484 |
| 2 | sim | sounds | SIM-0288 PlaySoundEffect@WinTrek/WinTrek.cpp:9529 groupWeaponsSound | missing | WinTrek/WinTrek.cpp:9529 |
| 2 | sim | sounds | SIM-0289 PlaySoundEffect@WinTrek/WinTrek.cpp:9531 ungroupWeaponsSound | missing | WinTrek/WinTrek.cpp:9531 |
| 2 | sim | sounds | SIM-0290 PlayNotificationSound@WinTrek/WinTrek.cpp:9693 salReloadingSound | missing | WinTrek/WinTrek.cpp:9693 |
| 2 | sim | sounds | SIM-0291 PlaySoundEffect@WinTrek/WinTrek.cpp:9719 dropSound | missing | WinTrek/WinTrek.cpp:9719 |
| 2 | sim | sounds | SIM-0296 PlaySoundEffect@WinTrek/WinTrek.cpp:10658 acceptCommandSound | missing | WinTrek/WinTrek.cpp:10658 |
| 2 | sim | sounds | SIM-0300 PlaySoundEffect@WinTrek/WinTrek.cpp:11157 countdown10minSound | missing | WinTrek/WinTrek.cpp:11157 |
| 2 | sim | sounds | SIM-0301 PlaySoundEffect@WinTrek/WinTrek.cpp:11161 countdown5minSound | missing | WinTrek/WinTrek.cpp:11161 |
| 2 | sim | sounds | SIM-0302 PlaySoundEffect@WinTrek/WinTrek.cpp:11165 countdown1minSound | missing | WinTrek/WinTrek.cpp:11165 |
| 2 | sim | sounds | SIM-0303 PlaySoundEffect@WinTrek/WinTrek.cpp:11169 countdown30Sound | missing | WinTrek/WinTrek.cpp:11169 |
| 2 | sim | sounds | SIM-0317 PlaySoundEffect@WinTrek/console.cpp:1268 chatKeySound | missing | WinTrek/console.cpp:1268 |
| 2 | sim | sounds | SIM-0318 PlaySoundEffect@WinTrek/console.cpp:2339 chatKeySound | missing | WinTrek/console.cpp:2339 |
| 2 | sim | sounds | SIM-0319 PlaySoundEffect@WinTrek/console.cpp:2424 chatKeySound | missing | WinTrek/console.cpp:2424 |
| 2 | sim | sounds | SIM-0320 PlaySoundEffect@WinTrek/console.cpp:3274 paneSlideSound | missing | WinTrek/console.cpp:3274 |
| 2 | sim | sounds | SIM-0321 PlaySoundEffect@WinTrek/load.cpp:236 mouseoverSound | missing | WinTrek/load.cpp:236 |
| 2 | sim | sounds | SIM-0322 PlaySoundEffect@WinTrek/load.cpp:1487 mountSound | missing | WinTrek/load.cpp:1487 |
| 2 | sim | sounds | SIM-0323 PlaySoundEffect@WinTrek/load.cpp:1887 mountSound | missing | WinTrek/load.cpp:1887 |
| 2 | sim | sounds | SIM-0324 PlaySoundEffect@WinTrek/load.cpp:1889 unmountSound | missing | WinTrek/load.cpp:1889 |
| 2 | sim | sounds | SIM-0325 PlaySoundEffect@WinTrek/load.cpp:2235 positiveButtonClickSound | missing | WinTrek/load.cpp:2235 |
| 2 | sim | sounds | SIM-0326 PlaySoundEffect@WinTrek/load.cpp:2247 mountSound | missing | WinTrek/load.cpp:2247 |
| 2 | sim | sounds | SIM-0327 PlaySoundEffect@WinTrek/loadout.cpp:1178 mouseoverSound | missing | WinTrek/loadout.cpp:1178 |
| 2 | sim | sounds | SIM-0328 PlaySoundEffect@WinTrek/sectormap.cpp:1362 errorSound | missing | WinTrek/sectormap.cpp:1362 |
| 2 | sim | sounds | SIM-0329 PlaySoundEffect@WinTrek/sectormap.cpp:1367 errorSound | missing | WinTrek/sectormap.cpp:1367 |
| 2 | sim | sounds | SIM-0330 PlaySoundEffect@WinTrek/sectormap.cpp:1442 mouseoverSound | missing | WinTrek/sectormap.cpp:1442 |
| 2 | sim | sounds | SIM-0331 PlaySoundEffect@WinTrek/sectormap.cpp:1523 mouseclickSound | missing | WinTrek/sectormap.cpp:1523 |
| 2 | sim | sounds | SIM-0332 PlaySoundEffect@WinTrek/sectormap.cpp:1586 mouseclickSound | missing | WinTrek/sectormap.cpp:1586 |
| 2 | sim | sounds | SIM-0333 PlaySoundEffect@WinTrek/teampane.cpp:1177 positiveButtonClickSound | missing | WinTrek/teampane.cpp:1177 |
| 2 | sim | sounds | SIM-0334 PlaySoundEffect@WinTrek/teamscreen.cpp:2209 mouseclickSound | missing | WinTrek/teamscreen.cpp:2209 |
| 2 | sim | sounds | SIM-0335 PlaySoundEffect@WinTrek/teamscreen.cpp:2217 mouseclickSound | missing | WinTrek/teamscreen.cpp:2217 |
| 2 | sim | sounds | SIM-0336 PlaySoundEffect@WinTrek/teamscreen.cpp:2438 mouseclickSound | missing | WinTrek/teamscreen.cpp:2438 |
| 2 | sim | sounds | SIM-0337 PlaySoundEffect@WinTrek/teleportpane.cpp:285 noTurretsSound | missing | WinTrek/teleportpane.cpp:285 |
| 2 | sim | sounds | SIM-0338 PlaySoundEffect@WinTrek/teleportpane.cpp:320 personalJumpSound | missing | WinTrek/teleportpane.cpp:320 |
| 2 | sim | sounds | SIM-0345 StartSound@WinTrek/trekigc.cpp:714 m_idAmbientSound | partial | WinTrek/trekigc.cpp:714 |
| 2 | sim | sounds | SIM-0348 PlaySoundIf@WinTrek/trekigc.cpp:990 turnSound | missing | WinTrek/trekigc.cpp:990 |
| 2 | sim | sounds | SIM-0361 StartSound@WinTrek/trekigc.cpp:1264 hullLowSound | missing | WinTrek/trekigc.cpp:1264 |
| 2 | sim | sounds | SIM-0362 StartSound@WinTrek/trekigc.cpp:1266 ammoLowSound | missing | WinTrek/trekigc.cpp:1266 |
| 2 | sim | sounds | SIM-0363 StartSound@WinTrek/trekigc.cpp:1268 fuelLowSound | missing | WinTrek/trekigc.cpp:1268 |
| 2 | sim | sounds | SIM-0364 StartSound@WinTrek/trekigc.cpp:1270 energyLowSound | missing | WinTrek/trekigc.cpp:1270 |
| 2 | sim | sounds | SIM-0365 StartSound@WinTrek/trekigc.cpp:1273 salNoAmmoSound | missing | WinTrek/trekigc.cpp:1273 |
| 2 | sim | sounds | SIM-0366 StartSound@WinTrek/trekigc.cpp:1275 salNoFuelSound | missing | WinTrek/trekigc.cpp:1275 |
| 2 | sim | sounds | SIM-0371 PlaySoundIf@WinTrek/trekigc.cpp:1310 m_pGunnerInteriorSound | missing | WinTrek/trekigc.cpp:1310 |
| 2 | sim | sounds | SIM-0376 PlaySoundIf@WinTrek/trekigc.cpp:1326 SidethrustInteriorSound | missing | WinTrek/trekigc.cpp:1326 |
| 2 | sim | sounds | SIM-0377 PlaySoundIf@WinTrek/trekigc.cpp:1329 CapSidethrustInteriorSound | missing | WinTrek/trekigc.cpp:1329 |
| 2 | sim | sounds | SIM-0382 PlaySoundIf@WinTrek/trekigc.cpp:1351 SidethrustExteriorSound | missing | WinTrek/trekigc.cpp:1351 |
| 2 | sim | sounds | SIM-0383 PlaySoundIf@WinTrek/trekigc.cpp:1354 CapSidethrustExteriorSound | missing | WinTrek/trekigc.cpp:1354 |
| 2 | sim | sounds | SIM-0388 PlaySoundEffect@WinTrek/trekigc.cpp:2859 dockedSound | missing | WinTrek/trekigc.cpp:2859 |
| 2 | sim | sounds | SIM-0389 PlaySoundEffect@WinTrek/trekigc.cpp:2860 salWelcomeHomeSound | missing | WinTrek/trekigc.cpp:2860 |
| 2 | sim | sounds | SIM-0395 PlayNotificationSound@WinTrek/trekigc.cpp:4237 salRepairedAtCarrierSound | missing | WinTrek/trekigc.cpp:4237 |
| 2 | sim | sounds | SIM-0399 PlaySoundEffect@WinTrek/trekigc.cpp:4536 idSonicChat | missing | WinTrek/trekigc.cpp:4536 |
| 2 | sim | sounds | SIM-0400 PlaySoundEffect@WinTrek/trekigc.cpp:4540 newCommandMsgSound | missing | WinTrek/trekigc.cpp:4540 |
| 2 | sim | sounds | SIM-0401 PlaySoundEffect@WinTrek/trekigc.cpp:4546 newChatMsgFromCommanderSound | missing | WinTrek/trekigc.cpp:4546 |
| 2 | sim | sounds | SIM-0402 PlaySoundEffect@WinTrek/trekigc.cpp:4548 newPersonalMsgSound | missing | WinTrek/trekigc.cpp:4548 |
| 2 | sim | sounds | SIM-0403 PlaySoundEffect@WinTrek/trekigc.cpp:4555 newChatMsgFromCommanderSound | missing | WinTrek/trekigc.cpp:4555 |
| 2 | sim | sounds | SIM-0404 PlaySoundEffect@WinTrek/trekigc.cpp:4557 newChatMsgSound | missing | WinTrek/trekigc.cpp:4557 |
| 2 | sim | sounds | SIM-0405 PlaySoundEffect@WinTrek/trekigc.cpp:4560 newOfflineChatMsgSound | missing | WinTrek/trekigc.cpp:4560 |
| 2 | sim | sounds | SIM-0410 StartSound@WinTrek/trekigc.cpp:5061 missileLockSound | missing | WinTrek/trekigc.cpp:5061 |
| 2 | sim | sounds | SIM-0411 StartSound@WinTrek/trekigc.cpp:5085 m_idAmbient | partial | WinTrek/trekigc.cpp:5085 |
| 2 | sim | sounds | SIM-0412 StartSound@WinTrek/trekmdl.cpp:471 m_soundId | missing | WinTrek/trekmdl.cpp:471 |
| 2 | sim | sounds | SIM-0413 PlayNotificationSound@clintlib/appmsg.cpp:163 salRepairedAtCarrierSound | missing | clintlib/appmsg.cpp:163 |
| 2 | sim | sounds | SIM-0415 PlaySoundEffect@clintlib/appmsg.cpp:282 pickUpPowerupSound | missing | clintlib/appmsg.cpp:282 |
| 2 | sim | sounds | SIM-0416 PlaySoundEffect@clintlib/appmsg.cpp:286 pickUpDevelopmentSound | missing | clintlib/appmsg.cpp:286 |
| 2 | sim | sounds | SIM-0417 PlaySoundEffect@clintlib/appmsg.cpp:291 pickUpCashSound | missing | clintlib/appmsg.cpp:291 |
| 2 | sim | sounds | SIM-0418 PlaySoundEffect@clintlib/appmsg.cpp:303 pickUpDevelopmentSound | missing | clintlib/appmsg.cpp:303 |
| 2 | sim | sounds | SIM-0425 PlayNotificationSound@clintlib/appmsg.cpp:970 salNoRipcordSound | missing | clintlib/appmsg.cpp:970 |
| 2 | sim | sounds | SIM-0428 PlaySoundEffect@clintlib/appmsg.cpp:1238 investorSound | missing | clintlib/appmsg.cpp:1238 |
| 2 | sim | sounds | SIM-0429 PlaySoundEffect@clintlib/appmsg.cpp:1313 paydaySound | missing | clintlib/appmsg.cpp:1313 |
| 2 | sim | sounds | SIM-0430 PlaySoundEffect@clintlib/appmsg.cpp:1757 newCommandMsgSound | missing | clintlib/appmsg.cpp:1757 |
| 2 | sim | sounds | SIM-0431 PlaySoundEffect@clintlib/appmsg.cpp:1894 personalJumpSound | missing | clintlib/appmsg.cpp:1894 |
| 2 | sim | sounds | SIM-0432 PlaySoundEffect@clintlib/appmsg.cpp:2071 boardSound | missing | clintlib/appmsg.cpp:2071 |
| 2 | sim | sounds | SIM-0433 PlaySoundEffect@clintlib/appmsg.cpp:2481 salPlayerWaitingSound | missing | clintlib/appmsg.cpp:2481 |
| 2 | sim | sounds | SIM-0434 PlaySoundEffect@clintlib/appmsg.cpp:2896 destroyedSound | missing | clintlib/appmsg.cpp:2896 |
| 2 | sim | sounds | SIM-0435 PlaySoundEffect@clintlib/appmsg.cpp:2965 commanderSound | missing | clintlib/appmsg.cpp:2965 |
| 2 | sim | sounds | SIM-0436 PlaySoundEffect@clintlib/appmsg.cpp:3149 enemyFlagLostSound | missing | clintlib/appmsg.cpp:3149 |
| 2 | sim | sounds | SIM-0437 PlaySoundEffect@clintlib/appmsg.cpp:3156 artifactFoundSound | missing | clintlib/appmsg.cpp:3156 |
| 2 | sim | sounds | SIM-0438 PlaySoundEffect@clintlib/appmsg.cpp:3169 flagLostSound | missing | clintlib/appmsg.cpp:3169 |
| 2 | sim | sounds | SIM-0443 PlayNotificationSound@clintlib/clintlib.cpp:3216 salReloadingMissilesSound | missing | clintlib/clintlib.cpp:3216 |
| 2 | sim | sounds | SIM-0444 PlaySoundEffect@clintlib/clintlib.cpp:3217 startReloadSound | missing | clintlib/clintlib.cpp:3217 |
| 2 | sim | sounds | SIM-0445 PlayNotificationSound@clintlib/clintlib.cpp:3224 salMissilesDepletedSound | missing | clintlib/clintlib.cpp:3224 |
| 2 | sim | sounds | SIM-0446 PlaySoundEffect@clintlib/clintlib.cpp:3249 deployChaffSound | missing | clintlib/clintlib.cpp:3249 |
| 2 | sim | sounds | SIM-0447 PlaySoundEffect@clintlib/clintlib.cpp:3253 deployMineSound | missing | clintlib/clintlib.cpp:3253 |
| 2 | sim | sounds | SIM-0449 PlayNotificationSound@clintlib/clintlib.cpp:3394 salReloadingChaffSound | missing | clintlib/clintlib.cpp:3394 |
| 2 | sim | sounds | SIM-0450 PlayNotificationSound@clintlib/clintlib.cpp:3399 salReloadingDispenserSound | missing | clintlib/clintlib.cpp:3399 |
| 2 | sim | sounds | SIM-0451 PlaySoundEffect@clintlib/clintlib.cpp:3404 startReloadSound | missing | clintlib/clintlib.cpp:3404 |
| 2 | sim | sounds | SIM-0452 PlayNotificationSound@clintlib/clintlib.cpp:3412 salChaffDepletedSound | missing | clintlib/clintlib.cpp:3412 |
| 2 | sim | sounds | SIM-0453 PlayNotificationSound@clintlib/clintlib.cpp:3417 salDispenserEmptySound | missing | clintlib/clintlib.cpp:3417 |
| 2 | sim | sounds | SIM-0467 PlayNotificationSound@clintlib/clintlib.h:1175 salNoRipcordSound | missing | clintlib/clintlib.h:1175 |
| 2 | sim | updates | SIM-0074 mine_expiry_boundary | partial | Igc/mineigc.cpp:150 |
| 2 | sim | updates | SIM-0078 model_spin | partial | Igc/modelIGC.cpp:76 |
| 2 | sim | updates | SIM-0108 miner_drilling_controls | partial | Igc/shipIGC.cpp:2254 |
| 1 | main | commands | TK_DecreaseTMClockSpeed | missing | Igc/igc.h:426 |
| 1 | main | commands | TK_IncreaseTMClockSpeed | missing | Igc/igc.h:425 |
| 1 | main | commands | TK_ResetTMClockSpeed | missing | Igc/igc.h:427 |
| 1 | sim | periodic | SIM-0137 lobby_dirty_rate_limit | partial | FedSrv/fsmission.cpp:5606 |

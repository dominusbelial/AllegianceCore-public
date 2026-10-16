# P691 — parity wave 9: economy simulation callbacks

Entry revision: `40bf3d100e95e7f346cbd07292f7b960faf2c71d`. Work remains in the working tree; no commits or pushes. Every native citation below is relative to `~/Development/Allegiance/src/` and was checked against that local source tree. Only the simulation matrix is inventoried and edited.

## Bundle and scope

Six related rows, total weight **18**: SIM-0047 (bucket progress), SIM-0160/0161 (hull/part stockpile completion), SIM-0163 (bucket changes), SIM-0164 (building tech changes), and SIM-0167 (station type changes). These share the bucket/side/station economy and the concrete `ServerHost` callback boundary. They replace missing producers/default-only host bindings with real state changes and notifications. The stock callbacks also exercise the native repeat-purchase reset through explicitly constructed stock buckets.

The measured entry simulation score is **45.16% weighted / 37.76% unweighted**, rather than treating the brief's historical approximations as the current baseline. Periodic is **25.53%**, callbacks **33.33%**, and updates **68.19%** weighted. This bundle concentrates on callbacks and their update producers; it contains no sound-only work.

`Sector.Update`, SIM-0063, collision scheduling, broadcast pipeline/interest tiers, security, rendering, Android, and installer work are untouched. The two small `Sector` helpers synchronize station records at mutation time and notify after station removal; they do not alter update structure. Progress delivery uses the existing BUCKET_STATUS codec and audience helpers, with no transport/pipeline policy changes.

## Native rules and executable evidence

| Row | Weight | Before → after | Native rule source | Port implementation |
| --- | ---: | --- | --- | --- |
| SIM-0047 | 3 | partial → implemented | Igc/bucketigc.h:44-73,169-172 | src/Allegiance.Core/Model/Bucket.cs:299 |
| SIM-0160 | 3 | partial → implemented | Igc/bucketigc.h:243-249; FedSrv/FedSrv.CPP:11327-11331; Igc/sideigc.h:384-450 | src/Allegiance.Server/ServerHost.cs:303 |
| SIM-0161 | 3 | partial → implemented | Igc/bucketigc.h:251-257; FedSrv/FedSrv.CPP:11334-11340; Igc/launchertypeigc.h:130-142; Igc/igc.h:3879-3885 | src/Allegiance.Server/ServerHost.cs:309 |
| SIM-0163 | 3 | partial → implemented | Igc/bucketigc.cpp:87-101; Igc/bucketigc.h:274-299; clintlib/clintlib.cpp:2859-2862 | src/Allegiance.Core/Model/Bucket.cs:238 |
| SIM-0164 | 3 | partial → implemented | Igc/sideigc.h:142-168,266-287; clintlib/clintlib.cpp:2864-2868 | src/Allegiance.Core/Model/Side.cs:99 |
| SIM-0167 | 3 | partial → implemented | Igc/stationIGC.cpp:65-75,210-276; clintlib/clintlib.cpp:2876-2880 | src/Allegiance.Core/Sim/Station.cs:103 |

**Bucket progress and lifecycle.** `Bucket.Update` computes native integer build-time percent after completing and clamping to the funded share. It notifies only when that integer changes, including completion before the final progress notification. AddMoney emits MoneyChange even when a capped donation spends zero. SetTimeAndMoney replaces both values before MoneyChange; SetEmpty clears state before Emptied; Terminate emits Terminated while its owning side/catalog entry are still available, then removes it. The side's update loop tolerates callback-driven bucket removal without allocating a per-tick catalog snapshot.

The concrete host forwards the typed changes through `BucketChanged`. Server's real subscription forwards progress and empty changes to the client's investment funding state with BUCKET_STATUS. Ordinary buckets use the side audience; the prosperity development uses the mission audience, preserving the native economic-victory visibility rule (`FedSrv/FedSrv.CPP:7183-7196`). This is an explicit port adaptation: native clients simulate bucket time and receive `OnBucketChange`; the managed client already consumes authoritative snapshots. Existing wallet spend/refund messages still originate in the donation transaction. A real TCP test checks both audiences, invested time/money in ClientWorld, empty reset, and the ordinary bucket's negative cross-side receipt assertion.

**Hull/part completion.** The static hull, part, and compact launcher records now implement the existing typed IGC markers. Real `ServerHost.HullTypeCompleted` adds one hull to the side stockpile; PartTypeCompleted adds one ordinary part, or the authored launcher's GetAmount(NULL), including its bridged mountable form. A pack is one stock item, regardless of the pack's ammo/fuel amount. Inventory keys distinguish buyable type and id and are local to a side; partial/exhaustive removal matches `Igc/sideigc.h:384-450`. Explicit hull/part buckets call the typed host callback and reset funding/time for another purchase (`Igc/bucketigc.h:243-257`).

Native's automatic hull/part bucket generation is commented out (`Igc/sideigc.cpp:257-311`); it remains disabled here. The native completion callbacks explicitly leave wire sends NYI (`FedSrv/FedSrv.CPP:11331,11340`), so these rows certify stockpile behavior and the callable completion contract, not a new shop catalog, spawned ship, or stockpile wire protocol.

**Building and station changes.** A mutation-time equality gate compares the actual OR of owned station effect techs with the last notified mask. Placement, last-provider removal, capture by another side, and type replacement notify after state changes. Removing one of several identical providers does not notify. Initial runtime stations now register after the mission and side links exist, individually, so separate tech gains each precede their station notification. The existing computed BuildingTechs/Techs buy gate remains authoritative.

Station.SetBaseStationType changes the resolved type and matching sector record while preserving identity, pose, side, and damage fractions. It recomputes building techs before StationTypeChange, including the native unconditional type notification when setting the same type. New runtime stations also invoke StationTypeChange after registration. Real host overrides forward these notifications through BuildingTechChanged/StationTypeChanged. Native FedSrv leaves these notification-only callbacks at their defaults; its client forwards them to the event source (`clintlib/clintlib.cpp:2864-2880`). This wave certifies the simulation notification contract; it does not add a successor-selection policy or a new tech wire message.

The new source evidence is `tests/Allegiance.Core.Tests/P691EconomyCallbackTests.cs:27,47,70,90,105,122,146,161,187` and `tests/Allegiance.ClientCore.Tests/P691BucketProgressLiveTests.cs:15`.

The regressions use the concrete production host via IIgcSite, rather than claiming RecordingIgcSite's defaults as production implementation. They check side/type inventory isolation, authored launcher counts, repeated stock purchases, exact callback order and state, duplicate-provider suppression, capture loss/gain, initial assembly, and type-replacement preservation. Existing assembly/update tests now distinguish station-construction notifications from callbacks produced by the subsequent tick. Existing donation tests still require exactly one funding-transaction snapshot and validate the additional progress snapshots separately.

## Scorecard before → after

Both scorecards validate **473 simulation rows**. Partial earns zero credit. All weights, native rules, row identities, and populations are preserved; exactly the six selected rows change.

| Simulation area | Weighted before → after | Complete / eligible before → after |
| --- | --- | --- |
| callbacks | 33.33% (66/198) → 40.91% (81/198) | 22/66 → 27/66 |
| constants | 95.56% (129/135) → 95.56% (129/135) | 37/39 → 37/39 |
| periodic | 25.53% (12/47) → 25.53% (12/47) | 3/15 → 3/15 |
| sounds | 24.75% (147/594) → 24.75% (147/594) | 45/228 → 45/228 |
| updates | 68.19% (238/349) → 69.05% (241/349) | 56/87 → 57/87 |
| warnings | 100.00% (10/10) → 100.00% (10/10) | 2/2 → 2/2 |
| TOTAL | 45.16% (602/1333) → 46.51% (620/1333) | 165/437 → 171/437 |

Unweighted total: **37.76% → 39.13%**. Missing/partial rows: **272 → 266**.

## Verification

`NUGET_HTTP_CACHE_PATH=/tmp/p691/nuget-http dotnet build AllegianceCore.slnx --nologo -m:1 -v:q` completed with **0 warnings / 0 errors**. The cache override only allows the NuGet audit cache to write inside the sandbox; package/audit checks remain enabled. Build log: `/tmp/p691/build.log`.

**62 affected methods / 88 data cases passed three individual exact-method invocations each**: 186 invocations, 264 case executions, zero failures/skips. This includes **10 new methods / 12 new cases**. Discovery and individual results, exact commands, counts, failure details, and TRX/log paths are preserved in [P691_RESULTS.json](P691_RESULTS.json). Raw artifacts are under `/tmp/p691/results/`. Source/test fingerprints are unchanged since the final build.

**Local bot/drone continuity.** The enabled 90-second, port-mode, debug-enabled 15v15 MCP battle passed its kills > 0 assertion with all 30 bots reached:

```
RESULT_P632 p691 model=port debug=1 size=15/side window=90s botsReached=30/30 killsA=7 killsB=3 killsTotal=10 deathsA=3 deathsB=7 shots=17870 hits=8560 hitRate=47.9 % friendly=0 shieldHits=5351 hullHits=3209 maxTargeted=30 maxVisibleEnemies=15
```

`MiningSellTests.Loopback_mine_then_dock_sells_ore_and_pays_FM_S_PAYDAY` passed **3/3 alone**, confirming mining, cargo sale, and treasury wire delivery. `Allegiance.ClientCore.Tests.BotAcceptsAndEngagesOrderLiveTests.Mcp_debug_bot_buys_relaunches_and_fights` passed **3/3 alone**, both lifecycle data cases each time: purchase, fight, lethal damage, pod return or pod destruction, repurchase/relaunch, and damage again. Three initial invocations used the wrong Core project and matched zero tests; those are retained in the ledger and excluded from these counts. The unfiltered sequential Core and ClientCore suites completed cleanly: **Core 4,581/4,581**, **ClientCore 1,006/1,006**, both with zero failures/skips. ClientCore enabled `ALLEGIANCE_LIVE_P660_SHOWCASE=1`; `P660ShowcaseLiveTests.Showcase_bots_and_humans_rotate_without_rejoin_and_finale_rearms` passed (21 seconds).

The first MonoGame attempt (with detailed console logging) exited 1 after 25.294 seconds and 2,162 reported pass entries, without a TRX file, final counters, or a named assertion failure. It is **incomplete**, not a successful full-suite result. Its last active fixture was P540EjectLiveProofTests; the exact `Client_applies_Eject_and_picks_nearest_friendly_rescue_target_and_pod_docks` method then passed **3/3 alone**. That does not establish what terminated the assembly run, so no cause or per-test load-flake attribution is claimed. The unfiltered MonoGame repeat with the standard console logger completed cleanly. No product code, assertions, deadlines, or quarantine traits changed in response to the interruption.

| Final complete assembly run | Passed | Failed | Skipped | Wall seconds |
| --- | ---: | ---: | ---: | ---: |
| Allegiance.Core.Tests | 4581 | 0 | 0 | 539.212 |
| Allegiance.ClientCore.Tests | 1006 | 0 | 0 | 426.143 |
| Allegiance.ClientCore.MonoGame.Tests | 2186 | 0 | 3 | 265.975 |

The three MonoGame skips are the existing optional Meshy-pack cases; the private pack is absent. All new/affected tests and every selected live/loopback check executed. No named test assertion failed in the complete assembly runs, so there were no full-suite assertion failures requiring load-flake classification. The interrupted attempt and its three successful candidate-fixture isolations are recorded separately rather than silently discarded.

Final `git diff --check` and the simulation scorecard pass. A field-by-field matrix audit confirms only the six selected rows changed and all native rule text/weights remain identical. All 16 changed/new source and test files retain their final-build SHA-256 fingerprints. The interface matrix and `Sector.Update` are unchanged; HEAD remains at the entry revision. No rig/221/200v200 check was invoked.

## Missing/partial inventory, descending weight

This is the **simulation-only entry inventory**, sorted by descending weight and then row id. Selected rows show their final implementation status. Entries that remain missing/partial form the final inventory of 266 rows. Listed excluded rows are audit context only, not implementation scope. Native citations in this table also use `~/Development/Allegiance/src/`.

| Weight | Row | Area | Entry → final | Native item | Native cite |
| ---: | --- | --- | --- | --- | --- |
| 5 | SIM-0063 | updates | partial | swept_collision_time_order | Igc/clusterIGC.cpp:255 |
| 4 | SIM-0033 | constants | partial | c_fcidWarpBombDelay | Igc/igc.h:98; FedSrv/FedSrv.CPP:12551 |
| 4 | SIM-0044 | updates | partial | ore_seen_by_side | Igc/asteroidigc.h:58 |
| 4 | SIM-0045 | updates | partial | asteroid_build_completion | Igc/asteroidigc.h:90 |
| 4 | SIM-0048 | updates | partial | build_effect_phase_animation | Igc/buildingeffectigc.h:160 |
| 4 | SIM-0059 | updates | partial | docked_npc_launch_rules | Igc/clusterIGC.cpp:116 |
| 4 | SIM-0060 | updates | missing | station_consumed_rock_fog | Igc/clusterIGC.cpp:141 |
| 4 | SIM-0062 | updates | partial | global_preplot_plot_execute_order | Igc/clusterIGC.cpp:170 |
| 4 | SIM-0064 | updates | partial | mine_attrition | Igc/clusterIGC.cpp:325 |
| 4 | SIM-0065 | updates | partial | visibility_every_tenth_pass | Igc/clusterIGC.cpp:348 |
| 4 | SIM-0069 | updates | partial | dispenser_mount_and_fire | Igc/dispenserigc.cpp:84 |
| 4 | SIM-0083 | updates | missing | probe_first_shot_reveal | Igc/probeigc.cpp:448 |
| 4 | SIM-0101 | updates | partial | npc_plan_legality | Igc/shipIGC.cpp:1938 |
| 4 | SIM-0107 | updates | partial | miner_capacity_and_depletion | Igc/shipIGC.cpp:2208 |
| 4 | SIM-0109 | updates | partial | constructor_drill_alignment | Igc/shipIGC.cpp:2272 |
| 4 | SIM-0110 | updates | partial | idle_npc_complaint_backoff | Igc/shipIGC.cpp:2308 |
| 4 | SIM-0112 | updates | partial | wingman_defend_acquire_return | Igc/shipIGC.cpp:2374 |
| 4 | SIM-0113 | updates | missing | wingman_unseen_target_memory | Igc/shipIGC.cpp:2410 |
| 4 | SIM-0114 | updates | partial | wingman_range_bands | Igc/shipIGC.cpp:2483 |
| 4 | SIM-0115 | updates | missing | wingman_missile_evasion_gate | Igc/shipIGC.cpp:2578 |
| 4 | SIM-0119 | updates | partial | goto_completion_and_layer_lay | Igc/shipIGC.cpp:2802 |
| 4 | SIM-0133 | periodic | missing | countdown_game_winner | FedSrv/fsmission.cpp:2335 |
| 4 | SIM-0142 | periodic | partial | auto_start_restart_ready_rules | FedSrv/FedSrv.CPP:8178 |
| 4 | SIM-0143 | periodic | partial | start_countdown_expiry | FedSrv/FedSrv.CPP:8237 |
| 4 | SIM-0253 | sounds | partial | Complain@Igc/shipIGC.cpp:2199 droneTooCrowdedSound | Igc/shipIGC.cpp:2199 |
| 4 | SIM-0254 | sounds | partial | Complain@Igc/shipIGC.cpp:2315 droneWhereToSound | Igc/shipIGC.cpp:2315 |
| 4 | SIM-0255 | sounds | partial | Complain@Igc/shipIGC.cpp:2319 GetConstructorNeedRockSound | Igc/shipIGC.cpp:2319 |
| 4 | SIM-0256 | sounds | partial | Complain@Igc/shipIGC.cpp:2326 droneWhereToLayMinefieldSound | Igc/shipIGC.cpp:2326 |
| 4 | SIM-0257 | sounds | partial | Complain@Igc/shipIGC.cpp:2328 droneWhereToLayTowerSound | Igc/shipIGC.cpp:2328 |
| 4 | SIM-0396 | sounds | partial | PlaySoundEffect@WinTrek/trekigc.cpp:4335 GetDestroyedSound | WinTrek/trekigc.cpp:4335 |
| 4 | SIM-0397 | sounds | partial | PlaySoundEffect@WinTrek/trekigc.cpp:4337 GetEnemyDestroyedSound | WinTrek/trekigc.cpp:4337 |
| 4 | SIM-0442 | sounds | partial | PlaySoundEffect@clintlib/clintlib.cpp:3103 GetLaunchSound | clintlib/clintlib.cpp:3103 |
| 3 | SIM-0047 | updates | partial → implemented | bucket_percent_notification | Igc/bucketigc.h:65 |
| 3 | SIM-0067 | updates | partial | explosion_mine_strength | Igc/clusterIGC.cpp:443 |
| 3 | SIM-0070 | updates | partial | damage_credit_slot_decay | Igc/igc.h:5081 |
| 3 | SIM-0089 | updates | missing | shield_five_percent_activation_sound | Igc/shieldIGC.cpp:93 |
| 3 | SIM-0100 | updates | partial | npc_ammo_goal_recovery | Igc/shipIGC.cpp:1715 |
| 3 | SIM-0104 | updates | partial | npc_ripcord_retry | Igc/shipIGC.cpp:2067 |
| 3 | SIM-0117 | updates | partial | wingman_cross_sector_pursuit | Igc/shipIGC.cpp:2707 |
| 3 | SIM-0118 | updates | partial | wingman_goto_opportunism | Igc/shipIGC.cpp:2747 |
| 3 | SIM-0138 | periodic | partial | ballot_resolution_boundary | FedSrv/fsmission.cpp:5817 |
| 3 | SIM-0141 | periodic | partial | server_sim_broadcast_cadence | FedSrv/FedSrv.CPP:4238 |
| 3 | SIM-0144 | periodic | partial | station_state_docked_broadcast | FedSrv/FedSrv.CPP:8253 |
| 3 | SIM-0150 | periodic | partial | warp_reentry_timer | FedSrv/fsship.cpp:1185; FedSrv/fsship.cpp:1190 |
| 3 | SIM-0160 | callbacks | partial → implemented | HullTypeCompleted@4771 | Igc/igc.h:4771 ; Igc/bucketigc.h:245 |
| 3 | SIM-0161 | callbacks | partial → implemented | PartTypeCompleted@4772 | Igc/igc.h:4772 ; Igc/bucketigc.h:253 |
| 3 | SIM-0163 | callbacks | partial → implemented | BucketChangeEvent@4775 | Igc/igc.h:4775 ; Igc/bucketigc.cpp:98; Igc/bucketigc.h:67; Igc/bucketigc.h:279; Igc/bucketigc.h:290; Igc/bucketigc.h:299 |
| 3 | SIM-0164 | callbacks | partial → implemented | SideBuildingTechChange@4776 | Igc/igc.h:4776 ; Igc/sideigc.h:166 |
| 3 | SIM-0167 | callbacks | partial → implemented | StationTypeChange@4779 | Igc/igc.h:4779 ; Igc/stationIGC.cpp:75; Igc/stationIGC.cpp:276 |
| 3 | SIM-0168 | callbacks | partial | ClusterUpdateEvent@4781 | Igc/igc.h:4781 ; Igc/clusterIGC.cpp:357 |
| 3 | SIM-0169 | callbacks | partial | BuildStation@4783 | Igc/igc.h:4783 ; Igc/buildingeffectigc.h:348 |
| 3 | SIM-0172 | callbacks | partial | ReceiveChat@4792 | Igc/igc.h:4792 |
| 3 | SIM-0173 | callbacks | partial | SendChat@4801 | Igc/igc.h:4801 ; Igc/shipIGC.cpp:1895; Igc/shipIGC.cpp:1898; Igc/shipIGC.cpp:1901; Igc/shipIGC.cpp:2171; Igc/shipIGC.h:1732 |
| 3 | SIM-0174 | callbacks | partial | SendChatf@4812 | Igc/igc.h:4812 ; Igc/buildingeffectigc.h:320; Igc/shipIGC.cpp:2386; Igc/shipIGC.cpp:2401; Igc/shipIGC.cpp:2561; Igc/shipIGC.cpp:2692; Igc/shipIGC.cpp:2779; Igc/shipIGC.h:2408; Igc/shipIGC.h:2449 |
| 3 | SIM-0175 | callbacks | partial | GetMoneyRequest@4828 | Igc/igc.h:4828 |
| 3 | SIM-0176 | callbacks | partial | PlaySoundEffect@4830 | Igc/igc.h:4830 ; Igc/asteroidigc.h:154; Igc/shieldIGC.cpp:94; Igc/shieldIGC.cpp:185; Igc/shipIGC.cpp:834; Igc/shipIGC.cpp:1004; Igc/shipIGC.cpp:1189; Igc/shipIGC.cpp:1193 |
| 3 | SIM-0177 | callbacks | partial | PlaySoundEffect@4831 | Igc/igc.h:4831 ; Igc/asteroidigc.h:154; Igc/shieldIGC.cpp:94; Igc/shieldIGC.cpp:185; Igc/shipIGC.cpp:834; Igc/shipIGC.cpp:1004; Igc/shipIGC.cpp:1189; Igc/shipIGC.cpp:1193 |
| 3 | SIM-0178 | callbacks | partial | PlayNotificationSound@4832 | Igc/igc.h:4832 ; Igc/afterburnerIGC.cpp:119; Igc/cloakigc.cpp:75; Igc/dispenserigc.cpp:98; Igc/magazineigc.cpp:96; Igc/shieldIGC.cpp:82; Igc/shieldIGC.cpp:186; Igc/shipIGC.cpp:1608; Igc/shipIGC.h:616; Igc/shipIGC.h:617; Igc/shipIGC.h:646; Igc/shipIGC.h:647; Igc/shipIGC.h:1302; Igc/shipIGC.h:1307; Igc/weaponIGC.h:46 |
| 3 | SIM-0180 | callbacks | partial | PlayVisualEffect@4834 | Igc/igc.h:4834 ; Igc/shipIGC.cpp:836; Igc/shipIGC.cpp:1006; Igc/shipIGC.cpp:1191; Igc/shipIGC.cpp:1195 |
| 3 | SIM-0182 | callbacks | partial | TerminateModelEvent@4836 | Igc/igc.h:4836 ; Igc/modelIGC.cpp:61 |
| 3 | SIM-0183 | callbacks | partial | TerminateMissionEvent@4837 | Igc/igc.h:4837 ; Igc/missionigc.cpp:3082 |
| 3 | SIM-0185 | callbacks | missing | KillAsteroidEvent@4839 | Igc/igc.h:4839 ; Igc/asteroidigc.h:162; Igc/clusterIGC.cpp:155 |
| 3 | SIM-0186 | callbacks | partial | DrainAsteroidEvent@4840 | Igc/igc.h:4840 |
| 3 | SIM-0190 | callbacks | partial | KillBuildingEffectEvent@4844 | Igc/igc.h:4844 ; Igc/shipIGC.cpp:218 |
| 3 | SIM-0193 | callbacks | partial | PostPlainText@4847 | Igc/igc.h:4847 |
| 3 | SIM-0194 | callbacks | partial | PostText@4848 | Igc/igc.h:4848 |
| 3 | SIM-0195 | callbacks | partial | PostNotificationText@4849 | Igc/igc.h:4849 ; Igc/afterburnerIGC.cpp:120; Igc/cloakigc.cpp:76; Igc/dispenserigc.cpp:99; Igc/magazineigc.cpp:97; Igc/shieldIGC.cpp:83; Igc/shipIGC.h:612; Igc/shipIGC.h:618; Igc/shipIGC.h:642; Igc/shipIGC.h:648; Igc/weaponIGC.h:47 |
| 3 | SIM-0197 | callbacks | partial | TargetKilled@4851 | Igc/igc.h:4851 |
| 3 | SIM-0199 | callbacks | partial | DamageShipEvent@4853 | Igc/igc.h:4853 ; Igc/shipIGC.cpp:398; Igc/shipIGC.cpp:1312 |
| 3 | SIM-0205 | callbacks | partial | LandOnCarrierEvent@4859 | Igc/igc.h:4859 ; Igc/shipIGC.cpp:1050; Igc/shipIGC.cpp:1057 |
| 3 | SIM-0206 | callbacks | partial | RescueShipEvent@4860 | Igc/igc.h:4860 ; Igc/shipIGC.cpp:865; Igc/shipIGC.cpp:907; Igc/shipIGC.cpp:1035; Igc/shipIGC.cpp:1040 |
| 3 | SIM-0207 | callbacks | partial | WarpBombEvent@4861 | Igc/igc.h:4861 ; Igc/missileigc.cpp:466 |
| 3 | SIM-0208 | callbacks | partial | HitWarpEvent@4862 | Igc/igc.h:4862 ; Igc/shipIGC.cpp:636 |
| 3 | SIM-0209 | callbacks | partial | HitTreasureEvent@4863 | Igc/igc.h:4863 ; Igc/treasureIGC.h:92 |
| 3 | SIM-0212 | callbacks | partial | ContinueRipcord@4866 | Igc/igc.h:4866 ; Igc/shipIGC.cpp:280 |
| 3 | SIM-0213 | callbacks | partial | UseRipcord@4867 | Igc/igc.h:4867 ; Igc/shipIGC.cpp:286 |
| 3 | SIM-0214 | callbacks | partial | FireMissile@4868 | Igc/igc.h:4868 ; Igc/magazineigc.cpp:118 |
| 3 | SIM-0215 | callbacks | partial | FireExpendable@4870 | Igc/igc.h:4870 ; Igc/dispenserigc.cpp:121 |
| 3 | SIM-0219 | callbacks | partial | ChangeStation@4876 | Igc/igc.h:4876 ; Igc/shipIGC.h:483 |
| 3 | SIM-0220 | callbacks | partial | ChangeCluster@4879 | Igc/igc.h:4879 ; Igc/shipIGC.h:254 |
| 3 | SIM-0221 | callbacks | partial | CommandChangedEvent@4882 | Igc/igc.h:4882 ; Igc/shipIGC.h:788 |
| 3 | SIM-0224 | callbacks | partial | ActivateTeleportProbe@4888 | Igc/igc.h:4888 ; Igc/probeigc.cpp:153; Igc/probeigc.cpp:347 |
| 3 | SIM-0225 | callbacks | partial | DestroyTeleportProbe@4890 | Igc/igc.h:4890 ; Igc/probeigc.cpp:230 |
| 3 | SIM-0228 | callbacks | partial | ChangeGameState@4899 | Igc/igc.h:4899 |
| 3 | SIM-0229 | callbacks | partial | CreateBuildingEffect@4901 | Igc/igc.h:4901 ; Igc/shipIGC.cpp:884 |
| 3 | SIM-0231 | callbacks | partial | GetCluster@4909 | Igc/igc.h:4909 ; Igc/asteroidigc.cpp:29; Igc/buildingeffectigc.h:84; Igc/buoyigc.cpp:60; Igc/clusterIGC.cpp:312; Igc/clusterIGC.cpp:313; Igc/clusterIGC.cpp:416; Igc/clusterIGC.cpp:444; Igc/clusterIGC.h:418; Igc/clusterIGC.h:436; Igc/common.cpp:481; Igc/common.cpp:687; Igc/common.cpp:726; Igc/common.cpp:772; Igc/common.cpp:775; Igc/common.cpp:785; Igc/common.cpp:788; Igc/common.cpp:871; Igc/common.cpp:901; Igc/common.cpp:923; Igc/common.cpp:972; Igc/common.cpp:989; Igc/common.cpp:1021; Igc/common.cpp:1034; Igc/common.cpp:1699; Igc/common.cpp:1841; Igc/common.cpp:1854; Igc/common.cpp:1873; Igc/common.cpp:1925; Igc/common.cpp:2244; Igc/common.cpp:2332; Igc/common.cpp:2333; Igc/common.cpp:2753; Igc/common.cpp:2782; Igc/igc.h:4912; Igc/igc.h:4919; Igc/igc.h:5333; Igc/igc.h:5334; Igc/magazineigc.cpp:127; Igc/mapmakerigc.cpp:239; Igc/mapmakerigc.cpp:263; Igc/mapmakerigc.cpp:264; Igc/mapmakerigc.cpp:279; Igc/mapmakerigc.cpp:335; Igc/mapmakerigc.cpp:816; Igc/mapmakerigc.cpp:822; Igc/mapmakerigc.cpp:828; Igc/mapmakerigc.cpp:834; Igc/mapmakerigc.cpp:989; Igc/mapmakerigc.cpp:1039; Igc/mineigc.cpp:62; Igc/missileigc.cpp:108; Igc/missileigc.cpp:371; Igc/missionigc.cpp:3661; Igc/modelIGC.h:589; Igc/probeigc.cpp:66; Igc/probeigc.cpp:392; Igc/probeigc.h:169; Igc/projectileIGC.cpp:193; Igc/shipIGC.cpp:1765; Igc/shipIGC.cpp:1922; Igc/shipIGC.cpp:1950; Igc/shipIGC.cpp:2036; Igc/shipIGC.cpp:2354; Igc/shipIGC.cpp:2376; Igc/shipIGC.cpp:2714; Igc/shipIGC.cpp:2720; Igc/shipIGC.cpp:2727; Igc/shipIGC.cpp:3785; Igc/shipIGC.cpp:3949; Igc/shipIGC.cpp:3970; Igc/shipIGC.cpp:4101; Igc/shipIGC.cpp:4156; Igc/shipIGC.cpp:4347; Igc/shipIGC.cpp:4378; Igc/shipIGC.h:349; Igc/shipIGC.h:1614; Igc/shipIGC.h:1644; Igc/shipIGC.h:1917; Igc/shipIGC.h:1929; Igc/shipIGC.h:2207; Igc/shipIGC.h:2252; Igc/shipIGC.h:2284; Igc/shipIGC.h:2307; Igc/shipIGC.h:2355; Igc/shipIGC.h:2356; Igc/sideigc.cpp:342; Igc/sideigc.cpp:367; Igc/sideigc.cpp:371; Igc/sideigc.cpp:373; Igc/sideigc.cpp:375; Igc/sideigc.cpp:438; Igc/stationIGC.cpp:45; Igc/stationIGC.cpp:68; Igc/stationIGC.h:314; Igc/treasureIGC.cpp:201; Igc/warpIGC.cpp:29; Igc/warpIGC.h:80; Igc/warpIGC.h:155; Igc/weaponIGC.cpp:177; Igc/weaponIGC.cpp:235 |
| 3 | SIM-0232 | callbacks | partial | GetRipcordCluster@4915 | Igc/igc.h:4915 ; Igc/common.cpp:811; Igc/common.cpp:824; Igc/shipIGC.cpp:3741; Igc/shipIGC.cpp:3760; Igc/shipIGC.cpp:4262; Igc/shipIGC.cpp:4281 |
| 3 | SIM-0233 | callbacks | partial | Reload@4923 | Igc/igc.h:4923 ; Igc/shipIGC.h:610; Igc/shipIGC.h:640 |
| 3 | SIM-0235 | sounds | partial | PlaySoundEffect@Igc/asteroidigc.h:154 rockHitSound | Igc/asteroidigc.h:154 |
| 3 | SIM-0236 | sounds | partial | SendChatf@Igc/buildingeffectigc.h:320 GetCompletionSound | Igc/buildingeffectigc.h:320 |
| 3 | SIM-0244 | sounds | partial | PlaySoundEffect@Igc/shipIGC.cpp:834 collisionSound | Igc/shipIGC.cpp:834 |
| 3 | SIM-0245 | sounds | partial | PlaySoundEffect@Igc/shipIGC.cpp:1004 collisionSound | Igc/shipIGC.cpp:1004 |
| 3 | SIM-0246 | sounds | partial | PlaySoundEffect@Igc/shipIGC.cpp:1189 collisionSound | Igc/shipIGC.cpp:1189 |
| 3 | SIM-0247 | sounds | partial | PlaySoundEffect@Igc/shipIGC.cpp:1193 collisionSound | Igc/shipIGC.cpp:1193 |
| 3 | SIM-0249 | sounds | partial | SendChat@Igc/shipIGC.cpp:1895 constructorRunningSound | Igc/shipIGC.cpp:1895 |
| 3 | SIM-0250 | sounds | partial | SendChat@Igc/shipIGC.cpp:1898 droneTooMuchDamageSound | Igc/shipIGC.cpp:1898 |
| 3 | SIM-0251 | sounds | missing | SendChat@Igc/shipIGC.cpp:1901 droneEnemyOnScopeSound | Igc/shipIGC.cpp:1901 |
| 3 | SIM-0252 | sounds | partial | SendChat@Igc/shipIGC.cpp:2171 droneComingHomeEmptySound | Igc/shipIGC.cpp:2171 |
| 3 | SIM-0258 | sounds | missing | SendChatf@Igc/shipIGC.cpp:2386 droneInTransitSound | Igc/shipIGC.cpp:2386 |
| 3 | SIM-0259 | sounds | missing | SendChatf@Igc/shipIGC.cpp:2401 droneInTransitSound | Igc/shipIGC.cpp:2401 |
| 3 | SIM-0260 | sounds | missing | SendChatf@Igc/shipIGC.cpp:2561 droneInTransitSound | Igc/shipIGC.cpp:2561 |
| 3 | SIM-0261 | sounds | missing | SendChatf@Igc/shipIGC.cpp:2692 droneInTransitSound | Igc/shipIGC.cpp:2692 |
| 3 | SIM-0262 | sounds | missing | SendChatf@Igc/shipIGC.cpp:2779 droneInTransitSound | Igc/shipIGC.cpp:2779 |
| 3 | SIM-0269 | sounds | missing | SendChatf@Igc/shipIGC.h:2408 droneInTransitSound | Igc/shipIGC.h:2408 |
| 3 | SIM-0270 | sounds | missing | SendChatf@Igc/shipIGC.h:2449 droneInTransitSound | Igc/shipIGC.h:2449 |
| 3 | SIM-0276 | sounds | partial | PlaySoundEffect@WinTrek/WinTrek.cpp:8315 salAutopilotDisengageSound | WinTrek/WinTrek.cpp:8315 |
| 3 | SIM-0292 | sounds | partial | PlaySoundEffect@WinTrek/WinTrek.cpp:10365 salAutopilotDisengageSound | WinTrek/WinTrek.cpp:10365 |
| 3 | SIM-0293 | sounds | partial | PlaySoundEffect@WinTrek/WinTrek.cpp:10372 salAutopilotEngageSound | WinTrek/WinTrek.cpp:10372 |
| 3 | SIM-0294 | sounds | partial | SendChat@WinTrek/WinTrek.cpp:10596 voNegativeSound | WinTrek/WinTrek.cpp:10596 |
| 3 | SIM-0295 | sounds | partial | SendChat@WinTrek/WinTrek.cpp:10615 voNegativeSound | WinTrek/WinTrek.cpp:10615 |
| 3 | SIM-0297 | sounds | partial | PlaySoundEffect@WinTrek/WinTrek.cpp:10692 salAutopilotEngageSound | WinTrek/WinTrek.cpp:10692 |
| 3 | SIM-0298 | sounds | partial | SendChat@WinTrek/WinTrek.cpp:10705 voAffirmativeSound | WinTrek/WinTrek.cpp:10705 |
| 3 | SIM-0299 | sounds | partial | PlaySoundEffect@WinTrek/WinTrek.cpp:11130 countdownStartingSound | WinTrek/WinTrek.cpp:11130 |
| 3 | SIM-0347 | sounds | partial | PlaySoundIf@WinTrek/trekigc.cpp:984 GetActivateSound | WinTrek/trekigc.cpp:984 |
| 3 | SIM-0349 | sounds | partial | PlaySoundIf@WinTrek/trekigc.cpp:1001 m_vpBurstSound[mount] | WinTrek/trekigc.cpp:1001 |
| 3 | SIM-0368 | sounds | missing | PlaySoundIf@WinTrek/trekigc.cpp:1287 ripcordOnLoopSound | WinTrek/trekigc.cpp:1287 |
| 3 | SIM-0370 | sounds | missing | PlaySoundIf@WinTrek/trekigc.cpp:1293 miningSound | WinTrek/trekigc.cpp:1293 |
| 3 | SIM-0372 | sounds | partial | PlaySoundIf@WinTrek/trekigc.cpp:1312 GetInteriorSound | WinTrek/trekigc.cpp:1312 |
| 3 | SIM-0373 | sounds | partial | PlaySoundIf@WinTrek/trekigc.cpp:1314 GetMainThrusterInteriorSound | WinTrek/trekigc.cpp:1314 |
| 3 | SIM-0374 | sounds | partial | PlaySoundIf@WinTrek/trekigc.cpp:1317 GetManuveringThrusterInteriorSound | WinTrek/trekigc.cpp:1317 |
| 3 | SIM-0375 | sounds | partial | PlaySoundIf@WinTrek/trekigc.cpp:1320 m_pAfterburnerInteriorSound | WinTrek/trekigc.cpp:1320 |
| 3 | SIM-0378 | sounds | partial | PlaySoundIf@WinTrek/trekigc.cpp:1337 GetExteriorSound | WinTrek/trekigc.cpp:1337 |
| 3 | SIM-0379 | sounds | partial | PlaySoundIf@WinTrek/trekigc.cpp:1338 GetMainThrusterExteriorSound | WinTrek/trekigc.cpp:1338 |
| 3 | SIM-0380 | sounds | partial | PlaySoundIf@WinTrek/trekigc.cpp:1341 GetManuveringThrusterExteriorSound | WinTrek/trekigc.cpp:1341 |
| 3 | SIM-0381 | sounds | partial | PlaySoundIf@WinTrek/trekigc.cpp:1344 m_pAfterburnerExteriorSound | WinTrek/trekigc.cpp:1344 |
| 3 | SIM-0384 | sounds | partial | PlaySoundEffect@WinTrek/trekigc.cpp:2089 newShipSound | WinTrek/trekigc.cpp:2089 |
| 3 | SIM-0385 | sounds | partial | PlaySoundEffect@WinTrek/trekigc.cpp:2093 newEnemySound | WinTrek/trekigc.cpp:2093 |
| 3 | SIM-0386 | sounds | partial | PlaySoundEffect@WinTrek/trekigc.cpp:2114 newShipSound | WinTrek/trekigc.cpp:2114 |
| 3 | SIM-0390 | sounds | partial | PlaySoundEffect@WinTrek/trekigc.cpp:3009 newCriticalMsgSound | WinTrek/trekigc.cpp:3009 |
| 3 | SIM-0391 | sounds | partial | PlaySoundEffect@WinTrek/trekigc.cpp:3014 newNonCriticalMsgSound | WinTrek/trekigc.cpp:3014 |
| 3 | SIM-0392 | sounds | partial | PlaySoundEffect@WinTrek/trekigc.cpp:3075 newCriticalMsgSound | WinTrek/trekigc.cpp:3075 |
| 3 | SIM-0393 | sounds | partial | PlaySoundEffect@WinTrek/trekigc.cpp:3080 newNonCriticalMsgSound | WinTrek/trekigc.cpp:3080 |
| 3 | SIM-0394 | sounds | partial | PlaySoundEffect@WinTrek/trekigc.cpp:3998 jumpSound | WinTrek/trekigc.cpp:3998 |
| 3 | SIM-0398 | sounds | partial | PlaySoundEffect@WinTrek/trekigc.cpp:4415 jumpSound | WinTrek/trekigc.cpp:4415 |
| 3 | SIM-0406 | sounds | partial | PlaySoundEffect@WinTrek/trekigc.cpp:4882 salAutopilotEngageSound | WinTrek/trekigc.cpp:4882 |
| 3 | SIM-0414 | sounds | partial | PlayNotificationSound@clintlib/appmsg.cpp:237 salAutopilotEngageSound | clintlib/appmsg.cpp:237 |
| 3 | SIM-0420 | sounds | partial | PlaySoundEffect@clintlib/appmsg.cpp:339 jumpSound | clintlib/appmsg.cpp:339 |
| 3 | SIM-0427 | sounds | partial | PlaySoundEffect@clintlib/appmsg.cpp:1057 jumpSound | clintlib/appmsg.cpp:1057 |
| 3 | SIM-0439 | sounds | partial | PlaySoundEffect@clintlib/appmsg.cpp:3326 GetCompletionSound | clintlib/appmsg.cpp:3326 |
| 3 | SIM-0448 | sounds | partial | PlaySoundEffect@clintlib/clintlib.cpp:3257 deployProbeSound | clintlib/clintlib.cpp:3257 |
| 3 | SIM-0454 | sounds | missing | ReceiveChat@clintlib/clintlib.cpp:3934 salQuitSound | clintlib/clintlib.cpp:3934 |
| 3 | SIM-0455 | sounds | missing | ReceiveChat@clintlib/clintlib.cpp:3939 salQuitSound | clintlib/clintlib.cpp:3939 |
| 3 | SIM-0456 | sounds | missing | ReceiveChat@clintlib/clintlib.cpp:3944 salQuitSound | clintlib/clintlib.cpp:3944 |
| 3 | SIM-0457 | sounds | missing | ReceiveChat@clintlib/clintlib.cpp:3952 salQuitSound | clintlib/clintlib.cpp:3952 |
| 3 | SIM-0458 | sounds | missing | ReceiveChat@clintlib/clintlib.cpp:3960 salQuitSound | clintlib/clintlib.cpp:3960 |
| 3 | SIM-0459 | sounds | missing | ReceiveChat@clintlib/clintlib.cpp:3965 salQuitSound | clintlib/clintlib.cpp:3965 |
| 3 | SIM-0460 | sounds | missing | ReceiveChat@clintlib/clintlib.cpp:3970 salQuitSound | clintlib/clintlib.cpp:3970 |
| 3 | SIM-0461 | sounds | missing | ReceiveChat@clintlib/clintlib.cpp:3975 salChangeSidesSound | clintlib/clintlib.cpp:3975 |
| 3 | SIM-0462 | sounds | missing | ReceiveChat@clintlib/clintlib.cpp:3991 salQuitSound | clintlib/clintlib.cpp:3991 |
| 3 | SIM-0463 | sounds | missing | ReceiveChat@clintlib/clintlib.cpp:3999 salMIASound | clintlib/clintlib.cpp:3999 |
| 3 | SIM-0464 | sounds | missing | ReceiveChat@clintlib/clintlib.cpp:4155 salRecruitsArrivedSound | clintlib/clintlib.cpp:4155 |
| 3 | SIM-0465 | sounds | missing | ReceiveChat@clintlib/clintlib.cpp:4162 salEnemyJoinersSound | clintlib/clintlib.cpp:4162 |
| 3 | SIM-0468 | sounds | missing | dynamic_leave_voice@4006 | clintlib/clintlib.cpp:4006; clintlib/clintlib.cpp:4009 |
| 3 | SIM-0470 | periodic | partial | ship_status_delta_and_turret_updates | FedSrv/FedSrv.CPP:3693 |
| 3 | SIM-0471 | periodic | partial | target_hull_shield_fraction_updates | FedSrv/FedSrv.CPP:4079 |
| 2 | SIM-0014 | constants | partial | c_fcidRipcordTime | Igc/igc.h:73; FedSrv/FedSrv.CPP:1676 |
| 2 | SIM-0074 | updates | partial | mine_expiry_boundary | Igc/mineigc.cpp:150 |
| 2 | SIM-0078 | updates | partial | model_spin | Igc/modelIGC.cpp:76 |
| 2 | SIM-0108 | updates | partial | miner_drilling_controls | Igc/shipIGC.cpp:2254 |
| 2 | SIM-0145 | periodic | partial | chat_budget_recovery | FedSrv/FedSrv.CPP:8282; FedSrv/fsship.h:449-468 |
| 2 | SIM-0147 | periodic | partial | network_roll_call_and_disconnect | FedSrv/FedSrv.CPP:7942 |
| 2 | SIM-0238 | sounds | missing | PlayNotificationSound@Igc/dispenserigc.cpp:98 mountedSound | Igc/dispenserigc.cpp:98 |
| 2 | SIM-0239 | sounds | missing | PlayNotificationSound@Igc/magazineigc.cpp:96 mountedSound | Igc/magazineigc.cpp:96 |
| 2 | SIM-0241 | sounds | missing | PlaySoundEffect@Igc/shieldIGC.cpp:94 activateSound | Igc/shieldIGC.cpp:94 |
| 2 | SIM-0242 | sounds | missing | PlaySoundEffect@Igc/shieldIGC.cpp:185 deactivateSound | Igc/shieldIGC.cpp:185 |
| 2 | SIM-0243 | sounds | missing | PlayNotificationSound@Igc/shieldIGC.cpp:186 salShieldsOfflineSound | Igc/shieldIGC.cpp:186 |
| 2 | SIM-0248 | sounds | missing | PlayNotificationSound@Igc/shipIGC.cpp:1608 salCloakEngageSound | Igc/shipIGC.cpp:1608 |
| 2 | SIM-0263 | sounds | missing | PlayNotificationSound@Igc/shipIGC.h:616 salReloadingAmmoSound | Igc/shipIGC.h:616 |
| 2 | SIM-0264 | sounds | missing | PlayNotificationSound@Igc/shipIGC.h:617 startReloadSound | Igc/shipIGC.h:617 |
| 2 | SIM-0265 | sounds | missing | PlayNotificationSound@Igc/shipIGC.h:646 salReloadingFuelSound | Igc/shipIGC.h:646 |
| 2 | SIM-0266 | sounds | missing | PlayNotificationSound@Igc/shipIGC.h:647 startReloadSound | Igc/shipIGC.h:647 |
| 2 | SIM-0267 | sounds | missing | PlayNotificationSound@Igc/shipIGC.h:1302 salCargoFullSound | Igc/shipIGC.h:1302 |
| 2 | SIM-0268 | sounds | missing | PlayNotificationSound@Igc/shipIGC.h:1307 pickUpPartSound | Igc/shipIGC.h:1307 |
| 2 | SIM-0272 | sounds | missing | StartSound@WinTrek/Slideshow.cpp:105 soundID | WinTrek/Slideshow.cpp:105 |
| 2 | SIM-0273 | sounds | missing | StartSound@WinTrek/WinTrek.cpp:1108 GetSound | WinTrek/WinTrek.cpp:1108 |
| 2 | SIM-0274 | sounds | missing | PlaySoundEffect@WinTrek/WinTrek.cpp:1110 id | WinTrek/WinTrek.cpp:1110 |
| 2 | SIM-0275 | sounds | missing | StartSound@WinTrek/WinTrek.cpp:6567 personalJumpSound | WinTrek/WinTrek.cpp:6567 |
| 2 | SIM-0279 | sounds | missing | StartSound@WinTrek/WinTrek.cpp:8827 newTargetSound | WinTrek/WinTrek.cpp:8827 |
| 2 | SIM-0280 | sounds | missing | PlaySoundEffect@WinTrek/WinTrek.cpp:9047 ungroupWeaponsSound | WinTrek/WinTrek.cpp:9047 |
| 2 | SIM-0281 | sounds | missing | PlaySoundEffect@WinTrek/WinTrek.cpp:9053 groupWeaponsSound | WinTrek/WinTrek.cpp:9053 |
| 2 | SIM-0282 | sounds | missing | PlaySoundEffect@WinTrek/WinTrek.cpp:9098 mountSound | WinTrek/WinTrek.cpp:9098 |
| 2 | SIM-0283 | sounds | missing | PlaySoundEffect@WinTrek/WinTrek.cpp:9102 unmountSound | WinTrek/WinTrek.cpp:9102 |
| 2 | SIM-0284 | sounds | missing | PlaySoundEffect@WinTrek/WinTrek.cpp:9114 salCargoFullSound | WinTrek/WinTrek.cpp:9114 |
| 2 | SIM-0285 | sounds | missing | PlaySoundEffect@WinTrek/WinTrek.cpp:9269 positiveButtonClickSound | WinTrek/WinTrek.cpp:9269 |
| 2 | SIM-0286 | sounds | missing | PlaySoundEffect@WinTrek/WinTrek.cpp:9463 vectorLockSound | WinTrek/WinTrek.cpp:9463 |
| 2 | SIM-0287 | sounds | missing | PlayNotificationSound@WinTrek/WinTrek.cpp:9484 salShipCantRipcordSound | WinTrek/WinTrek.cpp:9484 |
| 2 | SIM-0288 | sounds | missing | PlaySoundEffect@WinTrek/WinTrek.cpp:9529 groupWeaponsSound | WinTrek/WinTrek.cpp:9529 |
| 2 | SIM-0289 | sounds | missing | PlaySoundEffect@WinTrek/WinTrek.cpp:9531 ungroupWeaponsSound | WinTrek/WinTrek.cpp:9531 |
| 2 | SIM-0290 | sounds | missing | PlayNotificationSound@WinTrek/WinTrek.cpp:9693 salReloadingSound | WinTrek/WinTrek.cpp:9693 |
| 2 | SIM-0291 | sounds | missing | PlaySoundEffect@WinTrek/WinTrek.cpp:9719 dropSound | WinTrek/WinTrek.cpp:9719 |
| 2 | SIM-0296 | sounds | missing | PlaySoundEffect@WinTrek/WinTrek.cpp:10658 acceptCommandSound | WinTrek/WinTrek.cpp:10658 |
| 2 | SIM-0300 | sounds | missing | PlaySoundEffect@WinTrek/WinTrek.cpp:11157 countdown10minSound | WinTrek/WinTrek.cpp:11157 |
| 2 | SIM-0301 | sounds | missing | PlaySoundEffect@WinTrek/WinTrek.cpp:11161 countdown5minSound | WinTrek/WinTrek.cpp:11161 |
| 2 | SIM-0302 | sounds | missing | PlaySoundEffect@WinTrek/WinTrek.cpp:11165 countdown1minSound | WinTrek/WinTrek.cpp:11165 |
| 2 | SIM-0303 | sounds | missing | PlaySoundEffect@WinTrek/WinTrek.cpp:11169 countdown30Sound | WinTrek/WinTrek.cpp:11169 |
| 2 | SIM-0317 | sounds | missing | PlaySoundEffect@WinTrek/console.cpp:1268 chatKeySound | WinTrek/console.cpp:1268 |
| 2 | SIM-0318 | sounds | missing | PlaySoundEffect@WinTrek/console.cpp:2339 chatKeySound | WinTrek/console.cpp:2339 |
| 2 | SIM-0319 | sounds | missing | PlaySoundEffect@WinTrek/console.cpp:2424 chatKeySound | WinTrek/console.cpp:2424 |
| 2 | SIM-0320 | sounds | missing | PlaySoundEffect@WinTrek/console.cpp:3274 paneSlideSound | WinTrek/console.cpp:3274 |
| 2 | SIM-0321 | sounds | missing | PlaySoundEffect@WinTrek/load.cpp:236 mouseoverSound | WinTrek/load.cpp:236 |
| 2 | SIM-0322 | sounds | missing | PlaySoundEffect@WinTrek/load.cpp:1487 mountSound | WinTrek/load.cpp:1487 |
| 2 | SIM-0323 | sounds | missing | PlaySoundEffect@WinTrek/load.cpp:1887 mountSound | WinTrek/load.cpp:1887 |
| 2 | SIM-0324 | sounds | missing | PlaySoundEffect@WinTrek/load.cpp:1889 unmountSound | WinTrek/load.cpp:1889 |
| 2 | SIM-0325 | sounds | missing | PlaySoundEffect@WinTrek/load.cpp:2235 positiveButtonClickSound | WinTrek/load.cpp:2235 |
| 2 | SIM-0326 | sounds | missing | PlaySoundEffect@WinTrek/load.cpp:2247 mountSound | WinTrek/load.cpp:2247 |
| 2 | SIM-0327 | sounds | missing | PlaySoundEffect@WinTrek/loadout.cpp:1178 mouseoverSound | WinTrek/loadout.cpp:1178 |
| 2 | SIM-0328 | sounds | missing | PlaySoundEffect@WinTrek/sectormap.cpp:1362 errorSound | WinTrek/sectormap.cpp:1362 |
| 2 | SIM-0329 | sounds | missing | PlaySoundEffect@WinTrek/sectormap.cpp:1367 errorSound | WinTrek/sectormap.cpp:1367 |
| 2 | SIM-0330 | sounds | missing | PlaySoundEffect@WinTrek/sectormap.cpp:1442 mouseoverSound | WinTrek/sectormap.cpp:1442 |
| 2 | SIM-0331 | sounds | missing | PlaySoundEffect@WinTrek/sectormap.cpp:1523 mouseclickSound | WinTrek/sectormap.cpp:1523 |
| 2 | SIM-0332 | sounds | missing | PlaySoundEffect@WinTrek/sectormap.cpp:1586 mouseclickSound | WinTrek/sectormap.cpp:1586 |
| 2 | SIM-0333 | sounds | missing | PlaySoundEffect@WinTrek/teampane.cpp:1177 positiveButtonClickSound | WinTrek/teampane.cpp:1177 |
| 2 | SIM-0334 | sounds | missing | PlaySoundEffect@WinTrek/teamscreen.cpp:2209 mouseclickSound | WinTrek/teamscreen.cpp:2209 |
| 2 | SIM-0335 | sounds | missing | PlaySoundEffect@WinTrek/teamscreen.cpp:2217 mouseclickSound | WinTrek/teamscreen.cpp:2217 |
| 2 | SIM-0336 | sounds | missing | PlaySoundEffect@WinTrek/teamscreen.cpp:2438 mouseclickSound | WinTrek/teamscreen.cpp:2438 |
| 2 | SIM-0337 | sounds | missing | PlaySoundEffect@WinTrek/teleportpane.cpp:285 noTurretsSound | WinTrek/teleportpane.cpp:285 |
| 2 | SIM-0338 | sounds | missing | PlaySoundEffect@WinTrek/teleportpane.cpp:320 personalJumpSound | WinTrek/teleportpane.cpp:320 |
| 2 | SIM-0345 | sounds | partial | StartSound@WinTrek/trekigc.cpp:714 m_idAmbientSound | WinTrek/trekigc.cpp:714 |
| 2 | SIM-0348 | sounds | missing | PlaySoundIf@WinTrek/trekigc.cpp:990 turnSound | WinTrek/trekigc.cpp:990 |
| 2 | SIM-0361 | sounds | missing | StartSound@WinTrek/trekigc.cpp:1264 hullLowSound | WinTrek/trekigc.cpp:1264 |
| 2 | SIM-0362 | sounds | missing | StartSound@WinTrek/trekigc.cpp:1266 ammoLowSound | WinTrek/trekigc.cpp:1266 |
| 2 | SIM-0363 | sounds | missing | StartSound@WinTrek/trekigc.cpp:1268 fuelLowSound | WinTrek/trekigc.cpp:1268 |
| 2 | SIM-0364 | sounds | missing | StartSound@WinTrek/trekigc.cpp:1270 energyLowSound | WinTrek/trekigc.cpp:1270 |
| 2 | SIM-0365 | sounds | missing | StartSound@WinTrek/trekigc.cpp:1273 salNoAmmoSound | WinTrek/trekigc.cpp:1273 |
| 2 | SIM-0366 | sounds | missing | StartSound@WinTrek/trekigc.cpp:1275 salNoFuelSound | WinTrek/trekigc.cpp:1275 |
| 2 | SIM-0371 | sounds | missing | PlaySoundIf@WinTrek/trekigc.cpp:1310 m_pGunnerInteriorSound | WinTrek/trekigc.cpp:1310 |
| 2 | SIM-0376 | sounds | missing | PlaySoundIf@WinTrek/trekigc.cpp:1326 SidethrustInteriorSound | WinTrek/trekigc.cpp:1326 |
| 2 | SIM-0377 | sounds | missing | PlaySoundIf@WinTrek/trekigc.cpp:1329 CapSidethrustInteriorSound | WinTrek/trekigc.cpp:1329 |
| 2 | SIM-0382 | sounds | missing | PlaySoundIf@WinTrek/trekigc.cpp:1351 SidethrustExteriorSound | WinTrek/trekigc.cpp:1351 |
| 2 | SIM-0383 | sounds | missing | PlaySoundIf@WinTrek/trekigc.cpp:1354 CapSidethrustExteriorSound | WinTrek/trekigc.cpp:1354 |
| 2 | SIM-0388 | sounds | missing | PlaySoundEffect@WinTrek/trekigc.cpp:2859 dockedSound | WinTrek/trekigc.cpp:2859 |
| 2 | SIM-0389 | sounds | missing | PlaySoundEffect@WinTrek/trekigc.cpp:2860 salWelcomeHomeSound | WinTrek/trekigc.cpp:2860 |
| 2 | SIM-0395 | sounds | missing | PlayNotificationSound@WinTrek/trekigc.cpp:4237 salRepairedAtCarrierSound | WinTrek/trekigc.cpp:4237 |
| 2 | SIM-0399 | sounds | missing | PlaySoundEffect@WinTrek/trekigc.cpp:4536 idSonicChat | WinTrek/trekigc.cpp:4536 |
| 2 | SIM-0400 | sounds | missing | PlaySoundEffect@WinTrek/trekigc.cpp:4540 newCommandMsgSound | WinTrek/trekigc.cpp:4540 |
| 2 | SIM-0401 | sounds | missing | PlaySoundEffect@WinTrek/trekigc.cpp:4546 newChatMsgFromCommanderSound | WinTrek/trekigc.cpp:4546 |
| 2 | SIM-0402 | sounds | missing | PlaySoundEffect@WinTrek/trekigc.cpp:4548 newPersonalMsgSound | WinTrek/trekigc.cpp:4548 |
| 2 | SIM-0403 | sounds | missing | PlaySoundEffect@WinTrek/trekigc.cpp:4555 newChatMsgFromCommanderSound | WinTrek/trekigc.cpp:4555 |
| 2 | SIM-0404 | sounds | missing | PlaySoundEffect@WinTrek/trekigc.cpp:4557 newChatMsgSound | WinTrek/trekigc.cpp:4557 |
| 2 | SIM-0405 | sounds | missing | PlaySoundEffect@WinTrek/trekigc.cpp:4560 newOfflineChatMsgSound | WinTrek/trekigc.cpp:4560 |
| 2 | SIM-0410 | sounds | missing | StartSound@WinTrek/trekigc.cpp:5061 missileLockSound | WinTrek/trekigc.cpp:5061 |
| 2 | SIM-0411 | sounds | partial | StartSound@WinTrek/trekigc.cpp:5085 m_idAmbient | WinTrek/trekigc.cpp:5085 |
| 2 | SIM-0412 | sounds | missing | StartSound@WinTrek/trekmdl.cpp:471 m_soundId | WinTrek/trekmdl.cpp:471 |
| 2 | SIM-0413 | sounds | missing | PlayNotificationSound@clintlib/appmsg.cpp:163 salRepairedAtCarrierSound | clintlib/appmsg.cpp:163 |
| 2 | SIM-0415 | sounds | missing | PlaySoundEffect@clintlib/appmsg.cpp:282 pickUpPowerupSound | clintlib/appmsg.cpp:282 |
| 2 | SIM-0416 | sounds | missing | PlaySoundEffect@clintlib/appmsg.cpp:286 pickUpDevelopmentSound | clintlib/appmsg.cpp:286 |
| 2 | SIM-0417 | sounds | missing | PlaySoundEffect@clintlib/appmsg.cpp:291 pickUpCashSound | clintlib/appmsg.cpp:291 |
| 2 | SIM-0418 | sounds | missing | PlaySoundEffect@clintlib/appmsg.cpp:303 pickUpDevelopmentSound | clintlib/appmsg.cpp:303 |
| 2 | SIM-0425 | sounds | missing | PlayNotificationSound@clintlib/appmsg.cpp:970 salNoRipcordSound | clintlib/appmsg.cpp:970 |
| 2 | SIM-0428 | sounds | missing | PlaySoundEffect@clintlib/appmsg.cpp:1238 investorSound | clintlib/appmsg.cpp:1238 |
| 2 | SIM-0429 | sounds | missing | PlaySoundEffect@clintlib/appmsg.cpp:1313 paydaySound | clintlib/appmsg.cpp:1313 |
| 2 | SIM-0430 | sounds | missing | PlaySoundEffect@clintlib/appmsg.cpp:1757 newCommandMsgSound | clintlib/appmsg.cpp:1757 |
| 2 | SIM-0431 | sounds | missing | PlaySoundEffect@clintlib/appmsg.cpp:1894 personalJumpSound | clintlib/appmsg.cpp:1894 |
| 2 | SIM-0432 | sounds | missing | PlaySoundEffect@clintlib/appmsg.cpp:2071 boardSound | clintlib/appmsg.cpp:2071 |
| 2 | SIM-0433 | sounds | missing | PlaySoundEffect@clintlib/appmsg.cpp:2481 salPlayerWaitingSound | clintlib/appmsg.cpp:2481 |
| 2 | SIM-0434 | sounds | missing | PlaySoundEffect@clintlib/appmsg.cpp:2896 destroyedSound | clintlib/appmsg.cpp:2896 |
| 2 | SIM-0435 | sounds | missing | PlaySoundEffect@clintlib/appmsg.cpp:2965 commanderSound | clintlib/appmsg.cpp:2965 |
| 2 | SIM-0436 | sounds | missing | PlaySoundEffect@clintlib/appmsg.cpp:3149 enemyFlagLostSound | clintlib/appmsg.cpp:3149 |
| 2 | SIM-0437 | sounds | missing | PlaySoundEffect@clintlib/appmsg.cpp:3156 artifactFoundSound | clintlib/appmsg.cpp:3156 |
| 2 | SIM-0438 | sounds | missing | PlaySoundEffect@clintlib/appmsg.cpp:3169 flagLostSound | clintlib/appmsg.cpp:3169 |
| 2 | SIM-0443 | sounds | missing | PlayNotificationSound@clintlib/clintlib.cpp:3216 salReloadingMissilesSound | clintlib/clintlib.cpp:3216 |
| 2 | SIM-0444 | sounds | missing | PlaySoundEffect@clintlib/clintlib.cpp:3217 startReloadSound | clintlib/clintlib.cpp:3217 |
| 2 | SIM-0445 | sounds | missing | PlayNotificationSound@clintlib/clintlib.cpp:3224 salMissilesDepletedSound | clintlib/clintlib.cpp:3224 |
| 2 | SIM-0446 | sounds | missing | PlaySoundEffect@clintlib/clintlib.cpp:3249 deployChaffSound | clintlib/clintlib.cpp:3249 |
| 2 | SIM-0447 | sounds | missing | PlaySoundEffect@clintlib/clintlib.cpp:3253 deployMineSound | clintlib/clintlib.cpp:3253 |
| 2 | SIM-0449 | sounds | missing | PlayNotificationSound@clintlib/clintlib.cpp:3394 salReloadingChaffSound | clintlib/clintlib.cpp:3394 |
| 2 | SIM-0450 | sounds | missing | PlayNotificationSound@clintlib/clintlib.cpp:3399 salReloadingDispenserSound | clintlib/clintlib.cpp:3399 |
| 2 | SIM-0451 | sounds | missing | PlaySoundEffect@clintlib/clintlib.cpp:3404 startReloadSound | clintlib/clintlib.cpp:3404 |
| 2 | SIM-0452 | sounds | missing | PlayNotificationSound@clintlib/clintlib.cpp:3412 salChaffDepletedSound | clintlib/clintlib.cpp:3412 |
| 2 | SIM-0453 | sounds | missing | PlayNotificationSound@clintlib/clintlib.cpp:3417 salDispenserEmptySound | clintlib/clintlib.cpp:3417 |
| 2 | SIM-0467 | sounds | missing | PlayNotificationSound@clintlib/clintlib.h:1175 salNoRipcordSound | clintlib/clintlib.h:1175 |
| 1 | SIM-0137 | periodic | partial | lobby_dirty_rate_limit | FedSrv/fsmission.cpp:5606 |

# P681 — parity wave 7: simulation ordnance logic

Entry revision: `97072f7d` on `codex/wave4`. Changes remain in the working tree; no commits or pushes. Native citations below are relative to `~/Development/Allegiance/src/`, read from the local native checkout. The interface matrix is untouched.

## Bundle and scope

Selected six related update rows, weight **22**: missile activation/steering and burnout, teleport-probe activation, armed-probe targeting/emission, and projectile burnout. They share model clocks, authoritative ordnance updates, and projectile creation. This addresses concrete partial implementations without editing `Sector.Update`, swept-collision time order (SIM-0063), the broadcast pipeline or interest tiers, security, rendering, Android, or the installer. No sound rule or audio asset is changed.

The measured baseline is **40.51%** simulation parity, rather than the brief's approximate historical 38%. Periodic is already 17.02% after P676; callbacks is 33.33%. This bundle stays within the brief's permitted updates area.

## Native rules, implementation, and evidence

| Row | Weight | Native source | Port implementation | New regression methods (P681OrdnanceRulesTests) |
| --- | ---: | --- | --- | --- |
| SIM-0075 | 4 | Igc/missileigc.cpp:104-120,171-419 | src/Allegiance.Core/Sim/Missile.cs:178; src/Allegiance.Core/Sim/MissileIntercept.cs:10 | Missile_activation_inside_tick_uses_only_active_interval; Acceleration_intercept_satisfies_native_quartic; Missile_leads_moving_target_and_drops_it_after_sector_departure |
| SIM-0076 | 4 | Igc/missileigc.cpp:354-360 | src/Allegiance.Core/Sim/Missile.cs:178 | Missile_burnout_waits_for_previous_clock_and_explodes_at_previous_position |
| SIM-0081 | 4 | Igc/probeigc.cpp:151-154,333-349 | src/Allegiance.Core/Sim/Probe.cs:79; src/Allegiance.Core/Sim/Probe.cs:647 | Teleport_activation_is_once_and_expiry_takes_precedence |
| SIM-0082 | 4 | Igc/probeigc.cpp:255-435; Igc/probeigc.h:163-191; Igc/igc.h:4297-4305 | src/Allegiance.Core/Sim/Probe.cs:408; src/Allegiance.Core/Sim/Probe.cs:457; src/Allegiance.Core/Sim/Probe.cs:574; src/Allegiance.Core/Sim/Probe.cs:595 | Probe_AOE_prioritizes_missiles_otherwise_ships; Probe_specific_target_must_be_present_hostile_damageable_and_scanned; Probe_prioritizes_known_station_and_excludes_allied_station; Probe_requires_line_of_sight_except_for_flag_carrier; Probe_respects_live_alliance_groups_and_reacquires_when_alliance_ends |
| SIM-0085 | 3 | Igc/probetypeigc.cpp:43-47; Igc/probeigc.cpp:373-379,468-526; _Utility/HitTest.cpp:796-801; zlib/orientation.cpp:220-230 | src/Allegiance.Core/Sim/Probe.cs:495; src/Allegiance.Core/Sim/Probe.cs:542 | Probe_burst_uses_historical_clocks_rotated_muzzle_and_launcher_fallback; Probe_idle_clock_clamps_next_burst_to_previous_update; Probe_transverse_muzzle_preserves_current_up_when_turning |
| SIM-0086 | 3 | Igc/projectileIGC.cpp:120-143 | src/Allegiance.Core/Sim/Projectile.cs:102 | Projectile_crossing_blast_is_at_expiry_then_terminates_next_update |

All six rows change **partial → implemented**. The new regressions live in `tests/Allegiance.Core.Tests/P681OrdnanceRulesTests.cs:9` and combine synthetic loaded core/map cases with a real-core runtime-alliance regression, actual models, actual damage, CVH resolution, and host callbacks. Existing corpus-backed and loopback tests also execute.

**Missiles.** `Igc/missileigc.cpp:104-120,171-352,363-419` seeds and retains an acceleration-based intercept estimate, solves the native quartic, clamps active turning/thrust time to the activation boundary, and preserves the literal native forward teleport for the inactive prefix. Native subtracts a backward deltaV at :404, which adds forward displacement; the regression checks that exact sign. The port retains native Newton branch selection, the early approaching-target root, residual tolerance, and iteration limit. Tests independently check the physical intercept equation, including positive/negative/zero relative speed. Target departure drops tracking, and existing side turn-rate scaling remains active.

**Burnout.** `Igc/missileigc.cpp:354-360` checks the previous update clock: a crossing missile still moves, and its next update detonates at the prior position and terminates once. `Igc/projectileIGC.cpp:120-143` instead dispatches its blast on the crossing tick, at the exact expiry position/time, and terminates on the next update. The existing sector explosion resolver delivers the damage; its ordering and `Sector.Update` structure are unchanged. Exact-boundary and overshoot regressions distinguish these lifecycles.

**Probes.** `Igc/probeigc.cpp:151-154,333-349` activates zero-delay probes during construction and dispatches the strict-previous-clock/inclusive-current-clock activation edge once, with expiry taking precedence. This now reaches the actual `IIgcSite.ActivateTeleportProbe` seam through `IProbeIgc`; consumer UI feedback is separately partial in SIM-0224 and earns no new credit.

Target priority follows `Igc/probeigc.cpp:255-435`: a specific target never falls back; ships resolve to their source carrier; static station adapters are accepted. General acquisition considers stations, then AOE missiles, ships, then non-AOE missiles, taking the nearest valid member within a category. Live alliance groups (`Igc/igc.h:4297-4305`), explicit side pairs, damageability, lifepod, known-static visibility, signature/GA scanning, flag-carrier, and asteroid line-of-sight rules follow `Igc/probeigc.h:163-191`. Probe first-shot reveal (SIM-0083) remains missing; it is a separate rule.

Emission follows `Igc/probetypeigc.cpp:43-47`, `Igc/probeigc.cpp:373-379,468-526`, `_Utility/HitTest.cpp:796-801`, and `zlib/orientation.cpp:220-230`: resolve the real `wepemt` CVH frame, apply its X flip and radius scale, rotate it into the aim orientation while preserving the probe’s current up/roll with native parallel/antiparallel handling, and create each burst round at its historical `nextFire` time. Missing geometry gives the native zero offset. A missing or detached ship launcher falls back to the probe while retaining its owning mission; both paths emit the actual projectile-created callback. The existing ship-only wire export path is outside this model-emission row and is untouched.

Three old assertions were corrected to native law: `P649CombatBlastParityTests.Expiring_missile_blast_does_not_destroy_or_notify_its_source_twice` now observes the crossing tick before the next-tick detonation; `MissileHomingTests.Missile_turns_toward_an_offset_target_over_several_ticks` checks closing movement because the native acceleration lead need not align with the geometric line; `MissileHomingTests.Missile_drops_a_target_that_leaves_its_sector` checks the vector thrust increment rather than incorrectly assuming scalar speed grows by acceleration × dt while lateral velocity remains. The armed-probe corpus fixture also now sets the target ships’ actual sector, matching native cluster membership. A broader discovery run caught a real candidate regression where list membership alone admitted a migrated target; both probe eligibility and missile tracking now check the actual ship sector, including stale-list coverage. `CombatWeaponFiringTests.Projectile_expires_and_restores_the_model_count` also now asserts that the crossing tick retains the projectile, advances one update, then checks the original empty/model-count assertions (`Igc/projectileIGC.cpp:120-143`). The first full Core attempt exposed that stale fixture and was stopped before completion; its console evidence is `/tmp/p681/results/candidate-core-interrupted.log`. These were deterministic corrections, not load flakes. No deadlines, quarantine tags, or timing assertions were relaxed.

## Scorecard before → after

Both `python3 scripts/parity-scorecard.py` runs validate **473 simulation rows**. Eligible populations and weights are unchanged; partial receives zero credit. The script also prints the interface scorecard, but no interface inventory or edits are included in this report.

| Simulation area | Weighted before → after | Complete / eligible before → after |
| --- | --- | --- |
| callbacks | 33.33% (66/198) → 33.33% (66/198) | 22/66 → 22/66 |
| constants | 95.56% (129/135) → 95.56% (129/135) | 37/39 → 37/39 |
| periodic | 17.02% (8/47) → 17.02% (8/47) | 2/15 → 2/15 |
| sounds (unchanged) | 21.04% (125/594) → 21.04% (125/594) | 38/228 → 38/228 |
| updates | **57.88% (202/349) → 64.18% (224/349)** | **46/87 → 52/87** |
| warnings | 100.00% (10/10) → 100.00% (10/10) | 2/2 → 2/2 |
| **TOTAL** | **40.51% (540/1333) → 42.16% (562/1333)** | **147/437 → 153/437** |

## Verification

`dotnet build AllegianceCore.slnx --nologo -m:1 -v:q` completed with **0 warnings / 0 errors**. Source/test SHA-256 fingerprints for all nine changed executable files match that final build after verification. `git diff --check` is clean. The scorecard validates the final matrix; only the six selected records differ from the initial simulation matrix, with all weights and native rule text preserved.

**113 affected methods / 179 data cases**, including **14 new methods / 35 new cases**, passed **3/3 exact-method individual invocations** on unchanged product sources (339 invocations). Each theory invocation executes all of its data rows. Coverage includes armed-probe firing and live damage, probe targeting and wire deployment, teleport/ripcord loopback, missile steering and firing/combat, side GAs, expiry damage, and real projectile broadcasts. The expanded discovery runs also passed all 179 cases.

The three required **unfiltered** assemblies then ran sequentially, with `local.env` loaded for real artwork/native-source checks. ClientCore additionally enabled `ALLEGIANCE_LIVE_P660_SHOWCASE=1`, so its local 15v15 next-match showcase executed. Every command, filter, result count, failure, and raw TRX/log path is retained in [P681_RESULTS.json](P681_RESULTS.json). Earlier exploratory snapshots are explicitly labeled `candidate-*`. The 107-method cohort ran against the final product build; a later test-only rebuild corrected the legacy projectile-expiry fixture, and its entire class then ran 3× individually before the three final full suites. Product-source fingerprints remained identical between those builds.

| Assembly | Passed | Failed in full pass | Skipped | Total |
| --- | ---: | ---: | ---: | ---: |
| Allegiance.Core.Tests | 4388 | 2 | 0 | 4390 |
| Allegiance.ClientCore.Tests | 966 | 0 | 0 | 966 |
| Allegiance.ClientCore.MonoGame.Tests | 2167 | 0 | 3 | 2170 |

All **2 full-pass failures** passed **3/3 exact-method isolated reruns after all three suites finished**. Under the brief’s lane rule these are load/receipt flakes, recorded in [TEST_FLAKES.md](../TEST_FLAKES.md). Their raw failures remain visible above; no assertion, deadline, quarantine, or product change was made for them.

| Test | Full-pass symptom | Final isolation |
| --- | --- | --- |
| Allegiance.Core.Tests.Transport.InboundFloodSafetyLiveTests.Unsupported_fmid_flood_log_is_rate_limited_others_keep_ticking | healthy bot 0 must keep receiving per-tick updates during the flood (had 0, now 0) | 3/3 pass |
| Allegiance.Core.Tests.LoadoutChangeLiveTests.Mount_and_remove_part_broadcast_loadout_change_to_owner_and_teammate_but_not_enemy | Assert.Contains() Failure: Filter not matched in collection; Collection: [] | 3/3 pass |

The only skipped tests are the three existing optional private Meshy-pack cases in MonoGame. No required live/loopback or native-source/artwork test was filtered out. No remote rig / 221 / 200v200 run was attempted.

The source changes are confined to ordnance, with no bot, drone, mining, or economy implementation edits. Extra continuity verification nevertheless ran on the same build: a **15v15, 90-second local MCP battle** with the kill assertion enabled (`ALLEGIANCE_LIVE_P632_ASSERT=1`, `ALLEGIANCE_P632_SIZE=15`, `ALLEGIANCE_P632_SECONDS=90`, `ALLEGIANCE_P632_DEBUG=1`), plus three individual passes each of real MCP purchase/death/pod-docking/relaunch/refire and loopback mine/dock/sell/PAYDAY delivery.

Battle receipt:

```text
RESULT botsReached=30/30 killsA=2 killsB=6 killsTotal=8 deathsA=6 deathsB=2 shots=12762 hits=5385 hitRate=42.2 % friendly=0 shieldHits=3269 hullHits=2116 maxTargeted=29 maxVisibleEnemies=15
```

The ClientCore full suite also passed the opt-in 30-bot/two-human next-match migration showcase. These are separate locally booted battles and continuity checks, all using the verified final assembly. Raw artifacts remain in `/tmp/p681/results/` and `/tmp/p632_p681.txt`; durable invocation outcomes and the battle receipt are preserved here and in the JSON ledger.


## Initial missing / partial simulation inventory by weight

This is the entry-state **simulation-only, non-sound** inventory, sorted by descending weight then row ID. The six selected rows show their completion. Other entries retain their starting status. Sound-area rows, sound callbacks, and sound-specific update rows are omitted per the brief; adjacent callbacks and constants are listed for visibility. Exclusions are marked where directly identifiable from this matrix; none were implemented by this wave.

| Weight | Row | Area | Native item | Before → after | Native citation | Scope note |
| ---: | --- | --- | --- | --- | --- | --- |
| 5 | SIM-0063 | updates | swept_collision_time_order | partial | Igc/clusterIGC.cpp:255 | Excluded: Sector.Update / collision structure |
| 4 | SIM-0033 | constants | c_fcidWarpBombDelay | partial | Igc/igc.h:98; FedSrv/FedSrv.CPP:12551 |  |
| 4 | SIM-0044 | updates | ore_seen_by_side | partial | Igc/asteroidigc.h:58 |  |
| 4 | SIM-0045 | updates | asteroid_build_completion | partial | Igc/asteroidigc.h:90 |  |
| 4 | SIM-0048 | updates | build_effect_phase_animation | partial | Igc/buildingeffectigc.h:160 |  |
| 4 | SIM-0059 | updates | docked_npc_launch_rules | partial | Igc/clusterIGC.cpp:116 | Excluded: Sector.Update / collision structure |
| 4 | SIM-0060 | updates | station_consumed_rock_fog | missing | Igc/clusterIGC.cpp:141 | Excluded: Sector.Update / collision structure |
| 4 | SIM-0062 | updates | global_preplot_plot_execute_order | partial | Igc/clusterIGC.cpp:170 | Excluded: Sector.Update / collision structure |
| 4 | SIM-0064 | updates | mine_attrition | partial | Igc/clusterIGC.cpp:325 | Excluded: Sector.Update / collision structure |
| 4 | SIM-0065 | updates | visibility_every_tenth_pass | partial | Igc/clusterIGC.cpp:348 | Excluded: Sector.Update / collision structure |
| 4 | SIM-0069 | updates | dispenser_mount_and_fire | partial | Igc/dispenserigc.cpp:84 |  |
| 4 | SIM-0075 | updates | missile_delayed_steering | partial → implemented | Igc/missileigc.cpp:364 |  |
| 4 | SIM-0076 | updates | missile_burnout | partial → implemented | Igc/missileigc.cpp:356 |  |
| 4 | SIM-0081 | updates | teleport_probe_activation_edge | partial → implemented | Igc/probeigc.cpp:340 |  |
| 4 | SIM-0082 | updates | probe_target_priority | partial → implemented | Igc/probeigc.cpp:388 |  |
| 4 | SIM-0083 | updates | probe_first_shot_reveal | missing | Igc/probeigc.cpp:448 |  |
| 4 | SIM-0088 | updates | shield_mount_gate | partial | Igc/shieldIGC.cpp:72 |  |
| 4 | SIM-0101 | updates | npc_plan_legality | partial | Igc/shipIGC.cpp:1938 |  |
| 4 | SIM-0107 | updates | miner_capacity_and_depletion | partial | Igc/shipIGC.cpp:2208 |  |
| 4 | SIM-0109 | updates | constructor_drill_alignment | partial | Igc/shipIGC.cpp:2272 |  |
| 4 | SIM-0110 | updates | idle_npc_complaint_backoff | partial | Igc/shipIGC.cpp:2308 |  |
| 4 | SIM-0112 | updates | wingman_defend_acquire_return | partial | Igc/shipIGC.cpp:2374 |  |
| 4 | SIM-0113 | updates | wingman_unseen_target_memory | missing | Igc/shipIGC.cpp:2410 |  |
| 4 | SIM-0114 | updates | wingman_range_bands | partial | Igc/shipIGC.cpp:2483 |  |
| 4 | SIM-0115 | updates | wingman_missile_evasion_gate | missing | Igc/shipIGC.cpp:2578 |  |
| 4 | SIM-0119 | updates | goto_completion_and_layer_lay | partial | Igc/shipIGC.cpp:2802 |  |
| 4 | SIM-0129 | updates | treasure_deceleration | missing | Igc/treasureIGC.cpp:221 |  |
| 4 | SIM-0133 | periodic | countdown_game_winner | missing | FedSrv/fsmission.cpp:2335 |  |
| 4 | SIM-0135 | periodic | ambient_treasure_regeneration | missing | FedSrv/fsmission.cpp:2554 |  |
| 4 | SIM-0142 | periodic | auto_start_restart_ready_rules | partial | FedSrv/FedSrv.CPP:8178 |  |
| 4 | SIM-0143 | periodic | start_countdown_expiry | partial | FedSrv/FedSrv.CPP:8237 |  |
| 3 | SIM-0047 | updates | bucket_percent_notification | partial | Igc/bucketigc.h:65 |  |
| 3 | SIM-0058 | updates | cloak_mount_readiness | partial | Igc/cloakigc.cpp:68 |  |
| 3 | SIM-0067 | updates | explosion_mine_strength | partial | Igc/clusterIGC.cpp:443 |  |
| 3 | SIM-0070 | updates | damage_credit_slot_decay | partial | Igc/igc.h:5081 |  |
| 3 | SIM-0085 | updates | probe_projectile_emission | partial → implemented | Igc/probeigc.cpp:479 |  |
| 3 | SIM-0086 | updates | projectile_burnout_blast | partial → implemented | Igc/projectileIGC.cpp:120 |  |
| 3 | SIM-0100 | updates | npc_ammo_goal_recovery | partial | Igc/shipIGC.cpp:1715 |  |
| 3 | SIM-0104 | updates | npc_ripcord_retry | partial | Igc/shipIGC.cpp:2067 |  |
| 3 | SIM-0117 | updates | wingman_cross_sector_pursuit | partial | Igc/shipIGC.cpp:2707 |  |
| 3 | SIM-0118 | updates | wingman_goto_opportunism | partial | Igc/shipIGC.cpp:2747 |  |
| 3 | SIM-0138 | periodic | ballot_resolution_boundary | partial | FedSrv/fsmission.cpp:5817 |  |
| 3 | SIM-0141 | periodic | server_sim_broadcast_cadence | partial | FedSrv/FedSrv.CPP:4238 | Excluded: broadcast pipeline |
| 3 | SIM-0144 | periodic | station_state_docked_broadcast | partial | FedSrv/FedSrv.CPP:8253 | Excluded: broadcast pipeline |
| 3 | SIM-0150 | periodic | warp_reentry_timer | partial | FedSrv/fsship.cpp:1185; FedSrv/fsship.cpp:1190 |  |
| 3 | SIM-0154 | updates | afterburner_mount_fraction_persistence | partial | Igc/afterburnerIGC.cpp:111; Igc/afterburnerIGC.cpp:117 |  |
| 3 | SIM-0160 | callbacks | HullTypeCompleted@4771 | partial | Igc/igc.h:4771 ; Igc/bucketigc.h:245 |  |
| 3 | SIM-0161 | callbacks | PartTypeCompleted@4772 | partial | Igc/igc.h:4772 ; Igc/bucketigc.h:253 |  |
| 3 | SIM-0163 | callbacks | BucketChangeEvent@4775 | partial | Igc/igc.h:4775 ; Igc/bucketigc.cpp:98; Igc/bucketigc.h:67; Igc/bucketigc.h:279; Igc/bucketigc.h:290; Igc/bucketigc.h:299 |  |
| 3 | SIM-0164 | callbacks | SideBuildingTechChange@4776 | partial | Igc/igc.h:4776 ; Igc/sideigc.h:166 |  |
| 3 | SIM-0167 | callbacks | StationTypeChange@4779 | partial | Igc/igc.h:4779 ; Igc/stationIGC.cpp:75; Igc/stationIGC.cpp:276 |  |
| 3 | SIM-0168 | callbacks | ClusterUpdateEvent@4781 | partial | Igc/igc.h:4781 ; Igc/clusterIGC.cpp:357 |  |
| 3 | SIM-0169 | callbacks | BuildStation@4783 | partial | Igc/igc.h:4783 ; Igc/buildingeffectigc.h:348 |  |
| 3 | SIM-0172 | callbacks | ReceiveChat@4792 | partial | Igc/igc.h:4792 |  |
| 3 | SIM-0173 | callbacks | SendChat@4801 | partial | Igc/igc.h:4801 ; Igc/shipIGC.cpp:1895; Igc/shipIGC.cpp:1898; Igc/shipIGC.cpp:1901; Igc/shipIGC.cpp:2171; Igc/shipIGC.h:1732 |  |
| 3 | SIM-0174 | callbacks | SendChatf@4812 | partial | Igc/igc.h:4812 ; Igc/buildingeffectigc.h:320; Igc/shipIGC.cpp:2386; Igc/shipIGC.cpp:2401; Igc/shipIGC.cpp:2561; Igc/shipIGC.cpp:2692; Igc/shipIGC.cpp:2779; Igc/shipIGC.h:2408; Igc/shipIGC.h:2449 |  |
| 3 | SIM-0175 | callbacks | GetMoneyRequest@4828 | partial | Igc/igc.h:4828 |  |
| 3 | SIM-0180 | callbacks | PlayVisualEffect@4834 | partial | Igc/igc.h:4834 ; Igc/shipIGC.cpp:836; Igc/shipIGC.cpp:1006; Igc/shipIGC.cpp:1191; Igc/shipIGC.cpp:1195 | Excluded: visual/rendering work |
| 3 | SIM-0182 | callbacks | TerminateModelEvent@4836 | partial | Igc/igc.h:4836 ; Igc/modelIGC.cpp:61 |  |
| 3 | SIM-0183 | callbacks | TerminateMissionEvent@4837 | partial | Igc/igc.h:4837 ; Igc/missionigc.cpp:3082 |  |
| 3 | SIM-0185 | callbacks | KillAsteroidEvent@4839 | missing | Igc/igc.h:4839 ; Igc/asteroidigc.h:162; Igc/clusterIGC.cpp:155 |  |
| 3 | SIM-0186 | callbacks | DrainAsteroidEvent@4840 | partial | Igc/igc.h:4840 |  |
| 3 | SIM-0190 | callbacks | KillBuildingEffectEvent@4844 | partial | Igc/igc.h:4844 ; Igc/shipIGC.cpp:218 |  |
| 3 | SIM-0193 | callbacks | PostPlainText@4847 | partial | Igc/igc.h:4847 |  |
| 3 | SIM-0194 | callbacks | PostText@4848 | partial | Igc/igc.h:4848 |  |
| 3 | SIM-0195 | callbacks | PostNotificationText@4849 | partial | Igc/igc.h:4849 ; Igc/afterburnerIGC.cpp:120; Igc/cloakigc.cpp:76; Igc/dispenserigc.cpp:99; Igc/magazineigc.cpp:97; Igc/shieldIGC.cpp:83; Igc/shipIGC.h:612; Igc/shipIGC.h:618; Igc/shipIGC.h:642; Igc/shipIGC.h:648; Igc/weaponIGC.h:47 |  |
| 3 | SIM-0197 | callbacks | TargetKilled@4851 | partial | Igc/igc.h:4851 |  |
| 3 | SIM-0199 | callbacks | DamageShipEvent@4853 | partial | Igc/igc.h:4853 ; Igc/shipIGC.cpp:398; Igc/shipIGC.cpp:1312 |  |
| 3 | SIM-0205 | callbacks | LandOnCarrierEvent@4859 | partial | Igc/igc.h:4859 ; Igc/shipIGC.cpp:1050; Igc/shipIGC.cpp:1057 |  |
| 3 | SIM-0206 | callbacks | RescueShipEvent@4860 | partial | Igc/igc.h:4860 ; Igc/shipIGC.cpp:865; Igc/shipIGC.cpp:907; Igc/shipIGC.cpp:1035; Igc/shipIGC.cpp:1040 |  |
| 3 | SIM-0207 | callbacks | WarpBombEvent@4861 | partial | Igc/igc.h:4861 ; Igc/missileigc.cpp:466 |  |
| 3 | SIM-0208 | callbacks | HitWarpEvent@4862 | partial | Igc/igc.h:4862 ; Igc/shipIGC.cpp:636 |  |
| 3 | SIM-0209 | callbacks | HitTreasureEvent@4863 | partial | Igc/igc.h:4863 ; Igc/treasureIGC.h:92 |  |
| 3 | SIM-0212 | callbacks | ContinueRipcord@4866 | partial | Igc/igc.h:4866 ; Igc/shipIGC.cpp:280 |  |
| 3 | SIM-0213 | callbacks | UseRipcord@4867 | partial | Igc/igc.h:4867 ; Igc/shipIGC.cpp:286 |  |
| 3 | SIM-0214 | callbacks | FireMissile@4868 | partial | Igc/igc.h:4868 ; Igc/magazineigc.cpp:118 |  |
| 3 | SIM-0215 | callbacks | FireExpendable@4870 | partial | Igc/igc.h:4870 ; Igc/dispenserigc.cpp:121 |  |
| 3 | SIM-0219 | callbacks | ChangeStation@4876 | partial | Igc/igc.h:4876 ; Igc/shipIGC.h:483 |  |
| 3 | SIM-0220 | callbacks | ChangeCluster@4879 | partial | Igc/igc.h:4879 ; Igc/shipIGC.h:254 |  |
| 3 | SIM-0221 | callbacks | CommandChangedEvent@4882 | partial | Igc/igc.h:4882 ; Igc/shipIGC.h:788 |  |
| 3 | SIM-0224 | callbacks | ActivateTeleportProbe@4888 | partial | Igc/igc.h:4888 ; Igc/probeigc.cpp:153; Igc/probeigc.cpp:347 |  |
| 3 | SIM-0225 | callbacks | DestroyTeleportProbe@4890 | partial | Igc/igc.h:4890 ; Igc/probeigc.cpp:230 |  |
| 3 | SIM-0228 | callbacks | ChangeGameState@4899 | partial | Igc/igc.h:4899 |  |
| 3 | SIM-0229 | callbacks | CreateBuildingEffect@4901 | partial | Igc/igc.h:4901 ; Igc/shipIGC.cpp:884 |  |
| 3 | SIM-0231 | callbacks | GetCluster@4909 | partial | Igc/igc.h:4909 ; Igc/asteroidigc.cpp:29; Igc/buildingeffectigc.h:84; Igc/buoyigc.cpp:60; Igc/clusterIGC.cpp:312; Igc/clusterIGC.cpp:313; Igc/clusterIGC.cpp:416; Igc/clusterIGC.cpp:444; Igc/clusterIGC.h:418; Igc/clusterIGC.h:436; Igc/common.cpp:481; Igc/common.cpp:687; Igc/common.cpp:726; Igc/common.cpp:772; Igc/common.cpp:775; Igc/common.cpp:785; Igc/common.cpp:788; Igc/common.cpp:871; Igc/common.cpp:901; Igc/common.cpp:923; Igc/common.cpp:972; Igc/common.cpp:989; Igc/common.cpp:1021; Igc/common.cpp:1034; Igc/common.cpp:1699; Igc/common.cpp:1841; Igc/common.cpp:1854; Igc/common.cpp:1873; Igc/common.cpp:1925; Igc/common.cpp:2244; Igc/common.cpp:2332; Igc/common.cpp:2333; Igc/common.cpp:2753; Igc/common.cpp:2782; Igc/igc.h:4912; Igc/igc.h:4919; Igc/igc.h:5333; Igc/igc.h:5334; Igc/magazineigc.cpp:127; Igc/mapmakerigc.cpp:239; Igc/mapmakerigc.cpp:263; Igc/mapmakerigc.cpp:264; Igc/mapmakerigc.cpp:279; Igc/mapmakerigc.cpp:335; Igc/mapmakerigc.cpp:816; Igc/mapmakerigc.cpp:822; Igc/mapmakerigc.cpp:828; Igc/mapmakerigc.cpp:834; Igc/mapmakerigc.cpp:989; Igc/mapmakerigc.cpp:1039; Igc/mineigc.cpp:62; Igc/missileigc.cpp:108; Igc/missileigc.cpp:371; Igc/missionigc.cpp:3661; Igc/modelIGC.h:589; Igc/probeigc.cpp:66; Igc/probeigc.cpp:392; Igc/probeigc.h:169; Igc/projectileIGC.cpp:193; Igc/shipIGC.cpp:1765; Igc/shipIGC.cpp:1922; Igc/shipIGC.cpp:1950; Igc/shipIGC.cpp:2036; Igc/shipIGC.cpp:2354; Igc/shipIGC.cpp:2376; Igc/shipIGC.cpp:2714; Igc/shipIGC.cpp:2720; Igc/shipIGC.cpp:2727; Igc/shipIGC.cpp:3785; Igc/shipIGC.cpp:3949; Igc/shipIGC.cpp:3970; Igc/shipIGC.cpp:4101; Igc/shipIGC.cpp:4156; Igc/shipIGC.cpp:4347; Igc/shipIGC.cpp:4378; Igc/shipIGC.h:349; Igc/shipIGC.h:1614; Igc/shipIGC.h:1644; Igc/shipIGC.h:1917; Igc/shipIGC.h:1929; Igc/shipIGC.h:2207; Igc/shipIGC.h:2252; Igc/shipIGC.h:2284; Igc/shipIGC.h:2307; Igc/shipIGC.h:2355; Igc/shipIGC.h:2356; Igc/sideigc.cpp:342; Igc/sideigc.cpp:367; Igc/sideigc.cpp:371; Igc/sideigc.cpp:373; Igc/sideigc.cpp:375; Igc/sideigc.cpp:438; Igc/stationIGC.cpp:45; Igc/stationIGC.cpp:68; Igc/stationIGC.h:314; Igc/treasureIGC.cpp:201; Igc/warpIGC.cpp:29; Igc/warpIGC.h:80; Igc/warpIGC.h:155; Igc/weaponIGC.cpp:177; Igc/weaponIGC.cpp:235 |  |
| 3 | SIM-0232 | callbacks | GetRipcordCluster@4915 | partial | Igc/igc.h:4915 ; Igc/common.cpp:811; Igc/common.cpp:824; Igc/shipIGC.cpp:3741; Igc/shipIGC.cpp:3760; Igc/shipIGC.cpp:4262; Igc/shipIGC.cpp:4281 |  |
| 3 | SIM-0233 | callbacks | Reload@4923 | partial | Igc/igc.h:4923 ; Igc/shipIGC.h:610; Igc/shipIGC.h:640 |  |
| 3 | SIM-0470 | periodic | ship_status_delta_and_turret_updates | partial | FedSrv/FedSrv.CPP:3693 | Excluded: broadcast pipeline |
| 3 | SIM-0471 | periodic | target_hull_shield_fraction_updates | partial | FedSrv/FedSrv.CPP:4079 | Excluded: broadcast pipeline |
| 2 | SIM-0014 | constants | c_fcidRipcordTime | partial | Igc/igc.h:73; FedSrv/FedSrv.CPP:1676 |  |
| 2 | SIM-0074 | updates | mine_expiry_boundary | partial | Igc/mineigc.cpp:150 |  |
| 2 | SIM-0078 | updates | model_spin | partial | Igc/modelIGC.cpp:76 |  |
| 2 | SIM-0108 | updates | miner_drilling_controls | partial | Igc/shipIGC.cpp:2254 |  |
| 2 | SIM-0145 | periodic | chat_budget_recovery | partial | FedSrv/FedSrv.CPP:8282; FedSrv/fsship.h:449-468 |  |
| 2 | SIM-0147 | periodic | network_roll_call_and_disconnect | partial | FedSrv/FedSrv.CPP:7942 |  |
| 1 | SIM-0137 | periodic | lobby_dirty_rate_limit | partial | FedSrv/fsmission.cpp:5606 |  |

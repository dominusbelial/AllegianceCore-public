# P747 — simulation parity wave 14: callbacks and updates

Entry `a00e842fe424787e4bd0a2b7319fc386b41324b9` on `codex/p747`. Changes remain uncommitted in the working tree; no commits, pushes, deployments, or external rig/221/200v200 jobs.

## Result and scope

Ten related construction/asteroid lifecycle rows, weight **34**: **eight implemented**, **one n/a**, **one retained partial**. Completed credit increases by **27**; excluding the dormant native callback removes **3** eligible weight. Partial earns no credit. All **473** simulation records, native fields, weights, and identities are preserved. The interface matrix is unchanged.

The required earlier reports were read before selection: [P690](P690_PARITY_WAVE8.md), [P691](P691_PARITY_WAVE9.md), [P692](P692_PARITY_WAVE10.md), [P708](P708_PARITY_WAVE11.md), [P709](P709_PARITY_WAVE12.md), [P739](P739_PARITY_WAVE_SIM13.md), and [P743](P743_CORRECTNESS_PASS.md). Their completed equipment, economy, wingman, client-text and timer rows are not redone. Following P743's rule, a concrete host binding and tests of production transitions are required; a test recorder or callable helper alone is insufficient. P743's constructor drilling finding remains open under SIM-0109; this task does not claim that control/AI rule.

The small construction draw-loop change consumes the shared simulation phase instead of the old linear reveal. No sound, HUD, interface-matrix, siege/finale, training, or HD row is changed.

## Scorecard before → after

Exact validated output: [before](P747_assets/scorecard-before.txt), [after](P747_assets/scorecard-after.txt). The [field audit](P747_assets/matrix-audit.json) confirms exactly ten changed records and LF endings.

| Simulation area | Weighted before → after | Complete / eligible before → after |
| --- | --- | --- |
| callbacks | 54.55% (108/198) → **63.08% (123/195)** | 36/66 → 41/65 |
| constants | 98.52% (133/135) → unchanged | 38/39 → unchanged |
| periodic | 51.06% (24/47) → unchanged | 7/15 → unchanged |
| sounds | 83.45% (459/550) → unchanged | 170/212 → unchanged |
| updates | 80.52% (281/349) → **83.95% (293/349)** | 69/87 → 72/87 |
| warnings | 100.00% (10/10) → unchanged | 2/2 → unchanged |
| TOTAL | **78.74% (1015/1289) → 81.03% (1042/1286)** | **322/421 → 330/420** |

Unweighted simulation total: **76.48% → 78.57%**. Periodic's eight open rows remain visible in the inventory below; none belongs to this construction bundle.

## Native rules and production evidence

Every native path is relative to **`/home/domingo/Development/Allegiance/src/`** and was read locally. [Source fingerprints](P747_assets/source-manifest.json) record the checkout and final source files. Matrix entries carry exact production and test file:line references.

| Row | Weight | Before → after | Native source | Verified behavior |
| --- | ---: | --- | --- | --- |
| SIM-0044 | 4 | partial → partial | Igc/asteroidigc.cpp:57-63; Igc/asteroidigc.h:58-82,293-304; WinTrek/trekigc.cpp:4057-4064 | Zero-initialized per-side ore memory, current-eye-only refresh, strict >3-second wall-clock inhibit and authoritative bypass. Full client sector-entry prediction/inhibit flow remains absent; no completion credit. |
| SIM-0045 | 4 | partial → implemented | Igc/asteroidigc.h:85-91; Igc/buildingeffectigc.h:312-351 | Asteroid tick calls its attached effect after regeneration/knowledge; safe removal during iteration; exactly one transformation at shrink time, 30 seconds. Constructor parts/ammo/fuel/state are cleared and it is quietly consumed before station creation. |
| SIM-0048 | 4 | partial → implemented | Igc/buildingeffectigc.h:19-22,44-54,160-225 | Shared envelope/grow/opaque/shrink samples drive real sector state and client draw position/radius/alpha. Maximum radius is 1.1 times the larger radius; intensity/alpha branches and exact 40-second expiry are tested separately from the 30-second transformation. |
| SIM-0060 | 4 | missing → implemented | Igc/clusterIGC.cpp:141-155; Igc/clusterIGC.h:493-545; FedSrv/FedSrv.CPP:11045-11053,12382-12387 | Constructed stations retain their source rock and stale-side masks. Only flying unboarded ships apply old-rock scanner/signature/global-attribute/asteroid-occlusion geometry. Discovery retires one side once; station discovery and unseen station destruction also retire that side's ghost. |
| SIM-0168 | 3 | partial → implemented | Igc/clusterIGC.cpp:357; FedSrv/FedSrv.CPP:13694-13697 | Concrete host callback observes each completed sector chunk. Native's empty host behavior remains when no observer subscribes; repeated clocks do not notify. |
| SIM-0169 | 3 | partial → implemented | FedSrv/FedSrv.CPP:11374-11462; _Utility/Utility.h:1341-1358 | Concrete BuildStation selects successor, preserves rock hull fraction and exports its native div-240 BytePercentage, starts shields empty, places native axis/angle-zero pose/name/id/side/type, expires strictly overlapping nonconcentric probes at now+1, records visibility memory, and emits production export/rock receipts even when invoked directly. |
| SIM-0185 | 3 | missing → implemented | Igc/igc.h:4839; FedSrv/FedSrv.CPP:12433-12442 | ID/sector/side callback emits nonexplosive removal only to that side in its mission. Actual sector discovery and TCP decoding are covered. The separate combat object/bool overload now also supports direct world removal and attached-effect cleanup. |
| SIM-0186 | 3 | partial → n/a | Igc/asteroidigc.h:225-230; FedSrv/FedSrv.CPP:12443-12453 | The sole producer is literally inside the native NYI comment, disabled because He3 regenerates. The dormant declaration and host body are not an active parity obligation. The existing ASTEROID_DRAINED registry already records DeadInNative; regeneration remains tested. |
| SIM-0190 | 3 | partial → implemented | Igc/shipIGC.cpp:209-218; Igc/buildingeffectigc.h:112-133,294-297; FedSrv/FedSrv.CPP:12484-12492 | Concrete interruption unlinks once, clears constructor commitment, cancels queued creation, leaves a surviving rock intact, and sends one destruction notice within the owning mission. Builder death/removal/relocation and rock destruction share cleanup. |
| SIM-0229 | 3 | partial → implemented | Igc/shipIGC.cpp:884; Igc/missionigc.cpp:3658-3681; FedSrv/FedSrv.CPP:13733-13767 | Real constructor path calls CreateBuildingEffect; concrete host creates the typed effect with captured nose/start geometry, side/type and informed audience. Creation reaches owner/current eyes/observers of the builder, within that mission. |

The regressions use real loaded core data, concrete ServerHost dispatch through IIgcSite, Mission/Sector updates, and real loopback sessions. They distinguish before/equal/after boundaries, all four phases, floating hull fraction from its lossy wire byte, probe zero/equal/inside/outside overlap, direct and callback-driven completion, interrupted queues, hidden versus informed sides, and actual client removal/export state. A probe-triggered station discovery exercises the station-export retirement path independently of the ship-only old-rock eye path.

Existing managed adapters remain explicit: domain-only NullIgcSite missions retain the same shared transformation fallback; authoritative callbacks/receipts replace native client prediction. The client construction clock keeps its existing receipt-time epoch, and the port's opt-in research pacing still scales server construction. This task does not redesign wire-clock synchronization, late-join phase catch-up, native effect mesh geometry, or constructor drilling controls. The old construction export used a plain byte cast for hull health; native BytePercentage assignment proves it must be quantized to 240 units, so a 75% rock now produces byte 180 over real TCP. Native Rotation is axis plus angle (_Utility/Utility.h:515-574), so the constructed station also exports forward axis (0,0,1), angle zero, rather than quaternion identity; both fields have TCP assertions.

## Verification

Final whole-solution build: **0 warnings / 0 errors**, with `dotnet build AllegianceCore.slnx --nologo -m:1 -v:q -p:UseSharedCompilation=false`. Final Linux x64 self-contained **native AOT publish passed**, with **no IL2026 or IL3050 warnings**. It uses the brief's publish command plus `-p:UseSharedCompilation=false`; no reflection serialization was introduced. [Commands and build/publish fingerprints](P747_assets/build-publish.json).

**Focused qualification:** the serial [87-method / 139-case cohort](P747_assets/cohort.json) passed **261 individual exact-method invocations / 417 case executions**, zero failures/skips. After the final native station-export corrections (div-240 hull percentage and axis/angle-zero rotation), every affected construction method was repeated: [37 methods / 61 cases](P747_assets/construction-cohort.json), **111 invocations / 183 case executions**, zero failures/skips. That repeat includes **all 17 new methods / 37 new cases**, each 3/3 alone against the final code. Total recorded isolated executions: **372 invocations / 600 cases**. [Summary](P747_assets/focused-summary.json), [initial serial ledger](P747_assets/focused.jsonl), [final construction ledger](P747_assets/construction-focused.jsonl), [encoding correction](P747_assets/encoding-delta.json).

The initial serial cohort covers unchanged affected mining/combat/mission-clock behavior; the final construction cohort covers every method affected by the subsequent encoding correction. Longer new TCP tests carry `Tier=Live`. No affected Fast case exceeded ten seconds in the isolated ledgers. Existing real bot purchase → combat → death/pod → repurchase/relaunch and mining sale/payday tests passed their prescribed 3/3 invocations. The suite's Live/Perf tiers and remote rig jobs were not run.

**Parking comparison:** four qualified entry runs passed **4/4**; a fifth passing entry run is excluded for rebuild overlap. Final production 15v15 never-parks runs are separate exact-method invocations, without other task tests overlapping. [Entry receipts](P747_assets/base-parking.json), [final receipts](P747_assets/final-parking.json).

| Build | Run | Outcome | Longest stopped with target (s) | Kills |
| --- | ---: | --- | ---: | ---: |
| entry | 1 | Passed | 2.004 | 10 |
| entry | 2 | Passed | 1.904 | 10 |
| entry | 3 | Passed | 2.007 | 10 |
| entry | 4 | Passed | 2.005 | 7 |
| final | 1 | Passed | 2.005 | 10 |
| final | 2 | Passed | 1.905 | 8 |
| final | 3 | Passed | 2.006 | 7 |
| final | 4 | Passed | 2.216 | 9 |
| final | 5 | Passed | 2.005 | 7 |

Final prescribed parking result: **5/5 passed**, of five required.

**One fast-tier run only**, using `tools/run-tests.sh --tier fast --no-build --results-dir <workspace-scratch>/fast`, with `local.env` loaded. [Fast-tier summary](P747_assets/fast-summary.json), [command receipt](P747_assets/fast-command.json).

| Assembly | Passed | Failed | Skipped |
| --- | ---: | ---: | ---: |
| Allegiance.Lobby.Tests | 19 | 0 | 0 |
| Allegiance.ClientCore.MonoGame.Tests | 2486 | 0 | 3 |
| Allegiance.ClientCore.Tests | 1464 | 0 | 0 |
| Allegiance.Core.Tests | 4857 | 5 | 0 |

Runner overall result: **FAIL**.

The raw fast run is **8,826 passed / 5 failed / 3 skipped**, plus **9/9 infrastructure checks**, with **no timing violations**. The five failures are four existing PlayerBundlePathsTests methods under workspace-based TMPDIR, matching the already documented SC2 ancestor-discovery issue. They are **environment-context failures, not load flakes**. Each method passed **3/3 fresh isolated invocations / 21 total case executions** through a logical `/proc/<parent>/fd/3` alias to the same physical NVMe scratch, preserving the intended outside-repository fixture premise. Assertions and product code were unchanged. The complete fast run was not repeated, and its original FAIL is preserved. [Raw failures](P747_assets/fast-failures.json), [isolated rechecks](P747_assets/path-rechecks.json), [qualification](P747_assets/qualification.json), [stability ledger](../TEST_FLAKES.md).

The three existing skips are the private Meshy-pack prerequisites: `Pack_built_content_directory_exists`, `Pack_catalogue_has_the_full_meshy_set`, and `Pack_catalogue_merge_yields_identical_resolution_to_before`. No new test is skipped.

## Scratch and evidence handling

Both requested external destinations, `/home/domingo/allegiance-scratch/P747/` and `/home/domingo/allegiance-evidence/P747/`, returned **Read-only file system** under this session's sandbox. Temporary storage therefore used the disk-backed workspace directory `.p747-scratch/`, with TMPDIR and NuGet's HTTP cache redirected there. The official runner used its pre-existing zero-byte shared coordination lock `/tmp/allegiance-test-tiers.lock` (created 2026-10-18); no task build, result, cache, or temporary artifact was placed in `/tmp`. Only compact summaries/JSON remain in [P747_assets](P747_assets/), below the **2 MB** task limit. Raw TRX/logs and publish output are removed with the scratch directory after summary extraction; no external raw archive is claimed. Command/path fields in the ledgers identify the original transient artifacts and are not links to retained raw files.

The fifth initial parking pass overlapped the beginning of a Debug rebuild and is excluded from the uncontaminated entry comparison, despite passing. Four earlier entry passes are qualified. Exploratory compile/fixture failures and invalidated overlapping attempts are excluded from certified counts. The repeatable constructor failure exposed missing owner links on late-attached asteroids; it is fixed and has a dedicated regression. Five additional parking runs passed before the final station-pose correction and are superseded by the final-source runs ([additional receipts](P747_assets/additional-parking.json)). No deadlines or assertions were weakened to hide a failure.

## Complete entry inventory, ordered by weight

This is the requested entry inventory for callbacks, updates and periodic, including selected rows' final dispositions. [open-before.json](P747_assets/open-before.json) preserves every original field and the full native citations. An unchanged `partial` or `missing` remains open; `implemented` and `n/a` below are this wave's dispositions. Previously completed wave rows are excluded from this inventory.

| Weight | Row | Area | Entry status | Native item | Final status |
| ---: | --- | --- | --- | --- | --- |
| 5 | SIM-0063 | updates | partial | swept_collision_time_order | partial |
| 4 | SIM-0044 | updates | partial | ore_seen_by_side | partial |
| 4 | SIM-0045 | updates | partial | asteroid_build_completion | implemented |
| 4 | SIM-0048 | updates | partial | build_effect_phase_animation | implemented |
| 4 | SIM-0059 | updates | partial | docked_npc_launch_rules | partial |
| 4 | SIM-0060 | updates | missing | station_consumed_rock_fog | implemented |
| 4 | SIM-0062 | updates | partial | global_preplot_plot_execute_order | partial |
| 4 | SIM-0064 | updates | partial | mine_attrition | partial |
| 4 | SIM-0065 | updates | partial | visibility_every_tenth_pass | partial |
| 4 | SIM-0101 | updates | partial | npc_plan_legality | partial |
| 4 | SIM-0107 | updates | partial | miner_capacity_and_depletion | partial |
| 4 | SIM-0109 | updates | partial | constructor_drill_alignment | partial |
| 4 | SIM-0110 | updates | partial | idle_npc_complaint_backoff | partial |
| 4 | SIM-0119 | updates | partial | goto_completion_and_layer_lay | partial |
| 4 | SIM-0142 | periodic | partial | auto_start_restart_ready_rules | partial |
| 3 | SIM-0067 | updates | partial | explosion_mine_strength | partial |
| 3 | SIM-0089 | updates | missing | shield_five_percent_activation_sound | missing |
| 3 | SIM-0100 | updates | partial | npc_ammo_goal_recovery | partial |
| 3 | SIM-0141 | periodic | partial | server_sim_broadcast_cadence | partial |
| 3 | SIM-0144 | periodic | partial | station_state_docked_broadcast | partial |
| 3 | SIM-0150 | periodic | partial | warp_reentry_timer | partial |
| 3 | SIM-0168 | callbacks | partial | ClusterUpdateEvent@4781 | implemented |
| 3 | SIM-0169 | callbacks | partial | BuildStation@4783 | implemented |
| 3 | SIM-0172 | callbacks | partial | ReceiveChat@4792 | partial |
| 3 | SIM-0173 | callbacks | partial | SendChat@4801 | partial |
| 3 | SIM-0176 | callbacks | partial | PlaySoundEffect@4830 | partial |
| 3 | SIM-0177 | callbacks | partial | PlaySoundEffect@4831 | partial |
| 3 | SIM-0178 | callbacks | partial | PlayNotificationSound@4832 | partial |
| 3 | SIM-0180 | callbacks | partial | PlayVisualEffect@4834 | partial |
| 3 | SIM-0182 | callbacks | partial | TerminateModelEvent@4836 | partial |
| 3 | SIM-0183 | callbacks | partial | TerminateMissionEvent@4837 | partial |
| 3 | SIM-0185 | callbacks | missing | KillAsteroidEvent@4839 | implemented |
| 3 | SIM-0186 | callbacks | partial | DrainAsteroidEvent@4840 | n/a |
| 3 | SIM-0190 | callbacks | partial | KillBuildingEffectEvent@4844 | implemented |
| 3 | SIM-0195 | callbacks | partial | PostNotificationText@4849 | partial |
| 3 | SIM-0197 | callbacks | partial | TargetKilled@4851 | partial |
| 3 | SIM-0199 | callbacks | partial | DamageShipEvent@4853 | partial |
| 3 | SIM-0205 | callbacks | partial | LandOnCarrierEvent@4859 | partial |
| 3 | SIM-0206 | callbacks | partial | RescueShipEvent@4860 | partial |
| 3 | SIM-0208 | callbacks | partial | HitWarpEvent@4862 | partial |
| 3 | SIM-0209 | callbacks | partial | HitTreasureEvent@4863 | partial |
| 3 | SIM-0212 | callbacks | partial | ContinueRipcord@4866 | partial |
| 3 | SIM-0213 | callbacks | partial | UseRipcord@4867 | partial |
| 3 | SIM-0214 | callbacks | partial | FireMissile@4868 | partial |
| 3 | SIM-0219 | callbacks | partial | ChangeStation@4876 | partial |
| 3 | SIM-0220 | callbacks | partial | ChangeCluster@4879 | partial |
| 3 | SIM-0221 | callbacks | partial | CommandChangedEvent@4882 | partial |
| 3 | SIM-0229 | callbacks | partial | CreateBuildingEffect@4901 | implemented |
| 3 | SIM-0231 | callbacks | partial | GetCluster@4909 | partial |
| 3 | SIM-0232 | callbacks | partial | GetRipcordCluster@4915 | partial |
| 3 | SIM-0233 | callbacks | partial | Reload@4923 | partial |
| 3 | SIM-0470 | periodic | partial | ship_status_delta_and_turret_updates | partial |
| 3 | SIM-0471 | periodic | partial | target_hull_shield_fraction_updates | partial |
| 2 | SIM-0078 | updates | partial | model_spin | partial |
| 2 | SIM-0145 | periodic | partial | chat_budget_recovery | partial |
| 2 | SIM-0147 | periodic | partial | network_roll_call_and_disconnect | partial |

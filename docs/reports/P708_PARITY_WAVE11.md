# P708 — parity wave 11: ship simulation update controls

Branch `codex/p708`, entry revision `5556eb0f459095fe18e6fcf3b919c1786331c877`. Changes are left in the working tree. No commits, pushes, deployments, or rig/221/200v200 runs.

**Validation hold:** the required zero-failure parking gate has failed. The implementation and deterministic/focused tests are complete; this wave must not be described as regression-gate clean. Exact final outcomes and baseline comparison are recorded below. P707 dispenser/readiness code is unchanged.

## Bundle and scope

Eight previously partial/missing **updates** rows, total weight **27**: SIM-0104, SIM-0108, SIM-0112–0115, SIM-0117, SIM-0118. These share `CshipIGC::PlotShipMove` and its movement/control state: decide whether to continue a goal, retain a temporarily unseen target, pursue through a warp, interrupt a goto, maneuver and select weapons, or remain drilling. Implementing the timers together matters because native defend and goto share a check cooldown and plan changes reset both that cooldown and unseen-target memory. `Ship.NpcUpdates.cs` holds the new rules; the existing ship update calls them before movement and the existing weapon/magazine update.

Only these eight matrix rows change. The sector update structure, swept collision scheduler, broadcast pipeline/interest tiers, security, pods/docking implementation, showcase tech mode, research/loadouts, rendering, Android and installer are unchanged. P707's dispenser/readiness implementation is untouched. Callback and sound matrix work stays with P709/P710; update decisions may invoke the existing team-chat seam, without changing its delivery or sound bindings. The [scope audit](P708_assets/scope-audit.json) checks unchanged bodies for five equipment methods, all 30 MCP sources, the equipment partial, and the sector scheduler, plus the exact eight changed simulation rows and unchanged Git HEAD.

## Native rules and evidence

All native citations below are relative to **`~/Development/Allegiance/src/`**, read from the local native source. The source hashes and entry revision are in [source-manifest.json](P708_assets/source-manifest.json).

| Row | Entry → final | Native file:line | Implemented rule and regression evidence |
| --- | --- | --- | --- |
| SIM-0104 | partial → implemented | `Igc/shipIGC.cpp:2065–2080,4154–4189,4200–4228`; `Igc/common.cpp:1648–1751`; `Igc/shipIGC.h:213` | Five-second unsafe retry; native friendly/allied drone ID, mining/ripcord and rescue/carrier right of way; ship/rock/station/probe/mine drift scan; successful request marks drone-rip and stops mining; cancellation clears indicator. `Safe_ripcord_retry_waits_five_seconds_sets_indicator_stops_mining_and_clears_on_cancel`, `Ripcord_safety_respects_allied_drone_id_and_mining_right_of_way`, both `NpcRipcordTravelLiveTests`. |
| SIM-0108 | partial → implemented | `Igc/shipIGC.cpp:2249–2260` | Ready NPC miner faces its rock with turn skill 1, roll 1, throttle −1 and cleared movement/fire controls, before travel Dodge can displace it. `Mining_controls_face_roll_kill_throttle_and_clear_all_motion_and_fire_inputs`; existing ready/contended/working miner and server mining-income tests. |
| SIM-0112 | partial → implemented | `Igc/shipIGC.cpp:2030–2056,2370–2407,2680–2705` | 200 complete intervening passes between defend checks, rearmed on a new plan; strict 1500 acquisition radius, visible combat pilots, existing 2200/3500 return logic and closer-threat selection. Team-chat seam on acquire/switch/return. Two new defend methods and both existing live defend tests. |
| SIM-0113 | missing → implemented | `Igc/shipIGC.cpp:2410–2460`; `Igc/shipIGC.h:380–389,748–787` | Native side knowledge OR own signature/GA/LOS scanner; first unseen pass retains .75 skill, later guesses use .5 through three seconds; strict >3 coasts with existing controls through five seconds; strict >5 investigates accepted target position unless another visible enemy is within 1.5 times its distance, otherwise drops the order. Temporary plans restore the interrupted goto; replacement defend/station orders clear the superseded accepted attack. New unseen-boundary, investigation, plan-loss/reset tests. |
| SIM-0114 | partial → implemented | `Igc/shipIGC.cpp:2483–2576,2589–2604`; `Igc/common.cpp:1580–1644` | Native .45/.90/1.5 lifespan bands, manual forward/back thrust alongside base throttle, speed matching, boost hysteresis, long-range boost/10-second retarget, 100-pass Dodge movement retention, and aft-target strafe. Ordered ship-before-probe priority. New range/boundary/hysteresis/dodge/retarget tests; existing live test now checks manual inputs **and resulting opposite velocity signs**. |
| SIM-0115 | missing → implemented | `Igc/shipIGC.cpp:2578–2593,2673–2677`; `Igc/magazineigc.cpp:103–167`; `Igc/shipIGC.h:743–746` | Command target is selected before the regular lock update. NPC missile input requires UseMissiles, full lock, front hemisphere and strict transverse velocity <20+MaxLock×30. Existing magazine code owns load/mount/ammo gates and firing; repair plotting releases the prior attack missile/boost/coast inputs. New strict selection cases plus production NPC tick proving lock → real missile → exactly one round used → reload respected. |
| SIM-0117 | partial → implemented | `Igc/shipIGC.cpp:2707–2745` | Accepted attack crossing sectors becomes a temporary cluster goto only if the target's nearest destination mouth leads back to the pursuer, distance is strictly <800 and scanner is >100; otherwise discard the appropriate attack order. Accepted attack survives the temporary search route. Strict mouth/scanner boundary and visible reacquisition tests. |
| SIM-0118 | partial → implemented | `Igc/shipIGC.cpp:2000–2027,2747–2790`; `Igc/igc.h:784–792` | Goto checks every 100 intervening passes, reacquires visible accepted attacks, and applies ten-second InRange/TempSector aggression using the native distance/relative-speed priorities. Ships precede probes; temporary aggression becomes an accepted attack and consumes its flag; ordinary interruption resumes its goto on target loss. New cadence, reacquisition, ordered-probe and route-restoration tests. |

New test names without a class qualifier are in `P708ShipUpdateTests` (20 methods, 42 cases). Relevant existing suites are included in the isolated verification list.

## Native intent and bot impact

Each selected **rule is design**, including the pass-count cooldowns: this wave removes the old claim that evaluating defend every tick was a harmless optimization. No selected rule is merely an era constraint.

Two native implementation defects are deliberately not copied as failures: `shipIGC.cpp:2066` checks a future retry deadline with `>` against the current start time, despite the adjacent assignment of a five-second delay at `:2078`. The port keeps the intended five-second deferred retry and tests the exact deadline. `shipIGC.cpp:4216` divides by zero at zero drag; the port uses its analytic limit `distance = initialSpeed × ripcordTime`. These are **native bugs**, not substitutions of loaded gameplay constants.

- **SIM-0104:** MCP/client bots retain their own navigation and wire controls; server NPC requests use the existing ripcord seam, now with native safety and mining/indicator state.
- **SIM-0108:** Only arrived server NPC miners receive drilling controls; player/client-bot controls and geometric mining remain unchanged, with a dedicated player-input preservation test.
- **SIM-0112:** The cooldown changes server NPC defender decisions; client-bot defense logic is untouched and the showcase parking gate checks continued flight.
- **SIM-0113:** Sight-loss memory applies to server NPC plotting; client-bot targeting and input ownership stay in ClientCore.
- **SIM-0114:** Server NPC thrust/boost now follows native range bands; MCP bots continue driving their existing wire controls. Local live range checks prove actual braking and acceleration.
- **SIM-0115:** Server NPCs can now use their existing magazines as designed; client-bot missile choices, mounting/readiness, energy and ammo implementation stay unchanged. Production tick coverage verifies ordinary lock, consumption and reload behavior.
- **SIM-0117:** Accepted server NPC attacks can follow the native mouth heuristic through the existing warp planner; MCP bot navigation and sector collision/update scheduling are unchanged.
- **SIM-0118:** Opportunism is gated to server NPC wingman plotting; client-bot order/target logic remains unchanged, and interrupted NPC goto routes are explicitly restored.

## Scorecard

`python3 scripts/parity-scorecard.py` validates **438 parity rows and 473 simulation rows**, before and after. Raw outputs: [before](P708_assets/scorecard-before.txt), [after](P708_assets/scorecard-after.txt).

| Matrix/area | Weighted before → after | Complete/eligible before → after |
| --- | --- | --- |
| Simulation updates | **71.35% (249/349) → 79.08% (276/349)** | **59/87 (67.82%) → 67/87 (77.01%)** |
| Simulation total | **47.94% (639/1333) → 49.96% (666/1333)** | **177/437 (40.50%) → 185/437 (42.33%)** |
| Main parity total | 88.39% (1310/1482) → unchanged | 350/410 (85.37%) → unchanged |

Updates has **20 open rows, weight 73**, after this wave. Other simulation areas are unchanged: callbacks 45.45%, constants 95.56%, periodic 25.53%, sounds 25.08%, warnings 100% weighted.

## Verification

**Affected-method isolation: 79 methods × three exact-method invocations = 237/237 passing, 420 cases passed, zero skips.** The main pass covers 75 methods (225 invocations / 390 cases), including all 20 new methods and the real local mining, MCP relaunch/fight, and pod survival/rescue proofs. A final consumer review then corrected repair plotting to replace all non-selection inputs, releasing the new attack missile/boost/coast state exactly as `Igc/shipIGC.cpp:2673–2677` does. Its four affected repair methods passed 12 additional isolated invocations / 30 cases on the final build; see [repair-verification.json](P708_assets/repair-verification.json). Earlier implementation paths were unchanged by that consumer correction. The final Core suite is rerun after the correction; ClientCore and MonoGame use the final build. An additional preliminary 222 isolated invocations and ten parking runs passed and remain separately labeled in the ledger. The machine-readable [invocation ledger](P708_assets/verification.json), [affected method list](P708_assets/affected-methods-complete.json), compressed TRX files and per-invocation logs are retained under [P708_assets](P708_assets/). [verify.py](P708_assets/verify.py) reproduces build, affected-method isolation, parking gate, full suites and suite-failure isolation from the repository root.

The solution builds with zero warnings/errors using .NET 10. NuGet's HTTP cache is redirected to `/tmp/p708-nuget-http` because its default home-directory cache is read-only in this lane. Artwork configuration is loaded from the existing `local.env` without recording its contents. The final unfiltered suites enable the existing local P542 projectile proof and P660 15v15 showcase opt-ins. Rig/221/200v200 proofs are not invoked.

The entry-tree parking baseline passed: local 15v15, 25/30 reached, 3,770 shots, 2,083 hits, **13 kills**, longest speed<1 with a target **2.706 seconds**. This is baseline evidence, separate from the required ten final runs.

### Full suites and isolated recovery

| Suite | Passed | Failed | Skipped | Evidence |
| --- | ---: | ---: | ---: | --- |
| Core, first pass | 4,729 | 0 | 0 | [TRX](P708_assets/suite-Core.trx.gz) |
| Core, final build | 4,727 | 2 | 0 | [TRX](P708_assets/suite-Core-final.trx.gz) |
| ClientCore, final build | 1,073 | 0 | 0 | [TRX](P708_assets/suite-ClientCore.trx.gz) |
| MonoGame, final build | 2,234 | 0 | 3 | [TRX](P708_assets/suite-ClientCore.MonoGame.trx.gz) |

The final Core failures were `P699bPodLoadTests.Hundred_pods_per_side_return_from_Alderan_with_relaunch_traffic` (one oxygen expiration among 200 pods) and `MineCrossSideVisibilityLiveTests.Enemy_ship_spots_a_laid_mine_once_in_range_and_learns_its_destruction` (mine EXPORT receipt timeout). **Each passed 3/3 subsequent exact-method isolated runs**, six passing cases total, recorded as load flakes under the brief's rule in [TEST_FLAKES.md](../TEST_FLAKES.md). `suite-retry-000-{1,2,3}` are the mine-visibility reruns; `suite-retry-001-{1,2,3}` are the pod-load reruns. Neither failing fixture, its deadlines/assertions, the pod/docking code, nor any quarantine setting was changed.

MonoGame's only skips were the existing optional `MeshyPackTests.Pack_built_content_directory_exists`, `Pack_catalogue_has_the_full_meshy_set`, and `Pack_catalogue_merge_yields_identical_resolution_to_before`. The 605-second human-flight soak ran as part of the suite. All changed/new rules and affected-method isolation ran without skips. A broad clean suite pass is not used as a substitute for the required parking gate.

### Required parking gate — failed

The ten-run attempt finished **7 passed / 3 failed / 0 skipped**. Runs **02, 03 and 10** failed the unchanged `Parked >5s` assertion, with a live combat target and zero velocity. Two failures were heavy interceptors, one an advanced scout, all in target sector 1052 with `WorldReady=true`. The immediate next three isolated runs after the first failure burst (04–06) passed, but that recovery does **not** meet the required ten-run zero-failure gate. This is an integration hold, not a passing regression certification.

| Failed attempt | Hull / role | Ship → target | Evidence |
| --- | --- | --- | --- |
| 02 | Hvy Interceptor / Interceptor | 8106 → 8116 | [TRX](P708_assets/parking-final-02.trx.gz) |
| 03 | Hvy Interceptor / Interceptor | 8104 → 8121 | [TRX](P708_assets/parking-final-03.trx.gz) |
| 10 | Adv Scout / Scout | 8109 → 8118 | [TRX](P708_assets/parking-final-10.trx.gz) |

The seven completed passing battles each reached 25/30 objectives and recorded **12–16 kills** (98 total), real shots and hits; maximum observed stopped-with-target duration among those passes was **4.909 seconds**. Actual failure traces remain in the ledger and compressed TRX files. The final repair consumer input reset was verified separately after this gate attempt; no passing final-source parking-gate claim is made. No test assertions/deadlines or dispenser/readiness code were changed to get around parking.

### Entry-revision comparison

The unchanged entry revision `5556eb0f459095fe18e6fcf3b919c1786331c877` was extracted with `git archive` into `/tmp/p708-entry-baseline` and built separately. [baseline-manifest.json](P708_assets/baseline-manifest.json) confirms the archived ship/equipment/command sources match that revision; [baseline-build.log](P708_assets/baseline-build.log) records a clean build. The same exact P700 parking method passed **3/3 isolated entry-revision runs** (`entry-baseline-{1,2,3}` in the invocation ledger). The earlier entry-tree baseline also passed once.

This comparison did **not** reproduce parking on the entry revision. It does not establish a cause for the intermittent failures in the ten-run attempt, so this report does not attribute them to P707 or claim that P708 has proved the gate clean. The P707 readiness/dispenser code remains unchanged. Reproduce the comparison with [prepare-baseline.py](P708_assets/prepare-baseline.py) and `python3 docs/reports/P708_assets/verify.py baseline`.

### Final-build parking recovery and disposition

After the broad suites, both suite-failure isolations, and the entry-revision comparison, the unchanged P700 method passed **3/3 exact-method isolated runs on P708's final build**: [retry 1](P708_assets/parking-retry-1.trx.gz), [retry 2](P708_assets/parking-retry-2.trx.gz), [retry 3](P708_assets/parking-retry-3.trx.gz). These are recovery observations, not a replacement ten-run gate. The earlier 7/10 attempt preceded the final repair-input reset, so this report makes neither a causal parking-fix claim nor a passing final-source ten-run certification.

**Disposition: implementation complete, integration held by the unmet parking gate.** Source changes, tests, matrix evidence and this report remain uncommitted. The separate P707 dispenser/readiness implementation, pod/docking code, sector scheduler and MCP controllers were left untouched. A clean required ten-run parking gate is still needed before treating this wave as regression-cleared.

[final-verification.json](P708_assets/final-verification.json) checks all 79 affected methods have three nonempty passing isolated invocations (237 invocations / 420 cases), all full suites ran, both suite failures recovered 3/3, all ten gate attempts and both three-run comparisons are retained, final source hashes match, and Git HEAD remains the entry revision. The strict gate's `clean` field is **false**. [summary.json](P708_assets/summary.json) includes the passing battle metrics and the three actual optional skip outcomes.

During development, the old P171 live fixture expected the port's throttle substitute. It now asserts native forward/back **buttons plus actual signed velocity**, retaining its real server/loopback setup. P676's allied-acquisition fixture now explicitly makes its enemy visible for native `CanSee`; the alliance rejection and enemy-identity assertions are unchanged. These corrections are not flakes. No parking assertions, network deadlines, readiness rules or suite quarantine settings were relaxed.

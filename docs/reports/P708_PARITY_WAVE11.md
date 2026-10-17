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

## P734 merge

Merged P708 wave 11 onto master `d0acd9ecacdd06773839b2807ad0a410a31b79d1`
(P708 input `a6836a24`). The working tree arrived with a merge in progress.
The `Ship.cs` conflict keeps `UpdateWingmanCombat` delegating to
`PlotWingmanCombat` in `Ship.NpcUpdates.cs`, which supplies the eight P708
rules below. The obsolete inline combat body would lose the new bands,
missile controls, sight memory and pursuit. `docs/TEST_FLAKES.md` retains
both sides' observations. The index remains for the dispatcher to resolve;
no staging, commits, pushes or merge completion were performed.

### Preserved rules and later master work

Native citations are relative to `~/Development/Allegiance/src/` and were
checked against the local source; [source hashes](P734_assets/native-manifest.json)
and [reviewed excerpts](P734_assets/native-reviewed.txt) accompany the merge.

| P708 row | Rule retained | Native file:line |
| --- | --- | --- |
| SIM-0104 | Five-second unsafe ripcord retry, right of way, drift hazards, mining cancellation and drone-rip indicator | `Igc/shipIGC.cpp:2065-2080,4154-4228`; `Igc/common.cpp:1648-1751`; `Igc/shipIGC.h:213` |
| SIM-0108 | Arrived NPC miner faces the rock, rolls at 1, kills throttle and clears motion/fire inputs before travel Dodge | `Igc/shipIGC.cpp:2249-2260` |
| SIM-0112 | Visible combat-pilot defend acquisition, strict 1500 radius, existing return/switch distances, shared 200-pass check and team chat | `Igc/shipIGC.cpp:2030-2056,2370-2407,2680-2705`; `Igc/sounds.h:390` |
| SIM-0113 | Side knowledge or own scanner, reduced unseen-target lead through three seconds, coast through five, investigate/drop and accepted-order restoration | `Igc/shipIGC.cpp:2410-2460`; `Igc/shipIGC.h:380-389,748-787` |
| SIM-0114 | Strict .45/.90/1.5 lifespan bands, manual thrust, speed matching, boost hysteresis, Dodge retention, ten-second retarget and ordered ship/probe priority | `Igc/shipIGC.cpp:2003-2027,2483-2576,2589-2604`; `Igc/common.cpp:1580-1644` |
| SIM-0115 | Command-target lock selection, UseMissiles/full-lock/front-hemisphere/strict transverse-speed gate; existing magazine owns ammo/reload; repair clears stale nonselection inputs | `Igc/shipIGC.cpp:2578-2593,2673-2677`; `Igc/magazineigc.cpp:103-167` |
| SIM-0117 | Accepted attack follows a destination mouth leading back, strictly within 800 and scanner above 100 | `Igc/shipIGC.cpp:2707-2745` |
| SIM-0118 | Goto reacquisition and ten-second aggression on the shared 100-pass cadence; temporary aggression consumes its flag and ordinary interruption restores goto | `Igc/shipIGC.cpp:2003-2027,2747-2790`; `Igc/igc.h:784-792` |

The documented five-second deadline correction and analytic zero-drag limit
remain as described in P708's native-bug analysis above.

The [master preservation audit](P734_assets/master-preservation.json) compares
existing `.cs`/`.csproj` files directly with master's tree: **1,814 identical**;
only the five existing P708 source/test files differ. P708 also adds its new
partial and test fixture. This retains master's P707 held escape and owning-peer
speed clock, P699c docking, later sound cues, and P712 research/bucket reconciliation.
The [Ship.cs comparison](P734_assets/Ship-vs-master.diff) records all merged
ship changes. The P707 harness escape remains harness policy; native close-band
stopping and directional control integration remain `Igc/shipIGC.cpp:2492-2511,2965-3005`,
and velocity quantization remains `Igc/igc.h:2282-2310,2444-2454`.
Docking retains the native bay/collision/entry rules documented in P699
(`Igc/shipIGC.cpp:727-861,4024-4036`; `Igc/common.cpp:1287-1540`).
P712's bucket timing, lifecycle and tech reconciliation remain unchanged
(`Igc/bucketigc.h:40-73,180-299`; `Igc/sideigc.h:160-204,619-670`).
P709 and P733 implementation work is outside this merge.

The [matrix audit](P734_assets/matrix-audit.json) checks all **473 rows by ID**
against master: exactly SIM-0104, SIM-0108, SIM-0112–0115, SIM-0117 and SIM-0118
change, all to **implemented**; every other master row is identical. The file
contains **LF only**, with no carriage returns. Shifted port source-line citations
within the P708 rows were refreshed for the merged file locations. [Scorecard](P734_assets/scorecard.txt).

### Semantic docking merge correction

Keeping the P708 wrapper alone exposed a semantic conflict that Git did not
mark: its new `PlotWingmanCombat` cleared movement buttons before checking
whether an attack target exists. Master's P699c inline method intentionally
returned first when idle, preserving the bay approach's lateral thrust.
The unchanged existing
`P699cStationCorpusTests.Npc_docking_thrusters_survive_idle_combat_and_clear_on_next_order`
failed at its first nonzero-thrust assertion in the diagnostic Core check.

The merged plot now clears fire inputs first, returns when idle, and clears
movement/afterburner inputs only for active combat. Thus native docking/travel
controls remain owned by autopilot (`Igc/common.cpp:1287-1540`), while P708's
active-target range/Dodge/fire decisions remain intact
(`Igc/shipIGC.cpp:2483-2604`). No test assertion changed. The docking method
passed three fresh exact-method runs immediately after the fix; the definitive
cohort additionally includes every method in its eight-method P699c fixture.
A new whole-solution build after this correction passed with 0 warnings/errors
([final build](P734_assets/build-final.log)).

### P734 gate evidence

The definitive post-correction ledger is
[verification-final.json](P734_assets/verification-final.json); commands and
all diagnostic dispositions are documented in [README](P734_assets/README.md).
The product build is **0 warnings / 0 errors** on .NET 10.

**Isolation: 91 methods × 3 exact-method runs = 273/273 passing invocations,
537 case passes, zero skips.** This includes P708's full 79-method consumer
cohort, the four P707 parking methods and all eight P699c station-corpus
methods. The docking assertion additionally passed its three immediate
post-fix runs in [verification-docking.json](P734_assets/verification-docking.json).

**Final-source never-parks gate: 10/10 passing, zero failures/skips.** The
largest stopped-with-target interval was **2.205 seconds**, below the unchanged
five-second limit. Real gunfire, damage and objective arrival were checked in
every run. The ten earlier pre-correction passes are separately labeled
`parking-initial-*`; they do not replace this final-source gate. No base-rate
comparison was triggered because neither ten-run batch had a parking failure.

**Local two-match showcase: passed, two natural Stage Over transitions.**
Because P733 is absent on master, the brief's **unlocked** fallback was used,
with **civs 25 vs 3**. The final proof uses the existing SingleRing generator
(four sectors), the unchanged 45v45 mixed-fleet preset, staging and automatic
rotation. Per-IP admission is configured to 128 for the 90 local bots. The
existing finale settings use both sides, five-minute maturity, 50% relaunch
share, five recent kills and the existing escort allocation at share 1/cap 12.
Both matches ended before that late finale triggered. No poses, damage,
mission stages or game-over calls were supplied by the driver.

| Completed match | Stage Over wall time | Simulation clock | Host outcome |
| --- | ---: | ---: | --- |
| 1 | 107.144 s | 188.300 s | Alpha won by outlasting all other sides |
| 2 | 198.702 s | 366.200 s | Alpha won by outlasting all other sides |

The second mission was observed Started across six samples before ending;
automatic rotation then built a third NotStarted mission. **Zero simulation-stage
failures** occurred. Evidence: [timeline](P734_assets/showcase-ring-final.json),
[driver](P734_assets/showcase-ring/Program.cs), and
[console](P734_assets/showcase-ring-run.log.gz).

Earlier unstaged diagnostics are retained. Two small 15v15 scenes did not
complete before cancellation; a scale-3 setup first encountered the default
64-connections-per-IP ceiling and was corrected in the local driver. A fully
admitted 45v45 m2xspiderw attempt then reached its 15-minute bound without Stage
Over (601 kills in its last sample, zero simulation-stage failures); its
[status](P734_assets/showcase.json.gz) and
[console](P734_assets/final-showcase-run.log.gz) remain. An unstaged SingleRing
attempt also cycled bombers through the rendezvous without a base kill before
cancellation. The final staged proof above fulfills the two-match gate; these
failed/incomplete scene attempts are not reclassified as successful.

Two controllers unexpectedly continued after interruption and launched
overlapping **Core-only diagnostic attempts**, before the docking correction.
They did not proceed to ClientCore/MonoGame/Lobby: follow-on launches were
blocked. Their complete TRX captures remain: **4,804 pass / 3 fail** in
[diagnostic 1](P734_assets/diagnostic-Core-2.trx.gz) and **4,806 / 1** in
[diagnostic 2](P734_assets/diagnostic-Core-last.trx.gz), zero skips. Each caught
the real docking merge regression, which was fixed rather than called a flake.
The first also observed a raw-peer join timeout and a missing elimination
fixture ship. These diagnostics are additional to the two complete final
sweeps; they are not presented as final-source certification. One partial
artifact capture and the empty P707 selection are explicitly excluded from
counts. All controllers and diagnostic servers were stopped before the final
parking gate. The successful compact-map proof ran alongside the serial
suite projects; all isolated failure classification follows the sweeps.

### P734 suite results

**Exactly two complete final sweeps:** **16,520 passes / 0 failures / 6 existing optional skips**, covering all four solution test projects without a filter.

| Sweep | Project | Passed | Failed | Skipped | Wall seconds | Evidence |
| --- | --- | ---: | ---: | ---: | ---: | --- |
| 1 | Core | 4,807 | 0 | 0 | 957.894 | [TRX](P734_assets/suite-1-Core.trx.gz) |
| 1 | ClientCore | 1,174 | 0 | 0 | 2109.249 | [TRX](P734_assets/suite-1-ClientCore.trx.gz) |
| 1 | ClientCore.MonoGame | 2,260 | 0 | 3 | 958.511 | [TRX](P734_assets/completion-MonoGame-1.trx.gz) |
| 1 | Lobby | 19 | 0 | 0 | 0.946 | [TRX](P734_assets/suite-1-Lobby.trx.gz) |
| 2 | Core | 4,807 | 0 | 0 | 914.680 | [TRX](P734_assets/suite-2-Core.trx.gz) |
| 2 | ClientCore | 1,174 | 0 | 0 | 2057.668 | [TRX](P734_assets/suite-2-ClientCore.trx.gz) |
| 2 | ClientCore.MonoGame | 2,260 | 0 | 3 | 958.975 | [TRX](P734_assets/suite-2-ClientCore.MonoGame.trx.gz) |
| 2 | Lobby | 19 | 0 | 0 | 1.031 | [TRX](P734_assets/suite-2-Lobby.trx.gz) |

The only skips in each MonoGame sweep were the pre-existing optional private
Meshy-pack checks: `Pack_built_content_directory_exists`,
`Pack_catalogue_has_the_full_meshy_set`, and
`Pack_catalogue_merge_yields_identical_resolution_to_before`. The pack is not
configured. No selected new/affected method or required local scenario was
skipped. Both local P542 and P660 opt-ins were enabled. The real human-flight
soak passed in both completed MonoGame runs: `suite-1-ClientCore.MonoGame` 00:10:05.2498338, `suite-2-ClientCore.MonoGame` 00:10:05.2171119.

The initial MonoGame wrapper invocation exited 0 after 189.749 seconds
without a completed TRX or final summary. It was **not** counted as a pass or a
completed sweep. Its [incomplete record](P734_assets/verification-incomplete.json)
and [console](P734_assets/suite-1-ClientCore.MonoGame.log) remain. The same
already-built assembly was then run unfiltered through `dotnet vstest`, yielding
2,260 passes / 0 failures / 3 optional skips. That complete result is the first
MonoGame row above, with its original raw evidence retained under
`completion-MonoGame-1`. It ran alongside the second Core run; other suite
projects followed the serial runner. The early exit's cause is not established.
The ledger reconciliation preserves the incomplete attempt rather than
inferring test success from an exit code. No additional complete suite sweep
was run.

After the two complete sweeps, both unrelated methods that failed in the
pre-correction Core diagnostics passed **3/3 fresh exact-method isolated runs**:

| Method | Diagnostic observation | Isolated evidence |
| --- | --- | --- |
| `GameOverPlayersWireTests.Win_by_elimination_broadcasts_game_over_players_to_surviving_peer` | No matching fixture ship | `suite-retry-000-{1,2,3}`: 3/3 pass |
| `Transport.TcpTransportSendWorkerPoolTests.Pool_worker_and_reader_counts_stay_fixed_across_join_leave_churn` | No raw-peer join event before the original timeout | `suite-retry-001-{1,2,3}`: 3/3 pass |

These satisfy the brief's load/timing-flake classification rule. Their
assertions, deadlines and quarantine settings were unchanged; the isolated
passes do not establish an underlying cause. See [TEST_FLAKES.md](../TEST_FLAKES.md).
The docking assertion was a real merge regression and was fixed before the
final build, isolated cohort and both completed sweeps; it is not a flake.

**Disposition: merge resolution and required gates complete.** P708's native
update rules and later master behavior are retained, including the corrected
idle docking-thrust ownership. The definitive
[summary](P734_assets/summary.json) checks the exact triplets, ten clean parking
runs, two natural Stage Over events, two complete final suite sweeps, six
passing failure-isolation invocations, nonempty selections, source hashes,
removed conflict markers, and unchanged merge metadata. Its `clean` field is
**true**. Historical failed/incomplete attempts remain separately documented.

Files remain in the working tree. HEAD, index, MERGE_HEAD and ORIG_HEAD remain
unchanged; the dispatcher must stage the resolved conflict contents. No
staging, commits, pushes, deployments, rig/221 or 200v200 runs were performed.

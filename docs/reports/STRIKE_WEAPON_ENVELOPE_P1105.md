# P1105 — Strike weapon envelope

**Keep `finale_weapon_envelope` off by default.** In five matched pairs, every treatment pilot released on its station and median go-to-first-hit fell from 33.50 to 2.88 seconds. Credited station kills fell from seven to five, pre-deadline wins from two to one, and armed lives lost before firing rose from 13 to 27. [Match-by-match results](#matched-results)

## Brief items

Elapsed times are wall time. The local matches, implementation and gates overlap.

| Item | Status | Evidence | Elapsed |
|---|---|---|---|
| 1. Measure first | done | Instrumented the unchanged decision path first. Paced Brawl 959 released nine pilots on ship targets, resulting in eight shooters and seven effective hits. | 9 min to first completed baseline |
| 2. Grant and cloak lane | done | Three weapon classes tested against granted parts. The cloak test checks the lateral bit in the control state. | 20 min including native burst-cycle verification |
| 3. Ready subset and accepted fire | done | Owner feedback during hold, timeout boundary, release recheck and independent accepted-round accounting passed. | 21 min including owner-stream tests |
| 4. Default off | done | Omitted and explicit false preserve the old membership, five-second lead and release behavior, including with a ship lock. | 5 min, overlapping item 3 |
| 5. Matched local games | done | Five matched pairs, ten completed games. Two Brawl seeds and three four-sector SingleRing seeds. All credited kills reconciled to the final pilot scores. | 40 min for final paired series |
| 6. Tests and breaks | done | Eleven Fast cases passed, including an encoded cloak-lane control check. Eleven deliberate source breaks failed assertions. Every unrelated failure or timing overrun in the broad sweep has three isolated reruns below. | 29 min including sweep and triage |
| 7. Report and recommendation | done | Before/after tables, individual release receipts, gate triage, risks and recommendation are recorded here. | Maintained throughout the 73 min run, including the final Live repeat |

## Paced Brawl before the change

The original decision path at `22a595651` held visible pilots at 3,500 units and cloak pilots at 5,000. Assembly admitted another 3,600 units. The first Brawl 959 wave committed nine pilots, including two assigned cloak lanes. All nine had a real SRM Anti-Base 1 magazine (part 157). Its native centre-distance lock cutoff is 1,000 units. A stationary launch travels about 926.9 units before expiry because the motor lights after 0.25 seconds. The eight stealth bombers carried two rounds each and the standard bomber carried four. All nine owner reports named opposing ships at individual release.

| Pilot | Distance to station | Owner target | Lock |
|---:|---:|---|---:|
| 8104 | 3,024.2 | Ship 8103 | 1 |
| 8106 | 2,977.8 | Ship 8115 | 1 |
| 8108 | 3,818.7 | Ship 8119 | 0 |
| 8110 | 3,363.2 | Ship 8103 | 1 |
| 8112 | 5,379.6 | Ship 8115 | 0 |
| 8114 | 2,769.5 | Ship 8100 | 1 |
| 8116 | 3,558.2 | Ship 8100 | 1 |
| 8118 | 3,971.1 | Ship 8100 | 1 |
| 8120 | 2,764.5 | Ship 8115 | 1 |

Go was approximately 379.95 on the simulation clock, with up to one 250 ms observer interval of uncertainty. The first seven accepted anti-base missiles were created at 379.95. The first effective station hit came at 408.90, about 29.0 seconds after go. Eight distinct pilots fired 15 accepted anti-base rounds at the station. Seven distinct pilots landed seven effective hits. The lethal hit came at 419.40 and Alpha earned one credited base kill before the 664.8 finale deadline. Local receipts are in `.scratch/matches/Brawl-959-control*`.

## Design

The scenario option is `finale_weapon_envelope` in the MCP strike options. Its default is false. The coordinator still asks the existing mass/short-wave planner whether it has a sufficient arrival. With the option on, it checks the player’s observed hull, sector, station, actual granted mount, available rounds and target-specific damage. It chooses a usable strike magazine or the longest usable fixed gun. Cargo and turret guns cannot satisfy a pilot’s readiness check. [Weapon and lane calculations](../../tools/allegiance-mcp/StrikeWeaponEnvelope.cs), [owner feedback and controls](../../tools/allegiance-mcp/BotManager.StrikeEnvelope.cs), [wave coordinator](../../tools/allegiance-mcp/BotManager.StrikeMass.cs).

| Mounted weapon | Range calculation | Owner evidence needed for release |
|---|---|---|
| Guided missile | Initial speed × life + ½ acceleration × life², the native lock envelope | Fresh station type and ID, full lock, launcher ready and reload time elapsed |
| Dumbfire missile | Initial speed × life + ½ acceleration × active motor time². Active time excludes the ignition delay. | Fresh station type and ID, launcher ready, reload time elapsed and nose on station |
| Fixed gun | Projectile speed × life, with the side’s ammo-speed and energy-lifespan modifiers | Typed current station order, owner acquisition feedback, completed mount, sufficient energy/ammo, quiet burst cycle and aim |

The native range and lock checks are in `Igc/magazineigc.cpp:127–167`. Missile activation and acceleration are in `Igc/missileigc.cpp:104,363–404`. Fixed gun activation, burst and lifespan are in `Igc/weaponIGC.h:104–114,165–182`, and launch speed in `Igc/weaponIGC.cpp:213–221`. These are existing simulation rules. The bot’s conservative stationary-shooter distance, aim tolerance, 85% holding radius and eight-second wait are piloting choices.

On entering the hold, the pilot targets the observed station with the ordinary current-slot attack order. Missile feedback includes the recipient’s target type and ID. A gun-only owner has no typed missile echo. That pilot clears the current slot, waits for an owner HEAVY update with both target pools absent, orders the precise station, then waits for both pools to appear on a later update while that order remains current. HEAVY supplies scalar target pools and carries no station ID. The causal clear/order/acquire sequence uses the native per-recipient stream in `FedSrv/FedSrv.CPP:4079–4124`.

The pilot faces the station and uses ordinary directional thrust to reach its holding point. The cloaked element’s assigned bearing has a lateral component in the actual thrust command. A fixed ingress bearing prevents an orbit caused by rotating the goal on every tick. Velocity damping and the ordinary collision dodge remain active during approach. At fire time a fixed gun also checks the friendly firing cone.

Once the mass qualifies and at least one pilot is ready, a full ready subset can go immediately. After eight seconds the currently ready pilots can go together, even if others were lost or never acquired the target. Empty subsets keep holding. Each selected pilot rechecks readiness at its own go tick. The opt-in path removes the five-second legacy lead, which could invalidate readiness between selection and first fire. Permission counts once. The owner’s accepted missile creations or gun projectile echoes spend the pilot, consume the magazine estimate, and feed the separate wave-end accepted-round count. The fixture independently counts authoritative creations, including misses and final-tick rounds whose owner echo may arrive after wave closure. Option off uses the original standoff, membership, five-second lead, attack driver and release-time spent flag.

## Tests and deliberate breaks

The ten test methods comprise eleven Fast cases. Each deliberate change below was exercised against a separately built broken copy in scratch. All failures were assertion failures.

| Test method (unique prefix) | Deliberate change | Failure observed |
|---|---|---|
| Guided_grant_uses | Remove guided acceleration distance | Granted range differs from 1,645 |
| Dumbfire_uses_granted | Include ignition delay in the burn | Granted range differs from 926.875 |
| Gun_uses_fixed | Ignore the side’s energy projectile lifespan | Granted gun range differs from 1,012.5 |
| Ship_with_same_numeric | Accept a matching numeric ID regardless of object type | Ship falsely passes |
| Cloak_lane_applies | Remove the lateral component | No lateral thrust bit |
| Only_a_qualified | Require strictly more than eight seconds | Ready subset missing at the boundary |
| Only_an_accepted | Treat an unchanged echo counter as an accepted launch | Permission alone spends the pilot |
| Omitted_and_explicit_off | Enable the omitted option | Default assertion fails |
| P1105_Gun_only | Treat a hull-only owner report as a cleared slot | Station ordered before the clear acknowledgement |
| P1105_Hold_checks | Invert the opt-in spent assignment | Both disabled and enabled integration cases fail |
| P1105_Hold_checks, enabled case | Discard the wolf role when passing the lane to the actual flight controller | Encoded ship update lacks lateral thrust |

## Build qualification

Debug and Release solution builds passed with zero warnings and zero errors. All 158 tool projects built with the CI disk-saving option. The first offline tools pass exposed three missing KNI pipeline packages. Restoring their original 4.3.9001 packages from the local on-disk cache allowed the complete tools pass to succeed. NuGet packages, HTTP cache, CLI configuration and temporary files were under scratch. The glossary link checker verified 1,210 paths and the parity scorecard passed.

### Repeated strike and owner tests

All 124 new and affected cases across 89 methods passed three times, with each method in its own test process and artwork available. All 51 parameterized display cases also passed three times with exactly one case selected per process. The two final hold-driver variants each passed another three exact-case runs after adding the encoded lateral-thrust assertion. That final assertion was added after the full Fast sweep. The new test counts are eleven passes, zero failures and zero skips per repetition.

The repeated coverage included P1105, P959 massing, P961 reachable assembly, P801 massing and loopback, P745 siege duty, P896 evidence and survival, finale orders, fallback duty and missile-lock feedback. The P801 Live mass test passed in 3:33, 3:51 and 5:23. The P745 Live research siege passed in 16:38, 14:01 and 11:43. The scratch receipts are in `.scratch/final-isolated/`, `.scratch/exact/`, `.scratch/related*/`, `.scratch/mass-live-*.log` and `.scratch/fast-repeat/`.

### Fast sweep and isolated controls

The sweep ran 16,265 cases in 820.8 s. There were 16,247 passes, 11 failures and seven skips. All 2,549 cases in the bot’s ClientCore test project passed, including all eleven P1105 cases. Nine infrastructure checks passed. The broader gate was **red**. Eleven runs exceeded the calibrated 17.693 s limit. One of those was also the quarantined side-elimination failure, which passed the runner’s retry.

| Existing failing case | Same-environment reruns, three per exact display case |
|---|---|
| P1091 default 1920-wide HUD pixel goldens, Classic and Modern | Each failed 3/3. Both matched the recorded software-renderer hashes (Classic C100F69A43C7, Modern 1093B39B5B9B). |
| PlayerBundlePaths, artwork executable fallback, missing artwork, missing native source, local sky-config precedence and native-subdirectory guessing | Each failed 3/3. The simulated outside-repository install under the scratch temp directory discovers the checkout’s ancestor local.env. |
| P1017 docked buy-flow money change | Passed 3/3. The sweep saw Worse where the test expected Muted. |

The remaining failed or over-limit cases below all passed three fresh, single-display-case invocations. The time figures are seconds from TRX. The method signatures and failure families are recorded in the project flake ledger. The two newly itemized commander civ timings are recorded there as well. [Flake ledger](../TEST_FLAKES.md)

| Exact case | Fast sweep outcome and time | Isolated pass times |
|---|---|---|
| `BallotVotingLiveTests.Resign_passing_deactivates_the_side_and_ends_the_mission` | Failed, 0.244 | 0.800, 0.820, 0.760 |
| `KillBonusLiveTests.Real_end_to_end_projectile_kill_increases_kill_bonus` | Failed, 2.690 | 0.654, 0.642, 0.666 |
| `P1000CivProfileTests.Every_loaded_civ_has_immutable_specs_and_legal_routes(civ: 3)` | Passed, 24.797 | 2.259, 2.282, 3.003 |
| `P796VariedLoadoutTests.Varied_equips_every_legal_hull_and_part_of_the_role_over_many_purchases(civ: 25)` | Passed, 29.601 | 6.794, 6.777, 6.687 |
| `P796VariedLoadoutTests.Varied_equips_every_legal_hull_and_part_of_the_role_over_many_purchases(civ: 35)` | Passed, 51.575 | 4.597, 4.531, 4.567 |
| `P814GunnerTests.P814b_TurretCarrier_pref_keeps_or_beats_the_Best_hulls_turret_seats` | Passed, 39.056 | 4.114, 4.054, 4.048 |
| `P832bProsperityTests.Observed_He3_grows_miners_first_and_losses_trigger_replacement` | Passed, 18.283 | 3.220, 3.229, 3.265 |
| `P942ResearchCommanderTests.Generated_commander_funds_buyable_research_before_optional_production(civ: 18)` | Passed, 20.861 | 2.609, 2.740, 2.749 |
| `P942ResearchCommanderTests.Generated_commander_funds_buyable_research_before_optional_production(civ: 4)` | Passed, 22.292 | 2.446, 2.469, 2.527 |
| `P999CivStrategyTests.Every_profile_is_reachable_in_catalog_and_actual_side_buy_gates(civ: 5)` | Passed, 41.697 | 4.128, 4.052, 4.289 |
| `SC2NaniteSupportTests.Support_selection_obeys_research_dock_budget_and_mounts(civ: 25)` | Passed, 41.463 | 4.175, 4.160, 4.091 |
| `SC2NaniteSupportTests.Support_selection_obeys_research_dock_budget_and_mounts(civ: 3)` | Passed, 37.713 | 6.209, 6.163, 5.933 |
| `SideEliminationLiveTests.Eliminating_side2_sends_side_only_gameover_to_it_alone` | Failed, 21.502 | 0.733, 0.712, 0.792 |

## Exposure and interpretation

Closing to roughly 788 units for the common anti-base magazine puts a pilot inside defender fire and near the station’s collision volume while it waits for the rest of the wing. Dodge can pre-empt the shot on any tick. A visible or wolf lane is a steering assignment. Existing cloak-energy policy still applies, and immediate go leaves a wolf little or no committed lead time under cloak. Ejections establish a lost life. They do not by themselves identify a ram or attribute the loss to the station.

Missile lock range uses centre distance. A real missile can hit the near surface of a large station, inherit launch velocity or miss on a poor firing angle. For a gun, energy and ammo arrive as quantized owner fractions and there is no wire field naming the station alongside its target pools. The pilot’s quiet-cycle and target-acquisition checks are conservative estimates between updates. Accepted projectile creation is the independent firing evidence.

Both sides use the selected policy in every experimental game. A treatment win means the match was decided before expiry, regardless of which civ won. Fixed seeds reproduce the map and side assignment. Real socket scheduling, research, discovery, opponents and factory construction can still diverge. The post-go latency excludes time waiting to become ready. Read it alongside absolute go times, losses, per-match credits and matches with zero commits.

## Measurement method

Each arm uses ten combat pilots per side, the same support and probe seats, Iron Coalition (25) on Alpha, Ga’Taraan (3) on Bravo, normal research, varied purchases, native fog, 5,000× income and 64× research. The generator and match seed are pinned. Brawl has one sector. SingleRing has multiple sectors. The server advances 50 ms of simulation every 50 ms of wall time. The game cap is 900 s. The bot and server finale windows are 600 s, the maturity gate is one minute, prearm is two minutes, the initial bomber request is six, recent kills is one and minimum relaunch share is zero. The reachable-strike option uses its default. These are the paced BOT-R1 Brawl settings.

The fixture stamps go, individual release observations, accepted creations and post-tick station damage against the same simulation clock. Wave shooters are released pilots whose selected granted weapon actually produced a round. Effective hits decrease at least one station pool or deliver lethal damage. Multiple damage callbacks on the same tick share the tick’s final pools. A lethal tick counts one target death. Accepted fire stops at the target’s death, the next same-side commit or loss of the released life. An in-flight hit can still land after the pilot’s death. Side credits come from the server’s final station-kill tally. A clock winner is classified by the server’s deadline decision even when awarded on station kills.

The armed-life observer starts when the coordinator first verifies a real granted strike loadout on that life, including inbound pilots. It follows the life to its first server ejection or bot death. The loss measures count armed lives ended before any accepted round and before any accepted anti-base round (core damage type 8), respectively. Self-defence fire can distinguish these measures. A pilot surviving until game over contributes no loss. The first baseline predates the armed-life observer.

The match receipts include owner IDs and roles, typed targets, granted quantities, distance at release, time of first accepted shot, hit contributors, side credits and final pilot score totals. [Matched receipts](STRIKE_WEAPON_ENVELOPE_P1105_assets/matched-receipts.json)

To reproduce a matched arm, substitute the map, seed and output label. Add `--weapon-envelope` only for treatment.

```sh
dotnet run -c Release --project tools/p959-strike-proof -- --delivery-proof \
  --output .scratch/proof --count 10 --teams 2 --tick-ms 50 --game-seconds 900 \
  --finale-seconds 600 --finale-minutes 1 --bombers 6 --research-scale 64 \
  --seconds 1000 --mapgen Brawl --map-seed 959 --label Brawl-959-control
dotnet run --no-build -c Release --project tools/p959-strike-proof -- \
  --delivery-catalog > .scratch/proof/catalog.json
python3 tools/p959-strike-proof/delivery.py .scratch/proof/catalog.json .scratch/proof/*.log
```

## Matched results

| Map and seed | Arm | Shooters per wave | Go to hit (s) | Hits per wave | Match hits | Wave kills/commits | Credits A–B | Early or clock winner | Losses before any / anti-base shot |
| --- | --- | --- | --- | --- | --- | --- | --- | --- | --- |
| Brawl 959 | control | 9 | 33.50 | 8 | 8 | 1/1 | 1–0 | Alpha | 0 / 0 |
| Brawl 959 | treatment | 4 | 2.80 | 7 | 7 | 1/1 | 1–0 | Alpha | 11 / 11 |
| Brawl 960 | control | 8 | 48.55 | 7 | 26 | 1/1 | 5–0 | Alpha | 3 / 3 |
| Brawl 960 | treatment | 3, 7, 4 | 2.95, 3.05, 2.80 | 6, 4, 8 | 38 | 2/3 | 2–2 | clock (Bravo) | 9 / 9 |
| SingleRing 959 | control | 8 | 5.90 | 8 | 8 | 1/1 | 1–0 | clock (Alpha) | 2 / 2 |
| SingleRing 959 | treatment | — | — | — | 0 | 0/0 | 0–0 | clock (Bravo) | 2 / 2 |
| SingleRing 960 | control | — | — | — | 0 | 0/0 | 0–0 | clock (Bravo) | 6 / 6 |
| SingleRing 960 | treatment | — | — | — | 0 | 0/0 | 0–0 | clock (Alpha) | 4 / 4 |
| SingleRing 961 | control | — | — | — | 0 | 0/0 | 0–0 | clock (Alpha) | 2 / 2 |
| SingleRing 961 | treatment | — | — | — | 0 | 0/0 | 0–0 | clock (Alpha) | 1 / 1 |

A comma separates successive waves. A dash means no wave or no effective hit. The two loss counts can include pilots lost on ingress. Match credits include ordinary attacks, later targets and kills outside these strike waves.

| Arm | Games | Credited kills | Early wins | Wave kills/commits | Shooter-wave pairs | Accepted / hits | Median go to hit (s) | Losses before any / anti-base shot |
| --- | --- | --- | --- | --- | --- | --- | --- | --- |
| control | 5 | 7 | 2 | 3/3 | 25 | 43 / 23 | 33.50 | 13 / 13 |
| treatment | 5 | 5 | 1 | 3/4 | 18 | 28 / 25 | 2.88 | 27 / 27 |

The five pairs used identical Release bot binaries (SHA-256 prefix `D0B648616694C510`), side assignments, home sectors, tick duration and per-seed station geometry. All ten hosts reported clean shutdowns and zero tick overruns. All ten final per-side station-kill tallies matched the summed pilot base kills. Uncredited station destructions were zero throughout.

All 18 treatment releases carried SRM Anti-Base 1 (part 157). All reported the ordered station, a ready magazine and a distance of 770.0–898.5, inside the 926.875-unit dumbfire envelope. Control released 19 Anti-Base 1 and eight Anti-Base 2 (part 158) mounts. Seven of 27 were already inside their firing distance, but all 27 owner reports named ships. Guided missiles and fixed guns have grant, readiness and feedback tests. The final matched games supplied live delivery evidence for the two dumbfire parts.

Treatment delivered 25 effective target hits from 28 accepted strike rounds (89%) versus 23 from 43 (53%) for control. Eighteen of 18 treatment releases fired, compared with 25 of 27 control releases. The four treatment waves used the bounded subset. Three destroyed their nominated target. Control destroyed three targets in three waves. The conditional median uses the four treatment and three control waves with an effective hit.

On Brawl 959 the first station damage actually moved later in the game, from simulation second 368.30 to 515.05. Treatment’s go came at 512.25 versus 334.80 for control. Both won and earned one base. Across all five pairs, treatment lost 27 of 102 verified armed lives before their first shot (26%), compared with 13 of 77 (17%) in control. Three treatment games and two controls ended with zero base kills. The paired changes in credited kills were 0 and −1 on Brawl 959/960, then −1, 0 and 0 on SingleRing 959/960/961. Three four-sector treatments produced no commit. This sample supports the stricter release check and exposes the cost of the close hold. It supplies no match-level advantage for enabling the policy.

## Individual wave delivery

The distance interval covers actual station-centre distances at release. The station column counts owner missile reports that match the ordered station by type and ID. The ready column counts magazine-ready owner reports. The accepted column gives all selected-weapon creations and those aimed at the wave’s station during the target’s lifetime.

| Match | Wave | Side | Go (sim s) | Reason | Required/committed/released | Count × granted part | Distance | Station | Ready | Accepted / on target | Hit pilots |
| --- | --- | --- | --- | --- | --- | --- | --- | --- | --- | --- | --- |
| Brawl-959-control | 1 | A | 334.80 | massed | 9/9/9 | 9×157 | 3135.7–4676.3 | 0/9 | 9/9 | 17 / 17 | 8 |
| Brawl-959-treatment | 1 | A | 512.25 | ready-after-wait | 9/4/4 | 4×157 | 797.4–810.2 | 4/4 | 4/4 | 7 / 7 | 4 |
| Brawl-960-control | 1 | A | 327.60 | massed | 9/9/9 | 9×157 | 4491.7–5355.3 | 0/9 | 9/9 | 13 / 13 | 5 |
| Brawl-960-treatment | 1 | A | 350.40 | ready-after-wait | 9/3/3 | 3×157 | 779.9–799.3 | 3/3 | 3/3 | 6 / 6 | 3 |
| Brawl-960-treatment | 2 | A | 533.65 | ready-after-wait | 9/7/7 | 7×157 | 773.9–802.9 | 7/7 | 7/7 | 7 / 7 | 4 |
| Brawl-960-treatment | 3 | B | 539.90 | ready-after-wait | 9/4/4 | 4×157 | 770.0–898.5 | 4/4 | 4/4 | 8 / 8 | 4 |
| SingleRing-959-control | 1 | A | 907.55 | massed | 9/9/9 | 1×157, 8×158 | 410.9–2587.9 | 0/9 | 9/9 | 13 / 13 | 8 |

## Development matches

All 23 development games finished. These local runs exercised the measurement and policy before the final binary was pinned. The early configuration fixed the explicit civs and map seed but allowed a separate engine match seed to vary. The intermediate treatment began its eight-second wait at arrival qualification. The following treatment started its wait at first readiness and used the corrected dumbfire reach. The final build also records owner-accepted rounds separately in the wave-end line and uses the native fixed-gun burst interval. Each group includes its completed zero-kill games. The first group has no armed-life observer.

### Initial instrumentation

| Map and seed | Arm | Shooters per wave | Go to hit (s) | Hits per wave | Match hits | Wave kills/commits | Credits A–B | Early or clock winner | Losses before any / anti-base shot |
| --- | --- | --- | --- | --- | --- | --- | --- | --- | --- |
| Brawl 959 | control | 8 | 28.95 | 7 | 7 | 1/1 | 1–0 | Alpha | unmeasured |
| Brawl 959 | treatment | 1, 4 | 2.90, 4.10 | 2, 6 | 8 | 1/2 | 1–0 | Alpha | unmeasured |
| Brawl 960 | control | 8 | 40.75 | 7 | 26 | 1/1 | 5–0 | Alpha | unmeasured |
| Brawl 960 | treatment | 6, 3, 6 | 3.15, 2.95, 2.95 | 7, 6, 4 | 29 | 2/3 | 3–2 | clock (Alpha) | unmeasured |
| SingleRing 959 | control | 9 | 5.10 | 11 | 12 | 1/1 | 0–1 | clock (Bravo) | unmeasured |
| SingleRing 959 | treatment | — | — | — | 0 | 0/0 | 0–0 | clock (Alpha) | unmeasured |
| SingleRing 960 | control | 4 | 2.10 | 7 | 7 | 1/1 | 1–0 | clock (Alpha) | unmeasured |
| SingleRing 960 | treatment | 5, 8 | 3.00, 3.10 | 10, 7 | 17 | 1/2 | 1–0 | clock (Alpha) | unmeasured |

### Pinned-seed intermediate build

| Map and seed | Arm | Shooters per wave | Go to hit (s) | Hits per wave | Match hits | Wave kills/commits | Credits A–B | Early or clock winner | Losses before any / anti-base shot |
| --- | --- | --- | --- | --- | --- | --- | --- | --- | --- |
| Brawl 959 | control | 7 | 39.20 | 8 | 8 | 1/1 | 1–0 | Alpha | 0 / 0 |
| Brawl 959 | treatment | 7 | 3.40 | 7 | 7 | 1/1 | 1–0 | Alpha | 12 / 12 |
| Brawl 960 | control | 4, 7 | 27.90, 30.75 | 7, 9 | 35 | 2/2 | 4–2 | clock (Alpha) | 10 / 10 |
| Brawl 960 | treatment | 2, 6, 4 | 3.30, 3.25, 3.15 | 4, 6, 8 | 39 | 2/3 | 2–2 | clock (Bravo) | 11 / 11 |
| SingleRing 959 | control | — | — | — | 0 | 0/0 | 0–0 | clock (Alpha) | 1 / 1 |
| SingleRing 959 | treatment | — | — | — | 0 | 0/0 | 0–0 | clock (Bravo) | 9 / 9 |
| SingleRing 960 | control | — | — | — | 0 | 0/0 | 0–0 | clock (Alpha) | 4 / 4 |
| SingleRing 960 | treatment | 5, 7 | 0.55, 3.00 | 10, 9 | 28 | 2/2 | 2–1 | clock (Alpha) | 1 / 1 |
| SingleRing 961 | control | — | — | — | 0 | 0/0 | 0–0 | clock (Alpha) | 3 / 3 |
| SingleRing 961 | treatment | — | — | — | 0 | 0/0 | 0–0 | clock (Alpha) | 10 / 10 |

### First-ready timer and active burn

| Map and seed | Arm | Shooters per wave | Go to hit (s) | Hits per wave | Match hits | Wave kills/commits | Credits A–B | Early or clock winner | Losses before any / anti-base shot |
| --- | --- | --- | --- | --- | --- | --- | --- | --- | --- |
| Brawl 959 | treatment | 4 | 4.15 | 6 | 6 | 1/1 | 1–0 | Alpha | 13 / 13 |
| Brawl 960 | treatment | 4, 8 | 2.85, 2.95 | 8, 10 | 43 | 2/2 | 3–2 | clock (Alpha) | 12 / 12 |
| SingleRing 959 | treatment | — | — | — | 0 | 0/0 | 0–0 | clock (Alpha) | 7 / 7 |
| SingleRing 960 | treatment | — | — | — | 0 | 0/0 | 0–0 | clock (Bravo) | 5 / 5 |
| SingleRing 961 | treatment | 6 | 2.90 | 12 | 18 | 1/1 | 0–1 | clock (Bravo) | 0 / 0 |


## P1105b review fixes

**Keep `finale_weapon_envelope` off by default.** The final five pairs produced
nine credited station kills with the option on and five with it off. Eight of
the nine came in Brawl 960. Treatment won one game before the deadline,
control won two, and losses before firing rose from 12/72 armed lives (17%)
to 42/116 (36%). The idle observer recorded zero armed pilots in all ten games.

### Brief items

| Item | Status | Evidence | Time (UTC) |
|---|---|---|---|
| 1. Return pilots to ordinary flight | done | 18 handoffs queued no flight command and preserved cloak. A settled, exposed pilot encoded lateral thrust | 14:22 |
| 2. Sufficient ready subset | done | Mixed two-of-three grant, exact landing threshold and eight-second fallback passed | 14:09 |
| 3. Deadline commit | done | Two unready arrivals at legacy range committed immediately and flew the ordinary release | 14:09 |
| 4. Re-form after missed go | done | Both missed go, left membership after 20 s and committed together in the next wave | 14:09 |
| 5. Station order retries | done | Wire queue contained 1, 2, 3, 3 orders across loss, retries and owner confirmation | 14:09 |
| 6. Option-off transcript | done | Identical five-decision traces on dispatch base and fixed build, both settings | 14:09 |
| 7. Fast tests and deliberate breaks | done | All 27 new display cases passed three times alone and each failed an intentional mutation. All 146 directly affected Fast cases passed three times | 14:37 |
| 8. Five local pairs | done | Ten scored games, identical paired geometry and assembly, all pilot credits reconciled. See the match and wave tables | 14:54 |
| 9. Report and recommendation | done | Per-match and per-wave results, original-arm comparisons, mutation and gate receipts, risks and default recommendation | 14:59 |

The option-off comparison runs the same scenario and assertions against dispatch base
`62591b1bf` and the working tree. At 1801 the first arrival holds. At 1802
the second arrival completes the mass. Both pilots share go at 1807. Pilot 1
still holds at 1806.9, returns to ordinary flight and spends its grant at 1807,
and stays released at 1808. Omitted and explicit false both passed on both builds.

### Flight and coordinator changes

| Change | Implementation | Evidence |
|---|---|---|
| Handoff | `tools/allegiance-mcp/BotManager.StrikeEnvelope.cs:123` returns control on the first accepted shot, an unusable grant or a missing approach. `tools/allegiance-mcp/BotManager.StrikeMass.cs:863` propagates that return value. Released pilots outside the current membership also fall through. | The handoff tests inspect the encoded send queue and the prior cloak state. The ordinary defense and combat drivers run below `HoldFinaleStrike` in `BotManager.cs:5123`. |
| Delivery | `BotManager.StrikeEnvelope.cs:54` counts the selected granted mount, subtracts used missiles and caps gun shots to available owner pools. `StrikeWeaponEnvelope.cs:72` checks the predicted landed rounds against the planner's shots needed, then replays the mixed schedule against both regenerating station pools with power discounted by landing share. | At 16 scheduled rounds and 50% delivery the eight-round requirement qualifies. At 49.9%, nine required rounds, or excessive armor, the same group holds. Two pilots with four-round grants can commit before a third acquires. |
| Arrival, timeout and deadline | `BotManager.StrikeMass.cs:407` uses the same standoff arrival band in both arms. `StrikeWeaponEnvelope.cs:89` starts the wait when the current arrivals first qualify, including with zero locks. Losing mass qualification resets the wait. At eight seconds the entire still-qualified arrived wing receives the ordinary five-second go. A deadline-forced mass takes this path immediately. `BotManager.StrikeMass.cs:704,822` applies the release decision to individual pilots. | At 5,500 and 5,600 units two unready pilots qualified under each forced-release branch. Both tests check membership, go, hold before go and ordinary flight at go. |
| Re-form | `BotManager.StrikeMass.cs:434` removes an armed, unreleased member once its own 20-second release window has expired. The existing 15-second observation tail finishes before another wave forms. | Two pilots kept membership exactly at go+20, both left at go+20.1 and both joined the next commit. |
| Station order | `BotManager.StrikeEnvelope.cs:80,154` clears cached ship targets, captures the bearing once for a station and limits pending orders to one per two seconds. The typed magazine feedback or gun clear/acquire sequence acknowledges the order. | The test moves the ship, displaces the local station slot, verifies the encoded retry count and confirms the bearing stays fixed after a typed owner update. |

The changes are bot policy. The existing native station damage and regeneration
rules (`Igc/stationIGC.cpp:159–193`, `Igc/stationIGC.h:192–210`) and owner feedback (`FedSrv/FedSrv.CPP:4079–4124`)
supply the inputs to the decision.

During the first local run, Brawl 959 reported neutral controls at 461.35,
525.15 and 540.10 s on armed pilots before the 540.50 s commit. They had
reached the weapon lane and the velocity correction was inside its 20-unit
tolerance. At `BotManager.StrikeEnvelope.cs:166` the final driver strafes laterally in that tolerance while exposed
and keeps the nose on station. The existing correction steers back to the
fixed lane as the pilot moves. A separate packet test now checks that a
settled visible pilot actually commands lateral thrust. The five pairs below ran on that revision. The preliminary runs are excluded
from the final comparison.

### Deliberate breaks

Each row represents a separate edit to a scratch copy of the final source.
Every failure below was an assertion failure after a successful build.
Together the mutations failed every newly added display case.

| Production change in the broken copy | Failing case or cases |
|---|---|
| Send neutral controls and consume a spent or unusable tick | 14 handoff cases, including depleted magazine, null parts on the matching hull, stale owner, target and grant observations, and the accepted-shot edge |
| Consume the tick for a spent or released non-member | Both post-wave handoff cases |
| Consume a tick when the session has no current target-sector pose | The other two handoff cases. Together these three edits failed all 18 variants. |
| Require every arrived pilot to be ready | The real mixed-grant two-of-three commit |
| Remove the landing-share discount from the round count | The fractional boundary case |
| Accept a positive delivery count without replaying station damage | The same case with 400 armor |
| Change the eight-second bound to a strict greater-than test | Full-wing timeout at the exact boundary |
| Reject the deadline branch | Immediate deadline commit with zero locks |
| Keep readiness gating at individual go on a fallback wave | Both deadline and timeout transcripts |
| Remove released members in place of unreleased members at expiry | Both missed-go pilots still belonged to the old wave |
| Remove the order retry interval | Extra station orders before two seconds elapsed |
| Recompute the bearing from each new position | The order test observed the bearing move |
| Keep the cached ship order | The order test found an old target ID |
| Suppress the lateral thrust when the visible hold pilot settles | The encoded flight update lacked either lateral button |
| Set ordinary go lead to zero | Both option-off transcripts differed from the base |

### Verification

The final Debug and Release solution builds each finished with zero warnings
and zero errors. NuGet used local packages and a scratch-only restore source.
The glossary link checker validated 1,217 paths and the parity scorecard passed.
The Fast runner discovered all 36 P1105/P1105b cases in the bot client tests.
The focused run passed all 36 with artwork. Across the affected Fast suites,
146 exact display cases each passed in three fresh processes (438 passes).
The final tools gate built all 159 projects with the CI disk-saving option.

Both directly affected Live scenarios also passed three times alone with artwork.
The fogged mass-strike runs took 247.2, 258.8, 317.1 s. The research showcase
siege runs took 675.1, 748.8, 699.1 s. The gate receipts include the duration of
every affected repeat, the outcome and isolated repeats of every failing or
over-limit sweep case, and assertion-failure counts for each deliberate break.
[Gate receipts](STRIKE_WEAPON_ENVELOPE_P1105_assets/p1105b-gate.json)

For receipt reproduction, run the original delivery-proof command once per
listed seed and arm. Use `--review-observers` with `delivery.py` on these
P1105b logs. This identifies an observed zero even in a match with no arrivals.
A parser check stripped both new event types from a local log and recovered
the original commit and hits while leaving the historical idle count unknown.

**The final full Fast gate was red.** It ran 16,310 cases in 895.8 s, with
16,302 passes, two failures and six skips. All 2,594 ClientCore tests and
nine infrastructure checks passed. The two 1920-wide P1091 HUD goldens
(Classic and Modern, default scale) each reproduced their ledgered local
software-renderer mismatch on all three isolated repeats.

| Passing case over the final 15.059 s limit | Sweep (s) | Three isolated runs (s) |
|---|---:|---|
| SC2 nanite support selection, civ 25 | 18.591 | 3.879, 3.788, 3.755 |
| P814b turret carrier preference | 20.883 | 3.936, 3.906, 3.766 |
| SC2 nanite support selection, civ 3 | 16.370 | 5.511, 5.514, 5.524 |
| Tech tree side-attribute mission broadcast | 15.431 | 7.964, 7.426, 7.587 |

The earlier sweep preceded the settled-pilot change. Its 15 failures and
seven timing overruns each received three isolated runs. The four-CPU cap
made the five-worker pool test fail, the isolated loopback namespace lacked
a local broadcast route, and simulated installations under the checkout
inherited its artwork configuration. The final gate used eight logical CPUs,
an isolated local broadcast route and scratch-backed temporary storage
mounted outside the checkout. Those environment-dependent cases passed.
The preliminary TCP greeting-order timing has been added to the flake ledger.
[Flake ledger](../TEST_FLAKES.md)

### Matched local results

All ten runs reached game over. All hosts shut down cleanly with zero tick overruns. The two arms used the same Release bot assembly, 50 ms ticks, civilizations, sides and per-seed boot geometry. The fixture used the P1105 command and settings above for both arms. Absolute times are simulation seconds from host start. [Compact per-match receipts](STRIKE_WEAPON_ENVELOPE_P1105_assets/p1105b-matched-receipts.json)

| Map and seed | Fixed arm | First arrival | First match damage | Arrival to first match damage | Effective hits | Wave kills/commits | Credits A–B | Early winner | Lost before first shot / armed lives | Idle pilots |
| --- | --- | --- | --- | --- | --- | --- | --- | --- | --- | --- |
| Brawl 959 | off | 304.65 | 397.40 | 92.75 | 8 | 1/1 | 1–0 | Alpha | 2/9 | 0 |
| Brawl 959 | on | 287.20 | 598.45 | 311.25 | 7 | 1/1 | 1–0 | Alpha | 15/24 | 0 |
| Brawl 960 | off | 294.05 | 373.10 | 79.05 | 7 | 1/1 | 1–0 | Alpha | 0/9 | 0 |
| Brawl 960 | on | 286.75 | 487.25 | 200.50 | 47 | 2/2 | 5–3 | clock | 12/30 | 0 |
| SingleRing 959 | off | 673.50 | — | — | 0 | 0/0 | 0–0 | clock | 2/15 | 0 |
| SingleRing 959 | on | 558.50 | — | — | 0 | 0/0 | 0–0 | clock | 2/17 | 0 |
| SingleRing 960 | off | 580.35 | — | — | 0 | 0/0 | 0–0 | clock | 7/20 | 0 |
| SingleRing 960 | on | 648.20 | — | — | 0 | 0/0 | 0–0 | clock | 6/20 | 0 |
| SingleRing 961 | off | 614.30 | 796.25 | 181.95 | 28 | 2/2 | 2–1 | clock | 1/19 | 0 |
| SingleRing 961 | on | 667.85 | — | — | 0 | 0/0 | 0–0 | clock | 7/25 | 0 |

The arrival clock starts on the once-per-second coordinator’s first observation of an armed pilot inside the shared hold arrival band (3,500 visible or 5,000 wolf standoff, plus the 3,600-unit gather radius). The match damage column includes ordinary attacks by either side. The wave table attributes hits to released pilots against the committed target. A dash denotes an unobserved event.

| Map and seed | Arm / side | First hold arrival | Commit | Committed / required | Reason | Distinct shooters | Hits | First wave damage | Arrival to wave damage | Wave kills |
| --- | --- | --- | --- | --- | --- | --- | --- | --- | --- | --- |
| Brawl 959 | off / A | 304.65 | 354.95 | 9/9 | massed | 7 | 8 | 397.40 | 92.75 | 1 |
| Brawl 959 | on / A | 287.20 | 595.35 | 8/9 | weapon-ready | 8 | 7 | 598.45 | 311.25 | 1 |
| Brawl 960 | off / A | 294.05 | 323.70 | 9/9 | massed | 8 | 7 | 373.10 | 79.05 | 1 |
| Brawl 960 | on / A | 286.75 | 484.25 | 4/9 | weapon-ready | 4 | 8 | 487.25 | 200.50 | 1 |
| Brawl 960 | on / B | 474.90 | 543.85 | 9/9 | readiness-timeout | 6 | 8 | 551.20 | 76.30 | 1 |
| SingleRing 961 | off / A | 614.30 | 788.15 | 9/9 | massed | 6 | 8 | 796.25 | 181.95 | 1 |
| SingleRing 961 | off / B | 624.65 | 871.45 | 9/9 | massed | 6 | 10 | 879.60 | 254.95 | 1 |

The idle observer checks authoritative controls every simulation tick. It records a pilot with all buttons clear and reset axes continuously for at least one second within 1,500 units of a hostile station. The receipt counts distinct pilots whose real strike grant had already been observed on that life. This measures the reported freeze signature. It cannot detect every form of unproductive movement.

### Comparison with both original arms

| Map and seed | P1105 off | P1105 on | P1105b off | P1105b on |
| --- | --- | --- | --- | --- |
| Brawl 959 | 1 / 1 / 0 / 368.30 | 1 / 1 / 11 / 515.05 | 1 / 1 / 2 / 397.40 | 1 / 1 / 15 / 598.45 |
| Brawl 960 | 5 / 1 / 3 / 376.15 | 4 / 0 / 9 / 353.35 | 1 / 1 / 0 / 373.10 | 8 / 0 / 12 / 487.25 |
| SingleRing 959 | 1 / 0 / 2 / 913.45 | 0 / 0 / 2 / — | 0 / 0 / 2 / — | 0 / 0 / 2 / — |
| SingleRing 960 | 0 / 0 / 6 / — | 0 / 0 / 4 / — | 0 / 0 / 7 / — | 0 / 0 / 6 / — |
| SingleRing 961 | 0 / 0 / 2 / — | 0 / 0 / 1 / — | 3 / 0 / 1 / 796.25 | 0 / 0 / 7 / — |

Cells give credited kills / pre-deadline wins / losses before any accepted shot / absolute time of first wave-attributed station damage (s). All five games per arm contribute, including matches with zero kills or commits. Historical receipts have go and first wave-hit offsets. They lack a first-arrival timestamp and an idle-control observer.

| Build and arm | Games | Credited kills | Early wins | Kills/commits | Shooter-wave pairs | Accepted / wave hits | All effective hits | Losses / armed lives | Zero-credit games |
| --- | --- | --- | --- | --- | --- | --- | --- | --- | --- |
| P1105 off | 5 | 7 | 2 | 3/3 | 25 | 43/23 | 42 | 13/77 | 2 |
| P1105 on | 5 | 5 | 1 | 3/4 | 18 | 28/25 | 45 | 27/102 | 3 |
| P1105b off | 5 | 5 | 2 | 4/4 | 27 | 46/33 | 43 | 12/72 | 2 |
| P1105b on | 5 | 9 | 1 | 3/3 | 18 | 33/23 | 54 | 42/116 | 3 |

### Interpretation and risks

On Brawl 960, four ready Alpha pilots delivered eight effective hits and
destroyed their target. Bravo's eight-second wait expired and its entire
nine-pilot arrived wing committed. Six fired and their eight effective hits
destroyed that target too. Six of the eight match credits came outside those
two wave kills. Each side's final credit total matched the summed pilot
scores in every game. The fixture counted accepted creations on the server,
including attacks after handoff to ordinary flight. The envelope driver's
own echo count covers the handoff and cannot measure subsequent ordinary fire.

On both Brawl seeds the first strike pilot arrived earlier with the option on.
First damage nevertheless came 201.05 s later on 959 and 114.15 s later on
960. All three SingleRing treatments finished without a commit. Neither
fixed arm committed on SingleRing 959. Its treatment run reported bot-local
trigger at 60.89 s and game over at 430.81 s, within the bot's 600 s deadline
window. The host used a 900 s match cap. The deadline regression exercises
an actual due decision with unready pilots. None of these ten games emitted
a deadline-forced commit.

The mixed volley replay discounts the mount's actual damage by recent
landing share and assumes the group can deliver it inside one window.
Losses while waiting for enough arrivals remain a cost, especially at the
close hold. Both sides used the selected policy in each game. The five
fixed map seeds reproduce geometry while research timing, encounters,
destruction of additional stations and real socket scheduling still vary.

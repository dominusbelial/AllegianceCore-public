# P1104 — First sustainable attack

The ordinary research commander can now field a pre-finale raid with purchased, mounted weapons and keep a defender at home. In the four preset pairs, three of four raids landed a confirmed hit and none destroyed its target. A bomber-heavy 10v10 treatment did produce a credited ordinary raid kill. Across all six pairs, controls and treatments scored ten station kills apiece, and every game ended on a timer. Keep the option **off** for showcase, public perpetual and skirmish.

## Brief items

| Item | Status | Evidence and time |
|---|---|---|
| Inventory | done | Source and preset checks below, 2 min |
| Readiness | done | Mixed mounted volley, fog, route and timing checks passed. Implementation and fixtures took about 9 min. |
| Ordinary raid | done | Option-off transcript, independent side tests and a final-build live launch with a separate home defender. Implementation, live diagnosis of competing orders and the corrected multi-sector proof spanned about 32 min. |
| Telemetry | done | Ordered stage tests, four terminal outcomes, a final GAME_OVER observation and live server receipts. About 9 min. |
| Matched local games | done | All four required pairs completed on the corrected build. Geometry, sides, seeds, combat seats, station credit and end receipts were checked in all eight. The preset replays took about 14 min. |
| Fast tests and deliberate breaks | done | All 13 new cases passed three times in method isolation. Ten independent deliberate breaks failed the corresponding 13 cases. Final isolation took 8 min and ran alongside the negative controls. |
| Report | done | Per-match measurements, causal raid receipts, development failures, risks and decisions for all three deployments below. About 12 min of reporting and verification, overlapping the runs. |
| Light gate | not done | Builds, tools, isolated cases, mutation proofs, glossary and parity passed. The final full Fast sweep ran 10 min 30 s and was red on five unrelated assertions and six calibrated timing overruns. Details below. |

## Inventory before implementation

| Existing policy | Shipped value and effect | Showcase and public backfill |
|---|---|---|
| P959 prearm | `finale_prearm_minutes=0`. A positive lead reserves and legally refits a wing before the maturity threshold. | Spawn schema and parser in `McpTools.cs`. The P959 measurement fixture explicitly sets 2 minutes. The public driver calls the settings constructor with its default. |
| P959 early pressure | `finale_early_pressure_minutes=0`. A positive age allows a single earlier massed finale wing, subject to its target and timing gates. | Neither shipped preset sets a positive age. |
| P959 mass, survival and clock | `finale_bombers=12`, `finale_max_wing=18`, `finale_survival_margin=1`, `finale_deadline_seconds=900`. Growth, delivery loss, observed defenders and the deadline affect whether a useful mass commits. | Same defaults, including `finale_escort_share=0.1`, cap 12, radius 1,500 and leash 2,500. The spawn preset selects both sides (all for a multi-side scenario). Public backfill uses leader selection. The default maturity floor is 30 minutes, 0.5 relaunch share and five recent kills. |
| P961 reachable planner | `finale_reachable_strike=false`, `finale_plateau_seconds=45`, `finale_engagement_range=2500`. Enabled, it measures remaining ordnance and travel, allows a reachable retarget and releases an adequate plateau. | Neither preset opts in. No P961 claim that more station deaths imply more last-restart losses. |
| P1000/P1001 civilization strategy | Server-side configuration `bots.civStrategy=false`, `bots.civStrategyCoverageShare=0.15` when enabled. The plan describes deliverable research. The commander protects a miner/defence budget, funds legal jobs and composes a fleet with scout and strike seats. Purchases still depend on actual station, tech and money. | Both drivers consume the side's briefing. Neither switches on the server experiment. Existing unlocked and explicit objective policies have their own destination owners. |
| Existing ordinary tactics | ResearchTactics already issues attack, rally, escort, defence and repair orders. Its role/hull roster and staged timeout serve a different readiness condition. | Both run the ordinary commander where their game and briefing permit it. The new experiment reuses its orders and the existing volley simulation. |

Source checks used `ShowcaseFinale.Settings`, `McpTools.ReadFinaleSettings`, `BotManager.Backfill.ArmBackfillFinale`, `BotManager.Finale`, `FinaleAssembly`, `ResearchCommander.CivStrategy`, `BotManager.CivStrategy`, the P959/P961 and P1000/P1001 reports, and the public compose/example JSON.

## Local match design

The Spider fixture uses a fresh civilization RNG seed unless explicitly set and its file-map resource top-up is unseeded. Spider diagnostics are kept separately. The matched fixture sets `BenchmarkSeed`, `CivSeed` and the map generator seed in both arms. Two-team DiamondRing failed validation before boot in all six setup attempts. Those attempts produced zero matches. The three 10v10 pairs use the valid multi-sector SingleRing generator. The 5v5 pair uses Brawl. Both arms have the native fog, ordinary purchases, fixed civs 25/3, 900 simulation seconds, 420-second finale (both clocks), BOT-R1's 20 ms pacing, income 5,000× and research 64×. To allow an ordinary opening, prearm and early pressure are off, and the finale maturity floor is two minutes after registration. The closer retains its one recent kill requirement. The seed is 11040 plus the pair number. Pairs alternated which arm ran first. Each arm used a fresh local server and a single bot manager. Pilot admissions were checked by joining validated combat logons to the seated ship IDs, excluding probe and commander seats. Generated geometry, initial sides and the seed receipts were verified across each pair. Pair 3 enables P1001 in both arms. The other three use the authored research plans.

## Design and evidence

The decision runs after the side’s normal research tick. The briefing epoch and loaded plan core fence the pilot observations. Serial plans keep funding their steps. P1001 portfolio plans keep their budget, research jobs, fleet slots and dock selection. Neither plan treats research completion, cash, a requested fit, or a role label as delivered damage.

Readiness requires the station in the most recent side-visible STATIONS_UPDATE, a simulation sample at most two seconds old, and each prospective pilot's own exported station plus paired alephs on its route. The P961 travel estimator checks the shortest transit route used by ordinary flight and supplies per-pilot arrival estimates. An unexported aleph on that route blocks qualification. Timed missions need room for transit and a 20-second volley. The forecast uses full station durability and public side multipliers. It intersects each flying pilot's purchase acknowledgement with current mounted parts, removes confirmed launches, limits gun fire to observed ammo and energy, and uses the existing native damage/regen replay with offsets for estimated arrivals. Native references are in `docs/parity/p819-bot-policy.csv`. The arrival estimate and wing selection are bot policy.

Search proceeds in increasing wing size up to the existing 18-pilot planning cap. Damage upper bounds and skipping interchangeable candidates limit the search. A feasible wing leaves a currently armed combat pilot in a friendly restart sector. That pilot receives a defend order. Scouts, active defenders, repair pilots, reserved/refitting finale pilots and carriers on escort duty keep their assignments. ResearchTactics sends the attack through its ordinary capped individual command channel. Before the first raid closes, the new policy owns ordinary station-strike orders. It gives the first command slot to the home defender. The legacy strike-role planner resumes when the raid ends or the closer starts. Each AI side in a manager gets one opportunity per briefing epoch. Public backfill keeps these per-side decisions independent of its leader-only finale. Sharded showcase workers can evaluate only their own pilots. The closer can interrupt a raid. The option does not modify its side selection, maturity clock or fog grace.

`[ordinary-raid]` carries epoch, side, target, stage and simulation seconds. Sighting is the first commander sample whose fresh batch included that target. The client clock advances on server samples, so consecutive stages can share a timestamp. Readiness also records member IDs, home defender, station and plan. Launch means the first commander attack order. Arrival means a dispatched member was observed in the target sector. An observed health drop can include a teammate’s fire. The local observer records shooter-attributed effective hits independently using the existing station damage delegate, including lethal hits, and joins them to each wing, target and launch window. A confirmed destruction has a launcher ID. A changed owner gives a capture receipt. Loss of every available member or destruction by somebody outside the wing gives a repulse receipt. The launcher ID distinguishes that second case. A 270-second window, the finale or game over closes an unresolved sortie as timeout. Disappearance from fog alone supplies no kill credit.

## Decision and telemetry checks

The Fast fixture exercises the commander's production decision and the world inputs it consumes. Each change below was made separately in a disposable copy of the checkout. Each named test failed with the altered code. The four outcomes are distinct theory cases. A multi-sector diagnostic exposed the competing legacy strike branch. Its correction and the strike-role regression precede every qualification match reported below.

| Test in `P1104SustainableRaidTests` | Deliberate break | Failure observed |
|---|---|---|
| `No_enemy_target_has_no_launch` | Allow friendly stations into readiness | The friendly station produced a wing. |
| `Stale_or_withdrawn_visibility_invalidates_remembered_export` | Remove the two-second upper age bound | A three-second-old sighting produced a wing. |
| `Each_member_needs_a_known_route_to_this_station` | Accept a route to any station and use zero travel time | A pilot whose only route led elsewhere counted toward the kill. The intact test also rejects arrivals 25 seconds apart. |
| `Purchase_without_a_current_firing_mount_or_remaining_round_cannot_count` | Accept partially mounted parts | An incomplete missile mount produced a firing schedule. The intact test also covers cargo, changed hull, no current part and exhausted rounds. |
| `Smallest_mixed_granted_volley_can_launch_without_a_bomber_role` | Search from the largest wing down | A single sufficient pilot lost priority. The intact test also requires two complementary pilots when neither can destroy the station alone. |
| `Leave_a_defender_at_home_and_the_scout_on_its_lane` | Permit a wing member to count as its own home reserve | The last armed home pilot could leave. The intact test checks the actual defend order and scout exclusion. |
| `Strike_role_pilots_receive_only_the_raid_orders_within_the_shared_budget` | Run the legacy strike branch alongside the ordinary raid | A stale target received a legacy attack before readiness. The intact test also verifies the home defend order, ordered assignment of two attackers across two ticks, and zero legacy wave events. |
| `Off_preserves_the_ordinary_command_transcript_even_with_a_feasible_wing` | Make the driver option default on | The default assertion failed. The intact transcript checks both ordinary ship attack orders, their recipients, command suppression on the next tick and zero raid events. |
| `Game_over_keeps_the_credited_launcher_available_to_the_last_raid_observation` | Suppress the final observation on GAME_OVER | The final observation lost the credited launcher. Mission rotation clears both sight and destruction evidence. |
| `Each_side_has_one_independent_sortie_and_observed_outcome` | Replace the terminal event's stage | All four cases failed. The intact test checks both side policies, the ordered stage receipts and suppression of a second sortie in the same epoch. |

## Measurement definitions

Each match begins at the server's scheduled start of play. First useful hit is elapsed simulation time to the first enemy station damage receipt that reduced shield or armor or destroyed the station, regardless of which pilot fired. The server observer samples final health at the end of each tick. A raid hit additionally requires a wing member, its selected target and a hit inside its launch-to-outcome interval. A dash denotes no qualifying hit during that match.

Station kills use the mission's credited tally, checked against the server's end-of-game station score. Pre-finale kills are distinct station destructions before the server announced that it armed its bounded finale. When it never armed, the entire game counts as pre-finale. A home loss is destruction or capture of a starting home station in the boot geometry. Last-restart losses require the explicit side-elimination receipt. A deadline can be decided by the station tally while both sides still have restart stations.

To reproduce a preset arm, build the proof and run the following from the repository root. Set the seed and fleet size as shown in the results. Use Brawl for the small pair, add `--civ-strategy` to both arms of seed 11043, and add `--sustainable-raid` only to each treatment. The delivery-fleet pairs add `--raid-delivery-fleet`, use ten pilots per side, a 1,200-second cap and a 12-minute maturity floor. Their six bomber, three fighter and one scout requests per side still use the ordinary shop and can receive legal fallback equipment.

```sh
dotnet run -c Release --project tools/p959-strike-proof -- \
  --matched --teams 2 --count 10 --mapgen SingleRing --map-seed 11041 \
  --tick-ms 20 --game-seconds 900 --finale-seconds 420 --finale-minutes 2 \
  --prearm-minutes 0 --bombers 6 --research-scale 64 --seconds 1000 \
  --output .scratch/p1104/reproduce --label 1-off
```

## Matched results

The four preset pairs used the standard fleet chosen by the showcase spawn preset. S0 was Iron Coalition and S1 was Ga’Taraan throughout. “Cap” is the match time limit. “Finale” is the earlier bounded-finale deadline. Both are timer endings.

| Seed | Arm | Winner, end / decider | Station kills S0/S1 | Pre-finale S0/S1 | First useful hit (s) | Last restarts lost | Starting homes lost S0/S1 |
|---:|---|---|---|---|---:|---:|---|
| 11041 | OFF | S1, cap / ship kills | 0/0 | 0/0 | 609.6 | 0 | 0/0 |
| 11041 | ON | S1, cap / ship kills | 0/0 | 0/0 | 720.15 | 0 | 0/0 |
| 11042 | OFF | S0, cap / ship kills | 0/0 | 0/0 | — | 0 | 0/0 |
| 11042 | ON | S1, cap / ship kills | 0/0 | 0/0 | — | 0 | 0/0 |
| 11043 | OFF | S0, cap / station kills | 1/0 | 1/0 | 409.35 | 0 | 0/0 |
| 11043 | ON | S0, cap / ship kills | 0/0 | 0/0 | 373.3 | 0 | 0/0 |
| 11044 | OFF | S0, finale / ship kills | 0/0 | 0/0 | — | 0 | 0/0 |
| 11044 | ON | S1, finale / ship kills | 0/0 | 0/0 | — | 0 | 0/0 |

The four preset treatments launched four raids across three games. All four reached their target sector, three dealt shooter-attributed damage to the assigned station, and all four were repulsed. The four controls recorded one credited station kill, before the finale. The four treatments recorded zero. Seven of eight games had zero station kills and all eight ended on time. No starting home or last restart was lost. Both arms landed a useful hit on two of the four seeds. On seed 11043 the treatment hit 36.05 seconds earlier, while on 11041 it hit 110.55 seconds later. S0 won three controls and one treatment. These are paired observations from single executions.

### Additional matched delivery-fleet pairs

Seed 11045 used single-sector Brawl. Seed 11046 used four-sector SingleRing. Both sides had the same requested fleet, seed, economy and longer clock in each pair.

| Seed | Arm | Winner, end / decider | Station kills S0/S1 | Pre-finale S0/S1 | First useful hit (s) | Last restarts lost | Starting homes lost S0/S1 |
|---:|---|---|---|---|---:|---:|---|
| 11045 | OFF | S0, cap / station kills | 6/1 | 6/1 | 791 | 0 | 0/0 |
| 11045 | ON | S0, cap / station kills | 5/3 | 5/3 | 517.9 | 0 | 0/0 |
| 11046 | OFF | S0, cap / station kills | 2/0 | 2/0 | 790.6 | 0 | 0/0 |
| 11046 | ON | S0, cap / station kills | 2/0 | 2/0 | 695.65 | 0 | 0/0 |

The delivery-fleet controls recorded nine station kills and the treatments ten. Every kill preceded a server finale arm. On Brawl, the first team hit arrived 273.1 seconds earlier with the option on. S1’s bomber 8111 destroyed its assigned heavy teleport station, 9188. The authoritative destruction receipt named 8111 and the damage observer recorded the lethal hit at simulation second 803.25. S1 still lost the match on station score, 3–5. On SingleRing the first team hit was 94.95 seconds earlier, neither ordinary wing arrived, and the final station scores were identical.

Across all six matched pairs the six controls and six treatments each recorded **10 station kills**, all pre-finale. Seven of the 12 games had none. All 12 ended on a timer with zero last-restart eliminations and zero starting home losses. These totals combine two different fleet/clock settings. The per-pair results above are the basis for the deployment decision.

### Ordinary raid receipts

Times here are simulation-clock readings. The observer-confirmed hit uses the server clock. Sighting, launch, arrival and terminal timestamps use the commander's last server-clock sample. A dash in the arrival column means the commander never observed a dispatched member in the target sector. Eight sorties produced six observed arrivals, four shooter-confirmed first hits, one credited kill and seven repulses.

| Seed | Side → target | Attacker(s) / defender @ station | Sight | Ready | Launch | Sector arrival | Observer-confirmed first hit | Outcome at |
|---:|---|---|---:|---:|---:|---:|---:|---|
| 11041 | 0 → 9195 | 8120 / 8114 @ 0 | 615.75 | 615.75 | 615.75 | 715.4 | 735.45 | repulse 738.8 |
| 11042 | 0 → 9193 | 8120 / 8106 @ 0 | 674 | 702.95 | 702.95 | 726.55 | — | repulse 737.8 |
| 11043 | 0 → 9188 | 8114 / 8112 @ 0 | 186.7 | 250.7 | 250.7 | 368.65 | 388.65 | repulse 447.1 |
| 11043 | 1 → 9187 | 8113,8115 / 8111 @ 1 | 468.05 | 633 | 633 | 688.95 | 721.1 | repulse 754.85 |
| 11045 | 0 → 9187 | 8110 / 8108 @ 0 | 184.9 | 393.1 | 393.1 | 393.1 | — | repulse 478 |
| 11045 | 1 → 9188 | 8111 / 8103 @ 1 | 547.85 | 756.4 | 756.4 | 756.4 | 795.15 | credited-kill 800.8 |
| 11046 | 0 → 1 | 8108,8112 / 8110 @ 0 | 172.9 | 310.7 | 310.7 | — | — | repulse 420.4 |
| 11046 | 1 → 9191 | 8105 / 8115 @ 1 | 332.1 | 756.75 | 756.75 | — | — | repulse 876.25 |

The local raw evidence is under `.scratch/p1104/qualified-games/`. Each arm has its geometry, console, full match log and summary JSON. All 12 summaries identify the same bot assembly, SHA-256 prefix `3A4DCAA9A1DD0710`. Paired geometry matched byte for byte. The JSON preserves hit evidence, including the shooter, separately from the commander's health observation. Teardown receipts for subsequent empty missions are excluded at the first scheduled match's GAME_OVER.

## Development and setup attempts

The first seeded build used the original travel grouping. The second added arrival offsets, game-over evidence and hit attribution. Both still allowed a legacy strike-role planner to run alongside the ordinary raid. In the second build, a SingleRing transcript placed a legacy four-member attack on station 9195 before the new raid chose a single attacker for it. These earlier runs explain the regression test. Their outcomes stay in the development denominator.

Each cell gives winner, ending and decider, followed by team station kills (S0/S1), pre-finale kills (S0/S1), and elapsed seconds to first useful hit.

| Development build | Seed | OFF | ON |
|---|---:|---|---|
| First | 11041 | S1, cap / ship kills · 0/0 · 0/0 · — | S0, cap / ship kills · 0/0 · 0/0 · — |
| First | 11042 | S1, cap / ship kills · 0/0 · 0/0 · — | S1, cap / ship kills · 0/0 · 0/0 · — |
| First | 11043 | S0, cap / ship kills · 0/0 · 0/0 · 493.95 | S0, cap / station kills · 1/0 · 1/0 · 609.15 |
| First | 11044 | S0, finale / ship kills · 0/0 · 0/0 · — | S1, finale / ship kills · 0/0 · 0/0 · — |
| Second | 11041 | S1, cap / ship kills · 0/0 · 0/0 · 798.8 | S1, cap / station kills · 0/1 · 0/0 · 771.35 |
| Second | 11042 | S1, cap / ship kills · 0/0 · 0/0 · — | S1, cap / ship kills · 0/0 · 0/0 · — |
| Second | 11043 | S0, cap / station kills · 2/0 · 2/0 · 377.25 | S1, cap / ship kills · 2/2 · 2/0 · 394.4 |
| Second | 11044 | S0, finale / ship kills · 0/0 · 0/0 · — | S1, finale / ship kills · 0/0 · 0/0 · — |
| Second | 11045 | S0, cap / station kills · 2/1 · 2/1 · 500.7 | S0, cap / station kills · 5/0 · 5/0 · 348.1 |
| Second | 11046 | S0, cap / station kills · 4/0 · 4/0 · 667 | S0, cap / station kills · 2/1 · 2/1 · 623.4 |

Across those 20 games, 12 had zero station kills. The second-build treatments for seeds 11041 and 11043 each lost S0’s starting home. Every other development game had zero home losses. All 20 had zero last-restart eliminations.

One separate first-build 10v10 delivery trial on Brawl, seed 11045, ended S0, cap / station kills · 5/0 · 5/0 · 536.25. Home losses were 0/0 and last-restart losses were 0. The two ordinary raids ended in repulse and timeout.

The initial Spider launcher attempted eight games. Three 10v10s finished (OFF, ON, OFF). S0, S0 and S1 respectively won at the 900-second cap on ship kills. Each had 0/0 station kills, 0/0 pre-finale kills, no useful hit, no last-restart elimination and 0/0 home losses. The file-map resource top-up and civ RNG did not provide a matched seed. All three are retained here. Five process starts failed because the tools build removed the development proof binary. Two of the three completed Spider logs also lost their final summary step. Immutable proof directories removed that interference in later runs.

Six additional two-team DiamondRing configurations were rejected before boot. One delivery setup generated a map but submitted incompatible fleet/count arguments and never seated its combat fleets. These seven produced no playing time. Across qualification and development there were 48 attempted runs, 36 completed combat matches and 12 setup or process-start failures. Of the 36 completed matches, 22 had zero credited station kills.

## Risks and deployment

A station kill forecast assumes the rounds land. The P961 estimator uses its normal standoff distance, nominal speed and per-hop allowance. An accepted order still has to reach firing range, survive and maintain a firing solution. The P1001 heavy bomber on seed 11043 took almost 118 simulation seconds after launch to reach the sector. Its first confirmed hit took a further 20 seconds. The home guard is an equipped pilot at one friendly restart station when the commander issues the order. That provides neither a permanent occupancy guarantee nor coverage of every expansion.

Only two civilizations, fixed starting seats, one local worker and accelerated research/income were measured. Each seed, map and configuration has one qualifying game per arm. Recruitments, research timing, flight and combat can diverge even on equal opening geometry. The runs do not estimate a production win rate, multi-worker coordination, human commander handoff or how long a public match will last. Concentrating damage on expansions can move a station-kill tiebreak or compound an existing economic lead without eliminating the last restart. The two starting-home losses in development treatments warrant keeping home loss visible in future comparisons.

| Deployment | Recommendation | Evidence |
|---|---|---|
| Showcase | Keep the preset off. Retain the explicit switch for controlled research matches. | The standard fleet reached the assigned stations and landed three verified hits across four raids. The treatment had zero pre-finale station kills across four games. |
| Public perpetual | Keep the driver default off. | Independent sides can launch and retain separate home defenders. The comparison had fixed populations and omitted human seat churn, the public economy and worker topology. |
| Skirmish | Keep it off. | Neither 5v5 arm scored a useful station hit. That pair used the local showcase fixture on Brawl. Small offline skirmishes also have the meeting-engagement policy and a human seat. |

## Qualification evidence

Debug and Release solution builds both completed with 0 warnings and 0 errors. `tools/build-tools.sh` built all 158 tool projects. The glossary checker validated 1,210 paths, the parity scorecard validated the bot policy row, and `git diff --check` passed.

On the corrected build, 134 test methods ran individually three times. All **531 case executions passed**, including the 13 new cases each time, with zero skips. Coverage included SC6–SC9, order replacement and defence, station health and mission rotation, P801/P896/P959/P961 strike behaviour, P927 side selection, P1001 research/fleet duties and the backfill parser. All ten independent broken implementations failed their named tests. The build, ordinary match replays and test work overlapped.

The first Fast sweep ran 16,265 cases on a development build. Two 1920-pixel HUD image comparisons failed, along with five player-bundle path cases, one pool-size case, one observer seating timeout, one mine-loadout timeout, one miner cycle and one missing mine-export receipt. Every failing method was run three times alone. The four gameplay timing/receipt failures each passed 3/3. The four player-bundle methods reproduced their five failures 3/3 when the fake external bundle inherited the checkout's configuration, then passed 3/3 using the same scratch directory through a directory descriptor. The pool assertion reproduced 3/3 with the test process limited to four CPUs and passed 3/3 with eight. Both additional slow methods (varied purchases and NPC obstacle avoidance) passed 3/3. The new observer seating signature is in the [flake ledger](../TEST_FLAKES.md).

The two HUD comparisons failed 3/3 in isolation on the corrected build. The same test on archived dispatch `22a595651e58` reproduced the same two hashes with 19 other cases passing. Classic yielded `C100F69A43C7A4E8` and Modern `1093B39B5B9BA161` on the lane renderer. Softpipe also produced different hashes from the stored goldens with zero layout errors for both skins.


The intermediate sweep ran 16,266 cases. Its only assertion failures were the two HUD hashes. Ten executions also exceeded its 16.033-second calibrated limit. The newly observed P832b goal-funding overrun and its isolated results are in the flake ledger.

The final `tools/run-tests.sh --tier fast --no-build` sweep ran **16,267 cases**, with **16,255 passed, 5 failed and 7 skipped**. Both quarantined sets passed, as did all ClientCore Fast assertions. Test infrastructure checks passed. In addition to the two HUD failures, three existing Core tests failed.

| Method | Full-sweep result | Method-only repeats |
|---|---|---|
| `P855TrapHackTests.Out_of_range_wing_id_emits_one_traphack_event_and_flood_is_rate_limited` | Nine events against a maximum of eight | 3/3 passed |
| `NpcWingmanDefendLiveTests.Defender_switches_to_closer_threat_near_charge_while_chasing_distant_one` | Selected a different ship instance | 3/3 passed |
| `BotFlightTests.Bot_trajectory_matches_independent_ExecuteMove_rollout` | 3.58 units of position drift at tick 92 | 3/3 passed |

Six successful case executions exceeded the final 13.152-second calibrated time limit. These covered SC2 support selection (two civs), P796 varied purchases, P814 turret-carrier preference, P750 research asteroid models and P999 civ strategy. All five corresponding methods passed in three method-only runs. The ledger contains prior entries for these timing and Core assertion signatures. The full Fast gate remains red on this machine.

Work took about 51 elapsed minutes, including simultaneous local games and test runs. NuGet packages, HTTP cache, XDG configuration, local raw match transcripts, mutation builds and qualification receipts are under `.scratch/p1104/`. No deployment preset was changed.

## P1104b review fixes

### Brief items

| Item | Status | Evidence | Time (UTC) |
|---|---|---|---|
| 1. Legacy wave coexistence | done | Seven-tick replay and concurrent five-pilot wave passed. | 13:29 |
| 2. Bounded search | done | Count, cache, greedy fallback and round-limit assertions passed. Loaded p99 ≤0.402 ms across three fixtures. Wall-time maxima exceeded 1 ms. | 13:34 |
| 3. Stale orders | done | Seven order-lifecycle cases passed, including a newly accepted attack on the same station. | 13:29 |
| 4. Outcome attribution | done | Six sortie cases and six owner-state cases passed. | 13:29 |
| 5. Support routing | done | Concurrent wave stayed in sector 3 while the raid attacked sector 2. A raid alone left support inactive. | 13:29 |
| 6. Option-off parity | done | Both option settings reproduced the pre-P1104 SC8 transcript. | 13:29 |
| 7. Fast tests and negative controls | done | All 34 raid cases passed three times in method isolation. All 25 independent deliberate breaks produced assertion failures. | 13:39 |
| 8. Matched local games | done | Six pairs completed on the final build. The 46,512-byte receipts reproduce every match row, including both 5v5 windows. | 13:51 |
| 9. Report | done | Source locations, two timing runs, negative controls, recomputed match tables, recommendation and final gate exceptions recorded. | 13:59 |

The pre-P1104 commander at `22a595651` produced the seven-tick reference transcript in the Fast fixture. It includes a two-member timeout commit, late pilots continuing to rally, a members-gone edge and a three-member full commit. The first development arms started at 13:17 UTC. The final gameplay build started its six pairs at 13:33 UTC.

### Changes and decision evidence

Paths in this section are relative to `tools/allegiance-mcp/` unless a test path is given. These are commander policies over the existing native purchase, visibility, station damage and individual-order rules tracked in `docs/parity/p819-bot-policy.csv`.

- **Legacy waves and support.** `ResearchTactics.cs:234` releases old assignments before making new ones. At line 249 only the active raiders and their restart defender claim order slots. The remaining strike pilots and roster enter SC7/8/9 on the same tick, including when the raid uses both command slots. A partially reassigned wave retains its surviving members. `BotManager.SustainableRaid.cs:54` supplies the side-adjusted station profile through the raid observation, preserving the original legacy staging inputs. Support still reads the committed legacy wave and its sector at `ResearchTactics.cs:165`. An ordinary raid qualifies on its own mounted volley and already has a separate home defender. Redirecting repair pilots would change a second treatment.
- **Bounded selection.** `SustainableRaid.cs:60` caches examined station IDs for one observed health batch and rotates the starting station when a side exhausts its allowance. It scans at most four eligible stations per pass. Pilot IDs are unique within a candidate. At line 120 increasing-size sets of sorted candidate indices give a stable enumeration across unequal travel times. The last of twelve checks is available for a strongest-first group with compatible arrivals and a remaining defender. The same group is replayed once if both strategies select it. A maximum of 768 rounds can enter the damage replay in one side tick. The exact search has the first 384 rounds available and the greedy candidate can use the rest. At `SustainableRaid.cs:210` a priority queue merges mount streams by arrival-adjusted fire time, power and mount order. The replay consumes a chronological prefix and stops on a kill. Each admitted shot uses the shared station damage and regeneration code at `StrikeVolley.cs:318`. Every launch still requires the replay to destroy the station. A limited search can miss a feasible wing or send more pilots than the global minimum.
- **Expired orders.** `SustainableRaid.cs:36,285` records issued orders and releases each once, including an individual departing a continuing raid. `BotManager.SustainableRaid.cs:68` retains an identical current assignment and stamps other expiries with their epoch, mission generation and acceptance sequence. `BotManager.Orders.cs:57` consumes a pending expiry before looking for an accepted order. A newer acceptance invalidates it at `BotManager.cs:2371`. `BotManager.Lifecycle.cs:45,135,276` clears it on removal, life reset or an observed epoch/generation change. The test also interleaves a late publication of an old expiry with acceptance of an identical replacement.
- **Member evidence.** `BotManager.ResearchTactics.cs:35` samples the owner's world before filtering pilots for command eligibility. `BotManager.SustainableRaid.cs:43` recognizes a kill/pod or explicit damage-recovery return and separately identifies docking, boarding and finale reservations/refits. `SustainableRaid.cs:225` remembers each departure for the sortie and detects a death-counter increment between samples. A missing pilot remains unresolved. All members must have positive loss/turnback evidence for repulse. A changed owner is capture, a dispatched launcher's destruction is a credited kill, and another launcher's destruction or a finale takeover releases the sortie as withdrawn. The time limit or GAME_OVER closes an unresolved sortie as timeout.
- **Per-side gating.** `BotManager.Research.cs:54,61,164` keeps the opportunity by side and briefing epoch and requires ordinary research with compatible goals. `tests/Allegiance.ClientCore.Tests/P1104bRaidManagerTests.cs:133` uses one manager configured through the backfill option, four owner sessions and side-filtered tactical snapshots. A replacement combat pilot cannot reset the completed side, the other side can still launch, and changing the first side's epoch leaves the second side's wing intact.

The replay at `tests/Allegiance.ClientCore.Tests/P1104SustainableRaidTests.cs:203` uses the output of the compiled `ResearchTactics` and `StrikeLogLine` sources extracted with `git show 22a595651:<path>`. Both the ordinary option-off call and an enabled side without a qualifying raid match the ordered commands, wave edges, support activity and sector at every sampled tick.

### Search measurements

The Release microbenchmark alternated two side policies with 130 eligible pilots and one separate home guard per side. All five visible stations were reachable. Each pilot's travel offset differed by 0.001 s from the next pilot. Each mode used 1,000 warm-up calls per side and 5,000 measured calls per side. Fresh samples advanced the observation stamp every tick. Cached samples reused a stamp after all five stations had been examined. Measurements cover the side's `Ready` call, including candidate construction, ranking and damage replay. The commander’s observation collection and legacy orders run outside this timing interval.

For replication, station armor and shield each start at 100. The weak-armor case uses 21 one-second shots of power 12, armor/shield multipliers 0.00001/1 and regen 2/3. The 17-pilot case uses 21 one-second shots of power 0.59, unit multipliers and regen 20/30. The rapid case uses four mounts of 400 shots, interval 0.05 s, power 0.0078, with the same unit multipliers and regen. Each owner has an independent travel estimate to all five stations. Sorted `Stopwatch.GetTimestamp()` differences supply the nearest-rank percentiles.

The candidate-count-only build took 0.381 ms median, 0.750 ms p99 and 2.511 ms maximum on a 17-pilot failed replay during concurrent work. That result prompted the additional round limit. With both limits, under concurrent matches, builds and tests:

| Failed-replay input | Checks / rounds, peak | Median (ms) | p95 (ms) | p99 (ms) | Observed max (ms) |
|---|---|---:|---:|---:|---:|
| 21 rounds per pilot, weak armor damage | 12 / 592 | 0.058 | 0.067 | 0.200 | 2.292 |
| 21 rounds per pilot, regeneration defeats a 17-pilot volley | 4 / 768 | 0.101 | 0.116 | 0.348 | 5.153 |
| Four 400-round mounts per pilot, high regeneration | 2 / 768 | 0.109 | 0.130 | 0.401 | 7.432 |

All three repeated-picture runs examined zero candidates and replayed zero rounds. Their p99 was below 0.001 ms. The maxima above include pauses in wall time. The hard guarantees are the work counts.

After the matches and both Fast sweeps finished, another 10,000 fresh samples per mode measured elapsed time and calling-thread CPU time (`CLOCK_THREAD_CPUTIME_ID`).

| Failed-replay input | Elapsed p99 (ms) | Elapsed max (ms) | Thread CPU p99 (ms) | Thread CPU max (ms) |
|---|---:|---:|---:|---:|
| Weak armor damage | 0.059 | 0.215 | 0.059 | 0.207 |
| Regeneration defeats a 17-pilot volley | 0.111 | 3.105 | 0.111 | 0.378 |
| Four rapid-fire mounts per pilot | 0.123 | 0.389 | 0.123 | 0.390 |

The worst measured CPU cost in this run was 0.390 ms per side. The 17-pilot fixture still had a 3.105 ms elapsed outlier. The sub-millisecond target held at p99 under both loads, with occasional longer elapsed ticks.

### Fast tests and deliberate breaks

On the final gameplay build, **113 directly affected Fast methods** ran three times, one method per test process. All **462 case executions** passed, including all 34 raid cases on each repeat. Coverage also included legacy orders, wave membership and staging, volley replay, support, life/mission refresh, grant handling and side selection. The negative controls below used a disposable source copy. Each listed change was applied separately and failed the named fixture with an assertion.

| Fixture stem | Deliberate changes and observed failures |
|---|---|
| `Strike_decisions_match_the_pre_raid_commander` | Disable the timeout quorum and both transcript arms fail. Suppress strike pilots whenever a raid observation is supplied and just the enabled arm fails. |
| `A_partial_raid_leaves_the_committed_wave` | Return the raid's sector from the support accessor and the sector assertion fails. Remove the shared-budget check before the legacy strike loop and the order-count assertion fails. |
| `Dense_failed_replays_share_one_budget` | Raise the enumeration limit past the side cap, revisit cached stations, omit the greedy fallback, or remove the round-prefix bound. Each break fails its respective count, cache, feasible-wing or round assertion. |
| `Smallest_mixed_granted_volley` | Count duplicate observations of one owner as two attackers. A single magazine then qualified as a two-ship wing. |
| `Outcome_requires_observed_member_or_target_evidence` | Rename the terminal event and all six outcomes fail. Treat an empty snapshot as repulse and all six fail at the intermediate missing-observation step. |
| `P1104b_Owner_fit` | Record a station type as its home ID, ignore finale reservation, accept an incomplete aleph route, or drop the time required to shoot after arriving. Each change fails the owner-world fixture. |
| `P1104b_Backfill_sides` | Store both policies under side zero, ignore an epoch change, or allow the conquest briefing through ordinary-raid gating. Each break fails against the same manager and its replacement seat. |
| `P1104b_Tactical_snapshot` | Discard the per-owner observation while filtering the tactical roster. Docked, reserved, refitting, podded, killed and damage-return cases all lose their evidence. |
| `P1104b_Order_expiry` | Omit publication (six applicable cases fail), leave expiry pending on fresh acceptance, ignore the acceptance sequence during a raced publication, return before consuming a null-command expiry, expire an identical outgoing replacement, or omit epoch/generation cleanup (both rotation cases fail). |

The order cases exercise both the original attack and its home defend order. The replacement case accepts the same command, type and station through the owner's queued-command handler before the old expiry reaches the flight driver.

### Match evidence and reproduction

The final comparison repeats all six historical pairs, including the two delivery-fleet pairs. The first three presets are 10v10 four-sector SingleRing. Seed 11044 is the **5v5 single-sector Brawl** preset. Seed 11045 is 10v10 delivery Brawl and 11046 is 10v10 delivery SingleRing. Each arm used a fresh local server and bot manager. Within a pair both arms had the same combat seats, civs 25/3, home slots, generated geometry, 20 ms pacing and clock settings described above. Odd-numbered pairs ran control first. Even-numbered pairs ran treatment first. Independent pairs ran concurrently.

Use the P959 proof command in the original report, substituting the pair's seed, map and count. Use the delivery-fleet flag and longer clock for 11045/11046, and the civ-strategy flag for both 11043 arms. Only the treatment receives `--sustainable-raid`. The side plan names at GAME_OVER verify which pair actually ran the civ strategy.

The compact receipts include the pre-P1104b windows, both development builds and the final qualification build. For each arm they retain the validated combat ship IDs by seated side, geometry digest and boot homes, assembly hash, mission start, bounded-finale arm line, score and decider, station change and lethal shooter evidence, legacy commit edges, raid stages and the first effective hit. Distinct groups keep every attempted match on its own code version. Legacy commits count the ordinary numbered `wave=N commit` edges throughout the match. A hit can come from any teammate. A credited raid kill requires the dispatched wing's launcher on its assigned station.

Run the table generator from the repository root.

```sh
python3 tools/p1104b-raid-report.py
```

[Compact match receipts](FIRST_SUSTAINABLE_ATTACK_P1104_assets/p1104b-receipts.json) · [Table generator](../../tools/p1104b-raid-report.py)

### Fixed-build match windows

| Seed, combat seats | Arm | Winner, end / decider | Kills S0/S1 | Pre-finale S0/S1 | Legacy commits S0/S1 (pre-finale) | Raid launches and outcomes | First useful hit (s) | Starting homes lost S0/S1 |
|---|---|---|---|---|---|---|---:|---|
| 11041, 10v10 | OFF | S1, cap / ship kills | 0/0 | 0/0 | 1/0 (1/0) | 0 | — | 0/0 |
| 11041, 10v10 | ON | S0, cap / station kills | 1/0 | 1/0 | 1/0 (1/0) | 0 | 722.0 | 0/0 |
| 11042, 10v10 | OFF | S1, cap / ship kills | 0/0 | 0/0 | 2/0 (2/0) | 0 | — | 0/0 |
| 11042, 10v10 | ON | S1, cap / ship kills | 0/0 | 0/0 | 1/0 (1/0) | 1 S0 repulse | 531.75 | 0/0 |
| 11043, 10v10 | OFF | S0, cap / station kills | 2/1 | 2/1 | 5/1 (5/1) | 0 | 433.0 | 0/0 |
| 11043, 10v10 | ON | S1, cap / station kills | 1/2 | 1/2 | 1/4 (1/4) | 1 S0 credited-kill | 533.65 | 0/0 |
| 11044, 5v5 | OFF | S0, deadline / ship kills | 0/0 | 0/0 | 0/0 (0/0) | 0 | — | 0/0 |
| 11044, 5v5 | ON | S1, deadline / ship kills | 0/0 | 0/0 | 0/0 (0/0) | 0 | — | 0/0 |
| 11045, 10v10 | OFF | S0, cap / station kills | 4/0 | 4/0 | 19/2 (19/2) | 0 | 704.15 | 0/0 |
| 11045, 10v10 | ON | S0, cap / station kills | 4/0 | 4/0 | 9/2 (9/2) | 2 S0 repulse, S1 repulse | 503.9 | 0/0 |
| 11046, 10v10 | OFF | S0, cap / ship kills | 3/3 | 3/3 | 6/4 (6/4) | 0 | 642.5 | 0/0 |
| 11046, 10v10 | ON | S1, cap / ship kills | 2/2 | 2/2 | 3/3 (3/3) | 2 S0 timeout, S1 withdrawn | 702.6 | 0/0 |

### Before and after

| Build and fleet | Arm | Games | Station kills | Pre-finale kills | Legacy commits | Raid launches | Credited raid kills | Repulse | Withdrawn | Timeout | Games with a useful hit | Homes lost |
|---|---|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|
| P1104 preset | OFF | 4 | 1 | 1 | 8 | 0 | 0 | 0 | 0 | 0 | 2 | 0 |
| P1104 preset | ON | 4 | 0 | 0 | 3 | 4 | 0 | 4 | 0 | 0 | 2 | 0 |
| P1104b preset | OFF | 4 | 3 | 3 | 9 | 0 | 0 | 0 | 0 | 0 | 1 | 0 |
| P1104b preset | ON | 4 | 4 | 4 | 7 | 2 | 1 | 1 | 0 | 0 | 3 | 0 |
| P1104 delivery | OFF | 2 | 9 | 9 | 20 | 0 | 0 | 0 | 0 | 0 | 2 | 0 |
| P1104 delivery | ON | 2 | 10 | 10 | 18 | 4 | 1 | 3 | 0 | 0 | 2 | 0 |
| P1104b delivery | OFF | 2 | 10 | 10 | 31 | 0 | 0 | 0 | 0 | 0 | 2 | 0 |
| P1104b delivery | ON | 2 | 8 | 8 | 17 | 4 | 0 | 2 | 1 | 1 | 2 | 0 |
| P1104 all | OFF | 6 | 10 | 10 | 28 | 0 | 0 | 0 | 0 | 0 | 4 | 0 |
| P1104 all | ON | 6 | 10 | 10 | 21 | 8 | 1 | 7 | 0 | 0 | 4 | 0 |
| P1104b all | OFF | 6 | 13 | 13 | 40 | 0 | 0 | 0 | 0 | 0 | 3 | 0 |
| P1104b all | ON | 6 | 12 | 12 | 24 | 6 | 1 | 3 | 1 | 1 | 5 | 0 |

The standard fleet recorded three control and four treatment kills. One of those four came on 11041, where no raid launched. On 11043, pilot 8112 hit assigned station 9195 at simulation second 725.30 and destroyed it at 741.30. S0 still lost on station kills, 1–2. On 11046, the server credited pilot 8105 with destroying S1's assigned target. S1's sole assigned raider was 8103, so the sortie ended as withdrawn with zero members lost. The six sorties produced two shooter-confirmed first hits, one credited kill, three repulses with positive loss evidence, one withdrawal and one timeout. None ended in capture.

Across the final 12 games, five had zero station kills, ten ended at the match cap and both 5v5s ended at the bounded finale deadline. No side lost a starting home or its last restart. All 64 ordinary legacy commits preceded the finale arm. The seven-tick decision transcript isolates the nonqualifying commander's behavior. A whole-match wave-count difference can also reflect changing pilots, available stations and combat outcomes.

The receipts retain all 20 P1104b development games. On the first four pairs the controls scored four station kills and the treatments zero, with seven scoreless games and two treatment repulses. On the subsequent six candidate-cap pairs the controls scored seven and the treatments ten, including two treatment kills after the finale arm. Five games were scoreless, and one treatment lost a starting home. These runs used earlier gameplay builds and have separate denominators in the JSON.

### Recommendation

Keep `sustainable_raid` **off by default**. An ordinary preset pilot can now kill its assigned station while legacy waves continue. The six fixed-build treatments nevertheless finished one station kill behind the controls. The preset and delivery fleets moved in opposite directions. Each fleet, seed and arm has a single observation, the controls themselves varied between builds, and both sides share the option. This sample cannot establish a win-rate, conquest or home-defense benefit, or predict results for other civilizations, workers or unaccelerated clocks.

### Build and test gate

Both Debug and Release builds of `AllegianceCore.slnx` finished with zero warnings and zero errors. `tools/build-tools.sh --drop-bins` built all 159 tool projects. The glossary checker validated 1,219 paths, the parity scorecard ran successfully, and the match table generator reproduced the report from the receipts. The baseline checkout was `62591b1bf`; the 12 final arms all ran bot assembly SHA-256 prefix `C1BC2342ABBADC6B`.

The earlier full Fast run on the candidate-cap build reported three failed cases, seven skips and eight timing overruns at a calibrated 14.272-second limit. The launcher shop receipt passed three isolated repeats. Both 1920×1080 HUD pixel cases reproduced on all three repeats. All six over-limit methods passed their assertions on all three repeats. The launcher receipt and software-renderer hashes are recorded in the [test flake ledger](../TEST_FLAKES.md).

The final full Fast invocation ran 16,306 cases in 836 seconds. It recorded 16,291 passes, eight failures and seven skips. All 2,590 ClientCore cases passed. The calibrated limit was 13.311 seconds. Five executions exceeded it, including the failed asteroid fixture.

| Final Fast exception | Observed in the full sweep |
|---|---|
| P1091 HUD default scale at 1920×1080 | Classic and Modern differed from the stored whole-image hashes. |
| P813c reconnect | Parked-seat lookup raised a null reference at `P813cReconnectGraceTests.cs:845`. |
| P467 gun development | Single-round enemy damage receipt missing. |
| NPC wingman standby | Unopposed wingman moved 16.931 units. |
| Carrier resupply and relaunch | Relaunch message cookie was 2 while the lander's pose cookie still read 1 at `LiveRelaunchTests.cs:201`. |
| Ballot resignation | The passed-ballot chat arrived before the expected side-elimination chat. |
| Asteroid lethal hit | Missed the cross-side destruction receipt and took 19.079 s. |
| Timing-only cases | SC2 support civ 25 took 15.344 s, P796 varied loadout civs 25 and 35 took 13.431 and 20.585 s, and P814 gunner took 17.320 s. |

All ten failing or over-limit methods ran three times in separate test processes. Six Core failure cases passed all three repeats, as did every timing-only case. The new carrier cookie signature is in `docs/TEST_FLAKES.md` under “P1104b Fast sweep.” The other signatures match existing ledger entries. The HUD fixture repeated both failures each time and its other 19 cases passed each time. Classic produced hash prefix `C100F69A43C7A4E8` and Modern `1093B39B5B9BA161`, matching the archived baseline evidence. The full Fast gate remains red. The working tree contains the fixes, tests, receipts and report for review.

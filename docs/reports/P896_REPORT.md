# P896 — finale strikes: false "station-killed", waves that survive, and why one side never reaches the garrison

Base: local `master` at dispatch (`73b375bc`). Work is in the P896 worktree; nothing is committed or pushed.
Every native reference below is relative to `~/Development/Allegiance/src/` and was re-read for this report.
Port references are line numbers in the working tree at the end of this task.

| Part | Verdict |
| --- | --- |
| 1 — false `station-killed` | **Bug, fixed.** `RearmRotatedFinales` unioned every side's station table and `RegisterFinale` then handed a side a station only the ENEMY's world held; `UpdateFinales` read "not in my table" as "destroyed", which under P797 fog means "not observed". Both halves fixed (own-observations-only targeting + real death evidence) with 5 regression tests, 4 of which fail on master. |
| 2 — waves that survive | **Setting added, default OFF** (`finale_survival_margin`). 0 reproduces the P801/AIX wave exactly (test asserts equality); above 0 the wave carries `ceil(margin × max(last-wave losses, defenders seen in the target sector))` extra bombers, bounded by `Settings.Bombers`/`MaxPlannedBombers`. Locally measured A/B below. |
| 2b — why waves recommit at full health | **Answered from code + run:** the released wing dies before its magazine can land (14/14 released bombers killed in the margin-0 run), rounds that do land are spaced too widely to beat the 50 s half-shield refill, and — a real defect found on the way — a side whose bomber/anti-base tech gate is not met yet flew unarmed "bombers": 86 of 86 (margin-0 run) and 178 of 179 (earlier run) `no buyable bomber strike loadout` refusals were **side 1**. Fixed: a wave with no station-damaging loadout is no longer committed. |
| 3 — why side 1 never reaches side 0's garrison | **Not a route/position or side-id defect.** The rig map's home↔home route is exactly reciprocal (3 hops each way, 2 alternating 3-hop paths each way, 4 alephs per home, same garrison class — measured from the map). The reproducible cause is the unarmed-wave defect above (side 1 = Ga'Taraan reached its bomber/anti-base tech late in the local runs) plus wave attrition; both sides DO kill garrisons in runs where the loadout exists. Not a code path keyed on side id; the one `side == 0` branch is the human commander of the offline Skirmish host (`BotManager.Research.cs:20`), which is intended. |
| 4 — deadline ranking | **Proposal only**, no code: showcase-only station-kill criterion, or zero-deviation presentation/telemetry options, with expected effect and native deviation each. |

---

## Part 1 — the false `station-killed`

### Root cause (master `73b375bc`)

Two defects, chained:

1. **A side was handed a target it had never observed.**
   `tools/allegiance-mcp/BotManager.Finale.cs:415-451` (master) built ONE dictionary of
   `cs.World.Stations` from every participating bot, i.e. **every side's** view, and passed that union to
   `RegisterFinale`. `RegisterFinale` (`:186-196`) then picked, per side, the best enemy station **out of the
   union** — which the P797 discovery model does not allow: a station reaches a client only once its own side
   has seen it (`Fsmission.cpp:4169-4250`). After the P831 fog grace (`:451`, `LogRearmGrace(..., gate: ...)`
   then `gate = null`) this is guaranteed: the grace exists precisely for a side that has seen nothing.
2. **Death was inferred from absence.** `:585-587`:
   `if (run.TriggeredAt is not null && … && !cs.World.Stations.ContainsKey(plan.TargetId)) plan.KilledAt = now;`
   Under fog an unobserved station is simply not in the table, so a side's first tick after the trigger marked
   its (unobserved) target dead. That is b59 M5's
   `[strike] side=0|1 finale end … outcome=station-killed hitsInWindow=0 hull=-1->-1` for both sides while
   neither garrison had been touched, in the same run that logged
   `re-arm grace … no enemy station observed by side(s) [0,1]`.

### Fix

* **Own observations only** (`BotManager.Finale.cs:239-270`): `ObservedFinaleStations(side, bots, fallback)`
  returns what that side's OWN bot worlds hold; the caller's fleet-wide union is used **only** when a side has
  no readable world at all (fixture/driver), never to top up a readable but blind side.
  `PickFinaleTarget` keeps the P831 preference (restart-capable first, then station id).
* **A legal "no target yet" state** (`:214`, `:275-308`, `:793-802`, `:831`): a blind side is registered with
  `TargetId = -1`, logs it once, gets **no** strike commitment at the trigger, and picks a target the moment
  one of its own worlds reports one (`ResolveFinaleTargets`, once a second under the run lock). A side whose
  target arrives after the trigger is armed through the extracted `ArmFinaleSide` (`:313-333`), so the mass,
  budgets and telemetry are identical to the ordinary trigger path. Every consumer of the plan is guarded
  (`:831` relaunch, `:1051` escort transit, `StrikeMass.cs:135` mass tick, `:373` standoff hold).
* **Death needs evidence** (`ClientWorld.cs:399,1357,3726`; `BotManager.Finale.cs:711-725`): `ClientWorld` now
  records the stations it received a real `FM_S_STATION_DESTROYED(59)` for (the server broadcasts it to the
  WHOLE mission, `src/Allegiance.Server/Server.Combat.cs:956`; native `Messages.h:436-439`) and exposes
  `StationWasDestroyed`. The finale marks `KilledAt` only when the target's world table lacks it **and**
  either that broadcast arrived (`evidence=destroyed`) or the side had really observed it and then lost it
  (`evidence=observed-lost`) — the reconnect/table-rebuild case. The `[strike] … finale end` line now prints
  `evidence=` so a future rig log cannot repeat b59's ambiguity.

### Tests (fail on master)

`tests/Allegiance.ClientCore.Tests/P896FinaleEvidenceTests.cs`:

| Test | On master |
| --- | --- |
| `P896_A_side_is_never_armed_with_a_station_it_has_not_observed` | **FAIL** — side 1 gets `100` (the enemy's own garrison) instead of "no target" |
| `P896_Rearm_never_arms_a_blind_side_with_an_unobserved_station` (through the real `RearmRotatedFinales` grace path) | **FAIL** — side 1's target is `200`, never observed |
| `P896_Fog_absence_never_reports_the_target_as_killed` | **FAIL** — the blind side keeps an unobserved target (and the false kill follows) |
| `P896_A_kill_needs_observed_then_lost_or_a_destroyed_broadcast` | **FAIL** — `KilledEvidence` does not exist on master (NRE) and absence alone marked a kill |
| `P896_Station_absence_is_not_death_and_destruction_is_remembered` | passes on master only if the new accessor is back-ported; it covers the new evidence API (destroyed for an id the world never exported, cleared by `Reset`/`BeginNextMission`) |

Baseline proof: master's three production files (`BotManager.Finale.cs`, `BotManager.StrikeMass.cs`,
`BotManager.cs`) were restored from `git show HEAD:` **plus** only the `StationWasDestroyed` accessor (which
nothing on master reads), and the tests were run: **4 failed / 1 passed**
(the raw `.trx` was dropped at review: it held local paths). The failure messages are exactly the b59 bug:
`Expected: -1 Actual: 100` and `Expected: -1 Actual: 200`.

---

## Part 2 — a wave that can survive (behind a setting, default OFF)

### The setting

`ShowcaseFinale.Settings.SurvivalMargin` (`ShowcaseFinale.cs:11-30`), MCP argument
`finale_survival_margin` (`McpTools.cs:98` schema, `:1415` reader), validated `0..2`, default **0**.
At 0 the requirement is byte-for-byte the P801 arithmetic — asserted, not assumed:
`P896_Margin_zero_is_exactly_the_todays_wave_arithmetic` compares `Compute(..., margin 0, losses 9,
defenders 9)` with the plain call and asserts the P801 numbers (kill 4, required 5, survival 0) on real
PCore001 garrison 315 data. The commit log line also grows only when the margin is on
(`StrikeMass.cs:333-336`), so a default rig deploy stays AIX-comparable.

### The arithmetic

`StrikeVolley.Compute` (`StrikeVolley.cs:150-210`) keeps its bare requirement `kill + 1 spare` and adds

```
survival = margin <= 0 ? 0 : ceil(margin × max(lastWaveLosses, defendersSeenInTargetSector))
required = clamp(kill + 1 + survival, 1, min(roster, Settings.Bombers, MaxPlannedBombers))
```

* `lastWaveLosses` = this side's finale bombers podded since its previous commit (`plan.WaveLosses`,
  incremented in the pod path `BotManager.cs:3631-3635`, reset at commit `StrikeMass.cs:321`).
* `defendersSeenInTargetSector` = live enemy ships **this side can see** in the target sector
  (`FinaleDefendersInSector`, `StrikeMass.cs:103-121`) — fog-honest, never a shared view.
* The budget is the side's reserved wing, already capped by `Settings.Bombers`
  (`FinaleVolleyRequirement`, `StrikeMass.cs:88-101`), and `MaxPlannedBombers` keeps the mass assembleable
  inside P801's staging cap (180 s) rather than out-waiting the deadline.
* The requirement never falls below the bare kill, and `SurvivalBombers` reports the extra that was actually
  granted after clamping.

### Why waves recommit against a full-health station — from the code and a local run

Three candidate mechanisms, checked:

1. **Losses en route — confirmed, dominant.** The released wing is killed in the target sector before its
   rounds land. Margin-0 native-fog run (`run-training6-margin0.json`, pre-guard): **14 releases, 14 killed**
   (`ReleaseOutcomes`),
   and the garrison log stays at `stationHull=1.000`. The escort fight decides it, which is exactly what a
   survival margin is for.
2. **Spent magazines — real but secondary.** A released bomber falls through to ordinary combat
   (`HoldFinaleStrike` returns false after its go time, `StrikeMass.cs:398`), and the combat selector can
   point its anti-base launcher at a ship: a released finale bomber in the earlier local run locks
   `target 8130` with `mis05` instead of the station.
3. **Shield regeneration between waves — the reason it never accumulates.** Native regen is armour first,
   shield only at full armour (`Igc/stationIGC.cpp:192-210`), so a garrison that takes only 1-3 rounds
   refills half its shield in 50 s (`StrikeVolley.ShieldRefillSeconds`). Measured:
   `maxHitsIn20s` was 2 (side 0 → 2081) and 1 (side 1 → 2080) in the margin-0 run — vs the **7 rounds** the
   arithmetic says a 20k/10k garrison needs inside one window. Every recommit therefore starts from a
   regenerated shield.

**A fourth mechanism turned out to be a bug, not a balance question** (see Part 3): a side whose bomber /
anti-base tech gate was not met yet committed waves of ordinary fighters relabelled "bomber". The P801
planner already knows such a loadout cannot hurt the station (`LoadoutCount == 0`), but the finale ignored it
and fed the defenders free kills, leaving `stationHull=1.000` for the next wave. Now such a wave is not
committed at all (`StrikeMass.cs:174-186`), with a bounded `[strike] … finale holding: none of N armed
bomber(s) carries a station-damaging loadout` line; the deadline is untouched, so the side strikes the moment
a real anti-bomber upgrade lands.

### Tests

`tests/Allegiance.ClientCore.Tests/P896SurvivalMarginTests.cs` (7 cases): margin 0 == today's numbers (and the
extra inputs are ignored); sizing from losses; sizing from defenders; 1.0 defender-dominated; rounding up;
never above the reserved wing; never above `MaxPlannedBombers`; settings default/validation/MCP parsing.
`P896_A_wave_with_no_station_damaging_loadout_is_not_committed` (in the evidence file) covers the hold.

---

## Part 3 — why one side never reaches side 0's garrison

### The route is not asymmetric (measured, not assumed)

From the real map at boot (`--geometry-only`, `docs/reports/P896_assets/routes.txt`), rig map
`m2xspiderw.igc`:

| | side 0 home | side 1 home |
| --- | --- | --- |
| garrison | `#4328` Ganymede (1050) | `#4329` Organia (1051) |
| alephs | 4 | 4 |
| route to the enemy home | 3 hops, **2** distinct 3-hop paths (`1050→1054→1055→1051`, …) | 3 hops, **2** distinct 3-hop paths (`1051→1055→1054→1050`, …) |

The two homes are the same station class, the hop counts match in both directions, and the alternating path
count matches. This is consistent with P826's finding ("the main siege route is reciprocal").

### The side-keyed code audit

`grep` over `tools/allegiance-mcp` for side literals finds exactly one behavioural branch:
`BotManager.Research.cs:20` `HumanCommands(short side) => side == 0 && !string.IsNullOrEmpty(_humanCommanderName)`.
That is the offline Skirmish host's human commander (`ALLEGIANCE_SKIRMISH_COMMANDER`,
`src/Allegiance.SinglePlayer/SkirmishHost.cs:97`), where the human **is** on side 0 — intended, not a bot
bias; bot-only rig runs never set it. Everything on the strike path is side-agnostic and symmetric:
finale target choice (`PickFinaleTarget`), route assignment (`AssignResearchSiegeRoute` +
`FinaleRouteIndex`), staging/standoff, escorts, the push, defenders and the volley planner.
One soft asymmetry worth naming (not fixed, no measured effect): `BotManager.Research.cs:68` serves the
probe's side **last** in the background research loop
(`OrderBy(s => s == ProbeSide ? 1 : 0)`), so under a starved loop that side's research can lag. Reordering it
would change the research cadence that several live tests pin, and nothing in this task's evidence ties it to
a zero-hit garrison; it is reported instead of changed.

`WorkerPartition.Select` splits **per side** (`WorkerPartition.cs:15-25`), so a worker never gets one side's
whole share — the P819 "15:3 selection opportunity" was a per-worker finale count, not a routing defect.

### What the run shows instead

Same-map, same-code locals on `training_6.igc` (symmetric 2-hop map, `run-training6-*.json`):

* Both sides reach, hit and **kill** garrisons when their finale loadout exists: side 1 destroyed side 0's
  garrison `#2080` and five more side-0 stations in the margin-0.5 run; side 0 landed AB2 hits on side 1's
  `#2081` (shield 1.0 → 0.08).
* The failing side in the margin-0 run was side 1 for a **loadout** reason: every
  `[finale] side 1 … no buyable bomber strike loadout: no bomber hull passes the side + station tech gate`
  line in that run (86 of 86) belongs to side 1 = Ga'Taraan (civ 3), whose research plan reaches the bomber
  hull / anti-base missile later. Those waves committed unarmed and produced zero station damage, which is
  the b59 signature (`required=2..5`, `stationHull=1.000`, no hits on 4328).

So: b59's asymmetry is **matchup + wave attrition**, amplified by a real defect (unarmed waves committed),
not a position or side-id defect. That defect is fixed (Part 2 above); the matchup part is not something a
bot-side change may paper over, and small local samples must not be read as balance claims.

---

## Part 4 — deadline ranking: proposals only (no code changed)

`Mission.EvaluateCountdown` is untouched (native order `FedSrv/fsmission.cpp:2348-2490`; port
`src/Allegiance.Core/Model/Mission.cs`), as is the deadline. M3 (b59) is the case to fix: side 0 destroyed two
garrisons and most of the base, then lost 901-978 on kills.

| # | Proposal (showcase/skirmish presets only) | Expected effect | Native deviation |
| --- | --- | --- | --- |
| A | **Station-kills criterion**, ranked immediately *after* the native criteria and *before* kills (`fsmission.cpp:2468-2490`), fed by the per-side `StationKills` the port already tracks. Flag default off; the native order is kept when the flag is off. | M3: side 0 wins by station-kills (2 garrisons + base) instead of losing on a 77-kill margin. Directly makes endings varied in the way Domingo asked for. | **Yes** — native has no station-kill criterion; it is an insertion into the native ranking order, showcase-scoped. |
| B | Same criterion but only as a **tie-breaker** between equal kills/deaths (after `fsmission.cpp:2468-2490`, before the draw at `:2494-2503`). | A station-dismantling side no longer loses a *tied* match; a large kill deficit still wins for the other side (M3 stays a loss). | **Yes**, smaller: native goes straight from equal kills/deaths to the draw. |
| C | Enable the **native** `conquest` goal for showcase matches (flag-capable stations, native priority 1, `fsmission.cpp:2346-2370`). | Objective play decides the deadline with zero invented rules; a side holding more flag-capable stations wins. Does **not** reward *destroying* stations, so M3's case would not change. Caveat: `ConfigureFinale` disables the timed finale when the mission has a conquest goal (`BotManager.Finale.cs:87-88`), so this replaces the ending type rather than adding to it. | **None** (a configuration choice among criteria the server already implements). |
| D | Presentation/telemetry only: publish the station-kill tally and "enemy base shattered" in the finale feed and the score overlay; no winner change. | The demolition is visible and no longer reads as a random loss; zero risk to native parity. Does not change the winner. | **None**. |
| E | Shorten `FinaleTimeoutSeconds`/`GameLengthSeconds` for showcase so a side that is ahead on kills cannot run up a 2,000-point margin after losing its base. | Changes *when* the deadline fires, not the ranking; M3 would still be a loss (just closer). | Config only, but it changes the showcase's advertised match length. |

Recommendation for the next batch: **A behind a default-off flag**, with **D** as the always-on companion, since
A is the only option that directly makes "destroyed the enemy base" outrank "more kills" while staying inside
one showcase preset.

---

## Measure — bounded local runs

Harness: new proof tool `tools/p896-strike-proof` (in-process server + real MCP bots, native fog, ordinary
purchases, both finales, `m2xspiderw.igc` or `training_6.igc`, bounded by `--seconds`). Raw logs live outside
the repo (`~/allegiance-evidence/P896/`); the compact summaries are in `docs/reports/P896_assets/`.
No synthetic load was generated; runs were sequential, one match per arm.

Config for the A/B: `training_6.igc`, 15v15 (`size_scale 1`), research showcase, `finale_after_minutes 1`,
`finale_side both`, `finale_bombers 8`, `loadout_mode varied`, wall cap 660 s.

### The four comparable matches (1 per arm per code state)

`g = final code (with the unarmed-wave guard)`; `pre = before that guard`, same harness/config.

| Run | Arm | Sim s | Wave commits (side: required/assembled) | `survival=` | Releases | Released killed | Garrison hits (max in 20 s) | Garrison kills | Ending |
| --- | --- | ---: | --- | --- | ---: | ---: | --- | ---: | --- |
| `t6-m0-b` (pre) | margin 0 | 1471 | 1:1/1, 1:1/1, 0:5/6, 0:1/1, 0:3/3, 1:4/4 | — | 14 | **14/14** | 2081: 3 (2), 2080: 1 (1) | 0 | deadline by kills (side 0) |
| `t6-m05-c` (pre) | margin 0.5 | 1991 | 1:**8**/8, 0:3/3 | `3(margin=0.50,losses=0,defenders=10)`, `0(...,defenders=13)` | 11 | 4/11 | 2081: 2 (2), 2080: 3 (3) + 5 more side-0 stations | **1** (+5 other stations) | side 1's finale killed `#2080` (`evidence=destroyed`), deadline by kills (side 1) |
| `r-f-m0` (g) | margin 0 | 1353 | 0:5/5, 1:5/5, 0:1/1 | — | 10 | **10/10** | 2081: 2 (2), 2080: 2 (2) | 0 | deadline by kills (side 1) |
| `r-f-m05` (g) | margin 0.5 | 1353 | **none** (5 bounded `finale holding` lines: no armed bomber had a station-damaging loadout) | — | 0 | — | 0 | 0 | deadline by kills (side 1) |

What this shows, and what it does not:

* The **mechanism** works and is visible in real logs: waves mass (`assembled = required`, 2 lone wolves
  each), wait 50-67 s at the standoff, release together, and the station health is sampled at every commit
  (`stationHull=1.000` on every recommit — the Part 2b answer).
* **Everything that reached a garrison died on the way**: 28 of 35 released bombers across the four runs were
  killed before their magazine could land, so the best a wave achieved was 2-3 rounds inside a 20 s window
  (needed: 7). The shield refill (50 s for half a garrison shield) therefore always won, which is exactly the
  trickle the P801 report describes — one wave per match is not enough to accumulate.
* The **margin sized a real wave**: `t6-m05-c` committed an 8-bomber wave (base 5 + `survival=3` from 10
  defenders) and it killed a garrison; the opposite side's wave was roster-bound (`survival=0` of a demanded
  7), showing the `Settings.Bombers`/roster bound. Two runs (the exploratory `t6-m0` and `r-f-m05`) never had a
  station-damaging loadout inside the deadline at all, so **a margin A/B on wave size cannot be concluded from
  one match per arm** — the pure arithmetic is pinned by the 7 unit cases instead. No balance claim is made.
* The unarmed-wave guard works live: `r-f-m05` logged 5 bounded `finale holding` lines instead of feeding
  unarmed bombers to the defenders, and `r-f-m0` logged 5 holds before both sides got real 5-bomber wings.

Sample sizes: 1 match per arm per code state (4 matches in the table; 7 matches total including the exploratory
native-fog runs). Raw logs:
`~/allegiance-evidence/P896/runs/`; compact summaries: `docs/reports/P896_assets/run-training6-*.json`.

**Local run disclosure:** the two `r-*` arms used `--reveal-stations`, a *measurement fixture* (the same
`SideVisibility` seeding P733's live finale test uses) because on native fog a 15v15 training-map match often
never sends a scout into the enemy home inside the wall budget — three native-fog runs (`t6-m0`, `t6-m0-late`,
`t6-m05-b`) either never armed the finale or armed it too late to strike. `t6-m0-b` and `t6-m05-c` are
native-fog runs with no seeding.

### Caveats on sample size

One match per arm on a 3-sector training map. Not a balance claim and not a rig A/B (Claude runs that). The
gates that matter for correctness are the deterministic tests; the runs only show that the mechanisms fire and
that the new knobs are visible in real logs.

---

## Gate

| Gate item | Result |
| --- | --- |
| `dotnet build AllegianceCore.slnx` | **PASS** — 0 warnings, 0 errors |
| `tools/build-tools.sh` | **PASS** — all 97 tool projects built (includes the new `tools/p896-strike-proof`) |
| New tests 3× alone | **PASS** — `FullyQualifiedName~P896`: 13/13, 13/13, 13/13 (425-437 ms each) |
| ONE `tools/run-tests.sh --tier fast` | **RUN, FAIL** with **zero P896-introduced failures**: 11,901 tests, 532.6 s. `Allegiance.Core.Tests` had 2 failures — see the ledger below. |
| Showcase never-parks 5× (`P700ShowcaseLiveTests.Local_showcase_never_parks_with_target_for_five_seconds`) | **PASS 5/5** — 7m00s, 5m42s, 3m26s, 3m56s, 3m27s, no failures (sequential, load average 0.3-1.4). |
| Affected existing finale suites (regression check) | **PASS** — `P656ShowcaseFinaleTests`, `P831*`, `P819FinaleOrderTests`, `P733FinaleDutyTests`: 30/30; live `P733ResearchTests` (real server, real rotation, `RegisterFinale` through the live path): 9/9 in 5m22s. |
| AOT publish | **Not required**: no `src/Allegiance.Server` or `src/Allegiance.Core` file changed (only `ClientCore` state + `tools/allegiance-mcp`), per the lane rule. |
| `tools/check-glossary-links.py` | **PASS** — 381 distinct paths |

Fast-tier non-task failures (all ledgered in `docs/TEST_FLAKES.md` with the 3× isolation result):

* `Allegiance.Core.Tests.NpcWingmanDefendLiveTests.Defender_disengages_when_chased_enemy_flees_and_returns_to_charge`
  — fail / fail / fail alone. The long-recorded pre-existing baseline (P872/P884/P887/P888/P890/P894/P895), not
  this lane's path.
* `Allegiance.Core.Tests.DockDockingTests.Ship_nowhere_near_a_station_is_unaffected` — pass / fail / pass alone,
  already ledgered as a status-roster fixture intermittency (P872/P890; same failure signature, `Expected: -1
  Actual: 1051`).
* Two `SLOW FAST` over the calibrated 10 s limit (`P777WarpExitWobbleTests.Npc_exit_touch_rate_is_measured`
  10.663 s, `P750NativeAsteroidTypesTests.Research_showcase_rocks_carry_native_models_and_no_rock_names`
  11.146 s) — both pass 3/3 alone (11 s for the pair), so sweep-load only.

---

## Claude's review corrections (merge)

* **Fixed — false kill on the new late-sighting path.** The first version set `ObservedTarget` from ANY world of the
  side, then the per-bot pass read any OTHER world still lacking the station as `observed-lost`: a repeat of b59 M5
  on exactly the path P896 added, since exports reach a side's bots at different moments. `observed-lost` now needs no
  world of the side to hold the target (`TargetHeldBySide`).
* **Fixed — late-armed side got no loadout.** `ResolveFinaleTargets` passed the enemy-only list to
  `SelectFinaleLoadout`, which needs the side's own stations: every late arm hit `no shop`. Pinned by
  `P896_A_late_sighting_arms_a_loadout_and_a_lagging_teammate_is_no_kill` (failed before both fixes).
* **Fixed — telemetry.** `losses=` in the commit line was printed after the reset, so it always read 0, including the
  `losses=0` quoted in the table above. `FinaleDefendersInSector` scanned every bot's ship table each second even at
  margin 0; it now runs only with the margin on.
* **Part 3 is NOT settled for the rig.** The conclusion above rests on `training_6.igc`. The b59 bot logs do not show
  it: `no buyable bomber` refusals are 9 per side on each of the two workers (18 v 18), against this report's
  86-of-86 for side 1 locally, and side 0 logged 81 finale releases to side 1's 33. Why side 1 releases less and never
  hits 4328 stays open; "matchup + attrition" is not proven on the rig map.
* **The unarmed-wave guard is unconditional**, so the default rig deploy is not strictly the old behaviour wherever it
  fires. It also stops the commit only: `HoldFinaleStrike` still parks those bombers at the standoff in the enemy
  sector, so "no free kills" is not established.

## For Claude (rig) — what to watch

* The rig A/B is yours; this lane changed nothing for human servers (all of it is
  `tools/allegiance-mcp` showcase/skirmish policy + one `ClientWorld` accessor).
* Expected new log lines: `[finale] side N: no enemy station observed yet — armed with NO target`,
  `[finale] side=N observed enemy station#X in sector Y: target assigned (P896)`,
  `[strike] side=N finale end … evidence=destroyed|observed-lost`, and (only with the margin on)
  `survival=N(margin=…,losses=…,defenders=…)`.
* Two acceptance tests to re-confirm on the rig: the P801 acceptance still needs a massed commit
  (`P801StrikeMassLiveTests`) — with the unarmed-wave guard a side holds instead of committing when nobody
  has a station-damaging loadout, so a run where **both** sides are unarmed now logs holds and no commit;
  and M5's false `outcome=station-killed` must be absent (grep `evidence=`).
* Deploy the next rig batch with `finale_survival_margin` **absent or 0** to stay AIX-comparable, then A/B the
  margin on the rig (the local arms show it sizes a wave — `t6-m05-c`: 8 = 5 + 3 — but one match per arm
  cannot show a balance effect).

## Native-intent footer

* **Design (native intent kept).** Native fleets attack a station with a massed bomber wing and escorts, and a
  station dies only when its hit points are gone (`Igc/stationIGC.cpp:159-210`). Everything added here is
  bot policy on top of that: which station a side may target (only one it observed,
  `Fsmission.cpp:4169-4250`), whether a wave is big enough (`StrikeVolley`, unchanged native damage/regen
  maths), and whether a wave is worth committing at all.
* **Era constraint.** Native had no fog-aware finale and no per-side "dead station" evidence: a client that
  never saw a station had no id for it, so "absent" and "destroyed" could not be confused. The port's P797
  discovery model plus P831's bounded grace made that confusion possible, which is why the evidence rule
  (`STATION_DESTROYED` broadcast vs observed-then-lost) is explicit here.
* **Bug.** Three: (1) the union hand-out + absence-as-death (b59 M5, fixed); (2) survivors' wave sizing ignoring
  observed losses (fixed, default off); (3) committing a wave whose granted loadout cannot damage the target
  (fixed). None was a native deviation — they were port-side policy/mistakes.
* **Modern better approach.** Instead of ranking kills to decide a demolition match, drive the showcase from
  the objective the demolition actually changes (Part 4 A/D): keep the native criteria as the default and let
  a showcase preset weigh destroyed enemy infrastructure, with the tally published so a human can see why the
  match ended the way it did.

---

## Files changed

* `src/Allegiance.ClientCore/State/ClientWorld.cs` — recorded `STATION_DESTROYED` evidence,
  `StationWasDestroyed`, cleared on reset/new mission.
* `tools/allegiance-mcp/BotManager.Finale.cs` — own-observations target selection, legal "no target yet"
  state + late re-pick/arm, evidence-based kill, P896 telemetry (`evidence=`, hold line), wave-loss/defender
  inputs.
* `tools/allegiance-mcp/BotManager.StrikeMass.cs` — survival-margin sizing, defender count, unarmed-wave hold,
  `survival=` log field.
* `tools/allegiance-mcp/StrikeVolley.cs` — survival margin in `Compute` + `Requirement.SurvivalBombers`.
* `tools/allegiance-mcp/ShowcaseFinale.cs` — `Settings.SurvivalMargin` (+ validation).
* `tools/allegiance-mcp/McpTools.cs` — `finale_survival_margin` schema + reader.
* `tools/allegiance-mcp/BotManager.cs` — per-plan wave-loss counter on a pod.
* `tools/allegiance-mcp/allegiance-mcp.csproj` — `InternalsVisibleTo` for the new proof tool.
* `tools/p896-strike-proof/` — the bounded local measurement harness.
* `tests/Allegiance.ClientCore.Tests/P896FinaleEvidenceTests.cs`,
  `tests/Allegiance.ClientCore.Tests/P896SurvivalMarginTests.cs` — new tests.
* `GLOSSARY.md`, `docs/TEST_FLAKES.md` — term + ledger updates.

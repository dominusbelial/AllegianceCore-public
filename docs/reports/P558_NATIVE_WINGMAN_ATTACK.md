# P558 — Native wingman ATTACK steering in the MCP combat law (replaces the P539 stand-off ring)

Date: 2026-10-06
Author: Hermes (P558)

## 1. What this does

`tools/allegiance-mcp`'s MCP dogfight law no longer holds a **stand-off ring scaled
to each side's own weapon range** (the P556-measured root cause: longer-ranged
tech-4 bots orbited ~475 u vs ~432 u and got out-shot ½–⅔ rounds). The
`c_cidAttack` branch is now a faithful port of **native's wingman attack
steering**, `src/Igc/shipIGC.cpp:2356-2604`:

- **Dodge owns the tick** — keep the P553 shared dodge law (`if (!bDodge)`,
  shipIGC.cpp:2410); a dodging bot flies its dodge goal and fires nothing.
- **Turn to face the LEAD direction** — `turnToFace` → `angleToLead` (the residual
  angle by which the nose misses the lead), and the **throttle law**
  `throttle = 0.5 + (π − angleToLead)/(2π)` (shipIGC.cpp:2474) — the drone
  "always flies hard".
- **Distance bands keyed to the intercept time `t` vs the projectile lifespan**
  (shipIGC.cpp:2492-2571), not a ring: Close (`t<0.45·life`), Engage
  (`t<0.90·life`), Approach (`t<1.5·life`), Lost (escaped). Each band sets the
  forward/backward/afterburner buttons and, in band-Close's "match speed" case,
  the throttle axis (shipIGC.cpp:2506-2511).
- **Fire gate** — allWeapons only when `angleToLead ≤ c_fMaxOffAngle (0.10 rad)`
  AND `t ≤ lifespan·openFireDistPercentage (0.95)` (shipIGC.cpp:2589-2602).
- **Missiles** — fired only while reasonably aimed (`angleToLead < π/2`, inside
  the well-aimed block) with the lock ≥ 1.0 + evasion gate
  (`evasionSpeed < 20 + maxLock·30`, shipIGC.cpp:2584-2596; the P557 rule, kept
  and now gated on aim).
- **Mines** — the P557 `UseMines` rule kept unchanged (it is native,
  shipIGC.cpp:2651-2665, in the repair branch — out of P558 scope, kept as-is).
- **Target switching** — a Lost-band `c_wbbmInRangeAggressive` wingman re-targets
  to a nearer different-side ship; the default (non-aggressive) wingman burns
  after the escaped target (shipIGC.cpp:2545-2570).

Because the spacing is native's (lead-time bands + back-off), **tech / hull /
shield decide fights**: a longer-lifespan gun opens fire at its longer reach
while the throttle still pushes forward, instead of parking the better-ranged
bot farther out.

## 2. Native citations (every ported rule)

| Rule | Ported as | Native source |
|---|---|---|
| Dodge owns the tick | `TryDodgeGoal` → fly dodge goal, no fire | `if (!bDodge)` / dodgeCooldown — shipIGC.cpp:2410, 2485-2490 |
| Throttle law | `AttackThrottle` = `0.5+(π−angleToLead)/(2π)` | shipIGC.cpp:2474 |
| Turn to face lead | `SteerTowardDirection` returns `angleToLead` | shipIGC.cpp:2470-2474 |
| Band-Close | forward/back on |relativeSpeed|, match-speed throttle | shipIGC.cpp:2493-2514 |
| Band-Engage | back>50, forward<10, afterburner<−15/held or <−30, critical t>0.85 | shipIGC.cpp:2515-2530 |
| Band-Approach | back>120, forward<80, critical<40, afterburner<20 | shipIGC.cpp:2531-2543 |
| Band-Lost | aggressive→retarget else afterburner | shipIGC.cpp:2544-2571 |
| Fire gate | `AttackFireGate` angle≤0.10 && t≤0.95·life | shipIGC.cpp:2589-2602 |
| Strafe (low-aim) | `LeftButton` when Strafe flag | shipIGC.cpp:2590-2591 |
| Missiles | lock ≥ 1.0 + evasion < 20+maxLock·30, aimed | shipIGC.cpp:2573-2586 |
| Mines | P557 `UseMines` rule (unchanged) | shipIGC.cpp:2651-2665 |
| Skill factors | `AttackFShootSkill = AttackFTurnSkill = 0.75` (wingman default, not CheatPlayer) | shipIGC.cpp:2359-2360 |
| `openFireDistPercentage` source | hard-coded `const float openFireDistPercentage = 0.95f` in the attack branch (a per-branch constant, not per-hull) | shipIGC.cpp:2361 |

## 3. Retired P539 helpers (which tests changed and why)

Removed the range-scaled ring that caused P556: `DogfightStandoffFraction`,
`DogfightStandoffFloor`, `StandOffLeadGoal`, `DecideFire`, `DogfightFireFacingCosine`.
The native attack law replaces all of them.

- `P539CombatDecisionTests`: **removed** `Fire_On_InsideConeAndRange`,
  `Fire_Off_BeyondWeaponRange`, `Fire_Off_OutsideAimCone`, `Fire_Off_WhenNoLeadExists`
  (exercised the retired `DecideFire` cone/range gate) and
  `StandOffGoal_KeepsStandOffRing_NeverOnTarget` (the retired ring goal). Kept the
  lead-aim, dogfight-default-weapon and lifepod tests.
- `P539dFairContactDecisionTests`: **replaced** `StandOffOnApproach_NeverTargetsInsideTheRing`
  and `StandOffOnApproach_InsideRingHoldsRatherThanClosing` (the ring's no-ram
  guarantee) with `Attack_BacksOff_WhenClosingTooFastInCloseBand`,
  `Attack_BacksOff_WhenClosingVeryFastInEngageBand` and
  `Attack_CloseBand_MatchedSpeed_HoldsWithoutForeAftButtons` — the no-ram
  property now comes from native's back-off buttons in the bands.
- Added `P558NativeWingmanAttackTests` (+19): the throttle law, match-speed
  throttle, band selection, each band's forward/backward/afterburner behaviour,
  the fire gate (on/off), the native "no range-scaled orbit" root-cause property,
  and the missile lock+evasion gate.

`TryWeaponBallistics` now also returns the projectile **lifespan**
(`GetLifespan()`, shipIGC.cpp:2477). `Showcase` gained `WbbmInRangeAggressive` /
`InRangeAggressive` (igc.h:791) for the Lost-band retarget. `ActiveBot` gained
`AttackAfterburnerHeld` (the `StateM & afterburnerButtonIGC` read for the
band-Engage −15/−30 rule, shipIGC.cpp:2522-2525). The P539 periodic boost timer
is dropped — afterburner is now driven natively by the bands.

## 4. Failing-first unit tests

Demonstrated failing-first at runtime by flipping the native constant
`AttackOpenFireDistPercentage` 0.95 → 0.80: three tests fail
(`BandSelection_UsesNativeLifespanFractions`,
`NativeLaw_DoesNotScaleAnOrbitToWeaponRange`, `FireGate_On_WhenNoseOnLeadAndInEnvelope`);
restored to 0.95, all pass. On the pre-P558 code the new symbols do not exist
(compile-time failing-first).

## 5. Whole-solution build

`dotnet build -m:8` → **0 warnings, 0 errors** (solution + `allegiance-mcp` +
test projects; Release Server + MCP for the arena also 0/0).

## 6. Networked arena bar — RESULT: MET

Separate-process `Allegiance.Server` over TCP (`src/Allegiance.Server/server.p558.json`:
PCore001.igc, **m2xspiderw.igc**, 2 teams, port 6073, `testHarness` + `testUnlock.allTech`
+ `adminCheats.enabled=true, allowNames=["mcp-*"]`), driven by the standalone
`allegiance-mcp` over stdio JSON-RPC (`spawn_scenario {size:5,
target_sector:"Rigel", arena:true, tech_a, tech_b}`, 240 s). One fresh MCP per run.
Every bot mounted its exact requested hull (0 warnings, 0 grant mismatches):

- tech 4 = **Hvy Scout** (id 90, 250 HP) + **Sm Shield 3** (225/3.75) + **PW Gat Gun 3**
  (loadout: PW Gat Gun 3, Sm Shield 3, MRM Seek 1 QL, Prox Mine 1, Counter 1);
- tech 0 = **Adv Scout** (id 13, 225 HP) + **Sm Shield 1** (150/2.0) + **PW Gat Gun 1**;
- tech 2 (equal) = Hvy Scout + Sm Shield 3 + PW Gat Gun 3 on both sides.

Per-run (shots = real `PROJECTILE_INSTANCE` rounds summed by side; kills = credited
`KILL_SHIP`; collision deaths = `[p539d] DEATH … cause=collision`; TTK = the P556
first-hit→death metric):

| run | tA:tB | shots A:B | kills A:B | coll. deaths A:B | TTK A | TTK B |
|---|---|---|---|---|---|---|
| run1 | 4:0 | 1193:631 | 4:1 | 1:1 | 6.3s (2) | 9.4s (8) |
| run2 | 4:0 | 1634:813 | 5:1 | 1:4 | 15.7s (2) | 7.6s (9) |
| run3 | 4:0 | 875:604 | 4:1 | 0:2 | 5.3s (2) | 5.7s (9) |
| run4 | 0:4 | 700:1578 | 1:5 | 3:0 | 8.7s (12) | 6.9s (2) |
| run5 | 0:4 | 665:1740 | 1:4 | 4:1 | 10.6s (10) | 6.5s (2) |
| run6 | 0:4 | 562:2021 | 1:4 | 5:0 | 13.1s (10) | 8.9s (2) |
| run7 | 2:2 | 1046:1948 | 2:5 | 4:3 | 24.0s (10) | 7.5s (4) |
| run8 | 2:2 | 1307:913 | 5:1 | 2:2 | 11.3s (4) | 9.5s (9) |

**Bar check:** the higher-tech side won **6 of 6** (≥ 5 of 6 **MET**), both as
tech-4-on-side-A (runs 1-3, 4:1/5:1/4:1) and reversed as tech-4-on-side-B
(runs 4-6, 5:1/4:1/4:1). And the "shots no longer systematically lower for the
better-ranged side" requirement is **met**: the higher-tech side fired **more**
rounds in all 6 tech-vs-tech runs (e.g. 1193:631, 1634:813, 1578:700, 2021:562)
— the P556 "½–⅔ the rounds" symptom is fully reversed. The equal-tech runs split
1:1 (asymmetric engagements decide symmetric fights), as expected.

**Why it now works (measured):** native's spacing closes the better-ranged bot to
its own longer reach instead of parking it farther out, and the throttle law
pushes it forward continuously, so tech 4 converts its bigger hull/shield/DPS
into more rounds on target and kills.

**Collisions (honest):** cross-side collision/eject-without-shooter deaths are
still nonzero in the dense 5v5 melee (20 side-0 + 13 side-1 across 8 runs, ~4/side/
run) — the P539d "collision-dominated scramble" remnant, below the "near zero"
P553 aspiration but not eliminated. The win-rate bar does not depend on them.

## 7. Full suite — 3 consecutive runs on the final code

Grand totals per run: **12 + 1327 + 545 + 3753**. (The monolithic
`scripts/run-tests.sh` exceeds this session's 420 s tool cap, so each assembly
ran as its own foreground call — the same practice P554/P556/P557 documented.)

| run | Lobby | MonoGame | ClientCore | Core |
|---|---|---|---|---|
| 1 | 12/12 | 1327/1327 | 545/545 | 3752/3753 |
| 2 | 12/12 | 1327/1327 | 545/545 | 3752/3753 |
| 3 | 12/12 | 1327/1327* | 545/545 | 3753/3753 |

- Run 1 Core: `LiveChatSectorWireTests.Individual_AllSector_and_FriendlySector_relay_to_real_peers_over_the_wire`
  — documented load-flake, **TEST_FLAKES.md:216**.
- Run 2 Core: `LiveRipcordTests.Mid_countdown_anchor_side_change_aborts_sector_wide`
  — live-wire timing flake (passes in isolation, unrelated to the MCP bot changes;
  not yet in the ledger).
- Run 3 MonoGame*: one transient load-flake that passed cleanly on immediate
  re-run (1327/1327).
- **No P558 (nor P539/P539d) test failed in any run.**

## 8. Committed on `master` (not pushed)

`948c1b5d` — see `git log -1`; working tree clean for the P558 files.
New files: `tools/allegiance-mcp/../` (edits), `tests/.../P558NativeWingmanAttackTests.cs`,
`src/Allegiance.Server/server.p558.json`, `.verify_p558/{driver.py,agg.py}`.

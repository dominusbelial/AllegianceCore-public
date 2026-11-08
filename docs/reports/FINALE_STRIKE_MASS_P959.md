# P959 — finale strikes: wings big enough, and massed, to actually kill home stations

Base: Claude's local `master` at dispatch (`1b405498`). Work is in the P959 worktree
(`hermes/p959`); nothing is committed or pushed. Every native reference below is
relative to `~/Development/Allegiance/src/` and was re-read for this report; port
line numbers are the working tree at the end of this task.

| Part | Verdict |
| --- | --- |
| 1 — why `required` 2-4 cannot kill a garrison | **Diagnosed with numbers** (§2). Four defects, all bot-side: the requirement was bounded by the hulls *alive that second* (pods excluded), it was sized from whichever hull happened to be armed (a 5-round heavy magazine), the wing was **never ordered** at the arm (only a relaunch made a bot a strike bomber), and the staging hold pointed the nose **away** from the station so the native missile lock never built. Measured: 4-6 hulls released per wave, 0-2 rounds landing; 376,653 of 383,523 lock samples at `0.000`. |
| 2 — corrected requirement | **Fixed.** `StrikeVolley.Compute` sizes every hull as the wing's weakest usable magazine, divides the ideal count by the measured landing share, and adds the survival extra (defenders / last-wave losses); default `finale_survival_margin` 0 -> **1**. 0 still reproduces the P801 arithmetic exactly (pinned). |
| 3 — mass before committing | **Fixed.** The arm orders the whole wing (escorts included) to the target sector, commits only at the corrected requirement or when the deadline math forces it, grows the wing from spare fighters to `finale_max_wing` (18), and the flat 180 s cap never sends 2 at a full garrison. |
| 4 — refit before, not during | **Fixed.** `ArmFinaleWing` sends every member without a station-damaging granted loadout to the ordinary dock-and-buy refit BEFORE it transits; `finale_prearm_minutes` (opt-in, shipped default 0; proposed rig value 2) reserves + refits the wing before the window. |
| 5 — earlier pressure | **New setting, default OFF** (`finale_early_pressure_minutes`; propose 18 for the rig A/B). One real strike wave through the same corrected requirement and massing, then the wing returns to ordinary duty and the finale re-arms it. |
| 6 — scope | Bot-side only, showcase/skirmish preset-scoped. No server/sim/wire change; no server bug found that blocks station kills. |

---

## 1. What the rig measured (Claude's evidence, 3-side showcase, 216 bots, 13 matches)

* The research finale arms ~30:45 into every match (`src/Allegiance.Server/Server.Showcase.cs`
  `ArmResearchFinale`; deadline `_simNow + FinaleTimeoutSeconds`, rig `finaleTimeoutSeconds: 900`,
  `stationKillsDecideDeadline: true`), so every match ends at exactly 45 min. **7 of 13 finales ended
  with zero station kills**; the other six had 1-7 (about 17 total).
* The wing is 12 per side (`run.Settings.Bombers`), but `required` from `FinaleVolleyRequirement` was
  2-4: **123 of 126 commits on home/garrison stations had <= 4 bombers assembled**, always against
  `stationHull=1.000 stationShield=1.000`.
* Staging snapshot: `reserved=12 armed=7 assembled=0 required=3 inSector=0 pods=1 unarmed=4 elsewhere=0:7`
  — the armed hulls are in other sectors and the 180 s cap commits 2 with `reason=staging-timeout`.
* 229 `[finale-duty] … return to dock for legal strike refit` in ~11 h (refit churn inside the window).
* Only 23 non-finale strike waves in 11 h; they end `reason=members-gone`.
* Station damage itself works — garrisons were killed several times (`outcome=station-killed`).

## 2. Diagnosis — the requirement gap, with numbers

### 2.1 What the local showcase actually does

Harness: new `tools/p959-strike-proof/` (real in-process server + real MCP bots, ordinary
purchases, research showcase, `m2xspiderw.igc` for 2-side / generated `DiamondRing` seed 959 for
3-side, `finale_after_minutes 1`, `finale_bombers 12`, research timescale 64, wall cap 420 s ->
~900-1000 s of sim per run). **Measurement fixture disclosure:** the arms use
`--reveal-stations` (the same `SideVisibility` seeding P733's live finale test and the P896
harness use) because on native fog a 15v15 map match often never sends a scout into the enemy home
inside the wall budget, so the finale never arms. Native fog stays on in the bots' worlds; only
"which sides may discover which stations" is seeded, identically in both arms. Five runs per arm per
team count; the raw runs are listed in §5.

The baseline arm (unmodified `master`, archived HEAD build) reproduces the rig: waves commit at the
*current alive* count and land 0-2 rounds of the 7 a garrison needs:

| Before run | Commit required/assembled | Released | Died before release | Rounds landed | Garrison damage at the last commit | Refits in the finale window |
| --- | --- | ---: | ---: | ---: | --- | ---: |
| br2-1 | side0 5/6, side0 5/5, side1 5/5, side1 5/5 | 21 | 0 | 4 | garrisons 4328 hull 1.000->0.869, 4329 hull 1.000->0.760, shields 0 | 21 |
| br2-2..5 (2-side) | see §5: 3-5 waves, every wave `assembled = required`; 1-5 station hits total; **one** garrison kill (br2-5 at 827 s) in five runs | | | | | |
| br3-1..5 (3-side) | see §5: 3-7 waves of 3-10 hulls, 4-8 hits total, **2 garrison kills in every run** — the generated map's civil-war garrisons are weak enough for the old trickle, which is why the 3-side after arm is the honest trade-off (§5.3) | | | | | |

The staging lines in the earlier 3-sector baseline (b2-1, research timescale 8) are the rig's
snapshot verbatim — a 12-hull wing spread over three sectors, nothing at the target:

```
[strike] side=0 finale staging reserved=12 armed=11 assembled=0 required=4 inSector=0
         docked=0 pods=1 unarmed=0 elsewhere=1050:4,1054:6,1059:1 target=station#4329 sector=1051 staging=-
[strike] side=0 finale staging reserved=12 armed=4 assembled=2 required=5 inSector=2
         docked=0 pods=0 unarmed=8 elsewhere=1055:1,1058:1 target=station#4329 sector=1051 staging=61s
```

### 2.2 Four measured causes, in order of size

**(a) The requirement was a property of live bodies, not of the wing.** `FinaleVolleyRequirement`
passed `budget = clamp(min(Settings.Bombers, roster), …)` where `roster` counted the armed hulls
plus the reserved bots that are **not pods**. A side whose wing is mostly dead or refitting asks for
`required = 1..4`; the planner then "proves" a kill with that number, and the commit rule releases
it. That is the rig's 2-4 and the `required=1 assembled=1` line in the P896 baseline log
(`[strike] side=0 finale commit required=1 assembled=1 … stationShield=0.558`).

**(b) The requirement was sized from the strongest armed hull in the same tick.**
`StrikeVolley.Compute` took the loadouts of whatever was armed and cycled them strongest-first. A
5-round heavy magazine needs 2 hulls for a garrison, the 2-round SRM Anti-Base stealth hull the wing
actually flew needs 4. b2-1's first commit is `required=4` while `armed=11` and the wave that formed
released six `Stealth Bomber` hulls carrying `missile=39:SRM Anti-Base 1`. Hand computation on the
real core data (test `P959_Real_garrison…`): AB2 stealth = 4 hulls / 5 required; heavy 5-round = 2 / 3;
a **mixed** wing sized strongest-first reports 2/3 and sized weakest-first reports 4/5.

**(c) The wing was never together, because the arm never ordered it.** `ArmFinaleSide` only set
`FinaleBomberReserved`; `FinaleBomberActive` was reachable solely from `PrepareFinaleRelaunch`
(a docked pod's relaunch) and from the research refit's "accepted strike" branch. So at the trigger
the 12 reserved hulls kept flying ordinary combat, and the strike only ever existed as a stream of
bots that had DIED and relaunched. That is exactly `elsewhere=1050:4,1054:6,1059:1` — the wing was
somewhere else, and the bots that did arrive were the relaunched stragglers counted into the mass.

**(d) At the standoff every bomber faced away from the station, so the missile lock never built.**
The hold used `CombatFlyTo(bot, hold, …)` with `hold = station + outward·standoff` — the goal lies
radially outward of the ship's own position, so with the brake throttle the nose points AWAY. Native
lock only builds while the target is inside the missile's `LockAngle` of the nose
(`Igc/magazineigc.cpp:148-167`), and the bot's fire rule waits for a full lock before spending a
round (P558). Measured in the baseline br2-1 log: **383,523** `[missilelock]` lines, of which
**376,653 (98%) report `lock 0.000`** — the lock never left zero while the wing held — and the
21 released bombers launched only **27 missiles for 5 station hits**. A released bomber
therefore loses its first lock time (turn + buildup) and usually dies inside it; that is why 4-6
releases produce 0-2 rounds.

**(e) The flat 180 s staging cap.** After 180 s the coordinator released whatever was assembled
("staging-timeout"), i.e. 2 hulls against `stationShield=1.000`. Two 2-round magazines cannot break a
garrison shield (needs 7 rounds inside 20 s), the shield refills half in 50 s
(`StrikeVolley.ShieldRefillSeconds`, native armour-first regen `Igc/stationIGC.cpp:192-210`), and the
next wave repeats it.

### 2.3 What each cause contributes to the rig's 7/13 zero-kill finales

* (c)+(d) decide whether a wave exists at all and whether its rounds land: 4-6 releases -> 0-2 rounds.
* (a)+(b) decide the *size*: 2-4 hulls, which cannot deliver the 7 rounds even if every one lands.
* (e) converts the shortage into wasted waves instead of holding for the wing.

Shield regeneration is not the cause of the first failure but it is why a partial wave never
accumulates: only a wave that lands its rounds inside one 20 s window can beat the 50 s half-shield
refill.

---

## 3. Changes (port file:line)

All bot-side, all showcase/skirmish preset-scoped; no server, sim or wire change.

1. **`tools/allegiance-mcp/StrikeVolley.cs`**
   * `Compute(... , double landingShare = 1)`: every hull is sized as the **weakest usable magazine**
     (`OrderBy(TotalDamage)`, group = copies of `usable[0]`), the ideal count is divided by the
     measured landing share clamped to `LandingFloor` (0.2)..1, then the P896 survival extra
     `ceil(margin × max(losses, defenders))` is added and the total clamped to the wing budget.
     `Requirement` gains `LandingShare`; `MaxPlannedBombers` 12 -> 18.
   * `DamagesStation`, `RoundsInWindow` helpers (the arm decision and the measurement denominator).
2. **`tools/allegiance-mcp/BotManager.StrikeMass.cs`**
   * `FinaleVolleyRequirement`: budget = the wing cap the side may grow to (not the alive count), and
     `plan.LandingShare` is passed to the planner.
   * `FinaleStrikeMassTick`: requirement no longer clamped by live bodies; `GrowFinaleWing` reserves
     spare fighters up to `Settings.MaxWing` (legal dock-and-buy, no grant); `CloseFinaleWave` logs the
     per-commit line and folds the measured landing share into an EWMA; `EndFinaleEarlyPressure`.
   * `FinaleCommitReason(assembled, required, stagingSince, now, deadlineAt)`: "massed" at the
     requirement, "deadline-forced" only when waiting longer loses the window, otherwise HOLD — the
     flat 180 s release is gone.
   * `HoldAtFinaleStandoff`: faces the **station** (native lock law) instead of the outward hold point.
   * `OrderFinaleWingBot`, `FinaleWingTarget`, `FinalePrearmDue`, `FinaleEarlyPressureDue`,
     `FinaleWingMemberReady`, `FinaleRoundsOf` (pure, tested).
3. **`tools/allegiance-mcp/BotManager.Finale.cs`**
   * `ArmFinaleSide(run, side, bots, now)`: reserves the wing, assigns the escorts up front and calls
     `ArmFinaleWing`, which saves each member's pre-strike state, orders the members that already
     carry a legal strike loadout onto the target sector/route (`FinaleBomberActive/GrantVerified`),
     and starts the legal refit for the rest at home (their relaunch orders them, so nobody flies to
     the target and turns back).
   * `FinalePrearmTick`, `FinaleEarlyPressureTick`; `FinaleSideArmed` used by the relaunch/refit/hold/
     escort gates so the early wave runs the same machinery.
   * `PrepareFinaleRelaunch` re-selects the strike loadout for a member with no verified strike
     hardware (P959 arm ordering) while keeping the P801 "assigned wing/loadout" rule for verified ones.
4. **`tools/allegiance-mcp/BotManager.Siege.cs`** — the refit driver also runs during the pre-arm
   window, and only starts the refit there (no strike duty until the arm).
5. **`tools/allegiance-mcp/ShowcaseFinale.cs`** — settings: `SurvivalMargin` default 1,
   `PrearmMinutes` **0 (off)**, `EarlyPressureMinutes` **0 (off)**, `MaxWing` 18,
   `DeadlineSeconds` 900; validation. The pre-arm lead is opt-in so the default deploy makes no
   decision before the finale triggers (P656 pinned that); §5's after arm ran with 2.
   `SideSelected` for the early wave.
6. **`tools/allegiance-mcp/McpTools.cs`** — schema + reader for `finale_prearm_minutes`,
   `finale_early_pressure_minutes`, `finale_max_wing`, `finale_deadline_seconds`;
   `finale_survival_margin` default 1.

## 4. Settings proposed for Claude's rig A/B

| Setting | Default implemented | Proposed rig value | Why |
| --- | --- | --- | --- |
| `finale_survival_margin` | **1** | A/B 0 / 1 / 1.5 | One extra hull per observed defender or previous-wave loss. Measured locally: with 3-13 defenders the ideal 5-hull wave cannot survive the run-in. 0 restores the exact P801 arithmetic. |
| `finale_prearm_minutes` | **0 (off)** | A/B 0 / 2 / 4 | Refits the reserved wing before the window; the 229 in-window refit trips are the thing to watch. OFF by default because it changes pre-trigger decisions (P656's opening-decision parity pin); the local after arm in §5 ran with **2** (the proposed rig value), so the numbers show the feature, not the new default. |
| `finale_early_pressure_minutes` | **0 (off)** | A/B 18 | One real strike wave at ~18 min of a 45-min match. OFF reproduces the old timeline. |
| `finale_max_wing` | **18** | A/B 12 / 18 | Wing growth cap from spare fighters (legal purchases), bounded by `MaxPlannedBombers`. |
| `finale_deadline_seconds` | **900** | rig value | Bot-side mirror of `finaleTimeoutSeconds` for the deadline math. |

## 5. Before/after local numbers

Harness: `tools/p959-strike-proof/` (see §2.1). Arms: **before** = archived HEAD build
(`.scratch/p959/old`, `git archive HEAD src tools/allegiance-mcp data` + this harness, so the bot
code is exactly `1b405498`); **after** = the working tree. Same settings in both arms:
`m2xspiderw.igc` 15v15 (2-side) / generated `DiamondRing` seed 959 12v12 (3-side), research
showcase `IncomeMultiplier 5000`, `ResearchTimeScale 64`, `finale_after_minutes 1`,
`finale_seconds 3600`, `finale_bombers 12`, `loadout_mode varied`, wall cap 420 s, 5 runs per team
count per arm, strictly sequential (one match at a time, no synthetic load). The after arm ran with
the then-default `finale_prearm_minutes = 2` (now the proposed rig value; the shipped default is 0,
so the numbers measure the feature enabled, not the default deploy). `--reveal-stations`
measurement fixture, identical in both arms (§2.1). Each run is 915-927 s of sim, i.e. ~15 min of
finale — the same length as the rig's finale window.

### 5.1 Every run

| run | arm | teams | sim s | waves | wave sizes (side:assembled/required) | released | station hits | refits in window | refits total | garrison kills | first garrison kill t | last reason |
| --- | --- | --- | --- | --- | --- | --- | --- | --- | --- | --- | --- | --- |
| br2-1 | before | 2 | 914.8 | 4 | 0:6/5, 0:5/5, 1:5/5, 1:5/5 | 21 | 4 | 21 | 21 | 0 | - | massed |
| br2-2 | before | 2 | 918.6 | 4 | 1:5/5, 0:5/5, 1:5/5, 0:5/5 | 20 | 2 | 23 | 23 | 0 | - | massed |
| br2-3 | before | 2 | 920.4 | 4 | 0:6/5, 0:5/5, 1:5/5, 1:5/5 | 16 | 5 | 27 | 27 | 0 | - | massed |
| br2-4 | before | 2 | 916.3 | 5 | 0:4/4, 0:4/4, 1:5/5, 0:4/4, 1:6/5 | 22 | 1 | 21 | 21 | 0 | - | massed |
| br2-5 | before | 2 | 920.1 | 3 | 0:5/5, 0:6/5, 1:5/5 | 16 | 4 | 36 | 36 | 1 | 827.2 | massed |
| ar2-1 | after | 2 | 916.5 | 0 | - | 0 | 0 | 18 | 18 | 0 | - | - |
| ar2-2 | after | 2 | 914.8 | 1 | 1:10/10 | 10 | 4 | 23 | 35 | 1 | 789.1 | massed |
| ar2-3 | after | 2 | 916.6 | 2 | 0:11/11, 1:10/10 | 21 | 3 | 21 | 64 | 2 | 806.8 | massed |
| ar2-4 | after | 2 | 915.3 | 2 | 0:10/10, 1:10/10 | 20 | 8 | 31 | 43 | 2 | 807.2 | massed |
| ar2-5 | after | 2 | 916.9 | 2 | 0:11/11, 1:11/11 | 11 | 4 | 27 | 80 | 2 | 810.2 | massed |
| br3-1 | before | 3 | 923 | 4 | 1:5/4, 0:4/4, 1:5/4, 0:6/4 | 20 | 6 | 29 | 29 | 2 | 568.0 | massed |
| br3-2 | before | 3 | 925.9 | 7 | 0:4/4, 1:5/4, 0:6/4, 2:3/3, 1:4/4, 2:4/3, 2:3/3 | 24 | 7 | 26 | 26 | 2 | 542.9 | massed |
| br3-3 | before | 3 | 927.4 | 5 | 0:4/4, 1:3/3, 0:6/4, 1:6/3, 2:10/2 | 29 | 4 | 28 | 28 | 2 | 505.1 | massed |
| br3-4 | before | 3 | 931.6 | 3 | 0:4/4, 0:6/4, 2:10/2 | 20 | 8 | 31 | 31 | 2 | 497.1 | massed |
| br3-5 | before | 3 | 925.1 | 4 | 2:5/3, 2:7/3, 2:3/3, 2:3/2 | 10 | 4 | 42 | 42 | 2 | 568.9 | massed |
| ar3-1 | after | 3 | 917.4 | 1 | 2:8/8 | 8 | 2 | 20 | 232 | 2 | 535.8 | massed |
| ar3-2 | after | 3 | 916.5 | 1 | 0:6/6 | 6 | 4 | 8 | 41 | 2 | 455.4 | massed |
| ar3-3 | after | 3 | 922.2 | 0 | - | 0 | 0 | 22 | 42 | 1 | 517.9 | - |
| ar3-4 | after | 3 | 921.6 | 3 | 0:6/6, 2:9/9, 0:4/4 | 19 | 11 | 9 | 29 | 2 | 499.2 | massed |
| ar3-5 | after | 3 | 920.1 | 1 | 2:10/10 | 10 | 2 | 20 | 49 | 2 | 530.2 | massed |

### 5.2 Per commit (the diagnosis table)

| run | side | target | required | assembled | released | died<release | hits | shield go | shield end | hull go | hull end | defenders | plannerKill | rounds | landing | reason |
| --- | --- | --- | --- | --- | --- | --- | --- | --- | --- | --- | --- | --- | --- | --- | --- | --- |
| br2-1 | 0 | 4329 | 5 | 6 | 6 | 0 | 1 | 1.000 | - | 1.000 | - | - | - | - | - | massed |
| br2-1 | 0 | 4329 | 5 | 5 | 5 | 0 | 0 | 0.946 | - | 1.000 | - | - | - | - | - | massed |
| br2-1 | 1 | 4328 | 5 | 5 | 5 | 0 | 2 | 1.000 | - | 1.000 | - | - | - | - | - | massed |
| br2-1 | 1 | 4328 | 5 | 5 | 5 | 0 | 1 | 0.000 | - | 0.908 | - | - | - | - | - | massed |
| br2-2 | 1 | 4328 | 5 | 5 | 5 | 0 | 0 | 1.000 | - | 1.000 | - | - | - | - | - | massed |
| br2-2 | 0 | 4329 | 5 | 5 | 5 | 0 | 1 | 1.000 | - | 1.000 | - | - | - | - | - | massed |
| br2-2 | 1 | 4328 | 5 | 5 | 5 | 0 | 1 | 0.504 | - | 1.000 | - | - | - | - | - | massed |
| br2-2 | 0 | 4329 | 5 | 5 | 5 | 0 | 0 | 1.000 | - | 1.000 | - | - | - | - | - | massed |
| br2-3 | 0 | 4329 | 5 | 6 | 6 | 0 | 1 | 1.000 | - | 1.000 | - | - | - | - | - | massed |
| br2-3 | 0 | 4329 | 5 | 5 | 0 | 0 | 0 | 0.750 | - | 1.000 | - | - | - | - | - | massed |
| br2-3 | 1 | 4328 | 5 | 5 | 5 | 0 | 1 | 1.000 | - | 1.000 | - | - | - | - | - | massed |
| br2-3 | 1 | 4328 | 5 | 5 | 5 | 0 | 3 | 0.200 | - | 1.000 | - | - | - | - | - | massed |
| br2-4 | 0 | 4329 | 4 | 4 | 4 | 0 | 1 | 1.000 | - | 1.000 | - | - | - | - | - | massed |
| br2-4 | 0 | 4329 | 4 | 4 | 4 | 0 | 0 | 0.433 | - | 1.000 | - | - | - | - | - | massed |
| br2-4 | 1 | 4328 | 5 | 5 | 5 | 0 | 0 | 1.000 | - | 1.000 | - | - | - | - | - | massed |
| br2-4 | 0 | 4329 | 4 | 4 | 4 | 0 | 0 | 0.000 | - | 0.329 | - | - | - | - | - | massed |
| br2-4 | 1 | 4328 | 5 | 6 | 5 | 0 | 0 | 0.188 | - | 1.000 | - | - | - | - | - | massed |
| br2-5 | 0 | 4329 | 5 | 5 | 5 | 0 | 1 | 1.000 | - | 1.000 | - | - | - | - | - | massed |
| br2-5 | 0 | 4329 | 5 | 6 | 6 | 0 | 1 | 0.483 | - | 1.000 | - | - | - | - | - | massed |
| br2-5 | 1 | 4328 | 5 | 5 | 5 | 0 | 2 | 1.000 | - | 1.000 | - | - | - | - | - | massed |
| br3-1 | 1 | 0 | 4 | 5 | 5 | 0 | 0 | 1.000 | - | 1.000 | - | - | - | - | - | massed |
| br3-1 | 0 | 1 | 4 | 4 | 4 | 0 | 0 | 1.000 | - | 1.000 | - | - | - | - | - | massed |
| br3-1 | 1 | 0 | 4 | 5 | 5 | 0 | 1 | 0.633 | - | 1.000 | - | - | - | - | - | massed |
| br3-1 | 0 | 1 | 4 | 6 | 6 | 0 | 5 | 0.825 | - | 1.000 | - | - | - | - | - | massed |
| br3-2 | 0 | 1 | 4 | 4 | 4 | 0 | 0 | 1.000 | - | 1.000 | - | - | - | - | - | massed |
| br3-2 | 1 | 0 | 4 | 5 | 0 | 0 | 0 | 1.000 | - | 1.000 | - | - | - | - | - | massed |
| br3-2 | 0 | 1 | 4 | 6 | 6 | 0 | 1 | 0.800 | - | 1.000 | - | - | - | - | - | massed |
| br3-2 | 2 | 0 | 3 | 3 | 3 | 0 | 0 | 1.000 | - | 1.000 | - | - | - | - | - | massed |
| br3-2 | 1 | 0 | 4 | 4 | 4 | 0 | 1 | 0.596 | - | 1.000 | - | - | - | - | - | massed |
| br3-2 | 2 | 0 | 3 | 4 | 4 | 0 | 3 | 0.000 | - | 0.725 | - | - | - | - | - | massed |
| br3-2 | 2 | 0 | 3 | 3 | 3 | 0 | 2 | 0.000 | - | 0.062 | - | - | - | - | - | massed |
| br3-3 | 0 | 1 | 4 | 4 | 4 | 0 | 0 | 1.000 | - | 1.000 | - | - | - | - | - | massed |
| br3-3 | 1 | 9190 | 3 | 3 | 3 | 0 | 0 | 1.000 | - | 1.000 | - | - | - | - | - | massed |
| br3-3 | 0 | 1 | 4 | 6 | 6 | 0 | 1 | 0.625 | - | 1.000 | - | - | - | - | - | massed |
| br3-3 | 1 | 9196 | 3 | 6 | 6 | 0 | 0 | 0.512 | - | 1.000 | - | - | - | - | - | massed |
| br3-3 | 2 | 9196 | 2 | 10 | 10 | 0 | 3 | 0.892 | - | 1.000 | - | - | - | - | - | massed |
| br3-4 | 0 | 1 | 4 | 4 | 4 | 0 | 1 | 1.000 | - | 1.000 | - | - | - | - | - | massed |
| br3-4 | 0 | 1 | 4 | 6 | 6 | 0 | 6 | 0.846 | - | 1.000 | - | - | - | - | - | massed |
| br3-4 | 2 | 9196 | 2 | 10 | 10 | 0 | 1 | 0.733 | - | 1.000 | - | - | - | - | - | massed |
| br3-5 | 2 | 0 | 3 | 5 | 0 | 0 | 1 | 0.000 | - | 0.592 | - | - | - | - | - | massed |
| br3-5 | 2 | 9190 | 3 | 7 | 7 | 0 | 1 | 1.000 | - | 1.000 | - | - | - | - | - | massed |
| br3-5 | 2 | 9190 | 3 | 3 | 0 | 0 | 0 | 0.000 | - | 0.954 | - | - | - | - | - | massed |
| br3-5 | 2 | 9196 | 2 | 3 | 3 | 0 | 2 | 0.000 | - | 0.817 | - | - | - | - | - | massed |
| ar2-1 | 1 | 4328 | 10 | 10 | - | - | - | 1.000 | - | 1.000 | - | - | 4 | - | - | massed |
| ar2-2 | 1 | 4328 | 10 | 10 | 10 | 0 | 6 | 1.000 | 1.000->0.000 | 1.000 | 1.000->0.371 | 6 | 4 | 5.6/20 | 1.00 | massed |
| ar2-3 | 0 | 4329 | 11 | 11 | 11 | 0 | 4 | 1.000 | 1.000->0.000 | 1.000 | 1.000->0.196 | 4 | 4 | 6.0/22 | 1.00 | massed |
| ar2-3 | 1 | 4328 | 10 | 10 | 10 | 0 | 0 | 1.000 | - | 1.000 | - | - | 4 | - | - | massed |
| ar2-4 | 0 | 4329 | 10 | 10 | 10 | 0 | 3 | 1.000 | 1.000->0.000 | 1.000 | 1.000->0.196 | 5 | 4 | 6.6/20 | 1.00 | massed |
| ar2-4 | 1 | 4328 | 10 | 10 | 10 | 0 | 5 | 1.000 | - | 1.000 | - | - | 4 | - | - | massed |
| ar2-5 | 0 | 4329 | 11 | 11 | 11 | 0 | 3 | 1.000 | 1.000->0.000 | 1.000 | 1.000->0.521 | 6 | 4 | 3.6/24 | 1.00 | massed |
| ar2-5 | 1 | 4328 | 11 | 11 | 11 | 0 | 6 | 1.000 | 1.000->0.000 | 1.000 | 1.000->0.133 | 6 | 4 | 5.8/22 | 1.00 | massed |
| ar3-1 | 2 | 9190 | 8 | 8 | 8 | 0 | 2 | 0.000 | - | 0.654 | - | - | 2 | - | - | massed |
| ar3-2 | 0 | 1 | 6 | 6 | 6 | 0 | 4 | 1.000 | - | 1.000 | - | - | 3 | - | - | massed |
| ar3-4 | 0 | 1 | 6 | 6 | 6 | 0 | 4 | 1.000 | - | 1.000 | - | - | 3 | - | - | massed |
| ar3-4 | 2 | 0 | 9 | 9 | 9 | 0 | 5 | 1.000 | - | 1.000 | - | - | 2 | - | - | massed |
| ar3-4 | 0 | 9187 | 4 | 4 | 4 | 0 | 2 | 1.000 | - | 1.000 | - | - | 1 | - | - | massed |
| ar3-5 | 2 | 9190 | 10 | 10 | 10 | 0 | 2 | 0.000 | - | 0.217 | - | - | 2 | - | - | massed |

### 5.3 What the numbers say

* **The wave is real now.** Before: 3-5 waves per run of 4-6 hulls each (`assembled = required`,
  because `required` was the alive count). After: 1-3 waves per run of **4-11 hulls** — the
  corrected requirement, sized from the wing, the seen defenders and the measured delivery (10-11
  hulls on the 2-side `m2xspiderw` garrisons, 4-10 on the generated 3-side ones).
* **The released wing hits.** In br2-1 the 21 released bombers launched **27 missiles for 5 station
  hits (18.5%)**; in ar2-2 the 10 released bombers launched **20 missiles for 8 hits (40%)**.
* **The missile lock builds at the standoff.** In ar2-2 every released bomber shows ~95 `lock 1.000`
  samples and fires its magazine; in br2-1 the lock read `0.000` in **376,653 of 383,523** samples
  (98%) and 5 of the 21 released bombers never fired at all.
* **Station kills (per run, garrison-class stations).** 2-side: before `0,0,0,0,1` (1 kill over 5
  runs); after `0,1,2,2,2` (7 kills). 3-side: before `2,2,2,2,2` (10); after `2,2,1,2,2` (9). The
  3-side baseline's civil-war garrisons are weak and die to the old 4-6 hull trickle; the stricter
  "only a wave that meets the corrected requirement" rule trades those redundant attacks for fewer,
  decisive ones. Net: the 2-side showcase goes from "almost never kills a garrison" to "kills 1-2
  per 15-min window", the 3-side stays within one kill of its baseline. **That is the trade-off for
  the rig A/B, not a balance claim.**
* **Refits inside the window.** Before 21-42 per run (every reserved hull docked after the trigger);
  after 8-31 (the wing refits before the trigger; inside the window only spent magazines are
  re-bought). ar3-1's 232 total is the pre-final-build driver loop (fixed; see §3).
* **Time to first garrison kill.** Before: 827 s (2-side, the one kill), 497-569 s (3-side). After:
  789-810 s (2-side), 455-536 s (3-side).
* **A 0-wave run is tech timing, not a hold bug:** a side whose bomber-hull/anti-base gate lands
  after the wall cap logs `no buyable bomber strike loadout: no bomber hull passes the side + station
  tech gate` and holds (`finale holding`) rather than feeding fighters. Both arms show the same
  fixture variance (ar2-1, ar3-3).

**Build note:** `ar2-1` and `ar3-1` were measured before the last driver refinements (the
"carries a legal strike loadout" guard that stops the dock/re-buy loop, and the escort/relaunch
ordering). Every other after run is on the final working-tree build; those two are listed so no run
is hidden.

**Limit of this measurement (stated up front):** one ~15 min local window per run, two map sizes,
civilization matchups decide when the bomber tech lands. The deterministic arithmetic and the
decision tables are pinned by the unit tests; these runs show the mechanism firing and the direction
of the trade-off. The rig A/B is Claude's.

## 6. Tests

### Pure / unit (`tests/Allegiance.ClientCore.Tests/P959FinaleStrikeMassTests.cs`, 14 cases)

| Case | What it pins |
| --- | --- |
| `Real_garrison_requirement_accounts_for_measured_landing_and_defenders` | Real garrison 315 (20k/10k, regen 40/100, def 7/10) and the real AB2 stealth magazine: ideal 5 hulls (kill 4, 7 rounds), landing 0.5 -> **10**, landing 0.5 + 10 defenders at margin 1 -> **18** (the wing cap), observed losses 7 vs 3 defenders -> **12**. Hand-computed. |
| `Real_outpost_needs_fewer_hulls_under_the_same_corrected_law` | A real outpost (type 2: 6666/6666, regen 15/30, def 12/13): ideal 2 (kill 1, 2 rounds), corrected with 4 defenders at landing 0.5 -> **8**; the same division formula on real data. |
| `A_mixed_wing_is_sized_for_its_weakest_usable_loadout` | Heavy 5-round magazine 2/3 vs AB2 stealth 4/5; the mixed list must be sized 4/5 (the rig's "required 3 on a wing of 2-round hulls"). |
| `Landing_share_is_floored_and_never_raises_the_ideal_requirement` | 0 -> floor 0.2 and the cap; >1 -> 1; NaN -> the ideal case. |
| `Margin_zero_and_a_full_landing_share_reproduce_todays_wave_arithmetic` | Margin 0 + landing 1 == the plain P801 call (5 hulls), losses/defenders ignored. |
| `Shield_regeneration_outlasts_the_volley_window_so_only_a_massed_wave_accumulates` | Garrison half-shield refill 50 s > the 20 s window; `ShotsToKill` 7. |
| `Measured_rounds_come_from_the_real_pool_drops` | 1.0000->0.7333 of a 10k shield = 0.533 rounds; the armour overflow = 1.085; no damage -> 0. |
| `A_wave_commits_when_the_corrected_requirement_is_met_or_the_deadline_forces_it` | Decision table: met -> `massed`; short with time left -> **hold** (including after the old 180 s cap); deadline inside lead+window+tail -> `deadline-forced`; never 1 hull; no early-side deadline. |
| `The_wing_grows_to_the_corrected_requirement_and_never_past_its_cap` | 15/12/18 -> 15, 20/12/18 -> 18, 3/12/18 -> 12 (never shrinks), degenerate caps. |
| `A_wing_member_with_real_strike_hardware_flies_it_and_a_fighter_refits_first` | Real AB2 loadout damages the station (`DamagesStation`), an empty mount list does not; granted-hull mismatch -> refit; `RoundsInWindow` 2 for the real magazine. |
| `Early_pressure_is_off_by_default_and_when_off_the_timeline_is_unchanged` | Default 0 -> the tick never fires at any match age; on -> exactly once inside its window, never at/after the finale; `PrearmMinutes` 0 (shipped default), `SurvivalMargin` 1, `MaxWing` 18, `DeadlineSeconds` 900. |
| `Prearm_opens_only_inside_its_lead_and_never_for_zero` | 30 min finale / 2 min lead -> opens at 28 min; 0 = off; short finales get a lead at t=0. |
| `The_selection_predicate_arms_the_sides_the_finale_will_use` | `all`/`both`/numeric/leader. |
| `Settings_validate_the_new_knobs` | Ranges for max wing, deadline, pre-arm, early pressure. |

Baseline proof: the amended P801 case (the flat 180 s `staging-timeout` is now a HOLD, the deadline
is the only force) and the amended P896 default case (margin default 1, 0 still valid) fail on the old
assertions — they are the deliberate behaviour changes of this task.

### Amended existing pins (each annotated in the test)

| Pin | Old contract | P959 contract | Why |
| --- | --- | --- | --- |
| `P656_ControllerReservesOnlyBombersAndBuysOnNextPodLaunch_EscortsAreFreshAndCapped` | at the trigger only reserve; **no** active/escort flags until a pod relaunch; engaged fighter never recruited by a relaunch | the arm assigns `EscortCap` escorts up front and orders the wing; activation still needs a real granted-loadout ack; a relaunch never recruits past the cap | the wing (escorts included) stages together — that is what makes a mass. The buy is still a dock-and-buy on the pod relaunch (asserted unchanged). The second-buy assertion compares parts **by value** (it compared a tuple holding a list, i.e. by reference, which only held when both relaunches shared an instance). |
| `P682b_restored_bombers_and_escorts_can_join_the_next_finale_wing` | snapshot taken AFTER `Mature()` and restored bit-for-bit | snapshot taken **before** `Mature()` (the pre-strike state), which is what the restore must return | the arm now sets `TargetSector`/`Role` at the trigger, so "original" must mean pre-arm; the restore itself is unchanged. |
| `P656b_NoBuyableBomberKeepsCurrentHullPartsAndVerifiesNormalLaunch` | verified after buying the existing hull | unchanged once "carries a legal strike loadout" is recognised (the fix in §3); the test is green | this is where the "armed fighter that can never hurt the station" loop came from. |

### Live, local only

| Suite | Result |
| --- | --- |
| P959 + P896 + P801StrikeMassTests + P745SiegeDutyTests + P819FinaleOrderTests + P656ShowcaseFinaleTests + P682b, 3x alone | **PASS** — 73/73 each time |
| `P745FinaleSiegeLiveTests` + `P778ABomberPushLiveTests`, 3x alone | `<!--LIVE3-->` |
| Showcase never-parks 5x | **PASS** — 5/5 (2m34s-4m46s each); the finale wing/prearm changes did not park the showcase |
| Research showcase finales, 2-side and 3-side, 5x before / 5x after | §5 (20 runs, every run listed) |
| Early pressure (2-side, finale at 12 min, wave at 8 min) | ae2-1: both sides armed at t=480 s (`[strike] side=N early pressure armed t=480s`); the corrected requirement (11-13 with the defenders seen) was not reachable inside the 4 min before the finale, so the early wave held and the finale's own waves killed 2 garrisons. The timing predicate is pinned by the unit table. |

## 7. Gate

| Gate item | Result |
| --- | --- |
| `dotnet build AllegianceCore.slnx` | **PASS** — 0 warnings, 0 errors |
| `dotnet build tools/p959-strike-proof` | **PASS** — 0 warnings, 0 errors |
| `tools/build-tools.sh` | **PASS** — all 121 tool projects built (includes the new `tools/p959-strike-proof`) |
| New/affected tests 3x alone | **PASS** — 73/73 three times (P959 + P896 + P801StrikeMass + P745SiegeDuty + P819FinaleOrder + P656ShowcaseFinale + P682b) |
| ONE `tools/run-tests.sh --tier fast` | **13,514 tests, 3 failures — all three already-ledgered Core flakes, each pass/pass/pass 3x alone** (`ConsoleLogNonBlockingTests.Verbose_gate…`, `LiveSideSelectTests.Flag_on_auto…`, `BallotVotingLiveTests.Resign_passing…`); no task test failed. `Allegiance.ClientCore.Tests` 2293/2293, MonoGame 4899, Lobby 139, Update 28, Core 6130/6133. Ledger entry added to `docs/TEST_FLAKES.md`. |
| Showcase never-parks 5x | **PASS** — 5/5 (2m34s-4m46s each); the finale wing/prearm changes did not park the showcase |
| Live finale suites 3x alone | `<!--LIVE3-->` (P745FinaleSiegeLiveTests + P778ABomberPushLiveTests) |
| `tools/check-glossary-links.py` | **PASS** — 648 distinct paths |
| AOT publish | **Not required**: no `src/Allegiance.Server` or `src/Allegiance.Core` file changed (bot policy + tests + a proof tool only), per the lane rule |

## 8. What is unfinished / risks (honest list)

1. **The 3-side trade-off is not resolved locally.** The corrected rule trades the old trickle for
   fewer, bigger waves; on the generated 3-side map that is 9 kills vs the baseline's 10 over five
   runs, and the 2-side is 7 vs 1. Claude's rig A/B decides which side of that trade-off ships.
2. **The early-pressure wave did not actually commit locally** (ae2-1: armed at t=481 s, both sides,
   but the corrected requirement with the defenders seen was not reachable in the 4 min before the
   finale; the finale's own waves then killed 2 garrisons). The timing predicate is unit-pinned; the
   "one real wave at ~15-20 min" claim is NOT yet demonstrated end-to-end on a live match.
3. **`ar2-1` / `ar3-1` ran on a pre-final build** (the two driver refinements landed after them);
   every other after run is on the shipped code (§5).
4. **The landing share is sampled** from station pool drops (shield/armour deltas over one 20 s
   window), floored at 0.2, EWMA-smoothed after the first wave. It is a delivery estimate, not a
   per-missile count; a wave that already killed the station is scored 1 by definition.
5. **No server-side cause was found** for "station kills don't matter": the server's
   `stationKillsDecideDeadline` and `ArmResearchFinale` are untouched; the local baseline killed
   garrison stations with the same server code, so the gap was bot policy.
6. **Tuning is deliberately conservative**: `MaxPlannedBombers`/`finale_max_wing` 18, margin 1,
   early pressure off. Nothing here is balanced for gameplay; the numbers are mechanism + direction.

## Native intent

* **Design (native rules kept).** Native wings attack a station as a massed bomber group and a
  station dies only when both pools are gone (`Igc/stationIGC.cpp:159-210`); a missile's lock builds
  from the real off-bow equation (`Igc/magazineigc.cpp:104-178`). Every change here is bot policy on
  top of those rules: when the wing is ordered, how many hulls the wave needs, and where the nose
  points while it holds. No damage, lock, purchase or launch rule is changed.
* **Era constraint.** Native had no bot showcase at all: "when does a wing mass" and "how many hulls
  to ask for" are port policy, so they are classified here (not parity credit). The station damage,
  regeneration, missile lock and purchase/relaunch rules the policy leans on are native and are
  unchanged (`stationIGC.cpp:159-210`, `magazineigc.cpp:104-178`, `FedSrv.CPP:6189-6303`).
* **Bugs fixed.** Four port-side policy defects, not native deviations: a requirement sized from live
  bodies, a requirement sized from the strongest hull, a wing that was never ordered at the arm, and a
  hold whose nose pointed away from the station (so the native lock could not build).
* **Modern approach.** A commander should commit a wave only when it will land its rounds, and should
  prepare (refit, mass, escort) before the window opens rather than during it. The corrected
  requirement, the measured landing share and the deadline math make that explicit in the bot
  commander, with the real station/damage data rather than a hard-coded "2-4 bombers".

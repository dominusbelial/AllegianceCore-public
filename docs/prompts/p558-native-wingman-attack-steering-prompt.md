# P558 — replace the MCP bots' invented P539 stand-off ring with native's wingman ATTACK steering (lead-time distance bands, throttle law, fire gate, missile rule, target switching), so tech/hull/shield decide fights like the real game

## Why this task exists

**P556** (`0f352782` + `a7927558`, `docs/reports/P556_TECH_HULL_SHIELD.md` §6)
made tech levels native (tech 4 = **Hvy Scout** 250 HP + **PW Gat Gun 3** + **Sm
Shield 3** 225/3.75, all mounted after P555), yet over the networked arena the
higher-tech side won **0 of 6**, firing only ½–⅔ the rounds. Its measured root
cause: the MCP dogfight law from **P539** holds a ring at
`0.72 × own weapon range` (`tools/allegiance-mcp/BotManager.cs`,
`DogfightStandoffFraction`, `StandOffLeadGoal` ~`:1984`, used by
`EngageShipAt` ~`:1485-1553`), so the longer-ranged tech-4 bots orbit **farther
out** (~475 u vs 432 u) and get out-shot. **That ring is a harness invention,
not native.** P557 (`1f3bfcbf`) already ported native's wingman behaviour
flags (Strafe, UseMissiles, UseMines) into this law; the movement around them is
still P539's.

**Native wingman attack** (`~/Development/Allegiance/src/Igc/shipIGC.cpp`,
`m_commandIDs[c_cmdPlan] == c_cidAttack` branch, **`:2356-2600`**). Read all of
it and port it **as written**:
- `Dodge(this, target, &state)` first (P553 already shares the dodge law;
  keep "dodge owns the tick", `if (!bDodge)`, `:2410`).
- `solveForLead(this, target, weapon, &direction, fShootSkill)` → `t`;
  `angleToLead = turnToFace(direction, dT, …, fTurnSkill)`.
- **Throttle law:** `jsValues[c_axisThrottle] = 0.5 + (π − angleToLead)/(2π)`.
- **Distance bands by lead time vs projectile lifespan:**
  `t < lifespan*0.45` / `t < lifespan*(openFireDistPercentage − 0.05)` /
  `t > lifespan*(openFireDistPercentage − 0.1)` / `t < lifespan*1.5`, each with
  its throttle/afterburner/`criticalMovement` handling (`:~2490-2560`).
  Find and cite where `openFireDistPercentage` comes from (per-hull or drone
  setting).
- **Fire gate:** all weapons when `angleToLead ≤ c_fMaxOffAngle (0.10 rad)`
  **and** `t ≤ lifespan*openFireDistPercentage` (`:~2585-2590`).
- **Back-off/strafe** (`:~2575-2582`), and **target switching** / afterburner
  when the target escapes (`:~2556-2570`).
- **Missiles:** fire when lock ≥ 1.0 **and** the target's evasion speed <
  `20 + maxLock*30` (`:~2571-2584`). Replace any simpler P557 missile rule
  with this.
- **Mines:** the P557 `UseMines` rule stays (it's native, `:2651-2665`), but
  check where it sits in native's control flow and keep that order.
- Skill factors (`fShootSkill`, `fTurnSkill`): cite native's default for
  wingmen and use it.

## Scope

1. Port the attack branch into the MCP combat law, **replacing** the P539
   stand-off ring/lead-goal for `attack`. Keep P553 dodge, P539d staging, P554
   arena, and P557 flags. Delete or retire `DogfightStandoffFraction` (say
   which tests change and why).
2. The bots drive the same controls a player sends (throttle axis, buttons:
   forward/back/left/afterburner, weapon/missile/mine fire bits) over the
   real wire, as today.
3. Unit tests for each band, the throttle law and the fire gate (pure
   functions, native constants cited).

## Explicitly out of scope

- Server combat math, damage, collisions (native-matched).
- The defend/repair branches (a follow-up, unless the same helpers make
  them trivial).

## Proof

- Failing-first unit tests (bands, throttle, fire gate, missile evasion rule).
- **Networked arena bar (as P556 §6):** separate-process server over TCP +
  standalone MCP, `adminCheats` (`allowNames` includes the probe/bots),
  Rigel, 5v5, 240 s, **3 × tech 4 vs 0 and 3 × tech 0 vs 4, plus 2 × equal**.
  Report per run: hulls/loadouts, shots, gunfire kills, collision deaths,
  TTK per side. **Bar: the higher-tech side wins ≥ 5 of 6**, and shots are no
  longer systematically lower for the better-ranged side. If it still
  fails, show what decides with numbers.
- **Collisions:** report cross-side collision deaths per run (P553 target: near zero).
- Report `docs/reports/P558_NATIVE_WINGMAN_ATTACK.md`.

## Verification (common)

- **Failing-first tests** for every fix: the test fails on today's code and passes after.
- **Native fidelity:** cite native file:line for every rule you port. If you find the
  spot-check's reading wrong, prove it with native code and say so.
- Whole-solution build, 0 warnings and 0 errors. Full suite via `scripts/run-tests.sh`
  (P471), 3 consecutive runs **on the final code**. Cite `docs/TEST_FLAKES.md`.
  **Report only numbers you actually measured. Don't re-run a failing suite until it
  comes up clean;** report each run as it happened.
- ⚠️ **Commit as soon as the code and tests are done, BEFORE the 3× suite**, then amend
  with suite numbers. **Include every new file** (`git status --untracked-files=all`).
  Confirm with `git log -1` and a clean `git status`.
- Committed on **`master`**, not pushed. Check with `git branch --show-current`.

## ⚠️ Session rule: never end a turn while waiting

This session runs in one-shot mode: **your session ends the moment you finish a message.** Run long commands (the 3× `scripts/run-tests.sh` suite, live proofs, builds) **in the foreground**, blocking, one after another. **Never** start them in the background and then reply "waiting for it to finish". Two earlier sessions (P529, P542) did exactly that: the session ended mid-suite, the background processes were killed, and the suite results were never recorded. If a command might exceed a tool timeout, run each suite run as its own foreground call and record its numbers before starting the next.

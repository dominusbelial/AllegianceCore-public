# P166 — Wingman combat AI, Stage 2: defend an ally (`c_cidDefend` re-evaluation)

Task scope: the native `c_cidDefend` "protect an ally" decision layer
(`Igc/shipIGC.cpp:2373-2407`), ported on top of P162's existing simplified
pursuit/fire machinery — **no new combat physics, no dodging, no lead-solving**.

## What this task ports

Native splits the Wingman combat block (`shipIGC.cpp:2353-2787+`) into an
execution part (dodge / `solveForLead` / `turnToFace` / shoot-skills — all
still excluded) and a decision part (which enemy to chase, and when to return
to guarding duty). This task ports only the decision part, in two legs:

- **Part B — the re-evaluation block** (`shipIGC.cpp:2373-2407`, quoted in the
  task brief): while a Wingman is *attacking* (`plan == c_cidAttack`), each
  tick it decides whether to keep chasing its current `AttackTarget`, switch to
  a closer threat near the charge, or fall back to escorting the charge.
- **Part A — the companion acquisition scan** (`shipIGC.cpp:2680-2705`): while
  a Wingman is *escorting* (`plan == c_cidDefend`), it periodically scans for
  an enemy within 1500 of the charge and engages it. Included because the
  "Wingman engages an enemy near the charge" scenario the task's test specifies
  requires a charge-centered acquisition path, and because gating out the plain
  idle default-order for a defender (so it does not abandon its post or
  instantly re-engage the very foe it just broke off from) demands an alternate
  acquisition source. Together the two legs are the complete `c_cidDefend`
  decision life cycle; the actual pursuit/fire stays P162's
  `UpdateWingmanCombat` unchanged.

## Thresholds ported exactly (named constants, native values cited)

`src/Allegiance.Core/Sim/Ship.cs` (near the P162 `WingmanMaxEngagementStandoff`
/ `WingmanFireFacingCosine` block):

| Constant | Value | Native |
|---|---|---|
| `WingmanDefendAbandonDistance` | 3500 | `shipIGC.cpp:2380` (`distToProtectee > 3500 → shouldReturn`) |
| `WingmanDefendEvaluateDistance` | 2200 | `shipIGC.cpp:2382` (`else if distToProtectee > 2200`) |
| `WingmanDefendScanRadius` | 2500 | `shipIGC.cpp:2383` (`float dist = 2500.0f`) |
| `WingmanDefendSwitchMargin` | 1000 | `shipIGC.cpp:2385` (`dist < distToProtectee - 1000`) |
| `WingmanDefendAbandonRunawaySpeed` | −30 | `shipIGC.cpp:2395` (`targetDirSpeed < -30`) |
| `WingmanDefendAcquireRadius` | 1500 | `shipIGC.cpp:2689` (Part A scan: `float dist = 1500.0f`) |

**On the brief's "−30/−20/−50" grouping:** only **−30** belongs to the defend
block. The −20/−50 the brief loosely groups with it are the relative-speed
closing-distance thresholds of the **excluded** larger combat block
(`shipIGC.cpp:2494-2502`, the `relativeSpeed < -20 / < -50` maneuvering) — a
separate, out-of-scope piece (dodging / speed management). Only −30 is ported;
this is stated explicitly rather than silently picking up foreign thresholds.

## Accepted simplifications (each stated, not hidden)

- **The 200-tick cooldown (`m_checkCooldown = 200`, `shipIGC.cpp:2406`) is
  dropped.** It is a native *performance* optimization, not a behavioral
  requirement. This port re-evaluates every tick (its established per-tick
  `OnUpdate` cadence). Checking more often only ever produces the same-or-better
  decision, never a worse one — the conservative direction of the change is safe
  and simpler. (`UpdateWingmanDefend`'s remarks, item 1.)
- **"Fall back to escorting" = clear `AttackTarget` and steer toward the
  charge.** Native's `SetCommand(c_cmdPlan, charge, c_cidDefend)`
  (`shipIGC.cpp:2404`) is expressed here by clearing `AttackTarget` (the port's
  "not attacking") and letting `ApplyNpcAutopilot`'s new `DefendTarget` branch
  steer toward the charge's **live** position through the same shared
  `SteerToward` primitive — no new steering code (task Scope item 5).
- **The "just running away" reference point.** Native measures the chased
  target's velocity along `Orientate(chargePos − c_cmdCurrentPos)`
  (`shipIGC.cpp:2393-2394`); `c_cmdCurrent` is the Wingman's own current-position
  reference in this context, so the port uses
  `normalize(charge.Position − Position)` — the component of the target's
  velocity toward the charge. Below −30 the target is fleeing the guarded area
  → return to escort. (`UpdateWingmanDefend` remarks, item 4.)

## Center-position search design

Task Scope item 3 asked: refactor `FindNearestEnemyShip` to take an optional
center, or add a sibling method. **Chosen: a sibling method**
`Ship.FindNearestEnemyNear(Vector3 center, ref float radius)`
(`Ship.cs`, beside `FindNearestEnemyShip`). Reasoning:

- The defend search needs **two** behaviors P162's `FindNearestEnemyShip` lacks:
  a **radius bound** and the **found-distance output** (native's mutable
  `dist` in/out: `FindTargetShip(this, &chargePos, dist, c_ttEnemy|c_ttNearest,
  -2)`, `shipIGC.cpp:2384,2690`).
- Folding center + radius + distance-out into optional params on
  `FindNearestEnemyShip` would overload one method with three orthogonal
  concerns and put P162's hot-path, behavior-sensitive call at risk.
- A focused sibling that centers on an arbitrary position (radius in, distance
  out) mirrors native's `FindTargetShip` signature directly, satisfies the
  "arbitrary center" requirement (the charge's **live** position, not the
  Wingman's own), and leaves P162's `FindNearestEnemyShip()` byte-for-byte
  unchanged.

The sibling reuses the identical different-`SideId` enemy classification
(`c_ttEnemy`, no alliance system) and walks
`Sector.GetShipUpdateCandidates()` — so it is same-sector-only by construction,
consistent with P162's same-sector-only Wingman scope. Radius-in/distance-out:
on success `radius` becomes the found distance (feeding the
`dist < distToProtectee − 1000` switch test, `shipIGC.cpp:2385`); on failure it
is left unchanged and the method returns null.

## Cross-sector charge handling (task "confirm ... and state plainly")

Native: `GetCluster() != charge->GetCluster()` → shouldReturn
(`shipIGC.cpp:2376-2377`). This port is same-sector-only (P162 — no cross-sector
pursuit), and in **this task's tests the charge always sits in the Wingman's
own sector**, so the guard never fires there. It is nonetheless kept
defensively in `UpdateWingmanDefend`: if the charge is ever in a different
sector, the attack target is cleared and — the charge being unreachable across
sectors in this port — the Wingman holds position (`ApplyNpcAutopilot`'s escort
branch requires `charge.Sector == Sector`, so no goal is produced). Stated
plainly, not silently dropped. (`UpdateWingmanDefend` remarks, item 3.)

## Wiring

- `Wire/OrderChange.cs`: new `CidDefend = 3` constant (`Igc/igc.h:904`),
  following the existing constants' exact style. Not yet consumed by any wire
  handler (see exclusions).
- `Ship.DefendTarget` — the charge slot, distinct from `AttackTarget`.
- `Ship.FindNearestEnemyNear` — the charge-centered search (above).
- `Ship.UpdateWingmanDefend` — the per-tick defend decision leg (Parts A+B).
- `Ship.ApplyNpcAutopilot` — new `DefendTarget` escort branch (only when
  `AttackTarget is null`), reusing `SteerToward`.
- `Ship.OnUpdate` — calls `UpdateWingmanDefend` before `UpdateWingmanCombat` for
  a defending Wingman; gates the plain idle `DecideWingmanDefaultOrder` behind
  `DefendTarget is null`; adds `DefendTarget` to the take-cover "not idle" gate
  (the established per-slot precedent P161/P162/P165 set).

## Explicit exclusions (confirmed real, separate, still out of scope)

- **Dodging, lead-solving (`solveForLead` / `turnToFace`), shoot/turn-skill
  tiers, the relative-speed closing-distance maneuvering (`shipIGC.cpp:2494-2502`,
  whence the brief's −20/−50), and the "investigate last-known-position" buoy
  logic** (`shipIGC.cpp:2409-2461`) — all part of the much larger, still-excluded
  combat-maneuvering block. This task only ports the defend/re-evaluation
  decision layer on top of P162's simplified pursuit+fire.
- **Assigning `DefendTarget` via a wire/ORDER_CHANGE handler** — the `CidDefend`
  constant and ship-side behavior only; wiring a real player's ORDER_CHANGE to
  set a Wingman's charge is a separate, later task. This task's test sets
  `DefendTarget` directly (the established pattern every prior decision-cycle
  test uses).
- **Any change to P103-P165 code** beyond the `CidDefend` constant, the
  `DefendTarget` slot, `FindNearestEnemyNear`, `UpdateWingmanDefend`, and its
  `ApplyNpcAutopilot`/`OnUpdate` wiring.
- `Weapon.cs` is untouched — firing stays the pre-existing `StateM`-bit
  mechanic P162 selects.

## Verification

- Baseline re-confirmed before changes: **2796** tests (`dotnet test`,
  `net10.0`); the single baseline failure was the documented
  `LiveSuicideShipDeleteTests.Combat_death_produces_ship_delete_to_same_audience`
  flake, not a regression.
- New live loopback tests (`NpcWingmanDefendLiveTests`, 2 tests):
  1. `Defender_disengages_when_chased_enemy_flees_and_returns_to_charge` — the
     defender engages an enemy near the charge, the enemy flees beyond 3500,
     the Wingman actually disengages (`AttackTarget` cleared) and flies back
     toward the charge (measured distance decrease).
  2. `Defender_switches_to_closer_threat_near_charge_while_chasing_distant_one`
     — the defender switches `AttackTarget` to a closer enemy that appears near
     the charge (the 2200-band / 2500-scan / "1000 closer" rule).
- Related suites pass together (`NpcWingmanAttackLiveTests` P162, both P166
  tests, `NpcPilotTypeGateLiveTests`, `NpcLayerDefaultOrderLiveTests`,
  `NpcBuilderDefaultOrderLiveTests`, `NpcAutopilotLiveTests`) — 13/13.
- Full suite: 3 consecutive clean runs (expected 2798 with the 2 new tests).
- Session stats logged via `session_stats.py`.

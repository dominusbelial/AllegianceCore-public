# P169 — Wingman combat AI, Stage 3: aim leading (`solveForLead`)

Branch `master`, atop `08b5824` (P168 fixup). 3 consecutive clean full-suite
runs (see Verification), plus the two new P169 tests isolated-green and
empirically validated to fail when the lead gate is removed.

## What this stage is

P162's `Ship.UpdateWingmanCombat` decided to fire when the Wingman was in
weapon range and facing the target's *current* position — explicitly with no
aim leading. This stage adds real aim leading: predicting where to point so a
shot actually intercepts a *moving* target.

The whole task is wiring, not new physics, because of two confirmed reuse wins:

1. Native's `solveForLead` (`Igc/common.cpp:121-149`) is literally
   `solveForImpact` called with the projectile's speed instead of `0` — and
   this port already carries a faithful port of `solveForImpact` from P168:
   `Ship.SolveForImpact` (`src/Allegiance.Core/Sim/Ship.cs`, the utility P168's
   dodge already consumes). `SolveForLead` is a thin wrapper over it, with the
   exact same signature shape (deltaP, deltaV, speed, radius → out direction).
2. `solveForLead`'s two projectile-data inputs — `pt->GetSpeed()` and
   `pt->GetAbsoluteF()` — are already decoded and already read by this port's
   existing `Weapon.FireWeapon` (`src/Allegiance.Core/Sim/Weapon.cs:230-278`)
   for the *actual fired shot's* physics (`_projectileType.Speed`,
   `_projectileType.AbsoluteF`). `SolveForLead` reads the same two fields from
   the same `DataProjectileTypeIGC` (`src/Allegiance.Core/Data/DataProjectileTypeIGC.cs:26,74`),
   keeping the predict and the fire-physics on one projectile definition.

## `Ship.SolveForLead` — the faithful formula

Added to `Ship.cs` immediately after `SolveForImpact`, as an instance method
called from the Wingman's own fire decision:

```
SolveForLead(Ship target, Weapon weapon, float skill, out Vector3 direction)
```

Maps `common.cpp:132-148` term-for-term:

- **Shooter position** = `Ship.Position`, the ship's own position directly.
  Native adds `weapon->GetPosition() * myOrientation` (the gun barrel's local
  offset transformed to world), which has **no analogue here (no hull mesh)** —
  the identical omission the existing fire path already accepts at
  `Weapon.cs:230` (`"myPosition = _ship.Position; // + weapon muzzle offset
  (none: no hull mesh)"`). Inherited, not re-justified.
- **Target velocity, skill-scaled**: `hisVelocity = target.Velocity * skill`
  (`common.cpp:137`). Skill scales the *target's* velocity, i.e. a lower skill
  leads less. The plain-Wingman constant `WingmanShootSkill = 0.75f` matches
  native's `fShootSkill = 0.75f` (`Igc/shipIGC.cpp:2359`; only varied for
  `c_ptCheatPlayer`, `shipIGC.cpp:2366-2369`, which this port has no analogue
  for and doesn't need). Named+cited following the `WingmanFireFacingCosine`
  (P162) precedent for a tuned constant.
- **Projectile speed** = `weapon.ProjectileType.Speed` (`common.cpp:141`).
  Native multiplies by the side's `c_gaSpeedAmmo` attribute when ammo is
  spent; this port has **no side-economy attribute for it** — the identical,
  already-accepted omission stated at `Weapon.cs:233` (`"c_gaSpeedAmmo boost
  omitted (no side)"`). Inherited, not re-justified.
- **`AbsoluteF` gates the velocity term** feeding the intercept solve
  (`common.cpp:144-146`): an absolute projectile (motion doesn't inherit the
  shooter's own velocity) uses the (skill-scaled) target velocity alone;
  otherwise it uses that velocity relative to the shooter's own
  (`hisVelocity - myVelocity`). Read from the mounted weapon's real
  `DataProjectileTypeIGC.AbsoluteF` — neither branch is hardcoded.
- **Target radius** = this port's `0.5 × HullType.Length` ship-radius
  convention (the `GetRadius()` analogue, `Ship.cs:1873` — the same convention
  `EstimateWeaponRange`/`FindNearestEnemyShip`-adjacent radius math uses).

Returns the intercept time `t ≥ 0` (whatever `SolveForImpact` computes) and
fills `direction` with the world-space unit lead direction.

## The fire decision change (`UpdateWingmanCombat`)

Previously the fire gate measured facing against the target's raw current
position. Now, when the Wingman has a mounted weapon, it computes the lead
direction via `SolveForLead` and gates firing on facing *that* direction
within the existing `WingmanFireFacingCosine` tolerance — only the direction
the tolerance is measured against changes; the tolerance constant and the
range gate (`EstimateWeaponRange`) are untouched.

- **Which weapon leads**: native uses the FIRST mounted weapon
  (`m_mountedWeapons[0]`, `shipIGC.cpp:2353,2466`) — mirrored as `Weapons[0]`.
- **No weapon ⇒ no lead**: if the Wingman has no mounted weapon, fall back to
  P162's facing-the-raw-position behavior (no lead to solve) — the same
  empty-weapons null-safety `EstimateWeaponRange` already exercises for range.
- **No-solution guard**: `SolveForImpact`'s no-intercept branch leaves
  `direction = default` (zero) and returns its mmf sentinel
  (`Ship.cs:2919`). `UpdateWingmanCombat` checks `leadDir.LengthSquared() >
  1e-6` and falls back to raw facing on zero — native leaves `*direction`
  *unset* in that branch (`common.cpp:91`, undefined behaviour); this port
  declines to reproduce it and treats "no longer reachable" as "not worth
  leading at anyway". Documented in the code.

## The fire-facing-only scope boundary (the honest simplification)

**This task changes only the fire-facing *check*, not the ship's pursuit
steering.** Native couples the whole ship's turn-to-face to the lead direction
(`turnToFace(direction, ...)`, `shipIGC.cpp:2470`) — the entire ship turns to
the aim point and the bullets (fired along the nose) consequently fly at the
lead point. This port's `ApplyNpcAutopilot` combat-approach branch (P162)
still points the nose at the target's *live* position for pursuit/closing via
the shared `SteerToward`, and this task does not change that. The decoupling —
"where to fly" stays at the live position; only "where to aim for the fire
check" is the lead direction — is the honest, stated simplification, exactly
as scoped. Consequences stated plainly rather than hidden:

- Because the nose still chases the live position, the *actual bullet* still
  leaves along the nose (toward the current position) in this port; lead here
  corrects the *decision* to fire, not the ballistic point of impact. The
  turn-the-nose-to-lead coupling is explicitly out of scope (below).
- The discriminating live test therefore asserts the fire *gate* obeys the
  lead direction (it refuses a raw-aligned-but-not-lead attitude), not that a
  fast laterally-moving target is magically hit harder — which the gate-only
  change alone cannot deliver.

## Explicitly out of scope (unchanged, as directed)

- **`turnToFace` / skill-tiered turn-rate maneuvering** — P155's turn law
  (cross-product, reused by `SteerToward`) untouched; this task only changes
  the fire-facing *check*, not how fast the ship turns.
- **The relative-speed closing/backing-off maneuvering** (−20/−50 thresholds,
  excluded from P166) — separate, larger work.
- **The "investigate last-known-position" sight-loss fallback** — separate.
- **`c_gaSpeedAmmo` and muzzle-offset modeling** — both confirmed absent from
  this port already (see `Weapon.cs:230,233`); inherited as-is, not added.
- **No change to P103-P168 code** beyond `SolveForLead`, the new
  `WingmanShootSkill` constant, and `UpdateWingmanCombat`'s fire-facing gate.

## Tests

Two new tests in `tests/Allegiance.Core.Tests/NpcWingmanLeadLiveTests.cs`:

1. **`SolveForLead_intercepts_a_laterally_moving_target`** (deterministic,
   loopback-free) — drives `Ship.SolveForLead` directly via reflection (the
   same private-member reflection pattern `NpcCrossClusterLiveTests` already
   uses) on a geometrically-constructed target at the origin drifting +Y while
   the shooter sits 500 u out on −X. Asserts the solved aim direction (a)
   swings toward the target's motion (`dir.Y` > 0, `dot(raw)` < 0.996) and (b)
   **actually intercepts**: the round fired along `dir` at the projectile's
   speed lands on the target's radius sphere at the solved time
   (`|pf − tf| = targetRadius`, measured `8.500 = 8.500`). Cleanly fails if
   lead-solving is removed or wrong; no sim variance.
2. **`Wingman_fire_gate_leads_a_laterally_moving_enemy`** (live loopback,
   booted in-process server per the P162 harness) — a real Wingman NPC with a
   slow-projectile weapon (proj speed 400, nominal lead ≈ 41° ≫ the 0.90
   tolerance) engages an enemy driven laterally (kinematically, so the motion
   is exactly prescribed). Asserts the gate **refuses to fire** at an in-range
   snapshot where the Wingman clearly faces the raw position but clearly does
   NOT face the lead point, and never fires while clearly off-lead.
   **Empirically validated to fail when lead-solving is removed**: with the
   gate temporarily reverted to raw facing the test failed with
   `fired=1, firedOffLead=1, rawAlignedButNotFiring=0` — proving the
   discriminator is genuine, not always-pass.

Existing suites exercising the shared fire-decision method —
`NpcWingmanAttackLiveTests.cs` (P162, head-on/stationary) and
`NpcWingmanDefendLiveTests.cs` (P166) — pass unmodified, confirming no
regression of the simpler cases.

## Verification

- Baseline confirmed: `master` at `08b5824`, `git log` top = P168 fixup.
- Built with `/home/domingo/.dotnet/dotnet` (the box's `dotnet` on PATH is the
  broken snap stub; the real SDK is at `/home/domingo/.dotnet/dotnet`).
- No VM involved at any point.
- 3 consecutive clean full-suite runs (see below). The five documented flakes
  (`LiveChatSectorWireTests`, `LiveCombatWireTests`,
  `LiveInSpaceEjectTests.Already_a_lifepod_is_truly_killed_not_re_ejected`,
  `LiveSuicideShipDeleteTests.Combat_death_produces_ship_delete_to_same_audience`,
  `ShipsUpdateLiveTests.Locked_target_fills_first_heavy_slot_with_real_fractions`)
  are rerun-in-place on fire, not treated as regressions.
- Session stats logged via
  `python3 ~/Development/allegiance-tools/session_stats.py <session_id>`.

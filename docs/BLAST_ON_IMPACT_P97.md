# P97 — Missile / projectile blast-on-impact and blast-on-expiry

## Summary

`Missile`/`Projectile` had decoded-but-unused `BlastPower`/`BlastRadius` fields
(they were read back in P88/P91 but never acted on). P97 wires them into the
simulation's existing explosion machinery (`Sector.ResolveExplosion`, P96), for
both ordnance types, on both of the real native triggers.

The directly-hit target of an area-effect round takes damage **twice** — once
from its direct single-target `Power` hit, once again from its own blast's
falloff-adjusted `BlastPower`. This is intentional native behaviour
(`projectileIGC.cpp:211`, *"Area effect weapon: create an explosion (no matter
what)"*), **not a bug** and **not a duplicate** — the task named it as the
mechanic to preserve, and it is preserved as-is.

## Source changes

| File | Change |
|---|---|
| `src/Allegiance.Core/Sim/IOrdnance.cs` | Added `BlastPower` / `BlastRadius` to the shared ordnance shape. |
| `src/Allegiance.Core/Sim/Projectile.cs` | Implemented both properties off `ProjectileType` (`DataProjectileTypeIGC.cs:56-60`); added blast-on-expiry to `OnUpdate`. |
| `src/Allegiance.Core/Sim/Missile.cs` | Implemented both properties off `MissileType` (`DataMissileTypeIGC.cs:504-508`); added blast-on-expiry to `OnUpdate`. |
| `src/Allegiance.Core/Model/Sector.cs` | Added the blast-on-impact trigger to `ResolveCombatCollisions`' ordnance-hits-ship block. |

Each edit cites the exact native lines it ports in its code comment.

### 1. `IOrdnance` exposes `BlastPower`/`BlastRadius`

Interface gets two new members (`IOrdnance.cs:57-65`):

```csharp
float BlastPower { get; }
float BlastRadius { get; }
```

Both already decode cleanly from the type records (`DataProjectileTypeIGC.cs:20-21,68-69`,
`DataMissileTypeIGC.cs:64-65,130-131`), so the implementation is a one-line passthrough in each:

- `Projectile.cs:83-86` — `IOrdnance.BlastPower => ProjectileType.BlastPower;` etc.
- `Missile.cs:119-122` — same off `MissileType`.

### 2. Blast-on-impact (`Sector.ResolveCombatCollisions`)

The ordnance-hits-ship block already (P91) does the direct single-target hit
via `IOrdnance`, so the addition lands in that **one shared code path** for
both `Projectile` and `Missile` (`Sector.cs:358-378`): after the direct
`ship.ReceiveDamage(...)` call and the ordnance's removal from `_models`, if
`ord.BlastPower != 0f` it builds a real `Explosion` —

```csharp
new Explosion(ord.DamageType, ord.BlastPower, ord.BlastRadius, now, position1, ord.Launcher)
```

— centred on `position1` (the already-computed impact position,
`projectileIGC.cpp:174`), handed to the untouched `Sector.ResolveExplosion`
(P96, `Sector.cs:564-621`), which applies squared-distance falloff to every
`Ship`/`Mine` in blast range. Because the ordnance is removed before the blast
resolves, the spent round is never re-processed by its own explosion; the
`Launcher` is preserved so the existing friendly-fire/self gate applies to the
splash exactly as to any launcher-sourced damage. The direct-hit damage path is
**untouched**.

### 3. Blast-on-expiry (`Projectile.OnUpdate` / `Missile.OnUpdate`)

A round that times out without hitting anything detonates at its **expiry
position** — where it actually was when time ran out, not its launch position —
mirroring `projectileIGC.cpp:128-139` (and, for missiles, the inherited Update
via `Explode(GetPosition())`, `missileigc.cpp:356-360,516-532`). Both
`OnUpdate`s do the same thing (`Projectile.cs:122-142`, `Missile.cs:158-176`):

- advance position by `velocity·dt`;
- on `now >= _timeExpire`, if `BlastPower != 0f`, build an `Explosion` and
  resolve it, then remove self.

**Faithful expiry position.** The native projects the blast to the exact
burnout instant — `GetPosition() + GetVelocity()·(m_timeExpire −
GetMyLastUpdate())` (`projectileIGC.cpp:136-137`). This port's per-chunk advance
overshoots `now` past expiry by up to one chunk, so it unwinds that overshoot:

```csharp
var expiryPos = Position - Velocity * (float)(now - _timeExpire);
```

giving the precise straight-line burnout point (verified in the expiry test: the
round that burns out at `(0,0,-4000)` blasts exactly as if it died there).

## Scope respected

- `ResolveExplosion`'s target list is **unchanged** — still `Ship` + `Mine`
  only (`Sector.cs:571-621`). No ordnance-vs-ordnance blast chaining, no
  asteroid/station/probe targets, no chaff interaction (all explicit deferrals).
- The existing direct-hit path and friendly-fire gate are untouched.

## Tests added — `tests/Allegiance.Core.Tests/BlastOnImpactTests.cs`

Three data-derived tests (every number from `PCore001.igc`). Real observed
values:

**1. Impact — two real, distinct, additive damage components**
`Impact_round_deals_direct_plus_own_blast_damage_two_additive_components`:

```
target 1.00 → 0.8360  =  1 − [0.1000 direct + 0.0640 blast]  =  1 − 0.1640
```
The directly-hit target's hull drops by the **sum** of its direct `Power` and
its own blast's falloff-adjusted `BlastPower` (centred at the impact surface,
so the direct target's own-blast amount is `BlastPower·(1 − d/Radius)²` with
`d` = projectile radius — a real falloff, not the full blast). The assertion
also proves strict additivity (`target.Fraction < 1 − directFrac`). The shooter
(launcher) is untouched.

**2. Nearby second ship takes ONLY the blast splash**
`Nearby_second_ship_takes_only_the_blast_splash_no_direct_component` (big
area gun, `BlastRadius` 250):

```
primary   1.00 → 0.1607  (direct 0.4545 + own-blast 0.3847)
secondary 1.00 → 0.8295  =  1 − 0.1705  (blast-only falloff, d = 96.9)
```
The second ship (100 lateral, never on the beam — no direct hit possible)
takes only the falloff splash, a real distinct number strictly smaller than the
primary's direct+blast total.

**3. Blast-on-expiry**
`Round_that_times_out_still_detonates_at_its_expiry_position`:

```
victim 1.00 → 0.9424  =  1 − 0.0576   (d = 161.0 from expiry point (0,0,-4000))
```
A blast round fired into empty space hits nothing, burns out, and still
detonates at its exact expiry position, blasting the off-beam victim — no direct
hit ever occurred. The victim's damage is the exact falloff from the burnout
point, confirming the expiry-blast position is where the round truly died.

**4. No-blast round unchanged** — every `BlastPower == 0` path still behaves
exactly as before; proven by the full pre-existing suite (below) passing
unmodified. Independently verified against the corpus: the weapon `CombatDamage
Tests`/`CombatShieldTests`/`LiveCombatWireTests` select fire **non-blast**
projectiles (`DataProjectileTypeIGC.BlastPower == 0` for projectile ids 2/21/84/85),
and **no missile type in PCore001.igc has blast power at all** (all
`DataMissileTypeIGC.BlastPower == 0`), so the existing suites were unaffected.

## Verification

- `dotnet build` clean (0 warnings, 0 errors).
- New P97 suite: 3/3 pass.
- Full suite: **3 consecutive clean runs — 2358 pass / 0 fail** each (2355
  baseline + the 3 new P97 tests), the two units that existed at P96 verified
  via `ExplosionPropagationTests.cs`/`CombatDamageTests.cs`/etc. passing
  unmodified.
- No VM used.
- Session stats logged via `session_stats.py 20260907_205831_940fb6`.

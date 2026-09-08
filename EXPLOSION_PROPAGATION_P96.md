# P96 — Real explosion / blast propagation, closing mine destructibility

## What this task delivers

A generic **`Explosion`** type + a **blast-resolution pass**
(`Sector.ResolveExplosion`) that distributes squared-distance-falloff damage to
every **ship** and **mine** in blast range, and rewires P95's mine detonation so
it triggers a *real* explosion instead of a single-target hit. The one
integration point is the mine-detonation call site; mine destructibility —
`Mine.ReduceStrength` and the `0.25f` destruction threshold — is the mechanic
this pass finally exercises. All the native file:line references below were read
from `/home/domingo/Development/Allegiance/src/Igc/` (the native checkout), not
invented.

## Files changed

- `src/Allegiance.Core/Sim/Explosion.cs` — new `readonly record struct Explosion(DamageType, Amount, Radius, Time, Position, Launcher)`.
- `src/Allegiance.Core/Sim/Mine.cs` — `ReduceStrength(float)` (new), `Fraction` now mutable.
- `src/Allegiance.Core/Model/Sector.cs` — `ResolveExplosion(Explosion, double)` (new public pass); `ResolveMineDetonations` now builds + resolves an `Explosion` instead of a single `ReceiveDamage`.
- `tests/Allegiance.Core.Tests/ExplosionPropagationTests.cs` — 5 new tests (1 live loopback, 4 domain).

## The explosion type — `Explosion` (`Explosion.cs`)

Native `ExplosionData` is assigned in `CclusterIGC::CreateExplosion`
(`clusterIGC.h:340-367`): `damageType, amount, radius, explosionType, time,
position, launcher`. **`explosionType` is dropped** — it is a *visual-effect*
id handed straight to the render client (`clusterIGC.cpp:407`:
`m_pClusterSite->AddExplosion(e.position, e.radius, e.explosionType)`); this
headless port has no visual system, so the id has no analogue. Stated plainly.
The remaining six fields map 1:1 onto the native assignment
(`clusterIGC.h:354-360`).

Unlike `DataShipIGC`/`DataProjectileIGC`, native `ExplosionData` is **pure
in-memory sim state** — a member of `CclusterIGC`'s `m_explosions` ring
(`clusterIGC.cpp:406`), never serialized — so the port's representation is just
a plain C# type with no wire codec, no byte-layout/alignment concern.

## The resolution pass — `Sector.ResolveExplosion` (`Sector.cs`)

Port of the native explosion block (`clusterIGC.cpp:362-467`). For every live
`Ship` within `e.Radius` of `e.Position`, applies `ReceiveDamage` with the exact
native falloff `f = 1 − d/radius; amount *= f·f` where `d` is the distance from
blast centre to the ship's *surface* (`d = |e.pos − p| − pTarget->GetRadius()`,
`clusterIGC.cpp:420,424-429`; ship radius = `0.5·length`,
`shipIGC.cpp:1419`). For every live `Mine` within `e.Radius`, applies
`Mine.ReduceStrength(amount)` with the identical falloff (`clusterIGC.cpp:448-
458`).

### Immediate-resolution simplification (stated plainly)

Native queues an `ExplosionData` and resolves it on a *later* tick, projecting
each target's position by `dt = (e.time − m_lastUpdate) − m_tOffset`
(`clusterIGC.cpp:409,419`). This port resolves **immediately/synchronously**
against each target's **current** position. Same honest simplification category
as P95's "no swept dwell-time math." Since the only trigger today
(`ResolveMineDetonations`) fires immediately and ships/mines stand effectively
still on that tick, the projection would add only a fraction of a second of
displacement.

### Mines are treated as point targets in the falloff (labelled)

Native subtracts the target's `GetRadius()` (`clusterIGC.cpp:448`), and for a
mine that is `dataMineType->radius` (`mineigc.cpp:106`:
`SetRadius(dataMineType->radius)`). **The port's decoded base `Radius` for
`OT_mineType` is a constant junk sentinel** — every mine in `PCore001.igc`
decodes to −1.07e8 (`ExplosionPropagationTests`/scratch, all 8 mine types). The
field is therefore **not** fed into the falloff; a mine is treated as
point-like (radius 0), so it is hit iff its position is inside `e.Radius`.
Deviation from native is a few units of surface radius vs. ~100-unit blast
radii — negligible. (Ships DO subtract their real `0.5·length` radius.)

### Shared damage path + the existing friendly-fire gate

Ships flow through the **reused `Ship.ReceiveDamage`** — shields, armor matrix,
`DamageShipEvent`/`KillShipEvent` all intact, not reimplemented. The friendly
gate is the existing one, not a new invention: with a `Ship` launcher, a target
that *is* the launcher or shares the launcher's `SideId` is skipped
(self-exclusion + `mineigc.h:77`/`shipIGC.cpp:1222-1228` convention, same as
`ResolveCombatCollisions`). **Mines are not side-gated** — native calls
`ReduceStrength` on every mine in radius regardless of side
(`clusterIGC.cpp:441-458`).

## `Mine.ReduceStrength` and `Endurance` (`Mine.cs`)

Ported verbatim from `ImineIGC::ReduceStrength` (`mineigc.h:170-181`):

```
endurance       = MineType.Endurance
newEndurance    = endurance * m_fraction * exp(-amount / endurance)
m_fraction      = newEndurance / endurance            // = m_fraction · exp(-amount/endurance)
if (m_fraction < 0.25f) KillMineEvent(...)            // else SetMineStrength(fraction)
```

**`DataMineTypeIGC.Endurance` — confirmed and now used.** This is its first
real consumer (unconfirmed/unused since P94/P95). The field maps to native
`DataMineTypeIGC.endurance`, the denominator of the exponential decay, and the
formula is ported verbatim. Native also calls `SetMineStrength(m_fraction)` on a
non-destructive hit (`mineigc.h:180`) to broadcast the reduced fraction; this
port's mine wire payload has no strength byte, so that broadcast is a
deliberate no-op (the reduced fraction lives on the sim object). Below the
`0.25f` threshold the mine is removed from its sector and raises
`MineDestroyedEvent` exactly once — the same consistency contract as the P94
expiry / P95 detonation paths (`_sector.Remove` + the `OnUpdate`-expiry pair).
A defensive guard handles `Endurance <= 0` (native divides by zero; no real
core hits it — PCore001 mines are ~1400–3000).

## P95 behaviour change (stated plainly)

`ResolveMineDetonations` formerly applied a single full-`Power` hit to the
triggering ship. It now builds an `Explosion` at the mine's position
(`MineType.DamageType/Power/MineRadius`, launcher = the laying ship), consumes
the mine, then hands it to `ResolveExplosion` — so a mine detonation becomes a
real **multi-target blast** affecting every ship and mine in range. This is
more faithful to native, not less. P95's own single-nearby-target tests still
pass unchanged because a lone overlapping target takes full/near-full
falloff-adjusted damage (verified below).

## Verification — real results

New `ExplosionPropagationTests` (all data-derived from `PCore001.igc`, no
hardcoded values), with console output:

- **Live loopback** (3-bot / 2-side, real `MineCombatTests` construction):
  `P96-LIVE: enemy hull 1.00→0.843 (expect 0.843, d=44.0); mineB 1.00→0.982
  (expect 0.982, d_B=70.0); observer 1.00 unharmed (friendly gate)` — two real
  deployed mines; mine A detonates on a cross-side ship and its blast reaches
  mine B (fraction 1.00→0.982, a real `ReduceStrength` effect, not just the
  trigger's own removal) and the ship (exact falloff-adjusted 0.843). A same-side
  observer parked inside the same blast takes nothing.
- **Falloff is real:** `P96-FALLOFF: centre 0.5000 (Δ0.5000), edge 0.8750
  (Δ0.1250)` — overlapping-centre ship takes full Power·dtm/HP; a ship whose
  surface sits at d=½·radius takes exactly Amount·(½)². Two distinct numbers.
- **Shield absorption shared:** `P96-SHIELD: absorbed 0.250 (shield 1.0→0.750,
  hull unchanged)` — a shielded ship in blast absorbs through the same
  `ReceiveDamage` path (CombatShieldTests recipe).
- **Friendly gate:** `P96-FRIENDLY: same-side 1.0000 untouched; cross-side
  0.7112 (Δ0.2888)`.
- **Accumulated destruction:** `P96-ACCUMULATE: 982 destroyed after accumulated
  blasts (fraction 0.2466, event exactly once)` — repeated blasts push a mine
  below 0.25; it is removed from the sector and raises `MineDestroyedEvent`
  exactly once.

**Regression:** the full suite passes 3 consecutive clean runs at **2355/2355**
(with the 5 new tests). Prior combat/mine suites (`CombatDamageTests`,
`CombatWeaponFiringTests`, `CombatShieldTests`, `MissileCombatTests`,
`MineCombatTests`, `LiveCombatWireTests`) pass unmodified under the new
explosion-mediated detonation path. One `LiveCombatWireTests`
`Moving_target_streams_its_falling_hull_over_the_wire` failure appeared once
during a full-suite run and is a pre-existing login-assignment **race** (that
test passes 3/3 standalone, and involves projectile wire streaming, no mines) —
not a regression from this slice.

## Out of scope (as specified)

- Wiring `Missile`/`Projectile` expiry or impact into a real explosion — their
  `BlastPower`/`BlastRadius` stay read-but-unused this slice.
- Asteroids/stations/probes as explosion targets (no damage model yet).
- The native `dt`-offset queue timing, and the `c_maxDmgs`/`c_maxExplosions`
  caps.

## Natural next follow-up (not the next task — user's call)

Missile/projectile **blast-on-impact**: the `BlastPower`/`BlastRadius` deferred
since P88/P91, and `DataMissileTypeIGC`/`DataProjectileTypeIGC`'s fields, now
have a real resolution pass to plug into — feed `ImpactPosition` →
`new Explosion(damageType, blastPower, blastRadius, now, impactPos, launcher)`
from `Missile`/`Projectile`'s collision/expiry handling and mine destructibility
extends to cover warheads. This infrastructure makes that a small, mechanical
follow-up.

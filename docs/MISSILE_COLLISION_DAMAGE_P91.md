# P91 — Missile collision + damage resolution (Phase 2, slice 5)

Status: **REAL** — a missile now participates in the exact combat collision pass
that weapons/projectiles use, deals real single-target damage (shields included)
via the reused `Ship.ReceiveDamage`, and broadcasts a consistent destruction
notice whether it hits something or expires.

Branch: `master`. Slice: Phase 2 / slice 5 (follows P90's shipped non-homing
`Missile` sim object). Prior slices in this arc: P87 (shield damage), P88
(projectile broadcast), P89 (missile wire codecs), P90 (missile sim object +
fire/destroy same-side broadcast).

## What is real

**Single-target collision and damage, same rules as a projectile.**
`Sector.ResolveCombatCollisions` (`src/Allegiance.Core/Model/Sector.cs:287`)
now treats a `Missile` exactly like a `Projectile`:

- The early-exit guard fires when either a `Projectile` or a `Missile` is
  present (`Sector.cs:289`).
- The collider-construction switch builds a real sphere collider for a `Missile`
  from `MissileType.Radius`, `isStatic: false`, `HitShape.Sphere` — mirroring
  the projectile case (`Sector.cs:294`).
- Collision events are resolved through a shared shape, `IOrdnance`
  (`src/Allegiance.Core/Sim/IOrdnance.cs`), a **composition interface** (not a
  class base) implemented by both `Projectile` and `Missile` — so the whole
  event-resolution block — self-launcher exclusion, friendly-fire gate,
  impact-position math, `ReceiveDamage`, removal from `_models`, and kill
  removal of the target — runs once, and the friendly-fire logic cannot drift
  out of sync between the two kinds. This honors P90's documented decision that
  `Missile` and `Projectile` are *not* a shared class hierarchy (each is a port
  of a different C++ class); the interface is the idiomatic composition seam.
- A missile never hits its own `Launcher`; a missile never damages a ship on the
  same `SideId` as its launcher (when both carry a side) — friendly fire is
  always off and a friendly round/missile passes through without landing or
  terminating (`Sector.cs:320`).
- Damage is `ship.ReceiveDamage(MissileType.DamageType, MissileType.Power, now,
  position1, position2, missile.Launcher)` — **single-target only**, identical
  to the projectile call. `MissileType.Power` × the decoded hull armor constant
  (`DamageConstants`, via `Ship.ReceiveDamage`) is the exact, asserted hull
  delta. Since `ReceiveDamage` is P87's method, the shield absorption/leak math
  (mounted `Shield` part) comes along **for free** — proven live: a fresh full
  shield absorbs the computed fraction, and a knocked-down shield leaks the full
  raw hit exactly like an unshielded hull.
- A missile that strikes a target is removed from `_models` (spent, like a
  projectile), and the target is removed too if it died (`Sector.cs:347-348`).
- **Consistent destroy broadcast:** the impact-removal path raises
  `IIgcSite.MissileDestroyedEvent` (`Sector.cs:355`), the same event the expiry
  path raises (`Missile.OnUpdate`). A same-side peer who saw the launch thus
  learns of the destruction regardless of *why* (expired vs. hit something);
  the wire message `FM_S_MISSILE_DESTROYED(83)` carries no cause field, so
  nothing is lost by unifying. Proven live: the same-side observer that
  received `FM_CS_FIRE_MISSILE(70)` on launch receives `FM_S_MISSILE_DESTROYED(83)`
  for the same id after an impact.

Verified live (loopback `Bot`/in-process `Server`, 3-bot/2-side construction,
mirroring `MissileBroadcastTests`) and in-domain:

- A real cross-side missile impact drops the target's hull by the exact computed
  `MissileType.Power × dtmArmor / MaxHP` (asserted as a number, not "some
  damage"), the missile vanishes from the sector, and the same-side peer that saw
  it launch receives the destroy broadcast (`Cross_side_missile_impact_...`).
- A real cross-side missile into a mounted shield absorbs the computed
  `Power·dtmShield/MaxStrength` with the hull untouched, then (shield knocked
  down) leaks the full raw hit to the hull identically to the no-shield path —
  both numbers asserted (`Cross_side_missile_against_a_mounted_shield_...`).
- A real same-side shot deals **no** damage and passes through (terminating on
  nothing), then expires on its own and still broadcasts its destroy
  (`Same_side_missile_passes_through_a_friendly_...`).
- A weapon projectile and a missile coexist in one collision pass, each landing
  its own computed damage on a different target (`Missile_and_weapon_projectile_
  coexist_in_one_collision_pass`).
- P90's own expiry-path broadcast (`MissileBroadcastTests`) and the existing
  projectile/weapon/shield/wire combat suites (`CombatDamageTests`,
  `CombatWeaponFiringTests`, `CombatShieldTests`, `LiveCombatWireTests`) pass
  **unmodified** — the projectile path is byte-for-byte behaviorally identical.

## What is deferred (honest, read-but-unused)

- **Homing / turning / target-tracking.** `TurnRate`, `LockTime`, `MaxLock`,
  `Acceleration` are read and stored on the type record but never consulted — a
  missile still flies straight. (P90's own scope.)
- **Area-of-effect / blast damage.** `BlastPower` / `BlastRadius` are read but
  not acted on; a missile is single-target this slice, matching the projectile's
  current single-target scope. `MissileType.Radius` is the collision collider;
  the blast radius is *not*.
- **Chaff / spoofing interaction.** `ChaffResistance` is decoded data, unused
  here — a separate, later Phase 2 item.
- **Missile-vs-missile / missile-vs-projectile collisions.** The reference
  engine's combat pass is ship-vs-ordnance; mirror that scope exactly, the same
  way the projectile pass never considers projectile-vs-projectile.

## Next follow-up in this arc

**Missile homing/flight control** (or, if the arc continues to prefer combat
completeness first, **AoE/blast damage**) — homing is the headline deferred item
and needs the target/lock plumbing (`FireMissile`'s `TargetId`/`Lock` fields are
currently placeholders). After that, the next natural combat item is **blast
damage** (`BlastPower`/`BlastRadius`), which also enables the multi-ship area
hit that the reference `HandleCollision` applies. Chaff/spoofing is a distinct
later item.

## Design notes

- New file `src/Allegiance.Core/Sim/IOrdnance.cs` — the composition interface
  (`Launcher`, `Position`, `Velocity`, `Radius`, `DamageType`, `Power`) that the
  combat pass reads through; `Projectile` and `Missile` both implement it via
  their type records (`Radius`/`DamageType`/`Power` from `ProjectileType` /
  `MissileType`, `Launcher`/`Velocity`/`Position` already on each model).
- `Sector.ResolveCombatCollisions` is the single, shared resolver; the friendly-
  fire gate and impact math are written once and used by both kinds.
- All test expected numbers are data-derived from the real `PCore001.igc`
  static core (`DamageConstants` matrix + type records), nothing hardcoded.

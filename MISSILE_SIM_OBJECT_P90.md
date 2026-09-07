# MISSILE_SIM_OBJECT_P90

Phase 2, slice 4 — a real, observable missile that flies and expires,
broadcast to same-side peers on creation and destruction. Branch `master`,
top commit before this slice: `0b2b9f3` (P89, missile wire codecs).

## What is real (this slice)

- **`Sim/Missile.cs`** — a real sector `Model`, structurally a sibling of
  `Projectile` (not a repurposed projectile; the port treats a non-homing
  missile as its own sim class with its own decoded type). Constructed with
  `(id, sector, missileType, position, velocity, forward, now, launcher)`;
  moves in a straight line (`Position += Velocity·dt`, exactly
  `Projectile.OnUpdate`); expires once `now >= now + missileType.Lifespan`,
  removing itself from its sector.
- **`Ship.FireMissile(DataMissileTypeIGC, double)`** — a directly-callable
  method (not wired into live `ControlData`/`StateM` dispatch — there is no
  "fire missile" input bit in `ShipControlState`, confirmed at
  `ShipControlState.cs:13-44`). Fires along `Orientation.GetForward()` at
  `missileType.InitialSpeed` plus the ship's own velocity — the same
  "inherit the launcher's velocity" convention `Weapon.FireWeapon` uses for
  projectiles (`Sim/Weapon.cs:213-215`). Caller supplies the missile type
  directly, mirroring `Ship.MountWeapon` (`Sim/Ship.cs:301-311`), because the
  existing `Ship.Parts`/`MountPart` system cannot represent a mounted launcher
  at all today: `DataPartTypeIGC.EquipmentType` has no `Launcher` case
  (`DataPartTypeIGC.cs:9-19`) — a launcher is a separate 24-byte `OT_partType`
  size-class record (`DataLauncherTypeIGC.cs:6-14`), not an
  `EquipmentType`-tagged mountable.
- **Two new `IIgcSite` events**, no-op defaults, following the exact
  `ProjectileCreatedEvent` pattern: `MissileCreatedEvent(Missile)` raised in
  `Ship.FireMissile` right after `sector.Add`, and
  `MissileDestroyedEvent(Missile)` raised from the missile's own expiry path
  (`Sim/Missile.cs` `OnUpdate`, alongside `sector.Remove`) — the analogue of
  a ship death raising `KillShipEvent` (`Sim/Ship.cs:494`). Both bridged
  through `ServerHost` (`MissileCreated`/`MissileDestroyed` hooks) exactly as
  P88 bridged `ProjectileCreated`.
- **Same-side broadcast, both directions** (`Server.cs`): on creation, a real
  `FM_CS_FIRE_MISSILE(70)` with exactly one `MissileLaunchData` entry; on
  expiry, a real `FM_S_MISSILE_DESTROYED(83)` carrying where it expired
  (`≈ launchPosition + velocity·Lifespan`, not the launch position). Both use
  the identical `_teammateExportSent`-gated same-side audience as
  `BroadcastProjectileInstance` (the launcher's own peer plus same-side
  teammates who already hold the launcher's `ExportShip`).
- **Verified**: 3-bot / 2-side loopback test
  (`MissileBroadcastTests`) — teammate sees a real `FireMissile`(70) with one
  real `MissileLaunchData` entry, then a real `MissileDestroyed`(83) naming
  the same id at the expiry point; cross-side enemy sees neither even when
  ticks are pumped past expiry; firing twice yields two distinct ids and two
  distinct destroy events. Plus a pure-domain unit test (`MissileUnitTests`)
  for straight-line motion and expiry-triggers-removal-from-sector. Full
  suite green ×3 (2326 tests, see below).

## Deferred (honest — not reached for here)

- **Homing / turning / target-tracking.** `DataMissileTypeIGC`'s
  `TurnRate`/`LockTime`/`MaxLock`/`LockAngle`/`Acceleration`/`ChaffResistance`
  are read and stored on the type record but never acted on; the missile flies
  straight (P87-style "no-shield-regeneration" honest-deferral noted in
  `Sim/Missile.cs`). This is the explicit next step in this arc.
- **Collision / damage.** A missile does not yet deal damage on impact — real
  combat-integration work, parallel to how `Projectile`'s own collision
  resolution was a later task after its existence-and-ticking slice.
- **Multi-missile salvos.** `FireMissile(70)` always carries exactly one
  `MissileLaunchData` entry; a `MissileLaunchData[]` salvo (&gt;1 per fire) is a
  later extension.
- **A real "fire missile" control input.** No fire-missile bit exists in
  `ShipControlState`; `Ship.FireMissile` stays directly-callable only.
- **`Launcher` as a mountable `EquipmentType`/loadout subtype** — separate,
  legitimate follow-up if a real trigger path is built.
- Cross-side/enemy missile visibility (same-side only, matching every prior
  broadcast in this family).
- `FIRE_EXPENDABLE`, `MISSILE_SPOOFED`/`END_SPOOFING`, `CREATE_CHAFF`,
  `MINE_DESTROYED`, `PROBE_DESTROYED`, `ASTEROID_DESTROYED`, `PROBES_UPDATE`
  — separate Phase 2 items.

## Next follow-up in this arc

**Missile homing/turn-rate flight**: act on the stored `TurnRate`/
`LockTime`/`MaxLock` fields so a missile steers toward (and optionally locks)
a target, then a combat-impact task to deal `BlastPower`/`BlastRadius`
damage on arrival — completing the missile's journey from existence → guided
flight → destruction-with-consequence.

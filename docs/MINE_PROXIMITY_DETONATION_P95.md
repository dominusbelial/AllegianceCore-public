# P95 — mine proximity detonation

Phase 2, slice 8. Branch `master` atop `b235d6e` (P94). Build: `/home/domingo/.dotnet/dotnet`.
The native C++ references live in the sibling checkout `/home/domingo/Development/Allegiance`.

## Integration shape chosen: a dedicated point-in-sphere proximity pass

**Chosen: `Sector.ResolveMineDetonations` (`src/Allegiance.Core/Model/Sector.cs:423`),
a purpose-built point-in-sphere pass — NOT a static participant in the swept
`CollisionDetector`.**

Why. The native engine routes a ship touching a mine through the same swept
`sector → OT_mine: pModel->HandleCollision(...)` (`shipIGC.cpp:1014-1019`) and
its `CmineIGC::HandleCollision` (`mineigc.h:68-147`) is a genuine swept-sphere /
entry-exit solve, so the *native* shape leans shared-detector. But this task
deliberately simplifies the trigger to **no velocity requirement** ("a ship
sitting still next to a mine still sets it off") and **no swept `TCollision`
math** for impact points (just the current positions). A stationary ship already
inside the radius at tick start, or one that never moves, produces **no swept
crossing** for the `NarrowPhase` to find — the whole basis of the
swept `CollisionDetector` is bodies *moving into* contact over `dt`. Forcing a
mine into that detector as a `isStatic: true` `Collider` would make a parked
ship at the mine's centre untriggerable by construction (nothing crosses), and
would mislabel the blast `MineRadius` as the mine's *physical* collider radius.

The repo's established shape for a *stationary trigger volume* is the
point-in-sphere ship-loop pass — `ResolveWarpCollisions`, `ResolveDockCollisions`,
`ResolveMining` (`Sector.cs:365-429,431-610`), each computing
`|ship − target| ≤ target.Radius + shipRadius`. The mine is exactly such a
volume: it never moves (`Mine.OnUpdate`, `Mine.cs:157-172`), so only its static
blast sphere matters. Reusing that shape makes the simplified no-velocity
semantics trivially correct, keeps `MineRadius` as the blast radius (not a
physical model radius), and leaves `ResolveCombatCollisions` (projectiles +
missiles) untouched. This is also exactly where P94's own report pointed
(`MINE_SIM_OBJECT_P94.md:56-61`): mines need the point-in-shape precedents, not
the swept `IOrdnance` path. Given this slice's mandate (binary distance check,
no velocity, no dwell-time), the dedicated pass integrates most cleanly with the
code as it exists today.

## What is real (this slice)

- `Mine.SideId` (`short?`, `src/Allegiance.Core/Sim/Mine.cs:142`), set once at
  construction from the deploying ship (`Mine.cs:97,103`: `DeployTime = now`,
  `SideId = (launcher as Ship)?.SideId`) rather than re-derived from the launcher
  on every trigger. Mirrors `ExportMine.FromMine`'s wire `sideID` (which P93/P94
  already resolve) and `Ship.SideId`'s nullable-short shape. A launcher-less /
  side-less mine has no side → fires on everyone.
- `Mine.DeployTime` (`Mine.cs:129`) — exposed arm-clock origin (`m_time0`,
  `mineigc.h:77`), captured from the constructor's `now`.
- `Mine.ArmTimeSeconds = 1.0` (`Mine.cs:134`) — the port of native
  `c_dtArmTime = 1.0f` (`mineigc.h:75`). Kept (cheap, well-cited, no reason to
  drop it).
- `Sector.ResolveMineDetonations(double)` (`Sector.cs:423-479`), called once per
  sector tick right after `ResolveCombatCollisions` (`Sector.cs:156-158`):
  - **Arm gate** (`Sector.cs:431`): a mine is inert until
    `now ≥ DeployTime + Mine.ArmTimeSeconds`.
  - **Side gate** (`Sector.cs:443`): a mine never damages a ship whose
    `SideId` equals the mine's, engaging only when both carry a side — the exact
    friendly-fire convention `ResolveCombatCollisions` uses
    (`Sector.cs:325-328`; native `mineigc.h:77` `(pmodel->GetSide() != GetSide())`).
  - **Trigger** (`Sector.cs:433,449-457`): the first live, placed ship within
    `MineRadius + 0.5·shipLength` of an armed, hostile-side mine. Ship radius is
    the half-length — the identical value the combat/warp/mining passes use
    (`shipIGC.cpp:1419`, `Sector.cs:174`).
  - **Detonation** (`Sector.cs:460-466`): `ship.ReceiveDamage(MineType.DamageType,
    MineType.Power, now, position1=mine.Position, position2=ship.Position,
    launcher=mine.Launcher)` — impact positions are the *current* mine & ship
    positions, no swept `TCollision` math, full `Power`, once. Reuses the exact
    signature every other `ReceiveDamage` caller uses.
  - **Consumption + destroy** (`Sector.cs:470-474`): `_models.Remove(mine)` and
    `launcherShip.Mission?.Site.MineDestroyedEvent(mine)` — the identical pair
    `Mine.OnUpdate`'s expiry path already makes (`Mine.cs:157-172`), so a
    same-side peer who saw the mine deploy always learns of its destruction,
    expiry *or* detonation, with no cause field to distinguish them (P91's
    consistency rule, applied to mines).
  - **Single-target** (`Sector.cs:475` `break`): one mine hits the first
    qualifying ship in a tick — no multi-ship AoE, matching `Projectile`/`Missile`
    precedent (P88/P91), despite the type record carrying a blast field.

Everything is data-derived from the real `PCore001.igc` static core — no
hardcoded mine/hull/shield/radii.

## Verified (all real, loopback `Bot`/in-process server unless noted)

Full suite `2350` tests pass, 0 failures (baseline ~2346 at P94 + 4 new
`MineCombatTests`). New `tests/Allegiance.Core.Tests/MineCombatTests.cs`
(3-bot / 2-side construction mirroring `MissileCombatTests`):

- **`Cross_side_mine_detonates_on_the_first_ship_in_radius_with_computed_damage_after_the_arm_delay`**:
  1. Target hull fraction drops by the exact computed amount
     `Power·dtmArmor/MaxHP`: `enemy.Fraction` went `1.00 → 0.500` (asserted `±0.001`).
  2. The mine disappeared from the sector after detonation.
  3. The same-side observer received a real `FM_S_MINE_DESTROYED(84)` for the
     mine's id (`ClusterId 1051` = shooter's sector).
  4. **Arm-delay proof**: with the cross-side enemy already *inside* the blast
     radius before `DeployTime + 1.0`, the unarmed window showed `enemy.Fraction`
     unchanged (still `1.0`) and the mine still present — asserted; only after
     the arm delay elapsed did it detonate.
- **`Cross_side_mine_against_a_mounted_shield_absorbs_like_a_weapon_hit`**:
  a real mounted shield absorbed the computed `Power·dtmShield/MaxStrength`
  (= `0.250`): `ShieldFraction 1.0 → 0.750`, hull untouched — the reused
  `ReceiveDamage` shield path, proven with a computed shield-fraction delta.
- **`Same_side_ship_inside_the_blast_radius_never_triggers_a_mine_but_a_cross_side_ship_does`**:
  a real same-side approach drove up beside the armed mine, hull stayed full and
  the mine remained present (asserted, no destroy broadcast); only when the
  cross-side enemy arrived did it detonate (enemy took the computed `0.500`),
  with the same-side observer still receiving the destroy.
- **`Mine_with_no_intruder_expires_via_lifespan_and_broadcasts_destroy_unmodified`**
  (domain, no sockets): a mine with nothing entering its radius still removed
  itself via `TimeExpire = DeployTime + Lifespan` and raised
  `MineDestroyedEvent` exactly once — the P94 expiry path, confirmed unmodified
  and undisturbed by the new pass.

Existing `CombatDamageTests` / `CombatWeaponFiringTests` / `CombatShieldTests` /
`MissileCombatTests` / `LiveCombatWireTests` all pass unmodified within the 2350.

## Explicity out of scope (kept out this slice)

- Native swept entry/exit dwell-time damage scaling (`mineigc.h:116-134`) and the
  `"you gotta be moving"` gate (`mineigc.h:80-82`) — deliberately simplified to
  a binary distance check, no velocity requirement.
- Mine destructibility (being shot down) — a mine remains only an attacker,
  never a valid target in the ship-damage path.
- Multi-ship AoE detonation.
- `Endurance` — still unconfirmed/unused.
- Cross-side/enemy mine visibility (unchanged from P94 — same-side only).
- `FIRE_EXPENDABLE`, `PROBE_DESTROYED`, `ASTEROID_DESTROYED`, `PROBES_UPDATE`,
  `CREATE_CHAFF` — separate Phase 2 items.

**Next open item in this arc: mine destructibility** — a mine that can be shot
down by weapon/missile fire before it detonates. This is *not necessarily the
next task*; that is the user's call. Second in line: native dwell-time damage
integration (replacing the full-`Power`-once-on-trigger with the real
entry/exit time-scaled damage).

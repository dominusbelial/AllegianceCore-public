# P23 — arm every fixed hardpoint, not just mount 0

**Status:** built clean, all 1521 tests green across 3 consecutive `dotnet test`
runs (run 2 and 3: `Passed!  - Failed: 0, Passed: 1521, ... Duration: 15/17 s`),
including the new multi-hardpoint arming test.

## The change

`Server.MountCombatWeapon` previously mounted exactly one weapon at the default
`mountId = 0`:

```csharp
ship.MountWeapon(weaponType);
Console.WriteLine($"[combat] ship {ship.Id} armed with weapon '{weaponRec.p.Name}' "
    + $"(projectile {weaponType.ProjectileTypeId}, {weaponType.EnergyPerShot}/shot, mount 0)");
```

It now loops over `ship.HullType.Hardpoints` and calls
`Ship.MountWeapon(type, mountId)` for every index where
`Hardpoints[mountId].Fixed` is true — the data model and firing logic already
supported multiple mounted weapons (`Weapon` stores its own `_mountId`,
`Weapon.cs:48,70`; the ship tick drives every mounted weapon via
`foreach (var weapon in _weapons) weapon.Update(now)`, `Ship.cs:433-434`,
nothing in that loop references a specific mount index).

| file | lines | what |
|---|---|---|
| `src/Allegiance.Server/Server.cs` | 539-562 | doc comment updated (multi-hardpoint arming + the `PartMask` simplification, in this slice's plain-tone style) |
| `src/Allegiance.Server/Server.cs` | 563-603 | `MountCombatWeapon` body — loop over hardpoints, arm every `Fixed` one, skip turrets, zero-fixed-hull handled gracefully |
| `src/Allegiance.Server/Server.cs` | 586 | `for (short mount = 0; mount < hardpoints.Length; mount++)` |
| `src/Allegiance.Server/Server.cs` | 591 | `ship.MountWeapon(weaponType, mount);` — same proven weapon on every fixed mount |
| `src/Allegiance.Server/Server.cs` | 596 | zero-fixed-hull no-op path (logged, not thrown) |
| `src/Allegiance.Server/Server.cs` | 600-601 | log line reports armed/fixed/total/skipped |

## Deliberate simplification (explicitly documented, not silently skipped)

Neither `Hardpoint.PartMask` nor `DataPartTypeIGC.PartMask` (nor the hull-level
`PmEquipment[ET_MAX]` gate) is checked — every fixed hardpoint gets the same
proven weapon. This mirrors the slice's existing stated deviations (no shields,
no economy, no lobby): full mask-based loadout fitting is the parts/equipment
task that `FM_C_BUY_LOADOUT` was already deferred for, and chasing it here would
re-open exactly the scope-creep this codebase keeps out. The simplification is
called out in the method's `<summary>` doc comment and in `README.md`'s
"What the server does and does not do yet" section.

Why turret/gunner hardpoints are skipped rather than armed: `Weapon.IsWeaponSelected`
returns `false` unconditionally for a non-fixed mount
(`Weapon.cs:274-275` — "turret gun: requires a gunner, none in this slice"), so
a weapon mounted there could never fire — a silent dead end. Skipping and
logging the count is the honest behavior.

## The new test

`tests/Allegiance.Core.Tests/CombatWeaponFiringTests.cs`,
`MountCombatWeapon_arms_every_fixed_hardpoint_and_no_turrets()`.

Against the real decoded `PCore001.igc`, it selects a fightable hull with both
fixed and non-fixed hardpoints (preferring the most fixed slots) and invokes the
actual production `Server.MountCombatWeapon` via reflection (it is spawn-time
wiring with no public seam, and its body reads only `ship.Mission` /
`mission.Core` / `ship.HullType`, so an unbooted `Server` is a valid receiver),
then asserts a weapon landed on every fixed mount and on no turret slot.

## Captured hull data (the real decoded corpus)

The representative core's fightable hulls span 1 to 4 fixed hardpoints. Notable:
`Adv Fighter` 3/3, `Lxy Fighter` 4/4, `Flagship.4 [AsCr]` 4 fixed + 4 non-fixed.
The test's previous representative hull (`Fight.Value` → `Adv Scout`, HullId 13)
has only **one** fixed hardpoint — worth noting, since it could never have
proved multi-hardpoint arming. A flagship (4 fixed + 4 non-fixed) was therefore
chosen for the new test to make both claims (every fixed armed, no turret armed)
meaningful.

## Verbatim pass evidence (new test)

```
[combat] ship 9991 armed 4 of 4 fixed hardpoints with 'PW A-B Omega 2' (projectile 85, 0/shot; hull 4 fixed/8 total, 4 non-fixed skipped)
```

```
dotnet test --filter FullyQualifiedName~MountCombatWeapon_arms_every_fixed_hardpoint
Passed!  - Failed: 0, Passed: 1, Skipped: 0, Total: 1, Duration: 51 ms
```

The production log shows 4 of 4 fixed hardpoints armed on the flagship
(4 fixed / 8 total, 4 non-fixed skipped) — a real multi-mount hull, driven
through the real `Server.MountCombatWeapon`.

## Full-suite evidence (definition of green)

3 consecutive full runs, all green, all **1521** passed / 0 failed / 0 skipped
(duration differs run to run, count is stable — the new test brings the suite
from 1520 to 1521):

```
dotnet test
Passed!  - Failed:     0, Passed:  1521, Skipped:     0, Total:  1521, Duration: 17 s
Passed!  - Failed:     0, Passed:  1521, Skipped:     0, Total:  1521, Duration: 15 s
Passed!  - Failed:     0, Passed:  1521, Skipped:     0, Total:  1521, Duration: 17 s
```

## Files touched

- `src/Allegiance.Server/Server.cs` — `MountCombatWeapon` loops over hardpoints.
- `tests/Allegiance.Core.Tests/CombatWeaponFiringTests.cs` — new multi-hardpoint test.
- `README.md` — Status, "What the server does", and "does not do yet" sections updated.
- this note, `P23_MULTI_HARDPOINT_ARMING.md`.

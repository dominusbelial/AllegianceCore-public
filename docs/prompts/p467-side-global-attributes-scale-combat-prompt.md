# P467 — researched developments scale weapon output (gun and missile damage, ammo speed, energy lifespan)

This is the combat half of the side-global-attribute (GA) wiring, and part 1 of 2:
- **P467 (this task):** weapon output.
- **P467b:** ship durability (max energy, max armor, max shield, shield regen).
- **P446:** the flight half (max speed, thrust, turn rate, turn torque).

**Build on P446.** Check `git log --oneline | grep P446`.
- If P446 has landed, reuse its GA-aware `Ship` accessor, its per-side `Side` resolution, and the named index constants it added to `GlobalAttributeSet`.
- If it hasn't landed, add the same shape it specifies in `docs/prompts/p446-side-global-attributes-scale-flight-prompt.md` step 1, so P446 can reuse yours:
  - one cheap `Ship` → `Side` GA lookup
  - identity (1.0) when `SideId` is null
  - named constants citing `igc.h:585-619`

## Why this task exists

This comes from the 2026-09-29 gameplay-parity pass.

**Developments are applied but never read.** Completing a development composes its GA into `side.GlobalAttributes` (`src/Allegiance.Server/Server.Economy.cs:708`) and broadcasts `SIDE_ATTRIBUTE_CHANGE(76)` (`:778`). No simulation code ever reads it. So "+x% gun damage"-style research changes nothing in combat, which is part of why tech development isn't a game dynamic yet.

**Native multiplies weapon output at these sites.** The indices are at `~/Development/Allegiance/src/Igc/igc.h:585-619`.

| Attribute | Index | Native site | What it scales |
|---|---|---|---|
| `c_gaDamageGuns` | 21 | `Igc/projectileIGC.cpp:132` | blast |
| `c_gaDamageGuns` | 21 | `Igc/projectileIGC.cpp:186` | direct hit, `m_projectileType->GetPower() * GA` |
| `c_gaDamageMissiles` | 22 | `Igc/missileigc.cpp:474` | missile direct damage |
| `c_gaDamageMissiles` | 22 | `Igc/missileigc.cpp:523` | missile blast damage |
| `c_gaSpeedAmmo` | 14 | `Igc/weaponIGC.cpp:220` | projectile speed, ammo weapons only (`if (m_typeData->cAmmoPerShot)`) |
| `c_gaSpeedAmmo` | 14 | `Igc/common.cpp:141` | the lead-indicator solve |
| `c_gaSpeedAmmo` | 14 | `Igc/probeigc.cpp:367` | probe projectiles |
| `c_gaLifespanEnergy` | 15 | `Igc/weaponIGC.h:169` | energy-weapon lifespan in `GetLifespan` (check the exact condition there) |
| `c_gaTurnRateMissile` | 16 | `Igc/missileigc.cpp:389` | missile turn |

**Where the port leaves these out:**
- Projectile and missile damage is passed as raw `ord.Power` at `src/Allegiance.Core/Model/Sector.cs:1640,1692,1764,1843`, commented "× c_gaDamageGuns = 1, no side attribute" at `:1632-1633`.
- Missile blast is raw at `Sim/Missile.cs:~254-270`.
- `Weapon.cs:233` omits SpeedAmmo ("no side").
- `Weapon.GetLifespan()` returns the raw value (`Weapon.cs:127`).
- The server-side lead solve is around `Server.Broadcast.cs:~1816`.

## Scope

1. **Accessor.** Use or add the accessor described above, with constants for indices 14, 15, 16, 21 and 22.
2. **Damage.**
   - Multiply projectile damage by the **launcher's** side `DamageGuns` at every projectile hit site, both direct and blast.
   - Multiply missile damage, direct and blast, by the launcher's side `DamageMissiles`.
   - Follow native on which side's GA applies. Native uses `GetSide()` of the projectile or missile, which is the launcher's side.
   - Negative-power nanite rounds also flow through `DamageGuns` in native. Confirm this and keep native's behaviour.
3. **Speed and lifespan.**
   - Scale ammo-weapon projectile speed by `SpeedAmmo` in `Weapon.FireWeapon` (`Weapon.cs:233`).
   - Scale the server lead solve the same way.
   - Scale energy-weapon lifespan in `GetLifespan` by `LifespanEnergy`, under native's exact condition.
   - Scale missile turn rate by `TurnRateMissile`.
4. **Stale comments.** Update the "no side attribute" comments this task makes stale.

## Explicitly out of scope

- Ship durability GAs: `MaxEnergy`, `MaxArmorShip`, `MaxShieldShip` and `ShieldRegenerationShip` (**P467b**).
- Flight GAs (**P446**).
- Station GAs (`c_gaMaxArmorStation`, `c_gaMaxShieldStation` and the regeneration ones, `stationIGC.cpp:596-622`). Named follow-up.
- Scan range and signature GAs (visibility). Named follow-up.
- Mining GAs, `DevelopmentCost` and `DevelopmentTime`, which belong to the economy pass.
- Probe projectile speed (`probeigc.cpp:367`). Name it as a follow-up unless it's a one-liner through the same path, in which case do it and say so.

## Verification

Each test below **must fail on unfixed code first**; record that.
- A side whose GA has `DamageGuns = 1.25`: a projectile hit deals 1.25× `Power` to a shieldless target.
- The same pattern for `DamageMissiles`.
- An ammo weapon's projectile velocity scales with `SpeedAmmo`, and an energy weapon's does not.
- An energy weapon's lifespan scales with `LifespanEnergy` under native's condition.
- An end-to-end test: complete a real development whose GA raises gun damage (find one in PCore001 and name it), then show the damage change.
- Whole-solution build with `/home/domingo/.dotnet/dotnet build`: 0 warnings, 0 errors.
- `/home/domingo/.dotnet/dotnet test`: 3 consecutive clean runs. Only the pre-existing documented timing flakes are acceptable; name any you see.

## Deliverables

1. Committed on **`master`**, not pushed. Check `git branch --show-current` first.
2. Report `docs/reports/P467_SIDE_GA_SCALE_WEAPON_OUTPUT.md`, covering:
   - whether the P446 accessor was reused or introduced
   - every site wired, as native → port file:line
   - the real development used in the end-to-end test
   - the remaining unwired GA list, updated

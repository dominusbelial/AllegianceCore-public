# P277 — Nanite gun (field-repair weapon)

Feature: a **player-equippable repair weapon**. Firing the nan gun at a ship
(or station) restores its **hull** — the same way it does in FreeAllegiance —
instead of dealing damage. It mounts/selects like any other weapon (P225 mount
cycling), needs no separate UI, and is a **third, player-triggered repair
path** alongside the station (P261, on-launch) and probe (`Probe.cs`) paths —
none of which are touched.

## Native implementation (cited, not assumed)

Allegiance has **no distinct "Repair" weapon type**. The nan gun is an ordinary
`DataWeaponTypeIGC` part whose fired projectile carries **negative power**, and
that sign is exactly how native's own AI recognises a repair mount:

- `shipIGC.cpp:2608` — the wingman repair branch keys on
  `m_mountedWeapons[0]->GetProjectileType()->GetPower() < 0.0` under command
  `c_cidRepair` (`c_cidRepair = 6`, `igc.h:907`).
- The repair itself happens in `CshipIGC::ReceiveDamage`'s
  **`amount < 0` branch** (`shipIGC.cpp:1244-1267`): it restores hull fraction
  (`m_fraction -= amount * dtmArmor / maxHP`, `:1261`, amount negative so this
  **raises** fraction), clips at 1.0, bypasses shields entirely, and does not
  touch the repair-source's stat/achievement bookkeeping here
  (`:1265` `SetAchievementMask(c_achmNewRepair)`, `c_achmNewRepair = 0x04`,
  `igc.h:800` — the port has no repair-stat infra; already noted in
  `Ship.ReceiveDamage`).
- **Friendly-fire gate is sign-sensitive** (`shipIGC.cpp:1222-1228`): a launcher
  on the same (or allied) side with friendly fire OFF is suppressed **only when
  `amount >= 0.0f`** — so a negative-power nan round is *allowed through* to
  repair an ally. A positive-power round to an ally is still suppressed.

### Does the nan damage or heal enemies? (the honest, native answer)

Native's repair branch has **no side check**: `amount < 0` restores
`m_fraction` unconditionally, and the friendly-fire gate only guards
`amount >= 0`. The side test (`shipIGC.cpp:1266-1270`) exists **only to award
the launcher a repair-stat**, which this port omits. So — mechanically — the
nan **heals whichever hull it strikes, friend or foe**, and it **never damages**
anything (its power is negative, so it can only ever take the repair branch).

This is not ambiguity to paper over: it is the faithful behavior, and I chose
it over inventing a side-discrimination. AFS/Nanning's emphasis on healing
*friendlies* is the *intended* use, but a carelessly aimed nan heals an enemy —
exactly as in the original game. This is documented and unit-tested
(`Cross_side_nan_round_also_restores_enemy_hull`).

## Real part data decodes cleanly (no synthetic subtype needed)

The task brief asked me to "check first, don't assume it needs the synthetic
pattern if a real `WeaponType` already decodes cleanly." It does. `DataPartTypeIGC.Weapon`
is a real 424-byte decoded record (`DataPartTypeIGC.cs:79-93`), and the shipped
corpus contains real nan guns — e.g. `XC02.igc`:

- `ER Nanite 1` (part 91) → projectile 58, `Power = -22.5`, `Speed 400`,
  `Lifespan 1`, energy-per-shot 30, `cAmmoPerShot 0` (an energy weapon);
- `ER Nanite 2` (part 190) → projectile 59, `Power = -30`.

So the nan mounts exactly like any decoded weapon (`Ship.MountWeapon`), and no
P182-style construction-only subtype was added.

## What was built

The only production-code change is in the **ship friendly-fire gate**:

- `src/Allegiance.Core/Model/Sector.cs` (`ResolveCombatCollisions`) previously
  suppressed **every** same-side hit unconditionally, so a nan round could never
  reach a friendly's hull. It now suppresses only `ord.Power >= 0f` rounds,
  letting a negative-power (repair) round land on the same-side hull — mirroring
  native's `amount >= 0.0f` gate exactly (`shipIGC.cpp:1222-1228`).

Everything else was already present and now becomes reachable: `Ship.ReceiveDamage`
already implements the `amount < 0` repair branch (`Ship.cs:2907-2915`), and
`Station.ReceiveDamage`'s friendly-fire gate was already guarded on
`amount >= 0.0f` (`Station.cs:246-248`), so **stations are also nan-repairable**
without further change. `Weapon`/`Projectile` firing, dispersion, the sector
collision pass, and the wire `ServerShipUpdate.HullFraction`
(`ServerShipUpdate.cs:122`, from `ship.Fraction`) all predate P277.

### Repair formula

Per landed round, on the struck ship's hull:

```
ΔFraction = -Power · dtmArmor / maxHP     (Power < 0, so ΔFraction > 0)
```

clipped to `Fraction ≤ 1.0` (can't over-repair). `Power` = decoded projectile
power, `dtmArmor` = `DamageConstants[dmgType][hull.DefenseType]`
(`shipIGC.cpp:1233`), `maxHP` = decoded hull hit points. The experience
multiplier (`shipIGC.cpp:1239-1242`) is omitted — the port tracks no
experience.

## Tests

`Allegiance.Core.Tests/NaniteRepairTests.cs` (4):

- `Same_side_nan_round_repairs_friendly_hull` — a real ER Nanite fired at a
  same-side (friendly) ship lands (not swallowed by the gate), terminates on
  impact, and restores the hull by the exact decoded repair amount.
- `Cross_side_nan_round_also_restores_enemy_hull` — faithful: a nan striking an
  enemy heals it too (documented choice above).
- `Same_side_positive_power_round_still_deals_no_damage` — regression guard:
  the P277 gate edit must not weaken friendly-fire protection for real weapons.
- `ReceiveDamage_negative_amount_repairs_hull_and_does_not_touch_shield` —
  the repair branch is now live (was dead code), clips at 1.0, and bypasses the
  shield (nan heals hull, not shields, per AFS/Nanning).

**Must-fail verified:** reverting the `Sector.cs` gate change (removing the
`ord.Power >= 0f` clause) makes `Same_side_nan_round_repairs_friendly_hull`
fail — the friendly hull never moves.

`Allegiance.ClientCore.Tests/NaniteRepairLiveTests.cs` (1, live over real
loopback TCP):

- `NanRound_RepairsFriendlyHull_InRealWireData` — a real in-process server
  booted on the nan-bearing `XC02.igc` core, two real `ConnectionSession`s on
  the same side, the target's hull driven to 0.5, the shooter firing the real
  ER Nanite via `CommandControls(..., ShipControlState.AllWeapons)`. Observed
  on the **target's own `ClientWorld`** from real `SINGLE_SHIP_UPDATE` messages:

  ```
  LIVE-NAN: friendly 8097 hull 0.500 → 0.554 over the wire (203 ticks); shooter 8096 unharmed
  ```

  i.e. the hull fraction genuinely rises in real wire data — not a client-side
  visual — and the shooter's own hull is untouched.

## Build / test status

- Whole-solution build (`AllegianceCore.slnx`, Debug): **0 warnings, 0 errors**.
- Full suite run 1: Lobby 12/12, ClientCore.MonoGame 428/428,
  ClientCore 124/124 (incl. the new live test), Core 3129/3130 — the one
  failure is the **pre-existing** `LiveTurretGunnerBindingTests` timing flake
  (documented, fails persistently on the pristine tree; unrelated — it is a
  turret-binding test referencing none of P277's symbols).
- Full suite run 2: identical — only that same pre-existing flake.
- New tests green in isolation; live proof printed above.

## Out of scope (unchanged)

- Station (P261) and probe repair mechanics — untouched; this is an additional
  player-triggered path.
- Full weapon-selection UI beyond P225 mount cycling — the nan mounts/selects
  like any weapon.
- Native reference source (`~/Development/Allegiance`) — read-only, never modified.

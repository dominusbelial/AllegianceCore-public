# P467b — Side Global Attributes scale ship durability (max energy, max armor, max shield, shield regen)

Part 2 of the combat half of the side-global-attribute (GA) wiring. Reuses the
same shared accessor P467 wired (`Ship.GlobalAttributeFactor` / private
`SideGlobalAttributes()`, which P446 introduced) and runs on top of P461 (the
max-energy ceiling it clamps to is the one this task scales). Committed on
`master`, not pushed.

## Prerequisites confirmed

- P446 (shared per-side GA resolution) and P467 (shared `GlobalAttributeFactor`
  accessor) are both landed on `master`.
- P461 (energy recharge in flight) is landed (`faa4ec18`) — the recharge clamp
  this task scales exists and is wired.

## What was wired

Native wraps these four ship-durability maxima in GA multipliers; the port now
reads them through four new effective getters that mirror the native getters
exactly (identity when the ship is sideless or its mission can't resolve the
side — the honest neutral the P446/P467 accessor established).

| Attribute (index) | Native getter | Port effective getter |
|---|---|---|
| `c_gaMaxEnergy` (13) | `MyHullType::GetMaxEnergy` | `Ship.EffectiveMaxEnergy()` |
| `c_gaMaxArmorShip` (8) | `MyHullType::GetHitPoints` | `Ship.EffectiveMaxHitPoints()` |
| `c_gaMaxShieldShip` (9) | shield `GetMaxStrength` | `Ship.EffectiveMaxShieldStrength(shield)` |
| `c_gaShieldRegenerationShip` (10) | shield `GetRegeneration` | `Ship.EffectiveShieldRegen(shield)` |

Indices are at `igc.h:593-595,600`; the new constants were added to
`GlobalAttributeSet.cs` (MaxArmorShip=8, MaxShieldShip=9,
ShieldRegenerationShip=10, MaxEnergy=13), alongside P446/P467's.

### Site map: native → port

#### Max energy (`c_gaMaxEnergy = 13`)

| Native | Port |
|---|---|
| `shipIGC.cpp:299-302` recharge clamp `eMax = GetMaxEnergy()` | `Ship.cs:6653` `float eMax = EffectiveMaxEnergy()` |
| `shipIGC.h:1339` powerup `m_energy = GetMaxEnergy()` | `Ship.cs:349` `Energy = EffectiveMaxEnergy()` (ApplyPowerup) |
| `stationIGC.cpp:487-488` dock-repair `SetEnergy(GetMaxEnergy())` | `Ship.cs:3900` `Energy = EffectiveMaxEnergy()` (RepairAndRefuel) |
| hull-swap reseed (shipIGC `ReInitialize` path) | `Server.ShipCommands.cs:2057` `ship.Energy = ship.EffectiveMaxEnergy()` |
| carrier relaunch resupply | `Server.Docking.cs:1132` `lander.Energy = lander.EffectiveMaxEnergy()` |
| ship construction full-seed | `Ship.cs:168` `Energy = EffectiveMaxEnergy()` (identity at construction → no-op) |
| wire energy fraction denominator `pfractions->SetEnergy(GetMaxEnergy(), energy)` (`shipIGC.h:1124`) | `Wire/ServerShipUpdate.cs:161` `eMax = ship.EffectiveMaxEnergy()` |

#### Max armor / hull hit points (`c_gaMaxArmorShip = 8`)

| Native | Port |
|---|---|
| `shipIGC.cpp:1232` damage denominator `maxHP = GetHitPoints()` | `Ship.cs:3718` `float maxHP = EffectiveMaxHitPoints()` (ReceiveDamage) |
| `shipIGC.h:335-338` `GetHitPoints() = fraction · GetHitPoints()` | `Ship.cs:440` `HitPoints => _fraction * EffectiveMaxHitPoints()` |
| `shipIGC.cpp:962-965` collision HP pool (ship↔ship / station ramming) reads `GetHitPoints()` | `Sector.cs:2449` `hp = ship.HitPoints` (already effective via the property) |

#### Max shield strength (`c_gaMaxShieldShip = 9`)

| Native | Port |
|---|---|
| `shieldIGC.h:112-117` `GetMaxStrength` | `Ship.cs:762` `EffectiveMaxShieldStrength(shield)` |
| `shipIGC.cpp:1287` shield absorption divisor | `Ship.cs:3783` `ApplyShieldDamage(..., EffectiveMaxShieldStrength(shieldType), ...)` |
| `shieldIGC.cpp:88` regen divisor `GetMaxStrength()` | `Ship.cs:3966` regen `… / EffectiveMaxShieldStrength(st)` |
| `shipIGC.cpp:962-965` collision pool shield term | `Sector.cs:2454` `ship.ShieldFraction * ship.EffectiveMaxShieldStrength(shieldType)` |

#### Shield regeneration (`c_gaShieldRegenerationShip = 10`)

| Native | Port |
|---|---|
| `shieldIGC.h:106-111` `GetRegeneration` | `Ship.cs:773` `EffectiveShieldRegen(shield)` |
| `shieldIGC.cpp:88` regen gain `GetRegeneration()·dt` | `Ship.cs:3966` `_shieldFraction + EffectiveShieldRegen(st) * dt / …` |

### Not a max-energy fill: `Server.Docking.cs:577`

The brief listed `Server.Docking.cs:577` as a refit site. On inspection that line
is `target.Energy = newEnergy` — the **ripcord cost deduction** from a ship
anchor (native `shipIGC.cpp:3011-ish` `RipcordCost`), not a max-energy fill or
clamp. It is deliberately left unchanged. The two genuine server refits are at
`Server.Docking.cs:1132` and `Server.ShipCommands.cs:2057` (above).

### The collision pool: in scope, deliberately

Native's ship↔ship / station-ramming collision HP pool (`shipIGC.cpp:962-965`)
computes `hp = GetHitPoints()` (+ shield `fraction·GetMaxStrength()`), and both
of those native getters carry the GA multipliers. The task's out-of-scope list
names only **out-of-bounds** and **crowded-sector** damage (`shipIGC.cpp:385-425`);
the collision pool is a separate native site that already flowed through the
GA-scaled getters, so it is routed here too (via the now-effective
`Ship.HitPoints` property and the effective shield term in
`Sector.cs:2454`). Its existing tests are sideless, so they are unaffected.

## Fractions stay the stored state (confirmed against native)

Native stores **fractions**: the hull keeps `m_fraction` (`shipIGC.h:311-318`,
`SetFraction`), the shield keeps `m_fraction` (`shieldIGC.h:126-147`), and the
damage/regen math always works in fraction units (`leakage / maxHP`,
`GetRegeneration()·dt/GetMaxStrength()`). The GA multipliers wrap only the
**absolute ceilings** (`GetHitPoints() = hitPoints·GA`, `GetMaxStrength() =
maxStrength·GA`, `GetMaxEnergy() = maxEnergy·GA`). Consequently a GA change
rescales the absolute maximum **without touching the fraction** — e.g. doubling
max armor means the same absolute damage removes half the hull fraction, and a
full-hull ship still reports fraction 1.0. The port keeps the same invariant:
`_fraction` / `_shieldFraction` are untouched by GA; only the absolute
denominators/ceilings scale.

Energy is stored absolute (as native does: `m_energy`), and the port now seeds,
fills and clamps it to the effective ceiling; the **wire carries the fraction**
(`energy / effective max`), so a raised ceiling does not inflate the client
gauge.

## HUD and wire carry fractions — no client change

Checked and confirmed. The HUD reads `HullFraction`, `ShieldFraction`,
`EnergyFraction` (`HudRenderer.cs:276-277,377,380`), all decoded from the
47-byte `ServerShipUpdate` `CompactShipFractions` slot
(`Wire/ServerShipUpdate.cs:72-86`). The server quantizes `ship.Fraction`,
`ship.ShieldFraction`, and `ship.Energy / effective max` — all fractions. So
nothing absolute crosses the wire, and **no client change is needed**.

The one wire adjustment is the energy-fraction denominator: it previously
divided by raw `HullType.MaxEnergy`; it now divides by `EffectiveMaxEnergy()`
(`ServerShipUpdate.cs:161`), matching native `SetEnergy(GetMaxEnergy(), energy)`.
Without this, a side with `MaxEnergy > 1` would send a fraction inflated above
1.0 (the two EnergyRechargeTests that were updated would have exposed it).

## Verification

### Fail-first (recorded)

With the production wiring temporarily neutralized (the four getters forced to
return raw values so the code still compiles), the five new tests ran and
failed at the assertion level on the pre-P467b behavior — then passed after
restoring the GA wiring:

| Test | Pre-fix failure |
|---|---|
| `MaxArmorShip_scales_the_damage_denominator` | `Assert.InRange` ratio=1 (boosted==identity), wanted ~0.667 |
| `MaxShieldShip_raises_shield_absorption_capacity` | `Assert.InRange` ratio=1, wanted ~0.5 |
| `ShieldRegenerationShip_speeds_regeneration` | `Assert.InRange` ratio=1, wanted ~2 |
| `MaxEnergy_raises_the_ceiling_and_recharge_clamps_to_it` | `Assert.Equal` ceiling stayed raw 1800, wanted 3600 |
| `Durability_indices_match_native_and_identity_is_raw` | (identity/sideless plumbing — passes by construction) |

4 failed, 1 passed (the plumbing test) on the neutralized tree; 5/5 pass on the
wired tree.

### Existing tests that legitimately shifted (updated, following P467's precedent)

Because the real PCore001 **civ base** `GasBaseAttributes` carry non-1.0 values
for the newly-wired indices (e.g. civ 1 Dreghklar `base[8]=0.9`, `base[13]=1.15`;
civ 3 Ga'Taraan `base[9]=1.1`), existing tests whose ships sit on a real side
now see the GA legitimately applied — exactly the situation P467 handled for
`DamageMissiles=1.1`. These were updated to fold in the side's effective value
(all data-derived, no hardcoded civ constants):

- `EnergyRechargeTests` (2): recharge clamp and full-pool/wire-fraction now use
  `EffectiveMaxEnergy()` (the test side's civ base 1.15).
- `NaniteRepairTests`: repair denominator now `EffectiveMaxHitPoints()` (target
  civ base[8]=0.9 → repair ×1/0.9).
- `MissileCombatTests.Cross_side_missile_against_a_mounted_shield…` and
  `MineCombatTests.Cross_side_mine_against_a_mounted_shield…`: absorbed shield
  fraction divided by the enemy's `MaxShieldShip` factor (enemy civ base 1.1).
- `LiveRelaunchTests`: relaunch resupply energy now compared to
  `EffectiveMaxEnergy()`.
- `HullSwapLiveTests`: post-swap `HitPoints` and energy now compared to the
  effective (GA-scaled) values.

### Pre-existing live-wire flakes (reproduced on the pristine tree — NOT caused by P467b)

Ten failures occur identically on the pristine `master` tree (verified by
stashing the P467b changes and re-running the suspects), so they are
pre-existing live-wire flakes, not P467b-attributable:

`LiveWinConditionTests.Multi_mission_disconnect_scopes_per_mission`,
`ShipDeleteGoneLiveTests.DockOut_WatcherLatchesGone_UndockReturnsAndClears`,
`JoinFlowLiveTests.EntityTable_TracksARealTickingServer`,
`LiveTurretGunnerBindingTests.Board_binds_gunner_and_active_turret_update_roundtrips_live`,
`LiveMoneyTests.Board_refund_broadcasts_money_change_sidewide_not_cross_side`,
`GameOverPlayersWireTests.Conquest_win_in_one_mission_does_not_leak_game_over_players_to_another`,
`GameOverPlayersWireTests.Conquest_win_lists_every_connected_player_including_losing_side`,
`ConquestTerritoryLiveTests.Flag_capture_tips_conquest_and_broadcasts_game_state_then_game_over`,
`ConquestTerritoryLiveTests.Flags_disabled_capture_broadcasts_nothing_and_does_not_end`,
`ConquestTerritoryLiveTests.Conquest_win_in_one_mission_does_not_leak_to_another`.

(`LiveTreasurePickupTests.Rescue_ship_picking_cash_treasure…` appeared once in
the P467b run but passes on both the pristine tree and in isolation — a
timing flake, not P467b-attributable.)

### Build / test

- `/home/domingo/.dotnet/dotnet build` → 0 warnings, 0 errors.
- `/home/domingo/.dotnet/dotnet test` → 3 consecutive full-suite runs; no
  P467b-attributable failure in any (only the pre-existing live-wire flakes
  listed above, reproduced on the pristine tree).

## Out of scope (unchanged, per brief)

Weapon output GAs (P467), flight GAs (P446), station GAs (`stationIGC.cpp:596-622`),
scan range / signature, `c_gaRegenerationShield` in the cloak, and
**out-of-bounds** / **crowded-sector** damage (`shipIGC.cpp:385-425`) are all
left untouched.

## Files changed

- `src/Allegiance.Core/Model/GlobalAttributeSet.cs` — 4 new indices.
- `src/Allegiance.Core/Sim/Ship.cs` — 4 effective getters + routing + docs.
- `src/Allegiance.Core/Model/Sector.cs` — collision pool shield term (HP term
  effective via the property).
- `src/Allegiance.Core/Wire/ServerShipUpdate.cs` — energy-fraction denominator.
- `src/Allegiance.Server/Server.Docking.cs`, `Server.ShipCommands.cs` — refit energy.
- `tests/Allegiance.Core.Tests/P467bSideGaScaleShipDurabilityTests.cs` — 5 new
  tests.
- `tests/Allegiance.Core.Tests/{EnergyRecharge,NaniteRepair,MissileCombat,MineCombat,LiveRelaunch,HullSwapLive}Tests.cs` — updated expectations.

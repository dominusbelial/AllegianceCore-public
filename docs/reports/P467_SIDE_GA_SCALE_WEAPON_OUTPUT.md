# P467 — researched developments scale weapon output (gun/missile damage, ammo speed, energy lifespan)

The combat half of the side-global-attribute (GA) wiring. Developments were
already composed into `side.GlobalAttributes` and broadcast
(`SIDE_ATTRIBUTE_CHANGE(76)`), but no simulation code ever read them — so
"+x% gun damage"-style research changed nothing in combat. This task wires the
five weapon-output GAs into the damage / ballistics path. The durability half
is **P467b** (ship durability) and the flight half was **P446** (already landed).

Branch: `master` (verified with `git branch --show-current`). **Committed,
not pushed.**

---

## 1. P446 accessor — reused

`git log --oneline | grep P446` → **P446 has landed** (`d857c0b4`). So this task
**reused** P446's machinery rather than introducing a competing one:

- **Per-side `Side` resolution** — P446's private `Ship.SideGlobalAttributes()`
  (a cheap manual `foreach` over `Mission.Sides`, identity when `SideId` is null
  or the side is unresolvable) is the single resolution path.
- **The GA-aware accessor** — P467 adds one thin public read over that private
  method so the weapon-output consumers can use it:

  ```csharp
  // Ship.cs:691 — the port of GetSide()->GetGlobalAttributeSet().GetAttribute(i)
  public float GlobalAttributeFactor(int index) => SideGlobalAttributes().Get(index);
  ```

  Called on the **launcher's** ship (the round's own `GetSide()` == the
  launcher's side, exactly native).
- **Named constants** — P446 had added `MaxSpeed/Thrust/TurnRate/TurnTorque`
  (0–3). P467 adds the five weapon-output indices, all citing `igc.h:585-619`
  (verified against the native source): `GlobalAttributeSet.cs:60-69`
  (`SpeedAmmo=14`, `LifespanEnergy=15`, `TurnRateMissile=16`, `DamageGuns=21`,
  `DamageMissiles=22`).

Identity (1.0) when `SideId` is null → the no-side path is byte-for-byte the
pre-P467 behaviour.

---

## 2. Every site wired (native → port)

| GA | Index | Native site | Port site(s) |
|---|---|---|---|
| `c_gaDamageGuns` | 21 | `Igc/projectileIGC.cpp:132` (blast), `:186` (direct hit, `Power * GA`) | `Model/Sector.cs:1623` (factor), `:1669/:1721/:1793/:1872` (`ord.Power * gaMul` direct: ship/probe/station/asteroid), `:1688/:1743/:1811/:1892` (`ord.BlastPower * gaMul` blast-on-impact) |
| `c_gaDamageMissiles` | 22 | `Igc/missileigc.cpp:474` (direct), `:523` (blast) | `Model/Sector.cs:1623-1625` (a `Missile` picks `DamageMissiles`, `Projectile` picks `DamageGuns`); `Sim/Missile.cs:265` (expiry blast `BlastPower * GaFactor`) |
| `c_gaSpeedAmmo` | 14 | `Igc/weaponIGC.cpp:220` (ammo-weapon projectile speed, `if (cAmmoPerShot)`); `Igc/common.cpp:141` (lead solve, `if (GetAmmoPerShot())`) | `Sim/Weapon.cs:248-250` (`FireWeapon`, gated on `_type.CAmmoPerShot != 0`); `Sim/Ship.cs:4928` (`SolveForLead`, gated on `weapon.Type.CAmmoPerShot != 0`) |
| `c_gaLifespanEnergy` | 15 | `Igc/weaponIGC.h:169` (`if (m_ship && energyPerShot != 0)`) | `Sim/Weapon.cs:132-137` (`GetLifespan`, gated on `_type.EnergyPerShot != 0`) |
| `c_gaTurnRateMissile` | 16 | `Igc/missileigc.cpp:389` (`TurnRate * dt * GA`) | `Sim/Missile.cs:241` (homing turn via `GaFactor`) |

Notes on fidelity:

- **Whose side applies.** Native reads the ordnance's own `GetSide()` — which is
  the launcher's side. The port resolves `(ord.Launcher as Ship)?.GlobalAttributeFactor(..)`
  (Sector.cs:1623), and `Missile.GaFactor` reads its `Launcher` (Missile.cs:289).
  A sideless / launcher-less ordnance → identity 1.0.
- **Negative-power nanite rounds.** Confirmed against native: `projectileIGC.cpp:186`
  multiplies `GetPower()` by `c_gaDamageGuns` **regardless of sign**. The port's
  `ord.Power * gaMul` applies to negative-power (repair/nan) rounds too, so a
  repaired hull heals × the same factor — native's behaviour preserved.
- **Ammo vs energy split.** `c_gaSpeedAmmo` scales only an *ammo* weapon
  (`cAmmoPerShot ≠ 0`); `c_gaLifespanEnergy` scales only an *energy* weapon
  (`energyPerShot ≠ 0`), in both `FireWeapon` and the lead solve.
- **Both direct and blast.** Damage is scaled at every direct-hit site (ship /
  probe / station / asteroid) *and* every blast (blast-on-impact in the combat
  pass, and the missile's expiry blast in `Missile.OnUpdate`).

**Stale comments updated** (the "× c_gaDamageGuns = 1, no side attribute" /
"omitted (no side)" notes): `Sector.cs:1659-1663`, `Weapon.cs` class doc +
`GetLifespan`, `Ship.cs:4925-4928`, `Missile.cs:200-201`.

---

## 3. Out-of-scope / named follow-ups (unchanged in this task)

Wired: gun damage, missile damage, ammo speed, energy lifespan, missile turn.

Still unwired (the updated remaining-GA ledger):

- **Ship durability** — `MaxArmorShip` (8), `MaxShieldShip` (9),
  `ShieldRegenerationShip` (10), `MaxEnergy` (13) → **P467b**.
- **Flight** — `MaxSpeed/Thrust/TurnRate/TurnTorque` (0–3) → **P446** (landed).
- **Stations** — `MaxArmorStation` (4), `ArmorRegenerationStation` (5),
  `MaxShieldStation` (6), `ShieldRegenerationStation` (7) → named follow-up.
- **Visibility** — `ScanRange` (11), `Signature` (12) → named follow-up.
- **Mining** — `MiningRate` (17), `MiningYield` (18), `MiningCapacity` (19)
  → economy pass.
- **Ripcord** — `RipcordTime` (20) → named follow-up.
- **Economy** — `DevelopmentCost` (23), `DevelopmentTime` (24) → economy pass.
- **Probe projectile speed** — `probeigc.cpp:367` is *not* a one-liner through a
  shared weapon path (a probe fires its own projectile with its own speed), so
  it stays a named follow-up.

---

## 4. End-to-end: a real development raising gun damage

Completed **dev 143 'GA: PW&EW Damage 1'** (`gas[21] = 1.1`, price 2000,
TimeToBuild 120 s) — a real PCore001 development — through the real live
funding/completion pipeline (`BucketDonate → bucket completes →
`CompleteDevelopment` applies `GlobalAttributeSet.Apply` → `side.GlobalAttributes[21]`
becomes 1.1× the civ base). A real projectile hit's hull damage then scaled by
exactly that factor:

```
P467-E2E: dev 143 'GA: PW&EW Damage 1' (gas[21] 1.100) complete on side 0;
          single-round hull drop 0.2000 -> 0.2200, ratio 1.100 (expect ~1.1)
```

(The run also identifies the missile-damage sibling: dev 149 'GA: Missile
Damage 1', `gas[22] = 1.1`.)

---

## 5. Verification

**Each P467 test fails on the unfixed code first — recorded.** Stashing the three
production wire files (Sector/Weapon/Missile) while keeping the constants +
accessor produced 5 behavioural failures on the unfixed code:

```
GunDamage_GA_scales_a_shieldless_direct_hit                          FAIL
MissileDamage_GA_scales_a_shieldless_direct_hit                      FAIL
SpeedAmmo_GA_scales_ammo_weapon_speed_but_not_energy_weapon          FAIL
LifespanEnergy_GA_scales_energy_weapon_lifespan_but_not_ammo_weapon  FAIL
TurnRateMissile_GA_scales_homing_turn_rate                           FAIL
GlobalAttributeFactor_identity_and_indices_match_native              PASS (plumbing, GA-agnostic)
```

Restored, all pass, with the exact ratios:

```
P467-GUN:   identity drop 0.1549; GA1.25 drop 0.1936; ratio 1.250 (expect 1.25)
P467-MSIL:  identity drop 0.4000; GA1.25 drop 0.5000; ratio 1.250 (expect 1.25)
P467-SPEED: ammo round 1200.0 (2× raw), energy round 1250.0 (raw)  ✓
P467-LIFE:  energy lifespan 0.750 (=0.5×1.5), ammo lifespan 1.200 (raw)  ✓
P467-TURN:  first-tick turn GA2=0.0614 rad, identity=0.0307 rad, ratio 2.000
P467-E2E:   dev 143 complete → single-round drop 0.2000→0.2200, ratio 1.100
```

New test file: `tests/Allegiance.Core.Tests/P467SideGaWeaponOutputTests.cs`
(6 domain unit tests + 1 live E2E in `[Collection("LiveNetwork")]`).

**Regression found & fixed (collateral of wiring).** Wiring `c_gaDamageMissiles`
changed four existing live tests whose expected damage assumed the side's GA was
1.0: the missile combat tests' shooter side is a civ whose base `DamageMissiles`
is 1.1, so the actual (now native-faithful) damage is 1.1× the old expectation.
Verified these four **pass on pre-P467 code and failed on the first wired build**
(i.e. the new behaviour, not a flake). Made them GA-aware by folding the
launcher's side factor into the expected damage:
- `MissileCombatTests.Cross_side_missile_impact…` and
  `…against_a_mounted_shield…`
- `AsteroidCombatLiveTests.Same_side_friendly_missile_damages_the_asteroid…`
- `ProbeCombatLiveTests.Same_side_friendly_missile_damages_the_probe…`

**Whole-solution build** `/home/domingo/.dotnet/dotnet build`: **0 warnings,
0 errors**.

**`dotnet test` — 3 consecutive runs.** No failure in any run is attributable to
P467; the only failures are the pre-existing live-wire timing flakes (all named
in P439's report / the P471 flake-ledger prompt, and reproduced on the pristine
pre-P467 tree for the conquest/gameover/wincondition/turret classes). Seen across
the three runs:

- `JoinFlowLiveTests.EntityTable_TracksARealTickingServer` (all 3 runs)
- `ShipDeleteGoneLiveTests.DockOut_WatcherLatchesGone_UndockReturnsAndClears` (3)
- `ConquestTerritoryLiveTests.*` (3) / `GameOverPlayersWireTests.*` (3)
- `LiveTurretGunnerBindingTests.Board_binds_gunner_and_active_turret_update_roundtrips_live` (3)
- intermittent: `RetriedConnectLiveTests.*`, `AutoReloadOnEmptyTests.*`,
  `CargoBayReloadTests.*`, `LiveWinConditionTests.*`, `LiveMoneyTests.*`,
  `LiveStationRescueTests.*`, `AsteroidMinedLiveTests.*`,
  `LiveCountdownStartPingTests.*`, `LoadoutChangeLiveTests.*`,
  `LiveTreasurePickupTests.*`

The P467 tests and the four repaired missile tests passed in **all three** runs
(absent from every failure list).

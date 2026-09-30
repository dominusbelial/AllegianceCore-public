# P446 — apply the side's GlobalAttributeSet to ship speed, thrust, turn rate and torque

Date: 2026-09-30
Branch: master (committed, not pushed)
Repo: `/home/domingo/Development/AllegianceCore`

## Summary

Gameplay parity gap: completed developments and civilization bonuses had no
effect on how ships flew, even though the port already computed and broadcast
the exact multiplier set native uses. Native reads every hull stat through its
`MyHullType` wrapper, which multiplies by the owning side's
`GlobalAttributes[...]`; the port's `Ship.ExecuteMove` and the NPC/steering
reads used raw hull values.

P446 adds a GA-aware accessor set on `Ship` (mirroring native `MyHullType`) and
routes every flight-physics read through it; adds named index constants to
`GlobalAttributeSet`; wires `SIDE_ATTRIBUTE_CHANGE(76)` on the client and stores
it per side in `ClientWorld`; and makes the client autopilot braking estimate
GA-scaled.

## Change (server)

### `GlobalAttributeSet` — named index constants
Added named constants for the four wired consumers (plus these are the only
ones touched), citing `src/Igc/igc.h:585-610`:
- `MaxSpeed = 0` (`c_gaMaxSpeed`)
- `Thrust = 1` (`c_gaThrust`)
- `TurnRate = 2` (`c_gaTurnRate`)
- `TurnTorque = 3` (`c_gaTurnTorque`)

### `Ship` — GA-aware accessors + routed reads
New accessors (the port of native `MyHullType`, `shipIGC.cpp:4528-4546`):
- `EffectiveMaxSpeed()` = `_hull.Speed * GA[MaxSpeed]`
- `EffectiveThrust()` = `_hull.Thrust * GA[Thrust]`
- `EffectiveMaxTurnRate(axis)` = `_hull.MaxTurnRates[axis] * GA[TurnRate]`
- `EffectiveTurnTorque(axis)` = `_hull.TurnTorques[axis] * GA[TurnTorque]`

`SideGlobalAttributes()` resolves the owning side's set via `Ship.Mission →
Mission.Sides` (scoped to the ship's mission because side ids repeat across
world missions). A sideless ship (`SideId == null`) or an unresolvable side
reads `GlobalAttributeSet.Identity` (all 1.0) — reproducing pre-P446 behaviour
exactly. The lookup is a manual `foreach` (no LINQ/delegate), so it allocates
nothing per tick.

`SideMultiplier` / `BackMultiplier` are **not** scaled (native
`MyHullType::GetSideMultiplier`/`GetBackMultiplier` return raw,
`shipIGC.cpp:4548-4555`).

## Per-site native-wrapper-or-raw table

All five native flight sites read through the GA wrapper (`m_myHullType` or
`GetHullType()` — and `GetHullType()` returns `&m_myHullType`, `shipIGC.h:560-562`,
so it is the GA wrapper too, not the raw hull). Every port site was therefore
correctly routed through the new accessors:

| # | Port site (Ship.cs) | Raw read replaced | Native reference | Native uses GA wrapper? |
|---|---|---|---|---|
| 1 | `ExecuteMove` thrust | `_hull.Thrust` | `shipIGC.cpp:2863` `m_myHullType.GetThrust()` | ✅ GA-scaled |
| 2 | `ExecuteMove` turn rate | `_hull.MaxTurnRates[i]` | `shipIGC.cpp:2900` `m_myHullType.GetMaxTurnRate(i)` | ✅ GA-scaled |
| 3 | `ExecuteMove` turn torque | `_hull.TurnTorques[i]` | `shipIGC.cpp:2901` `m_myHullType.GetTurnTorque(i)` | ✅ GA-scaled |
| 4 | `ExecuteMove` max speed | `_hull.Speed` | `shipIGC.cpp:2922` `m_myHullType.GetMaxSpeed()` | ✅ GA-scaled |
| 5 | `GetTorqueMultiplier` | `_hull.Speed` | `shipIGC.h:546` `m_myHullType.GetMaxSpeed()` | ✅ GA-scaled |
| 6 | `TryComputeDodgeGoal` accel | `HullType.Thrust / Mass` | `common.cpp:1707` `pship->GetHullType()->GetThrust()` | ✅ GA-scaled |
| 7 | `ComputeWingmanRangeThrottle` | `HullType.Speed` | `shipIGC.cpp:2506` `GetHullType()->GetMaxSpeed()` | ✅ GA-scaled |
| 8 | `IsSafeToRipcord` | `HullType.Speed` + `HullType.Thrust` | `shipIGC.cpp:4210-4211` `m_myHullType.GetMaxSpeed()`/`GetThrust()` | ✅ GA-scaled |

Every one of the eight reads went through the wrapper in native — there was no
raw-hull site among them. This is the correctness guarantee the parity pass
demanded, and it is why each port read was switched (none left raw).

## Real developments and civs in PCore001 that change these four

Scanned `PCore001.igc` (305 developments, 10 civs). Developments (all
non-tech-only, with the `gas[i]` delta):

| Dev id | Name | gas[0] maxSpeed | gas[1] thrust | gas[2] turnRate | gas[3] turnTorque |
|---|---|---|---|---|---|
| 135 | GA: Ship Acceleration 1 | 1.0 | **1.1** | 1.0 | 1.0 |
| 136 | GA: Ship Acceleration 2 | 1.0 | **1.1** | 1.0 | 1.0 |
| 137 | GA: Ship Agility 1 | 1.0 | 1.0 | **1.1** | **1.21** |
| 138 | GA: Ship Agility 2 | 1.0 | 1.0 | **1.1** | **1.21** |
| 165 | GA: Ship Speed 1 | **1.1** | 1.0 | 1.0 | 1.0 |
| 166 | GA: Ship Speed 2 | **1.1** | 1.0 | 1.0 | 1.0 |

Civs with a non-identity base in these four (`GasBaseAttributes`):

| Civ | GA[0] maxSpeed | GA[1] thrust | GA[2] turnRate | GA[3] turnTorque |
|---|---|---|---|---|
| Belters | 1.15 | 1.0 | 1.05 | 1.1 |
| Bios | 1.0 | 1.0 | 1.2 | 1.3 |
| Dreghklar | 1.2 | 1.0 | 1.25 | 1.35 |
| Gigacorp | 1.1 | 0.9 | 1.0 | 0.8 |
| Omicron | 1.0 | 1.0 | 1.2 | 1.3 |
| Phoenix | 1.0 | 1.0 | 1.0 | 1.1 |
| Technoflux | 0.9 | 1.0 | 1.1 | 1.2 |
| Ga'Taraan / Iron Coalition / Rixian | 1.0 | 1.0 | 1.0 | 1.0 |

So there ARE real developments touching all four (Speed/Acceleration/Agility),
and the E2E test uses dev 165 'GA: Ship Speed 1'.

## Verification

### Pre-fix failure (recorded)
Before this task, `ExecuteMove`'s cruise used raw `_hull.Speed`, so a ship whose
side GA said `c_gaMaxSpeed = 1.5` still topped out at `hull.Speed`. Demonstrated
by temporarily reverting the ExecuteMove max-speed line to the old raw read and
running the P446 headline test:

```
[xUnit.net ...] Allegiance.Core.Tests.P446SideGaFlightScalingTests.MaxSpeed_GA_scales_full_throttle_cruise_top_speed [FAIL]
   Assert.InRange() Failure: Value not in range
Actual: 120
 cruise top speed=120
```
(`hull.Speed = 120`, expected `1.5 × 120 = 180`; the GA had no effect.)

### New tests
`tests/Allegiance.Core.Tests/P446SideGaFlightScalingTests.cs` (7 unit tests):
- accessors scale raw hull by the side's GA factors;
- sideless / unknown-side / identity-GA read raw (identity);
- **max speed**: full-throttle cruise converges to `≈ 1.5 × hull.Speed`
  (measured `180 = 1.5 × 120`);
- **identity**: cruise plateau equals raw `hull.Speed` (`120`), and a GA ship
  with an identity side flies bit-for-bit identically to a sideless ship;
- **thrust**: early acceleration scales by `c_gaThrust` (measured ratio
  `1.485` for a 1.5 factor);
- **turn rate**: steady-state yaw converges to `1.5 × hull.MaxTurnRates[yaw]`
  (measured `0.6283185 = 1.5 × 0.418879`);
- **turn torque**: ramp rate scales by `c_gaTurnTorque` (measured ratio
  `1.4999988` for a 1.5 factor).

`tests/Allegiance.Core.Tests/P446E2eTopSpeedLiveTests.cs` (1 live test): boots a
real server, seats a bot, completes **real dev 165 'GA: Ship Speed 1'**
(`gas[0] = 1.1`) through the real economy path, and shows a ship's real
`ExecuteMove` top speed rises by exactly the dev's multiplier:
```
P446-E2E: pre-dev top speed=144 (hull.Speed=120, side GA[0]=1.200)
P446-E2E: post-dev side GA[0]=1.320
P446-E2E: post-dev top speed=158.40001 ratio=1.1000 (want ~1.1)
```

`tests/Allegiance.ClientCore.Tests/ClientWorldSideAttributeChangeTests.cs` (3
unit tests): a decoded `SIDE_ATTRIBUTE_CHANGE(76)` round-trips through the wire
and lands in `ClientWorld` keyed by `SideId`; a later broadcast replaces the
set; an unknown side reads identity.

### Build & suite
- Whole-solution build (`/home/domingo/.dotnet/dotnet build`): **0 warnings, 0
  errors**.
- Full suite (`/home/domingo/.dotnet/dotnet test`): every deterministic
  (non-live) test passes, including all 11 new P446 tests. The only failures in
  four full-suite runs were the documented live-wire timing flakes — a
  rotating subset each run, every one from the known/pre-existing set. Seen
  across the runs (each named):
  `ConquestTerritoryLiveTests.Flag_capture…` / `.Flags_disabled…` /
  `.Conquest_win_in_one_mission…`,
  `GameOverPlayersWireTests.Conquest_win_in_one_mission…` /
  `.Conquest_win_lists_every_connected_player…`,
  `LiveTurretGunnerBindingTests`, `LiveWinConditionTests.Multi_mission_disconnect…`
  / `.Board_refused_once_mission_over`, `LiveMoneyTests.Board_refund…`,
  `LiveChatSectorWireTests`, `LiveRipcordTests.Cancel_while_active…`,
  `InboundFloodSafetyLiveTests`, `FullSessionKillIntegrationTests`,
  `ShipDeleteGoneLiveTests`, `JoinFlowLiveTests.EntityTable…`,
  `SeedThreePeers_TwoSameSide_OneCrossSide_DeterministicVisibility`,
  and the intermittent pure-unit flakes `CargoBayReloadTests.SwapPart…` /
  `AutoReloadOnEmptyTests.Auto_reload…` (both pass 3/3 in isolation).
- **Pristine A/B:** the persistent conquest/game-over cluster
  (`ConquestTerritoryLiveTests` + `GameOverPlayersWireTests`, 5 tests) was run
  with all P446 source changes `git stash`-ed and the new test files moved
  aside — it fails identically on the pristine tree, proving these are
  environmental full-suite flakes, not P446 regressions.

## Client change

- `Allegiance.ClientCore/Session/ConnectionSession.cs`: dispatch
  `SIDE_ATTRIBUTE_CHANGE(76)` → `ClientWorld.Apply(SideAttributeChange.Decode(m))`.
- `Allegiance.ClientCore/State/ClientWorld.cs`: `_globalAttributesBySideId`
  dictionary + `GlobalAttributesFor(sideId)` accessor (identity when unknown) +
  `Apply(SideAttributeChange)`. The client has no per-side join preface for the
  GA set (unlike CREATE_BUCKETS for the tech mask), so it learns each side's set
  only from these broadcasts — P447/P448 will consume it.
- `Allegiance.ClientCore.MonoGame/Game1.FlightInput.cs`: the autopilot braking
  estimate now multiplies the resolved hull thrust by the LOCAL side's
  `c_gaThrust` factor (identity when the local side is unknown), so braking
  tracks a GA-boosted engine exactly as the server sim does.

## Still-unwired GA consumers (follow-ups, NOT changed by P446)

Per the scope, the following `c_ga…` consumers remain unwired (raw). Native
location of each use:

| Index | Constant | Native use (file:line) |
|---|---|---|
| 4 | `c_gaMaxArmorStation` | stationIGC.cpp:596 |
| 5 | `c_gaArmorRegenerationStation` | stationIGC.cpp:617 |
| 6 | `c_gaMaxShieldStation` | stationIGC.cpp:607 |
| 7 | `c_gaShieldRegenerationStation` | stationIGC.cpp:622 |
| 8 | `c_gaMaxArmorShip` | shipIGC.cpp:4572 (hitPoints) |
| 9 | `c_gaMaxShieldShip` | shieldIGC.h:115 |
| 10 | `c_gaShieldRegenerationShip` | shieldIGC.h:109 (cloakigc.h:129 uses the `c_gaRegenerationShield` alias) |
| 11 | `c_gaScanRange` | shipIGC.cpp:4558, probeigc.h:173, stationIGC.cpp:591, clusterIGC.h:494 |
| 12 | `c_gaSignature` | clusterIGC.cpp:152, probeigc.h:177, stationIGC.h:322, shipIGC.h:357 |
| 13 | `c_gaMaxEnergy` | shipIGC.cpp:4563 |
| 14 | `c_gaSpeedAmmo` | weaponIGC.cpp:220, projectileIGC.cpp:132/186 (damage), probeigc.cpp:367, common.cpp:141 |
| 15 | `c_gaLifespanEnergy` | weaponIGC.h:169, probeigc.h:226 |
| 16 | `c_gaTurnRateMissile` | missileigc.cpp:389 |
| 17 | `c_gaMiningRate` | shipIGC.cpp:2212 |
| 18 | `c_gaMiningYield` | shipIGC.cpp:1988, shipIGC.h:449 |
| 19 | `c_gaMiningCapacity` | shipIGC.cpp:2166, shipIGC.cpp:2213, shipIGC.h:331, shipIGC.h:2249 |
| 20 | `c_gaRipcordTime` | shipIGC.cpp:4665 |
| 21 | `c_gaDamageGuns` | projectileIGC.cpp:132, 186 |
| 22 | `c_gaDamageMissiles` | missileigc.cpp:474, 523 |
| 23 | `c_gaDevelopmentCost` | bucketigc.cpp:72 |
| 24 | `c_gaDevelopmentTime` | bucketigc.cpp:73 |

Several of these are already flagged in port comments:
`Ship.cs:3824` (shield regen — `c_gaShieldRegenerationShip`/`c_gaMaxShieldShip`),
`Weapon.cs:26,234` and `Ship.cs:4854` (`c_gaSpeedAmmo`). All remain as-is.

## Out of scope (unchanged)

GA consumers other than the four flight attributes; how developments complete /
GA accumulates (already correct, P189); client-side prediction and dead
reckoning (P447/P448) — P446 only makes the data available; P438–P440, P445,
P447–P449, P441/P450.

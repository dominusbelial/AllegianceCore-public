# P452 — Minefields are persistent attrition fields (not one-shot bombs)

Landing: **master** (not pushed — Domingo pushes). Branch at commit time:
`master`.

## Summary

Replaced the one-shot proximity bomb in
`Sector.ResolveMineDetonations` with the native minefield's persistent area
denial, ported from `CmineIGC::HandleCollision`
(`src/Igc/mineigc.h:68-145`): an armed, hostile-side mine now chews on *every*
moving enemy ship inside its radius for as long as the ship is inside it,
wearing itself down as it deals damage, and persisting across hits and ships
until its endurance is exhausted (or it expires).

Three files changed:

- `src/Allegiance.Core/Model/Sector.cs` — `ResolveMineDetonations` rewritten
  to per-tick attrition with worst-hit-per-tick application (no area
  `Explosion`).
- `src/Allegiance.Core/Sim/Mine.cs` — new `TakeAttritionDamage(float)` sharing
  the native endurance-decay core with `ReduceStrength` via `ApplyDecay`;
  extracted `DestroyMine()` (removal + `MineDestroyedEvent`) now shared by the
  expiry, blast, and attrition paths.
- `tests/Allegiance.Core.Tests/MineCombatTests.cs` — the old single-hit /
  explosion tests converted to native semantics (requirements a–f), plus the
  full pre-fix failure evidence.

## `rMe` field decision (with struct citation)

Native sets the mine's collision radius from `dataMineType->radius`
(`mineigc.cpp:106`). `dataMineType` is a `DataMineTypeIGC*`, and in C++ the
derived-most member shadows the base of the same name, so `dataMineType->radius`
resolves to **`DataMineTypeIGC::radius`** — the mine-<em>specific</em> field at
`src/Igc/igc.h:1992`, <b>not</b> the base `DataObjectIGC::radius` at
`igc.h:1907`.

In the port's `DataMineTypeIGC` that mine-specific field is **`MineRadius`**,
decoded at **byte offset 464** (`src/Allegiance.Core/Data/DataMineTypeIGC.cs:49,102`)
with `power`@468, `endurance`@472, `damageType`@476 — exactly the native
`DataMineTypeIGC` tail order (`radius; power; endurance; damageType`,
`igc.h:1990-1996`). The base `Radius` field (offset 16) is the visual/model
radius and is **not** what feeds `SetRadius`. Consequently:

```
rMe = Mine.MineType.MineRadius   // native DataMineTypeIGC::radius @ igc.h:1992; port offset 464
```

This also means the existing point-in-sphere overlap radius
(`rMe + 0.5·shipLength`) was already the right radius — only the behaviour on
top of it was wrong.

## Discretisation chosen vs native swept collision

Native enters via the swept `CollisionDetector`, which yields the collision
entry time `tCollision`, then integrates the whole in-field dwell of that one
event to the exit time from the sphere-intersection quadratic
(`mineigc.h:84-113`), clamped to the tick's `tStop`.

This port keeps its existing point-in-sphere proximity pass (a mine is a
static, velocity-less body — there is no swept entry feed) and **discretises
the native integral per tick**: every tick a qualifying ship is found inside
the sphere, it solves the **same native quadratic** against the ship's motion
over that tick to find the in-field time, clamped to `[0, dt]`. Since the ship
is inside at tick start, the entry time is 0 and the in-field time for the tick
is `tExit` (exit root) clamped to the tick length:

```
dP  = pMe − pHim          (sense reversed wrt velocity, exactly native's comment at mineigc.h:90)
a   = |v|²                (gate requires a > 1)
halfB = dP · v
c    = |dP|² − r²          (r = rMe + rHim)
b2ac = halfB² − a·c
tExit = (halfB + √b2ac) / a
inFieldDt = clamp(tExit, 0, dt)
```

Summed over a full transit the per-tick partials reproduce the native dwell
integral, so the total damage a ship takes crossing the field matches native
within the per-tick discretisation error. This is a deliberate, documented
simplification (the port has no swept position feed here — the same rationale
the old single-hit pass already recorded at `Sector.cs`). The damage formula is
then the native one verbatim:

```
amount = inFieldDt · a · Power · rHim² · c_damageMultiplier / (2 · rMe)
c_damageMultiplier = 1/(5·5·100)                                  (mineigc.h:116-118)
```

## Implementation

### `Mine.cs`

- `ReduceStrength(float)` — now delegates the decay to `ApplyDecay`, keeps the
  blast `< 0.25f` fraction threshold, then `DestroyMine()`. (Blast path, P96,
  unchanged semantics.)
- `TakeAttritionDamage(float)` — the ship-contact attrition path
  (`mineigc.h:120-128`): applies the same exponential decay, returns the ship's
  `damage` (`oldEndurance − newEndurance`), and destroys the mine when
  `newEndurance < 1.0f` (the native kill test, `mineigc.h:139-142`).
- `ApplyDecay(float)` — shared native endurance decay
  `oldEndurance = E·fraction; newEndurance = oldEndurance·exp(−amount/E);
  fraction = newEndurance/E`; returns `(damage, newEndurance)`.
- `DestroyMine()` — the removal + `MineDestroyedEvent` pair, now shared by the
  expiry (`OnUpdate`), blast (`ReduceStrength`), and attrition
  (`TakeAttritionDamage`) paths.

### `Sector.ResolveMineDetonations(now, dt)`

For every armed mine and every different-side, living ship with `|v|² > 1`
whose sphere overlaps the field (`rMe + 0.5·length`):

1. Compute the tick's in-field dwell from the native quadratic (above).
2. `amount` from native's exact formula/constant; decay the field via
   `mine.TakeAttritionDamage(amount)` (sharing `ReduceStrength`'s decay), which
   returns the ship's `damage`.
3. Record the ship's **worst** mine hit this tick (`AddMineDamage`,
   `shipIGC.h:1998-2016`), then apply each ship's worst hit **once** at the end
   of the pass through `Ship.ReceiveDamage` (`ApplyMineDamage`,
   `shipIGC.h:2017-2035` / `clusterIGC.cpp:334`) — shields and the armor matrix
   included. **No area `Explosion` is ever created.**

A destroyed mine is skipped for the rest of the tick (no further ships hit a
spent field). `MineDestroyedEvent` is raised through the existing path when
`newEndurance < 1` or on expiry (unchanged).

## Tests converted from the old semantics

`MineCombatTests.cs` previously encoded the one-shot behaviour (a parked ship
detonating the mine at full `Power`, the mine consumed, an area blast). Those
were converted to native semantics; nothing was deleted. Coverage now maps to
the brief's verification items:

| Brief | Test |
|---|---|
| (a) stationary ship → zero damage, mine survives | `Stationary_enemy_ship_parked_inside_an_armed_field_takes_zero_damage_and_the_mine_survives` (live) |
| (b) moving ship → native formula damage, hand-computed | `Moving_cross_side_ship_takes_exact_native_formula_attrition_damage` (domain) |
| (c) two ships same tick, field survives worn | `Two_enemy_ships_crossing_in_the_same_tick_both_take_damage_and_the_field_survives_worn` (domain) |
| (d) speed² scaling | `Faster_crossing_takes_more_damage_scaling_with_speed_squared` (domain) |
| (e) repeated crossings wear out + `MineDestroyedEvent` | `Repeated_crossings_wear_the_field_to_destruction_with_mine_destroyed_event` (domain) |
| (f) same-side ship takes nothing | `Same_side_moving_ship_crossing_the_field_takes_nothing_and_does_not_wear_it` (domain) + live `Same_side_moving_ship_is_immune_while_a_cross_side_moving_ship_wears_the_field_without_consuming_it` |
| converted shield path | `Cross_side_moving_ship_with_a_mounted_shield_absorbs_the_attrition_hit_like_a_weapon_hit` (domain) |
| unchanged expiry | `Mine_with_no_intruder_expires_via_lifespan_and_broadcasts_destroy_unmodified` |

Notes on the domain tests:

- They use a 2-side `Mission` + `CountingSite`, deploy the mine at a remote,
  world-clean coordinate (`Remote`), and keep the launcher at the origin so the
  enemy can be driven through the field with no ship-vs-ship / ship-vs-world
  interference.
- The sim dams a ship's commanded velocity each tick (observed 40 → 38.67 m/s),
  so the formula tests hand-compute the expected damage from the **actual
  velocity read back after the tick** (which is exactly what the pass saw) —
  see `NativeOneTickFraction`. The speed² test still resolves to ~4× because
  both ships are damped proportionally.
- The old tests that mounted a shield / relied on exact `Power` maths were
  rebuilt around the native attrition formula with data-derived combo selection.

## Sibling-test conversion (`ExplosionPropagationTests`)

`ExplosionPropagationTests` (P96) contained one live test
(`Live_mine_detonation_blast_reduces_a_second_mine_and_damages_ships_in_range`)
whose premise was a mine *detonation* creating a real area `Explosion` that
cascaded into a second mine and nearby ships. P452 removes that premise — a
mine no longer blasts. The test failed on the fixed code for exactly that
reason (the pre-fix suite showed it among the failures), so it was **converted**
(not deleted) to `Two_armed_mines_do_not_blast_cascade_a_moving_cross_side_ship_wears_only_the_field_it_crosses`:
two armed fields, a moving cross-side ship chewed by the field it crosses,
field A wearing and persisting, and the neighbouring field B provably untouched
(the P452 inversion of the old blast cascade). The generic blast system
(weapon/missile-sourced) and its `ReduceStrength` propagation into mines remain
covered by this file's four domain tests, which are unchanged.

## Pre-fix failing evidence

The new/converted tests were run against the **unfixed** production code
(before `Mine.cs` / `Sector.cs` were changed). Results — `MineCombatTests`
filter, 6 failed / 3 passed:

```
Failed MineCombatTests.Moving_cross_side_ship_takes_exact_native_formula_attrition_damage
  Expected: 0.98 (0.9822)   Actual: 0      ← old code blasted the ship flat, not native attrition
Failed MineCombatTests.Faster_crossing_takes_more_damage_scaling_with_speed_squared
  Actual: NaN                                   ← old code consumed the mine on the first ship; no second measurement
Failed MineCombatTests.Stationary_enemy_ship_parked_inside_an_armed_field_takes_zero_damage_and_the_mine_survives
  Expected: 1   Actual: 0                       ← (a) parked ship detonated + killed on the unfixed code
Failed MineCombatTests.Same_side_moving_ship_is_immune_while_a_cross_side_moving_ship_wears_the_field_without_consuming_it
  (mine consumed / behaviour mismatch)
Failed MineCombatTests.Two_enemy_ships_crossing_in_the_same_tick_both_take_damage_and_the_field_survives_worn
  "enemy 1 must take attrition damage"          ← only the first ship was ever hit; mine consumed
Failed MineCombatTests.Cross_side_moving_ship_with_a_mounted_shield_absorbs_the_attrition_hit_like_a_weapon_hit
  (mine consumed on the old one-shot path)
Passed: expiry test (unchanged path), same-side domain friendly gate, wear-out
(coarse-level behaviour also reached on the old code because it consumed + evented on first hit).
```

The three passes are non-discriminating but valid; every discriminating test
failed for exactly the old one-shot reasons (no velocity gate, single hit, mine
consumed).

## Post-fix verification

After the production change, the `MineCombatTests` filter passes 9/9:

```
Passed!  - Failed: 0, Passed: 9, Skipped: 0
```

Whole-solution build (`/home/domingo/.dotnet/dotnet build`):

```
Build succeeded.  0 Warning(s)  0 Error(s)
```

Full test suite via `/home/domingo/.dotnet/dotnet test`, 3 consecutive runs:
all **mine-related** tests (MineCombatTests + ExplosionPropagationTests, 14
tests) passed every run. The only failures were the pre-existing documented
live-wire timing flakes, whose membership varies run to run (named below); no
deterministic or mine-related regression appeared in any run.

## Verification appendix — full-suite runs

Per-assembly totals, 3 consecutive runs:

| Assembly | Run 1 | Run 2 | Run 3 |
|---|---|---|---|
| Allegiance.Lobby.Tests | 12/12 ✅ | 12/12 ✅ | 12/12 ✅ |
| Allegiance.ClientCore.MonoGame.Tests | 974/974 ✅ | 974/974 ✅ | 973/974 (1 live flake) |
| Allegiance.ClientCore.Tests | 329/333 (4) | 330/333 (3) | 331/333 (2) |
| Allegiance.Core.Tests | 3566/3578 (12) | 3570/3578 (8) | 3568/3578 (10) |

**Mine-related tests: 0 failures in any of the 3 runs.**

Live-wire timing flakes observed (union over the 3 runs — all pre-existing,
varying run to run, and none touching mine logic):
`ConquestTerritoryLiveTests.{Conquest_win_in_one_mission_does_not_leak_to_another,
Flag_capture_tips_conquest_and_broadcasts_game_state_then_game_over,
Flags_disabled_capture_broadcasts_nothing_and_does_not_end}`,
`GameOverPlayersWireTests.{Conquest_win_lists_every_connected_player_including_losing_side,
Conquest_win_in_one_mission_does_not_leak_game_over_players_to_another}`,
`LiveTurretGunnerBindingTests.Board_binds_gunner_and_active_turret_update_roundtrips_live`,
`LiveWinConditionTests.{Multi_mission_disconnect_scopes_per_mission,
Board_refused_once_mission_over}`,
`LiveRipcordTests.Re_target_activates_to_requester_only`,
`JoinFlowLiveTests.EntityTable_TracksARealTickingServer`,
`ShipDeleteGoneLiveTests.DockOut_WatcherLatchesGone_UndockReturnsAndClears`,
`CrossSideCombatVisibilityLiveTests.CrossSide_sector_stays_correct_across_warp_return`,
`ThreePeerSeededSessionTests.SeedThreePeers_TwoSameSide_OneCrossSide_DeterministicVisibility`,
`StationsUpdateLiveTests.Visible_station_received_correct_fields_absent_from_unseeing_side`,
`StationUnderAttackAlertLiveTests.Second_hit_within_throttle_window_produces_no_second_alert`,
`AsteroidMinedLiveTests.No_broadcast_fires_once_miner_hits_capacity`,
`LiveCountdownStartPingTests.StartGame_owner_all_ready_goes_through_real_countdown`,
`LiveRelaunchTests.Carrier_without_launch_slots_uses_backward_fallback`,
`AddPartTests.Part_pickup_decline_recreates_treasure_at_current_position`,
`Allegiance.ClientCore.MonoGame.Tests.P394ShopPreSeedLiveTests.EquippedShield_UntouchedPreseeded_SurvivesRelaunch_Live`.

The set varies run to run (16 → 11 → 13), which is the signature of the
documented live-wire timing flakes (prior reports — e.g. P441 — record the same
families flaking intermittently). None is in the mine subsystem; the pass runs
with no mines hit the early `return` in `ResolveMineDetonations` with no added
cost, so the change is behaviourally isolated to missions that contain mines.

## Out of scope (unchanged)

Enemy-side mine visibility / scan-spotting (P453), the client-side mine visual
strength (`SetMineStrength` → thing site, no wire change), the `c_etMine` visual
micro-explosion, mine-vs-projectile interaction and explosion-vs-mine
(`ReduceStrength` from blasts), station capture's shield gate (P450), station
alerts (P451), and teleport probes (P454) are all deliberately left untouched.

# P156 — the miner decision cycle (NPC autonomously mines and sells)

Branch `master`, atop `d394680` (P155). This task is the decision-only remainder
of the miner-economy arc: everything it dispatches already exists (movement P155,
mining `Sector.ResolveMining`, the dock-sell path in `ServerHost`), so P156 only
*picks which of the two already-working halves an idle NPC walks toward right now*
— mine, or sell.

Scope landed (all citations are the line actually read in this working tree):

1. `DataStationTypeIGC.SabmUnload = 0x01` — `src/Allegiance.Core/Data/DataStationTypeIGC.cs:75`
   (native `c_sabmUnload = 0x01`, `src/Igc/igc.h:744`, "Ability to offload mined
   minerals"). It was genuinely missing (the port only had `SabmRestart`/`SabmRipcord`/
   `SabmLand`/`SabmPedestal`); added in the existing one-line style.
2. `Ship.MiningAsteroidTarget` (`Asteroid?`) — `src/Allegiance.Core/Sim/Ship.cs:666`.
   A new purpose-specific Plan-slot analog kept separate from the station-typed
   `PlanTarget` (P151), same precedent as P154's `AcceptedShipTarget`
   (`Ship.cs:600`).
3. `Ship.FindNearestMinableAsteroid()` — `Ship.cs:787`. Mirrors
   `FindNearestFriendlyStation` (P149, `Ship.cs:722`) over `Sector.Mineable`
   (`Model/Sector.cs:61`, already public), filtered to `Asteroid.CanMineHe3`
   (`Sim/Asteroid.cs:93`) + the `Data.Position is { Length: 3 }` guard
   (`Sector.cs:2044`). No side filter (asteroids are unowned — see §Mutual
   exclusion / reuse).
4. `Ship.ApplyNpcAutopilot` extended — `Ship.cs:2104`. The face/thrust steering
   now runs on a single plain `Vector3` goal resolved from either `PlanTarget`
   or `MiningAsteroidTarget`; both target types feed the *same* steering code, no
   second copy.
5. The decision cycle `Ship.DecideMiningOrSelling()` — `Ship.cs:2197`, called from
   the per-tick `IsNpc` branch of `OnUpdate` (`Ship.cs:2302`).
6. Mutual exclusion (setting one target clears the other) — inside
   `DecideMiningOrSelling` (§ below).
7. Live loopback proof: `tests/Allegiance.Core.Tests/MinerDecisionCycleLiveTests.cs`
   — an idle `Server.SpawnNpcShip` NPC (side 1, no players) autonomously mines a
   full bag and sells it, crediting side 1's treasury by exactly 750
   (`150 ore × OreCreditPerUnit 5`).

## Mutual-exclusion design (native single `c_cmdPlan`)

Native expresses "an NPC is either heading to mine or heading to sell, never both"
as the single `c_cmdPlan` pointer (`shipIGC.cpp:1955` `SetAutoPilot()` toward the
mine target). This port splits that one semantic slot into two typed fields —
exactly the P154 precedent (`AcceptedShipTarget` vs `AcceptedTarget`) — and keeps
them mutually exclusive:

- When the decision cycle sets `MiningAsteroidTarget`, it clears `PlanTarget`
  (`Ship.cs:2213`).
- When it sets `PlanTarget` (the sell leg), it clears `MiningAsteroidTarget`
  (`Ship.cs:2223`).
- A mining target is cleared once the bag fills (`Ore >= MineCapacity`,
  `Ship.cs:2305-2306`) so the cycle can switch to the sell leg — otherwise the
  ship would hover at the rock forever (ResolveMining stops accruing at capacity,
  `Sector.cs:2037`).
- Defensively, `ApplyNpcAutopilot` gives `PlanTarget` precedence if both ever held
  (`Ship.cs:2116-2121`).
- P151 take-cover is guarded to fire only while STILL fully idle after the miner
  decision (`Ship.cs:2320-2321`), preserving the invariant. Ordering is
  miner-first.

## Reuse, not rebuild

This task triggers three pre-existing subsystems and changes none of their logic:

- **Mining** — `Sector.ResolveMining` (`Model/Sector.cs:2023`) is untouched; the
  NPC only parks beside a `CanMineHe3` rock (velocity < 1, `Sector.cs:2051`) and
  the existing rate-based accrual (5 ore/tick at dt 0.05 × `MiningRate 100`,
  `Sector.cs:970`, capped at `Ship.MineCapacity 150`, `Ship.cs:1077`) fills the bag.
- **Selling** — the `ServerHost` dock-sell path (`ServerHost.cs:516-542`) is
  untouched; docking auto-converts `Ship.Ore × Ship.OreCreditPerUnit` (`Ship.cs:1092`)
  and credits the side treasury (`side.Money += (long)proceeds`, `ServerHost.cs:540`).
- **Station search** — the sell leg reuses `Ship.FindNearestFriendlyStation`
  directly (`Ship.cs:722`) with `DataStationTypeIGC.SabmUnload`; no second station
  search was written. The search is side-scoped by its own signature (the ship's
  own side) — asteroids are unowned so only the asteroid search omits a side check.

## Explicit exclusions (stated, not silently hidden)

- **No contention/fair-share logic.** Native's multi-miner least-targeted
  crowding logic (`shipIGC.cpp:2153-2202`) is not ported; the asteroid search has
  no idea which rocks other NPCs are mining, so multiple miners may pick the same
  rock. Stated in `FindNearestMinableAsteroid`'s remarks (`Ship.cs`).
- **No cross-cluster travel.** Same-sector search and movement only (the P149/P155
  established gap; no `c_ttAnyCluster` recursion, `common.cpp:648-652`).
- **No damage-interrupt priority system.** Damage-triggered take-cover (P151)
  interrupting a mining trip is not addressed; ordering is miner-first, so the
  miner decision wins when both could apply. Stated in the `OnUpdate` comment
  (`Ship.cs:2311-2320`) rather than engineering a priority system.
- **Every `IsNpc` ship is a "miner".** This port has no distinct pilot-type
  dispatch (`IsNpc` is the single archetype marker, P148), so the decision cycle
  is simply what every NPC does when idle — the established simplification, not a
  new one this task invents.
- `ResolveMining` / the `ServerHost` sell path are changed by no code here.

## Verification

- Live loopback test (`MinerDecisionCycleLiveTests.cs`) passes: logs
  `[npc] … idle … → mining asteroid 7761`, fills 150/150, `[npc] … full …
  → selling at station 8861 (SabmUnload)`, `[mine-sell] … sold 150.0 ore at 5/u
  = 750.0 credits to side 1`, `[payday] side 1 'Bravo': 0 players — accrued 750
  kept in treasury (balance 750)`, `P156-E2E … sampled side-money delta 750`.
  The NPC is placed on the side with zero players so `Server.OnPayday`
  (`Server.cs:7010-7014`) keeps the proceeds in the treasury (rather than paying
  them out to a seated player and draining `Side.Money` back to 0).
- Full suite: **2761 passed** (2760 baseline + this new test), 0 failed on the
  required 3 consecutive clean runs (runs 10–12). Interim runs hit only the five
  documented timing flakes (e.g. `LiveSuicideShipDeleteTests.
  Combat_death_produces_ship_delete_to_same_audience`), each confirmed clean on
  rerun — none a P156 regression; `MiningSellTests` passes unmodified, proving no
  interference with the mining/selling systems the decision cycle now drives.

Session stats logged via `python3 ~/Development/allegiance-tools/session_stats.py`.

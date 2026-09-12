# NPC Wingman Attack Foundation — P162

Stage 8 of the NPC-autopilot arc (Stages 1-7 = P155-P161): the first honest
slice of Wingman combat AI, applying to combat the exact strategy the arc
applied to `GotoPlan` (P155): **not a faithful port of native's large combat
system, but a small, honest "can a Wingman actually fight at all" slice**
reusing this port's already-built weapon-firing mechanic.

Branch `master`, commit on top of `675c5b7` (P161). No VM needed.

## The finding that shaped the task

Native's per-tick combat maneuvering block
(`Igc/shipIGC.cpp:2353-2787+`, gated by `c_ptWingman || c_ptCheatPlayer` at
`:2353` and `m_commandTargets[c_cmdPlan]->GetCluster() == GetCluster()` at
`:2354`) is genuinely large and sophisticated: `Dodge()` (`:2363`), randomized
`fShootSkill`/`fTurnSkill` tiers (`:2359-2369`), the `c_cidDefend`
protect-an-ally re-evaluation loop (`:2373-2407`), `CanSee` sight gating
(`:2409`), `CosForward` aim checks, and an `openFireDistPercentage = 0.95f`
(`:2361`) — the same scale of system `GotoPlan` was, which P155 explicitly did
**not** port faithfully. P162 applies the identical strategy: port only the
smallest honest slice that lets a Wingman actually fight.

**The reassuring "mechanics already built" finding (matching the arc's
recurring pattern):** the weapon-**firing** mechanic is already fully built and
automatic. `Weapon.Update`/`Weapon.FireWeapon`
(`src/Allegiance.Core/Sim/Weapon.cs:142-348`) already fires whenever the
mounting ship's `StateM` carries the weapon-selection bit —
`Weapon.IsWeaponSelected()` (`Weapon.cs:363-375`) checks
`ShipControlState.AllWeapons` (or `OneWeapon` + a matching mount,
`Weapon.cs:372-374`). That is the **exact same input a real player's client
streams over the wire**. Nothing had ever set that bit for an NPC. Exactly like
mining and warp-transit before it: the mechanic already works; only the AI
decision of *when to hold the trigger* was missing. **This task never modifies
`Weapon.cs` at all.**

## What was built (all in `src/Allegiance.Core/Sim/Ship.cs` unless noted)

1. **`Ship.AttackTarget`** — a new dedicated `Ship?` slot (Ship.cs:724-744),
   following the per-purpose-slot precedent of `MiningAsteroidTarget` (P156)
   and `LayerAsteroidTarget` (P161). The port of native's
   `SetCommand(c_cmdAccepted, pmodel, c_cidAttack)` (`Igc/shipIGC.h:2447`).
   Cleared when the target dies, docks, or leaves the ship's current sector
   (in `UpdateWingmanCombat`, below) — no cross-sector pursuit.

2. **`Ship.FindNearestEnemyShip()`** (Ship.cs:1101-1146) — the Wingman default
   order's target search, the port of native
   `FindTarget(this, c_ttEnemy | c_ttShip | c_ttNearest, NULL, pcluster, ...)`
   (`Igc/shipIGC.h:2441-2443`). **Same-sector only** — native passes **no**
   `c_ttAnyCluster` (unlike the Miner's/Layer's searches), so a Wingman never
   travels to find a fight; it only reacts to enemies already sharing its
   sector. Enumeration reuses `Sector.GetShipUpdateCandidates()`
   (`Sector.cs:290-302`), the port's established per-sector ship-membership
   accessor (undocked, alive, non-boarded), with a self-exclusion and a
   nearest-by-squared-distance rule (the `dp.LengthSquared()` strict-`<`
   replace of `common.cpp:575-578,617`).

3. **`Ship.DecideWingmanDefaultOrder()`** (Ship.cs:2816-2840) — the decision:
   idle+undocked gate (`AttackTarget is null`, `StationId is null`), sets
   `AttackTarget` to the nearest enemy if one exists, else stands by (logged,
   retries next tick — matching every prior decision cycle's no-op precedent).
   This is the port of native `PickDefaultOrder`'s `c_ptWingman` case
   (`Igc/shipIGC.h:2436-2455`); the `HandlePickDefaultOrder` training-mission
   hook (`:2438`) is skipped (no training-mission system in this port).

4. **`ApplyNpcAutopilot` combat goal branch** (Ship.cs:2511-2519) — a fourth
   goal-resolution branch, steering toward the enemy's **live** position each
   tick through the *same* shared face+thrust primitive. To keep "no new
   steering code", the existing inline face+thrust body was extracted verbatim
   into `SteerToward(targetPos, arrivalRadius, brakeStartDistance)`
   (Ship.cs:2561-2606), so the combat leg varies **only** the arrival/brake
   radii, never the steering law. The combat arrival condition is swapped from
   "stopped on the point" (`AutopilotArrivalRadius = 60`) to **"close enough
   to fire"**: a standoff inside the mounted weapon's effective range
   (`min(0.7 · weaponRange, 350)`, floored at the arrival radius) — so the
   Wingman closes to engagement range and holds there, nose-on, rather than
   trying to stop exactly on top of the enemy. **No orbit/strafe geometry is
   invented** — this is the simple, stated approach the task calls for.

5. **`Ship.EstimateWeaponRange()`** (Ship.cs:2608-2619) — the honest weapon
   reach: the max over mounted weapons of `projectileSpeed · projectileLifespan`
   (the furthest a round travels before despawning, `Weapon.cs:232-233` and
   `DataProjectileTypeIGC.Speed`/`Lifespan`, `DataProjectileTypeIGC.cs:22-23`).
   Not a guessed constant.

6. **`Ship.UpdateWingmanCombat()`** (Ship.cs:2862-2901) — the per-tick combat
   leg, called from `OnUpdate` (Ship.cs:3105-3111) **before** the weapon loop
   so this tick's selection bit is in `StateM` when `Weapon.Update` reads it.
   It (a) validates `AttackTarget` and clears it when it dies/docks/leaves the
   sector/is no longer a different-side enemy (no cross-sector pursuit), and
   (b) sets the `ShipControlState.AllWeapons` bit when the target is alive,
   same-sector, within `EstimateWeaponRange()`, **and** the nose is within
   `WingmanFireFacingCosine` (0.90) of the target's current position — using
   the CosForward-style `forward·dir` dot-product the P155 turn law already
   uses (`−Orientation.TimesInverse(dir).Z`, Ship.cs:2588). Clears the bit when
   any condition stops holding.

7. **`OnUpdate` wiring** (Ship.cs:3200-3215) — a `PilotType == PilotTypeWingman`
   decision branch (decide only when idle+undocked), and the take-cover idle
   gate extended to also require `AttackTarget is null` (Ship.cs:3234-3237) so
   an actively-pursuing Wingman is not diverted into take-cover (the same
   protection P161 added for the Layer's rock).

## Confirmed findings (each re-read this session)

- **"Enemy" = a different `SideId`.** This port has no alliance system — the
  established finding P159 states for its own same-side filter and P149
  documents for the friendly-station `c_ttFriendly` gate. The whole enemy test
  is `ship.SideId != this.SideId` with both sides non-null — the
  `pside != pHisSide` / `!AlliedSides` story (`common.cpp:269-273`) with no
  alliance tier to add. A ship with no `SideId` finds/enemy-none (native's
  null-`pside` path never matches `c_ttEnemy`).
- **Same-sector only for target *selection*** — matches native's literal case
  (`Igc/shipIGC.h:2441-2443`, no `c_ttAnyCluster`). Stated plainly: **a
  Wingman does not travel to find a fight; it only reacts to enemies in its
  current sector.** This is deliberate (and not the "limitation" Layer/Miner's
  cross-cluster searches fixed) — native's Wingman is same-sector-only by
  design.
- **`ApplyNpcAutopilot` already re-reads its goal fresh every tick** (the
  `PlanTarget`/`MiningAsteroidTarget`/`LayerAsteroidTarget`'s *current*
  position is read each `OnUpdate`; nothing caches a stale position), so
  pointing it at a moving ship's live position each tick already works for
  "chase" — confirmed by the task's own read of `Ship.cs`. No new re-aim logic.
  The genuine difference for combat (the existing brake/arrival heuristics
  were tuned to stop *at* a static point) is handled by the fire-range standoff
  in item 4 above, not by reinventing orbit/strafing geometry.
- **The reused weapon-firing mechanic** — `Weapon.Update` (`Weapon.cs:140-154`)
  → `FireWeapon` (`Weapon.cs:189-352`) fires automatically once `IsWeaponSelected()`
  (`Weapon.cs:363-375`) is satisfied. `AllWeapons = 32768` and
  `OneWeapon = 16384` are declared in `ShipControlState.cs:40-42`, copied
  verbatim from the native `ShipControlStateIGC` enum. P162 only ever sets
  `StateM |= ShipControlState.AllWeapons`; it never touches `Weapon.cs`.

## Live proof (new `tests/Allegiance.Core.Tests/NpcWingmanAttackLiveTests.cs`)

Two loopback tests, no VM, driving a real in-process server + one seated bot
and the deterministic `Srv.TickSim`:

- **Primary:** an idle real Wingman drone type (PCore001 `PilotType == 2`),
  given the fightable dispersion-0 loadout (`LiveCombatWireTests`' own weapon
  selection: `Power > 0`, no aim scatter), picks the nearest enemy-side ship
  (different `SideId`) in the sector, closes distance, holds the trigger when
  in range and facing, and **really damages it** — observed
  `enemy.Fraction` 1.0 → 0.94 from real projectile collisions. This proves the
  full loop (decision → pursuit → the pre-existing weapon mechanic engaging
  for real), *not* merely a `StateM` bit flip: the enemy's `Fraction` dropping
  below 1.0 is exactly the existing damage observability `LiveCombatWireTests`
  uses (there asserted to a kill over the wire; here asserted directly on the
  sim model for the NPC target). Assertions: `AttackTarget == enemy`, `StateM &
  AllWeapons` seen, `Fraction < 1.0`, distance closed, still engaged.
- **Negative (decision gate):** with the only other-side ship parked in a
  different sector and a same-side friend nearby, an identical armed Wingman
  stands by — no `AttackTarget`, no movement, never held the trigger. Proves
  the attack behavior only engages when an enemy is actually in the sector and
  that a same-side ship is never mistaken for one.

## Explicit exclusions (all confirmed real, large, separate — each native piece
is cited so it can be ported later if ever wanted)

- **Dodging** (`Dodge()`, native combat block) — not ported.
- **Randomized shoot/turn-skill tiers** (`fShootSkill`/`fTurnSkill`,
  `c_ptCheatPlayer`-specific tuning, `shipIGC.cpp:2359-2369`) — not ported; a
  P162 Wingman always fires whenever the simple facing/range condition holds.
- **`c_cidDefend` protect-an-ally re-evaluation loop** (`shipIGC.cpp:2373-2407`)
  — a real, separate behavior (guarding another ship, breaking off pursuit to
  return to its charge) — not built; only the plain `c_cidAttack` default-order
  case is implemented.
- **Cross-sector pursuit** — a fleeing enemy that warps out is simply lost
  (target cleared in `UpdateWingmanCombat`, stand by) — matches native's own
  same-sector-only selection (`shipIGC.h:2441-2443`), not a new limitation.
- **Aim leading / shot prediction** — fire at the target's current position
  when facing within tolerance, no velocity-lead calculation.
- **Any change to `Weapon.cs`** — the firing mechanic is already correct and
  untouched; this task only sets the pre-existing `StateM` selection bit.
- **No "owner" / squad-assignment layer** — the Wingman is an individual
  reacting to the nearest enemy in its sector (native's `FindTarget(...,
  c_ttNearest, ...)`), not tied to escorting a specific allied ship (that is
  `c_cidDefend`, excluded above).

## Verification

- Baseline re-confirmed before changes: **2785/2785** tests pass (`dotnet
  test`, `net10.0`), the P161 checkpoint count.
- New live tests pass in isolation, 3 consecutive runs (2/2 each).
- Related suites (LiveCombatWireTests, LiveSuicideShipDeleteTests,
  NpcPilotTypeGateLiveTests, NpcLayerDefaultOrderLiveTests,
  NpcAsteroidContentionLiveTests, NpcCrossClusterMinerDecisionLiveTests) pass
  together — the sole failure in that combined run was the documented
  `LiveSuicideShipDeleteTests.Combat_death_produces_ship_delete_to_same_audience`
  flake, confirmed to pass in isolation (not a regression).
- Full suite verified 3 consecutive clean runs (below).
- Session stats logged via `session_stats.py`.

# P159 — Fair-share asteroid contention among same-side NPC miners

**Branch:** `master` (on top of P158 `86afb87`)
**Scope:** close the gap P158's report named: `Ship.FindNearestMinableAsteroidAnyCluster`
has no awareness of other ships already heading to the same rock, so two
same-side idle NPCs can independently target the identical asteroid. This task
ports native's real-time arbitration (`CshipIGC::PlotShipMove`, "Special case
up front: miners mine", `Igc/shipIGC.cpp:2088-2204`) as a new per-tick step.

---

## Design confirmed: real-time arbitration, not avoidance-at-selection-time

Native has **no search-time collision avoidance either** — `FindNearestMinableAsteroidAnyCluster`
(P158) still lets two same-side NPCs both pick the same rock; the fairness is
enforced *continuously* afterward by `PlotShipMove`'s miner block, which runs
**every tick** while the ship holds `wantsToMineMaskIGC`
(`shipIGC.cpp:2088-2091`). Confirmed from the actual lines:

- The block is gated on `m_pilotType == c_ptMiner` **and** `(m_stateM &
  wantsToMineMaskIGC) != 0` (`shipIGC.cpp:2088-2091`) — not once at decision
  time, but every tick. This is the whole reason a decision-time-only port
  would be wrong: once a ship picks a rock it is no longer idle
  (`Ship.cs:2530` only calls `DecideMiningOrSelling()` when `MiningAsteroidTarget
  is null`), so a one-shot check would never re-arbitrate a later-arriving
  poorer contender. Hence P159 wires the arbitration call in
  `Ship.OnUpdate` **outside** the idle gate (`Ship.cs:2683-2684`): every tick,
  for an `IsNpc` ship with a non-null `MiningAsteroidTarget`.

**Port line:** `Ship.ArbitrateAsteroidContention()` (`Ship.cs:2514`) + call site
`Ship.cs:2683-2684`.

---

## Behavior (ported 1:1 from `shipIGC.cpp:2099-2185`)

For a ship with a non-null `MiningAsteroidTarget`, the step:

1. **Enumerates same-sector ships sharing the *identical* rock reference.**
   - Sector ship set = `Sector.GetShipUpdateCandidates()` (`Sector.cs:290`),
     reused deliberately rather than adding a new accessor (see *Reuse
     reasoning* below). It returns exactly "ships physically in the cluster —
     undocked, non-boarded, non-dead" (`Sector.cs:293-301`), which is precisely
     the set native iterates as `pcluster->GetShips()` (`shipIGC.cpp:2106`).
   - "Same rock" = reference equality on `MiningAsteroidTarget`
     (`Ship.cs:666`, an `Asteroid` reference) — the port's exact mirror of
     native's pointer equality `GetCommandTarget(c_cmdPlan) ==
     m_commandTargets[c_cmdPlan]` (`shipIGC.cpp:2113`). No new equality concept.
   - "Same side" = exact `SideId` equality (`Ship.cs` arbitration:
     `ship.SideId != sideId → skip`). This port has **no alliance system**
     (confirmed by `FindNearestFriendlyStationIn`'s own filter,
     `Ship.cs:735`: `station.SideId != sideId`), so native's
     `AlliedSides` half of `shipIGC.cpp:2115` has no analogue — "same side" is
     the whole test, exactly as established earlier this session.
   - `this` ship is counted like any other, so a rich contender redirects
     itself when its own tick runs the step.

2. **Winner = the contender with the least `Ore`; every other contender is
   peeled off to go sell.**
   - Least-ore selection uses native's **strict `<` replace**
     (`fOre < fOreMin`, `shipIGC.cpp:2121`): `Ship.cs` arbitration tracks
     `pshipMin`/`fOreMin` with `if (ship.Ore < fOreMin) { … }`.
   - **Tie-break (applied, matched to native):** on an ore tie the strict `<`
     keeps the **first** contender encountered in `GetShipUpdateCandidates()`
     order (stable `_models` insertion order, `Sector.cs:283-284`) — the same
     first-found rule native's loop exhibits (`shipIGC.cpp:2121`). The
     equal-ore live test (`Equal_ore_two_same_side_idle_miners_…
     _first_spawned_keeps_rock_second_redirected`) pins this: the first-spawned
     ship keeps the rock, the second is redirected.
   - **Losers go sell, not to a second rock.** Each displaced contender has its
     `MiningAsteroidTarget` cleared and is redirected to the nearest friendly
     `SabmUnload` station via the P158 cross-cluster search
     `FindNearestFriendlyStationAnyCluster(DataStationTypeIGC.SabmUnload)`
     (`Ship.cs` arbitration) — the exact same
     `SetStateBits(miningMaskIGC|wantsToMineMaskIGC, 0)` →
     `FindTarget(…, c_sabmUnload|c_sabmLand)` → `SetCommand(c_cmdPlan,…,
     c_cidGoto)` sequence native uses (`shipIGC.cpp:2156-2168`). The redirect
     writes `PlanTarget` + `PlanCommandId = OrderChange.CidGoto`
     (`Ship.cs:666` target slot), the same pattern the full-bag branch of
     `DecideMiningOrSelling` already uses (`Ship.cs:2434-2435`). It does **not**
     look for a different asteroid.
   - **No unload station reachable → stand by** (logged, non-crashing):
     `PlanTarget = null; PlanCommandId = CidNone`, matching native's
     `SetCommand(c_cmdAccepted, NULL, c_cidNone)` else-branch
     (`shipIGC.cpp:2174-2178`) and `DecideMiningOrSelling`'s own no-station
     stand-by (`Ship.cs:2443`).

3. **Winner grant is implicit.** `Sector.ResolveMining` accrues ore purely
   geometrically for any ship parked beside a `CanMineHe3` rock, so "keeping
   the right to mine" is simply the loser being removed from the rock — the
   native's explicit `pshipMin->SetStateBits(miningMaskIGC|wantsToMineMaskIGC,
   miningMaskIGC)` (`shipIGC.cpp:2185`) has no separate mask to set here. The
   winner needs no write beyond retaining its `MiningAsteroidTarget`.

**Convergence.** Each contender runs the step (as in native), but the writes are
monotonic — a loser's `MiningAsteroidTarget` is cleared and its `PlanTarget`
set, so it stops competing from the next tick; the winner is left alone
(`nFriendly <= 1` → no-op). Whichever ship ticks first, the same ship wins (it
is always the least-ore one on the different-ore case, the first-enumerated on
a tie), so there is no oscillation. Verified live.

---

## Reuse reasoning: `GetShipUpdateCandidates()`

The step reuses `Sector.GetShipUpdateCandidates()` rather than adding a new
accessor (e.g. a parallel `Ships` list mirroring `Stations`/`Mineable`/`Warps`).
Rationale, stated plainly:

- Its name is network-flavoured (it feeds `LIGHT/HEAVY_SHIPS_UPDATE` candidate
  assembly), but its *selection* is cluster-physical membership: undocked,
  non-boarded, non-dead ships in the sector (`Sector.cs:293-301`), which is
  exactly the set native's own arbitration loop iterates
  (`pcluster->GetShips()`, `shipIGC.cpp:2106`). The filter decisions are
  documented as faithful ports of the native candidate-list semantics
  (`Sector.cs:253-289`).
- Reusing it means arbitration reads the *same* physically-present ship set the
  rest of the cluster-model uses, with no divergent duplication of `_models`
  filtering logic, and no second list to keep in sync when a ship docks,
  boards, dies, or joins.
- The one consideration against (semantic coupling of a network concern to a
  gameplay one) is outweighed: the method is a pure query with no
  side-effects, and arbitration is a legitimate second reader of
  cluster-physical membership. A `Ships` accessor would be a near-copy of the
  same filter. This is recorded so a future port can revisit if the two
  concerns ever diverge.

---

## Explicit exclusions (deliberate gaps, not silently dropped)

- **Cross-side ("an enemy also wants it") lockout.** Native's `nEnemy > 0`
  branch (`shipIGC.cpp:2187-2203`) evicts *every* contender on both sides with
  a "too crowded" complaint when an enemy wants the exact same rock. **Not
  ported** — this port's enemy/hostility detection beyond raw `SideId`
  inequality has not been established as sufficient for this specific
  mechanic, and scoping it would roughly double this task. P159's step only
  arbitrates among same-side contenders and ignores cross-side ones entirely.
  Documented in the method's XML remarks (`Ship.cs` arbitration) and here.
- **`Sector.ResolveMining` accrual mechanics — untouched.** Exclusivity is
  enforced purely by redirecting losers away from the rock; once redirected
  they fly out of `ResolveMining`'s proximity gate naturally
  (`Sector.cs:2016+`), so no accrual-side change was needed or made.
- **Real (non-NPC) player ships — never arbitrated.** Native's block only runs
  for `c_ptMiner`-piloted, self-piloting ships (`shipIGC.cpp:2061`, 2088).
  This port has no non-miner pilot-type dispatch; the call site is gated on
  `IsNpc` (`Ship.cs:2683`), so real player ships are unaffected — the
  established single-archetype simplification, recorded for the future.
- **No change to any P103–P158 code** beyond the new arbitration method
  (`Ship.cs:2514-2647`) and its `OnUpdate` call site (`Ship.cs:2683-2684`).
  `git diff --stat` confirms the only changed source file is `Ship.cs`.

---

## Live verification

Loopback `Bot`/in-process-server tests in
`tests/Allegiance.Core.Tests/NpcAsteroidContentionLiveTests.cs` (two `[Fact]`
cases; the `XXV_simple.igc` map has zero native asteroids, so the single
injected He3 rock is the only mineable anywhere reachable):

1. **Least-ore wins / redirect-to-sell.**
   `Two_same_side_idle_miners_lower_ore_keeps_rock_higher_ore_redirected_to_unload`
   — two same-side idle NPCs (ore 10 and 40) in a sector with exactly one rock
   and one `SabmUnload` station (station 380 u from the rock; both spawns >140 u
   from the rock and >250 u from the station). Both start idle and both would
   pick the same rock (`FindNearestMinableAsteroidAnyCluster() == rock` for
   each). After arbitration: the ore-10 ship keeps the rock reference
   (`Assert.Same(rock, poor.MiningAsteroidTarget)`), the ore-40 ship has
   `MiningAsteroidTarget == null`, `PlanTarget.StationId == unload`, and
   `PlanCommandId == OrderChange.CidGoto` — i.e. redirected to the **unload
   station**, not to a (second) asteroid (there is only one). Phase 3: the
   winner's `Ore` rises above its start (it actually mines, physically reaching
   the rock), while the loser's `Ore` never rises (it never mines) and it flies
   toward the unload station.
2. **Tie-break (first-found on an ore tie).**
   `Equal_ore_two_same_side_idle_miners_first_spawned_keeps_rock_second_redirected`
   — both ore 25; the first-spawned (first in `_models`/`GetShipUpdateCandidates`
   order) keeps the rock, the second is cleared and redirected to the unload
   station.

**Suite:** full `Allegiance.Core.Tests` suite passes unmodified. The three
P156–P158 files the new per-tick step shares the decision cycle with —
`MiningSellTests.cs`, `AnyClusterAsteroidSearchTests.cs`,
`NpcCrossClusterMinerDecisionLiveTests.cs` (plus `MinerDecisionCycleLiveTests.cs`
and `NpcAutopilot*`) — pass in isolation (21/21), proving no interference with
the single-miner case (there `nFriendly == 1` makes the step a no-op).

### Verification runs
Full suite `Allegiance.Core.Tests` (net10.0): **3 consecutive clean runs of 2781/2781** (P158 baseline 2779 + 2 new P159 tests). Build clean, 0 warnings.

Runs performed during this task (all failures were the documented wire-timing flakes below, each independently re-run in isolation and confirmed to pass — none is a regression; a `git stash` A/B check on `Ship.cs` plus repeated-isolation runs confirmed `LiveCombatWireTests` fires intermittently on the untouched baseline too):

| run | result |
|-----|--------|
| 1  | 2780/2781 — 1 documented flake |
| 2  | 2779/2781 — 2 documented flakes |
| 3  | **2781/2781 clean** |
| 4  | 2779/2781 — 2 documented flakes |
| 5  | 2780/2781 — 1 documented flake |
| 6  | 2780/2781 — 1 documented flake |
| 7  | 2780/2781 — 1 documented flake |
| 8  | 2779/2781 — 2 documented flakes |
| 9  | 2780/2781 — 1 documented flake |
| 10 | **2781/2781 clean** |
| 11 | **2781/2781 clean** |
| 12 | **2781/2781 clean** |

Across all 12 runs the only failures were exactly the documented wire-timing flakes —
`LiveCombatWireTests` (kill-credit-to-peer timing) and
`LiveSuicideShipDeleteTests.Combat_death_produces_ship_delete_to_same_audience` —
none of which is a regression and each of which passes in isolation. The new
arbitration step is gated on `IsNpc && MiningAsteroidTarget is not null`, so it
never executes for the player/bot combat ships those flaky tests exercise.

Documented flakes (rerun in isolation, confirmed passing; NOT regressions):
- `LiveChatSectorWireTests`
- `LiveCombatWireTests` (`Real_two_party_combat_produces_a_kill_ship_over_the_wire`,
  `Real_Bot_class_observes_its_own_kill_over_the_wire`)
- `LiveInSpaceEjectTests.Already_a_lifepod_is_truly_killed_not_re_ejected`
- `LiveSuicideShipDeleteTests.Combat_death_produces_ship_delete_to_same_audience`
- `ShipsUpdateLiveTests.Locked_target_fills_first_heavy_slot_with_real_fractions`

P156/P158 decision-cycle regressions in isolation (all pass):
`MiningSellTests`, `AnyClusterAsteroidSearchTests`,
`NpcCrossClusterMinerDecisionLiveTests`, `MinerDecisionCycleLiveTests`,
`NpcAutopilot*` — 21/21.

---

## Session stats
Logged via `python3 ~/Development/allegiance-tools/session_stats.py 20260911_085608_6ba35c`.

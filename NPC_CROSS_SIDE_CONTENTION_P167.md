# P167 — Cross-side asteroid-contention lockout

**Branch:** `master` (on top of P166 `e2b578b`)
**Scope:** close the gap P159's own report named and deferred
(`NPC_ASTEROID_CONTENTION_P159.md`): when an *enemy*-side ship also wants the
exact same asteroid, native locks the rock out for **everyone**, both sides.
P159 arbitrated same-side contenders only and left cross-side contention
entirely unmodeled. This task implements native's `nEnemy > 0` branch
(`Igc/shipIGC.cpp:2187-2203`) in the port's existing
`Ship.ArbitrateAsteroidContention`.

---

## What native does (confirmed from the block)

The "miners mine" contention block in `CshipIGC::PlotShipMove`
(`Igc/shipIGC.cpp:2088-2204`) first makes a **counting pass** over the cluster's
ships wanting to mine THIS exact asteroid (same `c_cmdPlan` target pointer):

- `pship->GetSide() == pside || AlliedSides(...)` → `nFriendly++` (tracking the
  least-ore friendly contender)
- else → `nEnemy++`

Then it branches:

- **`nEnemy == 0`** → the same-side resolution P159 already ported
  (least-ore friendly wins; losers redirected to sell).
- **`nEnemy > 0`** (`shipIGC.cpp:2187-2203`) → evict **EVERY** contender wanting
  this rock, regardless of side, with a `droneTooCrowdedSound` complaint and
  **no winner at all** — no least-ore grant, no redirect-to-sell.

The eviction loop in the `nEnemy > 0` branch has **no side filter** — every
cluster ship with `wantsToMineMaskIGC` set and `GetCommandTarget(c_cmdCurrent)
== this asteroid` is cleared (`SetStateBits(miningMaskIGC|wantsToMineMaskIGC,
0)`). It also does **not** call the `FindNearestFriendlyStationAnyCluster`
sell-redirect that the same-side loser branch immediately above it does — evicted
ships just stand idle.

---

## The port's convention: "enemy" = a different `SideId`

Exactly as P159 established (and this port has repeated in every pilot-type
decision): **no alliance system exists in this port**, so a contender whose
`SideId` differs from the arbitrating ship's is an "enemy". Native's
`AlliedSides` check has no analogue here. This task does **not** add one —
`nEnemy` is incremented purely on `ship.SideId != sideId`
(`Ship.cs:3414-3422`). Same-sector scope matches P159 exactly: the survey walks
`Sector.GetShipUpdateCandidates()` (`Ship.cs:3402`), the port's port of native's
`pcluster->GetShips()` — no scope change needed.

---

## Change: `Ship.ArbitrateAsteroidContention` (`src/Allegiance.Core/Sim/Ship.cs`)

The method keeps its same-side survey but now also **counts enemy contenders**
(`Ship.cs:3414-3422`), then branches (`Ship.cs:3427-3448`):

- `nEnemy > 0` → **cross-side lockout** (`Ship.cs:3438-3448`): loop every ship in
  the sector sharing the identical asteroid reference (no side filter, matching
  native), clear its `MiningAsteroidTarget`, log the eviction, and **return**
  without ever reaching the same-side least-ore/redirect-to-sell resolution.
- `nEnemy == 0` → the existing P159 same-side resolution, **unchanged**
  (least-ore wins, losers redirected to sell).

Each eviction is logged with `Console.WriteLine` (`Ship.cs:3444`), matching the
decision-cycle precedent everywhere else in this port.

### No-redirect, honest outcome (vs. the same-side redirect path)

A cross-side-evicted contender's `MiningAsteroidTarget` is cleared with **no**
`PlanTarget` set — unlike the same-side loser path (`Ship.cs:3451-3477`), which
sets `PlanTarget` to the nearest friendly unload station. The evicted ship goes
idle; its own next tick's `DecideMiningOrSelling` (`Ship.cs:2893`) re-evaluates
from the cleared state — which **may well pick the same or a different rock
again if nothing else changed** (the `MiningAsteroidTarget is null` gate at
`Ship.cs:3592` re-runs it). This is the honest, faithful outcome, stated
plainly as intended, not a bug: native likewise clears the state and lets the
ship's own default-order cycle re-decide.

### A documented, inherited nuance (not the cross-side branch)

This port's `DecideMiningOrSelling` re-picks a rock **every tick** for an idle
ship (no throttling), so a same-side ship that is processed before the enemy
re-acquires its rock within a tick can momentarily observe no enemy contender
and fall back to P159's same-side path (its richer member redirected to the
nearest friendly unload). That is a pre-existing consequence of the P159
per-tick design — explicitly out of scope to change here — not the cross-side
branch, which **never** redirects. The live test asserts the cross-side lockout
on the simultaneous-contention eviction (all three genuinely contending → all
three evicted, no redirect) and the durable no-mining outcome that holds
regardless of that artifact.

---

## Live test: `tests/Allegiance.Core.Tests/NpcCrossSideContentionLiveTests.cs`

Harness mirrors P159 (`NpcAsteroidContentionLiveTests`): in-process server + one
seated bot, no Windows VM; mission advanced by the server's own deterministic
`TickSim` (one 0.05 s chunk each).

`Enemy_contender_evicts_everyone_no_redirect_and_none_mines`:
two same-side idle NPCs + one enemy-side idle NPC, all forced onto the
**identical** rock reference (the P159-style test-setup force), all spawned
>140 u from the rock and >250 u from the unload station.

1. **First simultaneous arbitration** → all three genuinely contending at once →
   the cross-side branch fires → **all three** `MiningAsteroidTarget` cleared
   **and** all three `PlanTarget` still `null` (no station redirect). This
   assertion is discriminating: **without P167** the least-ore same-side ship
   keeps the rock and the other same-side ship is redirected (verified by
   temporarily neutralizing the branch — the test failed exactly there).
2. **Durable lockout** over a long window → none of the three ever mines (no
   `Ore` accrues), and the two cross-side-denied ships (the would-be same-side
   least-ore winner and the enemy) are never redirected to sell — the lockout
   overrides P159's least-ore grant entirely.

---

## Exclusions (unchanged, each confirmed)

- **Same-side (no-enemy-contender) resolution path** — P159's least-ore-wins /
  redirect-to-sell logic stays exactly as landed (`Ship.cs:3450-3465`); no
  change there.
- **`droneTooCrowdedSound` chat complaint** — this port has no drone
  voice-line/chat-complaint system for NPCs (confirmed absent); the eviction is
  logged with `Console.WriteLine` instead, matching every other decision cycle.
- **Anything beyond `ArbitrateAsteroidContention` itself in P103-P166** — no
  other code touched. `Ship.cs` changes are confined to the method + its XML
  docs; `DecideMiningOrSelling`, the same-side path, the per-tick call site, and
  all pilot-type decision cycles are untouched.

---

## Verification

- No VM needed.
- Live test above (passes 5× in isolation).
- `NpcAsteroidContentionLiveTests` (P159, exercising the exact extended method):
  passes unmodified in isolation — proving the same-side-only scenarios are
  unchanged.
- Full suite: **2799 tests** (2798 + 1 new) across 8 full runs; 3 clean
  full-suite runs, and every failure in the other 5 was exclusively one of the
  five documented flakes (`LiveSuicideShipDeleteTests`,
  `LiveCombatWireTests`, `LiveChatSectorWireTests`) — none a regression. Mining
  subsystem (17 tests: `MiningSellTests`, `AnyClusterAsteroidSearchTests`,
  `NpcCrossClusterMinerDecisionLiveTests`, `NpcAsteroidContentionLiveTests`,
  `NpcCrossSideContentionLiveTests`) passes clean in isolation.

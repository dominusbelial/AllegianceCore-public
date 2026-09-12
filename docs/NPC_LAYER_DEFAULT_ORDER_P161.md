# P161 — Layer pilot-type default order: least-targeted asteroid positioning

Stage 7 of the NPC-autopilot arc, the **first real pilot-type behavior** built
on the P160 `Ship.PilotType` foundation. An idle, undocked **Layer** NPC
(whose drone type's pilot type is `c_ptLayer` = 5) picks the **least-targeted
reachable neutral asteroid** and flies to it, reusing this port's existing
`CidGoto` command and `ApplyNpcAutopilot` movement pipeline — no new command
type, no new game mechanic, no deployable.

Commit follows the repo's P-series convention (direct to `master`). Branch
was confirmed `master` at `b150013` (P160) before work began.

---

## What native does — the cited contract

`Igc/shipIGC.h:2417-2434` — `PickDefaultOrder`'s `c_ptLayer` case:

```cpp
case c_ptLayer:
{
    if (!bDocked)
        break;

    ImodelIGC*  pmodel = FindTarget(this,
                                    c_ttNeutral | c_ttAsteroid | c_ttNearest |
                                    c_ttLeastTargeted | c_ttAnyCluster | c_ttCowardly,
                                    NULL, pcluster, &position, NULL,
                                    0);

    if (pmodel)
    {
        SetCommand(c_cmdAccepted, pmodel, c_cidGoto);
        fGaveOrder = true;
    }
}
break;
```

Semantics read directly, not taken from the summary:

- **`c_ttNeutral | c_ttAsteroid`** — any unowned asteroid, not specifically a
  He3 rock. This port's asteroids are all unowned (established P149/P156
  finding), so the search enumerates every runtime asteroid with **no**
  `CanMineHe3` gate.
- **`c_ttLeastTargeted`** — the counting rule (`Igc/common.cpp:582-620`):
  for each asteroid candidate, count every same-side **or allied** NPC
  (`GetPilotType() < c_ptPlayer`) whose `GetCommandTarget(c_cmdAccepted) == m`
  **and** `GetCommandID(c_cmdAccepted) >= c_cidMine` (`common.cpp:591-605`) —
  i.e. NPCs actively mining/building at that asteroid; a plain Goto doesn't
  count. The iteration is mission-wide (`pside->GetMission()->GetShips()`,
  `common.cpp:591`; the `c_ttAnyCluster` flag likewise makes the search
  recurse across the warp graph). Fewest contenders wins.
- **Tie-break** — `common.cpp:607`: `bReplace = ((n < nTargeting) ||
  ((n == nTargeting) && (d < distance)))` — fewer contenders wins; on an
  equal count the *nearer* candidate wins.
- **`c_ttCowardly`** — restricts warp traversal toward friendly territory;
  confirmed absent as a port concept (no sector-ownership field exists),
  so dropped, consistent with P157's `FindPathToward` finding.

Native also gates the least-targeted replace on `abmAbilities &
c_aabmMineHe3` (`common.cpp:608`). The task treats the Layer target as any
unowned asteroid (per `c_ttNeutral | c_ttAsteroid`); whether a Layer hull
carries a mining ability is a separate native nuance not reproduced here, and
is stated as part of the applied simplification below.

## What the port implements

Four additions, all in `src/Allegiance.Core/Sim/Ship.cs`:

1. **`Ship.LayerAsteroidTarget`** (`Ship.cs:723`) — a dedicated `Asteroid?`
   slot following the `MiningAsteroidTarget` (P156) precedent exactly: its own
   purpose-specific slot, *not* a reuse of the miner's. This is what keeps a
   Layer's decision cycle and a Miner's structurally independent (each pilot
   type owns only its slot).

2. **`Ship.FindLeastTargetedAsteroidAnyCluster`** (`Ship.cs:1022-1095`) — the
   least-targeted, any-cluster asteroid search. Walks the same sector-warp
   graph as `FindNearestFriendlyStationAnyCluster` /
   `FindNearestMinableAsteroidAnyCluster` (`Sector.Warps` →
   `ResolvedWarp.Destination`, no third graph walk), hop 0 = own sector
   searched first, each sector visited once by reference identity. For each
   runtime asteroid with a position it counts same-side NPC contenders and
   keeps the fewest-contention one. Strict `<` replace → **first-found wins a
   tie** (`Ship.cs:1086`), consistent with the P159 tie-break precedent and
   the port's honest stand-in for native's nearer-on-tie
   (`common.cpp:607`); because the own sector is searched first and rocks are
   enumerated in insertion order, first-found already prefers nearer rocks.

3. **`Ship.DecideLayerDefaultOrder`** (`Ship.cs:2642-2673`) — the decision:
   commit `LayerAsteroidTarget` to the found rock (or stand by if none).

4. **Wiring** —
   - `Ship.OnUpdate` (`Ship.cs:2885-2893`): a `PilotType == PilotTypeLayer`
     branch beside the Miner branch decides when the ship is idle and undocked
     (`LayerAsteroidTarget is null && StationId is null`).
   - take-cover idle gate extended to require `LayerAsteroidTarget is null`
     (`Ship.cs:2917`) so a Layer's just-chosen rock isn't instantly preempted,
     exactly as a Miner's isn't.
   - `Ship.ApplyNpcAutopilot` (`Ship.cs:2396-2404`): a third goal-resolution
     branch steers toward `LayerAsteroidTarget` through the **identical**
     same-cluster / `CrossClusterWarpGoal` split P157/P158 built — no new
     steering logic.

## Applied simplifications vs native (each stated, not silently dropped)

- **Own-purpose contention count in place of native's `>= c_cidMine`
  command-based count.** Native counts NPCs whose *accepted* command target is
  the asteroid with a command id `>= c_cidMine` (`common.cpp:591-605`). This
  port has no `CidMine`/`CidBuild` command constants (the vocabulary is still
  `CidGoto`/`CidDoNothing`/`CidNone`, `Wire/OrderChange.cs`) and nothing ever
  sets a ship's `AcceptedTarget` to an asteroid with a mine-class command —
  so the contention datum native measures has **no representation here to
  count**. The closest faithful analogue, applied: count same-side NPCs (any
  sector) whose `LayerAsteroidTarget` is the same asteroid reference — other
  Layers already committed to the same rock for the same purpose
  (`Ship.cs:1047-1051, 1082-1085`). The deciding ship is idle here (its own
  slot is null), so it contributes no contender to any count. Same-side only
  — this port has no alliance system, matching the P149/P156-friendly-filter
  precedent (`common.cpp:596`'s `|| AlliedSides` collapses to `==`).

- **Idle-undocked gate in place of native's `bDocked` gate.** Native breaks
  out of the Layer case unless `bDocked` (`shipIGC.h:2419-2420`) — Layer
  ships there are spawned docked and get their first order on launch. This
  port's `Server.SpawnNpcShip` places ships flying directly in a sector (no
  docking concept at spawn), so the already-established port convention — the
  Miner cycle decides when idle and undocked (`DecideMiningOrSelling`'s
  `MiningAsteroidTarget is null && StationId is null` gate) — is followed:
  decide when `LayerAsteroidTarget is null && StationId is null`. Deliberate,
  consistent deviation, not silently dropped (`Ship.cs:2642-2673` remarks).

- **First-found tie-break in place of native's nearer-on-tie.** See above;
  consistent with P159's precedent and the BFS-orders-own-sector-first shape.

## Known, accepted side-effect — `Sector.ResolveMining` and a Layer's Ore

Confirmed directly (`Sector.cs:2098-2153`): `ResolveMining` triggers on **any**
ship's geometric proximity to a `CanMineHe3` asteroid with near-zero velocity,
gated only on `Dead`, `Mission is null`, `Ore >= MineCapacity`, and
`StationId is not null` — **no `PilotType` check at all**
(`Sector.cs:2109-2126`). This is the pre-existing, previously-accepted
simplification (this port's mining has always been "automatic for any ship
physically present," not gated on an explicit mining-intent flag the way
native's `miningMaskIGC` bit is). Consequence for this task, stated plainly: a
Layer NPC that parks near a minable He3 rock will incidentally accrue `Ore`
too, exactly like any other ship. This is **harmless** — nothing reads a
Layer's `Ore` for anything (the sell path and `DecideMiningOrSelling` are
Miner-gated). Gating `ResolveMining` on pilot type is **explicitly out of
scope** here and was not attempted.

## Explicit exclusions (out of scope)

- **No "lay a mine/probe" mechanic.** Native's `SetBaseData`
  (`shipIGC.h:2050-2065`) ties a Layer to a deployable `IbuildingEffectIGC`
  object via `GetDefaultOrder` (`shipIGC.h:2092-2097`, `c_cidBuild` at a
  buoy/warp) — a *different* code path from the `PickDefaultOrder` case this
  task ports. This task only makes an idle Layer travel to a low-contention
  asteroid; it does not deploy anything once there, matching native's own
  bounded default-order behavior. Real mine/probe-laying is a separate, later
  mechanic requiring a new deployable-object system this port doesn't have.
- **No `ResolveMining` `PilotType` gating** (see the side-effect finding).
- **No Wingman or Carrier behavior.** Carrier needs none (native has no
  `PickDefaultOrder` case for it — falls through to the `fGaveOrder=false`
  stand-by, `shipIGC.h:2458-2459`, which P160 already provides for free);
  Wingman needs its own combat-attack AI subsystem and `c_cidAttack`, a
  command this port's vocabulary lacks. Both are separate, later tasks.
- **No change to P103-P160 code** beyond the new `LayerAsteroidTarget` slot,
  the new least-targeted search, and the `OnUpdate`/`ApplyNpcAutopilot`
  wiring.

## Verification

- **Live loopback tests** (`tests/Allegiance.Core.Tests/NpcLayerDefaultOrderLiveTests.cs`,
  booted in-process server + seated bot, no VM — harness identical to P160/P159):
  - `Second_idle_layer_picks_the_less_targeted_asteroid_and_arrives` — two
    same-side idle Layers, two neutral asteroids A and B. The first claims A
    (both uncontended → first-found wins); the second, seeing A already
    claimed by the first Layer's `LayerAsteroidTarget`, picks **B** (fewer
    contenders) and **actually flies to it** (position within arrival band of
    B after ticking, measurably closer than spawn; stays committed to B, no
    churn/re-pick). Also asserts a Layer never writes the Miner slot.
  - `Miner_and_layer_on_the_same_side_never_interfere` — a Miner and a Layer
    on the same side each populate ONLY their own slot
    (`MiningAsteroidTarget` vs `LayerAsteroidTarget`), never the other's — the
    two decision cycles run independently (small assertion, not a second
    class).
- **Full existing suite** — `2785/2785` passing (2783 pre-P161 + 2 new).
- **3 consecutive clean full runs** confirmed (all `2785`). Two intermediate
  full runs each fired a single **documented** flake —
  `LiveInSpaceEjectTests.Already_a_lifepod_is_truly_killed_not_re_ejected` and
  `LiveSuicideShipDeleteTests.Combat_death_produces_ship_delete_to_same_audience`
  — both **passed in isolation** (known flakes, not regressions; unrelated to
  this change).
- Focused sibling-cycle set (P160 gate + miner-cycle + contention + any-cluster
  + mining-sell + new Layer) — `20/20` passing.
- Session stats: `python3 ~/Development/allegiance-tools/session_stats.py <session_id>`.

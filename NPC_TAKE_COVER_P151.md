# NPC_TAKE_COVER_P151 — NPC self-preservation: flee to safety when damaged (ORDER_CHANGE Stage 5)

Task: port the one small, self-contained "take cover at a local station if too
damaged" sub-block of native `PickDefaultOrder`'s `c_ptMiner` case — the first
genuinely autonomous (no player command needed) NPC behavior in this port.
Landed on `master`, on top of `6b8f334` (P150).

The sub-block is native `src/Igc/shipIGC.h:2230-2247`, its trigger is the
per-tick block `src/Igc/shipIGC.cpp:1934-1945`, and its hiding-station search
is `GetLocalStationToHide`, `shipIGC.h:2181-2212`.

---

## 1. What was ported

Four additive pieces (no change to any P103-P150 logic beyond the two new
constants, the new slot, the two new methods, and the `OnUpdate` hook):

1. **`DataStationTypeIGC.SabmLand = 0x20`** (`DataStationTypeIGC.cs`) — the
   station-ability bit `c_sabmLand = 0x20` (`src/Igc/igc.h:749`), added
   alongside `SabmRestart`/`SabmRipcord`/`SabmPedestal` in the same one-line
   style. `SabmRipcord = 0x08` already existed (`DataStationTypeIGC.cs:79`).
2. **`Ship.PlanTarget` / `Ship.PlanCommandId`** (`Ship.cs`) — the third and
   final command slot `c_cmdPlan` (`m_commandTargets[c_cmdPlan]`,
   `shipIGC.h:2631`), typed `DataStationIGC?` + `short` default
   `OrderChange.CidNone`, matching `AcceptedTarget`/`AcceptedCommandId`'s exact
   shape (P150). Also added **`OrderChange.CidGoto = 5`** (`OrderChange.cs`) —
   the `c_cidGoto` action the branch stores (`Wopr/Wopr/Constants/
   CommandID.cs:19`, `igc.h:903`), the sibling of the existing `CidNone`/
   `CidDoNothing`.
3. **`Ship.TryFindHidingStation()`** (`Ship.cs`) — the `GetLocalStationToHide`
   port: `FindNearestFriendlyStation(SabmLand)`, then if null **and**
   `ShieldFraction < 0.8f` try `FindNearestFriendlyStation(SabmRipcord)`
   (`shipIGC.h:2182-2191`). Both searches are the P149 primitive, same-cluster
   only (`c_ttFriendly|c_ttStation|c_ttNearest`, no `c_ttAnyCluster`).
4. **The take-cover trigger** in `Ship.OnUpdate` (`Ship.cs`) — wired into the
   existing per-tick hook that already runs every sector-resident ship
   (confirmed by P147), with the `Accepted`-to-`Plan` promotion (below) and the
   `IsNpc` gate (P148): real player ships never evaluate this. The damage
   decision itself is `Ship.TryTakeCover()` (`Ship.cs`).

`Ship.Fraction`/`Ship.ShieldFraction` (`Ship.cs:105,112`) are reused directly —
the native `ExportFractions`/`CompactShipFractions` roundtrip
(`shipIGC.h:2231-2232`) is this port's own already-decoded float fields, so no
new export/compaction step was added.

This does **not** touch `ORDER_CHANGE` at all — no new wire message, no
dispatch change. Pure server-side ambient behavior driven by the ship's own
tick, exactly as native runs `PickDefaultOrder` from inside `CshipIGC::Update`
(the trigger `shipIGC.cpp:1934-1945`).

## 2. The simplified legality check — stated plainly

Native's per-tick trigger first asks `LegalCommand(m_commandIDs[c_cmdPlan],
m_commandTargets[c_cmdPlan])`. That 2-arg overload
(`shipIGC.h:1604-1617+`) branches on pilot type and command id against
building-state, buoy types, mission ownership, and friend/enemy-of-target
(extending the 1-arg `LegalCommand`, `shipIGC.h:1559-1602`). This port's
command vocabulary is currently only `c_cidGoto`(5)/`c_cidDoNothing`(-3)/
`c_cidNone`(-1), and it has no `PilotType` enum — only the `IsNpc` bool marker
(P148). Porting the full pilot-type-branching switch is therefore dead weight.
**The port's legality check is: "the `Plan` slot is valid iff
`PlanTarget != null`."** This is a stated, accepted simplification, documented
in the `PlanTarget` property's XML comment and in `OnUpdate`, not silently
narrowed.

## 3. The Accepted-to-Plan promotion — INCLUDED

Native's trigger (`shipIGC.cpp:1934-1945`) promotes a legal `Accepted` command
into `Plan` before falling back to `PickDefaultOrder`:
`SetCommand(c_cmdPlan, m_commandTargets[c_cmdAccepted], m_commandIDs[c_cmdAccepted])`
(`shipIGC.cpp:1939`). This was included rather than deferred because it is a
genuinely trivial addition given the simplified legality check (`Target != null`)
and because omitting it would be **behaviorally wrong**: right after a P150
"return to base" order sets `AcceptedTarget`, the very next tick would otherwise
find `Plan` empty and let take-cover immediately override the fresh order with a
different hiding station. The port's `OnUpdate` therefore mirrors the native
control flow exactly:

```
if (PlanTarget is null) {                       // !LegalCommand(Plan), shipIGC.cpp:1934
    if (AcceptedTarget is { } accepted) {       // LegalCommand(Accepted), shipIGC.cpp:1936
        PlanTarget = accepted;                  // SetCommand(c_cmdPlan, ...), shipIGC.cpp:1939
        PlanCommandId = AcceptedCommandId;
    }
    else if (IsNpc) {
        TryTakeCover();                         // PickDefaultOrder, shipIGC.cpp:1943
    }
}
```

The promotion applies to the port's only ever-set Accepted producer (P150's
return-to-base on an NPC), so it effectively only fires for NPCs here anyway;
it is not additionally gated on `IsNpc` to match native's
not-pilot-typed-gated placement. Covered by a dedicated test.

## 4. The secret-ripcord-location buoy substitution — EXCLUDED

Native `GetLocalStationToHide`, on finding a ripcord-capable station, walks all
sides checking `SeenBySide` and, if any side doesn't know the teleport's real
location, substitutes a fake waypoint buoy positioned 2000 units in a random
direction (`shipIGC.h:2192-2210`). This port has no fog-of-war/`SeenBySide`
visibility model that substitution depends on meaningfully
(`SeenBySide` exists on `Model` but is populated only by the P132 visibility
sweep; the "protect the secret location" mechanic has no gameplay consequence
here). **Accepted simplification:** a ripcord-capable station is used directly
at its real position, no buoy created, no known/unknown-to-enemies check.
Documented in `TryFindHidingStation`'s remarks.

## 5. Explicit exclusions (accepted, documented — out of scope)

Everything else in `PickDefaultOrder`'s miner case (`shipIGC.h:2227-2387`), the
confirmation of which drove this task's narrow scoping:

- **Ore mining / cargo / payday economics** — `m_fOre`, capacity scaling,
  asteroid targeting, `c_cidMine`, unloading, teleport-refinery payday
  (`shipIGC.h:2248-2352`, `shipIGC.cpp:1947-1998`).
- **Cross-cluster warp pathfinding** and the "don't go back to the previous
  sector" logic (`shipIGC.h:2354-2380`) — and `FindTarget`'s `c_ttAnyCluster`
  warp-graph BFS (deferred by P149).
- **The queued-command/`c_cidJoin` handling** and `c_cidDefault` re-marking
  (`shipIGC.h:2252-2276, 2382-2384`).
- **Buoy-based waypoints** (`c_buoyWaypoint`, `shipIGC.h:2369-2377`).
- **`FindTarget`'s least-targeted / cowardly / front / previous modes**
  (`common.cpp`, deferred by P149).
- **Any other `PickDefaultOrder` pilot-type case** — builder/layer/wingman/
  carrier (`shipIGC.h:2388-2456`).
- No change to P103-P150 code beyond the two constants, the Plan slot, the two
  new methods, and the `OnUpdate` hook.

## 6. Test

`tests/Allegiance.Core.Tests/NPCTakeCoverTests.cs` — pure-domain (no sockets),
driving the real per-tick hook through the public `Model.Update` entry (which
runs the protected `Ship.OnUpdate`), so the `IsNpc` gate and simplified
legality check are exercised as they actually run. Seven `[Fact]`s:

1. damaged NPC (fraction 0.4, no shield) → `PlanTarget` = nearest friendly
   land-capable station, `PlanCommandId` = `CidGoto`;
2. undamaged NPC → `PlanTarget` stays null (the `GetFraction() < 1.0f` gate);
3. docked NPC (badly damaged) → `PlanTarget` stays null (the `!bDocked` gate);
4. damaged **real player** ship (`IsNpc=false`) → `PlanTarget` stays null (the
   `IsNpc` gate);
5. valid `Accepted` return-to-base order → promoted into `Plan`, NOT overridden
   by take-cover (the promotion branch wins);
6. damaged NPC, no qualifying station in sector → `PlanTarget` stays null
   (the `if (pstationToHide)` gate, `shipIGC.h:2239`);
7. no land station but a friendly ripcord station + shield < 0.8 → hides at the
   ripcord station (the `TryFindHidingStation` fallback, `shipIGC.h:2185-2191`).

Station **types** are real records pulled data-driven from the `PCore001.igc`
static core (a land-capable type and a ripcord-capable type, both asserted to
exist), so the ability-bit branches run against genuine capability masks.
Hull/shield fractions are set via reflection on the private
`_fraction`/`_shieldFraction` fields — the established pattern
`MissileCombatTests.cs:350` uses. Ships sit in the P111 no-station fly sector
1052 so the only stations in play are the injected ones. Unit-level is the
honest bar here (no wire message, no client — P149's "no live test needed for
pure logic" precedent).

## 7. Verification

- New test class: 7/7 passed in isolation.
- Full suite: see the run logs recorded during the 3-consecutive-clean-run
  discipline below. Baseline going in was 2737 (P150 checkpoint); this task
  adds 7.
- Five documented flakes (`LiveChatSectorWireTests`, `LiveCombatWireTests`,
  `LiveInSpaceEjectTests.Already_a_lifepod_is_truly_killed_not_re_ejected`,
  `LiveSuicideShipDeleteTests.Combat_death_produces_ship_delete_to_same_audience`,
  `ShipsUpdateLiveTests.Locked_target_fills_first_heavy_slot_with_real_fractions`)
  are rerun-if-fired, not regressions — the diff is additive-only and touches
  none of their code. A concurrent Windows build VM has been correlated with an
  elevated flake rate this session, so a higher-than-usual retry count is
  expected.
- Every claim above cites the exact native `shipIGC.h`/`shipIGC.cpp`/`igc.h`
  lines read this session.
- No VM, no live loopback test required (state plainly): pure per-tick logic.
- Committed only after build + test clean on 3 consecutive runs.

## Files changed

- `src/Allegiance.Core/Data/DataStationTypeIGC.cs` — `SabmLand = 0x20`.
- `src/Allegiance.Core/Wire/OrderChange.cs` — `CidGoto = 5`.
- `src/Allegiance.Core/Sim/Ship.cs` — `PlanTarget`/`PlanCommandId` slot pair,
  `TryFindHidingStation`, `TryTakeCover`, the `OnUpdate` take-cover/promotion
  hook.
- `tests/Allegiance.Core.Tests/NPCTakeCoverTests.cs` — new test class.
- `NPC_TAKE_COVER_P151.md` — this writeup.

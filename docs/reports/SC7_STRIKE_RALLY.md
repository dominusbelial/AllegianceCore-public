# SC-7 — Research commander: strike rally with goto, focused station attacks

Author: DeepSeek V4 Pro (one-shot delegation) · master `ac4527f5`

## Why this task existed (rig batch 10, master e1c528b2, 2026-10-20)

The 107v107 research showcase showed two gaps, both in the bot commander's tactics
policy, not the server:

1. **order-goto was 0 for both sides in every match** while order-attack sat at
   ~6000 / side. The server counter and goto legality were already wired and native
   (`RecordCommanderOrder` in `Server.ShipCommands.cs:1701-1712`; goto legality
   `ShipCommands.cs:1750-1752` ↔ `shipIGC.h:1631-1636`). The bottleneck was that the
   policy never sent a goto for a strike: `ResearchTactics.Tick` built only
   `CidRepair`/`CidAttack`, and the bots acted on goto only toward a friendly station.
2. **Side 1 (Ga'Taraan) killed no stations** in two full matches. Its anti-base hits
   landed 1–2 minutes apart, so shields/nanite recovered between hits (one AB1 hit
   ≈ 50% shield; a kill needs ~6 hits in a short window). Root causes: the strike
   target was picked as "lowest station id in the pilot's sector" (so the toughest
   hull — the Garrison — was pounded 12× while Supremacy ate 13× and Tactical 3×, none
   finished), and there was **no concentration** — every qualifying strike pilot got
   its own order; nothing grouped bombers.

Native has no AI commander, so this is a design choice modelled on how human
commanders play (rally at the aleph, go in together, focus one station). That is
stated explicitly in the Native-intent section (footer rule).

## What changed

All changes are in `tools/allegiance-mcp/` (the showcase commander policy and the
bot order-execution law). Nothing in `src/Allegiance.Server` or `src/Allegiance.Core`
changed, so the AOT-publish gate in the LIGHT GATE is not triggered.

### 1. Strike rally with goto — `ResearchTactics.cs`
`Tick` now maintains a small strike-wave state machine (race-agnostic, no
civ-specific branches):

- **Shared target** (`_strikeTarget`, a station id) chosen once per wave by
  `PickStrikeStation` and **kept stable across ticks** until it dies or disappears.
- **Rally point** (`_rally`) chosen by `ChooseRally`: the aleph (warp) whose partner
  leads INTO the target's sector, on our side of the border (preferring an aleph in a
  sector we control, i.e. one holding a friendly station); fall back to any aleph
  into the target sector, then to a friendly station on the border. When no aleph
  exists the wave attacks directly.
- **Gather quorum:** while fewer than `StrikeGroupSize` strike pilots are within
  `RallyGatherRadius` of the rally, each strike pilot is sent `CidGoto` to it. When
  ≥ `StrikeGroupSize` are gathered, the whole group is sent `CidAttack` on the ONE
  shared station (an already-on-target bomber attacks immediately rather than
  doubling back). The 2-orders-per-tick cap and the per-pilot 15s-same / 2s-different
  throttle are respected unchanged.

Constants (tuning surfaces for the rig, justified below):
- `StrikeGroupSize = 3`
- `RallyGatherRadius = 3600` (~3 km)
- `StrikeOnTargetRadius = 2400`

### 2. Bots act on goto — `BotManager.Orders.cs`
`DriveCommanderOrder` gained an explicit `CidGoto` branch dispatching by target type
(keeping the repair/defend/attack branches intact):

- **Warp (aleph):** fly to the mouth and **HOLD just short of the transit sphere**
  (`GotoWarpHoldArrival/Brake`) — never `FlyWarpLeg` through it, so the group bunches
  on our side of the aleph instead of crossing before the strike is ready.
- **Friendly ship:** **escort/follow** — `FollowFriendlyShip` re-reads the friend's
  live pose each tick at a soft standoff, never attacking.
- **Enemy ship:** preserved the established **P431 "engage that ship"** action
  (the port keys on `TargetType == Ship`; see the regression note below).
- **Friendly station:** unchanged (existing dock/approach).

### 3. Target choice — `ResearchTactics.PickStrikeStation`
Preference order (race-agnostic):
1. already-**damaged** enemy stations first (`Target.Damaged`);
2. among candidates, **non-Garrison** by distance from the strike group's centroid;
3. the **Garrison** by distance, only when no other enemy station exists.

### 4. Observation — `BotManager.ResearchTactics.cs`
`ObserveTacticalTargets` now also emits **warps (alephs)** as targets (with their
far-side sector via the partner warp's cluster) so the policy can pick the rally
aleph, and flags each station's **Garrison** via station-type class 0. Support-pilot
`CidGoto`-to-threatened-friendly-station (task item 4, optional) was not added —
friendly-station goto already works and is exercised by the bot law; keeping scope
tight.

## Constant justifications
- **`StrikeGroupSize = 3`:** rig data — one AB hit strips ~50% of a shield and a kill
  needs ~6 hits in a short window; a solo or duo strike never finishes a fresh hull.
  Three is the smallest group whose concentrated fire reliably lands a kill, and it
  fits the 2-orders-per-tick cap (a 3-group commits in ≤ 2 ticks).
- **`RallyGatherRadius = 3600` (~3 km):** matches the native defence *release*
  horizon — a chased enemy is abandoned beyond 3500 (`shipIGC.cpp:2404`) — so a
  "gathered" group is genuinely bunched at the aleph mouth, not scattered across half
  a sector.
- **`StrikeOnTargetRadius = 2400`:** a bomber effectively at the station shortcuts
  straight to attack; clearly closer than the rally radius so only a real on-target
  pilot skips the rally.

## Regression caught during verification (P431)
`BotAcceptsAndEngagesOrderLiveTests.ScenarioBot_AutoAcceptsAndEngagesTheSpecificCommandedTarget`
uses `CidGoto` as its representative "engage that ship" order (the port keys on
`TargetType == Ship` for any command id). My first goto branch swallowed it into
"follow". Fixed: goto to an **enemy** ship keeps the established focus-fire engage
path; only goto to a **friendly** ship escorts. The test passes 3/3 in isolation and
the full fast tier went green.

## Native behaviour classified

| Behaviour | Native cite | Class | What the port does |
|---|---|---|---|
| Goto legality (player/wingman to anything; station only if friendly) | `shipIGC.h:1631-1636` | design | Kept; bot honours goto by target kind |
| No AI commander | — | design (gap) | Modelled on how humans play: rally, go in together, focus one station |
| Defence acquire/release 1500/3500 | `shipIGC.cpp:2404` | design | Reused as the rally gather radius justification |
| Station class ⇒ GetClassID returns m_data.classID | `stationtypeigc.h` `GetClassID()`; `igc.h:2666 classID` | design (data) | Garrison = class 0 (verified across the 10-civ showcase race matrix), deprioritised |
| Queued order accepted ⇒ owns destination before autonomous orders | `shipIGC.cpp:1936-1939`, `shipIGC.h:2071-2161` | design | Retained in `DriveCommanderOrder` |

## Modern alternative considered
Native has no commander; a human issue is "rally then focus-fire". A modern RTS
solution would be a nav-mesh/flow-field plus a fire-team tasking aggregator. I kept a
**lightweight deterministic rally** instead: the bots already route through a real
`ClusterGraph` (`EnsureCombatPath`/`FindShortestPath`), so the marginal value of a
formation system is nil here, and the showcase need is *concentrating fire*, which the
shared-target state machine delivers with zero new navigation. Trade-off: no collision-
aware convoy spacing (bots already have P610 travel-spacing/aleah entry-stagger), and
the rally is a point, not a wedge — acceptable for bomber strikes. If a future task
wants parade-ground grouping, the constants above are the tuning surface.

## Verification (all numbers measured on this machine, not estimated)

- **Whole-solution build:** 0 warnings / 0 errors (ClientCore.Tests, ClientCore.MonoGame,
  Lobby, Explorer all `Build succeeded. 0 Warning(s) 0 Error(s)`).
- **New tests (`SC7CommanderOrdersTests`) 3× alone:** 3/3 pass on each of 3 runs
  (24 ms/run). Failing-first demonstrated: with the production changes stashed, the
  new tests fail to compile against the old `Target` record (no `Garrison`/
  `DestinationSector`), i.e. they are red before the fix.
- **One fast tier** (`tools/run-tests.sh --tier fast`): **9249 tests, PASS**
  (Core.Tests 4899, ClientCore.Tests 1549, MonoGame.Tests 2767+3, Lobby 19;
  quarantine 12). No failures, no slow-flag on the final run.
- **`P700ShowcaseLiveTests.Local_showcase_never_parks_with_target_for_five_seconds` 5×:**
  5/5 PASS (1 m 50 s – 1 m 53 s each). Bot targeting changed, so the load-sensitive
  check was run; base rate intact (no parks).
- **`SC6CommanderOrdersTests` + `SC6CommanderExecutionTests`:** 7 / 132 pass, no
  regressions from the `Tick` rewrite or the goto handling.

Because `src/Allegiance.Server` / `src/Allegiance.Core` were not modified, the AOT
publish gate and the 200v200 live-baseline harness were not re-run (the light gate
scopes both to server/Core changes).

**Local multi-station rally reproduction: not reproduced in this session.** A single
rally requires the research tree to reach a strike-bomber unlock plus several stations
built on asteroids — minutes of real-time sim at minimum, and the batch-10 events
showed the first strike only after ~10 minutes of a full 107v107 match. The local
in-process showcase **does** boot and run (the `never-parks` proof above exercised the
full showcase scenario and passed 5/5), and the rally policy is covered by the SC7
unit tests. The task's rig harness is the authoritative post-merge measurer for
order-goto ≥ 5/side, side-1 station kills, ≥ 3 AB hits on one station within 30 s,
skills ≥ 31/32, kills/min ≥ ~15, avg tick ~19 ms — deployed and measured by Claude
after this merge, per the "Explicitly out of scope" section.

## Notes
- **Side 1 low dock count (5–6 vs 43–52/match)** is a separate later question (per
  task scope, note-don't-fix). No cause in the tactics policy was identified here; the
  rally change routes strike bombers *past* friendly docks rather than into them,
  which could only reduce (not increase) that gap.
- The two full matches analysed lived on the rig master `e1c528b2`; the local master
  (`50830ab5` at start, now `ac4527f5`) carries the same `ResearchTactics`/`BotManager`
  policy lines the task cites.

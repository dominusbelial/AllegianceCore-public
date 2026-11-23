# P1077 — A constructor that never reaches its rock stalls a commander for the whole match

Status: complete. Implementation, tests, gate and report. No commits, pushes, package installs,
deployments or remote runs; all changes stay in the working tree.

## Verdict (updated as the investigation moved)

1. **Native keeps the trip.** A repeated identical BUILD to a constructor already travelling to that
   rock re-writes the plan slot, and `ResetWaypoint` rebuilds the *same* waypoint from the same target.
   `Waypoint` carries only `{target, objective}` (`Igc/igc.h:5201-5257`), so no travel progress can be
   lost; the only state a repeat resets is the per-order latch set (`Igc/shipIGC.h:755-763`). The 5 s
   re-send is **port-only**: the native commander is a human, and nothing in `FedSrv` re-sends drone
   orders (`FedSrv/FedSrv.CPP:3175-3325` runs once per `ORDER_CHANGE` chat).
2. **The brief's mechanism does not reproduce in isolation.** With the match-13 map regenerated exactly
   (`mapSeed=1932285663`, 2 teams, SingleRing, Ga'Taraan vs Iron Coalition), a `Constructor (Sml)`
   spawned at the side-1 home Garrison and ordered to BUILD asteroid 52 arrives, latches drilling and
   registers the building effect in **98-112 s (sim time) whether the order is re-sent every 5 s or sent
   once**. The port therefore already preserves this constructor's travel state across the repeat.
3. **No server change (Claude review).** The lane added an early return in `Ship.SetBuilderBuild` for a
   repeated identical BUILD. Review dropped it: the existing code already matches native. The builder
   target setter is a no-op for the same rock, and clearing the competing slots is what native's single
   plan write does. The early return would have kept a competing plan, for example the damaged drone's
   hide-at-station plan (`Igc/shipIGC.h:2230-2244`), which native replaces. The reproduction above
   (98-112 s with or without re-sends) already ran on the unchanged server. The test now pins this.
4. **The real protection is the commander safety net (port-only bot orchestration).** Whatever leaves a
   constructor parked short of its rock — an approach that cannot settle, a rock/geometry combination,
   or a lost order — a station step may no longer block the plan for the rest of the match. A step with
   no measurable progress toward its rock for 180 s releases the rock reservation and picks another
   compatible rock; with no other rock the step is deferred (logged) and the plan moves on. A step that
   has already registered a building effect is never abandoned.

## Item 1 — Native first

### What native does with a repeated identical BUILD

`CFSShip`/`CshipIGC` keep the command in four slots. `FedSrv::CommandToDrone` writes the **accepted**
slot for a drone (`FedSrv/FedSrv.CPP:3278-3285`, `SetCommand(c_cmdAccepted, pmodel, c_cidBuild)`), and
`CshipIGC::SetCommand` cascades accepted → current → plan for an autopiloted drone
(`Igc/shipIGC.h:768-776`). The plan write (`Igc/shipIGC.h:750-767`) does exactly two things:

* `ResetWaypoint()` (`Igc/shipIGC.cpp:3984-4160`). For `OT_asteroid` + `c_cidBuild` + `c_ptBuilder` it
  picks `Waypoint::c_oEnter` (`:4014-4016`) and calls `m_gotoplan.Set(o, pmodel)`
  (`:4151`). `GotoPlan::Set` (`Igc/igc.h:5324-5335`) stores the objective/target and resets only the
  *warp hop* (`m_wpWarp.Reset()`, `m_maskWaypoints = c_wpTarget`); `Waypoint::Set`
  (`Igc/igc.h:5235-5257`) replaces the target pointer. **Neither type holds travel progress** — position,
  velocity and orientation live on the ship, and the goto goal is recomputed from them every tick
  (`GotoPlan::SetControls`, `Igc/common.cpp:1830-2140`).
* the per-order latch reset for sub-player pilots (`Igc/shipIGC.h:755-763`): clear `drillingMaskIGC`,
  `m_fractionLastOrder = m_fraction`, `m_timeRanAway`, `m_bRunningAway = false`, `m_bGettingAmmo = false`,
  `m_targetFirstNotSeen = 0`, `m_checkCooldown = 0`.

So: **native keeps the trip.** The drilling bit is cleared, but the very next tick's gotoplan completion
sets it again (`Igc/shipIGC.cpp:2810-2817`) — the constructor's approach continues, and its collision
with the rock creates the building effect (`Igc/shipIGC.cpp:871-893`). The 10 s undock / launch logic
never re-runs either, because the accepted slot still names the same build
(`Igc/clusterIGC.cpp:116-140` gated on `OkToLaunch` or a goto/build accepted command).

The bot's 5 s cadence has **no native counterpart**. Native drones re-acquire only when their plan is
illegal or absent (`Igc/shipIGC.cpp:1934-1945` → `PickDefaultOrder`, `Igc/shipIGC.h:2388-2415` for
`c_ptBuilder`), and a human commander sends one order. The re-send loop is therefore
**port-only bot orchestration** (`tools/allegiance-mcp/ResearchCommander.cs`,
`world.Time - work.LastOrder >= 5`).

### Consequence for the port

Because native keeps the trip, the server must not reset a constructor's travel state when the same
rock is re-ordered. Item 3 makes that explicit for the port.

## Item 2 — Reproduction

### The map

`rotation.seed=927`, `mode=cycle`, three entries. The 13th match draws the 13th seed of
`new Random(927)`, **1932285663**, and `match % 3 == 0` selects the `SingleRing` entry — exactly the
logged `gen:SingleRing mapSeed=1932285663`. Regenerating with the production parameters
(`Teams=2`, `CivIds=[3,25]`, `MapSize=0`, the `MapGenParams` defaults the server uses) gives the logged
census: **4 sectors / 8 warps / 2 stations / 80 asteroids**.

| Fact | Value |
|---|---|
| Map | `gen:SingleRing`, `mapSeed=1932285663`, Ga'Taraan (civ 3, side 0) vs Iron Coalition (civ 25, side 1) |
| Side-1 home | sector 2 `Lemminakean`, Garrison (station type 115) at `(0, 0, 0)`, radius 423.5 |
| Asteroid 52 | `'2845'`, sector 2, position `(-2738.62, 2552.12, -180.41)`, radius **317**, `AabmCapabilities 0x8` (buildable), spin axis `(-0.296, 0.920, -0.257)`, ore 0 |
| Home → 52 | **3748 units** |
| Rocks near the straight line | `He8036` (r 174) 481 units of clearance at 1702 along; `He2753` (r 103) 785 clear at 2401; everything else ≥ 1200 clear. **Nothing physically blocks the line.** |
| Why 52 and not a nearer rock | the commander's snapshot orders rocks by `homeSector` first, then `AsteroidId` (`BotManager.Research.cs` `ResearchSnapshot`). In the home sector the buildable rocks are 52, 53, 54, 55, 56 — so 52 is the first compatible candidate even though 53/54 are closer. |

Reproducing the map is therefore exact, not an equivalent (`P1077ConstructorStallTests` pins the seed,
the census and asteroid 52's position/radius/capability).

### The test

`tests/Allegiance.Core.Tests/P1077ConstructorStallTests.cs`:

* assembles the generated mission from that seed and civ pair;
* spawns the constructor through the production bucket path (`CompleteStationType` → drone type 427,
  hull `Sm Std Constructor`, `Teleport (Hvy)` = station type 147 on the Iron Coalition side), which is
  the exact spawn the log records (ship 8453, "Constructor (Sml)", waits-for-build at the home Garrison);
* optionally docks it at the Garrison so the run also exercises the 10 s auto-undock (`RunNpcAutoLaunch`
  → `Station.Launch` bay geometry) that the log shows;
* orders BUILD at asteroid 52 through the production order path (`StartBuildIfLegal`), ticks the sector
  at the production 20 Hz cadence, and re-sends the same BUILD every 5 s when the variant asks for it;
* asserts arrival, the drilling latch and a registered building effect inside a bounded time.

Results (sim time, sector ticked at 0.05 s):

| Variant | Orders sent | Outcome |
|---|---|---|
| spawn flying, one order | 1 | building effect at **98 s** |
| spawn flying, re-send every 5 s | 18 | building effect at **98 s** |
| spawn docked → auto-undock at 10 s, one order at 12 s | 1 | building effect at **112 s** |
| spawn docked → auto-undock, re-send every 5 s | 20 | building effect at **112 s** |

**Conclusion: the re-send does not stall this constructor on this map.** The repeat is not sufficient
by itself. The port's `SetBuilderBuild` keeps the rock target on a repeat, because the target setter is a
no-op for the same object. The match-13 stall therefore needs a second factor that a static replay cannot recover from the
map alone (approach geometry/tick history/other traffic in the sector). The brief's own repair path —
item 4 — is what bounds that class of failure.

## Item 3 — Server: no change; the existing repeat handling is native

`Ship.SetBuilderBuild` on a repeated identical BUILD (no building effect registered) releases the P652c
idle reservation and clears `PlanTarget`, `GotoWaypoint` and the mining, layer, attack, defend and pickup
slots. `BuilderAsteroidTarget = rock` is a no-op for the same rock, so the trip target is kept. Native's
repeated order is a new plan write: the single `c_cmdPlan` slot names the rock again, `ResetWaypoint`
re-points the goto at it, and the per-order latches reset (`FedSrv/FedSrv.CPP:3278-3285`,
`Igc/shipIGC.h:750-776`, `Igc/shipIGC.cpp:4014-4016,4151`). Any competing plan is replaced either way.

The lane's first version returned early on the same rock and skipped those clears. Claude's review
dropped it, because it would keep a competing plan that native replaces: for example a damaged
constructor's hide-at-station plan (`Ship.cs` hide branch, native `Igc/shipIGC.h:2230-2244`). In native
the repeat sends that constructor back to the rock. `Repeated_identical_build_keeps_the_rock_and_replaces_a_competing_plan`
pins the native behaviour on the unchanged server.

## Item 4 — Commander safety net (port-only bot orchestration)

`tools/allegiance-mcp/ResearchCommander.cs`, station steps only:

* **Progress** = the constructor's distance to its reserved rock shrinking (by at least 1 unit), or the
  rock reporting `Building` (drilling/construction registered). Distances come from the wire positions
  the snapshot now carries (`Unit.Position`, `Rock.Position`).
* **No progress for 180 s** → release the rock reservation, exclude that rock for this step, and pick
  another compatible rock immediately (same tick); the next tick's BUILD retargets the constructor.
  *Limit justification:* the measured worst trip in the reproduction is 98-112 s (3748 units at the
  constructor's 60 u/s cruise, plus the launch and approach); 180 s is ≈1.6× that worst case, and short
  enough that a 60-minute match cannot be eaten by one step.
* **No other compatible rock** → log it, remember the step and advance past it; after
  `StationRetrySeconds` (300 s) the deferred station type is retried once through the existing
  "repair an unbuilt station" path (which already carries its own work/target bookkeeping) if it is
  still buyable.
* **Never abandoned:** a rock that reports `Building` never counts as stalled.
* Unknown positions (older/synthetic snapshots) cannot produce a progress signal, so the no-progress
  timer stays unarmed; a hard step cap of 600 s still prevents a forever-blocked step.

## Item 5 — Visibility

One line per safety-net firing, written to the server log (bot stdout, invariant-culture format), so a
`grep` on the VPS log counts them:

```
[constructor-stall] side=1 step=11/157 'Teleport (Hvy)' constructor=8453 rock=52 no-progress=181s -> released rock reservation, retargeted asteroid 53
[constructor-stall] side=1 step=11/157 'Teleport (Hvy)' constructor=8453 rock=52 no-progress=181s -> no other compatible rock; step deferred 300s (retry 1/1)
[constructor-stall] side=1 step=11/157 'Teleport (Hvy)' constructor=8453 rock=52 no-progress=181s -> retry already spent; step abandoned
```

`grep -c '\[constructor-stall\]' <bot log>` is the count; the side/step/constructor/rock/seconds
fields make each line attributable.

## Implementation notes

* **Wire positions in the snapshot.** `ResearchCommander.Unit`/`Rock` gained an optional `Position`
  (`BotManager.Research.cs` fills them from `ShipEntity.Latest.Position` and the asteroid export).
  Records keep their old constructor shapes (defaulted trailing parameters), so the recorded bot-decision
  corpus and existing fixtures are unaffected.
* **Progress clock.** The clock starts when the rock is reserved (`Work.TripAt`) so a snapshot cadence
  coarser than 180 s cannot postpone the check; the first pose only baselines the distance, and each
  later shrink of at least one unit re-arms the clock. A `Building` rock re-arms it unconditionally.
* **Retries.** `MaxStationRetries = 1`. A deferred station's retry goes through the existing
  "unbuilt prerequisite" path (its own `_repairs` work/target), so it does not disturb the plan index;
  when the budget is spent the station type is added to `_abandonedStations` and both the retry
  promotion and the unbuilt-prerequisite scan skip it, so a permanently unreachable step cannot keep
  re-reserving rocks or block the plan. Completion of the station clears all three books.
* **Server.** Unchanged (see item 3).

## Gate

The brief's 200v200 performance rig is explicitly **out of scope** ("Not in scope: any VPS, rig or
lane-bots change, deploy or run"), so tick cost was measured locally on the affected sim path at the
production 20 Hz cadence instead (see the last row below).

| Check | Result |
|---|---|
| `dotnet build AllegianceCore.slnx` (Debug, `--disable-build-servers`) | **0 warnings / 0 errors** |
| `tools/build-tools.sh` | **153/153 tool projects**, 0 warnings / 0 errors |
| New/affected tests, 3× alone with artwork | `P1077ConstructorStallTests` **3 × 7 passed** (0.54/0.53/0.52 s); `P1077CommanderStallTests` **3 × 5 passed** (0.072/0.071/0.072 s); constructor/build regression cohort (185 existing cases) **3 × 185 passed** (22 s each); research-commander cohort (`P703/P733/P942/P986/P987/P1001/P1077`) **82 passed / 2 skipped** (Live-gated) |
| One full Fast sweep | **16,040 results in 567.0 s: 16,032 passed / 1 failed / 7 not executed**; 9/9 infrastructure checks passed; timing gate failed on **2 pre-existing timing-only families** (`P814GunnerTests.P814b…`, `SC2NaniteSupportTests…(civ 25)`, 14.367 s / 14.630 s against 11.539 s), each **3/3 passed** isolated |
| Fast-sweep functional failure | `AsteroidMinedLiveTests.No_broadcast_fires_once_miner_hits_capacity` (queued-receipt signature ledgered under P1024/P1025/P1074) — **pass/fail/pass/pass/pass/pass** in six isolated runs; no mining path touched |
| Showcase never-parks, 5× serially | **4 passed / 1 failed** (4 m 18 s, 8 m 04 s, 2 m 55 s, 3 m 26 s, 6 m 24 s); the failure reached 19/30 at the 480 s arrival deadline with the parking bound at **1.921 s** and no `[constructor-stall]` line — the P939/P985-ledgered arrival-deadline shape (the base reproduces it too); that case **3/3 passed** isolated |
| Sim tick cost, local A/B (20,000 sector ticks, P1077 fixture, production 0.05 s step) | **4.164 µs/tick** without the repeat vs **3.171 µs/tick** with it (last of three warm passes; the two earlier passes were 26.8/5.2 and 8.1/3.8 µs including JIT warm-up, i.e. run-to-run noise — no measurable difference); `SetBuilderBuild` identical-repeat **0.048 µs/call** over 2,000,000 calls (an order-path call every 5 s, not per tick) |
| Flake ledger | `docs/TEST_FLAKES.md` P1077 section: sweep, isolated reruns, never-parks and the mining-receipt flake with six isolated runs |
| Parity / glossary | `scripts/parity-scorecard.py docs/parity/p1077-constructor-stall.csv` **3 rows validated, 100 %**; `tools/check-glossary-links.py` **1,128 paths OK** |

## Unfinished / limits

* **The match-13 trigger beyond the repeat is not identified.** The 5 s re-send does not stall this
  constructor on this map (both variants arrive in 98-112 s), so the live stall needed a second factor
  the log set cannot recover (approach/tick history, other traffic in the sector). The native-aligned
  repeat handling removes the port's one repeat-triggered reset; the safety net bounds the class of
  failure regardless of the trigger.
* **No VPS/rig measurement** (out of scope), so the "no measurable tick-cost change" claim rests on the
  local A/B above plus the fact that the changed server code is two reference comparisons on the order
  path, not the tick path.

## Native intent

* A repeated identical build order to a constructor already travelling to that rock keeps the trip:
  `Igc/igc.h:5324-5335`, `Igc/igc.h:5235-5257`, `Igc/shipIGC.h:750-767`,
  `Igc/shipIGC.cpp:4014-4016,4151`.
* The per-order latch reset (running-away, ammo, first-not-seen, cooldown, drilling bit) is the only
  state a repeat clears: `Igc/shipIGC.h:755-763`; the port mirrors it in `Ship.DronePlanChanged`.
* Drilling starts on gotoplan completion (`Igc/shipIGC.cpp:2810-2817`) and the building effect is created
  by the drilling builder's collision with its rock (`Igc/shipIGC.cpp:871-893`).
* Native drone orders are accepted per `ORDER_CHANGE` chat (`FedSrv/FedSrv.CPP:3175-3325`); no periodic
  re-send exists in native.

## Modern approach

* The 5 s BUILD re-send and the no-progress/timeout safety net are **port-only bot orchestration**
  (`tools/allegiance-mcp/ResearchCommander.cs`); they add no simulation rule.
* No server change. The port's existing handling of a repeated identical order already matches native's
  single-slot plan write (item 3).
* No VPS, rig or lane-bots change was made; no deployment or remote run.

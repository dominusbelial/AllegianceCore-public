# P536 — Drones, NPC AI & orders parity audit (P1–P200, part 6)

Read-only audit (part 6 of 8 of the P1–P200 parity pass). Scope: the commits
in `docs/audits/p1-p200/drones.txt` (28 commits) as they relate to **drone
spawn rules and counts, miner/builder/wingman decision logic, order/command
handling, autolaunch and dock timers, target selection, flee/cowardly
behaviour, and the timers/thresholds those use**. Native reference
`~/Development/Allegiance/src`, port `~/Development/AllegianceCore/src`.
`Igc/igc.h`/`shipIGC.h` are ISO-8859-encoded and were grepped with
`LC_ALL=C grep -a`.

Method: every row was verified by reading both sides' source, with file:line
cited. Items already reported by the 2026-09-29 parity pass
(`~/Documents/Domingo-Llm-Wiki/raw/2026-09-29-parity-{A-flight,E-team-meta}.md`)
are marked FIXED/OPEN against **current** code. The value-comparison lens from
the pass that caught the He3 100×/16× divergence (P528) was applied: constants,
formulas, cadences, gates and where each value comes from.

Net picture: the drone decision stack is far healthier than the pre-pass
worries implied — **drone production is no longer missing** (later tasks P458/
P459 made station-type and drone-type buckets actually spawn constructors and
real-hulled drones), SpawnNpcShip now gives each NPC its drone type's real hull
and pilot type (P458/P160), the fair-share asteroid contention and cross-side
lockout are faithfully ported (P159/P167), take-cover (P151) and ripcord-safety
(P172) match native thresholds/formulas exactly, and the wingman defend logic
(P166) reproduces native's 3500/2200/2500/1000/−30 constants. Five real
divergences remain, one with real gameplay weight:

**(1) the miner sell trigger — native flips to selling at half capacity
(`capacity/2`), the port waits until the bag is full (`capacity`)** — so port
miners hold and risk twice the ore native miners do; plus the related native
rules (unload-before-mine when crossing clusters with `ore > capacity·0.2`,
“reserve the rock” at `asteroid < capacity·0.5`) are absent. The rest are
lower: (2) miner rock/sell-station selection omits native's `c_ttCowardly`
(enemy-avoiding) and selection-time least-targeted preference; (3) the
continuous flee/runaway loop (`m_dtCheckRunaway`, wingman run@0.6/0.3-hull,
builder/layer flee, carriers never run) is missing — only idle take-cover
exists; (4) builder “completion” is a flat 40 s literal where native builds up
through the development system; (5) the initial-miner spawn bypasses native's
`bAllowDevelopments` + `CanBuy` gate (the count of 1 matches native's default).

---

## Findings

| item | native file:line | port file:line | verdict | severity | proposed fix (one line) |
|---|---|---|---|---|---|
| Miner **sell trigger** — mine while `ore < capacity`, sell when full | `Igc/shipIGC.h:2278` (`m_fOre < capacity/2.0f` mines; else sell/land `:2327-2346`) | `Sim/Ship.cs:5762` (`Ore < EffectiveMineCapacity()` mines; else sell `:5778`) | **DIVERGES** (native unloads at half, port at full) | **MED** | flip the `DecideMiningOrSelling` gate to `Ore < EffectiveMineCapacity()*0.5f` (and add the “< half → still mine else sell” split) |
| Miner “**reserve the rock**” threshold — park via `c_cmdPlan` while `asteroid ore ≥ capacity·0.5`, otherwise commit | `Igc/shipIGC.cpp:2226` (`pasteroid->GetOre() < capacity*0.5f`) | absent in `ResolveMining`/`DecideMiningOrSelling` (Ship.cs:5760-5795) | **DIVERGES / MISSING** (fold of #1) | LOW | when leaving to sell, set `c_cmdPlan` (return) when the rock still holds ≥ half a load, else clear |
| Miner **unload-before-mine cross-cluster** — `ore > capacity·0.2` triggers a detour to unload first | `Igc/shipIGC.h:2307` | absent (Ship.cs:5760-5795) | **DIVERGES / MISSING** (fold of #1) | LOW | before committing to a rock in another cluster, if `Ore > cap*0.2` route to a nearby unload station first |
| Miner **rock selection** — native uses `c_ttNearest \| c_ttLeastTargeted \| c_ttCowardly` | `Igc/shipIGC.h:2288, 2301` | `Ship.cs:1766-1787` `FindNearestMinableAsteroidIn` (nearest He3 only; no least-targeted, no cowardly) | **DIVERGES** (contention later handled dynamically by P159; selection-time cowardly absent) | MED | add a cowardly gate (skip rocks within an enemy-danger radius) and prefer lower-contention rocks at pick time |
| Miner **sell-station safety** — native first takes the `c_ttCowardly` (safe) unload station, else any; plus a `c_sabmLand` fallback | `Igc/shipIGC.h:2329-2340` | `Ship.cs:1710` `FindNearestFriendlyStationAnyCluster(SabmUnload)` — plain nearest, no safety tier, no Land fallback | **DIVERGES** | MED | add a cowardly-safe tier first (skip stations near enemies), then the plain tier, then a `SabmLand` fallback |
| **Continuous flee/runaway** — `m_dtCheckRunaway` (5/31/90), wingman run@0.6/0.3 hull, builder/layer flee while hurt, carriers never run | `Igc/shipIGC.cpp:1705-1800` (`PreplotShipMove`; `m_dtCheckRunaway` init `:186`, `:1909`, `:1726`) | only idle-only `TryTakeCover` `Ship.cs:2121-2139` (gated on fully idle); no continuous flee that can interrupt a plan | **MISSING** | MED | port the flee block: a damaged non-carrier interrupts its plan to run to a hide/repair station, respecting the hull%/behaviour gates |
| Wingman **combat manoeuvring** — `Dodge()`, skill tiers (0.75/0.75), `openFireDistPercentage=0.95`, `CanSee` sight gating + 3 s/5 s not-seen, investigate re-acquire | `Igc/shipIGC.cpp:2353-2787+` | `Ship.cs:5997-6098` `UpdateWingmanCombat` — leads the shot, fires within `EstimateWeaponRange()` + `WingmanFireFacingCosine=0.90` (`Ship.cs:4485`); no Dodge/skill-random/sight-gate | **STAND-IN** (deliberately documented, P162; P169 added lead) | LOW | (optional) port Dodge/skill tiers/sight-gating for full combat parity |
| Wingman **defend** logic — 3500 abandon / 2200 evaluate / 2500 scan / 1000 switch / −30 runaway / 1500 acquire / cooldown 200 | `Igc/shipIGC.cpp:2373-2407, 2689-2695` | `Ship.cs:4507-4512, 6145-6227` `UpdateWingmanDefend` (all constants MATCH) | MATCH | – | none |
| **Initial-miner spawn gate** — native spawns only when `bAllowDevelopments` and `pstation->CanBuy(pdt)` (and active side) | `FedSrv/fsmission.cpp:2196-2206` | `Server.Lifecycle.cs:542-579` `SpawnInitialMiners` (unconditional once a Miner drone type exists; count `DefaultInitialMinersPerTeam=1` `:493`) | **DIVERGES** (count MATCHES native default `igc.h:1330`; the develop/can-buy gate is absent) | LOW | gate the spawn on developments-allowed and the home station `CanBuy`ing the resolved miner type |
| **Drone production from buckets** — station-type → constructor, drone-type → real drone | `FedSrv/FedSrv.CPP:11343-11372, 11518-11546` | `Server.Economy.cs:663-682, 721-758, 873-929` (`CompleteStationType`/`CompleteDroneType`) | MATCH (**E-pass #1/#2 FIXED** by P458/P459) | – | none |
| **NPC hull/pilot type from drone type** (was lowest-HullId) | `FedSrv/FedSrv.CPP:2220-2231` | `Server.cs:827-841` `SpawnNpcShip` (uses `droneType.HullTypeId` + `PilotType`) | MATCH (**E-pass #3 FIXED** by P458/P160) | – | none (stale comment at `Lifecycle.cs:524-527` still claims lowest-HullId — update it) |
| Autolaunch **dock cooldown** (~10 s) | native auto-undock cadence | `Server.Docking.cs:2096` `NpcAutoLaunchCooldownSeconds=10.0`, `:2136-2159` | MATCH (approx) | LOW | none |
| Take-cover thresholds (shield 0 / shield<0.5&&hull<0.7 / hull<0.5; hide-station search) | `Igc/shipIGC.h:2230-2247` (`GetLocalStationToHide` `:2181-2212`) | `Ship.cs:2121-2139` `TryTakeCover`, `:2081-2090` `TryFindHidingStation` | MATCH (secret-location buoy substitution dropped — documented) | – | none |
| Ripcord **safety gate** `IsSafeToRipcord` (drift ∫, `tMax=1+s/v0` fudge, `state==0`) | `Igc/shipIGC.cpp:4200-4229` | `Ship.cs:5541-5556`, hazard scan `:5565-5601` | MATCH | – | none |
| Collision-avoidance **dodge** (P168/170) — shared `ShipDodgeLaw` | `Igc/common.cpp:1700-1790` `Dodge` | `Ship.cs:4940-4959` via `ShipDodgeLaw.TryComputeDodgeGoal` (P489 refactor), `DodgeGoalDistance=520` `:4451` | MATCH (structure; law abstracted) | – | none |
| ORDER_CHANGE command plumbing — stay-docked / launch / target (P147-154) | `FedSrv/FedSrv.CPP:7290-7302` | `Server.ShipCommands.cs:1098-1149` (+ P149/P151 helpers) | MATCH (transport/commands) | – | none |
| He3 mining **rate / capacity / price** constants (the 2026-09-29 100×/16× gap) | core `FloatConstants`/`c_gaMiningRate|MiningCapacity` | `Ship.cs:811-831` `EffectiveMiningRate`/`EffectiveMineCapacity` (loaded-core driven) | MATCH (**P528 FIXED** — already reported) | – | none |
| Builder **completion time** — flat literal vs native development-driven build-up | station construction accrues through the development/tech system (`Igc/stationIGC.cpp:273`) | `Sim/BuildingEffect.cs:61` `BuildDuration=40.0`; `Model/Sector.cs:363-392` `StartBuildingEffect` | **STAND-IN** (flat 40 s placeholder for the native build-up model) | MED | derive the completion cadence from the station type/development instead of a flat 40 s literal |

---

## Top fixes (ranked, sized as candidate P-tasks)

1. **Miner sell trigger → half capacity (MED, smallest slice).** Change
   `DecideMiningOrSelling` (`Ship.cs:5762`) so a miner with
   `Ore ≥ EffectiveMineCapacity()·0.5` stops picking new rocks and routes to
   sell/land, matching `shipIGC.h:2278`. One constant + the branch threshold +
   one cadence test. Highest gameplay weight (changes ore-retention cadence —
   the exact class of value divergence this pass exists to catch).

2. **Cowardly selection for miner rock + sell-station (MED).** In
   `FindNearestMinableAsteroidIn` and `FindNearestFriendlyStationAnyCluster`
   add an enemy-proximity `c_ttCowardly` rejection tier (skip a rock/station
   within a danger radius of a hostile), then a non-cowardly fallback for the
   station, mirroring `shipIGC.h:2288,2301,2329-2340`. Two predicates + reuse.

3. **Continuous flee/runaway loop (MED).** Port the `PreplotShipMove` flee
   block (`shipIGC.cpp:1705-1800`): a damaged non-carrier NPC interrupts its
   plan to run to a hide/repair station, with the wingman run@0.6/0.3-hull and
   builder/layer hurt-gates. Adds one per-tick branch + the `m_dtCheckRunaway`
   cooldown + tests.

4. **Builder completion cadence (MED, design decision for Domingo).**
   Replace the flat `BuildDuration=40.0` (`BuildingEffect.cs:61`) with the
   station/development-driven build-up, or document the 40 s as an accepted
   stand-in. Smallest slice: source it from a core field instead of a literal.

5. **Initial-miner spawn gate (LOW).** Gate `SpawnInitialMiners`
   (`Lifecycle.cs:542`) on developments-allowed and the home station
   `CanBuy`ing the resolved miner type, per `fsmission.cpp:2196-2206`; also fix
   the stale lowest-HullId comment (`Lifecycle.cs:524-527`).

---

## Coverage

All 28 commits in `docs/audits/p1-p200/drones.txt` were checked against
**current** source (as of `master`, ahead of P534):

**Deep (decision logic traced + values/formulas compared):**
- **P155** autopilot primitive, **P156** miner decision cycle,
  **P157/P158** cross-cluster movement + path-find widening,
  **P159/P167** fair-share contention + cross-side lockout,
  **P160** PilotType dispatch, **P161** layer default order,
  **P162/P169** wingman combat (lead + fire gate), **P165/P280** builder AI
  (incl. build gate + 40 s), **P166** wingman defend,
  **P151** take-cover, **P172** ripcord-safety, **P168/P170/P489** dodge.

**Medium (structure + event routing verified, values via shared/verified
paths):**
- **P148/P150/P152** ORDER_CHANGE stay-docked / return-base / launch arms,
  **P149** FIND_TARGET (nearest friendly station with an ability),
  **P153/P154** real-player command accepted paths,
  **P147** NPC existence + visibility, **P109/P140** turret binding/promote
  (command/role plumbing, combat damage out of slice).

**Brief (plumbing/render scaffolding, no drone-AI content):**
- **P19/P22/P24** live bot combat loop on the Bot class,
  **P35** warp+station export for navigation, **P37/P46** renderer-only.

Known 2026-09-29 E-pass items re-checked against current code:
**FIXED** — drone production from buckets (P458/P459), NPC hull/pilot type from
drone type (P458/P160). **PARTIAL/OPEN** — order vocabulary to drones (the port
still uses a Goto/DoNothing/None command set; later P403/P422/P425/P431/P469
add pieces), initial-miner count/gate (count matches native default but the
develop/can-buy gate is absent). **He3 mining constants**: already FIXED by
P528 (not re-reported as new).

No production or test code was modified; this report is the only change.

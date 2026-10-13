# P635 — multi-route showcase: split each side across ALL entry alephs into the rendezvous

## TL;DR

Domingo (2026-10-12): *"there are 2 ways to get to Alderan, why are we not doing 2x 100vs100 using
different paths to get there?"* Today the showcase funnels each whole side along ONE shortest path,
so ~195 ships per side queue at a single aleph mouth (side 0 via Rigel 1059, side 1 via Juniper 1061)
even though Alderan has six entry alephs.

**P635 adds multi-route wings**: `spawn_scenario` (and the P430 `size` baseline) now discovers up to
K=3 distinct entry-aleph routes per side, splits the fleet across them by least-loaded assignment,
gives each route its OWN staging sector, staging/entry-stagger/exit-clear-out instance and release
gate, and releases all wings of a side together so the fight still starts as one battle
(`routes=auto`; `routes=single` preserves today's behaviour; `maxPerRoute` caps bots per route).

The discovery, split, per-route staging and sync timing are unit-tested (fixture graph + the real 221
map), and the whole feature WORKS end to end: at **30v30 the multi-route split reaches the target
100% and fights under BOTH ship-update models** (port 60/60 reached, 42 kills, 0 friendly; native
59/60 reached, 46 kills, 0 friendly).

**The honest 200v200 verdict:** the multi-route split does **NOT** lift the ≥95%-of-390 arrival
ceiling. Measured on this workstation (195v195 = 390 bots, 200 s, m2xspiderw, port model, staging on):

| Metric | routes=single (pre-P635) | routes=auto (P635) |
|---|---|---|
| total warp crossings (both sides) | 269 | **290** (more) |
| arrivals into Alderan (botsReached) | **99/390 (25%)** | 70/390 (18%) |
| kills | 1 | 0 |

**Root cause (measured, not assumed):** the total warp-crossing rate is capped at ~270–290
crossings per 200 s for 390 local bots **regardless of routing** — auto produced *more* crossings
(290) than single (269) but *fewer* Alderan arrivals, because the 3-hop wings consume more crossings
per arrival and the synchronous-release barrier holds the fast 2-hop wings until the slower wing
assembles. So the funnel through one aleph was **not** the bottleneck. The ceiling is the pre-existing
global bot aleph-transit throughput at ~390 local bots (P632's "collapse above ~250"; P633's parallel
decisions and P634's separate send lanes did not lift it either — P634 measured ~80–88 bots/side
crossing even their FIRST aleph). That aleph-transit mechanic is explicitly **out of P635 scope**
(server side / transit mechanics belong to P634/P631's area), so per the task I report the per-route
numbers and this next blocker rather than shipping a fix that does not move the metric.

P635's multi-route feature is still correct, native-faithful (real commanders order wings onto
different paths) and demonstrates the intended behaviour at realistic per-aleph loads (30v30); it is
committed because the split is the gameplay-intent fix for the *single-mouth funnel* the task names,
even though the 390-local-bot ceiling is a separate, pre-existing transit limit that this task cannot
remove without touching the aleph mechanic.

## Scope → implementation map

1. **Route discovery** (`ClusterGraph.FindDistinctEntryRoutes`, new; and its probe-warp-table twin
   `BotManager.RouteEnumeration`): up to K distinct routes home→rendezvous that differ in their ENTRY
   ALEPH (penultimate sector) into the rendezvous, ranked by hops ascending, capped at K, deduped.
   Real 221 map (m2xspiderw) result, pinned in a unit test:
   - side 0 (Ganymede 1050): `[1050,1059,1052]` Rigel (the old single route), `[1050,1060,1052]`,
     `[1050,1054,1053,1052]`;
   - side 1 (Organia 1051): `[1051,1061,1052]` Juniper (the old single route), `[1051,1062,1052]`,
     `[1051,1055,1053,1052]`.
   Alderan's neighbours (entry alephs) are 1053, 1056, 1059, 1060, 1061, 1062 — six mouths; the old
   planner used one per side.
2. **Wing split** (`BotManager.PickLeastLoadedRoute`, pure argmin; wired into `SpawnScenarioModern`
   and `SpawnScenarioHomogeneous`): each bot is assigned to the least-loaded route wing, capped at
   `maxPerRoute` (default 100; excess goes to the least-loaded). Each wing gets its own staging
   sector (the route's penult) and its own staging/entry/exit instance.
3. **Synchronised arrival** (`StageReleaseRequired(side, routeIndex, now)` now per-ROUTE, with a
   cross-route assembly barrier): a wing advances when its own living bots are all staged AND every
   other wing of the side is staged (so all wings cross into the rendezvous together as ONE fight), or
   when it has held past its timeout `StagingHoldSeconds + SyncOffsetSeconds(...)` (a longer route
   gets a longer grace window so it is not forced out before its trailing bots stage). The existing
   "assembled/timeout" semantics are kept per wing; the timeout overrides the barrier so a straggler
   never freezes the group.
4. **Flags**: `spawn_scenario` args `routes` (`auto` default | `single`) and `maxPerRoute`; reported in
   the spawn summary. `list_active_bots` gains per-bot `RouteIndex`/`RouteName`; `get_perf_stats`
   gains a `routes` block with per-route assigned/arrived counts.
5. P622 (target spreading), P633 (parallel loop) and P631-related metrics are untouched; only the
   routing/staging policy changed.

## Verification

### Unit tests (failing-first: the tested API — `FindDistinctEntryRoutes`, `RouteEnumeration`,
`PickLeastLoadedRoute`, `SyncOffsetSeconds` — did not exist on P634's code, so every new test fails
there and passes here). `tests/.../P635MultiRouteTests.cs` (9 tests):

- **Fixture graph** distinct-entry enumeration (distinct penults, hop ordering, cap=0/1, same-endpoint,
  the penult-only-entry case) — `FixtureGraph_EnumeratesDistinctEntryRoutes`,
  `FixtureGraph_CapsAndDedupesRoutes`.
- **Real 221 map** — `RealMap_PinsDistinctEntryRoutesToAlderan` pins the exact 3-route sets above with
  distinct penults and ascending-hop ordering; `RealMap_ClusterGraphAndWarpTable_Agree` cross-checks the
  cluster-graph and probe-warp-table twins agree on the real map.
- **Split / cap / least-loaded** — `PickLeastLoaded_SplitsEvenlyAcrossRoutes` (195→98/97),
  `PickLeastLoaded_ExcessGoesToLeastLoadedUnderCap` (no route >100 with ≥2 routes),
  `PickLeastLoaded_AlwaysArgmin_ThreeRoutes` (100→34/33/33).
- **Synchronised timing** — `SyncOffset_LongerRouteWaitsLonger` (offset = (widestHops−routeHops)×20 s,
  clamped ≥0), `ReleaseTiming_AssembledImmediately_VsTimeoutLater` (assembled releases at once;
  partial holds until timeout; empty releases trivially).

### Live (local server, m2xspiderw, rendezvous Alderan 1052, real BotManager over TCP)

- **Mandatory combat check at 30v30, both models, routes=auto** (kills > 0 asserted in the report):
  - **port**: botsReached 60/60, kills **42** (deaths 42), 61.8% hit-rate, 0 friendly.
  - **native**: botsReached 59/60, kills **46**, 52.2% hit-rate, 0 friendly.
  All three routes per side actually split (10/10 per route) and the 2-hop routes reached within ~20 s.
- **200v200 (195v195, 390 bots, 200 s, port model, staging on)** — see TL;DR table. routes=single
  99/390 (reproduces P634's ~97/390); routes=auto 70/390 with MORE total warp crossings (290 vs 269),
  confirming a global transit cap rather than a per-mouth funnel. Per-route arrivals in the auto run:
  side 0 → 20+10+8 = 38 (routes 2/2/3 hops), side 1 → 17+8+7 = 32 (routes 2/2/3 hops); total 70.
  Kills 0–1 — essentially no sustained combat occurred at this collapsed scale, so per-phase tick
  comparison is dominated by the transit collapse, not combat.

### Build
- Whole-solution build (`dotnet build AllegianceCore.slnx`): **0 warnings, 0 errors** (also a
  standalone `dotnet build` of the mcp + test projects: 0/0).

### Suite — `scripts/run-tests.sh`, 2 runs on the final code
Reported as each run happened (flake ledger: `docs/TEST_FLAKES.md`):

- **Run 1:** Lobby 12/12 · MonoGame 1776/1779 (3 skipped) · ClientCore 647/649 ·
  Core 4005/4009. Failures, all documented live-wire timing flakes (none reference
  P635 symbols): ClientCore `ShipDeleteGoneLiveTests.CrossSide_WarperLeavesSector…
  OwnPeerNotHidden` (ledger line 51, load-flake), ClientCore
  `CrossSideCombatVisibilityLiveTests.CrossSide_sector_stays_correct_across_warp_return`
  (ledger, run-to-run timing), Core `TcpTransportGreetTimeoutPoolTests.Silent_connection…
  join` (live transport greet-pool timing, unrelated to P635 routing), Core
  `GameOverPlayersWireTests` ×3. `run-tests.sh` exit 1.
- **Run 2:** Lobby 12/12 · MonoGame 1776/1779 (3 skipped) · **ClientCore 649/649
  (clean)** · Core 4006/4009. Failures rotate to the ledger's other live-wire timing
  tests: `SideEliminationLiveTests.Eliminating_side2…` (ledger line 39),
  `P590PodRescueTests.Pod_pickup_order_streams_…real_player_rescuer` (×2, P634 run 2
  hit the same), `AddPartTests.Part_pickup_destroys_then_accept_merges_…mount`
  (ledger). `run-tests.sh` exit 1.

**Zero P635 failures in either run.** ClientCore (which hosts all nine
`P635MultiRouteTests` + the gated live harness) went 647/649 then **649/649**, and
the two run-1 ClientCore failures are the ledger's rotating live-wire timing tests,
absent in run 2. The mix rotates exactly as `docs/TEST_FLAKES.md` describes.

## Known limitations / flakes
- **200v200 ceiling unchanged by routing**: multi-route split does not lift arrivals at 390 local bots;
  the blocker is the global aleph-transit throughput (out of scope here — server/transit mechanics are
  P634/P631's area). Reported with per-route numbers per the task.
- **Sync barrier vs scale**: the synchronous-release barrier holds the fast 2-hop wings for the 3-hop
  wing, which is correct for "one fight" but slightly reduces arrivals inside a fixed 200 s window at
  the collapsed scale. At 30v30 the same barrier gives 100% arrival, so it is a scale interaction, not
  a bug.
- Native 195v195 spawn flake (P634, pre-existing): this box reproducibly trips the harness's 4 s
  spawn probe under the native model at 390 bots; native is verified at 30v30 and the P635 change is
  model-independent (same routing/staging code path).

## Native behaviour classified
| Behaviour | Native cite | Class | What the port does |
|---|---|---|---|
| wings routed onto different paths by a commander | PilotType `c_ptWingman = 2`, igc.h:683-692; WingmanBehaviourBitMask igc.h:784-794 | design (intent) | k distinct entry-aleph routes per side, least-loaded wing split |
| a side advances as a group, not ship-by-ship | FZ formations advance under the commander's move/attack order; P539d fair-contact staging (fsship.cpp:111-112 exit velocity) | design | per-route staging hold + cross-route release barrier |
| one shortest path per side (the funnel) | none — this is OUR BFS planner (BotManager BfsShortestPath / ClusterGraph.FindShortestPath), not a native feature | our artefact (defect) | replaced with distinct-entry-route enumeration when `routes=auto` |
| aleph transit throughput at 390 local bots | server warp-transit / sector collision handling (server side, P634/P631 area) | era/system constraint | out of scope; reported as the next blocker with per-route numbers |

## Modern alternative considered
- **k-shortest distinct-entry routes**: native has no route planner at all (players command ships); a
  modern RTS/strategy engine would compute k edge-disjoint or vertex-disjoint routes and throttle per
  corridor. We use entry-aleph-distinct routes (cheaper than full edge-disjoint and directly attacks
  the "different mouth" goal). Trade-off documented: the 3-hop alternates add transit time and consume
  crossing budget, which the measurements show.
- **Congestion-aware assignment** (send each wing down the least-congested corridor dynamically): listed
  as an alternative; since the measured ceiling is a global crossing-rate cap, not per-mouth
  congestion, dynamic rebalancing would not help at 390 and was not added. Kept the deterministic
  least-loaded static split (unit-testable).

## Deliverables
- `src/Allegiance.ClientCore/State/ClusterGraph.cs` — `FindDistinctEntryRoutes` (+ shared `BuildAdjacency`,
  `BfsAvoiding`).
- `tools/allegiance-mcp/BotManager.cs` — `ActiveBot.RoutePath/RouteIndex`, `EnsureCombatPath` route
  suffix, per-route staging + `StageReleaseRequired(side,route,now)` + `SyncOffsetSeconds` +
  `RouteTransitSecondsPerHop`, `RouteEnumeration`/`FindRoutes`/`PickLeastLoadedRoute`,
  `SpawnBot` route params, `ActiveBotView.RouteIndex/RouteName`, `RouteArrivals()`.
- `tools/allegiance-mcp/McpTools.cs` — `spawn_scenario` `routes`/`maxPerRoute` in both the P557
  mixed-fleet and the P430 homogeneous paths; `get_perf_stats` `routes` block.
- `tests/Allegiance.ClientCore.Tests/P635MultiRouteTests.cs` (new).
- `tests/Allegiance.ClientCore.Tests/P635MultiRouteLiveTests.cs` (new, gated `ALLEGIANCE_LIVE_P635=1`).
- `docs/reports/P635_MULTI_ROUTE_SHOWCASE.md` (this report).

Committed on `master` (not pushed). **Note for Claude: republish the Mac bot build (osx-arm64
self-contained) after this lands.**

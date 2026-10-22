# P780 — a lifepod that touches a rescue probe is rescued (c_eabmRescue / c_eabmRescueAny)

Branch `pi/p780`, base `f46cca2c` (re-grepped before editing; line numbers below are
post-change unless marked). No commit/push; all changes left in the working tree.

## Native rule

- **Bits.** `c_eabmRescue = c_sabmRescue` (0x2000, "Rescue lifepods that collide with it") and
  `c_eabmRescueAny = c_sabmRescueAny` (0x4000, "Rescue any lifepod that collide with it") —
  `Igc/igc.h:781-782`. Text-core names `CanRescue`/`CanRescueAny` (`Igc/missionigc.cpp:321-324`).
- **Trigger.** `CshipIGC::HandleCollision`, `case OT_probe` (`Igc/shipIGC.cpp:896-909`): a hull
  with `c_habmLifepod` colliding with a probe calls `RescueShipEvent(this, NULL)` when
  `(eabm & c_eabmRescueAny)`, or `(eabm & c_eabmRescue)` and the pod's side is the probe's side
  or an allied side (`IsideIGC::AlliedSides`, `shipIGC.cpp:905`). On acceptance the handler
  `break`s (no bounce). **No activation delay** — the probe's `m_time0` only gates ripcord use
  (`probeigc.h:248-254`) — and the probe is **not consumed**: it keeps its hit points and
  lifespan and can rescue again.
- **Shape.** The probe is loaded `c_mtDamagable | c_mtHitable | c_mtStatic` with
  `SetRadius(dataProbeType->radius)` (`Igc/probeigc.cpp:112-118`); the collision is continuous
  over the tick (`HitTest::Collide`), using the probe's `.cvh` hull when the art ships one, else
  a sphere (`_Utility/HitTest.cpp:827-863`).
- **Refusal fallthrough.** If the site returns false, control reaches the generic probe-collision
  response: bounce with elasticity 0.75; a lifepod takes no collision damage
  (`!HasCapability(c_habmLifepod)` gate, `shipIGC.cpp:968`).
- **Server site.** `FedSrvSiteBase::RescueShipEvent(pod, NULL)` (`FedSrv/FedSrv.CPP:13033-13076`):
  with a NULL rescuer there is no `AddRescue`, no Steam award and no `PLAYER_RESCUED` (the whole
  block sits inside `if (pshipRescuer)`, `:13036-13058`). Then `ResetWarpState()` (`:13060`;
  `fsship.h:196-199` — needed because `DockWithStationEvent` gates on `OkToWarp()`, `:13093`),
  `FindTarget(pod, c_ttStation | c_ttFriendly | c_ttAnyCluster | c_ttNearest, ..., c_sabmRestart)`
  (`:13063-13065`), falling back to `GetBase(pod's own side)` when that returns nothing or an
  allied (not own-side) station (`:13067-13071`) — so the pod **always docks at its own side's
  station**, also for a RescueAny probe of another side. `return pstation ?
  DockWithStationEvent(pod, pstation) : false` (`:13074`).
- **Native client.** The default `IIgcSite::RescueShipEvent` returns true (`Igc/igc.h:4860`), so
  the client's own collision just breaks; the docked state stops the oxygen timer
  (`shipIGC.cpp:361-363` only runs for a flying pod).

## Port before / after

**Before.** Two rescue passes existed in `Sector`: ship pickup (`ResolveRescueEvents` →
`Server.PerformRescue`, PLAYER_RESCUED + credit) and pod-at-rescue-station
(`ResolveStationRescueEvents` → `Server.PerformStationRescue`). There was **no
pod-touches-rescue-probe pass**: the client steers an ejected pod at rescue probes
(`Game1.Eject.cs:19-36`, `CommandOrderResolver.cs:141-161`, `Game1.Targeting.cs:1359,1399-1407`)
but the server let the pod drift on the probe until its oxygen ran out. In Claude's analysis of
the 4 h rig log there were **190 rescues, all ship pickups and none by a probe** (quoted as
Claude's; not re-derived here).

**After.**
- `Sector.Add`/`Remove` maintain `_rescueProbeCount` (a `Probe` whose type carries either rescue
  bit; named constants `DataProbeTypeIGC.EabmRescue/EabmRescueAny`, `igc.h:781-782`).
- New pass `ResolveProbeRescueEvents(dt)` in `Sector.cs` (~1509), called in `Update` right after
  `ResolveStationRescueEvents()` inside the same `TickProfiler.Enter("sim.rescue")` scope, with
  `dt` from `Update`. Gates verbatim from `shipIGC.cpp:899-907`; swept sphere-vs-sphere contact
  (pod `0.5 × HullType.Length` + `probe.ProbeType.Radius`, closest point on the segment
  `Position − CollisionVelocity·dt → Position`, the same segment test
  `ResolveTreasurePickups` uses — the port's stand-in for native's continuous
  `HitTest::Collide`; the probe is static). On contact it asks the site
  (`Mission?.Site.RescueShipEvent(s, null) ?? true`), queues `_pendingStationRescues.Add((s, null))`
  and logs once per rescue. A site refusal leaves the pod flying (native's fallthrough is a
  bounce, which the port does not model for any hull — noted as a follow-up).
- Queue/drain retyped to `(Ship Rescuee, Station? Station)`: **`Station == null` is a probe
  rescue** (native `RescueShipEvent(pod, NULL)`, `shipIGC.cpp:907` — the destination is the
  native `FindTarget(... c_sabmRestart)` search, not a touched station). The existing drain step
  in `Server.Broadcast.cs:253-254` (`foreach (var (rescuee, station) in ...)
  PerformStationRescue(rescuee, station);`) compiles **unchanged** — verified by build; that file
  was not edited.
- `Server.PerformStationRescue(Ship, Station?)` keeps the existing station branch byte-for-byte
  and delegates `station is null` to the new `PerformProbeRescue` (the NULL-rescuer body:
  `PlayerWarpState = WarpState.Ready` = native `ResetWarpState`, `FindFriendlyStation`
  destination, no PLAYER_RESCUED, no credit, `_podTrips.End(..., "probe-rescued")`, plus the
  port-telemetry `Skills.Record(SideId, ShowcaseSkill.PodRescue)` — mirrors the station branch,
  port-only, native grants nothing). The dock-and-latch core (move sector, `StationId` +
  `LastDockTime` for pod and children, `_peerShips` Sector snapshot) was factored into
  `DockRescueeAtStation`, shared by `PerformRescue` and the new branch.
- `ServerHost.RescueShipEvent`'s summary now names the third caller; the `Sector` queue doc names
  it too.

## Design notes

- **Where it runs.** Inside `Sector.Update`, immediately after the station-rescue pass, same
  `sim.rescue` profiler bucket — detection in Core, performance in the host via the existing
  drain (the Core side never sends wire).
- **Cheap.** `if (_rescueProbeCount == 0) return;` gives an O(1) early-out for every sector with
  no rescue probe (the ordinary case). The gather reuses a `_rescueProbes` buffer (cleared, never
  re-allocated); pods already queued this tick are skipped by a linear scan of the two small
  pending lists; cost is O(S + S·K), no allocation. The counter is decremented on every probe
  removal path — all of them funnel through `Sector.Remove`: expiry (`Probe.OnUpdate`,
  Probe.cs:691), lethal damage (`Probe.ReceiveDamage`, :264), ammo exhaustion
  (`Probe.FireIfArmed`, :566), all via `Terminate` → `Remove`; manual removal likewise. Sector
  teardown/rotation drops the whole sector object (count dies with it).
- **Alliance.** The pass uses the real `IsAlliedSide` (`Side.AllyGroup`), because native uses
  `AlliedSides` here. The two older rescue passes still say "this port has no alliance concept"
  (now at Sector.cs:1238 and :1440) — **that comment is stale** (P550+ gave the port a real
  alliance concept); per the brief both passes were left unchanged.
- `IsAlliedSide` was rewritten from LINQ (`FirstOrDefault`/`Any`) to indexed scans: the LINQ
  closures/delegates (and the boxed `IEnumerator<Side>` a `foreach` over the `IReadOnlyList`
  produces) allocated per call, which broke the pass's zero-allocation contract. Behaviour
  identical.

## Step 0 findings

- **No Core/Server code read `c_eabmRescue` (0x2000) or `c_eabmRescueAny` (0x4000) from a probe
  type** before this task (grep over `src/Allegiance.Core` + `src/Allegiance.Server` for
  `EabmCapabilities`/`0x2000`/`0x4000`: only `DataStationTypeIGC.SabmRescue/Any` station-bit
  readers and unrelated constants). Only the client did (`Game1.Eject.cs:19-36,178-183`,
  `CommandOrderResolver.cs:141-161`, `Game1.Targeting.cs:1359,1399-1407`, `ShopCatalog.cs:48`,
  `SectorMapQueries.cs:95-102`) and the bot dispenser filter
  (`tools/allegiance-mcp/BotManager.Dispensers.cs:59`). P780 adds the first server-side reader.
- **The port has no ship-vs-probe collision response at all**: `ResolveCombatCollisions` early-
  returns unless an ordnance model is present and its event loop requires one side of every pair
  to be `IOrdnance` (Sector.cs:2203-2208); `ResolveShipShipCollisions` (Sector.cs:3083) only
  pairs ships. Bounce/damage for ship-vs-probe (`shipIGC.cpp:911-1010`) is out of scope,
  including the refused-rescue fallthrough.
- **Stale alliance comments** in the two existing rescue passes confirmed (see above) and left
  as-is, per the brief.

## Tests (`tests/Allegiance.Core.Tests/P780PodRescueProbeTests.cs`)

Model tests use the `StationRescueModelTests` set-up (`PCore001.igc` + `XXV_simple.igc`,
`Mission.Assemble` with `RecordingIgcSite`, sector 1052, the `Lifepod(...)` helper) plus a real
`Probe` from `m.Core.ProbeTypes` with `with { }` eabm overrides, driving the real
`sector.Update`. Live tests use a real `Server` + two loopback bots (`LiveRescueTests` /
`LiveTeleportProbeRipcordTests` boot + `ShipSpawner`).

| # | Test | Base result |
|---|---|---|
| 1 | `Own_side_pod_touching_rescue_probe_is_docked_at_nearest_friendly_restart_station` (live) | **FAILS** ("pod was never rescued" — pod touching the probe at (29.8, 620, 896.9) vs probe (30, 620, 897)) |
| 2a | `Enemy_pod_touching_c_eabmRescue_probe_is_not_rescued` | passes on both (guard) |
| 2b | `Allied_pod_touching_c_eabmRescue_probe_is_queued` | **FAILS** (nothing queued) |
| 3 | `Any_pod_touching_c_eabmRescueAny_probe_is_rescued_to_its_own_side` | **FAILS**; destination asserted own-side (side-1) via `Server.FindNearestRestartStation` |
| 4 | `Probe_rescue_sends_no_PLAYER_RESCUED_and_no_rescue_credit` (live) | **FAILS** on the docked precondition (asserted first); asserts no message 16 and 0 rescues on every PlayerScore incl. the deployer's |
| 5 | `Probe_rescue_cancels_oxygen_timer` (live, 10 s endurance override, dock within ~1 s, 300 ticks past the deadline) | **FAILS** ("pod was never rescued") |
| 6 | `Probe_is_not_consumed_and_rescues_from_creation` (fresh probe rescues pod A on its creation tick, pod B on the next; still in `sector.Models`, Fraction 1) | **FAILS** |
| 7 | `Non_rescue_probe_or_non_lifepod_is_ignored` | passes on both (guard) |
| 8 | `Site_refusal_leaves_pod_flying` (RefusingSite; asserts the site **was called** with `(pod, null)`) | **FAILS** ("RescueShipEvent was never called") |
| 9 | `Rescue_probe_counter_tracks_add_expire_destroy` (new API) + `Probe_rescue_pass_allocates_nothing` (0-byte delta over 100 isolated `ProbeRescuePass` calls each with 0 and 2 rescue probes over 200 ships) | new API — counter test fails on the stubbed base; alloc test asserts the new seam only |
| 10 | `Rescue_probe_deployed_via_FIRE_EXPENDABLE_rescues_a_pod` (live, real dispenser deploy, ~34u spawn separation, sustained 20 u/s drift) | **FAILS** |
| inv | `Rescue_probe_inventory_matches_the_core` | passes on both — **Claude's inventory table matched the core exactly** (ids 19/51/2, eabm 0x2000/0x2000/0x4000, radius 6, life 1200/1200/1800, hp 25/1/25, price 250/100/100, tech bits 1+18 / 1+20 / 1+102; lifepod hulls length 4–5 speed 50–60 plus the Observer length 2 speed 160) |

On-base run was done with the production changes stashed and two compile-only stubs
(`RescueProbeCount => 0`, empty `ProbeRescuePass`) so the new test file builds: model tests
**6 of 9 fail** (2b, 3, 6, 8, 9, alloc), passes on both: inventory, 2a, 7; live tests **4 of 4
fail**. Output captured in `.scratch/` during the run (working tree since cleaned).

**Existing-test fixture fix (required, documented):** `ProbeCombatLiveTests` picks its probe by
"lowest HP, lifespan ≥ 8" — that selects the type-51 **Beacon** (hp 1, eabm 0x2000, a rescue
probe), and the deployer hull is "lowest HP hull" = the **Observer**, which carries
`c_habmLifepod`. Under the new (correct, native-faithful) rule the deployer touched its own
rescue probe and was rescued mid-test (`[rescue] lifepod ship 8098 touched rescue probe 5464` in
the log), breaking `Lethal_hit_kills_probe...` (deployer at 1053, broadcast cluster 1051). The
test failed **3/3 alone** on my branch and passed on base. Fix: both probe selectors in
`ProbeCombatLiveTests` now exclude rescue-capable probe types (`(p.EabmCapabilities & 0x6000) == 0`)
— a combat-math fixture must not deploy a rescue probe under a parked lifepod-hulled ship.

**Live-test flake root-caused and fixed:** two intermittent failures (~2 in 30 runs) were the
FIRE_EXPENDABLE request landing on the wrong ship — under load the two logons can complete
out of order relative to spawn bookkeeping, so `DeployRescueProbe` now resolves the firing bot
(and test 10 the pod's peer) by the ship ids the bots actually own, and re-sends the fire
request (3 rounds in the dispenser). 0 failures in the ~40 runs since.

## Timing (scratch harness, Release, sector 1052, 200 ships / 20 lifepods + 10 ordinary probes, 10,000 ticks × 0.05 s, warm 100)

| Variant | µs/tick |
|---|---|
| (a) base, no pass | 85.74 |
| (b) P780, 0 rescue probes | 84.44 |
| (c) P780, 3 rescue probes | 84.47 |

(c) − (a) ≈ −1.3 µs/tick is within run-to-run noise; the counter early-out makes the pass
unmeasurable, and 3 rescue probes add nothing measurable either.

**Fix 6 (station-pass `HashSet` reuse, taken):** `ResolveStationRescueEvents` isolated via
reflection on a sector with a synthetic `c_sabmRescue` station + 200 ships, 10,000 calls:
before **64.0 B/call allocated, 0.65 µs/call**; after (reusable `_stationRescueDone` set, cleared
at entry) **0 B/call, 0.61 µs/call**. Behaviour identical.

## Measured gate numbers

- Whole-solution Debug build: **0 errors, 0 warnings**.
- AOT publish (`dotnet publish src/Allegiance.Server -c Release -r linux-x64
  --self-contained -p:PublishAot=true`): exit 0, **0 warnings, no IL2026/IL3050**.
- 3× alone, all pass: `P780PodRescueProbeTests` + live (13 tests × 3), `StationRescueModelTests`
  + `P754CallbackTests` + `P667ParallelSectorSimDeterminismTests` (17 × 3), `LiveRescueTests`
  (5 × 3), `LiveTeleportProbeRipcordTests` (9 × 3), `ProbeCombatLiveTests` (3 × 3), and the
  oxygen/lifepod/pod set — `LifepodEnduranceDisplayTests`, `LiveInSpaceEjectPodAliveTests`,
  `LiveLifepodEnduranceDisplayTests`, `LiveLifepodOxygenTests`, `P590PodRescueTests`,
  `P699bPodLoadTests`, `P699bPodNavigationTests`, `P699bPodTelemetryTests`,
  `P560PodWarpLegClosedLoopTests`, `P699PodHomeLiveTests`, `P540EjectLiveProofTests`
  (28 tests × 4 runs total, 0 failures).
- Fast tier (`tools/run-tests.sh --tier fast`): first run FAILED on
  `ProbeCombatLiveTests.Lethal_hit_kills_probe...` (the fixture collision above); after the
  fixture fix: **PASS — 9872 tests, 274.6 s**.
- Showcase never-parks (`P700ShowcaseLiveTests.Local_showcase_never_parks_with_target_for_five_seconds`):
  **5/5 pass** (~1 m 50 s each; no base comparison needed — zero failures).

## Concurrency

- No P775 file was touched: `Server.Broadcast.cs`, `Server.Lobby.cs`, `Server.cs`,
  `Server.Combat.cs`, `TrainingHost.cs` are unmodified (`git status` clean of them); existing
  methods in them (`FindFriendlyStation`, `Server.Combat.cs:2134`) are only called.
- No P776 region touched: `ResolveWarpCollisions` and its call site are unmodified;
  `src/Allegiance.Core/Model/AlephConeHitTest.cs` does not exist here.
- Everything needed lived in `Sector.cs`, `Server.Docking.cs` (+ the doc comment in
  `ServerHost.cs`, constants in `DataProbeTypeIGC.cs`), tests and matrix rows. The five P775
  files were never needed.
- **Incident note:** stash namespaces are repo-global across worktrees. During the on-base
  measurement phase, another agent (P777) pushed a stash between my `git stash push` and `git
  stash pop`, so my first `pop` applied *their* changes to this worktree. I reverted them
  immediately and preserved the exact diff at
  `~/allegiance-evidence/P780/P777_STASH_ACCIDENTALLY_POPPED_IN_P780_WORKTREE.diff` so the P777
  agent can restore it; my own stash was then popped cleanly and verified
  (Sector/DataProbeTypeIGC/Server.Docking diffs present, solution rebuilt, all tests re-run).
  The P777 agent's base-phase measurement (if any) may need re-doing on their side.

## Matrix

- `docs/parity/sim-rules-matrix.csv` SIM-0206: status stays `implemented`; port_cite (col 8)
  gained `src/Allegiance.Core/Model/Sector.cs ResolveProbeRescueEvents`; evidence (col 12) gained
  `P780PodRescueProbeTests.Allied_pod_touching_c_eabmRescue_probe_is_queued`; notes (col 16)
  appended the P780 sentence. Field-count parse: every row has 16 fields.
- `docs/parity/parity-matrix.csv`: two new rows after `c_eabmQuickReady` — `c_eabmRescue` and
  `c_eabmRescueAny`, both **`done`** (production code reaches the rule at the cited native
  lines; tests 1–6 pass on the new code and fail on the base), each citing
  `ResolveProbeRescueEvents` + the `PerformStationRescue` null-station branch + the test class.
  Field-count parse: every row has 6 fields. `git diff --stat docs/parity`: 1 changed line +
  2 added lines.

## Follow-ups (not done here, per brief)

1. `Server.Broadcast.cs:240-249` step comment is now slightly stale (it says the station-rescue
   drain is "P301"; it now also carries probe rescues). Not edited — P775 owns that file.
2. Ship-vs-probe collision response (bounce, elasticity 0.75, non-pod collision damage,
   `shipIGC.cpp:911-1010`), including the refused-rescue fallthrough — the port has none for any
   hull.
3. The stale "no alliance concept" comments in `ResolveRescueEvents` / `ResolveStationRescueEvents`
   (Sector.cs:1238, :1440) and the possible double rescue between the ship-pickup and station
   passes (neither checks the other's queue) — noted, unchanged.
4. Native picks the nearest **friendly (incl. allied)** restart station and falls back to
   `GetBase(own side)` when that one is allied (`FedSrv.CPP:13063-13071`); the port's
   `FindNearestRestartStation` only searches the pod's own side. Result is still an own-side
   station, but may differ from native's pick when an allied base is nearer — existing P550
   simplification, not changed.
5. Rescue paths raise none of native `DockWithStationEvent`'s side effects (`SDR_DOCKED`
   announce, `ShipStatusDocked`) — existing behaviour for all three rescue kinds; clients see
   the dock through SHIP_STATUS.
6. Bots (P779 material): `tools/allegiance-mcp/BotManager.Dispensers.cs:57-60` never deploys a
   rescue probe (explicit `(EabmCapabilities & 0x6000) == 0` filter, scout-only); `DrivePodHome`
   (`BotManager.cs:3816+`) targets only restart-capable own stations and ignores rescue probes,
   unlike the port client's eject autopilot and native's `c_ttProbe` search (`appmsg.cpp:233`).

## Claude review fixups

- SIM-0206: the `ResolveProbeRescueEvents` cite had landed in column 9 (`port_site_binding`); moved to column 8
  (`port_cite_or_reason`) as the Matrix section states.
- Follow-up 3 widened: the reverse double-rescue across sim chunks (probe pass queues the pod in chunk 1, the
  ship-pickup pass queues it again in chunk 2 because `ResolveRescueEvents` does not look at
  `_pendingStationRescues`) is the same pre-existing class; the host would run `PerformRescue` then a harmless
  re-dock, recording the pod trip and skill twice.
- Test gaps noted, not fixed: no host-level test that an enemy-side Sanctuary probe docks the pod at its own side's
  station; `Site_refusal_leaves_pod_flying` lacks `Require.Artwork()`; test 10 duplicates test 1's setup.

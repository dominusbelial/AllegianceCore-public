# NT-3 — triage of the 2026-10-30 nightly (bad commit `b567ee1f`, base `48cdb2de`)

Worktree `~/allegiance-pi-nt3` (branch `pi/nt3`), base master at dispatch `69c37dbb`
(`b567ee1f` is its ancestor). No commit, no push — every change is in the working tree.
Lane rules followed: `~/.dotnet/dotnet` explicitly, no remote host contacted, no
`git stash`, no synthetic CPU load (load-dependent results come from bounded
self-parallel runs), scratch in `.scratch/` (excluded, deleted at the end).

The nightly ran on the mini runner, whose TRX/logs are not on this machine, so every
number here is a **local reproduction** on this workstation (24 cores, light load
unless a bounded parallel run is named).

## TL;DR — five of the eight "new" failures are two default flips; three are sweep-load flakes

The 10-29 merge window (`48cdb2de..b567ee1f`, 29 first-parent commits) flipped three
server defaults — `sim.nativeGotoPlan` (**P875**, `28052050`), and P887's
`broadcast.parallelBuild` / `broadcast.shipUpdateModel` / `transport.mode`
(**P887**, `76c6355e`) — and five fixtures/budgets were calibrated on the old
defaults. All three flips are deliberate, rig-A/B-backed design changes
(`docs/reports/P875_REPORT.md`, `docs/reports/P887_REPORT.md`), and P887 already
pinned 12 tests that need the old path; it missed the ones below.

**P890 is not the cause of any of them.** Four independent bisects/point-checks all
land *before* the P890 merge: `de6d670f` … `e6363aef` are good and `76c6355e` (P887) /
`28052050` (P875) are the bad steps, while `b567ee1f` (the P890 merge itself) is only
the bad *end* of the range. P890's only reachable change is
`src/Allegiance.Server/Server.Dispatch.cs` (`legacy` is now `u.Time == 0` only, plus the
replayed turn rates) and none of the failing tests sends a `Time != 0` pose.

| Test (tier) | Alone | Verdict | First bad commit | Action |
|---|---|---|---|---|
| `PipelineCostTests.Steady_512_peer_pipeline_allocation_budget` (Perf) | **fail 3/3** | regression-by-default-flip (P887 `parallelBuild`) | `76c6355e` | test pins the serial builder (budget unchanged) |
| `Hud17DetectionAllocationTests.Ship_detection_step_is_allocation_free_and_status_resends_only_grow_on_edges` (Perf) | **fail 3/3** | same | `76c6355e` | test pins the serial builder (budget unchanged) |
| `BroadcastSnapshotLoopbackTests.Crowded_TCP_pipeline_cost_three_configurations` (Perf) | **fail 3/3** | fixture lost its model pin (P887 `shipUpdateModel`) | `76c6355e` | fixture carries `basis`'s pinned `"port"` |
| `BroadcastSnapshotLoopbackTests.Crowded_TCP_interest_tiers_reduce_bytes_and_report_ages` (Perf) | **fail 3/3** | same | `76c6355e` | same |
| `MultiHopDroneLiveTests.Server_drone_crosses_two_alephs…` (Live, repeat) | **fail 3/3** | native-travel fixture not seeded (P875 `nativeGotoPlan`) | `28052050` | fixture seeds the scouted map (P875 pattern), assertions untouched |
| `P728HumanCommanderLiveTests.Human_research_spends_wallet…` (Live) | **fail 3/3** | default-flip casualty (P887 `shipUpdateModel`); order adopted then lost with the recipient's respawn | `76c6355e` | fixture re-issues the order while polling + durable target; 3/3 pass (§5) |
| `NpcPilotTypeGateLiveTests.Non_miner_idle_npc_stands_by…` (Fast) | pass 3/3, pass 4× parallel | load flake (already ledgered, P887 §4) | — | ledger re-confirmation |
| `NpcWingmanRangeMaintenanceLiveTests.Wingman_throttle_responds…` (Fast) | pass 3/3, pass 4× parallel | load flake | — | ledger entry added |
| `DeadLatchRespawnLiveTests.Remote_client_sees_victim_again…` (Live) | pass 3/3, pass 4× parallel | load flake | — | ledger entry added |
| `P798ScoutOpeningLiveTests.Fogged_showcase_scouts…` (Live, repeat) | **fail 3/3** | persistent master failure — fails at `48cdb2de` too | none in the window | ledgered as open (§6) |
| `SC4ShowcaseLiveTests.Research_showcase_deploys_chaff…` (Live, repeat) | **fail 3/3** | bot-policy regression (P877 removed the undirected drop) | `72f04522` (pass at `ea132a30`) | ledgered as open for the P877 owner (§6) |
| `SC2SupportPhaseLiveTests.Support_pilots_follow_the_side_phase…` (Live, repeat ×4) | **pass 1/1** (906 s) | not reproducible (NT-2's P791 fix is in master) | — | ledgered as unreproduced (§6) |
| `NpcWingmanDefendLiveTests.Defender_disengages…` (ledgered) | **fail 3/3** | unchanged persistent baseline | — | no action (§6) |
| `P745FinaleSiegeLiveTests.Research_showcase_finale_destroys_a_station` (ledgered) | **2 pass / 1 fail** | ledgered scenario sensitivity | — | no action (§6) |
| `ShipStatusServerTests.PeerReceivesItsOwnShipStatus_InJoinBurst` (Fast, seen in my own sweep) | pass 3/3 (0.35 s) | sweep load flake | — | ledger entry added (§7) |

Two more sweep-only observations from this task's own fast-tier runs, both already
ledgered and both reproduced here: `DockDockingTests.Ship_nowhere_near_a_station_is_unaffected`
(status-roster fixture intermittency, P872/P890 rows) and
`P777WarpExitWobbleTests.Npc_exit_touch_rate_is_measured` (10.30 s / 10.64 s vs the
10 s calibrated limit; the known fast-tier offender).

## 1. The allocation/cost pair — measured vs budget

Both budgets are **P679/HUD-17 serial-builder budgets** and are *unchanged* in this
worktree; what changed is which builder they were measuring.

`PipelineCostTests.Steady_512_peer_pipeline_allocation_budget` (512 peers, 100
measured ticks; budget `0..104_000` producer B/tick, `0..16_000` publisher B/tick):

| Configuration | producer B/tick | publisher B/tick | `step:broadcastShipStates` B/tick | verdict |
|---|---|---|---|---|
| `48cdb2de` (serial builder) | 77,748 | 2,536 | 53,736 | pass |
| `b567ee1f`/HEAD, default (parallel builder) | 240,148 / 247,512 / 260,012 | 182,328 | 169,034 | **fail** (`Range: 0-104000`) |
| HEAD with the test's serial pin | 77,748 | 2,536 | 53,736 | pass (exact pre-P887 numbers) |

`Hud17DetectionAllocationTests` (400 peers; `broadcastShipStates` edge replay must be
within `base + 2048` B/tick, and `shipDetection` must stay 0):

| Configuration | `broadcastShipStates` edge / base B/tick | SHIP_STATUS edge / base | verdict |
|---|---|---|---|
| `48cdb2de` (serial) | 488 / 488 | 17,036.8 / 1,208.0 per tick | pass |
| HEAD default (parallel) | 150,506 / 82,307 (also 128,740 / 84,941, 213,042 / 82,826) | identical | **fail** (`allowed +2048`) |
| HEAD with the test's serial pin | 488 / 488 | identical | pass |

Why the numbers move, and why the tests pin rather than re-budget:

- `broadcast.parallelBuild` became `true` by default in P887
  (`ServerConfig.cs:811`; resolution `Server.Lifecycle.cs:1644`). The parallel twin
  (`Server.Broadcast.cs:1241`) hands each tick's per-peer build to
  `Parallel.For` workers, so `GC.GetAllocatedBytesForCurrentThread()` — the seam both
  tests measure through `TestProjectionAllocationCapture`
  (`Server.BroadcastTestSeams.cs:245`) — measures **whatever share of the work the
  calling thread happened to take**: the same unchanged code produced 77 KB and
  240–260 KB producer bytes/tick across runs, which cannot carry a budget.
- The growth is nonetheless **real in total**, not an accounting illusion: the same
  HUD-17 replay measured with `GC.GetTotalAllocatedBytes` allocates 4.61–9.37 MB/tick
  in parallel mode vs 4.09–7.66 MB/tick serially (~+13–22 %, dominated by the 17 k
  status packets either way), and `PublishAllocatedBytes` grows 2.5 KB → 182 KB/tick
  at 512 peers. That is precisely the P603 §3 ("each worker owns its per-peer result
  list; pooled allocation is left as future polish") shortcut, now on the default
  path. **Not fixed here** — pooling `BroadcastPeerBuild`'s per-peer `List`s /
  `HashSet`s / `Dictionary`s is a broadcast-internals refactor that belongs behind
  its own rig A/B, and out of this triage's scope (§8 open item).
- So the budgets were not re-budgeted: they keep policing the serial builder they
  were calibrated on, exactly as P887 pinned its 12 port-path tests, and the parallel
  builder's numbers are recorded above for the follow-up.

## 2. The crowded-TCP loopback pair — a lost model pin

`BroadcastSnapshotLoopbackTests.RunCrowded` builds a *fresh* `ServerConfig` whose
`Broadcast` section only set `ImmutableSnapshot`/`MinUpdateHz`/`MaxDegreeOfParallelism`,
so `ShipUpdateModel` rode the default. It was `"port"` when the test was written and
is `"native"` since P887 (`Server.Lifecycle.cs:1843`), while the fixture's *intent*
is explicit — `var basis = UpdateCadenceTests.Config(2, native: false)`.

Measured (256 peers, per phase, `startEgress`):

| Mode | `b567ee1f`/HEAD | with `ShipUpdateModel` carried (`"port"`) |
|---|---|---|
| no snapshot / no tiers | 49.7 MB | 49.7 MB |
| snapshot + interest tiers | 115.6–115.8 MB → **`tiers must reduce crowded egress: 49 7xx xxx -> 115 7xx xxx`** | passes (< 0.8 × the previous phase) |

The interest-tier stream (`BroadcastTierPose`, entered when
`_broadcastInterestTiersEnabled`, `Server.Broadcast.cs:3101`/`3210`) replaces the
**port** per-tick teammate `SingleShipUpdate` posBatch stream — that is what P677
measured and what the test's `< 80 %` bar encodes. Under the native model it is
*added traffic on top of* the native deviation-ranked HEAVY/LIGHT budget
(`FedSrv.CPP:3923-3950`), which is the same +bandwidth direction the rig saw for
`interestTiers` and is why P887's baseline keeps that option **off**. Both tests
pass 3/3 with the pin; `ParallelBuild` needs no pin here (verified: the same runs
pass with it on).

## 3. `MultiHopDroneLiveTests` — P875's native travel needs a scouted route

First bad commit `28052050` (P875 merge) — confirmed by direct run at `28052050^1`
(`dff29be1`, pass, `[boot] sim.nativeGotoPlan = false`) vs `28052050` (fail,
`nativeGotoPlan = true`); the same failure also disappears at HEAD with
`ALLEGIANCE_SIM_NATIVE_GOTOPLAN=0` (pass, 1 m 25 s).

Native travel only flies routes/targets the side has **seen**
(`Igc/common.cpp:899-927`; `Igc/igc.h:4909-4913`: `GetCluster(pship, model)` is null
when `!pship->CanSee(model)` — warps are `c_mtSeenBySide`, `warpIGC.cpp:35`). The
fixture places a side-1 station in the *enemy* home (Omega) and orders a drone two
hops up, but never stages visibility, so under native travel the route is unknown
and the drone holds in place: `drone never crossed Plato→Alderan (first hop, ticks
60000, still in Plato/1060)`.

Fixed the way the other 16 P875 fixtures were fixed (no assertion touched): the game
config asks for the native map reveal (`ShowMap = true` → `bShowMap`, `Igc/igc.h:1123`
→ `CmapMakerIGC::RevealMap`, `mapmakerigc.cpp:1002-1017`) and the mission runs the
start-of-game visibility pass (`Mission.SeedStartingVisibility()`,
`src/Allegiance.Core/Model/Mission.cs:394`, needed because the debug auto-start writes
`Stage` directly and skips the lobby's Started-transition seed,
`Server.Lobby.cs:2162`). Result: both hops again — hop 1 at tick 265, hop 2 after
103.5 s of sim, `minD 227.4`, pass 43 s (3/3, deterministic).

## 4. `NpcPilotTypeGate` / `NpcWingmanRangeMaintenance` / `DeadLatchRespawn`

All three pass 3/3 alone (2–4 s each) **and** 4/4 under a bounded self-parallel run
(`xargs -P 4`, load average 3.1–6.0 on 24 cores, other lanes active), so nothing in
the 10-29 window makes them fail deterministically:

- `NpcPilotTypeGateLiveTests.Non_miner_idle_npc_stands_by…` is already in the ledger
  (`docs/TEST_FLAKES.md`, P887 §4: "failed once in sweep 1 (`travelled 126.161`);
  passes alone — 3/3"). Re-confirmed here; the class carries **no `Tier` trait**, so
  it runs in the *fast* sweep, where 5 projects share the box.
- `NpcWingmanRangeMaintenanceLiveTests.Wingman_throttle_responds…` — same shape
  (Fast tier, 2 s alone); new ledger entry.
- `DeadLatchRespawnLiveTests.Remote_client_sees_victim_again…` — Live tier but only
  2–3 s (`[P521-2C] B saw A die (IsDead latched) after 0 ticks`); new ledger entry.

## 5. `P728HumanCommanderLiveTests` — real, in the merge window (default flip)

`fail 3/3` at HEAD (`b567ee1f`) and HEAD+2 later commits (7 m 16 s, 437 s, identical
assertion) vs **pass** at `48cdb2de` (6 m 01 s). Failure:
`Bot did not adopt the human target through OrderedTarget` (line 148).

Bisect (`--first-parent 48cdb2de..b567ee1f`, signal = this test):

| step | commit | result |
|---|---|---|
| 1 | `de6d670f` (P885) | pass |
| 2 | `ac6f4d9e` (P887 merge + doc) | **fail** |
| 3 | `1d11f114` (docs) | pass |
| 4 | `76c6355e` (P887) | **fail** |
| 5 | `e6363aef` (AIX1) | pass → **first bad = `76c6355e` (P887)** |

Discriminator run at HEAD: with `ALLEGIANCE_SHIP_UPDATE_MODEL=port` the same test
**passes** (6 m 01 s) with `broadcast.parallelBuild` still on, and with
`ALLEGIANCE_SIM_NATIVE_GOTOPLAN=1`. So the trigger is P887's
`broadcast.shipUpdateModel: "native"` default (`Server.Lifecycle.cs:1843`), not P890
(which the bisect excludes) and not the parallel build.

Mechanism (instrumented copies of the MCP bot manager's order-clear sites, plus the
host's worker log — instrumentation reverted, nothing shipped):

- The bot **does** adopt the order: `[p431] bot … (ship 8105) AUTO-ACCEPTED queued
  order cmdID=1 target=Ship#8112` + `adopted commanded ship-target #8112`.
- The **recipient** then dies inside the observation window
  (`[p539d] DEATH bot … ship 8105 side 0 cause=gunfire deaths=2`), and a `Death`/
  respawn reset clears the bot's order bookkeeping (`tools/allegiance-mcp/
  BotManager.Lifecycle.cs:41,125`; `Kills.cs:81`). A destroyed/podded order TARGET
  clears it too (`BotManager.Orders.cs:62`), and in one run the spawned constructor
  was indeed gone at the deadline (`target 8112: present=False`).
- Under the native ship-update model the bots receive poses/status differently
  (`Server.Broadcast.cs:3113` — the teammate posBatch stream is suppressed and only
  the deviation-ranked HEAVY/LIGHT budget remains, `FedSrv.CPP:3923-3950`), so the
  furball resolves differently and a 20 s-old combat bot now dies inside the 120 s
  window where it used to survive.

Fix (test-only, assertion unchanged): the fixture re-issues the order while polling
(what a human commander actually does after losing a ship) and spawns its "durable,
unarmed stock constructor" beside its own side's home station instead of 1 km from
the recipient, so the target cannot be shot out of the window either. Verified 3/3
concurrently (7 m 16 s each, host load 8.34). **This one is worth a product look**:
nothing in the test's expectations implies the port-only path, so "a bot forgets a
commander order when it respawns" is a behaviour the native model makes much more
likely in real play.

## 6. The remaining repeats and the two ledgered rechecks

- **`P798ScoutOpeningLiveTests.Fogged_showcase_scouts_open_the_map…`** (repeat, 2
  nights, unledgered): **fail 3/3** alone (331 s / 294 s / 560 s), always the same
  assertion — `Assert.Single(sectors)` with `[1054, 1058]`: the two sides' *main clash*
  forms in different resource sectors instead of one shared area. It is **not a 10-29
  regression**: it fails identically at the pre-window base `48cdb2de`
  (`~/allegiance-evidence/NT3/base-p798.log`, 3 m 48 s), i.e. master has been red here for at least the
  two nights the brief names. No `src/` code in the window is implicated, and this task
  changed no production code — so it is ledgered as a **persistent master failure**
  with the exact failure and is left open for a clash-selection investigation.
- **`SC4ShowcaseLiveTests.Research_showcase_deploys_chaff…`** (repeat 2 of 4,
  unledgered): **fail 3/3** (602 s each, the test's own deadline). The counts are met on
  both sides (`chaff/probes/mines` = 173/24/32 and 79/33/28; 224/27/35 and 92/34/21;
  170/21/25 and 66/37/24 — all above 10/10/5), and `contestedProbes` is `[0,1]`; what
  never happens is the *defensive* mine: `homeMines` is `[]` or `[1]` instead of both
  sides. Every mine in every run is an **escape** mine
  (`[sc4] mine … defendedAleph=False escape=True … lifespan=15`, 60/56/49 drops).
  **First bad commit: P877 `72f04522`** — the test **passes at `72f04522^1`
  (`ea132a30`, 5 m 23 s)**. P877 removed the undirected "enemy within 2500 u" drop from
  the bot trigger set (`tools/allegiance-mcp/BotManager.Dispensers.cs:100-165`: only
  `escape` / `defendedAleph` / `arrivalField` drop now), and both survivor branches
  need a **long-lived** mine (`Showcase.PreLayMineAtAleph` and
  `PreLayMineAtArrivalAleph` require `mineLifespan >= PreLaidMineMinLifespan = 60 s`,
  `tools/allegiance-mcp/Showcase.cs:383-388`), while the launcher actually mounted in
  the showcase lays 15 s prox mines — so a bot only mines when *pursued*, and never at a
  home aleph. That is a deliberate bot-policy change (P877 is a docs/parity-tracked
  task), so this is **not** a product bug I should silently re-tune: the SC-1 skill
  claim "defensive mines at a defended aleph on EACH side" and the policy now disagree.
  Ledgered as an open regression of `72f04522` for the P877 owner (either the
  defensive-mining duty needs a long-lived launcher, or the scorecard expectation has
  to be re-defined and re-measured).
- **`SC2SupportPhaseLiveTests.Support_pilots_follow_the_side_phase…`** (repeat 4/4):
  **does not reproduce** — the first isolated run passed (906 s, the test's full
  150 s warm-up + 750 s observation). NT-2's P791 commander-repair hold bookkeeping fix
  (`tools/allegiance-mcp/BotManager.Support.cs:324-327`) is in master and the test's
  autonomous-hold metric is unchanged, so the nightly's 4/4 streak predates the fix
  landing. Ledgered as unreproduced (it needs a re-run on the mini before anyone calls
  it green); 3 isolated reruns were not completed inside this task's window.
- **`NpcWingmanDefendLiveTests.Defender_disengages…`** (ledgered recheck): **fail 3/3**
  (1 s each, `Assert.Same` at `NpcWingmanDefendLiveTests.cs:179` — `DefendTarget` is
  null). Unchanged, exactly as the existing P872/P887/P888/P890 rows say.
- **`P745FinaleSiegeLiveTests.Research_showcase_finale_destroys_a_station`** (ledgered
  recheck): three isolated runs were started concurrently; the recorded outcomes are in
  §7. It is already ledgered as unreliable on master (`TEST_FLAKES.md:3537` and the
  P821 row: the ending is decided by native kills before the station dies), and no
  source in the window touches finale massing or station damage.

## 7. Verification

Raw logs/TRX/JSON are outside the repo: `~/allegiance-evidence/NT3/` (isolated runs
under `solo/`, bounded parallel runs under `par/`, the three `git bisect` transcripts
as `bisect-trace.txt` / `bisect2-trace.txt` / `b728-trace.txt`). The compact numbers
are in [`NT3_assets/triage-measurements.json`](NT3_assets/triage-measurements.json).

| Gate | Result |
|---|---|
| `~/.dotnet/dotnet build AllegianceCore.slnx` | **0 warnings / 0 errors** — whole solution, re-run after every edit (last one after the final `P728` change) |
| Server Native AOT publish | **not needed** — no `src/` file changed (test-only diff: 4 test files + this report + `TEST_FLAKES.md`) |
| Showcase never-parks ×5 | **not needed** — no Server/Core/bot code changed |
| `tools/build-tools.sh` | see below |
| Touched tests 3× alone | see the table below |
| ONE `tools/run-tests.sh --tier fast` | two runs, both `FAIL` for environment reasons unrelated to the diff — see below |

**Touched tests (3× alone, plus a bounded self-parallel run where the item was a load
flake).** The 3×-alone results use separate `dotnet test` processes with
`--filter FullyQualifiedName~…`:

| Test | Isolated | Parallel | Notes |
|---|---|---|---|
| `PipelineCostTests.Steady_512_peer_pipeline_allocation_budget` | 3/3 pass (~30 s) | — | `producer` back to 77,748 B/tick (budget 104,000) |
| `Hud17DetectionAllocationTests.Ship_detection_step…` | 3/3 pass (~33 s) | — | `broadcastShipStates` 488 B/tick edge = base again |
| `BroadcastSnapshotLoopbackTests.Crowded_TCP_pipeline_cost_three_configurations` | 3/3 pass (~25 s) | — | |
| `BroadcastSnapshotLoopbackTests.Crowded_TCP_interest_tiers_reduce_bytes…` | 3/3 pass (~18 s) | — | |
| `MultiHopDroneLiveTests.Server_drone_crosses_two_alephs…` | 3/3 pass (43 s, hop 1 at tick 265 every run) | — | |
| `P728HumanCommanderLiveTests.Human_research_spends_wallet…` | 3/3 pass — three separate `dotnet test` processes run concurrently, 7 m 16 s each, host load 8.34 (so 3/3 even with contention) | — | failures were 3/3 for 7 m 16 s each before the fix |
| `NpcPilotTypeGateLiveTests.Non_miner_idle_npc_stands_by…` (load flake) | 3/3 pass (2 s) | **4/4** | |
| `NpcWingmanRangeMaintenanceLiveTests.Wingman_throttle_responds…` (load flake) | 3/3 pass (1–2 s) | **4/4** (36–37 s each under 4× contention — the load effect that breaks it in a sweep) | |
| `DeadLatchRespawnLiveTests.Remote_client_sees_victim_again…` (load flake) | 3/3 pass (2–3 s) | **4/4** | |
| `NpcWingmanDefendLiveTests.Defender_disengages…` (persistent) | 0/3 (1 s, `Assert.Same` → `DefendTarget` null) | 3 concurrent, same failure | unchanged baseline |
| `P745FinaleSiegeLiveTests.Research_showcase_finale_destroys_a_station` (ledgered) | **2 pass / 1 fail** (12 m 44 s / 14 m / 11 m, run concurrently) | — | exactly the ledgered "the ending is decided by native kills" behaviour |
| `SC2SupportPhaseLiveTests.Support_pilots_follow_the_side_phase…` | **1/1 pass** (906 s) | 3 further runs were cut at my harness's 900 s timeout (the test legitimately needs >900 s), so **not** 3/3 — re-run on the mini before calling it green | | |
| `P798` / `SC4` (repeat failures, left open) | 3/3 fail each | — | see §6 |

**Fast tier.** First run (`tiers-20261030T151157`) STARTED while the three concurrent
P728 runs were still alive: `Lobby 96/119 (23 FAIL)`, `Update 28/28`,
`MonoGame 3797/3800 (3 not executed)`, `ClientCore 2034/2034`,
`Core 5875/5878`, plus the known slow offender
`P777WarpExitWobbleTests.Npc_exit_touch_rate_is_measured` at 10.297 s.

All 23 Lobby failures are the **same environment error**, not a product failure:
`System.IO.IOException : The configured user limit (128) on the number of inotify
instances has been reached` (each `WebApplicationFactory` host creates a
`PhysicalFilesWatcher`; `fs.inotify.max_user_instances` is **128** on this box and the
shared host runs several lanes). The second run (`tiers-20261030T154401`, quiet box,
P744 factor 0.948, infrastructure checks PASS) reproduced it — `Lobby 94/119 (25
FAIL)`, again all inotify-limit — plus:
`ClientCore 2033/2034` — `DeadLatchRespawnLiveTests.Remote_client_sees_victim_again…`
("B never saw A's death after 2500 ticks": the pump counts *ticks*, so it needs the
client's receive thread to be scheduled — exactly the load flake proven 3/3 alone and
4/4 self-parallel above);
`Core 5875/5878` — `NpcWingmanDefendLiveTests.Defender_disengages…` (persistent, §6),
`DockDockingTests.Ship_nowhere_near_a_station_is_unaffected` (already ledgered
status-roster fixture intermittency, P872/P890 rows), and
`ShipStatusServerTests.PeerReceivesItsOwnShipStatus_InJoinBurst` (passes **3/3 alone**
at 0.35 s — new sweep load flake, added to the ledger);
slow offender `P777WarpExitWobble` 10.637 s (already ledgered).
`tools/build-tools.sh`: **PASS** — `all 96 tool projects built`, 0 errors (after the final whole-solution build).

## 8. Open items / follow-ups for Claude

1. **Parallel-build allocation (P887 baseline).** Measured +13–22 % total allocation
   and 2.5 KB → 182 KB/tick publish allocation vs the serial builder at 512 peers
   (§1). P603 §3 already names the fix ("pooled allocation is left as future
   polish"); it now applies to the default path. Suggest its own task with a rig A/B.
2. **`interestTiers` + native model.** With `shipUpdateModel = "native"`, turning
   `interestTiers` on *adds* ~2.3× crowded egress (the tier stream is additive on top
   of the native budget instead of replacing the port posBatch stream). The option
   stays off in the baseline (§P887), but the combination is now reachable by a
   config edit and is undocumented — worth a doc line in `README.md`/P677's report.
3. **A bot forgetting a commander order on respawn.** P728's native-model failure was
   the recipient dying/restarting inside the window and its order bookkeeping being
   reset. The fixture is now robust (`3/3`), but the underlying behaviour is worth a
   product look: under P887's default model a bot that is killed mid-order comes back
   with no order at all (the manager resets `OrderedCommand`/`OrderedTargetId` on the
   respawn, `tools/allegiance-mcp/BotManager.Lifecycle.cs:41,125`), where before the
   default flip the same order usually outlived the 2-minute observation. This is
   **not** a native-parity claim either way: native clears every command slot on a
   (re)spawn — `SetCommand(c_cmdQueued/Accepted/Current/Plan, NULL, c_cidNone)` in
   `CshipIGC::Initialize`, `Igc/shipIGC.h:502-505` — so a native respawned ship starts
   clean too. It is MCP bot-policy that changed behaviour only because the fight
   around the bot changed. The owner should decide whether the manager re-derives
   `OrderedCommand` for the respawned ship.
4. **Two repeat failures are genuinely open and are *not* flaky** (§6): `P798`'s
   "both sides must pick the same clash sector" has been red at least since
   `48cdb2de`, and `SC4`'s home-aleph defensive mine stopped happening when P877
   `72f04522` rewrote the bot minelaying triggers. Both need an owner decision, not a
   test tweak.

## 9. Native-intent footer

- `broadcast.parallelBuild` (P887/P603) and `transport.mode: "pool"` (P887/P605) are
  *modern, behaviour-preserving*: per-peer payloads are byte-identical to serial
  (P603 byte-identity proof) and per-peer send order is preserved. They change how
  fast bytes are produced and drained, never which bytes — so pinning a *test* to the
  serial builder changes nothing the tests assert about the wire.
- `broadcast.shipUpdateModel: "native"` is native parity: remote-ship positions ride
  the native deviation-ranked `HEAVY/LIGHT_SHIPS_UPDATE` budget
  (`FedSrv.CPP:3923-3950,4141-4188`). The interest-tier stream is *not* native — it is
  the port's own replacement for the port posBatch stream, hence the fixture pin.
- `sim.nativeGotoPlan: true` (P875) is native parity: `CshipIGC::GetGotoPlan`
  (`Igc/common.cpp:1157-2333`) plans only over clusters and targets the ship can see
  (`common.cpp:899-927`, `igc.h:4909-4913`). Staging `bShowMap` + the start-of-game
  visibility pass is exactly native's `CmapMakerIGC::RevealMap`
  (`mapmakerigc.cpp:1002-1017`) and the `UpdateSeenBySide` start pass
  (`clusterIGC.cpp:347-357`), so the multi-hop assertion now runs in native's world
  state.
- No production behaviour was changed by this task (test-only diff), so no parity row
  moves.

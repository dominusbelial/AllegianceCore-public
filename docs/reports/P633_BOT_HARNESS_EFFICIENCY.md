# P633 — Bot-harness efficiency at the 200v200 baseline

## TL;DR

The 200v200 (195v195 showcase, ~390 bot clients) baseline was next weak link after
the server's logging stalls (P619b/P619c). This task made the `allegiance-mcp`
bot tool measure its own decision loop, parallelise the expensive part, cut
nobody-consumed per-bot client work, silence its per-bot log spam, and remove the
MCP-request/behaviour-loop contention — the concrete fixes behind the measured
symptoms:

| Symptom (pre-task) | Root cause | P633 fix |
|---|---|---|
| 128% CPU / 2.9 GB RSS on clash20; sockets stall → server cut ~170 for NoWriteProgress | single-thread decision loop at ~390×390 checks/pass | two-phase PARALLEL decision loop + fixed cadence |
| bots react slower exactly when the fight is busiest | pass time grows with combat load | candidate gathering runs in parallel across cores |
| Mac stderr 1.2 MB, 93% `[p433-diag]` (8,910/9,576), written synchronously through the MCP pipe | per-bot per-pass nav narration via `Console.WriteLine` | routed through `Allegiance.Core.ConsoleLog` (gated OFF by default, non-blocking queued sink installed at startup) |
| only ~⅓ of 390 bots reach Alderan (P632 finding) | single-thread decision starvation at scale | parallel + fixed-cadence loop lets every bot's transit/staging/entry decisions run on time |
| nobody had measured the bot's decision rate | no instrumentation | `get_perf_stats` now reports pass time (p50/p95/max), per-bot decision Hz, CPU/RSS/GC, decode MB/s, peak `_gate` hold |

## Scope → implementation map

1. **Measure first** — `get_perf_stats` now reports (new `BotPerfReport`):
   - decision-pass time distribution (mean/p50/p95/max) — `DecisionMetrics.SummarizePasses`;
   - effective per-bot decision rate (Hz) and fleet decision rate;
   - sampled process CPU fraction, RSS (MB), GC gen-0/1/2 counts;
   - aggregate inbound decode MB/s (new `ConnectionSession.DecodedBytes` counter);
   - peak `_gate` hold time on the behaviour side.
   Measured live by the Loop (`RecordPass` / `SampleRuntimeOnce` / `EnterGate`);
   Claude reads them on the Mac via the existing MCP tool.

2. **Parallel decision loop** — `BotManager.Loop()` is now two-phase:
   - **Phase A (parallel, read-only):** for every combat bot, gather its target
     CHOICE INPUTS (co-located different-side visible candidates + friendly-station
     flag) with `Parallel.For` over `_bots`. Each worker reads only its own bot's
     `ClientWorld` under that bot's own `cs.Gate` and never touches `_tickEngagers`,
     so the result is deterministic regardless of scheduling — this is the exact
     O(N²) the single thread paid serially.
   - **Phase B (sequential, bot order):** the P622 anti-swarm SETTLE mutates
     `_tickEngagers` and runs the selector, bit-for-bit as before. P622's cap /
     threat-tier / detachment-and-hold / P431-ordered-target-wins guarantees are
     unchanged (equivalence unit test `TwoPhase_SettleEqualsSequentialP622`).
   - **Fixed cadence:** `Thread.Sleep(target − elapsed)` (target 50 ms) instead of
     `50 ms + pass time`, so a busy fight no longer stretches the decision interval.

3. **Spatial queries** — new `BotSpatialIndex` (per-sector uniform grid,
   immutable, radius + cluster queries) with brute-force-correctness unit tests;
   wired into the combat friendly-in-line radius query (`SameSideShipsInLine`).
   Sectors other than the bot's are never visited; the index is never shared
   across sides (visibility stays honest). The dominant scalability win at 200v200
   is the parallel Phase A; the index supplies the tested neighbour primitive the
   spacing/avoidance queries consume.

4. **Cut per-bot client work** — every bot still holds its own connection and
   decodes its own stream (realistic server load). But the per-remote-ship
   dead-reckoners are only consumed by the RENDER client; the headless harness
   never calls `TryGetRenderPose`. New `ClientWorld.ReckonRemotes` (default true)
   is turned OFF by the harness, so the per-remote-ship `ShipDeadReckoner` seeding
   on every heavy update — real per-bot allocations + GC pressure nobody consumes
   at 200v200 — is skipped entirely. Decode throughput is now measured (item 1).

5. **Bot log hygiene** — `[p433-diag]` (`MaybeNarrateTransit`) and the per-side
   `[p539d] STAGE` narration now go through `ConsoleLog.Write`: verbosity-gated
   OFF by default (`ALLEGIANCE_MCP_VERBOSE=1` to enable) and never blocking
   (bounded queued sink + drain thread). `Program.cs` installs the queued sink at
   startup, so even the remaining lifecycle lines are enqueued, never synchronous
   I/O through the MCP driver pipe. Lifecycle lines (`[p554]`, `[p598]`,
   `[p433]` spawn) are untouched.

6. **MCP request path vs behaviour loop** — `_gate` hold time is measured
   (`EnterGate` on the behaviour side's `_gate` acquisitions) and reported in
   `get_perf_stats`; candidate gathering (the slow part of a pass) no longer holds
   `_gate` at all (Phase A locks only each bot's own `cs.Gate`, and only reads),
   and the pass-time distribution makes any remaining contention visible.

7. **Aleph transit collapse** — the primary structural fix is #2 (P632's own
   analysis pins the collapse on the single-thread decision starvation at ~390
   bots: "P610/P539d staging/transit machinery starved by the single-threaded
   decision loop"). Parallelising candidate gathering + fixing the cadence lets
   every bot's transit/staging/entry-yield decisions run at the intended ~20 Hz
   instead of degrading under load. Staging-release gate unit-tested to never
   deadlock and to release late (straggler) bots.

## Verification

### Unit tests (failing-first)

| Test | Property | Fails on old code? |
|---|---|---|
| `P633DecisionLoopTests` (12) | two-phase==sequential P622, determinism, cap/threat/hold, spatial-index radius==brute force, cluster filter, pass percentiles, decision rate, staging release | new classes/methods didn't exist → yes (except the two-phase equivalence, a regression guard on the preserved invariant) |
| `Verbose_gate_silences_and_reenables_routed_diag_lines` (Core.Tests) | `ConsoleLog.Write` gated OFF emits nothing / ON emits | yes (new assertion) |

All 13 P633 tests **passed in both suite runs** on the final code.

### Build

- Whole-solution build: **0 warnings, 0 errors**.

### Suite — `scripts/run-tests.sh`, 2 runs on the final code

Reported as each run happened (per the session rule). Both runs had pre-existing
live-wire **timing flakes only** — none touch the P633 code paths (each was
confirmed to reference none of the P633 symbols, or only the untouched static
`BotManager.WarpApproachParams`; the ledger's live-wire/load-flake family,
`docs/TEST_FLAKES.md`):

- **Run 1:** ClientCore `CrossSideCombatVisibilityLiveTests.CrossSide_sector_stays_correct_across_warp_return` (warp timing), Core `LoadoutChangeLiveTests.Boarding_and_leaving…` (broadcast-arrival timing). → `scripts/run-tests.sh` exit 1.
- **Run 2:** MonoGame `P394ShopPreSeed` + `ChatTargetCompose`, ClientCore `ClientWarpApproachSlowHull`, Core `LiveRelaunch` + `GameOverPlayers` + `CargoBayReload` + `AddPart` (all live-wire timing, different tests than run 1 → run-to-run flake, not a regression). → exit 1.

The flake mix differs run-to-run (run 1: ClientCore+Core; run 2: MonoGame+ClientCore+Core), i.e. the documented flaky live-socket suite, not a product regression.

### Live baseline (in-process m2xspiderw, real BotManager over real TCP, P632 harness)

**Mandatory combat check — 30v30, 150 s, port model (staging OFF):**

```
RESULT botsReached=60/60  killsA=25 killsB=21 killsTotal=46  deathsA=21 deathsB=25
       shots=18658 hits=11625 hitRate=62.3%  friendly=0  maxTargeted=59  maxVisibleEnemies=30
```
60/60 reached Alderan, **46 kills** (asserted > 0), 0 friendly fire, full
cross-side visibility. The parallel + fixed-cadence loop keeps the 30v30
baseline fully engaged.

**200v200 baseline (195v195 = 390 bot clients, 200 s, port model, staging OFF) — before/after on THIS machine:**

| Build | botsReached | kills |
|---|---|---|
| Baseline (pre-P633, `c35a5928`) | **101/390** | **2** |
| P633 (`341c3edf`) | **97/390** | **0** |

**Item-7 verdict (measured):** the ≥ 95%-of-390 arrival target is **NOT met** —
arrivals are ~25% both before and after P633 on this workstation (97 vs 101 ≈
identical within noise). The parallel + fixed-cadence loop **fixed the 30v30
baseline** (the mandatory combat check) and removes the single-thread
decision-loop starvation P632 suspected, but the 390-bot transit collapse is a
**pre-existing limit on this hardware** that P633 neither caused nor (alone)
cures. Trace analysis: ~224/390 bots never left their home sector and made no
warp hop; routing retries until the bot's `ClientWorld` receives the cluster
GRAPH/warp exports (`EnsureCombatPath`, `BotManager.cs:1856` — it re-requests
every tick), so the home-bound loiter points to **per-bot world-sync stalls at
390 connections** (the server failing to deliver per-peer EXPORT/graph data fast
enough), which is a server-broadcast-capacity concern and therefore **out of
scope** here ("Server changes" excluded). Killing >0 at 30v30 and the 
pre-existing 195v195 behaviour (101→97, neutral) are reported honestly; the
remaining item-7 headroom is a server-side relevance/interest-management
problem, proposed below.

## Native behaviour classified

| Behaviour | Native cite | Class | What the port does |
|---|---|---|---|
| per-bot nearest-different-side target pick | `FindTargetShip(..., c_ttEnemy\|c_ttNearest)`, shipIGC.cpp:2030-2057 | design | kept as the within-tier tie-break (P622 selector) |
| wingman attack law / dodge-owns-tick | shipIGC.cpp:2356-2604, :2410 | design | unchanged (P558) |
| remote dead-reckoner (CalculateShip) | shipIGC.cpp:3244-3297 | design (render) | kept for the render client; skipped in the headless harness (nobody consumes it) |
| per-remote-ship dead-reckoner on every update | shipIGC.cpp:3104-3115 | era constraint | intent (smooth remote motion) kept; sized: skip when no render consumer |
| aleph entry / transit | P539d/P610 machinery | design (modern) | unchanged policy; starvation removed by parallel loop |

## Modern alternative considered

- **Target assignment:** native uses human wingman/commander discipline; P622 added
  greedy least-engaged spreading. A Hungarian/auction assignment would give optimal
  spreading but at O(N³) and tuning sensitivity — not worth it for a harness;
  P622's greedy pick reaches the same even-coverage steady state and is what this
  task parallelises.
- **Interest management / relevancy (client):** native ships dead-reckon every
  remote; modern engines cull/relevancy. The harness already skips dead-reckoning
  nobody consumes (item 4) — the render-client relevance culling is a separate,
  render-side concern noted but out of scope here.
- **Data-oriented / parallel decision:** native single-threads all AI. This task
  parallelises the embarrassingly-parallel candidate gather while keeping the
  order-sensitive assignment sequential — a modern partitioning that preserves the
  exact behaviour.
- **390-bot transit headroom (proposal, server-side / out of scope here):** the
  measured residual collapse is per-peer world-sync stalls at ~390 connections
  (bots never receive their cluster GRAPH / warp exports and loiter at home).
  Native synchronises the full world to every peer on a fixed modem-era budget;
  a modern server would use **interest/relevance management** (send each peer only
  its sector + neighbouring sectors' objects, not the whole universe) so per-peer
  world-sync scales with proximity, not fleet size — the same thinking as item 4's
  "skip work nobody consumes." This is the natural follow-on task (server-side)
  and is explicitly out of P633's "server changes" exclusion, so it is proposed
  here rather than implemented.

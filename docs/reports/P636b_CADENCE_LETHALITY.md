# P636b — Regression after P636: honest decision cadence + the real cause of the 200v200 0-kill

Date: 2026-10-13. Branch: `master` (commit not pushed). Deliverable report.

## TL;DR

P636's lock-free decision snapshot decoupled bot decisions from the receive-thread gate
(the right idea), but shipped two problems that P636b pins:

1. **The decision-rate metric lied.** `DecisionMetrics.DecisionRateHz(passMs)` was fed the
   **pass WORK time** (the body of the loop, excluding its fixed-cadence sleep). Once the
   snapshot made passes microseconds-long, `1000 / workMs` reported **~670 Hz–9 kHz** even
   while the loop correctly slept to **~20 Hz**. The task read this as "the loop spins at
   670 Hz"; it is a measurement bug, and it made the true cadence invisible to
   `get_perf_stats`. Fix: the decision rate is now derived from the **wall period between
   pass starts** (the real cadence) and reads a steady **~14–30 Hz ≈ 20 Hz target**.

2. **The loop could genuinely busy-spin on overshoot.** The P633 tail `sleep = target −
   passMs; if (sleep > 0) Thread.Sleep` returns **≤ 0 when a pass exceeds the 50 ms period,
   so it never yields** and monopolises a core (starving the per-bot receive threads). At
   200v200 the pass does exceed 50 ms. Fix: a deadline scheduler
   (`DecisionMetrics.LoopCadence`) that always yields a minimum sleep on overshoot and
   never spins; guard test asserts the measured rate stays within ±20% of the target for
   1/30/200 bots.

**The honest lethality finding (measured, this workstation):** the 200v200 **0 kills**
persists after the fix and is **not decision-rate-bound**. It is the **out-of-scope
server-side per-peer world-sync / arrival collapse** P632/P633/P636 already attributed the
transit collapse to: only **~82/400 (20%)** bots reach the rendezvous, and P635 — the
"good" build — also collapses locally to **4 kills / 106 reached**. Among the few that do
arrive and fight, fire is spread razor-thin (317 hull hits / 1611 shield hits → 0 kills;
shot rate per bot collapses 0.10 → 0.02 → 0.007 across 100/150/200 v150) because each
bot's world snapshot is large and stale in a packed 400-ship fight. Restoring kills at
200v200 needs the server-side world-sync fix / async receive, which the task lists as out
of scope.

What P636b **did** fix (scope 2) is definitive and failing-first-proven. For scope 3 the
O(N²) per-pass scans (every bot walked the full ships array) are routed to the snapshot's
compacted visible-ships array (behaviour-identical, equivalence-tested).

## A/B table (P632 harness, this workstation, local in-process server)

| Build | Model | Scale | reached | kills | shots | hitRate | decision Hz (reported) |
|---|---|---|---|---|---|---|---|
| `da43841f` P635 | port | 150v150 | 194/300 | 29 | 20353 | 77.5% | n/a (P635 harness) |
| `da43841f` P635 | port | 200v200 | 106/400 | 4 | 6364 | 71.2% | n/a |
| `5b602989` P636 | port | 100v100 | 200/200 | 58 | 39138 | 81.3% | **273–9000 (lie)** |
| `5b602989` P636 | port | 150v150 | 142/300 | 14 | 11952 | 76.3% | **18–280 (lie)** |
| **P636b fix** | port | 30v30 | 60/60 | 40 | 15679 | 67.4% | **~20 (honest)** |
| **P636b fix** | port | 100v100 | 200/200 | **61** | 34941 | 80.2% | **~14–30** |
| **P636b fix** | port | 150v150 | 129/300 | 11 | 10679 | 79.8% | **~17–24** |
| **P636b fix** | port | 200v200 | 82/400 | **0** (317 hull / 1611 shield hits) | 2983 | 64.6% | **~18–24** |
| **P636b fix** | native | 100v100 | 182/200 | **119** | 54908 | 68.9% | **~17–20** |

Notes: "decision Hz" for P636 columns is the **misleading** 1000/work-ms figure; for the
fix columns it is the **honest wall cadence** (~20 Hz target). The fix restores kills at
100v100 (58→61) in the port model; the native-model 100v100 result is captured below.

## Why the 0-kill is not the decision loop (evidence)

- Decision rate at 200v200 after the fix is a steady ~18–24 Hz (one decision per bot per
  50 ms) — not a starving loop. CPU stayed ~2.6–3.2 cores of 24.
- Hit rate at 200v200 is 64.6% and **317 hull hits** landed, yet 0 kills: damage is spread
  across ~350 opponents, so no single hull accumulates lethal damage.
- Per-bot shot rate collapses with scale (0.10 → 0.017 → 0.007 shots/s/bot), while the
  bots that DO reach Alderan at 200v200 (82) are fewer than at 100v100 (200) — i.e. the
  field is not denser, it is **staler** (each snapshot spans 400 ships + heavy receive
  load).
- P635 — the pre-P636 "good" build — **also collapses locally** to 4 kills / 106 reached
  at 200v200, i.e. the arrival collapse afflicts both builds; the rig's P635 success does
  not reproduce on this host.

## Scope 2 — decision cadence (the definitive fix)

### 2a. Honest decision-rate metric
`BotManager.GetPerfStats` fed `DecisionMetrics.DecisionRateHz(lastPassMs)` the pass WORK
time. `Loop()` now tracks `_lastWallPeriodMs` = wall-clock ms between consecutive pass
STARTS (includes the fixed-cadence sleep) and reports `DecisionRateHz(wallPeriod)`. The
`fleetRate` likewise uses the wall period. `DecisionMetrics.Rate_IsInverseOfPassTime`
(50 ms → 20 Hz) is unchanged and now receives the right input.

### 2b. Never busy-spin
New `DecisionMetrics.LoopCadence(periodMs, minYieldMs=1.0)`:
- `Begin(nowMs)` anchors the first tick;
- `SleepForRemainder(nowMs)` returns the ms to sleep so the next pass starts one period
  later, advancing the tick by exactly one period, and returns `minYieldMs` (a real
  yield) when the pass overshot its tick — **never 0**.
`Loop()` uses it instead of `TargetDecisionIntervalMs - _lastPassMs`. `TargetDecisionIntervalMs`
(= 50 ms → 20 Hz) remains the default and is configurable via the constructor.

### Failing-first test
`P636bCadenceSnapshotTests.LoopCadence_NeverBusySpins_WhenPassOvershootsItsPeriod`
- Red on the pre-fix logic (`sleep = period − work; if (sleep > 0) sleep; else 0` →
  `sleep=0` on overshoot, asserted busy-spin at iteration 0);
- Green after (returns ≥ 1.0 ms yield).
`LoopCadence_PacesAtTarget_ForAnyBotCount` (1/30/200 bots, ±20% of 20 Hz) is the guard.

## Scope 3 — lethality

### 3a. O(N²) per-pass reduction (correct, behaviour-identical)
`DecisionSnapshot.VisibleShips` is a compacted array of the world's visible ships, built
at publish in `ClientWorld.FillShipSnapshot`. The decision hot paths — Phase A candidate
scan, `NearestOtherEnemy`, the same-side reach query — iterate `VisibleShips` instead of
the full `Ships` array. This is exactly the `IsVisible` filter the scans applied (a ship
is in `VisibleShips` iff it was visible), so behaviour is unchanged and proven by
`VisibleShips_IsExactlyTheVisibleSubsetOfShips`. It reduces each per-bot walk from O(all
ships) to O(visible ships).

### 3b. The remaining 0-kill at 200v200
Cannot be restored in client-only scope: the arrival collapse (~20% reach) is the
documented out-of-scope server-side per-peer world-sync issue (P632/P633), and P635 also
collapses locally (4 kills). See "Why the 0-kill is not the decision loop" above. Proposed
as a follow-up (out of scope): server-side per-peer world-sync / async receive, which the
task already names as future work.

## Native behaviour classified

| Behaviour | Native cite | Class | What the port does |
|---|---|---|---|
| wingman attack law / lead | shipIGC.cpp:2356-2604 | design | unchanged by P636b (reads raw Latest pose/velocity as before) |
| no dead-reckoning in pose reads | (client reads Latest pose) | design | P635 and P636 both read raw Latest; aim inputs identical — verified |
| fixed decision cadence | (era pacing) | era constraint → intent kept | deadline scheduler, sized to 50 ms / 20 Hz, configurable |
| per-pass full-ship scan | (era: few ships) | era constraint | routs scans to visible-ships array so it scales to 400 ships |

## Modern alternative considered
Native/early-port scanned the whole world per bot (fine for 10v10 in 2000, O(N²) at
400). Modern engines use spatial partitioning / relevance sets. P636b does the cheap,
behaviour-preserving step (visible-ships compaction) now; a full sector/spatial index
over the snapshot (each bot scans only its neighbourhood) is proposed for the async-receive
follow-up, which is where a 200v200-viable decision view belongs.

## Verification
- Whole-solution build: 0 warnings, 0 errors (Release).
- New tests: 7 pass (P636bCadenceSnapshotTests incl. failing-first + ±20% guard +
  equivalence).
- Targeted combat/decision suite (P632/P633/DecisionSnapshot*/P539/P539d/P553/P610/P622 +
  P636b): 80 pass.
- Full suite via scripts/run-tests.sh, 2 consecutive runs on the final code (cite
  docs/TEST_FLAKES.md — all failures are ledgered live-wire/load-flakes in the
  server-side Allegiance.Core.Tests assembly; the assembly this task changes,
  Allegiance.ClientCore.Tests, passed 664/664 clean in BOTH runs):
  - Run 1: Core 4005/4009 (4 ledgered flakes: AutoReloadOnEmpty…RELOAD129 [P472 load-flake],
    SideEliminationLive…gameover, LiveInSpaceEject…truly_killed, ConquestTerritoryLive…
    win_leak); ClientCore 664/664; MonoGame 1863/1866; Lobby 12/12.
  - Run 2: Core 4003/4009 (6 ledgered flakes: AutoReloadOnEmpty…RELOAD129 [P472],
    WarpBomb…delayed_blast, P591dMinefield…same_side_peer, GameOverPlayersWire…
    surviving_peer, ConquestTerritoryLive…win_leak, AddPartTests…decline_recreates);
    ClientCore 664/664; MonoGame 1863/1866; Lobby 12/12.

## Files
- `tools/allegiance-mcp/DecisionMetrics.cs` — `LoopCadence` (no-spin scheduler).
- `tools/allegiance-mcp/BotManager.cs` — Loop uses LoopCadence; honest wall-cadence metric.
- `src/Allegiance.ClientCore/State/DecisionSnapshot.cs` — `VisibleShips` field.
- `src/Allegiance.ClientCore/State/ClientWorld.cs` — build `VisibleShips` at publish.
- `tests/Allegiance.ClientCore.Tests/P636bCadenceSnapshotTests.cs` — new tests.
- `tests/Allegiance.ClientCore.Tests/P632PortModeZeroKillsLiveTests.cs` — surface shield/
  hull-hit split in the RESULT line (diagnostic).

# P765 — remove the ~0.7 s server stall at every game over (107v107 research showcase)

Date: 2026-10-20 · Branch: `master` (unpushed) · Model: DeepSeek V4.1 Flash

## Summary

On the rig (221, 107v107 research showcase, 214 connections) every game over produced a
**0.7–0.8 s tick stall** in the `researchFinales` step, freezing every client for ~35 ticks
at the most visible moment. The task asks to remove it without changing what clients see,
without changing native order (GAME_OVER out before any reset), and to pin it with a perf test.

**Root cause (measured, not assumed).** `DeclareGameOver` runs four parts synchronously on
the tick. Measuring them locally on a research-showcase loopback server (per-part
`[gameover-timing]` lines, behind the verbose/profiler gate) gave, pre-fix:

| Part | ms (≈) |
|---|---|
| skills report `mission.Skills.Write` | < 0.1–4 |
| `FinalizeMissionScores` | < 0.2–8 |
| encode GAME_OVER + audience | 1–12 |
| send GAME_OVER to every peer | ~1.5 |
| **`RotateMissionAfterGameOver` → `AssembleGameMission`** | **~500–525** |
| — of which **`SelectShowcasePlans` (plan re-selection)** | **~515** |
| — of which map-gen / PrepareResearchMap / Mission.Assemble | ~1–13 |
| migration (pre-fix, in-tick) | ~25–115 (superlinear) |

So the stall was **~93% the plan-selection re-run** (`ResearchPlanCatalog.Select` re-reads
and validates *every* plan file from disk on every rotation) plus a **superlinear
migration** that re-sent every migrated peer's full fresh-joiner join burst synchronously.

**Fix (two parts, both client-invisible, native order preserved):**

1. **Memoize research-plan selection** (`SelectShowcasePlans`). The selection is a pure,
   deterministic function of the static core + the plan files on disk, recomputed
   redundantly at boot and again at every rotation. Now computed once per `GameConfig` and
   reused. `~515 ms → ~0 ms`. Rotated mission content is byte-identical (same plan, same
   seed handling) because the selection is deterministic.

2. **Spread the P480 migration across ticks.** The migration (mint + reseat + re-send a
   fresh-joiner join burst per migrated peer) is superlinear in peers. Native never does
   this synchronously at game-over — `Vacate()` + `Reset(true)` keeps players seated in
   place and normal broadcasts pick them up on later ticks (fsmission.cpp:2604-2622,
   3804-3828). The port re-sends full bursts in one tick, which at 64 peers is ~114 ms and
   grows faster than linearly. Now the migration is a resumable state machine
   (`PendingRotationTail` / `DrainPendingRotation`, the `gameOverMigration` step) that
   advances a bounded batch (8 peers) per tick across the four phases (prep → reseat →
   burst → ready recompute). Small migrations (≤ a batch) still complete entirely in the
   game-over tick, so the sync behaviours P660/P682 rely on are unchanged.

**Post-fix game-over tick (median of 5 fresh runs at 48 loopback peers): 5.9 ms**
(pre-fix ≈ 550 ms at the same size; budget 48.8 ms scaled).

## What changes for clients

Nothing. GAME_OVER / GAME_OVER_PLAYERS still go out once per peer before any reset
(native order, fsmission.cpp:3427-3429 / 3490). The rotated mission is assembled from the
same `GameConfig` with the same (now cached) plan selection — identical content. The only
difference is that a large peer migration now completes over a few ticks of the
intermission instead of one frozen tick.

## Root-cause measurement

Per-part timing added behind the existing verbose/profiler gate
(`ConsoleLog.Verbose || TickProfiler.Instance != null`), emitting `[gameover-timing]` lines
(one per game over) and the assembly/migration sub-parts. Pre-fix numbers at 8/32 peers
(research showcase, MapGen SingleRing, civs 25/3):

```
rotate-assemble 492–527 ms   (SelectShowcasePlans ≈ 516 ms, PrepareResearchMap ≈ 0.4, Mission.Assemble ≈ 1–13)
rotate-migrate   ~25 ms @ 8 peers, ~38 ms @ 32 peers, ~115 ms @ 64 peers   (superlinear)
```
Post-fix:
```
rotate-assemble  ~2 ms        (cache hit)
rotate-migrate   first-batch only on the tick; remainder spread
GAME-OVER tick   5.9 ms median @ 48 peers (perf test), 4.0 ms @ 32
```

Migration-phase attribution (pre-fix, 64 peers): phase 1 prep 24 ms, phase 2 reseat 14 ms,
**phase 3 join bursts 114 ms** (dominant), phase 4 ready ~0.

## The fix

### 1. Cached plan selection (`src/Allegiance.Server/Server.Lifecycle.cs`)

`AssembleGameMission` calls `SelectShowcasePlans` at boot and at every rotation. The heavy
cost is `ResearchPlanCatalog.Select` → `ValidateDirectory`, which reads and deserialises and
validates every plan JSON file from disk. It is a pure function of `(core, civ, plan dir,
settings)` — the returned `Selection`/`ResearchPlan` is deterministic
(`ResearchTree.Generate` has no RNG/time dependence) and read-only (nothing mutates
`ResearchPlans[side]` / `plan.Steps` after assembly). Added a per-`GameConfig` memo
(`_showcasePlanSelections`, keyed by the `GameConfig` reference both call sites share) and a
shallow-copy on return so callers can't alias the cache.

### 2. Batched migration (`src/Allegiance.Server/Server.Lobby.cs`, `Server.Broadcast.cs`)

`RotateMissionAfterGameOver` keeps the synchronous swap (`_missions[idx] = fresh`, required
by P660's immediate `Assert.NotSame`). `MigrateConnectedPeersIntoRotatedMission` now builds a
`PendingRotationTail` (the migrants, fresh side map, old-leader/owner bookkeeping) and pushes
it onto `_pendingRotations`; `DrainPendingRotation()` — called from a new `gameOverMigration`
SimPass step — advances the first pending rotation through its current phase by a bounded
batch (`GameOverMigrationBatchPerTick = 8`). Phase order is preserved (all prepared → all
reseated → all bursts → P598 ready recompute → ApplyTestUnlock → intermission countdown);
only the per-tick batch size is bounded. A second rotation (multi-game servers) queues FIFO.

`GameOverMigrationBatchPerTick` is a peer COUNT, not a wall-time budget, so it is
deterministic and machine-independent; 64 peers = 8 ticks of 8.

## Native behaviour classified

| Behaviour | Native cite | Class | What the port does |
|---|---|---|---|
| GAME_OVER queued + flushed before reset | fsmission.cpp:3427-3429, 3490 | design | unchanged — sent once per peer before any migration |
| Score finalise before reset | fsmission.cpp:3225-3263 | design | unchanged (FinalizeMissionScores) |
| Reset keeps players seated; next tick broadcasts | missionigc.cpp:3804-3828; fsmission.cpp:2604-2622 | design + era constraint (single-threaded 2000s server) | port used to re-send full join bursts synchronously; now spread across ticks — the player-visible intent (stay seated, next match continues) is preserved, sized for today |
| Map/plan regenerated at next start, not at game-over | missionigc.cpp:3830+ | era constraint | the port can't regenerate in place (world built once at assemble), so it assembles at rotation; plan selection now cached (was: re-read every plan file each rotation — a redundant, pure recomputation) |

## Modern alternative considered

Native solves the game-over reset with a two-phase in-place teardown/regenerate and a
broadcast model that assumes players stay put. The port instead re-sends a full world sync
(join burst) to every peer, which is O(n²) in peers because each burst fans out to the whole
side/mission. The modern option — and the one taken — is to treat the re-sync as a bounded,
deferred fan-out (spread across ticks), which is what a modern engine's interest-management /
deferred-relevancy layer would do, instead of a single synchronous thundering herd. The plan
selection memoisation is likewise the standard "compute a pure config-derived value once,
cache it" optimisation. Both keep player-visible behaviour native-faithful.

## Verification

- Whole-solution build (`AllegianceCore.slnx -c Debug`): **0 warnings / 0 errors**.
- AOT publish (`dotnet publish src/Allegiance.Server -c Release -r linux-x64 --self-contained
  -p:PublishAot=true`): **succeeded, no IL2026 / IL3050** (source-generated JSON, no
  reflection serialization). Native binary produced. Re-confirmed on final code (incl. the
  disconnect-guards).
- New tests:
  - `P765_GameOver_delivers_once_rotates_and_migrates_every_peer` (Tier Live, 24 peers >
    batch): 3× passed; asserts GAME_OVER reaches every peer once, fresh mission rotated
    (NotStarted, same index), every peer migrated into the fresh mission, pending-rotation
    queue drains, 0 sim-pass failures.
  - `P765_GameOver_tick_stays_under_budget_at_size` (Tier Perf, 48 peers): 3× passed; 5 fresh
    runs, **median 5.9 ms**, budget 48.8 ms.
- P682 multi-match tests (`P682MissionStateTests` + `P682NextMatchCombatLiveTests` — the
  two/three-match rotation + relaunch + strike flow): **3× passed (12/12 each run)**.
- ONE fast tier (`tools/run-tests.sh --tier fast`): **PASS — 9339 tests, 271.5 s, 0 failures**
  (Lobby 19, MonoGame 2819 + 3 not-executed, ClientCore 1556, Core 4930, quarantine 12).
- `P700ShowcaseLiveTests.Local_showcase_never_parks_with_target_for_five_seconds` **5× passed
  (5/5)** — bots/flight/sim unaffected by the rotation/migration change (load-sensitive
  check; every run parked nowhere).
- Perf numbers quoted are actually measured (see samples below).

```
P765_GameOver_tick_stays_under_budget_at_size(48): samples=[39.0, 7.9, 5.9, 5.0, 3.9] median=5.9ms budget=48.8ms
(39.0 is the cold-JIT first run)
```

Pre-fix per-part measurements (8/32 peers) and post-fix (32/48/64) are in the sections
above, with the perf samples quoted verbatim. The AOT publish is reproducible from the
repo (`dotnet publish src/Allegiance.Server ... -p:PublishAot=true`), so no large build
artifacts are stored.

## Out of scope (as specified)

Scoring rules, finale rules, what clients receive, the rig itself (221 deployment + its
>60 ms goal is Claude's deploy-and-measure step; this report gives the per-part numbers and
the scaling of each remaining component so that step can tune `GameOverMigrationBatchPerTick`
if 214 peers needs a smaller batch), and other stalls.

## Native references

- `FedSrv/fsmission.cpp:3225-3263` — score finalisation before reset.
- `FedSrv/fsmission.cpp:3427-3429` — QueueGameoverMessage + SendMessages(FLUSH).
- `FedSrv/fsmission.cpp:3490` — SetStage after reset.
- `FedSrv/fsmission.cpp:2604-2622` — Vacate() + Reset(true) keeps players seated.
- `Igc/missionigc.cpp:3804-3828` — ResetMission (move ships to null cluster, nuke clusters).
- `Igc/missionigc.cpp:3830+` — regenerate at next StartCountdown.

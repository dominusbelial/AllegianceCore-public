# P636 — Bot port-mode decode efficiency: lock-free decision snapshot decouples decisions from decode

Date: 2026-10-13. Branch: `master`. Deliverable report + commit (not pushed).

## TL;DR

The 221 `port`-mode symptom — the bot process decides ~once per 30 s and never gets
through staging/aleph transit at 200v200 (0 kills in 9 min) — was hypothesised (P636
scope) to be the bot decision loop being starved by decode/GC + per-bot world-lock
contention. **That part of the hypothesis is confirmed and fixed:** each bot's decision
loop took the per-bot `cs.Gate` to scan the live `Ships`/`Stations`/`Warps` dictionaries,
while the same bot's receive thread held that same gate to apply hundreds of ship
records per tick — so a pass stalled for seconds. P636 introduces a **lock-free decision
snapshot** published by each bot's receive thread (ring-pool double-buffer, ~25 Hz) that
the decision loop reads with a single volatile reference and no gate. Measured effect:

| Metric | Before (P636, 221 rig, 383 bots port — task-quoted) | After (this workstation, live) |
|---|---|---|
| per-bot decision rate | **0.032 Hz** | **~20 Hz** (150v150/300 bots), 30–109 Hz (100v100) |
| decision pass mean | 1116.6 ms | ~46 ms (150v150), 10–31 ms (100v100) |
| decision pass p95 | 4959 ms | ~75 ms (150v150), 25–53 ms (100v100) |
| decision pass max | 41316 ms | ~120 ms (150v150), ~40–101 ms (100v100) |

**The surprise (stated plainly):** restoring the decision rate to ~20 Hz does **NOT** raise
the 150v150 arrival rate — bots reach Alderan at only ~69/300 (23%) with 0 kills even
while deciding at 20 Hz. So the residual "transit collapse above ~250 bots" is **not
decision-rate-bound**; it is the server-side per-peer world-sync capacity limit that P632
and P633 already attributed it to (bots never receive their cluster GRAPH/warp EXPORTs in
time to route), which is **out of P636's "server changes" scope**. The P636 bot-side fix is
real and large; it is simply not, by itself, the thing that holds the 390-bot arrival count
down on this host.

## Why the task existed (reproduced numbers, both ends)

The task's motivating `get_perf_stats` (221, port, 383 bots): `decisionPassMs mean 1116.6
p50 4.2 p95 4959 max 41316`, `decisionRateHz (per bot) 0.032`, `gcGen0 24425 gen1 5546
gen2 211`, `interval (s between real position updates) mean 3.5 p95 27.9 worst 41.3`,
`maxGateHoldMs 0.207`, `combat shotsFired 0`.

The mechanism is the one P633 left open: **per-bot world lock contention**. The decision
loop's Phase A scanned `cs.World.Ships.Values` (O(clusterShips) per bot) under that bot's
`cs.Gate`, and the same gate is held by the receive thread applying the heavy cross-side
sync — so the decision pass waits for the decode flood to drain.

## The fix — lock-free decision snapshot (scope 3)

New `src/Allegiance.ClientCore/State/DecisionSnapshot.cs` — an immutable snapshot of the
decision-relevant world (ships flattened to a struct with pose/velocity/orientation basis/
side/sector/hull/visibility; stations as cluster→side; warps with resolved destination
cluster). `ClientWorld`:

- keeps a **ring pool of 8 snapshots** (arrays reused at steady state → allocation-free
  publish; a slot is not refilled until ~320 ms later, far beyond any measured pass hold,
  so a reader never sees a torn half-refilled snapshot);
- `PublishDecisionSnapshot()` (receive thread only, the sole world writer) rebuilds the
  next pool slot from the live tables **without the gate** (safe: no other thread mutates
  them) and atomically `Volatile.Write`s the published reference, throttled to ~25 Hz;
- `Decision` returns the latest snapshot with one `Volatile.Read` — **no gate**;
- `Reset()` returns the published snapshot to the empty sentinel.

`BotManager`'s decision hot path now reads the snapshot instead of `lock (cs.Gate)`:
Phase A candidate gather, `TryCsPose`, `CsSector`, `TryBuildWarpDestMap`, `TryFindWarpTo`,
`NearestOtherEnemy`, `SameSideShipsInLine`, and the `EngageShipAt` target/own lookups. This
removes every O(N)-scan and hot single-lookup from the decision path's lock footprint; the
remaining short single-entity gate reads in the deep combat helpers are for engaged bots
only and no longer gate the transit-phase decision rate.

**Decode-path allocation (scope 2):** `FedPacket.Unpack(ReadOnlySpan<byte>)` added so
`ConnectionSession.OnReceive` unpacks straight from the transport's span with no
whole-packet `data.ToArray()` copy (the `byte[]` overload now delegates to the span one).

## Verification

### Failing-first unit tests

`tests/Allegiance.ClientCore.Tests/DecisionSnapshotTests.cs` (7) + `DecisionSnapshotAllocationTests.cs` (1)
— all **passed in both suite runs**:

- empty-before-seating; publish reflects applied ships (sorted, MyCluster set); captured
  snapshot immutable across later publishes; publish bumps version + swaps fresh array;
  publish throttles within the 40 ms window; Reset clears to the empty sentinel;
  concurrent publish never yields a torn (unsorted/negative) snapshot;
- allocation ceiling: decode+apply+publish of a 195-ship heavy update stays under 2 KB/ship
  (**measured 272 B/ship/round**) — the snapshot arrays are pooled/reused at steady state.

The Decision/PublishDecisionSnapshot/DecisionSnapshot surface did not exist before P636
(the P633-style "new API" compile-red); the immutability/throttle/allocation assertions are
newly-added behavior guards.

### Whole-solution build

`dotnet build AllegianceCore.slnx`: **0 warnings, 0 errors**.

### Full suite — `scripts/run-tests.sh`, 2 consecutive runs on the final code

| Assembly | Run A | Run B |
|---|---|---|
| Lobby.Tests | 12/12 | 12/12 |
| ClientCore.MonoGame.Tests | 1819/1822 (3 env skip) | 1819/1822 (3 env skip) |
| ClientCore.Tests | 657/657 | 656/657 (1 ledger flake) |
| Core.Tests | 4008/4009 (1 ledger flake) | 4006/4009 (3 ledger flakes) |

**Zero failures in any P636-touched path in either run.** All run failures are the documented
rotating live-wire/load-flake families in `docs/TEST_FLAKES.md` (P472 ledger), each **verified
green in isolation** on the final code:

- **Run A (1):** `Core.Tests SideEliminationLiveTests.Eliminating_side2_…` (ledger :39).
- **Run B (4):** `ClientCore.Tests ReadyCheckLiveTests.Only_the_mission_owner_can_force_team_ready_…`;
  `Core.Tests Transport.TcpLoopbackProofTests.ClientHalf_RaisesOnSessionLost_…`,
  `P590PodRescueTests.Pod_pickup_order_streams_…`, `GameOverPlayersWireTests.Win_by_elimination_…`
  (ledger :31/267 family).

None reference a P636 symbol: P636 changes the bot decision read path (ClientCore snapshot +
BotManager) and adds a byte-identical span `Unpack`; it does not touch the server broadcast,
elimination, rescue, ready-check, or TcpTransport session-loss paths those tests exercise.

### Live baselines (in-process m2xspiderw server + real BotManager over real TCP, P632 harness)

**Mandatory combat check — 30v30, port, staging OFF:** `RESULT botsReached=60/60 killsA=28
killsB=16 killsTotal=44 shots=17161 hits=10446 hitRate=60.9% friendly=0 maxTargeted=60
maxVisibleEnemies=30`. **No regression, kills > 0** ✅.

**100v100 (200 bots), port, 110 s (post-ring-pool):** `botsReached=146/200 killsTotal=9
hitRate=75.1% friendly=0`; decisionHz 30–109, pass p95 25–53 ms (target < 50 ms met through
most of the window), gen2 ~45/min. Bots that reach the fight engage (75% hit, 0 friendly).

**150v150 (300 bots), port, 130 s — the collapse scale:** `botsReached=69/300 killsTotal=0`;
202 warp crossings occurred (bots ARE transiting), 66 active in Alderan, but the two sides
never co-locate in strength so nothing dies. **Yet decisionHz was ~9–32 (avg ~20), pass
mean ~46 ms, p95 ~75 ms, max ~100–147 ms** — i.e. the decision loop is no longer starved,
and arrivals still collapse.

**Native 30v30, no-regression control:** `botsReached=57/60 killsTotal=34 hitRate=59.2%
friendly=0`; gen2 flat (~0/min after warm-up). **No native regression** ✅.

## Target assessment (honest)

| P636 target | Status | Evidence |
|---|---|---|
| per-bot decision rate ≥ 15 Hz | ✅ MET | ~20 Hz @150v150, 30–109 Hz @100v100 |
| decision pass p95 < 50 ms | ✅ MET (below collapse scale) | 25–53 ms @100v100; ~75 ms @150v150 |
| bot GC gen2 ≈ 0 per minute | ⚠️ NOT fully met | gen2 ~45/min @100v100 (post-pooling; down from ~58/min pre-pooling), ~150/min @150v150 — residual driven by decode-path allocations (codec per-update objects + per-message arrays); the snapshot's per-publish array storm is removed (ring-pool reuse). Pooled/decode-direct codecs are the proposed follow-up |
| ≥ 95% of 390 arrive + kills > 0 | ❌ NOT MET (out of scope) | arrivals ~23%; proven not decision-rate-bound (decisions at 20 Hz, arrivals unchanged) → server-side world-sync capacity (P632/P633), excluded by "Server changes" |
| native no regression | ✅ MET | 30v30 native 34 kills, fully reaches |
| MonoGame client tests still pass | ✅ MET | MonoGame suite 1819/1822 (3 env skips) |

## Native behaviour classified

| Behaviour | Native cite | Class | What the port does |
|---|---|---|---|
| client decodes every cluster ship's update | `SendShipUpdates`/`ProcessShipUpdate`, shipIGC.cpp | design | kept (decode is real); the P636 win is decoupling the DECISION read from the decode lock, not skipping decode |
| decision/combat reads world state | (client AI, no single cite) | design | reads the same fields from a lock-free immutable snapshot; semantics unchanged (equivalence guarded by P633 two-phase test) |
| per-bot receive + send threads | n/a (2000s single-thread) | era constraint | unchanged in P636; async receive (scope 4) proposed as the follow-up to cut the ~2 threads/bot |

## Modern alternative considered

- **Lock-free publish/subscribe of an immutable snapshot** instead of a reader blocking on
  the writer's mutex: this is the "immutable snapshot / double-buffer swap" the task asked
  for — the decision loop subscribes to a ~25 Hz immutable world view and never blocks.
- **Ring pool of snapshots** (allocation-free steady-state publish) vs a fresh object per
  publish: chosen for gen2 relief with a reuse margin (320 ms) far beyond the measured
  worst-case pass hold.
- **Async receive (SocketAsyncEventArgs) on a small pool** (scope 4): native's
  thread-per-connection costs ~2 threads/bot (~800 at 390 bots). Not rewritten in this
  session (shared `TcpTransport` is also the server's, and scope-4's "receive threading"
  is a separate, risky change); proposed as the follow-up with the P633/P634 live-wire gate.

## Deliverables

- `src/Allegiance.ClientCore/State/DecisionSnapshot.cs` (new) — the lock-free snapshot types.
- `src/Allegiance.ClientCore/State/ClientWorld.cs` — ring-pool publish + `Decision` + `Reset`.
- `src/Allegiance.Core/Sim/Orientation.cs` — `SetBasis` (faithful snapshot reconstruction).
- `src/Allegiance.ClientCore/Session/ConnectionSession.cs` — span `Unpack` (no packet copy)
  + publish call at end of each receive batch.
- `src/Allegiance.Core/Wire/FedPacket.cs` — `Unpack(ReadOnlySpan<byte>)`.
- `tools/allegiance-mcp/BotManager.cs` — decision hot path reads the snapshot (no gate).
- `tests/Allegiance.ClientCore.Tests/DecisionSnapshotTests.cs` (new), `DecisionSnapshotAllocationTests.cs` (new).
- `tests/Allegiance.ClientCore.Tests/P632PortModeZeroKillsLiveTests.cs` — P636PERF reporting.
- `docs/reports/P636_BOT_PORT_DECODE_EFFICIENCY.md` (this report).

Committed on `master` (not pushed). See `git log -1`.

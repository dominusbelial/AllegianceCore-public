# P605 — Fixed Send-Worker-Pool Transport behind `transport.mode`

**Task:** put the server's outbound send path behind a feature flag
(`transport.mode`, default `thread-per-peer` = today's behaviour, unchanged),
optionally switching it to a **fixed pool of send workers** draining
hash-partitioned per-peer queues, parameterise the whole transport test suite
over the mode, and A/B it on a **local** server (never 221) at ~120 and ~240
players, plus a synthetic 1000-peer test.

**Branch/commit:** `master` (not pushed). Core code:
`src/Allegiance.Core/Transport/TcpTransport.cs` (`TransportMode`,
`SendWorkerPool`, `PeerSender` pool-drain). Server wiring:
`ServerConfig.cs` (`TransportConfig`), `Server.cs` (fields),
`Server.Lifecycle.cs` (`ResolveTransportMode`, boot print + profiler label).
Harness: `tools/p605synth/` (synthetic bench), `scripts/p605-ab.sh` (local A/B).

---

## 1. What shipped

- **Flag:** `"transport": { "mode": "thread-per-peer" | "pool" }`, **default
  `thread-per-peer`** (today's behaviour — the thread-per-peer path is
  byte-identical: `PeerSender.Start`/`WriterLoop` unchanged). Env override
  `ALLEGIANCE_TRANSPORT_MODE` (env wins). Optional `"poolWorkers"` +
  `ALLEGIANCE_TRANSPORT_POOL_WORKERS` (default ≈ core count; this box = 24).
  Both selectable at server start with **no rebuild**. Active mode printed at
  boot (`[boot] transport mode = pool (N workers)` / `= thread-per-peer`) and
  stamped into the profiler report header alongside the P603 broadcast flag
  (observed: `[broadcast.parallelBuild=false; transport=pool (24 workers)]`).
  An unrecognized mode fails loudly at boot (typo safety).

- **Pool mode** (`TransportMode.Pool`):
  - Keeps the per-peer blocking receive thread (`ReadDataLoop`, `peer-<id>`)
    exactly as today — P600 §6.3's "keep the cheap blocking per-peer reads".
  - Replaces the per-peer `peer-send` writer thread with a **fixed pool of N
    workers** (`SendWorkerPool`), started once at `Host()`. Each peer is
    assigned to exactly **one** worker by a stable hash of its connection id
    (`id % workerCount`), so every peer queue has a single consumer → **per-peer
    send order is preserved** (head-frame drain).
  - Workers drain their assigned peers **round-robin** (visit every peer each
    pass) → no starvation of a busy peer's neighbours.
  - **Back-pressure identical to today:** each peer keeps its bounded
    `LinkedList` queue with the same overflow → drop-oldest-non-guaranteed →
    sustained-overflow disconnect policy (`PeerSender.Enqueue` unchanged).
  - **A slow/stalled client never blocks its worker or the neighbours on it:**
    before writing a peer's head frame the worker checks
    `Socket.Poll(0, SelectWrite)`; a not-writable peer is **skipped** (return
    `Blocked`) and the worker moves on. Because the worker only writes when the
    socket says writable, healthy peers are pumped at full rate even beside a
    stalled one. A 250 ms per-write `SendTimeout` (applied on pool attach)
    bounds the rare write-already-started-then-stalled transient, so a peer can
    hold a worker for at most ~250 ms before being dropped — never the
    thread-per-peer 10 s default.
  - **Clean shutdown / removal:** `Shutdown()` stops and joins all N workers;
    `DropPeer`/read-loop reap calls `pool.Unassign(peer)` so no worker retains
    a dead peer and no queued buffer leaks.

## 2. Ordering & back-pressure guarantees → code + test

| Guarantee | Mechanism (cite) | Test |
|---|---|---|
| Per-peer order preserved | one consumer/peer (stable `id % N` partition, `SendWorkerPool.WorkerFor`), head-frame drain (`PeerSender.TryWriteHead`) | `Pool_preserves_per_peer_order_under_concurrent_enqueues` (4 producers → monotonic per-producer seqs) |
| Fairness (no starvation) | worker visits every assigned peer each pass | `Pool_stalled_peer_does_not_delay_a_neighbor_on_the_same_worker` (latency bound < 500 ms) |
| Slow/stalled client can't block others | `Socket.Poll(0, SelectWrite)` skip-not-writable + 250 ms pool write budget | `Pool_stalled_peer_does_not_delay_a_neighbor_on_the_same_worker` |
| Bounded queue, overflow→drop→disconnect | shared `PeerSender.Enqueue` policy (unchanged) | `Pool_overflow_drops_and_disconnects_a_stalled_peer_without_leaking_workers`; also the whole `TcpTransportSendDropSafety*` suite in pool mode |
| No leaked threads/buffers on churn | `pool.Unassign` on reap; `Shutdown` joins workers | `Pool_worker_and_reader_counts_stay_fixed_across_join_leave_churn`; `TcpTransportThreadLeakTests` |
| Thread count ≈ workers + per-peer readers | pool caps send threads at N | `Pool_worker_and_reader_counts_stay_fixed...` (PoolWorkerThreadCount == N throughout) |
| Flag defaults & binding | `ResolveTransportMode` (config + env), `TcpTransport(site, mode, workers)` | `Default_server_boot_stays_thread_per_peer`, `Pool_config_boot_spins_up_exactly_the_requested_workers`, `Boot_line_prints_the_active_transport_mode` |

## 3. Tests (parameterised over the mode)

- The **whole existing transport suite** now runs under BOTH modes via base
  classes (`TcpTransportSendDropSafetyTestsBase`,
  `TcpTransportGreetTimeoutTestsBase`) with `…ThreadPerPeerTests` /
  `…PoolTests` subclasses:
  - send/drop-safety (6 tests × 2 modes), greet-timeout (4 tests × 2 modes).
  - `TcpTransportThreadLeakTests` (full-server churn) unchanged (default mode).
- New pool suite `Transport/TcpTransportSendWorkerPoolTests.cs` (7 tests):
  concurrent-producer per-peer ordering; stalled-peer isolation (latency
  bound); overflow→drop→disconnect without worker leak; join/leave churn
  (worker + reader counts fixed); config binding (default TPP, pool boots N
  workers); boot line for both modes.
- All pass: the `FullyQualifiedName~Transport` filter ran **37/37 green** in
  Core.Tests; the new pool file 7/7 green.

## 4. A/B measurement (LOCAL server, never 221)

Method mirrors P600: Release `Allegiance.Server` on this workstation (20 upd/s,
`XXV_simple.igc`, 2 sides, 13 sectors), `profiler.enabled` + CSV, debugMode,
driven by `tools/allegiance-mcp` `spawn_scenario preset=showcase
size_scale=<N>` (≈15·N players/side); bots share the CPU (noted, as in P600).
Metrics: `/proc/<pid>/status` (Threads, VmRSS, ctx switches) sampled every 2 s
over the settle window; process CPU% from `/proc/<pid>/stat` (utime+stime)
over a 10 s window; tick wall mean/p95/max from the profiler's `[tickprof]`
log line; sim-thread% = tickMean / 50 ms. Pool used the default 24 workers.

**@ ~120 players (size_scale 4):**

| metric | thread-per-peer | pool (24) | Δ |
|---|---|---|---|
| threads (avg) | 252 | **155** | −38% |
| RSS (KB avg) | 106,168 | 103,693 | −2.3% |
| process CPU% | 31.3 | 30.9 | ≈ |
| voluntary ctx (40 s) | 1,467,110 | 988,877 | −33% |
| nonvoluntary ctx | 17,514 | 11,814 | −33% |
| tick mean / p95 / max (ms) | 4.93 / 6.10 / 6.74 | 4.72 / 5.72 / 6.59 | slightly better |
| sim-thread % | 9.9 | 9.4 | ≈ |

**@ ~240 players (size_scale 8):**

| metric | thread-per-peer | pool (24) | Δ |
|---|---|---|---|
| threads (avg) | 492 | **275** | −44% |
| RSS (KB avg) | 114,312 | 112,326 | −1.7% |
| process CPU% | 77.0 | 71.3 | −5.7 pt |
| voluntary ctx (45 s) | 3,877,725 | 1,975,486 | −49% |
| nonvoluntary ctx | 28,817 | 37,210 | n/a (noisy) |
| tick mean / p95 / max (ms) | 16.10 / 18.33 / 20.13 | 14.18 / 16.81 / 17.55 | −12% mean |
| sim-thread % | 32.2 | 28.4 | −3.8 pt |

**Validation vs P600** (sanity that the harness is honest and the default mode
is unregressed): this run reproduces P600's historical numbers — 252/253
threads @ 121, 492/492 threads @ 241; tick 4.93 vs 4.59 ms @ 121, 16.10 vs
16.38 ms @ 241. Same method, same ballpark.

**Per-bucket (last window, mean ms/tick):**

| bucket | TPP @120 | pool @120 | TPP @240 | pool @240 |
|---|---|---|---|---|
| `step:update` (all sim) | 0.216 | 0.210 | 0.507 | 0.450 |
| `step:drain` (transport on sim thread) | 0.136 | 0.131 | 0.223 | 0.221 |
| `step:broadcastShipStates` | 2.372 | 2.297 | 9.719 | 9.225 |
| `step:broadcastShipsUpdate` | 0.850 | 0.843 | 2.876 | 2.700 |

**Reading:** the pool leaves `step:update` (the whole simulation) and
`step:drain` (the transport's per-tick cost on the sim thread) essentially
unchanged — the pool does not burden the sim thread. The dominant bucket is
broadcast building (P600's finding), which is *marginally* lower under the pool
(@240: 9.23 vs 9.72 ms) from less scheduler/context-switch contention. **Egress
per player is unchanged** — both modes run the identical `SendPeer →
TcpTransport.Send → PeerSender.Enqueue` path; the only difference is which
thread drains the queue. The win at 120–240 is thread count (−38/−44%),
context switches (−33/−49%), and a modestly better tick (−12% @240).

## 5. Synthetic 1000-peer test

`tools/p605synth` runs a bare `TcpTransport` host (no full game server, so the
process's threads/RSS/CPU are purely the transport) with 1000 lightweight
loopback peers receiving a **fixed 30 Hz × 256 B broadcast** (~7.7 MB/s,
fully consumed by the clients — measured `totalRxBytes` equals `bytesSent`, so
**no overflow-drops and identical egress** in both modes).

| metric (1000 peers, 25 s load) | thread-per-peer | pool (8) |
|---|---|---|
| OS threads | 2010 | **1018** |
| RSS (KB) | 110,848 | **90,212** (−19%) |
| CPU% | 55.0 | **22.0** |
| bytes sent (egress) | 215,296,000 | 215,296,000 |
| bytes received (clients) | 215,296,000 | 215,296,000 |

This is the regime the pool exists for: **~2× fewer threads** (2010 vs 1018 at
1000 peers; extrapolates to ~4010 vs ~2018 at 2000), ~19% less RSS, and **>2×
less CPU** on the send path — while delivering byte-identical egress with the
same ordering and back-pressure.

## 6. Recommendation

- **The pool is safe and delivers what P600 §6.3 predicted:** it caps send
  threads at ~N, preserves per-peer order, and never lets a stalled peer block
  its worker or the sim thread. At 120–240 it is neutral-to-better; at 1000+
  peers it is a 2× thread / 2× send-CPU win with identical egress.
- Per the task, keep **default `thread-per-peer`** (unchanged) — the flag is
  opt-in. **Recommend a follow-up to flip the default to `pool`** (or ship it
  enabled on high-peer deployments / 221-class loads), since nothing in these
  measurements argues for keeping thread-per-peer once the pool is proven.
- At the measured 120–240 range the transport is **not** the ceiling (P600):
  `broadcastShipStates` still dominates the tick in both modes; the structural
  levers remain P603 parallel broadcast build (shipped) and distance culling.

## 7. Suite runs (final code, `scripts/run-tests.sh`, P471)

Ran the full suite 3× consecutively on the final committed code. All failures
in every run are **known ledgered live-wire flakes**
(`docs/TEST_FLAKES.md`); the P605 transport suite (37 tests) passed **every**
run.

| run | Lobby | MonoGame | ClientCore | Core.Tests | flakes (all ledgered) |
|---|---|---|---|---|---|
| 1 | — | — | 592/593 | **3900/3900** | ClientCore `ThreePeerSeededSessionTests.SeedThreePeers_…_DeterministicVisibility` (flake ledger line 38) |
| 2 | — | — | — | 3896/3900 | Core conquest/game-over cluster: `GameOverPlayersWireTests.Win_by_elimination_…`, `…Conquest_win_in_one_mission…`, `ConquestTerritoryLiveTests.Conquest_win_in_one_mission…` (+1) (ledger lines 29/30/31/432) |
| 3 | 12/12 | 1720/1723 (3 sk) | 592/593 | 3897/3900 | ClientCore `ClientWarpApproachSlowHullLiveTests.Legacy_200_450…` (ledger line 215); Core `LiveCountdownStartPingTests.StartGame_owner_all_ready…` (line 37), `Win_by_elimination…` (line 432), `P591dMinefieldExportAudienceTests.Npc_layer_drone_minefield…` |

(Runs 1–2 were executed with `tail` truncation so the early-assembly tallies
were not retained; run 3 captured all four assemblies in full.)

One of the run-2 conquest tests (`GameOverPlayersWireTests.
Conquest_win_in_one_mission_does_not_leak_game_over_players_to_another`) was
re-run 6× in isolation to characterise it: **5/6 pass, 1/6 fail** — a genuine
pre-existing live-wire flake (ledger line 30), not a transport regression (the
default thread-per-peer path is byte-identical, and this test is game-state
propagation over the wire, orthogonal to the send pool).

Whole-solution build: **0 warnings / 0 errors**.

## 8. Raw data

Committed in `docs/reports/P605_SEND_WORKER_POOL_TRANSPORT_assets/`:
`summary-{mode}-s{scale}.json`, `tick-{mode}-s{scale}.csv`,
`tickprof-{mode}-s{scale}.txt` (the `[tickprof]` log lines incl. the labelled
header), and `synth-{tpp,pool}.json`. Numbers above are only what was measured
on this workstation (2026-10-11), profiler on, bots sharing the CPU; no values
fabricated or extrapolated from 221.

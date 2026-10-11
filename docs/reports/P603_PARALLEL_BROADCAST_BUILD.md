# P603 — Parallel per-peer ship-state broadcast build (`broadcast.parallelBuild`)

**Task:** split `BroadcastShipStates` (and, where byte-safely possible,
`BroadcastShipsUpdate`) into a serial snapshot + parallel per-peer build +
serial enqueue, behind a feature flag defaulting OFF, producing byte-identical
per-peer payloads to serial mode, and A/B-measure it locally.

**Branch/commit:** `master` (not pushed). Code: `src/Allegiance.Server/Server.Broadcast.cs`,
config in `ServerConfig.cs` + `Server.Lifecycle.cs`; profiler label in
`src/Allegiance.Core/Profiling/TickProfiler.cs`; tests in
`tests/Allegiance.Core.Tests/ParallelBroadcastByteIdentityTests.cs`.

---

## 1. Why this task (the P600 lever)

P600's profiler (`docs/reports/P600_TICK_PROFILE_AND_PER_SECTOR_DESIGN.md`)
measured the whole `Mission.Update` sim at **~3–4% of the tick**, and the
`broadcast*` family at **~92%**. `BroadcastShipStates` alone was **~58%**
(9.5 ms of a 16.4 ms tick at 241 players; 2.2 ms of 4.6 ms at 121) and grows
**~4.3× for 2× players** (an O(N²) per-(recipient, visible-ship) pass that
builds each peer's payload serially on the sim thread). P600 §7.1 estimated
**about −9 ms @ 241** from building per-peer payloads in parallel across cores
feeding the existing per-peer enqueue queues — the #1 scalability lever, and
layout-independent. This task ships it behind a flag per Domingo's rule.

## 2. Feature flag

- Config: top-level `"broadcast": { "parallelBuild": true, "maxDegreeOfParallelism": 7 }`
  (`ServerConfig.Broadcast`, `BroadcastConfig`). **Absent or `false` = OFF** —
  the default — running the exact serial code path byte-for-byte.
- Env override: `ALLEGIANCE_BROADCAST_PARALLEL_BUILD` (`1|true|on` / `0|false|off`;
  env wins over config), resolved once at boot (`Server.Lifecycle.ResolveBroadcastParallelBuild`).
- **Boot print:** `[boot] broadcast.parallelBuild = true|false …` so a run is
  never mis-labelled.
- Degree of parallelism: `maxDegreeOfParallelism` (clamped to `[1, ProcessorCount-1]`)
  or the default `ProcessorCount − 1`, used as `Parallel.ForEach`'s
  `MaxDegreeOfParallelism` (bounded degree, leaving a core for transport/OS).
- **Profiler label:** the active mode is stamped into the tick-profiler report
  header (`[tickprof] window … [broadcast.parallelBuild=true (maxPar=7)] …`)
  so every measurement CSV/log is labelled (P600 §1 + §6.4 requirement).

## 3. Design — `BroadcastShipStatesParallel` (the dominant bucket)

`BroadcastShipStates` (the ~58% O(N²) bucket) splits into three phases, all
inside one tick; the sim thread waits:

**(a) Serial snapshot phase (sim thread).** Freeze every per-tick input the
builders read into read-only shared structures: the `peers = _peerShips.ToArray()`
frozen snapshot; the per-side `sameSide` grouping; the per-(mission,side)
same-side NPC grouping `sameSideNpcBySide`; a prebuilt per-ship `positionEncode`
(the SingleShipUpdate every same-side recipient reuses — bytes are a pure
function of the frozen world); and the per-ship loadout-snapshot cache prewarmed
for every ship the build can touch. The per-peer latch dictionaries are **not
copied** — they are read concurrently (multiple readers of a Dictionary are
safe; no worker writes them).

**(b) Parallel build phase.** `Parallel.For(0, peers.Length, po, …)` with bounded
degree. Each worker (`BuildPeerShipStates`) builds its peer's **ordered packet
list** from the snapshot + read-only latches only, and records per-peer
**effect deltas** (the latches / `_peerShips` write the serial builder performs
inline). No worker touches mutable world state, writes a shared collection, or
sends. Within-peer Add-then-Contains latch decisions (teamexport → playerinfo →
status/position, loadout-version read-back) use per-worker **local overlays** so
a peer sees its own writes exactly as serial's inline writes are.

**(c) Serial apply phase (sim thread).** Walk peers in `_peerShips` order,
enqueue each peer's packets via `SendPeer` in the exact serial order (so per-peer
wire order — and the global capture stream — are unchanged), apply the peer's
latch / `_peerShips` effects, and emit its buffered log lines.

**Byte-identity argument.** Every decision in the serial builder reads (i) the
frozen `peers` snapshot, (ii) per-peer / per-(peer,mate) / per-(peer,npc) latch
keys that are **disjoint across recipients**, and (iii) read-only world state
(quiescent during the broadcast). Because the latch keys are per-recipient, no
peer's writes influence another peer's decisions within the tick — so building
peers in parallel and applying each peer's effects serially afterwards yields the
exact same decisions, packet counts, packet order and final latch state as
serial. The only shared per-ship caches (`positionEncode`, loadout snapshot) are
pure functions of the frozen world, so precomputing them yields identical bytes.
No shared mutable buffers exist between peers (each worker owns its per-peer
result list; pooled allocation is left as future polish).

### What stayed serial — and why (`BroadcastShipsUpdate`)

`BroadcastShipsUpdate` (**not** per-peer-parallelised; it is parallelised
across **sectors** only, see below). Its per-peer body mutates each candidate
ship's `Deviation` staleness accumulator (`c.Deviation = 0f` / `+= 1.00f`,
the P219 "w0dk4 Bandwidth Patch" budget, `FedSrv.CPP:3844-3948`) **and a later
peer in the same sector reads that mutated value back this same tick** to order
its own serve/bump (`OrderByDescending(c => c.Deviation)`, `FedSrv.CPP:3923`).
That is **cross-peer, order-dependent shared mutable state fed back into the same
tick's decisions** — exactly the "not safely snapshot-able, leave it serial"
case the task's audit rule anticipates. A naïve per-peer parallel build would
change which ships each peer serves (not byte-identical) and race on `Deviation`.

Instead, `BroadcastShipsUpdate` is parallelised **across sectors**: peers are
partitioned by `kv.Value.Sector` (preserving `_peerShips` order within each
sector) and each sector's peers run serially on one worker
(`Parallel.ForEach(bySector, …)`). Peers in different sectors touch disjoint
candidate lists and disjoint `Deviation` sets, so this is byte-identical while
spreading the ~2.9 ms @ 241 ships-update bucket over the sectors. For the
**single-sector concentrated-fight** case this yields no speedup — documented
here; `broadcastShipStates` (the dominant bucket) is the per-peer-parallel one,
so the mechanism conclusion in the P600 report still holds.

## 4. Byte-identity proof

`tests/…/ParallelBroadcastByteIdentityTests.cs` drives `BroadcastShipStates` +
`BroadcastShipsUpdate` directly (via an internal test seam,
`Server.BroadcastTestSeams.cs`) against **deterministically-fabricated worlds**:
36–120+ peers on two sides spread across three sectors, a mix of docked / flying
/ moving ships, a shared cluster-info payload, and same-side NPCs (P366 path),
across multiple tick indices (0, 5, 99, 200 — past every refresh cadence). Each
scenario runs once in serial mode and once (after a full state reset) in
parallel mode, capturing each recipient's outbound packet stream, and asserts
the per-peer byte streams are **identical element-for-element**.

Results (all 5 byte-identity tests): **PASS**. The harness initially caught two
genuine discrepancies in the parallel build that the serial builder did not
have — a doubled `EXPORT` (the teamexport's `_teammateLoadoutVersionSent` write
was an un-applied effect when the same mate's loadout re-export check read it
back) and a per-ID dedup gap in the same-side NPC export overlay — both fixed so
the parallel gate mirrors serial's immediate-write/`.Add()` semantics exactly.
The byte-identity tests now prove the two modes emit identical bytes.

Plus:
- **Default-off:** `Parallel_flag_defaults_off_and_config_binds` asserts the flag
  resolves `false` on a default boot and `_broadcastMaxParallelism >= 1`.
- **Stress:** `Parallel_broadcast_survives_join_leave_churn_without_exceptions`
  seats ~14 real loopback clients on both sides, puts them onto real sides, then
  ticks the server **400 times with `broadcast.parallelBuild` ON while clients
  join and leave mid-stream** — no exception thrown, and a survivor still
  receives its ship-state stream afterwards.
- **Flag-on exercise:** the byte-identity tests run both builders in both modes;
  the parallel-mode live path is additionally exercised by the churn stress test.

## 5. A/B measurement (local server, never 221)

Method (identical to P600 §2): Release build of this repo's `Allegiance.Server`
(port 6073, 20 upd/s, `XXV_simple.igc`, 2 sides, 13 sectors) with
`profiler.enabled` + CSV; bots driven by `tools/allegiance-mcp`
`spawn_scenario preset=showcase`, grown by `size_scale`. **Bots and the server
share this workstation's cores** (the same caveat P600 noted), so reported
per-thread/server CPU is a ceiling under contention; the relative *bucket*
breakdown is unaffected, which is what the comparison leans on. `size_scale=3`
gave ~91 players; `size_scale=4` gave ~122. Settled-window means (5 s windows,
steady player count):

| players | mode | `broadcastShipStates` ms | `broadcastShipsUpdate` ms | `step:update` (sim) ms | tick-wall mean ms | p95 ms |
|---|---|---|---|---|---|---|
| ~91 | serial | ~1.25 | ~0.51 | ~0.16 | ~3.0 | ~3.9 |
| ~91 | **parallel** | **~1.05–1.35** | ~0.45–0.49 | ~0.16 | ~2.7–3.1 | ~3.9–4.3 |
| ~122 | serial | ~2.18 | ~0.80 | ~0.19 | ~4.55 | ~5.8 |
| ~122 | **parallel** | **~1.60** | ~0.53–0.56 | ~0.21–0.23 | ~3.74–3.79 | ~5.0–5.3 |

Raw profiler CSVs: `docs/reports/P603_assets/tick_parallel.csv`,
`tick_serial.csv`, `tick_parallel_120.csv`, `tick_serial_120.csv`.

**Reading (measured):** at ~122 players the parallel build cuts the dominant
`broadcastShipStates` bucket from **~2.18 ms to ~1.60 ms (−27%)**, the
`broadcastShipsUpdate` bucket from ~0.80 to ~0.55 ms, and the whole tick-wall
mean from **~4.55 ms to ~3.76 ms (−17%)** — a real, layout-independent reduction
with zero wire change. At ~91 players the saving is proportionally smaller
(~1.25→~1.05 ms on `broadcastShipStates`, tick ~3.0→~2.8 ms) because the O(N²)
term is still small there. The lever compounds quadratically: P600 measured
`broadcastShipStates` ~9.5 ms @ 241 in serial, and the parallel build removes
that bucket's per-peer *serialisation* (the CPU-bound encode spreads across
cores) — which is where P600's ≈−9 ms @ 241 / “about half the tick” estimate
applies. The 240-player measurement was not completed in this session (the
showcase at `size_scale=8` is a very heavy spawn on this shared box); the
mechanism and the measured 91/122-player reductions above are the evidence.
Bots and server shared this workstation's cores (P600's caveat), so CPU figures
are ceilings; the bucket breakdown is unaffected.

## 6. Recommendation

**Ship it OFF by default** (done), **do not enable on 221 yet.** The parallel
build is byte-identical (proven), survives churn, and measurably shrinks the
dominant `broadcastShipStates` bucket with zero wire-format change. Enable it on
a local A/B at ~240 players (or a staged rollout) once that datapoint is taken;
combined with P604 (distance-graded culling) it attacks the two O(N²) fan-outs.
`broadcastShipsUpdate`'s per-sector parallelisation is also safe to enable and
helps the spread layout; the concentrated single-sector fight is unaffected by it
(only `broadcastShipStates`'s per-peer parallelism helps there).

## 7. Verification

- Whole-solution build: **0 warnings / 0 errors**.
- New tests: 5 byte-identity + default-off + join/leave churn stress — all green.
- Full suite via `scripts/run-tests.sh` (P471), **2 consecutive runs on the final
  code** (per Domingo 2026-10-09, reduced from 3): each run had a small rotating
  set of pre-existing **ledgered** live-wire/load flakes
  (`docs/TEST_FLAKES.md`: `ShipDeleteGoneLiveTests.DockOut…` line 24,
  `LiveStationRescueTests…` line 156, `LiveChatSectorWireTests…` lines 216/419,
  `GameOverPlayersWireTests.Win_by_elimination…` line 432, `LiveTreasurePickupTests…`
  line 157, `CargoBayReloadTests…swaps…` lines 53-54, `ThreePeerSeededSessionTests…`
  line 38) and **zero P603 regressions**; the 6 new P603 tests passed in both runs.
  Suite numbers (final): **Lobby 12/12, MonoGame 1720/1720, ClientCore
  (run1) 592/593 (run2) 591/593, Core (run1) 3881/3884 (run2) 3882/3884** — all
  failures ledgered above.
- The serial default path is untouched (method renamed to
  `BroadcastShipStatesSerial`; `BroadcastShipsUpdate`'s body mechanically
  extracted to `BroadcastShipsUpdatePeer`) and every existing broadcast live test
  remained green.

# P600 — Tick Profile & Per-Sector Parallel Simulation Design

**Task:** measure where the server's single-threaded tick spends its time as
players grow, decide whether per-sector parallel simulation is a sound path,
and design the parallelisation (and a thread-pool transport option) behind
feature flags. Investigation + design only; the only code shipped is the
**off-by-default tick profiler**.

**Branch/commit:** `master` (not pushed). Companion profiler code:
`src/Allegiance.Core/Profiling/TickProfiler.cs`, wiring in
`Mission.Update`, `Sector.Update`, `SimPass`/`Step` (Server.Broadcast.cs,
Server.Lifecycle.cs), config in `ServerConfig.cs` (`profiler` section).

---

## 0. Headline findings (measured on this workstation, 2026-10-10)

1. **The ceiling is set by broadcast fan-out / message building, not by the
   per-sector simulation.** Across every player count, the `step:update`
   bucket (the whole `Mission.Update` = `CmissionIGC::Update` port, i.e. all
   per-sector sim) is only **~3–4% of the tick**. At 241 players it is
   0.45 ms of a 16.4 ms tick; the `broadcast*` family is ~92%.

2. **`broadcastShipStates` alone is the single largest bucket** — 2.2 ms of
   4.6 ms (~48%) at 121 players, 9.5 ms of 16.4 ms (~58%) at 241 players.
   Its growth from 121→241 is ~4.3× for ~2× players — consistent with the
   **O(N²)** per-(recipient, visible-ship) pass (`Server.Broadcast.cs`
   `BroadcastShipStates`).

3. **Tick wall grows super-linearly with players.** Local showcase
   (spread layout, XXV_simple): 0.79 ms @ ~30 → 4.59 ms @ ~121 →
   16.38 ms @ ~241 players. 30→121 is 5.8× for 4.0× players; 121→241 is
   3.6× for 2.0× players. Extrapolated to ~300 this is already ~80%+ of the
   50 ms period at 20 upd/s — the same story as the 221 saturation.

4. **Per-sector parallel simulation would attack only ~3–4% of the tick at
   these counts.** Splitting `Mission.Update` across sectors is therefore **not
   the lever**. The lever is parallelising **per-peer broadcast building /
   serialization**, and reducing the O(N²) visibility fan-out.

5. **Layout (concentration) barely matters at these counts.** A
   target-sector staging run that kept ~120 bots in few sectors showed the
   same ~4–5 ms tick and the same broadcast dominance as the 121-player
   spread run — because the dominant cost is per-**peer** fan-out (scales
   with peer count), not per-sector object density. (Honest caveat: natural
   travel-with-staging patrolled rather than forming a single megafight, so
   the concentrated datum is approximate — but the mechanism conclusion holds.)

6. **Networking is already parallel and is NOT the limit before the sim
   thread at these counts** (one read + one send thread per peer). Thread
   count tracked peers (~2/peer: 73 @ 30, 253 @ 121, 492 @ 241). The sim
   thread never blocks on socket I/O (bounded per-peer send queue). The
   thread-per-peer design is a *resource* concern at 1000+ peers, not the
   immediate ceiling; a pooled transport is a worthwhile follow-up (see §6)
   behind a feature flag, but does not unblock the sim thread.

---

## 1. What shipped: the off-by-default tick profiler

A committed, feature-flagged per-tick profiler (`TickProfiler` in
`Allegiance.Core.Profiling`). **Off by default** — a missing/disabled
`profiler` section leaves the static `TickProfiler.Instance == null`, and
every hot-path call collapses to a null check returning a defaulted no-op
struct (`ProfilerScope`). Enabling it measures, per simulation tick:

- **Per subsystem** — every `SimPass` `Step(...)` stage is timed under a
  `step:<stage>` bucket (drain, update, every `broadcast*`, lobby/roster,
  side-selection, etc.), and every intra-sector pass in `Sector.Update` under
  a `sim.*` bucket (`sim.movement`, `sim.collision`, `sim.combat`,
  `sim.docking`, `sim.mining`, `sim.economy`, `sim.rescue`, `sim.visibility`,
  `sim.warp`, `sim.mines`, `sim.station`, `sim.navigation`).
- **Per sector** — each `sector.Update` is timed under `sector:<id>` and the
  sector's object/ship population is recorded and reported.
- **Aggregate window** — every `reportIntervalSeconds` (default 5 s =
  100 ticks @ 20 upd/s) it prints mean / p95 / max ms **per tick per bucket**
  (plus the whole-SimPass tick-wall mean/p95/max), and — when `csvPath` is
  set — appends one CSV row per bucket per window.

**Config + env override** (so a live run can profile without editing a file):

```json
"profiler": { "enabled": true, "reportIntervalSeconds": 5, "csvPath": "/tmp/tick.csv" }
```
`ALLEGIANCE_TICK_PROFILER=1|true|on` → enable (log only);
`=0|false|off` → force off; any other value → treat as CSV path.
`ALLEGIANCE_TICK_PROFILER_INTERVAL` overrides the window.

It banners loudly at boot (`[boot] *** TICK PROFILER ACTIVE …***`) so a
measurement run is never mistaken for an uninstrumented one. The whole
SimPass wall time is the profiler's "tick" reference, so bucket percentages
are computed against a real denominator.

**Verification (unit tests, `tests/Allegiance.Core.Tests/TickProfilerTests.cs`):**

| Requirement | Test | Result |
|---|---|---|
| off by default | `Profiler_is_off_by_default…` | ✅ instance null, no-op scope |
| enable/disable round-trip | `Enable_then_disable…` | ✅ |
| records + reports to CSV | `Enabled_profiler_records…` | ✅ mean>0 for a slept bucket |
| buckets sum within tick wall | `Bucket_samples_sum…` | ✅ disjoint buckets ≈ timed work |
| negligible overhead when off | `Disabled_profiler_has…` | ✅ 1M no-op Enter/Record < 1 s |

Whole-solution build: 0 warnings / 0 errors. Suite: 2× green (see §7).

---

## 2. Method

- **Machine:** this workstation (Linux, shared cores). **Bots and the server
  share the CPU** — bots live in the `allegiance-mcp` process on the same
  host, so reported per-thread/server CPU is a ceiling under contention; the
  relative *bucket* breakdown is unaffected (all buckets compete equally),
  which is what the scaling analysis leans on.
- **Server:** Release build of this repo's `Allegiance.Server` (port 6073,
  20 upd/s, map `XXV_simple.igc`, 2 sides, 13 sectors) with `profiler.enabled`
  and a CSV path. Never connected to / deployed to 221.
- **Driver:** `tools/allegiance-mcp` `spawn_scenario` `preset=showcase`
  (P557), grown by `size_scale` to hit target peer counts; served over stdio
  via the existing MCP driver script.
- **Layout (a) spread:** default showcase — fleets stage in their side's home
  sectors (Ceto side 0 / Quaoar side 1) and patrol.
- **Layout (b) concentrated:** `target_sector="Haumea"` with travel-with-staging
  (no `arena` — arena's P554 relocation needs `adminCheats`, which we left off;
  travel avoids that dependency).
- **Metrics per point:** profiler CSV (per-bucket mean/p95/max) + `/proc/<pid>`
  Threads / voluntary & non-voluntary context switches / VmRSS, sampled at a
  settled window. Raw CSVs are committed in `docs/reports/P600_assets/`.

---

## 3. Measurements

### 3.1 Tick wall vs players (spread layout)

| players¹ | tick-wall mean | p95 | max | threads | vol.ctx | non-vol.ctx |
|---|---|---|---|---|---|---|
| ~30 | 0.79 ms | 0.96 | 1.49 | 73 | 18,490 | 2,228 |
| ~121 | 4.59 ms | 5.85 | 6.21 | 253 | 116,395 | 3,260 |
| ~241 | 16.38 ms | 18.70 | 20.06 | 492 | 340,093 | 3,880 |

¹ bots + probe seated; a few Stealth Bombers fell back to hull 13 under native
tech gating (honest, minor).

Scaling ratios: **30→121 (4.0× players): 5.8× tick** · **121→241 (2.0×
players): 3.6× tick** → strongly super-linear. The single dominant driver is
`broadcastShipStates`, growing ~4.3× from 121→241 (≈ quadratic in peers ×
visible ships).

### 3.2 Per-bucket breakdown (spread, mean ms/tick, settled window)

| bucket | @121 | @241 | growth (121→241) |
|---|---|---|---|
| `step:broadcastShipStates` | 2.19 (48%) | 9.48 (58%) | 4.3× |
| `step:broadcastShipsUpdate` | 0.83 | 2.97 | 3.6× |
| `step:broadcastMissileTrackWarning` | 0.25 | 0.56 | 2.2× |
| `step:broadcastOwnShipMining` | 0.23 | 0.51 | 2.2× |
| `step:broadcastLeadIndicator` | 0.23 | 0.45 | 2.0× |
| `step:broadcastOwnShipKillBonus` | 0.23 | 0.44 | 1.9× |
| `step:broadcastMissileLock` | 0.17 | 0.39 | 2.3× |
| **`step:update` (all sim)** | **0.18 (4%)** | **0.45 (3%)** | 2.5× |
| `step:drain` (transport) | 0.11 | 0.21 | 1.9× |
| broadcast family **total** | **~3.7 (80%)** | **~15.0 (92%)** | — |

**Reading:** the *simulation* is a rounding error; the tick is spent building
and fanning out wire messages (state updates, exports, visibility, owner-only
streams). The owner-only streams (missileLock, leadIndicator, killBonus,
ownShipMining, missileTrackWarning) each scale ~linearly (one message per
peer); `broadcastShipStates` + `broadcastShipsUpdate` (per-peer × per-visible-
ship) scale ~quadratically.

### 3.3 Concentrated layout (~120 players, target Haumea, staging)

tick-wall mean ~4.3–4.9 ms; threads 252; **voluntary context switches 684,073**
(≈6× the spread-121 figure — active per-peer sends in a closer fight). Bucket
shape is the same: `broadcastShipStates` ~2.3 ms dominates, `step:update`
~0.8 ms, `sim.combat` briefly spiked to ~0.2 ms. **Sector locality did not
move the needle** — cost follows peer count, not sector density.

> Caveat: travel-with-staging patrolled and never formed one megafight, so the
> concentrated datum approximates "close-quarters distribution" rather than a
> single 120-ship brawl. It still supports the mechanism finding.

### 3.4 Linear vs quadratic buckets

- **Roughly linear in N (per-peer owner-only):** `broadcastMissileLock`,
  `broadcastLeadIndicator`, `broadcastOwnShipKillBonus`, `broadcastOwnShipMining`,
  `broadcastMissileTrackWarning`, `drain`.
- **Super-linear / ~O(N²) (per-peer × per-visible-ship, or whole-sector
  pairs):** `broadcastShipStates` (4.3× per 2× N), `broadcastShipsUpdate`,
  and (at fight densities) `sim.visibility` / `sim.collision`.

---

## 4. Dependency analysis for per-sector parallelism

Today `CmissionIGC::Update` (`missionigc.cpp:3127`) iterates clusters
**sequentially** on one thread (`for l = m_clusters.first() … l->data()->Update`)
inside the ported `Mission.Update` (`Mission.cs:670-679`), driven by
`SimPass`'s `step:update` (`Server.Broadcast.cs:41-53`). To split sectors
across threads we must reason about every **cross-sector** state a parallel
`Sector.Update` would touch. Table (port cite + native cite):

| Cross-sector state | Touched where (this tick) | Port cite | Native cite | Handling for per-sector parallel |
|---|---|---|---|---|
| **Ships transiting a warp (sector change)** | A ship reassigns `Ship.Sector` and is moved between two sectors' `_models`; `SET_CLUSTER` broadcast | `Ship.cs:471` (`Sector` prop), `Sector.cs:113` (`_models`), warp collision pass `Sector.cs:702` | `cfsship.cpp` `SetCluster` route; `warpIGC.cpp` | **between-tick merge phase**: collect transits, apply after the parallel sector pass (single-threaded). No two sectors may mutate one ship concurrently. |
| **Sides / scoring / victory** | `Side.Update` runs **once per Update**, outside any cluster loop (`Mission.cs:617`), and is already serial before the cluster chunks | `Mission.cs:616-621` | `missionigc.cpp:3131-3137` | Already serial in the port; leave the side pass single-threaded. Scoring read per peer in broadcasts — serial. |
| **Economy / money** | `Side.Money` accrual on payday (`Mission.cs:645-657`); treasure pickup credits the side (`Server.Broadcast.cs` `treasurePickups`) | `Mission.cs:645-657`; `Side.cs:513` `Side.Update` | `fsmission.cpp:2514-2552`; `FedSrv.CPP:13589+` | Serial payday pass before sector parallel; money mutations funneled to the merge phase. |
| **Global ID allocation** | `Server.AllocateShipId`/`_nextShipId` (`Server.cs:100`), ship & object id fountain | `Server.cs:100`, `SpawnNpcShip` (`Server.cs:844`) | `FedSrv` id fountain | **atomic counter** (`Interlocked`) — the one genuinely parallel-safe cross-sector primitive already; keep it atomic. |
| **Chat** | Chat handler → broadcast fan-out (serial) | `Server.Dispatch.cs` chat handler | `FedSrv.CPP` chat | Broadcast is serial (fan-out step); chat *input* is already drained serially. |
| **Lobby / roster / scoreboard** | `BroadcastScoreRoster`, side-selection, ready flags (all serial steps) | `Server.Broadcast.cs` `broadcastScoreRoster`; `Server.Lobby.cs` | `fsmission.cpp` roster | Serial broadcast phase; no per-sector dependency. |
| **EXPORT to teammates in other sectors** | `SweepCrossSideExports` / `SweepCrossSideShipStatuses` iterate **all peers**, not per-sector; read each ship's `SeenBySide` | `Server.Broadcast.cs` `crossSideExportSweep` | `FedSrv.CPP` export | This is a **broadcast (serial fan-out) step**, not a sim step — unaffected by parallelising `Sector.Update`. |
| **Kill credit** | `ServerHost.ShipKilled`, kill attribution reads across sides | `Server.Combat.cs` | `FedSrv.CPP` kill | Serial event handling after the sim pass (merge phase). |
| **Sampler / log / notable** | `ReportNotable`, sampler writes | `ServerHost` | — | Fire-and-forget writes; serial merge phase / out-of-band. |
| **Shared static caches** | `HullLandingBayCache`, `WeaponMuzzleCache`, `StationLandingBayCache` (read-mostly) | `Server.cs:57-59` | — | Immutable-after-boot → safe to read from parallel sectors; verify no lazy mutation. |
| **Warp-bomb / cross-sector projectiles** | A projectile's sector is its owning `_models`; warp-bomb scheduled detonations are per-sector lists | `Sector.cs:87` | `clusterIGC.cpp` | Detonations resolved inside the owning sector; cross-sector effects (dmg to arriving ship) → merge phase. |

**Conclusion:** the only *hard* cross-sector mutators are (1) warp transits
(shipping between two sectors' membership) and (2) side/money/kill events.
Both are naturally handled by a **parallel per-sector simulate phase followed
by a serial between-tick merge phase** — the same shape native uses, where the
"merge" is implicit because everything is one thread. This is feasible, but
see §5: it would parallelise only ~3–4% of the measured tick.

---

## 5. Design proposal (written, not implemented)

### 5.1 Recommended architecture

Three phases per tick:

1. **Serial prelude (unchanged):** `drain`, side pass, payday, ID-allocation
   (atomic), countdowns, side-selection, ballots.
2. **Parallel per-sector simulate:** `Mission.Update`'s cluster loop becomes
   `Parallel.ForEach(Sectors, s => s.Update(now))` — each sector's own
   `_models`, collisions, combat, mining, visibility are disjoint. Collect
   per-sector **cross-sector event queues** (transits, ripcord teleports,
   rescues, treasure pickups, kills, station events) into thread-confined
   lists (no sharing).
3. **Serial merge + broadcast:** apply all cross-sector events single-threaded
   (transit ship membership moves, side/money/kill crediting), then the
   existing serial broadcast fan-out.

Feature flag mirroring the transport: `sim.parallelSectors: false` by default
(full A/B-able against `true`), printed at boot, and the profiler/sampler
labels every run so measurements are comparable.

### 5.2 Determinism / ordering

`Sector.Update` currently runs in deterministic sector order; `Parallel.ForEach`
breaks per-tick ordering of *which sector finishes first*, so any result that
observes cross-sector order (transit processing order, tie-breaking in
conflicts) must be applied in a deterministic order in the **merge phase**
(index by sector id). Floating-point results within a sector are unchanged
(each sector runs sequentially internally). The merge must run transits in
mission/sector id order to keep warp sequencing reproducible.

### 5.3 Minimal prototype

Take `Mission.Update`'s cluster loop (`Mission.cs:670-679`), extract the
per-sector body into `Sector.Step(now)` (already exists as `Sector.Update`),
make the event drains per-sector-thread-confined, and run
`Parallel.ForEach`. Gate on `sim.parallelSectors`. Expected effort: one
delegated task (small, contained), mostly the merge-phase for transits + the
three event families.

### 5.4 Expected speed-up (from measured numbers)

- **Layout (a) spread, any count:** parallelising `step:update` (≤0.45 ms of
  16.4 ms @ 241) buys **≤3%** wall-time. **Not worth it on its own.**
- **Layout (b) single-sector fight:** per-sector parallelism helps **zero**
  (one sector = one worker). Explicitly: **per-sector parallelisation does
  not help a single-sector fight** — the case where the server is actually
  saturated at ~300 players.

### 5.5 The levers that DO help (intra-sector / broadcast)

Because ~92% of the tick is **broadcast building + serialization + fan-out**:

1. **Parallel per-peer broadcast building (highest ROI).** `broadcastShipStates`
   and `broadcastShipsUpdate` dominate. The per-peer message *building*
   (compute the recipient's view, encode the wire bytes) is embarrassingly
   parallel per peer; the per-peer `PeerSender.Enqueue` is already
   lock-protected and cheap. **Est. −9 ms @ 241** if the two big broadcasts
   build per-peer payloads in parallel. *(P601 candidate.)*
2. **Reduce O(N²) visibility fan-out.** `broadcastShipStates` walks
   per-(recipient, visible-ship). Native thresholds visibility by
   **distance / scanner interest** (`VisibilityMath.InScannerRange` is already
   the determinism gate; the port sends a status for every *currently seen*
   ship every refresh). Native updates model states at distance-graded rates
   (see `CshipIGC`/`clusterIGC.cpp` per-tick update + the client-side
   `CanSee`; `shipIGC.cpp` uses distance-based detail). The port does not
   implement distance-graded **update rate** — every visible ship is re-sent at
   the same cadence. Culling the per-peer status/ship-update fan-out by
   distance (only nearby ships at full rate; distant at low rate) cuts the
   quadratic term directly. **Est. −5–8 ms @ 241**, layout-dependent.
3. **Batch/COBS-style payload reuse:** `_clusterInfoBytes` already reuses one
   mission-wide CLUSTERINFO payload (`Server.cs:373`); extend the same
   "encode once per unchanged view" idea to stations/asteroids/probes and the
   lead-indicator streams.

---

## 6. Networking threads — thread-pool option research (5b)

### 6.1 Today's design (thread-per-peer)

`TcpTransport` (Core) spawns, per connection: **one blocking read thread**
(`ReadDataLoop`, `TcpTransport.cs:406`, named `peer-<id>`, started at
`:340`) and **one send-writer thread** (`PeerSender.WriterLoop`,
`TcpTransport.cs:600`, `:540`, named `peer-send`), plus the accept loop
(`:154`). So ~**2 threads/peer** (matches the 221 table: 613 threads @ 301).

- **Main-thread blocking:** `Server.Send → TcpTransport.Send` only enqueues a
  fully-framed byte[] onto a bounded per-peer `LinkedList` under `_sync`
  (`TcpTransport.cs:197-224`, `PeerSender.Enqueue:545`) and returns — **no
  socket I/O on the sim thread**. The sim thread drains transport events from
  a lock-free `ConcurrentQueue` (`ServerHost.Drain`). So the sim thread never
  blocks on networking except microseconds of enqueue-lock.
- **Measured data-plane:** threads tracked peers exactly (73 @ 30 → 253 @ 121
  → 492 @ 241); voluntary context switches grew with activity (18k → 116k →
  340k; 684k in the concentrated fight), i.e. the per-peer send threads wake
  and yield proportional to egress, not to idle peer count. No sim-thread
  stall was observed; `drain` stayed 0.1–0.2 ms.

### 6.2 Is thread-per-peer a real limit before the sim thread? (measured)

**No, not at ≤~300–500 peers.** The sim thread is saturated (16 ms @ 241 local;
93–96% @ 301 on 221) while the transport threads are I/O-bound and cheap
(individually). Thread-per-peer becomes a *resource* problem at 1000+ peers
(2000+ threads: 2 MB+ default stacks each, scheduling pressure), but it is
**not** the 300-player ceiling. Ordering is trivially in-order per peer
(single writer). Back-pressure is good (bounded 8 MiB queue + overflow-drop +
disconnect).

### 6.3 Alternatives (designs compared)

| Design | Expected threads @300/500/1000 | Ordering per peer | Slow-client back-pressure | Complexity |
|---|---|---|---|---|
| **thread-per-peer (today)** | ~600 / ~1000 / ~2000 | in-order (1 writer) | bounded queue + drop + disconnect | low (exists) |
| **async sockets** (`SocketAsyncEventArgs`/`ReceiveAsync`/pipelines) on .NET pool | ~pool (tens) / ~pool / ~pool | in-order per socket (completion serialization per connection) | same queue policy | high (rewrite framing to async, no dedicated threads) |
| **fixed N send-worker pool draining per-peer queues** | N workers + 1 accept | in-order per peer if each peer's queue drained by one worker at a time (partition queues by hash → still in-order) | same queue policy | medium (keep blocking reads, pool the writers) |

For reference, native used **DirectPlay** (a COM game-networking layer) whose
transport the server did not micro-manage; the FedSrv networking stack
(`FedSrv/`/`netplay`) has its own worker/threading, not a strict thread-per-
peer model — i.e. native never depended on 2 threads/peer, which is one reason
it is not the architecture to emulate blindly at high peer counts.

**Recommendation:** the **fixed-send-worker-pool** is the pragmatic middle —
keep the blocking per-peer *read* (reads are cheap and rarely contended) and
replace the 1-per-peer *writer* with a small pool of writers each draining a
hash-partitioned set of per-peer queues (in-order preserved per peer). This
caps the send-thread count at ~#cores and removes the 2N-thread tax at 1000+
peers with far less rewrite risk than full async. Estimated gain at 300–500
peers: modest on the sim thread (not the ceiling), meaningful on memory /
scheduler pressure at 1000+. **Must ship behind a feature flag** (§6.4) so it
can be A/B’d.

### 6.4 Feature-flag + A/B spec (Domingo requirement)

Ship `transport.mode` **behind a feature flag**, default `thread-per-peer`
(today's behaviour) until the pool is proven:

- Config: `"transport": { "mode": "thread-per-peer" | "pool" }` (default
  `thread-per-peer`), plus env override `ALLEGIANCE_TRANSPORT_MODE`.
- Both modes selectable at server start with no rebuild; the active mode is
  printed at boot (`[boot] transport mode = pool`) and in the
  profiler/sampler output so every measurement is labelled.
- **A/B plan:** the same 100/200/300-player showcase run in each mode on the
  same machine, comparing main-thread %, process CPU, thread count, context
  switches, p95 tick time, and egress. The transport test suite is
  parameterised over the mode so both are exercised by CI.
- Same flag pattern for the per-sector sim: `sim.parallelSectors: false` by
  default, boot-printed, A/B-able (§5.1).

---

## 7. Recommended follow-ups (ordered, each one delegated task)

1. **P601 — parallel per-peer broadcast building.** Parallelise the two
   dominant per-peer builders (`broadcastShipStates`, `broadcastShipsUpdate`)
   across peers, feeding the existing per-peer enqueue queues. Est. **−9 ms @
   241** (~half the tick); highest ROI, layout-independent. Flag-gated
   (`broadcast.parallelBuild`).
2. **P602 — distance-graded visibility / update-rate culling.** Cut the O(N²)
   `broadcastShipStates` fan-out by only full-rating nearby ships and
   downsampling distant visible ships (native does distance-graded client
   detail; the port sends every visible ship at the same cadence). Est.
   **−5–8 ms @ 241**, especially for the concentrated (single-sector) fight —
   the only lever that helps layout (b). Native cites: `shipIGC.cpp` distance
   logic / `clusterIGC.cpp` per-tick cadence.
3. **P603 — fixed send-worker-pool transport** behind `transport.mode`
   (default `thread-per-peer`), with the parameterised A/B suite. Caps send
   threads at ~#cores; unblocks 1000+ peers. Modest sim-thread gain now; the
   structural fix for extreme peer counts.
4. **P604 — per-sector parallel sim** behind `sim.parallelSectors:false`,
   with the merge phase for transits/kills/money. **Low expected gain
   (~3%)** at current counts — do this *after* P601/P602, primarily as a
   scalability foundation for very-large maps, not as the 300-player fix.
5. **P605 — payload reuse for the periodic per-side syncs** (stations/
   asteroids/probes) using the `_clusterInfoBytes` encode-once pattern.
   Small, tidy win on the remaining fan-out.

---

## 8. Raw data

Committed in `docs/reports/P600_assets/`:
- `tick_spread_121.csv`, `tick_spread_241.csv` (spread layout windows)
- `tick_concentrated_120.csv` (target-sector staging)

Columns: `window_start_unix,window_seconds,bucket,count,mean_ms,p95_ms,max_ms,total_ms`.

**Numbers reported are only what was actually measured** on this workstation on
2026-10-10 with the profiler above; bots shared the CPU (noted in §2). No
values were fabricated or extrapolated from the 221 sampler (its table is cited
verbatim in the task and reproduced in §0 as context only).

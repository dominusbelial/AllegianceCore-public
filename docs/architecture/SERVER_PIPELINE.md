# P631 — Server Pipeline Architecture Study

> From "everything inside the tick" to a scalable pipeline.
> Study + design doc + phased, flag-gated plan. No production code landed by this
> task; prototypes referenced here are throwaway measurements only.
>
> Companion report: `docs/reports/P631_ARCHITECTURE_STUDY.md` (summary, measurements, phase tasks).
> Supporting prior work cited throughout: P600, P603, P604/P604b, P605/P606/P611,
> P619/P619c, P621/P621b, P632–P636d, P645/P646.

---

## 0. Executive summary

The 200v200 baseline (390 bot clients, `size_scale 13`, Alderan) is not bound by
a single cost — it is bound by **two different cliffs depending on where the
fleet is**:

1. **Distributed / travelling (P600 regime).** `step:update` (the simulation) is
   only ~3–4% of the tick; the cost is **per-peer broadcast building** — an
   O(N × V) per-peer personalised ship-state stream plus the visibility sweeps.
   The lever here is parallel + encode-once fan-out (much of P603/P604 already
   attacks this).
2. **Crowded single sector (clash38, the real 200v200 limit).** With ~387 bots
   fighting in one sector (Alderan, `sector:1052`), `step:update` balloons to
   ~35 ms mean / p95 56–63 ms while `sim.combat` is only 8–9 ms and
   `broadcastShipsUpdate` 8–9 ms. The O(ships × neighbours) collision/combat
   pass inside one sector is now the binding cost. The lever here is **intra-
   sector parallelisation (spatial partitioning with a deterministic merge) and
   cutting per-ship cost** — the opposite of the P600 conclusion, which was
   taken at counts where no single sector was saturated.

The target design reconciles both with one shape:

- **Sim/publish/send split.** The sim thread produces an **immutable per-tick
  snapshot**; publishing workers encode against per-client acked baselines; the
  transport threads only send. Encoding leaves the sim thread entirely.
- **Encode-once fan-out.** Encode a shared byte buffer per (sector × visibility
  group × tier) once; each client holds a list of buffer references. Turns the
  O(N × V) personalised encode into O(G) encodes + O(N × refs).
- **Interest tiers** (target/near full-rate, mid reduced, far summaries),
  comparable in *intent* to native's heavy/light deviation budget but sized for
  today — and critically **the client must interpolate/dead-reckon between the
  sparser tiers** (this port's renderer-only client already dead-recks; extend it).
- **Delta compression** against client-acked baselines on the unreliable
  physical-state lane, and a separate reliable lane so ACK/repair bursts never
  delay live state (the failing case the xorgy port documented).
- **Per-sector parallel simulation** with a deterministic merge and warp
  hand-off, for the crowded-sector cliff.

Each phase is flag-gated with an A/B on 221 (like P603/P604) and has an explicit
rollback flag. The recommended order leads with the cheapest, highest-leverage,
lowest-risk changes first (broadcast/publish split, then interest tiers, then
intra-sector sim) so each step is measurable against the 200v200 baseline.

---

## 1. The problem: everything inside the tick

The current server advances on one 50 ms tick driven by
`Server.Lifecycle.Tick` → `SimPass` (`Server.Broadcast.cs:75`). SimPass runs
sequentially on one thread (the "sim thread"); every stage is guarded by a
`Step(...)` exception barrier (`Server.Lifecycle.cs:1403`) and timed under a
`step:<name>` bucket by `TickProfiler` (P600).

Anatomy of one SimPass (from `Server.Broadcast.cs:75-466`):

| Stage | What it does | Scaling |
|---|---|---|
| `drain` | pump transport receive events (`_host.Drain`) | O(inbound msgs) |
| `update` | `Mission.Update(now)` for every mission → every sector → every object | O(S × ships) sim |
| small post-update steps | pod-pickup, queued-order reject, npc auto-launch, countdowns, ballots, ripcords, rescues, relaunches, treasure, building-effects | O(effects) |
| `crossSideExportSweep` / `crossSideMineExportSweep` / `crossSideShipStatusSweep` | per-(recipient, visible ship/mine) EXPORT latches + status changes | O(N × V_visible) |
| `broadcastShipStates` | per-peer personalised stream: own ship (SingleShipUpdate 78), teammate `posBatch`, team/NPC exports + loadouts; parallel build (P603) but serial apply | O(N × V) |
| `broadcastStations`/`Asteroids`/`Probes` | per-side batched object syncs | O(side × seen) |
| `broadcastShipsUpdate` | per-player LIGHT/HEAVY ship updates from the per-cluster candidate list (P138/P139) | O(N × cluster) |
| `clusterWarnings`, `broadcastMissileLock`, `broadcastMissiles`, `broadcastOwnShipMining/KillBonus`, `broadcastMissileTrackWarning`, `broadcastScoreRoster`, `broadcastLeadIndicator` | owner-only / per-side auxiliary streams | O(N) |
| `disconnectSummary` / `shipUpdateSummaries` | rolling `[egress]` + GC/alloc + acceptance windows | O(N) periodic |
| `EndTick` | roll the profiler window | O(1) |

The broadcast stages **build N personalised updates on the sim thread** (since
P603, the build phase of `broadcastShipStates` runs on a `Parallel.For`, but
`broadcastShipsUpdate` and the sweeps are still serial) and then hand N TCP
connections to either thread-per-peer or the P605 worker pool. Two transports:
`pool` (P605/P606/P611 — the default since P887) and thread-per-peer (the old
dedicated-writer-thread path, selected explicitly). Egress is drained by the
sim thread via `SendPeer`/`SendPeerCoalescing` (coalescing the per-tick ship
stream into batches), so the sim thread both *encodes* and *queues* every byte.

### Two measured cliffs

**Cliff A — distributed, broadcast-bound (P600, P604, clash timelines).**
Across player counts on the showcase, `step:update` stays ~3–4% of the tick
(P600: "per-sector parallel simulation would attack only ~3–4% of the tick").
Instead `step:broadcastShipStates` is O(N×V): measured 1.48 → 5.24 → 17.35 ms at
~121/225/391 players in `port` mode, and per-player egress 76.9 → 149.9 →
239.3 kB/s (P604:16-23). The O(N×V) `crossSideExportSweep` reached **461 ms in
combat** with `[xexport]` spam on the earlier runs (task statement).

**Cliff B — crowded single sector, sim-bound (clash38, added 2026-10-14).**
With the bot host fixed (two bot processes + server GC), 221 ran **below 20 Hz
for 16 minutes** while ~387 bots fought in one sector: 74–86 ticks/5 s, tick
mean 50–65 ms. The profiler at 16:30:22Z put `step:update` at **35 ms mean / p95
56–63 ms**, "almost entirely `sector:1052` (Alderan)", with `sim.combat` 8–9 ms
and `broadcastShipsUpdate` 8–9 ms. So a single saturated sector is now the real
200v200 limit, and intra-sector parallelisation — dismissed at low density in
P600 — becomes a first-class lever.

---

## 2. Measured baseline (numbers this study is anchored to)

All figures below are taken from the listed report/timeline files on disk
(reproduction commands in §7). They are prior **measured** 221/workstation runs;
this task adds no new server build.

**Per-step / per-model at ~391 players (P604:16-23, settled 5 s window):**

| metric | ~121 | ~225 | ~391 |
|---|---|---|---|
| `step:broadcastShipStates` ms — port | 1.48 | 5.24 | 17.35 |
| `step:broadcastShipStates` ms — native | 0.68 | 1.56 | 3.26 |
| tick-wall mean ms — port | 3.78 | 9.69 | 25.11 |
| tick-wall mean ms — native | 2.98 | 6.66 | 13.98 |
| egress per player kB/s — port | 76.9 | 149.9 | 239.3 |
| egress per player kB/s — native | 11.1 | 12.8 | 16.6 |

**GC/allocation and stalls (P619):**

| run (392 players) | ticks/5 s | tick p95 | tick max/window | gen2 / window |
|---|---|---|---|---|
| clash12 `native`, workstation GC | ~41 | 74 ms | 2.2–2.4 s | 1–2 |
| clash13 `port`, workstation GC | 16–34 | 1.2–1.8 s | 1.2–1.8 s | 54–67 |
| clash14 `port`, server GC | (recover) | 65–80 ms | ~2 s once/100 ticks | — |

Server GC fixes the *general* p95 but **one ~2 s tick per 100-tick window
survives both modes and server GC**; the `[slowtick]` attribution (P619) names
it as a **synchronous inbound-receive burst in `step:drain`** (captured 432 ms
locally; `unaccounted≈0, pause+0` rules out GC/broadcast). Root hypothesis:
hundreds of bot ACKs aligned to the 100/40-tick batching arrive synchronously
and are drained serially on the sim thread. This is a receive-path stall, not a
send-path stall — relevant to §5.e transport and the bot-side §5.g.

**200v200 single-sector (clash38, marks.txt + task):** ticks 74–86/5 s, tick
mean 50–65 ms; `step:update` ~35 ms mean / p95 56–63 ms dominated by
`sector:1052`; `sim.combat` 8–9 ms; `broadcastShipsUpdate` 8–9 ms.

**Per-tick / per-broadcast structure (source):** `Server.Lifecycle.cs:1121-1134`
(Tick → SimPass → cadence), `Server.Broadcast.cs:75-466` (SimPass stages),
`Server.Broadcast.cs:564-574` (serial vs parallel `broadcastShipStates`),
`Server.Broadcast.cs:1275-1330` (parallel `BuildPeerShipStates` + serial
`ApplyBroadcastPeerEffects`), transport caps at `Server.Lifecycle.cs:1274-1293`.

---

## 3. Cost model

Work and bytes expressed as functions of players **N**, ships **V** (visible to
a peer), sectors **S**, and per-client visibility tier. Assume 20 Hz tick
(`c_dsecUpdateClient`), homogeneous fleet in a few sectors.

| quantity | `port` (today) | `native` (P604) | target (this doc) |
|---|---|---|---|
| sim work per tick | Σ_sectors O(V_s²) worst (combat) | same | partitioned O((V_s/k)²·k) + merge O(V_s) |
| broadcast build work | Σ_peers O(V_peer) encodes ≈ O(N·V̄) serial+parallel | O(N·c) heavy/light (c≈2+2) | O(G·encode) + O(N·refs) |
| ship-state bytes/s/client | ~240 kB/s (port) | ~11–17 kB/s (native) | ~1.6–10 kB/s target tier-dependent |
| per-side syncs (stations/astro/probes/missiles/exports/status) | identical in both | identical | unchanged, grouped |
| encode duplication | V̄ encodes per peer (N-fold) | per peer, but tiny | shared per visibility-group |

Concretely (fits P604's measured rows): `port` is O(N·V̄) encode with per-player
egress ~239 kB/s at 391 players (mostly the per-tick teammate `posBatch`);
`native` collapses ship-state to a bounded heavy/light budget (~1.6 kB/s
ship-state budget per P604:30) but still does the non-ship per-side syncs
identically, so real per-player egress is ~11–17 kB/s. The **target** keeps
native's bounded-ticket idea but (i) spreads it across explicit interest tiers
instead of a hard 2-heavy/2-light cap, (ii) encode-once per visibility group so
the fan-out cost is O(refs) not O(encodes), and (iii) delta-compresses each
tier against an acked baseline so steady state << full state.

**Memory.** Today the sim holds all objects in mission/sector structures and the
broadcast allocates per-peer byte arrays every tick (the P619 alloc storm —
clash13's 54–67 gen2/window came from the per-tick personalised stream under
workstation GC). The target moves to: a **pooled immutable snapshot** (stable,
reusable byte buffers), a small ring of `Revision` buffers per sector/tier (8
raw revisions + periodic checkpoints, as the xorgy port bounds), and per-client
ack/baseline bookkeeping O(N). Peak footprint shifts from N-fold transient
encode buffers to a fixed-depth revision ring — roughly O(S·tiers·revisions) of
buffers plus O(N) of refs/acks, independent of per-peer encode churn.

---

## 4. Target design

### 4.a Sim / publish / send split

Three ownership domains, only one on the hot tick path:

- **Sim thread** (existing): `drain` → `update` → build an **immutable
  snapshot** of this tick's authoritative state, publish a pointer to it, and
  move on. It does **not** encode or send.
- **Publishing workers** (separate from the sim thread; the P605 pool becomes a
  publish pool): take the latest immutable snapshot, compute each visibility
  group's delta vs that group's baseline, **encode once per group**, and enqueue
  the encoded block refs to the relevant clients' send queues.
- **Network threads / transport** (thread-per-peer or pool): only send.

Why this fixes the measured pain: encoding leaves the sim thread (so
`step:broadcastShipStates` 17.35 ms and `crossSideExportSweep` 461 ms stop
inflating the sim tick), and the immutable snapshot eliminates the race that
makes parallel builds scary today (P603's parallel build produces byte-identical
results but still applies serially on the sim thread; with a snapshot the apply
is just enqueue-refs). Cadence stays the sim's; publishers lag it by at most one
tick (acceptable: the client interpolates).

### 4.b Encode-once fan-out

Today `broadcastShipStates`/`broadcastShipsUpdate` encode **per peer**. Instead:

- Group clients by (sector, side-visibility group, interest tier). A group is
  the set of clients that receive the **same** encoded bytes for a given
  category this tick (same sector, same side's visibility of a ship set).
- **Encode the canonical state once per group revision** (mirroring the xorgy
  port's `ArenaEncoder`: "encodes canonical state once per domain revision";
  healthy peers share its normal delta). Each client holds a **list of buffer
  refs** (which group revisions it subscribes to), not its own copy.
- Per-client lists are cheap O(N × tiers) pointers; the heavy encode is O(G)
  where G ≪ N.

For the crowded-sector case this converts the per-peer personalised stream from
O(390 × 387) encodes into O(few groups) encodes + O(390) ref lists — a >100× cut
in encode work for the ship-state category.

### 4.c Interest management tiers vs native's deviation ranking

Three tiers per client (by relevance):

- **T0 target / near** — full rate (20 Hz), full fidelity: own ship, current
  command target, enemies within engagement range.
- **T1 mid** — reduced rate (e.g. 4–10 Hz), reduced fields (position/heading,
  no per-tick fractions): allies and mid-distance contacts.
- **T2 far / strategic** — low rate summaries: sector counts, last-known
  sector/state (this port already streams `ClusterWarningUpdate`/`SHIP_STATUS`
  last-known data, so the category exists).

Compare with native (`FedSrv.CPP:3742-4196`): native keeps a **per-cluster
heavy/light budget** — `cHeavyPerInterval = 2`, `cLightPerInterval = 2`
(w0dk4 hardcode at `:3844-3846`), a proportional light share
`(cShipUpdates·cLightPerInterval)/cShips` (`:3870`), sorted **by deviation** via
`CmpShipDeviation` (`:3923`, comparator `:3614-3621`), with deviation bands
(heavy-served reset to 0; light +0.70+rand; skipped +1.00+rand; `:3937-3950`)
and the command target promoted first consuming a heavy slot (`:4141-4146`),
capped by `cbAvailable = 1600·interval` bytes/s (`:3836`). Its **intent** —
always show your target, and spend scarce bytes on the most-diverged ships — is
exactly interest management. Its **mechanism** (a hard 2+2 cap, modem-era 1.6
kB/s) is an era constraint: the real client compensated with a local IGC sim,
which our renderer-only client lacks (P604b). The target keeps the *priority*
idea but expresses it as explicit tiers with rates chosen for today's networks
and the client's dead-reckoner, not a fixed 2+2 budget.

### 4.d Delta compression against acked baselines

Each group revision keeps a delta from the previous acked baseline; a client
acks a baseline epoch, and gets (a) the normal incremental delta if it is
healthy, or (b) a self-contained **repair delta** for its baseline cohort if it
lagged (the xorgy port's scheme). Bound history with a small ring of raw
revisions + periodic full checkpoints (their "eight raw revisions and periodic
checkpoints bound the history"). Unchanged categories reuse the existing
revision (no re-encode). Because deltas are built **once per group**, not per
client, the ACK bookkeeping is per-client but the encode is shared.

### 4.e Transport: UDP lanes vs QUIC vs TCP; encryption and identity

- **Keep TCP as today's safe default** for the deployment's first phases (it is
  correct, the 200v200 runs already ship on it, and bot/peer reachability is
  proven). The binding problems are not transport-native at 390 one-sector
  (send is parallel; the measured stall is an **inbound** `step:drain` burst,
  P619) — so transport should be **lower priority** than §4.a–4.d.
- **Target: UDP with lanes via GameNetworkingSockets (GNS)** for internet
  deployments. GNS gives unreliable + reliable channels over one UDP socket,
  fragmentation, and encryption — and its own distinct **service thread** for
  crypto/socket work, which is exactly the "network threads only send" split.
  Licence is **BSD-3** (confirmed upstream); C# bindings exist (official plain-C
  interface; community wrappers: `ValveSockets-CSharp` nxrighthere, MIT;
  `Valve.Sockets.AutoGen` TSRBerry; `GameNetworkingSockets.Net` vrimar, MIT —
  wrap the BSD-3 C lib). The xorgy GNS fork (`scalability-experiments`,
  e8e735701d) adds an **io_uring reactor + shared payloads** for study (cite,
  don't copy).
- **Must-have lane separation** (the xorgy port's documented failing case): a
  reliable burst (ACKs, repairs, joins) must never delay the unreliable
  physical-state lane. So: physical state = unreliable lane; control/join/repair
  = reliable lane; the two never share a fifo.
- **QUIC** (HTTP/3 stack) is an alternative but heavier to integrate in the
  server (no service-thread split as natural, more alloc in managed stacks);
  not recommended as the first internet transport. Revisit only if GNS C#
  bindings underperform.
- **Encryption & identity:** internet-facing servers need per-connection
  encryption (GNS provides it) and a server identity/authentication story
  (cert/key config + a reconnect ticket) — out of the first LAN phases, in
  before any public exposure. On a trusted LAN (the 200v200 rig) encryption is
  optional; make it config-gated so the LAN cost is avoided but not "lost".

### 4.f Per-sector parallel simulation + warp hand-off

For the crowded-sector cliff (clash38):

- **Within a sector**: partition collision/combat spatially (a coarse grid or
  BVH; P621's convex-hull collision and P621b's broadphase already exist as
  primitives). Each worker processes its cell's ship pairs against a bounded
  neighbour halo; results merge **deterministically** (fixed cell iteration
  order, commutative reduction, no dependence on thread count) so the sim stays
  reproducible — the tick currently is deterministic by design
  ("Deterministic and fast" — `Server.Lifecycle.cs:1138`).
- **Across sectors** (the P600 "not the lever" verdict applies *only* at low
  density): parallelise `Mission.Update` per sector where sectors are
  independent — they mostly are, with cross-sector effects flowing through
  warps. **Warp hand-off**: a ship crossing an aleph moves ownership from
  sector-thread A to sector-thread B at a tick boundary via a small hand-off
  queue drained deterministically (warp costs, target sector arrival already
  handled in `PerformRipcordTeleport`/warp logic — reuse that serialisation
  seam).
- Estimate on 221 (Ryzen 9 6900HX, 8C/16T): splitting the single saturated
  sector's combat across ~4–8 worker threads with a deterministic merge should
  bring `step:update` from ~35 ms toward the parallel floor (~sim.combat 8–9 ms
  ÷ workers + merge), i.e. comfortably back to a ≥40 Hz tick for the worst case.
  Verify with a local crowded-sector repro (see §5), not on 221.

### 4.g Client side: interpolation, prediction, one simulation

- The port shares **one simulation core** between server and client
  (`Allegiance.Core`); own-ship prediction is P447/P448. Keeping a single
  simulation is what lets the client **dead-reckon between sparse tier updates**
  without a divergent local sim — this is the thing native's local-IGC-sim gave
  the real client and our renderer-only client lacks (P604b). So the tiers'
  sparsity is *safe* only if the client dead-reckons kinematically (P448a/b
  `ShipDeadReckoner` already replays the real flight model forward, with a 1.5 s
  extrapolation cap) and re-seeds on every pose arrival (both SingleShipUpdate
  and HeavyShipsUpdate re-seed it — P604:107). Extend that seam to the tier
  stream.
- Interest tiers + deltas also cut **client decode cost**: fewer entities parsed
  per tick, fewer allocs, less receive-thread work — directly addressing the
  bot-side costs below and the real client's frame time.
- One-simulation invariant to protect: prediction corrections must land on the
  shared core, not a per-view replica, or interpolation/prediction diverges.

### 4.h Native-faithful vs transport/implementation (footer classification)

| Behaviour | Native cite | Class | What the port must do |
|---|---|---|---|
| Per-cluster heavy/light budget (2+2) | `FedSrv.CPP:3844-3846` | **era constraint** (w0dk4; modem budget `:3836`) | Replace with interest tiers (intent kept) |
| Deviation ranking prioritises stale ships | `FedSrv.CPP:3923,3937-3950` | **design** | Keep as tier-promotion priority |
| Command target always sent (heavy slot + fractions) | `FedSrv.CPP:4075-4125,4141-4146` | **design** (gameplay) | Keep in T0 target tier |
| Ship-update ~±1.5 s validity window | `FedSrv.CPP:3772` | design (era timing) | Keep concept; tune to tier rate |
| Drone update every ~3rd frame | `FedSrv.CPP:3804` | design (fairness) | Fold into mid/low tier |
| UDP unreliable for ship updates | `FedSrv.CPP:4189` (`FM_NOT_GUARANTEED`) | design | Unreliable physical lane |
| Per-sender position reference (delta) | `FedSrv.CPP:4127-4133` | design | Keep; basis for delta baseline |
| Per-peer personalised encode | today's port (`Server.Broadcast.cs`) | **impl choice** | encode-once fan-out |
| One 50 ms serial tick | `Server.Lifecycle.cs:1121` | impl choice | sim/publish/send split |

### 4.i Rust readiness

Two layers to port:

1. **A shared sim-core crate** exposing the deterministic sim (sectors, ships,
   combat, collision) through a **C ABI** (`#[no_mangle] extern "C"` tick
   entry, opaque `World` handle). Both the C# server *and* the C# client already
   share `Allegiance.Core`; the crate can be the single authoritative sim both
   FFI into — preserving "keep one simulation" while escaping managed GC/allocation
   on the hottest path (the clash13 54–67 gen2/window storm and P619 alloc
   storm are managed-GC phenomena a native sim simply does not have).
2. **The publish/encode + network layers** port cleanly to Rust later (channel/
   actor model maps naturally to `tokio`/crossbeam; GNS is C, so FFI is the same).

**Risks:** (a) rewriting the sim risks subtle behavioural drift from the 20-year
native rules — mitigate by shipping the C-ABI crate *underneath* the C# core and
A/B-ing for byte-equal tick output before any rewrite; (b) FFI marshalling costs
per tick — mitigate by bulk snapshot exchange (dense structs, not per-field
calls); (c) ecosystem/time.

**Do first:** the sim/core crate boundary + C ABI + a deterministic vectorised
sector-update; keep the server orchestration in C#. A full rewrite of
orchestration/networking is NOT recommended as a first step — the cost/benefit
favours (1) first.

---

## 5. Phased plan (flag-gated, A/B on 221, rollback per phase)

Each phase is a **delegatable task (12–28 min, split as needed)** behind a
feature flag, with an A/B like P603/P604 and an explicit rollback flag. Every
phase measures the 200v200 baseline locally (`showcase`, `size_scale 13`,
Alderan, staging on) **before contact and in combat**, reporting ticks/5 s, tick
p95/max, gen2/window, kB/s per client, and disconnect reasons; bots must leave
home and reach target AND produce kills > 0 (30v30 combat gate, P632 protocol).
Rollback = flip the flag back to today's default; the flag's default must be
**the pre-phase behaviour, byte-for-byte** (as P603/P604 ship).

Acceptance metrics at 390 one-sector and at 1,024 multi-sector are listed per
phase. Recommended order (cheapest / lowest-risk / highest-leverage first):

- **Ph.0 Baseline (no code).** Pin the current 200v200 numbers reproducibly
  (commands in §7) as the A-side of every later A/B. ✓ done conceptually by this
  task (numbers in §2).
- **Ph.1 Snapshot hand-off for broadcast** (`broadcast.immutableSnapshot`).
  Build the immutable per-tick snapshot and route the *encode* work to a publish
  pool off the sim thread; byte-identical output. Rollback flag: off.
  Metric: `step:broadcastShipStates`/`crossSideExportSweep` leave the sim tick;
  sim tick mean recovers to the sim-only floor.
- **Ph.2 Encode-once fan-out for the ship-state category** (`broadcast.encodeOnce`).
  Group by (sector, side-visibility, tier); encode once; clients hold buffer
  refs. Rollback: off. Metric: per-peer encode count → O(refs); `step:`
  broadcast ms collapses; egress unchanged (byte-identical first, then tiered).
- **Ph.3 Interest tiers** (`broadcast.interestTiers=T0:T1:T2` with rates).
  Add T0/T1/T2 with client dead-reckoning between tiers (reuses P448a/b
  `ShipDeadReckoner`). Compare with native's deviation budget in the report.
  Rollback: off. Metric: per-player egress at 390 ≤ native (~17 kB/s) while
  kills/5s ≈ baseline (tier T0 keeps targets). 1,024 multi-sector: egress bound
  flat.
- **Ph.4 Acked delta baselines** (`broadcast.deltas`). Per-group delta vs acked
  baseline + repair delta for laggers; ring of revisions. Rollback: off. Metric:
  steady-state ship-state bytes well under full-state; no repair storms.
- **Ph.5 Decouple the inbound drain** (`receive.decouple`, P619 follow-up). Fix
  the ~2 s aligned-inbound-burst stall: bound the drain per tick (P646 already
  bounds it) and/or split receive processing off the sim thread. Rollback: off.
  Metric: `[slowtick]` aligned-drain stalls disappear; p95 stable under server GC.
- **Ph.6 Intra-sector parallel sim** (`sim.parallelSectorCells`) — TRIED AND
  REMOVED (P858): the collision-detection parallel path was implemented (P667,
  byte-identical determinism proven) but the 200v200 rig A/B (2026-10-27) showed
  no gain over the serial path at the clash peak, so the flag, env var, config
  keys and P667Bench were dropped. The `CollisionDetector` parallel API itself
  remains, exercised by the detector-level tests.
- **Ph.7 Warp hand-off + per-sector threads** (`sim.perSectorThreads`) for the
  1,024 multi-sector target. Rollback: off. Metric: distributes `step:update`
  across S sectors; 1,024 multi-sector tick ≥ 40 Hz.
- **Ph.8 UDP lanes via GNS** (`transport.lanes`, LAN-safe default off) for
  internet scale; reliable/unreliable lane separation. Rollback: thread-per-peer
  TCP (an explicit `transport.mode` away). Metric: 1,024 enc conns, unreliable lane never delayed
  by reliable bursts; p95 server work near sim floor. Encryption/identity phase
  before public exposure.

Order rationale: Ph.1–2 attack Cliff A (broadcast) at zero behavioural risk and
compound; Ph.5 fixes a measured pathological stall for free; Ph.6–7 attack Cliff
B (the true 200v200 single-sector limit); Ph.8 and §4.i (Rust) are the furthest
horizon.

---

## 6. Risks & validation

- **Determinism.** The sim is deliberately deterministic. Every parallel phase
  must prove byte-equal tick output (seed-controlled A/B) before it can claim a
  win — a nondeterministic sim breaks replays/tests and the shared-core client
  invariant (§4.g).
- **Renderer-only client.** Tier sparsity is only safe because the client
  dead-recks (P448). Validate client smoothness with a real-client window
  screenshot proof per the project's visual-change rule (backbuffer capture,
  2560×1440) — not offscreen renders.
- **Bot-side dependency.** Bots depend on the broadcast/wire (P606 lesson).
  Every phase touching broadcast/transport/wire must run the live bot gate:
  bots leave home, reach Alderan, and kill (>0 within ~150 s at 30v30).
- **Bot-host coupling.** 200v200 throughput at the rig is co-determined by the
  bot host (P632–P636d). All A/Bs must record the bot host configuration (process
  count, GC mode) so a transport/publish change isn't confounded by bot-side
  decode/publish cost (P636d: the snapshot 0-kills were a *receive-side publish*
  cost, 390×25×400 decode/s — remind the plan that client/bot decode cost falls
  with tiers+deltas).
- **Don't touch 221** except via A/B config flags; reproduce locally.

---

## 7. Reproduction commands (baseline measurement)

- Timelines: `~/allegiance-metrics-ab-2026-10-11/clash*.txt` (columns: time
  peers ticks/5s tick_mean_ms bss_ms rss_mb tx_mbps kills_30s) and its
  `marks.txt` legend (clash → commit → config). Re-derived with the server's
  tickprof `[egress]` window (P604b/P619 alloc+GC rows) and `[slowtick]` capture
  (`ALLEGIANCE_SLOW_TICK_MS`).
- Per-step: enable tickprof CSV
  (`ALLEGIANCE_TICK_PROFILER=/tmp/p.csv` or `profiler.enabled`), settle 5 s,
  read `step:*` buckets; crowded-sector reads under `sector:<id>` (P600).
- Per-model A/B: `ALLEGIANCE_SHIP_UPDATE_MODEL=port|native`,
  `ALLEGIANCE_BROADCAST_PARALLEL_BUILD`,
  `ALLEGIANCE_TRANSPORT_MODE=thread-per-peer|pool`,
  `ALLEGIANCE_TRANSPORT_POOL_WORKERS` (labelled at boot and in the report header).
- Local 200v200: own temp worktree + local server, `spawn_scenario showcase
  size_scale 13`, Alderan, staging on; bot processes + GC mode recorded; stop
  bots by your own PID.

---

## 8. References

- Native: `~/Development/Allegiance/src/FedSrv/FedSrv.CPP:3614-3621` (CmpShipDeviation),
  `:3742-4196` (heavy/light budget + deviation), `fsship.h:65-66` (deviation acc).
- Port source: `src/Allegiance.Server/Server.Lifecycle.cs` (tick,
  `:1121-1134`; `Step` `:1403`; config resolution `:1206-1384`),
  `src/Allegiance.Server/Server.Broadcast.cs:75-466` (SimPass stages),
  `:564-1330` (serial/parallel `broadcastShipStates`), `ServerHost.cs` (drain).
- Reports (all `docs/reports/`): P600 (per-sector, sim is ~3–4%), P603
  (parallel build), P604/P604b (native budget; egress table), P605/P606/P611
  (transport pool/robustness), P619/P619c (GC, alloc storm, `step:drain` stall),
  P621/P621b (collision broadphase/hulls), P632–P636d (bot side, snapshot
  lethality/publish cost), P645/P646 (transport/receive hardening).
- Reference designs: xorgy/Allegiance `multiport` `docs/native-network-scale.md`
  (1,024 conns, 4×256/29 sectors, 60 Hz sim/10 Hz publish, server work p99
  9.82 ms, encode-once `ArenaEncoder`, shared arenas, delta/repair baselines,
  publication worker, GNS service thread, reliable-burst-fails case);
  xorgy/GameNetworkingSockets `scalability-experiments` e8e735701d (io_uring
  reactor + shared payloads); Valve GameNetworkingSockets (BSD-3, plain-C
  interface, C# wrappers listed in §4.e).

---

## Native behaviour classified (quick table)

See §4.h for the full table. Summary: gameplay/visibility semantics (deviation
priority, target-always-heavy, per-sender reference deltas, unreliable ship
updates) are **design** and kept; the fixed 2+2 heavy/light budget and modem 1.6
kB/s cap are **era constraints** replaced by tiers sized for today; per-peer
personalised encode and the one-serial-tick shape are **implementation choices**
this study replaces with sim/publish/send split + encode-once fan-out.

## Modern alternative considered

Native's bounded-heavy/light + client-local-sim model is replaced by
snapshot-interpolation-ready **interest tiers + acked delta baselines +
encode-once fan-out** (modern games/engines), because our client has no local
sim (P604b) and the managed server pays a GC cost per encode that a native sim
does not. Chosen: keep native *intent* (always show target, spend on the
diverged), adopt modern *mechanism* (tiers, deltas, shared encoded buffers,
lanes), and let the shared-one-sim core let the client dead-reckon. Trade-off
documented: sparser tiers put more weight on client interpolation, which must be
proved on a real client window.

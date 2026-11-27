# PERF-F1 — Second server performance review (read-only)

Base: master `28debb3c`. This is a review: no source, test or config file was changed. Every claim is marked *(read)*
(seen in this revision), *(measured)* (observed locally for this review, method in "What was measured") or *(estimated)*
(a bound or expectation that nobody has timed). Unprefixed files are under `src/Allegiance.Server/`; `Core/` means
`src/Allegiance.Core/`. Native references name paths under the original Allegiance source tree.

## Summary

The remaining tick time at 200v200 is in three places: the simulation's per-tick passes whose cost grows with what a
fight leaves behind, the ranked ship-update stage that runs one sector's viewers on one thread, and the teammate
builder's every-pair walk. The remaining allocation is mostly outside the tick steps altogether: 31% in the send
workers and 17% in the reader threads, which no earlier report or fixture measured. The five items below are the ones
I would act on first. Numbers are from a local 195v195 match on one shared 16-thread host (JIT build, bots on the same
machine): good for shape and for shares, not for rig milliseconds.

**1. Treasure pickups test every ship against every treasure (N1; this is P1094's subject).** `sim.economy` was a
median 6.1 ms of a 9.8 ms `step:update` during the fight and grew from 3.6 ms to 7.0 ms as loot accumulated, while the
other simulation buckets stayed flat *(measured)*. That is the same shape as the late-match `step:update` growth on
the rig. The pass is ship × treasure with a hash probe and a swept-segment test per pair
(`Core/Model/Sector.cs:1957-2023`); a conservative grid that visits candidates in the original order keeps the
callback sequence identical. Expected 5 to 6 ms in the measured state *(estimated)*. I did not build it and it should
go to the P1094 lane rather than become a competing task.

**2. Ranked ship updates do each viewer's private work on the sequential thread (F3).** Ranking must stay in viewer
order because it rewrites the shared deviation, exactly as native does. But ranking is a small part of the stage; the
visibility and stamp pass, the floor sweep and the packet encode are private to each viewer and could run on the
builders before and after a short serial ranking pass (`Server.RankedPose.cs:22-165`). The stage is 11.5 ms with 400
viewers in one sector and a median 5.6 ms live *(measured)*; expected about 4 ms and 2.5 to 3.5 ms *(estimated)*, with
the largest gain at the peaks. It is byte-identical by construction but concurrent, so: default-off flag and a rig
A/B. F7 (112-byte stamps in 400 hash tables) shrinks the same stage and composes with it.

**3. The teammate builder re-checks every same-side pair every tick (F8).** 79,600 pairs and about 320,000 hash
probes per tick to send, on a settled roster, almost nothing. `bss.build` is a median 2.0 ms and p95 4.5 ms of wall
time across 15 builders *(measured)*. With the status change gate on, a recipient that is fully caught up can only
receive status for subjects in this tick's changed set, so the walk can visit just those, in the same order.
Expected 1.5 ms of mean and 3 to 4 ms of p95 *(estimated)*; the risk is a missed invalidation, so flag and rig A/B.

**4. Almost half of all allocation is two transport-thread sites, both trivial to remove (F2, F4).** Every send-worker
pass copies its peer roster (`Core/Transport/TcpTransport.cs:2814`): 30.9% of sampled bytes. Every inbound frame is
allocated by the reader and immediately copied again (`:1551`, `ServerHost.cs:2344`): 16.8% together. PERF-R1 listed
these ninth and thirteenth of fourteen and measured neither, and P1087-P1089 could not move the rig's allocation
median because they worked on owners that are a few percent of a live match. Add the own-pose message chain that
the parallel builder still allocates although the serial builder pools it (F5, about 9%) and the per-tick
`[missilelock]` log string that is formatted before the verbosity check (F6, 4.6%): four small, provably identical
changes remove about 55% of the allocation *(measured shares, estimated total)*.

**5. The server collects about four times a second because of a runtime default (F1).** With server GC in its default
adaptive mode the local server collected every 12.6 MB, 3.9 times a second, 6.6 ms per pause, 1.3 ms of pause per
tick period, although each collection promoted a third of a megabyte *(measured)*. Turning the adaptive mode off by
environment variable cut the pause total by about 90% in two runs. The effect on the tick was mixed: p95 improved by
1 to 6 ms and no window exceeded 40 ms, but the mean improved in one run and not in the other, single pauses became
longer, and resident memory doubled or more. It is worth one rig batch because it costs nothing; item 4 gets most of
the same benefit with no trade.

Also worth knowing: the send path (one worker wake and one `send` call per frame) used 72 to 82% of a core, more than
the simulation thread's 39 to 49% *(measured, F9)*; the incoming-missile warning is a flat 0.6 to 0.7 ms on every tick
(F10, PERF-R1's finding 6); and the discovery latch gains one entry per peer per treasure and was reallocated on the
large-object heap twice in 6.5 minutes (N2). On one vCPU with 20 players the server uses under 5% of the CPU; the
visible cost there is thread hand-offs, not simulation.

What I could not do: measure on the rig or on the AOT build, get allocation call stacks (sites are assigned from
thread, type and size), repeat the GC arms, or time any proposed change — this review changed no code.

## Tick map, current master

Symbols: P seated viewers, S flying ships, N ordered models in a sector, R rocks, T treasures in a sector, G missions,
I inbound events admitted this tick, D deliveries queued this tick. "Live" numbers are the 5 s profiler window at 5 min
into the local 195v195 run (see "What was measured"); they are local wall times on a shared host, useful for shape, not
as rig predictions. Rows marked **changed** differ from PERF-R1's map.

| # | Phase and bucket | Work and scaling *(read)* | Allocation *(read unless marked)* | Source |
|---|---|---|---|---|
| 1 | Loop wake, clock | `Loop → Tick → TickWork → SimPass`, then `SyncCadence` sleeps `period − cost` (a relative sleep, not an absolute schedule). `ObserveTimeDilation` feeds TiDi. | One `Stopwatch` per tick. | `Server.Lifecycle.cs:1704-1726,2130-2137`, `Server.Broadcast.cs:2618-2631` |
| 2 | Pass preamble | Console commands, `EnterTick` (profiler lock), open the snapshot (clear three shared tables, rent a container). | Each `Step` is a delegate; with the profiler on, each also builds `"step:" + stage` (about 45 short strings per tick). | `Server.Broadcast.cs:100-147`, `Server.Lifecycle.cs:2082-2110`, `Server.BroadcastSnapshot.cs:151-160` |
| 3 | `step:drain` | Pull at most the events present at entry into per-peer lanes, sort the lane order, round-robin select, sort the selection by (peer, sequence), handle. O(I log I + U log U), plus O(U²) list shifts as lanes empty. Live: mean 1.40, p95 2.20, max 9.8 ms. | Per tick: selection list, a `handled` dictionary, one `Queue` per active peer (created and dropped every tick), one closure. Per event on the sim thread: packet and message wrappers, per-message copy, decoded DTO. Per frame on the reader thread: body array, a second copy, the event, one closure. | `ServerHost.cs:2274-2347,2375-2476`, `InboundPacket.cs:15-45`, `Core/Transport/TcpTransport.cs:1526-1564` |
| 4 | `step:update` → `Mission.Update` | Sides, damage clock, periodic cluster discovery, then sectors in order. One chunk per tick at 20 Hz (`n = 1 + floor(dt / 0.25)`). With the profiler on: a `"sector:N"` string and a LINQ count over every model, per sector per tick. Live: mean 8.10, p95 12.70, max 18.0 ms. | `_missions.ToArray()`; profiler strings. | `Server.Broadcast.cs:148-160`, `Core/Model/Mission.cs:1047-1160,667` |
| 4a | `sim.prepare`, `sim.models` | Crowding cost walks every model (`OfType<Ship>`), then every model updates in snapshot order. O(N + per-model work). Live: 0.07 and 1.23 ms. | Snapshot lease is reused. | `Core/Model/Sector.cs:1000-1065` |
| 4b | `sim.combat`, `sim.collision`, `sim.mines`, `sim.warp`, `sim.docking` | As in PERF-R1. Live: 0.15, 0.74, small. | Detector storage reused (P928). | `Core/Model/Sector.cs:1080-1154` |
| 4c | `sim.mining` | Rock regen, then every ship: a player ship scans all mineable rocks through a bound delegate. O(S_player·R). Live: 0.40 ms. | Per sector per tick: a `HashSet`, a `Dictionary`, one `Func<Asteroid,bool>` per player ship. | `Core/Model/Sector.cs:4983-5015` |
| 4d | `sim.economy` | Building effects, then **treasure pickups: every eligible flying ship against every treasure**, O(S·T), with a hash probe and a swept-segment test per pair. Live: **mean 4.67, p95 5.41 ms**, the largest part of `step:update`. | Per sector per tick: a `List<Treasure>` and a `HashSet<Treasure>`. | `Core/Model/Sector.cs:1957-2023` |
| 4e | `sim.rescue` | **changed (P1087)**: the pair scan returns early unless the sector holds both a pod and a rescue-capable non-pod. With both present it is still every unordered pair, O(S²). Live (pods present): 0.47 ms. | Borrowed list. | `Core/Model/Sector.cs:1422-1436` |
| 4f | `sim.visibility` | Every tenth active pass. Live: 1.34 ms on due passes (`vis.scan` 0.58). | Participant arrays and index per sweep. | `Core/Model/Sector.cs:1237-1243` |
| 5 | Post-sim rules and drains | Gunfire seal, pod pickup assignment (each pod scans the sector's ships twice, O(pods·S); live 0.37 ms), order expiry, NPC launch, countdowns, rotation, side selection, ballots; then under the stats lock a name list copy and the lobby snapshot (counts peers per mission and builds the game parameters twice) every tick; then the ripcord, rescue, treasure and building drains. | `new List<string>(names)`, LINQ arrays for the lobby listing. | `Server.Broadcast.cs:27-42,164-367,649-696` |
| 6 | Identity, mine, discovery, status, command, detection sweeps | Incremental since P863. Live: 0.19, small, 0.36, 0.11, 0.05, 0.05 ms. | Audience lists, edge-triggered packets. | `Server.Broadcast.cs:1787,2079,2171,2463,2358`, `Server.CommandRelay.cs:53` |
| 7 | `step:broadcastShipStates` | `bss.snapshot`: copy peers, group by side into new lists, walk **every model of every sector** to group same-side NPCs, prewarm loadouts. `bss.build`: `Parallel.For` over peers; each peer walks its whole side, about four hash probes per teammate pair. `bss.apply`: ordered sends. O(Σ P_side·(P_side + NPC_side)). Live: mean 2.62, p95 5.31 ms (`bss.build` 2.33). | **changed (P1089)**: result objects and effect collections are reused. Still per tick: the side dictionary and lists; per moving peer: `SingleShipUpdate`, `ServerShipUpdate`, wire snapshot, orientation copy, message, two one-element arrays, packet, a `LiveShip` copy, and at encode a frame array and two byte arrays. | `Server.Broadcast.cs:1284-1415,1450-1488,1533-1672`, `Server.TeammateScratch.cs:7` |
| 8 | Stations, asteroids, probes | Cadence or dirty side. Small. | Shared batches. | `Server.Broadcast.cs:2666,2848,2931` |
| 9 | `step:broadcastShipsUpdate` | Group peers by sector into a new dictionary, `Parallel.ForEach` over sectors, viewers of one sector strictly in order. Per viewer: `Begin` (visibility filter, one stamp probe per visible ship, age sample, prune walk), bounded-heap rank over the visible list, deviation writes, parent slot, floor sweep (second stamp probe per visible ship), turret relays, LIGHT message, HEAVY records, one pooled packet. O(Σ_c P_c·S_c). Live: mean 5.73, p95 7.50, max 17.8 ms. Surge fixture (400 viewers, one sector): 11.4-11.6 ms. | **changed (P1087, P1088)**: no second sort, no second visibility filter, stamps updated in place. Still per viewer per tick: the `Rank` iterator, the LIGHT entry array, message and bytes. Per call: the sector dictionary and lists, and one partitioner buffer per worker. | `Server.Broadcast.cs:3034-3100`, `Server.RankedPose.cs:22-165`, `Server.UpdateCadence.cs:141-241,376-403`, `ShipUpdateRanking.cs:16-34` |
| 10 | Warnings and owner feeds | Sector threats, ship warnings, missile lock (every armed peer every tick; live 0.25 ms), missile positions, own mining, own cloak, incoming-missile warning (every peer walks every model of its sector, O(Σ P_c·N_c); live 0.59 ms). | Missile lock: DTO, message, array, packet and one formatted log string per armed peer per tick. | `Server.Broadcast.cs:3564-3583,3608-3644,3696,3754` |
| 11 | Submit | Seal gunfire, hand the snapshot to the publisher under its gate. | Container recycled. | `Server.BroadcastSnapshot.cs:169-188`, `BroadcastSnapshot.cs:132-181` |
| 12 | End of pass | Every 100 ticks: disconnect summary, per-peer send-window lines, egress line. `EndTick`; the 5 s report is built on the sim thread. | Report strings. | `Server.Broadcast.cs:555-623`, `Server.Egress.cs:80` |
| 13 | Publisher thread | `Parallel.For` encodes every delivery (most are already encoded or pooled), then one ordered loop: take the publisher gate per delivery, then `Send`. Live: 3,700-5,400 packets per tick, 9.5-14 per peer per tick *(measured)*. | Per snapshot: closure and `Parallel.For` bookkeeping. Per non-pooled packet: a frame array, message bytes, packet bytes. | `BroadcastSnapshot.cs:45-54,251-343`, `Server.BroadcastSnapshot.cs:265-288` |
| 14 | Transport enqueue | Global transport lock and peer lookup, rent and copy the frame, peer lock, two or three `DateTime.UtcNow` reads, counters, then **wake the owning worker on every enqueue**. | Frames come from a private per-peer pool. | `Core/Transport/TcpTransport.cs:585-628,1938-1947,1949-2115` |
| 15 | Send workers | Per pass: copy the worker's peer roster, then per peer a lock, a zero-timeout `Poll` and **one `Send` call per frame** (gather writes are an experimental option, off). A pass that wrote anything loops again at once. | **One `PeerSender[]` per pass: 30.9% of all sampled allocation** *(measured)*. | `Core/Transport/TcpTransport.cs:2146-2168,2198-2209,2424-2425,2800-2838`, `:899`, `ServerConfig.cs:993` |
| 16 | Reader threads | One thread per peer (454 threads at 391 peers *(measured)*). Per frame: three `Poll` + `Read` pairs (first header byte, rest of header, body). | Body array per frame, copied again into the event: 16.8% of all sampled allocation *(measured)*. | `Core/Transport/TcpTransport.cs:1449,1526-1565,1615-1665`, `ServerHost.cs:2344` |

## PERF-R1 cross-check

PERF-R1's fourteen ranked findings, then its structural options. "Fixed" cites the current-master location.

| R1 # | R1 finding | Verdict | Evidence on current master |
|---|---|---|---|
| 1 | Reuse ranked visibility work | **Already fixed by P1088.** Agree it was the right first target; the stage is still the largest projection bucket. | Ranking reads `cadence.Visible` (`Server.RankedPose.cs:52,73`); stamps updated through refs (`Server.UpdateCadence.cs:213,346,389`). Surge ranked mean on this host 11.4-11.6 ms *(measured)*. What remains is F3 and F7 below. |
| 2 | Teammate builder scratch | **Already fixed by P1089.** Agree on the allocation, but it only covered the quiet-tick constructors. In a live match the same stage still allocates per moving peer (F5), and `bss.build` wall time did not move. | `Server.TeammateScratch.cs:7`, `Server.Broadcast.cs:1300-1301,1376-1412`. Live `step:broadcastShipStates` allocation is still 23 MB per 5 s window *(measured)*. |
| 3 | Visibility fallback scanner | **Disagree with the rank.** R1's 12 ms p95 came from a run with crowding switched off; its other sample was first contact. On the P1086 living fixture the due sweep is 0.97 ms mean, 1.0-1.2 ms p95 sustained and 1.5-1.6 ms on first contact, once per ten passes *(measured)*. Live it is 1.6 ms on due passes. It is a 0.15 ms per tick item. P858 already rejected a spotter cache on the rig. | `Core/Model/Sector.cs:1237-1243`; P835Bench output below. |
| 4 | Rescue pair precondition | **Already fixed by P1087**, and agree. Note it only helps while the sector has no pod or no rescuer; in a real fight pods exist and the full pair loop runs (`sim.rescue` 0.5 ms live *(measured)*). | `Core/Model/Sector.cs:1422-1434`. |
| 5 | Second ordering of the ranked prefix | **Already fixed by P1087.** Agree. | `ShipUpdateRanking.cs:28-33`. |
| 6 | Incoming missile warning | **Agree, open, ranked too low.** It is a flat 0.56-0.70 ms on every tick with 400 viewers *(measured, surge and live)*, more than the rescue scan that was fixed. | `Server.Broadcast.cs:3608-3644`, unchanged. |
| 7 | Encode LIGHT into the combined lease | **Agree, open.** About 2% of live allocation with the `Rank` iterator *(measured)*. | `Server.RankedPose.cs:82,110-111`. |
| 8 | Mining eligibility before the rock scan | **Agree, open.** 0.28-0.40 ms live; the bound delegate is 0.9% of live allocation *(measured)*. | `Core/Model/Sector.cs:5000-5011`. |
| 9 | TCP writer roster | **Agree with the fix, disagree strongly with the rank.** R1 put it ninth and said no cost was in its numbers. It is the largest single allocation site in a live match: 27-31% of sampled bytes *(measured)*. See F2. | `Core/Transport/TcpTransport.cs:2814`. |
| 10 | Mine export and audience setup | **Agree, open, small.** The side audience is still gathered before the cadence check in all three object streams. | `Server.Broadcast.cs:2674`. |
| 11 | Missile position fan-out | **Agree, open.** Unchanged; 9.6 MB per 200 ticks in the surge fixture *(measured)*. | `Server.Broadcast.cs:3696-3731`. |
| 12 | Eager diagnostics and metadata | **Agree, open, ranked too low.** The `[missilelock]` line alone is one formatted string per armed peer per tick: 4.6% of live allocation, 5 MB per window *(measured)*. See F6. | `Server.Broadcast.cs:3581,3642`, `Core/ConsoleLog.cs:156-161`. |
| 13 | Inbound packet ownership | **Agree, open, ranked far too low.** R1 asked for a count first. The count: reader-thread byte arrays are 17% of live allocation and `step:drain` is the joint-largest allocating step (40 MB per window) *(measured)*. See F4. | `Core/Transport/TcpTransport.cs:1551`, `ServerHost.cs:2326,2344,2400-2424`. |
| 14 | WebSocket send storage | Not assessed (TCP-only control). | — |
| — | Structural: across-sector simulation | **Agree it is low value**, and P600 and P858 say the same. | — |
| — | Structural: data layout and capture locks | **Partly agree.** The capture gate is uncontended today only because the hot sector is single-threaded; F3 removes that restriction and then needs the precomputed records R1 describes. | `Server.RankedPose.cs:136-143`. |
| — | Structural: a different teammate broadcast model | **Agree**, and F8 gives a narrower exact version that needs no new subject model. | `Server.Broadcast.cs:1533-1672`. |
| — | Structural: publisher and transport ownership | **Agree on the caution.** F2 and F9 need no shared lease. | — |
| — | Structural: server NPC steering, reliable emission history | Not measured here; no evidence either way in the live profile (`sim.models` 1.6 ms, export sweep 0.24 ms). | — |

What PERF-R1 did not look at: garbage-collector behaviour (F1), the cost of the simulation's treasure pass in a real
fight (N1), the transport's thread wake-ups and system calls (F9), and the discovery latch's growth (N2). Its two
fixtures (a fighter-only simulation scene and a projection replay with synthetic peers) contain no inbound traffic, no
sockets, no loot and no moving-ship own poses, which is why about nine tenths of the live allocation was invisible to
it: the surge fixture allocates 0.32 MB per tick and P835Bench 0.08 MB per tick, against 2.4 MB per tick in the local
live run and 3.5-4.7 MB per tick on the rig.

## Ranked findings

Ranked by expected effect on the 200v200 tick (mean and p95) with the rig baseline settings, with allocation breaking
ties. "Live" means the local 195v195 run, fight phase, medians over 87 five-second windows unless stated. Expected wins
are *(estimated)* unless marked otherwise; none of the proposed code changes has been built or timed.

### N1. Treasure pickups test every ship against every treasure — the growing part of `step:update` (for P1094)

- **Where** *(read)*: `Core/Model/Sector.cs:1957-2023`. Every flying, sided, player-piloted, rescue-capable ship is
  tested against every treasure in the sector: a `HashSet` probe (`done.Contains`), a code check and a swept-segment
  test per pair. A new `List<Treasure>` and `HashSet<Treasure>` are built per sector per tick.
- **Measured**: `sim.economy` (this pass plus building effects) has a median of **6.09 ms** per tick (p95 6.79) out of a
  `step:update` median of 9.84 ms. It grows through the fight as loot accumulates: 0.09 ms before contact, 3.6 ms at
  100 s, 4.9 ms at 300 s, 7.0 ms at 600 s, while `sim.models` (1.3-1.9 ms), `sim.collision` (0.7 ms) and `sim.rescue`
  (0.5 ms) stay flat. From the size of the per-tick `Treasure[]` list growth (0.8% of sampled allocation) the hot sector
  held roughly 500 to 1,000 treasures *(estimated)*; with about 350 eligible ships that is 175,000 to 350,000 pair tests
  per tick.
- **Bearing on P1094**: this has the same shape as the reported late-match growth (`step:update` rising to about 9.6 ms).
  The local fight was much more kill-heavy than a rig match (11,262 kills in 11 minutes), so it reached this state early;
  on the rig the same count of drifting loot would arrive later in a match. P1094 should check `sim.economy` and the
  treasure count per sector in the rig's late windows before looking elsewhere. Not proposed as a separate P-task.
- **Behaviour-preserving change**: (1) gather only treasures with a pickable code, and skip the hash probe while `done`
  is empty; (2) put the treasures in a uniform grid keyed by their swept bounds for this tick, and for each ship visit
  only the treasures in the cells its own swept bound touches, **in ascending original treasure index**, running the
  unchanged scalar test on each. Fall back to the full scan when any input is non-finite or the cell count is degenerate.
- **Equivalence**: the pass has exactly two effects, the site callback `HitTreasureEvent` and the `done` /
  pending-pickup lists, and both happen only for pairs that pass the geometric test. A pair that fails it is pure
  arithmetic. If the grid bound is conservative (pickup radius + half hull length + both displacements this tick) then
  the set of passing pairs is identical, and visiting ships in model order and treasures in model order gives the same
  callback sequence, the same accept or refuse answers, the same pending order and no RNG draw. Native decides pickup
  through the cluster's collision set with the same average-velocity sweep (`Igc/treasureIGC.cpp:239-259`, gate
  `Igc/treasureIGC.h:86-87`, site rule `FedSrv/FedSrv.CPP:13558-13650`), so a broad phase is the native shape.
- **Tests**: a differential test that runs the old and new pass on seeded scenes (crossing treasures, flags, refused
  pickups, two ships on one treasure, zero-length sweeps, NaN positions) and compares the full callback transcript and
  pending list; the existing treasure, flag and P928 golden tests.
- **Expected win**: 5 to 6 ms of `step:update` in the measured state, growing with loot count. **Risk**: low to medium
  (one conservative bound to get right).

### F3. Ranked ship updates do each viewer's private work on the one sequential thread

- **Where** *(read)*: `Server.Broadcast.cs:3073-3083` runs all viewers of a sector on one worker, in order, because
  ranking mutates the shared `Ship.Deviation` (native `FedSrv/FedSrv.CPP:3923-3950` does the same: one deviation per
  ship, adjusted after each viewer's sort). But `BroadcastRankedPose` (`Server.RankedPose.cs:22-165`) does much more
  than rank. Of its stages only three touch deviation or another viewer's state: the target promotion and the rank loop
  (`:36`, `:82-88`) and the shared LIGHT capture (`:85`). The rest is private to the viewer: `Begin` — visibility filter,
  one stamp probe per visible ship, age histogram, prune walk (`Server.UpdateCadence.cs:190-241`); the parent slot;
  `MarkPose` and the floor sweep (`:344-350`, `:376-403`); the turret relays; the LIGHT message; the HEAVY record copy and
  relative-position write; the packet.
- **Measured**: 11.4-11.6 ms mean for 400 viewers in one sector (P863b surge, this host); live median 5.61 ms, p95
  7.56, worst 22.9 ms. P1088's instrumented profile of the same loop put the rank loop at 5.7 of about 40 µs per viewer
  before its change; `Begin`, the floor sweep and the record writes were the rest.
- **Change**: three passes per sector instead of one. (1) Parallel over the sector's viewers: `Begin`. (2) Sequential
  in the existing viewer order: resolve the target, rank, write deviations, capture LIGHT entries, and record each
  viewer's HEAVY list and LIGHT entries into that viewer's scratch; capture each selected ship's HEAVY record once
  into an immutable per-sector table. (3) Parallel over viewers: parent slot, `MarkPose`, floor sweep, relays, encode
  into the pooled packet. Then append the deliveries in viewer order. Fetch the sector's cached candidate list and
  the turret relay sets once, before the parallel passes, so no worker fills a shared cache.
- **Equivalence**: pass 2 reads exactly what the rank loop reads today (the visible list from `Begin`, the target, the
  current deviations) and performs the same deviation writes in the same viewer order, so every later viewer sees the
  same values. `Begin` reads only the candidate list, visibility flags, positions and the viewer's own stamps; none of
  those is written during projection. The floor sweep reads the viewer's own stamps, cursor and the pose marks from
  its own rank result. The shared HEAVY record for a ship is a pure function of the ship and the tick, so it does not
  matter which viewer captures it first (`Server.RankedPose.cs:136-143`). Each viewer still gets one packet with the
  same bytes; delivery order across viewers is already unordered between sectors and has one keyed delivery per peer.
  No RNG is involved (the port deliberately does not use native's random jitter, `P863_REPORT.md:221-222`).
- **Constraint respected**: this is not the "parallel same-sector ranking" that P863 and P863b declined. Ranking stays
  serial.
- **Tests**: the P1088 per-viewer differential (packets, floor suffix, stamps, age counters, deviation vector at every
  viewer boundary) under both builder modes; the P863b surge transcript SHA-256; `UpdateCadenceTests`,
  `P891BoardedPoseTests`, turret relay tests; a stress test with shuffled worker scheduling.
- **Expected win**: 11.5 → about 4 ms in the one-sector 400-viewer case (the 2-3 ms rank loop stays serial, the rest
  divides by the builder count). On the rig, where the mean is 5.3 ms, 2 to 3 ms of mean and more at the peaks, which
  is where the budget breaks. **Risk**: medium (thread safety of per-peer telemetry and the capture gate). Ship behind
  a default-off flag and A/B on the rig.

### F8. The teammate builder re-checks every same-side pair on every tick

- **Where** *(read)*: `Server.Broadcast.cs:1533-1672`. For each recipient the worker walks its whole side and for each
  teammate probes the export latch, the last-status table, the changed-status set and the loadout-version table, then
  repeats a shorter version for same-side NPCs. Two sides of 200 are 79,600 pairs and about 320,000 hash probes per
  tick into tables with tens of thousands of entries. On a settled roster almost none produces a packet.
- **Measured**: `bss.build` median 2.01 ms wall, p95 4.51, worst 28 ms, with 15 builders (so on the order of 10 ms of
  core time per tick); 0.80-0.87 ms in the surge fixture. P1089 removed its allocation, not its work.
- **Change**: give each recipient a "settled" mark, set at the end of a full walk in which every teammate and NPC
  ended exported, with a status tick, and with the sent loadout version equal to the current one. While the mark
  holds, the only packet the walk can produce is a status for a subject in this tick's changed-status set, so visit
  only those subjects, in the side's order. Clear the mark whenever its inputs can change: side roster membership or
  order, peer names, any removal from the latch tables the walk reads, the NPC list of the side, and a per-side sum of loadout
  versions (versions only increase, so the sum changes exactly when one does). Keep the full walk when the status
  change gate is off or the port position batch is on.
- **Equivalence**: with the gate on and native ship updates, the four per-pair decisions read only the latch tables,
  the changed-status set and the loadout version (`:1539-1541`, `:1563-1566`, `:1599-1603`). If the first three latches
  are satisfied and nothing was removed, the export and loadout branches cannot fire and the status branch reduces to
  set membership; visiting the changed subjects in side order emits the same packets in the same order.
  `SHIP_STATUS_CHANGE_GATE_P991.md:84-89` already guarantees the set is fixed before the builders run. Native sends
  status only on change (`FedSrv/FedSrv.CPP:3690-3699`).
- **Tests**: the P1089 transcript theory (cold join, quiet ticks, part replacement, side switch, NPC dock, churn at a
  tick boundary) is the right proof and already compares against serial; add one deliberate break per invalidation
  source, as P1089 did.
- **Expected win**: 1.5 ms of mean tick and 3 to 4 ms at p95 for this stage, and about 10 ms of builder core time per
  tick returned to the other parallel phases. **Risk**: medium (a missed invalidation is a missed packet). Default-off
  flag and rig A/B.

### F1. The runtime's default gen0 budget makes the server collect about four times a second

- **Where** *(read)*: `src/Allegiance.Server/Allegiance.Server.csproj:23-24` sets server GC and concurrent GC and
  nothing else; no Dockerfile, compose file or script sets any other GC option. The server therefore runs with the
  .NET 10 runtime's default for `System.GC.DynamicAdaptationMode`. Switching that one option off changed the
  behaviour as shown below, so the default is the adaptive mode, which keeps gen0 small.
- **Measured, baseline**: in the fight phase the server made **3.93 collections per second** (1,038 gen0 and 814 gen1
  in 472 s) while allocating about 49 MB/s, i.e. one collection per 12.6 MB. Each pause had a median of 6.6 ms and a
  p95 of 9.7 ms although a collection promoted a median of only 0.34 MB. That is 26.4 ms of pause per second, 1.30 ms
  per tick period. About half of the wall time is the cadence sleep and about half of the allocation happens on
  transport threads, so only part of that lands inside a tick; a tick that does contain a pause is 6 to 10 ms longer.
- **Measured, two alternative settings** (one run each, same scenario and host, medians over windows 40-126):

  | | Baseline | Adaptation off | Adaptation off + 64 MB gen0 cap |
  |---|---:|---:|---:|
  | Environment | — | `DOTNET_GCDynamicAdaptationMode=0` | plus `DOTNET_GCgen0MaxBudget=4000000` |
  | Collections per second | 3.93 | 0.22 | 0.20 |
  | Pause per 5 s window, median | 130.3 ms | 8.6 ms | 18.2 ms |
  | Pause per tick period | 1.30 ms | 0.09 ms | 0.18 ms |
  | Single pause median / p95 / max | 6.6 / 9.7 / 24.0 ms | 8.8 / 28.7 / 39.0 ms | 21.5 / 30.6 / 34.8 ms |
  | Pauses over 20 ms | 3 in 472 s | 25 in 429 s | 45 in 427 s |
  | Tick wall mean, median of windows | 22.75 ms | 19.17 ms | 22.85 ms |
  | Tick wall p95, median of windows | 29.79 ms | 23.35 ms | 28.60 ms |
  | Windows with p95 over 40 ms | 3 of 87 | 0 of 87 | 0 of 87 |
  | Worst tick per window, median / worst | 36.5 / 70.8 ms | 26.4 / 60.1 ms | 35.4 / 75.9 ms |
  | Allocation per window | 244.8 MB | 246.3 MB | 224.8 MB |
  | Heap after GC / resident memory | 241 MB / 0.40 GB | 251 MB / not sampled | 220 MB / 0.8-1.2 GB |

- **What this shows**: the collection count and total pause time are set by the runtime mode, not by the workload:
  both alternative runs cut pause time by 86-93%. The tick-time effect is **not** established. The first alternative
  run was 3.6 ms faster on the mean, but its fight spread over two sectors, which by itself shortens the sequential
  ranked stage; the second, whose fight sat in one sector like the baseline's, had the same mean as the baseline and
  a 1.2 ms better p95. The defensible local result is: mean unchanged to slightly better, p95 better by 1 to 6 ms,
  no window over 40 ms in either alternative run against 3 in the baseline.
- **The trade**: 18 times fewer pauses, but each one is longer (gen1 builds up to about 100 MB before it is
  collected) and resident memory roughly doubles to triples. The 64 MB cap did not shorten the pauses; it is not the
  right second knob. A worst tick is no better (75.9 ms in the capped run).
- **Rig expectation** *(estimated)*: the rig allocates 70-95 MB/s. At the 12.6 MB per collection measured here that is
  5 to 7 collections a second and 35 to 50 ms of pause per second. The rig's own `[gcprof]` lines (gen0 and gen1 per
  window, pause per window) confirm or refute this in one look; I did not have them.
- **Change**: none in game code. For an A/B set the environment variable on the rig. To make it a default, add
  `<GarbageCollectionAdaptationMode>0</GarbageCollectionAdaptationMode>` beside the two existing GC properties (the SDK
  maps it to the `System.GC.DynamicAdaptationMode` runtime option the same way it maps the server GC property).
- **Equivalence**: no game state, packet, byte or RNG draw depends on when a collection runs. It is a timing change.
- **Tests**: none in the repository can prove a GC setting. On the rig compare `[gcprof]` pause per window, gen counts
  and worst pause, tick p95, windows over 40 ms, worst tick, and resident memory per match.
- **Expected win**: up to 1 ms of mean tick and 1 to 6 ms of p95; possibly a worse single worst tick. **Risk**: low
  for correctness, real for memory and for the pause tail. Worth one rig batch because it costs nothing to try.
- **Interaction, and the better route to the same gain**: the allocation cuts F2, F4, F5 and F6 remove about 55% of
  the allocation, which under the default mode removes the same share of collections without lengthening any pause.
  Do those first; then decide whether the runtime setting still pays.

### F2. The send workers copy their peer roster on every pass: 31% of all allocation

- **Where** *(read)*: `Core/Transport/TcpTransport.cs:2814` — `snapshot = _peers.ToArray()` at the top of every worker
  pass. Every enqueue wakes the owning worker (`:2113`, `:2776-2779`), and a pass that wrote anything loops again
  immediately (`:2828`).
- **Measured**: `PeerSender[]` on the `peer-send-pool` threads is **30.9%** of sampled allocation over 6.5 minutes of
  the fight (27% over an earlier 3 minutes). The 16 workers made 43,000 voluntary context switches per second.
- **Change**: keep an immutable array per worker, replaced under the worker lock on assign and unassign; a pass reads
  the current array. PERF-R1 finding 9 describes the same fix.
- **Equivalence**: the same peers in the same order on every pass; no game state, packet or byte is involved. A pass
  that starts during an assign sees either the old or the new roster, as today.
- **Tests**: `TcpTransportSendWorkerPoolTests`, crowded `BroadcastSnapshotLoopbackTests`, plus assign and unassign
  during partial writes, idle wake and shutdown.
- **Expected win**: about a quarter to a third of the allocation rate (on the rig's 70-95 MB/s, 20-28 MB/s), and with
  it a proportional share of the collections in F1. **Risk**: low. No flag needed.

### F4. Inbound frames are allocated twice on the reader thread, and the drain rebuilds its scaffolding every tick

- **Where** *(read)*: the reader allocates the body (`Core/Transport/TcpTransport.cs:1551`), and the host copies it again
  into the event (`ServerHost.cs:2344`, `data.ToArray()`). The byte-budget update captures `charge` in a closure per
  frame (`ServerHost.cs:2326`). `Drain` creates and drops one `Queue` per active peer per tick (`:2400`, `:2449`),
  a selection list (`:2411`), a `handled` dictionary (`:2420`) and a closure (`:2424`); each event then
  becomes an `InboundPacket`, an `InboundMessage[]`, per-message copies and a decoded DTO (`InboundPacket.cs:15-45`).
- **Measured**: reader-thread byte arrays are **16.8%** of sampled allocation, all of them 65-128 bytes. On the sim
  thread, drain-owned types (`ITransportEvent[]`, the selection tuples, the `handled` entries, `Queue`, `InboundPacket`,
  `InboundMessage`, `InboundMessage[]`, `List<byte[]>`, the `charge` closure) add about 4%. `step:drain` is the joint
  largest allocating step (40.6 MB per window) and costs a median 1.55 ms per tick, p95 2.47.
- **Change, in order of value**: (1) hand the reader's exact-size body array to the host instead of copying it (an
  owned-array receive overload); (2) reuse the drain's list, dictionary and emptied lane queues; (3) use the
  `AddOrUpdate` overload that takes an argument instead of a closure; (4) later, slice messages from the owned frame
  as PERF-R1 finding 13 describes.
- **Equivalence**: the event carries the same bytes; admission, reservation, fair-share selection and the
  (peer, sequence) handling order are untouched. For (1), `ReadFrame` returns a fresh array of exactly the declared
  length that nothing else references, so transferring ownership changes no content.
- **Tests**: the ingress fairness and budget tests (P315, P646, P1023), malformed-frame tests, and a captured-input
  replay compared event by event.
- **Expected win**: (1) alone removes about 8% of allocation, (2) and (3) about 2%. Tick time: a few tenths of a
  millisecond of `step:drain`. **Risk**: low.

### F5. The parallel teammate builder still allocates the own-pose message chain the serial builder already pools

- **Where** *(read)*: `Server.Broadcast.cs:1471-1488` builds a `SingleShipUpdate`, a captured `ServerShipUpdate` (with a
  wire snapshot and an orientation copy), a `BroadcastMessage`, a one-element array that `PackBroadcast` copies again
  (`Server.BroadcastSnapshot.cs:89,94`), a `BroadcastPacket`, and a copy of the `LiveShip` record, for every peer whose
  ship moved; the publisher then allocates a frame array and two byte arrays to encode it
  (`BroadcastSnapshot.cs:45-54`). The serial builder writes the same message straight into a pooled packet
  (`Server.Broadcast.cs:856`, `PackOwnPose` at `Server.BroadcastSnapshot.cs:98-114`).
- **Measured**: on the builder threads `ShipWireSnapshot` 1.1%, `LiveShip` 1.0%, `BroadcastMessage[]` 1.2%,
  `ServerShipUpdate` 0.7%, `BroadcastPacket` 0.6%, `BroadcastMessage` 0.6%, `Orientation` 0.5%, `SingleShipUpdate` 0.3%,
  plus most of the builders' 5.2% of small byte arrays: about **9%** of sampled allocation. P1089 could not see this:
  the surge fixture's ships do not move between ticks, so this branch measured zero there.
- **Change**: use the pooled writer in the parallel builder, and keep the six last-broadcast fields in a small mutable
  per-peer record written in the ordered apply step instead of copying `LiveShip`.
- **Equivalence**: the bytes must equal the DTO encoding, including the ripcord byte (see N3). The dirty test, the
  coalesce key, the guarantee bit and the apply order are unchanged.
- **Tests**: `ParallelBroadcastByteIdentityTests`, the four P1089 transcript hashes, `FedPacketGoldenBytesTests`,
  `OwnShipCoalesceKeyTests`, plus a ripcording ship in the serial/parallel comparison.
- **Expected win**: about 9% of allocation; a small part of `bss.build`. **Risk**: low.

### F6. Log lines are formatted before the verbosity check

- **Where** *(read)*: `ConsoleLog.Write(string)` tests `Verbose` after the caller has built the string
  (`Core/ConsoleLog.cs:156-161`). `BroadcastMissileLock` formats a `[missilelock]` line for every armed peer on every
  tick (`Server.Broadcast.cs:3581`); 239 call sites in the server and core pass an interpolated string without a guard.
- **Measured**: `System.String` on the sim thread is **4.6%** of sampled allocation, almost all in the 129-256 byte
  bucket, about 370 per tick — one per armed peer. `step:broadcastMissileLock` allocates 5.0 MB per window.
- **Change**: add a `ConsoleLog.Write` overload that takes an interpolated-string handler which checks `Verbose` before
  any formatting and still increments the attempt counters. The compiler binds existing interpolated call sites to
  it, so no call site changes.
- **Equivalence**: no game state. Verbose output and the `TotalWrites` and probe counters are identical. The one thing
  to audit is that no interpolation hole has a side effect, since holes are no longer evaluated when the gate is off.
- **Expected win**: 4.6% of allocation and roughly 0.1 ms per tick. **Risk**: low.

### F7. The per-viewer cadence stamps are 112-byte entries in 400 hash tables

- **Where** *(read)*: `Server.UpdateCadence.cs:141-154,156`. Each viewer keeps a `Dictionary<short, Stamp>`. The stamp
  carries the floor fields (ship, last pose time, has-pose, generation) and, always, the interest-tier membership and
  the last served position and velocity, which the baseline never reads (tiers are off). `Begin` probes one entry per
  visible ship, the prune walk reads every entry again, and the floor sweep probes each a second time.
- **Measured**: `Stamp` is 112 bytes (128 per dictionary entry). At 400 viewers × 400 ships that is about 20 MB touched
  two to three times per tick by one thread, more than the rig CPU's 16 MB level-3 cache. P1088 measured 48 ns per
  stamp update on this path.
- **Change**: split the floor fields (24 bytes) from the optional tier and promotion fields, which move to a second
  table that exists only when a feature that reads them is on. A further step keeps the floor stamps in an array
  parallel to the visible list and carries them forward by a merge walk, which removes the hashing and the separate
  prune walk; that one needs a fallback for reordered sectors.
- **Equivalence**: the same values are stored and read at the same points; identity replacement, side and sector
  resets and generation pruning keep their boundaries (the nine deliberate breaks in `RANKED_VISIBILITY_REUSE_P1088.md`
  cover them).
- **Tests**: `P1088RankedVisibilityTests`, `UpdateCadenceTests`, `UpdateAgeMetricsTests`, `InterestTierTests` (tiers on
  must still work), surge transcript hash.
- **Expected win**: 1.5 to 3 ms in the one-sector 400-viewer case; it shrinks the parallel part of F3, so the two
  compose. **Risk**: low for the split, medium for the merge walk.

### F10. Incoming-missile warning walks every model once per viewer

PERF-R1 finding 6, still open at `Server.Broadcast.cs:3608-3644`. Measured 0.56 ms in the surge fixture and a median
0.70 ms live (p95 0.90). The cost is P × N per sector and N grows with loot and ordnance, so it rises through a fight
like N1. R1's fix (one pass per sector that builds a target → best-lock map, same strict `>` from zero, presence
recorded separately, native reference `WinTrek/trekigc.cpp:5025-5065`) is right. Expected win 0.6 ms per tick. Risk low.

### F9. Transport: one worker wake and one `send` per frame, three `poll` + `read` pairs per inbound frame

- **Where** *(read)*: every enqueue takes the transport lock, the peer lock and then wakes the worker
  (`Core/Transport/TcpTransport.cs:585-628,1949-2115`); the worker sends one frame per call (`:2424-2425`); the reader
  issues `Poll` then `Read` for the first header byte, the rest of the header and the body (`:1534-1560,1615-1640`).
- **Measured** (10 s sample at 391 peers): the 16 send workers used **82% of one core** and made 43,000 voluntary
  context switches per second; the publisher thread used 18%; the 391 reader threads 11% with 9,800 switches per
  second; the simulation thread itself used 49%. The server offered 113,000 packets per second (about 14 per peer per
  tick). So the send path costs more processor time than the simulation thread. It is off the tick, but on the rig's
  8 cores it competes with 15 builders, and on one vCPU its wake-ups land inside the tick (see "One vCPU").
- **Change**: (1) wake each worker once after the publisher's delivery loop (or every few hundred deliveries) instead
  of once per enqueue; (2) run the existing experimental `transport.tcpBatchWrites` gather send on the rig; (3) read
  inbound bytes through a per-peer buffer so a burst is one `Poll` and one `Read`; (4) take one timestamp per enqueue
  instead of two or three.
- **Equivalence**: bytes, per-peer order, coalescing and shedding rules are unchanged. (1) and (2) change when a
  frame reaches the socket by up to one publisher pass, so they are timing changes: **default-off flag, rig A/B**, and
  watch update age. (3) must keep the idle, frame and absolute deadlines (P654, P671, P1023).
- **Tests**: the send-worker pool tests, partial-write tests (P645), deadline tests, loopback transcripts; on the rig,
  update age and queue depth.
- **Expected win**: most of the send workers' core, half of the readers'; no direct tick-time claim. **Risk**: medium.

### F11. Smaller items

| Item | Where | Measured | Change |
|---|---|---|---|
| `Parallel.ForEach` over a dictionary of sectors | `Server.Broadcast.cs:3073-3083` | Partitioner buffers 1.0% of allocation; the per-tick dictionary and lists on top | Reuse per-sector lists and run `Parallel.For` over an array. |
| LIGHT message and `Rank` iterator per viewer | `Server.RankedPose.cs:82,110-111`, `ShipUpdateRanking.cs:16` | `LightShipUpdateEntry[]` 0.7%, `LightShipsUpdate` 0.4%, `<Rank>d__6` 0.8% | R1 finding 7; replace the iterator with an index walk over the drained list and the served flags. |
| `PackBroadcast` builds two one-element arrays | `Server.BroadcastSnapshot.cs:89,94` | `BroadcastMessage[]` 1.7% | Single-message packet type or pass ownership of the array. |
| Probe target closure | `Core/Sim/Probe.cs:392-440` | 1.7% (`Probe.<>c__DisplayClass59_1`) | Hoist the rock lookup out of the lambda. |
| Mining scan delegate, set and dictionary | `Core/Model/Sector.cs:5000-5011` | `Func<Asteroid,bool>` 0.9%, 0.28 ms | R1 finding 8. |
| Mounted-part lookups through LINQ with captured arguments | `Core/Sim/Ship.cs:3619,4119,4500`, `Core/Sim/Weapon.cs:127` | `Func<MountedPart,bool>` 0.9% | Indexed loop helper. |
| Missile lock stream | `Server.Broadcast.cs:3564-3583` | DTO, message, arrays and packet per armed peer per tick | Fixed-size pooled write like the other owner-only warnings. |
| Publisher `Parallel.For` when almost every packet is already encoded | `BroadcastSnapshot.cs:273-285` | `Replica<RangeWorker>` 0.5% | Encode inline when the snapshot has few unencoded packets. |
| Pod pickup assignment | `Server.Broadcast.cs:649-696` | 0.32 ms median | Collect pods once; skip the pass when there is none. |
| Rescue pair loop once pods exist | `Core/Model/Sector.cs:1436` | 0.51 ms median | Precompute pod and rescuer flags per ship and skip rows that are neither. Same (i, j) order. |
| Profiler-only work (the rig runs with the profiler on) | `Core/Model/Mission.cs:1103-1107`, `Server.Lifecycle.cs:2089-2093`, `Server.UpdateCadence.cs:230` | Not separated | A LINQ model count per sector per tick, a string per stage, and an age-histogram sample per visible pair. Not a production cost; it inflates rig numbers slightly and equally in both arms of an A/B. |

### N2. The discovery latch grows by one entry per peer per treasure and is rehashed on the sim thread

- **Where** *(read)*: `Server.cs:599` is one set keyed by (peer, kind, id). A treasure spawn adds an entry for every
  seeing peer in the sector (`Server.Docking.cs:510-520`), the sweep adds the rest (`Server.Broadcast.cs:2203-2263`),
  and nothing removes a treasure's entries when it is picked up or expires; a peer leaving scans the whole set
  (`Server.Lifecycle.cs:2396`). `P884B_REPORT.md:160-165` found 2.9 million slots for about 10,000 live entries.
- **Measured**: two large-object-heap allocations of this set's entry array (about 34 MB each) in 6.5 minutes of the
  fight; the large-object heap grew from 56 MB to a median of 95 MB and a peak of 153 MB; both gen2 collections in the
  run were triggered by large-object allocation. A growth step rehashes every entry on the sim thread inside a kill;
  the worst `combat.treasure` sample was 49.5 ms in the window with the worst tick (70.8 ms). That the resize caused
  that tick is *(estimated)*, not shown.
- **Change that keeps behaviour**: store membership per object as a bitmap of peer slots (about 50 bytes per object
  for 400 peers, against about 8 KB today). Membership answers are identical; growth is per object, so there is no
  multi-million-entry rehash, and a peer leaving clears one bit per object.
- **Bearing on P1094**: a tick-spike source that grows through a match and resets at rotation. Pruning dead objects
  is a separate question that P884B declined and that touches id reuse; not proposed here.

### N3. Correctness note found while reading (not a performance item)

`PackOwnPose` clears the fixed part of the message and never writes the ripcord byte, and its non-pooled fallback
does not set `BIsRipcording` either (`Server.BroadcastSnapshot.cs:98-114`), while the parallel builder sets
`BIsRipcording = ship.IsRipcordActive` (`Server.Broadcast.cs:1476`, native `FedSrv/fsship.cpp:542,807`). The serial
builder (`Server.Broadcast.cs:856`), which small servers use, therefore appears to send 0 for a ripcording ship where
the parallel builder sends 1 *(read; not reproduced)*. F5 must write the byte either way.

## One vCPU, about 20 players

*(measured)* The server pinned to a single CPU with `ALLEGIANCE_EXPECTED_PLAYERS=20`, 20 bots (10 per side) on other
CPUs, a small fight in progress. The resolver chose serial build and a one-worker send pool, and the runtime fell back
to workstation concurrent GC (`[boot] gc = workstation/concurrent`) because it sees one processor.

| | Value |
|---|---|
| Tick wall, median of 10 windows | mean 1.46 ms, p95 2.25 ms, worst tick per window 3.9 ms (one 23 ms tick on the first kill, which is JIT compilation on the only CPU and does not exist in the AOT build) |
| Whole server | about 4.6% of the CPU |
| Simulation thread | 2.2% |
| Send worker | 1.1%, three quarters of it in the kernel; 1,212 voluntary switches per second for about 3,000 packets per second |
| 21 reader threads | 0.8%; 407 switches per second |
| Publisher thread | 0.4%; preempted 1,193 times per second |
| Largest tick buckets | `step:update` 0.55 ms; `broadcast.snapshotPublish` 0.43 ms (p95 1.10, worst 4.5); `sim.visibility` 0.33 ms on due passes; `sim.collision` 0.20; `step:drain` 0.10; `step:broadcastShipsUpdate` 0.08 |
| Allocation and GC | 12.8 MB per 5 s window, one gen0 per window, 1.8 ms of pause per window |

What costs most there, in order:

1. **The simulation itself** (`step:update`, 38% of the tick). Nothing in it is abnormal at this size.
2. **Thread hand-offs on the single CPU** *(measured, cause read)*. `broadcast.snapshotPublish` is 0.002 ms on 16 CPUs
   and 0.43 ms here, 30% of the tick: submitting the snapshot wakes the publisher (`BroadcastSnapshot.cs:132-181`),
   which takes the CPU from the simulation thread and runs its whole pass before the simulation thread gets back to
   close the bucket. Inside that pass every enqueue wakes the send worker (`Core/Transport/TcpTransport.cs:2113`),
   which preempts the publisher in turn (the 1,193 preemptions per second). The work has to be done on that CPU
   either way, but it is counted as tick time and it is the time-dilation controller's input
   (`Server.Lifecycle.cs:1711-1726`).
3. **One system call pair per frame** in the send worker and three per inbound frame in the readers (F9). With one
   CPU these are the same core the simulation uses.

At 20 players the server uses 3% of the tick budget and under 5% of the CPU, so none of this is a problem at that
size. If the single-CPU case should scale further, F9's "wake once per publisher pass" and gather sends are the
changes that matter most there, more than anything in the simulation; they are timing changes and would be measured
on the same pinned set-up. Server GC settings (F1) do not apply: the runtime is not using server GC on one CPU.

## Where the allocation comes from

Method: an in-process listener on the runtime's allocation-tick event (one sample per roughly 100 KB allocated on a
thread, with the type and size of the object that crossed the threshold) attached to the real server during the local
195v195 match, aggregated by thread name and type over 6.5 minutes of the fight (215,738 samples). Shares are of
sampled bytes. The exact counter over the same interval was 19.4 GB, **49.8 MB/s, 2.5 MB per tick**; the rig reports
70-95 MB/s, so absolute rig numbers are roughly 1.4 to 1.9 times these, and the mix on the rig is unknown. The sampler
gives no call stacks: owner and call site are assigned by thread, type, object size and code reading, so every
"call site" entry is *(measured)* for the share and *(read)* for the location.

**By thread**: simulation thread 34.8%, send workers 30.9%, per-peer reader threads 16.8%, pool workers (builders and
publisher encode) 14.3%, publisher thread 2.3%, other 0.8%.

| Share | Owner | Types seen | Call site | Finding |
|---:|---|---|---|---|
| 30.9% | Send workers | `PeerSender[]`, 129-256 B each | Roster copy per pass, `Core/Transport/TcpTransport.cs:2814` | F2 |
| 16.8% | Reader threads | `byte[]`, 65-128 B | Frame body `Core/Transport/TcpTransport.cs:1551` and its copy `ServerHost.cs:2344` | F4 |
| 6.7% | Builders | `ShipWireSnapshot`, `LiveShip`, `ServerShipUpdate`, `BroadcastMessage[]`, `BroadcastPacket`, `BroadcastMessage`, `Orientation`, `SingleShipUpdate`, `ExportShip` | Own-pose chain `Server.Broadcast.cs:1471-1488`; exports on relaunch `:1541-1556` | F5 |
| 5.9% | Pool workers | `byte[]` ≤128 B, `byte[][]` | Deferred encode of non-pooled packets, `BroadcastSnapshot.cs:45-54` (own poses are the largest steady group) | F5 |
| 5.7% | Sim thread | `byte[]` ≤128 B, `byte[][]` | Event encodes and `FedPacket.Pack(new[] { … })` at direct send sites, e.g. `Server.Docking.cs:489,511`; per-message copies in `InboundPacket.cs:15-45` | F4, F11 |
| 4.8% | Sim thread | `Treasure[]`, `Ship[]`, `Model[]`, `MountedPart[]`, `List<int>[]`, `int[]`, boxed list enumerators | Per-tick gather lists in the sector passes (`Core/Model/Sector.cs:1960-1963` for treasures), broad-phase pair lists, `foreach` over `IReadOnlyList` | N1, F11 |
| 4.6% | Sim thread | `string`, 129-256 B | `[missilelock]` line per armed peer per tick, `Server.Broadcast.cs:3581` | F6 |
| 4.5% | Sim thread | `Probe` closure 1.7%, `Func<Asteroid,bool>` 0.9%, `Func<MountedPart,bool>` 0.9%, `Func<Station,bool>` 0.3%, dock closure 0.4% | `Core/Sim/Probe.cs:392-440`, `Core/Model/Sector.cs:5011`, `Core/Sim/Ship.cs:3619,4119,4500` | F11 |
| 4.2% | Sim thread | `ConnectionId[]`, peer snapshot arrays, `BroadcastMessage[]`, `BroadcastPacket`, `TreasureSpawn` | Audience lists (`Server.Broadcast.cs:2674`), `SnapshotBroadcastPeers` in native-order phases (`Server.BroadcastBuffers.cs:68-69`), loot spawn fan-out (`Server.Docking.cs:509-520`) | F11 |
| 3.8% | Sim thread | `ITransportEvent[]`, selection tuples, `Entry<uint,int>[]`, `Queue`, `InboundPacket`, `InboundMessage`, `InboundMessage[]`, `List<byte[]>`, closures | `ServerHost.cs:2326,2400-2424`, `InboundPacket.cs:15-45` | F4 |
| 3.2% | Sim and pool | `LightShipUpdateEntry[]`, `LightShipsUpdate`, `<Rank>d__6`, partitioner `KeyValuePair<long,…>[]`, `Replica<RangeWorker>`, stamp table growth | `Server.RankedPose.cs:82,110-111`, `Server.Broadcast.cs:3073-3083`, `BroadcastSnapshot.cs:273` | F11 |
| 2.3% | Publisher | `byte[]`, `byte[][]`, queue nodes | `BroadcastSnapshot.cs:45-54`, `Core/Transport/TcpTransport.cs:1810-1815` | F11 |
| 1.7% | Sim thread | `VisibilityParticipant[]` and index | `Core/Model/Sector.cs` sensor sweep (retained by P928) | — |
| 1.2% | Sim thread | `ClientShipUpdate`, `ShipUpdate`, `Orientation` | Inbound decode in dispatch | F4 |
| 0.3% | Sim thread, large-object heap | `Entry<(ConnectionId, byte, short)>[]`, about 34 MB each | Discovery latch growth, `Server.cs:599` | N2 |
| 3.2% | Other | Long tail under 0.2% each | — | — |

The profiler's own per-step attribution agrees on the order of the steps (median MB per 5 s window, global counter, so
each step also counts whatever other threads allocated while it ran): `step:drain` 40.6, `step:update` 40.5,
`step:broadcastShipStates` 23.4, `step:broadcastShipsUpdate` 18.2, `step:broadcastMissileLock` 5.0. Those five sum to
128 MB of a 245 MB window; the rest is allocated while no step is open or between ticks — which is the send workers
and readers, and why no step-based report ever showed them.

**Why P1087-P1089 did not move the rig median**: they removed about 0.34 MB per tick of projection scratch and 8 KB
per pass of simulation scratch, measured in fixtures. In a live match those owners are a few percent of 2.5-4.7 MB per
tick. Two thirds of the live allocation is in three places no fixture exercises: the send workers, the reader
threads and the moving-ship own-pose path.

**GC behaviour** *(measured, baseline)*: 3.9 collections per second, 2.2 gen0 and 1.7 gen1 (44% are gen1), and two
gen2 in 472 s, both triggered by a large-object allocation. Promoted bytes per collection: median 0.34 MB. Heap: gen2
162 MB, large-object heap 95 MB median and 153 MB peak (it was 56 MB at the start of the fight), pinned-object heap
0.06 MB, 0 to 2 pinned objects per collection, about 1,100 GC handles. Pinning and the pinned heap are not a problem.
The large-object heap holds the fixed 21 MB egress counter table, the per-tick delivery lists (a 24-byte struct per
delivery, 5,000 or more per tick, so the list's array is over the 85 KB threshold and its references are rewritten
every tick) and the discovery latch (N2).

## What was measured, and how

Host: one container with 16 logical CPUs of an AMD Ryzen 9 9955HX and 16 GB, Linux, .NET SDK 10.0.404, Release JIT
build of master `28debb3c` (`dotnet build AllegianceCore.slnx -c Release`, 0 warnings, 0 errors). The host is shared
with other work (the container was about 90% idle before the runs; the machine's load average was about 15 on 32
threads), so wall times carry noise. About 40 minutes of measurement in total. Scratch scripts and logs are under
`.scratch/perff1/` (not committed).

### Fixtures

```bash
export DOTNET_gcServer=1 DOTNET_TieredCompilation=0
dotnet build tools/P835Bench -c Release
dotnet tools/P835Bench/bin/Release/net10.0/P835Bench.dll 400 240 60      # three runs; one run with 200
dotnet test tests/Allegiance.Core.Tests -c Release --no-build \
  --filter 'FullyQualifiedName~Surge_profiles_three_alternating_runs_per_arm' --logger 'console;verbosity=detailed'
dotnet test tests/Allegiance.Core.Tests -c Release --no-build \
  --filter 'FullyQualifiedName~P863ClashBudgetTests' --logger 'console;verbosity=detailed'
```

| Fixture | Result *(measured)* |
|---|---|
| P835Bench living fleet, 400 ships, sustained, 3 runs | `Mission.Update` mean 2.11 / 2.12 / 2.15 ms, p95 2.99-3.03, p99 3.27-3.36; 80.5 KB per tick; population guard held at 400. Buckets: `sim.models` 0.94, `sim.mining` 0.33, `sim.combat` 0.32, `sim.collision` 0.25, visibility 0.97 ms mean and 1.16 ms p95 on due passes. |
| Same, first contact | Mean 1.88-1.92 ms, p95 3.75-3.79; 191.4 KB per tick; visibility 1.50-1.54 ms on due passes. |
| Same, 200 ships, 1 run | Mean 1.36 ms, p95 2.05; 55.5 KB per tick. |
| P863b surge, 400 viewers, production arm, 3 runs | Projection + flush mean 14.62 / 14.69 / 14.45 ms, p95 15.45-15.54, p99 24.2-25.2; ranked ship updates 11.56 / 11.56 / 11.37 ms; teammate states 0.86-0.92; missile warning 0.56; 63.7-63.8 MB per 200 ticks. Test passed. |
| P863 clash budget tests | 12 of 12 passed. Recorded projection, current arm: ranked ship updates 10.95-10.98 ms, teammate states 0.88-0.90 ms. |

These fixtures time fighters with no loot, no inbound traffic and no sockets; they agree with the P1087-P1089 reports
after allowing for the different CPU, and they show none of the costs in F1, F2, F4, F9, N1 or N2.

### Live local match

The real server and the real bot driver on the one host, loopback TCP: showcase preset, `size_scale` 13, march mode on
`m2xspiderw.igc`, 391 seated bots, two teams with capacity raised to 200 each, code defaults otherwise (immutable
snapshot, encode once, parallel build with 15 workers, pool transport with 16 workers, native ship updates, native
GotoPlan, server GC, status change gate and gunfire batch on, time dilation auto), `ALLEGIANCE_TICK_PROFILER=1`.
Fleets met after about 90 s; the fight phase used below is windows 40 to 126 (3.3 to 10.5 minutes). Every window ran
100 or 101 ticks. The fight was far more lethal than a rig match (11,262 kills in the baseline run), which loads the
kill, loot and relaunch paths harder and sooner than the rig does.

To sample allocations without a profiler tool, a copy of the server's build output was run with two runtime-config
switches flipped in the copy only (startup hooks and event sources enabled; the project disables both), and a
small startup hook subscribed an `EventListener` to the runtime's GC keyword at verbose level. It records allocation
ticks by OS thread name, type and size bucket, and the suspend, restart, start and heap-stats events of every
collection. The hook itself allocates a little on a runtime thread ("other" in the tables).

| Run | Setting | Length | Used for |
|---|---|---|---|
| a | Default team size | 1 min | Discarded: sides capped at 100. |
| b | Baseline | 11.5 min | Tick map, allocation table, GC baseline, per-thread CPU. |
| c | `DOTNET_GCDynamicAdaptationMode=0` | 10.5 min | F1. |
| d | Adaptation off plus `DOTNET_GCgen0MaxBudget=4000000` | 10.5 min | F1, third arm. |
| e | Server pinned to one CPU, 20 bots, baseline GC | 3.5 min | "One vCPU". |

Baseline, fight phase, medians over 87 windows: tick wall mean 22.75 ms, p95 29.79 ms, worst tick per window 36.5 ms
(70.8 ms overall), 3 windows with p95 over 40 ms; allocation 244.8 MB per window; 563,000 packets offered per window
(14 per peer per tick); server process 143-150% of one CPU. Per-thread CPU over 15 s (run c): send workers 71.9% of a
core (45.1 of those points in the kernel) with 39,600 voluntary context switches per second; simulation thread 38.6%; the 15
pool workers 36.7% together; publisher 15.8%; the 391 readers 6.8% with 5,700 switches per second; the 16 GC threads
0.4%.

### Could not measure

- The rig, the Native AOT build, a WAN, a second match or a rotation. Everything live is the JIT build on one shared
  host with the bots on the same machine.
- Call stacks for allocations: `dotnet-trace` and `dotnet-counters` are not installed and nothing was downloaded.
  Call sites are assigned from thread, type and size.
- Hardware counters (cache misses, false sharing): no `perf`. F7's cache argument is from sizes, not from counters.
- Repeats: one run per arm for F1. No candidate code change was built or timed; this is a read-only review.
- The true late-match state of a rig match. N1 and N2 were seen in a compressed, kill-heavy local fight.

## Prior decisions respected

These constrain the findings above. Report names are files in `docs/reports/`.

| Source | Decision or measured rejection | How this review respects it |
|---|---|---|
| `P863_REPORT.md:29-30,142-143`, `P863b_REPORT.md:47-50` | Deviation is shared, order-dependent state. Same-sector viewers must rank in order; parallel same-sector ranking was deliberately not attempted. | F3 keeps the ranking and every deviation write on one thread in the existing viewer order. Only work that never reads or writes deviation moves to workers. |
| `P863b_REPORT.md:22-24` | A pooled pose lease has one delivery and only detached bytes cross to the publisher. | F3 and F5 keep one lease per delivery. |
| `P858_DROP_REJECTED_FLAGS.md:7-12,138-144`, `P667_PARALLEL_SECTOR_SIM.md` | Parallel collision cells and the spotter cache showed no gain on the rig and were removed. "The bottleneck was never collision detection." | Nothing here parallelises simulation detection or caches spotters. N1 is a different pass (treasure pickup) that neither experiment touched. |
| `P600_TICK_PROFILE_AND_PER_SECTOR_DESIGN.md:270-275` | Per-sector parallel `Mission.Update` buys at most 3% and nothing in a one-sector fight. | Not proposed. |
| `P838_GC_PROFILE.md:13-21` | GC pauses were "not the missing ~45 ms" at the time (pause share about 4%). | F1 does not claim GC explains a clash peak. It reports that the collection count (about four a second here) comes from a runtime default, not from the workload, that a tick containing a pause is 6-10 ms longer, and that local runs with the default changed improved p95 but did not clearly improve the mean. |
| `P619_STALL_GC_ALLOC.md:115-136,187-189,257-259` | Server GC and concurrent GC are baked into the project. Pooling the outbound packet array was blocked because the transport kept the array. GC tuning was left as a follow-up idea. | F1 is that follow-up, measured, with a mixed result. The transport now copies into its own pooled frame (`Core/Transport/TcpTransport.cs:1938-1947`), so the old blocker no longer applies to F5. |
| `P928_REPORT.md:262-273` | Recycling the gunner `Orientation`, visibility scratch reuse and projectile pooling were deliberately left. Any allocation cut needs the same iteration order, float results and RNG draws. | Not re-proposed as quick wins. `VisibilityParticipant[]` is listed in the allocation table (1.6%) for completeness only. |
| `P884B_REPORT.md:211,365-368` | Within-match pruning of dead-object discovery entries was judged not worth a reverse index; capacity is trimmed at rotation with a guard. | N2 does not prune. It keeps the same membership in a compact per-object layout, and reports new evidence (large-object-heap resizes during a match) for the lane that owns it. |
| `SHIP_STATUS_CHANGE_GATE_P991.md:84-89` | The status decision is made once per subject in the serial detection sweep; builders only read it. | F8 relies on exactly that set and adds no second decision. |
| `P887_REPORT.md:78-82`, `docs/PERFORMANCE.md` | Interest tiers, native broadcast order and decoupled receive stay off; a new idea goes behind a default-off flag and is kept only if the 200v200 A/B improves. | No finding depends on those options. The P-task split marks which items need a flag and a rig A/B. |
| `PUBLISHER_STREAM_INDEX_P968.md:39-44` | Every keyed delivery still takes the publisher gate and tests the current pending tick. | F9 changes when workers are woken, not the freshness test. |
| `P889_REPORT.md:48-50` | Pool sizes come from the resolver; oversized pools hurt small games. | The one-vCPU section uses the resolver's own choice and does not propose new thresholds. |
| `P324_TICK_COST_REDUCTION.md:94-99`, `BROADCAST_SLICE_D_P990.md:346-356` | The missile stream is not throttled; own pose, target, parent, guaranteed frames and turret relays are never dropped. | No finding changes a cadence, a packet boundary or a recipient set. |
| `P1087`, `P1088`, `P1089` reports | Rescue early-out, reverse heap drain, visible-list reuse, in-place stamps and teammate scratch reuse have landed with byte-identical transcripts. | Not re-reported. F3, F5, F7 and F8 build on them and name the same transcript tests as their proof. |

## Open questions

1. **Rig GC numbers.** What do the rig's `[gcprof]` lines show per window for gen0, gen1, pause and worst pause in the
   P1086-P1089 A/B? If gen0 is in the tens per window, F1 applies as measured here. If it is 1 or 2, the rig's runtime
   is not in the adaptation mode this host's JIT build is, and F1 is void there.
2. **Does the AOT build pick the same GC defaults as the JIT build?** Both use the same collector and the same project
   properties; this review ran the JIT build only.
3. **Treasure count late in a rig match.** How many treasures does the busiest sector hold in the windows where
   `step:update` reaches 9.6 ms, and what is `sim.economy` there? (N1; for P1094.)
4. **Do the 140 ms single ticks coincide with a large-object allocation or a gen2?** The slow-tick capture
   (`profiler.slowTickCapture`) reports GC counts per slow tick; N2 predicts a gen2 or a `combat.treasure` spike on some
   of them.
5. **How much of the ranked stage is the rank loop on the rig?** F3's gain is the stage minus the rank loop. P1088's
   split was taken before its own change; a fresh per-stage split on current master would size F3 before it is built.
6. **Is the send path's processor time visible on the rig?** A per-thread CPU sample (the script used here reads
   `/proc/<pid>/task/*/stat`) during a fight would show whether the send workers cost a core there too, and whether
   the builders are short of cores at the peaks.
7. **Is one run per arm enough for F1's tick numbers?** No, and the two alternative arms disagree on the mean. The
   pause numbers are firm; the tick numbers need the rig's usual two or three matches per arm.
8. **N3**: does a ripcording ship's own pose carry the ripcord flag under the serial builder? Not a performance
   question, but F5 depends on the answer.
9. **Treasure id reuse and the discovery latch**: entries for dead treasures stay until rotation. If a 16-bit id is
   reused within a match, the sweep's latch would already be set for the new object. Not investigated.

## Suggested P-task briefs

One finding or a tight group each. "Flag + rig A/B" means default-off and promoted only on a 200v200 improvement;
"direct" means provably identical output, covered by transcript tests, no flag needed.

| Brief | Scope | Proof | Ship |
|---|---|---|---|
| A. GC adaptation mode (F1) | Environment variable on the rig first; the project property only if the batch wins. Watch the pause tail and resident memory. | `[gcprof]` gen counts, pause per window, worst pause; tick p95; heap and resident memory per match. | **Rig A/B** (no code flag needed; it is a runtime setting) |
| B. Send-worker roster array (F2) | Immutable per-worker roster swapped on assign and unassign. | Send-pool and loopback tests; allocation sampler before and after. | Direct |
| C. Inbound ownership and drain scratch (F4 parts 1-3) | Owned body hand-off, reusable drain collections, closure-free budget update. | Ingress fairness, budget and malformed-frame tests; captured-input replay. | Direct |
| D. Pooled own pose in the parallel builder (F5, N3) | Pooled writer in `BuildPeerShipStates`; mutable last-broadcast record; settle the ripcord byte first. | Parallel-versus-serial byte identity, P1089 hashes, golden bytes, a ripcording ship case. | Direct |
| E. Lazy log formatting (F6) | Interpolated-string-handler overload of `ConsoleLog.Write`; audit holes for side effects. | Verbose output and counters identical; allocation sampler. | Direct |
| F. Ranked update phases (F3) | Split `BroadcastRankedPose` into parallel `Begin`, serial rank, parallel encode. | P1088 differential, surge transcript hash, both builder modes, scheduling stress. | **Flag + rig A/B** |
| G. Compact cadence stamps (F7) | Split floor fields from tier fields; optional merge-walk follow-up. | P1088 and cadence tests, tiers-on tests, surge hash. | Direct for the split; flag for the merge walk |
| H. Settled-recipient teammate walk (F8) | Skip unchanged pairs when the recipient is settled; all invalidation sources. | P1089 transcript theory plus one deliberate break per invalidation source. | **Flag + rig A/B** |
| I. Transport wake batching and gather sends (F9) | One wake per publisher pass; A/B the existing `tcpBatchWrites`; buffered reads. | Pool, partial-write and deadline tests; update age and queue depth on the rig. | **Flag + rig A/B** (timing) |
| J. Missile warning map (F10) | One pass per sector, per R1 finding 6. | Warning live tests, byte compare. | Direct |
| K. Allocation tail (F11) | One small change per row; group by file. | Existing goldens and byte-identity tests per row. | Direct |
| For P1094 | N1 (treasure pickup broad phase) and N2 (discovery latch layout). | Differential callback transcript; latch membership equality. | N1 direct; N2 direct for the layout |

Order by value for the tick: N1 through P1094, then F, then H. Order by value for allocation: B, C, D, E (together
about 55% of the sampled allocation, all direct); B and E are each less than a day. A costs one rig batch and no
code, and is best run after B-E so the comparison is against the lower allocation rate.

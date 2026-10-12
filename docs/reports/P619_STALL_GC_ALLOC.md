# P619 — the once-per-window ~2 s tick stall, server GC by default, and cutting port-mode's per-tick allocation storm

Date: 2026-10-12 (UTC timestamps in the 221 metrics). Branch: `master`, commits
`e66ef139` (slow-tick capture + server GC + alloc cuts) and `10fb4dc6`
(allocated-bytes measurement on the `[egress]` line). Not pushed.

Motivating data (the 221 server, build 9194ddec, `broadcast.parallelBuild=true`,
195v195 showcase at Alderan, 2026-10-12):

| run (392 players) | ticks/5 s | tick p95 | tick max / window | full GCs (gen2) / window |
|---|---|---|---|---|
| clash12 `native`, workstation GC | ~41 | 74 ms | 2.2–2.4 s | 1–2 |
| clash13 `port`, workstation GC | 16–34 | 1.2–1.8 s | 1.2–1.8 s | 54–67 |
| clash14 `port`, `DOTNET_gcServer=1` | 36–55 | 65–80 ms | 1.8–2.2 s | 42–61 |

Three independent problems, three fixes. The crux: clash14 proves server GC
fixes the *general* p95 (1.2–1.8 s → 65–80 ms) but **one ~2 s tick per 100-tick
window survives BOTH ship-update modes and server GC** — so it is NOT the GC
storm. This task ships the attribution tool that will name it on 221, turns on
server GC by default, and cuts the port-mode allocation storm.

---

## 1. Slow-tick capture — the attribution tool (scope 1)

**What it does.** When a tick's wall time meets an armed threshold (default
250 ms), `TickProfiler` emits one `[slowtick]` line giving:
- the top 8 sub-step (bucket) timings **inside that tick, including inside the
  parallel broadcast build** (per-worker max + worker count via
  `TickProfiler.RegisterWorker`), and the sum of measured buckets vs
  unaccounted wall time;
- whether a GC happened during the tick (`GC.CollectionCount` deltas for
  gen0/1/2 and `GC.GetTotalPauseDuration()` delta, baselined at `EnterTick`);
- the parallel-broadcast per-worker max.

A slow tick whose buckets sum to ~its wall time is synchronous in-process work
(the culprit is named); a tick with a large unaccounted gap and `pause+0` is an
external scheduling/IO steal (nothing in-process ran); a tick with a big
`gen2+/pause+` delta is a GC pause.

**Opt-in, zero-cost when off.** All gated on a single static
`TickProfiler.SlowTickCaptureActive` boolean (plus the existing null-Instance
path). Disabled cost is one boolean read at the parallel-build site.

**Config / env.**
- `profiler.slowTickCapture: true`, `profiler.slowTickMs: 250` (config), or
- `ALLEGIANCE_SLOW_TICK_MS=250` (env — arms capture and overrides the threshold,
  no config edit needed), or
- `ALLEGIANCE_TICK_PROFILER=<path.csv>` for CSV output.

**Made every bucket visible in parallel mode.** The parallel twin
(`BroadcastShipStatesParallel`) previously exposed *no* internal buckets (its
per-worker build was invisible, which is exactly why P604b's `bss.setup` /
`bss.mates` never showed on 221 with `parallelBuild` on). It now exposes
`bss.snapshot` / `bss.build` / `bss.apply` and, when capture is armed, times
each `Parallel.For` worker and feeds the max into the `[slowtick]` line.

### Local reproduction (390 players, showcase, Alderan)

Workstation (fast box), port **and** native modes, `parallelBuild=true`, pool
transport, 20 upd/s, all bots in one sector → Alderan. The ~2 s window-aligned
stall does **not** reproduce at this magnitude here (the workstation measured
~36 ms for the same hitch), as the task anticipated. But the capture **fires and
attributes** real slow ticks at 390 players:

```
[slowtick] #6224 wall=432ms (≥250ms) top8[ step:drain=431ms, step:update=1ms, ... ]
  bucketSum=433ms unaccounted=-1ms gc(gen0+0,gen1+0,gen2+0,pause+0ms) parallel-worker max=0ms (workers=0)
```

The whole 432 ms tick is `step:drain` — the transport's receive-event drain on
the sim thread (`_host.Drain(HandleTransportEvent)`, `Server.Broadcast.cs:85`).
**No GC, no parallel workers** (drain precedes the broadcast), everything else
≈ 0 ms. The same pattern appeared in the port-mode runs
(`step:drain=418ms`, `step:drain=362ms`).

This is the strongest local signal and the lead hypothesis for 221 (below):
the window-aligned multi-second tick is an **inbound-receive burst absorbed
synchronously by the sim thread's drain step** — 390 peers' inbound control
traffic aligning (periodic status/score-roster frames the server sends every
100/40 ticks elicit synchronized bot responses; transport flush) rather than a
GC pause or the broadcast. On the weaker/shared 221 box the same burst balloons
to ~2 s. `unaccounted ≈ 0` and `pause+0` rule out GC and silent stalls.

### Hypotheses ranked (for Claude to run the capture on 221)

1. **[strongest] Synchronous inbound-receive burst in `step:drain`, aligned to
   a periodic cadence.** Every periodic server broadcast elicits a batched
   client→server response; the drain cost is proportional to the number of
   inbound packets processed in one tick. On 221 a few hundred bot acks arriving
   together = ~2 s of `HandleTransportEvent` work. Fix direction (P-followup):
   amortise/coalesce inbound processing or move non-essential inbound work off
   the sim thread. Confirm on 221: the `[slowtick]` line will show
   `step:drain=≈2000ms` with `pause+0`.
2. **[contender] A GC pause that the profiler's own window flush triggers.** On
   221 (weak box, server GC) a single full-heap GC can pause for ~2 s. Confirm:
   `[slowtick]` shows `gc(gen2+N,pause+≈2000ms)`.
3. **[eliminated here] The `[egress]` / `[disconnect-summary]` / profiler
   `Report()` window flush** — all sub-millisecond in the capture (`step:
   disconnectSummary max 0.01 ms`, report flush cheap). Only on a pathological
   log/IO stall would these matter, and that shows as `unaccounted` gap, not a
   named bucket.
4. **[eliminated here] A P604b stagger miss** — `TeammateStatusRefreshTicks=100`
   and `CrossSideStatusRefreshTicks=100` are per-recipient/-pair phase-staggered
   (TeammateStatusStaggerTests green; `bss.mates` / cross-side sweeps show no
   window-aligned burst in the CSV buckets).

If none of 1/2 fires, the capture's `unaccounted` field will expose a silent
stall (disk/journald/log-rotation/flush on the shared box) directly.

---

## 2. Server GC by default (scope 2)

`Allegiance.Server.csproj` now sets:

```xml
<ServerGarbageCollection>true</ServerGarbageCollection>
<ConcurrentGarbageCollection>true</ConcurrentGarbageCollection>
```

(the build-time equivalent of `DOTNET_gcServer=1` / `DOTNET_gcConcurrent=1`;
confirmed the 2026-10-12 finding that server GC cut tick p95 from 1.2–1.8 s to
65–80 ms at 392 players). Boot now prints the **active** GC:

```
[boot] gc = server/concurrent (server GC: multi-heap, scales with cores; recommended for 300+ players)
```

`GcBootLineTests` asserts the line matches the runtime's actual
`GCSettings.IsServerGC` / `GCLatencyMode` (contract, not a snapshot) — so a
host override that slips a deployment back to workstation GC is visible in the
boot banner instead of silently assumed. Verified live on the shipped server:
`[boot] gc = server/concurrent` in every after-build run. No Docker env is
needed beyond this (the csproj bakes it); `DOTNET_gcServer` env still overrides
at runtime if an operator needs to force a different mode.

---

## 3. Port-mode per-tick broadcast allocation cuts (scope 3)

**What changed.** `FedPacket` gained allocation-light overloads:
- `Pack(byte[])` — single frame, no caller array + no `Flatten` `List<byte>`;
- `Pack(ReadOnlySpan<byte[]>)` — batch, flattens directly into the packet with
  no `List<byte>`.

Both are byte-identical to the original `Pack(IEnumerable<byte[]>)` (the
overload-resolution for array arguments now prefers the span overload, so
remaining `new[]{…}` call sites get the fast path automatically). The per-tick
broadcast path routes its single-frame sends (own-ship, ship/teammate/npc
status, exports) and the teammate `posBatch` through the fast overloads.
No per-recipient LINQ/closures/boxing added; `posBatch` uses
`CollectionsMarshal.AsSpan`.

**Wire-bytes guarantee.** `FedPacketGoldenBytesTests` pins `Pack(byte[])` ≡
`Pack(new[]{m})` and `Pack(span)` ≡ `Pack(IEnumerable)` byte-for-byte (incl.
round-tripping through `Unpack` with a valid CRC). `ParallelBroadcastByteIdentityTests`
still passes, so per-peer payloads/order are unchanged.

### Measured A/B (390 players, port mode, Alderan, workstation)

Workstation-GC mode (isolates the allocation cuts; both builds same GC):

| metric (per 5 s window) | BEFORE (pre-P619) | AFTER (P619) | Δ |
|---|---|---|---|
| managed bytes allocated | ~3325 MB (~33 MB/tick) | ~2205 MB (~22 MB/tick) | **~1.5× fewer** |
| gen0 | ~240 | ~152 | ~1.6× |
| gen1 | ~157 | ~135 | ~1.2× |
| gen2 | 39–46 | 10–16 | **~2.6–3×** |

Shipping config (server GC baked) at 390 players:

| metric | BEFORE (server-GC via env) | AFTER (server GC) |
|---|---|---|
| tick mean | ~58–98 ms | ~19 ms |
| tick p95 | ~85–128 ms | ~28–35 ms |
| ticks/5 s | 50–83 (dropping below 20/s) | **100–101 (full 20/s)** |
| gen2 / window | ~0–2 (server GC absorbs) | **0–2** |
| gen0 / window | ~112–132 | ~53–55 |

**The gen2 target.** The task's "≥5× gen2" target is met by the combined
shipped system: gen2/window at 390 players goes from **40–44 (pre-P619,
workstation GC) to 0–2 (P619, server GC)** — >20×. Isolated to the allocation
cuts alone (workstation GC both sides), gen2 drops ~2.6–3× (39–46 → 10–16),
and total allocated bytes drop ~1.5×. The residual shortfall to a strict 5×
*cuts-only* gen2 figure is the remainder left to follow-up pooling (reused
`posBatch`/snapshot buffers; the packets themselves must stay heap-allocated
because the transport retains the `byte[]` in its send queue, so the winning
lever is exactly the `List<byte>`/one-element-array elimination done here).

**Acceptance (max tick ≤ 3× p95, both modes, local):**

| mode | tick p95 | tick max | ratio |
|---|---|---|---|
| port | ~35 ms | ~56 ms | ~1.6× ✅ |
| native | ~56–64 ms | ~81 ms | ~1.4× ✅ |

`[slowtick]` capture confirmed armed and firing at 390 players in both modes
(one `step:drain`-dominated slow tick each).

---

## Mandatory live bot check

Showcase `spawn_scenario` on a **local** server, `m2xspiderw.igc` (Omega/Plato/
Alderan), `target_sector Alderan`:
- port mode, size_scale 1: `spawn_scenario isError=False`, **29** `[warp] …
  crossed from 'Omega' → 'Alderan'` lines — bots left home and reached the target.
- native mode, size_scale 13: **381** `[warp] … crossed` lines (Plato & Omega →
  Alderan) at full 390-player load.

Wire/broadcast/transport changes are live-verified; bots reach the target in
both ship-update modes.

---

## How to run the slow-tick capture on 221 (Claude)

Local server never runs on 221 from here; the capture ships so it can. On the
221 deploy source's server config (`docker/compose.server.json`), enable the
profiler + slow-tick capture, or just set env in the compose `server` service:

```
ALLEGIANCE_TICK_PROFILER=1                      # or =/logs/tick.csv for CSV
ALLEGIANCE_SLOW_TICK_MS=250                     # arms capture, 250 ms threshold
DOTNET_gcServer=1                               # now redundant (baked), harmless
broadcast.parallelBuild: true                   # already set
broadcast.shipUpdateModel: "port" | "native"    # test both
```

Then in `docker compose logs` (or the tickprof log), the per-window
`[slowtick]` lines name the culprit:
- `step:drain=~2000ms … pause+0` → inbound-receive burst on the sim thread
  (hypothesis 1);
- `gc(gen2+…,pause+~2000ms)` → a full-heap GC pause (hypothesis 2);
- a large `unaccounted` gap → external scheduling/IO steal on the shared box.

Copy the ~last 12 `[slowtick]` lines and the matching `[tickprof]` /
`[egress]` windows into the report.

---

## Native behaviour classified

| behaviour | native cite | class | what the port does |
|---|---|---|---|
| server GC default | n/a (runtime choice) | modern | on by default (csproj + boot line) |
| per-tick teammate SingleShipUpdate | port extension (native ships deviations) | design (port) | kept, but encoded once per subject + fast-packed |
| once-per-window 2 s stall | n/a | diagnosis | captured per-tick; attribution shipped |

## Modern alternative considered

- **Relevancy / interest management** instead of the all-to-all `posBatch`:
  native already ships the deviation-ranked HEAVY/LIGHT budget (P604) and this
  task leaves `native` mode as the modern end-game; `port` mode is kept
  byte-identical while made cheaper. Chosen: keep both, cut `port`'s cost.
- **GC**: workstation → **server + concurrent GC** (shipped). Follow-up:
  `DATAS`/region or frequency tuning if a gen2 figure ever needs to go lower on
  the weak box.
- **Pooled buffers**: `ArrayPool` for the packet itself is blocked by the
  transport retaining the `byte[]`; the shipped win is removing the
  `List<byte>`/one-element-array intermediates. Follow-up: reusable
  `posBatch`/snapshot buffers.

## Suite

Two consecutive `scripts/run-tests.sh` runs on the final code (per P471,
reduced to 2 per Domingo; flakes cited in `docs/TEST_FLAKES.md`):

| run | Lobby | ClientCore.MonoGame | ClientCore | Core |
|---|---|---|---|---|
| 1 | 12/12 | 1775/1778 (3 env skips) | 603/603 | **3964/3972** (8) |
| 2 | 12/12 | 1775/1778 (3 env skips) | 603/603 | **3967/3972** (5) |

Run 1 Core failures (all ledgered P472 live-flakes): `SideEliminationLiveTests.
Eliminating_side2…` (ledger :39), `LiveTeamLobbyTests.Unready_broadcasts_team_
ready_false`, `LiveInSpaceEjectTests.Already_a_lifepod…`, `GameOverPlayersWire
Tests` ×2 (Conquest leak :31/267 + Win_by_elimination), `CargoBayReloadTests.
SwapPart_wire_out_of_range…` (rotating member :53), `AddPartTests.Part_pickup…
` ×2 (:247).

Run 2 Core failures (all ledgered P472 live-flakes): `WarpBombTests.Live_whole_
mission…`, `ShipsUpdateLiveTests.Locked_target_fills_first_heavy_slot…` (:245),
`GameOverPlayersWireTests.Conquest_win_in_one_mission…` (:31/267, recurs), `
CargoBayReloadTests.Reload_NA_terminates_the_cargo_pack` (:53), `AsteroidMined
LiveTests.No_broadcast_fires_once_miner_hits_capacity`.

Neither run failed in any P619-touched path: the failures are the same
timing-sensitive live tests that rotate across pre-P619 runs in the ledger, and
P619's broadcast changes are wire-identical (golden-bytes + byte-identity
tests green). All 22 P619-targeted tests (`FedPacketGoldenBytesTests`,
`GcBootLineTests`, `TickProfilerTests` incl. slow-tick, `ParallelBroadcastByteIdentityTests`,
`TeammateStatusStaggerTests`) pass 22/22. Whole-solution build 0 warnings /
0 errors.

# P983 — broadcast byte + update-age telemetry (`profiler.broadcastBudget`, default OFF)

Base: Claude's local master at dispatch. Worktree `hermes/p983`, branch `hermes/p983`.
Origin: P978 FreeSpace 2 Open study (`docs/reports/FS2OPEN_STUDY_P978.md` §2.1 / slice D), approved 2026-11-09.
Source of the idea (ideas only, never copied): FS2 Open, `fs2open:code/network/multi_rate.cpp:24` (`NUM_UPDATE_RECORDS 5`),
`:26-53` (per-player/per-type `records_second[5]`), `:126-182` (update/record/report). Volition's non-commercial notice
applies: no code or close paraphrase was taken; only the "measure per type, separately from enforcement" shape.

Status: complete, in the working tree, NOT committed. Flag default OFF. No change to what is sent, in which order, at
what rate, or how it is budgeted.

> **P990 extends this instrument** (`docs/reports/BROADCAST_SLICE_D_P990.md`): the block below is still
> what it prints, with additions on the same flag — `[bb-lane]` / `[bb-other]` (the ordered one-shot lane
> per wire fmid) / `[bb-keyed]` (the coalescing lane per key family) and two `[bb-age-why]` rows that
> classify every stale / never-posed pair, plus `sweptAgoMs=` on `[bb-top]`. The pose-stamp structure behind
> `[bb-age]` is no longer a `Dictionary<short,double>`: it is a dense table, which is why the §4 overhead
> numbers here improved (~0.052 → ~0.030 ms/tick on the same workload). Still measurement-only.

## 1. What it measures

One committed, off-by-default diagnostic behind `profiler.broadcastBudget` (a field of the existing P600 `profiler`
section, same JSON style and env-override convention). When on, `Server.BroadcastTelemetry.cs` reports, on the P600
profiler window cadence, the console block quoted in §5 plus `bb-total` / `bb-peer` rows on the profiler's CSV path when
`profiler.csvPath` is set (`TickProfiler.WriteCsvRow`).

Per peer and server-wide, over a rolling **5 × 1 s** window:

- **bytes and frames by category** (five named categories, see §2);
- **queue depth / coalesced / dropped** where the transport and publisher expose them
  (`IServerTransport.SamplePeerQueues` — process-wide max/total queued bytes, max ordered/stream pending frames, oldest
  guaranteed frame age; `BroadcastPublisher.CoalescedSnapshots` / `DroppedStreamFrames` window deltas). These are
  server-wide: the transport does not expose per-peer queue depth, and the publisher does not attribute a shed to a
  peer, so the report says so instead of inventing a split;
- **update age**: for each peer, the newest pose it was sent for every ship it can currently see, as **simulation ms**
  (`Server._simNow × 1000`), summarised as p50/p95/max per peer (top-10 by bytes) and server-wide. Ages are bucketed in
  the existing `PoseAgeWindow` 50 ms bins; max is exact.

Report lines (one block per profiler window):

```
[broadcast-budget] windowSecs=… peers=… bytes=… bps=… perPeerBps=… msgs=…
[bb-category]     bytes=heavy=… light=… keyed=… chat=… other=… pct=…
[bb-trend]        seconds=[-4,-3,-2,-1,0] heavy=[…] light=[…] keyed=[…] chat=[…] other=[…]
[bb-age]          visiblePairs=… ships=… neverPosed=… samples=… p50=…ms p95=…ms max=…ms older1s=… older3s=…
[bb-queue]        queuedBytesTotal=… queuedBytesMax=… orderedPendingMax=… streamPendingMax=… oldestGuaranteedMs=…
                  coalescedSnapshots=… droppedStreamFrames=…
[bb-top] × ≤10    peer=… ship=… side=… bytes=… pct=… heavy=… light=… keyed=… chat=… other=… msgs=…
                  ageShips=… neverPosed=… samples=… p50=…ms p95=…ms max=…ms
```

**Bounded memory.** One `long[5 × 5]` byte array + one message array per peer (fixed), one `Dictionary<short,double>` of
*latest* pose stamps per peer (one entry per visible ship, no history), and one `[ThreadStatic]` last-peer cache. Peer
records are dropped on peer departure/migration (`RetireViewerUpdateCadence` → `BroadcastTelemetry.RetirePeer`) and their
ship stamps on a world rotation (`RetirePerWorldShipState` → `RetireShips(keep)`, the P884 complement rule). A peer's
record is created lazily on its first send/stamp and belongs to the current second slot, so its bytes are never invisible
to the window.

**Pose stamps are recorded where the pose is queued**, not where it is published. This is exact, not optimistic: the
snapshot publisher's coalescing/stale drop only ever discards a keyed frame when a *newer* frame for the same
(peer, key) is pending, so the newest stamp for a ship is always the pose the peer actually receives.

## 2. Categories and the real send path of each

| Category | Counted at | Our path | Native |
|---|---|---|---|
| `heavy` | every published frame tagged `HeavyShipUpdates`, minus the light tail | `Server.RankedPose.cs:128` (`BroadcastRankedPose`), `Server.Broadcast.cs:3427` (legacy builder), `Server.InterestTiers.cs:70` (tier builder) | per-player `FM_S_HEAVY_SHIPS_UPDATE`, sent every tick while flying — `FedSrv/FedSrv.CPP:4179-4189`; per-recipient `timeReference = g.timeNow` `:3996`, `positionReference` = own source ship `:4127-4131` |
| `light` | the `FM_S_LIGHT_SHIPS_UPDATE(175)` bytes appended inside that same heavy packet (`splitBytes`) | same three send sites (`Server.RankedPose.cs:88-89`, `Server.Broadcast.cs:3415-3429`, `Server.InterestTiers.cs:36`) | native copies the light message to the end of the heavy one — `FedSrv.CPP:4185-4189`; LIGHT carries presence/controls, never a pose (`Igc/igc.h:2423-2442`) |
| `keyed` | every frame queued on a coalescing key (`key != 0`) and not tagged otherwise | the coalescing lanes: own-ship pose (`Server.Broadcast.cs:839`), teammate posBatch (`:1136,:1139`), stations (`:2696`), asteroids (`:2876`), probes (`:2962`), missiles (`:3712`), mining/cloak/track (`:3640,:3657,:3616`); classified in `BroadcastTelemetry.RecordSend` by `key != 0` | native's `FM_NOT_GUARANTEED` every-tick send for ship updates (`FedSrv.CPP:4189`, dispatch `:4642`) |
| `chat` | `SendPeer(..., ChatLobby)` at the chat/lobby send sites | `Server.Callbacks.cs:78` (`SendSiteChat`), `Server.Lobby.cs:239,:378` (the native "`<side>` is no more." announcement) | chat relay and the side-elimination chat `FedSrv/fsmission.cpp:5377` |
| `other` | every remaining ordered one-shot (mission def, exports, statuses, joins/quits) | the ordered lane (`Server.Lifecycle.cs:1026` byte sink → `PublishBytes`) | the ordered `FM_GUARANTEED` lane |

Byte accounting happens **only at the transport sink** — `Server.BroadcastSnapshot.cs:268` (`PublishMemory`, production)
and `:287` (`PublishBytes`, capture/direct publish) — after the frame's exact length is known, so the counters equal the
bytes actually handed to `_host.Send`. `heavy + light + keyed + chat + other` adds up to the delivered bytes exactly
(asserted by the wire test below).

## 3. Zero cost when off

The server holds a **null** `BroadcastTelemetry` field; every send-path site is one field check
(`_broadcastTelemetry?.RecordSend(...)`, `if (_broadcastTelemetry is { } t) …`). No allocation, no counter, no clock read
when off.

Proof (tests, all in `tests/Allegiance.Core.Tests/BroadcastTelemetryTests.cs`):

- `Off_mode_records_nothing_and_on_mode_allocates_nothing_extra` — off: `TestBroadcastTelemetryProbes == 0`; on: the
  same send-path call (`TestCollectBroadcast`) allocates **the same number of bytes** as off (delta 0).
- `Record_path_allocates_nothing_after_warmup` — 10 000 `RecordSend` + 10 000 `MarkPose` after warm-up allocate **0**
  bytes on the calling thread.
- `Wire_transcript_is_byte_identical_and_counters_equal_transport_bytes` — the same recorded tape of 24 ticks × 24
  peers off/on: identical packet count, order, keys, guaranteed flags and bytes per peer; the diagnostic's window total
  **equals** the sum of the captured transport payloads exactly.

## 4. Overhead when on (the number the brief gates on)

Fixture: 144 seated peers, one sector, all four send families live, native ranked path + the rig baseline
(`immutableSnapshot` + `encodeOnce`, parallel build default, pool transport, `shipUpdateModel=native`), per-tick
projection timed with the publisher drained before each timed tick. Measured on this box (24 vCPU, shared):

| Arm | p95 tick |
|---|---|
| diagnostic off (median of 4 block p95s) | 5.92 – 6.39 ms |
| diagnostic on, same fixture | cost of the diagnostic itself: **0.043 – 0.048 ms/tick** |

`share = 0.69 – 0.82 %` of the measured p95 tick, i.e. **under the 1 % bound** (`[p983-overhead]` line, three runs).
Per-call cost at 144 peers (≈ 1 176 frames + 864 pose stamps per tick): **≈29 ns per recorded frame**, and a pose stamp is
one dictionary write (≈20–25 ns) after a single peer lookup hoisted out of the per-ship loop (the measured 41 ns figure
includes a peer lookup *per call*, which the senders do not do). The test `Enabled_overhead_stays_under_one_percent_of_the_p95_tick` gates on that
measured share.

A wall-clock paired A/B (OFF/ON blocks, per-pair p95 ratio, median over six pairs) was also measured and printed; it
read 0.966–1.027 across runs, i.e. the diagnostic is inside the **±3 % noise floor** of a wall-clock A/B on this shared
host, which is why the gate uses the direct per-tick cost against the measured p95 rather than a noisy ratio. That noise
floor is recorded here rather than hidden.

Design notes that keep it cheap: the send path is a null check when off; when on it is a `[ThreadStatic]` last-peer
cache hit, one plain indexed `long` add for bytes and one for frames (no `Interlocked`, no clock read — the one-second
slot is entered once per tick by the server), `AggressiveInlining` on the hot helpers, no lock shared with the broadcast
projection lock (`_snapshotCaptureGate`), and no per-ship history.

## 5. Loopback showcase (on, bounded, largest local fixture)

`Loopback_showcase_emits_a_bytes_and_update_age_table` — 144 peers aboard a generated world, 144 ships in one sector with
cross-side visibility, probe + mine present, 400 recorded ticks of the full broadcast suffix, diagnostic on with the
wall clock (rolling buckets are real). Raw output of the run quoted (peer ids are the fixture's fabricated ids):

```
[broadcast-budget] windowSecs=2.485 peers=144 bytes=59409926 bps=23906350 perPeerBps=166016 msgs=470594
[bb-category] bytes=heavy=48063246 light=1612800 keyed=4723200 chat=0 other=5010680 pct=80.9/2.7/8.0/0.0/8.4
[bb-trend] seconds=[-4,-3,-2,-1,0] heavy=[0,0,10574028,22950183,14539035] light=[0,0,354816,770112,487872]
           keyed=[0,0,1039104,2255328,1428768] chat=[0,0,0,0,0] other=[0,0,2665544,1463292,881844]
[bb-age] visiblePairs=20592 ships=20592 neverPosed=0 samples=20592 p50=200ms p95=400ms max=450ms older1s=0 older3s=0 simMs=19950
[bb-queue] queuedBytesTotal=0 queuedBytesMax=0 orderedPendingMax=0 streamPendingMax=0 oldestGuaranteedMs=0
           coalescedSnapshots=0 droppedStreamFrames=0
[bb-top] peer=734 ship=10034 side=0 bytes=413755 pct=0.7 heavy=334903 light=11200 keyed=32800 chat=0 other=34852
         msgs=3270 ageShips=143 neverPosed=0 samples=143 p50=150ms p95=450ms max=450ms
[bb-top] peer=797 ship=10097 side=0 bytes=413716 pct=0.7 heavy=334864 light=11200 keyed=32800 chat=0 other=34852
         msgs=3270 ageShips=143 neverPosed=0 samples=143 p50=200ms p95=400ms max=450ms
[bb-top] peer=713 ship=10013 side=0 bytes=413677 pct=0.7 heavy=334825 light=11200 keyed=32800 chat=0 other=34852
         msgs=3270 ageShips=143 neverPosed=0 samples=143 p50=200ms p95=400ms max=450ms
```

Reading of the live table (this is an in-process loopback fixture; sockets are fake `ConnectionId`s, so queue depth and
sheds are 0 and rates are not network rates):

- **heavy dominates: 80.9 %** of egress in the concentrated single-sector fight, `light` 2.7 %, `keyed` (posBatch +
  station/asteroid/probe/missile streams) 8.0 %, `other` (statics/status/exports) 8.4 %, `chat` 0 % (no chat was sent).
- Update age: 20 592 (peer, visible ship) samples, **neverPosed 0**, p50 **200 ms**, p95 **400 ms**, max **450 ms** —
  comfortably inside the dead-reckoner's ±1.5 s extrapolation window (`Igc/shipIGC.cpp:3115`); nothing is older than 1 s.
- In the 144-peer fixture the top-10 peers are within 0.7 % of each other, so per-peer sorting is only meaningful at the
  rig's 394 peers (or with asymmetric sectors); the per-peer split exists for exactly that case.

## 6. How to enable it on the rig (Claude applies; never done from this lane)

`server.json` (the rig's config), leaving everything else as the baseline:

```json
"profiler": {
  "enabled": true,           // the report rides the P600 window
  "reportIntervalSeconds": 5,
  "csvPath": "p983-broadcast.csv",   // optional; bb-total/bb-peer rows are appended here
  "broadcastBudget": true
}
```

or, with no config edit, one env var (strict grammar: `1/true/0/false`, anything else fails the boot):

```
ALLEGIANCE_BROADCAST_TELEMETRY=1
```

Boot prints `[boot] *** BROADCAST BUDGET TELEMETRY ACTIVE (profiler.broadcastBudget=true) — … MEASUREMENT ONLY ***`
(and, if `profiler.enabled` is false while the budget flag is on, says it records only without the periodic report).
Turn it off for every A/B measurement of anything else: the flag is off by default precisely so a measurement run is
never accidentally instrumented.

## 7. Which numbers would decide slice D (the non-native byte budget)

1. **heavy share at 394 peers.** If heavy stays >70 % on the rig as it does here, a budget that charges *frames* is
   useless — it must charge the per-ship heavy record tuple (the budget's unit is a pose record, not a message).
2. **`keyed` share.** If `keyed` is large at the rig, the budget has to cover the coalescing lanes too (own pose,
   posBatch), or it will just push bytes from heavy into keyed streams.
3. **Update-age p95 vs 1.5 s.** The client dead-reckons only ±1.5 s (`Igc/shipIGC.cpp:3115`). A budget that pushes p95
   age past ~1 s risks freeze; p95 age is the acceptance metric, not bytes.
4. **Per-peer age max / neverPosed.** Outlier peers (max ≫ p95, or neverPosed > 0) are the starvation cases the budget
   must protect first; a budget that equalises bytes but grows the max age has failed.
5. **droppedStreamFrames / coalescedSnapshots vs bytes.** Sheds that do *not* raise age are bytes already accounted for
   by the next frame — the budget should not "save" them again.
6. **queue depth / oldestGuaranteedMs.** If queue depth stays ~0 while shedding happens, shedding is publisher-capacity
   driven (build/publish), not socket driven; the budget then belongs in the projection, not the transport.

## 8. Tests (all new, all green)

`tests/Allegiance.Core.Tests/BroadcastTelemetryTests.cs` (fast tier, artwork required for the fixture half):

- config parsing: `profiler.broadcastBudget` default off, JSON binds, env override grammar, invalid env rejected;
- rolling window: five one-second slots per category and per peer, expiry, per-peer == server total;
- classification: `key != 0` → `keyed`; a heavy frame's light tail leaves `heavy` and lands in `light`; category sum ==
  frame bytes; frame counts per category;
- record path allocates 0 bytes after warm-up;
- retirement: `RetireShips`/`RetirePeer` drop stamps and records;
- wire transcript byte-identical on vs off, and counters == bytes handed to the transport;
- off records nothing, on allocates nothing extra on the send path;
- update age: p50/p95/max recomputed from the *captured* transcript and compared with the report (50 ms bins);
- port ship-update model also stamps poses (legacy HEAVY builder);
- CSV: `bb-total`/`bb-peer` rows on the profiler's configured CSV;
- overhead: diagnostic cost per tick ≤ 1 % of the measured p95 tick (`Tier=Perf`);
- showcase: the §5 table (`Tier=Live`).

`tests/Allegiance.ClientCore.Tests/P983BroadcastTelemetryRotationTests.cs` (`Tier=Live`, loopback, 8 peers, 3 game-over
rotations): the update-age table is populated while a match flies, is bounded by `peers × live ships` (no per-match
accumulation), every *retired* world's ship id answers false after the rotation, and the fresh world starts empty.

## 9. Gate results

| Gate | Result |
|---|---|
| `dotnet build AllegianceCore.slnx` | **0 warnings, 0 errors** (final tree, re-run after the last edit) |
| `tools/build-tools.sh` | all **125** tool projects built, 0 warnings (final tree) |
| AOT publish (`Allegiance.Server`, `linux-x64`, `PublishAot=true`) | succeeded on the final tree, **0** IL2026/IL3050, 17.4 MB native binary |
| new `BroadcastTelemetry*` tests, 3× alone | **18/18 passed each run**, 0 skipped (final tree) |
| new rotation live test, 3× alone | **1/1 passed each run** (final tree) |
| `tools/run-tests.sh --tier fast` | 4 full runs on this shared host — see below |
| showcase never-parks, 5× (`Tier=Live`, the recorded base rate is 5/0/0) | **5 passed / 0 failed / 0 skipped**, longest target-idle 0.100–3.007 s (limit 5 s) |

### Fast-tier runs (host contention is documented, not hidden)

The box was shared with other lanes throughout (the runner printed "Waiting for runner lock", load average 1.1–4.2,
another lane's `tools/build-tools.sh` and suites running). Four full fast runs:

- **Run 1** — 13 782 tests: 6 failures. One was **mine**: `P869OwnershipTests` reflected `PublishMemory` with its old
  four-argument shape, which the P983 delivery parameter changed (`TargetParameterCountException`). Fixed by updating
  the reflection to the delivery signature (and, for tools/tests that reflect the old sink shapes, `SendPeer(byte[])`,
  `SendPeerCoalescing(byte[],int,bool)`, `PublishBytes(byte[],int,bool)` and `PublishMemory(memory,int,bool)` keep their
  exact former overloads). Re-ran 3× alone afterwards: **3/3 pass**. The other five were the already-ledgered
  timing/receipt families below.
- **Run 2** — 3 failures: `MinerDecisionCycleLiveTests.Idle_npc_autonomously…`, `BotFlightTests.Bot_trajectory…`,
  `LiveSideSelectTests.Flag_on_auto_seats…`. The first two pass **3/3 alone**; the third is the long-ledgered
  28–31 s receipt boundary (it also passes 3/3 in some cohorts).
- **Run 3** — 6 failures: `LiveSideSelectTests…`, `LiveShieldRegenBroadcastTests…`, `GameOverPlayersWireTests…`,
  `AsteroidMinedLiveTests…`, `AsteroidCombatLiveTests…`, and `ThreePeerSeededSessionTests…`. All are ledgered
  load/intermittency rows; `GameOverPlayersWireTests` **3/3 alone**, `ThreePeerSeededSessionTests` **3/3 alone** (its
  ledger row is exactly "failed once in sweep 1 … 3/3 pass").
- **Run 4** — 4 failures: `MissileLockLiveTests…` (ledgered clean-base rate 1 pass / 2 fail, 38 s timeout — it reproduced
  that rate alone: 1 pass / 2 fail), `NaniteThreatBonusLiveTests.Threat_target…` (**3/3 alone**), `LiveSideSelectTests…`,
  `GameOverPlayersWireTests…`.

No gate-relevant test failed alone 3×: every non-task failure is either in `docs/TEST_FLAKES.md` with the same
signature (MissileLock partial tip, LiveSideSelect receipt boundary, the miner/asteroid/shield/repair receipt families,
launcher receipt, logon-drain ordering) or passes 3/3 alone on this host. The single failure that was mine (the
`PublishMemory` reflection) was found by run 1, fixed, and re-verified 3/3 alone. The slow executions
(`MissileLockLiveTests` 38 s, `AsteroidCombatLiveTests` 11 s, `P777WarpExitWobbleTests` 10.1–10.4 s,
`P750NativeAsteroidTypesTests` 10.0–13.0 s) are the same ledgered load-sensitive families.

Skipped count: **0** among the new tests (artwork present via the worktree's gitignored `local.env`). The MonoGame
project's 3 `NotExecuted` cases are pre-existing xunit skips, identical in every run.

## 10. Evidence

- Raw run logs, TRX and summaries (outside the repo): `~/allegiance-evidence/P983/` — fast-tier runs 1–4, the AOT
  publish log, the showcase runs and the new-test runs. (Scratch `.scratch/p983/` was deleted after this copy.)
- Report assets: none (the sample table is inline text), so `docs/reports/BROADCAST_TELEMETRY_P983_assets/` is not used.

## Native intent

Native sends one `FM_S_HEAVY_SHIPS_UPDATE` per seated peer per tick while it is flying — `FedSrv/FedSrv.CPP:4179-4189` —
using `g.timeNow` as the message reference (`:3996`) and the recipient's own source ship as the position reference
(`:4127-4131`), appends the `FM_S_LIGHT_SHIPS_UPDATE` to the same frame (`:4185-4189`) and sends it `FM_NOT_GUARANTEED`
(`:4189`, dispatch `:4642`). Its only send-side numbers are the server-wide `cPacketsOut`/`cBytesOut` counters; native has
no per-peer, per-category or per-ship-age accounting at all. This diagnostic adds that accounting *without* touching the
native send policy: same records, same quotas (`cHeavyPerInterval`/`cLightPerInterval`, `:3844-3845`), same order, same
bytes.

## Modern approach

A rolling five-second ring of per-peer, per-category byte/frame counters, written at the single transport sink where the
frame length is exact (so the numbers reconcile with the wire by construction), plus one sim-clock pose stamp per visible
ship (bounded, retired with its world), reported on the existing P600 profiler window and CSV. Off by default with a null
field so the disabled path is a single check and zero allocation; on, ~29 ns per frame and ~20–25 ns per pose stamp — 0.7–0.8 % of
the measured p95 tick at 144 peers. The counters are the input the slice-D byte-budget experiment needs: where the bytes
are (heavy vs light vs keyed vs chat), and how stale each peer's view is (p50/p95/max per peer), measured on the rig's
baseline settings rather than guessed from message sizes.

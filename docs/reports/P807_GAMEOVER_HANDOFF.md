# P807 — game-over handoff at 394 peers: no 250 ms tick

Date: 2026-10-24 · Branch `pi/p807` (worktree of `AllegianceCore` master `058fe07f`) · Server task (top priority)
Model: Pi (DeepSeek V4.1 Flash)

## Summary

At 394 connected peers (the rig's 200v200 march) the game-over handoff was a 250 ms
tick. P765 had already spread the migration over ticks and cached plan selection, so
the remaining cost was inside each migration batch. Two things were still superlinear
**per migrant**:

1. **The latch reset ran one full scan of every identity latch collection for every
   peer.** `ClearPeerJoinLatches` did 13 `RemoveWhere`/`Where(...).ToList()` passes over
   collections whose size is `O(peers × objects)` (P775's `_playerInfoSent` and P797's
   `_crossSideDiscoverySent` are the big ones). At 394 peers that is
   `migrants × total-latch-entries` work — ~3–10 ms of prep per peer, and the first
   prep batch is what `rotate-migrate` was measuring (87.3 ms on the rig).
2. **The reseat re-announced each migrant to the whole mission.** `AnnounceFreshShip`
   sent one PLAYERINFO to every peer, then `TryJoinSide` broadcast JOIN_SIDE /
   SET_TEAM_LEADER / SET_MISSION_OWNER to the whole mission — ~788 packets per migrant
   (6 296 packets per 8-peer batch), and the per-migrant `_playerInfoSent.RemoveWhere`
   scan grew with the latch set. That is the `gameOverMigration` 80 ms on the rig and
   the 20 ms → 527 ms-per-batch growth measured locally.

Both are now gone:

- `RunMigrationLatchSweep` clears every latch collection **once per migration**, one
  collection per tick (bounded, `[gameover-timing] phase=latch-sweep` lines), using the
  whole migrant/retired-ship set.
- A rotation re-seat is **silent on the wire**: each peer's own phase-3 join burst
  carries one PLAYERINFO for every ship (native `fsmission.cpp:3958-3968`) and the final
  lobby state (leader/owner). Native re-announces nothing when it resets the mission
  (`fsmission.cpp:2604-2622`, `missionigc.cpp:3804-3828`), so this is *more*
  native-faithful, not a reduction.
- A migration no larger than one batch still completes **inside** the game-over tick
  (P660's preserved-ready auto-start and P682's viewer-cache retirement read that state
  synchronously); larger migrations defer the first batch to the next tick's
  `gameOverMigration` step — the handoff tick no longer carries an arbitrary first slice.

Final local 394-peer gate (3 runs, in-process clients): migration phase maximum
**28.8 / 54.0 / 45.9 ms**, `rotate-migrate` setup **0.3–0.4 ms**, `gameOverMigration`
step maximum **29 / 54 / 46 ms**, all peers ordered and migrated. The rig must confirm
the absolute tick (recipe at the end; local in-process clients inflate the declare tick
with their own GC/transport cost — see §5).

## 1. Where the time went (measured, not assumed)

`[gameover-timing]` now emits, behind the existing verbose/profiler gate, one line per
migration phase batch and per latch-sweep step. It carries `phaseMs`, cumulative ms,
allocated bytes on the sim thread and packets offered. `[slowtick]`'s
`step:gameOverMigration` timings are unchanged and are what the gate parses.

### Before (master-equivalent P765 batch=8), local 394-peer reproduction

| Part | Value |
|---|---|
| `DeclareGameOver` (whole handoff tick) | 170–223 ms |
| `rotate-migrate` (assemble + tail setup + first prep batch) | 24–47 ms local; **87.3 ms on the rig** (P765 log, 2026-10-24) |
| `step:gameOverMigration` | 20 ms rising to **527 ms** within one migration; **80 ms on the rig** |
| Migration ticks | 147 |
| Max whole tick | 247–292 ms (the rest is normal `broadcastShipStates` load) |
| Mean tick | 77–79 ms |

Phase attribution of the old per-peer design (latch sweep already bulk in this sample,
fan-out still present — same reseat code master runs):

| Phase | Per 8-peer batch (394 peers) |
|---|---|
| prep (mint/register + per-peer latch scans) | 3 ms/peer ⇒ ~24 ms/batch (small local latch volume; the rig is larger) |
| reseat (`AnnounceFreshShip` + `TryJoinSide` broadcast) | 20 ms → **527 ms** as already-reseated peers grew the audience; 6 296 packets/batch |
| burst (`CompleteJoinAndLaunch`) | 5–23 ms |

### After

| Phase | 8-peer batch (394 peers) | Notes |
|---|---|---|
| latch sweep | 14 steps, **max 27.5 ms**, mean 5.6 ms, sum ~78 ms | one full pass per collection, spread one collection/tick |
| prep | **max 3.6 ms, mean 0.46 ms** | no per-peer scans; `RetireViewerUpdateCadence` and the O(1) removes stay here |
| reseat | **max 1.0 ms, mean 0.82 ms**, **0 packets** | silent: side/leader/owner set server-side, wire state rides the join burst |
| burst | **mean 13.9 ms**, ~2 992 packets/batch | one PLAYERINFO-per-ship packet per recipient, no mission-wide catch-all announce |
| `rotate-migrate` setup | **0.3–0.4 ms** | tail creation only; first batch no longer inline for >batch migrations |

### Why it doubled since batch 20 (45 → 90 ms)

`git log` on `Server.Lobby.cs` / the rotation path since 2026-10-21:

- **P775** (2026-10-21, `04f21f0e`) added the single `_playerInfoSent` latch and the
  per-new-ship `RemoveWhere(e => e.ShipId == ship.Id)` (`AnnounceFreshShip`). That makes
  every migrated ship scan the whole `(recipient, ship)` set, which the migration itself
  grows to roughly `peers²` entries.
- **P797** (2026-10-23, `7734a197`) added `_crossSideDiscoverySent`
  `(recipient, kind, objectId)` — one entry per peer per seen static object — plus the
  join-burst per-side seen exports. Its volumes are the largest of all latch sets at
  200v200, and `ClearPeerJoinLatches` scanned it once **per migrant**.
- **HUD-20** (2026-10-21, `91456ca0`) extended the same clear path with the cross-side
  ship-status latches.

So batch 20's deployed build (2026-10-24) contained both new recipient-keyed latch
families. The per-peer clear cost is `O(migrants × latch entries)`; adding P775/P797
multiplied the second factor. No unrelated rotation change landed in that window.

## 2. Behaviour rows

Every row: the native rule (with cite), what the port did before, what it does after, how
the data reaches the client, and the native classification.

| # | Native rule | Native cite | Port before | Port after | Client data path | Class |
|---|---|---|---|---|---|---|
| 1 | Finalize scores before reset | `FedSrv/fsmission.cpp:3225-3263` | same | unchanged | `GAME_OVER(62)` side totals + `GAME_OVER_PLAYERS(63)` roster | design |
| 2 | Queue GAME_OVER, flush, **then** reset | `fsmission.cpp:3437-3438`, `3444-3448` (player `Reset(true)`), `3457` (`Vacate`), `3490` (`SetStage` NOTSTARTED) | GAME_OVER sent once per peer before the rotation/assembly | unchanged; first migration batch deferred for >batch migrations so no peer's reset burst is even *built* in the handoff tick | existing `FM_S_GAME_OVER(62)` + `FM_S_GAME_OVER_PLAYERS(63)`; no new field | design |
| 3 | `Vacate()` keeps players seated; no rejoin broadcast | `fsmission.cpp:2604-2622` | per-migrant mission-wide PLAYERINFO + JOIN_SIDE/SET_TEAM_LEADER/SET_MISSION_OWNER fan-out | silent reseat; each peer's own join burst carries every ship's PLAYERINFO and the final lobby state | existing `FM_S_PLAYERINFO(10)`, `JOIN_SIDE(125)`, `SET_TEAM_LEADER(121)`, `SET_MISSION_OWNER(123)` — the lobby state is built by `CompleteJoinAndLaunch` | design (native keeps the mission object; the port cannot — see 5) |
| 4 | `ResetMission`: move ships to null cluster, nuke clusters, destroy buckets | `Igc/missionigc.cpp:3804-3828` | fresh `Mission` assembled at rotation (identical content) | unchanged; first batch deferred | `MISSION_STAGE(37)` NotStarted + per-side world EXPORTs in the burst | era constraint (port assembles a new immutable world; native regenerates later) |
| 5 | Regenerate ids/clusters at the next start | `missionigc.cpp:3830-3845` (`m_nextShipID=0` …), `fsmission.cpp:1954`, `2031` (`SendMissionInfo(NULL, pside)` at start) | assemble now | unchanged (P765 plan cache kept) | `CLUSTERINFO(200)` + `FM_S_EXPORT(26)` per seen object | era constraint |
| 6 | Native sends no game-over re-announcement | `fsmission.cpp:3437-3490` (queue/flush/reset only) | 788 packets/migrant during reseat | 0 reseat packets; the burst is the only sender | `PLAYERINFO(10)` per ship, one packed packet per recipient | design |
| 7 | Port-only: reset per-(recipient, ship/object) identity latches on rotation | none (port adaptation; mirrors native's reset of its own per-object tables) | 13 full scans **per migrant** | one full scan **per collection per migration**, spread one collection/tick | server bookkeeping only — enables the existing identity messages | bug (P775/P797 added latch volume without an index); modern: per-recipient keyed index |
| 8 | Port-only: spread a large migration; never split GAME_OVER from its audience | `fsmission.cpp:3437-3438` order | P765 8 peers/tick, still superlinear per peer | 8 peers/tick, now linear per peer; small migrations still complete in the handoff tick | no change | design |

Behaviour parity is preserved: every peer gets GAME_OVER + the result roster before any
of its state is reset, is visible in exactly one mission at a time (the old `_peerShips`
entry is replaced when its fresh ship is registered), keeps its side id, and the next
match's countdown (P660 policy) starts only after `FinishPendingRotation`, i.e. after the
last join burst.

## 3. What changed

| File | Change |
|---|---|
| `src/Allegiance.Server/Server.Lobby.cs` | `RunMigrationLatchSweep` (bulk, one collection/tick); `PendingRotationTail.MigrantConns/OldShipIds/LatchSweepStep/LatchesCleared`; per-phase `Mark` timing (`phaseMs`, alloc, packets); `rotate-migrate-setup` timing; small-migration synchronous drain; `TryJoinSideCore(..., announce:false)` for migration; `CompleteJoinAndLaunch(..., announceToMission:false)`; migration reseat latches the fresh id but no longer calls `AnnounceFreshShip`; deferred first batch for >batch migrations; internal `LastGameOverDeclareMs`, `LastGameOverDeclareGcPauseMs`, `LastRotateMigrateSetupMs`, `MaxGameOverMigrationPhaseMs`, `GameOverMigrationTicks` for the gate; allocation/GC deltas in `SwMark`; `[gameover]` log includes the result packet size |
| `src/Allegiance.Server/Server.BroadcastSnapshot.cs`, `src/Allegiance.Server/Server.cs`, `src/Allegiance.Server/Server.Broadcast.cs` | `_egressPacketsOffered` counter (packet sibling of the byte counter), reported in `[egress]` and the migration timing |
| `tests/Allegiance.ClientCore.Tests/P807GameOverHandoffTests.cs` | new 394-peer gate + order capture; 24-peer two-consecutive-handoff test |
| `docs/parity/fedmsg-wiring.csv`, `docs/parity/sim-rules-matrix.csv` | parity rows updated (see §7) |

The latch sweep keeps the exact predicate of the old per-peer method: free every entry
whose *recipient* is a migrant, or whose *subject ship id* is one of the migrants'
retired ships. The recipient dimension covers the non-ship object latches (stations,
mines, discovery kinds). The O(1) recipient/ship entries (`RetireViewerUpdateCadence`,
`_lastStatus*`, `_teammateLoadoutCache`, `_shipDetected`, `_nextTeammateRefreshTick`)
stay in the bounded prep batch.

## 4. Tests

New `tests/Allegiance.ClientCore.Tests/P807GameOverHandoffTests.cs`:

- `P807_394_peer_handoff_is_cheap_ordered_and_migrates_every_peer` (Tier Live, 394
  peers — the rig's 200v200 count). **Fails on master's behaviour** (the test reads the
  P807 timing fields/log lines; the before numbers below are what those budgets reject):
  on master the reseat slice grew past 100 ms (20 → 527 ms locally; 80 ms on the rig),
  so the phase/step budgets fail. It also asserts the per-peer
  offer order `GAME_OVER(62)` before `JOINED_MISSION(95)`/`SET_CLUSTER(7)` through the
  production `TestPublicationCapture` seam. It also asserts every peer received
  GAME_OVER, has a new ship in the fresh NotStarted mission on its original side, all
  394 are gone from the old mission, `SimPassStageFailures == 0`, and no `[simpass]`
  line was logged.
- `P807_two_consecutive_handoffs_still_work` (Tier Live, 24 peers): rotation → all
  peers re-seat → ready up → the next match starts → a second forced game over → the
  second migration drains, with the second match actually `Started` in between.

Budgets: migration phase `< PerfCalibration.Scale(80 ms)`, `rotate-migrate` setup
`< Scale(30 ms)`, `step:gameOverMigration` `< Scale(100 ms)` (the task target), and the
declare tick's sim work excluding process-wide GC pauses `< Scale(150 ms)` (local
in-process clients dominate local GC; the other assertions are environment-independent).
Passing samples are quoted in §5.

## 5. Verification results

- Whole-solution build `dotnet build AllegianceCore.slnx -c Debug`: **0 warnings / 0 errors**.
- `tools/build-tools.sh`: **all 72 tool projects built, 0/0**.
- AOT publish `dotnet publish src/Allegiance.Server/Allegiance.Server.csproj -c Release
  -r linux-x64 --self-contained -p:PublishAot=true`: **succeeded, no IL2026/IL3050**.
- `P807GameOverHandoffTests` **3× alone, passed 3/3** (final code):
  `declareMs=189.7/173.0/167.6`, GC pause `71.0/72.7/69.3`, sim work
  `118.7/100.3/98.3`, `rotateMigrateSetupMs=0.3/0.4/0.3`, `migrationTicks=164`,
  `maxPhaseMs=28.8/54.0/45.9`, `maxGameOverMigrationStepMs=29/54/46`.
  (The 394-peer join takes ~20–85 s depending on machine load; the test is Live-tier.)
- `P765GameOverStallTests` **3× alone, passed 3/3**; the 48-peer game-over tick is now
  **3.5–3.6 ms median** (was 5.9 ms in P765), and 24 peers still all receive GAME_OVER
  and migrate.
- `P660NextMatchLiveTests` + `P682MissionStateTests` **3× alone, 11/11 passed each run**
  (including the synchronous small-migration behaviours: `enabled:false` reaches
  `Starting` in the game-over call, and `_viewerPoseCadence` is empty right after End).
- `P480RotationKeepsPlayersLiveTests` **3× alone, passed** (leaders, owner, original
  sides, new-world exports, new game starts without reconnect).
- `P730ShowcaseLiveTests.Two_local_showcase_matches...` (30 bots, two full matches)
  **passed**; P807's own 24-peer two-handoff test passed 3/3.
- `ClientJoinSideLiveTests` + `LeaderSameSideBotClientLiveTests` **3× alone** after
  preserving the two-argument `TryJoinSide` reflection signature.
- ONE fast tier (`tools/run-tests.sh --tier fast`): **PASS — 10 129 tests, 288.3 s,
  0 failures** (Lobby 24; MonoGame 3 278 + 3 not-executed; ClientCore 1 734; Core 5 078;
  quarantine 12).
  An earlier fast run hit two documented load flakes only:
  `Hud12bReviewFixTests.HUD_formatted_readouts_allocate_nothing_in_steady_state`
  (2 464 B) and `P784ComposeTests.Fanout_issues_one_order_message_per_recipient_on_the_wire`
  (`docs/TEST_FLAKES.md`); both passed **3/3 alone** immediately after, and the next
  full fast run passed.

## 6. Gate numbers vs target

| Metric @394 peers | Before (master-equivalent) | After (3 final runs) |
|---|---|---|
| migration phase max | reseat 20 → 527 ms | **28.8 / 54.0 / 45.9 ms** |
| `rotate-migrate` setup | 24 ms local / 87.3 ms rig | **0.3–0.4 ms** |
| `step:gameOverMigration` max | 80 ms rig; >100 ms locally | **29 / 54 / 46 ms** |
| migration ticks | 147 | 164 (+14 one-collection latch sweeps, + Finish) |
| mean whole tick | 77–79 ms | **~20–22 ms** with the rig-style broadcast tiers; local without tiers ~53 ms |
| whole-tick max | 247–292 ms | locally 170–195 ms, dominated by `broadcastShipStates` on 394 in-process clients (not the handoff; the handoff step is 29–54 ms) |

Target is "no tick ≥ 100 ms at the handoff". The handoff step is now 29–54 ms locally;
the rig's normal tick is ~45 ms, so the expected rig handoff tick is ~55–75 ms. The local
whole-tick maximum is not a fair rig number: 394 `ConnectionSession` clients live in the
same process as the server, so their receive/GC work and the per-peer frame-pool copies
of the 24 KB GAME_OVER packet appear in the measured tick (`send-gameover` alone is
3.4 ms on the rig, 70–110 ms locally). That is why the gate budgets the migration step
and the declare's sim work separately.

## 7. Parity rows updated

- `docs/parity/fedmsg-wiring.csv`:
  - `PLAYERINFO(10)` — rotation sends one PLAYERINFO per ship inside each migrated
    peer's join burst (native `fsmission.cpp:3958-3968`); no per-migrant mission-wide
    announce.
  - `GAME_OVER(62)` / `GAME_OVER_PLAYERS(63)` — per-peer offer order before the reset
    burst pinned at 394 peers.
  - `SET_CLUSTER(7)` — exactly one per migrated peer, after GAME_OVER.
  - `JOIN_SIDE(125)` — rotation reseat is silent; the join-burst lobby state carries
    side/leader/owner, as native's reset sends no JOIN_SIDE.
- `docs/parity/sim-rules-matrix.csv`: `SIM-0183 TerminateMissionEvent@4837` notes the
  P807 deferred/bulk handoff and the new evidence.

No native wire field/message was added; the client data path is entirely the existing
native messages listed in §2.

## 8. Classification and modern alternative

| Behaviour | Class | Modern better approach |
|---|---|---|
| Players stay seated across a reset; no re-announce | **design** (native resets in place) | the port must rebuild the mission object, but treats the re-sync as a per-recipient batch, not a thundering herd — the deferred/interest-managed handoff taken here |
| Regenerate world/clusters at the next start | **era constraint** (single-threaded 2000s server, world generated in place) | assemble the next world off the critical path / double-buffer it; the port already caches plan selection and assembles in ~2 ms |
| Per-(recipient, object) one-shot latches | **design**, with a **bug** in this port: adding P775/P797 latch volume without an index made the reset `O(migrants × entries)` | index latches by recipient (or key by a per-connection generation/epoch) so a reset is `O(recipient entries)`; fewer/wider sync packets would also remove the remaining per-object export loop |
| GAME_OVER copied into one pooled frame per peer | **era constraint** (native's outbox also queues one copy per connection) | shared immutable payload + multicast enqueue; not changed here because the wire is identical and the rig cost is 3.4 ms |

## 9. Rig recipe (for Claude; Pi lane cannot touch 221)

At the next 221 game over with 394 peers, with the server's normal verbose/profiler
gate on, expect one `[gameover-timing]` sequence:

```
part=declare-start / skills-report / finalize-scores / encode+audience / send-gameover
part=rotate-assemble
part=rotate-migrate-setup setupMs=<small>
part=migrate phase=latch-sweep step=0..13 doneMs=...
part=migrate phase=prep batch=8 done=...
part=migrate phase=reseat batch=8 done=...
part=migrate phase=burst batch=8 done=... packets=...
```

and `[slowtick]` lines with `step:gameOverMigration`. Success criteria: no
`step:gameOverMigration` ≥ 100 ms; `rotate-migrate-setup` a few ms; `phase=reseat`
~0 packets; `phase=burst` the largest slice. If the rig's full-match latch volume makes
`phase=latch-sweep` the largest slice (> 60 ms), lower
`GameOverMigrationBatchPerTick` only for the burst, or split the two large collection
sweeps (`_playerInfoSent`, `_crossSideDiscoverySent`) across two ticks each — both are
constant-only changes. If the rig's intermission is shorter than the drain time, the
queue holds the countdown (by design: the next match never starts before migration
completes), so size the intermission for 394 peers (164 ticks ≈ 8 s at 20 ups).

## 10. Unfinished / notes

- The rig's absolute tick is not measured here (lane rule: no 221 access). The receive
  recipe above is the remaining verification step.
- The remaining per-object burst (one `FM_S_EXPORT(26)` packet per seen asteroid/warp/
  station per migrated peer) is native's join shape; batching those exports into a
  single packet per peer would cut packet count but not the playable behaviour. It is
  left as a follow-up (would be another O(peers × objects) wire-shape change).
- `send-gameover` still copies the 24 KB result packet into one pooled frame per peer
  (native queues one message per connection too). The local 70–110 ms figure is
  in-process-client frame-pool/GC pressure; the rig measured 3.4 ms for this part.

## Native references

- `FedSrv/fsmission.cpp:3225-3263` — score finalization before reset.
- `FedSrv/fsmission.cpp:3437-3438` — `QueueGameoverMessage()` + `SendMessages(..., FM_FLUSH)`.
- `FedSrv/fsmission.cpp:3444-3457` — player `PlayerScoreObject::Reset(true)` then `Vacate()`.
- `FedSrv/fsmission.cpp:3490` — `SetStage(STAGE_NOTSTARTED)`.
- `FedSrv/fsmission.cpp:3527` — `m_pMission->ResetMission()`.
- `FedSrv/fsmission.cpp:2604-2622` — `Vacate()`: drones deleted, players reset in place.
- `FedSrv/fsmission.cpp:539-645` — `AddPlayerToSide` / JOIN_SIDE (native re-seat path).
- `FedSrv/fsmission.cpp:369-411` — `AddPlayerToMission` + `SendLobbyMissionInfo`.
- `FedSrv/fsmission.cpp:3936-3980` — lobby info: one `SendPlayerInfo` per ship to the joiner.
- `FedSrv/fsmission.cpp:4108-4250` — `SendMissionInfo`: per-side world exports at start.
- `FedSrv/fsmission.cpp:1954`, `2031` — next-start regeneration + per-side `SendMissionInfo(NULL, pside)`.
- `Igc/missionigc.cpp:3804-3828` — `ResetMission`.
- `Igc/missionigc.cpp:3830-3845` — `GenerateMission` resets ids/clusters.

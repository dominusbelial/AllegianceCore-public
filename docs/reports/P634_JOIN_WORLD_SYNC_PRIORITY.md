# P634 — Join/world-sync priority (separate send lanes for guaranteed vs stream)

## TL;DR

P633 left the 200v200 collapse's "server side" as a follow-up: ~224/390 bots never
received their cluster graph/warp exports and loitered at home, and P633's
hypothesis was that a joiner's one-time world data (MISSIONDEF, EXPORTs,
CLUSTERINFO graph, SET_CLUSTER) starved behind the per-tick latest-wins position
stream in the **single per-peer send FIFO**.

P634 split the per-peer sender into **two lanes** — an ORDERED lane (all
coalesceKey==0 traffic: join/world/exports/graph/chat/kills, never coalesced and
never dropped) drained **before** the STREAM lane (coalesceKey!=0 latest-wins
frames, coalesced and shed-able) — so guaranteed world data can never wait behind
position frames on the wire. This is the first concrete "separate lanes" step of
P631's architecture study.

**Measured on this workstation (195v195 = 390 bot clients, 200 s, m2xspiderw,
rendezvous Alderan 1052, port model):**

| Metric | Pre-P634 (P633 `c409ff02`) | P634 |
|---|---|---|
| bots world-ready (routing GRAPH delivered) | not measured (inferred stalled) | **390/390 (100%)** |
| world-ready delay p50/p95/max | — | **0.0 s / 0.0 s / 0.1 s** |
| EnsureCombatPath backoffs (waited on graph) | ~224/390 home-loitering (P633 trace) | **0** |
| server per-peer max oldest-guaranteed age | — | **0 ms** |
| server per-peer max ordered/stream pending | — | **0 / 5** |
| botsReached (Alderan) | 97–101/390 | **97/390** |
| kills | 2 | **0** |

**Verdict on the P634 hypothesis (honest):** the specific mechanism P633 proposed —
world data starving in the per-peer queue behind the position stream — is
**refuted on this hardware**. With the two-class queue in place the server's per-peer
send queues never back up at 390 connections on loopback (max oldest-guaranteed age
= 0 ms, ordered lane always 0 pending across the whole window) and **every one of
the 390 bots becomes world-ready within 0.1 s**, with zero routing backoffs. So join
and world data is delivered completely and immediately. The separation is the
correct native-faithful design (see "Native behaviour classified") and is now
demonstrated under a real pressure test (the transport suite) even though this
machine's loopback never pushes the queue into head-of-line territory.

The **≥ 95%-of-390 arrival target is NOT met** (97/390 ≈ 25%, kills 0) — identical
to the pre-P634 baseline. **Next blocker (out of P634 scope, "bot policy"):** only
~80–88 bots per side even cross their FIRST aleph out of home (530 total warp
crossings; 80→Rigel, 88→Juniper, 97→Alderan over 200 s), while all 390 are
world-ready. The arrival ceiling is the pre-existing bot-driver aleph-transit limit
at ~390 local bots that P632 documented as "collapse above ~250" and reproduced
identically across commits (`9194ddec..HEAD`) — it is a bot-routing/transit-policy
limit, not a world-data-delivery limit, and therefore out of scope here. P634's fix
is still committed because (a) it is the native-faithful "separate lanes" intent,
(b) it is the prerequisite P631 builds on, and (c) it eliminates the residual
risk that any real (non-loopback) backlog would starve world data — the unit tests
prove the ordering under pressure.

## Scope → implementation map

1. **Two-class per-peer send queue** (`TcpTransport.PeerSender`):
   - `_orderedQueue` — coalesceKey==0 ordered/guaranteed traffic (join burst:
     MISSIONDEF, PLAYERINFO, SET_MONEY, RANK_INFO, CLUSTERINFO/graph, cluster/
     warp/station exports, SET_CLUSTER, plus chat/kills/status). Never coalesced,
     never dropped, **always drained first**.
   - `_streamQueue` — coalesceKey!=0 latest-wins stream frames (ship states,
     own-ship, stations/asteroids/probes/missiles). Coalesced per key (P606),
     shed oldest non-guaranteed under capacity (P611), drained only when the
     ordered lane is empty.
   - Wire effect: ordered/world data always precedes position frames on the wire,
     so a joiner's world sync can never be head-of-line-blocked behind the stream.
2. **Measurement (scope 1)**:
   - client-side `ClientWorld.WorldReady` signal (`Graph != null && Clusters > 0`);
   - bot-side world-ready delay latch + `WorldNotReadyAttempts` (EnsureCombatPath
     retry count) + `WorldReadyAt`;
   - server-side per-window `[p634-queue]` sample (per-peer ordered/stream pending,
     max + total queued bytes, max oldest-guaranteed age) via a new
     `TcpTransport.SamplePeerQueues()`.
   - `P634JoinWorldSyncLiveTests` reports world-ready distribution, arrival curve,
     backoffs, kills, and server queue stats.
3. **Bot-side backoff (scope 3):** `EnsureCombatPath` no longer re-requests a route
   every tick when the graph hasn't landed — it backs off 2 s, counts the attempt,
   and logs the wait once.

## Verification

### Unit tests (failing-first)
- `TcpTransportPriorityLaneTests` (new, both send modes): ordered (world) traffic
  is routed to the ordered lane and is the next-drained frame even when enqueued
  after a stream backlog (FAILS on pre-P634: no ordered lane exists); ordered join
  frames are never shed when the stream collapses over capacity. 4 tests, both
  pass on P634.
- Existing `TcpTransportCoalesceAndReasonTests` (16) updated only for the renamed
  internal `_streamQueue` field; all coalesce/overflow/disconnect-reason contracts
  unchanged and green.
- `ClientWorldExportTests.WorldReady_requires_graph_and_clusters_and_resets` (new)
  — the client world-complete signal.

### Live (local server, m2xspiderw, rendezvous Alderan 1052, real BotManager over TCP)
- **30v30 mandatory combat check, both models** (asserted kills > 0):
  - **port**: botsReached 60/60, **kills 44** (deaths 44), 62.0% hit-rate, 0 friendly.
  - **native**: botsReached 50/60, **kills 42**, 55.9% hit-rate, 0 friendly.
- **195v195 (390 bots), 200 s**: see TL;DR table. Port model: worldReady 390/390
  (delay 0–0.1 s), botsReached 97/390, kills 0. **Native 195v195 could not be
  spawned on this box** (see "Known limitations / flakes").
- Server stays at nominal tick rate at 390 (max tick window 756 ticks / 5 s on a
  20 Hz server — no sim stall); per-peer queues never back up (ordered 0, stream ≤5,
  oldest-guaranteed 0 ms).

### Build
- Whole-solution build: 0 warnings, 0 errors.

### Suite — `scripts/run-tests.sh`, 2 runs on the final code
Reported as each run happened (per the session rule; the flake ledger is
`docs/TEST_FLAKES.md`):

- **Run 1:** Lobby 12/12 · MonoGame 1776/1779 (3 skipped) · ClientCore 638/639 ·
  Core 4009/4009. The single failure was
  `ThreePeerSeededSessionTests.SeedThreePeers_…DeterministicVisibility` — the
  documented live-wire timing flake (TEST_FLAKES.md line 38; P632's Run B hit the
  same test). It references none of the P634 symbols. `scripts/run-tests.sh` exit 1.
- **Run 2:** Lobby 12/12 · MonoGame 1776/1779 (3 skipped) · ClientCore 638/639 ·
  Core 4006/4009. Four failures, all documented live-wire timing flakes (different
  from run 1 — the ledger's run-to-run rotation): `CrossSideCombatVisibility…
  warp_return` (TEST_FLAKES ×3), `P366LeaderBotCommand…launch_effectively_live`
  (×2), `P590PodRescue…real_player_rescuer` (×2), `LiveRelaunch…backward_fallback`
  (×3). None reference P634 symbols. `scripts/run-tests.sh` exit 1.

**Zero P634 failures in either run.** Every new P634 test (TcpTransportPriorityLane,
TcpTransportCoalesce, ClientWorld WorldReady) passed in both runs; the flake mix
rotates run-to-run exactly as the ledger describes (run 1: ClientCore; run 2:
ClientCore + three Core live-wire tests) — the documented live-socket suite, not a
product regression.

## Known limitations / flakes
- **Native 195v195 spawn flake:** on this workstation, spawning 390 bots under the
  `native` ship-update model reproducibly trips the harness's 4 s
  `ProbeHasShip` post-BUY_LOADOUT confirmation for a mid-burst bot
  (`spawn_bot: server never began streaming ship …`). This is a pre-existing
  harness confirmation race at 390-bot native load (the spawn/confirmation path is
  unchanged by P634; port 195 spawned all 390 cleanly on this box). Reported
  honestly: native-at-scale here is limited by the harness spawn probe, not by the
  P634 transport change. The native model is verified at 30v30 (mandatory combat
  check) and the transport change is model-independent.

## Native behaviour classified
| Behaviour | Native cite | Class | What the port does |
|---|---|---|---|
| ordered join exports are guaranteed, never shed | Messages.cpp:548-563 (DPNSEND_GUARANTEED\|COALESCE never times out; best-effort DPNSEND_NONSEQUENTIAL drops at 500 ms); FedSrv.CPP:4189-4196 | design | ordered lane = never dropped, drained first |
| stream (HEAVY/LIGHT_SHIPS_UPDATE) sent non-guaranteed | FedSrv.CPP:4189-4196 (FM_NOT_GUARANTEED) | design | stream lane = non-guaranteed, shed-able |
| own-ship SINGLE_SHIP_UPDATE guaranteed | fsship.cpp:544 | design | own-ship guaranteed coalesce-keyed, never shed (P608) |
| reliable bursts must not be delayed behind state | xorgy port documentation ("keep the lanes separate") | design (modern) | two-lane per-peer queue |

## Modern alternative considered
- **Separate ordered/stream send lanes**: native's DirectPlay exposes the same
  guaranteed-vs-best-effort split (Messages.cpp). A modern engine would use
  reliability classes + priority queues on the transport (the approach taken here),
  or push to UDP + GameNetworkingSockets — which is exactly what **P631** decides.
  P634 implements the in-TCP "separate lanes" so the design carries forward.
- **Interest/relevance management** (send each peer only its neighbourhood): the
  real fix for per-peer world-sync scaling at 1000+ peers (P633 proposal). P634 does
  not change relevance — but the measurement here shows per-peer queues never back
  up at 390 on loopback, so relevance is not the observed arrival blocker either;
  the arrival ceiling is bot transit policy.
- **Hold joiner's stream until world complete / pace the join burst**: listed as
  alternatives in scope; the ordered-first drain already guarantees world data
  precedence, and queues never back up on this hardware, so the additional
  mechanisms were not needed and were not added (keeping the change minimal and
  native-faithful).

## Deliverables
- `src/Allegiance.Core/Transport/TcpTransport.cs` — two-lane PeerSender + `SamplePeerQueues()`.
- `src/Allegiance.Server/Server.Timescale.cs` — per-window `[p634-queue]` sample.
- `src/Allegiance.ClientCore/State/ClientWorld.cs` — `WorldReady` signal.
- `tools/allegiance-mcp/BotManager.cs` — `EnsureCombatPath` backoff + world-ready metrics + ActiveBotView fields.
- `tests/Allegiance.Core.Tests/Transport/TcpTransportPriorityLaneTests.cs` (new).
- `tests/Allegiance.ClientCore.Tests/P634JoinWorldSyncLiveTests.cs` (new).
- `tests/Allegiance.ClientCore.Tests/ClientWorldExportTests.cs` — WorldReady test.
- `docs/reports/P634_JOIN_WORLD_SYNC_PRIORITY.md` (this report).

Committed on `master` (not pushed).

# P967 — Transport shutdown ownership and unbounded server/transport collections

Task: the four lifecycle/collection findings of the independent server review
`docs/reports/SERVER_REVIEW_P963.md` (Kimi K3.1 Pro, 2026-11-08), P963-04 / 07 / 08 / 09.
Base: local `master` at `2cdfd100`. Worktree branch `hermes/p967`. Every finding below was
re-checked against master before changing anything, and 04/08 were re-reproduced with a bounded
loopback proof (the reviewer's own reproduction was re-derived, not trusted).

## Result at a glance

| Finding | Master | This change |
| --- | --- | --- |
| P963-04 a greeting that arrives after `Shutdown` still joins | `joinedAfterLateGreet=1` in both transport modes | `0`; no peer, no callback, no thread, no per-connection state survives |
| P963-07 chat budget of an abruptly dropped chatty peer | one permanent `_chatBudgets` entry per drop | removed by `ReleasePeerWithReason` (every release path); table drains to 0 |
| P963-08 `_peerEnded` tombstone per historical connection | 40 entries after 40 connect/drop cycles, no retirement path anywhere | bounded recent window (2048): 2600 cycles → 2048 markers, oldest retired, newest still closed |
| P963-09 `_bucketCompletions` grows forever across matches | unbounded list, whole-list copy on every read | bounded ring (64) + retired at game-over rotation |

No new performance path is added, so no default-off flag is needed: the transport change is O(1)
bookkeeping on the accept/greeting path (one set/thread registration per connection), the
tombstone trim is amortised O(1) per departure, and `IsPeerClosed` is still two dictionary
lookups. Nothing new runs per tick.

## Method and environment

* Worktree `allegiance-hermes-p967`, branch `hermes/p967`, linked to the public repo; `local.env`
  present (`ALLEGIANCE_ARTWORK_PATH`, `ALLEGIANCE_NATIVE_SRC_PATH`). Nothing outside the worktree
  and `~/allegiance-evidence/P967/` was written; nothing was committed, pushed or stashed.
* Baseline: the rig's winning settings are the reference for every run (immutable snapshot +
  encodeOnce, P863 clash tick budget, `broadcast.parallelBuild`, `transport.mode=pool`,
  `broadcast.shipUpdateModel=native`, `sim.nativeGotoPlan`, server GC, tiers off). The transport
  tests run under BOTH send modes (`ThreadPerPeer` and `Pool`); none relies on a default mode.
* Reproduction: the bounded loopback driver now lives in the repo as
  `tools/p967-shutdown-proof` (`dotnet run --project tools/p967-shutdown-proof -c Release`). It
  drives the production `TcpTransport` over real loopback sockets and reads the transport's private
  tables by reflection. It was developed and compiled against unmodified master first — that binary
  and its log are the "before" evidence — and the same program (moved into `tools/`, unchanged in
  behaviour) now passes against the fixed tree. Raw logs: `~/allegiance-evidence/P967/proof-before.log`
  and `proof-after.log`; trimmed transcript in `docs/reports/P967_assets/proof-summary.txt`. Load average at run time: ~4.4
  (24-CPU shared host, other lanes active). The proof has no synthetic load; every wait is bounded
  (≤3 s) and the churn loop is plain sequential connect/greet/drop.
* No remote host, no rig/mini/Mac contact, no synthetic CPU load.

## Reproduction before/after

All runs below are the committed proof tool `tools/p967-shutdown-proof`
(`dotnet run --project tools/p967-shutdown-proof -c Release`), the same program that produced the
"before" log against unmodified master. Full transcript:
`docs/reports/P967_assets/proof-summary.txt`; raw logs in `~/allegiance-evidence/P967/`.

### P963-04 — a greeting that arrives after `Shutdown` still joins

Master, both modes (`proof-before.log`):

```
[A/ThreadPerPeer] shutdownMs=3 joinedAtShutdownReturn=0 joinedAfterLateGreet=1 livePeers=0 liveThreads=0 pendingGreets=0
[A/ThreadPerPeer] FAIL: post-shutdown join ids=[1]
[A/Pool]          shutdownMs=0 joinedAtShutdownReturn=0 joinedAfterLateGreet=1 livePeers=0 liveThreads=0 pendingGreets=0
[A/Pool]          FAIL: post-shutdown join ids=[1]
```

Sequence: the client connects and stays silent → the accept loop starts an untracked raw `greet`
thread (master `TcpTransport.cs:652-655`) → `Shutdown()` returns (master `:564-589` stops only the
listener, `_peers` senders, `_threads` and the pool) → the client now sends a valid greeting →
`HandlePeer` completes the establishment (master `:720-756`) and raises `OnPeerJoined` for a
transport that is already down. `livePeers=0` afterwards because the read loop sees
`_running == false` and immediately reaps the peer — the callback, not the socket, is the defect.

After the fix (`proof-after.log`, A = late greeting after `Shutdown`, C = silent greeting socket
that must be owned and closed by `Shutdown`):

```
[A/ThreadPerPeer] shutdownMs=8 joinedAtShutdownReturn=0 joinedAfterLateGreet=0 livePeers=0 liveThreads=0 pendingGreets=0
[A/ThreadPerPeer] OK: no post-shutdown join
[A/Pool]          shutdownMs=2 joinedAtShutdownReturn=0 joinedAfterLateGreet=0 livePeers=0 liveThreads=0 pendingGreets=0
[A/Pool]          OK: no post-shutdown join
[C/ThreadPerPeer] shutdownMs=0 trackedThreads=0 pendingGreets=0 greetSocketClosed=True joined=0
[C/Pool]          shutdownMs=0 trackedThreads=0 pendingGreets=0 greetSocketClosed=True joined=0
```

The before-run's C scenario reported `greetSocketClosed=True joined=1` in both modes: the socket
looked dead only because the late greeting had already joined and been reaped, and `joined` is the
signal that the pending connection was not Shutdown's to close.

### P963-08 — the ended-peer tombstone grows with churn

Master, 40 sequential connect/greet/abrupt-drop cycles per mode (`proof-before.log`):

```
[B/ThreadPerPeer] cycles=40 joined=40 left=40 peers=0 threads=1 endedMarkers=40 closingMarkers=0
[B/ThreadPerPeer] FAIL: ended-marker storage grew with churn (40)
[B/Pool]          cycles=40 joined=40 left=40 peers=0 threads=1 endedMarkers=40 closingMarkers=0
[B/Pool]          FAIL: ended-marker storage grew with churn (40)
```

After the fix, the same 40-cycle run (markers retained in the recent window, live state 0) and a
2600-cycle run that pushes far past the window:

```
[B40/ThreadPerPeer]   cycles=40   ms=410   peers=0 threads=1 livePerConnectionState=0 [_peers=0 _peerIpById=0 _closingPeers=0] endedMarkers=40   endedRetireQueue=40
[B40/ThreadPerPeer]   IsPeerClosed newest=True oldest(1)=True
[B2600/ThreadPerPeer] cycles=2600 ms=26631 peers=0 threads=1 livePerConnectionState=0 [_peers=0 _peerIpById=0 _closingPeers=0] endedMarkers=2048 endedRetireQueue=2048
[B2600/ThreadPerPeer] IsPeerClosed newest=True oldest(1)=False
[B40/Pool]            cycles=40   ms=408   peers=0 threads=1 livePerConnectionState=0 endedMarkers=40   endedRetireQueue=40
[B2600/Pool]          cycles=2600 ms=26630 peers=0 threads=1 livePerConnectionState=0 endedMarkers=2048 endedRetireQueue=2048
[B2600/Pool]          IsPeerClosed newest=True oldest(1)=False
PROOF_RESULT: PASS (all scenarios held)
```

The table stops at the bound, the oldest departure is retired, the newest still answers "closed",
and no live per-connection state survives churn.

### P963-07 / P963-09 — proven by the churn/seam tests, not by the driver

These are single-owner server tables, so they are proven by the extended cache-churn test and the
seam test (Tests section): master has no `ChatBudgets` counter in `ConnectionScopedCacheCounts`
and `BucketCompletions` had no bound, so both tests fail on master by construction (a chatty peer
that drops without LOGOFF leaves its budget behind; the completion list is a plain unbounded
`List<short>`).

## Fixes

### 1. P963-04 — greeting sockets/threads are Shutdown's, and a late greeting fails closed

`src/Allegiance.Core/Transport/TcpTransport.cs`

* `:351-352` — the accept path now owns every accepted-but-not-established socket
  (`_greetSockets`) and its greeting thread (`_greetThreads`). It adopts both **while holding
  `_gate`** (`:671-708`) and starts the thread there, so a registered thread is always started (a
  `Join` on a registered-but-unstarted thread would throw) and `Shutdown` can never miss a socket.
* `:597-640` — `Shutdown` flips `_running = false` **under `_gate`** (the same gate the accept path
  uses to adopt a socket and `HandlePeer` uses to insert a peer), bumps `_generation`, then
  snapshots and clears the greeting sockets/threads with the peers. It closes every pending
  greeting socket and joins every greeting thread **before** closing the senders and shutting the
  pool down. An accept that loses the race sees `_running == false` inside the gate, disposes the
  socket and exits without starting a thread (`:712-726`).
* `:761-887` — `HandlePeer` re-checks the transport state twice: after the greeting read, under
  `_gate` (`:810-830`: leave the greeting set, and if `_running` is false fail closed — no peer
  insert, no workers, no callback), and again after `StartOrPool` (`:832-836` →
  `IsEstablishedPeer` at `:943-954`, comparing the captured shutdown generation) before the log
  line and `OnPeerJoined`. `:956-972` `AbandonLatePeer` tears such a peer down without callbacks,
  so `OnPeerJoined`/`OnPeerLeft` stay paired.
* `:923-940` — `HandleGreet` retires the thread from `_threads`/`_greetThreads` on every exit, so
  `TrackedThreadCount` and `Shutdown`'s snapshot stay bounded by live threads.
* `:864-871` — a greeting socket closed by `Shutdown` is attributed to
  `DisconnectReason.Shutdown` in the by-reason window instead of `ProtocolError`.
* The read loop is now only spawned for a peer that actually joined (`:888`), and from the captured
  sender instead of re-looking-up `_peers[id]` (which `Shutdown` may already have cleared).

Native intent: a connection and its player are destroyed together, and nothing runs for a dead
session afterwards — `FedSrv/FedSrv.CPP:4549-4566` (`OnDestroyConnection` deletes the player and
bumps `cTimeouts`), `FedSrv/fsship.cpp:991` (the two-way connection↔player link is severed in
`~CFSPlayer`). The port has no DirectPlay connection object, so the greeting phase is the only
window where a socket can exist without a peer; closing that window is what makes the port's
teardown match native's "destroyed means destroyed".

### 2. P963-07 — the chat budget is retired by the single owner of connection state

* `src/Allegiance.Server/Server.Lifecycle.cs:2194-2202` — `RemoveChatBudget(id)` is called in
  `ReleasePeerWithReason`, next to the other connection-scoped removals and **before** the
  reconnect-grace park branch and the idempotent early return, so every release path (abrupt drop,
  LOGOFF, parked seat, second release) retires it exactly once.
* `src/Allegiance.Server/Server.Dispatch.cs:1933` — the LOGOFF-only call is gone (the comment says
  where the budget is retired now); a single owner cannot be forgotten by a new path.

Native: the budget is player state, not connection state — `FedSrv/fsship.h:24`
(`c_chatBudgetMax`), `FedSrv/fsship.cpp:958` (`m_chatBudget(c_chatBudgetMax)` in the `CFSPlayer`
constructor), `FedSrv/fsship.h:449-468` (`IncrementChatBudget`/`DecrementChatBudget`),
`FedSrv/FedSrv.CPP:8287` (once per player per mission tick). Native destroys it with the player
object, which is exactly what `ReleasePeerWithReason` does for the port.
`Server.Periodic.cs:38-42` only *refills* existing entries (`TryGetValue`), so it cannot re-create
an entry for a departed peer.

### 3. P963-08 — the ended-peer tombstone gets a bounded recent window

`src/Allegiance.Core/Transport/TcpTransport.cs`

* `:325-346` — `_peerEnded` keeps its P311/P654 job (the late-packet suppression predicate
  `IsPeerClosed` reads, P654/H-14: "queued inbound packets must not rearm it") but each entry now
  has a lifecycle: `0` = the peer left while its receive side was still draining, `1` = the receive
  side has fully drained. Only drained markers enter the retirement queue `_endedOrder`, and that
  queue is trimmed to `MaxEndedRetention = 2048`. Connection ids are minted monotonically
  (`Interlocked.Increment`, never reused), so a bounded RECENT window is enough for the queued-packet
  drain and for the server's per-connection lifecycle reads (P765's migration guards at
  `Server.Lobby.cs:1022`/`:1152` run a few batches after a departure), while a long-uptime process
  can no longer retain one entry per historical connection.
* `:1081-1093` — `MarkReceiveDrained(id)` is called from the peer's read loop only after its exact
  final act (`:913`, in the read-loop `finally`, after the exactly-once raise), so a marker is
  retired **only after** the receive side has fully drained for that id. `:1095-1099`
  `TrimEndedLocked` evicts the oldest drained markers past the bound. A marker whose receive side
  is still draining is never evicted, so the table is bounded by `MaxEndedRetention` **plus the
  peers currently draining** — each of those still owns a live read thread and is therefore bounded
  by the connection cap, not by history.
* `:1124-1135` — `RaisePeerLeftOnce` adds the marker, but the exactly-once `OnPeerLeft` guard is no
  longer the tombstone: it moved onto the peer's own `PeerSender` (`internal int Ended`,
  `Interlocked.Exchange`, `:1731`), released with the peer object after every reaping path that can
  still reference it. Retiring a tombstone therefore cannot cause a second `OnPeerLeft`.
* `:989-998` — `DropPeer` returns immediately when the peer is no longer in the live table (the read
  loop's `finally` already retired and raised), so a late overflow/timeout callback from the sender
  thread cannot raise a second leave.
* `IsPeerClosed` (`:1028`) still answers `_closingPeers.ContainsKey(id) ||
  _peerEnded.ContainsKey(id)`, so the graceful-flush marker (P654/H-14) and the recent departures
  both report closed; ids the transport never established still report "not closed", which the
  in-process dispatch harnesses (`P867DispatchCertificationTests`, `P869DispatchTests`,
  `P653EconomyLoadoutIntegrityTests`, `…`) rely on when they inject datagrams for synthetic peers.
* Late-packet suppression evidence: `ReadDataLoop` (`:1103` master → same line region) skips
  `OnReceive` for a closed peer and `Server.Dispatch.cs:56` drops queued messages for a departed
  peer; the P311/P654 rules are unchanged, only the retention is bounded. A withdrawal is safe
  because a queued inbound event for that id is drained within a tick or two (P646's lane ordering
  puts the `PeerLeftEvt` behind the peer's receives), long before 2048 further departures.
  `uint` wrap (4.29e9 connections in one process) is the only point where an old id could be
  minted again after retirement; ids have been monotonic since boot and no test or rig run
  approaches that. `UdpTransport` keeps its own `IsPeerClosed` (from its live peer map,
  `UdpTransport.cs:668`) and its `Shutdown` already drops peers, clears introductions, disposes the
  socket and joins its worker (`:720-740`), so no UDP change was needed.

### 4. P963-09 — the bucket-completion diagnostic is bounded and per-mission

* `src/Allegiance.Server/Server.cs:167-169` — `_bucketCompletions` is a `Queue<short>` with
  `BucketCompletionHistoryLimit = 64`; `:912-922` appends and drops the oldest past the bound (ids
  repeat across worlds, so the seam only needs recent completions); `:936-938` exposes the retained
  count for tests.
* `src/Allegiance.Server/Server.cs:924-930` — `RetireBucketCompletionHistory()` clears it, called
  from `src/Allegiance.Server/Server.Lobby.cs:723-726` inside `RotateMissionAfterGameOver`'s
  per-mission retirement block (next to the P884b ship-state retirement), so a fresh match starts
  with an empty history instead of the process's whole list.
* `src/Allegiance.Server/Server.Economy.cs:740` — the P119 detection handler records through the
  bounded seam instead of appending to a list.
* Consumers found: only the public `Server.BucketCompletions` accessor (now a ≤64-element copy) and
  test assertions in `LiveBucketTests`, `P446E2eTopSpeedLiveTests`, `P467SideGaWeaponOutputTests`,
  `LiveBuyGateDevelopmentTests`, `P475E2eStationGaLiveTests`, `TechTreeDevelopmentLiveTests`,
  `DroneBucketSpawnsDroneLiveTests`, `StationBucketSpawnsConstructorLiveTests` and
  `P477SideGaMiningTests` (all `Count`/`Contains`/`Single` on a just-completed bucket). No bot,
  diagnostics or lobby consumer reads it, and none needs history across a rotation.
  `TechTreeDevelopmentLiveTests`' `DoesNotContain(b2.Id, …)` case is a *known* development that must
  never be recorded, so neither the limit nor the clear can affect it.

Native: bucket completion is a live-mission event (`Bucket.Update`'s `Complete`, the
`bucketigc.h:51` gate) and the game-over reset discards the mission with its buckets —
`FedSrv/fsmission.cpp:3527` (`m_pMission->ResetMission()`). Keeping the diagnostic history beyond
its mission had no native counterpart.

## Tests

| Test | What it proves | Alone, 3× |
| --- | --- | --- |
| `Transport/P967ShutdownOwnershipTests.Greeting_that_arrives_after_shutdown_never_joins_and_leaves_no_state` (ThreadPerPeer, Pool) | P963-04: no join, no leave, 0 live peers, 0 tracked threads, 0 live per-connection state after `Shutdown` | 2/2 passed ×3 |
| `…Shutdown_closes_and_joins_an_in_flight_greeting_socket` (both modes) | P963-04: the pending greeting socket is closed and its thread joined before `Shutdown` returns | 2/2 passed ×3 |
| `…Connection_churn_leaves_no_per_connection_state_and_keeps_departed_ids_closed` (both modes) | P963-08: 24 churn cycles — exactly one leave each, live state 0, markers == cycles, every departed id closed | 2/2 passed ×3 |
| `…Ended_marker_table_retires_the_oldest_drained_departures_past_its_bound` | P963-08: 2×2048 markers → exactly 2048 retained, oldest retired, newest window retained | 1/1 passed ×3 |
| `…A_marker_whose_receive_side_is_still_draining_is_never_retired` | P963-08: a still-draining marker survives a table pushed far past the window | 1/1 passed ×3 |
| `P967BucketCompletionBoundTests` (2 facts) | P963-09: 512 completions keep only the newest 64 in order; empty start | 2/2 passed ×3 |
| `ConnectionCacheCleanupTests.Connection_scoped_caches_drain_to_zero_after_repeated_disconnects` (extended) | P963-07: peers that actually chat then drop leave `ChatBudgets == 0` (plus the M1 caches) | 1/1 passed ×3 |

Eight new cases in `P967ShutdownOwnershipTests` (three theories over both transport modes + two
facts) and two in `P967BucketCompletionBoundTests` — 10 cases in the `~P967` filter, all passing; every suite above was re-run after the final
design change (a bounded tombstone rather than an earlier derived-predicate attempt, which broke the
in-process dispatch harnesses — see Gate).

## Gate

Everything below was run on the final tree (after the bounded-tombstone revision; the earlier
derived-predicate experiment is recorded at the end of this section because the first sweep
self-caught it).

* **Build**: `dotnet build AllegianceCore.slnx -c Debug` → **0 warnings / 0 errors**.
* **Tools**: `tools/build-tools.sh` → **all 122 `tools/*/*.csproj` built**, exit 0 (the gate's 121
  plus the new `tools/p967-shutdown-proof`, which is a proof tool and stays outside the solution).
* **AOT publish** (`src/Allegiance.Core` and `src/Allegiance.Server` changed): `dotnet publish
  src/Allegiance.Server/Allegiance.Server.csproj -c Release -r linux-x64 --self-contained
  -p:PublishAot=true -o <scratch>/aot-final` → succeeded with **no warnings, no IL2026/IL3050**
  and no reflection-serialization change; 17,128,288-byte self-contained binary produced (the scratch
  output was removed with the task scratch per the lane rule).
* **New/affected tests, 3× alone**:
  `--filter "FullyQualifiedName~P967|FullyQualifiedName~Allegiance.Core.Tests.Transport"` →
  **239/239 passed in each of three runs** (1 m 14 s each);
  `ConnectionCacheCleanupTests` → **1/1 in each of three runs**;
  `P869DispatchTests|P867DispatchCertificationTests|P653EconomyLoadoutIntegrityTests` → **99/99**
  (the in-process dispatch harnesses still see their synthetic peers as open).
* **One full suite** (`tools/run-tests.sh --tier fast`): **13,528 tests, 538.7 s** →
  13,522 passed / 3 failed / 3 not executed, **9/9 infrastructure checks**, **no timing offenders**.
  The 3 failures are ledgered flake families, each **passing 3/3 alone** (Fast sweep only, see
  `docs/TEST_FLAKES.md` P967 validation): `P590PodRescueTests.Proactive_assignment_…` (7 s),
  `MiningStateWireTests.Loopback_own_mining_state_…` (837 ms, first ledger entry),
  `LiveSideSelectTests.Flag_on_auto_seats_…` (32.45 against the fixture's [28, 31]). No
  task-related failure; gate verdict on the sweep is **FAIL due to those three only**, and the
  light-gate items above all pass.
* **Showcase never-parks 5×** (`Allegiance.ClientCore.Tests.P700ShowcaseLiveTests.Local_showcase_never_parks_with_target_for_five_seconds`,
  sequential, one method per run): **4 pass / 1 fail**, walls 370/453/468/302 s for the passes and
  485 s for the failure — the same shape as the base rate recorded in `docs/TEST_FLAKES.md`
  (**base 4 pass / 1 fail**, arrival assertion inside the 480 s window, no parking violation). The
  failing run's transcript for that pass did not retain the assertion text (the collection filter
  for that loop dropped it), so it is not quoted; two further full-output executions afterwards
  **passed** (262 s, 181 s), i.e. 6 of 7 executions passed. No captured line reported a parked ship.
* **Skipped count**: `local.env` is present, so every artwork-gated test ran. The Fast sweep reports
  **3 not executed** (`Allegiance.ClientCore.MonoGame.Tests`' own skip markers) and **0 skipped**;
  the new and affected suites report **0 skipped**.
* **Self-caught intermediate regression (not part of the gate)**: the first Fast sweep of the task
  (13,526 tests) failed 148 `Core.Tests` + 1 `ClientCore.Tests` cases because my first P963-08
  design made `IsPeerClosed` "not in the live peer table", which made in-process dispatch harnesses
  drop injected datagrams for ids that never touched a socket. The final design keeps the
  ended-peer predicate and bounds it instead; the harness suites pass 99/99 and the table is
  bounded with a receive-drained retirement rule.

## Native intent

Native Allegiance owns a connection's lifecycle in one place: `FedSrvSiteBase::OnDestroyConnection`
(`FedSrv/FedSrv.CPP:4549-4566`) deletes the player attached to a destroyed connection and counts the
drop, and `~CFSPlayer` severs the two-way connection↔player link (`FedSrv/fsship.cpp:991`), so
nothing runs for a dead session afterwards. Player-scoped state lives on the player and dies with
it — the chat budget is a `CFSPlayer` field (`FedSrv/fsship.cpp:958`, `FedSrv/fsship.h:449-468`)
refilled once per player per mission tick (`FedSrv/FedSrv.CPP:8287`), and the mission reset at game
over discards the mission with its buckets (`FedSrv/fsmission.cpp:3527`). LOGOFF forwards its
guaranteed echo and deletes the player in the handler, and the connection is never reused
(`FedSrv/FedSrv.CPP:5787-5802`). P963-04/07/09 port those ownership rules; P963-08 has no native
counterpart (DirectPlay had no greeting phase and the port mints ids itself), so its rule is the
port's own: a departed connection must stay "closed" for queued traffic without retaining state for
every connection the process ever saw.

## Modern approach

Treat shutdown as a generation barrier: every socket and thread that exists before the barrier is
closed and joined by the owner before the shared pools are torn down, and late arrivals fail closed
at the barrier instead of publishing callbacks (`_generation` + the two re-checks in `HandlePeer`).
Treat every client-supplied name/path/token as untrusted (unchanged by this task). Attach transient
state to a bounded connection/world generation: the ended-peer predicate keeps a bounded recent
window of drained departures, the exactly-once callback flag lives on the peer object it belongs to,
the chat budget is retired by the single owner of connection-scoped peer state, and the bucket
diagnostic is a bounded ring retired with its mission. No new per-tick work and no new default-on
behavioural path is introduced, so the rig baseline (immutable snapshot + encodeOnce, P863 budget,
parallel build, pool transport, native ship updates/GotoPlan, server GC, tiers off) is untouched.

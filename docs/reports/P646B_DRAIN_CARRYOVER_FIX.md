# P646b — Bounded drain carry-over: never drop a deferred event; keep per-peer FIFO across drains

Date: 2026-10-14
Branch: `master` (commit `be3a564b`, amended with suite numbers)
Scope: `src/Allegiance.Server/ServerHost.cs` (`ServerHost.Drain`), tests
(`tests/Allegiance.Core.Tests/Transport/ServerHostBoundedDrainTests.cs`),
`docs/TEST_FLAKES.md`.

---

## Why this task existed

P646 (`5c0f64f0`) bounded `ServerHost.Drain`. Claude's review found two defects
in its carry-over:

1. **Over-fair-share events were silently DROPPED.** In P646's Phase 2, when a
   peer had handled `DrainFairShareEvents` (256) events, its key was removed
   from `order` (`order.RemoveAt(idx)` in the `done >= fairShare` branch) while
   its lane still held events. Phase 3 re-queued only the lanes still listed in
   `order`, so **every event left in a fair-share-exhausted lane was lost**:
   - including that peer's `PeerLeftEvt`, so `ReleasePeer` never ran — its
     ship, `_peerShips` entry and accounting stayed forever;
   - and its `ReceivedEvt`s, whose byte reservations were never released (the
     peer's receive budget leaks; it may be throttled forever).
   `EventsDeferred` undercounted these too. This is exactly the flood case
   P646 was written for (a peer sending >256 events/tick, then disconnecting).
   Reproduced by test: a 500-receive-then-leave peer handled only 64 (dropped).

2. **Deferred events lost per-peer FIFO.** Phase 3 `_events.Enqueue(...)`
   appended deferred events to the **tail** of the shared `ConcurrentQueue`.
   Events from the same peer could already be there (left beyond `pullCap`, or
   arriving from transport threads during the drain), so the older deferred
   events ended up **behind** newer ones — a peer's receive could run after its
   own `PeerLeftEvt`, or out of order with other receives. Reproduced by test:
   a budget-1 carry with a mid-drain arrival yielded `[1,4,2,3]` instead of
   `[1,2,3,4]`.

## Fix

Replace re-enqueue-to-tail with a **sim-thread-owned carry-over**:

- The drain now owns two persistent fields, touched only on the simulation
  thread (transport threads write solely to `_events`/`_bufferedPerPeer`):
  `_pendingLanes` (per-peer `Queue<ITransportEvent>`) and `_pendingOrder` (a
  stable round-robin order that keeps its position across drains; keys are
  added on first sight and removed only when a lane empties).
- Each `Drain` first pulls a bounded slice out of `_events` into the **same**
  lanes, **appended after** anything already carried from a previous drain, so
  per-peer FIFO always holds (a peer's carried events always precede its newer
  arrivals).
- A lane leaves the rotation **only when it is empty** — never when it merely
  hits its fair share — so nothing is ever dropped and a fair-share-exhausted
  flooding peer's `PeerLeftEvt` is still carried and eventually handled, after
  all its receives (lifecycle order join → receives → leave guaranteed).
- The bounds are unchanged: at most `DrainBudgetEvents` (8192) handled per
  drain, at most `DrainFairShareEvents` (256) per peer per drain, and a bounded
  pull (`snapshot + budget`) per drain.
- **Worst-case carry-over growth is bounded by the receive-side byte
  reservation (P315).** A `ReceivedEvt` is enqueued only while its peer's
  reserved in-flight bytes stay under `MaxBufferedBytesPerPeer` (4 MiB), and a
  reservation is released only when the event is handled — so at any instant
  the sum of un-handled `ReceivedEvt` lengths per peer is ≤ 4 MiB. A flooding
  peer's lane can therefore never hold more than `4 MiB / min-frame-length`
  receives (plus its one `PeerLeftEvt`/`PeerJoinedEvt`), however hard it
  floods; past that, `OnReceive` drops the frame at the transport site (as it
  did in P646). Verified live: even a ~26 943-event join-burst window drained
  with `deferred=+0`.
- `EventsDeferred` now counts every carried event (the `[p646]` line is exact).

## Verification

### Unit tests (failing-first against `5c0f64f0`)

| test | pins | failing-first |
|---|---|---|
| `Flooding_peer_send_then_leave_every_receive_handled_once_in_order_then_released` | 500 receives then leave → every receive handled exactly once in order, then the leave; accounting removed; no reservation leak | **red** (old code handled 64/500, dropped the rest incl. the leave) |
| `Carried_events_keep_fifo_ahead_of_mid_drain_arrivals` | budget-1 carry + a mid-drain arrival → order `[1,2,3,4]` | **red** (old code → `[1,4,2,3]`, reordered) |
| `SelfReplenishing_flood_is_bounded_and_returns_within_budget` | flood → Drain returns ≤ budget, rest deferred | green (guard) |
| `Flooding_peer_does_not_starve_a_normal_peer` | round-robin reaches the normal peer in ≤2 drains | green (guard) |
| `Drain_release_never_recreates_a_departed_peers_accounting_entry` | release after entry gone does not re-insert | green (guard) |
| `Churn_of_1000_peers_leaves_zero_retained_accounting_entries` | 1 000 receive/leave churn → 0 entries | green (guard) |

Confirmed: the two new tests failed on the pristine tree and pass after; all six
are green on the fix.

### Whole-solution build

`dotnet build -m:8 -v:m` (Debug, working tree): **0 warnings, 0 errors**.
Release build in a temp worktree: **0 warnings, 0 errors**.

### Suite — `scripts/run-tests.sh`, 2 runs on the final code (P471 rule)

- **Run 1:** Lobby 12/12 · MonoGame 1968/1972 (3 skipped, 1 flake) · ClientCore
  749/749 · Core 4024/4027 (3 flakes). Failures: `ChatTargetComposeLiveTests.
  ComposeDefault_Team_…` (ledger 469), `P590PodRescueTests.Pod_pickup_order_…`
  (435), `GameOverPlayersWireTests.Win_by_elimination_…` (432), `AddPartTests.
  Part_pickup_decline_…` (247).
- **Run 2:** Lobby 12/12 · MonoGame 1969/1972 (3 skipped, clean) · ClientCore
  749/749 · Core 4024/4027 (3 flakes). Failures: `ShipIdAllocationLiveTests.
  Valid_logon_advances_nextShipId` (**new — added to ledger**),
  `LiveCombatWireTests.Moving_target_streams_…` (406), `AddPartTests.
  Part_pickup_destroys_then_accept_…` (247).

Every failure passed **3/3 in isolation** (ChatTargetCompose 3/3, P590PodRescue
3/3, GameOverPlayers 2/3 intermittent, AddPart 3/3, ShipIdAllocation 3/3,
LiveCombatWire 3/3) — all established live-wire load-flakes, none in code
touched by P646b. Ledger updated (`docs/TEST_FLAKES.md` P646b section).

### Live proof (own temp worktrees `/tmp/p646b-proof` (fix) and `/tmp/p646b-parent` (P646), local servers on 127.0.0.1, m2xspiderw, showcase, target Alderan)

**Mandatory live check (transport changed): bots reach target AND fight.**

- **30v30 (in-process P632 harness, port model, 120 s):** `botsReached=60/60`
  (all bots crossed into Alderan), **`killsTotal=25`** (A:10, B:15) within
  ~120 s, hitRate 63.9 %, maxTargeted 57, maxVisibleEnemies 30. (Trace:
  `docs/reports/P646B_assets/p632_30v30_p646b.txt`.)
- **195v195 (showcase `size_scale 13`, 391 peers, march + staging):** the
  `[p646]` health line read **`deferred=+0` in every one of the 67 windows** —
  including the ~26 943-event join-burst windows — `wouldBlockCatches=+0`, at
  391 peers (41 parent windows also `deferred=+0`). Normal play never defers,
  so the carry-over path adds zero work.
- **200v200 tick p95 no worse than P646** — proven by apples-to-apples A/B on
  the **same box under identical load**:

| metric (steady-state, ticks≥98) | P646b (fix) | P646 (parent `5c0f64f0`) |
|---|---|---|
| tick-wall mean (median) | 36.42 ms | 36.31 ms |
| tick-wall **p95** (median) | **44.25 ms** | **45.48 ms** |
| tick-wall max (median) | 51.80 ms | 53.45 ms |
| ticks/5 s | 100–102 (20 Hz, no stall) | 100–101 |
| `[p646]` nonzero-deferred windows | **0** | **0** |

The P646 report's 32/37/42 ms figure was taken in a quieter session; on this box
today both commits sit around p95 ~44–45 ms. P646b's p95 is statistically
identical to (marginally tighter than) P646's — **no regression**, as expected
since the fix is a no-op at zero deferral. Server logs preserved under
`docs/reports/P646B_assets/` (`server-195v195-fix.out`, `server-195v195-parent.out`).

All local server/bot/MCP processes were stopped by exact PID; temp worktrees and
ports cleaned up.

## Native behaviour classified

| Behaviour | Native cite | Class | What the port does |
|---|---|---|---|
| Inbound drain | `FedMessaging::ReceiveMessages()` (`_Utility/Messages.cpp:887`) drains the whole `m_msgList` in queue order on the single-owner thread — no drops, no fair share, per-peer order trivial | **era constraint** (single-threaded modem-era drain) | Kept the single-owner sim thread; P646 added the per-tick budget + per-peer fair share so a flood cannot defer simulation. P646b makes the bounded drain **drop-free and order-preserving** (per-peer carry-over lanes) so it reproduces native's invariants: nothing dropped, per-peer FIFO, join → receives → leave. |
| Lifecycle ordering | the sim thread processes each peer's messages in arrival order (Messages.cpp:887–950) | design | Guaranteed by the per-peer lane: `PeerLeftEvt` sits behind that peer's receives and is handled after them, even for a flooding peer. |
| Byte accounting | per-connection DirectPlay buffers, drained every frame | era constraint | P315 per-peer byte reservation bounds in-flight bytes (this is what bounds the carry-over worst case); P646 never-insert release; P646b guarantees the lane's `PeerLeftEvt` still runs `ReleasePeer → RemovePeerAccounting`. |

## Modern alternative considered

- **A drain scheduler with per-peer queues.** P646 already moved to per-peer
  lanes serviced round-robin — a small allocation-light scheduler. P646b makes
  that scheduler *stateful* (the drain thread owns the lanes across ticks) so a
  bounded drain can still honour per-peer FIFO and lifecycle ordering without
  ever dropping. The fully lock-free MPSC-with-fairness alternative was
  considered; the current design keeps the single-owner sim thread and the
  existing byte-budget accounting and is the smallest correct change.
- **Crediting/flow-control instead of drop** (tell a flooding peer to back off):
  out of scope; P315's byte-budget drop at the transport site already bounds
  the flood and remains the safety valve.

## Deliverables

- Commit on `master` (`be3a564b`, amended with suite numbers), not pushed.
  `git branch --show-current` = `master`.
- Report: `docs/reports/P646B_DRAIN_CARRYOVER_FIX.md`.
- Flake ledger updated: `docs/TEST_FLAKES.md` P646b section.
- Evidence: `docs/reports/P646B_assets/` (30v30 trace + both 195v195 server logs).

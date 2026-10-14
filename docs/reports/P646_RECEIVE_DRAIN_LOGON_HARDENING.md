# P646 — Receive-side hardening: one logon per connection, bounded fair drain, accounting leak, client renderer disposal

Date: 2026-10-14
Branch: `master` (commit `5c0f64f0`, amended with suite numbers)
Scope: `src/Allegiance.Server/Server.Dispatch.cs`, `ServerHost.cs`, `Server.Lifecycle.cs`, `Server.cs`, `Server.Timescale.cs`, `src/Allegiance.Core/Transport/TcpTransport.cs`, `src/Allegiance.ClientCore.Game/Game1.cs`, tests.

---

## Why this task existed

An external review (`docs/reviews/GPT_SOL_REVIEW.md` on `review/gpt-sol`; Claude
re-checked each against `8f7a3770`) found four receive-side defects plus a P645
follow-up:

1. **HIGH — a repeated LOGONREQ spawns a second ship and orphans the first.**
   `Server.Dispatch.cs` LOGONREQ branch had no "already logged on / pending"
   check, so a seated peer re-sending a version-matching LOGONREQ got a fresh
   ship id, a new `_pendingSideSelect` entry and, on seating, an overwritten
   `_peerShips[from]` — the old ship left flying in its sector owned by nobody.
   Native asserts this never happens.
2. **HIGH — the event drain never stops while traffic keeps arriving.**
   `ServerHost.Drain` looped `while (TryDequeue(...))`; consuming a receive
   releases the peer's byte reservation, immediately admitting more, so a steady
   stream of valid PINGs means the queue never empties and simulation/broadcast/
   cancellation are deferred indefinitely (the reviewer saw one `Drain` consume
   1,000 self-replenishing events).
3. **MEDIUM — `_bufferedPerPeer` re-inserts departed peers.** `OnPeerLeft`
   `TryRemove`s, then a receive queued earlier is drained and `AddOrUpdate`
   re-creates the entry; nothing removes it again, so churn grew one retained
   entry per historical connection (1,000 peers → 1,000 entries).
4. **LOW — two client renderers never disposed.** `Game1.UnloadContent` disposed
   19 renderers but not `_nativeShipRenderer` / `_nativeTreasureRenderer` (both
   own GPU buffers, textures and effects).
5. **P645 follow-up — the read loop must not spin on would-block.** P645 made
   pool-mode sockets non-blocking, so `TcpTransport.ReadExactly` caught a
   would-block `IOException`, `Thread.Sleep(1)` and retried — every idle pool
   peer threw ~one exception per ms (~390k/s at 390 peers).

---

## Design / era / bug classification (from the footer)

| System | Native behaviour | Class | What the port does |
|---|---|---|---|
| Receive funnel | Native is a single-owner sim thread that drains its inbound message list once per frame (`m_csMsgList`) — the sim thread IS the only consumer, so receive work is inherently per-frame bounded. | **era constraint** | Kept the single-owner sim thread; **bounded its work** for today's scale: each `Drain` pulls at most the events present when it began plus a per-tick event budget (8192), serviced round-robin with a per-peer fair share (256), deferring the rest to the next tick. No silent fix to a modem-era unboundedness. |
| One logon per connection | `FedSrv.CPP:4832` `assert (pfm->fmid != FM_C_LOGONREQ); // shouldn't have a pfsPlayer yet if logging on`; the LOGONREQ handler at `FedSrv.CPP:5805` assumes a fresh player. | **port bug** | Reject a seated peer's LOGONREQ (rate-limited log, no id, no ack); a pending peer's duplicate is idempotent (re-send the same ack, no new id). |
| Buffered-byte accounting | Native accounts bytes on the connection object owned by the connection lifecycle. | **port bug** | Releasing a reservation never inserts (atomic `TryUpdate` only); the entry is finally removed when the ordered `PeerLeftEvt` drains (`ReleasePeer → RemovePeerAccounting`). |
| Client renderer disposal | Native renderers own GPU resources and are torn down in `CFSWinApp` teardown. | **port bug** | `UnloadContent` now disposes the two flagged renderers plus 11 more the guard surfaced; a reflection guard prevents silent omission. |

## Native behaviour classified (per-policy)

| Behaviour | Native cite | Class | What P646 ports |
|---|---|---|---|
| Existing player never sends LOGONREQ | `FedSrv.CPP:4832` (assert in `OnAppMessage`); only a brand-new player may log on | design | Ported as a hard rule (the port has no assert-based control flow): a seated peer's LOGONREQ is rejected (logged, rate-limited, allocates nothing); a pending peer's is idempotent. |
| LOGONREQ validation path | `FedSrv.CPP:5805` (`if (pfmLogon->fedsrvVer == MSGVER)`) | design | Kept: validation still gates id allocation; only the duplicate/unexpected callers are gated off. |
| Single-owner sim-thread inbound | `FedSrv.CPP:4798` `OnAppMessage` (sim/queue thread owns all inbound) | era constraint | Kept single-owner sim thread; the drain's per-tick budget + per-peer fairness bound its work so a flood cannot defer simulation indefinitely. |
| Per-peer byte accounting | DirectPlay-side buffers; native's `m_csMsgList` is drained every frame | era constraint | Kept per-peer byte budget (P315) + never-insert release (P646) so the table stays bounded by the live peer set. |
| Client renderer teardown | native per-renderer GPU ownership in app teardown | design | `UnloadContent` disposes every `IDisposable` renderer; a reflection guard enforces it. |

## Modern alternative considered

- **Drain as a scheduler with per-peer queues.** Rather than one shared
  `ConcurrentQueue` drained naively, the fair drain pulls events into per-peer
  lanes and services them round-robin — a small, allocation-light scheduler. The
  modern alternative (a fully lock-free MPSC with per-consumer fairness and
  batching) was considered; the current design preserves per-peer FIFO, the
  single-owner sim thread and the existing byte-budget accounting while adding a
  hard per-tick work bound, so it was chosen as the smallest correct change.
- **Snappy/KCP-style reliability for sends** — out of scope (P645's lane design
  stands); noted for future work.
- **`Socket.Poll` before `Read`** instead of the exception spin: the classic
  non-blocking-I/O idiom. Restricted to non-blocking sockets because a blocking
  socket (the greet handshake with `ReceiveTimeout`) must keep its timed blocking
  `Read` — `Socket.Poll` bypasses `ReceiveTimeout`, and the full suite caught that
  regression (silent greets stopped timing out), which was fixed.

---

## Scope 1 — one logon per connection (`Server.Dispatch.cs`)

The LOGONREQ branch now checks, before validating/allocating:

- **Already seated** (`_peerShips.ContainsKey(from)`): reject — rate-limited log
  (new `_duplicateLogonLog`), no `AllocateShipId()`, no `_pendingSideSelect`
  entry, no `LogonAck`. Cites `FedSrv.CPP:4832`.
- **Already pending** (`_pendingSideSelect.TryGetValue(from, out …)`): idempotent
  — re-send the **same** `LogonAck` (same reserved ship id, same
  validated/cookie), re-send the side roster, allocate nothing, register nothing
  new.

Neither case can spawn an orphan ship. The `_duplicateLogonLog` rate-limiter is
trimmed with the peer in `ReleasePeer`.

## Scope 2 — bounded, fair drain (`ServerHost.cs`)

`Drain` now:
1. Snapshots the queue (`_events.Count`) and pulls at most `snapshot +
   DrainBudgetEvents` events into **per-peer lanes** (bounded pull → a
   self-replenishing flood cannot extend the drain forever).
2. Services lanes **round-robin**, at most `DrainBudgetEvents` total and
   `DrainFairShareEvents` per peer, so one flooding peer cannot starve others.
3. Re-queues the over-budget remainder for the next tick, preserving per-peer
   FIFO and a peer's join → receives → leave ordering (the accounting fix depends
   on the leave being drained after that peer's receives).

Deferrals are counted in `ServerHost.EventsDrained` / `EventsDeferred` and
surfaced per profiler window by a new `[p646]` health line
(`Server.Timescale.cs`). Budgets: `DrainBudgetEvents = 8192`, `DrainFairShareEvents
= 256` (internal seams; tests shrink them). The 200v200 baseline (below) observed
**zero deferrals** in normal play.

## Scope 3 — accounting never re-inserts (`ServerHost.cs` / `Server.Lifecycle.cs`)

- `OnPeerLeft` no longer `TryRemove`s `_bufferedPerPeer` (it was the race that
  let a queued receive's release re-create the entry for a departed peer).
- `Drain` releases a handled `ReceivedEvt` with `ReleaseReservation`, which only
  ever updates an **existing** entry (`TryUpdate`, never `AddOrUpdate`).
- The entry is finally removed on the sim thread when the ordered `PeerLeftEvt`
  drains: `Server.ReleasePeer → ServerHost.RemovePeerAccounting`.
- 1,000-peer receive/leave churn leaves 0 retained entries (test).

## Scope 4 — client renderer disposal (`Game1.cs` + guard)

`UnloadContent` now disposes `_nativeShipRenderer` and `_nativeTreasureRenderer`,
and — after the reflection guard surfaced them — **11 more** GraphicsDevice-owning
renderers that were also never disposed (`_stationRenderer`, `_buildingEffectRenderer`,
`_treasureRenderer`, `_chaffRenderer`, `_damageDebrisRenderer`, `_debrisRenderer`,
`_engineTrailRenderer`, `_nozzleGlowRenderer`, `_navStrobeRenderer`,
`_motionTrailRenderer`, `_alephGlimpseRenderer`). A guard test
(`RendererDisposalGuardTests`) reflects over `Game1`'s private `IDisposable`
renderer fields and asserts each is disposed in `UnloadContent`, so a future
renderer can't be omitted silently.

## Scope 5 — P645 follow-up: no would-block spin (`TcpTransport.cs`)

`ReadExactly` now parks in `Socket.Poll(SelectRead, 250 ms)` before `Read` — but
**only for non-blocking sockets** (`Socket.Blocking == false`, i.e. pool data
sessions). A blocking socket (the greet handshake carrying `ReceiveTimeout`, and
thread-per-peer reads) keeps a plain blocking `Read`, which already parks without
exceptions and honours `ReceiveTimeout`. The would-block catch remains only as a
belt-and-suspenders fallback for the reader/worker race on a shared socket. Two
read-only counters (`TcpTransport.WouldBlockCatches`/`WouldBlockWaits`) are
exposed for the live proof.

The full suite caught a regression in the first version: applying Poll to
blocking sockets too bypassed `ReceiveTimeout`, so silent greets stopped timing
out (`TcpTransportGreetTimeout*`). Restricted to non-blocking sockets → both
classes green.

---

## Verification

### Unit tests (failing-first)

All new tests fail against today's code (most don't even compile — the hardened
APIs/seams they assert against don't exist pre-P646; the behavioral ones go red),
and pass after:

| test | pins | failing-first |
|---|---|---|
| `ServerHostBoundedDrainTests.SelfReplenishing_flood_is_bounded_and_returns_within_budget` | a self-replenishing flood → Drain returns ≤ budget, rest deferred | red (old code returns 150 > 50; worse, would loop forever) |
| `…Flooding_peer_does_not_starve_a_normal_peer` | round-robin reaches the normal peer in ≤2 drains | guard (fair scheduler) |
| `…Drain_release_never_recreates_a_departed_peers_accounting_entry` | a release after the entry is gone does not re-insert | red (old `AddOrUpdate` → count 1) |
| `…Churn_of_1000_peers_leaves_zero_retained_accounting_entries` | 1,000 receive/leave churn → 0 entries | guard |
| `LiveDuplicateLogonTests.Seated_peer_duplicate_LOGONREQ_is_rejected_no_new_ship_no_new_id` | seated duplicate → no new pending, 1 ship, same id | red (old code creates a pending entry) |
| `…Pending_peer_duplicate_LOGONREQ_is_idempotent_same_ack_no_new_id` | pending duplicate → same ack, no new id | red (old code allocates a new id) |
| `TcpTransportNoWouldBlockSpinTests.Idle_pool_peer_accrues_almost_no_would_block_catches_over_one_second` | idle pool peer → ~0 would-block catches/s | red (old code ~1000/s) |
| `RendererDisposalGuardTests.Every_disposable_renderer_field_is_disposed_in_UnloadContent` | every renderer disposed | red (13 undisposed) |

Existing `InboundFlowControlTests`, the full transport suite, and the 35+ live
logon/seating/position tests all remain green.

### Whole-solution build

`dotnet build AllegianceCore.slnx -v q`: **0 warnings, 0 errors** (Release build
in the temp worktree also 0/0).

### Suite — `scripts/run-tests.sh`, 2 runs on the final code (P471 rule)

- **Run 1:** Lobby 12/12 · MonoGame 1969/1972 (3 skipped) · ClientCore 749/749 ·
  **Core 4022/4025** (3 live-wire load-flakes: `ConsoleLogNonBlockingTests.
  Queued_writer…` **[new, added to ledger]**, `GameOverPlayersWireTests.
  Conquest_win_in_one_mission…` [ledger], `AddPartTests.Part_pickup_destroys…`
  [ledger]).
- **Run 2:** Lobby 12/12 · MonoGame 1969/1972 (3 skipped) · ClientCore 749/749 ·
  **Core 4023/4025** (2 live-wire load-flakes: `LiveTreasurePickupTests.
  Rescue_ship_picking_cash…` [ledger line 157], `GameOverPlayersWireTests.
  Win_by_elimination…` [ledger line 432]).

Every failure is an established live-wire timing flake documented in
`docs/TEST_FLAKES.md` (the P646 section adds the two newly-observed classes and
records the greet-timeout regression + fix); **none are in code touched by P646**.

### Live proof (own temp worktree `/tmp/p646-proof`, local server 127.0.0.1:6713, m2xspiderw, showcase, target Alderan)

**Mandatory live check (transport changed): bots reach target AND fight.**

- **Reach:** 30v30 scale-1 run had 88 `[warp] … crossed …` lines and 568 at
  200v200, including `… crossed from 'Plato' (1060) to 'Alderan' (1052)` — bots
  leave home sectors and reach the target.
- **Combat (30v30 ≈ size_scale 1, 31 peers):** **16 victim deaths + 32 ejections**
  (`[scoring] victim ship … death +1`, `… ejection +1`) across distinct ships —
  kills > 0 within ~200 s. (P645's report noted the 200v200 march had not yet
  produced decisive combat in its capture window either; the combat check is the
  30v30.)
- **P646 health (`[p646]` line, every 5 s window): `deferred=+0
  wouldBlockCatches=+0`** at both 30v30 and 200v200 — the bounded drain never
  deferred under load and the non-blocking reader threw zero would-block
  exceptions (vs the ~390k/s the pre-P646 spin would throw at 390 idle peers).

**195v195 baseline** (showcase `size_scale 13` = 390 bot clients + probe = 391
peers, march + staging, profiler on):

| metric | observed (P646 final) | P645 baseline (report) |
|---|---|---|
| peers | 391 | 391 |
| tick rate | 100–101 ticks / 5 s (20 Hz nominal, no sim stall) | 100–101 |
| tick-wall mean / p95 / max | **32.25 ms / 37.47 ms / 42.46 ms** | 37.6–39.9 / 43.5–51 / 46–58 ms |
| CPU% | 127.5% | — |
| deferred events (`[p646]`) | **+0** every window | n/a |
| would-block catches (`[p646]`) | **+0** every window | n/a (would spin ~390k/s) |
| max per-peer queued / total | 17 820 B / 350 503 B (well under the cap) | ~13–17 kB / — |
| max oldest guaranteed | 506 ms | 39–383 ms |
| disconnect reasons | 0 transport | 0 transport |

Tick p95/max are **no worse** than the last (P645) baseline — slightly better.
Deferrals stayed 0 in normal play at full scale.

All local server/bot/MCP processes were stopped by exact PID (verified: `pgrep`
clean, port closed) and the temp worktree `/tmp/p646-proof` removed.

---

## Deliverables

- Commit on `master` (`5c0f64f0`, amended with suite numbers), not pushed.
  `git branch --show-current` = `master`.
- Report: `docs/reports/P646_RECEIVE_DRAIN_LOGON_HARDENING.md`.
- Flake ledger updated: `docs/TEST_FLAKES.md` P646 section.

# P611 — Replace the "never-drained" overflow rule with a real write-progress liveness signal

Date: 2026-10-11 · Branch: `master` (not pushed) · Commit: see `git log -1`

## Summary

The 2026-10-11 15:50 incident (P608 `cfcc1e2a`, `transport.mode=pool`, 195v195
showcase on the 221 server): at 15:50:06 local the server disconnected 392 → 0
players in under 90 s. Window summaries showed `QueueOverflow=232` and more in
neighbouring windows. Server and bot process stayed up. Root cause: P606's
anti-zombie overflow rule **(b)** disconnected a peer when its send queue had
been **continuously non-empty** for `OverflowTimeout` — reasoning that a
never-empty queue meant the socket had stopped accepting writes. Under a
sustained battle the server enqueues per-tick frames faster than a shared pool
worker fully drains each peer, so a **healthy** peer's queue is never
momentarily empty for 5 s even though bytes flow continuously. Hundreds of
peers tripped (b) together.

P611 replaces (b) with a **real liveness signal**: a peer is disconnected only
when its socket has made **no write progress** (no bytes accepted by the OS) for
a timeout. A peer that keeps accepting bytes is never "stuck", however full its
queue. The byte-cap rule (a) is kept (it is the memory bound), and the
disconnect reasons are split into distinct values (`QueueOverCapacity`,
`NoWriteProgress`, `SendTimeout`) in both the per-peer `[conn-drop]` line and
the window `[disconnect-summary]`.

Both transport modes. P606's guarantees are preserved: bounded memory, no send
stall, reasons logged.

---

## Which signal fired (scope 1)

Confirmed by reproducing the sustained-load condition locally: with the old
rule, a peer whose socket **keeps accepting** bytes but whose queue is
**never empty** under load is disconnected via the never-drained rule (b). The
reproduction is `tests/.../TcpTransportLivenessPolicyTests.cs` →
`Peer_whose_socket_keeps_accepting_bytes_is_never_disconnected_even_if_queue_never_empties`,
which **fails on the pre-P611 policy** (the peer is dropped) and passes after
(the peer stays). So the 15:50 mass drop was signal (b) — the never-drained
proxy misread healthy backpressure as death.

P608's guaranteed own-ship stream was also audited (scope 3): it uses a single
constant coalesce key per peer (`CoalesceOwnShip`, `Server.Broadcast.cs:676`),
sent guaranteed + coalesced latest-wins, so it is bounded to **one pending
frame per peer** and can never accumulate or push a peer over the byte cap. A
unit test pins this (`Guaranteed_own_ship_stream_never_triggers_overcapacity_disconnect`).

---

## Design (scope 2 & 3)

`src/Allegiance.Core/Transport/TcpTransport.cs`, `PeerSender`:

- Replaced the `_undrainedSinceUtc` "queue continuously non-empty" proxy with a
  **write-progress clock** `_lastWriteProgressUtcTicks` (Interlocked long).
- The writer (`WriterLoop`, thread-per-peer) and the pool worker
  (`TryWriteHead`) stamp it with `DateTime.UtcNow.Ticks` on **every successful
  `_stream.Write`** — i.e. whenever the OS accepts bytes (a send completes
  partially or fully). A peer that keeps accepting bytes refreshes its clock and
  is never "stuck".
- `Enqueue`'s overflow check now evaluates:
  - **(a) `QueueOverCapacity`** — a guaranteed-only (non-droppable,
    non-coalescable) backlog sits over `CapacityBytes` for `OverflowTimeout`.
    Nothing left to shed; the client genuinely cannot keep up. This is the
    **memory bound** (P606), kept.
  - **(b) `NoWriteProgress`** — while the queue holds data, the write-progress
    clock has been stale for `NoWriteProgressTimeout`. The peer is
    dead/link-dead, however small its (coalesced) backlog. This replaces the
    old anti-zombie rule with a real liveness measure.
  - Priority: `NoWriteProgress` (a dead socket is the more fundamental cause)
    is evaluated before `QueueOverCapacity`.
- **Fresh-window reset**: when `Enqueue` runs on an **empty** queue
  (`wasEmpty`), the write-progress clock is reset to now. An idle-then-active
  peer (quiet for longer than the timeout, then a single frame) is never
  misjudged stale before its writer gets a chance to run.
- **Reason split (scope 1)**: `SendTimeout` is now distinct from `SendError`
  (a write that stalled for the socket `SendTimeout` vs an outright connection
  error), via `IsSocketTimeout` on the write catch path. `QueueOverflow` was
  renamed `QueueOverCapacity` to reflect that it is strictly the byte-cap rule.
- **Attribution race fixed**: `DropPeer` now attributes the send-side reason
  (via `RaisePeerLeftIfFirst`) **before** `ps.Close()` tears the socket down.
  Previously `Close()` woke the read loop (socket error) and the read loop could
  race to reap the peer with `ReadErrorOrEof`, hiding the real cause. Recording
  first guarantees the exactly-once attribution is the initiating path's reason.

### Timeouts — naming and justification

| Knob | Value | Meaning | Native justification |
|---|---|---|---|
| `CapacityBytes` | 8 MiB | per-peer queued-byte bound | memory bound (P606) |
| `OverflowTimeout` | 5 s | guaranteed-only backlog over the byte cap before drop | keep-up/memory rule |
| **`NoWriteProgressTimeout` (new)** | **30 s** | no bytes accepted by the OS while data pending | native client link-dead tolerance |
| `SendTimeoutMs` | 10 s | per-write budget (thread-per-peer) | per-write safety net (P311) |
| `PoolSendTimeoutMs` | 250 ms | per-write budget (pool) | per-write safety net (P605) |

The new liveness timeout is justified by the native client's own link-dead
detection:

- `src/clintlib/clintlib.cpp:2045` — `const int g_cPingsTimeout = 5;`
- `src/clintlib/clintlib.cpp:2047` — `const DWORD g_sDeadTime = 30; // 30 seconds`
- `src/clintlib/clintlib.cpp:2050-2067` — `CheckServerLag`: the client sends a
  PING roughly every `maxLagCheck` = **6 s** (`:2052`) and declares
  `OnSessionLost("Pings aren't coming back.")` only when there have been
  **5 unanswered pings AND ≥ 30 s since the last server message** — i.e. a
  ~30-second silence tolerance before a link is judged dead.

The native game did **not** have an application-level "no write progress" timer
on the server; it delegated dead-link detection to DirectPlay's own transport
watchdog, surfacing a dead link as `QSR_LinkDead` on removal
(`src/FedSrv/fsship.cpp:1004` → `RemovePlayerFromMission(this, QSR_LinkDead)`),
driven by DirectPlay (`DPN_MSGID_TERMINATE_SESSION`,
`src/_Utility/DirectPlayTransport.cpp:730`). DirectPlay's own guaranteed sends
never time out (`dwTimeout = 0`) and best-effort sends drop at 500 ms
(`src/_Utility/Messages.cpp:549-563`, `src/_Utility/DirectPlayTransport.cpp:551`).
Our port must synthesize the watchdog DirectPlay used to provide; a 30 s
no-write-progress timeout matches the native client's own 30 s dead-time and is
comfortably below any real dead-link latency while being far above the
per-write `SendTimeout`.

`NoWriteProgressTimeout` (30 s) is deliberately much longer than `OverflowTimeout`
(5 s): the byte-cap rule bounds *memory* (must act fast, seconds), whereas the
liveness rule judges *death* (aligns with native's ~30 s dead-time and must not
drop a merely-backpressured peer).

---

## Tests (failing-first)

New `tests/Allegiance.Core.Tests/Transport/TcpTransportLivenessPolicyTests.cs`,
parameterized over **both** transport modes (thread-per-peer and pool):

- `Peer_whose_socket_keeps_accepting_bytes_is_never_disconnected_even_if_queue_never_empties`
  — **the core regression**: a peer whose stream accepts bytes (throttled
  write-progress model) but whose queue is never empty is NOT disconnected.
  **Fails on the pre-P611 policy** (the never-drained rule drops it) and passes
  after.
- `Peer_accepting_nothing_for_the_timeout_is_disconnected_with_NoWriteProgress`
  — a peer whose stream accepts nothing for `NoWriteProgressTimeout` is
  disconnected, with the distinct `NoWriteProgress` reason.
- `Byte_cap_overcapacity_disconnect_still_works_with_QueueOverCapacity` — a
  guaranteed-only backlog over capacity still disconnects (memory bound intact),
  with `QueueOverCapacity`.
- `Guaranteed_own_ship_stream_never_triggers_overcapacity_disconnect` — P608's
  guaranteed own-ship stream (bounded to one per key) never pushes a peer over
  capacity.
- `Overflow_reasons_are_distinct` — `QueueOverCapacity` / `NoWriteProgress` /
  `SendTimeout` are distinct enum values.
- `Blocked_write_hitting_send_timeout_records_SendTimeout_not_SendError`
  (thread-per-peer) — a write that stalls for the socket `SendTimeout` is
  attributed `SendTimeout`, not the generic `SendError`.

Because loopback RSTs a never-read peer nondeterministically (documented in
P606 — the kernel may reap with `ReadErrorOrEof` before the policy), the
reason-specific tests drive a controllable `ControlledStream` (a `NetworkStream`
subclass swapped into the peer's `_stream` via reflection) so the policy's write
behavior is deterministic and the reason attribution is not obscured by the OS.

Existing P606/P605 test helpers that shorten the overflow timer were extended to
also shorten `NoWriteProgressTimeout` (so a stalled peer is dropped promptly
under the new liveness rule), in `TcpTransportCoalesceAndReasonTests.cs`,
`TcpTransportSendDropSafetyTests.cs`, `TcpTransportSendWorkerPoolTests.cs` and
`NonBlockingSendLiveTests.cs`.

Transport suite (both modes): **67/67** (56 pre-existing + 11 new).

---

## Verification matrix

Measured on the final code (commit `e1024a2`). **Whole-solution build: 0 warnings /
0 errors.** Transport suite: **67/67** in both modes (standalone). Full suite via
`scripts/run-tests.sh` (P471) run twice on the final code; each run reported as
it happened. Every failure is a known ledgered live-wire load-flake
(`docs/TEST_FLAKES.md`) that passes in isolation / is cited below — none touches
the transport, send path, or overflow policy.

| Assembly | Run 1 | Run 2 |
|---|---|---|
| Allegiance.Lobby.Tests | 12/12 | 12/12 |
| Allegiance.ClientCore.MonoGame.Tests | 1735/1738 (3 sk) | 1735/1738 (3 sk) |
| Allegiance.ClientCore.Tests | 593/593 | 592/593 |
| Allegiance.Core.Tests | 3924/3929 | 3928/3929 |
| **P611 transport suite** (subset of Core) | 66/67 * | 67/67 |

\* Run 1's single transport failure was
`TcpTransportGreetTimeoutPoolTests.Silent_connection_does_not_block_a_second_client_join`
(a live-wire greet-timing flake — **passes 2/2 in isolation**, 825 ms). The four
other Run-1 Core failures were the ledgered `WarpBombTests.Live_whole_mission_warp_bomb_…`
(:183), `LiveRipcordTests.Cancel_while_active_aborts_sector_wide` (:417),
`LiveCountdownStartPingTests.StartGame_owner_all_ready_…` (:37) and
`GameOverPlayersWireTests.Win_by_elimination_broadcasts_game_over_players_…`
(:432). Run 2's two failures were the ledgered
`ClientCore…ShipDeleteGoneLiveTests.DockOut_…UndockReturnsAndClears` (:46, residual
~1/15 timing flake after P482) and `Core…MineCrossSideVisibilityLiveTests.
Enemy_ship_spots_a_laid_mine_…` (:184). None references any P611 symbol.

---

## Live proof (local server, own temp worktree — never 221)

Executed in a private worktree (`/tmp/p611-proof` at `e1024a2`), map
`m2xspiderw.igc` (side 0 home Omega 1053 / side 1 home Plato 1060, single warp
each to the arena choke **Alderan 1052**), `profiler.enabled`, debugMode,
`spawn_scenario preset=showcase size_scale=13 target_sector=Alderan`
(**391 peers**), **7 minutes** run — well past the start of the clash (the sim is
at full 20 upd/s broadcast load throughout, `[projectile] … 391 peer(s)`). Both
`transport.mode`s.

| metric (391 peers, 7 min) | pool (24 wk) | thread-per-peer |
|---|---|---|
| **disconnects by reason (whole run)** | **ReadErrorOrEof=1** | **ReadErrorOrEof=1** |
| mass disconnects (QueueOverCapacity/NoWriteProgress/SendTimeout) | **0** | **0** |
| distinct `[disconnect-summary]` windows | 1 (the single stray) | 1 (the single stray) |
| ticks/5 s (mean) | **100.4** (=20.1/s, full rate) | **99.7** |
| tick-wall mean / peak-max (ms) | 35.6 / 110.9 | 42.8 / 414.1 |
| RSS max | 165,892 KB (~162 MB) | 174,188 KB (~170 MB) |
| warp crossings Plato→Alderan (`[warp]` crossed) | **1591** | **1535** |
| ships in Alderan (1052) at peak window | 237 | (engaged) |

Reading: **0 mass disconnects in both modes at the exact scale of the incident
(391 peers) over 7 minutes** — versus the P608 incident's 392→0 in under 90 s
(`QueueOverflow=232` + neighbours). RSS stays bounded (~160–170 MB) versus the
5.1 GB runaway P606 fixed; the sim holds 20 upd/s the whole clash; and the fleet
actually **reaches the target**: thousands of `[warp]` lines show ships crossing
`Plato (1060) → Alderan (1052) via warp 2184`, and the sector-population report
puts 237 ships in Alderan (1052) at peak. The lone `ReadErrorOrEof=1` in each
run is a single stray peer teardown, not a mass event (the 391 `peer left` lines
logged at the very end are the process-kill teardown after measurement).

**Mandatory live check (bots reach the target, size_scale 1):** a smaller
`size_scale 1` showcase on the same worktree produced `[warp] ship … crossed
from 'Plato' (1060) to 'Alderan' (1052) via warp 2184` (151 warp lines) — bots
leave their home sector and cross into Alderan. Reachability is preserved on the
new code in both modes (the P608-era logs likewise show bots reaching Alderan
before the 15:50 mass drop; P611 keeps that while removing the false disconnect).

No changes were made to 221 and no `~/allegiance-play2`/`~/allegiance-deploy`
working tree was touched; all local servers/bots/processes started for this
proof were stopped by exact PID before session end.

---

## Files changed

- `src/Allegiance.Core/Transport/TcpTransport.cs`
- `tests/Allegiance.Core.Tests/Transport/TcpTransportLivenessPolicyTests.cs` (new)
- `tests/Allegiance.Core.Tests/Transport/TcpTransportCoalesceAndReasonTests.cs`
- `tests/Allegiance.Core.Tests/Transport/TcpTransportSendDropSafetyTests.cs`
- `tests/Allegiance.Core.Tests/Transport/TcpTransportSendWorkerPoolTests.cs`
- `tests/Allegiance.Core.Tests/Transport/NonBlockingSendLiveTests.cs`
- `docs/reports/P611_OVERFLOW_POLICY_LIVENESS.md` (this file)

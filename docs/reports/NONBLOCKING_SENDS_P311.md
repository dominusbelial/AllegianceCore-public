# P311 — Non-blocking sends with timeout (one stalled client can no longer freeze the whole server)

## Relationship (why this exists)

Finding **C1** — the single most severe stability finding across every server
review this project has run (read-only `zai`/glm-5.3 full-codebase review; see
`~/Documents/Domingo-Llm-Wiki/pages/allegiance-server-review-glm53-full-codebase.md`).

`TcpTransport.WriteFrame()` did a **synchronous, blocking `NetworkStream.Write`
under `_writeLock`**, reached from `Server.SendPeer()` → `ServerHost.Send()` →
`TcpTransport.Send()` for *every* packet the server emits on the single
simulation thread (every per-tick broadcast pass, every handler reply, every
post-tick drain). No `SendTimeout` was ever set on the accepted `TcpClient`
(`HandlePeer()` only set `NoDelay`), so the OS default of 0 (infinite) applied.

**Failure scenario:** one client's process suspends or its network path
black-holes; that peer's inbound data rate fills its send buffer in seconds;
the next write to that one peer blocks the sim thread — every player freezes,
input stops processing, the mission clock stops, and `Loop()`'s
cancellation-token check never runs (it only checks between ticks), so even
SIGTERM can't recover — only SIGKILL. No malicious client required.

## What was built

Outbound sends are now **non-blocking from the sim thread's perspective** by
construction, plus two layers of defense on the writer.

### 1. Per-peer bounded send queue + dedicated writer thread (`PeerSender`)
- Every `TcpTransport.Send` now only **enqueues a fully-framed `byte[]`** into
  the target peer's `PeerSender` and returns immediately — no socket I/O, ever,
  on the caller (sim) thread.
- Each `PeerSender` owns a background writer thread that serially drains its
  queue and performs the single real blocking `Write`. Serializing per peer
  preserves **per-peer FIFO ordering**; a frame is assembled wholesale
  (`BuildFrame`) so it is never split or interleaved.
- This mirrors the `SendPeer → ServerHost.Send → TcpTransport.Send` hot path:
  the sim thread can no longer be frozen by any single peer.

### 2. Layer 2: a real `SendTimeout` on the socket
- Accepted peers (and the client half) now set `client.SendTimeout = 10_000`
  and `client.SendBufferSize = 8 * 1024`. Even the writer thread's blocking
  `Write` now gives up (throws `SocketException`) after 10 s against a
  black-holed path instead of hanging forever (the old default of 0 = infinite).
- The modest send buffer also makes a genuinely unresponsive peer's OS send
  buffer fill quickly, so the backlog surfaces promptly.

### 3. Overflow / disconnect policy (no unbounded backlog either)
- `CapacityBytes` (8 MiB/peer) caps queued bytes.
- Under pressure the **oldest non-guaranteed** frames are dropped first
  (best-effort traffic may be shed to make room) — guaranteed traffic is
  **never** silently dropped.
- When only guaranteed traffic remains over capacity — the client genuinely
  cannot keep up and nothing is droppable — the backlog may sit over capacity
  for at most `OverflowTimeout` (5 s), after which the peer is **disconnected
  outright** via `DropPeer` → `ITransportSite.OnPeerLeft`. This prevents the
  "freeze traded for a memory leak" pathology: a truly stalled peer is dropped,
  a healthy one never sustains overflow.
- `DropPeer` raises `OnPeerLeft` **itself** (guarded exactly-once by a
  `ConcurrentDictionary`), so an overflow disconnect always surfaces to the
  server even when the peer's read loop is parked on a stalled socket (the
  disconnect no longer depends on the read loop noticing the close).

### Exactly-once peer teardown
Both the read-loop `finally` and `DropPeer` may reap a departed peer; a
`ConcurrentDictionary<ConnectionId,byte>` `_peerEnded` guarantees
`OnPeerLeft` fires exactly once whichever path wins.

## Scope / exclusions
- The accept-loop greeting-read freeze (**C2**) is a separate root cause —
  task **P312**, intentionally left untouched.
- Other H/M findings (turret-slot loop, exception isolation, queue/logging caps,
  id wraparound, O(P²) broadcast cost, console logging) — separate tasks.
- Native reference source `~/Development/Allegiance` — read-only, untouched.

## Verification

### Build
Whole solution: **0 warnings, 0 errors.**

### Full test suite
`Allegiance.Core.Tests`: **3230 total; all P311 tests pass.** The only
in-suite failures are the repo's pre-existing LiveNetwork timing flakes
(LiveTurretGunnerBindingTests is the documented persistent one; the others —
LiveRipcord, LiveMoney, LiveChatSectorWire, LiveTeamLobby, StationsUpdateLive —
rotate run-to-run and are **green in isolation**; `StationsUpdateLiveTests`
was confirmed to flake **3/4 in isolation on the pristine tree too**), so they
are environmental, not P311 regressions.

New tests (all pass **5×** consecutively):
- `TcpTransportSendDropSafetyTests.Stalled_non_reading_peer_does_not_block_and_is_disconnected`
  — Send to a never-reading peer returns in microseconds (400×64KiB), the peer
  is disconnected via the overflow policy rather than queued forever, and
  post-disconnect sends stay silent.
- `...Multiple_sends_to_one_peer_preserve_fifo_order` — ordering (requirement 4).
- `...Healthy_host_send_still_arrives_on_the_wire`, `...Host_send_to_a_reaped_peer_id_does_not_throw`,
  `...Client_role_Send_before_join_still_throws` (P60 survival, adapted).
- `NonBlockingSendLiveTests.Others_keep_ticking_and_mission_advances_while_one_peer_stalls_then_disconnects`
  — the headline live proof.

### Live proof (real loopback sockets + real in-process server, vizfight/bot harness)
3 real bots played normally while a raw, **seated** peer (spoke Greet +
LOGONREQ + POSITIONREQ so the server actually broadcasts to it, then stopped
reading) was connected:

- **Sim thread not frozen:** 60 ticks of mission time advanced in **~0.06 s of
  wall clock** while the stalled peer was present (assert: < 5 s).
- **Mission clock kept advancing:** `SimNow` advanced **3.00 s over 60 ticks**
  during the stall.
- **Healthy peers kept receiving:** bot0's per-tick `HEAVY_SHIPS_UPDATE` count
  went **2 → 62** (while stalled) → **944** (after disconnect).
- **Stalled peer eventually disconnected:** dropped from the seated set after
  ~882 ticks rather than accumulating an unbounded backlog.

**Must-fail (change removed):** with synchronous sends restored (temporary
`ForceSyncSend` demo switch, since removed), the same live scenario froze — the
server could not complete the 60-tick phase within 45 s (queued version < 2 s)
— confirming the queue is what prevents the freeze.

## Files
- `src/Allegiance.Core/Transport/TcpTransport.cs` — queue design (the fix).
- `src/Allegiance.Core/Allegiance.Core.csproj` — `InternalsVisibleTo` test seam.
- `tests/Allegiance.Core.Tests/Transport/TcpTransportSendDropSafetyTests.cs` — policy/ordering/P60 tests.
- `tests/Allegiance.Core.Tests/Transport/NonBlockingSendLiveTests.cs` — live anti-freeze + disconnect proof.

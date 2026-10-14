# P645 — Transport send-side hardening: never drop a half-sent frame; a hard memory cap for every peer

Date: 2026-10-14
Branch: `master` (commit `871da8f2`, amended with suite numbers)
Scope: `src/Allegiance.Core/Transport/TcpTransport.cs`, `src/Allegiance.Server/ServerHost.cs`, `src/Allegiance.Server/Server.Dispatch.cs`, tests.

---

## Why this task existed

An external code review (Codex/GPT Sol, `review/gpt-sol` branch,
`docs/reviews/GPT_SOL_REVIEW.md`, 2026-10-13) found two HIGH transport defects,
both verified against the code at `8f7a3770`:

1. **A human write timeout drops the frame, which can corrupt TCP framing.**
   - `TcpTransport.cs` `TryWriteHead` (~1064) called `PopHeadLocked` (~1157),
     which removed the frame from the queue *before* `_stream.Write(...)`. On an
     exception P619c's branch `if (IsHuman && timeout) return DrainResult.Dead;`
     neither restored the frame nor closed the connection; the next enqueue
     resumed sending on the same socket. The dedicated-writer path (~1225-1245)
     had the same policy. P619c's own comment said the socket wedged "mid-frame".
   - Effect: no bytes went out → a guaranteed (ordered) frame was silently lost;
     a prefix went out → the next frame's bytes completed the previous frame's
     declared body, and the client read body bytes as headers from then on. After
     a send timeout .NET leaves the socket in an indeterminate state. Pool mode
     sets `SendTimeout` to 250 ms, so this is reachable on a laggy client.
   - Why P619c did it (intent kept): a laggy-but-alive human must not be cut for
     a hiccup (the 2026-10-12 mass disconnect).

2. **A "human" peer has no memory bound.**
   - In `Enqueue`, the ordered lane always appends; the shedder
     `while (> CapacityBytes && DropOldestNonGuaranteed())` drops only stream
     frames, then `_orderedQueue.AddLast(...)`. `overCap = !IsHuman && …` disabled
     the overflow disconnect for humans.
   - "Human" is self-declared: `Server.Dispatch.cs` LOGONREQ calls
     `SetPeerHuman(from, true)` for any validated name not under the harness
     prefix. Attack: log on as "Alice", send valid PINGs fast, read slowly. Every
     PING adds an uncoalesced guaranteed reply, and occasional successful writes
     keep the no-progress timer fresh. The queue grows without limit, beyond the
     8 MiB `CapacityBytes`. The server faces the internet.

---

## Design / era / bug classification (from the footer)

| System | Native behaviour | Class | What the port does |
|---|---|---|---|
| Wire framing | Native used **DirectPlay**, which owned TCP framing (length-delimited packets, its own chunking). | — | This is a **port bug**: the C# port added its own 5-byte outer length prefix (FEDMSG.md §3) and owns framing entirely, so send-side integrity is the transport's responsibility. DirectPlay never exposed a half-sent-frame drop because it owned the packetization. |

## Native behaviour classified (per-policy)

| Behaviour | Native cite | Class | What P645 ports |
|---|---|---|---|
| Never cut a laggy-but-alive human (P619c intent) | client link-dead tolerance `g_sDeadTime = 30` (clintlib.cpp:2047); human grace extends this | design | Kept. A slow-but-progressing peer is never cut; only genuine no-progress (the `NoWriteProgress`/`HumanGrace` authority) or an actual hard-cap overflow disconnects. |
| Ordered vs streaming send split | `Messages.cpp:548-563` (guaranteed `DPNSEND_GUARANTEED|COALESCE`, best-effort `DPNSEND_NONSEQUENTIAL` drops at 500 ms) | design | Kept unchanged (P634 two-lane queue). |
| Ship-state frames best-effort / shed-able | `FedSrv.CPP:4189` (HEAVY/LIGHT_SHIPS_UPDATE `FM_NOT_GUARANTEED`) | design | Kept unchanged. |
| Client PING cadence | `clintlib.cpp:2050-2085` `CheckServerLag`, `maxLagCheck = 6.0f` → at most one PING per ~6 s | era constraint | Kept intent; P645 throttles replies to ≥1 per s (6× headroom) and drops+counts the excess, so a PING flood cannot amplify into an unbounded guaranteed-reply queue. |
| Half-sent frame on a timed-out write | DirectPlay owned packetization; not applicable to a blocking-write port | port bug | P645 never abandons an in-flight frame; a blocking write that times out with unknown progress **closes the connection** (`SendTimeout`). |

## Modern alternative considered

- **Snapshot + ack-based reliability with a single bounded reorder buffer** would
  be the modern endgame (reliability sliding-window instead of a per-peer FIFO).
  The existing two-lane FIFO is the transport's established, P634-proven design
  and changing it is out of scope; P645 hardens it rather than replaces it.
- **True non-blocking `Socket.Send` in every mode** (flip the shared socket
  non-blocking) was considered and rejected for the *dedicated* writer: it would
  require making the shared read loop would-block-tolerant everywhere (a
  receive-side change P646 owns) and would break the deterministic
  controlled-stream test seam. P645 uses non-blocking `Socket.Send` for the
  **pool** worker (where the shared worker must never block) and keeps the
  dedicated writer on a blocking `_stream.Write` bounded by `SendTimeout`,
  closing on a timed-out write — the "simplest correct option" the task allowed.
  Chosen: hybrid, minimal-risk, preserves both P619c intent and the test seam.

---

## Scope 1 — partial-write-safe sending

`PeerSender` now promotes the head frame into an **in-flight buffer + byte
offset** (`_inflight`, `_inflightOffset`) that is never abandoned until fully
sent. `PromoteHeadLocked()` removes the head from the queue and deducts its bytes
up front (so byte/overflow accounting stays exact), but unlike the old
`PopHeadLocked` it does **not** discard the frame: it lives in `_inflight` until
`SendInflight()` reports the whole frame on the wire.

`SendInflight()` transmits the remaining bytes and folds the accepted count into
the offset (`AdvanceInflight`), returning:

- `Wrote` — some or all bytes went out (in-flight cleared once the whole frame is
  on the wire); a partial send is resumed on the next pass, never abandoned;
- `Blocked` — the transport can't take more right now (OS buffer full / would-
  block): the in-flight frame is **kept** and retried;
- `Dead` — a hard error, or a write that **timed out with unknowable progress**:
  the connection is **closed** (`SendTimeout`) rather than continued on a
  possibly-corrupt stream.

Transmit selection:
- **Pool worker** — non-blocking `Socket.Send` (`AttachWorker` flips the socket
  non-blocking after `GetStream`, which the greeting already ran). Reports partial
  bytes / would-block, so the shared worker never blocks and a slow-but-alive
  **human** is never cut by a write timeout (would-block just waits; the
  no-progress grace remains the authority).
- **Dedicated writer** — blocking `_stream.Write` (bounded by the socket's
  `SendTimeout`). A slow-but-alive human's write simply completes (bytes flow), so
  P619c's intent is preserved; only a genuinely stalled write (unknown progress)
  closes the socket — the only safe action once framing may be corrupt.
- **`SendOverride` seam** (P645, `SendOutcome`) — a deterministic fake send-sink
  the suite uses to unit-test partial-write / would-block / timeout logic.

Because pool sends are now non-blocking, the read loop must tolerate a
non-blocking socket: `ReadExactly` retries on `IsWouldBlock`
(`SocketError.WouldBlock`/`TryAgain`, surfaced as an `IOException` wrapping a
`SocketException`, verified empirically: inner `SocketException code=WouldBlock
(10035)`). Greeting reads still run on the blocking socket (before `AttachWorker`).

## Scope 2 — absolute queued-byte cap for every peer

New `TcpTransport.HardCapBytes` (default `4 × CapacityBytes` = **32 MiB**),
checked in `Enqueue` for **every** peer, humans included. A peer whose queued
bytes exceed it is disconnected with a **new, logged reason
`DisconnectReason.QueueHardCap`** and no exemption. The human exemption stays
only for the timed `QueueOverCapacity` rule (`overCap = !IsHuman && …`).

Value chosen from the 200v195 (391-peer) live baseline: the largest legitimate
peer queue observed was **~17.5 KB** (`[p634-queue] maxQueuedBytes=17484`) during
the join/world/export march. `HardCapBytes = 32 MiB` leaves a **~1900× margin**
over any legitimate burst while still bounding a guaranteed-flood attacker's
memory tightly. Reported in the Live section below.

## Scope 3 — rate-limit client-triggered replies

The native client pings at most once per ~6 s (`clintlib.cpp:2052-2085`,
`CheckServerLag`, `maxLagCheck = 6.0f`). `ServerHost` now enforces a per-peer
`ShouldAnswerPing` gate (`MinPingReplyIntervalMs = 1000`, internal seam): within
the interval → answered; faster → **dropped and counted** (`PingsThrottled`), not
answered, so a PING storm cannot amplify into an unbounded guaranteed-reply queue.
The `[ping]` per-request console line is **gated** via a `PerPeerRateLimiter`
(sampled per peer, summarized), so a flood cannot force unbounded console I/O
(log-flood vector). The second `fmid == 35` PING branch in `Server.Dispatch.cs`
is unreachable (shadowed by the top-level `if (fmid == Ping.Fmid)` + `continue`)
and needs no separate gating.

---

## Verification

### Unit tests (failing-first) — `TcpTransportSendHardeningTests` (+ PING throttle)

All 9 new tests pass on P645; the hard-cap and timeout-close tests were proven
failing-first by temporarily reverting each fix and watching them go red:

| test | what it pins | failing-first proof |
|---|---|---|
| `Partial_writes_reassemble_every_frame_byte_exact_in_order` | a tiny-window non-reading peer forces partial/would-block writes; every frame reassembles byte-exact, in order, nothing lost (both modes) | — (correctness guard) |
| `Blocking_write_timeout_closes_the_connection` | a dedicated-writer blocking write that times out with unknown progress **closes** (reason `SendTimeout`); a human too | red when the old "human drops frame & resumes" was re-instated |
| `Human_whose_queue_exceeds_the_hard_cap_is_disconnected_with_QueueHardCap` | a human whose ordered queue exceeds the absolute cap disconnects with `QueueHardCap` (overflow exemption does not apply) | red when the hard-cap check was disabled |
| `Slow_but_progressing_human_under_the_cap_is_not_disconnected` | a human making write progress under the cap is never cut | — |
| `Ping_flood_over_the_reply_interval_is_dropped_and_counted` | 50 rapid pings from one peer → 1 answered, 49 throttled | red (no API pre-P645) |

Existing `TcpTransportLivenessPolicyTests` were updated to be **mode-aware**:
thread-per-peer writers transmit via `_stream`, so the fake `ControlledStream` is
swapped in as before; pool workers transmit via the non-blocking socket, so the
identical behaviour is installed as a `SendOverride` fake sink
(`InstallWriteControl`). All 71 transport tests green in both modes.

### Whole-solution build
`dotnet build AllegianceCore.slnx -c Release`: **0 warnings, 0 errors**.

### Suite — `scripts/run-tests.sh`, 2 runs on the final code (P471 rule)

- **Run 1:** Lobby 12/12 · MonoGame 1968/1971 (3 skipped) · ClientCore 748/749 ·
  **Core 4018/4018 clean**. Single failure:
  `SessionLossLiveTests.EstablishedSession_ServerKilled_RaisesLost_…` — a
  live-wire session-lost receipt timing flake (verified 3/3 in isolation), added
  to `docs/TEST_FLAKES.md`. `run-tests.sh` exit 1.
- **Run 2:** Lobby 12/12 · MonoGame 1967/1971 (3 skipped, `ChatTargetCompose…`
  flake) · ClientCore 749/749 · Core 4015/4018 (3 established flakes:
  `SideElimination…`, `P591dMinefield…`, `GameOverPlayers…Win_by_elimination`).
  `ChatTargetCompose…` (MonoGame) verified 3/3 in isolation, added to the ledger.
  `run-tests.sh` exit 1.

All 5 failures are documented live-wire load-flakes in `docs/TEST_FLAKES.md`
(two newly added); **none are in code touched by P645**, and the P645 transport
suite (`Allegiance.Core.Tests`) was fully green in run 1 (4018/4018).

### Live proof (own temp worktree `/tmp/p645-proof`, local server 127.0.0.1:6073, m2xspiderw, showcase, target Alderan)

**30v30 combat + reach check** (showcase `size_scale 2`, march + staging, ~60 bots):
- **reach:** 94 `[warp] … crossed … to 'Alderan'` lines — bots left home sectors
  and reached the target.
- **combat:** **18 ship kills** (`[combat] ship X killed by ship Y … KILL_SHIP`)
  with 0 friendly fire among them; 573 sector-1052 engagement lines.
- **disconnects:** 0 transport-caused (the only 2 `ReadErrorOrEof` were the
  probe/teardown sockets).

**195v195 baseline** (showcase `size_scale 13` = 390 bot clients + probe = 391
peers, march + staging, profiler on), travel + early-engagement windows:

| metric | observed |
|---|---|
| peers | 391 |
| tick rate | 100–101 ticks / 5 s (= 20 Hz nominal maintained, no sim stall) |
| tick-wall mean / p95 / max | 37.6–39.9 ms / 43.5–51 ms / 46–58 ms |
| **max per-peer queued bytes vs `HardCapBytes`** | **13 077 – 17 484 B vs 32 MiB** (~1900× margin) |
| max ordered pending / stream pending | 163–181 / 8–11 |
| max oldest guaranteed (head-of-line) | 39–383 ms |
| per-peer egress | ~242–248 kbps |
| GC gen0 / gen1 / **gen2** | ~42 / ~4 / **0** (no gen2 storms) |
| disconnect reasons | 0 transport (`NoWriteProgress`/`QueueOverCapacity`/`SendTimeout`/`QueueHardCap` all 0); 1 `ReadErrorOrEof` (teardown) |

The 195v195 march+staging had not yet developed decisive combat in the capture
window (wings stage before advancing at this scale); the mandatory combat check
was the 30v30 above (18 kills, zero disconnects). All local server/bot processes
were stopped by exact PID and the temp worktree removed.

---

## Deliverables

- Commit on `master` (`871da8f2`, amended with suite numbers), not pushed.
  `git branch --show-current` = `master`.
- Report: `docs/reports/P645_TRANSPORT_SEND_HARDENING.md`.

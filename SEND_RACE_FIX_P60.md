# P60 — Fix the send-race that crashed the whole server on an abrupt disconnect

**Date:** 2026-09-02
**Branch:** `master`

## What was wrong

An abrupt client disconnect could throw an **unhandled exception on the
simulation thread** and take down the *entire server process*, not just that
one peer's connection. The root cause is a two-table race window:

- The transport's per-peer read loop (`TcpTransport.cs:182-194`) removes the
  peer from the host's own `_peers` dictionary (`:190`, inside a `finally`)
  **before** it enqueues the matching `PeerLeftEvt` via `_site.OnPeerLeft(peerId)`
  (`:192`).
- That event is only *drained* on the sim thread the next time
  `Server.HandleTransportEvent` runs (`Server.cs:537-553`), which is where
  `_peerShips.Remove(l.Id)` (`Server.cs:551`) actually happens — the server's
  broadcast-target list.
- **Window:** between the two, `_peers` no longer has the dead peer but
  `_peerShips` still does. Any broadcast in that window —
  `BroadcastShipStates` (`Server.cs:379-435`), `SendPeer` call sites looping
  `_peerShips` — calls `SendPeer` → `ServerHost.Send` (`ServerHost.cs:398`) →
  `TcpTransport.Send` (`TcpTransport.cs:100-117`), whose host branch did
  `_peers.TryGetValue(target, out s) ?? throw new
  InvalidOperationException($"no such peer {target}")` (**old** `:107-108`) —
  the peer is already gone, so it threw.
- A second variant: the peer is still in `_peers` but its socket is already
  dead, so `WriteFrame`'s blocking `s.Write(...)` (`:225`) threw
  `IOException`/`SocketException`/`ObjectDisposedException` instead.
- Either exception propagated uncaught up through `Send` → `SimPass` → `Tick`
  → `Loop` (`Server.cs:310-333`, `:516-523`) → `RunAsync`, landing only at
  `ServerMain.RunAsync`'s outer `try/catch` (`Program.cs:44-52`), which prints
  `"server failed: {e.Message}"` and returns exit code 1. **The whole server
  died because one peer dropped ungracefully.**

Inbound was already non-fatal by design — `ReadDataLoop` (`TcpTransport.cs:198-214`)
catches `EndOfStreamException`, `IOException`, `SocketException`,
`ObjectDisposedException`, `ArgumentException` around a dead peer's own read
loop. Outbound sends during a broadcast had no equivalent protection.

## The fix (exact site)

**`src/Allegiance.Core/Transport/TcpTransport.cs` → `Send`, host branch
(`:100-131`).** Concretely:

1. Host branch: `_peers.TryGetValue(target, out s!)` — if the peer is no longer
   in `_peers`, `return;` (drop the packet silently) instead of `throw new
   InvalidOperationException(...)`. Removing a peer is an expected mid-tick
   outcome, and the caller has no way to act on a per-packet send failure
   anyway. No per-dropped-packet log line is added: the peer's own
   `[conn] peer {id} left` line already logs exactly once from `Server.cs:552`
   when the event drains (avoiding a repeat of P58's `[teamshipstatus]`
   log-spam issue).
2. Wrap the `WriteFrame(s, FrameType.Data, data)` call in `Send` with a
   `catch` for the dead-socket set — `IOException`, `SocketException`,
   `ObjectDisposedException` — dropping the packet, not propagating. This is
   exactly the same exception set `ReadDataLoop` already treats as "peer is
   gone", keeping the outbound policy symmetric with the inbound one.

**Where the catch lives and why:** the catch is placed *in `Send` around the
`WriteFrame` call site*, not inside `WriteFrame` itself. Rationale:
`WriteFrame` is also called by `Join` for the **client** greet frame
(`:96`). Putting the swallow inside `WriteFrame` would make that greet write
silent-fail too, masking a real client connect problem we want to stay loud.
Keeping the guard in `Send` scopes the quiet-drop policy precisely to the
`Send` data path.

**Client-role branch left throwing:** the `else` branch
(`s = _clientStream ?? throw new InvalidOperationException("not connected")`,
`:113-114`) is unchanged. That firing means `Send` was called before
`Join`/`Host` ever ran — a real usage bug on the client side, not a network
race — so it stays loud. Confirmed still throwing by the new
`Client_role_Send_before_join_still_throws` test (and the existing
`ClientHarness`/round-trip tests still exercise the healthy client send).

## New regression test

File **`tests/Allegiance.Core.Tests/Transport/TcpTransportSendDropSafetyTests.cs`**
(4 tests) plus one live scenario in **`BotFlightTests.cs`**:

### Approach chosen: the smaller, surgical unit test (fallback), justified

The task asked for a test that actually lands inside the live race window by
closing a peer's raw socket on a separate thread timed to a broadcast-heavy
tick. That is genuinely non-deterministic to reproduce from outside: the two
racing parties are the transport's peer thread (reaps `_peers`, raises
`PeerLeftEvt`) and the sim thread (drains the event and removes from
`_peerShips`) — both schedule freely on the machine, so a test cannot *force*
a broadcast to fire between the reap and the drain. Such a test would be flaky
for exactly the reason the existing `Bot_disconnect_leaves_the_server_ticking`
(`BotFlightTests.cs:264-300`) misses the bug: it settles after `bot.Shutdown()`.

So the two load-bearing regressions use reflection to **pin the transport into
each branch of the race deterministically** — the explicit fallback the task
sanctions. Each new test *fails* on the pre-fix code (verified empirically, see
below) and *passes* on the fixed code, with zero thread-timing sensitivity:

- `Host_send_to_a_reaped_peer_id_does_not_throw` — pins branch (a): removes a
  real connected peer's id from `_peers` (simulating the reap) and calls
  `host.Send(id,…)`. Pre-fix throws `InvalidOperationException: no such peer 1`;
  post-fix returns silently.
- `Host_send_to_a_peer_whose_socket_throws_does_not_propagate` — pins branch (b):
  replaces the peer's `_peers` entry with a `NetworkStream` whose `Write`
  always throws `IOException`. Pre-fix the `IOException` propagates out of
  `Send`; post-fix it is swallowed.
- `Healthy_host_send_still_arrives_on_the_wire` — the fix must not suppress
  good sends: after a normal host `Send`, the exact bytes come back on the
  client socket (decoded from the frame), proving `WriteFrame` still emits.
- `Client_role_Send_before_join_still_throws` — the client "not connected"
  path must stay loud: `Assert.Throws<InvalidOperationException>` on a
  transport that was never Host/Join-ed.

### Live scenario (bonus realism, non-flaky assertions)

`BotFlightTests.Abrupt_disconnect_during_broadcast_leaves_server_alive_and_others_served`
sets up **three real loopback peers**, gets each a moving ship (so every tick's
`BroadcastShipStates` targets all of `_peerShips`), then aborts one peer's
connection and *immediately* hammers `TickSim()` 900 times with no settle —
forcing any broadcast-landing-in-the-window to run against the dead peer. It
asserts (1) no exception escapes `TickSim` (the test itself fails if `Send`
threw — that's the pre-fix fatal symptom), (2) the server keeps ticking, and
(3) the other two still peers keep receiving broadcasts in the same window.
These assertions are all stable under the fix; the surgical tests are what
*deterministically* prove the catch.

## Empirical proof the tests bite

Verified against the pre-fix code (transiently reverted, then restored):
- `Host_send_to_a_reaped_peer_id_does_not_throw` → FAIL `InvalidOperationException: no such peer 1` at old `TcpTransport.cs:108`.
- `Host_send_to_a_peer_whose_socket_throws_does_not_propagate` → FAIL `IOException: socket dead` propagating from `WriteFrame`/old `Send:116`.

Same tests → PASS against the fixed code. Full suite green 3/3 below.

## Verification / results

- Baseline before change (unmodified HEAD): `dotnet test` → **2263 passed**, 0 failed.
- Post-change full suite: run #1 had one failure in an **unrelated, pre-existing
  flaky chat/layout test** (`LiveChatSectorWireTests.cs:144`, a 5-bot
  sector-membership count "Expected 2, Actual 1" — a timing-sensitive multi-bot
  race in server side/sector assignment, not this code path). It is not touched
  by this change, passed in the baseline, and passed in every run thereafter.
- **3 consecutive clean runs after that** (runs 2,3,4,5,6 all
  `Test Run Successful`: 2268/2268 passed, 0 failed; run 1 had the 1 unrelated
  flake). Logs: `/tmp/p60_run{1..6}.log`. Totals = baseline 2263 + the 4 new
  transport tests + 1 new live scenario = 2268.
- No VM involved; loopback-only.
- Commit made only after those clean runs.

## Out of scope — untouched (per task)

- Global `_writeLock`/head-of-line-blocking design — separate concern.
- P58 `[teamshipstatus]` O(n²)/log-volume — separate follow-up, not folded in.
- `guaranteed` semantics (`ITransport.cs:47,51` — accepted-and-ignored, TCP-only).
- `Server.cs` size/structure (`JoinSequence.cs` split) — unrelated.

## Files changed

- `src/Allegiance.Core/Transport/TcpTransport.cs` — the fix (+19/-2).
- `tests/Allegiance.Core.Tests/Transport/TcpTransportSendDropSafetyTests.cs` — new, 4 surgical tests.
- `tests/Allegiance.Core.Tests/BotFlightTests.cs` — +1 live scenario test (+52).

# P312 — Accept-loop greeting-read timeout (one silent connection no longer blocks all new joins)

## Relationship (why this exists)

Finding **C2** from the same read-only `zai`/glm-5.3 full-codebase review as
P311 (see
`~/Documents/Domingo-Llm-Wiki/pages/allegiance-server-review-glm53-full-codebase.md`).

`TcpTransport.ReceiveLoopStart()` accepted a connection
(`l.AcceptTcpClient()`) and then called `HandlePeer(client)` **inline on the
single accept thread**, which reads the peer's greeting **synchronously** via
`ReadFrame → ReadExactly → s.Read(...)`. No `ReceiveTimeout` was set, so a
client that connects and sends nothing (or a partial frame) parks the accept
thread forever in `s.Read`.

**Failure scenario:** any client — malicious, or behind a wedged NAT — opens
one TCP connection and sends zero bytes. The accept thread parks in `s.Read`;
**no further connection can complete a greet until the staller disconnects** —
a silent, total join outage from a single unauthenticated socket, while all
already-connected players are unaffected. Compounding: the staller could send a
5-byte frame header claiming a length up to `MaximumFrame` (~16 MiB), forcing a
16 MiB allocation *and then* blocking on the body read.

## What was built

Three cooperating changes, matching the task's three scope points.

### 1. Greeting read moved off the accept thread (per-connection thread)
`ReceiveLoopStart()` now returns from `AcceptTcpClient()` immediately and spawns
a short-lived **background thread per connection** that runs `HandlePeer`
(the greeting read + registration + the peer's read-loop spawn). A stalled
greet therefore blocks only that connection's own thread; the accept loop keeps
accepting, so one silent socket can never wedge new joins.

Concurrency choice: a plain `Thread` per greet (not tracked in `_threads`, since
it is ephemeral and background) — consistent with the transport's existing
thread-per-peer reader and writer design. Because `HandlePeer` can now run
concurrently for multiple connections, the host id fountain
(`_nextPeerId++`, previously a non-atomic read-modify-write that assumed the
single accept thread) was changed to `Interlocked.Increment`, so two
simultaneous greets can never mint the same id.

### 2. Real `ReceiveTimeout` on each accepted socket
`ConfigureAcceptedSocket` now also sets `client.ReceiveTimeout` (default 10 s,
test-seammed as `TcpTransport.GreetTimeout`), alongside the existing P311
`SendTimeout` (10 s) and `SendBufferSize` (8 KiB). This bounds even the
per-connection greeting read: a peer that connects and sends nothing gives up
and is dropped cleanly instead of parking forever.

Crucially the timeout is **cleared back to 0 (infinite) in `HandlePeer` right
after a successful greeting**, so it applies only to the join handshake and
never to the peer's data session — an idle-but-healthy player (hands off the
keyboard, sends no controls) is not dropped merely for going quiet.

### 3. Greeting body length capped below the 16 MiB frame cap
The greeting body is just a player name, so `HandlePeer` reads it via
`ReadFrame(stream, MaximumGreeting = 4096)` instead of the generic
`MaximumFrame`. A staller claiming a huge greeting length it never sends is
rejected **before any large allocation** (the length check precedes the body
`new byte[len]`), closing C2's compounding vector. A body *within* cap that
never arrives is still bounded by the socket `ReceiveTimeout`, so the read
drops rather than parking.

## Timeout values and why

- **GreetTimeout (receive), 10 s.** A real client sends its name in
  microseconds on connect; 10 s is very generous for a legitimate join and its
  sole purpose is to make a connection that sends nothing give up. It is
  reset to 0 after a successful greet so it never touches the data session.
- **SendTimeout, 10 s / SendBufferSize 8 KiB.** Retained from P311 — bound the
  per-peer writer thread against a black-holed consumer.

## Scope / exclusions

- Sim-thread blocking-send freeze (**C1**) — P311, already landed, untouched here.
- Other H/M findings from the same review — separate tasks.
- Native reference source `~/Development/Allegiance` — read-only, untouched.

## Verification

### Build
Whole solution (`AllegianceCore.slnx`, Debug): **0 warnings, 0 errors.**

### Unit tests — `TcpTransportGreetTimeoutTests` (4 new, all pass 2× in-suite + isolation)
- `Silent_connection_does_not_block_a_second_client_join` — while a silent
  socket sits idle, a second real client connects and completes logon fast.
- `Silent_connection_is_eventually_dropped` — the server closes the stalled
  connection rather than letting it linger.
- `Oversized_greeting_length_is_rejected_and_dropped_quickly` — a 64 KiB
  claimed greeting is rejected on the length cap, dropped fast (GreetTimeout
  lengthened so a broken impl that waited on the body read would hang the test).
- `Joined_but_idle_peer_is_not_dropped` — guards against the timeout leaking
  into the data session (regression guard for the reset-to-0).

**Must-fail (change removed), both proven red:**
- Reverting the accept loop to call `HandlePeer` inline reproduces the C2 bug —
  the silent staller wedges the accept loop and the second client cannot join
  ("accept loop appears wedged", 5 s timeout) — test (a) fails.
- Removing `client.ReceiveTimeout = 0` after the greet causes an idle joined
  peer to be wrongly dropped — test (d) fails.

### Live proof (real loopback sockets, real in-process `TcpTransport` host, /tmp/p312proof)
- Opened **3 raw TCP connections that sent zero bytes** (C2 stallers).
- A **real client** connected via the transport's `Join` path while all 3
  stallers sat idle: `Join()` returned in **1 ms**, and the host fired
  `OnPeerJoined` — i.e. a second client completed logon despite N silent sockets.
- All **3 stallers were then dropped by the host** (client reads returned EOF)
  rather than lingering.
- A **second real client joined cleanly after the stallers were reaped** — the
  accept loop stayed healthy the whole time.

### Full test suite (2 runs)
`Allegiance.Core.Tests`: **3234 total** (pre-P312 baseline 3230 + 4 new).
- Run 1: 6 failures, Run 2: 2 failures — the documented rotating LiveNetwork
  timing-flake families only (LiveTurretGunnerBindingTests persistent;
  LiveMoney / LiveCountdownStartPing rotating). All implicated flakes pass in
  isolation, and none of the failures are the new P312 tests, which pass in
  every run. No regression attributable to P312.

## Files
- `src/Allegiance.Core/Transport/TcpTransport.cs` — per-connection greet thread,
  `ReceiveTimeout`, `MaximumGreeting` cap, `Interlocked` id fountain.
- `tests/Allegiance.Core.Tests/Transport/TcpTransportGreetTimeoutTests.cs` — the 4 tests above.

# AllegianceCore review

Reviewed `8f7a3770` on `review/gpt-sol`. Findings below are ranked by severity.
Source code was not changed. This was a targeted review of transport framing,
server dispatch/lifecycle and broadcast scheduling, client session decoding, and
MonoGame resource ownership; it is not an exhaustive audit of every scoped line.
Tests were read for intent, not reviewed as deliverables.

## High — human write-timeout recovery loses frames and can corrupt TCP framing

**Category:** correctness / robustness. **Classification:** bug in the port's
TCP transport, not an intentional native gameplay rule.

**Locations:** `src/Allegiance.Core/Transport/TcpTransport.cs:1072`, `:1097`,
`:1235`, `:1165`.

```csharp
if (!PopHeadLocked(out frame!)) return DrainResult.Idle;
// ...
_stream.Write(frame, 0, frame.Length);
// catch:
if (IsHuman && timeout)
    return DrainResult.Dead;
```

`PopHeadLocked` removes the frame before writing. The human timeout branch
neither restores it nor closes the connection; the dedicated writer has the
same exception policy. This applies to guaranteed ordered messages as well as
stream snapshots. Pool mode reduces socket send timeout to 250 ms.

**Failure scenario:** a human client's receive window fills during a large
world/export frame. A write times out. If no bytes were sent, the guaranteed
frame disappears. If a prefix was sent, the next frame's bytes complete the
previous frame's declared body, and the receiver subsequently reads body bytes
as its next transport header. CRC rejection cannot repair this outer framing.
Later tick enqueues allow sending to resume on that same connection.

**Suggested fix:** close the connection on a synchronous write timeout whose
partial progress is unknown. To support recoverable backpressure, use a send API
that reports actual bytes transferred and retain an in-flight frame plus its
offset until the entire frame is sent. Never discard a partially sent frame.

**Evidence:** verified dequeue/exception/resumption paths by inspection. The
partial-write network scenario was not induced in a live client; it is a
conditional failure, not a claim that every timeout writes a prefix.

## High — a self-declared human peer has no guaranteed-queue memory bound

**Category:** security / performance. **Classification:** port bug.

**Locations:** `src/Allegiance.Core/Transport/TcpTransport.cs:968`, `:1012`;
`src/Allegiance.Server/Server.Dispatch.cs:75` and `:52`.

```csharp
while (_queuedBytes + frame.Length > CapacityBytes && DropOldestNonGuaranteed()) { }
_orderedQueue.AddLast(new QueuedFrame(frame, guaranteed, coalesceKey, DateTime.UtcNow.Ticks));
// ...
bool overCap = !IsHuman && _overflowSinceUtc is { } os && now - os >= OverflowTimeout;
```

The 8 MiB capacity is not a hard admission limit. Ordered frames are always
appended, and only non-guaranteed stream frames can be shed. A version-matching
logon with any nonempty name outside the harness prefix sets `IsHuman`; there
is no identity authentication behind this exemption.

**Failure scenario:** a peer logs on as `Alice`, continues sending valid PING
requests, and reads replies more slowly than it generates them. Every request
adds a guaranteed uncoalesced response. Periodic successful writes keep the
no-progress timer fresh, while `IsHuman` disables overflow disconnection. The
ordered backlog can therefore grow for the lifetime of the connection, beyond
the documented capacity. A stalled peer can also accumulate a large backlog
within its 60-second grace. Other clients' chat/world events use this same lane.

**Suggested fix:** enforce an absolute queued-byte limit for all peers, including
humans. Disconnect/resynchronize a peer whose reliable stream cannot fit, or
apply bounded admission/backpressure without blocking the simulation thread.
Rate-limit client-triggered replies separately. Longer human liveness grace
should not remove the memory limit.

**Evidence:** verified queue admission, name-based exemption and PING response
paths by inspection; no memory-exhaustion load test was run.

## High — repeated logon replaces a live ship without retiring it

**Category:** correctness / security. **Classification:** port lifecycle bug;
native `FedSrv/FedSrv.CPP:4832` explicitly asserts that an existing player must
not send LOGONREQ. That assertion alone is not adequate hostile-input protection
either, but orphaning ships is not a gameplay rule to preserve.

**Locations:** `src/Allegiance.Server/Server.Dispatch.cs:90`;
`src/Allegiance.Server/Server.Lobby.cs:2807`, `:2944`;
`src/Allegiance.Server/Server.Lifecycle.cs:1488`.

```csharp
shipId = AllocateShipId();
_pendingSideSelect[from] = new PendingSideSelect(shipId, req, _simNow, _nextJoinSeq++);
// CompleteJoinAndLaunch:
_peerShips[from] = live with { /* broadcast baseline */ };
```

**Failure scenario:** a seated client sends another version-matching LOGONREQ,
then chooses a side with POSITIONREQ (or waits for automatic seating). The
server spawns another ship and overwrites `_peerShips[from]`. The previous ship
is still in its sector/mission, with its last controls, but no connection owns
it. Repetition creates arbitrarily many orphan ships from one connection. On
disconnect, `ReleasePeer` retires only the newest mapped ship. Old ship IDs and
subject caches can also remain held, and other players continue to encounter
the orphan models.

**Suggested fix:** enforce one logon state transition per connection. Reject
duplicate logons for seated peers and handle pending retries idempotently. If
relogon is intentionally supported, perform complete old-ship and cache cleanup
before reserving/spawning its replacement.

**Evidence:** verified the dispatch-to-seat-to-map overwrite and disconnect
cleanup paths by inspection; not reproduced through a live artwork mission.

## High — continuously replenished receive traffic can prevent simulation ticks

**Category:** security / performance. **Classification:** era constraint in the
single-owner message funnel, with an incomplete modern flood defense. Keep
simulation ownership serialized, but bound the work it performs per tick.

**Locations:** `src/Allegiance.Server/ServerHost.cs:1101`;
`src/Allegiance.Server/Server.Broadcast.cs:85`;
`src/Allegiance.Server/Server.Dispatch.cs:53`.

```csharp
while (_events.TryDequeue(out var evt))
{
    if (evt is ReceivedEvt r)
        _bufferedPerPeer.AddOrUpdate(r.From.Value, 0L,
            (_, old) => Math.Max(0, old - r.Data.Length));
    handle(evt);
}
```

**Failure scenario:** one or more peers send small valid packets faster than
dispatch can handle them. Released byte reservations immediately allow more
receives while the drain is still running. The queue need never become empty,
so the simulation stage, broadcasts, and cancellation checks after `Tick` can
be delayed indefinitely. PING is a particularly cheap producer: each request
also causes response packing and an unconditional console write. Neither the
per-peer byte budget nor the 256-message per-packet limit bounds total dispatch
work in a tick. Even a finite queued burst can consume far beyond the 50 ms
budget at 20 Hz.

**Suggested fix:** give each drain a bounded message/byte/time budget, with
per-peer fairness and rate limits. Preserve lifecycle event ordering while
carrying remaining receive work into later ticks. Rate-limit malformed and
PING logs as well as unsupported-message logs.

**Evidence:** a temporary reflection probe started with one queued receive and
enqueued its successor from the handler. One `Drain` consumed all 1,000 events;
it did not stop at the initial queue contents. Infinite starvation under real
network load was not stress-tested.

## Medium — disconnect cleanup is undone by draining older receive events

**Category:** robustness / memory retention. **Classification:** port bug.

**Locations:** `src/Allegiance.Server/ServerHost.cs:1064`, `:1106`.

```csharp
// Transport thread, before queuing PeerLeftEvt:
_bufferedPerPeer.TryRemove(id.Value, out _);
// Later, simulation-thread drain of a previously queued ReceivedEvt:
_bufferedPerPeer.AddOrUpdate(r.From.Value, 0L,
    (_, old) => Math.Max(0, old - r.Data.Length));
```

**Failure scenario:** a connection sends a frame and disconnects before the
next tick. `OnPeerLeft` removes its accounting entry. Draining the earlier
receive recreates the entry with zero bytes. Processing the queued departure
does not remove it again, because server `ReleasePeer` cannot clean this host
dictionary. Repeated connect/send/disconnect churn retains one entry per
historical connection rather than per live peer.

**Suggested fix:** release reservations only against an existing accounting
entry (atomic update without insertion), or finalize accounting cleanup when
the ordered departure event is drained. Account for receive/departure races.

**Evidence:** reproduced without sockets in a temporary reflection probe:
1,000 unique peers each performed `OnReceive`, `OnPeerLeft`, then `Drain`.
The dictionary retained **1,000 entries** after every event was consumed.

## Low — native ship and treasure GPU owners are omitted from content cleanup

**Category:** client resource ownership / maintainability. **Classification:**
port bug, unrelated to native rendering appearance.

**Locations:** `src/Allegiance.ClientCore.Game/Game1.cs:1678`;
`src/Allegiance.ClientCore.Game/Game1.Lifecycle.cs:172`, `:603`;
`src/Allegiance.ClientCore.Game/Rendering/NativeShipRenderer.cs:323`;
`src/Allegiance.ClientCore.Game/Rendering/NativeTreasureRenderer.cs:305`.

```csharp
_nativeShipRenderer = new Rendering.NativeShipRenderer(GraphicsDevice, _assets);
_nativeTreasureRenderer = new NativeTreasureRenderer(GraphicsDevice, _assets);
// UnloadContent disposes native asteroid/station/missile renderers,
// but neither of these owners.
```

**Failure scenario:** content unloads while the graphics device remains alive,
then loads again. New renderer instances replace these fields without disposing
the old cached vertex/index buffers, textures, effects and rasterizer states.
Their resources survive the content lifecycle until device teardown or eventual
runtime cleanup. Ordinary process exit/device disposal limits the practical
impact, hence low severity rather than a claim of a per-frame leak.

**Suggested fix:** dispose both owners in `UnloadContent`, and centralize the
renderer ownership list so new disposable renderers cannot silently be omitted.

**Evidence:** verified constructors, ownership and `Dispose` implementations
against the complete unload method. No GPU/device reload experiment was run.

## Validation and limits

- Build: `dotnet build AllegianceCore.slnx --no-restore -m:1 -v:normal`
  succeeded with **0 warnings / 0 errors**. The initial default parallel build
  exited unsuccessfully without diagnostics; the serialized build succeeded.
- Requested filtered tests: `dotnet test AllegianceCore.slnx --no-build --filter
  "Requires!=Artwork&Requires!=NativeSource&Requires!=MeshyPack" -m:1` passed:
  **3,034 tests**, zero failures (1,674 MonoGame, 549 ClientCore, 799 Core,
  12 Lobby). The first sandboxed attempt could not create the test runner's
  local socket; the successful run used approved execution outside the sandbox.
- Two host lifecycle/scheduling probes ran from `/tmp`; no source or test files
  in the repository were changed. Native source was checked locally for the
  logon-state invariant. No performance claim here is a measured 400-player
  benchmark, and no omitted area should be interpreted as defect-free.

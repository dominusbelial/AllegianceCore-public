# INBOUND_QUEUE_CAPS_P315.md

## Relationship

Finding **H3** of the glm-5.3 full-codebase stability review
(`~/Documents/Domingo-Llm-Wiki/pages/allegiance-server-review-glm53-full-codebase.md`):
an inbound packet/queue handling path that compounds into a flood/freeze vector.
Three compounding parts, all fixed here:

1. **Unbounded inbound event queue.** `ServerHost._events` (a
   `ConcurrentQueue<ITransportEvent>`) is drained once per tick (~20 Hz).
   `ITransportSite.OnReceive` enqueued every arriving packet — and a copy of its
   byte array — with **no cap and no backpressure**, so a client sending valid-CRC
   packets faster than the drain grew the queue (and its copied buffers) without
   bound.
2. **No cap on messages per packet.** `FedPacket.Unpack` walked messages purely
   on `cbmsg` until the buffer was exhausted (native `PfmGetNext`,
   `Messages.cpp:495-515`, has no such cap), so a ~16 MiB frame of 4-byte messages
   decodes into millions of messages that `Server.ProcessDatagram` dispatches —
   and logs — one at a time inside a single tick.
3. **Per-message refusal logging is unbounded.** The fall-through "unsupported
   fmid" branch of `ProcessDatagram` wrote one `Console.WriteLine` per unsupported
   message, so a single crafted packet forced millions of synchronous console
   writes inside one tick (and echoing via `FM_CS_PING` compounded into the P311
   send path, though that outbound amplification is P311's fix).

Review remediation line (H3): cap inbound queue depth, cap messages-per-packet,
and rate-limit the refusal logging.

## Exclusions (out of scope this task)

- **P311 / P312** (non-blocking sends with timeout; accept-loop greeting-timeout)
  — the two CRITICAL freeze bugs. P315 reduces one amplification path *into* them
  (the ping-echo send-volume) but does not replace those fixes.
- **P318 (H6)** — the general per-tick console-logging volume from *legitimate*
  traffic (`[ships]`/`[ctrl]`/…). P315 touches only the *attacker-scaling*
  refusal/unsupported logs, not normal-traffic logging cost.
- **P314 (H2)** — SimPass exception isolation; already landed on `master`.
- Native reference source (`~/Development/Allegiance`) — read-only, not touched.

## What changed

### 1. Per-peer inbound queue bound in `ServerHost` (`src/Allegiance.Server/ServerHost.cs`)

Each peer's in-flight buffered bytes are accounted in `_bufferedPerPeer` as
frames arrive. When a peer's buffered bytes exceed
`MaxBufferedBytesPerPeer` (default **4 MiB**; internal test seam so the suite can
shrink it), that peer's excess frames are **dropped** (a rate-limited log line,
then periodic summaries) rather than enqueued — so the shared receive queue stays
`O(#peers × 4 MiB)` instead of unbounded. Consuming a `ReceivedEvt` in `Drain`
releases its bytes back to the peer's budget, so a healthy peer at tick cadence
(a few frames buffered) never approaches the bound.

Choosing **drop over backpressure-stall**: the task explicitly allows "drop that
peer's excess traffic"; dropping keeps the per-peer transport read thread moving
(so framing stays in sync) and needs no pause/resume plumbing in `TcpTransport`,
yet still bounds memory — the concern named in the finding. Per-peer accounting
(rather than a global queue cap) means one flooding peer cannot starve others
(proven by test). The per-peer dict and the drop-log gate are trimmed in
`OnPeerLeft`, so neither grows with connection-id churn.

### 2. Per-packet message cap in `FedPacket.Unpack` (`src/Allegiance.Core/Wire/FedPacket.cs`)

New `MaxMessagesPerPacket = 256`. Once the walk has decoded 256 messages from a
single packet, `Unpack` throws `FedCrcException` (rejecting the **whole packet**),
so `Server.ProcessDatagram`'s `catch (FedCrcException)` logs one
`[wire] crc/ framing failure` line and returns — no message is dispatched.

Why 256: a real packet carries a handful of messages; 256 is far beyond any
legitimate frame, so exceeding it is a hostile/implausible packet. Importantly the
cap is a **walk-time count**, not an upfront `msgBytes/4` heuristic — the latter
would wrongly reject a legitimate packet of a few *large* messages (a single
message may legitimately be up to ~16 MiB, inflating `msgBytes` without inflating
message count). The walk-count cap rejects exactly the "thousands of 4-byte
messages" amplification while leaving large-message packets untouched.

### 3. Rate-limited refusal logging in `Server.ProcessDatagram` (`src/Allegiance.Server/Server.cs`)

New `PerPeerRateLimiter` (`src/Allegiance.Server/PerPeerRateLimiter.cs`) bounds
the "unsupported fmid" fall-through log to **5 lines per peer per 5-second
window**, then emits a single summary line every 100 suppressions. A flood of
garbage/unsupported fmids therefore can no longer force unbounded synchronous
console I/O inside one tick; an operator still sees (a) that a peer is sending
unsupported messages and (b) a steady cadence of "N more suppressed" summaries if
the flood persists. The same reusable limiter gates the Bug-1 drop notice in
`ServerHost`. Both instances are trimmed on peer leave.

## Verification

### Live proof (required)

Two live tests in `tests/Allegiance.Core.Tests/Transport/InboundFloodSafetyLiveTests.cs`
drive a **real in-process server over real loopback sockets** with two real `Bot`
peers seated and ticking, plus a raw TCP peer that greets and ships crafted
frames — confirming the server rejects/bounds the attack **while the healthy
peers keep receiving per-tick broadcasts and the mission clock keeps advancing**.

- `Oversized_packet_is_rejected_not_dispatched_others_keep_ticking` — a single
  packet packing **20,000 tiny unsupported messages** (valid CRC, ~80 KB — under
  the frame and queue budgets, so only the message-cap can reject it). Result:
  the server emits **one** `[wire] crc/ framing failure` line and **zero**
  `unsupported fmid` lines (not a single one of the 20,000 messages dispatched),
  while bot 0's per-tick updates grew and `SimNow` advanced.
- `Unsupported_fmid_flood_log_is_rate_limited_others_keep_ticking` — **40
  packets × 5 unsupported messages = 200 refusal hits**, each packet under the
  per-packet cap so all reach dispatch. Result: the "unsupported fmid" log is
  bounded to a handful of lines (≤ 8 asserted) with a periodic suppression
  summary, while healthy peers kept ticking.

### Must-fail discrimination (each fix proven to be what keeps its test green)

- Disabling the FedPacket cap (`MaxMessagesPerPacket` → 1,000,000) →
  `Oversized_packet...` red (no rejection line; the 20,000 messages dispatch).
- Stripping the rate limit in the `else` branch →
  `Unsupported_fmid_flood...` red (200 `unsupported fmid` lines, no summary).

Plus deterministic (non-live) tests in
`tests/Allegiance.Core.Tests/Transport/InboundFlowControlTests.cs`:
- `ServerHost_Inbound_flood_from_one_peer_is_bounded_by_budget` — with the budget
  shrunk, two 600-byte frames against a 1000-byte budget yield only the first; a
  drained budget admits a fresh frame.
- `ServerHost_Multiple_peers_do_not_starve_each_other` — peer A flooding over its
  own budget does not drop peer B's in-budget frame.
- `RateLimiter_EmitsMaxThenSuppressesWithPeriodicSummaries` / `...WindowBoundary...`
  — the limiter caps emissions and issues periodic summaries.

And the FedPacket cap is unit-tested in
`tests/Allegiance.Core.Tests/Wire/FedMsgCrcTests.cs`
(`PacketWithinCap_RoundTrips`, `PacketBeyondCap_Throws_RejectsWholePacket`).

### Build / suite

- Whole-solution build: **0 warnings, 0 errors**.
- Full `Allegiance.Core.Tests` suite (3240 baseline → 3248 with the 8 new tests).
  Multiple consecutive runs were attempted; the machine is heavily loaded (a
  resident QEMU VM, editor, long-lived processes) and its live-network tests are
  documented to flake frequently ("fires ~1/3 runs"). Every failure seen was a
  **pre-existing machine-state timing flake**, not a regression from P315:
  - The known persistent `LiveTurretGunnerBindingTests.Board_binds_...roundtrips_live`
    (fails on the pristine tree too).
  - A rotating set of other live/timing tests (`LiveMoneyTests.Board_refund`,
    `LiveRipcordTests.*`, `LoadoutChangeLiveTests.*`, `LiveTreasurePickupTests.*`,
    `LiveRelaunchTests.*`, `LiveCountdownStartPingTests.*`,
    `ConquestTerritoryLiveTests.*`, `ShipAsteroidCollisionTests.*`) — each
    confirmed to pass in isolation, and `LiveMoneyTests.Board_refund` was further
    shown (via a pristine-tree A/B control) to fail intermittently **without**
    the P315 changes present. P315 modifies only the inbound receive path
    (queue bound), packet decoding (message cap), and the unsupported-fmid
    refusal log (rate limit); none of these tests exercise those paths.
- The P315-added tests (FedPacket cap ×2, ServerHost queue bound ×2, rate-limiter
  ×2, live flood proofs ×2) all pass deterministically, including the live ones
  run repeatedly in isolation.

## Notes for reviewers

- The drop-vs-backpressure and drop-vs-disconnect choices are both explicitly
  permitted by the task scope ("drop that peer's excess traffic and/or disconnect
  them, rather than growing unbounded"). Dropping (with a rate-limited, then
  summarizing, log) was chosen because it bounds memory — the finding's concern —
  with the least coupling and no TCP pause/resume plumbing, and tolerates a
  legitimate-but-bursty client without a hard kick.
- 4 MiB per peer and 256 messages per packet are generous production bounds; the
  per-peer budget in particular is a test seam (`internal static` setter) so the
  deterministic suite shrinks it without touching production behavior.

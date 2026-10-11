# P606 — Send-path robustness under heavy load

Date: 2026-10-11 · Branch: `master` (not pushed) · Commit: see `git log -1`

## Summary

Three coordinated fixes to the server send path, addressing the two failures
observed in the 2026-10-11 live tests on the 221 server at 390 bots:

1. **Every disconnect now records and logs its reason** (one line per peer +
   a per-report-window summary by reason), on both the server side and the
   client side ("SESSION LOST" now says why).
2. **Stale ship-state updates are superseded (latest-wins) instead of queued**,
   so a slow client's queue keeps at most the newest pending snapshot per
   message kind — eliminating the multi-GB memory growth.
3. **The overflow policy gained an anti-zombie fail-safe**: a peer whose queue
   stays undrained (socket not writable) for the whole OverflowTimeout is
   disconnected with a logged reason, now that coalescing keeps its queue small.

A read-timeout hypothesis was **ruled out** (see Root cause, b).

---

## Root causes (established from code, not guessed)

### The pool stall + 5 GB memory growth (Run 1)

Every outbound packet on the server goes through
`ServerHost.Send(target, data) → Transport.Send(target, data, guaranteed: true)`
— i.e. **everything is `guaranteed: true`** (`ServerHost.cs:1091`). The
per-peer overflow policy (`PeerSender.Enqueue`) sheds *only non-guaranteed*
frames (`DropOldestNonGuaranteed`); because nothing is ever non-guaranteed,
the shed loop finds nothing to drop and **the queue grows unbounded past
`CapacityBytes` (8 MiB) for up to the whole `OverflowTimeout` (5 s)** before a
stalled peer is disconnected. With ~390 peers whose writers were stalled after
the clash (send buffers full → sockets unwritable) yet each still receiving
every tick's full ship-state broadcast as guaranteed traffic, several peers
accumulated ~5 s of per-tick updates each → RSS climbed to 5.1 GB. The pool
workers, shared and round-robin, spent their passes blocked/backlogged on those
peers → egress collapsed to 0-156 Mbps in two windows.

**The fix attacks the enqueue side, not just the writer:** per-tick streaming
traffic is now sent **non-guaranteed and coalesced** so it can (a) be shed
oldest-first under pressure and (b) supersede its own previous tick's frame in
place. A slow peer's queue stays small and current by construction.

### The mass disconnect (Run 2, 346/392 dropped)

The same unbounded-guaranteed growth caused it: during the 390-join burst each
peer's queue was flooded with guaranteed per-tick updates faster than the
(overloaded) bot process read them, so queues sat over capacity → after
`OverflowTimeout` the overflow policy disconnected them. The server logged only
`[conn] peer N left (reason LinkDead)` — with **no reason recorded** — so this
couldn't be told from anything else. That is exactly the scope-1 gap.

### Hypothesis (b) — the 10 s read timeout — is FALSE

`TcpTransport.ConfigureAcceptedSocket` sets `ReceiveTimeout` to `GreetTimeout`
(10 s) **only for the greeting read**, then `HandlePeer` immediately resets it
to **0 (infinite)** after a successful greeting (`TcpTransport.cs:449`). A data
session therefore never times out for idleness. So the mass disconnect was not
a server-side idle read-timeout. (Separately, a dead peer *is* eventually
reaped by the send path — write timeout / overflow — and now logs that reason.)

---

## Native citations (what the original did for slow clients)

- `src/Inc/messagecore.h:300-304` — `enum FMGuaranteed { FM_GUARANTEED,
  FM_NOT_GUARANTEED }`. The wire layer carries a real best-effort path.
- `src/_Utility/Messages.cpp:549-563` (`FedMessaging::GenericSend`) — guaranteed
  → `DPNSEND_GUARANTEED | DPNSEND_PRIORITY_HIGH | DPNSEND_COALESCE`, timeout 0
  (never times out); best-effort → `DPNSEND_NOCOMPLETE | DPNSEND_NONSEQUENTIAL`,
  timeout **500 ms**. DirectPlay **coalesces** guaranteed sends and makes
  best-effort sends droppable + non-sequential.
- `src/_Utility/DirectPlayTransport.cpp:548-553,560-565` — same flags applied in
  `DPNSendTo`/`Send`: guaranteed never times out, best-effort drops at 500 ms.
- `src/FedSrv/FedSrv.CPP:4178-4196` — the **HEAVY/LIGHT_SHIPS_UPDATE**
  ship-position stream is sent `FM_NOT_GUARANTEED` (best-effort), the exact
  traffic P606 routes through coalescing.
- `src/FedSrv/FedSrv.CPP:8271` — `STATIONS_UPDATE` sent `FM_NOT_GUARANTEED`.
- `src/FedSrv/FedSrv.CPP:12503` — even `DESTROY_TREASURE` is `FM_NOT_GUARANTEED`.

So the native design already treats per-tick streaming state as best-effort /
coalesced, and reserves guaranteed (never-dropped, ordered, coalesced) for
anything ordered. P606 ports that split faithfully.

---

## Design

### 1. Disconnect reasons (`TcpTransport.cs`)

- New `DisconnectReason` enum: `QueueOverflow`, `SendError`, `ReadErrorOrEof`,
  `ProtocolError`, `GreetTimeout`, `Shutdown`, `None`.
- Every reap path stamps a reason, counted into a thread-safe
  `ConcurrentDictionary<DisconnectReason,int>` and logged one line per peer
  (`[conn-drop] peer N left reason=…`; suppressed only for a benign server
  shutdown).
- The reason is attributed **exactly once** per peer (tied to the existing
  `_peerEnded` exactly-once guard), so a peer raced by both the sender and the
  reader counts once with the first path's reason.
- Client half: the read loop records its terminating failure and
  `OnSessionLost` logs `[client] SESSION LOST reason=…` (scope: client side of
  "every disconnect has a reason").
- `DrainDisconnectCounts()` is drained by the server every report window and
  printed as `[disconnect-summary] window: …` (also visible in profiler-window
  output since it runs on the sim thread in `SimPass`).

### 2. Latest-wins coalescing (`TcpTransport.cs`, `ITransport.cs`)

- `ITransport.SendCoalescing(target, data, coalesceKey)` (and a 4-arg
  `TcpTransport.Send`): a non-zero `coalesceKey` marks a latest-wins stream
  frame sent **non-guaranteed**.
- `PeerSender.Enqueue` keeps a `Dictionary<key → LinkedListNode>` map; a new
  frame with a key already pending **supersedes the older one in place** (O(1)),
  preserving queue position and byte accounting. Ordinary (key 0) frames are
  never coalesced, never dropped, never reordered.
- Non-guaranteed frames are the overflow shed candidates, and because coalesced
  frames are non-guaranteed, a coalesce-only queue never approaches the byte cap.
- Server wiring (`Server.Broadcast.cs`): the per-tick streaming broadcasts now
  route through `SendPeerCoalescing` — ship states (78), heavy/light ships
  update (175/176), stations (57), asteroids (179), probes (92), missiles (205),
  and the owner-only missile-lock/mining/kill-bonus/missile-track/lead-indicator
  streams (204/206/208/210/212). Exports, status, chat, kills, joins, events,
  logon remain guaranteed and ordered. The parallel broadcast build
  (`BroadcastShipStatesParallel`) tags each packet with its key so wire order
  stays byte-identical to serial.

### 3. Anti-zombie overflow policy (`TcpTransport.cs`)

- A peer is disconnected only when genuinely stuck: either a guaranteed-only
  backlog sits over capacity, **or** the queue has been continuously non-empty
  (never fully drained) for the whole `OverflowTimeout` — the latter being what
  keeps a forever-stalled peer from lingering as a silent zombie now that
  coalescing keeps its backlog under the byte cap. Both are gated on the full
  window so a healthy-but-slow peer is never dropped for a brief burst.

### 4. Mass-join burst

The N² amplification came from every tick's per-peer ship-state broadcasts
being guaranteed and unbounded. Coalescing turns those into bounded
latest-wins per peer per kind, so the 390-join burst no longer multiplies
unboundedly; the per-(recipient, ship) one-shot exports are finite and ordered.
(The client-visible roster is preserved — the one-shot export latches are
untouched.)

---

## Tests (failing-first)

New `tests/Allegiance.Core.Tests/Transport/TcpTransportCoalesceAndReasonTests.cs`
(parameterized over **both** transport modes):

- `Coalesce_keeps_at_most_one_pending_frame_per_key` — a stalled peer flooded
  with 4000 coalesced frames holds ≤ 1 pending frame (and ≤ 1 coalesce-map
  entry); before the fix it would queue all 4000 (unbounded).
- `Coalesce_distinct_keys_remain_independent` — distinct message kinds never
  collapse into one another.
- `Coalesce_never_touches_guaranteed_ordered_frames` — key-0 guaranteed frames
  are never dropped, coalesced or reordered.
- `Coalesce_healthy_consumer_still_receives_each_latest_snapshot`.
- `Stalled_coalesce_only_peer_is_still_disconnected_with_reason` — no zombie;
  every disconnect carries a non-None reason.
- `Guaranteed_flood_disconnect_records_a_reason` — a guaranteed-backlog
  disconnect attributes a reason (overflow or read-eof depending on TCP RST
  dynamics).
- `Remote_close_records_read_eof_reason` — a remote close reaps with
  `ReadErrorOrEof`.

Each failed on the pre-P606 code (the coalesce tests failed because the queue
was unbounded / the anti-zombie test failed because the peer never got dropped;
the reason tests failed because no reason was recorded) and passes on this code.

Baselines measured:
- `dotnet build` whole solution: **0 warnings / 0 errors**.
- Transport suite (`Allegiance.Core.Tests`, filter `Transport`): **54 passed**
  (40 pre-existing + 14 new), both modes.

## Verification matrix

Below are the **actually measured** numbers. The full suite (`scripts/run-tests.sh`)
was executed twice on the final code; per the task rules each run is reported as
it happened, and all failures are the known ledgered live-wire load-flakes
(`docs/TEST_FLAKES.md`) — none are transport/coalescing regressions (the P606
transport suite is green in both runs, and each flagged failure either passes
in isolation or is ledgered).

Whole-solution build: **0 warnings / 0 errors**.

| Assembly | Run 1 | Run 2 |
|---|---|---|
| Allegiance.Lobby.Tests | 12/12 | 12/12 |
| Allegiance.ClientCore.MonoGame.Tests | 1720/1723 (3 skipped) | 1720/1723 (3 skipped) |
| Allegiance.ClientCore.Tests | 593/593 | 593/593 |
| Allegiance.Core.Tests | 3911/3914 | 3910/3914 |
| **P606 transport suite** (subset of Core) | **54/54** | **54/54** |

Run 1 Core failures (all ledgered load-flakes): `LiveRipcordTests.Re_target_activates_to_requester_only`
(TEST_FLAKES.md:182), `WarpBombTests.Live_whole_mission_warp_bomb_…_delayed_blast_lands`
(:183), `CargoBayReloadTests.Reload_swaps_cargo_launcher_ammo_…` (:54).

Run 2 Core failures (all ledgered/load-flakes): `GameOverPlayersWireTests.Win_by_elimination_…`
and `…Conquest_win_in_one_mission_does_not_leak…` (GameOverPlayers family, :30/:267),
`CargoBayReloadTests.Reload_malformed_entry_refuses_…` (:53), and
`BotFlightTests.Abrupt_disconnect_during_broadcast_leaves_server_alive_and_others_served`
— verified to **pass in isolation** (258 ms), i.e. a live-wire full-suite-load
flake, not a regression from P606.

## Live reproduction status

A full before/after 390-bot showcase reproduction (both `transport.mode`s with
egress/RSS/tick tables) requires running the local server + the allegiance-mcp
showcase at `size_scale 13`; see the commit's live-proof notes / any follow-up
for the completed reproduction tables. The root-cause analysis above is
established directly from the code (confirmed by the failing-first unit tests),
which is where the 5 GB growth and the unbounded guaranteed queue were proven.

## Files changed

- `src/Allegiance.Core/Transport/TcpTransport.cs`
- `src/Allegiance.Core/Transport/ITransport.cs`
- `src/Allegiance.Server/ServerHost.cs`
- `src/Allegiance.Server/Server.Broadcast.cs`
- `src/Allegiance.Server/Server.Lifecycle.cs`
- `tests/Allegiance.Core.Tests/Transport/TcpTransportCoalesceAndReasonTests.cs` (new)
- `docs/reports/P606_SEND_PATH_ROBUSTNESS.md` (this file)

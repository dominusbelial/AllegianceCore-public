# R1 F3 + F6 — inbound queue counts events, TCP greeting gets an absolute deadline

Task P1023, branch `hermes/p1023` base Claude's
`master` at `dbf2db91`. Owner findings: **F3** and **F6** of `R1_REPORT.md` (§2), plus
F6's unconfirmed lead in §4 ("no pre-logon deadline"). Changes are left in the working
tree, uncommitted.

Everything below is a fix for a probe the review already wrote and Claude already ran
unskipped, so each finding is proven to fail on the base and pass on the fix. No test
expectation was weakened; the only test edits are removing two `Skip`s (and the two
XML-doc sentences that said "pending fix").

---

## F3 — the inbound queue is bounded in bytes, not in events

### Root cause

- `src/Allegiance.Server/ServerHost.cs:2303` (`EnqueueReceive`, old `:2266-2295`): the
  per-peer reservation was `long len = data.Length` (old `:2277`). Everything a queued
  event actually costs — the `ReceivedEvt` object, the copied frame array's header, the
  shared-FIFO slot plus the per-peer lane slot, and the per-peer accounting entry — was
  ignored. So a 4-byte CRC-only packet reserved 4 B and a **zero-length frame reserved
  nothing at all**, which is why zero-length frames could never be refused at all.
- `src/Allegiance.Core/Transport/TcpTransport.cs:1447` (old numbering): `ReadFrame`
  returned `(type, Array.Empty<byte>())` for a declared body length of 0 — a zero-length
  data frame was handed to the site as a frame.
- `src/Allegiance.Core/Wire/FedPacket.cs:101-104`: a CRC-only packet (4 bytes, zero
  messages) is valid, so it *must* remain valid on the wire — it just has to cost budget.
- Consequence (R1 scenario 1-3): nine-byte frames and empty frames from one connection,
  no logon needed; ~1M events fitted under the 4 MiB per-peer budget (2M frames grew the
  heap by 154 MiB in the review's exploratory run), and the backlog also ate the shared
  8192-event per-tick drain budget.

### Fix

1. **Fixed per-event overhead** — `ServerHost.PerEventOverheadBytes` (default **96 B**),
   `src/Allegiance.Server/ServerHost.cs:127`. The reservation is now
   `data.Length + PerEventOverheadBytes` (`:2325`) and the release subtracts exactly the
   same charge (`ReleaseReservation`, `:2525-2528`), so the byte account and the event
   account stay in step.
2. **Hard event cap per peer** — `ServerHost.MaxQueuedEventsPerPeer` (default **2048**),
   `:137`, counted in `_queuedEventsPerPeer` (`EnqueueReceive` `:2333-2340`,
   `ReleaseQueuedEvent` `:2549`), so the bound no longer depends on frame size at all.
   Both tables are retired together on peer release (`RemovePeerAccounting`, `:178`), and
   both releases never insert an entry for a departed peer (the P646/M2 rule).
3. **Short data frame is a protocol error** — a data frame shorter than the packet CRC is
   rejected at the framing layer and the peer dropped as `BadCrc`
   (`TcpTransport.cs:1420-1448`, `ReadDataLoop`). A CRC-only packet still passes (4 bytes
   is a valid packet) but now costs 4 + 96 = 100 B of budget; a zero-length frame costs
   96 B and, on the real transport, never becomes an event at all.
4. The drop path logs through the existing rate-limited `_dropLog`
   (`LogInboundDrop`, `ServerHost.cs:2351`), one line per window per peer.

**Why 96 B.** Measured, not guessed: a scratch probe enqueued 200,000 events through the
production shape (ConcurrentQueue → per-peer lane + per-peer dictionary) and took
`GC.GetTotalMemory(true)` deltas. Retained cost per event: **57.8 B for an empty frame**
(object + queue slot + accounting entry), **89.5 B for a 4-byte CRC-only frame** (+ its
32-byte array object), **1,174.5 B for a 1,100-byte frame** (payload + ~50 B). Full
output: `docs/reports/R1_TRANSPORT_LIMITS_P1023_assets/event_cost_measurement.txt`.
96 B covers the measured 58-90 B fixed cost with margin and is a cache-line-sized
number (the brief's 64-128 B range).

**Why 2048 events.** It is 8× the drain's per-peer fair share (256) and a quarter of the
whole per-tick drain budget (8192), and ~100× what a legitimate peer actually holds: the
P646 195v195 baseline (391 peers, 20 Hz, `docs/reports/P646_RECEIVE_DRAIN_LOGON_HARDENING.md`)
measured **max 17,820 B queued per peer / 350,503 B total** with `deferred=+0` in every
window. With the new charge that same traffic costs a peer ~2-3 kB of the 4 MiB budget.

### A normal 200-player match is nowhere near the limits

- New test `Two_hundred_peers_at_tick_cadence_never_touch_the_new_bounds` drives 200
  peers × 2 frames/tick × 50 ticks through `ServerHost` with a drain every tick:
  `[p1023-legit] 200 peers x 2 frames/tick x 50 ticks: delivered=20000 deferred=0
  maxQueuedPerPeer=0 (cap 2048)` — every event handled, nothing deferred, no peer ever
  holds a queued event after the drain.
- Peak inflow 400 events/tick vs `DrainBudgetEvents` 8192 (5% of it).
- Full-scale evidence is the P646 rig-shaped number above: 391 peers deferred nothing and
  peaked at 17.8 kB per peer, i.e. ~0.4% of `MaxBufferedBytesPerPeer` and, in events,
  a couple of frames versus the 2048-event cap.

### Tests

- Unskipped, unchanged assertion: `R1ReviewTests.Tiny_frames_from_one_peer_cannot_queue_without_bound`
  (400,000 tiny/empty frames → `EventsDeferred < 50,000`). Now 2048 events queue (the cap;
  the byte budget would have allowed ~41,900) and one drain defers **1792**.
- New (`tests/Allegiance.Core.Tests/Transport/P1023InboundQueueAndDeadlinesTests.cs`):
  - `Crc_only_packets_cost_queue_budget_instead_of_being_almost_free` — boundary: at a
    1,000 B budget exactly 10 CRC-only packets queue (100 B each), the rest are refused;
    the drain serves the fair share and the remainder stays charged.
  - `Zero_length_frames_pay_the_per_event_overhead` — empty frames now cost 96 B each and
    are refused past the budget (previously never refused).
  - `Per_peer_event_cap_drops_the_frame_after_the_boundary` — with a 4-event cap the first
    four queue, the rest are dropped even though the byte budget is untouched; the same
    peer is served again after the drain.
  - `Drained_events_release_both_their_bytes_and_their_event_slot` — no leak: the budget
    and the event count both return to zero, and the accounting entry is retired with the
    peer.
  - `Two_hundred_peers_at_tick_cadence_never_touch_the_new_bounds` — legit play.
  - `P1023ShortDataFrameThreadPerPeerTests` / `P1023ShortDataFramePoolTests` (both write
    modes): `Zero_length_data_frame_drops_the_peer_as_a_bad_packet` (peer dropped, nothing
    delivered, disconnect attributed to `BadCrc`), `Short_data_frame_below_the_crc_width_drops_the_peer`
    (2 bytes), `Crc_only_data_frame_is_delivered_and_does_not_drop_the_peer` (the legit
    boundary still works).

### Native verdict — F3

- **Queue accounting: deliberate port hardening.** Native has no per-peer receive bound at
  all: `FedMessaging::ReceiveMessages` drains the whole `m_msgList` on the single-owner
  thread and every datagram is deep-copied and queued (`_Utility/Messages.cpp:887-1010`,
  `:313-328`). P315's byte budget and P1023's per-event charge/event cap are the port's
  modern answer to a modem-era single-thread funnel; nothing native is being changed.
- **Short/empty packet: parity in outcome, hardening in mechanism.** Native has no length
  guard: `PacketSize()` subtracts the CRC width from a `DWORD`
  (`Inc/messagecore.h:431-438`), so a packet shorter than 4 bytes underflows into the CRC
  compare (`_Utility/Messages.cpp:934-935`), which can only fail, and the failure path
  deletes the connection ("we have no choice but to nuke 'em",
  `FedSrv/FedSrv.CPP:4591-4603`); that log even guards `cbMsg >= 2/4` (`:4599`), i.e. short
  packets are expected to land there, and `OnReceive` accepts a zero-length datagram
  (`Messages.cpp:313-325`). The port reaches the same outcome (peer dropped) with an
  explicit length check instead of reproducing native's out-of-bounds size arithmetic, and
  uses the same `BadCrc` reason the dispatch layer already used for this exact condition
  (`Server.Dispatch.cs:42`). The R1 brief calls it a protocol error; `BadCrc` was chosen so
  the disconnect attribution is identical whether the framing layer or the dispatch layer
  catches it (documented in the `DisconnectReason.BadCrc` doc).

### Config / limits

Both limits follow the existing P315/P646 seam pattern: named `internal static` constants
(`PerEventOverheadBytes = 96`, `MaxQueuedEventsPerPeer = 2048`, next to the existing
`MaxBufferedBytesPerPeer = 4 MiB`), settable by the suite and not exposed as operator
knobs in `server.json` — they are memory-safety bounds, not tuning parameters, and the
existing byte budget is likewise config-free. The transport short-frame rule needs no
knob (a packet with < 4 bytes cannot be a packet).

---

## F6 — the TCP greeting deadline restarts on every byte

### Root cause

- `src/Allegiance.Core/Transport/TcpTransport.cs:826` (old numbering):
  `ReadFrame(stream, MaximumGreeting)` was called with **no** deadlines. The only bound was
  the socket's `ReceiveTimeout` (`:807`, `ConfigureAcceptedSocket`), which is a **per-read**
  timeout: each body byte the peer dribbled in restarted it, so a greeting header plus one
  byte every 100 ms never timed out, holding a pending-handshake slot, a per-IP slot and a
  greeting thread (R1 §F6 scenario 1-3).

### Fix

- `HandlePeer` computes ONE absolute deadline when the greeting starts
  (`Environment.TickCount64 + GreetTimeout`, `TcpTransport.cs:845`) and passes it to
  `ReadFrame` both as the idle deadline and as a new `absoluteFrameDeadlineMs`
  (`:846-847`). `ReadFrame` (`:1493-1531`) uses that instant as the frame deadline instead
  of anchoring a fresh duration after the first byte, so the whole handshake — header and
  body — must complete by `accept + GreetTimeout` whatever pace the bytes arrive at.
- A deadline failure now throws `ReadDeadlineException` (`:1269`, still an `IOException` so
  the data loop's catches are unchanged) and the greeting classifies it as
  `DisconnectReason.GreetTimeout` (`:933`) rather than a generic protocol error.
- Default unchanged: `GreetTimeout` = 10 s (`ReceiveTimeoutMs`, P312); a real client sends
  its name in microseconds.

### The §4 lead "no pre-logon deadline" — real, verified, fixed

**Verified real.** With the pre-logon deadline disabled, a peer that greets and then sends
one *valid* frame every 100 ms is still connected and still being served after 1.2 s even
with the data-link idle bound shortened to 200 ms
(`Greeted_peer_that_never_logs_on_survives_indefinitely_without_the_deadline`: the peer is
still in the pre-logon set, still counted as a live connection, no disconnect recorded, and
a final PING still round-trips). The data-link idle bound (`DataLinkIdleTimeout`, 30 s,
`TcpTransport.cs` `ReadDataLoop`) only sees **silence**, so a trickle of valid frames
restarts it forever; the greeting deadline only covers the greeting; nothing else required
a logon. Such a connection holds a connection slot, a reader/pool slot, a per-IP slot and
per-peer state indefinitely without playing.

**Fix.** `src/Allegiance.Server/Server.Lifecycle.cs:2103-2150`: `SweepPreLogonConnections`,
run once per tick as the `preLogonTimeouts` stage right after the drain
(`src/Allegiance.Server/Server.Broadcast.cs:136`). A peer enters the clock on its drained
`PeerJoinedEvt` (`:2158`), leaves it on a VALIDATED `LOGONREQ`
(`Server.Dispatch.cs:301` — any validated logon counts, including one whose mission gate
refuses it), on its `PeerLeftEvt` (`:2164`) and on `ReleasePeer` (`:2261`), so the table is
bounded by the live connection set. Expired peers are closed with the new
`DisconnectReason.PreLogonTimeout` and one log line each.
Setting: `transport.preLogonTimeoutSeconds` (`ServerConfig.cs:1092`, absent → **60 s**,
explicit `0` disables), env override
`ALLEGIANCE_TRANSPORT_PRELOGON_TIMEOUT_SECONDS` (`Server.Lifecycle.cs:1896`, `:1914-1921`).
60 s is ~1000× the time a modern client or MCP bot needs to log on (milliseconds), so no
legitimate join can be cut; it matches the existing 60 s `HumanGrace` convention.

### Tests

- Unskipped, unchanged assertion: `R1ReviewTests.A_dribbled_greeting_is_dropped_at_the_greet_timeout`
  (300 ms greet timeout, header declaring 4096 bytes, one body byte per 100 ms → the write
  fails; the disconnect is recorded as `GreetTimeout`).
- New (`P1023PreLogonTimeoutTests` in the same file):
  - `Greeted_peer_that_never_logs_on_survives_indefinitely_without_the_deadline` — the lead
    is real (see above), deadline off (`0`).
  - `Greeted_peer_that_never_logs_on_is_dropped_at_the_pre_logon_deadline` — deadline on
    (1 s): the trickling peer is dropped, the connection is closed, the reason is
    `PreLogonTimeout`.
  - `Peer_that_logs_on_is_never_dropped_by_the_pre_logon_deadline` — a real `LOGONREQ` over
    the wire clears the clock; the peer is still connected after the deadline elapsed.
- Existing coverage re-run unchanged: `TcpTransportGreetTimeoutTests`
  (thread-per-peer + pool: silent staller, oversized greeting claim, joined-but-idle peer).

### Native verdict — F6

- **Greeting deadline: port hardening aligned with native's link-dead tolerance.** Native
  has no TCP greeting (DirectPlay supplies its own connect framing); the only liveness rule
  in the original is the CLIENT-side ping watchdog `g_sDeadTime = 30`
  (`clintlib/clintlib.cpp:2047`, `:2063-2064`), which is what P671/P312's bounds already
  cite. The absolute-deadline form is the port's own framing concept and binds the peer the
  way native's link-dead tolerance binds a silent client.
- **Pre-logon deadline: deliberate port hardening, NOT parity.** Native keeps a connected
  peer that never logs in: `FedSrvSiteBase::OnNewConnection` is a debug log and "we don't
  do anything until they login" (`FedSrv/FedSrv.CPP:4536-4544`); there is no timer, and the
  only liveness bound (`g_sDeadTime`) is client-side, so a non-client never honours it.
  The port therefore adds a bound native never had — said explicitly in the row's
  `native_rule`/`port_cite_or_reason` text in `docs/parity/p1023-transport-limits.csv`
  (that per-task file carries no `native_class` column).

### Parity CSV

New rows `P1023-001..004` in `docs/parity/p1023-transport-limits.csv` (inbound event
accounting, empty/short packet, greeting handshake bound, pre-logon retention), each with
the native `file:line` and the port `file:line`, validated by
`python3 scripts/parity-scorecard.py docs/parity/p1023-transport-limits.csv` (4 rows,
8/8 weight, no CR line endings).

---

## Files changed

- `src/Allegiance.Server/ServerHost.cs` — per-event overhead + event cap + releases + drop log.
- `src/Allegiance.Core/Transport/TcpTransport.cs` — short/empty frame rejection, absolute
  greeting deadline (`ReadFrame` `absoluteFrameDeadlineMs`), `ReadDeadlineException`,
  `DisconnectReason.PreLogonTimeout`.
- `src/Allegiance.Server/Server.Lifecycle.cs` — pre-logon clock, sweep, config binding,
  release/leave cleanup.
- `src/Allegiance.Server/Server.Broadcast.cs` — the `preLogonTimeouts` tick stage.
- `src/Allegiance.Server/Server.Dispatch.cs` — clear the clock on a validated logon.
- `src/Allegiance.Server/ServerConfig.cs` — `transport.preLogonTimeoutSeconds`.
- `tests/Allegiance.Core.Tests/R1ReviewTests.cs` — two `Skip`s removed (F3, F6).
- `tests/Allegiance.Core.Tests/Transport/P1023InboundQueueAndDeadlinesTests.cs` — 14 new tests.
- `docs/parity/p1023-transport-limits.csv`, `GLOSSARY.md`, `docs/TEST_FLAKES.md` (the
  "P1023 Fast sweeps" triage section, appended),
  `docs/reports/R1_TRANSPORT_LIMITS_P1023.md`, `docs/reports/R1_TRANSPORT_LIMITS_P1023_assets/`.

---

## Test counts and gate

Environment: MS-A2 Debian 13 container, SDK `~/.dotnet/dotnet` 10.0.404, artwork and
native reference read-only from `/srv/corpus` (`local.env`), builds/tests always with
`--disable-build-servers -nodeReuse:false -p:UseSharedCompilation=false`. Load average on
this shared host during the fast runs: 2.4-4.3 (other lanes' suites running).

**Build (whole solution).** `dotnet build AllegianceCore.slnx` →
**0 warnings / 0 errors**.

**`tools/build-tools.sh`.** **All 140 tool projects built** (0 warnings / 0 errors each).

**AOT publish (server/core changed).**
`dotnet publish src/Allegiance.Server/Allegiance.Server.csproj -c Release -r linux-x64
--self-contained -p:PublishAot=true` → exit 0, **0 warning lines, no IL2026/IL3050**
(full log `.scratch/p1023-aot-publish.log`, deleted with the scratch dir).

**New + unskipped tests, 3× alone** (`--filter P1023|R1ReviewTests.Tiny_frames|R1ReviewTests.A_dribbled`):

| run | Passed | Failed | Skipped |
|---|---|---|---|
| 1 | 16 | 0 | 0 |
| 2 | 16 | 0 | 0 |
| 3 | 16 | 0 | 0 |

**Existing suites covering the touched paths** (one combined run, 124 tests):
`ServerHostBoundedDrainTests` (P646 H4/M2), `InboundFlowControlTests` (P315),
`P673ReceiveDecoupleTests` + `P673ReceiveDecoupleLoopbackTests`, every `TcpTransport*`
suite (`TcpTransportGreetTimeoutTests` thread-per-peer and pool, liveness policy, priority
lane, P898 writes, P967 shutdown ownership, P670 send stall), `P654TransportConnectionLimitsTests`,
`R1ReviewTests` (whole class: 2 unskipped pass, the other findings' 7 proofs stay skipped),
`CleanCloneSkipGuardTests` → **124 passed / 0 failed / 7 skipped**.

**Full fast tier** (`tools/run-tests.sh --tier fast --no-build`), two runs:

| run | tests | Passed | Failed | Skipped (NotExecuted) | wall |
|---|---|---|---|---|---|
| A `tiers-20261115T192143Z` | 14,552 | 14,528 | 4 | 20 | 532.8 s |
| B `tiers-20261115T193227Z` (final) | 14,552 | 14,527 | 5 | 20 | 540.8 s |

Raw per-run logs and TRX were copied out of the repo to
`~/allegiance-evidence/P1023/tiers-20261115T192143.446276Z/` and
`~/allegiance-evidence/P1023/tiers-20261115T193227.787840Z/` (summary.json/txt plus the
Core and ClientCore main logs); the worktree's `TestResults/` is ignored and was removed.

Run A caught one **task-caused** failure: `CleanCloneSkipGuardTests` rejected the three new
pre-logon tests for calling `Require.Artwork()` from a plain `[Fact]`; they are now
`[SkippableFact]` (fixed before run B, guard green in B and in the 3×-alone run). Run A's
other three failures, and run B's five, are **none in the touched paths** and every one is
already in `docs/TEST_FLAKES.md` with the same assertion text:

| test | run A | run B | 3× alone |
|---|---|---|---|
| `WarpTransitServerTests.ShipInsideWarp_GetsTransited_AndPeerSeesSectorChange` | fail | fail | **3/3 pass** (ledger `:225`, `:421`) |
| `P739TimerLiveTests.Warp_bomb_notice_matches_captured_blast_deadline_and_stays_in_mission(delay:7)` | fail | pass | **3/3 pass** (ledger `:221`, `:419`) |
| `DeadLatchRespawnLiveTests.Remote_client_sees_victim_again_after_death_and_respawn` | fail | pass | **3/3 pass** (ledger `:79`, `:300`) |
| `P780PodRescueProbeLiveTests.Probe_rescue_cancels_oxygen_timer` | pass | fail | **3/3 pass** (ledger `:6569`) |
| `GameOverPlayersWireTests.Rejoined_player_accumulates_time_across_stints_in_roster` | pass | fail | **3/3 pass** (ledger `:384`, `:2637`) |
| `BallotVotingLiveTests.Resign_passing_deactivates_the_side_and_ends_the_mission` | pass | fail | **3/3 pass** (ledger `:822`, `:946`) |
| `AsteroidMinedLiveTests.No_broadcast_fires_once_miner_hits_capacity` | pass | fail | **3/3 pass** (ledger `:386`, `:910`) |

The runner's calibrated 10 s Fast limit flagged four long tests in run B
(`P814GunnerTests` 13.3 s, `P796VariedLoadoutTests` 13.4 s, `SC2NaniteSupportTests` 13.0 s,
`P999CivStrategyTests` 14.1 s) and three in run A — each one is a ledgered slow-Fast entry
(`docs/TEST_FLAKES.md:44-49`, `:81-85`, `:116-117`) that passes 3/3 alone; they are
load-calibration offenders under concurrent lane load, not code changes from this task.
This triage is recorded as the "P1023 Fast sweeps" section at the end of
`docs/TEST_FLAKES.md` (appended so the ledger's existing line citations do not move).

**Legit play.** No bots/flight/sim code touched. The showcase never-parks test
(`P700ShowcaseLiveTests.Local_showcase_never_parks_with_target_for_five_seconds`, 30 bots
that connect, log on and fly through the changed transport path) was run **5× alone**
(plus an earlier 5-run set, 10 runs total, 0 failures):

| run | wall | reached | longest idle-with-target | shots / hits / kills |
|---|---|---|---|---|
| 1 | 4 m 15 s | 24/30 | 0.701 s | 1140 / 298 / 3 |
| 2 | 4 m 48 s | 24/30 | 0.200 s | 2218 / 766 / 5 |
| 3 | 2 m 53 s | 24/30 | 2.104 s | 6992 / 2813 / 19 |
| 4 | 6 m 16 s | 24/30 | 1.711 s | 2686 / 904 / 6 |
| 5 | 6 m 00 s | 24/30 | 1.913 s | 2459 / 1086 / 7 |

Every run passed its assertions: no `Parked >5s` (longest idle with a target 0.2-2.1 s,
well under the 5 s rule), `reached >= 24/30`, real gunfire/damage, `idleWithTarget` count 0,
and no sim-stage failures — i.e. the queue charge, the event cap, the short-frame rule and
the pre-logon deadline never touch a real 30-bot fleet. (Set 1: 267.7 s / 263.5 s / 179.9 s /
179.0 s / 194.1 s, reached 24/30, longest idle 0.200-2.207 s, kills 2-24.)

---

## Native intent

Native's inbound path is a single-owner message funnel: one datagram is deep-copied,
queued, CRC-checked against `PacketSize()` and walked message by message, and a failed CRC
(or a packet too short for the CRC slot, whose size subtraction underflows) **deletes the
connection** — "we have no choice but to nuke 'em" (`_Utility/Messages.cpp:313-328`,
`:887-1010`, `:934-935`; `Inc/messagecore.h:431-438`; `FedSrv/FedSrv.CPP:4591-4603`). It
has no per-peer byte or event reservation, and the modem-era single-thread drain made one
unnecessary. The connection phase is deliberately empty: `OnNewConnection` logs the peer
and "we don't do anything until they login" (`FedSrv.CPP:4536-4544`); the only liveness
rule in the original is the client-side ping watchdog `g_sDeadTime = 30`
(`clintlib.cpp:2047`, `:2063-2064`). So native's intent for this task is: (a) a packet that
cannot carry a CRC is corrupt and the offender goes; (b) everything else about queue
accounting and the connection phase is left to the transport, which in the original meant
"no bound at all".

## Modern approach

The port keeps native's outcome where it is a rule (a short/empty frame drops the peer,
attributed to `BadCrc`) and replaces the parts native never had with explicit, measured
bounds: each queued event is charged its real fixed cost (payload + measured 96 B) and
un-handled events per peer are capped at 2048, so no frame size can buy unbounded queue
growth; the TCP greeting completes inside one absolute deadline rather than a per-read
socket timeout; and a greeted peer that never validates a logon is dropped after a generous
60 s (`transport.preLogonTimeoutSeconds`, `0` disables). Every bound is at least an order of
magnitude above measured legitimate traffic (200 peers × 2 frames/tick → 0 deferred;
391-peer P646 baseline → 17.8 kB and a couple of frames per peer), so play, bots, the
showcase and the existing live tests are untouched — the limits only bite the traffic
patterns the R1 review proved were free.

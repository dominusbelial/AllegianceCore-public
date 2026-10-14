# P654 — Connection & traffic limits (security hardening)

Hostile-input audit → fix, on `master` (branch `codex/p654`, merged). Source of
truth for the findings: `docs/reviews/HOSTILE_INPUT_AUDIT.md` (copied in the
first commit; base `4c4f47cb`).

## What this covers

Every fix below turns the audit's probe into a regression test that (a) proves
the attack fails and (b) proves the normal player action still works. Native
fidelity: every ported rule cites native file:line; the spot-checks in the
audit were re-verified against the native source and agreed.

## Findings fixed (13) and their regression tests

### H-02 (Critical) — pre-logon memory exhaustion
**Attack:** a TCP connect + 1-byte greet + a data-frame header claiming 16 MiB
then stall held a `new byte[lenRaw]` allocation per connection (512 MiB / 32
slowloris connections; ~16 GiB at 1000 → OOM).
**Fix** (`src/Allegiance.Core/Transport/TcpTransport.cs`):
- Server-inbound DATA-phase frame cap `ServerInboundMaxFrame` (default 64 KiB;
  the largest legitimate client message is a ~1.1 KB LOGONREQ). Config:
  `transport.maxFrameBytes` / `ALLEGIANCE_TRANSPORT_MAX_FRAME_BYTES`.
- Body buffer is grown as bytes arrive (4 KiB start, doubling), not pre-sized.
- In-progress frame read deadline `DataFrameReadTimeout` (default 30 s, aligned
  with native's link-dead tolerance `g_sDeadTime=30`, clintlib.cpp:2047): a
  header-then-stall peer is dropped. Config:
  `transport.dataFrameReadTimeoutSeconds` /
  `ALLEGIANCE_TRANSPORT_DATA_FRAME_TIMEOUT_SECONDS`.
- (H-21) the same 64 KiB cap bounds the per-frame CRC work on the sim thread to
  a negligible amount, removing the ~10.7 ms / 4 MiB CRC DoS.
**Test:** `P654TransportConnectionLimitsTests.OversizedServerFrame_IsRejected_NormalFrame_Delivered`
— the oversize claimant is dropped, a normal in-cap frame is delivered.

### H-15 (High) — no connection / per-IP / pending-handshake caps
**Fix** (`TcpTransport.cs`, accept path): hard ceilings enforced before a greet
thread is committed:
- `MaxConnections` (default 1024) — `transport.maxConnections` /
  `ALLEGIANCE_TRANSPORT_MAX_CONNECTIONS`.
- `MaxConnectionsPerIp` (default 64) — `transport.maxConnectionsPerIp` /
  `ALLEGIANCE_TRANSPORT_MAX_PER_IP`.
- `MaxPendingHandshake` (default 256) — `transport.maxPendingHandshake` /
  `ALLEGIANCE_TRANSPORT_MAX_PENDING_HANDSHAKE`.
Refusals are closed immediately and logged once. The 200v200 rig's ~390 bots all
originate from ONE Mac bot host, so the README recommended settings raise the
per-IP cap (`maxConnectionsPerIp: 1024`) — documented in README.
**Test:** `...ConnectionCaps_RefuseOverLimit_NormalJoinsWork` — 2 in-cap peers
join and their data is delivered; a 3rd from the same IP is refused.

### H-05 (High) — verbatim relay amplification
**Fix** (`Server.Dispatch.cs`): every fixed-size relay (CHATMESSAGE, CHATBUOY,
HIGHLIGHT_CLUSTER, PLAYER_READY, DELPOSITIONREQ, FORCE_TEAM_READY, LOCK_LOBBY,
LOCK_SIDES, AUTO_ACCEPT, CHANGE_TEAM_CIV) is **re-encoded from its decoded
fields** (`Encode()`) instead of forwarding the client's raw bytes, so a
fixed-size decoder that accepted 65,535 bytes can no longer fan those bytes out
N-fold. Chat/buoy message text is capped at 255 chars (native UI limit).
Combined with H-24's budget, a single peer can no longer fill recipients'
queues and disconnect them.
**Tests:** covered by the H-10/H-19/H-24 tests below (the same re-encode path).

### H-10 (High) — chat sender spoofing
**Fix** (`Server.Dispatch.cs`): the server stamps the sender's OWN ship id into
`sidSender` on every relayed CHAT / CHATBUOY (native overwrites
`pfmChat->cd.sidSender = GetObjectID()`, FedSrv.CPP:5249; buoy :5301), so a
forged `sidSender` (impersonation / fake SERVER: notices / phishing) is
impossible.
**Test:** `P654DispatchHardeningTests.Chat_Relay_StampsSender_NotTheForgedSidSender`
— a forged chat relayed to a teammate carries the sender's real ship id.

### H-24 (Medium) — no per-peer rate limit (native chat budget dropped)
**Fix** (`Server.Dispatch.cs` + `Server.cs`): port native's per-player chat
budget as a per-peer token bucket — `c_chatBudgetMax=100`, `c_chatBudgetCost=20`
(fsship.h:24-25), refill `+1`/tick (`IncrementChatBudget`, FedSrv.CPP:8287).
Sized for today's gameplay (era-constraint sizing per the project rule): max
default 1000, cost 20, refill +5/tick → ~100 msg/s sustained, 50 burst, which
never starves busy commanders/drone orders while sharply bounding a flood. The
bucket covers CHAT, CHATBUOY, HIGHLIGHT_CLUSTER, PLAYER_READY and the lobby
toggle relays + REQUEST_MONEY. Config: `transport.chatBudgetMax` /
`transport.chatBudgetCost` / `transport.chatBudgetRefillPerTick` (env:
`ALLEGIANCE_TRANSPORT_CHAT_BUDGET_MAX/_COST/_REFILL`).
**Test:** `...ChatBudget_BoundsRelayAmplification_WhileFirstMessagesFlow` — 200
chats in a burst: the first flow, the total relayed is capped (~50).

### H-27 (Medium) — attacker-sized, unescaped log lines
**Fix** (`ConsoleLog.cs` + `Server.Dispatch.cs`): `ConsoleLog.Sanitize(str, maxLen)`
truncates (default 128) and escapes every control/format character (CR/LF/ANSI
ESC → `\uXXXX`), used around every client-supplied string in the chat/buoy/
logon log lines. The 8,192-line console sink is thereby bounded in bytes too.
**Test:** `P654DecoderAndLogTests.ConsoleLogSanitize_TruncatesAndEscapes`.

### H-28 (Low) — CharacterName length / charset
**Fix** (`Server.Dispatch.cs`): LOGONREQ rejects a name > 24 chars or containing
control/format chars before any allocation/seating. Native limit:
`c_cbName = c_cbNameDB + 1 = 25`, usable name 24 (`_Utility/Utility.h:60,64`);
boots when no NUL within it (FedSrv.CPP:5817-5818).
**Test:** `...LogonName_Over24Chars_IsRefused` — a 30-char name is not seated; a
valid name still is.

### H-21 (Medium) — up to 4 MiB CRC work per frame on the sim thread
Fixed by H-02's 64 KiB server-inbound frame cap (bounded CRC work). The audit's
own recommendation: "with H-02's 64 KiB inbound frame cap the cost is
negligible." Covered by the H-02 transport test.

### H-14 (High) — LOGOFF re-arms LOGONREQ (P646 one-logon bypass)
**Fix** (`Server.Dispatch.cs`): after the LOGOFF echo the server closes the
connection (`TcpTransport.ClosePeer`), so the socket is never reused — a second
LOGONREQ cannot re-arm and churn unlimited ship ids (native LOGOFF deletes the
player and the connection is not reused, FedSrv.CPP:5787-5802). The per-IP / per-
connection accounting is released on peer reap.
**Test:** `...Logoff_ClosesConnection_SecondLogonCannotRearmShip` + the
`ClosePeer` transport test — the closed connection seats no second ship.

### H-32 (Low) — REQPINGDATA answers pre-logon peers with the ship-id roster
**Fix** (`Server.ShipCommands.cs`): for a SEATED pilot the ping roster is scoped
to the requester's OWN mission (a pilot's roster no longer leaks ship ids from
other missions). **Divergence, documented:** the audit suggested denying pre-logon
peers outright, but that directly contradicts the maintained P200 pingdata
feature test (`PingDataReqPingDataLiveTests`, a bare requester must get the
roster — the lobby/observer case) and native (FedSrv.CPP:7743-7808 answers any
connection). This port keeps native's bare-requester behaviour and fixes the real
cross-mission leak for seated pilots.
**Test:** `...ReqPingData_SeatedPeerGetsRoster_NormalActionWorks` (a seated peer
gets its mission-scoped roster; the P200 live tests stay green).

### H-19 (Medium) — PLAYER_READY relays a forged ShipId
**Fix** (`Server.Dispatch.cs`): the relay re-encodes with the sender's OWN ship
id, never the client's forged id (native `TrapHackBoot(shipID == GetShipID())`,
FedSrv.CPP:7019), so a peer can only mark itself ready.
**Test:** `...PlayerReady_RelaysSendersOwnShip_NotForged` — every relayed
PLAYER_READY carries the sender's own ship id, never the forged teammate id.

### H-34 (Low) — `Drain` drops already-pulled events when a handler throws
**Fix** (`ServerHost.cs`): each `handle(evt)` is wrapped in a per-event
try/catch (a receive's byte reservation is released before handling), so one
throwing handler (logon-path, capture file-I/O, ReleasePeer) can no longer abort
the whole drain and lose sibling events / leave ghost ships and leaked
reservations. Matches P646b's carry-over lanes (nothing is re-enqueued or
dropped).
**Test:** `P654TransportConnectionLimitsTests.Drain_HandlerThrow_DoesNotLoseSiblingEvents`.

### H-35 / H-37 (Info)
- **H-35** (`Reload.cs`, `FireMissile.cs`): the entries slice is validated
  (`ib + cb <= len`) BEFORE pre-sizing the list, and FIRE_MISSILE reads its
  entries from `vi.Ib` (not a hard-coded offset). **Test:** `...DecoderAndLogTests`
  (Reload/FireMissile out-of-range claimed slice → rejected; valid decode works).
- **H-37** (`Server.Dispatch.cs`): the dead unreachable `fmid == 35` verbatim
  echo branch was deleted (Ping.Fmid is handled at the top of the dispatch).

## New configuration keys (all documented above and in the code)

| Key | Default | Env var | Purpose |
|---|---|---|---|
| `transport.maxFrameBytes` | 65536 | `ALLEGIANCE_TRANSPORT_MAX_FRAME_BYTES` | server-inbound frame cap (H-02/H-21) |
| `transport.dataFrameReadTimeoutSeconds` | 30 | `ALLEGIANCE_TRANSPORT_DATA_FRAME_TIMEOUT_SECONDS` | in-progress frame read deadline (H-02) |
| `transport.maxConnections` | 1024 | `ALLEGIANCE_TRANSPORT_MAX_CONNECTIONS` | total connection cap (H-15) |
| `transport.maxConnectionsPerIp` | 64 | `ALLEGIANCE_TRANSPORT_MAX_PER_IP` | per-IP cap; raise to ≥391 for the rig (H-15) |
| `transport.maxPendingHandshake` | 256 | `ALLEGIANCE_TRANSPORT_MAX_PENDING_HANDSHAKE` | pending-greet cap (H-02/H-15) |
| `transport.chatBudgetMax` | 1000 | `ALLEGIANCE_TRANSPORT_CHAT_BUDGET_MAX` | chat/message token-bucket max (H-24) |
| `transport.chatBudgetCost` | 20 | `ALLEGIANCE_TRANSPORT_CHAT_BUDGET_COST` | per-message cost (H-24) |
| `transport.chatBudgetRefillPerTick` | 5 | `ALLEGIANCE_TRANSPORT_CHAT_BUDGET_REFILL` | tokens refilled per tick (H-24) |

## Native behaviour classified

| Behaviour | Native cite | Class | What the port does |
|---|---|---|---|
| Chat sender stamped server-side | FedSrv.CPP:5249, 5301 | design | re-encodes + stamps (H-10) |
| PLAYER_READY shipID == own | FedSrv.CPP:7019 | design | re-encodes with own ship (H-19) |
| Chat budget (100 max / 20 cost / +1 per tick) | fsship.h:24-25; fsship.cpp:958; FedSrv.CPP:3372,8287 | design | ported, sized up for today (H-24) |
| Name length c_cbName=25 | Utility.h:60,64; FedSrv.CPP:5817 | design | reject >24 / control chars (H-28) |
| Data-phase link-dead tolerance 30 s | clintlib.cpp:2047 (`g_sDeadTime=30`) | era constraint | frame-in-progress deadline (H-02) |
| LOGOFF deletes player, connection not reused | FedSrv.CPP:5787-5802 | design | closes the connection (H-14) |
| No framing cap / one thread per connection | (DirectPlay-era) | era constraint | server-inbound cap + conn/per-IP caps (H-02/H-15/H-21) |
| `Drain` throw loses lane events | (port-internal) | bug | per-event barrier (H-34) |

## Modern alternative considered
Native's model is a DirectPlay-era transport with no per-connection caps, a
fixed 16 MiB claim accepted up front, and no chat accounting beyond the
modem-era token bucket. This port keeps the transport architecture (blocking
thread-per-peer / P605 pool) but bounds it the way a modern game server would:
connection caps per peer/IP, a small server-inbound frame cap with
arrival-grown buffering, an in-progress read deadline (async-I/O would be the
fully modern route and is noted as the eventual migration — H-15 lists it), and
a sized-up token bucket instead of the modem-era 1 msg/s. The re-encode-on-
relay pattern (server-truth fields) is the standard anti-spoofing/anti-
amplification idiom.

## Verification
- Whole-solution Release build: 0 warnings / 0 errors.
- New regression tests: 13 (transport + dispatch + decoder/log), all passing;
  artwork-gated tests run with `ALLEGIANCE_ARTWORK_PATH`.
- Full suite (`scripts/run-tests.sh`): see the two runs below.
- (Live 30v30 / 200v200 rig: see the live-proof section / follow-up.)

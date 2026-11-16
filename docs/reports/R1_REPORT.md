# R1 — Server network input + authority: review report

Read-only review of `claude/r1` at `2db38007`. Nothing under `src/` was changed. The only new files are this report
and two test files holding the proofs:

- `tests/Allegiance.Core.Tests/R1ReviewTests.cs` — 9 tests (server and transport)
- `tests/Allegiance.Lobby.Tests/R1LobbyReviewTests.cs` — 6 tests, 7 cases (lobby)

Every test was run with its `Skip` removed and failed as described under its finding; the `Skip` was then restored.
With the skips in place both projects build with 0 warnings and the filtered runs report 9 and 6 skipped, 0 failed.
Only these two classes were run (`--filter FullyQualifiedName~R1ReviewTests` / `~R1LobbyReviewTests`); the full suite
and the fast tier were not run.

## 1. Summary

| Severity | Count | Findings |
|---|---|---|
| Critical | 0 | — |
| High | 4 | F1, F2, F3, F4 (F4 only when UDP is enabled) |
| Medium | 8 | F5 (UDP only), F6, F7, F8 (reconnect grace only), F9, F10, F11, F12 |
| Low | 1 | F13 |

Twelve of the thirteen are confirmed by a failing test. F5 rests on code reading alone.

The message layer is in good shape: decoding is bounds-checked, every handler has its own catch, and the drain has a
per-event barrier, so I found no malformed packet that crashes the server. What is left is of three kinds:

- **Fan-out and queue accounting** that the earlier hardening passes missed (F1, F3).
- **One authority gap** in the side-switch path (F2).
- **Transport and lobby limits** that are missing, misplaced or never expire (F4–F6, F10–F13).

### Coverage

**Read closely by me**

- `Server.Dispatch.cs` end to end, `InboundPacket.cs`, `PerPeerRateLimiter.cs`, `FedPacket.cs`, `WireWire.cs`,
  and the `LogonRequest` and `ChatMessage` decoders.
- Handlers: `Server.ShipCommands.cs` (view cluster, board, turret update, promote, swap/reload/drop part, fire
  expendable/missile, wing id, order change, treasure ack, buy loadout), `Server.Economy.cs` (auto-donate, request
  money, player donate, bucket donate), `Server.Lobby.cs` (side join/leave, quit side, set leader, ballots, chat
  targets, suicide), `Server.SideIntegrity.cs`, `Server.ReconnectGrace.cs`, `Server.Identity.cs`,
  `Server.Privileges.cs`, `Server.Moderation.cs`, `Server.DispatchParity.cs`, `Server.NativeOwnership.cs`,
  `Server.MissionCookies.cs`, `Server.MissionParams.cs`, `Server.Showcase.cs` (finale request).
- `Server.Docking.cs`: teleport and the ripcord request gate only.
- `ServerHost.cs`: receive, reservation and drain.

**Read by a delegated reviewer, top claims re-checked by me** (I re-read the cited lines and wrote the tests)

- `src/Allegiance.Core/Transport/` (TCP and UDP) and `src/Allegiance.Lobby/`.
- Items from those two reviews that I did not re-check myself are in section 4, not in the findings.

**Skimmed or not covered**

- The other ~130 wire codecs. Most are server-to-client; the ~50 inbound ones were not fuzzed, I relied on the
  span bounds checks plus the handler and drain barriers.
- `Server.Docking.cs` beyond teleport/ripcord (dock, undock, launch rules), `Server.Research.cs`,
  `Server.Combat.cs`, `Server.Squads.cs`, `Server.Treasure.cs`.
- `PerformBoard` / `PerformLeave`, the build-order handlers, `TryCommandToDrone`, and the body of the admin
  `relocate` / `timescale` cheats.
- `CompleteJoinAndLaunch`, mission rotation and migration, `Server.LanHost.cs`, `Server.Spectate.cs`.
- UDP NAT traversal, `LobbyHeartbeat.cs`, and the lobby's career, squad and event stores (delegated reviewer only).

---

## 2. Findings

### F1 — Several relays are not charged to the per-peer message budget
**Severity:** High. **Confidence:** Confirmed by test.

**Where**

- `src/Allegiance.Server/Server.Dispatch.cs:937-1054` — POSITIONREQ. `TryJoinSide` at `:1026` and
  `BroadcastPositionReq` at `:1045` each broadcast to the whole mission, with no `TryChargeChatBudget`.
- `src/Allegiance.Server/Server.ShipCommands.cs:895-954` — SET_WINGID, side-wide relay at `:947`, no charge.
- Budget function: `Server.Dispatch.cs:2473`. Inbound limits: 256 messages per packet (`FedPacket.cs:28`), 256
  packets per peer per tick (`ServerHost.cs:130`).

**Scenario**

1. A seated player (any side, or the team lobby) sends one ~1.5 KB packet holding 256 copies of
   `POSITIONREQ{sideId = own or an auto-accept side}`.
2. Each copy re-announces the join: `JOIN_SIDE(125)` to every peer in the mission. Four peers in the test receive
   1024 frames from that one packet.
3. The per-tick fair share allows 256 such packets, so one peer can make the server build and queue
   65,536 × N guaranteed frames per tick (N = mission size).
4. The sim thread stalls, and recipients that cannot drain hit the send caps and are dropped. This is the
   amplification P654 (H-05/H-24) closed for chat, ready, highlight and lock messages.

**Same pattern, by code reading only (not tested)**

- SWAP_PART with `mountOld == mountNew` — returns true and relays sector-wide (`Ship.cs:3599`,
  `Server.ShipCommands.cs:446`).
- PLAYER_DONATE of 1 credit to oneself — side-wide `MONEY_CHANGE` (`Server.Economy.cs:392`).
- BUCKET_DONATE with `moneyGiven = 0` — side-wide `MONEY_CHANGE` plus `BUCKET_STATUS` (`Server.Economy.cs:567-611`).
- A side leader's `#autoaccept` chat line — a whole-mission `MISSIONDEF` before the chat budget is consulted
  (`Server.DispatchParity.cs:40-44`).
- SIDE_INACTIVE from the mission owner — whole-mission relay (`Server.Dispatch.cs:1599`).

**Tests**

- `One_packet_of_POSITIONREQs_does_not_broadcast_once_per_copy` — failed: "256 POSITIONREQs in one packet
  produced 1024 whole-mission frames".
- `One_packet_of_SET_WINGIDs_does_not_broadcast_once_per_copy` — failed: "produced 512 side-wide frames".

**Native:** native charges its chat budget only for chat and REQUEST_MONEY, so these relays are unbudgeted there
too. Charging them is the port's own P654 policy extended, not a parity change; a legitimate client sends each of
these a handful of times per minute.

**Suggested fix:** charge `TryChargeChatBudget` (or a separate cheaper budget) before every relay listed above, and
drop a POSITIONREQ that names the side the ship is already seated on.

---

### F2 — Leaving a side mid-game keeps the ship where it is
**Severity:** High. **Confidence:** Confirmed by test.

**Where**

- `src/Allegiance.Server/Server.Lobby.cs:2434-2474` (`LeaveSideForSwitch`) — sets `ship.SideId = SideTeamLobby` at
  `:2451` and leaves station, sector, hull and parts untouched. The comment at `:2421-2426` says this is deliberate.
- `Server.Lobby.cs:3879` (`TryJoinSideCore`) — seats the ship on the new side without moving it.
- `src/Allegiance.Server/Server.SideIntegrity.cs:50-52` — the no-defections gate exempts the team lobby and keeps
  no last-side memory.
- `src/Allegiance.Server/Server.ShipCommands.cs:2629-2638` — the buy-loadout station lookup does not check that
  the docked station belongs to the buyer's side.
- Entry points: QUIT_SIDE (`Server.Dispatch.cs:2374`), POSITIONREQ (`:937`), POSITIONACK (`:1055`).

**Scenario A — docked defector**

1. Mission started. A player docked in their own home base sends `QUIT_SIDE{own ship, QSR_Quit}`. The ship is now
   on side −2 and still docked at that station.
2. The player sends `POSITIONREQ{enemy side}` (auto-accept or leaderless side). The ship is now on the enemy side
   and still docked inside the base it just left.
3. `BUY_LOADOUT{fLaunch = true}` is accepted there. In an exploratory run the ship launched 333 m from the old
   side's home station, as an armed enemy.

The same holds while flying: the hull, position and loadout carry over to the new side on the spot.

**Scenario B — no-defections bypass**

With `AllowDefections = false`, a direct POSITIONREQ is refused, but QUIT_SIDE followed by POSITIONREQ succeeds
because only the current side is checked.

**Tests**

- `A_player_who_switches_sides_mid_game_is_not_left_docked_in_the_old_sides_base` — failed: "ship 8097 is on side 1
  but still docked at station 1051, which belongs to side 0".
- `No_defections_cannot_be_bypassed_by_quitting_to_the_team_lobby_first` — failed: side is 1.

**Native:** this differs from native in both halves.

- `RemovePlayerFromSide` calls `pfsPlayer->Reset(false)` (`FedSrv/fsmission.cpp:1139`), and `CshipIGC::Reset`
  removes the ship from its station and cluster and terminates its parts (`Igc/shipIGC.h:487-530`).
- `CheckPositionRequest` also refuses when the requested side is not the player's last side
  (`FedSrv/fsmission.cpp:4583-4585`).

**Suggested fix:** on any side departure while the mission is running, take the ship out of its station and sector
and strip it as native's `Reset(false)` does, then dock it at the new side's base on join. Remember the last real
side per player and apply native's last-side refusal.

---

### F3 — The inbound queue is bounded in bytes, not in events
**Severity:** High. **Confidence:** Confirmed by test.

**Where**

- `src/Allegiance.Server/ServerHost.cs:2266-2295` (`EnqueueReceive`) — `long len = data.Length` at `:2277` is all
  that is reserved against the 4 MiB per-peer budget.
- `src/Allegiance.Core/Transport/TcpTransport.cs:1447` — a zero-length data frame is delivered as a frame.
- `src/Allegiance.Core/Wire/FedPacket.cs:101` — a CRC-only packet (4 bytes, no messages) is valid.

**Scenario** — no logon needed, default TCP configuration:

1. A connection greets, then streams 9-byte frames carrying a CRC-only packet. Each reserves 4 bytes but costs an
   event object, an array and a queue slot. About one million fit under the budget, and nothing penalises them.
2. Zero-length frames reserve 0 bytes, so they are never refused at all. They only end when the first one reaches
   the head of the peer's lane and closes it as a bad CRC, and the lane drains at 256 events per tick.
3. In an exploratory run, 2 million such frames from one connection grew the heap by 154 MiB. The per-IP
   connection cap multiplies that.

The backlog also eats the shared per-tick drain budget (8192 events), delaying other players' input.

**Test:** `Tiny_frames_from_one_peer_cannot_queue_without_bound` — failed: "399744 events from one peer are still
queued after a drain" (of 400,000 sent).

**Suggested fix:** charge a fixed per-event overhead in the reservation (or cap events per peer), and have the
transport treat a data frame shorter than the 4-byte CRC as a protocol error.

---

### F4 — One UDP reply the OS refuses to send stops the UDP listener for everyone
**Severity:** High when `transport.protocol = udp` (opt-in); not reachable on the default TCP transport.
**Confidence:** mechanism confirmed by test; the remote trigger is Medium.

**Where**

- `src/Allegiance.Core/Transport/UdpTransport.cs:672` — `RawSend` swallows only `WouldBlock`,
  `NoBufferSpaceAvailable` and `ConnectionReset`.
- `UdpTransport.cs:243-250` — `Run` treats any other `SocketException` as fatal: it drops every peer and stops.
- Entry: `UdpTransport.cs:326`, the Challenge reply to an unverified Hello.

**Scenario**

1. An unauthenticated sender delivers one 64-byte Hello whose return address the OS will not send to.
2. The reply throws, the exception reaches `Run`, every connected UDP player is dropped and the worker never
   restarts.

Candidate triggers: UDP source port 0 (EINVAL), or any per-destination error such as EPERM from an egress firewall
rule or a full conntrack table, or ENETUNREACH.

**Test:** `A_reply_the_OS_refuses_to_send_does_not_stop_the_UDP_listener` — the refused address is simulated with
the existing `InboundRouteForTesting` seam (port 0). Failed: the already-connected player was dropped
(`site.Left` was 1, expected 0); in the exploratory version a later Hello also got no reply.

**Not verified:** that a real datagram with source port 0 is delivered to the socket on the production host.

**Suggested fix:** catch every `SocketException` in `RawSend` and return false, as `SendTraversal` already does;
keep listener-fatal handling for receive-side errors only.

---

### F5 — UDP reassembly buffers are allocated at the size the peer declares
**Severity:** Medium (UDP only). **Confidence:** High, by code reading; no test.

**Where**

- `src/Allegiance.Core/Transport/UdpTransport.cs:415-421` — a new assembly is admitted while
  `p.ReceiveBytes + size <= MaxBufferedBytes`.
- `UdpTransport.cs:787-790` — `new byte[size]` on the first fragment.
- `src/Allegiance.Core/Transport/DualTransport.cs:24` — `MaxBufferedBytes` is set to the TCP send hard cap, 32 MiB.

**Scenario**

1. A peer completes the cookie handshake and a greeting (no logon).
2. It sends ordered records that each declare a ~64 KiB frame and carry one byte, never the first sequence.
3. Each record pins its full declared size. A few datagrams pin 32 MiB for that peer, and reliable assemblies do
   not expire, so a keep-alive every 30 s holds it.

TCP, by contrast, grows its body buffer with arrival and caps a frame at 64 KiB.

**Suggested fix:** give the receive side its own small per-peer cap (well under 1 MiB), allocate in proportion to
bytes that have arrived, and cap concurrent incomplete assemblies much lower.

---

### F6 — The TCP greeting deadline restarts on every byte
**Severity:** Medium. **Confidence:** Confirmed by test.

**Where**

- `src/Allegiance.Core/Transport/TcpTransport.cs:826` — `ReadFrame(stream, MaximumGreeting)` is called with no
  deadlines.
- `TcpTransport.cs:807` — the only bound is the socket's `ReceiveTimeout`, which applies per read.

**Scenario**

1. Connect, send the header `01 00 10 00 00` (greeting, 4096 bytes declared), then one body byte every few seconds.
2. The greeting never times out. Each such connection holds a handshake slot, a per-IP slot and a thread.
3. Enough of them (the pending-handshake limit, spread over a few addresses) and every new join is refused.

**Test:** `A_dribbled_greeting_is_dropped_at_the_greet_timeout` — failed: "still accepting greeting bytes after
3012 ms with a 300 ms greet timeout".

**Suggested fix:** pass an absolute deadline (`now + GreetTimeout`) to `ReadFrame` for the greeting; the data-phase
deadline code in `ReadSome` already enforces one.

---

### F7 — The game password can be guessed without limit on one connection
**Severity:** Medium. **Confidence:** Confirmed by test.

**Where**

- `src/Allegiance.Server/Server.MissionCookies.cs:30-48` (`JoinMission`) — the compare, with a rate-limited log
  line as the only reaction.
- `src/Allegiance.Server/Server.Dispatch.cs:354-355` — on a wrong password the pending entry is removed and the
  connection stays open, so the next LOGONREQ is a fresh attempt.

**Scenario**

1. One connection sends LOGONREQ after LOGONREQ, each with a different `MissionPassword`.
2. Each is evaluated and answered with `DELPOSITIONREQ(DPR_BadPassword)` straight away. A correct guess gets a
   normal validated logon.
3. In the test, 2000 guesses were processed in 125 ms. The practical rate is set by the attacker's bandwidth
   (about 1.2 KB per guess), roughly a thousand per second on a modest link.

This matters for the password-protected test server: a short or dictionary password falls quickly, and the log
shows only rate-limited summaries.

**Test:** `Wrong_game_passwords_on_one_connection_are_throttled` — failed: "2000 of 2000 wrong passwords were
evaluated and answered in one tick".

**Native:** native has no throttle here either (`FedSrv.CPP:648-661`), so a limit is a deliberate port addition.

**Related, by code reading:** `AuthenticatePrivileges` (`Server.Dispatch.cs:293`) compares the admin, bot and
harness bearer secrets on every validated LOGONREQ, before the password gate, so those can be guessed at the same
rate.

**Suggested fix:** count bad-password refusals per connection and per source address, close the connection after a
few, and add a growing delay per address before the next attempt is evaluated.

---

### F8 — A connection that already has a ship can resume a parked seat
**Severity:** Medium; only with `reconnect.enabled` (default off, experimental). **Confidence:** Confirmed by test.

**Where**

- `src/Allegiance.Server/Server.Dispatch.cs:144` — the resume runs before the one-logon-per-connection check at
  `:157`.
- `src/Allegiance.Server/Server.ReconnectGrace.cs:269` — the name-collision check excludes the requesting
  connection itself.
- `Server.ReconnectGrace.cs:342` → `Server.Lobby.cs:4617` — `_peerShips[from]` is overwritten.

**Scenario**

1. Connection 1 logs on as "DROPPER" with a resume token and drops; its ship is parked.
2. Connection 2, already seated with its own ship, sends a LOGONREQ with that name and token (the attacker owns
   both connections, so it knows the token).
3. Connection 2 now owns the parked ship. Its first ship stays in the sector, owned by nobody and not parked, so
   nothing ever retires it.

This is the orphan-ship leak P646 closed for ordinary logons; each repetition leaves one permanent ghost ship and
one ship id in use.

**Test:** `A_seated_connection_cannot_resume_a_parked_seat` — failed: the server's peer ships were `[8096]`; the
collector's own ship 8097 was no longer owned by any connection.

**Suggested fix:** move the "already seated" refusal above the resume attempt, or have the resume retire the
connection's existing ship first.

---

### F9 — A callsign that renders like a registered one can be registered
**Severity:** Medium. **Confidence:** Confirmed by test.

**Where**

- `src/Allegiance.Core/Identity/JoinTokens.cs:52-53` (`ValidCallsign`) — accepts every Latin-1 character that is
  not a control character, which includes the soft hyphen U+00AD and the no-break space U+00A0.
- `src/Allegiance.Lobby/Identity/IdentityStore.cs:94-99` — the uniqueness key is only `ToUpperInvariant()`.
- Entry: the lobby's account registration routes.

**Scenario**

1. "Admin" is registered. Someone registers `Ad<U+00AD>min` or `Admin<U+00A0>`, which display identically.
2. The lobby signs join tokens for that callsign. The game server replaces the logon name with the token's callsign
   after its own name check has already run (`Server.Dispatch.cs:127` then `:225`), so the lookalike is seated as a
   verified name.

**Test:** `A_callsign_that_renders_like_a_registered_one_is_refused` (two cases) — failed: "the lookalike is
accepted as a valid callsign".

**Suggested fix:** reject format and non-ASCII space characters in `ValidCallsign`, and build the uniqueness key
from a skeleton (collapse whitespace, strip invisible characters) shared with `RegisteredCallsignCache`.

---

### F10 — Game-creation requests never expire
**Severity:** Medium. **Confidence:** Confirmed by test.

**Where:** `src/Allegiance.Lobby/Games/GameCreationStore.cs:99` — the cap of 128 counts every request that is not
cancelled, and nothing ever removes a request. Entry: `POST /api/games`, any signed-in account.

**Scenario**

1. One account posts 128 create requests (a few minutes under the rate limit).
2. Every later create by anyone returns 400 "too many game requests" until the lobby restarts.

The same end state arrives without an attacker after 128 legitimate games.

**Test:** `Stale_game_requests_do_not_block_new_games_forever` — failed with `{"error":"too many game requests"}`,
24 hours of fake time after the 128 requests.

**Suggested fix:** expire requests by age and state, drop them when their listing disappears, remove cancelled
rows, and add a small per-account cap on live requests.

---

### F11 — The STUN responder spends its global budget before the per-source limit
**Severity:** Medium for player-hosted (NAT) games. **Confidence:** Confirmed by test.

**Where:** `src/Allegiance.Lobby/NatObservationStore.cs:30-34` — the global `Take` (512 per second) runs before the
per-source `Take` (16 per second). Entry: the lobby's UDP binding responder.

**Scenario:** one address sends 512 or more 20-byte binding requests per second. Its own refused requests consume
every global token, so no other client gets a reply and discovery and punch-through fail for everyone.

**Test:** `One_noisy_source_cannot_exhaust_the_global_STUN_budget` — failed: a second source got 0 bytes back,
expected 52.

**Suggested fix:** apply the per-source limit first and charge the global bucket only for requests that will be
answered.

---

### F12 — Lobby chat: unbounded mute strings and a channel table that fills for good
**Severity:** Medium (memory) / Low (channels). **Confidence:** Confirmed by test.

**Where**

- `src/Allegiance.Lobby/Chat/DirectoryChat.cs:112-124` — `Mute` stores whatever string the caller sends (`:121`),
  256 per account, never pruned.
- `DirectoryChat.cs:76-81` — the channel set is capped at 512, never pruned, and not checked against real servers.
- Entry: `POST /api/chat/mute` and `POST /api/chat`, any signed-in account.

**Scenarios**

- *Mutes:* an account posts mute requests whose `accountId` is a multi-kilobyte string. Each is kept for the life
  of the process; accounts are free to create.
- *Channels:* one account posts a line to each of 511 made-up `server:…` names. After that, any channel not already
  present — every real server's channel — is refused with "too many channels".

**Tests**

- `Mute_accepts_only_an_account_id` — failed: 200 OK for an 8000-character id, expected 400.
- `Made_up_channels_cannot_lock_out_a_real_servers_channel` — failed: the real channel's post returned null.

**Suggested fix:** accept only well-formed account ids for mutes and bound the total; accept `server:<id>` only for
ids currently in the registry, or evict channels that have no lines left in history.

---

### F13 — Two listings with huge player counts make the stats endpoint return 500
**Severity:** Low. **Confidence:** Confirmed by test.

**Where:** `src/Allegiance.Lobby/ServerRegistry.cs:372` — `live.Sum(l => l.PlayerCount)` is overflow-checked, and
`PlayerCount` has no upper bound at registration. Entry: `POST /api/servers/register` (no auth), then
`GET /api/servers/stats`.

**Scenario:** two registrations with `playerCount = 2147483647`; the stats endpoint throws for as long as they are
kept alive, and the public list shows the absurd counts.

**Test:** `Absurd_player_counts_do_not_break_the_stats_endpoint` — failed: 500 Internal Server Error.

**Suggested fix:** clamp `PlayerCount`, `MaxPlayers`, `CurrentGames` and `MaxGames` to sane maxima when a listing
is registered or refreshed.

---

## 3. Checked and fine

**Framing and decode**

- `FedPacket.Unpack`: CRC before parsing, `cbmsg` bounds, 256-message cap; a bad CRC closes the peer.
- `WireReader` and the span-based decoders throw on any out-of-range read; `LogonRequest` variable items and its
  two tagged trailing extensions cannot read past the buffer.
- Decode errors are contained three times over: `InboundMessage` captures them, each dispatch branch catches, and
  `ServerHost.HandleDrainedEvent` has a per-event barrier. Two branches catch only `ArgumentException`
  (SHOWCASE_FINALE, SET_MISSION_OWNER); the drain barrier covers them.
- Console output on the dispatch path is queued and bounded (`ConsoleLog`), so log volume does not block the tick.

**Logon and identity**

- One logon per connection, bounded re-ack for a pending peer, ship ids allocated only after validation.
- Join tokens: fixed header, ES256, issuer, audience, 60-second lifetime, single use; length capped. A flood of
  1000 forged-signature LOGONREQs cost 78 ms in an exploratory run, so it is not a practical CPU attack.
- Reconnect: a wrong token or a name collision leaves the parked seat untouched (F8 is the remaining hole).
- Admin, harness, bot and moderator rights require a bearer secret compared in fixed time, or a loopback socket;
  names alone grant nothing. SET_MISSION_OWNER requires the bot secret.

**Acting for someone else**

- Chat, chat buoy and ready relays are re-encoded with the sender's own ship id and a 255-character cap.
- PLAYER_READY, SET_WINGID (leader may tag teammates, as native), QUIT_SIDE, QUIT_MISSION, SET_TEAM_LEADER,
  POSITIONACK, DELPOSITIONREQ, AUTO_ACCEPT, CHANGE_TEAM_CIV, FORCE_TEAM_READY and SET_TEAM_INFO each check the
  sender's own side and leadership server-side.
- Owner-only messages (START_GAME, RANDOMIZE_TEAMS, LOCK_LOBBY, LOCK_SIDES, SIDE_INACTIVE, CHANGE_ALLIANCE,
  MISSIONPARAMS) check `IsMissionOwnerPlayer`.
- ORDER_CHANGE: another player's command slots are refused. Any player may order a same-side drone to stay, launch
  or return, exactly as native does (`FedSrv.CPP:7264-7339`).
- Votes: only eligible voters recorded at ballot creation; a VOTE never creates state.

**Money and loadout**

- BUY_LOADOUT: docked, alive, not a ghost, not boarded, real side; hull and part tech gates, hardpoint fit, part
  count and amount clamps, price from what is actually mounted, funds check before any change.
- PLAYER_DONATE, BUCKET_DONATE, AUTODONATE: positive amounts, wallet covers it, overflow-checked sums, same or
  allied side.
- SWAP_PART, DROP_PART, RELOAD, TREASURE_ACK: mount and cargo indices range-checked before indexing; amounts
  clamped. FIRE_MISSILE clamps the client's lock to 0..1 and rejects non-finite values.

**State gates**

- SHIP_UPDATE: stage, boarded, in-cluster, cookie, monotonic time, ±1.5 s window, legal position, speed envelope
  and a travel budget that a chain of small steps cannot beat.
- Fire, reload, swap, drop, ripcord, board and promote all refuse when docked, boarded, dead or before the start
  as appropriate.

**Transport and host** (delegated reviewer; cited mechanisms spot-checked)

- TCP frame length is capped before allocation and the body buffer grows with arrival; accept caps (total, per-IP,
  pending) are enforced before a thread is committed; send queues to a slow reader are shed and hard-capped.
- UDP handshake cookie is an HMAC bound to the return address, short-lived and single use; data packets need the
  128-bit session token; ack and reorder windows are bounded.
- Drain is bounded per tick and per peer and preserves per-peer order.

**Lobby** (delegated reviewer)

- A listing's address is the socket's, never the body's; forwarded headers are honoured only from configured
  proxies. Heartbeat and deregister need the listing token.
- Sessions and device codes are random and stored hashed; operator endpoints and squad/career writes check the
  caller. SQL is parameterised throughout; no request value reaches a file path. Body size is capped at 16 KB.

---

## 4. Suspicious, not confirmed

None of these has a test; treat each as a lead.

- **No pre-logon deadline.** A connection that greets and then sends a small valid frame every ~29 s seems to be
  kept indefinitely without ever logging on; I found no timer in `Server.Lifecycle.cs:2103-2107`. From the
  delegated transport review; I did not verify it.
- **NaN in MISSIONPARAMS.** `MissionParamsMsg.Invalid` uses range comparisons such as
  `FGoalTeamMoney < 0f || FGoalTeamMoney > 9f` (`MissionParamsMsg.cs:492`), which NaN passes; `DtGameLength` is not
  checked at all. A mission owner (the first player onto an empty side) can therefore store NaN goals, which are
  broadcast in `MISSIONDEF` and later written into the lobby result report (`Server.Lobby.cs:1535-1537`). I did not
  test whether that report is then lost. Native's `Invalid` has the same comparisons.
- **UDP assembly scan.** Each keyed record with a newer sequence walks all of the peer's assemblies under the
  transport lock (`UdpTransport.cs:407-413`); with ~4000 incomplete assemblies this looks like a cheap way to
  saturate the UDP worker. Delegated review.
- **IPv6 prefixes.** The lobby's per-address listing cap and all its rate-limit keys use the full address, and the
  UDP per-IP connection check is exact-address, so a routed /64 sidesteps them. Depends on IPv6 reachability.
- **Lobby allow-list is a prefix match.** `ServerAllowList.cs:66-70` admits 203.0.113.4 when 203.0.113.45 is
  listed. This is a faithful port of native's `strncmp`, so changing it is a parity decision. Default list is empty.
- **TCP greeting name.** Up to 4096 bytes (UDP allows 128) and written to the transport log unsanitised
  (`TcpTransport.cs:892`), so a pre-logon peer may be able to forge log lines.
- **UDP challenge budget.** One global bucket (`UdpTransport.cs:317-320`): a steady trickle of Hellos appears
  enough to starve UDP handshakes, pushing clients to the TCP fallback.
- **Career results.** An enrolled game server can post unbounded values for any account, and summed minutes are
  parsed as `int` in `CareerStore.cs`; needs an operator-issued server secret. Delegated review.
- **Punch signalling.** Receipts are reusable for 30 s and the per-host budget is small, so two addresses may be
  able to starve introductions for one hosted game. Delegated review.
- **Secret-prefixed admin chat.** For a peer whose name is on the admin allow-list, the secret prefix is compared
  on every chat line before the chat budget (`Server.AdminCheats.cs:137-138`), so it can be guessed at packet rate.
  Only relevant where admin cheats are enabled on a reachable server, which the boot banner already warns against.

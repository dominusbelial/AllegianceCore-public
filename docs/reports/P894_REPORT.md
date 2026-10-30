# P894 — lobby: logon version gate, hidden games, server allow-list, duplicate identity, richer server listing

Base: Claude's local `master` at dispatch. Work done in the P894 worktree; nothing
committed or pushed. Native reference: `~/Development/Allegiance/src/` (read-only,
re-grepped for every cite below).

## 1. What changed

| Area | Change |
|---|---|
| **Version gate at logon** (LOB-0004/0020/0026) | The advertised `minClientVersion` is now **enforced** where the port's client logs on: the game server's LOGONREQ handler. The client reports its installed release in a new optional tagged `P894` trailing blob on LOGONREQ; a server with a minimum refuses an older (or silent) client with a reason naming the required build **and the updater**, and the client's connect-failure screen shows it (plus a "restart the client, the lobby's update feed installs it" hint). The client also now honours `LogonAck.FValidated` at all (it ignored it before, walking into a side select it could never complete), and drops listings it is too old to join at browse time. |
| **Server→lobby protocol version** (LOB-0004) | A registering server states `lobbyProtocolVersion`; a stated value that differs from `Lobby:LobbyProtocolVersion` is refused with native's `LOGON_SERVER_NACK` text, both versions, and HTTP 409. A host that omits the field (pre-P894) is accepted. |
| **Hidden games** (LOB-0007/0008) | The directory drops paused hosts, and applies native's exact mission predicate `players > 0 \|\| MSArena` to mission rows: a zero-player **client-created** (non-arena) game is hidden until its first player; the port's persistent server-owned (arena) servers stay listed empty. `GET /api/servers/stats` reports how many paused/empty rows were hidden. |
| **Approved-server list** (LOB-0005) | `Lobby:AllowedServers` (+ `Lobby:ServerFilterMode` allow/block) ports native's `IsServerAllowed`: loopback always allowed, native's `strncmp(ip,entry,strlen(ip))` matching, native rejection text, HTTP 403 before anything is stored. **Empty by default** → community servers keep listing. |
| **Duplicate identity** (LOB-0033) | No new lobby code needed. Re-grepping the native boot showed it is **dead code** (`#ifdef USECLUB`, never defined; CD-key call commented out), so shipped native never booted at the lobby. The port closes the intent at the game-server logon (P864c): a duplicate name in a mission is refused, and only the **same verified account** may replace its old seat — the parity ruling "dup logon native-when-verified, port refusal as flag" (`ServerConfig.AlwaysRefuseDuplicateLogons`). |
| **Richer listing** (LOB-0009/0010/0017/0018/0029) | Listing now carries `location`, current/max games, native's computed **percent load** `(games*c_GameFactor + players)/maxLoad` and the **lobby-normalized start time**. New `GET /api/servers/cores` relays the master core list (curated catalog + every live server's core, with server counts); the client browser draws it and the row/details panel show location, games/load and the start time. Player join/quit continues to update counts every heartbeat (names stay the P761b opt-in). |
| **Counters / logging** (LOB-0027/0035) | `GET /api/servers/stats` answers native's lobby counters on demand; the registry logs server connect / disconnect / expiry lifecycle lines (native `LE_ServerConnected`/`LE_ServerDisconnected`). |
| **Config/docs** | `LobbySettings` gained `LobbyProtocolVersion`, `AllowedServers`, `ServerFilterMode`, `GameFactor`, `MaxLoad`; `LobbyConfig` gained `location`; `ServerConfig` gained `MinimumClientVersion` (top level, with `ALLEGIANCE_MINIMUM_CLIENT_VERSION`) and `ResolveMinimumClientVersion()` (the legacy `lobby.minimumClientVersion` still works and is the same enforced+advertised value). `docker-compose.yml`, `docker/README.md`, `docs/LOBBY_APP.md`, `docs/P810_AUTO_UPDATE_DESIGN.md`, `server.lobby-example.json` and `GLOSSARY.md` updated; `docker compose config` validates. |

## 2. Per row

Native cites were re-grepped in `~/Development/Allegiance/src/`. "Classification" is
the native-intent reading; the full text (with every port cite) is in
`docs/parity/lobby-matrix.csv`.

### LOB-0004 — protocol version negotiation on server logon

- **Native.** `lobby/server.cpp:93` accepts the server logon only when
  `pfmLogon->verLobby == LOBBYVER_LS`; `:141-151` NACKs with a tailored reason
  ("The Public Lobby server that you connected to is older than your version…" /
  "Your server's version did not get auto-updated properly…") and drops the
  connection. The field is `short verLobby` on `LOGON_LOBBY`
  (`lobby/messagesls.h:43`).
- **Classification.** A version *negotiation gate* on the server's registration, not
  an authentication rule: the lobby refuses a build it cannot speak with.
- **Port change.** `LobbyProtocol` (`src/Allegiance.Core/LobbyProtocol.cs`) is the
  shared version + `IsAccepted` + native's two reasons. The heartbeat states it on
  every register (`LobbyHeartbeat.LobbyRegisterRequest.LobbyProtocolVersion`), and
  `POST /api/servers/register` refuses a stated mismatch before storing anything
  (`LobbyEndpoints.cs:42-50`). Absent field = pre-P894 host = accepted, so an
  upgraded lobby does not evict every existing community server.
- **Tests.** `P894VersionAndAllowListTests` (older → `did not get auto-updated`,
  newer → `older than your version`, matching, omitted).

### LOB-0005 — allowed-server IP list

- **Native.** `IsServerAllowed` (`lobby/server.cpp:24-84`) reads
  `Allegiance.cfg [Lobby] NumberOfServers/FilterType/Server1..N`: no entries ⇒ allow
  all (`:71-72`), entries ⇒ allow (default) or block (`FilterType=Block`,
  `:50-51`), matching `strncmp(ip, entry, strlen(ip))` (`:62`). Loopback is always
  allowed (`:131`); a refusal uses `:138`'s text and drops the connection.
- **Classification.** An optional operator allow/block list applied at server
  registration.
- **Port change.** `ServerAllowList` + `LobbySettings.AllowedServers` /
  `ServerFilterMode`, applied in the register endpoint against the socket source
  address (never a caller-supplied one) → HTTP 403 + native's text. Default empty =
  allow all. Blank entries (an unset env var bound into the array) are ignored rather
  than turning into "refuse everyone".
- **Tests.** allow/block/loopback/omitted cases plus the pure rule theory (including
  that a shorter netblock prefix does not match, exactly as native's `strncmp`).

### LOB-0007 — paused games hidden

- **Native.** `lobby/mission.cpp:78` — `!pServer->GetPaused() && (players>0 ||
  fMSArena)` gates the broadcast of a mission ("never advertize paused games",
  `:77`); the pause arrives as `FM_S_PAUSE` (`lobby/server.cpp:266-272`) and is
  logged as `LE_…paused, missions made invisible` (`lobby/lobbyevents.cpp:42-43`);
  the client also never lists a paused server (`lobby/client.cpp:356`).
- **Classification.** A paused host is "alive but not accepting players" — hidden,
  not deleted.
- **Port change.** The register payload carries `paused`; `ServerRegistry.List()`
  drops paused hosts (by-id detail still answers), and `Stats` reports
  `HiddenPausedServers`/`HiddenPausedMissions`.
- **Honesty note.** The port has no multiplayer pause control (only the
  single-player timescale pause), so no bundled server reports `paused:true` today;
  the directory rule is the ported behaviour and is honoured for any host that does.
- **Test.** `A_paused_server_is_not_advertised`.

### LOB-0008 — zero-player games hidden

- **Native.** `lobby/mission.cpp:77-86`: advertise only when not paused **and**
  `nNumPlayers > 0 || fMSArena`, where `fMSArena` is the mission's
  `bObjectModelCreated` (`FedSrv/fsmission.cpp:4087`). The comment says it plainly:
  "don't advertize Allegiance Zone games until someone is actually in it **unless
  it's admin created**".
- **Classification.** Hide a *created-but-abandoned* game; a **server-owned (arena)
  mission** is a persistent game and is advertised from birth, empty or not.
- **Port change (the fit).** The port's servers are persistent, server-owned missions
  (`LobbyGameParams.FromMission` publishes `objectModelCreated: true`), i.e. exactly
  native's arena case — so native's own predicate keeps them listed at zero players.
  A **client-created** game (the P871 create-game relay publishes
  `objectModelCreated: false`) with nobody in it is hidden until its first player;
  the server row stays (native's `SERVERS_LIST` is per server,
  `lobby/client.cpp:343-385`). Hiding *every* empty server would empty a
  direct-connect directory and is not native's intent.
- **Tests.** `A_zero_player_persistent_arena_server_stays_listed`,
  `A_zero_player_client_created_mission_is_hidden_until_someone_joins` (also proves a
  heartbeat with a player re-advertises the mission).

### LOB-0009 — static core list relay

- **Native.** `BuildStaticCoreInfo`/`SetStaticCoreInfo`
  (`lobby/server.cpp:100-120`; `lobby/server.h:54-69`) aggregates every connected
  server's `StaticCoreInfo` into one master list which `SERVERS_LIST` carries to the
  client (`lobby/client.cpp:377-385`) together with each server's core mask
  (`:363`).
- **Classification.** Clients must be able to choose a core before joining.
- **Port change.** `GET /api/servers/cores` (`ServerRegistry.CoreList`): every live
  listing's core with its server count, plus the curated `Lobby:GameCores` catalog at
  count 0. The client mirrors it (`LobbyCoreList`) and draws `CORES: …` above the
  browser rows.
- **Tests.** `The_core_list_relays_every_live_core_plus_the_curated_catalog`,
  client `The_core_list_binds_by_name`, and the live e2e.

### LOB-0010 — player join/quit tracking

- **Native.** `FM_S_PLAYER_JOINED`/`FM_S_PLAYER_QUIT` →
  `SetPlayerMission`/`RemovePlayerFromMission` (`lobby/server.cpp:211-263`), which
  update the per-mission and per-server counts.
- **Classification.** Keep each mission's live population current.
- **Port change.** Already present (the heartbeat refreshes `PlayerCount` and the
  opt-in roster in place) — kept, now cited with evidence.
- **Tests.** `LobbyHeartbeatTests.Second_heartbeat_updates_same_listing_in_place`,
  `Heartbeat_publishes_the_player_list_to_the_directory`.

### LOB-0013 — chat log relay to a moderation service

- **Native.** `FM_S_LOG_CHAT_MESSAGE` POSTs chat (source/target, Steam ids, IP) plus
  an api key to an external web service (`lobby/server.cpp:342-380`).
- **Classification.** Era constraint: a retired external Zone service, not a lobby
  rule. The port's DirectoryChat is in-memory and there is no external chat-log
  destination to port to.
- **Port change.** None (classified `n/a`/era). Adding moderation logging would need
  a new external destination — out of scope, and a privacy decision of its own.

### LOB-0017 — mission start time normalized

- **Native.** `plmi->dwStartTime += Time::Now().clock()` in `SetLobbyInfo`
  (`lobby/mission.cpp:61`): the server sends an offset, the lobby publishes an
  absolute time.
- **Classification.** Normalize the relative start time against the lobby's clock
  before relaying.
- **Port change.** `ServerRegistry.StartTimeUtc` = listing `LastSeenUtc` +
  `Game.StartTimeOffsetMs` (zero offset = no armed start, native's `dwStartTime 0`),
  published on the DTO and shown as `STARTS:` in the details panel.
- **Tests.** `Start_time_is_normalized_against_the_directory_clock`,
  `No_armed_start_time_is_advertised_as_null`,
  `The_details_panel_shows_location_games_and_the_normalized_start_time`.

### LOB-0018 — server metadata advertised

- **Native.** On the server logon the lobby records location (`:105-106`), privileged
  users (`:108-109`), server version (`:111-112`), max games (`:114`) and current
  games (`:117`); location and `iCurGames/iMaxGames` are drawn in the server list
  (`WinTrek/CreateGameDialog.h:79-100`).
- **Classification.** Expose the server's own metadata so a client can pick a server.
- **Port change.** `location` (register payload → listing → client row `[EU North]`
  and `LOCATION:` details line), version (already), current/max games (already since
  P883b, now mirrored by the client) and the computed **percent load** (LOB-0029).
  `LobbyConfig.Location` + the heartbeat publish it.
- **Privileged users — n/a sub-field, with reason.** The port authorises moderators
  with a bearer secret presented at logon (`ServerConfig.ModeratorAccounts`, P762b).
  There are no name-based privileged users to advertise, and publishing the list
  would publish the credentials. The row is `implemented` for the fields that can be
  ported, with this n/a documented here and in the matrix note.
- **Both HUD styles.** The server list is a **menu screen**, not in-flight HUD: it is
  drawn by `Game1.DrawLobby`/`DrawIntroFindServer` and the text rows are
  `ServerListing.FormatRow()`/`DetailsLines()` from the shared model, which the
  Modern style does not override. The new fields therefore appear identically in
  Classic and Modern; no `HudRenderer`/`ModernHudRenderer` code changed, so no HUD
  screenshot pair is claimed here (none of the changed surfaces is in-flight UI).
- **Tests.** `Location_is_recorded_and_exposed`,
  `Heartbeat_advertises_location_and_the_stated_protocol_version`, client row/details
  tests, live e2e.

### LOB-0020 — client logon handshake (auth + version check)

- **Native.** `FM_C_LOGON_LOBBY` (`lobby/client.cpp:245-334`): credentials
  (ticket/cd-key, Steam auth thread `:219-231`), the `crcFileList` → `AUTO_UPDATE_INFO`
  redirect (`:272-288`), the `verLobby` NACK (`:290-309`) and `QueueMissions` on
  success. Nothing is admitted before all of it passes.
- **Classification.** Admission control: validate identity **and** version at logon.
- **Port change.** Identity: the passkey identity service (already). Version: the
  **game-server logon**, which is the logon the port's client actually performs —
  `Server.Dispatch.CheckClientVersion` compares `LOGONREQ.ClientVersion` with
  `ServerConfig.ResolveMinimumClientVersion()` and refuses with a clear update reason.
  The client sets `ConnectionSession.ClientVersion` / the LOGONREQ extension from its
  installed release (`Game1.Session.InstalledClientVersion`), checks
  `FValidated`, and shows the refusal + updater hint.
- **Wire.** A tagged trailing blob (`"P894"` + length + bytes) after the native
  variable items; no native offset moves and a logon without it is byte-identical to
  the pre-P894 message (proved by
  `P894LogonVersionWireTests.Without_a_version_the_message_is_the_native_shape`).
- **Tests.** `P894VersionGateTests` (older refused with min+updater, silent client
  refused, current/newer seated, ungated server admits everyone, legacy
  `lobby.minimumClientVersion` enforced), `P894LogonVersionWireTests`, live e2e.

### LOB-0021 — client logoff

- **Native.** `FM_C_LOGOFF_LOBBY` is an explicit no-op TODO
  (`lobby/client.cpp:336-340`).
- **Classification.** Nothing to mirror: the native rule is "do nothing".
- **Port change.** None; classified `n/a` (native omission).

### LOB-0026 — autoupdate version gate

- **Native.** The lobby compares the client's `crcFileList` with its official list,
  sends `AUTO_UPDATE_INFO` (FTP location + CRC) and does **not** admit the client
  (`lobby/client.cpp:272-288`); the CRC comes from `AutoUpdate.cpp:50-89`.
- **Classification.** An out-of-date client is redirected to the updater instead of
  entering.
- **Port change.** The HTTP analogue is kept (`/api/update/info` + the signed feed and
  the P871 `crcFileList` gate), and P894 adds the missing *refusal*: an older client
  is refused at logon with the updater in the reason, and the browse list hides
  servers it cannot join.
- **Tests.** `P894VersionGateTests`, `P894ListingFieldsTests` (FilterForClient),
  `UpdateInfoTests`, live e2e (`/api/update/info` still advertises current/minimum/feed).

### LOB-0027 — lobby operational counters

- **Native.** `LOBBY_COUNTERS`/`PER_SERVER_COUNTERS` (`lobby/counters.h:4-26`) filled
  by `UpdatePerfCounters` (`lobby/lobbyapp.cpp:396-423`): logins/logoffs, players in
  missions vs lobby, queue lengths/sizes, missions, servers, per-server percent load.
- **Classification.** Operational telemetry.
- **Port change.** `GET /api/servers/stats`: servers, missions, players, summed
  percent load, plus the hidden paused/empty counters (which makes LOB-0007/0008
  observable). **n/a sub-metrics:** login/logoff counts and messaging-queue lengths —
  the directory has no persistent lobby client sessions or message queues (clients
  sign in to the identity service and connect straight to a game server).
- **Test.** `Stats_report_servers_missions_players_and_load`.

### LOB-0029 — server games capacity / load reporting

- **Native.** `SetMaxGamesAllowed`/`SetCurrentGamesCount`
  (`lobby/server.cpp:114-117`), `ServerCoreInfo.iCurGames/iMaxGames`
  (`lobby/messageslc.h:122-125`), `GetPercentLoad = (missions*c_GameFactor +
  players)/m_maxLoad` with `c_GameFactor = 5` (`lobby/server.h:17,70-73`) and
  `m_maxLoad = 300` (`lobby/server.cpp:457`).
- **Classification.** A per-server load metric for picking/displaying servers.
- **Port change.** `ServerRegistry.PercentLoad` with the same constants
  (`Lobby:GameFactor`, `Lobby:MaxLoad`), on the DTO and in the client row/details.
- **Tests.** `Game_counts_and_percent_load_follow_native_c_GameFactor`,
  `Percent_load_is_the_native_formula`, client row test.

### LOB-0033 — duplicate name / CD-key boot

- **Native.** `BootPlayersByName`/`BootPlayersByCDKey` send `REMOVE_PLAYER`
  (`lobby/lobbyapp.cpp:663-719`) with `RPR_duplicateName`/`RPR_duplicateCDKey`.
- **Classification (re-grep finding).** **Dead code in shipped native**:
  `SetPlayerMission` guards the call with `#ifdef USECLUB`
  (`lobby/lobbyapp.cpp:889-891`) and `USECLUB` is defined nowhere in the tree; the
  CD-key boot is commented out (`:950-965`). So the native outcome is "the lobby does
  not boot duplicates". The intent — one active instance per account — is what the
  port must preserve.
- **Port change.** None needed. `Server.Dispatch` refuses a duplicate character name
  in the mission and lets only the **same verified account** replace its old seat
  (`RemovePlayerByName`, native `FedSrv.CPP:629`), per the parity ruling "dup logon
  native-when-verified, port refusal as flag"
  (`ServerConfig.AlwaysRefuseDuplicateLogons` / `ALLEGIANCE_ALWAYS_REFUSE_DUPLICATE_LOGONS`,
  default off = native-when-verified). The identity service guarantees one account per
  credential and the P808c reserved-callsign rule stops an anonymous peer taking a
  registered name.
- **Out of scope.** A name can still exist on two **different** servers: the port has
  no global lobby presence (direct-connect), which is the same routing gap that makes
  LOB-0023/0030 out of scope.
- **Evidence.** `P864cDuplicateLogonTests`, `P655DuplicateIdentityTests`,
  `P808cReservedCallsignTests` (unchanged, still green in the fast tier).

### LOB-0035 — server lifecycle logging

- **Native.** `OnNewConnection`/`OnDestroyConnection` log
  `LE_ServerConnected`/`LE_ServerDisconnected` with name and address
  (`lobby/server.cpp:401-425`; texts `lobby/lobbyevents.cpp:46-47`).
- **Classification.** An audit trail of who is present.
- **Port change.** The registry logs `server connected` (new registration),
  `server disconnected` (token deregistration) and `server expired (missed roll
  call)`; the e2e lobby log shows them.

## 3. Out-of-scope routing rows (report-only)

These exist only because native routed creation/joins through a DirectPlay lobby. The
port's directory lists servers and the client connects to them directly, so they are
**out of scope** (design / era constraint) and no routing lobby was built:

| Row | Classification | Why it cannot be ported as-is |
|---|---|---|
| LOB-0023 CREATE_MISSION_REQ routing | **design** — replaced | P871's HTTP wizard + server claim is the port's creation path; the lobby does not relay packets to a chosen server. |
| LOB-0024 JOIN_GAME_REQ routing | **design** — replaced | Clients connect straight to the listed address/port; capacity is enforced at the game server (`JoinMission`), so the outcome is preserved without a relay. |
| LOB-0030 external population broadcast (Zone UDP) | **era** | Push-to-Zone-web-server population reporting has no modern equivalent; `GET /api/servers` serves the same facts on demand. |
| LOB-0031 creator auto-join notification | **design** — replaced | The client joins the server it asked to create on (P871b ACK → join), so a lobby-pushed JOIN_MISSION is unnecessary. |
| LOB-0034 server-initiated mission registration (cookie mapping) | **design** — replaced | A server's missions ride its register/heartbeat payload instead of a distinct cookie-mapping channel. |

**Proposed matrix treatment (for the dispatcher to decide).** Do not silently mark
these `n/a`, because that would remove real native behaviour from the denominator and
inflate the score. Proposal: add a distinct `native_class` value
`design-replaced` (or a new optional column `out_of_scope` = `routing-lobby`) and let
`scripts/parity-scorecard.py` exclude exactly that class while still requiring a
non-empty reason and a port-equivalent cite; until then the rows keep their honest
`missing`/`partial` status with the P894 note recorded in `notes` (which is what this
change does).

## 4. Tests added

| Test file | Covers |
|---|---|
| `tests/Allegiance.Lobby.Tests/P894LobbyTests.cs` (25 cases) | protocol gate (older/newer/matching/omitted), allow-list (allow/block/loopback/omitted/blank-entry + pure rule theory), paused hidden, zero-player arena vs client-created mission, location, percent load (native formula), start-time normalization/absence, core list, stats. |
| `tests/Allegiance.Core.Tests/P894LogonVersionWireTests.cs` (5) | LOGONREQ P894 extension: native shape unchanged without it, round-trip, coexistence with the P883 resume token, empty = absent. |
| `tests/Allegiance.ClientCore.Tests/P894VersionGateTests.cs` (7) | server logon gate: older refused with min+updater, silent client refused, current/newer seated, ungated server admits all, legacy lobby-block minimum enforced, resolved-minimum precedence + env override. |
| `tests/Allegiance.ClientCore.Tests/P894ListingFieldsTests.cs` (8) | client row shows location/games/load, details panel LOCATION/GAMES/STARTS, browse-time version filter, camelCase wire binding, core-list binding. |
| `tests/Allegiance.Core.Tests/LobbyHeartbeatTests.cs` (updated + 1 new) | register wire bytes unchanged when the new fields are absent, present when stated, and a heartbeat advertising `location` end-to-end through the real lobby. |
| `tools/p894-e2e-proof/` + `run-e2e.sh` | the live end-to-end proof below. |

## 5. Local end-to-end check

`tools/p894-e2e-proof/run-e2e.sh` starts the **real** lobby process (port 5147) and the
**real** server process (port 26073, artwork corpus, lobby heartbeat, location
"Loopback Lab", `minimumClientVersion: "2.0.0"`), then runs the client proof tool,
which uses the production client model and wire session. Result (full transcript in
[`P894_assets/e2e.txt`](P894_assets/e2e.txt), 17 PASS / 0 FAIL):

```
[p894-e2e] PASS the server is listed by the lobby (1 listing(s))
[p894-e2e] row: [unverified] P894 E2E Server  127.0.0.1:26073  [Loopback Lab]   0 players   P894 E2E  games 1/99 load 0%
[p894-e2e] PASS location advertised: 'Loopback Lab'
[p894-e2e] PASS game capacity advertised: 1/99, load 0%
[p894-e2e] PASS minimum client version advertised: '2.0.0'
[p894-e2e] PASS the client's details panel shows the location
[p894-e2e] browse gate for installed 0.0.0: 0 visible, 1 hidden
[p894-e2e] PASS master core list relayed: PCore001.igc
[p894-e2e] PASS directory counters: 1 server(s), 0 player(s)
[p894-e2e] PASS logon with client 2.0.0 accepted -> 'P894JOIN'
[p894-e2e] PASS joined and seated (ship id 8098)
[p894-e2e] PASS logon with client 0.0.1 refused
[p894-e2e] refusal reason: your client (0.0.1) is older than this server's minimum (2.0.0) - update the client and reconnect; the lobby's update feed installs it
[p894-e2e] PASS the refusal names the required build / points at the updater / says older
[p894-e2e] PASS the refused client was never seated
[p894-e2e] ALL CHECKS PASSED
```

The server log confirms the refusal happened at logon
(`[logon] 2 'P894OLD': client version '0.0.1' refused — …`, `validated=False`) and the
accepted peer was seated (`[entergame] peer 1 'P894JOIN' …`, `SHIP_DELETE … SDR_LOGGEDOFF`
on disconnect). Raw logs: `~/allegiance-evidence/P894/lobby.log`, `server.log`.

## 6. Matrix

`docs/parity/lobby-matrix.csv` (35 rows) — rows updated with port cites, evidence and
native-intent notes: LOB-0004, 0005, 0007, 0008, 0009, 0010, 0013, 0017, 0018, 0020,
0021, 0026, 0027, 0029, 0033, 0035; the five out-of-scope rows got the P894
classification note. Line endings LF (`grep -c $'\r'` = 0).

| `scripts/parity-scorecard.py` | Before | After |
|---|---|---|
| weighted (credit/weight) | **45.00 % (36/80)** | **84.62 % (66/78)** |
| unweighted (complete/eligible) | **34.38 % (11/32)** | **80.00 % (24/30)** |

Only the five routing-lobby rows (LOB-0023/0024/0030/0031/0034, weight 11) remain
non-complete — all classified out of scope above. The two rows that left the
denominator (LOB-0013, LOB-0021) are `n/a` for *native* reasons (era external service;
native's own no-op), each with the reason recorded.

## 7. Gate

| Step | Result |
|---|---|
| `dotnet build AllegianceCore.slnx` | **succeeded, 0 warnings, 0 errors** |
| `tools/build-tools.sh` | **all 95 tool projects built** |
| Server Native AOT publish (`-c Release -r linux-x64 --self-contained -p:PublishAot=true`) | **succeeded**, no IL2026/IL3050 |
| Lobby tests, 3× alone | 119 passed / 0 failed / 3 skipped each run |
| New tests, 3× alone | Lobby 25/25 ×3; Core P894 wire 5/5 ×3 and `LobbyHeartbeatTests` 14/14 ×3; ClientCore P894 15/15 ×3 |
| `tools/run-tests.sh --tier fast` (one run) | **11,787 executions, 558.8 s: 11,777 passed / 7 failed / 3 not executed**; every P894 test passed. The 7 failures are ledgered baseline/flakes, each re-run in isolation: `NpcWingmanDefendLiveTests…` 0/3 (pre-existing baseline), `MissileLockLiveTests…` 1/3 (pre-existing), `P739TimerLiveTests…(delay:7)` 2/3, `DockDockingTests…` 2/3, `P803TurretGunnerLoopbackLiveTests…` 3/3, `LiveCombatWireTests.Real_Bot_class…` 3/3 (0.4 s alone vs 31.8 s in the parallel sweep), `LiveSideSelectTests.Flag_on_auto_seats…` 2/3 then 0/8 (time-range assert under load). See the new P894 section in `docs/TEST_FLAKES.md`; raw logs and the full fast-tier receipt are in `~/allegiance-evidence/P894/` (host load average 4.97 at the time). |
| `docker compose config` | validates |
| `python3 scripts/parity-scorecard.py` | passes (all matrices validated) |
| `tools/check-glossary-links.py` | passes |

## 8. Native-intent footer

Every rule ported here keeps native's *outcome*, not necessarily its transport:

- **Version admission** — native refused an out-of-date client at the lobby and told
  it to update (`lobby/client.cpp:272-309`). The port's client logs on to a game
  server, so the same refusal happens there; what the player sees (a clear reason
  naming the required build and pointing at the updater) matches native's outcome,
  and the LOGONREQ extension is additive (an older client's bytes are unchanged).
- **Server admission** — native accepted a server only from an approved address and
  only when the lobby protocol version matched (`lobby/server.cpp:93,131,141-151`).
  The port applies exactly those two decisions to the registration request and keeps
  both off by default (empty allow-list; omitted protocol version accepted) so it does
  not break community servers the way a stricter-than-native default would.
- **What is listed** — native advertised a mission only when it was not paused and
  either had a player or was an admin/server-created arena
  (`lobby/mission.cpp:77-86`). The port applies that exact predicate; because the
  port's servers are persistent server-owned missions, native's own rule keeps them
  visible, and only created-but-abandoned client games disappear.
- **What is advertised** — location, version, game counts and a computed load are the
  server's own facts in native too (`lobby/server.cpp:104-117`, `lobby/server.h:70-73`),
  relayed to the client's server list; the port publishes the same facts and shows
  them where native did. The one native field deliberately **not** ported is the
  privileged-user list, because the port's privileged users are bearer secrets rather
  than names.
- **One instance per account** — native intended the lobby to boot a duplicate name
  (`lobby/lobbyapp.cpp:663-719`) but shipped with that code compiled out
  (`#ifdef USECLUB`); the port enforces the intent where the port can (`P864c`
  verified-account replacement + duplicate-name refusal) and documents the residual
  cross-server gap as inherent to the direct-connect design.
- **Routing rows** (LOB-0023/0024/0030/0031/0034) are not ported because the routing
  lobby itself is the design being replaced; they are recorded as out of scope rather
  than silently dropped, and a matrix-classification proposal is in §3.

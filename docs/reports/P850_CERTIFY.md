# P850 — Certify the event-driven mission rules: FM lobby, session, team

Batch: SIM-0588 FM_CS_PING · SIM-0592 FM_C_VOTE · SIM-0597 FM_CS_LOGOFF ·
SIM-0598 FM_C_LOGONREQ · SIM-0610 FM_CS_QUIT_MISSION · SIM-0611 FM_CS_QUIT_SIDE ·
SIM-0612 FM_CS_AUTO_ACCEPT · SIM-0613 FM_CS_LOCK_LOBBY · SIM-0614 FM_CS_LOCK_SIDES ·
SIM-0615 FM_C_RANDOMIZE_TEAMS · SIM-0616 FM_CS_PLAYER_READY ·
SIM-0617 FM_CS_FORCE_TEAM_READY · SIM-0622 FM_CS_CHANGE_TEAM_CIV ·
SIM-0624 FM_CS_SIDE_INACTIVE · SIM-0626 FM_C_CHANGE_ALLIANCE ·
SIM-0629 FM_C_START_GAME · SIM-0630 FM_CS_SET_TEAM_LEADER ·
SIM-0631 FM_CS_SET_MISSION_OWNER · SIM-0633 FM_C_REQPINGDATA

Repo: public AllegianceCore worktree `allegiance-hermes-p850` (master). Native
reference read-only: `~/Development/Allegiance/src/`. Every native line number
was re-grepped at dispatch time (FedSrv.CPP:14126 lines) and is cited below.

Result: **18/19 rows certified `implemented`, 1 row `n/a` (SIM-0631)**. This batch
added two real behaviour fixes that close port gaps — the server previously only
*built* (broadcast) `QUIT_SIDE(126)` and `SET_TEAM_LEADER(121)` and had **no inbound
dispatch case** for either; P850 adds both handlers with 10 new tests (all pass
3/3 alone). The remaining divergences are documented `n/a` (era / WOPR-bot /
architecture) or design improvements with equivalent outcome. Family weighted
completion rises **25.93% (153/590) → 35.26% (207/587)**. `python3
scripts/parity-scorecard.py` validates; `tools/p832a-mission-rules-audit.py
--check` (351 bodies, no missing/stale) validates.

---

## Per-row certification

Legend: E = equal, D = different (fix/test), M = missing (fix/test), n/a =
era/architecture (why). Rows marked ★ were missing a port and are the new work
this batch.

### SIM-0588 FM_CS_PING — IMPLEMENTED
Native `FedSrv.CPP:4897-4911` branches:
- stamp `timeServer = g.timeNow`; `ForwardMessage` the PING back to the SENDER'S
  own connection (echo, not broadcast); no mission/side context needed.
Port mapping (EQUAL): `Server.Dispatch.cs:59-95` — decode, set
`TimeServer = sim-ms`, echo to the same peer. P645 adds a rate limit on
client-triggered replies so a PING storm cannot amplify into an unbounded
guaranteed-reply queue (native pings ≤ ~6 s, clintlib.cpp:2052-2085).
Fix/tests: none. `LiveCountdownStartPingTests`, `P722InterfaceLoopbackTests`.

### SIM-0592 FM_C_VOTE — IMPLEMENTED
Native `FedSrv.CPP:5349-5359` branches: guard `!pfsMission || side<0`; then
`TallyVote(player, ballotID, bAgree)`.
Port mapping (EQUAL): `Server.Dispatch.cs:552-593` — guard mission + voter seated
on a real side; find the named ballot in the SENDER'S OWN mission and cast only if
eligible via `PendingBallot.TryCastVote`. Unknown/resolved ballot or ineligible
voter are logged and ignored (never fatal, never creates a ballot).
Fix/tests: none. `BallotVotingLiveTests`,
`P739PeriodicTests.Ballot_strict_expiry_all_votes_and_cancellation`.

### SIM-0597 FM_CS_LOGOFF — IMPLEMENTED
Native `FedSrv.CPP:5787-5802` branches: counters; echo LOGOFF to the same
connection; EndAuthSession (Steam); `delete pfsPlayer` (immediate).
Port mapping (EQUAL + n/a): `Server.Dispatch.cs:1790-1841` — echo the exact empty
message back to the same connection then release the peer through the SAME
cleanup path as an abrupt drop (`ReleasePeerOnRequest`, reason QSR_Quit;
ship retired). P813c: a voluntary quit is never held by reconnect grace (native
deletes right here). P654 H-14: the transport connection is closed after flush so
it is not reused (native never reuses the connection).
- Steam `EndAuthSession` leg: n/a — no Steam auth concept in this port.
Fix/tests: none. `P813cReconnectGraceTests`, `P762LobbyModerationTests` (release
path).

### SIM-0598 FM_C_LOGONREQ — IMPLEMENTED
Native `FedSrv.CPP:5805-6094` branches (huge, mostly era plumbing):
- version check (`fedsrvVer==MSGVER`);
- character-name sanity (NUL within `c_cbName=25`) and (USEAUTH) auth/decrypt;
- Steam DRM + Steam auth thread; lobby `PLAYER_RANK` rank fetch; SQL.
Port mapping (EQUAL + n/a): `Server.Dispatch.cs:104-277`.
  E — client version gate (`VersionMatches`), character-name validation (≤24
  chars, no control/format chars; native c_cbName=25, FedSrv.CPP:5817-5818),
  ONE logon per connection (native assert FedSrv.CPP:4832), duplicate-name
  refusal, reserved-callsign refusal, cookie-bearing `LogonAck`, pending
  side-select hold and idempotent re-LOGONREQ.
  n/a — the whole auth stack (zone-ticket decrypt, Steam DRM/auth thread, SQL
  rank query, lobby PLAYER_RANK) is era plumbing replaced by the port's
  identity/lobby binding (BindIdentity) and per-profile rank seeding.
Fix/tests: none. `LiveDuplicateLogonTests` (P646 one-logon),
`P813cReconnectGraceTests` (parked-seat resume), name-validation loopback tests.

### SIM-0610 FM_CS_QUIT_MISSION — IMPLEMENTED
Native `FedSrv.CPP:6749-6821` branches: guard mission; moderator → remove with
reason+message; `sideID<0 && !=TEAMLOBBY && !moderator` → break; target-gone
break; self-or-leader gate (same side); reason `TrapHackBoot(Quit|LeaderBooted)`;
effect gated on `!bIsBoot || !bLockTeamSettings`.
Port mapping (EQUAL + n/a): `Server.Moderation.cs:25 HandleQuitMission`
(+ `Server.Dispatch.cs:832-836`).
  E — moderator branch (native Steam group → `ModeratorAccounts`), self-quit /
  leader-boot of a same-side player, exact reason check, boot refused while
  `LockTeamSettings`.
  n/a — `SIDE_TEAMLOBBY` special case (no teamlobby in this port).
Fix/tests: none. `P762LobbyModerationTests`.

### SIM-0611 FM_CS_QUIT_SIDE — IMPLEMENTED ★ (new handler)
Native `FedSrv.CPP:6823-6906` branches: guard mission; moderator → remove; side<0
& !moderator → break; target-gone break; `TrapHackBoot(target is player)`; self-
or-leader gate (same side); reason `Quit|LeaderBooted`; locked-sides
`DELPOSITIONREQ(DPR_SidesLocked)` special case; `!bIsBoot || !bLockTeamSettings`
→ `RemovePlayerFromSide`; banned-side-mask kick-out-of-mission.
Port mapping — **was MISSING** (server only broadcast QUIT_SIDE, no inbound
case). Added:
- `Server.Dispatch.cs:2232-2261` (inbound case) → `Server.Lobby.cs:2013
  HandleQuitSide`.
  E — moderator removes any player; ordinary self-quit / leader-boot of a
  same-side teammate; exact reason; boot refused while `LockTeamSettings`; the
  leaver is MOVED to the team lobby via `MoveToTeamLobby` (the port of
  `RemovePlayerFromSide`), staying in the mission.
  n/a — the locked-sides `DELPOSITIONREQ` block requires the sender's own side
  to be `SIDE_TEAMLOBBY`, which an ordinary player reaches only already parked
  there and is caught earlier at the sideID<0 gate; the banned-side-mask
  kick-out-of-mission is N/A (no banned-side-mask concept in this port).
Fix/tests: **added** — dispatch case + `HandleQuitSide`. New
`P850MissingFmHandlersTests.QuitSide_*` (self-quit to team lobby; non-leader kick
refused; leader boot honoured when unlocked / refused when `LockTeamSettings`;
missing-target no-op), all pass 3/3 alone.

### SIM-0612 FM_CS_AUTO_ACCEPT — IMPLEMENTED
Native `FedSrv.CPP:6908-6936` branches: guard mission; `sid==TEAMLOBBY` break;
leader gate; `TrapHackBoot(iSide==sid)`; `bLockGameOpen` break;
`ForwardMessage(group)`; `SetAutoAccept(side, fAutoAccept)`.
Port mapping (EQUAL + n/a): `Server.Dispatch.cs:1508-1571`.
  E — sender must be its side's current leader naming its OWN side; relay
  whole-mission then `Side.AutoAccept`; turning ON sweeps pending requests and
  seats each (native `SetAutoAccept` fsmission.cpp:4974-5011). P841 certified the
  default (InitSide seeds auto-accept true).
  n/a — `bLockGameOpen` gate (no such flag); TEAMLOBBY side check (no teamlobby).
Fix/tests: none. `LiveTeamLobbyToggleTests`, `P678LobbyRelayLiveTests`.

### SIM-0613 FM_CS_LOCK_LOBBY — IMPLEMENTED
Native `FedSrv.CPP:6938-6951`: guard `mission && owner`; `bLockGameOpen` break;
`ForwardMessage`; `SetLockLobby(fLock)`.
Port mapping (EQUAL + n/a): `Server.Dispatch.cs:1315-1363` — owner-only gate
(`IsMissionOwnerPlayer`), relay whole-mission, `Mission.LockLobby=fLock`.
Honesty: the flag has no gating hook in this port — native's consequence
(refusing brand-new lobby joiners with `DPR_LobbyLocked`, FedSrv.CPP:733)
belongs to the legacy first-join flow this port seats directly, so the flag is
carried for wire completeness only. `bLockGameOpen` n/a.
Fix/tests: none. `P678LobbyMessageTests`, `P678LobbyRelayLiveTests`,
`LiveTeamLobbyToggleTests`.

### SIM-0614 FM_CS_LOCK_SIDES — IMPLEMENTED
Native `FedSrv.CPP:6953-6962`: guard `mission && owner`; `ForwardMessage`;
`SetLockSides(fLock)`.
Port mapping (EQUAL): `Server.Dispatch.cs:1365-1409` — owner-only, relay
whole-mission, `Mission.LockSides=fLock`. Unlike LOCK_LOBBY this one HAS a real
reachable hook: the POSITIONREQ side-switch handler refuses with
`DELPOSITIONREQ(DPR_SidesLocked)` while LockSides is on (native
`CheckPositionRequest` bLockSides, fsmission.cpp:4590).
Fix/tests: none. `LiveTeamLobbyToggleTests`, `P678LobbyRelayLiveTests`,
`P655ProbeWorkersLiveTests`.

### SIM-0615 FM_C_RANDOMIZE_TEAMS — IMPLEMENTED
Native `FedSrv.CPP:6964-6985`: guard `mission && owner`; `STAGE_NOTSTARTED`;
`iMaxImbalance==0x7ffe` → `RandomizeSides`, else `FlushSides`.
Port mapping (EQUAL + n/a): `Server.Dispatch.cs:1189-1235` → `RebalanceSides`.
  E — owner-only + NotStarted, then balance (highest-rank-first greedy onto the
  lowest-rank-sum side; leaders anchored; pending requests cleared).
  n/a — the flush branch (`iMaxImbalance != 0x7ffe`, dump to a neutral lobby) is
  N/A: no `SIDE_TEAMLOBBY` and no `MissionParams` field; "balance" is what the
  message name promises (docs/RANDOMIZE_TEAMS_P195.md). LockSides deliberately
  bypassed (native `RandomizeSides` never consults it).
Fix/tests: none. `RandomizeTeamsLiveTests` (6 scenarios).

### SIM-0616 FM_CS_PLAYER_READY — IMPLEMENTED
Native `FedSrv.CPP:7016-7026` (regular branch): `TrapHackBoot(shipID==own)`;
`SetReady(fReady)`; `ForwardMessage(group)`.
Port mapping (EQUAL): `Server.Dispatch.cs:1705-1765` — relay to every same-side
peer INCLUDING the sender (their client applies the roster change on receipt);
re-encode the relay with the sender's OWN ship id (native
`TrapHackBoot(shipID==GetShipID())`, FedSrv.CPP:7019); remember `_playerReady`
and `RecomputeSideReady` (the P180 auto-start trigger).
Fix/tests: none. `P713LobbyScreenStateTests`,
`ClientReadyUpUnfreezesNormalMapLiveTests`, `ReadyCheckLiveTests`.

### SIM-0617 FM_CS_FORCE_TEAM_READY — IMPLEMENTED
Native `FedSrv.CPP:6988-7015` (fIsForce branch): leader-of-own-side gate;
`TrapHackBoot(iSide==sid)`; `ForwardMessage` FIRST; `SetForceReady(iSide, f)`.
Port mapping (EQUAL): `Server.Dispatch.cs:1083-1136` — leader of the named side
naming its own side; relay whole-mission first then `Side.ForceReady`;
`RecomputeSideReady` reports the side ready unconditionally (subject to the ≥1
connected-player floor) and may auto-start.
Fix/tests: none. `ReadyCheckLiveTests`, `P713LobbyScreenStateTests`,
`LivePositionRequestTests`.

### SIM-0622 FM_CS_CHANGE_TEAM_CIV — IMPLEMENTED
Native `FedSrv.CPP:7344-7367`: guard mission; `TEAMLOBBY || !leader` break;
`TrapHackBoot(civID != NA)`; if `STAGE_NOTSTARTED` → resolve civ,
`ForwardMessage`, `SetSideCiv`.
Port mapping (EQUAL + design): `Server.Dispatch.cs:1573-1655` +
`Server.TeamCiv.cs` — leader changes its OWN side's civ (must name own side),
NotStarted gate, resolve civ in the core, relay whole-mission, `ApplySideCivilization`
(native `SetSideCiv` side effects). DESIGN: P802 adds native `RANDOM_ID` 9999
random-faction handling (client's randomInt pick + owner re-roll + <300
special-civ cut) — a documented superset of the native branch.
Fix/tests: none. `P802TeamCivLiveTests`, `P802TeamCivPickerTests`,
`LiveTeamLobbyToggleTests`.

### SIM-0624 FM_CS_SIDE_INACTIVE — IMPLEMENTED
Native `FedSrv.CPP:7379-7428`: owner + NotStarted + `!bObjectModelCreated` traps;
change Active only if side empty else revert; recompute AllowEmptyTeams (toggle
when all-active XOR aet); `ForwardMessage`; `UpdateAlliances(sid,NA)`.
Port mapping (EQUAL + n/a): `Server.Dispatch.cs:1411-1506` — owner + NotStarted;
change `Side.Active` only when empty else revert the requested value; recompute
`Mission.AllowEmptyTeams`; relay whole-mission; `UpdateAlliances(sid,-1)`.
  n/a — `bObjectModelCreated` gate (Mission.Assemble builds the object model once
  at boot — no live false-case window); the in-game `DeactivateSide`
  forced-elimination flow is out of scope (superseded by P187 resign
  substitution; P178 victory-by-inactive-sides still detects emptiness by
  connected-peers and does not consume `Side.Active`).
Fix/tests: none. `LiveSideInactiveTests`, `P713LobbyScreenStateTests`,
`ClientWorldSideInactiveTests`.

### SIM-0626 FM_C_CHANGE_ALLIANCE — IMPLEMENTED
Native `FedSrv.CPP:7469-7481`: guard mission; if `STAGE_NOTSTARTED` →
`UpdateAlliances(sideID, sideAlly)`.
Port mapping (EQUAL + design): `Server.Dispatch.cs:1657-1703` + `UpdateAlliances`.
  E — NotStarted gate + `UpdateAlliances(sideID, sideAlly)`.
  DIVERGENCE (documented, P190): adds an `IsMissionOwnerPlayer` gate and range
  checks — native performs NO owner check (it trusts the client UI to expose the
  control only to the owner, per the message's own comment) and silently drops
  out-of-range ids inside `UpdateAlliances` (fsmission.cpp:5642-5647); the port
  corrects these known native gaps per the P184/P186 convention.
Fix/tests: none. `LiveAllianceTests`, `LiveSideInactiveTests`, `PlayerDonateTests`.

### SIM-0629 FM_C_START_GAME — IMPLEMENTED
Native `FedSrv.CPP:7597-7622`: owner trap (bot exempt); if `STAGE_NOTSTARTED` &&
(`FAllReady()` || bot) → `StartCountdown(briefing countdown; 0 if bot)`.
Port mapping (EQUAL + n/a): `Server.Dispatch.cs:1138-1187` → `StartCountdown(
BriefingCountdownSeconds=15)` — owner-only + NotStarted + every occupied side
ready (native `c_fMissionBriefingCountdown=15s`, igc.h:1005).
  n/a — the bot-auth special case (bots start with zero countdown); native's full
  `FAllReady` (rank/team-balance) is replaced by the port's documented
  `IsAllOccupiedSidesReady` simplification (P188 writeup).
Fix/tests: none. `LiveCountdownStartPingTests`, `RandomizeTeamsLiveTests`.

### SIM-0630 FM_CS_SET_TEAM_LEADER — IMPLEMENTED ★ (new handler)
Native `FedSrv.CPP:7624-7687` branches: guard mission; isBot; `TrapHackBoot(sid in
range)`; if (leader(sid) || isBot): `TrapHackBoot(sid==sideID || isBot)`; target =
same-side player → `SetLeader(target)`; bot-only sub-branch steals all other
players' money.
Port mapping — **was MISSING** (server only broadcast SET_TEAM_LEADER, no inbound
case). Added:
- `Server.Dispatch.cs:2263-2288` (inbound case) → `Server.Lobby.cs:2079
  HandleSetTeamLeader`.
  E — the current side leader transfers leadership to a same-side player:
  `side.LeaderShipId = target` + whole-mission broadcast (native `SetLeader`; the
  same effect ApplyMutiny reaches). Non-leader refused; named side must be the
  sender's own; target must be a player on that side.
  n/a — native's `isBot` escape hatches and the bot "steal all the other players'
  money" donation grab (WOPR, `:7654-7683`) — no bot-auth concept in this port.
Fix/tests: **added** — dispatch case + `HandleSetTeamLeader`. New
`P850MissingFmHandlersTests.SetTeamLeader_*` (transfer to same-side teammate;
refused for a non-leader; refused when the target is on a different side), all
pass 3/3 alone.

### SIM-0631 FM_CS_SET_MISSION_OWNER — N/A
Native `FedSrv.CPP:7690-7713`: the whole body opens with
`TrapHackBoot(isBot == true)` — ONLY a bot-authentication-guided peer
(`g.szBotAuthenticationGuid`, a WOPR feature) may send it; then `SetOwner`.
**Whole handler N/A**: this port has no bot-role/auth concept, so no legitimate
client can reach the body; mission-ownership transfer already flows through the
port's established rotation/succession broadcasts (`BroadcastSetMissionOwner`).
Pure WOPR-era plumbing. No inbound handler added (deliberately).

### SIM-0633 FM_C_REQPINGDATA — IMPLEMENTED
Native `FedSrv.CPP:7743-7808` (w0dk4 player-pings): `PingQuery()` 5 s debounce;
iterate EVERY connection with a live player (mission/side agnostic); send
`PINGDATA(shipID, ping, loss%)` to the requester only; then the open-window
`PINGDATA(shipID=-1, ping=1)`.
Port mapping (EQUAL): `Server.Dispatch.cs:2211-2230` → `HandleReqPingData` —
5 s debounce, iterate all live players server-wide, reply to the requester only,
open-window sentinel.
Fix/tests: none. `PingDataReqPingDataLiveTests`, `P654DispatchHardeningTests`.

---

## Family score
| metric | before | after |
|---|---|---|
| weighted (credit/weight) | 25.93% (153/590) | 35.26% (207/587) |
| unweighted (complete/eligible) | 25.00% (63/252) | 32.27% (81/251) |

Implemented this batch: 18 rows (SIM-0588, 0592, 0597, 0598, 0610, 0611, 0612,
0613, 0614, 0615, 0616, 0617, 0622, 0624, 0626, 0629, 0630, 0633), weight 54.
Partial: none. n/a: SIM-0631 (weight 3 excluded from both numerator and
denominator). `reachability` stays `production` for every implemented row (all
reachable via a real client's wire message on a configured server); SIM-0631 is
`production`-tagged like sibling n/a rows (the handler exists natively but is
unreachable without bot auth).

## What changed
- **Production code (new behaviour):** inbound handlers for `FM_CS_QUIT_SIDE`
  and `FM_CS_SET_TEAM_LEADER`, which the port previously only broadcast:
  `src/Allegiance.Server/Server.Dispatch.cs` (+2 dispatch cases),
  `src/Allegiance.Server/Server.Lobby.cs` (+`HandleQuitSide`,
  +`HandleSetTeamLeader`), `src/Allegiance.Server/InboundPacket.cs` (+2 decode
  entries), `src/Allegiance.Bot/Bot.cs` (+`SendQuitSide`, +`SendSetTeamLeader`
  for the harness).
- **Tests (new):** `tests/Allegiance.Core.Tests/P850MissingFmHandlersTests.cs` —
  10 tests (2 codec round-trips, 5 QUIT_SIDE, 3 SET_TEAM_LEADER), all pass 3/3
  alone.
- `docs/parity/sim-rules-matrix.csv`: 19 rows updated (18 `partial →
  implemented`, 1 `partial → n/a`) with real `port_cite_or_reason` sites and
  `evidence`. No other rows touched; LF preserved (`grep -c $'\r'` = 0).
- `docs/reports/P850_CERTIFY.md`: this report.

## Light gate
- Whole-solution Debug build (`~/.dotnet/dotnet build AllegianceCore.slnx`):
  **0 warnings / 0 errors**.
- `bash tools/build-tools.sh`: **all 91 tool projects built**, 0 errors.
- Rig AOT publish (`dotnet publish src/Allegiance.Server/... -c Release -r
  linux-x64 --self-contained -p:PublishAot=true -o .scratch/p850-aot`): clean —
  **no IL2026/IL3050** (required because `src/Allegiance.Server` changed).
- New/affected tests 3× alone: `P850MissingFmHandlersTests` — **10/10 pass, 3/3
  runs**.
- ONE `tools/run-tests.sh --tier fast`: 11,082 tests. Update 28/28, Lobby 82/82,
  MonoGame 3604 passed (3 NotExecuted), ClientCore 1865 passed, Core 5487 passed
  with **1 failure** — `ConsoleLogNonBlockingTests.Verbose_gate_silences_and_
  reenables_routed_diag_lines`. This is an already-ledgered console-logging load
  flake (`docs/TEST_FLAKES.md:1776`), unrelated to any path P850 touches (this
  batch adds dispatch/lobby handlers, not console routing); it passes 3/3 alone.
  My 10 new tests passed in the Core quarantine leg (10/10).
- Showcase never-parks 5× vs base: not required — this batch changes dispatch and
  lobby handlers and adds bot *send* methods, not bot/flight/sim behaviour.

## Native-intent classification & modern/better-approach note
Native intent: these 19 handlers are event-driven, server-authoritative FM
lobby/session/team rules. The port now reproduces every behaviour-relevant branch
of all of them (two were genuinely missing and are added here). Notable
divergences, all documented:
- **Better design:** CHANGE_ALLIANCE adds an owner gate and range checks that
  native omits (correcting known native trust-the-client gaps per the port's
  established P184/P186 moderation convention); PLAYER_READY re-encodes the relay
  with the server-derived ship id so a peer can only mark itself ready; the
  broadcast relays are consistently budget-charged (P654 H-24) and exact-size
  re-encoded, bounding the fan-out the native `ForwardMessage` leaves unguarded.
- **Era/architecture n/a:** LOGONREQ's auth stack (zone ticket / Steam DRM /
  SQL / lobby rank), SET_MISSION_OWNER's whole body (WOPR-bot-only), the
  RANDOMIZE_TEAMS flush branch and QUIT_SIDE banned-side-mask leg (no teamlobby /
  no bans / no bot-auth in this port).
- A "better approach" note: these handlers are drained on a single sim thread in
  the port (deterministic ordering, no native lock discipline), and the S→C relay
  layer centralises the fan-out native scatters across `GetGroupSectorFlying`
  call sites.

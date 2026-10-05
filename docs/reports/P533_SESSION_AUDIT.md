# P533 — Session / teams / chat / ranks / scoring parity audit (P1–P200)

Read-only audit (part 3 of 8 of the P1–P200 parity pass). Scope: the 48
commits in `docs/audits/p1-p200/session.txt` plus the session-relevant items
in `other.txt`. Focus: join/ready/start flow, team & side assignment rules,
side limits/balance, chat routing & permissions, rank/score math, mission
parameters (where each value comes from), game-over conditions.

Method: every non-MATCH row was verified by reading both native
(`~/Development/Allegiance/src`) and port (`~/Development/AllegianceCore/src`)
source. Line numbers are cited for both sides. Items already reported by the
2026-09-29 parity pass are marked FIXED/OPEN against current code (many were
fixed by later P-tasks, notably P455 station-elimination + P439 rotation +
P456 chat default + P215 combat rating).

---

## Findings

| item | native file:line | port file:line | verdict | severity | proposed fix |
|---|---|---|---|---|---|
| Typed-chat default target | WinTrek/console.cpp:644 `m_chsChat(CHAT_TEAM)` | Rendering/ChatTargetLogic.cs:30 `DefaultTarget = Team`; Game1.Session.cs:573-587 | MATCH (was DIVERGES in 2026-09-29; FIXED by P456) | – | none |
| Chat Target enum values | Igc/igc.h:2729-2737 | Core/Sim/IgcEnums.cs:12-30 | MATCH (values 0-14 identical) | – | none |
| Chat routing: Everyone/Team/Individual/Group/AllSector/FriendlySector/Leaders/Wing | FedSrv/FedSrv.CPP:3428-3600 (chat dispatch switch) | Server.Lobby.cs:2346-2440 `SelectChatTargets` | MATCH (per-target same-side/all-sector/leader/wing semantics ported) | – | none |
| Chat routing: CHAT_ALLIES | FedSrv/FedSrv.CPP:3443-3465 (forwards to allied sides' groups) | Server.Lobby.cs:2346-2440 — no `ChatTarget.Allies` case → falls to default, message dropped (Server.Lobby.cs:2436-2438) | MISSING | MED | add `case ChatTarget.Allies`: forward to peers whose side is allied to the sender's (reuse P190 `Side.AllyGroup`/`AlliedSides`) |
| Chat routing: CHAT_SHIP / CHAT_ADMIN | FedSrv/FedSrv.CPP:3472-3520, 3467-3470 (child-ship forwarding / ADMIN NYI) | no case; dropped by default | UNPORTABLE (no child-ship/boarding or admin concept in port) | – | document as intentional |
| Chat permissions gate (`commandID == c_cidNone` guard) | FedSrv/FedSrv.CPP:3521,3542,3550 | Server.Lobby.cs:2359-2368 collapses Individual/NoFilter, no command gate | MATCH (orders handled separately via P457/P469 drone path) | – | none |
| ChatBuoy relay (P67) | FedSrv/FedSrv.CPP:5295-5315; clintlib.cpp:2432-2444 (OT_buoy→CHATBUOY) | Server.Dispatch.cs:377-455 | MATCH (routes via same SelectChatTargets) | – | none |
| MUTE(117) | Inc/Messages.h:194 (dead in native, `//NYI` style) | Wire/NotSupportedFedMsgs.cs:106,113 (`Reason.DeadInNative`) | UNPORTABLE (dead in native) | – | none |
| RANK_INFO payload (P130) | FedSrv.CPP:993-1001 / LoadRankInfo :103-150 (standalone: `requiredRanking=0, civ=-1` from ranks.txt) | Wire/RankInfo.cs:105-153 (FromRanksFile, standalone shape), sent at join Server.Lobby.cs:3091 | MATCH (both use standalone branch, requiredRanking=0/civ=-1; struct stride 36 verified) | – | none |
| RANK_INFO send timing | FedSrv.CPP:998-1001 (after LOGONACK) | Server.Lobby.cs:3084-3092 (in join burst) | MATCH | – | none |
| Score object non-persistence | igc.h:5453/5510 (per-session reset) | Server/PlayerScore.cs:30-49 | MATCH (structural: no accounts → in-memory only, deliberate) | – | none |
| Combat rating formula (P215) | common.cpp:2706-2717 `AdjustCombatRating`; constants igc.h:92-93 (c_fcidRatingAdd=28, c_fcidRatingDivide=29) | Server/PlayerScore.cs:126-132 (`k=(add+killee-killer)/divide`), indexes at :57-63 | MATCH | – | none |
| Kills/deaths counting | FedSrv.CPP:12026-12032, igc.h:5819/5837 | Server.Combat.cs:1386 (AddDeath), :1436 (AddKill) | MATCH | – | none |
| Full score breadth (base kills/captures, tech recovered…) | igcWrapper.h:2142-2200 PlayerScoreObject | Server/PlayerScore.cs:51-95 (only Kills/Deaths/CombatRating/Rank/Score) | DIVERGES (documented Stage-2 gap; rank/score unmutated) | LOW | later scoring task (already planned as PlayerScore Stage 2) |
| Rank seeding | igc.h (neutral seed) | PlayerScore.cs:67-73 (Rank=0, Score=0) | MATCH (given no persistence) | – | none |
| Side assignment / leadership on join (AddPlayerToSide) | fsmission.cpp:539-661 | Server.Lobby.cs:2199-2309 `TryJoinSide` | MATCH (leader=first, mission-owner, QUIT_SIDE/JOIN_SIDE/leader/owner broadcasts, auto-donate) | – | none |
| Side limits / balance on join (nMaxPlayersPerTeam) | fsmission.cpp:4052 `nAvailablePositions = nMaxPlayersPerTeam - nNumPlayersOnSide` (position request); CheckPositionRequest fsmission.cpp:4590 | no side-cap anywhere (GameConfig/ServerConfig absent; P184 handler drops balance gate — Server.Dispatch.cs:883-885, 603-608) | MISSING | HIGH | add per-side capacity check in TryJoinSide/P184 accept path using a configurable nMaxPlayersPerTeam (default off = unlimited preserves today) |
| Position request accept/reject (P184) | FedSrv.CPP:6658-6707 / RequestPosition fsmission.cpp:4729-4774 | Server.Dispatch.cs:594-741 | MATCH for reachable gates (side-exists, LockSides, leaderless auto-accept, leader accept/reject); defection/bans/squads/invite/rank gates N/A (documented) | – | none |
| FORCE_TEAM_READY (P184) | fsmission.cpp:5508-5509 (force-ready branch) | Server.Lobby.cs:867-874 (`side.ForceReady`) | MATCH | – | none |
| Ready aggregation (P180) | fsmission.cpp:5492-5529 CheckForSideAllReady (nMinPlayersPerTeam floor + force + per-player ready) | Server.Lobby.cs:838-890 RecomputeSideReady | MATCH (floor collapses to ≥1 connected player — no nMinPlayersPerTeam; documented stand-in) | LOW | add nMinPlayersPerTeam config → floor; currently flat 1 |
| Ready auto-start policy | native StartGame is external-admin/Zone driven (AdminGame.cpp), StartCountdown fsmission.cpp:1926-1976 | Server.Lobby.cs:943-958 TryAutoStart (per-mission all-occupied-ready), :967-976 IsAllOccupiedSidesReady | STAND-IN (deliberate substitute, no external admin layer; documented) | – | none |
| Briefing countdown length (P188) | c_fMissionBriefingCountdown = 15.0f (igc.h:1005) | Server.cs:236 `BriefingCountdownSeconds = 15.0f` | MATCH | – | none |
| START_GAME owner/stage gate | FedSrv.CPP:7597-7621 | Server.Dispatch.cs:870-918 | MATCH (mission-owner, NotStarted, all-occupied-ready; bot auth N/A) | – | none |
| Ballot vote duration | c_fVoteDuration = 30.0f (fsmission.cpp:5887,5946) | Core/Model/PendingBallot.cs:77, Server.Lobby.cs:1300 | MATCH | – | none |
| Ballot tally rules (majority per side) | fsmission.cpp:5836-5879 HasPassed/AllVotesAreIn/CastVote | PendingBallot.cs:110-149 | MATCH | – | none |
| Ballot side-effects (mutiny/resign/draw) | fsmission.cpp:6100-6194 OnPassed | Server.Lobby.cs:1438-1640 | MATCH for mutiny (leader+wallet transfer), draw; resign substitutes withdrawal for native DeactivateSide (honest stand-in) | LOW | resign → DeactivateSide once side deactivation is standardised |
| UpdateAlliances (P190) | fsmission.cpp:5638-5811 | Server.Lobby.cs:1931+ | MATCH (merge/clear algorithm, unconditional CHANGE_ALLIANCES broadcast) | – | none |
| RANDOMIZE_TEAMS (P195) | fsmission.cpp:5133-5260 RandomizeSides (flush to lobby then highest-rank-first to lowest-rank-sum side) | Server.Lobby.cs:1802-1896 RebalanceSides (no physical lobby; direct lowest-rank-sum greedy, `Rank<1→1` floor :1797,1873) | MATCH (rank-sum equilibrium equivalent; mechanism simplified — no SIDE_TEAMLOBBY) | – | none |
| RANDOMIZE_TEAMS iMaxImbalance / banned-side / AFK filters | fsmission.cpp:5183-5196, 5230-5240 | Server.Lobby.cs:1824-1828 (documented omissions) | STAND-IN (no banned-side mask / iMaxImbalance concept) | LOW | add iMaxImbalance player-count override |
| Game-over by peer-departure | fsmission.cpp:3767-3788, 5371-5427 | Server.Lobby.cs:54-111 CheckForVictoryByInactiveSides | ~~MATCH~~ → **DIVERGED (FIXED by P548)** — see dated note below | HIGH | P548 |
| Game-over by station elimination | fsmission.cpp:3698-3714 (always a win) + DeactivateSide :5318-5427 | Server.Lobby.cs:139-155 OnSideLostRestartStation + :193-271 DeactivateSide | MATCH (was MISSING in 2026-09-29; FIXED by P455) | – | none |
| Game-over → mission rotation | missionigc.cpp:3804-3828 Reset + regenerate | Server.Lobby.cs:403-461 RotateMissionAfterGameOver + :501-597 MigrateConnectedPeers | MATCH (was in-flight P439 in 2026-09-29; FIXED) | – | none |
| GAME_OVER_PLAYERS per-player roster | fsmission.cpp:3094-3141 QueueGameoverMessage (time-played>0, real side, ≤50 chunking) | Server.Lobby.cs:656-749 (P194) | MATCH (time-played filter collapses to "is seated" — no such counter; ≤50 chunking omitted, documented) | LOW | none urgent |
| Conquest/territory/prosperity/artifact/flags/kills win modes | fsmission.cpp:3568-3640,3749-3760,3883-3894 | Core/Model/Mission.cs:150-252 (implemented) | DIVERGES (reachable only if flags set; zero setters in src/tools, ServerConfig has no gameType → still OPEN, same as 2026-09-29) | MED | add a `gameType`/goals block to ServerConfig that sets the Mission win-mode flags + goals at assembly |
| MissionParams parsing | MissionParams (MessageDef/igc.h) fully populated in native | MissionDef.cs:52-56 — only szIGCStaticFile/verIGCcore/nTeams/civIds populated, rest zero-filled | STAND-IN (only the fields the join path reads are ported; deliberate) | – | none |
| MISSIONDEF static-core name/version (P29) | clintlib.cpp:4193-4224 (extensionless name + version word) | MissionDef.cs:82-86; Server.Lobby.cs:2769-2770 | MATCH (verified empirically in P29) | – | none |
| Initial miners per team | nInitialMinersPerTeam default 1 (igc.h:1214,1330), fsmission.cpp:2196-2217 | Server.Lifecycle.cs:483-570 SpawnInitialMiners, DefaultInitialMinersPerTeam=1 | MATCH (value); LOW: drone hull = lowest-HullId, not drone-type hull; miner type not civ-specific (parity-E #2/#84, OPEN) | LOW | use `DataDroneTypeIGC.HullTypeId` + per-civ CanBuy selection in SpawnNpcShip |
| Join-sync burst order & content (P29/P31a/P141) | fsmission.cpp AddPlayerToMission order; appmsg.cpp:2150-2215 | Server.Lobby.cs:2765-2932 CompleteJoinAndLaunch (MISSIONDEF→JOINED_MISSION→MISSIONDEF sides→EXPORT→PLAYERINFO→SET_CLUSTER→launch) | MATCH (empirically corrected order, heavily commented) | – | none |
| SET_BRIEFING_TEXT source (P121) | fsmission.cpp:1960-1971 (story text if present else base cluster name, fGenerateCivBriefing=true) | Server.Lobby.cs:3038-3077 (always the generate-civ-briefing branch with the joining side's friendly-station cluster name) | MATCH (no mission story-text field ported, so always the cluster-name branch — native's default) | – | none |
| Cross-side visibility sweep cadence (P132) | c_nPassesPerUpdate=10 (clusterIGC.h:22, clusterIGC.cpp:348) | Core/Model/Sector.cs:140 `c_nPassesPerUpdate=10`, :758 | MATCH | – | none |
| Scanner range / signature / LOS source | shipIGC.h:357, stationIGC.h:322, probeigc.h:165-177 (GA-scaled); radar constant from core | Sector.cs:3775-3894 (EffectiveScannerRange/EffectiveSignature, GA factors via GlobalAttributeSet) | MATCH | – | none |
| STATIONS/ASTEROIDS/PROBES update cadence (P133-135) | cluster-level update every n passes | Server.Broadcast.cs:1414-1570 (every 10 passes via c_nPassesPerUpdate) | MATCH | – | none |
| LIGHT/HEAVY_SHIPS_UPDATE (P136-138) | Inc/Messages.h:890-897; CFSDrone::GetLightShipUpdate fsship.cpp:1445 | Wire/LightShipsUpdate.cs / HeavyShipsUpdate.cs; Broadcast.cs:1802-1886 | MATCH (codec stride + fields ported) | – | none |
| Station damage: shield-absorbs-then-overflows (P125) | stationIGC.cpp:116-208 | Core/Sim/Station.cs:325-543 ReceiveDamage | MATCH (incl. GA-scaled max HP stationIGC.cpp:594-613 vs EffectiveMaxArmor) | – | none |
| Warp-bomb (P101) | FedSrv warp missile trigger | whole-mission WARP_BOMB(188) broadcast | covered by P540c wire-parity audit | – | see P540c |

---

## Top fixes (ranked, sized as candidate P-tasks)

1. **Side capacity / balance on join (HIGH).** Add a configurable
   `nMaxPlayersPerTeam` (per GameConfig) and enforce it in `TryJoinSide` /
   the P184 position-request accept path — refuser with
   `DELPOSITIONREQ(DPR_SideFull)` when the target side is at cap. Smallest
   slice: one config field + one check in the P184 accept branch
   (Server.Dispatch.cs ~664) + a guard in TryJoinSide. Mirrors native
   fsmission.cpp:4052.

2. **CHAT_ALLIES routing (MED).** Add the missing `ChatTarget.Allies` case to
   `SelectChatTargets` (Server.Lobby.cs:2346) forwarding to peers whose side
   is allied with the sender's, reusing P190's `Side.AllyGroup`. Tiny;
   only reachable once P190 alliances are set.

3. **Enable goal-based win modes (MED).** Add a `gameType`/goals block to
   ServerConfig that sets `Mission.ConquestEnabled/TerritoryEnabled/
   ProsperityEnabled/KillsEnabled` + goal counts at `AssembleGameMission`
   (Server.Lifecycle.cs:852) so games can actually be won by conquest/
   deathmatch — not only by outlasting or station elimination. Medium task;
   the underlying machinery already exists (Mission.cs:150-252).

4. **Initial-miner hull/drone-type fidelity (LOW).** In `SpawnNpcShip`, use
   the drone type's real `HullTypeId` and pick the miner type each side's
   station can buy (fsmission.cpp:2202 `pstation->CanBuy(pdt)`) instead of
   lowest-HullId/lowest-id. Tiny.

5. **Resign ballot → DeactivateSide (LOW).** Route `#resign` OnPassed through
   the now-real `DeactivateSide` (Server.Lobby.cs:193) instead of the
   withdrawal substitution, since P455 made side deactivation a first-class
   path.

6. **Ready floor from config (LOW).** Promote the "≥1 connected player"
   readiness floor (Server.Lobby.cs:861-866) to a configurable
   `nMinPlayersPerTeam`, honoring native fsmission.cpp:5501-5503.

---

## Coverage

Every commit in session.txt and the session-relevant other.txt items was
examined. Depth varied by likelihood of divergence:

- **Deep (formulas/constants read in full on both sides):** chat routing &
  ChatTarget enum (P25/P33/P67), chat default target (P456), RANK_INFO (P130),
  PlayerScore combat-rating/kills/deaths (P193/P215), ready/start flow
  (P180/P188), TryJoinSide leadership (P183), position request (P184),
  ballots (P187), alliances (P190), RANDOMIZE_TEAMS rank-sum (P195),
  game-over/elimination/rotation (P178/P191/P455/P439/P480), mission-param
  sourcing (P29/P141), station damage (P125), cross-side visibility cadence +
  scanner math (P131/P132), STATIONS/ASTEROIDS update cadence (P133-135),
  LIGHT/HEAVY codecs (P136-138).
- **Medium:** join-burst ordering (P29/P31a/P121/P141/P64), initial-miner
  spawn (P382/P62-64), PINGDATA/REQUEST_MONEY (P199/P200), MUTE/N/A
  declarations (P129/P124), DeactivateSide details.
- **Brief (presence + place in codebase, judged low-risk / already-parity-
  audited elsewhere):** missile/mine/probe static sim broadcasts (P90/P94/
  P99), warp-bomb (P101 — owned by P540c), asteroid destruct (P102), VIEW_
  CLUSTER/TELEPORT_ACK (P106/P110), HIGHLIGHT_CLUSTER/SET_WINGID (P142/P146),
  server-directory lobby (P174/P176/P177), AOT/infra (P175/P185/P20/P60/P72),
  RelaunchShip (P114/P116).

Known 2026-09-29 findings re-checked against current code: typed-chat default
**FIXED** (P456); station-elimination win **FIXED** (P455); game-over→rotation
**FIXED** (P439/P480); goal-based win modes **STILL OPEN** (no setter);
initial-miner drone hull/civ-specific selection **STILL OPEN**; score breadth
**PARTIAL** (Stage-2 planned).

---

## Dated correction (2026-10-05) — P548: "Game-over by peer-departure" was NOT MATCH

This report's row 51 rated native's peer-departure win-by-elimination as MATCH.
That was **wrong** and is corrected here. Claude's spot-check (2026-10-05, wiki
`allegiance-p1-p200-parity-audit`) found, and re-verified, the divergence:

**Bug.** The port's `CheckForVictoryByInactiveSides(Mission)`
(`Server.Lobby.cs:54-111`) returned early **only** when
`mission.Stage == MissionStage.Over`, and `ReleasePeer` called it on **every**
disconnect (`Server.Lifecycle.cs:~1226-1230`). So a peer drop that left ≤ 1
side with players declared game-over **in a NotStarted (lobby) mission too** —
the live "Stage Over fires on every peer drop" bug (fires 4× across a peer
chain). Native only deactivates a side — the step that leads to the victory
check — when
`bool fDeactivate = (STAGE_NOTSTARTED != m_misdef.stage) && !HasPlayers(psideOld, false);`
(`FedSrv/fsmission.cpp:1147`), then `DeactivateSide` → …
→ `CheckForVictoryByInactiveSides` (`:5381`). A NotStarted lobby drop never
deactivates/ends in native.

**Fix (P548).** Added native's stage + side-empty gate:
`ReleasePeer` now deactivates the just-emptied side only when
`mission.Stage != NotStarted && side now has no players`
(`fsmission.cpp:1147-1149`), via a new `DeactivateSideForPeerDeparture`
(`Server.Lobby.cs`, keeps P388's debug exemption), and
`CheckForVictoryByInactiveSides` now also returns early on `NotStarted`. See
`docs/reports/P548_GAME_OVER_STAGE_GATE.md`.


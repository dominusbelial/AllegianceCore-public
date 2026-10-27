# P841 — certify the event-driven mission rules: players & sides

Base: public master at dispatch (`1e4c33d4`). Batch: the 23 P841 rows of
`docs/parity/sim-rules-matrix.csv` (weight 48). Changes are left in the working
tree; nothing committed or pushed; no rig/Mac contact. All native line numbers
re-grepped against `~/Development/Allegiance/src/`.

## Result

**Family weighted score: 0% → 87.5% (42/48).** 20 of 23 rows `implemented`;
`SIM-0496` stays `partial` (two divergent call sites, both design/era);
`SIM-0761` stays `partial` (function ported and tested; its ProcessGameOver
call sites are inert without a persisted career score); `SIM-0497` is `n/a`
(dead code in native — zero call sites). `scripts/parity-scorecard.py`
validates.

| Row | Native body (branches) | Port sites | Verdict | Fix / test |
|---|---|---|---|---|
| SIM-0484 InitSide (fsmission.cpp:33-62) | side name, civ≠NA assert, leader NA, player count 0, autoAccept **true**, ready/forceReady false, active true, allies NA, money 0 | `Side.cs` field defaults; `Mission.Assemble`; leaderless auto-accept in `Server.Dispatch.cs` POSITIONREQ | was **different**: `Side.AutoAccept` seeded `false`, so a POSITIONREQ to a *led* side pended for approval where native seats immediately (`RequestPosition`, fsmission.cpp:4759) | **FIX**: seed `AutoAccept = true` (fsmission.cpp:58). Test `P841MissionRulesCertifyTests.Side_init_seeds_native_InitSide_defaults` (fails pre-fix). Leader-vetting tests updated to toggle AUTO_ACCEPT(false) first — the exact native leader flow |
| SIM-0485 CFSMission ctor (:79-255) | misparms, core default, autorestart, params/initialize, description, owner NA, stage, static core, civ draw, InitSide×N, list add, UpdateSides, invitations, AGC | `AssembleGameMission` (Lifecycle 1217+), `Mission.Assemble`, `MatchCivilizations.Resolve` | **equal** (invitations/AGC/DirectPlay groups/club flags = era) | existing assembly corpus tests |
| SIM-0486 ~CFSMission (:258-358) | AGC event, Vacate, remaining-player removal, container teardown, stage TERMINATE, MISSION_GONE, list removal, Terminate, ballots | `RotateMissionAfterGameOver` (Lobby 525+), `Shutdown` | **equal** (MISSION_GONE to the FM lobby = era; heartbeat + match report cover it) | MissionRotationLiveTests, P480, P548 |
| SIM-0487 AddPlayerToMission (:369-449) | old-player restore, group add, lobby info, side set, damage track, SendPlayerInfo, lobby PLAYER_JOINED, dirty flags | `CompleteJoinAndLaunch`, `AssignShip`, `AnnouncePlayerToMission` | old-player score restore was **missing** → **FIX** (see SIM-0521); rest equal (groups/lobby msgs = era) | `P841MissionRulesCertifyTests.Rejoining_player_restores_cached_score`; P775 moments tests |
| SIM-0488 RemovePlayerFromMission (:460-525) | score disconnect, SaveAsOldPlayer, RemovePlayerFromSide, join-request removal, QUIT_MISSION, side clear, lobby PLAYER_QUIT, empty-mission delete | `RetireDepartedPeer`/`ReleasePeerWithReason` (Lifecycle 1959+) | **equal** after the SIM-0490/0521 fixes (lobby PLAYER_QUIT = era) | LivePositionRequestTests.Requester_disconnect…; P841 departure tests |
| SIM-0489 AddPlayerToSide (:539-834) | guards, switch leg, owner/leader election, broadcasts, squads, wings, auto-donate, briefing, mission info, flags, lifepod, dock, ENTER_GAME | `TryJoinSideCore` (Lobby 3035+), `CompleteJoinAndLaunch`, P820b squad establishment | **equal** except one **design** divergence: a mid-game side switch keeps the ship in flight instead of native's dock-at-new-base re-seat (fsmission.cpp:791-796) — restructure deferred, documented below | LiveSideSelectTests, LivePositionRequestTests, PlayerDonateTests, P775 |
| SIM-0490 RemovePlayerFromSide (:848-1165) | flag drop, child promotion, leader succession, auto-accept on empty side, owner migration, payday-to-treasury, QUIT_SIDE, donation re-point, deactivation, boot mask | `HandleLeaderSuccession` (Lobby 2923+), `RetireDepartedPeer`, `MoveToTeamLobby` | three branches were **missing** → **FIX**: (1) departing flag carrier drops/respawns the flag (fsmission.cpp:875-893); (2) payday-to-treasury: wallet + full ship value (`IshipIGC::GetValue` port `ShipFullValue`, shipIGC.h:396-406) → side treasury + immediate DoPayday (fsmission.cpp:1042-1063, 1777); (3) auto-donate relationships re-point to the promoted leader / clear (fsmission.cpp:1046-1057) — the latter also fixes real income loss (payday credits dropped on terminated donate targets) | `P841MissionRulesCertifyTests.Departing_flag_carrier_drops_flag_treasure`, `.Departing_player_credits_hull_value_to_side_treasury` (both fail pre-fix); LiveGainFlagTests |
| SIM-0496 RemovePlayerByName (:1248-1273) | case-insensitive scan, boot, return found | logon duplicate handling `Server.Dispatch.cs:201-212` | **partial**: caller 1 (logon duplicate, FedSrv.CPP:629) — the port deliberately **refuses the newcomer** instead of evicting the seated player (recorded security design); caller 2 (lobby `FM_L_REMOVE_PLAYER`, fslobby.cpp:196) has no modern-lobby counterpart (era/design) | P762 moderation tests |
| SIM-0497 RemovePlayerByCDKey (:1289-1314) | body | — | **n/a**: dead code in native — grep of the whole tree finds only the declaration (fsmission.h:274) and definition; the CD-key boot flow routes through RemovePlayerByName | inventory tool |
| SIM-0498 GetCountOfPlayers (:1329-1339) | ISPLAYER + ghost filter, side/mission scope | `CountPlayersOnSideExcluding` (Lobby 2893), FAllReady loops | **equal** (parked peers = the ghost analogue, excluded from `_peerShips`) | P183/P178 tests |
| SIM-0499 GetSideRankSum (:1353-1369) | rank sum, floor at 1, ghost filter | `FinalizeMissionScores.RankSum` (Lobby 1127), `GetLeaderRankSum` (2570) | **equal** | PlayerScoreFoundationTests, P583 |
| SIM-0500 GetRankThreshold (:1380-1407) | integer formula, whole-ship-list divisor | `GetRankThreshold` (Lobby 1619) | **equal** (divisor = sector models ∪ peers, incl. docked) | P762b tests (SIM-0142) |
| SIM-0501 HasPlayers (:1422-1431) | emptiness test, ghost filter | `_peerShips` emptiness checks (victory, election, deactivation) | **equal** | LiveSideInactiveTests, P548 |
| SIM-0502 GetBase (:1433-1447) | first `c_sabmRestart` station of the side | `HomeSectorForSide` (Server.cs 969), `RepositionShipToSideHome` (Lobby) | was **different**: home anchoring picked the first side-*owned* station, not GetBase's restart-capable one | **FIX**: both resolvers prefer the SabmRestart station (fallback to first owned, where native returns NULL and callers need an anchor). Test `P841MissionRulesCertifyTests.Home_reposition_prefers_restart_capable_station` (fails pre-fix) |
| SIM-0520 Vacate (:2604-2622) | drone deletion, player Reset(true) | `RotateMissionAfterGameOver` + `MigrateConnectedPeersIntoRotatedMission` | **equal** (null-cluster lifepod walk n/a) | MissionRotationLiveTests, P480 |
| SIM-0521 SaveAsOldPlayer (:2624-2680) | old-player cache: score, sideID, lastSide, bannedSideMask branches, lifepod, characterID | new `_oldPlayerScores` (Server.cs) + `ApplyDeparturePaydayAndOldPlayer` + `TryTakeOldPlayerScore` | whole cache was **missing** → **FIX**: departure saves the live score object under (mission, case-insensitive name); same-name rejoin restores it and consumes the entry (fsmission.cpp:378-401); entries die with the mission at rotation. bannedSideMask/lastSide/lifepod legs n/a (no defection-ban model / lobby lifepods — documented) | `P841MissionRulesCertifyTests.Rejoining_player_restores_cached_score`; `PlayerScoreFoundationTests.Player_score_is_discarded_across_a_disconnect_and_reconnect` rewritten to the native contract |
| SIM-0539 GetMission (:4344-4353) | cookie scan over s_list | `Server.Missions` (726) + direct references | **equal** (single live mission per game slot + pending rotation; cookie = modern lobby match id) | MissionRotationLiveTests |
| SIM-0540 GetMissionFromIGCMissionID (:4356-4365) | IGC-id scan | `Server.Missions`; IGC mission id = Skills match number | **equal** | MissionRotationLiveTests |
| SIM-0760 InitializeCivList (:64-77) | list build + swap-remove draw with exhaustion refill | `MatchCivilizations.Resolve/Pool` | **equal** (n==1 refill ≡ count==0 refill; bonus-civ hack is commented out in native; P825 legal-pool = documented design) | P806/P825 tests |
| SIM-0761 GetExpMult (:3144-3158) | guards, avgExp over other sides, 0.1 floor | new `PlayerScore.GetExpMult` (exact port) | **partial**: function ported + pinned (`GetExpMult_matches_native_branches`); its three ProcessGameOver call sites (fsmission.cpp:3308/3325/3354) stay inert because the multiplier consumes the persisted career score (`timePlayed·persist.GetScore()`) which the port does not have (no stats DB, P583) — applying it to session scores would diverge from native's career-based anti-stacking | `P841MissionRulesCertifyTests.GetExpMult_matches_native_branches` |
| SIM-0764 SetCharStats (FedSrv.CPP:1045-1187) | zero-time early return, rank table update, CQCharStats post | `ReportMissionResultsToLobby` (Lobby 1188+) | zero-time peers were reported → **FIX**: skip `TimePlayed <= 0` (FedSrv.CPP:1051-1054). Rank-table branch n/a (lobby owns progression; no rank table) | P583 tests + LobbyHeartbeatTests cover the report path |
| SIM-0769 SendPlayerInfo (FedSrv.CPP:2056-2140) | persist list, stats, drone id, side, leader/owner flags, audience | `BuildPlayerInfo`/`AnnouncePlayerToMission` (Broadcast 1877+) + join burst | **equal**: persist list = single NA entry (documented P774 — every native client rank reader uses NA); leader/owner flags zeroed in the flags byte but the native client ignores that byte (SetReady commented out, appmsg.cpp:2289) and leader/owner reach the client via SET_TEAM_LEADER/SET_MISSION_OWNER broadcasts — equal information, different carrier | P775/P774/Hud20 wire tests |
| SIM-0839 GetNextCharacterID (FedSrv.CPP:67-88) | synthetic character-id counter + name→id cache for departed players | `Server.Identity.cs` AccountId (JWT identity) | **equal-by-substitution**: stable verified AccountId replaces synthetic ids — stronger (survives restarts); the counter itself is pure era plumbing | identity tests + PlayerScoreFoundationTests |

## Fixes shipped (each with a fail-first test)

1. **`Side.AutoAccept` seeds `true`** (`src/Allegiance.Core/Model/Side.cs`) —
   fsmission.cpp:58. Native lifecycle: InitSide seeds true; only AUTO_ACCEPT(52)
   or the flush-teams command turns it off; RequestPosition seats immediately
   while on. Affected port tests updated to reproduce the native leader-vetting
   flow (toggle off first): LivePositionRequestTests (×8 sites),
   LiveTeamLobbyToggleTests (×12 sites), P655GameStateIntegrityTests.H13,
   RandomizeTeamsLiveTests.Rebalance_clears_pending_join_requests.
2. **Departure effects of RemovePlayerFromSide** (`Server.Lifecycle.cs`):
   `DropFlagOnSideDeparture`, `ApplyDeparturePaydayAndOldPlayer` (+ `ShipFullValue`
   GetValue port), `RePointDonationsAfterDeparture`; `RetireDepartedPeer` gained
   the player-name parameter (passed from `ReleasePeerWithReason` and the three
   reconnect-grace retire sites). `MoveToTeamLobby` (squad change) zeroes the
   wallet into the treasury (ship-value leg omitted there — the port's side-quit
   keeps the ship alive, so crediting its value would mint credits; documented).
3. **Old-player score cache** (`Server.cs` `_oldPlayerScores`, `Server.Lobby.cs`
   `TryTakeOldPlayerScore` in `AssignShip`, clear-at-rotation) — the port of
   `m_oldPlayers`/`SaveAsOldPlayer`/`GetOldPlayerLink`.
4. **Home anchoring prefers the restart-capable station** (`Server.cs`
   `HomeSectorForSide`, `Server.Lobby.cs` `RepositionShipToSideHome`) — the
   GetBase contract. `RepositionShipToSideHome`/`StationRuntimesForSide` made
   `internal` for the test.
5. **`PlayerScore.GetExpMult`** — exact pure port + tests (call sites inert,
   row stays partial, reason recorded in the CSV).
6. **`ReportMissionResultsToLobby` skips zero-time-played peers** —
   FedSrv.CPP:1051-1054.

## Verification

- `dotnet build AllegianceCore.slnx`: **0 warnings / 0 errors**.
- `tools/build-tools.sh`: all 90 tool projects built.
- Rig AOT publish (`dotnet publish src/Allegiance.Server -c Release -r linux-x64
  --self-contained -p:PublishAot=true`): clean, no IL2026/IL3050.
- New/affected tests 3× alone: `P841MissionRulesCertifyTests` 6/6 ×3. Fail-first
  verified by reverting the five source fixes (keeping the new pure function)
  and re-running: Side_init / Rejoining / Home_reposition / Departing_flag /
  Departing_player all failed pre-fix, pass post-fix.
- ONE `tools/run-tests.sh --tier fast`: 10,973 tests — **one failure**:
  `P775PlayerInfoMomentsTests.Mid_game_sighting_of_a_joined_enemy_sends_no_playerinfo`,
  which is **red at base** (fails 3/3 alone on this worktree with all P841
  source changes reverted) and is recorded in `docs/TEST_FLAKES.md`. P841
  touches no PLAYERINFO join-announcement path; the P775 owner should re-derive
  the expected announcement count.
- Showcase never-parks (Live tier, `P700ShowcaseLiveTests.Local_showcase_never_parks_with_target_for_five_seconds`)
  5× vs base behaviour: runs 1/2/4/5 and two immediate retries **pass**; run 3
  failed once in a load window (no `Parked >5s` sample captured, other lanes
  running suites on the shared host) — recorded as a load flake in
  `docs/TEST_FLAKES.md`. Raw logs: `~/allegiance-evidence/P841/`.

## Reachability

All 23 rows keep `production` (or `n/a` for the dead-code body): every port
site is reachable from real config/lobby/client paths (logon, POSITIONREQ,
departure, game-over rotation, lobby report).

## Native-intent classification + modern/better-approach note

Native intent: the CFSMission player/side bookkeeping is an *event-replay
ledger* — every join/leave/boot rebroadcasts the whole team state (groups,
leaders, owner, money, masks) because DirectPlay clients hold no authoritative
copy. The port keeps the state machine but replaces the group-broadcast
transport with per-peer packets and computed counts; that is the right
modernization, and this batch closed the places where the *state machine*
itself (not the transport) had drifted: auto-accept seeding, departure
money/flag/donation effects, old-player score continuity, and GetBase's
restart-capable home rule.

The one deliberate standing divergence is the mid-game side switch keeping the
ship in flight (SIM-0489 note): native tears the player down to the team lobby
and re-seats them docked at the new side's restart base. A better approach than
restructuring `TryJoinSideCore` would be to reuse the rotation migration's
re-seat machinery (`AssignMigratedShip`-style fresh seat) for in-mission side
switches when the mission has started — it already produces exactly native's
post-switch state (fresh spawn loadout, docked at the new home base). The
remaining SIM-0761 work likewise wants the persistent-stats layer: once a
career score exists per verified AccountId, `GetExpMult`'s call sites slot into
`FinalizeMissionScores` unchanged.

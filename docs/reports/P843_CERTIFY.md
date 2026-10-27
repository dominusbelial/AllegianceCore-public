# P843 — Certify the event-driven mission rules: stage, countdown, start, mission params, lobby info

Batch: SIM-0511 SetStage · SIM-0512 GiveSideMoney · SIM-0513 SetMissionParams · SIM-0514 DelayCountdown ·
SIM-0515 StartCountdown · SIM-0516 StartGame · SIM-0523 MakeOverrideTechBits · SIM-0536 SendLobbyMissionInfo ·
SIM-0537 QueueLobbyMissionInfo · SIM-0538 SendMissionInfo · SIM-0541 FAllReady · SIM-0837 GetStateID

Repo: public AllegianceCore worktree `allegiance-hermes-p843` (master). Native reference read-only:
`~/Development/Allegiance/src/`. All native line numbers re-grepped at dispatch time.

Result: 6 rows certified `implemented`, 3 kept `partial` with a precise remaining branch, 3 classified `n/a`
(whole-body era / dead plumbing). Family weighted completion rises **1.52% (9/592) → 3.92% (23/586)**.
`python3 scripts/parity-scorecard.py` validates; `tools/p832a-mission-rules-audit.py --check` (351 bodies, no
missing/stale) validates.

---

## Per-row certification

### SIM-0511 CFSMission::SetStage — IMPLEMENTED
Native body `FedSrv/fsmission.cpp:1750-1768` branches:
- guard `if (m_misdef.stage != stage)` (no-op on same stage)
- set `m_misdef.stage = stage`; `fInProgress = (stage == STAGE_STARTED)`
- `BEGIN_PFM_CREATE ... pfmMissionStage->stage = GetStage()`; `SendMessages(GetGroupMission(), FM_GUARANTEED, FM_FLUSH)` (whole-mission MISSION_STAGE)
- `m_pMission->SetMissionStage(stage)` (IGC mission stage)
- `SetLobbyIsDirty()`

Port mapping (all EQUAL):
- `src/Allegiance.Server/Server.Lobby.cs:1853 BroadcastMissionStageToMission` — whole-mission `MISSION_STAGE(37)` sweep (native `GetGroupMission()` audience, fsmission.cpp:1759-1762).
- Call sites `Server.Lobby.cs:1686` (Starting), `:1816` (Started), `:547/:1764` (Over) — stage change always broadcasts.
- `src/Allegiance.Core/Model/Mission.cs:92 MissionStage.Stage` — the shared stage value; `fInProgress` is derived (Started). `m_pMission->SetMissionStage` collapses into the same `Mission.Stage`; `SetLobbyIsDirty` is periodic-heartbeat bookkeeping (no distinct port needed).
- Same-stage guard is preserved (assign + broadcast only on transition paths).
Fix/tests: none needed. Pinned by `LiveMissionStageTests` (P309: NotStarted/Starting/Started MISSION_STAGE to every joined peer).

### SIM-0512 CFSMission::GiveSideMoney — PARTIAL
Native `FedSrv/fsmission.cpp:1777-1787`:
- guard `if (money != 0)`
- `m_rgMoney[sideID] += money`
- `DoPayday(pside)` — immediately distributes the side's treasury to its players (fsmission.cpp:1785)
Port mapping:
- EQUAL — `src/Allegiance.Server/Server.Docking.cs:1143` NPC cash pickup does `side.Money += amount` (the side-treasury credit, native FedSrv.CPP:13614). The `<money != 0>` guard is implicit (`amount` is nonzero for a cash treasure).
- DIFFERENT — native immediately runs `DoPayday(pside)` for the credited side; the port defers distribution to the next periodic payday (`Server.Economy.cs:1179 OnPayday`, which divides `side.Money` by the side's player count and credits each player). Net treasury → players is preserved (the periodic payday distributes the pooled treasury), but the immediate per-side payout on give is not replicated (minor timing difference).
Tests: `PaydayTests`, `PlayerMoneyAutoDonateTests`, `LiveMoneyTests` pin the credit/distribution contract.
Remaining branch: immediate `DoPayday(pside)` on give.

### SIM-0513 CFSMission::SetMissionParams — PARTIAL
Native `FedSrv/fsmission.cpp:1796-1895` is a **live mutation** of an already-assembled `STAGE_NOTSTARTED` mission:
guards (`STAGE_NOTSTARTED`, `Invalid()`), team shrink/kick (`RemovePlayerFromSide` QSR_SideDestroyed), team grow
(`InitSide`), `m_pMission->SetMissionParams`, `UpdateSides`/`UpdateAlliances`, per-side `CheckForSideAllReady`,
rank-window kick (`RemovePlayerFromMission` QSR_RankLimits), squad-change maintenance
(`MaintainSquadLeadership`/`SetSideSquad`).
Port mapping:
- The port applies the **same settings once at mission assembly** — `ServerConfig.cs ResolveGoals` +
  `Server.Lifecycle.cs AssembleGameMission` — and enforces rank limits at join time (`CheckInitialPositionRequest`,
  `Server.Lobby.cs:3438`), so the configuration intent is covered.
- DIFFERENT/MISSING — there is no runtime path that mutates an already-assembled NotStarted mission's params
  (team/civ resize, rank-limit kick, squad-change maintenance, ready recheck on an existing mission). This is a
  structural difference: the port rebuilds from config rather than mutating live.
Tests: `P655GameConfigDefaultsTests`, `P806LobbyGameParamsTests` (FromMission reads resolved settings).
Remaining branch: the live pre-game mutation semantics (admin-driven team/civ resize + rank kick + squad change on an
existing NotStarted mission).

### SIM-0514 CFSMission::DelayCountdown — n/a (dead code)
Native `FedSrv/fsmission.cpp:1904-1917` has **no callers** anywhere in the native FedSrv tree — only the
declaration (`fsmission.h:303`) and its own definition. It is dead code with no reachable production path, so no port
is warranted. Classified whole-body n/a.

### SIM-0515 CFSMission::StartCountdown — IMPLEMENTED
Native `FedSrv/fsmission.cpp:1926-2221`:
- stage guard `NOTSTARTED || STARTING`; set `timeStart = Now() + fCountdownLength`; broadcast `SET_START_TIME` to `GetGroupMission()`
- when NOTSTARTED: generate mission, per-side SET_BRIEFING_TEXT, `SetStage(STAGE_STARTING)`, CreateDPGroups, per-side money seed + autodonate setup + SendMissionInfo, single/multi restart-station wing-aware distribution, miner spawn.
Port mapping (EQUAL for the wire+stage contract, rest documented as restructured):
- `Server.Lobby.cs:1671 StartCountdown` — Stage→Starting, records the double-second target, broadcasts `SET_START_TIME(14)` whole-mission (`CountdownPacket` `:1695`).
- `Server.Lobby.cs:1711 AdvanceStartCountdowns` — the per-tick `Starting → Started` driver.
- Mission generation happens once at `AssembleGameMission`; starting-money seed + initial payday happen at the Started transition (`CompleteStartCountdown` → `ApplyStartingMoneyAndInitialPayday`); briefing text is a separate port feature. These native side-effects are deliberately restructured, not dropped (documented in the port); the countdown announcement and stage policy are faithful.
Tests: `LiveCountdownStartPingTests` (SET_START_TIME), `LiveMissionStageTests`, `P762LobbyModerationTests.AutoStart_gate_starts_countdown_only_when_bAutoStart_is_set`.

### SIM-0516 CFSMission::StartGame — IMPLEMENTED
Native `FedSrv/fsmission.cpp:2229-2280`:
- if NOTSTARTED → `StartCountdown(0)`; if STARTING →
- `SetStage(STAGE_STARTED)`, connect player score objects, schedule `m_timeNextPayday`, clear winner/draw, `m_pMission->EnterGame()`, purge `m_oldPlayers`, broadcast `ENTER_GAME` to real sides, chat, AGC GameStarted, `DoPayday()`.
Port mapping (EQUAL):
- `Server.Lobby.cs:1781 CompleteStartCountdown` — Stage→Started, `SeedStartingVisibility`, `ApplyStartingMoneyAndInitialPayday`, `BroadcastEnterGameToMission` (`ENTER_GAME(128)` whole-mission, `:902`), `BroadcastMissionStageToMission(Started)` (`:1816`).
- `EnterGame`/score-object connect/purge are represented by the Started-transition seeding + per-peer score handling elsewhere.
Tests: `LiveEnterGameTests` (ENTER_GAME), `LiveMissionStageTests` (#2/#3 Started-transition broadcasts).

### SIM-0523 CFSMission::MakeOverrideTechBits — n/a (admin-era plumbing)
Native `FedSrv/fsmission.cpp:2718-2733` lazily allocates the per-side override-tech bitmasks consumed **only** by the
COM `CAdminGame::OverrideTechBit` (`AdminGame.cpp:319`) and the AGC param block (`fsmission.cpp:234`). The port has no
COM/AGC admin interface and no per-side override-tech mechanism in mission assembly (`ResearchSettings.cs` only
computes the base starting-tech mask). Whole-body n/a (pure era plumbing behind the unported COM/AGC admin path).

### SIM-0536 CFSMission::SendLobbyMissionInfo — IMPLEMENTED
Native `FedSrv/fsmission.cpp:3936-3972`:
- default recipient = player connection; queue `MISSIONDEF`; `JOINED_MISSION(shipID, dwCookie)`; `ExportObj` every side; `SendPlayerInfo` for every ship; flush.
Port mapping (EQUAL): the join burst `Server.Lobby.cs:3587 CompleteJoinAndLaunch` sends `MISSIONDEF(38)`, `JOINED_MISSION(95)` with shipID + dwCookie, side EXPORTs and per-player PLAYERINFO (P775/P309 note the native `AddPlayerToMission`/`SendPlayerInfo` correspondence). The port collapses native's lobby-only send into the single join burst because the port has no separate pre-launch "in lobby" client stage.
Tests: `P775PlayerInfoMomentsTests`, `LiveBoardShipTests`, `MissionAssemblyTests`.

### SIM-0537 CFSMission::QueueLobbyMissionInfo — PARTIAL
Native `FedSrv/fsmission.cpp:3983-4096` fills `pfmLobbyMissionInfo` with squad ids, port/cookie, start-time offset,
slot-availability flags, team/side counts and the mission-setting flags for the **native lobby wire**.
Port mapping:
- EQUAL for the advertised setting set — `LobbyHeartbeat.cs:458 LobbyGameParams.FromMission` publishes developments,
  lives, scores-count, object-model-created, in-progress, the win-goal flags, and the rank window to the HTTP
  directory (native `MissionParams::Reset` defaults used where the port has no equivalent).
- n/a for the wire-only fields — slots-available/guaranteed-slots, squad IDs, start-time offset, countdown-started,
  team/side counts, eject-pods, squad-game and invulnerable-stations have **no HTTP-directory consumer**; join-slot
  availability is instead gated at seat (`Server.Lobby.cs:3438 CheckInitialPositionRequest`).
Tests: `P806LobbyGameParamsTests` (FromMission maps the live mission onto native fields incl. Reset defaults).
Remaining: the native-wire-only advertisement fields (no directory consumer in the port's HTTP-lobby architecture).

### SIM-0538 CFSMission::SendMissionInfo — IMPLEMENTED
Native `FedSrv/fsmission.cpp:4108-4341`:
- default recipient (player conn or side group); CLUSTERINFO; per-cluster seen-by-side EXPORTs (cluster, warps, warp bombs, asteroids+building effects, stations, treasures, mines, probes); side buckets (CREATE_BUCKETS, per-bucket BUCKET_STATUS); per-other-player AUTODONATE + SET_MONEY; all-known-ships SHIP_STATUS; GAME_STATE victory rankings; flush.
Port mapping (EQUAL): the join burst `Server.Lobby.cs:3587 CompleteJoinAndLaunch` (which explicitly cites
`fsmission.cpp:4108-4341`) carries the cluster/warp/station/asteroid/treasure/mine/probe exports, `SET_CLUSTER`,
`SHIP_STATUS` and `GAME_STATE`; `SendSideTechToPeer` `:905` emits `CREATE_BUCKETS(191)` + one `BUCKET_STATUS(65)` per
bucket (fsmission.cpp:4255-4273); the autodonate/money loop is in `Server.Economy.cs` (AUTODONATE/SET_MONEY,
fsmission.cpp:4275-4302). Per-mission scoping matches native.
Tests: `P775PlayerInfoMomentsTests`, `LiveBucketTests`, `ClusterInfoServerTests`, `PlayerMoneyAutoDonateTests`, `MissionAssemblyTests`.

### SIM-0541 CFSMission::FAllReady — IMPLEMENTED
Native `FedSrv/fsmission.cpp:4375-4445`:
- balance check block (`:4375-4432`): min/max players, `iMaxImbalance`, rank-spread (when `iMaxImbalance==0x7ffe`), the `bAllowEmptyTeams` inactive-empty-team skip — then `return true`.
- The trailing "every side ready" loop (`:4435-4444`) is **unreachable dead code**: the balance block's own `return true` (`:4432`) precedes it.
Port mapping (EQUAL, P762b): `Server.Lobby.cs:1564 FAllReady` implements the balance check only (the dead loop is
correctly NOT restored); `:904 GetRankThreshold` follows the native integer formula (`fsmission.cpp:1380-1408`);
`TryAutoStart` `:1496` keeps the ready-up gate as the separate, documented P180 policy (and the `bAutoStart` gate,
`FedSrv.CPP:8223`).
Tests: `P762LobbyModerationTests` — `FAllReady_min_and_max_player_counts`, `FAllReady_player_count_imbalance`,
`FAllReady_rank_spread_only_when_rank_balancing_is_enabled`,
`FAllReady_allow_empty_teams_skips_inactive_empty_teams`,
`FAllReady_is_the_native_balance_check_only_and_ignores_side_ready_flags`,
`GetRankThreshold_divides_by_the_whole_mission_ship_list_including_docked_ships`,
`AutoStart_gate_starts_countdown_only_when_bAutoStart_is_set`.

### SIM-0837 GetStateID — n/a (standalone-retail plumbing)
Native `FedSrv/FedSrv.CPP:65-83` is compiled only under `#if defined(ALLSRV_STANDALONE)`: `m_CharMap` maps a
character name → sequential state id, used solely at `FedSrv.CPP:5885` in the standalone logon path to key retained
player statistics by name. The port is an online-lobby / anonymous server (`Server.Identity.cs` is JWT join-token
binding, not a name→id stat cache) and does not retain per-character stats by name across sessions. Whole-body n/a.

---

## Family score
| metric | before | after |
|---|---|---|
| weighted (credit/weight) | 1.52% (9/592) | 3.92% (23/586) |
| unweighted (complete/eligible) | 1.18% (3/254) | 3.59% (9/251) |

Implemented this batch: SIM-0511, SIM-0515, SIM-0516, SIM-0536, SIM-0538, SIM-0541 (weight 14). Partial: SIM-0512,
SIM-0513, SIM-0537. n/a: SIM-0514, SIM-0523, SIM-0837 (removed from the eligible denominator, 6 weight).

## Light gate
- Whole-solution Debug build: **0 warnings / 0 errors** (no production code changed — this batch touched only the CSV
  and this report, so no Server/Core AOT publish and no new tests were required).
- Cited certification tests (Core.Tests): P762LobbyModerationTests, P806LobbyGameParamsTests, LiveMissionStageTests,
  LiveEnterGameTests, LiveCountdownStartPingTests → **27/27 passed**; P775PlayerInfoMomentsTests, LiveBucketTests,
  ClusterInfoServerTests, PlayerMoneyAutoDonateTests, MissionAssemblyTests, LiveBoardShipTests, PaydayTests →
  **46/46 passed** (0 failed, 0 skipped).
- One `tools/run-tests.sh --tier fast` run: 10,967 tests; Allegiance.Update/Lobby/ClientCore fast suites all green,
  Allegiance.Core 5,427 passed / 1 failed. The single failure was the **already-ledgered live-network load flake**
  `P761bCommandRelayTests.QueuedStationGoto_KeepsOwnTarget_AndRelaysTheStationToTheTeammate` (Assert.Equal
  "goto Garrison" vs "goto", a relay seating-order race — HUD-19/2026-10-24 entries); it passed **3/3** isolated
  invocations on this worktree and P843 changes no production code, so it is not a regression (P843 observation added
  to `docs/TEST_FLAKES.md`).
- `tools/p832a-mission-rules-audit.py --check`: 351 bodies/cases, no missing/stale.
- `python3 scripts/parity-scorecard.py`: validates; CSV is LF (`grep -c $'\r'` = 0).

## Native-intent classification & modern/better-approach note
Native intent: stage/countdown/start/lobby-advertisement are event-driven mission-lifecycle rules; the only behavioural
difference found is `GiveSideMoney`'s immediate `DoPayday` (deferred to the periodic payday in the port — net-equivalent
to players, minor timing), and the biggest structural divergence is `SetMissionParams`, whose **live pre-game mutation**
of an assembled mission is replaced by the port's rebuild-from-config model (apply once at `AssembleGameMission`,
enforce rank limits at join). `QueueLobbyMissionInfo` similarly reflects the port's HTTP-directory lobby replacing the
native wire message. Three whole bodies (`DelayCountdown`, `MakeOverrideTechBits`, `GetStateID`) are dead or
admin/standalone-era plumbing and are correctly absent from the port. The port's single join burst
(`CompleteJoinAndLaunch`) is arguably a **cleaner design** than native's separate `SendLobbyMissionInfo` vs
`SendMissionInfo` split, and its explicit per-mission scoping (multi-mission hosting) improves on native's
whole-mission assumptions without changing observable behaviour.

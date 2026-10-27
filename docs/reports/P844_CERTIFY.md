# P844 — certify the event-driven mission rules: game over, results, victory checks

Batch: SIM-0522, SIM-0524, SIM-0525, SIM-0526, SIM-0527, SIM-0528, SIM-0529,
SIM-0530, SIM-0531, SIM-0532, SIM-0533, SIM-0534 — 12 rows, weight 36, all
`FedSrv mission rules (event-driven)`. Native bodies re-read at dispatch
(`~/Development/Allegiance/src/FedSrv/fsmission.cpp:2691-3899`; line numbers
re-grepped, they match the CSV cites). Built on the P832 chain (flags P832c,
artifacts P832d, conquest P832e + `Mission.StationVictory.cs`, kills P832f) —
not re-derived.

## Family score

| | before | after |
| --- | --- | --- |
| rows `implemented` | 0 / 12 | 10 / 12 |
| weighted credit | 0 / 36 (0%) | 26 / 36 (72.2%) |
| remaining `partial` | 12 | SIM-0528 (w5), SIM-0531 (w5) |

`python3 scripts/parity-scorecard.py` validates (headline 94.27%, no row errors).

## Per-row certification

Each row: native branches → port sites → verdict → fix/test.

### SIM-0522 — CFSMission::GameOver (fsmission.cpp:2691-2711) — **implemented** (w2)

| native branch | port site | verdict |
| --- | --- | --- |
| `m_psideWon/m_pszReason/m_bDraw = (psideWin==NULL)` (:2692-2694) | `Server.Lobby.cs:496 DeclareGameOver(mission, winnerId, reason)`; draw = `GameOver.NA`; `Stage→Over` is the port's no-winner-yet latch | equal |
| game ends at the next victory check, not here (:2698 comment) | every caller routes through the shared DeclareGameOver; stage gates arm immediately | equal |
| AGC `AllsrvEventID_GameEnded` context event with reason/winningTeam (:2699-2706) | — | n/a — era: AGC event bus (admin/monitoring COM era) has no port counterpart |

Evidence: `GameOverPlayersWireTests.Win_by_elimination_broadcasts_game_over_players_to_surviving_peer`
(Stage Over + GAME_OVER on the wire); `P844MissionRulesCertifyTests.Starting_mission_with_empty_teams_never_ends_by_inactive_sides`
control leg (draw → Over, `iSideWinner = NA`).

### SIM-0524 — RecordGameResults (fsmission.cpp:2757-2836) — **implemented** (w3)

| native branch | port site | verdict |
| --- | --- | --- |
| `CQGameResults` row: gameID, game name, winning team name/id (`NA` on draw), 6 goal switches + goal values, duration (:2768-2790) | **fixed (missing)**: new `LobbyGameResult` row in `ReportMissionResultsToLobby` (`Server.Lobby.cs:1294`), `LobbyMatchReport.Game` (`LobbyHeartbeat.cs`); duration = `LastUpdate − CountdownStart` (= `GetGameDuration`, :3175) | fixed + test |
| `#if !ALLSRV_STANDALONE` SQL post | lobby career report is the port's persistence (P808b) | design |
| JSON `gameInfo` blob (time/server/core/gameOverReason + teams + playersScore) posted to `szGameStatsUpdateUrl` when set (:2810-2822) | — | n/a — era: Steam-era stats-export URL; the lobby report carries the same facts |
| per-side `RecordTeamResults` walk (:2804-2821) | see SIM-0525 | equal |

Test: `P844MissionRulesCertifyTests.Results_report_carries_game_team_and_player_facts_over_the_wire`
(server AOT-context JSON → lobby `MatchReport` contract round-trip; fails before the fix — no `Game` field existed).

### SIM-0525 — RecordTeamResults (fsmission.cpp:2845-2915) — **implemented** (w3)

| native branch | port site | verdict |
| --- | --- | --- |
| per-side SQL row: name, techs, civID, kills, baseKills/captures, deaths, ejections, flags, artifacts, conquest %, prosperity bought/complete, timeEndured (:2862-2883) | **fixed (missing)**: new `LobbyTeamResult` per mission side in `ReportMissionResultsToLobby` (teamNumber, name, civ name, kills, baseKills/captures, deaths, ejections, flags, artifacts, conquest %, territory); client-visible per-side counters already equal (`Wire/GameOver.cs ForMission`, P752) | fixed + test |
| teams JSON array `{teamNumber, name, faction}` (:2886-2895) | same `LobbyTeamResult.Faction` | equal |
| prosperity %/techs/`TimeEndured` columns | — | design — the port side model tracks none of these; omitted (not behaviour-relevant: nothing reads them back into gameplay) |
| per-player `RecordPlayerResults` walk over `ISPLAYER` ships (:2900-2913) | see SIM-0526 | equal |

Test: same round-trip test (2 team rows asserted, incl. faction + conquest %).

### SIM-0526 — RecordPlayerResults (fsmission.cpp:2924-3047) — **implemented** (w3)

| native branch | port site | verdict |
| --- | --- | --- |
| per-player SQL row: 21 counters incl. kills breakdown, ejections, rescues, flags, artifacts, techsRecovered, alephs/asteroids spotted, combatRating, score, timePlayed (:2969-2986) | `ReportMissionResultsToLobby` `LobbyMatchResultEntry` — core subset existed (P808b); **fixed (missing)**: ejections, rescues, flags, artifacts, techsRecovered, warps/asteroids spotted, exact `TimePlayedSeconds` added as optional fields on both the server record and the lobby's `CareerContracts.MatchResultEntry` | fixed + test |
| verified-players-only gate (SetCharStats anti-abuse, FedSrv.CPP:1045-1140) | `AccountId(kv.Key) is null → skip` (P808b) | equal |
| zero-time-played skip (FedSrv.CPP:1051-1054) | `TimePlayed <= 0 → skip` (P855) | equal |
| Steam achievements/leaderboard update (`#else` branch, :3036-3042) | — | n/a — era |
| `debugf` result spew (:2996-3018) | — | n/a — diagnostics |

Test: same round-trip test (all added columns asserted end-to-end).

### SIM-0527 — QueueGameoverMessage (fsmission.cpp:3059-3142) — **implemented** (w2)

| native branch | port site | verdict |
| --- | --- | --- |
| GAME_OVER payload: reason (default `"Your side has been eliminated."` when NULL), per-side `rgSides` (name/civ/color/kill counters, `"<bug>"` absent-side fill), `nNumSides`, `iSideWinner`, `bEjectPods` (:3061-3087) | `Wire/GameOver.cs ForMission` (P752 fills the real per-side counters; `EjectPods=false` — no mission-param field, honest zeroing) | equal |
| connected-player roster with `GetTimePlayed() > 0 && side != TEAMLOBBY` gate (:3108-3121) | `BuildGameOverPlayerEntries` (`Server.Lobby.cs:1418`) — **fixed (different)**: gate now enforced, with the seat-time-delta fallback for the side-only path (whose TimePlayed the finalize has not stamped yet) | fixed + test |
| departed-player tail from `m_oldPlayers` with `TimePlayed != 0` and non-lobby side (:3123-3135) | **fixed (missing)**: appended from the P841 `_oldPlayerScores` cache + new `_oldPlayerSideIds` (`Server.cs`), saved in `ApplyDeparturePaydayAndOldPlayer` (`Server.Lifecycle.cs:2113`) which now also stamps the cached score's `TimePlayed` (native copies the live score object whose Connect/Disconnect pairs keep it current, igc.h:5602-5613); consumed on rejoin / cleared on rotation with the score cache | fixed + test |
| old-player save BEFORE the victory check (native: `SaveAsOldPlayer` in `RemovePlayerFromMission` fsmission.cpp:467-471, `GiveSideMoney` fsmission.cpp:1141, then `DeactivateSide` :1147-1149) | **fixed (different)**: `ApplyDeparturePaydayAndOldPlayer` moved before `DeactivateSideForPeerDeparture` in `RetireDepartedPeer` — previously the game-over roster was built before the cache was filled | fixed + test |
| 50-player GAME_OVER_PLAYERS chunks, queued from the roster end (:3131-3141) | `EncodeGameOverResult` (`Server.Lobby.cs:1522`) | equal |

Test: `GameOverPlayersWireTests.Win_by_elimination_broadcasts_game_over_players_to_surviving_peer`
now asserts 2 entries — survivor first, departed player second with its cached
side id and stamped time played (fails before the fix: 1 entry).

### SIM-0528 — ProcessGameOver (fsmission.cpp:3170-3549) — **partial** (w5)

| native branch | port site | verdict |
| --- | --- | --- |
| rank-ratio determination for exactly 2 sides (:3182-3197) | `FinalizeMissionScores` (`Server.Lobby.cs:1212`) | equal (P583) |
| per-player `Disconnect`/`EndGame` win/loss with time-played and half-duration gates (:3222-3245) | `FinalizeMissionScores` (SetWin + `CalculateScore` with core float constants) | equal (P583) |
| `GetExpMult` score damping over connected + old players and command scores (:3290-3360, helper :3144-3158) | helper ported pure and certified (P841/SIM-0761) but **inert at its call sites** — the port has no persisted career score to weight experience with | **different — REMAINING** (see below) |
| commander score = side score rate × time commanded (:3362-3400) | command-credit fields on the score object | different-minor (see below) |
| SetCharStats for every player + old players (:3403-3419) | `ReportMissionResultsToLobby` (P808b) | equal |
| squad-game record gate `bSquadGame && bScoresCount && winner && nTeams==2 && different squads` (:3421-3427) | `ReportMissionResultsToLobby` squad gate (P855) | equal |
| RecordGameResults + QueueGameoverMessage + guaranteed flush (:3429-3433) | `DeclareGameOver` order: finalize → report → encode → send | equal |
| lifepod clear, score reset, old-player purge, Vacate, ballot purge (:3437-3460) | `RotateMissionAfterGameOver` (+ `_ballots.Remove`, old-player cache clear) | equal |
| `SetStage(bRestartable ? NOTSTARTED : OVER)`, side ready/active/money resets, AET reset broadcast, `CheckForSideAllReady` (:3462-3500) | rotation assembles a fresh NotStarted mission; P660 intermission | equal (collapsed, documented) |
| sides `Reset()` + `ResetMission()` (:3502-3504) | fresh-mission rotation | equal |
| AGC `AllsrvEventID_GameOver` (:3507-3510) | — | n/a — era |
| auto-restart `timeStart` announce (:3539-3546) | `_autoRestartDeadline` + P660 countdown broadcast | equal |

**Remaining branch (exactly):** the exp-mult damping pass. Native scales every
score by `GetExpMult(Σ timePlayed·careerScore per side)` (floor 0.1). The pure
math is ported and branch-tested (`PlayerScore.GetExpMult`,
`P841MissionRulesCertifyTests.GetExpMult_matches_native_branches`), but the
port's game-over path has no persisted career score (`PlayerScore` is
per-session, no stats DB), so wiring it to session scores would diverge from
native's career-based balancing — a decision already taken and documented in
`docs/reports/P841_CERTIFY.md`. Not re-derived here. Commander score: the port
keeps the native derivation inputs but has no commander-ELO consumer (Steam era
n/a); command credit is carried on the score object.

### SIM-0529 — CheckForVictoryByStationBuild (fsmission.cpp:3558-3560) — **implemented** (w2)

| native branch | port site | verdict |
| --- | --- | --- |
| pure delegation to `CheckForVictoryByStationKill(NULL, NULL)` | build completion raises `NotifyStationVictory(station, builder, built: true)` (`Sector.cs:5578`) → `RecomputeConquestAndTerritory(null)` → `VictoryByStationsComputed` → `OnVictoryByStationsComputed` (`Server.Combat.cs:1503`) | equal |

Evidence: `P832eConquestRulesTests` (build raises the recompute; native
"won by out-building" reason text).

### SIM-0530 — CheckForVictoryByStationCapture (fsmission.cpp:3563-3566) — **implemented** (w2)

| native branch | port site | verdict |
| --- | --- | --- |
| delegation to `CheckForVictoryByStationKill(NULL, psideOld)`; capture re-sides the station BEFORE the check (fsship.cpp:631, 686), so the captured station counts for its new owner | capture paths raise `NotifyStationVictory(station, captor, captured: true, oldSide)` after the re-side (`Sector.cs:2497, 4659`); restart-station elimination + `VacateStation` handled in `OnVictoryByStationsComputed` | equal |

Evidence: `ConquestTerritoryLiveTests.Configured_territory_goal_wins_on_a_real_station_capture`;
`P832eConquestRulesTests`.

### SIM-0531 — CheckForVictoryByStationKill (fsmission.cpp:3568-3747) — **partial** (w5)

| native branch | port site | verdict |
| --- | --- | --- |
| stage gate `(STAGE_STARTED \|\| (STARTING && bAllowEmptyTeams)) && m_psideWon==NULL` (:3575-3578) | `RecomputeConquestAndTerritory` gate (`Mission.cs:1205`) | equal |
| conquest: flag-capable station tally per side, `bChange` gate, `conquest >= iGoalConquestPercentage` (:3581-3625) | `RecomputeConquestAndTerritory` conquest branch | equal (P191/P832e) |
| KGJV #62: AET + exactly one active team → no winner (:3628-3630) | same method | equal |
| territory: strict unanimity per cluster, threshold `(50 + clusters·iGoalTerritoryPercentage)/100` (:3634-3673) | same method | equal (P191) |
| GAME_STATE broadcast on `bChange` (:3685-3693) | `OnVictoryByStationsComputed` (`Server.Combat.cs:1503`, P201 real flags/artifacts) | equal |
| restart-station elimination → `DeactivateSide(psideOld)` (:3695-3710) | `OnSideLostRestartStation` (`Server.Lobby.cs:260`) → `DeactivateSide` | equal (P455) |
| **allies override tail (:3712-3745)**: if any side is allied and every ACTIVE side is one alliance group → `psideWon` = the alliance | — | **missing — REMAINING** |

**Remaining branch (exactly):** the allies override. `RecomputeConquestAndTerritory`
and `OnVictoryByStationsComputed` are frozen for Codex's parallel P861
GAME_STATE ordering fix, so per the brief the change is specified, not made:
in `RecomputeConquestAndTerritory`, immediately after the
`AllowEmptyTeams && one-active-team` suppression, add the same override P844
implemented for the win-by-inactive-sides family (`Server.Lobby.cs:161
AllAlliedWinner` / `:176 AllActiveSidesAllied` over `mission.Sides where
Active`): when every active side shares one `AllyGroup`, set `psideWon` to a
member of that group. Everything else in the body is certified equal.

### SIM-0532 — CheckForVictoryByKills (fsmission.cpp:3749-3764) — **implemented** (w3)

| native branch | port site | verdict |
| --- | --- | --- |
| `STAGE_STARTED && m_psideWon==NULL` gate (:3751) | `RecomputeFlagsAndKills` (`Mission.cs:1412`) — **fixed (different)**: was Started-or-Starting; tightened to Started-only | fixed + test |
| `IsDeathMatchGame()` gate (:3754) | `KillsEnabled` | equal |
| `psideTest->GetKills() >= nGoalTeamKills` (:3756-3758) | kills leg threshold scan | equal (scan vs single-side test: same winner set — only a credited kill can cross the threshold, one kill at a time) |

Test: `P844MissionRulesCertifyTests.Kills_win_never_fires_in_starting_stage`
(proven to fail against the pre-fix gate) + `Started_mission_still_wins_by_kills_flags_artifacts`.

### SIM-0533 — CheckForVictoryByInactiveSides (fsmission.cpp:3767-3858) — **implemented** (w3)

| native branch | port site | verdict |
| --- | --- | --- |
| `HasPlayers(pside,false) && GetActiveF()` tally; first/second populated side → winner/continue (:3776-3786) | `CheckForVictoryByInactiveSides` (`Server.Lobby.cs:55`) + `...AfterDeactivation` (:410) | equal (P178/P548/P455) |
| STANDALONE training-mission hack (`nTotalMaxPlayersPerGame == 1`, :3794-3800) | — | n/a — era: standalone-only |
| CanCheat STAGE_STARTING exemption (:3802-3806) | — | n/a — cheat-only test hack |
| KGJV #62: STAGE_STARTING + bAllowEmptyTeams → NULL (:3808-3811) | **fixed (missing)**: gate added to both variants — a countdown-phase drop could end the mission | fixed + test |
| allies override (:3820-3856): every ACTIVE side one alliance group → the alliance wins, overriding the tally | **fixed (missing)**: `AllAlliedWinner`/`AllActiveSidesAllied` (`Server.Lobby.cs:161/176`) applied in both variants when >1 side survives; reason = allied name list (:5396-5412) | fixed + test |
| caller reason strings incl. allied name list (fsmission.cpp:5396-5418) | `AlliedWinnerReason` (P852) — now only reachable where native reaches it | equal |

Tests: `Allied_survivors_win_when_third_side_is_eliminated` (proven to fail
pre-fix: game continued forever), `Unallied_survivors_keep_playing_when_third_side_is_eliminated`
(negative control), `Starting_mission_with_empty_teams_never_ends_by_inactive_sides`
(AET gate), `AllActiveSidesAllied_matches_native_override` (pairwise truth table).

### SIM-0534 — CheckForVictoryByFlags (fsmission.cpp:3861-3899) — **implemented** (w3)

| native branch | port site | verdict |
| --- | --- | --- |
| `STAGE_STARTED && m_psideWon==NULL` gate (:3864) | flags leg (`Mission.cs:1412`) and artifact leg (`Mission.cs:1333`) — **fixed (different)**: was Started-or-Starting on both; artifact leg now Started-only, prosperity leg keeps the P302 gate (native's DevelopmentCompleted win has no stage gate, FedSrv.CPP:11274-11280) | fixed + test |
| unconditional GAME_STATE broadcast with real counters (:3866-3880) | `OnFlagsKillsComputed`/`OnProsperityArtifactComputed` broadcast on `Changed` with real nFlags/nArtifacts (P201) | equal |
| `sidFlag != SIDE_TEAMLOBBY` → flags leg `GetFlags() >= nGoalFlagsCount` (:3883-3887) | flags leg threshold | equal |
| else artifact leg `GetArtifacts() >= nGoalArtifactsCount` (:3890-3894) | artifact leg threshold | equal |

Test: `P844MissionRulesCertifyTests.Flags_and_artifact_wins_never_fire_in_starting_stage`
(proven to fail pre-fix) + `Started_mission_still_wins_by_kills_flags_artifacts`.

## Verification

- `~/.dotnet/dotnet build AllegianceCore.slnx` — **0 warnings / 0 errors**.
- `tools/build-tools.sh` — all 90 tool projects, 0/0.
- Rig AOT publish (`dotnet publish src/Allegiance.Server -c Release -r linux-x64 --self-contained -p:PublishAot=true`) — clean, no IL2026/IL3050.
- New tests 3× alone: `P844MissionRulesCertifyTests` 8/8 ×3; updated
  `GameOverPlayersWireTests` 5/5 ×3; affected `P841MissionRulesCertifyTests` +
  `P808CareerLoopbackTests` + `P855SquadGameLoopbackTests` +
  `P660CountdownTests` 8/8 ×3; `BallotVotingLiveTests` + `FlagsKillsTests` +
  `ConquestTerritoryTests` + `P832eConquestRulesTests` +
  `ProsperityArtifactTests` 38/38 ×3.
- Must-fail proofs: the two stage-gate tests fail against the pre-fix
  `RecomputeFlagsAndKills` gate (temporarily reverted, 2/2 fail, re-applied);
  the allies-override, AET-gate and report-contract tests assert behaviour that
  did not exist before the fix (override returned early / gate absent / fields
  absent), so they fail on master by construction.
- ONE `tools/run-tests.sh --tier fast` — 11,207 tests; one failure:
  `ConquestTerritoryLiveTests.Flag_capture_tips_conquest_and_broadcasts_game_state_then_game_over`
  ("Expected 80, Actual 50") — **3/3 alone**, and the same symptom is already
  ledgered in `docs/TEST_FLAKES.md` (P852 gate, 2026-10-27) as the suspected
  conquest GAME_STATE recompute race that P861 is fixing; recurrence appended
  to the ledger. P844 touches no conquest recompute line.
- Showcase never-parks (`P730ShowcaseLiveTests.Two_local_showcase_matches...`)
  **5/5** (3m20s–4m30s each) vs base behaviour — no parking, mining/research/
  kills/relaunch all observed.
- `python3 scripts/parity-scorecard.py` — validates; CSV kept LF (0 CR).

## Native-intent classification

- **design** (kept): SQL game/team/player result rows → lobby career report
  (different persistence design, same facts); GAME_STATE on `Changed` instead
  of every invocation; rotation collapsing `ResetMission`+regenerate (P439);
  exp-mult left inert pending a persisted career score (P841 decision).
- **era constraint** (n/a): AGC `GameEnded`/`GameOver` context events;
  ALLSRV_STANDALONE training hack; CanCheat Starting exemption; Steam
  achievements/leaderboard/comm-ELO; `szGameStatsUpdateUrl` JSON export;
  COM/AGC admin plumbing.
- **native bugs**: none found in this batch's bodies (the fsmission.cpp:5397
  allied-reason loop uses the eliminated side's `pside` instead of
  `psideWin` — pre-existing P852 port note, kept as documented).

## Modern/better-approach note

The port collapses native's five scattered game-over writers (GameOver flag,
Stage flip, score finalize, message queue, reset) into one idempotent
`DeclareGameOver` with an explicit phase order and a rotation step — easier to
reason about than native's `m_psideWon`/`m_stage`/`m_ballots` scatter, and the
alliance logic is now one shared pair of helpers (`AllAlliedWinner`/
`AllActiveSidesAllied`) instead of three copy-pasted double loops. If native
upstream ever fixes the fsmission.cpp:5397 quirk, the port fixes it in one
place. The results-report contract is versioned by optional fields, so old
lobbies and old servers stay wire-compatible.

## Unfinished / handoffs

- SIM-0531 allies override: one-block change specified above, blocked by the
  P861 freeze of `RecomputeConquestAndTerritory`/`OnVictoryByStationsComputed`.
- SIM-0528 exp-mult wiring: needs a persisted career score on the server side
  (or an explicit design decision to weight by session score) — P841's call.
- Lobby career store does not yet persist `Game`/`Teams`/extended player
  columns (contract carries them; ingest ignores extras until its schema grows).

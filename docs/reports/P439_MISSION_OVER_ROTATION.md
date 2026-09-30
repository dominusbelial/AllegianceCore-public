# P439 — a finished mission never rotates back to a joinable pre-game

## Summary

A finished mission now rotates to a freshly-assembled `NotStarted` mission at
the same `_missions` index on the single shared game-over path, so the server
is joinable again after every end state (draw, win-by-elimination, conquest /
prosperity / artifact). Previously the only recovery was a full server
recreation (`docker compose up -d --force-recreate server`): `DeclareGameOver`
flipped `mission.Stage = Over` and never broadcast `MISSION_STAGE(37)`, and
every later joiner (routed to `_missions[0]` and told that mission's real
stage, P309) was stuck on a ready-check screen with nowhere to go.

## Native's real post-game-over flow (verified from source)

Read from `FedSrv/fsmission.cpp` at `~/Development/Allegiance/src/`:

- `CFSMission::GameOver` (`fsmission.cpp:2691`) runs the post-game sequence at
  `fsmission.cpp:~3420-3551`: queue/send GAME_OVER (`:3427-3429`), reset each
  player's lifepod/score (`:3430-3442`), `Vacate()` (`:3448`, body `:2604-2622`),
  kill pending ballots (`:3451-3452`).
- It computes `bRestartable = !g.fPaused && misparms.bAllowRestart`
  (`:3483`; forced false only for 1-player training missions, `:3484-3488`) and
  calls **`SetStage(bRestartable ? STAGE_NOTSTARTED : STAGE_OVER)`** (`:3490`).
  So a normal (restartable) game goes straight back to `STAGE_NOTSTARTED`;
  `STAGE_OVER` is only the no-restart fallback.
- `pside->Reset()` + `m_pMission->ResetMission()` tear down the world
  (`sideigc.h:573-583`, `missionigc.cpp:3804-3828`: terminate clusters, destroy
  side buckets), and the world is **regenerated on the next start**, not at
  game-over: `StartCountdown` (`fsmission.cpp:1926`) calls `GenerateMission`
  (`missionigc.cpp:3830+`) before `SetStage(STAGE_STARTING)` (`:~1976`).
- `SetStage` always broadcasts `MISSION_STAGE` to the whole mission
  (`fsmission.cpp:1750-1768`).
- `STAGE_OVER` blocks side joins (`DPR_ServerPaused`, `fsmission.cpp:4599-4600`)
  and excludes Over missions from the lobby (`:4045,4056`).

Conclusion the port reproduces: **`STAGE_OVER` is not the normal end state** —
a restartable game flips straight back to `STAGE_NOTSTARTED` and regenerates a
fresh world. The port had only the terminal branch (permanent `Over`, no stage
broadcast, world built once at boot), which is why it wedged.

## The fix

### 1. Extracted assembly helper (`Server.Lifecycle.cs`)

The per-game body of `InstantiateGames`' loop was factored verbatim into

```
private Mission AssembleGameMission(GameConfig g, StaticCore core, HullLandingBayCache landingBays)
```

covering: map load (file) or procedural MapGen (P397), config validation,
`Mission.Assemble`, the `_mapGenMissions` tag (P402), the P379 debug test
topology, the P116 landing-bay attach and `Stage = NotStarted` (P180). Pure
refactor, byte-identical boot behaviour — the boot tests stayed green.

To let the rebuild reach the same inputs after boot, two retained fields were
added to the server:
- `_core` (the loaded `StaticCore`, assigned in `Boot`);
- `_landingBays` (the shared `HullLandingBayCache`, assigned in
  `InstantiateGames`).

The `GameConfig` for the rebuild is recovered from `_config.Games` at the
mission's `_missions` index (`_missions` and `_config.Games` are index-aligned,
one mission per game).

### 2. One shared game-over path (`Server.Lobby.cs`)

`DeclareDraw` now routes through `DeclareGameOver`, keeping its reason string
(`"The game was declared a draw"`) and its distinguishing log text
(`[gameover] … (draw via AcceptDraw ballot)` — logged in `DeclareDraw` before
delegating), so the rotation below lives in a single place.

### 3. Rotation at the end of `DeclareGameOver`

`DeclareGameOver` already flipped `Over`, sent GAME_OVER(62) and
GAME_OVER_PLAYERS(63). P439 adds `RotateMissionAfterGameOver(mission)`, in
native's order (send game-over first, `fsmission.cpp:3427-3429`, then reset,
then `SetStage`, `:3490`):

1. `BroadcastMissionStageToMission(mission, Over)` — tell the old mission's
   still-connected peers the stage native would give them (already-connected
   clients get a real `MISSION_STAGE Over`).
2. Clear old-mission per-mission state keyed by the discarded `Mission`:
   `_startCountdownDeadline`, `_ballots`, `_mapGenMissions`, `_lastScoreRosterTick`.
3. `AssembleGameMission(_config.Games[idx], _core, _landingBays)` → a pristine
   `NotStarted` mission; swap it into `_missions` at the **same index**.
4. `SpawnInitialMiners(fresh)` (as boot does, once `_missions` is assigned).
5. In debug mode, `TryAutoStart(fresh)` — the P387 auto-start parity (below).

Swapping by index (not `Add`/`Remove`) keeps the `_missions` list length
constant, so a game-over fired from inside a `foreach (var m in _missions)`
sim step (e.g. conquest during `m.Update`) cannot invalidate the enumerator.

New joiners and reconnects now land on the pristine mission via the existing
join path (they are told its real stage — `NotStarted` — through the P309
`live.Ship.Mission.Stage` send), with **no join-path changes**.

### Pre-fix failing assertion (recorded as required)

Running `MissionRotationLiveTests.Finished_mission_rotates_to_fresh_notstarted_and_new_joiner_starts`
against the unfixed code (rotation call commented out) fails:
- **Assertion 1**: the old peer never receives `MISSION_STAGE(37) Over` —
  `DeclareGameOver` never broadcast the stage. The test surfaces this first.
- **Assertion 2**: a brand-new Bot joining afterwards would receive
  `MISSION_STAGE Over`, not `NotStarted` — the join path sends the mission's
  real stage, which remained `Over` forever. Against the fixed code the new
  joiner correctly receives `NotStarted`.
- Assertion 4 also fails pre-fix: `_missions[0]` stays the same (now-`Over`)
  object rather than being replaced by a different, pristine one.

### Existing game-over tests that asserted `Missions[0].Stage == Over`

Because `_missions[0]` is now a *fresh* object after game-over, any test that
checked `Missions[0].Stage == Over` (rather than a captured mission reference)
had to be pointed at the ORIGINAL mission object, which still retains
`Stage=Over` — exactly native's restartable-game-over semantics
(`ResetMission` + `SetStage(STAGE_NOTSTARTED)` keeps the old `CFSMission` in
`Over`). Updated to capture the pre-game-over mission reference:
`BallotVotingLiveTests.Draw_end_to_end_offer_then_accept_then_game_over`,
`ConquestTerritoryLiveTests.Conquest_win_in_one_mission_does_not_leak_to_another`,
`LiveWinConditionTests.{Second_player_disconnect_declares_first_side_winner,
Last_player_disconnect_is_a_draw, No_second_game_over_after_already_over,
Multi_mission_disconnect_scopes_per_mission}`,
`GameOverPlayersWireTests.{elimination, multi-mission-conquest}` and
`LiveP388DebugNoGameOverTests.Production_last_player_disconnect_still_ends_mission`.
No test logic/intent changed — only the target object of the Over-stage
assertions. (These are documented changes, discussed in the test comments.)

## Orphaned-peer audit (scope point 3)

Already-connected peers of the old mission are **not** re-seated (that is P440).
Their ships remain in `_peerShips` but their `Ship.Mission` points at the
discarded mission. Audited the code paths that could let these orphans corrupt
the new mission:

- **Ship-id allocator** — `AllocateShipId` → `IsShipIdLive`
  (`Server.Docking.cs:1463-1495`) checks `_peerShips` (and `_pendingSideSelect`)
  BEFORE walking `_missions`, so an orphaned peer's id stays live and a new
  ship/new miner can never be allocated it. No collision.
- **`_peerShips` loops** — every whole-mission broadcast/sim sweep filters by
  `ReferenceEquals(Ship.Mission, mission)` for a mission that is (or was) in
  `_missions`. An orphan's stale `Ship.Mission` (the discarded object) never
  matches the new mission object, so it is excluded from every new-mission
  broadcast (ENTER_GAME, MISSION_STAGE, TEAM_READY, GAME_OVER, etc.). The
  sim tick only iterates `_missions`, so orphan ships freeze (they are no
  longer updated) — the intended "kept GAME_OVER screen, must reconnect"
  behaviour.
- **Disconnect of the last orphan** — `ReleasePeer` → `CheckForVictoryByInactiveSides`
  returns immediately because the orphan's mission is already `Stage == Over`
  (idempotency preserved); it does not re-fire or touch the new mission.
- **Structural safety** — index-swap keeps `_missions` length constant.

Result: **no real collision found**, no orphan-seating change needed for P439.
Documented here and in `RotateMissionAfterGameOver`'s doc comment.

## Debug-mode handling (scope point 4)

P388 (`Server.Lobby.cs:59-79`) already skips win-by-elimination in debug mode,
so only a genuine combat/conquest outcome (through `DeclareGameOver` via
`OnVictoryByStationsComputed`, untouched) may end a debug mission. P387
(`Server.Lifecycle.cs:119-124`) auto-starts every debug mission at boot so the
unattended arena runs with no player.

The rotation gives a rebuilt debug mission the same auto-start treatment:
`RotateMissionAfterGameOver` runs `if (_config.LiveSettings?.DebugMode == true)
TryAutoStart(fresh);` after `SpawnInitialMiners(fresh)` — the same ordering and
condition as the P387 boot block. A debug mission that reaches game-over (e.g.
by conquest) therefore rebuilds AND auto-starts, exactly as at boot.
Verified by
`MissionRotationLiveTests.Debug_mode_rebuilt_mission_auto_starts_after_game_over`,
which boots debug mode (auto-start at boot → `Started`), drives a real
`#draw` → AcceptDraw → game-over (draws are not gated by the P388 skip, so they
remain a legal debug-mode end), and asserts the rebuilt mission climbs
`NotStarted → Starting → Started` on its own with no new player. The debug
auto-start is load-bearing: disabling the `TryAutoStart(fresh)` line makes that
test fail ("rebuilt mission did not auto-start").

## Out-of-scope decisions (stated plainly)

- **No re-seating of connected peers** (P440 — needs client work too).
- **Always restart; no `bAllowRestart` / `bAutoRestart` / `fRestartCountdown`
  config and no auto-restart `SET_START_TIME` timer** (`fsmission.cpp:3539-3549`)
  — added no config per the brief.
- **No `STAGE_TERMINATE` / server pause / `RestartPausedMissions`**.
- **No stats persistence** (`SetCharStats`, `RecordGameResults`, squad records).

## P440 scoping note (concrete)

Re-seating in place is native's full lobby-return flow:
`STAGE_NOTSTARTED` after game-over, players stay on their sides, get unready,
and return to the team screen (`gameoverscreen.cpp:609-625` Back button →
team lobby unless `STAGE_OVER`). The port already gives connected peers the
`MISSION_STAGE(37) Over` signal (this task) — P440's work is: (a) after
game-over + rotation, push the rebuilt mission's `NotStarted` stage to the old
peers too and re-anchor each orphan's `Ship.Mission` onto the fresh mission
(object identity, not index), preserving their `SideId`/readiness cleared back
to unready; (b) client (MonoGame) reaction: on `MISSION_STAGE NotStarted`
following `GAME_OVER`, drop the game-over screen and return to the ready-check,
re-subscribing to the new mission's sector/station exports (the world is a new
object, so the client must re-receive EXPORTs for the pristine world before it
can render it). Server-side, the main care is that the fresh mission's
sector/station objects differ from the old ones, so the in-place reseat needs a
join-sync-style EXPORT re-burst to the reseated peers rather than reusing their
stale world references.

## Verification

- New live tests (loopback real server + real bots over TCP):
  `tests/Allegiance.Core.Tests/MissionRotationLiveTests.cs` — asserts all four
  P439 verification points (old peer gets GAME_OVER then MISSION_STAGE Over;
  new joiner gets NotStarted not Over; the new joiner readies and the mission
  reaches Starting then Started via the real `StartCountdown`/
  `CompleteStartCountdown` path; `Missions[0]` is a different object and a
  station destroyed pre-game-over exists again in the pristine rebuild), plus
  the debug-mode auto-start parity test.
- Whole-solution build via `/home/domingo/.dotnet/dotnet build`: **0 warnings,
  0 errors**.
- Existing boot, game-over, ballot, conquest, GAME_OVER_PLAYERS and P388 debug
  tests stay green (with the documented Over-assertion retargeting above). A
  consolidated run of every affected class (MissionRotation, BallotVoting,
  ConquestTerritory, GameOverPlayers, LiveP388, FlagsKills, ProsperityArtifact,
  LiveTeamLeadership, LiveLogoff, LiveWinCondition) passes **62/62**.
- Full `Allegiance.Core.Tests` suite (~3524 tests): multiple consecutive runs
  taken. **No P439 regression was observed** — every failure was a live-wire
  timing / environmental flake, and the two rotation-caused classes (leadership
  succession + carrier logoff asserting on `Missions[0]`) were fixed and are
  green. Named flakes seen across runs (all environmental; none touch the
  game-over/rotation path): `LiveTurretGunnerBindingTests.Board_binds_gunner_...`
  (documented persistent), `LiveChatSectorWireTests.Individual_AllSector_...`
  (documented), `LiveWinConditionTests.Board_refused_once_mission_over` and
  `Multi_mission_disconnect_scopes_per_mission` (documented; both fail on the
  PRISTINE tree too), `ConquestTerritoryLiveTests.Conquest_win_in_one_mission_...`
  (fails under full-suite socket load, green in isolation),
  `LiveRipcordTests.Ship_anchor_insufficient_energy_...`,
  `LiveCountdownStartPingTests.{Multi_mission_countdown_...,StartGame_...}`,
  `LiveTeamLobbyTests.Unready_broadcasts_team_ready_false`,
  `AutoReloadOnEmptyTests.Auto_reload_relays_...`,
  `LoadoutChangeLiveTests.Boarding_and_leaving_...`,
  `LiveMoneyTests.Board_refund_...`, `ShipsUpdateLiveTests.Locked_target_fills_...`,
  `WarpBombTests.Live_whole_mission_warp_bomb_...`,
  `LiveStationRescueTests.Lifepod_reaching_rescue_station_...`,
  `LiveLifepodEnduranceDisplayTests.Driver_sees_countdown_...`,
  `LiveTeamLeadershipTests.*`/`LiveLogoffTests.Carrier_logoff_...` (were
  rotation regressions, since fixed — see below).
- **Rotation consequence fixed:** the shared game-over path means any
  disconnect that empties all-but-one side now also *rotates* `_missions[0]`.
  Four live tests asserted on `Missions[0]` after such a disconnect
  (`LiveTeamLeadershipTests.{Non_leader_departure_...,Leader_disconnect_promotes_...,
  Leader_last_on_side_...}` and `LiveLogoffTests.Carrier_logoff_detaches_...`);
  they were retargeted to the ORIGINAL (captured) mission object, which the
  rotation leaves intact (leadership/ownership/placement preserved). Confirmed
  this was a P439 effect (not environmental) by A/B: those tests fail on the
  P439 tree and pass on the pristine tree.

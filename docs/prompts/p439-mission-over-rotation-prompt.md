# P439 — a finished mission never rotates back to a joinable pre-game

## Why this task exists

This is a carried open item from the P426–P437 live-testing arc, hit
repeatedly. After a real game-over, every later joiner is told
`MISSION_STAGE(37) Over` and is stuck: the ready-check screen has nowhere
to go. The only recovery today is `docker compose up -d --force-recreate
server`. Traced by direct source read (native at
`~/Development/Allegiance/src/`):

**Native rotates. `STAGE_OVER` is not the normal end state.**

- `CFSMission::GameOver` (`FedSrv/fsmission.cpp:2691`) runs the post-game
  sequence in `fsmission.cpp:~3420-3551`:
  1. Queue and send GAME_OVER (`:3427-3429`).
  2. Reset each player's lifepod and score (`:3430-3442`).
  3. `Vacate()` resets player ships and deletes drones (`:3448`, body at
     `:2604-2622`).
  4. Kill pending ballots (`:3451-3452`).
  5. Compute `bRestartable = !g.fPaused && misparms.bAllowRestart`
     (`:3483`). It is forced false only for 1-player training missions
     (`:3484-3488`).
  6. **`SetStage(bRestartable ? STAGE_NOTSTARTED : STAGE_OVER)`**
     (`:3490`). So a restartable game goes straight from Started back to
     NotStarted. `STAGE_OVER` is only the fallback when the game is not
     allowed to restart. The one other thing that leaves `STAGE_OVER` is
     `RestartPausedMissions` on server un-pause (`FedSrv/FedSrv.CPP:8762-8771`),
     which again sets `STAGE_NOTSTARTED`.
  7. For every side: clear `rgfReady`, `rgfForceReady` and money, set
     `rgfActive=true` and `SetActiveF(true)`, then `CheckForSideAllReady`
     (`:3493-3521`).
  8. `pside->Reset()` clears side score counters (`Igc/sideigc.h:573-583`).
     `m_pMission->ResetMission()` moves every ship to the null cluster,
     **terminates every cluster**, and destroys side buckets
     (`Igc/missionigc.cpp:3804-3828`).
  9. If `bAutoRestart`, it sets a new `timeStart` and broadcasts
     `SET_START_TIME` (`:3539-3549`).
- **The world is regenerated at the next start, not at game-over.**
  `CFSMission::StartCountdown` (`fsmission.cpp:1926`) calls
  `m_pMission->GenerateMission(...)` (`:~1954`) before
  `SetStage(STAGE_STARTING)` (`:~1976`). `GenerateMission`
  (`Igc/missionigc.cpp:3830+`) resets every object-id counter and bumps
  `m_nReplayCount`.
- `SetStage` always broadcasts `MISSION_STAGE` to the whole mission
  (`fsmission.cpp:1750-1768`).
- `STAGE_OVER` also blocks side joins with `DPR_ServerPaused`
  (`fsmission.cpp:4599-4600`), and `:4045,4056` exclude Over missions from
  the lobby.
- Native client, on `MISSION_STAGE` → `STAGE_NOTSTARTED`
  (`clintlib/appmsg.cpp:2322-2329`): clears in-progress and countdown and
  `m_bWaitingForGameRestart`, then `OnMissionEnded`. On `FM_S_GAME_OVER`
  (`appmsg.cpp:3048-3073`): leaves the game, sets
  `m_bWaitingForGameRestart = true`, clears its cluster, resets players.
  The game-over screen's Back button returns to the team lobby unless the
  stage is `STAGE_OVER`, in which case it quits the mission
  (`WinTrek/gameoverscreen.cpp:609-625`).

**The port only has the terminal branch.**

- `DeclareGameOver` (`src/Allegiance.Server/Server.Lobby.cs:~115-145`)
  sets `mission.Stage = MissionStage.Over` (`:122`), then sends GAME_OVER(62)
  and GAME_OVER_PLAYERS(63). It **never** broadcasts `MISSION_STAGE(37)`,
  unlike native `SetStage`. `DeclareDraw` (`Server.Lobby.cs:1075-1088`) is a
  second copy of the same flip-and-broadcast.
- Nothing ever leaves `Over`. The only stage assignments in the server are
  boot `NotStarted` (`Server.Lifecycle.cs:857`), `Starting`
  (`Server.Lobby.cs:454`), `Started` (`:491`) and `Over` (`:122`, `:1078`).
  `StartCountdown` refuses anything other than NotStarted
  (`Server.Lobby.cs:450-452`).
- Every joiner lands in `_missions[0]` (`Server.Lobby.cs:1880,2001`) and is
  sent that mission's real stage (`Server.Lobby.cs:~2663-2671`, P309), so
  after game-over it gets `Over` forever.
- The port has no `GenerateMission`-at-start step. The world (sectors,
  stations, asteroids, buckets) is built once by `Mission.Assemble` in
  `InstantiateGames`' per-game loop body (`Server.Lifecycle.cs:~779-866`,
  including P379 `ApplyDebugTestTopology`, P402 `_mapGenMissions`, landing
  bays and `Stage = NotStarted`). Initial miners are spawned separately
  after `_missions` is assigned (`Server.Lifecycle.cs:~83-100`,
  `SpawnInitialMiners`). So an in-place Over→NotStarted flip alone would
  restart onto a destroyed world, and conquest would re-fire immediately.

## Scope

The smallest slice that makes a finished mission joinable again: at
game-over, **replace it with a freshly assembled NotStarted mission**.
That is the port's equivalent of native's `ResetMission` +
`GenerateMission` pair, collapsed into one rebuild because the port
generates at assembly time.

1. **Extract the per-game assembly** from `InstantiateGames`' loop body into
   a reusable method, for example `AssembleGameMission(GameConfig g, core,
   landingBays)`. It must cover map load or MapGen, `Mission.Assemble`, the
   `_mapGenMissions` tag, debug topology, landing bays and
   `Stage = NotStarted`. This is a pure refactor with byte-identical boot
   behaviour, and the existing boot tests must stay green. Keep whatever
   rebuild needs (the `GameConfig`, `core`, `landingBays`) reachable after
   boot.
2. **One shared game-over path.** Make `DeclareDraw` route through
   `DeclareGameOver`, keeping its reason string and log text, so the
   rotation below lives in one place.
3. **Rotate at the end of `DeclareGameOver`**, after GAME_OVER(62) and
   GAME_OVER_PLAYERS(63) go out (native order: send game-over first,
   `:3427-3429`, then reset, then `SetStage`, `:3490`):
   - Broadcast `MISSION_STAGE(37) Over` to the old mission's peers with
     `BroadcastMissionStageToMission`, so already-connected clients get the
     stage native would give them.
   - Build a fresh mission with step 1's helper. Swap it into `_missions`
     at the **same index**. Clear old-mission per-mission state, including
     `_startCountdownDeadline`, pending ballots and anything else keyed by
     the old `Mission`. Then run `SpawnInitialMiners` on the new mission,
     as boot does.
   - New joiners and reconnects now get `MISSION_STAGE NotStarted` and a
     pristine world through the existing join path, with no join-path
     changes.
   - **Already-connected peers of the old mission are NOT re-seated in
     this task.** They keep the GAME_OVER screen they already get and must
     reconnect to play again. That matches native's non-lobby behaviour
     when the Back button quits the mission (`gameoverscreen.cpp:611-613`).
     Make sure these orphaned peers can't corrupt the new mission: check
     the ship-id allocator (`AllocateShipId` → `IsShipIdLive` walks
     `_missions`), `_peerShips` loops and anything else that assumes every
     peer's `Ship.Mission` is in `_missions`. Document what you find, and
     fix any real collision you hit.
4. **Debug mode stays as it is.** P388 makes debug mode skip win-by-
   elimination (`Server.Lobby.cs:~64-80`), and P387 auto-starts debug
   missions at boot. If a debug-mode mission does reach game-over through
   conquest, the rebuilt mission must get the same auto-start treatment as
   at boot. Check `Server.Lifecycle.cs` for the P387 block and document
   what you did.

## Explicitly out of scope

- **Re-seating connected peers in place** without a reconnect. That is
  native's full lobby-return flow: players stay on their sides, get
  unready, and return to the team screen on `STAGE_NOTSTARTED`. It is the
  named follow-up, **a named follow-up task (number assigned later)**, which needs client work too: MonoGame reacting
  to `MISSION_STAGE NotStarted` after GAME_OVER by going back to the
  ready screen.
- `bAllowRestart`/`bAutoRestart`/`fRestartCountdown` config parameters and
  the auto-restart `SET_START_TIME` timer (`fsmission.cpp:3539-3549`).
  Always restart and don't add config. Say so in the report.
- Server pause / `RestartPausedMissions`, and `STAGE_TERMINATE`.
- Stats persistence (`SetCharStats`, `RecordGameResults`, squad records).
- The P438 JOIN_SIDE client fix, and vizfight's `[0,0,0]` fallback.

## Verification

- **Loopback live test**, following the existing live-test style in
  `tests/Allegiance.Core.Tests/BallotVotingLiveTests.cs`, which already
  drives a real `#draw` → OfferDraw → AcceptDraw → game-over chain. Win by
  elimination (disconnect every peer on one side) is an alternative
  trigger. It must assert all of the following:
  1. The old peers receive GAME_OVER(62) and then `MISSION_STAGE(37) Over`.
  2. A brand-new `Bot` joining afterwards receives `MISSION_STAGE(37)
     NotStarted`, not Over.
  3. That new joiner can ready up and the mission reaches `Starting`, then
     `Started` (real `StartCountdown`/`CompleteStartCountdown` path).
  4. The new mission's world is pristine. For example, a station destroyed
     before game-over exists again, and `Server.Missions[0]` is a
     different object from the pre-game-over mission.

  Run the test against the unfixed code first and record in the report
  that assertion 2 fails.
- Existing boot, game-over, ballot, conquest and P388 debug tests stay
  green.
- Whole-solution build with `/home/domingo/.dotnet/dotnet build`, 0 warnings
  and 0 errors. Bare `dotnet` on this box is a broken snap stub.
- Full suite via `/home/domingo/.dotnet/dotnet test`, 3 consecutive clean
  runs. Only the pre-existing documented live-wire timing flakes are
  acceptable. Name any flake you see.

## Deliverables

1. The fix plus tests, committed on **`master`** (not a side branch, not
   pushed; Domingo pushes). Run `git branch --show-current` before
   committing.
2. Report doc `docs/reports/P439_MISSION_OVER_ROTATION.md`. Cover:
   - native's real post-game-over flow as you verified it;
   - the pre-fix failing assertion;
   - the extracted assembly helper;
   - the swap and cleanup;
   - the orphaned-peer audit results;
   - debug-mode handling;
   - a concrete scoping note for the in-place re-seat follow-up.

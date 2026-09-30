# P455b — eliminated players see what native shows: the side-only game-over, plus the client's SIDE_INACTIVE

## Summary

P455 landed `CFSMission::DeactivateSide` (`FedSrv/fsmission.cpp:5318-5427`)
minus two pieces of the eliminated player's experience. This task (P455b) ports
those two:

1. **Server — the side-only game-over** (`fsmission.cpp:5420-5426`). When an
   elimination does *not* end the game (3+ sides, 2+ still active), native sends
   the full game-over screen **only to the eliminated side's group**, while every
   other side plays on. Added as step 7 of the port's `DeactivateSide`: if the
   win check did not declare a winner (mission still not `Over`) and the
   eliminated side still has connected ships/peers, send `GAME_OVER(62)` (with
   native's reason text `"Your side has been eliminated."`, `iSideWinner=NA`)
   and `GAME_OVER_PLAYERS(63)` to that side's peers only. It deliberately does
   **not** flip the mission stage and does **not** trigger P439's rotation.
2. **Client — `SIDE_INACTIVE(68)` handling.** Native's client
   (`appmsg.cpp:2580-2591`) calls `SetSideActive(sideID,bActive)` (and the AET
   bit when `bChangeAET`) then `OnTeamInactive`. The port's `ConnectionSession`/
   `ClientWorld` had no handling; now `ConnectionSession` dispatches
   `SideInactive.Fmid` → `ClientWorld.Apply(SideInactive)`, which records the
   per-side active state (and AET when `bChangeAET`), exposed via
   `ClientWorld.IsSideActive(sideId)`. It is surfaced in the in-mission
   scoreboard (`ScoreboardLayout.Build` now takes an `isSideActive` predicate
   and marks an eliminated side's rows `S<n>(DESTROYED)`), mirroring native's
   team list spelling out "destroyed" for an inactive team.

## What native does (authoritative reference)

- **`DeactivateSide` tail** (`fsmission.cpp:5420-5426`):
  ```cpp
  else if ((m_psideWon == NULL) && pships->n())
  {
      //Games not over for anyone but this team (only ships on the team will be players)
      CFMGroup* pgroup = CFSSide::FromIGC(pside)->GetGroup();
      g.fm.SetDefaultRecipient(pgroup, FM_GUARANTEED);
      QueueGameoverMessage();
      g.fm.SendMessages(pgroup, FM_GUARANTEED, FM_FLUSH);
  }
  ```
  Guard: no winner yet (`m_psideWon == NULL`) **and** the side still has ships
  (`pships->n()` — after the loop, only the still-connected player ships
  remain). Recipient: the side's group = that side's connected players.
- **`QueueGameoverMessage`** (`fsmission.cpp:3059+`) builds `GAME_OVER` with
  `m_pszReason ? m_pszReason : "Your side has been eliminated."` (so the
  side-only path uses the default), `iSideWinner = m_psideWon ? … : NA` (= NA
  here), all six `SideEndgameInfo` slots, and the per-player
  `GAME_OVER_PLAYERS` list (chunked ≤50). The full player list is built and
  addressed to whoever should see it — here only the eliminated side's group.

The port reuses the same builders as the full game-over
(`GameOver.ForMission` + a newly-extracted `BuildGameOverPlayerEntries`, shared
with the existing `BroadcastGameOverPlayers`); only the recipient set differs.

## Changes

### Server — `src/Allegiance.Server/Server.Lobby.cs`

- Extracted `BuildGameOverPlayerEntries(Mission)` (the per-player endgame roster
  loop) out of `BroadcastGameOverPlayers` so both the whole-mission and the
  side-only paths build the identical list (native builds one list for both,
  `fsmission.cpp:3115-3141`).
- Added `SendSideOnlyGameOver(mission, winnerId, reason, targets)` — packs
  `GAME_OVER` + `GAME_OVER_PLAYERS` and sends to the given peer set only, with
  no stage flip and no P439 rotation.
- Added **step 7** to `DeactivateSide` (after the win check):
  ```csharp
  if (mission.Stage != MissionStage.Over)          // no global winner/end
  {
      var eliminatedPeers = _peerShips
          .Where(kv => ReferenceEquals(kv.Value.Ship.Mission, mission)
                    && kv.Value.Ship.SideId == sideId)
          .Select(kv => kv.Key).ToArray();
      if (eliminatedPeers.Length > 0)
          SendSideOnlyGameOver(mission, GameOver.NA,
              "Your side has been eliminated.", eliminatedPeers);
  }
  ```
  `mission.Stage != Over` is the port's `m_psideWon == NULL` (the win check
  flips the stage to `Over` iff it declares an end; on the 3+-side continue path
  the stage is unchanged). "The side still has ships" reduces to "the side still
  has a connected peer": `DeactivateSide`'s kills finalize the eliminated side's
  players but do **not** disconnect them (they stay in `_peerShips`, just unable
  to respawn — the P455 `Side.Active` gate in `BroadcastKillShip`), so the
  `_peerShips` filter to this mission + sideId is exactly native's
  `pships->n()` after the kills and `CFSSide::FromIGC(pside)->GetGroup()`.

### Client — `src/Allegiance.ClientCore/Session/ConnectionSession.cs`,
`src/Allegiance.ClientCore/State/ClientWorld.cs`

- `ConnectionSession`: `case SideInactive.Fmid → _world.Apply(SideInactive.Decode(m))`.
- `ClientWorld`: new `_sideActiveBySideId` dictionary; `Apply(SideInactive)`
  sets `_sideActiveBySideId[m.SideId] = m.BActive` and, when `m.BChangeAet`,
  `AllowEmptyTeams = m.BAet`; accessors `IsSideActive(sideId)` (unknown →
  active) and `AllowEmptyTeams`; both cleared in `Reset()`.

### MonoGame client — `src/Allegiance.ClientCore.MonoGame/Rendering/ScoreboardLayout.cs`,
`Game1.Rendering.cs`

- `ScoreboardLayout.Build(entries, isSideActive = null)` marks an inactive
  side's rows `S<n>(DESTROYED)`; the in-mission scoreboard passes
  `ClientWorld.IsSideActive`. Pure ASCII, mirroring native's "destroyed" text.

## Native `OnTeamInactive` screens vs what the port now shows

Native's client reacts to `SIDE_INACTIVE` by calling `OnTeamInactive` in three
screens (all re-render on it):

- **`MissionsDlg`** (the lobby mission/team list, `TrekDlgs.h:492`) and
  **`SideStrategyDlg`** (the side-select screen, `TrekDlgs.h:542`) declare
  `OnTeamInactive` to redraw the team grid/roster.
- **`TeamScreen`** (the in-game team/score screen,
  `src/WinTrek/teamscreen.cpp:3682-3693`) — the screen whose team list
  (`m_plistPaneTeams`, `TeamPainter`) is the scoreboard/team list. On
  `OnTeamInactive` it force-refreshes the teams pane, status text, prompt, title,
  button states and squad-stats. The `TeamPainter` marks an inactive team:
  before game start it is **removed** from the list
  (`if (!SideActive && GetStage() != STAGE_STARTED) return;`, teamscreen.cpp:210);
  during a started game an empty-inactive team shows the word
  **"destroyed"** (teamscreen.cpp:318) instead of "available", its team tab is
  unchecked (`m_pbuttonsTeam[i]->SetChecked(SideActive(i))`, :1531), and it
  cannot be selected (KGJV #114, :2523).

The port has no per-team in-game list screen; its closest in-mission analogue of
native's TeamScreen team list is the **scoreboard overlay** (the per-player
name/side/kills/deaths panel). That is where the inactive state is surfaced:
rows belonging to an eliminated side are marked `S<n>(DESTROYED)`. Pre-game
rendering (side-select screen) intentionally leaves the P202 behaviour
untouched (out of scope here).

## Finding: what an eliminated player can still do (native vs port)

**Native.** After the side-only game-over the eliminated player is shown the
game-over screen, but the mission stage is **not** `Over` (only a full game-over
flips it). `GameOverScreen::OnButtonBack` (`gameoverscreen.cpp:609-625`)
branches on that:
- stage `== STAGE_OVER` → `StartQuitMission()` (full game-over: leave);
- otherwise (our case) → `GetWindow()->screen(ScreenIDTeamScreen)` — the player
  is returned to the **TeamScreen (team lobby)** of the still-running mission,
  where they can pick another side / sit in the lobby. They do not respawn on
  their eliminated side (it has no restart station — the `Side.Active` respawn
  gate), and they do not continue spectating the live match in place; they are
  pulled back to the lobby.

**Port.** The eliminated player now receives `GAME_OVER(62)`
("Your side has been eliminated.") + `GAME_OVER_PLAYERS`, so
`ClientWorld.GameOverResult` is set and the MonoGame client draws its existing
modal game-over screen (P262) — the "see the correct end screen" parity goal is
met. However, the port's game-over screen is modal with no in-app dismiss, and
the port has **no in-mission team-lobby return / re-seat flow** for a running
mission (P440 deliberately left "already-connected peers are not re-seated; they
must reconnect" as a follow-up), so the native "Back → in-mission team lobby"
behaviour cannot be matched today without building that missing subsystem.
Following the task's rule, this is **named as a follow-up, not fabricated here**:
the smallest faithful change (server sends the side-only game-over so the
eliminated player sees the correct screen; client records and marks the inactive
side for the survivors) is what this task ships.

## Tests

### Server / wire — `tests/Allegiance.Core.Tests/SideEliminationLiveTests.cs`

Added a 3-bot / 3-side live harness (`ConfigThree`, `Harness3`, `SeatThree`,
`MkCharlieStation`) reusing the P455 file's `Combo` missile driver, on
`XXV_simple.igc` with sides Alpha/Bravo/Charlie (civs 1/3/2), bots seated on
sides 0/1/2, and all of Charlie's map stations stripped so it owns exactly one
synthetic restart station.

**`Eliminating_side2_sends_side_only_gameover_to_it_alone`** — a real
cross-side missile from side 0 destroys side 2's only restart station. Asserts:
- Side 2 is deactivated; sides 0 & 1 stay active; `mission.Stage == Started`
  (not Over, no rotation).
- The side-2 bot receives `GAME_OVER(62)` with `SideWinner == NA` and reason
  `"Your side has been eliminated."`, plus `GAME_OVER_PLAYERS(63)` whose roster
  names the eliminated side's player.
- The side-0 and side-1 bots receive `SIDE_INACTIVE(68)` for side 2
  (`bActive=false`) and the `"Charlie is no more."` chat, but **no** `GAME_OVER`.

Observed live log (pass):
```
[sideinactive] side 2 deactivated (lost last restart station) => SIDE_INACTIVE(68) bActive=false -> 3 peer(s)
[sideinactive] side 2 elimination chat "Charlie is no more." -> 3 peer(s)
[sidegameover] mission #0 -> side-only GAME_OVER(62) "Your side has been eliminated." + GAME_OVER_PLAYERS(63) to 1 peer(s) on the eliminated side (mission NOT ended, no rotation)
P455B-LIVE: … side-2 bot got side-only GAME_OVER [msg=Your side has been eliminated.] (iSideWinner=-1) + GAME_OVER_PLAYERS; sides 0/1 got SIDE_INACTIVE only, stage still Started
```

### Pre-fix failing evidence (discriminator)

Run with the step-7 `SendSideOnlyGameOver` call temporarily disabled
(`if (false && …)`, equivalent to the unfixed P455-only tree) the test
**FAILED** — the eliminated side's bot got no `GAME_OVER`:
```
Assert.Single() Failure: The collection was empty   (line 497, cBot.GameOvers)
Failed SideEliminationLiveTests.Eliminating_side2_sends_side_only_gameover_to_it_alone
```
Restored, it passes. This proves the test genuinely discriminates the fix.

### Client — `tests/Allegiance.ClientCore.Tests/ClientWorldSideInactiveTests.cs`

3 unit tests exercising the exact `ConnectionSession` dispatch path
(`SideInactive.Decode` → `ClientWorld.Apply`):
- decoded `bActive=false` marks side 2 inactive via `IsSideActive(2)`, while
  unreported sides 0/1 stay active;
- a later `bActive=true` re-activates (last-write-wins);
- `AllowEmptyTeams` only updates when `bChangeAET=true` (the P455 elimination
  broadcast carries `bChangeAET=false` and must not flip AET).

## Verification

- Build (`/home/domingo/.dotnet/dotnet build`): **0 warnings, 0 errors**.
- New tests: 1 live (3-side side-only game-over) + 3 client unit tests, all pass
  in isolation; the live test's discriminating pre-fix failure recorded above.
- Regression proof on the same tree:
  - `Allegiance.Lobby.Tests`: 12/12 pass.
  - `Allegiance.ClientCore.MonoGame.Tests`: 908/908 pass (incl. the
    `ScoreboardLayout` tests — the new optional `isSideActive` param is
    backwards-compatible).
  - `Allegiance.ClientCore.Tests`: 325/329 — the only 4 failures are live-wire
    integration classes (`ShipDeleteGoneLiveTests`,
    `JoinFlowLiveTests.EntityTable_TracksARealTickingServer`,
    `FullSessionKillIntegrationTests`), **proven pre-existing** by a pristine
    A/B: with all P455b changes stashed and rebuilt, the identical tests still
    fail on the pristine tree.
- Full `Allegiance.Core.Tests` suite, **3 consecutive runs** (rotating subsets,
  no new failure category, P455b tests never among the failures):
  - run 1: 3559 passed / 9 failed
  - run 2: 3561 passed / 7 failed
  - run 3: 3557 passed / 11 failed
  Every failure is a documented pre-existing live-wire flake (details below);
  the changing counts/subset (7→9→11) is the environmental variance, exactly as
  P455 and the P-series ledger documented — P455b adds **zero** new failures.
- Commit: on `master`, not pushed (Domingo pushes).

## Pre-existing live-wire flakes observed across the 3 runs (not regressions)

Each was either documented in P455's report / the P-series ledger or proven on
the pristine tree; none exercises P455b's code:

- `ConquestTerritoryLiveTests.*` (3) and `GameOverPlayersWireTests.*` conquest
  (2) — the documented 10-11 s capture-path environment failure (P455 report:
  "fail on pristine; current-environment pre-existing").
- `LiveTurretGunnerBindingTests.Board_binds_gunner_and_active_turret_update_roundtrips_live` —
  documented pre-existing machine-state flake (P215; fails in isolation/pristine).
- Fast live-wire timing flakes (rotate every run, ~10-700 ms): `LiveChatSectorWireTests…`,
  `LiveRipcordTests.*`, `LiveWinConditionTests.Multi_mission_disconnect_scopes_per_mission`,
  `ShipsUpdateLiveTests.*`, `AddPartTests.*` (pickup variants), `LoadoutChangeLiveTests…`,
  `LiveTreasurePickupTests.*` — the chronic live-network family the ledger names.
- ClientCore (pristine-proven): `ShipDeleteGoneLiveTests.DockOut_WatcherLatchesGone_UndockReturnsAndClears`,
  `JoinFlowLiveTests.EntityTable_TracksARealTickingServer`,
  `FullSessionKillIntegrationTests.FullSession_ThreePeers_TeammateComposition_Then_RealCrossSideKill`.

P455b introduces **no** new failures; all observed failures predate it (verified
by pristine A/B for the ClientCore set and by the documented capture-path/wire
classification for the rest).

## Files changed

- `src/Allegiance.Server/Server.Lobby.cs` — `BuildGameOverPlayerEntries`,
  `SendSideOnlyGameOver`, `DeactivateSide` step 7.
- `src/Allegiance.ClientCore/Session/ConnectionSession.cs` — `SideInactive.Fmid`
  dispatch.
- `src/Allegiance.ClientCore/State/ClientWorld.cs` — per-side active state, AET,
  `Apply(SideInactive)`, `IsSideActive`.
- `src/Allegiance.ClientCore.MonoGame/Rendering/ScoreboardLayout.cs` +
  `Game1.Rendering.cs` — scoreboard `(DESTROYED)` marking.
- `tests/Allegiance.Core.Tests/SideEliminationLiveTests.cs` — 3-side harness +
  side-only game-over test.
- `tests/Allegiance.ClientCore.Tests/ClientWorldSideInactiveTests.cs` — client
  unit tests.

## Follow-ups

- **Eliminated-player "Back → in-mission team lobby" return** (native
  `gameoverscreen.cpp:609-625` else-branch). Needs an in-mission lobby / re-seat
  flow the port lacks (P440's "peers are not re-seated; reconnect to play again"
  is the current behaviour). Separate task; out of scope here per the task rule.

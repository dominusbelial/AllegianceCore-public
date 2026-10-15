# P455 — a side that loses its last restart station is eliminated, and the game ends

## Why this task exists

Found in the 2026-09-29 gameplay-parity pass and spot-verified by the
coordinator: **destroying or capturing the enemy's home base does not end
the game.** A bomber run on the garrison has no match consequence, which
removes the entire strategic arc of the original.

- **Native: eliminating every restart station of a side is always a
  win**, independent of any goal setting.
  `CFSMission::CheckForVictoryByStationKill(pstationKilled, psideOld)`
  (`~/Development/Allegiance/src/FedSrv/fsmission.cpp:3568`) ends with:
  `if ((psideWon == NULL) && psideOld)`, walk `psideOld->GetStations()`
  skipping `pstationKilled`, and if none has `c_sabmRestart`, call
  `DeactivateSide(psideOld)` (`fsmission.cpp:3698-3714`, comment:
  "Elimiation of all restart stations of all other sides is always a win").
  - Called on station kill: `FedSrv.CPP:12343-12344`
    (`CheckForVictoryByStationKill(pstation, pside)`).
  - Called on capture: `CheckForVictoryByStationCapture(psideTest, psideOld)`
    → `CheckForVictoryByStationKill(NULL, psideOld)`, `fsmission.cpp:3563-3566`.
- **Native `DeactivateSide`** (`fsmission.cpp:5318-5420`) does the following:
  - Sets `pside->SetActiveF(false)` and clears `rgfActive`/`rgfReady`.
  - Kills every drone of the side
    (`KillShipEvent`, `pilotType < c_ptPlayer`, `:5356-5359`).
  - Force-kills flying players with "negative damage" and resets them
    (`:5360-5367`).
  - Broadcasts `SIDE_INACTIVE(68)` `{sideID, bActive=false, bChangeAET=false}`
    to the whole mission (`:5371-5376`).
  - Sends chat `"%s is no more."` to everyone (`:5377`).
  - Then runs `CheckForVictoryByInactiveSides(bAllSidesInactive)`
    (`:5378-5415`): all inactive → `GameOver(NULL, "All sides were destroyed")`;
    exactly one winner → `GameOver(psideWin, "<name> won by outlasting all
    other sides.")`.
- **Native `CheckForVictoryByInactiveSides`** counts a side as alive only
  if `HasPlayers(pside, false) && pside->GetActiveF()`
  (`fsmission.cpp:3767-3790`).
- **The port has none of this.**
  - The station-kill path (`src/Allegiance.Core/Model/Sector.cs:1805-1821`)
    removes the station, raises `KillStationEvent`, then only runs
    `RecomputeConquestAndTerritory(null)`. That recompute is gated on
    `ConquestEnabled`/`TerritoryEnabled`, which nothing outside tests ever
    sets (`Mission.cs:153,165`; grep finds zero production setters).
  - The capture path (`Sector.cs:3025-3040`) is the same.
  - `SabmRestart` is read only for respawn (`Server.Docking.cs:1771`).
  - The port's `CheckForVictoryByInactiveSides` (`Server.Lobby.cs:40-103`)
    counts connected players only and ignores side activity. P202 added a
    real `Side.Active` field, used today only for pre-game roster shrinking
    via `SIDE_INACTIVE` (`Server.Dispatch.cs:1080-1095`, whose comment
    explicitly defers the in-game `DeactivateSide` flow).
  - `SideInactive` already has a wire codec (`src/Allegiance.Core/Wire/SideInactive.cs`).

## Scope

1. **Elimination check on both station-loss paths.** After the kill at
   `Sector.cs:~1809` and after the capture at `Sector.cs:~3028`:
   - Determine the losing side (`psideOld`). For a capture, that is the
     station's side *before* the re-side.
   - Check whether that side still owns any station whose type has
     `DataStationTypeIGC.SabmRestart`, excluding the killed station. That
     station is already removed, so confirm this rather than assume it.
   - If it owns none and the side is still `Active`, run the new
     `DeactivateSide`.
   - Keep the existing conquest/territory recompute as-is.
   - Route it through the same host-seam pattern the kill/capture
     broadcasts use (`ServerHost.StationKilled`/`StationCaptured`,
     wired at `Server.Lifecycle.cs:679-680`). Put the side logic in the
     server, not in `Sector`.
2. **`DeactivateSide(Mission, Side)`**, ported minimally and in native order:
   - `side.Active = false`.
   - Kill every NPC ship of the side through the existing kill path, so
     `KILL_SHIP(105)` goes out as for any death.
   - **Every ship on the side, before any kill** (`fsmission.cpp:5344-5354`):
     terminate all its parts and set ammo and fuel to 0.
   - Players on that side (`:5360-5366`): force-kill any that are in a
     cluster with no parent ship, through the same kill path, following
     native's negative-damage "forced" intent. Then, for **every** player
     ship, including docked ones, do native's `Reset(false)` and
     `ShipStatusExit()` equivalent, and leave them unable to respawn.
     **Domingo's decision: eliminated players see exactly what native
     shows**, so port this faithfully, don't design around it. The
     side-only game-over screen and client SIDE_INACTIVE handling are
     P455b, next in the chain.
   - Broadcast `SIDE_INACTIVE(68)` with `bActive=false`, `bChangeAET=false`
     to the whole mission.
   - Send the `"<Side name> is no more."` chat to everyone.
3. **Make the victory check consume `Side.Active`**, as native's
   `HasPlayers && GetActiveF()` does. Then, right after `DeactivateSide`:
   - no active sides left → `DeclareGameOver(mission, GameOver.NA,
     "All sides were destroyed")`
   - exactly one active side → `DeclareGameOver(winner,
     "<name> won by outlasting all other sides.")`

   The existing call on peer departure must keep working.
4. **Debug mode (P388).** P388 deliberately skips the
   *player-presence* victory check in debug mode, because 0 players is the
   normal state of the unattended arena (`Server.Lobby.cs:~46-70`). Its
   stated rule is that "only a genuine combat/conquest outcome may end a
   debug-mode mission." Station elimination **is** such an outcome, so:
   - In debug mode, elimination still deactivates the side and ends the
     game.
   - The winner test drops the `HasPlayers` half and uses `Side.Active`
     alone, so a 0-player arena can still produce a winner.
   - The existing player-presence-only skip stays exactly as it is.
   - The P379 debug topology's synthetic garrisons are StationTypeId 15,
     which inherits `SabmRestart` (`Server.Lifecycle.cs:~329`), so
     elimination works there. Confirm this.
   - After the game-over, **P439** (running now) rotates to a fresh
     mission and handles debug auto-start. Do not duplicate that. If P439
     has already landed, reference its helper. If it hasn't, stop at
     `DeclareGameOver`.

## Explicitly out of scope

- Enabling the goal-based win modes (conquest %, territory, prosperity,
  artifacts, flags, kills) through config. That is a separate follow-up,
  which fits P439's mission-assembly helper.
- Mission rotation after game-over (P439).
- Native's alliance handling (`bAllies`, `fsmission.cpp:3718-3745`). The
  port has no alliances.
- `bAllowEmptyTeams`/KGJV #62 behaviour and pre-game `SIDE_INACTIVE` (P202).
  Keep P202 working; don't extend it.
- The side-only game-over (native `fsmission.cpp:5420-5426`, 3+ sides)
  and the client's `SIDE_INACTIVE` handling are **P455b**. Don't build them
  here.
- Station kill/capture mechanics themselves (damage, capture rules).

## Verification

- A loopback wire test (existing `Bot` patterns, 2 sides). Destroy side 1's
  only restart station through the real damage path, or the smallest
  real seam the existing station-kill tests use. Assert that a Bot on
  side 0 receives `SIDE_INACTIVE(68)` for side 1, the "is no more." chat,
  and `GAME_OVER(62)` naming side 0 with the native reason text.
  **Run it against unfixed code first and record that it fails** (no
  `GAME_OVER`) in the report.
- Negative test: a side with two restart stations loses one → no
  `SIDE_INACTIVE`, game continues.
- A capture-path test: the last restart station is captured → the old
  side is eliminated.
- A debug-mode test: with `DebugMode=true` and 0 connected players,
  elimination still produces `GAME_OVER` with the correct winner.
- Build with `/home/domingo/.dotnet/dotnet build`: 0 warnings, 0 errors.
  Bare `dotnet` on this box is a broken snap stub, so always use the full
  path.
- Full suite with `/home/domingo/.dotnet/dotnet test`, 3 consecutive clean
  runs. Only the pre-existing documented live-wire timing flakes are
  acceptable; name any flake you see.

## Deliverables

1. The fix and tests, committed on **`master`**, not pushed (Domingo
   pushes). Check `git branch --show-current` before committing.
2. Report doc `docs/reports/P455_SIDE_ELIMINATION_ON_RESTART_STATION_LOSS.md`.
   Cover the pre-fix failing-test evidence, the ported `DeactivateSide`
   steps, what an eliminated player experiences, the debug-mode decision,
   and how this composes with P439.

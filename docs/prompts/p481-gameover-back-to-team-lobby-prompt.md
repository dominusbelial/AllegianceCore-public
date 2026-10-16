# P481 — game-over screen Back → team lobby (native OnButtonBack), for rotation and elimination

## Why this task exists

Native's game-over screen has a Back button. `GameOverScreen::OnButtonBack`
(`WinTrek/gameoverscreen.cpp:609-625`, native at `~/Development/Allegiance/src/`)
does this:
- **stage == `STAGE_OVER`** → `StartQuitMission()`, which leaves the mission;
- **otherwise** → `screen(ScreenIDTeamScreen)`, the **team lobby** of the
  same mission.

Two port cases need the "otherwise" branch:
- **After a full game-over plus rotation (P439/P480).** The stage is
  NotStarted, and players are still seated on their sides in the new
  mission (P480, previous task). Back takes them to the team lobby for the
  next game.
- **After a side-only game-over (P455b, `ba41a3f8`).** The eliminated
  player's mission is still Started for the other sides. Back takes them to
  the team lobby, where native lets them request a position on a different,
  still-active side. The port already has the position-request/join path:
  `TryJoinSide`, `PositionReq` broadcasts (`src/Allegiance.Server/Server.Lobby.cs`
  ~1505-1520, ~1705).

**Today the port's game-over screen is modal, with no dismiss**
(`Rendering/GameOverScreenRenderer.cs`, drawn in `Game1.Rendering.cs:~1224-1228`
while `ClientWorld.GameOverResult` is set). P455b's report documents this
gap ("Finding: what an eliminated player can still do"). The port does have
side-select and ready-check overlays: `SideSelectOverlayRenderer`/`SideSelectLayout`
and `ReadyCheckOverlayRenderer`/`ReadyCheckLayout`/`ReadyCheckRoster`
(P281/P282). **Reuse them as the "team lobby" rather than building a new
screen.**

Check that P480's commit exists (`git log --oneline | grep P480`). If it
doesn't, do only the elimination half and say so.

## Scope

1. **Add a Back action to the game-over screen**, with native's key or
   button where one exists (trace it in native), mirroring `OnButtonBack`'s
   branch:
   - stage `Over` → leave the mission (the port's existing quit/leave path);
   - otherwise → clear `GameOverResult` and show the team lobby: the
     side-select/ready-check overlays for the current mission.
2. **Returning to the team lobby after a rotation.** The player sees their
   current side, the roster and the ready check for the next game, and can
   ready up. A fresh `MISSION_STAGE` NotStarted must also reset any client
   state that assumed the previous game was running. Find what that covers,
   such as the scoreboard, kill feed, `IsGone` latches, and world entities
   from the old world if P480 re-sends the world.
3. **Returning to the team lobby after elimination.** The player can request
   a still-active side through the existing request path. Inactive sides
   (P455b's `SIDE_INACTIVE` state) are shown as native shows them and can't
   be picked.

## Explicitly out of scope

- The server-side migration (P480).
- Any new lobby UI beyond what native's TeamScreen shows and the existing
  overlays can express. Name bigger gaps as follow-ups.
- The outer lobby / mission list after leaving a mission.

## Verification

- Pure tests, no graphics device, for the Back branching (stage Over →
  leave, else → team lobby) and for the eliminated-side picker excluding
  inactive sides. Show they fail before the change.
- A loopback end-to-end test at the `ClientWorld`/`ConnectionSession` level,
  if the MonoGame state is testable there:
  - after rotation, a client leaves the game-over state and can send ready;
  - after elimination, a client can request and be seated on another side.
- Whole-solution build with `/home/domingo/.dotnet/dotnet build`, 0
  warnings and 0 errors. Full `/home/domingo/.dotnet/dotnet test`, 3
  consecutive runs. Cite `docs/TEST_FLAKES.md` for any remaining flake.

## Deliverables

1. Committed on **`master`**, not pushed. Check with
   `git branch --show-current`.
2. The report `docs/reports/P481_GAMEOVER_BACK_TO_TEAM_LOBBY.md`. Cover the
   native Back mapping, the key or button chosen, the client-state resets on
   the new game, and any follow-ups.

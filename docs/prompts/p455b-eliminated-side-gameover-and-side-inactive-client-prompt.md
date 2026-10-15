# P455b — eliminated players see what native shows: the side-only game-over, plus client SIDE_INACTIVE

## Why this task exists

**Domingo's decision (2026-09-29): eliminated players see exactly what
native shows.** P455, which runs just before this task, ports native
`CFSMission::DeactivateSide` (`FedSrv/fsmission.cpp:5318-5430`, native at
`~/Development/Allegiance/src/`). That covers stripping and resetting the
side's ships, `SIDE_INACTIVE(68)`, the `"<side> is no more."` chat, and
the game-over when zero or one sides remain. Check that P455's commit
exists (`git log --oneline | grep P455`). If it doesn't, **stop and
report**.

Two native pieces of the eliminated player's experience are left:

1. **Side-only game-over.** When the elimination does *not* end the game
   (3+ sides and 2+ still active), native sends the full game-over
   screen **only to the eliminated side's group**, while everyone else
   plays on (`fsmission.cpp:5420-5426`):
   `g.fm.SetDefaultRecipient(pgroup, …); QueueGameoverMessage();
   g.fm.SendMessages(pgroup, …)`. `QueueGameoverMessage`
   (`fsmission.cpp:3059+`) builds `GAME_OVER` with the reason text
   **`"Your side has been eliminated."`** when no mission-level reason is
   set, plus the per-side stats and the players list. The port already
   builds these for a full game-over: see `BroadcastGameOverPlayers` in
   `src/Allegiance.Server/Server.Lobby.cs:~230-280` and the `DeclareGameOver`
   path. Reuse those builders with a side-group recipient set. **Do not
   trigger the mission end or P439's rotation**, because the mission is not
   over for the other sides.
2. **The client handles `SIDE_INACTIVE(68)`.** Native: `appmsg.cpp:2580-2591`
   → `m_pMissionInfo->SetSideActive(sideID, bActive)` (and the AET bit if
   `bChangeAET`) → `OnTeamInactive`. The WinTrek screens implement that
   in `WinTrek/TrekDlgs.h:492,542`. Trace which screens, and what they
   show for an inactive team, for example in the team list and the
   scoreboard. The port's `ConnectionSession`/`ClientWorld` have **no**
   `SideInactive` handling. The codec exists (`src/Allegiance.Core/Wire/SideInactive.cs`),
   and only the test `Bot` decodes it (`src/Allegiance.Bot/Bot.cs:126`).

## Scope

1. **Server:** in P455's `DeactivateSide`, add the final
   `else if (no winner && side still has ships)` branch. It sends
   `GAME_OVER` (with native's reason text) and `GAME_OVER_PLAYERS` to that
   side's connected peers only. Check that the MonoGame client shows its
   existing game-over screen for them, and that it does **not** show for
   other sides.
2. **Client:** add `case SideInactive.Fmid` → `ClientWorld.Apply(SideInactive)`,
   recording per-side active state (and AET when `bChangeAET`). Surface it
   wherever the port already renders the equivalent of the native screens
   you traced, for example the scoreboard or team list. Mark only what
   native marks; don't invent new UI.
3. Confirm what an eliminated player can still do after the side-only
   game-over in native: stay and spectate, chat, or only leave. Match it
   with the smallest change. If it needs something the port lacks
   entirely, stop at a report and name a follow-up.

## Explicitly out of scope

- `DeactivateSide`'s other behaviour and the win check (P455).
- Mission rotation (P439). The side-only game-over must not trigger it.
- Pre-game `SIDE_INACTIVE` / `bAllowEmptyTeams` (P202). Keep that working.
- Alliances.

## Verification

- A loopback wire test with 3 sides: eliminate side 2 while sides 0 and 1
  are still active.
  - A side-2 Bot gets `GAME_OVER` with `"Your side has been eliminated."`
    and `GAME_OVER_PLAYERS`.
  - Side-0 and side-1 Bots get `SIDE_INACTIVE` for side 2 but **no**
    `GAME_OVER`, and the mission stage is still Started.
  - Run it against unfixed code first and record the failure.
- A client-side test: `ClientWorld` reflects side 2 as inactive after the
  message.
- Whole-solution build with `/home/domingo/.dotnet/dotnet build`, 0
  warnings and 0 errors. Full `/home/domingo/.dotnet/dotnet test`, 3
  consecutive clean runs. Only the pre-existing documented live-wire timing
  flakes are acceptable, and each one seen must be named.

## Deliverables

1. Committed on **`master`**, not pushed. Check with
   `git branch --show-current`.
2. Report doc `docs/reports/P455B_ELIMINATED_SIDE_GAMEOVER.md`. Cover what
   native's `OnTeamInactive` screens show and what the port now shows, plus
   the finding on post-elimination options.

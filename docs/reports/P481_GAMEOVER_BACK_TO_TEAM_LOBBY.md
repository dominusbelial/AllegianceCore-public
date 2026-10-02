# P481 — game-over screen Back → team lobby (native OnButtonBack), for rotation and elimination

Date: 2026-10-01 · Landed on `master` (not pushed; Domingo pushes) · Branch `master`

## Summary

The port's game-over screen (P262) was **modal with no dismiss** (P455b's report
named this gap: "what an eliminated player can still do"). This task adds the
**Back action** — a faithful port of native
`GameOverScreen::OnButtonBack` (`WinTrek/gameoverscreen.cpp:609-625`) — so a
player can leave the finished game's result screen and return to the **team
lobby** of the current mission. The two cases that need the "otherwise" branch
both now work end-to-end:

1. **After a full game-over + rotation (P439/P480)** the stage is `NotStarted`
   and the player is re-seated on its side in the new mission (P480). Back takes
   them to the ready-check lobby for the next game, where they can ready up.
2. **After a side-only game-over (P455b)** the eliminated player's mission is
   still `Started` for the other sides. Back takes them to the side-select
   picker, where they can request a position on a **still-active** side
   (inactive/eliminated sides shown `(DESTROYED)` and not pickable), re-seated
   through the existing `TryJoinSide`/`POSITIONREQ` path.

The lobby reuses the existing **ready-check** (`ReadyCheckOverlayRenderer`/
`ReadyCheckLayout`/`ReadyCheckRoster`, P282) and **side-select**
(`SideSelectOverlayRenderer`/`SideSelectLayout`, P281) overlays — no new screen.

## Native Back mapping (authoritative reference)

`WinTrek/gameoverscreen.cpp:609-625`:

```cpp
bool OnButtonBack()
{
    if (trekClient.MyMission()->GetStage() == STAGE_OVER)
    {
        GetWindow()->StartQuitMission();          // leave the mission
    }
    else
    {
        // ...commented-out auto-ready...
        GetWindow()->screen(ScreenIDTeamScreen);  // team lobby of the SAME mission
    }
    return true;
}
```

So native branches **solely on the mission stage**:
- `STAGE_OVER` → `StartQuitMission()` (leave);
- otherwise → `ScreenIDTeamScreen`, the team lobby.

Native's Back is the `m_pbuttonBack` **on-screen button** ("backButtonPane",
gameoverscreen.cpp:315,341). Two nuances make this exact branch reachable for
our two cases:
- A full game-over's stage is Over only *briefly*: native `CFSMission::GameOver`
  → `Vacate()`/`SetStage(STAGE_NOTSTARTED)` rotates immediately
  (fsmission.cpp:2604-2622, 3493-3521), so by the time a human clicks Back the
  stage is already `NotStarted` → the **else** branch → team lobby.
- The side-only elimination (P455b, fsmission.cpp:5420-5426) does **not** flip
  the mission stage — it stays `Started` → the **else** branch → team lobby.

## Key / button chosen

Native uses a mouse button; the port is keyboard-driven (Wayland, no reliable
mouse). The Back action is therefore bound to the **Backspace** key
(mnemonic: Back), **edge-detected** (held Backspace never re-fires), gated to
when the game-over panel is actually showing (`GameOverResult != null`,
`_state == Flying`) and not while composing chat / in the quick-chat menu
(where Backspace deletes text). The game-over panel (`GameOverScreenRenderer`)
now draws a **`[BACK] (BACKSPACE)`** button at the bottom to mirror native's
on-screen affordance.

## Making native's exact stage branch work in the port

The client must see the **real** stage for the branch to be correct. Two
changes in `ClientWorld` achieve that:

1. **`Apply(GameOver)` no longer flips the stage to Over.** Previously every
   `GAME_OVER(62)` forced `Mission.Stage = Over` — wrong for a side-only
   elimination (the mission is still running for the other sides), which would
   have misrouted the Back button to "leave" instead of the team lobby. Now the
   stage is driven **solely** by the server's real `MISSION_STAGE(37)`
   broadcast (native `SetStage` always broadcasts it, fsmission.cpp:1750-1768):
   - full game-over: the server sends `MISSION_STAGE Over` before the P439
     rotation (`Server.Lobby.cs:420`), so the client stage goes Over then
     NotStarted — exactly native;
   - side-only elimination: P455b's server sends no Over, so the stage stays
     Started — exactly native.
   `Apply(GameOver)` now only retains the snapshot for the result panel.

2. **`MISSION_STAGE Over` resets the finished game's world**
   (`ClientWorld.BeginNextMission`). This is the "fresh `MISSION_STAGE`
   NotStarted must reset client state that assumed the previous game was
   running" scope point. It is triggered by the *transition into Over* (guarded
   so it fires once), which the server sends **before** the P480 re-burst — the
   order-safe point to discard the old world before the new one repopulates:
   - clears the entity tables (ships, stations, warps, asteroids, projectiles,
     missiles, probes, mines, building effects, treasures, chaff, explosions,
     hull hits), the graph, countdown anchor, scoreboard/kill-feed rosters
     (`_scoreRoster`, `_roster`, `_events`), per-side ready / force-ready /
     leader / dev-tech / money maps, and transient systems (ripcord, missile
     lock, briefing, side-active);
   - clears `MyShipId` so the re-burst's `SET_CLUSTER` re-assigns the **fresh**
     ship id (P480 mints a new ship per migrated peer) — without this the client
     kept its old dead ship id and ready/rendering pointed at a ghost;
   - **preserves** `GameOverResult` (the panel stays up until Back) and
     `MySideId` (needed to title the panel; the rotation re-seats on the same
     side anyway).
   This mirrors native's `JOINED_MISSION` → `ResetStaticData` flush
   (clintlib.cpp:4193-4224) on the native client side.

## The Back action (`Game1.HandleGameOverBack`)

Branches via a **pure, headless-testable resolver** `GameOverBackPlanner`
(`src/Allegiance.ClientCore/State/GameOverBackPlanner.cs`):
`Plan(missionStage, ownSideActive)` →

- `missionStage == StageOver` → `LeaveMission`: the port's only client-initiated
  leave is **LOG OFF** (new `ConnectionSession.SendLogoff`, FM_CS_LOGOFF(3)) and
  return to the server browser (`ClientState.Lobby`). The outer mission-list
  lobby after leaving is explicitly out of scope.
- else + own side active → `TeamLobbyReadyCheck`: `ClearGameOver()`, reset the
  local ready latch, `_state = ReadyCheck` (player is re-seated on an active
  side after a rotation; ready cleared — `PLAYER_READY` re-enabled).
- else + own side inactive → `TeamLobbySideSelect`: `ClearGameOver()`,
  `_state = SideSelect` in a new elimination mode (`_sideSelectFromGameOver`);
  the picker excludes inactive sides so the eliminated player can only request a
  still-active side.

## Returning to the team lobby

### After a rotation (scope point 2)
The client reaches the ready-check lobby with the roster + ready check for the
next game and can ready up. The `BeginNextMission` reset covers the "assumed the
previous game was running" state (scoreboard, kill feed, per-side latches, old
world entities). Verified: after `DeclareGameOver` → rotation, the client's
`GameOverResult` is set, then stage `NotStarted` with a fresh `MyShipId`;
`ClearGameOver` dismisses the panel; `SendPlayerReady(newId)` reaches the server
and the next game auto-starts without reconnecting.

### After an elimination (scope point 3)
The eliminated player (own side inactive) goes to the **side-select** picker.
`SideSelectLayout` now takes an `isSideActive` predicate: inactive sides render
`(DESTROYED)` (muted, never highlighted; matching the P455b scoreboard wording
and native TeamScreen's "destroyed" paint, teamscreen.cpp:318/:210/:2523) and are
excluded from selection via the new pure `SelectableSideIds` helper. Picking an
active side sends a real `FM_C_POSITIONREQ(39)`; the server's existing
`TryJoinSide` re-seats the player on that side (JOIN_SIDE updates `MySideId`;
the ship id is unchanged). The elimination-mode picker exits back to the running
game once the local side is active again.

## Tests

### Pure / headless (fail-before demonstrated)
- `GameOverBackPlannerTests` (ClientCore.Tests, 5): stage-Over → leave regardless
  of side activity; NotStarted+active → ready-check; Started+inactive →
  side-select; Started+active → ready-check; null stage + inactive → side-select.
- `ClientWorldGameOverBackTests` (ClientCore.Tests, 5): side-only GAME_OVER does
  **not** flip the stage (⭐ fails on the pre-P481 code which forced Over — ran
  with the old flip restored: `Expected 2 Actual 3`); `MISSION_STAGE Over`
  resets the world + MyShipId but keeps the panel + side (⭐ fails with
  `BeginNextMission` disabled: "MyShipId stays the old dead ship"); the
  `BeginNextMission` preserves and `ClearGameOver` dismisses behaviour.
- `ClientWorldUnitTests.GameOver_RetainsSnapshot_AndKeepsStageUntouched` +
  `ClientWorldAnnouncerTests` (3 rewritten) — updated to the new faithful
  no-flip semantics.
- `SideSelectLayoutTests` (MonoGame.Tests, 2 new): `SelectableSideIds` excludes
  inactive sides; `Build` spells an inactive side `(DESTROYED)`, muted and never
  highlighted.

### Loopback end-to-end (`GameOverBackLiveTests`, ClientCore.Tests, LiveNetwork)
Real in-process server + real TCP `ConnectionSession` peer (the MonoGame data
plane), the same harness shape as `ClientJoinSideLiveTests`:
- **Rotation**: `DeclareGameOver` (reflectively, the single shared P178 path) →
  real GAME_OVER + MISSION_STAGE Over + P480 migration re-burst; asserts the
  client reaches `GameOverResult`, then stage `NotStarted` with a **fresh**
  `MyShipId` (⭐ fails pre-P481: old 8098 → got 8098), the Back plan is
  `TeamLobbyReadyCheck`, `ClearGameOver` dismisses, and `SendPlayerReady(newId)`
  auto-starts the next game without reconnecting.
- **Elimination**: 3-side server, peer seated on side 2; the eliminated client
  state is constructed from the exact broadcasts P455b sends an eliminated side
  (GAME_OVER "Your side has been eliminated." + SIDE_INACTIVE, stage stays
  Started); the Back plan is `TeamLobbySideSelect`, `ClearGameOver` dismisses,
  and a real `SendPositionReq(side0)` re-seats the peer onto the active side
  (JOIN_SIDE → `MySideId == side0`, same ship).

## Verification

- Whole-solution build with `/home/domingo/.dotnet/dotnet build`:
  **0 warnings, 0 errors** (full solution build confirmed).
- New tests green in isolation; both loopback tests green (and the two
  discriminating asserts proven to fail when the P481 wiring is removed).
- `Allegiance.ClientCore.Tests`: 361/362 — the sole failure
  `ShipDeleteGoneLiveTests.DockOut_WatcherLatchesGone_UndockReturnsAndClears`
  is a documented pre-existing live-wire flake (P455b's report lists it as
  pristine-proven; TEST_FLAKES).
- `Allegiance.ClientCore.MonoGame.Tests`: **1055/1055** pass.
- `Allegiance.Core.Tests`, 3 consecutive runs:
  - run 1: 3660 passed / 2 failed — `LiveRelaunchTests.Carrier_without_launch_slots_uses_backward_fallback`
    (TEST_FLAKES.md:40, "suspect (P472)") and
    `AutoReloadOnEmptyTests.Auto_reload_relays_RELOAD129_sector_wide`
    (TEST_FLAKES.md:52, "load-flake (P472)"); both green in isolation.
  - run 2: 3661 passed / 1 failed —
    `Transport.InboundFloodSafetyLiveTests.Oversized_packet_is_rejected_not_dispatched_others_keep_ticking`
    (TEST_FLAKES.md:55, "load-flake (P472)"); green in isolation.
  - run 3: **3662/3662 passed, fully clean**.
  None of the failures touch the game-over / rotation / elimination / client
  path; all are the documented rotating live-wire/load flakes (each verified
  green in isolation), and the third run was completely clean.

## Out of scope / follow-ups

- The server-side migration (P480) — already landed; P481 is the client half.
- The outer lobby / mission list after leaving a mission (native `StartQuitMission`
  → mission list). The port's "leave" is LOG OFF → server browser.
- Any new lobby UI beyond what the existing ready-check/side-select overlays can
  express. Named follow-ups: a combined TeamScreen-like roster+picker (native's
  TeamScreen mixes both), an auto-dismiss of the game-over panel when the next
  game's countdown begins (native `OnFrame` auto-switches to TeamScreen at
  STAGE_STARTING), and re-spawning the eliminated player onto a new side
  mid-Started-game (P455b's TryJoinSide moves the ship but does not spawn a
  fresh one).

## Files changed

- `src/Allegiance.ClientCore/State/ClientWorld.cs` — `Apply(GameOver)` no-flip;
  `Apply(MissionStageMessage)` reset-on-Over; `BeginNextMission()`; `ClearGameOver()`.
- `src/Allegiance.ClientCore/State/GameOverBackPlanner.cs` — new pure Back resolver.
- `src/Allegiance.ClientCore/Session/ConnectionSession.cs` — `SendLogoff()`.
- `src/Allegiance.ClientCore.MonoGame/Game1.FlightInput.cs` — Back (Backspace)
  edge handler on the game-over screen.
- `src/Allegiance.ClientCore.MonoGame/Game1.Panes.cs` — `HandleGameOverBack()`;
  side-select elimination mode; active-side picker.
- `src/Allegiance.ClientCore.MonoGame/Game1.Session.cs` / `Game1.cs` —
  `_sideSelectFromGameOver`/`_backKeyWasDown` fields + fresh-logoff reset.
- `src/Allegiance.ClientCore.MonoGame/Rendering/GameOverScreenRenderer.cs` —
  Back button + hint.
- `src/Allegiance.ClientCore.MonoGame/Rendering/SideSelectLayout.cs` —
  `isSideActive` predicate + `SelectableSideIds`.
- Tests: `GameOverBackPlannerTests`, `ClientWorldGameOverBackTests`,
  `GameOverBackLiveTests` (new); `ClientWorldUnitTests`, `ClientWorldAnnouncerTests`,
  `SideSelectLayoutTests` (updated/added).

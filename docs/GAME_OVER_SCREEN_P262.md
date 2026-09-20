# GAME_OVER_SCREEN_P262 — real game-over / results screen (not sound-only)

Closes a real gap in the MonoGame client: native FreeAllegiance has a real
`GameOverScreen` (`WinTrek/gameoverscreen.cpp`, 1033 lines) — title, reason
text, countdown, chat pane, save-stats button. Until now this port's client was
**sound-only** for a mission ending: `Game1.cs` played a sound and set a flag
and nothing else — the ship sat frozen in the 3D scene with zero on-screen
explanation. This stage adds the real on-screen result: **a real title + the
real reason text already available from this port's own `GameOver`(62) wire
message** (the P178 win-by-elimination / P191 conquest wins already produce a
real `reason` server-side via `DeclareGameOver`), shown the moment the client
receives the game-over signal.

## What landed

| Piece | Location | Kind |
|---|---|---|
| `GameOverResult` on the world | `src/Allegiance.ClientCore/State/ClientWorld.cs` | Retains the real `GameOver`(62) wire snapshot (reason + winner + side records), previously discarded |
| `GameOverPanelInfo` | `.../MonoGame/Rendering/GameOverPanelInfo.cs` | Pure, headless-testable text resolution (won/lost/draw + winner name + reason) |
| `GameOverScreenRenderer` | `.../MonoGame/Rendering/GameOverScreenRenderer.cs` | Draws the large centred high-contrast panel |
| `Game1` wiring | `.../MonoGame/Game1.cs` | Instantiates renderer; draws it top-most once `world.GameOverResult` is set |
| Tests | `tests/.../GameOverPanelInfoTests.cs` | 6 pure tests |

Faithfulness to native `WinTrek/gameoverscreen.cpp`:
- **Title** = native's three-way at `:351-362` — "You are victorious!" if the
  local player's side is `iSideWinner`, "You have been defeated." if some other
  real side won, and a spelled-out draw title otherwise (this port has no
  client won/lost accumulator, so the draw case is explicit rather than `""`).
- **Reason** = native's `m_ptextReason = CensorBadWords(GetGameoverMessage())`
  at `:689` — the real `szGameoverMessage` string the server put on the wire.
- **Winner line** = the wire's own `rgSides[iSideWinner].sideName`, the same
  data native's `SidePainter` (`:245-255`) renders in its side-stats list —
  promoted to a banner to satisfy this task's "err toward obvious/unmissable".
- Native's countdown timer (`m_ptextCountdown`, `:640-663`), chat pane
  (`m_pchatPane`/`m_peditPane`/`m_pbuttonSend`) and KGJV save-stats button
  (`m_pbuttonSave`) are **named follow-ups, not built** (see below).

Styling mirrors the established `HelpOverlayRenderer`/`StarmapRenderer` panel
family: translucent dark panel + bright teal border + gold title banner (gold =
the starmap's "you are here" highlight), drawn **top-most** over every other
overlay so it is unmissable. Text is word-wrapped to the panel width (a
defensive guarantee no future reason string clips) and every drawn string is
scrubbed to the HUD fonts' ASCII repertoire.

## Behaviour / scope

The screen appears only once the real `GAME_OVER`(62) wire message arrives
(`world.GameOverResult != null`), i.e. only on a genuine server-declared
mission end. **No change** to when/why a game ends — P178/P191's win logic is
untouched; this merely displays an event that already fires server-side.

## Verification

- **Live screenshot (required)** — `.verify_p262/gameover_victory_real.png`
  (also copied to `/tmp/p262verify/`): the REAL `GameOverScreenRenderer` fed
  the REAL `GameOverResult` of a REAL in-process server + REAL
  `ConnectionSession` (the P245 full-server harness pattern) after a REAL
  win-by-elimination. The panel displays the actual wire data:
  ```
  YOU ARE VICTORIOUS!
  WINNER:  Alpha          <- rgSides[iSideWinner].sideName, real wire
  REASON
  Alpha won by outlasting all other sides.   <- szGameoverMessage, real wire
  -- MISSION CONCLUDED --
  ```
  Confirmed by vision check (LLM vision reads the PNG). `gameover_defeat.png`
  and `gameover_draw.png` exercise the same real resolver+renderer across the
  other two outcomes (defeat shows "WINNER:" and the conquest reason; draw has
  no winner line). `no_panel.png` = the pre-P262 sound-only frame for contrast.
- **Build** — whole-solution `dotnet build AllegianceCore.slnx -c Debug` =
  0 warnings, 0 errors.
- **Tests** — see the final summary in the P262 task; the only pre-existing
  failure is the documented `LiveTurretGunnerBindingTests` timing flake.

## Out of scope — named follow-ups (not built)

- **Save-stats button** — native `m_pbuttonSave` (`gameoverscreen.cpp:24,345`):
  would need persisted per-player endgame scores this port has never built.
- **Full chat pane inside the screen** — native `m_pchatPane`/`m_peditPane`/
  `m_pbuttonSend`.
- **Countdown timer** — native `m_ptextCountdown` (`:640-663`).
- **Per-side/player endgame stats tables** — native's `PlayerPainter`/
  `SidePainter` stat grids (scores/kills/time) require the `GAME_OVER_PLAYERS`(63)
  data (`PlayerEndgameInfo`), which stays out of scope (P194 defines a minimal
  record but the port tracks no per-player score state to fill a rich grid).
- Any change to game-over *logic* — display only.
- Native reference source (`~/Development/Allegiance`) — untouched.

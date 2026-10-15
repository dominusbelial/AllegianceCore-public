# P388 — debug mode: don't end the mission just because no player is connected

## Why this task exists

Live-tested this session (2026-09-26), right after P387 landed and fixed the
mission-freeze latency bug. The moment the connected client disconnected,
the server immediately declared game over:

```
[conn] peer 1 left
[gameover] mission #0 -> Stage Over, iSideWinner=NA(draw), 0 peer(s)
```

Nothing happened afterward — both autonomous NPC miners just sat frozen
again, this time because the mission itself had ended, not because of
P387's bug. This directly defeats debug mode's whole stated purpose
(P379/P381/P382/P387: an **unattended** autonomous test arena that should
keep running with zero players connected).

## Confirmed real mechanism (not a bug — a real, correct, native-faithful feature that just needs a debug-mode exception)

- **`CheckForVictoryByInactiveSides`** (`Server.Lobby.cs:54-82`) — a real,
  correct port of native's win-by-elimination
  (`fsmission.cpp:5371-5427`): counts how many sides still have at least
  one seated **player** (`_peerShips` — NPCs never count, matching native's
  `HasPlayers`' `ISPLAYER && !IsGhost` check). ≤1 side with players →
  declares a winner (or a draw if zero) via `DeclareGameOver`
  (`Server.Lobby.cs:98-`), which flips `mission.Stage` to
  `MissionStage.Over` and broadcasts `GAME_OVER(62)`.
- **Called from exactly one place**: `Server.Lifecycle.cs:901`, inside the
  real peer-disconnect handler, right after the departing peer's ship is
  retired. This is the correct, intended behavior for a normal production
  game — if every real player leaves, the match should end. It is simply
  the wrong behavior for debug mode's unattended arena, which has *zero*
  players by design once everyone disconnects (or even from boot, per
  P387).
- **`DeclareGameOver` itself is shared** with the P191 conquest/territory
  win path (`OnVictoryByStationsComputed`) — a real combat/conquest outcome
  should probably still be able to end a debug-mode mission if one
  genuinely occurs. Don't touch `DeclareGameOver` itself; gate only the
  player-population-based elimination check.

## Scope

1. In `CheckForVictoryByInactiveSides` (`Server.Lobby.cs:54-57`), add a
   debug-mode early return alongside the existing idempotency guard:
   ```csharp
   if (mission.Stage == MissionStage.Over)
       return;                                  // idempotent: already decided
   if (_config.LiveSettings?.DebugMode == true)
       return;                                  // debug mode: unattended arena, never ends on empty lobby
   ```
   (match the existing `_config.LiveSettings?.DebugMode == true` access
   pattern already used at `Server.Lifecycle.cs:475` and `:664` — don't
   invent a second convention.) Log plainly when this skip actually
   prevents an outcome that would otherwise have fired, matching this
   port's existing `[boot]`/`[npc]`-style tagged logging, e.g.
   `[debugmode] skipping win-by-elimination check (0 players is expected in the unattended test arena)`.
2. Confirm the fix by reproducing exactly this session's sequence: boot a
   debug-mode server, connect a real client, disconnect it, and confirm the
   mission stays `Started`/`Starting` (not `Over`) and the autonomous NPC
   miners keep cycling (undock/warp/mine/sell/redock) with **zero** clients
   connected, for at least 2 full mine-sell cycles observed live.
3. Confirm production (debugMode off) is completely unaffected — a normal
   game still correctly declares game-over/draw when the last real player
   disconnects, exactly as before this change.

## Explicitly out of scope

- `DeclareGameOver` itself, and the P191 conquest/territory win path that
  shares it — a genuine combat/conquest outcome should still be able to end
  a debug-mode mission; this task only suppresses the *empty-lobby*
  elimination check, nothing else.
- Any other win-condition code path.
- P368-P387 — unrelated, don't touch.

## Verification — real proof, not just build/test

- **Live proof, required**: exactly the repro above — boot debug mode,
  connect then disconnect a real client, confirm the mission does NOT end
  and both autonomous miners complete at least 2 full cycles each with zero
  clients connected afterward. Also confirm a normal (debugMode off) game
  still ends correctly when its last player leaves — a real regression
  check, not just "the flag exists."
- Whole-solution build: 0 warnings, 0 errors.
- Full test suite, run at least 2x: only the pre-existing documented
  live-wire timing flakes expected.

## Deliverables

1. The fix, committed on `master` (not pushed — Domingo pushes).
2. Report doc `P388_DEBUG_MODE_NO_GAMEOVER_ON_EMPTY.md` in the repo root:
   the mechanism, the fix, and both live verifications (debug-mode
   survives disconnect; production still ends correctly).

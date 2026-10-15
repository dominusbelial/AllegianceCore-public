# P548 — "Stage Over" fires on every peer drop, even in a lobby or a fresh mission: add native's stage gate to the inactive-side victory check. Also: auto-assigned players never become team leader

## Why this task exists

**Live (Domingo, 221):** "Stage Over" fires **4×** when peers drop. The P533 audit
(`docs/reports/P533_SESSION_AUDIT.md`) wrongly rated "game-over by peer
departure" as MATCH. Claude's spot-check (2026-10-05, wiki
`allegiance-p1-p200-parity-audit`) found the root cause, and Claude
re-verified it:

- **Port:** `CheckForVictoryByInactiveSides(Mission)`
  (`src/Allegiance.Server/Server.Lobby.cs:54-57`) returns early **only when
  `mission.Stage == MissionStage.Over`**. It runs on **every** disconnect
  (`Server.Lifecycle.cs:~1199-1230`, called last at `:~1230`). So a drop that
  leaves ≤ 1 side with players declares game over **in NotStarted (lobby) too**.
  `DeclareGameOver` (`Server.Lobby.cs:~339-375`) then rotates and migrates peers
  into a new NotStarted mission (`:~403-461`), which the **next** drop ends
  again: one "Stage Over" per drop.
- **Native:** a side is only deactivated, which is what leads to the victory
  check, when
  `bool fDeactivate = (STAGE_NOTSTARTED != m_misdef.stage) && !HasPlayers(psideOld, false);`
  (`FedSrv/fsmission.cpp:1147`), then `DeactivateSide` → … →
  `CheckForVictoryByInactiveSides(bAllSidesInactive)` (`:~5381`). Read the whole
  chain (`DeactivateSide`, `:5375-5400`, including the "if the game has not
  started yet, start it so a side can win" branch) and port the gating
  **exactly**: stage ≠ NotStarted, AND the departing player's side now has
  no players.
- Keep P388's debug-mode exemption (empty debug arena is the steady state).

**Second, related bug (spot-check, "possible cause", verify first):** the
first player onto an empty side becomes team leader (`TryJoinSide`,
`Server.Lobby.cs:2238-2270` = native `fsmission.cpp:601`), but the
**auto-assign path** (`Server.Lobby.cs:~2617-2626` → `Server.cs:~753`) sets
`SideId` **without going through `TryJoinSide`**, so auto-assigned humans are
never elected leader. Meanwhile the MCP probe, which picks a side
explicitly, does become leader (a live observation: "the probe client takes
team leadership"). Verify; if real, route auto-assign through the same join
path as native's auto-assign (cite it).

## Scope

1. Port native's stage + side-empty gate for peer-departure deactivation
   and the victory check. No game over from a drop in NotStarted. A drop in
   Starting/Started deactivates only a side left empty, per native.
2. Make sure `DeclareGameOver` can't cascade: one game over per real
   decision.
3. The auto-assign leader fix, if verified.

## Explicitly out of scope

- Mission-scoped chat and payday with multiple configured games (separate,
  multi-game only).
- `nMaxPlayersPerTeam` and `CHAT_ALLIES` (later).

## Proof

- Failing-first tests: (a) in NotStarted, two peers join different sides
  and one drops: **no GAME_OVER**; (b) Started, the last player of side B
  drops: side B is deactivated, exactly **one** GAME_OVER with the native
  reason/winner; (c) a sequence of 4 drops after a game over produces **no**
  further GAME_OVERs (the ×4 regression); (d) an auto-assigned first player
  on an empty side is leader.
- **Live loopback:** start a server, connect 4 clients, drop them one by
  one in the lobby, then in a started game. Quote the server log: count
  `[gameover]`/"Stage Over" lines (expected 0 in the lobby, 1 in game).
- Report `docs/reports/P548_GAME_OVER_STAGE_GATE.md`, plus a dated
  correction note in P533's report.

## Verification (common)

- **Failing-first tests** for every fix: the test fails on today's code and passes after.
- **Native fidelity:** cite native file:line for every rule you port. If you find the
  spot-check's reading wrong, prove it with native code and say so.
- Whole-solution build, 0 warnings and 0 errors. Full suite via `scripts/run-tests.sh`
  (P471), 3 consecutive runs **on the final code**. Cite `docs/TEST_FLAKES.md`.
  **Report only numbers you actually measured. Don't re-run a failing suite until it
  comes up clean;** report each run as it happened.
- ⚠️ **Commit as soon as the code and tests are done, BEFORE the 3× suite**, then amend
  with suite numbers. **Include every new file** (`git status --untracked-files=all`).
  Confirm with `git log -1` and a clean `git status`.
- Committed on **`master`**, not pushed. Check with `git branch --show-current`.

## ⚠️ Session rule: never end a turn while waiting

This session runs in one-shot mode: **your session ends the moment you finish a message.** Run long commands (the 3× `scripts/run-tests.sh` suite, live proofs, builds) **in the foreground**, blocking, one after another. **Never** start them in the background and then reply "waiting for it to finish". Two earlier sessions (P529, P542) did exactly that: the session ended mid-suite, the background processes were killed, and the suite results were never recorded. If a command might exceed a tool timeout, run each suite run as its own foreground call and record its numbers before starting the next.

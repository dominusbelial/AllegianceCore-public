# P583 — native player scoring: port `PlayerScoreObject` counters and `CalculateScore` (rescues, kills by type, base kills/captures, spotting, techs, artifacts, flags, repairs, win ×2, rank ratio), feed them from the existing events, and show them where native does

## Why this task exists

Domingo: "saving pods gives some kind of bonus, right?" Yes: in native, the
rescuer's `AddRescue()` (`FedSrv/FedSrv.CPP:13033-13051`, `RescueShipEvent`)
is worth **5 points** (`c_pointsRescues`). The port's rescue path skips it:
`Server.Docking.cs:~645-656` says "Score / Steam: N/A, skipped … this port
has **no score system**". `src/Allegiance.Server/PlayerScore.cs` now exists
but implements **only Kills/Deaths/Ejections** (its own doc: "implements none
of those counters except the three P215's combat slice populates"; "the full
`CalculateScore` formula is Stage 2").

**Native formula** (`Igc/common.cpp:2666-2694`, `PlayerScoreObject::CalculateScore`):
`kMax = dtPlayed / (15 min)`;
`score = warpsSpotted·PointsWarp + asteroidsSpotted·PointsAsteroid +
techsRecovered·PointsTech + Σ_{miner,builder,layer,carrier,baseKill,baseCapture}
(c·kMax·Points / (c + kMax)) + playerKills·PointsPlayer + rescues·PointsRescues +
artifacts·PointsArtifacts + flags·PointsFlags + min(highValueTargetsSpotted,5)·4 +
repair·5`; `×2 if win`; `× rankRatio`. Constants (`Igc/missionigc.cpp:~2705-2720`,
loaded as `c_fcidPoints*` at `:~2769`, overridable by the core): asteroid 1,
tech 5, miner 10, builder 20, layer 10, carrier 10, player 10, base kill 25,
base capture 25, flags 10, artifacts 10, rescues 5, warp 2 (PCore15 comment).
Read the real values from the loaded core's float constants, not hard-coded.

## Scope

1. Port the `PlayerScoreObject` counters and `CalculateScore` 1:1
   (including `m_dtPlayed`, the diminishing-returns `kMax` terms, win ×2, and
   `m_rankRatio`: find where native sets it), with the core's constants.
2. **Feed every counter from the port's existing events** (find native's call
   sites for each `Add*`): kills by victim class (player / miner / builder /
   layer / carrier), base kills and captures, rescues (`Server.PerformRescue`),
   warp/asteroid/high-value spotting (the eye/visibility code), techs recovered
   (treasure pickups), artifacts and flags, repairs. Where an event doesn't
   exist in the port yet, list it as a gap with file:line; don't invent it.
3. **Show it where native does:** the in-game score/roster columns and the
   end-of-game screen (`GAME_OVER` already exists: `Wire/GameOver.cs`; native
   `gameoverscreen.cpp` fields incl. rescues/ejections). Check native's wire
   for per-player score updates during the game, and port them if they exist.
4. Bots count like players (MCP showcases will show scores).

## Out of scope

Persistent stats DB/rank progression across games, Steam achievements.

## Proof

- Failing-first unit tests for `CalculateScore` against hand-computed
  native values (incl. the `kMax` terms, win ×2), and one test per event
  feeding its counter (rescue → +1 rescue → +5 points).
- Live: a showcase run with a pod rescue, kills of different classes and a game
  end → the scoreboard/end screen shows the counters; screenshots + server log.
- Report `docs/reports/P583_NATIVE_SCORING.md`.

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
- **Visual changes: the proof must include screenshots from the REAL client window**
  (the backbuffer, e.g. via `ALLEGIANCE_AUTOSHOT`, window 2560×1440). `--capture` /
  offscreen renders alone don't count (they hid the P547 and P562 regressions).
- Committed on **`master`**, not pushed. Check with `git branch --show-current`.

## ⚠️ Session rule: never end a turn while waiting

This session runs in one-shot mode: **your session ends the moment you finish a message.** Run long commands (the 3× `scripts/run-tests.sh` suite, live proofs, builds) **in the foreground**, blocking, one after another. **Never** start them in the background and then reply "waiting for it to finish". Two earlier sessions (P529, P542) did exactly that: the session ended mid-suite, the background processes were killed, and the suite results were never recorded. If a command might exceed a tool timeout, run each suite run as its own foreground call and record its numbers before starting the next.

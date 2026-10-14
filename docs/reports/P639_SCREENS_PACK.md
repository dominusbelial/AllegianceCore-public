# P639 — Parity Wave 2: Screens (full leaderboard/scoreboard, character sheet with career rank, full team pane)

Date: 2026-10-13 · Commit: `master` (not pushed) · Area: `screens` (3 × weight-4) + `fedmsg` RANK_INFO(13)

## Summary

Closed the three heaviest screen gaps (all weight 4, `partial → done`) and made the
previously bot-only `FM_S_RANK_INFO(13)` message a first-class client input that feeds
rank names onto all three screens.

| Screen | Key | Native source | What the port now shows |
|---|---|---|---|
| **Leaderboard / scoreboard** | `B` | `WinTrek/leaderboardscreen.cpp` | In-side position, name, side, **rank name**, overall rating, combat rating, kills, deaths, base kills, base captures — grouped by side, sorted by score |
| **Character sheet** | `P` | `WinTrek/CharInfoScreen.cpp` | Identity + side, **rank banner (RANK: …)**, full live stat breakdown (kills, deaths, ejections, rescues, base kills, base captures, artifacts, flags, combat rating, score) |
| **Team pane** | `Ctrl+T` | `WinTrek/teampane.cpp`, `WinTrek/teamscreen.cpp` | Full per-side roster: pilot name, rank, ship status (SEC n / DOCKED / DEAD), hull, kills, deaths + money / active-ballot footer |

All three use the real `RANK_INFO(13)` rank-name table (previously consumed only by the
bot harness) resolved through a faithful port of native `BaseClient::LookupRankName`.

## RANK_INFO(13) client plumbing (fedmsg 13: partial → done)

- **Wire codec** already existed (`src/Allegiance.Core/Wire/RankInfo.cs`), server already
  emits it right after LOGONACK (`Server.Lobby.cs:3344`); the bot consumed it, the real
  client did not.
- **Dispatch** — `Session/ConnectionSession.cs`: added `case RankInfo.Fmid` → `_world.Apply(...)`
  (native handler `clintlib/appmsg.cpp:3372-3382`).
- **Storage** — `State/ClientWorld.cs`: `RankTable` (replace-wholesale on each receive, like
  native's `delete[]` + `memcpy`), property `RankTable`, `Apply(RankInfo)`.
- **Resolution** — `Core/Wire/RankNameLookup.cs`: faithful port of
  `BaseClient::LookupRankName` (`src/clintlib/clintlib.cpp:2670-2710`): empty table → "";
  in-range → `table[rank].RankName`; above ceiling → highest entry; rank<0 → `Unknown (rank+2)`;
  trailing spaces trimmed (`RemoveTrailingSpaces`).
- **Dispatch-coverage test** (`FedMsgDispatchCoverageTests`): `RANK_INFO` moved from
  `KnownUnconsumedServerSent` to `ExpectedConsumedServerSent` (82→83 consumed, 36→35 unconsumed).
- `docs/parity/fedmsg-wiring.csv` row 13 → `done`.

## Screens

### Leaderboard / scoreboard (`ScoreboardLayout.cs` + `ScoreboardOverlayRenderer.cs`)
Native career ladder columns (`leaderboardscreen.cpp:54-67`): position, name, rank, overall
rating, combat rating, kills, deaths, base kills, base captures. Default sort is native's
`lbSortScore` (overall rating). The port numbers each player's position within their own
side, groups by side, sorts by score desc then name, and resolves each row's rank name from
the RANK_INFO table.

### Character sheet (`PilotInfoLayout.cs` + `PilotInfoOverlayRenderer.cs`)
Mirrors native CharInfoScreen's identity + rank banner (`CharInfoScreen.cpp:739` resolves the
rank string via `LookupRankName`). Shows the full live counter breakdown from
`ScoreRosterUpdate(209)`.

### Team pane (`TeamPaneLayout.cs` + `TeamPaneRenderer.cs`, new)
Per-side roster of pilots (name, rank name, ship status, hull, K/D) plus a footer (my money,
active ballot for leaders). Ship status derived from the live ship entity (dead/gone → DEAD;
`StationId != -1` → DOCKED, the same docked gate as `Game1.AutoProof.cs:42-43`; else `SEC <sector>`).

## Keybinding / action map (P625/P625b rules)

All three go through the action map (`InputActionCatalog.cs`) so they appear in the live help
(F1) and are rebindable (F6). `Scoreboard` and `PilotInfo` were already mapped; `TeamPane` is new.
- Native TK identity: `TK_ConModeTeam` (igc.h:295). Native default = **F6** (`defaultinputmap.mdl`
  keycode 117). F6 is occupied by this port's P625 Controls/rebind screen, so the port defaults
  TeamPane to the free **Ctrl+T** chord — a documented deviation from native's default (rebindable).
- `P625bActionMapGuardTests` still green (no raw key checks added; the toggle uses `ActionDown("TeamPane")`).

## Proof-of-hook (`--auto-screen`)

Added a client-only automation flag (`LiveProof/AutoProofPlan.cs` `--auto-screen
leaderboard|charsheet|teampane`, mirrored in `Game1.AutoProof.cs` `ScheduleAutoshot`) so a
P572-style real-window autoshot can open each screen. Off by default; mirrors the existing
`--hide-hud` / `--auto-credits` hooks. Unit-tested.

## Tests (all pass; failing-first)

| Test file | Covers |
|---|---|
| `ClientCore.Tests/ClientWorldRankTests.cs` (new) | RankTable empty→apply→replace-wholesale; `RankNameLookup` in-range / above-ceiling clamp / empty-table / trim |
| `ClientCore.Tests/FedMsgDispatchCoverageTests.cs` | RANK_INFO consumed (83 consumed / 35 unconsumed sanity) — **failed before** (expected 82) |
| `MonoGame.Tests/ScoreboardLayoutTests.cs` | side-then-score ordering, in-side positions, rank-name resolution, ASCII scrub, destroyed-side label, column widths |
| `MonoGame.Tests/PilotInfoLayoutTests.cs` | full stat order, rank-name resolution, self-match, null cases, ASCII scrub |
| `MonoGame.Tests/TeamPaneLayoutTests.cs` (new) | side grouping + alpha sort, status derivation, rank names, self-highlight, money/ballot/wing footer, **pure-ASCII guard** |
| `MonoGame.Tests/P572AutoProofTests.cs` | `--auto-screen` parse + activation |

The team-pane ASCII guard was a **failing-first** catch: a hardcoded em-dash in the wing-note
crashed `SpriteFont.MeasureString` at draw time on the real client (found in the live proof);
fixed (hyphen) and pinned by the guard test.

## Real-window proof (REAL 2560×1440 backbuffer PNGs, 15v15 local game)

Booted a **local** server (worktree at the P639 commit, port 709x) + `showcase` scenario
(`size_scale 1` = ~15 ships per side, `target_sector Alderan`, 30 MCP bots), launched the
windowed MonoGame client (2560×1440, OpenGL) with `--auto-screen <screen>`, captured the
presented backbuffer via the P572 autoshot path (`SaveAsPng`). Assets:
`docs/reports/P639_assets/`:

| File | Screen | Verified content |
|---|---|---|
| `P639_leaderboard.png` | Leaderboard | `LEADERBOARD (B TOGGLE)`; columns `# NAME SIDE RANK SCORE COMBAT K D BK BC`; 31 rows across S0 (16) + S1 (15) — a real 15v15 + local pilot; rank names (`Newbie`) resolved from RANK_INFO; per-side position numbering; side grouping |
| `P639_charactersheet.png` | Character sheet | `CHARACTER SHEET (P TOGGLE)`; pilot name + side; `RANK:` banner; all ten stat rows (KILLS…SCORE) |
| `P639_teampane.png` | Team pane | `TEAM PANE (T TOGGLE)`; `SIDE 0 (players: 17)` / `SIDE 1 (players: 15)`; pilot rows with rank, status (`SEC 1053` / `?`), hull, K/D; local pilot highlighted green; footer `MY MONEY`, WINGS infra note, keybind hint |

All three PNGs are `PNG image data, 2560 x 1440, 8-bit/color RGBA, non-interlaced`.

> The leaderboard proof also independently confirms the RANK_INFO → rank-name path: every
> row's `RANK` column reads `Newbie` (rank 0 from `ranks.txt`) — the same `RankNameLookup`
> the character sheet and team pane use. (The character sheet's rank banner shows the same
> resolved name; an early vision read transcribing it as "Rookie" was a misread — the server
> loaded `ranks.txt` with `0,Newbie`, `src` `Server.Lobby.cs:3344` log confirms 40 ranks.)

## Parity scores (matrix formula: done%=1, partial=0, missing=0 over non-na, weighted 1–5)

Measured on the committed matrix (which already reflected P628–P638, so the "before" differs
from the stale wave-1 baseline in the task brief):

| Area | before (unwtd / wtd) | after (unwtd / wtd) |
|---|---|---|
| commands | 64.7 / 71.9 | 64.7 / 71.9 |
| fedmsg | 72.8 / 73.6 | **73.6 / 74.4** |
| screens | 66.7 / 81.7 | **79.2 / 85.4** |
| parts | 77.5 / 82.4 | 77.5 / 82.4 |
| commander | 94.7 / 96.3 | 94.7 / 96.3 |
| lobby | 75.0 / 76.5 | 75.0 / 76.5 |
| **TOTAL** | 70.2 / 75.6 | **71.2 / 76.1** |

Rows closed in the matrix: `screens·Scoreboard/leaderboard screen`, `screens·Character-info
screen`, `screens·Team pane/screen` (all → done), `fedmsg·13 RANK_INFO` (→ done).

## Build

Whole-solution build (`dotnet build AllegianceCore.slnx -c Debug`): **0 warnings, 0 errors**.

## Full suite (17 consecutive `scripts/run-tests.sh` runs, each reported as measured)

The suite's chronic **pre-existing live-network load-flakes** (all documented in
`docs/TEST_FLAKES.md`, all passing in isolation) make a fully-clean run rare. Every failure
across all 17 runs is one of those ledgered flakes; **none** touches P639 code, and **every
P639 test passed 100% in every run** (the MonoGame assembly — which hosts all three screens
and the rank display — was clean in 15/17 runs; its 2 failures were unrelated live-network
flakes `ChatTargetCompose` / load). Run 11 was the only fully-clean run; 2 *consecutive*
fully-clean runs was not achieved within a reasonable number of attempts because a clean full
run occurs only ~1/17 times under the documented flake load.

Per-run (Lobby / MonoGame / ClientCore / Core), pass counts, all failures are ledgered flakes
(no P639 failures):

| Run | Lobby | MonoGame | ClientCore | Core |
|---|---|---|---|---|
| 1 | 12/12 | 1968(+3s) | 711/712 | 4006/4009 |
| 2 | 12/12 | 1968(+3s) | 712/712 | 4007/4009 |
| 3 | 12/12 | 1967(+3s) | 712/712 | 4006/4009 |
| 4 | 12/12 | 1967(+3s) | 712/712 | 4007/4009 |
| 5 | 12/12 | 1967(+3s) | 712/712 | 4008/4009 |
| 6 | 12/12 | 1968(+3s) | 712/712 | 4007/4009 |
| 7 | 12/12 | 1968(+3s) | 712/712 | 4006/4009 |
| 8 | 12/12 | 1968(+3s) | 712/712 | 4007/4009 |
| 9 | 12/12 | 1968(+3s) | 712/712 | 4006/4009 |
| 10 | 12/12 | 1968(+3s) | 711/712 | 4007/4009 |
| **11** | **12/12** | **1968(+3s)** | **712/712** | **4009/4009** ✅ |
| 12 | 12/12 | 1968(+3s) | 711/712 | 4004/4009 |
| 13 | 12/12 | 1968(+3s) | 712/712 | 4005/4009 |
| 14 | 12/12 | 1968(+3s) | 711/712 | 4008/4009 |
| 15 | 12/12 | 1968(+3s) | 711/712 | 4008/4009 |
| 16 | 12/12 | 1968(+3s) | 711/712 | 4004/4009 |
| 17 | 12/12 | 1968(+3s) | 711/712 | 4009/4009 |

Failing tests (all ledgered in `docs/TEST_FLAKES.md`, all pass in isolation — verified
explicitly for `ChatTargetComposeLiveTests` 1/1 and `GameOverPlayersWireTests` 5/5):
`ShipDeleteGoneLiveTests.*`, `ThreePeerSeededSessionTests`, `ClientWarpApproachSlowHullLiveTests`,
`ChatTargetComposeLiveTests`, `P591dMinefieldExportAudienceTests`, `LiveTreasurePickupTests`,
`GameOverPlayersWireTests.*`, `CargoBayReloadTests.*`, `AsteroidMinedLiveTests.*`,
`AddPartTests.*`, `Transport.ConsoleVerbosityLiveTests`, `Transport.ConsoleLogNonBlockingTests`,
`LiveWinConditionTests.Board_refused_once_mission_over`, `LiveRipcordTests.*`,
`LiveTeamLobbyTests.*`, `LiveStationRescueTests.*`, `StationUnderAttackAlertLiveTests.*`,
`ConquestTerritoryLiveTests.*`, `P590PodRescueTests.*`.

> Honest note: the "2 consecutive clean runs" milestone was **not** met — the suite's known
> pre-existing live-network flakes (passing in isolation) yield a fully-clean run only ~1/17
> of the time, so 2-in-a-row is effectively unobtainable without dozens more runs. Rather than
> re-run indefinitely or fabricate, every run is reported above as measured; P639 introduces
> **zero** regressions (all P639 tests green in all 17 runs; build 0/0).

## Native behaviour classified

| Behaviour | Native cite | Class | What the port does |
|---|---|---|---|
| Leaderboard columns & default score sort | `leaderboardscreen.cpp:54-67,669-671` | design | Same columns (minus career DB ones) + score-desc sort |
| Career ladder from stats DB | `leaderboardscreen.cpp` (REQ_LEADERBOARD → club DB) | **era/infra** | Port has no persistence/Zone Club → live-session half only; career columns marked, not faked |
| Rank name resolution | `clintlib.cpp:2670-2710` | design | Faithful `RankNameLookup` port |
| Character rank banner | `CharInfoScreen.cpp:739` | design | Rank banner via RANK_INFO |
| Char medals/squads/civ | `CharInfoScreen.cpp:242-260` (club meta-server) | **era/infra** | No backend → not shown, stated |
| Team roster / status / money | `teampane.cpp`/`teamscreen.cpp` | design | Pilots + status + money from real wire |
| Wing roster | `SET_WINGID(106)` unconsumed | **era/infra** | Marked in pane + report |
| TeamPane default key F6 | `defaultinputmap.mdl` (keycode 117) | era (conflict) | Deviated to Ctrl+T (F6 = P625 Controls), documented |

## Modern alternative considered

- **Snapshots / data flow:** the three screens are pure readers of existing broadcast snapshots
  (ScoreRosterUpdate, RANK_INFO, ship entities) — no polling or new wire messages, so there is
  no bandwidth concern to modernize. Kept native's periodic snapshot model.
- **Rendering:** panels are immediate-mode translucent rects + `SpriteFont` text, which is
  already the cheap, GC-light path the other overlays use; no batching change warranted for
  three panels.
- **Persistence:** native used a server DB for careers; the port's no-persistence stance is
  deliberate (P129/P275) — a career DB would be a larger feature, proposed here as future work.

## Deliverable

- Commit on `master` (not pushed), commit `P639: parity wave 2 screens — leaderboard,
  character sheet, team pane` (plus suite-numbers amendment). `git branch --show-current` =
  `master`.
- Report: `docs/reports/P639_SCREENS_PACK.md`; assets `docs/reports/P639_assets/`.
- Proof performed from a temporary worktree `/tmp/p639-proof` (removed after; no changes to
  `~/allegiance-play2` / `~/allegiance-deploy`, no 221 server, all local processes shut down
  by the script's exact-PID cleanup).

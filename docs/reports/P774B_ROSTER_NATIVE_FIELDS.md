# P774b — show the roster fields that have no native in-mission source the native way

Base: P774's WIP commit (`ad80a929`, roster derived from PLAYERINFO(10) + KILL_SHIP(105), fmids 209/219
retired) on master `bff2b905`. Scope: display only — the in-mission scoreboard and pilot-info screens
showed seven fields that native never shows in mission; this task drops them and shrinks
`ScoreRosterEntry` to the fields with a native in-mission source. No server, Core or wire change.

## 1. Native rule per field (every cite re-opened and verified)

| field | native in-mission source | native display | verdict |
|---|---|---|---|
| kills | the ship's counter — `MissionKills()` reads `m_pship->GetKills()` (`clintlib/clintlib.h:346-347`) | in mission | **keep** |
| deaths | `MissionDeaths()` (`clintlib.h:347-348`) | in mission | **keep** |
| ejections | `MissionEjections()` (`clintlib.h:348-349`) | in mission | **keep** |
| rank | `Rank()` = `GetPersistScore().GetRank()` (`clintlib.h:359-361`) | in mission | **keep** |
| score | none in mission; `GAME_OVER_PLAYERS` row shows `scoring.GetRecentScore()` (`WinTrek/gameoverscreen.cpp:158`) | game-over only | **drop in mission** |
| base kills | none in mission; `GetRecentBaseKills()` at game over (`gameoverscreen.cpp:146`) | game-over only | **drop in mission** |
| base captures | none in mission; `GetRecentBaseCaptures()` at game over (`gameoverscreen.cpp:152`) | game-over only | **drop in mission** |
| combat rating | none — `//m_fCombatRating = ppso->GetPersist().GetCombatRating();` is commented out in the game-over row's `Set` (`Igc/igc.h:5981`) | never | **drop everywhere** |
| rescues | none — `FM_S_PLAYER_RESCUED` only posts text + a sound (`clintlib/appmsg.cpp:309-340`); game-over `//m_cRescues` commented out (`igc.h:6001`) | never | **drop everywhere** |
| artifacts, flags | none — `FM_S_GAIN_FLAG` sets the carried flag (`pship->SetFlag(...)`) and posts text, no counter (`appmsg.cpp:3113-3150`); game-over `//m_cFlags`/`//m_cArtifacts` commented out (`igc.h:5999-6000`) | never | **drop everywhere** |

Native `CharInfoScreen`'s full stat breakdown (score, base captures/kills, wins/losses, minutes,
combat rating, …) is the Zone Club **career database** — `REQ_CHARACTER_INFO` →
`trekClient.m_fmClub` (`WinTrek/CharInfoScreen.cpp:242-260`) rendered in
`OnCivChange` (`CharInfoScreen.cpp:717-774`) — which this port does not have. All cites above were
re-opened in `~/Development/Allegiance/src/` and say what this table claims.

## 2. What each screen drops / keeps

- **Scoreboard** (`ScoreboardLayout.cs`, `ScoreboardOverlayRenderer.cs`): dropped the SCORE, COMBAT,
  BK and BC columns. Kept #, NAME, SIDE, RANK, K, D (header row is now exactly
  `# NAME SIDE RANK K D`, `Row.ColumnCount = 6`, drawn from `Row.HeaderCells`). Sort changed from
  side-asc/score-desc to **side asc → kills desc → deaths asc → name (ordinal)**; in-side position
  numbering unchanged. The game-over screen (`GameOverScreenRenderer.cs`, P752 — a different record
  type fed by GAME_OVER_PLAYERS) is untouched and keeps showing score and base kills/captures.
- **Pilot info** (`PilotInfoLayout.cs`, `PilotInfoOverlayRenderer.cs`): the sheet now carries exactly
  RANK (resolved from RANK_INFO(13), drawn as the banner), KILLS, DEATHS and EJECTIONS. Dropped
  RESCUES, BASE KILLS, BASE CAPTURES, ARTIFACTS, FLAGS, COMBAT RATING and SCORE (was an 11-row stat
  list; now 4).
- **`ScoreRosterEntry`** shrunk from 13 fields to
  `(CharacterName, SideId, Kills, Deaths, Ejections, Rank)`; doc comment updated (the "until P774b"
  clause is gone). Every constructor/reader fixed; the game-over player records are a different type
  and untouched.
- **No change** to the team pane, commander side headers or funding (they read only kills, deaths,
  rank or `IsHuman`). No server, Core or wire change at all.

## 3. Tests

Each new test failed at runtime on the unfixed code (temp harness
`P774bTempFailingTests`, deleted after the fix; raw log
`~/allegiance-evidence/P774B/p774b-failing-tests-before-fix.log`):

| test (final home) | failure on unfixed code |
|---|---|
| `ScoreboardLayoutTests.In_mission_header_row_is_position_name_side_rank_k_d_only` | header constants were `["#","BC","BK","COMBAT","D",…10 entries]`; `ColumnCount` was 10 |
| `ScoreboardLayoutTests.Build_orders_by_side_then_kills_desc_deaths_asc_name` | order was name-ordinal (score all 0): `["Ann","Bob","Cid","Zed"]` instead of `["Cid","Ann","Bob","Zed"]` |
| `PilotInfoLayoutTests.Build_returns_local_players_full_stats_in_order` (labels assertion) | labels were `["RANK NAME","KILLS","DEATHS","EJECTIONS","RESCUES",…11 entries]` |

Rewritten (not deleted) to pin kept behaviour on the shrunken record:
`ScoreboardLayoutTests` (E-helper, ordering test, `ColumnWidths_accommodate_longest_cells_and_headers`),
`PilotInfoLayoutTests` (E-helper, both extraction tests), `TeamPaneLayoutTests` (E-helper),
`P642ConsoleZoomTests.Mini_team_uses_only_own_side_and_preserves_self_highlight_and_money`,
`P761TeamPaneSortTests` (3 constructor sites),
`P774DerivedScoreRosterTests.Roster_is_built_from_PLAYERINFO_humans_only` (the seven zero-field
asserts replaced by the six-field contract comment). `ScoreRosterLiveTests` needed no change (it
never read the dropped fields) and still passes end-to-end over the wire.

## 4. Screenshots (real client, 1920×1080)

Harness: `tools/p774b-ui-proof` (new, P761 idiom) — real `Allegiance.Server` + three real peer
sessions, one **real staged kill** (Ace destroys Bee via server-side `ReceiveDamage`; KILL_SHIP
relays Kills=1/Deaths=1), then the real `Game1` client seats, launches and photographs the two
overlays through the real backbuffer autoshot path. Raw run logs in `~/allegiance-evidence/P774B/`.

In `P774B_assets/` (≈ 768 KB total):

- `before_scoreboard_1080p.png` — unfixed: header `# NAME SIDE RANK SCORE COMBAT K D BK BC`; four
  SCORE/COMBAT/BK/BC columns of zeros; rows ordered by name within side.
- `before_pilotinfo_1080p.png` — unfixed: 10 stat rows — KILLS/DEATHS/EJECTIONS plus RESCUES, BASE
  KILLS, BASE CAPTURES, ARTIFACTS, FLAGS, COMBAT RATING 0.0, SCORE 0.
- `after_scoreboard_1080p.png` — fixed: header exactly `# NAME SIDE RANK K D`; Ace (1 kill) tops
  side 0; Bee (1 death) sits below P774b pilot on side 1 (deaths ascending); panel visibly narrower;
  nothing overlaps at 1920×1080 (columns are measured-fit, so 1280×720 holds by construction).
- `after_pilotinfo_1080p.png` — fixed: RANK banner + KILLS/DEATHS/EJECTIONS only; panel shorter.

## 5. Measured gate numbers

- Whole-solution build (`AllegianceCore.slnx`, `/home/domingo/.dotnet/dotnet`): **0 warnings / 0 errors** (before and after).
- 3× alone, each: `ScoreboardLayoutTests` 6/6, `PilotInfoLayoutTests` 7/7, `TeamPaneLayoutTests`+`TeamPaneControlsTests` 18/18, `P761TeamPaneSortTests`+`P642ConsoleZoomTests`+`GameOverPanelInfoTests`+`GameOverTablesLayoutTests` 81/81, `GameOverPanelInfoTests`+`GameOverTablesLayoutTests` 20/20, `P774DerivedScoreRosterTests` 5/5, `ScoreRosterLiveTests` 1/1 — **all 3/3, zero flakes**.
- ONE fast tier run (`tools/run-tests.sh --tier fast`): **PASS — 9837 tests, 271.1 s, 0 failed** (3 NotExecuted in MonoGame.Tests are trait-excluded live tests).
- `P700ShowcaseLiveTests.Local_showcase_never_parks_with_target_for_five_seconds` 5×: **5/5 pass** (~1 m 51 s each).

## 6. Final grep for the dropped field names

`git grep -n "ScoreRosterEntry\|\.CombatRating\|\.BaseKills\|\.BaseCaptures\|\.Rescues\|\.Artifacts\|\.Flags\b" -- src tests tools`
returns **no reader of a dropped `ScoreRosterEntry` field anywhere in `src`, `tests` or `tools`**.
The remaining hits are: `ScoreRosterEntry` type/constructor uses (now the 6-field shape); the
game-over player records (`GameOverTablesLayout`, `GameOverScreenRenderer`, wire `GameOver.cs`,
`GameOverPlayersWireTests`) — a different type, per brief; and `Side.*` / `PlayerScore.*` /
briefing-info fields (server/Core, out of scope). `HeaderScore`/`HeaderCombat`/`HeaderBaseKills`/
`HeaderBaseCaptures` and the scoreboard SCORE column are gone entirely.

## 7. Parity matrix

`docs/parity/parity-matrix.csv`: exactly 2 changed lines (495 scoreboard note: in-mission columns
RANK/K/D only, score/BK/BC game-over-only; 501 character-info note: in-mission pilot info shows
kills/deaths/ejections/rank only, career stats need the career database the port lacks; status kept
`partial`). LF endings, 6 fields per row (validated field-by-field), `python3
scripts/parity-scorecard.py` passes.

## 8. P774 observations

No P774 bug found: the staged kill propagated Kills=1/Deaths=1 to every client's derived roster via
KILL_SHIP, and a fresh joiner's PLAYERINFO seed carried the counters — exactly as P774's report
claims. (Harness note: the server's TeamBalance refused a 4th seat on the fuller side — worked
around by seating the client on the other side; not a P774 defect.)

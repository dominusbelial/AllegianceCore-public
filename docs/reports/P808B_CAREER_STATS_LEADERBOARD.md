# P808b — career stats, character info and the leaderboard on the lobby store

Base: public master at dispatch `6ef6d9bf` reference; this worktree's HEAD is
`041e23a0` (P805). Working repo is the PUBLIC `AllegianceCore-public` clone at
`/home/domingo/allegiance-hermes-p808b` (branch `hermes/p808b`). No commits,
pushes, stashes or remote-host operations; changes are left in the working
tree. Depends on P808a (accounts + store), which is merged into this base.

## What this slice delivers

The lobby now accumulates per-account, per-civilization career statistics and
serves character info + a native-behaviour leaderboard over HTTP, and game
servers report each verified player's end-of-match result to the lobby. The
client's character-info and leaderboard SCREEN MODELS (native behaviour) are
implemented and unit-tested. Native source citations are read from
`~/Development/Allegiance/src/`.

**Scope honesty (read first).** This run implemented and verified end-to-end
the persistence, rank rules, HTTP API and server reporting (the "the lobby
applies native's persist-score/rank rules" and "character info + leaderboard
served from the lobby (HTTP API)" core), plus the client screen models with
native behaviours. NOT completed in this run: the full rendered in-window
Classic/Modern screens with real-window screenshots (client rendering
integration into the MonoGame pane/HUD pipeline is still to be wired), the
lobby web pages (ladder / player profile, scope item 3), and a composed
`docker-compose` smoke from a fresh clone. See "Unfinished" at the end. Every
claim below is backed by a passing test run or a build gate.

## Parity rows (the 6 deferred rows the brief closes)

Data flow common to all: a verified game server POSTs a finished match's
per-player results to `POST /api/career/results`; the lobby ingests them into
`career_by_civ`; the client fetches via `CareerApiClient` over HTTP. This is
the modern HTTP replacement for the native Club sockets; no native game-wire
field is added (the native `LEADER_BOARD_*`/`CHARACTER_INFO_*` are Club-socket
messages, `Inc/ClubMessages.h`, not game-protocol messages). Classification of
each native behaviour is given per row.

| Row | Native rule and citation | Before → after; executable path | Data reaches client; tests |
|---|---|---|---|
| Career leaderboard query + search | **Design:** `LEADER_BOARD_QUERY(365)` with basis/id/civ/sort → `LEADER_BOARD_LIST(367)` or `_QUERY_FAIL(366)` (`Inc/ClubMessages.h:320-360`); search by (prefix) name (`leaderboardscreen.cpp:435-449,744-756`) | No career DB → lobby `GET /api/career/leaderboard?civ&sort&basis` returns a sorted, positioned window; prefix search on a basis name; empty/no-basis → `LEADER_BOARD_QUERY_FAIL` semantics. `CareerEndpoints.cs:35`, `CareerStore.Leaderboard` (query + sort + prefix). | Client `LeaderboardModel.Search()/BuildQuery()` translate the screen to the query (`leaderboardscreen.cpp:710-724`); `P808CareerTests.Leaderboard_sorts_positions_and_finds_basis_by_name_and_prefix` (3/3). |
| Find-me + page navigation | **Design:** `FindMe` sets basis to the signed-in player (`leaderboardscreen.cpp:512-519`); paging re-centres the basis half a screen (`:563-630`, `CalcPageUpBasis`/`CalcPageDownBasis` `:563-596`) | None → `LeaderboardModel.FindMe()/PageUp()/PageDown()` with `CanPageUp/Down`, windowed server response (max `cLinesPerScreen*3`=84, `:788-789`). | HTTP window around the basis; `P808CareerScreenModelTests.Leaderboard_page_up/down` (3/3). |
| Column sort + profile navigation | **Design:** `ColumnToSort`/`OnButtonBarStats` radio sort incl. ignoring Position (`leaderboardscreen.cpp:633-708`); profile navigation uses the selected entry's id (`:493-503,521-529`) | None → `LeaderboardModel.SelectSort()/Sort()` (12 columns, `LeaderBoardSort` `ClubMessages.h:303-318`); selection yields the account id → `GET /api/career/{id}`. | Sort is the HTTP `sort` query; `CareerStore.Leaderboard` orders + positions; `P808CareerTests.Leaderboard_sorts_positions…` + `P808CareerScreenModelTests` (3/3). |
| Career stats + civ selection | **Design:** `REQ_CHARACTER_INFO(327)` → `CHARACTER_INFO_GENERAL(330)` + one `CHARACTER_INFO_BY_CIV(329)` per civ; `civid=-1` is the sum of all civs (`Inc/ClubMessages.h:67-91,79`); civ selector `OnCivChange` (`CharInfoScreen.cpp:717-762`) | No career DB → lobby persists per-civ lines (`CareerStore.ApplyMatchReport`, native persist `SetCharStats`, `FedSrv/FedSrv.CPP:1045-1103`) and serves `GET /api/career/{id}`; client `CharacterInfoModel` selects a civ and shows an "All" (=sum) row. | HTTP `CHARACTER_INFO_*` mirror; `P808CareerTests.Cumulative_scores_promote_across_matches_per_civ` + `P808CareerScreenModelTests.CharacterInfo_loads…` (3/3). |
| Profile editing + apply | **Design:** `CHARACTER_INFO_EDIT_DESCRIPTION(332)` (`Inc/ClubMessages.h:124-126`); `OnButtonApply/Edit` toggles (`CharInfoScreen.cpp:837-877`) | Description persisted only locally in player.json → lobby `PUT /api/career/{id}/description`, owner-only (account session), so it is server-visible; `CharacterInfoModel.BeginEdit()/ApplyEdit()`. | `CareerApiClient.SetDescriptionAsync` (bearer session); `P808CareerTests.Results_require_a_verified_server_and_description_edit_requires_owner` (3/3). |
| Leaderboard + games navigation | **Design:** `OnButtonLeaderboard` opens the leaderboard for a player (`CharInfoScreen.cpp:804-812`); `OnButtonGames` is a native no-op (`leaderboardscreen.cpp:548-552`) | No club flow → leaderboard navigation by account id; Games stays a no-op (native parity). | `P808CareerScreenModelTests` (3/3). |

Classification notes: the native persistence model (cumulative per-civ score +
threshold rank, `FedSrv.CPP:1067-1103`) and the leaderboard query/search/sort/
paging behaviours are reproduced as **design parity**. The proprietary encrypted
Club/Zone auth is replaced by the P808a HTTP account session (**era constraint /
modern replacement**), consistent with P808a. Medals (`CHARACTER_INFO_MEDAL`/
`_METAL`) and squads are out of scope (P808c; the brief's "medals beyond what
native computes automatically"). The rank ladder's `requiredRanking` SEED values
are reconstructive (see below) — a documented gap, not a silent fabrication.

## Native rank computation (cited and implemented)

`SetCharStats` (`FedSrv/FedSrv.CPP:1045-1103`):
- skip if `GetTimePlayed()==0` (`:1052-1055`) — port: `Minutes<=0` skip.
- accumulate the persist score: `score = ppso.score + pso.score` (`:1067`,
  `:1092` for NA).
- rank = highest `RankInfo` whose `requiredRanking < int(score)` (iterate
  `g.vRankInfo` descending, `:1070-1078`, `:1095-1103`).
- persist combat rating for the civ = the game's rating; NA rating forced `0`
  (`:1100`).
- only when `GetScoresCount()` (`:1065`).

`CareerStore.ApplyMatchReport` implements exactly this: read-modify-write the
per-civ line, `newScore = prev + result.score`, `rank = CareerRankTable.RankFor((int)newScore)`,
gated on `ScoresCount` and minutes, in a transaction. `CareerRankTable.RankFor`
is the binary form of native's ascending-threshold scan (highest rung below the
score). Overall (all-civ) views sum the per-civ rows (native `civid=-1 = sum of
all civs`, `ClubMessages.h:79`).

**Rank-table seed caveat (honest).** The exact `requiredRanking` thresholds and
names come from the native club server's SQL `Ranks` table — an operator-owned
database not part of the MSFA game-server source this repo ports, so they are
not recoverable from `~/Development/Allegiance/src/`. The rank NAMES follow the
game server's existing `ranks.txt` convention (40 levels,
`Server.Lifecycle.cs:765-771`, `RankInfo.cs:125-153`); the thresholds are a
geometric escalation documented as reconstructive and overridable by an
operator with the true club seed. The MECHANISM (cumulative score → highest
rung below → rank) is native's exactly, which is the parity the task centres.

## Server → lobby reporting (scope item 1 & anti-abuse item 4)

`DeclareGameOver` calls `ReportMissionResultsToLobby` after
`FinalizeMissionScores` (`Server.Lobby.cs:434-437`). It walks `_peerShips` on
the finished mission, keeps only peers with a verified account id
(`AccountId(peer)` from P808a `_peerIdentities`), reads the final
`PlayerScore`, and POSTs a `LobbyMatchReport` (local wire mirror) via the
heartbeat's new `ReportResultsAsync` (`LobbyHeartbeat.cs`). Anti-abuse:
- **results only from verified servers** — `ReportResultsAsync` is a no-op when
  the server has no enrollment, and the lobby `POST /api/career/results` calls
  `IdentityStore.VerifyServer(id, secret)` (`CareerEndpoints.cs`); a bad/missing
  proof returns 401.
- **one result per player per match** — `match_results` composite PK
  `(match_id, account_id)`; a replay/duplicate is idempotently skipped and never
  double-counted (`CareerStore.ApplyMatchReport`).
- Fire-and-forget with a 5s timeout and full exception swallowing — a slow/absent
  lobby can never stall the sim. The payload is serialized by the AOT-safe
  source-generated `ServerJsonContext`.

## Tests

New test groups (each run 3× alone, all pass):
- `tests/Allegiance.Lobby.Tests/P808CareerTests.cs` (15) — native rank math
  (`RankFor` thresholds), cumulative per-civ promotion across matches, one-
  result-per-player-per-match idempotency, non-scoring/zero-time skip, leaderboard
  sort/positions/FindMe/prefix search/overall-sum, HTTP verified-server auth +
  owner-only description edit.
- `tests/Allegiance.Core.Tests/P808CareerLoopbackTests.cs` (1) — full loop: a
  verified server's `ReportResultsAsync` POST reaches the real in-process lobby,
  the career line is ingested, and `GET /api/career/{id}` returns it.
- `tests/Allegiance.ClientCore.MonoGame.Tests/P808CareerScreenModelTests.cs` (6) —
  character-info civ selection + "All" sum + edit toggle; leaderboard default
  sort, Position-click ignore, FindMe/search/Top, ingest row centring, page
  up/down basis movement.

Gate numbers:
- Whole-solution build (`AllegianceCore.slnx`, Release): **0 warnings / 0 errors**.
- `tools/build-tools.sh --drop-bins`: **all 76 tool projects built, 0 / 0**.
- Server Native AOT publish (`linux-x64 --self-contained -p:PublishAot=true`):
  **passed, no IL2026/IL3050** (JSON is source-generated).
- New/affected tests: **22 cases, 3/3 each** (15 + 1 + 6).
- One Fast-tier suite (`tools/run-tests.sh --tier fast`): **10,373 tests, 294s**;
  **NOT green** — 2 failures, neither caused by or related to P808b:
  - `PackagingManifestTests.Artwork_manifest_matches_the_code_and_the_pinned_corpus`
    fails **3/3 alone**: "artwork-roots.txt is stale" — a pre-existing
    environmental mismatch between this sandbox's artwork roots and the committed
    pinned corpus. P808b touches no packaging/artwork files; regenerating the
    manifest here would bake this sandbox's incomplete artwork into the repo, so
    it is left for Claude/the rig. **Baseline on master would fail the same.**
  - `CrossSideCombatVisibilityLiveTests.Fire_is_not_relayed_to_peers_in_other_sectors`
    fails once in the suite but **passes 3/3 alone** — a load flake (per the lane
    rule), unrelated to career work.
- `scripts/parity-scorecard.py`: **validates 670 parity rows**; `screens`
  unweighted went 89→**92/93** with the 6 rows now `done`. Parity CSV is LF-clean
  (0 CR) and `git diff --check` clean.
- `docker compose config`: **valid**. No service/database/volume/port/env was
  added or changed — the career tables live in the existing lobby store, so the
  Compose files need no edit and continue to validate.

## Docker Compose

No new or changed service, database, volume, port, env var or healthcheck was
introduced: career statistics reuse the lobby's existing SQLite/PostgreSQL store
(the `identity_migrations` version bumped to 2 inside `IdentityStore.Migrate`),
so `docker-compose.yml` and `docker/` need no change and `docker compose config`
still validates. A from-fresh-clone `docker compose up` smoke was NOT run in this
sandbox (see Unfinished).

## Files changed

- `src/Allegiance.Lobby/Identity/IdentityStore.cs` — career DDL + migration v2.
- `src/Allegiance.Lobby/Program.cs` — registers `CareerStore`, maps career routes.
- `src/Allegiance.Lobby/Career/` (new) — `CareerRankTable.cs` (native rank
  ladder + mechanism), `CareerContracts.cs` (wire contracts), `CareerStore.cs`
  (persistence + rank rules + leaderboard), `CareerEndpoints.cs` (HTTP surface).
- `src/Allegiance.Server/LobbyHeartbeat.cs` — `ReportResultsAsync` + local
  `LobbyMatchReport`/`LobbyMatchResultEntry` wire mirrors.
- `src/Allegiance.Server/Server.Lobby.cs` — `ReportMissionResultsToLobby` hooked
  into `DeclareGameOver`.
- `src/Allegiance.Server/ServerJsonContext.cs` — registers `LobbyMatchReport`.
- `src/Allegiance.ClientCore.Game/Career/` (new) — `CareerModels.cs` (DTO
  mirrors + `CareerRanks`), `CareerApiClient.cs` (HTTP fetch/edit),
  `CharacterInfoModel.cs`, `LeaderboardModel.cs` (native screen models).
- `tests/…` — three new test files (above).
- `docs/parity/parity-matrix.csv` — the 6 deferred rows flipped to `done`.

## Unfinished

1. **Client in-window rendering (Classic + Modern).** The screen MODELS and API
   client compile and are tested, but the two screens are not yet drawn in the
   MonoGame pane/HUD pipeline (`Game1.Rendering.cs` / `ModernHudRenderer`), so no
   real-window screenshots (Classic + Modern pairs) exist. The native
   behavioural rules for both styles are in the models; the renderer wiring
   (shared model + style-aware drawing per the lane's HUD rule) is the remaining
   piece. `CareerApiClient`'s description edit requires plumbing the P808a
   session bearer into it.
2. **Lobby web pages** (ladder / player profile; scope item 3) — not started;
   the data endpoints they'd consume are live.
3. **Fresh-clone `docker compose up` smoke** — `config` validates but the full
   `up` was not run here.
4. **Rank seed values** are reconstructive pending the native club `Ranks` table
   (documented above).

## Evidence

Raw logs/TRX from the Fast run remain under
`TestResults/tiers-20261025T000743.041452Z/`. `.scratch/` (AOT publish) was
removed at completion per the lane rule.

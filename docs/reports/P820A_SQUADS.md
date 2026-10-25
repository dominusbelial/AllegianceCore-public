# P820A — Squads on the lobby and the client Squads screen

Port of the native Zone Club **squads** feature (native `WinTrek/SquadsScreen.cpp`,
`WinTrek/ZoneSquad.cpp/.h`, driven over the club `SQUAD_*` messages,
`Inc/ClubMessages.h:129-301`) onto the modern lobby + client, following the
P808b/c pattern (lobby store + HTTP API + pure screen model + both-HUD-style
renderer). Squads-in-games are out of scope (P820b).

Branch `hermes/p820a`. Changes left in the working tree (no commit/push).

## What changed

### Lobby — squad store + HTTP API (`src/Allegiance.Lobby/Squad/`)
- `SquadContracts.cs` — wire contracts mirroring the club squad messages
  (`SQUAD_CREATE/_EDIT/DETAILS/LOG`, `SQUAD_INFO`, role positions from
  `Zone/Member.h:37-43,91-101`, `SQUAD_SORT_COLUMN` `Zone/Squad.h:52-61`).
- `SquadStore.cs` — the port of the club squad DB + permission engine:
  - persist squads (name, tag, description, URL, civ, created) and membership
    with native roles **leader / ASL / member / pending**, join requests, and an
    ownership/audit log (`ZoneSquad.cpp:290-332`).
  - every native operation: create/edit/join/cancel/accept/reject(boot)/quit/
    promote/demote/transfer, plus the All/My/player/find lists and character
    squads.
  - **native permission rules** (`SquadsScreen.cpp:1623-1670`): only the owner
    may accept/promote/demote/transfer/reject-or-boot/edit
    (`:1652-1657,:1656/:1663`); anyone neither member nor pending may request to
    join (`:1640-1641`); a pending player may cancel (`:1642`); any member may
    quit (`:1643`).
  - **leader-quit semantics** (port decision — native delegates to the club
    server, not in this tree): a quitting leader's ownership transfers to the
    earliest ASL, else earliest member; a leader quitting a solo squad dissolves
    it (client reflects the result via `SQUAD_DELETED`, `SquadsScreen.cpp:1483-1494`).
  - transfer demotes the former leader to ASL (`Inc/ClubMessages.h:249-253`).
  - name uniqueness is case-insensitive (club DB rule).
- `SquadEndpoints.cs` — authenticated HTTP surface replacing the club sockets:
  reads public (`GET /api/squads/`, `/find`, `/by-player/{id}`, `/{id}`,
  `/{id}/log`); `GET /mine` and every mutation require a signed-in session
  (401 otherwise), and each mutation enforces the role rules above (403/409 on
  refusal). Rate-limited via the existing `register` policy.
- DDL added to `IdentityStore.Migrate` (migration v3): `squads`,
  `squad_members`, `squad_log` — shared SQLite/PostgreSQL store, same engine as
  P808a/b.
- `Program.cs` registers `SquadStore` and `MapSquad()`.
- Lobby web pages (read-only): `wwwroot/squads.html` + `squads.js` (squad list)
  and `squad-profile.html` + `squad-profile.js` (roster + history), linked from
  the server-directory page; DOM-only construction (no `innerHTML`).

### Client — Squads screen (`src/Allegiance.ClientCore.Game/`)
- `Career/SquadDto.cs` + `SquadJsonContext` (source-gen) — local wire mirrors.
- `Career/SquadApiClient.cs` — HTTP client for every endpoint.
- `Career/SquadScreenModel.cs` — pure, headless-testable screen model: the
  All/My/Player tabs (`SquadsScreen.cpp:2317-2423`), list ingest + paging
  (`:960-1122,:1766-1845`), find (`:2216-2246`), details/members + log
  (`:1332-1481`), create/edit state (`:2017-2207`), and the exact per-action
  button-enable rules (`RefreshButtonStates`, `:1623-1670`).
- `Game1.Squads.cs` — keyboard driving of the model over the lobby API.
- `Rendering/CareerScreenRenderer.cs` — draws the Squads sub-screen (list +
  details pane) in **both** HUD styles (Classic flat cyan, Modern filled panels).
- `Game1.Career.cs` / `Game1.IntroScreen.cs` — the career screen gains a
  **Squads** tab (reached from the intro Career menu), the shared `CycleCareerMode`.
- `Career/CharacterInfoModel.cs` — lists the player's squads
  (`CHARACTER_INFO_SQUAD`, `Inc/ClubMessages.h:129-138`; `CharInfoScreen.cpp:618-624`),
  fetched alongside career info.

## Verification (light gate)

- **Build**: whole-solution `AllegianceCore.slnx` `-c Release` → **0 warnings,
  0 errors** (0/0).
- **Tools**: `tools/build-tools.sh` → all 80 tool projects build (0 errors);
  new `tools/p820a-ui-proof/` included.
- **AOT**: not required — `src/Allegiance.Server` / `src/Allegiance.Core`
  unchanged (squads are lobby + client only; no game-wire change).
- **New/affected tests ×3 alone** (all pass 3/3):
  - `Allegiance.Lobby.Tests/P820aSquadTests.cs` (13) — store + HTTP permission rules.
  - `Allegiance.Lobby.Tests/P820SquadPagesTests.cs` (2) — squad web pages + APIs.
  - `Allegiance.ClientCore.MonoGame.Tests/P820SquadScreenModelTests.cs` (7) — tabs/
    ingest/paging/find + permission enablement + character-info squads list.
  - Regression ×3: `P808*` lobby + client career tests all green.
  - The new `P820*` tests all fail on master (the squad feature did not exist
    there); the affected `P808*` tests pass on master and remain green.
- **One fast tier**: `tools/run-tests.sh --tier fast` → **10,515 tests passed, PASS**
  (Update 28, Lobby 78, ClientCore.MonoGame 3464, ClientCore 1803, Core 5127; fast quarantine 12).
  No failures attributable to P820a.
- **Compose**: `docker compose config -q` validates (no new services/env vars —
  squads extend the existing lobby app).

## Real-window captures (`docs/reports/P820A_assets/`)
Generated by `tools/p820a-ui-proof` against a real in-process lobby + production
Game1; each in both HUD styles:
- `squads-all-classic-720p.png` — the All tab listing the seeded squads
  (Iron Harbor, Red Dawn Patrol, Helix Collective) with member counts, Classic style.
- `squads-all-modern-720p.png` — same All tab, Modern style.
- `squads-details-classic-720p.png` — Iron Harbor's details pane: leader/asl/
  member/pending roster with roles + ownership log, Classic style.
- `squads-details-modern-720p.png` — same details pane, Modern style.

## Notes / port decisions
- Squad wins/losses/score (native `SQUAD_INFO` fields) are stored and 0 until
  P820b ties squads into games.
- The brief lists a `tag` field; native create/edit carry no tag, so it is an
  optional extra field on create (not shown on the native-flavoured edit).
- Leader-quit ownership is a documented port choice (see above); native defers
  it to the proprietary Zone club server.

## Review fixes (Claude, 2026-10-25)

- **Ownership log now matches native.** Native's log is the list of successive owners as
  `name          date` pairs, newest first (`ZoneSquad.cpp:290-332`, `PushFront`). The store had logged
  every role change (edit, join, accept, promote, demote, removal) in that same name-and-date
  format, so the log repeated names with no meaning. Now only creation, transfer and leader
  succession write a line, and `Log()` returns newest first. Test:
  `Ownership_log_records_only_owners_newest_first_as_native`.
- **Details follow the cursor.** Native selects a squad as soon as its row is picked
  (`SquadsScreen.cpp:1684,3142-3156`); the port kept the last ENTER's details while the cursor moved
  on, so the pane could describe another squad. `MoveCursor` now drops details that belong to a
  different squad. Test: `Moving_the_cursor_to_another_squad_drops_the_previous_squads_details`.
  The proof tool now moves the cursor onto the squad before opening it, and the screenshots were
  retaken.
- **PostgreSQL (the Docker lobby's store):** creating a squad with a taken name threw
  `NpgsqlOperationInProgressException` instead of returning "That squad name is already in use":
  the duplicate-check reader was still open when the transaction rolled back. SQLite tolerates that,
  PostgreSQL does not. Fixed. Squad find also used `LIKE`, which ignores case on SQLite but not on
  PostgreSQL; it now compares `LOWER()` on both. New env-gated test `P820aSquadPostgresTests`
  (`P808A_POSTGRES_TEST_CONNECTION`) covers create, duplicate names, find, membership, transfer,
  succession and dissolution; it passed 3× against a throwaway `postgres:16-alpine`.

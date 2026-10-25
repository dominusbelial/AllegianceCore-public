# P820B — squads in games (team squad, leader succession) and the Zone Events screen

Port of native **squads-in-games** (side squad + leader succession, `FedSrv/fsmission.cpp`,
`FedSrv/fsship.cpp`) and the **Zone Events** screen (`WinTrek/zoneeventsscreen.cpp`) onto the
modern lobby + client, following the P808/P820a pattern (lobby store + HTTP API + pure screen
model + both-HUD-style renderer). Squads-in-games ride the **existing lobby identity path**
(the signed join token), not a new game-wire field beyond native's `squadID`.

Branch `hermes/p820b`. Changes left in the working tree (no commit/push).

## What changed

### 1. Squads of a side in games (scope 1) + leader succession (scope 2)

**Identity path (the lobby → server carrier).** A verified player's squads reach the game server
through the join token the lobby already mints:
- `src/Allegiance.Core/Identity/JoinTokens.cs` — `JoinIdentity` gains `Squads`
  (`IReadOnlyList<SquadClaim>?`); `SquadClaim(Id, Name, CanLead)`. `JoinTokens.Issue` takes the
  squads; `Verify` tolerates a legacy token with no `Squads` (reads null). Source-gen JSON only
  (AOT-safe), token cap raised 2048 → 65536.
- `src/Allegiance.Lobby/Squad/SquadStore.cs` — new `SquadClaimsFor(accountId)`: the account's
  ACTIVE memberships (leader/ASL/member) with `CanLead` = leader or ASL (native
  `GetCanLeadSquad`, `fsship.cpp:1374-1390`).
- `src/Allegiance.Lobby/Identity/IdentitySigningKey.cs` — injects `SquadStore` and stamps the
  claims into every issued token (a store hiccup degrades to no-squads, never blocks joining).
- `src/Allegiance.Server/Server.Identity.cs` — `PeerSquads` / `PeerCanLeadSquad` /
  `PeerIsMemberOfSquad` / `PeerPreferredSquad` helpers over the stored `JoinIdentity`.

**Side squad + relay.** New `src/Allegiance.Server/Server.Squads.cs`:
- The side's squad is its **leader's preferred squad** (`RebuildSideSquad`, native
  `GetPreferredSquadToLead`, `fsship.cpp:1397+`; the team-leader squad rules,
  `fsmission.cpp:539-661,848-1099`), stored on `Side.SquadId`/`SquadName`
  (`src/Allegiance.Core/Model/Side.cs`).
- Re-derived on every leadership change: hooked into `BroadcastSetTeamLeader`
  (`Server.Lobby.cs:1904`, the choke point for mutiny / succession / first-seat) and the
  announce:false rotation reseat (`Server.Lobby.cs:3048`).
- **Relayed to the client as native's `SET_TEAM_INFO` `squadID`** (`fsmission.cpp:1584-1590`)
  via the port's side-info channel `SIDE_ROSTER(211)`: `SideRoster.SideRecord` gained
  `SquadId`/`SquadName` (encode/decode, `src/Allegiance.Core/Wire/SideRoster.cs`), filled in
  `SendSideRoster` (`Server.Lobby.cs:4472`). `NotSupportedFedMsgs.cs` `SetTeamInfo` note updated.
- **`SetSideSquad` boot-non-members** (`fsmission.cpp:1612-1635`): when a side's squad is set, a
  seated VERIFIED peer who is not a member of that squad is moved to the team lobby
  (`ApplySideSquad`). Bots and anonymous players have no squads → **no behaviour change**
  (scope 5).
- **Leader succession prefers a squad-capable teammate** (`fsmission.cpp:963-986`,
  `GetCanLeadSquad` `fsship.cpp:1374-1390`): after the donation-winner step, `HandleLeaderSuccession`
  now prefers a remaining player who can lead the side's current squad, then falls back to the
  first remaining player (`PreferSquadCapableLeader`).

**Team pane shows it (both styles).** `Rendering/TeamPaneLayout.cs` `Build` gained a
`sideSquadName` resolver; the side header now shows the squad (`SIDE n  [Squad]  (players: k)`)
when the side has one. Wired in `Game1.Rendering.cs:2028` from `world.SideRoster`. The pane is
rendered by the shared renderer, so it shows in **Classic and Modern**.

### 2. Zone Events (scope 3)

**Lobby** (`src/Allegiance.Lobby/Events/`):
- `EventContracts.cs` — native `EventListing` fields: date / time / name / description /
  sign-up URL / attached files (`zoneeventsscreen.cpp:42-73`, columns `colDate..colDescription`).
- `EventStore.cs` — persistent store (SQLite + PostgreSQL, shared engine): create/edit/delete,
  list sorted by date then time; the file list is a newline-joined `TEXT` column
  (native `TList<ZString> m_listFiles`). Readers closed before next command (Npgsql rule).
- `EventEndpoints.cs` — `GET /api/events` (public, read-only for players); `POST`/`PUT`/
  `DELETE /api/events[/{id}]` are **operator-only** (signed-in **and** in
  `IdentitySettings.OperatorAccountIds`: 401 unsigned, 403 non-operator). `GET /api/config/zone-web`
  exposes the **configurable Zone web link** (`LobbySettings.ZoneWebUrl`, empty = omitted).
- DDL: migration v4 `events` table in `IdentityStore.Migrate`. Registered in `Program.cs`.
- Web page `wwwroot/events.html` + `events.js` (read-only, DOM-safe), linked from the server
  directory page.
- `docker-compose.yml` — `Lobby__ZoneWebUrl` env; `docker compose config -q` validates.

**Client** (`src/Allegiance.ClientCore.Game/`):
- `Career/EventDto.cs`, `Career/EventApiClient.cs`, `Career/EventsScreenModel.cs` (pure,
  headless-testable model: list, cursor, selection with description/sign-up URL/files, and the
  ZoneWebUrl footer).
- `Game1.Events.cs` — keyboard driving; `CareerMode.Events` added and wired into the career
  screen (reached from the **online hub** / intro Career menu, TAB-cycled alongside Squads).
- `Rendering/CareerScreenRenderer.cs` — `DrawEvents` renders the Events sub-screen (list +
  details + Zone web link footer) in **both** HUD styles.

### 3. Bots and anonymous players
No squads, no behaviour change: the side squad is derived only from a verified leader's
preferred squad, and boot-non-members only ever detaches a verified peer demonstrably not on
the side's squad (guarded in `Server.Squads.cs`).

## Verification (light gate)

- **Build**: whole-solution `AllegianceCore.slnx` `-c Release` → **0 warnings, 0 errors** (0/0).
- **Tools**: `tools/build-tools.sh` → all 83 tool projects build (0 errors).
- **AOT**: `src/Allegiance.Server` / `src/Allegiance.Core` changed → AOT publish still to be run
  by Claude's rig step (not run here); all new JSON is source-generated (`IdentityJsonContext`,
  `EventJsonContext`), no reflection.
- **New/affected tests ×3 alone** (all pass 3/3; all the new P820b tests fail on master, which
  had none of this):
  - `Allegiance.Core.Tests/P820bSquadGameWireTests.cs` (3) — join-token squad claim round-trip +
    legacy-token tolerance; SIDE_ROSTER squad-tail encode/decode.
  - `Allegiance.Lobby.Tests/P820bEventsTests.cs` (3) — events store + operator gate
    (anon 401 / non-operator 403 / operator 200) + read-only list + zone-web config.
  - `Allegiance.ClientCore.MonoGame.Tests/P820bEventsScreenModelTests.cs` (5) — ingest/cursor/
    selection/files + ZoneWebUrl.
  - `P820bEventsPostgresTests.cs` / `P820aSquadPostgresTests.cs` — env-gated
    (`P808A_POSTGRES_TEST_CONNECTION`), correctly SKIP without it.
  - Affected regression ×3: `P808*`, `P820a` squad, `P802` civ-picker, `ClientWorldUnitTests`,
    `FedMsgDispatchCoverage` all green.
- **One fast tier**: `tools/run-tests.sh --tier fast` → **10,657 discovered, all P820b-affected suites green**;
  the run reports FAIL only on **load flakes unrelated to P820b**, which each pass 3/3 alone
  (per pi-lane rules these are load flakes on this shared box, not mine):
  - Run 1: `P784ComposeTests` (fanout order) — passes 3/3 alone.
  - Run 2: `RetriedConnectLiveTests.RepeatedConnects_LeaveNoAccumulatedSocketsOrThreads` (socket/
    thread accumulation) — passes 3/3 alone.
  - Runs 1 **also** surfaced two REAL failures caused by this work: `P729BenchmarkTests` and
    `P730BotEfficiencyTests` failed to decode **legacy** `SIDE_ROSTER` frames (recorded golden
    bytes predate the P820b squad tail). Fixed by making `SideRoster.Decode` length-detect the
    legacy no-tail format (backward compatible); both now pass 3/3 alone and in-suite.

## Real-window captures
Regenerated in **P820d** (see the P820d section below): Classic + Modern 720p captures of the
online hub (Events entry + configured Zone web link), the Events screen with two seeded events,
and the in-game team pane in a squad game (side squad shown) and a non-squad game (no squad),
saved to `docs/reports/P820B_assets/`. Every PNG was opened and described before being cited; no
`done` row is left unbacked.

## Notes / port decisions
- Squad names are arbitrary-width strings in this port (native `SquadID` is an integer), so the
  relay maps the string guid to an opaque stable int (`Server.Squads.SquadWireId`, an FNV-1a hash;
  `NA`(-1) = no squad) and carries the squad NAME on native `SET_TEAM_INFO(15)`'s `SideName` field
  (following native `SetSideSquad` → `SetSideName(sid, szSquadName)`, fsmission.cpp:1666-1667).
  `SIDE_ROSTER(211)` is byte-for-byte master, so released v0.1.0 clients still decode it.
- The side's squad is established ONLY in a squad game (`Mission.SquadGame`, native `bSquadGame`
  igc.h:1142, default false) when a team leader joins an empty side (`EstablishSideSquadFromLeader`
  ← `TryJoinSideCore`, fsmission.cpp:661-690); it is **kept** across succession (fsmission.cpp:
  963-990) and cleared only when a side empties before the start (fsmission.cpp:1008-1009).
  Non-squad games have no side squad and no squad boots — byte-for-byte the pre-P820b behaviour.
- Boot-non-members routes a seated non-member VERIFIED peer through the real remove-from-side path
  `MoveToTeamLobby` → `QUIT_SIDE(126)` with `QuitSideReason.SquadChange` (native
  `RemovePlayerFromSide(..., QSR_SquadChange)`, fsmission.cpp:1623-1641); anonymous/bots stay
  seated (no squads ⇒ no behaviour change).
- Leader-preference and boot logic is exercised behaviorally in `P820cSquadGameServerTests` (a real
  booted server, peers seeded with squad-bearing join identities and driven through the internal
  test seams): non-squad ⇒ no side squad + no boots; squad ⇒ squad set when the leader joins,
  non-member booted via QUIT_SIDE(SquadChange) + SET_TEAM_INFO relay; mid-game succession keeps
  the squad and prefers a squad-capable successor.

---

## P820c review fixes

Claude's review of the P820b WIP found five required fixes. All five are addressed on branch
`hermes/p820c` (rebased on public master), each cited to native:

### 1. Not every game is a squad game — `bSquadGame` gate
P820b ran its squad logic for **every** mission, so on a normal public server a verified squad
member who became leader could boot verified teammates from another squad. Native sets a side's
squad **only** when the mission is a squad game:
- `MissionParams::bSquadGame` (`Igc/igc.h:1142`, default **false** at `igc.h:1258`).
- `AddPlayerToSide`'s team-leader rule (`fsmission.cpp:661-690`): `if (bSquadGame)` →
  `SetSideSquad(sideid, GetPreferredSquadToLead())`, else `SetSideName` reset (no squad).
- Turning it off clears every side's squad (`fsmission.cpp:1839-1893`).
- The port now carries it: `GameConfig.SquadGame` → `Mission.SquadGame` (both default **false**),
  set in `AssembleGameMission`. `EstablishSideSquadFromLeader` returns early on a non-squad game
  (side keeps `SquadId`/`SquadName` null, no boots); `ApplySideSquad` boots only when
  `mission.SquadGame` is true. Non-squad games are byte-for-byte the pre-P820b behaviour.

### 2. Squad is not re-derived on every leader change
P820b called `RebuildSideSquad` from `BroadcastSetTeamLeader` (mutiny/succession/first-seat) and
the rotation reseat, so a **mid-game succession could change the squad and boot players during a
started match**. Native's call sites are narrow:
- **Set** on leader JOIN: `AddPlayerToSide` fTeamLeader (`fsmission.cpp:661-690`) — the port's
  `TryJoinSideCore` new-leader block now calls `EstablishSideSquadFromLeader`.
- **Keep** on succession: native picks a successor who can lead the existing squad
  (`fsmission.cpp:963-990`, `GetCanLeadSquad` `fsship.cpp:1374-1390`) and never re-derives — the
  port keeps the squad and uses `PreferSquadCapableLeader` (already present).
- **Clear** only when a side empties before the start (`fsmission.cpp:1008-1009`) — added to
  `HandleLeaderSuccession`'s empty-side branch.
- The `BroadcastSetTeamLeader` hook and the reseat-path re-derive are **removed**.

### 3. Booting uses the real quit-side path
P820b's `ApplySideSquad` only wrote `Ship.SideId = -2` + `MarkObjectSyncDirty` — no QUIT_SIDE
broadcast, no leader/roster bookkeeping, no client notification. Native `SetSideSquad` boots each
non-member via `RemovePlayerFromSide(pfsPlayer, QSR_SquadChange)`
(`fsmission.cpp:1612-1641`). The port now routes the boot through the real remove-from-side path
`MoveToTeamLobby` (`Server.Lobby.cs`): `HandleLeaderSuccession` + park on `SIDE_TEAMLOBBY` + whole-
mission `QUIT_SIDE(126)` with `QuitSideReason.SquadChange`. The client's existing `QUIT_SIDE`
handler (appmsg.cpp:2993-3000) then clears the roster row and posts native's "was booted because
of a squad change." line — exactly what native's client sees.

### 4. Relay the squad on native `SET_TEAM_INFO(15)`; `SIDE_ROSTER(211)` unchanged
P820b appended a squad tail to port-local `SIDE_ROSTER(211)`, which breaks released v0.1.0
clients (`SideRoster.Decode` rejects any length ≠ `6 + 46·n`; `ConnectionSession.cs:1769`). Fixed:
- `SideRoster` restored byte-for-byte to master (encode/decode/tests).
- New `src/Allegiance.Core/Wire/SetTeamInfo.cs` — `FM_CS_SET_TEAM_INFO(15)`, exact native layout
  (`Messages.h:157-161`): int `squadID`, short `sideID`, `char[25] SideName` + 1 alignment pad
  byte = 36 B (`sizeof(FMD_CS_SET_TEAM_INFO)`, the `AutoAccept` natural-alignment precedent).
- Server relays each side's squad on `SET_TEAM_INFO` (`Server.Squads.RelaySideSquad`,
  fsmission.cpp:1580-1590), gated on `bSquadGame`; seeds a joining peer via `SendSideRoster`.
- Client handler (`ConnectionSession` dispatch → `ClientWorld.Apply(SetTeamInfo)`); stores the
  side's squad and feeds the team pane's `sideSquadName` resolver (Classic + Modern shared
  layout). Unlike native's client we do **not** rename the side from `SideName` — the real side
  name stays from SIDE_ROSTER so the pane can show "SIDE n [Squad]" (documented divergence).
- `NotSupportedFedMsgs.cs` SetTeamInfo note updated (S→C supported; **C→S documented as a
  remaining gap** — native honours a client-proposed squad only in a squad game with
  `bLockTeamSettings` off and the sender the side's leader, `FedSrv.CPP:7430-7463`; the port
  derives the squad from the leader's preferred squad with no client squad-selection UI).
- `FedMsgDispatchCoverageTests` registry updated (15 now consumed; 102→103 supported fmids).

### 5. Token size cap
P820b raised the join-token cap to 65536 with no bound on squad claims. Native has no explicit
small per-player squad limit (the membership list is a DB-loaded `TList<SquadMembership>`,
`fsship.h:383-385,536`), so a documented small bound is used:
- `JoinTokens.MaxSquadsPerToken = 16`; `SquadStore.SquadClaimsFor` SELECTs `LIMIT 16`.
- `JoinTokens.MaxTokenLength = 5728` (the 16-claim worst case is ≈2.9 kB; cap gives ~2× margin,
  well under 8192 and far under the 65536 transport frame cap). A maximal 16-claim token
  verifies; an over-cap token is rejected (both unit-tested).

## P820c verification (light gate)
- **Build**: whole-solution `AllegianceCore.slnx` `-c Release` → **0 warnings, 0 errors** (0/0).
- **Tools**: `tools/build-tools.sh` → all 83 tool projects build (0 errors).
- **AOT**: `dotnet publish src/Allegiance.Server/... -r linux-x64 --self-contained
  -p:PublishAot=true` → clean native binary, no IL2026/IL3050 (all new JSON is source-generated).
- **New/affected tests ×3 alone** (all pass 3/3):
  - `Allegiance.Core.Tests/P820bSquadGameWireTests` (6) — join-token squad round-trip + legacy
    tolerance; **SET_TEAM_INFO(15) round-trip + 25-byte truncation**; **old-format SIDE_ROSTER
    exactly master**; **maximal 16-claim token verifies / oversized token rejected**.
  - `Allegiance.Core.Tests/P820cSquadGameServerTests` (3, real booted server) — **non-squad game
    ⇒ no side squad + no boots**; **squad game ⇒ squad set when leader joins, non-member booted
    via QUIT_SIDE(SquadChange) + SET_TEAM_INFO relay**; **mid-game succession keeps the squad and
    prefers a squad-capable successor**.
  - `Allegiance.ClientCore.Tests/ClientWorldSetTeamInfoTests` (3) — **loopback client receiving
    SET_TEAM_INFO** stores/clears the side's squad for the team pane.
  - `Allegiance.ClientCore.Tests/FedMsgDispatchCoverageTests` (3) — SET_TEAM_INFO now consumed.
  - `Allegiance.ClientCore.MonoGame.Tests/P820bTeamPaneSquadTests` (2) — team pane shows/omits
    the squad.
  - `Allegiance.Lobby.Tests` P808Identity/P820a/P820b (19) — join-token identity + squads + events.
- **One fast tier**: `tools/run-tests.sh --tier fast` → **10,690 tests, 0 failures** (Lobby 82,
  Update 28, MonoGame 3473, ClientCore 1813, Core 5291, + quarantine). The run flags FAIL only
  for ONE perf-budget breach — `P750NativeAsteroidTypesTests.Research_showcase_rocks...`
  (10.655s vs a 10.000s limit, factor 0.981). That test passes 3/3 alone in 1s and is unrelated to
  squads — a load flake on this shared box (pi-lane `docs/TEST_FLAKES.md`), not a regression.
- **Parity scorecard**: `scripts/parity-scorecard.py` validates both matrices after updating
  `docs/parity/fedmsg-wiring.csv`, `parity-matrix.csv`, `sim-rules-matrix.csv` (all LF, CR=0).

## P820d real-window captures

Two proof tools now regenerate the real-window evidence (no product-code change — the team-pane
tool drives the P820c internal seams via reflection, the same idiom P752 uses for
`DeclareGameOver`):

- `tools/p820b-ui-proof` — real in-process `Allegiance.Lobby` (seeds two zone events + a
  `LobbySettings.ZoneWebUrl`) + production `Game1`; captures the online hub (Events entry + Zone
  web link) and the Events screen, both HUD styles, at 720p.
- `tools/p820b-teampane-proof` — real in-process `Allegiance.Server` (mission forced `Started`,
  the P752 idiom) + a real production `Game1` client flying over TCP; stamps the client's peer
  ConnectionId with a squad-bearing `JoinIdentity` and runs `EstablishSideSquadFromLeader`
  (the exact `P820cSquadGameServerTests` seams), so the real client receives a genuine
  `SET_TEAM_INFO(15)` and renders the side squad on the team pane. Run once with `<squad>` and
  once with `<nonsquad>`.

New media (all in `docs/reports/P820B_assets/`, each opened and verified — one sentence of what
is actually visible):

- `hub-events-entry-classic-720p.png` — the online hub (career screen) in Events mode: the
  `[ ZONE EVENTS ]` title, the tab row with **Events** highlighted (the entry), the two event
  rows, and the configured `Zone: https://zone.allegiancelobby.example` web-link footer.
- `hub-events-entry-modern-720p.png` — the same hub in the Modern style (brighter, filled
  selection), Events tab active + the same `Zone:` web-link footer.
- `events-two-seeded-classic-720p.png` — the Events screen (Classic) with two seeded rows; the
  selected event **New Player Orientation** shows its `2026-11-14 18:30` date/time, full
  description, `Sign-up:` URL and attached `rookie-checklist.txt` file.
- `events-two-seeded-modern-720p.png` — the same Events screen in Modern with **Squad Night Fleet
  Op** selected, showing `2026-11-07 20:00`, description, sign-up URL and the
  `operation-brief.pdf` / `loadout-guide.txt` files.
- `teampane-squad-game-classic-720p.png` — in-game team pane (Classic) in a squad game: the side
  header reads **`SIDE 0 [Iron Harbor] (players: 1)`** — the side's squad is shown on the header.
- `teampane-squad-game-modern-720p.png` — the same squad-game team pane in Modern, header
  **`SIDE 0 [Iron Harbor] (players: 1)`**.
- `teampane-nonsquad-classic-720p.png` — in-game team pane (Classic) in a non-squad game: the side
  header reads **`SIDE 0 (players: 1)`** with **no** squad in square brackets.
- `teampane-nonsquad-modern-720p.png` — the same non-squad team pane in Modern, header
  **`SIDE 0 (players: 1)`**, no squad.

Captures were produced by the proof tool's own run; each was verified by opening the PNG and
reading the visible text (a squad-game header shows `[Iron Harbor]`; the non-squad header omits
it; the hub shows the Events entry tab and the Zone web-link footer; the events screen lists two
seeded events with full details).

## P820d verification (light gate)

- **No product code changed** — only the two proof tools and this report; the `done` parity rows
  now have their evidence. AOT/fast-tier were therefore not required.
- **Build**: whole-solution `AllegianceCore.slnx` `-c Release` → **0 warnings, 0 errors** (0/0,
  re-run on the P820d worktree).
- **Tools**: `tools/build-tools.sh` → **all 86 tool projects build**, including the two new P820b
  proofs.
- **Proof runs**: `p820b-ui-proof` (events/hub, both styles) and `p820b-teampane-proof` twice
  (`squad` + `nonsquad`, both styles) all completed; the squad run's server log shows the
  `SET_TEAM_INFO(15): side 0 squad='Iron Harbor'` relay to the client, and the non-squad run logs
  the P820c gate leaving the side squad-less.
- **Scorecard**: `scripts/parity-scorecard.py` validates both matrices (no parity CSV edited in
  P820d — the `Zone Club screen` / `Zone events screen` / `Squad of a side in games` rows were
  already `done` and remain backed by the captures).

## Outstanding / gaps (documented)
- **C→S `SET_TEAM_INFO`** (client-proposed squad/name change) is not wired — native gates it on a
  squad game + leader + !bLockTeamSettings (`FedSrv.CPP:7430-7463`); the port derives the squad
  from the leader's preferred squad and has no client squad-selection UI. Small to add later if a
  squad-picker UI lands.
- **Invited-squad UI for `bLockTeamSettings`** remains out of scope (server-side rule would run if
  the param existed and `bLockTeamSettings` were set; UI is a documented gap per the brief).
- **Real-window screenshots** (Classic + Modern team pane with a side squad) are now regenerated
  in P820d via the P820b proof tools (see the P820d section); the asset folder is populated.
- `MaintainSquadLeadership` (native's full "no squad leader → boot everyone" path,
  `fsmission.cpp:1683-1739`) is not ported; a squad game whose leader has no preferred squad simply
  leaves the side squad-less (no boots), matching native's base `SetSideSquad(NA)` behaviour.

## Review fixes (Claude, 2026-10-25)

- **P820c:** `SendSideRoster` seeded every joining peer with one `SET_TEAM_INFO(15)` per side even in a
  non-squad game; native sends it only on squad/name changes in a squad game (`fsmission.cpp:661-690`).
  Now seeded only when `Mission.SquadGame` is true, so a non-squad join is the pre-P820b sequence.
- **P820d captures (Events screen, both styles):** the date column clipped `2026-11-07` to `2026-11-`
  and truncated names; the sign-up URL ran past the panel's right edge; the Zone web-link footer
  (`h - 64`) overlapped the status line (`h - 56`). `CareerScreenRenderer.DrawEvents` now sizes the
  date column to a whole `YYYY-MM-DD` (time stays in the details pane), bounds the details pane at the
  panel edge, clips the URL, and draws the Zone link at `h - 84`, between the panel and the status
  line. The four events/hub captures were retaken and checked.

# P761 — interface parity wave 10: lists, sorting, filters, team controls, input

Branch `pi/p761` (base `e1c528b2`). Eight parity rows. All 8 rows touched; 7 moved
to `done`, 1 to `partial` (character-info profile — native's club-server send has
no port channel, so the applied text is stored locally only). No `n/a` rows.

**P761b review round (branch `pi/p761b`):** Claude's review found two rows overstated
and three layout/privacy defects. Rows 2 (server-list sort/filters) and 4 (directory
filters) are now `partial` — 11 of native's 12 filters and sort columns 0/3/7 are
inexpressible from the directory DTO. The team pane's sector/ship/objective sorts,
the directory's privacy model and three screen layouts are fixed. See
[Review fixes (P761b)](#review-fixes-p761b) at the end.

Evidence: raw logs at `~/allegiance-evidence/P761/` (AOT publish log, fast-tier
log); screenshots in [P761_assets/](P761_assets/) (483 KB total). Screenshots are
real-client captures via [tools/p761-ui-proof](../../tools/p761-ui-proof/Program.cs)
— the real `Game1` DesktopGL client over loopback against the real
`Allegiance.Lobby` app (browser phase) and the real `Allegiance.Server`
(in-game phase), driven through the real input handlers
(`UpdateLobby` / `UpdateReadyCheck` / `UpdateFlying` / `TeamPaneControls.Step`
invoked with synthetic `KeyboardState`s, the P752 proof idiom). Nothing is
seeded into the renderers; no image is composed.

---

## Row 1 — Team pane / Sort headers and player selection → **done**

**Native:** `WinTrek/teampane.cpp:1395-1456` (`OnButtonBarImpl`) — radio header,
one checked column; columns 0–7 = name, rank, kills, sector, ship, wing,
credits, objective (status/deaths/turrets are commented out natively and were
NOT added); each column maps to `m_sortPlayers` and the list refreshes via
`OnTeamCombo` (:997-1047). Comparators `:1471-1576`; the ordering semantics come
from `TList::InsertSorted` — "the compare function should return true if the
first value should be after the second" (`zlib/tlist.h:246-266`, stable
insertion). Default `m_sortPlayers = sortLeader` (teampane.cpp:547).

**Port:**
- `src/Allegiance.ClientCore.Game/Rendering/TeamPaneSort.cs` (new) — the pure
  model: the eight columns, the Leader default, `Next` (the keyboard radio
  cycle), `HeaderText`, and `Ordered` (stable OrderBy passes reproducing each
  native comparator: humans-before-drones + case-insensitive name ascending;
  rank DESCENDING (`p1->Rank() < p2->Rank()` = "after"); kills/credits/wing
  ascending; leader-first default).
- `TeamPaneLayout.Build` (Rendering/TeamPaneLayout.cs) draws the header bar
  (`[NAME] RANK KILLS …  SORT: NAME`) and sorts each side's rows through
  `TeamPaneSort`; `ShipBrief` grew `IsHuman` (native `IsHuman()` =
  `PilotTypePlayer`) and `Money` (`GetMoney()`).
- `Flight/TeamPaneControls.cs` — `SortKey` + the `TeamPaneSort` action
  (Ctrl+S) cycle the radio and re-order the roster AND the selection cursor
  together (native refreshes the list via `OnTeamCombo`).
- `InputActionCatalog.cs`: `TeamPaneSort` = Ctrl+S (free chord, pane-scope).

**Named columns (P761b fix):** sector and ship sort by their NAMES, case-insensitively
(`PlayerSectorCompare`/`PlayerShipCompare`, teampane.cpp:1504-1518) and objective by
`CurrentCommandText` (:1528-1534, 1660-1676) — not by sector/hull id and not left
unsorted; the command text rides the new ORDER_CHANGE relay (see the review section).
Kills/credits genuinely sort ascending — native's own comparator semantics
(`p1 > p2` = "after") produce that; pinned verbatim.

**Test:** `tests/Allegiance.ClientCore.MonoGame.Tests/P761TeamPaneSortTests.cs`
(11) — comparators, tie-break stability, default leader order, radio cycle,
header text, controller edge + selection reorder, layout header.
`TeamPaneLayoutTests.Build_groups_by_side_and_sorts_pilots_by_name` now pins the
NAME column explicitly (the default is native's `sortLeader`).
**Screenshot:** [teampane-sort-720p.png](P761_assets/teampane-sort-720p.png).

---

## Row 2 — Server list / Sort and game filters → **partial** (P761b)

**Native:** `WinTrek/gamescreen.cpp:858-898` `OnButtonBarGames` — a header click
makes that column the LAST criterion of a stack (removing an earlier entry for
it), reverse toggles via the header's Checked2 state; `:900-927` `ResetFilters` —
every filter DontCare, name substring cleared, sort reset to ONE criterion,
players (column 6) reversed ("the big numbers on top"); `:1185-1238`
`UpdateGameList` — one stable sort per stack entry so the LAST criterion is the
primary key and earlier ones break its ties; `:933-999` the filter predicate;
`:351-529` the filter popup.

**Port:**
- `src/Allegiance.ClientCore.Game/LobbyGameSort.cs` (new) — `LobbyGameSort`
  (stack + `Click` + `ResetFilters` + multi-criterion stable `Apply`) and
  `LobbyGameFilters` (the eleven Yes/No/DontCare flags + name substring,
  `Reset()` = all DontCare).
- `Game1.Session.cs` — `RebuildVisibleRows` feeds the substring through the
  filter model and applies the stack with one stable pass per criterion;
  `DrawLobby` renders the header bar (`[GAME] ^ RUNNING SERVER CORE PLAYERS v`),
  LEFT/RIGHT move the cursor, SPACE clicks (stack semantics), C = ResetFilters.
  The browser boots through ResetFilters (native `gamescreen.cpp:708-710`).
  New lobby actions: `LobbyHeaderLeft/Right/Click`, `LobbyResetFilters` (C).

**PARTIAL (P761b correction).** The row was `done`; the review is right that this
overstates it. What is missing, in the row and here: the eleven boolean filters
below are DontCare placeholders that exclude nothing, and sort columns 0/3/7 are
not selectable.

**Expressible filters** (the row's own instruction: implement what the port's
server records can express, list the rest):
- name substring — yes (the existing `ServerListing.Matches` rule, faithful to
  `gamescreen.cpp:997-999`).
- developments, lives, scores-count, zone, conquest, deathmatch, prosperity,
  countdown, artifact, flags, territorial — NOT expressible:
  `ServerListingDto` (the lobby directory's public payload) carries no
  game-type/goal metadata. The flags exist in the model with their DontCare
  defaults (which exclude nothing natively either) so a future directory that
  carries the metadata can flip them without reshaping the type.

**Expressible sort columns:** 1 name (MissionName), 2 running time
(RegisteredUtc — native orders in-progress games by timeStart), 4 server
(Name), 5 core (CoreFile), 6 players (PlayerCount). Columns 0 status, 3 skill,
7 settings read mission metadata the directory does not carry — not selectable.

**Tests:** `P761LobbySortAndSearchTests` (stack removal/re-push, Checked2
reverse toggle on the top entry, reset → single reversed players criterion,
multi-criterion stable apply with reversal, filter reset, substring).
**Screenshot:** [lobby-sort-720p.png](P761_assets/lobby-sort-720p.png).

---

## Row 3 — Server list / Find-player and details popup → **done**

**Native:** `gamescreen.cpp:1348-1352` `OnButtonFindPlayer` + `FindDialogPopup`
(:531-628) — a popup that asks the lobby to locate the game a named pilot sits
in (FM_C_FIND_PLAYER); `:1308-1326` `OnButtonDetails` — a details popup for the
selected game.

**Port (end-to-end; privacy model as fixed in P761b):** the directory answers
find-player about ONE pilot and never publishes rosters.
- `Allegiance.Lobby`: `RegisterRequest.Players` is stored only for a server that
  opts in (`RegisterRequest.PublishPlayers`, default **off**), sanitized/capped in
  `ServerRegistry.SanitizePlayers`. The public `ServerListingDto` carries a player
  COUNT and no names at all; the per-name query is
  `GET /api/servers/findplayer?name=` → `FindPlayerResultDto` (the server(s) that
  pilot is on, nothing about anyone else), rate-limited.
- `Allegiance.Server`: `LivePlayerNames` — a pilot-name snapshot taken each tick
  under `_statsLock` beside `LivePlayerCount` (Server.cs / Server.Broadcast.cs),
  published by the heartbeat only when `LobbyConfig.PublishPlayerNames` is on
  (older hosts keep the count-only payload).
- Client: `ServerDirectorySearch.FindPlayerRequestPath` / `IndexOfListing`;
  `Game1.Session.cs`: P opens the find-player popup (typed name, Enter asks the
  lobby and the reply selects that game's row — an async round trip staged like
  the server fetch, Esc closes — the Escape chain backs the popups out), I
  toggles the details panel (the directory's genuine fields, player count only);
  Enter no longer joins while the popup owns the key.
- Privacy pinned by test: `/api/servers` carries no pilot names and an opted-out
  server never matches the query.

**Adaptation note (honest scope):** native's details flow downloads the
mission's details files (`BeginEventDetailsDownload`, :1308-1326) for the Zone
lobby; the port has no details-file hosting, so its popup shows the directory's
genuine per-listing fields (game/server/address/core/version/players/pilots).
The find-player lookup itself achieves native's outcome over real data.

**Tests:** `P761LobbySortAndSearchTests` (search hit/miss/case-insensitivity,
details lines); `tests/Allegiance.Lobby.Tests/P761PlayerListTests.cs` (registry
persist + heartbeat refresh + sanitization); `LobbyHeartbeatTests.
Heartbeat_publishes_the_player_list_to_the_directory` (server → directory);
`LivePlayerNames_tracks_joined_peers` (server snapshot).
**Screenshots:** [lobby-findplayer-720p.png](P761_assets/lobby-findplayer-720p.png),
[lobby-found-720p.png](P761_assets/lobby-found-720p.png),
[lobby-details-720p.png](P761_assets/lobby-details-720p.png).

---

## Row 4 — lobby / Directory filters details and player search → **partial** (P761b)

Same code as rows 2–3 (`gamescreen.cpp:1308-1375`); this row's evidence is the
same: the browser against the REAL `Allegiance.Lobby` app in the proof run
(registered via its real `ServerRegistry`), the sort stack + reset, the
find-player round trip end-to-end, and the details panel. **P761b: `partial`** —
it inherits row 2's missing eleven boolean filters and the three non-selectable
sort columns, and the details/search data is deliberately narrower than native's
(no rosters published; find-player is the per-name query).

---

## Row 5 — Team lobby / Native team configuration and roster controls → **done**

**Native:** `WinTrek/teamscreen.cpp:1554-1573` (`UpdateButtonStates`) and the
button handlers `:2773-2815` (`OnButtonTeamReady` FORCE_TEAM_READY,
`OnButtonAutoAccept` AUTO_ACCEPT fAutoAccept+iSide, `OnButtonLockLobby`
LOCK_LOBBY fLock, `OnButtonLockSides` LOCK_SIDES fLock). The port's server
ALREADY handles all three messages (`Server.Dispatch.cs:1432` AUTO_ACCEPT —
relay + `Side.AutoAccept` + accept sweep; `:1239` LOCK_LOBBY; `:1289`
LOCK_SIDES) — no server work was needed.

**Port:**
- `src/Allegiance.ClientCore/State/LobbyScreenState.cs` — `BuildTeamControls`:
  the exact visibility/enable matrix (Team Ready + Auto-Accept visible only to
  the leader of the viewed side, enabled only before `Stage >= Started`;
  Auto-Accept also needs `!bLockGameOpen`; Lock Lobby enabled for the mission
  owner (`AmMissionOwner`, the SET_MISSION_OWNER(123) tracking) while
  `!bLockGameOpen`, checked mirrors `LobbyLocked`; Lock Sides owner-only,
  mirrors `SidesLocked`), plus `DisplayLines` (one line per VISIBLE control —
  hidden controls are omitted, like native's SetHidden).
- `bLockGameOpen` is now real data: `MissionDef` decodes bit 18 of the
  MissionParams bool:1 block (igc.h:1139; byte +2, bit 2, MSVC LSB-first) into
  `MissionInfo.LockGameOpen` (decode + encode round trip; the port's own
  encoder zeroes it — its server config has no such creation flag yet, so it
  reads false from our servers, which is the truth).
- `Game1.Panes.cs` — `UpdateReadyCheck` handles `AutoAcceptToggle` (U),
  `LockLobbyToggle` (N), `LockSidesToggle` (H): the matrix decides whether the
  key fires (a hidden/disabled control does nothing, like native's
  SetEnabled), and the sends are `SendAutoAccept/SendLockLobby/SendLockSides`
  (already on `ConnectionSession`). `DrawReadyCheck` appends the control rows.

**Tests:** `P761TeamLobbyControlsTests` (10) — the full matrix (leader vs
non-leader, in-progress, bLockGameOpen gate, owner gating, authoritative
mirrors, viewed-side visibility, DisplayLines omission) + the wire shapes
(AutoAccept fAutoAccept+iSide; LockLobby/LockSides fLock) + the MissionDef bit
round trip.
**Screenshot:** [readycheck-controls-720p.png](P761_assets/readycheck-controls-720p.png)
(AUTO-ACCEPT was toggled through the real U handler; the `[x]` is the server's
authoritative echo).

---

## Row 6 — Character-info / Profile editing and apply → **partial**

**Native:** `WinTrek/CharInfoScreen.cpp:837-880` — Edit (`OnButtonEdit`)
makes the description editable + focused, hides Edit / shows Apply and clears
the display string; Apply (`OnButtonApply`) makes it read-only, copies the
text, swaps the buttons back, then sends `CHARACTER_INFO_EDIT_DESCRIPTION` to
the club server.

**Port:**
- `Rendering/PilotInfoLayout.cs` — `ProfileEditor`: the state machine with the
  native button/pane flags (`Snapshot`: ReadOnly, EditVisible/Enabled,
  ApplyVisible/Enabled), empty draft on BeginEdit (native `SetValue("")`),
  Apply copies the draft and returns it, Cancel, capped Append/Backspace.
- `Rendering/PilotInfoOverlayRenderer.cs` — the sheet gained the description
  section (read-only text, or the draft + apply hint while editing).
- `Game1.FlightInput.cs` / `Game1.cs` / `Game1.Session.cs` — Ctrl+P toggles
  edit→apply on the visible sheet (new `PilotProfileEdit` action), Enter
  applies, Esc cancels (Escape-chain `None` case), Backspace edits, typing
  flows through `OnTextInput`, and editing counts as a composing surface
  (flight input blocked).
- Persistence: `IntroPreferences.PilotDescription` in the client's
  `player.json` — **local only**. Native's `CHARACTER_INFO_EDIT_DESCRIPTION`
  goes to the Zone club server, which this port does not have; no other player
  can see the text. **The row stays `partial` for exactly that reason.**

**Tests:** `P761PilotProfileTests` (7) — initial state, the Edit and Apply
toggles (the state toggles the row asks for), Apply-noop outside editing,
Cancel, cap/typing gate, prefs persistence round trip.
**Screenshot:** [pilotinfo-profile-720p.png](P761_assets/pilotinfo-profile-720p.png).

---

## Row 7 — commands / TK_StartChat (P743's follow-up test) → **done**

**Native:** `WinTrek/WinTrek.cpp:9441-9446` — `StartChat()` + input focus off
the flight controls.

**Port:** the behaviour was already in `UpdateFlying`; what P743 asked for was
the handler-level test. **P761b:** the seam is master's P757
`Game1.HandleChatCompose` — P761's own duplicate `StepChatCompose` extraction
was removed in the merge (see the review section) — plus `Game1.GateCompose`
(the composing gate: every keystroke flag neutralised, persistent throttle rides
through). The handler now ALSO owns the profile editor's Enter/Backspace edges
(row 6). The `XnaKeys.Back` raw check is justified in the P625b raw-key
allow-list (text-entry rule).

**Tests:** `P761StartChatComposeTests` (10) — open on the fresh StartChat edge,
send while composing, held-Enter no-refire, modal suppression, backspace edges,
Tab cycle gating, the compose gate (flight input blocked, throttle preserved),
the profile editor owning Enter/Backspace without opening chat, Esc-cancel via
`MainMenuRules.Escape` → `EscapeUiAction.Chat` (focus returns), and a loopback
compose-send round trip (real server + `ConnectionSession`: the composed line
reaches the chat log).

---

## Row 8 — commands / TK_RollModifier → **done**

**Native:** `WinTrek/WinTrek.cpp:10825-10840` (SenseJoystick: modifier held →
`jsValues[c_axisYaw]=0`, the X axis feeds `c_axisRoll`; released → X feeds yaw
and axis 2 feeds roll) and `:10895-10903` (internal camera: with the modifier
held, TK_YawRight/TK_YawLeft drive roll); name "Roll Modifier"
(`trekinput.cpp:183,1437`); `TK_RollModifier = 208` (`Igc/igc.h:434`).

**Port:**
- `Flight/FlightInput.cs` — `FlightInputState.RollModifier`; `Translate`: while
  held the yaw-axis contribution (opposing keys already cancelled) moves to
  roll and yaw is 0; released, unchanged.
- `Game1.FlightInput.cs` — the mouse virtual-joystick block: modifier held →
  mouse X deflection feeds ROLL and yaw is 0 (the exact SenseJoystick rule for
  mouse/keyboard-axis input).
- `InputActionCatalog.cs` — `RollModifier` in MOVEMENT, **unbound by default**
  (`U()`, the MouseWheel precedent): this checkout ships no
  `defaultinputmap.mdl`, so native's default (if any) cannot be copied; the
  action is fully rebindable via the F6 Controls flow and the catalogue names
  the TK identity.

**Tests:** `P761RollModifierTests` (5) — both states (held: yaw keys → roll,
yaw 0; released: X → yaw, roll keys → roll), composition with the roll keys and
opposite-cancel through the modifier, the catalogue entry (unbound, TK-named),
and a rebind flowing through the live action map.

---

## Track coordination

`git branch --list "pi/*" --no-merged master` reviewed at start: P757b
(options menu, chat filter, CommandOrderResolver), P759 (lobby/briefing, LOD
commands) and the HUD waves touch none of this task's functions. The files
changed here (`Game1.Session.cs`, `Game1.Panes.cs`, `FlightInput.cs`,
`LobbyScreenState.cs`, `MissionDef.cs`, heartbeat/registry) were untouched by
those branches at dispatch.

## Out of scope (unchanged)

Leaderboard/career-stats backend, training screens and `TK_*TM`, team chat
compose recipient (P757b), lobby chat relay (P760), timed side assignment,
HUD and simulation rows. Native's details-file download (row 3) is an
adaptation, noted above.

## Scorecard (before → after)

`python3 scripts/parity-scorecard.py` (parity-matrix.csv only):

| area | before weighted | after weighted | before unweighted | after unweighted |
| --- | --- | --- | --- | --- |
| commands | 95.09% (562/591) | **96.28% (569/591)** | 93.16% (177/190) | **94.21% (179/190)** |
| lobby | 69.96% (32.880/47) | **77.05% (36.214/47)** | 66.67% (22/33) | **72.73% (24/33)** |
| screens | 70.78% (80.693/114) | **73.82% (84.160/114)** | 68.13% (62/91) | **71.43% (65/91)** |
| TOTAL | 76.93% (1397.737/1817) | **77.69% (1411.538/1817)** | 73.22% (462/631) | **74.33% (469/631)** |

Rows updated: `TK_StartChat` partial→done, `TK_RollModifier` missing→done,
character-info profile editing missing→**partial**, team pane sort headers
partial→done, server-list sort/filters partial→done, find-player/details
partial→done, directory filters/player search partial→done, team lobby
configuration controls partial→done.

## Verification

- **Whole-solution build** `dotnet build AllegianceCore.slnx -c Release`:
  **0 warnings, 0 errors**.
- **AOT publish** (`Allegiance.Server`, `PublishAot=true`, linux-x64
  self-contained): clean, **no IL2026/IL3050** (log:
  `~/allegiance-evidence/P761/aot-publish.log`). Required — `Allegiance.Server`
  changed (heartbeat player names + live name snapshot).
- **New/affected tests 3× alone, all green:**
  - `Allegiance.ClientCore.MonoGame.Tests` (new P761 classes + affected
    TeamPane/TeamPaneLayout/PilotInfo/LobbyFilter/FlightInput/P625b/
    HelpOverlay/P678/P680/P713): 218/218 × 3.
  - `Allegiance.Core.Tests` (LobbyHeartbeatTests + LivePlayerCountTests):
    9/9 × 3.
  - `Allegiance.Lobby.Tests` (P761PlayerListTests): 3/3 × 3.
  - `Allegiance.ClientCore.Tests` (P713LobbyScreenState + P678LobbyMessage +
    P718): 45/45 × 3.
  - Showcase never-parks test (P730ShowcaseLiveTests, load-sensitive — flight/
    sim paths touched by the RollModifier + compose changes): **2/2 × 5**, all
    runs ~2m49s (no drift against the base rate).
- **Fast tier** `tools/run-tests.sh --tier fast`: **PASS, 9070 tests** (~272 s)
  — summary `~/allegiance-evidence/P761/fast-tier.log`. (The first run failed
  one pre-existing-style test: `HelpOverlayTests` — the five new catalogue
  entries pushed the CAMERA/VIEW help group 12 px past the 90% panel band;
  fixed by growing the help panel to 92% (renderer + test) — that is the one
  renderer change outside the row list.)
- **Real-client screenshots** (1280×720 backbuffer, 483 KB total):
  [P761_assets/](P761_assets/), captured with
  [tools/p761-ui-proof](../../tools/p761-ui-proof/Program.cs) against the real
  `Allegiance.Lobby` (in-process, real `ServerRegistry` + endpoints) and the
  real `Allegiance.Server`; input driven through the real handlers.

| Visible behaviour | Capture |
| --- | --- |
| Team pane radio sort header (`[NAME] … SORT: NAME`) | [teampane-sort-720p.png](P761_assets/teampane-sort-720p.png) |
| Server browser sort header + applied stack (`[GAME] ^`, PLAYERS v) | [lobby-sort-720p.png](P761_assets/lobby-sort-720p.png) |
| Find-player popup with the typed pilot name | [lobby-findplayer-720p.png](P761_assets/lobby-findplayer-720p.png) |
| Find-player result: the lobby's per-name answer selects that game | [lobby-found-720p.png](P761_assets/lobby-found-720p.png) |
| Details popup (directory fields; player count only — no rosters) | [lobby-details-720p.png](P761_assets/lobby-details-720p.png) |
| Ready-check configuration controls (`[x] AUTO-ACCEPT` via the real handler) | [readycheck-controls-720p.png](P761_assets/readycheck-controls-720p.png) |
| Character sheet profile editor mid-edit | [pilotinfo-profile-720p.png](P761_assets/pilotinfo-profile-720p.png) |

---

# Review fixes (P761b)

Branch `pi/p761b`, created from `pi/p761` (`c59352cf`) and merged with master
(`a0f4d9fa`) first, per the brief. The merge is committed; everything below is in
the working tree (uncommitted).

**Merge resolution (the three conflicts).**
- `IntroPreferences.cs` — both properties kept (`ChatFilterPlayersOnly` from P757,
  `PilotDescription` from P761), matching the resolved copy the reviewer left.
- `Game1.FlightInput.cs` — **semantic**: master's P757 `HandleChatCompose` is the
  one extraction that survives; P761's duplicate `StepChatCompose`/
  `ChatComposeStep` were deleted. The P761 additions were re-applied on top of
  master's handler: the profile editor's Enter (apply + local prefs write) /
  Backspace (delete draft) path, and `_pilotProfile.Editing` in `composing`.
  `GateCompose` stays (it is the composing gate for the flight state, not a
  second copy of the chat block). The `P761StartChatComposeTests` class was
  rewritten to drive the real handler over a headless `Game1` (the P757 idiom)
  instead of the deleted extraction, and `P757ChatPaneTests`' headless builder
  now installs the profile editor the handler touches.
- `tests/.../P625bActionMapGuardTests.cs` — the raw-key allow-list follows
  master's extraction: one `HandleChatCompose`/`XnaKeys.Back` entry (the profile
  editor's draft delete reuses it), no `UpdateFlying`/`StepChatCompose` entries.

**One bug the merge surfaced** (fixed): with the profile editor's Apply on the
same Enter frame, `_pilotProfile.Editing` was already false when the chat gate
ran, so the same press also opened the chat composer behind the closed editor.
The gate now reads the frame-start state (`profileWasEditing`).

## 1. Server-list sort/filters row → `partial`

Correct: the row said `done` and claimed the 11 boolean filters were "in the
model with their DontCare defaults". They are placeholders, not evidence.
- The eleven native filters (developments, lives, scores-count, zone, conquest,
  deathmatch, prosperity, countdown, artifact, flags, territorial —
  `WinTrek/gamescreen.cpp:933-999`, popup `:351-529`) **cannot be expressed**: the
  directory DTO (`ServerListingDto` in `Allegiance.Lobby/ServerListing.cs`, the
  client mirror `ServerListing`) carries no game-type/goal metadata. They stay
  DontCare and exclude nothing (`LobbyGameFilters`).
- Sort columns **0 (status), 3 (skill/rank) and 7 (settings) are not selectable**
  for the same reason (`LobbyGameSort` exposes 1 name, 2 running time, 4 server,
  5 core, 6 players only).
- The lobby "Directory filters…" row (row 4) reuses that evidence, so it is
  `partial` too. Both rows now say exactly what is missing
  (`docs/parity/parity-matrix.csv`).

## 2. Team-pane sort: ship/sector by NAME, objective by command text

**Native:** `PlayerShipCompare` = `_stricmp(ShipName(p1), ShipName(p2)) > 0`
(`WinTrek/teampane.cpp:1504-1510`; `ShipName` `:1586-1621`, `<none>` for a missing
hull type), `PlayerSectorCompare` = `_stricmp(SectorName(..))` (`:1512-1518`;
`SectorName` `:1577-1583`, `"unknown"` for a NULL cluster), `PlayerObjectiveCompare`
= `_stricmp(CurrentCommandText(p1), CurrentCommandText(p2))` (`:1528-1534`;
`CurrentCommandText` `:1660-1676` = `szVerb` + `" "` + the target model's name, `""`
when the command is illegal, the bare verb with no target; verb table
`c_cdAllCommands[].szVerb`, `Igc/common.cpp:41-52`).

**Port:**
- `Rendering/TeamPaneSort.cs` — `Row` grew the three native string keys
  (`ShipName`, `SectorName`, `Objective`) and `Ordered` compares them with
  `StringComparer.OrdinalIgnoreCase` (`_stricmp`), replacing the old hull-id /
  sector-id proxies and the unsorted objective column.
- `TeamPaneLayout.SortRow(world, entry, hullName)` — the ONE resolver for a row's
  sort keys (hull NAME from the parsed core, sector NAME from
  `ClientWorld.Clusters`, objective from `ClientWorld.AcceptedCommandText`). Both
  the drawn list (`Game1.Rendering`) and the selection roster
  (`TeamPaneControls.BuildRoster`) go through it, so the cursor and the rows can
  never disagree.
- **The objective text is real data, end to end.** Native forwards every ship's
  accepted-command change to the whole side (`FedSrvSiteBase::CommandChangedEvent`,
  `FedSrv/FedSrv.CPP:13695-13732`) and the client applies it to whichever ship it
  names (`clintlib/appmsg.cpp:1831-1855`, `if (pship != m_ship)
  pship->SetCommand(...)`). The port had neither half:
  - `Allegiance.Server/Server.CommandRelay.cs` (new) — a per-tick edge sweep that
    latches each ship's `c_cmdAccepted` triple (NPC slot `AcceptedCommandId`/
    `AcceptedTarget`, player slot `AcceptedShipCommandId`/`AcceptedShipTarget`)
    and sends `ORDER_CHANGE(93)` to that side's connected peers on change
    (guaranteed, `CFSSide::GetGroup`, `FedSrv.CPP:13724-13731`). Only transitions
    are sent (native's event semantics); native's other arm (`c_cmdCurrent` for an
    AoE-weapon player, `:13703-13705`) is not relayed — the port's `c_cmdCurrent`
    is the client-local weapon-aim slot and the objective text reads
    `c_cmdAccepted` only.
  - `Core/Wire/OrderChange.cs` — the verb table (`CommandVerbs`,
    `Igc/common.cpp:41-52`) and `AcceptedCommandText(commandId, targetName)`.
  - `ClientCore/State/ClientWorld.Orders.cs` — `Apply(OrderChange)` now stores the
    accepted slot on ANY named ship (native) and `AcceptedCommandText(shipId)` /
    `TargetNameFor(type, id)` compose the text; `ShipEntity` carries
    `AcceptedCommandId`/`AcceptedTargetType`/`AcceptedTargetId`.

**Tests:** `P761TeamPaneSortTests` — `ShipSort_ByShipName_CaseInsensitive_NotByHullId`,
`SectorSort_BySectorName_CaseInsensitive_NotById`,
`ObjectiveSort_ByAcceptedCommandText_CaseInsensitive` (ids deliberately opposite to
the names, so an id sort would reverse them),
`RelayedOrderChange_AppliesToATeammatesShip_AndBecomesItsObjectiveText`,
`SortRow_ResolvesSectorNameAndObjective_FromTheWorld`,
`Layout_SortsPilotRowsByShipAndSectorName`. `P761bCommandRelayTests` — the verb/text
table against `Igc/common.cpp:41-52` and a real two-client loopback proof: A accepts
an attack order on B's ship, the server relays it, and B's client reads
`"attack relayB"` — the exact text the pane now sorts by.

**Scope note (honest):** the objective column sorts by the text; the pane does not
draw an OBJECTIVE cell per row (native draws an icon from
`c_cdAllCommands[cid].szIcon`; the port's ASCII pane shows name/rank/status/kills/
deaths/wing as before). The build-order target (`c_cidBuild` on an asteroid) is not
stored in a ship slot in this port, so a builder's text is the bare verb.

## 3. Privacy: no rosters on the open listing; find-player is a per-name query

The reviewer is right: publishing every server's pilot list on open `/api/servers`
exposes the whole player base, while native's FIND_PLAYER asked the lobby about
**one** pilot (`WinTrek/gamescreen.cpp:1348-1352` → `FM_C_FIND_PLAYER`).

- `Allegiance.Lobby/ServerListing.cs` — `ServerListingDto` has **no player field**
  at all (count only). The names live on the internal listing and only for a
  server that opted in: `RegisterRequest.PublishPlayers` (default `false`;
  `ServerRegistry` stores an empty list when it is not set).
- New `GET /api/servers/findplayer?name=` → `FindPlayerResultDto` (id, name,
  address, port, maxPlayers, mission, core, version): case-insensitive **exact**
  match, only servers that opted in, only the server(s) that pilot is on, nothing
  about any other player. Rate-limited (`RateLimiting.FindPlayerPolicy`, same
  per-IP window as registration) so it is not a cheap roster oracle.
- `Allegiance.Server` — `LobbyConfig.PublishPlayerNames` (default **false**) is the
  per-server opt-in; `LobbyHeartbeat` only samples/sends the name snapshot (and
  the `publishPlayers` flag) when it is on. With no authentication for
  self-hosted servers, default-off is the honest default.
- Client — `ServerListing` lost `Players`/`PlayerNames`; the find-player popup's
  Enter now performs the lobby round trip (`ServerDirectorySearch.
  FindPlayerRequestPath`) and the staged reply selects the answering row
  (`IndexOfListing`); `DetailsLines` shows the fields plus the player COUNT.
- **Tests:** `tests/Allegiance.Lobby.Tests/P761PlayerListTests.cs` —
  `PublicListing_CarriesNoPilotNames_EvenWhenTheServerReportsThem` (asserts the raw
  `/api/servers` JSON contains no name and no `players` field),
  `FindPlayer_AnswersWithOnlyTheServerTheNamedPilotIsOn` (case-insensitive exact,
  partial name is a miss, 400 without a name),
  `FindPlayer_NeverMatches_AServerThatDidNotOptIn`,
  `Heartbeat_refreshes_the_player_list_in_place`,
  `Player_names_are_sanitized_and_capped`. `LobbyHeartbeatTests` — the opted-in
  heartbeat answers the query while `/api/servers` stays clean, and the default
  (no opt-in) publishes nothing.

## 4. Screenshots (re-shot; every image opened and described below)

Code changes behind them:
- **Server browser** (`Game1.Session.cs`, `DrawLobby`): the top band is now one
  line per row (title 16, polling state 52, player 74, sort-header bar 98,
  filter/find line 122, banner 146) so the column header no longer prints through
  "Polling …". The details panel is sized/positioned **before** the rows: it owns
  the right-hand column, the row band and highlight stop 12 px short of it, and
  long rows are fitted to that width, so the panel can never cover the selected
  row (and it is drawn after the rows, so nothing prints over it). The footer was
  shortened so it fits 1280 px.
- **Character sheet** (`PilotInfoOverlayRenderer`): each line now advances by its
  FONT's line spacing (the big name+side line used to advance a small-font row),
  so the name/"S0" title no longer overlaps `RANK:`; the panel height is measured
  from the same metrics.
- **Briefing** (`Game1.Rendering.cs`): the mission briefing is hidden while the
  character sheet or the team pane is up (`paneReplacesBriefing`) — native shows
  those as full screens, so the port's overlays must not have the briefing behind
  (or over) them. It is hidden, not dismissed: the countdown and text survive and
  it reappears when the pane closes.
- **Team pane** (`TeamPaneLayout` + `TeamPaneRenderer`): long lines wrap at 96
  columns in the layout and are hard-fitted (binary-search `Fit`, `…`) to the panel
  width in the renderer — the hint line can no longer run off the right edge. The
  panel alpha went 0.94 → 0.985 (native's pane is an opaque surface) so the flight
  HUD behind it (the ENERGY gauge label sits under the sort header) no longer shows
  through the text.

| Capture (all re-shot, 1280×720) | What is actually visible |
| --- | --- |
| [lobby-sort-720p.png](P761_assets/lobby-sort-720p.png) | "SERVER BROWSER", "Polling http://…/api/servers" on its own line, then the sort bar `[GAME] ^ RUNNING SERVER CORE PLAYERS v (LEFT/RIGHT choose, SPACE sort)`, three rows with the first highlighted, and a footer fully inside the frame. |
| [lobby-findplayer-720p.png](P761_assets/lobby-findplayer-720p.png) | The find-player line "Find player: PilotTwo_  (ENTER asks the lobby, ESC close)" under the sort bar; the list is unfiltered. |
| [lobby-found-720p.png](P761_assets/lobby-found-720p.png) | After Enter: the popup is closed and the lobby's answer selected the third row, "Domino's Fortress 10.0.0.1:6073 3 players Wing War". |
| [lobby-details-720p.png](P761_assets/lobby-details-720p.png) | The details panel on the right (GAME/SERVER/ADDRESS/CORE/VERSION/PLAYERS: 3) with the selected "Domino's Fortress" row fully visible to its left, not covered. |
| [teampane-sort-720p.png](P761_assets/teampane-sort-720p.png) | TEAM PANE, the header `[NAME] RANK KILLS SECTOR SHIP WING CREDITS OBJECTIVE  SORT: NAME` (no HUD bleed, no overprint), the roster row, MY MONEY, the controls hint wrapped over two lines inside the panel, the chat/moderation notes — and no mission briefing behind it. |
| [pilotinfo-profile-720p.png](P761_assets/pilotinfo-profile-720p.png) | CHARACTER SHEET header, the big "P761 UI pilot  S0" title, `RANK: Newbie` on its own line (no overlap), the stat rows right-aligned, and the profile editor mid-edit ("DESC: flying PCore001 since the parity waves_" + "EDITING ENTER/CTRL+P APPLY ESC CANCEL"); the briefing that used to cover the sheet is gone. |
| [readycheck-controls-720p.png](P761_assets/readycheck-controls-720p.png) | Unchanged by this round: the ready-check overlay with `[x] AUTO-ACCEPT U` from the real handler echo (re-shot for consistency). |

Evidence: the PNGs are the committed assets above (496 KB total, limit 2 MB); the
capture run's console log is at `~/allegiance-evidence/P761b/ui-proof.log`.

## Verification (P761b)

- **Whole-solution build** `dotnet build AllegianceCore.slnx -c Release`:
  **0 warnings, 0 errors**.
- **AOT publish** (`Allegiance.Server`, `PublishAot=true`, linux-x64,
  self-contained): clean, exit 0, **0 IL2026 / 0 IL3050** — required because
  `Allegiance.Server` changed (the accepted-command relay) and the lobby's DTO
  changed. Log: `~/allegiance-evidence/P761b/aot-publish.log`.
- **Every new/affected test 3× alone, green:**
  - `Allegiance.ClientCore.MonoGame.Tests` (P761TeamPaneSort, P761StartChatCompose,
    P757ChatPane, P761LobbySortAndSearch, P761PilotProfile, TeamPaneLayout,
    TeamPaneControls, P625bActionMapGuard, ConnectScreenLayout): **73/73 × 3**.
  - `Allegiance.Core.Tests` (P761bCommandRelay + LobbyHeartbeat + LivePlayerCount):
    **13/13 × 3**.
  - `Allegiance.Lobby.Tests` (whole project, incl. P761PlayerList): **24/24 × 3**.
  - `Allegiance.ClientCore.Tests` (order/command/lobby/P718/P755 filter — the
    ClientWorld ORDER_CHANGE path): **189/189 × 3**.
- **Fast tier** `tools/run-tests.sh --tier fast`: **9,317 tests, 0 failed, 296.7 s**.
  The runner reports FAIL only because its calibrated 10 s fast-limit guard flagged
  one UNRELATED live test at 25.3 s
  (`CrossSideCombatVisibilityLiveTests.CrossSide_sector_stays_correct_across_warp_return`;
  the runner's own quarantine retry passed it, and it passes **3/3 alone in ~1 s
  each**) while other agents' suites were hammering the machine. An earlier sweep in
  this round hit the same guard on a different unrelated test
  (`P750NativeAsteroidTypesTests.Research_showcase_rocks…`, 10.311 s, 3/3 alone in
  ~1 s). Both are documented load flakes in `docs/TEST_FLAKES.md`; no test, budget
  or limit was changed. Log: `~/allegiance-evidence/P761b/fast-tier.log`.
- Showcase never-parks test: **corrected in P761c** — this claim was wrong: the
  relay's `acceptedCommandSweep` is a sim-thread step (`Server.Broadcast.cs`), so
  the sweep does run in the sim path (and did allocate per tick). P761c reuses its
  scratch and ran the test 5× (see the P761c verification section).

## Scorecard (P761b)

`python3 scripts/parity-scorecard.py`, before = the merge commit (P761 + master),
after = this round:

| area | before weighted | after weighted | before unweighted | after unweighted |
| --- | --- | --- | --- | --- |
| lobby | 77.05% (36.214/47) | **72.80% (34.214/47)** | 72.73% (24/33) | **69.70% (23/33)** |
| screens | 82.74% (94.328/114) | **81.57% (92.995/114)** | 82.42% (75/91) | **81.32% (74/91)** |
| TOTAL | 79.46% (1443.706/1817) | **79.27% (1440.373/1817)** | 77.02% (486/631) | **76.70% (484/631)** |

The score goes **down** on purpose: two rows that P761 had marked `done` are now
`partial` because the review is right that 11 filters and 3 sort columns are not
expressible from the directory payload. Nothing was marked `n/a` to shrink the
denominator; the team-pane row stayed `done` and gained real data (command text).

## Still open / not done

- The eleven boolean server filters and sort columns 0/3/7 need game-type/goal
  metadata on the directory payload (a lobby + server + client change across
  `RegisterRequest`/`ServerListingDto`/`ServerListing`/`LobbyGameFilters`); out of
  scope for this fix round.
- The team pane draws no OBJECTIVE cell (sort only) — an ASCII icon/text column is
  a layout task of its own.
- Find-player's privacy model assumes self-hosted servers cannot be authenticated;
  if the lobby ever gains server auth, the opt-in can become an enforced policy.
- Character-info description is still local-only (unchanged P761 `partial`).

## Lane / evidence notes

- Ran nothing on a remote host; no rig, mini-runner or Mac access. Scratch work
  lived in `.scratch/` inside the worktree (excluded, deleted at the end); raw
  logs/TRX live in `~/allegiance-evidence/P761b/`.


# Review fixes (P761c)

Branch `pi/p761c`, created from `pi/p761b` (`6ca90d4a`) and merged with master
(`4bf6b10a`) first; the merge is committed (`d37c1318`). Everything below is in
the working tree.

**Merge resolution (three conflicts).**
- `docs/parity/parity-matrix.csv` — kept master's P759 `TK_ToggleLODSlider`
  `done` row (newer) and P761's `TK_StartChat` `done` row (the P761 test proves
  the handler), rather than master's older `partial`/`missing` pair.
- `Game1.Session.cs` `OnTextInput` — kept BOTH: HUD-10's `PlayChatKey()` inside
  the chat composer and P761's character-sheet profile-editor draft after it.
- `HelpOverlayTests.cs` — master's production-shaped `LayoutSections` call with
  P761's 0.92 panel height (matching `HelpOverlayRenderer.cs`).

**Merge fallout fixed.** Master's P281 side-select hold means a logged-on peer no
longer spawns until it sends `POSITIONREQ`; two P761 live tests still waited for
`MyShipId` without it. They now send the real `SendPositionReq(-1)` the client
sends (`P761bCommandRelayTests.Logon`, `P761StartChatComposeTests` send round
trip). The first fast-tier run caught the latter; the final run is green.

## 1. The relay no longer echoes back to the commanded ship's own client

**Native:** the server forwards a ship's `c_cmdAccepted` change to the whole side
(`FedSrv/FedSrv.CPP:13695-13732`), but the client applies `ORDER_CHANGE` only when
`pship != m_ship` (`clintlib/appmsg.cpp:1836-1849`, the guard at `:1838`); the own
ship's text comes from the client-local accepted slot.

**Port before:** `SendAcceptedCommand` sent to every connected side peer including
the commanded ship, and `ClientWorld.Apply` wrote the echo into `MyAcceptedCommand`.
For a station/asteroid/warp order the relay carried `(cid, -1, -1)`, wiping the
target the client had just set locally (the HUD's "Current orders" lost it).

**Fix:**
- `Server.CommandRelay.cs` `SendAcceptedCommand` skips the commanded ship's own
  connection (and reuses a `List<ConnectionId>` instead of a LINQ `ToArray`).
- The P760 `OnCommandChanged` callback in `Server.Combat.cs` has the same
  native own-ship skip (both `c_cmdAccepted` and `c_cmdCurrent`), since it is the
  other ORDER_CHANGE relay path.
- `ClientWorld.Orders.cs`: the entity slot is written only when
  `order.ShipID != MyShipId`; the own ship's `AcceptedCommandText` reads
  `MyAcceptedCommand` (set locally by the issue/accept path, exactly native's
  `SetAccepted`).

**Test:** `P761bCommandRelayTests.QueuedStationGoto_KeepsOwnTarget_AndRelaysTheStationToTheTeammate`
— a real two-client loopback: the server queues a station goto on A, A accepts it
through `AcceptQueuedCommand` + the `c_cmdQueued` ORDER_CHANGE, then after 40
ticks A's `MyAcceptedCommand` still holds `(c_cidGoto, OT_station, stationId)` and
A's own entity slot is untouched, while B reads `goto <station>`. Before the fix
the station-target assert fails (the echo replaces it with OT_invalid/NA). The
existing `AcceptedOrder_OnOnePlayer_ReachesTheTeammate_WithItsCommandText` now
also pins that A's own entity slot is not written by the echo.

## 2. The relay sends the target's REAL type, and keeps non-Model targets

**Native:** `SetCommand` carries an `ImodelIGC` for every target kind, and the
server relays `pfmOC->objectType/objectID` (`FedSrv.CPP:13699-13732`); the team
pane's `ShipName`/`CurrentCommandText` then print the model name
(`teampane.cpp:1586-1621, 1660-1676`).

**Fix:**
- `Server.CommandRelay.cs` `AcceptedCommandOf` derives the type from the model
  with `EquipmentTarget.TypeOf` (Ship / Probe / Mine / an `EquipmentTarget`'s
  real `OT_station`/`OT_asteroid`/`OT_warp`) instead of hardcoding `Ship`.
- `Ship` gained the accepted/queued WIRE triples
  (`AcceptedShipWireType`/`AcceptedShipWireId`,
  `QueuedShipWireType`/`QueuedShipWireId`). `QueueCommandForPlayer` stores the
  real wire `(ot, oid)` (its Model branch also uses `EquipmentTarget.TypeOf`,
  so a Probe is not mislabelled), and the queued → accepted promotion
  (`WinTrek.cpp:10668-10681`) copies the triple forward. A station/asteroid/warp
  therefore relays as `(goto, OT_station, id)`, not `(goto, -1, -1)`.

**Tests:** `P761bCommandRelayTests.AcceptedCommandOf_DerivesTypeFromTheModel_AndKeepsTheWireTarget`
covers a ship (`OT_ship`), a probe (`OT_probe`, a Model but not a Ship) and a
station (no Model; wire triple). The loopback station test above proves the whole
path end to end and B's pane text `goto Garrison`.

## 3. `SweepAcceptedCommands` no longer allocates every tick

`Server.CommandRelay.cs` now reuses three fields — a `HashSet<Ship>` for the live
set, a `List<Ship>` for the gone-latch pass, and a `List<ConnectionId>` for the
send targets — instead of allocating a new `HashSet` and two LINQ `ToArray`s per
tick. The gone pass uses an explicit loop (no `Keys.Where(...).ToArray()`).

**Correction to the P761b report:** the sweep runs inside
`Server.Broadcast.cs`'s `acceptedCommandSweep` step, i.e. on the sim thread, so
P761b's “no bot/flight/sim path changed” line was wrong. The P761b verification
line above is corrected, and the load-sensitive showcase never-parks test was
run 5×: **5/5 PASS** (1 m 47 s – 1 m 48 s each, the usual base rate).

## 4. The team pane draws the names it sorts by (and the objective text)

`TeamPaneLayout.Build` now renders the same resolved strings the sorts compare:
the sector NAME (`SectorName()`, `teampane.cpp:1577-1583`), the hull NAME
(`ShipName()`, `:1586-1621`) and the accepted-command text
(`CurrentCommandText`, `:1660-1676`) in an `OBJ:` cell. The old `SEC 1053
HLD13 ID:8096` id tail is gone. The columns were tightened so the `OBJ:` cell is
visible before the role/wing tail (the renderer still fits/truncates the tail at
the panel edge).

**Test:** `P761TeamPaneSortTests.Layout_RendersResolvedSectorAndHullNames_AndTheObjectiveText_NotIds`
and the updated `TeamPaneLayoutTests` (Ceto/Titan/`OBJ:defend Bob`, no `SEC 1`,
`HLD301`, `ID:42`).

**Screenshot:** [teampane-sort-720p.png](P761_assets/teampane-sort-720p.png)
(1280×720, real client over loopback to the real server; opened and described
here). What is visible: the dark near-opaque TEAM PANE panel with the
`[NAME] RANK KILLS SECTOR SHIP WING CREDITS OBJECTIVE  SORT: NAME` header, `SIDE 0
(players: 1)`, and the row `P761 UI pilot  Newbie  Ceto  Adv Scout  K:0
D:0  OBJ:goto Garrison [COMMANDER] W...` — i.e. the sector name (Ceto), the hull
name (Adv Scout) and the relayed objective text (goto Garrison), with only the
WING tail truncated by the renderer's fit. Above the panel, the real HUD shows
`Current orders: goto Garrison`, confirming the own ship's text came from
`MyAcceptedCommand` and survived. `MY MONEY: 18400`, the wrapped controls hint and
the moderation notes are inside the panel; the flight gauges (THRUST/FUEL) are
visible only OUTSIDE it — no HUD label prints through the panel.

## 5. A missing hull reads `<none>`; no catalog falls back to the id text

Native `ShipName` returns `<none>` when `GetHullType` is NULL
(`teampane.cpp:1586-1621`, `:1612`). `Game1.Panes.HullNameForSort` now returns
`<none>` for a hull the parsed core does not contain, and `HLD<id>` when there is
no shop catalog at all; `TeamPaneLayout.ResolveHullName` implements the same rule
for `SortRow` (null resolver → `HLD<id>`, resolver miss → `<none>`), so the SHIP
sort still has a stable key. Empty `""` is no longer produced.

**Test:** `P761TeamPaneSortTests.SortRow_MissingHullReadsNone_AndNullCatalogFallsBackToTheHullIdText`.

## 6. Panels: opaque backgrounds, value clamped inside the border

- The character sheet's panel alpha is now 0.995 (was 0.92) and the team pane's
  0.995 (was 0.985); `PilotInfoOverlayRenderer.PanelAlpha` /
  `TeamPaneRenderer.PanelAlpha` are pinned by
  `PilotInfoLayoutTests.TeamPaneAndCharacterSheetPanels_AreNearOpaque`.
- The character-sheet stat values right-align to the panel's inner right edge
  (`textX + contentW`) and are fitted first if wider than the content column,
  instead of the old `contentW - Padding` x that put the widest value over the
  border. `PilotInfoOverlayRenderer.ValueLeft` is the tested rule
  (`StatValue_RightAlignsInsideTheInnerPanelWidth`).

**Screenshots (re-shot, opened and described):**
- [pilotinfo-profile-720p.png](P761_assets/pilotinfo-profile-720p.png) — the
  CHARACTER SHEET panel with `P761 UI pilot  S0`, `RANK: Newbie`, the stat rows
  and `COMBAT RATING 0.0`: the `0.0` now ends inside the blue right border. The
  profile editor shows `DESC: flying PCore001 since the parity waves_` and
  `EDITING  ENTER/CTRL+P APPLY  ESC CANCEL`. The panel is near-opaque: the HUD's
  AMMO/ENERGY/ARMOR/THRUST labels and gauge arcs are NOT visible inside it (the
  fragments that remain are outside the panel's right edge); outside, the HUD
  still shows `Current orders: goto Garrison` and the gauges.
- [teampane-sort-720p.png](P761_assets/teampane-sort-720p.png) — described in
  item 4; no flight-HUD text shows through the team pane surface.

## 7. `_findPlayerQuery` is staged under the volatile flag

`QueryFindPlayerAsync` (worker) now writes the asked name to a new
`_findPlayerQueryStaged` field and only then sets the volatile
`_findPlayerResultPending`; the main thread copies it into `_findPlayerQuery` when
it consumes the reply. `_findPlayerQuery` is now written only by the main thread
(open/close/consume), removing the two-writer race with `UpdateLobby`. This is a
data-race fix with no deterministic unit test: the earlier code was a plain
non-volatile field written from both threads, and a timing test would be a load
flake. Verified by inspection plus the full fast tier.

## 8. Stray brief

Deleted the untracked `P761B_BRIEF.md` from the worktree root. `P761C_BRIEF.md`
remains untracked and is excluded from git (never commit briefs).

## Verification (P761c)

- **Whole-solution build** `dotnet build AllegianceCore.slnx -c Release`:
  **0 warnings, 0 errors** (final).
- **AOT publish** (`Allegiance.Server`, `PublishAot=true`, linux-x64,
  self-contained): exit 0, **0 IL2026 / 0 IL3050** (Server and Core changed).
- **Every new/affected test 3× alone, green:**
  - `Allegiance.Core.Tests` `P761bCommandRelayTests`: **5/5 × 3**.
  - `Allegiance.ClientCore.MonoGame.Tests`
    (P761TeamPaneSort, TeamPaneLayout, PilotInfoLayout, TeamPaneControls,
    P675CommandAction): **67/67 × 3**.
  - `P761StartChatComposeTests`: **10/10 × 3** (merge fallout fix).
  - `Allegiance.ClientCore.Tests` (ClientWorldSoundEvent, P675CommandLive,
    ClientWorldCurrentCommand — the ClientWorld ORDER_CHANGE path):
    **99/99 × 3**.
- **One fast tier** `tools/run-tests.sh --tier fast`: **9,499 tests, PASS,
  272.0 s** (24 Lobby, 2,917+3 MonoGame, 1,575 ClientCore, 4,968 Core; quarantine
  12/12). An earlier sweep failed one test
  (`P761StartChatComposeTests.SendRoundTrip…`, “must be seated”) because of the
  P281 side-select merge; that is a real failure, was fixed with the real
  `POSITIONREQ`, and the final sweep is the PASS above.
- **Showcase never-parks 5×:** **5/5 PASS** (1 m 47 s – 1 m 48 s each), matching
  the base rate; required because the relay sweep is a sim-thread step.

**Evidence:** raw logs/TRX at `~/allegiance-evidence/P761c/`; screenshots in
[P761_assets/](P761_assets/) (496 KB total, under the 2 MB limit). The capture
run's log is `~/allegiance-evidence/P761c/ui-proof.log`.

## Matrix (P761c)

`Team pane / screen / Sort headers and player selection` stays **done**: fixes 1,
2 and 4 landed (the relay no longer wipes the own target and carries the real
station/probe/ship type; the pane draws the names and objective text it sorts by).
The lobby rows are unchanged, as instructed.

## Merge review of P761c (Claude, 2026-10-20)

- **Fixed:** `SweepAcceptedCommands` used `foreach` over `IReadOnlyList` sectors/models, which boxes an enumerator per
  sector per tick. It now uses index loops.
- **Fixed:** an accepted command relayed by P760's `OnCommandChanged` was relayed again by the sweep on the next tick
  (teammates got the same ORDER_CHANGE twice). `OnCommandChanged` now latches the relayed triple in
  `_lastRelayedAcceptedCommand`.
- **Known gap:** the server-side own-ship skip (`Server.CommandRelay.cs`) has no test of its own. The loopback test
  passes through the client guard even without it.

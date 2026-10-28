# P864d — the team lobby on the client: native lobby player list + side request, both HUD styles, live proof

Base: public `master` at dispatch (`a94c13c7`). Work done in the `allegiance-pi-p864d` worktree; nothing
committed or pushed. Native reference re-grepped this session from `~/Development/Allegiance/src/`
(read-only). **Domingo's ruling (2026-10-28) applied: everything here closes as `implemented`** — the
port's shape is the native wire path, not a "the port chose differently" substitute.

P864b ported native's team lobby on the **server** (`SIDE_TEAMLOBBY` park, `FlushSides`,
`RandomizeSides`, bot re-seat) but the **client** only showed a *count* on the port-local
`SIDE_ROSTER(211)` scalar. This task closes the client half: the team screen now lists the lobby
players **by name**, a lobby player can **request a side from it and land there**, and a team leader
sees the pool and **accepts from it** — with real-window screenshots in both HUD styles.

---

## 1. What native does (re-grepped)

| Native fact | Cite |
|---|---|
| `SIDE_TEAMLOBBY` is the mission side every "not on a team" player sits on | `Igc/igc.h:514` |
| The client's lobby side is displayed as **"Not on a team"** | `clintlib/clintlib.h:571` (`MissionInfo::SideName`) |
| The **team list is the real sides + the lobby side** | `WinTrek/teamscreen.cpp:1330-1335` (`ConcatinatedList(m_plistSides, SingletonList(GetSideInfo(SIDE_TEAMLOBBY)))`) |
| The lobby team row shows its **member-list count** | `WinTrek/teamscreen.cpp:378-386` |
| Selecting a team lists its **members + pending requests** | `WinTrek/teamscreen.cpp:2563-2567` |
| The player list draws **one row per player by name with a status column** ("Requesting" / "AFK" / "Not Playing" / "Playing" / "Ready") | `WinTrek/teamscreen.cpp:470-510` (`:495-497` is "Requesting") |
| The prompt for a player on no team is `"<name> -- you need to pick a team"`; while requesting, `"<name>, YOU ARE REQUESTING TO JOIN A TEAM"` | `WinTrek/teamscreen.cpp:1655-1671` |
| Picking a team sends `FM_C_POSITIONREQ(39)`; a leaderless side auto-accepts it | `WinTrek/teamscreen.cpp:2962-2982`; `FedSrv/fsmission.cpp:4729-4774` (`:4757`) |
| A pending request is a whole-mission `POSITIONREQ(40)`; the client adds it to the side's request list and the leader hears "X wishes to join your team" | `clintlib/appmsg.cpp:2470-2483` |
| The team leader's **Accept** button sends `FM_C_POSITIONACK(41) accepted` for the selected player | `WinTrek/teamscreen.cpp:2997-3015`; `FedSrv/FedSrv.CPP:6709-6747` |
| The lobby park exempts a player from the no-defection gate | `FedSrv/fsmission.cpp:4583-4590` |
| `FlushSides` / `RandomizeSides` park every non-leader on `SIDE_TEAMLOBBY` | `FedSrv/fsmission.cpp:5069-5119`, `:5173-5185` (dispatch `FedSrv/FedSrv.CPP:6964-6997`) |
| **Who** is in the lobby reaches the client as ordinary player info: a joining peer is a mission member on the lobby side, and `SendLobbyMissionInfo` ends with "tell them about all of the players" — one `PLAYERINFO` per ship | `FedSrv/fsmission.cpp:3936-3971` (loop `:3958-3967`), `:406-411`; `clintlib/clintlib.cpp:4082-4090` (`AddPlayerToMission` → `SetSideID(SIDE_TEAMLOBBY)`) |
| The client's per-side **count is a running count** it maintains as players join/leave (`SideNumPlayers`), not a snapshot | `clintlib/clintlib.h:578`; `clintlib/clintlib.cpp:885-898` (`AddPlayer` `:893`), `:911-931` (`RemovePlayer` `:928`) |

**Wire-path decision (item 1).** Native has no "lobby roster" message: the lobby is a side, its members
are ordinary players, and the client learns them from `PLAYERINFO(10)`/`EXPORT(26)` plus the
`QUIT_SIDE(126)`/`JOIN_SIDE(125)` relays. So this port does **not** widen `SIDE_ROSTER(211)`: the client
reads the ships it already tracks (`ShipEntity.SideId == -2`) and their names/requests. The one real gap
was structural — the port defers a joiner's ship to a P281 side-select stage, so a **still-choosing** peer
is not in `_peerShips` and never received native's join-time player list; the server now sends exactly
that (native's `SendLobbyMissionInfo` player loop) alongside the `SIDE_ROSTER` snapshot.

---

## 2. What changed

### Client — the team screen lists the lobby pool by name
- **`src/Allegiance.ClientCore/State/ClientWorld.Lobby.cs`**
  - `SideTeamLobby = -2` (documented `igc.h:514` / `missionigc.cpp:3612-3634` "Team Lobby" /
    `clintlib.h:571` "Not on a team").
  - `TeamLobbyMembers()` — the lobby pool as display rows: every known ship on `-2` (non-drone), name
    from the player-info relay, `IsSelf`, and the side it is *requesting* (from `POSITIONREQ(40)`;
    native's "Requesting" status). Name-sorted (native's name sort), buffered, read under the session
    gate like the rest of the roster.
  - `SideHumanCount(sideId)` — the port of native `MissionInfo::SideNumPlayers` (`clintlib.h:578`): the
    **running** per-side human count from the player table, `0` for the lobby side, `null` when this peer
    has no player table yet (caller falls back to the `SIDE_ROSTER` count). This removed a real
    staleness bug the screenshots exposed: a seated client's `SIDE_ROSTER` counts are frozen at its own
    join, so the team screen showed "(players: 2)" for a side that had one player left.
- **`src/Allegiance.ClientCore.Game/Rendering/SideSelectLayout.cs`** — the team screen before admission:
  - The lobby row (P864b's `"Not on a team"` + count) now also lists **each member by name** with
    native's status column (`Requesting` / `Ready`), `(you)` on the local player
    (`teamscreen.cpp:378-386`, `:470-510`).
  - Native's prompt line for a player on no team (`:1655-1671`).
  - Per-side counts come from the running `SideHumanCount` when available (`teamscreen.cpp:390-395`).
- **`src/Allegiance.ClientCore.Game/Rendering/ReadyCheckLayout.cs` + `ReadyCheckRoster.cs`** — the team
  lobby once seated gets the same pool section (`NOT ON A TEAM (players: n)` + `Requesting <side>` /
  `Not Playing` per player) for **every** viewer, plus the two team-screen buttons native offers there:
  the owner's `R RANDOMIZE TEAMS` and the leader's `A ACCEPT LOBBY REQUEST`.
  `ReadyCheckRoster.GatherLobby` reduces the world to those rows and to the first request pending on the
  local leader's own side.
- **`src/Allegiance.ClientCore.Game/Game1.Panes.cs`**
  - `DrawSideSelect` feeds the pool/prompt/running counts; `DrawReadyCheck` feeds the pool section and the
    accept hint.
  - `UpdateReadyCheck`: new `R` (mission owner, `STAGE_NOTSTARTED` → `FM_C_RANDOMIZE_TEAMS`, native's
    Randomize button) and `A` (team leader → `FM_C_POSITIONACK(41) accepted` for the first request pending
    on its own side, native's Accept button).
- **`src/Allegiance.ClientCore.Game/Game1.FlightInput.cs`** — **the routing that was missing**: a local
  ship parked on `-2` while the mission is live (a `FlushSides`/`RandomizeSides` park) now moves the client
  to the team screen (`ClientState.SideSelect`, `_sideSelectFromGameOver` = stay until admitted to an
  ACTIVE side) instead of leaving it on a dead in-flight/ready-check view. Enter then sends the ordinary
  `POSITIONREQ` (`teamscreen.cpp:2962-2982`) and the existing admission check releases it.
- **`src/Allegiance.ClientCore.Game/Rendering/InputActionCatalog.cs`** — new rebindable
  `AcceptJoinRequest` (A, READY CHECK scope); `RandomizeTeams` description widened to the team screen.

### Server — native's join-time player list for a still-choosing peer
- **`src/Allegiance.Server/Server.Lobby.cs`**
  - `SendSideRoster` now also sends native's `SendLobbyMissionInfo` closing burst: one `PLAYERINFO(10)`
    per mission ship (humans with their real side — `-2` for a parked player — and NPCs), through the same
    one-shot `_playerInfoSent` latch the join burst uses, so `CompleteJoinAndLaunch` never duplicates it.
  - `MoveToTeamLobby` refreshes the pending peers' `SIDE_ROSTER` after a park (lobby count + the same
    player burst), the pending-peer analogue of the mission-group `QUIT_SIDE` relay they cannot receive
    (pending peers are not in `_peerShips`).

No new service, port, env var or store → `docker-compose.yml` unchanged.

### Proof harness
- **`tools/p864d-teamlobby-proof/`** — real-window proof: a real `Allegiance.Server` + **two real wire
  clients** (`Allegiance.Bot`) + the real windowed MonoGame client over loopback. The first bot is side 0's
  leader/mission owner; it sends the real `FM_C_RANDOMIZE_TEAMS` (the mission's rank-balance mode runs
  `RandomizeSides`, whose phase 2 flushes every non-leader). The proof then drives the **production**
  handlers (`UpdateSideSelect`/`UpdateReadyCheck`) with synthetic keyboard states and reads the outcome
  back from the server's mission model:
  `flush → team screen lists the lobby → Down/Enter requests Bravo → lands → second bot requests →
  leader sees it and presses A → accepted`.

---

## 3. Verification (light gate)

| Check | Result |
|---|---|
| `~/.dotnet/dotnet build AllegianceCore.slnx` | **0 warnings / 0 errors** (final run; one earlier run hit a stale VBCSCompiler server, `CS2012 … Read-only file system` — `dotnet build-server shutdown` + rebuild is green, no code change) |
| `tools/build-tools.sh` | **all 92 tool projects built** |
| New tests 3× alone — `P864dTeamLobbyScreenTests` | **13/13 pass ×3** (`P864dTeamLobbyScreenTests`) |
| New tests 3× alone — `P864dTeamLobbyClientListLiveTests` | **2/2 pass ×3** (Live tier, so it runs here) |
| Affected client tests | `SideSelectLayoutTests` + `ReadyCheckLayoutTests` + `ReadyCheckRosterTests` + `P625bActionMapGuardTests` + `P764UiSoundSeamTests` + `P761TeamLobbyControlsTests` → **56/56 pass** |
| Affected server/live tests | `P775PlayerInfoMomentsTests` + `LiveSideSelectTests` + `RandomizeTeamsLiveTests` + `P852MissionRulesCertifyTests` + `LivePositionRequestTests` + `P867DispatchCertificationTests` → **104/104 pass** |
| `P832aMissionRulesInventoryTests` (native-body inventory audit) | **pass** (it caught my first SIM-0852 `native_body` value; fixed to the exact inventory key) |
| Showcase never-parks **5×** (`P730ShowcaseLiveTests.Two_local_showcase_matches_keep_mining_research_kills_pods_and_relaunch`) | **pass 5/5** — 5m09s / 4m01s / 4m31s / 3m51s / 3m12s, no parking (evidence: `~/allegiance-evidence/P864d/showcase-never-parks-5x.txt`) |
| ONE `tools/run-tests.sh --tier fast` | see §4 — **no lane-caused failure** |
| `python3 scripts/parity-scorecard.py` | validates: parity-matrix 687 rows, sim-rules 852 rows (LF only) |
| `python3 tools/check-glossary-links.py` | validates (new GLOSSARY entry, all paths real) |

### 4. Fast tier detail (two runs, documented in full)

- **Run 1** (`TestResults/tiers-20261028T214951.649438Z`, load 10.2): 11,430 tests, **2 failures** —
  (a) `P832aMissionRulesInventoryTests.Every_native_mission_body_handler_and_local_rule_helper_has_a_matrix_row`,
  **my own regression** (the new SIM-0852 row's `native_body` was prose instead of the inventory key);
  fixed and re-verified; (b) `RadarTargetHighlightLiveTests.LiveOffScreenEnemy_IsTheFlaggedRadarTargetHighlight`
  (ledgered flake, `docs/TEST_FLAKES.md`).
- **Run 2** (`TestResults/tiers-20261028T215959.594624Z`, load 5.2), after the fix: 11,430 tests,
  **4 failures**, all outside this lane's code paths and each re-run alone:

  | Failure | Alone ×3 | Note |
  |---|---|---|
  | `ConsoleLogNonBlockingTests.Verbose_gate_silences_and_reenables_routed_diag_lines` | **3/3 pass** | ledgered (global `ConsoleLog` state; also fails in the nightly → NT-2) |
  | `BotFlightTests.Bot_trajectory_matches_independent_ExecuteMove_rollout` | **3/3 pass** | live bot determinism under shared-host load |
  | `ShopLauncherMountNumberingLiveTests.ShopBuyOrder_LauncherGrantedAtMount0_LocksAndFires_Live` | **3/3 pass** | ledgered load flake |
  | `RadarTargetHighlightLiveTests.LiveOffScreenEnemy_IsTheFlaggedRadarTargetHighlight` | 0/3 alone — **and 1/3 alone on a pristine `HEAD` copy of the tree** (`.scratch/base`, `git archive HEAD`) | ledgered; the enemy NPC drifts while the peer waits for its 30 s auto-seat, so the "behind my facing" staging decays with wall-clock speed. **Not caused by this lane** (base tree reproduces it) |

  Plus one calibrated-limit offender, `P750NativeAsteroidTypesTests.Research_showcase_rocks_carry_native_models_and_no_rock_names`
  (12.7 s vs 10.0 s) — ledgered since 2026-10-27, and it *passed*. All of this is recorded in
  `docs/TEST_FLAKES.md`.

Not run here (per the lane rules): the rig (192.168.0.221), the mini and the Mac. Nothing was measured on
another machine.

Raw logs: `~/allegiance-evidence/P864d/` (fast-tier loops, showcase runs, 3× logs).

---

## 5. Live proof (real window, both HUD styles)

`tools/p864d-teamlobby-proof .scratch/p864d classic|modern` — 1280×720 real presented backbuffers
(`SaveAsPng`), a real server and two real wire clients on this host. The proof log (classic run):

```
[p864d] wire clients seated: owner ship 8096 (leader/owner), lobby ship 8097
[p864d] windowed client seated: ship 8098 side 0, state ReadyCheck
[p864d] owner sends RANDOMIZE_TEAMS (FlushSides: non-leaders -> team lobby)
[teamlobby] flushed 2 non-leader player(s) to SIDE_TEAMLOBBY (16 BalanceSides) (fsmission.cpp:5097-5117,5173-5185)
[monogame] team lobby: local ship is on no side (-2) — team screen (pick a side)
[p864d] team lobby now holds: P864D-Lobby, P864D-Pilot(you) (server ships 8098->-2, 8097->-2)
[p864d] side request landed: ship 8098 on side 1 ('Bravo') — state ReadyCheck, leader=True
[p864d] lobby player 8097 requests side 1
[p864d] leader sees 1 waiting player(s): P864D-Lobby
[monogame] ready-check: leader accepts lobby request ship 8097 into side 1 (POSITIONACK)
[p864d] accepted: lobby player 8097 seated on side 1; team lobby now 0 waiting
```

`docs/reports/P864d_assets/` (8 PNGs, 532 KB total, 63–68 KB each, all 1280×720; every one opened and
read before describing it):

1. **`p864d-lobby-list-{classic,modern}.png`** — the team screen after the flush. Title `SELECT SIDE`,
   hint `UP/DOWN SELECT  ENTER CONFIRM`, the native prompt **`P864D-Pilot -- YOU NEED TO PICK A TEAM`**,
   the two real sides with their **running** counts (`> Alpha (players: 1) [Dreghklar]`,
   `Bravo (players: 0) [Ga'Taraan]`), then the lobby row **`Not on a team (players: 2)`** with both
   members by name — **`P864D-Lobby  Ready`** and **`P864D-Pilot  Ready  (you)`** — plus the P864b lobby
   status lines (`LOBBY: OPEN  SIDE CHANGES: OPEN`, per-side auto-accept). This is the item-2 screenshot:
   two clients, flushed into the lobby, listed by name.
2. **`p864d-after-request-{classic,modern}.png`** — after `Down`+`Enter` (the real POSITIONREQ path): the
   client is on **Bravo** (`TEAM Bravo  READY: --`, `[WAIT ] you  (you)`, `TEAM CIV: Ga'Taraan`,
   `TEAM NAME: Bravo`) and the same screen now shows **`NOT ON A TEAM  (players: 1)` →
   `P864D-Lobby  Not Playing`**. The log next to it confirms the authoritative server side: ship 8098 on
   side 1.
3. **`p864d-leader-lobby-{classic,modern}.png`** — the *leader's* view of the same pool: the ready-check
   overlay now shows **`A  ACCEPT LOBBY REQUEST`**, `NOT ON A TEAM (players: 1)` with
   **`P864D-Lobby  Requesting Bravo`**, and the request line `P864D-Lobby awaiting approval for side 1`.
4. **`p864d-leader-accepted-{classic,modern}.png`** — after pressing `A`: the lobby section is **gone**
   and the same-side roster grew to **`[WAIT ] P864D-Lobby`** + **`[WAIT ] you  (you)`**; the server log
   confirms the accept seated ship 8097 on side 1.

**Both HUD styles — verified, not assumed.** The proof loads the real preference file through
`ALLEGIANCE_HUD_SETTINGS` and then asserts the style the client actually adopted (it reads
`Game1._hudSettings.Style` and fails the run on a mismatch). That check caught a real trap: the
`HudSettings` source-generated JSON uses the **string** enum converter and PascalCase members
(`Rendering/HudSettings.cs` `HudSettingsJson`), so a `{"style":0}` file fails to bind, the loader swallows
the `JsonException` and falls back to the fresh-profile default (Modern) — the first proof runs were both
silently Modern. With `{"Style":"Classic"|"Modern","Size":1.0}` the runs log:

```
classic: [p864d] windowed client seated: … HUD style Classic (requested 'classic')
modern:  [p864d] windowed client seated: … HUD style Modern (requested 'modern')
```

and each classic/modern pair is **byte-identical** — so the team-screen and ready-check overlays are
style-independent by construction (both are drawn by the shared pre-flight renderers on the shared HUD
sprite fonts; Modern re-skins the in-flight HUD). The hashes, from the final runs:

```
870eeb695d0ee0ce45d847e69a0473bf  p864d-lobby-list-{classic,modern}.png
3a7d38f617e6423e18f343728075d348  p864d-leader-accepted-{classic,modern}.png
7c8d7d41e54c75d91247620be0d99391  p864d-leader-lobby-{classic,modern}.png
22c1da9f9611f2d56b4153ef77e32e12  p864d-after-request-{classic,modern}.png
```

(The team-screen content is therefore proven in both styles by the shared model; the pairs are kept so a
future style change that *does* touch the lobby panels is caught by a differing hash.)

---

## 6. Parity scorecard before → after

| Metric | Before | After |
|---|---|---|
| sim-rules, family "FedSrv mission rules (event-driven)" weighted | 589/603 (97.68%) | **591/605 (97.69%)** |
| sim-rules, family rows implemented/eligible | 250/256 | **251/257** |
| sim-rules TOTAL weighted | 1877/1903 (98.63%) | **1879/1905 (98.64%)** |
| `parity-matrix.csv` lobby area weighted | 47.0/47.0 | **52.5/52.5** |
| `parity-matrix.csv` lobby rows | 33/33 | **36/36** |
| `parity-matrix.csv` TOTAL weighted | 1856.332/1858.000 (99.91%) | **1861.832/1863.500 (99.91%)** |

Rows added:
- **SIM-0852** `CFSMission::SendLobbyMissionInfo@3936` — the join-time player list that puts the lobby
  pool on the client's team screen (`implemented`, weight 2).
- `parity-matrix.csv` lobby: *Lobby pool ("Not on a team") listed by name* (weight 2),
  *Lobby player requests a side and is admitted* (weight 2),
  *Team leader accepts a waiting lobby player from the pool* (weight 1.5).

---

## 7. Native-intent classification

- **The lobby pool on the team screen — intended-feature, full parity.** Native's team list *is* the real
  sides plus the lobby side, and its player list *is* the selected side's members; the port now shows the
  same population by name with the same status words and the same prompt (`teamscreen.cpp:378-386`,
  `:470-510`, `:1655-1671`, `:1330-1335`). The pool is derived from native's own source of truth (the
  player-info traffic), so no port-local roster message is invented.
- **Requesting a side from the lobby — intended-feature, full parity.** The lobby park is exempt from the
  no-defections gate (`fsmission.cpp:4583-4590`), `Enter` sends the native `POSITIONREQ`
  (`teamscreen.cpp:2962-2982`), a leaderless side auto-accepts (`:4757`) and a led side pends, which is
  exactly the live run's two phases.
- **The leader accepting from the pool — intended-feature, parity with one stated adaptation.** Native
  selects a row in `ConcatinatedList(member list, request list)` and presses Accept
  (`teamscreen.cpp:2563-2567`, `:2997-3015`); the port's team-lobby overlay is a flat keyboard/touch
  layout (the native mouse list-pane does not exist there), so `A` accepts the **first request pending on
  the leader's own side** — the same message, the same audience, the same server gate
  (`FedSrv.CPP:6709-6747`). No behaviour is simulated client-side.
- **Who is in the lobby — intended-feature, native wire path.** `SendLobbyMissionInfo`'s per-ship
  `PLAYERINFO` burst (`fsmission.cpp:3958-3967`) is exactly what the port now sends to a still-choosing
  peer. Nothing was marked n/a: the only "native state" the port does not model is the lobby `IsideIGC`
  object (the port's `-2` park, P864b), which has no client-visible difference here.
- **Running per-side counts — port bug fixed, not a new feature.** `SideNumPlayers` is a running count in
  native (`clintlib.h:578`, `clintlib.cpp:893,928`); the port was showing a join-time snapshot, which the
  first screenshot caught (`Alpha (players: 2)` with one player left).

## 8. Modern / better-approach note

- The **shared-model** approach is what makes "both HUD styles" free: the lobby pool is a pure function of
  `ClientWorld` (`TeamLobbyMembers`, `SideHumanCount`) rendered by the same two layout classes both styles
  already use, so Classic and Modern cannot diverge — and the unit tests prove the rows, not just the
  pixels.
- Deriving the pool from the ship table instead of adding a roster message keeps one source of truth: a
  player leaving the lobby (`JOIN_SIDE`), joining it (`QUIT_SIDE`), or requesting a side
  (`POSITIONREQ(40)`) updates the screen through the ordinary receive path, with no extra server state.
- The one place the port genuinely beats native: native's `MissionInfo` and the team screen each keep
  their own member/request lists and per-side counters, which is why native's team screen needs
  `ConcatinatedList`/`FilteredList` plumbing; the port reduces the same data on demand into display rows,
  so the lobby row, the leader's accept target and the same-side roster all read the same snapshot.
- Kept out of scope deliberately: no native `TeamScreen` port (the port's P743 split maps it onto the
  side-select + ready-check overlays), and no mouse-driven list with per-row selection.

## 9. Unfinished / deferred

- **Leader accept selects the first pending request** rather than a highlighted row (documented above);
  a per-row cursor would need a row-based cursor model in the pre-flight overlays, which the P743 split
  deliberately does not have.
- The **`ReadyCheckLayout` pool section is informational for non-leaders** (no per-player actions such as
  native's Reject/Make-leader from this screen); the existing in-flight team pane still owns those verbs
  (P752/P761).
- **`docs/TEST_FLAKES.md` observation added** for this lane: no new flakes, but the radar-highlight
  staging failure is now documented as time-based (the enemy NPC drifts while the peer waits for its 30 s
  auto-seat) together with the suggested fix, and the pre-existing flake list is reproduced on a pristine
  `HEAD` copy for the A/B.
- **`HudSettings` style-file format trap (found here, not fixed):** `Rendering/HudSettings.cs`
  deserializes with the string-enum converter, so any harness that writes `{"style":0}` (numeric, or
  lowercase) silently gets the fresh-profile default (Modern). P853b's proof tool writes the same
  numeric shape, so its "identical classic/modern frames" were very likely two Modern runs; this task's
  proof writes `{"Style":"Classic"}`, asserts the adopted style, and *then* shows the frames are
  genuinely identical. Worth fixing in the other proof harnesses (a small `HudSettings.WriteStyle` helper
  would remove the footgun).
- Evidence not kept in the repo: raw fast-tier logs and the 3×/5× loops live in
  `~/allegiance-evidence/P864d/`; the repo holds only the 8 reviewed PNGs (532 KB).

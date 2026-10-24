# P802 — team-lobby civilization picker

Branch `pi/p802`, worktree `/home/domingo/allegiance-pi-p802`, base = master
`6ef6d9bf` (`tests: P661 egress hot-path allocation test takes the best of three
windows`). Working tree only — nothing committed or pushed.

**What this adds:** the side's **team leader can change the side's civilization
before the game starts**, from the team lobby (ready-check) screen: current civ
name + native `icon<civ>bmp` art, a selector over every civilization the mission
core offers plus **RANDOM**, locked to read-only for everyone else and after the
start. Built on the existing native message `FM_CS_CHANGE_TEAM_CIV(51)` — the
client half (send + UI) is new, the server half is completed (random factions,
the `SetSideCiv` side effects, research plans).

**Correction to the dispatch brief:** the brief said “in this native source only
the bot frameworks SEND it; the WinTrek client has no picker.” That is not true
of this source tree: WinTrek has a real civilization picker — the TEAM SETTINGS
dialog's `civComboPane` (`WinTrek/teamscreen.cpp:860-1104`) which posts the
message in `OnButtonOk` (`:1035-1078`) — plus the team screen's
`OnCivChosen` (`:3049-3065`) and the owner's random re-roll loop
(`:2657-2695`). So this UI is a **port of a native UI** (native cites below), not
a port-invented addition; the parity rows are `done`, not `done-visual`.

Evidence: screenshots in [P802_assets/](P802_assets/) (four 1280×720 PNGs, 256 KB
total); raw logs and the standard live-proof output at
`~/allegiance-evidence/P802/` (`fast-tier.log`, `build-tools.log`, `aot.log`,
`p802-classic.log`, `p802-modern.log`, `live-proof.log` and
`live-proof/*.png`).

---

## 1. Native rules (with cites)

| Rule | Native cite |
|---|---|
| Message shape: `SideID iSide; CivID civID; bool random;` — “sent by team leader to change the civ of the side, and forwarded to everybody”. Wire: `cbmsg@0, fmid=51@2, iSide@4, civID@6, random@8`, 1 align-2 pad byte → 10 B. | `Inc/Messages.h:407-411` |
| Server gate: ignore unless the sender's mission exists, the sender is that side's **leader**, the sender is **not in `SIDE_TEAMLOBBY`**, and the stage is **`STAGE_NOTSTARTED`**. Native changes the sender's **own** side (`pside = pfsPlayer->GetSide()`), so a mismatched `iSide` is not honoured. | `FedSrv/FedSrv.CPP:7344-7366` |
| On success: `TrapHackBoot(civID != NA)`; the civ must resolve in the mission core (`TrapHackBoot(pciv)`); **forward whole-mission first**, then `SetSideCiv`. | `FedSrv/FedSrv.CPP:7360-7365` |
| `CFSMission::SetSideCiv`: `pside->SetCivilization(pciv)` then, for **every ship on the side**, `ppso->SetPersist(pfsship->GetPlayer()->GetPersistPlayerScore(civID))` — the player's **persistent score for the new civ becomes their live score**. (`m_misdef.rgCivID` is deliberately **not** updated — the assignment is commented out, so a rotation rebuilds the lobby with the configured civ.) | `FedSrv/fsmission.cpp:5268-5284` (score loop `:5274-5283`, commented rgCivID `:5272`) |
| `IsideIGC::SetCivilization`: if the civ changed — `m_data.ttbmDevelopmentTechs = GetCivilization()->GetBaseTechs()`, `m_ttbmBuildingTechs.ClearAll()`, `m_data.civilizationID = …`, and **turn every station of the side into the new civ's initial station type** (`pstation->SetBaseStationType(pciv->GetInitialStationType())`). | `Igc/sideigc.h:76-102` (dev techs `:88`, building clear `:89`, station loop `:92-100`) |
| Client receive: `FM_CS_CHANGE_TEAM_CIV` → `GetSide(iSide)->SetCivilization(GetCivilization(civID))`, `OnTeamCivChange(...)`, `SetRandomCivilization(random)`. | `clintlib/appmsg.cpp:2530-2539`; `clintlib/clintlib.h:81,154` |
| Native UI: the TEAM SETTINGS dialog (`civComboPane`) lists the core civilizations below **300** (“NYI hack to prevent special civs”) and, on OK, sends the civ + `random`; `Random` (`RANDOM_ID = 9999`) picks `randomInt(0, numCivilizations-1)` over that legal list. | `WinTrek/teamscreen.cpp:860-1104`, list `:897-916`, Random selection `:1006-1009`, send `:1062-1078`; `RANDOM_ID` `Igc/igc.h:501` |
| Native UI: `OnCivChosen` (the team screen's own civ combo) sends `CHANGE_TEAM_CIV` for the leader's side with `random=false`. | `WinTrek/teamscreen.cpp:3049-3065` |
| Native UI: the side's civ art is `icon<civ.GetIconName()>bmp`; the lobby watermark is `icon<civ>lobbybmp` (default watermark for a random civ). `OnTeamCivChange` refreshes it and the team list. | `WinTrek/teamscreen.cpp:586-591`, `:2479-2492`, `:2493-2510` |
| Native random factions: the mission owner re-rolls every random side every 5 s while the mission is not in progress (loop, `GetRandomCivilization()`, legal list `< 300`). Sides with an unset `rgCivID` at mission creation get a random legal civ (`InitializeCivList` + `randomInt`). | `WinTrek/teamscreen.cpp:2657-2695`; `FedSrv/fsmission.cpp:64-77,175-198` |

In-game civ switching is forbidden natively (the `STAGE_NOTSTARTED` gate) and
stays forbidden here.

## 2. Port — what was built

### 2.1 Client send
`ConnectionSession.SendChangeTeamCiv(sideId, civId, random)` — the native 10-byte
layout, packaged like every other session send, mirror of
`Bot.SendChangeTeamCiv`. Wire proof: section 4.

### 2.2 Picker UI (team lobby / ready check, both HUD skins)
- `src/Allegiance.ClientCore/State/TeamCivPicker.cs` (new, pure): option list —
  every core civilization with `CivilizationId < 300`, ascending, then a
  trailing `RANDOM` entry (`teamscreen.cpp:897-916`, `igc.h:501`); `CanPick`
  (own side's current leader **and** `STAGE_NOTSTARTED` — the server's own
  gate); current civ from the roster / `CHANGE_TEAM_CIV` echo; cursor wrap;
  request mapping; display name and `icon<civ>bmp` stem.
- `src/Allegiance.ClientCore.Game/Rendering/TeamCivPickerLayout.cs` (new, pure,
  ASCII-only): the always-visible `TEAM CIV: <name>` line; for the leader the
  `C  CHANGE TEAM CIVILIZATION` hint and, when open, a `SELECT CIVILIZATION`
  list with the cursor, `UP/DOWN SELECT   ENTER CONFIRM   C CLOSE`; for everyone
  else a read-only note instead of any control.
- `Game1.Panes.cs` (`UpdateReadyCheck` / `DrawReadyCheck`): `C` opens the picker
  (new `CivPicker` action in `InputActionCatalog`, ready-check scope); Up/Down
  move, Enter/Space send `SendChangeTeamCiv`, B/C close; the open selector
  **replaces** the ready-check body (native's modal dialog) so the list fits a
  720p window; the mission starting or leadership changing closes it.
- `ReadyCheckOverlayRenderer.Draw(..., Texture2D? civIcon)`: draws the native
  civ art top-right (native size, capped to a 72 px box, no stretching) — loaded
  through the existing native `.mdl`/PNG pipeline
  (`HudNativeTextures.LoadMissionImage`, new wrapper over the same decoder).
- `SideSelectLayout` gained an optional `civNameFor` resolver, so the **side
  screen** also lists each side's civilization read-only.
- Both HUD skins: the lobby overlay uses the shared HUD sprite fonts in Classic
  and Modern (skin choice swaps only the in-flight HUD renderer), and the four
  screenshots below are the same scene rendered in each configured skin.

Before/after (real windowed `Game1` DesktopGL, real server, 1280×720 backbuffer;
every screenshot opened and described, not just pixel-counted):

- **Before (master):** the ready-check panel had no civilization information at
  all — `Y TOGGLE READY`, `O TOGGLE TEAM READY`, `TEAM Alpha READY: --`,
  `YOU: NOT READY`, the same-side roster, then the lobby/control lines. A leader
  had no way to change the side's civ.
- **After — picker open** ([civ-picker-720p.png](P802_assets/civ-picker-720p.png); Classic and Modern render the lobby identically — the captures were byte-identical, one kept): a
  centred panel reading `TEAM CIV: Dreghklar` with the native Dreghklar icon art
  top-right, the title `SELECT CIVILIZATION`, the eleven legal choices
  (`> Dreghklar`, Technoflux, Ga'Taraan, Phoenix, Omicron, Gigacorp, Iron
  Coalition, Bios, Belters, Rixian, `RANDOM`) with the highlighted cursor, and
  the `UP/DOWN SELECT   ENTER CONFIRM   C CLOSE` hint.
- **After — changed** ([civ-changed-720p.png](P802_assets/civ-changed-720p.png); identical in both HUD styles): the
  ready-check panel is back with `TEAM CIV: Technoflux`, the Technoflux civ icon
  top-right, and the `C  CHANGE TEAM CIVILIZATION` hint — the change came back
  through the server's whole-mission relay, not from local state.
- Classic and Modern PNGs are byte-identical in content (same scene, same
  overlay fonts); that is the honest result of the lobby being skin-independent,
  and both were produced by a run with `ALLEGIANCE_HUD_SETTINGS` forcing that
  skin.

Proof driver: [tools/p802-civ-proof](../../tools/p802-civ-proof/Program.cs) —
boots the real `Allegiance.Server`, launches the real **windowed** `Game1`
DesktopGL client over loopback, seats it as side 0's leader **without readying**
(mission stays `NotStarted`), then drives the production `UpdateReadyCheck`
handler with synthetic `KeyboardState`s (the P752/P761 proof idiom): `C` opens,
a different civ is selected and confirmed, the shot is taken only after the
server's echo changed the roster. Nothing is seeded into the renderers.

The standard `scripts/live-proof.sh` was also run (`--port 6182`, 2560×1440,
`proof_t008.0_sOmega_ccockpit.png` / `proof_t012.0_sOmega_ccockpit.png` under
`~/allegiance-evidence/P802/live-proof/`): release client + server boot and run
cleanly, but live-proof only scripts post-launch flight shots (the open frame I
inspected is the docked hangar/loadout screen), so the lobby picker frames are
the dedicated proof tool's.

### 2.3 Server completeness
`src/Allegiance.Server/Server.TeamCiv.cs` (new) + the P186 handler in
`Server.Dispatch.cs`:

- **Gates unchanged** (own side, current leader, `STAGE_NOTSTARTED`, civ must
  exist — `FedSrv.CPP:7344-7366`), relay before apply.
- **`random` honoured** (it was ignored): a `random=true` request (civID may be
  a placeholder such as `RANDOM_ID = 9999`) is resolved on the server to a
  concrete civ from the legal lobby pool (`< 300`, `teamscreen.cpp:897-916`,
  `:2667`) with the server's seeded RNG (`_rand`, the same stream native
  mission creation uses, `fsmission.cpp:191`). The whole-mission relay carries
  the **resolved** civ id with `random=true` — the same shape native's client
  sends (`teamscreen.cpp:1062-1078`). Port addition vs native: the server does
  the picking, so the native owner re-roll loop (`teamscreen.cpp:2657-2695`) is
  not needed for the first pick; it is **not** ported (see gaps).
- **`SetSideCiv` side effects**
  - `pside->SetCivilization`: side civ + `DevelopmentTechs := new civ's base
    techs` (`sideigc.h:88`) — the shop, investment pane, research gates and buy
    gate follow the pick. `BuildingTechs` is recompute-on-access from owned
    stations in this port, so retyping the stations *is* native's
    `ClearAll()` (`sideigc.h:89`).
  - Stations: every station of the side is retyped to the new civ's
    `InitialStationTypeId` via `Station.SetBaseStationType` (`sideigc.h:92-100`;
    the port method already fires `StationTypeChange` + building-tech notify,
    `stationIGC.cpp:210-276`). Pre-start a side only owns its home station(s),
    so this is the visible base-type/art change native performs.
  - Score reset: `PlayerScore.ResetToPersistScoreForCiv` — the port of
    `fsmission.cpp:5274-5283`. **Deliberate simplification, stated:** native
    copies the player's *persistent per-civ* score into the live object; this
    port has no persistent/account layer (P193/P583), so that object is the
    native default-constructed (zero) score and the copy collapses to zeroing
    the live session counters (rank/combat rating kept — the per-civ career
    record they come from does not exist here). In a pre-start lobby the
    counters are already zero, so the ordering is ported without changing
    normal lobby behaviour.
  - The client applies the same local mirror native clients do: civ/random
    state, dev techs and station retypes from its own `MissionCore`
    (`ClientWorld.Interface.cs`), so a peer that was already seated sees the
    shop/investment/station art update without a second message.
  - Pending side-select peers get a fresh `SIDE_ROSTER` (their screen reads the
    snapshot, not the relay) — SIDE_ROSTER row in `docs/parity/parity-matrix.csv`.
- **Career / leaderboard** stays out of scope: no persistence exists, and native's
  `SetSideCiv` only touches the per-civ persist score object.

### 2.4 Bots and research plans
- **No bot fights the leader:** the only senders of `CHANGE_TEAM_CIV` are the
  human picker (new) and the `Bot`/MCP test helpers, which are never invoked
  spontaneously (`grep SendChangeTeamCiv src tools`). NPC ships have no lobby
  client and cannot send it; server-side bots that happen to be a side's leader
  simply never touch the civ. Non-leader humans are refused server-side.
- **Research plans follow the new civ:** plans are keyed by civ
  (`data/showcase-plans/<core>/civ<id>-*.json`) and were selected once at
  mission assembly from the boot-time `games[].civIds`. A lobby change now
  re-selects the side's plan for the new civ (`ReselectSideResearchPlan`,
  same `ResearchSettings`/directory/"auto" inputs as assembly) and re-sends the
  P703 `RESEARCH_BRIEFING` to that side's peers. The MCP research commander
  (`tools/allegiance-mcp`) rebuilt its executor only on an epoch change; it now
  also rebuilds when `Plan.CivId` no longer matches the briefing
  (`ResearchCommander.PlanCivId`). The plan is therefore picked **for the civ
  the game starts with**, not the one the side joined with. Native does not
  persist the lobby civ into `misparms.rgCivID` (`fsmission.cpp:5272`
  commented out), so a game-over rotation rebuilding from the config's civ ids
  is native behaviour, not a regression.
- Live proof: `P802TeamCivLiveTests.Research_plan_follows_the_new_civilization`
  boots the real server with `data/showcase-plans`, changes side 0 from civ 1 to
  civ 25 and asserts `mission.ResearchPlans[0].CivId == 25`.

## 3. Parity rows
- **Updated** `fedmsg, 51 CHANGE_TEAM_CIV` (parity matrix) and its
  `docs/parity/fedmsg-wiring.csv` row: client send + picker, server random
  resolution, `SetSideCiv` effects, cites.
- **New** `lobby` row: *Team lobby / civilization picker / Team leader changes
  the side's civilization before start* — `done`, weight 2.000.
- **New** `screens` row: *Ready-check / Team civilization block (name, icon,
  read-only for non-leaders)* — `done`, weight 1.000.
- `scripts/parity-scorecard.py` (before → after):

| Area | before | after |
|---|---|---|
| lobby | 88.40% (41.548/47.000), 27/33 | **88.87% (43.548/49.000), 28/34** |
| screens | 85.81% (97.829/114.000), 77/91 | **85.94% (98.829/115.000), 78/92** |
| TOTAL (parity) | 91.25% (1668.041/1828.000) | **91.26% (1671.041/1831.000)** |
| sim-rules TOTAL | 94.39% (1212/1284) | 94.39% (unchanged) |

Validation prints `parity-matrix.csv: validated 670 rows` and
`sim-rules-matrix.csv: validated 473 rows`, exit 0.

## 4. Wire proof
- Native golden bytes (P718, unchanged): `{SideId=0, CivId=5, Random=true}` →
  `0A003300000005000100` — `cbmsg=10, fmid=51, iSide@4, civID@6, random@8,
  pad@9`.
- New: `P802TeamCivPickerTests.SendChangeTeamCiv_sends_the_native_layout_over_real_tcp`
  drives the real `ConnectionSession.SendChangeTeamCiv` over real loopback TCP
  and decodes the host-side frame: 10 bytes, fmid 51, side/civ/random as sent;
  a second send with `random=false`. This fails on master (the method does not
  exist there).
- Server side: `P802TeamCivLiveTests` uses real `Allegiance.Bot` peers over
  loopback; the leader's change is observed by the second peer's `ChangeTeamCivs`
  list (whole-mission relay), not just by the server's own state.

## 5. Tests
All new test classes were run **3× alone** and passed 3/3 (logs in
`~/allegiance-evidence/P802/`):

- `tests/Allegiance.Core.Tests/P802TeamCivLiveTests.cs` (Live-network, real
  server + real Bot peers, 3 tests):
  - `Random_change_resolves_to_a_real_legal_civ_and_relays_it` — random with
    placeholder 9999 resolves to a real `< 300` civ and both peers receive the
    resolved civ with `Random=true`.
  - `Change_applies_new_civ_base_techs_stations_and_score_reset` — dev techs ==
    new civ base techs, every side station retyped to the civ's initial station
    type, live score counters reset (seam `TestPlayerScore`).
  - `Research_plan_follows_the_new_civilization` — plan civ 25 after changing to
    civ 25.
- `tests/Allegiance.ClientCore.Tests/P802TeamCivPickerTests.cs` (5 tests): real
  TCP send layout; `CHANGE_TEAM_CIV` echo retargets dev techs + stations + roster
  (Artwork); option list (legal civs ascending + RANDOM last, null core → empty);
  leader/non-leader and before/after-start gate; request/cursor semantics.
- `tests/Allegiance.ClientCore.MonoGame.Tests/P802CivPickerLayoutTests.cs` (4
  tests): closed picker shows the current civ and only the leader gets the hint;
  open picker lists every legal civ + RANDOM, cursor highlighted, read-only after
  start; initial cursor follows the current civ / RANDOM flag; side-select roster
  civ annotation.

**What fails on master (and why):**
- `Random_change_resolves_to_a_real_legal_civ_and_relays_it` — master's P186
  handler ignores `random` and looks up the placeholder civ `9999`, which is not
  in the core, so it logs “not in core” and changes nothing (timeout on the
  apply/relay condition).
- `Change_applies_new_civ_base_techs_stations_and_score_reset` — master only
  assigns `side.Civilization`; dev-tech/station assertions and the score reset
  fail.
- `Research_plan_follows_the_new_civilization` — master never re-selects the
  plan, so `ResearchPlans[0].CivId` stays at the configured civ.
- All `P802TeamCivPickerTests` / `P802CivPickerLayoutTests` — the production
  types don't exist on master (compile-level failure for the picker/UI and the
  session send).

**Affected existing tests re-run:** `LiveTeamLobbyToggleTests` (14/14, includes
the P186 CHANGE_TEAM_CIV gates), `P718*` (20/20),
`ReadyCheck|SideSelect|P761TeamLobby` (30/30), `P703ResearchCommanderLiveTests`
(4/4 ×3), `P700ShowcaseLiveTests.Local_showcase_never_parks_with_target_for_five_seconds`
**5×** (5/5, ~6-8 min each due to load from other agents; no parks).

**One small test-harness fix:** `P703ResearchCommanderLiveTests` configured its
own `ServerConfig` without `SideSelectAutoSeatSeconds`, so its `ConnectionSession`
peers (which never send `POSITIONREQ`) hung in side-select after P763 made
auto-seat opt-in/off by default — it failed 3/3 alone **before** my change as
well (failure was at `Connect`, not in research code). Added
`SideSelectAutoSeatSeconds = 30` with the same comment `P660NextMatchLiveTests`
already carries; the test now passes 3/3.

## 6. Gate numbers
- Whole-solution build: `dotnet build AllegianceCore.slnx` → **0 warnings, 0
  errors**.
- `tools/build-tools.sh` → **all 73 tool projects built** (includes the new
  `tools/p802-civ-proof`).
- AOT publish (`dotnet publish src/Allegiance.Server -c Release -r linux-x64
  --self-contained -p:PublishAot=true`): success, **0 IL2026/IL3050, 0
  warnings**; produced a 14 MB native `Allegiance.Server` binary.
- Fast tier (one run): `tools/run-tests.sh --tier fast --no-build` →
  **10139 tests, PASS, 289.7 s** (`Allegiance.Lobby.Tests` 24;
  `Allegiance.ClientCore.MonoGame.Tests` 3282 passed / 3 not executed;
  `Allegiance.ClientCore.Tests` 1737; `Allegiance.Core.Tests` 5081; quarantine
  12).
- New/affected tests 3× alone: as listed in §5 (all green).
- Showcase never-parks: 5/5.
- `scripts/parity-scorecard.py`: validates, totals in §3.
- Real-window evidence: 4 PNGs in `docs/reports/P802_assets/` + 2 live-proof
  PNGs under `~/allegiance-evidence/P802/live-proof/`.
- No flakes added; `docs/TEST_FLAKES.md` untouched (nothing flaked once, apart
  from the P703 harness gap explained above, which was deterministic and is
  fixed in the test config).
- **New media for review:** `docs/reports/P802_assets/*.png` (4 files, 260 KB)
  are new PNGs and need Domingo's review before the public push
  (`scripts/publish-guard.sh` media gate). No personal paths/secrets in code
  (`git diff` scan for `/home/<user>/` in `src tests scripts tools docker
  .github` is empty).

## 7. Files changed
Production:
- `src/Allegiance.ClientCore/Session/ConnectionSession.cs` (send)
- `src/Allegiance.ClientCore/State/ClientWorld.Interface.cs` (native client
  mirror of `SetCivilization`)
- `src/Allegiance.ClientCore/State/TeamCivPicker.cs` (new, pure picker state)
- `src/Allegiance.ClientCore.Game/Rendering/TeamCivPickerLayout.cs` (new, pure
  rows)
- `src/Allegiance.ClientCore.Game/Rendering/ReadyCheckOverlayRenderer.cs` (civ
  icon)
- `src/Allegiance.ClientCore.Game/Rendering/HudNativeTextures.cs`
  (`LoadMissionImage`)
- `src/Allegiance.ClientCore.Game/Rendering/SideSelectLayout.cs` (optional civ
  annotation)
- `src/Allegiance.ClientCore.Game/Rendering/InputActionCatalog.cs` (`CivPicker`
  = C)
- `src/Allegiance.ClientCore.Game/Game1.cs` / `Game1.Panes.cs` (state, keys,
  draw)
- `src/Allegiance.Server/Server.TeamCiv.cs` (new: random resolution,
  `SetSideCiv` effects, plan re-selection, briefing re-send)
- `src/Allegiance.Server/Server.Dispatch.cs` (handler wiring)
- `src/Allegiance.Server/Server.Lifecycle.cs` (`_missionConfigs`,
  `ResearchSettingsFor`, mission→config link)
- `src/Allegiance.Server/Server.Lobby.cs` (briefing extraction, rotation cleanup)
- `src/Allegiance.Server/PlayerScore.cs` (`ResetToPersistScoreForCiv`)
- `src/Allegiance.Server/Server.BroadcastTestSeams.cs` (`TestPlayerScore`)
- `src/Allegiance.Core/Wire/ChangeTeamCiv.cs` (docs: random is honoured)
- `tools/allegiance-mcp/ResearchCommander.cs` + `BotManager.Research.cs`
  (rebuild on plan civ change)
- `docs/parity/parity-matrix.csv`, `docs/parity/fedmsg-wiring.csv`
Tests/tools/docs:
- `tests/Allegiance.Core.Tests/P802TeamCivLiveTests.cs` (new)
- `tests/Allegiance.ClientCore.Tests/P802TeamCivPickerTests.cs` (new)
- `tests/Allegiance.ClientCore.MonoGame.Tests/P802CivPickerLayoutTests.cs` (new)
- `tests/Allegiance.ClientCore.Tests/P703ResearchCommanderLiveTests.cs`
  (auto-seat flag)
- `tools/p802-civ-proof/` (new proof driver)
- `docs/reports/P802_TEAM_CIV_PICKER.md`, `docs/reports/P802_assets/*.png`

## 8. Known gaps / deliberate scope
- The owner random **re-roll loop** (`teamscreen.cpp:2657-2695`) is not ported:
  a random pick resolves once, server-authoritatively. Re-rolling would need a
  lobby timer and is invisible to the port's lobby flow (the message already
  carries a concrete civ to every peer).
- The native TEAM SETTINGS dialog's other fields (team name, squad) remain
  outside this row; the port exposes the civilization selector from the team
  lobby instead of a modal settings button.
- Touch/pad can open the picker (`C` action is keyboard-only, like the rest of
  the lobby configuration controls P761 added); the picker's Up/Down/Confirm use
  the existing pad navigation.
- Career/leaderboard and in-game civ switching stay out of scope (native
  forbids the latter).
- Pre-start station retyping covers the home station(s) a lobby side owns; a
  file map that pre-places several station types for one side gets them all
  retyped (native does the same), but this port does not re-export live station
  geometry — clients apply the same local retype from their core, which is what
  native clients do (`clintlib/appmsg.cpp:2530-2537`).

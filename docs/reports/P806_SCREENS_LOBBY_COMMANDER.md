# P806 (PAR-11) — screens, lobby and commander leftovers

Base: worktree of the PUBLIC AllegianceCore clone at master `6e5c745f`
(`P807: game-over handoff — the next-match migration no longer stalls the server tick at 394 peers`),
branch `pi/p806`. Native reference: `~/Development/Allegiance/src/`.

All native line numbers below were re-grepped against that checkout. Every row below was
verified against the executable path named in it, not against symbol presence.

## Result at a glance

| # | Area | Row | Before | After | Weight |
|---|------|-----|--------|-------|--------|
| 1 | screens | Chat pane / History scrolling and channels | partial | **done** | 2.500 |
| 2 | screens | Intro screen / Options menu | partial | **done** | 0.250 |
| 3 | screens | Server list / Sort and game filters | partial | **done** | 1.333 |
| 4 | commander | General default-order target resolution | partial | **done** | 1.668 |
| 5 | commander | Drones: miners / Native optimal travel | partial | **partial (explicit split)** | 1.668 |
| 6 | lobby | Directory filters details and player search | partial | **done** | 2.000 |
| 7 | lobby | Lobby chat / Lobby-directory relay feedback | partial | **n/a (era/infrastructure)** | 2.000 |

Career/leaderboard/character-info rows were not touched (deferred by the brief); the team civ
picker (P802) and training (P785) were out of scope.

## How the new data reaches the client (rows 3/6)

Native does not compute the server-list filters from a running game: the lobby relays each
game's `MissionParams` inside the mission definition, and the client reads it through
`MissionInfo`'s accessors (`clintlib/clintlib.h:540-557`: `AllowDevelopments()`,
`LimitedLives()` = `iLives != 0x7fff`, `ScoresCount()`, `WasObjectModelCreated()`,
`GoalConquest()/GoalDeathMatch()/GoalProsperity()/GoalCountdown()/GoalArtifacts()/GoalFlags()/
GoalTerritory()`, `GetMinRank()/GetMaxRank()`, `InProgress()`). The list screen then filters
(`GameScreenFilter::operator()`, `WinTrek/gamescreen.cpp:933-999`) and sorts (`UpdateGameList`,
`:1185-1238`) purely on the client.

The port replaces the Zone lobby with its own HTTP directory, so the **existing native field
set** rides the service that already plays the lobby's role — no new game-state protocol was
invented:

```
Allegiance.Server                              Allegiance.Lobby                 Allegiance.ClientCore.Game
LobbyGameParams.FromMission(mission)  ──POST──▶ GameParamsRequest → GameParamsDto ──GET──▶ GameParams on ServerListing
  (LobbyHeartbeat register/heartbeat)            (ServerRegistry, /api/servers)            (LobbyGameFilters / LobbyGameSort)
```

* `LobbyGameParams.FromMission` (`src/Allegiance.Server/LobbyHeartbeat.cs`) snapshots the live
  mission on the sim thread once per tick next to `LivePlayerCount`/`LivePlayerNames`
  (`Server.Broadcast.cs`), so the heartbeat thread never touches mission state.
* Values with no port equivalent take native `MissionParams::Reset`'s value and are documented:
  `bScoresCount = true` (`Igc/igc.h:1141`), `bObjectModelCreated = true` (`igc.h:1150`: every
  port mission is created by the server app), `iMinRank = -1` / `iMaxRank = 1000`
  (`igc.h:1301-1302`: no rank window in the port).
* The directory treats the payload as optional (`GameParamsRequest` with nullable members,
  missing values filled with the same native Reset defaults) and stores it next to the listing;
  a pre-P806 host that sends nothing registers exactly as before (`Game = null`).
* On the client the settings are `ServerListing.Game` (`GameParams`), a mirror of the native
  accessors. **Native computes the game type on the client**, and so does this port:
  `NativeGameTypes.IndexOf` is the `gametypes.cpp` table, used by the SETTINGS sort column.

## Row 1 — Chat pane / History scrolling and channels (screens, 2.500) — done

**Native rule.** Two independent filters, both in `WinTrek/chatpane.cpp`:
* `m_bPlayerChatsOnly` (`:128,146,226-233`): drops "non-critical" lines — not from a player, not
  from an object model, not `CHAT_INDIVIDUAL_NOFILTER`. The initial value is `true`; the Game
  Options `Chat/Mute/Filter` family and `FilterChatsToAll` are the user-facing switches
  (`WinTrek.cpp:4568-4582`, `:4706-4722`; the latter is applied in `ReceiveChat`,
  `trekigc.cpp:4493`: drop `CHAT_EVERYONE` while in game).
* the **lobby** channel list: the team screen's chat-target buttons reset the pane and add
  `{target, CHAT_INDIVIDUAL, CHAT_INDIVIDUAL_NOFILTER, CHAT_ADMIN}` (`teamscreen.cpp:3082-3118`;
  Everyone `:3088-3092`, Team `:3097-3101`, Leaders `:3108-3112`, the Leaders button gated on
  team leadership `:3104-3116`). The in-flight console chat pane is the same class but selects
  **no** channels (`console.cpp:3439-3442`).
* History semantics were already native: autoscroll off + 10 s resume timer
  (`chatpane.cpp:167-198`), PageUp/PageDown/OnScroll re-enable (`:255-283`), append keeps a paused
  pane's content (`:323-339`), channel mutators rebuild and snap to bottom (`:286-297,340-358`).

**Before.** The production pane had an **Alt+C cycle All → Players → Channel** and applied the
`[compose channel, Individual, IndividualNoFilter, Admin]` set in *flight* — a port adaptation
with no native equivalent (the native flight pane has no channel filter), and the lobby pane's
rule was not reachable anywhere.

**After.**
* The cycle is deleted. `Game1.ApplyChatPaneFilter` now applies the two native mechanisms:
  `m_bPlayerChatsOnly` (seeded from the persisted Game Options value; the rebindable
  `ChatPaneFilter` key is now the native "Filter Chats" switch) and, on the team lobby screen
  only, `ChatPaneModel.ApplyLobbyChatTarget` — `ResetChannels` + the four native channels, with
  the Leaders button refused for a non-leader. In flight the channel list stays empty.
* The team lobby (ReadyCheck screen, the port's TeamScreen) now hosts the chat pane
  (`Game1.Panes.cs` `UpdateReadyCheck`/`DrawReadyCheckChat`): TAB is the keyboard form of the
  native chat-target buttons (`ChatTargetLogic.NextLobby`: Everyone → Team → Leaders, leader
  only), each step re-applies the channel list, and the filtered history is drawn bottom-left.
* `FilterChatsToAll` is implemented at the native point (`ChatLinePassesPaneFilter` drops
  `CHAT_EVERYONE` while in game; `IntroPreferences.FilterChatsToAll` default **true**, native
  `WinTrek.cpp:3271-3272`), toggled from Game Options → Chat/Mute/Filter.
* A native detail worth stating: because the lobby channel set holds only the current target plus
  Individual/NoFilter/Admin, switching the button to Team hides Everyone (and Leaders) lines —
  that is what the shots show, not a bug.

**Tests.** `P757ChatPaneTests.P806_chat_pane_pipeline_uses_the_two_native_filters` (in-flight =
players-only, no channels; team lobby = the four-channel set),
`P757ChatPaneTests.P806_filter_chats_to_all_drops_everyone_lines_in_game`,
`P757ChatPaneTests.P806_lobby_chat_target_cycle_is_the_native_button_set` (leader gate +
`ResetChannels`/`AddChannel` order). All three fail on master: `ApplyChatPaneFilter`,
`ApplyLobbyChatTarget`, `NextLobby` and `_chatFilterChatsToAll` do not exist there.

**Shots.** `P806_assets/lobby-chat-classic-720p.png`, `P806_assets/lobby-chat-modern-720p.png`.
Both were opened and read: the READY CHECK panel is up, the bottom-left pane is titled
`TEAM CHAT TAB: EVERYONE (ALL)` (then `TAB: TEAM`), and the visible lines are the Individual and
Admin messages; the Everyone/Team lines are absent because of the native channel set /
`FilterChatsToAll` rules. The ready-check screen is not `HudStyle`-branching (verified: no
`_hudSettings`/`HudStyle` read in `Game1.Panes.cs`, `Game1.Session.cs`, `Game1.IntroScreen.cs`),
so both captures show the identical pane.

**Classification.** design (native's per-screen pane split). No modern improvement needed —
but the port's compose-from-the-lobby text box is still flight-only (the compose/send row is
marked done for flight; a lobby composer is a separate feature, not part of this row).

## Row 2 — Intro screen / Options menu (screens, 0.250) — done

**Native rule.** `ShowOptionsMenu` (`WinTrek.cpp:4429-4442`) adds four items: Graphics Device,
Graphics Options, **Sound Options (`idmSoundOptions`, 'S')** and **Game Options
(`idmGameOptions`, 'G')**, the last two passing `m_psubmenuEventSink` so they *open submenus*.
`GetSubMenu` (`:4502-4590`) builds:
* Sound Options: quality/hardware/DSound8 toggles + SFX volume up/down + voice-over volume
  up/down (`:4554-4566`);
* Game Options: `idmMuteFilterOptions` "Chat/Mute/Filter" (a nested submenu, `:4569`), sticky
  chase, linear controls ('L'), large dead zone ('Z'), virtual joystick ('J'), flip Y ('Y'),
  force feedback (nested) and `idmConfigure` "Map Keys and Controls" ('C') (`:4568-4586`);
* Chat/Mute/Filter: censor/filter toggles incl. `idmFilterChatsToAll` ('A'), chat line count and
  text size (`:4588-4606`). The `FilterChatsToAll` label is the action
  (`WinTrek.cpp:5664-5667`), and the toggle applies to `ReceiveChat` (`trekigc.cpp:4493`).

**Before.** The four-group list existed, but Sound Options and Game Options were **one-press
toggles** (`case 1` = music toggle, `case 2` = chat filter), with the rest of the native Game
Options inventory absent.

**After.** `OptionsMenuModel` is the native tree (root / Sound / Game / Chat pages, per-row
native hotkeys, state-bearing labels, `Back()` = ESC leaves a submenu then closes at the root).
`Game1.UpdateOptionsMenu` executes each command against the port's real control:
music toggle + volume (the port has one music mixer, not native's separate SFX/VO mixers),
Linear Controls / Dead Zone / Flip Y / Virtual Joystick (the `MouseFlightController` state the
in-flight keys drive), Map Keys and Controls (the key-binding popup) and Filter Chats To All
(persisted + applied to the chat log). Native items with **no port control** are omitted rather
than faked: sound quality/hardware, DirectSound8 usage, the SFX/voice-over gain pairs, Force
Feedback and the mouse-acceleration/wheel-delay submenu (`GetSubMenu :4554-4566,4590-4606`).
The benchmark-sharing panel moved from S to **B** because 'S' is the native Sound Options key.

**Tests.** `P806OptionsMenuTests` (7 cases: root shape and submenu descent, Sound items, Game
items + nested Chat/Mute/Filter, the flipping `Filter Chats` label, ESC/Back, native hotkeys,
cursor wrapping). Fails on master (no `OptionsMenuModel`).

**Shots.** `P806_assets/options-root-720p.png` (`Sound Options >`, `Game Options >`),
`options-sound-submenu-720p.png` (`Music: off / Music Volume Up / Music Volume Down`),
`options-game-submenu-720p.png` (`Chat/Mute/Filter >`, `Linear Controls`, `Mouse Dead Zone`,
`Flip Y`, `Virtual Joystick`, `Map Keys and Controls`), `options-chat-submenu-720p.png`
(`Don't Filter Chats Sent To All`). All four were opened and read.

## Rows 3 and 6 — server-list filters / sort and directory details (screens 1.333, lobby 2.000) — done

**Native rule.** `FilterDialogPopup` (`gamescreen.cpp:351-529`) holds eleven Yes/No/DontCare
combos (developments, lives, scores count, zone, conquest, deathmatch, prosperity, countdown,
artifact, flags, territorial) plus the game-name substring; OK copies them and calls
`UpdateGameList`, Cancel discards. `GameScreenFilter::operator()` (`:933-999`) compares each set
flag with the mission metadata; a `DontCare` flag excludes nothing. The eight sort columns are
`STATUS` (colour then in-progress/object-model/stage, `:1002-1044`), `GAME` (`:1046-1052`),
`RUNNING` (in-progress first, then start time, `:1054-1065`), `SKILL` (max then min rank,
`:1067-1076`), `SERVER` (`:1109-1116`), `CORE` (`:1118-1125`), `PLAYERS` (`:1127-1133`) and
`SETTINGS` (the `gametypes.cpp` GameType index, **descending**, `:1135-1168`). `ResetFilters`
(`:900-927`) resets the stack to PLAYERS reversed.

**Before.** Only the name substring filtered anything; the eleven flags were `DontCare`
placeholders that excluded nothing, and columns 0/3/7 were "not selectable" because the
directory DTO carried no settings.

**After.**
* The directory carries the settings (see the data-path section); `LobbyGameFilters.Apply` is the
  native predicate for all eleven flags — a set flag against a listing that publishes nothing
  hides it (unknown cannot be proven).
* `LobbyGameSort.Columns` is all eight; `ListingKey` implements each native comparator as an
  ascending key (native comparators return "a sorts **after** b", so SETTINGS is negated to keep
  native's descending game-type order, and STATUS packs colour/in-progress/object-model/stage).
* `LobbyFilterDialog` is the popup model in native row order with the yes/no/dontcare cycle,
  OK/cancel semantics and check-state text; the screen binds it to the new rebindable
  `LobbyGameFilters` key (**L**, free in the lobby group) and draws it as a centred panel.
* The details panel adds the published `TYPE` (`gametypes.cpp` name), `STATE`, `GOALS`,
  `DEVELOPMENTS` and `LIVES`; it still shows only the player **count**, never names, and the open
  `/api/servers` listing still carries no rosters (P761b privacy fix — find-player remains the
  per-name query `GET /api/servers/findplayer`, opt-in per server, default off, native
  `FIND_PLAYER` semantics `gamescreen.cpp:1348-1352`).
* Known native comparator quirks, classified rather than copied: `SkillCompare` also returns true
  for two games with the *same* rank window (an inconsistent comparator — native bug), so the port
  uses a stable ascending `(maxRank, minRank)` key; `StatusCompare`'s squad tie-break and the
  yellow "guaranteed slots" branch are dead code in native (the branch is commented out,
  `:1008-1012`) and stay constant. Native's `bInvulnerableStations`/`bEjectPods`/`bExperimental`
  have no port mission parameter; the game-type classifier uses native `Reset`'s defaults.

**Tests.**
* `P806LobbyFiltersTests` (13 cases): every one of the eleven flags on/off, DontCare + unknown
  metadata, all eight columns, each new sort column's native ordering, the `gametypes.cpp` table
  and index mapping, `ResetFilters`, the dialog (eleven combos, seed, cycle both ways,
  commit/cancel, cursor) and the details lines.
* `P806ServerListLoopbackTests` (5 cases): a **real in-process lobby** (`LobbyTestApp`) with three
  **mixed games** registered over `POST /api/servers/register`, then the real `GET /api/servers`,
  the real client mirror and the real filter/sort models — developments/lives/territorial/flags+
  countdown/conquest filters each select the matching game, the status/skill/settings columns
  order the three, a settings-less legacy host still lists (unknown metadata), and a heartbeat
  refreshes the settings in place.
* `P806LobbyGameParamsTests` (Allegiance.Core.Tests): `FromMission` maps the live mission
  (P191/P302/P303 flags, lives, countdown, stage, developments) and fills native Reset defaults
  for the rest; a real heartbeat publishes the object to a real lobby and the listing hands it
  back.
* All of these fail on master (missing `ServerListing.Game`, `GameParams`, `LobbyFilterDialog`,
  `LobbyGameParams`, the eight columns).

**Shots.** `P806_assets/server-list-header-details-720p.png` (the eight-column header
`[STATUS] GAME RUNNING SKILL SERVER CORE PLAYERS v SETTINGS`, three real listings, the details
panel showing `TYPE: Territorial / STATE: waiting / GOALS: territorial / DEVELOPMENTS: on /
LIVES: unlimited`), `server-list-filter-dialog-720p.png` (the eleven-combo popup, Conquest=Yes,
Territorial=No), `server-list-filtered-720p.png` (only the Conquest game remains; details show
`TYPE: Conquest`). All three were opened and read.

## Row 4 — General default-order target resolution (commander, 1.668) — done

**Native rule.** `IshipIGC::GetDefaultOrder(ImodelIGC* pmodel)` (`Igc/shipIGC.h:2071-2161`):
* Miner/Builder: a **sideless** asteroid that `HasCapability(m_abmOrders)` becomes Mine/Build,
  else Goto (`:2080-2090`). `m_abmOrders` is the drone's `DataShipIGC.abmOrders`
  (`shipIGC.cpp:104`; miners `c_aabmMineHe3`, builders `c_aabmBuildable` — the training/offline
  creators use exactly those, `trainingmission.cpp:613-617`; a builder with a base takes its
  station type's `GetBuildAABM()`, `shipIGC.h:2053-2057`).
* Layer: buoy or warp → Build (`:2092-2097`).
* Wingman/Player/CheatPlayer, friendly target → Defend, then (only when not docked on a parent):
  ship lifepod/rescue pairing → Pickup, carrier + LandOnCarrier → Goto (`:2108-2119`); station
  CapLand/Rescue|Land/Land ladder (`:2121-2136`); own lifepod + rescue probe → Goto
  (`:2137-2146`).
* Otherwise enemy/neutral: treasure → Pickup; probe → Attack (lifepod vs RescueAny → Goto);
  any sided model → Attack (`:2147-2161`).

**Before.** `CommandOrderResolver` contained two things: the full native table
(`ResolveDefaultOrder`, already used by the issue-order key and the plain target pick) **and** a
legacy `Resolve` helper that only mapped builder+asteroid. The row was marked on the legacy
helper, and the table's miner/builder branch ignored the asteroid capability (any rock became a
Mine/Build target, which the server then refused).

**After.**
* The legacy `Resolve` helper is deleted — one resolver, one truth; both production paths
  (`Game1.Targeting.cs` issue-order, `Game1.Orders.cs` target pick) call `ResolveDefaultOrder`.
* `CommandTargetProfile` gained `AsteroidAbm`, filled from the world's asteroid
  `AabmCapabilities` (`Game1.Targeting.BuildTargetProfile`), and the miner/builder branch now
  matches native: sideless asteroid **with** the commandee's orders ABM (Miner →
  `c_aabmMineHe3`, Builder → `c_aabmBuildable`), otherwise Goto.
* The client does not decode a drone's `DataShipIGC.abmOrders` from the wire (the port's
  EXPORT_SHIP/NEW_SHIP_INSTANCE codecs read the slot and discard it, and both only carry player
  ships), so the mask is derived from the hull role — the same masks the stock core's miner and
  builder types use. Documented in the code; the server still rechecks authoritatively.

**Tests.** `CommandOrderResolverTests` rewritten to drive the production table (builder at a
buildable rock → real Build ORDER_CHANGE shape; miner at He3 → Mine; wrong capability/unknown
capability → Goto; non-builder at a rock → Goto; cluster → no order). `P757DefaultOrderTests`
updated for the capability gate (and still pins every native branch); `P728CommanderTests` and
`Hud11LayoutPerfTests` unchanged and green. On master the miner/builder capability cases fail
(the branch produced Mine/Build for any asteroid).

**Classification.** design. The orders-ABM derivation is a documented adaptation pinned to the
stock core's drone types; a follow-up that decodes `abmOrders` from drone ship exports would
remove it (server-side change, not needed by any current row).

## Row 5 — Drones: miners / Native optimal travel controls (commander, 1.668) — split, still partial

**Native rule.** `GotoPlan` (`Igc/igc.h:5285-5365`; `Igc/common.cpp` `Execute` `:1793-1828`,
`SetControls` `:1830-2300+`) is a full optimal-travel controller, not a steering helper:
* waypoints (`Waypoint`, `igc.h:5201-5283`) with objective Enter/Goto, per-target approaches
  `DoApproach`/`GetGotoPosition` and `GotoPositionMask` flags (NoDodge/Pivot/Enter/DodgeShips);
* cross-cluster routing by `FindPath` with a coward/brave retry, re-planned when the ship or the
  target changes cluster (warp waypoint), with the `c_wpTarget`/`c_wpWarp` mask;
* a stopping-distance solve from the drag equations (rest position `positionRest`,
  `distanceRest`), throttle control, `turnToFace`, `Dodge` against ships/asteroids/mines and
  afterburner/boost handling (`m_pship->GetWantBoost()`, `stateM |= afterburnerButtonIGC`).

**Before/after.** `src/Allegiance.Core/Sim/Ship.cs` `ApplyNpcAutopilot` (~`:5190`) still uses the
simplified steering the P743 split documented; this wave did not change it. The economic cycle
(`Mine readiness`, `Unload and repeat`) is green and unchanged; its tests certify outcomes
(decisions, ore accrual, sell), not identical trajectories.

**Why split.** A faithful port needs the waypoint/path/dodge/stopping-distance machinery plus a
new trajectory-certification harness, which is well over a day; the brief explicitly allows the
split. Follow-up plan:

1. **Extract the native pieces into headless models** under `src/Allegiance.Core/Sim/Flight/`:
   `GotoPosition` (pure: ship pose/hull numbers + target geometry → goto point, facing, skip
   model, mask) and `StoppingDistance` (the `distanceRest`/`positionRest` solve at
   `common.cpp:1950-1995`), both table-driven and unit-tested against the native formulas.
2. **Waypoint/GotoPlan state machine** per ship (`PlanTarget` = target + objective, `PlanWarp`,
   masks), driven from `ApplyNpcAutopilot`; `FindPath` maps onto the existing
   `Sector`/`ClusterGraph` warp graph (the port already has the graph for the minimap), with the
   coward/brave retry and the target-changed-cluster invalidation.
3. **Dodge/turn integration**: `Dodge` (`common.cpp:1693-1788`) needs the collision model the
   port already uses for ship/asteroid collisions; `turnToFace` (`:152-190`) is an orientation
   controller to port alongside the existing `TurnRate` code.
4. **Certification harness**: record a reference trajectory from the native formulas in a
   table-driven test (positions/controls at fixed dt step), then assert the port's
   `ApplyNpcAutopilot` matches within a tolerance over a scripted mine/sell/warp route; extend
   `MinerDecisionCycleLiveTests` with the same route so "identical trajectories" is testable.
5. Only then flip the row: it is `partial` until step 4 exists.

## Row 7 — Lobby chat / Lobby-directory relay feedback (lobby, 2.000) — n/a

**Native rule.** The cited handler `clintlib/appmsg.cpp:2640-2665` is `FM_CS_CHATMESSAGE →
ReceiveChat`: the client renders a relayed chat for its target, with mute/`FilterChatsToAll`/
lobby/unknown/quick-comms filters in `trekigc.cpp:4480-4560`. "Lobby-directory" chat in native is
the Zone **club lobby** — a separate service with player identities that relays chats between
lobby members.

**Why n/a (era/infrastructure).** This port replaced the Zone lobby with `Allegiance.Lobby`, a
stateless HTTP server directory: it has no player sessions, no identities and no chat transport,
so there is no directory-wide channel to relay. Faking one would mean inventing identities and
moderation — a privacy regression the public repo does not want.

**What is covered instead (and tested).** The client half of the relayed-chat path is complete:
`FM_CS_CHATMESSAGE` is applied for every channel the port's server forwards (including mute at
intake, `ClientWorld.Callbacks.cs:177` = native `ppi->GetMute()`), and the team-lobby chat pane
now displays the relayed lobby chat with the native channel set (row 1,
`P757ChatPaneTests` P806 cases). The modern alternative (a WebSocket lobby chat with an
identity/moderation model) is deliberately not added.

## Classifying every native behaviour touched

| Behaviour | Class | Note / modern alternative |
|---|---|---|
| Pane filters are per-screen (lobby channel set vs in-flight no channels) | design | The port keeps one pane model and clears the channel list on entering flight; no alternative wanted. |
| Lobby chat target is a 3-button choice, not a cycle | design | TAB is the keyboard stand-in for the buttons; a clickable bar is UI polish, not behaviour. |
| Filter Chats To All default ON | design (native default) | Kept: native filters mass all-chat in game. |
| Options Sound/Game as submenus | design | Native menu shape; no alternative. |
| Native sound quality/hardware/DSound8, Force Feedback, mouse-accel submenu | **era constraint** | Era-specific hardware toggles with no port control; omitted rather than faked. A modern audio device list would be the replacement. |
| Eleven server-list filters + eight sort columns from MissionInfo | design | The directory publishes exactly the native fields. |
| `SkillCompare` returns "after" for identical rank windows | **bug** (native) | Inconsistent comparator; the port uses a stable ascending key instead of reproducing the tie inversion. |
| Status comparator's yellow/guaranteed-slots branch and squad tie-break | dead code (native) | Commented out upstream (`gamescreen.cpp:1008-1012`) / no squads; kept constant and documented. |
| Miner/Builder default order needs the rock's capability | design | Now matched; no alternative. |
| Drone orders ABM derived from hull role | **adaptation** | The port does not decode a drone's `DataShipIGC.abmOrders`; deriving the mask from the hull matches the stock core. Follow-up: carry `abmOrders` on drone exports. |
| `GotoPlan` optimal travel | design | Split out (row 5) with the plan above. |
| Zone lobby-directory chat relay | **era constraint** | Replaced by a stateless HTTP directory; modern alternative = WebSocket lobby chat with identity + moderation (deliberately not added). |

## Code changed

* `src/Allegiance.Lobby/ServerListing.cs`, `ServerRegistry.cs` — `GameParamsDto` /
  `GameParamsRequest`, optional on the register payload, normalized with native Reset defaults,
  stored on the listing, projected on the DTO.
* `src/Allegiance.Server/LobbyHeartbeat.cs`, `Server.cs`, `Server.Broadcast.cs`,
  `Server.Lifecycle.cs` — `LobbyGameParams.FromMission`, the cross-thread-safe `LiveGameParams`
  snapshot and the register payload field.
* `src/Allegiance.ClientCore.Game/ServerListing.cs`, `LobbyGameSort.cs`,
  `LobbyGameMetadata.cs` (new), `Game1.Session.cs`, `Game1.cs`, `Rendering/InputActionCatalog.cs`
  — the client mirror, the eight columns, the eleven flags, the filter dialog UI + key and the
  details lines.
* `src/Allegiance.ClientCore.Game/OptionsMenuModel.cs` (new), `Game1.BenchmarkSharing.cs`,
  `Game1.IntroScreen.cs` — the native options tree.
* `src/Allegiance.ClientCore.Game/Rendering/ChatPaneModel.cs`,
  `Rendering/ChatTargetLogic.cs`, `Game1.Panes.cs`, `Game1.FlightInput.cs`,
  `IntroPreferences.cs` — the chat filters, lobby chat target and team-lobby chat pane.
* `src/Allegiance.ClientCore.Game/CommandOrderResolver.cs`, `Game1.Targeting.cs` — one resolver,
  asteroid capability gate.
* `tools/p806-ui-proof/` (new) — the real-window proof that produced the shots below.
* Tests: `tests/Allegiance.ClientCore.MonoGame.Tests/P806LobbyFiltersTests.cs`,
  `P806OptionsMenuTests.cs`, `CommandOrderResolverTests.cs`, `P757ChatPaneTests.cs`;
  `tests/Allegiance.Lobby.Tests/P806ServerListLoopbackTests.cs`;
  `tests/Allegiance.Core.Tests/P806LobbyGameParamsTests.cs`.
* `docs/parity/parity-matrix.csv` rows 1-7 above; `GLOSSARY.md` (new model/tool entries).

## Screenshots (all captured from the real window, opened and read before claiming)

| Shot | Shows |
|---|---|
| `P806_assets/options-root-720p.png` | options root with `Sound Options >` / `Game Options >` |
| `P806_assets/options-sound-submenu-720p.png` | Sound Options submenu |
| `P806_assets/options-game-submenu-720p.png` | Game Options submenu incl. `Chat/Mute/Filter >` |
| `P806_assets/options-chat-submenu-720p.png` | Chat/Mute/Filter submenu, action-style label |
| `P806_assets/server-list-header-details-720p.png` | eight-column header, three mixed games, details metadata |
| `P806_assets/server-list-filter-dialog-720p.png` | the eleven-combo filter dialog |
| `P806_assets/server-list-filtered-720p.png` | filtered list + Conquest details |
| `P806_assets/lobby-chat-classic-720p.png` | team-lobby chat pane, Classic |
| `P806_assets/lobby-chat-modern-720p.png` | team-lobby chat pane, Modern |

The shot tool (`tools/p806-ui-proof`) drives the production `Game1`: the browser lane spawns the
real `Allegiance.Lobby`, registers three mixed games over its API, and lets the client fetch them;
the ready lane runs a real server with a NotStarted mission and injects chat through
`ClientWorld.Apply(CHATMESSAGE)`. Reproduce with
`dotnet run --project tools/p806-ui-proof -- <outdir> [--intro|--ready]`.

## Gate

| Check | Result |
|---|---|
| `dotnet build AllegianceCore.slnx` | **0 warnings, 0 errors** |
| `tools/build-tools.sh` | **all 74 tool projects built**, 0 warnings |
| AOT publish (`Allegiance.Server`, Release linux-x64, `PublishAot=true`) | **succeeded**, no IL2026/IL3050 (the new nested register-payload record rides the existing source-generated `ServerJsonContext`) |
| New/affected tests, 3× alone | `P806LobbyFiltersTests` + `P806OptionsMenuTests` + `P757ChatPaneTests` + `CommandOrderResolverTests` + `P757DefaultOrderTests` + `P728CommanderTests`: **63/63, three times**; `P806ServerListLoopbackTests`: **5/5, three times**; `P806LobbyGameParamsTests` + `LobbyHeartbeatTests` + `P739LobbyPeriodicTests`: **12/12, three times**; `CommandedDroneBuildKeyLiveTests` + `Hud11LayoutPerfTests`: **18/18** (live/targeted reruns after the capability-gate fixes) |
| `tools/run-tests.sh --tier fast` | **PASS twice — 10,212 tests, 289.3 s and 286.5 s, 0 failed** (3 NotExecuted = artwork/GPU skips). Two earlier sweeps each had one load flake: `P802TeamCivLiveTests.Random_change_resolves_to_a_real_legal_civ_and_relays_it` (passes 3/3 alone) and `DockedStationUpdateLiveTests.Docked_player_receives_seen_own_station_update_within_native_cadence` (passes alone; ledgered in `docs/TEST_FLAKES.md` next to its sibling's identical entry) |
| `scripts/parity-scorecard.py` | **validated 670 rows**; TOTAL 93.92% weighted (screens 89.49%, lobby 96.91%, commander 97.94%) |
| `python3 tools/check-glossary-links.py` | OK (104 paths) |
| `docker compose config -q` | valid (no service/env/port change in this task) |
| `git diff` publish-guard scan | no `/home/<user>/` paths in `src/tests/scripts/tools/docker/.github`, no secrets/emails added; the 9 PNGs under `docs/reports/P806_assets/` (544 KB) are new media and need Domingo's review before push |

Reproduce the shots with `~/.dotnet/dotnet run --project tools/p806-ui-proof -- <indir> [--intro|--ready]`.

## Unfinished / known limits

* Row 5 (miner `GotoPlan`) is intentionally still `partial` with the plan above.
* Row 7 is `n/a` by infrastructure; a lobby chat service is out of scope.
* Native options entries with no port control are omitted (listed in row 2), and the
  "Filter Chats To All" preference implements the receive-side rule only — native's censor /
  unknown-chat / quick-comms filters have no port equivalent (no word list, no unknown-chat
  notion).
* The client derives a drone's orders ABM from its hull role (row 4) because the port does not
  decode `DataShipIGC.abmOrders` for drones; a follow-up could carry it on the drone export.
* The status column's squad tie-break and yellow "guaranteed slots" branch are native dead code
  and stay constant.

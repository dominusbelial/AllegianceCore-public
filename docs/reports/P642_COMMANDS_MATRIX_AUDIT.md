# P642 — commands and matrix accuracy audit

Completed the Codex implementation and socket-free verification in the working tree.
No commits or pushes. The pre-existing staged P640/P641 work is preserved; P642
changes are unstaged. Live socket tests and presented-window proof are prepared
for the unrestricted runner, as required by the brief's Codex lane rules.

## Player behavior

- Target actions now distinguish **nearest**, **next**, and **previous**. Nearest
  ignores the current selection; cycling walks the matching model list with
  wraparound. Missing selections fall back to nearest; an empty match clears
  the selection. Generic Target preserves the current type/relation, otherwise
  searches enemy ships/stations. All three forms are registered and rebindable.
- Warp targeting searches known alephs in the viewed sector. Flag targeting
  selects enemy flag-code treasures; Artifact selects neutral flag-code treasures.
  Own/allied flags, ordinary parts/cash and other-sector treasures are excluded.
  Treasure targets resolve position and sector for brackets and autopilot, and
  vanish when the existing DESTROY_TREASURE path removes the entity.
- Enemy/Friendly ship and base families have explicit next/nearest/previous
  actions. Native's generic enemy-ship family filters rescue/miner/builder hulls.
  Bases use the source hull's dockability; pods also consider rescue probes.
  AlliedBase excludes one's own side when allies exist, and uses native's
  friendly-base fallback without alliances. Base searches exhaust the local
  sector, then traverse connected sectors breadth-first; remote distance is
  measured from the destination aleph. Unreachable bases are excluded.
- Fixed P623 class triads to walk model order, consume press edges during
  composition, prime held keys at flight entry, and include allied friends.
  Major-base searches now traverse other sectors instead of filtering locally.
- **Combat** returns to cockpit; **Command** uses the existing F3 action and
  returns any command-family camera to combat/hangar. **Nav** toggles the sector
  pane. **Inventory** toggles airborne mounted/cargo inventory; docked it switches
  loadout/hangar and closes banishable overlays before leaving loadout.
  **GameState** shows live mission stage, side activity, pilots, kills and money.
  **Team/MiniTeam** switch expanded/compact roster sizes and banish other modal
  panes. Inventory and Nav remain independent, matching console.h's overlay mask.
- External view toggles the native generic external camera/cockpit. Keyboard
  pitch/yaw/roll rotates its remembered external orientation. Zoom changes
  cockpit FOV by radians/sec (5–50 degrees) and external distance by dt*distance
  (100–6000); command zoom retains P640's factor-two distance rule and drag guard.
- Grid toggles the existing command grid and saves ShowGrid beside the keymap in
  console-settings.json. Malformed/missing settings use the visible default.
  OccupyNextTurret sends BOARD_SHIP naming the current parent; the server selects
  the next equipped seat. Pilots without a parent send nothing.
- Escape opens a main menu with Resume, Help, Controls, Credits, Game State and
  Quit. Existing chat/quick-chat/briefing cancellations take precedence. Override,
  connection-lockdown and game-over surfaces suppress menu opening. Menu input
  consumes flight/chat input; quitting requires selecting Quit or the existing
  Alt+F4 action.

All new actions appear in F1 help and F6 Controls. Team-pane captions also use live rebound keys; the unavailable-wing note uses plain player-facing language. Effective defaults preserve
existing port bindings, while Restore native metadata follows defaultinputmap.mdl.
The new shortcuts are:

| Action | Port default | Native default |
|---|---|---|
| Warp / Flag / Artifact / AlliedBase next | Alt+W / Alt+G / Alt+A / Alt+Z | unbound |
| Enemy previous / FriendlyBase previous | Ctrl+F / Ctrl+B | same |
| Combat / GameState / Nav / Inventory | Alt+F2 / Alt+F7 / Shift+F7 / Shift+F4 | F2 / slash / F7 / F4 |
| Command / Team / MiniTeam | F3 / Ctrl+T / Alt+F6 | F3 / F6 / Shift+F6 |
| External / Grid / OccupyNextTurret | Alt+V / Ctrl+period / Alt+comma | unbound / Ctrl+period / unbound |
| Zoom in/out / MainMenu | keypad Add/Subtract / Escape | same primary keys |

Other new target forms default to unbound and can be assigned in F6. This avoids
existing cloak, unmount, team-pane and cargo-drop chords. Native alternate zoom
keys (semicolon/quote) remain occupied by the established port clear/center
bindings. Native keyboard defaults are metadata; effective port chords remain
explicitly classified divergences. Fixed VK_ADD/VK_SUBTRACT decoding to the real
keypad keys. The metadata test checks every catalogued TK against embedded MDL.

## Native evidence and classification

Native paths below are relative to `~/Development/Allegiance/src/`.

| Behavior | Native handler/rule | Classification and port |
|---|---|---|
| Next/nearest/previous masks | WinTrek/WinTrek.cpp:8957-8969; Igc/common.cpp:470-510,526-544,638-645 | Keep; Game1.TargetCommands.cs ChooseTarget, action-specific press edges |
| Generic target type/relation | WinTrek/WinTrek.cpp:267-281,10274-10284 | Keep; default enemy ships/stations and similar-type selection |
| Enemy utility / friendly ship masks | WinTrek/WinTrek.cpp:10154-10160,10287-10295 | Keep; ShipRelationMatches; separate class filters remain available |
| Base hull ability and source carrier | WinTrek/WinTrek.cpp:10194-10262 | Keep Land/CapLand/pod rescue and any-cluster flags |
| Friendly versus allied-only bases | WinTrek/WinTrek.cpp:8987-9023; Igc/common.cpp:266-285 | Keep alliance-group relation and native no-allies fallback |
| Remote search order and arrival position | Igc/common.cpp:650-743 | Keep local-first BFS and reversed previous traversal; route over known exported warps |
| Flag/artifact/warp filters | WinTrek/WinTrek.cpp:10335-10357; Igc/common.cpp:266-277,337-340; Igc/treasureIGC.cpp:128-149 | Keep flag-code + side classification; reuse live treasure entities |
| Major-base flag filter | WinTrek/WinTrek.cpp:10202-10206,10266-10270 | Keep flag ability; correct P623's local-only live wrapper |
| Console GameState/Nav/Command/Combat | WinTrek/WinTrek.cpp:9871-9933,9977-9999; WinTrek/console.h:61 | Keep mode/overlay transitions; reuse map/command/panel renderers |
| Inventory docked/airborne | WinTrek/WinTrek.cpp:10001-10023 | Keep loadout/hangar/overlay switching; modern explicit inventory list uses P641's real inventory |
| Team/MiniTeam sizes | WinTrek/WinTrek.cpp:10038-10081; WinTrek/teampane.cpp | Keep switch/toggle semantics; reuse TeamPaneLayout and renderer, compact own-side roster |
| External placement and keyboard orientation | WinTrek/WinTrek.cpp:9796-9800,7108-7116,8601-8616 | Keep generic external placement and remembered orientation |
| Zoom limits/rates | WinTrek/WinTrek.h:25-27; WinTrek/WinTrek.cpp:73-82,8393-8409,8538-8550,8618-8645,10469-10511 | Keep native continuous-key rates and clamps; existing wheel throttle stays native throttle outside command view |
| Grid preference | WinTrek/WinTrek.cpp:10433-10437 | Keep toggle/persistence; JSON replaces Windows registry |
| Next turret request | WinTrek/WinTrek.cpp:10452-10464; FedSrv/FedSrv.CPP:2347-2402 | Keep current-parent BOARD_SHIP; no client-side seat guess |
| Main-menu invocation and content | WinTrek/WinTrek.cpp:9304-9320,4116-4236 | Keep menu invocation/composition gate; reuse available port panes and explicit Quit; legacy platform/account option pages remain unavailable |
| Alliance updates | clintlib/appmsg.cpp:2554-2569; Igc/igc.h:4297-4302 | Keep six signed group values; groups replace prior state and reset on reconnect/rotation |
| Initial alliance state for a late join | clintlib/appmsg.cpp:2424; FedSrv/fsmission.cpp:5288-5291 | Native seeds through MISSIONDEF.rgfAllies; port sends existing CHANGE_ALLIANCES once at join (Server.Lobby.cs CreateAllianceSnapshot) |
| Keyboard identity/default metadata | WinTrek/trekinput.cpp:484-598; Igc/igc.h:226-451; ~/Development/Artwork/defaultinputmap.mdl:3-114 | Keep native identities/default metadata; conflicting effective chords remain port choices |

Modern alternatives considered: one parameterized selector and pure transition
rules replace duplicated TK handlers; shared existing panes avoid parallel UI
implementations; authoritative BOARD_SHIP retains server seat arbitration; the
existing alliance codec avoids expanding MissionDef's partial side-table codec.
Stable insertion-order ties replace container-specific equal-distance tie quirks.
Remote routing uses known warp connectivity; native ripcord shortcut heuristics,
undisclosed visibility and absent career/wing/platform data are not synthesized.

## Matrix accuracy pass

Verified **231 rows**: all **203 commands** rows and every other row touched by
P623–P641, plus CHANGE_ALLIANCES newly consumed here. The scope was obtained by
comparing the matrix revisions for P623/P624/P625/P625b/P626/P627/P638/P639 and
P640/P641's staged matrix changes. The P630–P637 packs had no additional changes
to this matrix. Each row records action-map registration, live handler, existing
or new test evidence, status and finding in [P642_VERIFIED_ROWS.csv](P642_VERIFIED_ROWS.csv).
Missing/NA rows explicitly record the absence of an implementation or applicable
behavior; arithmetic helpers and V-camera cycling do not establish separate TK
command coverage.

**227 rows changed**, including citation-only corrections. The complete list,
before/after statuses and citations, and reason for each change is in
[P642_CHANGED_ROWS.csv](P642_CHANGED_ROWS.csv). Native command citations now name
handlers where present instead of only enum declarations. Existing P623 class
rows cite the actual dispatch and shared live selector. Fixed stale Ripcord=L,
PromoteTurret=plain digits, Help=H and raw-backslash-alias claims. Default metadata
corrections include Target's unbound native default, true Shift defaults for
nearest friend/enemy/base, unbound Prev/NextWeapon and PromoteTurret keys,
ToggleCommand's native Shift+F3, and native movement/boost/quick-chat defaults.

Closed **30 command rows**: ZoomIn/Out, MainMenu, ViewExternal, Combat/GameState/
Nav/Inventory/MiniTeam console modes, generic nearest/previous and friendly next,
four generic previous ship/base commands, Grid, OccupyNextTurret, all nine
flag/artifact/warp forms, and all three AlliedBase forms. CHANGE_ALLIANCES is now
`done`; fedmsg-wiring.csv and dispatch coverage agree: **89 consumed / 29 known
unconsumed**, with **102 native supported server-message identities** accounted for.

The audit corrected these previously overstated statuses:

| Row | Before → after | Evidence / reason |
|---|---|---|
| TK_ViewMissile | done → partial | V-cycle offers missile camera; no separately bindable TK_ViewMissile action |
| TK_ViewChase | done → partial | V-cycle offers chase; no separately bindable native TK_ViewChase action |
| TK_ViewTurret | done → partial | V-cycle offers port carrier camera; native command handler is commented out and no TK action exists |
| TK_Throttle33 | done → partial | Preset arithmetic exists but no action-map entry or live preset dispatch |
| TK_Throttle66 | done → partial | Preset arithmetic exists but no action-map entry or live preset dispatch |
| TK_ConModeInvest | done → partial | Investment pane action is docked-only; native can toggle investment while airborne |
| TK_TargetCommand | done → missing | No accepted-target model or live TK action; P417 method discussion is not an implementation |
| TK_ExecuteCommand | done → partial | Home accepts and executes pod pickup only; no separate TK_ExecuteCommand action for arbitrary queued targets |
| TK_PauseTM | partial → missing | Training scripts are not an implemented pause command; no action/handler |

P623 had nearest/previous helpers and class-triad dispatch. Generic TargetNearest
and TargetPrev lacked action-map entries/live dispatch, so P642 actually wires
them; merely changing those old descriptions to done would have been incorrect.
The nine downward corrections remain concrete follow-up command gaps, even where
a broader feature or arithmetic helper exists.

## Corrected scorecard

Measured against the actual P642-entry working matrix including P640/P641,
using done/non-NA; partial rows earn no done credit. The brief's 73.3%/68.5%
baseline is older and is not the before measurement for this pack.

| Area | Weighted before → after | Unweighted before → after |
|---|---:|---:|
| commands | 78.0% → 87.5% | 71.6% → 83.2% |
| fedmsg | 78.3% → 79.0% | 77.6% → 78.4% |
| screens | 86.8% → 86.8% | 79.2% → 79.2% |
| parts | 86.3% → 86.3% | 82.5% → 82.5% |
| commander | 100.0% → 100.0% | 100.0% → 100.0% |
| lobby | 76.5% → 76.5% | 75.0% → 75.0% |
| total | 80.6% → 84.7% | 76.3% → 82.0% |

The current matrix has 438 rows. Existing career database/wing UI limits remain
explicit in the relevant screen citations. Scorecard refresh also updates the
P617 overview; these scores describe matrix coverage, not full native UI fidelity.

## Verification and unrestricted handoff

- `dotnet build AllegianceCore.slnx --no-restore -m:1`: **0 warnings / 0 errors**.
- Socket-free test results: **494 passed: MonoGame 448, ClientCore 28, Core 18; 105 P642 cases**. Includes native-MDL
  metadata verification, P625b no-raw-key guard, rebinding/conflict regressions,
  target direction/order, flag/artifact rules, any-cluster BFS, allies and late
  joins, turret-parent gate, console/hangar/menu rules, visible zoom matrices,
  grid persistence, and P623–P641 regressions.
- Tests executed through xUnit's in-process discovery/execution API using the
  temporary `/tmp/p640-runner` harness. Logs: `/tmp/p642-build.log`,
  `/tmp/p642-mono-tests.log`, `/tmp/p642-client-tests.log`, `/tmp/p642-core-tests.log`.
- An earlier broad Core selection included eight CargoBayReloadTests live cases;
  all eight were refused at socket creation (`Permission denied`). That mixed
  log is `/tmp/p642-core-mixed-tests.log`. The final socket-free selection omits
  that class. No live/window success is included in the measured test count.
- `scripts/check-p642-parity.py`, `git diff --check`, and
  `bash -n scripts/proof-p642.sh`: passed. The parity checker verifies all command
  rows are audited, statuses match, and each source/test file:line exists.
- Written/built, pending unrestricted execution: the existing
  PromoteTurretKeyLiveTests now equips two turret sockets, sends the production
  same-parent BOARD_SHIP request, asserts advance to the second socket and wrap
  to the first, then exercises the existing promotion/receive path.
- Run `scripts/proof-p642.sh [output-directory]` on the unrestricted runner.
  It creates an isolated detached temp worktree without commits, overlays this
  working tree, builds, starts P572's local server and a presented MonoGame
  window, and invokes the production warp selector followed by Inventory,
  GameState, MiniTeam, Team, Combat, External, zoom, Command, grid and MainMenu transitions. It requests frames at
  5/8/12/16/20/24/28/32/36/40/44 seconds, requires all mode/zoom/grid/menu log events and eleven real frames, and
  removes the temporary worktree afterward. Inspect the frames for the warp
  target and mode switches before accepting visible parity; then run the full
  live suite, including the turret proof and alliance join/update tests.

## P642b

Continued the interrupted working tree on 2026-10-14, including the brief's
live/socket update. Changes remain uncommitted; no commits or pushes were made.
The earlier socket restrictions above describe the P642 run, not this verification.

### Root causes and fixes

1. **Renderer disposal guard:** `09f757f9` already contains the real
   `_commandGridRenderer?.Dispose()` call in `Game1.UnloadContent` (line 1716).
   The failure was the guard's stale, independently maintained `DisposedOnUnload`
   registry, not a missing disposal call. Retained the production disposal,
   synchronized that one registry entry, and strengthened the guard to check
   every reflected disposable renderer's actual `Dispose()` call inside the
   source method. Comments cannot satisfy that check. This is the genuine test
   bug that justifies retaining the interrupted run's registry correction:
   deleting the real disposal now fails the guard even with the registry entry.

2. **F1 help overflow:** wave 2 enlarged the camera/console groups, and packing
   groups in catalogue order placed the large late group below the panel.
   `HelpOverlayLayout` now packs tallest groups first into the shortest column,
   preserves catalogue order for a single column and stable equal-height ties,
   and returns slots indexed in the original order. The action map still
   generates all help entries. `HelpOverlayTests` is unchanged.

3. **Turret promotion confirmation:** the gunner's own boarding status arrived
   promptly, but the carrier's client could still have `gunner.ParentId == -1`
   when PROMOTE arrived. PROMOTE names only the promoted child, so the new
   receive handler returned early on that client; later periodic status updated
   the seats without the demotion notice. Captured this with temporary receive
   instrumentation (removed). Successful BOARD_SHIP now immediately flushes
   the existing SHIP_STATUS to peers on that mission/side, before any later
   PROMOTE. Native sets the boarder's state/parent and flushes queued messages
   in `FedSrv/FedSrv.CPP:2450-2470`; the client derives the old parent and posts
   both notices in `clintlib/appmsg.cpp:824-849`. No new acknowledgement codec
   or client-side seat guess was introduced. The existing live proof still
   checks next-seat, wraparound, the role swap, and both exact notices.

4. **P641 inventory round trip:** the original `p641` character did not match
   the configured harness's `mcp-` prefix, so its money/tech grant never applied
   and BUY_LOADOUT could be refused. Retained the partial fix to use `mcp-p641`
   and phase-specific timeout messages. After that fix, the live run exposed
   purchased cargo incorrectly realized as an equipped weapon at unsigned
   mount 255, instead of signed cargo mount -1. BUY_LOADOUT now routes negative
   mounts into `Ship.CargoHold` through `TryMountCargoPart`, without realizing
   cargo weapons. Loadout broadcasts include the whole inventory, hardware
   value includes cargo, and the wire part ceiling counts both stores. Native
   purchase preserves signed mounts (`Igc/shipIGC.h:901-921,1030-1039`), and
   negative mounts address cargo (`Igc/shipIGC.cpp:1520-1548`). The live test adds
   server cargo/equipped assertions and retains all production unmount, mount,
   replace, four weapon selections, booster fuel/release, and drop checks.

5. **Ammo RELOAD reset:** the port must clear P641's cached fractional mounting
   remainder as well as the byte fraction, including when that byte is already
   zero. RELOAD now removes the relevant cached progress unconditionally;
   clearing a loadout also clears that cache. Mount advancement now stops while
   docked or boarded, matching native's cluster/parent update gates
   (`Igc/shipIGC.cpp:266-271`; native docked ships leave their cluster).

6. **Launcher RELOAD reset:** the same stale-progress fix applies to the equipped
   launcher at mount zero. Native resets ammo weapons and partially replenished
   launchers to zero (`FedSrv/FedSrv.CPP:6574-6581,6637-6638`), then advances their
   readiness on later flight updates (`Igc/weaponIGC.h:33-49`,
   `Igc/dispenserigc.cpp:86-102`). The two old live tests asserted a permanently
   zero fraction after pumping more ticks for another socket's receipt; with
   P641's real mounting progression this could reach 240 or observe a partial
   fraction such as 2. Their live assertions now require an unready part after
   reload, while preserving the exact ammo/launcher amounts, cargo amounts and
   sector broadcast checks. Two new deterministic simulation cases assert
   **exactly zero immediately after each reload**, prove a second reset clears
   a sub-byte remainder (0.75 + 0.5 would otherwise become byte 1), retain zero
   while docked/boarded, then advance and finish in flight. This corrects the
   asynchronous observation premise without disabling native mounting or
   dropping the exact-zero reset regression check.

7. **Side-only elimination:** inspected `git diff 89a60f77`. Wave 2 did not
   change side elimination, GAME_OVER/GAME_OVER_PLAYERS construction or routing,
   or side/score roster logic. Its sole server change was the initial alliance
   snapshot during logon plus its builder in `Server.Lobby.cs`; neither mutates
   side activity or game-over state. The named test is already quarantined in
   `docs/TEST_FLAKES.md` with a measured 2/10 isolated timing-flake rate.
   No elimination code or test was changed for P642b.

### Measured verification

- `dotnet build AllegianceCore.slnx --no-restore -m:1`: **0 warnings / 0 errors**.
- Each brief-listed test ran in a fresh process three times, sequentially, with
  real `dotnet test --no-build`: **21 passed / 0 failed / 0 skipped**.

| Brief item / isolated test | Passes |
|---|---:|
| 1 — RendererDisposalGuardTests.Every_disposable_renderer_field_is_disposed_in_UnloadContent | 3/3 |
| 2 — HelpOverlayTests.Table_BalancesIntoTypicalPanelWithoutOverflow | 3/3 |
| 3 — PromoteTurretKeyLiveTests.Live_promote_turret_key_over_production_send_promotes_the_gunner | 3/3 |
| 4 — P641FlightPartsLiveTests.Production_inventory_commands_weapon_selection_and_booster_roundtrip | 3/3 |
| 5 — CargoBayReloadTests.Reload_transfers_ammo_updates_ship_ammo_resets_weapon_fraction_and_broadcasts | 3/3 |
| 6 — CargoBayReloadTests.Reload_swaps_cargo_launcher_ammo_into_equipped_partially_loaded_launcher | 3/3 |
| 7 — SideEliminationLiveTests.Eliminating_side2_sends_side_only_gameover_to_it_alone | 3/3 |

Then ran the three **full, unfiltered assemblies once each**, sequentially,
including live sockets and quarantined tests:

| Assembly (`dotnet test tests/<assembly> --no-build`) | Passed | Failed | Skipped | Total |
|---|---:|---:|---:|---:|
| Allegiance.Core.Tests | 4098 | 1 | 0 | 4099 |
| Allegiance.ClientCore.Tests | 812 | 9 | 0 | 821 |
| Allegiance.ClientCore.MonoGame.Tests | 2117 | 0 | 3 | 2120 |
| **Total** | **7027** | **10** | **3** | **7040** |

All seven requested regressions also passed in their full assemblies. The three
skips are the existing optional Meshy pack catalogue/content tests; that private
pack is absent. No filter or new skip excluded any requested test.

Every other full-suite failure was subsequently run **three times in fresh
isolated processes**: **30 case passes / 0 failures / 0 skips**, covering all ten
failed cases (the travelling-fighter theory has two cases).

| Assembly / full-suite failure | Isolated reruns |
|---|---:|
| Core — ConquestTerritoryLiveTests.Conquest_win_in_one_mission_does_not_leak_to_another | 3/3 |
| ClientCore — BotAcceptsAndEngagesOrderLiveTests.ScenarioBot_AutoAcceptsAndEngagesTheSpecificCommandedTarget | 3/3 |
| ClientCore — BotFixRegressionTests.DefenseAfterArrival_ReturnsToOriginalObjective_AndPreservesStaging | 3/3 |
| ClientCore — BotFixRegressionTests.DefenseAlert_BoundedNearestWing_ResumesOriginalRouteAfterQuietAndMinimum | 3/3 |
| ClientCore — BotFixRegressionTests.DefenseDetour_FliesHomeWarp_PreservesObjectivePath_AndRecordsArrival | 3/3 |
| ClientCore — BotFixRegressionTests.DefenseDisableSwitch_RestoresTravelPastThreats_AndIgnoresAlerts | 3/3 |
| ClientCore — BotFixRegressionTests.DefenseRelaunch_EngagesVisibleBaseAttackerBeforeLeaving_AndPrioritizesBomber | 3/3 |
| ClientCore — BotFixRegressionTests.DefenseTravellingFighter_AnswersReceivedHitOnSelfOrFriendlyAsset_ThenResumes | 3/3 **each** (asset=false, asset=true) |
| ClientCore — ClientReadyUpUnfreezesNormalMapLiveTests.Harness_ready_up_starts_normal_map_mission_and_opposing_combat_bot_moves | 3/3 |

The Core failure is already quarantined in `docs/TEST_FLAKES.md` (2/10 historical
isolated failures). Its observed failure was the other mission's peer receiving
a GAME_STATE; no conquest or routing code changed in wave 2/P642b.

The ClientCore failures expose an existing fixture-order problem, beyond socket
load: `BotManager.DecisionSnapshotEnabled` is a `static readonly` copy of mutable
`ClientWorld.DecisionSnapshotEnabled` (`tools/allegiance-mcp/BotManager.cs:65`).
Snapshot fixtures temporarily enable the latter, while the defense fixture
disables it (`BotBaseDefenseTests.cs:14-16`). Initializing the manager while the
flag is enabled leaves its readers using snapshots after publishing is disabled;
its sector/pose reads then return empty-world results (observed sector -1 and
frozen bots). These manager/fixture files are unchanged from `89a60f77`; P642b
also makes no ClientWorld changes. The fresh-process 3/3 results distinguish
these order-dependent failures from the requested deterministic regressions.
No unrelated bot/snapshot code or tests were edited.

- The two new exact-zero/reset-progress simulation cases passed in isolation
  and in the full Core assembly.
- `scripts/check-p642-parity.py`: **231 audit rows / 438 matrix rows validated**;
  existing scores unchanged. `git diff --check` and
  `bash -n scripts/proof-p642.sh`: passed.
- Evidence: `/tmp/p642b-build.log`, `/tmp/p642b-results/*.trx`,
  `/tmp/p642b-full-{Core,ClientCore,MonoGame}.log`,
  `/tmp/p642b-verification.json`, and `/tmp/p642b-rechecks.json`.

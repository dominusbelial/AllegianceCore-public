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

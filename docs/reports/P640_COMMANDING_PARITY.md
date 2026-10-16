# P640 — commanding parity

Implemented in the working tree on `codex/wave2`; no commits or pushes.
Build and selected socket-free tests pass. Live socket tests and presented-window
frames remain pending the unrestricted runner; no screenshots were fabricated.

## Player behavior

- **F3** enters/exits the native tactical command camera. **Shift+F6** remembers
  and switches three-quarter/top-down framing. **Ctrl+F7** tracks the selected
  visible target. Arrows pan, existing thrust keys add half-speed pan, existing
  roll keys rotate the sector view, numpad +/- zoom, and the wheel zooms instead
  of changing cruise throttle. The native 8px edge gutter also pans the view.
- The camera draws the existing sector scene, the native circular XY grid,
  height drop lines/feet for ships, stations, asteroids and alephs, and NORTH.
  Draws subtract the eye; the view matrix contains rotation only (P612b).
  P612 transitions smooth camera mode, tracking subject and viewed-sector changes.
  Radar brackets now project with the displayed eye rather than the pilot position.
- Left-click selects a friendly command recipient; dragging selects a group;
  Shift-click adds/removes one. Right-click selects the order target and sends
  each recipient's order through the existing role resolver and `ORDER_CHANGE`
  sender. The existing issue-order key uses that same selection. The map consumes
  its own clicks. Manual camera movement does not fly the player's ship; an
  already-engaged autopilot can continue.
- **Ctrl+F8** follows the commanded drone/wingman externally; **Ctrl+F9** views
  through it. The actual visible source carrier is resolved for boarded ships;
  its hull is hidden in cockpit view. Unknown, dead, missing, enemy or other-sector
  follow subjects are refused; losing the subject returns to chase. The player's
  identity and order recipient are preserved. These two dedicated views are port
  additions, not invented native TK commands. The earlier cinematic follow moved
  from F3 to **Ctrl+F3** to free native's command-console alias.
- A docked command view can click another sector and consume `VIEW_CLUSTER(177)`
  to browse the server-confirmed scene without moving the pilot.
- `HIGHLIGHT_CLUSTER(199)` marks each sector independently of local map selection,
  on both maps and the HUD. Activation plays native `receivedobjective`; false
  clears the mark, and reset clears all marks. Native Ctrl+right-click toggles and
  relays a leader's mark; other teammates can dismiss their local mark.
- `PROMOTE(189)` transfers the carrier's hull, mounted parts, cargo, telemetry and
  remaining gunners to the promoted player. The old pilot becomes a turret gunner.
  The promoted own simulation reseeds; `MyShipId` and the player's cookie stay
  intact. The HUD reads the new hull/parent state and the native pilot/demotion
  notices appear. Duplicate and unresolved promotions are safe no-ops.

All keyboard actions and mouse modifiers use the live action catalog, appear in
F1 help and can be rebound through F6. Modified game chords take precedence over
registered plain chords on the same key, preventing command toggles from also
opening Controls, cinematic, target, free or orbit cameras. The existing tolerant
`ActionMap.IsDown` API remains available; game dispatch uses `IsDownMostSpecific`.

## Native evidence and classification

Paths below are relative to `~/Development/Allegiance/src/` unless stated otherwise.

| Rule | Native citation | Classification / port |
|---|---|---|
| Enter/leave command mode; remember 3/4 vs TD | `WinTrek/WinTrek.cpp:9894-9953`, initial mode `:2707` | Keep; `Game1.Commanding.cs:16` |
| Native defaults/identities | `Igc/igc.h:261,291,352,435`; `~/Development/Artwork/defaultinputmap.mdl:36,64` | TK_ViewCommand and Track are unbound; F3 is TK_ConModeCommand's alias, Shift+F6 is TK_ToggleCommand. Catalog documents port defaults. |
| Axes, source/station focus, radius floor, target tracking, world bound | `WinTrek/WinTrek.cpp:73-78,7424-7544`; `zlib/orientation.h:83-110`, `orientation.cpp:492-510` | Keep native XY plane and **world-Z PostRoll**, not an orbit rig; `Rendering/CommandCamera.cs:8` |
| Pan, gutter, zoom, roll; suspend while selecting | `WinTrek/WinTrek.cpp:8469-8604` | Keep, routed through action map; `Game1.Commanding.cs:58` |
| Wheel camera zoom and 20% per notch | `WinTrek/console.cpp:1999-2019`, `WinTrek.cpp:10469-10488` | Keep; `CommandCamera.ZoomWheel` and flight input wheel gate |
| Grid/major lines, drop lines/feet, north | `WinTrek/cmdview.cpp:17-115,117-257,263-304`; `WinTrek.cpp:76,3099,7293-7294` | Keep native radius 7500, 40 subdivisions, colors and 15-unit feet; `Rendering/CommandGridRenderer.cs:8` |
| Pointer orders, rectangle/Shift recipient selection | `WinTrek/console.cpp:1857-1990,2081-2178,2250-2265`; rectangle threshold `:23-28` | Keep flow and 5px drag threshold. Modern shared screen-pick radius approximates projected-hull overlap. Existing P431/P469 order resolver/sender retained. |
| Resolve source ship and require visible same-sector pose | `WinTrek/WinTrek.cpp:7505-7513`, external target `:7099-7112` | Keep tracking rule; modern selected teammate chase/cockpit instead of cinematic-interest selection; `Game1.Commanding.cs:124` |
| Docked sector browsing | `WinTrek/sectormap.cpp:1539-1545`, `clintlib/appmsg.cpp:443-455` | Keep existing request/stream; new receive and render filter, no pilot relocation |
| Sector mark relay, clear, activation sound | `WinTrek/sectormap.cpp:1491-1510,1044,1192-1195`; `clintlib/appmsg.cpp:1749-1758`; Artwork `sounddef.mdl:300` | Keep; defensive unknown-sector guard replaces unchecked native pointer; `ClientWorld.Commanding.cs:25` |
| Promotion/board events and notices | `clintlib/appmsg.cpp:824-849`; `Igc/shipIGC.cpp:3509-3593` | Keep identity-preserving seat/hull/inventory swap; refresh derived HUD/simulation instead of native COM board callbacks; `ClientWorld.Commanding.cs:39` |

No career/lobby, wing-assignment, alliance or other wave-pack work was added.
Arbitrary empty-space waypoint construction and the native command-composition
console's verb UI remain the existing port's scope; object orders use P431/P469.

## Parity scores

Recomputed from actual branch HEAD before P640 and the resulting matrix. Formula:
`done / non-na`, weighted by the matrix's veteran-visibility weights. Partial rows
receive no done credit. The brief's 73.3%/68.5% baseline predates earlier committed
packs on this sequential branch and is not the P640 starting point.

| Area | Weighted before → after | Unweighted before → after |
|---|---:|---:|
| commands | 71.9% → 73.6% | 64.7% → 66.8% |
| fedmsg | 74.4% → 77.5% | 73.6% → 76.8% |
| screens | 86.8% → 86.8% | 79.2% → 79.2% |
| parts | 82.4% → 82.4% | 77.5% → 77.5% |
| commander | 96.3% → 100.0% | 94.7% → 100.0% |
| lobby | 76.5% → 76.5% | 75.0% → 75.0% |
| **total** | **76.2% → 78.2%** | **71.2% → 73.4%** |

Nine rows closed: TK_ViewCommand, TK_ConModeCommand, TK_ToggleCommand,
TK_TrackCommandView, 177 VIEW_CLUSTER, 189/190 PROMOTE, 199 HIGHLIGHT_CLUSTER,
and commander drone view/follow. Updated the already-done commander screen and
map-overlay citations. Three inherited P639 screen rows had unquoted commas;
quoted their existing descriptions so the CSV again has exactly six columns and
numeric weights. No status or weight changed for those three rows.

`fedmsg-wiring.csv` now names the real consumers/effects for 177/189/199 and the
complete promote request path. Dispatch coverage moved all three messages from
unconsumed to consumed: **86 consumed / 32 known unconsumed**, with the original
102-native-message completeness check intact.

## Verification and handoff

- `dotnet build AllegianceCore.slnx --no-restore -m:1`: **0 warnings / 0 errors**.
  Restored from the machine's cached packages with an empty local NuGet source
  first; no network restore was needed.
- **262 socket-free tests passed**: ClientCore **32/32**, MonoGame **230/230**.
  Includes **30 new P640 cases**, P625b guard/behavior tests, P625 keymap tests,
  dispatch completeness, camera/easing/orbit math, pointer picking, starmap,
  order resolver, radar projection/brackets, promote-seat mapping and dock/loadout/reset/export regression
  checks. Executed via xUnit's in-process discovery/execution API with a temporary
  runner under `/tmp/p640-runner` (VSTest's socket transport was not used).
- `git diff --check` and `bash -n scripts/proof-p640.sh`: passed.
- Extended `PromoteTurretKeyLiveTests` to wait for client boarding, assert both
  client seats/hulls/notices after real promotion, and verify highlight apply/clear
  through the real server relay. **Written, not run in this socket-restricted lane.**
- `scripts/proof-p640.sh [output-directory]` creates its own temporary detached
  worktree, overlays current working changes, builds a desktop head, then uses
  P572's local server and real presented-window capture. It requests a **15v15**
  arena with three-quarter/top-down command and wingman external/cockpit frames,
  plus an actual server-echoed highlighted sector with map/HUD. It rejects absent
  camera frames and missing highlight receipt. **Prepared and syntax-checked;
  no live window, PNGs or live-test pass is claimed.**

Run that proof script on the unrestricted workstation, inspect the frames, and
run the full suite (including the extended live test) before accepting visible
parity. Logs from this lane: `/tmp/p640_build_final.log`,
`/tmp/p640_client_tests.log`, `/tmp/p640_mono_tests.log`.

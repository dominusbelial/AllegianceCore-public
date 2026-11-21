# Browser hangar and full ship loadout in the game window (P1063 gap F2) — P1070

Started **2026-11-21 10:02**, finished **2026-11-21 11:52** (final gate at the end of this report), lane local time
(America/Bogota, UTC−5). Five-hour cap **2026-11-21 15:02**. No commits, pushes, installs or public-host access.

Scope: P1063 gap row **F2** ("Choose a ship or buy anything", task D) — the desktop's docked **hangar screen**
(`WinTrek/hangarscreen.cpp`) and the **full Loadout V2** screen (`WinTrek/load.cpp`) drawn and driven inside the
browser game window, plus the HTML launch controls moved behind `?dev=1`.

| Brief item | Result |
|---|---|
| 1 — Browser hangar in the game window | **Done**: the native hangar screen (`HangarScreenRenderer`) replaces the four-line docked text box, with the desktop's hover/click/keyboard handling, both HUD styles, and the shared launch-readiness blockers shown on screen |
| 2 — Full Loadout V2 (hulls, hardpoints, cargo, buy flow, ACK, board, money request) | **Done**: hull column, every hardpoint/equipment slot, the part picker, prices/money from the world, `SendBuyLoadout`/ACK, `REQUEST_MONEY`, disembark-then-act and the streamed hull preview |
| 3 — HTML launch controls behind `?dev=1`, `gameMenu` hook | **Done**: `#online-launch`/`#launch-status`/`#launch-reason` render only with the dev switch; `gameMenu.hangar()`, `gameMenu.pointer()` and `gameMenu.launch()` added; the two harnesses that clicked the button without `?dev=1` were updated |
| 4 — Docked / undocked transitions, refusal messages, session reset | **Done**: docking opens the hangar, launching leaves it, a refused buy shows the shop's own text, an ACK failure reverts the pending ship, and session attach/detach clears every latch |
| 5 — Proof | **Unit tests done** (19 + 2, 3× alone). **Harness written and syntax-checked; browser run and captures are for Claude — this lane has no Chromium** (same limitation as P1066/P1067) |

## Item 1 — the browser hangar

Two new files in `src/Allegiance.ClientCore.Game.Browser/` — a **browser copy** of the desktop glue, never a move of
it:

- `HangarLoadout.cs` (582 lines) — `BrowserHangarModel`, the state machine. It is a line-for-line mirror of
  `src/Allegiance.ClientCore.Game/Game1.Hangar.cs` and `Game1.LoadoutV2.cs`/`Game1.LoadoutV2Input.cs`; each method
  cites the desktop lines it copies. Pure: no `GraphicsDevice`, no devices, so the whole screen is unit-tested
  headlessly.
- `Game1.Hangar.cs` (453 lines) — the drawing and device plumbing: the hangar screen, its hover strip and status band,
  the loadout frame, the hull preview and the canvas pointer.

The docked branch of `Game1.Flight.cs:396-403` is the only change to that file's ship pass: the four-line
`DockedScreenRenderer` text box became one call, `DrawBrowserHangar()`.

| Piece | Browser | Desktop it mirrors |
|---|---|---|
| Docked shop model | `HangarLoadout.cs:216-243` `EnterDock` | `Game1.Panes.cs:150-206` (fresh `ShopModel` from the docked ship, `ShopCatalog.FilteredFor` over the station's `TtbmLocal` + the side's live developments, the hardware ledger) |
| Per-frame offers/money | `HangarLoadout.cs:244-267` `RefreshOffers` | `Game1.Panes.cs:443-458` `RefreshDockedOffers` (rebuild only when the tech set or station type moved) |
| ACK reconciliation | `HangarLoadout.cs:268-289` `SyncShopWithServer` | `Game1.Hangar.cs:141-160` (`LoadoutBuyFlow.Sync`, `SyncShopWithServer`) |
| Lifepod restore | `HangarLoadout.cs:290-302` | `Game1.Panes.cs:2342-2350` (P996, `clintlib.h:1876-1886`) |
| Hangar screen frame | `Game1.Hangar.cs:217-288` | `Game1.Hangar.cs:216-268` (`UpdateHangarControls`, the FrameImage launch ramp, the click edge) |
| Hangar artwork | `Game1.Hangar.cs:289-306` | `Game1.Hangar.cs:222-231` + `ClassicScreenSkinRenderer.DrawHangar` (`Rendering/Skins/ClassicScreenSkinRenderer.cs:80-84`) |
| Missing-artwork fallback | `Game1.Hangar.cs:307-327` | `Game1.Hangar.cs:288-320` `DrawHangarControlsTextFallback` |
| Status / quit-confirm band | `Game1.Hangar.cs:328-355` | `Game1.Hangar.cs:203-213, 315-319` |
| Verb dispatch | `HangarLoadout.cs:375-424` `Activate` | `Game1.Hangar.cs:167-201` `ActivateHangar` |

Details kept exactly as the desktop has them:

- **Rectangles.** The hangar draws `Rendering/HangarScreenRenderer` on the same `Rendering/HangarScreen` rectangles
  the pointer hit test uses (`HangarScreen.Rect`), laid out in native 800×600 and scaled uniformly and centred
  (`HangarScreen.ScreenTransform`, `WinTrek/WinTrek.cpp:3656-3681`). The screenshot-listed native verbs are all there
  (Launch, Command, Loadout, Invest, Help, Teleport, Team, Exit) plus the port-only Buy/Default cells; the Invest pane
  hides when the mission forbids developments (`hangarscreen.cpp:129-130`).
- **Both HUD styles.** Classic and Modern take the identical pair of renderers `ClassicScreenSkinRenderer` forwards
  to, and the loadout's layout scale is the shared `ModernHudScale.Core(1f, _hudSettings.Size, height)`, so both
  styles get their own typography at 1920×1080 and 800×480 (`Game1.Hangar.cs:381-385`).
- **Mouse.** The DOM feeds the canvas pointer (`wwwroot/flight-input.js`, `HangarPointer`), converted into the same
  drawing-buffer space `menuViewport()` and `HangarScreen.Rect` use. Hover selects the native face row; a press edge
  dispatches `HangarControls.Hit` → `Activate`. Pointer lock, fullscreen and the audio gesture stay flight-only.
- **Keyboard.** `flight-input.js` now forwards the key stream while the panel is in the Hangar state (it used to be
  Flight-only), so the shared action map's `MenuConfirm` (Enter) / `MenuBack` (B) / `MainMenu` (Escape) drive the quit
  confirmation, the loadout's Back and the picker close, exactly the actions the desktop reads. Escape is no longer
  swallowed by the "release flight input" path while docked.
- **Launch readiness.** `BrowserHangarModel.Launch` (`HangarLoadout.cs:425-463`) gates on the shared
  `HangarLaunch.Of(session)` and words a blocker with `HangarLaunchText.For` (new,
  `src/Allegiance.ClientCore.Game.Browser/HangarLaunchText.cs`), which the page's HTML control now delegates to as
  well (`Menu.Online.cs:314`, `LaunchBlockerText`). So the canvas and the HTML control cannot disagree: the three
  texts are the P1058 ones, unchanged. The band under the screen shows them (`DrawHangarStatus`), together with the
  busy line while a launch is on its way.
- **Skipped on purpose: local training.** The desktop's `CompleteLocalTrainingPurchase` / `ApplyLocalTrainingLaunch`
  branch (`Game1.Hangar.cs:162-176`) exists only for the embedded skirmish host (`_embeddedHost`). A WASM head has no
  local server, so the browser always takes the else branch — one ordinary `FM_C_BUY_LOADOUT` on the wire. Stated in
  the type documentation and not implemented anywhere.
- **Verbs whose screens are later tasks** (Command, Team, Invest, Teleport/boarding, Help) are still drawn, exactly as
  native draws them, and say so in the status band instead of doing nothing silently
  (`HangarLoadout.cs:405-420`). Team/side picker, mission briefing, investment and command screens are out of scope.

## Item 2 — full Loadout V2

`Game1.Hangar.cs:356-391` (`UpdateLoadoutScreen`) is the browser's `Game1.LoadoutV2.cs:26-143`:

- **Model** — `HangarLoadout.cs:303-339` `RebuildViewModel` builds the shared `Rendering/LoadoutV2Model` over the shop
  and the catalogue the server loaded (`ShopCatalog.TryLoad`, filtered by `ShopCatalog.FilteredFor`), with the side's
  reachable tech (`BucketCatalog.ReachableTechs`), the station's `TtbmLocal` and the live turret occupants
  (`HudInventoryPaneBuilder.SourceShipOccupants`). The hull column is `ShopModel.BuyableHulls` — the server's offer
  list, never a browser list; a hull behind an unreached development is not offered at all (unit-tested).
- **Presentation** — `HangarLoadout.cs:344-370` `BuildFrame` builds the shared `LoadoutV2Presentation` (title, money,
  hull rows with prices and lock reasons, the slot rows with the mounted part and its gunner, the inline offers with
  signed stat deltas, the stat sheet from `ShipStatSheet`, the price/refund/net/shortfall box, the Buy and Launch
  buttons and the status line). Drawn by `Rendering/LoadoutV2Renderer` (`Game1.Hangar.cs:388`).
- **Input** — the shared `Rendering/LoadoutV2Input` router (`Game1.Hangar.cs:359-368`): the same
  `LoadoutV2Input.Keys` merge (key table + pad), the same pointer struct, the same live gate (`!_launchSent`, no
  popup/quit confirm), the real layout's hit targets, the wheel for all three scroll offsets. The router is suspended
  while its screen is not shown (`Game1.Hangar.cs:248-257`), so a held key never becomes a fresh press.
- **Buy / Launch / Back / Undock** — `HangarLoadout.cs:464-522`. Buy is `LoadoutBuyFlow.Buy` with native's disabled
  gate on the V2 screen: `REQUEST_MONEY` for the shortfall (`WinTrek/load.cpp:1511-1531`) or `BUY_LOADOUT` without
  launch; Launch is `LoadoutBuyFlow.Launch` → `BUY_LOADOUT(fLaunch=true)` (`hangarscreen.cpp:145-160`); Undock sends
  `FM_C_DOCKED`; Back returns to the hangar. Cargo: `ShopModel` is built with `own.Cargo` and the catalogue's
  launchers exactly as the desktop builds it (`Game1.Panes.cs:177-190`), so expendable/cargo pricing and refunds match.
- **Prices and money from the world** — `world.MyMoney` every docked frame (`RefreshMoney`), plus the richest
  teammate from `world.WalletByShipId` for native's "can anyone on the team afford it" (`Game1.Hangar.cs:264-285`).
- **Wire** — `HangarLoadout.cs:78-100` are the ports: the browser partial binds them straight to the shared
  `ConnectionSession` methods the desktop calls (`SendBuyLoadout`, `SendMoneyRequest`, `SendBoardShip`, `SendDocked`,
  `SendQuitMission`), so there is one implementation and one encoder.
- **Board ship** — where the desktop offers it: a berthed pilot disembarks first and repeats the action on the
  authoritative parent-clear (`HangarLoadout.cs:524-548` ← `Game1.Hangar.cs:68-84`, `clintlib/appmsg.cpp:1929-1938`),
  and a `BOARD_NACK` cancels the wait (`clintlib/appmsg.cpp:1899-1911`). The teleport/board pane itself is a later
  task.
- **Hull art preview** — `Game1.Hangar.cs:392-452` `DrawLoadoutPreview`: the shared pure `DockedHullOrbit`
  (1 rad/s about Y + π/8 tilt, `WinTrek/load.cpp:877-887`) and `DockedHullPreviewFraming` (50° FOV, eye at 2.4× the
  rendered radius, native clip planes, `load.cpp:860-934`), drawing the hull's real native mesh. A mesh that is not in
  the join pack is requested through **the existing verified P1065 streamer**
  (`RequestBattlePack?.Invoke("hull-" + hullId)`, `Assets/BattlePackStreamer.cs`, per-hull closures from
  `tools/p1065-battle-packs.py`) and shows the flight pass's own lit-box stand-in until the verified bytes land
  (`BattleShipScene.MeshBytesReady`). **The join budget does not grow**: no manifest, packer or publish-script change
  is in this task, and `join-manifest.json` is untouched. The metrics report `previewStreamed` per capture so the
  harness can tell which state a shot caught.

## Item 3 — the HTML launch controls behind `?dev=1`

`src/Allegiance.ClientCore.Browser/Pages/BrowserHangarChrome.cs` holds the one rule:

```csharp
public static bool HtmlLaunchControls(bool dev) => dev;   // plus CanvasNotice for a player
```

`Menu.razor:89-103` renders `#online-launch` and `#launch-reason` only inside
`@if (BrowserHangarChrome.HtmlLaunchControls(_dev))`; the busy `#launch-status` is skipped in the Hangar state without
the switch (`Menu.razor:146`). Everything else on the panel is untouched (server list, password, sides, briefing,
flight toolbar, disconnect) — those are later tasks. A player sees `#hangar-canvas-note` instead, one line pointing at
the game window.

**Harness audit** — every `tools/*.cjs` that clicks `#online-launch`:

| Harness | Opened with | Result |
|---|---|---|
| `p1048b-browser-join.cjs` | `?dev=1` | unaffected |
| `p1051-browser-flight.cjs` | `?dev=1` | unaffected |
| `p1053-browser-polish.cjs` | `?dev=1` | unaffected |
| `p1054-browser-sectors.cjs` | `?dev=1` | unaffected |
| `p1058-browser-launch.cjs` | `?dev=1` | unaffected |
| `p1065-browser-battle.cjs` | `?dev=1` | unaffected |
| `p1066-browser-scene.cjs` | `?dev=1` | unaffected |
| `p1067-browser-watch.cjs` | `?dev=1` | unaffected |
| `p1059-browser-devcases.cjs` | **plain page on purpose** (the switch is its subject) | updated: clicks the HTML button when it exists, otherwise `gameMenu.launch()` — the game window's own Launch verb |
| `p1062-browser-signin.cjs` | **plain page** | updated: asserts the Hangar state has no `#online-launch`, then launches with `gameMenu.launch()` |

**The JS test hook** (`wwwroot/menu.js`, `Menu.razor.cs`):

| Hook | Answer |
|---|---|
| `gameMenu.hangar()` | visible, screen (`Hangar`/`Loadout`), `loadoutOpen`, hover, board state, status, last action, launch latches, the live launch blocker + its text, money, hull/current hull/name, parts, offered hulls/parts, cost/shortfall, buy/launch enabled, shop state and refusal, slots, open-slot offers, `previewStreamed`, **the drawn hangar verb rectangles** and **the loadout's drawn element rectangles** |
| `gameMenu.pointer(x, y, down, wheel)` | feeds the canvas pointer (same path `flight-input.js` uses) |
| `gameMenu.launch()` | clicks the HTML button when it exists, else calls the game window's Launch verb — `'html'` / `'window'` |

Nothing else is hidden: `#online-panel`, the server list, password, sides, briefing, the whole flight toolbar and
`#online-close` keep rendering in both switch states (the new harness asserts four of them on the no-dev run).

## Item 4 — docked / undocked transitions

| State | Rule | Where |
|---|---|---|
| Dock | `EnterDock` builds a fresh shop from the ship the server says we are in and opens the **hangar** (`_hangarOnly = true`, `Game1.Panes.cs:127-135`) | `HangarLoadout.cs:216-243` |
| Launch | Once the ship is no longer docked the model resets; the page flips the panel to Flight when `StationId < 0` | `HangarLoadout.cs:189-215`, `Menu.Online.cs:395-410` |
| Buy refused for money | `REQUEST_MONEY` + native's "can't afford / asked your team" state text in the status band | `HangarLoadout.cs:483-489` |
| Buy refused for tech | never offered: the shop's catalogue is the server's tech-filtered one | `HangarLoadout.cs:216-267` |
| Buy refused by the server | the ACK's incomplete `fBoughtEverything` reverts the pending ship and prints `ShopModel.PartsUnavailableText` (`load.cpp:2195-2205`) | `HangarLoadout.cs:268-289` |
| Launch the server did not perform | an ACK without `fLaunch` releases the latch (`appmsg.cpp:3180-3181`) | `HangarLoadout.cs:279-285` |
| Death / eject | unchanged shared paths (`Game1.Eject.cs`, `Game1.Prediction.cs`): the pilot respawns docked, so the hangar simply reopens; a berthed lifepod restores the last real loadout instead of offering the pod | `HangarLoadout.cs:290-302` |
| Ghost (out of lives) | native lands a ghost in the command view, never the hangar (`Game1.Panes.cs:106-126`); the browser has no command view yet, so the canvas says so and no verb is live | `Game1.Hangar.cs:221-231` |
| Reconnect | `HangarLaunch.Of` reports `Reconnecting`; the launch is blocked with the HTML control's own text and the shop keeps its state (the resume keeps the same world) | `HangarLoadout.cs:425-463` |
| Session reset | `AttachOnlineSession(null)` or a new session clears the shop, the view model, the frames and every latch | `Game1.Hangar.cs:191-195`, `HangarLoadout.cs:166-188` |

## Item 5 — proof

### Unit tests

`tests/Allegiance.ClientCore.Game.Browser.Tests/P1070HangarTests.cs` — **19 cases**, run ×3 alone (see the gate
table). They drive the real model over a real `ClientWorld` built from the wire (`SET_CLUSTER`, `EXPORT_SHIP`,
`PLAYERINFO`, `SHIP_STATUS`, `EXPORT_STATION`, `SET_MONEY`, `BUY_LOADOUT_ACK`, `BOARD_NACK`, `ENTER_GAME`) with the
server's catalogue (`ShopCatalog`), and a recording wire that mirrors `ConnectionSession.SendBuyLoadout`'s own ledger
bookkeeping.

| Test | What it pins |
|---|---|
| `Docking_opens_the_hangar_with_the_servers_offer_list` | the hangar opens on the ship's own hull; the hull behind an unreached development is **not** in the offer list; money is the world's |
| `The_loadout_pane_opens_the_loadout_and_back_returns_to_the_hangar` | the Loadout pane opens the V2 screen, the presentation title is the shared one, Back returns |
| `Choosing_a_hull_and_a_part_changes_only_the_pending_loadout` | hull + part change the pending ship, never the ship itself |
| `Away_hangar_verbs_say_they_are_not_ported_instead_of_doing_nothing` | Command/Team/Invest/Teleport/Help report instead of silently doing nothing |
| `Buy_sends_the_desktop_buy_loadout_request_without_launch` | **the request bytes equal the desktop's** for the same choice (`BUY_LOADOUT(fLaunch=false)` + parts) |
| `Launch_sends_the_desktop_buy_loadout_request_with_launch` | same, with `fLaunch=true`; the second click is dropped |
| `A_short_wallet_asks_the_team_for_the_shortfall_instead_of_buying` | `REQUEST_MONEY` for the shortfall, no buy, native's wording |
| `The_servers_acknowledgement_replaces_the_ship_and_clears_the_pending_change` | ACK success: the shop follows the real ship, the pending change is gone |
| `A_refused_buy_shows_the_desktops_message` | ACK failure: `PartsUnavailableText` and the pending ship reverted |
| `An_acknowledgement_without_the_launch_releases_the_launch_latch` | `appmsg.cpp:3180-3181` |
| `Launch_blockers_are_the_html_controls_own_words_in_the_game_window` | the three blocker texts, character for character, and no send |
| `Before_the_mission_starts_the_launch_defers_to_the_pages_ready_pipeline` | pre-`STAGE_STARTED` the launch is the page's ready→started→DOCKED pipeline; one-shot request |
| `The_undock_button_always_leaves_without_buying` | `FM_C_DOCKED`, latched |
| `A_berthed_gunner_disembarks_first_and_repeats_the_action_after_the_ack` | disembark then buy, on the parent-clear ACK |
| `A_board_refusal_cancels_the_waiting_action` | `BOARD_NACK` cancels |
| `A_confirmed_quit_sends_quit_mission` | cancel vs confirm |
| `Undocking_resets_the_hangar_and_a_new_dock_reopens_it` | launch leaves the hangar; the next dock opens it on the real hull |
| `Reset_clears_the_shop_and_every_latch` | session reset clears everything |
| `A_lifepod_dock_restores_the_last_real_loadout` | P996 restore, never the pod |

`tests/Allegiance.ClientCore.Browser.Tests/P1070DevGateTests.cs` — **2 cases**: the dev-flag visibility of the HTML
launch controls and the player-facing notice.

### Browser run — this lane has no Chromium

Checked first: Node is present, but Playwright and `@sparticuz/chromium` are not installed anywhere on this container
and no system Chromium can start (the bundled Hermes build misses `libglib-2.0.so.0`, libnss3, libatk, libcups,
libxkbcommon, libcairo, libpango, libasound; the apt cache holds none of them and this lane forbids package installs).
This is the same explicit unavailable-browser path P1065/P1066/P1067 recorded. No browser metric, frame time or
capture is claimed as measured here.

`tools/p1070-browser-hangar.cjs` (274 lines) is written and **syntax-checked** (`node --check`). What it does, per
`p1051-browser-host --bots 1`:

1. register a passkey identity, join with the typed game password, pick a side, wait for the Hangar panel;
2. assert the canvas hangar is up (`gameMenu.hangar().visible`, screen `Hangar`, `offeredHulls > 0`, the Launch and
   Loadout drawn rectangles exist);
3. click the **Loadout** verb at its drawn rectangle (canvas pointer), assert the loadout opens and lists slots;
4. click a **different hull row**, then a **slot**, then the **last offer row** (a real part), asserting the hull
   changed and a part was mounted in the pending loadout;
5. click the drawn **Buy** button and wait for the server's acknowledgement to move the wallet;
6. for each of Classic/Modern × 1920×1080/800×480: capture the loadout, click Back, capture the hangar (8 JPEG q80
   captures, ≤ 2 MiB asserted);
7. click the drawn **Launch** pane, wait for Flight, assert `flying`, assert the hangar is gone, fly, `#flight-dock`,
   wait for the Hangar panel and assert the hangar is drawn again;
8. a second context loads the page **without** `?dev=1`: asserts `gameMenu.metrics().dev === false` and that
   `#online-launch`, `#launch-status`, `#launch-reason` and `#online-pose` are all absent, that the canvas hangar is
   still there, that `gameMenu.launch()` returns `'window'` and the pilot really launches, and that
   `#flight-contact`, `#flight-watch`, `#flight-dock` and `#online-close` are still present;
9. writes `browser-hangar.json` (cases, per-capture rows, `captureBytes`, the no-dev results) or `failure.json`.

### Exact workstation commands — for Claude

```bash
set -euo pipefail
export DOTNET="$HOME/.dotnet/dotnet"
export NUGET_HTTP_CACHE_PATH="$PWD/.scratch/p1070/nuget-http"
export ALLEGIANCE_ARTWORK_PATH=/srv/corpus/Artwork
export BROWSER_CLIENT_VERSION=2.0.0
NPM_DIR=.scratch/p1051/npm                       # existing playwright + @sparticuz/chromium
test -d "$NPM_DIR/node_modules/playwright"
test -d "$NPM_DIR/node_modules/@sparticuz/chromium"

for project in tools/p1051-browser-host src/Allegiance.ClientCore.MonoGame \
  src/Allegiance.ClientCore.Browser; do
  "$DOTNET" build "$project" --disable-build-servers -nodeReuse:false -p:UseSharedCompilation=false \
    -p:RestoreSources="$HOME/.nuget/packages" -p:NuGetAudit=false
done

mkdir -p .scratch/p1070/traces
python3 - <<'PY'
import gzip, json
from pathlib import Path
traces = json.loads(gzip.decompress(Path('docs/reports/BROWSER_FLIGHT_P1051_assets/desktop-opens.json.gz').read_bytes()))
for name, trace in traces.items():
    target = Path('.scratch/p1070/traces') / name
    target.parent.mkdir(parents=True, exist_ok=True)
    target.write_text(json.dumps(trace))
PY
scripts/publish-browser-flight.sh .scratch/p1070/browser-generation \
  .scratch/p1070/traces/opens/Classic.json .scratch/p1070/traces/opens/Modern.json \
  .scratch/p1070/traces/flight-opens4/Classic.json .scratch/p1070/traces/flight-opens4/Modern.json \
  .scratch/p1070/traces/flight-miners0/Classic.json .scratch/p1070/traces/flight-miners1/Modern.json

state_dir=$(mktemp -d "$PWD/.scratch/p1070/browser-host-1.XXXXXX")
output_dir="$PWD/docs/reports/BROWSER_HANGAR_LOADOUT_P1070_assets/browser"
"$DOTNET" tools/p1051-browser-host/bin/Debug/net10.0/p1051-browser-host.dll \
  "$ALLEGIANCE_ARTWORK_PATH" .scratch/p1070/browser-generation "$state_dir" \
  --bots 1 --seconds 1200 > "$state_dir/host.log" 2>&1 &
host_pid=$!
cleanup() { touch "$state_dir/stop"; wait "$host_pid" || true; }
trap cleanup EXIT INT TERM
result=0
node tools/p1070-browser-hangar.cjs "$NPM_DIR" "$state_dir" "$output_dir" \
  .scratch/p1070/browser-generation || result=$?
cleanup
trap - EXIT INT TERM
exit "$result"
```

Capture slots the harness writes (values recorded at capture time; **no image exists on this lane**):

| File | Style / size | Screen |
|---|---|---|
| `hangar-loadout-classic-1920x1080.jpg` | Classic 1920×1080 | Loadout V2 (hull column, preview, slots, stats, price, Buy/Launch) |
| `hangar-classic-1920x1080.jpg` | Classic 1920×1080 | the native hangar screen |
| `hangar-loadout-modern-1920x1080.jpg` | Modern 1920×1080 | Loadout V2 |
| `hangar-modern-1920x1080.jpg` | Modern 1920×1080 | the native hangar screen |
| `hangar-loadout-classic-800x480.jpg` | Classic 800×480 | Loadout V2 |
| `hangar-classic-800x480.jpg` | Classic 800×480 | the native hangar screen |
| `hangar-loadout-modern-800x480.jpg` | Modern 800×480 | Loadout V2 |
| `hangar-modern-800x480.jpg` | Modern 800×480 | the native hangar screen |

`docs/reports/BROWSER_HANGAR_LOADOUT_P1070_assets/browser/` intentionally holds no images.

### What Claude must add

Open each capture and describe only what is visible (which hangar panes are drawn, the hover strip, the loadout's
hull rows/slots/stats, whether the preview shows the hull mesh or the stand-in box, the HUD style's typography at both
sizes), then paste the `cases`, the per-capture rows and the `noDev` block from `browser-hangar.json`. This lane
cannot view images.

## Desktop unchanged

`git diff --stat -- src/Allegiance.ClientCore.Game src/Allegiance.ClientCore src/Allegiance.Server src/Allegiance.Core`
is **empty**: no desktop head, shared core or server file was touched, so no pure move and no zero-delta capture
comparison was needed (the P1066 desktop-pixel method). The browser-only additions are new files in
`src/Allegiance.ClientCore.Game.Browser/` and `src/Allegiance.ClientCore.Browser/Pages/`, plus the two page files and
the JS the brief allowed (`Menu.Online.cs` — the launch controls, their dev gate, the launch-request drain and the
Hangar→Flight state flip; `Menu.razor` — the launch-control gate; `Menu.razor.cs` — the JS hooks;
`wwwroot/menu.js`, `wwwroot/flight-input.js` — the hooks and the docked input forwarding).

Parallel-task safety: P1068's files (`Pages/Menu.Online.cs` guest label, `BrowserJoinPolicy.cs`, `BrowserServer.cs`,
`src/Allegiance.Lobby/`) — my edits in `Menu.Online.cs` are confined to the launch path and the state flip; P1069's
files (`Game1.Flight.cs` ship pass, `Game1.Scene.cs`) — my only change in `Game1.Flight.cs` is the docked branch and
the one `ResetBrowserHangar()` call in `AttachOnlineSession`.

## Native intent / Modern approach

**Native intent.** A docked player lives on the hangar screen (`WinTrek/WinTrek.cpp:6565,9381` →
`CreateHangarScreen` at `:6706`), which is a screen of artwork panes, not a menu of buttons: Launch
(`hangarscreen.cpp:145-160` → `BuyLoadout(ship, true)`), Loadout (`:186-190` → `vmLoadout`), and the rest. The loadout
screen (`WinTrek/load.cpp`) is where the hull (`:1568-1637`), every hardpoint and equipment slot (`:1680-1751`) and the
ship's real model (`:860-934`) live, where affordability decides the Buy/Launch state (`:2139-2190`), and where Buy
becomes `REQUEST_MONEY` or `BUY_LOADOUT` (`:1503-1531`) and `BUY_LOADOUT_ACK` applies or reverts what was bought
(`:2193-2210`, `clintlib/appmsg.cpp:3176-3187`). The messages are native's own: `C_BUY_LOADOUT`
(`Inc/Messages.h:65`) / `S_BUY_LOADOUT_ACK` (`:70`), `C_BOARD_SHIP` (`:686`), `CS_REQUEST_MONEY` (`:940`),
`CS_QUIT_MISSION` (`:812`).

**Modern approach.** The browser stops being a web page with a launch button: the game window *is* the hangar and the
loadout. It reuses the renderers, models and routers the desktop already ships (they were already compiled into the
browser assembly) and adds only a browser copy of the desktop's glue, in new files, with every rule cited back to the
line it mirrors. The offered hulls and parts are always the server's tech-filtered catalogue; the buy/launch bytes are
produced by the same shared encoder from the same shared flow, so behaviour cannot drift from the desktop. A hull whose
art is not in the join pack is streamed through the existing verified P1065 packs and shown as the flight pass's
stand-in until it arrives, so the join budget does not grow. The HTML launch controls that used to be the only way to
leave the hangar become a `?dev=1` proof surface, the same switch the flight toolbar's proof controls already use, and
two JS hooks let a harness see and drive the hangar without them.

## Gate

All checks on the final source.

| Check | Result |
|---|---|
| Solution build (`dotnet build AllegianceCore.slnx`, disabled build servers/node reuse/shared compilation) | **0 warnings / 0 errors** |
| `tools/build-tools.sh` | **all 151 tool projects built**, 0 warnings / 0 errors |
| New tests, alone ×3 — `P1070HangarTests` | **19/0/0 ×3** (82 ms each) |
| New tests, alone ×3 — `P1070DevGateTests` | **2/0/0 ×3** (9 ms each) |
| `tests/Allegiance.ClientCore.Game.Browser.Tests` full | **186 passed / 0 failed** |
| `tests/Allegiance.ClientCore.Browser.Tests` full | **51 passed / 0 failed** |
| Desktop MonoGame tests (`--filter Tier=Fast`) | **6001 passed / 0 failed / 4 artwork skips** |
| Server buy/loadout/dock/money tests (`Allegiance.Core.Tests`, `Tier=Fast` + loadout/hangar/dock/money/purchase) | **300 passed / 0 failed / 2 skips** |
| Lobby suite (`Tier=Fast`) | **326 passed / 0 failed** |
| Desktop zero-delta | `git diff --stat -- src/Allegiance.ClientCore.Game src/Allegiance.ClientCore src/Allegiance.Server src/Allegiance.Core` **empty** |
| Glossary / docs | new **Browser hangar and ship loadout (P1070)** entry in `GLOSSARY.md`; `docs/BROWSER_HOSTING.md`'s P1059 developer-switch section now records that the docked HTML launch controls are behind the same switch and names the three `gameMenu` hooks |
| Glossary link check | `tools/check-glossary-links.py` **OK (1,075 paths)** |
| Parity CSV | `scripts/parity-scorecard.py docs/parity/p1070-browser-hangar-loadout.csv` **validated 8 rows** (100 % of 18 weight) |
| Harness | `tools/p1070-browser-hangar.cjs` written, `node --check` clean; `p1059`/`p1062` harnesses updated and syntax-checked |
| Final Fast tier (`tools/run-tests.sh --tier fast`, run last on the final source) | **15,893 tests, 564.2 s**, all 9 infrastructure checks passed, calibrated limit 11.528 s; every project green (Browser 51, Browser game 186, Lobby 326, Update 55, MonoGame 6,001 + 4 artwork skips, ClientCore 2,473 + 1, Core 6,773 + 1); **red for two existing live/transport flakes plus two over-limit rows**, see below |
| The two flaky tests, 3 fresh isolated processes each | `WarpTransitServerTests.ShipInsideWarp_GetsTransited_AndPeerSeesSectorChange` **1/0/0 ×3** (305/304/305 ms); `DeadLatchRespawnLiveTests.Remote_client_sees_victim_again_after_death_and_respawn` **1/0/0 ×3** (1 s each) — both already ledgered chronic families |
| The two over-limit rows | `SC2NaniteSupportTests` 12.3 s, `P814GunnerTests.P814b_TurretCarrier...` 13.0 s — both **passed**, both chronic load-sensitive families already in the ledger with 3× isolated evidence |
| Earlier Fast sweep on the same source | **15,893 tests, 562.1 s**, one failure `P855TrapHackTests.Out_of_range_wing_id_...` (**1/0/0 ×3** alone at 437/435/422 ms, already ledgered), three passing over-limit rows |
| `docs/TEST_FLAKES.md` | new **P1070** entry (both sweeps, the six isolation results and the chronic families) |
| Browser run, captures | **not run — no Chromium on this lane; for Claude** |

No process is left running: no server, host, driver or browser was started on this lane (only `dotnet build`/`test`
and `node --check`, all exited). `.scratch/p1070/` holds the NuGet HTTP cache only.

**Unfinished:** the browser run and the eight captures — this lane has no Chromium. Everything needed is in place:
`tools/p1070-browser-hangar.cjs`, the exact commands above, and `p1051-browser-host --bots 1`. Claude runs it, opens
the captures and appends the metrics and visual descriptions.

## Workstation browser results — Claude (2026-11-21, post-merge with P1068/P1069)

`tools/p1070-browser-hangar.cjs` against `p1051-browser-host --bots 1`: **PASS** — hangar in the
canvas, loadout opened by its drawn pane, a hull and a part chosen by drawn rows, Buy
acknowledged (state `Current`, hull 17 Adv Stl Fighter + 7 parts; the proof host's test unlock
refills the wallet to $500,000, so money is not evidence here), eight captures (728,543 bytes),
launch from the canvas, fly, re-dock, and a plain visit with no `?dev=1` controls.

Review found and fixed, in this order:

1. **Small-window crash (real bug).** The shared `LoadoutV2Layout.Compute` supports 640×480 and
   up; an 800×480 page leaves an 800×369 canvas, `Compute` threw `ArgumentOutOfRangeException`
   inside `Draw`, and the WASM loop died — the session dropped (`LinkDead`). The browser now lays
   out and draws the loadout at the smallest same-aspect size that meets the minimum into an
   offscreen target and scales it onto the canvas; the pointer is scaled the same way, so
   draw == hit holds. Desktop code is unchanged.
2. **Stale shop after launch (real bug).** `BrowserHangarModel.Update` resets when undocked, but
   it only ran while docked, so a launched pilot kept the old shop and the next dock skipped
   `EnterDock`. The undocked flight path now resets it (desktop builds a fresh shop on every dock,
   `Game1.Panes.cs:127-206`).
3. JS hook: verb bounds were XNA `Rectangle`s whose fields JSON interop skips (null coordinates);
   now plain `{x,y,width,height}`. `slots`/`hullName`/`offers` now read the drawn view model,
   which resolves a docked hull the station does not offer from the full core (P1014 design;
   native `clintlib.h:1499-1505` keeps the current hull) — the shop's offer list does not.
   New `chosen` field (part@mount) so replacing a part in an occupied slot is visible.
4. Harness: Chromium `--single-process` shared localStorage across contexts, so the "plain
   visit" inherited the remembered dev switch; the flag is dropped (as P1068 did) and the plain
   context gets the signed-in storage minus `allegiance.dev`.

Captures opened and checked: the Classic/Modern hangar shows the native hangar art with the
Exit pane and the port's Buy/Default cells; the Launch pane is a text fallback because the
`animlaunch` frame animation is missing from the browser pack (console warning) — follow-up.
The loadout shows the hull column (198 rows: the proof host's all-tech unlock), the streamed
hull preview, seven equipment slots with parts, the stat sheet and Default/Revert/Confirm/
Launch. At 800×480 the loadout is the scaled layout, readable; the hangar's Buy/Def cells sit
at the bottom edge partly under the identity strip — follow-up.

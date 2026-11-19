# Browser developer/proof controls — P1059

Status: **items 0, 1 and 3 complete; item 2 complete with the harness runs below.** Started
2026-11-19 07:35 America/Bogota (lane local time), deadline 11:35. No commits, pushes, deployment
or non-loopback *proof* traffic: every page/harness request was `localhost`/`127.0.0.1` (the
harnesses assert it, and the proof browser runs behind `--proxy-server=http://127.0.0.1:9`). Changes
are left in the working tree.

| Item | Status |
|---|---|
| 0 — Classify every control | **Complete** — table below; native cites checked against `/srv/corpus/Allegiance/src`. Three controls (plus the waypoint form's five elements) are developer/proof; everything else maps to a native player action or is required to play in a browser. Three questions for Domingo. |
| 1 — One dev switch, off by default, hidden not deleted | **Complete** — `?dev=1` / `localStorage['allegiance.dev']`, `?dev=0` turns it off; the three dev elements are not rendered when off; no game method removed; no rule/wire/server change. |
| 2 — Harnesses keep working + switch-off check | **Complete** — new `tools/p1059-browser-devcases.cjs` (4 combinations, off and on, plus stickiness) and all five existing harnesses re-run green (P1051 flight 4/4, security 11/11, P1053 polish 5 profiles, P1054 sectors 4/4, P1058 launch 9 cases). |
| 3 — Docs, glossary, parity row, screenshots | **Complete** — `docs/BROWSER_HOSTING.md`, `GLOSSARY.md`, `docs/parity/p1059-browser-dev-controls.csv`; eight unedited PNGs (412,157 bytes) opened and described below. |

## Item 0 — classification

Rule used: a control stays player-visible only if it maps to something a native player has (native
`file:line`) or is required to play in a browser at all (join, side, launch, disconnect, style,
sound). Everything else is developer/proof.

### Menu toolbar (`#menu-controls`, `Menu.razor:8-15`)

| Id / element | Label | What it does | Class | Native cite / reason |
|---|---|---|---|---|
| (`Menu.razor:10-11`) | Classic / Modern | Persists `HudStyle` and re-selects the shared HUD renderer | player | Required to play in the browser (style is a browser `HudStyle` choice); no game rule |
| `#online-open` | Play on loopback | Opens the local lobby listing (`OpenOnlineAsync`) | player | Required to play in the browser; native logon/list path (`WinTrek/logon.cpp`) |
| (`Menu.razor:13`) | Enable menu sound / Menu sound on | Activates the browser AudioContext for the shared menu audio | player | Required to play in the browser; native sound engine (`soundengine/`) |
| (`Menu.razor:14`) | Download screenshot | Requests the next RAF frame as a PNG download | player | `TK_ScrnShot = 218` `Igc/igc.h:445`, handled at `WinTrek/WinTrek.cpp:9284` |

### Online panel (non-flight states)

| Id / element | Label | What it does | Class | Native cite / reason |
|---|---|---|---|---|
| `#online-refresh` | Refresh identity and servers | Re-reads `/api/identity/me` and `/api/servers` | player | Required to play in the browser (native logon retry, `WinTrek/logon.cpp`) |
| `.online-join` | Join &lt;mission&gt; | Mints a ticket and joins the loopback server | player | Required to play in the browser; native join (`clintlib/appmsg.cpp`) |
| `.online-side` | &lt;side&gt; · N players | Sends POSITION_REQ and accepts the seat | player | Native side select `WinTrek/teamscreen.cpp:2750-2762` |
| `#join-retry` | Retry | Re-runs the staging/join after a failure | player | Required to play in the browser |
| `#online-briefing` | Mission briefing | Toggles the mission briefing pane built from `MissionBriefingInfo` | player | Native mission briefing `WinTrek/teamscreen.cpp:795` (title), `:1673` ("if we should show the mission briefing") |
| `#online-launch` | Ready and launch current ship | Sends `PLAYER_READY` then `DOCKED` | player | Native `WinTrek/hangarscreen.cpp:145-157`; `FedSrv/FedSrv.CPP:7031` (FM_C_DOCKED) |
| `#launch-reason`, `#launch-status` | reason / busy text | Visible blocker/countdown text for the launch click | player | Presentation of the same native rule (P1058) |
| `#online-close` | Disconnect and return to menu | Flushes `LOGOFF` and disposes the session | player | Required to play in the browser; native main menu `TK_MainMenu` `Igc/igc.h:248` |

### Flight toolbar (`_onlineState == "Flight"`)

| Id / element | Label | What it does | Class | Native cite / reason |
|---|---|---|---|---|
| `#online-pose` | "Ship 12 · sector 1053 · position … · N bytes received" | Diagnostic readout of ship id, sector, authoritative position and decoded byte count | **developer/proof** | No native player readout of this shape; added for the flight proofs (P1051). It is also `display:none` inside the flight toolbar (`wwwroot/menu.css:37`), so it exists only for the DOM-reading harnesses |
| (`.flight-help`) | "W/S thrust · arrows turn · …" | Key help line | player | Native key help (`TK_Help = 204`, `Igc/igc.h:428`) |
| `#flight-contact` | View nearby ships / Chase view | Switches between the chase camera and the external "contact" camera | player | Native view modes `TK_ViewExternal = 30` `Igc/igc.h:253`, `TK_ViewChase = 34` `Igc/igc.h:257` |
| `#flight-rock` | View nearby asteroid | Toggles `_viewRock`: draws nearby asteroid meshes for the sector-pack screenshots | **developer/proof** | No native player camera for an asteroid; added by P1054 for screenshots (`Game1.Sectors.cs:17`) |
| `#flight-angle` | Rotate view | Steps the external camera through six native directions | player | Native external orbit `TK_ViewExternalOrbit = 107` `Igc/igc.h:330` |
| `#flight-dock` | Dock at home station | Autopilots to the nearest friendly landable station (server-side docking unchanged) | player | Native autopilot `TK_ToggleAutoPilot = 43` `Igc/igc.h:266`, `TK_ExecuteCommand = 156` (`:374`); docking `FedSrv/FedSrv.CPP:7031` |
| `#flight-starmap` | Starmap | Toggles the always-on mini-map into the full sector/chart map | player | Native `TK_ToggleCommand = 129` `Igc/igc.h:352` ("Toggle command mode 3/4 vs. top down"), `TK_ViewSector = 37` `:260` |
| `#map-sector` | Sector &lt;select&gt; | Highlights a sector on the starmap and prefetches that sector's visuals | player (question 1) | Native starmap cluster selection `WinTrek/sectormap.cpp:1413-1461` (`MouseMove` → `SelectCluster`); the browser selection sends nothing, it only highlights/prepares visuals |
| `#flight-warp` | Aleph &lt;select&gt; | Autopilots toward a known aleph in the current sector (ordinary `goto` autopilot) | player (question 2) | Native warp targeting `TK_TargetWarp = 197` `Igc/igc.h:419` with autopilot `:266`/`:374`; the list is the client's known-warp table |
| `#flight-waypoint`, `#waypoint-x/y/z`, `#waypoint-go` | Waypoint, X/Y/Z, Fly to waypoint | Registers a data-buoy waypoint at typed coordinates and autopilots to it; used by P1054's recorded travel to reach charted mouths | **developer/proof** | Native waypoints are placed by picking a point in the 3D/command view (`WinTrek/console.cpp:1940-1972`, `clintlib/clintlib.cpp:2460-2469`), never typed as numbers |
| `#flight-fullscreen` | Fullscreen | Browser fullscreen gesture (also Alt+Enter) | player | Native `Alt+Enter` toggles fullscreen `engine/enginewindow.cpp:72-79` |

### Non-control status elements (listed for completeness)

| Element | Purpose | Class |
|---|---|---|
| `#pacing-note` | "Running slow · audio paused" frame-pacing status | player (presentation status, not a control) |
| `#sector-progress` | Sector-pack download/prepare status | player (presentation status, P1054 non-modal progress) |
| `#load-panel`, `#retry` | Menu load progress / retry | player (required to load the game in a browser) |

### Keyboard paths

The developer/proof controls have **no keyboard binding**: `ToggleRockView`, `FlyToWaypoint`,
`SelectMapSector` and the waypoint fields appear nowhere in
`src/Allegiance.ClientCore.Game/Rendering/InputLayouts.cs` or `InputActionCatalog.cs` (grep, no
matches), so there is no key path to make inert. The starmap key itself is the player binding
(`ToggleStarmap`, native `TK_ToggleCommand`) and stays live.

### Questions for Domingo

1. **`#map-sector`** — kept player-visible because native `SelectCluster` on the starmap is a player
   action (`WinTrek/sectormap.cpp:1413-1461`) and P1054 already recorded the browser selection as
   reusing it. But in the browser it only highlights a sector and prepares its pack (it sends no
   command). Say the word if you want it behind the switch instead.
2. **`#flight-warp`** — kept player-visible (native warp target + autopilot). P1054 also used it as
   the proof's travel selector. Same question: proof-only or player?
3. **Waypoints** — the typed X/Y/Z form is hidden (no native counterpart). Native players place
   waypoints by picking a point in the command view; a click-based player waypoint is a *new player
   feature* and therefore out of scope here. Want it queued?

## Item 1 — one dev switch

`src/Allegiance.ClientCore.Browser/wwwroot/dev-switch.js` (new) resolves the switch before the .NET
runtime starts, exactly the way P1058's `console-log.js` resolves `?log=verbose`: URL parameter
first, `localStorage['allegiance.dev']` otherwise, off by default, an unknown URL value falling
through to storage. It exposes `window.allegianceDevEnabled()` (what Blazor reads once while the
menu loads) and `window.allegianceDev.setEnabled(true|false)` (live, and it remembers the choice).
`?dev=1` is sticky like `?log=verbose`; `?dev=0` stores `'0'`, so it overrides an earlier `'1'`. A
missing script or a refused JS call leaves the component at `false`: the switch only fails towards
off.

| File | Change |
|---|---|
| `src/Allegiance.ClientCore.Browser/wwwroot/dev-switch.js` (new) | Resolution and the global API; no game, wire or server code |
| `src/Allegiance.ClientCore.Browser/wwwroot/menu.html:12`, `index.html:16` | Load it before the .NET runtime; content-hashed by `tools/p1053-browser-publish.py` (`dev-switch.b9d6a20bdb9c7e1245fa.js`; 39 hashed application files, was 38) |
| `src/Allegiance.ClientCore.Browser/Pages/Menu.razor.cs:31` | `_dev` field (default `false`) |
| `src/Allegiance.ClientCore.Browser/Pages/Menu.razor.cs:44`, `:87-93` | Read once per load through `allegianceDevEnabled`; a refused/missing JS call keeps it off |
| `src/Allegiance.ClientCore.Browser/Pages/Menu.razor.cs:177` | `dev` in `Metrics()` so a harness can read the state |
| `src/Allegiance.ClientCore.Browser/Pages/Menu.razor:93-96` | `#online-pose` only with the switch on |
| `src/Allegiance.ClientCore.Browser/Pages/Menu.razor:99-102` | `#flight-rock` only with the switch on |
| `src/Allegiance.ClientCore.Browser/Pages/Menu.razor:118-131` | `#flight-waypoint` and its X/Y/Z form only with the switch on |

With the switch off these elements are **not rendered** (no DOM node: nothing to click and nothing
reachable by Tab). No keyboard binding exists for any of them (`InputLayouts.cs`,
`InputActionCatalog.cs`: no match for `ToggleRockView`/`FlyToWaypoint`/`SelectMapSector`), so no key
path had to be made inert. `Game1.ToggleRockView`, `Game1.FlyToWaypoint`, `_pose` and every other
game method stay. Nothing in the switch touches simulation, prediction, transport, tickets, server
checks or limits — it is three `@if` blocks plus one boolean, and the desktop client never loads
the file.

## Item 2 — harnesses

Every harness that clicks a developer/proof control now opens the page with `?dev=1`:
`tools/p1051-browser-flight.cjs:12`, `p1051-browser-security.cjs:56`, `p1048b-browser-join.cjs:40`
(reads `#online-pose`), `p1053-browser-polish.cjs:42`, `p1054-browser-sectors.cjs:9`,
`p1058-browser-launch.cjs:17` (combined with `&log=verbose`). Nothing else in those harnesses
changed.

### New switch check — `tools/p1059-browser-devcases.cjs`

Real Chromium 153.0.8010.0, production identity/passkey, loopback server, actual join → side →
launch, then DOM counts. Four combinations; each starts from a cleared storage state (default off)
and then loads `?dev=1`:

| Combination | Switch off | Switch on |
|---|---|---|
| Classic 1920×1080 | 3 dev controls absent, 7 player controls present, `#map-sector` present on the open starmap | all 3 dev controls + 7 player controls present, X/Y/Z/go present |
| Modern 1920×1080 | same | same |
| Classic 800×480 | same | same |
| Modern 800×480 | same | same |

`dev` in `Metrics()` was `false`/`true` respectively, no page errors, and the documented stickiness
was proved on the real page: `?dev=1` → stored `'1'` → a later plain visit stays on; `?dev=0` →
stored `'0'` → a later plain visit is off. Harness receipt:
`{"passed":true,"combos":4,"sticky":true,"pngBytes":412157}`.

### Existing harnesses, re-run against the new generation

| Harness | Result |
|---|---|
| `tools/p1051-browser-flight.cjs` | **4/4 profiles PASS** (Classic 1920 cold, Modern 1920 warm, Classic 800 stale, Modern 800 storage-refused): controls, mount cycling/single-weapon fire, cross visibility and shots, pointer refusal + retry, blur neutral, hidden audio, same-ship WS resume, authoritative dock, clean logoff. p50 16.7 / p95 16.7–16.8 ms; warm visit fetched 0 pack files, stale fetched 1 |
| `tools/p1051-browser-security.cjs` (`--deferred-cancel`) | **11/11 cases PASS**: LAN plaintext listing, public WSS refusal, list-failure retry, deferred join-manifest cancel, deferred decoder cancel, wrong audience, expired ticket, ticket-failure retry, replayed ticket, stale mission cookie (ship registry unchanged), back-navigation. 577 Cache API files / 32,536,380 bytes inspected; no secret in storage, logs or URLs |
| `tools/p1053-browser-polish.cjs` (`after`) | **exit 0**, 5 profiles + the pacing/gamepad exercise: cold Modern menu 760.1 ms, warm 597.8 ms; scale steps 1 → .75 → .5 → .75 → 1 with audio suspend/resume, one Gamepad poll per frame, DPR cap 2, fresh control reversals accepted at both throttling rates |
| `tools/p1054-browser-sectors.cjs` | **4/4 profiles PASS** on a fresh proof host: capped entry surfaces, warm reload 0 pack requests/0 bytes, entry p95 16.7–16.8 ms, remote p95 16.7–16.8 ms, retry fixture honoured, clean logoff, authoritative sector 1051 |
| `tools/p1058-browser-launch.cjs` | **9 cases PASS**: 0/100/500/2000 ms quiet, 0 ms verbose, resume (Modern + Classic), hold (Modern + Classic) — one server `[ready]` per intended launch, no silent click, no 404s, per-asset lines 0 visible quiet / 65 visible verbose, favicon present |

Two fixture notes (no product change; see Unfinished): the first P1054 run hit a **rotated mission**
in the long-lived proof host (its asteroid instance is not the recorded one, and the `hold` launch
case could not reach flight in a mission whose stage had moved on), so P1054 sectors and the launch
cases were re-run against a **fresh** proof host and passed 4/4 and 9/9. One `hold --style Classic`
attempt died in identity registration (`#account` never appeared) and passed immediately on re-run.

## Item 3 — docs and screenshots

| Doc | Change |
|---|---|
| `docs/BROWSER_HOSTING.md:34-62` | "Developer/proof switch (P1059)": what is hidden, the four ways to set it, default off, the guarantees (rendering only), and which harnesses open with `?dev=1` |
| `GLOSSARY.md:121-137` | Browser glossary entry: the switch, its files, the player-mapped controls, proof and parity links |
| `docs/parity/p1059-browser-dev-controls.csv` | Two `n/a` rows (hidden dev controls = modern presentation only; player controls reuse native rules without new credit); `scripts/parity-scorecard.py` validates 2 rows, LF only |
| `tools/p1059-dev-switch-tests.cjs` | 6 `node --test` cases: default off, `?dev=1`/`?dev=0`, unknown values, storage fallback, refused storage, `install`/`setEnabled` persistence |

Screenshots are unedited Chromium captures at 800×480, `?dev` off then `?dev=1`, both HUD styles. I
opened every one; what each actually shows is:

* [toolbar Classic off](BROWSER_DEV_CONTROLS_P1059_assets/toolbar-classic-off.png) — the flight
  toolbar alone: "Mission briefing", "View nearby ships", "Dock at home station", "Starmap", the
  Aleph selector showing "Select destination", "Fullscreen", "Disconnect and return to menu". No
  asteroid or Waypoint button.
* [toolbar Classic on](BROWSER_DEV_CONTROLS_P1059_assets/toolbar-classic-on.png) — the same toolbar
  with "View nearby asteroid" added after "View nearby ships" and "Waypoint" added before
  "Fullscreen".
* [toolbar Modern off](BROWSER_DEV_CONTROLS_P1059_assets/toolbar-modern-off.png) and
  [toolbar Modern on](BROWSER_DEV_CONTROLS_P1059_assets/toolbar-modern-on.png) — the HTML toolbar is
  the same in both styles (as before P1059): off has no asteroid/Waypoint buttons, on has both.
* [flight Classic off](BROWSER_DEV_CONTROLS_P1059_assets/flight-classic-off.png) — Classic HUD: arc
  gauges labelled THRUST, FUEL, SHIELD, ARMOR, ENERGY, AMMO around the ship, 32 mps, CHASE, the
  callsign tag `DevSwitch1059226678`, asteroids and a red reticle; the bottom bar is the off-state
  toolbar above. No pose line is visible.
* [flight Classic on](BROWSER_DEV_CONTROLS_P1059_assets/flight-classic-on.png) — the same Classic
  scene at 30 mps with "View nearby asteroid" and "Waypoint" in the bar. No pose line is visible
  either: `menu.css:37` hides non-role paragraphs inside the flight toolbar, so `#online-pose` never
  appears in a capture; its presence is proved by the DOM counts above.
* [flight Modern off](BROWSER_DEV_CONTROLS_P1059_assets/flight-modern-off.png) — Modern HUD: circular
  gauges AMMO/ENERGY/ARMOR/THRUST/FUEL at 100 %, SHIELD 0 %, 26 mps, "HUD Tactical / RADAR Default /
  CHASE", a planet below the asteroid field; toolbar without asteroid/Waypoint.
* [flight Modern on](BROWSER_DEV_CONTROLS_P1059_assets/flight-modern-on.png) — the same Modern scene
  at 42 mps with "View nearby asteroid" and "Waypoint" in the toolbar.

Total PNG bytes **412,157** (limit 1 MB); the asset directory holds 8 PNGs plus the proof JSON.

## Verification

| Check | Result |
|---|---|
| `dotnet build AllegianceCore.slnx` (browser projects included) | **0 warnings / 0 errors** |
| `tools/build-tools.sh` | **all 148 tool projects built**, no failure |
| Browser head tests `tests/Allegiance.ClientCore.Browser.Tests` | **12/12 passed** |
| Browser game-seam tests `tests/Allegiance.ClientCore.Game.Browser.Tests` | **74/74 passed** |
| New JS switch tests alone | **6/6 × 3 runs** |
| New Chromium switch harness | 4/4 combinations, off and on, sticky switch, no page errors |
| Release publish `scripts/publish-browser-flight.sh` | PASS — 39 content-addressed application files (dev-switch.js hashed), the generation used by every Chromium run above |
| Desktop zero-delta (`src/Allegiance.ClientCore.MonoGame/bin/Debug/net10.0`) | **753 files before (built from `git archive 20b52780` in scratch) and 753 now; `diff` of the sorted relative names empty — identical set.** Assembly bytes always differ across builds (MVID/PDB ids), so the gate is the filename set, as P1058 used. No shared `src/Allegiance.ClientCore.Game` file changed (`git status`), so no HUD run was needed |
| Final `tools/run-tests.sh --tier fast` (run last) | **15,563 passed / 3 failed / 7 skipped of 15,573, 661.0 s, exit 1**; 9/9 infrastructure checks passed; browser head 12/0/0 and game-seam 74/0/0 inside the sweep. Each failing method then ran **3× in fresh processes and passed 9/9** (`P590PodRescueTests.Pod_pickup_order_arms_NPC_diverter_and_rescues`, `GameOverPlayersWireTests.Rejoined_player_accumulates_time_across_stints_in_roster`, `ChaffTests.Live_firing_mounted_probe_launcher_creates_probe_decrements_ammo_and_broadcasts`) — all three are existing ledgered families, now recorded in `docs/TEST_FLAKES.md`; Fast stays red for them |

## Evidence

* [switch proof](BROWSER_DEV_CONTROLS_P1059_assets/dev-controls-proof.json) — per-combination DOM
  counts, `dev` metric, stickiness, console errors.
* Raw logs (builds, publish, the five harnesses with their JSON/console dumps, the nine isolated Fast
  runs, the Fast TRX set) are in the lane evidence directory outside the repo: `fast/`,
  `fast-isolated/`, `harnesses/`, plus `build.log`, `build-tools.log`, `publish.log` and the
  generation receipts.

## Unfinished / notes

* Items 0–3 are complete and every requested gate ran. Two harness observations are fixture issues,
  not product defects: the long-lived proof host's mission had **rotated** by the time P1054 sectors
  and the launch `hold` case ran (its asteroids are not the recorded ones, and its stage had moved
  on), so both were re-run on a fresh host — 4/4 and 9/9; one `hold --style Classic` attempt timed
  out during identity registration and passed on the immediate re-run.
* Proof-environment tooling (not product, nothing committed): this container had no Playwright or
  Chromium shared libraries, so `playwright@1.65.1` + `@sparticuz/chromium@153.0.0` were installed
  into ignored scratch, and `libnspr4`/`libnss3` were fetched with `apt-get download` and
  `dpkg-deb -x` into scratch (no root install). All *proof* traffic still stayed on loopback and the
  harnesses assert it.
* Not in scope and untouched: toolbar styling, new player features, removing any control or game
  method, server/lobby/wire changes, hosting/CSP/CDN, desktop HUD.
* `P1059_BRIEF.md` was removed from the worktree at the end (a copy stays in the lane evidence
  directory) so it is not part of the change set.

## Native intent

Native Allegiance has no developer toolbar at all: its player controls are key-bound
(`TK_ViewExternal`/`TK_ViewChase` `Igc/igc.h:253,257`, `TK_ViewExternalOrbit` `:330`,
`TK_ToggleAutoPilot` `:266` / `TK_ExecuteCommand` `:374`, `TK_ToggleCommand` `:352`, `TK_ViewSector`
`:260`, `TK_TargetWarp` `:419`, `TK_ScrnShot` `:445`, `Alt+Enter` `engine/enginewindow.cpp:72-79`,
mission briefing `WinTrek/teamscreen.cpp:795,1673`, docking `FedSrv/FedSrv.CPP:7031`), and its
waypoints are picked in the 3D/command view (`WinTrek/console.cpp:1940-1972`,
`clintlib/clintlib.cpp:2460-2469`). The browser toolbar is an HTML convenience layer over those same
client actions; P1059 keeps every one of them visible and hides only the three controls that have no
native player counterpart or are proof readouts. No simulation, prediction, transport, ticket, limit
or privilege check changed — the switch is presentation only.

## Modern approach

A single small script resolves one documented switch before the .NET runtime starts
(`?dev=1` / `localStorage['allegiance.dev']`, `?dev=0` off, sticky like `?log=verbose`), the Blazor
component reads it once, and three `@if` blocks decide whether the proof-only elements exist in the
DOM at all. Hidden, not deleted: developers and the harnesses opt in with `?dev=1`, players never
see them, and Classic and Modern share the same markup and the same decision.

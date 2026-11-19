# Browser launch — P1058

Status: **items 0–3 complete; qualification done with two documented harness caveats.** Started
2026-11-19 03:05 UTC (lane local time), finished 2026-11-19 05:25 UTC. No commits, pushes,
deployment or external network access. Changes are left in the working tree; the brief is in ignored
scratch. Headless Chromium measurements are **not** GPU-representative.

## Item status

| Item | Status |
|---|---|
| 0 — Reproduce ("human pace" clicks) | **Complete.** Baseline measured at 0/100/500/2000 ms and in the same-session resume window. |
| 1 — Launch never silent, never stuck | **Implemented; Chromium-proved** at every delay and across a resume, Classic and Modern. |
| 2 — Favicon | **Implemented; 0 404s on the game page.** `favicon.png` derived from `packaging/allegiance.svg`. |
| 3 — Quieter console | **Implemented; Chromium-proved** quiet default + `?log=verbose`. Desktop output unchanged. |

## Root cause

Claude's reading is **half right and half wrong**, and the wrong half matters:

* **Wrong:** "the Hangar state is shown before the client knows its ship id". It is not. The
  hangar is entered only after `SelectSideAsync` has waited for the ship id
  (`src/Allegiance.ClientCore.Browser/Pages/Menu.Online.cs:178-182`:
  `await WaitOnlineAsync(session, () => session.World.MySideId == side && session.World.MyShipId is not null)`),
  and an immediate click when the Hangar state appears **launches successfully** — measured at
  0/100/500/2000 ms on the unmodified base (table below), one server `[ready]` line per click.
* **Right:** the silently-dropped click is the `MyShipId` guard at the top of
  `LaunchOnlineAsync` (`Pages/Menu.Online.cs:189` before the fix), and the click leaves no trace
  at all: no status, no error, no `PLAYER_READY` on the wire, so no server `[ready]` line.

**The real trigger is a same-session world rebuild.** `ClientWorld` clears the launch identity on
every join burst:

* `src/Allegiance.ClientCore/State/ClientWorld.MissionEntry.cs:74-78` — `JOINED_MISSION` sets
  `JoinedShipId = message.ShipId` and **`MyShipId = null`** ("the joined player exists; a launch
  pose has not been assigned yet");
* `src/Allegiance.ClientCore/State/ClientWorld.cs:2920` — only the later `SET_CLUSTER` sets it
  (`MyShipId ??= set.Ship.ShipId`, native `clintlib/appmsg.cpp:1009-1093`);
* `ClientWorld.ResetCore` (`:2308`, `MyShipId = null`) does the same on a full reset.

A browser auto-reconnect **resume** (default-on for the browser session,
`Menu.Online.cs:130` `session.EnableAutoReconnect()`) re-sends the native join burst on a fresh
socket: `ConnectionSession` rebuilds the world with `ResetPreservingMissionIdentity`
(`ConnectionSession.cs:2298`) and then re-applies `JOINED_MISSION` → `SET_CLUSTER`. In that
window `MyShipId` is `null` **while the page still shows the Hangar panel**: nothing changes
`_onlineState`, nothing shows a status, and the launch button stays enabled because it was only
disabled on `_onlineBusy` (`Pages/Menu.razor:85`). A click there hits `:189` and returns
silently. The window is invisible to the player: `RefreshOnline` only reports `session.Lost`
(`Menu.Online.cs:254`), never an in-progress resume.

Measured on the base (evidence `launch-resume.json`): the click fired (`domClicks=1`), the guard
was `no-ship`, `readySends` stayed 0, the server logged **no** `[ready]` for it; 2 s later, on the
same session and the same ship id, the same button launched (stage 0 → 2 → Flight, one `[ready]`
line). That is exactly the reported shape: several silent clicks, no server `[ready]`, then a
later click on the same peer and ship works with no reload.

Why a resume happens while sitting in the hangar is not client-choice-dependent: the browser tab
keeps one WebSocket; a dropped socket (proxy/idle/background-tab timer throttling) is resumed
silently by design. The brief's own host log line (`ENTER_GAME(128) deferred …`) is unrelated to
this drop: the ship id is not carried by `ENTER_GAME` at all, and the deferred message is
delivered on ready-up auto-start, i.e. *after* the click.

### The field that sets the ship id, and when it arrives

| Value | Set by | Message | Arrives |
|---|---|---|---|
| `JoinedShipId` | `ClientWorld.MissionEntry.cs:76` | `JOINED_MISSION` | first message of the join burst, before the hangar is reachable |
| `MyShipId` | `ClientWorld.cs:2920` (`??=`) | `SET_CLUSTER` | later in the same burst (pose + cookie) |
| *both cleared* | `ClientWorld.cs:2308`, `MissionEntry.cs:77` | `Reset` / a new `JOINED_MISSION` | at every resume/rejoin |

`JOINED_MISSION` already names the player's own ship (native `clintlib/appmsg.cpp:2150-2214`
creates the dummy ship at `clintlib/clintlib.cpp:3860-3880` and calls `pPlayerInfo->SetShip()`,
so `GetShipID()` works immediately), which is why the fix can launch on `MyShipId ?? JoinedShipId`
with no server or wire change.

## Reproduction (item 0)

`tools/p1058-browser-launch.cjs`, a fresh proof host per case (`tools/p1051-browser-host`), real
lobby enrollment/passkey identity/fresh tickets, Modern style, 1920×1080. "Delay" is measured
from the first rendered Hangar panel to the click; no briefing click, no other waits.

| Case | Base (201b5f9c) | Fixed tree |
|---|---|---|
| 0 ms | launched: `readySends=1`, 1 server `[ready]` | launched: 1 `[ready]` |
| 100 ms | launched: `readySends=1`, 1 `[ready]` | launched: 1 `[ready]` |
| 500 ms | launched: `readySends=1`, 1 `[ready]` | launched: 1 `[ready]` |
| 2000 ms | launched: `readySends=1`, 1 `[ready]` | launched: 1 `[ready]` |
| resume window (socket dropped after the Hangar panel; click when `MyShipId` is `null`) | **silent drop**: the DOM click fired, guard `no-ship`, `readySends=0`, `busy=false`, button **enabled** (the last paint predated the reset), no status, no alert, **0** server `[ready]` for that click; 2 s later the same session launched with 1 `[ready]` — the reported shape exactly | the click is answered with “Reconnecting to the server…” (`role=alert`, measured in the DOM), `readySends=0`, no `PLAYER_READY` sent; while the resume keeps retrying (the “hold” case below) the button renders **disabled** with the same reason (`#launch-reason`), and once the resume completes it re-enables and the retry launches with **1** `[ready]` |
| resumed-upgrade refused for ~3 s ("hold", both styles) | not exercised | disabled with “Reconnecting to the server…” after 174 ms (Modern) / 153 ms (Classic), `shipId=null`, `reconnecting=true`, `resumeAttempts=1`; on release `disabled=false`, reason gone, `resumeSuccesses=1`, `resumeAttempts=2`, launch with 1 `[ready]` |

## Changes

| Source | Change |
|---|---|
| `src/Allegiance.ClientCore/State/HangarLaunch.cs` (new) | One shared ready/launch decision: `Blocker` (`NoSession`, `Reconnecting`, `NoShipAssigned`), the launch ship id (`MyShipId ?? JoinedShipId`, present in the world table), and a pure `Evaluate` the tests drive. Native rules and citations are in its doc comment. |
| `src/Allegiance.ClientCore.Browser/Pages/Menu.Online.cs` | The three silent returns are gone: every blocked click sets `_onlineError` with the reason. The launch uses `HangarLaunch`'s ship id; the wait for `STAGE_STARTED` is now the reported `WaitOnlineForStartAsync` (live countdown, 60 s without stage/countdown movement → clear timeout message) instead of an unbounded silent wait; `BusyStatus()` names what the click waits for. |
| `src/Allegiance.ClientCore.Browser/Pages/Menu.razor` | The Hangar button is `disabled="@(!launch.Enabled)"` from that one decision and a `#launch-reason` status line (`role=status`) shows why; the busy line is `#launch-status` with the countdown/ready text. Classic and Modern differ only in chrome. |
| `src/Allegiance.ClientCore.Browser/Pages/Menu.razor.cs` | `Metrics()` exposes the launch/online state the harness reads (`launch.*`, `online.resumeAttempts/reconnecting`). No behaviour. |
| `src/Allegiance.ClientCore.Browser/wwwroot/console-log.js` (new) | Console router: info → `console.debug`, warnings/errors always shown, a log line whose own text reports a failure is promoted; `?log=verbose` / `localStorage['allegiance.log']` / `allegianceLog.setVerbose(true)` restore the full stream. Loaded before the .NET runtime, which binds `console.log` at startup. |
| `src/Allegiance.ClientCore.Browser/wwwroot/menu.html`, `index.html` | Declare the favicon and load `console-log.js` first. |
| `src/Allegiance.ClientCore.Browser/wwwroot/favicon.png` (new) | 32×32 RGBA icon derived from `packaging/allegiance.svg` (the same artwork `packaging/generate-icon.py` uses for the macOS icon); content-hashed by `tools/p1053-browser-publish.py` like every other application file (`favicon.47ff617b3e387ab71cc4.png`). |
| `packaging/generate-favicon.py` (new) | Reproduces that PNG from the SVG's own shapes with 4×4 supersampling (no renderer dependency). |
| `tests/Allegiance.ClientCore.Tests/HangarLaunchTests.cs` (new) | 8 cases: JOINED_MISSION identity before the pose, pose-first precedence, and every blocker (no session, reconnecting, no ship, id outside the world). |
| `tools/p1058-browser-launch.cjs` (new) | The item-0 harness: human-pace clicks at 0/100/500/2000 ms, the same-session resume window, a refused-upgrade "hold" case that keeps the resume open, Classic and Modern, quiet/verbose console, favicon/404 and one-`[ready]`-per-launch assertions. |
| `tools/p1058-console-log-tests.cjs` (new) | `node --test` for the level router (real lines, promotion, quiet/verbose, switch precedence, live toggle). |
| `tools/p1051-browser-flight.cjs`, `p1053-browser-polish.cjs`, `p1051-browser-security.cjs` | Callsign field set through the DOM instead of synthesized typing: **this container's** headless Chromium build delivers keydown but not text insertion (measured with `fill`, `keyboard.type` and raw CDP `Input.insertText`; the same failure on a plain input). No assertion weakened. The security harness additionally reads the fresh-ticket retry through a one-shot route (read before delivery, so the harness's own navigation cannot invalidate it) and counts the one ticket body its observer loses to navigation instead of failing it; its case assertions are unchanged and it reports the count. *(Merge note: superseded by P1054's route-owned capture, which reads every ticket body before delivery; the counter was removed.)* |
| `tools/p1051-loopback-proxy.cjs` | Proof-only `hold-ws` control: while the file exists the proxy refuses the upgrade, so the client's own resume retry stays visible long enough to capture the blocked affordance. Production paths untouched. |
| `docs/parity/p1058-browser-launch.csv`, `GLOSSARY.md` | Three `n/a` rows (two native rules reused, one modern presentation) and the glossary entry. |

The same proof also needed a writable fontconfig pointing at the fonts `@sparticuz/chromium` extracts;
without it this Chromium build lays HTML text out with no glyphs (the KNI canvas draws its own font
atlases, so only the HTML panel looked empty). That is a scratch-only proof setting, not a product change.

### Console levels (item 3)

A normal session emits ~65–78 per-asset information lines (`[monogame] status art …`,
`hud-10 art …`, `target-pane …`, `[sim] freeze/resume …`, `[hd] …`) plus the KNI/WebGL chatter. With
the router loaded, the same session is:

| Default (`quiet`) | `?log=verbose` |
|---|---|
| `perAsset` lines as **`console.debug`**: 65 | `perAsset` lines as **`console.log`**: 65 |
| visible `debug` lines in total: 74 | every line at its own level, nothing filtered |
| a real problem (`status art 'enemybmp' is fully transparent … procedural fallback`) promoted to **`warning`** (visible) | same text, `log` level |
| genuine errors (e.g. the expected 401 from the unauthenticated `/api/identity/me`) stay **`error`** | unchanged |

Evidence: `console-0.txt` / `console-0-verbose.txt` beside the receipts. The desktop process never
loads this file (it lives in the browser `wwwroot`), and no shared logging call site changed.

## Verification

| Check | Result |
|---|---|
| Solution build (both browser projects included) | **0 warnings / 0 errors** |
| `tools/build-tools.sh` | built every `tools/*/*.csproj`; result below |
| Browser Release publish (`scripts/publish-browser-flight.sh`) | PASS — the generation used for every Chromium case below (38 content-addressed application files, favicon included) |
| Browser head tests | **12/12 passed** |
| Browser game-seam tests | **59/59 passed** |
| New C# launch-gate tests alone | **8/8 × 3 runs** |
| New JS console-router tests alone | **6/6 × 3 runs** |
| P1058 Chromium launch cases (item 0/1) | nine cases, one server `[ready]` per intended launch, no silent click, 0 404s |
| Desktop Debug output filenames | **753 before / 753 now, identical set** (`src/Allegiance.ClientCore.MonoGame/bin/Debug/net10.0`) |
| Desktop HUD 1,848-case comparison | below |
| Desktop log text, fixed run | below |
| P1051 flight harness, P1053 polish harness, 9-case security harness | below |
| Final `tools/run-tests.sh --tier fast` | **RUN LAST — 15,551 tests, 1,142.3 s, FAIL** (4 failed, 4 not executed, 5 slow), all four failure methods 3× alone (three pass 3/3, the P739 timer family reproduces). Ledgered in `docs/TEST_FLAKES.md`. |

`tools/build-tools.sh --drop-bins`: **all 148 tool projects built**, no failure reported (each build
0 warnings / 0 errors; log in `<lane evidence dir>`).

### Desktop zero-delta

* **Debug output filenames**: `src/Allegiance.ClientCore.MonoGame/bin/Debug/net10.0` — 753 files in
  the base tree (`git archive` of `201b5f9c`) and 753 now, **identical set** (sorted relative paths,
  SHA-256 `0b12486ad16813658f8d58678ca369d0fe27ea1252cc9f7aa9faabf8951295ce` on both).
* **HUD 1,848-case comparison**: captured the full P980 matrix with `tools/hud2-proof --capture` from
  the base tree, then re-rendered it on this tree with `--compare`:
  **1848 combinations, 0 pixel differences outside the animation masks, 0 semantic differences,
  82 animation-only regions, 0 corrupt goldens → PASS** (the same 82 animated-badge regions P1053
  recorded).
* **Desktop log text, fixed run**: the same 1,848-combination run's shared diagnostics
  (`[monogame] …`, `[sim] …`, `[hd] …`) — **22 lines, byte-identical** between the base tree and this
  tree (`diff` empty). The console router is a browser `wwwroot` script; no desktop/shared logging call
  site changed.

### Harness regressions

* **9-case security harness**: **9/9 passed** (LAN plaintext listing, public WSS refusal, list failure
  retry, wrong-audience, expired, ticket-failure retry through a real fresh-ticket join, replayed
  unexpired ticket, wrong/stale mission cookie with an unchanged ship registry, browser
  back-navigation). 573 Cache API files / 32,906,544 bytes inspected; no secret in logs or URLs.
* **P1051 flight harness** and **P1053 polish harness**: results below.

### Flight and polish harnesses

* `tools/p1051-browser-flight.cjs` (four profiles, gen4): **Classic 1920×1080 cold PASS**
  (controls, mount cycling/single-weapon fire, cross visibility/shots, pointer refusal+retry, blur,
  hidden audio, same-ship WS resume, 60 s RAF sample p50 16.7 / p95 16.7 ms, authoritative dock, no
  zombie) and **Modern 1920×1080 warm PASS** (p50 16.7 / p95 33.4 ms). The third profile (Classic
  800×480 stale) stopped at P1053's own audio assertion
  (`gameFlight.audioStates()` not all `suspended` 100 ms after a synthetic `visibilitychange`,
  `tools/p1051-browser-flight.cjs:123`); that code path (`wwwroot/flight-input.js` visibility/pacing)
  is not touched by this slice. A single-profile re-run (`--modern-cold`) then passed end to end
  (exit 0, 611,253 bytes of captures), and the security harness passed in the same session.
* `tools/p1053-browser-polish.cjs` (`after --timing-only`, gen4): **exit 0**, including its pacing and
  gamepad exercise. Cold Modern 1920×1080 shell 48 ms / menu 808.1 ms / flight 12,606.3 ms; warm
  40 / 567.1 / 48,626.1 ms — the same figures P1053 recorded (796.8 / 631.7 ms menu).


### Evidence

* [launch-cases.json](BROWSER_LAUNCH_P1058_assets/launch-cases.json) — base vs fixed per-case records
  (guards, `readySends`, server `[ready]` lines per launch, console level counts, 404/favicon, blocked
  affordance).
* Screenshots (unedited Chromium captures, opened and reviewed): [Modern blocked hangar](BROWSER_LAUNCH_P1058_assets/blocked-modern-hold.png),
  [Classic blocked hangar](BROWSER_LAUNCH_P1058_assets/blocked-classic-hold.png),
  [Modern resume alert](BROWSER_LAUNCH_P1058_assets/blocked-modern-resume.png). Each shows the bug
  window *fixed*: the launch button, the visible “Reconnecting to the server…” line and “Disconnect
  and return to menu”. The two Modern captures also show the native hangar text (callsign, Station 1051);
  the Classic capture's canvas is blank behind the panel (review note, Claude), and its launch button is
  rendered disabled. In the Modern resume capture the button is still enabled — that is the raced-click
  case, answered by the alert line.
  Total PNG bytes **106,882**; the whole asset directory is **132 KB** (limit 2 MB).
* Raw logs (builds, tool builds, HUD capture/compare, harness runs, per-case JSON/console dumps) live in
  the lane evidence directory outside the repo (`~/allegiance-evidence/P1058/`).

## Unfinished

* Items 0–3 are complete; every requested gate ran. Two harness observations are recorded rather than
  waived: the P1051 flight harness's third profile (Classic 800×480) stopped at P1053's own 100 ms
  audio-visibility assertion (single-profile re-run passed end to end, security harness re-passed in
  the same session), and the security harness needed the two proof-observer fixes listed under Changes.
* Proof-environment caveats (not product defects, all documented above): this Chromium build does not
  implement text insertion (harnesses set the callsign through the DOM) and needs an explicit
  fontconfig for HTML text to render; the browser tests, publish and both HUD/log proofs are green.
* Not in scope and untouched: hangar visuals/shop, lobby, hosting, server/wire/game rules, other
  engines, physical mouse calibration.

## Native intent

The ready click is the native one: `PLAYER_READY` for the player's own ship, whose id the native client
already has from `JOINED_MISSION` (`clintlib/appmsg.cpp:2150-2214`, dummy ship at
`clintlib/clintlib.cpp:3860-3880`, sent from `WinTrek/WinTrek.cpp:8192-8206` /
`WinTrek/teamscreen.cpp:2750-2762`, flag on the local player info `clintlib/clintlib.h:333`). The
launch stays a `DOCKED` request that only takes effect once the mission is `STAGE_STARTED`
(`FedSrv/FedSrv.CPP:7031-7034`), and the hangar's launch needs the client's own ship
(`WinTrek/hangarscreen.cpp:145-157`). Nothing in this slice changes a wire message, a server
check, a limit or a privilege; the server still re-stamps the relay with its own peer ship id
(`src/Allegiance.Server/Server.Dispatch.cs:1931-1957`).

## Modern approach

Browser lifecycle only: one shared readiness decision drives the HTML affordance (disabled with the
reason, enabled the moment the world allows it), the silent early-return is replaced by an explicit
message, the readiness wait reports the countdown instead of hanging, a console level router keeps the
DevTools console on warnings/errors with a documented `?log=verbose` switch, and a small content-addressed
favicon derived from the repository's own artwork. The desktop client and its logging are untouched.


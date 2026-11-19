# Browser polish — P1053

Status: **items 0–4 complete; qualification complete with a red Fast sweep**. Started
2026-11-18 at approximately 17:56 UTC; six-hour cap 23:56 UTC. Finalized 2026-11-18 23:31 UTC within that cap. No commits, pushes, deployment or external network access. Changes are
left in the working tree. Raw evidence lives in `<lane evidence dir>`; the brief
is retained only in ignored scratch. Headless Chromium measurements are **not
GPU-representative**.

## Item status

| Item | Status |
|---|---|
| 0 — Merged route rule | **Passed on the base.** Modern 1920×1080 cold full flight harness; original seven security cases plus two new listing refusals: **9/9**. No listing, privilege or admission rule changed. |
| 1 — Frame pacing | **Implemented and Chromium-proved.** Real CDP throttling suspends audio, reaches half resolution and recovers; newly reversed controls reach the authoritative server throughout. Final proof records all five scale states, actual viewport/buffer equality and capped DPR. |
| 2 — Gamepad | **Implemented and Chromium-proved.** Exact shared desktop pad method, native dead zone/throttle latch; matching quantized keyboard controls, fire, blur and disconnect release. Final repeat confirms **one API poll per flight frame** and release with fire held at disconnect. |
| 3 — First load | **Implemented; target met.** Final cold menu **1,286.1 → 796.8 ms (38.0% faster)**. Shell **128 → 48 ms**; flight **13,649.8 → 12,181.0 ms**. Warm menu **988.3 → 631.7 ms**; warm flight **48,669.2 → 49,176.3 ms**. |
| 4 — Layout | **Implemented, both styles/sizes captured.** Measured browser chrome insets replace overlays. All 17 selected before/after images opened and reviewed; current PNG total **1,679,365 bytes**. **1,848-case desktop zero delta passed**. |

## Changes and rules

| Source | Change |
|---|---|
| `src/Allegiance.ClientCore.Browser/wwwroot/frame-pacing.js:3` | Pure RAF-gap policy, tested without busy loops. Hidden RAF samples clear the sampling window and cannot authorize audio recovery. |
| `wwwroot/flight-input.js:5,64,75` | Audio remains gesture-owned and respects focus/visibility; standard-pad polling feeds cached physical controls, with complete release on blur, hidden and disconnect. A refused API or non-standard mapping falls back to keyboard/mouse. |
| `wwwroot/preview.js:21,28,77` | Samples pacing and gamepad once before the existing game tick; changes only the WebGL drawing buffer. Boot preloads remain behind the existing WASM/WebGL capability gate. |
| `src/Allegiance.ClientCore.Game.Browser/Game1.Browser.cs:53` | Explicitly synchronizes KNI's retained viewport and presentation dimensions with the DOM buffer; deferred flight fonts/core no longer delay the menu. |
| `src/Allegiance.ClientCore.Game/Game1.GamePadFlight.cs:20` | Extracts the desktop `ApplyGamePadFlight` method **byte-for-byte unchanged** into a source-shared partial. Desktop callers, bindings and behavior are unchanged. |
| `src/Allegiance.ClientCore.Game.Browser/Game1.Flight.cs:54,216` | Converts standard physical pad slots to `GamePadState`, then calls that same method. Manual turn axes cancel docking steering using the existing dead-zone rule. Release clears pad axes/buttons, shoulder latches, keys, mouse and autopilot, and sends native neutral controls. |
| `src/Allegiance.ClientCore.Browser/Pages/Menu.razor.cs:149` | Small presentation-size and raw-pad interop adapters. |
| `src/Allegiance.ClientCore.Game.Browser/Assets/AssetStager.cs:20,59` | At most four asynchronous verified downloads overlap. Failed batches drain before returning; only a complete verified snapshot is published. No worker thread or unbounded fanout. Menu-only staging defers obsolete-file cleanup until a complete join stage; existing 64 MiB/1,024-entry limits and verification still apply. |
| `src/Allegiance.ClientCore.Browser/Pages/Menu.Online.cs:63,90` | Defers full join-manifest parsing and verified Brotli compilation until Join; reuses complete resident assets on same-page rejoin. Shared preparation publishes only a fully linked decoder; generation checks retire canceled joins before they mint tickets, connect, or replace sessions. |
| `tools/p1053-browser-publish.py:15,53` | Keeps only the menu's actual two font bakes and click sound in initial staging; all 26 deferred entries must remain byte-identical in the complete join manifest. Hashes remaining application files, preserves SDK fingerprinted WASM/JS/**ICU data**, rewrites imports/links, removes stale compressed HTML/metadata, and emits gated boot preloads. |
| `wwwroot/menu.js:2`, `wwwroot/menu.css` | Reports top controls, wrapped bottom toolbar and footer as canvas insets. Hangar controls use the same bottom rail; the briefing expands above it. Desktop HUD renderer sources are unchanged. |

Here `wwwroot/` means `src/Allegiance.ClientCore.Browser/wwwroot/`.

### Pacing numbers

A **strictly greater than 100 ms** RAF gap suspends tracked AudioContexts and
shows the non-modal “Running slow · audio paused” note. Recovery requires
**30 consecutive gaps strictly below 34 ms**. Gaps of 34–100 ms reset that count.
Recovery only removes the pacing veto: it cannot enable audio without the prior
trusted gesture, while blurred, or while hidden. Contexts created during a stall
also start suspended. Native flight mixing remains outside this browser slice.

The backing ratio is `min(devicePixelRatio, 2) × scale`. A rolling **2-second**
RAF-gap window uses nearest-rank p95. After the first full window, **p95 >40 ms
for 2 seconds** steps **1 → .75 → .5**; **p95 <34 ms for 5 seconds per step**
recovers **.5 → .75 → 1**. Every step resets its sustained-pressure timers;
changes are at least 2 seconds apart downward and 5 seconds upward. Sparse,
seconds-long frames still lower resolution rather than starving the window.

The scene and native HUD share the reduced buffer. CSS `image-rendering:
pixelated` keeps bitmap edges crisp instead of adding a soft browser resample;
the fallback has coarser pixels, while HTML controls/notes stay at CSS resolution.
This keeps both native HUDs on the same inexpensive browser presentation path.
The full-resolution viewport returns with hysteresis. Simulation, prediction,
server-clock logic and the native 0.15-second pose cadence are unchanged.

Actual Chromium CDP rate **20 → 1** observations (each buffer size matched the
KNI viewport):

| Scale | Drawing buffer | p95 ms | Tracked audio |
|---|---|---:|---|
| 1 | 1920×997 | 33.3 | running |
| .75 | 1440×748 | 199.9 | suspended |
| .5 | 960×499 | 216.8 | suspended |
| .75 | 1440×748 | 16.8 | running |
| 1 | 1920×997 | 16.8 | running |

Fresh left/right command reversals were accepted at each throttle setting.
Injected DPR 3 was capped at 2, producing 3840×1994. Standard fake-pad and
keyboard yaw/pitch/throttle were **1/1/1**, with native stateM **32768** (group
fire); blur and disconnect produced **0/0/−1**, stateM **0**, on the server. The
held pad stayed neutral while blurred. The API poll count equaled flight frames.

### Gamepad source and boundaries

`GamePadFlightRoles.DefaultLayout` in
`src/Allegiance.ClientCore.Game/Flight/GamePadFlightInput.cs:58` supplies the
existing Mode-2 mapping; `JoystickFlightController.cs:139,172,229` supplies the
**0.1** dead zone, native signed rescale and movement-latched raw throttle.
`Game1.GamePadFlight.cs:20` supplies the existing fire/booster/shoulder bindings
and preserves keyboard ActionMap fire groups and weapon selection. Browser Y
axes are converted from down-positive to MonoGame's up-positive coordinates;
standard physical button/trigger slots are translated without a browser action
binding table. Non-standard pads are ignored, with keyboard/mouse still usable.
No touch, rebinding UI, rumble or force-feedback work is included.

Reused native intent: `WinTrek/WinTrek.cpp:94-99,10783-10796,10819-10892`
(dead zone, joystick response and throttle ownership), `:8326-8333` (manual-turn
cancellation), and the already shared pose/control cadence. The supplemental
`docs/parity/p1053-browser-polish.csv` records reuse as **n/a**, with no new parity
credit or game-rule claim. No wire, server simulation, listing rule, throttle,
limit or privilege check changed.

## Item 0 — base results

The base is merge `ec268c2a9c5f`. Its loopback-directory exception accepts local
WS/WSS only when the lobby itself is loopback; public directories retain strict
public WSS ingestion. The browser's existing public-join gate remains intact.
The proof uses actual local enrollment, passkey identity, fresh signed tickets,
server simulation and a normally authenticated opposing desktop pilot.

| Case | Result |
|---|---|
| Modern 1920×1080 cold P1051 flight | **PASS**: controls, mount cycling/single-weapon fire, cross visibility/shots, pointer refusal/retry and motion, fullscreen, blur/hidden, same-ship WS resume, full minute, authoritative home dock, logoff/no zombie, stale cookie and actual WebGL context-loss explanation |
| List failure/retry | PASS |
| Correctly signed wrong audience | Refused; clear message; no zombie |
| Correctly signed expired ticket | Refused; clear message; no zombie |
| Ticket failure/retry through a fresh real join | PASS |
| Unexpired ticket replay | Refused; clear message; no zombie |
| Stale mission cookie | Refused; server ship registry unchanged |
| Browser back navigation | PASS; joined session retired |
| Loopback directory listing `ws://10.1.2.3/game` | PASS: “Joining this server is not available in the browser (no web route).”; **0 WebSocket attempts**, no Join button |
| Loopback directory listing public WSS | PASS: “Public joining is not available in the browser.”; **0 WebSocket attempts**, no Join button |

Security inspection covered actual Cache API bytes/URLs, IndexedDB stores,
console and requested URLs; no tested credentials persisted or leaked. The first
full flight attempt passed its gameplay profile but exceeded a 30-second harness
wait during repeated asset staging for the final stale-cookie check. Giving that
**proof staging wait** 240 seconds produced a complete pass. No production deadline
changed. First-launch, reconnect and other gameplay assertions remained intact.

## Load, transfer and hosting

Baseline and final generations use the same recorded desktop traces, local
server/Chromium, viewport and verified complete join payload. Cold means a new
browser context/cache; warm means a second navigation in that context. Shell is
first-contentful-paint, menu is the first completed real Game1 draw, and flight is
the first observed real flight draw. These include UI list/join/side/launch work.
The proof proxy retains **no-cache** HTTP headers; warm pack reuse is Cache API
reuse, not a production CDN claim. The existing shell already had progress;
P1053 adds the static Allegiance title and begins boot preloads after capability
checking, before ordered interop evaluation.

| Modern 1920×1080 | Shell ms before → after | Menu ms before → after | Flight ms before → after |
|---|---:|---:|---:|
| Cold | 128 → 48 | 1,286.1 → 796.8 | 13,649.8 → 12,181.0 |
| Warm | 40 → 36 | 988.3 → 631.7 | 48,669.2 → 49,176.3 |

The cold menu improvement is **38.0%**, meeting the ≥30% target. Warm flight timing includes the normal server readiness/intermission/countdown
wait. It is not a pure asset-cache or download measurement; no claim is made
that those longer timestamps mean cached asset verification is slower than cold
network staging.
The [final timing/pad/pacing receipt](BROWSER_POLISH_P1053_assets/polish-final.json) records the corrected exact-pack cold/warm Modern profile. The separate [layout matrix receipt](BROWSER_POLISH_P1053_assets/layout-matrix.json) covers both styles at both resolutions; its renderer and CSS match the final generation. Native training ran concurrently with the exact-pack before/after timing runs; these are individual observations, not a statistical benchmark. The [baseline timings](BROWSER_POLISH_P1053_assets/load-before.json), [base full flight](BROWSER_POLISH_P1053_assets/base-flight.json), [nine security cases](BROWSER_POLISH_P1053_assets/base-security.json) and [publish-size accounting](BROWSER_POLISH_P1053_assets/publish-sizes.json) are retained separately.

| Published logical payload (excluding `.br`/`.gz` duplicate copies; includes manifests/receipts/notices) | Before | After | Delta |
|---|---:|---:|---:|
| Raw bytes | 51,187,517 | 51,212,898 | +25,381 |
| Conservative per-file gzip bytes | 14,087,882 | 14,098,255 | +10,373 |
| Unique verified pack bytes, raw | 32,906,544 | 32,906,544 | 0 |
| Unique pack files | 573 | 573 | 0 |

The complete conservative gzip payload is **13.45 MiB**, below 20 MiB. Initial
menu staging falls from 29 files to 3; the complete join retains all original
verified bytes. An early reproduction selected `flight-opens2` instead of P1051’s `flight-opens3`; the reported timing, size and final qualification use the exact nine checked-in P1051 traces (596 logical aliases, 573 unique pack files), with before/after join hashes and sizes equal. No per-sector pack system or artwork transformation was added.
Release publication passed with the installed prebuilt runtime; the optional
`wasm-tools` suggestion is informational, not a build warning.

Intended future hosting headers, also documented in [hosting notes](../BROWSER_HOSTING.md) and linked from `docker/README.md`:

| Class | Intended Cache-Control |
|---|---|
| HTML and mutable JSON manifests/receipts | `no-store` |
| Content-addressed application/boot/KNI files, SDK fingerprints, pack hashes, art hashes and notices | `public, max-age=31536000, immutable` |
| Remaining stable-name compatibility files | `no-cache` |

The completed generation hashes 36 additional application files, embeds its
import map in HTML data, and keeps framework integrity hashes intact. No host,
CDN/TLS, CSP, lobby `/play/` or compose service/port/store/env-var change is made.
Docker is unavailable and no Docker execution claim is made.

## Verification

| Check | Passed / Failed / Skipped |
|---|---|
| Solution including browser variants | **0 warnings / 0 errors on final source** |
| `tools/build-tools.sh --drop-bins` | **148 projects**, 0 warnings / 0 errors |
| P1048/P1048B/P1052 selection | **135/0/0 P1048**, **49/0/0 P1048B**; current P1052 plus original seven RWS cases **99/0/0** (Core 27, lobby 52, desktop 10, browser 10) |
| Complete browser game seam project | **59/0/0** |
| Complete browser head project | **12/0/0** |
| New C# gamepad + bounded staging | **8/0/0 ×3 on final source** |
| JS pacing + production input | **14/0/0 ×3** |
| Python publication regression | **4/0/0 ×3** |
| Existing browser cache JS suite | **8/0/0** |
| Browser security with deferred-cancellation coverage | **11/0/0 ×3**, fresh cohorts |
| Release browser publication | PASS; final generation used for Chromium proof |
| Desktop Debug filenames | **753/753 identical**, no added or removed filenames |
| Desktop HUD | **1,848/1,848 passed**, 0 pixel/semantic differences, 82 animation-only regions, 0 corrupt goldens |
| Training Fast + Live, artwork, repeat 3/window proofs once | **1,206/0/2 overall**; all six children passed; **42/42 physical windows passed** |
| Final Fast tier | **15,509/5/6**, 15,520 total, 876.2 s; **red**. Nine infrastructure checks passed; no timing offenders. All five failure methods ran three times alone; four passed all runs, the timer family reproduced twice |
| Glossary / supplemental parity / whitespace | Passed: **990 glossary paths**, supplemental parity and whitespace |

The detachment fixture needed its new pad-controller field initialized because it
intentionally allocates Game1 without running its constructor; its collection and
callback-retirement assertions remain unchanged. The browser publish regression
also covers SDK-fingerprinted ICU files, after an initial smoke test found and
corrected accidental double fingerprinting. The synthetic disconnect event now
includes the standard `gamepad` property expected by KNI. A proof-only async response observer also raced navigation; reading the real ticket response before delivering it keeps the secrecy check inside the harness error/teardown path. A back-to-back proof hit the unchanged join-token HTTP 429 budget; a fresh local fixture was used for the final full flight rerun. No assertion, production deadline, limit or game
rule was relaxed to hide those failures.

All build/restore operations use preinstalled packages, a writable task HTTP cache,
and disabled build servers/node reuse/shared compilation. No packages were added.
No synthetic CPU load is used; the pacing proof uses CDP browser throttling only.

[Sanitized verification accounting](BROWSER_POLISH_P1053_assets/verification.json) and [complete desktop HUD comparison](BROWSER_POLISH_P1053_assets/desktop-hud.json) retain the counts, TRX hashes, publication inventory/manifest fingerprints, filename-manifest hashes and reviewed PNG hashes.

### Training child results

`tools/run-training-tests.sh --no-build --repeat 3 --window-proofs-once`, with artwork, serial client projects and both Fast/Live selections. All six children completed successfully:

```text
Run 1 — Passed!  - Failed:     0, Passed:   131, Skipped:     0, Total:   131, Duration: 9 s - Allegiance.ClientCore.Tests.dll (net10.0)
Run 1 — Passed!  - Failed:     0, Passed:   299, Skipped:     2, Total:   301, Duration: 3 h 59 m - Allegiance.ClientCore.MonoGame.Tests.dll (net10.0)
Run 2 — Passed!  - Failed:     0, Passed:   131, Skipped:     0, Total:   131, Duration: 10 s - Allegiance.ClientCore.Tests.dll (net10.0)
Run 2 — Passed!  - Failed:     0, Passed:   257, Skipped:     0, Total:   257, Duration: 11 m 8 s - Allegiance.ClientCore.MonoGame.Tests.dll (net10.0)
Run 3 — Passed!  - Failed:     0, Passed:   131, Skipped:     0, Total:   131, Duration: 9 s - Allegiance.ClientCore.Tests.dll (net10.0)
Run 3 — Passed!  - Failed:     0, Passed:   257, Skipped:     0, Total:   257, Duration: 11 m 8 s - Allegiance.ClientCore.MonoGame.Tests.dll (net10.0)
```

All **42 physical P912 windows passed**. The two opt-in M4 hunt cases retain their existing skips. Full Classic/Modern Nanite, Modern loss, Classic/Modern sacrifice and both early-mount variants passed. Repeats 2–3 omit only the P912 class under the requested `--window-proofs-once`; every other selected Fast/Live test remains. Overall: **1,206 passed / 0 failed / 2 skipped**. The training command exited **0**.

### Final Fast and isolated failures

The single final sweep ran **last**, after every implementation/build/browser/training gate: `tools/run-tests.sh --tier fast --parallel 1 --no-build`. It exited **1**: **15,509 passed / 5 failed / 6 skipped**, 15,520 tests in **876.2 s**. All **nine infrastructure checks passed**, with **zero timing offenders**, no quarantine retry/flakes, and an unchanged calibrated per-case limit of **11.523 s**. Both browser projects passed **12/0/0** and **59/0/0** within this sweep too.

The only test invocations afterward were the brief-required failure isolations, each method in three fresh processes with artwork:

| Existing family | Sweep failure | Isolated runs, passed/failed/skipped |
|---|---|---|
| Remote death receipt | B never saw A’s death after 2,500 ticks | **1/0/0 ×3** |
| P660 disabled/off countdown | **14.900000000054206** outside **[14.94,15.06]** | **4/0/0 ×3**, all four theory rows |
| Mining at capacity | Unexpected `AsteroidMined` broadcast | **1/0/0 ×3** |
| P739 timer upper bound | Delay 5: **5.0000000000000009 > 5** | **2/1/0, 3/0/0, 2/1/0**; delay 7 failed in run 1 and delay 5 failed in run 3, each one ULP over its upper bound; all three delay rows retained |
| Rejoined-player roster time | **8.14999962** outside **[4,8]** | **1/0/0 ×3** |

These five signatures already occur in the [flake ledger](../TEST_FLAKES.md#p1053-browser-polish-final-fast). The timer boundary remains reproducible alone. No assertion, timeout, tier, quarantine, simulation rule or production limit was changed. **Fast remains red.** [Summary](BROWSER_POLISH_P1053_assets/fast-summary.json), [failures/skips](BROWSER_POLISH_P1053_assets/fast-exceptions.json) and [three isolated repetitions](BROWSER_POLISH_P1053_assets/fast-isolated.json) retain exact methods, rows, timings and TRX hashes.

The six skips are three optional private-art-pack tests, two map-dependent station-gate fixtures, and the packaging-pin test because the supplied artwork corpus is not a Git checkout of that pin. Actual staged file hashes/closure were independently verified; the skipped pin qualification is not counted as passed.

## Images

All **17** selected images were opened and visually reviewed. Total PNG bytes:
**1,679,365** (below both 2 MB and 2 MiB). These are unedited Chromium screenshots;
they do not establish GPU performance. Several immediate after-load images show
the new non-modal slow note at the upper right.

| View | Before | After | What the pair shows |
|---|---|---|---|
| Classic 800×480 hangar | [Before](BROWSER_POLISH_P1053_assets/before-docked-Classic-800x480.png) | [After](BROWSER_POLISH_P1053_assets/after-docked-Classic-800x480.png) | The central HTML panel covers most native hangar text before; all four native lines and money are visible above a separate bottom rail after. |
| Modern 800×480 hangar | [Before](BROWSER_POLISH_P1053_assets/before-docked-Modern-800x480.png) | [After](BROWSER_POLISH_P1053_assets/after-docked-Modern-800x480.png) | Same panel obstruction and its removal, with Modern selected. |
| Classic 1920×1080 hangar | [Before](BROWSER_POLISH_P1053_assets/before-docked-Classic-1920x1080.png) | [After](BROWSER_POLISH_P1053_assets/after-docked-Classic-1920x1080.png) | Native hangar text is already visible at the left; the large central HTML panel becomes a compact bottom rail. |
| Modern 1920×1080 hangar | [Before](BROWSER_POLISH_P1053_assets/before-docked-Modern-1920x1080.png) | [After](BROWSER_POLISH_P1053_assets/after-docked-Modern-1920x1080.png) | The same rail placement, with Modern selected. |
| Classic 800×480 flight | [Before](BROWSER_POLISH_P1053_assets/before-flight-Classic-800x480.png) | [After](BROWSER_POLISH_P1053_assets/after-flight-Classic-800x480.png) | Before: lower HUD/CHASE text lies beneath the toolbar. After: centre/lower gauges, speed and CHASE fit above the rail. |
| Modern 800×480 flight | [Before](BROWSER_POLISH_P1053_assets/before-flight-Modern-800x480.png) | [After](BROWSER_POLISH_P1053_assets/after-flight-Modern-800x480.png) | Tactical/radar/CHASE labels are exposed above the rail after; the scene and Modern gauges remain visible. |
| Classic 1920×1080 flight | [Before](BROWSER_POLISH_P1053_assets/before-flight-Classic-1920x1080.png) | [After](BROWSER_POLISH_P1053_assets/after-flight-Classic-1920x1080.png) | Classic centre gauges and the lower-left CHASE label sit inside the canvas, above separate HTML buttons. |
| Modern 1920×1080 flight | [Before](BROWSER_POLISH_P1053_assets/before-flight-Modern-1920x1080.png) | [After](BROWSER_POLISH_P1053_assets/after-flight-Modern-1920x1080.png) | Before, Mission briefing obscures the left status area; after, HUD Tactical, RADAR Default and CHASE are all visible above the button rail. |


The [additional Classic nearby-ship capture](BROWSER_POLISH_P1053_assets/after-nearby-Classic-800x480.png)
was also opened and reviewed: the blue **DesktopPilot** label and **94 m** range
are fully above the toolbar. The original
[P1051 capture](BROWSER_FLIGHT_P1051_assets/flight-ships-classic-800x480.png)
provides the reported pre-fix reference for that label obstruction. Both clients
flew normally; no server pose was injected. The final [full flight receipt](BROWSER_POLISH_P1053_assets/final-flight.json)
and [eleven-case security receipt](BROWSER_POLISH_P1053_assets/final-security.json)
qualify the updated generation too. The two added deferred-startup cancellation cases delayed the join-manifest read and Brotli import: both ended with **0 tickets, 0 WebSocket attempts and no zombie**.

## Unfinished / decisions

Items 0–4 are complete. Browser qualifications, Release publication, 0-warning/error builds, new repeated tests, desktop filename comparison, the 1,848-case HUD comparison and all six training children passed. The final Fast sweep remains **15,509/5/6, red**, for the five existing families listed above. All **15 fresh isolated processes** completed (**28 passed / 2 failed / 0 skipped** test rows); the timer upper bound remains reproducible. No behavior decision for Domingo is needed.

All owned proof hosts, Chromium contexts, desktop/test children and tool sessions ended. Proof HTTP/lobby ports **8048/8049 are closed**. The standard fixtures’ scratch directories were collected under `<lane evidence dir>/gate-artifacts/` after completion; raw evidence was preserved. Process accounting is retained in the verification receipt. No commits or pushes were made; changes remain in the working tree.

Native intent / Modern approach: preserve the shared native controls, simulation,
authority and cadence; use browser-owned gestures, lifecycle release, pacing,
content-addressed startup and measured chrome insets for presentation.

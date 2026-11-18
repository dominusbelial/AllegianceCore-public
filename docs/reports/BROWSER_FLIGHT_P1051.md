# Browser flight — P1051

Status: **items 0–5 implemented and browser-proved**. All requested gates were run. Training retained two known M2 reminder failures; the final Fast sweep retained three existing failure signatures, each passing three isolated repetitions. These two gates remain **red**, with exact results below. No commits, pushes, deployment or external network access. Changes are left in the working tree; the brief is excluded from it. All proof servers/proxies/browser sessions and test processes exited or were stopped using their owned handles/stop files.

## Item status

| Item | Status |
|---|---|
| 0 — WASM mission briefing | **Complete.** Runtime and browser alternatives measured; the installed MIT runtime Brotli decoder runs in resident WASM modules. All three real local-server frames are byte-equal to raw desktop BrotliStream output in Chromium; native generated briefing text is visible. Node decoder tests: 29 cases / 35 checks ×3. |
| 1 — Shared Game1 flight | **Complete.** Shared manual sampling, prediction/clock/cadence, catalogue/mount lookup, docking geometry/steering and eject helpers compile into the browser; native LDR renderers and both HUDs consume the shared model. Solution 0/0; desktop 753 filenames identical and 1,848 HUD cases have zero pixel/semantic delta outside animation masks. |
| 2 — Recorded join pack and persistent cache | **Complete.** Actual 60-second desktop traces, verified dependency closure and explicit tested rock-only surface caps. Assets + framework: 13,813,274 bytes; stricter all-application-files + assets: **13,990,271 bytes (13.34 MiB)**, under 20 MiB. Cold, warm (zero pack bytes), one stale file, obsolete-file purge, storage refusal and same-page retry passed before fresh tickets. |
| 3 — Flight, browser input, both HUD styles | **Complete.** Classic/Modern at 1920×1080 and 800×480: native scene and HUD, accepted keyboard/throttle/mouse controls, single-weapon firing, authoritative prediction/dock pose, gesture-owned capture/fullscreen/audio, blur/hidden release. Actual WebGL loss/reload explanation passed in the final isolated cohort. Physical mouse calibration remains unqualified in headless CDP; relative DOM steering under real capture is qualified. |
| 4 — Two clients, dock/rejoin and reconnect | **Complete.** Normally authenticated headless desktop and browser on opposing sides: cross visibility and shots in both directions, home docking, deliberate LOGOFF without a zombie, return and fresh rejoin. Each profile survives exactly one forced WS resume on the frozen joined endpoint through a full minute. Stale mission-cookie refusal preserves the server ship registry. |
| 5 — Optional ticket hardening | **Complete: 7/7 Chromium cases.** Correctly signed wrong-audience/expired tickets and an unexpired replay are refused clearly; list/ticket failure retry, stale mission cookie and browser-back cleanup pass. Actual Cache API, IndexedDB, URLs and console inspected for tested secrets. No production policy/limit change. |

## Measurements and changes

Wire layouts, server simulation/game rules, core assets, lobby limits and production hosting are unchanged. Successful resume now completes the existing authenticated transport bookkeeping. The loopback proof uses the normal verified-server enrollment, passkey identity, ticket, team-balance and visibility checks. No network requests leave loopback. No packages were downloaded; offline restores use the preinstalled package folder with the HTTP cache redirected into the ignored lane evidence directory.

| Source seam | Change and reason |
|---|---|
| `src/Allegiance.ClientCore.Game/Game1.ManualFlight.cs:11`, `Game1.FlightInput.cs` | Extract the existing manual `FlightInputState` construction unchanged; desktop retains its existing training overrides. |
| `src/Allegiance.ClientCore.Game/Game1.Prediction.cs:85,245,291,380` | Source-share own seeding/reset, clock, native 0.15-second emission cadence and remote hull/side GA resolution. The browser passes the same elapsed-time input to this simulation, without an extra platform clamp. The last method moved unchanged from Rendering. |
| `Game1.PartLookup.cs`, `Game1.DockApproach.cs`, `Game1.FlightTargets.cs`, `Game1.AutopilotEngage.cs` | Move existing methods/types, including the hull’s fixed weapon-mount count, into selectable partials. Real hull/weapon data, allied dockability, real CVH landing bays and existing route steering remain shared. Desktop engagement stays in its desktop-only partial. |
| `src/Allegiance.ClientCore.Game.Browser/Game1.Flight.cs:77,100,143,162,225,253` | Attach an owned session, sample DOM keys through the same ActionMap/FlightInput, release input, run prediction, and draw native LDR scene/HUD objects. Detachment first releases the world’s callbacks so late packets can drain without calling a detached renderer. The native mouse response, roll modifier and autopilot disengage threshold are reused. Classic and Modern consume one `HudFrame`/contact model. |
| `src/Allegiance.ClientCore.Browser/Pages/Menu.Online.cs:60,124,147,187` | Stage/merge a complete verified join snapshot before requesting a ticket; display the native briefing; launch through the existing no-purchase desktop U/DOCKED path; await voluntary leave. |
| `src/Allegiance.ClientCore.Game.Browser/Session/ConnectionSession.Leave.cs:8` | Send the native LOGOFF and asynchronously let the server close before retiring exactly the captured owner. Immediate WS disposal could otherwise cancel the queued LOGOFF and park a deliberately departed ship. |
| `src/Allegiance.ClientCore/Session/ConnectionSession.cs:60,1645,1873` | Browser-only injected briefing decoder. Desktop keeps its existing managed decoder. Browser resume also retains the initial canonical callsign instead of treating the server's resume greeting as a new name. |
| `src/Allegiance.Server/Server.ReconnectGrace.cs:311` | A validated parked-seat resume now clears both pre-logon deadlines and restores the existing human traffic class, exactly as fresh validated logon does. The 60-second deadline and refused-resume checks remain unchanged. |
| `Assets/AssetStager.cs`, `BrowserAssetCache.cs`, `StagedAssetSource.cs`, `game-platform.js` | Manifest retention across starter/join, complete atomic resident merge, per-file verification/progress/retry, purge and shared visit-memory fallback after refused writes. |
| `flight-input.js:1`, `menu.js:4` | Install DOM gesture handlers before the KNI game starts; explicit pointer intent, refused-request retry on a fresh gesture, fullscreen, focus/visibility release and AudioContext lifecycle. |
| `tools/p1051-flight-opens`, `tools/p1051-browser-packs.py`, `scripts/publish-browser-flight.sh` | Record real desktop opens, verify original source hashes, compute the first-sector closure and publish a new completed generation atomically. |

The P1048/P897 async session ownership is reused: `ConnectionSession.cs:540` uses `Task.Delay` for keepalive rather than the desktop thread at `:561`; `ConnectionSession.Ownership.cs:29` schedules on the event loop rather than `Task.Run`. Its owner/deadline checks and transport retirement remain authoritative. The desktop keepalive rule mirrored here is `:561–589`: six-second pings, more than five unanswered and at least thirty seconds without server traffic (native `clintlib.cpp:2050–2087`). DOM Backslash/IntlBackslash reach the existing full-throttle bindings; both bracket directions cycle via the shared hull fixed-mount count (`Game1.PartLookup.cs:44`) and `FlightInput.NextMount`/`PrevMount`. The browser proof fires the real gun in single-weapon mode after cycling. No browser wait blocks a thread. The synchronous decoder does computation on already resident bytes and preserves receive-message order.

Native rules are reused, with citations retained in their shared source and tracked in `docs/parity/p1051-browser-flight.csv` as **n/a / no additional parity credit**. The default-off reconnect extension is modern lifecycle work, not a native rule claim. Fixed-mount cycling: `clintlib.cpp:1935–1975`; controls and pose cadence: `WinTrek.cpp:8270–8271`, `clintlib.cpp:1815–1866`; flight integration: `shipIGC.cpp:2885–3037`; own/allied landing: `shipIGC.cpp:732–735,843–855`; canonical name: `appmsg.cpp:39–40`; voluntary logoff: `FedSrv.CPP:5787–5802`. DOM gestures, cache and Brotli interop are platform mechanisms, not invented native game rules.

## Browser seams and exclusions

The only compile selection change is adding shared `Game1.Prediction`, `ManualFlight`, `PartLookup`, `DockApproach`, `FlightTargets` and `Eject` to the existing Intro/Options set (`Allegiance.ClientCore.Game.Browser.csproj:77`). The new browser lifecycle partial supplies their platform fields and orchestration. `Game1.AutopilotEngage` remains desktop-only because desktop engagement also cancels its training/command state. No new exclusion directory was added.

The existing exclusions remain explicit in the csproj: other Game1 orchestration (desktop lobby/offline/training and native window lifecycle); `Discovery` (UDP); diagnostics/capture/replay (desktop files/processes); `Training`, skirmish and HostLan (not this browser slice); native OpenAL audio/voice (`SoundManager`, `Music`, `AudioDevice`, `AsrSound`, `AudioVoice`, `TrainingVoicePcm`); `RelativeMouse` (SDL; DOM pointer lock replaces it); native filesystem asset/profile/password and saved LAN helpers; desktop command fanout/order resolvers and equipment-swap input; desktop lobby listing/update helpers; GameOver performance/screen and custom postprocessing/upscaling. These are retained P1047 exclusions, not new disabled gameplay rules.

`NativeShipRenderer`, `NativeStationRenderer`, `NativeAsteroidRenderer`, `NativeSkyRenderer`, `WarpRenderer`, `ProjectileRenderer`, both HUD renderers, radar projection and native fonts are source-shared. Custom effects/HDR/bloom/FSR stay off: KNI WebGL LDR has no qualified translation of the desktop custom shaders (P1047 constraint). Ship HD texture substitution is off; staged native geometry/textures are used. Flight audio mixing remains a desktop native-audio exclusion; menu sound and all browser AudioContexts obey the gesture/visibility boundary. No claim is made for native flight sound coverage.

The browser is scoped to this recorded first sector, current-ship launch, ordinary movement/turn/fire, nearby-ship view and home docking. Full equipment shopping, command panes, desktop offline modes, cross-sector asset streaming, large matches and other browsers remain outside this slice.

## Pack and decoder budget

The lane publishes with its installed prebuilt browser runtime; `wasm-tools` native relinking is unavailable (the publish notice is informational, not a warning). Runtime BrotliDecoder and BrotliStream both throw `PlatformNotSupportedException: IOCompressionBrotli_PlatformNotSupported` in the published net10 WASM build. Chromium 153 rejects both `DecompressionStream("br")` and `("brotli")`, while gzip works. The installed browser Brotli assembly marks these APIs unsupported and the shipped dotnet.native WASM exports no Brotli decoder. No new package selected. The loopback-only constraint is satisfied by reusing the installed runtime object files, with no new package or external network. The adapter strips debug/linking sections and relocates only memory and allocator callback entries; it preserves each module's original function/type/global indices. Unsupported relocation kinds fail closed. Initial module size: **176,773 raw / 80,119 gzip bytes**, before JS/managed glue. Production output is capped at 4 MiB and WASM memory at 64 MiB; rejection resets its temporary arena and stack.

The chosen decoder adds **176,773 raw / 80,119 gzip bytes** of ten maintained runtime object modules plus **3,119 raw / 1,321 gzip bytes** of JS glue (**179,892 / 81,440 bytes** total). Its modules come from `Microsoft.NETCore.App.Runtime.Mono.browser-wasm` **10.0.15** already installed on the lane, specifically `libbrotlicommon.a` and `libbrotlidec.a`; Google Brotli is MIT, with its complete notice in the published `THIRD-PARTY-NOTICES.txt` Brotli section. `tools/p1051-brotli-runtime.py --native-dir <runtime native directory> --output <browser wwwroot>/brotli` reproduces the modules. This is the runtime's real C decoder, with a small relocator/allocator adapter, not a new Brotli implementation. No separate managed decoder package was added.

Fresh Release comparison, excluding `.br`/`.gz` duplicates and asset/receipt manifests: runtime `_framework` **77 files, 16,997,697 → 17,054,017 raw**, **6,058,970 → 6,084,697 gzip**; all non-pack published files **17,456,155 → 17,700,329 raw**, **6,151,546 → 6,261,694 gzip**. The latter delta (**244,174 raw / 110,148 gzip**) includes flight/input/UI and loose decoder copies, not just decoder interop. Pack transfers use their hash paths; loose decoder copies are not separately requested.

| Pack component | Source names / unique files | Raw bytes | gzip bytes |
|---|---:|---:|---:|
| Starter including decoder | 29 / 29 | 2,738,014 | 344,744 |
| Additional join files | 567 / 544 | 30,168,530 | 7,383,833 |
| Complete verified resident assets | 596 / 573 | 32,906,544 | 7,728,577 |
| Runtime `_framework` | 77 | 17,054,017 | 6,084,697 |
| Runtime `_framework` + complete assets | | 49,960,561 | **13,813,274 (13.17 MiB)** |
| All non-pack application files + complete assets (includes loose decoder copies) | | 50,606,873 | **13,990,271 (13.34 MiB)** |
| Transfer budget | | | **20,971,520 (20 MiB)** |

The resident source includes source-name aliases (**33,023,470 bytes** in browser metrics); transfer accounting deduplicates the **573** hash payloads. The stricter all-application-files total also fits the budget, including decoder copies never requested separately. The pack arithmetic is conservative gzip of every framework file plus each distinct asset hash, even framework files the browser does not request. Browser network transfer including HTTP headers is measured separately below. The native recorded closure without the explicit rock surface cap would be **32,343,450 gzip bytes** including runtime, **11,371,930 bytes over budget**. Every changed surface was 2048×2048 RGB565; only point-sampled surface pixels/dimensions change to 256×256. Geometry, UVs, collisions, CVH, hulls, weapons, ranges and core bytes remain original, verified against desktop open size/hash. The pack builder independently compares every output RGB565 pixel to the selected original pixel.

| Explicit surface fallback | Original raw | Original gzip | Capped raw | Capped gzip |
|---|---:|---:|---:|---:|
| bgrnd03bmp.mdl | 8,388,724 | 2,364,955 | 131,188 | 51,253 |
| bgrnd05bmp.mdl | 8,388,724 | 1,724,094 | 131,188 | 39,777 |
| bgrnd51bmp.mdl | 8,388,724 | 2,567,505 | 131,188 | 56,330 |
| bgrnd52bmp.mdl | 8,388,724 | 3,532,547 | 131,188 | 70,653 |
| bgrnd53bmp.mdl | 8,388,724 | 3,489,230 | 131,188 | 69,907 |
| bgrnd55bmp.mdl | 8,388,724 | 3,025,660 | 131,188 | 58,747 |
| bgrnd56bmp.mdl | 8,388,724 | 2,220,145 | 131,188 | 47,293 |

Four 60-second **wall-clock** Classic/Modern desktop recordings, a 60-second opposing-side trace and two 60-second native-miner traces supply the observed closure and common contact/rock data. Final entry sector is native Ceto, seed=1051. Older Ceto traces also contribute observed surface variants. The two normal-miner recordings verify `dminr`/`dminrbmp` and `wc_gtmnr`/`utl401bmp` for NPCs appearing after a match rotation; all four files are original and actually opened, not placeholders. The opposing-side Quaoar background (`environment5bmp`, `globe5`, `nebplnt03bmp`) is explicitly deferred because this pack covers the browser's Ceto entry sector; geometry/common files remain included. Initial accelerated GameTime-only recordings were discarded and are not counted as 60-second evidence.

Cache API stores immutable bytes because it accepts streamed fetch responses and hash-addressed same-origin requests directly; IndexedDB stores the small manifest/LRU metadata and preferences. Startup retains all current join hashes while staging the starter, preventing warm-cache eviction of the deferred pack. Each staged cached file is checked for exact size and SHA256 before entering the resident source; only a missing/stale hash is fetched. Manifest commit purges obsolete hashes. A refused write logs the filename and retains bytes in a shared visit-memory dictionary so retry reuses preceding completed files. Ticket minting occurs only after complete staging, so a failed file has no ticket to expire. “Warm zero transfer” refers to immutable pack files: the unchanged loopback proof proxy still refetches the application/framework on navigation, measured separately in the HTTP totals.

## Verification

Baseline solution build passed with **0 warnings / 0 errors** after offline restore from the preinstalled NuGet folder. Baseline HUD capture completed **1,848/1,848 cases** with artwork before editing shared sources; comparison passed: **1,848 cases, 0 pixel differences outside the fixture's animation masks, 0 semantic differences, 0 corrupt goldens** (79 animation-only regions differed in the final comparison; 84 in the earlier comparison). Desktop Debug filenames are **753/753 identical**. Browser flight library build passed **0/0**.

Final solution build passed **0 warnings / 0 errors**, including both browser projects. P897 transport/UI selection passed **774/774**; Core P1048/P1048B selection passed **184/184**; the combined final run also included **16** P813 reconnect-grace cases and the new authenticated-resume test (**201/201** total). Browser tests passed **33/33**. New C# selections passed alone three times each: staging **3/3**, successive resume/canonical identity **1/1**, callback detachment/collection **1/1**, physical DOM key → desktop ActionMap **5/5**, and real-WS server authentication **1/1**; production JS input tests **5/5 × 3** and runtime Brotli tests **29 cases / 35 checks × 3** passed. Release browser publish passed. All **147 tool projects** built 0/0. Glossary validation passed **971 paths**, the default parity matrices and five supplemental n/a rows validate, and working-tree hygiene/media hashes pass. Training completed with the two ledgered reminder failures detailed below. The final Fast sweep completed with three existing failure signatures, all passing three isolated repetitions; its result remains red. Core simulation/data sources are unchanged. Core transport adds browser-only disconnect diagnostics; server Native AOT publish passed with zero trim/AOT warnings. Server resume bookkeeping also has a real-WS regression test, passed 1/1 × 3.

## Training qualification

`tools/run-training-tests.sh --no-build --repeat 3` completed with artwork, offscreen SDL and null OpenAL, retaining serial project/case settings and the Fast+Live selection. Overall: **1,189 passed / 2 failed / 2 skipped**. The command exits 1 because of the two known reminder failures; this gate is **not green**. The [bounded training receipt](BROWSER_FLIGHT_P1051_assets/training-summary.json) records each TRX hash, failure signature, P912 outcome and Nanite result.

| Repeat | Child | Passed | Failed | Skipped | Result |
|---|---|---:|---:|---:|---|
| 1 | Allegiance.ClientCore.Tests | 126 | 0 | 0 | PASS |
| 1 | Allegiance.ClientCore.MonoGame.Tests | 297 | 2 | 2 | FAIL |
| 2 | Allegiance.ClientCore.Tests | 126 | 0 | 0 | PASS |
| 2 | Allegiance.ClientCore.MonoGame.Tests | 257 | 0 | 0 | PASS |
| 3 | Allegiance.ClientCore.Tests | 126 | 0 | 0 | PASS |
| 3 | Allegiance.ClientCore.MonoGame.Tests | 257 | 0 | 0 | PASS |

Exact child result lines:

```text
Run 1 / Allegiance.ClientCore.Tests
Passed!  - Failed:     0, Passed:   126, Skipped:     0, Total:   126, Duration: 10 s - Allegiance.ClientCore.Tests.dll (net10.0)
Run 1 / Allegiance.ClientCore.MonoGame.Tests
Failed!  - Failed:     2, Passed:   297, Skipped:     2, Total:   301, Duration: 3 h 59 m - Allegiance.ClientCore.MonoGame.Tests.dll (net10.0)
Run 2 / Allegiance.ClientCore.Tests
Passed!  - Failed:     0, Passed:   126, Skipped:     0, Total:   126, Duration: 10 s - Allegiance.ClientCore.Tests.dll (net10.0)
Run 2 / Allegiance.ClientCore.MonoGame.Tests
Passed!  - Failed:     0, Passed:   257, Skipped:     0, Total:   257, Duration: 11 m 9 s - Allegiance.ClientCore.MonoGame.Tests.dll (net10.0)
Run 3 / Allegiance.ClientCore.Tests
Passed!  - Failed:     0, Passed:   126, Skipped:     0, Total:   126, Duration: 10 s - Allegiance.ClientCore.Tests.dll (net10.0)
Run 3 / Allegiance.ClientCore.MonoGame.Tests
Passed!  - Failed:     0, Passed:   257, Skipped:     0, Total:   257, Duration: 11 m 10 s - Allegiance.ClientCore.MonoGame.Tests.dll (net10.0)
```

All **42 physical P912 windows** ran once: **40 passed / 2 known reminder failures**. The two opt-in M4 hunt cases skipped before opening a window. Full, loss/sacrifice and early-mount Nanite windows passed for their selected styles; all other selected training tests remained in each repeat.

The two first-repeat failures are the existing `P912TrainingWindowTests.Basic_flight_completes_with_muted_missing_or_repeated_narration` cases, Classic/Modern with policy `reminders`: **“M2 9.2: native prompts missing or out of order”** after mission completion/docking. The [flake ledger](../TEST_FLAKES.md) already records this exact signature, including prior isolated and untouched-base evidence; this lane is not claiming a new base-control run. No lesson, narration rule, assertion or timing limit was changed.

The brief permits the long real-window proofs once. An ignored DOTNET wrapper appends **`FullyQualifiedName!~P912TrainingWindowTests` only for results paths in repeats 2–3**; repeat 1 retains every assigned prefix and full/early Nanite window. The repository training script is unchanged. All other selected Fast+Live training tests remain in every repeat. All six child lines and final counts are retained above. The repository script and assertions are unchanged.

## Final Fast qualification

The **single final Fast sweep** ran after all implementation, browser proofs, builds and training children: `tools/run-tests.sh --tier fast --no-build --parallel 1`. It completed **15,391 tests in 861.8 s: 15,377 passed / 3 failed / 11 skipped**. All nine infrastructure checks passed. Calibrated per-test limit **11.526 s**; **zero timing offenders**, zero quarantine retries/flakes. The sweep exits 1 and is **not green**.

[Fast summary](BROWSER_FLIGHT_P1051_assets/fast-summary.json), [failures/skips](BROWSER_FLIGHT_P1051_assets/fast-exceptions.json) and [three isolated repetitions](BROWSER_FLIGHT_P1051_assets/fast-isolated.json) retain bounded results and TRX hashes. These are the only test invocations after the final sweep: the brief-required isolated repetitions of its failures.

| Existing failure family | Sweep observation | Isolated repetitions, passed/failed/skipped |
|---|---|---|
| `P660NextMatchLiveTests.Late_game_over_rebuild_ready_countdown_started_has_no_clock_jump` (`enabled=False`, `dilation=off`) | **14.900000000054206** outside **[14.94, 15.06]** | **4/0/0 ×3**, all four theory rows each time |
| `P739TimerLiveTests.Warp_bomb_notice_matches_captured_blast_deadline_and_stays_in_mission` (`delay=5`) | **5.0000000000000009** above inclusive **5** | **3/0/0 ×3**, delay 5/7/10 together each time |
| `NpcWingmanLeadLiveTests.Wingman_fire_gate_leads_a_laterally_moving_enemy` | Raw-aligned/not-lead-aligned refusal discriminant absent | **1/0/0 ×3** |

All three signatures already occur in the [flake ledger](../TEST_FLAKES.md); P1051 adds this lane’s observed sweep and isolated outcomes. Countdown/timer and AI fire-gate code are unchanged. The 11 skips comprise five existing RWS review findings, two map-dependent station-gate fixtures, three optional private-art-pack cases, and the packaging-pin test because the supplied artwork corpus is not a Git checkout of that pin. The independently verified recorded asset hashes/closure and all P1051 checks passed; the skipped packaging-pin test is not claimed as a pass.

## Reproduction and receipts

[Verification counts](BROWSER_FLIGHT_P1051_assets/verification-summary.json), [decoder capability](BROWSER_FLIGHT_P1051_assets/decoder-baseline.json), [module reproduction hashes](BROWSER_FLIGHT_P1051_assets/decoder-reproduction.json), [join receipt](BROWSER_FLIGHT_P1051_assets/join-receipt.json), [publish size comparison](BROWSER_FLIGHT_P1051_assets/publish-size-summary.json) and [nine desktop opening traces](BROWSER_FLIGHT_P1051_assets/desktop-opens.json.gz) retain reviewable measurements without personal paths or signing material. Three `.bin` briefing frames and their raw desktop `.desktop.json` outputs are beside them. The Classic/Modern frames share one briefing content; the opposing-side frame supplies a second distinct content. Classic/Modern decoded to 335 bytes each; the opposing-side payload decoded to 343 bytes. Each is byte-equal in the actual Chromium WASM decoder and the desktop BrotliStream, also independently checked with Node Brotli.

Use the installed SDK and corpus, with `NUGET_HTTP_CACHE_PATH` redirected to a writable ignored directory before builds/tests. The tools accept explicit evidence paths; there is no new production service, port or environment variable. Docker is unavailable in this lane; compose files are unchanged.

```sh
export DOTNET="$HOME/.dotnet/dotnet"
export NUGET_HTTP_CACHE_PATH="$PWD/.scratch/p1051/nuget-http"
export ALLEGIANCE_ARTWORK_PATH=/srv/corpus/Artwork
# Build solution and tools before collecting real-window traces.
# Recorder: Classic|Modern OUTPUT.json [SIDE] [MINERS]
"$DOTNET" tools/p1051-flight-opens/bin/Debug/net10.0/p1051-flight-opens.dll \
  Classic .scratch/p1051/flight-recording/Classic.json 0 true
# Run Modern/side 0, plus both native-miner sides, serially with offscreen SDL.
# The nine exact inputs used here are in desktop-opens.json.gz; extract its
# relative-keyed JSON entries into a scratch directory to rebuild this closure.
scripts/publish-browser-flight.sh .scratch/p1051/new-generation \
  .scratch/p1051/opens/Classic.json .scratch/p1051/opens/Modern.json \
  .scratch/p1051/flight-opens4/Classic.json .scratch/p1051/flight-opens4/Modern.json \
  .scratch/p1051/flight-opens3/Classic.json .scratch/p1051/flight-opens3/Modern.json \
  .scratch/p1051/flight-opens-enemy/Modern.json \
  .scratch/p1051/flight-miners0/Classic.json .scratch/p1051/flight-miners1/Modern.json
node tools/p1051-brotli-tests.cjs docs/reports/BROWSER_FLIGHT_P1051_assets
node --test tools/p1051-input-tests.cjs
"$DOTNET" tools/p1051-browser-host/bin/Debug/net10.0/p1051-browser-host.dll \
  "$ALLEGIANCE_ARTWORK_PATH" .scratch/p1051/new-generation .scratch/p1051/proof-host
# While that host owns loopback 8048/8049, in another shell:
node tools/p1051-browser-flight.cjs <preinstalled-playwright-npm-directory> \
  .scratch/p1051/proof-host .scratch/p1051/proof \
  docs/reports/BROWSER_FLIGHT_P1051_assets .scratch/p1051/new-generation
node tools/p1051-browser-security.cjs <preinstalled-playwright-npm-directory> \
  .scratch/p1051/proof-host .scratch/p1051/security
# Stop this exact proof host and its child proxy using its stop file.
touch .scratch/p1051/proof-host/stop
```

The publish script is required: plain `dotnet publish` produces the application but does not generate the recorded join pack. It writes into a temporary generation and moves the completed wwwroot only after both manifests and packs succeed. The lane's headless Chromium also needs its preinstalled compatible shared libraries and a writable fontconfig configuration pointing at installed DejaVu fonts; those machine-specific scratch paths are deliberately absent from production sources.

## Two-client and browser lifecycle proof

The Linux proof keeps its generated P-256 private signing material in an anonymous `memfd`; the production path-based signer imports it normally. Only the public verification key is persisted in ignored scratch. Superseded scratch private files from initial fixture runs were removed.

The fixture uses the production lobby app and enrollment/signer, a normally authenticated headless desktop `ConnectionSession`, and the browser's passkey UI. The desktop joins the opposing side because a same-side second pilot is correctly refused by TeamBalance. It follows the shipped map as a chart, crosses real warps with ordinary controls, reuses the existing client Dodge feed against visible rocks/stations/ships, and approaches the browser pose actually visible in its client world. A rehearsal’s fourth-case desktop pilot hit asteroid 57 before reaching its image rendezvous; the fixture now uses that existing avoidance feed rather than weakening collisions. It never moves a server actor, grants hardware, changes a limit, injects a packet into the server world or bypasses scanner visibility.

The native server-assigned scout and gun are retained through the same no-purchase U/DOCKED launch used by desktop. Re-buying that hardware was rejected by the normal station tech gate in the first smoke test; this was corrected in the client flow, without changing the gate.

The long first proof exposed two resume problems: the client treated the server’s “Welcome back …” greeting as a new callsign, and the server left the successfully resumed socket in its pre-logon phase. The browser now retains its initial canonical callsign; the server retires both existing pre-logon timers and marks the accepted peer’s normal authenticated/human traffic class. A new real-WS test survives both shortened configured deadlines; the browser harness requires exactly one forced resume across its full minute of flight. Voluntary departure flushes LOGOFF before disposing the captured owner, and late packets no longer retain/call a detached renderer. The per-world projectile receipt counter resets during resume; the proof records the actual launcher IDs and accumulated native draw count before the forced drop, and checks the current peer’s shot on both clients. A zero prediction-correction count is healthy: docking must still end with seeded clock/prediction and exact equality to the authoritative docked pose.

Pointer lock is requested/refused/retried through actual trusted Chromium gestures. A plain-canvas probe showed headless CDP absolute mouse moves arrive as equal-and-opposite relative recenter pairs. The steering assertion therefore dispatches a standard relative `MouseEvent` **under the real trusted capture**, then checks accepted server yaw; physical mouse calibration is not qualified. Alt+Enter uses actual page key events. Blur/hidden events are synthetic, with real held keys, server controls and native AudioContexts; capture is released, server input becomes neutral, hidden audio suspends and visible audio resumes. Canvas context-menu containment and actual `WEBGL_lose_context` reload handling are exercised.

The report captures use ordinary native external-camera placement, with a “Rotate view” control over six free external directions. The harness selects a quiet-sky angle for the strict PNG budget. It does not edit screenshot pixels. Two 800×480 chase captures are also published to show the larger own scout and native planetary backdrop; the 1920×1080 chase captures remain supplementary scratch evidence. The external-camera captures show both ships, rocks and the HUD together.

## Optional Slice-4 hardening

The Chromium hardening harness passed **seven cases**: failed list → return/retry; correctly signed wrong-audience and expired tickets → clear refusal; ticket-fetch failure → retry with a fresh ticket; replay of a still-unexpired used ticket → refusal; wrong/stale mission cookie → refusal with the ship registry unchanged; and browser back-navigation from the joined side-selection state → cleanup. It inspected **573 actual Cache API files (32,906,544 bytes)**, all of the app’s IndexedDB stores, URLs and console messages against the tested bearer/ticket/cookie values. None were found. Production rate limits, CSRF, Origin policy, proxy and body caps are unchanged. A first harness attempt exposed a response-body/navigation race in the test; it now drains ticket-body reads before navigating.

## Visual evidence and performance

The combined Chromium run recorded complete cold/warm/stale results, then ended with **SIGTERM (143)** after the fourth dock capture and before its final receipt; the cause is not established. Storage refusal **passed completely in an independent rerun against the identical publish**. The [combined receipt](BROWSER_FLIGHT_P1051_assets/browser-proof.json) identifies both cohorts and retains [the three-case receipt](BROWSER_FLIGHT_P1051_assets/browser-flight-cohort.json) and [the isolated receipt](BROWSER_FLIGHT_P1051_assets/browser-storage-cohort.json). The interrupted fourth result/captures are not counted or published. Global stale-cookie, context-loss and final log/URL assertions ran in the completed isolated cohort; the separate [seven-case hardening receipt](BROWSER_FLIGHT_P1051_assets/security-proof.json) also covers full storage/log/URL inspection. The [publish fingerprint](BROWSER_FLIGHT_P1051_assets/publish-fingerprint.json) identifies the shared application generation.

Chromium **153.0.8010.0**, headless software rendering; these are **not GPU-representative** numbers. Required training validation ran concurrently. HTTP bytes are Chromium CDP encoded transfer lengths, including headers where reported; the intercepted 503 is a fault fixture. Pack budget arithmetic above counts every framework file, while the browser requests a smaller subset.

The flight timestamp is observed after the UI reaches Flight and more than three native draw frames have run: a conservative upper bound on the first drawable flight frame. Navigation time includes login-to-game UI automation/staging; launch time includes the server’s ordinary readiness/intermission/countdown. Warm/stale launch waits are therefore longer despite minimal pack transfer.

| Profile / cache | Pack requests | Pack transfer B | Framework transfer B | All HTTP to flight B | Navigation → flight s | Launch → flight s |
|---|---:|---:|---:|---:|---:|---:|
| Classic 1920×1080 / cold | 573 | 7,981,028 | 4,433,952 | 12,699,908 | 16.48 | 2.78 |
| Modern 1920×1080 / warm | 0 | 0 | 4,433,952 | 4,718,880 | 49.40 | 39.80 |
| Classic 800×480 / stale | 1 | 7,706 | 4,433,952 | 4,726,586 | 49.08 | 40.95 |
| Modern 800×480 / storage-refused | 574 | 7,981,028 | 4,433,952 | 12,699,908 | 11.95 | 2.55 |

| Profile | Measured flight window s | RAF intervals | p50 ms | p95 ms |
|---|---:|---:|---:|---:|
| Classic 1920×1080 | 60.014 | 2,494 | 16.7 | 33.4 |
| Modern 1920×1080 | 60.031 | 2,474 | 16.7 | 33.4 |
| Classic 800×480 | 60.014 | 2,564 | 16.7 | 33.4 |
| Modern 800×480 | 60.031 | 2,517 | 16.7 | 33.4 |

Server proof: all four profiles commanded yaw **−1**, pitch **+1**, throttle **−0.8** and received **−1 / +1 / −0.8031496**, stateM **32**. Worst axis error **0.0031496**, within **1/127** wire quantization. Physical backslash reached server throttle **+1**; bracket cycling stayed within the actual hull count; a real single-weapon gun shot reached the desktop. Desktop shots reached the browser’s native projectile renderer. Each profile then resumed the same ship exactly once, seeded its shared clock/prediction, docked at station **1051**, and finished with local position exactly equal to its authoritative docked pose. Healthy voluntary departures left neither that player nor that ship on the server.

**18 inspected PNGs, 1,428,849 bytes total** (under 2,000,000 bytes). Each copied PNG is byte-identical to the image opened for review; [image-review.json](BROWSER_FLIGHT_P1051_assets/image-review.json) records its hash, dimensions and individual observation. No screenshot pixels were edited. The two 800×480 chase views supplement the 16 primary captures; the isolated harness’s own PNG count covers its four primary captures.

| Profile | Docked | Briefing | Both ships + HUD | Own-ship chase | Dock/return |
|---|---|---|---|---|---|
| Classic 1920×1080 | [Hangar](BROWSER_FLIGHT_P1051_assets/docked-classic-1920x1080.png) | [Briefing](BROWSER_FLIGHT_P1051_assets/briefing-classic-1920x1080.png) | [External view](BROWSER_FLIGHT_P1051_assets/flight-ships-classic-1920x1080.png) | Own scout is visible in external view | [Docked again](BROWSER_FLIGHT_P1051_assets/return-classic-1920x1080.png) |
| Modern 1920×1080 | [Hangar](BROWSER_FLIGHT_P1051_assets/docked-modern-1920x1080.png) | [Briefing](BROWSER_FLIGHT_P1051_assets/briefing-modern-1920x1080.png) | [External view](BROWSER_FLIGHT_P1051_assets/flight-ships-modern-1920x1080.png) | Own scout is visible in external view | [Docked again](BROWSER_FLIGHT_P1051_assets/return-modern-1920x1080.png) |
| Classic 800×480 | [Hangar](BROWSER_FLIGHT_P1051_assets/docked-classic-800x480.png) | [Briefing](BROWSER_FLIGHT_P1051_assets/briefing-classic-800x480.png) | [External view](BROWSER_FLIGHT_P1051_assets/flight-ships-classic-800x480.png) | [Chase](BROWSER_FLIGHT_P1051_assets/flight-own-classic-800x480.png) | [Docked again](BROWSER_FLIGHT_P1051_assets/return-classic-800x480.png) |
| Modern 800×480 | [Hangar](BROWSER_FLIGHT_P1051_assets/docked-modern-800x480.png) | [Briefing](BROWSER_FLIGHT_P1051_assets/briefing-modern-800x480.png) | [External view](BROWSER_FLIGHT_P1051_assets/flight-ships-modern-800x480.png) | [Chase](BROWSER_FLIGHT_P1051_assets/flight-own-modern-800x480.png) | [Docked again](BROWSER_FLIGHT_P1051_assets/return-modern-800x480.png) |

The desktop-size briefing captures show the full text and tip; the small captures show the title/objective/body, with the tip/lower controls below the scrollable fold. Hangar captures show the readable launch/briefing/return panel; at 800×480 it covers most native station text (the initial Modern capture has no native hangar text visible). External views show two scout meshes, rocks and the selected HUD; the Classic 800×480 desktop label is partly behind the bottom toolbar. Chase views show a larger own scout against the native planetary backdrop. Return captures precede the disconnect click: the no-zombie/menu return and fresh join are established by the subsequent UI/server assertions.


## Unfinished work

Items 0–5 are complete within the qualified loopback Chromium/first-sector scope. Training and the final Fast sweep completed with the existing failures above. All three Fast failure methods passed three isolated repetitions and are ledgered. No requested item remains unimplemented; the training/Fast gates remain red rather than being waived. Public hosting, other browser engines, physical mouse calibration, flight audio mixing, custom effects and large matches remain outside this slice. Other-sector backgrounds are explicitly deferred as described above.

## Native intent

Reuse the shared authoritative simulation, wire data, controls and prediction, with the original core and collision geometry. Existing native rule citations remain in source and in the supplemental parity ledger.

## Modern approach

Single-threaded browser lifecycle, async asset staging and transport, persistent verified assets, gesture-owned input and LDR rendering.

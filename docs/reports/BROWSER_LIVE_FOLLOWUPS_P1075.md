# P1075 — Browser live follow-ups

All five items and requested visual/browser proofs are complete. The broad Fast gate remains red on existing flakes/timing families; all implicated methods passed three isolated reruns. Baseline: `d1c3a825`. This report was created before implementation and updated during the work. All changes remain in the working tree. No commits, pushes, package installs, deployments, VPS/rig operations or lane-bots service changes were made.

## 1. Late art

The baseline was reproduced in a real generated-map browser session. After a station was built during flight, a proof-only request delivered its verified closure: `modelResident` became true, but `TextureStemFor` remained null in both styles. The original station renderer had already remembered the model miss. A second problem was exposed: P1073's sector/content-revision request shortcut could skip a newly built station entirely when no other pack subsequently promoted.

`Assets/AssetContentRevision.cs` supplies the shared invalidation boundary. `StagedAssetSource` implements its optional revision interface. Missing-resource sets now expire on `ContentRevision`; case-insensitive model indexes rebuild on that same change. Successful geometry and textures remain resident. A directory-backed desktop source has no revision, so its cache lifetime stays unchanged. The sector request path rechecks the live roster, immediately on station-count/sector changes and otherwise at a bounded 250 ms cadence.

Audit and changes:

| Browser-reachable cache | Treatment |
|---|---|
| Station, asteroid, ship | Revision-aware model/texture misses; existing prepared sector-texture lookup remains first for asteroids |
| Missile, treasure, model projectile | Refresh the initially incomplete case-folded file index; expire logged misses |
| Missile / model projectile textures | Cache by the resolved texture stem, so a temporary alternate texture cannot remain attached to a model after its preferred texture arrives |
| Probe | Retain the authored texture scope when the caller cannot resolve a texture yet; retry primary/part textures after revision changes and retain successes |
| Hangar | Retry cached null bitmaps/empty launch animation; refresh the weakly held source index |
| Sky / planet, moon / lens flare | Expire missing textures and retry incomplete layer loads |
| Distant / galaxy / giant libraries | Retry an initially empty library/config; retain successful images |
| Native effects / warp / ambient dust | Do not preserve a failed texture/fallback as a permanent successful lookup |
| Native material file lookup | Refresh its case-folded fallback index |
| HUD fonts, gauges, status, icons | Eagerly constructed only after the verified resident join closure; no deferred negative cache to invalidate |
| HD sky/material/shader caches, desktop preload/probe factory, desktop projectile sprites / ship LOD sprites / slideshow | Not selected by the browser's LDR flight lifecycle; no HD/default/input change |

The dev-only graphics probe uses the actual native models below. Its probe renderer is deliberately constructed with an unresolved (null) texture name, so the authored mesh scope must survive until promotion. Each first misses its model, then sees the model while its texture is still withheld, then sees the texture at a second revision. It executes the production renderer and reads a fresh GPU target for each case. Both styles passed every row; readbacks are independently nonblack.

| Renderer | Native model | Resolved texture | Drawn pixels (96×96) |
|---|---|---|---:|
| Station | `dgarr` | `dgarrbmp` | 1,454 |
| Asteroid | `bgrnd05` | `bgrnd05bmp` | 2,276 |
| Ship | `quizscoutv2` | `quizscout2bmp` | 224 |
| Missile | `mis05` | `mis05bmp` | 27 |
| Probe | `utl02` | `utl02bmp` | 677 |
| Treasure | `acs14` | `acs14bmp` | 590 |
| Model projectile | `bgrnd55` | `bgrnd55bmp` | 3,309 |

The loopback host's new `LateStationProof` invokes the existing authoritative `ServerHost.BuildStation` path on the sim thread. It adds no server rule. The harness holds the closure until the browser has observed the new station and attempted its missing art. Afterwards, Classic's `dgsup` resolves `dgsupbmp`, and Modern's `drgsy` resolves `dregabmp`; both have resident bytes and 29 actual station draw frames at the final measurement. Their in-view captures are [Classic](P1075_assets/built-station-classic.jpg) and [Modern](P1075_assets/built-station-modern.jpg).

The local-only `tools/p1075-proof-pack.py` supplies original probe/treasure/projectile fixtures through the existing battle manifest and verifier. It is not called by publication scripts. The probe and read-only camera are available only through the existing developer switch. Ordinary join manifests and pack verification are unchanged.

## 2. Purple frame

The exact render-target switch is `Rendering/AlephGlimpseRenderer.cs:483-490`: `EnsureTarget` binds the destination-sky target and then rebinds the previous target. Previously, the browser called this from the glimpse pass **after** drawing the main sky and geometry. Rebinding KNI's discard backbuffer discarded that scene; the subsequent HUD survived. P1073's content-revision invalidation repeatedly made those destination targets need regeneration. Font atlas construction happens before the flight scene; the native texture upload paths do not perform this late target switch.

The browser now prepares glimpse targets in `Game1.Scene.cs` before the main scene, clears the returned target, then draws sky/geometry. The later glimpse composite passes `allowPreparation: false`; it cannot switch targets. Desktop's existing call remains unchanged.

`tools/p1075-frame-probe.cjs` samples nine scene locations after **every RAF that presents a flight draw**, using WebGL readback without target switches. It detects the discard RGB band around `(68–69,34,136)`. Pack responses are delayed 150 ms apiece after launch, and the dev camera looks near an actual portal to exercise destination-target refresh while packs promote.

| Controlled first join | Classic | Modern |
|---|---:|---:|
| Baseline sampled / discarded frames | 2,105 / **12** | 2,096 / **11** |
| Fixed sampled / discarded frames | 3,089 / **0** | 3,283 / **0** |

A final graphics qualification also waited for the deferred painting library to become ready in both styles: **3,204 Classic + 3,350 Modern frames, zero discards**, with all seven native cases passing again. This includes the unresolved probe-constructor case.

The independent Node tests check detection, normal scenes, callbacks without a draw and offscreen-target exclusion. Machine-readable evidence, including the baseline pixel samples, is in [evidence.json](P1075_assets/evidence.json).

## 3. First-join bytes

Measured using fresh browser contexts, a local generated `SingleRing` seed 1073, civs 1/3 and the existing 5-per-side combat scenario, with a friendly bot seat reserved for the guest. The fixture also retains its desktop/keeper and research-commander peers. Both builds use the same fixture, response delay and read-only portal view; the pilot's throttle is stopped after launch to prevent sector transit from changing the 30-second sample. The second style sees the first style's completed fixture station. These are local controlled measurements, not a claim about every random-civ match.

The main controlled measurement uses the same starting camera procedure in both builds. The tables count **HTTP body bytes completed during the first 30 seconds after the launch click**, beyond the resident join. Gzip bytes come from the served response's encoded content length; raw bytes are the HTTP-decompressed original pack files, not GPU RGBA allocations. Families use native source names first (`bgrnd*`, `nebplnt*`, `environment*`/`globe*`) and assign shared assets once. All measured transfers used gzip; there were zero duplicate hash requests.

| Family | Before gzip, Classic / Modern | After gzip, both styles | Before raw, Classic / Modern | After raw, both styles |
|---|---:|---:|---:|---:|
| Stations | 0 / 74,174 | 0 | 0 / 201,236 | 0 |
| Hulls | 0 / 0 | 0 | 0 / 0 | 0 |
| Rocks | 8,276,880 / 8,276,880 | 2,220,145 | 25,166,172 / 25,166,172 | 8,388,724 |
| Planet posters | 2,313,836 / 2,313,836 | 2,313,836 | 8,388,728 / 8,388,728 | 8,388,728 |
| Globes / environments | 672,561 / 672,561 | 672,561 | 8,388,728 / 8,388,728 | 8,388,728 |
| Scene / funnels / effects | 10,905,673 / 10,905,673 | 947,445 | 14,426,954 / 14,426,954 | 4,474,466 |
| **Total** | **22,168,950 / 22,243,124** | **6,153,987** | **56,370,582 / 56,571,818** | **29,640,646** |

Classic's compressed on-demand traffic fell **72.2%**, and its raw promoted file traffic fell **47.4%**. The unchanged 601-file join manifest is 7,564,835 gzip bytes. Fresh framework publications are 6,478,942 bytes before and 6,484,868 after: a 5,926-byte code increase. Thus the join+runtime publication ledger remains below the existing 14 MiB budget (14,049,703 bytes after). Headers, HTML and manifest overhead are outside that publication ledger.

The final graphics repeat measured 6,153,987 gzip bytes in Classic and **12,210,722 in Modern** (29,640,646 / 46,418,094 raw). Modern requested two additional native rock surfaces during the initial camera interval; the queue responds to the view, so the exact window varies with early camera interest and timing. That repeat still reduced Modern traffic by **45.1%** versus its baseline. The primary table reports the original controlled pair rather than silently replacing it with this repeat. Both receipts are preserved in the evidence.

P1073's 9–17 MB estimate was gzip **in addition to** the 13.8 MB join/runtime ledger, whereas the live ~77 MB observation was promoted uncompressed files. The local baseline still exceeded the estimate at 22.2 MB gzip: it fetched offscreen rock surfaces and the full decorative painting library during the initial window. An earlier coasting reproduction also fetched a second sector's environment and reached 66.9–67.1 MB raw / 23.4 MB gzip. This explains why comparing the live promotion counter directly with the estimate was misleading.

Delivery changes:

- Queue current-sector artwork by camera visibility and distance. Turning toward an offscreen object raises its priority immediately; otherwise offscreen packs get a 30-second grace.
- Delay speculative sector prefetch and the decorative painting library. Stars, native planet/sky, native effects and visible geometry retain their normal delivery; the same original decorative files arrive later.
- Share one `AssetStager` between battle and recorded-sector streaming, coalescing concurrent same-hash downloads with independent waiter cancellation. Existing promoted-source deduplication remains. Tests also exercise a shared station texture and hash aliases with storage refusing persistence.
- Retain every original LOD and texture. The largest closures were audited rather than resampled or stripped.

The largest station closure (`station-307`, also 311) is **9,784,590 gzip bytes**: it includes the model's imported `ss93abmp` (2,834,736), `ss303.png` (5,504,650), `beltersbay.png` (1,426,822), their namespaces and geometry. The largest static-rock closure (`static-rock-ss300`) is **6,135,886**: `ss300.png`, `beltersbay.png`, `bgrnd03bmp` and geometry/namespaces. The production native parser and existing `mdl-to-glb inspect` / no-texture export confirm three object material scopes and one LOD level in each model (734 / 848 object triangles). All three listed texture stems are referenced by object and LOD mesh primitives; none of the largest texture payloads is an unused import. The model geometry is only tens of kilobytes, so stripping LODs would barely affect these multi-megabyte closures and could change later views. [pack-audit.json](P1075_assets/pack-audit.json) records the largest station/rock constituents. No LOD, texture size, verification rule, canonical-name rule or CSP was changed.

## 4. Afterburner

Native first: `WinTrek/trekigc.cpp:820-825` obtains the mounted afterburner and returns zero when absent; `WinTrek/consoledata.cpp:1122-1129` reports no active part for a null wrapper. Inventory panes are constructed from actual mounted/carryable parts (`WinTrek/inventoryimage.cpp:105-126`). Native does not have the port's top-centre English ACTIVE/PWR block.

The shared `HudModel` now derives readout activity from actual power, preserving the allowed port-only inactive label and `PWR 0%`. It also handles the real power ramp after key release. Raw button telemetry remains separate, so the browser proof demonstrates held Shift with power zero and an inactive readout in both styles. Desktop captures: [Classic](P1075_assets/desktop-afterburner-classic.png), [Modern](P1075_assets/desktop-afterburner-modern.png). Browser captures are the station views above; baseline [Classic](P1075_assets/baseline-afterburner-classic.jpg) / [Modern](P1075_assets/baseline-afterburner-modern.jpg) show the original false ACTIVE label.

## 5. Hangar / help

P945 deliberately preferred letterbox placement for Buy/Def, with an interior fallback at 4:3; P1070 reused that shared geometry. Browser page chrome made the margin unsafe. `HangarScreen.PortVerbRect` now uses native interior cells `(704,562,46,16)` and `(752,562,46,16)` at every aspect ratio. They are right of the native hover pane (ends at x=700) and avoid all eight native hotspots and the turret badge (`hangar.mdl:74-102`). Draw and hit-test still use the same rectangles. Both browser styles and desktop captures were reviewed at all requested sizes:

| Size | Browser Classic / Modern | Desktop Classic / Modern |
|---|---|---|
| 1920×1080 | [Classic](P1075_assets/hangar-classic-1920x1080.jpg) / [Modern](P1075_assets/hangar-modern-1920x1080.jpg) | [Classic](P1075_assets/desktop-hangar-classic-1920x1080.png) / [Modern](P1075_assets/desktop-hangar-modern-1920x1080.png) |
| 1280×800 | [Classic](P1075_assets/hangar-classic-1280x800.jpg) / [Modern](P1075_assets/hangar-modern-1280x800.jpg) | [Classic](P1075_assets/desktop-hangar-classic-1280x800.png) / [Modern](P1075_assets/desktop-hangar-modern-1280x800.png) |
| 800×480 | [Classic](P1075_assets/hangar-classic-800x480.jpg) / [Modern](P1075_assets/hangar-modern-800x480.jpg) | [Classic](P1075_assets/desktop-hangar-classic-800x480.png) / [Modern](P1075_assets/desktop-hangar-modern-800x480.png) |

The native hangar art is intentionally identical between desktop styles; the corresponding PNG pairs are byte-identical. The desktop proof uses the actual shared renderers in a MonoGame software-GL window, not an HTML mockup. The browser captures include page chrome and its identity strip.

The shared help renderer now names browser LEFT/RIGHT or PGUP/PGDN pages, UP/DOWN scroll, BACKSPACE back and Esc close under `BROWSER`. Desktop retains its prior text and navigation. Both real overlays were captured: [Classic](P1075_assets/help-classic.jpg), [Modern](P1075_assets/help-modern.jpg).

No hangar launch key was added. Native's `HangarScreen` wires the launch mouse event (`WinTrek/hangarscreen.cpp:92,145-160`), has no key handler (`:17-300`), and its base `Screen` exposes no key/accelerator method (`WinTrek/screen.h:10-18`). The native pane is `FrameImageButtonPane("animlaunch", .7, false, false)` (`hangar.mdl:12`); neither that namespace nor `engine/button.cpp` / `engine/frameimage.cpp` supplies a hangar launch accelerator. Desktop input/defaults are unchanged.

## Validation

| Check | Result |
|---|---|
| Solution build, disabled build servers | **0 warnings / 0 errors** |
| `tools/build-tools.sh` | **153 tool projects passed**, final source rebuild |
| New browser-side P1075 unit cases | **13 cases ×3 alone = 39 passed** |
| Shared presentation cases compiled for MonoGame | **9 cases ×3 alone = 27 passed** |
| New frame-sampler Node tests | **3 tests ×3 alone = 9 passed** |
| Browser suites | **52 + 261 passed** on final-source Fast discovery/execution |
| Affected MonoGame renderer/model/layout tests | **148 passed**, artwork enabled |
| Throttled P1075 browser proof | **Classic + Modern passed**, seven native renderer cases each, mid-session station, zero discard frames, ten captures |
| Desktop presentation proof | **8 captures passed** |
| P1073 generated-map browser harness | **PASS**, six captures |
| P1074 keyboard browser harness | **PASS**, Classic + Modern |
| Final full Fast sweep | **16,018 passed / 3 failed / 7 skipped**, 638.2 s; **9 infrastructure checks passed**; known failures/timings ledgered |
| Glossary links / parity CSV / diff whitespace | Passed |

The final Fast sweep failed three already ledgered assertions: P890 skirmish/native aim drift, P865b carrier relaunch re-task, and Chaff probe-broadcast receipt collection. Nine passing case executions across seven selection/catalog/artwork-loading methods exceeded the calibrated **11.762 s** limit. Every implicated method then ran in three fresh isolated processes, including all theory rows: **129/129 cases passed**, all below that limit. Earlier sweeps hit the ledgered wake-mine and NPC wingman assertions; their qualification batch also passed **120/120 cases**. Exact outcomes and durations are recorded in [the flake ledger](../TEST_FLAKES.md) and [evidence.json](P1075_assets/evidence.json). No assertion, tier, timeout, quarantine or calibration threshold was relaxed. The broad gate remains red.

All dotnet work used `$HOME/.dotnet/dotnet`, a task-specific `NUGET_HTTP_CACHE_PATH` and disabled build servers. Existing Chromium 153, its native libraries/fonts, Xvfb and software GL were reused. Initial Chromium launches lacked the existing font configuration; corrected launches are stable. Screenshots were opened and reviewed; they contain only generated fixture guest/bot names and native MIT artwork.

## Reproduction / remaining work

Use the existing `scripts/publish-browser-generation.sh` with a fresh output and client version `2.0.0`. For the local graphics fixture only, run:

```sh
python3 tools/p1075-proof-pack.py --generation "$GENERATION" --artwork "$ALLEGIANCE_ARTWORK_PATH"
python3 tools/p1061-browser-provenance.py "$GENERATION"
"$HOME/.dotnet/dotnet" tools/p1051-browser-host/bin/Debug/net10.0/p1051-browser-host.dll \
  "$ALLEGIANCE_ARTWORK_PATH" "$GENERATION" "$STATE" \
  --guests 1 --bots 5 --seconds 1800 --map-gen SingleRing --map-seed 1073
node tools/p1075-browser-followups.cjs "$NPM_DIRECTORY" "$STATE" "$EVIDENCE" "$GENERATION"
```

Use the installed Chromium's required library/font environment for the last command. `tools/p1075-presentation-proof` captures the desktop renderers under Xvfb. The harness is loopback-only. Baseline evidence used an isolated `git archive` of `d1c3a825`, with only read-only proof-camera/explicit-proof-request hooks; no cache, render-order or scheduling fixes were backported. The explicit baseline request separates the permanent renderer miss from the newly discovered sector-request starvation.

No implementation, requested screenshot or browser regression remains unfinished. The broad Fast gate is red on the ledgered failures/timing families described above, with 129/129 final isolated qualifications passing (plus the earlier 120/120). All owned proof hosts, browsers, MCP drivers and Xvfb windows have exited. Public-host remeasurement is deliberately outside this lane's scope.

## Native intent

Retain the original model, group texture, LOD, mounted-part and hangar-pane rules. Native model texture resolution remains `WinTrek/trekigc.cpp:1837-1839` and `effect/thinggeo.cpp:1038-1043`; afterburner activity comes from a real mounted part; native's hangar remains a mouse screen. Parity tracking is `docs/parity/p1075-browser-followups.csv`. Browser packs, revision invalidation and WebGL presentation scheduling are port-only delivery mechanisms.

## Modern approach

Keep verified atomic promotion and the existing CSP/canonical boundary, retain successful resources, prepare GPU targets before the scene, and schedule the bytes needed by the current view before speculative work. Share behavior and rectangles across Classic/Modern and desktop/browser. Leave reviewable code, test evidence and captures in the working tree.

# Browser sector packs — P1054

Status: **Items 0–4 are implemented, tested and reported. Final Fast remains red for the five ledgered families below.** Started **2026-11-19 03:05 UTC** (lane local time); deadline **11:05 UTC**. Finished **2026-11-19 10:24 UTC**, within the eight-hour cap. No commits or pushes.

| Item | Status |
|---|---|
| 0 — Record every sector | **Complete, retained from the first run:** 13/13 sectors, both HUDs, ≥30 seconds each, starmap and verified desktop opens. Not re-recorded. |
| 1 — Split packs | **Complete:** gameplay base, four entry first-frame copies and 13 original visual groups; independent source/budget audit passes. |
| 2 — Fetch on demand | **Complete:** priority upgrade/entry/prefetch, bounded verified staging/cache, incremental texture preparation, non-modal progress and retry. |
| 3 — Chromium proof | **Optimized 4/4 matrix passed**, all requested streaming cases, both HUDs/viewports. Legacy flight 4/4, security 11/11 and full P1053 pacing/gamepad/layout suites pass; final optimized legacy flight and security also passed, and the extra sky captures are complete. |
| 4 — Desktop and gates | **Complete with explicit red Fast disposition:** training **1,206/0/2**, all 42 windows; builds, tools, desktop and unit gates passed. Final Fast **15,547/5/6**; all failures repeated three times and ledgered. |

## Ruling and scope

Domingo ruled **LOW-RES FIRST, THEN UPGRADE**. The recorded Ceto entry retains the tested 256² asteroid surfaces in base. Immediately after actual launch, original 2048² entry surfaces have the highest priority and replace the displayed capped textures. Other sector packs contain originals only. No blank entry rocks, degraded gameplay files, larger transfer cap or changed simulation/wire/listing rule is authorized or implemented.

The first run established that original entry surfaces before the ticket exceed 14 MiB; its [codec](BROWSER_SECTOR_PACKS_P1054_assets/entry-codecs.json) and [runtime response-body](BROWSER_SECTOR_PACKS_P1054_assets/runtime-body.json) receipts remain. The ruling resolves that decision. `bgrnd*bmp.mdl` are **asteroid surfaces**, separate from the globe/environment/planet sky.

Qualification is for the recorded **XXV_simple.igc** fixture with **SimulationRandom seed=1051**, PCore001 and the recorded initial map population. Later population seeds and other maps/cores are outside this slice. The manifest identifies map, seed and sector IDs so additional recorded sets can be added. The proof keeps a real friendly player docked, plus a normally admitted opposing player to satisfy native team balance, so warm reload does not trigger native empty-side victory and rebuild the map. Neither player changes server state outside ordinary client messages.

## Changes and source references

| Area | Source / result |
|---|---|
| Split and audit | `tools/p1054-browser-packs.py:23` verifies recorded closures, allows only visual deferral, preserves geometry/core/CVH/weapon bytes, emits `join-manifest.json`, `sector-manifest.json` and receipts, and removes unreferenced hash files. `scripts/publish-browser-flight.sh:29` integrates it before content-addressed finalization. `tools/p1054-sector-audit.py:11` independently compares published bytes to original sources and measures the stricter whole-application budget. |
| Dependency contract | `src/Allegiance.ClientCore.Game.Browser/Assets/StagedManifest.cs:8` adds explicit visual dependencies; ordinary dependencies remain resident. `Assets/SectorManifest.cs:12` in that project validates the complete graph, immutable metadata, visual allow-list and bounded batches before ticket minting. |
| Staging and priority | Browser `Assets/AssetStager.cs:13` shares four download slots across loaders using the same HttpClient; `Assets/SectorPackStreamer.cs:52` owns priority and `:114` verifies SHA-256 in bounded yielding blocks. No simulation/input/prediction method awaits a pack. |
| Texture replacement | Browser `Assets/IncrementalSurface.cs:19` preserves native RGB565 rows/pitch/UV interpretation. `Assets/SectorTexturePool.cs:74` allocates separately, decodes/uploads eight rows per RAF, then atomically publishes each completed texture. Case-canonical names and monotonic texture epochs survive rejoining. Validated texture names use direct case-insensitive dictionary membership rather than repeated path validation in the rendering loop. Current plus one selected/prefetched sector own GPU residency. |
| Optional renderer hook | `src/Allegiance.ClientCore.Game/Assets/IResidentTextureSource.cs` exposes prepared textures without IO. `Rendering/NativeAsteroidRenderer.cs:123`, `Rendering/NativeSkyRenderer.cs:105,344` and `Rendering/PlanetPoster.cs:66,143` in the shared Game project consult it; desktop DirectoryAssetSource does not implement it. |
| Browser orchestration | `src/Allegiance.ClientCore.Browser/Pages/Menu.Online.cs:106` reads/validates the sector index and retains its cache hashes before base staging/ticket minting. `Pages/Menu.Sectors.cs:17,34` owns the worker and launch/entry signals. `src/Allegiance.ClientCore.Game.Browser/Game1.Sectors.cs:46,57,73` supplies starmap selection, discovered aleph selection and ordinary waypoint navigation. The starmap key uses the shared `ToggleStarmap` binding, preserving native Tab thrust. |
| Progress/lifecycle | `Pages/Menu.razor` and `wwwroot/menu.css` in the browser head show non-modal progress and accessible controls. Close cancels preparation without needing another graphics frame; nullable world lists remain safe while disconnect is draining. `Assets/BrowserAssetCache.cs` in that head bounds refused-storage visit memory as well as the existing persistent cache. |
| Proof and index | `tools/p1054-browser-sectors.cjs` uses actual UI, identity, client controls and normal warp travel; `tools/p1051-browser-host/Program.cs` adds read-only authoritative pose receipts and the ordinary keeper player. `GLOSSARY.md` and `docs/parity/p1054-sector-delivery.csv` record the seams and reused native intent without new parity credit. |

The first run's recorder/analysis and asynchronous audio-race fix are retained. `tools/p1051-flight-opens/Program.cs` and `tools/p1054-sector-analysis.py` supplied item 0; `flight-input.js` serializes resume/suspend and rechecks current lifecycle intent. No additional audio or gamepad mapping change is made in this continuation.

## Recorded map and per-sector table

**Complete:** the final recorder visited **all 13 sectors unattended** in Classic and Modern, with at least 30 seconds per sector and the native starmap. Actual file-open counts: **985 Classic / 984 Modern**. Both game/server processes exited cleanly with zero server tick overruns.

Recorded map: **XXV_simple.igc, seed=1051**, **Ceto (1053)** entry. Its two home and eleven neutral sectors, 38 alephs and 321 asteroids exercise the existing P1051 map with varied planets, nebulae and asteroid surfaces. The final return through Sedna was performed entirely by the checked-in waypoint/aleph pilot. No ship pose, sector, speed, visibility or server object was injected. The recorder flies clear to give the ordinary desktop arrival/braking law a normal return approach.

[Classic trace](BROWSER_SECTOR_PACKS_P1054_assets/desktop-classic.json), [Modern trace](BROWSER_SECTOR_PACKS_P1054_assets/desktop-modern.json), [complete verified file attribution](BROWSER_SECTOR_PACKS_P1054_assets/sector-analysis.json).

| Attribution | Files | Original raw bytes | gzip bytes |
|---|---:|---:|---:|
| Arrokoth (1051) | 6 | 8,461,800 | 2,788,775 |
| Borasisi (1052) | 4 | 16,781,312 | 2,902,195 |
| Ceto (1053) | 19 | 19,338,412 | 4,341,209 |
| Deucalion (1054) | 3 | 8,426,822 | 2,938,243 |
| Eris (1055) | 1 | 8,388,728 | 1,360,306 |
| Farout (1056) | 3 | 16,780,680 | 1,636,906 |
| Haumea (1057) | 2 | 8,391,952 | 948,746 |
| Ixion (1058) | 7 | 16,854,416 | 1,074,528 |
| Makemake (1059) | 3 | 8,425,920 | 1,492,645 |
| Orcus (1060) | 1 | 8,388,728 | 3,159,436 |
| Quaoar (1061) | 3 | 9,025,628 | 2,151,635 |
| Varda (1062) | 1 | 8,388,728 | 2,313,836 |
| Sedna (1063) | 1 | 8,388,728 | 2,992,527 |
| common | 915 | 168,742,262 | 58,684,321 |

These are **actual desktop opens**, including desktop-only audio and speculative prewarming; these totals are not browser pack sizes. Prelaunch opens and dependency reuse are common. A texture prewarmed from an adjoining sector is attributed to its recorded consuming root when it is not shared. Original size and SHA-256 were checked for every counted file before measuring deterministic gzip. No inferred open is counted. Native model namespace roots are included in the receipts.

| Sector | Classic dwell (s) | Modern dwell (s) |
|---|---:|---:|
| Arrokoth | 30.016 | 30.016 |
| Borasisi | 30.013 | 30.015 |
| Ceto | 30.003 | 30.015 |
| Deucalion | 30.004 | 30.001 |
| Eris | 30.013 | 30.016 |
| Farout | 30.015 | 30.016 |
| Haumea | 30.000 | 30.000 |
| Ixion | 30.016 | 30.016 |
| Makemake | 30.006 | 30.014 |
| Orcus | 30.007 | 30.001 |
| Quaoar | 30.014 | 30.016 |
| Varda | 30.017 | 30.017 |
| Sedna | 30.016 | 30.000 |

Reproduce the audit with the two trace paths, artwork/content roots and `--expected-sectors 13` using `tools/p1054-sector-analysis.py`. The long recording mode keeps the production replay reader’s ten-minute limit intact by skipping its briefing-fixture extraction; its complete wire recording remains in ignored evidence. The older 60-second mode retains its original decoder-fixture export.

## Pack sizes, integrity and deferral

The final base has **601 logical names / 577 unique immutable files**, **32,536,380 raw / 7,564,835 gzip bytes**. It is **163,742 transfer bytes (2.12%) smaller** than P1051's 7,728,577-byte base closure. Runtime gzip is **6,159,974 bytes**, giving **13,724,809 bytes (13.09 MiB)** for base + runtime.

The stricter inventory includes **all 124 non-pack files**, including application code, manifests, receipts, notices and the separate preview art: **13,988,395 gzip bytes (13.34 MiB)**, **691,669 bytes below 14 MiB**. This conservative calculation gzips every framework file even when Chromium does not request it. Observed HTTP bytes including headers are measured separately by the browser harness.

The independent audit passes **684 source/publish comparisons**, **29 original surfaces** (27 unique streamed files plus two resident entry sky surfaces), **four entry copies**, **604 total unique hash files** and **zero unreferenced pack files**. Every streamed surface is byte-identical to the original artwork, including source size and SHA-256. Only the four entry first-frame copies are transformed. All original rock surfaces are 2048²; other surfaces retain their actual original dimensions. No original is capped in a sector group.

| Sector | Files | Original raw bytes | gzip bytes |
|---|---:|---:|---:|
| Arrokoth (1051) | 7 | 58,721,076 | 15,868,095 |
| Borasisi (1052) | 6 | 50,332,352 | 13,504,454 |
| Ceto (1053) | 4 | 33,554,896 | 10,647,256 |
| Deucalion (1054) | 7 | 58,721,076 | 16,416,514 |
| Eris (1055) | 6 | 50,332,352 | 11,456,730 |
| Farout (1056) | 6 | 50,332,352 | 12,239,470 |
| Haumea (1057) | 7 | 58,721,076 | 14,599,010 |
| Ixion (1058) | 8 | 67,109,804 | 13,775,905 |
| Makemake (1059) | 7 | 50,463,540 | 13,428,113 |
| Orcus (1060) | 6 | 42,074,816 | 13,807,066 |
| Quaoar (1061) | 6 | 50,332,352 | 13,173,414 |
| Varda (1062) | 7 | 58,721,076 | 15,416,844 |
| Sedna (1063) | 6 | 50,332,352 | 12,630,796 |

These are logical group sizes; shared original hashes are reused between sectors and incur no second fetch while cached. Each sector is a group of content-addressed files, not an additional archive. Ixion slightly exceeds 64 MiB including headers; it is staged in successive ≤4-file snapshots, preserving the existing 16 MiB file / 64 MiB snapshot limits. All geometry, common models, collision/CVH, core, hulls, weapons, projectiles and gameplay data stay in base. The publisher's deferral allow-list contains only recorded rock surfaces and planet/nebula textures; the source audit verifies every other base byte too. Sky dome geometry stays resident.

Cache API bytes remain bounded at **64 MiB / 1,024 entries** with the existing quota reserve, recovery, SHA/size checks and obsolete-hash purge. Refused storage uses bounded visit memory. Base commits retain current sector-index hashes. The fitting base + entry working set qualifies for warm zero-byte pack reload; a larger tour can legitimately evict old persistent files. Cache eviction never evicts resident gameplay bytes.

## Runtime behavior

Delivery priority is **entry upgrade → current sector → speculative prefetch**. Selecting an aleph or a sector in the starmap requests prefetch; when a partner mouth is still undiscovered, a uniquely matching visible aleph label provides only a speculative visual hint. It supplies no navigation or physics value. Sector entry requests any missing group. The worker uses 1/2/4/8/16/32-second capped backoff on failure and never ends the session.

Before a remote visual group arrives, the game draws its normal **(5,12,20) dark clear color**, seeded star points (**zero on this map**) and native sun/lens-flare components where visible; missing environment dome texture and planet poster are omitted. Asteroid geometry remains lit-solid when a remote surface is absent. Entry rocks retain their capped textures until each fully verified/prepared original is ready. The real native sky/rock renderers use completed textures on subsequent frames. Simulation, keyboard/mouse/gamepad sampling, prediction and pose cadence keep running.

## Chromium qualification

**Final optimized matrix: 4/4 profiles passed** in Chromium **153.0.8010.0**. Each profile checks cold base-only-before-ticket ordering, capped → original entry upgrade without blank rocks, actual uncached Borasisi entry with placeholder → prepared visuals, authoritative controls and pose receipts while streaming, starmap prefetch followed by ready-on-arrival Arrokoth entry, and 503 → backoff → success. Persistent profiles also qualify warm reload at **zero pack bytes**; Modern 800×480 refuses storage and qualifies bounded same-page memory/retry. [Complete final receipt](BROWSER_SECTOR_PACKS_P1054_assets/sector-proof.json).

| HUD / viewport | Base HTTP bytes | Entry p95 ms | Remote p95 ms | Entry / remote max frame ms | Warm pack bytes | Result |
|---|---:|---:|---:|---:|---:|---|
| Classic 1920×1080 | 7,819,047 | 16.7 | 16.8 | 116.7 / 100.1 | 0 | Passed |
| Modern 1920×1080 | 7,819,047 | 16.8 | 16.7 | 116.7 / 100.0 | 0 | Passed |
| Classic 800×480 | 7,819,047 | 16.8 | 16.8 | 116.7 / 100.0 | 0 | Passed |
| Modern 800×480 · storage refused | 7,819,047 | 16.8 | 16.8 | 116.7 / 100.0 | same-page memory | Passed |

The HTTP column sums CDP encoded response lengths for base pack files, including response overhead. The publish budget separately measures deterministic gzip file bodies; runtime/application files are included in the strict budget above.

All four rendered entry surface widths change **256 → 2048**; every sampled entry frame has nonblank rock textures. The largest measured texture-preparation step in this matrix is **8.1 ms**. Authoritative accepted-pose counters and fresh left/right controls continue through both swap windows; maximum sampled receipt-change gaps are **300–481 ms**. Authoritative receipt samples and correction counters in the captured metrics remain in the JSON; this is not described as a perfectly uniform network cadence.

The p95 threshold is **<34 ms**, the P1053 recovery budget. Full-window maximum frame intervals and authoritative receipt gaps are retained too; p95 is not a claim that every individual frame is below 34 ms. An [earlier optimized run](BROWSER_SECTOR_PACKS_P1054_assets/sector-optimized-first.json) reached **183.4 ms** for a single frame and **11.5 ms** for a preparation step; the final matrix above reached **116.7 ms** and **8.1 ms** respectively. Headless Chromium timings are **not GPU-representative**. Capture/readback is outside swap timing windows.

**Final P1051 full flight: 4/4 passed**, including controls, cross-client visibility/shots, blur/hidden release, same-ship authenticated resume surviving the full minute, docking, stale cookie cleanup and context-loss explanation. [Final legacy flight receipt](BROWSER_SECTOR_PACKS_P1054_assets/legacy-flight-qualified.json). **Final security: 11/11 passed**, the required nine admission/security cases plus two deferred-cancellation cases, with actual Cache API/IndexedDB inspection and no secret persistence/log/URL leakage. [Final security receipt](BROWSER_SECTOR_PACKS_P1054_assets/security-qualified.json). P1053 initially timed out waiting for its <20 ms quiet-start condition while continuously turning; production remained full-scale with slow mode off. Repeated path regex/canonicalization was removed from the validated texture lookup. The optimized Release now passes the **unchanged** quiet-start, gamepad, five-step pacing exercise and **all five** layout/cache profiles. [Final P1053 receipt](BROWSER_SECTOR_PACKS_P1054_assets/polish-qualified.json). No production threshold, timeout or proof assertion is relaxed. The first-run baseline receipts remain available: [flight](BROWSER_SECTOR_PACKS_P1054_assets/flight-final.json), [security 9/9](BROWSER_SECTOR_PACKS_P1054_assets/security-final.json), [polish](BROWSER_SECTOR_PACKS_P1054_assets/polish-final.json). They are baseline results, not final streaming qualification.

## Verification gates

| Check | Passed / Failed / Skipped |
|---|---|
| Solution, both browser projects | **0 warnings / 0 errors** on current source |
| Standalone tools | **148 projects**, 0 warnings / 0 errors; current fixture host separately rebuilt 0/0 |
| Release publish and original-byte/budget audit | Passed |
| Core P1048/P1048B/P1052 selection | **207/0/0** |
| Lobby P1048/P1048B/P1052 selection | **49/0/0** |
| Desktop P1052 selection | **10/0/0** |
| Entire browser game / head projects | **74/0/0**, **12/0/0** |
| New C# sector cases | **15/0/0 ×3 alone**, including priority, retries, cancellation, global four-download bound, integrity, native pixel equality, epochs and case canonicalization |
| New Python splitter cases | **4/0/0 ×3** |
| New JS cache cases | **2/0/0 ×3 alone**; entire cache suite **10/0/0** |
| Input/pacing JS | **19/0/0 ×3** |
| Brotli, real desktop briefing fixtures | **29 cases / 35 checks ×3** |
| Python publish regression | **4/0/0 ×3** |
| Desktop Debug filenames | **753/753 identical**, no added or removed names |
| Desktop HUD | **1,848/1,848**, zero pixel/semantic differences, 84 animation-only regions, zero corrupt goldens |
| Training Fast + Live, artwork, repeat 3/window proofs once | **1,206/0/2**, all six children passed, **42/42 physical windows passed** |
| Final Fast | **15,547/5/6**, red; nine infrastructure checks passed; five methods ×3 isolated, **14/1/0** |
| Glossary / parity / whitespace | Passed: **1,001 glossary paths**, registered parity matrices plus three supplemental n/a rows, clean diff whitespace |

One optimized legacy invocation ended with SIGTERM after its first reconnect and produced no assertion result; it is not counted. A fresh fixture completed all four scenarios.

The security harness initially reproduced its existing CDP response-event body race after navigation. `tools/p1051-browser-security.cjs` now captures each real ticket response inside the route before delivering it, matching the flight proof. No admission rule, assertion, token budget or production timeout is relaxed; a fresh fixture passes all 11 cases.

The full gate uses the preinstalled SDK/packages, a task-local writable NuGet HTTP cache and disabled build servers/node reuse/shared compilation. No packages are added or fetched. Training was restarted after shared-renderer implementation settled; the interrupted exploratory run is not counted as a completed gate.

## Training child results

The first qualification attempt passed its headless child **131/0/0** and 16 physical windows, then the Modern M8 default child exited at goal 3.3 after 3 m 27 s. Its captured log ends mid-frame with no managed exception; the 21-minute wrapper timeout did not fire. The remaining attempt was stopped, so it is not counted as a completed gate. Similar window-child exits are already ledgered under P918, but this does not establish a cause. The exact Modern M8/default parameter case then passed **1/0/0 in each of three separate processes**, in 00:05:57.1449639, 00:05:56.9536055, 00:05:54.9515969. [Failure and isolation receipt](BROWSER_SECTOR_PACKS_P1054_assets/training-window-exit.json). A fresh complete `tools/run-training-tests.sh --no-build --repeat 3 --window-proofs-once` gate started **05:44 UTC**. The ignored launcher now records each proof-child exit code and enables a crash dump if another native crash occurs; production and test assertions are unchanged. The fresh complete gate exited **0** at **10:06 UTC**, with **1,206/0/2 overall**. [Six children, TRX hashes and all 42 window markers](BROWSER_SECTOR_PACKS_P1054_assets/training-qualified.json).

```text
Run 1 — Passed!  - Failed:     0, Passed:   131, Skipped:     0, Total:   131, Duration: 10 s - Allegiance.ClientCore.Tests.dll (net10.0)
Run 1 — Passed!  - Failed:     0, Passed:   299, Skipped:     2, Total:   301, Duration: 3 h 58 m - Allegiance.ClientCore.MonoGame.Tests.dll (net10.0)
Run 2 — Passed!  - Failed:     0, Passed:   131, Skipped:     0, Total:   131, Duration: 9 s - Allegiance.ClientCore.Tests.dll (net10.0)
Run 2 — Passed!  - Failed:     0, Passed:   257, Skipped:     0, Total:   257, Duration: 11 m 9 s - Allegiance.ClientCore.MonoGame.Tests.dll (net10.0)
Run 3 — Passed!  - Failed:     0, Passed:   131, Skipped:     0, Total:   131, Duration: 10 s - Allegiance.ClientCore.Tests.dll (net10.0)
Run 3 — Passed!  - Failed:     0, Passed:   257, Skipped:     0, Total:   257, Duration: 11 m 8 s - Allegiance.ClientCore.MonoGame.Tests.dll (net10.0)
```

All **42 physical P912 windows passed**, including both default Nanite completions, Modern loss, both sacrifice and both early-mount variants, plus the individual lesson checkpoints. The two M4 hunt cases retain their existing opt-in skips. Repeats 2–3 omit only P912 under the requested flag; all other selected Fast/Live lessons remain.

## Fast accounting

The **first run's baseline** ended at **15,533 passed / 4 failed / 6 skipped**; each failure passed three isolated reruns and was ledgered. [Baseline summary](BROWSER_SECTOR_PACKS_P1054_assets/fast-summary.json), [exceptions](BROWSER_SECTOR_PACKS_P1054_assets/fast-exceptions.json), [isolations](BROWSER_SECTOR_PACKS_P1054_assets/fast-isolated.json). The continuation's **single final Fast sweep ran last**, after implementation, all browser/desktop/build/static checks and the completed six-child training gate. It exited **1**: **15,547 passed / 5 failed / 6 skipped**, **15,558 total**, **902.6 s**. All **nine infrastructure checks passed**. The calibrated case limit remained **11.531 s**; one timing offender and one quarantine flake were recorded, both the already quarantined side-elimination case. Browser head and game remained **12/0/0** and **74/0/0** in this sweep.

Each failing method, including the timing offender, was then selected in **three separate fresh processes** with artwork: **15 invocations**, **14 passed / 1 failed / 0 skipped** test rows. No second Fast sweep was run.

| Method | Sweep observation | Three isolated outcomes (passed/failed/skipped) |
|---|---|---|
| `DeadLatchRespawnLiveTests.Remote_client_sees_victim_again_after_death_and_respawn` | B never latched A's death after 2,500 ticks | **1/0/0, 1/0/0, 1/0/0** |
| `MissileTrackingLiveTests.FiredMissile_AppearsMovesAndDisappears_InClientWorld_OverTheWire` | Missile never appeared | **1/0/0, 0/1/0, 1/0/0**; the isolated failure saw the one-shot missile at zero but no advancing per-tick position |
| `BotFlightTests.Bot_trajectory_matches_independent_ExecuteMove_rollout` | Tick 3 pose drift **78.796715** from the independent rollout | **1/0/0, 1/0/0, 1/0/0** |
| `BallotVotingLiveTests.Resign_passing_deactivates_the_side_and_ends_the_mission` | Passed-ballot announcement arrived without the expected matching observation | **1/0/0, 1/0/0, 1/0/0** |
| `SideEliminationLiveTests.Eliminating_side2_sends_side_only_gameover_to_it_alone` | Lethal restart-station hit absent; **18.800 s**, over **11.531 s**; quarantine retry passed | **1/0/0, 1/0/0, 1/0/0**, all below the calibrated limit |

These are existing ledgered families, not a root-cause claim. Missile delivery remains reproducible alone, with a different observation from the sweep. **Fast remains red.** No assertion, timeout, tier, quarantine policy, throttle, privilege check, simulation or wire rule was changed. The affected server simulation, ballot and missile transport files are unchanged by this task. [Final summary](BROWSER_SECTOR_PACKS_P1054_assets/continuation-fast-summary.json), [sweep failures](BROWSER_SECTOR_PACKS_P1054_assets/continuation-fast-exceptions.json), [three isolated runs per method and TRX hashes](BROWSER_SECTOR_PACKS_P1054_assets/continuation-fast-isolated.json).


## Images

All **10** PNGs below were individually opened and reviewed. They are unedited 800×480 screenshots from the optimized Chromium build, **1,945,989 bytes total**, under both 2 MB and 2 MiB. Entry upgrade, sky swap and starmap selection are shown in **both HUD styles**. The 1920×1080 profiles are covered by the final matrix receipt; their larger captures remain in ignored evidence. [Image sizes/hashes/review](BROWSER_SECTOR_PACKS_P1054_assets/image-review.json).

Native external-camera angles with quieter sky directions keep the screenshots within budget; no pixels were cropped, resized or edited. [Two-HUD entry camera proof](BROWSER_SECTOR_PACKS_P1054_assets/entry-camera-proof.json). Screenshot readback is outside the primary swap timing windows.

| View | Before / selection | After | Visible content |
|---|---|---|---|
| Classic Ceto entry | [Capped](BROWSER_SECTOR_PACKS_P1054_assets/entry-capped-classic-800x480.png) | [Original](BROWSER_SECTOR_PACKS_P1054_assets/entry-original-classic-800x480.png) | The centered asteroid has coarse surface sampling before and finer terrain detail after, with Classic gauges visible. Rock orientation, lighting and drawing-buffer scale change over time; byte identity and 256 → 2048 texture dimensions are proved separately. |
| Modern Ceto entry | [Capped](BROWSER_SECTOR_PACKS_P1054_assets/entry-capped-modern-800x480.png) | [Original](BROWSER_SECTOR_PACKS_P1054_assets/entry-original-modern-800x480.png) | The near asteroid's broad, coarse lit patch becomes a more detailed cratered surface, with Modern gauges and the non-modal loading note. Normal motion changes the rock's orientation and lighting. |
| Classic Borasisi sky | [Placeholder](BROWSER_SECTOR_PACKS_P1054_assets/placeholder-sky-classic-800x480.png) | [Prepared](BROWSER_SECTOR_PACKS_P1054_assets/real-sky-classic-800x480.png) | The first view has a plain dark backdrop and a loading note; the later view has a green/yellow/purple nebula behind a drawn asteroid. [Extra Classic camera proof](BROWSER_SECTOR_PACKS_P1054_assets/sky-visual-proof.json). |
| Modern Borasisi sky | [Placeholder](BROWSER_SECTOR_PACKS_P1054_assets/placeholder-sky-modern-800x480.png) | [Prepared](BROWSER_SECTOR_PACKS_P1054_assets/real-sky-modern-800x480.png) | The same dark → colored-nebula appearance is visible behind a drawn asteroid, with Modern gauges. [Extra Modern camera proof](BROWSER_SECTOR_PACKS_P1054_assets/sky-modern-visual-proof.json). |
| Classic starmap prefetch | [Selection](BROWSER_SECTOR_PACKS_P1054_assets/starmap-classic-800x480.png) | — | The overlay shows authored sector links, Ceto's current-sector marker and Borasisi selected while Ceto upgrade has priority. |
| Modern starmap prefetch | [Selection](BROWSER_SECTOR_PACKS_P1054_assets/starmap-modern-800x480.png) | — | The same selection and shared overlay over the Modern HUD. |

Starmap labels are crowded at this small viewport. The reused renderer's fixed “TAB TOGGLE” title is stale for the native Insert binding; the browser uses the shared binding and the visible Starmap button. The early entry captures also reflect the existing P1053 drawing-buffer recovery after synchronous base-renderer setup. Images establish presentation content, not GPU performance.

## Disposition

Items **0–4 are implemented, exercised and reported**. All required builds, publication/integrity audits, new repeated tests, desktop checks, browser proofs and the complete training gate passed. The final Fast sweep remains **red** for the five observations above; all required isolated repetitions are complete and ledgered. Missile delivery reproduced once alone. This is an unresolved existing test-family observation, not a green Fast-gate claim. No task implementation remains unfinished and no behavior decision remains.

All changes are left in the working tree, uncommitted. No commits, pushes or stash operations occurred. The brief is preserved in ignored task scratch and absent from the change set. All owned proof hosts, Chromium, training, Fast and isolation processes have ended. Ten published screenshots were individually reviewed, with **1,945,989 PNG bytes** total. Changed-line hygiene found no added secret, personal email or home path; all report links resolve.

No new service, persistent store, port or application environment setting is introduced; compose changes and Docker validation are not applicable (Docker is unavailable). All network work was loopback-only. Public receipts contain names, hashes, counts and timings, not credentials or artwork payloads.

## Native intent

Preserve authoritative data, ordinary client controls, visibility/discovery and warp travel. Native image rows/pitch and mesh UVs come from `engine/modeler.cpp:723-733` and `engine/device3d.cpp:2245-2246`; waypoint self-orders reuse `WinTrek/console.cpp:1940-1972` and `clintlib/clintlib.cpp:2460-2469`; Dodge/external camera/starmap rules are existing helpers. No new game or wire rule earns parity credit.

## Modern approach

Deliver verified, content-addressed decorative visuals asynchronously per recorded sector. Keep entry first-frame textures and all gameplay data resident, prepare original textures incrementally, publish completed GPU surfaces and keep progress/retry outside simulation, input and prediction. Desktop sources retain the original loading path.

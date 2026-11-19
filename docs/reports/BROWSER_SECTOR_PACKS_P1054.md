# Browser sector packs — P1054

Status: **Stopped for the behaviour decision explicitly permitted by the brief. Item 0 is complete; items 1–3 are not implemented.** The recorded map’s original entry visuals conflict with the 14 MiB transfer cap as measured below. Independent audio safety fixes and all requested baseline checks are complete; final Fast is red for four existing failure families, each passing three isolated reruns. The 8-hour cap was not reached. No commits or pushes.

| Item | Status |
|---|---|
| 0 — Record every sector | **Complete:** 13/13 sectors, both styles, ≥30 s each, starmap; final unattended walks and verified attribution table. |
| 1 — Split original-resolution packs | Awaiting the documented entry-visual/budget decision; original-byte and codec measurements completed. |
| 2 — Fetch on demand | Awaiting the entry-visual/budget decision; no residency policy changed. |
| 3 — Chromium proof | Pending: cold, uncached/prefetched warp, warm, storage refusal, 503 retry; both styles and resolutions. |
| 4 — Desktop and gate safety | **Baseline checks complete:** builds, 148 tools, filename/HUD zero-delta, browser projects and P1051/P1053/security harnesses passed. Fast **15,533/4/6, red**; all four failures passed 3× alone and are ledgered. New streaming remains unqualified. |

## Changes

`tools/p1051-flight-opens/Program.cs:35` adds the optional `--sectors` recording mode while retaining the original first-sector mode. `:98` records sector attribution, `:112` collects per-sector dependency roots, and `:215` drives native desktop waypoint/aleph navigation and wall-clock dwell. `tools/p1054-sector-analysis.py:42` verifies original opened bytes, walks recorded namespace roots, classifies common/reused assets and measures deterministic raw/gzip sizes. `tools/p1054-sector-analysis-tests.py` covers the analysis contract. `GLOSSARY.md` indexes these tools. Pack builder, desktop loading and game rules have no authored changes while the entry residency decision is pending. An independent baseline audio race is fixed in `src/Allegiance.ClientCore.Browser/wwwroot/flight-input.js:5`: one coalescing worker per context serializes async resume/suspend and rechecks current gesture/focus/visibility/pacing intent after each operation. Five delayed-resume regression cases in `tools/p1051-input-tests.cjs` fail against the original source and pass **5/5 ×3** alone; all JS input/pacing cases pass **19/19 ×3**. No simulation or input step awaits an audio operation. `tools/p1053-browser-polish.cjs:114` now waits for normal pacing recovery and captures the initial scale synchronously before CDP throttling; the same five scale transitions, audio checks and actual server controls remain mandatory.

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

## Pack sizes, integrity and deferral rule

**No P1054 sector packs have been published.** The local regression generation retains the existing P1051 capped join pack. The numbers below measure original artwork and establish the budget conflict; published original-surface byte identity and per-sector pack sizes remain unimplemented.

Initial hard budget finding (original artwork, deterministic gzip level 9): Ceto's four recorded asteroid surfaces alone require **10,647,256 bytes (10.154 MiB)**. The fresh Release runtime alone requires 6,131,057 gzip bytes (77 files; 17,175,361 raw), giving a **16,778,313-byte (16.001 MiB) lower bound**, before core, hulls, HUD, fonts, sky or application code. This exceeds the 14 MiB cap by 2,098,249 bytes. This is a measured lower bound, not the complete proposed base size.

| Ceto original surface | Raw bytes | gzip bytes |
|---|---:|---:|
| bgrnd55bmp.mdl | 8,388,724 | 3,025,660 |
| bgrnd52bmp.mdl | 8,388,724 | 3,532,547 |
| bgrnd03bmp.mdl | 8,388,724 | 2,364,955 |
| bgrnd05bmp.mdl | 8,388,724 | 1,724,094 |

With the existing per-file gzip/Brotli delivery formats: the four rock textures **plus the actual entry sky dome texture and planet texture**, compressed with Node Brotli quality 11, require **11,173,496 bytes**. The fresh SDK runtime's smallest available published encodings total **4,909,489 bytes**. Their combined **16,082,985 bytes (15.338 MiB)** exceed the cap by **1,402,921 bytes**, still excluding core, hulls, weapons, HUD, fonts, application code and manifests. [Codec receipt](BROWSER_SECTOR_PACKS_P1054_assets/entry-codecs.json). Gzip sizes in the first table use Python's deterministic level-9 encoder; the codec receipt separately records Node's encoder, whose gzip output differs slightly.

The full-publish figure includes optional runtime files. A separate cold Chromium probe measured the **actual runtime response bodies**, excluding HTTP headers: **4,438,399 bytes across 75 responses**. Adding the same six original entry surfaces gives **15,611,895 bytes (14.889 MiB)**, **931,831 bytes over 14 MiB**, still before all gameplay/base assets. [Payload-only receipt](BROWSER_SECTOR_PACKS_P1054_assets/runtime-body.json). Thus the decision also follows from observed transfer, not only the stricter published-inventory accounting.

These are asteroid textures, as the P1051 report also describes, rather than sky dome backgrounds. Geometry and CVH must remain resident regardless of the visual delivery policy.

**Behaviour decision for Domingo:** Should the 14 MiB cap be raised to fit original entry-sector surfaces, or may those entry-sector asteroid textures stream after launch (rendering the existing solid-lit asteroid fallback until ready)? For this recorded representative map, the requirements demand both original entry visuals before the ticket and a transfer cap below the measured transfer under the existing gzip/Brotli delivery formats. No cap, gameplay residency rule or source bytes have been weakened. The recorded map is the existing P1051 standard comparison map. No entry residency policy or budget was changed without this decision. The remaining sector-pack implementation is blocked on that choice, not on a restore or build problem.

## Runtime and Chromium evidence

Baseline admission harness: **9/9 passed** in Chromium 153.0.8010.0, against the unchanged P1051/P1053 browser generation. [Sanitized receipt](BROWSER_SECTOR_PACKS_P1054_assets/baseline-security.json): route absence, public-WSS loopback refusal, list retry, wrong audience, expiry, fresh-ticket retry, replay, stale cookie and Back cleanup. These are baseline results, not qualification of unimplemented sector streaming. Headless Chromium timings are not GPU-representative.

Baseline P1053 harness: **Passed**, all five cold/warm/style/size profiles, including the production gamepad and pacing exercise. [Sanitized receipt](BROWSER_SECTOR_PACKS_P1054_assets/baseline-polish.json).

The initial P1051 baseline failed the synthetic hidden/audio assertion (`tools/p1051-browser-flight.cjs:121`). A focused Chromium trace reproduced the pending-resume race described above. All five new regressions fail against the original source; the fix passes those five cases three times alone and all 19 JS cases three times. The fresh generation also passes **8/8** repeated Chromium visibility cycles at the unchanged 100 ms checkpoints. [Before trace](BROWSER_SECTOR_PACKS_P1054_assets/audio-race-before.json), [after trace](BROWSER_SECTOR_PACKS_P1054_assets/audio-race-after.json).

**Final P1051 harness: 4/4 profiles passed, with its original hidden/audio assertion unchanged.** [Full receipt](BROWSER_SECTOR_PACKS_P1054_assets/flight-final.json). These remain legacy-pack regressions, not P1054 streaming qualification.

| Profile | Cache case | Pack transfer bytes (incl. HTTP headers) | Frame p95 (ms) | Result |
|---|---|---:|---:|---|
| Classic 1920×1080 | cold | 7,929,876 | 16.7 | Passed |
| Modern 1920×1080 | warm | 0 | 16.7 | Passed |
| Classic 800×480 | stale | 7,706 | 16.8 | Passed |
| Modern 800×480 | storage-refused | 7,929,876 | 16.7 | Passed |

Every profile passed real cross-client visibility and shots, keyboard/mouse/group-fire/mount controls, physical pointer lock/fullscreen, blur/hidden release, exactly one same-ship WS resume surviving a full minute, authoritative docking and deliberate LOGOFF without a zombie. Warm fetched zero pack bytes; stale fetched one file; storage-refused retained verified visit-memory files across the injected 503 and same-page retry before a fresh ticket. Stale mission-cookie refusal preserved the server ship registry, and actual WebGL loss showed the reload explanation. Final security passed **9/9**; final P1053 passed all five profiles with all five pacing-scale observations, real server gamepad/key controls and viewport/buffer checks. [Security](BROWSER_SECTOR_PACKS_P1054_assets/security-final.json), [polish](BROWSER_SECTOR_PACKS_P1054_assets/polish-final.json).

The lane initially lacked Chromium library/font environment variables. Reusing the installed native libraries and the repository's bundled DejaVu fonts fixed launch and empty-input fixture failures; no package, production file or identity rule changed.

## Desktop and verification gates

Baseline solution build: **Passed, 0 warnings / 0 errors**, including both browser projects. Release browser publish: **Passed**. All package restores were offline from installed packages, with HTTP cache redirected into ignored task scratch.

| Check | Passed | Failed | Skipped |
|---|---:|---:|---:|
| Core P1048/P1048B/P1052 selection | 207 | 0 | 0 |
| Lobby P1048/P1048B/P1052 selection | 49 | 0 | 0 |
| Browser game project | 59 | 0 | 0 |
| Browser head project | 12 | 0 | 0 |

Desktop HUD comparison passed **1,848/1,848**, with **0 pixel differences, 0 semantic differences, 84 animation-only regions and 0 corrupt goldens**. Desktop Debug filenames are **753/753 identical** (no additions/removals). Both final desktop recordings completed. The standalone tools gate passed **all 148 projects**, with 0 warnings / 0 errors. New Python analysis tests passed **4/4 ×3** in isolation; final browser input/pacing JS tests passed **19/19 ×3**, including **5/5 new race cases ×3 alone** and publish Python tests **4/4 ×3**. The single final Fast sweep was the last broad gate; its result and required isolated reruns are recorded below.

## Final Fast gate

**Failed:** 15,533 passed / 4 failed / 6 skipped, **15,543** total, **881.6 seconds**. All **9** infrastructure checks passed, calibrated case limit **11.532 s**, zero timing offenders and zero quarantine flakes. Each exact failing method passed **3/3 alone** in fresh processes with artwork (**12/12** isolated invocations). The four signatures are existing remote-death receipt, wingman defend acquisition, NPC obstacle clearance and roster-clock families; no new cause is inferred. [Summary](BROWSER_SECTOR_PACKS_P1054_assets/fast-summary.json), [failures](BROWSER_SECTOR_PACKS_P1054_assets/fast-exceptions.json), [isolated repetitions](BROWSER_SECTOR_PACKS_P1054_assets/fast-isolated.json). The matching observations are appended to `docs/TEST_FLAKES.md`. Passing isolations do not turn the full gate green.

## Images

No P1054 streaming captures yet: runtime streaming awaits the budget decision. Baseline harness images remain in ignored scratch and are not presented as before/after sector-streaming evidence. PNGs in this report: **0 bytes**.

## Unfinished work

Item 0 is complete with final unattended evidence. Items 1–3 await the entry-visual/budget decision. Item 4 has the passing baseline checks listed above and the red final Fast result; no new streaming qualification is claimed. Training is not required by the current source changes: no shared Game1 or rendering file changed.

## Working-tree disposition

All changes are uncommitted; no pushes or stash operations were performed. The brief is preserved in ignored task scratch and is absent from the change set. No packages were added or fetched; all requests were loopback-only. No new app/service/store/listener/environment setting was introduced, so compose changes and Docker validation are not applicable (Docker is not installed in the lane). All started proof hosts, proxies, browser sessions, desktop windows, build/test processes and isolated test processes ended through their owned handles, graceful stop files or exact child process handles. Raw logs, wire recordings, package HTTP cache and baseline screenshots remain in ignored task scratch. Public evidence contains names/hashes/counts and no artwork payloads, credentials, personal paths or new PNGs. [Verification accounting](BROWSER_SECTOR_PACKS_P1054_assets/verification.json).

## Native intent

Reuse the existing native game rules, authoritative data and ordinary warp travel. No new simulation or wire rule is ported by the recorder or analysis. Waypoint creation reuses `WinTrek/console.cpp:1940-1972` and `clintlib/clintlib.cpp:2460-2469`; normal desktop Game1 prediction and autopilot drive the actual crossings.

## Modern approach

Pending the entry-visual decision, the intended next step is verified, content-addressed visual delivery per sector without waiting in simulation, input or prediction. The desktop asset path stays unchanged. The implemented browser audio fix similarly keeps asynchronous platform work outside simulation and applies the latest lifecycle intent after pending operations settle.

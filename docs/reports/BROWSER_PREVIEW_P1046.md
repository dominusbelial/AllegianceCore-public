# P1046 — Allegiance browser preview

Date: 2026-11-16. Baseline: `1dcf757f`. Slice 1 is implemented as a default-off,
independently published KNI preview at `/play/preview/`. All authored changes remain
in the working tree. No commits, pushes, stash operations, deployments or remote game
connections were made. The brief is retained in ignored task scratch, outside the change set.

## Numbered brief checklist

1. [x] **Separate browser project.** net10.0, KNI 4.3.9001, single-threaded WASM,
   trimming enabled, solution registration. Plain SDK 10.0.404 has **no installed
   workloads** and builds/publishes successfully. No `WasmEnableThreads`, AOT or CI
   workload addition. Browser references Core only; **no Game assembly or existing
   shared Game source is linked**. Started from P1045's archived render spike.
2. [x] **Preview content.** Original `fig05` scout and texture, procedural star sky,
   slow turntable, rotate/zoom buttons, exact requested header, loading progress,
   Retry and pre-bundle capability fallback. A single hull needs no next-hull selector.
   No flight, joining, identity, audio, HUD, pointer lock or server transport.
3. [x] **Verified async artwork.** Versioned manifest records relative fingerprinted
   path, source filename, byte size, SHA-256 and MIME. Bounded asynchronous reads and
   hash checks finish before parser/GPU use. Original artwork bytes are **not in the
   repository or image**. Lobby reads only manifest-listed files from its configured
   Artwork root; invalid hashes, names and symlinks fail closed. MIT FreeAllegiance
   attribution appears on every state and the served notice.
4. [x] **Optional lobby hosting.** `BrowserPreview:Enabled` defaults false. Flag off
   installs no preview middleware and reads no bundle/artwork. Flag on supplies exact
   routes, MIME, compression negotiation, ETags/cache policy, scoped CSP and 404s.
   The API limiter and existing security middleware remain in force for APIs.
5. [x] **Packaging and desktop isolation.** Generation-directory publish script,
   browser Dockerfile and optional compose exporter; lobby mounts bundle/art read-only.
   Existing desktop/Android project/content/source diffs are empty. Desktop Debug output
   lists are identical before/after: **753 files**. Docker is absent: compose config and
   image execution are explicitly **Skipped**, not claimed validated.
6. [x] **Tests.** 12 pure browser cases and 16 lobby preview cases each pass **3/3
   in isolation** on the final source. The fast runner includes the new test project.
   Full gate status is recorded below.
7. [x] **Local real-browser evidence.** Chromium 153.0.8010.0 at 1920×1080 and
   800×480; loading, loaded, fallback and error PNGs, cold/warm traffic and CPU timings.
   Every final image was opened and inspected. Firefox launch was attempted but its
   executable is unavailable; see limitations. Preview lobbies and browsers are stopped.
8. [x] **Glossary/parity.** Browser project, flag and manifest documented in
   `GLOSSARY.md`; glossary link check passes (914 paths). No parity CSV changes:
   no gameplay rules were ported. Server/Core/desktop/Android source is untouched.

## What changed (source references)

| Area | Files and entry points |
|---|---|
| Browser build boundary | `src/Allegiance.ClientCore.Browser/Allegiance.ClientCore.Browser.csproj:3` enables trimming and pins KNI; `:39` references Core; `Program.cs:15` registers the three used KNI factories explicitly, avoiding reflection discovery. `AllegianceCore.slnx:3` registers the head. |
| Scene and controls | `src/Allegiance.ClientCore.Browser/PreviewGame.cs:19` creates reusable stars; `:24` bounds button controls, `:34` uploads existing MDL geometry/texture, `:56` draws the turntable without file/network I/O. `Pages/Index.razor:8` presents loading/error/controls; `Pages/Index.razor.cs:23` stages and retries. |
| Asset contract | `src/Allegiance.ClientCore.Browser/Assets/PreviewAssets.cs:9` validates the manifest; `:30` checks size/hash; `:44` owns async staging and a platform request hook. `wwwroot/asset-manifest.json:1` allow-lists just `fig05.mdl` (46,742 bytes) and `fig05bmp.mdl` (131,188 bytes). |
| Shell/fallback | `src/Allegiance.ClientCore.Browser/wwwroot/index.html:1`, `preview.css:1`, `preview.js:37` provide the responsive shell, safe text-only failure UI and Retry. `preview.js:51` checks WebAssembly/WebGL before framework downloads. |
| Lobby flag and isolation | `src/Allegiance.Lobby/BrowserPreview/BrowserPreviewSettings.cs:5` defaults off; `src/Allegiance.Lobby/Program.cs:28` binds settings and `:106` installs hosting after original security/body checks, before the unchanged limiter. `BrowserPreviewHosting.cs:22` builds the closed registry; `:67` serves it. |
| Packaging/licences | `scripts/publish-browser-preview.sh:5` publishes to a fresh sibling staging directory and atomically renames the complete wwwroot; it refuses existing destinations. `src/Allegiance.ClientCore.Browser/Dockerfile:1` exports the bundle without artwork. `docker-compose.yml:22` defaults the flag off; optional exporter uses a profile. `src/Allegiance.Lobby/Dockerfile:24` copies the linked contract source. The browser csproj publish target appends the actual runtime/Blazor notices, including their upstream text, to the served framework notice. |
| Tests/proof | `tests/Allegiance.ClientCore.Browser.Tests/PreviewAssetTests.cs:9`, `tests/Allegiance.Lobby.Tests/P1046BrowserPreviewTests.cs:14`, test-host settings in `LobbyTestApp.cs:28`; `tools/p1046-browser-proof.cjs:1` reproduces browser metrics/states/security assertions. `tools/test-tiers/runner.py:18` includes browser tests. |
| Operator/index docs | `docker/README.md:418` documents local/compose setup, artwork mounts, default-off behavior and stopped-lobby upgrades; `GLOSSARY.md:32` indexes the preview boundary. |

All short browser filenames in this table are relative to the browser project; short
hosting filenames are relative to its lobby `BrowserPreview/` directory.

## Design decisions

**Build and rendering.** KNI is confined to the browser head. The unchanged
`MdlFile.Parse` / `MdlTextureDecoder.Decode` produce two meshes, **526 vertices /
453 triangles**, and the original **256×256** texture. BasicEffect provides the
LDR lighting; 280 deterministic stars supply the sky. CPU arrays are prepared before
Draw. No desktop content builder/output or Android evaluation is altered. The only
linked source is the **new** manifest/loader source, compiled into lobby and pure tests
to keep validation identical without referencing a WASM assembly.

**Loading and retry.** The tiny HTML/JS shell checks for both required capabilities
before requesting interop/runtime/artwork. It shows runtime-component progress, then
byte-based verified-artwork progress. A manifest accepts at most eight entries, each
at most 4 MiB, only canonical lowercase single `.mdl` source names, unique paths/names
and complete lowercase SHA-256 fingerprints. Staging publishes no partial dictionary.
Retry starts a new attempt and uses browser `cache: reload` for manifest/art requests,
so corrupt immutable cached bytes cannot trap the user. Runtime promise failures,
global module errors, CSP violations and a 120-second initialization deadline all
lead to the Retry UI. Renderer errors also stop the RAF loop and offer Retry.

**Artwork exposure.** The manifest URL is `asset-manifest.json` below the preview
base. Art URLs contain their full SHA-256; their source names are never accepted as
arbitrary HTTP paths. At startup the lobby verifies actual source size/hash and
rejects file links. It holds only this small verified set and its compressed variants
in memory. Bundle files come from a closed startup registry; symlink directories,
hidden files and direct `.br`/`.gz` URLs are excluded. Neither a directory listing nor
an index fallback exists. Raw percent/backslash/dot-segment paths, including mixed-case
preview prefixes normalized by Kestrel toward `/api`, return 404. No path ever opens
an artwork file supplied by a request.

**CSP.** Only preview responses use `default-src 'none'` with self-hosted scripts,
styles, images and connections, `base-uri 'self'`, no objects/forms/framing, and
`script-src 'self' 'wasm-unsafe-eval'`. The base allowance is needed for the scoped
`<base href="/play/preview/">`. There are no inline scripts, wildcard sources,
`unsafe-eval`, external/CDN hosts, workers or isolation headers. The browser proof
removes only `wasm-unsafe-eval` from a test HTML response: .NET WASM compilation is
blocked and Retry appears. This demonstrates the narrow allowance is required by
this running KNI/.NET target; normal captures show no CSP errors. Ordinary lobby/API
responses retain their original CSP exactly.

**Compression/cache.** `.wasm` is `application/wasm`. SDK-published `.br`/`.gz`
representations are chosen by Accept-Encoding quality, preferring Brotli on ties;
identity remains available and unacceptable representations return 406. Each response
has the original MIME, a single Content-Encoding and `Vary: Accept-Encoding`; no
response-compression middleware was added. Ten-character .NET fingerprinted runtime
filenames and full-hash art use `public,max-age=31536000,immutable`. Launch HTML,
manifest and other unfingerprinted files use `no-cache`, with representation-specific
strong ETags, conditional 304 and HEAD support. Runtime licence text is appended after
publish, so stale precompressed copies of that notice are deleted; its plain response
revalidates. The generation script does not replace an active directory. Select a new
`BrowserPreview:BundlePath` and restart; keep old directories for rollback. Stop the
lobby before updating the optional compose export volume.

**API/security boundaries.** New static serving is **after** forwarded-header trust,
authentication, security headers and the 16 KiB body gate, and **before** rate limiting.
Only the closed preview namespace is handled. `RateLimiting.cs`, API handlers,
Origin/CSRF checks, Kestrel body limits and proxy settings are unchanged. Tests spend
150 static requests, then obtain exactly 120 successful API reads and a 429; static
files still work after API exhaustion. Named identity permits, rejected cross-origin
writes, oversize bodies and untrusted forwarded addresses behave the same with the
flag both off and on. Missing `/api` paths never receive preview HTML.

## Verification

| Check | Result |
|---|---|
| Plain SDK solution build | **Passed** — 0 warnings / 0 errors; SDK 10.0.404, no workloads. Final build 11.22 s. |
| Trimmed Release publish and generation script | **Passed** — original hull drawn from the published bundle. SDK's optional wasm-tools recommendation is informational; no workload was installed. |
| Standalone tools | **Passed** — `tools/build-tools.sh --drop-bins`, all 140 projects, 0 warnings/errors. |
| Pure browser tests | **Passed** — 12 cases × 3 isolated processes; corrupt hash/cache, fresh retry, MIME/size/missing/malformed data, traversal/duplicate/bounds/fingerprint validation. |
| Lobby preview tests | **Passed** — 16 cases × 3 isolated processes; off/on, entry redirects, MIME, Brotli/gzip quality/no double compression, ETag/cache/CSP, HEAD, unknown/traversal/allow-list/symlink/hash rejection, API budgets/security. |
| Full Fast tier | **Failed** — 14,953 cases / 577.2 s: 14,944 passed, 2 failed, 7 not executed; 9 infrastructure checks passed. Three passing cases exceeded the calibrated 11.551 s limit. All new/browser/lobby cases passed. See isolation/ledger below. |
| Chromium evidence/security proof | **Passed** — licence links navigate to complete served documents; both sizes; loading, hull, fallback, corruption and Retry recovery; controls alter canvas; no WASM/WebGL means no bundle; restrictive CSP fails as expected; five raw traversal variants return 404. |
| Desktop output and source isolation | **Passed** — before/after relative file lists identical (753 files); csproj/content/desktop/Android/Core/server/parity diffs empty. |
| Glossary / whitespace / script syntax | **Passed** — 914 real glossary paths; `git diff --check`; `bash -n` publish script. |
| Docker compose config and images | **Skipped** — Docker executable is not installed. Compose/Dockerfile edits are provided but not claimed runtime-tested. |
| Firefox evidence | **Skipped** — no system/cached Firefox; Playwright launch reports missing executable. Browser-CDN downloads are outside this lane's NuGet/npm/docs internet allowlist. No Firefox compatibility claim. |
| Server AOT / training gates | **Skipped (not applicable)** — server and training are untouched. |

The lane's default NuGet HTTP cache is read-only. Builds used an ignored writable
HTTP cache and package cache (existing installed packages reused), preserving audit
checks. Builds used `--disable-build-servers -nodeReuse:false -p:UseSharedCompilation=false`;
the tools loop used a task-local dotnet wrapper to supply the same flags. The fast
gate uses `--no-build` after the complete solution build. No timing threshold, tier
classification, assertion or existing gameplay rule was relaxed.

## Full-fast failures and isolation

The full sweep remains **FAIL**. Core failures match existing ledger signatures:
`P739TimerLiveTests.Warp_bomb_notice_matches_captured_blast_deadline_and_stays_in_mission(delay: 7)`
measured `7.0000000000000009` against an inclusive 7-second upper bound, and
`GameOverPlayersWireTests.Rejoined_player_accumulates_time_across_stints_in_roster`
measured `8.45000076` against `[4,8]`. Three passing cases exceeded the actual
**11.550928 s** calibrated limit: SC2 support/civ 25 (14.929 s), P814 turret carrier
(14.675 s), P999 profile/civ 5 (15.018 s). The runner's generic error text says
“10 second limit”; its recorded calibrated threshold is 11.551 s.

Each exact failed/slow display case ran **3× alone**, one process per case. The timer
passed 3/3. The roster case passed / failed / passed, with the isolated failure
measuring `8.19999981` outside `[4,8]`. SC2, P814 and P999 each passed 3/3 at
2.939–3.176 s, below the calibrated limit. The roster remains intermittent even
alone; the observations do not establish a root cause. Results are recorded in [isolated-tests.json](BROWSER_PREVIEW_P1046_assets/isolated-tests.json)
and entered in [the flake ledger](../TEST_FLAKES.md). Core/server/game code,
existing assertions, tiers and timing thresholds are unchanged. Isolated passes do
not turn the full gate green. [Full fast summary](BROWSER_PREVIEW_P1046_assets/fast-summary.json).

## Browser measurements

Published Release bundle, fresh browser/context per viewport, local HTTP with actual
negotiated Brotli; browser web security/CSP remain enabled. Headless Chromium
**153.0.8010.0**, Playwright **1.65.1**, npm `@sparticuz/chromium` **153.0.0**. The
lane needed the npm package's bundled al2023 libraries and fonts configured locally;
no OS packages or external browser download was used. Renderer: **ANGLE / SwiftShader
software Vulkan**. The reported HTTP bytes come from CDP encodedDataLength, including
protocol overhead, rather than extrapolating the on-disk bundle.

| Viewport | Cold transferred bytes / requests | First submitted frame | Steady CPU Tick median / p95 / p99 | RAF median / p95 | Warm transferred bytes / first frame |
|---|---:|---:|---:|---:|---:|
| 1920×1080 | 3,634,480 / 93 | 742.5 ms | 0.90 / 1.40 / 2.00 ms | 16.70 / 16.70 ms | 9,525 / 387.0 ms |
| 800×480 | 3,634,480 / 93 | 601.3 ms | 0.90 / 1.50 / 2.00 ms | 16.70 / 16.80 ms | 9,525 / 390.7 ms |

All **93 cold responses use Brotli**. Warm reload emits 93 logical request events,
with **74 from disk cache**; the remaining requests revalidate and transfer only
**9,525 bytes** in total. No service worker is involved. Tick timings bracket synchronous
JS→managed `Game.Tick` command submission, 600 samples after 60 warm-up frames. They
are **not GPU execution times**. First frame is navigation-origin time to the first
successful managed draw submission, not a presentation timestamp. This is a local,
one-hull viewer measurement, with no claim about WAN startup or game-scale performance.
Build/test/isolation and media receipt: [verification.json](BROWSER_PREVIEW_P1046_assets/verification.json).
Raw traffic resources, timings and security results: [browser-metrics.json](BROWSER_PREVIEW_P1046_assets/browser-metrics.json).

## Reviewed screenshots

All eight **final** PNGs were opened and visually inspected, **579,997 bytes total**
(below 2 MB). They contain reviewed original FreeAllegiance scout imagery, no private
content. No original MDL/texture binary is in the change set. Loading is captured by
holding an actual runtime request; fallback disables a capability; error flips one
byte in an intercepted same-length response. These exercise production UI, without
adding test-only product switches.

| State | 1920×1080 | 800×480 | What is actually visible |
|---|---|---|---|
| Loading | [PNG](BROWSER_PREVIEW_P1046_assets/chromium-loading-1920x1080.png) | [PNG](BROWSER_PREVIEW_P1046_assets/chromium-loading-800x480.png) | Dark page, requested header/subtitle, centered blue loading panel, progress bar and “Downloading C# / WebAssembly…”; licence links at bottom. No hull yet. |
| Loaded | [PNG](BROWSER_PREVIEW_P1046_assets/chromium-loaded-1920x1080.png) | [PNG](BROWSER_PREVIEW_P1046_assets/chromium-loaded-800x480.png) | Fully visible grey/olive scout, yellow-striped fins and orange texture details against sparse stars; four readable rotate/zoom buttons, header and notices. At 800×480 the hull sits just above controls. No HUD or other ships. |
| Fallback | [PNG](BROWSER_PREVIEW_P1046_assets/chromium-fallback-1920x1080.png) | [PNG](BROWSER_PREVIEW_P1046_assets/chromium-fallback-800x480.png) | “Browser preview unavailable”, explicit WebAssembly/WebGL requirement and desktop alternative, Retry, header and licence footer. No framework/art downloads; just three shell requests. |
| Error | [PNG](BROWSER_PREVIEW_P1046_assets/chromium-error-1920x1080.png) | [PNG](BROWSER_PREVIEW_P1046_assets/chromium-error-800x480.png) | “Preview could not load”, artwork verification failure naming fig05.mdl, Retry; disabled scene controls below. Clicking Retry after restoring bytes reaches the textured hull at both sizes. |

## Reproduction and disposition

Local publish: `scripts/publish-browser-preview.sh OUTPUT_DIRECTORY` (new generation).
Run the lobby with `BrowserPreview__Enabled=true`, `BrowserPreview__BundlePath` set
to that directory, `ALLEGIANCE_ARTWORK_PATH` set to the existing checkout, and a
loopback `ASPNETCORE_URLS`. Identity test files belong in ignored scratch. Operator
and compose details are in [docker/README.md](../../docker/README.md).

For the browser proof, install the two pinned npm packages into `.scratch/p1046/npm`,
prepare the npm browser's supplied libraries/fonts if the host lacks them, and run
`tools/p1046-browser-proof.cjs` against the loopback lobby. The proof restricts network
through a loopback-only proxy bypass and closes all browsers in finally blocks.
All preview listeners started here have been stopped; no rig, mini, Mac, VPS, public
lobby or remote game server was contacted. The required fast suite uses its own loopback test hosts. Scratch retains only ignored local build/test
artifacts, package caches, the brief and raw logs. No commits or pushes were made.

Remaining qualification: Firefox rendering and Docker image/compose execution need
a lane with those executables. The flag remains **off by default**. No broader browser
game migration, login, network joining or deployment is included.

## Native intent

Reuse the original Allegiance scout and existing managed MDL/RGB565 parsers, with
no new gameplay rule, authority decision or wire behavior. Core and native parity
ledgers stay unchanged; this slice has no new native rule requiring a CSV row.

## Modern approach

A small single-threaded KNI head, async hash-verified artwork and a tightly scoped,
default-off lobby host make the browser preview independently deliverable. Desktop
and Android remain separate MonoGame outputs; later playable slices retain their
own explicit acceptance gates.

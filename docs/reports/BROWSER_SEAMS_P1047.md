# P1047 — Browser game seams

Dates: 2026-11-16–17 UTC. Baseline: `7b9a724d5619`, branch `codex/p1047`.
Implementation and Chromium acceptance are complete. Qualification ended within
the eight-hour cap; the Fast gate fails on recorded legacy signatures, and three
complete native training selections were not achieved. Changes remain in the working tree. No commits, pushes, stash operations,
deployments or remote game connections were made. The brief is retained in ignored
`.scratch/p1047/`, outside the change set.

The browser runs the shared **Game1 intro and Options handlers/drawing**, with an
intro-only KNI lifecycle and resident assets. This is a source-compiled variant,
not a replacement HTML menu or the MonoGame desktop DLL running against KNI.
Desktop output filenames are identical, and the 1,848-case HUD comparison has
zero pixel/semantic differences outside the fixture's documented animation masks.

## Numbered brief checklist

1. [x] **Browser compilation.** Separate Game.Browser and Core.Browser libraries,
   KNI 4.3.9001, net10.0, no additional workload. The solution and Release browser
   publish build with zero warnings/errors. Shared intro/Options source is selected
   with browser lifecycle/platform files; both HUD renderers and presentation
   helpers compile against KNI. Connection/flight/offline orchestration remains
   excluded, as required for this slice.
2. [x] **Async assets/cache.** Version-2 path/size/SHA-256/MIME/provenance/dependency
   manifest, asynchronous bounded reads, reused P1046 integrity checks, atomic
   resident promotion, in-flight de-duplication and caller-owned cancellation.
   Cache API holds verified immutable bytes; IndexedDB holds bounded metadata and
   settings. Quota, eviction, corruption and aborted/partial-write recovery are tested.
3. [x] **Recorded pack and budgets.** Each actual desktop style records 892 opens
   while visiting Intro, Options and Game Options. Starter: 19 files, **2,561,241
   raw / 264,625 gzip bytes**. All 17 desktop font bakes, PCore001 binary and the
   native menu cue are included. Actual whole cold transfer is **4,629,148 bytes**,
   below 8 MiB. The projected join union exceeds 20 MiB; the measured proposed cut
   below is explicitly unshipped.
4. [x] **Platform seams.** Resident profile storage and asynchronous IndexedDB
   flush; browser navigation/download adapters; gesture-enabled menu audio;
   compile-time exclusions of session, offline host, native updater, raw
   transports/NAT, discovery, GL timer, SDL relative mouse, replay, narration
   decoder, screenshot worker and saved-password/DPAPI paths. No blocking
   constructs occur in the selected application source sets.
5. [x] **Real browser boot.** Separate `menu.html` entry in the existing head,
   staging before Game1 starts, real Options toggles and style preferences survive
   reload. Skirmish says “not available in the browser.” LDR only; all custom
   desktop effects are disabled with a logged reason. The original P1046 scout
   page, header and controls pass a separate smoke proof.
6. [ ] **Desktop/Android proof and training gate — incomplete training repeats.**
   Desktop/Android project/content files are unchanged; desktop Debug output is
   **753/753 identical filenames**. HUD: **1,848 cases, 0 pixel / 0 semantic
   differences**, 83 animation-only differences. All three headless training
   selections pass **126/0/0** each. One complete native selection is
   **290/2/2** (passed/failed/skipped); two additional native selections were
   interrupted at the task time cap before aggregate results. Both report only
   the already-ledgered Classic/Modern reminder failures; each exact case also
   fails 3/3 alone. Three complete Fast+Live project pairs remain unfulfilled.
7. [x] **Tests and gates.** Solution/tools builds and all isolated new tests
   pass. The completed Fast sweep is 14,973 passed / 2 failed / 7 skipped, with
   two timing offenders. Each non-task failure/offender runs 3× alone and is
   ledgered; the wingman case fails 1/3 alone. Training accounting remains item 6.
8. [x] **Browser evidence.** Chromium at 1920×1080 and 800×480, Classic/Modern
   intro/Options/persisted toggle, loading, corrupt asset with successful Retry,
   unavailable skirmish, cold/warm traffic, CPU tick and managed heap. Screenshot
   downloads contain the drawn canvas rather than a discarded black buffer.
   Firefox launch was attempted; its executable is absent.
9. [x] **Index/parity.** Glossary entries cover both source-compiled variants,
   staging and storage; 929 glossary paths validate. Parity CSVs and native
   simulation/wire rules are unchanged.

## Source and platform boundary

| Area | Implementation / source references |
|---|---|
| Compiled variants | `src/Allegiance.ClientCore.Game.Browser/Allegiance.ClientCore.Game.Browser.csproj:3` selects 595 source files, including ClientCore state and the shared presentation code; `src/Allegiance.Core.Browser/Allegiance.Core.Browser.csproj:3` selects 386 Core sources. OS transports, process ownership and console-drain workers are excluded. The ordinary Core project/source is unchanged. |
| Real shared intro | `src/Allegiance.ClientCore.Game/Game1.IntroMenu.cs:10` is the existing main-menu key handler; `:21`, `:32`, `:107` are the existing title, main-menu and Single Player chooser drawing, moved from the desktop partial. Native citations remain with the existing models/methods. |
| Real shared Options | `src/Allegiance.ClientCore.Game/Game1.OptionsMenu.cs:32` retains the existing tree/commands; `:305` retains the renderer. Desktop sharing/chat/session side effects remain in `Game1.BenchmarkSharing.cs`; optional partial hooks and narrow audio interfaces select browser behavior. |
| Browser lifecycle | `src/Allegiance.ClientCore.Game.Browser/Game1.Browser.cs:11` constructs Game1 without a session/offline host, loads resident XNB/binary-core bytes, drives the shared menu and shows unsupported-feature screens. Opening Enter is latched before the Options page owns input, avoiding a second activation on the next browser RAF. |
| KNI API adaptation | `src/Allegiance.ClientCore.Game.Browser/Platform/KniCompatibility.cs:8` adapts glyph access and manual Model/Mesh/Bone construction through explicit .NET accessors pinned to KNI 4.3.9001. Both existing HUD renderers compile unchanged. `Rendering/TextureMipmapBuilder.cs:30` uses positional ctor arguments; `Rendering/StarmapRenderer.cs:158` explicitly copies Numerics X/Y into XNA vectors. |
| Asset verification/staging | `src/Allegiance.ClientCore.Browser/Assets/PreviewAssets.cs:35` and `:43` supply the shared SHA/size check and bounded async reader. `src/Allegiance.ClientCore.Game.Browser/Assets/StagedManifest.cs:24` validates v2; `Assets/AssetStager.cs:18`, `:40`, `:68` own manifest read, promotion and shared downloads. `Assets/StagedAssetSource.cs:10` serves immutable resident streams only. |
| Content and binary core | `src/Allegiance.ClientCore.Game.Browser/Assets/ResidentContentManager.cs:7` overrides KNI's stream opener; `Assets/AssetSources.Browser.cs` avoids desktop asset-source initialization. PCore001 is parsed from resident bytes; `TextCoreAssets.cs` is absent from this variant. |
| Profile abstraction | `src/Allegiance.ClientCore.Game/Platform/ProfileStorage.cs` selects direct System.IO on desktop/Android and resident storage in the browser. `Platform/ResidentProfileStorage.cs` permits only four non-secret preference documents, validates import atomically and rejects credential fields. `PlayerConfig.cs:21`, `IntroPreferences.cs:138`, `Rendering/HudSettings.cs:164` retain the original entry-point signatures and add injected-store overloads. |
| Keymap / trimming | `Rendering/KeyBindingStore.cs:58` preserves the parameterless constructor and default path semantics; an explicit store/path supports browser and isolated tests. `Rendering/InputBindings.cs:359` uses generated metadata only on BROWSER for the identical JSON contract. `Platform/BrowserProfileJson.cs` supplies browser profile metadata. |
| Browser persistence/cache | `src/Allegiance.ClientCore.Browser/Pages/Menu.razor.cs:29` stages before construction; `:88` serializes async settings flushes. `wwwroot/game-platform.js:70` owns the bounded cache, `:151`/`:155` read/write settings, `:160`/`:165` supply navigation/downloads. No token/password store is available. |
| Input/capture/audio | `wwwroot/menu.js:3` delivers browser text input without a desktop Window event, preserves native Tab focus and provides Shift+Tab access to the HTML toolbar; the focusable canvas follows that toolbar in document order. The pilot character filter calls the shared `IntroScreenModel.TypesPilotName` policy. `Pages/Menu.razor.cs:106`/`:129` captures within the same RAF submission after Draw, before WebGL discards its buffer. `Platform/MenuAudio.cs` loads only the resident native click cue after a real user gesture; the head explicitly registers KNI's audio factory. |
| Generators/proof/packaging | `tools/p1047-menu-opens/Program.cs` records production desktop opens; `tools/p1047-browser-packs.py` verifies those sources and remeasures join candidates. `scripts/publish-browser-menu.sh` publishes a new generation atomically; `tools/p1047-static-server.py` binds loopback only; `tools/p1047-browser-proof.cjs` drives actual browser keys and production UI. The existing head Dockerfile adds only the four required source COPY lines. |

Short `Rendering/`, `Platform/` and desktop `Game1.*` paths in this table are
relative to `src/Allegiance.ClientCore.Game`; short browser-variant `Assets/` and
`Platform/` paths are relative to `src/Allegiance.ClientCore.Game.Browser` where
identified. Full evaluated compile lists and conditional locations are retained
in [source-audit.json](BROWSER_SEAMS_P1047_assets/source-audit.json).

The project-selected lifecycle excludes desktop `OnDeactivated`/`OnExiting`,
window positioning and desktop text-input subscriptions rather than emulating
those APIs in KNI. The glyph adapter supplies the missing Character alongside
KNI's internal glyph array; the stand-in adapter supplies KNI's internal model
constructors/setters. These model/HUD adapters are compile-qualified here; this
slice does not claim browser flight rendering parity.

## Every conditional and selected seam

There are **12 BROWSER-related conditional blocks**, all inventoried with exact
line numbers in the source audit:

- `Platform/ProfileStorage.cs`: select the default store; exclude the physical
  file implementation entirely in the browser (two blocks).
- `IntroPreferences.cs`: omit skirmish preferences and select generated browser
  JSON metadata (two blocks).
- `PlayerConfig.cs`: select generated browser JSON metadata (one block).
- `Rendering/InputBindings.cs`: generated browser map writer, metadata and reader;
  desktop keeps its existing JSON implementation (three blocks).
- `Game1.OptionsMenu.cs`: omit benchmark hotkey/panel and unavailable directory-chat
  state (three blocks).
- `Rendering/ShipStandInModel.cs`: select the pinned KNI construction adapter
  (one block).

All other platform differences use project-selected files or interfaces: browser
Game1 lifecycle, Core console logging, source/container initialization, resident
ContentManager, transport preference enum only, menu audio, storage, async cache,
text input, navigation and capture. The selected sources contain **zero**
`GetAwaiter().GetResult`, `.Wait(`, `new Thread`, `ManualResetEvent` or `Task.Run`
constructs. Original narration decode, career remote-save wait, discovery worker,
capture worker, session keepalive and raw transport workers are not selected.

The browser runtime still uses the ordinary synchronous `IAssetSource` interface,
but its only implementation on the executed path is resident memory. Missing
reads throw `AssetNotResidentException` without starting IO; staging failures keep
loading/error/Retry UI above a stopped game. No HTTP, filesystem or JS-cache call
runs from the shared Draw methods. Browser focus events mask game input while
HTML controls own the keyboard and latch held keys when the canvas regains focus. Desktop `DirectoryAssetSource` is unchanged.

## Staging, cache and persistence design

V2 accepts at most 1,024 logical files, 16 MiB per file and 64 MiB total. Paths are
relative, content-addressed hashes; logical source names are case-canonicalized.
Same-content aliases are accepted, while case collisions with different content
or inconsistent hash metadata fail before staging. Every dependency must exist in
the same complete pack. Parsers/GPU construction begin only after the full verified
snapshot is promoted. A failed or canceled new generation cannot replace an
existing complete snapshot.

Concurrent callers share immutable downloads, while each waiter verifies its own
manifest's size/hash contract. Each caller can cancel its own wait;
the request is canceled when its last waiter leaves. Corrupt cached bytes are
removed and fetched again. Retry requests bypass the HTTP cache. Both HTTP reads
and persistent-cache reads are bounded; no partial download becomes resident.

Persistent bytes are capped at **64 MiB and 1,024 entries**, with a 2 MiB origin-quota
reserve and LRU eviction. Cache.put publishes a complete response before its
IndexedDB metadata commits. Recovery removes orphan responses/stale metadata;
failed writes remove their candidate and can be retried. Manifest metadata has
one current record. Persistence refusal does not discard verified memory bytes.

Profiles load asynchronously into a resident snapshot; synchronous shared-model
reads/writes touch only that snapshot. The head serializes async flushes and tracks
revisions so later edits are not lost behind an earlier write. Only `player.json`,
`hud.json`, `keymap.json` and `graphics.json` are permitted, each capped at
131,072 UTF-16 code units.
Temporary atomic-save names do not enter persisted snapshots. Nested password,
token, secret, credential and private-key fields are rejected in both C# and JS.
The initial HUD migration/default choice is saved on first boot, before a later
player-profile write can make a new profile look like a legacy one. The browser
proof retains Modern across that write/reload without clicking a style button.
Settings refusal is reported to the user; no saved-password/DPAPI or identity-client
implementation is compiled into the browser.

## Recorded starter pack, fonts and budgets

[Classic opens](BROWSER_SEAMS_P1047_assets/desktop-opens-classic.json) and
[Modern opens](BROWSER_SEAMS_P1047_assets/desktop-opens-modern.json) each record
**892 actual opens**, plus the visited Intro/Options/Game Options scenes. The
recorder wraps the production asset source and ContentManager stream opener;
it does not replace Game1 or seed renderer data. The real Options handler toggles
and saves Flip Y during each recording. The lobby URL is explicitly empty and no
game server is started or joined by the recording tool.

The generator selects every recorded SpriteFont open, recorded PCore001 binary and
the recorded native mouseclickSound closure, resolved from `sounddef.mdl:321`.
Desktop eagerly opens flight-only art at boot; those opens are recorded but deferred
by the intro-only browser lifecycle. The actual intro uses procedural fills and
font glyphs, so it has no menu bitmap to guess or ship. Dependencies are empty for
these self-contained bakes/data/cue files. Every source size/hash is checked against
the recording before generation.

All **17 original desktop XNB bakes** are used: HUD, HUDLarge, HUDModern and the 14
native Classic density bakes. KNI reads these existing bakes correctly; no new
content workload or font rasterization is required. HUD/HUDLarge contain NPOT DXT3
atlases: KNI's HiDef capability profile permits their dimensions and uses WebGL2
on this Chromium. The backbuffer is explicitly `SurfaceFormat.Color`; this does
not enable HDR. Font licence text accompanies every generated pack.

| Layer / target | Files | Raw bytes | Gzip bytes / actual transfer | Result |
|---|---:|---:|---:|---|
| Recorded starter assets | 19 | 2,561,241 | 264,625 gzip | Staged, verified and rendered |
| Whole cold menu, runtime + HTTP overhead | 110 requests | — | 4,629,148 transferred | **4.41 MiB < 8 MiB** |
| Full P1045 join-candidate union | 146 | 245,554,228 | 57,482,329 gzip | **54.82 MiB > 20 MiB**, measure only |
| Proposed deferred join cut | 110 | 42,255,180 | 13,260,123 gzip | **12.65 MiB assets**, unshipped proposal |

[Pack receipt and complete projected lists](BROWSER_SEAMS_P1047_assets/pack-receipt.json).
Join candidates are re-read/rehashed and recompressed on this lane; the old study's
font entries lacked hashes, so current desktop bake hashes are supplied. The full
union includes all-map skies, five high-resolution rock surfaces and alternative
TTF sources. The proposed cut keeps first-sector scenery and XNB fonts, defers the
other three groups, and leaves room for runtime bytes under 20 MiB. It still needs
capped/shared rock surfaces or tested emergency geometry and a real join dependency
closure. No join bytes are staged or claimed playable.

## Rendering and browser behavior

`menu.html` is separate from the unchanged P1046 entry. The existing scout header,
model, controls and v1 manifest remain intact; only the shared browser head gains
the new menu route and libraries. Local static generation supplies both manifest
versions. The lobby's existing v1 closed artwork registry is not broadened to serve
arbitrary game assets; this slice is qualified with the loopback static tool.

The browser keyboard proof types and saves the pilot field, ignores typing while
the field is unfocused, backspaces, enforces the existing 24-character limit and
reloads that value. Tab stays with native pilot/menu focus; Shift+Tab reaches the
HTML toolbar, and ordinary Tab returns from its final button to the canvas.
HTML controls own their keys; toolbar Enter cannot also activate the underlying
game menu. Held keys are latched on focus return, and typing resumes correctly.
This exercises actual trusted browser keyboard events, not a seeded profile.

The browser uses the same intro/Options drawing for Classic and Modern, as desktop
does. Their intro pixels are consequently identical except the browser style
indicator. Both density/font sets are loaded and the selected HUD preference
survives reload. Flight controls, background music, online features and offline
hosts return an unavailable screen instead of invoking desktop services. Replay,
login and joining have no browser entry path.

The logged disabled effect list is **LinearDepth, SoftParticle, Bloom, Volumetric,
DisplacedSphere, NativePbr, FsrEasu, FsrRcas and Tonemap**. Their desktop `.mgfxo`
bytes are not KNI/WebGL content, and no corresponding browser processor is created.
Only built-in SpriteBatch/preview BasicEffect render LDR. Desktop HDR/visual defaults
are unchanged. Audio starts only after “Enable menu sound”; it loads/plays the
resident native click XNB, with no narration decoder or native sound initialization.
An additional fresh Chromium gesture probe observes no AudioContext/buffer before
the button, then a **running 44,100 Hz context** and a **75.5 ms mono buffer** after
the real click, with no browser errors. This checks actual Web Audio startup, not
only the menu's enabled flag. The KNI registry can be inspected with
`nkJSObject.objectMap.filter(x => x instanceof AudioContext).map(x => x.state)`;
the receipt is [audio-proof.json](BROWSER_SEAMS_P1047_assets/audio-proof.json).

Navigation is an asynchronous-callable browser new-tab adapter with
`noopener,noreferrer`; sign-in itself is excluded. Screenshot saves use browser
Blob downloads. The capture is requested in UI input, then submitted immediately
after the next Draw in the same RAF, before WebGL discards its drawing buffer.
The proof checks the downloaded PNG's native background pixel and opens it for
visual review; no native capture thread or filesystem save is involved.

## Browser measurements

Chromium 153.0.8010.0 / Playwright 1.65.1 / npm Chromium 153.0.0. Browser web security
and scoped CSP remain enabled; software ANGLE/SwiftShader renders the canvas. Fresh
browser/context for each measured cold boot; Classic's preference is configured
through the real style button in a separate setup browser, then only settings are
restored. Cache bytes and residency metadata remain cold. Warm reload uses the
same context and persistent cache. The Live training gate also ran on the lane;
these are local qualification timings, not unloaded hardware benchmarks. The
final solution build overlapped the first Classic measurement; its 3,698.6 ms
startup sample is retained rather than selecting a quieter repetition.

| Viewport / style | Cold bytes / requests | First submitted menu frame | Tick CPU median / p95 / p99 | Managed heap after boot | Warm bytes / first frame |
|---|---:|---:|---:|---:|---:|
| 1920×1080 Classic | 4,629,148 / 110 | 3,698.6 ms | 0.90 / 2.00 / 4.10 ms | 15,743,120 B | 36,123 / 780.3 ms |
| 1920×1080 Modern | 4,629,148 / 110 | 1,227.4 ms | 0.90 / 1.50 / 2.50 ms | 15,754,392 B | 36,123 / 789.5 ms |
| 800×480 Classic | 4,629,148 / 110 | 1,058.9 ms | 0.80 / 1.40 / 2.10 ms | 15,743,024 B | 36,123 / 771.0 ms |
| 800×480 Modern | 4,629,148 / 110 | 992.6 ms | 0.80 / 1.40 / 2.00 ms | 15,754,392 B | 36,123 / 765.0 ms |

Warm boots issue **91 logical CDP requests: 69 disk-cache hits and 22 network
requests, zero pack-byte requests**; resident-cache bytes are SHA-verified
again before use. Network bytes use CDP encodedDataLength, including protocol
overhead, with actual Brotli runtime/gzip pack responses. CPU timings bracket
synchronous JS→managed Tick submission, 600 samples after 60 warm-up frames. They
are not GPU times. First frame is command submission, not a compositor presentation
timestamp; heap is `GC.GetTotalMemory(false)`, not total browser/GPU memory. Raw
resources/cache counts, RAF intervals and logs are in
[browser-metrics.json](BROWSER_SEAMS_P1047_assets/browser-metrics.json).

Firefox launch fails before page requests because the executable is absent. No
browser-CDN download was attempted; internet use stayed within NuGet/npm/docs.

## Reviewed captures

All **18 final PNGs** were opened individually and reviewed after the final
published-variant proof. They total **504,058 bytes** (492.2 KiB), below
the 2 MB limit. The shared native intro/Options drawing is identical between
styles; the selected style is visible in the browser control. At 800×480 the
intro rows use the production compact type size, and the full menu, pilot field,
keyboard hints and licence footer remain visible. The Game Options capture
retains “Flip Y: on” after reload. Both downloaded canvas PNGs were also opened;
they contain the actual title/menu/pilot drawing, without the HTML overlay.

| State | 1920×1080 | 800×480 | Visible content |
|---|---|---|---|
| Intro Classic | [PNG](BROWSER_SEAMS_P1047_assets/intro-classic-1920x1080.png) | [PNG](BROWSER_SEAMS_P1047_assets/intro-classic-800x480.png) | Teal ALLEGIANCE title, full centered production menu, highlighted Single Player, focused anonymous pilot field, keyboard hint and browser-only controls/licences. |
| Intro Modern | [PNG](BROWSER_SEAMS_P1047_assets/intro-modern-1920x1080.png) | [PNG](BROWSER_SEAMS_P1047_assets/intro-modern-800x480.png) | The same native intro drawing with Modern selected; no flight, account or fabricated game rows. |
| Options Classic | [PNG](BROWSER_SEAMS_P1047_assets/options-classic-1920x1080.png) | [PNG](BROWSER_SEAMS_P1047_assets/options-classic-800x480.png) | Production Options tree: Graphics, Sound, Game, Visuals and Controls; first row highlighted. |
| Options Modern | [PNG](BROWSER_SEAMS_P1047_assets/options-modern-1920x1080.png) | [PNG](BROWSER_SEAMS_P1047_assets/options-modern-800x480.png) | Same Options tree using the real shared handler/renderer. |
| Reloaded Classic toggle | [PNG](BROWSER_SEAMS_P1047_assets/persisted-classic-1920x1080.png) | [PNG](BROWSER_SEAMS_P1047_assets/persisted-classic-800x480.png) | Game Options after a full reload, with “Flip Y: on” retained. |
| Reloaded Modern toggle | [PNG](BROWSER_SEAMS_P1047_assets/persisted-modern-1920x1080.png) | [PNG](BROWSER_SEAMS_P1047_assets/persisted-modern-800x480.png) | Same persisted Game Options state in Modern. |
| Loading | [PNG](BROWSER_SEAMS_P1047_assets/loading-1920x1080.png) | [PNG](BROWSER_SEAMS_P1047_assets/loading-800x480.png) | Real staged asset request held; loading panel/progress, no submitted menu frame yet. |
| Corrupt asset | [PNG](BROWSER_SEAMS_P1047_assets/asset-failure-1920x1080.png) | [PNG](BROWSER_SEAMS_P1047_assets/asset-failure-800x480.png) | Verification failure names the actual altered font, offers Retry; restoring bytes and clicking Retry reaches the menu at both sizes. |
| Skirmish unavailable | [PNG](BROWSER_SEAMS_P1047_assets/skirmish-unavailable-1920x1080.png) | [PNG](BROWSER_SEAMS_P1047_assets/skirmish-unavailable-800x480.png) | “Skirmish is not available in the browser.” and ESC back, reached through the real Single Player chooser. |

## Verification and unfinished training qualification

| Check | Current result |
|---|---|
| Solution / browser publish | **Passed**, 0 warnings/errors; plain SDK 10.0.404, no installed workloads. CI's 10.0.403 pin is not changed. |
| Standalone tools | **Passed**, all 141 projects after the final profile ABI preservation. |
| New browser tests | **Passed**, 23 ×3 isolated processes, including cross-manifest shared-download size verification; 0 failed/skipped. |
| Desktop storage/ABI tests | **Passed**, all six storage/ABI cases ×3 isolated processes. |
| Existing P1046 tests | **Passed**, 12 ×3; unchanged preview smoke passes; lobby preview cases run in Fast. |
| JS production cache tests | **Passed**, 8 ×3, including bounded read, metadata count, quota, eviction, failed write recovery and settings rejection. |
| Desktop affected-seam regressions | **Passed**, 187 cases, no skips. |
| HUD golden comparison | **Passed**, 1,848, 0 pixel / 0 semantic / 0 corrupt golden differences; 83 animation-only combinations. |
| Desktop output / Android source | **Passed**, 753 unchanged filenames; desktop/Android csproj/content diff empty. Android runtime build **Skipped**: no Android workload is installed or required by the solution. |
| Fast tier | **Failed**, 14,973 passed / 2 failed / 7 skipped, all 14,982 cases covered; 2 passing timing offenders above 11.788064 s. Each failed/slow case runs 3× alone. The wingman case fails 1/3 alone. Infrastructure 9/9. |
| Training Fast+Live ×3 | **Incomplete / failed**. Three ClientCore selections: 126/0/0 each. One complete MonoGame selection: 290/2/2; two more interrupted at the time cap without aggregate results. Both report the same two reminder failures; each exact case fails 3/3 alone. The completed selection skips two opt-in hunt cases. |
| Browser / capture / Retry | **Passed** all four viewport/style combinations, both failure recoveries, no unexpected browser errors; original scout page passes. |
| Firefox | **Skipped**, attempted executable unavailable. |
| Docker | **Skipped**, executable absent. Compose is unchanged; no Compose service, server store, production listener or env var was added. Client Cache API/IndexedDB storage needs no container volume. The proof uses an ephemeral loopback static listener. |
| Glossary / parity / whitespace / script syntax / hygiene | **Passed**, including untracked source and all 18 reviewed images. Counts and hashes are in `verification.json`. |

The lane's default NuGet HTTP/package locations are read-only. Ignored writable
caches reuse installed package versions and retain vulnerability audit checks.
Builds use `--disable-build-servers -nodeReuse:false -p:UseSharedCompilation=false`.
Fast uses `--no-build` after the complete solution build, project parallelism 1.
Training used the requested script with artwork, offscreen SDL and null OpenAL,
so the real narration-loader check ran rather than skipped. The initial
`--repeat 3` invocation produced one complete project pair; its duplicate second
headless pass/partial native continuation is excluded. Two independent
`--repeat 1` cohorts used separate result/profile directories while retaining
serial projects/cases and the original sequential runsettings. These repetitions
overlapped on the lane. They were interrupted before aggregate native results
at **05:42 UTC** to leave time for final accounting within the eight-hour limit
from 21:48 UTC.
Only complete selections are counted: all three headless selections, one native
selection and consequently one complete pair. This does **not** satisfy three
complete Fast+Live repetitions or an uninterrupted serial `--repeat 3` run.
The completed pair covers every assigned prefix and both styles of full and
early-Nanite completion. Counts, complete-TRX hashes and incomplete-run failure
records are in [training-summary.json](BROWSER_SEAMS_P1047_assets/training-summary.json).
The final child-window checkpoints from the additional runs are retained in
[training-window-checkpoints.json](BROWSER_SEAMS_P1047_assets/training-window-checkpoints.json);
the ten checkpoints include two fresh successes for the last assigned prefix
and both styles of loss/early-Nanite windows. These are child-window completions,
not complete project selections.

| Test source | Cases / purpose |
|---|---|
| `tests/Allegiance.ClientCore.Game.Browser.Tests/P1047StagingTests.cs:8` | 12 cases: verified atomic publication, missing/corrupt/short/long/MIME failures, cache corruption/eviction, quota refusal, cancellation, shared-download contracts, canonical names/dependencies. |
| `tests/Allegiance.ClientCore.Game.Browser.Tests/P1047StorageTests.cs:6` | Eight cases: resident round-trip, rejected secrets/paths, atomic import and staging names. |
| `tests/Allegiance.ClientCore.Game.Browser.Tests/P1047BrowserPreferencesTests.cs:7` | Both styles exercise actual source-compiled browser profile/keymap metadata and reload. |
| `tests/Allegiance.ClientCore.Game.Browser.Tests/P1047CompileBoundaryTests.cs:7` | Evaluated source set plus assembly types/references verify the platform exclusions. |
| `tests/Allegiance.ClientCore.MonoGame.Tests/P1047ProfileStorageTests.cs:9` | Six desktop cases: existing ABI, UTF-8/atomic file bytes, preference/keymap contract and both HUD styles. |
| `tools/p1047-cache-tests.mjs:25` | Eight cases exercise the production JS algorithm with injected Cache/metadata/quota dependencies. |

The completed final Fast invocation runs for **1,065.8 s** with the unchanged
calibrated **11.788064 s** limit. Its failures are the ledgered command-relay
lookup receipt (`goto` instead of `goto Garrison`) and wingman throttle-state
case (expected 16, got 32). Relay passes 3/3 alone; wingman passes/fails/passes
(expected 16, got 128 in its isolated failure). The passing civ-5 scorer and
rock-model showcase exceed the limit in the sweep, then pass 3/3 alone in
3.990–4.401 s and 1.946–2.217 s respectively. Counts preserve these failures;
no assertion or threshold is relaxed.

A preceding 813.3 s sweep (13.393899 s calibrated limit) aborts in Core. Core's test host aborted at `TcpTransport.Send:600` via
`Bot.OnReceive:2340` with `InvalidOperationException: not connected`. It had
recorded 5,595 passed/3 skipped main cases; its 17 quarantine cases subsequently
pass. The crash has no attributed owning test case. Core/Bot/Server sources and
the Core test project are unchanged, and they do not reference Game.Browser.
Three isolated repetitions therefore select the entire Core Fast main group,
using the original filter and collection scheduling, rather than guessing the
owner. This does not change the failed original sweep or hide its 957-case gap.
The first full Core repetition completes all **6,555** selected cases: **6,551
passed / 1 failed / 3 skipped**, covering those original missing cases. Its
`LiveCombatWireTests.Real_two_party_combat_produces_a_kill_ship_over_the_wire`
failure is the already-ledgered shooter kill-credit receipt timeout (33.286 s).
That exact case subsequently passes **3/3 alone**, 1.834 / 1.847 / 1.851 s.
Core repetitions 2 and 3 also complete all 6,555 cases: **6,549/3/3** and
**6,551/1/3** (passed/failed/skipped). The background crash does not recur in
these three repetitions. The five distinct assertion failures (combat credit,
warp-transit receipt, timer delay-10 one-ULP bound, proactive pod rescue and NPC
auto-launch/mining receipt) each pass **3/3 in separate one-case processes**.
These observations do not establish root causes or turn the aborted sweep green.
The subsequent completed Fast sweep covers all 14,982 cases. Final browser-only
focus handling also passes the 23-case browser project ×3 and the full Chromium
proof after that sweep; desktop source is unchanged by those refinements.
A preceding incomplete Fast attempt exited with SIGTERM before MonoGame's TRX;
it is not counted as a completed sweep. Final runner accounting is in
[fast-summary.json](BROWSER_SEAMS_P1047_assets/fast-summary.json).

Every distinct non-task assertion failure and timing offender has been replayed
three times alone and recorded in `docs/TEST_FLAKES.md`; isolated results do not
turn a failing full gate green.

## Reproduction

From the repository root, with the read-only artwork configured and a writable
NuGet cache where required:

```sh
DOTNET="$HOME/.dotnet/dotnet"
"$DOTNET" build tools/p1047-menu-opens/p1047-menu-opens.csproj \
  --disable-build-servers -nodeReuse:false -p:UseSharedCompilation=false
mkdir -p .scratch/p1047/opens .scratch/p1047/profile
for style in Classic Modern; do
  SDL_VIDEODRIVER=offscreen ALSOFT_DRIVERS=null ALLEGIANCE_NOVSYNC=1 \
    XDG_CONFIG_HOME="$PWD/.scratch/p1047/profile" \
    "$DOTNET" tools/p1047-menu-opens/bin/Debug/net10.0/p1047-menu-opens.dll \
      "$style" ".scratch/p1047/opens/$style.json"
done
scripts/publish-browser-menu.sh .scratch/p1047/menu-generation \
  .scratch/p1047/opens/Classic.json .scratch/p1047/opens/Modern.json
python3 tools/p1047-static-server.py .scratch/p1047/menu-generation
```

Open `http://127.0.0.1:8047/play/preview/menu.html`. To reproduce the proof, install
`playwright@1.65.1` and `@sparticuz/chromium@153.0.0` under `.scratch/p1047/npm`,
provide the npm browser's bundled libraries/fonts where the host needs them, and
run `node tools/p1047-browser-proof.cjs`. Browser network is constrained to loopback.
`node --test tools/p1047-cache-tests.mjs` tests the production JS cache algorithm.

No server deployment/public hosting change is included. Core/ClientCore/Desktop/
Android behavior outside the selected platform seams is preserved. Browser joining,
identity, pointer lock, replay, hangar/flight and custom-effect parity remain later
slices, not hidden partially enabled paths in this build.

## Native intent

Reuse existing intro/Options behavior, native menu cue identity and original binary
core/font data. No simulation, authority, visibility or wire rule is changed;
existing native citations stay with their shared implementations and parity CSVs
remain unchanged.

## Modern approach

A source-compiled KNI boundary selects only the browser-safe lifecycle, stages and
hash-verifies complete resident packs asynchronously, and keeps platform IO behind
small storage/navigation/capture/audio seams. Desktop/Android continue to use their
original MonoGame projects, content and direct file-backed behavior.

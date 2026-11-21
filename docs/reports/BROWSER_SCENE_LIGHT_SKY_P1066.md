# Browser scene light and sky — P1066

Started **2026-11-21 04:35**, America/Bogota (UTC−5); six-hour cap **10:35**.
Finished **2026-11-21 05:56** local; task runtime/build/browser processes have exited.
Written before implementation and updated throughout. No commits, pushes, package
installations, public-host access or `docker/public/*` changes. Scratch is ignored.

## Browser availability and completion

**No Chromium on this lane.** Node is available; the P1051 harness directories
`.scratch/p1051/npm/node_modules/playwright` and `@sparticuz/chromium` are absent,
and no standard Chromium/Chrome executable is available. No packages were installed.
The brief explicitly permits the browser captures/pacing to be **for Claude** in
this case. Exact commands, including a matched P1065 baseline, appear below.

| Item | Result |
|---|---|
| 0 — Browser check | Done first; unavailable browser recorded above. |
| 1 — Shared scene foundation | Implemented; desktop extraction plus source-shared `NativeScene` primitives. |
| 2 — Browser scene | Light, default black sky, dust, strobes, cloak opacity, aleph extras and station extras implemented. Actual WebGL look remains for Claude. |
| 3 — Effect load | Implemented and tested through the real RAF/.NET bridge. Dust goes off before any resolution decrease. |
| 4 — Per-frame cost | Shared preparation and browser effect state allocate zero steady-state managed bytes; measured before/after below. No interpreter/GPU timing is claimed. |
| 5 — Proof | Shared tests, desktop HUD/filename comparison and byte-identical real desktop scene pair complete. Browser 5 v 5 images, pacing and visual review are for Claude. Final gate accounting is below. |

## Shared design and desktop invariance

`src/Allegiance.ClientCore.Game/Game1.Scene.cs:35` holds the extracted desktop
strobe gather; `:61` the sky tier dispatch; `:139` the aleph glimpse adapter;
`:192`, `:224`, `:253` the construction, teleport and strobe passes. Their call
positions in `Game1.Rendering.cs`, locks, sector gates, clocks, renderer arguments
and draw order are retained. The native/HD sky launch settings remain desktop choices.

Both clients compile and call
`src/Allegiance.ClientCore.Game/Rendering/NativeScene.cs:14` through the existing
browser shared-source glob. Its explicit arguments keep host/session orchestration
outside the shared primitives: cluster light (`:18`), cloak (`:24`), sky (`:26`),
dust (`:29`), authored station lights (`:88`), construction (`:96`), glimpses
(`:100`), ship strobes (`:107`), burst/lead-up sparks (`:130`, `:138`). Dust and
strobe arithmetic is unchanged; indexed reads and caller-owned storage eliminate
enumerator/list churn. The existing default visual-option lookup now indexes its
table in the same order, avoiding a boxed enumerator. Browser-only renderer branches
reuse sky effects/quads, dust geometry, strobe billboards, glimpse lists and expiry
scratch; desktop renderer bodies remain intact.

**B2/B3 extension:** add their GPU-free preparation functions to `NativeScene`,
keep emitter/particle buffers in the owning scene state, and add renderer adapters
at the existing desktop pass positions. The browser can call the same preparation
and draw functions after its visibility gather. `Game1.FullEffectLoad` is the
browser-only decision B2 can use for exhaust particles. No engine glow, exhaust,
ribbons, projectile sprites, hits, explosions or missile scene pass was added here.

## Before → after, intent and limits

Browser lifecycle/scene setup is in
`src/Allegiance.ClientCore.Game.Browser/Game1.Scene.cs:44` / `:71`; extras are at
`:103`. `Game1.Flight.cs` supplies the already-selected camera and predicted poses.
Both Classic and Modern consume this same scene and the same warp-flash HUD input.

| Gap | Before | After / native intent |
|---|---|---|
| L3 — Light | Every hull/rock/station received null lighting. | One `NativeScene.Light` rig from the current cluster is passed to every original hull, rock and station draw. Unknown cluster uses the unchanged desktop white fallback. Native: `WinTrek/WinTrek.cpp:3634-3640`, `Igc/clusterIGC.h:306-334` (key color/direction, blue alternate, ambient .25). |
| L4 — Sky | Navy clear and native nebula tier. | Flight clears black and draws the desktop default `BlackSkyRenderer`, including its default simulated-star/glare options, native moon art, distant paintings and galaxy field. Dust uses the same default subtle shell and native-debris fallback. Native poster/flare intent: `effect/efimage.cpp:527-563,601-613,724-743`; native debris size/near skip: `effect/debrisgeo.cpp:163-190`. Black tier, subtle dust and astronomical placement are existing desktop presentation choices, not newly claimed native rules. |
| L11 — Strobes/cloak | No ship strobes; opacity fixed at 1. | Exact existing three-anchor desktop ship layout and `LightsGeo` pulse, with constant RGB and size modulation; emitters use drawn, scanner-visible current-sector poses and camera culling. Missing-mesh stand-ins receive the same cloak opacity as meshes. Native pulse: `effect/efapp.cpp:171-222`; the geometric ship anchors remain the desktop's documented substitution. Shimmer is existing port UX, not a native viewport effect; native state: `WinTrek/consoledata.cpp:480-489,565-575`. |
| L9 — Aleph | Funnel only. | Reused native spark pool, velocity-derived spray and P781 fixed 60 Hz/four-tick cap, own one-shot flash, remote/own transit swell/pulse, and cached destination glimpses. Native sparks: `effect/thinggeo.cpp:503-528,619-696`, `effect/particlegeo.cpp:209-250`. Flash/glimpse/pulse are existing port presentation; native `WinTrek/trekigc.cpp:2976-2991` has no corresponding viewport effect. Browser flare uses the desktop's unoccluded fallback when no linear-depth target exists. |
| L10 — Station | Bare mesh. | Authored SS light transforms, native pulse and >32-pixel projected-radius gate; actual native build shells, phase, rotation, shrink/fade and default lifetime table. Native: `effect/thinggeo.cpp:1482-1510,1618-1630`, `effect/efgeo.cpp:99-227`, `clintlib/appmsg.cpp:1692-1718`. Browser build art is decoded only when a visible shell can draw. |

`BrowserSceneEffects.cs:48` owns reusable frame state, `:90` gathers drawn ship
strobes/lead-up sparks, `:135` emits/snapshots particles, `:141` detects the own
transit edge, and `:149` resolves partner-warp destination sectors. Initial spawn
never flashes; view/mission/session resets clear histories. Camera-culled re-entry
uses wire velocity rather than dividing an old position delta by one frame; a new
view cannot replay a remote ship's old sector as a fresh arrival. Early detachment before
any flight renderer loads still releases the world's renderer callbacks. Contacts
and P1065 missing-mesh stand-ins continue independently of effect delivery.

Tracking: [parity matrix](../parity/p1066-browser-scene.csv). All rows reuse existing
native or explicitly identified port presentation; no new game/wire rule or parity
credit is claimed.

## Original artwork delivery

`tools/p1066-scene-packs.py` adds original-byte, content-addressed closures to the
existing verified battle streamer, without altering the join/sector manifests.
Stars, resident planet/flare, dust and resident strobe art draw immediately even
while the deferred pack retries. Native moon/spark art arrives first; paintings
follow; giant art is requested only for
sectors named in its desktop configuration; build art only for current-sector
construction. `BlackSkyRenderer.PromoteResidentLayers` enables completed layers
without rebuilding the native sky textures. Glimpse postcards invalidate when
new layers or sector textures arrive. A native-pack promotion refreshes the sky
once so missing-moon cache entries cannot persist. Failure leaves the available scene usable
and retries through the existing worker; neither simulation nor input awaits it.

| Pack | Sources | Additional raw bytes | Additional gzip bytes | Request |
|---|---:|---:|---:|---|
| `scene-b1` | 10 | 2,364,624 | 573,472 | After launch; native moons/flare/strobe/dust/teleport art |
| `scene-paintings` | 239 | 9,952,488 | 9,958,228 | After the native scene pack |
| `scene-build` | 2 | 8,399,684 | 5,886,448 | Current-sector building effect |
| `scene-giants` | 2 | 8,469,859 | 8,363,070 | Configured giant sector (this artwork: Terra, 1057) |

Shared resident sources are excluded from the additional-byte columns. The full
union is 253 names, 248 additional unique files, 29,186,655 raw / 24,781,218 gzip
bytes; a player in Ixion does not request the giant pack. The previous single-pack
shape would have delayed all sky presentation behind that full transfer, which is
why delivery is split by actual use. Persistent cache limits and the shared four-
download semaphore remain unchanged. `scene-paintings` is explicitly allowed the
existing 1,024-file staging bound; ordinary hull closures retain their 128-source
bound. The only additional allowed JSON visual source is `giant-objects.json`.

The processed NASA/ESA/NOIRLab/Webb paintings retain their CC BY 4.0/public-domain
credits and modification notices in generated `scene-NOTICE.txt`; final provenance
points to its fingerprinted filename. Native artwork retains its MIT notice.
[Publication receipt](BROWSER_SCENE_LIGHT_SKY_P1066_assets/publication.json),
[scene receipt](BROWSER_SCENE_LIGHT_SKY_P1066_assets/scene-receipt.json), and
[independent C# original-byte/closure audit](BROWSER_SCENE_LIGHT_SKY_P1066_assets/scene-audit.json):
**253/253 original sources verified**, all closures validated. Join assets remain
601 names / 577 unique hashes, 32,536,380 raw / 7,564,835 gzip bytes. Final base +
runtime transfer is **13,758,095 bytes**, under the 14,680,064-byte P1054
budget. Notices (including native/framework notices) resolve to existing fingerprinted files. Resource creation at verified promotion, sector entry
or a newly drawn build remains outside the steady-state preparation measurement;
actual browser upload hitches and heap must be qualified in the deferred run.

## Effect-load fallback

`wwwroot/frame-pacing.js:25` reduces **effect load first**, then scale 1 → .75 → .5.
The existing rolling two-second p95 window, >40 ms pressure/two-second hold and
<34 ms/five-second recovery remain. Recovery restores .5 → .75 → 1, then effects.
Hidden time cannot authorize recovery. The real `preview.js` RAF bridge sends
`EffectLoad(bool)` only on changes, and only to the menu/flight head; the standalone
scout preview never calls that API. Audio-gap handling is unchanged.

`Game1.Scene.cs:27` receives this browser-only decision. Reduced load gates dust,
clears its presentation history and leaves light, sky, strobes and aleph feedback
active. There is no `visuals.*` flag and no desktop quality change. The tests cover
thresholds, ordered degradation/recovery, sparse/hidden frames, reset and the actual
RAF bridge's calls before any drawing-buffer resize.

## Per-frame preparation cost

[Measured JSON](BROWSER_SCENE_LIGHT_SKY_P1066_assets/benchmark.json), from
`tools/p1066-scene-proof`, Release JIT with tiering disabled, 2,000 warm-up and
20,000 measured passes. Before is P1065's indexed selection/hull/contact/name work;
after includes that work plus B1 light, cloak, strobes, live aleph lead-up particles,
flash/pulse state, glimpse selection and default dust/culling. All ships in this
fixture are eligible drawn emitters; its simultaneous approaching-aleph workload
has 300/1,290/1,380 live sparks at 2/10/20 ships. This is an intentional upper
particle workload with a stationary camera, not an observed browser battle; camera
movement/refill and renderer costs are not measured. Render orientations are already
resolved by the existing prediction path before preparation, just as in Game1.

| Ships | Known off-sector | Before µs/pass | B1 full µs/pass | B1 reduced µs/pass | Before / after allocated bytes/pass |
|---:|---:|---:|---:|---:|---:|
| 2 | 0 | 0.130 | 6.844 | 4.186 | 0 / 0 |
| 2 | 500 | 0.123 | 6.840 | 4.166 | 0 / 0 |
| 10 | 0 | 0.574 | 20.676 | 17.888 | 0 / 0 |
| 10 | 500 | 0.572 | 20.746 | 17.946 | 0 / 0 |
| 20 | 0 | 1.141 | 24.707 | 22.024 | 0 / 0 |
| 20 | 500 | 1.141 | 24.782 | 22.164 | 0 / 0 |

These are **not .NET interpreter, RAF or GPU times**. They exclude existing pose
prediction, asset decode/upload, WebGL drawing, HUD rendering and JS pacing.
Preparation allocation tests run against both desktop and browser builds; a
separate browser test includes sustained live spark state. New renderer-side
storage is reused after warm-up, and dust/strobe work is camera/visibility gated.

## Desktop zero-delta proof

An independent `git archive HEAD` baseline (`d1943a5881ce`) was compiled with only
installed packages. The HUD harness reports `Dirty=true` because it was launched
inside this working tree; the independently compiled archive sources and their
[hashes](BROWSER_SCENE_LIGHT_SKY_P1066_assets/baseline-sources.json) are the baseline.

- **1,848/1,848 HUD cases passed:** 0 pixel differences, 0 semantic differences,
  84 existing animation-mask differences, 0 corrupt goldens.
  [HUD receipt](BROWSER_SCENE_LIGHT_SKY_P1066_assets/desktop-hud.json).
- **753/753 desktop output filenames identical**, no added/removed paths.
  [Filename manifest](BROWSER_SCENE_LIGHT_SKY_P1066_assets/desktop-filenames.json).
- **Full real desktop scene pair is byte-identical**, not masked: replay frame
  1800, 1920×1080, Xvfb/llvmpipe, 570,799 bytes each, SHA-256
  `ac6cdc9cd014480132391fab6e81a6cfef1b46015194b752b2772c86532cd4be`.
  [Before](BROWSER_SCENE_LIGHT_SKY_P1066_assets/desktop-scene-before.png),
  [after](BROWSER_SCENE_LIGHT_SKY_P1066_assets/desktop-scene-after.png),
  [receipt](BROWSER_SCENE_LIGHT_SKY_P1066_assets/desktop-scene.json).

**Both saved scene images were opened.** They show the same black Ganymede view:
dim blue/brown rocks, a blue vortex in the upper left, scattered bright flecks,
teal Classic gauges, a sector minimap and 171 mps readout. No expected pixel
change exists. These are desktop evidence, not substitute browser captures.

## Gates

| Check | Result |
|---|---|
| Solution build | **0 warnings / 0 errors** |
| `tools/build-tools.sh` | **150 projects passed**, 0-warning/error builds |
| New browser/shared/view/culling tests + prior detach regression, alone ×3 | **19 passed ×3** (18 P1066 cases plus the existing detach case) |
| Shared desktop preparation tests, alone ×3 | **11 passed ×3** |
| JS pacing + actual RAF bridge tests, alone ×3 | **8 passed ×3** |
| Python scene closure/license tests, alone ×3 | **3 passed ×3** |
| Full browser game-model project | **136 passed** |
| Full browser preview project | **39 passed** |
| Full lobby suite | **328 passed, 3 optional PostgreSQL skips** |
| Original-byte/closure audit | **253/253 passed** |
| `tools/p1061-deploy-check.py` | **PASS, 45 checks** |
| Glossary / task parity CSV / whitespace | **PASS** |
| Working-tree hygiene | No secret/personal-home/email hits; brief absent from change set; two PNGs opened; no public deployment changes |
| Final Fast tier, run last | **RED:** 15,821 passed / 2 failed / 7 not executed; 5 additional passing timing offenders; 565.1 s |

[Verification receipt](BROWSER_SCENE_LIGHT_SKY_P1066_assets/verification.json)
retains repeated-test and suite counts/TRX hashes. [Hygiene receipt](BROWSER_SCENE_LIGHT_SKY_P1066_assets/hygiene.json)
records the working-tree check. The push guard itself checks commit ranges; no
commit was created to invoke a prospective push workflow. The changed media were
reviewed directly. All command/build-server invocations use the installed SDK,
local package source and the brief's disabled-server/shared-compilation settings.

The single final Fast sweep exercised **15,830 cases**, with all **9 infrastructure
checks passing**, no quarantine flakes and a calibrated **11.566 s** per-case limit.
Its browser head/game and lobby counts were 39/136/325 passes. The two assertion
failures are outside this task: `FullSessionIntegrationTests` observed teammate
side 0 while its peer still held team-lobby side −2 (line 229), and
`P990BroadcastLaneTelemetryTests.Lane_record_and_stamp_paths_allocate_nothing_after_warmup`
observed 3,032 allocated bytes instead of zero (line 167). The five passing timing
offenders belong to existing selection/strategy/asteroid families. Each affected
method was selected three times alone, including all theory rows, in fresh
processes. [Sweep receipt](BROWSER_SCENE_LIGHT_SKY_P1066_assets/fast-summary.json).
The non-browser `ClientWorld` source matches HEAD after preprocessing; no session,
server telemetry, simulation test, assertion, timing limit, tier or quarantine rule
was changed. Fast remains red; isolated passes do not erase the sweep result.

Isolation finished **2026-11-21 05:55 local**: **56 passed / 1 failed** across
21 fresh processes. FullSession passed/failed/passed with the same side-export
signature; telemetry passed ×3. All theory rows of all five timing methods passed
every isolated run, below the sweep's limit.
[Isolated receipt](BROWSER_SCENE_LIGHT_SKY_P1066_assets/fast-isolated.json) includes
case durations, failure text, TRX hashes and unchanged assertion/server source hashes.
[Flake ledger](../TEST_FLAKES.md) records the full table.
No second broad sweep or unrelated fix was attempted. **Fast remains red.**
Browser images/RAF pacing/WebGL heap qualification remains for Claude as permitted
by item 0; everything executable on this lane is complete.

## Browser 5 v 5 proof and comparison — for Claude

The checked-in P1065 browser summary is **10 v 10**, not a matched 5 v 5 baseline.
No 5 v 5 RAF comparison is invented from it. Run HEAD and P1066 generations with
the same host, `--bots 5`, sample duration and four style/size cases. The new
`tools/p1066-browser-scene.cjs` extends the P1065 harness with scene readiness,
black-sky/quality metrics and PNG captures. It is syntax-checked; runtime browser
qualification remains for Claude. Any pilot death, empty sector or timeout must
be reported/retried with a fresh host, never treated as successful combat evidence.

Use the already-installed P1051 modules. No npm/NuGet package installation; all
network endpoints stay on loopback. From the repository root:

```bash
set -euo pipefail
export DOTNET="$HOME/.dotnet/dotnet"
export NUGET_HTTP_CACHE_PATH="$PWD/.scratch/p1066/nuget-http"
export ALLEGIANCE_ARTWORK_PATH=/srv/corpus/Artwork  # existing workstation artwork
export BROWSER_CLIENT_VERSION=2.0.0
NPM_DIR=.scratch/p1051/npm
mkdir -p .scratch/p1066/traces
test -d "$NPM_DIR/node_modules/playwright"
test -d "$NPM_DIR/node_modules/@sparticuz/chromium"
python3 - <<'PY'
import gzip,json
from pathlib import Path
traces=json.loads(gzip.decompress(Path('docs/reports/BROWSER_FLIGHT_P1051_assets/desktop-opens.json.gz').read_bytes()))
for name,trace in traces.items():
    p=Path('.scratch/p1066/traces')/name
    p.parent.mkdir(parents=True,exist_ok=True)
    p.write_text(json.dumps(trace))
PY
# Only installed packages; build the current loopback host/MCP/desktop/browser.
for project in tools/p1051-browser-host tools/allegiance-mcp src/Allegiance.ClientCore.MonoGame src/Allegiance.ClientCore.Browser; do
  "$DOTNET" build "$project" --disable-build-servers -nodeReuse:false -p:UseSharedCompilation=false \
    -p:RestoreSources="$HOME/.nuget/packages" -p:NuGetAudit=false
done
# Independent HEAD client baseline. Choose fresh directories if reproducing twice.
BASE="$PWD/.scratch/p1066/browser-baseline-source"
mkdir -p "$BASE"
git archive HEAD | tar -x -C "$BASE"
for project in src/Allegiance.ClientCore.MonoGame src/Allegiance.ClientCore.Browser; do
  "$DOTNET" build "$BASE/$project" --disable-build-servers -nodeReuse:false -p:UseSharedCompilation=false \
    -p:RestoreSources="$HOME/.nuget/packages" -p:NuGetAudit=false
done
TRACE="$PWD/.scratch/p1066/traces"
CURRENT="$PWD/.scratch/p1066/browser-current-workstation"
BEFORE="$PWD/.scratch/p1066/browser-before-workstation"
scripts/publish-browser-flight.sh "$CURRENT" "$TRACE/opens/Classic.json" "$TRACE/opens/Modern.json" \
  "$TRACE/flight-opens4/Classic.json" "$TRACE/flight-opens4/Modern.json" \
  "$TRACE/flight-miners0/Classic.json" "$TRACE/flight-miners1/Modern.json"
(cd "$BASE" && scripts/publish-browser-flight.sh "$BEFORE" "$TRACE/opens/Classic.json" "$TRACE/opens/Modern.json" \
  "$TRACE/flight-opens4/Classic.json" "$TRACE/flight-opens4/Modern.json" \
  "$TRACE/flight-miners0/Classic.json" "$TRACE/flight-miners1/Modern.json")
for pass in before after; do
  generation="$BEFORE"; harness=tools/p1065-browser-battle.cjs
  output="$PWD/.scratch/p1066/browser-before"
  if [[ $pass == after ]]; then
    generation="$CURRENT"; harness=tools/p1066-browser-scene.cjs
    output="$PWD/docs/reports/BROWSER_SCENE_LIGHT_SKY_P1066_assets/browser"
  fi
  state_dir=$(mktemp -d "$PWD/.scratch/p1066/browser-host-$pass.XXXXXX")
  "$DOTNET" tools/p1051-browser-host/bin/Debug/net10.0/p1051-browser-host.dll \
    "$ALLEGIANCE_ARTWORK_PATH" "$generation" "$state_dir" --bots 5 --seconds 1200 \
    > "$state_dir/host.log" 2>&1 &
  host_pid=$!
  cleanup() { touch "$state_dir/stop"; wait "$host_pid" || true; }
  trap cleanup EXIT INT TERM
  result=0
  node "$harness" "$NPM_DIR" "$state_dir" "$output" "$generation" 60 || result=$?
  cleanup
  trap - EXIT INT TERM
  [[ $result == 0 ]] || exit "$result"
done
python3 - <<'PY'
import json
from pathlib import Path
before=json.loads(Path('.scratch/p1066/browser-before/browser-battle.json').read_text())
after=json.loads(Path('docs/reports/BROWSER_SCENE_LIGHT_SKY_P1066_assets/browser/browser-scene.json').read_text())
for b,a in zip(before['timing'],after['timing'],strict=True):
    assert (b['style'],b['width'],b['height']) == (a['style'],a['width'],a['height'])
    print(a['style'],a['width'],a['height'],'before',b['p50Ms'],b['p95Ms'],'after',a['p50Ms'],a['p95Ms'])
PY
```

The expected four P1066 PNGs are `scene-5-classic-1920x1080.png`,
`scene-5-modern-1920x1080.png`, `scene-5-classic-800x480.png` and
`scene-5-modern-800x480.png`, under the `browser/` asset directory. The harness
requires each PNG **and the four-image set** to fit 2 MiB and refuses a non-flying,
non-battle frame. If the set exceeds the limit, retain the failure and optimize
lossless PNG encoding before handoff; do not substitute JPEG for the requested PNGs.
**None exists on this lane, so none was opened or described.** Claude must open
all four, describe only visible content, append the before/after p50/p95, actual
buffer scale/effect-load state, contacts/meshes/stand-ins and WASM/managed memory,
and keep headless timing separate from real-GPU qualification.

## Native intent / Modern approach

**Native intent:** preserve cluster illumination, native light pulses/authored
station lights, poster/flare/debris rules, teleport sparks and build-shell behavior;
retain the desktop's existing, explicitly identified presentation additions.

**Modern approach:** source-share preparation/drawing, stream verified original
visual closures by use, reuse frame storage, and lower browser effect load before
resolution. Desktop behavior/pixels, native visibility and server/wire rules remain
unchanged. Browser visual/pacing/heap evidence is explicitly for Claude because
this lane lacks Chromium.

## Workstation browser results — Claude

Run 2026-11-21 on the workstation from the merged review branch (P1066 + P1067), headless Chromium via the existing
P1051 Playwright modules, `p1051-browser-host --bots 5`, **40 s per case** for both passes (see "Retries" below).
[Summary JSON](BROWSER_SCENE_LIGHT_SKY_P1066_assets/browser/browser-scene-summary.json).

**Bug found and fixed in review (`033fb445`): the scene packs never loaded.** The lobby serves every extensionless
`packs/<sha256>` file as `application/octet-stream` (`src/Allegiance.Lobby/BrowserPreview/BrowserPreviewHosting.cs:58-63`),
and `AssetStager` requires the response type to equal the declared one (`AssetStager.cs:183-184`). `p1066-scene-packs.py`
declared PNG sources as `image/png`, so `scene-b1` (which holds `e_tele.png`) failed with `InvalidDataException` on every
retry and `scene.ready` never became true; all 239 paintings would have failed the same way, on the VPS too. The P1054,
P1065 and P1066 packers now declare `application/octet-stream`; `StagedManifest.Validate` refuses any other pack type at
manifest load, with a regression test (`P1066BrowserSceneTests.Pack_png_declared_as_image_png_is_refused_at_manifest_load_not_retried_in_flight`).
The harness now records `[browser]` console lines and pack requests on failure, which is how this was found.

| Case | Before p50 / p95 ms | After p50 / p95 ms | After frames |
|---|---|---|---|
| Classic 1920×1080 | 16.7 / 16.7 | 16.7 / 16.8 | 2,402 |
| Modern 1920×1080 | 16.7 / 16.8 | 16.7 / 16.8 | 2,388 |
| Classic 800×480 | 16.7 / 16.7 | 16.7 / 16.7 | 2,396 |
| Modern 800×480 | 16.7 / 16.8 | 16.7 / 16.7 | 2,429 |

Headless Chromium (SwiftShader) paces at the 60 Hz cap, so equal numbers show no regression below that cap — they are
**not** real-GPU cost. After the forced fetch failure and release: `scene.ready` and `paintingsReady` true, sky tier
`black`, effect load `full`, dust 16, 3 contacts = 3 meshes, 0 stand-ins, ship pass 0.3 ms, 7 battle packs completed
(1 retry, the injected failure), 12.3 MiB promoted, managed heap 111 MiB. While packs were held, a ship without its mesh
drew as a stand-in (3 contacts = 2 meshes + 1 stand-in) and the sky drew black with dust before any pack arrived.

**Retries (not relabelled):** two 60 s attempts lost the pilot in the fourth window (pod → home 1053, harness asserts
1058). The pilot survives ≈3 minutes parked in the battle, so both passes were re-run at 40 s per case; the before pass
was re-run too, so the comparison stays matched.

**Captures** (opened and checked by Claude):
- `scene-5-classic-1920x1080.png`: black space with sparse small star points and dust specks; a few rocks at the edges,
  lit dark blue-grey; distant fighters as small meshes with name labels, a lifepod warning triangle, Classic ring gauges
  centred, `CHASE` tag bottom-left, the HTML flight toolbar along the bottom.
- `scene-5-modern-1920x1080.png`: the same sector at another moment — many rocks, side-lit blue-grey with one warm
  orange-brown rock top right; dust specks; Modern gauges (shield 0 % with the red warning, the known P1063 H7 issue),
  target bracket, enemy labels with range.
- `scene-5-classic-800x480.png`: denser rock field, lit; enemy labels with ranges; Classic gauges scaled down.
- `scene-5-modern-800x480.png`: one large lit rock left, distant contacts as coloured edge markers, Modern gauges.

**Stars:** the browser now matches the desktop's default black tier — compare `desktop-scene-after.png`, which also
shows sparse star points, side-lit blue rocks and dust. Strobes counted 2 in an earlier (retried) attempt and 0 in the passing one; they are too small to read at
these 2–3 km ranges; no aleph transit happened during capture, so sparks/flash are covered by the unit tests only.

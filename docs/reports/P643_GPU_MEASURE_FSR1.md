# P643 — GPU/GC instrumentation, FSR1 and dynamic resolution

Codex lane, 2026-10-14. Base `master` at `2ee472f3`, including P628 ship LOD. Changes are left in the working tree; no commits or pushes.

## Outcome and measurement limits

Implemented FSR1 EASU + RCAS, native-resolution HUD/overlays, persisted graphics settings (default **Off**), optional GPU-driven dynamic resolution, CPU/GPU frame telemetry, GC telemetry and opt-in flight-only SustainedLowLatency. Workstation GC remains explicit in the DesktopGL runtime configuration. The latency experiment remains **off by default**: there is no 200v200 A/B evidence supporting changing the default.

`DISPLAY=:0` and `WAYLAND_DISPLAY=wayland-0` are available, and `xdpyinfo` succeeds. Real client windows and PNG captures were run at 1920x1080 and 2560x1440. However, this sandbox has **no `/dev/dri`**. MonoGame reports **llvmpipe (LLVM 21.1.8, 256 bits)**, not the host's hardware GPU. The window log also reports failure to load iris / create a DRI3 screen. These are software-renderer measurements, not 680M measurements.

The brief's Codex rule reserves anything “live on the rig / 221 / 200v200” for Claude. No rig or 195v195/200v200 battle was started. Local hull and station inspectors exercise the actual shared renderers, soft depth, post processing and native text without a server. They do **not** represent a wide battle or close dogfight. Hardware-bound classification for those two scenes, station-in-battle classification, and the flight GC A/B remain Claude's explicitly assigned measurement work. No battle FPS or hardware GPU/CPU verdict is invented here.

## Renderer and settings

Order: opaque linear-depth pass at render resolution -> scene color/particles/sky at render resolution -> optional bloom bright-pass/blur/composite at render resolution -> **EASU** to a native-size RGBA8 target -> **RCAS** to the backbuffer (or native capture target) -> radar, HUD, text and 2D overlays. Existing aleph portal/flare overlays retain their position after the scene composite and therefore run at native resolution. Depth occlusion sampling converts native overlay coordinates to render-resolution depth coordinates. Docked UI previews remain part of the native-resolution UI. The offline hull/station inspector also uses the new pipeline.

`Ctrl+F6` opens **Graphics**, through the rebindable `OpenGraphicsSettings` action. Up/down selects a row; left/right changes its value; Escape closes it. The RCAS slider also supports mouse dragging. The modal panel releases movement/fire commands while open. All five values are saved automatically:

| Setting | Values / behavior |
|---|---|
| Upscaling | Off (default), FSR1 |
| Preset | Ultra Quality 1/1.3 (~77%), Quality 1/1.5 (~67%), Balanced 1/1.7 (~59%), Performance 1/2 (50%) |
| RCAS sharpness | 0–1 lobe strength; 0 disables sharpening, 1 is strongest; default 0.5 |
| Dynamic resolution | Off by default; scale is bounded between 50% and the selected preset |
| Target | 8–100 ms; default 16.667 ms |

Preset dimensions round to nearest pixel, away from zero at half pixels, and clamp to at least 1x1. Dynamic resolution uses actual asynchronous scene+post GPU samples, an EWMA (0.1), an 8% dead band, 30-sample dwell and 2.5-percentage-point steps. It never treats update time or vsync/present blocking as GPU time. Without GPU queries it holds the preset scale; a CPU-bound frame cannot force a reduction. A target that cannot be reached at 50% remains constrained there. Changing preset resets the controller. Targets are recreated only when their dimensions change, including window resizes. Missing effects fall back to native resolution and are reported by the shader cache and settings panel.

Settings live at `LocalApplicationData/Allegiance/graphics.json`, overridable with `ALLEGIANCE_GRAPHICS_SETTINGS`. Save uses a temporary file + replacement; malformed/missing settings fall back safely and invalid numeric values are normalized. This shared implementation and the shader asset paths are also used by Android; the existing keyboard-oriented UI needs a keyboard for the opening chord there.

The shaders are SM3/full-precision ports of AMD's [FSR1 source](https://github.com/GPUOpen-Effects/FidelityFX-FSR/blob/a21ffb8f6c13233ba336352bdff293894c706575/ffx-fsr/ffx_fsr1.h), revision `a21ffb8f6c13233ba336352bdff293894c706575`. EASU retains the 12-tap directional/anisotropic Lanczos approximation and nearest-four deringing; RCAS retains its adaptive cross-neighborhood lobe limiter. SM3 has no gather/bit reinterpretation path: point samples replace gathers, full-precision reciprocal/rsqrt replace bit approximations, and epsilon guards handle flat black/white neighborhoods. Sharpening is a separate pass. This is AMD's spatial algorithm, with these documented portability adaptations. Full MIT notice is in `NOTICE`, with attribution in `CREDITS.md`. AMD describes the [two spatial passes here](https://gpuopen.com/fidelityfx-superresolution/).

The same `/Profile:OpenGL` bytecode is loaded through `ShaderAssetCache` on DesktopGL and packaged under Android `assets/Content/Shaders/`. Both compiled effects and SHA-256 source records are supplied. The Android debug APK built with zero warnings/errors, and zip inspection confirmed EASU (14,005 bytes), RCAS (3,176 bytes), and both hashes. A physical Android/GLES runtime load was not performed; DesktopGL and offscreen Mesa runtime loads both passed.

## Telemetry and GC

Enable with `ALLEGIANCE_CLIENT_PERF=1`. Once per second, the log and native-resolution perf overlay show frame p50/p99/max, CPU update/draw submission/post submission, scene+post GPU time, present wait, actual MonoGame draw calls, scene/output dimensions, gen0/gen1/gen2 **collection deltas**, total GC pause delta, total allocated bytes/frame across managed threads, and managed heap size. Frame IDs correlate p99/max with that frame's GC pause and GPU query results. Percentiles describe rendered frame intervals measured by Stopwatch, not `GameTime`'s potentially fixed-step delta. CPU submission can include driver blocking.

GPU timing uses SDL's current DesktopGL context and `GL_TIME_ELAPSED` around the depth+scene and post passes. A 32-query ring polls availability without blocking and never reuses an outstanding query. Core GL >=3.3 is checked before querying. Query results are associated with the originating frame ID. CSV output is delayed 16 frames to permit completion; an unavailable/late result is `NaN`, never fabricated. GPU totals cover scene+post, excluding native UI. Contexts without supported queries (including Android's non-SDL context path) report **unavailable**. Present wait is logged separately as CPU blocking; with vsync off, resolution sensitivity and present stalls can inform an inference, but it is not a GPU timestamp and does not drive dynamic resolution.

`ALLEGIANCE_CLIENT_PERF_CSV=/path/frames.csv` writes every frame's interval, CPU phases, present wait, scene/post GPU times, GC pause, allocations, draw calls and dimensions. Reporting uses fixed arrays; loading allocations are excluded from initial counter baselines. Report text and CSV formatting themselves have small allocations, included in the next frame's all-thread allocation delta. Screenshots/readback disturb timings: exclude the final capture frame, as the summary script does.

`ALLEGIANCE_CLIENT_GC_LOWLATENCY=1` opts into `GCSettings.LatencyMode = SustainedLowLatency` only while flying and undocked. Menus/docking/loading use the original mode; disposal restores it. The startup log prints workstation/server status and initial latency. No server-GC switch was introduced. Unit tests verify default opt-out and restoration both on leaving flight and disposal.

## Local real-window evidence

Each comparison used an 8-second auto-stop budget starting at renderer initialization (including the remainder of content loading), vsync and the fixed-step limiter disabled with `ALLEGIANCE_NOVSYNC=1`. First 2 seconds and the final capture/exit frame are excluded below. All use the default inspector camera, native art, soft depth on, bloom off, dynamic resolution off, sharpness 0.5. Hull: id 13 Adv Scout; station: inspector's initial station. These short runs share a machine with other work; they are correctness/cost checks, not controlled hardware benchmarks.

| Local scene | Mode | Frame p50 / p99 / max (ms) | GPU scene / post median (ms) |
|---|---|---|---|
| hull-1080 | Off | 4.73 / 7.89 / 12.87 | 1.58 / 0.11 |
| hull-1080 | Quality | 11.03 / 15.95 / 19.58 | 2.11 / 5.87 |
| hull-1080 | Performance | 10.92 / 17.43 / 21.60 | 1.96 / 5.84 |
| hull-1440 | Off | 7.82 / 11.25 / 16.38 | 2.28 / 0.13 |
| hull-1440 | Quality | 16.65 / 24.25 / 30.27 | 2.70 / 8.61 |
| hull-1440 | Performance | 18.54 / 28.54 / 34.49 | 2.51 / 9.88 |
| station-1080 | Off | 4.24 / 7.82 / 15.22 | 1.32 / 0.09 |
| station-1080 | Quality | 11.13 / 15.36 / 17.12 | 2.08 / 5.92 |
| station-1080 | Performance | 10.77 / 16.40 / 23.04 | 1.95 / 5.69 |
| station-1440 | Off | 8.58 / 30.08 / 42.06 | 2.75 / 0.10 |
| station-1440 | Quality | 16.81 / 22.56 / 25.29 | 2.66 / 8.60 |
| station-1440 | Performance | 16.76 / 21.78 / 23.90 | 2.43 / 8.62 |

[Full CPU/present/GC table and p99/max frame IDs](P643/frame-table.md). [All raw CSVs, per-second logs and 12 PNGs](P643/) are retained next to this report (checkout-root paths in logs are replaced with `<repo>`). For example: [native hull 1080p](P643/hull-1080-Off.png), [Quality](P643/hull-1080-Quality.png), [Performance](P643/hull-1080-Performance.png), [native station 1440p](P643/station-1440-Off.png), [Quality](P643/station-1440-Quality.png), [Performance](P643/station-1440-Performance.png).

In these **software-rendered inspectors**, .NET update and ordinary submission are small; native presentation/render work dominates. FSR's native-size post processing dominates when enabled (about 5.7–9.9 ms median GPU post versus 0.1 ms without it), making these simple scenes slower. This says nothing reliable about hardware battle fill rate. It supports retaining Off as the default.

GC correlation is observable rather than presumed: hull/1080/Quality's max frame #209 was 19.58 ms with a 7.39 ms GC pause and 16.11 ms scene+post query time; hull/1440/Quality's p99 frame #207 had a 7.77 ms pause. The values overlap in wall time and must not be added. Conversely station/1440/Off's 42.06 ms max had **zero** GC pause; that hitch cannot be blamed on a measured GC pause. These examples demonstrate why both counters are needed.

### Allocation sample

A separate 12-second hull-inspector run collected `dotnet-trace --profile gc-verbose`; allocation ticks after 3 seconds were grouped by type. These are **sampled allocation byte estimates**, not retained heap sizes or an exact allocation census. See [type totals](P643/allocations-summary.txt) and [run log](P643/hull-allocations.log). Steady hull allocation was ~19 KB/frame, versus ~1.7 KB/frame for the station inspector.

| Type | Estimated sampled bytes | Samples | Candidate source to investigate later |
|---|---:|---:|---|
| `InspectorHull[]` | 30,563,416 | 287 | `Game1.Inspector.cs:302` `VisibleInspectHulls()` rebuilds filtered lists on repeated current-hull/HUD reads |
| `System.String` | 5,110,336 | 48 | `Game1.Inspector.cs:1411` `DrawInspectHud()` formats status strings every frame |
| `System.Int16[]` | 2,130,080 | 20 | `Game1.Inspector.cs:1439` projectile/missile/mine name helpers sort ID keys on each HUD draw |
| `System.Int32[]` | 1,596,184 | 15 | sorting/enumeration work; type sampling alone does not establish its allocating call site |
| `OrderedIterator<short,short>` | 851,776 | 8 | the same ID-key `OrderBy` paths |

Call-site associations above are source-based candidates, not allocation-stack proof. No inspector allocation cleanup was mixed into this task. No in-flight battle allocation profile was collected, so these inspector findings must not be generalized to combat. GC A/B at a 200v200 viewpoint still needs frame p50/p99/max, total pauses, gen2 deltas, and heap/private-memory growth under both modes before deciding whether SustainedLowLatency helps.

## Reproduction and Claude's rig run

Build and shader rebuild:

```bash
# In this sandbox, give NuGet a writable HTTP cache. The normal package cache is readable.
export NUGET_HTTP_CACHE_PATH=/tmp/p643-nuget-http
dotnet build AllegianceCore.slnx
# Existing Wine-equipped development host:
scripts/build-shaders.sh
# Android: stages the DesktopGL Content, including the two new shaders.
scripts/build-android-head.sh
```

The sandbox's system Wine prefix/runtime directories were read-only. For the shader build only, a private prefix and Wine binary copy were made under `/tmp`, with Wine's runtime-directory literal relocated to `/tmp`; `wine`, `wine64` and `winepath` wrappers selected that copy. Both shaders then compiled with the existing mgfxc. This workaround is not needed by the game or by a normally writable Wine installation and does not modify host toolchain files. Initial restore errors from a read-only NuGet HTTP cache were resolved with the environment variable above.

Offscreen/headless self-test (no server, artwork or display needed on Mesa with EGL):

```bash
dotnet build tools/fsr-proof
SDL_VIDEODRIVER=offscreen ALSOFT_DRIVERS=null \
  dotnet tools/fsr-proof/bin/Debug/net10.0/fsr-proof.dll "$PWD" /tmp/fsr-proof
# For a real window, omit SDL_VIDEODRIVER.
```

The self-test loads **both actual compiled effects**, renders a colored/diagonal test scene through scene RT -> optional bloom -> EASU -> RCAS -> **backbuffer**, reads pixels and verifies output dimensions, non-black colors, orientation, flat black/white stability, all four presets, changing target dimensions, both sharpness endpoints, and exact alternating one-pixel native HUD columns. It also requires positive asynchronous GPU query results when queries are supported. Both window and offscreen runs passed with 78 query results across 40 frames. The xunit render test launches this in its own process to isolate GL context/thread ownership.

Create three explicit settings files, then use the same scene/camera across comparisons:

```bash
mkdir -p /tmp/p643-rig
printf '%s\n' '{"Upscaling":"Off"}' > /tmp/p643-rig/Off.json
printf '%s\n' '{"Upscaling":"FSR1","Preset":"Quality","Sharpness":0.5}' > /tmp/p643-rig/Quality.json
printf '%s\n' '{"Upscaling":"FSR1","Preset":"Performance","Sharpness":0.5}' > /tmp/p643-rig/Performance.json

# Local offline example; repeat Off / Quality / Performance and 1920x1080 / 2560x1440.
ALLEGIANCE_GRAPHICS_SETTINGS=/tmp/p643-rig/Quality.json \
ALLEGIANCE_CLIENT_PERF=1 ALLEGIANCE_NOVSYNC=1 \
ALLEGIANCE_CLIENT_PERF_SECONDS=60 \
ALLEGIANCE_CLIENT_PERF_CSV=/tmp/p643-rig/quality-1440.csv \
ALLEGIANCE_CLIENT_PERF_CAPTURE=/tmp/p643-rig/quality-1440.png \
  dotnet run --no-build --project src/Allegiance.ClientCore.MonoGame -- \
  --inspect --window=2560x1440
# Substitute --inspect-stations for the station inspector.

python3 scripts/p643-frame-summary.py --warmup-seconds 10 /tmp/p643-rig/*.csv
```

For the rig, use the established MCP `spawn_scenario` workflow with `{ "preset": "showcase", "size_scale": 13, "mode": "march", "target_sector": "<chosen real rendezvous>" }` (195 bot ships per side), then the existing client direct-connect command:

```bash
ALLEGIANCE_GRAPHICS_SETTINGS=/tmp/p643-rig/Quality.json \
ALLEGIANCE_CLIENT_PERF=1 ALLEGIANCE_NOVSYNC=1 \
ALLEGIANCE_CLIENT_PERF_SECONDS=90 \
ALLEGIANCE_CLIENT_PERF_CSV=/tmp/p643-rig/wide-1440-quality.csv \
ALLEGIANCE_CLIENT_PERF_CAPTURE=/tmp/p643-rig/wide-1440-quality.png \
  dotnet run --no-build --project src/Allegiance.ClientCore.MonoGame -- \
  "$HOST" "$PORT" p643-observer --window=2560x1440
```

`HOST` and `PORT` must identify Claude's authorized test server. Use F8/free cam for a reproducible wide view, the same chase/target view for a close dogfight, and the same station camera for the station view. Allow loading/flight warmup before a stable collection window, keep bloom/effects/sky identical between runs, and repeat **each scene × 1080p/1440p × native/Quality/Performance**. Confirm the adapter is hardware-backed and the telemetry says `GL_TIME_ELAPSED`. If vsync is enabled, the presentation period can hide the bottleneck. If GPU queries are unavailable, document that the classification is inferred from unthrottled CPU/present timing and resolution sensitivity.

For the 200v200 flight GC A/B, repeat the same viewpoint/settings with `ALLEGIANCE_CLIENT_GC_LOWLATENCY=0` then `=1`, using distinct CSV/log names and at least 60 steady seconds each. Verify the player is **flying and undocked**; inspector/menu runs intentionally never engage flight low latency. Redirect stdout to preserve the once-per-second gen2/heap data. Compare frame p50/p99/max and pause totals using the summary script, sum per-second gen2 deltas over the same collection window, and monitor private/RSS as well as the logged managed heap. Preserve p99/max frame IDs to distinguish GC pauses from render stalls. Do not enable the default based on p50 alone or without a memory-growth check.

Allocation sampling on an authorized battle client:

```bash
dotnet tool install --tool-path /tmp/p643-tools dotnet-trace
/tmp/p643-tools/dotnet-trace collect --profile gc-verbose \
  --process-id "$CLIENT_PID" --duration 00:00:01:00 \
  --output /tmp/p643-rig/flight-allocations.nettrace
```

Open the trace in a TraceEvent-compatible allocation viewer, group `GCAllocationTick` by `TypeName`/`AllocationAmount64`, and inspect allocating stacks for combat sources. The local sample used EventPipeEventSource / `Clr.GCAllocationTick` to sum those fields after startup, which is sufficient for type candidates but not stack attribution.

## 680M estimate and classification

No numerical 680M FPS estimate is defensible from llvmpipe. Inference to validate on hardware: particles and overlapping transparent effects, volumetric raymarches, bloom, sky and other large screen-space effects are fill/bandwidth-heavy. Ultra Quality/Quality/Balanced/Performance reduce scene pixel counts to approximately 59%/44%/35%/25% of native (41%/56%/65%/75% less scene pixel work for resolution-proportional effects). EASU and RCAS still run at output resolution; this SM3 port uses 12 + 5 point samples per output pixel and full-precision math, so its fixed cost matters on a 680M. Native HUD cost, CPU updates, object gathering and draw calls do not shrink. P628 LOD is retained; FSR complements its geometry savings only if scene pixel cost is actually the bottleneck.

Classification: **modern addition, native n/a**. No native gameplay, era GC rule or original upscaling rule is asserted or changed, so there is no native source rule needing an era file:line citation. Future temporal FSR/DLSS/XeSS work would need a supported modern graphics backend plus motion vectors, depth, projection jitter, history/reset management and appropriate handling of transparent/reactive effects. It is outside this spatial OpenGL change.

## Verification

Verification completed, including real loopback/network suites.

- Desktop solution: `dotnet build AllegianceCore.slnx`, zero warnings/errors. Android final debug build: zero warnings/errors; shader assets present in the signed APK.
- **53 new/affected test methods**, each run **3x with its own FullyQualifiedName filter**, all passed (378 passing theory/case executions across the three repetitions). [Method list](P643/isolated-methods.txt). The nine new methods cover preset/dimension math, controller convergence/hysteresis/missing samples, settings round trip and corruption, GC opt-out/restoration, delayed frame-ID telemetry, source/bytecode freshness, and actual effect load/render.
- Final-source GL render/load, GC latency restoration and input-map source guard each repeated **3/3 passing** after renderer/UI review.
- Real-window and offscreen GL self-tests passed. Twelve native/Quality/Performance inspector captures completed; PNG dimensions, CSV resolutions and unique frame IDs verified. The Python summary script compiles and reproduces the retained table.

| Full suite (sequential, live/loopback included) | Passed | Failed | Skipped | Duration |
|---|---:|---:|---:|---|
| Core | 4167 | 2 | 0 | 4m40s |
| ClientCore | 868 | 1 | 0 | 4m52s |
| MonoGame | 1986 | 0 | 3 | 4m22s |
| MonoGame final-source repeat | 1986 | 0 | 3 | 4m22s |

The final-source MonoGame repeat was also clean. Additional real-window Native-VFX inspector smoke checks passed with both [upscaling Off](P643/native-vfx-Off.png) and [FSR1 Quality](P643/native-vfx-Quality.png), each 1920x1080 with visible hull pixels in the center of the frame. This covers FSR independently of the Enhanced-VFX post processor.

The three full-run failures all passed **3/3 alone**: Core `LiveRipcordTests.Cancel_while_active_aborts_sector_wide`, Core `GameOverPlayersWireTests.Win_by_elimination_broadcasts_game_over_players_to_surviving_peer`, and ClientCore `ShipDeleteGoneLiveTests.CrossSide_WarperLeavesSector_WatcherLatchesGone`. These are recorded in [TEST_FLAKES](../TEST_FLAKES.md), with their actual assertions and outcomes. The first two were already documented there. No Core/ClientCore production code or test deadlines were modified. The full runs were therefore **not unconditionally green**; their failures meet the brief's load-flake rule. MonoGame's three skips require the optional private Meshy pack, which is not configured on this checkout. No new P643 test was skipped.

Commands used (after sourcing the gitignored `local.env` for available artwork/native corpora):

```bash
# For each method in docs/reports/P643/isolated-methods.txt, repeat this 3 times:
dotnet test tests/Allegiance.ClientCore.MonoGame.Tests --no-build \
  --filter "FullyQualifiedName=$METHOD"
# Then the complete assemblies, in this order, without excluding live tests:
dotnet test tests/Allegiance.Core.Tests --no-build
dotnet test tests/Allegiance.ClientCore.Tests --no-build
dotnet test tests/Allegiance.ClientCore.MonoGame.Tests --no-build
# Each failed FQN was then repeated 3 times against its own assembly.
```

Full TRX/log files remain at `/tmp/p643-tests/`; [portable verification progress](P643/verification-progress.txt) records filters/outcomes. `git diff --check` passes. No commit or push was performed.

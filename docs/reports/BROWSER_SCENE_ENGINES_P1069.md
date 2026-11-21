# Browser scene B2 — engine glow, afterburner exhaust, motion ribbons (P1069)

Started **2026-11-21 09:09**, America/Bogota (UTC−5); five-hour cap **14:09**.
Finished **2026-11-21 12:24** local; task/build/browser processes have exited.
Report written before implementation and kept current. No commits, pushes, package
installations, public-host access or `docker/public/*` changes; scratch is ignored
and removed except the evidence this report links.

## Browser availability — checked first

Node exists. Chromium and the P1051 harness's Playwright /
`@sparticuz/chromium` modules are absent on this lane, and no packages were
installed. Browser captures, RAF timing, WASM heap and visual review are
therefore **for Claude**, with exact workstation commands in the last section.
Every managed, Python and C# proof below runs here.

| Item | Result |
|---|---|
| 1 — Shared preparation | Done: `NativeScene` owns the GPU-free engine preparation; desktop call sites are pure extraction. |
| 2 — Browser engine effects | Done: exhaust, nozzle glow and ribbons gather from the drawn roster and draw with the desktop renderers. WebGL look is for Claude. |
| 3 — Effect load | Done: reduced load clears exhaust first; glow and ribbons stay. |
| 4 — First-draw hitch | Done: budgeted per-frame decode/upload off the draw path, stand-in until uploaded, `firstDrawMs`/`maxShipPassMs` metrics. |
| 5 — Proof | Done except the workstation browser run (no Chromium here). Desktop zero delta proven. |

## Native references

- Exhaust: native `ThingGeoImpl::AddThrustParticles`, `effect/thinggeo.cpp:758-917`
  (`m_fThrustPower > 0` gate `:760`, per-frame count clamp `:770-777`,
  fire/smoke emission `:798-864`).
- Nozzle glow: native `ThingGeoImpl::RenderGlow`, `effect/thinggeo.cpp:1419-1443`
  (`m_scaleGlow > 0`, authored "thrust" frame), fed by `UpdateGlow`
  `effect/thinggeo.cpp:440-460`, HSB→RGB `src/zlib/color.cpp:12-33`.
- Ribbon: native `TrailGeo`, `effect/efgeo.cpp:492-661` (created `:630-638`),
  colour from `ThingSite::SetTrailColor` `effect/thinggeo.cpp:921-954`,
  right vector `SetOrientation` `:1149-1183`.
- Ramp: native `CafterburnerIGC::Update`, `Igc/afterburnerIGC.cpp:133-146`.

## 1. Shared preparation (GPU-free)

`src/Allegiance.ClientCore.Game/Rendering/NativeScene.cs:163-217` adds the pure
preparation both clients compile:

| Shared primitive | Line | Desktop site it reproduces |
|---|---|---|
| `ThrustPowerOnRate` / `ThrustPowerOffRate` | 163 | `Game1.cs` P358 constants (now aliases of the shared values) |
| `TrailWidthFraction` | 168 | `Game1.cs` P363 ribbon half-width |
| `ThrustPower(hasAfterburner, thrusting, previous, dt)` | 174 | `Game1.Rendering.cs:141-153` `UpdateEngineTrail` (ramp + emitter gate) |
| `NozzleGlow(hasAfterburner, stateM, throttle)` | 179 | `Game1.Rendering.cs:174-189` `UpdateNozzleGlow` value |
| `AftWorldOffset(frames, scale, orientation)` | 187 | `Game1.Rendering.cs:5087` `NativeAftWorldOffset` (RY(π) flip + scale) |
| `GlowView(position, orientation, radius, value, aft)` | 201 | `Game1.Rendering.cs:~1536-1557` glow draw block (HSB tint, half-extent, anchor) |
| `AppendMotionTrail(pool, id, position, orientation, radius, r, g, b)` | 215 | `Game1.Rendering.cs:202-213` `UpdateMotionTrail` anchor/right/colour |

The desktop call sites now delegate to these functions with the same inputs,
same draw order, same locks and the same renderer arguments. No desktop
visibility, clock, sector or wire rule changed. Because every desktop edit is an
argument-for-argument delegation of existing arithmetic, the extraction is
value-exact (proved below by exhaustive equivalence tests on the desktop
compilation and a byte-identical real desktop scene pair).

## 2. Browser engine effects

`src/Allegiance.ClientCore.Game.Browser/BrowserSceneEffects.cs:25-89` owns the
desktop P358/P363 pools (`ShipEngineTrailParticles`, `ShipMotionTrailRibbons`) plus
this frame's gathered exhaust emitters and glow views, all in reused lists.
`GatherEngines` (`:162-189`) runs for every ship in the P1065/P1066 drawn roster
(`BattleShipScene.Gather`: `CanDrawInSector`, docked/dead/other-sector excluded,
own ship pose from `_ownSim`) and:

- ramps the thrust power every frame even when the ship is camera-culled, exactly
  like the desktop pose pass;
- attaches an exhaust emitter only when `power > 0` (native `m_fThrustPower > 0`);
- computes the glow value/size/tint through the shared `NativeScene` primitives;
- feeds the ribbon anchor with the real side colour (neutral grey fallback);
- takes the P566 authored nozzle offset only when the hull's mesh is uploaded,
  otherwise the documented radius-based aft-centre anchor — so a stand-in still
  gets glow, exhaust and ribbon.

`Game1.Flight.cs:499-527` calls it inside the ship pass for every roster ship,
stand-ins included. `Game1.Scene.cs:145-160` draws, in the desktop's pass order
relative to P1066's passes: teleport sparks → afterburner exhaust
(`ShipEngineTrailRenderer`) → nozzle glow (`EngineNozzleGlowRenderer`) → ship
strobes → motion ribbons (`ShipMotionTrailRenderer`). The renderers are the
unchanged desktop classes and their billboard/strip paths are already KNI/WebGL
safe (no `.mgfxo`, no soft-particle depth, no float render target). Missing art
falls back to the shared soft sprite; in practice the `scene-engines` pack below
supplies the real `e_fire`/`e_smoke` art.

Own ship trust: the browser has no granted-loadout shop session, so every ship
(own included) uses the desktop's documented remote approximation — the wire
`StateM.AfterburnerButton` bit with the hull's lifepod flag; the wire throttle
drives the ordinary glow, exactly as the desktop remote path does.

## 3. Effect load

`Game1.Scene.cs:111` passes the browser-only P1066 `FullEffectLoad` decision into
the scene state before the frame. `BrowserSceneEffects.BeginFrame:88-89` clears
the exhaust pool and skips its aging on the first reduced frame, and
`GatherEngines:176` stops new exhaust emission. Nozzle glow and ribbons are
untouched. `Game1.Scene.cs:145` also skips the exhaust draw. This is the
requested ordering: **exhaust particles off first**, then (unchanged P1066
behaviour) resolution scales. The ordering is covered by
`P1069EngineSceneTests.Reduced_effect_load_turns_exhaust_off_first_and_keeps_glow_and_ribbons`
and the existing RAF/effect-load ordering test `tools/p1066-frame-tests.cjs`
(2/2 pass, effect change strictly before any presentation resize).

## 4. First-draw hitch

The P1065 hitches were the first draw of a newly promoted hull decoding and
uploading its mesh in one frame. `src/Allegiance.ClientCore.Game.Browser/BrowserHullUploads.cs`
queues each hull once, when its verified bytes become resident, and decodes /
uploads **at most one hull per frame** in the update path
(`Game1.Flight.cs:362`); the ship draws its existing radius-sized stand-in until
then (`Game1.Flight.cs:505-521`). The upload calls the same
`NativeShipRenderer.MeshFor`/`TextureFor` cache the draw path used, so the work
is moved, not changed. The queue resets on mission/session change; a sector
view change keeps the resident renderer cache (nothing to re-decode) while the
effect pools themselves reset on every view change, as P1066's `ResetScene` did.
Metrics (`Game1.Flight.cs:182-187`): `firstDrawMs`,
`maxFirstDrawMs`, `maxShipPassMs`, `hullUploadMs`, `hullQueued`, `hullUploaded`.

Managed promotion proof ([promotion.json](BROWSER_SCENE_ENGINES_P1069_assets/promotion.json),
Release JIT, the 179 shipping hull models from `PCore001.igc`, `MdlFile.Parse`
is the CPU decode half of `MeshFor`; the GPU buffer/texture build is the same
budgeted step inside the browser and is not measurable headless):

| Hulls promoted together | Old: one-frame decode | New: frames spread (1/frame) | New max decode in any frame |
|---:|---:|---:|---:|
| 2 | 0.044 ms | 2 | 0.029 ms |
| 5 | 0.139 ms | 5 | 0.084 ms |
| 10 | 0.445 ms | 10 | 0.084 ms |
| 20 | 0.955 ms | 20 | 0.084 ms |

All 179 models decode in 7.683 ms in total; the worst single hull decode is
1.7 ms. The hitch fix bounds per-frame promotion work to one hull's decode +
upload instead of the whole batch.

## 5. Artwork delivery (P1066 scene-pack path)

`tools/p1066-scene-packs.py` adds a `scene-engines` closure with the original
bytes the desktop exhaust renderer decodes: `e_firebmp.mdl` + `e_fire.png` and
`e_smokebmp.mdl` + `e_smoke.png` (the nozzle glow reuses `f101bmp.mdl` from
`scene-b1`). Content-addressed, verified, `application/octet-stream`; the join
manifest is unchanged. **11,946 raw / 12,014 gzip bytes**; the P1054 join+runtime
metric moves from 13,758,095 to **13,770,109 bytes**, still under the
14,680,064-byte budget. The pack is requested through the existing
`RequestBattlePack`/`BattlePackReady` path from `Game1.Scene.cs:82-86`; no page,
lobby or browser-menu source was touched. The Python closure/licence regression
(`tools/test-p1066-scene-packs.py`) asserts the engine pack's exact four sources.

## 6. Per-frame preparation cost

[benchmark.json](BROWSER_SCENE_ENGINES_P1069_assets/benchmark.json) from
`tools/p1069-scene-proof --benchmark`, Release JIT with tiering disabled, 2,000
warm-up and 20,000 measured passes. Every fixture ship is a drawn, boosting
wire pose. **Not** .NET-interpreter, RAF or GPU time; camera/prediction, asset
decode/upload, WebGL drawing, HUD and JS pacing are excluded.

| Ships | Before µs/pass (P1066 B1) | B2 full µs/pass | B2 reduced µs/pass | Allocated bytes/pass | Live exhaust / glows / ribbons |
|---:|---:|---:|---:|---:|---|
| 2 | 0.439 | 1.167 | 0.247 | 0 / 0 / 0 | 282 / 2 / 2 |
| 10 | 1.958 | 5.850 | 1.100 | 0 / 0 / 0 | 1,410 / 10 / 10 |
| 20 | 3.783 | 10.274 | 2.238 | 0 / 0 / 0 | 2,048 / 20 / 20 |

Reduced load has no exhaust particles (0 at every size) and keeps the glows and
ribbons. Steady-state allocations are zero in all 6 rows, matching the
`Warm_engine_preparation_for_thrusting_ships_allocates_no_steady_state_bytes`
test at 2/10/20 ships.

## 7. Desktop zero delta

Baseline = `git archive HEAD` (`978250f4`) built independently in ignored scratch;
only preinstalled packages.

- **1,848/1,848 HUD cases passed:** 0 pixel differences, 0 semantic differences,
  83 animation-mask-only differences (the documented Modern wall-clock badge),
  0 corrupt goldens. [desktop-hud.json](BROWSER_SCENE_ENGINES_P1069_assets/desktop-hud.json).
- **753/753 desktop output filenames identical**, no added/removed paths.
  [desktop-filenames.json](BROWSER_SCENE_ENGINES_P1069_assets/desktop-filenames.json).
- **Byte-identical real desktop scene pair:** the actual Game1 flight loop
  (Xvfb/llvmpipe, 640×360, real native hull + exhaust + glow + ribbon emitters)
  rendered from the baseline and the current tree is **128,380 bytes each,
  SHA-256 `3dd8e912ff0055fca1d5986c762f6a5cf3e7fc43a5ebe95b86137f12ffbcb0aa`**.
  The harness froze the loopback world, used a fixed 20 Hz step, fixed particle
  seeds and constant session clocks, and hid the HUD — the HUD's own wall-clock
  blink is not byte-comparable frame-to-frame and is proven separately by the
  1,848-case comparison above. Emitter counts on the frame:
  `engineEmitters=1 glowEmitters=1 ribbons=1`.
  [Before](BROWSER_SCENE_ENGINES_P1069_assets/desktop-scene-before.png),
  [after](BROWSER_SCENE_ENGINES_P1069_assets/desktop-scene-after.png),
  [receipt](BROWSER_SCENE_ENGINES_P1069_assets/desktop-scene.json).
- Exhaustive desktop equivalence tests
  (`tests/Allegiance.ClientCore.MonoGame.Tests/P1069EngineExtractionTests.cs`)
  recompute every pre-extraction formula on the desktop compilation over 500
  ramp frames × 4 mount/button paths, 1,001 glow values × 3 radii + the authored
  offset, the full throttle/state grid, 20 ribbon ships and the RY(π) offset.

## 8. Tests

| Test | Cases | Alone ×3 |
|---|---:|---|
| `P1069EngineSceneTests` (browser KNI scene) | 11 | 11 passed ×3 |
| `P1069EngineExtractionTests` (desktop compilation) | 6 | 6 passed ×3 |
| `tools/test-p1066-scene-packs.py` | 3 | 3 passed ×3 |
| `tools/p1066-frame-tests.cjs` (existing RAF/effect ordering) | 2 | 2 passed |

The browser tests cover: the drawn-roster gather (stand-ins included, docked /
other-sector / dead excluded); thrust ramp + glow tint/size/anchor equal to the
desktop formulas for the same inputs; authored aft offset vs radius fallback;
ribbon anchor/right/side-colour and the native brightness taper; reduced-load
ordering (exhaust off, glow/ribbons kept); reset on session and view change
(including a restarted ramp); zero steady-state allocation at 2/10/20 thrusting
ships; and the upload queue (one hull per frame, stand-in until uploaded,
promotion→first-draw delay, unreadable model, reset, budget > 1).

Full projects: **Game.Browser.Tests 178 passed**, **Browser.Tests 49 passed**,
desktop MonoGame tests and the lobby suite in the gate table.

## Gates

| Check | Result |
|---|---|
| Solution build | **0 warnings / 0 errors** |
| `tools/build-tools.sh` | **152 tool projects built**, 0-warning/error |
| New/changed tests alone ×3 | browser 11×3, desktop 6×3, Python 3×3 — all passed |
| Browser game-model project | **178 passed** |
| Browser preview/entry project | **49 passed** |
| Desktop MonoGame project (Fast tier filter) | **6,008 passed, 3 skipped** |
| Lobby suite | **329 passed, 3 optional PostgreSQL skips** |
| HUD zero-delta | **1,848/1,848**, 0 pixel/semantic differences |
| Desktop filename manifest | **753/753 identical** |
| Desktop scene pair | **byte-identical** |
| Glossary (`check-glossary-links.py`) | **PASS**, 1,075 paths |
| Parity CSV | `docs/parity/p1069-browser-engines.csv`, 6 rows, LF |
| Final Fast tier (last) | **RED:** 15,881 passed / 1 failed / 7 not executed; 4 timing offenders; 644.3 s |

The final Fast sweep exercised **15,889 cases** in 644.3 s with all 9
infrastructure checks passing and a calibrated **12.090 s** per-case limit. The
one assertion failure is outside this task: `WarpTransitServerTests.ShipInsideWarp_GetsTransited_AndPeerSeesSectorChange`
saw only same-sector `SHIP_STATUS` entries (line 146) and passed 3/3 alone
(363–368 ms). The four timing offenders are the existing chronic load-sensitive
families; each whole theory passed 3/3 alone with an isolated per-case maximum of
6.84 s. [Flake ledger](../TEST_FLAKES.md) records both. **Fast remains red**;
isolated passes do not erase the sweep result.

## Workstation browser run — for Claude

No npm/NuGet installation; use the workstation's already-installed P1051
modules. Run from the repository root; all endpoints stay on loopback.

```bash
set -euo pipefail
export DOTNET="$HOME/.dotnet/dotnet"
export NUGET_HTTP_CACHE_PATH="$PWD/.scratch/p1069/nuget-http"
export ALLEGIANCE_ARTWORK_PATH=/srv/corpus/Artwork  # workstation artwork path
export BROWSER_CLIENT_VERSION=2.0.0
NPM_DIR=.scratch/p1051/npm
test -d "$NPM_DIR/node_modules/playwright"
test -d "$NPM_DIR/node_modules/@sparticuz/chromium"
for project in tools/p1051-browser-host tools/allegiance-mcp src/Allegiance.ClientCore.MonoGame src/Allegiance.ClientCore.Browser; do
  "$DOTNET" build "$project" --disable-build-servers -nodeReuse:false -p:UseSharedCompilation=false \
    -p:RestoreSources="$HOME/.nuget/packages" -p:NuGetAudit=false
done
mkdir -p .scratch/p1069/traces
python3 - <<'PY'
import gzip, json
from pathlib import Path
traces = json.loads(gzip.decompress(Path('docs/reports/BROWSER_FLIGHT_P1051_assets/desktop-opens.json.gz').read_bytes()))
for name, trace in traces.items():
    target = Path('.scratch/p1069/traces') / name
    target.parent.mkdir(parents=True, exist_ok=True)
    target.write_text(json.dumps(trace))
PY
# Fresh generation; the publisher refuses to overwrite an existing directory.
scripts/publish-browser-flight.sh .scratch/p1069/browser-generation \
  .scratch/p1069/traces/opens/Classic.json .scratch/p1069/traces/opens/Modern.json \
  .scratch/p1069/traces/flight-opens4/Classic.json .scratch/p1069/traces/flight-opens4/Modern.json \
  .scratch/p1069/traces/flight-miners0/Classic.json .scratch/p1069/traces/flight-miners1/Modern.json
state_dir=$(mktemp -d "$PWD/.scratch/p1069/browser-host-XXXXXX")
"$DOTNET" tools/p1051-browser-host/bin/Debug/net10.0/p1051-browser-host.dll \
  "$ALLEGIANCE_ARTWORK_PATH" .scratch/p1069/browser-generation "$state_dir" --bots 5 --seconds 1200 \
  > "$state_dir/host.log" 2>&1 &
host_pid=$!
cleanup() { touch "$state_dir/stop"; wait "$host_pid" || true; }
trap cleanup EXIT INT TERM
node tools/p1069-browser-engines.cjs "$NPM_DIR" "$state_dir" \
  "$PWD/docs/reports/BROWSER_SCENE_ENGINES_P1069_assets/browser" \
  .scratch/p1069/browser-generation 40
cleanup; trap - EXIT INT TERM
```

The harness is syntax-checked (`node --check`) but not runtime-qualified on this
lane. It drives identity → join → side → launch in `/play/menu.html?dev=1`, flies
to Ixion through the real autopilot, sets full throttle (`Backslash`) and holds
the afterburner (`LeftShift`), waits until exhaust/glow/ribbon counts are live,
then samples every 500 ms in Classic and Modern at 1920×1080 and 800×480 and
captures JPEG q80 (`engines-5-*.jpg`) plus one dedicated close-behind-afterburner
frame. It asserts a live combat frame in the target sector each capture and
refuses an empty effect set; a pilot loss must be retried, never relabelled. The
JPEG set must stay ≤ 2 MiB. Expected outputs:
`engines-5-classic-1920x1080.jpg`, `engines-5-modern-1920x1080.jpg`,
`engines-5-classic-800x480.jpg`, `engines-5-modern-800x480.jpg`,
`engines-close-afterburner-1920x1080.jpg`, `browser-engines.json` (samples,
RAF p50/p95, `maxShipPassMs`, `maxFirstDrawMs`, peak effect counts, WASM linear
memory, capture sizes). Claude must open every JPEG, describe only the visible
ships/plume/glow/ribbons and both HUD styles, and append the measured table.

## Native intent / Modern approach

**Native intent:** every thrusting drawable ship emits the native afterburner
fire/smoke plume (`thinggeo.cpp:758-917`), carries the HSB nozzle glow at its
authored nozzle frame (`thinggeo.cpp:440-460,1419-1443`), lays the side-coloured
`TrailGeo` ribbon (`efgeo.cpp:492-661`) and gates it all on the ramped
afterburner power (`afterburnerIGC.cpp:133-146`).

**Modern approach:** one shared GPU-free preparation layer used by both clients
(the desktop call sites delegate without changing values or order); the browser
gathers the same drawn roster, reuses the unchanged desktop renderers and pools,
turns exhaust off first under reduced load, streams the original fire/smoke art
through the verified scene-pack path, and spreads a promoted hull's decode/upload
over frames instead of one hitch. Desktop pixels/HUD and every server/wire rule
stay unchanged; the WebGL look and its timing are the deferred workstation run.

## Unfinished

- The Chromium 5 v 5 run, its JPEG captures and visual descriptions are for
  Claude (no Chromium on this lane), using the commands above.
- The desktop scene-pair frame hides the HUD because the HUD's wall-clock blink
  is not frame-comparable; HUD zero delta is proven by the 1,848-case comparison.
- No real-GPU frame cost or browser WASM heap is claimed here.

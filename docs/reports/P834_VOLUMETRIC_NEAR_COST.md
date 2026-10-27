# P834 — volumetric explosions slow down when the camera is close: resolution-aware fill budget, quarter-res near pass, camera-inside handling

**Base:** public `master` at dispatch (`8f7dda9d`).
**GPU:** this workstation only — Intel Arc Pro B50 (BMG G21, xe driver, Mesa 26.0.8), Xwayland,
`DISPLAY=:0`. No rig, no Mac, no VA-API post-processing.
**Driver of the task:** Domingo, 2026-10-26 on the 200v200 rig, VOLUMETRIC tier (raymarch A),
window maximised ≈ 4096×2154 of a 5120×2160 monitor: *"performance is crazy good, the volumetric
explosions need some work, when too close to the animation it gets slow"*. Evidence:
`~/allegiance-evidence/P834/close-explosion-64s.png` (a plume around the own ship) and
`unique-frames-per-second.txt` (recorder-capped ~26 fps; the dip to 18 at t=64 s is that explosion).

---

## 0. Outcome

The near/screen-filling volumetric cost is fixed **on the GPU** and the P579 policy is now
resolution-aware. A new repeatable harness renders the **real** `VolumetricRenderer` +
`Volumetric.fx` at 1080p/1440p/4K/5K2K and measures the volumetric pass with GL timer queries.
Measured on the B50, mean GPU time of the volumetric pass per frame (min of two interleaved
before/after runs, 100 frames each):

| scenario (blast directly ahead) | 1920×1080 | 2560×1440 | 3840×2160 | 5120×2160 |
|---|---|---|---|---|
| **far** — fighter @1500 u (10 % of height)      | 0.45 → 0.46 ms | 0.60 → 0.61 ms | 0.55 → **1.09** ms | 0.53 → **1.03** ms |
| **near** — fighter @450 u (33 %)                | 0.32 → 0.27 ms | 0.45 → 0.46 ms | 1.06 → 1.09 ms | 1.06 → 1.07 ms |
| **screen-filling** — fighter @150 u (100 %)     | 2.04 → **0.42** ms | 3.46 → **0.88** ms | 6.16 → **2.24** ms | 7.69 → **2.50** ms |
| **screen-filling** — bomber blast @450 u (157 %) | 4.96 → **0.96** ms | 8.22 → **2.00** ms | 15.43 → **4.45** ms | 16.76 → **4.52** ms |
| **screen-filling** — station blast @800 u (188 %) | 6.55 → **1.23** ms | 8.47 → **1.94** ms | 21.34 → **3.84** ms | 22.11 → **5.81** ms |
| **camera inside** — bomber blast (r 165 u) @60 u | 5.19 → **1.39** ms | 9.16 → **1.91** ms | 23.81 → **4.21** ms | 32.67 → **7.65** ms |

**Stated target met:** the worst case (camera inside a bomber plume at **5K2K**) drops from
**32.7 ms to 7.7 ms** — **4.3× faster**, 46 % of a 16.7 ms frame, so a screen-filling blast at
4K/5K2K holds **≥ 60 fps** (4K camera-inside 4.2 ms, 5K2K 7.7 ms; the rest of the 16.7 ms frame is
left to the scene). A third confirmation run of the final build measured 5K2K camera-inside
7.23 ms, 4K camera-inside 5.27 ms and 5K2K station 5.35 ms (§3). `station`/`bomber`/`full` improve 3–5.6× at every resolution. The only cell
that gets *more* expensive is **far at 4K/5K2K (+0.5 ms)**, and that is the deliberate fix, not a
regression (see §1.1 root cause 4 and §6: the old absolute-pixel thresholds gave the same on-screen volume a *cheaper*
level at 4K than at 1440p; it now costs ~1 ms, 6 % of a frame).

Three levers, in the order they matter: **quarter-res** for screen-filling volumes, the
**48-step `Minimal` level** for anything that covers the frame, and the **resolution-aware
pixel-step budget** (per screen pixel, measured on the B50) with a bounded, smooth,
GPU-agnostic adaptive fallback. The shader also clamps the march to the **visible depth range**
(near plane → opaque scene), which is the camera-inside correctness fix and is worth a further
~50 % when a hull occludes part of the plume.

---

## 1. Measure first — the harness

New one-off tool **`tools/p834-vol-perf/`** (built by `tools/build-tools.sh`, not in the solution):

`tools/p834-vol-perf/Program.cs` opens a real DesktopGL window at the requested size, loads the
**committed** `Content/Shaders/Volumetric.ogl.mgfxo` into the **real**
`Rendering.VolumetricRenderer`, injects one deterministic `ExplosionEntity` at a fixed distance
straight ahead, and draws it through the production `Draw()` (same sampler, budget, LOD,
half/quarter-res passes, composite). It brackets the pass with `Diagnostics.GlFrameTimer`
(`GL_TIME_ELAPSED`, asynchronous — the same timer the client's P643 telemetry uses) and also logs
wall frame-time percentiles with vsync off. Diagnostics (`LastVolumeCount`, `LastFullResVolumes`,
`LastHalfResVolumes`, `LastQuarterResVolumes`, `LastPixelSteps`, `LastBudget`) are read
reflectively, so the **same harness binary** measured the pre-fix renderer and the fixed one.

```bash
# one process per resolution (see §8 for why); 4 resolutions
tools/p834-vol-perf/run.sh before .scratch/p834/before --frames 100 --warmup 25 --png .scratch/p834/before
tools/p834-vol-perf/run.sh after  .scratch/p834/after  --frames 100 --warmup 25 --png .scratch/p834/after
# scenario grammar: label:type:distance (type = native explosion category)
#   default far:3:1500, near:3:450, full:3:150, bomber:4:450, station:7:800, inside:4:60
# extras: --forced none|low|med|high|ultra, --budget <pixel-steps>, --age <s>,
#         --depth DIST[:R] (synthetic linear-depth occluder disc), --fov <deg>
```

Blast age is fixed at **0.80 s** (the first 3–4 sub-volumes at full radius, still hot — the phase
the evidence screenshot shows). 50° vertical FOV (the flight-camera default), so "screen-filling"
means the projected diameter equals/exceeds the viewport height at *every* resolution.

Raw per-frame CSVs and the raw 4-resolution PNGs: `~/allegiance-evidence/P834/p834-final/`,
`~/allegiance-evidence/P834/raw-frames-{before,after}/`.

### 1.1 The curve before the fix (master `8f7dda9d`)

The table in §0 (left of each arrow) is the P579 policy measured this way. It reproduces the
report: at 4K the station blast costs 21.3 ms and the camera-inside case 23.8 ms of GPU time
**for the volumetric pass alone**; at 5K2K 22.1 / 32.7 ms. Add the rest of the frame and the
client is at ~30 fps exactly when the camera flies into a plume — the t=64 s dip.

Root causes found by measurement (all four are visible in the diagnostics):

1. **The budget's floor did not fit.** `MaxPixelSteps` was the absolute 42 M pixel-steps tuned at
   1440p, but a *single* screen-filling volume sits at Low/64 steps and cannot be degraded further.
   At 5K2K the camera-inside case's nominal cost was **708 M pixel-steps** (4 × half-res 2560×1080
   × 64) — 17× the budget, and the allocator had nothing left to take away.
2. **Half of 4K/5K2K is still > 1080p.** `NearHalfResMinPx = 240` px absolute meant half-res of a
   5K2K frame is 2560×1080 → 177 M pixel-steps for *one* volume at 64 steps.
3. **Camera inside marched the whole chord.** `RaySphere` clamps `t0 = max(tA, 0)`, so with the eye
   inside the bounding sphere the march started at 0 (in front of the near plane) and ran to the
   far intersection — including everything behind hulls.
4. **The area metric over-counted the camera-inside case.** A radius-165 sphere 60 u ahead projects
   a 25,476 px diameter → `diamPx²` = **649 M px** of "area" for a frame that holds 11 M, so the
   budget degraded every *other* volume on a bogus cost.
   Plus the resolution-blind thresholds: at 4K a volume covering 10 % of the height (216 px) fell
   into the old absolute `160 px` bucket → Med (128 steps), while the identical screen coverage at
   1440p (144 px) was High (255) — i.e. the *same picture* got a different quality depending on
   the monitor.

## 2. What changed

### 2.1 Coverage is a screen fraction, and there is a 48-step floor
`src/Allegiance.ClientCore/State/VolumetricQuality.cs` — `VolumeLod`:

* thresholds are fractions of the viewport height (`UltraMaxFraction = 56/1440`, `High = 160/1440`,
  `Med = 420/1440`), so the same on-screen volume gets the same level at every resolution;
  `ForPixelDiameter` remains as the P579 reference-height entry point.
* new cheapest level **`Level.Minimal` = 1×48 steps** for volumes that cover ≥ 100 % of the height
  (screen-filling / camera inside) — the "fewer, longer steps" rung. `LevelCount` = 5, cost order =
  enum order = the budget's degradation ladder.

### 2.2 Quarter-res pass chosen by coverage
`VolumeScreenPolicy` (`VolumeRenderScale.Full/Half/Quarter`): ≥ 75 % of the height → quarter
(1/16 the marched pixels), ≥ 16.7 % (P579's 240 px @1440p) → half, else full.
`Rendering.VolumetricRenderer` renders three bucket sets — full into the current target, half into
a half-size RT, quarter into a quarter-size RT — and composites each downscaled target up with the
existing raw-additive (`One, One`) `SpriteBatch` upsample, restoring the actual previous render
targets and satisfying the P582b lossless-excursion guard for both passes.

### 2.3 Resolution-aware budget + adaptive fallback
`VolumeBudget`:

* `PixelStepsPerViewportPixel = 18` — **the number**: measured on the B50 the raymarch sustains
  ≈ 17–23 G pixel-steps/s at 5K2K (two independent measurements agree: pre-fix 708 M / 30.5 ms,
  post-fix 133 M / 5.7 ms). 18 pixel-steps per screen pixel = **66 M at 1440p, 149 M at 4K,
  199 M at 5K2K ≈ 8.6–11.5 ms** — the volumetric share of a 16.7 ms frame, which is what keeps a
  screen-filling 4K/5K2K blast at ≥ 60 fps.
* `MaxPixelStepsFor(width, height)` = `max(42 M, 18 × width × height)`: scales by viewport pixels,
  never below the P579 1440p-tuned value (1080p keeps 42 M).
* volume cost = projected area × steps **at the volume's render scale** (`area / 16` for quarter),
  and the projected area is **clamped to the viewport** — a volume can never shade more pixels than
  the frame holds (fix 4 above).
* `VolumeBudgetController` — the GPU-agnostic fallback: the budget is multiplied by a scale in
  **[0.25, 1]**, lowered by 0.06 and raised by 0.02 per 0.25 s window when the *measured* frame
  time is above 1.15× / below 0.85× the 16.67 ms target. Bounded and smooth (1.0 → 0.25 over ~3 s,
  never a cliff), driven only by measured frame time (no vendor/GPU queries), and a vsync-locked
  60 Hz frame (16.7 ms) never triggers a change. Fed from
  `Game1.FlightInput.Update` (`VolumetricRenderer.ObserveFrame`), alongside the existing P573
  last-resort tier drop. Unit-tested headless.

### 2.4 Camera inside / near: clamp the march to the visible depth range
`VolumetricGeometry.ClampMarchRange` + `Volumetric.fx ClampVisible`: the pixel shader fetches the
opaque scene's linear depth **once**, then the march starts at the near plane
(`tNear = Near / -rd.z`) and stops at the scene (`tScene = (scene/InvRange + Near) / -rd.z`);
`clip()` when nothing survives. `DepthFadeScene` uses the same fetch (one depth fetch per fragment,
as before). Effect measured at 5K2K with a synthetic 40 u occluder disc covering the middle of the
frame: camera-inside **7.65 → 3.46 ms** (a repeat measured 3.83); station blast **5.81 →
0.18 ms** (the whole volume is behind the hull). See `docs/reports/P834_assets/depth_clamp_inside.png`.

> **P834b — the `-rd.z` cosine above is superseded.** It only holds for a camera looking down world
> −Z (the one orientation this section measured), so every other heading clipped the volume away;
> the shipped shader now uses `dot(rd, ViewForward)` with the camera axis passed in from the view
> matrix. See **§9**, which re-measures this section's effect at other headings (the occluder result
> holds: 1.97–2.14 → 0.69–1.00 ms at yaw 0/90/180).

### 2.5 Shader: the 48-step level keeps the look
`RaymarchMinimal` (1×48). `MarchAcc` gained `extScale`: per-step extinction is scaled by 64/48 so
the **optical depth per unit distance is unchanged** — `trans *= exp(-dens·3.2·extScale)`. This is
required, not cosmetic: with the plain formula the 32-step level I tried first was visibly
brighter (mean luminance +38 %, 6.9 % of the frame blown out vs 0 % before) because the
transmittance series is not step-count invariant. 32 steps *also* aliased the finest fBm octave on
a radius-165 volume (step ≈ noise cell size); 48 steps at quarter-res matches the 64-step look to
within a few percent (measured full-frame mean luminance: full 20.4→20.6, bomber 45.4→47.4,
station 64.7→67.9, inside 179.6→191.5, blown-out pixels 0 % in every case).

## 3. After the fix — the same curve

Values are the left→right arrows in §0; the measured **nominal** pixel-steps and budget printed by
the harness for the 5K2K runs (final build, 48-step `Minimal`): far 47.6 M / 199.1 M, near
33.1 M / 199.1 M, full 63.6 M / 199.1 M, bomber 120.4 M / 199.1 M, station 132.7 M / 199.1 M,
inside 132.7 M / 199.1 M — every case inside the budget, so nothing is degraded at 5K2K (the
budget now bites only for genuinely many-volume scenes). At 4K: far 47.6 M, near 33.1 M, full
63.6 M, bomber/station/inside 99.5 M, all under 149.3 M.

Spread across the runs, for honesty — 5K2K camera-inside 7.65 / 7.79 / 7.23 ms; 4K camera-inside
4.21 / 4.25 / 5.27 ms; 5K2K station 5.81 / 5.95 / 5.35 ms; 4K station 3.84 / 4.41 / 5.05 ms. The
machine is shared (other suites were building/running all session), so each cell in §0 is the
**minimum of two interleaved before/after runs measured in the same sitting**, and the third run
above is the confirmation with the final cost model.

### 3.1 Side-by-side frames

`docs/reports/P834_assets/` (all 2560×1440, before = master, after = this change):

| file | what it shows |
|---|---|
| `sbs_far.png` | fighter @1500 u: **no visible difference** (full-res, High/255 both sides) |
| `sbs_near.png` | fighter @450 u: **no visible difference** (half-res + Low/64 both sides) |
| `sbs_screen_filling.png` | fighter @150 u: same silhouette/size/brightness; the after hot core is slightly rounder |
| `sbs_camera_inside.png` | bomber blast from inside: same orange wash and the same dark top-left lobe; the after is slightly paler/smoother |
| `crop_screen_filling_1to1.png` | 1:1 centre crop: the quarter-res upsample **softens the internal fBm mottling and the lobe edges**; silhouette and brightness match |
| `depth_clamp_inside.png` | after, camera inside, with/without a 40 u hull: the hull correctly hides the volume behind it |
| `real_window_blast.png` | real client window 2560×1440 with `ALLEGIANCE_VOL_PROOF` (fighter death @150 u): fireball rendered through the quarter-res pass, HUD + starfield intact (no black-world regression) |

I opened all of them. The honest summary of the visible difference: **nothing changes for far/near
blasts; the screen-filling and camera-inside plumes keep their shape, size and brightness but lose
some internal fine noise and edge crispness** where the quarter-res target is upsampled (visible in
the 1:1 crop), and the camera-inside wash is ~6 % brighter on average (no blown-out pixels added).
That is the intended trade (quarter-res of a full-frame plume, 3–5× less GPU time).

## 4. Verification (light gate)

* `dotnet build AllegianceCore.slnx` — **0 warnings, 0 errors**.
* `tools/build-tools.sh` — all tool projects build (includes `tools/p834-vol-perf`).
* New tests `tests/Allegiance.ClientCore.MonoGame.Tests/P834VolumetricNearCostTests.cs`
  (31 tests): budget scales with viewport · same screen fraction picks the same LOD/scale at every
  resolution · quarter-res is 1/16 the marched area and is what makes the camera-inside cost fit
  the 5K2K budget · `Minimal` is the cheapest rung · inside/near/degenerate march clamps ·
  adaptive controller (bounded, smooth, vsync-immune, invalid input ignored). **3× alone: green
  each time** (31/31, 31/31, 31/31).
* P573/P579/P582b/P834 volumetric tests still green (97/97 with the P579/P573/P582 filter, 71/71
  with the focused P834+P573/P579/P582b filter); the one P579 assertion that named the old
  floor (`Budget_never_degrades_below_low`) now asserts the new cheapest level and says why.
* ONE `tools/run-tests.sh --tier fast` — see §5.
* Real-window smoke test (local server + windowed client, §3.1 last row): tier resolves to
  VOLUMETRIC, `[volumetric] pre-warmed shader LODs + half/quarter-res targets`, blast renders
  through the quarter-res composite, no exception, no black world.

No server/sim code touched; ENHANCED and billboard/Native tiers untouched (`DisplacedSphereRenderer`,
`EnhancedExplosionRenderer`, `ExplosionSampler` unchanged). Manifest/JSON/Docker untouched (no new
service, store, port or env var — the harness's `--depth`/`--forced` flags are tool-only).

## 5. Fast-tier run

`tools/run-tests.sh --tier fast` (one run, after the final build + the 3×-alone new tests):

```
Discovery counts: Lobby 82 fast / Update 28 / MonoGame 3522 / ClientCore 1821 / Core 5410
fast: 10863 tests, 306.5s, PASS
  Allegiance.Update.Tests         28 passed
  Allegiance.Lobby.Tests          82 passed
  Allegiance.ClientCore.MonoGame.Tests  3519 passed, 3 not executed (documented skips)
  Allegiance.ClientCore.Tests     1819 passed (+2 quarantine)
  Allegiance.Core.Tests           5400 passed (+10 quarantine)
PASS: TestResults/tiers-*/summary.json
```

(An earlier fast run failed only on my own new assertion while I still had the CPU step table at 32
for the shader's 48-step `Minimal` technique — the failing-first catch; fixed in
`VolumeLod.StepsFor`, re-ran the new tests 3× alone and then the whole fast tier above.)

## 6. Face-finding notes for review

* The **only regression** is far/medium volumes at 4K/5K2K (+0.5 ms, 0.55→1.09 ms) — the level is
  now the one the screen coverage implies (High/255 instead of the old absolute-pixel Med/128). If
  you would rather keep the extra cheapness at 4K, raise `VolumeLod.HighMaxFraction`; that restores
  the old mismatch between resolutions, so I did not.
* `VolumeBudget.PixelStepsPerViewportPixel = 18` is a **measured** constant for this GPU class; a
  slower GPU is handled by the adaptive controller (bounded at 0.25× → ~50 M pixel-steps at 5K2K)
  and then by the existing 80 ms/3 s tier drop. It is not a per-vendor table.
* The adaptive controller uses *whole-frame* time as the proxy (a GPU-agnostic signal). A frame
  slowed by something other than the explosion lowers the volumetric budget too; it only caps the
  raymarch, recovers at 0.02/0.25 s, and a vsync-locked 60 Hz frame never moves it.
* The 48-step level's look is calibrated against the 64-step Low level (optical depth + measured
  mean luminance). If the art direction wants the fastest possible plume, reducing it further
  aliases the fBm (measured at 32 steps) — do not go below 48 without changing the noise frequency.

## 7. Files

New:
* `tools/p834-vol-perf/` — `Program.cs` (harness), `run.sh` (one process per resolution),
  `orientation-sweep.sh` (P834b orientation table), `p834-vol-perf.csproj`.
* `tests/Allegiance.ClientCore.MonoGame.Tests/P834VolumetricNearCostTests.cs`.
* `docs/reports/P834_assets/*.png` — 7 frames: before/after pairs for far, near, screen-filling and
  camera-inside, a 1:1 crop, the depth-clamp pair and the real-window frame (≤ 0.5 MB each,
  1.2 MB total).

Changed:
* `src/Allegiance.ClientCore/State/VolumetricQuality.cs` — fraction thresholds, `Minimal` level,
  `VolumeRenderScale`/`VolumeScreenPolicy`, per-screen-pixel budget, `VolumeCost` + area clamp,
  `VolumeBudgetController` (all pure/unit-tested).
* `src/Allegiance.ClientCore/State/VolumetricGeometry.cs` — `ClampMarchRange`/`MarchRange`.
* `src/Allegiance.ClientCore.Game/Rendering/VolumetricRenderer.cs` — three bucket sets, quarter-res
  RT, half/quarter composite with the P582b target guard, scale-aware budget, diagnostics,
  `ObserveFrame`.
* `src/Allegiance.ClientCore.Game/Shaders/Volumetric.fx` (+ compiled
  `Content/Shaders/Volumetric.ogl.mgfxo` and its `.fx.sha256`, rebuilt with
  `mgfxc /Profile:OpenGL`) — `ClampVisible`, one depth fetch per fragment, `RaymarchMinimal`,
  `extScale`.
* `src/Allegiance.ClientCore.Game/Game1.FlightInput.cs` — feed the adaptive controller each frame.
* `tests/.../P579VolumetricAdoptTests.cs` — the floor assertion follows the new cheapest level.
* `GLOSSARY.md` — a "Volumetric explosions (raymarch tier)" entry pointing at the policy, the
  renderer/shader and this report (`tools/check-glossary-links.py` passes).

Native: no new native rule is ported here — the change is the **port's own** fill-rate policy.
The native inputs the visuals still consume are untouched: per-decal stagger/offset/scale
(`effect/explosion.cpp:112-119`), 20 fps film (`effect/explosion.cpp:183`), and the per-type
count/radius/tint table (`WinTrek/trekigc.cpp:95-177`). No `docs/parity/*.csv` row changes.

## 8. Caveats / unfinished

* **Harness vs game.** The harness draws the production renderer/shader/RT path into the real
  backbuffer, but with a single synthetic blast over an empty scene, so the numbers are the
  volumetric pass in isolation (which is exactly the cost the task is about). The client's real
  frame adds the normal scene (~5–10 ms at 5K2K) — 7.7 ms + scene still fits 16.7 ms.
* **One process per resolution.** MonoGame's DesktopGL backbuffer path is not reliable across an
  in-process `ApplyChanges` on this driver: the *first* full-res-only pass after the resize renders
  nothing (GPU timer 0, black capture) while the FBO (half/quarter) passes work. That is a harness
  artefact, so `run.sh` runs one process per resolution. Not observed in the game (whose resize
  path re-creates its targets); flagged here so nobody is surprised by the harness.
* **Machine shared.** Other suites were running on this workstation during the session; the GPU's
  effective clock varied by up to ~30 % between sittings (the same final build measured 5.7 ms and
  7.7 ms for the 5K2K camera-inside case). §0 uses min-of-two interleaved runs per cell; the ratios
  are stable, the absolute milliseconds are ±10 %.
* **Real-window auto proof stayed docked** in the first attempts (the auto session never undocked
  before `--exit-after`), so the real-client visual proof is from a run with `--auto-launch`
  (`real_window_blast.png`); I did not try to reproduce the live 200v200 plume close-up — that is
  the rig's job (Claude/Domingo).
* Not done: an actual slow-GPU test of the adaptive controller (unit-tested only); an eighth-res
  level (quarter + 48 steps is enough on this GPU, and eighth-res of 5K2K would visibly soften the
  plume).

---

## 9. P834b — orientation: the clamp must hold for ANY camera heading

**Follow-up to §2.4.** P834's near/scene clamp measured the ray's view-axis cosine as
`-rd.z`. `rd = normalize(i.WorldPos - EyePos)` is a **world** direction (the renderer feeds
camera-relative *world* positions with `EyePos = 0`, so the camera rotation lives only in the
view matrix), therefore `-rd.z` is that cosine only for a camera looking down world −Z — the
single heading every P834 measurement used (`CreateLookAt(0, (0,0,-1), up)`). Claude's review
was right, and it is worse than a wrong clamp: at most headings every volume is clipped away.

Four things came out of this task, in the order they had to be solved:

1. **The harness was measuring the wrong bytecode** and reported a black capture for that
   reason — that, not physics, is why "both shaders time the same at every yaw".
2. **The real orientation bug in P834's shader** (`-rd.z`) — reproduced, measured, fixed.
3. **The WIP's view-matrix-in-the-pixel-shader is a trap**: a matrix referenced from
   `Volumetric.fx`'s pixel shader makes MonoGame upload the PS constants to the wrong
   registers (silently: `Near` becomes a view-matrix element, `Brightness` becomes 0 → a black
   plume at every heading; referencing the whole matrix instead throws inside
   `ConstantBuffer.SetData`). The fix therefore passes the camera axis as a `float3`.
4. The CPU mirror (`VolumetricGeometry.ClampMarchRange`) never modelled the cosine at all, so
   no test could have caught it; it gained `ClampVisibleRange`/`ViewAxisInvCos`/`ViewForward`
   plus 55 tests driven through real view matrices at 17 orientations.

### 9.1 Making the harness trustworthy (it was not)

`tools/p834-vol-perf` resolved its shader with `ResolveContentRoot()`
(`tools/p834-vol-perf/Program.cs:216-228`), which prefers the **build output** copy
`src/Allegiance.ClientCore.MonoGame/bin/Debug/net10.0/Content/Shaders/Volumetric.ogl.mgfxo`.
Copying a `.mgfxo` into `Content/Shaders/` without re-running the MonoGame build leaves that
copy stale, so the A/B run silently measured the **same** bytecode twice. That is exactly what
the review's table shows — re-measured with the bytecode pinned per run:

| 2560×1440, `inside:4:60`, GPU mean ms | yaw 0 | 45 | 90 | 135 | 180 | 270 |
|---|---|---|---|---|---|---|
| review's "P834 shader" row | 2.24 | 0.41 | 0.40 | 0.97 | 2.11 | 1.93 |
| review's "WIP shader" row | 2.06 | 0.40 | 0.38 | 0.98 | 2.17 | 2.00 |
| **WIP bytecode (`2793d966`), measured now** | 1.93 | 0.36 | 0.50 | 0.99 | 2.18 | 1.88 |
| **P834 bytecode (`399a2712`), measured now** | 2.04 | 0.58 | 0.67 | **0.07** | **0.10** | 0.97 |

Both review rows track the **WIP** bytecode (same shape, inside this machine's run-to-run spread — §8: ±10 %,
and up to ~25 % between sittings), and neither matches the P834 bytecode where it matters — at
yaw 135°/180° P834 costs 0.07–0.10 ms and the review reports 0.97–2.11 ms. The review's capture
"black, mean luma 6" is also exactly what the WIP bytecode produces (measured luma 5.0–7.6 in
every cell). With the bytecode pinned, the P834 shader *is* cheap at 135°/180° — because its
plume is **missing** there (`lit = 0 %`, §9.2). The theory was right; the table hid it.

Harness changes (all inert unless used):

| change | why |
|---|---|
| `--shader PATH` (`Program.cs:114`; resolution `:158-163`; header note `:32-35`) | pin the `.mgfxo` under test |
| prints `sha256` of the loaded `.mgfxo` **and** of the `.fx` source (`:321-326`) | a run can never again measure "some" shader |
| `--pitch DEG` (`:113`, `Cameras()`/`Forward()`/`Up()`) | pitch about the camera's right axis, blast stays dead ahead |
| `--forced` accepts every `VolumeLod.Level` (`:118-127`) | `minimal` (P834's own screen-filling rung) could **not** be pinned before; the parser only knew P579's four |
| `[p834-frame]` CPU framing line (`DescribeFraming`, `:574-588`) | prints the blast's **view-space position**, NDC, screen pixel, projected diameter/fraction, radius — the "is this really dead ahead?" check |
| capture readout: mean luma / lit % / hot % / bbox (`LumaStats`, `:541-560`) | a missing plume is a number, not something you have to eyeball |
| a clear "missing params" message instead of "not usable" (`MissingFromShader`) | a wrong shader file is now named |
| `tools/p834-vol-perf/orientation-sweep.sh <label> <outdir> [args]` | re-runs exactly the nine cells of §9.2 for any `--shader`, printing GPU ms + luma + coverage per cell |

Checked against P834's own captured frames (`docs/reports/P834_assets/sbs_camera_inside.png`, the
same `inside:4:60` cell): that asset's halves measure **190.5** (BEFORE, P579) and **199.7**
(AFTER, P834) mean luma; re-capturing the same two shaders today gives **196.2** and **206.7** —
same relationship (+9–10 for P834), same order of magnitude, with the plume covering the frame in
both. Per-pixel identity is not expected: the shader's fBm churns on the `Time` uniform, so every
run has its own mottle. (Where two runs *do* hit the same churn phase the frames agree to within
7/255 — §9.2, yaw 0 fix vs P834.) So the harness reproduces P834's frames; what it did **not** do
is run the shader you think you swapped in (§9.1).

Framing proof that `--yaw`/`--pitch` rotate the camera *and* the blast together — identical in
all 9 cells (`[p834-frame]`): `blast view=(x 0.0, y 0.0, z -60.0) ndc=(0.000, 0.000)
screen=(1280,720) diam=16984px (1179% of h) radius=165u`. So the harness camera is correct; the
orientation dependence had to be in the shader/renderer.

### 9.2 What actually depends on orientation

2560×1440, `inside:4:60` (camera inside a bomber plume), 40 measured frames per cell, same
process type, `--shader` pinned (`tools/p834-vol-perf/orientation-sweep.sh <label> <outdir>
[--shader …]` re-runs the nine cells). **GPU mean ms / captured mean luma / lit coverage**:

| cell | master P579 (`8f7dda9d`, no clamp, `--forced low`) | P834 (`-rd.z`) | WIP (`mul(rd,WV)` in PS) | **fix** |
|---|---|---|---|---|
| yaw 0 | 2.63 / 193.6 / 100 % | 2.04 / 205.2 / 100 % | 1.93 / 6.0 / **0 %** | 1.97 / 205.2 / 100 % |
| yaw 45 | 0.76 / 241.9 / 100 % | 0.58 / 246.9 / 100 % | 0.36 / 5.6 / **0 %** | 0.49 / 247.0 / 100 % |
| yaw 90 | 1.35 / 205.9 / 100 % | 0.67 / **113.0 / 49.8 %** | 0.50 / 5.7 / **0 %** | 1.02 / 223.1 / 100 % |
| yaw 135 | 2.78 / 130.9 / 100 % | **0.07 / 3.9 / 0 %** | 0.99 / 5.0 / **0 %** | 2.14 / 144.6 / 100 % |
| yaw 180 | 2.73 / 133.8 / 100 % | **0.10 / 3.9 / 0 %** | 2.18 / 6.2 / **0 %** | 2.05 / 152.1 / 100 % |
| yaw 270 | 2.47 / 237.7 / 100 % | 0.97 / **122.8 / 49.8 %** | 1.88 / 7.5 / **0 %** | 1.95 / 243.3 / 100 % |
| yaw 0 pitch −40 | 2.67 / 238.1 / 100 % | 1.97 / 243.8 / 100 % | 2.02 / 7.6 / **0 %** | 1.98 / 243.9 / 100 % |
| yaw 0 pitch +40 | 2.47 / 206.4 / 100 % | 2.07 / 222.5 / 100 % | 2.38 / 6.1 / **0 %** | 1.94 / 222.5 / 100 % |
| yaw 135 pitch 30 | 0.98 / 140.5 / 100 % | **0.15 / 6.2 / 2.4 %** | 0.37 / 5.0 / **0 %** | 0.75 / 151.0 / 100 % |

(The master column is a **control**, not master's whole pipeline: master's `VolumeLod` has no
`Minimal` rung (`Low/64` is its biggest-coverage level, `8f7dda9d`
`VolumetricQuality.cs:24-41`), so it is run with `--forced low` through today's renderer; the
handicap is the 64-vs-48 step count, which is exactly the constant 0.75 factor quantified
below. `lit` =
pixels above the (3,4,6) clear colour.)

Reading it:

* **P834**: looking toward world +Z (yaw 135/180, and 135/30) the plume is **gone** — `lit 0 %`,
  0.07–0.15 ms — and at yaw 90/270 exactly **half** the frame is gone. That is the reported bug
  reproduced, and the "cost collapse" the brief asked to root-cause.
* **WIP**: black at **every** heading at 0.36–2.38 ms — the cost of the full march, so it looked
  "normal" in a timing table while rendering nothing (§9.3).
* **fix**: plume present at **every** cell, and the residual spread is *not* the clamp. Two
  proofs: (a) the fix costs a **constant 0.75 ± 0.04** of the unclamped master at all 9 cells
  (min 0.64, max 0.79) — i.e. the same marched work, just 48 steps instead of 64 — while P834's
  ratio collapses to 0.50 / 0.025 / 0.037 / 0.153 at yaw 90/135/180/135-30; (b) across the 9
  cells the captured luma correlates with master at **r = 0.996** (means 203.7 vs 192.1). The
  remaining luma/cost spread (e.g. 205 at yaw 0 vs 152 at yaw 180) is the *same* spread master
  shows (193.6 vs 133.8): the fBm is sampled in **world** space and the self-shadow bias is
  `dot(rd, LightDir)` in world space, so turning the camera looks at a different slice of the
  plume. That is P579 behaviour and is deliberately unchanged.
* At yaw 0 the fix and P834 produce the **same picture**: mean per-channel difference 0.01/0.08/
  0.04 levels (max 4–7) — the fix changes nothing where P834 was already right.
* Other proximities are equally affected and equally fixed (yaw 180, 2560×1440): fighter @1500 u
  0.00 ms / lit 0 % → **0.67 ms / lit 0.13 %** (a 78 px blob at the frame centre), fighter @150 u
  0.10 / 0 % → **0.82 / 14.8 %**, bomber @450 u 0.09 / 0 % → **1.31 / 41.9 %**.
* **P834's win survives at the new headings** (5120×2160, `inside:4:60`): P834 6.57 ms at yaw 0
  but 0.18 ms / lit 0 % at yaw 180; fix **6.93 ms at yaw 0 and 7.01 ms at yaw 180**, `lit 100 %`
  both — i.e. 7 ms (not master's 32.7 ms) at every heading.

CPU-side, nothing depends on orientation and nothing needed changing: the billboard basis is the
view matrix's columns, `WorldViewProj = view * projection`, and coverage/LOD/scale come from the
projection matrix and the viewport (`VolumetricRenderer.cs:256-295`) — the framing print in §9.1
shows identical view-space geometry and coverage at every heading.

### 9.3 Why the WIP was black everywhere: a matrix in the pixel shader

`ClampVisible` did `mul(float4(rd, 0.0), WorldView).z`. The HLSL is right (it is
`rd · column3(view)`, i.e. the view-space z — verified with a probe shader that outputs the three
candidate cosines: the `mul(rd, WorldView)` form gives exactly 1.0 on the view axis at yaw
0/45/90/135/180/270, `-rd.z` only at 0/180, and `mul(WorldView, rd)` never). What breaks is the
**constant upload**. Compiled bytecode, side by side:

| | P834 | WIP |
|---|---|---|
| PS uniform block | `uniform vec4 ps_uniforms_vec4[7]` | `uniform vec4 ps_uniforms_vec4[8]` |
| first PS uniform | `#define ps_c0 ps_uniforms_vec4[0]` | `#define ps_c2 ps_uniforms_vec4[0]` |

mgfxc/MojoShader packs `WorldView` into **one** vec4 (the PS only reads `.z`) and renumbers the
registers, but the effect metadata keeps the matrix's 4-register footprint, so MonoGame's
`ConstantBuffer` writes every PS parameter after it to the wrong slot. Measured with a probe that
keeps the WIP's exact uniform shape and outputs the parameters (`R` = did the fragment survive,
`G` = `Near`, `B` = `InvRange*1000`): at yaw 0 `Near` reads **0.03**, at yaw 90 (a different
screen pixel) **1.03** — it is `sin(yaw)`, i.e. the `M13` element of `WorldView`, not the near
plane — `InvRange` saturates its ×1000 probe (so ≥ 1e-3 instead of 1.27e-4, i.e. the "far" scene
distance becomes ≤ 1000 u instead of 7900), and `Brightness` (the last parameter) reads 0,
which is why the march ran at full cost and produced nothing. Referencing the whole matrix
instead is worse: `System.ArgumentException: Offset and length were out of bounds` in
`Microsoft.Xna.Framework.Graphics.ConstantBuffer.SetData` → `EffectPass.Apply`, every frame.

**Rule (now in the shader header, `Volumetric.fx:31-41`): never reference a matrix from the
Volumetric pixel shader.** The camera axis is a `float3` uniform instead.

### 9.4 The fix

* `Volumetric.fx` — new global `float3 ViewForward;` (`:66`) and
  `float cosView = dot(rd, ViewForward); float invZ = 1.0 / max(1e-4, cosView);` in
  `ClampVisible` (`:274-286`). Everything else (near-plane start, scene stop, `clip()`) is
  unchanged, so the §2.4 result stands: with a 40 u occluder disc the camera-inside case still
  drops to **0.69–1.00 ms** at yaw 0/90/180 (from 1.97–2.14 ms) — and now at *every* heading.
* `VolumetricRenderer.cs:362-366` — sets it once per frame from the view matrix:
  `VolumetricGeometry.ViewForward(view.M13, view.M23, view.M33)` (= `-(M13,M23,M33)`, the same
  derivation the P579 proof hook already used, `Game1.VolumetricProof.cs:121`). Optional on
  purpose (`:174-182`): a pre-P834b `.mgfxo` has no such parameter and logs a warning instead of
  disabling the tier, which is what let the old shaders be measured in §9.2.
* `VolumetricGeometry.cs` — `ViewForward(m13,m23,m33)` (`:113`), `ViewAxisInvCos(rayDir,
  viewForward)` (`:79`, mirrors the shader's `1/max(1e-4, cos)` guard) and `ClampVisibleRange(...)`
  (`:94`), the full mirror of `ClampVisible`: it converts the view-axis near/scene depths into ray
  distances with that cosine and then clamps. `ClampMarchRange` (`:66`) stays as the
  ray-distance-only helper its tests use.
* Recompiled `Content/Shaders/Volumetric.ogl.mgfxo` with `mgfxc /Profile:OpenGL` (Wine) and wrote
  the new source hash: `Volumetric.fx.sha256` =
  `ff7cd6ab0af6ac6e796160ee4a45e771d94af2f50908ae031cb375b3e2eb0e00`, bytecode sha256
  `e36330c9e40b61dd08929630fdf854ef2afa9a3840213e81eddd6c16d68b6aef`. The rebuilt PS is back to
  `ps_uniforms_vec4[8]` with the uniforms starting at `ps_c0` (7 P834 parameters + `ViewForward`).
* `Game1.VolumetricProof.cs:93-106` — the vol proof now logs the camera's **world heading** once a
  second, and `:148-155` logs it on the frame of every frame-strip shot. That is what makes a
  real-window orientation proof checkable ("this PNG was taken looking at world +Z").

### 9.5 Tests that would have caught it

New `tests/Allegiance.ClientCore.MonoGame.Tests/P834bVolumetricOrientationTests.cs` (55 tests).
They drive the CPU mirror through real `Matrix.CreateLookAt` view matrices at 17 orientations
(yaw 0…315 every 45°, six pitches ±75…±20, and combinations), so a heading-dependent clamp is a
failure, not a screenshot nobody looks at:

* `View_axis_cosine_is_one_for_a_ray_that_looks_where_the_camera_looks` — `dot(rd, ViewForward) = 1`
  at every orientation.
* `Clamp_is_identical_at_every_orientation_for_the_same_on_screen_pixel` /
  `Off_axis_rays_clamp_identically_at_every_orientation` — for the same on-screen ray the marched
  interval is identical at every heading (span + near-plane start + far-scene stop).
* `The_P834_formula_would_have_clipped_the_volume_looking_toward_world_plus_z` — the regression
  witness: `-rd.z` at yaw 180 gives `invZ = 10⁴`, `tNear ≫ sphere exit`, `Visible == false`,
  while the fixed mirror keeps the plume; also at 90°.
* `The_near_plane_and_the_scene_still_clamp_through_the_view_axis` — the hull/scene stop still
  works, expressed in ray distance (`Near/cos`, `40/cos` at 40° off-axis).
* `View_forward_is_the_camera_forward_axis_at_any_heading`, `View_axis_inverse_cosine_matches_the_shader_guard`.

### 9.6 Verification (light gate)

* `dotnet build AllegianceCore.slnx` — **0 warnings, 0 errors**.
* `tools/build-tools.sh` — all **88** tool projects built, every one 0 warnings / 0 errors.
* New tests 3× alone: **55/55, 55/55, 55/55**. Affected volumetric suite (P573/P579/P582b/P834/
  P834b) 3× alone: **152/152** each run.
* ONE `tools/run-tests.sh --tier fast`:

```
Discovery counts: Lobby 82 fast / Update 28 / MonoGame 3577 / ClientCore 1821 / Core 5410
fast: 10918 tests, 304.1s, PASS
  Allegiance.Update.Tests         28 passed
  Allegiance.Lobby.Tests          82 passed
  Allegiance.ClientCore.MonoGame.Tests  3574 passed, 3 not executed (documented skips)
  Allegiance.ClientCore.Tests     1819 passed (+2 quarantine)
  Allegiance.Core.Tests           5400 passed (+10 quarantine)
PASS: TestResults/tiers-20261027T030805.706291Z/summary.json
```

### 9.7 Real client check (windowed, local server)

`scripts/live-proof.sh --client-extra "--auto-launch --camera orbitcam --fx volumetric"` with
`ALLEGIANCE_VOL_PROOF=12:4@60` (a bomber plume 60 u dead ahead = camera inside it) and
`ALLEGIANCE_VOL_FRAMESTRIP=0.4,1.0,…,7.0`. The auto-proof orbit camera spins at 1 rad/s, so the
12 frame-strip shots sweep a full circle of world headings — with the heading logged per shot.
Real 2560×1440 backbuffer frames, `lit`/luma from the harness helper:

| frame (age) | camera forward (world) | mean luma | verdict |
|---|---|---|---|
| 0.37 s | ( 0.765, 0, −0.644) | 151.8 | plume |
| 0.97 s | ( 0.974, 0, −0.228) | 214.9 | plume |
| **1.57 s** | **( 0.994, 0, 0.108)** | **235.2** | **sideways +X: plume** |
| **2.17 s** | **( 0.165, 0, 0.986)** | **189.0** | **toward world +Z: plume** |
| 2.77 s | (−0.385, 0, −0.923) | 244.4 | plume |
| 3.37 s | ( 0.999, 0, −0.045) | 243.2 | plume (sideways +X) |
| 3.97 s | (−0.859, 0, 0.512) | 241.9 | plume |
| 4.57 s | ( 0.239, 0, −0.971) | 242.9 | plume |
| 5.17 s | ( 0.864, 0, −0.503) | 223.8 | plume |
| 5.77 s | ( 0.868, 0, −0.496) | 188.4 | plume |
| 6.37 s | (−0.158, 0, −0.987) | 130.2 | plume, cooling |
| 6.97 s | ( 0.395, 0, 0.919) | 32.4 | smoke tail (age 7 s) |

(The §8 note still holds: without `--auto-launch` the auto session parks in the station loadout
screen and nothing is injected — the first attempt of this task did exactly that and produced an
empty "proof" frame, which is why the heading log matters.)

### 9.8 Frames (`docs/reports/P834_assets/`, added here)

| file | what I see in it |
|---|---|
| `orient_p834_vs_fix.png` | 2×6 grid, 2560×1440, `inside:4:60`. TOP = P834: full plume at yaw 0/45, **half black at 90/270**, **completely black at 135/180**. BOTTOM = fix: the same plume at all six, with the same colour/size and the same world-noise slice (yaw 180 is the darker brown-orange, as master is). |
| `orient_p579_vs_fix.png` | master P579 (no clamp) vs fix, same six headings: visually the same plume in every column; the fix is a touch smoother/brighter at some headings (48-step `Minimal` + quarter-res vs 64 × `Low`), and the per-heading brightness pattern matches P579's. |
| `orient_wip_black.png` | the rejected intermediate at the six headings: black squares — the full-cost march with a zeroed `Brightness` (§9.3). |
| `real_window_worldpZ.png` | real client window, camera forward (0.165, 0, 0.986): the fireball fills the frame under an intact HUD (thrust/fuel arcs, "SIG: 60 %", lock rings, radar "Bug Nebula/Omega/Tellus", the sun and starfield visible through the wash). |
| `real_window_sideways.png` | real client window, camera forward (0.994, 0, 0.108): the same plume from sideways, saturated white-yellow with the HUD and starfield intact. |

Raw evidence (per-cell harness logs, the real-window client log with the heading lines, the
frame-time CSV, the fast-tier log and the probe shader + the extracted GLSL that shows the
`ps_uniforms_vec4[7]` → `[8]` / `ps_c0` → `ps_c2` change): `~/allegiance-evidence/P834b/`.

All five PNGs are **new media**: they need Domingo's review before the push (`publish-guard`
fails on new media unless `ALLOW_MEDIA=1`); none of them is Meshy-generated, and the working tree
adds no `/home/<user>` path, personal email or secret outside `docs/`.

### 9.9 Caveats / unfinished

* **The look varies with heading — deliberately.** The fBm is sampled in world space and the
  self-shadow bias is `dot(rd, LightDir)` in world space, so a plume looks different from a
  different heading; master does the same (§9.2, r = 0.996). What is now heading-independent is
  the *clamp*: no cell loses the plume and the marched work tracks the unclamped shader at a
  constant 0.75 ± 0.04.
* The `inside:4:60` cells have a large intrinsic cost spread (0.5–2.2 ms) because the transmittance
  early-out fires at different depths for different world-noise slices — the unclamped master
  shows the same 0.4–2.8 ms spread. It is not a clamp artefact; use `--shader` + the P579 control
  when reading the table.
* The harness **still prefers the build-output `.mgfxo`** by default (that is what lets it run
  against a freshly built client). It now prints the sha256 of what it loaded, so the trap is
  visible; `--shader` is the reliable form and is what every number above used.
* `ViewForward` is optional in the renderer (pre-P834b bytecode must still render). Consequence: if
  someone ships an old `.mgfxo` with a new `.fx`, the shader keeps the old, heading-broken clamp
  instead of failing. The P547 staleness test pins the `.fx` ↔ `.mgfxo` pair, so this only affects
  hand-made A/B runs.
* The orientation table is a **camera-inside** table (the case P834's brief used). Far/near/full
  blasts were spot-checked at yaw 180 only (§9.2) — they were equally broken and are equally
  fixed.
* Still not done (from §8, unchanged): a slow-GPU test of the adaptive budget controller, and an
  eighth-res level. No server/sim/Docker/manifest change in P834b; the only new client surface is
  the proof log line, no new env var.

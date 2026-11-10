# P989 — `visuals.space.simPalette`: the sim colour grade on the HDR pipeline (default OFF)

Lane: pi (worktree `allegiance-pi-p989`, base `6659cb91` = Claude's master at dispatch). Changes are left in the
working tree — **no commit, no push**, no rig/mini/Mac or public host contacted. `local.env` was present (artwork + native
source), so the artwork-backed runs and captures ran against real assets. Scratch work (base-commit build, capture
matrix, gate logs) lived in the git-ignored `.scratch/p989/` and is deleted at the end of the task; the full-resolution
960×540 capture matrix, the base-commit evidence, the gate logs and the proof log are outside the repo in
`~/allegiance-evidence/P989/`, and the review copies live in `docs/reports/P989_assets/` (1.9 MB).

**Origin:** P978 FreeSpace 2 Open study (`docs/reports/FS2OPEN_STUDY_P978.md` §5.3, candidate **G2**), approved by Domingo
2026-11-09 as a flagged batch. FS2 Open is **ideas only** (Volition non-commercial notice): §2 describes what SCP does in
my own words with `fs2open:code/...` citations, no SCP code or prose was copied, and **no SCP lighting-profile constant
was transplanted** (P978 §5.3: those numbers belong to a different content scale). P984 (merged,
`docs/reports/HDR_FOUNDATION_P984.md`) is the foundation this task drives.

---

## 1. What this slice delivers

With `visuals.space.simPalette` **ON** (and `visuals.space.hdrPipeline` really producing the RGBA16F scene):

* the world reads as one **filmic, restrained, slightly darker and slightly desaturated** look — the "sim vibe" P978
  traced to SCP's post chain — while **nothing structural changes**: no new fullscreen pass, no new render target, no
  new shader;
* the grade is expressed as **five values on the passes the frame already runs**: the curve the existing output
  transform (`Shaders/Tonemap.fx`, P984's one transform) applies, its exposure, its world saturation, and the bright-pass
  threshold + bright-pass scale of the existing bloom pass (`Shaders/Bloom.fx`, untouched);
* the **HUD is never graded**: Classic and Modern both draw after the transform, and the GPU proof plus the capture diff
  show the HUD pixels are unchanged (only the world behind translucent panels moves);
* it is a **no-op unless the HDR pipeline is live**: with the flag off, or the float target refused, or the transform
  shader missing, the resolved grade is exactly P984's picture and — new framework data, not per-flag plumbing — the
  Visuals row says `(inactive: needs HDR scene)`;
* the flag-off / pipeline-off path is **byte-identical to the base commit**, proven on a real replay frame in both HUD
  styles (§4.1).

---

## 2. What SCP does (own words, cited)

SCP's "sim vibe" is a **post chain**, not a lighting model:

1. **One tonemap pass with a selectable curve.** `opengl_post_pass_tonemap` binds the scene colour texture, sets a generic
   uniform block — the algorithm id (`tonemapper`), `exposure`, and the piecewise-power curve intermediates — and draws a
   fullscreen quad into a scene-LDR texture (`fs2open:code/graphics/opengl/gropenglpostprocessing.cpp:69-101`). The shader
   multiplies by `exposure` and switches on the algorithm id
   (`fs2open:code/def_files/data/effects/tonemapping-f.sdr:208-235`), writing `linear_to_srgb(...)` when the target is
   not linear (`:237-241`).
2. **The curves are enumerated, and per-profile.** `TonemapperAlgorithm` lists
   `Linear/Uncharted/Aces/Aces_Approx/Cineon/Reinhard_Jodie/Reinhard_Extended/PPC/PPC_RGB` (plus an HDR10-only
   `HdrScene`) (`fs2open:code/lighting/lighting_profiles.h:17-33`); the ACES forms are the usual public filmic fits
   (`fs2open:code/def_files/data/effects/tonemapping-f.sdr:81-107`). A lighting profile supplies `$Exposure:` and
   per-light brightness/radius adjustments (`fs2open:code/lighting/lighting_profiles.cpp:414`, list at `:395-421`), so the
   grade is content-tuned data, not a compile-time choice.
3. **Bloom runs BEFORE the tonemap, at reduced resolution and in mips.** The bloom pass bright-passes at half resolution
   (`fs2open:code/graphics/opengl/gropenglpostprocessing.cpp:120-130`), blurs horizontal+vertical across mip levels
   (`:150-185`), and composites additively onto the scene colour with a `bloom_intensity` uniform (`:188-206`). The
   call order is explicit: bloom, then tonemap, then AA/lightshafts (`:518-521`). This is why SCP's bloom is graded by
   its tone curve instead of being drawn on top of the finished image.
4. **The HUD is not graded.** The tonemap writes a scene texture that the 2-D HUD/interface is later drawn over — the
   same console-image ordering native Allegiance uses (`WinTrek/WinTrek.cpp:2320-2327` sets the console MDL from the
   civilization's `GetHUDName()`; the 3-D view and the HUD are separate images).

What is **not** transferable: the profile numbers. SCP's default profile (Uncharted, exposure 4, overall-light 3.5) is
tuned for SCP's content and light units; P978 §5.3 says so explicitly and this task does not copy them. Our values were
tuned on our own captures (§3.3).

## 3. What we do

### 3.1 One row in the flag table

* `Rendering/VisualFlags.cs:101` `SimPaletteKey = "space.simPalette"`; row at `:148-156` — toggle, **default OFF**, hotkey
  `P` (R M S T G K H A U D were taken; the uniqueness test and `P989`'s own test stay green), and a new
  `VisualFlag.RequiresKey` field (`:55`) naming `space.hdrPipeline`. `VisualOptions.SimPalette` (`:203`) reads it; parse,
  persist, `--visuals` and the startup log are the P979 framework unchanged.
* The dependency is **table data**, so the options page and the use site cannot disagree: `OptionsMenuModel` appends
  `" (inactive: needs HDR scene)"` to the row when the device has decided the float path is unavailable
  (`OptionsMenuModel.cs:272-286`), driven by the new `OptionsState.HdrSceneActive` (`:144`, `null` = not known yet, i.e.
  the intro menu before a flight session). `Game1.BenchmarkSharing.cs:247` feeds it from `HdrScene`.

### 3.2 The grade drives the EXISTING transform and the EXISTING bloom

`Rendering/SimPalette.cs` is the whole model:

```csharp
public readonly record struct HdrGrade(HdrCurve Curve, float Exposure, float Saturation,
                                       float BloomThreshold, float BloomContribution);
public static HdrGrade Grade(bool requested, bool hdrActive, bool acesComparison)
```

* `HdrGrade.Native(curve)` is **P984's picture** (neutral/ACES curve, exposure 1, saturation 1, P547 bloom threshold
  0.80, bloom scale 1). Flag off, pipeline off, or a refused float target all resolve to it — the dependency rule is a
  value, not an `if` scattered through the renderer.
* `SimPalette.Grade(true, true, …)` returns the sim preset (below). `SimPalette.Applies` is the one predicate.
* `HdrOutput.Output(stored, curve, exposure, saturation)` and `HdrOutput.Saturate(linear, s)` are the C# mirrors of the
  shader, so the tests and the GPU proof compare the real pass against the same equations.
* `Game1.cs:428` `_grade` + `SyncHdrGrade()` resolve it once per rendered frame (flight **and** showcase) and push the
  two bloom knobs onto the existing `BloomPostProcessor`; `Game1.Rendering.cs:1881` and `Game1.Inspector.cs:1003` hand
  `_grade` to `_post.Output.Render(...)`.
* `Shaders/Tonemap.fx` gained one uniform, `Saturation`, applied in **linear light** by the filmic (`Aces`) pass only;
  `PS_Neutral` is untouched, so the neutral comparison curve is still bit-exact. `HdrOutputTransform.RequiredParameters`
  grew `Saturation` (its P984 overload is kept for callers that only pick a curve).
* `Shaders/Bloom.fx` is **unchanged** (byte-identical to the base commit): the palette's "contribution 0.08" is the
  bright-pass scale the pass already has (`BloomPostProcessor.BloomScale`), so scaling the extracted glow needs no new
  code in the shader and the default path's compiled bytecode stays exactly P547's.
* No new pass, no new target: `HdrOutput.Plan` is unchanged, and the plan's last two steps are still
  `OutputTransform > Hud`.

### 3.3 Final constants, and why

| Knob | Value | Why |
|---|---|---|
| Curve | ACES approximation (Narkowicz fit, the one `Tonemap.fx` already had) | P978's starting preset: filmic highlight roll-off instead of clipped emissives. Public fit, not SCP code. |
| Exposure | **0.90** | P978 §5.3 starting value; keeps midtones below the wash. Applied in **linear light**, once. |
| World saturation | **0.75 linear** (P978 said 0.85) | **Tuned down after the captures.** 0.85 is a *linear-light* figure; measured on the rendered frame it **raised** display chroma (mean max−min over the frame: native sky 25.8 → 25.9…31.6 on HD during the 0.85 run) because the filmic curve pulls the washed nebula down out of its shoulder while the exposure drops. P978 wants a slightly *desaturated* world, so the value came down until the rendered frame sits at chroma parity with the ungraded picture. With 0.75: HD sky mean chroma 29.31 → 28.68 and mean luma 40.73 → 35.89; native 25.78 → 23.75 / 28.61 → 25.53; black 2.98 → 2.55 / 5.50 → 5.02 (`P989_assets/frame_tone.json`). |
| Tint / lift | none (`SimPalette.Tint = one`) | P978 asked for no tint/lift; a white stays white and black stays black. |
| Bloom threshold | **1.2 linear** = `HdrOutput.ToStored(1.2)` ≈ **1.083** in the scene target | P978's value is linear; the float target stores *display-encoded* RGB with headroom, so the bright pass must read the unclamped sRGB encode of 1.2 (`HdrOutput.Encode` clamps at 1 — that is the display write, `ToStored` does not clamp). Effect: highlights below linear 1.2 stop blooming (GPU proof §5.4). |
| Bloom contribution | **0.08** | P978's value, expressed as the existing bright-pass scale. The explosion capture shows the difference plainly (§6). |

**A bug this slice fixed (P984's):** `PS_Aces` multiplied the **stored display-encoded** value by `Exposure`
*before* the sRGB decode, i.e. exposure lived in the wrong domain. With exposure at unity (P984) the two agree, which is
why it never showed; the C# mirror `HdrOutput.Output` always applied exposure *after* the decode. The shader now matches
the mirror (`Tonemap.fx:104-116`), and the GPU proof pins the agreement at 0.25/0.5/0.9/1.0 exposure (§5.1). Nothing
changed for exposure 1, so P984's neutral and ACES pictures are untouched.

---

## 4. Pixel identity and what actually changes

### 4.1 Flag off / pipeline off == the base commit (decoded RGBA hash)

Two builds: base `6659cb91` and this tree, same settings (HD sky, HD textures + HD PBR, bloom on, 960×540), the **same
replay frame** (`data/benchmarks/battle.alr`, frame 2400 — captured by replay-frame number, see §4.3):

```
flag off (space.hdrPipeline=off)
classic : base sha256(decoded RGBA) f1888ade8a7c3d042d9f74f896fc9bb347f64b7e0096ba1e7565c193a2efcdaf
          this  sha256(decoded RGBA) f1888ade8a7c3d042d9f74f896fc9bb347f64b7e0096ba1e7565c193a2efcdaf  -> IDENTICAL
modern  : base sha256(decoded RGBA) c8992bf521e1d6a88291a86ba7b9e9c20e181954d9327292225949db4c24e2e9
          this  sha256(decoded RGBA) c8992bf521e1d6a88291a86ba7b9e9c20e181954d9327292225949db4c24e2e9  -> IDENTICAL
pipeline off + simPalette ON (the dependency, at the pixel level):
classic / modern  -> identical to the base hashes above
```

The GPU proof repeats the same claim at the pass level: the default grade through the real `Tonemap.ogl.mgfxo` is
**byte-equal** to P984's neutral output for eight stored samples including an over-1.0 highlight
(`P989_PALETTE_FLAGOFF_PASS`). The inspector demo (11 shots × 2 HUD styles) is also identical between the two builds for
every shot without time-advanced particles (`P989_assets/flag_off_identity.json`; the VFX shots differ only because the
particle age at the shot depends on frame pacing, which is a fixture property, not a rendering one).

### 4.2 Off vs on — where the pixels move (`P989_assets/pixel_deltas.json`)

Same replay frame, 960×540, Classic (Modern is within 0.3 %/2 levels of the same numbers):

| pair | changed pixels | max Δ | mean abs Δ |
|---|---|---|---|
| off → HDR neutral, black sky | 0.33 % | 4 | 0.003 |
| off → HDR neutral, native sky | 1.54 % | 37 | 0.018 |
| off → HDR neutral, HD sky | 7.12 % | 41 | 0.072 |
| off → **simPalette**, black sky | 7.25 % | 38 | 0.734 |
| off → **simPalette**, native sky | 53.77 % | 60 | 5.417 |
| off → **simPalette**, HD sky | 72.49 % | 61 | 6.794 |
| HDR neutral → **simPalette**, HD sky | 72.49 % | 68 | 6.789 |
| **simPalette** → **simPalette + P982 sim flags**, HD sky | 0.01 % | 188 | 0.009 |

Read plainly: the HDR foundation alone barely moves the picture (P984's "same picture, HDR plumbing"); the sim palette
re-grades **the whole world** (sky, hulls, asteroids, particles — the maximum delta stays ≈ 60/255, i.e. nothing slams to
black or white), and P982's `simStars`/`simGlare`/`subtleDust` **compose with it as one look**: they add a few dozen star
and glare pixels (0.01 % of the frame, max 188 where a new sprite appears) and change no graded pixel — the palette is not
fighting them.

### 4.3 Determinism of the evidence

A wall-clock capture lands on a *different* replay frame per run (the frame cost varies with the pipeline under test), so
A/B evidence could not be compared pixel for pixel. `Game1.GpuBudget.cs:317-338` now honours
`ALLEGIANCE_BENCHMARK_FRAME=<n>` + `ALLEGIANCE_BENCHMARK_FRAME_PNG=<path>`: it writes one valid PNG (`SaveAsPng`) at or
just after replay frame `n`, suppresses the benchmark's own telemetry overlay (`DrawGpuBudgetOverlay`) while a capture is
armed, and exits. Same class of hook as the existing `ALLEGIANCE_BENCHMARK_SCREENSHOT`, inert without the env vars; two
runs of the same configuration produce **byte-identical** PNGs (verified). For the base-commit comparison the same hook
was pasted into the base *copy* in `.scratch/p989/base` (inert, rendering untouched) so both builds can shoot the same
frame.

---

## 5. Tests

### 5.1 `tests/Allegiance.ClientCore.MonoGame.Tests/SimPaletteTests.cs` (new, 18 cases: 14 facts + 2 theories)

| Test | Proves |
|---|---|
| `Flag_is_off_dotted_in_the_space_namespace_and_requires_the_hdr_pipeline` | default OFF, dotted key, `RequiresKey = space.hdrPipeline`, hotkey `P`, no collision |
| `Launch_override_accepts_the_dotted_key` (4) | `--visuals space.simPalette=on`, case-insensitive, with/without the pipeline |
| `Flag_persists_in_the_profile_visuals_group_and_the_override_still_wins` | profile round trip, override precedence, no aliasing, legacy profile → OFF |
| `Grade_without_the_pipeline_is_exactly_p984s_picture` | flag off / pipeline off / refused float target ⇒ `HdrGrade.Native` (the dependency rule) |
| `Grade_with_the_pipeline_is_the_p978_starting_preset` | filmic curve, 0.90 exposure, 0.75 saturation, 0.08 contribution, no tint; log string |
| `Bloom_threshold_is_stated_in_linear_light_and_read_in_the_targets_stored_domain` | 1.2 linear ↔ ≈1.083 stored; `Encode` clamps, `ToStored` does not; round trip |
| `Saturation_preserves_luma_and_pulls_chroma_toward_grey` | Rec.709 linear saturation; unity exact; grey untouched |
| `Output_mirror_applies_exposure_and_saturation_once_in_linear_light` | mirror == shader equation; a second application is detectable; neutral untouched |
| `The_grade_never_reaches_the_hud_and_adds_no_pass` | plan order (`OutputTransform < Hud`), one transform, one scene texture, no HUD input |
| `Tonemap_shader_applies_the_world_saturation_only_in_the_filmic_pass` | `PS_Neutral` untouched; `PS_Aces` linear exposure + saturation; every C# uniform declared |
| `Bloom_shader_takes_the_sim_contribution_and_the_default_is_the_p547_look` | Bloom.fx unchanged, contribution = existing `BloomScale`, defaults are P547 |
| `Visuals_row_explains_the_dependency_in_both_hud_styles` (Classic + Modern) | the row says `(inactive: needs HDR scene)`, edits the right key, hotkey resolves |
| `The_4k_fold_exists_but_the_shipping_frame_still_runs_the_separate_transform` | the measured 4K fold stays out of the frame path (§7) |
| `Gpu_proof_applies_the_sim_grade_bloom_and_keeps_the_hud_ungraded` (Tier Live, Requires Gpu) | runs `tools/p989-palette-proof` and asserts its PASS markers |

Extended: `P979VisualsOptionsTests` (row count 11 → 12), `VisualFlagsTests` (`Describe()` string), `P547ShaderTests`
(shader/parameter pairing still exact), `HdrFoundationTests` (unchanged, still green).

### 5.2 `tools/p989-palette-proof` (new, P921 proof pattern: offscreen SDL, separate process, real `.mgfxo`)

```
[p989-proof] greys sim=0,55,146,203,221,229,247
[p989-proof] colour sim=(0.847,0.412,0.224) aces=(0.867,0.365,0.090) luma 0.2500/0.2326 chroma 0.624/0.776 (luma-kept=True calmer=True)
[p989-proof] flag-off samples=0,64,128,191,225,240,255,118
[p989-proof] hud-after-graded-transform pixel=(128,64,0) expected=(128,64,0) ungraded=True
[p989-proof] bloom nearDim/nearBright: p547-threshold=0.0025/0.0301 sim-threshold=0.0000/0.0025 sim-full=0.0000/0.0002
[p989-proof] fsr fold: changed=262/9216 maxDelta=1/255 equivalent=no
P989_PALETTE_TRANSFORM_PASS  the sim grade matches the CPU mirror; saturation preserves luma
P989_PALETTE_FLAGOFF_PASS    flag off / pipeline off is byte-identical to the P984 neutral output
P989_PALETTE_HUD_PASS        the HUD drawn after the graded transform is ungraded
P989_PALETTE_BLOOM_PASS      existing bloom at 1.2 linear / 0.08 leaves below-threshold highlights alone
P989_PALETTE_FOLD_MEASURED   folded path differs by one 8-bit step - NOT adopted (separate transform kept)
P989_PALETTE_CHECK_PASS
```

It proves, with pixel readback, that (1) the real transform equals the mirror, (2) the saturation is luma-preserving in
linear light (exactly, in the unit test) and lowers chroma in the rendered frame — the rendered luma moves by ≤ 0.04 in
linear terms on a saturated sample because the saturation runs *before* the per-channel filmic fit, (3) the default grade is P984's bytes, (4) the HUD is ungraded, (5) the *existing* bloom at 1.2 linear
ignores a linear-1.0 highlight that 0.8 blooms, and at 0.08 adds ≈1/12 of the glow, and (6) the 4K fold's delta (§7).

---

## 6. Captures (`docs/reports/P989_assets/`, 1.9 MB)

Production client, real replay (`data/benchmarks/battle.alr`), **the same replay frame 2400** for every tile, 960×540
source, HD textures + HD PBR, bloom on, isolated preferences (`ALLEGIANCE_HUD_SETTINGS`/`ALLEGIANCE_PLAYER_SETTINGS`),
columns **off / HDR neutral / simPalette / simPalette + P982 sim flags**, rows **Classic / Modern**:

| Asset | What is visible (opened and checked) |
|---|---|
| `sky_hd.png` | HD nebula: the ungraded and neutral columns are the warm orange wash; simPalette drops it ~12 % in luma and stops the nebula reading as "pale"; the asteroid and the target ship keep their shading, the gauges, chat text and range labels are unchanged. `simPalette + P982` adds a handful of neutral stars at the top; nothing else moves. |
| `sky_native.png` | native dome/nebula: same story with a stronger effect (53 % of pixels differ from off, max 60) — the nebula becomes deeper and less orange-tinted, the blue-white target ring and the yellow HUD highlights stay exactly as they were. |
| `sky_black.png` | black sky + stars: the difference is confined to ships, asteroids, engine glows and particles (7 % of pixels, max 38); stars, radar and every HUD element read identically. |
| `scene_explosion_classic.png` | inspector demo step *damage_engine_explosion* (real Enhanced explosion + engine trail + 25 % damage smoke): off and neutral show a wide, bright, white-cored fireball with a large additive halo; **simPalette restrains it** — the halo shrinks and dims, the core keeps its shape and the ship silhouette stays readable. `+P982` is the same frame plus the extra sun/stars. |
| `scene_station_classic.png` | station + HD sky at replay frame 9840: the station's rust/orange panels and blue-lit faces keep their **faction/art identity** — the grade darkens and calms them, it does not re-tint them; the planet/nebula backdrop gets deeper and the HUD is untouched. (This replay moment has the station with the nebula/planet behind it; the sector's sun glare is not in frame at that frame, so the "station + sun" capture is a station + lit sky.) |
| `diff_off_vs_sim_hd_classic_x4.png` | off↔simPalette difference amplified 4×: the whole **world** (nebula, asteroids, hulls, particles) moves; the HUD text, labels and gauge shapes appear as dark, unchanged shapes, and only the pixels behind translucent HUD panels move with the world. |
| `pixel_deltas.json`, `frame_tone.json`, `frame_cost.json`, `flag_off_identity.json` | every number quoted in §4 and §8. |

**Hull / faction identity (judged by eye on the 960×540 originals):** the graded hulls keep their faction hue — the
PBR-lit fighter in the HD sky, the near asteroid and the station panels all read as the same painted surfaces, just
darker and calmer; nothing turns silver, grey or hue-shifted, and the shield/impact whites still read as white. That is
the saturate-in-linear-light + filmic-curve combination doing what P978 describes rather than a global tint.

**HUD identity:** the transform is the last 3-D pass and the HUD draws after it, so HUD *pixels* cannot move — proven
structurally (§4.1 test), at the pass level (`P989_PALETTE_HUD_PASS`), and visible in the diff asset. Pixels *behind*
translucent HUD panels do move with the world, which is why a whole-frame hash of the HUD area would not be a meaningful
test.

---

## 7. The 4K fold (tried, measured, **not adopted**)

P984's report suggested folding the output transform into FSR's RCAS output pass so 4K would not need the extra
full-resolution RGBA16F target (66 MB at 3840×2160). This slice built that and measured it:

* `Shaders/FsrRcas.fx` gained two passes (`RcasNeutralGraded`, `RcasAcesGraded`) that run RCAS and then the *same*
  equations `Tonemap.fx` uses; `FsrPostProcessor.Render(..., HdrGrade? foldGrade)` can write the display-referred result
  straight to the final target. The proof renders a float scene with over-1.0 highlights and saturated colours through
  **both** paths with the real `FsrEasu.ogl.mgfxo`/`FsrRcas.ogl.mgfxo` and compares the decoded 8-bit frames:

```
[p989-proof] fsr fold: changed=262/9216 maxDelta=1/255 equivalent=no
```

2.84 % of pixels differ by **exactly one 8-bit step** — the RGBA16F round trip of the RCAS result that the fold removes
(the folded path is the *more* accurate one). The brief's rule is "keep it only if pixel-equivalent", so it is **not**
wired into the frame: `Game1.Rendering.cs` still runs `_fsr.Render(...)` into the float target and then the separate
transform, and a test pins that decision (`The_4k_fold_exists_but_the_shipping_frame_still_runs_the_separate_transform`).
Adopting it later is a one-line change in `Game1.Rendering.cs` plus deleting the separate `Output.Render`, if Domingo
accepts 1 LSB on ~2.8 % of pixels for 66 MB of VRAM and one full-resolution float write+read back at 4K.

## 8. Cost

`ALLEGIANCE_CLIENT_PERF=1` telemetry (`[p643]` summaries, async GL timer queries, no readback stall), replay fixture, HD
sky, bloom on, **Intel Arc Pro B50** (Mesa 26.0.8) on this host — a discrete GPU, so treat the absolute numbers as "this
box", not as a UM690 measurement:

| config | GPU ms | draws | frame wall p50 |
|---|---|---:|---:|
| 1080p, flag off | 0.47 | 337 | 1.71 ms |
| 1080p, HDR neutral | 0.49 | 335 | 1.77 ms |
| 1080p, **simPalette** | **0.44** | 335 | 1.74 ms |
| 4K (FSR1 Quality, 2560×1440 render), off | 1.40 | 336 | 1.76 ms |
| 4K, HDR neutral | 2.20 | 338 | 1.81 ms |
| 4K, **simPalette** | **2.21** | 338 | 1.78 ms |

**The palette costs nothing measurable over the HDR foundation**: it changes uniform values on passes the frame already
runs (same pass list, same targets, same draw count), so its delta sits inside the ±0.05 ms sampling noise. What it does
*not* do is reduce the HDR foundation's own cost (P984: ≈ +0.07 ms post at 1080p, ≈ +0.6–0.8 ms at 4K with FSR); the
4K fold above was the attempt at that, and it is parked on the pixel-equivalence rule.

**UM690 estimate:** the palette's incremental traffic is zero (no extra target, no extra fullscreen pass), so the
expected iGPU cost of turning it on is **≈ 0 ms** over `visuals.space.hdrPipeline` already being on. The foundation's own
cost stays P984's estimate (≈ 0.4–0.5 ms at 1080p, ≈ 2–3 ms at 4K+FSR on a 680M), which is why both flags remain opt-in
and the fold stays available for a later 4K decision.

---

## 9. Gate (light)

| Step | Result |
|---|---|
| `dotnet build AllegianceCore.slnx` (`--disable-build-servers -nodeReuse:false -p:UseSharedCompilation=false`) | **0 warnings / 0 errors** |
| `tools/build-tools.sh` | **all 129 tool projects built**, exit 0 (includes the new `tools/p989-palette-proof`), zero warnings/errors |
| `scripts/build-shaders.sh` | 9 shaders compiled; `Tonemap.fx` + `FsrRcas.fx` changed with their `.sha256`, **`Bloom.fx` byte-identical to base** (the other recompiles were reverted) |
| new/affected tests 3× alone (`SimPaletteTests`, `P979VisualsOptionsTests`, `VisualFlagsTests`, `HdrFoundationTests`, `P547ShaderTests`, `P982SimSpaceTests`, `P806OptionsMenuTests`) | **128 passed / 0 failed / 0 skipped in each of 3 runs** (0.30 s each), including **both** GPU proofs (`p984-hdr-proof` and the new `p989-palette-proof`) against the real shaders |
| one full fast-tier run (`tools/run-tests.sh --tier fast --no-build`) | **13,877 tests, 672.3 s, FAIL**: 13,874 passed / 1 failed / 3 not-executed (the suite's own skip markers) + 3 calibrated-slow offenders. The single failure is the long-ledgered OPEN `MissileLockLiveTests.MissileLock_BuildsPartialThenFull_AndPosts_Tips_OverTheWire` (`lock 1.000`, 38.9 s — the same clean-base signature recorded in `docs/TEST_FLAKES.md`), **0/3 alone**, and no missile/lock/tip source is in this diff. Per project: MonoGame 5,096 (+3 not executed), ClientCore 2,352/2,353, Core 6,207, Lobby 144, Update 55. Recorded in `docs/TEST_FLAKES.md` §P989 with the two slow offenders (`P814b` 11.5 s, `P777` 12.5 s) that pass 3/3 alone afterwards |
| showcase never-parks (`P700ShowcaseLiveTests.Local_showcase_never_parks_with_target_for_five_seconds`) 5× isolated, serial | **5 passed / 0 failed / 0 skipped** — 4 m 52 s, 3 m 34 s, 3 m 34 s, 4 m 33 s, 6 m 57 s, every run reaching the fixture's 24/30 target with no parking violation (matches the recorded base rate; the change is client-render-only) |
| glossary (`tools/check-glossary-links.py`), publish guard, working-tree hygiene | new `GLOSSARY.md` "Sim palette" entry with its paths; no secrets, personal identifiers or home paths in code |

Only one test failed in the sweep and it is the ledgered, still-open partial-tip receipt family; everything that failed or
ran slow in the sweep passes 3/3 (or 8/8) alone afterwards, and the exact rates are appended to `docs/TEST_FLAKES.md`
(§P989). `tools/build-tools.sh` hung once on the shared MSBuild node (`dotnet build` at 0.1 % CPU for 7 min); it was
killed by PID and re-run with `--disable-build-servers -nodeReuse:false -p:UseSharedCompilation=false`, which is how the
129/129 result above was produced. Raw gate logs, the base-commit build and the full-resolution capture matrix are outside the repo in
`~/allegiance-evidence/P989/` (`.scratch/p989/` is deleted at the end of the task; the compact numbers are in this section
and `P989_assets/`).

---

## 10. Out of scope (kept out on purpose)

Auto-exposure, HDR10 display output, environment maps, SCP's per-light brightness/radius adjustments, a bloom mip chain
(P547's single H/V Gaussian stays), P982's own three sim flags (untouched, they compose), and any change to the 200v200 or
server paths. The palette is client-only and visual-only: no wire message, server rule, fog/sensor rule or HUD layout
changed.

## 11. Native intent

* **Native's picture is display-referred, 8-bit and has no tone curve at all** — the back buffer format is chosen from
  `A2R10G10B10 / X8R8G8B8 / R5G6B5 / A8R8G8B8` (`engine/D3DDevice9.cpp:156-161`), and lighting is the bidirectional
  ambient + coloured key + constant blue back light evaluated on the vertex colours
  (`engine/device3d.cpp:756-795`, `Igc/clusterIGC.h:321-334` — the `Color(0, 0.5f, 1.0f)` alt light and the 0.5/0.25
  ambient levels). Nothing in native tone-maps or grades the world, so this whole look is a **visual departure, not a
  parity fix** — which is exactly why it sits behind a default-OFF `visuals.*` flag next to P984's pipeline, with the
  native 8-bit path kept and byte-pinned (§4.1).
* **Native never grades the HUD**: the console/HUD is a separate image built from the civilization's HUD model
  (`WinTrek/WinTrek.cpp:2320-2327`) and drawn over the 3-D view. The sim grade keeps that ordering exactly — the
  transform is the last 3-D pass and the HUD draws after it (§5, §6).
* **Native's sector look comes from the sector's light colour/ambient** (`WinTrek/WinTrek.cpp:320-329` sets the world
  clip and the camera FOV the scene is drawn with; the light comes from the client's cluster). The grade leaves the
  sector light *inputs* alone and only re-shapes the output, so a sector's identity survives — that is why the station and
  hull captures keep their faction/art colours (§6).

## 12. Modern approach

* **Grade as data, passes as plumbing.** The look is a five-field `HdrGrade` value resolved once per frame from the flags
  and the proven device state. P984's "insert the palette into a tested pass list" hook is exactly what happened: no
  render-graph change, no new target, no shader for the palette — the *existing* single transform and the *existing* bloom
  pass are retuned. That is also why the feature's incremental GPU cost is zero.
* **Filmic curve before the HUD, HUD outside the grade.** SCP's ordering (bloom → tonemap → interface,
  `fs2open:code/graphics/opengl/gropenglpostprocessing.cpp:518-521`) is the model; our port already had it from P984 and
  this slice only changes what the transform *does*, which keeps the HUD provably ungraded.
* **Dependencies as table data.** `VisualFlag.RequiresKey` + `OptionsState.HdrSceneActive` generalises "this enhancement
  needs that one" for the whole P979 framework, so `space.hdrAces` and `space.simPalette` both explain themselves and the
  no-op is asserted at the value level instead of being hoped for at the call site.
* **Deterministic evidence.** Replay-frame-number capture (`ALLEGIANCE_BENCHMARK_FRAME`) replaced wall-clock timing for
  A/B work, which is what made a base-commit *pixel-hash* comparison possible this time; the same hook is worth reusing
  for every future visual flag.
* **Next task hooks:** the exposure and saturation are single constants in `Rendering/SimPalette.cs` plus uniforms on the
  existing passes, so a future "sim palette strength" range row or a second preset is a table row + one value; the 4K fold
  is implemented and measured (`FsrRcas.fx` graded passes + `FsrPostProcessor.Render(..., foldGrade)`) and needs only a
  one-line switch plus Domingo's call on 1 LSB on ~2.8 % of pixels.

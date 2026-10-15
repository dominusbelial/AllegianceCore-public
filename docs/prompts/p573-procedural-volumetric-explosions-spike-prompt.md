# P573 — SPIKE: 100% procedural volumetric explosions (raymarched noise volume) for DesktopGL, plus an offline-baked flipbook path for Android/low-end; renders + numbers, nothing default

## Why this task exists

**Domingo (2026-10-06):** "can you research if we can do 100% procedural
volumetric explosions?" Claude's research said: feasible on DesktopGL with
custom shaders (P547 pipeline: Wine + `mgfxc`, OpenGL profile, MojoShader,
**ps_3_0/vs_3_0 limits**, runtime `.ogl.mgfxo` loading via `ShaderAssetCache`),
with caveats: **Texture3D is not available on OpenGL ES 2.0** (MonoGame's
Android/iOS baseline), raymarching is fill-rate heavy, and ps_3_0 has
instruction/loop limits. Three techniques were identified:
- **A. Raymarched density volume:** a pixel shader marches a ray through a
  bounding sphere/box (or an impostor quad) around the blast, sampling animated
  fBm noise shaped by a radial falloff; density → temperature colour ramp
  (white-hot core → orange → red → smoke); cheap self-shadow approximation;
  depth-aware (P547 linear depth) for soft intersection; optional bloom.
- **B. Noise-displaced sphere mesh** (stylised fireball): a sphere displaced by
  noise per frame (CPU or vertex shader), temperature ramp, erosion over life.
- **C. Procedural-baked:** run A offline (or at load) to render procedural
  explosions into flipbook atlases, then play them back like today's sprites
  (works everywhere, including Android).

Domingo approved the spike. Build on **P561** (smoke sizes tuned) and P546/P547.

## Scope (spike: branch `spike/p573-volumetric` in a dedicated worktree, like P543)

0. **Feasibility first:** compile a minimal raymarch shader for the OpenGL
   profile via `scripts/build-shaders.sh`; record the ps_3_0 instruction count
   reported by `mgfxc`, and how MojoShader handles the loop (static vs dynamic,
   unrolled). Decide the noise source: 2D tiled noise texture slices (GLES2-safe),
   ALU noise, or Texture3D (DesktopGL only). Report it.
1. **Technique A** behind a new setting `--fx volumetric` (DesktopGL): radius,
   duration and type driven by the native `ExplosionPreset`/`ExplosionSampler`
   (`AddExplosion`, `trekigc.cpp:95-177`); animated noise growth → billowing →
   dissipation into smoke; temperature ramp; **distance LOD** (step count by
   on-screen size) and a **cap on simultaneous volumes** (fallback to Enhanced
   sprites beyond it); P547 depth for soft intersection; correct state pinning
   (sampler/blend/depth/raster; P568 lesson).
2. **Technique C**: a tool (`tools/roster-capture vfx-bake` or similar) that
   renders A's volume offline into flipbook atlases (several seeds per preset)
   at the sizes the game uses, loadable by the existing Enhanced flipbook
   renderer, as the Android/low-end path. Report atlas size/VRAM.
3. **Technique B**: a full contender (see the section below).
4. Sparks/debris stay as CPU particles; the volume replaces the fireball and
   smoke ball only.

## ➕ Direction: support BOTH (Domingo: "we could support both right? desktop will have better resources always")

Design the recommendation as **tiers chosen by platform + a quality setting**,
not a single winner:
- **Volumetric** (A, real-time raymarch): default on **desktop**.
- **Enhanced/baked** (C, flipbooks **baked from the same A shader**, so the art
  direction matches): default on **Android/low-end**.
- **Native** (original art): always available (classic look, parity checks).

Prototype the selection: an `Effects quality: Native | Enhanced | Volumetric`
setting (+ CLI flag), a **platform default** (DesktopGL → Volumetric if the
shader loads and a quick GPU-time probe passes; Android → Enhanced/baked), and an
automatic **fallback** (shader load failure, or frame-time over budget → drop a
tier, logged once). Report the thresholds used and how C's atlases are packaged
per platform (desktop doesn't need them if A runs; Android ships C only).

## ➕ Native explosion rules the volumes must honour (visual only; gameplay untouched)

Native `WinTrek/trekigc.cpp:95-191` (`AddExplosion`): 7 types by what died
(`Igc/igc.h:37-45` `c_et*`): 1 mine (count 4, radius 25, wave 50, violet tint),
2 missile/projectile (count 6, radius **1**, deliberately ignores scale, no wave),
3 small ship/probe (12, 35, 70, orange), 4 big ship (16, 165, 250), 5 small
station (16, 100, 800), 6 asteroid (5, 150, no wave), 7 large station (24, 350,
1000); `scale = 2 × model radius` (`:180-191`) picks the sound (>10 medium, >100
large) and the radius where the type doesn't fix it; position, forward/right
(shock-wave plane) and **velocity drift**; stations pick large vs small by
`c_sabmFlag` (`:4327-4332`). The port mirrors this in
`ExplosionPreset`/`ExplosionSampler` (P263). **Every volumetric explosion must
map 1:1 from these** (sub-volume count/positions from `count`, size from radius,
the tint, the drift), with the P263 fx18 shock-wave ring kept per type. Prove all
7 types on a contact sheet.

**Native sequencing to drive each volume over time** (`effect/explosion.cpp`):
`count` sub-explosions, each starting **0.25 s after the previous one**
(`index * 0.25f`, `:115`), at `position + RandomPosition(radiusExplosion * 0.5)`
(`:117`), random angle (`:119`), scale `radiusExplosion` (`:120`), played as 20 fps
frames (`:191`); the shock wave grows linearly `scale = t × radiusShockWave`
(`:158`) and fades `1 − t²` (`:153`); all drift with the wreck's velocity
(`t × m_dposition`). **Each sub-explosion becomes a volume whose radius and shape
evolve:** fast-then-easing expansion up to `radiusExplosion`, churning
(time-advected) noise so the surface billows, a temperature ramp cooling from
white-hot core to orange/red to smoke, and the smoke continuing to expand and
thin. The staggered sub-volumes should read as one **rolling, growing fireball**
(big ship: 16 over 4 s; large station: 24 over 6 s). Show it in the frame
strips: the size must visibly change over time.

**Gameplay damage is separate and server-side** (`CreateExplosion(damageType,
BlastPower, BlastRadius)`, `projectileIGC.cpp:212`, `missileigc.cpp:522`; area
damage in `clusterIGC.cpp:~407+`; ship deaths deal no area damage). Don't touch
it. **Optional, clearly non-native, off by default:** a setting that sizes the
volume for area-damage explosions to their real `BlastRadius` (danger-zone
readability). Prototype only if cheap, and report it separately.

## ➕ Technique B promoted to a full contender (Domingo: "this is so interesting")

Reference: https://stevencraeynest.wordpress.com/2013/03/29/easy-volumetric-explosion-in-unity3d/
(SM3.0): dense **icosphere** (~15k verts), **vertex displacement along the
normal** from a noise texture with **3 noise patterns in R/G/B**, animated by 3
sines 120° apart (`r = sin(t/dur·2π)·0.5 + 0.25`, etc.) so the surface churns;
scale animated over life; colour + emission from a **1D ramp indexed by
displacement** (low = dark smoke, high = hot core); **clip-threshold erosion**
to fade out (no alpha blending → no sorting, writes depth); Cull None. Users noted
it is heavier than particles.

Implement B properly (not "only if quick"), in MonoGame terms:
- Displacement: vertex texture fetch (`tex2Dlod` in vs_3_0) on DesktopGL **if
  MojoShader/GL supports it here** (prove it); otherwise **ALU noise in the
  vertex shader** or **CPU displacement** (GLES2/Android-safe). Report which.
- **Mesh LOD** by on-screen size (e.g. ~2.5k / 640 / 160 verts; prove the
  silhouette holds) and the same volume cap as A.
- Ramp per native type (the type tint, e.g. violet mine, orange ship), scale
  from `radiusExplosion` over life, sub-explosions staggered 0.25 s, velocity
  drift, all per the native rules above.
- Opaque + depth writes → it must appear in the P547 depth pass (soft smoke
  against it) and pin its own raster state (Cull None) per the P568 lesson.
- Keep trailing smoke as the existing particles (wispy edge).
Include B in every comparison sheet and cost table (1/6/20 explosions), and in
the tier recommendation (B may suit Android better than baked C, or desktop
better than A: decide by numbers and looks).

## Explicitly out of scope

- Making volumetric the default, or shipping it in `master`.
- Changing gameplay explosion data.

## Proof (real window: P572 `scripts/live-proof.sh` / `--autoshots`)

- **Side-by-side real-window screenshots**: Native | Enhanced (P546/P561) |
  Volumetric A | Displaced-sphere B | Baked C for (a) a fighter death, (b) a station death, (c) 6
  overlapping deaths, each at 3 distances and 2 angles, and **frame strips**
  (0.1/0.3/0.6/1.0/2.0 s).
- **Cost:** ms/frame at 2560×1440 for 1, 6 and 20 simultaneous explosions per
  technique (A with LOD on/off), GPU time if measurable; VRAM for C.
- **Android estimate** for C, and whether A is viable on GLES3 devices.
- **Recommendation** (adopt A / B / C / mix; parameters), with the feasibility
  findings.
- Report `docs/reports/P573_VOLUMETRIC_EXPLOSIONS_SPIKE.md` on the spike branch.
  Commit on `spike/p573-volumetric` only; never touch `master` or the main repo
  checkout.

## Verification (common)

- **Failing-first tests** for every fix: the test fails on today's code and passes after.
- **Native fidelity:** cite native file:line for every rule you port. If you find the
  spot-check's reading wrong, prove it with native code and say so.
- Whole-solution build, 0 warnings and 0 errors. Full suite via `scripts/run-tests.sh`
  (P471), 3 consecutive runs **on the final code**. Cite `docs/TEST_FLAKES.md`.
  **Report only numbers you actually measured. Don't re-run a failing suite until it
  comes up clean;** report each run as it happened.
- ⚠️ **Commit as soon as the code and proof are done.** For this spike, run the
  ClientCore.MonoGame test project once instead of the full 3× suite. **Include every new file** (`git status --untracked-files=all`).
  Confirm with `git log -1` and a clean `git status`.
- **Visual changes: the proof must include screenshots from the REAL client window**
  (the backbuffer, e.g. via `ALLEGIANCE_AUTOSHOT`, window 2560×1440). `--capture` /
  offscreen renders alone don't count (they hid the P547 and P562 regressions).
- Committed on the spike branch **`spike/p573-volumetric`** in the dedicated worktree `/home/domingo/Development/AllegianceCore-p573` (NOT master; never `git checkout master`, `git merge` or `git push`; never touch `/home/domingo/Development/AllegianceCore`). Check with `git branch --show-current`.

## ⚠️ Session rule: never end a turn while waiting

This session runs in one-shot mode: **your session ends the moment you finish a message.** Run long commands (the 3× `scripts/run-tests.sh` suite, live proofs, builds) **in the foreground**, blocking, one after another. **Never** start them in the background and then reply "waiting for it to finish". Two earlier sessions (P529, P542) did exactly that: the session ended mid-suite, the background processes were killed, and the suite results were never recorded. If a command might exceed a tool timeout, run each suite run as its own foreground call and record its numbers before starting the next.

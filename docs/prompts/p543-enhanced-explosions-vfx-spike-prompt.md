# P543 — SPIKE: enhanced explosions/VFX (CC0 flipbooks + bloom + soft particles + smoke layer), side-by-side with the original art. Renders plus numbers, not a full rebuild

## Why this task exists

**Domingo (2026-10-04):** "I wonder if we can investigate a replacement for
better explosions/particles. Right now they follow the original game art,
right? Some flat textures are rendered." He approved a **spike**: judge the
look from real renders before committing to replacing every effect.

**Today (verified):**
- `src/Allegiance.ClientCore.Game/Rendering/ExplosionRenderer.cs` (P263)
  draws the **original** `exp20..exp25bmp.mdl` fireball films (RGB565,
  decoded at runtime by `ExplosionAssets.cs` from `ALLEGIANCE_ARTWORK_PATH`)
  at native 20 fps on camera-facing quads, additive blend, plus the `fx18`
  shock-wave ring (billboarded).
- Sizes and timing come from native's `AddExplosion` table
  (`trekigc.cpp:95-177`) through `ExplosionPreset`/`ExplosionSampler`.
  **Keep that driving everything** (parity and readability).
- Other effects use the same flat-billboard approach: `HullHitSpark*`,
  `DamageDebris*`, `MissileTrailParticles`, `ShieldFlare*`,
  `WarpTransit*`, `ShipEngineTrailParticles`.
- **No post-processing at all.** The only `RenderTarget2D` is a capture
  target (`Game1.cs:945`, `Game1.Lifecycle.cs:94`). **The project has no
  custom `.fx` shaders**; only built-in effects are used. MonoGame
  **3.8.5.1** for desktop GL and Android
  (`Allegiance.ClientCore.Android.csproj`).

## The MonoGame way (official docs; follow these, cite them in the report)

- **Custom effects** (https://docs.monogame.net/articles/getting_started/content_pipeline/custom_effects.html):
  - MGFX `.fx` files, either built by the **MonoGame Effect content
    processor** (MGCB, loaded with `Content.Load<Effect>`) or compiled
    offline with **`mgfxc`** to `.mgfxo` and loaded at runtime with
    `new Effect(GraphicsDevice, bytes)`.
  - **OpenGL/Android effects go through MojoShader and are limited to
    `vs_2_0/ps_2_0` and `vs_3_0/ps_3_0`** (no SM4/5 features). Use the
    `OPENGL`/`GLSL` preprocessor symbols for GL variants.
  - **On GL, effect parameter default values don't work**: set every
    parameter from code.
- **Shader compilation on Linux needs Wine** with `d3dcompiler_47`
  (https://docs.monogame.net/articles/getting_started/tools/mgfxc.html,
  https://docs.monogame.net/articles/getting_started/1_setting_up_your_os_for_development_ubuntu.html:
  a 64-bit Wine prefix, winetricks `d3dcompiler_47`, `MGFXC_WINE_PATH`,
  MonoGame's `net8_mgfxc_wine_setup.sh`). **This workstation has no Wine,
  and installing it needs sudo, which you don't have.** So in step 0, don't
  try to install Wine. Instead:
  (a) confirm the MGCB effect build fails here and quote the error;
  (b) prepare the `.fx` sources plus exact `mgfxc` commands for
  **DesktopGL and Android** profiles so Domingo (or the Windows VM
  192.168.0.240, which has DirectX natively, see the `allegiance-windows-build`
  notes) can produce `.mgfxo` files;
  (c) build the spike's **runtime-loading path** (`new Effect(GraphicsDevice,
  File.ReadAllBytes(...))`) so precompiled `.mgfxo` files drop in with no
  pipeline change;
  (d) meanwhile, do everything else **without custom shaders**: built-in
  `BasicEffect`/`AlphaTestEffect`, `SpriteBatch` with blend states, and
  render targets for a shader-free bloom (bright-pass via
  `BlendState`/colour tricks, downsample chains with `SamplerState.LinearClamp`,
  additive composite).
- **Post-processing / render targets** (MonoGame graphics how-tos:
  https://docs.monogame.net/articles/getting_to_know/howto/graphics/; the
  advanced 2D shaders tutorial:
  https://docs.monogame.net/articles/tutorials/advanced/2d_shaders/10_next_steps/index.html):
  - draw the scene into a `RenderTarget2D`, process it, and composite with
    `SpriteBatch.Begin(..., effect)`
  - mind `RenderTargetUsage` and `DepthFormat` (soft particles need a depth
    copy: render linear depth into its own target, since the backbuffer
    depth can't be sampled)
  - restore `GraphicsDevice` states after `SpriteBatch` (this repo has been
    bitten by that leak repeatedly: P339, P525, P529)
- **Particles:** MonoGame has **no built-in particle system**. Either keep
  this repo's own CPU particle classes (`*Particles.cs` + renderers) or
  evaluate **MonoGame.Extended**'s particle system. Say which and why in the
  recommendation; prefer not adding a dependency unless it clearly pays off.

## Scope (spike: a separate branch, nothing wired as default)

0. **Feasibility first:** can this repo compile and load a custom `.fx`
   effect through the MGCB content pipeline **on this Linux box**, for
   DesktopGL (and say what Android would need)? Try a minimal effect. If
   `mgfxc` needs Wine or another tool, document exactly what's needed, and
   continue with shader-free techniques where possible. Report this
   clearly; it decides the real implementation.
1. **Assets (CC0 only; record source URL and license per file in the
   report):**
   - Unity's free VFX flipbooks/image sequences (CC0):
     https://unity.com/blog/engine-platform/free-vfx-image-sequences-flipbooks
   - Kenney particle and smoke packs (CC0):
     https://opengameart.org/content/particle-pack-80-sprites and
     https://opengameart.org/content/smoke-particle-assets
   - (Optional, if time allows) JangaFX's free EmberGen VDBs (CC0), rendered
     to a flipbook in Blender: https://www.cgchannel.com/?p=113054
   - Put assets under a spike folder (e.g. `assets/vfx-spike/`) with a
     README listing licenses. Keep the total size reasonable (downscale to
     the resolution actually used).
2. **An "Enhanced" explosion renderer**, behind a switch and **not the
   default**, driven by the same `ExplosionPreset`/`ExplosionSampler`
   timing and size. Layers:
   - flash
   - high-res fireball flipbook (additive)
   - **smoke flipbook (alpha-blended, lingers longer)**
   - spark streaks
   - the existing shock wave
   - optionally a brief point-light pulse on nearby hulls via BasicEffect's
     lights
3. **Bloom:** threshold, downsample/blur (render targets with linear
   filtering if no custom shaders are available), additive composite. Behind
   its own toggle.
4. **Soft particles** (depth fade), only if custom shaders work (step 0).
   Otherwise document what's needed.
5. **Side-by-side proof:** extend `tools/roster-capture` with a `vfx` mode
   (its offscreen render path, as P525/P526/P529 did). Render a **fighter
   explosion** and a **station explosion** at several time steps (e.g. 0.1,
   0.3, 0.6, 1.0, 2.0 s), **Original vs Enhanced** (and Enhanced + bloom),
   against a ship/asteroid background so intersections are visible. Stitch
   contact sheets.
6. **Cost:** per-frame time of Original vs Enhanced vs Enhanced + bloom on
   this box (offscreen, fixed resolution, e.g. 1280×720, many frames,
   averaged), plus texture memory added. Give an Android caution estimate
   if you can't measure on device.

## Explicitly out of scope

- Replacing other effects (sparks, trails, shield flares, warp). Describe
  how the same approach would apply, but don't implement it.
- Changing gameplay timing or size (`AddExplosion` stays authoritative).
- Making Enhanced the default, or any settings UI.

## Verification and deliverables

- You are running in a **dedicated git worktree**,
  `/home/domingo/Development/AllegianceCore-p543`, already on branch
  **`spike/p543-vfx`**. Commit on that **current branch**, **not master**.
  Never run `git checkout master`, `git merge` or `git push`, and never touch
  `/home/domingo/Development/AllegianceCore`; that's the main repo the rest
  of the queue uses. Confirm with `git log -1` and `git branch --show-current`.
- Whole-solution build, 0 warnings and 0 errors. No full test suite is
  needed for a spike branch, but run the ClientCore.MonoGame test project
  once and report it.
- Report `docs/reports/P543_VFX_SPIKE.md`:
  - the shader-pipeline feasibility result (step 0) with exact commands and
    errors
  - the asset license table
  - the contact sheets (Original | Enhanced | Enhanced + bloom)
  - the frame-time table
  - a recommendation for the real implementation (what to adopt, what it
    needs, the risks)
- **Report only numbers you actually measured.**

## ➕ Widened scope (2026-10-04): "better particle effects for everything"

**Domingo:** "we definitely need better particle effects for everything".
Dense fights and missile launches turn into white patches. **P545** (queued
before this spike) moves every particle effect to **native art** first:
`e_smokebmp`/`e_firebmp` smoke and fire, the `fx09bmp` shield flare,
`fx11bmp`/`f103bmp` hull hits, and so on. Build on that `master` state.

In addition to explosions, the spike now covers:
- **Contact sheets for every effect family:** missile launch and trail
  (0, 0.1, 0.3, 0.6 s), engine trails, shield hit, hull hit, sparks and
  debris, warp transit. Show **P545-native | Enhanced | Enhanced + bloom**.
- **Readability in dense combat:** the same ~10-ship "dense combat" scene P545
  uses. Report the **share of blown-out pixels** (all channels ≥ 250) for
  each variant.
- **Saturation control:** prototype at least one way to keep overlapping
  additive effects from clipping to white, e.g. premultiplied-alpha instead
  of pure additive for smoke, per-effect intensity budgets or caps per
  screen region, or (with bloom) an HDR-ish render target plus tone-map
  composite. Show which one keeps impacts readable.
- The recommendation must cover a full particle overhaul: which effects to
  replace with which CC0 assets, which techniques to adopt, own CPU
  particles vs MonoGame.Extended, and the Android cost.
- The "Explicitly out of scope: replacing other effects" item above is
  superseded **for the spike branch only**. Still nothing becomes the
  default.

## ➕ Smoke readability (2026-10-04): smoke must not blind the pilot

**Domingo:** "smoke looks kind of flat texture that won't let you see
correctly; in combination with the white patch coming out of the missile it
really makes flying difficult, and if you fire several missiles it is worse,
lots of white everywhere." P545 restores native's dim `e_smokebmp` smoke,
which removes the white. **This spike must make smoke look volumetric AND
keep the view readable.** Prototype and compare:
- **Animated smoke:** CC0 smoke flipbooks (Unity's wispy/disc smoke, Kenney's
  smoke packs), with per-particle random start frame and rotation, instead
  of one static flat texture.
- **Alpha, lit smoke, not additive:** tint by the P530 sector light (key +
  ambient), so smoke reads as a grey volume, not glow.
- **Near-camera fade:** fade particles out as they approach the camera's near
  zone (e.g. full at > N m, gone at < M m; pick and justify N/M), so your own
  missiles' smoke never covers the screen. Cheap, needs no shaders.
- **Soft particles** (depth fade at intersections), if step 0's shader path
  works; otherwise documented.
- **Density budget:** cap simultaneous smoke particles per emitter and on
  screen, thinning older puffs first under load.
- **Proof:** a 4-missile volley **from the player's own flight camera**
  (the one Domingo flies with) at 0.2, 0.5 and 1.0 s, comparing
  **P545-native | Enhanced | Enhanced + near-fade**, with the blown-out-pixel
  share and the **share of the screen covered by smoke** (alpha > 0.5) for
  each. The winner should show the missiles' smoke while leaving the
  target and crosshair area readable.

## ⚠️ Session rule: never end a turn while waiting

This session runs in one-shot mode: **your session ends the moment you finish a message.** Run long commands (the 3× `scripts/run-tests.sh` suite, live proofs, builds) **in the foreground**, blocking, one after another. **Never** start them in the background and then reply "waiting for it to finish". Two earlier sessions (P529, P542) did exactly that: the session ended mid-suite, the background processes were killed, and the suite results were never recorded. If a command might exceed a tool timeout, run each suite run as its own foreground call and record its numbers before starting the next.

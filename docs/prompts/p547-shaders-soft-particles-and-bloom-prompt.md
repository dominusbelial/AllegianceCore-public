# P547 — custom shaders: a build script plus runtime `.mgfxo` loading, soft particles (depth-fade) for the P546 smoke, and thresholded bloom as an off-by-default toggle. DesktopGL and Android

## Why this task exists

**Shaders now compile on the workstation** (2026-10-05). Domingo installed
Wine 10, and Claude ran MonoGame's official `net8_mgfxc_wine_setup.sh` →
prefix `~/.winemonogame` (Windows .NET SDK 8.0.401, `d3dcompiler_47.dll`,
MonoGame `fxccs`). `MGFXC_WINE_PATH` is in `~/.profile`. Verified:

```
MGFXC_WINE_PATH=$HOME/.winemonogame WINEDEBUG=-all \
  ~/.dotnet/tools/mgfxc Bloom.fx Bloom.ogl.mgfxo /Profile:OpenGL         # 4272 B
  ~/.dotnet/tools/mgfxc SoftParticle.fx SoftParticle.ogl.mgfxo /Profile:OpenGL  # 2014 B
```

The **OpenGL profile** `.mgfxo` serves **DesktopGL and Android** (MonoGame
docs: GL/GLES go through MojoShader, limited to `vs_3_0/ps_3_0`; **effect
parameter defaults don't work on GL, so set every parameter from code**).

**P543** (spike, branch `spike/p543-vfx`, report
`docs/reports/P543_VFX_SPIKE.md`) left the sources
`assets/vfx-spike/shaders/Bloom.fx` and `SoftParticle.fx` (read them from
that branch). **It claimed a runtime `new Effect(GraphicsDevice, bytes)`
loader, but none exists in its code.** Build it here. P543's numbers:
shader-free bloom re-raises clipping (dense combat 15% → 89.7% blown-out),
so **only thresholded bloom is acceptable, and it's off by default.**

**P546** (queued just before this) adopts the enhanced explosions and
premultiplied animated smoke, with a **near-camera fade**, a density
budget and a `Native | Enhanced` setting (default Enhanced). Build on
P546's code. Read its report first. Soft particles are the proper
version of what the near-fade approximates: smoke fading where it meets
geometry, so there are no hard cut edges against hulls, rocks and
stations.

**The game has no post-processing today:** the only `RenderTarget2D` is
the capture target (`Game1.Lifecycle.cs:94-95`). Android:
`src/Allegiance.ClientCore.Android` exists; the Android SDK
(`~/Android/Sdk`) and the `android` .NET workload are installed.

## Scope

1. **Shader pipeline:**
   - Move the shader sources to a real game location (e.g.
     `src/Allegiance.ClientCore.Game/Shaders/*.fx`).
   - Add `scripts/build-shaders.sh`: compiles every `.fx` to `.ogl.mgfxo`
     with the command above. It fails clearly if `MGFXC_WINE_PATH`/Wine is
     missing and points at the setup steps.
   - **Commit both** the `.fx` and the compiled `.ogl.mgfxo`, so building
     and running the game **never needs Wine**. Ship the `.mgfxo` with
     DesktopGL and Android (match how P546 packages its atlases).
   - A runtime loader (`new Effect(GraphicsDevice, bytes)`), cached, that
     logs once and returns null on failure, so callers fall back.
   - **A staleness test:** each committed `.mgfxo` must be newer than its
     `.fx` (or match a recorded source hash), so an edited shader without a
     rebuild fails CI.
   - Document the toolchain in `docs/SHADERS.md`: Wine, the setup script,
     the env var, the command, and the GL/MojoShader limits.
2. **Soft particles (Enhanced only):**
   - A **linear-depth pass**: render opaque scene geometry (asteroids, ships,
     stations, models) into a depth `RenderTarget2D`, using a tiny depth
     `.fx` if needed. **Prefer a format that works on GLES/Android:** encode
     linear depth into `SurfaceFormat.Color` (RGBA8 packing) unless
     `Single`/`HalfSingle` render targets are proven supported on both
     targets.
   - The smoke and fire draw path (P546) samples it and fades each particle
     by `saturate((sceneDepth − particleDepth) / softness)`. Keep the
     near-camera fade as well.
   - **Fallback:** if the effect or render target is unavailable (or a
     setting turns it off), use the near-fade-only path. Never render
     nothing.
   - Named constants for softness, with their justification.
3. **Bloom (thresholded, toggle, default OFF):**
   - Render the 3D scene to a render target, then bright-pass (threshold),
     then downsample and separable blur (`Bloom.fx`), then an additive
     composite. Draw the HUD and SpriteBatch UI **after** the composite, so
     they don't bloom.
   - Setting `Bloom: Off | On`, default **Off**, plus a CLI flag for
     captures. Named threshold and intensity constants.
   - Must not reintroduce clipping. See the bar below.
4. **Render-state hygiene:** set and restore render targets, blend, depth
   and sampler states (the P339/P525/P529/P545 lessons). Handle window
   resize, device reset and the capture path (`_capTarget`) correctly.

## Explicitly out of scope

- New effect art; shield, hull-hit and warp enhancements.
- Changing P546's look beyond adding soft particles.
- Other post effects (tone mapping, DOF, motion blur), unless strictly
  needed for bloom.

## Verification

- **Failing-first unit tests:**
  - The loader returns null and logs once for a missing or corrupt `.mgfxo`.
  - The staleness test catches an `.fx` newer than its `.mgfxo`.
  - The soft-fade factor (pure function) at depth difference 0, softness,
    and beyond.
  - Linear-depth encode/decode round-trips within tolerance.
  - The Bloom setting defaults to Off; soft particles default to On in
    Enhanced and are absent in Native.
  - Every effect parameter is set from code (no reliance on GL defaults).
- **Headless proof** (`roster-capture vfx`, game classes, as in P546):
  - **Soft particles:** smoke crossing a ship hull and an asteroid,
    near-fade-only vs soft. Measure the hard-edge artefact, e.g. the
    gradient magnitude along the intersection line, and show crops.
  - **Bloom:** dense combat and a station explosion, Off vs On. **Bloom On
    must keep dense combat blown-out ≤ 20%** (P546 Enhanced baseline ~15%;
    the shader-free spike bloom was 89.7%). Report the numbers.
  - Contact sheets in the report assets.
- **Cost:** ms/frame for P546 Enhanced, + soft particles, + bloom, at
  1280×720, and the render-target memory.
- **Live DesktopGL sanity:** the real client against a local server with
  bots fighting for ≥ 60 s with soft particles on, then once with bloom on.
  No exceptions, HUD unaffected, resize works. Screenshots.
- **Android:** build the Android project (`dotnet build` for the Android
  head) with the `.mgfxo` packaged. Report success, the APK containing the
  shader files, and any GLES render-target format concerns. If an emulator
  run is cheap, do it; otherwise say it wasn't run.
- Whole-solution build, 0 warnings and 0 errors. Full suite via
  `scripts/run-tests.sh` (P471), 3 consecutive runs **on the final code**.
  Cite `docs/TEST_FLAKES.md`. **Report only numbers you actually measured.
  Don't re-run a failing suite until it comes up clean;** report each run as
  it happened.
- ⚠️ **Commit as soon as the code, tests and proof are done, BEFORE the 3×
  suite**, then amend with suite numbers. **Include every new file,
  including the `.fx`, `.mgfxo` and `docs/SHADERS.md`**
  (`git status --untracked-files=all`). Confirm with `git log -1`, a clean
  `git status`, and a clean-worktree build.

## Deliverables

1. Committed on **`master`**, not pushed. Check with
   `git branch --show-current`. Don't touch `spike/p543-vfx`.
2. The report `docs/reports/P547_SHADERS_SOFT_PARTICLES_BLOOM.md`: the
   pipeline, loader, staleness test, soft-particle design (depth format and
   GLES choice), bloom design, numbers, cost, live screenshots, the Android
   result, tests.

## ⚠️ Session rule: never end a turn while waiting

This session runs in one-shot mode: **your session ends the moment you finish a message.** Run long commands (the 3× `scripts/run-tests.sh` suite, live proofs, builds) **in the foreground**, blocking, one after another. **Never** start them in the background and then reply "waiting for it to finish". Two earlier sessions (P529, P542) did exactly that: the session ended mid-suite, the background processes were killed, and the suite results were never recorded. If a command might exceed a tool timeout, run each suite run as its own foreground call and record its numbers before starting the next.

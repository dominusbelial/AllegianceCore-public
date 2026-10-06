# Custom Shaders — toolchain & usage (P547)

The game ships two custom effects (plus the linear-depth pass that powers soft
particles), compiled from `.fx` sources with MonoGame's official `mgfxc`, and
loaded at RUNTIME from committed bytecode — so building AND running the game
never needs a shader compiler.

| Shader | Purpose |
|---|---|
| `Bloom.fx` | Thresholded bloom: bright-pass → separable Gaussian → additive composite. |
| `SoftParticle.fx` | Depth-fade for the ENHANCED smoke (P546): fade each puff where it meets the scene's linear depth. |
| `LinearDepth.fx` | Renders opaque scene geometry (ships/asteroids/stations) into an RGBA8 linear-depth target the soft-particle shader samples. |

Sources: `src/Allegiance.ClientCore.Game/Shaders/*.fx` (documentation / the
rebuild input).
Compiled bytecode: `src/Allegiance.ClientCore.MonoGame/Content/Shaders/*.ogl.mgfxo`
(committed, shipped with DesktopGL and Android).

---

## Build / rebuild the shaders

```
scripts/build-shaders.sh
```

This compiles every `*.fx` in `src/Allegiance.ClientCore.Game/Shaders/` to a
`.ogl.mgfxo` in `src/Allegiance.ClientCore.MonoGame/Content/Shaders/` using the
OpenGL profile (serves **DesktopGL AND Android**), and writes a
`<stem>.fx.sha256` source-hash record next to each bytecode file.

Requirements (fail-fast with pointers to the setup below):

1. **`mgfxc`** — `dotnet tool install -g dotnet-mgfxc`
2. **`MGFXC_WINE_PATH`** — the Wine prefix holding the Windows .NET SDK +
   `d3dcompiler_47` (see "The Wine toolchain").
3. Wine 10+ (mgfxc shells out to `wine64 dotnet c:\fxccs.dll`).

Run `scripts/build-shaders.sh` and it prints each `fx -> mgfxo` with a byte
count, or fails with a clear "point at the setup steps" message.

### Staleness guard

Each committed `.mgfxo` must match its `.fx` source hash (or be rebuilt). This
is enforced by the unit test
`P547ShaderTests.Mgfxo_staleness_each_committed_shader_matches_its_source_hash`:
if you EDIT a `.fx` and don't rebuild, the committed `.fx.sha256` no longer
matches and CI fails. (Hash-based rather than mtime-based because a fresh git
checkout collapses all mtimes to checkout time.)

---

## The Wine toolchain (Domingo's workstation)

Installed on 2026-10-05 by Domingo. Wine 10 + MonoGame's official
`net8_mgfxc_wine_setup.sh`, prefix `~/.winemonogame`.

Verified working commands (both DesktopGL and Android use `/Profile:OpenGL`):

```
MGFXC_WINE_PATH=$HOME/.winemonogame WINEDEBUG=-all \
  ~/.dotnet/tools/mgfxc Bloom.fx Bloom.ogl.mgfxo /Profile:OpenGL
```

The prefix needs the Windows .NET SDK (`8.0.401`) and `d3dcompiler_47.dll`,
which the setup script provisions. `MGFXC_WINE_PATH` is also exported in
`~/.profile`.

## GL / MojoShader limits (why the OpenGL profile, why set every parameter)

- DesktopGL and Android both execute the OpenGL profile through **MojoShader**,
  which is limited to `vs_3_0`/`ps_3_0` — no SM4/5. The `.fx` files declare
  `#define VS_SHADERMODEL vs_3_0` / `PS_SHADERMODEL ps_3_0`.
- **Effect parameter defaults do not work on GL.** Every parameter must be set
  from code each frame. The renderers do this explicitly:
  - `SoftParticleRenderer` sets `WorldView, WorldViewProj, InvViewportSize,
    Near, InvRange, SoftnessInv, Texture, DepthTexture`;
  - the depth pass (`AsteroidRenderer/StationRenderer/ShipRenderer.DrawDepth`)
    sets `WorldView, WorldViewProj, Near, InvRange`;
  - `BloomPostProcessor` sets `TexelSize, Threshold, BloomScale, Direction,
    SceneTexture`.
  A unit test (`P547ShaderTests.Every_effect_parameter_is_set_from_code`)
  parses each `.fx`'s declared top-level uniforms and asserts they exactly
  match what the renderers set, so nobody can slip in a parameter the code
  forgets to set.
- The backbuffer's depth buffer cannot be sampled on OpenGL, so the scene's
  linear depth is rendered into its **own RGBA8 RenderTarget2D** (`LinearDepth
  .fx`) — `SurfaceFormat.Color` is universally supported on GLES/Android,
  whereas `Single`/`HalfSingle` targets are not guaranteed. Linear depth is
  packed into 32 bits with the Valve float-RGBA encoding (see
  `Rendering/LinearDepth.cs`, self-consistent with the shaders).

## Shipping & runtime loading

- DesktopGL: the MonoGame csproj copies `Content/Shaders/*` (the `.ogl.mgfxo`
  bytecode + the `.fx.sha256` staleness records) into the output `Content/`.
- Android: `scripts/pack-android-assets.sh` copies the compiled DesktopGL
  `Content/` (including `Content/Shaders/`) into `Assets/Content/`, so the same
  `TitleContainer` path resolves inside the APK.
- Runtime: `Rendering.ShaderAssetCache` loads a committed `.mgfxo` via
  `TitleContainer.OpenStream → new Effect(gd, bytes)`, caches the result (incl.
  null), and logs a failure exactly once so callers degrade gracefully:
  soft particles → near-fade-only; bloom → off. It never throws.
- `Rendering.VfxPostProcessor` owns the depth/scene render targets and the
  soft-particle + bloom effects; its `SetSize` keeps targets in step with the
  window on resize.

## Toggles

- Soft particles: default ON in Enhanced; `--soft off` disables; absent in
  Native. Falls back to near-fade-only if the shader/target is unavailable.
- Bloom: default OFF; `--bloom on` enables (a look toggle; thresholded so dense
  combat stays ≤ ~20% blown-out). HUD/SpriteBatch UI draws AFTER the bloom
  composite so it never blooms.

See `docs/reports/P547_SHADERS_SOFT_PARTICLES_BLOOM.md` for the design, the
headless proof numbers, cost, and the Android result.

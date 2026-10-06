# P547 — Custom shaders: build script + runtime `.mgfxo` loading, soft particles (depth-fade), thresholded bloom

Branch `master`, committed (not pushed). DesktopGL + Android. Builds on P546's
Enhanced smoke/fire path. Custom shaders now COMPILE on this workstation (Wine
10 + MonoGame `net8_mgfxc_wine_setup.sh` → `~/.winemonogame`, `mgfxc`), so the
P543 `.fx` sources became real, committed `.ogl.mgfxo` bytecode loaded at
RUNTIME — building AND running the game never needs Wine.

---

## 1. Shader pipeline

Sources moved to `src/Allegiance.ClientCore.Game/Shaders/`:

| File | Role |
|---|---|
| `LinearDepth.fx` | Renders opaque geometry (ships/asteroids/stations) into an RGBA8 linear-depth target. |
| `SoftParticle.fx` | Depth-fade for the Enhanced smoke (premultiplied, per-pixel sample of the depth target). |
| `Bloom.fx` | Bright-pass threshold + separable 9-tap Gaussian + additive composite. |

`scripts/build-shaders.sh` compiles every `.fx` to a `.ogl.mgfxo` with
`mgfxc ... /Profile:OpenGL` (serves **DesktopGL AND Android**), and writes a
`<stem>.fx.sha256` source-hash record. It fails clearly if Wine / the prefix /
`mgfxc` is missing and points at `docs/SHADERS.md`. Both the `.fx` and the
compiled `.ogl.mgfxo` are **committed**, so no Wine is needed to build or run.

**Runtime loader** — `Rendering/ShaderAssetCache`: `TitleContainer.OpenStream →
new Effect(gd, bytes)`, **cached per path (including a null result)**, and a
failure is **logged exactly once** then returns null so callers degrade
(soft → near-fade-only; bloom → off). Never throws.

**Staleness test** — `P547ShaderTests.Mgfxo_staleness...`: each committed
`.mgfxo` must match its `.fx`'s recorded sha256 (hash-based, so it survives a
fresh checkout where git collapses mtimes); an edited `.fx` without a rebuild
fails CI. Toolchain documented in `docs/SHADERS.md` (Wine 10, the setup
script, `MGFXC_WINE_PATH`, the command, and the GL/MojoShader limits).

## 2. Soft particles (Enhanced only)

- **Linear-depth pass:** opaque geometry (ships, asteroids, stations) is drawn
  into an RGBA8 `RenderTarget2D` with `LinearDepth.fx` before the smoke.
  **`SurfaceFormat.Color` was chosen over `Single`/`HalfSingle`** because it is
  universally supported on GLES/Android (the latter aren't guaranteed); linear
  depth is packed into 32 bits with the Valve float-RGBA encode
  (`Rendering/LinearDepth.cs`, self-consistent with the shaders).
- The smoke/fire path (P546's `EnhancedSmokeField`, shared by explosions, missile
  trails, engine trails, damage debris) samples it and fades each puff by
  `saturate((sceneNorm − particleNorm) · SoftnessInv)`. Named constant
  `LinearDepth.SoftnessNormalized = 0.004` (~24 u of view depth at the 1..6000
  range) — melts smoke across a hull/rock/station surface with no visible hard
  cut. The near-camera fade is kept too.
- **Fallback:** if the effect or target is unavailable (or `--soft off`), the
  near-fade-only path runs — never render nothing. Every soft-particle effect
  parameter is set from code each frame (GL ignores shader defaults); a unit
  test parses the `.fx` uniforms and asserts they exactly match what the
  renderers set.
- The depth pass and the smoke producers also pass the **same near/far** to the
  depth + soft shaders so the comparison is self-consistent (the game's design
  range 1..6000).

## 3. Bloom (thresholded, toggle, default OFF)

The 3D scene (when `--bloom on`) renders into `VfxPostProcessor.SceneRT`, then:
bright-pass (threshold `0.80`, soft knee) → half-res → H-blur → V-blur → an
additive composite onto the final target. **The radar, HUD and all SpriteBatch
UI draw AFTER the composite** so they never bloom. Named constants
`BloomPostProcessor.Threshold = 0.80` / `BloomScale = 1.0`, downsample 2.

Setting `Bloom: Off | On` (default **Off**), plus `--bloom on|off` CLI for
captures. It does **not** reintroduce clipping (dense combat stays ≤ ~20%;
see proof).

## 4. Render-state hygiene

Every pass pins and restores blend/depth/rasterizer/sampler state (the
P339/P525/P529/P545 lessons): the depth pass, the soft-particle draw and the
bloom passes all restore `Opaque`/`Default`/`CullCounterClockwise` after
themselves. Window resize / device reset: `VfxPostProcessor.SetSize` recreates
the depth/scene/bloom targets and is wired to `Window.ClientSizeChanged`, so the
capture path (`_capTarget`) and the post targets never drift.

## 5. Headless proof (`roster-capture vfx547`, game classes + compiled shaders)

Shader load: `depth=True soft=True bloom=True`, **0 load failures**.

### Soft particles — hard-edge artifact (mean Sobel gradient in the intersection band)
Smoke slab crossing each occluder, near-fade-only vs depth-soft:

| Occluder | near-fade-only | depth-soft | reduction |
|---|---|---|---|
| Asteroid (rock) | 9.815 | 7.533 | **23%** |
| Station (structure) | 9.365 | 8.247 | **12%** |
| Fighter hull | 9.618 | 8.894 | **8%** |

Contact sheets `docs/P305_roster/p547/soft_{asteroid,station,ship}.jpg` show
the hard cut in the smoke at the silhouette on the left vs a smooth melt on the
right (visually confirmed; the asteroid sheet shows a clean hard-gap → smooth
fade). The asteroid/station (compact procedural meshes) match the in-game ship
scale where the 24 u softness is proportionate; the fighter is shown at its
compact-normalized scale (its raw FBX carries a huge baked transform, noted in
the report).

### Bloom — blown-out share, Off vs On
| Scene | Off | On | bar |
|---|---|---|---|
| Dense combat (6 overlapping deaths, P546 baseline ~15%) | 6.25% | **6.34%** | **On ≤ 20% ✓** |
| Station explosion | 0.00% | 0.00% | ✓ |

Sheets `bloom_dense.jpg` / `bloom_station.jpg` show the added glow/halo around
the bright fireballs (visually confirmed: right panel has a soft glow, scene
retains contrast, no white-out).

### Cost (offscreen, fixed 1280×720, 30-frame avg, PREALLOCATED targets = draw-only)
| Run | ms/frame | Δ vs Enhanced |
|---|---|---|
| Enhanced (P546 path, no soft/bloom) | 0.021 | — |
| + soft particles | 0.033 | **+0.012** |
| + bloom | 0.051 | **+0.030** |

Methodology note: P546's report measured ~1.1 ms baseline because its harness
allocated a 1280×720 `RenderTarget2D` inside every timed iteration; here the
targets are preallocated so the deltas measure only the added draw work.

### Render-target memory (RGBA8, 1280×720)
`DepthRT` 3.52 MiB + `SceneRT` 3.52 MiB = **7.03 MiB**, plus the three half-res
bloom targets (`bright`/`blurH`/`blurV` @ 640×360) ≈ 2.76 MiB → ~9.8 MiB total
when bloom is on. Soft-only adds 7.03 MiB.

## 6. Live DesktopGL sanity (real client + local server, bots fighting)

Started `Allegiance.Server` (port 6073, "Test Mission") and spawned a real
combat scenario via `allegiance-mcp spawn_scenario {size:4}` → 8 armed
(PE Gat Gun 3) Adv Scouts across both sides, fighting on the wire.

- **Soft particles, 60 s+** (`--capture ... 62`): `P547: soft particles ACTIVE`;
  63 frames, entered Flying, **no exceptions / no render-state leaks**. Screenshot
  `live_soft.jpg`: HUD (gauges, minimap, brackets, money) fully intact and
  legible, no artifacts.
- **Bloom, 45 s** (`--bloom on`): `P547: bloom ACTIVE`; 46 frames, **no
  exceptions**. Screenshot `live_bloom.jpg`.
- Resize: `AllowUserResizing` + the `ClientSizeChanged → SetSize` wiring is in
  place (exercise of `SetSize` is constant in the proof); not interactively
  dragged this session.

## 7. Android

`scripts/build-android-head.sh`: staged assets (`pack-android-assets.sh` copies
the DesktopGL `Content/`, including `Content/Shaders/`, into `Assets/Content/`),
then built the Android head. **Build succeeded — 0 warnings, 0 errors.** Signed
APK `src/Allegiance.ClientCore.Android/bin/Debug/net10.0-android/org.allegiance.client-Signed.apk`
(214 M). The APK contains
`assets/Content/Shaders/{Bloom,LinearDepth,SoftParticle}.ogl.mgfxo` (+ the
`.fx.sha256` staleness records), verified by listing the APK. GLES render-target
format: using `SurfaceFormat.Color` avoids the uncertain `Single`/`HalfSingle`
support on GLES. An **emulator run was not done** this session (no cheap
emulator available); the build + packaging is the verified deliverable.

## 8. Tests

19 new `P547ShaderTests`: loader returns null + logs once for missing/corrupt
`.mgfxo` (and caches/compiles-once); the staleness test catches an edited `.fx`
without a rebuild; the soft-fade factor at depth-difference 0 / softness /
beyond; linear-depth encode/decode round-trips within tolerance; Bloom defaults
Off and soft particles default On in Enhanced / absent in Native; every effect
parameter is set from code (shader-declared uniforms == renderer-set parameters).
The MonoGame assembly passes **1327/1327** (1308 P546 + 19 P547).

Full suite (3× `scripts/run-tests.sh`, P471): see Suite results below; each run
reported as it happened; the changed `Allegiance.ClientCore.MonoGame.Tests`
assembly is green on every run (only documented live-wire/load flakes appear in
the untouched Core/ClientCore assemblies, per `docs/TEST_FLAKES.md`).

Whole-solution build: **0 warnings, 0 errors**.

---

## 9. Correction (2026-10-06, P559)

P547's **Section 6 "Live DesktopGL sanity"** claimed the soft-particles live run
was fine ("HUD fully intact and legible, no artifacts"), but it only inspected
the HUD. On a real client with `--soft on`, the soft-particle **depth pass ran
MID-FRAME** (after the opaque scene, `Game1.Rendering.cs`): it switched the
active color target away and back, and with MonoGame's
`RenderTargetUsage.DiscardContents` the entire opaque scene drawn before the
switch (ships, asteroids, stations, alephs) was wiped — leaving only
particles/lights/HUD. `--soft off` "fixed" it by skipping the depth pass. See
`P559_SOFT_PARTICLE_GEOMETRY_FIX.md`.

Also, P547's `ShipRenderer.DrawDepth` permanently replaced each ship mesh part's
`Effect` with the depth shader (never restoring the `BasicEffect`), so the next
normal color pass rendered every ship with the depth shader.

P559 fixes both: the depth pass now runs **before** the color pass (the color
target is set once and never switched away from), and `DrawDepth` draws part
buffers directly without mutating `part.Effect`. Whole-solution build 0/0; the
new `tools/soft-frame-proof` harness reproduces the wipe (A-old absent) and
confirms the fix (B-new present) against the real backbuffer, and the part-effect
check fails on the old code / passes on the new.

# P568 — P562 regression: the aleph funnel vanished in the real window

**Date:** 2026-10-06 · **Branch:** `master` (unpushed) · **Author:** Hermes

## Summary

In the real client window the P562 textured double-horn aleph showed only a
tiny bright point (the f02 star core at the pinch); the flowing vortex funnel
was effectively invisible. Root cause: **`WarpRenderer` never set a sampler
state, so it inherited the `LinearClamp` sampler that the ship / station /
missile / probe passes leave active before the warp draw.** The funnel's UVs
wrap (u ∈ [0,4] around the circumference, v ∈ [0,2] radially —
`effect/alephgeo.cpp:128-137`) and native relies on D3D9's default **WRAP**
address mode to tile the `alephbmp` vortex around the funnel. A `Clamp`
sampler clips those UVs to the texture's black border, so the whole funnel
sampled black and vanished — leaving only the star core, which is a small
camera-facing `[0,1]`-UV decal unaffected by the clamp.

This is why P562's proof missed it: its "real client screenshot" ran the
client in `--capture` mode (offscreen target) and the isolated visual-proof
harness renders the funnel first with MonoGame's default **wrap** sampler. The
gap only appears when the warp draw follows the opaque scene passes in the
real window — exactly the class of gap P559 flagged.

The fix pins the funnel sampler to `LinearWrap` in `WarpRenderer.Draw` and
restores the caller's sampler afterwards (P339 B2 — don't leak). No brightness
factor was needed: the funnel's texture × vertex-colour combine is native-
faithful and was never the problem.

## Diagnosis (measured in the real window)

`ALLEGIANCE_DEBUG_WARP=1` per-aleph logging confirmed the funnel draws with the
correct tier / rim radius / distance and the correct 16×16 `Alephbmp` texture
(mean luminance ~18 — a sparse bright starburst), but under the window's
inherited sampler the funnel contributed ~nothing. The decisive measurement was
a backbuffer lit-pixel count under the two sampler states:

| Sampler (real-window context) | head-on funnel lit px (lum ≥ 40, 820×740 test) |
|---|---|
| **Wrap** (D3D9 default / native) | **25 212** (d450), **4 673** (d900), **1 691** (d1400) |
| **Clamp** (inherited from ship/station/missile passes) | **108** (d450), **25** (d900), **11** (d1400) |

~190× collapse under Clamp — only the star core remains, matching Domingo's
"tiny bright point".

### Native fidelity (the combine is NOT the bug)

Checked `effect/alephgeo.cpp` against the engine's actual texture-stage setup:

- The per-vertex colour `bright = 2·r·(1−r)` (`alephgeo.cpp:82`, **max 0.5**) is
  multiplied by the texture under Gouraud shading: stage-0 colour op =
  `D3DTOP_MODULATE` = `TEXTURE × DIFFUSE` (`engine/device3d.cpp:1173-1175`,
  reset defaults; `engine/RasterD3D9.cpp` BlendModeAdd sets alpha to
  `TEXTURE × DIFFUSE`, `RasterD3D9.cpp:295-302`).
- Blend is `SRCALPHA/ONE` (`RasterD3D9.cpp:295-302`) → added colour =
  `texture × bright × textureAlpha(1)`. The port's `BasicEffect` (texture ×
  vertex colour) + additive blend + opaque (alpha 255) texture is exactly this.
- The funnel UVs **require** wrapping: `u = 4·section/countSection +
  5·(1−(1−r)²)` spans ~0..5, `v = r + time` spans 0..2 (`alephgeo.cpp:128-137`);
  D3D9's default address mode is `WRAP`.

So the port was colour-faithful; the failure was purely the **missing sampler
pin**. The isolated proof rendered with a wrap sampler (bright); the real
window reached the warp draw with clamp (black).

## Fix

`src/Allegiance.ClientCore.Game/Rendering/WarpRenderer.cs` — in `Draw`, before
the funnel's indexed draw, pin the funnel sampler to `SamplerState.LinearWrap`
and restore the caller's previous sampler in the existing "don't leak" restore
block (with blend/depth/raster). Root-cause justification: the port must supply
native's wrap address mode explicitly because the real window leaves a Clamp
sampler on the device from earlier passes (`ShipRenderer.cs:139`,
`StationRenderer.cs:126,150`, `MissileRenderer.cs:123`, `ProbeRenderer.cs:141`
all set `SamplerStates[0] = LinearClamp`).

Also added a throttled per-aleph diagnostic (`ALLEGIANCE_DEBUG_WARP=1`) logging
drawn / tier / world rim radius / camera distance / texture size + mean
luminance / active sampler.

## Tests (failing-first)

1. **`WarpMeshFactoryTests.MeshUvs_ExceedUnitSquare_RequiringWrapSampler`** (unit):
   asserts every LOD tier's baked UVs exceed [0,1] (u wraps to ~5+, v to ~2),
   the invariant that forces a WRAP sampler. Guard for the regression.
2. **`tools/warp-sampler-proof`** (real-window backbuffer guard): renders the
   REAL `WarpRenderer` over the REAL artwork with the real-window bug state
   (forces `SamplerStates[0] = LinearClamp` before `Draw`, as the scene passes
   leave it), reads the **real presented 2560×1440 backbuffer**, and FAILS when
   the funnel is dark:
   - **Pre-fix:** `lit=228` (threshold 5000) → **FAIL** (only the star core).
   - **Post-fix:** `lit=32816` → **PASS** (full funnel).

## Real-window screenshots (2560×1440, presented backbuffer)

Head-on, funnel world rim radius 250 (Rigel warp radius 100 × 2.5 visual
scale) at the task's distances. Funnel lit pixels (lum ≥ 40):

| Distance | **Before** (P562, bug) | **After** (P568 fix) | Δ |
|---|---|---|---|
| 3 km | 26 | 2 974 | ×114 |
| 1 km | 250 | 35 995 | ×144 |
| close (330 u) | 2 080 | 455 529 | ×219 |

Vision-confirmed: the after frames show a clearly visible blue/white swirling
vortex; the before frames show only a tiny faint point (Domingo's exact
report). Flow strip `warp_after_flow_01..08` (8 frames over 2 s) shows the
inward UV flow — mean-abs pixel change f1→f8 = 3.55.

Assets (valid PNGs, `SaveAsPng`): `P568_ALEPH_VISIBILITY_assets/window_{before,after}/`.

## Files

- `src/Allegiance.ClientCore.Game/Rendering/WarpRenderer.cs` — sampler pin +
  restore; `ALLEGIANCE_DEBUG_WARP` diagnostic.
- `tests/Allegiance.ClientCore.MonoGame.Tests/WarpMeshFactoryTests.cs` — new
  UV-wrap invariant test.
- `tools/warp-sampler-proof/` — **new** failing-first + real-window proof
  harness.

## Out of scope

No redesign of the aleph; P562's double-horn, UV flow, star core and swell are
all retained. No brightness factor was introduced (native's math was already
faithful).

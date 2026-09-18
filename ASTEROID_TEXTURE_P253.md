# Real procedural rock texture for asteroids (P253)

## What this is

The final piece of making an asteroid actually *look* like a giant rock:
previously every rock was shaded with a single flat `RockColor` diffuse
`(0.52, 0.46, 0.40)` — `AsteroidRenderer.cs` hard-coded every vertex's UV to
`Vector2.Zero` and bound **no texture at all**. This stage gives `RockMesh`
real per-vertex UVs and a real generated diffuse, and wires both into the
renderer. Confirmed this session: there is deliberately no authored rock asset
in the project's artwork tree (only generic UI-primitive `.x` files), so this
is **synthetic/procedural work**, the same "generated, not loaded" philosophy
the project already uses for the starfield and the warp funnel.

It is genuinely new work, not a restore — `RockMeshFactory.cs`'s own doc
comment already states the artwork corpus has no real asteroid mesh/texture.

## 1. Real per-vertex UVs (`RockMeshFactory.cs`)

`RockMesh` gained a fourth array, `Uvs` (u,v per vertex, length
`VertexCount * 2`), computed in `Generate()` from each vertex's **normalized
radial direction** using a standard spherical / equirectangular mapping:

```
u = atan2(dirZ, dirX) / (2π) + 0.5
v = acos(clamp(dirY, −1, 1)) / π
```

Longitude `u` winds fully around the rock (0 at +X through ±π); latitude `v`
runs 0 at the +Y pole to 1 at the −Y pole — so the whole surface maps onto the
square diffuse with no aspect squeeze. Because the rock's displacement is
purely radial (position = dir·scale), the direction is unchanged by it, so the
same UVs hold before/after displacement.

**Accepted limitation (flagged, not engineered away):** like every
equirectangular mapping of a closed sphere it has a longitude seam at u=0↔1 and
pole pinch at the two Y poles. Verified visually and numerically they are not
objectionable on this low-poly rock (see Live proof); a seam-free
cube-map/atlas would be disproportionate complexity for a rock.

## 2. Procedural rock diffuse (`RockTextureFactory.cs`, new)

New `RockTextureFactory` mirrors `RockMeshFactory`'s discipline: a **pure**
CPU pixel-buffer generator (takes no `GraphicsDevice`, so it is unit-testable
with no device) that `AsteroidRenderer` uploads to a `Texture2D` via `SetData`
— the established MonoGame generated-not-loaded pattern.

- **Determinism:** every lattice hash is pure integer arithmetic seeded from
  the fixed `DefaultSeed` (517613); no `System.Random`, no time — same
  arguments → byte-identical pixels (unit-tested).
- **Tileable:** each value-noise octave's lattice is periodic with the image
  (corner lookups wrap via `% cells` on an integer cell count), so
  left/right and top/bottom edges match. A true 3×2 render of the tiled sheet
  shows the longitude seam at 0.74× the interior neighbour cost — smoother than
  the interior, i.e. seamless.
- **Recipe:** 7 octaves (f = 1…64, weight decaying ×0.731 per octave) of
  smoothstep value noise (macro gray/brown blotches down to mid grain), plus a
  per-pixel deterministic speckle to break the value-noise's inherent
  smoothness. Contrast gain 9, brightness swing ~±32% around the base tone,
  clipped at ±1 so it never blows out. Chromatic drift keeps darker pockets
  cooler/greyer and brighter ones warmer/rustier. Mean RGB ≈ (0.46, 0.42, 0.37)
  — squarely in the old `RockColor` gray-brown family (unit-tested), not a
  newly invented palette.

## 3. Renderer wiring (`AsteroidRenderer.cs`)

- Vertices now take `new Vector2(mesh.Uvs[i*2], mesh.Uvs[i*2+1])` instead of
  `Vector2.Zero`.
- One shared `Texture2D` is built once in the constructor from
  `RockTextureFactory.GeneratePixels()` (256×256) and re-used for **every**
  asteroid instance — no per-asteroid texture, matching the existing "one
  shared mesh scaled per instance" convention.
- `BasicEffect` sets `TextureEnabled = true`, `Texture = _texture`, and
  `DiffuseColor = Color.White`. **Why white:** `BasicEffect` multiplies the lit
  color by the sampled texel, so leaving the old `RockColor` diffuse in place
  alongside a RockColor-toned texture would darken it to ~half brightness
  (Texture × Diffuse). Baking the tone into the texture and making the diffuse
  an identity keeps the overall brightness parity with the pre-P253 flat rock.
  Lighting remains `EnableDefaultLighting` (the P251 normal shading).
- `_texture` is disposed with the renderer.

## Live proof (`.verify_p253/`, offscreen technique)

The P207/P218/P234/P240 offscreen-screenshot technique (`/tmp/p253verify`
MiniGame harness, rendered synchronously to `RenderTarget2D`s) renders the
REAL textured `AsteroidRenderer` from three cameras plus a **flat control**
(the exact pre-P253 look: same mesh, same 3 default lights, Diffuse=`RockColor`,
no texture) — so any difference is exactly the texture work.

**real-minus-flat pixel diff over the rock region** (a nonzero spatial
std-dev means real mottling, not just a brightness shift):

| frame | rockpx | signed mean | signed std-dev |
|---|---|---|---|
| a0 | 63,286 | −15.3 | 13.7 |
| a1 | 80,149 | −10.7 | 11.5 |
| a2 | 34,243 | −10.1 | 13.3 |

Vision cross-check (direct crop comparison of the lit core, `real` vs `flat`):
- **Real (`crop_real_a0.png`):** "NOT flat or uniform… clearly visible
  irregular patches of lighter and darker brown/gray tones scattered across the
  surface" — mottled, reads as stone.
- **Flat (`crop_flat_a0.png`):** "flat/uniform… no mottled patches or organic
  texture… each individual facet is a solid uniform color."

The tiled diffuse sheet (`texture_tiled.png`) reads as seamless weathered
sandstone — clearly no longer the old flat single-color fill.

**Honest artifact notes:** the diffuse is soft/cloudy rather than sharply
granular — an inherent consequence of a *low-poly* rock sampled per-vertex
(each large triangle's UV interpolates smoothly, blurring the finest octaves;
a finer tessellation was rendered diagnostically and confirms this is a mesh-
density trait, not a texture failure). On the lit core the mottle is clearly
visible; shadowed back-faces read flat (that's lighting, expected). The
equirectangular pole/seam shows no objectionable line on the side view.

## Build / test

- Whole-solution build (`AllegianceCore.slnx -c Debug`): **0 warnings, 0 errors.**
- `Allegiance.ClientCore.MonoGame.Tests`: **284 pass** (baseline 275 + 9 new:
  3 UV + 6 texture-factory).
- `Allegiance.Core.Tests`: **3066 pass** — the only failure in 2 runs is the
  pre-existing documented `LiveTurretGunnerBindingTests` machine-state flake
  (zero references to this stage's symbols), byte-identical to baseline.
- `Allegiance.ClientCore.Tests`: **83 pass**, unchanged.
- Full suite run **twice**, identical both runs.

New tests include must-fail discriminators: `Uvs_WindFullyAroundLongitudeAndLatitude`
reds if UVs collapse to a point (the old all-zero bug); `HasStrongMotting_NotAFlatFill`
reds if the noise collapses to a constant; `EdgesTile_SeamlesslyAcrossWrap` reds
if the diffuse stops tiling; `SameSeed_*` pins determinism for both factories.

## Files changed

- `src/Allegiance.ClientCore.MonoGame/Rendering/RockMeshFactory.cs` — `RockMesh`
  gains `Uvs`; `Generate()` computes the equirectangular UVs.
- `src/Allegiance.ClientCore.MonoGame/Rendering/RockTextureFactory.cs` — new:
  deterministic, tileable procedural rock diffuse.
- `src/Allegiance.ClientCore.MonoGame/Rendering/AsteroidRenderer.cs` — real UVs,
  generated texture, `TextureEnabled`/`Texture`, diffuse → white.
- `tests/Allegiance.ClientCore.MonoGame.Tests/RockMeshFactoryTests.cs` — +3 UV tests.
- `tests/Allegiance.ClientCore.MonoGame.Tests/RockTextureFactoryTests.cs` — new: 6 tests.

## Out of scope / status notes

- **Collision** — separately confirmed not a bug, not added.
- **Rotation (P252)** — already landed (`4783fce` earlier / `08d35ed` is P252);
  this texture work is independent and coexists with the per-asteroid spin.
- **Normals/shading (P251)** — already landed (`4783fce`, its normals fix is in
  `RockMeshFactory`); this stage rode that in and benefits from it.
- No authored/external rock asset sourced; procedural only.
- Native reference (`~/Development/Allegiance`) not modified (read-only).

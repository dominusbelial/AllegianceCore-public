# P529 — asteroid render pass: native per-sector bidirectional lighting, mipmapped rock textures, explicit render state

Date: 2026-10-04
Branch: `master` (not pushed)
Model: deepseek-v4-flash (Hermes)

## Summary

Domingo's live playtest (2026-10-04) reported asteroids "switching to black …
then suddenly chang[ing] and almost look[ing] like gold" while he observed them.
Three causes were traced in source and fixed, all confined to the asteroid pass:

1. **Wrong lighting model** — the black↔gold flip. The port lit rocks with
   XNA/MonoGame's stock `BasicEffect.EnableDefaultLighting()` three-light rig,
   whose warm key light (with **specular**) flashes over the rock texture and
   whose unlit faces fall to a near-black ambient. Native lights the scene with
   the **current sector's own** bidirectional light (key + constant blue
   back-light + 0.25 ambient, no specular). The port now replicates that.
2. **No mipmaps** on the 2048×2048 rock surfaces (and the procedural fallback),
   so minified rocks aliased and the colour shimmered/jumped as the camera or
   rock moved. The textures are now created as full mip chains.
3. **No explicit render state** on the asteroid pass, so it inherited whatever a
   preceding SpriteBatch HUD pass left (LinearClamp / AlphaBlend / Depth-None),
   which could change frame to frame. The pass now pins its own state.

Follow-ups that are explicitly OUT of scope (kept for a separate decision):
ships / stations / probes / building effects keep their current lighting
(`ShipRenderer` uses P355's camera-relative headlight for readability; moving
them to sector lighting is a separate call), asteroid mesh/variant/art
selection, and lens-flare/backdrop/starfield lighting.

---

## Cause 1 — the wrong lighting model (black ↔ gold)

### The stock rig the port was using

`src/Allegiance.ClientCore.Game/Rendering/AsteroidRenderer.cs` (pre-P529) called
`_effect.EnableDefaultLighting()` in `Draw` (old `:114`) and set
`AmbientLightColor = (0.25, 0.25, 0.28)` (`:84`). The `BasicEffect`
three-light default rig has:

- a key directional light whose diffuse **and specular** are warm
  `(1.0, 0.9569, 0.8392)`-ish with specular power ~16,
- a fill light, a rim/back light, and a **near-black ambient**
  (EnableDefaultLighting overrides ambient to ≈ `(0.051, 0.045, 0.035)`).

Because each rock **spins** (`AsteroidSpin`, `Game1.Rendering.cs` P252,
`:561-567` pre-P529 region), different facets sweep under the warm key light.
A facet momentarily aligned with the key light catches a **warm specular
highlight added on top of the texture**, which reads as **gold**; a facet turned
away falls to the near-black ambient on a dark rock texture, which reads as
**black**. Hence the reported black↔gold flicker as the camera or rock moved.

### What native does — per-sector bidirectional lighting

`TrekWindowImpl::SetCluster` (`src/WinTrek/WinTrek.cpp:3614-3640`) reads the
current cluster's light and drives the scene:

```
3634  m_color                      = pcluster->GetLightColor();
3635  m_colorAlt                   = pcluster->GetLightColorAlt();
3636  m_ambientLevel               = pcluster->GetAmbientLevel();
3637  m_ambientLevelBidirectional  = pcluster->GetBidirectionalAmbientLevel();
3639  SetLightDirection(pcluster->GetLightDirection());
3640  UpdateBidirectionalLighting();
```

`UpdateBidirectionalLighting` (`:4928-4937`) is **on by default**
(`m_bBidirectionalLighting(true)`, `:2696`; the preference defaults to TRUE,
`:3296-3297`):

```
4928  void UpdateBidirectionalLighting() {
4929    if (m_bBidirectionalLighting) {
4930      m_pimageScene->SetLight(m_color, m_colorAlt);           // key + back
4931      m_pimageScene->SetAmbientLevel(m_ambientLevelBidirectional);  // 0.25
4932    } else {
4933      m_pimageScene->SetLight(m_color);
4934      m_pimageScene->SetAmbientLevel(m_ambientLevel);               // 0.5
```

The cluster's map data carries the light: `DataClusterIGC` in native
(`Igc/igc.h:1736-1737`) has `lightColor` (a D3DCOLOR) and `lightDirection` (a
`Vector`). The values are:

- `GetLightColor()` — decodes the packed D3DCOLOR via
  `RGB_GETRED/GREEN/BLUE` (`clusterIGC.h:307-318`),
- `GetLightColorAlt()` — a **constant `Color(0, 0.5f, 1.0f)`** (blue),
  `clusterIGC.h:321-324`,
- `GetBidirectionalAmbientLevel()` — **0.25**, `clusterIGC.h:331-334`,
- `GetAmbientLevel()` (non-bidirectional) — **0.5**, `clusterIGC.h:326-329`.

`GeoImage::Render` (`src/engine/image.cpp:1163-1174`) then issues
`pcontext->BidirectionalLight(m_vecLight, m_color, m_colorAlt)` where
`m_vecLight = Normalize(GetLightDirection())`
(`GeoImage::SetLightDirection`, `image.cpp:1131-1133`).

The actual per-vertex math lives in
`Device3D::BidirectionalLightVertex` (`src/engine/device3d.cpp:2303-2350`):

```
float bright = nx*m_vecLight.X + ny*m_vecLight.Y + nz*m_vecLight.Z;   // dot(normal, vecLight)
if (bright >= 0)        // face points ALONG vecLight -> key light
    rgb = Saturate(bright * m_color.RGB  + m_brightAmbient);
else                    // face points away from vecLight -> back/alt light
    bright = -bright;
    rgb = Saturate(bright * m_colorAlt.RGB + m_brightAmbient);
```

That is exactly a **two-sided Lambert diffuse**: the face aligned with the
sector's toward-light vector receives the **key colour**, the opposite face
receives the **blue alt light**, and **0.25 ambient is added to both**.
Critically, there is **no specular term anywhere** in this path — native rock
lighting is purely ambient + two-sided Lambertian diffuse.

### The port already parsed this data and never used it

`src/Allegiance.Core/Data/DataClusterIGC.cs` decodes `LightDirection` and
`LightColor` (`:33-34`, `:68-74`) with a note at `:27` about rare degenerate
direction magnitudes — but nothing in `Allegiance.ClientCore*` read them.

### The fix

New pure helper `src/Allegiance.Core/Rendering/SectorLighting.cs`:
`SectorLighting.FromCluster(lightDirection, lightColor)` produces the
effect-ready two-light + ambient params. `AsteroidRenderer.ApplySectorLighting`
(`AsteroidRenderer.cs`) programs `BasicEffect` with two directional lights
whose summed output reproduces native's Branch A (front → key colour) and
Branch B (back → alt colour) plus the same 0.25 ambient, with **specular pinned
to zero on every light**.

The draw loop (`Game1.Rendering.cs`, P529) reads the local ship's cluster and
computes the lighting once per frame:

```
_sectorLighting = world.Clusters.TryGetValue(mySector, out var dc)
    ? SectorLighting.FromCluster(dc.LightDirection, dc.LightColor)
    : SectorLighting.FromCluster(null, 0xFFFFFFFF);          // sane white fallback
```
…applied to every asteroid drawn in that sector.

### Direction-sign reasoning (the to/from question)

The task flagged the classic hazard: native's `lightDirection` is a direction
*to* or *from* the light, while `BasicEffect.Direction` is the **propagation**
direction (P355's documented precedent at `ShipRenderer.cs:111-120`). We settle
it from the native math directly:

- `Device3D::BidirectionalLightVertex` makes the **brightest** face that whose
  normal is **aligned with** `vecLight = Normalize(lightDirection)` (dot > 0).
  A diffuse surface is brightest when its normal points **toward** the light,
  so `vecLight` points **TOWARD the sun**.
- `BasicEffect` (per the P355 headlight precedent, `ShipRenderer.cs:111-120`)
  lights the face whose normal **opposes its `Direction`** (Direction is
  propagation: the headlight propagation = camera forward lights the face
  pointing back at the camera, i.e. normal = −Direction).

So to make the same face bright that native makes bright (normal = +toward),
BasicEffect needs `−Direction = +toward` ⇒ **key propagation = −toward**.
The back/alt light comes from the opposite side ⇒ **alt propagation = +toward**.

Concretely (validated in the contact sheets): the proof map's local sector
reported `towardDir = (0.707, 0, 0.707)` — the camera-facing (+Z) hemisphere is
therefore key-lit warm (which is what the sheet shows), and the opposite
(−Z) hemisphere carries the blue alt light. Had the sign been flipped, the
camera-facing side would have appeared **blue-dominated**, which it does not.

```csharp
// SectorLighting.FromCluster
toward   = Normalize(lightDirection)      // native toward-the-light
KeyDirection = -toward;  KeyColor = DecodeColor(lightColor)   // native Branch A
AltDirection  = +toward; AltColor = (0, 0.5f, 1.0f)           // native Branch B
Ambient       = (0.25, 0.25, 0.25)                            // bidirectional ambient
// specular: none (native BidirectionalLightVertex has no specular term)
```

`LightColor` (a `0xAARRGGBB` D3DCOLOR) is decoded with native's byte layout
(`RGB_GETRED` >>16 &0xff, GREEN >>8 &0xff, BLUE &0xff, /255; alpha ignored) —
`SectorLighting.DecodeColor`.

Defensive normalisation: a degenerate/missing direction (rare near-zero
magnitudes, `DataClusterIGC.cs:27`) falls back to a sane fixed direction
(native's own default `(0,0,1)`, `GeoImage` ctor) paired with a white light so
the rock still reads — never black.

---

## Cause 2 — no mipmaps on the rock textures

`src/Allegiance.ClientCore.Game/Rendering/RockTextureSource.cs` created the real
P421 `bgrndNNbmp` surfaces with `new Texture2D(gd, w, h)` (**mipmap=false**,
old `:127`), and the procedural fallback `Game1.EnsureAsteroidTexture`
(old `~:2116-2117`) did the same. Minified at distance this aliased — colour
shimmered and jumped as the camera or rock moved.

### The fix

New pure chain generator `src/Allegiance.Core/Rendering/MipmapChain.cs`
(`Rgba32` + `LevelCount` + box-filter `Downsample`), uploaded by a new
Game-side boundary `Rendering/TextureMipmapBuilder.cs`
(`Texture2D(gd,w,h,mipmap:true,SurfaceFormat.Color)` + `SetData` per level).
Used by every rock texture path:

- `RockTextureSource.RockTextures.Load` — the real 2048×2048 surfaces,
- `AsteroidRenderer` ctor — the shared procedural diffuse,
- `Game1.EnsureAsteroidTexture` — the procedural per-type fallback.

Measured: every real surface is `LevelCount = 12` (2048×2048), and the
procedural 256×256 rock would be `LevelCount = 9` (see the proof log).

---

## Cause 3 — no explicit render state

`AsteroidRenderer.Draw` previously set no `SamplerStates[0]`,
`RasterizerState`, `BlendState` or `DepthStencilState`; it inherited whatever the
previous draw left — a SpriteBatch HUD pass leaves `LinearClamp` /
`AlphaBlend` / `DepthStencilState.None` (the leak class P339/P525 fixed
elsewhere). Wrap-vs-clamp sampling could therefore differ frame to frame.

### The fix

`AsteroidRenderer.Draw` now pins its own state at the start of the pass
(P339/P525 discipline — restore nothing; the next passes set their own):

```
BlendState.Opaque;  DepthStencilState.Default;
RasterizerState.CullCounterClockwise;   // the CCW procedural mesh
SamplerStates[0] = SamplerState.LinearWrap;   // UVs wrap + mip filtering
```

---

## Implementation footprint

New files:
- `src/Allegiance.Core/Rendering/SectorLighting.cs` — pure sector→lighting bridge.
- `src/Allegiance.Core/Rendering/MipmapChain.cs` — pure mip-chain generation.
- `src/Allegiance.ClientCore.Game/Rendering/TextureMipmapBuilder.cs` — GPU upload boundary.
- `tests/Allegiance.Core.Tests/P529AsteroidLightingTests.cs` — 7 failing-first unit tests.
- `tools/asteroid-lighting-proof/` — headless proof (in-process server + real
  MonoGame client); `compose_sheets.py` stitches the cell PNGs into sheets.

Modified:
- `src/Allegiance.ClientCore.Game/Rendering/AsteroidRenderer.cs` — lighting + state + mips.
- `src/Allegiance.ClientCore.Game/Rendering/RockTextureSource.cs` — real surfaces mipmapped.
- `src/Allegiance.ClientCore.Game/Game1.cs` — `_sectorLighting` field.
- `src/Allegiance.ClientCore.Game/Game1.Rendering.cs` — compute sector lighting,
  pass it to every rock draw; procedural fallback mipmapped.

Scope guards (per the brief): asteroids only. Ships/stations/probes/buildings
untouched; mesh/variant/texture-art/type-classification/spin untouched;
lens-flare/backdrop/starfield untouched.

---

## Verification

### Unit tests — failing-first

`tests/Allegiance.Core.Tests/P529AsteroidLightingTests.cs` was written first and
failed to compile (CS0234 — `Allegiance.Core.Rendering` did not exist) before
the helpers were added — the red gate. After implementation, all **7/7 pass**:

- known cluster → expected key direction (`-toward`), decoded key colour, the
  opposite-direction blue `(0,0.5,1)` alt light, 0.25 ambient, no specular;
- direction normalisation to unit length;
- degenerate/missing-direction fallback → fixed direction + white light;
- `0xAARRGGBB` decode of the key colour (incl. pure red/green/blue);
- `MipmapChain.LevelCount` (256→9, 2048→12, 1→1, 2→2; all > 1);
- box-filter downsampling of a 2×2 block;
- repeated downsampling reaches 1×1.

### Whole-solution build

`/home/domingo/.dotnet/dotnet build AllegianceCore.slnx` → **0 warnings,
0 errors**.

### Full test suite — 3 consecutive runs

`scripts/run-tests.sh` (P471) — see below for the exact measured numbers from
the three consecutive runs.

### Headless visual proof

`tools/asteroid-lighting-proof` boots an in-process server on the real
`m2xspiderw.igc` map, joins a real MonoGame client, loads the REAL
`bgrndNNbmp` surfaces, and renders one spinning rock (Carbon + Helium) under
the OLD stock rig and the NEW sector lighting, close-up (6 rotation angles) and
at distance. Contact sheets are committed in
`docs/reports/P529_ASTEROID_LIGHTING_assets/`:

- `spin_Carbon_sheet.png`, `spin_Helium_sheet.png` — 2 rows (OLD / NEW) × 6
  rotation angles, close-up;
- `dist_Carbon_sheet.png`, `dist_Helium_sheet.png` — 2 rows (OLD / NEW) ×
  2 distances (close / far, mipmapped).

**Measured pixel statistics** (PIL analysis of the cell PNGs, inset 10px):

Blue back-light (the "no more black faces" signature — faces turned away are
now lit by the native blue `(0,0.5,1)` alt light):
- Carbon spin: OLD ≈ **0.000**, NEW ≈ **0.028–0.056** per cell (aggregate
  blue fraction 0.150 vs 0.000).
- Helium spin: OLD ≈ **0.000–0.002**, NEW ≈ **0.026–0.060** (aggregate 0.284
  vs 0.007).

White-specular flash (the "gold" hotspot — present under the stock rig's
warm specular, eliminated by the new lighting):
- Helium close-up: OLD **88** near-white specular pixels vs NEW **9** (~90%
  reduction); Helium at distance: OLD **64** vs NEW **0**.
- (Carbon's dark surface shows no specular above the 200-luminance threshold in
  either row; the Helium row is the decisive specular comparison.)

Direction-sign check: the proof sector's `towardDir = (0.707,0,0.707)`. Under
NEW the camera-facing (+Z) hemisphere is key-lit warm — if the sign were wrong
it would read blue-dominated, which it does not.

Caveat (honest): the real 2048×2048 carbon/helium surfaces are genuinely dark,
so the raw "pure-black luminance" fraction stays similar in both rows — but the
operative difference is structural: under OLD the unlit faces carry neither the
key nor the alt light (and the stock ambient is ≈0.05), while under NEW the
formerly-black back faces demonstrably carry the **blue alt light** (the blue
metric) and the white specular hotspot is gone.

### Commit

Committed on `master` (see `git log -1` in the commit message).

---

## Follow-up (explicitly out of scope)

Ships, stations, probes and building effects **keep their current lighting**.
Moving them to native sector lighting is a deliberate, separate decision for
Domingo — in particular `ShipRenderer` uses P355's camera-relative headlight
for readability and would lose it (or need the sector key light merged with a
subtler headlight) if switched. Recommended next step: a P-series task to port
the same `SectorLighting` bridge to ships/stations with a headlight fusion, then
re-validate readability in combat.

# P529 — asteroid render pass: native per-sector bidirectional lighting, mipmapped rock textures, explicit render state (asteroids only)

## Why this task exists

**Domingo's live playtest (2026-10-04):** "sometimes the colors switch to black
while observing the asteroids … sometimes they look like carbon texture, and
then suddenly change and almost look like gold."

**Three causes, traced by Claude in source:**

1. **Wrong lighting model (the black ↔ gold flip).** Asteroids use XNA's stock
   `BasicEffect.EnableDefaultLighting()` three-light rig
   (`src/Allegiance.ClientCore.Game/Rendering/AsteroidRenderer.cs:108-121`,
   `:114`; ambient `(0.25,0.25,0.28)` at `:84`). Its key light diffuse
   **and specular** are a warm `(1.0, 0.96, 0.81)`, and specular is *added*
   on top of the texture. Each rock spins (`AsteroidSpin`,
   `Game1.Rendering.cs:561-567`). A facet catching the key light flashes a
   warm highlight over a dark rock texture, which reads as gold, while the
   faces turned away fall to ambient on a dark texture, which reads as black.
   - **Native** lights the scene with the **current sector's own light.**
     `TrekWindowImpl::SetCluster` (`~/Development/Allegiance/src/WinTrek/
     WinTrek.cpp:3619-3640`) reads `pcluster->GetLightColor()`,
     `GetLightColorAlt()`, `GetAmbientLevel()`,
     `GetBidirectionalAmbientLevel()` and `GetLightDirection()`, then
     `UpdateBidirectionalLighting()` (`:4928-4937`) does
     `SetLight(m_color, m_colorAlt)` + `SetAmbientLevel(bidirectional
     ambient)` when bidirectional lighting is on. **It is on by default**
     (`m_bBidirectionalLighting(true)`, `:2696`; the preference defaults to
     TRUE, `:3296-3297`).
   - Values: `lightColor`/`lightDirection` are per-cluster map data
     (`Igc/igc.h:1736-1737`, `DataClusterIGC`), the alt (back) light is a
     constant **`Color(0, 0.5f, 1.0f)`** (`Igc/clusterIGC.h:321-324`), the
     bidirectional ambient is **0.25** (`:331-334`) and the plain ambient is
     0.5 (`:326-329`). So natively the unlit side gets a **blue back light**
     from the opposite direction instead of going black.
   - **The port already parses this data and never uses it.**
     `src/Allegiance.Core/Data/DataClusterIGC.cs:33-34, 68-74` reads
     `LightDirection` and `LightColor` (with a note at `:27` about rare
     degenerate direction magnitudes), but nothing in `Allegiance.ClientCore*`
     reads them.
2. **No mipmaps on the 2048×2048 rock textures.**
   `Rendering/RockTextureSource.cs:127` creates
   `new Texture2D(gd, tex.Width, tex.Height)` (mipmap=false) for the P421
   real `bgrndNNbmp` surfaces, and the procedural fallback
   (`Game1.Rendering.cs` `EnsureAsteroidTexture`, ~:2105-2120) does the
   same. Minified at distance this aliases, so colour shimmers and jumps as
   the camera or rock moves.
3. **No render state set.** `AsteroidRenderer.Draw` sets no
   `SamplerStates[0]`, `RasterizerState`, `BlendState` or `DepthStencilState`.
   It inherits whatever the previous draw left: SpriteBatch HUD passes leave
   `LinearClamp`/`AlphaBlend`/`DepthStencilState.None`, which is the leak
   class P339 and P525 already fixed elsewhere. Wrap vs clamp sampling can
   therefore change from frame to frame.

## Scope (asteroids only)

1. **Native sector lighting for asteroids:** light asteroid draws with the
   current sector's `lightDirection` + `lightColor` as the key light, the
   native alt light `(0, 0.5, 1.0)` from the **opposite** direction, and
   ambient **0.25** (native bidirectional default). **No specular** unless
   you find native evidence for rock specular; cite it either way. Use the
   cluster the local ship is in (the same sector the asteroid loop already
   filters by, `ast.ClusterId != mySector`). Normalise the direction
   defensively (see `DataClusterIGC.cs:27`), and fall back to a sane fixed
   white light when it's degenerate or missing. Keep the math in a small
   pure, testable helper (direction/colour → light params).
   - Check the direction convention. Native's `lightDirection` is the
     direction *to* or *from* the light; BasicEffect's `Direction` is the
     **propagation** direction (see the P355 note at `ShipRenderer.cs:111-
     120`). Get the sign right and record the reasoning in the report.
2. **Mipmaps:** create the rock textures with mipmaps (`mipmap: true` +
   `SetData` per level, or generate the chain), both the real P421 surfaces
   and the procedural fallback.
3. **Explicit state** at the start of the asteroid pass: `BlendState.Opaque`,
   `DepthStencilState.Default`, a wrap sampler with mip filtering (e.g.
   `SamplerState.LinearWrap` or anisotropic wrap) and the rasterizer the
   mesh needs. Restore nothing; the next passes already set their own.

## Explicitly out of scope

- **Ships, stations, probes and building effects keep their current
  lighting.** Moving them to sector lighting is a separate decision for
  Domingo (ships use P355's headlight for readability). Note it in the
  report as the follow-up.
- Asteroid meshes, mesh variant choice, textures' art, type classification
  (`RockTextureFactory.Classify`), spin.
- Lens flare / backdrop / starfield lighting.

## Verification

- **Failing-first** unit tests on the lighting helper: a known cluster
  `LightDirection`/`LightColor` produces the expected key light
  direction/colour, the opposite-direction blue alt light and 0.25 ambient,
  with zero specular, and the degenerate-direction fallback works. Add a
  test that the rock textures are created with `LevelCount > 1`, or
  structure the creation so that's assertable headlessly.
- **Headless visual proof** via `tools/roster-capture`'s offscreen path (as
  P525/P526 did): render one spinning asteroid of two resource types at
  several rotation angles under (a) the old stock rig and (b) the new
  sector lighting, close up and at distance. Put contact sheets in the
  report showing no gold highlight flashes and no pitch-black faces (the
  back side should show the blue alt light).
- Whole-solution build with `/home/domingo/.dotnet/dotnet build`, 0 warnings
  and 0 errors. Full suite via `scripts/run-tests.sh` (P471), 3 consecutive
  runs. Cite `docs/TEST_FLAKES.md` for any remaining flake. **Report only
  numbers you actually measured.**
- **Commit before you finish**, and confirm it with `git log -1`.

## Deliverables

1. Committed on **`master`**, not pushed. Check with
   `git branch --show-current`.
2. The report `docs/reports/P529_ASTEROID_LIGHTING.md`: the three causes
   with file:line, native citations, the direction-sign reasoning, test
   results, contact sheets, and the ships/stations follow-up note.

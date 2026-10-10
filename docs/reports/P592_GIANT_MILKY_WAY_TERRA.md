# P592 — GIANT sky objects: our own galaxy (face-on Milky Way) over TERRA, seamless on the black sky

## Summary

After the black sky (P588) and planet-style objects (P589/P589b/P589c), Domingo
asked for "a larger object that spans across half the sky but still respects
the black background," showing our own galaxy face-on in one sector. P592 puts
**our face-on Milky Way** as a **GIANT sky object over TERRA (cluster 1057)** of
the 221 map (`m2xspiderw.igc`): a **single seamless curved patch of the sky
sphere ~90° across**, drawn camera-relative at infinity, depth-off, never
far-clipped, with the object feathered to pure black well inside the patch
border so no edge/seam ever shows.

Scope highlights:
- **Geometry (no stitching):** one texture on one tessellated spherical cap
  (`GiantObjectMath.BuildPatch`). The patch's own centre sits on its local
  "equator" — the centre maps to the texture centre, UV stays in [0,1] (never a
  full dome, never wrapping), no grid lines converge to a pole (no pole
  pinching). 40 segments → 41×41 nodes, 3200 triangles.
- **Cut-out:** `tools/space-objects/process_giant.py` centres the galaxy body
  in the square and feathers it **radially to pure black well inside the patch
  border** (0 non-black pixels outside ρ=0.842). Milky Way reaches **0
  outside-envelope stars** (it is a clean artist's concept — no photographic
  field stars). Sprite is 4096×4096 straight-alpha, premultiplied at load.
- **Look:** brightness 0.82, dim enough for combat readability, with the
  artist's-concept bright core kept; placement fixed per cluster in
  `giant-objects.json`, enforced clear of the sector sun (lens flare) and the
  planet.
- **Config/toggle/credits:** `giant-objects.json` (cluster id → image, angle,
  direction, brightness) so another sector can gain a giant later;
  `--giant-objects off` disables (ON by default in the black sky); credits
  added to the Credits screen (F12).
- **Second giant — ANDROMEDA (M31) over ALDERAN (1052):** attempted with the
  ESA/Hubble heic2501a PHAST full-disk mosaic (CC BY 4.0) and **DROPPED**: the
  ultra-deep mosaic resolves Andromeda's own disk into individual stars across
  the faint halo and outer disc, indistinguishably mingled with foreground
  Milky Way stars, so reaching a clean zero-star look destroys the galaxy's
  resolved structure — the exact Sombrero failure the task warned about. Per
  the task instruction, **Alderan is left without a giant** (documented below).

Committed on `master` (not pushed).

---

## 1. Implementation

### 1a. Pure geometry — `GiantObjectMath.cs` (Core, headless-testable)

The giant is a **tessellated spherical cap**: every vertex lies on the sky
sphere at a fixed radius, so it curves seamlessly with the sky as the camera
pans (a flat billboard large enough to span ~90° would bow at its edges and
stretch). Parameterisation (the "centre on its local equator / no pole pinch"
rule): a tangent-plane square around the centre direction `c` with a local
orthonormal basis `(right, upᵥ)`; grid node `(t,s) ∈ [−1,1]²` maps to
`offset = (right·t + upᵥ·s)·tan(halfAngle)` then `dir = normalize(c + offset)`.
Because the square is *lifted* (a smooth, locally-invertible map), the centre
node lands exactly at the patch centre, no grid lines collapse into a pole fan,
and every UV stays in [0,1]. A `rotationRad` rolls the texture about the centre
(used for a future tilted disc).

Constants: `GiantObjectConfig.DefaultSpanDeg = 90` (the patch is ~90° across;
named constant, tunable in the real window). `GiantObjectRenderer.Segments = 40`.

### 1b. Renderer — `GiantObjectRenderer.cs`

Loads `giant-objects.json` + the sprite from the artwork root; on `SetCluster`
resolves the direction and rebuilds the mesh once (positions at radius 6600 on
the sky sphere, inside the sky's own far plane 80000, `NativeSkyPlacement.
SkyProjection`); `Draw` emits ONE premultiplied `DrawPrimitives(TriangleList)`
call with `LinearClamp` (no wrap), restoring blend/sampler states (the P568
"pin render states" lesson). Render states (depth-off, CullNone, projection)
come from the enclosing `BlackSkyRenderer`; no mid-frame render-target switches.

### 1c. Placement — `GiantObjectConfig.cs`

`giant-objects.json` maps cluster id → `{image, angle_deg, direction, brightness,
rot_deg, credit}`. The direction is the **nominal FIXED/seeded** placement;
`ResolveDirection` enforces the two physical rules from the task (clear of the
sector sun so the lens flare doesn't wash it out; clear of the planet) by
nudging only if the configured direction collides (default min-clear 22°). Terra
(1057): the fixed direction is ~104° from its sun and ~24° from its planet, so
it is left unchanged. Deterministic and pure (testable headlessly).

### 1d. Cut-out pipeline — `tools/space-objects/process_giant.py`

Reuses the P589c ideas (`tools/space-objects/starclean.py` helpers): a galaxy-body
envelope + a shape-based compact-point detector. It centres the galaxy body so
the disc sits in the central portion, then **radially feathers alpha to pure
black well inside the patch border** (ρ_f2 = 0.852 for the Milky Way → the patch
edge at ρ=1.0 and the corners at ρ=√2 are exactly 0). Star removal is only
applied outside the galaxy body (Milky Way needs none; it is a clean artist's
concept). Output 4096×4096 straight-alpha; the client premultiplies at load.

Verified on the sprite: **0 non-black pixels outside ρ=0.842**, 0 outside the
galaxy-body envelope, bright core preserved.

### 1e. Wiring

`BlackSkyRenderer` gains a `GiantObjectRenderer` (drawn right behind the other
paintings, before the lens flare) + `giantObjectsOn` ctor flag;
`Game1.LaunchOptions.GiantObjectsOn` (default true) flows from `--giant-objects
on|off` (Program.cs) through `Game1` to the renderer. Credits screen
(`CreditsOverlayRenderer`) credits the Milky Way (NASA/JPL public domain).

---

## 2. The Milky Way giant

- Image: **NASA/JPL-Caltech/R. Hurt (SSC/Caltech)** "Our Milky Way Gets a
  Makeover" face-on artist's concept (Spitzer), **public domain** (US Gov
  work), credit `NASA/JPL-Caltech/R. Hurt (SSC/Caltech)`. 5600×5600 (the max
  available for this artwork — no 8k+ rendition exists at any mirror).
- Sprite: `giantobj/milkyway_terra.png` (4096²), disc ~56% of the square,
  feathered to black by ρ=0.852.
- `giant-objects.json`: `cluster_id 1057 (Terra)`, `angle_deg 90`,
  `direction [0.7497, 0.6091, -0.5814]`, `brightness 0.82`.

## 3. Andromeda (Alderan) — attempted, dropped

Obtained ESA/Hubble **heic2501a** (2025, PHAT+PHAST) full-disk Andromeda mosaic,
CC BY 4.0 (credit `NASA, ESA, B. Williams (University of Washington)`),
42208×9870. Ran the strict P589c-style removal. Measured: **0 compact points on
the empty sky around the object**, but **155 residual compact points all lying
WITHIN the galaxy's own footprint** (faint halo + outer disc). These are
Andromeda's own stars resolved by the ultra-deep mosaic — indistinguishably
mixed with the foreground Milky Way population — so eliminating them destroys
the galaxy's resolved disk (damage), exactly where the Sombrero failed. Per the
task ("if M31 can't reach zero stars without damage, report it and leave
Alderan without a giant"), **Alderan has no giant**. The source is archived and
credited as dropped; the processed sprite was deleted.

---

## 4. Tests (failing-first)

- `GiantObjectMathTests` (Core, **10 tests**): grid size; **UV never wraps**
  (all u,v ∈ [0,1]); centre maps to texture centre (not a pole); **no pole
  pinch** (all nodes distinct); angular extent = 2·half-angle (edge midpoints at
  45°, corners at atan(√2·tan45°)); roll spins the texture about the centre
  without changing the footprint; Terra (1057) owns a giant, others don't; json
  parses image/angle/brightness/direction; **direction kept clear of sun &
  planet**; a clear direction is left unchanged.
- `SpaceObjectLaunchOptionTests` (MonoGame, +2): `GiantObjects_DefaultToOn`,
  `GiantObjects_CanBeDisabled`.
- **Failing-first demonstrated:** with `ResolveDirection` temporarily returning
  the raw direction (no clearance), `Direction_IsKeptClearOfSunAndPlanet` FAILS;
  restored, it passes. The geometry module is brand-new (no pre-P592 code
  existed), so its tests pin the new contract (the P588/P589 treatment of
  brand-new modules).
- Result: Core GiantObjectMathTests 10/10; MonoGame LaunchOptions 8/8.

## 5. Build

Whole solution `AllegianceCore.slnx` Debug: **0 warnings, 0 errors**.
`tools/p592-proof` builds **0/0**.

## 6. Proof (real window 2560×1440, P572 tooling)

`docs/reports/P592_assets/proof/` — 22 real frames driving the ACTUAL
`BlackSkyRenderer` (which reads `giant-objects.json` + the sprite) into a real
2560×1440 window, each scene rendered to an offscreen target (the same draw
calls the game makes to its backbuffer) and blitted to the real backbuffer so
the presented frame matches the saved frame. Capturing from the per-scene
target makes every frame independent — the one-scene-per-present capture tool
otherwise accumulates bright content across frames (p589-proof shows the same
artifact on its sunstrip; the giant's size amplifies it), so per-scene capture
is the reliable way to get truthful frames while still presenting to the real
window (the P547/P562 render-target-regression class is unaffected).

- `terra_giant_dir0..5.png` — six directional views incl. **looking at the patch
  edge**: the face-on Milky Way is centred and clean, the edge view shows a
  gradual feather (no seam/edge/stretch/banding).
- `terra_giant_pan_00..11.png` — a slow pan across the whole object: the galaxy
  sweeps edge-to-edge and back smoothly (frame mean 0 → 13 → 0, a clean bell
  curve) — the curved patch never shows a seam while panning.
- `terra_giant_border.png` + `terra_giant_border_crop300.png` — the feathered
  border, plus a 300% crop: pure black at the patch edge (0 non-black pixels in
  the outer 10% bands / corners), the border is never visible.
- `terra_giant_readability.png` — dark ship silhouettes in front of the giant:
  ships stay readable against the galaxy (readability check).
- `terra_giant_off.png` — `--giant-objects off`: the giant is gone from Terra's
  sky (visual confirmation of the toggle).
- `alderan_no_giant.png` — Alderan's sky has no giant (Andromeda dropped).
- **Frametime (real backbuffer path):** `BlackSkyRenderer.Draw` avg **0.258 ms**,
  max **5.182 ms** over 30 warm steady-state frames (the giant's single mesh draw
  is negligible against the existing sky; cf. P589's ~2.7–3.5 ms).

## 7. Suite (2 consecutive runs on the final code, P471 runner)

Runs were executed one after another on the committed code and reported as they
happened (not re-run to force a clean pass). Every failure is a documented
rotating live-wire timing flake in `docs/TEST_FLAKES.md` (the same families the
P589c report cited: `AddPartTests` line 247, `AutoReloadOnEmpty` RELOAD129 line
147, `Conquest…leak` 29/267, `GameOverPlayers` Win_by_elimination 30/206, plus
`ThreePeerSeededSession`, `RetriedConnectLive`, `FullSessionKill`,
`TcpLoopbackProof`). **None involve P592 code** — the P592 tests
(`GiantObjectMathTests` Core 10/10, `SpaceObjectLaunchOptionTests` MonoGame 8/8)
were green in every run.

| Run | Lobby | MonoGame | ClientCore | Core |
|-----|-------|----------|------------|------|
| 1 | 12/12 | 1691/1691 | 577/579 (2 flakes) | 3867/3871 (4 flakes) |
| 2 | 12/12 | 1691/1691 | 578/579 (1 flake)  | 3869/3871 (2 flakes) |

## 8. Files

New:
- `src/Allegiance.Core/Rendering/GiantObjectMath.cs` — pure patch geometry.
- `src/Allegiance.Core/Rendering/GiantObjectConfig.cs` — config parse + resolve.
- `src/Allegiance.ClientCore.Game/Rendering/GiantObjectRenderer.cs` — renderer.
- `tests/Allegiance.Core.Tests/GiantObjectMathTests.cs` — 10 tests.
- `tools/space-objects/process_giant.py` — giant cut-out pipeline.
- `tools/p592-proof/` — real-window proof tool.
- `docs/reports/P592_assets/` — `giant-objects.json`, `giant-objects-sources.json`,
  `licenses/`, `sources/`, `sprites/`, `proof/`.
- `/home/domingo/Development/Artwork/giant-objects.json` + `giantobj/` (live assets).

Modified:
- `src/Allegiance.ClientCore.Game/Rendering/BlackSkyRenderer.cs`,
  `Game1.Lifecycle.cs`, `Game1.Session.cs`, `Game1.cs`,
  `Rendering/CreditsOverlayRenderer.cs` (credits),
  `src/Allegiance.ClientCore.MonoGame/Program.cs` (`--giant-objects`),
  `tests/Allegiance.ClientCore.MonoGame.Tests/SpaceObjectLaunchOptionTests.cs`.

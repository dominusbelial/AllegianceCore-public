# WARP BLUISH PALETTE + REAL TRANSLUCENCY — P242

Stage: P242. Master top commit before this stage: `4a078e8` (P241, cockpit HUD).
Landed on `master` (not pushed — Domingo pushes).

## Relationship to prior stages
- P227 built the procedural swirling-vortex warp renderer
  (`Rendering/WarpMeshFactory.cs` geometry + `Rendering/WarpRenderer.cs`), using
  a warm brown/amber palette sourced from native's tutorial dialogue
  (`training/mission4.cpp:494` "a big swirling brown vortex"). Geometry,
  swirl, and the dark-rim → bright-center gradient structure are P227's work and
  are UNCHANGED here.
- P237 added the visual scale multiplier (now aliased to `Sector.WarpVisualScale`
  = 9.5) and the warp ambient sound. Unchanged.
- P238 promoted the scale constant to a single shared source of truth and
  widened the real transit/warp-bomb trigger radii. Unchanged (this stage does
  not touch `Sector.cs`).

This stage touches exactly three files: `WarpMeshFactory.cs` (color stops),
`WarpRenderer.cs` (blend/render state + restore), and the mesh unit tests.

## What was wrong and the source of truth
Two independently-confirmed defects from live testing:

1. **Wrong hue.** P227's only color lead at scoping time was the tutorial text
   ("swirling BROWN vortex"), and it turned out not to match the real rendered
   aleph. Domingo corrected this directly from his memory of the original game:
   **"it's bluish, not brown."** Native's own aleph texture is unrecoverable
   from this checkout (the same two-tree situation ship models and asteroids
   were already in for P218 — `warpIGC.cpp:33` `LoadWarp(textureName)` names an
   asset that is not present in the artwork tree), so Domingo's memory is the
   source of truth for the hue. The task brief explicitly directs this: take
   Domingo's memory, not the tutorial text.

2. **No translucency.** `WarpRenderer.cs` had NO blend state configured at all —
   it drew with the device default (`BlendState.Opaque`), so the vortex rendered
   fully opaque. A warp/portal energy effect should read as translucent glowing
   light.

## Changes

### 1. Bluish palette — `Rendering/WarpMeshFactory.cs`
Replaced the warm stops (dark brown `(0.033,0.025,0.015)` → amber → white-hot)
with a cool palette, keeping the exact "dark rim → bright center" structure:

```
Rim (t=0):   deep navy   R=0.041  G=0.076  B=0.324   (dark, blue-dominant)
Mid (t=0.6): mid-tone blue R=0.099 G=0.374 B=1.000  (blue peaks here)
Tip (t=1):   white-hot / pale-cyan core  R=1.0 G=0.977 B=1.0
MidT = 0.6 (unchanged)
```

The `ColorAt` lerp and the per-vertex `SpiralArms` brightness wedges (which
scale R,G,B together, preserving hue ratio) are untouched — only the three RGB
stop values changed. Mesh alpha stays 1.0 (opaque) because the translucency is
delivered by the renderer's additive blend, which needs full srcAlpha to add
the vertex colour at full strength.

### 2. Additive blend + depth-read + restore — `Rendering/WarpRenderer.cs`
The draw now configures three render states, then restores all three:

- `BlendState` = additive (`ColorSourceBlend=SourceAlpha, ColorDestinationBlend=One`,
  both blend functions Add) — the same equation `MissileTrailRenderer` uses for
  its soft exhaust sprites.
- `DepthStencilState = DepthStencilState.DepthRead` — the warp still tests depth
  against the opaque geometry drawn before it (asteroids/ships write real depth,
  so a solid object in front correctly occludes the warp), but it does NOT write
  depth, so the warp's own rotating/swirled layers never self-occlude or z-fight
  as they spin.
- `RasterizerState = _cullNone` (unchanged, P227 — hollow funnel shows inner wall).

After the draw, all three are restored to the device defaults
(`BlendState.Opaque`, `DepthStencilState.Default`, `RasterizerState.CullCounterClockwise`)
so the additive/depth-read/cull-none state does not leak into the opaque
projectile/missile passes and the HUD sprite batch that run after this in
`Game1.Draw()`. This mirrors `MissileTrailerRenderer`'s "restore the caller's
expectations" discipline (it restores `BlendState` after its own additive draw);
we restore all three states we touched so the frame is exactly as we found it.

## Blend choice: additive (not alpha-blended), and why
Chosen **additive** as the translucency model. Rationale:

- It is the canonical real-time technique for glowing portal/vortex energy.
  With additive, the funnel's self-lit layers ACCUMULATE where they overlap, so
  the drained core of the vortex naturally burns hotter without one ring shell
  occluding the one behind it — exactly the "bright spot in the center" drain
  look. Alpha blending (SrcAlpha/InvSrcAlpha with a translucent mesh) instead
  composites shells semi-opaquely, softening and washing out that energetic core.
- Additive pairs with the bluish palette: blue + additive accumulation reads as a
  luminous cyan-white glow.
- Crucially for the verification requirement, additive preserves whatever is
  BEHIND the warp (it brightens it rather than painting over it), so background
  stars/objects stay at least partially visible through it — demonstrated live
  (below). An alpha-blended translucent mesh can also show through, but additive
  is the better artistic fit for an energy field and is what reads best once
  rendered (confirmed live).

## Scope boundary / exclusions
- Geometry (funnel rings, taper, helix twist), rotation, swirl speed: P227,
  untouched. `SwirlSpeed`, `Default*` params, `SpiralArms`, `ArmAmplitude`,
  `DefaultDepth`, `DefaultTaper`, `DefaultSpiralTightness` — all unchanged.
- Real-world scale (`Sector.WarpVisualScale` / `WarpRenderer.VisualScaleMultiplier`,
  P237/P238) — untouched; this stage never touches `Sector.cs`.
- Warp ambient sound (P237) — untouched.
- Native reference source (`~/Development/Allegiance`) — read-only, never modified.

## Verification

### Tests (pure-logic, headless — MonoGame.Tests)
Baseline 241 (P241) → **242**. The new
`WarpMeshFactoryTests.ColorPalette_IsBluish_NotBrown` pins the hue correction:
mid ring blue dominates red by a wide margin (would FAIL on the old amber where
midR≈0.96 crushed midB≈0.07), the rim is a real navy (blue > 2× red), and the
tip stays near-white (bright center retained). The existing
`ColorGradient_DarkRimToWhiteHotTip` still passes (dark rim / bright tip
structure preserved) and was re-worded to the navy palette.

Must-fail proven: temporarily restoring the old amber stops made
`ColorPalette_IsBluish_NotBrown` redden (`rim must be navy (R 0.924, B 0.020)`)
→ the test genuinely discriminates the correction, then reverted (file restored,
suite green again).

### Live screenshots (offscreen RenderTarget2D, the established P207/P218/
P234/P235 technique) — `/.verify_p242/` (throwaway MiniGame harness in /tmp,
referencing the real MonoGame project; real `WarpRenderer` + committed mesh).
1. `warp_oblique_blue_01.png` / `_02.png` — oblique views of the real warp; both
   read BLUE/navy/cyan with a pale-cyan core ("no brown or amber present").
   Numeric: warp-only mean RGB over the silhouette = **(21, 68, 189)** → strongly
   blue-dominant. (The two frames a moment apart also confirm the constant swirl
   still animates under the new blend.)
2. `tb_warp_additive_over_bg.png` — the warp drawn additively over a warm ORANGE
   disc sitting behind it: the orange clearly shows THROUGH the translucent blue
   funnel. Numeric (97,380 warp∩disc pixels): background-only red **255** is fully
   retained in the additive composite (**255**, i.e. the background is still there,
   just brightened + blue-washed) — genuine see-through.
3. `tb_warp_opaque_over_bg.png` — SAME mesh + SAME orange disc but drawn with the
   Opaque blend (emulating P227's no-blend behavior): the red is crushed to the
   warp's blue (**21**), i.e. an opaque mesh fully buries the background. This is
   the direct before/after for translucency (plus P227's own brown `/.verify_p237/`
   captures serve as the hue "before").

These prove both required points: (a) the palette reads blue, not brown; (b) the
vortex is genuinely translucent — a background object remains partially visible
through it, unlike the opaque P227 render.

### Build / suites
- Whole-solution build (`AllegianceCore.slnx`): **0 warnings, 0 errors.**
- Full `Allegiance.Core.Tests` run 3× (each): 3064 passed / 3065 total; the only
  failure every run is the pre-existing machine-state
  `LiveTurretGunnerBindingTests.Board_binds_gunner_and_active_turret_update_roundtrips_live`
  flake (persists on the pristine tree; references none of this stage's symbols;
  baseline count unchanged at 3065). No Core code changed.
- `Allegiance.ClientCore.Tests`: 83/83 passed.
- `Allegiance.ClientCore.MonoGame.Tests`: **242/242 passed** (241 → 242).

## Citations
- `training/mission4.cpp:494` — native tutorial "swirling brown vortex" (the lead
  P227 used; superseded by Domingo's direct memory for the hue).
- `warpIGC.cpp:33` — `LoadWarp(textureName)`: native aleph texture asset absent
  from this checkout → procedural mesh stays; hue taken from Domingo, not texture.
- P218/P227 skill notes: artwork/two-tree absence of native visual assets;
  P234/P235 offscreen-RT synchronous-in-LoadContent capture pattern.
- `MissileTrailerRenderer.cs` (`_additiveSoft` BlendState + restore-after-draw) —
  the in-tree precedent for additive glow + state restoration this stage follows.

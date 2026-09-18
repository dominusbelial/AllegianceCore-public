# Correct Asteroid Rock Normals to Match the Bumpy Shape (P251)

## What this is

A single, surgical fix to `RockMeshFactory.Generate()`
(`src/Allegiance.ClientCore.MonoGame/Rendering/RockMeshFactory.cs`) so each
asteroid-rock vertex's **lighting normal** reflects the **actual displaced
bumpy geometry** instead of the pre-displacement unit-sphere direction. It
makes the per-vertex lighting in `AsteroidRenderer`'s `BasicEffect`
(`EnableDefaultLighting`) shade against the real lumps — protrusions catch
light, dents fall into shadow — rather than against a phantom smooth sphere,
which is what produced Domingo's "rocks don't look solid / change shape as you
move around them" symptom in the live field.

Scope discipline honoured: positions, the displacement algorithm, the
icosphere subdivision and `RockMesh`'s public shape are **untouched**; only the
`Normals` array is corrected. Nothing in `AsteroidRenderer.cs`, `Game1.cs` or
the draw/lighting setup changed.

## Root cause (confirmed, precisely)

`Generate()` builds the rough rock by radially displacing every icosphere
vertex by a deterministic per-vertex hash:

```
dirX/dirY/dirZ = normalized pre-displacement radial direction of the vertex
scale          = 1 + (t − 0.5)·2·displacement   (t ∈ [0,1) from the hash)
positions[x]   = dirX · scale   …   (purely radial, on the vertex's original ray)
normals[x]     = dirX   …   ← the BUG: never recomputed from the bumped surface
```

Because the displaced position is on the same radial ray, the old `normals =
dirX` equalled each vertex's own **radial unit vector** — i.e. the normal field
of a *perfect sphere*, regardless of how far each vertex was pushed in or out.
`BasicEffect`'s default per-vertex lighting shades with exactly those normals,
so the shading stayed a smooth-sphere gradient that ignored the actual bumpy
shell. Rotating around the rock the highlights/shadows slid as if it were a
smooth ball while the silhouette wobbled — the "changes shape" read.

## Technique chosen: smooth face-normal averaging (and why)

Used the standard, mesh-faithful approach the brief specified:

1. After displacement, walk every triangle in `Indices`.
2. Take the cross product of two of its (now-bumped) edges → that face's true
   geometric normal.
3. Orient it **outward** (away from the rock centre, via a dot with the face
   centroid) so a triangle's winding can never flip the lit side.
4. Accumulate each face normal into all three of its vertices, then
   renormalize per vertex.

The icosphere already shares vertices across adjacent faces through the
`MidPoint`/unordered-edge-cache during subdivision, so summing face normals per
vertex yields the classic smooth (Gouraud) surface normal at every shared
vertex — the correct analytic normal of the displaced shell.

Why this over alternatives: computing true per-facet (flat) normals would
produce a hard faceted look that the mesh's shared-vertex topology argues
against; the pre-displacement radial (the bug) is plainly wrong. Face-normal
averaging is the one technique that uses *only* the actual displaced vertices
(no assumptions), is fully deterministic (pure float/int arithmetic, no
`System.Random`, no time), and preserves the smooth-shaded aesthetic the
renderer already presents. Degenerate slivers are skipped; an (unreachable)
unreferenced vertex falls back to its radial ray. Positions/indices are bit-
identical to before.

Determinism contract preserved: the fix uses only `HashUnit` (already
deterministic) + the displaced `positions` + `indices`, so the same seed always
yields byte-identical `Normals` — the existing `SameSeed_TwoCalls_ByteIdentical`
test still passes.

## Tests (must-fail-if-reverted proven)

Added two pure tests to `tests/Allegiance.ClientCore.MonoGame.Tests/RockMeshFactoryTests.cs`:

- **`ProtrudingVertexNormal_IsNotItsOwnRadialDirection`** (the must-fail
  discriminator): finds the most-outward (max-radius) vertex — a prominent
  protrusion — and asserts its surface normal deviates > 5° from its own unit
  radial direction. At default params this is ~17°. The pre-P251 code set that
  normal to exactly the radial (0°), so the test **reds when the fix is
  reverted** — verified live: forcing all normals back to radial reproduced the
  failure ("got 0.0°"), then green after restoring the fix.
- **`AllNormals_AreUnitLengthAndPointOutward`**: sanity that every normal is a
  unit vector and faces away from the rock centre (never lights the interior).
  All 162 default vertices satisfy both.

Existing 6 `RockMeshFactoryTests` (real mesh, in-range indices, byte-identical
determinism, seed sensitivity, subdivision growth, displaced-shell radii) all
still pass — confirming positions/shape were not altered.

## Live proof (`.verify_p251/`)

`/tmp/p251verify` MiniGame harness (the P207/P218/P234/P240 offscreen-screenshot
technique, rendered synchronously in `LoadContent` to `RenderTarget2D`s) renders
the real mesh with real per-vertex lighting **twice per camera** — a "fixed"
mesh straight from `RockMeshFactory` and a "buggy" twin with **identical
positions/indices but radial normals** (the pre-P251 behaviour) — so any shading
difference is exactly the normals fix. Metrics computed from the saved PNGs:

| frame | corrected | buggy | corrected/buggy |
|---|---|---|---|
| `light_side` (game default 0.22, one light) strong-edge density | 0.149 | 0.046 | **3.2×** |
| `craggy_a0` (0.45, one light) strong-edge density | 0.168 | 0.033 | **5.2×** |
| `craggy_a1` (0.45, one light) strong-edge density | 0.241 | 0.029 | **8.3×** |
| `craggy_a2` (0.45, one light, side-on) strong-edge density | 0.205 | 0.079 | **2.6×** |

"Strong-edge density" = fraction of interior surface pixels at a strong shading
transition. A smooth sphere under one directional light has essentially **zero**
of these (single slow cosine ramp); the corrected normals produce many localized
bright/dark facet transitions (protrusions lit, adjacent dents shadowed) — i.e.
shading that demonstrably tracks the bumpy surface from **two-plus camera
angles** (`a0`, `a1`, `a2`).

Vision cross-check (direct quotes summarised):
- **Corrected** (`craggy_a2_fixed`): "shading is entirely faceted and
  geometric… each individual polygon is a distinct patch of brightness…
  protrusions catch light as bright facets while notches and back-facing
  surfaces drop into shadow."
- **Buggy** (`craggy_a2_buggy`): "a smooth, even gradient like a billiard
  ball… the geometry is bumpy, but the shading hides that bumpiness" — exactly
  the pre-fix symptom.

The `client_*` frames render through the REAL `AsteroidRenderer` (the in-game
path, three default lights). There the perceptual gain is naturally gentler —
the game's 0.22 displacement is mild and three soft lights flatten contrast —
but the normals are now *mathematically* correct (unit test pins it), and under
directional light (day/night terminator) the corrected shading measurably and
visibly hugs the bumps.

## Build / test

- Whole-solution build (`AllegianceCore.slnx -c Debug`): **0 warnings, 0 errors.**
- `Allegiance.ClientCore.MonoGame.Tests`: **269 pass** (baseline 267 + 2 new).
- `Allegiance.ClientCore.Tests`: unchanged.
- `Allegiance.Core.Tests`: unchanged (this stage is client-only, zero
  Core/Server references) — full suite run confirmed; only the pre-existing
  documented `LiveTurretGunnerBindingTests` timing flake expected.

## Files changed

- `src/Allegiance.ClientCore.MonoGame/Rendering/RockMeshFactory.cs` — replace
  the normal assignment with smooth face-normal averaging over the displaced
  geometry.
- `tests/Allegiance.ClientCore.MonoGame.Tests/RockMeshFactoryTests.cs` — +2
  tests (the must-fail protrusion test + unit/outward sanity).

## Out of scope (explicitly NOT built)

- **Collision** — asteroids are damage targets only in both native and this
  port (never a ship-navigation obstacle); researched separately, confirmed not
  a bug, not added.
- **Texture** (P253), **rotation** (P252) — separate tasks, none built here.
- Any change to `AsteroidRenderer.cs`, `Game1.cs` draw/lighting, or `RockColor`.

No native source (`~/Development/Allegiance`) was modified (read-only).

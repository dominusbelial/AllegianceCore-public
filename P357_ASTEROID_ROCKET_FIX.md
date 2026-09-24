# P357 — Remove the wrong non-rock mesh (`rrocket`) from the asteroid variety pool

## Status

Landed on `master` (not pushed — Domingo pushes). Confirmed live playtest bug fixed,
with a genuine 3rd native rock restored for variety.

## Root cause (confirmed, not assumed)

`Game1.cs` configured the asteroid visual-variety pool with
`["Models/drgrock", "Models/rrocket", "Models/tacrock"]`. `rrocket` is **not a rock**:

- Rendered in Blender, `rrocket.fbx` is a blocky, mechanical, panel-and-spike
  silhouette — nothing like the genuine lumpy `drgrock`/`tacrock` boulders.
- The native source `~/Development/Artwork/rrocket.mdl` decodes (via the project's
  `mdl_parse.py`) with **5 named hardpoints `thrust`, `rthrust`, `lthrust`,
  `smoke`, `trail`** — real engine/projectile effect attachment points that no rock
  has. `rrocket` = **R-ROCKET**, a guided rocket/projectile model, included in the
  rock pool by a filename-substring "rock" match (`r-ROCK-et`).
- Because `ImportedRockMeshLoader` always discards the imported mesh's texture in
  favour of the procedural `RockTextureFactory` gray-brown diffuse, the wrong rocket
  silhouette still wore convincing rock coloring — which is why it looked
  "asteroid-textured" yet ship-shaped.

Also confirmed: the real wire `ModelName` carried by asteroids in the shipped mission
is not per-rock — decoding `m2xspiderw.igc` shows every asteroid's `ModelName` is a
generic `bgrnd03`/`bgrnd05`/`bgrnd56`, shared across many rocks. So the P286 decision
to choose the pool mesh cosmetically (by stable `AsteroidId`) stands; the fix is purely
to the pool contents.

## Was a real 3rd variant found? YES — `bgrnd05`

Searched the native `Artwork` corpus thoroughly (not just the "rock" substring):

- The other `*rockbmp.mdl` files (`carbonrockbmp`, `copperrockbmp`, `goldrockbmp`,
  `heliumrockbmp`, `hotrockbmp`, `waterrockbmp`) are **texture-only proxies** —
  `mdl_parse.py` reports zero meshes/exports. Not usable as rock geometry (matches
  P286's own note).
- The genuine native asteroid rock models are the `bgrndNN.mdl` background-rock
  meshes — exactly the family the wire `ModelName` actually references. Checked
  against the native `asteroidigc.cpp` → `m_asteroidDef.modelName` → `Load(...)`
  path, which is what the shipped maps populate with `bgrnd03/05/56`.
- Notable: `drgrock.mdl` ≡ `bgrnd03.mdl` and `tacrock.mdl` ≡ `bgrnd51.mdl` (identical
  decoded OBJ md5) — the two existing "good" rocks are themselves `bgrnd`-family
  members. So replacing the rocket with another `bgrnd` restores genuine, native,
  distinct 3-way variety.
- Candidate geometries that are genuinely **distinct** from both existing rocks:
  `bgrnd05` (74v/80t), `bgrnd56` (68v/96t), etc. I rendered `bgrnd05`, `bgrnd56`,
  `bgrnd57` in Blender via the established `mdl_parse.py → OBJ → Blender FBX` path.
  `bgrnd05` and `bgrnd56` are confirmed **natural irregular rock/boulder** shapes;
  `bgrnd57` failed to frame (dropped).

**Added `Models/bgrnd05` as the pool's 3rd variant** — it is (a) a real native rock,
(b) genuinely distinct in silhouette from both drgrock and tacrock, and (c) one of the
three `bgrnd` models the live mission actually uses (`bgrnd03/05/56`), i.e. maximally
"native". Added via the project's established pipeline (`mdl_parse.py` native decode →
headless-Blender FBX, material-stripped, UVs+normals kept, triangulated), wired into
`Content.mgcb`, `meshAssets`, and the `MeshVariantFor` spread.

**Detail note (honest):** `drgrock.fbx`/`tacrock.fbx` are ~12k-tri Meshy-regenerated
versions of their native ~80–190-tri rocks; `bgrnd05.fbx` is the authentic native
80-tri geometry. Shape/variety are genuine; the one difference is polygon density. If
Domingo wants `bgrnd05` Meshy-upscaled to match the ~12k detail of the other two, that
is a follow-up needing the `MESHY_API_KEY` (not available in this session). The task's
bar — a real rock replacing a fake one — is met either way.

## The static-vs-"floating" question — RESOLVED as perceptual, with evidence

**Conclusion: it was a perceptual effect of the asymmetric rocket silhouette, not a
physics difference.** Asteroids are unambiguously static server-side.

Evidence (code citations):
- `Sector.ResolveShipAsteroidCollisions` (`Sector.cs:1943-1948`) registers every
  asteroid collider as
  `new Collider(ast, new Vector3(ap[0],ap[1],ap[2]), Vector3.Zero, Orientation.Identity,
  hull: null, ast.Radius, isStatic: true, HitShape.Sphere)` — **zero velocity,
  `isStatic: true`**, position taken straight from `ast.Data.Position` (set once at
  `AttachAsteroid`, never re-written). Line 1970 comment: "The ship's position at the
  moment of collision (the rock is static)."
- The mining pass builds the same `isStatic: true` sphere collider (`Sector.cs:1540`).
- Server-side the asteroid's `Update` only regenerates ore; it never translates.
  (Native parity: `CasteroidIGC` is a `TmodelIGC` registered with `c_mtStatic`, and
  asteroidigc.cpp sets the model with static-type flags.)

Why it nevertheless *looked* like it "floated freely": the client applies the **same**
per-asteroid spin animation to every rock (P252 `AsteroidSpin`, seeded from the wire
axis+rate, accumulated in `Game1.Draw` for all asteroids regardless of variant). A
near-spherical boulder rotating reads as "fixed and turning in place"; an **elongated,
asymmetric rocket** with fins/spikes rotating about its body axis reads to the eye as
drifting/tumbling free in space — even though its position is pinned. No per-mesh
translation exists anywhere; the fix (removing the rocket silhouette) removes the
perceptual cue at its source.

## Verification (real output, not just build/test)

**Live proof via the real content pipeline** (`/.verify_p357/harness`, offscreen
RenderTarget2D harness referencing the built MonoGame content and the real
`AsteroidRenderer`/`ImportedRockMeshLoader`/`RockTextureFactory`):

- Content diagnostics (real `.xnb` through the loader):
  `drgrock: 9974 verts / 11948 tris; tacrock: 9056 verts / 12000 tris;
  bgrnd05: 74 verts / 80 tris`.
- `variants.png` — the three pool meshes side by side, each drawn by the real
  `AsteroidRenderer` with its real per-type rock diffuse: **all three are natural
  irregular rocks** (drgrock elongated/oval, tacrock bulky rounded boulder, bgrnd05
  lumpy rounded blob), none ship/rocket-shaped, and all three are clearly distinct —
  **real 3-way variety restored**.
- `field.png` — the real `m2xspiderw` Alderan field (134 asteroids) assigned variants
  via `MeshVariantFor(AsteroidId)`: variant counts `[44, 44, 46]` across the three
  rocks (healthy spread), and every rendered body reads as a natural rock — **no
  ship-shaped silhouette remains**.

**Whole-solution build** (`AllegianceCore.slnx -c Debug`): **0 warnings, 0 errors.**
**Tests:** `Allegiance.ClientCore.MonoGame.Tests` (incl. `ImportedRockMeshLoaderTests`
5/5, covering the 3-variant `MeshVariantFor` spread) and the full
`Allegiance.Core.Tests` suite — run twice; only the pre-existing documented live-wire
timing flakes appear, none referencing any symbol P357 touched (P357 is entirely
client-side content).

## Scope honored

- Only the asteroid **content pool** changed: `rrocket` removed from `meshAssets`,
  `bgrnd05` added; `Content.mgcb` entry swapped; comments/docstrings updated.
- **Nothing else** in the rendering pipeline was touched (texture factory, procedural
  path, mesh-loading mechanics all unchanged — exactly the "content-pool fix" scope).
- P354/P355/P356 untouched.
- **`rrocket.mdl`/`rrocket.fbx` are NOT deleted** — the asset remains in
  `Content/Models/rrocket.fbx` (and native `rrocket.mdl`) for any legitimate future
  use as the rocket/projectile it actually is; it is only removed from the asteroid
  pool. `Content.mgcb` no longer builds it into the client content (nothing loads it).

## Files changed

- `src/Allegiance.ClientCore.MonoGame/Game1.cs` — `meshAssets` pool
  (`drgrock`/`tacrock`/`bgrnd05`) + explanatory comment.
- `src/Allegiance.ClientCore.MonoGame/Content/Models/bgrnd05.fbx` — new asset (native
  `bgrnd05.mdl` via `mdl_parse.py` → Blender FBX).
- `src/Allegiance.ClientCore.MonoGame/Content/Content.mgcb` — `bgrnd05` entry replaces
  the `rrocket` entry.
- `src/Allegiance.ClientCore.MonoGame/Rendering/AsteroidRenderer.cs` — `MeshVariantFor`
  docstring now names `drgrock/tacrock/bgrnd05` (logic unchanged, still `mod 3`).
- `src/Allegiance.ClientCore.MonoGame/Rendering/ImportedRockMeshLoader.cs` — docstring
  names the three genuine rocks.

Evidence outputs live in `/.verify_p357/out/` (`variants.png`, `field.png`) and the
harness in `/.verify_p357/harness/`.

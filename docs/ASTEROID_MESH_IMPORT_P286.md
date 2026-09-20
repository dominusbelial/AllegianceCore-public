# Import Meshy asteroid meshes with per-type colour (P286)

## What this is

Previously every asteroid rendered as one fully-procedural rock shape
(`RockMeshFactory.Generate`) wearing one shared gray-brown diffuse
(`RockTextureFactory`). This stage:

1. **Imports three real Meshy asteroid meshes** — `drgrock` / `rrocket` /
   `tacrock` (the port's three actual distinct asteroid shapes; the other
   `*rockbmp.mdl` files are texture-only skins of these same shapes) —
   through the MonoGame content pipeline exactly like the ship model.
2. **Keeps the procedural diffuse**: the Meshy baked (flat gray) texture is
   discarded; `RockTextureFactory`'s deterministic, seeded rock diffuse is
   applied to the imported geometry instead.
3. **Adds real per-type colour**: each asteroid's diffuse is tinted by its real
   `AabmCapabilities` resource type, so a sector shows genuinely varied,
   resource-coloured rocks.

Scope discipline: `RockMeshFactory`'s procedural path is untouched (kept as a
fallback variant); missiles (`mis05`/`mis400`/`artifact`) are out of scope;
native reference source (`~/Development/Allegiance`) untouched.

## Content-pipeline format/setup (why FBX)

Followed the existing ship-model precedent exactly: `Content.mgcb` registers each
mesh with `FbxImporter` + `ModelProcessor` (same params as
`Models/bios_scout_upscaled.fbx`, `GenerateTangentFrames=False`,
`Scale=1`), loaded via `Content.Load<Model>("Models/drgrock")` etc. The three
assets were downloaded as `.glb`; a headless Blender (5.2.2) pass converted each
to FBX (binary 7.4, `axis_forward=-Z, axis_up=Y`, material stripped, UVs +
normals kept, triangulated). Build compiles all three to `.xnb` with 0 warnings.

Asset-prep note: `rrocket.fbx` came out at ~485k triangles (far too dense to
render 134 rocks per sector in real time), so each mesh was decimated
(planar collapse) to ~12k triangles — well above low-poly, silhouettes
preserved. This is a preprocessing necessity, not a regeneration; flagged in
`REWORK_LIST.md`.

## UVs map cleanly (verified, not assumed)

`ImportedRockMeshLoader.FromModel` extracts the processor-produced
`VertexPositionNormalTexture` (stride 32: Position@0, Normal@12, TexCoord@24)
and hands it to the pure `Finalize` stage: centre on origin, normalize to unit
extent (so the renderer's `CreateScale(ast.Radius)` keeps world size), recompute
smooth outward normals (the P251 discipline — imported normals discarded), and
fix the Meshy/FBX back-facing winding so normal back-face culling works (the
quirk that forces `ShipRenderer` to `CullNone`).

Diagnostics (compiled assets, real content pipeline): all three meshes land at
max-radius 1.000, UVs entirely within [0,1] (u/v min/max swept the square, zero
out-of-range), all normals unit length, zero inward normals. No UV stretching or
overflow — the equirectangular sampling assumption holds.

## Which mesh an asteroid uses

`AsteroidRenderer.MeshVariantFor(ast.AsteroidId)` → 0..2 selects
drgrock/rrocket/tacrock from the asteroid's stable `AsteroidId`, guaranteeing a
healthy silhouette spread in a sector (the wire `ModelName` is unreliable — the
corpus ships many asteroids sharing one `bgrndNN` name). Index 3 is the
procedural `RockMeshFactory` rock as a fallback.

## Real per-type colour

Measured across **all 214 shipped maps (973 asteroids)**, the real
`AabmCapabilities` values are: 0x00 plain (147), 0x01 helium (71), 0x04 gold
(11), 0x08 buildable (728), 0x10 uranium (7), 0x20 silicon (5), 0x40 carbon
(4). `RockTextureFactory.Classify(short)` maps each to an
`AsteroidResourceType`; `PaletteShift(type)` multiplies the base gray-brown tone
(Plain = identity, byte-identical to P253). Results:

| type | tint |
|---|---|
| Plain | original gray-brown |
| Helium (0x01/0x02) | icy blue-gray |
| Gold (0x04) | warm gold |
| Uranium (0x10) | toxic green |
| Silicon (0x20) | pale crystalline gray |
| Carbon (0x40) | dark charcoal |
| Buildable (0x08) | muted rust |

Determinism/discipline preserved: same seed + type → byte-identical pixels;
`Plain` reproduces the original P253 rock exactly; existing
`RockTextureFactoryTests` still pass unchanged. `Game1` caches one texture per
type (`EnsureAsteroidTexture`) — never per rock.

## Live proof (`/tmp/p286verify/.verify_p286/`, offscreen technique)

Real m2xspiderw Alderan (cluster 1052: 9 helium + 125 buildable rocks) rendered
through the REAL renderers + REAL per-type textures + REAL Game1 world
composition to offscreen `RenderTarget2D`s:

- `sector_overview.png` — a real field of ~18 Alderan rocks: solid faceted
  low-poly asteroids, **varied shapes** (rounded / elongated / lumpy) and a
  distinct **cool helium rock among the warm buildable ones**. No hollow faces,
  no stretched UV seams.
- `shapes_closeup.png` — one of each imported mesh side by side: three clearly
  different silhouettes.
- `colors_pair.png` — the same mesh (drgrock) tinted helium (cool blue-gray)
  vs buildable (warm brown): unambiguous per-type colour.
- `texture_sheet.png` — all 7 per-type diffuses: distinctly different mottled
  stone palettes.

## Build / test

- Whole-solution build (`AllegianceCore.slnx -c Debug`): **0 warnings, 0 errors.**
- `Allegiance.ClientCore.MonoGame.Tests`: **466 pass** (incl. new per-type
  texture + `ImportedRockMeshLoader.Finalize` + `MeshVariantFor` tests).
- `Allegiance.ClientCore.Tests` (127), `Allegiance.Lobby.Tests` (12): pass.
- `Allegiance.Core.Tests`: the server-core live-wire test family is
  intermittently flaky on this machine with **volatile membership across runs**
  (`LiveTurretGunnerBindingTests` — the documented flake — plus, unpredictably,
  one or two of `StationsUpdateLiveTests` / `AsteroidsUpdateLiveTests` /
  `ConquestTerritoryLiveTests` / `LiveMoneyTests`, which also fail sporadically
  in small subsets). None reference any symbol P286 changed — this stage is
  entirely client-side, and the `Allegiance.Core.Tests` binaries compile no
  changed code — so this is pre-existing environment/timing flakiness, not a
  regression. `CargoBayReloadTests` (the one non-live casualty) passes 7/7 in
  isolation. Suite run repeatedly to confirm.

## Files changed

- `Content/Content.mgcb` — 3 new Model entries (drgrock/rrocket/tacrock.fbx).
- `Content/Models/{drgrock,rrocket,tacrock}.fbx` — new imported assets.
- `Rendering/ImportedRockMeshLoader.cs` — new sibling loader (Model → RockMesh).
- `Rendering/RockTextureFactory.cs` — `AsteroidResourceType` + `Classify` +
  `PaletteShift` + per-type `GeneratePixels`.
- `Rendering/AsteroidRenderer.cs` — per-type-texture `Draw` overload +
  `MeshVariantFor`.
- `Game1.cs` — 4 renderer variants, per-type texture cache, draw wiring.
- `tests/.../RockTextureFactoryTests.cs`, `tests/.../ImportedRockMeshLoaderTests.cs`
  — new tests.
- `~/Development/allegiance-tools/reference/meshy-results/REWORK_LIST.md` —
  asteroids marked done (missiles remain open).

Committed on `master` (Domingo pushes). Native reference untouched.

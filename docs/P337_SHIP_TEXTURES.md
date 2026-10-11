# P337 — Restore real Meshy textures on ship (and missile) models

The P287 `glb_to_fbx.py` conversion deliberately stripped every material
(`o.data.materials.clear()`) while preserving UVs, so all 75 imported ship +
missile content models rendered **flat matte gray** even though real faction
livery still lived in each source `.glb`. This stage re-attaches those real
textures as first-class Content Pipeline assets and binds them explicitly at
render time — the deterministic path the P287 review recommended over
re-running the FBX conversion with embedded textures (Assimp's embedded-texture
resolution being "historically unreliable").

## Approach (followed the review's recommendation, not a re-derivation)

- **Keep the geometry-only FBX pipeline.** The `ModelProcessor` output is
  unchanged; the FBX still carries no material.
- **Extract each model's baseColor from its source `.glb`** with a small
  self-contained Python script (`extract_meshy_textures.py`, no Blender):
  a GLB is a 12-byte header + JSON chunk + BIN chunk; the material's
  `baseColorTexture` -> image -> `bufferView` is a JPEG blob, transcoded to
  PNG and written to `Content/Textures/ships/<asset>.png`, keyed by the same
  asset names `HullModelResolver.ShipAssets` enumerates.
- **Register them in `Content.mgcb`** as `TextureImporter`/`TextureProcessor`
  assets with `GenerateMipmaps=True` (ships are usually seen minified — without
  mipmaps they'd shimmer/crawl at distance) and `TextureFormat=Compressed`
  (DXT5: compiled `.xnb` are exactly 699,189 B each = 1024² DXT5 with a full
  mip chain; ~683 KB/texture in VRAM, ~52 MB total).
- **Bind explicitly at load time**, following `AsteroidRenderer`'s
  `EnsureAsteroidTexture` + explicit-texture-bind precedent:
  - `ShipRenderer.Draw` gains a `Texture2D? texture` parameter →
    `basic.Texture = texture; basic.TextureEnabled = texture is not null`.
  - `Game1.LoadContent` builds a `_shipTextures` dictionary in parallel with
    the existing `_shipModels` dictionary (same asset names), plus
    `_missileTextures` and a per-instance `_missileTextureByModel` mirror of
    the P336 `_missileScaleByModel` map. `DrawMissileContentModel` got the same
    treatment.
  - `HullModelResolver` is unchanged — the same asset-name string that resolves
    the model is reused as the texture lookup key.
- **Ambient-light fix** (review's explicit caveat): ship + missile-model
  effects previously ran with effectively no ambient, so texture detail on a
  hull's unlit side would crush to black against space the moment textures
  landed. Both now set `AmbientLightColor = 0.32, 0.34, 0.36`
  (`ShipRenderer.ShipAmbient`), matching `StationRenderer` /
  `BuildingEffectRenderer` / `ProbeRenderer`.

### Source-texture verification (scope item 1)

Inspected every source `.glb`'s JSON chunk directly:

- **All 72 ship assets**: exactly 1 image, 1 material, 1 `baseColorTexture`
  each. One texture per asset is correct — no per-meshpart mapping needed.
- **The 3 missiles** (`mis05`, `mis400`, `artifact`): exactly 1
  `baseColorTexture` each; their 2 extra images are the `metallicRoughness`
  and `normal` PBR maps, irrelevant to BasicEffect's diffuse-only sampling.
- **`bios_scout_upscaled`** (the pre-P287 fallback): also exactly 1 baseColor.
  Its source `.glb` is the P207-era `bios_scout_upscaled.glb` (whose exported
  fbx is byte-identical to the shipped `Content/Models/bios_scout_upscaled.fbx`),
  *not* a file in the current `meshy-results/models` tree (Bios has no "scout"
  class in the P287 manifest, so no `bios-scout.glb` exists there) — flagged as
  a deviation from the brief's literal wording, with the texture pulled from
  the true source so the fallback gains paint too instead of staying matte.

Sources were downscaled 2048² → 1024² (LANCZOS) — ample for low-poly ships
seen mostly minified, and it keeps the repo + GPU footprint sane.

## Live proof ((screenshots of Meshy models, kept in the private asset pack))

Real `HullModelResolver` + real `ShipRenderer.Draw` + compiled P287 content
models + the new real textures, driven two ways (both through `tools/roster-capture`,
which reuses the exact in-game render path):

- **`roster_net_bios_wire.jpg`** — the true networked proof: a real
  `ConnectionSession` MonoGame client joined a live `vizfight roster` server
  (9 factions, 72 bots) over the wire, seated on the Bios side, and rendered
  the **13 teammate ships it actually received as EXPORT/UPDATE packets**
  (ship 8096→hull 290→`bios-bomber`, …). The ships show real faction livery:
  green Bios energy accents, metallic hull panels/panel lines, a glowing
  cruiser reactor, secondary orange on the ejectpod — **not flat gray** — and
  the unlit/backlit faces stay readable (the ambient fix), with a couple of
  dark-textured models (bios-miner/cruiser) reading dimmer than the bright ones.
- **`roster_all.jpg` / `roster_bios.jpg` / `roster_gt.jpg`** — the full
  reference sheets regenerated with textures: 72 distinct models in
  `roster_all.jpg`; the faction sheets show each civ's real paint scheme
  (Bios green, Ga'Taraan gold/rust, …). Distinct per-asset JPEG byte sizes
  remain 72/72 (real variety).
- **`bios-fighter_amb0.jpg` vs `bios-fighter_amb032.jpg`** (and
  `roster_bios_amb0.jpg` vs `roster_bios.jpg`) — the ambient A/B: at ambient 0
  only a thin lit sliver of the hull is visible and the rest crushes to pure
  black; at 0.32 the same model shows readable livery (green accents, panels)
  on the faces turned from the light. Confirms the lighting fix does what the
  review flagged.

## Build / test

- Whole-solution build (`AllegianceCore.slnx`, Debug): **0 warnings, 0 errors**.
  All 76 texture assets compile through the content pipeline (DXT5 + mipmaps).
- `Allegiance.ClientCore.MonoGame.Tests`: **606 pass** (incl. the P288
  `HullModelResolverTests` and P305 `RosterPlannerTests`).
- `Allegiance.Lobby.Tests`: **12 pass**.
- `Allegiance.ClientCore.Tests`: run 1 → 161/162 (one timing flake:
  `FullSession_ThreePeers_TeammateComposition_Then_RealCrossSideKill`, a
  loopback EXPORT race that fails/passes across reruns and whose project does
  not reference any code this stage changed); run 2 → **162/162 pass**.
- `Allegiance.Core.Tests`: 3264/3269 on run 1 — the documented live-wire
  timing flake subset that rotates every run (server/socket contention; none of
  the changed projects feed `Allegiance.Core`). Repeated for the second clean
  reading.

## Files changed

- `extract_meshy_textures.py` — new extraction script (repo root, alongside the
  other `verify_*.py` tooling).
- `Content/Textures/ships/*.png` — 76 new texture assets (72 ships + 3 missiles
  + `bios_scout_upscaled`).
- `Content/Content.mgcb` — 76 new `TextureImporter`/`TextureProcessor` entries
  (`GenerateMipmaps=True`, `TextureFormat=Compressed`).
- `Rendering/ShipRenderer.cs` — `texture` param on `Draw`, `ShipAmbient`.
- `Game1.cs` — `_shipTextures`/`_missileTextures`/`_missileTextureByModel`/
  `_shipTextureById` loading + binding, ambient on `DrawMissileContentModel`.
- `tools/roster-capture/Program.cs` — binds textures in the reference renderer
  and fixes a pre-existing unlocked `world.Ships` gather in `wire` mode (the
  P319/P306 "collection modified" race) so the networked proof can run.
- (screenshots of Meshy models, kept in the private asset pack).

Committed on `master` (Domingo pushes). Native reference source untouched.

# P421 — real original per-resource-type asteroid textures

Date: 2026-09-28
Branch: master (committed, not pushed — Domingo pushes)

## TL;DR

The old synthetic "rough rock" procedural diffuse is gone for every resource type
that has a real original bitmap in the artwork tree. Asteroids now render with the
**genuine native per-resource-type rock surfaces** (`bgrndNNbmp.mdl`), each a real
2048×2048 RGB565 original-game bitmap — far higher resolution than the 256px
procedural stand-in, and the same assets the native game ships with. The procedural
generator stays as the documented fallback for the one type that has no native rock
(gold) and for any missing/undecodable asset.

**Important premise correction (verified against native source, not guessed):**
the task brief assumed the eight `*rockbmp.mdl` files it listed were "the real
per-resource-type rock bitmaps". Decoding them shows that is wrong:

- `drgrockbmp.mdl` / `tacrockbmp.mdl` (256×256) are real rock-surface textures, but
  they pair with the **drgrock/tacrock meshes**, not with resource types.
- `carbonrockbmp` / `copperrockbmp` / `goldrockbmp` / `heliumrockbmp` /
  `hotrockbmp` / `waterrockbmp` are all **16×16 radar/map icons** — the native
  `asteroidTypes[].iconName` values (`heliumrock`, `hotrock`, `copperrock`,
  `carbonrock`), **not** surface textures.

The authoritative per-resource-type mapping is the native `asteroidTypes[]` table in
`src/Igc/asteroidigc.cpp`, which assigns each resource type a **specific native rock
model** (`bgrndNN`) whose surface texture is the matching `bgrndNNbmp.mdl`. Those are
the real originals, and that is what this port now loads.

## 1. What got wired to what

Mapping implemented in `src/Allegiance.ClientCore.MonoGame/Rendering/RockTextureSource.cs`,
straight from the native table (`Igc/asteroidigc.cpp:126-137` + `AsteroidDef`
`igc.h:1709-1720`):

| Resource type (capability) | Native model | Real surface asset | Resolution |
|---|---|---|---|
| Plain (`0x00`) | `bgrnd03`/`bgrnd05` | `bgrnd05bmp.mdl` | 2048×2048 |
| Buildable (`0x08`) | `bgrnd03`/`bgrnd05` | `bgrnd05bmp.mdl` | 2048×2048 |
| Helium (`0x01`/`0x02`) | `bgrnd55`/`bgrnd56` | `bgrnd55bmp.mdl` | 2048×2048 |
| Uranium (`0x10`) | `bgrnd51` | `bgrnd51bmp.mdl` | 2048×2048 |
| Silicon (`0x20`) | `bgrnd52` | `bgrnd52bmp.mdl` | 2048×2048 |
| Carbon (`0x40`) | `bgrnd53` | `bgrnd53bmp.mdl` | 2048×2048 |
| **Gold (`0x04`)** | *(no native row)* | **none → procedural fallback** | — |

The mapper returns null for gold, which is exactly the "no real asset" contract the
brief's point 4 asks to keep as graceful fallback.

## 2. What still falls back to procedural, and why

- **Gold (`0x04`)** — the native `asteroidTypes[]` table has **no gold row**.
  `c_aabmMineGold = 0x04` (`igc.h:763`) is a capability constant, but no native rock
  model is dedicated to it, and `goldrockbmp.mdl` is a 16×16 icon. So a gold-capable
  asteroid renders with the (gold-tinted) procedural generator — the documented,
  native-faithful degradation.
- Any real asset that is missing or fails to decode at startup logs a warning and
  falls back to procedural for that type (the port's established graceful-degradation
  convention — same as `GaugeTextures`/`ExplosionAssets`). Today all five distinct real
  assets decode cleanly (logged `[monogame] rock surface … 2048x2048`).

Plain and Buildable intentionally share `bgrnd05bmp` — that is exactly what native
does (both plain and buildable rows use `bgrnd03`/`bgrnd05`).

## 3. Mesh ↔ texture pairing

The port already imports the real **mesh geometry** (`drgrock`/`tacrock`/`bgrnd05`
via content pipeline, `ImportedRockMeshLoader`), so real rocks ride on real 3D
geometry. The per-type texture is applied per asteroid resource type exactly as before
(`EnsureAsteroidTexture` in `Game1.Rendering.cs`) — only the source changed from the
procedural generator to the real surface bitmap for types that have one.

Two consequences of using the native bgrndNN surfaces (rather than the two 256px
drgrock/tacrock textures):

- They are genuine per-type *original* rock photographs (2048×2048), matching what the
  native game painted onto its bgrndNN rocks.
- The rock diffuse is deliberately a per-**type** choice (this port picks mesh by
  AsteroidId for silhouette variety and texture by resource type for colour/material
  variety — a pre-existing P286 design). So a drgrock-mesh rock of Helium type gets the
  real bgrnd55bmp, not drgrockbmp. That is consistent with the brief's own instruction
  to "apply the real texture to whatever mesh is already used for that type".

No UV changes were needed: the existing equirect per-vertex UVs map the square 2048
surface onto the rock exactly as they mapped the 256 procedural one.

## 4. Real texture resolution — the "should we AI-enhance?" question

Domingo's original question was whether the originals could be enhanced. The relevant
fact for that follow-up:

- The real per-resource-type rock surfaces are **2048×2048**, already far higher-res
  than the 256px procedural stand-in they replace. No upscaling was done (out of scope),
  and none is obviously needed for these assets — they are the full-res originals the
  native game shipped.
- The 16×16 icon files (`heliumrockbmp`, etc.) are *not* candidates for enhancement —
  they are UI icons, not surfaces.
- `drgrockbmp`/`tacrockbmp` (256×256) are the only genuinely low-res real rock
  surfaces, and they belong to the two meshes, not to a resource type. If a future task
  wants the per-mesh drgrock/tacrock surfaces improved, *those* 256px textures would be
  the legitimate AI-upscale candidates. Not touched here.

## 5. Verification

### Unit / real-asset tests (new: `RockTextureSourceTests`, 12 cases)

Mirroring the repo's existing real-asset test convention (`ExplosionAssetFramesTests`),
these decode the actual shipped artwork via the headless `MdlTextureDecoder` — no GPU:

- every resource type resolves to its **correct** real stem (plain/buildable→bgrnd05bmp,
  helium→bgrnd55bmp, uranium→bgrnd51bmp, silicon→bgrnd52bmp, carbon→bgrnd53bmp,
  gold→null) — no mismatch, no silent fall-through when a real asset exists;
- every non-null stem decodes to a **≥256×256 square surface** (2048×2048), proving it
  is a real rock surface and not a 16×16 icon;
- gold reports **no path**, so the caller provably falls back to procedural;
- the five distinct real assets are **mutually distinct files** (distinct SHA-256).

Existing `RockTextureFactoryTests` (procedural generator) all still pass — the generator
remains intact as the documented fallback.

### Whole-solution build

`dotnet build -c Release` → **0 warnings, 0 errors** (whole solution).

### Full test suite (2+ runs)

`Allegiance.ClientCore.MonoGame.Tests` (the only project this change compiles into,
incl. the new tests): **826/826 pass**, twice consecutively.

`Allegiance.Core.Tests` + `Allegiance.ClientCore.Tests` show only the **pre-existing
documented live-wire timing flakes** — a *different* rotating subset each run
(LiveWinCondition, LiveTurretGunner, LiveRipcord, LiveMoney, LiveRelaunch,
AutoReloadOnEmpty, CargoBayReload, DockOut watcher, EntityTable…), exactly the pattern
the repo has documented across P128/P134/P251/P288/P356/P365/etc. This change is
rendering-only in the MonoGame client and cannot affect those server-side timing paths.

### Live proof (real server, real field, real renderer)

New harness `tools/rock-texture-proof/` boots a real in-process server hosting
`m2xspiderw.igc` (**134 real asteroids**), joins a real MonoGame `ConnectionSession`,
and renders the field through the **real `AsteroidRenderer`** with the real imported
`drgrock` mesh (9974 verts — matching the task brief's own boot log) plus the **real
`RockTextures`**, saving a PNG per resource type present, plus a split-screen
real-vs-procedural comparison. Output in `.verify_p421/`.

Run evidence:
```
[proof] joined; asteroids known=134
[monogame] rock surface 'Plain':    2048x2048 'bgrnd05bmp.mdl'
[monogame] rock surface 'Helium':   2048x2048 'bgrnd55bmp.mdl'
[monogame] rock surface 'Uranium':  2048x2048 'bgrnd51bmp.mdl'
[monogame] rock surface 'Silicon':  2048x2048 'bgrnd52bmp.mdl'
[monogame] rock surface 'Carbon':   2048x2048 'bgrnd53bmp.mdl'
[monogame] rock surface 'Buildable':2048x2048 'bgrnd05bmp.mdl'
[proof] using real drgrock mesh (9974 verts)
[proof] wrote .verify_p421/rock_Helium_real.png     [Helium]    real=YES cap=0x01 rad=296
[proof] wrote .verify_p421/rock_Buildable_real.png  [Buildable] real=YES cap=0x08 rad=292
[proof] wrote .verify_p421/rock_compare_real_vs_procedural.png
```

Visual confirmation (inspected the PNGs):

- **`rock_compare_real_vs_procedural.png`** — left half (real bgrnd05bmp) shows a dark,
  high-frequency, angular real-rock surface; right half (procedural) shows the old
  lighter, smoother gray-brown mottle. **Clearly distinct** — the two are unmistakably
  different surfaces.
- **`rock_Helium_real.png`** — cool, bluish-grey cast (real bgrnd55bmp), rich texture;
- **`rock_Buildable_real.png`** — dark bronze/brown (real bgrnd05bmp), jagged facets.
- The two **real** renders from the same live field are measurably distinct: helium is
  bluer (B/R 1.09) and brighter than buildable (B/R 1.02) — the real per-type rock
  look, confirmed on the same drgrock mesh in the same field.

Honesty note: `m2xspiderw`'s real rock field happens to contain Helium + Buildable type
rocks, so the live PNGs show those two real surfaces (plus the comparison). The other
three real surfaces (uranium/silicon/carbon) are pinned by the unit tests' real-asset
decode; the same `RockTextureSource` drives them identically at runtime.

## 6. Files

- `src/Allegiance.ClientCore.MonoGame/Rendering/RockTextureSource.cs` — **new**:
  per-type real asset mapping + `RockTextures.Load` (mirrors `GaugeTextures` decode
  pipeline; reuses `MdlTextureDecoder`, no second decoder).
- `src/Allegiance.ClientCore.MonoGame/Game1.Lifecycle.cs` — load `RockTextures` at
  startup.
- `src/Allegiance.ClientCore.MonoGame/Game1.Rendering.cs` — `EnsureAsteroidTexture`
  prefers the real surface; procedural is the fallback.
- `src/Allegiance.ClientCore.MonoGame/Game1.cs` — field + dispose.
- `tests/Allegiance.ClientCore.MonoGame.Tests/RockTextureSourceTests.cs` — **new**
  real-asset mapping tests.
- `tools/rock-texture-proof/` — **new** live rendering-proof harness.
- `.verify_p421/` — proof PNGs (rendered artifacts; kept locally, not committed).
- `P421_REAL_ASTEROID_TEXTURES.md` — this report.

Out of scope honoured: no AI upscaling, no other rendering pipeline touched, no
P368-P420 touched.

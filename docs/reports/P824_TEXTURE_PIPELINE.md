# P824 — texture half of the authoring pipeline: PNG/glTF material → native `<stem>bmp.mdl` (and back)

Base: master @ `9be9120a` (P812 + P823). Branch `pi/p824`.
Native reference: `~/Development/Allegiance/src/` (all `file:line` re-grepped on this lane).

P812/P823 moved geometry, hardpoints and collision through glTF, but not
textures: P823's new-art hull imports stem `hullbmp` with no texture file
behind it, so new art rendered untextured. P824 closes that gap: the port now
**writes** the compiled native texture units it already **decoded**, the glTF
pipeline carries the base-colour image both ways, and the UV convention is
proven numerically through the port's real renderer.

## 1. What a native texture unit is (re-verified against the corpus)

A compiled texture is its own `.mdl` file named after the texture stem
(`fig30bmp.mdl` exports `fig30bmp`; the hull's `TextureGeo` imports stem
`fig30bmp` from namespace `fig30bmp`). Layout, byte-verified against the
artwork checkout (2 657 image files parsed):

| offset | content | native cite |
|---|---|---|
| 0x00 | magic `0x0DEBADF00`, version `0x00010000`, 1 namespace (`model`), 1 import (ns 0, `ImportImage`), 1 export (`<stem>`), 0 definitions | `engine/mdlbin.cpp` `MDLBinaryNameSpace::ReadFile` / `ReadObject`; `ImportImage` registered in the `model` namespace (`engine/modeler.cpp:707`) |
| 0x30 | one object (index 0): `ObjectImport(0)`, `ObjectBinary` | `engine/mdlbin.cpp` (`ObjectImport`=9, `ObjectBinary`=7) |
| — | 36-byte stored `BinarySurfaceInfo`: sizeX, sizeY, pitch = align4(2·sizeX), bitCount 16, masks `0xF800/0x07E0/0x001F`, alphaMask 0, colour-key byte (0 in **every** corpus image), 3 trailing pad bytes | `engine/enginep.h:313-322` (`sizeof` = 36 on x86: 8 dwords + 1-byte `bool` + 3 pad); copied raw by `engine/mdl.h:37-50` `GetStructure` |
| — | RGB565 rows, **top row first**, each row 2·sizeX bytes + zero pad up to pitch; consumed as `pitch·sizeY` bytes | `engine/modeler.cpp:715-731` (`ImportImageFactory::Read`, `MovePointer(pbsi->m_pitch * pbsi->m_size.Y())`) |
| end | 4-byte `ObjectEnd` token (`0x00000000`) closing the single object | `engine/mdlbin.cpp` `ReadObject` loop |

Survey facts that drive the writer (corpus scan of every texture referenced by
a model `.mdl` — 639 models, 776 texture units, plus all 2 657 `*bmp*.mdl`
files):

- **Masks/bitcount**: every image file is RGB565 with `F800/07E0/001F`,
  alphaMask 0, bitCount 16. No other format occurs.
- **Colour key**: 0 in every file; the 3 pad bytes are **uninitialised**
  mdlc stack garbage (mostly `0xCC`, otherwise arbitrary). The reader ignores
  them (raw `sizeof` copy), so the writer zeroes them — the only byte delta of
  a native round trip.
- **Sizes**: model textures are power-of-two per side (767/776; the 9 outliers
  are backdrops, not model textures), min 16, **max 2048** (ss27bmp et al.).
  HUD/UI art is arbitrary size — out of scope (2-D HUD path).
- **Pitch**: `align4(2·w)` everywhere (odd widths pad, e.g. 91×80 → pitch 184).
- **Mip-maps**: none. File length = header + `pitch·sizeY` + the 4-byte end
  token; no second surface behind the rows. Nothing to port.

While verifying this, one latent decoder bug surfaced and was fixed:
`MdlTextureDecoder` used to read the rows **packed** (`i / sizeX`), ignoring
`pitch` — wrong for every native odd-width file (the corpus' HUD art). It now
walks rows via `pitch` and bounds-checks `pitch·sizeY`
(`src/Allegiance.Core/Data/MdlTextureDecoder.cs`).

## 2. The writer (`src/Allegiance.Core/Data/MdlTextureWriter.cs`)

`MdlTextureWriter.Write(stem, width, height, rgbaTopLeftOrigin, options)` emits
the layout above exactly. Decisions, each with its rule stated:

- **RGB565 quantisation** (`Rgb565`): a byte component maps to the 5-bit
  (red/blue) or 6-bit (green) code whose **decoded** value is nearest, ties to
  the lower code. This is the exact inverse of the decoder's
  `round(code/max·255)` expansion — `encode(decode(code)) == code` and
  `decode(encode(byte)) == byte` for every byte the decoder can produce, which
  is what makes the native byte round trip lossless. (Plain
  `round(c·max/255)` is *not* invertible: 4 would encode to code 0, decoding
  back to 0.) Pinned by
  `P824TexturePipelineTests.Rgb565_quantisation_is_the_exact_inverse_of_the_decoder_expansion`.
- **Ordered dithering**: optional 4×4 Bayer (one quantum of spread before the
  nearest-code rule), behind `MdlTextureWriteOptions.OrderedDither`,
  **default off = plain rounding**. Visual option for new art only; never used
  on round trips.
- **Size rule**: each side power-of-two, capped at 2048 (round up to preserve
  authored detail; the cap rounds down). Sizes below the corpus' observed
  minimum of 16 stay power-of-two rather than being padded with invented
  pixels (documented; the corpus band 16–2048 is guidance for artists, not a
  format limit). Resizes use an exact-coverage **box (area-average) filter**
  (`ResampleRgba`): downscaling is anti-aliased and a uniform image resamples
  to itself. `Resize:false` turns violations into a loud failure.
- **Input convention**: `rgbaTopLeftOrigin` is GPU order — exactly what
  `MdlTextureDecoder.Decode(..., flipVertical: true)` produces and what a glTF
  image holds. With the default `FlipVertical: true` the writer stores rows
  bottom-picture-first (the files' top-row-first picture order), so the
  decoder's 3-D path reconstructs the input exactly; HUD/2-D callers pass
  `FlipVertical: false`. Alpha is ignored (RGB565 has none).

## 3. Round trip on native files

`P824TexturePipelineTests.Native_texture_files_survive_decode_write_byte_identically`
(`[SkippableTheory]`, `ALLEGIANCE_ARTWORK_PATH`, skips on clean clones) runs
decode → write → byte-compare on 12 stems: fig30bmp, bom04bmp, ss27bmp,
ss27insidebmp, cap01bmp, fig07bmp, acs01bmp, artifactbmp, bom30bmp, fig405bmp,
fig403bmp, belt_omnibmp (512² down to 64², incl. the two-stem ss27). Result on
this host: **all pass**; every file differs from its rewrite in at most the 3
uninitialised pad bytes (asserted: diffs confined to
`[structEnd−3, structEnd)`), decoded pixels identical.

The CLI end-to-end (`mdl-to-glb verifyimport`, now texture-aware: native →
glb **with embedded base-colour PNG** → mdl/cvh + texture unit → compare)
on this host:

```
VERIFY-IMPORT OK: fig30   (fig30bmp 512×512   — texture byte round trip differs only in the 3 pad bytes)
VERIFY-IMPORT OK: bom04   (bom04bmp 256×256   — ditto)
VERIFY-IMPORT OK: ss27    (ss27bmp 2048×2048 + ss27insidebmp 1024×1024 — ditto)
```

(The mesh `.mdl` BYTE-DIFF lines in that output are the P823 report's known
LOD-detail-level delta, unchanged.)

## 4. Pipeline wiring (`tools/mdl-to-glb`)

- **export** now decodes each texture stem's `<stem>.mdl` from the art dir and
  embeds it as the GLB's base-colour image (`images` + `samplers` +
  `textures` + `materials`, `mimeType image/png`; material named after the
  stem, textured primitives reference it) — a GLB opens textured in Blender.
  `--no-texture` skips embedding; missing texture files are reported and the
  GLB stays untextured.
- **import** resolves each textured mesh's stem (material name first — what
  Blender-authored GLBs carry — then the P812 `<stem>_<texstem>_<i>` mesh-name
  convention; `--texture-stem` overrides), **rejects** a textured mesh with no
  base-colour image unless `--no-texture` (`GlbImporter.ValidateTextureImages`;
  deliberately not part of `Validate`, because an image-less GLB remains a
  legal untextured P823 import), and writes each embedded PNG as the native
  `<stem>.mdl` texture unit via the writer.
- **`PngCodec.cs`** — minimal dependency-free PNG codec (8-bit RGB/RGBA, all
  five row filters, CRC-checked, non-interlaced): `Encode` writes filter-0
  RGBA, `Decode` reads what Blender writes. Tested round-trip, per-filter, and
  malformed-input (bad signature / truncation / CRC / palette).
- **`gentexture` / `gentesthull`** — build the proof texture (checker + four
  corner markers) and the P823 proof hull (with the texture embedded) entirely
  in code, for the new-art proof below.

**UV convention.** glTF and the port's renderer share the top-left UV origin;
the exporter embeds the decoder's `flipVertical:true` output (the exact RGBA
`NativeShipRenderer.TextureFor` uploads), and the importer feeds the GLB image
back through the writer's inverse flip — so a UV that points at a texel in
Blender points at the same texel in game. Proven numerically, not visually:

- `Proof_texture_markers_land_where_the_test_hull_uvs_say` — the generated
  proof texture written as a native texture unit and decoded through the port's
  real decoder: nearest sampling at the test hull's corner UVs
  (0,0)/(1,0)/(0,1)/(1,1) hits the TL-magenta/TR-yellow/BL-cyan/BR-green
  markers exactly.
- End to end through the renderer (below): all four `--uv-check` assertions
  PASS against the GPU texture `NativeShipRenderer` actually uploaded.

## 5. New-art proof (P823's hull + generated texture, rendered)

```
mdl-to-glb gentesthull .scratch/p824/newart testhull --size 128   # glb + sidecar, texture embedded
mdl-to-glb import      .scratch/p824/newart testhull --out .scratch/p824/newart
  → testhull.mdl (736 B) + testhull.cvh + hullbmp.mdl (32 880 B, native layout)
p823-render-proof --dir .scratch/p824/newart --stem testhull --label p824 \
    --uv-check "0,0:FF00FF" --uv-check "1,0:FFFF00" --uv-check "0,1:00FFFF" --uv-check "1,1:00FF00"
  mesh loaded: tris=8 radius=3.3166 texStem=hullbmp
  uv-check (0,0) → texel (0,0)     rgb=(255,0,255) PASS
  uv-check (1,0) → texel (127,0)   rgb=(255,255,0) PASS
  uv-check (0,1) → texel (0,127)   rgb=(0,255,255) PASS
  uv-check (1,1) → texel (127,127) rgb=(0,255,0)   PASS
```

The marker lands where the UVs say, through writer → file → decoder → GPU
texture. PNGs for Claude (not viewable on this lane):

- `.scratch/p824/p823-p824-testhull-hangar.png` (coverage 8.3%)
- `.scratch/p824/p823-p824-testhull-threequarter.png` (coverage 3.4%)

(mirrored to `~/allegiance-evidence/P824/`; nothing binary enters git —
`.scratch/` is git-ignored.)

## 6. Files

| what | where |
|---|---|
| Texture decoder (moved from `src/Allegiance.ClientCore.Game/Rendering/` so the pipeline can decode headless; + pitch row-padding fix) | `src/Allegiance.Core/Data/MdlTextureDecoder.cs` |
| Texture writer + RGB565 quantisation + size rule/box resample | `src/Allegiance.Core/Data/MdlTextureWriter.cs` |
| GLB images/textures/materials (writer + reader), texture-stem-from-material, image validation | `tools/mdl-to-glb/GlbWriter.cs`, `GlbReader.cs`, `GlbModel.cs`, `GlbImporter.cs` |
| PNG codec; proof texture/hull generators | `tools/mdl-to-glb/PngCodec.cs`, `TestTextureGenerator.cs`, `Program.cs` (`export`/`import`/`verifyimport`/`gentexture`/`gentesthull`) |
| Numeric UV check through the real renderer | `tools/p823-render-proof/Program.cs` (`--uv-check`) |
| Tests (37: quantisation, layout, flips, size rule, PNG, GLB round trip, import rejection, UV proof, 12 native byte round trips) | `tests/Allegiance.Core.Tests/P824TexturePipelineTests.cs` |

`GlobalUsings.cs` in `src/Allegiance.ClientCore.Game` papers over the decoder
move with a project-wide `using Allegiance.Core`.

## 7. Verification

- `dotnet build AllegianceCore.slnx` — 0 errors / 0 warnings.
- `tools/build-tools.sh` — all 87 tool projects, 0/0.
- New tests 3× alone (`P824TexturePipelineTests`): 37/37 × 3. Affected suites
  3× alone: `MdlTextureDecoderTests` 7/7 × 3, `GlbToMdlTests`+`MdlToGlbTests`
  42/42 × 3.
- ONE `tools/run-tests.sh --tier fast` — 10 801 tests, 1 failure:
  `P802TeamCivLiveTests.Random_change_resolves_to_a_real_legal_civ_and_relays_it`,
  the four-times-ledgered random+timing load flake (docs/TEST_FLAKES.md): the
  full class passes 3/3 alone, and P824 touches no team-civ/relay path.
- `python3 scripts/parity-scorecard.py` — validates (new `content` row + P812
  row evidence updated).
- `python3 tools/check-glossary-links.py` — pass (glossary updated).

## 8. Notes / unfinished

- The 3 uninitialised `BinarySurfaceInfo` pad bytes are the only native→write
  byte delta; everything else is byte-identical on every file tested.
- Dithering ships off by default; turn on per-call
  (`MdlTextureWriteOptions.OrderedDither`) when authoring art with gradients
  that band in RGB565.
- 2-D HUD art and the HD add-on's PBR path (`HdMaterialRenderer`, private
  repo) are out of scope and untouched.

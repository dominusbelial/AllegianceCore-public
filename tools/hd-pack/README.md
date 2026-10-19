# Atlas material pack builder

No generated textures are shipped here. Requires Python 3.11+, Pillow, and .NET 10
when exporting a catalogue directly from a core.

```sh
python3 tools/hd-pack/build_pack.py \
  --input /external/per-stem-maps --artwork "$ALLEGIANCE_ARTWORK_PATH" \
  --core "$ALLEGIANCE_ARTWORK_PATH/PCore001.igc" --output /external/hd-pack
```

Input is `<input>/<atlas-stem>/{basecolor,normal,metallic,roughness,emission}.png`.
The output is `hd-pack/manifest.json`, `hd-pack/coverage.json`,
`hd-pack/textures/<stem>/*.png`, and `hd-pack.zip`. Existing outputs are refused.
Set `ALLEGIANCE_HD_PACK_PATH` to the unpacked directory containing the manifest.
The client keeps reading legacy schema-1 packs.

For ESRGAN outputs named by model, pass `--mapping mapping.json`, containing an
explicit object such as `{"wc_icfig":"fig02bmp"}`. Use the HD source's
`inputs/<model>/native.json` **Texture** field; never infer this from model names.
A map set belongs to one atlas, even if its source model has several atlases.
Build each atlas separately; do not stretch one atlas over unrelated model parts.

Basecolour/emission PNG samples are sRGB; normal/metallic/roughness samples are
linear data. Untagged PNGs follow this ESRGAN convention. The builder checks
explicit PNG gamma/sRGB tags and rejects ICC profiles and conflicting tags.
Optional `maps.json` per input folder can declare `colorSpaces` (the full five-map
object in the manifest) and `nativeSha256` (the source native texture `.mdl` hash).
Use `--expected-hashes hashes.json` to require a source hash for every selected
stem. This detects outdated ESRGAN outputs against the current artwork before
publishing. Without provenance metadata, the builder fingerprints the current
artwork; pixel values alone cannot prove the source used to generate a map.

All maps must be RGB, RGBA or L PNGs, from 1 to 8192 pixels per axis. Normal and
emission sizes must equal basecolour; scalar metallic/roughness maps may be half
resolution on both axes (the current ESRGAN 4K/2K export convention). Every map
is fully decoded before publishing. The manifest records dimensions, colour
spaces, map hashes and the native `.mdl` SHA256 for each atlas. The native hash
is also checked lazily by the client; a mismatch or missing map uses native art.
The release zip is reproducible for identical maps/catalogue.

`--core` uses `catalog/` with the repository's actual IGC and MDL parsers. Coverage
includes each core hull/station/missile/probe/projectile texture override plus
all per-mesh texture scopes in the model's base geometry and LODs. Map-owned
asteroids and alephs and runtime treasure visuals are not static-core model rows;
their atlas materials can still be included and appear as unreferenced HD stems.
`--catalog existing.json` accepts a previously exported catalogue with the same
`models: [{kind, model, stems}]` layout, useful for CI synthetic packs. Coverage
lists every HD, referenced, covered, missing and extra stem rather than assuming
one material per model.

```sh
python3 -m unittest discover -s tools/hd-pack -p 'test_*.py' -v
```

## Compressed release packs (schema 3)

Install `python3 -m pip install -r tools/hd-pack/requirements.txt`, then add
`--format bc` to the command above. PNG authoring mode remains the default and
only requires Pillow. BC mode also uses NumPy and **ispc_texcomp 1.0.1**, MIT
Python bindings to Intel's MIT ISPCTextureCompressor. Linux CI wheels (Python
3.11+, x86_64/aarch64), Windows and macOS wheels are available. No Wine, GPU,
proprietary SDK, runtime encoder dependency or generated artwork is shipped.
Sources/licences: [bindings](https://github.com/K0lb3/ispc_texcomp_py),
[Intel codec](https://github.com/GameTechDev/ISPCTextureCompressor).

The release contains DX10 DDS files with complete mip chains, BC7 sRGB for
basecolor/emission, BC5 UNORM for normal XY (shader reconstructs positive Z),
and separate BC4 UNORM metallic/roughness. Color mips are filtered in linear
light using the sRGB transfer function; normal mips average vectors and
renormalize. Each mip smaller than a block repeats edge texels before encoding.
The `alpha_fast` BC7 preset preserves alpha. BC3 sRGB fallback DDS color maps
are published as a separate, complete `<output>.bc3.zip` alternative, so
macOS GL 4.1 and older GL contexts without BPTC never need CPU transcoding.
Install exactly one zip: the normal zip for BPTC devices, the BC3 zip otherwise.
The unpacked authoring directory includes both variants for testing. RGTC (BC4/5) requires desktop GL 3.0+;
unsupported platforms retain native textures. Schema 1/2 PNG packs still work.

Default basecolor maximum dimensions:

| Class | Pixels | Full material MiB, including mips |
| --- | ---: | ---: |
| Capital | 4096 | 37.33 |
| Station | 4096 | 37.33 |
| Fighter/scout/bomber/small craft | 2048 | 9.33 |
| Missile/probe/projectile | 1024 | 2.33 |

The other four maps use half the basecolor limit. Sources are never enlarged;
non-power-of-two inputs round down to power-of-two dimensions. The native
catalogue exports a `capital` boolean from hull length ≥75 (an authoring policy; native length field at
`~/Development/Allegiance/src/Igc/igc.h:1780`) or capital threat/carrier capabilities (native
`~/Development/Allegiance/src/Igc/igc.h:5383-5394`, carrier bit at `:736`);
there is no class inference from filenames. Shared atlases use the largest
class; unreferenced atlases default to small. Pass `--resolution-policy policy.json`
to override the four limits (`capital`, `station`, `small`, `projectile`), each a
power of two from 4 to 4096. A previously exported catalogue without `capital`
flags treats ships as small; re-export with `--core` for release builds.

`manifest.json` records each encoded map's format, dimensions, mip count,
resident block bytes, hashes, atlas class and per-class file/residency totals.
The final CLI JSON reports actual zip bytes, class totals and maximum material
residency. Each release zip contains one color codec; no duplicate color-map download
is required. The unpacked authoring directory contains both variants. Native atlas SHA256 validation remains mandatory on first use.

The DesktopGL adapter is pinned to MonoGame 3.8.5.1: that version lacks BC4/5/7
`SurfaceFormat` values, so a narrow JIT-only texture-handle bridge calls
`glCompressedTexImage2D` directly. MonoGame retains lifetime, sampler and binding
ownership. DDS dimensions, format, complete mip chain, 2D/single-array status
and exact payload length are checked before allocating. No CPU BC decoder is
present. Keep the real-GL compressed regression passing when updating MonoGame.

The renderer estimates the mesh's projected diameter, keeps roughly two texels
per screen pixel (64px minimum), seeks past unused DDS levels, and uploads only
the required mip tail. Approaching instances promote detail; a two-second hold
prevents far instances sharing an atlas from immediately demoting a close view.
LRU eviction counts compressed block bytes; unsupported, stale, missing, corrupt
or over-budget materials follow the existing native fallback. Uploads remain
synchronous on the draw thread; first-use latency should be measured separately
from warmed steady-state FPS.

BC authoring uses two concurrent atlases by default; `--workers 1..4` bounds
CPU and peak working memory. Unlit native passes use a separately budgeted
UNORM compressed basecolor view so BasicEffect retains encoded sRGB samples.

`--resolution-policy tools/hd-pack/compact-policy.json` halves each class limit
for a smaller download. This is an explicit quality/size option; the default
still uses the requested 4096/2048/1024 class limits. Mip streaming reduces
resident bytes independently of the release's maximum texture resolution.

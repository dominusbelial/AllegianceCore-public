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

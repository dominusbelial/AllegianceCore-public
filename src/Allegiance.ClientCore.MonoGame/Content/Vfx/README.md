# Enhanced VFX (P546) — CC0 effect art shipped with the game

CC0 flipbook atlases used by the P546 enhanced-explosion / enhanced-smoke
renderers (adopted from the P543 spike). Unlike the native `.mdl` art (loaded
from `ALLEGIANCE_ARTWORK_PATH`), these are **ours to ship** — they are packaged
into the game's Content (DesktopGL) and Android assets and load on a clean
checkout with **no** artwork path set.

`VfxAssets` loads each PNG via `TitleContainer.OpenStream("Content/Vfx/atlas/<stem>.png")`
and slices the grid at runtime (mirroring how `ExplosionAssets` slices the
native `expNN` strips). See
`docs/reports/P546_ENHANCED_EXPLOSIONS_AND_SMOKE.md` for the port + packaging
details.

## License table

| Asset file (atlas) | Source | Grid | Frame px | Atlas px | Raw RGBA | License |
|---|---|---|---|---|---|---|
| `atlas/Explosion00.png` | Unity Free VFX: https://unity.com/blog/engine-platform/free-vfx-image-sequences-flipbooks | 5×5 | 128 | 640×640 | 1.56 MiB | CC0 |
| `atlas/Explosion01.png` | Unity Free VFX (above) | 5×5 | 128 | 640×640 | 1.56 MiB | CC0 |
| `atlas/Explosion01-light.png` | Unity Free VFX (above) — additive fire-ball glow | 5×5 | 128 | 640×640 | 1.56 MiB | CC0 |
| `atlas/Cloud01.png` | Unity Free VFX (above) — billowy smoke | 8×8 | 96 | 768×768 | 2.25 MiB | CC0 |
| `atlas/WispySmoke01.png` | Unity Free VFX (above) — wispy smoke | 8×8 | 96 | 768×768 | 2.25 MiB | CC0 |
| `atlas/DiscSmoke01.png` | Unity Free VFX (above) — soft disc smoke | 16×4 | 96 | 1536×384 | 2.25 MiB | CC0 |

Total atlas VRAM (raw RGBA8, uncompressed upload): **~9.9 MiB** (the six atlases
plus the procedural soft disc) — see the P546 report's cost section.

Licenses (archived under `licenses/`):
- `licenses/unity_free_vfx_flipbooks_CC0.txt` — Unity free VFX flipbooks, CC0
- `licenses/kenney_particlePack_License.txt` — Kenney Particle Pack, CC0
  (https://opengameart.org/content/particle-pack-80-sprites; licensed for
  future use, not shipped in this task)
- `licenses/kenney_smoke_License.txt` — Kenney Smoke Particles, CC0
  (https://opengameart.org/content/smoke-particle-assets; licensed for future
  use, not shipped in this task)

Processing: source TGA atlases extracted from each `-flipbooks.zip`, each grid
cell cropped and downscaled (LANCZOS) to the listed frame size, re-packed into
a square RGBA PNG atlas by `PIL` (script documented in the P543 report). The C#
side loads these PNGs and slices the grid at runtime.

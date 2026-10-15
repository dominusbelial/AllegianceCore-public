# Credits & third-party art

AllegianceCore's code is licensed as described in [`LICENSE`](LICENSE). This
file credits the **third-party images and art** that the client ships or that
this repository contains. Licence texts are archived next to the assets
(links below). The in-game **Credits** screen shows the same attributions to
players.

**Original Allegiance game code and art.** AllegianceCore ports Microsoft's
Allegiance source code (MIT since 2017, "Copyright Microsoft Corporation"); the
required notice is in [`NOTICE`](NOTICE). Most original game art (`.mdl`
models, textures, HUD bitmaps, maps, `PCore001.igc`) is **not** in this
repository: it's loaded at runtime from the FreeAllegiance Artwork set
(`ALLEGIANCE_ARTWORK_PATH`, https://github.com/FreeAllegiance/Artwork). Some
of that art (e.g. planet posters such as `nebplnt06`/`nebplnt33`) itself
derives from NASA imagery.

The following original files **are** included, from the FreeAllegiance Artwork
repository (MIT, "Copyright (c) 2017 FreeAllegiance"; notice in `NOTICE`):

| Files | Where |
|---|---|
| 418 game sounds (`.ogg`) | `src/Allegiance.ClientCore.MonoGame/Content/Sounds/` |
| `sounddef.mdl`, `quickchat.mdl` (sound and quick-chat definitions) | `src/Allegiance.ClientCore.MonoGame/Content/` |

Renders of original ship models (reference images used to generate the Meshy
models below) are in `docs/meshy_hints/`, and many screenshots in
`docs/reports/` show original game art.

## Meshy-generated ship models

108 `.fbx` ship models (`src/Allegiance.ClientCore.MonoGame/Content/Models/`,
including `repaired/`) and their textures
(`src/Allegiance.ClientCore.MonoGame/Content/Textures/ships/`) were generated
with [Meshy](https://www.meshy.ai) (Multi-Image-to-3D) from renders of the
original Allegiance ship models, then processed for the game (P510–P520,
P581/P581c). They're an optional alternative to the original models.

---

## Space objects on the black sky (P589/P589b/P589c)

Processed cut-outs (stars removed, feathered on black) committed under
`docs/reports/P589_assets/sprites/` · licences:
[`docs/reports/P589_assets/licenses/`](docs/reports/P589_assets/licenses/)

| Object | Credit | Licence | Source |
|---|---|---|---|
| Helix Nebula (NGC 7293) | NASA, NOAO, ESA, the Hubble Helix Nebula Team, M. Meixner (STScI), and T.A. Rector (University of Alaska Anchorage/NSF NOIRLab) | CC BY 4.0 | https://noirlab.edu/public/images/noao0307a/ |
| Whirlpool Galaxy (M51) | NASA, ESA, S. Beckwith (STScI), and The Hubble Heritage Team (STScI/AURA) | CC BY 4.0 | https://esahubble.org/images/heic0506a/ |
| Ring Nebula (M57) | NASA, ESA, and C. Robert O'Dell (Vanderbilt University) | CC BY 4.0 | https://esahubble.org/images/heic1310a/ |
| Pinwheel Galaxy (M101) | NASA, ESA, K. Kuntz (JHU), F. Bresolin (University of Hawaii), J. Trauger (Jet Propulsion Lab), J. Mould (National Australian Observatory), Y.-H. Chu (University of Illinois, Urbana), and STScI | CC BY 4.0 | https://esahubble.org/images/heic0602a/ |
| Lion Nebula (NGC 2392), Webb MIRI | Image: NASA, ESA, CSA, STScI; Image Processing: Alyssa Pagan (STScI) | CC BY 4.0 | https://science.nasa.gov/asset/webb/lion-nebula-miri-image/ |
| Lion Nebula (NGC 2392), composite | X-ray: NASA/CXC/SAO; Optical: NASA/STScI | Public domain (US Government work) | https://www.nasa.gov/image-article/beautiful-end-stars-life/ |
| Deep-field galaxies (233 sprites) | NASA, ESA, and S. Beckwith (STScI) and the HUDF Team (Hubble Ultra Deep Field) | CC BY 4.0 | https://esahubble.org/images/heic0406a/ |
| "Little Red Dots" | Procedural (generated in code) | n/a | n/a |

The Sombrero Galaxy (M104) was evaluated and dropped (field stars could not be
removed cleanly); its files have been removed from the repository.

## Giant sky object (P592)

| Object | Credit | Licence | Source |
|---|---|---|---|
| Milky Way, face-on (over sector Terra) | NASA/JPL-Caltech/R. Hurt (SSC/Caltech) | Public domain (NASA/JPL) | https://www.spitzer.caltech.edu/image/ssc2008-10a1-the-milky-way-galaxy |

Licences: [`docs/reports/P592_assets/licenses/`](docs/reports/P592_assets/licenses/)

## Optional HD sky tier (P577/P577b, `--sky hd`; off by default)

Licences: [`docs/reports/P577_assets/licenses/`](docs/reports/P577_assets/licenses/) · manifest
[`docs/reports/P577_assets/sky-hd.json`](docs/reports/P577_assets/sky-hd.json)

| Use | Credit | Licence | Source |
|---|---|---|---|
| Star field | NASA/Goddard Space Flight Center Scientific Visualization Studio, Deep Star Maps 2020 | Public domain | https://svs.gsfc.nasa.gov/4851/ |
| globe1: Veil Nebula | NASA, ESA, and the Hubble Heritage (STScI/AURA)-ESA/Hubble Collaboration | CC BY 4.0 | https://esahubble.org/images/heic0712a/ |
| globe2: Bubble Nebula (NGC 7635) | NASA, ESA, Hubble Heritage Team | CC BY 4.0 | https://esahubble.org/images/heic1608a/ |
| globe3: Westerhout 5 (Soul Nebula) | ESA/Hubble & NASA, R. Sahai | CC BY 4.0 | https://esahubble.org/images/potw2251a/ |
| globe4: Pillars of Creation (M16) | NASA, ESA/Hubble and the Hubble Heritage Team | CC BY 4.0 | https://esahubble.org/images/heic1501a/ |
| globe5 | NASA, ESA and Jesús Maíz Apellániz (Instituto de Astrofísica de Andalucía, Spain). Acknowledgement: Davide De Martin (ESA/Hubble) | CC BY 4.0 | https://esahubble.org/images/heic0619a/ |
| globe6: N103B | ESA/Hubble, NASA | CC BY 4.0 | https://esahubble.org/images/heic1707a/ |
| globe7 | NASA, ESA, and STScI | CC BY 4.0 | https://esahubble.org/images/heic2007a/ |
| globe8: "Mystic Mountain" (Carina) | NASA, ESA, M. Livio and the Hubble 20th Anniversary Team (STScI) | CC BY 4.0 | https://esahubble.org/images/heic1007a/ |
| globe9: Horsehead Nebula (IR) | NASA, ESA, and the Hubble Heritage Team (AURA/STScI) | CC BY 4.0 | https://esahubble.org/images/heic1307a/ |
| globe10: Westerlund 2 | NASA, ESA, the Hubble Heritage Team (STScI/AURA), A. Nota (ESA/STScI), and the Westerlund 2 Science Team | CC BY 4.0 | https://esahubble.org/images/heic1509a/ |

HD planets use the native planet art (no external imagery).

## Enhanced effects (P546)

CC0 flipbook atlases (Unity free VFX image sequences), shipped in
`src/Allegiance.ClientCore.MonoGame/Content/Vfx/atlas/`. Licence table and texts:
[`src/Allegiance.ClientCore.MonoGame/Content/Vfx/README.md`](src/Allegiance.ClientCore.MonoGame/Content/Vfx/README.md).

---

**ESA/Hubble, ESA/Webb and NOIRLab imagery** is used under
[CC BY 4.0](https://creativecommons.org/licenses/by/4.0/); use of these images
does not imply endorsement by ESA, NASA, NOIRLab or any of their staff of this
project. **NASA imagery** is generally not subject to copyright in the US; it's
credited here per NASA's media guidelines. Images were processed (cropped, star
removal, background removal, feathering, rescaling) for use in the game.

### AMD FidelityFX Super Resolution 1

EASU edge-adaptive upsampling and RCAS sharpening are ported from
[AMD GPUOpen FidelityFX-FSR](https://github.com/GPUOpen-Effects/FidelityFX-FSR),
revision `a21ffb8f6c13233ba336352bdff293894c706575` (MIT, copyright 2021 AMD).
The full license is in `NOTICE`. This is a modern client addition (P643).

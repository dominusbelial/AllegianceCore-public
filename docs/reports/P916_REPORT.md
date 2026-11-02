# P916 — mesh texture orientation

## HD pack verdict

**The deployed pack does not need regeneration for this fix. No assets need
Meshy credits. No generation service was called or credits spent.** The private
HD checkout was read only.

The actual schema-3 release contains **348 atlases**. All **332 binary-native
atlases** derive from vertically reversed native rows; all **16 text-imported
image atlases** derive from file-row images. A blanket loader flip would break
those sixteen images. P916 binds the legacy binary-atlas maps with reversed V
at the mesh material boundary; imported-image maps bind directly. This works
for PNG and compressed BC7/BC3/BC5/BC4 mip chains, classic HD and PBR, without
rewriting maps or decompressing them. PBR tangent handedness follows the map's
coordinate system as well.

Evidence: [complete atlas audit](P916_assets/hd-orientation.json) and
[all-five-map spot checks](P916_assets/hd-map-samples.json). Every deployed
atlas had an actual input PNG available: **342 input PNGs matched reversed
binary rows byte for byte**, and **16 matched file rows byte for byte** (some
binary atlases have multiple model-named inputs). Every deployed binary
basecolor was closer to reversed native rows than to file rows; every imported
image basecolor was closer to file rows. This was measured from the actual DDS
files, not inferred only from a script comment.

`METHOD.md` describes the older Meshy pilot, not sufficient evidence for the
current release. I also read the root `manifest.json` credit/job ledger,
`pack-mapping.json` (138 mappings), `pack-mapping-all.json` (348 mappings), all
10 `native-verification.json` entries, and the ESRGAN export/derivation code.
The private bridge decodes with the old default, `glb.py` embeds those exact
pixels with unchanged UVs, and `export_atlases.py` preserves file rows only for
text imports. `esrgan_variant.py` upscales those inputs and derives the data
maps without a row reflection. The public `tools/hd-pack/build_pack.py` copies
PNG maps or resizes/compresses them through `bc_maps.py`, without reflecting
rows. Thirty actual DDS/source comparisons (five maps × pod, fighter, capital,
station, rock and text-imported station) confirm that orientation survives
packing. BC5 checks compare stored XY; black emission maps are symmetric and
cannot independently distinguish row order.

This verdict concerns texture orientation. It does not certify the artistic
quality of the older Meshy pilot, repair its neutral-black emission fallback,
or claim that generation preserved every authored detail. If a future pack is
rebuilt in canonical file-row order, it needs explicit orientation metadata or
a new format; silently replacing maps under the existing legacy schema would
conflict with its compatibility binding.

## Native rule and diagnosis

Re-grepped native source confirms the two separate paths:

- MeshGeo draws its existing vertex/index streams (`engine/geometry.cpp:311-314`).
  Device3D copies mesh U and V unchanged (`engine/device3d.cpp:2245-2246`).
  ImportImage hands its original data pointer and pitch to CreateSurface
  (`engine/modeler.cpp:723-733`). **A native mesh samples file row zero at V=0.**
- Image/decal geometry generates its own UVs. `Decal::GetVertices` puts V=1 on
  the upper corner (`engine/context.cpp:211-240`); `DrawImage3D` reverses its
  source rectangle Y (`context.cpp:659-660`). Screen-space `DrawImage` instead
  uses the upper-left origin and BitBlt (`context.cpp:553,579-584`).

The old decoder documentation conflated these paths. Its historical default
is retained; its documentation now calls that the legacy image/decal
convention. Codec logic and default values are unchanged. Native mesh loaders
explicitly use file rows. The fighter comparison independently reproduces
P915's finding: the old atlas places ring/panel details across the engine and
tail; the corrected image puts luminous centres on the nozzles and exposes
the authored ribbed centre panel.

Parity tracking: [P916 rules](../parity/p916-texture-orientation.csv), with the
superseded P915 UV row updated in [P915 effects](../parity/p915-client-effects.csv).

## Consumer inventory

The dispatch-base game had **23 default-flip decode calls**, including two
projectile-model bindings and two explosion bindings. The table accounts for
each; related explicit-file-row HUD loaders are listed below it. Native mesh
rows in this table share the MeshGeo/Device3D/ImportImage source chain above.

| Original consumer / calls | Kind and native path | Wrong before? / final handling |
|---|---|---|
| `NativeShipRenderer` / 1 | Hull and LOD meshes; native `effect/thinggeo.cpp:1038-1053,1501-1503` | Yes. Shared file-row mesh reader; all players and views. |
| `NativeStationTexture` / 1 | Station meshes, including imported PNG namespaces; `Igc/stationIGC.cpp:240`, `engine/modeler.cpp:623-674` | Yes. Binary and PNG rows now share file order. |
| `NativeAsteroidRenderer` / 1 | Native rock meshes; `Igc/asteroidigc.cpp:32-38` | Yes. File rows. |
| `Game1.Lifecycle`, `Game1.Rendering`, `ProbeRenderer` / 3 | Initial probe, per-type probe and per-material probe meshes; `Igc/probeigc.cpp:112-113` | Yes. File rows, existing alpha key retained. |
| `NativeMissileRenderer` / 1 | Missile meshes; `Igc/missileigc.cpp:50-56` | Yes. File rows, existing alpha key retained. |
| `NativeTreasureRenderer` / 1 | Cash, artifact and part meshes; `Igc/treasureIGC.cpp:174-179` | Yes. File rows; flag decals stay on image path (`:159-164`). |
| `ProjectileModelRenderer` / 2 | Model texture and per-material texture; `Igc/projectileIGC.cpp:53` | Yes. Both bindings use file rows. |
| `HullHitSparkRenderer` / 1 | Textured debris-bit mesh; native `effect/efgeo.cpp:795,842` / MeshGeo | Yes. File rows, alpha key retained. |
| `NativeSkyRenderer` / 1 | Environment globe MeshGeo; `WinTrek/WinTrek.cpp:3625-3630` | Yes. File rows; generated HD sky PNGs unchanged. |
| `AssetPreloader` / 1 | Shared model-material warm-up; native `WinTrek/trekigc.cpp:4912-4937` | Latent wrong mesh surface. Warmed material now uses file rows. |
| `NativeEffectTextures` / 1 | Mixed: particle/decal films and aleph funnel; `effect/particlegeo.cpp:61,125`; `effect/alephgeo.cpp:128-142,224-230` | Funnel wrong; image films correct. Explicit false only for the funnel; default image output identical. |
| `ExplosionAssets` / 2 | Film frames and shockwave image quads; `effect/explosion.cpp:161,198` | Correct decal convention. Explicit true, identical output. |
| `ProjectileSpriteRenderer` / 1 | Projectile/missile image quads; `Igc/projectileIGC.cpp:59`, `Igc/missileigc.cpp:60` | Correct decal convention. Explicit true. |
| `MineModelSource` / 1 | MineFieldGeo image field; `Igc/mineigc.cpp:103`, `WinTrek/trekigc.cpp:1934-1957` | Correct decal convention. Explicit true. |
| `ChaffModelSource` / 1 | Single decal; `Igc/chaffigc.cpp:42-46` | Correct decal convention. Explicit true. |
| `AmbientDustRenderer` / 1 | Dust image quads; `effect/debrisgeo.cpp:166-178` | Correct decal convention. Explicit true. |
| `PlanetPoster` / 1 | Poster square mesh; `effect/efimage.cpp:554`, `engine/geometry.cpp:568-571` | Yes. This image quad has top V=0; file rows now match its native square and its PNG fallback. |
| `MdlTexture2D` / 1 | Lens flare and starmap use `DrawImage3D`; `effect/efimage.cpp:698,745`, `WinTrek/sectormap.cpp:1045,1062`; moons use a non-native generated disc | Correct generated-image convention for flare/map; no authored native moon mesh UVs in the disc. Explicit true preserves these consumers. |
| `RockTextures` / 1 | Legacy imported/procedural sphere material with its own UV layout | No demonstrated native-UV defect: this is not the authored native rock mesh. Explicit true preserves this fallback; native rocks use `NativeAsteroidRenderer`. |

Already explicit **false**, unchanged: `GaugeTextures`, `IndicatorTextures`,
`LockTextures`, `StatusTextures`, `FeedbackTextures`, `TargetPaneTextures`,
`InventoryPaneTextures` (both icon and inventory image), `HudNativeTextures`
and `RadarIconTextures`. These are screen-space images, not authored mesh UVs.
Inventory's flat images keep their pixels; any 3-D inventory, hangar and target
preview uses the shared native model/material renderers.

Outside the game, three default callers were mesh consumers:
`tools/mdl-to-glb/Program.cs`, `tools/station-bay-proof/Program.cs`, and the probe
binding in `tools/hd2-proof/AtlasProof.cs`. All now use file rows. The GLB import,
texture verification and generated proof texture commands explicitly write
file rows too, so Blender export/import remains consistent with the game.
The general codec defaults remain compatible with old tools/tests. No golden
image or texture hash was edited.

The station-bay proof also compared PNG readback through `height - 1 - y`;
that expected-row reflection is removed so its proof follows the native file
rows. This is an assertion correction, not an artwork/golden modification.

The only existing mesh `1 - V` compensation was P915's
`NativeShipResolver.WithReversedTextureV` / `NativeShipGeometry.UprightTexture`,
gated by `ShipEffectRules.UprightHullTexture`. All three and their scoped tests
were removed. The new `1 - V` is solely the legacy-HD material adaptation,
shared across mesh families, plus the equivalent flowing-funnel binding.
Position, normal, winding, scale and native/depth buffers stay canonical.

## Implementation and scope

`NativeStationTexture.Decode` is now the shared mesh image reader, keeping its
historical class name. It supports both binary RGB565 and text-imported PNG
namespaces. Reusing it also permits ship material scopes such as
`cap501/beltersbaybmp` to use their real PNG instead of the previous binary-only
loader's untextured fallback. All existing per-family transparency/key behavior
is retained. Sky and model preloading explicitly request unreflected rows.

Planet posters are another confirmed exception to the broad "image quad" label:
`PosterImage` draws `Geo::GetSquare` with upper V=0, unlike `DrawImage3D` and
`DrawDecal`. They now share file-row loading, while flares, map overlays and
particle/decal films retain their existing rows.

`NativeMaterialParts` caches a legacy-HD classic vertex buffer and a PBR buffer
when necessary. All five PBR maps use the same coordinates; rebuilding tangents
also reverses bitangent handedness. This avoids large PNG staging flips and
BC7/BC5 decompression. Text-imported atlas parts within a mixed-material model
continue to use their direct UVs. Missing or rejected HD materials fall back to
the canonical native buffer; the orientation lookup is delayed until the HD
material has actually loaded. The aleph funnel applies the same adjustment to
its animated V, while its star-core decals remain unchanged.

P915 damage-smoke sizing and mounted-afterburner/nozzle gates remain intact and
are retested. Production behavior changes are in the game client; the only Core
source edits correct codec XML/comments. Server, simulation, wire, artwork and
the private pack are unchanged.

## Coverage and visual evidence

The [GPU corpus](P916_assets/corpus.json) checks actual production texture
uploads, including material scopes and LOD UVs: **447 hull records, 143 station
records, 9 native asteroid rows, 56 missile records, 6 projectile models, 53
probe records and 2 treasure models**, plus **8 sky domes, one debris-bit
model, 43 native planet textures and the aleph funnel**. It checks full uploaded pixels and
**745,724 triangle-centroid UV samples**. **714,124** sample different pixels
under the old orientation. The same audit run with the dispatch-base game DLL
fails immediately: `Wrong uploaded rows: hull ss20/ss20bmp`
([record](P916_assets/old-orientation-check.json)). This is a regression
that fails on the old implementation, not just a self-consistent decoder test.
The full check covers 725 model records and 341 family/atlas uploads, plus
43 planet uploads and the funnel. The one missing source is `utl22/ilpodbmp`; the audit lists it explicitly rather
than calling its untextured fallback a texture pass.

The real pod atlas test checks recovered non-black authored panels. Image
regressions compare mine, chaff and effect-film output with the unchanged
legacy image decode. Five real GLB export tests cover pod, fighter, capital,
station and rock atlases and unchanged mesh UVs. The tangent test verifies
that HD adaptation preserves geometry and reverses normal-map handedness.

All linked images were opened and inspected. Each comparison has **before on
the left, after on the right**, from actual 1280×720 Game1 backbuffers. Both
versions use the same rendering-only client fixture after real loopback
logon/launch and host pause: full hull health, fixed pose, Enhanced/Medium VFX,
HD off / real installed schema-3 HD / experimental PBR. Incidental particles
and labels can vary. The pod uses P915's 13-unit player zoom. Station and rock
fixtures use a tiny own pod at their centre as camera anchor. The station's
small target preview is an actual production target-pane draw.

| Scene | HD off, before/after | Classic HD, before/after | PBR, before/after | Visible observation |
|---|---|---|---|---|
| Own pod `utl203`, Modern | [pair](P916_assets/off-pod-modern.webp) | [pair](P916_assets/hd-pod-modern.webp) | [pair](P916_assets/pbr-pod-modern.webp) | Retains the already-correct P915 own-pod appearance after removing its special case. |
| Fighter `wc_bifig`, Modern | [pair](P916_assets/off-fighter-modern.webp) | [pair](P916_assets/hd-fighter-modern.webp) | [pair](P916_assets/pbr-fighter-modern.webp) | Bright nozzle centres and ribbed tail panel now align with the geometry. |
| Fighter `wc_bifig`, Classic | [pair](P916_assets/off-fighter-classic.webp) | [pair](P916_assets/hd-fighter-classic.webp) | [pair](P916_assets/pbr-fighter-classic.webp) | Same corrected material under Classic gauges. |
| Capital `cap20`, Modern | [pair](P916_assets/off-capital-modern.webp) | [pair](P916_assets/hd-capital-modern.webp) | [pair](P916_assets/pbr-capital-modern.webp) | Paired engine bells now sample their bright centres and rims; the upper hull panel no longer samples the shifted stripe. |
| Station `ss27`, Modern | [pair](P916_assets/off-station-modern.webp) | [pair](P916_assets/hd-station-modern.webp) | [pair](P916_assets/pbr-station-modern.webp) | Hull seams, emblems and orange borders move to their authored panel positions. |
| Rock `bgrnd03`, Modern | [pair](P916_assets/off-rock-modern.webp) | [pair](P916_assets/hd-rock-modern.webp) | [pair](P916_assets/pbr-rock-modern.webp) | Same rock geometry, with crater/surface patches sampling the proper rows. |

[Classic station target pane](P916_assets/preview-station-classic.webp): the
small model below the main view uses the same corrected station material.

The initial capital fixture revealed a pre-existing separate issue: the own
ship is absent from `_shipRadiusById`, so the draw loop's one-unit LOD fallback
turns a capital viewed at its normal chase distance into a far sprite. The
proof explicitly seeds that fixture's known radius to exercise the capital
mesh; both before and after receive the same value. Production LOD behavior
is unchanged. This is not evidence that P916 fixes that separate visibility
issue. The earlier blank capital captures were discarded.

Not visually captured: every remote pod/view combination, individual probes,
missiles/projectiles/treasure, debris bits, sky domes and aleph funnels, or
Blender's interactive UI. The corpus certifies the listed production uploads;
P800 exercises all hulls in hangar, near/mid/far flight and target previews.
Sky/funnel/bit paths also have production GPU upload checks; native planet
poster uploads are checked separately. Correct image/decal paths retain their
previous rows. PBR screenshots verify execution and material alignment, not
physical accuracy of the estimated data maps. Only local rendering was used;
no rig, mini or Mac was contacted.

## Validation

- Final `dotnet build AllegianceCore.slnx`: **0 warnings, 0 errors**.
- Final `tools/build-tools.sh`: **all 110 tools**, **0 warnings/errors**.
- **125 focused cases × 3**, all passed, **0 skipped**: both complete HD
  renderer classes, all 12 new task cases, retained P915 effect gates, station
  behavior and the P824 texture pipeline. The GPU corpus is included each run.
- **Station GPU proof × 3:** **81 unique models / 207 draw groups / 105 full
  texture readbacks** each time, all passed. The reflected PNG expectation is
  corrected to file order, as documented above.
- **P800 hull proof:** passed for all **447 hulls** across hangar, near/mid/far
  flight and target-pane renders; also isolated for the sweep timing check.
- Server native-AOT publish (required by the lane after the Core comment-only
  edits): passed, with **no IL2026/IL3050 warnings**.
- Glossary paths, registered parity scorecards, sidecar CSV/LF checks, Python
  audit syntax, added-code hygiene and `git diff --check` passed. No service,
  port, product environment variable or training lesson rule was added.

The single Fast sweep is retained as **FAIL**: **12,949 executions**, **12,933
passed / 13 failed / 3 skipped**, **570.993 s**, all **nine infrastructure
checks passed**, and three executions exceeded its calibrated ten-second limit.
The three skips are the pre-existing optional `MeshyPackTests`; artwork
checks and new task tests had **0 skips**. Two failures exposed a task regression in HD metadata lookup: `ReadExactly`
assumed every hash-validated source was at least four bytes. Existing synthetic
HD tests use shorter non-MDL sources. The correction classifies only a complete
binary MDL signature as reversed, preserves the existing convention otherwise,
and adds four short/non-MDL source guards. The final three-run cohort includes
both complete HD renderer test classes
and the four new compatibility guards; all pass. Non-task failures and timing violations
are isolated three times and ledgered in `docs/TEST_FLAKES.md`. The whole sweep
is not repeated, as the brief requests one run.
The [isolation results](P916_assets/isolation-summary.json) cover all twelve
non-task failed/timed method families. Five bundle-path cases and the default
profile-write fixture retain their sandbox obstructions; the profile control
passes 3/3 with writable task settings. The existing defend-target case fails
3/3. Auto-seat timing fails/passes/passes, and join-burst ship status
fails/fails/passes. Held-Enter and side elimination pass 3/3. P800 and warp-exit
assertions pass 3/3 below ten seconds. These unresolved non-task outcomes remain
ledgered; no unrelated implementation or test assertion was changed.


Changes are left in the working tree, with **no commits or pushes**.

Raw logs/TRX/PNG captures are under `.scratch/p916/evidence/` and
`.scratch/p916/captures/`, within the authorized worktree. The dispatch-base
game DLL is retained at `.scratch/p916/evidence/dispatch-game.dll` for the
regression control; temporary build copies, AOT output, caches and settings
were removed. The lane's usual
external evidence directory is outside this sandbox's writable roots.
The 19 inspected WebP comparisons/previews total **828,864 bytes**; all small
JSON evidence keeps the asset directory below both the brief's 3 MB limit and
the lane's 2 MB limit. [Screenshot hashes](P916_assets/screenshots.json) and
[validation summary](P916_assets/validation.json) retain the measurements. New media still needs the normal human review before any later
push; this lane makes no commit or push.

## Native-intent footer

**Design:** native mesh UVs and image-generated UVs are distinct contracts;
texture rows must match the contract at each binding. Existing team markings,
authored glow, geometry and gameplay rules remain authoritative.

**Era constraint:** the native engine mixed D3D mesh streams, lower-left
image/decal coordinates and screen-space BitBlt surfaces. A single undocumented
"3-D flip" did not describe those different paths.

**Bug:** the port generalized an image/decal row reversal to native mesh
atlases. P915 compensated for one own-pod view; P916 fixes the shared material
convention and removes that hull/view condition.

**Modern better approach:** keep canonical native UVs and top-left/file-row
images, declare orientation in future pack metadata, and adapt old pack formats
at material binding. Validate real uploaded texels and asymmetric native atlas
features; extra ambient light cannot repair sampling the wrong rows.

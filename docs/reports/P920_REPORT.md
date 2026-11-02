# P920 — TextureCraft one-ship pilot

**Verdict: useful local material-authoring tooling, but this pilot does not beat the current HD pack. Do not replace all hull textures or spend Meshy credits to fix orientation.** TextureCraft successfully baked and exported a repeatable material on `wc_bifig`, preserving its recognizable native colours and layout. Close-up wear and highlights are a modest stylistic alternative to native, not a demonstrated overall improvement. The existing HD atlas retains sharper authored detail. This evaluation changes tools and documentation only; the game, native artwork and real HD pack are untouched. Nothing was committed or pushed; no generation service, rig, mini or Mac was contacted.

P916 explicitly found **no orientation-related assets needing regeneration**, including its 332 binary and 16 imported-image atlases. Its older Meshy quality/emission reservations were not a regeneration list. P920 supplies no reason to buy replacement textures for that fix. P919's offline model workflow was followed: GLB is an authoring bridge; the game continues loading its original MDL/CVH and samples replacement atlas maps.

## Actual tool and control discovery

Installed binaries report `TextureCraft 0.2.0 (e01dac2 2026-11-01)`. Local README, ATTRIBUTION, MIT/Apache licences and the [pinned control protocol](https://github.com/storytold/texturecraft/blob/e01dac2/docs/control-protocol.md) were read; engine/export/tangent source at that revision was also inspected. This is a version-specific result, not certification of the current development branch.

The desktop command `texturecraft --empty --control PORT` worked on the existing local display. Its log contained `failed to get driver name for fd -1`, `failed to retrieve device information`, `ZINK: failed to choose pdev`, and `failed to create dri2 screen`, but then announced its control server. An actual TCP connection authenticated and returned `pong`; `command.list` and `session.inspect` also succeeded. These warnings were not treated as an application failure or evidence of hardware acceleration.

Control is loopback TCP, newline-delimited JSON: `{id, method, params}` requests; `{id, ok, result}` replies. First call is `auth` with a token read from the task's temporary token file. `TEXTURECRAFT_NO_PREFS=1` isolated desktop settings; the short-lived process was stopped after discovery. No token is published here.

Without DISPLAY and WAYLAND_DISPLAY, the real desktop binary failed with `winit EventLoopError` and `neither WAYLAND_DISPLAY nor WAYLAND_SOCKET nor DISPLAY is set`. Its help offers no desktop headless switch. The installed **`texturecraft-cli serve --read-root DIR --write-root DIR`** is the proper headless alternative: the same request envelope on stdio, without TCP authentication. All material runs used it, with **`bake.run {cpu:true}`**; the reply explicitly reported `tracer:["CPU"]`. No GPU bake or TextureCraft viewport performance is claimed.

Commands exercised include `mesh.import`, `resource.import`, `layer.add.image`, `layer.add.fill`, `layer.blend`, `mask.add.black`, `effect.add.generator`, `bake.run`, `textures.export`, `project.save`, `mesh.export` and inspection. The real command catalogue also exposes **`file.exportTextures`**; `textures.export` is the headless server's file-root-aware wrapper. `texturecraft-cli commands` prints a text catalogue, not JSON. The script checks every reply's `ok` field and records timings, parameters and results.

## Geometry, UVs, orientation and limitations

Export: **one mesh, 633 triangles, 497 vertices**, atlas **`fig20bmp`**, native texture **256×256**, 12 mounts, six lights and one collision hull. The existing exporter/native roundtrip passed with **zero mesh float difference and identical indices**. No unwrap, retopology, scale or geometry import into the game occurred.

TextureCraft reported `uvsGenerated:false`, no out-of-range UVs, **50 zero-UV-area triangles**, and `overlapRatio:1.523638` (its UI labels that “152.4% overlap”; this is its metric, not a fraction of the hull's surface). Comparing its exported GLB back to the input found the same position/UV triangle multiset and triangle count. TextureCraft repaired zero-length source normals at four triangle corners to `(0,1,0)` during import; valid normals differed only by normalization/float rounding. Its re-exported mesh is diagnostic only and is never installed. Shared/mirrored atlas islands force different surfaces to share wear; zero-area triangles cannot receive meaningful per-surface painted detail. These constraints remain authoritative rather than being “fixed” by changing UVs.

Native meshes and GLB use **file row zero at V=0**, with original UVs. Schema-3 binary-source HD maps use reversed rows, and P916 reverses V and rebuilds the tangent basis when binding them. The script therefore **reflects all outgoing atlas rows once**, without editing geometry or flipping its UVs. Native nozzle centres, green strips, ribbed rear panels and wing borders stay in the same places in the comparisons below. No new gross orientation error is visible; this is not a claim that every seam is clean.

Normal convention was checked against `PbrVertex.Build`, `NativeMaterialParts` and `NativePbr.fx`, plus TextureCraft's MikkTSpace/export code. TextureCraft computes its OpenGL bitangent toward decreasing image V; the game rebuilds its bitangent after the schema-3 V reflection. **OpenGL output + reversed image rows + unchanged green** is the matching combination here. A tilted-normal diagnostic exported `(153,179,241)` in OpenGL and `(153,76,241)` in DirectX across 147,285 fully covered pixels: red/blue identical, green sums exactly 255. Fractionally covered boundary pixels are attenuated by the exporter and were excluded from that convention check. The actual self-bake is neutral `(128,128,255)` throughout: there is no high-poly detail or height layer, so this pilot does **not** demonstrate improved normal detail or use screenshots to claim a directional bump test.

Two practical export limitations needed adaptation:

- Importing this textured GLB created an empty paint layer; it did not install its embedded base colour into the layer stack. The script extracts the exact embedded PNG and adds it as an image layer.
- Composite PNG export masks to rasterized UV coverage, including fractional coverage in RGB/data values. Direct replacement would lose authored texels sampled by degenerate UVs and corrupt data-map edge values. The script applies **wear export minus unworn export** over the complete bilinearly enlarged native atlas, removes coverage attenuation from scalar maps, and uses the exported mesh-map normal with its neutral uncovered texels. Black emission satisfies the existing schema; it does not restore authored emissive semantics.

No high-resolution detail is invented. AO is baked for generators but has no separate runtime slot in this pack. A single low-poly self-bake cannot separate competing surfaces in overlapped UV islands. The 512-pixel/16-ray bake is an inexpensive pilot setting, not a final quality standard for capital ships.

## Settings and private pack

The repeat script is [tools/p920-texturecraft.py](../../tools/p920-texturecraft.py), 173 lines. It accepts another **single-atlas binary-native hull** and refuses existing output directories, incompatible schema/sizes and stale source hashes. Multiple-material hulls deliberately need a future extension.

| Setting | Exact value |
|---|---|
| Working texture set | 1024×1024, native PNG image base |
| Base material | roughness 0.62, metallic 0.18; colour/normal/height disabled |
| Mesh bake | 512²; normal_base, ambient_occlusion, curvature, thickness; 16 rays; AA 1; dilation 8; CPU |
| Edge wear | `#999891`; roughness .48; metal .40; edgeWear level .32; opacity .12 |
| Dust | `#746b59`; roughness .82; metal .04; dirt level .35; opacity .10 |
| Grime | `#292a24`; roughness .88; metal .02; dirt level .25; opacity .14 |
| All three masks | black mask + generator; softness .25; noise .12; seed 920; normal/height disabled |
| Export | PNG 8-bit, padding none, OpenGL; black emission; schema-3 row adaptation |
| Packing | existing HD pack builder, one worker; full mip chains; existing reference material-tuning object |

The resulting private pack is `.scratch/p920/final-e/pack`; repeat proof is `.scratch/p920/final-f/pack`. Only `fig20bmp` is present. The installed reference pack's exact resolution/schema are matched:

| Map | Dimensions | Codec | Space |
|---|---:|---|---|
| Base colour | 1024² | BC7 + separate BC3 fallback | sRGB |
| Normal XY | 512² | BC5 | linear |
| Metallic | 512² | BC4 | linear |
| Roughness | 512² | BC4 | linear |
| Emission | 512² | BC7 + separate BC3 fallback | sRGB |

The manifest contains the current native atlas SHA256 and hashes of all encoded maps; material residency is 2,446,784 bytes including mips. The directory and both generated archives have the same tuning metadata. The game was loaded with `ALLEGIANCE_HD_PACK_PATH` pointing at this directory, and its logs confirm the schema-3 catalogue and `fig20bmp` uploads. Missing other atlas entries use the normal native fallback.

## Viewed evidence and judgement

All linked WebPs were opened and inspected. Each comparison orders **native, current HD, TextureCraft** from left to right. Chase and target evidence comes from actual 1280×720 Game1 backbuffers following real loopback join/launch, then a paused host and fixed client pose/lighting. The target is a client-only fighter at `(0,5000,-1000)`, selected by the production HUD. Incidental particles/glow differ; this is visual material evidence, not a frame-time benchmark.

[Modern chase](P920_assets/chase-modern.webp) · [Classic chase](P920_assets/chase-classic.webp)

Chase: the ship remains immediately recognizable. TextureCraft slightly dirties/reduces contrast on the bright panels; it cannot resolve the native atlas's coarse lines. The current HD pack gives cleaner, sharper wing edging and rear panel detail. The restrained wear is hard to distinguish at ordinary chase scale; PBR specular changes are more visible than the procedural colour work. Native uses its own classic lighting; current HD and TextureCraft select PBR.

[Modern hangar preview](P920_assets/hangar-modern.webp) · [Classic hangar preview](P920_assets/hangar-classic.webp)

These 640² captures call **Game1's actual `DrawDockedHullPreview`** in its graphics context, with spin fixed at 0.9 radians, before the shop overlay. The normal hangar backbuffer's shop panels hid the hull, so those UI images were rejected as material evidence. These are production hangar-renderer captures inside the real client, not an independently recreated preview or proof of an improved hangar UI. Close up, the current HD material preserves finer panel lines; TextureCraft adds mild mottling/wear but also exposes the limits of the original atlas and overlapped UVs. Neither added detail nor better seam quality is demonstrated.

[Modern target pane](P920_assets/target-modern.webp) · [Classic target pane](P920_assets/target-classic.webp)

Target-pane comparisons select **classic HD**, where replacement base colours work. Existing `TargetModelPreviewRenderer.RenderShip` omits the model key; `NativeMaterialParts.Draw` requires that key for PBR, so selecting PBR falls back to native there. This was recorded, not fixed. At the pane's small size, wear contributes little; the magnified crop repeats the actual pixels without adding detail.

[Canonical-row atlas comparison](P920_assets/atlases.webp) · [Four actual baked maps](P920_assets/bakes.webp)

The atlas comparison makes the decision clearer than distant chase frames: TextureCraft retains the native design rather than creating the HD pack's finer lines. Its grey wear also lifts some originally dark engine/rear-panel areas, reducing their authored contrast; that is a reason to lower or hand-mask the wear before any release. Curvature/AO/thickness have useful signals, but the normal bake is flat, as expected without high-poly source detail. No all-hull or complete seam-quality certification is implied.

The eight inspected WebPs total **281,626 bytes**; the complete evidence directory is **286,185 bytes**, well below 2 MB. [Sanitized measurements and image hashes](P920_assets/measurements.json) record the final map hashes, repeat timings, UV report and normal diagnostic.

## Repeatability, costs and recommendation

The final recipe was run twice from fresh exports. Both runs produced identical hashes for **all seven DDS files**, including BC3 fallbacks. End-to-end export, headless processing, compression and archive creation took approximately **2.7–2.9 seconds per run** on this workstation; the actual four-map CPU bake took about 0.13 seconds. These are this tiny hull's measurements, not estimates for every hull. The initial discovery, coverage/normal adaptation and client evidence took substantially longer than pressing “run”.

The installed coverage catalogue has **179 distinct ship models/atlases**; P916's 348-atlas total also includes other object families. At this pilot's cost, 179 comparable single-atlas jobs would take about **nine minutes of computation**, or about 17 minutes for 348 comparable atlases, serially. That is an extrapolation; complex geometry, more rays, larger bake sizes and multi-atlas support can increase it considerably. Export/import, hashing, CPU bake, seeded generators and packing can be batched. Material judgement, UV-conflict inspection, emissive decisions and seam repair cannot be certified by this one script. Budget roughly **5–15 minutes of visual review per model** (15–45 hours for 179), plus several hours to harden multi-material processing; that is a planning estimate, not measured throughput.

**Recommendation:** keep TextureCraft as a zero-credit procedural authoring option for a small, artist-reviewed selection needing dirt/edge wear or new scalar maps. Do not mass-replace the HD set with this low-resolution self-bake. Do not regenerate anything for P916 orientation. If a specific older Meshy result is judged artistically damaged, first try its native atlas plus a local TextureCraft material and compare it individually; this pilot supports that cheap experiment, not a blanket substitute for generative restoration. Spending expiring credits is not justified by this result alone.

## Licences and shipping

The installed [ATTRIBUTION at this revision](https://github.com/storytold/texturecraft/blob/e01dac2/ATTRIBUTION.md) states that built-in smart materials, patterns, environments, brush alphas and meshes are procedural code by TextureCraft contributors under **MIT OR Apache-2.0**, and are not derived from another product's presets or images. This recipe uses that procedural engine plus the existing native image, without external material downloads. Its attribution lists app artwork/screenshots under MIT/Apache, Phosphor icons under MIT, and several UI-font licences including OFL, Ubuntu Font Licence and Bitstream Vera; those UI assets are not baked into these maps. ArtCraft brand marks have separate restricted brand terms and are not included.

TextureCraft's procedural content is therefore a viable permissively licensed input for a future distributed material, retaining applicable copyright/licence notices. The underlying Allegiance artwork still needs its existing distribution rights; using this tool grants no new rights to it. This evaluation does **not** approve these maps for shipping: quality, UV/normal edge treatment, emissive interpretation and native-art attribution still need review. No full material pack or uncompressed private texture was added to the public tree.

## Exact repeat commands and checks

Set `HD_PACK` to the existing unpacked schema-3 reference directory. Paths below are caller-provided or worktree-relative; the reference is read only. Python needs Pillow, numpy and `ispc_texcomp==1.0.1` (a task-local Python 3.12 environment was used).

```bash
. ./local.env
export DOTNET="$HOME/.dotnet/dotnet"
"$DOTNET" build tools/mdl-to-glb/mdl-to-glb.csproj -p:UseSharedCompilation=false
"$DOTNET" build tools/p916-texture-proof/p916-texture-proof.csproj -p:UseSharedCompilation=false
python tools/p920-texturecraft.py --artwork "$ALLEGIANCE_ARTWORK_PATH" \
  --reference-pack "$HD_PACK" --hull wc_bifig --out .scratch/p920/replay

export SDL_VIDEODRIVER=offscreen ALSOFT_DRIVERS=null
export XDG_CONFIG_HOME="$PWD/.scratch/p920/settings"
export XDG_DATA_HOME="$PWD/.scratch/p920/data"
export ALLEGIANCE_HD_PACK_PATH="$PWD/.scratch/p920/replay/pack"
"$DOTNET" tools/p916-texture-proof/bin/Debug/net10.0/p916-texture-proof.dll \
  .scratch/p920/captures modern fighter texturecraft pbr
"$DOTNET" tools/p916-texture-proof/bin/Debug/net10.0/p916-texture-proof.dll \
  .scratch/p920/captures modern fighter hangar-texturecraft pbr hangar
"$DOTNET" tools/p916-texture-proof/bin/Debug/net10.0/p916-texture-proof.dll \
  .scratch/p920/captures modern fighter target-texturecraft hd target
```

Repeat with `classic` for the other HUD style. For native, select `off`; for the reference, set `ALLEGIANCE_HD_PACK_PATH="$HD_PACK"` and use `pbr` for chase/hangar or `hd` for the target pane. Tags only name the output; preserve separate tags for each comparison. Use a new `--out` directory for every material run. `commands.json`, `summary.json`, `pilot.tcraft`, exported maps, source GLB/sidecar and packed outputs make each run inspectable.

Validation: solution build with `-p:UseSharedCompilation=false` **0 warnings, 0 errors**; exporter roundtrip passed; repeat DDS hashes matched; Python syntax, glossary links and `git diff --check` passed. Fast, full suites, training and timing tests were not run, as the brief forbids them. No parity rows changed. Task-local raw logs, diagnostics and captures remain under `.scratch/p920/`; only compressed reviewed evidence and sanitized measurements accompany this report.

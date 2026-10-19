# HD-4 — compressed HD materials

2026-10-19. Codex lane; no commits or pushes. Changes remain in the working tree.

Schema 3, direct compressed DesktopGL uploads, class resolution limits, pre-generated mips, screen-size mip selection, budget accounting, and BC3 alternative releases are implemented. Schemas 1/2 and PNG authoring remain supported. The actual 138-atlas ESRGAN pack was rebuilt and exercised by the client.

**Acceptance limits:** the requested default resolution policy meets **<40 MiB per material**, but its BC7 download is **1,557.38 MB**, above the 800 MB target. The explicit compact policy meets the download target at **405.58 MB**, with reduced maximum resolution. Hardware FPS acceptance and native Windows/macOS verification remain open. Local captures use software GL and fail the 5% performance target. The rig/221/200v200 work was skipped as directed by the lane rules. One local skirmish process also requires parent-lane cleanup, described below.

## Implementation

- `HdPack` accepts schema 3 atlas entries and DDS map paths while retaining lazy native atlas SHA256 checks, shared atlas identity, negative-result caching, and incomplete/stale/native fallback behavior.
- `tools/hd-pack/build_pack.py --format bc` writes DDS DX10: BC7 sRGB basecolor/emission, BC5 UNORM normal XY, and separate BC4 UNORM roughness/metallic. Every map includes the complete mip chain. Color filtering uses linear-light sRGB conversions; normal filtering averages vectors and renormalizes. Tiny mip blocks repeat edge texels. No runtime CPU BC decoder exists.
- The default basecolor limits are capital/station 4096, small craft 2048, and missile/probe/projectile 1024. Other maps use half the class limit, which is necessary to keep a 4K material below 40 MiB. Sources are never enlarged. Shared atlases use the largest applicable class. `--resolution-policy` and `--workers 1..4` are documented; PNG remains the default authoring format.
- The catalogue exports capital classification from physical hull length ≥75 (an authoring policy) or the existing capital-threat/carrier capability classification. Native data/rules: `~/Development/Allegiance/src/Igc/igc.h:1780` (length), `:5383-5394` (threat classification), and `:736` (carrier). Class resolution thresholds themselves are new authoring policy, not native rules.
- MonoGame DesktopGL **3.8.5.1 has no BC4/5/7 SurfaceFormat values**. A narrowly scoped adapter obtains its existing GL texture handle and calls `glCompressedTexImage2D` with the original blocks. MonoGame continues owning binding, samplers, and disposal. The wrapper is never passed to a software texture decoder or `GetData`/`SetData`. Its internal handle dependency is pinned and fails to native when unavailable; updating MonoGame requires retaining the real GL regression.
- The renderer validates the bounded DDS header, format/color contract, 2D/single-array shape, full mip chain, and exact payload length before allocation. It estimates projected mesh diameter, chooses a mip tail at roughly two texels per screen pixel with a 64-texel floor, seeks past larger levels, and uploads only that tail. Close instances promote shared atlases immediately; detail holds for two seconds before demotion. Existing LRU/idle fallback is retained, counting actual compressed blocks and conservative BC4 placeholder staging. Unlit BasicEffect users receive a separately budgeted UNORM basecolor view so encoded sRGB samples are preserved.
- BPTC capability is checked. Each build produces a normal BC7 zip and a **standalone `.bc3.zip` alternative**, containing BC3 color maps under the same primary filenames plus BC5/BC4 data maps. Install one archive. The authoring directory contains both color variants for testing. An unsupported BC7 installation returns to native with an instruction to install the BC3 release.

The encoder is **ispc_texcomp 1.0.1**, MIT bindings to Intel's MIT ISPCTextureCompressor, with the `alpha_fast` BC7 profile. Linux CI uses Python 3.11+, Pillow, NumPy, and the published native wheel; no proprietary SDK, GPU, or Wine is required to encode. Requirements, policy examples, commands, limitations and upstream licenses are in [the builder documentation](../../tools/hd-pack/README.md). Generated textures and release archives are ignored external evidence, not repository assets.

Primary format references: [BPTC/BC7](https://registry.khronos.org/OpenGL/extensions/ARB/ARB_texture_compression_bptc.txt), [RGTC/BC4/5](https://registry.khronos.org/OpenGL/extensions/ARB/ARB_texture_compression_rgtc.txt), [pinned MonoGame surface formats](https://github.com/MonoGame/MonoGame/blob/v3.8.5.1/MonoGame.Framework/Graphics/SurfaceFormat.cs), [encoder bindings](https://github.com/K0lb3/ispc_texcomp_py), [Intel encoder](https://github.com/GameTechDev/ISPCTextureCompressor).

## Actual pack results

Inputs: `~/Development/AllegianceCore-hd/variants/esrgan`, its `pack-mapping.json`, current `~/Development/Artwork`, and a freshly exported PCore001 catalogue. All **138 mapped atlases** are present; they cover 138 of the catalogue's 350 referenced stems. Other stems retain native rendering.

| Release | Basecolor limits, capital / small | BC7 download, decimal MB | BC3 download, decimal MB | Maximum full material, MiB |
| --- | --- | ---: | ---: | ---: |
| Requested defaults | 4096 / 2048 | 1,557.38 | 865.67 | 37.3334 |
| Explicit compact override | 2048 / 1024 | 405.58 | 229.85 | 9.3334 |

All downloads are below the brief's 2 GB release-file limit. **Only the compact override meets <800 MB.** It is not a claim that the default 4K/2K pack met that target.

| Actual class | Atlases | Default per material, MiB | Default total blocks, MiB | Compact per material, MiB | Compact total blocks, MiB |
| --- | ---: | ---: | ---: | ---: | ---: |
| Capital / large hull | 49 | 37.3334 | 1,829.3383 | 9.3334 | 457.3383 |
| Small craft | 89 | 9.3334 | 830.6757 | 2.3334 | 207.6757 |

This ships-only input contains no station/projectile-class atlas rows. Class policy coverage for those kinds is tested synthetically. All full-resolution default materials together total 2,660.014 MiB; compact materials total 665.014 MiB. They do **not** all fit simultaneously at maximum detail in 512 MiB. Screen-size mip tails and the existing eviction/native fallback enforce residency. For comparison, a single distant regression material occupies **21,952 bytes**, versus **1,398,208 bytes** at the closer tested footprint; actual close-up client materials occupied **9.33 MiB**.

During the build, the catalogue's capital policy was corrected to include large hulls without threat bits. Unaffected encoded atlases were retained; the affected class changes were re-encoded with the same builder codec. The compact release drops the pre-generated top mip levels to the limits in [compact-policy.json](../../tools/hd-pack/compact-policy.json); it records the default zip's SHA256 as provenance. The full commands/finishing script, original/corrected manifests, and class-change list are retained in raw evidence. A fresh source build is:

```sh
python3 tools/hd-pack/build_pack.py --format bc --workers 4 \
  --input "$HD_SOURCE/variants/esrgan" --mapping "$HD_SOURCE/pack-mapping.json" \
  --artwork "$ALLEGIANCE_ARTWORK_PATH" --core "$ALLEGIANCE_ARTWORK_PATH/PCore001.igc" \
  --output "$HD_OUTPUT/allegiance-hd-ships-bc"
# Add --resolution-policy tools/hd-pack/compact-policy.json for the compact limits.
```

[Exact sizes and zip hashes](HD4_assets/pack-sizes.json), [all per-material resolutions/residency](HD4_assets/materials.json), and [archive audit](HD4_assets/archive-audit.json) are small evidence. Each of the four archives passed all **690** map hash, codec, dimensions, mip count, payload-size and material-byte checks.

Local release files (ignored, not for the WIP commit):

- [Default BC7](/home/domingo/allegiance-codex-hd4/.hd4-evidence/allegiance-hd-ships-bc.zip)
- [Default BC3](/home/domingo/allegiance-codex-hd4/.hd4-evidence/allegiance-hd-ships-bc.bc3.zip)
- [Compact BC7](/home/domingo/allegiance-codex-hd4/.hd4-evidence/allegiance-hd-ships-compact.zip)
- [Compact BC3](/home/domingo/allegiance-codex-hd4/.hd4-evidence/allegiance-hd-ships-compact.bc3.zip)

## Real client visual and performance evidence

The production DesktopGL client joined an owned local small skirmish, selected the hull through normal buy/launch automation, used the chase camera and captured its real **2560×1440** backbuffer. These are close views of three ships, not fleet-overview or synthetic material renders. The HD captures loaded schema 3 and logged actual compressed atlas uploads. Their source PNGs were only losslessly recompressed for repository evidence; all decoded pixels and dimensions were checked identical.

| Ship | Hull / model | Native / HD off | Compressed HD on |
| --- | --- | --- | --- |
| Heavy interceptor | 481 / `wc_ichvy` | [Off](HD4_assets/interceptor-off.png) | [On](HD4_assets/interceptor-on.png) |
| Advanced fighter | 488 / `wc_icadv` | [Off](HD4_assets/fighter-off.png) | [On](HD4_assets/fighter-on.png) |
| Heavy bomber | 489 / `wc_icbmb` | [Off](HD4_assets/bomber-off.png) | [On](HD4_assets/bomber-on.png) |

![Compressed HD heavy interceptor, real 1440p chase camera](HD4_assets/interceptor-on.png)

The sandbox could not open the hardware graphics device. X11 and offscreen GL both reported **llvmpipe (LLVM 21.1.8, 256 bits), OpenGL 4.5 Compatibility, Mesa 26.0.8**; X11 additionally logged failure to create an iris/DRI3 hardware screen. SDL offscreen backbuffer captures omitted the hulls, so the evidence above was recaptured through X11. Render-target GL regressions passed in both contexts.

The same client captures provide honest, non-acceptance **software-renderer** measurements. FPS below is frames divided by summed frame duration in the fixed 5.0–12.5-second telemetry window, including capture stalls; the shared skirmish continued between runs. These are not an isolated hardware performance gate.

| Close-up case | HD off FPS | HD on FPS | Change |
| --- | ---: | ---: | ---: |
| Interceptor | 66.50 | 33.48 | −49.66% |
| Fighter | 65.65 | 40.01 | −39.06% |
| Bomber | 72.75 | 41.29 | −43.24% |

The **within-5% target is not met in this environment**. Compression removes the large PNG decode/residency problem; it does not establish PBR shading parity on a CPU software renderer. Hardware rig/skirmish FPS acceptance remains for Claude's rig lane, explicitly excluded here. [Machine-readable measurements](HD4_assets/client-perf.json) and all CSVs/logs/capture scripts remain in raw evidence.

## Validation

- Whole-solution build: **0 warnings, 0 errors**.
- Required Linux x64 server Native AOT publish: passed; **no IL2026/IL3050** diagnostics.
- Final affected .NET tally: **26 tests ×3 alone = 78 passes**, including schema 1/2 compatibility, schema 3 guards, DDS validation/budgets/mips, shader freshness, the existing nine atlas renderer kinds, and compressed real GL rendering. [Individual results](HD4_assets/targeted.json).
- Builder: **10 tests ×3 alone = 30 passes**, including real BC encoding, scalar/normal channel packing, linear-light color mips, standalone BC3 zip metadata, deterministic primary/alternative zips, unsafe input and existing authoring checks. [Results](HD4_assets/python-targeted.json).
- Final fast tier: **8,780 cases; 8,777 passed, 3 skipped** for absent optional Meshy assets; no timing offenders. [Summary](HD4_assets/fast-summary.json). An initial attempt had five unrelated path-fixture failures because workspace TMPDIR inherited the repository's `local.env`. All nine path-fixture cases passed in three standalone reruns using a directory-handle alias to the same scratch storage, then the corrected full fast run passed. This was an environment isolation issue, not classified as a load flake.
- Real GL regression: actual and synthetic DDS uploads, BC7/BC5/BC4, forced BC3, standalone BC3 primary names, unlit sRGB pixel preservation, mip-tail selection, promotion, toggle/native parity and over-budget fallback passed. The rebuilt default and compact packs both passed the real GL draw proof.
- Linux GL verified; Windows GL and macOS were **not available**. Their shared DesktopGL path was reviewed, and BC3 fallback was executed on Linux; this does not substitute for native OS verification.

## Evidence and cleanup

The sandbox rejected writes to both `~/allegiance-scratch/HD4/` and `~/allegiance-evidence/HD4/` as read-only. Task scratch therefore used the ignored workspace `.hd4-scratch`; retained archives and raw evidence use ignored `.hd4-evidence`. No task scratch was placed in `/tmp`. Wine shader compilation used a copied prefix and a temporary path adapter to redirect its otherwise read-only runtime directory into workspace scratch. Generated packs, Wine data and package caches are not staged or shipped.

Small evidence under `HD4_assets/` is **under 2 MB**. [Raw logs, TRX, source screenshots, scripts and manifests](/home/domingo/allegiance-codex-hd4/.hd4-evidence/raw) are retained outside the report asset set.

**Parent-lane cleanup is still required:** the owned local skirmish listener on **127.0.0.1:39275** survived termination of its launcher. Its process is outside later commands' sandbox PID namespace; signaling its peer PID handle was rejected with EINVAL. From the parent host namespace, identify and terminate that listener and its owned bot children, then remove the runtime directories listed in [cleanup.json](/home/domingo/allegiance-codex-hd4/.hd4-evidence/raw/cleanup.json). Only that linked active runtime is retained in `.hd4-scratch`; bulk build/encoder/Wine scratch is removed. No other agent's processes were targeted.

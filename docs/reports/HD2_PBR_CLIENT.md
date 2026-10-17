# HD-2 — optional PBR materials on native models

2026-10-17. Base: `33fc6df2d92cce775f29f9fa5a4a609d613b22c4`.
Codex lane: changes remain in the working tree; no commits or pushes.

## Delivered behavior

`ALLEGIANCE_HD_PACK_PATH` resolves through `ExternalPaths` in the same environment-variable → gitignored `local.env` order as the other optional external resources. `local.env.example` documents it. No private machine path or generated map is added to product code.

The loader reads the HD-1 schema-1 `manifest.json` (`models.<stem>.native.{Texture,TextureSha256,ModelSha256}`). A model is eligible only when its resolved native texture matches the entry and both the native texture **and model file** match SHA256. Missing maps, changed texture overrides, stale hashes, malformed metadata, missing shaders, corrupt PNGs and over-budget materials fall back to the existing native `BasicEffect` path. Results and failures are cached for the renderer lifetime; changing a pack on disk requires restarting the client. An absent pack emits one console log, with no player popup or chat message.

For HD-1 manifests with a `jobs.<model>:multiview` ledger, the job must be complete and its review metrics accepted. All ten current HD-1 pilot entries have `accepted: false`, so the actual pack currently retains native materials in normal play. Hand-authored/synthetic schema-1 catalogues can omit that generation ledger. The review harness uses a temporary copy of the manifest to inspect rejected pilot maps; it never edits the original manifest or enables rejected maps in production.

`NativeShipRenderer` supplies the model/texture identity in flight, inspector and docked hull previews. `HdMaterialRenderer` owns the reusable material service independently of hull identity or simulation type. Future station, missile, probe, mine and asteroid callers can supply the same tangent vertices/index buffer and their existing world transform; those object renderers are not switched to HD in this ships-first pilot. Native pods already use the ship path.

`PbrVertex` constructs an orthonormal tangent frame from the original positions, normals, UVs and indices, preserves mirrored UV handedness, and provides a finite fallback for degenerate UVs/normals. Tangent buffers are created only for HD draws, including when a reduced native LOD is selected. Geometry, winding, UVs, hardpoints, scale and orientation are retained.

`NativePbr.fx` uses tangent-space normals, red-channel metallic/roughness maps, GGX direct specular, Schlick Fresnel, diffuse energy reduction for metals, and emission. Base color/emission use a gamma-2.2 sRGB approximation; normal/metallic/roughness remain linear data. It shares sector key/back lighting and the existing P530 `(0.50, 0.52, 0.56)` HD readability floor. Native-only shading retains its original ambient and diffuse behavior. The shader shares the existing opaque/cloaked depth/blend states, feeds the existing scene/bloom target, and renders before FSR and the native-resolution HUD. It uses the existing LDR render targets; there is no environment cubemap/IBL or new HDR/tone-mapping pipeline.

## Native rule references

All paths below are in `~/Development/Allegiance/src/`:

| Retained rule | Native file:line |
| --- | --- |
| Hull model and texture override passed together to `Load` | `Igc/shipIGC.cpp:1412` |
| Hull radius is half its authored length | `Igc/shipIGC.cpp:1419` |
| Mesh scale is radius divided by mesh radius | `effect/thinggeo.cpp:1187` |
| Model orientation/scale followed by the conditional Y-axis π flip | `effect/thinggeo.cpp:1484`, `effect/thinggeo.cpp:1501` |
| Authored texture binding on geometry | `engine/geometry.cpp:1079` |
| Sector key/alternate colors, ambient and direction selected together | `WinTrek/WinTrek.cpp:3634` |
| Alternate light is `(0, 0.5, 1)`; bidirectional ambient is 0.25 | `Igc/clusterIGC.h:321`, `Igc/clusterIGC.h:331` |
| Directional contribution uses the normal/light dot product and selects key versus alternate | `engine/device3d.cpp:2315`, `engine/device3d.cpp:2320`, `engine/device3d.cpp:2335` |
| Sector passes enable both lights and lighting | `engine/device3d.cpp:814`, `engine/device3d.cpp:818` |
| Native specular is disabled; GGX is explicitly an optional modern material extension | `engine/device3d.cpp:775` |

## Controls

- Set `ALLEGIANCE_HD_PACK_PATH=/path/to/private/HD-pack` in the environment or `local.env`.
- **Ctrl+F6 → HD textures (add-on)** toggles the feature and saves `HdTextures` in the existing graphics settings JSON. It defaults on when a pack is supplied; an absent pack has no visual effect. The row reports incremental HD texture residency.
- In the hull inspector, **H** selects native/HD material review, **S** toggles simultaneous comparison, and **M** switches the single-hull material. Comparison places native left and HD right on the same original mesh. A missing/rejected HD entry displays native on the HD side too. K does not persist an art-source choice while reviewing HD.
- `--inspect-hd --inspect-hull 414` opens that comparison directly. Inspector material overrides are temporary and independent of the saved gameplay toggle.

## Texture memory and loading

PNG dimensions are checked before decoding (maximum 8192 per axis). Every map has a full trilinearly sampled mip chain. Color/emission mips average in linear light; normal mips renormalize their vectors; scalar maps average as data. Rectangular and odd dimensions retain edge pixels.

The material cache has a **512 MiB** GPU texture budget, with least-recently-used eviction after a resident has been unused for two seconds. Active materials stay resident when the visible working set exceeds the budget; excess models render native and retry after 0.5 s. This prevents repeated large uploads within every frame. Preflight includes all mip levels and reserves one additional map conservatively for the temporary decode surface, so staging is accounted for before upload. Native textures, geometry and scene/post targets are outside this incremental HD texture budget. Failed loads release partially uploaded maps. Disposal releases all resident HD maps and tangent buffers.

Loading is **lazy, synchronous on first visible HD use**, not background streaming. No PNG decoding, tangent construction or GPU material upload occurs at startup or for native-only draws. The synthetic five-map 256² material uses **1,747,620 bytes (1.67 MiB)** including mips. The current pilot uses three 4096² and two 2048² maps: **313,174,692 bytes (298.67 MiB)** for one model. All ten would total about **2.92 GiB** if loaded together, so the cache does not preload them. Under this budget, only one such pilot material fits at a time.

Measured first upload was approximately **23–48 ms** for synthetic maps and **4.7–5.7 s** for the pilot on this software-rendering host. The latter is a visible first-use stall; switching to a different large pilot material after the previous one becomes idle can repeat it after eviction. Simultaneously visible over-budget models use native fallback instead of repeatedly evicting each other. Warm GPU timings below exclude that load. The toggle and LRU bound residency, but this pilot does not establish hitch-free background streaming or a hardware performance budget.

## GPU timings and captures

Both proof and actual-client runs use P643/P687's asynchronous `GlFrameTimer` (`GL_TIME_ELAPSED`), keeping scene and post samples separate. **This sandbox has no usable hardware GPU: the adapter is `llvmpipe (LLVM 21.1.8, 256 bits)`**. X11 reports failure to load `iris` and falls back to software GL. These are measured GL-query milliseconds, not a claim about the workstation's real Intel GPU. Rig/221/200v200 runs are left to Claude per the brief.

The initial large SDL-offscreen client captures were cropped/blank despite nominal backbuffer dimensions. They are excluded from the accepted matrix. Final client captures/timings use a real X11 window at the requested 1920×1080 and 2560×1440 sizes. Small 321×181 headless shader tests still use SDL offscreen successfully.

<!-- CLIENT_MATRIX -->
Actual client: offline native `wc_icfig` hull inspector, saved HD toggle, 15 s per run, first 2 s and capture tail excluded. FSR cells include bloom and use Quality (1/1.5 scene dimensions). GPU scene columns are median / p95; post columns are medians, all milliseconds.

| Output | Pipeline | Native scene | HD scene | Scene Δ median | Native / HD post | Valid scene samples native / HD |
| --- | --- | ---: | ---: | ---: | ---: | ---: |
| 1920×1080 | Native resolution | 2.415 / 5.724 | 2.433 / 3.628 | +0.017 | 0.116 / 0.683 | 722 / 1186 |
| 1920×1080 | FSR Quality + bloom | 2.127 / 2.981 | 2.526 / 3.510 | +0.399 | 13.262 / 12.508 | 583 / 603 |
| 2560×1440 | Native resolution | 2.815 / 3.865 | 4.144 / 5.194 | +1.329 | 0.033 / 0.250 | 1139 / 1030 |
| 2560×1440 | FSR Quality + bloom | 2.674 / 3.350 | 3.075 / 3.769 | +0.401 | 18.499 / 18.424 | 369 / 384 |

All eight client runs exited 0. The cached synthetic material uses 1.67 MiB. [Client matrix, frame/CPU statistics and valid-sample counts](HD2_assets/client-matrix.json). Raw accepted CSVs/settings/logs/synthetic pack: `/tmp/hd2-client-z5ygf597`. The desktop compositor can block presentation; GPU columns come from GL queries, not present wait. Other work shares this host, so small differences are not a hardware regression threshold.
<!-- /CLIENT_MATRIX -->

The material proof renders **40 hulls** with identical geometry/transforms in native and HD modes; 20 warm-up frames precede 80 attempted measured frames per cell. Readback is outside the scene/post queries. Valid sample counts are recorded rather than filling late query results. Synthetic maps are 256²; pilot maps retain their actual 4K/2K sizes. Post is either a copy or bloom → FSR1 Quality → RCAS. The proof uses a 2560×1440 output window and appropriately sized scene targets for each case; the client matrix above uses each requested window size directly. The proof post values are diagnostics for that fixed output window; use the client table for output-resolution post-cost comparisons.

<!-- PROOF_MATRIX -->
All numbers below are X11 GL-query medians in milliseconds. Each cell has **80 valid scene samples**. This is software GL on a shared host; the actual client table above is the feature-level A/B.

| Material / output | Pipeline | Native scene | HD scene | Native / HD post |
| --- | --- | ---: | ---: | ---: |
| Synthetic 40-sphere scene, 1080p | Native resolution | 5.839 | 9.810 | 3.302 / 4.071 |
| Synthetic 40-sphere scene, 1080p | FSR Quality + bloom | 5.342 | 9.760 | 21.508 / 26.018 |
| Synthetic 40-sphere scene, 1440p | Native resolution | 7.689 | 14.076 | 4.779 / 5.373 |
| Synthetic 40-sphere scene, 1440p | FSR Quality + bloom | 6.816 | 9.707 | 26.151 / 26.010 |
| Private pilot 40-hull scene, 1080p | Native resolution | 2.382 | 2.842 | 3.132 / 3.425 |
| Private pilot 40-hull scene, 1080p | FSR Quality + bloom | 2.061 | 2.453 | 15.018 / 14.608 |
| Private pilot 40-hull scene, 1440p | Native resolution | 2.342 | 3.484 | 3.227 / 4.034 |
| Private pilot 40-hull scene, 1440p | FSR Quality + bloom | 2.423 | 2.848 | 19.975 / 20.309 |

[Synthetic medians/p95, sample counts and residency](HD2_assets/timings.json), [pilot medians/p95, sample counts and residency](HD2_assets/pilot-timings.json). Raw proof logs: `/tmp/hd2-synthetic-bench.log`, `/tmp/hd2-pilot-bench.log`.
<!-- /PROOF_MATRIX -->

![Synthetic material: original native shading left, PBR right](HD2_assets/synthetic-native-hd.png)

<!-- CLIENT_SCREENSHOT -->
![Actual hull inspector: native left, synthetic HD right](HD2_assets/client-inspector-native-hd.png)

[1080p native](HD2_assets/client-1080-native-off.png), [1080p HD](HD2_assets/client-1080-native-hd.png), [1440p HD](HD2_assets/client-1440-native-hd.png), [1440p HD through FSR/bloom](HD2_assets/client-1440-fsr-hd.png). These captures use only native geometry/art and mathematically generated test maps.
<!-- /CLIENT_SCREENSHOT -->

The private pilot review capture is [native versus HD on wc_icfig](/tmp/hd2-private-review/pilot-review-native-hd.png). It is a rejected-art **review**, not an accepted visual-quality recommendation. The private PNG and raw pilot maps remain outside this repository. [Pilot timing numbers](HD2_assets/pilot-timings.json) contain no generated image/model data.

## Validation

[Final validation summary](HD2_assets/validation-summary.json).

- Whole solution build: **0 warnings / 0 errors** ([build output](HD2_assets/build.txt)).
- DesktopGL `/Profile:OpenGL` shader compilation: passed; committed-ready `.ogl.mgfxo` and matching source SHA256 are left in the working tree. The actual effect loads and draws through GL in both SDL-offscreen and X11 contexts.
- **33 selected methods × 3 fresh isolated invocations: 114 case passes, 0 failures/skips** ([ledger](HD2_assets/isolated.json)). This includes all 14 new methods plus affected native texture/scale/winding, shader-staleness and graphics-settings checks. No combined filter was used for these method-level runs; each invocation selected one method (a theory retains all of its data rows).
- All eight pack methods also passed a final **24/24 individual invocations**, including an oversized numeric schema fallback ([final pack ledger](HD2_assets/pack-final.json)).
- All six PBR methods also passed a final **18/18 individual invocations**, including hot-resident protection, native fallback under pressure, and eviction/reload after idle ([final PBR ledger](HD2_assets/pbr-final.json)).
- The final synthetic GL method additionally passed **3/3 exact-method invocations** with normal, metallic and roughness varied independently ([final render ledger](HD2_assets/render-final.json)).
- Synthetic GL assertions cover native fallback equivalence, all five maps, a changed sector light, cloak opacity, corrupt PNG fallback, gamma-aware/normalized mips, budget rejection, LRU eviction/reload, and bloom/FSR output. No external artwork or private generated maps are needed for this test.

The full run exposed a pre-existing stale P652 test: `Enemy_lockout_complains_with_native_voice_id_after_initial_backoff` still observed `RecordingIgcSite.SendChat` and the old `1342` cue, while the existing P725 implementation emits `DroneChatEvent` with the port's `341` cue. It failed **3/3 exact-method baseline invocations** ([baseline evidence](HD2_assets/voice-baseline.json)). The fixture now observes the actual P725 event and cue; the 29.99 / 30 / 30.1 second backoff sequence, two-complaint count, text and zero-ore assertions remain intact. It passed **3/3 after correction** ([fixed evidence](HD2_assets/voice-fixed.json)). Production audio/simulation code is unchanged. Native's complaint trigger/backoff references are `~/Development/Allegiance/src/Igc/shipIGC.cpp:2199` and `~/Development/Allegiance/src/Igc/shipIGC.h:1723`.

<!-- SUITE_RESULTS -->
Exactly one unfiltered run of each required project completed ([raw counters/failures](HD2_assets/suite.json)).

| Project | Passed | Failed in full run | Skipped | Wall seconds |
| --- | ---: | ---: | ---: | ---: |
| Core | 4781 | 2 | 0 | 989.1 |
| ClientCore | 1178 | 2 | 0 | 2550.0 |
| MonoGame | 2324 | 0 | 3 | 958.6 |

Total: **8,283 pass / 4 initial failures / 3 skips** across **8,290 cases**. The three skips are the existing optional, unavailable private Meshy-pack tests. All new HD tests passed.

The full-sweep failures are retained in the ledger, not relabeled as passes:

- Core stale P652 voice observation: failed 3/3 before correction, corrected fixture passed 3/3. See the explanation and ledgers above; production audio/simulation is unchanged.
- Core raw-peer pool churn: `TimeoutException: no OnPeerJoined for raw peer`; passed 3/3 exact-method runs ([evidence](HD2_assets/transport-isolated.json)).
- ClientCore P705 research, `101/1, size=5`: upgraded-launch wait timed out at simulation time 18,390.7; passed 3/3 exact-case runs, each completing both matches ([evidence](HD2_assets/p705-isolated.json)).
- ClientCore P703 first branch, `25/3, generated=False`: one completed side observed instead of two; passed 3/3 exact-case runs ([evidence](HD2_assets/p703-isolated.json)).

The latter three meet the brief's load-flake rule and are recorded in [TEST_FLAKES.md](../TEST_FLAKES.md). No second full sweep was run after the fixture correction; the required targeted triplet verifies that correction, and Claude's mini runner owns the second sweep.

The required showcase never-parks gate passed **5/5 individual runs** (100.4, 98.5, 97.0, 100.8, 96.3 seconds), all with real local server/bot traffic ([ledger](HD2_assets/showcase.json)). No designated run failed, so the conditional base-rate comparison was not triggered. The same method also passed in the single ClientCore sweep.
<!-- /SUITE_RESULTS -->

Live/loopback tests run with network access, the configured native/artwork corpora and `ALLEGIANCE_LIVE_P660_SHOWCASE=1`. Existing opt-in multi-minute diagnostic/rig drivers are not substituted for the required suite. No second full sweep is run in this lane. Deadlines, quarantine traits and simulation/bot behavior are unchanged. The stale test observation below was corrected without relaxing its timing or count assertions.

## Reproduction

Normal toolchain:

```sh
scripts/build-shaders.sh
NUGET_HTTP_CACHE_PATH=/tmp/hd2-nuget-cache dotnet build AllegianceCore.slnx
SDL_VIDEODRIVER=offscreen ALSOFT_DRIVERS=null \
  dotnet tools/hd2-proof/bin/Debug/net10.0/hd2-proof.dll "$PWD" --test
SDL_VIDEODRIVER=x11 ALSOFT_DRIVERS=null \
  dotnet tools/hd2-proof/bin/Debug/net10.0/hd2-proof.dll "$PWD" \
  --output docs/reports/HD2_assets
python3 tools/hd2-proof/measure-client.py --sdl-driver x11 \
  --art "$ALLEGIANCE_ARTWORK_PATH" --output docs/reports/HD2_assets
```

`measure-client.py` needs Pillow and the already-built DesktopGL client. It creates synthetic maps and verifies their real native model/texture hashes, uses fresh saved settings for each HD/FSR combination, and retains per-frame CSVs, settings, logs and the temporary synthetic pack under a printed `/tmp/hd2-client-*` directory. The report stores summaries and captures. For private pilot review, add `--pilot "$ALLEGIANCE_HD_PACK_PATH" --art "$ALLEGIANCE_ARTWORK_PATH" --output /tmp/hd2-private-review` to the proof command. Never choose a public-tree output directory for that private review.

In this sandbox the default NuGet cache and Wine prefix/runtime are read-only. NuGet's HTTP cache was redirected to `/tmp`. Shader compilation used a temporary copy of the configured Wine prefix and Wine runtime with only its `/run/user` runtime-directory path redirected to writable `/tmp`; compiler code, shader inputs and compiler options were unchanged. No installed toolchain or private pack was modified.

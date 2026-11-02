# P922 — target pane follows the HD material mode

The ship, station and asteroid previews now retain their resolved model/texture stems and forward them to the same material draw used in the world. A held target follows Off → Classic → Pbr and the change of HUD style without replacing its cached art. No commits or pushes were made.

## Cause and change

`Game1.Rendering.cs:2777-2783` caches the two string references in `PreviewArt`; the three resolver branches populate them at `:2815-2860` and the draw switch forwards them at `:2731-2768`. The previous cache kept only geometry, texture object and scale. `TargetModelPreviewRenderer.cs:99-123` now passes both stems to the existing renderer parameters. No mode or selected HD texture is cached, and preserving these existing strings adds no allocation to a held target's frame.

The previous wrappers omitted both optional parameters. `NativeMaterialParts.cs:89-92` requires a non-null model before attempting PBR, so all three preview kinds fell through to BasicEffect. Its Classic lookup at `:89,97-100` can recover a part's own texture stem, but cannot recover an inherited file-level texture stem from a `Texture2D` object. The world callers already supplied both stems. The wrappers still use the same native renderer, framing, headlight and render target.

Treasure needs no change: `TargetModelPreviewRenderer.cs:129-135` passes the complete `TreasureVisual`; `NativeTreasureRenderer.cs:201-209,239-241` resolves its fallback texture stem and passes `visual.ModelStem` plus that stem to `Materials.Draw`. The probe preview uses this same path. This is not the omitted-parameter bug.

Native intent was re-grepped in the read-only source: `effect/thinggeo.cpp:373-380` normalizes the selected object's `ThingGeo` and calls `RenderTargetGeo`; `:1482-1560` applies orientation/scale, binds the object's texture and renders the existing `m_pgeoMesh`. World rendering also renders that mesh (`:1468`). P922 repairs the port's optional material extension; it introduces no native gameplay rule. The existing **Target model preview** parity row only gains the evidence reference; its status and score are unchanged.

## File-row finding

Two concepts must be distinguished: an image's file-row orientation and a mesh part inheriting its texture from the file/core record. The installed schema-3 catalogue has **16 imported-image atlases**, referenced by **17 model entries**. They do not all rely solely on an inherited texture. The fresh [material audit](P922_assets/material-audit.json) records actual mesh scopes: seven entries contain inherited triangles; the other ten have explicit part stems throughout. For example, `ss102` has three parts, 44 inherited triangles, and explicit `ss102bmp`/`biosbaybmp` scopes on the other parts.

The regression proof builds two parameter fixtures from each real model's unchanged vertices/UVs/indices: all parts use their own atlas stem, or all inherit that same atlas from the file-level binding. Across ship, station and asteroid wrappers, these representations produce identical pixels with the fix. Repeating the old omitted-stem call loses Classic **only for the inherited representation**, and loses PBR for **both** representations. This confirms the conditional Classic gap, including an imported-image atlas, without claiming that all 16 atlases were broken.

In the four unmodified models' sampled pose, old and corrected **Classic** calls happen to produce identical pixels; the inherited surfaces in the two stations do not contribute a visible difference in that pose. In particular, this proof does **not** reproduce wholesale Classic HD loss on `ss102`. The parameter fixtures isolate the missing fallback rather than relying on that view exposing an interior surface.

## GPU proof

`tools/p922-target-proof/` starts the real Game1 client against a local loopback server, pauses the host after logon, selects deterministic targets and renders all 24 kind/style/mode combinations. It exercises production art resolution, cache, setting synchronization, preview wrappers and HUD composition. The two Live test cases in `P922TargetMaterialTests` run with the configured pack and with an explicitly absent catalogue. Both cases passed **3/3 alone**, with **0 skipped**. The selected target and the exact cached art object remain held across all six mode/style phases per kind.

Quantitative comparisons use a fixed camera, orientation and `TargetModelPreview.Lighting` so camera settling cannot masquerade as a material change. Every corrected pane is byte-identical to the corresponding direct world-renderer draw with those same parameters. All Off draws equal the previous omitted-stem call. With the catalogue absent, every mode equals both Off and the old call, byte for byte. The [HD measurements](P922_assets/hd-summary.json) and [absent-pack measurements](P922_assets/native-summary.json) include all 24 cases each; [gate totals](P922_assets/gates.json) record the final validation results.

Changed RGBA pixels out of 65,536, identical for Classic and Modern HUD styles under the fixed camera:

| Kind / model / texture | Off vs old call | Classic vs Off | Pbr vs Off | Pbr vs Classic | Pbr vs old call |
|---|---:|---:|---:|---:|---:|
| Ship `wc_bifig` / `fig20bmp` | 0 | 4,319 | 4,448 | 4,418 | 4,448 |
| Station `ss27` / `ss27bmp` | 0 | 19,149 | 19,276 | 19,276 | 19,276 |
| Asteroid `bgrnd03` / `bgrnd03bmp` | 0 | 18,028 | 20,352 | 20,355 | 20,352 |
| Imported file-row station `ss102` / `ss102bmp` | 0 | 3,458 | 3,876 | 3,876 | 3,876 |

For every row and both styles, absent-pack Off/Classic/Pbr differences are **0**. The parameter fixtures additionally assert the old-call failure conditions for explicit and inherited stems on every wrapper, including the imported station.

### Lighting, render state, UVs and mip checks

- Both the pane and its direct-render oracle receive `TargetModelPreview.Lighting`; PBR uses that parameter at `HdMaterialRenderer.cs:177-184`. It receives the pane world/view/projection each draw (`:173-176`), not cached world matrices. The same shared `HdMaterialRenderer.Mode` remains unchanged after drawing.
- `Begin` binds the 256² depth-backed target. `End` restores the caller's colour target and resets opaque blend/default depth (`TargetModelPreviewRenderer.cs:75-96`). The proof asserts those states after the excursion, and the real Game1 Debug build's render-target guard also runs. `End` does not snapshot every device state: rasterizer, sampler and shader bindings are set by the following native world renderer. The direct world draws after a PBR excursion match the pane exactly; normal flight frames continue drawing correctly.
- The unchanged shared material boundary calls `ReverseMeshV` for binary atlases and rebuilds the reflected tangent basis (`NativeMaterialParts.cs:100,117-130`); text-imported atlases bind directly. Both categories match the world oracle. The screenshots retain the fighter's engine circles/wing panels, station panel borders and the imported station's green side markings in the same positions across modes.
- **Existing limitation, left outside this material-identity fix:** both footprint estimators divide by view-space distance (`NativeMaterialParts.cs:82-84`, `HdMaterialRenderer.cs:160-163`). They see the correct **256 px target viewport**, but apply a perspective formula to the pane's orthographic projection. A radius-one preview at camera distance eight reports about **32 px**, although its enclosing projected diameter is 256 px. The requested quality quantizes to 6 (64) before map-specific mip selection. A shared world resident may already be finer and is retained by the existing two-second demotion policy. This explains some softness/resolution differences in captures. No mip/residency policy or new PBR feature was introduced here.

## Captures and visual inspection

All eight new WebP images were opened and inspected. The [Classic contact sheet](P922_assets/panes-classic.webp) and [Modern contact sheet](P922_assets/panes-modern.webp) each contain all four kinds and all three modes. The fighter retains four bright engine circles and a central spine; the cross-shaped station retains its blue/orange panels; the asteroid retains its silhouette/craters; the imported station is a narrow pair of vertical spars with green markings, dark in Off/Classic and brighter under PBR. The sheets show sharper native detail in some Off views and softer HD maps in others; this is evidence of mode participation, not a certification of pack quality.

Real Game1 full-frame captures show the actual target preview inside each style's HUD, with the selected fighter held:

| HUD | Off | Classic material | Pbr |
|---|---|---|---|
| Classic | [frame](P922_assets/flight-classic-off.webp) | [frame](P922_assets/flight-classic-classic.webp) | [frame](P922_assets/flight-classic-pbr.webp) |
| Modern | [frame](P922_assets/flight-modern-off.webp) | [frame](P922_assets/flight-modern-classic.webp) | [frame](P922_assets/flight-modern-pbr.webp) |

Classic has its artwork gauges and small target model below the own ship; Modern has cyan/grey gauges and the same target model. The fixture's world is mostly dark and some HUD labels overlap at this window size; layout is unchanged and outside scope. Full frames are context evidence; the pane readbacks and fixed-input comparisons establish the material differences.

New media: the two `panes-*.webp` sheets and six `flight-*.webp` frames above, **229,654 bytes total**. No textures, models or regenerated packs were added. These remain for the requested human media review before any later push; this lane made no push.

## Preview draw cost

Measured locally using Mesa **llvmpipe (LLVM 21.1.8, 256 bits)**, a software OpenGL implementation. This is a real graphics context and shader/readback proof, **not hardware-GPU or rig performance**. The target is the production 256×256 target, using radius-one framing and the preview headlight. Each mode gets eight warmup draws, 40 submission samples, and 12 completed draw/readback samples. Times include render-target begin/end. The completion measurement includes synchronization and readback, so it is not a GPU timer query. Warm maps/tangents are excluded from steady submission samples. The local server uses the current production defaults; it is paused for rendering measurements.

Final run, Modern HUD; milliseconds per preview:

| Kind | Off submission | Pbr submission | Off completed + readback | Pbr completed + readback |
|---|---:|---:|---:|---:|
| Ship | 0.034 | 0.032 | 1.040 | 24.693 |
| Station | 0.043 | 0.051 | 1.460 | 24.177 |
| Asteroid | 0.016 | 0.016 | 0.360 | 19.053 |
| Imported station | 0.044 | 0.047 | 0.748 | 13.868 |

These small submission differences are noisy shared-host measurements; PBR's large software rendering cost is real but cannot be extrapolated to a discrete GPU. Existing material rendering still allocates (recorded in the JSON, e.g. 136 bytes per Off draw); the fix adds two cached string references and forwards them, with no new per-frame resolution, string construction, closure or art object.

## Validation and repeat

- `~/.dotnet/dotnet build AllegianceCore.slnx`: **0 warnings, 0 errors**. The sandbox needs `NUGET_HTTP_CACHE_PATH` inside `.scratch/p922/`; the first restore tried to update the read-only user cache, then succeeded with this environment-only correction.
- `tools/build-tools.sh`: **all 111 projects built**, 0 warnings/errors. The subsequently extended audit/proof was also rebuilt with 0 warnings/errors.
- New GPU tests: **2 passed, 0 failed, 0 skipped**, repeated three times alone (37 s / 37 s / 38 s). An earlier no-pack comparison used the still-settling chase camera across frames; the fixture was corrected to compare identical camera inputs before the final three passes.
- Exactly **one Fast-tier run** was performed: **12,964 test executions, 12,956 passed, 5 failed, 3 skipped**, 558.9 seconds; overall **FAIL**. All three skips are optional `MeshyPackTests` (unconfigured private pack), not missing artwork. The nine infrastructure checks passed. Six otherwise-passing tests also exceeded the calibrated Fast duration limit. Each passed 3/3 individually in 1.805–9.686 seconds (all below 10 seconds); their exact three-run measurements and all unrelated failures are ledgered in `docs/TEST_FLAKES.md`.
  The profile-editor case fails 3/3 with the default read-only user configuration and passes 3/3 with worktree-local `XDG_CONFIG_HOME`. The timer-boundary, automatic-seat timing and remote-death cases pass 3/3 in isolation. The NPC-defender case still fails 3/3 at its phase-one null attack-target assertion; it was already reported in previous sweeps and remains open. No simulation fix is included in this rendering task.
- Parity scorecard before/after: successful, output identical; no status or score change. Glossary link validation passed (504 distinct paths); `git diff --check` passed. New code/test sources were scanned for private paths and secret patterns; none found. The commit-oriented publish guard was not represented as a working-tree check; no commit or push was made.

Repeat with `HD_PACK` set to the existing unpacked schema-3 catalogue. All outputs/settings remain worktree-local:

```bash
set -a; . ./local.env; set +a
export DOTNET="$HOME/.dotnet/dotnet"
export PATH="$HOME/.dotnet:$PATH"
export NUGET_HTTP_CACHE_PATH="$PWD/.scratch/p922/nuget-http"
export ALLEGIANCE_HD_PACK_PATH="$HD_PACK"
"$DOTNET" build AllegianceCore.slnx
"$DOTNET" test tests/Allegiance.ClientCore.MonoGame.Tests --no-build \
  --filter FullyQualifiedName~P922TargetMaterialTests
"$DOTNET" tools/p922-target-proof/bin/Debug/net10.0/p922-target-proof.dll \
  --audit .scratch/p922/material-audit.json
# Standalone captures; use a fresh directory for each run.
export SDL_VIDEODRIVER=offscreen ALSOFT_DRIVERS=null
export XDG_CONFIG_HOME="$PWD/.scratch/p922/settings"
export XDG_DATA_HOME="$PWD/.scratch/p922/data"
"$DOTNET" tools/p922-target-proof/bin/Debug/net10.0/p922-target-proof.dll .scratch/p922/captures
"$DOTNET" tools/p922-target-proof/bin/Debug/net10.0/p922-target-proof.dll .scratch/p922/no-pack --no-pack
```

Raw logs/TRX and original captures are retained only in the ignored, task-local [.scratch/p922 evidence directory](../../.scratch/p922/) (the sandbox cannot write the usual external evidence store). The small sanitized JSON and WebP evidence is under `docs/reports/P922_assets/`. No remote machine was contacted. No training, simulation, server or deployment setting changed.

## Native-intent footer

**Design:** native uses the selected object's own `ThingGeo`/mesh and texture in both the world and target view (`effect/thinggeo.cpp:379-380,1468,1482-1560`).

**Era constraint:** BasicEffect-style native lighting has no modern material pack or PBR maps. HD/PBR participation is a port extension; different visuals in that mode are intentional.

**Bug:** the port's preview adapters dropped material identity while the world adapters preserved it. The fix restores that identity at the existing boundary, leaving Off and missing-pack fallback equivalent.

**Modern better approach:** retain one cached material identity for every view and evaluate the live presentation mode at the shared renderer. A future separate graphics task can make footprint estimation projection-aware and measure the result on hardware without changing this cache contract.

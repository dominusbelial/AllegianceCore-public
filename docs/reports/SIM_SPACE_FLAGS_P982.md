# P982 — optional sim space visuals

2026-11-09. Codex lane, `codex/p982`, base `aa979ab4`. No commits or pushes.
The report was opened before implementation and gating, then filled with the
implementation and evidence. All four enhancements default **OFF**. They are
independent client presentation choices; server, sensor/fog, targeting, starmap,
FOV and game rules are unchanged. Existing `visuals.radialVisibility` is untouched.

## Choices and provenance

FS2 Open supplied ideas, never implementation code. Its source was read only;
no code or close paraphrase was incorporated. Citations below refer to the local
source and were rechecked for this task, as were the native Allegiance lines.

| Flag | SCP idea | Native / existing port | P982 mode |
|---|---|---|---|
| `visuals.subtleDust` | A small persistent population close to the eye, with distance-weighted visibility and recycling after movement/cuts (`fs2open:code/starfield/starfield.cpp:46-57,1877-1938`). | Distance-driven forward-cone grains, born within 100 m, culled beyond 500 m or after a two-second fade; camera travel over 100 m clears them (`effect/debrisgeo.cpp:14,77-157,163-190`). The brief's 500 m refers to culling, not initial placement. Stationary native dust eventually fades away. | A deterministic hollow near-eye shell; fixed count at each existing Debris level, no stationary random churn, outer-surface recycling and distance fade. |
| `visuals.simStars` | Orientation-only background directions and individual intensities (`fs2open:code/starfield/starfield.cpp:1757-1812`). | Native seeded random directions and grey intensities, with four times the map's count (`effect/starimage.cpp:32-53`); black tier has 4,200 parallax points, native tier its seeded points, HD tier its baked NASA star texture. | At most 500 neutral direction points, grey 24–160/255, using sector seed and `min(500, max(0, Stars × 4))`. All tiers replace only their star layer; nebula/planet art remains. |
| `visuals.simGlare` | Separate directional sun sprites and configured glare/flare artwork (`fs2open:code/starfield/starfield.cpp:1274-1361,1364-1416`). | Eight additive elements, including the large aura at the light position; limited by projection into the viewport (`effect/efimage.cpp:601-613,716-745`). The port's black tier draws these; native/HD tiers previously had no sun renderer. | A small angular sun with much fainter neutral ghosts. Black replaces its flare styling; native/HD gain the same restrained directional sun when this flag is enabled. |
| `visuals.starStreaks` | Keep prior rotated star positions and use a bounded directional tail (`fs2open:code/starfield/starfield.cpp:1769-1775,1812-1850`). | Native retains previous screen points and can draw lines capped at 16 Manhattan pixels (`effect/starimage.cpp:45-46,102-143`); the existing port draws points. P982 leaves that default intact. | One short line batch per existing point layer while rotating, with a fixed-time exposure reconstructed from the previous view. No ship, scene or HUD pixels are blurred. HD tails attach to actual bright baked-star texels. |

Native Debris levels are unchanged: Low 1.5 → Medium 1.0 → High 0.8 → Off 0
(`WinTrek/WinTrek.cpp:4633-4653`). The enhancement reads `_debrisGeo.Density`,
including `--debris`, Options, and the existing GPU-budget downshift. Off stays
zero grains in either mode; there is no second density setting.

## Implementation and constants

Paths in this section are relative to `src/Allegiance.ClientCore.Game/`.

- `Rendering/VisualFlags.cs:68-94`: four default-off table rows and descriptions,
  hotkeys S/T/G/K. P979's profile converter, `--visuals`, live generic menu commands
  and startup logger automatically include them. No flag-specific menu branches.
  `OptionsMenuModel.cs:318-331` selects a hotkey's matching table row before
  returning its generic visual command, so S/T/G/K and the numeric U/D keys
  resolve the intended key even when another row was highlighted.
  `OptionsMenuModel.cs:57-64` and `Game1.BenchmarkSharing.cs:368-379` lay out table
  rows by available height, retaining old spacing when it fits; the eight-row
  page and navigation hint fit 360/480/540/1080-pixel windows.
- `Rendering/SubtleDust.cs:8-67`: Low/Medium/High budgets **160/240/300**; arbitrary
  positive density remains bounded at 300. The 15–80 m shell gives a fighter a
  nearby motion reference without filling the cockpit with close dots. Initial
  positions cover uniform shell volume; only grains outside either boundary
  recycle, exactly onto the outer surface. Brightness is full to 40 m and falls
  linearly to zero at 80 m. Half-extents are 0.025–0.045 divided by focus, versus
  native's 0.1/focus; neutral intensities 0.16–0.28 are also much lower than native.
  Count and stationary positions do not depend on render rate; no time-based RNG
  churn occurs. Movement recycling samples the camera path at render updates.
- `Game1.Rendering.cs:688-735`: switch between the shell and the original dust
  block, sharing the existing `AmbientDustRenderer`. **`DebrisGeo.cs`, its tests,
  `AmbientDustRenderer.cs`, and its sizing tests are unchanged.** Returning to
  native mode resets the shell, so re-enabling it refills at the current eye.
- `Rendering/SpaceVisualMotion.cs:16-33`: reset on first draw, sector/camera/sky
  change, travel **>100 m**, a view-axis change >45°, backward time, or a gap
  >250 ms. Duplicate stationary draws do not refill. `Game1.Rendering.cs:2306-2309`
  supplies camera elapsed time (`_nowSeconds`), independent of simulation pause
  or time dilation.
- `Rendering/SpaceVisualMotion.cs:39-105` and
  `Rendering/SpaceStarRenderer.cs:19-76`: seeded sphere directions; eye translation
  is excluded from projection. Reconstruct a **1/60 s exposure** from quaternion
  rotation rate, project its previous direction, cap at **8 pixels at 540p**
  (scales with viewport height), suppress sub-0.05 px tails, fade intensity with
  an **80 ms** response and a **0.6** peak multiplier, fade the tail endpoint to
  transparent. No rotation means no line. First frames/cuts/mode gaps clear
  history. Existing black parallax stars keep parallax when only streaks is on;
  their tails respond solely to view rotation.
- `Rendering/HdStarSamples.cs:11-48`, `Rendering/HdSkyRenderer.cs:201-210`:
  when HD streaks first turn on without sim stars, read the existing star texture
  once, take the brightest neutral-intensity texel above 100/255 in each 16² tile,
  keep at most 4,200, and locate each through the existing dome's UV triangles.
  The original baked-star dome still draws. Only tails are added; no proxy point
  stars or screen-space motion blur. Extraction has a one-time allocation/readback
  cost; steady-state timing below excludes that first-enable work.
- Tier branches: `Rendering/BlackSkyRenderer.cs:138-164`,
  `Rendering/NativeSkyRenderer.cs:179-199`, `Rendering/HdSkyRenderer.cs:167-190`.
  The native branch remains the default. New effects and textures are disposed
  with their renderer; the point and line buffers are reused.
- `Rendering/SimGlare.cs:10-21`, `Rendering/LensFlareRenderer.cs:90-157,191-217`:
  sun half-angle **0.35°** (0.7° diameter), anti-aliased 32² disc, **0.75** disc
  opacity; native aura art shrunk this far was nearly invisible, so the small
  geometric disc preserves a readable sun. Ghosts shrink to **35%** of their old
  size, use neutral tint and at most **0.25** alpha, smoothly reaching zero at
  `dot ≤ 0.7`. Actual angular disc size follows FOV and viewport projection.
  The sun remains visible within the viewport outside the ghost cone.
- `Rendering/AlephGlimpseRenderer.cs:77,427-468` and
  `Game1.Rendering.cs:2386`: destination postcards receive sim stars/glare, with
  cached postcards invalidated when those choices change. Postcards explicitly
  do not advance flight view history. They contain no rotation tails.

`FlareOcclusion.cs` and `BloomPostProcessor.cs` are unchanged. The sun stays in
the backdrop, so ordinary opaque geometry still covers it. Aleph occlusion
readbacks retain their existing behavior. HUD drawing remains after bloom
composition; no enhancement adds a fullscreen pass. The sun disc itself is below
the existing 0.8 bright-pass threshold on black, before additive overlaps.

Glossary updated. Existing parity rows EFX-0003/0008/0010 record the retained
native controls/defaults and the opt-in visual departures; no new gameplay rule
is claimed as a parity completion.

## Visual evidence

`tools/p982-space-proof/` uses production sky, dust, native fighter geometry and
Classic/Modern HUD renderers, fixed seed 982, 960×540 source frames, 50° vertical
FOV. The same fixtures run with all flags off, each alone, and all four on.
Stationary speed 0, cruise 180 m/s, boost 500 m/s, turning 0.3 rad/s. Accessory
moon/galaxy/distant-object paintings are disabled in both arms to expose the
changed layers. No colour/exposure enhancement was applied to evidence.

Each sheet has columns **off / subtleDust / simStars / simGlare / starStreaks /
all** and rows **stationary / cruising / boosting / turning**. Full-resolution
144 PNG source frames stay in `.scratch/p982/stills/`; the sheets are compact
viewing copies. Modern still captures clear the renderer's gauge animation history and frame
clock in the fixture, preserving production drawing while making A/B captures
deterministic without regenerating font atlases for each image.

| Sky | Classic | Modern |
|---|---|---|
| Black | [24 cases](P982_assets/black_classic.webp) | [24 cases](P982_assets/black_modern.webp) |
| Native | [24 cases](P982_assets/native_classic.webp) | [24 cases](P982_assets/native_modern.webp) |
| HD | [24 cases](P982_assets/hd_classic.webp) | [24 cases](P982_assets/hd_modern.webp) |

Four **4-second, 20 fps** WebM clips. Each frame shows black/native/HD across
columns and Classic/Modern across rows:

| Motion | Native/default visuals | All four on |
|---|---|---|
| Cruise | [off](P982_assets/cruising_off.webm) | [all on](P982_assets/cruising_all.webm) |
| Turn | [off](P982_assets/turning_off.webm) | [all on](P982_assets/turning_all.webm) |

**Flags-off pixel proof:** a scratch project builds the original base versions
of BlackSkyRenderer, NativeSkyRenderer, HdSkyRenderer, Starfield, LensFlareRenderer
and MdlTexture2D, with only namespace/interface compatibility edits. The identical
fixture renders 24 off cases. **0 changed RGBA pixels out of 12,441,600 pixels**,
including both complete HUDs, after deterministic HUD animation reset. Per-case
results: [default-pixels.json](P982_assets/default-pixels.json). The original
dust implementation and native sky/flare tests are also unchanged.

Options-page captures (production Game1, isolated preferences and loopback-only
lobby configuration) verify the new labels and logged launch overrides:
[Classic off](P982_assets/options_classic_off.webp),
[Classic on](P982_assets/options_classic_on.webp),
[Modern off](P982_assets/options_modern_off.webp),
[Modern on](P982_assets/options_modern_on.webp).

Final media: **1,645,089 bytes** total (six scene sheets, four Options captures,
four four-second clips). Largest clip is 342,309 bytes; every clip is below 1 MB
and the full set is below 3 MB. All sheets, both menu styles and sampled clip
frames were reviewed; imagery is native/NASA/procedural only. No private paths, account data, replacement model assets,
or external host details appear in the media.

## Cost

The local offscreen renderer is **llvmpipe (LLVM 21.1.8, 256 bits)**. This is
software rendering, **not a UM690 GPU measurement**. The fixture submits **400
native fighter hulls**, at 960×540, with 20 warm-up and 80 measured frames for
each flag/tier combination. CPU is draw submission time; GL timer queries report
the renderer's elapsed interval. HUD, synchronization readback and PNG encoding
are outside the measured region. Raw data: `.scratch/p982/cost/cost.csv`.

| Sky | Flag/mode | Draw calls | CPU submit mean (ms) | GL elapsed mean (ms) |
|---|---|---:|---:|---:|
| black | off | 412 | 8.615 | 0.136 |
| black | subtleDust | 412 | 8.137 | 0.134 |
| black | simStars | 410 | 8.574 | 0.136 |
| black | simGlare | 412 | 8.504 | 0.092 |
| black | starStreaks | 415 | 8.681 | 0.121 |
| black | all | 411 | 8.424 | 0.078 |
| native | off | 403 | 8.373 | 0.136 |
| native | subtleDust | 403 | 8.226 | 0.125 |
| native | simStars | 403 | 8.510 | 0.144 |
| native | simGlare | 411 | 8.273 | 0.122 |
| native | starStreaks | 404 | 8.851 | 0.137 |
| native | all | 412 | 8.644 | 0.152 |
| hd | off | 403 | 8.285 | 0.172 |
| hd | subtleDust | 403 | 8.496 | 0.176 |
| hd | simStars | 403 | 8.179 | 0.122 |
| hd | simGlare | 411 | 7.955 | 0.172 |
| hd | starStreaks | 404 | 8.585 | 0.184 |
| hd | all | 412 | 8.101 | 0.101 |

Raw portable data: [cost.csv](P982_assets/cost.csv). These small differences are
software-renderer means with host/driver noise; apparent wins are not hardware
speedup claims. Draw-call structure:
subtle dust uses the existing single dust batch; sim stars use one point batch
instead of three black point batches / one native point batch / one HD dome;
turning tails add three batches for native black stars, one for native/HD, or
one for sim stars. Sim glare replaces the eight black flare quads and adds eight
small quads to native/HD when the light is central; fewer outside the cone.

Expected UM690 iGPU impact is small geometry/submission work rather than screen
area: at most 300 dust quads, 500 sim points, bounded short line batches and
small sun/ghost quads, with no fullscreen target or pass. A reasonable working
budget is sub-millisecond steady-state incremental GPU cost at 1080p, **an
estimate requiring local UM690 measurement**, not a measured claim. Sim stars
also reduce point count / HD star texture sampling. HD tail extraction can cause
a one-time first-enable hitch; it is cached thereafter. No rig was contacted.

## Validation and gate

All final commands use the required local SDK, with artwork/native source from
this worktree's `local.env`; the available local sky pack was supplied explicitly.
The first restore attempted the default NuGet vulnerability audit (a public feed)
and failed writing its read-only cache. Final builds disable that audit and use
the existing package cache. No rig, mini, Mac or game host was contacted; UI
captures use an isolated loopback lobby URL.

| Check | Passed / Failed / Skipped or result |
|---|---|
| Solution build | **0 warnings / 0 errors** |
| `tools/build-tools.sh` | **126 tool projects built**, zero warnings/errors; updated clip proof is also rebuilt after its canvas correction |
| Focused new/extended visual tests, three serial isolated repetitions | **58 / 0 / 0 each time**: 36 new geometry/flag/layout cases plus 22 flag-framework/menu cases |
| Unchanged dust, sizing, sky-placement, glare-occlusion and bloom regressions | **74 / 0 / 0** |
| Unchanged native sky/flare math regressions | **26 / 0 / 0** |
| Base vs flags-off full-resolution screenshots | **24 cases, zero changed RGBA pixels**, both HUD styles and every sky/flight-scene combination |
| One final full Fast sweep, 566.1 s | **13,794 / 5 / 3**; infrastructure **9 passed**; two calibrated timing offenders |
| Five final isolated never-parks runs | **5 / 0 / 0**, all five reached 24/30 and observed real firing/damage |
| Glossary / parity / whitespace / working-tree hygiene | Validated; no secrets, personal identifiers or home paths introduced |

The three Fast skips are exactly the optional private `MeshyPackTests` catalogue,
resolution and built-content cases. Artwork and native-source coverage is present;
none of the new visual tests skipped. No private replacement models appear in the
captures.

The five Fast failures are pre-existing ledger families: P902 sound-cache allocation
(1,384 bytes versus zero), Nanite threat-credit receipt timing (0.222 versus 0.500),
the open missile partial-tip receipt failure (full lock 1.000), early missile-destroy
position, and independent bot rollout drift (18.57 m). The timing offenders are
that missile-tip case (38.325 s) and P777 warp-exit measurement (10.378 s versus the
calibrated 10 s budget). No sound, nanite, missile, bot, collision, simulation or
wire source is changed by P982. Three isolated repetitions for every encountered
non-task family are recorded in `docs/TEST_FLAKES.md` and the table below; the Fast gate itself
is recorded as **FAIL**, not silently converted to a pass.

A preliminary run exposed the already-ledgered read-only profile restoration test:
**0/3 pass** with the real read-only profile, **3/3 pass** using the existing
`ALLEGIANCE_PLAYER_SETTINGS` scratch override. The final sweep uses that override
and the original `local.env` is restored byte-for-byte afterward. Its profile test
passes. The earlier run was superseded when the generic visual-hotkey row-selection
bug was found and fixed; the final sweep above includes that fix.

The interrupted earlier drivers initially survived and overlapped showcase loops.
Those observations are **excluded from isolation evidence**. Both stale loops were
stopped, their in-flight tests drained, and a temporary local MSBuild cleanup hook
was removed with `Directory.Build.props` restored byte-for-byte. Only the fresh
serial cohort under `.scratch/p982/serial-showcase/` is counted below. No rendering,
encoding or other tests ran during that cohort.

| Run | Test duration (s) | Reached | Longest speed <1 with target (s) |
|---|---:|---:|---:|
| 1 | 243.3 | 24/30 | 1.915 |
| 2 | 235.9 | 24/30 | 1.413 |
| 3 | 194.8 | 24/30 | 2.808 |
| 4 | 181.8 | 24/30 | 2.707 |
| 5 | 162.2 | 24/30 | 2.516 |

**5/5**, compared with the documented base cohort's **4/5** in
`docs/reports/VISUAL_FLAGS_RADIAL_VISIBILITY_P979.md:229` (arrival-window miss in
its fifth run). The maximum low-speed interval here is **2.808 s**, below the
five-second bound. These visual flags are not exercised by the headless showcase
fixture, so no bot improvement is claimed. Compact receipts:
[showcase.json](P982_assets/showcase.json).

Logs, TRX files, base renderer scratch build and full-resolution PNG sources remain
under `.scratch/p982/`. No training lesson or rule changed. No commit or push was
made. Every started test/capture driver has ended, both temporary configuration
edits were restored exactly, and build servers were shut down before handoff.

### Isolated non-task follow-up

| Family | Three serial isolated executions | Interpretation |
|---|---|---|
| P902 sound-cache allocation | **pass / pass / pass** (0.009 / 0.009 / 0.012 s) | Existing allocation intermittency; no audio source changed. |
| Nanite threat-credit ratio | **pass / pass / pass** (3.434 / 3.435 / 3.392 s) | Existing wire-credit timing family. |
| Missile partial-tip receipt | **fail / pass / fail** (38.517 / 1.666 / 38.456 s) | **Remains open**: full lock reaches 1.000 without both expected tips. Same isolated rate as the P979 cohort. |
| Missile destroy position | **pass / pass / pass** (0.508 / 0.480 / 0.526 s) | Existing early-destruction receipt family. |
| Independent bot rollout | **pass / pass / pass** (1.092 / 1.061 / 1.097 s) | Existing trajectory timing/collision family. |
| P777 warp-exit timing offender | **pass / pass / pass** (9.309 / 9.883 / 9.941 s) | Under the 10 s calibrated budget alone. |
| Preliminary cross-side warp-return quarantine flake | **pass / pass / pass** (1.577 / 1.580 / 1.935 s) | Existing visibility receipt/timing family; final sweep quarantine also passes. |

No broad sweep was repeated to erase these failures. Compact isolated results:
[triage.json](P982_assets/triage.json). The report retains the failing Fast result
and the open missile issue rather than claiming a fully green gate.

### Proof implementation and final media review

`Game1.cs:193-195` owns the shared shell and motion model.
`Rendering/ISkyRenderer.cs:20` carries the optional visual frame;
`Rendering/Starfield.cs:93-116` adds tails to the legacy black bands without
changing their default point generation. Test coverage is in
`tests/Allegiance.ClientCore.MonoGame.Tests/P982SimSpaceTests.cs:9` and the extended
`VisualFlagsTests.cs` / `P979VisualsOptionsTests.cs` in that directory.

`tools/p982-space-proof/Program.cs:99,129,163,278` contains the GPU state/history
contracts, still matrix, clip matrix and 400-hull cost fixture. Its clip canvas
uses **PreserveContents** across target switches (`:168`), with an explicit
first-frame nonempty-pixel check for **all six panels** (`:191-206`). These checks
pass for all four clips on **three complete exports**, and the encoded frames
were visually reviewed again. This corrects a discarded-canvas issue in the
initial proof export; it changes no production rendering path.
`tools/p982-space-proof/make-media.py:1` assembles the sheets, bounds the WebM
encodes and asserts the media budget. All four clips are exactly 4.000 s at
20 fps. No fullscreen pass is added to the client.

## Native intent

Preserve native Debris controls, seeded per-sector star inputs, background
composition and every server/sensor/game rule. Flags off retain the previous
renderer paths and native regression fixtures.

## Modern approach

Independently selectable restraint: nearby faint motion grains, sparse stars at
infinity, a small readable sun, and short rotation cues with a fixed exposure.
All four are default-off entries in the shared client visuals table.

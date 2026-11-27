# P1091 · Weapon rows and selected-target text

Fourteen of 21 flat-HUD renders on `138e61db` had a weapon-row or own/target collision. The repaired renderers clear all 21. The seven originally clear frames have identical RGBA pixels, including Classic with native gauge art.

## Brief items

Times are approximate elapsed working intervals. Implementation, capture and verification overlapped.

| Item | Status | Evidence | Time |
| --- | --- | --- | --- |
| 1. Reproduce both overlaps | done | Original-renderer matrix below, including the 800×480 bar and 1280×720 target glyph coordinates | 7 min for initial reproduction, 3 min for the final control sweep |
| 2. Repair both layouts | done | All 21 final frames pass ink, order, alignment, count and viewport checks | 18 min including native-art control and visual inspection |
| 3. Preserve unaffected pixels | done | Seven complete frames match the baseline, with zero changed pixels in both 1080p screenshot pairs | 2 min |
| 4. Fast tests and deliberate breaks | done | 21 Fast cases pass. Four independent mutations produce the expected failures below | 4 min |
| 5. Before/after captures | done | Eight lossless PNGs, 632,955 bytes total. All eight decode to the original framebuffer bytes | 2 min |
| 6. Report and light gate | done | Builds and targeted gates passed. Two full Fast sweeps and three isolated reruns per unrelated failure are documented below | About 32 min for the gate and triage |

## What the baseline painted

Each frame selects **Commander Peregrine Longflight: Heavy Interceptor**. Own KB is 0% and target KB is 21%. Two mounts carry the same PW Gat Gun 1 name. Their proof readings differ by one round (16520 and 16521 of 32767) so substituting either mount for the other is detectable. Both display NOT READY.

The weapon painter placed the name and ammo as independent movable text, then painted a bar at its original row offset. At 800×480 the second Modern bar painted x=518–773, y=268–270. The ammo glyphs started at y=269. On Classic at 1280×720 one bar also struck SYSTEMS.

The Modern target painter drew raw strings around its native anchor. The own gauges used a text placer that could ignore the approximate pane reservation. At 1280×720 the target name painted x=393–887, y=462–481. Own ARMOR began at (501,459) and SHIELD at (710,459). With native art, Classic at 800×480 also put the long caption across AMMO/ENERGY and the target medal number across SIG.

The table gives **weapon bar/text intersections / own/target intersections** on the baseline. Each arc stroke contributes its own painted rectangle, so one label can intersect more than one stroke. Both numbers are zero throughout the final matrix.

| Viewport | Classic, bundled fonts | Classic, native art | Modern min | Modern default | Modern max | Modern scale floor | Modern high DPI |
| --- | --- | --- | --- | --- | --- | --- | --- |
| 800×480 | 1 / 1 | 1 / 3 | 1 / 7 | 1 / 12 | 1 / 12 | 0 / 3 | 1 / 12 |
| 1280×720 | 2 / 0 | 2 / 0 | 0 / 0 | 0 / 14 | 0 / 14 | 0 / 0 | 0 / 14 |
| 1920×1080 | 0 / 0 | 0 / 0 | 0 / 0 | 0 / 0 | 2 / 10 | 0 / 0 | 2 / 10 |

Min, default and max use Size 0.5, 1 and 2 on a density-1 desktop. The floor uses density 0.75 and Size 0.5 to reach the 0.4 scale clamp. High DPI uses density 2 and Size 2 to reach each window cap (0.667, 1 and 1.5). Classic uses its fixed font policy at all Size settings.

## Layout changes

- The shared systems panel measures the name, status and bar of each mount before drawing. All three use offsets in the same row. A clear anchor keeps its coordinates. When the stack has to move, its backing moves with it. Oversized lists can continue in another column.
- Modern measures target glyph coverage from the same scaled SDF atlas, bearings and advances used for drawing. The layout checks the own gauge rings, percentage and caption plates, active status readouts and the target group's text and geometry. A collision selects a three-row bottom arrangement at the chosen text scale, with a separate column for a model preview when present. Own and target KB retain their separate draws. The open arrangement determines the sliding pane's destination.
- The native-art Classic control revealed a collision below the review's 720p Classic example. Classic can shift the intact target pane horizontally when its name meets an own gauge caption or its experience number meets SIG. Its artwork, medal and text keep their relative coordinates.
- Building the signed-distance atlas now visits candidate texels in distance order and stops at the first opposite coverage value. This made the real-font renders practical in Fast. The pixel controls cover the resulting atlas and the final composition.

The shared weapon painter and Classic anchor are in [HudRenderer.cs](../../src/Allegiance.ClientCore.Game/Rendering/HudRenderer.cs). The Modern arrangement and glyph bounds are in [ModernHudRenderer.TargetPane.cs](../../src/Allegiance.ClientCore.Game/Rendering/ModernHudRenderer.TargetPane.cs) and [HudSdfFont.cs](../../src/Allegiance.ClientCore.Game/Rendering/HudSdfFont.cs).

## Render proof and deliberate breaks

The proof renders the full HUD to an offscreen DesktopGL target. It also replays the submitted text, weapon bars and Modern gauge arcs at the same fractional pixel phase into separate cells of a transparent atlas. Reading the nonzero alpha bounds detects the original ink even when a later draw covers it. The assertions check both entity captions and KBs, the ordered name/status/bar submissions for each mount, their alignment, the viewport and intersections. Classic's art-free fallback labels hull strength HULL. Native art and Modern use ARMOR. The 1080p automated goldens hash all 2,073,600 RGBA pixels in each style. [Render fixture](../../tools/hud2-proof/OverlapProof.cs) · [Fast tests](../../tests/Allegiance.ClientCore.MonoGame.Tests/P1091HudOverlapTests.cs)

An isolated copy of the dispatch sources generated the baseline with the same fixture and fonts. Its only renderer instrumentation opened the Modern class for derivation and made the bar and arc methods overridable. The recording subclasses forward each submission to its production painter. Restoring both original layouts in the Fast test host gave 14 failures and seven passes. The deliberate breaks below were also built and run outside the working sources.

| Deliberate change | Observed test result |
| --- | --- |
| Put each bar through the middle of its measured ammo line | All 21 cases failed |
| Leave the Modern target in its original arrangement on collision | Nine cases failed the own/target ink check |
| Suppress the Modern target KB draw while leaving the own KB present | All 15 Modern cases failed |
| Move every weapon bar down one pixel, keeping text clear | Only the two 1080p golden assertions failed |

With local artwork configured, reproduce the matrix or write an individual capture after a Debug build:

```sh
dotnet test tests/Allegiance.ClientCore.MonoGame.Tests --no-build --filter 'FullyQualifiedName~P1091HudOverlapTests'
SDL_VIDEODRIVER=offscreen ALSOFT_DRIVERS=null LIBGL_ALWAYS_SOFTWARE=1 \
  dotnet tools/hud2-proof/bin/Debug/net10.0/hud2-proof.dll --overlaps 1280 Modern default .scratch/captures
```

The capture command accepts widths 800, 1280 and 1920, skins `Classic`, `ClassicArt` and `Modern`, and scale cases `min`, `default`, `max`, `floor` and `ceiling`.

## Screenshots

All pairs show the same selected ship and mount data at density 1 and Size 1. Classic uses the installed native art. Counts compare decoded pixels across the full frame.

| Style | Viewport | Before | After | Changed pixels |
| --- | --- | --- | --- | ---: |
| Classic | 1280×720 | [Baseline PNG](P1091_assets/classic-1280x720-before.png) | [Fixed PNG](P1091_assets/classic-1280x720-after.png) | 36,581 |
| Modern | 1280×720 | [Baseline PNG](P1091_assets/modern-1280x720-before.png) | [Fixed PNG](P1091_assets/modern-1280x720-after.png) | 186,365 |
| Classic | 1920×1080 | [Baseline PNG](P1091_assets/classic-1920x1080-before.png) | [Fixed PNG](P1091_assets/classic-1920x1080-after.png) | 0 |
| Modern | 1920×1080 | [Baseline PNG](P1091_assets/modern-1920x1080-before.png) | [Fixed PNG](P1091_assets/modern-1920x1080-after.png) | 0 |

## Gate

- Debug and Release solution builds each finished with zero warnings and zero errors. The standalone tool build passed for all 156 projects.
- The new Fast filter passed **21/21** in each of three fresh test hosts. The directly affected filter passed **159/159** three times with local artwork, including the existing 1,848-combination Classic/Modern Live render. All six runs had zero skips.
- The glossary link checker validated 1,178 distinct paths. The report links, lossless image round trips, LF endings and whitespace check passed.
- The first full Fast sweep ran 16,185 cases in 11 min 40 s. It passed 16,171, skipped five and failed nine. Five bundle-path cases discovered the checkout's local configuration above their temporary install. The other four failures were the wake mine, delayed warp blast, two-peer kill scoring and idle wingman cases. Four executions exceeded the calibrated 10.587 s limit, including the hull render and civilization strategy cases.
- Each affected method passed in three fresh hosts when run alone, including the two timing-only offenders. The bundle tests used a directory descriptor for their temporary root so the fixtures saw an outside-repository install while their files stayed under `.scratch/`. The bundle, server and timing signatures appear in the [flake ledger](../TEST_FLAKES.md).
- A second full Fast sweep used that temporary root and ran projects serially. It passed 16,176, skipped five and failed four in 16 min 59 s. All five bundle failures cleared. The four server failures involved a missing owner treasure frame (P1031), a shield that never registered damage, a missing projectile kill notification and a docked station update with the wrong value. The NPC warp-exit measurement passed in 10.387 s against a 10 s limit. The missing treasure frame is newly recorded in the [flake ledger](../TEST_FLAKES.md#p1091-fast-sweep-2026-11-27).
- Each of those four failures and the NPC warp-exit measurement passed three isolated reruns. Both full sweeps remained red. All 21 new HUD cases passed in both.

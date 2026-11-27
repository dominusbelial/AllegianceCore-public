# P1096 crowded HUD label layout

In Release, with 72 coincident labels, Classic placement fell from 249 to 43 µs of thread CPU per frame. At 150 it fell from 1.89 ms to 0.18 ms. The four-box Modern case cost an additional 0.08 µs.

In the Debug comparison, with all 72 coincident labels admitted, median placement CPU time fell from 1.42 to 0.53 ms in Classic with decluttering switched off, and from 1.51 to 0.47 ms in Modern. At 150 contacts the corresponding medians fell from 11.59 to 2.02 ms and from 11.70 to 2.14 ms. Every warmed indexed layout allocated zero bytes. Modern with decluttering enabled had a small CPU regression when the selector reduced the scene to four or ten boxes.

## Brief items

Elapsed times are approximate wall-clock intervals. Builds, measurements and verification overlapped.

| Item | Status | Evidence | Time |
|---|---|---|---|
| 1. Measure first | done | 48 baseline measurements from `28debb3c`, completed before changing the placer | 4 min including setup |
| 2. Index the rectangles | done | Reused tile lists serve text, reservations and panel sweeps in the shared renderer | 1 min implementation, 2 min first verification |
| 3. Exact output | done | Ordered oracle comparisons and four matching reference framebuffers across Classic and Modern | 2 min including capture |
| 4. Fast tests and deliberate breaks | done | Six Fast cases, including four skin/setting cases. Each failed under a deliberate break. Desktop and KNI results below | About 5 min including scratch builds and both backends |
| 5. Repeat measurements | done | Same executable source, workload, CPU affinity and 48 measurements on the change | 1 min including rebuild |
| 6. Report measurements, equivalence, tests and flakes | done | Both full sweeps and all isolated failures are accounted for below. Report links, LF endings and glossary validation passed | About 40 min elapsed including gates and reruns |

## Placement measurements

The focused Linux benchmark ran the Debug game assembly with .NET SDK 10.0.404 on one pinned thread with tiered compilation disabled. The input matches the CLIENT-R1 960×540 viewport, 72×24 blocks and crowded anchor (460,250). The spread control uses 72 contacts on a 12-column grid. Every eighth contact has an accepted order. The production radar selector runs once outside the measurement window. The input to the placer consists of its admitted blocks in contact order followed by its merge tags (24×14). Classic's renderer uses the Off plan for both preference values. Modern uses the saved preference and the default Tactical preset. The box count includes tags.

Each row gives the three consecutive means for 250 frames after 30 warm-up frames. CPU is Linux calling-thread CPU time in microseconds per layout. Bytes are calling-thread managed allocations per layout and were identical in all three runs in each row. The window covers Reset and PlaceBlock. It excludes contact selection, font measurement, rendering and presentation. There are no locks in this path. [Benchmark](../../tools/p1096-label-bench.cs) · [Original measurement](CLIENT_REVIEW_R1.md#rendered-replay-and-isolated-costs)

| Skin | Declutter preference | Layout | Contacts | Boxes | Base CPU µs, runs 1 / 2 / 3 | Indexed CPU µs, runs 1 / 2 / 3 | Base B | Indexed B |
|---|---|---|---:|---:|---:|---:|---:|---:|
| Classic | off | spread | 72 | 72 | 72.52 / 71.52 / 71.25 | 24.60 / 24.95 / 25.25 | 12,672 | 0 |
| Classic | off | coincident | 24 | 24 | 64.16 / 62.38 / 61.87 | 54.09 / 55.02 / 54.97 | 4,224 | 0 |
| Classic | off | coincident | 72 | 72 | 1,437.83 / 1,419.61 / 1,417.49 | 559.75 / 533.36 / 517.27 | 12,672 | 0 |
| Classic | off | coincident | 150 | 150 | 11,446.71 / 11,724.91 / 11,594.62 | 2,089.68 / 1,978.17 / 2,018.62 | 24,864 | 0 |
| Classic | on | spread | 72 | 72 | 68.89 / 73.95 / 85.41 | 21.88 / 20.93 / 19.99 | 12,672 | 0 |
| Classic | on | coincident | 24 | 24 | 63.73 / 72.12 / 66.29 | 43.75 / 44.24 / 47.72 | 4,224 | 0 |
| Classic | on | coincident | 72 | 72 | 1,489.33 / 1,490.87 / 1,495.89 | 455.87 / 464.00 / 541.49 | 12,672 | 0 |
| Classic | on | coincident | 150 | 150 | 11,885.04 / 12,113.53 / 11,788.26 | 2,104.71 / 2,147.89 / 2,004.76 | 24,864 | 0 |
| Modern | off | spread | 72 | 72 | 82.35 / 72.29 / 74.96 | 24.82 / 23.38 / 24.48 | 12,672 | 0 |
| Modern | off | coincident | 24 | 24 | 64.13 / 61.17 / 65.25 | 44.67 / 43.44 / 44.85 | 4,224 | 0 |
| Modern | off | coincident | 72 | 72 | 1,511.76 / 1,505.27 / 1,492.58 | 472.47 / 474.77 / 515.37 | 12,672 | 0 |
| Modern | off | coincident | 150 | 150 | 11,696.24 / 11,646.03 / 12,002.81 | 2,137.62 / 2,119.42 / 2,254.16 | 24,864 | 0 |
| Modern | on | spread | 72 | 21 | 7.19 / 7.67 / 7.70 | 5.28 / 5.25 / 5.21 | 3,696 | 0 |
| Modern | on | coincident | 24 | 4 | 0.94 / 0.83 / 0.84 | 2.15 / 2.07 / 2.03 | 704 | 0 |
| Modern | on | coincident | 72 | 10 | 6.55 / 6.57 / 6.75 | 9.79 / 9.28 / 9.04 | 1,760 | 0 |
| Modern | on | coincident | 150 | 20 | 48.35 / 47.94 / 47.11 | 39.36 / 37.90 / 35.02 | 3,520 | 0 |

At four admitted boxes the median increased by 1.23 µs. At ten it increased by 2.71 µs. The cost of maintaining and consulting tiles is visible at those sizes. At 20 admitted boxes the indexed median was lower by 10.04 µs.

To reproduce, use the same benchmark file with each source revision, scratch-local NuGet caches and a CPU available to the process. Each invocation prints the entire three-run matrix.

```sh
dotnet build tools/p1096-label-bench.cs -c Debug -o .scratch/bench
DOTNET_TieredCompilation=0 taskset -c 4 dotnet .scratch/bench/p1096-label-bench.dll
```

### Release cross-check

After the gate and isolated reruns, the same pinned CPU ran the optimized Release benchmark against the base and indexed Release game libraries in two fresh processes. Each value is the mean calling-thread CPU time in µs per layout for one 250-frame run. The Release allocation measurements in all 96 windows agreed exactly with the Debug byte columns above.

| Skin | Preference | Layout | Contacts | Boxes | Base Release µs, runs 1 / 2 / 3 | Indexed Release µs, runs 1 / 2 / 3 |
|---|---|---|---:|---:|---:|---:|
| Classic | off | spread | 72 | 72 | 16.95 / 17.59 / 17.74 | 3.36 / 3.40 / 3.33 |
| Classic | off | coincident | 24 | 24 | 11.84 / 11.73 / 11.92 | 4.58 / 4.55 / 4.66 |
| Classic | off | coincident | 72 | 72 | 255.47 / 249.43 / 247.77 | 42.23 / 42.55 / 42.66 |
| Classic | off | coincident | 150 | 150 | 1891.10 / 1888.63 / 1885.89 | 178.08 / 177.62 / 177.29 |
| Classic | on | spread | 72 | 72 | 15.19 / 15.38 / 15.81 | 3.59 / 3.54 / 3.61 |
| Classic | on | coincident | 24 | 24 | 11.00 / 10.98 / 10.84 | 4.76 / 4.79 / 4.79 |
| Classic | on | coincident | 72 | 72 | 244.55 / 244.88 / 245.22 | 42.49 / 42.48 / 42.42 |
| Classic | on | coincident | 150 | 150 | 1883.23 / 1883.37 / 1884.27 | 177.02 / 177.15 / 177.00 |
| Modern | off | spread | 72 | 72 | 14.81 / 15.10 / 15.20 | 3.59 / 3.46 / 3.51 |
| Modern | off | coincident | 24 | 24 | 11.01 / 10.85 / 10.87 | 4.64 / 4.74 / 4.69 |
| Modern | off | coincident | 72 | 72 | 246.16 / 245.49 / 244.45 | 42.46 / 42.52 / 42.55 |
| Modern | off | coincident | 150 | 150 | 1878.27 / 1886.18 / 1881.94 | 176.67 / 177.02 / 176.93 |
| Modern | on | spread | 72 | 21 | 1.58 / 1.56 / 1.55 | 0.95 / 0.99 / 0.96 |
| Modern | on | coincident | 24 | 4 | 0.23 / 0.21 / 0.20 | 0.33 / 0.29 / 0.29 |
| Modern | on | coincident | 72 | 10 | 1.74 / 1.33 / 1.34 | 1.06 / 1.10 / 1.07 |
| Modern | on | coincident | 150 | 20 | 8.15 / 9.02 / 8.19 | 3.66 / 3.75 / 3.75 |

## Why the boxes match

The layout owns a directory of 64-pixel tiles. Each obstacle is entered in every tile it spans. Coordinates outside the directory map to the outermost tile. The directory caps each axis at 64 tiles and buckets retain their capacity across frame resets and viewport changes. Retained entry capacity depends on the high-water number of obstacles spanning each tile. A large rectangle uses one entry in each covered tile. End tiles are inclusive so zero-width or zero-height geometry receives a conservative lookup. With reversed endpoints, any intersecting interval spans the start coordinate. Saturation can add candidate comparisons and preserves every possible intersection. [Tile index](../../src/Allegiance.ClientCore.Game/Rendering/HudObstacleTiles.cs)

The final test is the same XNA `Rectangle.Intersects`. All recorded text, including fixed anchors and overflow, enters the index with the original two-pixel padding. Successful blocks also enter as unpadded reservations. Instrument searches skip ordinary reservations and honor instrument reservations and padded text. A panel takes the maximum intersecting bottom minus its part offset. Seeing an obstacle in two tiles gives the same maximum.

Given identical earlier boxes, the same candidate is therefore blocked in either placer. Place and PlaceBlock try the original clamped column and alternating row sequence, which gives the same first free candidate or the same clamped overflow anchor. Only Record appends to the public ordered text list. This argument applies successively to every call, including reservations added between labels. The native overflow reference is `WinTrek/radarimage.cpp:1284-1311`. This changes the implementation of the existing port layout and adds no native rule. [Placer](../../src/Allegiance.ClientCore.Game/Rendering/HudTextLayout.cs)

## Exactness evidence

The test-only oracle is the linear placer from `28debb3c`. The seeded test compares every return value, the panel's updated parts list, and every recorded string, bounds and fixed-anchor bit across 48 frames with changing viewports. It interleaves ordinary and instrument reservations, fixed and movable text, panels and blocks. The four skin/setting cases place 150 contacts in coincident, single-row, edge-clamped and oversized layouts, including accepted and queued orders and merge tags. The allocation case measures ten warmed crowded frames. The desktop and browser-game test projects compile the same oracle and cases against their respective MonoGame and KNI assemblies. [Fast tests](../../tests/Allegiance.ClientCore.MonoGame.Tests/P1096HudTextLayoutTests.cs) · [Frozen oracle](../../tests/Allegiance.ClientCore.MonoGame.Tests/LinearHudTextLayout.cs)

The four deliberate breaks below ran against both MonoGame and KNI in isolated builds of the placer with the same six test cases. Both unmodified controls passed all six, and each break produced the listed failures on both backends.

| Deliberate break | Observed failure |
|---|---|
| Remove the two-pixel text padding | Mixed sequence and all four skin/setting cases disagreed with the oracle |
| Restrict insertion and lookup to the leftmost column | Mixed sequence and all four skin/setting cases disagreed with the oracle |
| Retain obstacles across Reset | All six failed, including stale placements after resizing and growing allocation |
| Allocate a fresh tile directory every Reset | Allocation failed while the five geometry cases passed |

Additional mutations independently omitted overflow collision entries, omitted fixed-anchor collision entries and made ordinary reservations invisible to labels. Each broke the mixed sequence. Reversing the alternating row direction broke all four style/setting cases and the mixed sequence. Returning the first panel intersection instead of the greatest also ran as a sensitivity check. All six cases passed that variant. A shorter jump can reach the same position through an extra panel sweep. Production retains the original maximum.

The existing hud2-proof capture and compare commands rendered the radar-Default scene at 1280×720, size 1, with artwork, Xvfb and llvmpipe. All four captures matched across all RGBA channels and the full semantic snapshots. The comparison reported zero animation-only differences and zero corrupt goldens.

| Skin | Backdrop | RGBA SHA-256, base and indexed |
|---|---|---|
| Classic | dark | `c03fd28bedcc9e4a8eab3cac8cbe1aa72f9f9eb4196e52a7908281c4c8ac5d45` |
| Modern | dark | `a1edefa26fa56336122c0d4fdd5446311dba01a8601f19962a5a9ae8997744e2` |
| Classic | bright | `59ef78b7b4c4549f25aaa9de1024a0f057981cacd6e7740bb8418b8d8576aded` |
| Modern | bright | `6ad7ffba7f44e4433a7529f06dbb96558da55e3f03304919ec880a8f5945f501` |

```sh
export SDL_VIDEODRIVER=offscreen ALSOFT_DRIVERS=null LIBGL_ALWAYS_SOFTWARE=1
proof=tools/hud2-proof/bin/Debug/net10.0/hud2-proof.dll
# Run on base, then run the comparison with the changed Debug output.
xvfb-run -a dotnet "$proof" --capture .scratch/base-radar --filter 'radar-Default/1280x720/s1/'
xvfb-run -a dotnet "$proof" .scratch/radar-compare --compare .scratch/base-radar --filter 'radar-Default/1280x720/s1/'
```

### Crowded battle replay

A separate before/after check ran the CLIENT-R1 busy replay slice through Game1 on software DesktopGL. It used the P1092 fixture with the same recorded deliveries, 48 additional posed ships, 96 projectiles and four blasts. Each process advanced to logical frame 6500, warmed 60 draws, then rendered frames 6561–6740. At each frame the check recorded the stock frame-state hash followed by the active renderer's ordered text, fixed-anchor bit and four integer bounds for every box. The two binary traces compared byte-for-byte within each skin. All processes ended with 54 gathered poses and 68 HUD contacts. [Replay fixture](../../tools/p1092-particle-slice.fsx)

| Skin | Compared frames | Ordered boxes | SHA-256 of matching state and box trace |
|---|---:|---:|---|
| Classic | 180 | 14,220 | `2301882777f54d7fc1fd59fcfd018a2ef9fe338d6bb25b8c074f95323c8a0b2a` |
| Modern | 180 | 5,220 | `715f130d5d6ec8f516f8881a8547c4367a230549f2bb4bd2ed19ab5c36788430` |

The replay session had no transport attached during rendering. At the final state, an in-memory transport recorded a scripted view-sector request, highlight-on, order change and highlight-off through the session's normal send methods. Across both builds and skins the four outgoing packets matched in order, length and content. The 68-byte trace includes the four little-endian packet lengths and has SHA-256 `f0709eba14e8096b59bddd00fc5d71a2acdc986d80a96750a9cebfd21e50460b`.

## Light gate and flake notes

Debug and Release solution builds, including the browser projects, both finished with zero warnings and zero errors. Package restore used the local offline feed with scratch-local caches and configuration. The first standalone tool sweep could not restore three pinned KNI shader-pipeline packages from the initial offline feed. Adding the packages from an existing local cache let the shader tool build, and a fresh `tools/build-tools.sh --drop-bins` passed all 157 projects. The glossary checker validated 1,188 paths. The Fast gate results are below.

The new tests passed 6/6 in three fresh desktop test hosts and 6/6 in three fresh browser-game test hosts using KNI on .NET. The existing layout, overlap and declutter suites plus the new tests ran three times. Each pass had 87 passes, two failures and zero skips. The 1,848-combination two-skin Live render passed every time.

The two failures were the P1091 1920×1080 Classic and Modern SHA-256 assertions. All 21 overlap fixtures passed their ink and geometry checks. The P1091 method also ran three times by itself, with the same 19 passes and two hash failures each time. As a control, the game library was rebuilt from the archived source of `28debb3c` and loaded into the same proof host with the same content. Three base renders per style reproduced the changed hashes exactly. Switching to Mesa softpipe changed both base hashes again. The software-rendered radar comparison above uses the same driver on both sides. [Graphics-environment ledger entry](../TEST_FLAKES.md#p1096-software-rendered-p1091-pixel-controls)

| P1091 capture | SHA-256 of all RGBA pixels on llvmpipe, base and indexed |
|---|---|
| Classic, 1920×1080, default | `c100f69a43c7a4e8de520f90d3dd9675ee813ba149f6a7bf83715465e78c73c1` |
| Modern, 1920×1080, default | `1093b39b5b9ba1616bf0c8ad8d27d83ba6dc81ff5b1df46ebec58618246f6364` |

### Fast sweeps

The first sweep ran 16,205 cases serially by project in 18 min 5 s, with 16,195 passes, six skips and four failures. All nine infrastructure checks passed. No case exceeded the calibrated 16.387 s limit. This run included all six desktop P1096 cases. The two P1091 hashes are covered above. The session visibility test expected side 1 and saw lobby side −2. It passed in three isolated hosts, matching the [three-peer logon timing entry](../TEST_FLAKES.md). The small dedicated boot test expected five workers and saw four because the four-core build setting was still in the test environment. It passed three times with the container's normal processor count, matching the [P1049 low-core control](../TEST_FLAKES.md). The final sweep used the normal processor count and included the additional six linked KNI cases.

The final `tools/run-tests.sh --tier fast --parallel 1 --no-build` sweep ran **16,211 cases in 15 min 23 s**. It passed 16,201, skipped six and failed four. All nine infrastructure checks passed. The calibrated limit was 11.508 s and there were no timing offenders. All 12 P1096 desktop and KNI cases passed. The P928 supplemental golden belongs to the Live tier and was outside these runs.

| Final Fast failure | Isolated follow-up | Classification |
|---|---|---|
| P1091 1080p default Classic and Modern | Same two hash failures in all three method-only runs. Three base controls per skin matched the changed build. | Reproducible on this graphics stack, recorded above |
| DeadLatchRespawnLiveTests remote death and respawn | Passed in 1.461, 1.459 and 1.411 s | Existing remote death receipt signature, 2,500 polls without the observer latching the death |
| PlayerBuildCommandLiveTests legal build | Passed in 1.430, 1.463 and 1.483 s | Empty construction-effect collection after the order at line 240. Passed in the first sweep. Newly recorded in the [flake ledger](../TEST_FLAKES.md#p1096-fast-sweep-construction-receipt) |

The repository-wide Fast gate remains red. Both P1091 reference images and all existing test assertions are intact.

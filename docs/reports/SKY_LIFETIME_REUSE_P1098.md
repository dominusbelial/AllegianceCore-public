# P1098 sky lifetime and draw-state reuse

## Brief items

| Item | Status | Evidence | Time |
| --- | --- | --- | --- |
| Baseline measurements | done | Three fresh processes per sky, six device lifetimes and three pairs of local skirmishes | 5 min |
| Sky lifetime | done | No live handles at unload. A same-size reset rebuilt a pixel-identical destination target. | 3 min implementation and first checks |
| Draw-state reuse | done | Six warm windows fell to zero game-thread bytes per draw. | 2 min alongside lifetime |
| Draw and pixel equivalence | done | 36 fixture captures and 36 installed-art captures matched the base in pixels and draw counts. | 5 min including the portable controls |
| Fast tests and deliberate breaks | done | The lifetime test and all three sky theory cases passed. Nine mutations failed their intended cases. | 4 min including isolated rebuilds |
| Numbers and report | done | Allocation, CPU time, resource counts, pixel comparisons, mutations and three isolated runs of each unrelated failure are recorded below. | 16 min including the Fast sweep and triage |
| Green full Fast sweep | not done | Two pre-existing HUD golden mismatches and a ledgered station-elimination timeout. Seven cases exceeded the calibrated time limit. | 11 min 47 s |

Times are approximate elapsed intervals. Fixture construction, measurement and verification overlapped.

## Baseline on 28debb3c

Each of three processes loaded, unloaded and recreated Game1 at display densities 1 and 1.5. Each cycle had 8 cold resources and 27 live warm resources in the sky owners. Of 28 distinct handles retained across the cluster switches, 25 were still live immediately after UnloadContent. The same-size reset reused a postcard marked rendered. Across three pairs of real local skirmishes, the installed sky owners stayed the same. The fixture counted 24 current resources after each match and 23 still-live handles from the first match after the second.

## Ownership and draw state

Game1 unloads the aleph renderer first because it borrows the active sky. It then releases all three tiers and the three separate pre-sector starfields. The tier latches are reset for the next load. HD star and nebula textures share a cache, which now disposes the HD star texture once. The giant-object renderer used to drop its vertex buffer reference on a sector switch before the mesh builder could dispose it. A destination glimpse also visits this path. The renderer now disposes the outgoing buffer when leaving its cluster.

A painter lazily creates its own BasicEffect on its graphics device. Both heads use the same preparation for texture selection, texture enablement, diffuse tint, world, view and projection. Vertex colours and unlit shading are fixed when the private effect is created. Tier entry pins depth off. Individual passes select opaque, additive or premultiplied blending, and the tier restores the incoming device state. The planet, moon and distant-object painters each retain four corners. The flare retains its own screen-space vertices. These quads share six immutable indices. Native star colours and shell positions are now built when the count or seed changes. HD reuses its dome meshes and explicitly switches the shared dome effect between white stars and intensity-scaled nebulae.

A graphics-device reset releases the cached destination targets. The next glimpse renders at the existing 256-square resolution and restores the current cluster. The effect and geometry owners remain valid across viewport changes. The existing native poster placement and flare table drive the same draws. [Game1 unload](../../src/Allegiance.ClientCore.Game/Game1.cs) · [Effect preparation](../../src/Allegiance.ClientCore.Game/Rendering/SkyEffect.cs) · [Destination cache](../../src/Allegiance.ClientCore.Game/Rendering/AlephGlimpseRenderer.cs)

## Measured draw costs

Three paired fresh-process runs used Debug DesktopGL under Xvfb, software llvmpipe (LLVM 19.1.7, Mesa 25.0.7), and the same generated artwork. Runs 1 and 3 used base first. Run 2 used the changed assembly first. Each sector received eight warm-up draws. Calling-thread allocation covers the next 32 calls to Game1’s sky dispatcher. A separate 160-draw window used Linux’s thread CPU clock. Readback, sector decoding, destination preparation, HUD drawing and presentation are outside these windows. All three allocation samples in each cell were identical. CPU values are microseconds per draw on the software renderer. Concurrent suite/build activity contributed to their spread. There were no lock changes.

| Sky | Scene | Base B/draw | Changed B/draw | Base thread µs, runs 1 / 2 / 3 | Changed thread µs, runs 1 / 2 / 3 |
| --- | --- | ---: | ---: | --- | --- |
| Black | Start sector, sim stars and glare | 77,000 | 0 | 650.1 / 537.1 / 521.5 | 514.6 / 446.5 / 449.1 |
| Black | Destination sector, legacy layers | 64,592 | 0 | 362.0 / 270.5 / 275.6 | 238.4 / 197.1 / 223.3 |
| Native | Start sector, sim stars and glare | 39,312 | 0 | 232.0 / 147.5 / 139.4 | 125.1 / 117.4 / 122.4 |
| Native | Destination sector, legacy layers | 57,320 | 0 | 133.2 / 116.1 / 115.6 | 51.4 / 33.1 / 38.4 |
| HD | Start sector, sim stars and glare | 39,312 | 0 | 195.6 / 156.5 / 176.4 | 169.2 / 109.5 / 119.2 |
| HD | Destination sector, legacy layers | 37,456 | 0 | 84.4 / 68.4 / 74.5 | 39.0 / 24.6 / 24.9 |

## Resource counts

The inventory retains strong references to unique graphics handles reachable from all three tiers, the three fallback starfields and the aleph renderer. It samples after load, after drawing all tiers, after a destination switch and after reset. Holding the earlier samples catches a live handle even if a renderer drops its last reference. Counts exclude device-internal shader caches and unrelated game resources. Game1 exits and ticks its normal UnloadContent path before the graphics device is disposed. The recreate probe repeats the cycle with a new Game1 at 1.5× display density. Results repeated in three processes.

| Inventory point | Base | Changed |
| --- | ---: | ---: |
| Live immediately after load | 8 | 8 |
| Live after warming all tiers and a destination | 27 | 36 |
| Distinct handles sampled over the cycle | 28 | 41 |
| Still live immediately after unload | 25 | 0 |
| Destination preparations on first use after a same-size reset | 0 | 1 |
| Current sky handles after real skirmish 1 / 2 | 24 / 24 | 34 / 34 |
| Handles from the first match still live after the second | 23 | 32 |
| Of those, live but missing from the second match’s owners | 1 | 0 |

The skirmish probe starts and stops two Small local spectator skirmishes through Game1, waits for each host to retire and draws the same authored sector in each match. It checks owner identity across the restart. The higher warm inventory pays once for retained drawing effects. The baseline’s orphan was the previous giant-object vertex buffer. The reset test also checks that the retired target was disposed, the replacement has identical RGBA pixels, and the cache rebuild happened exactly once on first use. A counted HD cache texture separately observes Dispose attempts, including repeated calls MonoGame would otherwise suppress.

## Pixel controls

Two different textured planets, native domes, HD nebulae, a star map, flare surfaces, moons and a giant patch make the input reproducible without external media. A real SET_CLUSTER moves the pilot. The proof calls Game1’s sector dispatch and the aleph renderer against the same tier. An independent cold painter renders the destination, and a second cold painter renders the current sector. Each skin uses its actual HUD renderer with fixed telemetry. This compares all pixels and submitted draws on the executing device. The reference llvmpipe driver additionally checks the full SHA-256 of each readback against the pre-change build. The base and changed builds produced identical reset frames at the resized dimensions.

Each cell gives **aleph / Classic / Modern** draw counts. The destination target is 256 × 256. The flight captures are 480 × 320 before and after the warp and 640 × 400 after resize and after same-size reset.

| Sky | Before warp | After warp | Resize | Same-size reset |
| --- | --- | --- | --- | --- |
| Black | 15 / 50 / 64 | 16 / 51 / 65 | 16 / 51 / 65 | 16 / 51 / 65 |
| Native | 3 / 47 / 61 | 3 / 39 / 53 | 3 / 39 / 53 | 3 / 39 / 53 |
| HD | 3 / 47 / 61 | 3 / 39 / 53 | 3 / 39 / 53 | 3 / 39 / 53 |

Full pre-change digests are in the [frame manifest](../../tools/p1098-sky-proof/frames.txt). The same-size reset uses the resized golden. The frame fixture checks both cold destination renders and hot composites. The allocation test requires zero total bytes over 32 draws on the reference driver. On other backends it permits less than 2,048 B/draw for bookkeeping, below the cost of one former per-draw effect.

A second comparison used the installed artwork directory as the Game1 asset source. A scratch copy of the fixture selected native `nebplnt06bmp` and `nebplnt33bmp` for the two sectors and compared the archived base and changed Game assemblies directly. Both used the same installed dome meshes, HD textures and art libraries. All 36 complete readback hashes and draw counts matched, including the aleph targets. Both black-sky allocation windows measured 77,232 B/draw on the base and zero after the change. The native and HD windows also measured zero after the change. The fixture’s generated goldens keep the Fast checks independent of the installed art revision.

## Deliberate breaks

Each mutation ran in a separate build of the rendering assembly against the Fast tests. The working renderer used for qualification was independent of these builds.

| Mutation | Observed result |
| --- | --- |
| Omit Game1’s HD sky disposal | Five live sky handles after unload |
| Remove the device-reset subscription | The same-size reset retained a stale postcard |
| Drop the outgoing giant vertex buffer reference without disposing it | Two live handles after unload across the destination round trip |
| Restore per-draw construction of the four billboard vertices | All three tiers failed the zero-allocation assertion. Over 32 frames the quad arrays cost 11,520 B in black and 3,840 B in native and HD. |
| Allocate a 4 KiB array on every billboard draw | All three tiers failed the measured ceiling, at 12,360 B/draw for black and 4,120 for native and HD |
| Draw the HD nebula at full intensity | The destination pixel golden failed for HD |
| Shrink the planet poster half-extent by 20% | The destination pixel golden failed for all three skies |
| Restore the destination instead of the current sector after warming a glimpse | All three Classic composites failed. Black submitted a different draw count. Native and HD submitted different pixels. |
| Dispose the HD star texture by both its cache entry and active reference | The instrumented texture received two releases |

## Reproduction

Build the solution in the selected configuration. The Fast tests generate deterministic small artwork and substitute the small shipped font atlas for the Modern large font, as in P1090. The skirmish measurement additionally uses the locally configured artwork/core and the built bot driver.

```sh
set -a
. ./local.env
set +a
export DOTNET_ROOT="$(dirname "$(command -v dotnet)")"
dotnet test tests/Allegiance.ClientCore.MonoGame.Tests --no-build --filter FullyQualifiedName~P1098SkyTests
for scene in black native hd lifetime skirmish; do
  LIBGL_ALWAYS_SOFTWARE=1 ALSOFT_DRIVERS=null xvfb-run -a \
    dotnet tools/p1098-sky-proof/bin/Debug/net10.0/p1098-sky-proof.dll "$scene" --measure
done
```

## Gate and flake qualification

Debug and Release solution builds, including the source-shared browser renderer, each finished with zero warnings and zero errors. All 158 standalone tool projects built with `tools/build-tools.sh --drop-bins`. The first tool sweep lacked the three KNI pipeline packages in the initial offline feed. Adding their existing local package archives and rerunning the script completed the gate. NuGet package and HTTP caches and XDG configuration were under `.scratch/`. The four new Fast cases passed three isolated Debug runs and a Release run. The directly affected selections passed three times each with zero skips. Each run covered 120 desktop tests (including the shared P1090 fixture), 20 browser tests and 36 Core sky-math tests. The glossary checker validated 1,189 paths and the parity scorecard completed. [Fast tests](../../tests/Allegiance.ClientCore.MonoGame.Tests/P1098SkyTests.cs) · [Pixel probe](../../tools/p1098-sky-proof/SkyFrames.cs) · [Resource inventory](../../tools/p1098-sky-proof/SkyInventory.cs)

The full Fast tier ran 16,203 cases in 706.9 seconds. It passed 16,194 and skipped six. Three assertions failed. Seven executions exceeded the calibrated 13.366 s limit, including the station-elimination failure. All four P1098 cases and the nine Fast infrastructure checks passed. The final focused runs additionally exercised the tightened zero-allocation assertion and the reset/ownership controls.

- The 1920 × 1080 default Classic and Modern P1091 overlap cases returned zero overlap errors and different full-frame hashes from their checked-in goldens. Both failed in all three isolated runs. The remaining 19 cases passed each time. Replacing the Game assembly with the original `28debb3c` build reproduced the exact local Classic and Modern RGBA hashes. The software-renderer signature is recorded in the [flake ledger](../TEST_FLAKES.md#p1098-fast-qualification-2026-11-27).
- `SideEliminationLiveTests.Destroying_last_restart_station_eliminates_side_and_declares_winner` timed out on the lethal station hit at 20.769 s. It passed in 0.716, 0.640 and 0.744 s in isolation. The same lethal-hit timeout and three-pass qualification already appear in the [flake ledger](../TEST_FLAKES.md).

The remaining six timing overruns passed their assertions in the full tier. Every case passed in each of three serial, isolated runs. These fixtures already have timing entries in the [flake ledger](../TEST_FLAKES.md).

| Fixture | Full tier, s | Cases per isolated run | Slowest case in each run, s |
| --- | ---: | ---: | --- |
| P796 varied loadout, civ 35 | 14.231 | 3 | 6.160 / 6.209 / 6.256 |
| P814 gunner turret seats | 15.491 | 1 | 3.654 / 3.693 / 3.566 |
| SC2 nanite support, civ 25 | 14.093 | 2 | 4.584 / 4.730 / 4.812 |
| P1000 civ profiles, civ 3 | 14.522 | 10 | 1.991 / 1.663 / 1.899 |
| P999 civ buy gates, civ 5 | 15.419 | 10 | 4.639 / 4.502 / 4.531 |
| P800 real-core hull rendering | 17.489 | 1 | 2.854 / 3.004 / 2.849 |

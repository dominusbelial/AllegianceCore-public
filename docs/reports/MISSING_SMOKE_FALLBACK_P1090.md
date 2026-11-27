# P1090 · Missing smoke atlas in flight

## Brief items

| Item | Status | Evidence | Elapsed |
| --- | --- | --- | --- |
| 1. Reproduce the three atlas failures | done | Pre-fix Fast theory failed with DivideByZeroException for missing, invalid and fourth-frame upload. Both healthy controls passed. | 11 min including offline restore and fixture construction |
| 2. Select native smoke and release partial frames | done | All three flight cases passed. Four allocated Cloud01 frames were disposed after the injected fourth upload failed. Each failed load logged the fallback once across both sectors. | 1 min after reproduction |
| 3. Compare healthy Enhanced and Volumetric draws and pixels | done | Four effect passes per tier matched the reference at every pixel. Before/after hashes below. | 1 min, overlapping item 2 |
| 4. Exercise two sectors and verify deliberate breaks | done | Six Fast cases passed 3/3 times. Five separately reverted mutations produced the failures listed below. | 5 min for mutations, builds and isolated verification, following item 2 |
| 5. Document cause, change, tests and gate results | done | Root cause, measured pixels, mutations and all gate exceptions are recorded below. | 20 min for the gate and qualification, with report updates throughout |

## Failure and repair

At base `138e61db`, `VfxAssets.cs:87–117` caught open, decode and upload exceptions and returned zero frames. The catch could no longer reach the texture array allocated inside the try. An upload failure abandoned even the texture whose SetData threw. `Game1.Lifecycle.cs:868–869` installed the field regardless of the result. An eligible puff reached the remainder at `EnhancedSmokeField.cs:129` before the length check at line 134. The exception propagated through Game1.Draw.

The loader now owns every slice until the entire grid succeeds. Failure disposes each allocated slice, including the current one. A non-integral grid or an unusably small feathered cell follows the same fallback. Game1 passes a null smoke field to the existing engine, missile and damage renderers when Cloud01 fails. The explosion dispatch then uses the existing native blast renderer for Enhanced and retains the independent volume renderer for Volumetric. The smoke field also returns before any frame arithmetic on an empty atlas. The single load failure message names the fallback.

## Rendering evidence

The Fast theory runs a DesktopGL child with a generated two-sector recording, synthetic native textures and geometry, and the shipped CC0 atlases and shaders. SET_CLUSTER changes the flight view. In each sector the fixture seeds the engine and damage particle pools and a small-ship explosion, calls RunOneFrame, and checks the frame counter and all three production draw lists. A small shipped font substitutes for the large Modern HUD font to keep unrelated distance-field generation within the Fast time budget.

The isolated 96 × 96 passes use fixed poses and ages. Each compares the renderer installed by Game1 with the original healthy construction or the explicit native construction on the same device. The test checks visible pixels, draw counts and all 9,216 RGBA values per pass. The following values are draw counts and the first 16 hexadecimal digits of SHA-256 over the readback. Both healthy rows were measured before the repair and repeated after it on software DesktopGL.

| Configuration | Engine smoke | Missile smoke | Damage smoke | Blast |
| --- | --- | --- | --- | --- |
| Healthy Enhanced | 6 / CF5C504B11AD88D7 | 5 / B4F8A773F0668C48 | 3 / EC75D6597B449E1D | 5 / 29C95ECEF0CD578A |
| Healthy Volumetric | 6 / CF5C504B11AD88D7 | 5 / B4F8A773F0668C48 | 3 / EC75D6597B449E1D | 4 / 104F05C5C17E2129 |
| Enhanced with failed Cloud01 | 1 / CBE375A4F1EB546B | 1 / CBE375A4F1EB546B | 1 / 83C49F03E6B16D4B | 2 / D140F6F7C858EEB2 |

## Deliberate breaks

Each mutation was applied separately to the repaired code. Each ran all six theory cases. The sources were restored between runs.

| Deliberate break | Failing cases | Observed failure |
| --- | --- | --- |
| Install the enhanced field for zero frames | Missing, invalid PNG, invalid grid, partial | Wrong installed smoke renderer |
| Remove the guard before frame selection | Same four | Direct draw of an empty field threw DivideByZeroException |
| Omit disposal from the loader catch | Partial | All four allocated textures remained live |
| Wire the engine trail to native smoke for a healthy atlas | Enhanced, Volumetric | Engine draw count or pixels differed from the original construction |
| Accept fractional cells in a decoded atlas | Invalid grid | A 17 × 16 image produced 64 truncated frames for the 8 × 8 grid |

The original three crash cases and two healthy controls ran before the fix. The sixth case covers an additional failure mode discovered while validating grid dimensions. On the reference rasterizer the healthy tests also check the pre-fix hashes above. On other drivers the full same-device pixel comparison remains active.

## Gate

Debug and Release solution builds each finished with 0 warnings and 0 errors. The proof project is registered in the solution so the Release test also launches a Release child. The six new cases passed three isolated Debug runs and one Release run. The 87 directly related VFX, emitter, flight-pool and volumetric cases passed in each of three isolated runs. All 279 required focused test executions passed with zero skips. The solution and tools used a local package feed and scratch-local caches. All 157 tool projects built. The glossary check validated 1,178 paths.

The requested full Fast sweep ran 16,170 cases in 649.9 seconds. It recorded 16,160 passes, five skips and five assertion failures. All six P1090 cases passed. Two other passing assertions exceeded the calibrated 10.000 s limit. The broad gate was red. The five assertion failures were bundle-path fixtures whose temporary installs inherited the checkout’s local configuration. A separate control gave the same physical scratch directory an independent path ancestry and all nine bundle-path cases passed.

| Full Fast exception | Three isolated runs under the original conditions | Existing ledger entry |
| --- | --- | --- |
| `PlayerBundlePathsTests.UsesExecutableDirectoryOutsideRepository` (Artwork), `MissingDirectoryHasNoDefault` (Artwork and NativeSource), `LocalConfigWinsOverBundleWithBlankEnvironment`, `NativeSourceNeverGuessesAnInstallSubdirectory` | All five failed in each run. The SkyPack companion cases passed. A scratch directory accessed through an open directory descriptor made the fixtures independent of the checkout’s ancestor configuration. The whole class then passed 9/9. | [P968 fixture ancestry qualification](../TEST_FLAKES.md#p968-qualification--publisher-index-2026-11-08-utc) |
| `P777WarpExitWobbleTests.Npc_exit_touch_rate_is_measured` (10.489 s) | Passed in 8.926, 9.167 and 9.130 s. | [P1083C warp timing](../TEST_FLAKES.md#p1083c--browser-shader-load-gate-2026-11-24) |
| `P999CivStrategyTests.Every_profile_is_reachable_in_catalog_and_actual_side_buy_gates` (civ 5, 14.779 s) | All ten cases passed in each run. Longest case per run was 4.241, 4.212 and 4.202 s. | [P1085 strategy timing](../TEST_FLAKES.md#p1085--backfill-recovery-qualification-2026-11-25) |

Each exception matched an existing ledger entry. The P928 supplemental golden belongs to the Live tier.

To repeat the focused test from the checkout, run `dotnet test tests/Allegiance.ClientCore.MonoGame.Tests --filter FullyQualifiedName~P1090`. Each case builds on the same fixture and can run without the private artwork corpus.

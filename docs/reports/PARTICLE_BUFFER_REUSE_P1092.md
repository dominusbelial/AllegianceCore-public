# P1092 particle and trail buffer reuse

The busy replay shed about 951 kB of game-thread allocation per rendered frame in both HUDs. Six full-frame RGBA comparisons matched byte for byte.

## Brief items

Elapsed times are approximate wall-clock intervals. Measurements, development and verification overlapped.

| Item | Status | Evidence | Elapsed |
|---|---|---|---|
| 1. Measure the base | done | Three fresh processes per skin and sector, plus a separate scoped profile | 6 min |
| 2. Reuse and bound storage | done | Renderer-owned staging, atlas bins and quad arrays. Retention and growth test passed | 2 min implementation |
| 3. Preserve output | done | Six complete replay frames, two HUD fixtures, burst-to-quiet draw checks | 10 min including setup |
| 4. Fast differential and pixel tests | done | All four cases passed three times. Three isolated code mutations turned all four red | 8 min including oracle and mutation runs |
| 5. Measure the change | done | Twelve changed-build slices and a final 24-process paired confirmation | 4 min + 6 min confirmation |
| 6. Report and qualification | done | Source attribution, equivalence, mutations, builds and Fast qualification below | 39 min elapsed for the brief |
| Green full Fast sweep | not done | Eight unrelated failures and two timing violations. All qualified below | 17 min sweep plus isolation |

## Rendered replay

Base and local master were both `138e61db` at dispatch. The first twelve baseline processes completed before the production edits. Each fresh .NET 10.0.403 FSI process ran the Debug desktop client at 960 × 540 with server GC, shipped content, installed artwork, SDL offscreen, software GL, null audio and vsync off. The fixture seeks `battle.alr` to logical frame 6500, warms 60 rendered frames, then times 180 complete `RunOneFrame` calls. Bytes are calling-thread deltas. Gen0 is process-wide collections × 100 / 180. Wall times include presentation.

All modest runs ended with 18 ships, 6 poses and 20 HUD contacts. Busy runs ended with 66 ships, 54 poses and 68 contacts, with peaks of 96 gathered bolts and four scene explosions. The 48 injected ships, projectile instances and explosions are constructed outside the timing window. The updated harness also checks those counts and the selected skin before printing a result.

Each pre-edit baseline cell gives the three measurements in order.

| Scene | Game-thread B/frame | Gen0/100 frames |
|---|---:|---:|
| Modest Modern | 485,064 / 485,096 / 485,021 | 0.00 / 0.00 / 0.00 |
| Modest Classic | 490,963 / 491,086 / 490,936 | 0.00 / 1.11 / 0.00 |
| Busy Modern | 1,373,457 / 1,373,394 / 1,373,430 | 1.11 / 1.11 / 1.11 |
| Busy Classic | 1,387,622 / 1,387,578 / 1,387,459 | 1.67 / 1.67 / 1.11 |

For the final comparison, each base process was paired with a process running the final changed binaries. Pair order was base first in rounds 1 and 3, change first in round 2. This series began after all builds, the Fast sweep and isolated reruns had finished. Each matched pair had the same submitted draw count.

| Sector, skin, run | Base B/frame | Changed B/frame | Gen0/100 base / changed | Mean ms base / changed | p95 ms base / changed | p99 ms base / changed |
|---|---:|---:|---:|---:|---:|---:|
| Modest Modern 1 | 485,084 | 261,930 | 0.00 / 0.00 | 5.90 / 6.39 | 7.18 / 7.83 | 7.76 / 8.36 |
| Modest Modern 2 | 485,133 | 261,950 | 0.00 / 0.00 | 6.49 / 5.94 | 7.68 / 7.26 | 8.27 / 7.77 |
| Modest Modern 3 | 485,185 | 261,954 | 1.11 / 0.00 | 6.55 / 6.18 | 7.97 / 7.58 | 15.37 / 8.60 |
| Modest Classic 1 | 490,961 | 267,810 | 0.00 / 0.00 | 6.67 / 6.32 | 8.08 / 7.87 | 8.66 / 8.89 |
| Modest Classic 2 | 491,108 | 267,824 | 1.11 / 0.00 | 7.03 / 6.14 | 8.38 / 7.51 | 15.96 / 8.28 |
| Modest Classic 3 | 490,970 | 267,826 | 0.00 / 0.00 | 6.38 / 6.30 | 7.78 / 7.88 | 8.66 / 8.85 |
| Busy Modern 1 | 1,373,472 | 422,656 | 1.67 / 0.00 | 9.85 / 9.65 | 11.22 / 10.78 | 23.64 / 11.99 |
| Busy Modern 2 | 1,373,405 | 422,692 | 1.11 / 0.00 | 10.21 / 9.88 | 12.57 / 11.24 | 28.44 / 12.24 |
| Busy Modern 3 | 1,373,425 | 422,664 | 1.11 / 0.00 | 9.87 / 9.55 | 11.43 / 10.81 | 18.82 / 11.38 |
| Busy Classic 1 | 1,387,605 | 436,794 | 1.11 / 0.00 | 14.42 / 13.90 | 17.39 / 18.08 | 28.23 / 19.14 |
| Busy Classic 2 | 1,387,560 | 436,818 | 1.11 / 0.00 | 14.28 / 13.82 | 17.84 / 17.34 | 26.57 / 19.77 |
| Busy Classic 3 | 1,387,538 | 436,882 | 1.11 / 0.00 | 14.44 / 13.68 | 18.92 / 16.79 | 25.38 / 17.85 |

The allocation reduction is about 223 kB/frame in the modest fixture and 951 kB/frame in the busy fixture. All twelve changed windows recorded zero Gen0 collections. Busy mean wall time fell in each matched pair, by 0.21–0.32 ms in Modern and 0.45–0.76 ms in Classic. Modest Modern moved in both directions. These are short software-renderer windows and frame-time variation is larger than the differences in some pairs.

### Call-site attribution

A separate busy Modern process per revision used `GC.GetAllocatedBytesForCurrentThread()` around each gather or geometry build in a scratch copy of the rendering assembly. Counters were cleared after warm-up. These are non-overlapping byte deltas on the game thread over 180 frames. Driver calls, downstream renderers and other client work are outside these regions. CLIENT-R1's earlier 47 MB figure was a sample by type from a different slice.

| Scope | Base bytes | Changed bytes | Change B/frame |
|---|---:|---:|---:|
| ShipEngineTrailRenderer.Draw, fire and expanded trail puff staging | 15,122,968 | 131,752 | −83,285 |
| DamageDebrisRenderer.Draw, expanded vent puffs | 47,230,560 | 14,400 | −262,312 |
| EnhancedSmokeField.Draw, density admission and grouping before draw dispatch | 44,382,848 | 32,328 | −246,392 |
| SoftParticleRenderer.TryDraw, billboard vertex and index build | 64,253,744 | 0 | −356,965 |
| **Total** | **170,990,120** | **178,480** | **−948,954** |

The missile-trail and enhanced explosion staging scopes each recorded zero bytes in this slice on both revisions. The scene explosion count alone does not identify which quality renderer drew a blast. Small allocations remain in gathering, including enumerators and buffer growth.

## Storage and equivalence

- Ship trails, missile trails, vent smoke and the enhanced explosion layers own their lists. Each active gather clears its lists. Inputs whose maximum emitted count could exceed **8,192 entries per staging list** use call-local storage. Sub-puff and decal multiplicities are included in that bound. Oversized input still emits every item. Repeated over-limit draws can allocate again.
- A smoke or glow atlas owns a dictionary of lists, one per distinct texture. Each call records the textures in first-encounter order. Aliased frames share a bin. Appends preserve source order within the bin. The draw iterates only the active list, then clears the bins in a `finally` block. Each bin retains at most **512 billboard slots** after the call. The fixed atlases have 64 smoke frames and 25 glow frames.
- The BasicEffect batcher and soft renderer grow their own vertex and index arrays. Each retains at most **8,191 quads**, or 32,764 vertices and 49,146 short indices. The draw consumes four vertices and two triangles per used quad. The existing billboard draw keeps its original chunk boundaries. The other overloads preserve a single submission, using temporary arrays for oversized batches.
- Emission seeds, position arithmetic, UVs, triangle winding, age and fade functions, tint premultiplication, blend states, depth unbinding and pass order follow the original paths. The shared smoke budget resets on every nonempty call, including successive emitters in one frame. Its original replacement policy still admits a new puff after the budget fills. P1090 was on a separate branch when work began. No atlas-loading or fallback code changed, and no new native rule was ported.

## Tests and pixels

The Fast CPU differential compares first-seen texture order and every used vertex and index against an independent allocating oracle. It uses repeated texture identities, random angles, position-derived and explicit seeds, clamped jitter, filtered puffs and consecutive input sizes 0, 1, 14, 1,600, 2, 0, 63 and 7. It checks budget count after each call and retained bin capacity after clearing. A separate test sends an oversized batch between two small batches and checks the original vertex/index array identities. A direct DesktopGL control combined oriented patches, axis quads and billboards at 0, 2, 61, 8,192, 1 and 0 items per draw. All six base and changed raster hashes matched.

The Fast GL theory runs `hud2-proof --particle-pixels` in a separate offscreen process per skin. It draws the shipped Cloud01 textures and actual Classic or Modern HUD, compares every RGBA pixel to the allocating reference, and checks that the burst changes pixels. Each skin covers soft and ordinary premultiplied draws at 21, 1 and 0 puffs. The fully visible depth fixture lets the same reference cover both paths.

| Temporary break in an isolated build | Observed failure |
|---|---|
| Reallocate reusable geometry for every positive count | Retention test failed on array identity after the oversized draw |
| Insert newly active textures at the front of the draw order | Randomized stream test failed on the texture sequence |
| Change the soft path's destination color blend to One | Both skin cases failed on the soft 21-puff RGBA comparison |

The complete busy replay also matched at logical frames 6561, 6621 and 6740 in separate base and changed processes. For this comparison only, the existing `ALLEGIANCE_BENCHMARK_FRAME` capture mode suppresses the wall-clock profiler overlay. The first comparison with telemetry visible differed in its bottom 92-pixel strip. With the overlay suppressed, **0 of 518,400 pixels differed in each of the six pairs**, with no masks or tolerances. With telemetry enabled the busy runs averaged 624.86 draws for Modern and 750.86 for Classic on both builds.

| Skin | Frame 6561 RGBA SHA-256 | Frame 6621 | Frame 6740 |
|---|---|---|---|
| Modern | `2b7b65d37e47b2befbbd939070793ad965869917c8a6a4e7f220fbcef2c1b101` | `261073b48b5ebcb27a372ac726afba5f715dfb5ec518608fd934026e4c033c64` | `b2b54fcf575f17842abf5d1578bca3af13aba789620f9c2c67fb596e968003fe` |
| Classic | `a21013b6201d7add895eb208218ed4dcb1c690dce2cbc4165192a05dfbc0bad3` | `5312bc04a6cd09a129ce01bb96b58dfaa819316c58f6c6db91eeec6cc97497f3` | `03cb84a8bbc84491c97ed8deea7eddc46445ed9911ab3a0118377d1291822322` |

## Reproduction and gates

Use the environment from CLIENT-R1's measurement commands, including `CLIENTBIN` for the matching Debug output and `ALLEGIANCE_ARTWORK_PATH` for local artwork. Keep the SDK host's server GC for short slices. Run from the repository root, repeating each command in a fresh process per sample.

```bash
export SDL_VIDEODRIVER=offscreen ALSOFT_DRIVERS=null ALLEGIANCE_NOVSYNC=1
export LIBGL_ALWAYS_SOFTWARE=1 MESA_SHADER_CACHE_DISABLE=true
export LD_LIBRARY_PATH="$CLIENTBIN/runtimes/linux-x64/native"
for run in 1 2 3; do
  for skin in modern classic; do
    for scene in modest busy; do
      "$DOTNET_ROOT/dotnet" "$DOTNET_ROOT/sdk/10.0.403/FSharp/fsi.dll" \
        --nologo --quiet --lib:"$CLIENTBIN" tools/p1092-particle-slice.fsx "$skin" "$scene"
    done
  done
done
```

Debug and Release solution builds passed with zero warnings and zero errors. Package restore used a local feed and scratch caches. The Release retry limited MSBuild to two nodes after a parallel worker exited prematurely. The focused gate passed **121 cases per pass, three times**, including the four new Fast cases and the directly affected shader, density, trail, curve, depth, billboard, shield and hull-spark classes. All 156 standalone tool projects built with zero warnings. The glossary check passed (1,178 paths).

### Fast sweep and isolated follow-ups

The complete run used `tools/run-tests.sh --tier fast --parallel 1` with artwork, scratch-local caches, XDG configuration and TMPDIR. Across seven projects, **16,155 passed, 5 skipped and 8 failed**. All nine Fast infrastructure checks passed. Two executions exceeded the calibrated 10.0 s limit. None of the failures or time overruns was in the changed renderer or its tests.

| Case | Sweep | Three isolated reruns | Prior ledger match |
|---|---|---|---|
| Five PlayerBundlePaths cases | Each found the checkout configuration while testing a simulated external install | Same five failed 3/3 under the literal scratch TMPDIR. With the documented directory-FD alias into the very same scratch directory, all nine class cases passed 3/3 | Worktree TMPDIR and ancestor `local.env`, P974 |
| `BallotVotingLiveTests.Resign_passing` | Expected terminal chat had not arrived | passed, passed, passed (0.695 s, 0.685 s, 0.698 s) | Proposal-passed chat only, P980 |
| `MissileBroadcastTests.Directly_fired_missile` | Earlier destruction at 1,478.85 m against 1,848.56 m | passed, passed, passed (0.533 s, 0.508 s, 0.541 s) | Earlier missile destruction |
| `StationCombatLiveTests.Cross_side_missile` | Missile missed the station, 16.423 s | passed, passed, passed (0.495 s, 0.499 s, 0.508 s) | Miss at the shield-fraction assertion |
| `P777WarpExitWobbleTests.Npc_exit_touch_rate_is_measured` | Assertions passed, 10.450 s | passed, passed, passed (9.221 s, 9.344 s, 9.387 s) | Near-limit physics duration |

These signatures are already recorded in [the flake ledger](../TEST_FLAKES.md). Discovery classifies both P928 crowded-sector golden cases as Live. This light gate did not run or modify that golden.

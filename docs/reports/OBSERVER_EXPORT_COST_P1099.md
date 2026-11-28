# P1099 spectator observation export

Baseline is master `28debb3c`. Work ran from 21:35 to 22:19 UTC on November 27, about 44 minutes elapsed. Item timings overlap.

## Brief items

| Item | Status | Time spent | Evidence |
|---|---|---|---|
| 1. Baseline | done | 4 min | Six loopback matches, 600 observations and 1,214 ticks. |
| 2. Shorten the tick gate | done | 12 min | Final hold means of 0.054 and 0.074 ms. Detached pose encoding and ordered frame assembly run after release. |
| 3. Preserve observer state and cadence | done | 6 min, overlapping test development | Same-clock sequence, same-tick live oracle, camera match and unchanged Game1 deadline. |
| 4. Oracle, invalidation and live tests | done | 10 min including isolated repetitions | Three new cases and 70 directly related cases passed three times. Five deliberate mutations caused six expected failures. |
| 5. Repeat measurements | done | 8 min including controls and tuning | Three final live matches per size. See the run table and CPU comparison below. |
| 6. Report | done | 5 min across the run | Per-run measurements, parity controls, mutation results, full sweep and all isolated reruns are recorded below. |
| Green light gate | not done | 24 min including reruns | Debug, Release, all 157 tools and focused tests passed. The full Fast sweep finished red. Both persistent HUD golden failures reproduce on archived master. |

## Measurement

The measurements use the shipped Medium (15v15) and Large (30v30) presets and their normal bot quotas. Each fresh Debug process started a seeded local spectator match, waited for both teams and at least one team's worth of flying ships, warmed 20 observations, then took 100 samples with a 100 ms sleep after each apply. Each iteration used the production camera and applied the observation to a private client world. The existing host tick sampler ran over the same window. This measures headless game-thread operations and excludes rendering and presentation.

Across three runs, baseline mean gate hold was 0.160 ms at 15v15 and 0.215 ms at 30v30. Export allocated 100,325 and 109,181 bytes per observation respectively. An intermediate version scanned static terrain twice during client apply. Propagating the terrain revision removed that extra work.

### Before implementation

Each row gives the mean of 100 observations. All time columns are milliseconds. The server tick column is the host's measured work inside its tick cadence. Observation wait is the caller's time to acquire the real simulation monitor. Hold ends immediately before exiting that monitor. The byte counter is the calling thread's allocation delta around the complete Observe call.

| Teams | Run | Flying ships | Wait | Hold | Hold p95 | Export B | Client apply | Server tick | Ticks |
|---|---:|---:|---:|---:|---:|---:|---:|---:|---:|
| 15v15 | 1 | 31.86 | .0186 | .1531 | .2420 | 100,321 | .0889 | 1.872 | 202 |
| 15v15 | 2 | 31.90 | .0248 | .1533 | .2379 | 100,333 | .0756 | 1.790 | 202 |
| 15v15 | 3 | 31.86 | .0383 | .1734 | .2600 | 100,321 | .0998 | 1.850 | 202 |
| 30v30 | 1 | 62.00 | .0861 | .2197 | .3211 | 109,248 | .1065 | 2.776 | 203 |
| 30v30 | 2 | 62.00 | .0761 | .2084 | .3295 | 109,248 | .1106 | 2.846 | 203 |
| 30v30 | 3 | 62.00 | .0356 | .2158 | .3197 | 109,048 | .1182 | 2.592 | 202 |

### Base versus final change

The following repeat of the base adds Linux calling-thread CPU measurement. That control and the final candidate use the same harness. Each slash is mean / nearest-rank p95 in ms. CPU covers export and apply, including canonical ship encoding. Each run contains 100 observations. The scratch evidence uses base logs 1–3 and final change logs 4–6 under `.scratch/p1099-cost/`. Earlier change runs measured intermediate implementations. Rare multi-millisecond waits can raise the mean above p95.

| Teams | Build and run | Flying ships | Gate wait | Gate hold | Export B | Client apply | Thread CPU | Server tick | Ticks |
|---|---|---:|---:|---:|---:|---:|---:|---:|---:|
| 15v15 | base 1 | 31.9 | 0.0237 / 0.0008 | 0.2218 / 0.2882 | 100,332 | 0.1005 / 0.1585 | 0.3197 | 1.8383 / 2.3460 | 202 |
| 15v15 | base 2 | 31.9 | 0.0240 / 0.0008 | 0.1742 / 0.2442 | 100,333 | 0.0883 / 0.1396 | 0.2586 | 1.8878 / 2.5700 | 202 |
| 15v15 | base 3 | 32.0 | 0.0388 / 0.0017 | 0.2480 / 0.2911 | 100,366 | 0.0920 / 0.1223 | 0.3479 | 2.6207 / 6.1242 | 202 |
| 15v15 | change 1 | 31.9 | 0.0219 / 0.0003 | 0.0553 / 0.0832 | 78,306 | 0.0332 / 0.0568 | 0.1744 | 1.8686 / 2.4464 | 202 |
| 15v15 | change 2 | 31.9 | 0.0223 / 0.0003 | 0.0557 / 0.0845 | 78,301 | 0.0323 / 0.0452 | 0.1725 | 1.7898 / 2.1964 | 202 |
| 15v15 | change 3 | 31.9 | 0.0261 / 0.0004 | 0.0510 / 0.0773 | 78,323 | 0.0333 / 0.0620 | 0.1819 | 1.8366 / 2.3633 | 202 |
| 30v30 | base 1 | 62.0 | 0.0559 / 0.0011 | 0.2226 / 0.3319 | 109,248 | 0.1077 / 0.1695 | 0.3355 | 2.7524 / 3.3617 | 203 |
| 30v30 | base 2 | 62.0 | 0.0435 / 0.0008 | 0.1939 / 0.2966 | 109,248 | 0.1056 / 0.1635 | 0.3044 | 2.7097 / 3.3208 | 203 |
| 30v30 | base 3 | 62.0 | 0.0250 / 0.0007 | 0.2064 / 0.2961 | 109,248 | 0.1027 / 0.1632 | 0.3135 | 2.7741 / 3.3544 | 202 |
| 30v30 | change 1 | 62.0 | 0.0392 / 0.0009 | 0.0843 / 0.1509 | 94,465 | 0.0500 / 0.0778 | 0.2558 | 2.9215 / 3.7552 | 202 |
| 30v30 | change 2 | 62.0 | 0.0347 / 0.0003 | 0.0732 / 0.1030 | 94,464 | 0.0469 / 0.0717 | 0.2419 | 2.7419 / 3.3490 | 202 |
| 30v30 | change 3 | 62.0 | 0.0391 / 0.0005 | 0.0639 / 0.1110 | 94,465 | 0.0415 / 0.0765 | 0.1996 | 2.9114 / 3.3708 | 202 |

Across 300 samples per cell, **gate hold fell 75% at 15v15 and 64% at 30v30** against the repeated base. The corresponding reductions against the pre-implementation baseline were 66% and 66%. Export allocation fell 22% and 14%. Client apply fell 65% and 56%, and combined export/apply CPU fell 43% and 27%. The complete public frame still gets fresh wrappers and arrays. Keeping detached encoder inputs until publication costs some allocation and moves quantization off the tick gate.

Mean acquisition wait moved from 0.029 to 0.023 ms and from 0.041 to 0.038 ms. Mean server work moved from 2.116 to 1.832 ms at 15v15 and from 2.745 to 2.858 ms at 30v30. The pre-implementation 15v15 mean was 1.838 ms. The results support a shorter observation critical section and lower caller CPU. The tick results vary with the match and scheduling. P889's 147 ms spectator frame p95 was an end-to-end frame measurement. The headless timings here cover export and apply only.

## Change and equivalence

One observer owns the reusable sampler from capture through publication. The ticker needs only its original simulation gate, which is released as soon as capture has sampled the mission. A sector's warp/asteroid records refresh when its identity or terrain revision changes. An ordered comparison of live station records catches capture, rename, retype, death, insertion and removal. Hull and shield fractions are read every time. The client receives a new terrain identity on any such static change or mission/sector replacement.

Each sector's model list is traversed once in insertion order. The ship sample uses the existing detached CaptureModel encoder inputs, including a copy of the mutable orientation. Reading the captured ship ID when building the triple materializes the canonical 47 bytes after the gate has been released. Pod and sector samples include docked, parented and zero-fraction ships before the flying filter. The builder updates kill history from those samples in the original sector/model order. Projectile receipt times are sampled on the local clock in the requested sector. Missiles cover every sector. Research captures funded bucket names and percentages. After gate release, the sampler formats the side views and emits the roster followed by each sector's cluster, warps, stations, station health, probes, mines, asteroids and ship triples. Each frame owns its public message wrappers and result arrays.

Game1 still samples on the same 0.1-second deadline or camera step and gives the complete ordered sample to the same camera before world application. A new terrain identity triggers the original full reducer. With stable terrain, the client applies the ordered dynamic subset, retains matching dictionary slots and rebuilds a table when its key sequence changes. This preserves the iteration order of a fresh dictionary even when a new ship would otherwise reuse a departed ship's slot. Reset invalidates the client terrain identity. Local observer entry still rejects a seated pilot. Both desktop HUDs read this same world and camera. The change introduces no native game rule.

## Reproduction

The Linux measurement tool builds a scratch copy of SinglePlayer with two stopwatch probes around the simulation monitor. It links the client and server projects from the selected checkout and starts that checkout's bot driver. The production assemblies contain no timing hooks. Its raw OBS rows contain wait ms, hold ms, export bytes, apply ms, flying ships and calling-thread CPU ms for Observe plus apply. TICK rows contain server work ms. Camera.Update runs on every frame between export and apply and is outside both CPU intervals. The warm window excludes initial terrain construction and JIT startup.

Run with the local SDK on PATH, source the checkout's local.env with export enabled, and point the NuGet and XDG directories at scratch. BASE below is a local checkout or archive of master `28debb3c` including its data directory. Both builds use the same installed artwork. Each invocation after preparation starts a fresh match.

```bash
measure=tools/p1099-observer-cost/measure.py
python3 "$measure" "$BASE" base --prepare
python3 "$measure" . change --prepare
for run in 1 2 3; do
  for size in Medium Large; do
    python3 "$measure" "$BASE" base --size "$size" --run "$run"
    python3 "$measure" . change --size "$size" --run "$run"
  done
done
```

## Exactness and deliberate breaks

The test-only oracle retains the base traversal and div-240 station conversion. The Fast sequence supplies the same mission, receipt clock and camera steps to both readers. It compares every message type and encoded byte in order, warp metadata, side views, kill timestamps, projectiles and missiles. It applies the reference frame through the full client reducer and the candidate through Game1's frame entry point. Checks include ship fields, roster, ordered dictionary keys, terrain values, station health and camera IDs. A retained frame is re-encoded after later samples and rotation.

The fixture spawns, docks, parents, kills and removes ships. One ship crosses sectors and becomes a pod between capture and publication. A shot is visible only in the requested sector, while the missile in the other sector remains visible. The terrain fixture removes and reinserts a rock at the same ID and population, renames, captures and retypes a station, regenerates its health, replaces a station and removes a warp. A new mission reuses the IDs. The live test observes a real seeded local bot match on ten or more samples and runs the oracle under the same simulation lock. Each independent local receipt timestamp must lie within that capture interval. All other values compare exactly.

Five deliberately broken builds ran against the tests in scratch. Each variant used a private copy of the affected assembly.

| Deliberate break | Observed failure |
|---|---|
| Omit SingleShipUpdate during publication | The Fast sequence expected 16 objects and got 15. The live oracle expected 519 and got 509. |
| Reuse sector terrain solely by sector identity | The equal-count rock replacement expected asteroid 101 first and got 100. |
| Attribute an ejection to the new sector | Kill time 0.75 appeared under sector 20. The oracle recorded it under sector 10. |
| Treat every client dictionary order as stable | The world enumerated ships 302, 301, 303. The reference order was 301, 303, 302. |
| Ignore the station change when advancing the terrain identity | The host exported the rename but the client retained the old station name. |

As a pilot-view control, archived master and the working tree replayed the committed battle.alr through separate ConnectionSession instances. All 10,809 ordered FrameStateHash results matched byte for byte. The SHA-256 of each newline-delimited hash sequence was `a56859bf35a5ad44ee1020f757ce12f0f0dee6e65ef79a915f446435c509a435`. These hashes cover consumed wire bytes, camera markers, ship poses and visibility.

The observer is local to the embedded host. Game1 disposes the pilot session and resets the world when entering spectator mode. The command send path, visibility rules, HUD renderers, camera algorithm and dedicated server sources are outside the diff.

## Verification and flake notes

Both `dotnet build AllegianceCore.slnx` and its Release build completed with zero warnings and zero errors, including the browser projects. The glossary link check validated 1,189 paths and the parity scorecard completed successfully.

Each new test ran alone three times. The related classes also ran in isolation, including the existing live spectator/seat/rotation and skirmish objective cases. All **73 cases passed in each of three passes**, with zero skips (219 executions). Coverage includes SP5 spectator and station camera behavior, seat hotkeys, sector travel, the score roster, construction visuals, station updates and station durability. The mission-reset invalidation assertion also passed in three isolated invocations after it was added.

The first standalone-tool sweep lacked three pinned KNI shader-pipeline packages in the offline feed. All three were available in the machine's existing package cache. Restoring the exact versions locally made the shader tool build with zero warnings and errors. The full `tools/build-tools.sh --drop-bins` rerun built all 157 standalone projects with zero warnings and errors.

The Fast sweep finished in 933.2 seconds. It discovered 16,201 cases across seven projects, with 16,192 passed, six skipped and three failed. All 19 quarantine cases passed on their first invocation. One passing case exceeded the calibrated 11.769-second limit.

The Fast sweep exposed two P1091 golden mismatches at 1920×1080. A fresh build of the HUD proof from archived master produced **pixel-identical frames to P1099** in both styles on this machine. Both base and candidate had zero paint/layout errors. Full RGBA SHA-256 was `C100F69A43C7A4E8DE520F90D3DD9675EE813BA149F6A7BF83715465E78C73C1` for Classic and `1093B39B5B9BA1616BF0C8AD8D27D83BA6DC81FF5B1DF46EBEC58618246F6364` for Modern. The stored golden digests begin `DEAF3D59` and `28FB84F3`. The four software DesktopGL captures are in scratch under `rgba-base` and `rgba-change`. Three isolated invocations of the existing HUD theory each passed 19 cases and failed those same two golden comparisons. This signature is recorded in [the flake ledger](../TEST_FLAKES.md).

The other two Fast findings match existing entries in the flake ledger.

| Test | Sweep result | Three isolated invocations |
|---|---|---|
| `WarpTransitServerTests.ShipInsideWarp_GetsTransited_AndPeerSeesSectorChange` | The peer received only the old sector's status for ship 8098. | Passed in 0.291, 0.298 and 0.296 s. |
| `P999CivStrategyTests.Every_profile_is_reachable_in_catalog_and_actual_side_buy_gates` | Civ 5 passed its assertions in 11.774 s, 0.005 s over the calibrated limit. | All ten civs passed each run. Civ 5 took 3.029, 3.001 and 2.914 s. |

The full sweep and TRX files are under `.scratch/fast/`. The isolated reruns are under `.scratch/fast-followups/`. The light gate remains red for this sweep. The P928 supplemental golden was excluded as requested.

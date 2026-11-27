# P1097 sector texture warmup

Cold native rocks and planet posters now decode on bounded CPU workers. The game thread uploads rows as frame time and bytes allow, and finishes a required texture before its first draw.

## Brief items

| Item | Status | Evidence and elapsed time |
| --- | --- | --- |
| 1. Baseline measurements | done | Three 120-frame windows per workload on the instrumented base. First-draw bindings, RGBA digests and stage timings recorded under scratch (about 6 minutes). |
| 2. Bounded background preparation | done | Bounded binary, imported PNG and sibling PNG paths passed byte, deadline and pixel controls (about 11 min). |
| 3. First-visible correctness and lifetime | done | Exact RGBA in five readiness/lifetime states, both skins. Inspected disposal of private GL surfaces on interruption and shutdown (about 7 min). |
| 4. Fast tests and pixel comparisons | done | Six queue/GL cases, decoder tests and existing related suites passed three times. Eleven deliberate breaks failed their targeted checks (about 13 min). |
| 5. After measurements | done | Three paired runs per window, a separate Classic parity run, and 360-frame source substitution controls (about 6 min). |
| 6. Report and flake notes | done | Measured tables, gate receipts and isolated reruns below. Report, qualification and triage spanned about 39 min, including two overlapping build/test cycles. |

Times are approximate wall time per activity. Builds, probes and test runs overlap.

## Workload

The base is `28debb3c`. The Linux DesktopGL harness uses the installed artwork, the
960 × 540 battle replay, a fresh client per sample, and the benchmark's fixed
camera clock. It seeks to logical frame 6500, renders 40 origin frames, then
drives the production approach seed and promotion with explicit viewed sectors.

| Window | Schedule within 120 rendered frames |
| --- | --- |
| Cold | Approach Alderan (1052) from Ganymede (1050) on frame 0, enter on 80 |
| Revisit | Return to Ganymede for eight frames, repeat the same approach and crossing |
| Interrupted | Announce 1054 on frame 0, redirect to Tellus (1055) on 1, enter on 2 |

These are controlled sector-view transitions through Game1 Update, Draw and
Present. The harness disables the optional aleph glimpse and automatic proximity
scans to keep the destination set and lead time reproducible. No live server
runs. Arrival frame 0 below is relative to crossing. All frame times describe
software GL. Window CPU comes from the Linux game-thread CPU clock. Allocation
uses calling-thread deltas around the frame. Readback and residency sampling
run outside that interval.

## Threading and lifetime

The renderer resolves the model's actual texture override or imported namespace.
Each preparation queue owns a single asset source. Its key includes the
case-insensitive native image name and the poster colour-key policy. Queue
promotion reuses matching work. A new priority head can retire a speculative
slot. Both active and retiring decodes count toward the two-job limit.

Workers read independent streams, expand RGB565 or inflate an 8-bit RGB/RGBA
PNG, and apply the existing near-black key in place for poster namespaces.
A bare sibling PNG retains its platform alpha handling. The model-authoring API
keeps its existing PNG entry points. The graphics thread alone resolves meshes,
creates surfaces, submits rows, publishes completed textures in the original
renderer cache and disposes them. Workers never take the session gate.
An incomplete surface is private to its upload job. A required draw joins the
matching read and finishes all remaining rows immediately. Unscheduled,
oversize, unsupported and failed preparations use the existing first-use loader.

The input limit is 12 MiB and the RGBA limit is 16 MiB per job. PNG preparation
also reserves up to 12 MiB for compressed image chunks, two scanlines of at
most 8 KiB each and a text namespace of at most 16 KiB. This bounds large
managed payloads below 41 MiB per slot (82 MiB for two), plus stream/decoder
overhead. The shared RGB565 expansion table is 256 KiB. Each unpublished GPU
surface is at most 16 MiB. Completed GPU surfaces remain in the P862 caches.
Warm uploads share 4 MiB per rendered frame, in 512 KiB row-aligned slices
(one full row for unusually wide binary surfaces). The existing 2 ms flying
and 12 ms idle deadlines are checked between submissions. An indivisible device
allocation or GL call can overrun the deadline. First-visible demand can exceed
both budgets.

Session replacement, mission reset and leaving play retire outstanding work.
Reset disposes an unpublished GL surface immediately. A retired worker finishes
its bounded CPU operation, releases the result and holds its reservation until
exit. Shutdown drains workers before destroying renderer caches. Native mesh UVs,
RGB rounding, colour key, filtering, mip levels and draw order are preserved.
No native simulation rule was ported. The browser retains its resident-source
loaders and compiles without the desktop queue.

## Window measurements

The timed runs below used one client process at a time after builds, tests and isolated retries finished. Each row shows three fresh Modern clients in run order. Allocation is decimal MB on the game thread, CPU is milliseconds summed over 120 frames, and p99 and maximum are software-rendered frame milliseconds. p99 is the second slowest frame.

| Window | Build | Game-thread MB | Game-thread CPU ms | Software p99 ms | Software max ms |
| --- | --- | --- | --- | --- | --- |
| cold | base | 288.06 / 288.08 / 288.07 | 753.0 / 767.7 / 778.0 | 76.83 / 79.15 / 78.33 | 89.95 / 88.00 / 88.09 |
| cold | after | 36.96 / 36.95 / 36.95 | 497.1 / 509.9 / 508.1 | 7.20 / 7.84 / 9.13 | 75.76 / 78.11 / 77.74 |
| revisit | base | 25.45 / 25.43 / 25.42 | 387.6 / 378.8 / 385.8 | 6.60 / 5.81 / 5.50 | 9.63 / 6.03 / 5.67 |
| revisit | after | 25.48 / 25.47 / 25.46 | 390.8 / 386.5 / 383.1 | 5.61 / 5.59 / 5.46 | 5.78 / 5.64 / 5.74 |
| interrupted | base | 190.15 / 190.15 / 190.15 | 543.1 / 548.4 / 548.3 | 88.86 / 94.36 / 90.83 | 115.88 / 118.19 / 119.61 |
| interrupted | after | 39.42 / 39.42 / 39.42 | 389.2 / 393.7 / 394.4 | 6.24 / 6.07 / 8.35 | 162.38 / 139.82 / 140.92 |

Peak target residency includes private and published rock/poster GL surfaces, sampled after each frame. RSS is the entire process, including managed heap and software GL. Gate counts and times cover the sector warmer’s direct session-lock acquisitions, including seed and approach scans. The replay has no competing network receiver.

| Window | Build | Peak surfaces MiB (each run) | Peak RSS MiB (three runs) | Gate enters per run | Median total wait / hold ms |
| --- | --- | --- | --- | --- | --- |
| cold | base | 80 / 80 / 80 | 1011 / 1028 / 1035 | 247 | 0.024 / 0.307 |
| cold | after | 80 / 80 / 80 | 1046 / 1075 / 1040 | 247 | 0.036 / 0.330 |
| revisit | base | 80 / 80 / 80 | 1041 / 989 / 976 | 246 | 0.020 / 0.153 |
| revisit | after | 80 / 80 / 80 | 1051 / 997 / 1044 | 246 | 0.021 / 0.184 |
| interrupted | base | 112 / 112 / 112 | 1103 / 1096 / 1081 | 249 | 0.022 / 0.163 |
| interrupted | after | 112 / 112 / 112 | 1070 / 1048 / 1127 | 249 | 0.023 / 0.177 |

## Per-source work

Times are medians of three runs, in milliseconds. Each cell is base → change. Decode includes reading the native namespace and expanding RGB565 to RGBA. Convert is the additional poster colour key. Upload accumulates only device creation and row submission. Worker times are elapsed times and can overlap.

| Texture | Input bytes | Decode ms | Convert ms | Upload ms | Decode allocation bytes | Convert allocation bytes | Upload allocation bytes |
| --- | --- | --- | --- | --- | --- | --- | --- |
| bgrnd03bmp | 8388724 | 61.5 → 21.1 | 0.0 → 0.0 | 4.7 → 5.6 | 58590088 → 25170752 | 0 → 0 | 256 → 256 |
| bgrnd05bmp | 8388724 | 62.6 → 21.8 | 0.0 → 0.0 | 4.7 → 5.9 | 58590088 → 25170752 | 0 → 0 | 256 → 256 |
| bgrnd56bmp | 8388724 | 62.5 → 21.8 | 0.0 → 0.0 | 4.5 → 8.4 | 58590088 → 25170752 | 0 → 0 | 256 → 256 |
| nebplnt06bmp | 8388728 | 60.7 → 22.8 | 18.3 → 18.4 | 5.2 → 5.8 | 58590088 → 25170752 | 16777240 → 8168 | 256 → 256 |
| nebplnt12bmp | 8388728 | 63.2 → 18.4 | 18.4 → 16.4 | 4.6 → 4.4 | 58590088 → 25170752 | 16777240 → 0 | 256 → 256 |
| nebplnt25bmp | 8388728 | 60.8 → 20.8 | 18.5 → 16.0 | 4.6 → 5.7 | 58590088 → 25170752 | 16777240 → 0 | 256 → 256 |
| nebplnt33bmp | 8388728 | 61.0 → 19.4 | 18.0 → 14.8 | 4.2 → 5.1 | 58590088 → 25170752 | 16777240 → 0 | 256 → 344 |

All seven surfaces are 2048 × 2048, with 16,777,216 bytes decoded and uploaded per surface. On a poster the conversion touches another 16,777,216 bytes in place. All decode and conversion allocations in the base occur on the game thread. After the change these columns include worker allocation, with first-use runtime overhead visible on the origin poster. The revisit reads, converts and uploads zero target bytes.

## First visible pixels

The visibility control replaces one source at a time with opaque magenta on its original non-keyed pixels. It compares each of the 360 resulting backbuffers to the same build with untouched artwork. The table gives the first changed frame relative to crossing. “—” means no pixel changed anywhere in that window.

| Source | Cold | Revisit | Interrupted |
| --- | --- | --- | --- |
| bgrnd03bmp | 0 | 0 | — |
| bgrnd05bmp | 0 | 0 | — |
| bgrnd56bmp | — | — | — |
| nebplnt06bmp | — | — | — |
| nebplnt12bmp | — | — | — |
| nebplnt25bmp | — | — | — |
| nebplnt33bmp | — | — | — |

First two rendered arrival frames, SHA-256 prefixes of backbuffer RGBA. All four paired runs match byte digests across versions.

| Skin | Window | Frame 0 | Frame 1 |
| --- | --- | --- | --- |
| Modern | cold | BE2DDB8DD7A30E60 | DAFE7A4BA1C1DA88 |
| Modern | revisit | 55B1A51D987FB56E | E10C474EEBE9041A |
| Modern | interrupted | F22E00A44B3BD243 | FEAFA1ED9E463047 |
| Classic | cold | A88107803D05C14A | 7710CF5C3E2E662A |
| Classic | revisit | 4FF076CBA8CF4FEF | 3E3DF3880EFD0A3A |
| Classic | interrupted | E0D712159ED5E797 | 61353ECAF7B27196 |

The concatenation of the 420 benchmark state hashes has SHA-256
`2D86A0571CBA9D0C03B5121F444FED89E98A7C2D014C1A9D3404D1E8E166A112`
for both builds and both skins. The replay consumes recorded messages. The change adds no command send, input change or simulation step.

All 2,880 paired backbuffers in the untouched and seven single-source Modern
visibility runs also matched across builds. At arrival the binding probe recorded
three rocks and poster 06 in Alderan and poster 12 in Tellus. Only rocks 03 and
05 affected the final replay image. The Fast fixture points the camera at planet
06 and rock 05 and checks that removing either changes the RGBA image.

## Interpretation

In the standalone runs the cold window saved a median 251.12 MB of game-thread
allocation (87%) and 259.6 ms of game-thread CPU (34%). All four destination
textures were ready before entry. The cache-only revisit changed allocation by
0.04 MB in the medians. Its median game-thread CPU was 385.8 ms on the base and
386.5 ms on the change. Sector lock acquisition counts were identical across versions.

The interrupted window saved 150.73 MB of game-thread allocation. Its median
software p99 fell from 90.83 to 6.24 ms, but its median worst frame rose from
118.19 to 140.92 ms. On the base, the two planet loads stall separate approach
frames. With the split, the required Tellus poster joins its unfinished CPU work
on the crossing frame, where other cold rendering also runs. Required waits were
33.51 / 29.30 / 29.84 ms. The abandoned destination's speculative poster later
finishes into its normal cache. Both versions eventually hold seven 16 MiB
surfaces. This case preserves first-frame
pixels at a measurable latency cost.

The initial pre-implementation Modern baseline measured cold p99 of
75.48 / 79.82 / 84.97 ms on software GL. The paired table uses the final fixed
camera clock and matching probes on both builds. In a separate three-run
comparison under local build/test load, median cold allocation was 288.02 → 36.95 MB,
game-thread CPU was 1,024.7 → 640.5 ms and software p99 was 104.97 → 11.96 ms.
The interrupted maximum under that load was 120.85 → 188.08 ms. Mesh creation,
station/ship warming, HD material lookups and sky-dome/glimpse work retain their existing P862 paths.

## Reproduction

Run the harness from the checkout root after building the Debug desktop client on Linux.

```sh
set -a
. ./local.env
set +a
export CLIENTBIN="$PWD/src/Allegiance.ClientCore.MonoGame/bin/Debug/net10.0"
export LD_LIBRARY_PATH="$CLIENTBIN/runtimes/linux-x64/native"
export SDL_VIDEODRIVER=offscreen LIBGL_ALWAYS_SOFTWARE=1 ALSOFT_DRIVERS=null
export ALLEGIANCE_NOVSYNC=1 ALLEGIANCE_BENCHMARK_FRAME=1097000 ALLEGIANCE_ASSET_LOG_MS=0
for skin in modern modern modern classic; do
  xvfb-run -a dotnet fsi --nologo --quiet --lib:"$CLIENTBIN" tools/p1097-warp-slice.fsx "$skin"
done
```

Run the same harness against desktop binaries built from the base to compare
WINDOW, PIXELS and STATE. An optional second argument names a directory of
artwork overrides and captures every frame. Phase allocation and GATE rows used
scratch-only probes in copies of both render assemblies. The base probe brackets
the old read/decode, poster conversion and upload. The change probe brackets
worker decode/convert and individual GL submissions on their owning threads.
The gate probe times Monitor.Enter and the acquired scope separately. The paired
standalone logs are in `.scratch/standalone-{base,after}-{1,2,3,4}.log`.
The loaded comparison uses the same naming pattern with prefix `final-`. The source controls
are in `.scratch/vis{base,after}-*.log`. A separate capture produced 12
byte-for-byte equal arrival image pairs. Lossless PNGs for frames 0 and 1 of
each window and skin are in `.scratch/captures/`.

## Tests and deliberate breaks

The six P1097 Fast cases ran in fresh test hosts three times. The two GL cases
ran with artwork for Classic and Modern. Each renders the origin poster, switches
cluster on the same renderer, then compares two destination draws in the ready,
partially uploaded, never queued, interrupted and shut-down states to a
synchronous reference. Source byte counts catch redundant reads. The PNG control
also compares partially transparent and zero-alpha texels against DesktopGL in
both native namespace forms. The related MonoGame suite (87 cases) and Core
authoring/orientation suite (42 cases) passed three times each. Decoder checks
exercise the allocation threshold and all 65,536 packed RGB565 values.

Each mutation below ran against a disposable build. Each produced a failing
assertion. The working source and qualification binaries used the normal code.

| Deliberate break | Observed failing check |
| --- | --- |
| Admit another job for an identical source | Duplicate read of the case-insensitive rock key |
| Evict the priority head instead of the tail | Promoted head no longer prepared |
| Release a retired slot before its worker exits | Third job admitted while both reads still blocked |
| Return immediately from a required in-flight lookup | Required lookup completed before the held read was released |
| Ignore an expired frame deadline | GL fixture submitted rows with zero time available |
| Double the frame byte allowance | Partial upload exceeded the shared allowance |
| Invert worker row order | First-arrival pixels differed in both skins |
| Remove the decoded-size limit | Over-budget MDL accepted |
| Perturb the packed red-channel lookup | Exhaustive RGB565 equivalence failed |
| Omit disposal of the unpublished GL texture | Partial resource survived the lifetime boundary |
| Apply the namespace colour key to a bare sibling PNG | Prepared RGBA differed from the platform readback |

## Qualification

Debug and Release builds of `AllegianceCore.slnx` completed with zero warnings
and zero errors, including the browser targets. `tools/build-tools.sh` built all
157 tool projects. Its initial offline run lacked three shader pipeline packages.
Seeding the scratch NuGet feed from the local package cache allowed the complete
rerun to pass. The glossary link check validated 1,189 distinct paths.

The qualifying Fast sweep ran 16,207 cases in 764.5 s, with 16,197 passing, four
failing and six skipped. Both quarantine batches and all nine infrastructure
checks passed. A passing P777 test also exceeded the calibrated 13.662 s limit.
The sweep used the final production implementation. The subsequently expanded
departure/arrival pixel fixture passed all six P1097 cases three more times.

| Qualifying sweep failure or overrun | Three isolated reruns and baseline evidence |
| --- | --- |
| P1091, 1920 default Classic and Modern | Consistent local hash mismatch in both skins. The base renderer gave the same actual pixels. Details below. |
| LiveCombatWire moving target | Neither hull drop nor kill observed in the sweep. Passed 3/3 in isolation, matching the ledger. |
| AsteroidMined no broadcast at capacity | Extra mining receipt. Pass / pass / fail in isolation, with the same extra receipt on the third run. This signature is already ledgered. |
| P777 NPC warp exit touch rate | Passed in 13.964 s in the sweep, then passed 3/3 in isolation at about 8 s each. The timing family is ledgered. |

The machine-readable result is `.scratch/qualification/fast/summary.json`.

An earlier Fast sweep ran 16,204 cases during development. Its two HUD
golden failures also appeared in the subsequent run. Three additional Core
failures and one timing overrun matched the existing flake ledger. Each of the
following ran in its own test host three times after that sweep.

| Case | Sweep symptom | Isolated result |
| --- | --- | --- |
| P590 proactive pod rescue | Diverter missed the rescue window | 3/3 pass, 0.96–1.00 s |
| MiningStateWire own mining/departure | Expected mining update missing | 3/3 pass, 0.67–0.68 s |
| LiveCombatWire moving target | Neither hull drop nor kill observed | 3/3 pass, about 1 s each |
| P814b TurretCarrier | Passed in 13.453 s, over a 12.629 s calibrated limit | 3/3 pass, about 4 s each |
| P1091 painted mount rows, 1920 default Classic and Modern | Both pinned hashes differed | Each failed three times. The unchanged base produced the identical local hashes three times, with zero layout errors. ClassicArt passed. |

The local HUD golden discrepancy is recorded in [the flake ledger](../TEST_FLAKES.md).

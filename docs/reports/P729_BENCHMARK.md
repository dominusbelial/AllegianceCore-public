# P729 — local skirmish and deterministic replay benchmarks

Working-tree implementation based on `a8a9eb24`. No commit, push, deployment, rig,
221, or 200v200 run was performed. Native training/drone AI remains a separate track.

## Player flow and workloads

The desktop client accepts `--benchmark skirmish:small|medium|large|epic` and
`--benchmark replay[:file]`. The main menu has a Benchmark entry. First launch
opens a skippable benchmark offer; accepting or skipping records that choice.
Skirmish uses P727's owned loopback server and worker processes, fixed map
`m2xspiderw.igc`, civilizations 25/3, research mode, server seed 729, and the
selected 5v5 / 15v15 / 30v30 / 60v60 fleet. Each selected workload runs three times.
The measured skirmish route lasts 150 seconds by default, following warmup until
both teams have visible ships in the battle sector. The camera follows chase,
sustained battle in Alderan, then a home-station flyby. Startup and warmup are
excluded from client FPS and server tick samples.

The local host now reserves enough per-IP transport connections for combat
pilots, the human, commanders and worker probes. The dedicated default of 64
blocked Large/Epic despite their larger mission seat limits. Small/Medium retain
64; Large uses 92 and Epic 152. Dedicated-server defaults are unchanged. The
four-size configuration theory passed three fresh invocations after this fix.

The benchmark observer uses the existing privileged local spectator stream,
scoped to the explicit `p729-benchmark` identity on the loopback host. Its decoder
uses the selected telemetry sector and accepts the observer's authorized ship
poses. Ordinary scanner visibility remains unchanged. Background bots, economy,
research, mining, combat and normal mission rotation continue through the real
host. Server RNG seeding and benchmark orchestration are port policies; the
skirmish mode is not advertised as deterministic simulation.

## Replay and bundles

The incoming transport recording hook is `ConnectionSession.Recording`.
`WireRecording` stores original complete server-to-client deliveries, timestamps,
CRC/framing and camera-sector/phase markers in a versioned gzip file. The reader
rejects unsupported versions, decreasing timestamps, truncated payloads,
invalid markers, oversized packets, more than one million records, more than
ten minutes, or more than 512 MiB of decompressed payload.

The bundled `data/benchmarks/battle.alr` is a real **180-second** capture from this
workstation, using the host above. Its driver observed **549 shots / 419 hits**,
nine combat bots and a running battle. An offline inspection finds **454
projectile entries** and visible enemy battle poses. SHA-256:
`c16676c5a064b122df57b6e40ebc1e216f00dd924d5a376ef483a3d2b4f36972`.

Replay constructs no server, bot driver or socket connection. It feeds the same
client decoder at 60 logical frames/second, with a logical arrival/effect clock,
recorded camera markers, raw authoritative ship poses, seeded/reset flight
particles, reset asteroid spin, and a replay clock for HD residency. Volumetric
and displaced explosion age also use that clock. Each baseline traverses the
same **10,809 frame states**. The per-frame digest covers decoded ship poses and
orientation, visibility, identity, simulation scale, camera phase/sector, and a
rolling digest of every incoming wire byte. Every baseline digest must match
before a successful result is written. A subsequent live reconnect clears the
replay clock and observer sector; a real loopback regression passed three fresh
invocations after the lifecycle correction. These are frame-state hashes, not a claim
of identical framebuffer pixels across different GPU drivers.

The desktop project copies the battle to `benchmarks/battle.alr` in build and
publish outputs; packaging checks that it exists, and the bundle inventory
includes it. Bundle smoke runs short headless Skirmish and Replay modes. CI also
runs the bundled replay without server/artwork and runs comparison-tool tests.
`tools/benchmark` provides the same headless runner plus capture/inspection tools.

## Results and settings

JSON records all three runs, scalar FPS/timing medians, CPU/GPU split, frame
p50/p95/p99/worst, tick mean/p95/worst excluding cadence sleep, exact per-bot
decision-thread CPU, worker/supervisor OS CPU and RSS, amortized process CPU per
bot, managed allocation/heap, GC generation counts and pause time. The resource
and process-ID snapshot comes from the middle run; its repetition is retained.
Missing graphics measurements stay null in headless mode. Present wait is not
reported as GPU time. New result/config JSON uses source-generated serialization.

The result screen calls out the measured bottleneck and gives reasons for battle
size, bot difficulty, research speed, spectator default, FSR/resolution scale,
bloom, HD PBR, texture quality, frame cap/vsync, effects density and view distance.
It tries the proposed settings in an actual battle for 12 seconds (wall time in
skirmish, logical time in replay), checks 60 FPS
1% low and tick p95 < 50 ms when a server is present, then reduces settings once
and checks again if needed. Verification runs uncapped to measure headroom;
the proposed saved frame cap is 60. Failure to reach the target remains explicit.
The reasons and displayed values follow the settings actually proposed after a
step down. Replay verification seeks to an actual visible battle and renders two logical
seconds of warmup before measuring. Its
skirmish-size/difficulty defaults are explicitly provisional because replay
measures no live bots. View-distance changes preserve camera FOV, aspect and
projection type; the strategic command camera retains its map projection.

Click **Apply** or press Enter to save the settings; Escape keeps previous
settings. Skirmish defaults are consumed by the setup model, graphics settings
by the render paths, and spectator default by the existing director. Preferences
live in the player's `AllegianceCore/benchmark.json`; FSR/HD texture preferences
also use the existing graphics JSON. The benchmark itself restores the prior
settings until Apply is selected.

Useful commands:

```sh
Allegiance --benchmark skirmish:small --benchmark-output small.json
Allegiance --benchmark replay --benchmark-output replay.json
Allegiance --benchmark replay:/path/battle.alr --benchmark-exit
Allegiance --benchmark skirmish:epic --benchmark-headless --benchmark-seconds 3
Allegiance --benchmark skirmish:small --benchmark-record /tmp/battle.alr
python3 packaging/bench-compare.py A.json B.json
python3 packaging/bench-compare.py A.json B.json --fail-percent 5
```

Comparison rejects mismatched modes, presets, seed/map/civs, duration,
recording checksum, resolution, tested settings, or headless/rendered status
unless `--allow-mismatch` is explicit. FPS and time regressions have opposite
signs; missing GPU values remain unavailable. Threshold violations exit 1.

## Workstation evidence

This is an Intel Core Ultra 9 285HX workstation. The sandbox's GL context is
**Mesa llvmpipe (LLVM 21.1.8, 256 bits)**, with GL 4.5 / Mesa 26.0.8. Hardware DRM
initialization failed and the context used software rendering. Thus the rendered
measurements characterize this context under shared workstation load; they are
not physical-GPU results. The optional HD texture/PBR pack is absent here.
Claude's UM690 and M4 Max runs remain separate, as requested.

[Renderer query](P729_assets/renderer.json) ·
[First-launch offer](P729_assets/intro_proof.png)

| Mode | Runs × workload duration | FPS avg / 1% low | Frame p50 / p99 ms | CPU / GL scene+post ms | Tick mean / p95 / worst ms |
| --- | --- | --- | --- | --- | --- |
| Small skirmish | 3 × 150s | 134.21 / 35.64 | 7.40 / 20.07 | 4.78 / 4.90 | 2.11 / 4.16 / 22.71 |
| Recorded battle replay | 3 × 180s logical | 191.45 / 76.90 | 4.72 / 9.27 | 2.94 / 3.28 | — / — / — |

Scalar values above are recomputed from the three retained raw runs. The full
Small capture predates the final scalar-summary formatting change. Its compact
JSON summary was re-aggregated from the retained original runs using the final
median rule; the original application output is preserved in the raw evidence
directory and linked by `EvidenceProcessing`. The measured runs and actually
tested auto-tune profile remain unchanged. Current runs write scalar medians directly. The full Small baseline
has frame-time spikes and a 1% low below 60; its short tuning verification passed.
The final replay baseline and verification both passed 60 FPS at the 1% low
(76.90 and 110.85 respectively). Replay renders uncapped at fixed logical steps:
the 180-second clip took about 56 seconds per traversal, not 180 wall seconds.
The final-source short rendered Small check also passed three runs and its tuning
verification (79.34 baseline / 114.94 verification 1% low).

| Headless preset | Combat bots | Workers | Tick mean / p95 / worst ms (median) | Per-bot CPU entries |
| --- | ---: | ---: | --- | ---: |
| Small | 9 | 4 | 1.46 / 1.75 / 5.23 | 9 |
| Medium | 29 | 4 | 3.73 / 5.22 / 6.43 | 29 |
| Large | 59 | 4 | 7.53 / 9.11 / 10.54 | 59 |
| Epic | 119 | 4 | 19.51 / 23.12 / 30.71 | 119 |

Every headless preset completed **three 3-second measured runs after launch**.
These short smoke measurements do not establish sustained late-battle capacity;
graphics FPS/CPU/GPU values are intentionally absent. Replay headless completed
three 10,809-frame traversals with matching hashes.

[Scalar workstation summary](P729_assets/workstation-summary.json) ·
[Full Small runs and scalar summary](P729_assets/skirmish-rendered.json) ·
[Replay raw runs and verification](P729_assets/replay-rendered.json) ·
[Replay results screen](P729_assets/replay-results.png) ·
[Headless replay](P729_assets/replay-headless.json) ·
[Final skirmish screen](P729_assets/skirmish-final-screen.png) ·
[Final short rendered skirmish check](P729_assets/skirmish-final-smoke.json)

## Validation

The whole-solution build completed with **0 warnings / 0 errors**. The required
Linux-x64 self-contained server AOT publish completed without IL2026 or IL3050.
The Linux self-contained player and bot-driver publish includes the recording;
both its replay and three-run local skirmish smoke passed from `/tmp`.

**73 affected/new methods × 3 fresh invocations = 219 final focused invocations,
354 passed cases, no failures.** Packaging's 11 tests passed, including three
fresh invocations per test (33/33). Comparison self-check exits 0 with zero
percent deltas; direction, unavailable metrics and workload mismatch are tested.

The replay-to-live clock correction landed after the single ClientCore sweep;
its new regression then passed 3/3 fresh invocations, followed by the 0/0 solution
build and refreshed player publish. No full sweep was repeated.

The never-parks test passed **5/5**, with maximum stopped-with-target intervals
2.017 / 2.015 / 2.024 / 1.909 / 1.907 seconds. The preceding
[P727 base report](P727_SINGLE_PLAYER_HOST.md) also records 5/5. No P729 failure
required a new base-rate rerun.

The **single Core sweep** completed **4,875 pass / 4 fail / 0 skip**. All four
failed methods passed **3/3 alone** and are recorded as light-gate load flakes in
[TEST_FLAKES.md](../TEST_FLAKES.md). Assertions, deadlines and quarantines were
not changed. No full sweep is repeated.

The **single ClientCore sweep** completed **1,388 pass / 0 fail / 1 skip**. The
skip is the existing opt-in P660 showcase fixture. It was then explicitly enabled
with `ALLEGIANCE_LIVE_P660_SHOWCASE=1` and passed **3/3 fresh isolated runs, no
skips** (real 15v15 MCP showcase and human rotation). The **single MonoGame sweep**
completed **2,436 pass / 0 fail / 3 skips**; those three tests require the absent
private Meshy pack. All available live/loopback cases were exercised.

[Compact validation ledger](P729_assets/validation-summary.json) contains the
method triplets, cases and isolated follow-up outcomes. Raw commands/logs/TRX
remain in the external directory below.

The early recording fixture failed the strengthened enemy-pose/projectile test:
its battle camera ended before the fleets reached sustained combat. The final
fixture holds the battle view longer and passes the stronger test. These were
implementation failures that were fixed, not load flakes. The earlier Large
startup failure was the per-IP cap described above and was also fixed.

Raw logs, TRX files, temporary recordings and diagnostic results
are under [the raw evidence directory](/tmp/allegiance-evidence/P729/).
Publish outputs are `/tmp/p729-server-aot/` and `/tmp/p729-player/`.
The brief's requested `~/allegiance-evidence/P729/` is outside this sandbox's
writable roots, so `/tmp/allegiance-evidence/P729/` is used instead. Only compact
JSON and screenshots are left under `docs/reports/P729_assets/`, about 631 KB (decimal), below 2 MB.

## Native basis

Benchmark CLI/UI, recording format, fixed logical frame stepping, seeding,
recommendations, auto-tune, local observer and resource policies are port
orchestration additions. The existing gameplay paths are reused. Native
citations are relative to `~/Development/Allegiance/src/`:

| Reused rule | Native source |
| --- | --- |
| Existing Single Player intro entry remains available | `WinTrek/introscreen.cpp:1221`, `WinTrek/introscreen.cpp:1558` |
| Started stage releases the client into the mission | `clintlib/appmsg.cpp:2330-2346` |
| Native sector preview is docked-only; the authorized flying observer is the existing port extension | `FedSrv/FedSrv.CPP:6420-6454` |
| Existing privileged identity distinction | `FedSrv/FedSrv.CPP:664` |
| Uniform random direction distribution retained when adding a local seed | `zlib/vector.cpp:37-49` |
| Ordinary economy treasury/payday flow | `FedSrv/fsmission.cpp:2294-2324` |
| Normal bot steering/fire gates remain the reused wingman policy | `Igc/shipIGC.cpp:2470-2600` |
| Mission restart returns to the normal start countdown | `FedSrv/fsmission.cpp:3539-3546` |
| Keepalive continues throughout local play | `clintlib/clintlib.cpp:2050-2087` |

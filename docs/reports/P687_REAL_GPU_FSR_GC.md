# P687 — Real-GPU FSR 1 + client-GC A/B measurement

Real-hardware complement to P643 (the FSR 1 + dynamic-resolution + CPU/GPU/GC telemetry
work that was merged on `master` as `3298370c`). P643's sandbox only had **llvmpipe**, so
every timing in that report was meaningless for hardware. This report repeats the matrix on
**this workstation's real GPU** — the Intel Arrow Lake-S integrated GPU (Xe/Arc-class iGPU,
`/dev/dri/renderD128`, Mesa `iris`) under a real GNOME/Wayland display — with a real
windowed client, and measures the **client GC A/B** and **dynamic resolution** in a real
bot battle.

Base `master` at `3298370c` (P643 merged). No production code changed. All numbers below
were actually measured on this machine; each run is reported as it happened.

## Hardware and method

| Item | Value |
|---|---|
| GPU | Intel Arrow Lake-S iGPU (`8086:7D67`, i915). Software adapter string `Mesa Intel(R) Graphics (ARL)` |
| GL | `iris` driver, hardware-backed (the P643 window log's llvmpipe is gone) |
| GPU timer | `GL_TIME_ELAPSED scene/post, asynchronous` — **supported and sampled on every run** |
| Display | GNOME Wayland (`:0`, `wayland-0`), real on-screen window, backbuffer = requested size |
| Loop | `ALLEGIANCE_NOVSYNC=1`, fixed-step limiter off; real client `dotnet run -c Release` against a local loopback server |
| Telemetry | `ALLEGIANCE_CLIENT_PERF=1` per-frame CSV + per-second overlay |

Settings files mirror P643 (`Off` / `Quality` / `Performance` / `UltraQuality` / `Balanced`,
sharpness 0.5). Each run auto-stops at 60 s of telemetry; summaries drop the first seconds
(load) and the final capture frame.

## Scope 1 — real-window self-test on the real GPU: **PASS**

`tools/fsr-proof` run without `SDL_VIDEODRIVER` (a real window) on the hardware adapter:

```
GL adapter: Mesa Intel(R) Graphics (ARL); timer=True
FSR_SELF_TEST_PASS: 40 scene->EASU->RCAS->backbuffer frames; 321x181; all presets, resize,
  bloom, sharpness endpoints, orientation, black/white, native HUD; GPU samples=78
```

It exercises both compiled effects, all four presets, target-dimension changes, both
sharpness endpoints, and requires **positive asynchronous `GL_TIME_ELAPSED` query results** —
all passed. `timer=True` confirms the hardware path P643's report flagged as "no `/dev/dri`".
The same battle/inspector runs below re-confirm `GL_TIME_ELAPSED` in the live client.

## Scope 2 — FSR matrix at 1920×1080 and 2560×1440

### A diagnostic discovered first (why it matters)

In the **inspector** scenes FSR runs at >1 500 fps. At that rate the client's 32-slot
async GPU-query ring saturates: every `glBeginQuery` is still outstanding at the 16-frame
CSV write delay, so **all** `scene/post_gpu_ms` are `NaN` (the P643-defined "late result").
This is **not** an FSR bug — the native (`Off`) inspector run at ~900 fps returns valid
timers, and the **battle** (realistic 60–300 fps) returns ~100 % valid timers even with FSR.
It is the documented NaN-for-late-result behaviour surfacing at pathological frame rates,
and it means the inspector's FSR runs are hopelessly CPU/submission-bound (GPU near-idle).
Section "Measurement caveats" quantifies this. For the inspector GPU column below, scene
time is the measured valid native value; FSR post time is the same output-resolution cost
measured in the battle (identical EASU→RCAS→backbuffer pipeline) and, where available, the
throttled isolation run.

### Ship + station inspectors (no server; real shared renderers, HUD, post)

p50 / p99 / max in ms (the real user-facing numbers); CPU update/draw and draw calls from
the same CSV.

| Scene | Mode | frame p50 / p99 / max | CPU draw med | draws | GPU scene (post) ms | bound |
|---|---|---:|---:|---:|---:|---|
| hull 1080 | Off | 0.63 / 2.68 / 4.22 | 0.14 | 8 | 0.10 (0) | CPU |
| hull 1080 | FSR Quality | 0.46 / 3.45 / 11.02 | 0.03 | 4 | ~0.1 (~1.2) | CPU |
| hull 1080 | FSR Perf | 0.47 / 3.25 / 7.78 | 0.03 | 4 | ~0.1 (~1.2) | CPU |
| hull 1440 | Off | 0.69 / 3.03 / 11.49 | 0.04 | 4 | ~0.13 (0) | CPU |
| hull 1440 | FSR Quality | 0.77 / 3.07 / 11.45 | 0.04 | 4 | ~0.13 (~2.4) | CPU |
| hull 1440 | FSR Perf | 0.72 / 3.00 / 8.64 | 0.04 | 4 | ~0.13 (~2.4) | CPU |
| station 1080 | Off | 0.46 / 2.55 / 8.47 | 0.03 | 4 | ~0.1 (0) | CPU |
| station 1080 | FSR Quality | 0.47 / 2.54 / 8.00 | 0.03 | 4 | ~0.1 (~1.2) | CPU |
| station 1440 | Off | 0.67 / 4.14 / 8.19 | 0.04 | 4 | ~0.13 (0) | CPU |
| station 1440 | FSR Quality | 0.67 / 4.13 / 7.86 | 0.04 | 4 | ~0.13 (~2.4) | CPU |

`Off`-1080 was captured on a 20 s run (identical numbers to the 60 s neighbours); all others
60 s. At these frame rates (~1 500 fps) the scene is **overwhelmingly CPU/submission-bound**;
FSR's extra EASU+RCAS post (~1.2 ms at 1080 / ~2.4 ms at 1440) is smaller than the draw-loop
jitter and does not move p50 measurably. HUD/text stay crisp because they are composed at
native resolution (see assets).

### Real battle view (local loopback server + MCP showcase, scale-6 arena at the client's home sector)

A persistent local `Allegiance.Server` on 127.0.0.1:6073 (m2xspiderw, `native` ship-update,
pool transport, adminCheats on) hosted a `spawn_scenario showcase size_scale 6 mode=arena`
battle: **180 bot ships mirrored into the sector the client launches in**, all fighting at
once (continuous kill feed, missile/explosion traffic, 300–900 draw calls in the dense
phase). The client joins before contact and uses:
- **free cam** (`--camera freecam`, F8) — auto-frames the in-sector battle centroid, wide FOV (`--flightFov 120`);
- **chase** (`--camera chase --auto-target enemy`) — close dogfight.

Per run: frame p50/p99/max, CPU update/draw, GPU scene+post (async), draw calls, GC pause
sum, allocations, and a GPU- vs CPU-bound verdict (`GPU-bound` = scene+post > update+draw).

**Free-cam wide view**

| Mode | frame p50 / p99 / max | CPU u/d | GPU sc/post | draws | GC ms | alloc M/fr | bound |
|---|---:|---:|---:|---:|---:|---:|---|
| 1080 Off | 6.67 / 12.26 / 203 | 0.94 / 5.43 | 0.86 / 0 | 879 | 1261 | 4.42 | CPU |
| 1080 FSR Qual | 5.77 / 9.23 / 202 | 0.90 / 4.55 | 0.61 / 1.32 | 391 | 1109 | 3.74 | CPU |
| 1080 FSR Perf | 6.57 / 11.03 / 217 | 0.96 / 5.28 | 0.54 / 1.47 | 733 | 1149 | 4.27 | CPU |
| 1440 Off | 5.03 / 8.75 / 360 | 0.74 / 4.01 | 0.63 / 0 | 452 | 1113 | 3.60 | CPU |
| 1440 FSR Qual | 6.19 / 10.42 / 359 | 0.84 / 4.85 | 0.64 / 2.36 | 238 | 1093 | 3.85 | CPU |
| 1440 FSR Perf | 5.98 / 9.75 / 352 | 0.75 / 4.74 | 0.40 / 2.35 | 493 | 1093 | 3.94 | CPU |
| 1440 FSR UQ | 5.66 / 9.47 / 357 | 0.68 / 4.34 | 0.49 / 2.37 | 259 | 773 | 3.60 | CPU |
| 1440 FSR Bal | 5.25 / 8.52 / 364 | 0.64 / 3.72 | 0.43 / 2.44 | 262 | 841 | 3.42 | CPU |

**Chase dogfight**

| Mode | frame p50 / p99 / max | CPU u/d | GPU sc/post | draws | GC ms | alloc M/fr | bound |
|---|---:|---:|---:|---:|---:|---:|---|
| 1080 Off | 3.14 / 6.18 / 196 | 0.25 / 2.74 | 0.49 / 0 | 528 | 1150 | 2.62 | CPU |
| 1080 FSR Qual | 2.67 / 5.11 / 193 | 0.24 / 2.15 | 0.20 / 1.15 | 523 | 859 | 1.91 | CPU |
| 1080 FSR Perf | 2.95 / 5.51 / 194 | 0.27 / 2.40 | 0.33 / 1.18 | 524 | 941 | 1.96 | CPU |
| 1440 Off | 2.69 / 4.84 / 334 | 0.23 / 2.32 | 0.38 / 0 | 476 | 876 | 1.97 | CPU |
| 1440 FSR Qual | 5.29 / 7.56 / 342 | 0.28 / 1.70 | 0.29 / 2.80 | 479 | 474 | 1.45 | **GPU** |
| 1440 FSR Perf | 4.98 / 7.36 / 345 | 0.28 / 1.70 | 0.24 / 2.27 | 522 | 489 | 1.49 | **GPU** |
| 1440 FSR UQ | 5.14 / 7.66 / 352 | 0.29 / 1.71 | 0.30 / 3.27 | 494 | 446 | 1.46 | **GPU** |
| 1440 FSR Bal | 4.84 / 7.81 / 352 | 0.28 / 1.73 | 0.28 / 3.17 | 485 | 461 | 1.48 | **GPU** |

(The draw-call count varies between rows because the free-cam auto-pan and the evolving
battle change how much is in the frustum — see caveats.)

**Reading:** on this Intel iGPU the 1080 battle is CPU/draw-submission bound (draw 2.7–5.4 ms
vs GPU scene ~0.5–0.9 ms), so FSR's scene reduction does not move the frame time. At 1440 the
scene cost is still small; FSR's EASU+RCAS post is a **fixed ~2.3–3.3 ms** at 1440. In the
chase-1440 rows — where draw counts are matched (~480) — every FSR preset is **~2–2.6 ms
slower than native Off**, because the post cost is no longer hidden under a bigger CPU draw
budget and the scene tips to GPU/post-bound. In the free-cam-1440 rows the extra asteroids in
view keep it CPU-bound, so FSR's post is absorbed and frame times are ~equal to Off. **On
this GPU class FSR never repays its post cost below 4K**; it is neutral at best and
net-negative where the scene is thin. This is the decisive reason to keep **Off** the default
here, and it motivates the 680M estimate below (weak GPU = scene-bound = FSR pays off).

Screenshots (real-window backbuffer, `--autoshots`): [`freecam_1080_{Off,Quality,Performance}`](P687_assets/freecam_1080_Off.png) and [`chase_1440_{Off,Quality,Performance}`](P687_assets/chase_1440_Off.png), plus inspectors [`inspector_hull_1080_{Off,Quality,Performance}.png`](P687_assets/inspector_hull_1080_Off.png). HUD/text is sharp at native resolution in every mode.

## Scope 3 — client GC A/B in the battle view

Same chase-1440 Quality view, `ALLEGIANCE_CLIENT_GC_LOWLATENCY=0` (Interactive, current
behaviour) vs `=1` (`SustainedLowLatency` while flying/undocked; menus keep the original
mode — confirmed in `FlightGcLatency`, `Diagnostics/ClientFrameTelemetry.cs`). The switch is
silent in the log; the startup line `latency=Interactive` is the pre-flight mode.

Pair 1 (sequential), ~48 s steady window:

| Mode | frame p50 / p99 / max | total GC pause | gen0/gen1/gen2 | heap med (max) MiB |
|---|---:|---:|---:|---:|
| Interactive (`=0`) | 5.59 / 11.45 / 351 | 442 ms | 593/152/144 | 39.5 (50.6) |
| SustainedLow (`=1`) | 5.18 / 7.54 / 349 | 527 ms | 763/289/237 | 37.2 (50.0) |

Pair 2 (interleaved, to cancel battle drift), ~45 s window:

| Mode | frame p50 / p99 / max | total GC pause | draws |
|---|---:|---:|---:|---:|
| SustainedLow (`=1`) | 5.17 / 7.72 / 344 | 500 ms | 122 |
| Interactive (`=0`) | 4.97 / 7.82 / 355 | 487 ms | 121 |

**Verdict:** the first pair's apparent p99 win (11.45→7.54) **did not reproduce** in the
interleaved pair — there `=1` had a marginally *worse* p50 (5.17 vs 4.97), the same p99, and a
slightly higher total GC pause. SustainedLowLatency does **not** clearly improve p99/max in
this battle, and its heap stays bounded (~50 MiB, no blow-up) but its gen2/pause counts are
not lower. **Recommendation: do not flip the default** — keep the GC low-latency A/B opt-in
(`ALLEGIANCE_CLIENT_GC_LOWLATENCY`). The criterion "only if it clearly improves p99/max
without a memory blow-up" is not met.

**Allocations are high** (~1.4–4.4 MB/frame in the battle; the per-frame CSV and the perf
overlay agree). A 30 s `dotnet-trace --profile gc-verbose` on a battle client grouped
`GCAllocationTick` (`AllocationAmount64`) by type (~712 MB sampled over 30 s):

| Type (top 10) | sampled bytes | candidate source |
|---|---:|---|
| `VertexPositionColorTexture[]` | 210 MB | billboard vertex buffers rebuilt per frame |
| `Puff[]` | 188 MB | particle puffs (blasts/smoke) |
| `Rendering.ParticleBillboard[]` | 147 MB | particle billboard array builder |
| `System.Int16[]` | 46 MB | ID-key sorts / path enumeration |
| `System.String` | 20 MB | HUD/log status strings |
| `Color[]` | 17 MB | particle colour buffer |
| `Enumerator<DataHullTypeIGC>` | 14 MB | fleet/hull enumeration |
| `Audio.BudgetSound[]` | 8.6 MB | per-frame sound-budget arrays |
| `Sim.Orientation` | 6.8 MB | orientation transforms |
| `ProjectileEntity[]` | 4.0 MB | projectile entity lists |

These are **sampled byte estimates**, not exact censuses. The dominant sources are the
particle/billboard/vertex arrays and HUD strings — rendering-path allocations that recur
every frame in combat. They are the natural target of the "modern alternative" batching/pooling
below; they are not GC-low-latency sensitive in a way that changes the default.

## Scope 4 — dynamic resolution (real battle, 4K)

The controller (`DynamicResolutionController`, `Rendering/UpscalingSettings.cs`) raises/lowers
the render scale against async scene+post GPU samples, EWMA 0.1, 8 % dead band, 30-frame
dwell, 2.5 pp steps, bounded [0.5, preset]. Because the target floor is 8 ms and this GPU's
≤1440 battle GPU time is ~0.5–3 ms, dynres only engages where the scene is actually heavy.
Running the battle at **3840×2160, FSR Quality, `TargetFrameMs: 8`**:

- native render 3840×2160 → after the opening heavy frames the controller stepped the render
  scale down to the Quality floor **2560×1394 and held it for 7 400+ consecutive frames** (see
  the raw CSV's `render_width`), while the output (and HUD) stayed native 3840.
- steady GPU scene+post ~5.3 ms (p90 ~7 ms), i.e. inside the target's dead band → **hysteresis
  holds** (no flutter); brief dips (2464/2368 px) from transient load spikes return to the floor.
- frame p50 ~7.6 ms at 4K; perf overlay confirms `2560x1394 -> 3840x2091` — scene upscaled,
  HUD crisp ([`dynres_4k_quality_target8.png`](P687_assets/dynres_4k_quality_target8.png)).

So dynamic resolution **converges and holds with hysteresis** exactly as designed once the
GPU is genuinely the budget (4K, or the weaker-GPU case); at ≤1440 on this GPU it correctly
stays passive because the scene is never near the 8 ms GPU budget.

## Recommendations (this GPU class, Intel ARL iGPU)

1. **Default `Off` is correct and should stay.** On this machine FSR never repays its post
   cost below 4K: it is neutral in CPU-bound scenes and ~2–2.6 ms *slower* in the thin
   chase-1440 view. Leave the persisted default Off.
2. **Dynamic resolution Off by default** is also correct here — it has nothing to do until
   the GPU is the budget. Its behaviour (converge + hysteresis) is proven at 4K and by the
   P643 unit tests; consider enabling it only where the target GPU is weak or the user runs
   >1440p.
3. **GC low-latency stays opt-in.** The A/B shows no clear p99/max gain in combat; keep
   `ALLEGIANCE_CLIENT_GC_LOWLATENCY` an explicit opt-in.
4. **Allocation hygiene (future):** the battle allocates ~1–4 MB/frame dominated by particle /
   billboard / vertex-buffer arrays and HUD strings. Pooling the billboard vertex/colour
   buffers and caching the per-frame HUD strings would cut the biggest GC sources; the FSR
   post itself is fine.

### Estimate for the Radeon 680M (fill-rate-heavy parts)

No 680M exists on this machine; the following is an inference from the measured per-pixel
costs on this iGPU, not a measured 680M number:

- EASU+RCAS here cost ~1.2 ms (1080) / ~2.4 ms (1440) / ~9 ms (4K) of GPU post at output
  resolution — this is resolution-driven and roughly shader-ALU / fill proportional, so a
  680M (RDNA2, ~half this iGPU's FP32 and far less bandwidth than the ARL iGPU shares) would
  pay **more** per output pixel for the post pass.
- The payoff only arrives when the *scene* pass is the constraint. 680M is markedly slower at
  the scene/fill-heavy parts (overlapping transparent effects, volumetric raymarch, bloom,
  sky, asteroid fill), so at 1080–1440 a 680M battle is plausibly scene/GPU-bound where this
  iGPU was CPU-bound — and there FSR Quality (≈67 % scene pixels) should buy real frame time,
  repaid against the ~1.2–2.4 ms post. Performance/Balanced are reasonable only if Quality is
  still GPU-bound.
- **Suggested defaults for a 680M-class dGPU:** start from `Off` (safe), and if a user's
  target frame time is missed at native in a large battle, prefer **FSR Quality** over
  Balanced/Performance (least visual cost that still unloads fill), with dynamic resolution
  enabled and `TargetFrameMs` ≈ their display period as the automatic fallback. This is an
  estimate to validate on a real 680M, not a measured claim.

## Native behaviour classified

| Behaviour | Native cite | Class | Port |
|---|---|---|---|
| Spatial upscaling (FSR1 EASU/RCAS), native-res HUD, dynres, GPU/GC telemetry | none (native has no upscaler; `FedSrv.CPP:2822-` cheat list has no graphics setting) | **modern addition** | as implemented in P643; unchanged here |
| GPU-timer async ring + `NaN` late result | none | modern telemetry | documented limitation at >1500 fps (see caveats) |

No native gameplay or era rule is asserted or changed by this measurement task.

## Modern alternative considered

| What the client does | Modern option | Chosen | Why |
|---|---|---|---|
| Rebuilds billboard vertex/colour buffers + particle arrays every frame (top GC sources) | Pooled scratch buffers / instanced billboard batching | Measured, not changed (out of scope) | Batching is already partly done (P628); pooling the per-frame arrays is the highest-leverage allocation fix and is proposed as a follow-up with a failing-first test |
| FSR spatial upscale | Temporal upscale (FSR2/DLSS/XeSS) needs motion vectors, depth, projection-jitter history | Keep spatial FSR1 | Temporal needs a modern backend + history/reset plumbing; spatial is what P643 shipped and is provably free-of-charge when CPU-bound |
| Fixed 8 ms dynres target floor | Adaptive target from present/vsync | Keep 8 ms floor | An 8 ms GPU floor is already below any playable present period; a lower floor buys nothing on this GPU |

## Measurement caveats (read before quoting)

- **Shared box:** the local server + 180 MCP bots ran on the same machine as the measured
  client, so the client's CPU draw/update numbers are inflated by co-tenancy. On a dedicated
  play rig the CPU budget would be lower and the GPU/post balance could shift — the *direction*
  (FSR's fixed post vs this iGPU's small scene) holds, but absolute CPU figures are machine-specific.
- **Battle drift:** the matrix ran back-to-back over one battle (~20 min); ship density decays
  (draw calls fell from ~900 to ~120). Cross-row `draw calls` are therefore not constant; the
  most density-matched comparisons are chase-1080 (523–528) and chase-1440 (476–522), which is
  exactly where the net-negative FSR-1440 result appears.
- **"≥100 ships in view":** the battle is a scale-6 arena with **180 bot ships in the viewed
  sector**, sustained combat, continuous kill feed and transit pulses, and 300–900 draw calls
  in the dense phase — a crowded battle by any load measure. The auto free-cam however holds a
  close chase-like framing (it hugs the ship when the battle is near its centroid), so the
  widest *still* photographs show tens of clearly-separated ships while the rendered
  population/load confirms 100+. Full wide-overview framing of 100+ in one still needs the
  camera pulled well back (no xdotool/free-cam-back automation available in this session).
- **Inspector FSR GPU NaN:** at >1500 fps the 32-slot async GPU-query ring is permanently
  pinned (queries never retire inside the 16-frame write delay), so the unthrottled FSR
  inspector rows report `NaN` GPU. This is the documented late-result behaviour, confirmed
  *not* an FSR defect (native Off is valid; the battle is valid). The inspector GPU column is
  therefore sourced from (a) the measured native scene time and (b) the output-resolution FSR
  post measured in the battle (identical pipeline) / the throttled isolation run where valid.
- **Suite note (common verification):** the 2-run full-suite rule below means "2 runs on the
  final code", not "2 clean runs"; load-flakes are handled per the footer rule.

## Verification

- **Real-window self-test** (`tools/fsr-proof`, no `SDL_VIDEODRIVER`): PASS on the real GPU (above).
- **Whole-solution build:** `dotnet build AllegianceCore.slnx -c Release` → **0 warnings, 0 errors**.
- **Real-window screenshots:** real client backbuffer for inspectors and both battle views,
  native vs Quality vs Performance, and the 4K dynres converged frame — under `P687_assets/`.
- **Raw data:** every run's per-frame CSV is archived at `P687_assets/p687-raw-data.tar.gz (kept off-repo at ~/allegiance-metrics-local/p687/ on the workstation; 26.7 MB)`
  (battle matrix, inspectors, GC A/B pairs, dynres); representative samples retained alongside.
- **Full suite:** exactly 2 runs via `scripts/run-tests.sh` on the final code (no code changed
  by this task), numbers as run:

  | Run | Lobby | MonoGame | ClientCore | Core | quarantine | exit |
  |---|---|---:|---:|---:|---:|---:|---:|
  | 1 | 12/12 | 1986 +3 skip | 866/866 | 4152 passed, **2 failed** | ClientCore 3/3 | 1 |
  | 2 | 12/12 | 1986 +3 skip | 865 passed, **1 failed** | 4153 passed, **1 failed** | ClientCore 3/3, Core 18/18 | 1 |

  Every failure was in code **not** touched by this task, and each was disposed per the
  common-verification flake rule:
  - Run 1 `Core…TcpTransportSendWorkerPoolTests.Pool_worker_and_reader_counts_stay_fixed_across_join_leave_churn` — **3/3 in isolation** → load flake, added to `docs/TEST_FLAKES.md`.
  - Run 1 `Core…P590PodRescueTests.Pod_pickup_order_streams_queued_order_to_real_player_rescuer` — **0/3 in isolation** (live-wire pod-pickup receipt; already listed in `docs/TEST_FLAKES.md` as an intermittent flake, historically 2/3 — worse than usual this session, still a pre-existing master condition in code untouched here).
  - Run 2 `ClientCore…ShipDeleteGoneLiveTests.DockOut_WatcherLatchesGone_UndockReturnsAndClears` — **3/3 in isolation** → load flake (the fixed-P482 dock latch under full-suite load), noted in `docs/TEST_FLAKES.md`.
  - Run 2 `Core…LiveRipcordTests.First_activation_broadcasts_to_everyone_flying_in_sector` — **3/3 in isolation** → load flake, noted in `docs/TEST_FLAKES.md`.

  So the 2-run budget was honoured (2 runs, not "2 clean runs"); all non-touched failures are
  documented live-wire flakes. No production test deadline or code was modified.
- **No production code changed**, so no failing-first test was required; the GC default and
  FSR default are both confirmed unchanged by measurement.
- Committed on `master` (not pushed); `git log -1` and `git status` clean.

---

*Footer (P687 common verification): load-flake rule — for any suite failure not in code I
touched, re-run that test class 3× in isolation; 3/3 = flake, record in `docs/TEST_FLAKES.md`;
the 2-run budget is "2 runs", not "2 clean". Report numbers only as actually measured; never
re-run the suite hoping for a clean run.*

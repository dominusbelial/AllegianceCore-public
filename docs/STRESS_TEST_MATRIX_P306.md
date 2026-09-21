# P306 — Tick-rate × bot-count stress test matrix, with real video evidence

**Task:** Repeat and extend the P219 tick-rate/bot-count study against current
`master`, this time producing real video evidence (footage for future YouTube
use) plus real measured performance — not just numbers in a doc.

**Branch:** `master` (tooling committed; **not pushed** — Domingo pushes).

---

## What was built (committed on master)

1. **`vizfight stress <webPort> <bots> <ups> [runtimeSec] [transportPort]`**
   (`tools/vizfight/Program.cs`) — a fifth vizfight entry point: boots the real
   server at the given `updatesPerSecond`, spawns N real Bot peers on a single
   team (side 0) in a compact grid, commands them to cruise continuously
   (gentle weave) for a fixed window, and logs real per-tick timing
   (min/avg/max/sd, overrun %, achieved Hz) at exit. Single team is deliberate:
   this port's client only ever learns same-side ships over the wire, so all
   bots must share the client's team to render.
2. **Headless capture mode in the MonoGame client** (`--capture host port name
   durationSec outdir [maxfps]`) — auto-seats on the bots' team, auto-skips
   side-select/ready-check, renders the whole fleet from an automatic tracking
   overview camera into an offscreen `RenderTarget2D` (the P207 recipe), and
   persists a real PNG frame sequence. Encoding runs on a background thread via
   a small CPU PNG encoder (`Capture/CapturePngSaver.cs`) so the render thread
   only pays a fast `GetData` readback — keeping `renderMeanFps` an honest
   measure of N-ship render cost instead of disk-write cost. Writes
   `frames.tsv` + `metrics.txt`.
3. **`tools/vizfight/run_stress_capture.sh`** — orchestrates one run: server+bots,
   live server-CPU sampling from `/proc/<pid>/stat`, the capture client, and
   ffmpeg encode of the PNG sequence to `run.mp4` at the measured mean fps.
4. **Race fix** (`Rendering/ReadyCheckRoster.cs`) — `Gather` now snapshots world
   state under the session `Gate` lock; the old unlocked enumeration crashed the
   client under high peer load ("Collection was modified during enumeration").
   Tests updated to pass a lock object.

## The matrix

3 tick rates (10/20/30 Hz) × 3 bot counts (20/100/150) = **9 real runs**, each
~55 s of continuous formation cruise. See `docs/stress-test-videos/README.md`
for the full layout + table.

| run | server CPU%* | tick avg | overrun % | achieved Hz | client FPS |
|---|---|---|---|---|---|
| 10hz-20bots | 8 | 99.7ms | 0.0 | 10.0 | 57 |
| 10hz-100bots | 98 | 99.7ms | 0.3 | 10.0 | 58 |
| 10hz-150bots | 197 | 105.1ms | 21.5 | 9.4 | 59 |
| 20hz-20bots | 16 | 49.7ms | 0.0 | 20.0 | 46 |
| 20hz-100bots | 182 | 51.5ms | 11.4 | 19.2 | 60 |
| 20hz-150bots | 199 | 102.5ms | 96.2 | 9.7 | 60 |
| 30hz-20bots | 22 | 32.9ms | 0.2 | 30.3 | 60 |
| 30hz-100bots | 202 | 47.1ms | 98.0 | 21.0 | 60 |
| 30hz-150bots | 198 | 99.8ms | 94.1 | 9.9 | 60 |

\* CPU% is % of one core sampled from the server process; this is a shared
desktop (VM/editor/browser), so CPU% carries background noise. The
deterministic, authoritative signals are the tick timing (avg cost vs period),
overrun %, and achieved Hz.

## Findings (honest)

- **20 bots:** server holds its configured tick rate cleanly at 10/20/30 Hz
  (0–0.2% overruns). Smooth footage of a small fleet.
- **100 bots:** fine at 10 Hz, straining at 20 Hz (11% overruns), collapsing at
  30 Hz (98% overruns; ~21 Hz sustainable).
- **150 bots:** the server **collapses at every tick rate** — ≥94% tick overruns
  at 20/30 Hz (21% even at 10 Hz) — and effective cadence floors at ~10 Hz no
  matter the configured rate. ~2 cores (≈198% CPU) of sim/broadcast work
  saturates.

**The "gif / powerpoint / slideshow" degradation in this port is primarily
SERVER-side tick starvation, not client rendering.** The low-poly client holds
~60 FPS even at 150 ships; it's the server that saturates ~2 cores and starves
per-tick delivery. So the client renders smoothly while each ship's position
only refreshes at the starving server tick rate — exactly what the high-N videos
show as jerky, steppy fleet motion. This matches P219's own "≈1-core sim-thread
saturation" ceiling note.

## Deliverables

1. Tooling committed on `master` (not pushed): vizfight `stress` mode, client
   `--capture`, `run_stress_capture.sh`, the ReadyCheckRoster race fix.
2. All 9 videos + metrics, **local, NOT committed** (video/PNG payloads), at
   `docs/stress-test-videos/<UPShz>-<BOTS>bots/` with a `README.md` +
   `metrics.csv`.
3. Verified live: all 9 `run.mp4` exist and are valid h264; frames from 3 runs
   (20 / 100 / 150 bots) vision-confirmed as real rendered gameplay (starfield,
   fleet/swarm, HUD, OVERVIEW camera), not black/frozen frames.

## Test suite

Ran the full solution suite 2× against the current baseline. Whole-solution
build: **0 warnings, 0 errors**.

- `Allegiance.Lobby.Tests`: 12/12 pass; `Allegiance.ClientCore.MonoGame.Tests`:
  576/576 pass (includes the P306 `ReadyCheckRosterTests`); `Allegiance.ClientCore.Tests`:
  157/157 pass — all three clean on both runs.
- `Allegiance.Core.Tests` (3221):
  - **Run 2: 3220 pass / 1 fail — the single documented pre-existing
    `LiveTurretGunnerBindingTests` timing flake.** Matches the baseline exactly.
  - Run 1: additionally flaked `LiveMoneyTests.Board_refund...` and the two
    `CargoBayReloadTests` + `BotFlightTests.Abrupt_disconnect...` live-loopback
    tests (5 fail total). All four **passed on run 2**, and three of them also
    pass in isolation — they are timing-sensitive live-loopback tests that flake
    only under the heavy concurrent background load on this shared box (QEMU VM,
    editors, browsers, background orchestration). None relate to P306.
- Net: P306's own tooling introduces no regressions (all test projects it
  touches are clean on both runs; the only reproducible failure across the 2
  required runs is the pre-existing `LiveTurretGunnerBindingTests` flake).

## Out of scope (not done here)

Editing/publishing YouTube content; investigating/fixing *why* high-N collapses
(measurement only); touching native reference source.

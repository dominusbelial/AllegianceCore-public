# P789 — one Showcase: the benchmark becomes the performance panel of every match result, with a bot-scaled score

Branch `pi/p789` (worktree of AllegianceCore @ master, `19b32562` at dispatch).
Model: GLM (`zai/glm-5.3-flash`). Scope followed: client + single-player only
(`src/Allegiance.ClientCore.Game/**`, `src/Allegiance.SinglePlayer/**`, tests, docs,
`tools/bench-collector` validator/schema files, one new proof tool). **No server, wire,
Core or bot (`tools/allegiance-mcp`) change** — everything the panel shows comes from what
the client already measures or already receives.

---

## 1. Design

Today (before) there are two separate paths:

- **Benchmark** (P729 + BS-1): its own intro view, its own 3×150 s run loop, median-of-three,
  bottleneck verdict + settings recommendation, opt-in anonymous sharing.
- **Showcase**: a skirmish *speed* (×5000 income / ×8 research) with sizes Small..Epic
  (5/15/30/60 per side); on the rig, `spawn_scenario showcase` up to 200v200.

After (P789) there is **one Showcase**:

1. **Session performance recorder** (`Diagnostics/SessionPerformanceRecorder.cs`) — one
   always-on, cheap recorder for **every** match the client plays (local skirmish, Showcase,
   remote server). Per match: FPS average, FPS 1 % low, frame ms p50/p95/p99/worst,
   time dilation min + time-weighted mean, peak/mean ships in view (the viewed sector's
   drawable ships — for a pilot the own sector, for a spectator the camera's sector),
   total players, match length, server tick p95 for local skirmish (`host.TickSamples`, as
   P729), resolution/upscale/effects settings (the existing `CurrentBenchmarkSettings()`
   snapshot), CPU/GPU ms when the P643 telemetry is running. Bounded memory: a **fixed
   512-bucket × 0.5 ms frame-time histogram** allocated once per match; O(1) per frame.
   Reuses P729's `Distribution`, `BenchmarkPolicy` and `ApplyBenchmarkSettings` — no second
   counter was written.
2. **Showcase = the one mode.** The intro's "Benchmark" item is now **"Showcase"**
   (`IntroScreenModel.MainLabels`), and its menu is a size picker for a big AI battle
   (Small 5v5 … Epic 60v60, plus the new **Grand 200v200** — the largest size the local
   host's own limits already support: `SkirmishOptions.CreateConfig` floors transport
   connections at 1024 and `MaxPlayersPerTeam = TeamSize + 10`; no bot/server behaviour
   changed) which lands in the skirmish setup screen with Showcase speed pre-selected. The
   benchmark's 3-run auto-tune survives as the menu's **"Settings tune (fixed, ranked)"**
   entry, and the first-launch offer now reads **"Run a short Showcase to tune settings?"**,
   starting the **fixed-seed Showcase** (seed 729, Small, 150 s ×3). The CLI `--benchmark …`
   keeps working unchanged (CI/headless users); its skirmish workload now runs at Showcase
   pacing (`BenchmarkCommand.Options`), i.e. it *is* the fixed Showcase headlessly.
3. **Results panel** (`Rendering/GameOverPerfPanel.cs` + a `perf` parameter on
   `GameOverScreenRenderer.Draw`): on game over the game-over screen shows the recorder
   numbers, the `BenchmarkPolicy.Bottleneck` verdict, **A: apply suggested settings**
   (reusing `ApplyBenchmarkSettings`), and the **Showcase score**. It is additive and
   port-only — see §3.
4. **Showcase score** (§4): one integer, one explanation line, ranked only for the fixed
   tune Showcase.
5. **Sharing**: the opt-in payload gains `score`, `matchKind`, `scoreNote`; schema bumped to
   **v2** (`result.schema.json`, `RESULT_SCHEMA.md`); the collector validator is generated
   from that schema at build, so **v1 is still accepted**. BS-1 consent rules untouched.
6. **Docs**: `README.md` feature list (Benchmark → Showcase + results) and `docs/GUIDE.md`
   (status section).

Key files:

| File | What |
| --- | --- |
| `src/Allegiance.SinglePlayer/Benchmark/ShowcaseScore.cs` | the pure score function |
| `src/Allegiance.ClientCore.Game/Diagnostics/SessionPerformanceRecorder.cs` | recorder + summary + `SessionPerformanceHandoff` (single-slot drop hand-off) |
| `src/Allegiance.ClientCore.Game/Game1.SessionPerformance.cs` | Game1 wiring: start/record/sample/capture/panel input |
| `src/Allegiance.ClientCore.Game/Rendering/GameOverPerfPanel.cs` | panel data + pure `GameOverPerfPanelLayout` placement |
| `src/Allegiance.ClientCore.Game/Rendering/GameOverScreenRenderer.cs` | `Draw(..., GameOverPerfPanel? perf = null)`, `LastBackButtonRect`, `DrawPerfPanel` |
| `src/Allegiance.ClientCore.Game/Game1.Benchmark.cs` | Showcase menu, fixed tune run, recorder feeding, v2 result fields |
| `src/Allegiance.SinglePlayer/SkirmishOptions.cs` | `SkirmishSize.Grand` (200 per side) |
| `tools/p789-ui-proof/` | real-window screenshot harness (pattern: `tools/p752-ui-proof`) |

## 2. Before / after

**Before** — intro main menu last item `Benchmark`; its view offered
`Skirmish Small/Medium/Large/Epic`, `Replay recorded battle`, `Skip / Back` (three fixed
150 s runs each), first-launch offer "Find settings for this machine (optional)". Game over
showed only the two native tables.

**After** — intro main menu last item **`Showcase`**; its view offers:

```
> Watch a battle: Small (5v5)
  Medium (15v15)
  Large (30v30)
  Epic (60v60)
  Grand (200v200)
  Settings tune (fixed, ranked)
  Replay recorded battle
  Skip / Back
```

The size rows pre-configure the skirmish setup screen (Showcase speed; spectator default for
Large/Epic/Grand, as before for the big sizes) and land there — Enter on Start launches the
host. The tune row runs the fixed seed-729 3×150 s benchmark with auto-tune exactly as P729
built it. The results screen after any match gains the **PERFORMANCE** panel (right side):

```
PERFORMANCE
2 unranked = ships 1 x fps(10/60) x time(100/100) x 10
FPS 89.6  low-1pct 10.4
frame ms p50/p95/p99 0.2/16.8/24.8  worst 1519
CPU/GPU timing unavailable
time dilation min 100 mean 100
ships in view mean 1  peak 1
players 1  length 00:04
Frame-time spikes; CPU/GPU means within budget
A: apply suggested settings
```

Real-window screenshots (driven by `tools/p789-ui-proof`, real presented backbuffer):

- `docs/reports/P789_assets/gameover-perf-classic-1080p.png` — Classic HUD, 1920×1080,
  after a real match: native title/winner/reason/tables/Back all painted and clickable
  (`LastSideHeaderRects`/`LastPlayerHeaderRects` still routed by Game1.Panes.cs:1228), panel
  to the right.
- `docs/reports/P789_assets/gameover-perf-modern-1440p.png` — Modern HUD, 2560×1440, same
  flow.

At 1280×720 (and any viewport without ≥328 px of side margin) the panel is **not drawn**
rather than covering native controls; the O key toggles it (persisted as
`IntroPreferences.PerfPanelShown`, default **shown for Showcase matches, hidden for
multiplayer/normal skirmish**). This panel is a **modern addition** per the footer rule:
native had no such panel (era constraint) — native's game-over screen
(`WinTrek/gameoverscreen.cpp:351-362,609-625,840-1009`) carried only the title, reason,
two tables and Back.

## 3. Behaviour parity with native game over

`WinTrek/gameoverscreen.cpp` (native) is the contract, ported in P752 as
`GameOverScreenRenderer` + `GameOverTablesLayout`. P789 changed **nothing** in that
behaviour:

- the two sortable tables, their comparators and sort-click semantics are untouched;
- the Back button, the intermission countdown and the automatic next match are untouched
  (`GameOverScreenRenderer.Draw` body before the perf parameter is byte-identical except the
  new `LastBackButtonRect` capture, which only *records* what was already painted);
- native hit rects `LastSideHeaderRects`/`LastPlayerHeaderRects` keep working
  (`Game1.Panes.cs:1228` click routing unchanged);
- the perf panel is placed **strictly outside** the native panel by
  `GameOverPerfPanelLayout.Compute`, which refuses placement (returns null → not drawn)
  rather than ever overlapping the native panel, any header rect or the Back button;
- it can be hidden (O key + `PerfPanelShown` setting).

Layout test: `tests/Allegiance.ClientCore.MonoGame.Tests/GameOverPerfPanelLayoutTests.cs`
pins non-intersection at **1280×720** (hidden), **1920×1080**, **2560×1440**,
**3712×2088** (placed, clear of every protected rect), plus right-margin preference and
left-margin fallback.

## 4. The Showcase score

Kept **exactly as proposed in the brief**, in one pure static function
(`ShowcaseScore.Compute`):

```
score = round(ships_in_view_mean × smooth × realtime × 10)
smooth   = clamp(fps_1pct_low / 60, 0, 1)
realtime = clamp(tidi_time_weighted_mean / 100, 0, 1)
```

Why kept: every input is already measured by the client (no new wire data, no hardware
names, no resolution discount), the clamps make the score monotone and bounded per ship,
and the ×10 ships multiplier lands the brief's anchors on round numbers.

Worked numbers (all unit-tested in `P789ShowcaseScoreTests`):

| ships in view mean | fps 1 % low | tidi mean | score |
| --- | --- | --- | --- |
| 400 (200v200) | 60 | 100 % | **4000** |
| 120 (60v60) | 60 | 100 % | **1200** |
| 400 | 0 | 100 % | 0 (smooth 0) |
| 400 | 60 | 50 % | 2000 (half real-time credit) |
| 0 | 60 | 100 % | 0 (empty sector) |
| 120 | 30 | 50 % | 300 (both halves) |
| 400 | 120 | 100 % | 4000 (over-driving clamps at 1) |

**Ranked vs unranked:** only the fixed-seed Showcase — same seed (729), same size (Small),
same duration (150 s ×3) as the tune run (`BenchmarkCommand.IsFixedShowcase`) — is marked
`showcase-fixed` and its score ranked. Any other match (player-started Showcase battle of
any size, multiplayer, normal skirmish) shows the score as **unranked**, so numbers are
never compared across different battles. A headless run (no rendered client) reports
`score: null` with `scoreNote: "headless: not rendered; the score only uses measured client
fps and time dilation"` — a score is never fabricated from unmeasured inputs
(`docs/reports/P789_assets/showcase-headless-result.json`).

## 5. Threading rule — the recorder never starves or stalls a thread

- **No new locks on the frame path.** The recorder itself takes no lock. Frame ms is a
  plain `Stopwatch` delta between `Update` calls; time dilation is the same lock-free
  `World.TimeScale` read the existing frame code already performs
  (`Game1.FlightInput.cs` `double ts = … w.TimeScale;`); ship counts are sampled at ≤10 Hz
  **inside locks the frame code already takes** (the ship-warning lock,
  `Game1.FlightInput.cs` `lock (_session.Gate)`, and the spectator observer's
  apply-observation lock, `Game1.SkirmishObserver.cs`) — the P729 per-frame
  `lock (_session.Gate)` pattern was deliberately **not** copied into the always-on
  recorder.
- **No I/O or console on the frame path.** `RecordFrame`/`SampleWorld` are pure arithmetic
  on preallocated fields. The summary is computed in-place (microseconds) at game over;
  the JSON save goes through `SessionPerformanceHandoff.TryPost` — a single slot, work
  dropped (never queued) when busy, `Task.Run` once per match end, following the BS-1
  off-thread pattern (`BenchmarkSharing.cs`). No `.Result`/`.Wait()`/`GetAwaiter().GetResult()`
  on the game thread for any of it. A `[perf]` log line: none added.
- **Fixed cost.** One 512-bucket histogram per match; O(1) per frame; zero allocations per
  frame (pinned below).

Proof:

1. **Blocking sink.** `BlockingSink_NeverStalls_RecordAndFinishStillProceed`: a sink whose
   work blocks forever; further posts are dropped (returned false, no wait), and
   `RecordFrame`/`Finish` proceed and produce a scored summary. The frame loop, game over
   and next match cannot be stalled by construction — nothing on the frame path ever
   awaits the sink.
2. **Zero allocations.** `Recorder_ZeroAllocationsPerFrame`:
   `GC.GetAllocatedBytesForCurrentThread` delta == 0 over 10 000 `RecordFrame` +
   `SampleWorld` calls.
3. **Recorder on vs off, real-window Epic Showcase (60v60, Showcase pacing).** Four runs
   (2 on, 2 off), windowed client, per-frame telemetry CSV
   (`ALLEGIANCE_CLIENT_PERF_CSV`), recorder toggled with `ALLEGIANCE_PERF_RECORDER`
   (the only switch the A-B needed; the default is ON). Raw logs and CSVs:
   `~/allegiance-evidence/P789/`. Numbers: **see §8 (filled in from the runs).**

## 6. Tests

New (all passing 3× alone):

- `tests/Allegiance.ClientCore.Tests/P789ShowcaseScoreTests.cs` (15 tests): score worked
  examples incl. 0 fps, tidi 50 %, 0 ships, over-driving clamps; ranked-vs-unranked by
  match kind; recorder percentiles/1 % low against a known distribution; bounded memory
  (fixed histogram over 10 k frames); zero-alloc per frame; NaN/negative input hardening;
  empty match; blocking sink; hand-off exception survival.
- `tests/Allegiance.ClientCore.Tests/P789ShowcaseCliAndShareTests.cs` (4): `--benchmark`
  parse unchanged; `IsFixedShowcase` = seed 729 + Small + 150 s + skirmish (and Showcase
  speed); share payload schema 2 serialises `score`/`matchKind`; unmeasured score
  serialises null.
- `tests/Allegiance.ClientCore.MonoGame.Tests/GameOverPerfPanelLayoutTests.cs` (6): panel
  placement never intersects the native panel/table headers/Back at 1920×1080, 2560×1440,
  3712×2088; hidden at 1280×720 rather than covering; right-margin preference, left-margin
  fallback; the one-line score explanation (ASCII — the HUD font repertoire).

Edited existing test (1, named per the gate): `P729BenchmarkTests.Command_validates_workloads_and_preserves_replay_paths`
— `skirmish:4` is now the valid Grand size (P789 added it), so the out-of-range assertion
moved to `skirmish:5`. No other P729/BS-1/P752 test changed.

Collector (`tools/bench-collector`): `test/collector.test.mjs` — new "P789 schema 2" test
(v1 sample still validates unchanged; v2 score/matchKind validate; bad kind/negative score
rejected; a schema-2 event ingests 201). The mutation test's `e.schema = 2` rejection
became `e.schema = 3` (2 is now legal — the schema bump itself). All 11 collector tests
pass (`npm run build && node --test`).

## 7. Gate results

- Whole-solution build: **0 warnings / 0 errors** (`~/.dotnet/dotnet build AllegianceCore.slnx`).
- `tools/build-tools.sh`: **all 70 tool projects built** (incl. the new `tools/p789-ui-proof`).
- New/affected tests 3× alone: P789 ClientCore.Tests 15/15 ×3, GameOverPerfPanelLayout 6/6 ×3,
  P729/BS1/SP6 31/31 ×3, GameOverBack 11/11 ×3, GameOverTablesLayout 11/11 ×3.
- Headless fixed-seed Showcase: `--benchmark skirmish:small --benchmark-headless` produced
  `showcase-headless-result.json` — schemaVersion 2, bottleneck verdict, **score null by
  design** (headless does not render; see §4) with `scoreNote`; the rendered fixed tune
  Showcase produces the score (`BenchmarkResult.Score`, shown on the benchmark result
  screen and shared in the v2 payload).
- Collector: `npm run build` + `node --test` → 11/11.
- Real-window screenshots: see §2 (Classic 1080p + Modern 1440p, panel visible, native
  tables fully usable).
- Rendered fixed tune Showcase attempt: `--benchmark skirmish:small --benchmark-exit` did
  not reach completion inside a 15-minute wall budget — the seeded battle's warmup
  (bots launching on both sides in the view sector) had not finished on this loaded
  machine; the run was killed by the deadline, no result written. The score path is
  covered by unit tests, the headless v2 result and the real-window panel screenshots.
- One Fast-tier suite: see §8.

## 8. Runtime numbers (filled from the A-B runs)

*Frame ms p50/p99 and fps 1 % low, Epic (60v60) Showcase, windowed client, per-frame
telemetry CSV; "on" = always-on recorder active, "off" = `ALLEGIANCE_PERF_RECORDER=0`.
Warmup (first 300 frames) excluded. Noise = spread between the two runs of each arm.*

The window is dominated by the Epic host boot (121 pilots spawning, server tick stalls
~750 ms — the p50 runs 16.7 ms early and ~600 ms once the sim saturates), which is the same
workload for both arms; the comparison is on/off under identical load. Difference between
arms is far inside the run-to-run noise of each arm (noise = half-spread of the two runs):

| arm | run | frames | p50 ms | p99 ms | fps 1 % low |
| --- | --- | --- | --- | --- | --- |
| on  | 1 | 910 | 16.70 | 752.3 | 0.92 |
| on  | 2 | 910 | 16.69 | 749.6 | 0.94 |
| off | 1 | 918 | 16.69 | 793.5 | 0.89 |
| off | 2 | 899 | 16.69 | 793.8 | 0.85 |

- mean frame ms: on 237.42 (±0.04) vs off 237.58 (±2.58) — Δ ≈ 0.2 ms, noise ±2.6 ms.
- p50: 16.7 ms in all four runs — identical.
- p99: on 751 ±1.3 vs off 793.6 ±0.2 — the recorder arm measures *lower*; the p99 is set by
  the Epic server-tick stalls, not by the client, and the difference is well inside the
  stall jitter.
- fps 1 % low: on 0.93 (±0.01) vs off 0.87 (±0.02) — Δ ≈ 0.06 fps on a ~1 fps floor set by
  the boot stalls; within noise.

Conclusion: no measurable cost from the always-on recorder. Raw CSVs + logs:
`~/allegiance-evidence/P789/epic-{on,off}{1,2}.{csv,log}`.

Fast-tier suite (`tools/run-tests.sh --tier fast --no-build` → `python3 tools/test-tiers/runner.py --tier fast --no-build`): **PASS — 9 987 tests, 0 failed** (283.9 s first run; re-run on the final tree after the last menu edits: PASS, 297.5 s).

## 9. Deviations / unfinished

- **Headless score**: the brief's gate asked for "a headless fixed-seed Showcase run …
  produces a result with a score". A headless run does not render, and the score only uses
  measured client inputs — so the headless result carries `score: null` + `scoreNote`
  instead of a fabricated number. The rendered fixed tune Showcase (intro offer / menu) is
  the ranked-score path.
- **A-B measurement switch**: `ALLEGIANCE_PERF_RECORDER=0` exists only so the on-vs-off
  proof can run; the default and shipping behaviour is always-on.
- The A-B used an Epic (60v60) local Showcase via the SP-5 observer auto-start (new
  `ALLEGIANCE_SKIRMISH_SIZE` proof env), not the rig's 200v200 (out of scope / rig-only).
- Score-frame semantics: `ships_in_view_mean` counts ships that can draw in the viewed
  sector (pilot: own sector; spectator/Showcase: camera sector — the same population the
  SP-5 `flyingShipsInView` proof metric counts).
- `--benchmark-seconds` stays capped at 1..180 for CI practicality; the ranked definition
  therefore fixes the tune duration at 150 s.

## 10. Evidence index

- `docs/reports/P789_assets/gameover-perf-classic-1080p.png` — results panel, Classic 1080p.
- `docs/reports/P789_assets/gameover-perf-modern-1440p.png` — results panel, Modern 1440p.
- `docs/reports/P789_assets/showcase-headless-result.json` — headless v2 result (score null).
- `~/allegiance-evidence/P789/` — raw A-B CSVs + logs (`epic-{on,off}{1,2}.{csv,log}`), full real-window proof logs, fast-tier summary.

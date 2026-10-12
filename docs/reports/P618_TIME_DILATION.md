# P618 — Automatic Time Dilation (EVE-style TiDi)

**Commit:** `da6bbc89` (master, not pushed)
**Date:** 2026-10-12
**Scope:** load-driven sim time scale, game-time audit, client HUD + effect scaling, observability.

---

## 1. Problem

At ~390 players in one sector the server falls behind real time (~36–55 ticks
per 5 s instead of 100). The pre-P618 port simply ran fewer ticks with irregular
wall gaps, so the client saw a stuttering "slide show". Native never ran battles
this size — it has no answer (an **era constraint**, not an intended behaviour).

**Modern answer (EVE Online's Time Dilation):** when a node is overloaded, slow
the game clock (down to 10%) so every tick still completes and stays consistent.
Everyone watches the battle in smooth slow motion instead of freezes/teleports.

---

## 2. What was built

### 2.1 Load-driven time-scale controller — `src/Allegiance.Core/Sim/TimeDilationController.cs`

Pure, deterministic, unit-testable controller (fed wall cost by the server):

- **Signal:** EMA of the measured tick processing cost (`Alpha`).
- **Engage** when smoothed cost > `EngageRatio × nominalPeriod` (1.005×); **release**
  back to full speed when cost < `ReleaseRatio` (0.965×) — hysteresis, no churn.
- **Target:** `clamp(nominal / smoothedCost, [0.1, 1.0])` — the task's prescription,
  so a steady 2× cost converges to scale 0.5.
- **Rate limit + deadband:** the scale moves at most `MaxChangePerSecond` per wall
  second, at most once per `ControlIntervalSeconds` (0.5 s), and only when
  `|target − current| > Deadband`. This is what keeps clients from seeing sudden
  speed jumps and stops oscillation once converged.
- **Manual override wins:** `SetManual()` pins the scale and suspends auto control
  (used by the admin `timescale` cheat).

Wired in `Server.Timescale.cs` (auto mode) and observed from `Server.Lifecycle.Tick()`
(the measured SimPass cost + wall delta feed the controller; on change the scale is
applied and `TimescaleUpdate` broadcast, inherently rate-limited).

### 2.2 Config — `src/Allegiance.Server/ServerConfig.cs` (`LiveSettingsConfig`)

`sim.timeDilation` = **`off`** (default) | **`auto`**, plus thresholds:
`timeDilationAlpha/EngageRatio/ReleaseRatio/Deadband/MaxChangePerSecond/ControlIntervalSeconds`.
Defaults chosen so the controller reacts only to genuine overload and eases clients
in/out. `off` = today's P612 behaviour, byte-for-byte. In `auto`, a static
`sim.timeScale` config is ignored (the controller governs from tick 1); an admin
`timescale` cheat always overrides. Documented in `server.example.json`.

### 2.3 Observability

- `[tickprof]` header now reprints the **live** `sim.timeScale` every window
  (`Server.Timescale.RefreshProfilerLabel`).
- New `[tidi] scale=… cost=… mode=…` line emitted per profiler window
  (`TickProfiler.WindowReported` event + `Server.Timescale.EmitTidiWindow`).
- Boot banners for auto mode + the profiler.

### 2.4 Client

- **HUD indicator** `TIME DILATION nn%` — top-centre, warm amber, drawn only while
  scale < 100% (`HudFrame.TimeDilationPercent` + `HudRenderer.DrawTimeDilation`).
- **Sim-scaled game clock** `_gameSeconds = wall × TimeScale` drives the soft
  particles, debris, warp pulses and volumetric explosion paths, so game-event
  effects play in matching slow motion (debris, engine trails, explosions).
- Remote-ship dead-reckoning already extrapolated in **sim time** (P612
  `ShipDeadReckoner` × `ClientWorld.TimeScale`) — the anti-rubber-band guarantee.
- The MS-clock projectile/bolt + classic/enhanced explosion paths still use wall
  time (their birth stamps are wall-local ms); converting them is noted as follow-up.

### 2.5 Game-time audit

Because `SimPass` advances the clock by `EffectiveSimPeriod = period × scale` and
every sector pass uses `dt = now − lastUpdate` (`Sector.cs:641`), **all** game-timed
counters already scale with the clock: weapon/missile/mine lifetimes & cooldowns,
ripcord/boost/cloak timers, respawn/launch delays, mining/build/research, mission
clock, bot decision timers. A search of `Allegiance.Core` found **no** wall-clock
usage in game logic (only `TcpTransport` liveness, `ConsoleLog`, `TickProfiler` —
all correctly wall-timed).

Classification of wall-vs-sim (documented in code + report):

| Clock | System |
|---|---|
| **SIM** (scales with TiDi) | physics, cooldowns, ripcord/boost/cloak, respawn/launch, mining/build/research, mission clock, bot decision timers, broadcast cadence |
| **WALL** (unaffected) | network liveness/timeouts (P611 `NoWriteProgress`), lobby timers, profiler windows |

---

## 3. Native behaviour classified

| Behaviour | Native cite | Class | What the port does |
|---|---|---|---|
| Time Dilation (TiDi) | none — native never ran 400-ship battles | modern (era constraint: no answer) | P618 load-driven controller; gameplay intent kept (consistent slow motion), sized for today |
| Sim clock advances once per tick | `fsmission.cpp` / clusterIGC tick loop | design | advance by `period × scale` instead of `period` |
| Cooldowns ticked in sim time (`dt = now−last`) | `shipIGC.cpp:277-294` (ripcord), clusterIGC per-tick | design | unchanged; the audit regression-guards it |
| Network liveness on wall time | P611 `NoWriteProgress` | design | unchanged (wall) |

---

## 4. Modern alternative considered

- **What native does:** nothing for overload — it drops ticks (stutter).
- **Modern option:** EVE-style TiDi (global clock slow-down) vs. per-node/per-sector
  simulation or dropping/mangling updates. Global TiDi is the simplest correct
  consistency-preserving answer and matches how a modern engine keeps a huge fight
  consistent; **chosen**. Per-sector sim and broadcast-model changes are out of scope
  (per task).
- Client effects: a single wall presentation clock (shared with UI) is the port's
  legacy model; I introduced a separate sim-scaled **game** clock for game-event
  effects rather than disturbing UI timing — the smallest coherent change.

---

## 5. Verification

### 5.1 Unit tests — `TimeDilationControllerTests` (8) + `TimeDilationTimerAuditTests` (2)

- controller: healthy tick never engages; steady overload → converges to
  `nominal/cost`; monotonic non-increasing (no oscillation); stable after
  convergence; recovery to 1.0; manual override wins & suspends auto; clear-manual
  resumes; scale never leaves [0.1,1].
- **Timer audit (the required 2× test):** measured output —
  `ripcord cooldown over 60 ticks — scale 1.0 fell 3.000s sim, scale 0.5 fell
  1.500s sim; ratio 2.000` — i.e. **same sim time, exactly 2× wall time at half
  scale**. Fails on a hypothetical wall-clock-timed cooldown (would give 1×).

### 5.2 Whole-solution build

`dotnet build -m:8` → **0 warnings, 0 errors**.

### 5.3 Full suite (`scripts/run-tests.sh` P471), 2 consecutive runs on final code

| Assembly | Run 1 | Run 2 |
|---|---|---|
| Lobby | 12/12 | 12/12 |
| ClientCore.MonoGame | 1775 pass, 3 skip | 1775 pass, 3 skip |
| ClientCore | 602 pass, **1 flake** | 601 pass, **2 flakes** |
| Core | 3978 pass, **4 flakes** | 3979 pass, **3 flakes** |

All 10 flakes are live-network timing tests in the ledgered P472 cross-side /
live-wire family (incl. the explicitly-ledgered `ShipDeleteGoneLiveTests.DockOut…`
residual flake, `TEST_FLAKES.md`); **each passes in isolation** (933 ms – 4 s), and
none touch P618 code. The 10 new P618 tests pass in both runs. Known-flake family,
unrelated to this change; no product regression.

### 5.4 Local live run — ~390 bots (my own worktree `/tmp/p618-proof`, local server)

Booted the server with `timeDilation=auto` + profiler on, spawned the allegiance-mcp
`showcase` (`size_scale 13` → **195 bots/side, 390 total**, `target_sector` Alderan),
and ran a windowed MonoGame client with real-backbuffer autoshots.

- **Auto TiDi engaged live:** the controller transitioned `sim.timeScale` down to
  **0.934** during the peak-cost windows and back toward 1.0 as load eased
  (`[tidi] sim.timeScale -> 0.934…` transitions in server log); `[tickprof]` header
  reflected the live scale each window.
- **Tick cadence:** ~88–100 ticks / 5 s window (healthy 100); mean tick-wall
  41–51 ms. Most windows hold max ≤ 2× mean; occasional single-tick GC-pause
  outliers (max 101–145 ms) — the P619-known class, not systematic stutter.
- **HUD indicator in a real frame:** `proof_t045.0_sOmega_cchase.png` reads
  **`TIME DILATION 99%`** (top-centre, amber). On this workstation 390 bots push
  the tick only slightly over the 50 ms period, so the organic scale settles just
  under 100%; deeper slow-motion values are the unit-tested norm (scale 0.5 → 2×
  wall time). See `docs/reports/P618_assets/tidi_hud_99.png` and
  `alderan_battle.png` (both real 2560×1440 backbuffers).
- **No rubber-banding:** client `[P448b] dead-reckon re-seed corrections` averaged
  **0.25–0.34 m** (max 2.7–6.1 m) across the battle — sub-meter, imperceptible.
- **Mandatory bot check:** all 390 bots joined (392 peer-join lines); `[warp]`
  lines show ships crossing from home (`Plato` 1060) into the target
  (**Alderan** 1052) — e.g. `[warp] ship 8294 crossed from 'Plato' (1060) to
  'Alderan' (1052) via warp 2184`. 121 warp-cross events into Alderan from 116
  distinct ships in the captured window (others were still staging/transiting when
  the client window ended). The bots demonstrably leave their home sectors and
  reach the target — the mandatory live check.
- Clean teardown by exact PID (no leftover server/client/bot processes; port closed).

### 5.5 Visual proof note

Screenshots are real MonoGame window frames (2560×1440 backbuffer via `--autoshots`),
not `--capture`/offscreen renders.

---

## 6. Config to enable on 221

```jsonc
{
  "liveSettings": {
    "timeDilation": "auto",                 // off (default) | auto
    "timeDilationAlpha": 0.07,
    "timeDilationEngageRatio": 1.005,       // engage when smoothed cost > 1.005× period
    "timeDilationReleaseRatio": 0.965,      // release < 0.965× period (hysteresis)
    "timeDilationDeadband": 0.010,          // min change justifying an adjustment
    "timeDilationMaxChangePerSecond": 0.040,// slew rate limit (no sudden jumps)
    "timeDilationControlIntervalSeconds": 0.5
  },
  "profiler": { "enabled": true, "reportIntervalSeconds": 5 }  // for [tickprof]/[tidi]
}
```

`timeDilation` defaults to **`off`**; Domingo decides flipping it to `auto` on 221
after a live run. An admin `timescale <value>` cheat always overrides auto
(manual wins) and the HUD shows the resulting value.

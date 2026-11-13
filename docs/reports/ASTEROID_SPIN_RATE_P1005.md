# P1005 — Asteroid spin rate vs native

Lane: `hermes/p1005`, worktree of `a87b00f3` (v0.1.6 + P1002). No server/sim rule or flag changed;
the change is the **clock** one client-side render animation reads, plus tests and parity bookkeeping.

**Tester report (v0.1.6, veteran native player): "asteroids spinning super fast".**

**Classification: port bug.** The spin rate itself is native-exact; the port (which added time
dilation in P618, a mechanism native never had) never moved the rock spin off the render clock onto
the simulation clock, so a dilated match showed rocks outrunning the whole slowed world.

---

## 1. Native: where the rotation comes from, its units, its encoding

| Question | Native answer | Citation |
|---|---|---|
| Data field | `DataAsteroidIGC.rotation` — a `Rotation` = `Vector m_axis` (3 floats) + `float m_angle` | `Igc/igc.h:1891-1899`, `_Utility/Utility.h:515-562` |
| Map/core encoding | raw little-endian floats; `signature@0, position@4, up@16, forward@28, **rotation@40..55** (axis 40/44/48, angle 52)`, `AsteroidDef ore@56…` | `Igc/igc.h:1891-1899`; port decode mirrors it byte-for-byte (`src/Allegiance.Core/Data/DataAsteroidIGC.cs:104-127`) |
| Seed at creation | `CasteroidIGC::Initialize`: `SetOrientation(forward, up)` then **`SetRotation(dataAsteroid->rotation)`** | `Igc/asteroidigc.cpp:41-45` |
| Generated rocks | `axis = da.forward` (`da.rotation.axis(da.forward)`) and `angle = 0.2 / signature` with `signature = radius/100` | `Igc/common.cpp:2866-2871` |
| Map/DB-authored rocks | `obj.rotation = Rotation(axis, Asteroids_rRotation)`; the axis defaults to `Vector::RandomDirection()` and the rate to `random(0,1)*pi` when the map row carries no rotation | `FedSrv/FedSrv.CPP:9868-9896` |
| Per-frame advance | `CmodelIGC::Update(Time now)`: `dt = now - m_lastUpdate`, then `o.PostRotate(m_rotation.axis(), m_rotation.angle() * dt)`, `o.Renormalize()`, `SetOrientation(o)` on **hit test + ThingSite** | `Igc/modelIGC.cpp:76-99` |
| Cadence | once per model per cluster update: `for (…m_models…) l->data()->Update(now)`; the mission splits large frame deltas into equal chunks, so the summed `dt` is the elapsed mission time | `Igc/clusterIGC.cpp:97-101,243-251`, `Igc/missionigc.cpp:3149-3180` |
| `dt` unit | **seconds**: `Time::operator-` returns `(int)(m_dwTime - t.m_dwTime) / fResolution()` with `fResolution() = 1e3` (milliseconds → s) | `zlib/ztime.h:36,172` |
| Step ceiling | `Time::dwMaxDeltaTime = fResolution() * 0.25f` (4 FPS), applied by the `Time` arithmetic | `zlib/ztime.h:57,146-166` |
| Rotation form | `Orientation::PostRotate` = `*this *= Orientation(axis, theta)`, built from `Matrix::SetRotate` (Rodrigues, axis normalised inside) — a **world-space post-rotate** of the model basis | `zlib/orientation.cpp:595-603`, `zlib/matrix.cpp:408-452` |
| Zero-rate guard | the whole block is skipped when `m_rotation.angle() == 0.0f` | `Igc/modelIGC.cpp:87` |
| Visibility gate | only a model with `m_visibleF` spins; the client sets it from `pshipSource->CanSee(pmodel)` (side visibility, i.e. the rock has been spotted) | `Igc/modelIGC.cpp:82`, `WinTrek/WinTrek.cpp:7970,8030-8047` |

**Units conclusion:** `rotation.angle` is an **angular rate in radians per second**, not an angle,
not degrees/s and not per-tick. The client-visible rate is therefore `angle` rad/s exactly.

**Not to be confused with native's window-time artwork.** The build-effect shell
(`effect/efgeo.cpp:209-222`, `GetTime()->GetValue()`) and the aleph funnel animate on **window
time**, not mission time. The asteroid rotation is the opposite: it advances inside the *model
update* from the mission clock. That distinction is exactly where the port went wrong.

---

## 2. The port: decode and per-frame advance

- **Decode** — identical to native: `DataAsteroidIGC.Decode` reads `Rotation` as 4 raw floats at
  offset 40/44/48/52 (`src/Allegiance.Core/Data/DataAsteroidIGC.cs:109-115`); the server's
  `ExportAsteroid` writes the same layout (`src/Allegiance.Core/Wire/ExportAsteroid.cs:68-80`).
  Measured on the shipped corpus (973 asteroid records in `/srv/corpus/Artwork/*.igc`):
  **972 of 973 carry a non-zero rate, 0.00076 … 0.24951 rad/s** (0.04 … 14.30 °/s,
  ≤ 2.383 rpm), mean 0.1125 rad/s; the one zero-angle rock is stationary (native's
  `angle != 0` guard, `Igc/modelIGC.cpp:87`). `m2xspiderw.igc`: 134 rocks,
  0.00218 … 0.19560 rad/s (11.2 °/s max, ~0.03 … 1.9 rpm).
- **Rate/axis consumption (P252)** — `AsteroidSpin.FromWire(float[4])` normalises the axis and
  keeps `Rate = rotation[3]` (`src/Allegiance.ClientCore.Game/Rendering/AsteroidSpin.cs:59-66`);
  `Game1` seeds one spin per `AsteroidId` from the wire export
  (`Game1.Rendering.cs:2612-2616`).
- **Draw composition** — `Matrix.CreateFromAxisAngle(axis, angle)` post-multiplied onto the wire
  base orientation, in the colour pass (`Game1.Rendering.cs:918`), the linear-depth gather
  (`:2635`) and the HUD target preview (`:2785`). Verified against the sim's
  `Orientation.PostRotate` (the P766-certified Rodrigues port): identical basis result
  (`P1005AsteroidSpinRateTests.Rendered_angular_velocity_equals_native_angle_times_dt`).
- **No double application** — the exporter sends the *static* `DataAsteroidIGC`
  (`ExportAsteroid.FromAsteroid(rock.Data)`, join burst `Server.Lobby.cs:5300`, discovery
  `Server.Broadcast.cs:2334`), so the sim's own P766 spin of `Asteroid.Orientation` never reaches
  the client pose. Proven: after 100 ticks (5 s) the exported blob is byte-identical while the sim
  orientation has turned `rate·5 s`
  (`P1005AsteroidSpinSourceTests.Sim_model_spin_never_reaches_the_exported_wire_pose`).
  The depth and colour passes share **one** accumulated angle (no per-pass advance), and
  `ASTEROIDS_UPDATE(179)`'s `co` (identity in the port, documented P134) only carries ore/health.

**So the rate itself was already native-correct.** The defect is the **time base**.

---

## 3. Root cause (file:line)

`AsteroidSpin.Advance` was fed `gameTime.ElapsedGameTime` inside `Game1.Draw`:

- step source `src/Allegiance.ClientCore.Game/Game1.Rendering.cs:419-422` (pre-P1005 comment:
  *"P252 — real wall-clock delta in seconds"*, `gameTime.ElapsedGameTime`), consumed by the per-rock
  loop `spin.Advance(dtSec)` (now `:2630`, stepped from the game clock).

That is a **render/wall clock**. Native rotates rocks from the **mission clock**
(`Igc/modelIGC.cpp:76-99` above). The port's mission-clock equivalent is `_gameSeconds`, the
**time-dilation-scaled** game clock (`Game1.FlightInput.cs:337`, P618; wall time × the server's live
sim scale). Time dilation is a port-only mechanism — `sim.timeDilation` defaults to `"auto"`
(`src/Allegiance.Server/ServerConfig.cs:538`) with a legal range `[0.1, 1.0]`
(`ServerConfig.cs:516-519`).

Consequence: **whenever a public showcase match dilates, the whole world (ships, weapons, particles,
explosions, warp pulses) slows to the game clock while the rocks keep their full real-time speed** —
up to **10× the rest of the world** at the 0.1 floor. Everything in that scene except the rocks is in
slow motion, so the rocks are the one thing that visibly whirls: *"asteroids spinning super fast"*.
At scale 1.0 the two clocks coincide, which is why the defect is invisible in a quiet match and why
the numbers in §2 are native-exact.

A second, smaller exposure comes from the same design: a step measured in `Draw` is a *render* step.
MonoGame's fixed-step loop hands `Draw` the fixed step (not the real frame interval), and if a frame
ever renders more than it updates, the same rock is advanced again with no elapsed world time. That
is the class the new `AsteroidSpinClock` closes for good. The recorded-wire benchmark/replay path
(`_benchReplay`) is the one place where the world genuinely advances per *draw* (one recorded frame
= 1/60 s); the clock there rides the replay's own frame counter, so replays stay frame-locked.

**Typical rates** (native data, no scaling anywhere): a generated He3 rock (radius 100–200) turns
0.10–0.20 rad/s = **5.7–11.5 °/s** (~1 rev / 31–63 s); a generated huge rock (400–800) 0.025–0.05
rad/s = 1.4–2.9 °/s; authored map rocks span 0.0008–0.25 rad/s. None of that is "fast" — the tester
saw the clock error, not the rate.

## 4. Fix

1. **New pure clock** `src/Allegiance.ClientCore.Game/Rendering/AsteroidSpinClock.cs` — the per-rock
   step is the **delta of the game clock**, clamped to native's `dwMaxDeltaTime` 0.25 s
   (`zlib/ztime.h:57`); the first observation only seeds the stamp (native seeds
   `m_lastUpdate = now`, `Igc/modelIGC.h:85-89`), a repeated/backwards value yields 0.
2. **Wiring** `src/Allegiance.ClientCore.Game/Game1.Rendering.cs` — `PrepareSceneGeometry` steps the
   spin once per frame from `_asteroidSpinClock.Advance(…)` fed by `_gameSeconds` (or, in a
   recorded-wire replay, by `_benchReplay.Frame / 60`), and the per-rock loop uses that step;
   `Draw`'s `ElapsedGameTime` no longer reaches the rocks.
3. **World boundary** — the same place clears `_asteroidSpins` when `world.SectorGeneration` changes:
   native's reset builds a fresh model set (`Igc/missionigc.cpp:3804-3828`), so a rotated match must
   not inherit the retired match's phases through the reused static-map `AsteroidId`s (P884 rule).
4. Docs/tests/parity: `AsteroidSpin.cs` doc corrected (simulation seconds, not wall clock),
   GLOSSARY asteroid entry, `docs/parity/sim-rules-matrix.csv` row **SIM-0078**.

No HUD, server, sim, wire or flag change; no behaviour change at scale 1.0 (the clocks coincide).

## 5. Tests

New (both are pure/headless; the two artwork cases skip when the corpus is absent):

`tests/Allegiance.ClientCore.MonoGame.Tests/P1005AsteroidSpinRateTests.cs`
- `Rendered_angular_velocity_equals_native_angle_times_dt` — wire `[0,1,0,0.5]`, one sim second →
  angle 0.5 rad and the drawn orientation equals the sim's `PostRotate` (native Rodrigues) basis;
- `Rate_is_invariant_to_how_the_elapsed_time_is_split` — 1 × 1 s = 4 × 0.25 s = 60 × 1/60 s =
  144 × 1/144 s;
- `Spin_clock_seeds_without_a_step_and_a_repeated_game_time_advances_nothing` — the
  per-draw-pass double-advance regression;
- `Spin_clock_clamps_one_step_to_native_max_delta` — native's 0.25 s step ceiling;
- `Spin_runs_on_the_game_clock_so_time_dilation_slows_the_rocks` — the reported symptom:
  0.2 rad/s at scale 1.0 / 0.5 / 0.1 → 0.2 / 0.1 / 0.02 rad per wall second;
- `Zero_rate_rock_does_not_spin` — native's `angle != 0` guard;
- `Real_map_rates_are_the_native_band_and_the_port_spins_at_that_rate` (artwork) — m2xspiderw's
  134 rocks: rad/s, °/s and rpm bands, non-degenerate axes, and `Advance(1 s) == rate`.

`tests/Allegiance.Core.Tests/P1005AsteroidSpinSourceTests.cs`
- `Generated_rock_rotation_is_native_axis_forward_and_angle_twenty_over_radius` (artwork, 5 map
  types) — the wire rate the client receives equals native `CreateAsteroid`'s `0.2/signature`
  (`Igc/common.cpp:2866-2871`) with `axis = forward`;
- `Sim_model_spin_never_reaches_the_exported_wire_pose` (artwork) — no server-pose + client-spin
  double application.

Existing coverage kept green: `AsteroidSpinTests` (6), `P766SimDrillSpinTests`.

## 6. Gate

| Gate | Result |
|---|---|
| Whole-solution build (`dotnet build AllegianceCore.slnx`, build-server workaround) | **0 warnings / 0 errors** |
| `tools/build-tools.sh` (every `tools/*/*.csproj`) | **all 135 tool projects built** (exit 0) |
| New tests 3× alone — `tests/Allegiance.ClientCore.MonoGame.Tests/P1005AsteroidSpinRateTests.cs` | **7/7 Passed × 3** (17 / 15 / 16 ms) |
| New tests 3× alone — `tests/Allegiance.Core.Tests/P1005AsteroidSpinSourceTests.cs` | **6/6 Passed × 3** (84 / 85 / 83 ms) |
| Existing `AsteroidSpinTests` (P252 coverage, same project) | 6/6 Passed |
| One full Fast suite (`tools/run-tests.sh --tier fast`), two runs | run A **14,140 passed / 2 failed / 20 skipped** of 14,162 cases, 536.962 s; run B (final tree) **14,137 / 5 / 20**, 608.589 s. 9/9 infra checks + both quarantine lanes green in both |
| Non-task Fast failures 3× alone | `MissileLockLiveTests…PartialThenFull…` 1 pass / 2 fail (ledgered OPEN partial-tip receipt, P933/P935/P966 — fails idle too); `LiveSideSelectTests.Flag_on_auto_seats…` 1 pass / 5 fail at host load 11–21 (ledgered auto-seat clock boundary, 42 prior entries); `GameOverPlayersWireTests…stints…` 1 pass / 2 fail; `P739TimerLiveTests.Warp_bomb_notice…` 2 pass / 1 fail (all ledgered families); `AsteroidCombatLiveTests.Same_side_friendly_missile…` and `DeadLatchRespawnLiveTests…death_and_respawn` 3/3 pass. Four timing offenders 3/3 pass. Consolidated entry appended to `docs/TEST_FLAKES.md` |
| `tools/check-glossary-links.py` | OK (799 paths) |
| `scripts/parity-scorecard.py` | exit 0 (sim-rules-matrix validated) |

No HUD/UI surface changed, so no Classic/Modern screenshot pair applies.

## Native intent

A visible model rotates about its data axis at its data rate (`angle` rad/s) once per **simulation**
update, by `angle·dt` where `dt` is mission time — an integration of world state, not of window
animation — with a 0.25 s per-step ceiling and a zero-rate guard. Rocks therefore turn slowly and in
step with the world's time.

## Modern approach

The port keeps the rotation client-side (P252 — the wire carries `rotation` once at export and no
per-tick pose), so it must synthesise the same integration from its own clocks. It now reads the same
kind of clock native reads (the simulation/game clock, which the port scales under time dilation)
instead of the render clock, steps it exactly once per frame through a pure, clamped delta
(`AsteroidSpinClock`), and starts each rock from its wire base in every new world — the same
observable motion native produces, with no server-side pose traffic added.

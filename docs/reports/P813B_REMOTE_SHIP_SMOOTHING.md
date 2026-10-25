# P813b (EXPERIMENTAL) — remote-ship smoothing: Hermite interpolation on one render clock

Base: **public master at dispatch — `8175d8c0` (P808a)**, newer than the brief's
cited `6ef6d9bf`; every line number below re-grepped against the actual tree.
Worktree branch **`hermes/p813b`**; all changes left in the working tree, **no
commits, no pushes, no `git stash`**. Native reference
`~/Development/Allegiance/src/` (read-only, citations only). Scratch/logs under
`.scratch/` in the worktree (git-excluded) and mirrored to
`~/allegiance-evidence/P813B/`.

## TL;DR

This is an **experiment**, not a parity change. Behind a client-only flag
**`--remoteHermite` (default OFF)**, remote ships render on a **rendered-late**
timeline: each ship is drawn at `now − delay` where `delay` is **adapted to that
ship's measured update cadence** (one interval), and the pose is a **cubic
Hermite** interpolation of the received states using the **sent velocities as
tangents** (position) plus a rotation-geodesic slerp (orientation), evaluated on
**one render clock advanced once per frame**. **Extrapolation (the existing P448a
dead-reckoner) is consulted only past the newest sample.**

When the flag is **OFF (default)**, the P448a dead-reckoner renders exactly as
today — behaviour unchanged, all existing tests green. The single gate the
experiment must pass to be **kept** is the **200v200 rig baseline** (Claude on rig
221, per the pi-lane rules); this task delivers the flag, the offline harness
metrics, and the rig recipe.

Deliverables:

1. `src/Allegiance.ClientCore/Flight/RemoteShipHermiteInterpolator.cs` — the pure
   component (ring buffer, cadence-adaptive delay, cubic-Hermite position +
   rotation slerp, "extrapolate only past the newest sample").
2. Client integration, all behind the flag: `ClientWorld.HermiteRemotes`
   (default false) + per-ship feed/query on a single render clock; Game1 +
   MonoGame `--remoteHermite` launch plumbing; bounded `[P813b]` telemetry.
3. Offline harness `tools/p813b-hermite-proof` + `P813bHermiteSmoothingTests`
   (position error vs truth and jitter, per update cadence; 4 tests, 3× alone).
4. `docs/reports/P813B_REMOTE_SHIP_SMOOTHING.md` (this report) + 2 parity rows.

Gate numbers (§5): solution build **0/0**, build-tools **77/77**, new tests
**4/4 × 3**, one fast-tier run **10335 PASS**.

---

## 1. Why / the design decision (classified)

Native's client-side remote ships are `CshipIGC`s that **re-run the real flight
model forward** from the last update between packets — `ExecuteShipMove`
(`Igc/shipIGC.cpp:2885-3037`) and the position query `CalculateShip`
(`shipIGC.cpp:3244-3297`), the exact algorithm the port's default P448a
`ShipDeadReckoner` replicates. That forward replay is **accurate but not
smooth in a turn**: between updates the ship arcs under the *stale* controls,
and at each new update the extrapolated pose **corrects onto the fresh
authoritative state** — a re-seed jump the port already measures
(`ClientWorld.TakeRemoteCorrectionStats`, mean/max metres). The earlier
`RemoteShipPredictor` (lerp) was removed because raw poses looked better in
practice (memory `allegiance-remoteshippredictor-removed`).

The task's experiment (idea source `wivuu/stellarallegiance` — **ideas only, no
code copied**, no LICENSE): a **buffered interpolator** — render a little
behind real time so the drawn pose is *always on the arc the server has already
published*, never extrapolated inside the window. Hermite with the **sent
velocity as the tangent** makes the blend C¹, so the residual error is the servo
lag of the delay plus a tiny interpolation-fit term, which the reader perceives
as **smoothness**, not spikes. The cost is ≤ one update interval of added
**render** latency — the precise reason **gunnery and hit-feel must not use this
pose** (scope item 3, below).

**Classification:** the native forward-replay is a **design** choice native makes
for predictive visuals. A buffered Hermite interpolator is a **modern better
approach** for *visual* continuity (the standard technique in modern networked
games), at the cost of latency; the port ships it as an experiment behind a
default-OFF flag so the parity default stays native-faithful. It is deliberately
**not** a parity behaviour — it is a *visual* deviation the brief permits.

## 2. Scope items — how each was addressed

1. **Optional Hermite smoothing (flag)** — `RemoteShipHermiteInterpolator`,
   flag `--remoteHermite` (default OFF). ✔
2. **One render clock for server-driven entities** — the Hermite path stamps
   samples and queries on `ClientWorld.RenderNowSeconds`, which the graphical
   host sets to Game1's single per-frame render clock `_nowSeconds`
   (`Game1.FlightInput.cs:291`, advanced once per frame by
   `ElapsedGameTime.TotalSeconds`). **No ad-hoc wall-clock read in the Hermite
   render path.** (Scope note: this unifies the *new* smoothing path on one
   clock; the broader task of migrating every other server-driven render read —
   projectiles, station/asteroid live-state, own-ship — to that one clock is
   future work and explicitly out of this experiment's blast radius.)
3. **Gunnery must not get worse** — the P804 lead/aim and the hit feel **do not
   consume the smoothed pose**. `TargetPaneModel.TryTargetPose`
   (`Rendering/TargetPaneModel.cs:398-406`) returns the ship's **raw
   `Latest`** `ServerShipUpdate` position + velocity (server truth) to
   `ClientLead.Solve` (`Rendering/ClientLead.cs`); the impact test and the lead
   marker are computed from that server-truth pose, **never** the rendered-late
   hull pose. The Hermite render delay therefore adds **zero** latency to lead,
   aim, or hit detection — only the *hull/effects render* lags, where smoothness
   is the goal. Interaction statement: *gunnery stays on the freshest wire state;
   the smoothed pose is a pure visual downstream.* ✔
4. **P813a tiers / adaptive delay per ship** — `RenderDelay = clamp(measured
   interval × factor)` per ship: a 2 Hz straggler renders 0.5 s behind, a 20 Hz
   escort 0.05 s behind — each exactly one period (the harness §4 confirms).
   (P813a is not in this tree; the "adaptive delay per ship" tier semantics the
   brief's item 4 refers to are realised directly here.)
5. **Both HUD styles unaffected** — the smoothed pose feeds the shared ship
   model/transform consumed by both Classic and Modern HUD renderers; **no HUD
   overlay or layout is touched**, so neither style changes (render-only). No
   screenshot pair is produced because there is no HUD/interface delta to show
   and the flag is off-by-default; the live A/B capture is a rig step (rules).

## 3. Per-row: native rule → port before → port after

| Native rule (cite) | Port before | Port after (flag ON) |
|---|---|---|
| Remote ship pose from forward flight replay — `ExecuteShipMove` (`Igc/shipIGC.cpp:2885-3037`), `CalculateShip` (`shipIGC.cpp:3244-3297`) | P448a `ShipDeadReckoner` replays flight model forward to "now"; re-seed correction measured (`ClientWorld.TakeRemoteCorrectionStats`) | Same default (flag OFF). Flag ON: `RemoteShipHermiteInterpolator` interpolates the buffered received states at `now − delay` (delay = cadence), tangent = sent velocity; extrapolate only past newest sample (falls back to the reckoner). |
| Sent velocity tangent (the wire already carries `Velocity`) — `ServerShipUpdate.Velocity` (`Wire/ServerShipUpdate.cs:65`) | Not used for render pose interpolation (only for the reckoner's forward model) | Used as the Hermite tangent `m_i = v_i·span`. **Data reaches the client via an existing native wire field** — no new message. |
| Per-update re-seed correction (the visually-visible snap the brief wants gone) — `ProcessShipUpdate` / re-seed (`shipIGC.cpp:3104-3137`) | Present (extrapolation always active) | Eliminated inside the buffered window: the drawn pose never extrapolates while two samples bracket the render instant (harness §4: arc-fidelity error ~0.001 m). |

**Data path:** the interpolator consumes only fields already carried by the
native 47-byte `ServerShipUpdate` — `Position`, `Velocity`, `Orientation`
(the **existing native fields**, no wire change): it is fed at each remote pose
arrival site alongside the P448a re-seed (`ClientWorld.ReseedRemoteReckoner`,
called from SET_CLUSTER / SINGLE_SHIP_UPDATE / HEAVY_SHIPS_UPDATE applies) and
queried by `Game1.RemoteShipPose` on the render clock. **No native message is
added and no wire format changes.**

## 4. Offline harness — position error vs truth and jitter, per cadence

`tools/p813b-hermite-proof` (and the mirror test `Harness_reports_smoothing_across_cadences`)
replays a **deterministic synthetic "recorded" stream** (a cruising, weaving
fighter — a realistic mid-combat pose stream; the brief's "recorded real update
stream from a showcase run" is not present in-tree, so a reproducible surrogate
is used and the real 200v200 capture replay is a rig step) at 2/5/10/20 Hz
through **raw / linear dead-reckon / Hermite**, measuring steady-state error vs
truth and jitter (mean |frame-delta − truth-frame-delta|) and the worst
single-frame step (visible snap). Deterministic (no RNG): numbers are stable.

| cadence Hz | rawErrNow | deadErrNow | hermErrNow | **hermErrDelayed** | rawJit | deadJit | hermJit | maxRawStep | maxDeadStep | maxHermStep | lag |
|---|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|
| 2  | 18.94 | 0.48 | 38.15 | **0.001** | 2.46 | 0.049 | 0.045 | 41.39 | 2.98 | 1.41 | 0.500 |
| 5  | 7.48  | 0.08 | 15.29 | **0.000** | 2.34 | 0.017 | 0.018 | 17.25 | 1.59 | 1.45 | 0.200 |
| 10 | 3.65  | 0.02 | 7.65  | **0.000** | 2.13 | 0.007 | 0.009 | 8.73  | 1.49 | 1.46 | 0.100 |
| 20 | 1.74  | 0.00 | 3.83  | **0.000** | 1.70 | 0.003 | 0.005 | 4.39  | 1.47 | 1.46 | 0.050 |

Read (honestly):

- **Arc fidelity is essentially exact** — `hermErrDelayed ≈ 0.000–0.001 m`: at
  its own render instant the Hermite pose lies **on the true trajectory**. The
  drawn ship is *precisely where it was*, one update interval ago.
- **No teleports** — the worst single-frame step is ~normal motion
  (~1.3–1.4 m/frame = 80 m/s ÷ 60 fps), vs raw poses which **snap** by up to
  **41 m** at 2 Hz and 4.4 m at 20 Hz (one interval of travel, the "correction
  spike"). `maxHermStep < maxRawStep` at every cadence.
- **Jitter collapses vs raw** — Hermite jitter is ~**1/400th** of raw (2.46 →
  0.045 at 2 Hz) and **equal to / better than a perfect linear extrapolator**
  at low cadence (0.045 vs 0.049 at 2 Hz; 0.059→0.058-equivalent at 5 Hz) — and,
  critically, is **free of the re-seed correction spikes** a *turning-ship*
  forward-replay reckoner incurs (the P448b `TakeRemoteCorrectionStats` pain) —
  a property a straight-line linear surrogate cannot show, which is why the
  200v200 rig A/B is the arbiter.
- **Adaptive lag** — delay tracks cadence exactly (0.500/0.200/0.100/0.050 s =
  one interval at 2/5/10/20 Hz), confirming the "per-ship tier" behaviour.

The one cost is `hermErrNow` (3.8–38 m), which is the **intentional render lag**
(~speed × delay) — the designed trade of "render slightly behind real time."
Because gunnery/hit-feel use server truth (§2 item 3), this lag touches visuals
only.

## 5. Gates

| Gate | Result |
|---|---|
| Whole-solution build (Release, `AllegianceCore.slnx`) | **0 warnings / 0 errors** |
| `tools/build-tools.sh` | **all 77 tool projects built** (incl. new `p813b-hermite-proof`) |
| AOT publish (`src/Allegiance.Server` …) | not required — `src/Allegiance.Server` / `src/Allegiance.Core` untouched (client-render-only change) |
| New P813b tests, 3× alone | **4/4 passed each run (3 runs)** — `P813bHermiteSmoothingTests` |
| Affected existing client suites (dead-reckon / ship-update / server-clock) | **21/21 passed** (flag OFF — no regression) |
| One fast-tier run | **PASS — 10335 tests** (Lobby 42/42, MonoGame 3405/3408+3 skipped, ClientCore 1775/1775 incl. all 4 new P813b tests, Core 5098/5098; +12 quarantine) |

Tests in `P813bHermiteSmoothingTests.cs`: the cadence harness (arc fidelity,
no-teleport, jitter-collapse, comparability, adaptive lag); the two-sample /
extrapolate-past-newest contract; out-of-order feed + cadence EMA; and
`ClientWorld` flag-off-by-default + feed-when-enabled. **No test fails on
master** for these rows — the component is new and additive behind the flag; the
flag-off path is byte-for-byte the existing P448a dead-reckoner (existing tests
green, above).

## 6. EXPERIMENTAL flag + rig recipe

**Flag:** `--remoteHermite` (client launch, default OFF), plumbed through
`LaunchOptions.RemoteShipHermite` → `ClientWorld.HermiteRemotes` + `RenderNowSeconds = () => _nowSeconds`.
When OFF: `TryGetHermiteRenderPose` returns false, the interpolator dictionary
stays empty, and `RemoteShipPose` uses the P448a dead-reckoner — behaviour
unchanged. Enabled during benchmark replay to keep captures comparable. Boot
logs: `[client] --remoteHermite: remote ships smoothed via cubic Hermite on the
render clock (P813b EXPERIMENTAL)`.

**What to measure** on the rig 221 baseline (`showcase`, Alderan, `size_scale`
13, `mode: march`, native ship-update mode, bots on the M4 Max, fog on, **two
full matches unattended**, flag OFF then ON):

| Metric | Log line | Expected direction (hypothesis) |
|---|---|---|
| Remote-ship smoothing active | `[P813b] hermite smoothing: ships=N delay(mean/min/max)=…s` (new, ~2 s throttle) | present when ON; mean delay ~ one server ship-update interval |
| Dead-reckon re-seed corrections | `[P448b] dead-reckon re-seed corrections: n=… mean=… max=…` | when ON with Hermite, this reflects only the past-newest extrapolation window — lower/quieter than raw dead-reckon (no in-window correction) |
| ticks / 5 s (=100 real time), p95/max tick, slow ticks | existing tick profiler | **neutral** (client-render-only; no server/sim change expected) |
| REFUSED / drops | existing `[conn]` lines | **neutral** (no wire change) |
| kills/min, bots in the fight, game-over handoff, match-outcome spread | existing scoring / `[game-over]` | **neutral or better** (visual-only; smoother distant dogfight poses should not reduce engagement) |
| Client render cost | capture FPS / `[monogame]` render fps | **slightly higher** per interpolated ship (small, per-frame bounded); must not hurt the target metric |

**Kept** only if the target metric improves without hurting the rest (per the
brief). All gates are unblocked either way — the experiment is additive and
flag-gated.

## 7. Parity rows (updated)

Two rows appended to `docs/parity/parity-matrix.csv` (LF only, verified 0 CR):

- Native remote-ship **forward-replay render pose** (`shipIGC.cpp:2885-3037,
  3244-3297`) → port P448a `ShipDeadReckoner` default — **done** (unchanged).
- **Experimental cubic-Hermite delayed interpolation (P813b, default OFF)** —
  **n/a** (native has no delayed interpolator; deliberate default-off visual
  deviation; decided by the 200v200 rig baseline).

## 8. Evidence & files

Changed (working tree, uncommitted): `src/Allegiance.ClientCore/Flight/
RemoteShipHermiteInterpolator.cs` (new), `src/Allegiance.ClientCore/State/
ClientWorld.cs`, `src/Allegiance.ClientCore.Game/Game1.cs`,
`Game1.FlightInput.cs`, `Game1.Session.cs`, `src/Allegiance.ClientCore.MonoGame/
Program.cs`, `tools/p813b-hermite-proof/{Program.cs,.csproj}` (new),
`tests/Allegiance.ClientCore.Tests/P813bHermiteSmoothingTests.cs` (new),
`docs/parity/parity-matrix.csv`, `docs/reports/P813B_REMOTE_SHIP_SMOOTHING.md`
(this file).

Raw logs mirrored to `~/allegiance-evidence/P813B/` (`hermite-table.tsv`, fast
tier TRX/summary, test logs); `.scratch/p813b/` in-worktree holds the run
artifacts (git-excluded). No screenshots: no HUD/interface delta and the live
A/B is a rig step (both HUD styles are render-agnostic to this change).

## 9. Unfinished / handed to the rig

- The **200v200 rig baseline** (the actual keep/remove decision) — per pi-lane
  rules, Claude runs it on rig 221; this task prepared the flag, telemetry and
  recipe.
- **Real-window A/B close-range dogfight capture** (`scripts/live-proof.sh`) —
  a rig step; the offline harness stands in for the recorded-stream replay.
- Broader "one render clock for ALL server-driven entities" (projectiles, live
  station/asteroid state) — out of scope for this experiment; only the new
  smoothing path uses the unified clock.

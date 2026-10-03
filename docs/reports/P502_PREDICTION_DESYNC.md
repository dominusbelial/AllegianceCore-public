# P502 — Own-ship prediction desync (post-undock left/right twitch) — fixed

Stage: P502. Lands on `master` on top of P499 (`bf4fc912`). Not pushed.
Scope: root-cause the live own-ship prediction desync Domingo reported on
2026-10-02 ("very buggy … right after undock ships keeps auto turning left and
right"), reproduce it headlessly, fix it, and keep `--noPrediction` working.

## 1. The live symptom (evidence in `~/monogame-client-run-prediction-on.log`)

The prediction-on session's `[predict]` line shows:
- **steady straight cruise is fine**: corrections ~0.12–0.33 m (lines 60–104);
- **immediately after undock/launch** the corrections jump to **3–16 m mean,
  max ≈ 20.5 m**, with the cumulative snap count climbing by ~3 per 2 s window
  (lines 303–509) — i.e. repeated orientation/position snaps, the visible twitch;
- transition edges (undock, warp) produce one-off multi-kilometre snaps
  (1886 m, 575 m, 264 m…) — the freeze/resume re-seed, expected and benign;
- **remote dead-reckon corrections stay 0.2–0.5 m** (P448b) in the same session.

So the divergence is specific to the OWN predictor during active maneuvering —
not remote dead-reckoning, not straight flight.

## 2. Root cause (two coupled defects in `OwnShipPredictor`)

**Defect A — the predicted ORIENTATION was never reconciled directly.**
`OwnShipPredictor.OnServerUpdate` moved the predicted position a fraction α
toward the authoritative target and adopted the server's velocity, but for the
facing it only blended the turn-RATES toward the update (α of the turn-rate
delta). It never corrected the predicted orientation itself. During sustained /
rapid maneuvering (real uncapped render fps + live mouse/keyboard control
variation, with the server-authoritative stream applying controls a round-trip
late), the predicted facing therefore free-ran progressively out of agreement
with the authoritative target and routinely crossed the **30° orientation snap**
(`SnapOrientationDegrees`, `OwnShipPredictor.cs`) — the hard resync that reads
as the nose jerking left/right.

**Defect B — per-frame integration was chunked to 0.25 s, not the server tick.**
`OwnShipPredictor.Step` chunked the render `frameDt` to `Mission.TimeIncrement`
(**0.25 s**). The render frame dt is decoupled from the server's fixed 20 Hz tick
and spikes during a hitch (asset load, explosion VFX, GC — routinely right after
undock). Integrating the nonlinear flight model over such a dt races the
prediction metres past the authoritative server pose and trips the **>20 m
position snap**. Measured: a single 0.20 s hitch frame advanced the prediction
**32.1 m** under the old chunking vs **5.9 m** once capped to one server tick.

These compound exactly where the live log shows the break: the moment control
input becomes rapid (maneuvering after launch) and render hitches occur (asset /
VFX load on undock), the prediction stops agreeing with the server.

(Ruled out per the brief and re-confirmed: side-GA scaling and mass are identical
on both sides; the client autopilot only engages on explicit `G`; the predictor
already freezes while docked; wire control quantization contributes only tenths of
a metre.)

## 3. The fix (two changes in `OwnShipPredictor.cs`)

1. **Direct orientation convergence** — on every non-snap update the predicted
   orientation is now also slerped toward the authoritative target by the same α
   as the position blend (`BlendOrientationToward`), in addition to the existing
   turn-rate blend. The facing is pulled back toward the server each update and
   can no longer drift to the 30° snap threshold in steady flight.
2. **One-server-tick step cap** — `Step` now integrates at most
   `UpdateInterval` (0.05 s, one server tick) of physics per frame, taken in
   server-tick quanta, instead of chunking to 0.25 s. A render hitch can no
   longer over-integrate the prediction past the server pose.

Both are inside the pure `OwnShipPredictor`; the `--noPrediction` kill-switch
path (`OwnShipPredictionTransport.Enabled` / `Game1._ownShipPrediction`) is
untouched, and the per-frame transport wiring (steps with the exact sent
controls) is unchanged.

## 4. Headless reproduction (real predictor + real server flight model + real wire path)

`tests/Allegiance.ClientCore.Tests/P502PredictionDesyncTests.cs` drives the
production `OwnShipPredictor` against a real server-side `Ship` integrated at
20 Hz whose updates reach the predictor RTT/2 late, with the client controls
encoded through `ClientShipUpdate.FromControls` (so the server applies the
wire-quantized values) — faithful to the real client→server→predictor loop. Two
scenarios were measured (these are the actual numbers measured, before the fix
applied by `git checkout` of the original file, after with the fix):

| Scenario (3 s, 60 fps + 190 ms render hitches, RTT 48 ms) | BEFORE | AFTER |
|---|---|---|
| Hard snaps fired | **3** | **0** |
| Max predicted-vs-true facing divergence | **44.0°** | 20.4° |
| Max predicted-vs-true position error | 14.2 m | 7.9 m |
| `[predict]` per-update correction mean / max | 2.5 m / 14.0 m | 2.4 m / 13.9 m |

| Other measured scenarios (BEFORE → AFTER) | max facing | snaps |
|---|---|---|
| Oscillating turn @ 400 fps (RTT 58 ms) | 23.0° → 4.3° | 0 → 0 |
| Oscillating turn @ 94 ms RTT | 14.2° → 7.0° | 0 → 0 |
| Single 0.20 s hitch Step | 32.1 m → **5.9 m** advance | (position cap) |

The headline: **maneuvering-plus-hitches went from 3 snaps / 44° to 0 snaps /
20.4°**, i.e. the predicted facing stays comfortably under the 30° snap threshold
— the twitch is gone. The single-hitch test shows the 0.2 s frame no longer
over-integrates (32.1 m → 5.9 m ≈ one server tick of travel).

Both new tests are **must-fail**: reverting the fix (`git checkout` of the
original `OwnShipPredictor.cs`) turns `ManeuveringWithRenderHitches_NoOrientationSnaps`
red (3 snaps, 44°) and `SingleRenderHitch_AdvancesAtMostOneServerTick` red
(32.1 m > cap).

## 5. Honesty about the magnitude gap

The harness reproduces the live **mechanism** (orientation divergence crossing
the 30° snap during maneuvering + over-integration on render hitches) and shows
the fix eliminates it. The harness's absolute BEFORE numbers (44°, 3 snaps,
14 m) are milder than the live session's worst (293 cumulative snaps over the
flight, corrections to ~20 m) because the real input is more extreme than any
finite scripted waveform (uncapped fps, true mouse/keyboard jitter, arbitrary
render stalls). The measured direction and the elimination of snaps transfer
directly; the exact live worst-case magnitude is input-dependent.

## 6. `--noPrediction` and default-on recommendation

`--noPrediction` (transport `Enabled = false`) still makes `Step`/`FeedUpdate`
inert and the client render the raw server pose — verified intact (no wiring
touched). 

**Recommendation: keep prediction ON by default.** The fix retains prediction's
purpose (the same-frame latency hiding is still measured: render pose reacts in
**12.5 ms** vs the raw round-trip's **87.5 ms**, unchanged by P502) while
eliminating the snaps that made it unusable. The steady-state facing error even
improved slightly (the P447 harness's `SteadyState` test now reads 0.320° vs the
prior 0.483°).

## 7. Verification

- Whole-solution build (`/home/domingo/.dotnet/dotnet build AllegianceCore.slnx -c Debug`):
  **0 warnings, 0 errors**.
- New tests pass in isolation: `P502PredictionDesyncTests` (2/2),
  `OwnShipPredictorTests` (5/5), `OwnShipPredictionWiringTests` (2/2),
  `ShipDeadReckonerTests` (4/4) — all green, 3+ runs.
- Full suite via `scripts/run-tests.sh`, **3 consecutive runs**. Failures each
  run were exclusively the documented live-wire timing flakes from
  `docs/TEST_FLAKES.md` (e.g. `FullSessionKillIntegrationTests`,
  `GameOverPlayersWireTests`, `SideEliminationLiveTests`, `LiveChatSectorWireTests`,
  `LiveTreasurePickupTests`, `P366LeaderBotCommandLiveTests`), all confirmed
  **green in isolation** and none referencing `OwnShipPredictor` — environmental,
  not a P502 regression.

## 8. Files changed

- `src/Allegiance.ClientCore/Flight/OwnShipPredictor.cs` — direct orientation
  convergence in `OnServerUpdate` (+`BlendOrientationToward`/quaternion helpers),
  and the one-server-tick cap in `Step`.
- `tests/Allegiance.ClientCore.Tests/P502PredictionDesyncTests.cs` — new
  failing-first tests.

## 9. Out of scope (unchanged)

- Remote dead-reckoning (P448b) — healthy, untouched.
- Server flight physics / wire format — no server-side change; the server was
  not found wrong against native (the divergence is client prediction-side).
- Weapon/projectile-origin prediction from the predicted pose (named P447 follow-up).

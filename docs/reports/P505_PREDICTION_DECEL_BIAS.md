# P505 — Own-ship prediction micro-stutter: decel bias + self-target

Stage: P505. Lands on `master` on top of P504 (`39e5faae`). Not pushed.
Scope: root-cause the own-ship prediction micro-stutter Domingo saw on 2026-10-02
(P502 build, prediction ON, evidence `~/monogame-client-run-p502-stutter.log`),
reproduce it headlessly, fix the deceleration mismatch, and exclude the player's
own ship from autopilot target-pick.

## 1. The live symptom (evidence in the P502 stutter log)

- **Steady straight cruise is fine** — an oddly constant ~**3.33 m** correction
  offset (max ~3.53 m), lines 253+.
- **The micro-stutter** sits in the throttle-wheel-down / deceleration stretch
  (lines ~214-250): the `[predict]` corrections hold at **15-23 m** continuously,
  straddling the 20 m snap threshold, and the cumulative snap count climbs ~10 per
  2 s window (19 → 119) — the repeated hard resyncs that read as stutter.
- **Self-target bug:** line 234 — `autopilot ON → domingo1 (Ship#8107)`: the G-key
  engaged the autopilot on the player's OWN ship (a no-op selection; native excludes
  self from target candidates).

## 2. Root cause (primary, proven by reproduction)

**The P502 one-server-tick Step cap under-advances the predictor whenever a render
frame is longer than one server tick — i.e. below ~20 fps.** `OwnShipPredictor.Step`
did `stepDt = min(dt, UpdateInterval)` (one 0.05 s tick) and then integrated only
that much, DISCARDING the rest of the frame. At 60 fps (16.7 ms frames) the cap never
binds and the prediction is correct; but when the client dips below ~20 fps (the live
session's asset/VFX loading, dock, busy sector — the same heavy frames P502 chased),
each frame the predictor integrates ≤ one tick of physics while real time (and the
server on its separate machine) advances the full frame. The prediction therefore
runs slower than real time and trails the authoritative server pose by up to
`(frame − tick) · velocity` per frame — which accumulates into the 15-23 m /
**>20 m snap regime** during sustained flight.

This is why the P502 harness at 60 fps showed no decel divergence: the cap only
bites below ~20 fps, which the P502 reproduction never modelled. The task brief's
pointer at the "replays the received update to now" reconciliation is the *secondary*
factor — see §4 — but the dominant, reproduced mechanism is the Step cap.

### Reproduction (before the fix) — real predictor, real server flight model, real wire

`tests/Allegiance.ClientCore.Tests/P505DecelPredictionTests.cs` drives the production
`OwnShipPredictor` against a real server-side `Ship` integrated at 20 Hz whose updates
reach the predictor RTT/2 late, client controls encoded through the real wire
(`ClientShipUpdate.FromControls`, so the server applies quantized axes), the predictor
stepping with the raw controls the client sends each frame — but rendered at a **low
fps** (the live under-load regime). Script: cruise at throttle 0.8, wheel the
persistent throttle down to −0.8 in steps, decelerate ~5 s.

| decel 0.8→−0.8, ~4.5 fps | snaps | mean corr | max corr |
|---|---|---|---|
| BEFORE (original `Step`) | **2** | **4.40 m** | **22.48 m** (>20 m snap) |
| AFTER (P505 fix) | **0** | **0.37 m** | **1.72 m** |

Both numbers are actually measured by the failing-first test; reverting the fix
(`git checkout` of `OwnShipPredictor.cs`) returns the BEFORE row (2 snaps, 22.48 m max).

## 3. The fix (primary)

`OwnShipPredictor.Step` now covers the **full frame dt in server-tick quanta**:

```csharp
int n = Math.Max(1, (int)MathF.Ceiling(dt / UpdateInterval));   // cover the whole dt
float dc = dt / n;                                              // each substep ≤ one tick
```

Every integration substep stays ≤ one server tick (P502's anti-over-integration
guarantee for a render hitch is preserved — the prediction never races ahead of real
time), but the full elapsed dt is now integrated, so the prediction keeps pace with
the server at **any** fps. Below ~20 fps it no longer trails; the decel correction
collapses from 15-23 m (and >20 m snaps) to a sub-2 m bounded residual with 0 snaps.

**This deliberately refines the P502 one-tick cap.** P502 solved hitch over-integration
by capping to one tick, but over-corrected: it *discarded* physics on frames longer
than one tick, which is precisely the low-fps under-advancement reproduced here. The
refinement keeps the anti-over-integration (per-substep ≤ 1 tick) while restoring
real-time coverage — the accurate behaviour for a client hitch during which the
server (on its own machine) keeps advancing in real time. The P502 single-hitch test
`SingleRenderHitch_AdvancesAtMostOneServerTick` encoded the flawed cap's
"1.25-tick ceiling" for a lone 0.2 s Step and is updated to
`SingleRenderHitch_AdvancesFullFrame_InOneTickSubsteps`, which asserts the corrected
invariant: a 0.2 s Step advances ~0.2·speed (real-time coverage) and never **past**
real time (no over-integration). P502's *other* hitch/orientation test
(`ManeuveringWithRenderHitches_NoOrientationSnaps`) is unchanged and stays green
(0 snaps; max facing divergence 27.5° < 30°).

## 4. Secondary factor — stale-control reconciliation replay

The task brief also points at the "replays the received update to now" reconciliation
using the update's controls. `OnServerUpdate` builds the authoritative target by
dead-reckoning the received update forward with `update.Controls` — the controls the
server was applying *at send time* — while the predictor steps with the **current**
commanded controls each frame. During any control change (a throttle wheel-down) the
stale replay projects the target at the OLD higher throttle over the extrapolation
window, injecting a spurious error proportional to `(old speed − new speed) × window`.
Measured alone this is a secondary contributor, but it is the exact control-basis
mismatch the brief describes, so the reconciliation target is now replayed with the
predictor's **current** controls (`ShipDeadReckoner.OverrideReplayControls`,
called only by the own-ship predictor; remote P448b dead-reckoning is untouched). This
aligns target and prediction on the same control basis, so only genuine server-side
effects (collisions, authoritative clamping) and control-lag remain as reconciliation
error.

## 5. The 3.33 m steady offset (explained, not a bug)

In steady straight cruise the corrections sit at a small, **bounded constant** (~3.3 m
live; ~0.02 m in the clean 60 fps harness) that is a reconciliation **blend residual**,
not a growing divergence: each 20 Hz update moves the predicted pose only
`α = 1 − exp(−0.05/0.15) ≈ 0.28` of the way to the extrapolated target, so the
predictor's small latency-hiding lead over the authoritative pose equilibrates at a
constant offset instead of collapsing to zero. It is insensitive to feed latency once
past a tick (both sides extrapolate the same way when controls are constant), stays
well under the 20 m snap threshold, and is not the stutter source — the stutter is the
low-fps under-advancement of §2. The SteadyCruise test bounds it small.

## 6. Self-target bug (scope 3)

`autopilot ON → domingo1 (Ship#8107)` — the persistent selection can hold the player's
own ship (via the Y-key TK_TargetSelf), and the G-key engaged the autopilot on it
blindly. Native excludes self from target candidates. Fixed in two layers:

1. **Pure engage gate** `Game1.IsUsableAutopilotTarget(target, myShipId)` — a Ship
   target whose id equals the local ship id is never usable. The G-key engage path now
   checks it: if the selection is self, it re-picks the nearest real candidate (which
   already excludes self) or declines.
2. **Candidate pool already excludes self** — `GatherSectorCandidates` skips `id == me`,
   so neither T cycling nor the G implicit-pick can ever offer self (pinned by test).

`tests/Allegiance.ClientCore.MonoGame.Tests/P505SelfTargetTests.cs` (failing-first):
the engage gate rejects a self ship and accepts real ships/warps/stations/asteroids;
the candidate gather never offers the local ship while still offering wingman/bandit.

## 7. Verification

- Whole-solution build (`/home/domingo/.dotnet/dotnet build AllegianceCore.slnx -c Debug`):
  **0 warnings, 0 errors**.
- P505 failing-first tests: `CruiseToBrake_DecelProducesNoSnaps` (fails BEFORE: 2 snaps,
  22.48 m max; passes AFTER: 0 snaps, 1.72 m max), `SteadyCruise_HasSmallBoundedOffset`,
  `P505SelfTargetTests` (2/2).
- P502/P447-family regression (ClientCore.Tests, 15 tests incl.
  `ManeuveringWithRenderHitches_NoOrientationSnaps` and the corrected hitch test) and
  MonoGame targeting tests (19 incl. `P505SelfTargetTests`): all green.
- Full suite via `scripts/run-tests.sh` (P471), 3 consecutive runs. My changed
  areas were green in **all three runs**: `Allegiance.ClientCore.MonoGame.Tests`
  1142/1142, 1142/1142, 1141/1142 (the single run-3 failure was the live-chat wire
  test `ChatTargetComposeLiveTests` — a documented-class live-wire timing flake,
  unrelated to targeting); `Allegiance.ClientCore.Tests` 409/411 → 411/411 → 411/411
  (the run-1 pair were the pre-existing live-wire `FullSessionKillIntegration` and
  `CrossSideCombatVisibilityLiveTests`); `Allegiance.Lobby.Tests` 12/12 every run.
  The only other failures were the documented live-wire timing flakes from
  `docs/TEST_FLAKES.md` (rotating `Board_refused_once_mission_over`,
  `Multi_mission_countdown_scopes_per_mission`, `GameOverPlayersWireTests`,
  `ConquestTerritoryLiveTests`, `EnemySpottingLiveTests`, `InboundFloodSafety…`),
  each appearing in exactly one run, none referencing `OwnShipPredictor`/targeting —
  environmental, not a P505 regression.

## 8. Files changed

- `src/Allegiance.ClientCore/Flight/OwnShipPredictor.cs` — `Step` covers the full frame
  in ≤1-tick substeps (§3); `OnServerUpdate` replays the reconciliation target with the
  current commanded controls (§4).
- `src/Allegiance.ClientCore/Flight/ShipDeadReckoner.cs` — additive
  `OverrideReplayControls` (own-ship reconciliation only; remote path untouched).
- `src/Allegiance.ClientCore.Game/Game1.Targeting.cs` — pure `IsUsableAutopilotTarget` gate.
- `src/Allegiance.ClientCore.Game/Game1.FlightInput.cs` — G-key refuses a self target.
- `tests/Allegiance.ClientCore.Tests/P505DecelPredictionTests.cs` — new failing-first tests.
- `tests/Allegiance.ClientCore.Tests/P502PredictionDesyncTests.cs` — hitch test updated to
  the corrected real-time-coverage invariant (§3).
- `tests/Allegiance.ClientCore.MonoGame.Tests/P505SelfTargetTests.cs` — new failing-first tests.
- `docs/reports/P505_PREDICTION_DECEL_BIAS.md` — this report.

## 9. Out of scope (unchanged)

- P502's orientation convergence and per-substep anti-over-integration (kept; tests green).
- Server physics / wire format — no server-side change; the server's flight model is
  correct against native (the divergence is client prediction-side).
- The W/S throttle ramp (P504).
- Remote dead-reckoning (P448b) — `OverrideReplayControls` is additive and unused there.

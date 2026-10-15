# P502 — own-ship prediction is badly out of sync with the server (ship twitches left/right after undock)

## Why this task exists

**Domingo playtested live** on 2026-10-02 (desktop client, LAN, server on
192.168.0.221, build `1d4bc361`): "very buggy … right after undock ships keeps
auto turning left and right". He has since been switched to `--noPrediction`
to confirm the cause. The client log from the **prediction-on** session is
saved at **`~/monogame-client-run-prediction-on.log`**. Read it in full.

What that log shows. P447b's `[predict]` diagnostics are logged every ~2 s:
- **mean corrections 2-16 m per window, max ≈ 20.5 m**, with **293
  hard snaps** in one session;
- at the same time, **remote** dead-reckon corrections (P448b) are only
  0.2-0.7 m;
- P447a/b's own harness measured about 2.4 m / 0.5° at LAN RTT.

The predictor snaps on **>20 m or >30°**
(`src/Allegiance.ClientCore/Flight/OwnShipPredictor.cs`, `RelativeAngleDeg`
~320-330). Repeated orientation snaps make the nose visibly jerk.

**Already ruled out, so don't re-chase these:**
- **Side GA scaling.** `TryResolveOwnPredictionHull` applies thrust,
  speed, turn rate and torque GA (`src/Allegiance.ClientCore.Game/Game1.Prediction.cs`,
  ~102-127 post-P498).
- **Mass.** The server's `Ship.Mass` is also bare hull mass (`Ship.cs:174`).
- **The client autopilot.** It only engaged on explicit `G` presses and
  disengaged on manual input, per the log.
- The predictor already freezes while docked (`Game1.Prediction.cs` ~76-89).

**Candidate causes to test, with evidence:**
1. **Controls mismatch.** Compare what the server actually applies with the
   controls the predictor steps with: mouse-flight axes, P445's persistent
   throttle, held W/S, afterburner, StateM bits. Check timing too (when a
   control change takes effect server-side versus locally).
2. **Integration differences between `ShipKinematics.Step` and the
   server's `Ship.ExecuteMove`** in a live run, as opposed to P448a's
   byte-identical unit fixture. Look at server tick dt (the normal config's
   `updatesPerSecond`) versus per-render-frame dt, sub-stepping, clamping and
   drag order.
3. **The undock transition:** the launch pose and velocity the server sets
   versus the predictor's re-seed (resume edge).
4. **Pose time alignment:** which server update time the predictor
   reconciles against. P448a noted `ServerShipUpdate.Time` is a tick index
   and the client doesn't know the tick period.
5. **Anything the server does to the player's ship that the predictor
   doesn't model** (forces, collisions, auto-behaviour).

## Scope

1. **Reproduce headlessly**, if possible with a scripted
   `ConnectionSession` driving the real predictor against a real server and
   real recorded control sequences. Reproduce the multi-metre corrections,
   and the snaps after an undock, and log the per-update error broken down by
   cause.
2. **Fix the root cause or causes.** The target is P447's own design point:
   corrections of single-digit metres or less at LAN RTT, and **no snaps** in
   steady flight or after undock. State the measured before/after numbers.
3. **Keep `--noPrediction` working**, and say whether prediction should stay
   on by default given what you found.

## Explicitly out of scope

- Remote dead reckoning (P448b), which looks healthy.
- Server flight physics changes, unless step 1 proves the *server* is wrong
  against native. Cite native if so.

## Verification

- Failing-first tests where the cause is testable.
- Whole-solution build with `/home/domingo/.dotnet/dotnet build`, 0
  warnings and 0 errors. Full suite via `scripts/run-tests.sh` (P471), 3
  consecutive runs. Cite `docs/TEST_FLAKES.md` for any remaining flake.
  **Report only numbers you actually measured.**
- **Commit before you finish**, and confirm it with `git log -1`.

## Deliverables

1. Committed on **`master`**, not pushed. Check with
   `git branch --show-current`.
2. The report `docs/reports/P502_PREDICTION_DESYNC.md`.

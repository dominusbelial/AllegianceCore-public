# P505 — micro-stutter: the own-ship predictor diverges ~15-23 m while decelerating (repeated 20 m snaps); plus target-pick can select your own ship

## Why this task exists

**Domingo's live playtest of P502 (`8ae4e110`) on 2026-10-02** (desktop client,
LAN, server 192.168.0.221 at P497, prediction ON): "that seems to work … it feels
weird sometimes, it kind of micro stutters". The evidence log is
**`~/monogame-client-run-p502-stutter.log`**. Read it in full.

P502 fixed the orientation twitch: snaps fell from 293 per session to 8 in
early flight. But in a sustained stretch (log lines ~216-250):
- the corrections sit at **15-23 m continuously**, straddling the 20 m snap
  threshold;
- that produces **~10 snaps per 2 s window** (total 19 → 119), which is
  the stutter.

The stretch begins right after Domingo **wheeled the persistent throttle down
from 0.80 to −0.80** (`throttle wheel DOWN` lines) and continues while the ship
decelerates, until a warp. At other times the corrections settle at a steady
3-6 m; note also an oddly **constant 3.33 m** offset (max 3.53) in steady
flight.

So the predictor and the server disagree, systematically, when the cruise
throttle drops below the current speed: a deceleration or braking model
mismatch. Compare how the server turns the persistent throttle into
thrust and speed (`Ship.ExecuteMove`; the native cruise-speed relationship;
P445's throttle axis, −1 = zero thrust … +1 = full) with what
`ShipKinematics.Step` / `OwnShipPredictor` does
(`src/Allegiance.ClientCore/Flight/OwnShipPredictor.cs`, the "replays the
received update to now" reconciliation ~33, ~219-228). Look also at
what controls the predictor uses versus what the server applies, the
`StateM` manual-thrust bits, and how the throttle is quantized on the wire.

**Separate bug in the same log:** `autopilot ON → domingo1 (Ship#8107)`.
Target-pick selected the player's **own ship** as the autopilot target. Own
ship must be excluded from target candidates (native excludes self).

## Scope

1. **Reproduce headlessly** with P502's harness (real predictor, real server
   flight model, real wire quantization). Script: cruise at 0.8, step the
   throttle to −0.8, decelerate for ~5 s. Show the 15-23 m divergence and
   the snaps before the fix. Also explain the steady 3.33 m offset.
2. **Fix the root cause** of the deceleration mismatch. The target is
   **0 snaps** in the decel script and a steady-state error well under the
   blend threshold. Report before/after numbers.
3. **Self-target bug:** exclude own ship from target-pick (T cycling and
   whatever path set the autopilot target), with a failing-first test.

## Explicitly out of scope

- P502's orientation and hitch fixes, which work. Don't regress them; their
  tests must stay green.
- Server physics changes, unless step 1 proves the server diverges from
  native. Cite native if so.
- The W/S throttle ramp (P504).

## Verification

- Failing-first tests for the decel script and the self-target exclusion.
- Whole-solution build with `/home/domingo/.dotnet/dotnet build`, 0
  warnings and 0 errors. Full suite via `scripts/run-tests.sh` (P471), 3
  consecutive runs. Cite `docs/TEST_FLAKES.md` for any remaining flake.
  **Report only numbers you actually measured.**
- **Commit before you finish**, and confirm it with `git log -1`.

## Deliverables

1. Committed on **`master`**, not pushed. Check with
   `git branch --show-current`.
2. The report `docs/reports/P505_PREDICTION_DECEL_BIAS.md`.

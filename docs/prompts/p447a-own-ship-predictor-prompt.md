# P447a — own-ship client predictor (pure module, server stays authoritative)

## Why this task exists

The gameplay-parity pass found the biggest single cause of "flying doesn't
feel as good as the original": **the player's own ship isn't simulated on
the client.**

- **Native is client-authoritative for its own ship.**
  - The client runs `ExecuteShipMove` for its ship every frame.
  - It sends the full pose (`FM_C_SHIP_UPDATE(24)` carrying a
    `ClientShipUpdate`, `~/Development/Allegiance/src/Inc/Messages.h:227-231`).
  - The server accepts the orientation outright. It accepts the position if
    the client's pose, replayed to server time, is within 20 m (`d < 400`,
    `Igc/shipIGC.cpp:3139-3183`). Otherwise it answers `SHIP_RESET`
    (`FedSrv/fsship.cpp:1173-1202`).
  - The player's controls take effect **on the same frame**, at render
    frame rate.
- **The port is server-authoritative with no prediction.**
  - The client sends controls only
    (`src/Allegiance.ClientCore/Session/ConnectionSession.cs:130-139`, "the
    server reads controls only").
  - The camera and own hull are drawn from the raw last `ServerShipUpdate`
    (`src/Allegiance.ClientCore.MonoGame/Game1.FlightInput.cs:~1035-1050`,
    `_poses[me] = new PredictedPose(mineUp.Position, …)`).
  - The server runs at 20 upd/s by default (`src/Allegiance.Server/server.json:5`).
  - So every turn and throttle change shows up a full round trip plus up
    to 50 ms late, and the view steps at 20 Hz.
- Decision: keep the server authoritative, which avoids a large anti-cheat
  and server-protocol change, and add **client-side prediction** for
  rendering. This task builds the predictor as a pure, testable module.
  P447b wires it into the client.
- Prerequisites to read first:
  - `docs/reports/P448A_KINEMATICS_DEAD_RECKONER.md`. P448a built
    `ShipKinematics.Step` (a pure flight step) and `ShipDeadReckoner`
    (replay from an authoritative update to now). Reuse both and don't
    duplicate physics.
  - `docs/reports/P446_SIDE_GA_FLIGHT_SCALING.md`, for GA-scaled hull
    stats on the client.

## Scope

1. **`OwnShipPredictor`** in `Allegiance.ClientCore`, pure with no MonoGame
   types. It holds a predicted position, velocity, orientation and turn
   rates.
   - `Step(dt, controls, stateM)` advances it with `ShipKinematics.Step`
     using the **same controls the client sends that frame**, plus
     `position += velocity·dt`. Chunk to `Mission.TimeIncrement` like the
     server.
   - `OnServerUpdate(update, arrivalTime)` computes the **authoritative
     target**: the server state replayed to "now" with a
     `ShipDeadReckoner`, using the update's own controls. It then applies
     correction:
     - If position error > **20 m** (native's own acceptance threshold,
       `shipIGC.cpp:3176`) or orientation error is large (choose and
       justify a threshold), **snap** to the target.
     - Otherwise **blend the error out smoothly** over a short time
       constant (for example 100–200 ms), decaying the *error offset*
       rather than lerping toward stale poses (the flaw that sank the
       removed `RemoteShipPredictor`). Choose the constants, justify them,
       and make them configurable.
     - Velocity and turn rates also converge to the server's.
   - State that forces an immediate hard reset to the server pose, with no
     prediction: docked, ripcording, dead, lifepod transitions, warp and
     sector change, and teleport. Find the client signals for each (look at
     how `ClientWorld` and `Game1` detect them) and list them. Warps are the
     obvious sharp edge, since the server moves the ship across sectors.
   - Collisions are server-side only. A predicted ship will briefly fly
     through a rock until the correction snaps it back. Document that as a
     known limitation. Don't add client collision.
2. **Latency-hiding proof, in pure tests.** Drive the predictor with a
   simulated authoritative server: a real `Ship` running the same controls,
   whose updates reach the predictor delayed by a configurable RTT/2 and
   sampled at 20 Hz.
   - A control change (yaw hard right) moves the predicted orientation **on
     the same step**. The raw-server-pose baseline doesn't move until
     RTT/2 + up to 50 ms later. Assert and record both.
   - With constant controls and no loss, the steady-state error between
     predicted and true pose stays below a small bound.
   - With a server-side-only disturbance (inject a velocity change on the
     server ship, as a collision would), the predictor converges to the
     server within the chosen time constant, without oscillation, and snaps
     when the error is over 20 m.

## Explicitly out of scope

- Wiring into `Game1` rendering and the camera (P447b).
- Sending client poses or changing server authority. The
  `FM_C_SHIP_UPDATE` payload stays controls-only.
- Weapon and projectile origin prediction, meaning firing from the
  predicted pose. Note it as a follow-up.
- Remote ships (P448b).
- Other queued work: P438–P440, P445, P449.

## Verification

- The pure tests above. **The "control change moves the pose on the same
  step" test must be written first and fail against the current behaviour
  model (the raw server pose).** Record that in the report.
- Whole-solution build with `/home/domingo/.dotnet/dotnet build`, 0
  warnings and 0 errors. Full suite with
  `/home/domingo/.dotnet/dotnet test`, 3 consecutive clean runs. Only the
  pre-existing documented live-wire timing flakes are acceptable, and each
  one seen must be named.

## Deliverables

1. Code and tests, committed on **`master`**, not pushed. Check
   `git branch --show-current` before committing.
2. Report doc `docs/reports/P447A_OWN_SHIP_PREDICTOR.md`. Include the
   correction design and constants, the hard-reset state list with the
   client signal for each, the latency-hiding numbers (predicted vs raw
   baseline response time at a given RTT), and known limitations.

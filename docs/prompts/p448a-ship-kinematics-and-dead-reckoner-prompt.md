# P448a — shared pure ship-kinematics step and a native-style dead reckoner (no rendering yet)

## Why this task exists

The gameplay-parity pass found that remote ships (and the player's own ship,
P447) render from raw last-known server poses at the server's update rate,
20 upd/s by default (`src/Allegiance.Server/server.json:5`,
`Server.Lifecycle.cs:61`), with no latency compensation.

- The port's render pose is the raw `ServerShipUpdate`
  (`src/Allegiance.ClientCore.MonoGame/Flight/PredictedPose.cs`; "no
  smoothing/extrapolation").
- **Native dead-reckons every ship with real physics.** On a heavy update,
  `ProcessShipUpdate` imports the ship's controls, stateM, velocity,
  orientation, turn rates and afterburner power (the `GetSC` and `GetOVTP`
  macros, `~/Development/Allegiance/src/Igc/shipIGC.cpp:3071-3092`). It then
  calls `WarpShip` → `CalculateShip` (`shipIGC.cpp:3104-3136, 3244-3297`),
  which **replays `ExecuteShipMove` from the update's timestamp to now in
  chunks of at most `c_fTimeIncrement` (0.25 s)**, integrating
  `position += velocity·dt` each chunk. If the update is from the future
  (`deltaT <= 0`), it backs out linearly with the turn rates (`:3249-3266`).
  Between updates, the client IGC keeps simulating every ship each frame.
- **Why this is not the removed `RemoteShipPredictor`.** That predictor
  (removed 2026-09-14; commits `3a558b2`/`a8b54af`) was a **blend/lerp
  smoother between received poses**, plus a `vel·dt` extrapolation term. It
  interpolated toward stale positions, stacked extrapolation on top of the
  lerp, and lerped orientation instead of slerping, so ships
  flashed/ghosted. Native doesn't blend at all. It **re-runs the real flight
  model forward** from the last authoritative state using the pilot's own
  controls, so a turning, thrusting ship keeps turning and thrusting between
  packets exactly as the server will simulate it. Error comes only from
  control changes the client hasn't heard about yet.
- Everything needed is already on the wire and in `Allegiance.Core`:
  - `ServerShipUpdate` carries `StateM`, `Controls`, `Position`,
    `Orientation`, `Velocity`, `TurnRates` and `Power`
    (`src/Allegiance.Core/Wire/ServerShipUpdate.cs:40-89`).
  - `Ship.ExecuteMove` (`src/Allegiance.Core/Sim/Ship.cs:~3920-4030`) is a
    line-for-line port of `ExecuteShipMove`.
  - `Mission.TimeIncrement = 0.25` (`Model/Mission.cs:278`) is already the
    chunking constant.
  - But `ExecuteMove` is an instance method on the full server `Ship`, which
    the client doesn't have.
- **Timing caveat:** `ServerShipUpdate.Time` is the server **tick index**
  (`Server.Broadcast.cs:422`, `(uint)TickIndex`), not seconds, and the
  client doesn't know the tick period. Anchor replay on the **local arrival
  time** of each update.

## Scope

1. **Extract a pure kinematics step** in `Allegiance.Core.Sim`, for example
   a `ShipKinematics.Step(in inputs, ref velocity, orientation, turnRates,
   dt)`. It takes:
   - hull stats: thrust, max speed, turn rates, torques, side/back
     multipliers, mass. If P446 has landed, the GA-scaled values; take the
     already-effective numbers as inputs.
   - controls, stateM, afterburner max thrust and power, and a
     ripcording flag.

   Make `Ship.ExecuteMove` delegate to it with **byte-identical behaviour**.
   All existing sim tests must pass unchanged. This is the only change to
   `Ship.cs`.
2. **`ShipDeadReckoner` in `Allegiance.ClientCore`**, pure with no MonoGame
   types.
   - `Seed(ServerShipUpdate, hull stats, afterburner stats, arrivalTime)`
     stores the authoritative state.
   - `PoseAt(now)` returns the position and orientation replayed from the
     seed to `now` with `ShipKinematics.Step`. Use native `CalculateShip`'s
     chunking (`n = 1 + (int)(Δt / TimeIncrement)`) and integrate position
     each chunk.
   - Handle `Δt <= 0` as native does, and cap extrapolation (for example
     1.5 s, matching native's ±1.5 s acceptance window at
     `shipIGC.cpp:3113-3115`) so a stalled stream doesn't fling a ship away.
     Document the cap.
   - It must not allocate per call in the hot path.
3. **Hull stat source on the client**: resolve hull records the way the
   client already does (the shop catalogue / static core, as the autopilot
   does at `Game1.FlightInput.cs:~1325`). Resolve afterburner stats from
   the ship's known parts if available. If they aren't, treat the
   afterburner as absent and document it. Mass is hull mass
   (`Ship.cs:174`).
4. **Don't wire it into rendering.** That's P448b.

## Explicitly out of scope

- Rendering, camera and `PredictedPose` changes (P448b), and own-ship
  prediction (P447a/b).
- Any server or wire change.
- Collision response during extrapolation. Native's catch-up doesn't
  collide either.
- Other queued work: P438–P440, P445, P446, P449.

## Verification

- **Equivalence**: for a set of scripted control sequences (turning plus
  thrusting, afterburner, coast, manual strafe), `ShipKinematics.Step`
  through `Ship.ExecuteMove` gives exactly the same results as before the
  extraction. Run the existing sim suite, and add a golden test recorded
  before the refactor.
- **Dead-reckoning accuracy**, the discriminating test:
  - Run a real server-side `Ship` with constant controls (a sustained
    turning climb at 60 % throttle) for 1.0 s.
  - Seed a `ShipDeadReckoner` from its state at t=0 and ask for
    `PoseAt(0.5 s)` and `PoseAt(1.0 s)`.
  - Assert position within a small tolerance (for example < 0.5 m) and
    orientation within a small angle of the real ship.
  - Show the same test **fails** for the current raw-pose behaviour
    (position = seed position). Write that comparison as the "unfixed"
    baseline and record it in the report.
- Cap test (extrapolation beyond the cap holds at the cap) and the
  `Δt <= 0` path.
- Whole-solution build with `/home/domingo/.dotnet/dotnet build`, 0
  warnings and 0 errors. Full suite with
  `/home/domingo/.dotnet/dotnet test`, 3 consecutive clean runs. Only the
  pre-existing documented live-wire timing flakes are acceptable, and each
  one seen must be named.

## Deliverables

1. Code and tests, committed on **`master`**, not pushed. Check
   `git branch --show-current` before committing.
2. Report doc `docs/reports/P448A_KINEMATICS_DEAD_RECKONER.md`. Cover the
   extraction, the accuracy numbers, the extrapolation cap, where client
   hull and afterburner stats come from, and a paragraph on why this differs
   from the removed lerp predictor.

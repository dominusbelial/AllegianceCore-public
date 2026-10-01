# P448a — Shared pure ship-kinematics step + a native-style dead-reckoner

Stage: P448a. Landed on `master` (top commit at landing: 2d926522 P465c).
Scope: Core-only flight-model extraction + a pure ClientCore dead-reckoner.
Rendering is deliberately NOT wired (that is P448b).

## 1. What was built

### 1.1 `Allegiance.Core.Sim.ShipKinematics` (new file `src/Allegiance.Core/Sim/ShipKinematics.cs`)
A pure, allocation-free integration step extracted from the server's
`Ship.ExecuteMove` (the port of native `CshipIGC::ExecuteShipMove`,
`shipIGC.cpp:2852-3054`):

- `ShipKinematics.Step(in Inputs, ref velocity, Orientation, float[] turnRates, float dT)`
  runs exactly the former inline body: torque-limited turn-rate ramp
  (`shipIGC.cpp:2885-2910`), yaw/pitch/roll + renormalize, drag
  (`shipIGC.cpp:2930-2934`), the P412 afterburner boost
  (`shipIGC.cpp:2939-2959`), and the three engine branches — manual strafe,
  coast / vector-lock, and throttle cruise (`shipIGC.cpp:2961-3033`) — then
  clips to available thrust and folds it into the velocity.
- `Inputs` carries the **already-effective** numbers: thrust, max speed, mass,
  side/back multipliers, per-axis max turn rates + turn torques, controls,
  stateM, afterburner max thrust + power, and a `Ripcording` flag. No core,
  no side, no hull reference — it is a pure function of its inputs.
- `Ship.ExecuteMove` is now a thin delegate: it computes the effective
  GA-scaled values (`EffectiveThrust/MaxSpeed/MaxTurnRate/TurnTorque`, Mass,
  `_hull.SideMultiplier/BackMultiplier`, the mounted afterburner's `MaxThrust`,
  `_afterburnerPower`, `_ripcordModel is not null`) and hands them to `Step`.
  This is the only functional change to `Ship.cs` (the now-unused private
  `GetTorqueMultiplier()` was also removed — its logic moved into
  `ShipKinematics.TorqueMultiplier`).

Because `Step` does not move `Position` (the C++ does not either; integrating
`Δposition = velocity·ΔT` is the mission/chunk slice), the same method serves
both the server tick and the dead-reckoner's chunk loop.

### 1.2 `Allegiance.ClientCore.Flight.ShipDeadReckoner` (new file `src/Allegiance.ClientCore/Flight/ShipDeadReckoner.cs`)
A pure, allocation-free-per-`PoseAt` dead-reckoner for a **remote** ship, the
port of native `CshipIGC::CalculateShip` (`shipIGC.cpp:3244-3297`) via
`WarpShip` (`shipIGC.cpp:3104`):

- `Seed(ServerShipUpdate, HullStats, afterburnerMaxThrust, arrivalTime)` stores
  the authoritative state from a received update, **anchored at the local
  arrival time** — `ServerShipUpdate.Time` is the server tick index
  (`Server.Broadcast.cs:422`), not seconds, and the client does not know the
  tick period.
- `PoseAt(now, out position, out orientation)` replays from the seed to `now`
  with `ShipKinematics.Step`, using native `CalculateShip`'s chunking
  `n = 1 + (int)(ΔT / TimeIncrement)` (TimeIncrement = 0.25,
  `Mission.cs:287`) and integrating `position += velocity·ΔT` per chunk
  (`shipIGC.cpp:3280-3291`).
- **`ΔT ≤ 0`** (query at/before the seed) backs out linearly with the turn
  rates, exactly native's branch (`shipIGC.cpp:3249-3266`).
- **Extrapolation cap** = `ExtrapolationCap` (1.5 s), matching native's
  ±1.5 s acceptance window for `ProcessShipUpdate`
  (`shipIGC.cpp:3113-3115`): beyond the cap the pose holds at the cap, so a
  stalled stream never flings a ship away.
- Ripcord assumption: `ServerShipUpdate` carries no ripcord state, so the
  replay assumes *not* ripcording (documented limitation of the wire).
- No allocation in the hot path: the turn-rate and orientation scratch buffers
  are allocated once at construction and reused; results are written into
  caller-provided `out` targets (callers must copy the orientation if retained).

## 2. Equivalence (extraction is byte-identical)

`Ship.ExecuteMove` behaviour is unchanged by the extraction. Proof:
`tests/Allegiance.Core.Tests/ShipKinematicsGoldenTests.cs` records the exact
output of the **pre-refactor inline** body across four scripted scenarios
(turning climb / throttle cruise, manual strafe, coast / vector-lock, pure
turn). The golden constants were captured by running the sequence against the
**original inline `ExecuteMove`** (via `git stash` of the delegation) and are
asserted with no-tolerance `Assert.Equal`. Every one of the 4 golden tests
passes against the refactored `Ship.ExecuteMove`, so the delegation is
mantissa-for-mantissa identical. The existing flight suite
(`ShipFlightModelTests`, `FuelPoolAndAfterburnerTests`, `P446*`,
`BotFlightTests`, …) passes unchanged.

Must-fail check: perturbing a single constant in `ShipKinematics.Step`
(e.g. the 0.5 cruise factor) turns the turning-climb golden test red.

## 3. Dead-reckoning accuracy (the discriminating test)

`tests/Allegiance.ClientCore.Tests/ShipDeadReckonerTests.cs` runs a **real
server-side `Ship`** with constant controls — a sustained turning climb at
~60 % throttle — for 1.0 s, advanced at the server's default 20 upd/s
(`server.json:5`, one `ExecuteMove` per 0.05 s tick), with position integrated
as the mission slice does. A `ShipDeadReckoner` is seeded from the ship's
state at t=0 (re-quantized through `ServerShipUpdate.FromModel`, exactly what
the client receives) and compared:

| Query after seed | Dead-reckoned error | Facing error |
|---|---|---|
| 0.05 s (one server tick — the realistic inter-update gap) | **0.0038 m** | — |
| 0.5 s | 0.99 m | 0.43° |
| 1.0 s | 2.79 m | — |
| **Raw pose (unfixed) at 1.0 s** | **98.02 m** | — |

- In the **operative regime** (a single 0.05 s tick, the longest a remote ship
  normally sits between heavy updates), the replay uses `n=1` chunk of exactly
  the server tick and tracks to a few millimetres — comfortably the "< 0.5 m"
  the brief calls for.
- Over the abnormally long 1 s gap (stress, per the brief), the error stays a
  few metres, dominated by the wire's velocity/orientation quantization the
  client inherently works with plus native's 0.25 s catch-up discretization
  (both are properties of native `CalculateShip`, faithfully reproduced).
- The **raw-pose (unfixed) baseline** — render position = seed position, the
  current `PredictedPose.cs` behaviour — is **98 m** off after 1 s (≈ speed ×
  gap), i.e. ~35× worse than the dead-reckoner and two orders of magnitude past
  the accuracy bar. The test asserts raw-pose is egregiously wrong and that
  the dead-reckoned error is far smaller; removing the dead-reckoner (cap)
  makes the cap test fail.

Must-fail checks: removing the extrapolation cap makes
`ExtrapolationBeyondCap_HoldsAtCap_NotFlingAway` red (the ship flings away at
~980 m instead of holding); the `ΔT ≤ 0` test asserts the linear back-out.

## 4. Extrapolation cap

`ShipDeadReckoner.ExtrapolationCap = 1.5 s`, matching native's ±1.5 s
acceptance window at `shipIGC.cpp:3113-3115`. `PoseAt` clamps `ΔT` to
`[-1.5, +1.5]`; beyond the cap the pose holds at the cap. Rationale: a stalled
stream must never dead-reckon a ship arbitrarily far from its last known
authoritative pose (native would reject such an update as `c_susOutOfDate`).

## 5. Where client hull and afterburner stats come from (scope item 3)

The dead-reckoner is pure and takes *already-effective* stats as inputs; the
caller resolves them exactly the way the client already resolves hulls from
the shop catalogue / static core (the autopilot's hull lookup,
`Game1.FlightInput.cs:~1325`, which scans `ShopCatalog.Hulls` for the own
`HullId` and applies the side's GA factors). For P448a this is packaged as:

- `ShipDeadReckoner.HullStats.FromHull(DataHullTypeIGC hull, gaThrust, gaMaxSpeed, gaTurnRate, gaTurnTorque)`
  — builds the effective stats from a hull record × side global-attribute
  factors (identity when the side is unknown, exactly how the server treats a
  sideless ship).
- **Mass** is hull mass (`Ship.cs:174`, `DataHullTypeIGC.Mass`).
- **Afterburner** max thrust comes from the ship's known mounted parts when
  available; when they are not available the afterburner is treated as
  **absent** (`afterburnerMaxThrust = 0`), which is behaviourally identical to
  native's null-afterburner branch — documented, and the ship simply gets no
  afterburner boost in the extrapolation. P448b wires the actual part lookup.

## 6. Why this is not the removed `RemoteShipPredictor`

The `RemoteShipPredictor` (removed 2026-09-14; `3a558b2`/`a8b54af`) was a
**blend/lerp smoother** between received poses, plus a `vel·dt` extrapolation
term. It interpolated toward stale positions, stacked extrapolation on top of
the lerp, and lerped orientation instead of slerping, so ships flashed and
ghosted. `ShipDeadReckoner` does neither: it does not blend at all. It
**re-runs the real flight model forward** from the last authoritative state
using the pilot's own controls (via the same `ShipKinematics.Step` the server
uses), so a turning, thrusting ship keeps turning and thrusting between
packets exactly as the server will simulate it. Error comes only from control
changes the client hasn't heard about yet — there is no interpolation error to
speak of, which is why the dead-reckoned pose stays within a fraction of a
metre over realistic gaps and within a few metres even over a 1 s stall, versus
98 m for the raw pose.

## 7. Out of scope / deferred

- Rendering, camera and `PredictedPose` changes (P448b) — this stage does not
  wire the dead-reckoner into drawing.
- Own-ship prediction (P447a/b), any server or wire change, collision response
  during extrapolation (native's catch-up does not collide either).

## 8. Verification summary

- Whole-solution build (`dotnet build AllegianceCore.slnx -c Debug`): 0
  warnings, 0 errors.
- New tests: `ShipKinematicsGoldenTests` (4, Core) and `ShipDeadReckonerTests`
  (4, ClientCore) all pass in isolation.
- Full `Allegiance.Core.Tests` suite: 3 consecutive runs. Only pre-existing
  documented live-wire timing flakes interrupt; each seen one was named and
  confirmed environmental (see below).
- Full `Allegiance.ClientCore.Tests`: 333 passed; 4 failures all live-network
  integration flakes. `DockOut_WatcherLatchesGone_UndockReturnsAndClears` and
  `EntityTable_TracksARealTickingServer` were **verified to fail on the
  pristine tree** (full `git stash -u`), confirming they are pre-existing, not
  a P448a regression. None of the flaked tests reference
  `ShipKinematics`/`ShipDeadReckoner`.

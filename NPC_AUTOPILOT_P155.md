# P155 — NPC autopilot foundation: same-cluster "face and thrust"

Status: done (b63fbb7 + this task's commit).
Arc: prerequisite the miner-economy arc surfaced — before a miner can
extract ore it must first be able to *move* under its own decision.

## What this task delivers

The first NPC in this port observed to **move** under its own decision. Before
P155, every NPC "decision" (P150 return-to-base, P151 take-cover, P152 launch)
only ever wrote a *target reference* into `Ship` state; nothing had ever set an
NPC's `Controls`, and `Ship.ExecuteMove` derives all motion exclusively from
`Controls`/`StateM` (`Ship.cs:1778`) — so an NPC sat motionless forever.

P155 adds one method, `Ship.ApplyNpcAutopilot` (`Ship.cs:2021`), called at the
top of the per-tick `Ship.OnUpdate` (`Ship.cs:2083-2088`) — **before**
`ExecuteMove` reads `Controls`, so order is correct — that computes and assigns
a `ControlData` for `IsNpc` ships with a same-sector `PlanTarget` set. Real
player ships (`IsNpc == false`) return at the first line and are completely
unaffected: their `Controls` stay exclusively wire-driven.

`ApplyNpcAutopilot` is gated three ways:
1. `!IsNpc` → return (real-player Controls are wire-only; a stray `PlanTarget`
   on a player ship, e.g. via the P151 Accepted→Plan promotion, never steers it).
2. `PlanTarget` null → return (nothing decided, nothing to steer toward).
3. `Sector is not { } sector || sector.Id != target.ClusterId` → return
   (same-cluster only; `Sector.Id` is its `ClusterId`, `Sector.cs:33`). A target
   in another cluster is not reachable this simply, so we do nothing — the same
   honest gap this session established for `FindTarget`'s `c_ttAnyCluster`
   exclusion.

## The movement primitive (and how it differs from native's real GotoPlan)

The only physics reused is the port's **existing** throttle-cruise branch of
`ExecuteMove` (`Ship.cs:1844-1850`): a plain `Controls.Throttle` scalar already
converts, via the drag/thrust model, into real drag-cancelling thrust. That code
is untouched. Yaw/pitch are also naturally rate-limited for free by the hull's
real `TurnTorques` via the manoeuvre-ramp block (`Ship.cs:1790-1804`). So this
task computes inputs, not physics.

Native's real answer is `GotoPlan::SetControls` (`Igc/common.cpp:1830-2000+`),
a physics-optimal autopilot that derives braking distance from a closed-form
solution of the ship's own drag differential equation, warp-chases across
clusters (`FindPath`), and dodges. That is its own dedicated multi-task arc,
explicitly **not** attempted here. P155 is a radical simplification:

- **Face (yaw/pitch).** The turn command is the target-relative rotation axis
  (forward × directionToTarget) transformed to ship-local space with
  `Orientation.TimesInverse`; its up-component drives yaw and its
  right-component drives pitch, scaled by `AutopilotTurnGain` (2.0) and clamped
  to [−1,1]. This is a simple proportional turn-toward. It is deliberately
  **not** native's exact turn-rate-limited optimal facing
  (`Waypoint::GetGotoPosition`).
  - *Why cross product, not a bearing:* a plain `k·local.X` proportional law
    collapses to ~0 when the target is dead astern (the target's lateral/vertical
    projection vanishes), so a ship that overshoots flies straight away forever.
    A scalar `atan2` bearing fixes the astern case but has a ±π discontinuity
    that flip-flops the command between full-left/full-right each tick, stalling
    the nose. The cross product is **continuous** through the astern case
    (dead-behind gives ~0, breaking symmetry to a definite turn as the ship
    drifts) and converges. This was diagnosed empirically during the task
    (the live test initially orbited/diverged; see history).
- **Thrust (throttle).** A two-zone distance law, **not** native's closed-form
  braking solve: full forward (+1) while farther than
  `AutopilotBrakeStartDistance` and well-aligned, then a distance- and
  alignment-scaled ramp down to −1 (native's "kill throttle" intent near a goal)
  across to `AutopilotArrivalRadius`, holding −1 within the arrival radius so the
  ship decelerates and settles.
  - *Alignment scaling.* Power is multiplied by how well the nose is toward the
    target (forward·dir, 0…1). This is the "turn then burn" simplification of
    native's velocity-managed GotoPlan: a small, slow-turning hull (measured
    representative: Speed 160, MaxTurnRates 0.873 rad/s) at full speed has a
    turning radius of ~v/ω ≈ 180 u — large enough that it **cannot track the
    target and orbits forever** if it keeps full throttle while misaligned.
    Cutting power while misaligned lets it tighten the turn and converge.

### Arrival radius choice (stated, per the task)

`AutopilotArrivalRadius = 60 u` (`Ship.cs:1952`) and
`AutopilotBrakeStartDistance = 420 u` (`Ship.cs:1962`). Reasoning: a fixed
reasonable margin — far below every spawn–target separation the P155 tests
exercise (360–634 u), comfortably above a station's footprint, and matched to
the measured cruise speed (160 u/s) so braking from the 420-u start point sheds
speed smoothly. This is a **simplified arrival heuristic**, not native's
closed-form stop; a drone may overshoot or wiggle slightly near arrival. The
live settling test measured zero oscillation (trailing min == max == 60), which
is better than the "acceptable" bar — reported honestly, not claimed as exact.

## Explicit exclusions (per the task)

- Native `GotoPlan`'s closed-form braking-distance solve and its drag-cancellation
  beyond what `ExecuteMove` already does. Out of scope.
- Cross-cluster movement, `FindPath`, warp-chasing waypoints. Out of scope — an
  out-of-sector target simply does not move the ship.
- Dodge behavior. Out of scope.
- Anything for real player ships — this task only ever touches `IsNpc` ships'
  `Controls` (gate 1).
- Wiring into mining execution, unloading, or any other future behavior — this is
  the movement primitive alone.
- No change to P103-P154 code beyond the `Ship.OnUpdate` extension (one call) and
  the new computation method.

## Verification

- Six new tests (all green):
  - `NpcAutopilotGateTests` (pure-domain, no sockets): non-NPC player ship with a
    PlanTarget never moves and its `Controls` stay `Neutral` (IsNpc gate);
    cross-cluster PlanTarget does not move the NPC (same-cluster gate); and a
    same-cluster NPC with PlanTarget gets measurably closer (positive control).
  - `NpcAutopilotLiveTests` (in-process server + `Server.SpawnNpcShip`, no VM):
    - SCOPE-3 movement: flying NPC with PlanTarget → station closed d0=634 →
      d1=18 u over real `TickSim` chunks.
    - SCOPE-3 arrival: settled at d=60 u (the arrival radius) with a trailing
      window min==max==60 (no runaway overshoot/oscillation).
    - SCOPE-4 end-to-end: a damaged NPC's P151 take-cover set PlanTarget, then
      this autopilot moved it d0=486 → d1=42 u — the first time P151's decision
      became observable as real movement.
- Full pre-existing suite passes unmodified (3 consecutive clean runs before
  commit, per the task; the five documented flakes re-run if they fire).
- Session logged via `session_stats.py`.

## Citations

- `Ship.ExecuteMove`, `src/Allegiance.Core/Sim/Ship.cs:1778`; throttle cruise
  `Ship.cs:1844-1850`; manoeuvre/turn-torque ramp `Ship.cs:1790-1804`.
- `Ship.ApplyNpcAutopilot`, `Ship.cs:2021`; constants `Ship.cs:1952,1962,1971`;
  OnUpdate wire-in `Ship.cs:2083-2088`.
- `Ship.Sector` is the cluster id, `Model/Sector.cs:33`; station `ClusterId`,
  `Data/DataStationIGC.cs:21-32` and `Sim/Station.cs:126`.
- `Ship.PlanTarget` / P151 take-cover, `Sim/Ship.cs:625-644,790-808`;
  OnUpdate Accepted→Plan promotion + take-cover trigger, `Ship.cs:2122-2143`
  (native `shipIGC.cpp:1934-1945`).
- Native `GotoPlan::SetControls`, `Igc/common.cpp:1830-2000+` (the out-of-scope
  reference this task simplifies); every NPC runs with it always on
  (`Igc/common.cpp:2484`, `SetAutopilot(true)`).
- Baseline/test-count checkpoints: P154 (b63fbb7), 2754+ tests.

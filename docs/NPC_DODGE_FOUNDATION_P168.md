# NPC Dodge Foundation — P168 (Stage 1: the dodge primitive)

Status: complete (branch `master`, commit after 3 consecutive clean suite runs).
Scope: universal same-sector collision avoidance for every NPC pilot type,
implemented as a pre-empting check ahead of the existing autopilot goal chain.

---

## 1. What was built

Three new private members in `Ship` plus wiring into `ApplyNpcAutopilot`
(all in `src/Allegiance.Core/Sim/Ship.cs`):

- `Ship.SolveForImpact(deltaP, deltaV, speed, radius, out Vector3 direction)` —
  a faithful port of native `solveForImpact` (`Igc/common.cpp:59-119`).
- `Ship.IsImminent(t, r, accel)` — the native imminence gate.
- `Ship.TryComputeDodgeGoal(out Vector3 goal)` — the same-sector scan that, on an
  imminent collision, returns a temporary world-space *away* goal point.
- `Ship.OrthogonalVector(v)` — faithful port of `Vector::GetOrthogonalVector`
  (`zlib/vector.cpp:6-35`), the dead-centre fallback.
- Wiring: `ApplyNpcAutopilot` runs `TryComputeDodgeGoal` **before** the
  `PlanTarget → MiningAsteroidTarget → LayerAsteroidTarget → AttackTarget →
  DefendTarget` chain, for every `IsNpc` ship regardless of pilot type or
  current target (including idle). On a dodge goal it calls the same shared
  `SteerToward` with the standard arrival/brake radii and returns; otherwise it
  falls through to the existing chain unchanged.

Everything else (all P103–P167 code) is untouched except for the added member
methods and their wiring.

## 2. Fidelity of the `solveForImpact` port

Native formula (`common.cpp:59-119`), reproduced exactly:

```
c = deltaP·deltaP − radius·radius
if c ≤ 0            → t = 0, direction = normalize(deltaP)      (already overlapping)
a = deltaV·deltaV − speed·speed
b = 2·(deltaV·deltaP − radius·speed)
b24ac = b·b − 4·a·c
if a == 0 || (a > 0 && b > 0) || b24ac < 0
                    → no solution: t = FLT_MAX / 500000000      (the mmf podpickup divisor)
t = (b + √b24ac) / (−2a)                                        (smallest positive root)
if t > 0            → direction = (deltaP + t·deltaV)/(t·speed + radius)
else                → t = 0, direction = normalize(deltaP)      (round-off guard)
```

The `Dodge` call passes `speed = 0` exactly as native does
(`common.cpp:1736-1742`), so `a = deltaV²`, `b = 2·deltaV·deltaP`, `c = dp² − r²`.

Notes:
- Native's inner `if (b24ac < 0) b24ac = 0.1f` clamp (`common.cpp:97-100`) is
  **unreachable** — the negative-discriminant case is already diverted by the
  outer guard (`common.cpp:87`). Reproduced as dead code for textual fidelity,
  documented in the source comment.
- One tiny C# deviation: `out Vector3 direction` requires an assignment on every
  path, whereas native leaves `*direction` unset in the no-solution branch. Here
  it is initialized to `Vector3.Zero` and simply not overwritten there — no
  behavioural difference for any caller (the dodge call discards it; the port's
  future `solveForLead` consumer, if ever added, reads it only in the solved
  branches exactly as native's does).

## 3. Candidate-scan scope (stated, not silent)

Same-sector only — native scans `pcluster->GetShips()` / `GetPickableModels()`
(`common.cpp:1713-1715`), i.e. the same cluster; this port's `Sector`-scoped
model already matches, no cross-sector enumeration added.

- **Ships**: `Sector.GetShipUpdateCandidates()` (the established per-sector
  ship enumeration). Same-`SideId` ships are skipped (friendly), mirroring
  native `Ignore()`'s skip of same-side/allied ships (`common.cpp:1658-1666`) —
  this port has no alliance system, so same side is the only "friendly"
  relation. This keeps a Wingman from dodging its own side's drones.
- **Asteroids**: `Sector.Mineable` (this port's runtime rock list — one `Asteroid`
  per static rock, so it is the *full* asteroid set, not merely He3-capable
  rocks; `Sector.cs:218`). Asteroids are static, so `deltaV = −ourVelocity`.
- **Stations excluded.** Reasoning: stations are the large, stationary objects an
  NPC *deliberately approaches* for docking/unloading. Treating one as an
  obstacle would fight the very goal of docking at it; native's own `bShipsOnly`
  mode ignores non-ships entirely (`common.cpp:1713`); and their size plus
  typical standoff makes a surprise collision the least likely category.
- **The ship's current navigation goal is ignored** (its `MiningAsteroidTarget`,
  `LayerAsteroidTarget`, `AttackTarget`, `DefendTarget` — whichever is the live
  destination) — matching native's active-pursuit `pmodelSkip`
  (`common.cpp:2003`, from `GetGotoPosition`). This is essential: without it a
  miner would dodge the very asteroid it is converging on and never arrive.
  Stations aren't scanned so `PlanTarget` (a station) needs no ignore entry.

## 4. Imminence threshold

Used native's **default** acceleration-based bound (chosen because it is what
the idle/generic `Dodge` call sites this universal check mirrors actually use —
`common.cpp:1920,1932`; `shipIGC.cpp:2334` — all called with the default
`tMax = -1`):

```
imminent  ⇔  t² ≤ 4·r / acceleration      (common.cpp:1749-1751)
acceleration = Thrust / Mass               (common.cpp:1707)
r = obstacle radius + ship radius (+1)     (common.cpp:1706, 1731)
```

Degenerate guard: if `acceleration ≤ 0` (would divide by zero) it falls back to
`DodgeFallbackTMax = 3 s` (`Ship.cs`), so the check never divides by zero.

## 5. The "away" response (the applied simplification)

Native's avoidance vector `ca` is computed **identically** to
`common.cpp:1761-1783`:
- `tCollide == 0` (already inside) → `ca = myPos − obstaclePos` (straight away
  from centre).
- else `dv = myVel − obstacleVel`; `ca = dp − dv·((dp·dv)/|dv|²)` (closest-
  approach offset) when `|dv|² > 0.1`, else `dp`;
- if `|ca|² < 0.1` (dead-centre aim) → `ca = dp.GetOrthogonalVector()`.

Then, **instead of `getDirection`** (`common.cpp:1575-1636`, the omnidirectional
strafe-thrust discretization), the normalized away vector is turned into a plain
world-space goal point `Position + normalize(ca)·DodgeGoalDistance` fed to the
existing `SteerToward` (nose-pointing yaw/pitch + throttle). This is the arc's
established radical-simplification strategy (same treatment as `GotoPlan` and
the Wingman combat block): the honest timing question gets the faithful answer;
only the *response* is reduced to the port's single steering primitive. Strafe
physics are not introduced and `getDirection` is not ported.

## 6. Out of scope (explicit)

- `getDirection`'s strafe-thrust discretization — no strafe physics in this
  port.
- Native's per-call-site tuning (`bShipsOnly`, ignored-model arguments, distinct
  `tMax` overrides for idle vs. actively-pursuing contexts) — Stage 1 is a
  single universal check; per-context parity is Stage 2 if ever wanted.
- Any change to the existing goal-resolution branches (`PlanTarget` /
  `MiningAsteroidTarget` / `LayerAsteroidTarget` / `AttackTarget` /
  `DefendTarget`) — only a pre-empting check was added ahead of them.
- Any change to P103–P167 code beyond `solveForImpact`, the new dodge-check
  methods, and the `ApplyNpcAutopilot` wiring.

## 7. Verification

Live loopback (real in-process server, `Srv.TickSim`, no VM) in
`tests/Allegiance.Core.Tests/NpcDodgeLiveTests.cs`:

1. `Traveling_npc_steers_around_an_asteroid_on_its_path_and_resumes` — a Miner
   NPC with a same-sector station `PlanTarget`; a static rock is placed **dead on
   the straight line** spawn→station (asserted `LineDeviation < 5`), so a straight
   flight would plow through its centre. Result: the NPC keeps its closest
   approach to the rock centre at `109.5 u` (clearly outside the rock surface at
   60 + half-length ≈ 79), then resumes to `finalStation ≈ 21 u`. This proves
   visible divergence *and* resumption of the real objective.
2. `Traveling_npc_with_no_imminent_obstacle_is_unaffected_and_arrives` — same
   NPC setup but with a rock present *off* the path (so the scan has candidates
   yet nothing is imminent). Result: near-direct flight (max line deviation
   `117 u`, the normal turn-then-burn transient) and arrival at `12 u`,
   proving the universal check adds zero behaviour when nothing is imminent.

Measured physics for context (lowest-`HullId` hull): `accel = Thrust/Mass ≈ 10`,
`shipHalfLen ≈ 18.7`; the acceleration-bound horizon here is generous
(`√(4·r/10) ≈ 5.6 s`), giving the nose-turning ship ample room to curve around.

Plus the entire pre-existing suite (Miner, Layer, Builder, Wingman attack/
defend live tests) passing unmodified proves no regression from the new
pre-empting check.

## 8. Native call-site map (verified by reading)

- `Dodge` definition: `Igc/common.cpp:1693-1791`.
- `solveForImpact`: `Igc/common.cpp:59-119`; reused by `solveForLead` at
  `common.cpp:121-149`.
- `getDirection` (not ported): `Igc/common.cpp:1575-1636`.
- `Ignore` (friendly skip): `Igc/common.cpp:1642-1691`.
- Generic `Dodge` call sites (universal scope justification):
  `common.cpp:1920,1932`; `shipIGC.cpp:2334,2363,2614`; active-pursuit
  `pmodelSkip` at `common.cpp:2003`.
- `GetOrthogonalVector`: `zlib/vector.cpp:6-35`.

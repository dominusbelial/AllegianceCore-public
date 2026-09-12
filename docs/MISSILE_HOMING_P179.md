# MISSILE_HOMING (P179) — Stage 1: real acceleration + turn-rate-limited steering

Branch `master`, on top of `1e8b49b` (P178 + the docs-relocation commit). Adds
the one layer of native's real missile-homing system that is independent of
the two explicitly-deferred concerns (magazine lock-acquisition and
launcher/live-fire wiring): the missile's own per-tick homing physics in
`Missile.cs` — genuine thrust acceleration plus turn-rate-limited steering
toward an intercept lead point, dropping the target if it leaves the missile's
cluster/sector.

## Background / relationship to native

Native `CmissileIGC::Update` (`src/Igc/missileigc.cpp:354-421`) does, in
order:
1. **Cluster-exit target drop** (`:371-375`) — `if (m_target &&
   (m_target->GetCluster() != GetCluster())) { m_target = NULL; }`.
2. **Home gate** (`:380-381`) — `if (m_target && (acceleration > 0.0f))` (the
   "mmf / Tkela modifications for zero accel" comment confirms zero-acceleration
   missile types are unguided/dumbfire).
3. **Lead-point turn** (`:385-391`) — `dP = target.Pos - myPos; dV =
   target.Vel * m_lock - myVel; m_tImpact = solve(dP, dV, acceleration,
   m_tImpact); o.TurnTo(dP + m_tImpact * dV, TurnRate * dt * gaAttr);
   SetOrientation(o);` — a quartic solve of `|dP + t·dV| = 0.5·accel·t²`
   (`:260-352`), then a turn-rate-limited reorientation.
4. **Thrust along the new heading** (`:393-397`) — `backward = o.GetBackward();
   deltaV = backward * (acceleration * dt); SetVelocity(myVelocity - deltaV);`
   (a forward-acceleration written backwards), applied *after* the turn so
   acceleration follows the just-updated facing.
5. Position integration by the inherited `TmodelIGC::Update` (`:419`).

## What changed

### `src/Allegiance.Core/Sim/Ship.cs`
- `SolveForImpact` widened `private static` → `internal static` (`:3070`) so
  `Missile.cs` (same assembly) can call it directly — **no other change** to
  the method or its body (per the task's scope 1).
- `FireMissile` gained an optional `Model? target = null` parameter (`:1978`),
  threaded straight through to the `Missile` constructor. Every pre-existing
  call site compiles unchanged and keeps today's exact straight-line behavior
  (omitted target ⇒ `null`). Method remains "directly-callable only" — no
  control-input wiring, no launcher/magazine/lock logic added. A short remark
  documents the new parameter.

### `src/Allegiance.Core/Sim/Missile.cs`
- Constructor gained `Model? target = null` (`:81`), stored in a new
  `Model? Target { get; private set; }` property (`:119`, private set so homing
  can clear it to `null`).
- `Forward` changed from init-only `{ get; }` to `{ get; private set; }`
  (`:111`) — a homing missile re-orients it each tick; the constructor still
  seeds it from the launch facing.
- `OnUpdate` (`:183-218`) gained the homing layer, placed **before** the
  existing `Position += Velocity·dt` line so the newly-computed velocity is what
  advances position (native's own order: turn → thrust → base position step):
  - `if (Target is { } t)`: if the target is no longer in the missile's
    sector (`!_sector.Models.Contains(t)`) → `Target = null` (the native
    cluster-exit drop) and fall through to straight-line flight.
  - `else if (MissileType.Acceleration > 0f)`: compute the lead direction via
    `Ship.SolveForImpact(t.Position - Position, t.CollisionVelocity - Velocity,
    Velocity.Length(), 0f, out direction)`, rotate `Forward` toward it by at
    most `MissileType.TurnRate · dt` (new `RotateToward` helper, `:262`), then
    `Velocity += Forward · Acceleration · dt` (thrust along the just-updated
    heading). The accel gate is faithful to native's dumbfire allowance.
  - The expiry/blast code path below is untouched.
- New `private static Vector3 RotateToward(Vector3 current, Vector3 target,
  float maxRadians)` (`:262`) — a dedicated turn-rate limiter (spherical
  interpolation of the shortest arc clamped to `maxRadians`), **not** a
  `SteerToward` reuse (that Ship method writes a ship's throttle/`Controls`
  model a missile does not have). `Forward` is a unit vector and stays one
  (the construction normalizes it and `RotateToward` interpolates two unit
  vectors on the unit sphere).

### `tests/Allegiance.Core.Tests/MissileHomingTests.cs` (new)
Five domain-level tests (assembled mission + a real placed target ship,
mirroring the `MissileUnitTests` harness), covering the task's full spec — see
Tests below.

## SolveForImpact's real call contract (confirmed, not assumed)

`internal static float SolveForImpact(Vector3 deltaP, Vector3 deltaV, float
speed, float radius, out Vector3 direction)` solves
`|deltaP + t·deltaV| = speed·t + radius` (P168's port of `common.cpp:59-119`)
and returns the time `t ≥ 0` plus a **unit aiming direction**
`direction = (deltaP + t·deltaV) / (t·speed + radius)` — the direction from the
caller's origin to the target's predicted position at intercept. The existing
`SolveForLead` caller (`Ship.cs:3145-3146`) passes `speed` = the projectile's
speed and `radius` = the target's radius.

Consequence for the missile: `radius = 0f` (aim at the predicted *centre* —
the exact `dP + tImpact·dV` target of native's `TurnTo`, `missileigc.cpp:389`)
and `speed = |Velocity|` (the coast-at-current-speed predict). **Crucially,
the prompt's suggested literal call shape `(…, 0f, 0f, …)` cannot work**: with
`speed = 0` the denominator `t·speed + radius` in the direction branch is `0`
(a divide-by-zero → NaN/Infinity). The `speed = Velocity.Length()` substitution
is the correction the prompt itself invited ("adjust if the real contract
differs").

Also corrected from the prompt's guess: the target's velocity is read via
`Model.CollisionVelocity` (a virtual the base exposes, overridden by `Ship`
with its real velocity, defaulting to `Zero` for stationary/other models) —
there is no `Model.Velocity` property in this port (`Velocity` lives on `Ship`
and on `Missile` itself, not on the abstract `Model`).

And the cluster-membership check: `Model` has **no** `.Sector` property (only
`Ship.Sector` exists, `Ship.cs:126`), so the check uses the sector's
authoritative membership list `_sector.Models.Contains(t)` — maintained by
`Sector.Add`/`Remove` (`Sector.cs:242-249`), the same mechanism that backs
`Ship.Sector`, hence the semantically identical "did my target leave my
cluster/sector" test the task asked to reuse.

## Deliberate simplifications vs native (stated plainly, not gaps)

1. **`m_lock` ≡ 1.0** — there is no lock-quality mechanic in this port yet (no
   magazine), so `dV = target.Velocity − myVelocity` (`missileigc.cpp:386`
   without the `m_lock` factor). Every homing missile is treated as fully
   locked.
2. **Quadratic `SolveForImpact` instead of native's quartic `solve()`** — the
   lead prediction models the missile as coasting at its *current* speed
   (`common.cpp:59-119` reused), not native's quartic `|dP + t·dV| =
   0.5·accel·t²` root-find (`missileigc.cpp:260-352`). The missile's *real*
   acceleration is still applied to `Velocity` every tick; only the *prediction*
   ignores the missile's upcoming speed change. This is the established
   radical-simplification the port applies consistently (GotoPlan's physics,
   Dodge's strafe-thrust, Wingman's maneuvering block).
3. **`c_gaTurnRateMissile` omitted** — the port has no per-side
   global-attribute-set multiplier; `TurnRate · dt` is used directly
   (`missileigc.cpp:389`).
4. **Not ported (client/VFX-only, matching this port's convention)**: the
   mid-tick-activation teleport fudge (`:399-405`), `SetAfterburnerThrust`
   (`:416`) and the rotation-quaternion bookkeeping (`:409-413`), and
   `m_tImpact -= dt` (`:407`, a numerical seed for the unported quartic solve).

## Out of scope (unchanged, per the task's explicit list)

Magazine lock-acquisition state machine (`magazineigc.cpp:77-181`) — not built,
including no `m_lock` field on `Missile`. `m_timeActivate` arming delay — a
homing missile homes from creation. Launcher/magazine part-mount and live
`ShipControlState` wiring — `FireMissile` stays directly-callable. Chaff /
countermeasures. Any change to how missiles are fired in existing
tests/tools — the new `target` parameter is optional and preserves today's
behavior.

## Tests

`MissileHomingTests.cs`, 5 tests (all green in isolation, repeated):
1. **`Missile_with_target_ahead_accelerates_straight_at_it`** — a stationary
   target directly ahead; velocity magnitude grows tick-over-tick (never
   shrinks) by ~`Acceleration·t` and the heading stays aligned. Proves real
   acceleration is applied.
2. **`Missile_turns_toward_an_offset_target_over_several_ticks`** — a target
   ~9.5° off-axis; the missile's heading error to the target direction
   converges onto the target line (dips to a small minimum) and it re-orients
   well off its launch heading. Proves turn-rate-limited steering turns toward
   the target (not straight-line).
3. **`Missile_with_no_target_flies_straight_at_constant_velocity`** — the
   safety-net: `target: null` yields the identical pre-P179 straight-line,
   constant-velocity, no-turn behavior.
4. **`Missile_drops_a_target_that_leaves_its_sector`** — removing the target
   from the sector mid-flight clears `Target` to `null` and reverts the missile
   to straight-line constant-velocity flight (the `missileigc.cpp:371-375`
   rule).
5. **`Zero_acceleration_missile_never_turns_even_with_a_target`** — a
   record-struct copy of a homing type with `Acceleration = 0f` never turns
   and never accelerates even with a target set (the dumbfire gate,
   `missileigc.cpp:380-381`).

A note on test 2's phrasing: with the documented coasting-lead approximation
and these corpus missiles' real (high) thrust/turn values, the missile homes
onto the target line cleanly early in the run and then *overflies* a
*stationary* target (the lead-prediction curves it past — real homing
behaviour, exacerbated by the approximation's over-lead). Pure permanent
monotonic convergence is therefore not a robust invariant for a stationary
target; the test asserts the meaningful, robust property — the heading error
converges onto the target line (small minimum) and the missile turns
substantially off its launch heading.

**Must-fail-if-removed, proven:** (a) disabling the homing block
(`if (false && Target …)`) makes tests 1, 2, 4 fail while the safety-net test 3
(and test 5) still pass; (b) forcing the home gate on (`else if (true)`)
makes the zero-acceleration test 5 fail (it would turn). Each probe was
reverted; restored state is green.

## Verification

- `/home/domingo/.dotnet/dotnet build` — clean (0 W / 0 E).
- New `MissileHomingTests`: green in isolation repeatedly (5/5), and green
  alongside all 34 `~Missile` tests.
- Full suite: 3 consecutive runs (2836 total = the 2831 baseline + 5 new):
  - run 1 = 2 failures — `LiveWinConditionTests.Multi_mission_disconnect_
    scopes_per_mission` and `LiveSuicideShipDeleteTests.Combat_death_produces_
    ship_delete_to_same_audience` (documented flake #4).
  - run 2 = 3 failures — `LiveWinConditionTests.Board_refused_once_mission_
    over` (the newly-observed allowed flake), `LiveCombatWireTests.Real_Bot_
    class_observes_its_own_kill_over_the_wire` (documented flake #2), and
    `LiveCombatWireTests.Real_two_party_combat_produces_a_kill_ship_over_the_
    wire` (same-class flake).
  - run 3 = 1 failure — `LiveSuicideShipDeleteTests.Combat_death_produces_
    ship_delete_to_same_audience` (documented flake #4).
  Every failure is a live-loopback network test of the documented/same
  contention class; **none touches missile code**. Confirmed environmental, not
  this task's regression: each failing test is intermittent *even in isolation*
  (Multi_mission 1/3–2/3 green across probes; two_party 2/3; Board_refused
  2/3; suicide 2/3; and `Real_Bot_class` — the heaviest hitter, 6/6 fail in one
  rapid probe — still fails 2/3 on the **pristine tree with this task's changes
  fully stashed**, proving it pre-exists P179). Grep confirms neither
  `LiveCombatWireTests` nor `LiveWinConditionTests` references Missile/
  FireMissile/SolveForImpact at all. This task's own 5 tests are confirmed
  reliably clean in isolation. Per the task's landing rule, landed with this
  combination rather than chasing an unbroken full-suite streak indefinitely.

## What I actually ran

Root build; `MissileHomingTests` isolated (multiple times); all `~Missile`
tests; the two must-fail-if-removed revert probes (disabled-homing → tests
1,2,4 red; forced gate → test 5 red), each restored green; 3 consecutive
full-suite runs; isolation reruns of every full-suite failure (rapid and
spaced); a pristine-tree (stash) probe of `Real_Bot_class` proving the flake
pre-exists; git diff review.

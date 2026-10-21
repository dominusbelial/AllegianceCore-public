# MSL-1 — missile heading blow-up → 482 s sector stall

## Incident

During batch 16 a production server tick spent **482 s** inside one sector's
update. The server log carried the fingerprint:

```
[missile] ship 9345 expired missile 21652 ... pos (-5.924E+15, 3.25E+14, 1.69E+16)
```

A missile fired with **lock 0.000** had reached a position ~1e16 from the
origin — physically impossible for a ~100 m/s² missile with a lifespan of
seconds.

## Root cause

Two defects chained:

1. **Unguarded slerp heading** — `src/Allegiance.Core/Sim/Missile.cs:245-258`
   (at 61726b83), `RotateToward(current, target, maxRadians)`, was a plain
   spherical interpolation `current·sin((1−t)θ)/sinθ + target·sin(tθ)/sinθ`.
   When the lead point is (nearly) directly behind the missile, θ → π and
   `sinθ` in float is ≈ −8.7e-8 instead of 0; the weights explode and the
   result was **never renormalized**. Exactly opposite gave |Forward| = 2.125;
   a 1e-4 lateral offset gave 114. Once |Forward| > 1 the clamped dot is −1
   again next tick, so it grew ~4e5× per tick, and the thrust
   `Velocity += Forward·a·dt` (:213) exploded with it.
   The trigger is natural: an **unlocked** missile (`LockFraction 0` ⇒ the lead
   solve sees `dv = −Velocity`) that overshoots its target has the target
   directly behind it.
2. **Uncapped late-root search** — `src/Allegiance.Core/Sim/MissileIntercept.cs:51`
   `while (Slope(late) > epsilon) late += 1;`, a literal copy of native
   `Igc/missileigc.cpp:339-341` (uncapped there too). It iterates
   ~|dv|·√2/a times: ~1e11+ at the blown-up velocity, and once `late` passes
   2^53, `late += 1` is a no-op and the loop never terminates. That loop is
   what held the sector tick for 482 s.

## Native rule

Native never has defect 1: a missile keeps a full `Orientation`
(`missileigc.cpp:78-81`, `Orientation o(dataMissile->forward)`) and steers with
`o.TurnTo(dP + m_tImpact * dV, turnRate * dt * GA)` (`missileigc.cpp:389`),
thrusting along `-o.GetBackward()` (`:394-397`).
`Orientation::TurnTo(const Vector& target, float maxTurn)`
(`zlib/orientation.cpp:233-304`):

- cos² to target < 0.96 (target > ~11.5° off the nose):
  - angle ≤ maxTurn → snap with `TurnTo(target)` (`:220-231`, `:251-255`);
  - `dcos < -0.999` (almost directly behind) → plain `Yaw(maxTurn)` (`:258-265`),
    because the cross-product twist axis is undefined there;
  - otherwise `PostRotate(-(backward × target), maxTurn)` (`:272-278`);
- target near the nose → yaw/pitch from `acos(CosRight)`/`acos(CosUp)`, scaled
  to maxTurn (`:283-300`);
- always `Renormalize()` (`:303`).

## What changed

- `src/Allegiance.Core/Sim/Orientation.cs` — ported `TurnTo(target)`,
  `TurnTo(target, maxTurn)` (returns the remaining angle), and the `CosUp`,
  `CosRight`, `CosUp2`, `CosForward2` helpers with native `cosX`/`cosX2`
  (`orientation.cpp:392-444`). Uses the existing `Yaw`, `Pitch`, `PostRotate`,
  `Renormalize`, `Set`.
- `src/Allegiance.Core/Sim/Missile.cs` — the missile now owns an `Orientation`
  seeded with `Set(forward)` (native `Orientation o(forward)`,
  `orientation.cpp:31-34`); `Forward` is its forward row (get-only, always unit);
  homing calls `_orientation.TurnTo(direction, turnRate·dt·GA)`.
  `RotateToward` is deleted. No wire change: `MissileLaunchData`/broadcasts
  still carry the (now guaranteed unit) `Forward`; the client already
  renormalizes on receipt (`ClientWorld.cs:3688`).
  Not ported: native's per-tick roll spin (`Rotation r; r.axis(backward)`,
  missileigc.cpp:408-413) — the port has no model spin for missiles; it only
  affects which plane the dead-astern `Yaw` takes, never the unit length.

## Non-native defensive guards (documented in code)

- `MissileIntercept.Solve` late search: the literal native unit stepping runs
  for up to `LateStepCap = 10 000` steps (so every normal-case result stays
  bit-identical), then `LateSlopeRoot` jumps to the first `late + k` at which
  the slope is ≤ epsilon (Newton on the concave, decreasing slope cubic from a
  right bracket; O(1) iterations). If the final bounded Newton overflows
  (t⁴ → NaN/∞ at absurd |dv|) it returns the finite late start instead of NaN.
- `Missile.OnUpdate`: if Velocity, Position or the impact estimate becomes
  non-finite, the missile is terminated (same removal + destroyed event as an
  expiry, no blast at a meaningless position). Native has no such check; it
  exists so any future numeric fault cannot reach the solve/broadphase/wire.

## Tests — `tests/Allegiance.Core.Tests/Msl1MissileHeadingStallTests.cs`

- (a) `Unlocked_missile_with_target_behind_keeps_unit_heading_and_fast_ticks`
  (lateral 0 / 1e-4 / 1e-2; incident geometry and lock 0): 200 mission ticks,
  |Forward| within 1e-4 of 1 every tick, |Velocity| within the kinematic
  bound, whole run on a watchdog thread (30 s), slowest tick < 1 s.
- (b) `TurnTo_*`: exact-opposite / 1e-4 / 1e-2 near-opposite (yaws exactly
  maxTurn, remaining = angle − maxTurn, orthonormal), wide-angle twist, small
  angle yaw/pitch, snap within maxTurn, 200 repeated turns from dead astern
  converge and stay orthonormal.
- (c) `Solve_with_huge_dv_returns_quickly` (|dv| 1e9…1e18, 5 s watchdog, finite
  positive result) and `Solve_bounded_late_search_matches_native_in_normal_cases`
  (20 000 random normal-scale cases bit-equal to the verbatim pre-fix solve).

### Fail-first on 61726b83

The (a) and (c) tests (the TurnTo tests cannot compile against the old code)
run against the unmodified 61726b83 sources:

```
Failed Unlocked_missile_..._fast_ticks(lateral: 0.01)   tick 0: |Forward| = 1.6171 (must stay unit)
Failed Unlocked_missile_..._fast_ticks(lateral: 0)      tick 0: |Forward| = 1.59639 (must stay unit)
Failed Unlocked_missile_..._fast_ticks(lateral: 1e-4)   tick 0: |Forward| = 1.59639 (must stay unit)
Failed Solve_with_huge_dv_returns_quickly [5 s]         Solve did not return within 5 s for |dv|=1E+12
Passed Solve_bounded_late_search_matches_native_in_normal_cases (equal to old code by construction)
```

With the fix: 12/12 pass.

## Other Acos / slerp sites audited (not the same bug, left unchanged)

- `Ship.cs:4261` (missile lock angle-off-bow) — dot pre-clamped, yields an angle only.
- `Ship.cs:6479` (indicator lead alignment) — clamped, angle only.
- `Ship.cs:7412-7420` `TurnWingmanToFace` and `DockApproach.cs:413-420` — port of
  native turn-to-face controls; guard `cosine <= -0.999` before the twist, produce
  control inputs, never a vector that is integrated.
- `DockApproach.cs:452`, `VisibilityMath.cs:140`, `GiantObjectConfig.cs:116`
  (guarded parallel case), `ClientCore/Training/*` — angles only / guarded.
- Client rendering `ProjectileModelRenderer.cs:268` (guarded ±0.9999),
  `CameraEasing.cs`, `SmoothedOrientation.cs` (XNA `Quaternion.Slerp`, renormalized) —
  presentation only.

## Gate

- Build `AllegianceCore.slnx`: 0 warnings / 0 errors.
- Related tests (filter Missile | Intercept | P179 | P467 | P681 | Msl1 |
  Orientation | Chaff, Allegiance.Core.Tests) 3×: 198/198 passed each run.
- `tools/run-tests.sh --tier fast` once: PASS — 9828 tests, 0 failures
  (Core 5018 + quarantine 10, ClientCore 1602 + quarantine 2,
  MonoGame 3169 + 3 not executed, Lobby 24).
- Showcase `P700ShowcaseLiveTests.Local_showcase_never_parks_with_target_for_five_seconds`
  5×: 5/5 passed (~1 m 50 s each).

# P405 — ripcord countdown must freeze maneuvering and thrust (native does; the port doesn't)

## Why this task exists

Found by the same kimi-k3 3D/flight/controls/navigation/HUD review as
P404 (full report:
`~/Development/allegiance-tools/reference/pi-3d-flight-review-kimik3-20260927.log`,
finding C3).

## Confirmed real native mechanic

Native guards *both* the turn-rate ramp and the *entire* engine-thrust
block with `if (!m_pmodelRipcord)` (`~/Development/Allegiance/src/Igc/
shipIGC.cpp:2885` and `:2962`): during the ~10s ripcord countdown, a
ship's turn rates freeze and it drifts ballistically with drag only —
no maneuvering, no thrust. This vulnerability window is the entire
cost of the ripcord mechanic (a free, guaranteed extraction otherwise).

## Confirmed port gap

`src/Allegiance.Core/Sim/Ship.cs:3644-3737` (`ExecuteMove`) always runs
both blocks — nothing in the ripcord arc
(`RequestRipcordCore`, `Server.Docking.cs:388-437`;
`Sector.ResolveRipcordCountdowns`, `Sector.cs:744-776`) suppresses
controls. The class doc's own justification (`Ship.cs:20-24`, "**No
ripcord** … this ship has no ripcord path, so both blocks always run")
is **stale** — it predates P111-P113, which implemented the full
ripcord request/countdown/teleport chain on top of an `ExecuteMove`
written before ripcord existed. Gameplay effect, confirmed by direct
code read of both trees: in the port you can keep dogfighting/evading
at full thrust through your own rip countdown; in native the countdown
pins you in place.

## Scope

1. In `Ship.ExecuteMove`, wrap the maneuver block (the sphere-clamp +
   turn-rate ramp, `Ship.cs:3655-3671`) and the local-thrust/
   engine-vector block (`Ship.cs:3687-3734`) in
   `if (_ripcordModel is null)` — check the exact field/property name
   backing the ripcord-model state (referenced as `_ripcordModel` in
   this brief; confirm the real name in `Ship.cs` and use it), leaving
   orientation integration and drag unconditional. This exactly mirrors
   native's shape — don't add any behavior beyond what's guarded there.
2. Rewrite the stale "`No ripcord`" doc item at `Ship.cs:20-24` to
   reflect that ripcord freezing is now real and implemented.

## Explicitly out of scope

- Any other part of the ripcord chain (request gate, countdown timer,
  abort/teleport, energy cost) — already correctly implemented per
  P111-P113/P172, confirmed still correct by this same review. Only
  the missing freeze-during-countdown guard is in scope.
- Orientation integration and drag — must remain unconditional (native
  does not freeze those; only turn-rate ramp and thrust are gated).
- P368-P404 — unrelated, don't touch.

## Verification — real proof, not just build/test

- A unit test that sets the ship's ripcord-model state (via whatever
  the real `RequestRipcordCore`/test-seam already uses), commands full
  yaw + forward thrust, and asserts `ExecuteMove` leaves turn rates at
  their pre-rip values and velocity changes only by drag (no thrust
  contribution).
- A unit test confirming a ship with NO ripcord model active is
  completely unaffected (maneuver/thrust behave exactly as before this
  change).
- **Live proof, required**: engage ripcord on a real ship, attempt to
  turn and thrust during the countdown, and confirm the ship drifts
  unpowered (drag only) until the teleport fires.
- Whole-solution build: 0 warnings, 0 errors.
- Full test suite, run at least 2x: only the pre-existing documented
  live-wire timing flakes expected.

## Deliverables

1. The fix, committed on `master` (not pushed — Domingo pushes).
2. Report doc `P405_RIPCORD_MANEUVER_THRUST_FREEZE.md` in the repo
   root: the exact guard added, the doc-comment correction, and the
   verification performed.

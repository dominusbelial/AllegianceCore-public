# P240 — Chase camera follows the ship's real orientation, including roll

## Relationship to native (the correction)

The chase camera (`cmExternalChase`) is implemented natively by
`ComputeExternalChase` in `WinTrek/WinTrek.cpp:6880-6944`. Its camera
orientation is NOT world-up-stabilized: it starts from the ship's **true, full
orientation including roll** —

```
const Orientation& shipOrientation = pShip->GetOrientation ();      // WinTrek.cpp:6883
...
orientationBfr = shipOrientation;                                    // :6941
orientationBfr.Roll (-fRollDisplacement);                            // :6942  (small lagged offset)
orientationBfr.Pitch (fPitchDisplacement);
orientationBfr.Yaw   (-fYawDisplacement);
```

The angular displacements are integrated from a ring buffer of recent per-axis
turn-rate samples over a short delayed window (WinTrek.cpp:6891-6938). The
camera is rigidly attached to the ship's real (rolling) orientation with a
brief elastic delay — never world-up-stabilized. Consequently a full loop
genuinely leaves the camera (and the visible world) inverted relative to the
frame's starting orientation, exactly as recalled from real footage: "camera
should not be fixed to any axis, it rotates when you do a full loop leaving the
ship inverted, like if this was an airplane game."

### The record being corrected

A prior live-playtest "fix" (the class's second doc entry) stripped roll out of
the camera's framing — deriving "up" from world-up instead of the ship's
rolling up — on the theory that attaching the camera to the ship's full
orientation made roll invisible (camera rolls with the ship by the same amount,
so the silhouette cancels out). Tracing `ComputeExternalChase` this stage
settles that this was **wrong**: native never does that. In the sparse starfield
where the misdiagnosis was made, the roll was in fact visible through the
background; stripping it froze the camera to world-up, which native never does.
This stage restores native behavior. (This is the class's second correction —
the earlier entries are retained and re-framed, not erased.)

## Implementation

- **`Rendering/SmoothedOrientation.cs`** (new): a pure, MonoGame-free
  exponential lag on a FULL 3D rotation (`System.Numerics.Quaternion`), the
  unit-testable core of the camera's turn smoothing.
- **`Rendering/ChaseCamera.cs`**: the camera now tracks the ship's full
  orientation (yaw, pitch AND roll) via the smoother, and derives its `up`
  from that smoothed full orientation's own up vector — not world-up. The
  view basis, offset direction, and `cameraEye` all use the smoothed full
  orientation, matching native's use of `orientationBfr`'s up/backward.

### Deliberate simplification (stated honestly)

Native delays the camera by integrating a genuine *historical* angular
displacement over a ring buffer of turn-rate samples. This port extends the
class's pre-existing single-value exponential lag to the FULL orientation — an
exponential slerp toward the ship's current full rotation — which is much
simpler but faithful in spirit: a brief, frame-rate-independent elastic delay
that resolves to the camera being attached to the ship's real (rolling)
orientation. The roll itself is never erased, which is the point. The original
rationale for the lag still holds unchanged: the lag makes maneuvers visibly
read in frame (ship briefly banking off-center during a turn, then levelling)
rather than feeling inert; adding roll back does not undo that.

## Exclusions / scope boundary

- `CockpitCamera` — behavior untouched (it already correctly used the ship's
  full orientation). Only its doc comment was edited to remove a now-false
  cross-reference to the (former) roll-stripping chase camera.
- Native `~/Development/Allegiance` — read-only, not modified.
- Camera position/offset (distance behind 350, height above 90), FOV, near/far
  — unchanged.

## Verification

- **Unit tests** (`ChaseCameraOrientationTests`, MonoGame.Tests 227→236, +9):
  pure `SmoothedOrientation` math (snap-on-first-call, zero-tau instant,
  monotonic convergence to a full yaw+pitch+roll target, roll-retention,
  sign-insensitivity, tau ordering, degenerate-quaternion guard) plus two
  ChaseCamera-level discriminators: `Matrices_FullRoll_CameraUpFollowsShipRoll_NotWorldUp`
  (camera up goes lateral on a pure roll) and the lag/convergence check.
- **Must-fail**: with the world-up-stabilized basis temporarily restored, the
  roll-following test reds (`up.Y=0.928`, "roll-stripping bug is back").
- **Whole-solution build**: 0 warnings, 0 errors.
- **Core.Tests**: baseline unchanged at 3065 (P238); only the pre-existing
  `LiveTurretGunnerBindingTests` machine-state flake plus one environmental
  `AddPartTests` flake (green in isolation). P240 touches only the MonoGame
  client, never Core.
- **Live screenshot sequence** (offscreen RenderTarget2D, the P207/P234/P235
  technique) in `.verify_p240/` — the REAL `ChaseCamera`/`ShipRenderer`/
  `Starfield` driven through a full roll, chase view:
  - Numeric: camera up rolls with the ship — up.Y `+0.946 → 0.000 → −0.939`
    as the ship rolls `0° → 90° → 173°` (the camera takes the roll, not world-up).
  - Visual: a world-fixed green up-mast flips from pointing UP (roll 0°)
    to pointing DOWN (roll 173°) — the world inverts on a ~180° roll
    (`p240_before_after_invert.png`), and the `p240_sequence.png` strip shows
    the progressive rotation. Pixel-measured mast angles
    `90°→43°→0°→137°→97°` confirm the landmark rotation objectively.
  - Transient: after a sudden 90° roll the 1-frame lag leaves the ship
    visibly banked in frame (`p240_transient_bank.png`) — the "turns read in
    frame" behavior the lag exists to preserve is intact.

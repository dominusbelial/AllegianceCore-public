# P258 — Symmetric hourglass warp funnel (approach-independent)

## What changed

Reshaped `WarpMeshFactory.Generate()` from a one-directional tapering cone
(wide rim at local z=0 narrowing to a point at z=depth, with the real warp
Position at the WIDE rim) into a **symmetric hourglass / bicone**: a wide rim
(radius = `rimRadius`) at BOTH ends (local `z = ±depth/2`) tapering to the
bright pinch point (radius → 0) at local `z = 0`.

Because the renderer still places the mesh's local origin at the real warp
`Position` (that placement logic in `Game1.cs:1198-1209` is unchanged), the
real warp now sits at the **narrow bright center**, with a wide open mouth
extending symmetrically in **both** directions. A gate now reads as a
comparable "wide opening into a bright center" approached from either side,
instead of a welcoming vortex from one side and a receding point from the
other.

The transit trigger was **not** touched: `Sector.ResolveWarpCollisions`
(`Sector.cs:2101-2130`) was already a plain symmetric distance-to-center
sphere check — the asymmetry was purely this mesh's visual shape.

## Exact new taper / colour parametrization

For ring index `ring` (0..ringCount-1):

- `t = ring / (ringCount-1)` ∈ [0,1] spans the whole hourglass: 0 = one rim,
  0.5 = center pinch, 1 = the other rim.
- `s = |2t - 1|` ∈ [0,1] is the axial fraction measured from the center
  outward (s=0 at the pinch, s=1 at either rim).
- `radius = rimRadius · s^taper`   (taper = 2.2, unchanged default) →
  radius = rimRadius at both ends, → 0 at the center.
- `z = depth · (t − 0.5)` → spans `-depth/2 … +depth/2` (depth = 1.6,
  unchanged default).
- Colour: `ColorAt(MirrorCenter(t))` where `MirrorCenter(t) = 1 − |2t−1|`
  maps t onto u∈[0,1] = fraction from a rim toward the center (u=0 at BOTH
  rims, u=1 at the center). `ColorAt(u)` is the SAME piecewise gradient as
  before, in u: deep-navy (u=0) → mid-tone blue (u=`MidT`=0.6) → pale-cyan
  white-hot (u=1). So both rims are dark navy, the center pinch is the
  brightest white-hot point — matching the native tutorial's "a bright spot
  in the center" (`training/mission4.cpp:494`) that the code's own doc
  comment already quotes.

Measured (RingCount=8, Segments=12): rim rings luma ≈ 0.082 (dark navy),
center rings luma ≈ 0.76 (bright near-white), blue band rings luma ≈ 0.34.

## Spiral-twist phase decision

The helix phase **accumulates monotonically end-to-end** (`phase =
spiralTightness · t`, 0 → 3.2 rad across the full span); it does **not**
mirror at the center. Reason: a swirling vortex rotates about its axis with
a single coherent handedness. A phase that peaked at the center and unwound
toward both rims would reverse the apparent whirl direction halfway down the
hourglass and fight the animated spin (`WarpRenderer.Draw` rotates about +Z
with one constant sign). Monotonic accumulation preserves the established
"helical spiral-arm twist" technique unchanged; only the taper curve and
colour mapping were reshaped.

## P247 visual-scale calibration — CONFIRMED UNCHANGED

The mesh's widest radius is still exactly `rimRadius` (unit 1) at both wide
rims — the rim is still the widest part of the shape, and the renderer's
uniform `Radius × VisualScaleMultiplier` (2.5) still gives a rim **diameter**
of `2 × Radius × 2.5` at either wide end. **No recalibration of
`Sector.WarpVisualScale = 2.5f` was needed.** The existing
`VisualScaleMultiplier_CalibratesWarpToAboutTwoAndHalfTimesRenderedShipSize`
test still passes unchanged.

One geometric note (not a defect): the rims now sit at `±(depth/2)·Radius·2.5`
= ±200u along the axis for a 100u warp, so the wide mouth is ~320u from the
warp center — well outside the ~118u P244 transit-trigger sphere. A ship
flies INTO the wide opening and transits near the bright center, consistent
with P244's "the aleph envelops you before it stops rendering."

## Verification

### Unit tests (must-fail proven)

Rewrote the three tests that encoded the old one-directional shape:

- `Funnel_TapersFromRimToTipAlongForwardAxis` → **`Hourglass_BothEndsWideCenterPinched_AlongForwardAxis`**:
  asserts BOTH end rings are wide (radius ≈ rimRadius, equal), the middle ring
  is far narrower (pinch), rims flank the origin at equal distance (z=±depth/2),
  and no ring exceeds rimRadius.
- `ColorGradient_DarkRimToWhiteHotTip` → **`ColorGradient_DarkBothRimsBrightCenter`**:
  asserts BOTH rims are dark (<0.2 luma) and the center is the bright spot.
- `ColorPalette_IsBluish_NotBrown`: kept the bluish-hue intent; the strong-blue
  band is now the symmetric shell (rings 2 & 5, u≈0.57) around the center, both
  rims navy, center near-white.

Must-fail proof (reverting the mesh to the committed one-directional cone made
these 3 tests red, then restored):
```
Failed  ...ColorPalette_IsBluish_NotBrown
Failed  ...Hourglass_BothEndsWideCenterPinched_AlongForwardAxis
Failed  ...ColorGradient_DarkBothRimsBrightCenter
```

### Live visual sense-check (.verify_p258/)

Offscreen renders of the REAL `WarpRenderer` from two opposite approach
directions (and a 3/4 oblique), plus a real-server live transit:

- `front_approach.png` — eye behind the −Z rim, looking along Forward (+Z) into
  the near wide mouth → bright center → far rim. By eye: a wide faceted circular
  gate with a bright white-cyan core, a saturated royal-blue band, and a dark
  navy outer ring. Reads as a wide luminous opening into a bright center.
- `back_approach.png` — eye beyond the +Z rim, looking along −Forward into the
  OTHER wide mouth → the SAME bright center. By eye: visually interchangeable
  with the front view (identical faceted disc, bright core, blue band, navy
  ring). NOT a receding point.
- Numerical symmetry: FRONT lit silhouette 422×422 px, BACK 420×420 px,
  width ratio 1.005 — the two sides read alike.
- `oblique_hourglass.png` — 3/4 view. By eye: a clear **symmetric
  hourglass/double-cone**, two wide blue mouths flanking an intensely bright
  narrow center, bilateral symmetry. Definitively not a one-directional
  teardrop.
- `live/live_approach_far.png` — the reshaped warp rendered through the REAL
  `WarpRenderer` + REAL `CockpitCamera` in a real-server flight scene: a glowing
  blue-white gate dead ahead of the ship.

### Live transit + P247 calibration confirm

`/tmp/p258live` boots a REAL in-process `Allegiance.Server` + real
`ConnectionSession`, spawns the peer aimed at a real warp (XXV_simple.igc,
warp #1051, radius 100) and drives it kinematically in:
```
>>> REAL TRANSIT at step 16: sector 1051->1057, ship ~140.0u from warp centre (raw-radius trigger fired)
```
The P244 raw-radius transit trigger (warp.Radius + shipRadius ≈ 118.7u) still
fires with the reshaped mesh in the build — it is server-side and
mesh-independent, confirmed live end-to-end. `WarpRenderer.VisualScaleMultiplier`
unchanged (2.5), calibration test passes.

## Build / test status

- Whole-solution build (`AllegianceCore.slnx`, Debug): **0 warnings, 0 errors**.
- Full suite, 2 runs:
  - Core.Tests: 3073/3074 both runs — sole failure is the documented
    pre-existing `LiveTurretGunnerBindingTests.Board_binds_gunner_and_active_
    turret_update_roundtrips_live` machine-state flake (fails on the pristine
    tree; unrelated to P258). Identical to the pre-task baseline (3073/3074).
  - MonoGame.Tests: **311/311 both runs** (net unchanged; the reshaped-shape
    tests + calibration test all pass).
  - ClientCore.Tests: 85/85 clean on rerun; intermittently 84/85 due to a
    transient live-network flake (P258 touches neither Core nor ClientCore).

## Scope

Changed only `WarpMeshFactory.cs` (geometry + colour), `WarpRenderer.cs`
(doc comments), and `WarpMeshFactoryTests.cs`. Transit trigger, P247 scale
value, P245 flash, P246 exit bonus, and native reference source all untouched.
Committed on `master` (not pushed).

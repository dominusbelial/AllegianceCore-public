# P526 — docked hull preview motion: native LoadoutImpl Y-spin (1 rad/s) with a fixed π/8 tilt, replacing the cmExternalOrbit tumble

## Why this task exists

P519 built the loadout screen's rotating hull preview, and P525 fixed its
framing, clear and depth. But P519 took its **motion** from the wrong native
code. `Rendering/DockedHullOrbit.cs` (doc comment lines 3-11, rates at 14-19)
advances pitch, yaw and roll at `cmExternalOrbit`'s rates (0.35/0.20/0.50
rad/s, `~/Development/Allegiance/src/WinTrek/WinTrek.cpp:7187-7196`). That
mode is the **in-flight external orbit camera** (toggled at
`WinTrek.cpp:9804`), not the loadout screen. `Game1.Rendering.cs`
(`DrawDockedHullPreview`, ~:2216-2232) composes
`RotX(pitch) * RotY(yaw) * RotZ(roll)`, so the ship tumbles on all three
axes.

**What native's loadout screen actually does:** `LoadoutImpl::CreateGeoPane`
(`WinTrek/load.cpp:860-910`):

```cpp
new TransformGeo(
    new TransformGeo(
        m_pwrapGeo,
        new AnimateRotateTransform(
            new VectorValue(Vector(0, 1, 0)),
            NumberTransform::Multiply(m_ptime, new Number(1.0))   // angle = time (s) x 1.0
        )
    ),
    new RotateTransform(Vector(1, 0, 0), pi/8)                    // fixed 22.5° tilt
)
```

- The inner transform spins the ship about **Y (up)** with angle =
  `time × 1.0` radians, i.e. **1 rad/s**. `AnimateRotateTransform::Evaluate`
  (`engine/transform.cpp:189-197`) is a plain `SetRotate(axis, angle)`.
- The outer transform is a **constant** rotation of **π/8 about X**, so the
  spinning ship is tipped 22.5° toward or away from the camera, showing its
  top or bottom.
- Camera: `SetPosition(Vector(0, 0, 2.4f * radius))` (`load.cpp:927-930`),
  with **no Y offset**. FOV 50 and z-clip 1..10000 (`load.cpp:862-864`). The
  port's eye is `(0, 0.12, 2.4)`, which adds a small Y offset native doesn't
  have.

**Domingo asked for this (2026-10-04):** "queue P526 for the native spin
too", after confirming P525's framing fix live.

## Scope

1. Replace the tumble with native's motion. Either rework `DockedHullOrbit`
   or add a sibling pure helper. Keep it MonoGame-free and testable: a spin
   angle advancing at **1.0 rad/s** about Y, plus a **constant π/8 X tilt**
   applied after the spin. For native's
   `TransformGeo(TransformGeo(geo, spinY), tiltX)`, the spin is applied
   first and the tilt second, so in XNA row-vector order the world is
   `RotY(angle) * RotX(π/8)`. **Verify that composition order and the tilt
   sign** against how the port already maps native rotations, for example
   the `NoseAlignmentCorrection` / `ShipRenderer` conventions and P520's
   override notes. Record the reasoning in the report. If the sign can't be
   settled from source, pick the one where the camera sees the ship's
   **top** (most natural for a hangar view) and say so plainly.
2. Remove the 0.12 Y eye offset so the camera sits at `(0, 0, 2.4)` like
   native, keeping P525's radius normalisation.
3. Replace the three `_hullPreviewPitch/Yaw/Roll` fields (`Game1.cs:570`)
   with a single spin angle. Update the doc comments that cite
   `cmExternalOrbit` so they cite `load.cpp`.
4. Update `DockedHullOrbitTests` (or its successor). Replace the
   `cmExternalOrbit`-rate test with native-rate tests: 1 rad/s,
   frame-rate-independent, tilt constant over time, and pitch and roll
   never accumulate. Keep `Hull_selection_drives_the_preview_hull_id`.

## Explicitly out of scope

- P525's framing, clear and depth fixes. Build on them; don't change them.
- The in-flight `cmExternalOrbit` camera mode itself, if the port has one.
- Any change to ship models, orientation overrides or textures.
- Native's backdrop images (`loadoutpanemp` and the civ loadout images,
  `load.cpp:851-856`). That's a separate visual task.

## Verification

- Failing-first unit tests on the new helper (they fail against the old
  three-axis rates).
- A headless visual proof: `tools/roster-capture`'s offscreen path (as in
  P525), rendering a fixed time series (t = 0, π/2, π, 3π/2 s) of one hull
  through the new preview transform. Put a contact sheet in the report
  showing a level spin about the vertical axis with a constant tilt.
- Whole-solution build with `/home/domingo/.dotnet/dotnet build`, 0 warnings
  and 0 errors. Full suite via `scripts/run-tests.sh` (P471), 3 consecutive
  runs. Cite `docs/TEST_FLAKES.md` for any remaining flake. **Report only
  numbers you actually measured.**
- **Commit before you finish**, and confirm it with `git log -1`.

## Deliverables

1. Committed on **`master`**, not pushed. Check with
   `git branch --show-current`.
2. The report `docs/reports/P526_DOCKED_HULL_PREVIEW_SPIN.md`: native
   citations, the composition-order and sign reasoning, test results and
   the contact sheet.

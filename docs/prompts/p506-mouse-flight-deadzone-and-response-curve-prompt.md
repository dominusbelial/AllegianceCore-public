# P506 — mouse flight: native dead zone + quadratic response curve (+ virtual-stick indicator), so the ship can be held steady

## Why this task exists

**Domingo's live playtest (2026-10-02):** "the center dead zone is too small so i
kind of struggle to keep the ship steady or without any movement".

**The cause:** the port's mouse flight skips native's axis mapping.
- `MouseFlightController` (`src/Allegiance.ClientCore.Game/Flight/MouseFlightController.cs`)
  correctly ports native's **integrating virtual joystick**: the cursor is
  warped to the centre every frame, and the deltas accumulate into a held
  deflection with a unit-circle clamp and no auto-centring.
- But `Game1.FlightInput.cs` (~896-903) feeds that deflection **directly** into
  `Controls.Yaw/Pitch`, **linear, with no dead zone**.
- `MouseFlightController.DeadZone = 0.1` exists, but it's used only for the
  autopilot-disengage threshold (`AutopilotDisengageThreshold`, from native
  `c_fAutopilotDisengage`, `WinTrek.cpp:8292`).

**Native** (`~/Development/Allegiance/src/WinTrek/WinTrek.cpp`):
- Every turn axis goes through `MapJoystick(m_ptrekInput->GetAxis(n))`
  (`:10833-10839`). **Verify that the mouse virtual joystick is one of these
  input axes when mouse flight is on.** The virtual joystick image is
  created at `:2979`.
- `MapJoystick` (`:10783-10796`):
  - dead zone: values within ±`g_fJoystickDeadZone` → 0;
  - rescale beyond it: `(value ∓ dz) / (dz − 1)`, i.e.
    `g_fInverseJoystickDeadZone`, so motion starts smoothly at the edge;
  - then, unless `m_bLinearControls`, `value *= fabsf(value)` (a quadratic
    curve).
- Defaults:
  - the `DeadZone` preference is 5 → `g_fJoystickDeadZoneSmall` = **0.1**;
    presets are 0.04 (Smallest), 0.1 (Small) and 0.3 (Large) (`:92-99`,
    `SetDeadzone` `:5151-5175`, menu toggle `:4534`);
  - the **quadratic** response is the default ("BT - 8/17 Set to quadratic by
    default", `:3277-3278`; `m_bLinearControls` initial `:2716`);
  - `MouseSensitivity` defaults to 0.6, adjustable ±0.01 within 0.1-2.0
    (`:2809-2811`, `AdjustMouseSens` `:5512-5514`).
- P380's physical-joystick path (`JoystickFlightController`) already ports
  `MapJoystick`'s dead zone and rescale. Reuse that math rather than writing a
  third copy.

## Scope

1. **Apply native's `MapJoystick` to the mouse deflection** before it becomes
   yaw and pitch: dead zone 0.1 by default, rescale, and the quadratic curve
   by default. Share one implementation with the P380 joystick path. Keep the
   integrating, non-centring virtual-stick behaviour exactly as it is.
2. **Settings** in the existing client settings home (P378's `liveSettings`,
   or the client's equivalent):
   - dead-zone preset (Smallest / Small / Large, with native's values);
   - linear vs quadratic response;
   - mouse sensitivity (native range).

   Desktop defaults are native's. Mention in the report how a player changes
   them.
3. **A virtual-stick indicator on the HUD**, if the port lacks one. Native
   shows the virtual joystick image: a small reticle showing the current
   deflection and the dead-zone circle, so the player can see when they're
   centred. Check what P250-era HUD work already draws before adding it.
4. **Recentre aid** (check native for an equivalent and match it if one
   exists; otherwise make it a named follow-up rather than inventing one).

## Explicitly out of scope

- The W/S throttle ramp (P504) and prediction (P505).
- Gamepad (P499) and touch (P501) curves. If you change a shared helper, they
  must stay behaviourally identical; note the effect.

## Verification

- Pure tests, failing first:
  - a deflection inside the dead zone gives exactly 0 yaw/pitch;
  - just outside, it ramps from 0 with no step;
  - the quadratic and linear curve values;
  - full deflection still reaches ±1;
  - the joystick path's existing P380 tests stay green.
- Whole-solution build with `/home/domingo/.dotnet/dotnet build`, 0
  warnings and 0 errors. Full suite via `scripts/run-tests.sh` (P471), 3
  consecutive runs. Cite `docs/TEST_FLAKES.md` for any remaining flake.
  **Report only numbers you actually measured.**
- **Commit before you finish**, and confirm it with `git log -1`.

## Deliverables

1. Committed on **`master`**, not pushed. Check with
   `git branch --show-current`.
2. The report `docs/reports/P506_MOUSE_FLIGHT_DEADZONE.md`.

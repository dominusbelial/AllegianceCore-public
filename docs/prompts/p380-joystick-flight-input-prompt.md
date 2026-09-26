# P380 — real analog joystick flight input (native SenseJoystick, port of the missing mechanic)

## Why this task exists

Domingo has a real DJI FPV Remote Controller 3, confirmed live this
session to enumerate on Linux as a standard HID joystick with zero
extra drivers (`/dev/input/js1`, kernel-reported `ABS=1f` — 5 real
analog axes, real buttons). This port's MonoGame client currently reads
**one** gamepad button (`Game1.cs:1252`,
`GamePad.GetState(PlayerIndex.One).Buttons.Back`) and nothing else —
`Microsoft.Xna.Framework.Input.Joystick` (MonoGame's real
cross-platform joystick API) is not referenced anywhere in this
codebase. Native has a complete, real joystick flight-control system
that was never ported.

## Confirmed real native mechanic

`WinTrek/WinTrek.cpp:10818-11016`, `SenseJoystick(...)`, called once
per frame while flying and the combat screen has focus
(`WinTrek.cpp:8253`):

- **Axis roles** (`Inc/AGCIDL.idl:400-407`): `Yaw=0, Pitch=1, Roll=2,
  Throttle=3`, hat=4. This is the SAME 4-float order this port's own
  `Allegiance.Core/Sim/ControlData.cs:17`
  `(Yaw, Pitch, Roll, Throttle)` already uses — the port already
  mirrors native's role convention at the data-model level, just has
  nothing feeding it from a real joystick.
- **Deadzone/rescale** — `MapJoystick(float value)`
  (`WinTrek.cpp:10783-10789`): linear, not curved. Inside
  `±deadzone` → 0; outside it, rescaled by `(value ∓ deadzone) /
  (deadzone - 1.0)` so the full ±1 range is preserved past the
  deadzone edge. Real default deadzone =
  `g_fJoystickDeadZoneSmall = 0.1` (also `0.04`/`0.3` presets,
  `WinTrek.cpp:92-99`).
- **Roll/yaw share an axis under a modifier**: axis 0 is Yaw normally,
  Roll while `TK_RollModifier` is held (and Roll's own axis goes to 0
  in that state) — a real, deliberate native behavior, not
  incidental.
- **Throttle is latched, not a direct pass-through**
  (`WinTrek.cpp` ~10855-10866, `trekClient.joyThrottle`/
  `fOldJoyThrottle`): the joystick throttle axis only "takes over"
  from keyboard throttle control once it moves more than the deadzone
  from its last sampled value — otherwise keyboard step commands
  (`TK_ThrottleUp/Down/Zero/33/66/Full`) or match-speed still drive
  throttle. Prevents a resting-but-nonzero throttle axis from fighting
  keyboard taps.
- **Free-look/external camera mode bypasses joystick axes entirely**
  (`bInternalCamera` branch, `WinTrek.cpp:10883-10915`) — roll/yaw/
  pitch fall back to digital ±1 keyboard-only values while external
  camera is active; joystick still works normally once back in
  internal/cockpit view.
- **Buttons are merged, not exclusive**: fire/missile/booster/
  match-speed are read via `IsTrekKeyDown(..., bReadKeyboard)`, a
  unified keyboard-OR-joystick-button check — keyboard and joystick
  buttons coexist, neither excludes the other.
- **Native was fully rebindable, not fixed**: real axis-to-role
  remapping UI (`trekinput.cpp`'s `EditSelection`), persisted to
  `inputmap1`/`defaultinputmap`. The default DirectInput GUID→slot
  convention (`engine/dinput.cpp:726-793`,
  `GUID_XAxis→0(yaw), GUID_YAxis→1(pitch), GUID_Slider/ZAxis→2(roll),
  GUID_RzAxis→3(throttle), GUID_POV→4`) was just the DEFAULT for a
  traditional flight-stick/HOTAS layout — not a hardcoded assumption
  to blindly copy.

**Important, don't skip**: Domingo's actual hardware is a drone RC
transmitter (DJI FPV Remote Controller 3), not a traditional
flight-stick. Drone RCs conventionally use a "Mode 2" stick layout
(left stick = throttle+yaw, right stick = pitch+roll) — a physically
DIFFERENT stick-to-axis arrangement than native's default HOTAS
convention, even though the four logical roles (yaw/pitch/roll/
throttle) are conceptually the same. **Do not assume the DJI
controller's raw axis indices 0-3 map directly to native's
Yaw/Pitch/Roll/Throttle order** — verify empirically with the actual
connected device (move one stick at a time, read which raw axis index
changes) before wiring up a default mapping, or build the mapping as
an easily-editable config from the start given this real uncertainty.

## This port's current flight-input pipeline (confirmed, reuse exactly)

- `Game1.cs` samples `Keyboard.GetState()`, builds a `FlightInputState`
  (`Flight/FlightInput.cs:33-61`, plain booleans:
  `ThrottleForward/Back, StrafeLeft/Right, PitchUp/Down, YawLeft/
  Right, RollLeft/Right, Afterburner, Fire, GroupFire,
  SelectedMount`).
- `FlightInput.Translate(FlightInputState)` (`FlightInput.cs:80-136`)
  is the pure, testable layer that turns that into a real
  `FlightCommand(ControlData(yaw, pitch, roll, -1f), stateM)`.
  Throttle idles at `-1f` (not 0) — a deliberate, already-documented
  contract (see the comment at `FlightInput.cs:126-135` for why);
  respect it.
- **Real, already-working precedent for adding an analog input
  source**: mouse-look. `Game1.cs:1721-1724` calls
  `_mouseFlight.Update(...)` (`Flight/MouseFlightController.cs`) AFTER
  `FlightInput.Translate` runs, then does `cmd.Controls with { Yaw =
  defl.Yaw, Pitch = defl.Pitch }` — overriding specific fields of the
  already-built `ControlData` post-translation, with keyboard as the
  always-present base layer underneath. This is the shape to follow
  for joystick axes too (matching native's own keyboard+joystick
  coexistence via `bReadKeyboard`), not a parallel/separate input
  path.

## Scope

1. **Wire up `Microsoft.Xna.Framework.Input.Joystick`** (confirmed
   available, unused) — detect a connected joystick, read its real
   axis/button state once per frame alongside the existing keyboard
   sample.
2. **Empirically determine (or make trivially configurable) the real
   axis-role mapping** for the actual connected DJI controller before
   assuming native's default HOTAS order — see the callout above. A
   simple named-constant mapping (not a full rebind UI — that's a
   real native feature but out of scope for a first pass, see below)
   is fine, as long as it's clearly one place to edit, not scattered.
3. **Port the real deadzone/rescale math** (`MapJoystick`'s exact
   linear formula, real default `0.1` deadzone) — not an approximation
   or a different curve.
4. **Port the real throttle-latch behavior** — joystick throttle only
   takes over once it moves past the deadzone from its last sample;
   otherwise existing keyboard throttle controls keep working
   unchanged. Respect the existing `-1f` idle-throttle contract.
5. **Integrate as a post-`Translate` override**, matching the
   mouse-look precedent exactly — keyboard remains the always-present
   base layer; joystick axes override specific `ControlData` fields
   when a joystick is connected and its axes are outside deadzone;
   buttons merge with keyboard (fire/missile/booster/etc. work from
   either source), not replace it.
6. **Respect the real free-look/external-camera bypass** — check how
   this port's own camera-mode state (`CameraMode`, already used
   elsewhere this session for chase/cockpit) maps to native's
   `bInternalCamera`, and skip joystick axis override the same way
   native does while in that mode, if this port has an equivalent
   external/free-look mode. If it doesn't have an exact equivalent,
   note that plainly rather than guessing.

## Explicitly out of scope

- A full rebindable-axis settings UI (native's real
  `EditSelection`/`inputmap1` system) — real, but a separate, larger
  feature. A simple, clearly-documented default mapping (ideally
  verified against the actual hardware) is enough for this task.
- Any change to the existing keyboard-only flight path when no
  joystick is connected — must be byte-for-byte unaffected.
- P368-P379 — unrelated, don't touch.

## Verification — real proof, not just build/test

- **Live proof, required**: real client, real DJI FPV Remote
  Controller 3 connected (Domingo will have it plugged in — if it's
  not connected during the delegate session, build against the real
  MonoGame `Joystick` API correctly and clearly flag in the report
  that hands-on stick verification still needs to happen with Domingo
  live, rather than skipping verification silently). Confirm: moving
  each physical stick produces the correct, correctly-signed flight
  response (test each of yaw/pitch/roll/throttle individually);
  keyboard flight controls still work identically with no joystick
  connected; keyboard and joystick buttons both fire weapons
  correctly; the deadzone actually holds a resting stick at zero
  (no drift); throttle latching behaves sensibly (moving the throttle
  stick takes over, keyboard taps still work when the stick is idle).
- Whole-solution build: 0 warnings, 0 errors.
- Full test suite (`/home/domingo/.dotnet/dotnet test`), run at least
  2x: only the pre-existing documented live-wire timing flakes are
  expected.

## Deliverables

1. The joystick flight-input integration, committed on `master` (not
   pushed — Domingo pushes). Do not create a side branch.
2. Report doc `P380_JOYSTICK_FLIGHT_INPUT.md` in the repo root: the
   real axis-role mapping determined/used (and how it was verified
   against the actual hardware, or flagged if it couldn't be), the
   deadzone/throttle-latch math ported, and the live verification
   performed.

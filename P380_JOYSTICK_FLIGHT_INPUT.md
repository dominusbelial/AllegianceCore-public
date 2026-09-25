# P380 — Real analog joystick flight input (port of native SenseJoystick)

Port of the original Allegiance joystick flight-control system into the
MonoGame client, via MonoGame's real cross-platform joystick API
(`Microsoft.Xna.Framework.Input.Joystick` — previously unreferenced anywhere in
this codebase; the client read only a single `GamePad` button, `Game1.cs:1252`).

Status of the live-hardware step is flagged explicitly at the bottom: the DJI
FPV Remote Controller 3 was confirmed connected at the start of this session
and empirically characterized, but it was unplugged before any physical stick
could be moved, so the axis→role mapping and directions are a documented
default that still needs Domingo's hands-on confirmation.

## What was implemented

- **`src/Allegiance.ClientCore.MonoGame/Flight/JoystickFlightController.cs`** (new)
  — pure, graphics-free port of native `SenseJoystick`
  (`WinTrek/WinTrek.cpp:10818-11016`):
  - `MapJoystick` — the exact linear deadzone/rescale
    (`WinTrek.cpp:10783-10789`): inside ±deadzone → 0; outside, rescaled by
    `(value ∓ deadzone) / (deadzone − 1.0f)`. The divisor is
    `g_fInverseJoystickDeadZone = g_fJoystickDeadZone − 1.0f` (a negative
    number, `WinTrek.cpp:98-99`), so past the deadzone edge the full ±1 range
    is preserved *and the sign is inverted* — native's convention, reproduced
    exactly. Real default deadzone `g_fJoystickDeadZoneSmall = 0.1f`
    (`WinTrek.cpp:92-99`).
  - The **throttle latch** (`WinTrek.cpp:10855-10882`): the joystick throttle
    axis only takes over once it moves more than the deadzone from its last
    sampled value (`trekClient.joyThrottle`/`fOldJoyThrottle`); otherwise the
    caller keeps its existing keyboard throttle (this port: FlightInput's W/S
    movement buttons with `ControlData` idling at −1). Once engaged it tracks
    the live value each frame, exactly like native.
  - A **per-axis "active" flag** so the caller overrides a turn axis only when
    it is outside the deadzone — keeping the keyboard arrow value on a centered
    axis (see integration below).
  - `JoystickAxisLayout` — the **single, clearly-labelled place to edit** the
    raw-axis→role mapping and per-axis invert flags, with two presets:
    `NativeHotas` (native's default HOTAS order, `engine/dinput.cpp:726-793`)
    and `DjiFpvRc3` (the default used here; provisional, see Hardware below).

- **`src/Allegiance.ClientCore.MonoGame/Game1.cs`** — wiring as a post-`Translate`
  override, exactly the mouse-look precedent
  (`_mouseFlight.Update(...)` at `Game1.cs:1721-1724`):
  - Each frame (not while composing chat) it asks
    `CurrentJoystickState()` for the first connected joystick (cached index,
    hot-plug re-scanned), gathers the role axes, and merges the result into the
    already-built `FlightCommand`:
    - **Turn axes**: `Yaw/Pitch/Roll` are overridden **only when that axis is
      outside the deadzone** (`jsr.YawActive ? jsr.Yaw : cmd.Controls.Yaw`).
      Keyboard remains the always-present base layer — a resting/centred stick
      changes nothing.
    - **Throttle**: `Throttle = jsr.ThrottleOverride ?? cmd.Controls.Throttle`
      — the latch decides; when not engaged the keyboard/idle −1 stands.
    - **Buttons merge, never replace** (native's unified
      `IsTrekKeyDown(TK_*, bReadKeyboard)` sense): a joystick fire button ORs
      `FlightInput.FireContribution(...)` (respecting the group/single-weapon
      selection) into `stateM`, and a booster button ORs
      `AfterburnerButton` — coexisting with the keyboard Space/Shift bits.
    - The joystick turn-axes-active signal also feeds the autopilot manual-input
      disengage check (native disengages autopilot when a turn axis moves past
      the deadzone, `WinTrek.cpp:8326-8333`), alongside mouse + keyboard.
  - When **no joystick is connected** `CurrentJoystickState()` returns null and
    the block is skipped entirely — the keyboard-only flight path is
    byte-for-byte unchanged.

## Native behaviour faithfully reproduced vs. deliberately simplified

Reproduced exactly:
- `MapJoystick` deadzone/rescale, including its sign inversion and real 0.1
  default (verified: changing the divisor to the non-inverting `1−dz` makes 10
  of the new tests go red — must-fail demonstrated).
- Throttle latch semantics (take-over only past deadzone, tracking while
  engaged; removing the latch orphans the `_joyThrottleEngaged` field and fails
  to compile — the latch is load-bearing).
- The four logical roles yaw/pitch/roll/throttle matching `ControlData`
  (`Allegiance.Core/Sim/ControlData.cs:17` and `AGCIDL.idl:400-407`), and the
  sign convention consistent with `FlightInput`'s keyboard mapping (verified by
  tests that push a "roll right" deflection through the DJI layout and confirm
  it lands in the same signed space as the `D` key).

Simplified (each noted, not guessed):
- **Roll/yaw axis-sharing under `TK_RollModifier`** (native's axis-0-yaw /
  axis-2-roll-with-modifier dance) is not reproduced: it is a HOTAS-era
  compromise tied to native's default DirectInput axis layout
  (`WinTrek.cpp:10899-10905`), not applicable to a Mode-2 transmitter with four
  dedicated analog axes, and there is no roll-modifier binding in this client.
  Each role reads its own raw axis per the config.
- **`m_bFlipY` pitch inversion** (a preference toggled at `WinTrek.cpp:5299`)
  is exposed as `JoystickAxisLayout.InvertPitch` (a per-role constant) rather
  than a runtime-toggled preference — this client has no settings-persistence
  system.
- **No rebind UI** (`EditSelection`/`inputmap1`): out of scope by the task
  brief; the single-editable `JoystickAxisLayout` is the sanctioned substitute.

## Camera-mode (free-look / external) handling — correction to the brief

The brief's item 6 predicted the port should "skip joystick axis override while
in an external/free-look mode". Checking the live native source rather than
taking the summary at face value (per the P-series workflow skill's
verify-positive-claims rule):

- `SenseJoystick` is called with `bNoCameraControl` (`WinTrek.cpp:8223,8253`),
  and `NoCameraControl(cm)` returns true for **InternalCamera(cockpit),
  ExternalMissile, and ExternalChase** — and false only for the true free-look
  external cameras (`Treki.h:140-152`). So in native the joystick axes are read
  in *every* camera mode; the only thing the free-look cameras change is that
  the *arrow keys* steer the camera rather than pitching/yawing/rolling the
  ship, so the digital keyboard-ship overrides inside the `if (bInternalCamera)`
  block are skipped there.
- This port has **no camera-orbit/free-look mode**: its four `CameraMode`s
  (Chase, Cockpit, ExternalTurret, ExternalMissile — `Rendering/CameraMode.cs`)
  are all ship-following views where the arrow keys fly the ship. There is
  therefore **no camera mode in this port that should bypass the joystick axes**,
  and none was added. This is stated plainly rather than guessed, per the
  brief's instruction. If a future stage adds a free-look camera-orbit mode, it
  should then gate the joystick axis override exactly as native does
  (`bNoCameraControl == false`).

## Hardware / empirical status

Confirmed live this session while the controller was connected:
- Enumerates as `/dev/input/js1`, "DJI Virtual Joystick", USB VID:PID `2ca3:1021`
  (zero extra drivers, standard HID joystick).
- Kernel reports `ABS=1f` → five real analog axes (raw indices 0-4); at rest all
  four stick axes (0-3) sat centred at 0 and axis 4 sat at an extreme
  (a spare switch/knob).
- MonoGame's SDL backend enumerates exactly one joystick ("DJI Virtual
  Joystick", **5 axes / 24 buttons**) at index 0 — so `Joystick.LastConnectedIndex`
  / `GetCapabilities(0)` locate it in the running game. `JoystickState.Axes` is
  raw SDL int16 centred at 0; normalization (`÷32767`, clamped to ±1) is applied
  in `GatherRoles`.

Not verified (the controller was unplugged before any stick could be moved):
- Which raw axis index maps to which **physical** control (the Mode-2 arrangement:
  right-stick X→roll, right-stick Y→pitch, left-stick X→yaw, left-stick
  Y→throttle is the default, provisional).
- The **sign/direction** of each raw axis (hence `InvertYaw/Pitch/Roll/Throttle`
  defaults of false are provisional).
- Which of the 24 buttons should be fire/booster (provisional: button 0 = fire,
  button 1 = booster).

**These are the constants to confirm with Domingo.** Everything is in one place
— `JoystickAxisLayout.DjiFpvRc3` (axis indices) + `Invert*` flags +
`JoystickFlightController.FireButton`/`BoosterButton`. A ready-to-run probe that
prints the live numbered axis values/buttons is at `/tmp/dji_probe.py`
(reconnect the RC, run `python3 /tmp/dji_probe.py`, move one stick at a time,
flip the matching invert for any axis that responds backwards).

## Verification performed

- Whole-solution build: **0 warnings, 0 errors**.
- `Allegiance.ClientCore.MonoGame.Tests`: **761 passed, 0 failed** across two full
  runs (includes 32 new `JoystickFlightControllerTests`).
- Must-fail-if-removed (hard requirement): reverting the native sign-inverting
  divisor broke 10 MapJoystick/chain tests; removing the throttle latch orphaned
  its state field (compile failure). Both restored; 32/32 green.
- New tests cover: the exact deadzone/rescale formula and sign inversion,
  inside/at-boundary zeroing, both axis layouts, per-axis invert, short-array
  safety, resting-stick-zero/no-drift, per-axis activation (keyboard preservation),
  the full roll/yaw sign chain against `FlightInput`, and the complete throttle
  latch lifecycle (baseline, engage, tracking, no-release chatter, reset,
  negative side, keyboard-retains-idle-−1).
- `Allegiance.Core.Tests` full suite run **3×** (3355-3356 of 3360 passing each
  time): the only failures are the documented live-wire timing flakes, and the
  exact failing set shifts between runs (AddPartTests, AutoReloadOnEmpty,
  CargoBayReload, LiveRipcord, LiveTurretGunner, LiveWinCondition,
  LiveInSpaceEject, LiveTeamLobby, NpcShipFoundationLive among them) — the
  signature of environmental machine-state flakes, not a regression.
  Conclusively not P380: `Allegiance.Core.Tests.csproj` references only Core /
  Server / Bot / Lobby (not the MonoGame client), and none of the flaky test
  files reference any P380 symbol, so the P380 code cannot influence them.

## Live proof still outstanding (needs Domingo, with the RC connected)

Hands-on verification against the real hardware is **not yet done** — the RC was
unplugged during the session. When Domingo reconnects it:
1. Run `/tmp/dji_probe.py`, move each stick/dial one at a time, and confirm the
   axis index + direction for roll, pitch, yaw, throttle (adjust
   `DjiFpvRc3` + `Invert*` accordingly).
2. Launch a real server + real client with the RC connected and confirm:
   moving each stick produces the correct, correctly-signed flight response
   (yaw/pitch/roll/throttle individually); keyboard flight controls still work
   with no joystick connected; keyboard and joystick buttons both fire; the
   deadzone holds a resting stick at zero (no drift); throttle latching behaves
   sensibly (moving the throttle stick takes over, keyboard taps still work when
   the stick is idle).

## Scope boundary

- Only `Allegiance.ClientCore.MonoGame` client files and their tests changed.
- No server, no wire, no sim changes — joystick input is folded into the existing
  `FlightCommand`/`CommandControls` path exactly as the keyboard already is.
- P368-P379 untouched. The working tree also carries a **pre-existing, unrelated
  uncommitted edit** to `src/Allegiance.Bot/Bot.cs` (an `ExportWarp` accumulator)
  that predates this task; it was left untouched and is not included in the P380
  commit.

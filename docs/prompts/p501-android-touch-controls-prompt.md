# P501 — Android touch flight controls (virtual sticks + action buttons) on the existing flight-command pipeline

## Why this task exists

The Android head (P499, `bf4fc912`) is **gamepad-only**. Without a paired
controller, a phone user can't fly. The flight pipeline is already
input-agnostic. Gamepad input is merged into the per-frame `FlightCommand` by
`ApplyGamePadFlight` (`src/Allegiance.ClientCore.Game/Game1.FlightInput.cs:~111-135`),
using `GamePadFlightRoles` / `GamePadFlightInput`
(`src/Allegiance.ClientCore.Game/Flight/GamePadFlightInput.cs`). It reuses
P380's joystick deadzone math and P445's persistent-throttle model. Touch
should feed the **same** `FlightCommand` / `ControlData` the same way.

Native Allegiance had no touch input, so this is a port extension. Keep it
faithful to the **native control semantics** (yaw, pitch and roll axes; held
thrust versus persistent throttle; fire; afterburner) even though the input
device is new.

## Scope

1. **A pure touch-layout and logic module, testable without a graphics
   device:**
   - a **left virtual stick**: roll and throttle, mirroring the gamepad
     layout, or whatever P499's default layout uses for the left stick;
   - a **right virtual stick** (or drag-to-aim area): yaw and pitch, with
     deadzone and rescale like `MapJoystick` / P380;
   - **buttons**: fire (held), afterburner (held), missile fire,
     target-cycle (native `T`), autopilot (native `G`), throttle step up and
     down (P445), and dock/shop or menu where they're reachable.

   It handles multi-touch with per-finger ownership: a finger that starts on
   a control keeps it until lifted. Positions are relative to the viewport,
   scaling to phone and tablet aspect ratios.
2. **Wiring:** read `TouchPanel.GetState()` once per frame and merge into
   `FlightCommand` exactly like `ApplyGamePadFlight`. Precedence must be
   clear and documented: an active gamepad, then touch, then keyboard on
   desktop. On desktop with no touch, behaviour is unchanged.
3. **Overlay rendering:** semi-transparent sticks and buttons in the HUD
   pass. They're hidden when a gamepad is connected, and they must not cover
   critical HUD gauges. Check the HUD layout at a phone resolution and
   report what overlapped.
4. **Docked, shop and investment screens** are usable by taps: list rows,
   buy, invest, launch. Reuse existing layout hit-rects where they exist.

5. **Pre-flight screens must be usable on a phone. This is a blocker found
   on the emulator, 2026-10-02.** Side select (`UpdateSideSelect`: Up / Down
   / Enter) and the ready check (`UpdateReadyCheck`: **`Y`** toggles ready,
   **`O`** is the owner's force-team-ready, `Game1.Panes.cs`) are
   **keyboard-only**, with **no gamepad button and no tap target**. On the
   emulator, the server auto-assigned the Android player a side after 30 s,
   but the player **could not ready up**. In addition, **key input doesn't
   reach the game on Android at all**: `adb shell input keyevent KEYCODE_Y`,
   including `--longpress`, produced no `ready-check` log line, while the
   same key works on desktop.
   - Add tap targets (ready / unready, force-ready for the owner, and side
     rows plus confirm) and gamepad buttons (A = ready/confirm, D-pad =
     side choice, X = force-ready for the owner).
   - Diagnose and fix why Android hardware-keyboard events don't reach
     MonoGame's `Keyboard.GetState()`: view focus, `MainActivity` key
     handling, or the MonoGame.Android input path. A Bluetooth keyboard
     must also work. Verify on `p499emu` with injected key events.

## Explicitly out of scope

- Gyro aiming. That's a named follow-up.
- General HUD and UI scaling for small screens, beyond avoiding overlap.
  That's a follow-up.
- The connect screen (P500).

## Verification

- Pure-layout and logic tests (no graphics device) that fail first, where
  applicable.
- Whole-solution build with `/home/domingo/.dotnet/dotnet build`, 0
  warnings and 0 errors. Full suite via `scripts/run-tests.sh` (P471), 3
  consecutive runs. Cite `docs/TEST_FLAKES.md` for any remaining flake.
- **An Android build that includes the assets.** Run
  `source ~/.android_env.sh`, then `scripts/build-android-head.sh`, or build
  the DesktopGL head, run `scripts/pack-android-assets.sh`, then build the
  Android head. A fresh checkout otherwise yields a 45 MB APK with **no
  assets**. Confirm the APK contains `assets/Content` and `assets/artwork`.
- Run it on the `p499emu` emulator (P499 set it up) if you can, and record
  what you observed. **Report only numbers and outcomes you actually
  measured.**
- **Commit before you finish**, and confirm it with `git log -1`.

## Deliverables

1. Committed on **`master`**, not pushed. Check with
   `git branch --show-current`.
2. The report `docs/reports/P501_ANDROID_TOUCH_CONTROLS.md`.

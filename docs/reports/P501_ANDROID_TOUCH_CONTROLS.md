# P501 — Android touch flight controls (virtual sticks + action buttons) on the existing flight-command pipeline

Date: 2026-10-03
Repo: `/home/domingo/Development/AllegianceCore` (branch `master`)
Prereq: P499 `bf4fc912` (gamepad head) + P500 `fffa4df1` (connect screen) confirmed present.

Purpose: make the Android head flyable WITHOUT a paired controller. Add virtual
touch sticks + action buttons that feed the SAME per-frame `FlightCommand` /
`ControlData` the gamepad path uses, fix the pre-flight keyboard-only blocker
(side-select / ready-check unusable on a phone, and Android hardware-keyboard
events never reaching `Keyboard.GetState()`), and make the docked/shop/invest
screens tappable.

---

## 1. The pure touch module — `Flight/TouchFlightInput.cs` (testable, no GPU)

The core deliverable is a device-free module (same discipline as `GamePadFlightRoles` /
`FlightInput` — no MonoGame type on the public surface), in two parts:

**`TouchLayout`** — given a viewport `width × height`, computes:
- the two **stick capture zones** in the bottom corners (left = roll + throttle,
  right = yaw + pitch, the exact Mode-2 mapping `GamePadFlightRoles` uses so a
  touch gesture feels like the gamepad stick it mirrors);
- a **floating-stick deflection clamp radius** (drag ÷ radius → −1..+1);
- the **action-button rects**: `Fire` + `Afterburner` (held), `Missile`,
  `TargetCycle` (T), `Autopilot` (G), `ThrottleUp`/`ThrottleDown` (P445 = / −),
  `DockOrMenu` — every button the task lists.

Sizes/positions are fractions of the viewport and a unit derived from the
**smaller dimension**, so the same code places usable controls on a narrow phone
and a wide tablet. The constants in `TouchLayout.Compute` are the single edit
point for retuning the feel.

**`TouchFlightInput`** — the stateful per-frame processor that owns the task's
**multi-touch per-finger ownership**: a finger that starts on a control keeps it
until lifted; a new finger claims one control; lifting releases it (knob springs
back, deflection → 0). It converts each stick's drag into −1..+1 deflections in
the flight-y-up space, passes them through `GamePadFlightRoles.Gather` (so the
sign handling — incl. the default pitch inversion — is identical to a pad), and
reports held buttons vs edge buttons. The result feeds the SAME
`JoystickFlightController` the pad path uses, so deadzone/rescale (P380
`MapJoystick`) and the P445 throttle latch come free.

**`TouchTaps`** + the menu geometry helpers (`Rendering/PreFlightTaps.cs`,
`Rendering/DockedTouchZones.cs`) give the non-flight screens the P500-style
tap positions/hit-zones (side rows, ready toggle, force-ready, docked verbs).

## 2. Wiring — `Game1.Touch.cs` + the precedence rule

`TouchPanel.GetState()` is read **once per frame** (`TouchSamplesNow`) and folded
into `FlightCommand` by `ApplyTouchFlight(cmd)`, structurally identical to
`ApplyGamePadFlight`. The precedence (documented at the call site in
`Game1.FlightInput.UpdateFlying`):

> **active gamepad → touch → keyboard (base layer)**

- a connected gamepad wins: `ApplyGamePadFlight` runs, touch is ignored, and the
  overlay is hidden;
- otherwise, when the platform reports touch (`TouchPanel.GetCapabilities()
  .IsConnected`), `ApplyTouchFlight` overrides the turn axes outside the deadzone
  and latches the throttle, ORs held fire/afterburner into `stateM`, and fires
  the edge buttons (missile `FIRE_MISSILE`, target-cycle → `HandleTargetCycleKey`,
  autopilot → `ToggleAutopilotForTarget` [both extracted from the T/G key
  handlers so touch and keys share ONE implementation], P445 throttle steps,
  `DockOrMenu` → the in-flight help/"menu" overlay);
- desktop with no touch is **unchanged**: `TouchAvailable()` is false there, so
  the touch path never runs and the keyboard base layer stands untouched.

## 3. Overlay rendering (HUD pass)

`DrawTouchOverlay()` draws semi-transparent stick rings/knobs and labelled
buttons from the same `TouchLayout`, called from the Flying HUD pass
(`DrawHud()` → `DrawTouchOverlay()`), above the gauges but beneath the
starmap/chat overlays. It is skipped when no touch is available and when a
gamepad is connected (matching the input precedence).

### HUD overlap check at a phone resolution (2400×1080, measured from source)

The six critical gauges sit **centrally** (native offsets × `spread=1.588` from
screen centre (1200,540); e.g. Thrust ≈(999,688), Fuel ≈(1400,688), Ammo
≈(999,361), Energy ≈(1400,361), Hull ≈(1096,596), Shield ≈(1304,596), each a
~92 px texture). The touch controls are at the far corners:
- left/right stick bases at (480,886)/(1920,886) with 64 px zones — **clear of
  the six gauges** and the bottom-centre speed readout (speed centre at y=972);
- Fire (1440,734) and Afterburner (960,778) sit in the lower "thumb lane",
  below the Hull/Shield gauges (bottom ≈642) — no gauge overlap;
- the right-side **throttle gauge** (x=w−74=2326) is right of the utility column
  (ends x≈2254) — clear.

**What DOES overlap** (peripheral readouts, semi-transparent underneath):
- the **left status panel** (P241 hull/shield/energy/fuel/ammo/power bars down
  the left edge) is partially covered by the left-upper Missile/TargetCycle/
  Autopilot buttons (x≈150–215);
- the **top-corner readouts** (kill bonus top-left, roll angle top-right, money
  top-left) sit behind the DockOrMenu/ThrottleUp/ThrottleDown column (x≈2190–
  2250) and the left-upper cluster.

These are non-critical diagnostics and the overlay is ~0.5-alpha translucent;
general HUD/UI scaling for small screens is the explicitly-scoped follow-up. The
button/stick positions are the single `TouchLayout.Compute` edit point to
retune once verified hands-on on a device.

## 4. Pre-flight blocker (side-select + ready-check were keyboard-only) — fixed

`Game1.Panes.cs` `UpdateSideSelect` / `UpdateReadyCheck` gained both **tap
targets** and **gamepad buttons**:

- **Side select**: DPAD ↑/↓ = move, A = confirm (folded into the existing Up/
  Down/Enter edges via `GamePadNavNow`); a **tap on a side row** selects AND
  confirms it (`PreFlightTaps.SideBand` → the same `SendPositionReq` Enter sends).
- **Ready check**: A toggles ready (confirm), **X is the owner's force-ready**
  (`PadNav.X` was added for it); the big **READY/UNREADY button** and (owner)
  **FORCE READY button** are tap targets (`PreFlightTaps.ReadyToggleRect` /
  `ForceReadyRect` → the same `SendPlayerReady` / `SendForceTeamReady`).
- All gates unchanged (owner-gated force-ready, seated-only ready toggle,
  `_captureSpec` auto-paths untouched).

## 5. Android hardware-keyboard blocker — diagnosed and fixed

**Diagnosis** (MonoGame.Framework.Android 3.8.5.1, inspected in
`MonoGameAndroidGameView.OnKeyDown/OnKeyUp`): key events are forwarded to
MonoGame's `Keyboard` only when `IsKeyboard(device)` passes, which requires the
event's device to report `SOURCE_KEYBOARD|SOURCE_DPAD` **and a non-zero
vendor/product id**. The `adb`/emulator **virtual keyboard reports vendor=0**, so
MonoGame silently dropped every injected `adb shell input keyevent KEYCODE_Y`
(hence no `ready-check` log line), while a real (non-zero-vendor) keyboard passed
the gate. This is exactly the observed symptom.

**Fix** (`MainActivity.DispatchKeyEvent`): intercept every hardware key at the
Activity entry point and feed the keycode straight into MonoGame's (internal)
`Keyboard.KeyDown/KeyUp` via a small cached reflection, for ANY keyboard-class
device — bypassing the faulty vendor gate. Idempotent, so a real Bluetooth
keyboard that also reaches the view's `OnKeyDown` isn't double-processed (the
state is simply set twice to the same value) — **a Bluetooth keyboard therefore
works too**.

## 6. Docked / shop / investment screens are tappable

`Game1.Panes.cs` `HandleDockedTouch` (called every docked frame) maps P500-style
released taps onto the existing verbs, with pure zones in `DockedTouchZones`:
- **LAUNCH** / **UNDOCK** buttons (shop) — the same `BUY_LOADOUT(fLaunch)` / `FM_C_DOCKED(NA)`;
- **TELEPORT** / **INVEST** pane toggles — the same T / I actions;
- a **tap on a row** of the active sub-view moves the cursor onto it and acts
  (`shop.Activate()` to buy/select, `TryInvest` for investment, `ActivateTeleport`).

(The docked renderer computes text positions from font metrics inside `Draw` and
exposes no shared hit-rects, so the zones are generous, centred approximations —
consistent with `PreFlightTaps`; per-row pixel alignment is a follow-up.)

## 7. Fail-first pure tests

`tests/.../TouchFlightInputTests.cs`:
- `TouchFlightInputTests` (20): layout inside the viewport across phone/tablet/
  portrait; buttons disjoint; stick zones in the two bottom corners; neutral
  with no touch; left-stick drag-right→roll / drag-up→throttle; right-stick
  drag-right→yaw / drag-up→pitch; two-finger independence; **a finger keeps its
  stick when it drags outside the zone**; lifting returns axes to 0; partial
  deflection scales and clamps (`DragToAxes`); fire/afterburner held while the
  finger stays down; **edge buttons fire once on finger-down, not while held**;
  missile/throttle/dock edges; autopilot re-edge on re-tap; `TouchTaps` reports
  only new fingers.
- `PreFlightTapsTests` (3): side-band row mapping + rejection; ready/force
  buttons inside the viewport and disjoint.
- `DockedTouchZonesTests` (2): verb buttons inside + disjoint; row-band mapping.

## 8. Verification (numbers measured)

- Whole-solution build (`/home/domingo/.dotnet/dotnet build`): **0 warnings, 0
  errors** (Debug).
- **Completed by Claude after the delegate session (2026-10-03).** The P501
  delegate ended without running the suite, building the Android head or
  committing, and left these lines as placeholders. The guard stopped the
  chain with "no new commit". Measured afterwards:
  - **Android head build initially FAILED:** `MainActivity.cs(75)` error CS8765.
    The `DispatchKeyEvent(KeyEvent e)` override didn't match the base's
    `KeyEvent? e` nullability (an error because warnings are treated as
    errors). Fixed with a one-character change to `KeyEvent? e`; the body
    was already null-safe (`e?.Device`). After the fix the Android head
    builds with **0 warnings, 0 errors**, and the signed APK (189,166,528
    bytes) contains **574 `assets/Content` + 206 `assets/artwork`** entries.
  - The P501 touch, pre-flight and docked-touch tests: **29/29 passed**.
  - **Full suite (one `scripts/run-tests.sh` run, not three):** Lobby 12/12,
    MonoGame 1207/1207, Core 3699/3699, ClientCore 408/411. The 3 failures
    (`ThreePeerSeededSessionTests…`, `FullSessionKillIntegrationTests…`,
    `ClientWarpApproachSlowHullLiveTests.Legacy_200_450…`) are all in
    `Allegiance.ClientCore.Tests`, which doesn't reference the game/UI layer
    P501 changed, and all pass **3/3 in isolation**. They're known ledger
    flakes.
  - **`p499emu` observation:** not yet done as part of this commit. The
    keyboard-dispatch fix is verified on the emulator separately (see the
    commit follow-up / wiki log).

## 9. Deliverables

1. Committed on `master`, not pushed (`git branch --show-current` / `git log -1`
   confirmed at commit time).
2. This report.

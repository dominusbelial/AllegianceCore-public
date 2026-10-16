# P445 — native persistent throttle: step keys, presets, mouse wheel, start at full

## Why this task exists

Domingo: "ship navigation … does not feel as good as the original." A
gameplay-parity pass found the MonoGame client has **no persistent throttle
at all**. The flight physics is a faithful port, but the control scheme
feeding it is not.

- **Native has two separate forward-motion systems.**
  - **Held thrust keys.** `TK_ThrustForward/Backward/Left/Right/Up/Down`
    each OR a movement bit into `buttonsM` while held
    (`~/Development/Allegiance/src/WinTrek/WinTrek.cpp:8255-8270`).
  - **A persistent throttle axis**, `trekClient.trekThrottle` in [-1, 1].
    `TK_ThrottleUp`/`TK_ThrottleDown` step it by ±0.2, clamped to
    [-1, 1] (`WinTrek.cpp:10515-10527`, `10917-10935`). `TK_ThrottleZero`,
    `TK_Throttle33`, `TK_Throttle66` and `TK_ThrottleFull` set it to
    -1, -0.3, +0.3 and +1 (`WinTrek.cpp:10939-10960`). It is sent every
    frame as `jsValues[c_axisThrottle]` (`WinTrek.cpp:10985`).
  - The sim's cruise branch turns throttle into a target speed,
    `-0.5*(1+throttle)*maxSpeed`: -1 means stop and +1 means full
    (`Igc/shipIGC.cpp:2990-2998`, ported faithfully in
    `src/Allegiance.Core/Sim/Ship.cs`'s `ExecuteMove` cruise branch).
  - On-line play initializes the throttle to **full (1.0)**. See the comment
    at `WinTrek.cpp:10860-10862` ("it is 1.0f for on-line play, and -1.0f
    for off-line") and `bInitTrekThrottle`.
  - The **mouse wheel** also steps the throttle, paced by the `WheelDelay`
    preference (`WinTrek.cpp:3052`, `8246-8250`).
- **Native's default bindings**, decoded from `~/Development/Artwork/defaultinputmap.mdl`
  (triples are (VK, modifier, TK id)) against the TK enum
  (`Igc/igc.h:226-451`, e.g. `TK_ThrottleUp = 58`):
  - Throttle: `=` (VK 187) → ThrottleUp(58), `-` (189) → ThrottleDown(59),
    Backspace (8) → ThrottleZero(60), `\` (220) → ThrottleFull(63).
    Throttle33/66 are unbound by default.
  - Mouse wheel: `mouseButtonCommandMap` binds 9 → 58 and 8 → 59, so wheel
    up/down steps the throttle.
  - Thrust keys: Tab/S forward/back, A/D left/right, W/X up/down,
    Space booster, L lock-vector.
- **The port**:
  - `Flight/FlightInput.cs`'s `Translate` hard-codes the throttle axis to
    **-1** every frame (`FlightInput.cs:~147`, `new ControlData(yaw, pitch, roll, -1f)`).
    The comment says -1 was chosen so that releasing every key stops the
    ship.
  - W/S are held Forward/BackwardButton bits (`Game1.FlightInput.cs:590-591`).
  - Nothing in `src/Allegiance.ClientCore.MonoGame` reads
    `Mouse.GetState().ScrollWheelValue`.
  - `=` and `-` are currently bound to music volume
    (`Game1.FlightInput.cs:234-239`).
  - The net effect is that you can't set a cruise speed and fly with the
    mouse. Releasing W brakes you to a stop.

## Scope

1. **Client-local persistent throttle state**, a float in [-1, 1] owned by
   `Game1` next to `_groupFire` and `_mouseFlightEnabled`. Pass it into
   `FlightInput.Translate` through `FlightInputState` (a new field). Send it
   as `ControlData.Throttle` instead of the hard-coded -1. Keep `Translate`
   pure and unit-testable.
2. **Initialize it to +1 (full)** when the local ship launches or undocks
   into flight, per native's on-line default. Document exactly where you
   reset it.
3. **Step and preset keys**, edge-triggered (one step per press, not per
   frame):
   - ThrottleUp/Down ±0.2, clamped to [-1, 1] with native's exact
     `< 0.8 ? +0.2 : 1.0` / `> -0.8 ? -0.2 : -1.0` rule.
   - Zero → -1, 33% → -0.3, 66% → +0.3, Full → +1.
   - **Key choice:** native uses `=`, `-`, Backspace and `\`. Audit the
     MonoGame key map for conflicts: `=`/`-` are music volume, and Backspace
     is used somewhere, so check `Game1*.cs`. Use native's keys where they
     are free. Where they aren't, move the less important binding or pick a
     free key, and document every choice in a small table in the report.
     Bind 33%/66% to free keys, or leave them unbound as native does, and
     say which.
4. **Mouse wheel**: each wheel notch steps ThrottleUp/Down, the same
   function as the keys. Pace repeats like native's `WheelDelay` default
   (preference 2, see `WinTrek.cpp:3052` and the `delay[]` table near
   `:8246`; find the real delay value). Don't add a settings UI.
5. **W/S stay held thrust**, as native's `TK_ThrustForward/Backward` do.
   The sim already gives movement buttons priority over cruise
   (`Ship.ExecuteMove`'s manual branch). So holding W is full manual thrust,
   and releasing it returns the ship to the cruise throttle instead of
   stopping.
6. **HUD throttle readout**: a minimal indicator of the current throttle
   setting, reusing the existing HUD gauge style. Map it to 0–100% of cruise
   speed, i.e. `0.5*(1+throttle)`.
7. **Autopilot interplay**: autopilot already sends its own throttle
   (`Game1.FlightInput.cs:~990-995`). Leave that path alone, but confirm
   that disengaging autopilot resumes the player's persistent throttle
   rather than snapping to -1.

## Explicitly out of scope

- Re-mapping the rest of the key layout to native's defaults (Tab forward,
  A/D strafe, W/X vertical, and so on). That's a separate decision for
  Domingo. Only the throttle keys and wheel are added here.
- Vector-lock (`TK_LockVector`) and vertical thrust (`TK_ThrustUp/Down`).
  They're real gaps, but a separate small task.
- Joystick throttle (P380 already handles it through `ThrottleOverride`).
  Just make sure a real joystick throttle still overrides the keyboard
  throttle once moved, as P380 intended.
- Any server or sim change. The cruise branch is already correct.
- Other queued work: P438–P440, P446–P449, and P441/P450 in other areas.

## Verification

- Unit tests (pure, no graphics device, following `FlightInput`'s existing
  test style):
  - `Translate` sends the passed-in throttle, not -1.
  - The step rule: +0.2 steps clamp to 1.0 from 0.8 and above, with the
    mirror case at the bottom.
  - The four presets.
  - Initial value +1 on launch.
  - Movement buttons plus a throttle compose correctly (stateM bits set and
    throttle still sent).
- **Write the throttle-sent test first and run it against the unfixed code.
  It must fail (throttle is always -1). Record that output in the report.**
- A sim-level test using the real `Ship.ExecuteMove`: with no movement
  buttons and throttle +1, the ship converges to max speed. With throttle
  -1 it converges to 0. This proves the persistent throttle actually cruises.
- If you can, a short live run against the local server showing the ship
  keeps cruising after W is released. Record log lines or `__`-style
  evidence.
- Whole-solution build with `/home/domingo/.dotnet/dotnet build`, 0
  warnings and 0 errors. Bare `dotnet` is a broken snap stub, so always use
  the full path.
- Full suite with `/home/domingo/.dotnet/dotnet test`, 3 consecutive clean
  runs. Only the pre-existing documented live-wire timing flakes are
  acceptable, and each one seen must be named.

## Deliverables

1. The change and tests, committed on **`master`**, not pushed (Domingo
   pushes). Check `git branch --show-current` before committing.
2. Report doc `docs/reports/P445_PERSISTENT_THROTTLE.md`. Include the
   pre-fix failing test output, the final key-binding table (native key vs
   port key, and what moved), the wheel pacing value and its source, and
   where the throttle initializes.

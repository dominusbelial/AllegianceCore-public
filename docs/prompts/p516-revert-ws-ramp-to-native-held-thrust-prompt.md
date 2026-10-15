# P516 — revert W/S to native held thrust (undo P504's W/S throttle ramp); keep P504's wheel and `\` fixes

## Why this task exists

**Domingo's decision (2026-10-03), reversing his own 2026-10-02 choice:**
"revert the w/s keys throttle acceleration thing we did to match the mouse
wheel, match the original client again."

P504 (`39e5faae`, `docs/reports/P504_WS_THROTTLE_RAMP.md`) made W/S **ramp the
persistent throttle while held**. Its §1 lists what that removed from W/S:
- native momentary manual thrust (`ForwardButton`/`BackwardButton` movement
  bits, formerly set in `FlightInputState`/`Translate`);
- **reverse thrust** with S held;
- **S-to-brake**.

**Native** (`~/Development/Allegiance/src/WinTrek/WinTrek.cpp`):
- W/S are `TK_ThrustForward` / `TK_ThrustBackward`, momentary manual
  thrust.
- The persistent cruise throttle (`trekClient.trekThrottle`, −1 = zero
  thrust … +1 = full) is separate. It's stepped by `=` / `-` ±0.2
  (`:10515-10530`, `:10915-10935`), set by presets (Backspace = zero,
  `\` = full, `:10936-10962`), and driven by the mouse wheel.
- Online play starts at full cruise (`trekigc.cpp:2922`).

P445 ported exactly this; see `docs/reports/P445_PERSISTENT_THROTTLE.md`
§2.6: "W/S stay held thrust … on release the ship returns to the persistent
throttle".

## Scope

1. **Restore native W/S:** held W sets `ForwardButton` and held S sets
   `BackwardButton`, giving manual thrust, reverse and brake exactly as before
   P504. The sim's manual branch overrides cruise while held, and releasing
   returns to the persistent throttle. Restore the `FlightInputState` /
   `Translate` fields P504 removed, rather than inventing new ones.
   `KeyBindingTable` and the in-game help must describe W/S as native thrust
   again.
2. **Remove P504's W/S ramp code** and its tests, or convert the tests to
   assert the native behaviour. No dead ramp code may remain.
3. **KEEP the parts of P504 that fix real, native-correct bugs:**
   - the **mouse-wheel direction** fix: scroll down steps the throttle down,
     native wheel-down → ThrottleDown;
   - **`\` full throttle** bound to both `OemPipe` (US) and `OemBackslash` (ISO);
   - the live HUD throttle readout.
4. **Gamepad (P499) and touch (P501) throttle paths** must keep working.
   Check that neither relied on the P504 ramp.
5. **Record the reversal** in the report, for whoever reads P504's report
   next.

## Explicitly out of scope

- Any other P504/P505/P506 behaviour.
- The own-ship model (P507/P508).
- Making it a setting. Domingo chose native, full stop.

## Verification

- Failing-first tests, which fail on the P504 behaviour:
  - holding W sets `ForwardButton` and leaves the persistent throttle
    unchanged;
  - holding S sets `BackwardButton` (reverse/brake);
  - release returns to cruise;
  - the wheel and `=`/`-`/Backspace/`\` still drive the persistent throttle
    as P504 left them.
- Whole-solution build with `/home/domingo/.dotnet/dotnet build`, 0
  warnings and 0 errors. Full suite via `scripts/run-tests.sh` (P471), 3
  consecutive runs. Cite `docs/TEST_FLAKES.md` for any remaining flake.
  **Report only numbers you actually measured.**
- **Commit before you finish**, and confirm it with `git log -1`.

## Deliverables

1. Committed on **`master`**, not pushed. Check with
   `git branch --show-current`.
2. The report `docs/reports/P516_REVERT_WS_TO_NATIVE.md`.

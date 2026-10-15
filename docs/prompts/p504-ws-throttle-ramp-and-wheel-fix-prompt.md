# P504 — W/S ramp the persistent throttle while held (Domingo's control scheme); fix the mouse-wheel direction and the `\` full-throttle key

## Why this task exists

**Domingo's live playtest feedback (2026-10-02):** "ship keeps auto accelerating
… i like persistent but i should be able to stop it", then "w/s should have
the same effect as the mouse wheel". **Decision (Domingo chose it explicitly):
W/S ramp the persistent throttle while held.** Holding W raises the cruise
throttle and holding S lowers it toward zero; releasing **keeps** the
setting.

**This deliberately deviates from native**, and the deviation is Domingo's
call. In native, W/S are momentary manual thrust (`TK_ThrustForward`/`Backward`),
while the persistent throttle (`trekClient.trekThrottle`, −1 = zero thrust …
+1 = full) is stepped by `=`/`-` ±0.2 (`WinTrek.cpp:10515-10530, 10915-10935`),
presets (`:10936-10962`) and the wheel. Native starts online play at full,
via `OverrideThrottle(1.0f)` (`trekigc.cpp:2922`; offline training uses −1,
`Training.cpp:96`; the comment is at `WinTrek.cpp:10860-10861`). P445 ported
the native scheme (`docs/reports/P445_PERSISTENT_THROTTLE.md`).

**Current code** (build `1d4bc361`; post-P498 the paths are under
`src/Allegiance.ClientCore.Game/`):
- `Game1.FlightInput.cs` ~640-680: `=`/`-`/Backspace/`OemBackslash` →
  step up/down, zero, full. Each logs `[monogame] throttle …`.
- ~680-713: the wheel. It accumulates `Mouse.GetState().ScrollWheelValue`
  deltas in 120-unit notches, paced at 22 ms.
- ~715+: `FlightInputState(ThrottleForward: W, ThrottleBack: S, …)`. W/S
  are held thrust that sets StateM Forward/BackwardButton.
- The launch reset to full is at ~228 (ReadyCheck→Flying), plus undock in
  `HandleDockedScreen`.

**Two input bugs seen in the same playtest:**
1. **Mouse wheel goes only one way.** Domingo's session log
   (`~/monogame-client-run.log`) contains only `throttle wheel UP` lines,
   with no DOWN, and he reports the wheel "keeps going to 100%". The code
   maps the sign correctly, so this is likely a MonoGame DesktopGL / SDL
   `ScrollWheelValue` sign or accumulation quirk on Linux/Wayland. **Confirm
   with evidence first**: log raw `ScrollWheelValue` deltas per frame while
   scrolling each way.
2. **The `\` full-throttle key** is bound to `Keys.OemBackslash`. That's the
   extra ISO key; on US layouts `\` is `Keys.OemPipe`, so "full" is likely
   unreachable.

## Scope

1. **W/S ramp:**
   - While W is held, `_throttle` rises toward +1; while S is held, it falls
     toward −1 (zero thrust). Use a continuous, frame-rate-independent rate
     (full range in about 1.5-2 s, stated and easy to tune). Releasing keeps
     the value.
   - Use the same `_throttle` that the wheel and `=`/`-` drive, so all
     inputs compose.
   - Respect the chat/menu `composing` gate exactly as the other throttle
     keys do.
   - W/S **no longer** set manual Forward/BackwardButton thrust. Check what
     that removes, such as reverse thrust and any S-to-brake use, and
     report it. Strafe (Q/E) and the axes are unchanged.
   - Log throttle changes at a sane rate: on release or ~4 Hz, not every
     frame.
2. **Wheel direction:** diagnose (raw deltas each way), then fix at the root
   so scroll-down steps the throttle down. If the platform really reports a
   wrong sign or monotonic values, derive the direction robustly, and say
   exactly what you observed.
3. **`\` full:** bind both `OemPipe` and `OemBackslash`.
4. **The HUD throttle readout** must reflect the ramp live. Check that one
   exists.
5. **Gamepad (P499) and touch (P501, if landed):** make sure their throttle
   paths still compose with the new W/S behaviour. Change nothing else there.

## Explicitly out of scope

- The prediction desync (P502) and dust (P503).
- Making native's held-thrust W/S a selectable option. Note it as a possible
  follow-up only.

## Verification

- Pure tests (no graphics device), failing first:
  - holding W for N seconds of simulated frames ramps by the expected
    amount, independent of frame rate;
  - releasing keeps the value;
  - S ramps toward −1 and clamps;
  - the composing gate blocks ramping;
  - the wheel direction mapping is right under the observed platform values.
- Whole-solution build with `/home/domingo/.dotnet/dotnet build`, 0
  warnings and 0 errors. Full suite via `scripts/run-tests.sh` (P471), 3
  consecutive runs. Cite `docs/TEST_FLAKES.md` for any remaining flake.
  **Report only numbers you actually measured.**
- **Commit before you finish**, and confirm it with `git log -1`.

## Deliverables

1. Committed on **`master`**, not pushed. Check with
   `git branch --show-current`.
2. The report `docs/reports/P504_WS_THROTTLE_RAMP.md`. Cover the wheel
   diagnosis evidence and what W/S no longer does.

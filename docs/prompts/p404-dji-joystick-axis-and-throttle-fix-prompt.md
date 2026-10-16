# P404 — DJI FPV RC3 right-stick axis swap + joystick throttle polarity

## Why this task exists

Found by a full read/bash/edit/write kimi-k3 review of the 3D/flight/
controls/navigation/HUD surface (2026-09-27, full report at
`~/Development/allegiance-tools/reference/pi-3d-flight-review-kimik3-20260927.log`),
run specifically to chase down Domingo's live report: *"i think the
right stick axis needs to be rotated 90 degrees right, feels like what
is left and right should be up and down, and what is up needs to be
right and right needs to be down."* This is real, reproduced against
the port's own production code (a scratch probe driving `GatherRoles`+
`MapJoystick`), and comes bundled with a second, independently-confirmed
bug in the same file/same live-verification session.

## Confirmed finding 1 — Pitch/Roll axis indices swapped

`JoystickFlightController.cs:89-90`:
```csharp
public static JoystickAxisLayout DjiFpvRc3 { get; } = new(
    YawAxis: 3, PitchAxis: 0, RollAxis: 1, ThrottleAxis: 2);
```
The layout's own doc comment states the intent: right-stick X→roll,
right-stick Y→pitch. The current constants only satisfy that if raw
axis 0 is the right stick's vertical and axis 1 its horizontal —
exactly what Domingo's report falsifies (a Pitch/Roll swap: axis 0 is
really right-X, axis 1 is really right-Y). The review's probe, driving
the real production mapping code with standard Linux/evdev polarities,
confirms the CURRENT mapping produces right-stick-RIGHT → nose-up and
right-stick-UP → roll-left — exactly the felt 90° rotation Domingo
described. The pure index swap (no invert flags needed) produces the
correct Mode-2 response: right→roll-right, up→nose-down (stick-push
convention, matching keyboard Up).

**Fix**: `JoystickFlightController.cs:90` →
```csharp
public static JoystickAxisLayout DjiFpvRc3 { get; } = new(
    YawAxis: 3, PitchAxis: 1, RollAxis: 0, ThrottleAxis: 2);
```
(Keep `YawAxis: 3`/`ThrottleAxis: 2` — those left-stick axes are not
contradicted by Domingo's report, which was about the right stick
only.) Update the three tests that encode the pre-fix physical labels
so they describe the corrected mapping, not just pass numerically:
`tests/Allegiance.ClientCore.MonoGame.Tests/
JoystickFlightControllerTests.cs:58-70` (`GatherRoles_DjiFpvRc3_…`,
comment "raw[0]=pitch (right stick vertical), raw[1]=roll" — now
backwards), `:95-106` (reads index 0 as "DJI pitch"), `:161-176`
(`Chain_RollRightStick_…` pushes raw[1] as "right stick right" — under
the corrected mapping that raw index is vertical; make it push raw[0]
instead).

## Confirmed finding 2 — joystick throttle inverted relative to native

Native `SenseJoystick` (`WinTrek/WinTrek.cpp:10846`):
```cpp
js->controls.jsValues[c_axisThrottle] = -m_ptrekInput->GetAxis(3);
```
The negate is device-independent (applied to every joystick), matching
SDL/evdev polarity where a throttle pushed up/forward reads negative —
so native's negate is what turns "pushed up" into +1 = full forward.
The port's latch (`JoystickFlightController.cs:252`,
`throttleOverride = raw.Throttle;`) passes the raw axis straight
through with no negation. Probe-confirmed through production code:
throttle stick pushed up (raw −1) → override **−1** (idle; the cruise
branch then targets 0 speed) where native yields **+1** (full
forward).

**Fix**: add `InvertThrottle: true` to both `DjiFpvRc3` (`:90`, same
edit site as finding 1) and `NativeHotas` (`:78`) — native's negate is
not DJI-specific, it applies to any joystick. `GatherRoles` already
applies the invert flag before the latch, so both the latch comparison
and the override value are corrected by this one flag.

## Scope

1. Apply both fixes above (both land in the same `JoystickAxisLayout`
   struct edits, `JoystickFlightController.cs:78,90`).
2. Update the three tests named in finding 1 to the corrected physical
   labels.
3. Add a regression test for finding 2: drive the throttle latch from
   rest to full-up raw input and assert the resulting override is +1
   (full forward), not −1.

## Explicitly out of scope

- `NativeHotas`'s Yaw/Pitch/Roll axis indices — only its throttle
  invert is touched here (a separate, uncontested finding); don't
  change its axis-index layout, that's native's own documented default
  order and not part of either confirmed finding.
- Any other joystick preset or the deadzone/latch math itself — only
  the `JoystickAxisLayout` constants are wrong here, not the pipeline
  around them.
- The `ce5e1da9` follow-up's left-stick changes (Yaw 2→3, Throttle
  3→2) — the review found nothing contradicting those; leave them.
- P368-P403 — unrelated, don't touch.

## Verification — real proof, not just build/test

- The updated/added unit tests pass and correctly describe the
  corrected physical mapping (not just numeric pass-through).
- **Live proof, required, and this is the actual confirmation Domingo's
  own P380 report has been waiting on** — plug in the real DJI FPV
  Remote Controller 3 and check, in order (throttle fix must land
  first or the last check reads inverted):
  1. Right stick RIGHT → ship banks right (roll).
  2. Right stick UP → nose pitches down (this port's keyboard/stick-push
     convention).
  3. Left stick LEFT/RIGHT → yaw.
  4. Left stick UP → throttle increases (THR gauge and speed readout
     climb).
  If pitch or roll responds backwards after the index swap, that means
  the device's raw polarity is nonstandard (not what the probe
  assumed) — flip only that axis's `Invert*` flag, don't re-swap
  indices.
- Whole-solution build: 0 warnings, 0 errors.
- Full test suite, run at least 2x: only the pre-existing documented
  live-wire timing flakes expected (confirmed present under full-suite
  parallel load by this same review; pass serially).

## Deliverables

1. The fix, committed on `master` (not pushed — Domingo pushes).
2. Report doc `P404_DJI_JOYSTICK_AXIS_AND_THROTTLE.md` in the repo
   root: both fixes, the updated tests, and the live verification
   performed (or a clear note that it's pending Domingo's own hands-on
   pass with the physical controller).

# P404 — DJI FPV RC3 right-stick axis swap + joystick throttle polarity

Task: `docs/prompts/p404-dji-joystick-axis-and-throttle-fix-prompt.md`
Date: 2026-09-27
Branch: `master` (committed locally, not pushed — Domingo pushes)

Two independently-confirmed bugs in `JoystickFlightController.cs` were fixed,
found by a full read/bash/edit/write kimi-k3 review of the 3D/flight/controls
surface (full log:
`~/Development/allegiance-tools/reference/pi-3d-flight-review-kimik3-20260927.log`)
chasing Domingo's live report of a felt 90° right-stick rotation.

---

## Fix 1 — Pitch/Roll axis indices swapped (right-stick 90° rotation)

`JoystickFlightController.cs:90` (record `JoystickAxisLayout.DjiFpvRc3`):

```csharp
// before
YawAxis: 3, PitchAxis: 0, RollAxis: 1, ThrottleAxis: 2
// after
YawAxis: 3, PitchAxis: 1, RollAxis: 0, ThrottleAxis: 2
```

The layout's own intent (right-stick X→roll, right-stick Y→pitch) only held if
raw axis 0 was the right stick's vertical and axis 1 its horizontal — which
Domingo's report falsified. The review's scratch probe, driving the real
production `GatherRoles`+`MapJoystick` with standard Linux/evdev polarities,
confirmed the old 0/1 assignment produced right-stick-RIGHT → nose-up and
right-stick-UP → roll-left: exactly the felt 90° rotation. A pure index swap
(no invert flags) yields the correct Mode-2 response: right → roll-right,
up → nose-down (stick-push convention, matching keyboard Up).

`YawAxis: 3` / `ThrottleAxis: 2` are unchanged — those left-stick axes were not
contradicted by Domingo's report (right-stick-only).

## Fix 2 — Joystick throttle inverted relative to native

Native `SenseJoystick` (`WinTrek/WinTrek.cpp:10846`):

```cpp
js->controls.jsValues[c_axisThrottle] = -m_ptrekInput->GetAxis(3);
```

The negate is device-independent (every joystick), matching SDL/evdev polarity
where a throttle pushed up/forward reads negative — so native's negate is what
turns "pushed up" into +1 = full forward. The port's latch
(`JoystickFlightController.cs:252`, `throttleOverride = raw.Throttle`) passed
the raw axis straight through with no negation: throttle pushed up (raw −1)
→ override −1 (idle; cruise branch targets 0 speed) instead of native's +1
(full forward).

Added `InvertThrottle: true` to both `DjiFpvRc3` (`:90`) and `NativeHotas`
(`:78`) — native's negate is not DJI-specific. `GatherRoles` applies the flag
before the latch, correcting both the latch comparison and the override value.

Scope respected: `NativeHotas` axis indices untouched (its default order is
native's documented default; only throttle invert added). No other preset,
deadzone/latch math, or P368–P403 touched.

---

## Updated / added tests

`tests/Allegiance.ClientCore.MonoGame.Tests/JoystickFlightControllerTests.cs`

Three tests updated to the corrected physical mapping (labels, not just
numeric pass-through):

- `GatherRoles_DjiFpvRc3_PlacesRawAxesIntoRoleOrder` — now describes
  raw[0]=roll, raw[1]=pitch, and asserts the swapped indices (plus the
  inverted-throttle sign).
- `GatherRoles_ShortArrayOrBadIndex_IsSafeZero` — index 0 is now described as
  DJI roll (was pitch); assertions swapped accordingly.
- `Chain_RollRightStick_ProducesNegativeRoll_LikeFlightInputRollRight` — now
  pushes raw[0] (right-stick-right), not raw[1] (which is vertical under the
  corrected mapping).

Plus one assertion updated because Fix 2 touches `NativeHotas`:
- `GatherRoles_NativeHotas_MatchesHotasOrder` — throttle assertion flipped to
  `-3300/32767` to reflect the newly-added `InvertThrottle` on `NativeHotas`.

New regression test for Fix 2:
- `Throttle_FullUpRawInput_OverridesPositive_FullForwardNotIdle` — drives the
  throttle latch from rest to full-up raw input (−1 at raw index 2) through the
  real `DjiFpvRc3` layout and asserts the override is +1 (full forward), not −1.

---

## Verification

Whole-solution build (`/home/domingo/.dotnet/dotnet build AllegianceCore.slnx`):
0 warnings, 0 errors.

Unit tests:
- Joystick test class: 33/33 pass (includes the new regression test).
- Full solution test suite run 2×: `Allegiance.ClientCore.MonoGame.Tests` is
  fully green both runs (771/771). The other projects surface a shifting
  handful of pre-existing live-wire timing flakes (real ticking server /
  watcher-latch / cargo-reload timing — e.g. `LiveCombatWireTests`,
  `JoinFlowLiveTests.EntityTable_TracksARealTickingServer`,
  `ShipDeleteGoneLiveTests`, `LiveWinConditionTests`, plus occasional
  `CargoBayReloadTests`): counts drift between runs (Core 6/7/8, ClientCore
  3/2/2), the exact syndrome the kimi-k3 review documented. Zero failures in
  the joystick/flight area. The joystick change is lexically isolated to two
  files referenced by none of those projects, so it cannot influence them.

Live hardware verification with the physical DJI FPV Remote Controller 3 is
**PENDING Domingo's hands-on pass** — the controller is not attached to this
machine during the task. Check, in order (throttle fix must land first or the
last check reads inverted):

1. Right stick RIGHT → ship banks right (roll).
2. Right stick UP → nose pitches down (this port's stick-push convention).
3. Left stick LEFT/RIGHT → yaw.
4. Left stick UP → throttle increases (THR gauge and speed readout climb).

If pitch or roll responds backwards after the index swap, the device's raw
polarity is nonstandard — flip only that axis's `Invert*` flag, don't re-swap
indices. This is the live confirmation Domingo's own P380 report has been
waiting on.

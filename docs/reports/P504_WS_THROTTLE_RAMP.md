# P504 — W/S ramp the persistent throttle while held; fix the wheel direction and the `\` full key

Stage: P504. Landed on `master` (not pushed). Top commit: `git log -1` after landing.

Domingo's live playtest feedback (2026-10-02): "ship keeps auto accelerating …
i like persistent but i should be able to stop it", then "w/s should have the
same effect as the mouse wheel". His explicit decision, adopted here: **W/S
RAMP the persistent cruise throttle while held** — holding W raises the
throttle toward +1, holding S lowers it toward −1 (zero thrust / stop),
releasing **keeps** the setting. This deliberately deviates from native (where
W/S are momentary `TK_ThrustForward/Backward` manual thrust and the cruise
throttle is stepped by `=`/`-`, presets and the wheel).

## 1. What W/S no longer do (the behavioural cost of Domingo's choice)

Before P504, W/S set the sim's `ForwardButton`/`BackwardButton` movement bits
(`FlightInputState.ThrottleForward/ThrottleBack` → `stateM`,
`FlightInput.cs:112-113`). Those fields have been **removed** from
`FlightInputState` and `Translate`. Concretely removed:

- **Manual fore thrust (W held)** — W used to thrust the ship forward
  continuously, independent of the cruise throttle. Now W only ramps the cruise
  target; the sim accelerates through the cruise-control branch instead of the
  manual-thrust branch.
- **Reverse thrust (S held)** — S used to thrust the ship backward. Gone; S now
  only lowers the throttle target (down to −1 = stop).
- **S-to-brake (tap S)** — a quick S press used to instantly brake/kill
  momentum. Gone; slowing now means ramping S toward the stop setting (or
  pressing `=`/`-`, Backspace→zero, or the wheel).

Unchanged by design: strafe (Q/E), all turn axes, afterburner (Shift), fire
(Space) — W/S are exclusively the throttle ramp now. The persistent throttle
is still stepped/preset by `=`/`-`, Backspace (zero) and `\` (full), and by the
wheel, so every input path composes onto one axis.

Possible follow-up (explicitly out of scope, noted only): exposing native's
held-thrust W/S as a selectable option.

## 2. The W/S ramp implementation

- **Pure, frame-rate independent integrator** in the graphics-free
  `Flight.FlightInput` (so it is unit-testable without a device):
  `RampThrottle(current, direction, deltaSeconds)` integrates
  `current + direction · ThrottleRampRatePerSec · deltaSeconds`, clamped to
  [−1, +1]. Direction comes from `RampDirection(wHeld, sHeld, composing)`:
  +1 for W, −1 for S, 0 when neither (or composing, or opposing W+S cancel).
- **Rate / tuning knob**: `ThrottleRampRatePerSec = 1.2` (axis-units per
  second) → a full [−1,+1] sweep takes `ThrottleSpan/rate ≈ 1.7 s`
  (`ThrottleRampFullSweepSeconds`), inside the task's "about 1.5–2 s" band and
  easy to tune by editing the one constant.
- **Composes with the other throttle inputs**: the ramp mutates the same
  `_throttle` that `=`/`-`, the wheel and the presets drive (`Game1.cs:_throttle`
  → `FlightInputState.Throttle` → `ControlData.Throttle`), so all inputs share
  one axis.
- **Composing gate**: `RampDirection(…, composing)` yields 0 in chat / quick-chat,
  exactly like the other throttle keys.
- **Logging cadence**: throttle-ramp changes are logged at ~4 Hz (250 ms) while
  held and once on release — never per frame (`_lastThrottleRampLogMs` /
  `_wsWasHeld`).
- **Autopilot disengage**: W/S remain manual input (the ramp direction is folded
  into the existing `manualFlightInput` disengage check), preserving the old
  "pressing W/S leaves the autopilot" behaviour.

## 3. Wheel direction — diagnosis and fix

### 3.1 What I observed / established

- The brief reports the live playtest log contained only `throttle wheel UP`
  lines (no DOWN) and that the wheel "keeps going to 100%"; Domingo was trying
  to **slow** the auto-accelerating ship by scrolling **down**, and the throttle
  only ever climbed.
- The port's wheel code maps a positive ScrollWheelValue delta to an UP step and
  a negative delta to a DOWN step. An only-UP outcome therefore means the
  platform delivered a **positive delta for a real scroll-down** — i.e. the sign
  it reports is inverted relative to the assumed convention.
- The shipped framework is verified at source to accumulate the **documented
  natural sign**: MonoGame 3.8.5.1 (the exact `PackageReference` version)
  `MonoGame.Framework/Platform/SDL/SDLGamePlatform.cs:141-144` does
  `Mouse.ScrollY += ev.Wheel.Y * 120` where SDL's `ev.Wheel.Y` is positive
  "away from the user" (scroll up) and negative toward the user (scroll down);
  MonoGame's `MouseState` docs state ScrollWheelValue increments on scroll up and
  decrements on scroll down. So there is **no inversion inside the framework**;
  the wrong sign arrives from the **SDL/Wayland event layer** on Domingo's box.
- That inversion on Linux is a **documented MonoGame issue**: MonoGame/MonoGame
  #2686 ("Mouse scroll is inverted on Linux") describes ScrollWheelValue coming
  out sign-inverted on Linux.
- Limitation, stated plainly: this session is a headless remote CLI on Domingo's
  live GNOME/Wayland desktop, so I did **not** inject synthetic wheel scrolls
  into the running game (too intrusive/risky against the active session). The
  live confirmation is instead instrumented and deferred to the next real run
  (§3.3).

### 3.2 The fix

`FlightInput.WheelSignNormalizer` returns `-1` on Linux, `+1` elsewhere
(`NormalizeWheelDelta(rawDelta)` applies it). `Game1` now queues the
**normalised** per-frame delta before the paced step loops, so on Domingo's
Linux/Wayland box a real scroll-down (delivered as a positive raw delta) is
flipped to negative and steps the throttle **DOWN**, restoring the documented
convention. Windows/macOS (which already deliver the documented sign) are
passed through unchanged. Rationale for choosing inversion over monotonic:
Domingo's live evidence (scroll-down intent producing only UP) plus the
documented Linux inversion (#2686) both point to inversion; under inversion the
normaliser is a complete fix, whereas under a hypothetical monotonic-always-
positive delivery it degrades to always-DOWN — distinguishable via the new
diagnostic, and trivially corrected by editing the one normaliser.

### 3.3 Evidence instrumentation (next-run confirmation)

A rate-limited (~4 Hz, scroll-activity only) diagnostic now logs the **raw
pre-normalisation** per-frame ScrollWheelValue delta plus the corrected sign,
e.g.:

    [monogame] wheel raw delta +120 (normalised sign -1)
    [monogame] wheel raw delta -120 (normalised sign +1)

That is exactly the per-direction evidence the brief's step 1 calls for; the
first playtest after this lands will record it in the session log.

## 4. The `\` full-throttle key

Previously bound only to `Keys.OemBackslash` — the extra ISO/EU backslash — so
on a US layout, where `\` is `Keys.OemPipe`, "full" was unreachable. Now `thFull`
reads **both** `OemBackslash || OemPipe`, so the physical `\` key triggers Full
(+1) on any layout. (Key-binding help updated accordingly.)

## 5. HUD readout

Item 4 confirmed: the HUD throttle gauge already reads the client-owned
`_throttle` live every frame (`Game1.Rendering.cs` builds `HudFrame { Throttle:
_throttle }`; `HudRenderer.DrawThrottleGauge` maps it 0.5·(1+throttle) = cruise
%). Because the ramp mutates the same `_throttle`, the gauge tracks the ramp in
real time with no extra work.

## 6. Gamepad (P499) and touch (P501)

- **Gamepad (P499)**: unchanged. Its throttle path is a post-Translate override
  (`Throttle = gpr.ThrottleOverride ?? cmd.Controls.Throttle`) plus shoulder-
  button steps that mutate `_throttle` directly. The W/S ramp sets the same
  `_throttle` base, so with no pad throttle-override the ramp value is used, and
  when a pad latches its own throttle that value wins — the paths compose with
  no code change.
- **Touch (P501)**: not landed — only a comment in `Game1.cs` refers to it; no
  touch/touchpanel input path exists to affect.

## 7. Verification (numbers measured)

Build: `/home/domingo/.dotnet/dotnet build` — **0 warnings, 0 errors** (all
assemblies).

Pure tests (no graphics device), all green — `Allegiance.ClientCore.MonoGame.Tests`:
- Holding W for 1 s of simulated time ramps by the expected amount, and is
  frame-rate independent (one 1 s step ≡ 120 × (1 s/120) steps).
- Releasing keeps the value.
- S ramps toward −1 and clamps (from full-reverse it stays −1; a short S-hold
  lowers proportionally).
- W/S set no Forward/Backward thrust bits (even both held).
- The composing gate blocks ramping (direction 0 under `composing`).
- The wheel direction mapping is right under the observed platform values (the
  normaliser flips the delivered inverted sign back to the documented
  convention; test is platform-relative so it stays green off-Linux too).
- Ramp full sweep ≈1.7 s is inside the 1.5–2 s band.

Full suite via `scripts/run-tests.sh` (P471), 3 consecutive runs (only numbers
actually captured shown; a run marked “—” is an assembly whose pass/fail line
wasn’t captured for that run but did not change the overall outcome):

| run | Lobby | MonoGame.Tests | ClientCore.Tests | Core.Tests |
|-----|-------|----------------|------------------|------------|
| 1   | —     | —              | —                | 5 failed / 3694 passed (3699) |
| 2   | 12/12 | 1140/1140     | 409/409          | 2 failed / 3697 passed (3699) |
| 3   | 12/12 | 1140/1140     | 2 failed / 407 passed (409) | 3 failed / 3696 passed (3699) |

Every failure across all runs is a **live-socket load-flake** in the integration
suites (Core.Tests, ClientCore.Tests), rotating run-to-run — observed failure
names across runs: `LiveWinConditionTests.Board_refused_once_mission_over`,
`LiveTreasurePickupTests.Rescue_ship_picking_cash_treasure…`,
`LiveRipcordTests.Mid_countdown_anchor_side_change_aborts_sector_wide`,
`ConquestTerritoryLiveTests.Conquest_win_in_one_mission_does_not_leak_to_another`,
`LiveStationRescueTests.Lifepod_reaching_rescue_station…`,
`FullSessionKillIntegrationTests.FullSession_ThreePeers_…`,
`ReadyCheckLiveTests.Only_the_mission_owner_can_force_team_ready…`,
`ShipDeleteGoneLiveTests.CrossSide_WarperLeavesSector_WatcherLatchesGone`.
All are live-wire/board-latch timing races of the kind `docs/TEST_FLAKES.md`
already documents (e.g. §“Future sweep”: the sub-top-5 load-flakes including
`FullSessionKill` and `Board_refused_once_mission_over` "rotate run-to-run";
`LiveTreasurePickup`/`LiveRipcord` live-wire receipts are listed as load-flakes).
They are independent of this change — which touches only client input logic in
`Allegiance.ClientCore.Game` — and **`Allegiance.ClientCore.MonoGame.Tests`
(all of this change’s tests) passed 1140/1140 in every captured run.**

Out of scope as briefed: the P502 prediction desync and P503 dust.

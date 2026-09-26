# P325 — client medium-fix batch: autopilot deadzone, double-bound B, ambience sound leak

Three MEDIUM findings from the kimi-k3 MonoGame client review
(`~/Documents/Domingo-Llm-Wiki/pages/allegiance-monogame-client-review-kimik3.md`)
batched as small, independent, low-risk fixes. Landed on `master` in three
separate commits (not pushed — Domingo pushes).

## M-1 — autopilot disengages on a 1-pixel mouse twitch (deadzone)

**Finding.** `Game1.manualFlightInput` (in `UpdateFlying`) disengaged the
autopilot on ANY nonzero raw per-frame cursor delta while mouse-flight was
enabled: `(_mouseFlightEnabled && (mouseDx != 0 || mouseDy != 0))`. Ordinary
hand tremor (a few px of jitter) therefore dumped the autopilot constantly —
it never "stuck".

**Native reference.** `WinTrek/WinTrek.cpp` `DoTrekUpdate` (~8280-8299):
native captures the joystick values at engage (`trekJoyStick[axis]`,
`bInitTrekJoyStick`, :8283-8289) and disengages only when the current value
has LEFT that baseline by more than `c_fAutopilotDisengage = g_fJoystickDeadZone *
2.0f` on any axis (`jsValues[axis] - trekJoyStick[axis] > ±2×deadzone`).
Default deadzone `g_fJoystickDeadZoneSmall = 0.1` (:94), so the disengage
threshold is `0.2` in normalized-axis units — the same unit space the port's
`MouseFlightController` deflection lives in (clamped to the unit circle).

**Fix.**
- `MouseFlightController` gained native's constants (`DeadZone = 0.1f`,
  `AutopilotDisengageThreshold = DeadZone * 2f`) and
  `DeflectionMovedPast(baseline, threshold)` — a faithful per-axis port of the
  native `jsValues[axis]-trekJoyStick[axis] > 2×deadzone` check against the
  INTEGRATED deflection.
- `Game1` captures `_autoPilotMouseBaseline = _mouseFlight.Current` at engage
  (native's `trekJoyStick` snapshot) and the manual-input check now uses
  `_mouseFlight.DeflectionMovedPast(_autoPilotMouseBaseline, AutopilotDisengageThreshold)`
  instead of `mouseDx != 0 || mouseDy != 0`. The keyboard side is unchanged
  (keys are binary).

**Live proof** (`.verify_p325/m1_tremor_liveproof.log`, harness
`.verify_p325/m1_harness_program.cs` — drives the REAL `MouseFlightController`
at its REAL 1/512 sensitivity through the exact production decision, over a
realistic oscillatory hand-rest tremor model):

```
deadzone=0.1  autopilotDisengageThreshold=2×deadzone=0.2000
sensitivity=1/512 (0.001953)
single 1px twitch                      frames=    1  old-wiring disengages=    1  new-wiring disengages=    0
two consecutive 2px tremors            frames=    2  old-wiring disengages=    2  new-wiring disengages=    0
hand-rest tremor, 4800 frames          frames= 4800  old-wiring disengages= 4800  new-wiring disengages=    0
sustained one-way push (real turn)     frames=  410  old-wiring disengages=   10  new-wiring disengages=    7
```

Pre-fix wiring dumps the autopilot on every tremor frame (4800/4800 — it never
sticks); post-fix survives the entire trace (0/4800) and still disengages on a
genuine stick push once the integrated deflection crosses the deadzone.

Unit tests (`MouseFlightControllerTests`): `TremorTrace_NeverLeavesAutopilotDisengageDeadzone`,
`TremorTrace_DeflectionMovedPast_StaysFalse`, `MeaningfulStickMove_PastTwiceDeadzone_Disengages`,
`EngagedWhileAlreadyDeflected_SmallMovesDoNotDisengage_ThenBigMoveDoes`.

> Honesty note: the environment is headless, so an actual OS mouse + a live
> server+docked session could not be driven. The proof above exercises the real
> production controller and the exact production decision expression over a
> statistically realistic tremor trace — the closest faithful stand-in, and it
> directly contrasts buggy-vs-fixed wiring.

## M-2 — B double-bound: scoreboard toggle + teleport "back" on one press

**Finding.** `Game1`'s B scoreboard toggle (Update, ~1035) was gated only on
`!_chatActive && !_qcMenuOpen`, not on docked state, while `HandleDockedScreen`
(:1762-1784) separately maps B to "back to hangar" when the teleport pane is
open. Pressing B while docked+teleport-open fired BOTH — closing the pane AND
flipping the scoreboard.

**Fix.** Extracted the gate into `Game1.ScoreboardToggleAllowed(chatActive,
qcMenuOpen, dockedActive) => !chatActive && !qcMenuOpen && !dockedActive` and
wired the scoreboard toggle to it. Now B while the teleport pane is open only
closes the pane.

**Proof.** `ScoreboardToggleGateTests` (Theory) + `DockedGate_IsTheM2RegressionGuard`.
Must-fail verified: removing the `!_dockedActive` clause turns the docked row
red (Assert.False Actual: True).

## M-3 — SoundManager.UpdateAmbience leaks a SoundEffectInstance every trigger

**Finding.** `UpdateAmbience` did `_ambienceCurrent = inst;` overwriting the
previous (now-stopped) instance without disposing it; `Dispose()` only
disposed the current one. At ~1 trigger per 10s, ~700 leaked voice handles over
a 2-hour session → the "audio cuts out late in a long session" failure.

**Fix.** Introduced a tiny pure holder `Audio/ReplacingSlot<T>` that evicts
(the previous instance) exactly once whenever `Replace(next)` puts a new value
in, and `Clear()` on teardown. `SoundManager` now keeps the ambience one-shot
in a `ReplacingSlot<SoundEffectInstance>` (evict = `Safe(i.Dispose)`), so each
new trigger disposes the prior instance instead of abandoning it.

**Proof.** `ReplacingSlotTests`: `Replace_EvictsPreviousExactlyOnce_InOrder`,
`ManyReplaces_NoAccumulation` (N=731 replaces → exactly N-1 evictions, the
leak count encoded), `ReplaceSameReference_DoesNotEvict`, `Clear_EvictsCurrentOnce_AndIsIdempotent`.
Must-fail verified: disabling the eviction makes `ManyReplaces_NoAccumulation`
red (Expected 730, Actual 0).

> Honesty note: no audio device in the environment, so actual playback couldn't
> be heard; the no-accumulation invariant (the exact thing that was wrong) is
> proven by the holder test through the real class the manager now uses.

## Out of scope (unchanged)
- CRITICAL/HIGH (yaw inversion C-1, thread-safety H-2/H-3, disconnect H-4,
  transport leak H-5) — separate tasks (P310, P319, P320, P321).
- M-4 per-frame Draw allocation churn — P326.
- M-5 hull visual/sim mismatch, M-6 MyShipId single-latch — not scoped this round.
- Native reference source (`~/Development/Allegiance`) untouched.

## Build / test
- Whole solution build: 0 warnings, 0 errors.
- `Allegiance.ClientCore.MonoGame.Tests`: 592 passed (14 new).
- Full `AllegianceCore.slnx` suite run(s): see final summary (only the
  pre-existing `LiveTurretGunnerBindingTests` timing flake expected).

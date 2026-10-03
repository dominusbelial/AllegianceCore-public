# P516 — revert W/S to native held thrust (undo P504's W/S throttle ramp); keep P504's wheel and `\` fixes

Stage: P516. Landed on `master` (not pushed). Top commit: `git log -1` after landing.

## 0. Why this exists — Domingo reversed his own 2026-10-02 choice

On **2026-10-02** (P504) Domingo asked for W/S to "have the same effect as the
mouse wheel", implemented as W/S RAMPING the persistent cruise throttle while
held. On **2026-10-03** he reversed that:

> "revert the w/s keys throttle acceleration thing we did to match the mouse
> wheel, match the original client again."

He chose native, full stop. This stage is that reversal. It does **not** make
W/S behaviour a setting; native W/S is the only behaviour now.

## 1. Scope of the revert vs. what P504 legitimately fixed

### 1.1 Reverted — P504's W/S persistent-throttle ramp

Removed, with no dead code left behind:

- `FlightInput.ThrottleRampRatePerSec`, `ThrottleSpan`,
  `ThrottleRampFullSweepSeconds`, `RampDirection`, `RampThrottle` —
  the whole ramp API.
- The `Game1` per-frame ramp block (its `wDown`/`sDown`/`rampDir` locals,
  `_throttle` mutation, ramp log pacing) and the now-dead fields
  `_lastThrottleRampLogMs` and `_wsWasHeld`.
- The `FlightInputState` doc remark describing W/S as the ramp, and the
  KeyBindingTable entry ("W / S — Ramp cruise throttle up / down (hold)").
- The ramp unit tests (`RampDirection_…`, `RampThrottle_…`,
  `RampFullSweep_…`).

### 1.2 Restored — native W/S held thrust (what P504's §1 said it removed)

W/S are native's `TK_ThrustForward` / `TK_ThrustBackward`, momentary manual
thrust. Restored exactly as P445 had ported them:

- `FlightInputState.ThrottleForward` (W) and `ThrottleBack` (S) are back at the
  front of the record struct, and `FlightInput.Translate` ORs
  `ShipControlState.ForwardButton` / `BackwardButton` from them again.
- `Game1` samples `ks.IsKeyDown(W)` / `ks.IsKeyDown(S)` into those fields every
  frame; the **manual branch overrides cruise while held**, and **releasing
  returns the ship to the persistent cruise throttle** — native semantics
  (P445 report §2.6).
- **Reverse thrust (S held)** and **S-to-brake** are back.
- Autopilot disengage: the manual-input check again treats `ThrottleForward` /
  `ThrottleBack` as manual input (they were the original form; P504 had swapped
  in `rampDir`).
- KeyBindingTable and the in-game help list W/S under MOVEMENT as native thrust
  again: "W / S — Throttle fwd / back (held thrust) [TK_ThrustForward/Back]".

The persistent cruise throttle remains fully intact and unchanged: `=`/`-` step
it ±0.2, Backspace → Zero, `\` → Full, the wheel paces it — all still drive the
same `_throttle` axis, and online play still starts at full cruise. W/S no
longer touch `_throttle` at all (they are movement bits, exactly as native).

### 1.3 Kept — the parts of P504 that fix real, native-correct bugs

These P504 changes are unrelated to the W/S ramp and are retained unchanged:

- **Mouse-wheel direction fix**: `FlightInput.WheelSignNormalizer`
  (`-1` on Linux, `+1` elsewhere) + `NormalizeWheelDelta`, applied to the
  per-frame `ScrollWheelValue` delta in `Game1` so scroll-**down** steps the
  throttle **down** (native wheel-down → ThrottleDown). The `WheelNotch` const
  now lives on `FlightInput` (shared), and the raw pre-normalisation delta is
  still logged at ~4 Hz (`_lastWheelDiagMs`) for live confirmation.
- **`\` full throttle on either layout**: `thFull` reads
  `OemBackslash || OemPipe` (the US-layout backslash), so "full" is reachable
  on US and ISO keyboards alike.
- **Live HUD throttle readout**: `Game1.Rendering.cs` still builds
  `HudFrame { Throttle: _throttle }` every frame, so the gauge reflects the
  persistent throttle in real time (no ramp needed to read it).

Because the ramp was the only W/S interaction with `_throttle`, removing it
cannot disturb the wheel / `=`/`-`/preset code — that block is byte-for-byte as
P504 left it (only the queue-application loop that P504 re-pointed at
`FlightInput.WheelNotch` remains, and that constant is kept).

### 1.4 Gamepad (P499) and touch (P501) throttle paths

Neither depended on the P504 ramp:

- **Gamepad (P499)**: the pad's throttle is a post-Translate override
  (`Throttle = gpr.ThrottleOverride ?? cmd.Controls.Throttle` in the joystick
  path, `Game1.FlightInput.cs:129`) plus shoulder-button steps that mutate
  `_throttle` directly via `StepThrottleUp/Down` (`Game1.FlightInput.cs:147-153`).
  It shares the `_throttle` base and never called the ramp — unchanged.
- **Touch (P501)**: `Game1.Touch.cs:155-161` steps `_throttle` via
  `StepThrottleUp/Down` — likewise independent of the ramp — unchanged.

## 2. Verification

### Failing-first tests

Written against the P504 behaviour first (they failed on the ramp), then the
native behaviour landed:

- holding W sets `ForwardButton` and leaves the persistent throttle unchanged —
  `MovementKeys_SetExactlyTheirButton`, `MovementButtons_ComposeWithPassedThrottle`;
- holding S sets `BackwardButton` (reverse/brake) — `MovementKeys_SetExactlyTheirButton`;
- W+S both held latch both bits — `WAndSBothHeld_ThrustBitsBothLatched`;
- release returns to cruise (bits clear, throttle verbatim, no ramp/sticky
  thrust) — `Release_ReturnsToCruise_NoStickyMovementBits`;
- the wheel and `=`/`-`/Backspace/`\` still drive the persistent throttle as
  P504 left them — `WheelNormalizer_*`, `StepThrottleUp/Down_*`,
  `Presets_*`, `CruiseFraction_*` (all unchanged, all green).

### Build

`/home/domingo/.dotnet/dotnet build` — **0 warnings, 0 errors** (all assemblies).

### Change's own assembly

`Allegiance.ClientCore.MonoGame.Tests` — passed in full (see run table below).

### Full suite via `scripts/run-tests.sh` (P471), 3 consecutive runs

Numbers actually measured (pass/fail of the four assemblies per run):

| run | Lobby | MonoGame.Tests | ClientCore.Tests | Core.Tests |
|-----|-------|----------------|------------------|------------|
| 1   | 12/12 | 1197/1197      | 418/419 (1)      | 3703/3708 (5) |
| 2   | 12/12 | 1196/1197 (1)  | 417/419 (2)      | 3707/3708 (1) |
| 3   | 12/12 | 1197/1197      | 417/419 (2)      | 3705/3708 (3) |

The change's own assembly, `Allegiance.ClientCore.MonoGame.Tests`, was fully
green in runs 1 and 3; the single failure in run 2 was the live-socket
`DispenserEquipFireLiveTests.BuyMountAndFireARealDispenser_Live(kind: Chaff)`,
**not** any of this change's (pure unit) W/S tests — it passed in runs 1 and 3
and is the documented live-wire timing class (added to `docs/TEST_FLAKES.md`
below). Every failure in the other three assemblies is the well-documented
live-socket load-flake class (fixed-sleep live-wire receipt under full-suite
load) that `docs/TEST_FLAKES.md` already catalogs — all named flakes are already
listed there or are the same class:

- `ThreePeerSeededSessionTests.SeedThreePeers_…DeterministicVisibility`
  (ledger: before `suspect`, after `load-flake`) — runs 2, 3.
- `FullSessionKillIntegrationTests.FullSession_ThreePeers_…RealCrossSideKill`
  (ledger: live-wire timing, line 33/148) — run 2.
- `ConquestTerritoryLiveTests.Conquest_win_in_one_mission_does_not_leak_to_another`
  (ledger line 29/267 — recurs as a live-timing flake) — run 2.
- `ShipDeleteGoneLiveTests.CrossSide_WarperLeavesSector_WatcherLatchesGone`
  (ledger: live-wire timing, line 51) — run 3.
- `AutoReloadOnEmptyTests.Auto_reload_relays_RELOAD129_sector_wide`
  (ledger line 52) — run 3.
- `GameOverPlayersWireTests.Conquest_win_…_leak_game_over_players_to_another`
  (ledger line 31/267) — run 3.
- `AddPartTests.Part_pickup_decline_recreates_treasure_at_current_position`
  (ledger line 247) — run 3.
- `DispenserEquipFireLiveTests.BuyMountAndFireARealDispenser_Live` (run 2) —
  same live class; added to the ledger so it stays the single source.

None touches the changed code — the change is confined to pure W/S input logic
in `Allegiance.ClientCore.Game` + its MonoGame unit tests, and `git diff
--name-only` shows zero source/test changes in Core/Server/Bot/ClientCore/Lobby.

## 3. Out of scope (not touched)

- Any other P504/P505/P506 behaviour (other than the W/S ramp, wheel sign and
  `\` key addressed here).
- The own-ship model (P507/P508).
- Making W/S behaviour a setting — native, full stop (Domingo's decision).

## 4. For whoever reads P504's report next

P504 (`docs/reports/P504_WS_THROTTLE_RAMP.md`, commit `39e5faae`) describes the
W/S persistent-throttle ramp as Domingo's adopted control scheme and lists what
it removed (manual thrust, reverse thrust, S-to-brake). **That ramp was
reverted by P516 on 2026-10-03** — Domingo changed his mind and asked to "match
the original client again". The ramp is gone; native held W/S thrust is back.
Everything else in P504 that was a genuine bug fix (the mouse-wheel direction
fix, the `\` full key on both layouts, the live HUD throttle readout) is kept
and still in place. Do not re-introduce the ramp as the default; if you believe
it deserves to exist at all, it would have to be as an explicit opt-in feature,
which Domingo has explicitly declined to pursue so far.

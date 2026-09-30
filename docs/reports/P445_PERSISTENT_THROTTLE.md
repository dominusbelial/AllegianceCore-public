# P445 — Native persistent throttle: step keys, presets, mouse wheel, start at full

## Summary

The MonoGame client had **no persistent throttle**: `FlightInput.Translate` hard-coded
the throttle axis to `-1` every frame (`ControlData(yaw, pitch, roll, -1f)`), so releasing
W braked the ship to a stop and you could never set a cruise speed and fly with the mouse.
Native Allegiance has a separate persistent throttle axis (`trekClient.trekThrottle`,
[-1, 1]), stepped by `= / -`, set by Backspace / `\` presets, ridden by the mouse wheel,
and initialized to **full (+1)** for on-line play.

P445 ports that: the client now owns a persistent throttle, initializes it to full on every
launch/undock, steps it with `= / -` and the mouse wheel (native keys), maps Backspace →
Zero and `\` → Full, keeps W/S as held manual thrust (the sim's manual branch overrides
cruise while a movement button is held, then returns the ship to the cruise throttle when
released), and the existing HUD Thrust gauge now reads this persistent setting as
`0.5*(1+throttle)` (percent of max cruise speed). Autopilot is untouched and, on disengage,
control returns to the player's persistent throttle rather than snapping to -1.

Out of scope honoured: no key-layout remap beyond the throttle keys, no vector-lock /
vertical-thrust, no server/sim change (the cruise branch was already correct), no other queued
P-series work. Joystick throttle override (P380) still takes precedence once the stick moves.

---

## 1. Pre-fix failing test (written first, run against unfixed code)

Added `Throttle_PassedThroughNotHardcodedMinusOne` to `FlightInputTests.cs` first
(the `Throttle` field was added to `FlightInputState` as pure plumbing; `Translate` still
hard-coded -1). It failed exactly as the task requires:

```
Failed Allegiance.ClientCore.MonoGame.Tests.FlightInputTests.Throttle_PassedThroughNotHardcodedMinusOne [2 ms]
  Error Message:
   Assert.Equal() Failure: Values are not within 3 decimal places
Expected: 0.5 (rounded from 0.5)
Actual:   -1 (rounded from -1)
  Stack Trace:
     at Allegiance.ClientCore.MonoGame.Tests.FlightInputTests.Throttle_PassedThroughNotHardcodedMinusOne() in .../FlightInputTests.cs:line 232
     at System.Reflection.MethodBaseInvoker.InterpretedInvoke_Method(...)
Failed!  - Failed:     1, Passed:     0, Skipped:     0, Total:     1
```

After the fix (`Translate` now sends `input.Throttle`), this and the rest of the new tests
pass: the MonoGame project suite is **908/908**.

---

## 2. Implementation

### 2.1 `FlightInputState` + `Translate` (pure, testable)
- Added trailing `float Throttle = -1f` to `FlightInputState`. `None` explicitly pins
  `Throttle = -1f` (the plain parameterless `new()` would give the raw float default 0,
  which the server's cruise branch would read as *half* speed forward — the exact trap the
  original -1 hard-code existed to avoid).
- `Translate` now returns `ControlData(yaw, pitch, roll, input.Throttle)` — the value is sent
  verbatim, no keystroke derivation.
- Added pure helpers on `FlightInput` (all unit-tested, no graphics device):
  - `StepThrottleUp(t)` = `t < 0.8f ? t + 0.2f : 1f` (native `WinTrek.cpp:10515-10518`)
  - `StepThrottleDown(t)` = `t > -0.8f ? t - 0.2f : -1f` (native `WinTrek.cpp:10917-10920`)
  - `ThrottlePreset` enum (Zero -1, ThirtyThree -0.3, SixtySix +0.3, Full +1) + `PresetValue`
  - `DefaultThrottle = 1f` (native's on-line default)
  - `CruiseFraction(t)` = `0.5f*(1f+t)` — the 0–100% of cruise-speed mapping the HUD uses.

### 2.2 `Game1` persistent state + key/wheel edges
- New field `_throttle = FlightInput.DefaultThrottle` in `Game1.cs`, beside `_groupFire` /
  `_mouseFlightEnabled`, plus edge latches `_thUp/_thDown/_thZero/_thFullWasDown` and the
  wheel-pacing state (`_lastWheelValue`, `_wheelQueue`, `_lastWheelStepMs`).
- In `Game1.UpdateFlying` (flight input only, gated behind `!composing`):
  - `= / -` step the throttle ±0.2 (edge-triggered, one step per press).
  - Backspace → Zero, `\` → Full.
  - Mouse wheel steps the throttle (below).
- The throttle value is threaded into `FlightInputState.Throttle` in **both** the normal and
  the chat-composing branches, so composing a chat line pauses movement keys but the cruise
  throttle keeps running (and the ship keeps cruising) — matching native, which sends the
  axis every frame regardless.

### 2.3 Where the throttle initializes (full +1)
Reset to `FlightInput.DefaultThrottle` (+1) at **every** launch / undock into flight:
- **Initial launch:** the ReadyCheck→Flying handoff in `Game1.Update` — a single reset point
  that catches all four `_state = ClientState.Flying` sites in `UpdateReadyCheck` (capture
  countdown, capture forced, stage-Started, countdown-began).
- **Undock from a station:** the three dock-launch sites in `Game1.HandleDockedScreen`
  (L-key BUY_LOADOUT launch, U-key undock-no-buy, and the 30 s unattended fallback
  auto-launch). Docking keeps `_state == Flying`, so these are separate resets.

The field initializer is also `DefaultThrottle`, so the very first flight is full before any
reset fires.

### 2.4 Key conflict resolution (audited)
Native throttle keys vs the MonoGame map (see table in §4). Conflicts found:
- `=`/`-` (OemPlus/OemMinus) were **music volume up/down** (`Game1.FlightInput.cs`) — the
  less-important binding. Music volume moved to **F6 / F5** (volume up / down), which are the
  only free F-keys (F4 = Alt+F4 quit, F11 = screenshot in use).
- **Backspace** is the chat-delete key, but only while the chat composer is open; in normal
  flight it was free, and throttle keys are gated `!composing`, so no collision.
- **`\`** (OemBackslash) was entirely unused → free.

### 2.5 Mouse wheel pacing
Native reads `WheelDelay` (default preference **2**) into `m_iWheelDelay = LoadPreference("WheelDelay", 2) % 5`
(`WinTrek.cpp:3052`), indexing `static const float delay[] = {0.066f, 0.033f, 0.022f, 0.016f, 0.0f}`
(`WinTrek.cpp:8178`). `delay[2] = 0.022 s` is the real default pacing — **22 ms between
throttle steps while scrolling**. The port accumulates `Mouse.GetState().ScrollWheelValue`
deltas (each notch = `WheelNotch = 120`) into a queue and applies **at most one step per
`WheelStepIntervalMs = 22`**, so a fast scroll paces repeats exactly like native. No settings UI.

### 2.6 W/S stay held thrust
Unchanged: W/S still set `ForwardButton`/`BackwardButton` in `stateM` (native
`TK_ThrustForward/Backward`). The sim's manual branch (`Ship.ExecuteMove`,
`(StateM & MoveButtonsMask) != 0`) gives movement priority over cruise; on release the ship
returns to the persistent throttle instead of stopping.

### 2.7 HUD readout
The HUD already draws a real original-game Thrust gauge whose fill fraction is
`(throttle+1)/2 = 0.5*(1+throttle)` with a percent readout beneath. Its source is now the
client-owned `_throttle` (accurate, lag-free) instead of the server-echoed `Controls.Throttle`;
during autopilot it shows the cruise speed the ship will resume on disengage.

### 2.8 Autopilot interplay
The autopilot path is untouched — it sends its own `ControlData` from `AutopilotSteering`
and bypasses `Translate`. On disengage (G toggle, manual input, target lost) control falls
back to `cmd.Controls`, whose `Throttle` is `_throttle`, so the player's persistent throttle
resumes rather than snapping to -1.

---

## 3. Verification

### 3.1 Unit tests (`FlightInputTests.cs`, pure, no graphics device)
- `Throttle_PassedThroughNotHardcodedMinusOne` — the written-first failing test (§1).
- `StepThrottleUp_AddsTwoTenths_ClampsAtFull` / `StepThrottleDown_SubtractsTwoTenths_ClampsAtReverse`
  — the native `<0.8?+0.2:1.0` / `>-0.8?-0.2:-1.0` rule incl. the clamp cases.
- `Presets_MapToNativeAxisValues` — Zero -1, 33% -0.3, 66% +0.3, Full +1.
- `Throttle_StepsAndSends_NeverClipBeyondBounds` — full-step lifecycle through `Translate`.
- `MovementButtons_ComposeWithPassedThrottle` — W/S bits set AND throttle still sent.
- `LaunchDefault_IsFullThrottle` — `DefaultThrottle == 1f == PresetValue(Full)`.
- `CruiseFraction_MapsThrottleToCruisePercent` — the 0–100% readout mapping.
- `HelpOverlayTests` updated: `GroupCount` 10→11, new CRUISE THROTTLE group asserted.

### 3.2 Sim-level cruise test (`ShipFlightModelTests.cs`, real `Ship.ExecuteMove`)
`PersistentThrottle_CruisesToMaxSpeed_AndIdleStops` runs the real cruise branch
(`Ship.cs:4024-4030`, the port of `shipIGC.cpp:2990-2998`) with **no movement buttons**:

```
P445 full-throttle cruise speed=120 maxSpeed=120
P445 idle(-1) cruise speed=0 (want ~0)
```
- Throttle +1 → converges to **exactly max speed** (120/120).
- Throttle -1 → converges to **0** (even from an initial max-speed velocity).

This is the deterministic proof that the persistent throttle actually cruises.

### 3.3 Live run
A full local-server + MonoGame-client live run was not performed in this pass (the sim-level
`Ship.ExecuteMove` cruise proof plus the pure input-wiring tests cover the behavioural
contract deterministically; a live run is noted as a follow-up nicety rather than a gate).

### 3.4 Build & full suite
- Whole-solution build (`/home/domingo/.dotnet/dotnet build`): **0 warnings, 0 errors**.
- `Allegiance.ClientCore.MonoGame.Tests`: **908/908** pass; `Allegiance.Lobby.Tests`: **12/12**.
- `Allegiance.Core.Tests` new/changed tests: my `ShipFlightModelTests` additions (**8/8**, incl.
  the P445 sim cruise test) pass deterministically.
- **Three consecutive full-suite runs** were executed. Every failure in each run was a
  **pre-existing, documented live-wire timing flake** (this heavily-loaded box — resident QEMU
  VM, editors, long-lived processes — is documented to flake these live/timing tests; see
  `docs/reports/INBOUND_QUEUE_CAPS_P315.md` and `docs/reports/NONBLOCKING_SENDS_P311.md`).
  P445 touches only MonoGame-client input, so it cannot affect these. Observed flakes per run:

  Run 1 — Core (3530/3540): `LiveTurretGunnerBindingTests.Board_binds…roundtrips_live`
  (the known persistent one), `LiveRipcordTests.Re_target_activates_to_requester_only`,
  `LiveRelaunchTests.Carrier_without_launch_slots…`, `GameOverPlayersWireTests.Conquest_win_…`
  (×2), `ConquestTerritoryLiveTests.*` (×3), `CargoBayReloadTests.SwapPart_negative_mount…`,
  `BotFlightTests.Abrupt_disconnect_during_broadcast…`. ClientCore (320/323):
  `ShipDeleteGoneLiveTests.DockOut_…`, `JoinFlowLiveTests.EntityTable_…`,
  `FullSessionKillIntegrationTests.FullSession_ThreePeers_…`.

  Run 2 — ClientCore (319/323): `JoinFlowLiveTests.EntityTable_…`,
  `FullSessionKillIntegrationTests.FullSession_ThreePeers_…` (plus 2 same-family).
  Core (3528/3540): `NpcShipFoundationLiveTests.Ownerless_npc_ship…`,
  `LoadoutChangeLiveTests.Boarding_…`, `LiveWinConditionTests.Multi_mission…`,
  `LiveTurretGunnerBindingTests.Board_binds…`, `LiveRipcordTests.*` (×2),
  `GameOverPlayersWireTests.Conquest_win_…` (×2), `ConquestTerritoryLiveTests.*` (×3),
  `AsteroidMinedLiveTests.Mining…`.

  Run 3 — ClientCore (319/323): same JoinFlowLive / FullSessionKill family.
  Core (3531/3540): `LiveTurretGunnerBindingTests.Board_binds…`,
  `LiveStationRescueTests.Lifepod_reaching…`, `LiveRipcordTests.*` (×2),
  `GameOverPlayersWireTests.Conquest_win_…` (×2), `ConquestTerritoryLiveTests.*` (×3).

  All of the above names are members of the rotating set of live/timing families documented in
  the repo reports (LiveTurretGunnerBinding, LiveRipcord, LiveRelaunch, LoadoutChangeLive,
  LiveTreasurePickup, LiveCountdownStartPing, LiveStationRescue, LiveWinCondition, Conquest /
  GameOverPlayers wire, AsteroidMinedLive, NpcShipFoundationLive, JoinFlowLive,
  ShipDeleteGoneLive, FullSessionKill, CargoBayReload, BotFlight). **None are regressions from
  P445**; each passed in isolation / in the pristine-tree A/B controls described in the cited
  reports. The P445-added tests passed in every run.

---

## 4. Key-binding table (final)

| Function (native TK) | Native key | Port key | Change |
|---|---|---|---|
| Throttle up (TK_ThrottleUp) | `=` | `=` (OemPlus) | reclaimed from music-volume-up |
| Throttle down (TK_ThrottleDown) | `-` | `-` (OemMinus) | reclaimed from music-volume-down |
| Throttle zero (TK_ThrottleZero) | Backspace | Backspace | free in flight (chat-delete only while composing, gated off) |
| Throttle full (TK_ThrottleFull) | `\` | `\` (OemBackslash) | was unbound — free |
| Throttle 33% (TK_Throttle33) | unbound | unbound | left unbound as native's defaultinputmap.mdl does |
| Throttle 66% (TK_Throttle66) | unbound | unbound | left unbound as native's defaultinputmap.mdl does |
| Mouse wheel up/down | wheel | wheel (paced 22 ms) | added |
| **Music volume up (idmMusicVolumeUp)** | — | **F6** | **moved** (was `=`) |
| **Music volume down (idmMusicVolumeDown)** | — | **F5** | **moved** (was `-`) |

Decisions documented: native keys `=`, `-`, Backspace, `\` used where free; music volume (the
less important binding) relocated to the free F6/F5; 33%/66% left unbound exactly as native's
default map does. The help overlay (H) gains a CRUISE THROTTLE group reflecting this table.

## 5. Files changed
- `src/Allegiance.ClientCore.MonoGame/Flight/FlightInput.cs` — `Throttle` field on the state,
  `Translate` sends it, pure step/preset/cruise-fraction helpers.
- `src/Allegiance.ClientCore.MonoGame/Game1.cs` — `_throttle` field + edge latches + wheel state.
- `src/Allegiance.ClientCore.MonoGame/Game1.FlightInput.cs` — throttle keys, wheel, music-volume
  relocation, ReadyCheck→Flying reset, state wiring.
- `src/Allegiance.ClientCore.MonoGame/Game1.Panes.cs` — throttle reset at the three dock
  launch/undock sites.
- `src/Allegiance.ClientCore.MonoGame/Game1.Rendering.cs` — HUD Thrust gauge reads `_throttle`.
- `src/Allegiance.ClientCore.MonoGame/Rendering/KeyBindingTable.cs` — CRUISE THROTTLE group +
  MUSIC F6/F5.
- `tests/.../FlightInputTests.cs`, `tests/.../HelpOverlayTests.cs`, `tests/Allegiance.Core.Tests/ShipFlightModelTests.cs` — tests.
- `docs/reports/P445_PERSISTENT_THROTTLE.md` — this report.

Committed on `master` (not pushed — Domingo pushes).

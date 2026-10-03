# P506 — mouse flight: native dead zone + quadratic response curve (+ virtual-stick indicator)

**Stage:** P506 · **Branch:** `master` (unpushed) · **Date:** 2026-10-03

## Goal & outcome

Domingo's live playtest (2026-10-02): *"the center dead zone is too small so i
kind of struggle to keep the ship steady or without any movement."*

The port's mouse flight fed the integrated virtual-joystick deflection directly
into `Controls.Yaw/Pitch` with **no dead zone at all** — so a trembling hand
turned the ship. P506 applies native's joystick **response curve** (dead zone +
rescale + quadratic) to the mouse deflection before it becomes yaw/pitch, adds
the three native **settings** (dead-zone preset, linear-vs-quadratic, mouse
sensitivity) as in-flight toggles, and draws a **virtual-stick indicator** on the
HUD so the pilot can see when the stick is centred inside the dead zone.

## ⚠️ Brief-vs-source discrepancy (verified, corrected)

The brief asserted *"every turn axis goes through `MapJoystick`"* and directed
applying it to the mouse. **That is false for the mouse in native:**

- `SenseJoystick` (`WinTrek.cpp:10826-10829`) branches on the virtual-joystick
  image being enabled; when it is (mouse flight on), yaw/pitch are read
  **directly** from `m_pjoystickImage->GetValue(0/1)->GetValue()` — `MapJoystick`
  is applied **only** in the `else` (physical-stick) branch.
- The virtual-joystick image (`JoystickImageImpl::MouseMove`, `efimage.cpp:182-209`)
  only integrates + unit-circle clamps; no dead zone inside.
- So native gives the mouse **zero** dead zone — which is exactly why the port
  (a faithful reproduction of that path) felt unstable.

**Decision:** The brief is Domingo's explicit request to *add* a dead zone (his
complaint is real: effective dead zone = 0). We therefore deliberately apply the
**physical-stick** response curve (`MapJoystick` dead zone + rescale + quadratic)
to the mouse as an enhancement, and say so plainly. This is a documented
improvement, not a hidden deviation. The pre-existing `MouseFlightController`
XML already warns "do not add any centring/decay not in the original" — we add a
dead-zone **band**, not centring/decay; the integrating, non-centring behaviour
is untouched.

## Implementation

### 1. Shared response curve (`MouseFlightController.MapResponse`)

Pure static, in `Flight/MouseFlightController.cs`:

```csharp
static float Map(float v, float dz, bool lin) {
    float mapped = -JoystickFlightController.MapJoystick(v, dz);   // dead zone + rescale
    return lin ? mapped : mapped * MathF.Abs(mapped);              // native value *= fabsf(value)
}
return (Map(defl.Yaw, deadzone, linear), Map(defl.Pitch, deadzone, linear));
```

- **Shares** `JoystickFlightController.MapJoystick` (the P380 physical-stick
  dead zone + rescale) — a single implementation, not a third copy.
- **Sign compensation:** `MapJoystick`'s divisor `dz − 1` is negative, so it both
  rescales and **inverts sign** (correct for the physical stick, whose raw axis
  convention opposes the sim's). The mouse virtual stick is already sim-aligned
  (`efimage.cpp:193-194` feeds it straight to `jsValues`), so `MapResponse`
  **negates** `MapJoystick`'s output to recover a sign-preserving dead
  zone + rescale. Guarded by `MouseSign_IsPreserved_NotInvertedLikePhysicalStick`
  — a naive reuse would have reversed the mouse controls.
- Quadratic default: `value *= fabsf(value)` (`WinTrek.cpp:10792`).
- Full deflection (magnitude 1.0) still reaches ±1 in both curves.

**Wiring** (`Game1.FlightInput.cs`, the point that previously did
`Yaw = defl.Yaw`):

```csharp
var (outYaw, outPitch) = MouseFlightController.MapResponse(
    defl, _mouseFlight.Deadzone, _mouseFlight.LinearControls);
cmd = cmd with { Controls = cmd.Controls with { Yaw = outYaw, Pitch = outPitch } };
```

`_mouseFlight.Current` (used by the autopilot manual-input disengage check,
`WinTrek.cpp:8292`) is **unchanged** — it still reports the raw integrated
deflection, so the M-1 disengage threshold semantics are untouched.

### 2. Settings (in-flight toggles; no menu exists in this keyboard port)

Defaults are native's. Added settable properties on `MouseFlightController`:

| Setting | Default | Native source |
|---|---|---|
| `Deadzone` | `0.1` (Small preset) | `g_fJoystickDeadZoneSmall`, `WinTrek.cpp:94` |
| `LinearControls` | `false` → quadratic | `m_bLinearControls` + `LinearControlResponse`, `WinTrek.cpp:3277,10792` |
| `MouseSensitivity` | `1.0` (see note) | `m_pnumMouseSens` 0.6 / [0.1,2.0] / ±0.01, `WinTrek.cpp:2809,5512` |

Presets `DeadzonePresets = {0.04, 0.1, 0.3}` = native
`g_fJoystickDeadZoneSmallest/Small/Large` (`WinTrek.cpp:93-95`).

How the player changes them (in flight, `!composing`-gated, edge-triggered,
grep-verified free keys — native's menu keys `Z`/`J` are already taken in the port):

- **D5** — cycle dead-zone preset (Smallest 0.04 → Small 0.1 → Large 0.3 → wrap).
- **D6** — toggle linear / quadratic response.
- **D7 / D8** — mouse sensitivity −0.01 / +0.01, clamped to [0.1, 2.0].

Console logs confirm each change (same pattern as the M/I toggles).

**Sensitivity note:** native's `MouseSensitivity` preference defaults to `0.6`
and multiplies the fixed `1/512` image sensitivity
(`WinTrek.cpp:2809`, `AdjustMouseSens`). The port's existing default feel is
`1/512`; defaulting the knob to `0.6` would silently change flight feel (and
break the pre-existing `Constructor_DefaultSensitivity_MatchesOriginal` test).
We default the knob to `1.0` (≈1.67× native's nominal `0.6`) to preserve the
exact current feel, and expose the full native range. Documented, not hidden.

### 3. Virtual-stick indicator (HUD)

Added `MouseStickView(Yaw, Pitch, Deadzone, LinearControls)` (Flight namespace)
and `HudFrame.MouseStick` (trailing optional, `null` when mouse flight is off).
`HudRenderer` draws a small reticle at bottom-centre: a unit-deflection range
circle, an inner **dead-zone circle**, a dot at the live integrated deflection
(turns the teal accent colour once it leaves the dead zone), and a caption
`MOUSE DZ <n>% QUAD|LIN`. The dot wanders inside the dead-zone circle freely —
the whole point is the pilot can *see* when they're centred and hold there.

**Fidelity check on the indicator:** native's `JoystickImageImpl` has **no**
`Render` override and no child image (`efimage.cpp`), so native draws no visible
reticle/dead-zone circle either — the brief's premise there is also aspirational.
The indicator here is an original port-side addition (explicitly requested by
scope item 3: "add one if the port lacks it").

### 4. Recentre aid — **named follow-up, not implemented**

Native has no mouse auto-centring aid (the virtual stick deliberately does not
auto-centre; `MouseFlightController` XML forbids adding decay). Matching native
means there is nothing to reproduce, so per the brief this becomes a named
follow-up rather than an invented feature.

## Out-of-scope effects (checked)

- **P380 joystick / P499 gamepad / P501 touch:** all three call
  `JoystickFlightController.MapJoystick` (linear, no quadratic) via their own
  paths. `MapResponse` is mouse-only and does not touch the shared `MapJoystick`
  function's behaviour — so P380/P499/P501 output is **byte-identical**. The
  shared surface reused here (the `MapJoystick` formula and the dead-zone
  constants) is unchanged. `JoystickFlightControllerTests` + `GamePadFlightRolesTests`
  stay green (verified).
- **W/S throttle ramp (P504) and prediction (P505):** untouched.

## Verification

### Pure tests (`MouseResponseCurveTests`, +24, failing-first)

- `Deflection_InsideOrAtDeadzone_IsExactlyZero` — deflection within ±0.1 → exactly `(0,0)`.
- `JustOutsideDeadzone_RampsFromZeroWithNoStep` — monotonic ramp from ~0 past the edge (first value < 0.003), no discontinuity.
- `Quadratic_Versus_Linear_CurveValues` — e.g. 0.5 → 0.44444 linear vs 0.19753 quadratic.
- `FullDeflection_StillReachesFullMagnitude` — ±1.0 → ±1.0 in both curves.
- `BelowFullDeflection_MapsProportionallyUnderOne` — sanity <1.
- `MouseSign_IsPreserved_NotInvertedLikePhysicalStick` — regression guard vs sign reversal.
- `DefaultSettings_AreNative_QuadraticAndSmallDeadzone`, `DeadzonePresets_MatchNativeValues`,
  `MouseSensitivity_ClampsToNativeRange`, `MouseSensitivity_MultipliesThePixelDelta`.

**Must-fail-if-removed:** temporarily changed `MapResponse` to a raw passthrough
(no dead zone / rescale) → **12/24 curve tests went red**; reverted to the real
implementation → green again.

### Suite

- Whole-solution build (`/home/domingo/.dotnet/dotnet build AllegianceCore.slnx`): **0 warnings, 0 errors**.
- `scripts/run-tests.sh` (P471), three consecutive runs. Measured totals:
  Lobby.Tests 12, ClientCore.MonoGame.Tests **1166** (incl. the 24 new P506 tests),
  ClientCore.Tests 411, Core.Tests 3699 (grand total **5288 per run**).

| run | Lobby | MonoGame (P506 area) | ClientCore | Core | failures |
|---|---|---|---|---|---|
| 1 | 12/12 | 1166/1166 | 411/411 | 3698/3699 | `LiveRipcordTests.Ship_anchor_insufficient_energy_aborts_consumed_not_retried` |
| 2 | 12/12 | 1166/1166 | 411/411 | 3697/3699 | `LiveCountdownStartPingTests.Multi_mission_countdown_scopes_per_mission`, `ConquestTerritoryLiveTests.Conquest_win_in_one_mission_does_not_leak_to_another` |
| 3 | 12/12 | 1166/1166 | 411/411 | 3699/3699 | — |

Every failure is a pre-existing live-wire timing flake in the **untouched**
`Allegiance.Core` assembly (P506 changes live entirely under
`src/Allegiance.ClientCore.Game` + `Allegiance.ClientCore.MonoGame.Tests`, zero
Core/Server/Bot/ClientCore edits). Each of the three failed tests passed **3/3 in
isolation** (`--filter`), confirming load-flake, not regression. `LiveChatSectorWireTests`,
`ConquestTerritoryLiveTests.Conquest_win_in_one_mission…`, and
`LiveCountdownStartPingTests.Multi_mission…` are already cited in
`docs/TEST_FLAKES.md`; the `LiveRipcordTests.Ship_anchor…` run-1 flake is the same
fixed-sleep live-wire class (rotates run-to-run) — noted here, the ledger stays
the single source. The P506 area (MonoGame) was green in all three runs.

## Files changed

- `src/Allegiance.ClientCore.Game/Flight/MouseFlightController.cs` — response curve + settings.
- `src/Allegiance.ClientCore.Game/Game1.FlightInput.cs` — curve wiring + D5-D8 toggles.
- `src/Allegiance.ClientCore.Game/Game1.cs` — edge-latch fields.
- `src/Allegiance.ClientCore.Game/Rendering/HudFrame.cs` — `MouseStick` field.
- `src/Allegiance.ClientCore.Game/Rendering/HudRenderer.cs` — indicator rendering.
- `src/Allegiance.ClientCore.Game/Game1.Rendering.cs` — builds `MouseStick`.
- `tests/Allegiance.ClientCore.MonoGame.Tests/MouseResponseCurveTests.cs` — new pure tests.
- `docs/reports/P506_MOUSE_FLIGHT_DEADZONE.md` — this report.

## Commit

`master` only, **not pushed** (branch verified with `git branch --show-current` → `master`).
`git log -1` confirms the `P506:`-prefixed commit is HEAD. Working tree clean of tracked changes (only pre-existing untracked files from other sessions remain).

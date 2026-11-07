# P948 — Mouse options: persisted invert-Y and native sensitivity

**Task:** `P948_BRIEF.md` (Domingo, 2026-11-07: "mouse is inverted again"; mouse "feels hyper sensitive").
**Base:** Claude's local master at dispatch (worktree `allegiance-pi-p948`, branch `pi/p948`). No commit, no push.
**Native reference:** `~/Development/Allegiance/src/` (read-only). Every line below was re-grepped while writing.

## 1. Native findings

### 1.1 Native HAS an invert-pitch option — `FlipY` ("Y Axis Flipped")

The brief's quick grep found nothing, but native does ship one; it is **persisted in native's preference
store**, which is exactly the gap the port had:

| Native | Cite |
|---|---|
| field `bool m_bFlipY`, constructed `false` | `WinTrek/WinTrek.cpp:2729` |
| `m_bFlipY = (LoadPreference("FlipY", 0) != 0)` — **default 0 (not inverted)** | `:3048` |
| Game Options → `idmToggleFlipY` 'Y', label `GetFlipYMenuString()` ("Y Axis Not Flipped " / "Y Axis Flipped ") | `:4536`, `:5903-5906` |
| dispatch `idmToggleFlipY` → `ToggleFlipY()` | `:6286-6291` |
| `ToggleFlipY()` flips the field **and saves `FlipY`** | `:5297-5306` |
| applied as `jsValues[c_axisPitch] = -jsValues[c_axisPitch]` | `:10846-10851` |

So the port was *less* faithful than native: P806 already mirrored the menu row ("Flip Y"), but the value
lived only in memory.

### 1.2 Native's mouse sensitivity

| Native | Cite |
|---|---|
| `m_pnumMouseSens = LoadPreference("MouseSensitivity", "0.6")` — **default 0.6** | `WinTrek.cpp:2809` |
| `m_pinputEngine->GetMouse()->SetSensitivity(value)` | `:2811` |
| step `c_fMouseSensDelta = 0.01f` | `:88` |
| `AdjustMouseSens`: clamp `std::min(2.0f, std::max(0.1f, value + delta))`, `SavePreference("MouseSensitivity", …)`, re-`SetSensitivity` | `:5512-5531` |
| Game Options → "Mouse Options" submenu (`idmMouseOptions` 'Q') | `:4539`, `:4606-4614` |
| Raise/Lower items `idmMouseSensUp` 'U' / `idmMouseSensDown` 'D' | `:4612-4613` |
| labels `GetMouseSensMenuString`: "Raise Mouse Sensitivity to NN %", "(highest)", "(lowest)" | `:5599-5616` |
| menu dispatch → `AdjustMouseSens(±c_fMouseSensDelta)` (one step per activation) | `:6431-6437` |

### 1.3 How the value scales the mouse delta (5a)

Native applies the sensitivity **twice-fold**, at two stages:

1. **Engine mouse** — `MouseInputStreamImpl::SetSensitivity` stores it (`engine/dinput.cpp:602`);
   `CalculateDelta` multiplies the raw device delta by it
   (`engine/dinput.cpp:384-395`): `delta * m_sensitivity`, with `×2`/`×4` only past the Windows
   acceleration thresholds and only if acceleration is on. Acceleration is a separate preference with
   **default 0** (`LoadPreference("MouseAcceleration", 0)`, `WinTrek.cpp:3051`) and the port has no
   acceleration, so the default path is the plain `delta * sensitivity`.
2. **Virtual-joystick image** — `JoystickImageImpl::MouseMove` multiplies the cursor offset from
   (320,240) by the image's own fixed sensitivity (`effect/efimage.cpp:182-205`), which native creates as
   `CreateJoystickImage(1.0f / 512.0f)` (`WinTrek.cpp:2982`). Its `SetSensitivity`
   (`effect/efimage.cpp:74-77`) is a different knob and native never changes it.

Because the image re-centres the cursor every frame, the per-frame cursor offset *is* the engine-scaled
delta, so native's effective per-device-unit axis scale is

```
native effective = engineSensitivity × imageSensitivity = 0.6 × (1/512) = 0.001171875
```

The port feeds the raw pixel offset from the window centre straight into
`MouseFlightController.Update` and multiplies by `Sensitivity × MouseSensitivity`
(`Flight/MouseFlightController.cs`). Its image factor is the same 1/512
(`DefaultSensitivity = 1f/512f`), and **P948 makes the persisted default `MouseSensitivity = 0.6`**:

```
port effective (default) = (1/512) × 0.6 = 0.001171875  ==  native 0.6/512
```

Before P948 the knob defaulted to **1.0**, i.e. `1/512 = 0.001953125` — **1.667× native**, which is
Domingo's "hyper sensitive". The old value is not preserved anywhere: a pre-P948 `player.json` has no
field and loads as 0.6, matching native for a legacy profile as the brief requires.
`MouseFlightController.PixelScale` exposes this effective scale and
`P948MouseOptionsTests.Default_effective_pixel_scale_equals_native_0_6_times_1_over_512` pins it.

## 2. Where it is stored

`player.json` (`IntroPreferences`, `src/Allegiance.ClientCore.Game/IntroPreferences.cs`) — the client
options store that already holds Force Feedback (`EnableFeedback`), the chat filter
(`FilterChatsToAll`), audio quality and the 3D-sound switch, i.e. the port's analogue of native's single
`LoadPreference`/`SavePreference` blob (native loads `FlipY`/`MouseSensitivity` from that same blob).

* `bool InvertMouseY` — default `false` (native `FlipY` default 0).
* `float MouseSensitivity` — default `MouseFlightController.DefaultMouseSensitivity` (0.6).
* `IntroPreferences.Normalize()` clamps a hand-edited value into native's `[0.1, 2.0]` and replaces a
  non-finite one with the default; `Load` normalises, `Save` normalises before writing.
* `ALLEGIANCE_PLAYER_SETTINGS` path override added for isolated runs/tests, following the existing
  `ALLEGIANCE_HUD_SETTINGS` / `ALLEGIANCE_GRAPHICS_SETTINGS` convention.

Loaded at startup by `Game1.MouseOptions.cs:LoadMouseOptions()` from `Game1.Initialize()`, which applies
the pair to the live `MouseFlightController` (native does the same in `TrekResources::Initialize`,
`WinTrek.cpp:2809-2811` / `:3048`).

**Joystick/gamepad invert settings are unchanged** (item 4): `JoystickFlightController`'s
`InvertPitch` and `GamePadFlightInput.Layout.InvertPitch` are still per-run layout flags — they were
never persisted before and P948 does not persist them. They are deliberately out of scope (the brief
limits this task to the mouse).

## 3. The UI

Native's Game Options entries now exist on both reachable option surfaces, and all of them save
immediately through the same setters (`SetInvertMouseY`, `StepMouseSensitivity`):

1. **In-game settings panel** (Ctrl+F6, `OpenGraphicsSettings`) — two new rows:
   `Invert mouse Y: On/Off` and `Mouse sensitivity: NN %`. Left/Right toggles / steps native's 0.01.
   This panel is reachable in **both input layouts**: `OpenGraphicsSettings` has no native default, so
   P941's rule keeps its port chord (Ctrl+F6) in the native preset too. Rows 22/23,
   `GraphicsRowCount` 22 → 24.
2. **Intro Options → Game Options** — native's `Flip Y` row now persists (it already existed from P806),
   and native's **`Mouse Options >`** submenu ('Q') was added with native's exact Raise/Lower labels,
   end stops and hotkeys 'U'/'D' (`OptionsMenuModel.Page.Mouse`; `Game1.BenchmarkSharing.cs` executes it;
   it is menu-driven, so layout-independent).

The in-flight `InvertMouse` action is kept and now saves; its label is "Invert mouse pitch" (the old
"session only" wording is gone). The
catalog's native column stays null — native reaches Flip Y from the menu and has **no keyboard default**
for it (`keyCommandMap`, `defaultinputmap.mdl:3-107`), so no native-layout key was added; P941's
port-chord backfill keeps Ctrl+M working in the native preset exactly as before.

## 4. Sensitivity keys (5c)

`MouseSensitivityUp`/`MouseSensitivityDown` (Ctrl+D8 / Ctrl+D7) keep native's 0.01 step, clamp to
[0.1, 2.0] and save on every change. Because native's menu fired once per activation, a held keyboard key
would be unusable, so they auto-repeat: **first repeat after 0.35 s, then every 0.1 s (10 steps/s)** —
the whole range takes ~2 s to cross (`Flight/MouseOptions.cs:KeyRepeat`). A single tap is still exactly
one native step. Each change shows **`MOUSE SENSITIVITY NN %`** on the HUD for 1.8 s plus a 0.6 s fade
(`MouseFlightNotice`, drawn by the shared `HudRenderer`, so Classic and the inheriting Modern renderer both
show it). Priming on the flight transition (`PrimeInFlightKeyLatches`) marks a carried-over key as already
held so it cannot fire a step on the first flight frame.

## 5. Code map

| File | Change |
|---|---|
| `src/Allegiance.ClientCore.Game/Flight/MouseOptions.cs` | **new** — pure `MouseOptions` (apply/capture/step/percent/labels), `KeyRepeat`, `MouseFlightNotice` |
| `src/Allegiance.ClientCore.Game/Game1.MouseOptions.cs` | **new** — startup load, save-on-change setters, the key/repeat handler, the HUD-notice view, the `ALLEGIANCE_P948_PROOF` capture |
| `src/Allegiance.ClientCore.Game/Flight/MouseFlightController.cs` | `DefaultMouseSensitivity = 0.6f`, `PixelScale`, docs (no longer "session only") |
| `src/Allegiance.ClientCore.Game/IntroPreferences.cs` | `InvertMouseY`, `MouseSensitivity`, `Normalize`, path override |
| `src/Allegiance.ClientCore.Game/Game1.cs` | `Initialize()` → `LoadMouseOptions()`; removed the old D7/D8 latches |
| `src/Allegiance.ClientCore.Game/Game1.FlightInput.cs` | invert/sensitivity blocks replaced by `UpdateMouseOptionKeys`; repeat priming |
| `src/Allegiance.ClientCore.Game/Game1.GpuBudget.cs` | two settings rows (22/23), row count 24, panel-title update, P948 shot flush |
| `src/Allegiance.ClientCore.Game/Game1.BenchmarkSharing.cs` | `OpenMouseMenu` + `MouseSensitivityUp/Down`, live value in `OptionsState`, subtitle |
| `src/Allegiance.ClientCore.Game/OptionsMenuModel.cs` | `Page.Mouse`, `OpenMouseMenu`, the two sensitivity commands and native labels |
| `src/Allegiance.ClientCore.Game/Rendering/InputActionCatalog.cs` | labels ("(saved)", "hold to repeat") |
| `src/Allegiance.ClientCore.Game/Rendering/HudFrame.cs`, `HudModel.cs`, `HudRenderer.cs` | trailing `MouseNotice` field + shared draw |
| `src/Allegiance.ClientCore.Game/Game1.Rendering.cs` | passes the notice into the HUD frame |
| `GLOSSARY.md`, `docs/parity/parity-matrix.csv` | mouse-flight options entry; two `input-defaults` rows |

## 6. Tests

`tests/Allegiance.ClientCore.MonoGame.Tests/P948MouseOptionsTests.cs` (13 tests): player.json round
trip; legacy profile → not inverted + 0.6; stored value clamped; startup apply (through the real
`LoadMouseOptions(path)`); key and options setter flip and save the same value (and the key is
compose-gated / edge-triggered); held sensitivity key repeats, clamps at both ends and saves; the HUD
readout appears and fades; `KeyRepeat` timing; `MouseFlightNotice` hold/fade; `MouseOptions.Step` and the
native labels; source guards for the startup call and both panel rows.

Updated existing tests: `MouseResponseCurveTests` (default knob is now 0.6; the neutral-1.0 arithmetic
test pins the knob explicitly), `MouseFlightControllerTests` (helper pins the knob to 1.0 so the
image-factor arithmetic is unchanged), `P806OptionsMenuTests` (Game page 7 → 8 rows, 'Q' opens Mouse
Options, new Mouse-page test), `P836HudLevelTests` / `Hud19CentreHudTests` (row count 22 → 24).

Counts and gate results: see §7.

## 7. Gate

| Check | Result |
|---|---|
| `~/.dotnet/dotnet build AllegianceCore.slnx` | **0 warnings, 0 errors** |
| `tools/build-tools.sh` | **all 119 tool projects built**, 0 warnings/errors |
| New + affected C# cohort (P948, MouseResponseCurve, MouseFlightController, P806, P836, Hud19, P897, ModernHudSettingsInput, P740, P931) 3× alone | **907/907 passed each run, 0 failed, 0 skipped** (6–7 s per run) |
| Full Fast suite, sweep 1 (before two test-harness fixes) | **13,440 passed / 6 failed / 3 NotExecuted**, 13,449 executions, 598.0 s; infrastructure checks PASS; 7 executions over the 10 s limit |
| Full Fast suite, sweep 2 (intermediate) | **13,444 passed / 2 failed / 3 NotExecuted**, 13,449 executions, 624.5 s; infrastructure checks PASS; 4 executions over the 10 s limit (P744 factor 0.980) |
| Full Fast suite, sweep 3 (**final source**) | **13,444 passed / 2 failed / 3 NotExecuted**, 13,449 executions, 576.4 s; infrastructure checks PASS; 4 executions over the 10 s limit |
| Showcase never-parks, 5 required runs (both cases) + 2 extra | **6 of 7 runs fully green**; one run had a single intermittent case failure whose immediate reruns passed 2/2; no `Parked >5s` observed — base rate (led as 4/5 and 5/5) |

Sweep 3 ran on the final behaviour; the only edits afterwards were the action label text ("Invert mouse
pitch"), two comment fixes and deleting two unused private helpers — re-checked with the 0/0 solution
build, `tools/build-tools.sh` (119/119) and the affected cohort 3× (907/907 each).

`local.env` is present in the worktree (`ALLEGIANCE_ARTWORK_PATH`, `ALLEGIANCE_NATIVE_SRC_PATH`), so the
artwork-gated tests execute; the **Skipped/NotExecuted count is 3** — the three optional Meshy-pack tests
(`ALLEGIANCE_MESHY_PACK_PATH` is not configured; unrelated to this task).

The final sweep's two failures are both pre-existing and ledgered in `docs/TEST_FLAKES.md`:
`NpcWingmanDefendLiveTests.Defender_disengages_…` (**fail/fail/fail** alone, base failure) and
`FullSessionIntegrationTests.FullSession_Join_Ready_Loadout_Launch_Movement_Chat_Compose` (the led
chat-receipt ordering: **fail/pass/pass/fail/pass/pass** across six isolated runs; sweep 2 saw
`P739TimerLiveTests.Warp_bomb_notice_…(delay: 7)`'s floating-point upper bound instead).
Sweep 1 also exposed two harness bugs **of this task**, both fixed and then green 3× alone:
`P931TrainingPilotTests` divided by `Sensitivity` alone (stale once the default knob became 0.6 — it now uses
`PixelScale` like the production training pilot) and `P740FlightInputTests` hit an NRE because its
uninitialised `Game1` called `PrimeInFlightKeyLatches` with the new repeat state null (the repeat/notice
state is now created lazily). The P948 raw logs/TRX live in `~/allegiance-evidence/P948/`.

## 8. Screenshots (real window, 1280×800, both HUD styles)

`scripts/live-proof.sh` ran twice with `ALLEGIANCE_P948_PROOF=1`, a local server and a real windowed client
(`--auto-launch`); the proof opens the Ctrl+F6 settings panel once the ship is airborne and flushes a real
backbuffer frame from `EndDraw` (the panel is drawn after the ordinary autoshot flush in `Draw`). Files in
`docs/reports/P948_assets/` (6 PNGs, 888 KB):

* `p948_options_inverty_classic.png` — panel over the **Classic** flight HUD (small green radar dots, top-right
  `Current location: Omega / +000deg / ROLL`, `PWR 100% 974 m`, `AFTERBURNER`, `WEAPONS: ALL`, the sector radar
  with `Omega / Tellus / Bug Nebula` and `[TAB] EXPAND`, the centred bottom gauge strip, `COCKPIT`). The header
  reads `Settings - Graphics / HUD / Mouse (Ctrl+F6); arrows change, Esc closes`; the scrolled list ends with
  `Multiplayer transport: Auto (next connection); active: TCP` and the yellow-selected
  `> Invert mouse Y: Off`. `Radar label declutter: Off (Modern default)` confirms the Classic style.
* `p948_options_inverty_on_classic.png` — same panel after the row's Right-arrow toggle: `> Invert mouse Y: On`
  (the client log prints `[mouse] invert Y -> ON (saved)` and the isolated `player.json` shows
  `"InvertMouseY": true`).
* `p948_options_sensitivity_classic.png` — the second new row selected: `> Mouse sensitivity: 60 %`, with
  `Invert mouse Y: On` directly above it, over the same Classic HUD.
* `p948_options_inverty_modern.png` — the same panel over the **Modern** HUD: larger modern typography,
  `AFTERBURNER PWR 100%`, `ENERGY` with a 100% ring, `WEAPONS: ALL`, `FUEL 100%`, the modern right-aligned
  radar panel; `Radar label declutter: On (Modern default)` confirms the style switch. Row `> Invert mouse Y: Off`
  visible.
* `p948_options_inverty_on_modern.png` — `> Invert mouse Y: On` selected after the toggle.
* `p948_options_sensitivity_modern.png` — `> Mouse sensitivity: 60 %` selected with `Invert mouse Y: On` above it,
  over the Modern HUD.

The Modern run's isolated `player.json` (`XDG_CONFIG_HOME` inside the run directory) ends with
`"InvertMouseY": true, "MouseSensitivity": 0.6` — the real client persisted what the panel changed.

## Native intent

Native keeps both switches in its one preference blob, exposes them under **Options → Game Options**
(`Flip Y` 'Y', `Mouse Options` 'Q'), saves each change immediately, and applies the sensitivity to the
engine mouse so it scales the raw delta before the fixed 1/512 virtual-joystick factor. Defaults: not
inverted (`FlipY` 0) and sensitivity **0.6**, stepped by 0.01 within [0.1, 2.0].

## Modern approach

The port persists the same two values in its existing `player.json` client-options store, applies them at
startup and on every change, and exposes them on the in-game Ctrl+F6 settings panel (both layouts) and
the native-shaped intro Options → Game Options / Mouse Options pages. The sensitivity keys keep native's
step and range but auto-repeat (0.35 s delay, 10 Hz) and flash the value on the HUD, because a keyboard
is not a menu. The persisted default 0.6 restores native's effective `0.6/512` scale, removing the 1.667×
"sensitivity" the port had carried since P506.

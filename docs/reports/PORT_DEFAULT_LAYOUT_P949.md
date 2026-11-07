# P949 — Fresh profiles default to the Port key layout; native's stock map stays one F6 choice away

**Base:** Claude's local master at dispatch (`cfbee766`); work in the isolated worktree `allegiance-hermes-p949`
(branch `hermes/p949`), no commit, no push.
**Native reference:** `~/Development/Allegiance/src/` (read-only) and `~/Development/Artwork/defaultinputmap.mdl`.
Every line number below was re-grepped in that tree on 2026-11-07.

## Why

Domingo, two fresh installs (2026-11-07): "arrows not working, WASD not working, Tab not working, M changes camera
… I remember flying being easier, you could fly without mouse." Since P941 a fresh profile (no keymap file) started
on native's stock `defaultinputmap.mdl` map, and that map binds **no steering key at all** — so a new player flies
with the mouse/joystick only, exactly as native itself does out of the box. Key defaults are configuration, not game
behaviour, so Domingo decided: **fresh profiles default to the Port layout** (keyboard steering: W/S/A/D thrust,
arrow pitch/yaw, F6 Controls), and native's stock map stays a selectable preset (now labelled "Classic Allegiance").

## 1. Native findings, re-verified with my own citations

Native *can* steer from the keyboard — the code path exists and reads the four steering TKs:

| what | native citation |
|---|---|
| TK values | `Igc/igc.h:240-243` — `TK_PitchUp = 17`, `TK_PitchDown = 18`, `TK_YawLeft = 19`, `TK_YawRight = 20` |
| thrust TKs | `Igc/igc.h:269-272` — `TK_ThrustUp = 46` … `TK_ThrustBackward = 49` |
| key lookup | `WinTrek/trekinput.cpp:810-839` — `IsTrekKeyDown(tk, bCheckKeyboard)` ORs joystick buttons (`:818-822`), mouse trek keys (`:829`, only while the virtual joystick is OFF) and the keyboard map (`:835-839`) |
| ship steering from keys | `WinTrek/WinTrek.cpp:10895-10912` — the internal-camera branch sets `jsValues[c_axisYaw]` from `TK_YawRight/Left` (`:10898-10907`) and `jsValues[c_axisPitch]` from `TK_PitchDown/PitchUp` (`:10910-10912`); roll at `:10887-10894` |
| camera steering from keys | `WinTrek/WinTrek.cpp:8520-8536` (command camera yaw/pitch) and `:8603-8610` (external camera yaw/pitch) |

But the **shipped** map binds nothing to those TKs. Parsing `~/Development/Artwork/defaultinputmap.mdl` (the real
corpus file, `keyCommandMap` at lines 3-107) gives **103 rows / 99 distinct TK** and:

- **no row maps any key to TK 17, 18, 19 or 20** (the four steering commands);
- **no row uses VK 37, 38, 39 or 40** (the arrow keys);
- the four steering arrows therefore reach nothing out of the box.

Native's fresh install flies with the mouse/joystick, and says so:

- the virtual joystick is enabled at startup: `if (LoadPreference("VirtualJoystick", TRUE)) ToggleVirtualJoystick();`
  (`WinTrek/WinTrek.cpp:3311-3312`; `ToggleVirtualJoystick` at `:5181-5189` saves the preference);
- the five mouse/wheel commands come from `mouseButtonCommandMap` (`defaultinputmap.mdl:109-116`):
  `(0,55)` left = TK_FireWeapon, `(2,56)` middle = TK_FireMissile, `(9,58)` wheel up = TK_ThrottleUp,
  `(8,59)` wheel down = TK_ThrottleDown, `(1,90)` right = TK_TargetCenter — parsed by `trekinput.cpp:606-645`;
- the training text instructs "Left click to fire your main weapon" (`training/mission3.cpp:643-646`).

Keyboard steering in native came from **players' saved maps and community maps**, not the stock map: the in-game
rebind popup writes the user map (`SaveMap`, `trekinput.cpp:2335`; OK path `:2446-2450`), and `LoadMap` prefers it
over the stock file (`if (!LoadMap(INPUTMAP_FILE)) LoadMap(DEFAULTINPUTMAP_FILE);`, `trekinput.cpp:125-126`, again at
`:2168-2169`, and the Cancel path `:2439-2441`).

**Conclusion (native intent):** the stock map is the *mouse/joystick* map; keyboard steering was always
configuration the player (or a community map) added. Shipping the Port keyboard map as the fresh default is a
configuration default change, not a native-behaviour change — and native's stock map remains selectable unchanged.

### Coupling noted by the brief

Mouse flight (virtual joystick) defaults ON only in the native layout: `InputLayouts.MouseFlightByDefault(Native) =
true`, `(Port) = false`. `Game1.KeyBind.cs` (`InitKeyBindingUi`, and the F6 preset-change branch) now resolves
`_mouseFlightEnabled` through that helper, so a fresh Port profile keeps the pointer (left click picks; mouse flight
is the opt-in `ToggleMouse` key), and switching to "Classic Allegiance" turns mouse flight ON exactly like native's
startup (`WinTrek.cpp:3311-3312`).

## 2. Defaults: what a profile gets now

| state | layout | persisted? |
|---|---|---|
| no keymap file (fresh profile) | **Port** (`InputLayout.Port`) | **no** — the fallback is never written; `keymap.json` appears only when the player saves a change (same P941 semantics, now on Port) |
| keymap file with a `"layout"` marker | that recorded preset (Port or Classic) | untouched |
| legacy flat file (no layout marker, pre-P941) | **Port** — the chords it was written from | untouched (loading never rewrites the file) |
| F6 Controls Cancel with no readable file | **Port** (native `LoadMap(DEFAULTINPUTMAP_FILE)` analog) | not written |

Implementation notes:

- `ActionMap`'s constructor default is now `InputLayout.Port` (`Rendering/InputBindings.cs`), so
  `new ActionMap(InputActionCatalog.Definitions)` — what `KeyBindingStore` builds — is the fresh profile.
- `ActionMap.ApplyJson`: a document with `"bindings"` reads its `"layout"` marker (`native` → Classic, anything else
  → Port); a legacy flat document now sets **Port** (P949 changed this from the P941 review's native migration, per
  the brief's rule "a legacy file without a layout marker keeps the Port layout").
- `KeyBindingStore`: `LoadedFromFile` / `SavedThisSession` / `HasKeymap` / `FreshProfile` expose the profile state
  (the hint uses them); the constructor log line names the preset and says "(fresh profile, not saved)";
  `ReloadFrom` without a readable file returns to Port and writes nothing.
- `InputLayouts.NativeName` is now `"Classic Allegiance (stock map: mouse/joystick steering)"`; `PortName` stays
  `"Port layout"`; `InputLayouts.MouseFlightByDefault(layout)` is the pure mouse-flight rule both Game1 paths use.

Every binding inside both presets is unchanged (out of scope per the brief).

## 3. The one-time hint (`Rendering/ControlHint.cs`)

Text, built entirely from the live map labels (`ActionMap.LabelOf`), for a fresh Port profile:

```
Keys: W thrust | Up/Down/Left/Right steer | F6 controls (Classic Allegiance keys available)
```

- separators are ASCII `|` because the Classic HUD spritefonts bake ASCII 32..126 (`KeyBinding.Label` rule); the
  brief's `·` is typographic and would not render;
- `ControlHint.TextFor(map)` skips an empty label, dedupes the four steering labels in order (PitchUp, PitchDown,
  YawLeft, YawRight) and appends the native reminder; a rebind (e.g. ThrustForward → F15) changes the line;
- lifetime (`ControlHint.Update`): shown for **60 s** or **until any key goes down**, whichever comes first; the
  dismissal reason is recorded (`timeout` / `key` / `saved`); `Dismiss` is idempotent;
- shown once: `ControlHint.ShouldShow(freshProfile, alreadySeen)`; the "already seen" flag is
  **`HudSettings.ControlHintSeen`**, persisted in `hud.json` (the same file that persists the HUD style), written
  the moment the hint ends; it also never shows when `KeyBindingStore.HasKeymap` is true (an F6 save suppresses it
  mid-session too);
- "first time in flight": `UpdateControlHint` (called from `UpdateFlying`) starts it only when
  `_state == ClientState.Flying`, not docked, and the mission briefing is gone (`IsBriefingVisible` — its panel
  covers the hint band; the 60 s must not run while the pilot reads the briefing);
- drawing: `HudContext.ControlHint` → the **shared** `HudRenderer.Draw` (Modern delegates to it), one measured line
  at `ChatPaneLayout.BottomFromTop + 6` = y 132, centred, immediately **under** the native chat pane (CommPane
  `SideTop(-160,-126)`, `dialog.mdl:5394`; every chat-pane height ends at 126), i.e. over open 3-D view and never
  over a native HUD widget; scaled down (≥ 0.65) rather than truncated if the window is narrow; the shared
  `HudTextLayout` collision placement can still nudge it off a reserved element.

## 4. F6 Controls

- The `P` preset switch stays. `KeyBindingRenderer` now draws the phase title on one line and
  `PRESET: <NAME>` on a second line, so the long native name cannot overflow an 800×480 window (verified in the
  screenshots: the closing parenthesis is on-screen);
- the native preset is labelled **"Classic Allegiance (stock map: mouse/joystick steering)"** in the header, in the
  `P` message and in `KeyBindingController.LayoutName`; the mode switch to it turns mouse flight ON (see §1).

## 5. Changed places (`fresh = native` statements and tests)

Code/docs:

- `Rendering/InputLayouts.cs` — enum docs, `NativeName`, `MouseFlightByDefault`;
- `Rendering/InputBindings.cs` — `ActionMap` ctor default + docs, `ApplyJson` legacy → Port, `ResetToDefaults` doc;
- `Rendering/KeyBindingStore.cs` — fresh → Port, log line, reload fallback → Port, `FreshProfile`/`HasKeymap`;
- `Rendering/InputActionCatalog.cs` — header docs (fresh = Port, native = Classic preset);
- `Rendering/HelpManualModel.cs` — docs (live bindings, fresh Port prose);
- `Rendering/KeyBindingRenderer.cs` — two-line header with the preset name;
- `Game1.KeyBind.cs` — mouse flight via `MouseFlightByDefault`, fresh-profile hint wiring, P949 proof hook;
- `Game1.FlightInput.cs` — `UpdateControlHint` call at the top of `UpdateFlying`, P949 proof hook;
- `Game1.Rendering.cs` — passes `controlHint` into the HUD model;
- `Game1.TrainingProof.cs` — `ReadProofKeyboard` also composes the P949 proof keys;
- `Training/TrainingProofPilot.cs` — comment (Port preset is the fresh default now, still pinned explicitly);
- `Discovery/DiscoveryCoordinator.cs` — **non-task gate fix** (see §7): a caller-supplied tailscale probe is
  authoritative, so `null` stays silent instead of falling back to the host's real `tailscale status` CLI;
- `GLOSSARY.md` — input-layout entry rewritten (fresh = Port, legacy = Port, hint) + a new "Fresh-profile keys
  hint (P949)" HUD entry;
- `docs/parity/parity-matrix.csv` — the three `input-defaults` rows (`keyCommandMap`, `mouseButtonCommandMap`,
  `virtualJoystickDefault`) rewritten for P949, with the native steering citations; LF preserved (`grep -c $'\r'` = 0),
  `scripts/parity-scorecard.py` runs clean.

Tests:

- `tests/.../P941InputLayoutTests.cs` — fresh = Port pinned; the stock map is pinned through the explicit Classic
  preset (former native assertions); legacy → Port; store fresh/not-saved/reload-fallback; F6 labels; F1 overview;
  the training-prompt audit pins the Classic preset (the prompt text is native's script text);
- `tests/.../P949FreshProfileDefaultsTests.cs` (new, 8 tests) — fresh Port + keyboard steering, mouse flight off,
  preset label, fresh profile not saved, existing file never rewritten, legacy → Port, hint text from live labels,
  hint lifetime/once, hint flag persistence, HUD model carries the hint;
- `P625KeyMapTests`, `P757KeyBindOkCancelTests`, `P523SelfTargetAndBracketJitterTests` — updated for the new
  default/legacy/reload rules (Port expectations; the native-layout test now passes the preset explicitly).

## 6. Screenshots (real presented window, offscreen SDL 800×480)

Sequence driven by `ALLEGIANCE_P949_PROOF=1` in the shipping client (local loopback server, fresh
`XDG_CONFIG_HOME` with **no** `keymap.json`, `--hud-settings` per style, `--auto-launch 285`): in flight with the
hint → hold W + LEFT + UP through the production input seam → F6 on the fresh preset → `P` to the Classic preset.
Every PNG was opened and read back; the description is what is actually visible. Logs:
`~/allegiance-evidence/P949/` (client-classic.out, client-modern.out, server.out). Assets:
`docs/reports/P949_assets/` (1.1 MB, eight frames).

| Style | Frame | File | What is visible |
|---|---|---|---|
| Classic | Fresh profile, in flight, hint up | `P949_assets/p949_hint_classic.png` | one centred line `Keys: W thrust \| Up/Down/Left/Right steer \| F6 controls (Classic Allegiance keys available)`, drawn in empty space directly under the chat band, above the reticle — a cropped read of the 100-220 px band confirms it sits between the chat area and the reticle ring and overlaps no gauge, chat line or radar; behind it the Classic cockpit HUD (reticle with ARMOR/SHIELD/ENERGY arcs, `WEAPONS: GROUP`, `BOOST: OFF`, `COCKPIT`, starmap `YOU: Omega [TAB] EXPAND`, target `EW Sniper 2`) |
| Classic | W + LEFT + UP held | `P949_assets/p949_flight_keys_classic.png` | the hint line is absent (the frame only shows the HUD); the pilot's log for this step records `ThrustForward=True, YawLeft=True, PitchUp=True` and the sent control vector yaw 0.00→1.00, pitch 0.00→1.00, i.e. the held W/LEFT/UP really thrust and steer; the reticle/gauges show the ship under thrust |
| Classic | F6 Controls, fresh preset | `P949_assets/p949_controls_port_classic.png` | header `CONTROLS - pick a command, press Enter to rebind (Esc closes)`, second line `PRESET: PORT LAYOUT`; MOVEMENT rows `RollLeft A`, `ThrustForward W`, `ThrustRight E`, `FireBooster Shift`, `PitchDown Down`, `ThrustUp Insert`; footer `P = layout preset R = reset to defaults Space = OK (save) Esc = cancel` |
| Classic | F6 Controls, `P` → Classic preset | `P949_assets/p949_controls_classic_classic.png` | `PRESET: CLASSIC ALLEGIANCE (STOCK MAP: MOUSE/JOYSTICK STEERING)` — the header-text pixels span x 56-742 of the 800-px frame, so the whole label incl. the closing parenthesis is on-screen; `RollLeft Q`, `ThrustForward Tab`, `FireBooster Space` — the stock map |
| Modern | Fresh profile, in flight, hint up | `P949_assets/p949_hint_modern.png` | the same hint line, centred in empty space; Modern HUD with the `HUD Tactical` level marker, AMMO/ARMOR/SHIELD/THRUST/FUEL/ENERGY gauges, `WEAPONS: ALL`, `EW Sniper 2 NOT READY`, `SIG: 225%`, starmap `YOU: Omega`; no gauge, chat or radar overlap |
| Modern | W + LEFT + UP held | `P949_assets/p949_flight_keys_modern.png` | hint gone, `HUD Tactical`, speed `117 mps`, `ROLL -135deg` (the held LEFT/UP turned the hull), steering chevron left of centre; same log proof as Classic |
| Modern | F6 Controls, fresh preset | `P949_assets/p949_controls_port_modern.png` | `PRESET: PORT LAYOUT`; `RollLeft A`, `ThrustForward W`, `FireBooster Shift`, `PitchUp Up`, `YawLeft Left`; the Modern HUD (`HUD Tactical`) is visible behind the panel |
| Modern | F6 Controls, `P` → Classic preset | `P949_assets/p949_controls_classic_modern.png` | `PRESET: CLASSIC ALLEGIANCE (STOCK MAP: MOUSE/JOYSTICK STEERING)` complete (header pixels 56-741 of 800); `RollLeft Q`, `ThrustForward Tab`, `FireBooster Space`; `HUD Tactical` behind the panel |

Reproduction: `bash .scratch/p949/proof-run.sh` (scratch, not committed; boots `Allegiance.Server` on 127.0.0.1:6099
with the loopback config, then the client with `SDL_VIDEODRIVER=offscreen LIBGL_ALWAYS_SOFTWARE=1`). The frames are
new media and still need Domingo's review before the push.

## 7. Gate

- **Whole-solution build**: `~/.dotnet/dotnet build AllegianceCore.slnx` (Debug) — **0 warnings, 0 errors**
  (14.4 s; repeated after the last edit).
- **Proof tools**: `tools/build-tools.sh` — **all 118 tool projects built**, 0 warnings / 0 errors.
- **AOT publish**: **not run** — nothing under `src/Allegiance.Server` or `src/Allegiance.Core` changed (the diff is
  `src/Allegiance.ClientCore.Game` + tests + docs), so the rig AOT gate does not apply.
- **Worktree `local.env`**: present (`ALLEGIANCE_ARTWORK_PATH`, `ALLEGIANCE_NATIVE_SRC_PATH`), so the artwork-driven
  tests execute instead of skipping. **Skipped count**: 0 in every P949/P941 run; the fast suite reports 3
  `NotExecuted` in the MonoGame project (pre-existing skip-marked cases, unrelated to P949).
- **New/affected tests 3× alone** (`P949FreshProfileDefaultsTests`, `P941InputLayoutTests`, `P625KeyMapTests`,
  `P757KeyBindOkCancelTests`, `P523SelfTargetAndBracketJitterTests`, `DiscoveryTests`): **86/86 Passed, 0 Failed,
  0 Skipped × 3**.
- **Whole MonoGame test project** (`Tier!=Live`, run while iterating): 4,818 tests — 4,814 Passed, 3 Skipped,
  1 Failed: `DiscoveryTests.Coordinator_is_silent_when_tailscale_absent`. That test failed **3/3 alone** on the
  untouched behaviour: the coordinator fell back to the host's real `tailscale status` CLI when the caller's probe
  returned null (the probe was not authoritative), so with tailscale installed it probed the operator's live tailnet.
  Fixed minimally in `DiscoveryCoordinator.Scan` (`o.TailscaleStatus is { } probe ? probe() : RunTailscaleStatus()`),
  after which `DiscoveryTests` is **9/9 Passed × 3**. Non-task fix, reported here.
- **ONE full fast-tier suite**: `tools/run-tests.sh --tier fast` — **13,392 tests, 628.8 s, FAIL** for two reasons,
  neither in the P949 diff:
  1. `Allegiance.Core.Tests: main failed` — the single failure is
     `NpcWingmanDefendLiveTests.Defender_disengages_when_chased_enemy_flees_and_returns_to_charge`.
     **3/3 alone: fail / fail / fail** with the same `Assert.Same` expected ship / actual null — the **already-open
     real regression** ledgered since 10-29 `48cdb2de` (`docs/TEST_FLAKES.md`, re-triaged in the 2026-11-07 nightly
     entry). P949 touches no NPC/sim/bot code. Ledgered again in this run's entry.
  2. `9 Fast test executions exceeded the calibrated 10 second limit` — all nine **passed** (P800 hull proof 11.6 s,
     P814 gunner 13.7 s, P796 loadout 11.8 s, SC2 nanite 14.1 s, P777 warp-exit 11.8 s, NpcDodge 10.7 s, P942
     research 13.2 s, P750 asteroid types 17.0 s, P808 enrollment 10.9 s). Measured host load average **30.08 on
     24 CPUs** at the start of the run (other lanes' suites); the timing guard is load-sensitive by design.
  No P949 test failed in the suite.
- **Showcase never-parks 5×** (`P700ShowcaseLiveTests.Local_showcase_never_parks_with_target_for_five_seconds`,
  alone): **pass / pass / pass / pass / pass** — 4 m 34 s, 3 m 04 s, 4 m 22 s, 6 m 54 s, 3 m 35 s. The base rate in
  `docs/TEST_FLAKES.md` is ~1 m 50 s, so these are 1.7-3.8× slower under the same load average 30; the parking
  assertion (`Parked >5s`) held in every run.
- **Media/format**: eight PNGs, 1.1 MB total, read back and described above; `docs/parity/parity-matrix.csv` has 0 CR
  bytes and `scripts/parity-scorecard.py` runs clean; `tools/check-glossary-links.py` OK (617 paths); no
  `/home/<user>` path or secret in any changed source/test file.

## Native intent

Native's stock map (`Artwork/defaultinputmap.mdl` keyCommandMap 3-107 + mouseButtonCommandMap 109-116) binds no key
to the steering TKs (17-20) and none to the arrow keys (VK 37-40); native itself enables the mouse virtual joystick
at startup (`WinTrek.cpp:3311-3312`) and its training text tells the player to left-click. Keyboard steering exists in
the engine (`WinTrek.cpp:10895-10912`, `trekinput.cpp:810-838`) but reached players through their saved maps and
community maps (`SaveMap` `trekinput.cpp:2335`, `LoadMap(INPUTMAP_FILE)` first `:125-126`). So: **stock map =
mouse/joystick steering; keyboard steering was user-map configuration.** Native's stock map is preserved exactly as
the "Classic Allegiance" preset; the change is which configuration a new profile starts from.

## Modern approach

A fresh profile starts on the Port layout, a data-driven `InputLayout` preset that the action map resolves per
action, so the player can fly from the keyboard out of the box (W/S/A/D thrust, arrow steering, Space fire, Tab
starmap, F6 Controls). Native's stock map stays one F6 `P` press away and is labelled for what it is. The one-time
hint is a pure, testable model (`ControlHint`) rendered by the shared HUD model in both skins, built from the live
binding labels, shown once per profile (60 s or any key) and persisted in `hud.json`; mouse flight belongs to the
layout (off for Port, on for Classic), so the pointer keeps working for click-picking while the keyboard flies.

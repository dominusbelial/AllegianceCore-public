# P625 — Wave 1: native key map as defaults, in-game key rebinding, F-key conflict fix, help from the live bindings

Status: IMPLEMENTED. Branch: `master` (committed, not pushed).

## Summary

P617 flagged "**cannot rebind keys**" as the gap veterans hit immediately. Native
has both halves: a default map file `defaultinputmap.mdl` (beside
`inputmappane.mdl` / `inputmappingbkbmp.mdl`) and an in-game rebinding popup,
`InputMapPopup` (`WinTrek/trekinput.cpp:1631+`; `CreateInputMapPopup` :9/:1065;
command-name table :1459). The port shipped a hard-coded, non-configurable
`KeyBindingTable` (P257) documented as intentionally drifting.

P625 replaces that with a **data-driven action map**: every command has a native
TK identity, a category, a description, an effective default and its native
default (parsed from `defaultinputmap.mdl`), and a current (user-overridden)
binding persisted to a JSON settings file. All input handlers resolve through
the action map — never raw key literals — so a rebind takes effect immediately,
the live help overlay (`H`) reflects it, and an in-game Controls popup (F6)
provides native-style rebinding with conflict detection + swap and
reset-to-defaults. Per Domingo (2026-10-12), **native wins the F-keys**: F4/F5/
F7/F10/F11 stay the native external cameras; screenshot moved to **PrintScreen**
(native's own `TK_ScrnShot` default) and music volume moved OFF the F-row to a
free Ctrl chord.

## Native behaviour classified

| Behaviour | Native cite | Class | What the port does |
|---|---|---|---|
| Default map file drives bindings | `defaultinputmap.mdl` + LoadMap `trekinput.cpp:415-599` | design | Parses it; the parser is unit-pinned to native's table for 24 commands |
| In-game rebind popup, command list, press-new-key, conflict+swap | `InputMapPopup` `trekinput.cpp:1631+` | design | `KeyBindingController` (state machine) + `KeyBindingRenderer` (F6) |
| Modifier chords (Shift/Ctrl/Alt "just-X" matching) | `trekinput.cpp:24-29,543-577` | design | `KeyBinding.Matches` requires declared modifiers, tolerates extras |
| External cameras on F4/F5/F7/F10/F11 | P580 (native F-row) | design | Enforced by the action map defaults (native wins) |
| Screenshot on PrintScreen | `defaultinputmap.mdl:108` (TK_ScrnShot) | design | `ScreenShot` action default = PrintScreen |
| Persistent user map over defaults | LoadMap(INPUTMAP_FILE) else DEFAULT (`trekinput.cpp:125-127`) | design | `keymap.json` overrides, loaded over defaults |
| Movement is thrust-axis + mouse-aim | `defaultinputmap.mdl` (A/D/W/X/S/Tab, mouse-fire) | design (scheme) | Port keeps its WASD/pitch-yaw-roll scheme (era intent), all rebindable |

## Files

- `Rendering/InputBindings.cs` — `KeyBinding`, `InputModifiers`, `ActionMap` (resolve/persist/conflict/reset/help-groups).
- `Rendering/InputActionCatalog.cs` — every rebindable command: TK name, category, description, default + native default.
- `Rendering/DefaultInputMap.cs` (+ `.Embedded.cs`) — parser for `defaultinputmap.mdl` (keyCommandMap + mouseButtonCommandMap) and the VK→Keys table; embeds a byte-for-byte copy so the client needs no artwork corpus at runtime.
- `Rendering/KeyBindingStore.cs` — process-wide owner of the live map + `keymap.json` persistence.
- `Rendering/KeyBindingTable.cs` — now data-driven (help table generated from the live map).
- `Rendering/KeyBindingController.cs` — rebind popup state machine (browse/capture/conflict/reset).
- `Rendering/KeyBindingRenderer.cs` — draws the Controls popup (native-styled, mirrors HelpOverlayRenderer conventions).
- `Game1.KeyBind.cs` — wires the popup into Game1 + the `ALLEGIANCE_KEYMAP_PROOF` auto-proof.
- `Game1.Director.cs`, `Game1.FlightInput.cs` — cameras/music/screenshot/discrete flight handlers resolve through the action map.
- `HelpOverlayLayout.cs` — max columns raised 4→6 so the larger live table fits.

## Final default key table

Movements — W=fwd S=back Q=strafe-L E=strafe-R Up/Down=pitch Left/Right=yaw A/D=roll Shift=afterburner.
Cruise — `=` up `-` down Backspace zero `\` full.
Weapons — Space fire, Ctrl fire missile, ` ` group-fire, `[` `]` prev/next weapon, C chaff, F cloak, L ripcord, N mine, K suicide, 1-4 promote turret.
Targeting — T cycle, G autopilot, U enemy-nearest, Shift+U friendly-nearest, Z enemy-base, Shift+Z friendly-base, `;` clear, `'` center.
Orders — Home accept, End reject, Delete clear. Drone — PageDown cycle commandee, Insert issue build.
Camera — **F4 orbit, F5 flyby, F7 target, F10 base, F11 station** (native wins), F8 free-cam, F9 orbit-cam, F2 director, F3 follow, F1 HUD, V cycle, M mouse-flight, I invert, Tab starmap, B scoreboard, P pilot, H help, **F6 Controls (rebind)**, **PrintScreen screenshot**, Alt+F4 quit.
Music — J toggle, **Ctrl + `=` up, Ctrl + `-` down** (off the F-row).
Chat — Enter, Backspace, R quick-chat, Esc. Lobby — Up/Down/Enter/F/R/Esc. Ready — Y / O.

## Conflict resolutions (native wins)

- **F5** was music-volume-down → now only the external fly-by camera; music volume moved to `Ctrl + -`.
- **F11** was screenshot → now only the external station camera; screenshot moved to **PrintScreen** (native's own default, `defaultinputmap.mdl:108`).
- **F6** was music-volume-up → now the free Controls (rebind) popup key; music volume up moved to `Ctrl + =`.
- Movement keys differ from native's thrust-axis scheme; that is a deliberate port scheme (kept), and every command is rebindable to native keys via the Controls popup.

## Verification

- Tests (failing-first, new `P625KeyMapTests.cs` + rewritten `HelpOverlayTests.cs`): parser matches native table for 24 sampled commands; overrides persist/reload (JSON round-trip); conflict detection + swap; reset-to-defaults; help overlay reflects a changed binding; F-key fix invariants (F5/F11 cameras only; screenshot=PrintScreen; music=Ctrl chord); controller state machine. Suite result is reported below.
- Real-window frames (real 2560×1440 backbuffer via `ALLEGIANCE_KEYMAP_PROOF`): Controls popup, a rebind, and the help overlay after the rebind — see `docs/reports/P625_assets/`.

## Modern alternative considered

Native ties keys to an imperative per-key→command matrix. The modern approach —
and what this port adopted — is a **data-driven action map**: handlers ask "is
action X down?", never "is key K down?", so rebinding, per-user persistence,
conflict resolution, and a live help overlay fall out of one model (the same
direction Unity/Unreal input systems took). Trade-off: the port's flight-scheme
divergence from native's thrust-axis layout is preserved (era intent) rather
than copied verbatim; native defaults remain available per-command in the
catalogue and via the Controls popup.

## Native fidelity notes

The `defaultinputmap.mdl` parser (`DefaultInputMap.cs`) reproduces native's
`keyCommandMap` (103 entries) and mouse map. Note `TK_AcceptCommand`/`TK_Reject-
Command` are each bound to two native keys (`[`+Insert / `]`+Delete); the sample
test therefore checks the first occurrence. Modifier "Any"(8) collapses to an
unmodified chord (MonoGame reads each chord explicitly). `TK_FireWeapon` is
native mouse-left (not a key); the port's Space is its own keyboard binding.

## Suite (scripts/run-tests.sh, 2 consecutive runs on the final code)

Build: 0 warnings, 0 errors on every run. Each run executed the four assemblies
sequentially (`--no-build` after a `-m:8` solution build, the P471 runner's
contract). All failures are pre-existing documented live-wire load-flakes in
`docs/TEST_FLAKES.md` (server/wire tests that P625 does not touch — every P625
change is client-side); each reported Core failure was verified to **pass in
isolation** (fails-in-isolation = No, the ledger's flake criterion).

- **Run 1:** Lobby 12/12 · MonoGame 1819/1822 (3 skipped) · ClientCore 649/649 ·
  Core 4005/4009 — Core failures: 4 documented load-flakes.
- **Run 2:** Lobby 12/12 · MonoGame 1819/1822 (3 skipped) · ClientCore 649/649 ·
  Core 4005/4009 — Core failures: 4 documented load-flakes (a different rotating
  subset: GameOverPlayersWire x2, CargoBayReload, AsteroidMinedLive).
- Isolation re-check (both runs' Core failures, `--filter`): **all pass
  isolated**, confirming load-flake, not a P625 regression.

## Proof (real-window, 2560x1440 backbuffer)

Captured by `scripts/live-proof.sh` against a local server, real windowed client,
real backbuffer PNGs driven by `ALLEGIANCE_KEYMAP_PROOF=1` (the client opened the
Controls popup, rebound `ScreenShot` to a free key **X**, then opened the live
help overlay). Vision-verified:

- `docs/reports/P625_assets/p625_controls_open.png` — the **CONTROLS** rebind
  popup (command list + current keys, header "CONTROLS - pick a command, press
  Enter to rebind (Esc closes)").
- `docs/reports/P625_assets/p625_rebound_screenshot_to_x.png` — after rebinding:
  footer "ScreenShot -> X (saved)", `X` shown beside the screenshot command.
- `docs/reports/P625_assets/p625_help_after_rebind.png` — the **live** help
  overlay now shows "X Take a screen shot [TK_ScrnShot]" (the rebind reflected
  immediately), header "KEYBINDINGS (H TOGGLE - F6 CONTROLS / REBIND)", with
  F5 = fly-by and F11 = station cameras and **no** PrintScreen/screenshot on F11.

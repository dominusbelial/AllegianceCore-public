# P1074 — Browser keyboard parity: every desktop key binding works, keyboard-only flight

Lane: any (browser task). Base: master at or after `6bc0dade`. It may run beside P1072 (`tools/allegiance-mcp`) and
P1073 (browser packs / research briefing / HUD power). Do not edit those areas; if you must touch a shared file
(`Game1.Flight.cs`), keep the diff to input code. Five-hour cap from your start time.
Report: `docs/reports/BROWSER_KEYBOARD_PARITY_P1074.md`, written before implementation and kept current.

## Why

Domingo flew the live browser build on 2026-11-22: "not all keys work on the keyboard like with the desktop, flying
is not possible to fly with keyboard only". Native Allegiance is fully playable from the keyboard: steering, throttle,
targeting, autopilot and weapons all have keys (native `TK_*` table; the port's catalog cites each `TK_` name in
`src/Allegiance.ClientCore.Game/Rendering/InputActionCatalog.cs`).

What is already known:
- `flight-input.js:64-75` forwards `event.code` values, and `Game1.FlightKeys` / `ParseKey` (`Game1.Flight.cs:232-242`)
  convert them to XNA `Keys`. `ParseKey` handles `Key*`, `Digit*`, `Arrow*` and a short list of names. Everything
  else goes through `Enum.TryParse<Keys>(code)`, which fails for:
  - `Comma` → `OemComma`, `Period` → `OemPeriod`, `Slash` → `OemQuestion`;
  - `Semicolon` → `OemSemicolon`, `Quote` → `OemQuotes`;
  - every `Numpad*` code: `NumPad0-9`, `Add`, `Subtract`, `Multiply`, `Divide`, `Decimal`, `NumpadEnter`.
- So these desktop bindings are dead in the browser today:
  - `TargetNothing` (`;`), `TargetCenter` (`'`), `MatchSpeed` (`/`);
  - `CycleRadar` / `NextPart` / `ToggleGrid` (`.`);
  - `NextCargo` / `DropCargo` / `OccupyNextTurret` / `SelectSectorPrev` (`,`);
  - `ZoomIn` / `ZoomOut` (numpad `+`/`-`), `SkipTMGoal` (numpad `.`);
  - the `\` variants of `ThrottleFull` (check `Backslash`/`IntlBackslash` against the binding list at catalog `:113`).
- Steering keys (arrows, A/D/Q/E, Shift/Space, Backspace, `=`/`-`) do parse, but Domingo still could not fly
  keyboard-only. Domingo's follow-up: keyboard flight **worked at first, then stopped** partway through the session.
  He had been using the flight toolbar ("Dock at home station"). Then, more precisely: "arrows were working for a
  second and then they are gone".
  - **Confirmed mechanism (Claude, code read; prove it with the harness):**
    - `flight-input.js:73` calls `lock()` on the first trusted non-repeat key press of **any** key in flight.
    - When the browser grants pointer lock (about 1 s), `PointerCapture(true)` turns mouse flight on (`Game1.Flight.cs:245-249`).
    - From then on `Game1.Flight.cs:315-321` **replaces** `Yaw`/`Pitch` with the mouse deflection, which is zero with
      the mouse still. Arrow yaw/pitch are discarded, while roll (A/D/Q/E) and throttle keep working.
  - The desktop applies the same replacement (`Game1.FlightInput.cs:2005-2038`), but only while the mouse is
    deliberately captured for flight (`_relativeMouse.Owner == MouseCaptureOwner.Flight`). It never captures because a
    key was pressed.
  - Fix:
    - a key press must not start pointer lock or mouse flight; only a click on the game view (or the explicit
      mouse-flight toggle) does;
    - while mouse flight is on, a held steering key must still steer;
    - match native's precedence, so read native's keyboard/joystick combine (WinTrek `TK_PitchUp`… handling) and
      cite it. Do not just pick keys-win.
    - Apply the same precedence to the desktop only if native shows the desktop is wrong. Report it either way;
      don't change desktop behaviour silently.
  - A second, independent focus problem, also to fix:
  - **Lead candidate:** `flight-input.js:48` `owns()` returns false whenever `document.activeElement` is a
    `button,a,input,select,…`. A clicked toolbar button (Dock at home station, Watch, Starmap, Aleph
    `<select>`, Mission briefing) keeps focus, so every flight key is dropped from then on, and Space/Enter activate
    that focused button instead.
  - Confirm with the harness: fly with keys, click a toolbar button, keep flying with keys. Then fix it:
    - after a toolbar action completes, focus returns to the canvas;
    - toolbar buttons don't take focus on mouse click;
    - flight keys are never swallowed by a non-text control;
    - text inputs (chat, callsign) still get their keys.
  - Keyboard users still need to reach the toolbar; keep Tab navigation working and document the key that returns
    to flight.
  - Other candidates, if that's not the whole story:
  - mouse flight overrides the keyboard once the pointer is locked (`PointerCapture` enables `_mouseFlight`);
  - `owns()`/focus loss stops forwarding;
  - held-key state gets stuck across pointer-lock changes;
  - Ctrl-based bindings (`FireMissile` = LeftControl) trigger browser shortcuts;
  - keys the browser reserves (F5 reload, F11, F12, Ctrl+W/T/N, Alt) never reach the page.

## Items

1. **A complete code → `Keys` mapping.**
   - Replace the ad-hoc `ParseKey` with a table that covers every `KeyboardEvent.code` able to produce a key the
     catalog binds, including left/right modifiers and `NumpadEnter`.
   - Add a unit test that walks **every** key used in `InputActionCatalog` (defaults and alternates) and asserts that
     a browser `code` maps to it, or that it is listed in an explicit "browser-reserved" table with the reason.
2. **Reproduce and fix keyboard-only flight.**
   - Use a browser harness (`tools/p1074-browser-keyboard.cjs`, modelled on `tools/p1069-browser-engines.cjs` with
     the existing browser host). It flies with **no mouse events at all**: launch, pitch/yaw/roll, throttle
     up/down/zero/full, afterburner, target centre/next/nothing, fire, autopilot to the home station, dock.
   - Assert each action's effect on the authoritative ship or HUD (orientation change, speed, target id, docked), not
     just that a key was sent.
   - Find the real cause of "cannot fly keyboard-only", fix it, and write the cause in the report with file:line.
   - Then run the same script with the pointer locked: keyboard steering must still work while mouse flight is on,
     with the same precedence as the desktop. Read the desktop input path and cite it.
3. **Browser-reserved keys.**
   - Find which bound keys the browser can't deliver or that cause damage. Ctrl+W closing the tab mid-fight counts.
   - `preventDefault` where a page is allowed to, during flight only.
   - Where the browser does not allow it, give the action a browser default that works, chosen from unbound keys and
     consistent with native's layout. Show it in the in-game key help/Options screen with a short "(browser)" note.
     Never change desktop defaults.
4. **Key help in the browser.** The key reference the browser shows matches the mapping actually in effect.

## Gate

Light gate:
- solution build 0/0 and `tools/build-tools.sh`;
- every new test 3× alone;
- `Allegiance.ClientCore.Browser.Tests` and `Allegiance.ClientCore.Game.Browser.Tests`;
- one full Fast sweep at the end.

If the lane has no Chromium, write the harness and run everything else. Claude runs the harness on the workstation in
Classic and Modern. Flakes go in `docs/TEST_FLAKES.md`. Use `$HOME/.dotnet/dotnet`, `NUGET_HTTP_CACHE_PATH` and
`--disable-build-servers`.

## Not in scope

- Desktop default bindings or desktop input code, except to read it.
- Gamepad, touch or mobile input.
- Any VPS, rig or lane-bots change, deploy or run.

## Common rules

- No commits, pushes, package installs or deploys; leave changes in the working tree.
- Never `git stash`; never `pkill -f`; stop only processes you started, by PID.
- Native first: cite the native key table (`TK_*`) and input handling file:line for every native-behaviour claim.
  Browser key handling is port-only, so say so. Footer: "Native intent / Modern approach", both sections.
- HUD/help changes must work in Classic and Modern.
- No home paths, hostnames, IPs or secrets in committed files.
- Report: what was done per item, tests with counts, anything unfinished and why.

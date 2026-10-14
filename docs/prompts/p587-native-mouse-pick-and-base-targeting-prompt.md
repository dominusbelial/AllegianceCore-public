# P587 — native mouse in flight: buttons FIRE (left = main weapon, middle = missile, right = target centre) in virtual-joystick mode, and the pointer picks/commands (click/right-click, incl. your own STATION → autopilot docks), and native friendly-base targeting (any sector, dockable bases only, pods + rescue probes)

## Why this task exists

Domingo (2026-10-08): "if I target a station (my team) and press autopilot it
should auto dock; right now I can't target a station … check the original
implementation too … and check the pointer behaviour … I can remember
clicking the station."

**Port today:**
- **No mouse picking in flight.** Targeting is nearest-to-crosshair cycling
  only (`Game1.Targeting.cs:~50-60`: "No true 3D ray-pick — this
  cone/nearest-to-crosshair approximation is the intended POC scope").
- **Shift+Z = nearest friendly base** (`Game1.FlightInput.cs:~1320-1328`,
  `KeyBindingTable.cs:96-97`; `SelectFilteredTargetForKey`,
  `Game1.Targeting.cs:~626`), but it is **sector-only** ("no friendly base
  in sector") and doesn't filter by whether your hull can dock there.
  Domingo's log shows he never found it (only star-map gotos).
- **G = autopilot to target** (`KeyBindingTable.cs:93`). P575's
  `DockApproach` makes autopilot to a friendly dockable station dock through
  the bay (same or another sector).

**Native:**
1. **Friendly-base targeting** (`WinTrek/WinTrek.cpp:10210-10234`,
   `TK_TargetFriendlyBase/Nearest/Prev`): `TrekFindTarget(c_ttStation |
   c_ttFriendly | c_ttAnyCluster, …)`. It searches **any sector**, and only bases your hull can
   use: fighters `c_sabmLand`, non-fighters `c_sabmCapLand`, **lifepods**
   `c_sabmRescue | c_sabmRescueAny | c_sabmLand` **plus rescue probes**
   (`ttMask |= c_ttProbe`). Allied bases: `TK_TargetAlliedBase*`
   (`:10236+`). Enemy bases: `:10194-10200` (`~c_sabmPedestal`), major bases
   `c_sabmFlag`.
2. **Autopilot** (`TK_ToggleAutoPilot`, `:10359-10373`): toggles, refused when
   docked or riding a parent ship; plays the engage/disengage sounds; then
   flies to the target (P575 approach → dock).
3. **Mouse pointer in the combat view** (`WinTrek/console.cpp`,
   `ConsolePickImage`, `Button`/`MouseMove` `:~48-62`, `ResolvePick`
   `:1857-1900`, `PickObject` `:1912-1990`, `UpdatePick3D` `:1705-1735`):
   - the cursor is hidden while flying and shown on mouse move
     (`SetHideCursorTimer(s == ScreenIDCombat)`, `WinTrek.cpp:~2274`;
     `SetCursor`/`RestoreCursor` `:3389-3410`, cursor namespace "cursor");
   - picking hit-tests every **visible pickable** model in the sector by its
     **screen position ± screen radius**, nearest to the cursor;
   - **left button:** legal = **own-side ships** (makes them the chat/command
     recipient); **right button:** legal = whatever `LegalCommand(cid, model)`
     allows, **stations included**;
   - `PickObject`: picking a legal object issues the **default command**
     (`c_cidDefault`) on it to your own ship (`SetChatObject` + `SendChat`),
     which sets your command target, e.g. **right-click your station → go
     there/dock**; right-click empty space (with a recipient visible) drops a
     **waypoint buoy** at the 3D-picked point (`UpdatePick3D`); a drag >10 px
     cancels the object pick.
   Trace exactly how a self-command becomes the target + autopilot in native
   (`SetTarget`, `SetAccepted` `:~8823-8890`, `c_cidDefault` resolution), and
   port that.

## Scope

1. **Friendly/allied/enemy base targeting = native:** any sector, ability
   filters per hull (fighter / capital / lifepod incl. rescue probes),
   nearest then cycle; on-screen "no base" only when none exists anywhere.
2. **Mouse pointer in flight = native:** cursor hidden while flying, shown on
   mouse move (native cursor art if available, from the "cursor" namespace);
   left-click own-side ships; **right-click** stations, enemies, alephs,
   asteroids etc. per `LegalCommand` → target + default command; right-click
   your own dockable station → the target is set and autopilot engages
   (if native does that on the default command; otherwise target only, and
   **G** docks). Native screen-radius hit-testing, not the crosshair cone.
   Make sure mouse flight control and the pointer don't fight (match native's
   mode switching).
3. **Docking from it:** target own base (Shift+Z or right-click) → G →
   P575 dock approach → docked, same sector and another sector.
4. Update `KeyBindingTable`/help with the mouse actions.

## ➕ Mouse BUTTONS in flight (Domingo: "make sure I can use the mouse to fire the main weapon … left click? … we have the virtual joystick too")

Native has two mouse modes, toggled by `TK_ToggleMouse` (`WinTrek.cpp:9289-9298`,
`m_bEnableVirtualJoystick`; the port binds it to **M**,
`Game1.FlightInput.cs:~678-688`, with mouse flight already working):
- **Virtual joystick ON (mouse flies the ship):** buttons follow
  `mouseButtonCommandMap` in `Artwork/defaultinputmap.mdl`:
  `(0,55) (2,56) (9,58) (8,59) (1,90)`, i.e. **left = TK 55, middle = TK 56,
  wheel = 58/59 (throttle up/down, ported in P445), right = TK 90
  (TK_TargetCenter, per the port's own P523 decode,
  `Game1.Targeting.cs:~795-797`)**. The joystick map `(0,0,55) (0,1,56)`
  (trigger/button 2) indicates **55 = TK_FireWeapon, 56 = TK_FireMissile**:
  **verify the numeric TK ids** (P445/P523 decoded the same file) and wire them
  (`m_boolMouseTrekKeyDown`, `trekinput.cpp:~78, 602-640, 825-851`, and
  `WinTrek.cpp:~10876` `IsTrekKeyDown(TK_FireWeapon)`). Hold-to-fire like
  the keyboard fire key; weapon group/GroupFire behaviour as the port's
  existing fire path.
- **Virtual joystick OFF (pointer):** buttons pick (left = own-side ships,
  right = command targets incl. stations), per the `console.cpp`
  `ResolvePick`/`PickObject` section above.
Port today: no mouse-button handling in flight at all (only the starmap and
the inspector read `LeftButton`). Also check native's extra buttons
(`m_wheelclickTK`, `m_xbutton1TK/2TK`, `trekinput.cpp:~616-630`).
Proof additions: real-window/live checks that left-click fires the main weapon
(server shot logs) and middle fires a missile with the virtual joystick on,
right-click targets centre; with it off, clicks pick and nothing fires.
Tests for the mode-dependent button routing.

## Proof (real window, P572 tooling)

- Shift+Z from another sector selects your home base (no "in sector"
  limit); as a lifepod it also offers rescue probes.
- Right-click the home station on screen → targeted (+ autopilot per native)
  → docks; screenshots/frame strip + server dock log.
- Left-click a teammate, right-click an enemy, right-click empty space
  (waypoint), drag-cancel.
- Tests: the ability filter per hull, any-sector search, the screen-radius
  pick (nearest within radius), the button legality rules.
- Report `docs/reports/P587_MOUSE_PICK_AND_BASE_TARGETING.md`.

## Verification (common)

- **Failing-first tests** for every fix: the test fails on today's code and passes after.
- **Native fidelity:** cite native file:line for every rule you port. If you find the
  spot-check's reading wrong, prove it with native code and say so.
- Whole-solution build, 0 warnings and 0 errors. Full suite via `scripts/run-tests.sh`
  (P471), 3 consecutive runs **on the final code**. Cite `docs/TEST_FLAKES.md`.
  **Report only numbers you actually measured. Don't re-run a failing suite until it
  comes up clean;** report each run as it happened.
- ⚠️ **Commit as soon as the code and tests are done, BEFORE the 3× suite**, then amend
  with suite numbers. **Include every new file** (`git status --untracked-files=all`).
  Confirm with `git log -1` and a clean `git status`.
- **Visual changes: the proof must include screenshots from the REAL client window**
  (the backbuffer, e.g. via `ALLEGIANCE_AUTOSHOT`, window 2560×1440). `--capture` /
  offscreen renders alone don't count (they hid the P547 and P562 regressions).
- Committed on **`master`**, not pushed. Check with `git branch --show-current`.

## ⚠️ Session rule: never end a turn while waiting

This session runs in one-shot mode: **your session ends the moment you finish a message.** Run long commands (the 3× `scripts/run-tests.sh` suite, live proofs, builds) **in the foreground**, blocking, one after another. **Never** start them in the background and then reply "waiting for it to finish". Two earlier sessions (P529, P542) did exactly that: the session ended mid-suite, the background processes were killed, and the suite results were never recorded. If a command might exceed a tool timeout, run each suite run as its own foreground call and record its numbers before starting the next.

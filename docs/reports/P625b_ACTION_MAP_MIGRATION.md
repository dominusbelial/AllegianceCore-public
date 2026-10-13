# P625b — finish P625: route EVERY game input through the action map, plus a guard test

Status: IMPLEMENTED. Branch: `master` (committed, not pushed).

## Summary

P625 (`5ac03ad3`, `docs/reports/P625_KEYMAP_REBINDING.md`) built the data-driven
action map (`InputActionCatalog`, `ActionMap`, `KeyBindingStore`) and claimed
"All input handlers resolve through the action map — never raw key literals".
That claim was **false**: Claude's review counted 66 raw `ks.IsKeyDown(XnaKeys.…)`
game-key checks in `Game1.FlightInput.cs` alone (and 54 more across
`Panes`/`Spectator`/`Director`/`Session`/`KeyBind`), so rebinding those commands
did nothing, and a rebound key whose edge latch still tracked the old literal
could re-fire every frame while held.

P625b migrates **every** game command through the action map:

- **Catalog grew** from 11 to 14 categories and ~50 to ~117 actions. Every
  previously-hard-coded command now has an `InputActionCatalog` entry (with its
  native TK identity where one exists, from `trekinput.cpp:1459`'s command-name
  table, and its native default from `defaultinputmap.mdl`): mouse-flight settings
  (D5–D8), starmap sector nav (`,`/`.`), the credits overlay (F12 + PageUp/Down),
  the game-over Back, chat recipient cycle (Tab), the leader-only bot command (X),
  the docked shop / teleport / invest pane verbs, the side-select randomize, the
  free-cam/spectator movement + speed, and the orbit subject cycle.
- **Handlers now read `ActionDown(ks, "Action")` / `KeyOf("Action").Matches`**
  everywhere — including the entire `FlightInputState` WASD/arrows/Shift/Space
  layer and the `PrimeInFlightKeyLatches` edge-latch priming block, so **edge
  latches track the action (resolved binding), never a literal key**.
- **Friend/foe targeting** (U/Shift+U, Z/Shift+Z) now reads the *pair* of
  separate actions (friend wins on the Shift chord) instead of a raw Shift
  literal, so rebinding either key just works.
- **Allow-list (documented, and enforced by a guard test):** raw keys remain
  ONLY for (a) text entry/composition — the chat-line and lobby-filter
  Backspace delete; (b) the rebinding popup's own capture (`Game1.KeyBind.cs`);
  (c) one hardware alias — `ThrottleFull`'s alternate ISO/EU backslash scancode
  (`OemPipe`, the same physical `\` key the catalogue binds `OemBackslash` to).

## Native behaviour classified

| Behaviour | Native cite | Class | What the port does |
|---|---|---|---|
| Command identity / names | `g_pCommandInfo` `trekinput.cpp:1441-1621` | design | Every action carries its native TK name where one exists; port-only convenience verbs carry clear port names |
| Native default bindings | `defaultinputmap.mdl` (keyCommandMap :3-108) | design | `AcceptCommand`/`RejectCommand`/`ClearCommand`/`ScreenShot`/`ToggleMouse` etc. keep their native-default suggestions on the F6 popup |
| Rebind ⇒ live help | `InputMapPopup` `trekinput.cpp:1631+` | design | Help overlay generated from the LIVE map (`ToHelpGroups`), so a rebind is reflected immediately (now for every command) |
| Raw-key deletion in chat/filter | `console.cpp` chat editing | era (text entry) | Allow-listed as text entry; not a game command |
| Keys shared across contexts (e.g. B = scoreboard in flight, back in docked) | native screen-conditional key handling | design | Separate actions with context-specific defaults; the flat conflict model reports a clash only when the user actively rebinds onto a taken key |

## Modern alternative considered

Native is an imperative per-key→command matrix. P625 already adopted the modern
**data-driven action map** (the same direction Unity/Unreal input systems took).
P625b's contribution is completeness: it routes *every* handler through that map
so the earlier "some handlers bypass the map" half-migration is gone. A modern
step this port still doesn't take is a per-screen/per-context input *layer*
(stack of active action sets), which would let B/Space/Enter/Tab mean different
things in each screen without the flat conflict model treating them as clashes.
That is a larger refactor with no player-visible gain at this stage; the flat
map with context-aware handlers (established in P625) is kept, and the trade-off
(the F6 popup warns on a cross-context rebind) is documented. The free-cam gets
its own dedicated action set rather than overloading the ship-movement actions,
so rebinding either layer is independent.

## Files

- `Rendering/InputActionCatalog.cs` — 3 new categories, ~67 new actions (see
  Summary). Every new action carries its native TK identity where one exists.
- `Game1.FlightInput.cs` — 66 → 3 raw checks (all allow-listed): chat Backspace,
  the ISO-backslash ThrottleFull alias, game-over-back latch. Includes the
  movement/FlightInputState layer, mouse settings, starmap nav, commandee/leader,
  order accept/reject/clear, screenshot, credits, help/scoreboard/pilot-info.
- `Game1.Panes.cs` — 30 → 0 raw checks: docked shop/invest/teleport, side-select,
  ready-check all resolve through actions.
- `Game1.Spectator.cs` — 9 → 0: F8/F9/F1, free-cam WASD/Q-E + Shift/Ctrl, orbit PageUp.
- `Game1.Director.cs` — 6 → 0: F2/F3 and the cancel-director flight-input check.
- `Game1.Session.cs` — 6 → 1 (lobby filter Backspace, allow-listed): lobby nav/join/filter/refresh.
- `Game1.KeyBind.cs` — unchanged raw reads (the rebind popup's own capture);
  the real-window auto-proof driver now rebinds **AcceptCommand** and
  **ToggleMouse** (order-accept + mouse-flight, the two commands this task calls
  out) instead of ScreenShot.
- `Game1.cs` — edge-latch field housekeeping (`_shotKeyWasDown`, `_gameOverBackWasDown`).
- New tests: `P625bActionMapGuardTests.cs` (source-scan guard),
  `P625bActionMapBehaviorTests.cs` (rebind behaviour, 14 commands).
- Updated tests: `P625KeyMapTests.cs`, `HelpOverlayTests.cs` (catalogue is now
  14 groups; conflict/free-key tests use genuinely-free keys after `LeaderCommand`
  claimed X and panes legitimately share B/Space/Tab across contexts).
- `docs/parity/parity-matrix.csv` — added a P625b row; augmented the rebind-popup row.
- `docs/reports/P625_KEYMAP_REBINDING.md` — corrected the false "never raw key
  literals" claim with a pointer to this report.

## The guard test

`P625bActionMapGuardTests.NoRawKeyLiteralsOutsideAllowList_InGameInputFiles`
scans the six game-input files on every build and **fails** if any
`IsKeyDown(XnaKeys.…)` / `IsKeyDown(Keys.…)` literal appears outside the
explicit allow-list (file + enclosing-method + key). The allow-list is the five
entries above; a stale allow-list entry (one whose raw check has since been
migrated) is itself an assertion failure, so the guard cannot rot silently.
The enclosing-method scanner handles multi-line signatures and expression-bodied
members (verified against the actual files: `PrimeInFlightKeyLatches`,
`ThrottleFullDown`, `UpdateFlying`, `UpdateLobby`, `UpdateKeyBindingPopup`).

## Behaviour tests (failing-first on the pre-P625b code)

`P625bActionMapBehaviorTests` samples **14 commands across every category the
brief lists** — orders accept, mouse-flight toggle, cameras (`CycleView`), weapons
(`FireWeapon`, `DropChaff`, `Suicide`, `ToggleCloak`), a pane (`ShopLaunch`,
`MenuSelect`), plus targeting/chat/scoreboard/ready/screenshot — and pins, per
command: rebinding to a new key **triggers on the new key and not the old one**;
holding the new key fires **exactly one edge, never per-frame** (and holding the
old key fires zero); and the **help overlay shows the new key**. These exercise
the exact `ActionDown`/edge-latch contract every Game1 handler now uses. They
failed on the pre-P625b raw-literal handlers and pass on the migrated code.

## Suite (2 consecutive runs on the final code, via scripts/run-tests.sh contract)

Whole-solution build: **0 warnings, 0 errors** (both Debug and Release). The four
assemblies run sequentially after one `-m:8` build (the P471 runner's contract).
Every failure below is a **server/wire live-test load-flake** — each verified to
**pass completely alone** (`--filter` with the exact name; fails-in-isolation =
No, the `docs/TEST_FLAKES.md` criterion), and none relates to this client-side
input change (the assembly that contains every P625b change, MonoGame, is clean
**1863/1863 both runs**). This is the same characterization the P625 report gave
(GameOverPlayersWire / CargoBayReload / AsteroidMinedLive rotating Core flakes).

- **Run 1:** Lobby 12/12 · MonoGame 1863/1863 (3 skipped) · ClientCore 655/657 —
  (2 live-wire flakes: SessionLossLiveTests, pass-in-isolation) · Core 4004/4009
  — (5 live-wire flakes incl. CargoBayReload; rotating set, pass-in-isolation).
- **Run 2:** Lobby 12/12 · MonoGame 1863/1863 (3 skipped) · ClientCore 656/657 —
  (1 live-wire flake: ThreePeerSeededSessionTests, pass-in-isolation) · Core
  4006/4009 — (3 live-wire flakes: LiveCountdownStartPing, GameOverPlayersWire,
  AddPartTests — each pass-in-isolation). The failing tests **rotate** between
  the two runs (SessionLoss → ThreePeer; CargoBay → GameOverPlayersWire …), the
  signature of load-flakiness, not a regression.

## Proof (real-window, 2560×1440 backbuffer)

Driven by `ALLEGIANCE_KEYMAP_PROOF=1` via `scripts/live-proof.sh` against a
LOCAL server on this workstation (never 221), a real windowed client, real
backbuffer PNGs. The F6 flow is driven through the **same controller the real
popup uses** (navigate → select → capture), rebinding order-accept and
mouse-flight; the client log records the resolution:

```
[keymap-proof] shot A: Controls open
[keymap] saved bindings -> …/keymap.json
[keymap-proof] AcceptCommand -> NumPad1, ToggleMouse -> NumPad2
[keymap-proof] shot B: AcceptCommand+Numpad1, ToggleMouse+Numpad2 (rebound)
[keymap] Controls closed
[keymap-proof] shot C: live help reflects Numpad1/Numpad2
[keymap-proof] done (three real frames requested)
```

- `docs/reports/P625b_assets/p625b_controls_open.png` — the F6 Controls popup.
- `docs/reports/P625b_assets/p625b_rebound_accept_mouse.png` — after rebinding
  AcceptCommand → NumPad1 and ToggleMouse → NumPad2.
- `docs/reports/P625b_assets/p625b_help_after_rebind.png` — the LIVE help
  overlay; vision-verified to render the full 14-category table (PANES / MENUS,
  FREE-CAM / SPECTATOR visible) including the rebound **NumPad1 = Accept the
  pending order** row.

The "use them" semantics — the new key triggers the command, the old key does
not, no repeat-fire while held — are pinned by the headless behaviour tests
above (they exercise the exact predicate + edge-latch contract every handler
uses), while the real-window proof shows the F6 rebind UI and the live help
actually reflecting the new keys in the presented frame. Runtime artefacts
(`keymap.json`, `screenshots/`) were removed after capture; the client and server
were shut down by exact PID.

## Native fidelity notes

The P625 defaultinputmap.mdl parser and TK-name table are unchanged. The new
port-only actions (pane verbs, free-cam, mouse settings) have no native TK and
correctly carry a null native-default (the F6 popup shows no "restore native"
suggestion for them), matching the port's established convention.

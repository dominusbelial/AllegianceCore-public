# HELP_OVERLAY_P257 — F1 keybinding-reference overlay

Pure client-side UX feature in the MonoGame client (`Allegiance.ClientCore.MonoGame`).
Pressing **F1** toggles a large, readable overlay panel listing every current
client keybinding, organised into logical groups. Zero server / wire / sim
change; display-only.

## What landed

| Piece | Location | Kind |
|---|---|---|
| `KeyBindingTable` | `src/Allegiance.ClientCore.MonoGame/Rendering/KeyBindingTable.cs` | Pure data (no MonoGame types) |
| `HelpOverlayLayout` | `.../Rendering/HelpOverlayLayout.cs` | Pure balanced-column layout |
| `HelpOverlayRenderer` | `.../Rendering/HelpOverlayRenderer.cs` | Draws the panel |
| `Game1` wiring | `src/Allegiance.ClientCore.MonoGame/Game1.cs` | F1 edge-toggle + draw last |
| Tests | `tests/Allegiance.ClientCore.MonoGame.Tests/HelpOverlayTests.cs` | 24 pure tests |

## The full keybinding table (as built, read from the code)

Built from `Game1.UpdateFlying` / `UpdateLobby` and `Flight.FlightInput.Translate`
— NOT from the stale `docs/KEYBINDING_AUDIT_P220.md`. All 29 `XnaKeys.*`
referenced in `Game1.cs` today are covered.

MOVEMENT (always, while flying)
- W / S — throttle forward / backward
- Q / E — strafe left / right
- Up / Down — pitch up / down
- Left / Right — yaw left / right
- A / D — roll left / right
- Shift (either) — afterburner boost (TK_FireBooster), hold to sustain

WEAPONS (always, while flying; keys disabled while composing chat)
- Space — fire weapons (TK_FireWeapon), hold; all in group-fire mode, the
  selected mount in single-weapon mode
- Left Ctrl — fire missile (TK_FireMissile), hold; server acquires & fires
- ` (backtick) — toggle group-fire / single-weapon (TK_ToggleGroupFire)
- [ / ] — previous / next weapon mount (TK_PrevWeapon / TK_NextWeapon)
- C — drop chaff (TK_DropChaff)
- N — drop mine / probe (TK_DropMine)

TARGETING / AUTOPILOT
- T — cycle target, nearest-to-crosshair first (ships then stations)
- G — toggle autopilot toward the selected target (nearest if none selected)

CAMERA / VIEW
- V — toggle cockpit / chase camera
- M — toggle mouse flight (move the mouse to steer while on)
- Tab — toggle the whole-mission starmap overlay (P249)
- F1 — toggle this help overlay (P257)

CHAT / COMMS
- Enter — open chat line / send while composing
- Backspace — delete previous character while composing
- R — open the quick-chat voice-macro menu (P217)
- Esc — cancel chat line / close quick-chat menu

LOBBY (server browser)
- Up / Down — select server
- Enter — join highlighted server
- R — refresh server list
- Esc — quit

Notes: Esc quits (or cancels chat / closes menus); the gamepad Back button does
the same. While typing chat or in the quick-chat menu, most flight/combat keys
are deliberately neutralised. The mouse is used only when mouse-flight (M) is
enabled.

## Layout / grouping choice

A large (≈98% × 90%) translucent panel drawn TOP-MOST over every other overlay,
organised into the six logical groups above, distributed into **balanced
columns** (`HelpOverlayLayout` — each group is placed in the currently shortest
column, so columns stay near-equal). Keys are gold, descriptions light, group
headers teal on the established translucent-dark panel with a teal border
(styling mirrors `StarmapRenderer`/`HudRenderer`). A defensive uniform text
scale shrinks the whole panel (min 0.5×) whenever the combined column width
would exceed the viewport, so no binding can ever be clipped.

It is an **independent flag**: toggling F1 on/off never disturbs the starmap,
HUD, or chat-log state, which all resume unchanged when it closes (opening F1
over an open starmap covers it — a reference screen you consult briefly, then
close to reveal the starmap again).

## Verification

- **Live screenshot (required)** — `/tmp/p257verify/help_on.png`: the real
  `HelpOverlayRenderer` rendered offscreen (the P207/P218/P211 technique) at the
  client's real 1280×720 default window; every group labelled, all 26 rows
  legible, no clipping. `help_off.png` = the same frame without the overlay.
  Also composited over the REAL `HudRenderer` flight HUD on/off:
  `/tmp/p257live/help_over_hud_on.png` / `help_over_hud_off.png`.
- **F1 toggles off too** — the same renderer flag drives both states (shown
  on/off above); the F1 edge-toggle in `Game1.Update` (`_helpVisible =
  !_helpVisible`, composing-gated) is verified by code-read. Physical keypress
  injection is unavailable on this Wayland box (no xdotool/ydotool — the
  documented constraint behind every prior P-task's auto-trigger recipes).
- **3 spot-checks, live** — `/tmp/p257live` boots a REAL in-process server +
  REAL `ConnectionSession` and drives the exact methods the keys trigger:
  1. **Space = fire weapons** — held `CommandControls(stateM=AllWeapons 0x8000)`;
     server logged `[ctrl] ... state=0x8000` and real `[projectile] PROJ_INST(12)`;
     59 projectiles appeared in the world (0 → 59). Confirmed live.
  2. **C = drop chaff** — `SendFireExpendable(ChaffLauncher)`; server logged
     `[fireexpendable] fired chaff 21 ... FE(87)+CREATE_CHAFF(109) ... dispenser
     3 -> 2`; a real `Chaff` appeared in the sector. Confirmed live.
  3. **K = suicide** — `SendSuicide()`; server logged `[suicide] ... re-triggering
     the combat kill path (KillShipEvent, launcher NULL)` then `[eject] player
     ship 8096 ejected`. The suicide command genuinely fires; on this non-lifepod
     fighter it resolved to an in-space eject (native's `bKilled=false` for
     non-lifepod — the P222-documented rule), so no KILL_SHIP. Reported honestly.
- **Build** — `dotnet build src/Allegiance.ClientCore.MonoGame` = 0 warnings, 0 errors.
- **Tests (2 full runs each)** — Core.Tests 3073 passed / 3074 total (the only
  failure both runs is the pre-existing documented `LiveTurretGunnerBindingTests`
  timing flake); MonoGame.Tests 311 passed; ClientCore.Tests 85 passed. My
  change is purely in the MonoGame client project (+24 pure `HelpOverlayTests`);
  Core/ClientCore baselines are untouched.

## Named limitation — maintenance drift (do not silently refactor)

This table is a **hardcoded snapshot** of the current bindings, maintained by
hand — it is not a declarative input registry that drives both the input
dispatch and this display. The very next keybinding added elsewhere in the
codebase will make this list stale again, exactly as `docs/KEYBINDING_AUDIT_P220.md`
went stale (it predates Tab / Left-Ctrl / C / N / ` / [ / ] / K etc.). That is an
accepted, explicitly-named cost of this feature; a single-registry refactor of
how input is dispatched is deliberately out of scope for this pass. When a new
binding lands, `KeyBindingTable.All` must be updated to match. The pure tests
(`Table_ContainsCoreBindings`, `EveryCell_IsPureAscii`, `Table_HasAllSixLogicalGroups`)
pin the table's existence/completeness/shape but cannot detect semantic drift —
updating it is a human duty.

## Out of scope (deliberately not done)

- Any input-dispatch refactor / single declarative registry.
- Rebinding / customisation — read-only reference display.
- Changing what any key does — display only.
- P254 (ship-asteroid collision), P255 (mining visual), P256 (warp autopilot
  priority) — all three have LANDED on master before this stage (git log:
  47e6e78/b8c1f7e/43f85e0/cdebfb9) but none are depended upon here.
- Native reference source (`~/Development/Allegiance`) — untouched.

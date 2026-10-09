# P587 — Native mouse in flight + friendly-base targeting (any sector)

Status: LANDED on `master` (36b69832, amended). Build 0 warnings / 0 errors.
Scope: the client (`Allegiance.ClientCore.Game`) + help table + tests. No server / Core changes.

## What was ported

### 1. Base targeting is now ANY-SECTOR with the local hull's dockability filter

Previously `Shift+Z` (friendly base) and `Z` (enemy base) ran `SelectFilteredTargetForKey`
through `GatherSectorCandidates`, which is **sector-only** (P371) and applied **no ability
filter**. That meant Domingo could never target his home station from another sector, and the
result was "no friendly base in sector" even when one existed elsewhere.

The port now mirrors native `TK_TargetFriendlyBase*` / `TK_TargetEnemyBase*`:

- Native resolves the search ability mask from the LOCAL ship's own base-hull capabilities
  (`WinTrek.cpp:10211-10224`):
  - **lifepod** → `c_sabmRescue | c_sabmRescueAny | c_sabmLand` AND `ttMask |= c_ttProbe`
    (rescue probes join the search);
  - **fighter** → `c_sabmLand`;
  - **anything else (capital)** → `c_sabmCapLand`.
- The search mask is `c_ttStation | c_ttFriendly | c_ttAnyCluster` (friendly) or
  `c_ttStation | c_ttEnemy | c_ttAnyCluster` (enemy, role `~c_sabmPedestal`)
  (`WinTrek.cpp:10210-10223`, `:10199`).
- The probe special case matches a rescue-capable probe **regardless of its side**
  (`FindableModel`, `src/Igc/common.cpp:279-285`).

Implemented as pure, unit-tested modules in `Game1.Targeting.cs`:
`FriendlyBaseSpec(hullHabm)`, `EnemyBaseSpec()`, `GatherBaseCandidates(stations, probes, …)`
(all-stations-any-cluster + optional probes, each classified with `SideId` + ability `RoleBits`
from the existing catalogue data). `TargetFilterSpec` gained `IncludeProbes`, and
`MatchesFilter` now accepts a `Probe` kind and applies the rescue-probe side-exception.
`SelectFriendlyBaseForKey` / `SelectEnemyBaseForKey` drive them; the `Z` / `Shift+Z` keys in
`Game1.FlightInput.cs` now call these. The "no friendly base" on-screen line appears only when
no matching base exists in ANY sector.

Ability semantics = native `HasCapability` = "shares at least one bit"
(`hullTypeIGC.h:243-245`), which the existing role filter already implements.

### 2. Mouse buttons in flight (virtual-joystick mode)

Native `TK_ToggleMouse` (`WinTrek.cpp:9289-9298`, `m_bEnableVirtualJoystick`) — the port's
**M** key already toggles `_mouseFlightEnabled`. The decoded
`Artwork/defaultinputmap.mdl` `mouseButtonCommandMap` is `(0,55) (2,56) (9,58) (8,59) (1,90)`,
and the joystick `buttonCommandMap` `(0,0,55) (0,1,56)` confirms the TK ids
(`src/Igc/igc.h`: `TK_FireWeapon=55`, `TK_FireMissile=56`, `TK_TargetCenter=90`). Verified the
decode directly against `igc.h:278,313`.

New `HandleFlightMouseButtons` (in the new partial `Game1.MousePick.cs`), called each flying
frame from `UpdateFlying`, when `_mouseFlightEnabled` is on:
- **Left button → FireWeapon (55):** held/continuous, ORs `FlightInput.FireContribution` into
  the command stateM exactly like the P380 joystick fire button (and Space).
- **Middle button → FireMissile (56):** held, shared 250 ms throttle with the `Left Ctrl` key.
- **Right button → TargetCenter (90):** edge-triggered → `SelectCentermostTarget()` (the same
  ' key action). Verified: the decoded ids match the joystick-map triple (trigger/button 2 →
  55/56), so 55/56 are FireWeapon/FireMissile as the brief predicted.

### 3. Pointer picking (virtual-joystick OFF)

When `_mouseFlightEnabled` is off the cursor is already visible (`IsMouseVisible =
!_mouseFlightEnabled` on the M toggle). New pointer actions (port of `console.cpp`
`ResolvePick` `:1857-1900` and `PickObject` `:1912-1990`, and `Button`):
- **Left-click** on an **own-side ship** → makes it the command recipient
  (`_commandee`), matching native's `button != 1 → own-side ships`
  (console.cpp:1874-1877).
- **Right-click** on a **legal command target** (any non-self ship, station, asteroid or warp —
  stations included) → sets the autopilot target and ENGAGES the autopilot, matching native's
  default-command → `SetTarget`/`SetAccepted` (`WinTrek.cpp:8823-8890`); a friendly dockable
  station then docks through P575's `DockApproach` (same sector or, via P540b, another sector).
- **Drag > threshold cancels** the pick (native `>100 px²`, console.cpp:1931).

Hit-testing is by **screen position ± radius, nearest to the cursor** (native
console.cpp:1893-1899), **not** the crosshair cone. Pure resolver
`ResolvePointerPick` is unit-tested.

#### Documented deviations
- Native hit-tests each model by its **own** `ThingSite::GetScreenRadius`. This port's
  candidates carry no model radius, so it approximates with a fixed normalized radius
  (`PointerPickRadius = 0.085`, ~8.5% of half-screen). Faithful substitute, documented.
- Native's pointer left-click also composes a chat command / waypoint-buoy drop for empty
  space (PickObject else-branch). The port restricts left-click to the own-side-ship pick and
  right-click to the command-target pick; the empty-space waypoint-buoy drop is out of scope
  (no command-console/buoy concept in this client) and noted in the writeup rather than built.
- `TK_TargetAlliedBase` degrades to the same-side friendly match (this client has no alliance
  side map; same convention as P415's `TargetRelation.AlliedBase`).

### 4. Help / keybinding table

`KeyBindingTable.cs` (the H help overlay) updated: WEAPONS group gains the mouse-flight fire /
missile rows; TARGETING gains the pointer right-click (target + autopilot, stations dock),
pointer left-click (commandee), and mouse-flight right (target-center); the Z / Shift+Z rows
now say "ANY sector" / "docks by hull". The `HelpOverlayTests` layout/ASCII/duplicate-key
constraints all still pass.

## Failing-first

The new pure functions (`FriendlyBaseSpec`, `GatherBaseCandidates`, `ResolvePointerPick`) and
the file `Game1.MousePick.cs` do not exist on the pre-P587 HEAD (`git show HEAD:` returns 0
hits; `git cat-file -e HEAD:…Game1.MousePick.cs` is fatal). The 10 new tests therefore
**fail (do not compile) on today's code** and pass after — the standard failing-first posture
for an additive feature in this codebase. They pin real behavior (e.g. friendly-base selection
finds a Land-capable base in a *different* sector — impossible under the old sector-only code —
and a lifepod picks a rescue probe of any side).

Tests: `tests/…/P587MousePickAndBaseTargetingTests.cs` — 10 facts, all passing.

## Full suite (final code) — 3 runs, as measured

Each run = Lobby + MonoGame + ClientCore + Core (`scripts/run-tests.sh` projects, executed
sequentially per the session rule; numbers are what was actually measured).

| Run | Lobby | MonoGame | ClientCore | Core |
|-----|-------|----------|------------|------|
| 1   | 12/12 | 1664/1664 | 568/568 | 3836/3840 (4 flakes) |
| 2   | 12/12 | 1664/1664 | 564/568 (4 transient; immediate re-run clean 568/568) | 3838/3840 (2 flakes) |
| 3   | 12/12 | 1664/1664 | 566/568 (2 transient; immediate re-run clean 568/568) | 3835/3840 (5 flakes) |

Every failure is a **documented rotating live-wire load-flake** in `docs/TEST_FLAKES.md`
(Board_refused_once_mission_over, Rescue_ship_picking_cash_treasure, GameOverPlayers
Win_by_elimination / Conquest_win leak, ConquestTerritory conquest_leak, Live_whole_mission_
warp_bomb, Auto_reload_relays_RELOAD129, Eliminating_side2, Lifepod_reaching_rescue_station,
Multi_mission_countdown, CargoBayReload SwapPart, ClientCore FullSessionKill/CrossSideKill
family). Each was confirmed **green in isolation** (re-run 2-3× per failed test). My changes
are client-only (`git diff --name-only` shows only `Allegiance.ClientCore.Game` +
`KeyBindingTable` + tests), so these server-side Core/ClientCore flakes are provably
pre-existing and unrelated. **The MonoGame project — which contains all 10 new P587 tests —
passed 1664/1664 cleanly on all 3 runs.**

## Real-window proof (P572 tooling)

Ran `scripts/live-proof.sh` with `--auto-dock-after 9 --dock-sweep 4` on the 221 map
`m2xspiderw.igc` at window 2560×1440. Produced real backbuffer PNGs (not offscreen renders):
`docs/reports/P587_assets/p587_dock_approach.png` (+ hangar sweep + early flight) and the full
server + client logs.

Verified end-to-end:
- Client: `[monogame] autoproof: head to dock autopilot ON → Omega Debug Garrison (Station#24465)`,
  `[monogame] autopilot OFF (docked)`, `docked: hangar/loadout screen`.
- Server: `[dock] ship 8098 docked at station 24465 'Omega Debug Garrison' (side 0) in sector 'Omega'`,
  `[docked] peer 1 ship 8098 <- FM_S_DOCKED(61) station 24465`.

`p587_dock_approach.png` (visually inspected) shows the ship in cockpit view flying straight at a
fully rendered 3D station with a targeting reticle locked on it, full HUD and the minimap — the
autopilot→P575 `DockApproach`→docked chain that both **Shift+Z → G** and **right-click on the
home station** drive, rendered into the real presented window. This covers the docking endpoint
(Domingo's original ask). The pointer→target→autopilot resolution half is pinned by the
`ResolvePointerPick` unit tests; a fully automated synthetic mouse-click at the station's pixel
coordinates was not scripted (would require adding new injection tooling) and is the residual
gap, documented rather than claimed.

## Files changed
- `src/Allegiance.ClientCore.Game/Game1.Targeting.cs` (FriendlyBaseSpec/EnemyBaseSpec/GatherBaseCandidates/IncludeProbes; live Z/Shift+Z wrappers; ProbeRoleBits)
- `src/Allegiance.ClientCore.Game/Game1.MousePick.cs` (NEW — mouse buttons + pointer pick)
- `src/Allegiance.ClientCore.Game/Game1.FlightInput.cs` (wire mouse handler into UpdateFlying; Z/Shift+Z to any-sector)
- `src/Allegiance.ClientCore.Game/Rendering/KeyBindingTable.cs` (help rows)
- `tests/Allegiance.ClientCore.MonoGame.Tests/P587MousePickAndBaseTargetingTests.cs` (NEW — 10 tests)
- `docs/reports/P587_MOUSE_PICK_AND_BASE_TARGETING.md` + `docs/reports/P587_assets/` (proof)

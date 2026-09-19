# P273 — Teleport pane: board a friendly ship's turret / switch stations while docked

Native reference: `WinTrek/teleportpane.cpp` (class `TeleportPane`, the `ofTeleportPane`
docked overlay). Landing: this repo `master` (Domingo pushes).

## Native citations (read-only source, never modified)
- Destination list construction = `RebuildList` (teleportpane.cpp:399-435): same-side
  ships that pass `CanBoard` (teleportpane.cpp:375-397: not self, same side,
  `LastSeenState()==c_ssDocked`, hull `GetMaxFixedWeapons() != GetMaxWeapons()` — the
  hull actually has turret hardpoints) plus the side's stations with
  `c_sabmRestart` (teleportpane.cpp:419-430).
- Selection = `OnButtonTeleport` (teleportpane.cpp:296-361): board a ship
  (`BoardShip` when not on one, `DisembarkAndBoard` when already on another);
  teleport to a station (`DisembarkAndTeleport` when on a ship, else a direct
  `DOCKED(stationID)`).
- Sort default `sortName` (teleportpane.cpp:27-30); type/sector sorts skipped as a
  named follow-up.

## Real message path used (step 1 of the brief — confirmed, no new server logic)
The port already had multi-crew turret-gunner support server-side
(`LiveTurretGunnerBindingTests`) and docking/repair (P261). Research confirmed the
client entry point was genuinely missing, so the pane wires selection to the two
real messages the server already handles:
- **Board a ship as gunner** — `FM_C_BOARD_SHIP(103)` with `SidParent = ship id`
  (new `ConnectionSession.SendBoardShip`). Server `HandleBoardShip`/`PerformBoard`
  (Server.cs:6296, 7636) resolves on the sender's side and binds the gunner to the
  carrier's turret weapon. When already on a different ship the pane leaves first
  (native's `DisembarkAndBoard`), sending `BOARD_SHIP(-1)` then `BOARD_SHIP(target)`.
- **Switch friendly stations** — `FM_C_DOCKED(48)` with `stationID != NA` (existing
  `ConnectionSession.SendDocked`). Server `PerformTeleport` (Server.cs:9326, P110)
  relocates a docked ship to the named same-side restart-capable station. When on a
  ship it leaves first (native's `DisembarkAndTeleport`).
- No new server-side logic was added; this is purely the missing player-facing UI.

## What was built (client-only)
- `Rendering/TeleportPaneModel.cs` — pure, headless-testable destination model
  (the `ScoreboardLayout` discipline): filters ships (side/docked/turret-slots) and
  stations (side/restart/not-current) with the real `PCore001.igc` catalogue, owns the
  cursor, and `Activate()` returns a BoardShip/TeleportToStation verdict.
- `Rendering/TeleportLayout.cs` — pure ASCII rows reusing the P268 `DockedScreenRenderer`
  translucent-panel conventions (own SpriteBatch session, gold border, pure-ASCII).
- `Rendering/ShopCatalog.cs` — now also carries `StationTypes` for the restart gate.
- `Game1.cs` — while docked, `T` toggles the hangar↔teleport sub-view; UP/DOWN cycle the
  list, SPACE acts, `B`/`T` return to the hangar. `BuildTeleportModel` rebuilds the list
  from the live `ClientWorld`; `ActivateTeleport` sends the real messages.
- `ConnectionSession.cs` — added `SendBoardShip` (+ `SendActiveTurret`/`SendInactiveTurret`
  for the live control-transfer proof), mirrors of `Bot.SendBoard` / the turret sends.

## Live proof (real in-process server + real wire, /tmp/p273verify)
Two real `ConnectionSession`s (CARRIER 792 "Adv Devastator" with 4 turret slots, LOCAL
504 boarder), both docked at station 1051. LOCAL's real `ClientWorld` (genuine wire data)
drives the real `TeleportPaneModel`/`TeleportLayout`/`DockedScreenRenderer`, rendered
offscreen:
- `P273_teleport_pane.png` — translucent panel listing `> Adv Devastator turrets:4 [SHIP]`
  as boardable (the carrier), plus "TELEPORT TO FRIENDLY STATION" (no others on the map).
- LOCAL sent `SendBoardShip(carrierId)` → server log `boarded 8097 as turret gunner slot 2`;
  `AFTER BOARD: ParentShip==carrier=True TurretId=2 gunnerBound=True`.
- LOCAL sent a real `FM_C_ACTIVE_TURRET_UPDATE(99)` → server `StateM=weaponsMaskIGC`, gunner
  orientation re-aimed from (0,0,-1) to (-0.470,-0.199,-0.860); `CONTROL TRANSFER:
  aim-from-gunner=True` — the turret aims from the gunner's input, matching
  `LiveTurretGunnerBindingTests` server-side proof driven through the real client sender.
- Leave cycle clean (`ParentShip==null`, gunner cleared).
- Screenshots live at /tmp/p273verify/P273_teleport_pane.png and P273_after_board.png.

## Build / test
- Whole-solution build: 0 warnings, 0 errors (3x).
- Core suite (`Allegiance.Core.Tests`, 3126 tests) run 3x: 3125/3126 each time, the sole
  failure being the documented pre-existing `LiveTurretGunnerBindingTests` timing flake
  (the only expected one; one run also hit another documented machine-state flake). No
  server/Core changes in this task, so these are not regressions.
- MonoGame suite: 407/407 pass (incl. 15 new `TeleportPaneModelTests`/`TeleportLayoutTests`).

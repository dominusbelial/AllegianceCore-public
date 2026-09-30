# P455 — a side that loses its last restart station is eliminated, and the game ends

## Summary

Ported native's station-elimination win into the C# port. When a side loses its
**last restart-capable station** — to a **kill** or a **capture** — it is now
eliminated (deactivated), the whole mission is told via `SIDE_INACTIVE(68)` and
the `"<Side name> is no more."` chat, and the win-by-elimination check ends the
game, crowning the survivor. This closes the parity gap found in the
2026-09-29 gameplay-parity pass where "destroying or capturing the enemy's home
base does not end the game."

The ported logic lives in the **server** (`Server.DeactivateSide`), routed
through a new host seam, exactly as the kill/capture broadcasts are — the side
logic is *not* in `Model.Sector`.

## What was wrong

- The station-kill path (`Model/Sector.cs` combat pass) removed the station,
  raised `KillStationEvent`, and only ran `RecomputeConquestAndTerritory(null)`,
  which is gated on `ConquestEnabled`/`TerritoryEnabled` (never set outside
  tests). Destroying a garrison had no match consequence.
- The capture path was identical.
- The port's `CheckForVictoryByInactiveSides` counted *connected players* only
  and ignored `Side.Active`.

Native's authoritative behavior:
- `CFSMission::CheckForVictoryByStationKill` (`FedSrv/fsmission.cpp:3568`,
  called on kill `FedSrv.CPP:12343-12344` and on capture via
  `CheckForVictoryByStationCapture` → `CheckForVictoryByStationKill(NULL,
  psideOld)`, `fsmission.cpp:3563-3566`) ends with: walk `psideOld->GetStations()`
  skipping `pstationKilled`; if none has `c_sabmRestart`, call `DeactivateSide`
  (`fsmission.cpp:3698-3714`, comment "Elimiation of all restart stations of all
  other sides is always a win").
- `CFSMission::DeactivateSide` (`fsmission.cpp:5318-5427`) deactivates the side
  and runs `CheckForVictoryByInactiveSides` (`:5378-5419`), which counts a side
  as alive only if `HasPlayers && GetActiveF()` (`:3767-3790`).

## Changes

### New host seam (server-side elimination decision)
- `IIgcSite.SideLostRestartStation(Mission, short sideId)` — a C#-only seam
  (no igc.h member), no-op default, matching the `VictoryByStationsComputed`
  seam style. Raised from `Model/Sector.cs` on both station-loss paths, carrying
  the **losing side** (the station's owner *before* the kill/re-side).
  - Kill path (`Sector.cs` combat pass, after the P191 recompute):
    `m.Site.SideLostRestartStation(m, station.SideId)`. A kill never re-sides,
    so the static record's `SideId` is the loser; the dead station is already
    removed from `StationRuntimes`, so the "exclude the killed station" premise
    is **confirmed true by construction** (no extra exclusion needed).
  - Capture path (`Sector.cs` dock pass): captures `oldSideId = rt.SideId`
    *before* the re-side (the station's pre-capture owner), then raises
    `SideLostRestartStation(mission, oldSideId)` after the recompute. The
    freshly captured station now belongs to the captor, so it no longer counts
    for the loser (matching native's `CheckForVictoryByStationKill(NULL,…)`
    where the captured station counts for its new owner).
- `ServerHost.SideLostRestartStationHook` + site implementation, wired in
  `Server.Lifecycle.cs` to `Server.OnSideLostRestartStation` (alongside the
  `StationKilled`/`StationCaptured`/`VictoryByStationsComputed` wiring at
  `Server.Lifecycle.cs:679-681`).

### `Server.OnSideLostRestartStation(Mission, short)` — the elimination check
The port of the `fsmission.cpp:3698-3714` block:
1. Resolves the losing side; returns if unknown or already `!Active`.
2. Walks the mission's station universe (`Sector.StationRuntimes`) and returns
   (not eliminated) if the side still owns a station whose type
   `HasCapability(DataStationTypeIGC.SabmRestart)`.
3. Otherwise runs `DeactivateSide`.

The existing conquest/territory recompute is kept as-is — elimination is its
own unconditional check precisely because the recompute is gated on
`ConquestEnabled`/`TerritoryEnabled`.

### `Server.DeactivateSide(Mission, Side)` — the ported elimination
Kept in native order (`fsmission.cpp:5318-5427`):
1. `side.Active = false` (`:5325`).
2. Every ship of the side, **before any kill**: terminate all parts + ammo=0 +
   fuel=0 (`:5344-5354`). The port's part-termination is `Ship.ClearLoadout()`
   (its `LOADOUT_CHANGE` side broadcast is harmless and accurate; terminating
   first means the killed ships drop no loot, the same reason native does).
3. Kill every NPC ship (`PilotType < PilotTypePlayer`) through the **real kill
   path** (`mission.Site.KillShipEvent` → `BroadcastKillShip`), so a real
   `KILL_SHIP(105)` goes out as for any death (`:5356-5359`).
4. Players: force-kill any in a cluster with no parent ship through the same
   kill path with native's negative-damage "forced" intent (`amount = -1.0f`,
   `:5362-5364`); `Ship.ClearLoadout()`/ammo/fuel + `FinalizeDeadShip` removal
   stand in for native's `Reset(false)` + `ShipStatusExit()` (`:5365-5366`),
   leaving eliminated players unable to respawn (see the respawn gate below).
5. Broadcast `SIDE_INACTIVE(68)` with `bActive=false`, `bChangeAET=false`,
   `bAET=false` to the whole mission (`:5371-5376`).
6. Send `"<Side name> is no more."` to everyone (`CHAT_EVERYONE`, `:5377`).
7. Run `CheckForVictoryByInactiveSidesAfterDeactivation` (`:5378-5419`).

### `Server.CheckForVictoryByInactiveSidesAfterDeactivation` — the winner test
No P388 debug-mode skip (see Debug mode below). Alive = `Side.Active` alone in
debug mode (the 0-player arena), or `HasPlayers && Active` in production.
Zero alive → draw `"All sides were destroyed"`; exactly one → that side wins
`"<name> won by outlasting all other sides."`; more → game continues. Declares
via the single shared `DeclareGameOver` path (which also runs P439's mission
rotation).

### Shared `CheckForVictoryByInactiveSides` now consumes `Side.Active`
The peer-departure victory check (called from `ReleasePeer`) now filters its
player-side tally to active sides, matching native's
`HasPlayers && GetActiveF()` (`fsmission.cpp:3776`). Behavior-preserving for
the peer-departure path (sides only become inactive via `DeactivateSide`, which
removes their players first); keeps this check and the elimination one agreeing.
The P388 debug-mode skip stays exactly as it is.

### Respawn gate — eliminated players stay dead
Native's lifepod respawn requires a restart-capable friendly station (the
`FindTarget` at `FedSrv.CPP:12034-12038` carries `c_sabmRestart`); a deactivated
side has none. The port's `BroadcastKillShip` respawn now honors
`Side.Active`: a victim on an inactive side is finalized dead instead of
respawning as a lifepod at some remaining (non-restart) station.

## What an eliminated player experiences

Faithful to native (Domingo's decision: "eliminated players see exactly what
native shows"):
- Their in-flight ship is force-killed through the real death path: a real
  `KILL_SHIP(105)` (and `SHIP_DELETE(20)`) goes out to same-sector observers,
  with the death blast — exactly as for any death.
- They do **not** respawn as a lifepod (no restart station; the `Side.Active`
  respawn gate).
- Their loadout is cleared and ammo/fuel zeroed.
- They see/hear `SIDE_INACTIVE(68)` for their side and the
  `"<Side name> is no more."` chat (whole-mission).
- The match ends: `GAME_OVER(62)` names the surviving side with the native
  reason. (The side-only game-over screen and client `SIDE_INACTIVE` handling
  are **P455b**, next in the chain.)

## Debug mode (P388) decision

P388 skips the *player-presence* victory check in debug mode because 0 players
is the normal unattended-arena state, on the rule that "only a genuine
combat/conquest outcome may end a debug-mode mission." Station elimination **is**
such an outcome, so:
- In debug mode elimination still deactivates the side and ends the game.
- The elimination winner test drops the `HasPlayers` half and uses `Side.Active`
  alone, so a 0-player arena can still produce a winner.
- The existing player-presence-only skip (the `ReleasePeer` call) stays exactly
  as it is.
- The P379 debug topology's synthetic garrisons are `StationTypeId 15`, which
  inherits `SabmRestart` (`Server.Lifecycle.cs:~329`) — confirmed, so
  elimination works there.
- P439 (already landed) rotates the finished mission to a fresh NotStarted one;
  this task does not duplicate rotation — it just calls `DeclareGameOver`, whose
  shared path runs `RotateMissionAfterGameOver`.

## Out of scope (unchanged)

- Goal-based win modes (conquest %, territory, prosperity, artifacts, flags,
  kills) through config — separate follow-up (fits P439's helper).
- Mission rotation after game-over (P439).
- Native alliance handling (`bAllies`, `fsmission.cpp:3718-3745`) — no alliances
  in the port.
- `bAllowEmptyTeams`/KGJV #62 and pre-game `SIDE_INACTIVE` (P202) — untouched.
- The side-only game-over (3+ sides, `fsmission.cpp:5420-5426`) and client
  `SIDE_INACTIVE` handling — **P455b**.
- Station kill/capture mechanics themselves.

## Tests

`tests/Allegiance.Core.Tests/SideEliminationLiveTests.cs` (4 live loopback
tests, 2 sides on XXV_simple, synthetic restart-capable stations owned by side B
via `Mission.PlaceStation`, real missile / real dock-capture drivers):

1. **`Destroying_last_restart_station_eliminates_side_and_declares_winner`** —
   a real cross-side missile destroys side B's only restart station (real damage
   path → seam → `DeactivateSide`); the side-A Bot receives `SIDE_INACTIVE(68)`
   for side B (`bActive=false`), the `"Bravo is no more."` chat, and
   `GAME_OVER(62)` naming side 0 with the native reason; the mission flips Over.
2. **`Side_with_two_restart_stations_survives_losing_one`** — negative: a side
   with two restart stations loses one → no `SIDE_INACTIVE`, no game-over,
   side stays active, mission stays Started.
3. **`Capturing_last_restart_station_eliminates_old_side`** — the last restart
   station is captured (real hostile boardable-hull dock with shields down) →
   the old side is eliminated.
4. **`Debug_mode_elimination_still_declares_winner_with_zero_players`** —
   `DebugMode=true`, 0 connected players: elimination still deactivates the side
   and ends the game, crowning the survivor (Active-only winner, not a draw).

### Pre-fix failing evidence (discriminator)

The kill test was run with the P455 hook wiring temporarily disabled
(equivalent to the unfixed behavior where station elimination is a no-op) and it
**FAILED** — no `GAME_OVER`, no `SIDE_INACTIVE`, no elimination — then passed
again with the wiring restored. This proves the test genuinely discriminates the
fix. (Reported run:
`Failed SideEliminationLiveTests.Destroying_last_restart_station_eliminates_side_and_declares_winner`.)

## Verification

- Build (`/home/domingo/.dotnet/dotnet build AllegianceCore.slnx -c Debug`):
  **0 warnings, 0 errors**.
- New tests: 4/4 pass in isolation.
- Affected regression classes (`SideEliminationLiveTests`, `LiveWinConditionTests`,
  `StationCombatLiveTests`, `StationCaptureLiveTests`, `LiveSideInactiveTests`):
  30/30 pass.
- Full suite: **3559 passed / 8 failed** on this tree. Every one of the 8 is a
  pre-existing / environmental live-wire flake, proven by pristine A/B: the
  identical conquest cluster fails on the pristine tree, and a full pristine run
  actually failed **more** (3552 / 11) with a different rotating subset
  (`ChatSectorWireTests`, `AsteroidsUpdateLiveTests`, …) — the environmental
  variance dwarfs any P455 effect. P455 adds **zero** new failures.
- Commit: on `master`, not pushed (Domingo pushes).

## Pre-existing flakes observed (not regressions)

All were verified against the pristine tree (source stashed + test file moved
aside) and are unrelated to P455:

- `ConquestTerritoryLiveTests.Flag_capture_tips_conquest_and_broadcasts_game_state_then_game_over`,
  `ConquestTerritoryLiveTests.Flags_disabled_capture_broadcasts_nothing_and_does_not_end`,
  `ConquestTerritoryLiveTests.Conquest_win_in_one_mission_does_not_leak_to_another` —
  fail on pristine; current-environment pre-existing (10-11 s capture-path timeout).
- `GameOverPlayersWireTests.Conquest_win_in_one_mission_does_not_leak_game_over_players_to_another`,
  `GameOverPlayersWireTests.Conquest_win_lists_every_connected_player_including_losing_side` —
  reuse the same conquest harness; fail on pristine (verified).
- `LiveTurretGunnerBindingTests.Board_binds_gunner_and_active_turret_update_roundtrips_live` —
  documented pre-existing machine-state flake (fails in isolation and on pristine, P215).
- Fast live-wire flakes (each failed ~50 ms in the full run; a full pristine run
  produced a comparable rotating set): `ShipsUpdateLiveTests.Ship_leaving_cluster_disappears_from_next_update`,
  `LiveTreasurePickupTests.Rescue_ship_picking_cash_treasure_credits_side_broadcasts_moneychange_and_destroys_it`,
  plus on pristine `LiveChatSectorWireTests…relay_to_real_peers_over_the_wire`,
  `AsteroidsUpdateLiveTests.Mining_depletion_reflected_in_next_update…`.
- `LiveWinConditionTests.Multi_mission_disconnect_scopes_per_mission` —
  documented flap (green 1/3-2/3, environmental, P200); green on re-run.


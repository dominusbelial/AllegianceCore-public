# P393 — death/lifepod respawn sends debug-mode players to the real home station, not the arena garrison

Status: implemented, whole-solution build clean (0 warn / 0 err), new live tests
pass, live "must-fail-if-removed" proof captured, full-suite runs recorded below.
Landed on `master` (not pushed).

---

## 1. Root cause (confirmed against live code)

`Server.Combat.cs:1434-1443` — `FindFriendlyStation`, the port's documented P105
stand-in for the native nearest-friendly-station search
(`FedSrv.CPP:12034-12038`):

```csharp
private static DataStationIGC? FindFriendlyStation(Ship ship)
{
    if (ship.SideId is not { } sideId || ship.Mission is not { } mission)
        return null;
    foreach (var sector in mission.Sectors)
        foreach (var station in sector.Stations)
            if (station.SideId == sideId)
                return station;
    return null;
}
```

It returns **any** same-side station first-found in the mission's deterministic
sector-then-station order. That is a fine P105 simplification when a side owns
exactly one station (true for every normal production game) — but the P379 debug
topology (`Server.Lifecycle.cs:244-314`) gives side 0/1 **two** same-side
stations: the real Ganymede/Organia (part of the original m2xspiderw map,
enumerated early) and the synthetic Omega/Plato garrison added at boot via
`PlaceStation` (`Server.Lifecycle.cs:273-274`, ids `90001`/`90002`). "First in
mission order" therefore reliably picks the real station for every side-0/side-1
death — a pure enumeration-order artifact, not a spatial judgment.

`FindFriendlyStation` is the sole destination resolver on the death/lifepod
respawn path: `BroadcastKillShip` calls it at `Server.Combat.cs:1346`, then
relocates the dying ship to the station's cluster and docks the lifepod there
(`Server.Combat.cs:1353-1388`). This is exactly the same bug-shape P390 fixed for
the join path (`ResolveSideHomeSector`, `Server.Lobby.cs`), just at a different
call site.

Live confirmation (this change's must-fail proof, below) shows the unfixed
behavior: a side-0 death respawns at station **4328** (Ganymede) and a side-1
death at **4329** (Organia) — the real map — instead of the 9000x debug
garrisons.

## 2. Scope-3 audit: is `FindFriendlyStation` the only respawn destination resolver?

`FindFriendlyStation` has exactly three call sites, all routing through the one
helper:

1. `Server.Combat.cs:1346` — the death/lifepod respawn (this task's target).
2. `Server.Docking.cs:690` — `PerformRescue` (rescue of a drifting lifepod). Not a
   separate destination resolution — it reuses the same helper, so it inherits
   the same fix (a rescued debug player also lands at the debug garrison).
3. `Server.Lobby.cs:2366` — the join `SET_BRIEFING_TEXT` cluster name. Reuses the
   same helper; not a respawn path, but benefits consistently (briefing names the
   arena garrison's cluster in debug mode).

No independent sibling implementation of "resolve a destination station for a
death/respawn" exists: a grep of the server for the native respawn/rescue seams
shows `KillShipEvent` (death) and `PerformRescue` (rescue) both converging on
`FindFriendlyStation`. Fixing the single helper fixes every death/respawn
destination resolution consistently; there is no partial-fix gap. (The separate
`ResolveSideHomeStation`, `Server.Lifecycle.cs:473`, is the **initial-miner
spawn** path — a different concern, deliberately left untouched.)

## 3. The fix: ONE shared debug-garrison matcher

Rather than copy the `OmegaDebugStationId/PlatoDebugStationId` check a third
time, a single shared helper now defines the debug-garrison match once:

`Server.Lifecycle.cs` (next to the garrison constants):

```csharp
private static readonly HashSet<short> _debugGarrisonStationIds =
    [OmegaDebugStationId, PlatoDebugStationId];

private static DataStationIGC? FindDebugGarrisonStation(Sector sector, short sideId)
{
    foreach (var st in sector.Stations)
        if (st.SideId == sideId && _debugGarrisonStationIds.Contains(st.StationId))
            return st;
    return null;
}
```

Matching by the station's **own `SideId`** (not a hardcoded side→id map) holds
for any arena side count — the rule P390's `ResolveSideHomeSector` already
established; it is now expressed once and shared by both consumers:

- `Server.Combat.cs` — `FindFriendlyStation` now tries the side's debug garrison
  first, then falls through to the exact pre-P393 first-in-mission-order scan:
  ```csharp
  foreach (var sector in mission.Sectors)
      if (FindDebugGarrisonStation(sector, sideId) is { } garrison)
          return garrison;
  foreach (var sector in mission.Sectors)
      foreach (var station in sector.Stations)
          if (station.SideId == sideId)
              return station;
  ```
- `Server.Lobby.cs` — `ResolveSideHomeSector` is refactored to locate the
  garrison's sector via the same helper (the P390 inline HashSet + dual
  StationRuntimes/Stations scan collapse to one shared rule; `AttachStation`,
  `Sector.cs:241-245`, populates both lists with the same static
  `DataStationIGC`, so scanning the static list is equivalent).

### Why this preserves production behavior byte-for-byte

`FindDebugGarrisonStation` returns non-null **only** when a synthetic 9000x
garrison is present in the mission. Those ids exist solely because
`ApplyDebugTestTopology` ran (it is debug-gated, `Server.Lifecycle.cs:254`) —
a normal game never attaches one. When the helper returns null, `FindFriendlyStation`
falls through to the identical pre-P393 scan, and `ResolveSideHomeSector` to the
identical `HomeSectorForSide` fallback. The regression test below pins exactly that.

## 4. Verification (real, not just build/test)

### 4a. Live proof — both sides, real debug server + real loopback client

`tests/.../LiveP393DebugRespawnTests.cs` boots a real in-process debug-mode
server (`DebugMode=true`, m2xspiderw), connects a real loopback `FltBot`, seats
it on a side via an explicit POSITIONREQ (P390's proven debug recipe), converts
the ship to the side's Lifepod hull so the death reaches the `bKilled` respawn
branch (a non-lifepod hull would EJECT, P107), then suicides over the wire:

- `Side0_death_respawns_at_omega_debug_garrison_not_ganymede` — respawned
  lifepod lands at station `90001` (Omega Debug Garrison) in sector 1053.
- `Side1_death_respawns_at_plato_debug_garrison_not_organia` — respawned lifepod
  lands at station `90002` (Plato Debug Garrison) in sector 1060.

**Must-fail-if-removed (required):** temporarily removing the debug-garrison loop
from `FindFriendlyStation` makes both tests RED — the side-0 death lands at
station **4328** (Ganymede) and the side-1 death at **4329** (Organia), exactly
the bug. Fix restored → green again.

### 4b. Regression — debugMode OFF, real map

`Normal_game_death_respawn_still_hits_the_pre_p393_real_station` boots a
non-debug m2xspiderw server, confirms no 9000x garrison exists, and asserts a
real death respawns at **exactly** the station the pre-P393
first-in-mission-order scan would return (replicated in the test as
`PreP393FirstFriendlyStation`). Green both with and without the fix — the
fallback is provably byte-for-byte unaffected. The existing P105 test
`Player_death_relocates_to_docked_lifepod_and_only_owner_gets_enter_lifepod`
(a non-debug respawn to the real station) also continues to pass unchanged as a
second regression net.

## 5. Build / test results

- Whole-solution build (`AllegianceCore.slnx`, Debug): **0 warnings, 0 errors**.
- Related live suites green: P393 (3), P390 (3), P379 debug topology, P105
  player-death-respawn, P103 suicide-ship-delete — 22/22.
- Full `Allegiance.Core.Tests` suite run 1: 3380 passed / 5 failed; run 2:
  3382 passed / 3 failed (total 3385). Every failure is a live-network timing /
  machine-state flake that rotates across runs — the failing tests differ
  between runs and none reference this change's symbols:
  - `LiveTurretGunnerBindingTests` — documented pre-existing persistent
    machine-state flake (fails even on the pristine tree; not chased).
  - `LiveWinConditionTests.Board_refused_once_mission_over` — documented
    intermittent live-wire flake; fails intermittently in isolation and passes
    in isolation on the pristine tree (code-independent timing).
  - `GameOverPlayersWireTests` (a different member each run) + `LiveMoneyTests`
    — rotating live-network flakes; both classes pass green in isolation
    (GameOverPlayersWireTests 5/5, LiveMoneyTests 2/2).
  - Each suspect class confirmed green in isolation; none of the failing tests'
    files reference `FindDebugGarrisonStation` / `FindFriendlyStation` / the
    debug garrison ids.

## 6. Deliverable

Fix committed on `master` (not pushed; Domingo pushes). This report.

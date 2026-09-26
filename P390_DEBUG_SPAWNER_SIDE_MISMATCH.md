# P390 — debug-mode ShipSpawner ignores the player's explicit side choice

Status: implemented, whole-solution build clean (0 warn / 0 err), new live tests pass,
full-suite run(s) recorded below. Landed on `master` (not pushed).

---

## 1. Root cause (confirmed against live code)

`Server.Lifecycle.cs:291-311` wires the P379 debug topology's `ShipSpawner`
closure. It has no side parameter at all (`Func<StaticCore, Mission, short, (Ship, Sector)>`,
`Server.cs:326`) and places purely by its own round-robin counter:

```csharp
int n = spawned++;
short sideId = (short)(n % 2);            // side 0 -> Omega, side 1 -> Plato
...
var home = sideId == 0 ? omega : plato;
...
ship.SideId = sideId;
mission_.PlaceShip(ship, home);
```

`Server.Lobby.cs` (`AssignShip`, 1830-1837) calls that spawner with **no side
information**:

```csharp
var (ship, sector) = ShipSpawner is not null
    ? ShipSpawner(mission.Core, mission, shipId)      // no requestedSideId here
    : SpawnDefaultShip(mission, shipId, requestedSideId);
```

The default path (`SpawnDefaultShip`, `Server.cs:558`) already threads
`requestedSideId` in **before** placement (P368), so it never strands a ship.
But the custom-spawner branch simply accepts whatever side/sector the closure
happened to pick. `CompleteSideSelection` (`Server.Lobby.cs:1975-1981`) then
calls `TryJoinSide(live.Ship, requestedSideId)` — which only rewrites
SideId/leadership/roster bookkeeping, **never the ship's position**. Net
effect, reproduced in the live session's own log:

```
[debugtopology] peer 2 spawned: ship 8099, hull Lxy Scout, side 1, sector 1060 'Plato'
[sideselect] 2 'domingo1' chose side 0 (ship 8099)
```

The player is on side 0 but physically sitting at side 1's home (Plato); a
station only accepts same-side ships, so docking is refused — "docking not
working."

Scope discipline: the `ShipSpawner` signature and its ~150 closures are
untouched. Reconciliation happens **only** in the one caller `AssignShip`.

---

## 2. The fix (reconciliation in the one caller)

`AssignShip` (`Server.Lobby.cs:1849-1855`) now detects the mismatch and
relocates:

```csharp
if (requestedSideId is { } requested && ship.SideId != requested)
    sector = RepositionShipToSideHome(ship, mission, requested);
```

`RepositionShipToSideHome` (`Server.Lobby.cs:1866`):

1. Unregisters the ship from the spawner's sector first
   (`ship.Sector?.Remove(ship)`), so the subsequent `mission.PlaceShip` does not
   double-register the ship in two sectors' model lists (the P368 pitfall:
   `Mission.PlaceShip`, `Mission.cs:667`, adds without removing from any old
   sector).
2. Resolves the requested side's home sector via a new
   `ResolveSideHomeSector` helper (below).
3. Parks the ship near the side's home station at the same 420u x-standoff the
   P379 spawner uses — not the station's exact center, still inside its dock
   radius.
4. Sets `ship.SideId = sideId` and `mission.PlaceShip(ship, home)`.
5. Returns `home` as the sector, so `CompleteJoinAndLaunch` sends the correct
   SET_CLUSTER (the live ship's sector and position both reflect the move).

### 2a. Finding: `HomeSectorForSide` alone is the WRONG target for the debug arena

The task's point 2 suggested reusing the existing `HomeSectorForSide`
(`Server.cs:539`). Instrumenting it on the real `m2xspiderw.igc` map proved it
insufficient for the debug arena:

```
HomeSectorForSide(mission, sideId=1) -> sector 1051 'Organia'   (NOT Plato 1060)
```

`HomeSectorForSide` returns the **first side-owned station in mission order**
(P368 semantics — the real map home). For side 1 that is Organia, a real base
lying **outside** the Omega/Plato/Alderan arena the P379 topology isolates.
Reusing it would fix "stranded at an enemy station" (Organia is a real side-1
home where the player could dock) but would fling the player clean out of the
debug arena — defeating the topology's whole purpose, and failing the stated
acceptance test ("lands in Plato and docks there").

So the reconciliation anchors instead on the **debug topology's garrison** —
the sector that holds the side's synthetic garrison (station 90001 for side 0 /
Omega, 90002 for side 1 / Plato, `Server.Lifecycle.cs:197-198,273-274`) — which
is exactly the sector the P379 closure spawns that side into
(`Lifecycle.cs:296`). This is robust: it follows the garrison wherever the
topology placed it (no hardcoded 1053/1060), matches the garrison's own runtime
`SideId` (not a side→id map), and only engages when a debug garrison exists;
any other custom spawner (a plain test closure) finds no garrison and falls
through to `HomeSectorForSide`'s generic real-home answer.

```csharp
private static Sector ResolveSideHomeSector(Mission mission, short sideId)
{
    var garrisons = new HashSet<short> { OmegaDebugStationId, PlatoDebugStationId };
    foreach (var sector in mission.Sectors)
        foreach (var st in sector.StationRuntimes)
            if (st.SideId == sideId && garrisons.Contains(st.StationId))
                return sector;
    foreach (var sector in mission.Sectors)
        foreach (var st in sector.Stations)
            if (st.SideId == sideId && garrisons.Contains(st.StationId))
                return sector;
    return HomeSectorForSide(mission, sideId) ?? mission.Sectors[0];
}
```

The production path is untouched: when no custom spawner is installed
(`ShipSpawner is null`), `AssignShip` still routes through `SpawnDefaultShip`,
which already handles `requestedSideId` correctly — and `HomeSectorForSide`
keeps its production "real map home" meaning there.

---

## 3. Judgment call (task point 3): hull-mix & positioning on relocation

The task asked for an explicit judgment: re-derive the spawner's per-side
Scout/Fighter hull mix on relocation, or accept a near-station (slightly off)
position within the home sector.

Decision: **keep the spawner-assigned hull; accept the near-station position —
do not re-derive the slot grid.** Reasoning:

- The spawner's hull mix (`fighter = (n/2) % 2`, `Lifecycle.cs:295`) keys off
  its **private per-side ordinal counter**, which `AssignShip` has no access to.
  Re-deriving it would require reproducing spawner-local policy (a Scout/Fighter
  name heuristic tied to the debug topology) inside the generic caller — the
  exact layering violation the task warns against ("reconciliation in the ONE
  caller," "don't invent a second mechanism"). The hull the spawner already
  chose is always a valid combat hull (Scout or Fighter), so a cosmetic hull
  skew on a side-switching player is immaterial to the actionable defect.
- For position, replicating `Lifecycle.cs:299-304`'s slot grid likewise needs
  the spawner's private counter. A fixed 420u x-standoff in front of the side's
  home station keeps the ship clearly "at its base" (not the exact center) and
  well inside the garrison's dock radius — the property that actually matters.

Documented here, not silently picked.

---

## 4. Reverse case (task point 4): auto-assign agrees

When no explicit side is chosen, the client sends `POSITIONREQ(-1)`
(`Bot.AutoSelectSideAfterLogon`, `Bot.cs:1229-1233`). `CompleteSideSelection`
routes that as `requestedSideId == -1 → null` into `AssignShip`
(`Server.Lobby.cs:1980`), so the reconciliation condition
(`requestedSideId is { } requested`) is false and **no relocation runs**. The
spawner's round-robin side and its placement are the same decision, so the
seated side always equals the sector the ship actually occupies. Verified by the
new `Auto_assign_side_matches_the_sector_the_spawner_actually_used` test (four
peers) and by the pre-existing P379 auto test.

---

## 5. Verification

### Whole-solution build
`dotnet build AllegianceCore.slnx -c Debug` → **0 warnings, 0 errors** (all
projects: Core, Server, Bot, Lobby, ClientCore, ClientCore.MonoGame, Explorer,
all test projects, all tools).

### New tests (tests/Allegiance.Core.Tests/LiveP390DebugSpawnerSideReconcileTests.cs)
Three live tests over a real in-process debug-mode server + real loopback
FltBot peers on the real `m2xspiderw.igc` map:

1. `Explicit_side_one_relocates_first_peer_to_plato_and_it_docks_there` — first
   peer (spawner n=0 would place on side 0/Omega) explicitly chooses side 1 →
   ship lands in Plato(1060) on side 1 **and docks at the Plato Debug Garrison**
   (station 90002).
2. `Explicit_side_zero_relocates_second_peer_to_omega_and_it_docks_there` —
   after an auto peer consumes n=0, the n=1 peer (spawner would place on side
   1/Plato) explicitly chooses side 0 → ship lands in Omega(1053) on side 0
   **and docks at the Omega Debug Garrison** (station 90001).
3. `Auto_assign_side_matches_the_sector_the_spawner_actually_used` — four
   auto-assign peers: seated side always equals the ship's sector (reverse case
   confirmed live, not assumed).

**Must-fail discriminator:** temporarily disabling the reposition
(`&& false` in `AssignShip`) makes tests 1 and 2 red (side-1 requester left at
Omega/1053, side-0 requester left at Plato/1060 — the exact pre-fix bug); the
auto test stays green. Restored after the probe.

### Full test suite
Three full runs of `tests/Allegiance.Core.Tests` (baseline before this task was
3376; +3 new = 3379 total):

- Run 1: 3374/3379 passed — 5 failures, all the documented live-wire timing
  flakes (incl. `LiveChatSectorWireTests`; `GameOverPlayersWireTests.Conquest_win...`
  in the mission-scoping flap family). Every one passed on later runs.
- Run 2: 3378/3379 passed — only the documented PERSISTENT pre-existing flake
  `LiveTurretGunnerBindingTests.Board_binds_gunner...` (fails even on the
  pristine tree; per convention not chased).
- Run 3: 3376/3379 passed — persistent TurretGunner flake + two transient
  flakes (`P366LeaderBotCommandLiveTests`, `ShipsUpdateLiveTests.Ship_leaving_...`),
  both confirmed **green in isolation**.

None of the flaking test files reference the P390 symbols
(`AssignShip`/`RepositionShipToSideHome`/`ResolveSideHomeSector`); the only
failure that recurs is the known persistent environmental one. My 3 new tests
pass in every full run.

---

## 6. Out-of-scope respected

- `ShipSpawner` delegate signature and all ~150 existing closures: untouched.
- Other debug topology behaviors (P379/P381/P382/P386/P387/P388): untouched.
- The unrelated ShopModel/PartInfoLayout crash (separately committed): untouched.

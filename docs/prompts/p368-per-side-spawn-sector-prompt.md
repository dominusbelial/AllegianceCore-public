# P368 — fix: every player spawns in the SAME sector regardless of side

## Why this task exists

Live 2-client playtest (2026-09-24, real remote server on 192.168.0.221,
2 real MonoGame clients, sides Alpha/Bravo): Domingo reported the
mini-map showing both ships in "Ganymede" when one should be on the
opposite side of the map. Traced this to a real, confirmed server bug
— **not** a display bug, the mini-map is reporting the truth.

**Root cause, confirmed by direct read** (`src/Allegiance.Server/Server.cs:504-540`,
`SpawnDefaultShip`):

```csharp
var sector = mission.Sectors[0];                    // ALWAYS the first sector
var ship = new Ship(shipId, hull, new Vector3(x, 730f, z), ...);
mission.PlaceShip(ship, sector);                     // placed here, unconditionally

// side is chosen AFTER placement, and never used to pick the sector:
if (mission.Sides.Count > 0)
{
    var sideIdx = _peerShips.Count % mission.Sides.Count;
    ship.SideId = mission.Sides[sideIdx].Id;
    ...
}
```

Every ship — auto-assigned OR explicit-side-picked — physically spawns
in `mission.Sectors[0]` ("Ganymede" in `m2xspiderw.igc`, confirmed the
literal first cluster in that map). The explicit-side path doesn't fix
this either: `CompleteSideSelection`
(`src/Allegiance.Server/Server.Lobby.cs:1808-1869`) calls `AssignShip`
(which internally calls `SpawnDefaultShip`, already committing to
sector 0) and only THEN calls `TryJoinSide` — and `TryJoinSide`
(`Server.Lobby.cs:1647-1710`) only ever sets `ship.SideId` and handles
leadership/roster broadcast; it never touches `ship.Sector` or
`ship.Position`. The `SET_CLUSTER` sent to the client
(`Server.Lobby.cs:2414`, `SectorId = live.Sector.Id`) faithfully
reports whatever `AssignShip` picked — so both sides' players really do
co-spawn in the same physical sector every time. This is a genuine
gameplay/balance bug (two teams starting in the same sector, not just
a cosmetic one), confirmed via a real live 2-side session.

## Scope

1. **Determine each side's real home sector from real data, not a
   convention/assumption**: a side's home sector is the sector
   containing a station that side owns (`Station.SideId`, confirmed a
   real field — check `Server.Combat.cs:894/1396` for how it's already
   used elsewhere). Handle the map having more than one station per
   side, if that's possible — check the real boot log's station count
   for the mission's test map(s) (`m2xspiderw.igc` reports "2
   stations" for a 2-side mission in the live test — verify whether
   that's 1-per-side or something else before assuming a 1:1 mapping).
2. **Fix the spawn-sector selection to depend on the FINAL side, not
   `mission.Sectors[0]`**, for both the auto-assign path
   (`SpawnDefaultShip`'s own round-robin, `Server.cs:504+`) and the
   explicit-side-select path
   (`CompleteSideSelection`/`AssignShip`/`TryJoinSide`,
   `Server.Lobby.cs`). Concretely: `CompleteSideSelection` already HAS
   `requestedSideId` in scope *before* it calls `AssignShip` — thread
   it through (or an equivalent) so the correct side is known before
   the ship is placed, and do ONE correct `Mission.PlaceShip` call
   using that side's home sector, rather than placing at sector 0 and
   trying to relocate afterward.
3. **Watch this specific pitfall**: `Mission.PlaceShip`
   (`src/Allegiance.Core/Model/Mission.cs:667-677`) does `ship.Sector =
   sector; sector.Add(ship);` — it does NOT remove the ship from any
   previously-assigned sector's collection first. If your fix calls
   `PlaceShip` a second time (spawn-then-relocate) instead of getting
   the sector right on the FIRST placement, the ship will likely be
   double-registered in two sectors' collections. Prefer resolving the
   correct side+sector BEFORE the single `PlaceShip` call over a
   spawn-then-relocate approach. If a relocate-after-the-fact approach
   turns out unavoidable for some code path, you must also remove the
   ship from its old sector's collection — check `Sector`'s own
   remove/leave method (used elsewhere for warping between sectors,
   e.g. warp-traversal code) and reuse it, don't hand-roll a new one.
4. **Fallback when a side has no owned station yet** (e.g. very early
   mission state, or a map/config with no per-side stations): fall
   back to `mission.Sectors[0]` and log the fallback plainly (matching
   this same function's existing convention just below it for "mission
   has no sides" — a logged finding, not a silently fabricated value).
5. Auto-assigned peers (no explicit side pick) must also end up in
   their assigned side's real home sector — this path currently has
   the identical bug (side picked via round-robin AFTER the sector 0
   placement), fix it the same way.

## Explicitly out of scope

- The mini-map/starmap rendering code itself
  (`Rendering/StarmapRenderer.cs`) — already confirmed correct (it
  faithfully renders whatever `SectorId` the server reports). Do not
  touch it; this is a pure server-side spawn-placement fix.
- Any other P358-P367 work — unrelated, don't touch.
- Redesigning how sides get assigned stations in the first place (map
  generation / mission setup) — this task only fixes where a NEWLY
  SPAWNED SHIP is placed given the side (and that side's) already-
  existing stations, not how stations get assigned to sides.

## Verification — real proof, not just build/test

- **Live proof, required**: a real 2-client session against a real
  server (reuse the same 2-side setup Domingo just ran: direct-connect
  `dotnet run --project src/Allegiance.ClientCore.MonoGame -- <host>
  <port> <name>`, one client on each side). Confirm via the mini-map
  (and/or server logs) that the two ships spawn in DIFFERENT, correct
  per-side sectors — not both in the same one. Show the actual sector
  names/ids observed for each side in the report.
- Whole-solution build: 0 warnings, 0 errors.
- Full test suite (`/home/domingo/.dotnet/dotnet test`), run at least
  2x: only the pre-existing documented live-wire timing flakes are
  expected. Pay particular attention to any existing spawn/placement
  tests that assumed sector-0-for-everyone — if any exist and now
  fail, fix the test's assumption (it was testing the bug), don't
  weaken the fix to keep a wrong test passing.

## Deliverables

1. The per-side spawn-sector fix, committed on `master` (not pushed —
   Domingo pushes). Do not create a side branch.
2. Report doc `P368_PER_SIDE_SPAWN_SECTOR.md` in the repo root: the
   exact mechanism used to resolve a side's home sector, how the
   double-registration pitfall (scope item 3) was avoided, and the
   live 2-client verification performed (real sector names/ids for
   each side).

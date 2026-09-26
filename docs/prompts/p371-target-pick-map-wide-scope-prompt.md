# P371 — target-pick (T-cycle) offers every object on the WHOLE MAP, not just your sector

## Why this task exists

Domingo reported live: "we need to reduce the range of the hud/radar,
it should not go beyond the sector, i should not be scrolling to the
large list of unnamed targets with t". Confirmed as a real, clear bug
by direct code read.

**Root cause** (`src/Allegiance.ClientCore.MonoGame/Game1.cs`, the
target-pick candidate gathering function, ~line 5563-5580): the
candidate list is built from `world.Ships`/`world.Warps`/
`world.Stations`/`world.Asteroids` with **zero sector filtering and
zero distance filtering**:

```csharp
foreach (var (id, ship) in world.Ships)     // every known ship, ANY sector
    if (id == me || ship.IsDead || ship.Latest is not { } up) continue;
    ...
foreach (var (id, wp) in world.Warps)        // ALL 44 warps on the whole map
    ...
foreach (var (id, st) in world.Stations)     // ALL stations on the whole map
    ...
foreach (var (id, ast) in world.Asteroids)   // ALL 134 asteroids on the whole map
    ...
```

These collections are the client's full, unconditionally-exported,
map-wide sets (every warp/station/asteroid of every sector is exported
to every peer at join — a deliberate simplification from an earlier
task, not something to change). The ONLY filter applied afterward is a
raw screen-space frustum projection with no distance cutoff
(`SelectCandidates`, checking `Math.Abs(sx) > 1f` etc.) — no check
against the player's current sector at all. Since sectors don't share
a bounded/related coordinate space, an object from a totally different
sector can coincidentally project inside the camera's view and get
offered as a real target.

**This is inconsistent with how this exact problem is already solved
correctly elsewhere in the same file**: the warp-draw loop
(`Game1.cs:2824-2835`) already does this right —
`if (wp.ClusterId != myWarpSector) continue;` — filtering to only the
warps in the player's own current sector before ever considering
frustum/visibility. The target-pick loop just never got the same
treatment.

**"Unnamed" targets, explained**: `DataAsteroidIGC`/asteroids don't
carry meaningful individual names, and any ship with no
`CharacterName` (including a stale/ghost entry, see P370) falls back
to a generic `"ship {id}"` label. With all 134 map-wide asteroids and
every known ship (including out-of-sector ones) dumped into one
candidate pool, most of what a player cycles through with `T` is
exactly this kind of noise, not real nearby objects.

## Scope

1. **Filter every candidate source to the player's own current
   sector** before frustum selection, mirroring the exact pattern
   already used correctly in the warp-draw loop:
   `DataWarpIGC.ClusterId` for warps (already confirmed to exist and
   already used this way). Find the equivalent real cluster/sector id
   field for stations and asteroids (check `ExportStation`/
   `ExportAsteroid`/`DataAsteroidIGC` — `ExportAsteroid.cs`'s own doc
   comment already references `DataAsteroidIGC.ClusterId`, confirm and
   use it; find stations' equivalent the same way) and apply the same
   filter. For ships, filter by `ship.SectorId == mine.SectorId`
   (reading the local player's own current sector the same way the
   warp-draw loop already does via `ownW.SectorId`/`myWarpSector`).
2. **Confirm this doesn't just paper over P370's ghost-ship bug** —
   once P370 lands (already in flight, don't wait if it hasn't landed
   yet by the time you start, just don't assume it's the same fix:
   this task's sector-filter is independently correct regardless of
   whether P370's "ship left view" notification exists, since even a
   properly-notified/cleaned-up ghost should never have been eligible
   as a target-pick candidate from a different sector in the first
   place). Both fixes are complementary, not redundant.
3. Keep the existing frustum/FOV selection logic exactly as-is for
   whatever remains AFTER the sector filter — this task only narrows
   the candidate POOL, it doesn't change how a candidate is chosen
   from within it.

## Explicitly out of scope

- P370's ghost-ship-never-notified fix — separate, complementary,
  don't touch its files beyond what's needed to avoid conflicts if it
  hasn't landed yet.
- Any other target-selection UI/logic beyond the candidate-gathering
  scope described above.
- The warp-draw loop itself (`Game1.cs:2824-2835`) — already correct,
  reference it, don't modify it.

## Verification — real proof, not just build/test

- **Live proof, required**: real multi-client session, at least 2
  clients in DIFFERENT sectors of the same mission with real
  ships/warps/asteroids around. Confirm pressing `T` (or whatever the
  real target-cycle key is) on one client only ever offers objects
  actually in that player's CURRENT sector — no cross-sector ships,
  warps, stations, or asteroids should ever appear in the cycle list.
  Confirm the list length/content matches what's really nearby, not a
  map-wide dump (compare against the real per-sector warp/asteroid
  counts from the map data, e.g. via the `missiondump` tool used
  earlier this session).
- Whole-solution build: 0 warnings, 0 errors.
- Full test suite (`/home/domingo/.dotnet/dotnet test`), run at least
  2x: only the pre-existing documented live-wire timing flakes are
  expected.

## Deliverables

1. The sector-filtered target-pick fix, committed on `master` (not
   pushed — Domingo pushes). Do not create a side branch.
2. Report doc `P371_TARGET_PICK_SECTOR_SCOPE.md` in the repo root: the
   real cluster/sector id fields used for each object type, and the
   live verification performed (real per-sector candidate counts
   before/after, for at least 2 different sectors).

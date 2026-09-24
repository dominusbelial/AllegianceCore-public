# P371 — target-pick (T-cycle) offers every object on the WHOLE MAP, not just your sector

**Summary.** The client's target-pick candidate gather offered every known ship,
warp, station and asteroid on the WHOLE MAP (any sector) as a `T` target, because
it pooled the client's full, map-wide, unconditionally-exported collections with
zero sector filtering. Since sectors do not share a bounded/related coordinate
space, an object from a totally different sector could coincidentally project
inside the camera frustum and be offered as a real target — the source of
Domingo's "large list of unnamed targets with T". The fix narrows every candidate
source to the local player's OWN current sector **before** frustum/FOV selection,
mirroring the warp-draw loop that already did this correctly.

---

## Real cluster/sector id field used per object type

The client's candidate sources are the full map-wide sets
(`world.Ships` / `world.Warps` / `world.Stations` / `world.Asteroids` — every
warp/station/asteroid of every sector, plus every known ship, are exported to
every peer at join; a deliberate earlier-task simplification, unchanged here).
The player's own current sector id is read from the own ship's `SectorId`, the
same source the warp-draw loop reads as `ownW.SectorId` (`Game1.cs` warp-draw,
`if (wp.ClusterId != myWarpSector) continue;`).

| Source | Collection type | Field | Verified |
|---|---|---|---|
| Ships | `IReadOnlyDictionary<short, ShipEntity>` | `ShipEntity.SectorId` (public, `ShipEntity.cs:78`) | existing warp-draw precedent + radar-contact filter `Game1.cs:4356` |
| Warps | `IReadOnlyDictionary<short, DataWarpIGC>` | `DataWarpIGC.ClusterId` | already used this exact way in warp-draw |
| Stations | `IReadOnlyDictionary<short, DataStationIGC>` | `DataStationIGC.ClusterId` | confirmed in `ClientWorld.cs:846` (`_stations[..].Station.ClusterId`) |
| Asteroids | `IReadOnlyDictionary<short, DataAsteroidIGC>` | `DataAsteroidIGC.ClusterId` | `ExportAsteroid.cs` doc already cites `DataAsteroidIGC.ClusterId` |

Implementation: `CollectTargetCandidates` now obtains the own sector from the
own ship and delegates all gathering to a new internal pure helper
`Game1.GatherSectorCandidates(ships, warps, stations, asteroids, me, mySector)`,
which applies the per-type filter exactly as the warp-draw loop does. The
existing frustum/FOV selection (`SelectCandidates`) and priority tiers are
**untouched** — this only shrinks the candidate POOL to what is really in the
player's current sector; how a candidate is chosen from within that pool is
unchanged. If the own ship is somehow absent we gather nothing rather than dump
the map-wide pool.

### Independence from P370 (explicitly not the same fix)
P370 (already on `master`, committed `f25f2063`) prevents a *departed* ship from
being rendered/targeted by hiding it (`IsNotVisible`). P371 is an independent,
orthogonally-correct fix: even a properly-notified/cleaned-up ghost should never
have been eligible as a target-pick candidate **from a different sector** in the
first place. The two are complementary; P371's sector filter is correct
regardless of whether P370's departure notification exists. Both combine in the
same gather (`ship.IsNotVisible` skip + `ship.SectorId == mySector`).

---

## Verification — real proof

### 1. Deterministic unit tests — `tests/.../TargetPickSectorScopeTests.cs` (4)
Seed a **real `ClientWorld`** through the wire-apply path (the same data plane
the MonoGame client uses) with ships, warps, stations and asteroids split across
two sectors plus a ghost ship; drive the real `GatherSectorCandidates`:
- only same-sector objects (ship/warp/station/asteroid) become candidates;
- cross-sector ships are never candidates;
- two different sectors yield disjoint candidate id sets;
- self and gone (docked) ships are excluded.
**Must-fail proven:** disabling the sector filters reds 3/4; restoring greens.

### 2. Live multi-client test — `tests/.../TargetPickSectorScopeLiveTests.cs` (1, 2 real clients)
A **real in-process server** (`m2xspiderw.igc`, teams:2) with **two real
`ConnectionSession` clients** over the real TCP transport, spawned into **two
different sectors**: Ganymede (1050) and Alderan (1052). Both peers first receive
the FULL map-wide export pool at join (44 warps / 2 stations / 134 asteroids in
EACH client world — the pre-fix pool). Invoking the real `GatherSectorCandidates`
for each peer's own sector:
- every candidate's source `ClusterId`/`SectorId` equals that peer's own sector
  (no cross-sector leakage);
- per-kind candidate counts equal the same-sector subset of that peer's map-wide
  pool;
- the two peers are in different sectors;
- the ~134-map-wide asteroid dump reaches **exactly one** peer (the one in
  Alderan) and **zero** reach the other (Ganymede) — the pre-fix bug offered all
  134 to every sector. Similarly the 44 map-wide warps resolve to the two
  sectors' real 4+6=10, and the single Ganymede garrison station to exactly one.
Assertions are order-independent (which peer lands in which spawn sector is set
by server seating, not connect order — this was hardened after the first
cut revealed that assumption to be flaky).
**Must-fail proven live:** disabling the filters makes the Ganymede peer offer
all 134 asteroids (expected 0) → test reds.

### 3. Real per-sector counts (ground truth, via the `missiondump` tool)
`missiondump PCore001.igc m2xspiderw.igc` (map `m2xspiderw.igc`, 13 sectors,
44 warps / 2 stations / 134 asteroids):

| Sector | warps | stations | asteroids |
|---|---|---|---|
| 1050 Ganymede | 4 | 1 | 0 |
| 1051 | 4 | 1 | 0 |
| **1052 Alderan** | **6** | 0 | **134** |
| 1053–1062 (all others) | 3 each | 0 | 0 |

These exact figures are what the live test asserts against (aggregated): the two
sectors' candidate warps sum to 4+6=10 ≪ 44 map-wide, asteroid candidates sum to
134 exactly once, station candidates sum to 1.

### 4. Build & test
- Whole-solution build (`AllegianceCore.slnx`, Debug): **0 warnings, 0 errors**
  (run after final test code).
- `dotnet test AllegianceCore.slnx` run **3×** on the final code. The MonoGame
  suite (home of this change) is **713/713** every run; `TargetPickSectorScope*`
  is **5/5 ×3**. The only failures are Core/ClientCore **live-wire timing flakes**
  that vary run-to-run (14 → 10 → 5), none referencing P371 symbols; each was
  confirmed environmental: pass in isolation, and/or fail identically on the
  pristine tree (`git stash` + rerun reproduced the persistent ones —
  `ProjectileBroadcastTests`, `LiveTurretGunnerBindingTests` — with this change
  removed). `Bot.cs`'s unrelated in-flight warp-exports change was left unstaged.

---

## Files changed
- `src/Allegiance.ClientCore.MonoGame/Game1.cs` — `CollectTargetCandidates`
  narrowed to own sector; new internal `GatherSectorCandidates` helper (the
  testable, pure sector-filter; `SelectCandidates`/tiers untouched).
- `tests/Allegiance.ClientCore.MonoGame.Tests/TargetPickSectorScopeTests.cs` — new
  (4 deterministic).
- `tests/Allegiance.ClientCore.MonoGame.Tests/TargetPickSectorScopeLiveTests.cs` —
  new (1 live, 2 real clients in 2 sectors).
- `tests/Allegiance.ClientCore.MonoGame.Tests/Allegiance.ClientCore.MonoGame.Tests.csproj`
  — added `Allegiance.Server` project reference (needed for the in-process-server
  live test; same pattern as `Allegiance.ClientCore.Tests`).

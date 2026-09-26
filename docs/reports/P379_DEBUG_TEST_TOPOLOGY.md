# P379 — Debug-mode test topology: Omega/Plato home bases, single choke-point warp to Alderan

Branch: `feature/p379-debug-test-topology` (NOT pushed, no PR — for Domingo to review/push).

Everything is gated on the single reusable `liveSettings.debugMode` flag landed by
P378. With the flag off, production server behavior is byte-for-byte unchanged —
the entire block is skipped and no shared spawn/warp/station path is touched.

## What was built

Three additive, debug-mode-only effects, applied once at boot right after
`Mission.Assemble` and before the mission stage is set (all in
`src/Allegiance.Server/Server.Lifecycle.cs`):

1. **Two synthetic home-base garrisons injected** via the existing public
   `Mission.PlaceStation(Station, Sector)` — one in **Omega (1053)** owned by
   **side 0**, one in **Plato (1060)** owned by **side 1**. Both use
   `StationTypeId = 15`, the exact real garrison type the map's real Ganymede /
   Organia home garrisons use (so they inherit identical `SabmRestart` /
   `SabmLand` / etc. capabilities with no hand-crafted bitmask), with
   `BpHull`/`BpShield = 240/240` mirrored from the real garrisons and real
   interior positions.
2. **Warp isolation** via the new `public bool Sector.RemoveWarp(ResolvedWarp)`
   (added to `src/Allegiance.Core/Model/Sector.cs`, mirroring the existing
   `RemoveStation`): Omega's warps **2154→Bug Nebula** and **2156→Tellus** are
   removed; Plato's **2179→Ganymede** and **2190→Rigel** are removed. The
   Alderan choke points **2153** (Omega→Alderan) and **2184** (Plato→Alderan)
   are kept, and **Alderan(1052)'s own warp list is left completely untouched**
   (all six real warps). Removal is one-directional (each sector's warp list is
   its own), so the kept Omega↔Alderan / Plato↔Alderan pairs are never affected.
   Every real consumer (CLUSTERINFO graph build, the per-peer warp export loop,
   warp collision, NPC pathfinding BFS) reads `Sector.Warps` directly, so
   removing from the source sector's list is sufficient — verified the per-peer
   export loop in `Server.Lobby.cs` iterates `sector.Warps`, so the trimmed
   gates genuinely disappear from the client view.
3. **Debug `ShipSpawner` closure** wired through the existing `internal
   Func<StaticCore, Mission, short, (Ship, Sector)>? ShipSpawner` seam (set at
   boot, mirrors `tools/vizfight`'s `InjectExplorerSpawn` pattern): a
   closure-captured monotonic counter (never derived from `_peerShips.Count`,
   safe across reconnects). Side alternates `spawnCount % 2` (side 0 → Omega,
   side 1 → Plato); hull alternates `(spawnCount / 2) % 2` — each side's first
   peer → **Scout**, second → **Fighter**, cycling again for a 5th+ connection
   instead of erroring (handles any number of clients). Hulls resolved by real
   name-substring lookup (`Contains("Scout"/"Fighter", OrdinalIgnoreCase)`,
   never a hardcoded hull id). `Ship.SideId` is explicitly set before return (a
   real requirement — without it a peer's own `EXPORT(26)` never fires). Ships
   are placed via `Mission.PlaceShip` just off their base's station (outside
   dock radius so they visibly fly in to dock/buy, or straight to the warp).

A loud boot log names the real topology so it is never silently on by accident:
`[boot] DEBUG MODE ACTIVE — side 0 home Omega(1053)/side 1 home Plato(1060),
single warp each to Alderan(1052), Scout+Fighter mix`.

## StationId constants chosen

| Base | Side | StationId (constant) | Runtime short value | Position |
|---|---|---|---|---|
| Omega | 0 | `unchecked((short)90001)` | 24465 | (800, 990, 0) |
| Plato | 1 | `unchecked((short)90002)` | 24466 | (-1060, -612, 0) |

Deliberately in the 90000s, clear of the real corpus garrison ids (**4328/4329**)
and of `Mission.GenerateNewStationID()`'s internal counter (starts at **9187**
and only ever emits small positives; it is also `internal` to Allegiance.Core and
not callable from Allegiance.Server anyway). Station ids are only ever compared
against other station ids, so the wrap past `short.MaxValue` is immaterial —
the value round-trips exactly through the wire (verified the dock latch uses the
same constant).

## Verification performed

All against the real `m2xspiderw.igc` + `PCore001.igc` (topology cross-checked
first with `tools/missiondump` — sector ids, warp ids/directions, station
absence, Alderan's untouched warp list).

Automated live proof (`tests/Allegiance.Core.Tests/LiveP379DebugTopologyTests.cs`,
real in-process server on real loopback TCP + real `Bot` peers) — all 3 pass:

- **Debug ON, warp/station topology**: Omega has exactly one warp (2153→Alderan)
  and a side-0 garrison-type station (id 90001/24465, StationTypeId 15);
  Plato has exactly one warp (2184→Alderan) and a side-1 garrison (90002/24466);
  Alderan keeps all six warps and gains no station. Both injected stations are
  present in `Sector.Stations` (static, for docking) and `Sector.StationRuntimes`
  (combat/respawn).
- **Debug ON, spawn placement + hull mix** (4 real peers, one at a time for
  deterministic counter order): peer0 side0 **Lxy Scout** → Omega(1053),
  peer1 side1 **Lxy Scout** → Plato(1060), peer2 side0 **Lxy Fighter** → Omega,
  peer3 side1 **Lxy Fighter** → Plato — each side gets exactly one Scout + one
  Fighter.
- **Debug ON, dockable station**: a peer moved to the synthetic Omega garrison's
  position latches a real dock (`Ship.StationId == 24465`, the same `StationTypeId
  15` garrison the real bases use) — dock/buy loop reachable.
- **Debug OFF (regression)**: with default config, Omega/Plato stay genuinely
  station-less, both keep their full real 3-warp networks, Alderan keeps all six
  warps, and a real peer spawns at its real home sector (Ganymede 1050) — exactly
  as before this change.

Whole-solution build: **0 warnings, 0 errors**.

Full test suite: run twice (per the task). Both runs show only pre-existing
live-wire timing flakes in unrelated gameplay tests (chat relay, win
conditions/leaks, turret-gunner bind, part pickup, and the P377/P378 lifepod
timer measurements), never in the P379 topology or any file this change touched.
(Baseline-confirmed against a clean `master` checkout — same flaky set, see
below.)

## Notes / deferred

- Spawning bombers or other test fixtures at Alderan: explicitly deferred (later
  step per Domingo) — not built.
- No generic/configurable N-sector topology system — the one real topology is
  hardcoded behind `debugMode`.
- Explicitly out of scope (untouched): `AssignShip`, `CompleteSideSelection`,
  `SpawnDefaultShip`, `HomeSectorForSide`, and all of P368–P378 (P378's
  `LiveSettings`/`DebugMode` config surface is reused as-is, branched from its
  master tip).
- Final dock/buy at the debug station is best exercised in the live MonoGame
  session (the automated proof confirms the station is present and dockable);
  bucket funding/donation is side-scoped and needs no station proximity, so the
  economy loop works the moment two sides exist.

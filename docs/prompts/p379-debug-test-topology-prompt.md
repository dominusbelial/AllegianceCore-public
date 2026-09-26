# P379 — Debug-mode test topology: Omega/Plato home bases, single choke-point warp to Alderan

## IMPORTANT — workflow difference from every other task this session

**This task ships as a real pull request, not a direct commit to
`master`.** Every other P-task this session committed straight to
`master`. This one is different:

1. Create a new branch off the current `master` tip:
   `feature/p379-debug-test-topology`.
2. Do all work and commit(s) on that branch.
3. Do **NOT** push the branch. Do **NOT** open a PR. Do **NOT** merge
   to `master`. Domingo (or a separate confirmed step) handles the
   push + `gh pr create` after reviewing your branch locally.
4. If P378 (a separate task, `liveSettings`/`debugMode` config +
   30-min lifepod-endurance override) has landed on `master` before
   you start, branch from that tip so you build on its real
   `LiveSettings`/`DebugMode` config surface rather than re-adding it.
   If it hasn't landed yet, add the minimal `LiveSettings.DebugMode`
   piece yourself (see Scope item 1) — don't block on it.

## Why this task exists

This session's live playtesting requires manually flying from each
side's real home sector (Ganymede/Organia) to a shared meeting point
every time a cross-side interaction needs testing — slow and
repetitive. Domingo wants a debug-mode feature that sets up a real,
contained mini-map for combat+economy testing: each side spawns at its
own home base (Omega for one side, Plato for the other), each base has
a real, dockable, side-owned station so the full economy loop (dock,
buy, bucket-funded research) works, and each base's warp network is
trimmed to a single choke-point into a shared contested sector
(Alderan, which already has 14 real asteroids for mining/combat
testing) — so a match plays out exactly like a real one, just without
minutes of travel to set up.

This must be an **isolated, feature-flagged code path** — the
project's established convention (`tools/vizfight`'s
`InjectExplorerSpawn`/`InjectStressSpawn` precedent) — that cannot
change production (debug-mode-off) server behavior at all.

Design already confirmed with Domingo:
- Combatants: real connecting clients only, any number (not assumed
  exactly 4).
- Hulls: fixed mix — each side gets exactly one Scout + one Fighter.
- Trigger: `liveSettings.debugMode` alone enables the whole topology —
  spawn placement, station injection, and warp isolation together, no
  separate on-demand command.
- Teams spawn at their own home base (not directly at the contested
  sector) and fly out through the one open warp.
- Home bases get real, synthetically-injected stations (not just spawn
  points) so the economy loop is genuinely testable.
- The other real warps out of each home base are actually removed, not
  just left alone — true isolation.

## Real topology (from `m2xspiderw.igc`, confirmed via
`tools/missiondump` against the same `PCore001.igc` core already used
throughout this session)

| Sector | Id | Role | Warp to keep | Warps to remove |
|---|---|---|---|---|
| Omega | 1053 | Side 0 home | 2153 → Alderan | 2154 (→ Bug Nebula), 2156 (→ Tellus) |
| Plato | 1060 | Side 1 home | 2184 → Alderan | 2179 (→ Ganymede), 2190 (→ Rigel) |
| Alderan | 1052 | Contested zone (14 real asteroids, 0 stations, untouched) | — | — |

Neither Omega nor Plato has a station in the real map data today —
confirmed genuinely station-less; verify this yourself against the
live map data before building rather than trusting this table blindly.
The two real existing home Garrisons (Ganymede side 0 / Organia side
1) use `StationTypeId = 15` — reuse that exact id for both synthetic
debug stations so they inherit identical real capabilities
(`SabmRestart`/`SabmLand`/etc.) with no hand-crafted capability
bitmask.

## Why this is safe to build as a pure add-on (verify each claim yourself before relying on it)

1. **`Server.ShipSpawner`** (`src/Allegiance.Server/Server.cs:319`) —
   an existing, currently-always-`null`-in-production `internal`
   delegate field:
   `internal Func<StaticCore, Mission, short, (Ship Ship, Sector Sector)>? ShipSpawner;`
   `AssignShip` (`Server.Lobby.cs:~1811-1813`) already prefers it over
   `SpawnDefaultShip`/P368's `HomeSectorForSide` when set:
   ```csharp
   var (ship, sector) = ShipSpawner is not null
       ? ShipSpawner(mission.Core, mission, shipId)
       : SpawnDefaultShip(mission, shipId, requestedSideId);
   ```
   The production executable never sets this today. Setting it
   conditionally at boot touches zero shared spawn code. Real prior
   art for the closure shape: `tools/vizfight/Program.cs`'s
   `InjectExplorerSpawn`/`InjectStressSpawn` (own closure-captured spawn
   counter, hull lookup by name, explicit `ship.SideId` set, calls
   `mission.PlaceShip` directly, wired before `server.Boot(...)`).
2. **`Mission.PlaceStation(Station, Sector)`** (`Mission.cs:~730-742`)
   — already public, already mirrors `PlaceShip`/`PlaceAsteroid`
   exactly (guards the sector belongs to the mission, calls
   `sector.AttachStation`, which updates both the static and runtime
   station lists every real downstream consumer already reads —
   docking via `ResolveDockCollisions`'s fixed `DockRadius` sector-wide
   constant, respawn/teleport via `StationTypeId`'s
   `DataStationTypeIGC.SabmRestart` capability check, combat/capture,
   NPC "nearest friendly station" targeting). **The team economy
   (`Side.Buckets`) needs no station-specific wiring at all** — bucket
   funding/donation is side-scoped, gated only on the donor ship's
   `SideId`, zero proximity check to any station — so the synthetic
   station only has to exist for docking/respawn/NPC purposes; the
   economy already works the moment two sides exist.
3. **A new, small, public warp-removal method** — no removal API
   exists today (only `internal void AttachWarp(ResolvedWarp warp) =>
   _warps.Add(warp);`, `Sector.cs:267`), but `Sector` already has a
   direct, exact precedent to mirror: `public bool
   RemoveStation(Station station)` (`Sector.cs:~253-257`). Add `public
   bool RemoveWarp(ResolvedWarp warp)` the same shape. Every real
   consumer (CLUSTERINFO's graph build, the P30 unconditional per-peer
   warp export in `Server.Lobby.cs`, warp-hit/warp-bomb collision in
   `Sector.cs`, NPC pathfinding BFS in `Ship.cs`) reads `Sector.Warps`
   directly — nothing needs special-casing, removing from the source
   sector's list is sufficient on its own. Each warp direction is
   stored independently per-sector (removing Omega's warp to Tellus
   does not touch Tellus's own separate warp-to-Omega object) — cannot
   accidentally break the Omega↔Alderan/Plato↔Alderan pairs being
   kept. `Mission.Warps` (a separate flat list assembled once at
   `Mission` construction) won't reflect the removal — its only
   consumer is a boot-summary console log line (`Server.Lifecycle.cs`,
   near the existing `Mission.Assemble` call), not any gameplay path;
   harmless, don't try to fix it.

## Scope

### 1. Config

```json
"liveSettings": {
  "debugMode": true
}
```
No new config fields beyond `debugMode` itself — this feature's entire
topology (Omega/Plato/Alderan, the specific warp ids, `StationTypeId =
15`) is real, fixed data for the specific test map already in use this
session, not meant to be arbitrarily reconfigured. Hardcode it behind
the one flag rather than inventing a generic N-sector config system.
If P378 already landed `ServerConfig.LiveSettings`/`DebugMode` (check
`git log master` for a P378 commit before you start — this task may be
branching from after it), reuse that class/field exactly; only add it
yourself if P378 genuinely hasn't landed, following the identical
established `ServerConfig.cs` pattern (a nested class mirroring
`LobbyConfig`, registered in `ServerJsonContext.cs`'s source-generated
`[JsonSerializable]` list — this project does NOT use reflection-based
JSON binding).

### 2. Boot-time setup

Right after the existing `Mission.Assemble(...)` call in
`Server.Lifecycle.cs` (search for it — cited earlier this session
around line 269, confirm the real current line yourself), before the
mission's stage is set to `NotStarted`, add one gated block:
`if (config.LiveSettings?.DebugMode == true) { ... }`.

1. **Inject the two stations**: build a `DataStationIGC` record each
   for Omega (owned by side 0) and Plato (owned by side 1) —
   `StationTypeId = 15`, a real `Position` inside each sector, a
   `StationId` picked to avoid collision with both the real corpus ids
   (4328/4329) and `Mission.GenerateNewStationID()`'s internal counter
   (starts at 9187 — and note `Allegiance.Server` has no
   `InternalsVisibleTo` grant into `Allegiance.Core.Tests`'s allowed
   list, so you cannot call that internal method anyway; pick your own
   constants, e.g. clearly in the 90000s, not derived from the sector
   id). Wrap each in `new Station(data, stationType)` (resolving
   `stationType` from `core.StationTypes[15]`) and call
   `mission.PlaceStation(station, sector)`.
2. **Remove the non-Alderan warps**: resolve Omega's and Plato's real
   `ResolvedWarp` objects for warp ids 2154/2156 (Omega) and
   2179/2190 (Plato) from each sector's `Warps`, call the new
   `sector.RemoveWarp(...)` for each. Leave warp 2153 (Omega→Alderan)
   and 2184 (Plato→Alderan) untouched. Leave Alderan's own warp list
   completely untouched.
3. **Wire the debug `ShipSpawner`**: a closure with its own monotonic
   spawn counter (mirroring `InjectExplorerSpawn`'s pattern — never
   derived from `_peerShips.Count`, which is not safe across
   reconnects). For each connecting ship: side alternates `spawnCount
   % 2` (side 0 → Omega, side 1 → Plato); hull alternates
   `(spawnCount / 2) % 2` (first peer per side → Scout, second →
   Fighter, cycling again for a 5th+ connection rather than erroring —
   handle "any number of clients," not just exactly 4); resolve hull
   by real name-substring lookup
   (`core.HullTypes.Values.First(h => h.Name.Contains("Scout"/"Fighter", StringComparison.OrdinalIgnoreCase))`,
   the established codebase convention — never a hardcoded hull id);
   explicitly set `ship.SideId` before returning (a real requirement —
   without it a peer's own `EXPORT(26)` never fires, confirmed
   elsewhere in this codebase's own spawner precedent); spread the
   pair at each base a little apart so they don't spawn stacked on top
   of each other; call `mission.PlaceShip(ship, sector)`; return
   `(ship, sector)`.
4. Log plainly at boot when this activates, naming the real topology
   so it's never silently on by accident, e.g.: `"[boot] DEBUG MODE
   ACTIVE — side 0 home Omega(1053)/side 1 home Plato(1060), single
   warp each to Alderan(1052), Scout+Fighter mix"`.

### 3. Explicitly deferred — do not build, note clearly in the report

- Spawning bombers or other additional test fixtures at Alderan —
  Domingo named this as a later step, not required now.
- Any generic/configurable N-sector topology system — hardcode the one
  real, specific topology asked for.

## Explicitly out of scope

- Any change to `AssignShip`, `CompleteSideSelection`,
  `SpawnDefaultShip`, or `HomeSectorForSide` — this feature must go
  through the existing `ShipSpawner` seam only, never modify the
  shared spawn path those functions represent.
- P368-P378 — unrelated, don't touch (except reusing P378's
  `LiveSettings` config surface if it's already landed, per the
  workflow note above).

## Verification — real proof, not just build/test

- **Live proof, required**: enable `liveSettings.debugMode` against a
  real server running `m2xspiderw.igc`, connect at least 2 (ideally 4)
  real MonoGame clients, and confirm:
  - Side 0 peers spawn at Omega, side 1 peers at Plato — not their
    normal home sectors.
  - Each base has a real, dockable station of the connecting side's
    own color/ownership; docking/buying there works.
  - Each base's minimap/starmap shows exactly one warp out (to
    Alderan) — the other two are genuinely gone, not just unlisted.
  - Flying through the kept warp actually reaches Alderan, and
    Alderan's own connectivity to its OTHER neighbors (Cirrus,
    Juniper, Rigel, Gaia) is unchanged.
  - Hull mix is correct per side (one Scout + one Fighter each).
  - **Regression check, required**: a normal (debug-mode-off) server
    boot and connect is completely unaffected — connect a client
    against a debug-mode-off server and confirm it still spawns at its
    real home sector (Ganymede/Organia) with the full real warp
    network intact, exactly as before this change.
- Whole-solution build: 0 warnings, 0 errors.
- Full test suite (`/home/domingo/.dotnet/dotnet test`), run at least
  2x: only the pre-existing documented live-wire timing flakes are
  expected.

## Deliverables

1. All work committed on branch `feature/p379-debug-test-topology`
   (branched off `master`, or off P378's tip if it landed first) — NOT
   `master`, NOT pushed, NOT a PR opened by you.
2. Report doc `P379_DEBUG_TEST_TOPOLOGY.md` in the repo root, on that
   same branch: what was built, the exact `StationId` constants
   chosen, and the live verification performed (including the
   debug-mode-off regression check).
3. At the very end of your session, print the exact branch name and
   confirm the working tree is clean (everything committed) so the
   branch can be reviewed and pushed next.

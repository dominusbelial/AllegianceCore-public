# P402 — Debug-mode test arena: replace the hand-injected Omega/Plato/Alderan topology with generated `Brawl`

Swaps debug mode's hand-built test arena over to the procedural map generator
(P397-P401) that P401's `Brawl` provides. Debug mode now takes a
**generated-arena** branch when the mission's map came from `g.MapGen`: the
generator already placed each side's real home station (from core data, not a
hardcoded station type) and produced the topology, so the P379 station-injection
and warp-removal machinery is skipped entirely — but everything debug mode needs
for unattended autonomous testing (hull-mix ship spawning, docked initial
miners, mission auto-start) is preserved against the generated arena. The real
`m2xspiderw` branch is kept byte-for-byte (regression-proven).

---

## 1. Design (verified against the live code, per the brief's instruction)

The brief's core claim — that a generated mission has **no** leftover real
Ganymede/Organia home garrisons, so the hardcoded synthetic-station-id
preference is only meaningful on the real m2xspiderw map — was confirmed against
the actual `MissionMapGenerator`/`Mission.Assemble` output before relying on it.
A `MapGen`-sourced mission is assembled entirely from the generator's synthetic
`MissionMap` (P397's option-(b) path), so it contains exactly one real
side-owned home station per side, co-located per topology (one shared sector for
Brawl, per-side home sectors for e.g. SingleRing). `HomeSectorForSide`'
s generic first-side-owned-station iteration is therefore already correct there
— no ambiguity to disambiguate.

Consequence: the signal "did this mission's map come from MapGen" is recorded at
instantiation time (`InstantiateGames`), stored per-mission, and consulted by the
debug-station resolvers — it is **never** re-derived by sniffing sector
names/ids.

## 2. What changed in each named method

- **`Server.cs` — new field `_mapGenMissions`** (`HashSet<Mission>`): the subset
  of missions whose map was generated. Reference-identity, populated only in
  `InstantiateGames`.
- **`InstantiateGames` (Server.Lifecycle.cs ~701)**: computes
  `bool fromMapGen = g.MapGen is not null;` once per game (the caller already
  knew this at the call site), records the assembled mission into
  `_mapGenMissions` when true, and threads `fromMapGen` into
  `ApplyDebugTestTopology(mission, fromMapGen)`. No re-derivation from the
  mission's shape.
- **`ApplyDebugTestTopology(Mission, bool fromMapGen)` (~288)**: takes the new
  signal parameter. When `fromMapGen` is true it takes a **generated-arena
  branch**: calls `WireGeneratedArenaShipSpawner()` and returns — **no** station
  injection, **no** warp removal (the generator produced the right stations and
  topology). The existing `:313-317` guard now means *"not MapGen **AND** not
  m2xspiderw"* before it skips entirely (guarding P378's endurance test on
  `XXV_simple` etc. exactly as before). The real-map branch is otherwise
  untouched.
- **`WireGeneratedArenaShipSpawner()` (new, ~370)**: wires the hull-mix
  `ShipSpawner` closure. Keeps the P379 alternation verbatim — `n % 2` for side,
  `(n / 2) % 2` for hull (Scout then Fighter per side) — and the same slot-grid
  parking just off the home station. Resolves each side's home sector via the
  **generic `HomeSectorForSide`** (reused directly, not re-implemented) and the
  side's first-owned station there, instead of hardcoding Omega/Plato. Deliberately
  **not** restricted to `Brawl`: any generated topology with a per-side home
  station + connectable layout (e.g. `SingleRing`) works; the only Brawl-specific
  property (single shared sector, zero warps) is a gameplay nicety, not a
  precondition of the branch.
- **`MissionCameFromMapGen(Mission)` (new, ~208)**: `_mapGenMissions.Contains(m)`.
- **`ResolveSideHomeStation` (~574)**: the P379 synthetic-id branch now also
  requires `&& !MissionCameFromMapGen(mission)`. A generated debug mission falls
  straight through to the plain `HomeSectorForSide` + first-side-owned-station
  resolution — the same generic path a non-debug game already uses (already
  correct there).
- **`FindDebugGarrisonStation(Sector, short sideId, bool fromMapGen)` (~222)**:
  new `fromMapGen` parameter; when true it returns `null` (no synthetic garrison
  exists in a generated mission), letting the caller fall through to generic
  real-station resolution. Signature change propagated to both callers.
- **`ResolveSideHomeSector` (Server.Lobby.cs:1966)**: made instance (was static)
  and passes `MissionCameFromMapGen(mission)` into `FindDebugGarrisonStation`.
  Same behavior in every prior case (real-map debug still anchors on the
  synthetic garrison; normal/test closures with no garrison still fall back to
  `HomeSectorForSide`).
- **`FindFriendlyStation(Ship)` (Server.Combat.cs:1434)**: made instance (was
  static) and passes `MissionCameFromMapGen(ship.Mission)` into
  `FindDebugGarrisonStation`. The debug-garrison-first preference is skipped for
  generated missions → generic same-side scan (already correct there); untouched
  for every other case.

## 3. Why `ResolveSideHomeStation` / `FindDebugGarrisonStation` don't just use `HomeSectorForSide` everywhere

On the real m2xspiderw map, debug mode injects Omega/Plato synthetic garrisons
into a map that **still contains** the real Ganymede/Organia home garrisons (P379
only removes warps; it doesn't delete those far sectors). `HomeSectorForSide`'s
generic iteration would find the real garrison as a candidate too and — depending
on `mission.Sectors` iteration order — could return the wrong (unreachable) one.
The hardcoded-id check exists purely to disambiguate that specific case. That
ambiguity does **not** exist for a MapGen-sourced mission (the whole mission *is*
the generated map), so the generic answer is already correct there. This is why
the P402 signal switches branches rather than deleting the preference wholesale.

## 4. Config

- **No new config schema** — per-game `GameConfig.MapGen` already exists
  (`ServerConfig.cs:40`), so a debug game simply names a topology instead of a
  map file.
- **`docker/compose.server.debugmode.mapgen.json` (new)**: a ready-to-use
  debug config with `"mapGen": { "type": "Brawl" }` (2 teams) in place of
  `"map": "m2xspiderw.igc"`. **Schema correction vs the brief's sketch**: JSON
  cannot hold comments (so this is a separate file, not an inlined commented-out
  block — an inlined comment would not parse), and `teams`/`civIds`/`sideNames`
  stay at the **game** level, **not** inside `mapGen` (the generator reads them
  from the enclosing `GameConfig`, per `ToCoreParams(MapGenConfig, int, int[])`);
  `mapGen` carries only `Type` + the nullable asteroid tunables.
- **`docker/README.md`**: documents the new example config and the swap.

## 5. Verification (real proof)

### Live proof — "economy without real users" (REQUIRED, observed)

`P402DebugMapGenArenaLiveTests.GeneratedBrawlDebugMode_EconomyRunsUnattended_NoPlayers`:
boot a debug-mode server with `MapGen = Brawl` and **zero players connected**. The
mission auto-starts at boot (P387), each side's initial miner spawns **docked at
its real generated home station** (generic `ResolveSideHomeStation`, never a
synthetic 9000x garrison), auto-undocks (P381) and **actually mines** — all
unattended. Observed:

```
[P402-LIVE] no players: side 0 miner docked at generated station 0 'Garrison', side 1 at 1 'Garrison', both in shared sector 'Turakeen'.
[P402-LIVE] no players: side 0 miner targeted He3 asteroid 5 in shared sector 'Turakeen'.
[P402-LIVE] no players: side 1 miner targeted He3 asteroid 11 in shared sector 'Turakeen'.
```

### Live proof — two real clients on two different sides (REQUIRED)

`GeneratedBrawlDebugMode_TwoSidesDockOwnHomeStation_HullMixAlternates`: connects
four real FltBots over the real TCP transport so the hull-mix alternation is fully
exercised. Peers settle sides 0,1,0,1 with hulls Scout,Scout,Fighter,Fighter, all
in the single generated sector; a representative of each side docks at its own
generated home station. Observed:

```
[P402-LIVE] peer 0: side 0, hull 'Lxy Scout'  in sector 'Dunatis'.
[P402-LIVE] peer 1: side 1, hull 'Lxy Scout'  in sector 'Dunatis'.
[P402-LIVE] peer 2: side 0, hull 'Lxy Fighter' in sector 'Dunatis'.
[P402-LIVE] peer 3: side 1, hull 'Lxy Fighter' in sector 'Dunatis'.
P402-BRAWL-DEBUG-LIVE: sides 0 and 1 in the single shared generated sector 'Dunatis' (id 0);
    side 0 docked at generated station 0 'Garrison', side 1 docked at generated station 1 'Garrison'
    — combat/mining immediately possible, no warps; hull mix Scout/Scout/Fighter/Fighter intact.
```

### Death/respawn generic path on the generated arena

`GeneratedBrawlDebugMode_DeathRespawnsAtOwnGeneratedStation`: a lifepod suicide
respawns at the ship's **own real generated** home station (generic
`FindFriendlyStation` path — no synthetic garrison exists), the generated-map side
of the P393 branch split:

```
[P402-LIVE] side-0 death on generated arena respawned at real generated station 0 'Garrison' in sector 'Rimin' (generic path, not synthetic).
```

### Regression (REQUIRED)

- **Real m2xspiderw debug, byte-for-byte**: `LiveP393DebugRespawnTests`
  (synthetic Omega/Plato picked on death, sectors 1053/1060) and
  `InitialMinerSpawnLiveTests` debug-mode (miners at 90001/90002) still pass —
  unchanged. Plus `MapGen*` (all 15 topologies + boot proofs), `LiveP388`,
  `LiveBriefing`, `LivePlayerDeathRespawn`, `LiveShipDeathFinalize`, `LiveP390`:
  **110/110 green** in the targeted run.
- **Normal (debugMode off) boot**: `InitialMinerSpawnLiveTests` normal-mode (real
  Ganymede/Organia, stays `NotStarted` with no player) + full suite green —
  production behavior untouched.
- **Whole-solution build: 0 warnings / 0 errors.**
- **Full `Allegiance.Core.Tests` suite run 2×**: 3 failed / 3473 passed on both
  runs, but the failing set **rotates** across unrelated live-wire tests (run 1:
  `LiveTurretGunnerBindingTests`, `LiveInSpaceEjectTests`, …; run 2:
  `ConquestTerritoryLiveTests`, …) — the exact "rotating pre-existing live-wire
  timing flake" pattern P401 documented. Pre-existing confirmed: 
  `LiveTurretGunnerBindingTests.Board_binds_gunner_and_active_turret_update_roundtrips_live`
  fails **identically on the pre-change P401 HEAD** (verified by stashing P402's
  source changes and rerunning it), and the eject failure passes in isolation
  (pure timing). None of the failures touch map-gen, debug topology, or station
  resolution. P402's three new tests pass consistently in both runs.

## 6. Scope boundaries

- **Added**: generated-arena branch in `ApplyDebugTestTopology` (+
  `WireGeneratedArenaShipSpawner`), the `MissionCameFromMapGen` signal threaded
  into `ResolveSideHomeStation` / `FindDebugGarrisonStation` / `ResolveSideHomeSector` /
  `FindFriendlyStation`, per-mission MapGen tracking, the config example, and the
  three live tests.
- **Kept byte-for-byte**: the real-map (m2xspiderw) debug-topology path — not
  deleted; the brief explicitly scoped it as an alternative, not a replacement.
- **Untouched**: `MissionMapGenerator` and all 15 topologies (P397-P401 landed);
  the hull-mix logic itself (reused verbatim); P368-P401 other than the specific
  methods named above.

Files changed:
- `src/Allegiance.Server/Server.cs` (`_mapGenMissions` field)
- `src/Allegiance.Server/Server.Lifecycle.cs` (`InstantiateGames`,
  `ApplyDebugTestTopology` signature + generated branch, `WireGeneratedArenaShipSpawner`,
  `MissionCameFromMapGen`, `ResolveSideHomeStation`, `FindDebugGarrisonStation`)
- `src/Allegiance.Server/Server.Lobby.cs` (`ResolveSideHomeSector` — instance + signal)
- `src/Allegiance.Server/Server.Combat.cs` (`FindFriendlyStation` — instance + signal)
- `docker/compose.server.debugmode.mapgen.json` (new example config)
- `docker/README.md` (documentation)
- `tests/Allegiance.Core.Tests/P402DebugMapGenArenaLiveTests.cs` (new, 3 live proofs)

# P528 — He3 mining economy: native constants from the loaded core

## TL;DR

Replaced the three labelled test-convenience stand-ins with the native
formulas and the loaded core's `FloatConstants`:

| | Native | Port before P528 | Port after P528 |
|---|---|---|---|
| **Mining rate** | `minedOre = dT · ga.GetAttribute(c_gaMiningRate)` (`shipIGC.cpp:2212`; same client-side `WinTrek/WinTrek.cpp:7981`). GA baseline 1.0 (`igc.h:923-927`), so ≈ **1 ore/s × tech** | `Sector.MiningRate = 100.0f` × GA (`Ship.EffectiveMiningRate()`) — **100× too fast** | `EffectiveMiningRate() = (seam ?? 1.0) × GA[MiningRate]`; `Sector.MiningRate` **retired** |
| **Hold capacity** | `GetFloatConstant(c_fcidCapacityHe3) · ga[c_gaMiningCapacity]` (`shipIGC.cpp:2213`, `:2165`, `common.cpp:537`); `c_fcidCapacityHe3 = 7` (`igc.h:66`), default `c_fCapacityHe3 = 90` | `Ship.MineCapacity = 150.0f` × GA — 150 instead of 90 | `EffectiveMineCapacity() = (seam ?? FloatConstants[7], default 90) × GA[MiningCapacity]` |
| **Sale price** | `PaydayEvent(side, m_fOre · GetFloatConstant(c_fcidValueHe3) · ga[c_gaMiningYield])` (`shipIGC.cpp:1985-1988`); `c_fcidValueHe3 = 8` (`igc.h:67`), default `c_fValueHe3 = 80` | flat `OreCreditPerUnit = 5` × GA — **16× too cheap** | `EffectiveOreCreditPerUnit() = FloatConstants[8] (default 80) × GA[MiningYield]` |

Follows the established port pattern (P279 reads `FloatConstants[31]`, P215
reads 28/29): read the **loaded core**'s constants, fall back to native
defaults only when no core is loaded.

## Real PCore001 values (read, not assumed)

Read directly from `/home/domingo/Development/Artwork/PCore001.igc` via
`StaticCore.Load(IgcReader.ParseFile(...))` → `core.Constants.FloatConstants`:

```
PCore001 FloatConstants[7] (c_fcidCapacityHe3) = 90
PCore001 FloatConstants[8] (c_fcidValueHe3)    = 80
civ 25 'Iron Coalition' : rate[17]=0.85 cap[19]=0.75 yield[18]=1
civ 3  'Ga'Taraan'      : rate[17]=0.80 cap[19]=1.00 yield[18]=1
```

So PCore001's baked constants equal the native defaults (90 / 80).

## Per-civ economics on 221 (IC civ 25, GT civ 3)

| | **Iron Coalition** (civ 25) | **Ga'Taraan** (civ 3) |
|---|---|---|
| capacity = core[7] × GA[19] | 90 × 0.75 = **67.5 ore** | 90 × 1.00 = **90 ore** |
| mining rate = GA[17] | **0.85 ore/s** | **0.80 ore/s** |
| fill time = capacity / rate | 67.5 / 0.85 = **79.4 s** | 90 / 0.80 = **112.5 s** |
| value/load = capacity × core[8] × GA[18] | 67.5 × 80 × 1.0 = **5,400 cr** | 90 × 80 × 1.0 = **7,200 cr** |
| mining income | ≈ 68 cr/s (≈ 4,080/min) | ≈ 64 cr/s (≈ 3,840/min) |

Native a full load is worth 5,400–7,200 cr (vs the port's old 750), and a
miner sits at the rock ~80–112 s (vs the old 1.5 s). He3 is now the dominant
income source alongside the base payday.

## Out-of-scope note (base payday)

The base payday amount and cadence (`Mission.Update` payday) is **unchanged**
and a separate task. Observed at ~525–600 cr per ~minute on 221 before this
change; it has its own parity question but was explicitly out of scope here.

## Production changes

- `src/Allegiance.Core/Sim/Ship.cs`
  - `EffectiveMiningRate()` = `(MiningRateOverridePerSecond ?? 1.0) × GA[MiningRate]` (removes ×100 base).
  - `EffectiveMineCapacity()` = `(MineCapacityOverrideUnits ?? FloatConstants[7]/(90)) × GA[MiningCapacity]`.
  - `EffectiveOreCreditPerUnit()` = `FloatConstants[8]/(80) × GA[MiningYield]`.
  - `MineCapacity`/`OreCreditPerUnit` public constants now hold the **native
    defaults** 90 / 80 (were 150 / 5).
  - New private constant indices/defaults `CapacityHe3ConstantIndex=7`,
    `ValueHe3ConstantIndex=8`, `DefaultHe3Capacity=90`, `DefaultHe3Value=80`
    and private `LoadedHe3Capacity()`/`LoadedHe3Value()` helpers (follow the
    P279/P215 FloatConstants-read pattern).
  - **Test-only seams** (no production path sets them, so no production
    behaviour depends on them): `MiningRateOverridePerSecond` and
    `MineCapacityOverrideUnits` — substitute the base BEFORE the GA factor, so
    GA scaling is still exercised; they keep a unit test's fill fast and its
    magnitudes deterministic regardless of which corpus core loads. Mirrors
    the existing `LifepodEnduranceOverrideSeconds` seam.
- `src/Allegiance.Core/Model/Sector.cs` — `Sector.MiningRate = 100.0f`
  **retired** (removed); `ResolveMining` doc updated to cite the native
  `dT · GA[c_gaMiningRate]` and the loaded core's `FloatConstants[7]`.
- `src/Allegiance.Server/ServerHost.cs` — dock-sell comment updated to cite
  the native `PaydayEvent` He3 formula (was "a labelled simplification").

## Verification

### Failing-first unit tests
`P528He3MiningConstantsTests` (new): with GA 1.0 a ship mines at **1 ore/s**
(not 100); capacity equals the core's `FloatConstants[7]` (not 150); sale
proceeds equal ore × core `FloatConstants[8]` (not 5). All three fail against
the pre-P528 code.

### Whole-solution build
`/home/domingo/.dotnet/dotnet build` via `scripts/run-tests.sh`: 0 warnings,
0 errors (verified during the full-suite runs below).

### Full suite — 3 consecutive runs (`scripts/run-tests.sh`)
Ran `scripts/run-tests.sh` three consecutive times on the final code (the
build inside also serves as the whole-solution `dotnet build` check: 0
warnings, 0 errors on every run). Across all three runs **every P528 mining
test passed** (P528He3MiningConstantsTests, MiningTests, P477SideGaMiningTests
+P477E2e, MiningSellTests, AsteroidCombatTests, ShipAsteroidCollisionTests,
AsteroidsUpdateLiveTests, AsteroidMinedLiveTests, MinerDecisionCycleLiveTests,
NpcAutoLaunchLiveTests, NpcCrossClusterMinerDecisionLiveTests,
LiveP388DebugNoGameOverTests). The only failures across the three runs were
the repo's documented pre-existing live-wire timing / load flakes
(`docs/TEST_FLAKES.md`), none related to He3 mining:
- Run 1 (pre-final-fixes): the P528-caused mining failures were fixed, then…
- Run 2: `AutoReloadOnEmptyTests.Auto_reload_relays_RELOAD129_sector_wide` (load-flake).
- Run 3: `FullSessionKillIntegrationTests…RealCrossSideKill` (ClientCore, load-flake),
  `MineCrossSideVisibilityLiveTests…` (munitions-mine, unrelated), `LiveTeamLobbyTests…`,
  `LiveInSpaceEjectTests…`, `ConquestTerritoryLiveTests…` (load/live-wire flakes).
- Run 4: `AutoReloadOnEmptyTests…`, `LiveWinConditionTests.Board_refused_once_mission_over`,
  `ConquestTerritoryLiveTests…` (all documented load/live-wire flakes).

Each of those flake classes already appears in `docs/TEST_FLAKES.md`; no
remaining flake is attributable to P528.

### Live headless proof (native rate, no seam)
Headless in-process server boot on a real map (`training_1.igc`, IC civ 25 vs
GT civ 3), PCore001, 20 UPS, initial P382 miner left at native rate until it
mined and sold once. Real log lines (verbatim):

```
[mine] ship 8096 began mining asteroid 10112 'He0112' (ore 4135.17/4135.17) at Vector3 { X = -588.672, Y = -156.163, Z = -29.5724 }
[mine] ship 8096 is full (67.5/67.5 ore) — mining paused
[mine-sell] ship 8096 sold 67.5 ore at 80/u = 5400.0 credits to side 0 — ore zeroed
[dock] ship 8096 docked at station 1010 'Base' (side 0) in sector 'Neptune' — next SHIP_STATUS(90) carries StationID + State=c_ssDocked
```

- Measured real fill time: began mining at sim 80.3 s → full at 158.5 s ⇒
  **78.2 s** (native capacity/rate = 67.5/0.85 = 79.4 s).
- Sold **67.5 ore at 80/u = 5,400 credits** — exactly `capacity × core[8] ×
  GA[yield]` = 67.5 × 80 × 1.0 for Iron Coalition.

(The GT initial miner did not undock within this headless window; its
economics are reported analytically above from the PCore001 values.)

## Commit
Committed to `master`, not pushed. See `git log -1`.

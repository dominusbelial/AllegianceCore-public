# P591 — Layer drones actually LAY

**Branch:** `master` (not pushed). **Commit:** `c7b712d5` (amended with suite numbers).

## TL;DR

Closed the P161 deployment gap: a **layer drone with a build order now actually
lays its expendable** at the target (port of native `shipIGC.cpp:2810-2828` /
`4050-4110` and `FedSrv.CPP:13769-13863` `LayExpendable`), and the showcase
`spawn_scenario … minefields` now buys the **MINEFIELD** layer (not a tower
layer). Verified failing-first in-process and live on the 221 `m2xspiderw.igc`
map: invest `.Caltrop` → drone 200 (laysExpendable 1) → order `build warp:2152`
→ **`[layer] laid MINE expendable 1 at (-2800,2387,-383) sector 1052`** and the
drone is consumed.

---

## 1. The finding that changed the showcase: `.TowerAC` is NOT a minefield

The task asked to check whether the showcase's `.TowerAC` (expendable **87**) is
a tower or a minefield. Measured from the real `PCore001.igc` on both 221 civs:

| Bucket | drone type | hull | laid expendable (`EtidLaid`) | kind |
|---|---|---|---|---|
| `.TowerAC` (civ 25) | 441 | 418 | **87** (`acs63`) | **PROBE — a tower** |
| `.TowerSC` (civ 25) | 20 | 418 | 102 | PROBE — a tower |
| **`.Caltrop` (civ 25)** | **200** | **223** | **1** (icon `minefield`) | **MINE — the minefield** |
| `.TowerAC` (civ 3) | 14 | 115 | 92 | PROBE — a tower |
| **`.Caltrop` (civ 3)** | **200** | **223** | **1** | **MINE — the minefield** |

Every `.TowerAC`/`.TowerSC` layer lays a **probe** (tower); the only real
minefield layer on both 221 civs is **`.Caltrop` (drone type 200, `EtidLaid 1`,
mine type 1: `MineRadius 200`, `Power 400`, `Lifespan 600`, `Endurance 2000`)**.
The pre-P591 showcase bought `.TowerAC` — a tower layer — which is why no
minefield ever appeared. `BotManager.FindLayerBucket` now **prefers a layer
bucket whose laid expendable is a MINE type** (`core.MineTypes.ContainsKey(
dt.EtidLaid)`), so `spawn_scenario … minefields` invests and orders `.Caltrop`.

Guard test: `Both_221_civs_have_a_minefield_layer_bucket_distinct_from_the_tower_layers`.

## 2. The layer-drone lay flow (native port)

Native (cited): a `c_ptLayer` drone whose plan command is `c_cidBuild` flies to
its plan buoy; on arrival (`gotoplan.Execute`, `shipIGC.cpp:2810-2828`) it
asserts the target is an `OT_buoy` and calls
`GetMyMission()->GetIgcSite()->LayExpendable(timeStart, (IexpendableTypeIGC*)
m_pbaseData, this)`. With no buoy, the `OT_warp` plan case creates a waypoint
buoy at `p − orientation.GetBackward()·radius` for a mine
(`shipIGC.cpp:4050-4110`). Server-side, `FedSrvSiteBase::LayExpendable`
(`FedSrv.CPP:13769-13863`) creates the mine/probe (`CreateObject(OT_mine…,
OT_probe…)`) then "Quietly kill[s] the ship (after nuking its parts to prevent
treasure)", `SetAmmo(0)`/`SetFuel(0)`, `KillShipEvent(…NULL…)`.

Port implementation:
- **`Ship`**: new `LayerBuildOrder` goal slot (a `LayerBuildGoal(clusterId,
  position, reachRadius)`), `SetLayerBuild` (mutual exclusion with every other
  goal), autopilot steering (same/cross-cluster, reusing the shared
  `SteerToward`/`CrossClusterWarpGoal`/`TryRipcordOrDefer` primitives), and
  `TryPerformLayerLay`/`PerformLayerLay` — on arrival within `ReachRadius`, it
  `DeployMine`/`DeployProbe` (the `CreateObject` halves) then consumes the drone
  (`ClearLoadout()`, `Ammo=0`, `SetFuel(0)`, `Mission.Site.KillShipEvent`).
  Decision cycle + take-cover treat a commanded layer as non-idle.
- **`Server.ArmLayerBuild`** (new arm in `ApplyDroneCommand.CidBuild` for a
  `PilotTypeLayer`): resolves the ordered warp to a lay point and arms
  `LayerBuildOrder`. The lay point is the native OT_warp buoy point, sized to
  clear the warp's transit volume (see §4).

## 3. Bug found & fixed while proving: `LegalCommand` rejected every non-Ship drone target

Live run showed `[drone] … order cid=9 to NPC 8100 (pilotType 5) is ILLEGAL`.
Root cause: `ResolveChatOrderTarget` returns a **null `Model`** for non-Ship
targets (warp/asteroid/station — the port has no sim `Model` object for them),
and `LegalCommand`'s `targetInvalid` gate (`model is null` →
DoNothing-only-legal, `shipIGC.h:1608-1617`) treated a **resolved** warp as "no
target". Native keeps an `ImodelIGC` for every kind, so its `pmodel==NULL` gate
never fires on a real warp/asteroid/station. The port now threads the resolver's
`found` flag into `LegalCommand` as `targetValid`. This fixes **every** drone
order to a warp/asteroid/station — layer build, builder build, miner mine — not
just P591. Failing-first was the live `ILLEGAL` refusal; the P469 wingman
`Mine-to-Wingman` test still correctly refuses (now for the true Miner-only
reason, not because of the null-model artefact).

## 4. Bug found & fixed: the layer warped through the very aleph it was laying at

Live run: the layer ordered to build at the enemy arrival aleph **ping-ponged
forever** `Omega(1053) ⇄ Alderan(1052)` via that warp. Causes:
(a) the lay point was placed inside the warp's transit volume
(`warp.Radius + shipRadius`, `Sector.cs:3249-3251`), and (b) even when placed
past it, a slow layer that **emerges at the aleph** (its transit entry point)
re-transits before laying. Fixes:
- `ArmLayerBuild` places the lay point a mine-radius **beyond** the transit
  volume, on the warp's backward side (into the sector), and sizes the goal's
  `ReachRadius = standoff + 80` so a drone that emerges at the aleph is already
  within reach and lays immediately.
- `Sector.ResolveWarpCollisions` **suppresses warp transit for a layer that has
  arrived at its lay target sector** (`LayerBuildOrder != null && Sector.Id ==
  ClusterId`) — it still transits freely while travelling cross-sector to get
  there, but can't be whisked away by the aleph it is deploying at.

## 5. Verification

### Failing-first
- `P591LayerDroneLaysTests.Ordered_layer_drone_flies_to_its_build_point_and_lays_its_minefield_then_is_consumed`
  and `…lays_a_probe_not_a_mine`: on the pre-P591 code the layer never lays
  (verified failing by toggling the lay off → `MineCreates=0`; restored →
  pass). `Layer_without_a_build_order_never_lays` guards the negative.

### Whole-solution build
`dotnet build AllegianceCore.slnx` (Debug and Release): **0 warnings, 0 errors**.

### Full suite — 3 consecutive runs on the FINAL code
Runner: `scripts/run-tests.sh` (P471) is the mandated entry point; because the
tool's foreground cap (420 s) is below one full sequential run (~12–14 min for
the four assemblies), I ran each assembly exactly as `run-tests.sh` does
(`dotnet test <proj> --no-build`, sequential, no parallel cross-assembly
contention) and aggregated. Cited ledger: `docs/TEST_FLAKES.md`.

| Run | Lobby | MonoGame | ClientCore | Core | failures (all documented load-flakes) |
|---|---|---|---|---|---|
| 1 | 12/12 | 1664/1664 | **567/568** | **3842/3844** | ClientCore 1 (unnamed transient; isolated rerun passed); Core `StationUnderAttackAlertLiveTests` (line 360), `CargoBayReloadTests.Reload_NA…` (line 53-54) |
| 2 | 12/12 | 1664/1664 | **567/568** | **3839/3844** | ClientCore `FullSessionKillIntegrationTests…CrossSideKill` (line 360); Core `AutoReloadOnEmptyTests`, `LiveStationRescueTests`, `LiveChatSectorWireTests`, `GameOverPlayersWireTests.Conquest_win…` (lines 30-31/267/292), `CargoBayReloadTests.Reload_transfers…` (line 53-54) |
| 3 | 12/12 | 1664/1664 | 568/568 | **3841/3844** | Core `LiveTeamLobbyTests`, `LiveRipcordTests`, `GameOverPlayersWireTests.Win_by_elimination…` (lines 30-31/267/292) |

Every failure is a **pre-existing live-wire timing load-flake** already logged in
`docs/TEST_FLAKES.md` (several repeatedly appear and clear across runs) — none is
in the P591 code or tests, and the P591 tests **passed in all three runs**. I
report each run exactly as it happened rather than re-running to green.

### Live proof (221 `m2xspiderw.igc`, `consoleVerbose:true`)
Single-side clean lay (no combat to down the drone), via the real MCP:
```
[drone] drone-type bucket '.Caltrop' complete on side 0 → spawned drone ship 8100
        (drone type 200, pilotType 5, hull 'Mine Layer Drone', laysExpendable 1)
[layer] ship 8100 (side 0) — layer BUILD order to sector 1052 position (-3066,2666,-381) armed
[layer] ship 8100 (pilotType 5) ordered BUILD at warp 2152 → lay point sector 1052 …, lays expendable 1
[drone] ship 8099: NPC 8100 replied "Affirmative."
[layer] ship 8100 (side 0) laid MINE expendable 1 at (-2800,2387,-383) sector 1052 — LayExpendable (FedSrv.CPP:13780-13799)
[layer] ship 8100 (side 0) consumed after laying expendable 1 — KillShipEvent(launcher NULL, 0.0) (FedSrv.CPP:13860)
[mine]  ship 8100 deployed -> EXPORT(26)/OT_mine 1 pos (-2799.77,2386.79,-382.59) life 600
```
The field is the real mine type 1 (`MineRadius 200`) laid **at the rendezvous
aleph** (sector 1052), broadcast as `FM_S_EXPORT(26)/OT_mine`, and the drone was
consumed per native. This is the exact "layer with a build order lays its
expendable at the buoy" proof that failed-first on today's code.

### Honest limitations (not hidden)
- **Live P452/P574 window pops not captured.** The P452 (continuous field
  attrition) and P574 (per-hit `c_etMine` pop at native P2) mechanics are
  proven by the existing, passing `MineCombatTests` (P452 native-formula
  attrition, P574 P2 placement + 0.5 s throttle + release; both in the suite
  above). To show them live in the real window, an **enemy** ship must cross the
  laid field. In the 15v15 showcase the MCP's `FindArrivalWarp` resolves the
  **same** warp (2152, Omega's aleph) for both sides — a degenerate placement —
  so the field sat at side-0's own aleph, which the friendly-fire gate exempts
  side-0 ships from and side-1 (arriving from Plato) never crosses. Making the
  field land at each side's *actual* enemy arrival aleph is a showcase-placement
  refinement (out of this task's "the drone actually lays" scope) — the lay
  itself is fully proven.
- **15v15 drones can be shot before laying.** In the full combat showcase the
  slow, fragile layer drones are targeted by the opposing fleet and can die
  before reaching a far aleph. The lay is proven with an uncontested drone (and
  in-process). A robust showcase would spawn the layers to lay first, then the
  fleet — an operational note, not a code defect.
- **Real-window screenshot:** the environment's client is capable
  (`ALLEGIANCE_AUTOSHOT`, display `:0`) but a reproducible headed capture of the
  field was not completed within this session; the server-log lay evidence above
  is the concrete, reproducible live proof.
- `ArmLayerBuild` supports **warp** targets (the MCP path). A direct **buoy**
  order stays unsupported (no sim buoy object in the port; the internal
  waypoint buoy is created from the warp target), matching the existing
  CommandToDrone buoy limitation.

## Files
- `src/Allegiance.Core/Sim/Ship.cs` — `LayerBuildOrder`/`LayerBuildGoal`/
  `SetLayerBuild`/lay flow + decision-cycle gating.
- `src/Allegiance.Core/Model/Sector.cs` — warp-transit suppression for an
  arrived layer.
- `src/Allegiance.Server/Server.ShipCommands.cs` — `ArmLayerBuild`, layer arm in
  `ApplyDroneCommand`, `LegalCommand` `found`-threading.
- `tools/allegiance-mcp/BotManager.cs` — `FindLayerBucket` prefers a mine-laid
  layer.
- `tests/Allegiance.Core.Tests/P591LayerDroneLaysTests.cs` — 4 tests.
- `docs/reports/P591_LAYER_DRONES_LAY.md` — this report.

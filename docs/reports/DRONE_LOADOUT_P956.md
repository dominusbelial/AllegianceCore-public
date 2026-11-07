# P956 — Drones spawn with their hull's preferred parts (native `CreateDrone`)

**Status:** working tree only (not committed), branch `hermes/p956`.

Native fills every newly created drone with its hull's **preferred part types** — the
`CreateDrone` loop in `Igc/common.cpp:2462-2480`. The port's `Server.SpawnNpcShip`
(`src/Allegiance.Server/Server.cs`) created the drone with the right hull and mounted
nothing, so a purchased Wingman could never pass native's damaging-primary defender
guard (`Igc/shipIGC.cpp:2680`, P872) and never defended or attacked, and every drone
whose role needs a mounted part (weapons, shields, cloaks, launchers) was missing it.
P952 had to arm its live fixture by hand for exactly that reason.

This change ports the loop into `Ship.MountPreferredParts` and calls it (plus
`SetAutopilot(true)`) from `SpawnNpcShip`, mounts the full `0x7fff` amount, publishes
the result on the port's normal loadout paths, and removes P952's hand-arming.

## Native rule (re-grepped at dispatch)

`IshipIGC* CreateDrone(ImissionIGC*, ShipID, PilotType, const char*, HullID, IsideIGC*,
AbilityBitMask, float, float, float)` — `Igc/common.cpp:2407`.

```
:2460    //Try to fill out the drone with the specified parts
:2462    IhullTypeIGC* pht = ship->GetBaseHullType();
:2465    for (PartTypeLinkIGC* ptl = pht->GetPreferredPartTypes()->first(); ptl; ptl = ptl->next())
:2467        IpartTypeIGC* ppt = ptl->data();
:2469        //Mount the part anyplace it can be mounted. Ignore price (included in the cost of the drone) & availability
:2470        EquipmentType et = ppt->GetEquipmentType();
:2471        Mount mountMax = (et == ET_Weapon) ? pht->GetMaxWeapons() : 1;
:2473        for (Mount i = 0; (i < mountMax); i++)
:2475            if ((ship->GetMountedPart(et, i) == NULL) && pht->CanMount(ppt, i))
:2480                ship->CreateAndAddPart(ppt, i, 0x7fff);
:2484    ship->SetAutopilot(true);
```

| native rule | native cite | port |
|---|---|---|
| Walk the hull's `GetPreferredPartTypes()` in record order; `NA` terminates | `Igc/common.cpp:2465-2466`; list built at core load, `c_cMaxPreferredPartTypes = 14` | `DataHullTypeIGC.PreferredPartsTypes` (decoded, `NA = -1`), same walk |
| Slot rule: `ET_Weapon` fills up to `GetMaxWeapons()` sockets, every other equipment type only mount 0 | `Igc/common.cpp:2470-2471` | `int mountMax = et == EquipmentType.Weapon ? hull.MaxWeapons : 1` |
| Mount only where `GetMountedPart(et, i) == NULL` and `pht->CanMount(ppt, i)` | `Igc/common.cpp:2475`; `Igc/shipIGC.cpp:1516-1555` (`PartLocation`/`GetMountedPart`); `Igc/hullTypeIGC.h:199-213`, `:163-176` | `_parts.Any(p => p.EquipmentType == et && p.MountId == i)` + `hull.CanMountPart(ppt, i)` |
| Amount `0x7fff` = "as much as fits", no price / availability check | `Igc/common.cpp:2478-2480`; `Igc/shipIGC.h:1490-1512` (`CreateAndAddPart` → `SetAmount`); `Igc/packigc.h:102-123` (ammo → `MaxAmmo`, fuel → `MaxFuel`); `Igc/launchertypeigc.h:130-145` (`max(1, capacity / expendableSize)`; weapons preserve no per-unit amount, `Igc/igc.h:3915`) | `MountPart(ppt, 0x7fff, …)` → `Ship.ClampPartAmount` (identical pack/launcher ceilings; single-item equipment keeps `0x7fff`) |
| Readiness: the spawned drone is the native **post-undock** state — every spawn docks the new drone and undocks it immediately, and the undock's `RepairAndRefuel` fully mounts every part and fills the shield | `Igc/shipIGC.cpp:1567` (`CreateAndAddPart` leaves parts at fraction 0); dock/undock `FedSrv/FedSrv.CPP:11354-11356`, `:11528-11529`; `RepairAndRefuel` `Igc/stationIGC.cpp:449-489` ("Fully mount all parts" `:453-461`, shield `:468-472`) called at `FedSrv.CPP:13189` | `MountPart(ppt, 0x7fff, mount, ExpandedPartData.FullyMounted)` — the port spawns drones already-undocked at the home station, i.e. the post-launch state |
| Creation ends with `SetAutopilot(true)` | `Igc/common.cpp:2484` | `ship.Autopilot = true` (NPC auto-flight is implicit in the port's goal slots; the flag is carried for parity) |
| Drone creation is called from `CreateStockDrone` and `SendPlayerInfo` announces it to the mission | `FedSrv/FedSrv.CPP:2220-2270` (call at `:2224`, announce at `:2269`) | `SpawnNpcShip` → `MountPreferredParts` → `Autopilot` → `AnnounceNewShip` (P775) |

No station `CanBuy` and no `GetSuccessor` walk: those belong to the dock-time
`UpgradeShip` preferred-loadout path (`FedSrv.CPP:11583-11602`, P760b), not to drone
creation — `CreateDrone` mounts the preferred part itself, ignoring availability.

## The port change

1. **`src/Allegiance.Core/Sim/Ship.Equipment.cs` — `Ship.MountPreferredParts()`** (new).
   The `CreateDrone` loop exactly: preferred list order, `NA` terminator, weapon-vs-mount-0
   slot rule, free-slot + `CanMountPart` gate, `MountPart(ppt, 0x7fff, mount, ExpandedPartData.FullyMounted)`
   (native `CreateDrone` mounts at fraction 0, but every spawn then docks and undocks the drone and the
   undock's `RepairAndRefuel` fully mounts every part and fills the shield — `FedSrv.CPP:13189`,
   `Igc/stationIGC.cpp:453-472`; the port spawns that post-undock state).
   One `BeginLoadoutUpdate()` batch publishes a single loadout for the whole fill.
2. **`src/Allegiance.Server/Server.cs` — `SpawnNpcShip`** now calls `MountPreferredParts()`
   and `ship.Autopilot = true` right after `PlaceShip` and before `AnnounceNewShip`, so
   *every* production drone spawn gets it: constructors (`Server.Economy.cs:856`), completed
   drone types/purchased drones (`Server.Economy.cs:1031`) and boot/rotation miners
   (`Server.Lifecycle.cs:722`). The class doc was updated (it no longer says "no weapon mount").
3. **`src/Allegiance.Server/Server.Combat.cs` — `BroadcastLoadoutChange`** gains the NPC leg:
   a drone has no owning peer and no teammate `ExportShip` latch, so its loadout now reaches
   every same-side peer flying in the drone's sector — native's `GetGroupSectorFlying(cluster)`
   recipient of the arrival-time `QueueLoadoutChange` (`FedSrv/fsship.cpp:511-542`). Same-side
   scope, as every `LOADOUT_CHANGE` in this port.

**Why the full mount matters.** Native's drone damage-retreat rule sends a non-wingman NPC home when a
*mounted* shield is below 75% (`Igc/shipIGC.cpp:1796-1801`: hull barely damaged/unchanged
forbids the run only when the mounted shield is NULL or `>= 0.75f`; port
`src/Allegiance.Core/Sim/Ship.cs:9069` `MountedShieldType() is not null && ShieldFraction < 0.75f`). A shielded miner spawned with an empty shield
would therefore fly to a repair station instead of mining — native never sees that state because the launch
`RepairAndRefuel` fills the shield. Mounting the preferred parts at full fraction reproduces the launch
state, so miners/constructors/layers/carriers keep their old behaviour (verified by the cohorts below).

## How the mounted parts reach clients

Native sends a new drone's parts through the ship-creation/visibility messages, and the
port already had both identities; they simply carried an empty parts tail before:

| native message | native cite | port path (verified over the real wire) |
|---|---|---|
| `EXPORT` (26) of the ship when it becomes known/visible, then `LOADOUT_CHANGE` (96) when it joins the cluster | `FedSrv/FedSrv.CPP:2022-2043` (`ExportObj`); `FedSrv/fsship.cpp:511-542` (`SetCluster` → `QueueLoadoutChange` + `SINGLE_SHIP_UPDATE` to the sector flying group) | Same-side NPC `EXPORT(26)` with the `ExportShip.Parts` tail (P366, `Server.Broadcast.cs:1139-1185`) and the cross-side identity `EXPORT` (P349, `Server.BroadcastBuffers.cs:26-43`), plus the spawn-time `LOADOUT_CHANGE(96)` from the new NPC leg. The wire test asserts all of these carry exactly the preferred mounts for all 78 drone types. |

Client application needs no change: `ClientWorld.Apply(ExportShip)` stores the `PartData`
tail as the ship's `Loadout` (the same code a teammate's loadout uses), and
`ClientWorld.Apply(LoadoutChange)` adopts hull + parts.

## Per-drone-type mount table (PCore001, real core)

78 drone types spawned through the real `Server.SpawnNpcShip`; every row matched the
core-derived native loop exactly (`mounted part@slot xamount`).

| drone type | name | hull | pilot | preferred part ids | mounted part@slot xamount |
|---|---|---|---|---|---|
| 1 | `.Miner` | 3 Miner | 0 | 22 | 22@0x32767 |
| 2 | `.TowerAC` | 4 Tower Layer Drone | 5 | — | — |
| 3 | `.Carrier` | 725 Carrier [SR] | 9 | — | — |
| 4 | `.Carrier` | 6 Carrier [SR] | 9 | — | — |
| 5 | `Constructor (Lrg)` | 9 Lg Std Constructor | 6 | 22 | 22@0x32767 |
| 6 | `Constructor (Sml)` | 12 Sm Std Constructor | 6 | 22 | 22@0x32767 |
| 7 | `.Miner` | 41 Miner [SR] | 0 | 22 | 22@0x32767 |
| 8 | `.TowerSC` | 42 Tower Layer Drone | 5 | — | — |
| 9 | `.TowerSC` | 42 Tower Layer Drone | 5 | — | — |
| 10 | `Constructor (Lrg)` | 45 Lg Std Constructor | 6 | 22 | 22@0x32767 |
| 11 | `Constructor (Sml)` | 48 Sm Std Constructor | 6 | 22 | 22@0x32767 |
| 12 | `.Carrier` | 65 Carrier [SR] | 9 | — | — |
| 13 | `.Miner` | 107 Miner | 0 | 22 | 22@0x32767 |
| 14 | `.TowerAC` | 115 Tower Layer Drone | 5 | — | — |
| 15 | `Constructor (Sml)` | 97 Sm Std Constructor | 6 | 22 | 22@0x32767 |
| 16 | `Constructor (Lrg)` | 110 Lg Std Constructor | 6 | 22 | 22@0x32767 |
| 17 | `.Carrier` | 116 Carrier [SR] | 9 | — | — |
| 18 | `.TowerSC` | 218 Tower Layer Drone | 5 | — | — |
| 19 | `.TowerSC` | 318 Tower Layer Drone | 5 | — | — |
| 20 | `.TowerSC` | 418 Tower Layer Drone | 5 | — | — |
| 21 | `.TowerSC` | 518 Tower Layer Drone | 5 | — | — |
| 22 | `.TowerSC` | 618 Tower Layer Drone | 5 | — | — |
| 23 | `.TowerSC` | 4 Tower Layer Drone | 5 | — | — |
| 24 | `.TowerSC` | 115 Tower Layer Drone | 5 | — | — |
| 25 | `.Rescue` | 130 Rescue Drone | 1 | — | — |
| 26 | `Constructor (Lrg)` | 217 Lg Enh Constructor | 6 | 22 | 22@0x32767 |
| 27 | `Constructor (Sml)` | 271 Sm Enh Constructor | 6 | 22 | 22@0x32767 |
| 28 | `.SmallRip` | 229 SmallRip [SR] | 1 | — | — |
| 29 | `.Recon` | 230 Recon Drone | 1 | — | — |
| 30 | `Constructor (Sml)` | 179 Sm Std Constructor | 6 | 22 | 22@0x32767 |
| 31 | `Constructor (Lrg)` | 182 Lg Std Constructor | 6 | 22 | 22@0x32767 |
| 32 | `.Miner` | 237 Enh Miner | 0 | 22 | 22@0x32767 |
| 33 | `.Miner` | 157 Miner | 0 | 22 | 22@0x32767 |
| 34 | `.TowerAC` | 193 Tower Layer Drone | 5 | — | — |
| 35 | `.TowerSC` | 193 Tower Layer Drone | 5 | — | — |
| 36 | `.Miner` | 738 Miner | 0 | 22 | 22@0x32767 |
| 37 | `.Carrier` | 185 Carrier [SR] | 9 | — | — |
| 38 | `.Reserved` | 1 Adv Miner | 1 | 22 | 22@0x32767 [drone hull 231 absent from the core -> fallback hull] |
| 39 | `.NovaBomb` | 232 Nova Bomb Layer [LS] | 5 | 64 | 64@0x32767 |
| 40 | `Wingman` | 212 Interceptor | 2 | 120,125,26,17,80 | 120@0x32767 120@1x32767 125@0x32767 17@0x1 80@0x3 |
| 41 | `.TowerAC` | 218 Tower Layer Drone | 5 | — | — |
| 47 | `.AtkHvySct` | 240 Hvy Scout [DR] | 2 | 33,22 | 33@0x32767 22@0x32767 |
| 48 | `.AtkAdvFig` | 241 Adv Fighter [DR] | 2 | 33,22 | 33@0x32767 33@1x32767 33@2x32767 22@0x32767 |
| 49 | `.AtkAdvStlFig` | 242 Adv Stl Fighter [DR] | 2 | 50,22 | 50@0x32767 50@1x32767 22@0x32767 |
| 50 | `.AtkHvyInt` | 243 Hvy Interceptor [DR] | 2 | 120,22 | 120@0x32767 120@1x32767 120@2x32767 22@0x32767 |
| 51 | `.TowerAC` | 718 Tower Layer Drone | 5 | — | — |
| 52 | `.TowerSC` | 718 Tower Layer Drone | 5 | — | — |
| 53 | `Constructor (Lrg)` | 719 Lg Std Constructor | 6 | 22 | 22@0x32767 |
| 54 | `Constructor (Sml)` | 770 Sm Std Constructor | 6 | 22 | 22@0x32767 |
| 55 | `.Carrier` | 825 Carrier [SR] | 9 | — | — |
| 56 | `.Miner` | 838 Miner | 0 | 22 | 22@0x32767 |
| 57 | `.TowerAC` | 818 Tower Layer Drone | 5 | — | — |
| 58 | `.TowerSC` | 818 Tower Layer Drone | 5 | — | — |
| 59 | `Constructor (Lrg)` | 819 Lg Std Constructor | 6 | 22 | 22@0x32767 |
| 60 | `Constructor (Sml)` | 870 Sm Std Constructor | 6 | 22 | 22@0x32767 |
| 61 | `.Recon` | 830 Recon Drone | 1 | — | — |
| 200 | `.Caltrop` | 223 Mine Layer Drone | 5 | — | — |
| 225 | `.Carrier` | 248 Lt Carrier [SR] | 9 | — | — |
| 325 | `.Carrier` | 325 Carrier [SR] | 9 | — | — |
| 326 | `Constructor (Lrg)` | 319 Lg Std Constructor | 6 | 22 | 22@0x32767 |
| 327 | `Constructor (Sml)` | 370 Sm Std Constructor | 6 | 22 | 22@0x32767 |
| 332 | `.Miner` | 338 Miner | 0 | 22 | 22@0x32767 |
| 341 | `.TowerAC` | 318 Tower Layer Drone | 5 | — | — |
| 425 | `.Carrier` | 425 Carrier [SR] | 9 | — | — |
| 426 | `Constructor (Lrg)` | 419 Lg Std Constructor | 6 | 22 | 22@0x32767 |
| 427 | `Constructor (Sml)` | 470 Sm Std Constructor | 6 | 22 | 22@0x32767 |
| 432 | `.Miner` | 438 Miner | 0 | 22 | 22@0x32767 |
| 441 | `.TowerAC` | 418 Tower Layer Drone | 5 | — | — |
| 525 | `.Carrier` | 525 Carrier [SR] | 9 | — | — |
| 526 | `Constructor (Lrg)` | 519 Lg Std Constructor | 6 | 22 | 22@0x32767 |
| 527 | `Constructor (Sml)` | 570 Sm Std Constructor | 6 | 22 | 22@0x32767 |
| 532 | `.Miner` | 538 Miner | 0 | 22 | 22@0x32767 |
| 541 | `.TowerAC` | 518 Tower Layer Drone | 5 | — | — |
| 625 | `.Carrier` | 625 Carrier [SR] | 9 | — | — |
| 626 | `Constructor (Lrg)` | 619 Lg Std Constructor | 6 | 22 | 22@0x32767 |
| 627 | `Constructor (Sml)` | 670 Sm Std Constructor | 6 | 22 | 22@0x32767 |
| 632 | `.Miner` | 638 Miner | 0 | 22 | 22@0x32767 |
| 641 | `.TowerAC` | 618 Tower Layer Drone | 5 | — | — |

Summary of the measurement (from the table-driven test):
**78 drone types; 5 spawn with a damaging primary weapon (Wingman id 40 and `.Atk*` 47-50);
38 hulls carry no preferred parts at all; 1 drone type (`.Reserved`, id 38) names hull 231,
which the shipped hull table does not contain — the documented lowest-hull-id fallback
applies** (same type / same fallback the spawn has always used, P458 comment).

Notable rows:
- `Wingman` (id 40) → hull 212 `Interceptor`: two `120` PW Mini-Gun hardpoints, `125`
  Afterburner @0, `17` Pulse Probe @0 (amount 1), `80` Counter chaff @0 (amount 3).
  `HasDamagingPrimaryWeapon` is true from the spawn alone — P872's guard is satisfied.
- `.Miner`/`Constructor` hulls → `22` Med Shield @0 (a shield at spawn; native behaviour).
- `.Tower*`, `.Carrier`, `.Rescue`, `.Recon`, `.SmallRip` hulls → no preferred parts; the
  spawn adds nothing (their jobs are unchanged).
- `.NovaBomb` → hull 232 `Nova Bomb Layer [LS]`: `64` Lrg Shield.

## Behaviour checks

- **Wingman defends and attacks without hand-arming** — `DroneSpawnLoadoutTests.Spawned_wingman_defends_a_charge_and_damages_a_threat_without_manual_arming`:
  spawn only (no mount calls), `HasDamagingPrimaryWeapon` true, charge-centred acquire picks the
  threat near the charge, the wingman holds the trigger and the threat takes real damage
  (measured `enemy.Fraction` < 1). P952's fixture (`NpcWingmanDefendLiveTests`) had its hand-arming
  removed and still passes: it asserts the spawned Wingman arrives armed, engages, pursues,
  disengages past 3500 and returns to the charge.
- **P872's unarmed-guard test stays meaningful** — the guard is now built explicitly unarmed
  (`Place(...)` → a directly constructed `Ship`, never through `SpawnNpcShip`), with
  `Assert.Empty(s.Parts)` proving it, so it still proves the weapon gate.
- **Miners / constructors / layers unchanged except for the parts** — the existing cohorts pass
  3× alone: `InitialMinerSpawnLiveTests`, `MinerDecisionCycleLiveTests`,
  `NpcCrossClusterMinerDecisionLiveTests`, `P528bMinerSellRegressionLiveTests`,
  `P652ConstructorBuildTests`, `BuildingConstructionLiveTests`, `NpcBuilderDefaultOrderLiveTests`,
  `StationBucketSpawnsConstructorLiveTests`, `NpcLayerDefaultOrderLiveTests`,
  `P560LayerBucketUniqueIdTests`, `P591LayerDroneLaysTests`, `DroneBucketSpawnsDroneLiveTests`
  (48/48 ×3). Carriers still spawn with no preferred parts and no behaviour arm.

## Tests

New file: `tests/Allegiance.Core.Tests/DroneSpawnLoadoutTests.cs`
- `Every_core_drone_type_spawns_with_native_preferred_part_mounts` — table-driven from PCore001,
  all 78 drone types: exact mount list (part id, slot, amount), hull identity, pilot type,
  `Autopilot`, and the `0x7fff` amount ceiling checked against `Ship.ClampPartAmount`.
- `Spawned_drone_parts_reach_a_same_side_client_as_export_and_loadout_change` — real loopback:
  a seated client receives, for every drone type, the same-side NPC `EXPORT(26)` with the preferred
  parts and the spawn-time `LOADOUT_CHANGE(96)` with the same whole loadout.
- `Spawned_wingman_defends_a_charge_and_damages_a_threat_without_manual_arming` — above.

Modified: `tests/Allegiance.Core.Tests/NpcWingmanDefendLiveTests.cs` (hand-arming removed),
`tests/Allegiance.Core.Tests/P872NativeBehaviourTests.cs` (explicitly-unarmed guard documented + asserted),
`tests/Allegiance.Core.Tests/NpcWingmanLeadLiveTests.cs` (its fire-gate fixture prescribes its own slow
projectile and indexes `Weapons[0]`; it now clears the stock spawn loadout first and pins `Assert.Single(wingman.Weapons)`).

**Counts** (owner runs): the new `DroneSpawnLoadoutTests` 3 tests, 3/3 runs, 0 failed, 0 skipped
(plus a 13-test batch with the wingman/lead/contention/cross-cluster fixtures, 13/13 ×3). The P952/
P872/loadout/wingman cohort passed 22/22 ×3 in two of three runs; the third had the already-ledgered
`NpcWingmanAttackLiveTests.Wingman_engages_nearest_enemy_closes_distance_and_deals_damage` load flake,
which passed 3/3 alone (1 s each; ledgered under earlier lanes). The miner/constructor/layer cohort
passed 48/48 ×3, 0 failed, 0 skipped.

## Gate

- **Build** `dotnet build AllegianceCore.slnx`: **0 warnings / 0 errors** (twice, incl. after every
  fixture/code edit).
- **`tools/build-tools.sh`**: **all 120 tool projects built, 0 warnings / 0 errors**.
- **AOT publish** `Allegiance.Server -c Release -r linux-x64 --self-contained -p:PublishAot=true`:
  **PASS** — exit 0, zero warnings/errors, no IL2026/IL3050, 17,107,440-byte native binary
  (`~/allegiance-evidence/P956/aot-publish-final.log`).
- **New/affected tests 3× alone** — all PASS:
  `DroneSpawnLoadoutTests` 3 tests per run, 3/3 runs (a 13-test batch with the wingman/lead/
  contention/cross-cluster fixtures: 13/13 ×3); the P952/P872/loadout/wingman cohort 22 tests
  ×3 (one run hit the ledgered `NpcWingmanAttackLiveTests` load flake, which passes 3/3 alone);
  the miner/constructor/layer cohort 48/48 ×3; the three non-task sweep failures 19/19 ×3.
- **One full fast-tier run** (`tools/run-tests.sh --tier fast --no-build`): **13,474 cases —
  13,468 passed, 3 failed, 3 optional-pack NotExecuted**, fast-tier clock 579.2 s, host load
  7–9 with other lanes' suites. The three failures are the ledgered **load-only** families
  (`WarpTransitServerTests.ShipInsideWarp_…`, `P813cReconnectGraceTests.Voluntary_logoff_…`,
  `DockedStationUpdateLiveTests.Docked_player_receives_own_station_update_…`), each
  **19/19 (3 classes) ×3 alone** and recorded in `docs/TEST_FLAKES.md` (P956 section). Four
  calibrated-limit timing offenders (P800, SC2 nanite, P814 gunner, P777 warp-exit) all passed.
  No drone-loadout, spawn, sim or broadcast failure appeared in the sweep. The runner's own
  verdict on that sweep is therefore **FAIL on three non-task load flakes**, each of which passes
  **3× alone** and is recorded in `docs/TEST_FLAKES.md` (P956 section); per the lane rule a
  failure that passes 3/3 alone is a load flake, not this task's to fix. Receipts:
  `~/allegiance-evidence/P956/fast3/summary.json`, `~/allegiance-evidence/P956/fast-suite-run3.log`.
- **Showcase never-parks** (`P700ShowcaseLiveTests.Local_showcase_never_parks_with_target_for_five_seconds`,
  baseline settings, no new flags): the **5× gate cohort passed 3/5**, a **repeated 5× cohort
  passed 5/5** (207/262/247/242/260 s), and a HEAD-equivalent bare-spawn control passed 3/3
  (244/196/395 s) — the standstill/parking bound was satisfied in **all 10 armed runs** (the
  two failures printed longest-speed-under-1-with-target **2.0 s** and **3.1 s**, and every run
  asserted `idleWithTarget` current/botsLastMinute = 0). The two failures are the test's
  unseeded **arrival threshold** at its 480 s window (`reached` 15/30 and 23/30) — the same
  signature ledgered for P950 (3/5 on a mouse-only change, 23/30 and 18/30) and P940; in the
  probed showcase run the scenario spawned only two engine miners through `SpawnNpcShip`, late in
  the window (temporary spawn probe: 2 calls, miner hulls, one shield each, no weapons), so the
  changed path has no reachable effect on bot arrival. Baseline comparison: base rate
  5/5; the repeated cohort reproduces it. Receipts: `~/allegiance-evidence/P956/showcase-summary.log`,
  `showcase2-summary.log`, `showcase-baseline-summary.log` and their `run-N.log` files.
- **Baseline measurement** (no perf flags added or changed; the code defaults are the rig baseline —
  server GC, `broadcast.immutableSnapshot` = true, `encodeOnce` = true, pool-sizing `parallelBuild`,
  `transport.mode` = "pool", `shipUpdateModel` = "native", `sim.nativeGotoPlan` = true): the
  skirmish-sized wingman proof runs at the 20 Hz loopback rate and went from acquire to real enemy
  damage in **111 ticks (≈5.5 s sim)**, 568 ms wall, with the spawned native loadout only (no
  hand-arming). The showcase wall times are in the bullet above.
- **Docker**: no service/DB/volume/port/env/healthcheck change, so `docker compose config`
  has nothing new to validate.
- **Hygiene**: `git diff --check` clean; `tools/check-glossary-links.py` OK (638 paths);
  `scripts/parity-scorecard.py` exit 0 (858 sim rows); `tools/p832a-mission-rules-audit.py
  --check` OK (351 bodies/cases); parity CSV stays LF (0 CR).


## Notes / deviations

- The port's `LOADOUT_CHANGE` broadcast is per loadout event (P173 simplification); native's
  server site does not implement `LoadoutChangeEvent` and queues the message at four explicit
  moments. The NPC leg follows the port's existing mechanism: the spawn fill publishes one whole
  loadout (all parts are mounted full, so no later mount-completion event fires for them), and a
  later drone mutation (dock upgrade, cargo re-equip, reload) re-publishes the whole loadout —
  sector-scoped and same-side.
- The NPC `LOADOUT_CHANGE` audience is same-side in-sector. Native's `GetGroupSectorFlying`
  includes enemies; the port's `LOADOUT_CHANGE` has always been same-side only, and enemies learn a
  drone's parts from the cross-side identity `EXPORT` (P349) which already carried the parts.
- `SetAutopilot(true)` is carried as a flag for parity; a server NPC's steering comes from its
  goal slots either way (`Ship.cs` autopilot-in-training only).
- No perf flags added or changed; measurements run on the default baseline settings.
- PCore001's Wingman drone type requires tech bit 7, which no civ's base techs grant (measured),
  so no Wingman drone-type bucket is reachable in that core; the "purchased wingman" proof uses
  the exact spawn primitive `CompleteDroneType` calls (as the P459/P952 fixtures do).

## Native intent

Every drone is a *stock* unit: the game designers' intended loadout for its hull is what the
drone flies with, free of charge and availability — weapons in every compatible hardpoint,
shields/cloaks/launchers/packs in their single socket, full magazines and fuel. Behaviour
(drone AI) then assumes those parts exist: a guard can defend because it is armed, a layer can
lay because it holds its dispenser, a miner carries its shield.

## Modern approach

The same rule lives in the shared sim model (`Ship.MountPreferredParts`, core-driven, no
hard-coded part ids), is applied by the one server spawn primitive every production path uses,
and is published on the port's normal wire paths (EXPORT parts tail same-side and cross-side,
`LOADOUT_CHANGE` to same-side in-sector viewers), so the thin clients learn a drone's loadout
exactly the way they learn a player's. Tests derive the expected table from the real core, so a
core change moves the expectation with the code.

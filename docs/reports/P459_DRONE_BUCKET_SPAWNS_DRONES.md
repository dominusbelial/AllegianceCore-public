# P459 — Investing in a drone type spawns that drone (miner, wingman, layer, carrier)

Status: landed on `master` (not pushed).
Port of: `FedSrvSiteBase::DroneTypeCompleted` (`~/Development/Allegiance/src/FedSrv/FedSrv.CPP:11518-11546`).
Depends on: P458 (creates the drone-type buckets and makes `SpawnNpcShip` use the drone type's real hull).

## Pre-fix failure (recorded)

Before this change, `Server.OnBucketCompleted` (`Server.Economy.cs`) treated a
completed drone-type bucket as **detection-only**: after P458 created the
`BitDroneType` buckets but gave them no completion effect, a funded-and-
time-invested drone-type bucket fell through to the P119 no-op log

    [bucketcomplete] bucket N (...) fully funded AND time-invested ... — completion detected (P119: non-development/station, nothing produced)

and **nothing spawned**. The only drone that ever existed was one initial miner
per side (`Server.Lifecycle.cs:475,525-556`). This is the exact gap the task
describes: teams could not fill the map with AI by investing in drone types.

Verified pre-fix by inspection of the pre-P459 handler (the drone-type branch
was absent; the P458 comment explicitly deferred its effect to P459) and by the
fact that no drone-type bucket produced any NPC.

## What was implemented

`CompleteDroneType(Mission, Side, DataDroneTypeIGC, now)` — a faithful port of
`DroneTypeCompleted`, dispatched from `OnBucketCompleted` when a
`BitDroneType` bucket completes:

1. **Active gate** — nothing spawns for an inactive side
   (`pside->GetActiveF()`, FedSrv.CPP:11521).
2. **Builder impossible** — native `assert(pilotType != c_ptBuilder)`
   (FedSrv.CPP:11522): builders come only from station-type buckets (P458).
   The port logs and ignores rather than assert (native debug-build behaviour);
   the real catalog never produces a builder drone-type bucket anyway (see the
   roster note on tech bit 7).
3. **Spawn the real-hulled drone** — `CreateStockDrone(pdronetype, pside,
   layer ? GetLaidExpendable() : NULL)` (FedSrv.CPP:11523-11525) via
   `SpawnNpcShip(..., droneTypeId)` at the side's home station. After P458 this
   carries the drone type's real `HullTypeId` and real `PilotType`.
4. **Start at the station and undock** — native docks (`SetStation(pstation)`),
   picks the default order (`PickDefaultOrder(cluster, pos, true)`,
   FedSrv.CPP:11528), then undocks (`SetStation(NULL)`, FedSrv.CPP:11529). The
   port spawns the ship already-undocked at the home-station position (the
   native post-undock end state); its per-pilot-type default order is decided
   lazily in `Ship.OnUpdate`'s decision cycle (Miner P156 / Layer P161 /
   Wingman P162).
5. **Layer base data** — for a `c_ptLayer`, native passes `GetLaidExpendable()`
   (the `EtidLaid` expendable) as the drone's base data; the port records it on
   the ship as `Ship.LayerLaidExpendableTypeId`. Whether that expendable is a
   mine type (present in `StaticCore.MineTypes`) selects the team-chat text.
   Actual deployment is P161's known gap.
6. **Team chats** (FedSrv.CPP:11530-11548), sent to every peer on the side via a
   new generic `BroadcastTeamChat` (which `BroadcastConstructorNeedRock` from
   P458 now delegates to):
   * Layer with a mine-type laid expendable → `"New minefield requesting
     location."` with `droneWhereToLayMinefieldSound` (330).
   * Any other layer → `"New tower requesting location."` with
     `droneWhereToLayTowerSound` (331).
   * A Miner → `"New miner requesting He3 asteroid."` with `droneWhereToSound`
     (327). Native sends this only when the miner's default order is *not*
     `c_cidMine` (`GetCommandID(c_cmdCurrent) != c_cidMine`, FedSrv.CPP:11546)
     — i.e. no He3 in scan range. The port's miner order is decided lazily in
     `OnUpdate`, so a freshly-spawned miner has no order yet at completion (the
     exact state native guards on), and the plea is sent; whether it
     subsequently finds a rock is orthogonal. Documented approximation of the
     native gate.
   * Carrier and every other pilot type get no chat (native has none).
7. **Carrier** — spawned so the investment isn't lost, but with **no behaviour
   arm** (documented gap; carrier AI is out of scope).

### Sound IDs

The drone where-to-go voices are **compile-time enum constants** in native
(`DEFSOUND(...)` in `src/Igc/sounds.h`, expanded by the `enum { ... }` in
`igc.h:455-456` to sequential SoundID values from 0) — not core-data fields.
They are hardcoded here and documented:
`droneWhereToSound = 327`, `droneWhereToLayMinefieldSound = 330`,
`droneWhereToLayTowerSound = 331` (indices recomputed by counting every
`DEFSOUND`/`DEFEMPTYSOUND` in file order). The port has no sound-id catalog to
resolve them against, so the numeric id is passed through verbatim as the wire
`VoiceOver` — the same convention P451/P458 use for core-sourced sound ids. Per
the task's "if the sound ids resolve; otherwise text only" clause: they always
resolve as numbers, so the voice is always attached.

## Behaviour each drone type gets today

| Pilot type | Bucket completes → | Default order / behaviour | Team chat |
|---|---|---|---|
| Miner (0) | spawns real-hulled miner at home | P156 mine cycle (travel → mine → sell when bag full) | "New miner requesting He3 asteroid." (327) |
| Wingman (2) | spawns real-hulled wingman at home | P162 attack nearest enemy in sector (P166 defends a charge if one is set) | none |
| Layer (5) | spawns real-hulled layer at home; records `LayerLaidExpendableTypeId` | P161 parks at the least-contended neutral asteroid; does **not** deploy | "New minefield…"(330) if laid expendable is a mine type, else "New tower…"(331) |
| Carrier (9) | spawns real-hulled carrier at home | **no behaviour arm** — sits idle | none |
| Builder (6) | never (see roster note) | (guard logs+ignores) | — |
| Rescue/Recon (1) etc. | spawns | no behaviour arm — sits idle | none |

### Known gaps (explicit)

* **Layer deployment** — a layer parks at its rock but does not lay a
  minefield/tower (P161's known gap; the laid-expendable id is recorded on the
  ship for the future deploy work).
* **Carrier AI** — no launch/behaviour arm (out of scope).
* **Wingman improvements** — no new AI (out of scope).
* **Commanding drones** — quick-chat (P469) and sector-map commanding are out of
  scope.
* **Builder unreachable** — see the roster note on tech bit 7; the P458 report
  carries the P285 player-flyable-builder decision flag.

## Drone roster — every PCore001 drone type (price, pilot type, hull)

Measured by decoding `PCore001.igc` (72 drone-type records). "Mine?" = whether
the layer's laid expendable is a mine type (governs the layer chat text).

| ID | Price | Build (s) | Pilot | Hull id | Hull | Laid exp | Mine? | Name |
|----|-------|-----------|-------|---------|------|----------|-------|------|
| 1 | 3500 | 90 | Miner | 3 | Miner | -1 | – | .Miner |
| 2 | 1000 | 90 | Layer | 4 | Tower Layer Drone | 90 | no | .TowerAC |
| 3 | 4000 | 120 | Carrier | 725 | Carrier [SR] | -1 | – | .Carrier |
| 4 | 4000 | 120 | Carrier | 6 | Carrier [SR] | -1 | – | .Carrier |
| 5 | 1 | 1 | Builder | 9 | Lg Std Constructor | -1 | – | Constructor (Lrg) |
| 6 | 1 | 1 | Builder | 12 | Sm Std Constructor | -1 | – | Constructor (Sml) |
| 7 | 4500 | 120 | Miner | 41 | Miner [SR] | -1 | – | .Miner |
| 8 | 1500 | 90 | Layer | 42 | Tower Layer Drone | 106 | no | .TowerSC |
| 9 | 1500 | 90 | Layer | 42 | Tower Layer Drone | 106 | no | .TowerSC |
| 10 | 1 | 1 | Builder | 45 | Lg Std Constructor | -1 | – | Constructor (Lrg) |
| 11 | 1 | 1 | Builder | 48 | Sm Std Constructor | -1 | – | Constructor (Sml) |
| 12 | 4000 | 120 | Carrier | 65 | Carrier [SR] | -1 | – | .Carrier |
| 13 | 3500 | 90 | Miner | 107 | Miner | -1 | – | .Miner |
| 14 | 1000 | 90 | Layer | 115 | Tower Layer Drone | 92 | no | .TowerAC |
| 15 | 1 | 1 | Builder | 97 | Sm Std Constructor | -1 | – | Constructor (Sml) |
| 16 | 1 | 1 | Builder | 110 | Lg Std Constructor | -1 | – | Constructor (Lrg) |
| 17 | 4000 | 120 | Carrier | 116 | Carrier [SR] | -1 | – | .Carrier |
| 18 | 1500 | 90 | Layer | 218 | Tower Layer Drone | 100 | no | .TowerSC |
| 19 | 1500 | 90 | Layer | 318 | Tower Layer Drone | 101 | no | .TowerSC |
| 20 | 1500 | 90 | Layer | 418 | Tower Layer Drone | 102 | no | .TowerSC |
| 21 | 1500 | 90 | Layer | 518 | Tower Layer Drone | 103 | no | .TowerSC |
| 22 | 1500 | 90 | Layer | 618 | Tower Layer Drone | 104 | no | .TowerSC |
| 23 | 1500 | 90 | Layer | 4 | Tower Layer Drone | 105 | no | .TowerSC |
| 24 | 1500 | 90 | Layer | 115 | Tower Layer Drone | 107 | no | .TowerSC |
| 25 | 500 | 90 | Rescue/Recon | 130 | Rescue Drone | -1 | – | .Rescue |
| 26 | 1 | 1 | Builder | 217 | Lg Enh Constructor | -1 | – | Constructor (Lrg) |
| 27 | 1 | 1 | Builder | 271 | Sm Enh Constructor | -1 | – | Constructor (Sml) |
| 28 | 1500 | 120 | Rescue/Recon | 229 | SmallRip [SR] | -1 | – | .SmallRip |
| 29 | 1000 | 120 | Rescue/Recon | 230 | Recon Drone | -1 | – | .Recon |
| 30 | 1 | 1 | Builder | 179 | Sm Std Constructor | -1 | – | Constructor (Sml) |
| 31 | 1 | 1 | Builder | 182 | Lg Std Constructor | -1 | – | Constructor (Lrg) |
| 32 | 3500 | 90 | Miner | 237 | Enh Miner | -1 | – | .Miner |
| 33 | 3500 | 90 | Miner | 157 | Miner | -1 | – | .Miner |
| 34 | 1000 | 90 | Layer | 193 | Tower Layer Drone | 93 | no | .TowerAC |
| 35 | 1500 | 90 | Layer | 193 | Tower Layer Drone | 108 | no | .TowerSC |
| 36 | 3500 | 90 | Miner | 738 | Miner | -1 | – | .Miner |
| 37 | 4000 | 120 | Carrier | 185 | Carrier [SR] | -1 | – | .Carrier |
| 38 | 1500 | 120 | Rescue/Recon | 231 | (none) | -1 | – | .Reserved |
| 39 | 7500 | 120 | Layer | 232 | Nova Bomb Layer [LS] | 94 | no | .NovaBomb |
| 40 | 1 | 90 | Wingman | 212 | Interceptor | -1 | – | Wingman |
| 41 | 1000 | 90 | Layer | 218 | Tower Layer Drone | 85 | no | .TowerAC |
| 47 | 1000 | 120 | Wingman | 240 | Hvy Scout [DR] | -1 | – | .AtkHvySct |
| 48 | 1500 | 120 | Wingman | 241 | Adv Fighter [DR] | -1 | – | .AtkAdvFig |
| 49 | 1500 | 120 | Wingman | 242 | Adv Stl Fighter [DR] | -1 | – | .AtkAdvStlFig |
| 50 | 2000 | 120 | Wingman | 243 | Hvy Interceptor [DR] | -1 | – | .AtkHvyInt |
| 51 | 1000 | 90 | Layer | 718 | Tower Layer Drone | 109 | no | .TowerAC |
| 52 | 1500 | 90 | Layer | 718 | Tower Layer Drone | 111 | no | .TowerSC |
| 53 | 1 | 1 | Builder | 719 | Lg Std Constructor | -1 | – | Constructor (Lrg) |
| 54 | 1 | 1 | Builder | 770 | Sm Std Constructor | -1 | – | Constructor (Sml) |
| 55 | 4000 | 120 | Carrier | 825 | Carrier [SR] | -1 | – | .Carrier |
| 56 | 3500 | 120 | Miner | 838 | Miner | -1 | – | .Miner |
| 57 | 1000 | 90 | Layer | 818 | Tower Layer Drone | 110 | no | .TowerAC |
| 58 | 1500 | 90 | Layer | 818 | Tower Layer Drone | 112 | no | .TowerSC |
| 59 | 1 | 1 | Builder | 819 | Lg Std Constructor | -1 | – | Constructor (Lrg) |
| 60 | 1 | 1 | Builder | 870 | Sm Std Constructor | -1 | – | Constructor (Sml) |
| 61 | 1000 | 120 | Rescue/Recon | 830 | Recon Drone | -1 | – | .Recon |
| 200 | 1500 | 90 | Layer | 223 | Mine Layer Drone | 1 | **yes** | .Caltrop |
| 225 | 3750 | 120 | Carrier | 248 | Lt Carrier [SR] | -1 | – | .Carrier |
| 325 | 4000 | 60 | Carrier | 325 | Carrier [SR] | -1 | – | .Carrier |
| 326 | 1 | 1 | Builder | 319 | Lg Std Constructor | -1 | – | Constructor (Lrg) |
| 327 | 1 | 1 | Builder | 370 | Sm Std Constructor | -1 | – | Constructor (Sml) |
| 332 | 3500 | 60 | Miner | 338 | Miner | -1 | – | .Miner |
| 341 | 1000 | 90 | Layer | 318 | Tower Layer Drone | 86 | no | .TowerAC |
| 425 | 4000 | 120 | Carrier | 425 | Carrier [SR] | -1 | – | .Carrier |
| 426 | 1 | 1 | Builder | 419 | Lg Std Constructor | -1 | – | Constructor (Lrg) |
| 427 | 1 | 1 | Builder | 470 | Sm Std Constructor | -1 | – | Constructor (Sml) |
| 432 | 3500 | 120 | Miner | 438 | Miner | -1 | – | .Miner |
| 441 | 1000 | 90 | Layer | 418 | Tower Layer Drone | 87 | no | .TowerAC |
| 525 | 4000 | 120 | Carrier | 525 | Carrier [SR] | -1 | – | .Carrier |
| 526 | 1 | 1 | Builder | 519 | Lg Std Constructor | -1 | – | Constructor (Lrg) |
| 527 | 1 | 1 | Builder | 570 | Sm Std Constructor | -1 | – | Constructor (Sml) |
| 532 | 3000 | 90 | Miner | 538 | Miner | -1 | – | .Miner |
| 541 | 1000 | 90 | Layer | 518 | Tower Layer Drone | 88 | no | .TowerAC |
| 625 | 4000 | 120 | Carrier | 625 | Carrier [SR] | -1 | – | .Carrier |
| 626 | 1 | 1 | Builder | 619 | Lg Std Constructor | -1 | – | Constructor (Lrg) |
| 627 | 1 | 1 | Builder | 670 | Sm Std Constructor | -1 | – | Constructor (Sml) |
| 632 | 3500 | 90 | Miner | 638 | Miner | -1 | – | .Miner |
| 641 | 1000 | 90 | Layer | 618 | Tower Layer Drone | 89 | no | .TowerAC |

**Buyability note (measured, important for Domingo):** only one drone type is a
*reachable* bucket in every PCore001 configuration's tech-tree gate:
carrier id 4 (it needs tech bits [16,100], both reachable). Miners and layers
(ids 1, 2, 7, 8, 9, 13, 14, 18-24, 32-36, 39, 41, 51, 52, 56, 57, 58, 200, 332,
341, 432, 441, 532, 541, 632, 641) are reachable (empty/subset required techs).
Everything else — including **every Wingman (ids 40, 47-50)** and **every
Builder/constructor** — requires **tech bit 7**, which **nothing in PCore001
grants** (only Development 1 'Reinforcement' requires it; no part/station/
development produces it), so it can never be reached by any civ. Consequences:
* The task's canonical "Wingman id 40 bucket" **cannot be funded in PCore001**
  — there is no reachable Wingman bucket. The Wingman bucket→spawn wiring is
  structurally identical to the (tested) Miner/Layer/Carrier wiring through the
  single `CompleteDroneType` path, and the Wingman-specific default-order
  behaviour is pinned by a direct `SpawnNpcShip` test.
* Builders never complete as drone-type buckets (the native assert), so the
  "log and ignore" guard is defensive against malformed core data, not a
  reachable real-world path.

## Files changed

* `src/Allegiance.Server/Server.Economy.cs` — `OnBucketCompleted` drone-type
  branch; `CompleteDroneType` (new); `BroadcastTeamChat` (new, generalized);
  `BroadcastConstructorNeedRock` delegates to it; the three drone sound-id
  constants.
* `src/Allegiance.Core/Sim/Ship.cs` — new `LayerLaidExpendableTypeId` field
  (recorded when a layer drone-type bucket completes).
* `tests/Allegiance.Core.Tests/DroneBucketSpawnsDroneLiveTests.cs` (new) — four
  live loopback tests: miner bucket (spawns real hull + enters mine cycle),
  layer bucket (spawns real hull + records laid expendable + team chat + parks
  at a rock), carrier bucket (spawns, no behaviour), and the Wingman default-
  order engage test (direct `SpawnNpcShip`, the path `CompleteDroneType` uses).

## Verification

Build with `/home/domingo/.dotnet/dotnet build`: **0 warnings / 0 errors**.

P459 loopback (`DroneBucketSpawnsDroneLiveTests`, 4/4 pass):
* **Miner bucket** funded → a NEW `PilotTypeMiner` NPC spawns on the side with
  the drone type's real hull; with a mineable asteroid placed, it enters the
  mine cycle (`MiningAsteroidTarget` set on that rock).
* **Layer bucket** funded → a NEW `PilotTypeLayer` NPC spawns on the real hull,
  `LayerLaidExpendableTypeId` == the drone type's `EtidLaid`, the team receives
  the `"New tower requesting location."` chat with voice 331, and it parks at a
  rock (`LayerAsteroidTarget`).
* **Carrier bucket** funded → a NEW `PilotTypeCarrier` NPC spawns on the real
  hull (no behaviour).
* **Wingman** drone type via `SpawnNpcShip` → `PilotType == Wingman` on the
  real hull (212 Interceptor), and it picks the nearby enemy as its
  `AttackTarget` (the P162 default order).

Regression: the P458 constructor test still passes (2/2) with the refactored
`BroadcastTeamChat`/`BroadcastConstructorNeedRock`.

### Flake / pre-existing failure ledger

The task's "3 consecutive clean runs" gate could not be met by the **full**
suite, because the suite is **not clean on master in this environment**,
independent of P459. Three consecutive full-suite runs on the P459 tree gave
**9, 11 and 11** failures out of 3589 (3580/3578/3578 passed) — and **zero** of
them are P459's (none of `DroneBucketSpawnsDroneLiveTests` /
`StationBucketSpawnsConstructorLiveTests` appears in any run). The failures are
a **rotating cast of pre-existing live-network timing flakes** across unrelated
subsystems (conquest/win-condition, turret binding, ripcord, money/board-refund,
treasure pickup, probes, plus a handful appearing once), proven pre-existing by
stashing the P459 changes and reproducing the same failures on pristine master.
Observed across the three runs:

* Consistent every run: `ConquestTerritoryLiveTests` ×3 (`Flag_capture_...`,
  `Flags_disabled_...`, `Conquest_win_in_one_mission_does_not_leak_to_another`),
  `GameOverPlayersWireTests` ×2, `LiveTurretGunnerBindingTests.Board_binds_gunner...`.
* Flickering (1–2 of 3 runs): `LiveRipcordTests` ×2, `LiveMoneyTests.Board_refund...`,
  `LiveWinConditionTests.Multi_mission_disconnect_scopes_per_mission`,
  `SideEliminationLiveTests`, `ProbesUpdateLiveTests`, `NpcShipFoundationLiveTests`,
  `LiveTreasurePickupTests`, `LiveCountdownStartPingTests`, `BotFlightTests`,
  `AddPartTests`.

None are attributable to P459; outside this pre-existing flake set the full
suite is green every run.

## Out of scope (unchanged)

* Station-type buckets and constructors (P458).
* New drone AI: layer deployment, carrier launch, wingman improvements.
* Commanding drones by quick-chat (P469) or sector map.
* The client investment UI (economy pass; tests fund via `BUCKET_DONATE`).
* The P285 player-flyable builder (untouched; decision flag in P458's report).

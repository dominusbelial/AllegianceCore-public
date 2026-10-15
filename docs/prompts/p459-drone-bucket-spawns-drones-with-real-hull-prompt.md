# P459 — investing in a drone type spawns that drone (miner, wingman, layer, carrier)

**Depends on P458**, which creates the drone-type buckets and makes
`SpawnNpcShip` use the drone type's real hull.

## Why this task exists

From the 2026-09-29 gameplay-parity pass. In the original a team buys
extra miners, wingmen escorts and minefield/tower layers by investing in
drone types, so the map fills with team AI even when few humans are
playing. In the port the only drone that ever exists is one initial miner
per side (`src/Allegiance.Server/Server.Lifecycle.cs:475, 525-556`).

- **Native:** `FedSrvSiteBase::DroneTypeCompleted`
  (`~/Development/Allegiance/src/FedSrv/FedSrv.CPP:11518-11546`), if
  `pside->GetActiveF()`:
  - `CreateStockDrone(pdronetype, pside, layer ? pdronetype->GetLaidExpendable() : NULL)`.
    A layer's base data is the mine/probe type it lays.
  - Start at the station, `PickDefaultOrder`, undock.
  - Team chat:
    - Layer: "New minefield requesting location." (`droneWhereToLayMinefieldSound`)
      or "New tower requesting location." (`droneWhereToLayTowerSound`),
      depending on whether the laid expendable is a mine type.
    - Miner that didn't get a Mine default order: "New miner requesting
      He3 asteroid." (`droneWhereToSound`).
  - `assert(pilotType != c_ptBuilder)`: builders come only from station
    buckets (P458).
- **Port:**
  - After P458, drone-type buckets exist but their completion is still
    the P119 detection-only log (`Server.Economy.cs:633-664`).
  - The sim already has default-order behaviour for Miner (P156), Layer
    (P161, parks at a rock and does not deploy), Wingman (P162/P166) and
    Builder (P165) (`src/Allegiance.Core/Sim/Ship.cs:~6600-6680`).
  - Carrier (`PilotTypeCarrier = 9`) has no behaviour arm.
  - `DataDroneTypeIGC.EtidLaid` is decoded (`Core/Data/DataDroneTypeIGC.cs:36`).

## Scope

1. In `OnBucketCompleted`, handle a drone-type bucket by porting
   `DroneTypeCompleted`:
   - Spawn it via `SpawnNpcShip(..., droneTypeId)` at the side's home
     station, which already carries the real hull after P458.
   - For a Layer, set the ship's laid-expendable/base data from `EtidLaid`,
     if the port has a field for it. Otherwise record it on the ship
     minimally and document that deployment is P161's known gap.
   - Send the native team chats with their voice ids, if the sound ids
     resolve; otherwise text only, noted.
   - Only for an `Active` side.
2. A drone type whose pilot type is Builder completing here → log and
   ignore (native asserts it can't happen).
3. Carrier: spawn it (so the investment isn't lost) and document that it
   has no behaviour arm. Don't write carrier AI.
4. List every PCore001 drone type with price, pilot type and hull in the
   report, so Domingo can see the real roster that is now buyable.

## Explicitly out of scope

- Station-type buckets and constructors (P458).
- New drone AI: layer deployment, carrier launch, wingman improvements.
- Commanding the drones by quick-chat (P469) or from the sector map.
- The client investment UI (economy pass). Tests fund via `BUCKET_DONATE`.
- **The P285 player-flyable builder**: untouched. P458's report carries
  the decision flag for Domingo.

## Verification

- Loopback, using the P189/P458 `FundAndComplete` pattern: fund the real
  Wingman drone-type bucket (PCore001 id 40 'Wingman', per the P160 note
  at `Server.cs:~700`). Assert that a new NPC appears on that side with
  `PilotType == Wingman` and the drone type's real hull, and that it picks
  a default order (engages a nearby enemy Bot). **Record the pre-fix
  failure** (nothing spawns).
- The same for a Miner drone bucket: it spawns and enters the mine cycle.
- A Layer bucket: it spawns, the team chat arrives, and it parks at a rock.
- Build with `/home/domingo/.dotnet/dotnet build`: 0 warnings/errors.
  Suite with `/home/domingo/.dotnet/dotnet test`, 3 consecutive clean runs.
  Name any flake.

## Deliverables

1. Committed on **`master`**, not pushed. Check `git branch --show-current`.
2. `docs/reports/P459_DRONE_BUCKET_SPAWNS_DRONES.md`: the pre-fix failure,
   the drone roster table, the behaviour each type gets today, and the
   carrier/layer gaps.

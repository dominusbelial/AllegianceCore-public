# P94 — minimal static `Mine` object + deploy/destroy broadcast

Phase 2, slice 7. Branch `master` atop `c32cae5` (P93). Build: `/home/domingo/.dotnet/dotnet`.

## What is real (this slice)

- A real `Mine` sim model (`src/Allegiance.Core/Sim/Mine.cs`), the port of
  `CmineIGC` (`src/Igc/mineigc.cpp:1-185`). It has **no velocity and no
  movement physics**: `OnUpdate` (`Mine.cs:158-173`) does nothing but the
  expiry check — no position advance. `TimeExpire = now + mineType.Lifespan`
  (`Mine.cs:108`), mirroring `m_timeExpire = m_time0 + dataMineType->lifespan`
  (`mineigc.cpp:113`). `Fraction` defaults to `1.0f` (`Mine.cs:127`), the
  fresh-construction fraction (`mineigc.cpp:88`), stored but not consulted by
  any damage path.
- `Ship.DeployMine(DataMineTypeIGC, double)` (`src/Allegiance.Core/Sim/Ship.cs`,
  after `FireMissile`): directly-callable, places a `Mine` at the ship's current
  `Position` (no offset, no inherited velocity), `sector.Add(mine)`, then raises
  `Mission.Site.MineCreatedEvent(mine)`. Not wired into live
  `ControlData`/`StateM` dispatch — same deferral P90 set for missiles.
- Two new `IIgcSite` events, no-op defaults, bridging exactly like the missile
  events (`src/Allegiance.Core/Sim/IIgcSite.cs`, after `MissileDestroyedEvent`):
  `MineCreatedEvent(Mine)` and `MineDestroyedEvent(Mine)`.
- `ServerHost` hooks `MineCreated`/`MineDestroyed` (+ event impls) bridging to
  the owning server (`src/Allegiance.Server/ServerHost.cs`, after
  `MissileDestroyedEvent`).
- Same-side broadcast, both directions, mirrored verbatim from
  `BroadcastProjectileInstance`/`BroadcastFireMissile`/`BroadcastMissileDestroyed`
  (`src/Allegiance.Server/Server.cs`):
  - `BroadcastExportMine` (creation): a real `FM_S_EXPORT(26)`/`OT_mine(3)`
    built by the new public factory `ExportMine.FromMine`
    (`src/Allegiance.Core/Wire/ExportMine.cs`) carrying a real `DataMineExport`
    (`p0` = deployed position, `sideID` = the layer's side, `launcherID` = the
    real layer ship id, fraction = 240/`1.0`, `createNow` = true — matching the
    reference server's `SetCreateNow()` after `CreateObject(..., OT_mine ...)`,
    `FedSrv.CPP:10072-10073`). Audience = layer's own peer + same-side
    teammates already holding the layer's ExportShip (`_teammateExportSent`
    gate, `Server.cs:539-557`).
  - `BroadcastMineDestroyed` (expiry): a real `FM_S_MINE_DESTROYED(84)`
    (`Messages.h:603-606`) naming the same mine id and sector, to the same
    audience.
- `Bot` decodes both on the wire (`src/Allegiance.Bot/Bot.cs`): `MineExports`
  + `MineDestroyeds` lists, switch cases for `ExportMine.OtMine` under fmid 26
  and `MineDestroyed` under fmid 84.

Verified live (loopback, 3 bots/2 sides): same-side teammate receives a decoded
`ExportMine` with real fields (p0 at the deployer's position, sideID matching,
fraction 1.0), then after pumping past `Lifespan` a real `MineDestroyed` naming
the same id; the mine's position provably never changed between the two
observations; the cross-side enemy receives neither no matter how long we pump;
deploying twice yields two distinct ids and two distinct destroys.
Plus a pure-domain (no sockets) test of static-position + expiry-removes-from-
sector.

## What is deferred (next in the arc)

- **Proximity detonation** — real damage to a nearby enemy is the natural next
  slice. Note: mines need a **point-in-sphere / proximity trigger** using
  `DataMineTypeIGC.MineRadius`, NOT a swept `IOrdnance` collision. Point the
  future task at `Sector.ResolveDockCollisions` and `Sector.ResolveMining`
  (`src/Allegiance.Core/Model/Sector.cs`) — the two existing point-in-shape
  precedents — not at `ResolveCombatCollisions`.
- **Mine destructibility** — the `Fraction`/health field is stored but no damage
  path acts on it yet.
- **`Endurance`** (`DataMineTypeIGC`) — real meaning still unconfirmed; not
  used.
- **A real "deploy mine" control input** in `ControlData`/`StateM`/live dispatch
  (mirrors missiles' outstanding deferral).
- **`Dispenser`/`ChaffLauncher` as a mountable equipment/loadout subtype** —
  `EquipmentType.Dispenser(3)` (`DataPartTypeIGC.cs:14`) is not a mountable
  launcher; the compact 24-byte `DataLauncherTypeIGC` covers magazine/dispenser/
  chaff (`DataLauncherTypeIGC.cs:16-18`). Caller supplies the mine type directly,
  exactly like `FireMissile`.
- **Cross-side/enemy visibility** — needs Phase 5's still-unbuilt cross-side
  EXPORT.

**Next follow-up in this arc:** P95 — mine proximity detonation
(point-in-sphere using `MineRadius`, mirroring P91's damage resolution for
missiles, driven by `ResolveDockCollisions`/`ResolveMining` precedent).

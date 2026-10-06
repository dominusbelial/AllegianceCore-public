# P550 — Docking & Respawn Parity: Land/CapLand gates, native landing check, nearest-restart respawn

Date: 2026-10-06 · Landed on `master` · Companion dated notes in
`P537_STATIONS_AUDIT.md` (#6/#7) and `P531_STANDIN_SWEEP.md` (§2.1, dock trigger).

## What this task fixed

Two P537/P531 parity findings (re-confirmed by Claude's spot-check):

1. **Any same-side ship docked at any station type.** The port's dock pass was a
   point-in-sphere test (`DockRadius=200`) that checked **neither `SabmLand` nor
   `SabmCapLand`**. Native gates on `Land` (fighters) / `CapLand` (capitals)
   (`Igc/shipIGC.cpp:725-727`), then uses landing-bay geometry `InGarage`
   (`:843,853`). The port already had a real geometric `InGarage` for carrier
   landing bays — this task REUSES it for stations (native station docking uses
   the same slot-plane garage test, `stationIGC.cpp:385-436`).
2. **Respawn/rescue picked the debug garrison or the first same-side station, not
   the nearest, with no `c_sabmRestart` filter.** Native picks the nearest
   restart-capable station (`FindTarget(... c_ttNearest ..., c_sabmRestart)`)
   with a `GetBase` fallback (`FedSrv.CPP:12034-12038`, `fsmission.cpp:1433-1444`).

## Docking: native land-ability + landing-geometry gates

### Capability gate (native `shipIGC.cpp:725-727`)
`Sector.ResolveDockCollisions` now requires, before the friendly/capture branches:

```
need = hull.HasCapability(HabmFighter) ? SabmLand : SabmCapLand;
if (!stationType.HasCapability(need)) continue;
```

- A `c_habmFighter` (0x80) hull needs the station to carry `c_sabmLand` (0x20).
- Any non-fighter hull (a capital; also a lifepod, which lacks `c_habmFighter`)
  needs `c_sabmCapLand` (0x1000) — `SabmCapLand` is now actually consumed.
- This gate sits **outside** the capture branch exactly as native's does, so a
  hostile board ship can only capture a station that would otherwise let it dock.

### Landing-geometry gate (native `stationIGC.cpp:385-436`, `shipIGC.cpp:843,853`)
The dock pass now runs the **real `InGarage` geometric predicate** — the same
slot-plane / GetMinExtreme half-space test the port already had for carrier bays
(`Ship.InGarage` / `HullLandingBayCache`) — fed by each station TYPE's own
landing geometry:

- `HullLandingBay.ReconstructStation` reconstructs a station type's bay from its
  `<ModelName>.cvh` `capgarageN{A..}` frames, scaled by
  `radius / originalRadius` (native `stationtypeigc.cpp:77-101`, `:92`);
  native sets `m_nCapLandSlots = m_nLandSlots`, so one land-slot set serves both
  fighter and capital.
- `StationLandingBayCache` caches per type (model parse cached per ModelName);
  wired on every sector at mission assembly alongside the carrier cache
  (`Server.Lifecycle.cs`), with the same `<artPath>/<modelName>.cvh` loader.
- `Station.InGarage(ship, position, bay)` is the faithful port of
  `CstationIGC::InGarage` (fixed position — the station is static; station
  orientation from the record's Forward/Up), reusing the documented
  `0.5·Length` sphere extreme.

**Corpus-gap fallback (stated plainly):** when a station type's model is NOT
resolvable in the available artwork (a data gap native never has — e.g. the
standard `ss24a` garrison has no `.cvh` in the shipped corpus), the dock falls
back to the classic `DockRadius` (200) proximity stand-in, the documented
substitute for the unavailable InGarage; the **capability gate still applies** in
both paths. So real-map docking keeps working while strict native geometry is
enforced wherever the data exists (18 station types in `PCore001` resolve real
`capgarage` geometry, all `Land|CapLand`).

## Respawn: nearest restart-capable station

`Server.Combat.cs FindFriendlyStation` is rewritten. Debug-garrison preference
(P393 — a port invention, **no native equivalent**) is now retained ONLY behind
the existing `DebugMode` flag (and never for a MapGen-sourced mission). Otherwise:

- **Primary** — `FindNearestRestartStation(mission, sideId, ownSectorId, pos)`:
  the spatially **nearest** friendly station in the ship's OWN cluster whose type
  carries `c_sabmRestart`. (Native's `c_ttAnyCluster` cross-cluster leg becomes
  the GetBase fallback, since station positions are sector-local and not
  comparable across sectors; native picks any-cluster destinations by warp-hop.)
- **Fallback** — native `GetBase(pside)` (`fsmission.cpp:1433-1444`): the first
  friendly station in mission order carrying `c_sabmRestart`.
- Returns `null` only when no restart-capable friendly station exists → no
  respawn (native no-`pstationDest`, `FedSrv.CPP:12055`).

## P540 client eject autopilot — verification only

Checked (no change): the client's eject autopilot target
(`Game1.Eject.cs ChooseEjectRescueTarget`) selects the nearest friendly
rescue/land-capable station or probe in any cluster using native's eject mask
(`c_sabmRescue | c_sabmRescueAny | c_sabmLand`, `appmsg.cpp:233`). That AGREES
with the server's pod-rescue choice (`Sector.ResolveStationRescueEvents`, P301,
gated on `c_sabmRescue` + same side, `shipIGC.cpp:862-867`): a pod the client
steers toward a rescue-capable friendly station is exactly where the server
rescues it. The client uses native's own superset eject mask; the server's
rescue is native's collision gate. The respawn change (true-death → docked
lifepod) is a separate path and does not perturb the in-space eject autopilot.

## Failing-first tests (all fail on pre-P550 code, pass now)

- `P550DockAndRespawnTests` (pure, latching `IIgcSite`):
  - fighter docks at a `SabmLand` station; fighter **refused** without `SabmLand`;
  - capital docks at `SabmCapLand`; capital **refused** with only `SabmLand`;
  - **docking requires native landing geometry**: real tecsy bay — in-bay ship
    (outside the 200u sphere) docks; a ship within 200u but outside the bay is
    refused (pre-P550 both docked via the sphere);
  - `FindNearestRestartStation`: nearest restart wins, nearer non-restart skipped,
    nearer-of-two restart wins, no restart-capable → null.
- `P550LiveDockGateTests` (real client over loopback TCP):
  - a real client docks at a geometry-bearing VALID station (native InGarage
    path, unchanged `SHIP_STATUS` docked feel);
  - a real client is **refused** at a same-side station lacking `SabmLand` (in
    the bay, so geometry passes and only the ability gate blocks).

## Test updates (gates correctly surfaced stale fixtures)

- `StationCaptureLiveTests` / `ConquestTerritoryLiveTests`: their synthetic
  stations carried `SabmCapabilities=0`/flag-only — no longer dockable/capturable
  by any hull (native's capture branch sits INSIDE the land gate). Granted real
  `SabmLand|SabmCapLand` (flag bases add `SabmFlag`).
- `RepairAndRefuelTests` negative: the m2xBowTie Refinery has no `SabmLand`, so a
  fighter can no longer dock there (natively correct). Test spawns the ship
  already docked, preserving its no-heal/no-rearm-at-launch purpose.

## Citations (native → port)

| native | port |
|---|---|
| `shipIGC.cpp:725-727` fighter→`SabmLand`, else→`SabmCapLand` | `Sector.ResolveDockCollisions` capability gate |
| `shipIGC.cpp:843,853` `pStation->InGarage(ship, predictedPos)` | `Sector.ResolveDockCollisions` → `Station.InGarage` |
| `stationIGC.cpp:385-436` `CstationIGC::InGarage` | `Station.InGarage` |
| `stationtypeigc.cpp:77-101` station `capgarageN{A..}` load | `HullLandingBay.ReconstructStation` / `StationLandingBayCache` |
| `FedSrv.CPP:12034-12038` respawn `FindTarget(c_ttNearest, c_sabmRestart)` | `FindNearestRestartStation` |
| `fsmission.cpp:1433-1444` `GetBase(pside)` | `FindNearestRestartStation` fallback |

## Build & tests

Build: 0 warnings, 0 errors (whole solution).

Full suite via `scripts/run-tests.sh` (P471): **3 consecutive runs on the final
code.** I ran the four test assemblies (sequentially, as the P471 runner does)
three times; each line is one measured run:

| run | Lobby.Tests | ClientCore.MonoGame.Tests | ClientCore.Tests | Core.Tests |
|-----|-------------|---------------------------|------------------|------------|
| 1   | 12/12 | 1327/1327 | 543/545 | 3761/3763 |
| 2   | 12/12 | 1327/1327 | 545/545 | 3759/3763 |
| 3   | 12/12 | 1327/1327 | 544/545 | 3761/3763 |

All failures are the **documented intra-assembly parallel-load flakes**
(`docs/TEST_FLAKES.md`), not this task's code: they are live/socket timing tests
(asteroid-mining broadcast, transport packet flood, cargo-bay part lookup,
treasure pickup, game-over players receipt, side-elimination game-over) that
vary per run and **pass in isolation** — I re-ran every distinct failure class in
isolation and each passed (e.g. 4/4 at 283 ms, 2/2 at 259 ms). None touch the
dock/respawn paths this task changed. Core failures per run: 2, 4, 2; ClientCore:
2, 0, 1; MonoGame and Lobby: 0 every run. The dock-capital regression these gates
correctly surfaced (hull 792 Adv Devastator at non-CapLand garrisons) was fully
resolved and those tests pass deterministically (P550DockAndRespawnTests,
P550LiveDockGateTests, and the 44 boarding/promote/turret/logoff/win/flag tests
all pass; MonoGame turret suites 1327/1327 every run).


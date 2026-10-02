# P482 — Docked Ship Frozen to Watchers: same-tick ship update defeats the SHIP_DELETE "gone" latch

Date: 2026-10-01
Branch: `master` (committed, not pushed)
Predecessor: P481 (game-over Back -> team lobby)
Closes: P472 follow-up #1 (the one genuine production race among the top-5 flakes)

## TL;DR

A ship that docks can stay on other players' screens, frozen at its last pose,
because the one-shot `SHIP_DELETE(20) SDR_DOCKED` "gone" latch is undone **in the
same tick** by the periodic `HEAVY_SHIPS_UPDATE(176)`. Root cause: docking sets
`Ship.StationId` but leaves the ship in the sector's `_models`, so the per-tick
ship-update candidate cache (`Sector.GetShipUpdateCandidates`) built earlier in
the tick is never invalidated and still feeds the just-docked ship to the
broadcast; the client's `Apply(HeavyShipsUpdate)` unconditionally clears
`IsGone`. Fixed by invalidating the candidate cache at the point the dock takes
effect — restoring native's invariant that a docked ship is absent from
`cluster->GetShips()`.

## 1. The confirmed chain (re-verified from scratch)

The dock-out live test `ShipDeleteGoneLiveTests.DockOut_WatcherLatchesGone_UndockReturnsAndClears`
failed **3/3, deterministically in isolation** (reproduced 1/1 on the pristine
tree before any change; the failing assertion is `watcher marked the docked ship
IsGone`, line 376). The exact chain:

1. A ship enters a friendly station's dock radius. The sector's per-tick dock
   pass `Sector.ResolveDockCollisions` (`Sector.cs:3417`) raises
   `IIgcSite.DockWithStationEvent`, which on the server latches
   `Ship.StationId` (`ServerHost.DockWithStationEvent`, `ServerHost.cs:718`,
   `sh.StationId = ds.Station.StationId;`) and raises `ShipDocked`.
2. The host's `OnShipDocked` broadcasts the one-shot `SHIP_DELETE(20)` with
   reason `SDR_DOCKED` to every peer still **flying** in the same sector
   (`BroadcastShipDeparted(ship, ship.Sector, ShipDelete.SdrDocked)`,
   `Server.Combat.cs:2266`; the broadcast builder is
   `BroadcastShipDeparted`, `Server.Combat.cs:2141-2178`; `GetGroupSectorFlying`
   port). Native: `FedSrvSiteBase::DockWithStationEvent` ->
   `pfsShip->AnnounceExit(pship->GetCluster(), SDR_DOCKED)` (`FedSrv.CPP:13107`),
   and `CFSShip::AnnounceExit` sends to `GetGroupSectorFlying(pclusterOld)`
   (`fsship.cpp:196-230`).
3. The watcher's `ClientWorld.Apply(ShipDelete)` sets `ShipEntity.IsGone = true`
   (`ClientWorld.cs:2096`, `e.IsGone = true`).
4. **The race.** The same tick's periodic `HEAVY_SHIPS_UPDATE(176)` is assembled
   from `Sector.GetShipUpdateCandidates` (`Sector.cs:456`). That list is **cached
   once per sector per tick** (`Sector.cs:468-479`) and only invalidated by
   `UpdateCount` (advances at the top of `Sector.Update`) or `_memberVersion`
   (bumped only by `Add`/`Remove`, `Sector.cs:308/315`). Because docking **neither
   advances `UpdateCount` mid-tick nor `Remove`s the ship from `_models`**, the
   cached snapshot built earlier in the same tick — during the models loop, which
   runs *before* `ResolveDockCollisions` and *can* call
   `GetShipUpdateCandidates` for NPC arbitration/dodge scans (`Ship.cs:1826`,
   `:1909`, `:1967`, `:4772`, `:5473`, `:6221`) — still contains the not-yet-
   docked ship and is reused by `BroadcastShipsUpdate` (`Server.Broadcast.cs:1643`).
5. The client's `Apply(HeavyShipsUpdate)` unconditionally sets `e.IsGone = false`
   (`ClientWorld.cs:1325`) for every heavy entry, undoing the latch in the same
   tick. The watcher's entity for the docker ends with `StationId=1051` (docked,
   via SHIP_STATUS) but `IsGone=false` — the frozen ghost.

### Why the client's HeavyShipsUpdate IsGone clear is the specific culprit

Only `Apply(HeavyShipsUpdate)` clears `IsGone` among the per-tick telemetry
messages. `Apply(SingleShipUpdate)` deliberately does **not** clear `IsGone`
(`ClientWorld.cs:1244-1251`, "do NOT clear IsGone here") precisely because a
single update can arrive for a ship outside our view. `Apply(LightShipsUpdate)`
only `Upsert`s. So the one message that could defeat the latch same-tick is the
heavy update. The server fix removes the just-docked ship from *all* of the
tick's heavy candidates, closing the hole for every update tier at the source.

### Native citations

- A docked ship is **not physically in the cluster**: native docking detaches
  the ship from its cluster (its `m_cluster` is set to NULL), and cluster
  membership is what feeds the ship-update list. `CmodelIGC::SetCluster`
  (`modelIGC.cpp:246-275`) calls the site's `SetCluster(m_model, newVal)` which
  does the cluster's `AddModel`/`DeleteModel`; a ship with `cluster == NULL` is
  absent from `cluster->GetShips()`. This is exactly the port's own comment at
  `Sector.cs:~484` ("docked ships are not physically in the cluster (native:
  absent from cluster->GetShips())").
- Native ship-update assembly iterates `pcluster->GetShips()` to build the
  candidate array (`FedSrv.CPP:3727`) and sends them per-recipient
  (`FedSrv.CPP:4150-4165`, `SendShipUpdates`). A docked ship is therefore never
  a candidate in any tick.

## 2. The fix chosen and why

**Primary, native-faithful server fix: invalidate the per-tick ship-update
candidate cache at the point the dock takes effect.** In
`Sector.ResolveDockCollisions`, after a successful dock, the sector now calls
`InvalidateShipUpdateCandidates()` (a small helper that bumps `_memberVersion`,
which is consulted *only* by the candidate cache, so the bump is a pure
invalidation signal with no other reader). The very next
`GetShipUpdateCandidates` call in the same tick recomputes from the live
`_models` and — because the just-docked ship's `StationId` is now set — the
existing `if (ship.StationId is not null) continue;` filter excludes it.

This is the **Option-1** choice from the brief ("bump `_memberVersion`, or
invalidate the candidate cache, at the point the dock takes effect"). Rationale:

- **Faithful to native.** Native's dock *removes* the ship from the cluster, so
  a docked ship can never appear in that tick's ship-update broadcast. This port
  keeps the ship in `_models` (it must still tick, resupply, and be able to
  undock), approximating native's removal with the `StationId` filter; the
  missing piece was that the filter only applies on a cache **rebuild**, and the
  cache could already hold the ship. Invalidating on dock restores the invariant
  "a docked ship is not in the candidate list this tick."
- **Handles all update tiers at the source.** Because every per-tick
  heavy/light ship-update is assembled from the same candidate list, the single
  invalidation excludes the just-docked ship from the whole family in that tick.
  (Single-ship updates are a separate per-dirty relay and already cannot clear
  `IsGone`, see §5.)
- **Does not regress P324's cache.** P324 (M3) introduced the per-tick cache as a
  per-tick perf optimisation. This fix preserves it: the cache is still built at
  most once per tick in the normal case, and the invalidation only adds a
  *second* build on the rare tick in which a ship actually docks (one extra
  O(models) pass). Steady-state cost is zero.

**Rejected alternatives** (with reasons):

- *Option 2, re-applying the cheap `StationId`/`Dead`/`ParentShip` filters at
  broadcast assembly*: would need to be added at every per-peer assembly site
  and re-introduces the redundancy the cache exists to amortise; it fixes heavy
  but not the principle, and diverges from native (native never even iterates a
  docked ship). Less faithful, more invasive.
- *Option 3, reordering dock processing vs the snapshot*: the dock already runs
  inside `Mission.Update`, strictly before the `broadcastShipsUpdate` stage of
  the same `SimPass`; reordering cannot help because the stale snapshot is built
  *inside* the sector update (models loop), not by ordering the broadcast stage.
- *Client-side guard only*: would mask the server's data corruption and diverge
  from native's wire contract (see §5 — not needed).

## 3. Sibling reasons: warp-out and log-off

`SHIP_DELETE` also covers warp-out (`SDR_LEFTSECTOR`) and log-off
(`SDR_LOGGEDOFF`) (P370/P128). Both were checked for the same same-tick
staleness and are **not vulnerable**:

- **Warp-out**: `ServerHost.HitWarpEvent` physically moves the ship to the
  destination sector via `oldSector.Remove(sh); destSector.Add(sh);`
  (`ServerHost.cs:940`). `Remove`/`Add` both bump the old sector's
  `_memberVersion`, so the old sector's candidate cache is invalidated and the
  warped-out ship is recomputed out of the same-tick broadcast before
  `broadcastShipsUpdate`. (Its new sector `Add`s it, correctly including it
  there.)
- **Log-off**: `RetireShipOnDisconnect` broadcasts `SHIP_DELETE(SDR_LOGGEDOFF)`
  and then `sector.Remove(ship); ship.Sector = null;`
  (`Server.Lifecycle.cs:1225`) — again a `Remove`, bumping `_memberVersion` and
  invalidating the cache.

So **only docking** leaves the ship resident in `_models` with a `StationId`
latch and no cache invalidation — which is exactly why only docking exhibited the
deterministic failure. No sibling fix or sibling test was required (they were
not vulnerable); the existing `SameSide_WarperLeavesSector…` and
`CrossSide_WarperLeavesSector…` live tests remain green (3/3, see §6).

## 4. Perf sanity check (vs P430's "15v15 holds ~20 Hz flat")

Measured with an in-process real-server harness (real `Server` + 30 real
`ConnectionSession` peers seated 15v15 in one sector, mission Started),
measuring the real `SimPass` cadence through the production `Tick` loop over a
6-second wall window (the same server metrics P430 relies on: `LoopIterations`
and `Overruns`, where an overrun is a pass exceeding the 50 ms 20 Hz period).

| configuration | ticks | achieved Hz | overruns | overrun % |
|---|---|---|---|---|
| **fix present** | 121 | **20.1** | 0 | 0.00% |
| **fix reverted** (baseline) | 121 | **20.1** | 0 | 0.00% |

Byte-identical. The change contributes zero steady-state cost: it only fires on a
dock tick (`_memberVersion++` plus one candidate-list rebuild), and this 15v15
run never docks, so the two runs exercise the exact same per-tick broadcast path.
P430's "holds ~20 Hz flat" claim is unregressed.

## 5. Client side: `Apply(HeavyShipsUpdate)`'s unconditional `IsGone = false`

**Decision: keep it; no guard added.**

Once the server is fixed, a docked ship is absent from the same-tick (and every
subsequent) heavy update until it undocks, so no heavy entry for a just-docked
ship can reach the client after its `SHIP_DELETE(SDR_DOCKED)`. Moreover:

- `Apply(HeavyShipsUpdate)`'s unconditional clear is the **required return-to-view
  mechanism**: when a ship undocks/warps back, it re-enters the recipient's
  sector candidate list, a fresh heavy entry arrives, and the client must clear
  `IsGone` — this is exactly what `DockOut_WatcherLatchesGone_UndockReturnsAndClears`
  asserts on the reverse leg (`!e.IsGone` after undock, via telemetry). Removing
  or gating the clear would break the undock-return contract.
- The only residual concern would be a *stale reordered datagram* (a heavy update
  built before the dock arriving after the `SHIP_DELETE`). This cannot happen on
  this port's transport: peers use ordered reliable TCP (`ConnectionSession` /
  `TcpTransport`), and both messages are emitted on the same thread in send order
  (`SHIP_DELETE` during the `update` stage, `HEAVY_SHIPS_UPDATE` during the later
  `broadcastShipsUpdate` stage) — so a post-delete heavy entry for the docked ship
  is impossible in both the ordering and the content senses.

Per the brief's instruction ("Don't add a guard 'just in case'"), no client guard
was added. The server fix is both necessary and sufficient.

## 6. Verification

- **Must-fail / failing-first**: the unmodified `DockOut_WatcherLatchesGone_UndockReturnsAndClears`
  failed 3/3 deterministically in isolation before the fix (reproduced 1/1 at the
  start of this session) and now **passes 20/20 in an isolated loop** (a `for i
  in $(seq 1 20)` run returned `pass=20 fail=0`). The test is unchanged (no
  `[Skip]`, no assertion loosened) — the production code was fixed.
- **Siblings**: `ShipDeleteGoneLiveTests` (all three: DockOut + SameSide warp +
  CrossSide warp) passes 3/3.
- **Perf**: §4 — 20.1 Hz / 0 overruns, identical with and without the fix.
- **Whole-solution build**: `/home/domingo/.dotnet/dotnet build` — 0 warnings,
  0 errors (see §7).

## 7. Deliverables

- Change (single file): `src/Allegiance.Core/Model/Sector.cs`
  - `ResolveDockCollisions`: invalidate the candidate cache when a ship docks.
  - new `InvalidateShipUpdateCandidates()` helper (+ corrected the P324 cache
    comment that wrongly claimed docking removes from `_models`).
- `docs/TEST_FLAKES.md`: `DockOut_WatcherLatchesGone…` -> `fixed` with cause;
  P482 resolution section added; P472 follow-up #1 marked closed.
- Report: this file.
- Committed on `master`, not pushed (`git branch --show-current` = `master`).

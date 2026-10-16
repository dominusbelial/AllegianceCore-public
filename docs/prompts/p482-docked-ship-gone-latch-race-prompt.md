# P482 — a docking ship reappears frozen to watchers: same-tick ship update defeats the SHIP_DELETE "gone" latch

## Why this task exists

P472 (`9612f1d6`) found this as a **genuine production race**, not a test flake
(`docs/reports/P472_FLAKY_TEST_FIXES.md` §1, `docs/TEST_FLAKES.md` row
"real bug (P472)"). The test
`ShipDeleteGoneLiveTests.DockOut_WatcherLatchesGone_UndockReturnsAndClears`
fails 3/3, deterministically, even in isolation. It was deliberately left
unmodified, and it's correct.

**The race, as traced by P472. Re-verify every step yourself.**

1. A ship docks. The server sends the one-shot `SHIP_DELETE(20)` with reason
   `SDR_DOCKED` to the same-sector watcher: `BroadcastShipDeparted`
   (`src/Allegiance.Server/Server.Combat.cs:2155-2176`), triggered at `:2266`.
2. The watcher's `ClientWorld.Apply(ShipDelete)` sets `ShipEntity.IsGone = true`
   (`src/Allegiance.ClientCore/State/ClientWorld.cs:2096-2108`).
3. That same tick, the periodic `HEAVY_SHIPS_UPDATE(176)` is built from
   `Sector.GetShipUpdateCandidates` (`src/Allegiance.Core/Model/Sector.cs:456-486`).
   That list is **cached per tick** and only invalidated by `UpdateCount` or
   `_memberVersion`. The cached snapshot was taken **before** the dock removed
   the ship from `_models` and set `StationId`, so it still includes the
   just-docked ship.
4. The client's `Apply(HeavyShipsUpdate)` unconditionally sets
   `e.IsGone = false` (`ClientWorld.cs:1325`), which undoes the latch in the
   same tick.

**What a player sees:** a ship that docks can stay on other players' screens,
frozen at its last pose. That's the same class of ghost P344 (death) and P370
(dock/warp "left your view") were built to stop.

**Native:** a docked ship is not in `cluster->GetShips()`, so it can't be in
that tick's ship update at all. See the port's own comment at
`Sector.cs:~479` ("docked ships are not physically in the cluster (native:
absent from cluster->GetShips())"). Native is at `~/Development/Allegiance/src/`.
Cite native's dock removal and ship-update assembly when you fix this.

## Scope

1. **Server-side fix: the primary, native-faithful one.** A ship that
   docked this tick must not appear in this tick's ship-update broadcast,
   whether the update is heavy, light or single. Fix the cache staleness
   where it originates. Options include:
   - bumping `_memberVersion`, or invalidating the candidate cache, at the
     point the dock takes effect;
   - re-applying the cheap `StationId`/`Dead`/`ParentShip` filters at
     broadcast assembly;
   - correcting the order of dock processing vs. snapshot.
   Pick one and justify it against P324 (M3)'s reason for the cache: it was
   a per-tick perf optimisation, and **P430 measured that 15v15 holds about
   20 Hz flat**. Don't regress that, and say how you checked.
2. **Check the sibling reasons.** `SHIP_DELETE` also covers warp-out and
   log-off (P370). Confirm whether the same same-tick staleness can defeat
   the latch for those. If it can, fix them through the same mechanism and
   test them.
3. **Client side.** Decide whether `Apply(HeavyShipsUpdate)`'s unconditional
   `IsGone = false` is still right once the server is fixed. If keeping it
   leaves a real hole, for example a stale reordered datagram, add the
   narrowest guard and justify it with native's client behaviour. Don't add
   a guard "just in case".

## Explicitly out of scope

- The other `docs/TEST_FLAKES.md` load-flakes.
- Redesigning P370's gone/reappear signal.
- Ship-update budget and prioritisation.

## Verification

- `ShipDeleteGoneLiveTests.DockOut_WatcherLatchesGone_UndockReturnsAndClears`
  **passes unmodified**: 20/20 in an isolated loop, and clean across 3
  full-suite runs via `scripts/run-tests.sh` (P471's sequential runner).
  It is already the failing-first test, so record its 3/3 pre-fix failure in
  the report.
- New tests for any sibling reason (warp-out, log-off) you found
  vulnerable, failing first.
- A perf sanity check against P430's claim, using the existing
  `get_perf_stats` / scenario path or an equivalent in-process measurement.
  Show update cadence isn't regressed at 15v15.
- Update `docs/TEST_FLAKES.md`: DockOut → `fixed`, with the cause.
- Whole-solution build with `/home/domingo/.dotnet/dotnet build`, 0
  warnings and 0 errors.

## Deliverables

1. Committed on **`master`**, not pushed. Check with
   `git branch --show-current`.
2. The report `docs/reports/P482_DOCKED_SHIP_GONE_LATCH_RACE.md`. Cover the
   confirmed chain with native citations, the fix chosen and why, the
   sibling-reason findings, and the perf check.

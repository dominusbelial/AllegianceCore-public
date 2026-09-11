# P158 — cross-cluster searches: closing the autonomous mining loop

Branch `master`, atop `72c6b81` (P157). No VM, no wiki writes.

## What landed

Stages 1–3 of the NPC-autopilot plan are landed (movement P155, the miner
economy loop P156, cross-cluster transit P157). P157 proved a ship *can* travel
to a cross-cluster target — but only when something explicitly hands it one:
`Ship.FindNearestFriendlyStation`/`FindNearestMinableAsteroid` (the searches the
miner decision cycle actually calls) were still same-sector-only, so an idle NPC
with nothing mineable/sellable in its own sector would stand by forever. This
task closes that loop: the idle NPC now looks further out on its own initiative.

Four small, faithful changes in `src/Allegiance.Core/Sim/Ship.cs`:

1. **Per-sector search cores extracted** from both searches, with the existing
   public methods kept as thin same-sector wrappers (no behavior change for
   existing same-sector callers/tests):
   - `FindNearestFriendlyStation(short abilityBit)` → delegates to a new
     `FindNearestFriendlyStationIn(Sector?, short abilityBit)` core
     (`Ship.cs:722`, `:725`) — same null guards, same
     `sector.Stations` loop, same squared-distance selection.
   - `FindNearestMinableAsteroid()` → delegates to a new
     `FindNearestMinableAsteroidIn(Sector?)` core (`Ship.cs:842`, `:845`).
2. **Two new any-cluster searches** — BFS outward from the ship's own sector via
   the same warp graph `Sector.FindPathToward` walks:
   - `FindNearestFriendlyStationAnyCluster(short abilityBit)` (`Ship.cs:789`)
   - `FindNearestMinableAsteroidAnyCluster()` (`Ship.cs:897`)
   Both seed with the ship's own sector (hop 0) and call the per-sector core on
   each newly-reached sector in BFS order, returning the first match.
3. **`CrossClusterWarpGoal` generalized** from being hardcoded to
   `DataStationIGC` to `CrossClusterWarpGoal(short targetClusterId, string what)`
   (`Ship.cs:2323`) — it only ever read the target's `ClusterId` (it returns the
   *warp's* position, not the destination's), so it generalizes cleanly. Both the
   station branch (`Ship.cs:2233`) and the **newly-fixed asteroid branch**
   (`Ship.cs:2240`) of `ApplyNpcAutopilot` now route through it.
4. **`DecideMiningOrSelling` switched to the any-cluster searches**
   (`Ship.cs:2413`) — the mining leg now calls `FindNearestMinableAsteroidAnyCluster()`
   and the sell leg `FindNearestFriendlyStationAnyCluster(SabmUnload)`.

## The latent bug fixed as part of this task

`Ship.ApplyNpcAutopilot`'s **asteroid** branch had no cross-cluster check or
path-finding at all (unlike the station branch P157 had extended). Previously:

```csharp
else if (MiningAsteroidTarget is { } rock && rock.Data.Position is { Length: 3 } rp)
    goal = new Vector3(rp[0], rp[1], rp[2]);   // steering at the raw position regardless of its sector
```

This was harmless only because nothing had ever set a cross-cluster asteroid
target (the search was always same-sector). Extending the search *without*
fixing this branch would have produced a real, silent bug: the ship would steer
at a different sector's raw position coordinates as if they were local to its
own sector — meaningless, and it would never arrive. Now (`Ship.cs:2236-2241`):

```csharp
else if (MiningAsteroidTarget is { } rock && rock.Data.Position is { Length: 3 } rp)
{
    if (sector.Id == rock.Data.ClusterId)
        goal = new Vector3(rp[0], rp[1], rp[2]);                        // same-cluster — direct flight
    else
        goal = CrossClusterWarpGoal(rock.Data.ClusterId, $"asteroid {rock.Data.AsteroidId} '{rock.Data.Name}'");  // P158
}
```

`DataAsteroidIGC.ClusterId` already existed (`DataAsteroidIGC.cs:45`) — everything
needed to generalize `CrossClusterWarpGoal` the same way for an asteroid target.
The cross-cluster logging in `CrossClusterWarpGoal` (`Ship.cs:2323`) now uses the
passed `what` descriptor (e.g. `asteroid 7761 'ROID_7761'` vs
`station 8861 'SELL'`), preserving the one-shot-per-episode transition-guarded
logging P157 introduced.

## "Nearest across sectors" = fewest hops, not exact distance

The task's confirmed finding: a cross-sector "nearest" by raw distance is not
computable honestly anyway — a station/asteroid in another sector's coordinate
frame has no comparable distance to the ship's position in its own frame. So
both any-cluster searches adopt the same bar `FindPathToward` already
established: **BFS visit order by hop count** is the honest, consistent
comparator. Concretely: hop 0 (the ship's own sector) is always searched first
(the common local case returns exactly what the same-sector search returns),
then neighbours, then their neighbours, etc.; the first sector with a qualifying
candidate wins, and within that sector the per-sector core picks the nearest by
squared distance (deterministic, even if that intra-sector distance is only
meaningful for the hop-0 case).

Both searches traverse `Sector.Warps`/`ResolvedWarp.Destination` — the *same*
graph `FindPathToward` walks (`Sector.cs:41`, `ResolvedWarp.cs:32`), no second
traversal mechanism — each sector visited at most once by reference identity
(O(E) on the small real-corpus warp lists), and dangling edges
(`Destination == null`) are never traversed, the same rule `FindPathToward`
applies (`Sector.cs:370`).

## Explicitly out of scope (unchanged)

- **Fair-share/contention-aware searching** — still not modeled (P156's own
  accepted gap, unchanged): the searches have no idea which rocks/stations other
  NPCs already target, so multiple miners may pick the same rock.
- **Multi-hop distance-weighted "nearest"** — BFS hop-count order is the honest
  bar, per the finding above.
- **No change to `Sector.FindPathToward`, `Sector.ResolveWarpCollisions`, or
  `ServerHost.HitWarpEvent`** — all already correct, untouched (P157's
  "mechanics already built" finding; this task only *decides which sector to
  check next* and reuses the existing path-finder to get there).
- **No change to P103–P157 code beyond** the extracted per-sector cores, the two
  new any-cluster search methods, the `CrossClusterWarpGoal` generalization, and
  the `DecideMiningOrSelling`/`ApplyNpcAutopilot` call-site updates.

## Behavior-preserving extraction (same-sector callers unaffected)

The public `FindNearestFriendlyStation(short)` and `FindNearestMinableAsteroid()`
are now one-line wrappers over the extracted cores with the exact same guards,
iteration order, and selection logic they always had — so every existing
same-sector caller and test observes byte-identical behavior. This is proven by
the pre-existing suite staying green unmodified, paying particular attention to
`FindNearestFriendlyStationTests.cs` (the 5 P149 behaviors: friendly-repair
found; friendly-non-repair excluded; enemy-repair excluded; nearer-of-two wins;
none-qualify → null) and the P156 `MinerDecisionCycleLiveTests.cs` end-to-end
loop.

## Verification

No VM needed. Three layers of new tests (+11 total, all in
`tests/Allegiance.Core.Tests/`):

1. **Pure-domain BFS proofs — `AnyClusterAsteroidSearchTests.cs` (6)** on fully
   controlled synthetic graphs built from bare `Sector`s (warps/asteroids injected
   via reflection into `_warps`/`_mineable`, the same mechanism P157 uses):
   own-sector candidate beats a cross-sector one (hop-0 priority even vs a
   "closer"-by-foreign-frame rival); cross-sector rock found when own empty;
   one-hop beats two-hop (hop-count bar); whole-reachable-component empty → null;
   a dangling warp is never traversed → null; no sector → null.
2. **Pure-domain station proofs — `AnyClusterStationSearchTests.cs` (3)** on a
   real `XXV_simple.igc` mission: the ship sits in the P111 no-station fly sector
   1052 (empty own sector), whose real map warps are cleared via reflection so the
   reachable graph is exactly one synthetic edge — cross-sector friendly unload
   found (own-sector search agrees null); own-sector unload beats cross-sector
   (hop-0 priority); no-friendly-reachable → null (an enemy unload is excluded by
   the same per-sector friendly filter).
3. **Live loopback — `NpcCrossClusterMinerDecisionLiveTests.cs` (2)** — the
   task's required proof that the miner decision cycle now travels cross-sector
   *on its own initiative*, without any test explicitly handing it a target
   (the first time this port does so):
   - **Mining leg** (`Idle_npc_in_empty_sector_finds_and_mines_cross_sector_asteroid_on_its_own_initiative`):
     an idle NPC in a station-free sector of the real `XXV_simple.igc` map
     (which has zero map asteroids, so the one injected rock is the only
     mineable anywhere) with a warp to a sector that has the rock. Measured:
     the own-sector search returns null, the any-cluster search returns the far
     rock, `DecideMiningOrSelling` targets it (logged `mining asteroid 7761
     'ROID_7761'`), the fixed asteroid autopilot leg fires (logged
     `cross-cluster asteroid 7761 'ROID_7761' is in sector 'Haumea' (1057);
     steering toward warp 1051`), the ship transits (Arrokoth→Haumea at
     t≈298×0.05s), and begins mining (`Ore 5.0`, within 140 u of the rock).
   - **Selling leg** (`Full_npc_in_empty_sector_finds_cross_sector_unload_station_and_sells_on_its_own_initiative`):
     the mirror — a full NPC (started at `Ore = MineCapacity = 150`) in a
     station-free sector with a friendly `SabmUnload` station one warp away.
     The any-cluster station search targets it, the ship transits (t≈329×0.05s),
     docks, and sells: `Ore → 0`, side treasury credited exactly 750
     (150 × `OreCreditPerUnit` 5), the P156 dock-sell path reused unmodified.

Full existing suite green, **3 consecutive runs** (2779 tests entering this P158
checkpoint = 2768 baseline + 11 new), modulo the same five documented flakes
(`LiveChatSectorWireTests`, `LiveCombatWireTests`,
`LiveInSpaceEjectTests.Already_a_lifepod_is_truly_killed_not_re_ejected`,
`LiveSuicideShipDeleteTests.Combat_death_produces_ship_delete_to_same_audience`,
`ShipsUpdateLiveTests.Locked_target_fills_first_heavy_slot_with_real_fractions`)
which are rerun, not treated as regressions.

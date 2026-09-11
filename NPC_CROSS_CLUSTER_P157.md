# P157 — NPC cross-cluster movement: `FindPath` + autopilot extension

Branch `master`, atop `93cdf47` (P156). No VM, no wiki writes.

## What landed

Two small, faithful changes plus a live-loopback proof, covering the last
explicitly-deferred gap from P149/P155: an `IsNpc` ship whose ultimate target
(a station) lives in a *different* sector now navigates there — steering to the
connecting warp, transiting (via the already-working warp-collision pass), and
continuing toward the station in the new sector.

1. **`Sector.FindPathToward(Sector targetSector)`** — a breadth-first search
   over the sector warp graph, returning the **first `ResolvedWarp`** to head
   toward from the current sector, or `null` when the target is `this` sector
   or unreachable.
   (`src/Allegiance.Core/Model/Sector.cs`, inserted after `GetShipUpdateCandidates`,
   ~line 396.)
2. **`Ship.ApplyNpcAutopilot` goal-resolution extension** — when
   `PlanTarget`'s `ClusterId` differs from the ship's own sector, resolve the
   target `Sector`, path-find the next warp, and steer toward **that warp's
   position** instead of the station's; `ResolveWarpCollisions` carries the
   ship through automatically. Fallbacks (mission unbound / target sector
   absent / no warp path) are silent stand-bys, logged once each — no crash,
   no guess, no per-tick spam.
   (`src/Allegiance.Core/Sim/Ship.cs` — `CrossClusterWarpGoal`/`LogNoPath`
   beside `ApplyNpcAutopilot`, ~lines 2177-2262.)

No change to `Sector.ResolveWarpCollisions` (`Sector.cs:1653-1682`) or
`ServerHost.HitWarpEvent` (`ServerHost.cs:619+`) — both already work correctly
(the task's "mechanics already built" finding), and this task only ever *decides
which warp to fly at*.

## Simplified-BFS design and its honest deviations from native `FindPath`

Native's real `FindPath(IshipIGC*, IclusterIGC*, bool bCowardly)`
(`~/Development/Allegiance/src/Igc/common.cpp:894-1012`, read in full) is a
**distance-sorted best-first** search: it seeds the frontier with the current
cluster's visible warps ordered by physical distance to the ship
(`common.cpp:914-960`), then pops nearest-first, returning `path.pwarpStart` —
the *first warp of the winning path* (`common.cpp:972-977`; the start warp is
propagated through expansions at `common.cpp:933, 1001-1002`).

This port ships **plain unweighted FIFO BFS**. Stated plainly: both find *a*
shortest path by hop count; the only thing lost is native's tie-break that
prefers the physically nearer warp among equal-hop alternatives. That is the
documented, accepted simplification the task authorized. The return value maps
1:1 onto native's `pwarpStart` — the first warp to fly toward from the current
sector — so `ApplyNpcAutopilot` gets exactly the edge it needs, however many
hops remain.

### Friendly-territory (`bCowardly`) filter: **confirmed absent, dropped**

Native's cowardly mode prunes warps that neither lead toward the target nor
into a friendly cluster via `IsFriendlyCluster` (`common.cpp:924-926, 991-993`).
Investigated directly rather than assumed: this port's sector model carries **no
ownership/territory notion** — a `Sector` is a neutral `OT_cluster`
(`Sector.cs:27-33`), its `Id` is a layout id, not an owner, and there is no
`Sector.SideId` or analogous field anywhere in the sector model. With nothing to
filter on, **all warps are traversed unconditionally** — the non-cowardly native
behavior — stated as the applied default. (This is independent of the
no-alliance finding from this session's earlier work; sector *territory* was
confirmed separately and found equally absent.)

### Visibility gate: **dropped**

Native also skips warps the ship cannot `CanSee` (`common.cpp:920, 987`). An
autonomous NPC pathfinder in this pure-sim port navigates the full static graph
regardless of scan-state; folded into the same unconditional-traversal
simplification above.

### Cost / replan

Each sector is visited at most once (`HashSet<Sector>`, reference identity) and
each warp examined at most twice, so the search is O(E) — trivially cheap on the
real corpus. The autopilot re-resolves its goal **every tick**, which *is* the
replan: a ship that warps mid-journey naturally re-paths from its new sector on
the next tick (P157 scope), with no separate cache built.

## Explicit exclusions (unchanged)

- **Extending `Ship.FindNearestFriendlyStation` / `FindNearestMinableAsteroid`
  to search other sectors** is a separate, deliberately deferred problem
  (enumerating reachable sectors and checking each for a qualifying candidate).
  This task is purely: given an *already-known* cross-sector target — which only
  arises today via an explicitly test-authored `PlanTarget`, since the decision
  cycle's own searches are same-sector-only — get the ship there.
- **No change to `Sector.ResolveWarpCollisions` / `ServerHost.HitWarpEvent`.**
- **No change to any P103-P156 code** beyond the new path-finder and the
  `ApplyNpcAutopilot` goal-resolution extension.

## Verification

New tests in
`tests/Allegiance.Core.Tests/NpcCrossClusterLiveTests.cs` (7 tests):

- **Headline live loopback** (`Cross_cluster_npc_paths_through_warp_and_continues_to_station_in_new_sector`):
  boots the in-process server on the real multi-sector `XXV_simple.igc`,
  discovers a genuine cross-cluster warp pair at runtime (rather than
  hard-coding map geometry), spawns an `IsNpc` ship in sector A with a
  cross-sector `PlanTarget` to a real land-capable station in sector B, and
  ticks real chunks. Measured: `d0=10124 → (transit@331 ticks) dTransit=436 →
  dFinal=1.0`, ship's `Sector` changed to B, position measurably closer to the
  station — both overall and *after the crossing* (the "continues toward the
  station in the new sector" assertion).
- **Unreachable no-op** (`Cross_cluster_npc_with_unreachable_target_stands_by_without_moving_or_crashing`):
  a cross-sector target naming a `ClusterId` absent from the mission resolves to
  no path; the autopilot stands by — sector unchanged, position drift `< 1e-3`
  over 3000 ticks, no exception, no thrash.
- **Four direct unit proofs of `FindPathToward`** on fully-controlled synthetic
  graphs (built via reflection into the private `_warps`, the same mechanism the
  suite uses for `Ship._fraction`): already-there → `null`; adjacent → the
  connecting warp; multi-hop (A→C→B) → the **first** warp (A→C), proving the
  `pwarpStart` semantics carry across levels; unreachable (dead-end A→C, target
  B elsewhere) → `null` cleanly with no throw; no-warps → `null`.

**A real test-derived finding worth recording:** placing the B-side station
arbitrarily (e.g. via the max-warp-clearance direction) made the ship **stall
and ping-pong A↔B** — after transiting it re-crosses before it can leave the
exit warp's collision volume (110 u) within the 2 s warp-debounce window
(`Sector.ResolveWarpCollisions` gates on `WarpCooldownSeconds`;
`ResolveDockCollisions` also defers docking by the same window, `Sector.cs:1974`),
because the turn-then-burn autopilot cuts power while the nose re-aligns. The
fix is placement-informed-by-mechanics: `HitWarpEvent` launches the crossing
ship along the **partner warp's `Forward`** (`ServerHost.cs:639-646`), so
placing the station along that axis lets the ship burn straight out of the
volume toward it. That is a faithful illustration of why the autopilot's
per-tick goal steering and the transit pass cooperate — not a hack to the
physics.

Full existing suite green, 3 consecutive runs (2768 tests entering this P157
checkpoint = 2761 baseline + 7 new), modulo the five documented flakes
(`LiveChatSectorWireTests`, `LiveCombatWireTests`,
`LiveInSpaceEjectTests.Already_a_lifepod_is_truly_killed_not_re_ejected`,
`LiveSuicideShipDeleteTests.Combat_death_produces_ship_delete_to_same_audience`,
`ShipsUpdateLiveTests.Locked_target_fills_first_heavy_slot_with_real_fractions`)
which are rerun, not treated as regressions.

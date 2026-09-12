# P75 — undock needs a real debounce, not just position/velocity tuning

## The regression this fixes

P74 dropped P69's outward launch velocity (its arbitrary direction could
send the ship thousands of units off-course before the autopilot
corrected — a real, live-observed defect, "la la land"). But removing
the velocity entirely regressed the *original* P69 bug: launched at zero
velocity, a ship reorienting slowly toward a distant autopilot target can
drift back inside `DockRadius` and get re-latched by
`Sector.ResolveDockCollisions`, which gates purely on `StationId==null`
with no time component. Confirmed live: the ship re-docked ~38 seconds
after undocking — far past any short grace window, proving this isn't a
timing edge case velocity tuning alone can paper over.

## The fix

Two changes, working together rather than either alone:

1. **Restore the outward velocity kick** (`ExitStationSpeed`, 50 u/s,
   station→ship direction) for fast initial separation. Its direction is
   still uninformed relative to the real target (the server has no
   visibility into the client-side `SetCommand`/`SetAutopilot` state
   Miner.cs already set before requesting undock) — this fix does not
   claim to solve the "ship flies oddly for a while before committing to
   a heading" quality issue, which is native autopilot reorientation
   dynamics, a separate and deeper concern than this fix's scope.

2. **A real debounce, mirroring the existing warp-transit pattern**:
   `Ship.LastLaunchTime` (parallel to the already-existing
   `Ship.LastWarpTime`) and `Sector.LaunchCooldownSeconds` (8.0s — longer
   than the 2.0s warp cooldown, since a warp transit relocates the ship
   well clear of the source aleph as part of the transit itself, while an
   undock only guarantees a fixed `DockRadius + LaunchClearMargin`
   position offset with no guarantee about how the ship moves
   afterward). `Sector.ResolveDockCollisions` now checks this exactly
   like it already checks the warp cooldown. This is the actual
   guarantee against re-docking — not the velocity kick, which is best
   understood as an optimization/nicety on top of it.

## Why not just tune the velocity instead

Both of P69's and P74's approaches tried to make position+velocity alone
carry the whole guarantee (either "kick hard enough to reliably clear the
zone" or "start at rest so nothing conflicts with the real heading").
Both failed on a real, live-tested run. A time-based debounce is the
robust fix because it doesn't depend on the ship's flight path being
"good enough" within some implicit window — it directly encodes the
actual invariant (a freshly-undocked ship should not be considered for
re-docking for a while), the same design already proven for the
analogous warp-transit problem in this codebase.

## Tests

2290/2290 across 3 consecutive clean runs.

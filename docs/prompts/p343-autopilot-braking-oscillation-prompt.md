# P343 — Fix the player autopilot's unstable braking oscillation

## Context

`AllegianceCore` is a C# port of native FreeAllegiance
(`~/Development/Allegiance`, read-only reference, never modify it).

Live flight-testing today (2026-09-22) found a real, reproducible bug in
the player "click-and-go" autopilot (P212, `T` to pick a target, `G` to
engage): approaching a stationary target from a nontrivial distance at
speed, the ship does **not** converge — it settles into a sustained,
literally periodic oscillation, overshooting the target every cycle.
Domingo's own words: "it activates as soon as i hit t again... there it
is, again the same loop, flight trajectory... it is funny that changing
the target as it cycles in autopilot it runs/ends in the same loop
flight trajectory" — the same characteristic bounce reproduces for any
target, not one specific ship.

**Root cause, confirmed with a live per-tick diagnostic trace** (added
temporarily to `Game1.cs`, reverted after diagnosis — see the exact
numbers below, captured live against a stationary `vizfight roster`
bot):

```
dist=371.1 throttle=0.73   <- closing, near a local minimum, decelerating
dist=751.1 throttle=-1.00  <- ballooned back out to ~751 UNDER FULL REVERSE THRUST
dist=766.0 throttle=-1.00
dist=778.8 throttle=-1.00
...
dist=814.2 throttle=-0.18  <- peak distance, throttle finally flips positive
dist=812.4 throttle=0.31
...
dist=371.5 throttle=0.73   <- back down to ~371, same as the start — one full period
dist=753.9 throttle=-1.00  <- and it repeats, forever
```

Distance bounces between ~370 and ~815 units in a stable, repeating
cycle. This is a classic **proportional-only control instability**:
`AutopilotSteering.SteerToward` (`src/Allegiance.ClientCore.MonoGame/Flight/AutopilotSteering.cs:76-114`)
computes throttle purely from **current distance** (the two-zone
`distFactor` ramp between `AutopilotBrakeStartDistance` (420) and
`AutopilotArrivalRadius` (60)) and nose alignment — it has **no braking
distance calculation and no velocity term at all**. A ship closing fast
has real momentum; by the time distance drops into the brake zone and
throttle reverses, the ship is already moving too fast to actually stop
before crossing the target, so it overshoots, and reverse thrust (now
fighting a growing separation) takes just as long to arrest and reverse
that motion — producing exactly the sustained, undamped bounce
observed. It's not target-specific because the same unstable dynamic
reproduces for any stationary target approached with enough initial
closing speed.

This is the **player-side copy only** — `AutopilotSteering.cs`'s own
doc comment already calls it out precisely: native has "a physics-
optimal autopilot deriving braking distance from a closed-form solution
of the ship's own drag differential equation" (`GotoPlan::SetControls`,
`src/Igc/common.cpp:1830-2000+`), and this port "deliberately" chose the
simplified two-zone distance ramp instead. That simplification is what's
now proven unstable in practice.

**A second, confirmed-working data point from the same trace, worth
preserving**: approaching more slowly/from closer range, the exact same
code converges cleanly and holds station indefinitely (`dist=362→56.5`,
monotonic, no oscillation, `yaw`/`pitch` decaying smoothly to 0). So the
two-zone law is not wrong in every case — it just has no protection
against high closing speed relative to available deceleration, which is
precisely what a real braking-distance calculation would fix.

**Ship velocity is available and NOT currently used anywhere in this
code path** — confirmed by reading the wire type: `ServerShipUpdate.Velocity`
(`src/Allegiance.Core/Wire/ServerShipUpdate.cs:56-57`, decoded from the
real `CompactVelocity` wire bytes) is real per-tick data already
delivered to the client for every ship, own and teammate alike. Nothing
in `Game1.TryOwnPose` (`Game1.cs:4673-4685`) currently reads it — it
only extracts `Position`/`Orientation` from the same `up` (`ServerShipUpdate`)
object that already carries `Velocity`.

## Scope

1. **Extend `TryOwnPose`** (`Game1.cs:4673-4685`) to also output the
   ship's current `Velocity` (a third `out SimVec` param, or a new
   overload — your call, whichever reads cleaner at the two call sites
   that need it vs. the several that don't).
2. **Redesign `AutopilotSteering.SteerToward`'s throttle law**
   (`src/Allegiance.ClientCore.MonoGame/Flight/AutopilotSteering.cs:100-113`)
   to incorporate the ship's own current velocity so braking begins
   early enough to actually stop by the arrival radius, not just when
   static distance crosses a fixed threshold. Concretely: project the
   ship's current velocity onto the line toward the target to get a
   real closing speed, and only begin braking once the *actual stopping
   distance* at that closing speed (using whatever deceleration this
   port's throttle=-1 realistically achieves — look at how `ExecuteMove`
   converts `Controls.Throttle` into real accel/drag in `Ship.cs` for a
   grounded number, don't guess one) is within `AutopilotBrakeStartDistance`
   of the target, rather than raw distance alone. This is the standard
   fix for this exact instability class (a real braking-distance term
   instead of a naive distance ramp) — keep it as simple as it can be
   while actually being stable; this does not need to fully replicate
   native's closed-form drag solution, just enough velocity-awareness
   to kill the overshoot.
3. **Keep the existing `TryGetAutopilotControls` arrival-disengage
   logic** (already landed, commit `6480d73` on `master`: disengages
   once within `AutopilotArrivalRadius` of a *target that hasn't moved
   since engage*) — it's correct and complementary, just currently
   unreachable in the oscillating case because distance never actually
   drops that low. Once the oscillation is fixed and the ship can
   really converge, confirm this existing disengage still fires
   correctly on arrival (it should, unchanged).
4. **Signature/call-site update**: `SteerToward`'s public signature will
   need the velocity input added — update its one caller
   (`TryGetAutopilotControls`, `Game1.cs:~5071-5089`) and its existing
   unit tests (`tests/Allegiance.ClientCore.MonoGame.Tests/` — search for
   `AutopilotSteering`/`SteerToward`) to pass a velocity, defaulting
   existing zero-velocity test cases to `Vector3.Zero` so today's
   passing assertions keep meaning what they meant (a ship starting at
   rest).

## Explicitly out of scope this task

- **`Ship.cs`'s `ApplyNpcAutopilot`/`Ship.SteerToward`** (the NPC/bot AI
  copy this was originally ported from, `Ship.cs:3720`-ish) — a
  deliberately SEPARATE, standalone copy per `AutopilotSteering.cs`'s
  own doc comment. Do not touch it: it drives live bot behavior
  (mining, combat, wingman orders) that has its own established tuning,
  and destabilizing it is a much bigger, separate concern than this
  player-only bug. If you believe the NPC copy has the identical
  instability, note it in your final report as a candidate follow-up —
  do not fix it in this task.
- Any change to `AutopilotArrivalRadius` (60), `AutopilotBrakeStartDistance`
  (420), or `AutopilotTurnGain` (2.0) as bare constants — if your fix
  needs new constants (e.g. an assumed deceleration), add them
  alongside, don't repurpose the existing three.
- The `I`-key mouse-invert-pitch feature, ship-ship collision (confirmed
  by design absent, `Sector.cs:783-788` — not a bug, don't add it), and
  anything about the `vizfight roster` tool itself — unrelated,
  untouched this task.
- Do not touch native reference source (`~/Development/Allegiance`) —
  read-only.

## Verification — real proof, not just build/test

- **Live proof, required**: fly a real MonoGame client (against a
  `vizfight roster <port>` server is fine — that's how this was
  originally found), pick a stationary target with `T` from a distance
  of several hundred units, engage with `G`, and confirm the ship now
  converges smoothly to a stop near the target — no sustained
  back-and-forth bounce — for at least 3 independent approaches (vary
  starting distance/angle). Also re-confirm the ALREADY-WORKING close/
  slow-approach case from the diagnostic trace above still converges
  cleanly (no regression).
- Whole-solution build: 0 warnings, 0 errors.
- Full test suite (`/home/domingo/.dotnet/dotnet test`), run at least
  2x: only the pre-existing documented `Allegiance.Core.Tests` live-wire
  timing flakes are expected (see P332's final summary) — nothing in
  `Allegiance.ClientCore.MonoGame.Tests` should newly fail.

## Deliverables

1. The fix, committed on `master` (not pushed — Domingo pushes). Do not
   create a side branch.
2. A short final summary: the actual braking-distance/velocity approach
   used, the 3+ live approach-angle proofs performed (starting distance
   and outcome for each), build/test status, and whether you spotted
   the same instability in `Ship.cs`'s NPC copy (informational only,
   not fixed here).

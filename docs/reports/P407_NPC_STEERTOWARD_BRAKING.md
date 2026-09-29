# P407 — NPC SteerToward never got P343's velocity-aware braking

**Verdict: INVESTIGATED, NOT CURRENTLY PLAYER-VISIBLE — no fix ported.**

The code divergence the review identified (finding U1) is real and
reproducible in an idealized closed loop, **but the claimed player-visible
case — a laden NPC miner overshooting and orbiting its garrison before
docking — does not reproduce live**. The real dock latches the miner on its
inward pass before the overshoot regime is ever reached, so the naive ramp
is masked in the one scenario the brief scoped the repro to. Per the
brief's explicit instruction ("if it does NOT reproduce … don't port the fix
speculatively"), **`Ship.SteerToward` is left unchanged.**

---

## The divergence (confirmed, high confidence)

`Ship.SteerToward` (`src/Allegiance.Core/Sim/Ship.cs:4682-4723`, the
server-side NPC steering law) still uses the naive two-zone distance ramp
P343 replaced in the player autopilot:

```csharp
float distFactor = distance >= brakeStartDistance
    ? 1f
    : Math.Clamp((distance - arrivalRadius) / (brakeStartDistance - arrivalRadius), 0f, 1f);
```

The player autopilot (`AutopilotSteering.cs:150-159`) widens the brake onset
to `max(brakeStartDistance, closing²/decel + arrivalRadius)` using the ship's
real closing speed and max deceleration. `Ship.SteerToward` never got that
term.

Quantifying the real-world consequence for the actual miner hull
(`dminr` "Adv Miner", the hull `SpawnNpcShip` assigns):

```
Speed = 120 u/s   Thrust = 1000   Mass = 100
max decel (Thrust/Mass) = 10 u/s²
stopping distance at cruise (Speed²/decel) = 1440 units   ← vs. the fixed 420-unit brake start
```

The miner's cruise stopping distance (1440 u) is **more than three times the
420-unit brake-start distance**, so the fixed ramp is far too short to stop a
miner arriving at full cruise. Driving the production private
`Ship.SteerToward` through the real `Ship.ExecuteMove` physics in a
dock-free closed loop (the exact P343 test methodology) reproduces the
diagnosed failure for the `dminr` hull nose-on at cruise:

```
nose-on, 120 u/s, 950 u out:  min distance = 0.1  (blows straight through the target)
                              settles oscillating at ~229 u   oscillations = 1
slow/rest approach:           settles at ~31 u, no trouble   (the known-good case preserved)
```

So the braking law *is* broken for a sustained-full-cruise point-stop — the
divergence is not imaginary.

## Live repro (the brief's scope-1 question): laden miner dock approach

The brief's repro is specifically **"a laden NPC miner's dock approach at
full cruise speed … does it overshoot and orbit before successfully
docking?"** Done live (in-process server, real `Server.TickSim`, real
`Ship.ExecuteMove` + real dock latch — the port's established "live" idiom,
no VM): a laden miner (`Ore = MineCapacity`) with the real `dminr` hull is
spawned returning to a friendly `SabmUnload` garrison and approaches at
**true full cruise (measured `maxSpeed = 119 u/s`)** from ~1000–1550 u out.
The laden-miner decision cycle arms `PlanTarget` → the garrison and the real
autopilot steers it in.

Three approach geometries, all dock cleanly on the first inward pass — **no
overshoot, no orbit, no re-approach**:

```
nose-on 1000   d0=1000.0  minD=199.8  maxSpeed=119  first<200@428  docked@428   no re-expansion
off-axis       d0=1048.6  minD=197.0  maxSpeed=119  first<200@268  docked@268   no re-expansion
tangential     d0=1549.5  minD=199.7  maxSpeed=119  first<200@399  docked@399   no re-expansion
```

(`minD` ≈ 198-200 = the ship is latched the moment it crosses `DockRadius`;
`maxAfterClose` never exceeded 200 → no oscillation; every case `docked=True`.)

**Why the overshoot never surfaces:** the dock latch is
`Sector.DockRadius = 200` (`Sector.cs:1443`), **larger than the autopilot's
arrival radius (60)** and inside the region the ship inevitably passes
through on its way to the steering point. A dock approach is not a
point-stop; the miner only needs to *enter* the 200-radius sphere around the
garrison, which it does regardless of braking quality. The overshoot the
closed loop shows (oscillating ~229 u) happens only *after* passing through
the station centre — by which point the ship has already latched the dock.

## Adjacent check: the mining-approach arrival (same law, genuine point-stop)

Because docking masks the law, the natural next question was whether the
*same* `SteerToward` law causes a visible problem at the one NPC arrival
that genuinely requires a near-stop: mining. `Sector.ResolveMining`
(`Sector.cs:3126-3128`) only accrues ore when the ship is within the rock's
proximity **and** `Velocity² < 1` (essentially stopped) — no 200-unit latch.
Live, a miner approaching a He3 rock from ~1000-1400 u at full cruise:

```
nose-on 1000   d0=1020  minD=21.5  single stop-zone entry  firstNearStop@556  beganMining@556  maxOre=150
nose-on 1300   d0=1295  minD=7.8   single stop-zone entry  firstNearStop@619  beganMining@619  maxOre=150
off-axis 1100  d0=1402  minD=10.9  single stop-zone entry  firstNearStop@481  beganMining@481  maxOre=150
```

Every approach stopped, settled in one zone entry (no orbit), began mining,
and filled the bag to capacity. In the real miner flows the ship accelerates
from a spawn/undock velocity and reaches the rock having never spent long at
sustained full cruise near the rock — so the fixed ramp is adequate in
practice for the demonstrated distances.

## Residual risk (why this stays a divergence, not a shipped bug)

The divergence is real and would become player-visible in any hypothetical
NPC goal that forces a *sustained-full-cruise point-stop* — e.g. a very
distant mining rock (≳ the cruise stopping distance) approached at full
cruise, or a faster/lighter hull with an even longer stopping distance.
Mining target selection and other NPC decision logic are explicitly out of
P407's scope, and no such case reproduces in the in-scope dock flow, so the
fix is not ported now. This is recorded as a latent limitation of
`Ship.SteerToward`, not an active defect.

## What was changed

Nothing in production code. `Ship.SteerToward` is untouched, as are
`AutopilotSteering.cs`, the dock/mining/sell logic, and P368–P406. The
exploratory repro harnesses used to establish the verdict were temporary
scratch tests and were removed; no test changes remain on the tree.

## Verification performed

- Live in-process-server repro of the laden-miner dock approach at true full
  cruise (nose-on / off-axis / tangential): docks cleanly, no overshoot, no
  orbit (three geometries, measured `maxSpeed ≈ 119 u/s`).
- Live in-process-server repro of the mining-approach arrival (same law,
  genuine point-stop): stops, settles, mines to capacity (three geometries).
- Dock-free closed-loop drive of the production private `Ship.SteerToward`
  through real `Ship.ExecuteMove` physics: confirms the naive ramp's
  stopping shortfall at sustained full cruise (min 0.1, oscillates ~229 for
  the `dminr` hull), establishing the divergence is real but unreached in
  the in-scope live flow.
- Whole-solution build of `tests/Allegiance.Core.Tests` (the only project
  touched, by transient scratch files since removed): **0 warnings, 0
  errors**. No production assembly changed, so no suite regression is
  attributable to P407.

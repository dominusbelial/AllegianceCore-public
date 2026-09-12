# NPC Wingman Range Maintenance — P171

Stage 4 of the Wingman combat AI arc (following P162's fixed standoff, P166's
defend, P169's aim leading). Adds native's *relative-speed* range maintenance —
backing off when the target is closing too fast, pressing in when it pulls
away, else matching its speed — as a **deliberately reduced single band**,
replacing P162's fixed standoff-hold throttle **only while the Wingman is
actively engaged**.

Repo: `/home/domingo/Development/AllegianceCore`, branch `master`, on top of
`f20b27c` (P170).

---

## 1. The native source, read in full

Native block: `Igc/shipIGC.cpp:2479-2512` (the relative-speed metric plus band 1
of the maneuvering block), embedded in the larger combat block read in full
this session.

The closing-speed metric native keys the whole block off
(`shipIGC.cpp:2479-2481`):

    float ownDirSpeed = GetOrientation().CosForward(GetVelocity())*GetVelocity().Length();
    float targetDirSpeed = GetOrientation().CosForward(m_commandTargets[...]->GetVelocity())*...->GetVelocity().Length();
    float relativeSpeed = ownDirSpeed - targetDirSpeed;

Each term is the projection of that ship's velocity onto **this ship's own
forward** (`CosForward(v)·|v| = forward·v`); `relativeSpeed` is therefore
positive when the pair is *closing*, negative when *falling behind* — the sign
convention that pins the thresholds below.

Band 1 (the in-the-heat band, `shipIGC.cpp:2492-2512`):

    if (angleToLead < pi * 0.5f) {              // :2492
        if (t < lifespan * 0.45f) {             // :2493  "in the heat"
            if (relativeSpeed < -20.0f) {       // :2494
                state |= forwardButtonIGC;      // :2495  press in
            }
            else if (relativeSpeed > 20.0f) {   // :2499
                state |= backwardButtonIGC;     // :2500  back off
            }
            else {                              // match speed
                float throttle = (targetDirSpeed >= myMaxSpeed)
                    ? 1.0f
                    : (targetDirSpeed <= 0.0f ? -1.0f : 2.0f * targetDirSpeed / myMaxSpeed - 1.0f);  // :2507-2509
                m_controls.jsValues[c_axisThrottle] = throttle;      // :2511
            }
        }
    }

---

## 2. Finding: `forwardButtonIGC`/`backwardButtonIGC` ARE this port's `Throttle` axis in disguise — CONFIRMED

Native never applies the two mechanisms together: the button branches set a
move-button bit (`state |= forwardButtonIGC`/`backwardButtonIGC`, `:2495`/`:2500`)
and **fall out of the match-speed branch**, while the match branch sets the
graduated `jsValues[c_axisThrottle]` value **instead of** either button
(`:2507-2511`). They are mutually exclusive longitudinal commands — "drive full
speed along my current heading" (button) versus "set this exact throttle"
(axis) — and the ship has already turned nose-on to the target before this
block runs (via `turnToFace`, out of P171 scope; P162/P169 already handle
facing via `SteerToward`).

This port has exactly one longitudinal control with the same meaning:
`Controls.Throttle` (a −1…+1 scalar consumed by `ExecuteMove`'s throttle-cruise
branch, `Ship.cs:2379-2385`: `+1` = full forward, `−1` = brake/stop). So the
translation is exact: `forwardButtonIGC → Throttle +1`, `backwardButtonIGC →
Throttle −1`. **No new strafe/reverse physics is needed or added** — the port's
single Throttle axis already expresses both. Stated plainly per the task brief.

## 3. Sign reconciliation (task-prose parenthetical corrected)

`relativeSpeed` is positive = closing. Therefore the faithful reading of the
native `:2494`/`:2499` is:

- `relativeSpeed < −20` (falling behind / target pulling away) → `forwardButtonIGC` → **press in (Throttle +1)**;
- `relativeSpeed > +20` (closing too fast / target inbound) → `backwardButtonIGC` → **back off (Throttle −1)**.

This matches Scope item 4's requirement ("target moving toward the Wingman →
back off; moving away → close in") and the native code. The task prose's
parentheticals ("back off if closing too fast (`relativeSpeed < -20`)") had the
two labels swapped relative to native; this port implements the **native** sign
(the one the test-scope also demands), stated here as the deliberate correction.

## 4. The single reduced band and how it composes with P162's standoff

Only band 1's *logic* is ported (the `relativeSpeed`-vs-±20 three-way plus the
graduated match-speed formula), in a new private method
`Ship.ComputeWingmanRangeThrottle(Ship)` (`Ship.cs:3227`), fed by a new
`Ship.ComputeRelativeSpeed(Ship)` (`Ship.cs:3198`) that reproduces
`shipIGC.cpp:2479-2481` using the port's existing `forward·v =
−Orientation.TimesInverse(v).Z` dot product (the same CosForward-style metric
the P155 turn law and `SteerToward` already use, `Orientation.cs:147-151`,
`Ship.cs:3119`). The ±20 thresholds are a named, cited constant
`WingmanClosingSpeedThreshold` (`Ship.cs:2584`, `shipIGC.cpp:2494,2499`),
following the `WingmanFireFacingCosine` (P162) precedent.

**Wiring / composition** (`ApplyNpcAutopilot`'s combat branch, `Ship.cs:2697-2734`,
override at `Ship.cs:2761`): when `AttackTarget` is set and same-sector, the
branch still sizes the P162 `arrivalRadius`/`brakeStartDistance` standoff (those
continue to drive `SteerToward`'s approach and its facing), but once the
Wingman is **engaged** — `distance ≤ standoff`, i.e. it has reached its
engagement range and is in the heat of the fight — `rangeThrottle` is computed
from `ComputeWingmanRangeThrottle(enemy)` and **overrides the fixed standoff-hold
THROTTLE** after `SteerToward` (facing/yaw/pitch untouched). Outside the
standoff the ship is still *approaching*, so the unchanged distance ramp closes
distance exactly as P162/166/169 already did.

**Why the engagement boundary is `distance ≤ standoff` rather than native's
literal `t < lifespan·0.45`/`angleToLead < π/2` gates (`:2492-2493`), stated
honestly:** P162's fightable test weapon has a projectile range of 2000 u
(P162's own log: "mounted weapon projectile range = 2000.0"), so native's heat
boundary `0.45·lifespan ≈ 900 u` sits **above** P162's approach spawn (d0 ≈ 420 u)
— a faithful-wide band would put an inert-target approach into the in-the-heat
match-speed hold (`targetDirSpeed=0 → Throttle −1`) and P162 would never close
or deal damage (an inert target is exactly the case that native routes through
its *outer approach band*, which P171 does not port). Since only band 1 exists
here, the approach phase must keep P162's ramp; the reduced heat boundary is
therefore the engagement standoff. This is a deliberate, stated further
simplification of `:2492-2493`, forced by the combination of a huge test weapon
range and the deliberate exclusion of the outer bands.

## 5. Explicit exclusions (each confirmed real, not silently dropped)

- **The other three distance/time bands** — the approach band
  (`t < lifespan·(openFireDistPercentage−0.05)`, `shipIGC.cpp:2515-2530`), the
  long-range-close band (`t < lifespan·1.5`, `:2531-2542`), and the
  give-up/re-target fallback (`:2544-2568`) — are **not** ported. Real, separate,
  larger follow-on work.
- **Afterburner triggers and `criticalMovement`** — `:2496-2497`, `:2501-2502`,
  `:2522-2525`, `:2537-2539`, `:2545-2567` — not ported; the port's
  `AfterburnerButton` state is untouched.
- **The target-re-acquisition fallback** (`ModelHasTargetPriority`,
  probe-scanning, `m_wingmanBehaviour & c_wbbmInRangeAggressive`, `m_timeRanAway`,
  `:2544-2568`) — confirmed absent in this port: there is no target-priority or
  probe-scanning model at all (re-affirmed against the P162/P166 target model,
  which is a single `AttackTarget` reference with no priority/probe notion).
- **`turnToFace` / skill-tiered turning** — excluded since P162; unchanged.

## 6. Verification

- **Baseline re-confirmed**: full suite is now **2812** tests (up from 2805 at
  P169 / prior stages).
- **3 consecutive full-suite runs**:
  - Run 1: 2812 total, 2 failures — both **documented flakes**
    (`LiveCombatWireTests.Real_two_party_combat_produces_a_kill_ship_over_the_wire`,
    `LiveSuicideShipDeleteTests.Combat_death_produces_ship_delete_to_same_audience`);
    each confirmed **green in isolation** afterwards.
  - Run 2: **clean 2812/2812**.
  - Run 3: **clean 2812/2812**.
- **Affected prior stages pass unmodified**: `NpcWingmanAttackLiveTests` (P162),
  `NpcWingmanDefendLiveTests` (P166), `NpcWingmanLeadLiveTests` (P169) all green
  (6 tests) with the new wiring in place — repeatedly.
- **New tests (5)** in `tests/Allegiance.Core.Tests/NpcWingmanRangeMaintenanceLiveTests.cs`:
  4 deterministic reflection discriminators on `Ship.ComputeWingmanRangeThrottle`
  (closing→−1, fleeing→+1, graduated match-speed exact value, formula bounds) and
  1 live loopback discriminator.
- **"Must fail if removed" proven**: temporarily disabling the throttle override
  made the live test's fleeing measurement collapse to −1.0 (the fixed
  standoff-hold value) — the fleeing assertion went red; restoring it returned
  green. Also confirmed the discriminator is stable across isolated reruns.

### The live discriminator, in short
A real Wingman engages an enemy pinned at a **fixed separation** (233 u) inside
the standoff, and the target's velocity is flipped between inbound (−120 u/s,
closing) and outbound (+120 u/s, fleeing). With separation constant, P162's
distance-only ramp would issue the **same** throttle for both; the port instead
produces `closing = −1.000` (back off) vs `fleeing = +1.000` (press in) — the
speed-driven response. Removing the wiring makes both −1.0 and the test fails.

Stats: `python3 ~/Development/allegiance-tools/session_stats.py 20260912_001855_845bba`

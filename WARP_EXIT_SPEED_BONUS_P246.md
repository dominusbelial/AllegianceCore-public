# Warp Exit-Speed Bonus Tuning (P246)

## What this is

A pure numeric tuning of the exit-speed bonus applied when a ship transits a
warp, brought in line with native's **real** constant. This is a gameplay-sense
correction: in the original game, entering an aleph at full throttle meant
emerging noticeably faster on the far side — hard to instantly reverse course —
which is why enemies stacked mines near warp exits. The mechanic was already
ported correctly; only the magnitude was a small stand-in.

## Relationship to native (verified against source)

Native `CFSShip::HitWarp` computes the exit velocity as

```
speed = -(speed + pwarp->GetMission()->GetFloatConstant(c_fcidExitWarpSpeed));
m_pShip->SetVelocity(backward * speed);
```
(`FedSrv/fsship.cpp:126-127`) — i.e. exit speed = **entry speed + a flat
bonus**, launched along the destination warp's backward axis.

The bonus constant is `c_fcidExitWarpSpeed = 3`
(`Igc/igc.h:62`), which the **mission constructor unconditionally seeds** with
`m_constants.floatConstants[c_fcidExitWarpSpeed] = c_fExitWarpSpeed;`
(`Igc/missionigc.cpp:2743`), where `c_fExitWarpSpeed = 15.0f`
(`Igc/missionigc.cpp:2688`). It is never read back from the loaded core at
runtime (the core's `DataConstantsIGC.FloatConstants` array is a separate
block), so the faithful value is exactly **15.0f** — the same reasoning this
repo already applies to `Mission.IncomePerPayday` and
`Mission.SecondsBetweenPaydays` (fixed mission constants, not core reads).

The port had the same formula structure at `ServerHost.cs:702`
(`sh.Velocity = backward * -(entrySpeed + ExitWarpSpeedBonus)`) but the
stand-in was `ExitWarpSpeedBonus = 5f` (`ServerHost.cs:625`). Changed to `15f`.

## Change

- `src/Allegiance.Server/ServerHost.cs:625` — `ExitWarpSpeedBonus` `5f` → `15f`;
  doc comment updated to cite native (`fsship.cpp:126`, `missionigc.cpp:2743/2688`)
  and drop the obsolete "exact native value not meaningful" framing.

No test encoded the old `5f` value or a derived exit speed — a grep of the test
tree found only direction-based cross-cluster tests (which position their
objectives well beyond the P238 exit clearance, so a larger exit speed doesn't
affect them) and the sector-change proofs in `WarpTransitServerTests`, none of
which assert the magnitude.

## New test + live proof

Added `WarpTransitServerTests.ShipExitSpeed_IsEntrySpeedPlusNativeBonus_AfterTransit`.
It boots a real server (XXV_simple.igc), spawns a ship **outside** the collision
volume with full-forward throttle facing the warp, and lets the ship's real
cruise-control carry it in at a steady **hull.Speed = 120** until the real
proximity pass transits it, then reads the server-side exit velocity:

```
P246-LIVE: steady-cruise entry speed 120.0 -> measured exit speed 135.0
           (native bonus 15 gives 135.0; old stand-in 5 would give 125.0)
```

Measured exit = **135.0 = 120 + 15**, exactly native. (Using full-throttle
cruise rather than an idle ship is deliberate: an idle ship's cruise target of
0 bleeds ~0.6 u of speed in the single tick before the transit reads it, which
would muddy the exact-number assertion.)

**Must-fail proven:** reverting `ExitWarpSpeedBonus` to `5f` makes the test red
(measured exit 125.0 vs expected 135.0), then green again after restore.

## Sanity check on magnitude

Typical top cruise speed in this port is 120 (e.g. the scout/fighter-class hull
used here). A full-throttle entry now exits at 135 — 12.5% above cruise, a
sharp but believable velocity fling, exactly native's design intent. Not absurd
for this port's speed scale; no further tuning applied.

## Out of scope (unchanged)

- P244 transit-trigger-radius fix and P245 screen-flash — untouched.
- Exit direction/jitter logic (`ServerHost.cs:695-718`) — already correct.
- Native reference source (`~/Development/Allegiance`) — read-only, untouched.

## Build / test

- Whole-solution build: **0 warnings, 0 errors.**
- `Allegiance.Core.Tests`: 2 clean full-suite runs → 3066 passed / 1 failed /
  3067 total. The single failure in both runs is the documented pre-existing
  `LiveTurretGunnerBindingTests.Board_binds_gunner_and_active_turret_update_roundtrips_live`
  machine-state flake (fails in isolation; unrelated to this constant change).
  Baseline was 3066; the +1 is the new `ShipExitSpeed_...` test.
- `Allegiance.ClientCore.Tests` / `Allegiance.ClientCore.MonoGame.Tests`:
  unchanged by this server-side constant change (build included them clean).

# P408 — ship↔asteroid collision HP-pool: deliberate decision needed (native's static branch is shield-only, port unified it with hull+shield)

## Why this task exists

Found by the same kimi-k3 3D/flight/controls/navigation/HUD review as
P404-P407 (full report:
`~/Development/allegiance-tools/reference/pi-3d-flight-review-kimik3-20260927.log`,
finding U2). **This is a decide-deliberately task, not a clear bug** —
low impact either way; the ask is an explicit, documented choice plus
an accurate code citation, not necessarily a behavior change.

## Confirmed divergence

`Sector.ResolveShipAsteroidCollision` (`src/Allegiance.Core/Model/
Sector.cs:2058`) sizes the ship's collision HP pool via
`CollisionHpPool` (`Sector.cs:2404`): hull HP **plus**
`ShieldFraction × MaxStrength` (an additive `+=` formula).

Native's *static*-collision branch (asteroid/station/building-effect,
`~/Development/Allegiance/src/Igc/shipIGC.cpp:949-953`) instead does
`hp1 = shieldFraction × maxStrength` when a shield is mounted — a
**replacement, not addition**. Native's *ship-ship* branch was later
changed from `=` to `+=` (the "terralthra fix … this was an = should be
a +=" comment at `shipIGC.cpp:1149-1151`), but the static branch kept
the original `=`. The port unified both collision passes onto the
*ship-ship* (additive) formula, applying it to the asteroid case too —
where native never made that change.

**Consequence, confirmed narrow**: `hp1` only scales the damage dealt
**to the asteroid** (`Sector.cs:2080-2084`) — damage to the ship itself
(scaled by the rock's `hp2`) is unaffected either way. So a shielded
ship ramming a rock currently damages the rock *more* in the port than
in native. Low gameplay impact.

`CollisionHpPool`'s own doc comment currently cites only the ship-ship
branch (`shipIGC.cpp:1147-1158`) — which is the *wrong* citation for
the asteroid call site regardless of which behavior is kept.

## Scope

1. **Decide, and document the reasoning plainly**: either (a) match
   native literally — give `ResolveShipAsteroidCollision` its own
   shield-only (`=`) pool calculation distinct from the ship-ship
   additive one, or (b) keep the current unified additive formula for
   both collision types and say explicitly in the report why (e.g.
   "simpler, one formula, narrow/low-impact divergence, native's own
   asteroid-branch behavior is arguably an inconsistency it never got
   around to fixing").
2. Regardless of which you choose, fix `CollisionHpPool`'s doc comment
   to cite the correct native line(s) for whichever call site(s) it's
   documenting — don't leave the wrong citation in either way.

## Explicitly out of scope

- Ship↔ship collision (`Sector.cs:2247-2357`) — already confirmed
  correct (the additive `+=` formula matches native's own later fix
  there), don't touch.
- Any other part of asteroid-collision handling (reflection, damage
  split, lifepod exemption, mining exclusion) — already confirmed
  correct by this same review.
- P368-P407 — unrelated, don't touch.

## Verification — real proof, not just build/test

- If behavior changes (option a): a test proving a shielded ship's
  collision HP pool in the asteroid case is shield-only, while the
  ship-ship case remains additive (both pools produce their own
  correct, distinct values — a regression test for ship-ship must
  still pass unchanged).
- If behavior is kept (option b): no behavior test needed, but the
  report must state the decision and reasoning explicitly, and the
  doc-comment fix must still land.
- Whole-solution build: 0 warnings, 0 errors.
- Full test suite, run at least 2x: only the pre-existing documented
  live-wire timing flakes expected.

## Deliverables

1. The decision (and any resulting fix), committed on `master` (not
   pushed — Domingo pushes).
2. Report doc `P408_ASTEROID_COLLISION_HP_POOL_DECISION.md` in the
   repo root: the decision made and why, the corrected doc citation,
   and the verification performed.

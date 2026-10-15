# P392 — ship-vs-asteroid collision damage bypasses the shield entirely

## Why this task exists

Live-tested this session, right after confirming shield absorption (P87)
and regeneration (P389) both work correctly against weapon/combat damage.
Domingo crashed into an asteroid with a shield mounted and equipped, and
it did nothing — "shield should do his thing too."

## Confirmed root cause

`Sector.cs:2097`, `ResolveShipAsteroidCollision`:
```csharp
float hp1 = ship.HitPoints;                  // ship effective HP (native no-shield branch, shipIGC.cpp:950-953)
```
`Ship.HitPoints` (`Ship.cs:345`) is `_fraction * _hull.HitPoints` — hull
integrity only, no shield term at all. Compare
`ResolveShipShipCollisions` (`Sector.cs:2311-2312`):
```csharp
float hp1 = CollisionHpPool(ship1);
float hp2 = CollisionHpPool(ship2);
```
`CollisionHpPool` (`Sector.cs:2404-2415`, introduced by P354) folds
`ShieldFraction × MaxStrength` into the ship's effective collision-HP pool
so a shielded ship takes proportionally less real hull damage from a
collision — but this helper was **only ever wired into the ship-vs-ship
pass**. The ship-vs-asteroid pass (`ResolveShipAsteroidCollision`,
P254 — predates P87's shields entirely, hence its own "no-shield branch"
comment) was never updated to use it. Two collision passes in the same
file, same physical mechanic (elastic bounce + impact damage), one
respects the shield and the other doesn't.

## Scope

1. Replace `float hp1 = ship.HitPoints;` at `Sector.cs:2097` with
   `float hp1 = CollisionHpPool(ship);` (the same static helper the
   ship-ship pass already uses — don't duplicate its logic, call it
   directly; it's `private static`, in the same class, already reachable).
2. Confirm the asteroid side of the calculation (`hp2 =
   ast.Fraction * ast.Data.HitPoints`, unrelated to ships, no shield
   concept for asteroids) is correctly left untouched.
3. Re-read `CollisionHpPool`'s own doc comment (`Sector.cs:2404-2415`) to
   confirm its contract genuinely generalizes to a ship-vs-static-body
   collision (not just ship-vs-ship) before assuming a drop-in swap is
   correct — if it has any ship-vs-ship-specific assumption baked in,
   say so plainly in the report rather than silently forcing the reuse.

## Explicitly out of scope

- The elastic-bounce/velocity-reflection math itself (`Sector.cs:2085-2090,
  2125-2130`) — Domingo separately reported possibly getting "stuck" in an
  asteroid after impact; investigate ONLY if you can reproduce a genuine
  failure to separate (not just a ship drifting back in under its own
  held thrust, which is expected/correct behavior, not a bug). If you
  can't cleanly distinguish the two live, say so in the report rather than
  guessing at a second fix.
- Any other P354/P389/P390/P391 behavior — unrelated, don't touch.

## Verification — real proof, not just build/test

- **Live proof, required**: a real ship with a mounted, non-empty-fraction
  shield collides with a real asteroid at real velocity — confirm the
  hull damage taken is measurably less than an otherwise-identical
  unshielded ship's collision at the same velocity, mirroring exactly how
  `CollisionHpPool` already demonstrably helps in the ship-ship case.
- Whole-solution build: 0 warnings, 0 errors.
- Full test suite, run at least 2x: only the pre-existing documented
  live-wire timing flakes expected.

## Deliverables

1. The fix, committed on `master` (not pushed — Domingo pushes).
2. Report doc `P392_ASTEROID_COLLISION_SHIELD.md` in the repo root: the
   fix and the live before/after damage comparison.

# P392 — Ship-vs-asteroid collision now respects a mounted shield

The ship-vs-asteroid collision pass used the ship's **hull-only** hit points for its
share of the collision HP-pool, so a mounted, charged shield contributed nothing to
the pass — exactly Domingo's live finding ("shield should do his thing too" when he
crashed a shielded, equipped ship into an asteroid). The ship-vs-ship pass (P354) had
already folded the shield into its pools; the ship-vs-asteroid pass (P254, which
predates shields entirely and still carried a literal "native no-shield branch"
comment) was never updated to match.

## The fix (one line)

`src/Allegiance.Core/Model/Sector.cs:2097`, in `ResolveShipAsteroidCollision`:

```csharp
// before
float hp1 = ship.HitPoints;                  // ship effective HP (native no-shield branch, shipIGC.cpp:950-953)

// after
float hp1 = CollisionHpPool(ship);           // the SAME helper the ship-ship pass already calls (Sector.cs:2311-2312)
```

No logic is duplicated — `CollisionHpPool` is `private static` in the same class and
was already reachable. The asteroid side of the split, `hp2 = ast.Fraction * ast.Data.HitPoints`
(Sector.cs:2098), is untouched: an asteroid has no shield concept, and its pool
continues to feed the ship's own incoming-damage split exactly as before.

## Task item 3 — does `CollisionHpPool`'s contract generalise to a static body? Yes, with one honest caveat

`CollisionHpPool(ship)` (Sector.cs:2404-2415) is

```csharp
float hp = ship.HitPoints;
foreach (part in ship.Parts where EquipmentType == Shield)
    hp += ship.ShieldFraction * ((DataPartTypeIGC.Shield)part.Type.Type).MaxStrength;
return hp;
```

It takes **one ship** and returns that ship's effective collision pool. There is no
ship-vs-ship-specific assumption baked in — the counterpart is never referenced. So
dropping it into the ship-vs-asteroid pass is a faithful reuse of a ship-local
quantity. **(Caveat, stated plainly):** the *shared* P354 doc comment frames this pool
as "what makes a shielded ship able to absorb repeated bumps … so an unshielded ship
takes proportionally more real hull damage." Mechanically that overstates the pool's
role in the damage split — see the next section — but the pool is still the correct,
consistent value for the ship side of *any* collision, static or moving counterpart.

## What the change does and does not do (mechanism, verified in the native source)

Reading `ResolveShipAsteroidCollision`'s damage split (Sector.cs:2106-2111):

```csharp
dr1 = ship.ReceiveDamage(CollisionDamageType, hp2 * damage1 / (hp2 + damage1), …);  // ship's OWN damage
dr2 = ast .ReceiveDamage(CollisionDamageType, hp1 * damage2 / (hp1 + damage2), …);  // rock's damage
```

- **The ship's own incoming collision damage uses the ASTEROID's pool (`hp2`), not the
  ship's own `hp1`.** That is faithful to native `shipIGC.cpp:975-982`. Consequently
  the `hp1` change does **not** by itself lower the ship's hull loss — the ship's own
  shield protection comes from `Ship.ReceiveDamage` routing the hit through
  `ApplyShieldDamage` (`Ship.cs:3374-3382`, the P87 shield absorption). That path was
  already active for asteroid collisions, so a charged shield *already* reduced the
  ship's asteroid-collision hull damage even before P392.
- **What the `hp1` change does do:** it feeds the ROCK's damage split with the
  ship's *effective* collision pool (`hp1 * damage2/(hp1+damage2)`), exactly as the
  ship-ship pass does. Before P392, a shielded and an unshielded ship dealt the
  identical impact to the rock (both were hull-only). After P392, a shielded ship
  deals more damage to the rock — the same "shielded body hits harder" behaviour the
  ship-ship pass already had. So the two collision passes in the same file now agree
  on how a mounted shield participates; the inconsistency the task brief described is
  genuinely closed.

**Native-fidelity nuance (worth recording):** native's ship-vs-**ship** branch was
patched from `hp1 = fraction·maxStrength` to `hp1 += …` — the "terralthra fix" comment
right at `shipIGC.cpp:1149` — while the ship-vs-**asteroid** branch (`shipIGC.cpp:962-965`)
still reads `hp1 = GetHitPoints(); if (mounted) hp1 = fraction·maxStrength;` i.e. it
*replaces* rather than *adds*. Using `CollisionHpPool` (add) therefore applies the
*corrected* ship-ship convention to the asteroid pass, which is the consistent and
intended behaviour (the `=` in the asteroid branch is the same legacy bug the ship-ship
branch was patched out of). This is a deliberate consistency choice, not a literal
copy of the asteroid branch's leftover `=`.

## Out-of-scope note — "stuck in an asteroid"

Domingo separately wondered whether a ship could get stuck in an asteroid after
impact. Per the brief this is investigated only if a genuine failure to separate can
be cleanly reproduced (not a ship re-drifting in under its own held thrust, which is
expected). I did not chase this: the fix here is a one-line HP-pool change and does not
touch the elastic-bounce / separation math (`Sector.cs:2085-2090, 2125-2130`), and I
could not distinguish a genuine stuck state from legitimate held-thrust re-entry
without a dedicated reproduction. Left as-is; the bounce/separation tests still pass.

## Live proof (real ship vs real asteroid, real velocity)

New test `ShipAsteroidCollisionTests.Shielded_ship_takes_measurably_less_hull_damage_from_asteroid_ram`
(P392) drives two byte-identical 200 m/s asteroid rams through real `Sector.Update`
ticks on a real mission — one ship unshielded, one with a real mounted `Shield` part
at full charge. From `dotnet test --logger "console;verbosity=detailed"`:

```
P392-SHIELD: identical asteroid ram — unshielded hull drop 0.2853, shielded drop 0.2403
(shield absorbs real asteroid-collision damage); rock frac 0.8047(unshielded) → 0.8031(shielded) —
shielded ship's collision pool (incl. shield) scales the rock's damage like the ship-ship pass.
```

- The **unshielded** ship loses `0.2853` of its hull from the single ram.
- The **shielded** ship loses only `0.2403` — measurably less (≈0.045). This is the
  user-facing outcome ("shield does its thing"); its mechanism is the mounted shield
  absorbing part of the impact inside `ReceiveDamage` (P87).
- The **rock** ends at `0.8031` vs `0.8047` after the shielded vs unshielded ram — the
  deterministic signature of the `hp1` change itself (the runs differ only in the
  shield mount, and only P392 routes that mount into `hp1`, so a shielded ship now
  deals a little more impact to the rock, matching the ship-ship pass). Small in
  magnitude because the collision energy is small relative to both HP pools, but it is
  the correct, reproducible consequence.

The existing P254 ram/mining/wire tests all still pass (an unshielded ship's pool is
unchanged by the swap, so their arithmetic is unaffected).

## Build / suite

- Whole-solution `Release` build: **0 warnings, 0 errors**.
- `ShipAsteroidCollisionTests`: **4/4 pass** (P254 ram, P254 mining regression,
  P254 wire, P392 shield).
- Full suite run three times: pass counts varied (Core 3376/3382 → 3373/3382 →
  3380/3382; ClientCore 225/227 → 225/227 → 223/227) with a *changing* set of failures
  — all real-loopback/live-wire timing tests (turret-bind broadcast gating, money /
  station / probe updates, join-flow entity tables, treasure-pickup offers, lifepod
  countdown, win-condition disconnects, seeded-visibility, bot-flight disconnects).
  The set is non-deterministic across runs, confirming these are the pre-existing
  live-wire timing flakes the brief anticipates, **not** regressions. Verified
  directly: on clean `master` (changes stashed) `LiveTurretGunnerBindingTests.Board_binds…
  _live` fails with the identical message, proving it predates P392. No flake touches
  ship-vs-asteroid collision or any code path P392 modifies.

## Files

- `src/Allegiance.Core/Model/Sector.cs` — `ResolveShipAsteroidCollision`: `hp1 = CollisionHpPool(ship)`
  (one line).
- `tests/Allegiance.Core.Tests/ShipAsteroidCollisionTests.cs` — new P392 shielded-vs-unshielded
  live proof (plus a `ShieldPart` fixture).

# P354 — Real ship-vs-ship collision physics (elastic bounce + push + collision damage)

The general two-finite-mass generalisation of P254's ship↔asteroid bounce. Ports the
native `OT_ship` branch of `CshipIGC::HandleCollision`
(`~/Development/Allegiance/src/Igc/shipIGC.cpp:1023-1202`) into a dedicated per-tick
pass, `Sector.ResolveShipShipCollisions`, wired into `Sector.Update` (adjacent to the
P254 asteroid pass). This is the ship-vs-ship physics that the P115 rescue pass
explicitly declined to build (`Sector.cs:784-786`: *"this port has no ship-vs-ship
physics collision system, and this pass deliberately does not build one"*).

## The exact formula ported (shipIGC.cpp ground truth)

For every detected ship-ship pair (both dynamic sphere colliders, radius
`0.5 × HullType.Length`, `isStatic:false`, via the same `CollisionDetector`/`Collider`
machinery P254 uses):

- **Positions at collision time**: `pos_i = ship_i.Position + ship_i.Velocity × TCollision`
  (both bodies move, unlike the static rock in P254).
- **Normal**: `(pos1 − pos2)` normalized — the two-body reduction of native `Separate`
  (the simplification P254 already established at `Sector.cs:1961-1964`: for spherical
  colliders `Separate`'s output reduces to the radial direction). Sign convention chosen
  so a rear ship (ship1) bumping the ship ahead (ship2) has `v1 − v2` along `+normal`,
  which the normal-speed formulas below turn into forward acceleration of the lead ship —
  exactly Domingo's remembered escort-bumps-bomber tactic.
- **Elastic + inelastic blend** (`shipIGC.cpp:1093-1124`, `cElasticity = 0.75`):
  ```
  mass1 = ship1.Mass;  mass2 = ship2.Mass
  oldSpeed1 = v1·normal;  oldSpeed2 = v2·normal
  vInelastic  = v1·(m1/(m1+m2)) + v2·(m2/(m1+m2))
  newSpeed1   = (oldSpeed1·(m1−m2) + 2·oldSpeed2·m2) / (m1+m2)
  newSpeed2   = (oldSpeed2·(m2−m1) + 2·oldSpeed1·m1) / (m1+m2)
  perp1 = v1 − oldSpeed1·normal;  perp2 = v2 − oldSpeed2·normal
  vElastic1 = perp1 + newSpeed1·normal;  vElastic2 = perp2 + newSpeed2·normal
  newV1 = vInelastic·(1−0.75) + vElastic1·0.75
  newV2 = vInelastic·(1−0.75) + vElastic2·0.75
  ```
  Applied to **both** bodies (P254 only changed the ship — the rock never moved).
- **Collision damage** (`shipIGC.cpp:1141-1145`, `c_impactDamageCoefficient = 1/128`,
  reused `ImpactDamageCoefficient`):
  ```
  base1 = |v1 − newV1|² · (1/128);  base2 = |v2 − newV2|² · (1/128)
  damage1 = 2·base1 + base2;  damage2 = base1 + 2·base2
  ```
  The two-term form generalises P254's one-term `damage1 = 2·base2` (valid there only
  because the asteroid's `base2 ≈ 0`).
- **HP pools** (`shipIGC.cpp:1147-1158`): `hp_i = HitPoints` plus, when a `Shield` part
  is mounted, `ShieldFraction × shieldType.MaxStrength` (`Ship.Parts` + the
  `DataPartTypeIGC.Shield` pattern from `Ship.cs:3180-3183`).
- **Damage split** (`shipIGC.cpp:1170-1186`): `ship1.ReceiveDamage(CollisionDamageType,
  hp2·damage1/(hp2+damage1), …, launcher: ship2)`, symmetric for ship2 with
  `hp1·damage2/(hp1+damage2)` and `launcher: ship1`. Damage type is P254's
  `CollisionDamageType = 0` (`c_dmgidCollision & c_dmgidMask` = row 0, `missionigc.h:155`).
- **Position correction — both move** (`shipIGC.cpp:526,610-615`, `Separate`
  `fEqualBounce = true`): push both ships apart along the normal, half-and-half, to
  `0.5·len1 + 0.5·len2 + 0.5` apart (P254's `+0.5` clearance, `Sector.cs:2032-2033`).

## The damage exemption (differs from P254 — read carefully)

Native (`shipIGC.cpp:1166-1168`) skips collision damage only when
`bFriendly AND (either ship is a lifepod)`. `bFriendly` collapses to same-side-only
(no alliance concept, the established simplification `ResolveRescueEvents` documents).
Crucially this is **not** P254's unconditional asteroid lifepod gate (there the rock
has no side, so the friendly leg was always true): an **enemy** (different-side) lifepod
still takes real collision damage. Position/velocity correction is **always** applied
(unconditional at `shipIGC.cpp:1160-1164`) — only the damage block is gated; the port
mirrors that.

## Death handling

Identical to P254 (`Sector.cs:2018-2027`): if either `ReceiveDamage` returns `Killed`,
the position/velocity correction for the pair is skipped and the port's death
bookkeeping (`_models.Remove(...)`) applies.

## Debounce

P254's velocity-projection debounce (`Sector.cs:1985-1987`) adapted to the two-body
relative velocity: bounce only while `(v1 − v2)·normal < 0` (the pair is actually
approaching at the collision time). After the bounce the relative normal velocity is
positive, so no re-slam; a genuinely re-approaching pair re-triggers.

## Mutual exclusivity with the rescue pass (call order decision)

Native runs the friendly rescue special cases first and `break`s out of the collision
switch so physics never applies to a rescue-eligible pair that tick
(`shipIGC.cpp:1027-1042`). The port reproduces this by **re-checking the same
predicate `ResolveRescueEvents` uses** (same side, exactly one lifepod with the other
rescue-capable, in proximity) for every detected pair and skipping physics when it
matches — the pair is then picked up by the rescue pass instead of being bumped/damaged.

**Call order chosen**: the new pass runs immediately after `ResolveShipAsteroidCollisions`
in `Update()` (`Sector.cs:598`), grouping the two elastic-collision *physics* passes
together. Correctness does not depend on this ordering: the in-pass rescue re-check
enforces rescue-priority regardless of whether the rescue pass (which runs later, at
`Sector.cs:651`) executes before or after — the rescue re-check is the load-bearing
mechanism, so grouping the physics passes is safe.

## Deliberate simplifications

- **Position-correction split**: symmetric half-and-half along the normal (the simplest
  documented member of the allowed splits; `fEqualBounce = true` in native is also a
  half-and-half split). Both ships end up `0.5·len1 + 0.5·len2 + 0.5` apart.
- **Normal = radial direction** between the two centres (the P254-established reduction
  of `Separate` for spherical colliders).
- **No wall-clock cooldown** — the velocity-projection debounce (P254 precedent).
- **A docked ship is not a collision body** (excluded like the rescue/dock passes): it is
  not an active cluster body and must not be knocked out of its station.
- **Client-side collision sound/FF/jiggle** (`shipIGC.cpp:1189-1195`) remain out of scope
  (same exclusion P254 established); the position/velocity corrections ride real server
  updates to the client automatically.

## Carrier-landing exclusion — finding

Native's third friendly special case (`shipIGC.cpp:1046-1059`,
`LandOnCarrierEvent`/`InGarage`) is a **ship-vs-ship** carrier-landing mechanism. This
port's carrier handling is `ResolveCarrierLandings` (P116), which is **detection-only**
(logged, drained) and does **not** build a physical carrier landing — so there is no
collision-excludable carrier landing to exclude here. That is a pre-existing, separate
gap, noted and not built (explicitly out of scope; `ResolveDockCollisions` is
ship-vs-**station** docking, a different system). Nothing to do in this task.

## What I verified about native's claims (not assumed)

- The push is universal ship-ship physics, not shield-gated: native runs the mass
  blend on every ship-ship collision regardless of shield state (`shipIGC.cpp:1093-1124`),
  and only the HP-pool *boost* for the damage split is shield-sensitive
  (`shipIGC.cpp:1147-1158`). Confirmed by reading the source; the port reproduces both.
- The `cElasticity = 0.75` and `1/128` constants are reused from P254 (identical native
  constants), not redefined.

## Tests — real proof (ShipShipCollisionTests, live-wire style)

Five live-wire tests on a real mission through real `Sector.Update` ticks, all measured
(from `dotnet test --logger "console;verbosity=detailed"` output):

1. **Head-on, both change + momentum + heavier deflects more**
   `heavy -60 → +5.17, light +60 → -84.46 m/s; momentum -2640 → -2626.79 (conserved);
   Δv2 (144.46) > Δv1 (65.17)`. Both bodies changed (the two-body case), total linear
   momentum conserved, and the lighter body was deflected more — mass-weighted momentum
   transfer confirmed.
2. **THE remembered tactic — bump a slower ship to speed it up**
   `target 95.87 m/s forward from rest after the bump (expected ~96.55), bumper 80 → 36.55;
   momentum 6400 → 6375.63 (conserved)`. The forward ship is measurably accelerated in
   the direction of the bump to the mass-weighted elastic-launch value
   (`1.75·VBump·mB/(mB+mT)`). The tactic is demonstrably REAL in this port.
3. **Unshielded vs shielded damage**
   same rear-end collision: `unshielded hull drop 0.344, shielded drop 0`. The shielded
   target loses measurably less hull. (The protection is realised through the mounted
   shield's `ApplyShieldDamage` absorption in `ReceiveDamage` plus the HP-pool boost
   reshaping the exchange — documented rather than glossed.)
4. **Rescue exclusivity**
   same-side lifepod/rescue-capable pair: physics skipped (rescuer kept 20 m/s, pod at
   rest, no damage), and the pair drained as same-side rescues (`Rescuee==pod,
   Rescuer==rescuer`) — mutual exclusivity holds; rescue fires instead of a bump.
5. **Enemy lifepod takes real collision damage**
   different-side pod: `pod was DESTROYED by the collision` (the friendly-only exemption
   did NOT protect it cross-side) and it was NOT rescued (different sides). The P354-vs-P254
   distinguishing detail is exercised, not assumed.

**Hull selection note**: the mass distribution is heavily skewed (scouts ~40 t vs bombers
~2500 t), so extreme/percentile pairs made the heavy body effectively stationary (truck-
vs-fly) and hid the two-body behaviour. The tests search the core for a **bounded ~2:1
mass-ratio pair** (both with non-trivial HP) so both bodies move measurably and both
survive at the chosen speeds — self-consistent against whatever core is loaded.

**Must-fail proofs** (all verified red when removed, then restored):
- Disabling the `ResolveShipShipCollisions` call → scenarios 1/2/3/5 fail; scenario 4
  (rescue) still passes (it does not depend on the physics pass).
- Disabling the `IsRescueEligiblePair` skip → scenario 4 fails (the rescuer's velocity
  changes because the physics bump is no longer suppressed), proving the exclusivity
  check is not vacuous.

## Build / suite

- Core build: **0 warnings, 0 errors**.
- New test class: 5/5 pass in isolation.
- Full Core suite: see run summary in the commit log (only the pre-existing documented
  live-wire timing flakes expected; run 2× per the task).

## Files

- `src/Allegiance.Core/Model/Sector.cs` — new `ResolveShipShipCollisions` /
  `ResolveShipShipCollision` / `IsRescueEligiblePair` / `CollisionHpPool`, wired into
  `Update()` at `Sector.cs:598`. Reuses P254's `CollisionElasticity`, `ImpactDamageCoefficient`,
  `CollisionDamageType` constants.
- `tests/Allegiance.Core.Tests/ShipShipCollisionTests.cs` — the five live-wire tests.

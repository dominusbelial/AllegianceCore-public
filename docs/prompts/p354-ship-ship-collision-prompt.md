# P354 — Real ship-vs-ship collision physics (elastic bounce + push + collision damage)

## Why this task exists

Domingo remembered a real native tactic: a faster escort ship could
shield-bump a slower bomber to accelerate its bomb run. Verified
against the native source this session (not assumed):

- **The push is universal ship-ship physics, not a shield-specific
  mechanic.** `CshipIGC::HandleCollision`, the `OT_ship` case
  (`~/Development/Allegiance/src/Igc/shipIGC.cpp:1023-1202`), runs a
  real mass-weighted collision blend on **every** ship-ship collision
  and sets new velocities on **both** ships (`shipIGC.cpp:1160-1164`).
  It is not gated by shield state — it fires whenever two ships touch.
- **Shields make the tactic survivable, they don't gate the push.**
  Each ship's effective HP pool used for the collision-damage split is
  boosted by `shield.Fraction * shield.MaxStrength` when a shield is
  mounted (`shipIGC.cpp:1148-1158`). A shielded ship can absorb being
  bumped repeatedly without dying; an unshielded one takes real hull
  damage from the same collision.
- **The port has no ship-vs-ship physics at all, and says so
  explicitly.** `src/Allegiance.Core/Model/Sector.cs:780-789` (the doc
  comment on `ResolveRescueEvents`, P115): *"this port has no
  ship-vs-ship physics collision system, and this pass deliberately
  does not build one"* — it only handles the rescue-pairing special
  case via a simplified point-in-sphere proximity check, explicitly
  citing `shipIGC.cpp:1022-1058` as the native branch it stands in
  for. Confirmed via grep: no mass-weighted elastic blend exists
  anywhere for ship-vs-ship today.
- **This is not a from-scratch build — there is a direct, working
  precedent already in this codebase.** P254's
  `ResolveShipAsteroidCollisions`/`ResolveShipAsteroidCollision`
  (`Sector.cs:1919-2035`) already ports the *same* elastic-collision
  math, the same `cElasticity = 0.75` constant, the same
  `CollisionDetector`/`Collider`/`CollisionEvent` machinery, and the
  same damage-application pattern — just for one **static, infinite-
  mass** body (a rock). This task generalizes that exact pattern to
  two finite masses, which is what the native formula already handles
  (`shipIGC.cpp:1093-1124`) — the two-body math is not a new
  derivation, it's sitting right there in the native file.

## The exact native formula (ground truth — cite these lines, don't re-derive)

All from `~/Development/Allegiance/src/Igc/shipIGC.cpp`:

- **Friendly-only special cases run FIRST and can pre-empt physics**
  (lines 1027-1060): same-side rescue (lifepod ↔ rescue-capable
  partner) and same-side carrier landing each `break` out of the
  switch case entirely if they fire — physics never applies to that
  pair this tick. Different-side pairs, or same-side pairs where
  neither special case matches, fall through to plain physics
  (lines 1062+) below.
- **Elastic + inelastic blend** (lines 1093-1124):
  ```
  mass1 = ship1.Mass; mass2 = ship2.Mass
  oldSpeed1 = velocity1 · normal;  oldSpeed2 = velocity2 · normal
  vInelastic = velocity1 * mass1/(mass1+mass2) + velocity2 * mass2/(mass1+mass2)
  newSpeed1 = (oldSpeed1*(mass1-mass2) + 2*oldSpeed2*mass2) / (mass1+mass2)
  newSpeed2 = (oldSpeed2*(mass2-mass1) + 2*oldSpeed1*mass1) / (mass1+mass2)
  perpendicular1 = velocity1 - oldSpeed1*normal;  perpendicular2 = velocity2 - oldSpeed2*normal
  vElastic1 = perpendicular1 + newSpeed1*normal;  vElastic2 = perpendicular2 + newSpeed2*normal
  cElasticity = 0.75
  newVelocity1 = vInelastic*(1-cElasticity) + vElastic1*cElasticity
  newVelocity2 = vInelastic*(1-cElasticity) + vElastic2*cElasticity
  ```
  `normal` is the separating-plane normal between the two ships (see
  Position correction below).
- **Collision damage** (lines 1141-1145, `c_impactDamageCoefficient =
  1/128` same as P254's `ImpactDamageCoefficient`):
  ```
  base1 = |velocity1 - newVelocity1|² * (1/128)
  base2 = |velocity2 - newVelocity2|² * (1/128)
  damage1 = 2*base1 + base2      // NOTE: two-term, unlike P254's
  damage2 = base1 + 2*base2      // simpler one-term version (there base2≈0, the rock doesn't move)
  ```
- **HP pools for the damage split** (lines 1147-1158): `hp1 =
  ship1.HitPoints` (+ `ship1.ShieldFraction * shieldType.MaxStrength`
  if a Shield part is mounted — same pattern
  `Ship.cs:3180-3183` already uses: `_parts.FirstOrDefault(p =>
  p.EquipmentType == EquipmentType.Shield)`, then
  `shieldPart.Type.Type is DataPartTypeIGC.Shield shieldType`, then
  `shieldType.MaxStrength`), same for `hp2`/ship2.
- **Damage split** (lines 1170-1186): `ship1.ReceiveDamage(collision
  type, hp2*damage1/(hp2+damage1), ...)`, symmetric for ship2 with
  `hp1*damage2/(hp1+damage2)`. Collision damage type is the same
  `CollisionDamageType = 0` constant P254 already defined
  (`Sector.cs:1952`, native `c_dmgidCollision` masked = row 0) — reuse
  it, don't redefine.
- **Damage exemption is DIFFERENT from P254's asteroid version — read
  this carefully** (lines 1166-1168): native skips damage only when
  `bFriendly AND (either ship is a lifepod)` — i.e. a friendly lifepod
  bump is damage-free, but an enemy lifepod still takes real collision
  damage. P254's asteroid version exempts *any* lifepod unconditionally
  (asteroids have no side, so the friendly leg was always true there).
  Do not copy P254's simpler check — implement the real two-part
  native condition.
- **Position correction — both ships move, not just one** (lines
  1072-1076, `Separate()` at `shipIGC.cpp:514-608`): native's
  `Separate` passes `fEqualBounce = true` for ship-ship (comment at
  line 526: *"don't bother if object 2 is static"* — the false branch,
  used by the asteroid case). `Separate`'s real body is a general
  convex-hull GJK-style extreme-point search — **not** meant to be
  ported literally. P254's own doc comment
  (`Sector.cs:1961-1964`) already established that for this port's
  simplified sphere-collider model, `Separate`'s output reduces to the
  plain radial direction between the two centres. Apply the same
  simplification here: `normal` = direction from ship2's centre to
  ship1's centre (or vice versa — be consistent with the sign
  convention used in the velocity math above), and since both bodies
  are dynamic here (unlike P254), **push both ships apart** along that
  normal so they end up `0.5*ship1.HullType.Length +
  0.5*ship2.HullType.Length + 0.5` apart (the same `+0.5` clearance
  constant P254 uses, `Sector.cs:2032-2033`) — split the correction
  however is simplest (symmetric half-and-half is a reasonable,
  documentable choice; note whatever you pick).
- **Debounce**: reuse the exact same velocity-projection debounce
  P254 uses (`Sector.cs:1985-1987`: only bounce while at least one
  ship is actually approaching, `v_relative · normal < 0`, not a
  wall-clock cooldown) — adapt it to relative velocity
  `(velocity1 - velocity2) · normal < 0` since both bodies move here.
- **Death handling**: same as P254 (`Sector.cs:2018-2027`) — if either
  `ReceiveDamage` call returns `Killed`, skip the position/velocity
  correction for that pair and do this port's existing death
  bookkeeping (`_models.Remove(...)`) instead.

## Scope

1. Add a new per-tick pass, `ResolveShipShipCollisions`, in
   `Sector.cs`, following the exact structure and doc-comment density
   of `ResolveShipAsteroidCollisions`/`ResolveShipAsteroidCollision`
   (`Sector.cs:1919-2035`) — reuse `CollisionDetector`/`Collider`
   (both ships `isStatic: false`, `HitShape.Sphere`, radius
   `0.5*HullType.Length`, exactly like the existing ship colliders in
   that pass) to detect ship-ship pairs each tick.
2. Implement the mass-weighted elastic/inelastic blend, damage
   calculation, HP-pool shield boost, damage exemption, and
   two-body position correction exactly as specified above, citing
   the same native line numbers in your own doc comments (this
   project's established citation discipline — every non-obvious
   choice gets a `shipIGC.cpp:NNNN` pointer).
3. **Mutual exclusivity with the existing rescue pass**
   (`ResolveRescueEvents`, `Sector.cs:819-875`): native's rescue check
   runs first and pre-empts physics via `break` for that pair
   (lines 1027-1042). Reproduce this by re-checking the SAME predicate
   `ResolveRescueEvents` already uses (same side, exactly one lifepod
   with the other rescue-capable, in proximity) for each detected
   collision pair in the new pass, and skip physics if it matches —
   don't let a rescue-eligible pair also take a collision bump/damage
   the same tick. Decide and document the call order in `Update()`
   (`Sector.cs:574` onward) — running the new pass adjacent to
   `ResolveShipAsteroidCollisions` (line 598) or `ResolveRescueEvents`
   (line 651) are both reasonable; pick one and say why.
4. Wire the new pass into `Update()`.

## Explicitly out of scope

- **Carrier-landing collision exclusion** (native's third friendly
  special case, `shipIGC.cpp:1046-1059`, `LandOnCarrierEvent`/
  `InGarage`). Check whether this port has an existing ship-vs-ship
  (not ship-vs-station — `ResolveDockCollisions` is station docking,
  a different system) carrier-landing mechanism; if one exists,
  exclude those pairs too the same way as rescue. If none exists,
  that's a pre-existing, separate gap — do not build carrier landing
  in this task. Note what you found either way.
- **Client-side collision sound/force-feedback/jiggle effects**
  (native `shipIGC.cpp:1189-1195`) — same exclusion P254 already
  established (`Sector.cs:1915`), a separate task; position/velocity
  corrections ride real server updates to the client automatically.
- **Any change to `ResolveShipAsteroidCollisions` or
  `ResolveRescueEvents`** themselves — this task adds a new pass
  alongside them, it does not refactor either.
- **Alliance/`AlliedSides`** — this port has no alliance concept
  (the established simplification every prior friendly-gate task in
  this arc uses); `bFriendly` collapses to same-side-only, exactly as
  `ResolveRescueEvents` already does.

## Verification — real proof, not just build/test

- **New tests, live-wire style, matching this project's existing
  `CombatDamageTests`/`CombatShieldTests`/`AsteroidCombatTests`
  conventions** (find and follow their actual pattern, don't
  invent a new one): at minimum —
  - Two ships of different mass on a collision course: confirm both
    velocities change post-collision per the formula above (not just
    one, unlike the asteroid case), and confirm the direction/relative
    magnitude match what mass-weighted momentum conservation predicts
    (heavier ship deflects the lighter one more).
  - **The actual tactic Domingo remembered**: a faster/heavier ship
    bumping a slower one measurably increases the slower ship's
    forward speed in the direction of the bump — assert this
    concretely (a real velocity-magnitude or projected-speed
    assertion, not just "no exception thrown").
  - An unshielded ship takes real collision damage; a shielded ship
    (same collision) takes proportionally less, per the HP-pool boost.
  - A same-side lifepod/rescue-capable pair in proximity still
    triggers rescue, NOT a physics bump (confirm mutual exclusivity
    holds).
  - An enemy (different-side) lifepod colliding with another ship
    takes real collision damage (the friendly-only exemption does
    NOT apply cross-side) — this is the specific detail that differs
    from P254's asteroid version, make sure it's actually tested, not
    assumed.
- Whole-solution build: 0 warnings, 0 errors.
- Full test suite (`/home/domingo/.dotnet/dotnet test`), run at least
  2x: only the pre-existing documented live-wire timing flakes are
  expected (failing test names vary run to run).

## Deliverables

1. The new `ResolveShipShipCollisions` pass in `Sector.cs`, wired into
   `Update()`, plus new tests, committed on `master` (not pushed —
   Domingo pushes). Do not create a side branch.
2. Report doc `P354_SHIP_SHIP_COLLISION.md` in the repo root (this
   project's established convention — see `P352_MESHY_GT_MODELS.md`
   for the shape/depth expected): the exact formula ported, every
   deliberate simplification made (position-correction split, call
   order vs. the rescue pass, whatever else), what you found (or
   didn't) for carrier-landing exclusion, and the concrete test
   results proving the push/damage/shield/rescue-exclusivity
   behaviors all actually work — not just "tests pass."
3. A short final summary confirming build/test status and that the
   actual remembered tactic (bump a slower ship to speed it up) is now
   demonstrably real in this port.

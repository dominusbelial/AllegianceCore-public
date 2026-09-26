# P364 — Weapon impact spark effect (fires on every hit, distinct from damage-vent smoke)

## Why this task exists

Systematic sweep of native `~/Development/Allegiance/src/effect/thinggeo.cpp`
(following the same-file pattern that already found 3 real missing
effects tonight: engine exhaust particles/P358, ribbon motion trail/
P363, and the already-correctly-ported damage-vent smoke) found a 4th
real, distinct, currently-missing effect.

**Native mechanism**: `AddHullHit(const Vector& vecPosition, const
Vector& vecNormal)` (`thinggeo.cpp:1324-1345`):
```
m_phullHitGeo->AddHullHit(vecPosition, vecNormal);   // a separate geo call, not traced further — investigate

float size   = m_radius / 2;
float speed  = 0.4f * size;
float spread = 0.25;
Vector vecWorldPosition = m_vecPosition + vecPosition;

for (int index = 0; index < 2; index++) {
    m_pbitsGeo->AddBit(
        vecWorldPosition,
        speed * Vector(
            vecNormal.X() + random(-spread, spread),
            vecNormal.Y() + random(-spread, spread),
            vecNormal.Z() + random(-spread, spread)
        ),
        size
    );
}
```

This fires on **every single weapon hit that connects** — not gated
by hull-fraction/damage-vent thresholds like `AddDamage`
(`thinggeo.cpp:1358+`, already correctly ported as
`DamageDebrisParticles.cs`). Two small debris "bits" launch from the
exact impact position, roughly along the surface normal with a bit of
random spread, sized to half the ship's radius. This is almost
certainly one of the most commonly-seen visual effects in the whole
game — every laser/gun hit that lands should produce this.

**Confirmed missing from this port**: no `HullHit`/`ImpactSpark`/
`HitSpark`/similar concept found anywhere in
`Rendering/`. This is a genuinely separate system from the already-
ported damage-vent smoke.

## Scope

1. **Trace `m_phullHitGeo->AddHullHit(...)`** — this task's excerpt
   only shows the `m_pbitsGeo->AddBit` debris-bit half; there's a
   SEPARATE geo call (`m_phullHitGeo`) right before it that this
   summary hasn't traced. Read what that actually does in
   `effect/efgeo.cpp` (or wherever `HullHitGeo`/similar is defined)
   before assuming the "2 debris bits" is the WHOLE effect — there may
   be a second visual component (e.g. a hit-flash decal) bundled with
   this call that also needs porting.
2. **Find where `AddHullHit` gets CALLED from** — trace back to the
   real weapon-hit-resolution code (likely `shipIGC.cpp`'s combat/
   damage-application path, near wherever `ReceiveDamage` or the
   weapon-fire collision resolution lives) to confirm the exact
   trigger condition (every hit that connects, on the DEFENDER's hull,
   presumably — confirm rather than assume) and what position/normal
   data is actually available at that call site.
3. **Build the port equivalent**: a real particle-bit effect fired at
   the moment a weapon shot connects, launched from the real impact
   position along the real hit-surface normal (with the same random
   spread), sized relative to the target ship's radius. Reuse existing
   particle-rendering infrastructure (`DamageDebrisParticles.cs`/
   `MissileTrailParticles.cs`'s shared patterns) rather than building
   a whole new rendering pipeline from scratch — this is structurally
   similar (spawn some particles at a position/direction, age them,
   draw them), just a different trigger and visual parameters.
4. Confirm this fires for weapon hits on ANY visible ship the client
   knows about (attacker's own shots landing on a remote target,
   AND remote combat the local player is merely observing), matching
   the same "works for remote ships too" bar every other effect
   tonight has been held to.

## Explicitly out of scope

- Damage-vent smoke (`AddDamage`, already correctly ported) — don't
  touch `DamageDebrisParticles.cs`'s existing behavior, this is a
  genuinely separate, additional effect that fires MORE often (every
  hit, not just while a hull-fraction threshold is crossed).
- P358 (engine exhaust), P359 (target readout), P360 (mini-map), P361
  (auto-reload), P362 (weapon status HUD), P363 (side-color trail) —
  unrelated, don't touch.
- P354-P357 — unrelated, already landed, don't touch.

## Verification — real proof, not just build/test

- **Live proof, required**: real combat (weapon fire connecting on a
  real target, e.g. via `Allegiance.Bot`/`vizfight`'s combat harness or
  a real client), confirm a real, visible spark/debris effect appears
  at the actual impact point on every landed hit, distinct from (and
  in addition to) the existing damage-vent smoke stream. Confirm it
  works for a remote target too, not just the local player's own hits.
- Whole-solution build: 0 warnings, 0 errors.
- Full test suite (`/home/domingo/.dotnet/dotnet test`), run at least
  2x: only the pre-existing documented live-wire timing flakes are
  expected.

## Deliverables

1. The weapon impact spark effect, committed on `master` (not pushed
   — Domingo pushes). Do not create a side branch.
2. Report doc `P364_WEAPON_IMPACT_SPARKS.md` in the repo root: what
   `m_phullHitGeo->AddHullHit` actually turned out to do (the traced
   detail this prompt couldn't complete), where the real trigger call
   site lives natively, what was built, and the live verification
   performed.

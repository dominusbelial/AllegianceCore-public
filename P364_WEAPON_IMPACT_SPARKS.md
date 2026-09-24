# P364 — Weapon impact spark effect (fires on every hit, distinct from damage-vent smoke)

## Summary

Ported native `ThingGeo::AddHullHit` (`src/Allegiance/src/effect/thinggeo.cpp:1324-1345`)
as a client visual effect that fires a bright 2-debris-bit **impact spark** at the
exact point a projectile strikes a damagable target, launched along the real
hit-surface normal with the native random spread, sized to half the target's
radius. It fires on **every landed weapon hit** (not gated by the damage-vent
hull-fraction thresholds that drive the already-ported `DamageDebrisParticles.cs`
smoke), and is delivered to every client that can see the shot — including a
client merely *observing* remote combat it did not participate in.

---

## 1. What `m_phullHitGeo->AddHullHit` actually turned out to do (traced detail)

Native `ThingGeo::AddHullHit` (`thinggeo.cpp:1324-1345`) drives **two** distinct
visual sub-effects from the same impact data (position + normal):

1. **The decal component — `m_phullHitGeo->AddHullHit(vecPosition, vecNormal)`**
   (`thinggeo.cpp:1326`), implemented by `HullHitGeoImpl` (`effect/efgeo.cpp:674-749`).
   It pushes a `HullHitData` entry (position, normal, derived forward/right axes)
   and, each render frame, draws **two flat surface decals** at the impact point:
   a *tangent* decal (`AWF_EFFECT_TANGENT` surface) oriented by `forward`×`right`,
   and a *normal* decal (`AWF_EFFECT_NORMAL` surface) offset along the normal.
   Both are solid white (`Color(1,1,1)`), scale up from `0.1` to a `maxSize = 2.0`
   over `0.19`s (`scale = 0.1 + 2.0·5.0·(t − tStart)`), then the entry is removed
   (`efgeo.cpp:704-747`). This is a short-lived "hit flash" painted **on the struck
   model's mesh surface**.

2. **The debris-bits component — `m_pbitsGeo->AddBit(...)` × 2** (`thinggeo.cpp:1334-1344`),
   implemented by `BitsGeoImpl` (`effect/efgeo.cpp:798-846`). It spawns **two small
   particles ("bits")** from the world impact point
   (`vecWorldPosition = target.worldPos + localImpactOffset`), each launched with
   velocity `speed · (normal + random(−spread, spread) per axis)` where
   `speed = 0.4 · size`, `size = m_radius / 2` (half the target ship's radius),
   `spread = 0.25`. Each bit flies straight at constant velocity
   (`position = origin + velocity · dtime`, `efgeo.cpp:836`) for the native bit
   lifetime `g_bitsLifeTime = 2`s (`efgeo.cpp:764`), drawn scaled to `m_size`.

**Port decision.** The MonoGame renderer is a **billboard/particle-only pipeline** —
it has no projected-decal pass that paints onto an imported ship mesh. The decal
component is therefore documented here and **not ported** (a deliberate, disclosed
substitution), and the **bits** — the prominent "two small debris bits launched from
the exact impact position" the task names — are ported faithfully:
`Rendering/HullHitSparkParticles.cs` (pure pool) + `Rendering/HullHitSparkRenderer.cs`
(bright warm-white additive billboards). This is the same "reuse the particle
infrastructure rather than build a new render path" discipline the task requested.

---

## 2. Where the real trigger call site lives natively

The weapon-hit path is **`CprojectileIGC::HandleCollision`** (`src/Igc/projectileIGC.cpp:161-231`).
On impact with a non-mine target it computes the round and target positions at the
collision instant (`position1` / `position2`, `projectileIGC.cpp:174-175`), calls
`ReceiveDamage`, then fires `AddHullHit` under a strict gate
(`projectileIGC.cpp:220-226`):

```cpp
else if (fExplosion && ((type & c_dmgidNoDebris) == 0))
{
    Vector pos = position1 - position2;
    pModel->GetThingSite()->AddHullHit(pos, pos.Normalize());
}
```

where `fExplosion = (dr <= c_drHullDamage) && visible && inCluster`
(`projectileIGC.cpp:193`), `c_drHullDamage = 2` (igc.h:47-51), and this is the
**else-of-the-blast** branch (the round has no blast power — an area-effect round
creates a real explosion instead). So the effect fires when a **projectile** (a
missile does not take this path — `missileigc.cpp` has its own blast VFX):

- deals hull-or-worse damage (`dr ≤ HullDamage`),
- has `BlastPower == 0`,
- its damage type lacks the `c_dmgidNoDebris` flag (`0x40`, `igc.h:560`).

The impact **world normal** is `normalize(position1 − position2)` — the direction
from the target's centre toward the incoming round — and the **world impact point**
is `position1`. This port replicates that gate verbatim in
`Model/Sector.cs::MaybeRaiseProjectileHit` (raised from `ResolveCombatCollisions`,
the port's `HandleCollision` analogue). The port's `visible && inCluster` halves of
`fExplosion` collapse to always-true (every sector model is visible; the pass runs
per-sector), so the port gates on the damage result, blast power and no-debris flag.

There is a second, unrelated native call site — the builder **drilling an asteroid**
(`shipIGC.cpp:887-891`, `AddHullHit(pos, backward)`) — which is the mining-drill
debris, out of scope here (the task targets weapon impacts).

---

## 3. What was built

Because the port's MonoGame client is a **pure renderer with no static core and no
in-process IGC combat simulation**, it cannot infer the exact impact position /
normal of a remote hit (a projectile just vanishes on expiry; the wire carried no
hit data). Following the established P263 `EXPLOSION_EVENT(207)` precedent (a
server-driven VFX message for the pure-renderer client), P364 adds a new
server→client wire notice for every qualifying hit.

**Wire** — `src/Allegiance.Core/Wire/ProjectileHullHit.cs`
`FM_S_PROJECTILE_HULL_HIT(213)`, a this-port-only message above native's real FMID
range (max 200), 36 B fixed:
`targetId(2) + clusterId(2) + radius(4) + position(12) + normal(12)`. Carries the
real world impact point, the real world hit-surface normal, and the struck target's
collision radius (the client sizes each bit `radius/2`; it has no core to resolve
the radius itself).

**Server (sim → host → wire):**
- `Sim/IIgcSite.cs` — new no-op `ProjectileHitEvent(Projectile, impact, normal, radius, targetId, clusterId)` seam (C#-only, mirroring `ProjectileCreatedEvent`).
- `Model/Sector.cs` — `ResolveCombatCollisions` captures each target's `DamageResult` and calls `MaybeRaiseProjectileHit` (the native gate) in the ship/probe/station/asteroid branches; raises the seam event.
- `Sim/RecordingIgcSite.cs` — records the event (test observer).
- `ServerHost.cs` / `Server.Lifecycle.cs` / `Server.Combat.cs` — wire the seam to a `BroadcastProjectileHullHit` that packs + sends `ProjectileHullHit(213)` to the same audience every prior projectile broadcast uses (the shooter's own peer + same-side teammates who hold the shooter's `ExportShip` — `_teammateExportSent`-gated), i.e. the local player's own hits **and** remote combat observed by a same-side teammate.

**Client:**
- `ClientCore/State/HullHitEntity.cs` + `ClientWorld.cs` — transient buffer fed by
  `Apply(ProjectileHullHit)`, drained atomically by `TakeNewHullHits()` (mirrors
  `TakeSoundEvents`, so each hit is emitted exactly once per frame).
- `Session/ConnectionSession.cs` — receive case for FMID 213.
- `Rendering/HullHitSparkParticles.cs` — pure MonoGame-free pool: `Emit` spawns
  exactly 2 bits at the impact point with the native velocity/size/spread/lifetime.
- `Rendering/HullHitSparkRenderer.cs` — camera-facing additive billboards; bright
  warm-white (`255,235,166` × remaining-life), constant size (bits don't grow), the
  two bits per hit are visually **distinct** from the grey damage-vent smoke.
- `Game1.cs` — drains `world.TakeNewHullHits()` under the Gate, emits each own-sector
  hit into the pool, ages + draws additively over the scene.

**Proof tool** — `tools/hullhit-proof/hullhit-proof.csproj` (+ `InternalsVisibleTo`
grant), a permanent live-proof harness.

---

## 4. Live verification performed

Ran `hullhit-proof` against a **real in-process Allegiance.Server** hosting a real
fight (an armed shooter bot on side 0 pours rounds into a defenceless target bot on
side 1, `m2xspiderw.igc`), with a real `ConnectionSession` MonoGame-rendering client
joining as a **same-side observer that never fires** — i.e. it watches remote combat.

```
[hullhit] seated: viewer MyShipId=8098, shooter ship=8096, target ship=8097
[hullhit] target hull 'Assault Carrier Drone' (8097) radius 165
[hullhit] shooter opened fire — accumulating real PROJECTILE_HULL_HIT(213) at the observer...
[hullhit] observer drained 8 PROJECTILE_HULL_HIT(213) events from remote combat
[hullhit] PROOF 1 (wire): 8 real hits at the target; worst impact offset 180 u (radius 165);
          all sector 1050, all target 8097 — PASS
[hullhit] PROOF 2 (visual): PNGs written to .verify_p364 — hullhit_closeup_birth/drift/smoke_context.png
[hullhit] hullhit-proof PASS (wire + renders)
```

- **Wire proof (decisive, programmatically asserted, harness exits non-zero on
  failure):** the *observer client* — which fired nothing — received 8 real
  `FM_S_PROJECTILE_HULL_HIT(213)` events over the real wire while merely watching
  a teammate shoot an enemy, every one naming the real target (8097) and its real
  sector (1050), positioned at the target's real hull surface (worst offset 180 u
  vs collision radius 165 u). This confirms the effect reaches **remote combat the
  local player is merely observing**, and works for the attacker's own shots too
  (the shooter's own peer is in the same audience). Threshold, sector, target-id and
  distance checks must all pass or the harness reports FAILED.
- **Visual proof (PNGs in `.verify_p364/`):** the real `HullHitSparkParticles` +
  `HullHitSparkRenderer`, fed from the real received hit, render a bright
  white-hot/warm spark at the impact point (birth frame) that visibly **fades by
  ~0.7 s later** (drift frame) — the real effect drawing. The smoke-context/close-up
  frames also drive the real `DamageDebrisRenderer` (grey) alongside the bright
  spark in the same offscreen scene, confirming the two effects are distinct in
  colour and rendering.

## Tests

- `Core.Tests/ProjectileHullHitCombatTests` (2): landed shots raise
  `ProjectileHitEvent` per hit (target takes damage, count grows across windows,
  names target id + sector); a friendly-fire-suppressed round raises **no** event.
- `ClientCore.Tests/ClientWorldHullHitTests` (6): `ProjectileHullHit(213)` codec
  round-trip / fixed 36 B / wrong-fmid rejection; world ingestion; `TakeNewHullHits`
  drains exactly-once.
- `ClientCore.MonoGame.Tests/HullHitSparkParticlesTests` (8): exactly 2 bits per
  hit, size = radius/2, velocity = 0.4·size·(normal ± 0.25 spread), 2 s lifetime,
  straight-line integration, pooling/reset.

Full suites: Core 3293 (only pre-existing live-wire timing flakes; the one
non-documented `ProjectileBroadcastTests` failure was **confirmed identical on the
pristine tree** — a machine-state flake, not a regression), ClientCore 175 clean,
MonoGame 686 clean. Whole-solution build: 0 warnings, 0 errors.

Committed on `master` (not pushed).

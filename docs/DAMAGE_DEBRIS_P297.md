# DAMAGE_DEBRIS_P297 — continuous damage-debris smoke stream (AddDamageParticles)

Port of native's continuous damage-debris particle stream
(`effect/thinggeo.cpp:699-755`) into the MonoGame client. Distinct from the
P228 `DamageFlashTracker` flash (a momentary full-screen red pulse on the
instant of a hit); this is the *ongoing* visual state of an already-damaged
ship — grey smoke pouring off the hull while it remains damaged.

## Native relationship (citations)

- `AddDamageParticles` (thinggeo.cpp:699-755) runs *continuously each frame*
  while gated on `(m_iLastDamageIndex >= 0) && m_pParticleGeo && m_bShowDamage`
  (thinggeo.cpp:701).
- `m_iLastDamageIndex` grows in `AddDamage` (thinggeo.cpp:1358-1382) and
  shrinks in `RemoveDamage` (thinggeo.cpp:1384-1395) as the hull heals — the
  stream is **bidirectional** (starts below a threshold, stops when repaired
  above it). Index formula:
  `iDamageIndex = (int)((1.0f - fDamageFraction) * MAX_DAMAGE_FRAMES) - 1`,
  `MAX_DAMAGE_FRAMES = 10` (thinggeo.cpp:215, 1360).
- **Threshold**: the stream is on while `(1-frac)*10 - 1 >= 0`, i.e.
  `fraction <= 0.9`; vent count = active frames = `(int)((1-frac)*10)` (1…9 for
  a living ship). `RemoveDamage` on repair drops the count back toward 0
  (thinggeo.cpp:1384-1395).
- Emission per frame, per vent (thinggeo.cpp:704-751):
  `iParticlesPerFrame = clamp(int(|vel|·dt), 1, s_iShowSmoke=1)` → 1 puff/vent;
  `fEffectSize = random(1.5, 2.5)` (739); uses `ComputeFireParticleParameters`
  (thinggeo.cpp:537-579) with ejection (4, 6), start variance 0.05, duration
  0.35 (745); `AddSmoke` particle gets
  `m_vecAcceleration = vecStartVelocity · −0.333` (749), `m_fSize = fEffectSize`
  (750), `m_fDuration = 0.35` (751). Origin = ship position + rotate(frame
  offset); ejection along rotate(frame forward) (732-733).
- Rendering = native `SmokeBehavior::Render` (particlegeo.cpp:37-62): grey
  `RGBA = (c, 0.8c, c, c)` with `c = 0.25·countdown`, growing
  `size = m_fSize·(0.75 → 3.0)` as it fades; position integrated with
  acceleration (particlegeo.cpp:58).
- **Scope**: native applies it to *any* visible thing with a model + hull
  damage — ships (`shipIGC.cpp:1311`), stations (`stationIGC.cpp:196`),
  probes (`probeigc.h:143`) — not just the local ship. `m_bShowDamage` is
  toggled only for the *local* ship between internal/cockpit (off) and
  external (on) camera views (WinTrek.cpp:6607-6622).

## Port design

**Scope: any visible damaged ship.** The client has the real `HullFraction` on
every ship's `ServerShipUpdate` (byte 40 → 0..1), so the stream is driven
per-ship for all visible ships — matching native's per-ship scope. The local
ship only vents in non-cockpit camera modes (native WinTrek.cpp:6616-6621).

**Sibling tracker, not an extension of `DamageFlashTracker`.** `DamageFlashTracker`
(P228) is edge-triggered (hull *decrease* → momentary 2 s flash), local-ship-only,
full-screen HUD wash. The debris is *level*-triggered (hull *below* 0.9 →
continuous in-world smoke), per-ship. Different native subsystems (WinTrek HUD
vs thinggeo model state), different signals (edge vs level), different targets
(local vs all). Built `Flight/DamageDebrisTracker` (static pure) reproducing the
native `(int)((1-frac)*10)` vent-count mapping, clamped 0..10; bidirectional
by construction (reads the current hull level). Documented divergences:
the per-event `c_dmgidNoDebris` type gate (shipIGC.cpp:1310) and per-hit
damage-frame *offsets* are not portable to a level-based client inference
(the client sees only the resulting fraction, never the hit positions/types).

**`Rendering/DamageDebrisParticles`** — pure MonoGame-free ring-buffer pool
(reusing MissileTrail/WarpTransit conventions) reproducing native emission:
one puff per vent with native sizing/ejection/duration, drag acceleration,
ship-velocity inheritance, closed-form integration. Vent hull *placement* is a
deterministic pseudo-random unit direction per shipId+vent (hash), scaled
`radius·0.33` — a documented stand-in for the unavailable per-hit offsets;
the vent *count* is real (from the tracker).

**`Rendering/DamageDebrisRenderer`** — billboard soft-quad renderer reproducing
native SmokeBehavior grey `(c,0.8c,c,c)` growing 0.75→3.0× size, additive soft
alpha (repo particle convention).

**Wiring (`Game1`)** — in the pose-gather pass (under the Gate lock), each
visible ship's emitter (position/velocity/orientation + vent count from its
real hull fraction) is recorded; in the 3D pass one `Emit` per ship per frame
is fed to the shared pool, aged, and drawn.

## Tests (MonoGame.Tests 527 → 555, +28)

- `DamageDebrisTrackerTests`: native vent-count map (0..9, 0.9 threshold),
  threshold on-at-0.9/off-above, monotonic scaling, constants guard.
- `DamageDebrisParticlesTests`: one puff/vent, native 1.5–2.5 size, ship-velocity
  inheritance (velocity delta == ship-velocity delta), −⅓ drag, 0.35 s expiry,
  closed-form drag integration, shared-pool accumulation, reset.

Must-fail verified: neutralizing the drag term reds both acceleration tests;
forcing the tracker to 0 reds the mapping + threshold tests.

## Live proof (.verify_p297/)

Real in-process Server (XXV_simple) + real ConnectionSession; the peer's hull
is really lowered and raised via the real `Ship.ReceiveDamage` (shipIGC.cpp
port), and the changed fraction reaches the client on the real
`ServerShipUpdate` wire payload; driven each frame exactly as Game1 (read real
received hull fraction → tracker vent count → real pool emit from real
Position/Velocity/Orientation → real renderer offscreen).

- **Healthy (frac 1.0)**: 0 vents, 0 active, 3 frames byte-identical
  (`704204FE5333…`).
- **Real damage → serverFrac 0.520 / receivedFrac 0.521**: stream appears
  (active 4→24) and **persists at a steady 24 live puffs across all 30 frames**
  (continuous, not a one-shot). Pixel analysis: 210→429 pixels differ from the
  healthy baseline in a ~32×16 grey region at the ship, brightest 61 vs ~20
  backdrop (visible grey smoke, faithful to native's subtle 0.25-intensity
  SmokeBehavior).
- **Real repair → 1.0**: vents stop, pool drains (20→0), and frames become
  **byte-identical to the healthy baseline** again (stream stopped;
  bidirectional confirmed live).

## Build / test status

- Whole-solution build: **0 warnings, 0 errors**.
- MonoGame.Tests: **555 passed, 0 failed** (527 baseline + 28).
- Allegiance.Core.Tests full suite: run ≥2×; only the pre-existing
  `LiveTurretGunnerBindingTests` timing flake is expected (see run log). No
  Allegiance.Core/Server/ClientCore code changed — this task is MonoGame-only.

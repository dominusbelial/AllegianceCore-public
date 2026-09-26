# P358 — Ship engine-thrust trail effect

Native function ported: `AddThrustParticles` (case 1, the "rocket" afterburner
fire+smoke path), `effect/thinggeo.cpp:758-917` — a real, dedicated continuous
engine-thrust particle system native runs every frame a ship has real thrust
power. Distinct from the damage-vent smoke (`AddDamageParticles`,
`DamageDebrisParticles.cs`) and missile trails (`MissileTrailParticles.cs`,
missile-only) — this is a NEW ship-specific effect this port was missing.

## What was ported (exact native parameters)

Gate — `m_fThrustPower > 0` (thinggeo.cpp:760). `m_fThrustPower` is fed per
frame from the mounted afterburner's ramped power
(`pthing->SetAfterburnerThrust(pship->GetOrientation().GetBackward(),
afterburner->GetPower())`, WinTrek.cpp:8002-8003); `CafterburnerIGC::Update`
ramps power to 1.0 while the afterburner button is held and decays it to 0 when
released (afterburnerIGC.cpp:133-146).

Per frame per emitting frame, native emits exactly ONE fire + ONE smoke particle
(`iParticlesPerFrame = clamp(int(2·|vel|·dt/(m_sizeSmoke·m_fThrustPower)), 1,
s_iShowSmoke=1)`, thinggeo.cpp:770-774). Parameters:

- `m_sizeSmoke = 4.0` (constructor, thinggeo.cpp:235)
- `fEffectSize = m_sizeSmoke · random(0.875, 1.125)` (thinggeo.cpp:809)
- **Fire** — `ComputeFireParticleParameters` (thinggeo.cpp:537-579) with ejection
  (9, 12), start variance 0.2, duration `m_fAfterburnerFireDuration = 0.333`
  (thinggeo.cpp:816-836), zero acceleration. Rendered by FireBehavior as a
  bright orange/white flame that fades fast (particlegeo.cpp:68-129).
- **Smoke** — `ComputeSmokeParticleParameters` (thinggeo.cpp:582-616) with
  ejection (18, 24), start variance 0.1, duration `m_fAfterburnerSmokeDuration
  = 2.0` (thinggeo.cpp:841-860), zero acceleration. Rendered by SmokeBehavior as
  a grey plume that swells and fades (particlegeo.cpp:24-66).
- Both pass `fEffectSize · m_fThrustPower` as the compute effect-size
  (thinggeo.cpp:826, 850) — a decaying power thins the trail. Both eject along
  the ship's backward direction (`m_vecThrustDirection = GetBackward()`,
  WinTrek.cpp:8002) and inherit the ship's own velocity (+vecFrameVelocity).

## Hardpoint-position approach: (b) aft-centre approximation — chosen over (a)

Investigated option (a) — real per-hull hardpoint extraction — first. Native
`.mdl` files DO carry real named hardpoint frames with local positions; I parsed
several with `~/Development/allegiance-tools/reference/mdl_parse.py`
(FrameData decoding, mdl_parse.py:163-178) and confirmed:
- `quizfigv2.mdl` (Bios Fighter): `thrust`, `trail`, `smoke` — only `smoke`
  (pos ≈ (−0.00, 0.22, −2.93)) drives the effect (name contains "smoke", not
  "rocket" → native case 2, plain smoke).
- `fig02.mdl`: `lsmoke`/`rsmoke` (case 2), `lthrust`/`rthrust`/`trail` do NOT
  trigger (their names contain neither "rocket" nor "smoke").
- `fig01.mdl`: `smoke` (case 2).

However, option (a) was judged not cleanly tractable here: this port renders
Meshy-regenerated content hulls (P287/P351-356), NOT the native `.mdl` geometry,
and the two coordinate frames differ — the missile path documents that native
`.mdl` noses point +Z while this port's content ship models are nose-at-−Z
(Game1.cs:2729), plus each Meshy export has arbitrary per-asset scale/orientation
(ShipRenderScale.cs:13-23 normalizes them). Mapping a native hardpoint's local
offset onto a regenerated mesh across 70+ content hulls would require per-model
transforms AND still not sit on the regenerated exhaust geometry — fragile and
not "more faithful." Some hulls even lack any "smoke"/"rocket" hardpoint, so
native itself shows no trail for them.

Chose **(b) a single aft-centre emitter derived from the ship's real hull
geometry**: emission origin = `position + backward · (radius · 0.95)` where
`radius = 0.5 × HullType.Length` — the exact "faithful approximation when
precise native data isn't cleanly portable" precedent `DamageDebrisParticles.cs`
uses for damage vents (P297/P338). It is real, robust, places the emitter on the
actually-rendered hull's aft, and is clearly "the ship is thrusting." (Aft-centre
is faithful to native smoke hardpoints sitting at the model aft, e.g.
quizfigv2 `smoke` at ≈1.05×radius behind centre.)

Two documented normalizations (both labelled in the pool):
1. `SmokeSizeFactor = 0.5` — native's `m_sizeSmoke = 4.0` is a constant in native
   model units (~1.3× a native fighter radius); this port normalizes all geometry
   to real world units (radius = 0.5×Length), so particle size is scaled by
   `radius · SmokeSizeFactor` to keep the trail proportionally readable on every
   hull class. (Without it, a real fighter's radius ~25 would show a 4-unit
   trail — ~6× smaller relative than native's look.)
2. The compute calls' frame-acceleration term is omitted (client has no per-frame
   acceleration history; velocity inheritance is direct) — the same documented
   omission `DamageDebrisParticles.cs` makes.

## Trigger: the real wire afterburner bit, ramped like native power

The trail is gated on the real `StateM.AfterburnerButton` bit already on the
wire for every visible ship — `ServerShipUpdate.StateM` (ServerShipUpdate.cs:43)
is encoded from the live ship's real `StateM` in `FromModel`
(ServerShipUpdate.cs:106-110), so the client already knows it for the local hull
AND every remote ship (no new wire data). This is the literal native
`m_fThrustPower > 0` signal (afterburner engaged) — more faithful than deriving
thrust from velocity/acceleration as the brief suggested. Game1 ramps a per-ship
power toward 1.0 while the bit is held and decays it to 0 when released
(native CafterburnerIGC::Update semantics; representative onRate 6 / offRate 3),
so the trail appears on boost and thins then stops as power decays — matching
native's ramp.

## Implementation

- `Rendering/ShipEngineTrailParticles.cs` — pure, MonoGame-free ring-buffer pool
  (the MissileTrail/DamageDebris conventions: `ITrailRandom` seam, shared
  `TrailParticleView`), reproducing the native emission math exactly.
- `Rendering/ShipEngineTrailRenderer.cs` — camera-facing additive soft-quad
  billboard renderer (fire orange, smoke grey, smoke grows), restoring all three
  GPU states (P339 B2 pattern).
- `Game1.cs` — new pool/renderer wired into LoadContent; the pose-gather pass
  (Update) maintains the per-ship thrust-power ramp from `StateM.AfterburnerButton`
  for the local ship (non-cockpit, matching the P297 damage-debris gate) and every
  visible remote ship; Draw emits/ages/draws after the damage-debris pass.
- `tests/.../ShipEngineTrailParticlesTests.cs` — 6 pure pool tests; the
  thrust-power gate is must-fail-proven (red when neutralised).

## Out of scope (per brief — untouched)

- Navigation lights — no native basis found; not built.
- Real per-hit damage-vent positions — `DamageDebrisParticles.cs` not touched.
- Missile trails — `MissileTrailParticles.cs`'s missile-driving path unchanged
  (the new effect is a separate class reusing only the shared conventions).

## Verification — live proof (real server → real wire → real pool → real renderer)

Evidence: `.verify_p358/frames/` (PNGs + `p358_run.log`). Harness: real
in-process `Allegiance.Server` (XXV_simple) + two real `ConnectionSession` peers
(ships 8096/8097, same side) on a realistically-sized fighter (HullType.Length
42.5 → radius 21.2). Two ships fly +X at X=±45; the LOCAL peer's ship boosts via
the authentic afterburner path, the REMOTE via its own peer's controls. Each
frame reads the server-authoritative Ship state (the exact values
`ServerShipUpdate.FromModel` quantizes onto the wire), ramps the power, emits
into the REAL pool, and renders the REAL renderer offscreen (960×640). Metrics:
active particle count, wire-thrusting ship count, per-half lit pixels, frame md5.

Results:
- Phase A (idle): `active=0`, all three frames byte-identical black baseline
  (md5 405F558AC020).
- Phase B (LOCAL 8096 boosts): `emitting=8096`, active persists 2→14, left-half
  lit pixels 1636→3779, all md5s differ from A → a real continuous trail renders
  behind the local ship.
- Phase C (REMOTE 8097 also boosts): `emitting=8096+8097`, right-half lit pixels
  climb 1654→5176 (the REMOTE ship's trail appears), left stays lit → the trail
  renders for a remote ship too, not just the local hull.
- Phase D (thrust stops): `emitting=` (no emission), active drains 36→0, lit→0,
  final frame D29 md5 = 405F558AC020 = the EXACT Phase-A idle baseline →
  the trail thins and stops when thrust stops.

Vision on the saved frames confirms the look: warm orange/amber fire glows at
the nozzle of each thrusting ship (left = local, right = remote) with grey
particles streaming horizontally behind — a real engine exhaust trail.

## Build / tests

- Whole-solution build: 0 warnings, 0 errors.
- `Allegiance.ClientCore.MonoGame.Tests`: 658 passed (6 new).
- `Allegiance.Core.Tests`: run below; only the pre-existing documented
  live-wire timing flakes expected.

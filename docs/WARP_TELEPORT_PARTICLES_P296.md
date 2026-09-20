# Warp-Transit Teleport Particle Spray (P296)

## What this is

A **warp/ripcord transit particle spray** in the MonoGame client: a burst of
hot-blue sparks sprayed around the local ship at the instant of a real sector
transit, using native Allegiance's real `AddTeleportParticles`
(`effect/thinggeo.cpp:619-696`). It fires **together with** the P245 screen
flash, reusing the exact same once-per-transit trigger, and renders through the
same particle-system conventions as the missile exhaust trail (P235).

## Correction of an earlier session's scope (not a silent oversight)

P245 (the warp-transit screen flash) concluded an "exhaustive grep of native
`WinTrek/*.cpp/h`" found no transit flash and built an honestly-labelled
**original** flash. That grep was scoped to `WinTrek/` only and never reached
`effect/thinggeo.cpp`. Native **does** have a distinct transit visual there:
`AddTeleportParticles` (`thinggeo.cpp:619-696`), gated on
`m_fTimeUntilRipcord`/`m_fTimeUntilAleph`, spraying particles along axes computed
from the ship's real velocity at the moment of transit. **P245's flash is
correct and stays** (it is the *screen* feedback; this is the *3D particle*
feedback). They are complementary — this port, like native, layers multiple
transit cues. Neither replaces the other.

## Native parameters (cited)

`AddTeleportParticles` (thinggeo.cpp:619-696), per call:

- **Gate:** runs while `fTimeUntilTeleport >= 0`; `fTimeUntilTeleport` is the
  smaller of `m_fTimeUntilRipcord`/`m_fTimeUntilAleph` (622-625).
- **Spray axes from real velocity** (632-647): `vecZ = normalize(velocity)`,
  `vecX = vecZ.GetOrthogonalVector()`, `vecY = cross(vecX, vecZ)`; falls back to
  the hull orientation (`GetForward()`/`GetRight()`) when stationary.
- **Ramp:** `fTimeToShowSparks = clamp(fTimeUntilTeleport / 3.0f, 0, 1)`
  (651-656); `fCountUpToOne = 1 - fTimeToShowSparks` (657).
- **Spark count:** `iSparkCount = floor(TELEPORT_SPARK_COUNT ·
  s_iShowSmoke · fCountUpToOne) + 0.5`, with
  `#define TELEPORT_SPARK_COUNT 3` (thinggeo.cpp:223,659) and default
  `s_iShowSmoke = 1`.
- **Size scale:** `fScaledUpCount = ((fCountUpToOne·2) + 1) · m_radius`
  (658) → `m_fSize = random(fScaledUpCount·0.15, fScaledUpCount·0.3)` (688).
- **Per spark:** blast `= RandomDirection() · m_radius·random(0.75,1.25)`
  (666-667), projected toward the in-plane basis by `fTimeToShowSparks`
  (671-676); `m_vecVelocity = vecFrameVelocity + blast` (681);
  `m_vecAcceleration = -vecFrameVelocity` (682); start position from the ship's
  last position (683-687); `m_fDuration = 0.5f` (689).
- **Render colour** (`SparkBehavior::Render`, particlegeo.cpp:230-242):
  `c = (duration − age)/duration`, `k = ½·c²`,
  `RGBA = (1.5k, 2.0k, c, k)` — "hot blue, fading out"; position integrated
  `pos + vel·t + ½·accel·t²`; decal drawn at `m_fSize`.

## Port behaviour (deliberate, documented divergences)

- **Trigger = the P245 transit edge, exactly once.** Native runs the spray
  continuously through the multi-second ripcord/aleph lead-up, ramping the count
  up to the maximum as transit approaches. The port observes only the transit
  instant (the same `SET_CLUSTER` sector-change edge `Flight.WarpTransitFlash`
  detects), so it emits **one burst at the peak transit-instant parameters**
  (`fTimeToShowSparks = 0`, `fCountUpToOne = 1`) — exactly what one native call
  would emit on the final frame before the countdown goes negative. This is a
  faithful recreation of the per-call computation, adapted to the single edge
  the port's wire offers.
- **Ship radius stand-in.** Native sizes particle size and blast speed off
  `m_radius`; the client wire carries no per-ship radius and the client has no
  static ship-type core (the same gap `MissileTrailParticles.MissileRadiusDefault`
  bridges) → `ShipRadiusDefault = 50f`, a documented approximation.
- **Both effects fire on the same arm.** In `DrawHud`, `WarpTransitFlash` arms
  on `JustTransited` and buffers the ship's real position/velocity/axes; the
  next frame's 3D pass consumes that and emits the burst (mirroring native's
  "velocity at the transit moment"), then ages/draws the pool. The flash and the
  spray therefore coincide per transit.

## Out of scope (unchanged)

- `AddDamageParticles` (thinggeo.cpp:699+) — a separate, unrelated continuous
  damage-debris effect; not built here (separate task).
- `WarpTransitFlash` detection logic and P245's flash — untouched.
- Native source — read-only.

## Files

- `src/.../Rendering/WarpTransitParticles.cs` (new) — pure MonoGame-free pool,
  `EmitBurst` + `Update` (age/expire) + `Enumerate` (closed-form
  `origin + vel·t + ½·accel·t²`), reusing the `ITrailRandom` seam of
  `MissileTrailParticles`.
- `src/.../Rendering/WarpTransitParticleRenderer.cs` (new) — camera-facing soft
  quads, native `(1.5k, 2.0k, c, k)` hot-blue fade, additive blend, native world
  size.
- `src/.../Game1.cs` — fields, renderer construction, `DrawHud` buffers the
  transit state on `JustTransited`, 3D scene pass emits+ages+draws the pool,
  disposal.
- `tests/.../WarpTransitParticlesTests.cs` (new) — 8 pure-logic tests.

## Verification

**Live proof (`.verify_p296/`)** — real in-process server (XXV_simple.igc) +
real ConnectionSession; the ship is driven kinematically through a real warp
(the server performs a genuine transit, exactly one `SET_CLUSTER` to the peer),
and the real `WarpTransitParticles` + `WarpTransitParticleRenderer` are rendered
offscreen. Log:

```
>>> TRANSIT ARM #1: frame=22 ... sector->1057 flashOp=0.000 shipVel=15.0u/s
>>> BURST #1 emitted at transit: 3 sparks, dt-paced
TRANSIT detected frame=22 sector 1051->1057; capturing the spray…
>>> TRANSIT ARM #2 ... sector->1051
DONE: arms=2 bursts=2   (2 live transits -> 2 arms -> 2 bursts)
```

- **Exactly once per transit, not continuous:** two live transits → two arms →
  two bursts, each of exactly `TELEPORT_SPARK_COUNT` (3) sparks.
- **Rendered spray + native fade:** fresh-frame hot-blue pixel count
  `304 → 510 → 341 → 135 → 0 → 0` and peak brightness `360 → 226 → 132 → 80 →
  52 → 39` across the burst frames (f023→f028) — the spray appears right at the
  arm, spreads, then decays to nothing (native `c²` "hot blue, fading out").
- **Not continuously, not missing:** the drained frames (f029+) are all
  byte-identical (md5 `704204fe`, 20868 B) — a unique clear baseline — so the
  spray is a discrete once-per-transit event, present only in the burst frames
  (which differ from that baseline only by the sparks).
- The same `JustTransited` arm drives the P245 flash (flashOp logged at the arm),
  so both effects fire together on the same real transit.

**Must-fail proven (temporarily reverted, watched red, restored):**
dropping the ship-velocity inheritance turns
`EmitBurst_sparks_inherit_the_ships_velocity_plus_a_blast` red; replacing
`acceleration = -velocity` with zero turns
`EmitBurst_spark_acceleration_opposes_the_ships_velocity` and
`Sparks_integrate_position_with_acceleration_closed_form` red (3/3), green again
after revert.

## Build / test

- Whole-solution build: **0 warnings, 0 errors.**
- `Allegiance.ClientCore.MonoGame.Tests`: **527 pass** (+8 new
  `WarpTransitParticlesTests`), clean runs.
- `Allegiance.Core.Tests` / `Allegiance.ClientCore.Tests`: my change is
  client-only (MonoGame project + MonoGame tests); full-suite runs confirm the
  existing baseline (only the documented `LiveTurretGunnerBindingTests` timing
  flake is expected).

## Confirmation of the correction

The P245 conclusion ("no coded native transit visual in `WinTrek/`") is accurate
for `WinTrek/` but was incomplete in scope: native's real, distinct transit
visual lives in `effect/thinggeo.cpp` (`AddTeleportParticles`). P296 ports it
faithfully and reuses P245's edge detector so both effects fire together.

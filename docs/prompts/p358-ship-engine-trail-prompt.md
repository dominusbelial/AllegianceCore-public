# P358 — Add a real ship engine-thrust trail effect (a genuine native feature this port is missing)

## Why this task exists

Domingo remembered ships leaving a real engine exhaust trail similar
to missile trails. Verified against native source this session (not
assumed):

- Native has a real, dedicated, continuous engine-thrust particle
  system: `AddThrustParticles`
  (`~/Development/Allegiance/src/effect/thinggeo.cpp:758-900+`),
  called every frame a thing has real thrust power
  (`m_fThrustPower > 0`, line 760) from `thinggeo.cpp:518`. This is
  **completely distinct** from two other effects already investigated
  this session and confirmed NOT to be this: the damage-vent smoke
  (`AddDamage`, `thinggeo.cpp:1358+`, already faithfully ported as
  `DamageDebrisParticles.cs`) and missile trails
  (`MissileTrailParticles.cs`, ported but missile-only).
- It iterates the model's real named hardpoint frames
  (`FrameData`/`m_pframes`) and branches on the frame's NAME
  substring, not a fixed convention — read this carefully, it is easy
  to get wrong: `frame.m_strName.Find("rocket") != -1` → afterburner-
  style fire+smoke (case 1, `thinggeo.cpp:787-864`); else
  `frame.m_strName.Find("smoke") != -1` → plain engine smoke only
  (case 2, `thinggeo.cpp:789-900+`, continues past what's excerpted
  here — **read the rest of the function yourself**, this summary
  stops mid-case-2). A hardpoint frame named e.g. `thrust`/`trail`
  alone does NOT trigger this particular effect — only frames whose
  name contains "rocket" or "smoke" do. Confirmed on a real hull this
  session: Bios Fighter (`quizfigv2.mdl`) has hardpoints named
  `thrust`, `trail`, `smoke` — only its `smoke` hardpoint would drive
  this effect (plain engine smoke, not afterburner fire+smoke, since
  the name doesn't contain "rocket").
- **This port has zero ship-specific engine trail effect.** The only
  trail-particle system that exists,
  `MissileTrailParticles.cs`/`MissileTrailRenderer.cs`, is driven
  exclusively by `_drawTrailMissiles`
  (`Game1.cs:2890`, confirmed missile-only by tracing the call site) —
  ships never touch it.

This is a real, well-documented, scopable native feature gap — not
new functionality (unlike a damage-position wire field, ruled out
this session) and not a nonexistent feature (unlike nav lights, also
ruled out this session — no native basis found for those at all,
don't build them in this task).

## Scope

1. **Read the rest of `AddThrustParticles`** (`thinggeo.cpp:758` to
   its end — this prompt's excerpt stops mid-function) for the exact
   remaining parameters (case 2's duration value, the function's
   overall structure, `ComputeFireParticleParameters`/
   `ComputeSmokeParticleParameters`'s exact signatures) before writing
   any code — this project's standing discipline, ground every claim
   in the real source, don't guess constants.
2. **Determine how to get real hardpoint positions for this port's
   actual ship hulls.** This port's ships use Meshy-regenerated
   content models (the P287→P351-356 arc), not the original native
   `.mdl` geometry directly, so the render mesh itself carries no
   hardpoint markers. Two real options, pick the more practical one
   after investigating both — don't assume:
   - **(a) Real per-hull hardpoint extraction**: the native `.mdl`
     files DO carry real named hardpoints (confirmed this session via
     `~/Development/allegiance-tools/reference/mdl_parse.py`, which
     already decodes them — see its "named frames/hardpoints found"
     output). Parsing each hull's native `.mdl` for its real "smoke"/
     "rocket"-named hardpoint LOCAL position, then mapping that into
     this port's own render/sim coordinate convention (the same
     `ShipRenderScale`/nose-alignment conventions `ShipRenderer.cs`
     already establishes), would be the most faithful result but is
     real, nontrivial cross-coordinate-system work — investigate
     whether this is actually tractable before committing to it.
   - **(b) A single practical aft-mounted approximation**: emit from
     one fixed point derived from the ship's own known real geometry
     (e.g. the hull's aft-center, using `HullType.Length` the same way
     `DamageDebrisParticles.cs` already approximates hull extent with
     `radius` when exact per-hit data isn't available — that's an
     already-accepted precedent in this exact codebase for "faithful
     approximation when precise native data isn't cleanly portable").
     Less faithful to multi-engine hulls, but real, simple, and still
     clearly recognizable as "the ship is thrusting."
   State which you chose and why in the report — either is acceptable
   if it's a real, working, reasoned engineering choice, not a
   shortcut taken without investigating (a) first.
3. **Build the effect**, reusing `MissileTrailParticles`'s real,
   already-ported particle mechanics (`ComputeFireParticleParameters`/
   equivalent math, fire+smoke dual-particle pattern, the
   `TrailParticleView`/`ITrailRandom` interfaces) as the structural
   precedent — this is the same underlying native particle system
   (`thinggeo.cpp`), just a different call site with different
   parameters and trigger condition (real thrust power > 0, driven by
   the ship's own already-client-known velocity/acceleration — no new
   wire data needed, this is a pure visual effect from data the client
   already has).
4. **Wire it into `Game1.cs`'s per-frame draw loop** for every visible
   ship with real thrust (not just the local player's own ship — every
   ship the client renders, same as missiles render for every visible
   missile).

## Explicitly out of scope

- **Navigation lights** — investigated this session, no native basis
  found at all (no hardpoint convention, no rendering code in
  `effect/`/`clintlib/`/`Igc/`). Do not build this.
- **Real per-hit damage-vent positions** — investigated this session;
  native never sent this over the wire either, so "fixing" it would be
  new functionality beyond native parity, a separate future task if
  ever pursued. Don't touch `DamageDebrisParticles.cs`.
- **Missile trails** — already correctly implemented, don't touch
  `MissileTrailParticles.cs`'s existing missile-driving code path,
  only reuse its structural pattern for the new ship effect (a new
  class/instance, or a generalized shared one if that's clean — your
  call, but the existing missile behavior must not change).
- P354/P355/P356/P357 — unrelated, already landed or in flight, don't
  touch.

## Verification — real proof, not just build/test

- **Live proof, required**: launch a real client against a real server
  (e.g. `vizfight proof` or `stress` mode, or the standalone
  `Allegiance.Server` + real MonoGame client, whichever is more
  convenient), fly/thrust a real ship, and confirm a real, continuous
  engine trail renders behind it — screenshot or described observation
  of the actual visual result, not just "the code compiles and the
  particle count increases."
- Confirm the trail correctly stops/thins when thrust stops (matching
  native's `m_fThrustPower > 0` gate) and that it renders for a REMOTE
  ship too (not just the local player's own), since the client already
  knows every visible ship's velocity/acceleration from the wire.
- Whole-solution build: 0 warnings, 0 errors.
- Full test suite (`/home/domingo/.dotnet/dotnet test`), run at least
  2x: only the pre-existing documented live-wire timing flakes are
  expected.

## Deliverables

1. The new ship engine-trail effect, committed on `master` (not
   pushed — Domingo pushes). Do not create a side branch.
2. Report doc `P358_SHIP_ENGINE_TRAIL.md` in the repo root: which
   hardpoint-position approach was chosen (real per-hull extraction vs.
   the aft-approximation) and why, the exact native parameters ported
   (cite the real `thinggeo.cpp` values you read), and the live visual
   proof performed.

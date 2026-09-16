# P235 — Missile smoke/fire trail particle effect

Landing stage: the classic "rocket effect with smoke trail" behind in-flight
missiles in the MonoGame client, reusing native's own exhaust-particle system
parameters. Landed on `master` (not pushed — Domingo pushes), no side branch.

---

## 1. The native mechanism (what is being ported)

Missiles in native Allegiance do **not** have a bespoke trail effect: they reuse
the ship-afterburner exhaust system, re-parameterized at creation
(`Igc/missileigc.cpp:53-55`):

```
SetAfterburnerSmokeSize   (0.25f + radius * 0.1f)
SetAfterburnerFireDuration(0.33f)
SetAfterburnerSmokeDuration(2.0f)
```

`effect/thinggeo.cpp` `AddThrustParticles` (:758+) then walks the exhaust frame
list and, for the `"rocket"` frame, emits each frame (with the default
`s_iShowSmoke = 1`, one fire + one smoke pair):

- **Fire** — `ComputeFireParticleParameters` (:537-579) with ejection (9, 12),
  start-variance 0.2, duration 0.33, acceleration zero (:831-836).
- **Smoke** — `ComputeSmokeParticleParameters` (:582-616) with ejection (18, 24),
  start-variance 0.1, duration 2.0, acceleration zero (:855-860).

Both scale `fEjectionVelocity = random(min,max) * fEffectSize` where
`fEffectSize = smokeSize * random(0.875,1.125)` (:809), add the emitter's own
velocity, and scatter the start position by `startVariance * fEffectSize *
randomDirection`. Fire travels from the jittered start to a theoretical end point
over its duration (giving the flame a velocity), while smoke's velocity is simply
the ejection thrust plus the inherited frame velocity.

## 2. What was ported and the deliberate simplifications

`Rendering/MissileTrailParticles.cs` (pure, MonoGame-free — no graphics device)
reproduces those formulas directly (see the file's remarks for the exact mapping).
Deliberate simplifications, each documented:

1. **No per-missile radius / velocity on the wire.** The client's `MissileEntity`
   carries only position + heading (P234), so the smoke-size radius term uses a
   constant `MissileRadiusDefault = 2.0` stand-in, and the emitter's velocity is
   **estimated by finite difference** of the per-tick position samples — which
   keeps the trail attached to a fast missile the way native's inherited
   frame-velocity does. Tested and working.
2. **Emission pacing.** Native paces emission by distance travelled
   (`iParticlesPerFrame = int(2·ΔL/(smokeSize·power))`); with `s_iShowSmoke=1`
   that collapses to one pair per frame, reproduced here (one fire + one smoke
   per live missile per `Update`).
3. **Zero acceleration.** Both native fire/smoke in the rocket path use a zero
   acceleration, so the pool integrates position at constant velocity.
4. **No ported FrameData / nozzle attach point.** Emission origin is the tail of
   the bounding geometry along −Forward (`TailOffset = 1.5`), the task's blessed
   fallback since P233's optional FrameData wasn't ported.
5. **Renderer display tuning.** The pool reports native-faithful (small) sizes;
   `MissileTrailRenderer.BillboardScale` scales them to readable quad world-size,
   and older smoke balloons up (`SmokeGrowth`) for the classic expanding-plume
   read. These are display constants kept OUT of the pure pool.

## 3. Rendering

`Rendering/MissileTrailRenderer.cs` batches every live particle into one
`VertexPositionColorTexture` array per frame and draws it with a single
`DrawUserIndexedPrimitives` call under an additive soft-alpha blend. Each quad is
a camera-facing billboard (basis recovered from the view matrix's first two
columns), sized by the particle's native size × `BillboardScale`, coloured
bright hot-orange/white for fire and dull gray for smoke, and **faded out by its
remaining lifetime** (`RemainingFrac`, eased) — so particles dissolve, never pop.
The soft circular sprite is generated procedurally (no texture asset).

`Game1.Draw()` hooks the pool + renderer right where the P234 missile loop lives:
the same in-sector live missiles gathered each frame are handed to the pool as
`TrailMissile` samples, the pool `Update`s (spawn + age) with the real frame dt,
and the billboards are drawn after the missile mesh. All under the same Gate-lock
discipline as the other world reads; the pool is mutated only on the render
thread.

## 4. Live-proof screenshot observations (offscreen RT)

A throwaway harness (outside the repo, `/tmp/missile_harness`) booted the real
stack — in-process `Allegiance.Server` + real `ConnectionSession` — fired a real
homing missile (slow long-lifespan no-lock type toward a far enemy so it stays
aloft), and rendered with the COMMITTED pool + `MissileTrailRenderer` +
`MissileRenderer`, captured offscreen to `.verify_p235/trail_frame{A,B}.png`.

Hard-won harness lesson worth recording: the per-frame `Draw` path on this
Wayland box produced an **empty** offscreen RT even though the missile was
dead-centre (NDC 0,0); the proven P234 pattern of rendering **synchronously in
`LoadContent`** works. And a camera derived from the missile's (unstable) heading
via `Cross(F, upW)` intermittently blanked the frame — a **side-on stable camera**
(offset mostly perpendicular to the flight axis) frames the trail reliably.

Observed (vision-checked):
- **`trail_frameA.png`** (missile at x≈111, t≈1.2 s of flight): a bright
  white-yellow fire/exhaust glow with a **short, faint** smoke trail (~5-10% of
  frame width) just forming behind it.
- **`trail_frameB.png`** (missile at x≈268, t≈2.2 s): the same bright fire glow
  with a **longer, wider, fading** gray smoke trail extending away — the classic
  rocket-smoke look. The pool logged 48 live particles (7 fire, 41 smoke)
  spanning ~150 world-units behind the missile at capture B.

The two frames a moment apart show the trail **growing/maturing and the missile
moving** (x 111→268) — the same "proves the effect is alive over time" evidence
style P227 used for the warp spin. Diagnostics + the temp camera tweaks lived
only in the throwaway harness (reverted; committed Game1.cs is clean).

## 5. What changed

- `src/Allegiance.ClientCore.MonoGame/Rendering/MissileTrailParticles.cs` *(new)*
  — pure pool: ring buffer, spawn/age/expire, capacity bound, per-missile
  velocity estimate; `TrailMissile`/`TrailParticleView`/`ITrailRandom` types.
- `src/Allegiance.ClientCore.MonoGame/Rendering/TrailRandomSource.cs` *(new)* —
  the `System.Random`-backed `ITrailRandom` used at runtime.
- `src/Allegiance.ClientCore.MonoGame/Rendering/MissileTrailRenderer.cs` *(new)*
  — additive soft-billboard renderer (procedural sprite, fade, smoke growth).
- `src/Allegiance.ClientCore.MonoGame/Game1.cs` — trail pool field + renderer
  construction/dispose, and the per-frame gather→`Update`→draw wired into the
  P234 missile block.

**Tests (must-fail-if-removed verified):** `MissileTrailParticlesTests` (+8 in
MonoGame.Tests) — spawn of the fire+smoke pair per missile, independent spawn per
missile, fire (0.33 s) expiring before smoke (2.0 s) after emission stops, pool
draining to empty, the fixed capacity bound (never exceeded), position
integration by velocity, reset clearing, and `RemainingFrac` decreasing with age.
Neutralising the pool's spawn loop turned 6 of the 8 red.

## 6. Build / test status

- Whole-solution build: **0 warnings, 0 errors.**
- `Allegiance.ClientCore.MonoGame.Tests`: **178 passed** (was 170 at P234; +8 mine).
- `Allegiance.ClientCore.Tests`: **83 passed** (unchanged).
- `Allegiance.Core.Tests`: **3061 passed**, 2 consecutive runs, the only failure
  each time the pre-existing `LiveTurretGunnerBindingTests` machine-state flake
  (documented; not this stage's regression — this stage touches no Core code).

Scope respected: no ship-afterburner reuse (out of scope), no missile audio
(P236), no changes to native reference (read-only) or P233/P234's code.

## 7. Future work (out of scope, natural reuse)

The ship's own afterburner (P220) currently has no particle trail at all (just a
sound loop). Native reuses this exact exhaust system for it, so the same
`MissileTrailParticles` pool + renderer is the natural retrofit when afterburner
particles are wanted — flip the emitter's tail/thrust vectors to the ship's
thrust direction and use the ship's afterburner smoke/fire parameters. Left as a
separate, optional follow-up per the task brief.

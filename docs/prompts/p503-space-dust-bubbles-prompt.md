# P503 — ambient space dust renders as "bubbles": match native DebrisGeo's decal look

## Why this task exists

**Domingo playtested live** on 2026-10-02: "the space dust looks like
bubbles". The dust comes from P449a (`1c50f116`):
`src/Allegiance.ClientCore.Game/Rendering/AmbientDustRenderer.cs` (path
post-P498; before P498 it was under `Allegiance.ClientCore.MonoGame`).

**Native** (`~/Development/Allegiance/src/effect/debrisgeo.cpp`):
- loads the decal `debris1bmp` (`:51`);
- places particles at `radius = rfocus·distance·sqrt(random)` (`:139`);
- varies **density** with speed: more particles below 75 m/s
  (`distancePerParticle … - (0.3 - 0.004·speed)`, `:90-93`);
- draws each one with `DrawDecal(…, scale = 0.1f / focus, …)` (`:166-184`).

**Native does NOT stretch particles with speed**; they're fixed decals.

**The port's guesses:**
- the on-screen size comes from an invented `Calibration = 0.13`
  (`AmbientDustRenderer.cs:~54-60`, "a visual tuning parameter");
- it uses an additive soft-alpha blend (`:~88-96`);
- it has a generated 32 px fallback sprite (`SpriteSize = 32`).

Domingo's client log shows the real decal loaded:
`ambient dust decal: 32x32 'debris1bmp.mdl'`.

## Scope

1. **Find why it looks like bubbles, with evidence.** Dump the decoded
   `debris1bmp` texture (pixels and alpha) to a PNG, and check whether it's
   a ring or soft disc versus a speck, against the native `.mdl` bitmap
   decode rules. Find native `DrawDecal`'s scale semantics in the native
   engine (what `scale` means in screen or world units) and native's decal
   blend mode. Compare a rendered port frame (headless `--capture`, used by
   earlier proofs) with what native's math produces.
2. **Fix it to native:**
   - the texture decode or alpha, if wrong;
   - the decal size derived from native `DrawDecal` semantics instead of
     `Calibration`, if those can be established;
   - the blend mode, and native's speed-based density if it's missing.
   If native's exact scale can't be established from source, say so, and
   pick the size from the native math with a stated assumption.
3. **Before/after captures** (headless `--capture` frames) in the report.

## Explicitly out of scope

- Other particle effects and the starfield.
- Camera FOV (50°, P449b).

## Verification

- Failing-first tests where the cause is testable.
- Whole-solution build with `/home/domingo/.dotnet/dotnet build`, 0
  warnings and 0 errors. Full suite via `scripts/run-tests.sh` (P471), 3
  consecutive runs. Cite `docs/TEST_FLAKES.md` for any remaining flake.
  **Report only numbers you actually measured.**
- **Commit before you finish**, and confirm it with `git log -1`.

## Deliverables

1. Committed on **`master`**, not pushed. Check with
   `git branch --show-current`.
2. The report `docs/reports/P503_SPACE_DUST_LOOK.md`.

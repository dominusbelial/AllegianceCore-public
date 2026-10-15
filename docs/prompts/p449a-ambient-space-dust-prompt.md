# P449a — native ambient space dust (DebrisGeo) for sense of speed

## Why this task exists

The gameplay-parity pass found that in open space the MonoGame client gives
almost no visual cue of the player's own speed or drift. Native's main cue
is an ambient dust field around the camera:

- `~/Development/Allegiance/src/effect/debrisgeo.cpp:17-205`, `DebrisGeo`.
  - Each frame it measures how far the camera moved.
  - For every `distancePerParticle` travelled it spawns a point ahead of the
    camera, at `distance = maxDistance·sqrt(rand)` along forward and a
    lateral radius of `rfocus·distance·sqrt(rand)` (`:128-150`).
  - Each point gets a bluish-white colour: r 0.5–0.75, g and b 0.75–1.0
    (`:145-150`).
  - A point's brightness is `1 - 0.25·t²`, so it fades out over 2 s. Points
    are removed when faded or beyond 500 m (`distanceSq > 250000`,
    `:108-120`). A jump or teleport clears the list.
  - It draws the `debris1bmp` decal at `scale = 0.1/focus`, skipping points
    within 20 m of the camera (`:163-190`).
- It's created in `WinTrek.cpp:3091-3121`, with a user density preference
  `Debris` (default 1.0) where 0 disables it.
- Read `Evaluate()`'s first half (`debrisgeo.cpp:60-100`) for the exact
  `maxDistance`, `distancePerParticle` and jump-detection constants. They
  weren't extracted in the scoping pass.
- **The port** has only damage-debris particles
  (`src/Allegiance.ClientCore.MonoGame/Game1.cs:114-121`,
  `Rendering/DamageDebris*`), and nothing tied to the camera's own motion.

## Scope

1. **Port `DebrisGeo` as a camera-relative particle system** in the MonoGame
   renderer, keeping the math pure and unit-testable in a small class
   separate from the drawing, as the port's other effects do. It covers
   distance-driven spawning, placement, colour, the fade curve, cull
   distance, the near-camera skip, and clearing on a jump.
   - For "jump", use native's own detection rule. At minimum a warp,
     teleport or sector change clears the field.
2. **Texture**: load the real `debris1bmp` artwork through the port's
   existing `.mdl`/bitmap asset loading, the same way other native bitmaps
   are loaded (the P428 lock sprites are a recent example). If the asset
   can't be decoded, fall back to a small procedural soft dot and say so.
3. **Density setting** mirroring native's `Debris` preference, put in the
   existing client `liveSettings` section (P378), default 1.0, 0 = off.
4. Draw it in every flight camera mode. Check that it works with the
   own-ship pose source regardless of whether P447b prediction has landed:
   it should follow whatever pose the camera uses.

## Explicitly out of scope

- Camera jiggle and FOV (P449b).
- Other particle systems, and the aleph proximity FOV effect.
- Other queued work: P438–P440, P445–P448.

## Verification

- Pure tests (these fail on unfixed code because the class doesn't exist):
  - No particles spawn while stationary.
  - Moving N·distancePerParticle spawns N.
  - Brightness at t = 0/1/2 s follows `1 - 0.25·t²`.
  - Points beyond 500 m or faded are culled.
  - A jump clears the field.
  - Spawn positions lie in the forward cone per the formula.
- A captured frame, or two frames at different speeds, from a real client
  run showing the dust. The client's `Capture` facility or a screenshot
  path is fine. Describe what's visible. If you can't capture, say so.
- Whole-solution build with `/home/domingo/.dotnet/dotnet build`, 0
  warnings and 0 errors. Full suite with
  `/home/domingo/.dotnet/dotnet test`, 3 consecutive clean runs. Only the
  pre-existing documented live-wire timing flakes are acceptable, and each
  one seen must be named.

## Deliverables

1. Code and tests, committed on **`master`**, not pushed. Check
   `git branch --show-current` before committing.
2. Report doc `docs/reports/P449A_AMBIENT_SPACE_DUST.md`. Include the native
   constants extracted with their file:line, the texture source or fallback,
   the setting name, and the capture evidence.

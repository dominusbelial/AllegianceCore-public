# P576b — the P576 sky "looks terrible" live: dome/stars/planet are placed at `cameraEye` in a CAMERA-RELATIVE view (so they're offset by the camera position), cut by the far plane, and possibly back-face culled from inside

## Why this task exists

Domingo (2026-10-08, live on 221, client @ `84391ac2`, `--aleph-glimpse
on`): "oh it looks terrible". Claude's grab of his real window:
`docs/prompts/p576b-sky-broken-live-2026-10-08.png`. The sky renders as huge
**hard-edged flat polygons**: a planet texture as a flat slab filling the
lower left, the nebula as straight-edged panels, and **black wedges** where
faces are missing.

**Cause (same class as P573's bug):** the game renders **camera-relative**
(the view matrix keeps the eye at the origin; every renderer subtracts
`cameraEye`, cf. `EnhancedExplosionRenderer.cs:120-122`, P573 fix `1bae5d0e`).
`Rendering/NativeSkyRenderer.cs` instead:
- `DrawDome`: `World = Matrix.CreateTranslation(cameraEye)` (`:~216`, "dome
  centred on the camera"), which in camera-relative space centres it at the
  camera's **world position**, thousands of units away;
- `DrawStars`: `pos = cameraEye + dir * StarRadius` (`:~182`);
- `DrawPlanet`: check for the same.
The dome is radius 7500 (native, per P576's own study) while the far plane is
≈ 7900 (P547 `InvRange` ≈ 1/7899), so once it's offset, large parts fall past the
far plane (the black wedges). Also `RasterizerState.CullCounterClockwise`
(`:~148`) while viewing the dome **from inside** may cull the faces you
should see. Native draws it in a Z-less layer with no clipping.

**Video (Domingo, 2026-10-08):** `/home/domingo/Videos/2026-10-08 08-09-24.mp4`. "I can see the art but when I
rotate the ship I see a black patch, kind of like the black-world bug." A
black region that moves with the view direction fits (a) the dome offset by
`cameraEye` (part of it falls behind the camera/past the far plane, so some view
directions show no sky), and/or (b) back-face culling of the dome from
inside. It should **not** be confused with P582b's backbuffer wipe (that one
wiped asteroids/stations too); check that rocks/stations stay visible in the
black patch to tell them apart. The proof must include **a slow 360° yaw
and pitch sweep** (frame strip every 30°) with no black patch in any
direction.

## Scope

1. Make every sky element camera-relative: dome/stars/planet centred at the
   origin of the view space (`World = Identity` + direction × radius, rotation
   only), never translated by `cameraEye`.
2. Never far-clip the backdrop: either scale the sky to fit well inside the
   far plane (e.g. radius = 0.5–0.9 × far, it has no parallax anyway) or draw
   it with its own projection (large far) and depth off. Prefer the simpler
   one and justify it.
3. Culling: use Cull None (or the winding that keeps the inside visible) for
   the dome; pin all states (P568 lesson) and restore them.
4. Planet size: verify against native (`planetRadius` byte 50–100 as the
   poster scale relative to the 7500 dome, P576 study lines ~44-50); compare
   with the P576 contact sheets for sensible on-screen size.
5. The aleph glimpse (P576-B) renders the destination sky through the same
   path: re-check it after the fix.

## Proof (real window, the thing P576 missed)

Real-window screenshots (P572 `live-proof.sh`/`--autoshots`) in **Ganymede,
Alderan and Rigel**, at the home station and after flying **≥ 3000 u**
away (that's where the camera-offset bug shows), from 4 view directions: a
seamless nebula backdrop, no hard edges or black wedges, the planet at a
sensible size, stars intact, and an aleph glimpse frame. Test: the dome's
world matrix has zero translation; the sky is drawn identically at two camera
positions 5000 u apart (same direction).
Report `docs/reports/P576b_SKY_FIX.md`.

## Verification (common)

- **Failing-first tests** for every fix: the test fails on today's code and passes after.
- **Native fidelity:** cite native file:line for every rule you port. If you find the
  spot-check's reading wrong, prove it with native code and say so.
- Whole-solution build, 0 warnings and 0 errors. Full suite via `scripts/run-tests.sh`
  (P471), 3 consecutive runs **on the final code**. Cite `docs/TEST_FLAKES.md`.
  **Report only numbers you actually measured. Don't re-run a failing suite until it
  comes up clean;** report each run as it happened.
- ⚠️ **Commit as soon as the code and tests are done, BEFORE the 3× suite**, then amend
  with suite numbers. **Include every new file** (`git status --untracked-files=all`).
  Confirm with `git log -1` and a clean `git status`.
- **Visual changes: the proof must include screenshots from the REAL client window**
  (the backbuffer, e.g. via `ALLEGIANCE_AUTOSHOT`, window 2560×1440). `--capture` /
  offscreen renders alone don't count (they hid the P547 and P562 regressions).
- Committed on **`master`**, not pushed. Check with `git branch --show-current`.

## ⚠️ Session rule: never end a turn while waiting

This session runs in one-shot mode: **your session ends the moment you finish a message.** Run long commands (the 3× `scripts/run-tests.sh` suite, live proofs, builds) **in the foreground**, blocking, one after another. **Never** start them in the background and then reply "waiting for it to finish". Two earlier sessions (P529, P542) did exactly that: the session ended mid-suite, the background processes were killed, and the suite results were never recorded. If a command might exceed a tool timeout, run each suite run as its own foreground call and record its numbers before starting the next.

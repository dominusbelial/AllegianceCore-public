# P589b — P589 review fixes: the galaxy field renders as HOLLOW SQUARES, moons render as square texture tiles, (the cut-out kept the frame edge and erased the galaxy), and some hero objects still carry field stars

## Why this task exists

Claude reviewed P589's own assets (2026-10-08, before deploying):
1. **Galaxy field broken:** `docs/reports/P589_assets/sheets/contact_galaxies.png`
   (copy: `docs/prompts/p589b-galaxy-field-hollow-squares.png`): nearly every
   one of the ~400 sprites is a **square/rectangular outline with an empty
   centre**, so the segmentation/feather mask is **inverted or offset**: the
   galaxy pixels go to alpha 0 and the tile border survives. In the real-window
   proof (`proof/Alderan_objects_galaxies.png` etc.) faint little squares are
   scattered across the sky. This violates Domingo's rule (object only, no
   edges), and the field is **ON by default**.
2. **Field stars around hero objects:** `sheets/contact_objects.png` (copy:
   `docs/prompts/p589b-hero-objects-star-specks.png`): the **Sombrero (M104)**
   has obvious field stars around it; **Ring (M57)** and **Pinwheel (M101)**
   have a few. Domingo: "we don't want photos with star backgrounds, but the
   natural object itself."

The rest is good: the Helix, Whirlpool and Lion objects, the sun + native lens
flare, planet + moon.

## Scope

1. Fix the galaxy cut-out: the mask must keep the galaxy (connected bright
   component around the segment's peak) and fade to 0 toward the tile
   border, never the reverse; reject sprites whose alpha mass touches the
   tile border or whose centre is transparent; reject stars (point-like,
   diffraction spikes). Regenerate the library; the sheet must show **filled
   galaxies with soft edges on black, no outlines**. Add an automated
   check: for each sprite, the alpha at the tile border is ≈ 0 and at the
   brightest object pixel it's ≈ 1 (fails on the current library).
2. Remove the remaining field stars from the hero objects (outside the object
   envelope → alpha 0; inside, keep the object's own clusters/H II regions),
   regenerate `contact_objects.png`; the Sombrero must show only the galaxy.
3. Re-take the real-window proof frames (Alderan/Ganymede/Rigel) and include
   crops at 400% around galaxy-field sprites showing no squares.

3. **Moons render as SQUARE rock-texture tiles** (Domingo live, 2026-10-08:
   "some really don't make sense"; screenshot
   `docs/prompts/p589b-live-moon-square-and-galaxy-squares.png`: a large
   grey mottled square at the top centre, plus the faint galaxy squares).
   The native `moonbmp`/`moon7bmp` art is a **surface texture for the 3D
   `moon.mdl` sphere**, not a disc image, so drawing it as a flat billboard
   gives a square. Fix: render moons as the native **`moon.mdl` sphere** with
   that texture (lit by the sector light, at infinity, like a small planet),
   or, if a billboard is kept, a properly **lit circular disc** generated from
   the texture (circular alpha mask + limb/terminator shading from the light
   direction). Check `moonnebbmp` the same way. No square edges at any
   size. Compare with the native planet posters' look.

## Proof

New sheets + crops + real-window frames; the automated alpha checks
(failing-first on the old library). Report `docs/reports/P589b_GALAXY_FIELD_FIX.md`.

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

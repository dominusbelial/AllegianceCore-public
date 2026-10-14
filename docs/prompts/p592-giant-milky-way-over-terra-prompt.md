# P592 — GIANT sky objects: our own galaxy (face-on Milky Way) over TERRA and ANDROMEDA over ALDERAN, each spanning ~half the sky, seamless, on the black sky

## Why this task exists

Domingo (2026-10-09), after the black sky (P588) and planet-style objects
(P589/P589b/P589c): "I wonder if we can bring a larger object that spans
across half the sky but still respects the black background … I like the idea
of having one particular sector show our own galaxy shot … it would have to
be a cut that doesn't look bad when you do that final stitching."

The 221 map (`m2xspiderw.igc`) has a sector **Terra (cluster 1057)**:
put it there.

**Image:** no photograph of the Milky Way from outside exists. Use the
standard **face-on scientific illustration**: candidates (verify each
page's licence/credit and resolution; prefer ≥ 8k):
- **NASA/JPL-Caltech/R. Hurt (SSC/Caltech)** "Milky Way" face-on artist's
  concept (Spitzer-based). NASA/JPL imagery is generally usable with credit;
  confirm the JPL image-use policy page.
- **ESA/Gaia/DPAC** face-on Milky Way illustration (often **CC BY-SA 3.0
  IGO**: share-alike; only use it if the licence fits, otherwise skip).
Do **not** use the Gaia all-sky (from-inside) map: it's a star band, against the
black-background rule.

## Scope

1. **Geometry (no stitching):** draw the galaxy as **one texture on one
   curved patch of the sky sphere** (~90° across; the angle as a named
   constant, tuned in the real window), never a full dome and never wrapping.
   Map it with the patch's own centre on its local equator (no pole pinching),
   camera-relative at infinity, depth off, never far-clipped, render states
   pinned, no mid-frame render-target switches (the P573/P576b/P582b lessons).
2. **Cut-out:** the galaxy only; soft radial feather to **pure black well
   inside the patch border** so the patch edge is never visible; remove any
   star specks outside the galaxy body (the P589c pipeline, zero-stars check).
3. **Look:** dim enough for combat readability (the P589 readability check:
   ship silhouettes over it), slightly brighter core, sector-light-agnostic.
   Placement direction seeded/fixed for Terra (away from the sun so the flare
   doesn't wash it out; avoid the planet).
4. A small `giant-objects.json` (cluster id → image, angle, direction,
   brightness) so another sector can get a giant later (e.g. Andromeda),
   with a `--giant-objects off` toggle; credits on the credits screen.

5. **Second giant: ANDROMEDA (M31) over ALDERAN (cluster 1052)** (Domingo,
   2026-10-09; Alderan is the showcase battleground). Same seamless single
   curved patch method, its own direction/angle in `giant-objects.json`.
   Image: a **real photo**: prefer the **ESA/Hubble full-disk Andromeda
   mosaic (2025, PHAST)** (CC BY 4.0; verify the release page, exact credit
   and pick a large-but-manageable rendition ≥ 8k); alternatives NASA GALEX
   (UV) or Spitzer (IR). It's crowded with **foreground Milky Way stars**:
   run the strictest star removal (P589c pipeline, including stars **inside the
   faint halo**: the Sombrero failed exactly there), zero-stars check;
   **if M31 can't reach zero stars without damage, report it and leave
   Alderan without a giant** rather than ship a starry one. Make sure it doesn't
   clash with Alderan's native planet or the sun/lens flare direction, and
   keep combat readability (Alderan is where the fights are).

## Proof (real window, P572 tooling)

Terra from 6+ directions incl. **looking at the patch edge** and a **slow pan
across the whole object** (frame strip): no seam, edge, stretch or banding;
a 300% crop of the feathered border; readability frames with ships in front;
frame time. Tests: the patch mesh UV/no-wrap, Terra-only placement, toggle.
Proof for Alderan/Andromeda too (same views + a 15v15 readability frame). Report `docs/reports/P592_GIANT_MILKY_WAY_TERRA.md`.

## Verification (common)

- **Failing-first tests** for every fix: the test fails on today's code and passes after.
- **Native fidelity:** cite native file:line for every rule you port. If you find the
  spot-check's reading wrong, prove it with native code and say so.
- Whole-solution build, 0 warnings and 0 errors. Full suite via `scripts/run-tests.sh`
  (P471), 2 consecutive runs **on the final code** (reduced from 3 per Domingo, 2026-10-09: the flakes are known/ledgered). Cite `docs/TEST_FLAKES.md`.
  **Report only numbers you actually measured. Don't re-run a failing suite until it
  comes up clean;** report each run as it happened.
- ⚠️ **Commit as soon as the code and tests are done, BEFORE the 2× suite**, then amend
  with suite numbers. **Include every new file** (`git status --untracked-files=all`).
  Confirm with `git log -1` and a clean `git status`.
- **Visual changes: the proof must include screenshots from the REAL client window**
  (the backbuffer, e.g. via `ALLEGIANCE_AUTOSHOT`, window 2560×1440). `--capture` /
  offscreen renders alone don't count (they hid the P547 and P562 regressions).
- Committed on **`master`**, not pushed. Check with `git branch --show-current`.

## ⚠️ Session rule: never end a turn while waiting

This session runs in one-shot mode: **your session ends the moment you finish a message.** Run long commands (the 2× `scripts/run-tests.sh` suite, live proofs, builds) **in the foreground**, blocking, one after another. **Never** start them in the background and then reply "waiting for it to finish". Two earlier sessions (P529, P542) did exactly that: the session ended mid-suite, the background processes were killed, and the suite results were never recorded. If a command might exceed a tool timeout, run each suite run as its own foreground call and record its numbers before starting the next.

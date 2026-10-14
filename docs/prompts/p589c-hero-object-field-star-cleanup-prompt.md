# P589c — remove the remaining field stars around the Sombrero (M104), Pinwheel (M101) and Ring (M57) hero objects; drop any object that still shows stars

## Why this task exists

Domingo's rule for the P589 distant objects: "we don't want photos with star
backgrounds, but the natural object itself, … like a painting against the
sky." P589b (`13a47f0d`, `docs/reports/P589b_GALAXY_FIELD_FIX.md`) fixed the
galaxy field, but Claude's review of
`docs/reports/P589_assets/sheets/contact_objects.png` (23:34) shows clear
field stars around **Sombrero M104** (many), **Pinwheel M101** (several)
and **Ring M57** (a couple). P589b's point-source filter has a "ceiling (55)"
that deliberately tolerates compact bright points.

## Scope

1. Stricter star removal **outside each object's envelope only**: detect
   field stars by shape (PSF-like compact profile, diffraction spikes, high
   peak-to-surrounding contrast) and inpaint them from the local background;
   **inside** the envelope keep the object's own H II regions/clusters
   (unchanged). Envelope from a smoothed luminance threshold of the object.
2. Automated check per hero object: count star-like detections outside the
   envelope after processing; **must be 0**. If an object can't reach 0
   without damaging it, **drop it** from the set (manifest + credits) and say
   so.
3. Regenerate `contact_objects.png` (on pure black, the in-game blend) and
   real-window frames of each kept object; include 300% crops of the
   surroundings.
Report `docs/reports/P589c_HERO_OBJECT_STAR_CLEANUP.md`.

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

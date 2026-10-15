# P588 — new DEFAULT sky: black background + the old point stars + the native planet + space dust; nebula domes (native and HD) become opt-in only

## Why this task exists

Domingo (2026-10-08, live with `--sky hd` @ `2d828028`): "I don't like the
looks, keep the old black sky; planets: keep the native planets only, with
the space dust we had; I'd rather have the black background; the sky looks
like improperly stitched fabric." He also didn't like the native nebula dome
(P576) earlier.

- **Before P576** the sky was black with the generic point-star shells
  (`Rendering/Starfield.cs`, three layers created in
  `Game1.Lifecycle.cs:~269-285`) plus the ambient space dust (native Debris
  pref, `debris1bmp.mdl` decals; client log: "ambient space dust:
  density=1").
- **P576** (`Rendering/NativeSkyRenderer.cs`) added per-sector seeded stars,
  the environment dome (`globeN`/`environmentNbmp`) and the planet poster.
  **P577/P577b** added `--sky hd` (NASA star cube + Hubble nebula domes,
  native planets).
- "Improperly stitched fabric" is the visible seams/stretch of the dome/cube
  mapping (P577 cube faces / P576 dome UVs). It doesn't matter for the new
  default, because there's no dome; mention the cause in the report (a
  follow-up can fix the seams if the domes are ever wanted again).

## Scope

1. Add a sky mode **`black` and make it the DEFAULT**: black clear colour,
   the **pre-P576 point starfield** (restore exactly the old `Starfield`
   shells/brightness, or the P576 seeded stars if they look the same: pick
   the one that matches the old look and justify), the **native planet
   poster** per sector (P576 placement/size, camera-relative, no far
   clipping), and the ambient **space dust** unchanged. **No nebula dome.**
2. Keep `--sky native` (P576 dome + planet) and `--sky hd` (P577/P577b) as
   opt-in options; update the settings, the credits screen (show HD credits
   only when HD assets are used) and `--help`.
3. The aleph glimpse (`--aleph-glimpse on`) in `black` mode shows the
   destination's black sky + its planet (+ stars).
4. Docs/report: note the dome seam cause ("stitched fabric") for later.

## Proof (real window, P572 tooling)

Default launch in Ganymede, Alderan and Rigel: black sky, stars, the native
planet visible at its native position/size, space dust, no dome, from 4
directions (no seams, no black wedges). `--sky native` and `--sky hd` still
work. Tests: the default resolves to `black`; `black` draws no dome.
Report `docs/reports/P588_BLACK_SKY_DEFAULT.md`.

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

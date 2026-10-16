# P577 — HD sky tier from real telescope data (NASA star map + ESA/Hubble/Webb or NASA nebulae, matched per native environment), behind P576's `Sky: Native | HD` switch; Native stays the default until Domingo approves

## Why this task exists

Domingo (2026-10-07): "could we have something just better? higher definition?
maybe something from real telescope public data? nasa" … "we will do both
right? native and new version via switch?" **Yes, both.** **P576** ported the
native per-sector sky (environment domes `globe1…10` + `environmentNbmp`, 43
`nebplnt` planets, seeded stars), added the `Sky: Native | HD` seam and produced
**contact sheets of all 10 environments and 43 planets**. Read its report
(`docs/reports/P576_SECTOR_SKY_AND_ALEPH_GLIMPSE.md`) and sheets first.
Native textures are 16 bpp (~2048²), which explains the banding.

## Scope

**HD sky tier (Domingo: "could we have something
just better? higher definition? … real telescope public data? nasa"), setting
`Sky: Native | HD` + `--sky native|hd`; default HD on desktop only after
Domingo approves the comparison sheets, Native until then; Android lower res.**

0. **Use P576's study** (don't redo it): Native's sky is a
   **library, not per-location art**: 10 environments `globe1…10.mdl` (a ~3 KB
   `MeshGeo` sky dome, `model environmentNbmp`, textured with
   `environmentNbmp.mdl`, ~8 MB ≈ 2048² at 16 bpp) and 43 planets
   `nebplntNNbmp.mdl` (same size/format); maps pick entries per cluster (221:
   6 environments, 9 planets across 13 sectors). Document the dome geometry
   (decode it with the P566 MDL decoder), UV/projection, draw order, blending,
   and how planets are sized/placed (`PosterImage`, `effect/`). Produce
   **contact sheets of all 10 environments and all 43 planets** (they are
   Domingo's reference and the basis for C's mapping).
1. **Stars:** NASA SVS **Deep Star Maps 2020**
   (https://svs.gsfc.nasa.gov/4851; Hipparcos-2/Tycho-2/Gaia DR2, 1.7 B stars,
   plate carrée 4k–64k EXR). Credit "NASA/Goddard Space Flight Center Scientific
   Visualization Studio". Convert the 8k or 16k map offline to a compressed
   cubemap (~2048²/face desktop, ~1024² Android), with an exposure/tonemap step.
   This replaces the seeded point stars in the HD tier only.
2. **Per library entry, not per sector:** an HD counterpart for each of the 10
   environments, i.e. a nebula layer composited from **ESA/Hubble or ESA/Webb
   (CC BY 4.0: visible credit, no implied endorsement;
   https://esahubble.org/copyright/)** or **NASA** (public domain; check each
   image for third-party content) imagery, **matched to the native
   environment's colour palette and bright-region layout** so each sector
   keeps its mood; and HD planets for at least every planet the 221 map uses
   (NASA public-domain planet maps, or a CC BY nebula/planet image), placed with
   native's lat/long/radius. Keep a `sky-hd.json` manifest: native id → HD
   asset → source URL, licence and credit text.
3. **Readability guard:** per-entry brightness/saturation caps so ships,
   projectiles and HUD stay readable (measure the luminance/contrast of a ship
   silhouette over each sky), plus a global "sky intensity" setting.
4. **Credits:** an in-game Credits/Licences screen and a `licenses/` folder
   listing every HD asset's credit, as CC BY requires (match the P546 CC0
   packaging pattern). Commit only assets whose licence is verified; record
   the licence page URL for each.
5. Proof: Native vs HD side-by-side real-window screenshots for the 6
   environments 221 uses (sky alone + with a dogfight), the readability
   numbers, memory per tier, and the glimpse (B) showing HD destination skies.

## Out of scope
Changing the Native tier; per-sector (rather than per-library-entry) art.

## Report
`docs/reports/P577_HD_SKY_TIER.md`: sources, licences, the manifest, the matching
rationale per environment (native sheet vs HD), readability numbers, memory,
and the side-by-side screenshots.


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

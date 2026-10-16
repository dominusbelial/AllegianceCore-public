# P589 — space objects against the black sky: the native SUN + LENS FLARE (per-sector light direction), plus moons (native art) and distant galaxies/small nebulae as cut-out objects (no domes)

## Why this task exists

Domingo (2026-10-08) chose a black sky with the native planets (P588: the
`black` sky default, no nebula domes) and said: "planets look really good
against the background, maybe we can do something similar with other space
objects? … they look good against the black background." Single objects on
black read well; wrap-around domes looked like "improperly stitched fabric"
(P576/P577).

## Scope (all drawn at infinity, camera-relative, no far clipping, depth off,
render states pinned, no mid-frame render-target switches: the P573/P576b/P582b lessons)

1. **Native sun + lens flare (native, ON by default):**
   - native `LensFlareImage` (`effect/efimage.cpp:573-760`): elements
     `g_lensFlareData` (`:601-612`): `(index 2, pos −1.00, scale 1.50,
     White)`, four Blue elements at −0.75/−0.50/−0.25 with scales
     1.00/0.50/0.25, the mirrored three at +0.25/+0.50/+0.75, and
     `(index 1, +1.00, 1.50, White)`; images `AWF_EFFECT_LENS_FLARE_MID_RING` = **`f104bmp`**, `STAR_AURA` =
     **`f105bmp`**, `END_RING` = **`f106bmp`** (`effect/efart.h:8-10`; loaded at
     `efimage.cpp:631-655`); the newer render at `:~706+`. Created in `WinTrek.cpp:3158`,
     driven by `SetLightDirection(pcluster->GetLightDirection())`
     (`WinTrek.cpp:3614-3617, 3634`): the per-cluster light direction the port
     already has (P530 `SectorLighting`).
   - Port it faithfully: the flare appears when the light direction is in
     view, its elements laid out along the line through the screen centre,
     occlusion per native (check whether native hides it behind
     ships/asteroids; if it does, use the P547 depth). A visible **sun
     disc/aura** at the light direction (native's STAR_AURA element) so each
     sector has a light source.
2. **Moons (non-native placement, native art, ON by default in `black`
   mode, toggle `--moons off`):** unused native art `moonbmp.mdl`,
   `moon7bmp.mdl`, `moonnebbmp.mdl` (+ `moon.mdl`); 0–2 moons per sector,
   seeded by cluster id (deterministic), placed near the native planet
   (smaller, a sensible angular offset), lit consistently with the sector
   light direction if the art allows.
3. **Distant galaxies / small nebulae as cut-out objects (non-native, ON by
   default in `black` mode, toggle `--distant-objects off`):** reuse P577's
   Hubble/ESA set (and the dropped Whirlpool M51 heic0506a, which fits as a
   distant object), cut out with soft alpha edges (no rectangles/seams),
   2–4 per sector, small angular sizes, seeded per cluster, dim enough not to
   compete with ships/HUD (readability check). Credits/licences on the
   credits screen (CC BY 4.0 requires it), and `sky-hd.json`-style manifest
   entries.
   **Cut-out method (Domingo: "we will have to look for objects that can be
   edited, edges removed?"): automatic, no hand editing.** Choose images that
   are **already an isolated object on dark space** (galaxies first, then
   compact nebulae/star clusters; skip frame-filling fields like Pillars or
   Carina). Make the black transparent (additive blend, or luminance→alpha
   with a black-point lift), then apply a **soft radial feather** to the
   image border so no rectangle/corner/seam can show. Produce a **contact
   sheet of every candidate against pure black** (the actual in-game blend)
   and reject any with visible edges, banding or background noise; report
   rejections. A small `tools/` script does the processing (deterministic,
   re-runnable), and the processed PNGs + licences are committed.
   **"Added just like the planets"** (Domingo): moons and distant objects use
   the **same poster path as the native planet** (P576/P588
   `NativeSkyRenderer` planet poster: a billboarded image at a direction on
   the infinity shell, sized by an angular radius, same blending/lighting
   treatment), just with more entries per sector. No new rendering style.
   **Domingo's art direction (2026-10-08): "we don't want photos with star
   backgrounds, but the natural object itself, similar to the planet size;
   it can be positioned like a painting against the sky box."** So:
   - **No star fields in the image.** Only the object (galaxy, nebula,
     cluster core) survives. Remove foreground/background stars: pick images
     with few field stars, then remove the remaining point sources (detect
     small bright compact blobs outside the object mask, inpaint from the
     surrounding background; a scripted step, not hand editing). The
     candidate contact sheet must show no star specks around the object.
   - **Planet-like size:** angular size comparable to the native planet
     posters (same order as `planetRadius` on the dome), not tiny specks and
     not sky-filling.
   - **Placed like a painting:** a single poster at a fixed direction,
     exactly like the planets; our own sky stars stay the only stars.
   **Domingo's candidate source (2026-10-08): NOIRLab.** Example: the **Helix
   Nebula (NGC 7293)**, a planetary nebula (an isolated round ring object,
   ideal as a "painting"), release https://noirlab.edu/public/news/noao0307/,
   image page https://noirlab.edu/public/images/noao0307a/ (16000×16000;
   large JPEG https://storage.noirlab.edu/media/archives/images/large/noao0307a.jpg).
   Credit: "NASA, NOAO, ESA, the Hubble Helix Nebula Team, M. Meixner (STScI),
   and T.A. Rector (University of Alaska Anchorage/NSF NOIRLab)". **Licence:
   NOIRLab images are CC BY 4.0** unless noted
   (https://noirlab.edu/public/copyright/): clear, visible credit. Note it has
   a dense star field: run the star removal and check the sheet. Add NOIRLab
   to the candidate sources alongside ESA/Hubble, ESA/Webb and NASA; planetary
   nebulae (Helix, Ring M57, Cat's Eye, Dumbbell), compact galaxies and globular
   cluster cores are the best "single object" types.
   **More of Domingo's candidates:** **NGC 2392** (planetary nebula; call it
   "Lion Nebula" / NGC 2392, as NASA no longer uses its old nickname, per the
   editor's note on the page):
   (a) Webb MIRI image https://science.nasa.gov/asset/webb/lion-nebula-miri-image/
   (credit "NASA, ESA, CSA, STScI; Image Processing: Alyssa Pagan (STScI)";
   984×717 TIF/PNG/JPG; mid-IR false colour);
   (b) https://www.nasa.gov/image-article/beautiful-end-stars-life/ (the classic
   view; get the image URL and the exact credit from the page: composites
   often credit several observatories). NASA imagery is generally public
   domain, but **verify and record each page's credit/terms** (ESA/Webb releases
   are CC BY 4.0). Pick the one that cuts out best (or keep both as two
   different sector objects).
   **Galaxy field (Domingo: "add hundreds of galaxies, different sizes,
   against the black background"):** a deep-field look behind everything.
   - Source: the **Hubble Ultra Deep Field** (ESA/Hubble, CC BY 4.0, e.g.
     heic0406a; verify the release page) and/or the **Webb deep fields**
     (check each page's terms/credit). Script: segment the field into
     individual galaxies (threshold + connected components), cut each out
     with a soft feathered alpha, drop stars (point-like/diffraction-spike
     sources), and keep a library of a few hundred varied sprites (spirals,
     ellipticals, edge-ons; red and blue).
   - Placement: **hundreds per sector**, seeded by cluster id (stable),
     **log-distributed sizes** from a couple of pixels up to a small disc
     (well below the planet size), **dim** (a readability check vs ship
     silhouettes and the HUD), a random orientation each.
   - Performance: **one batched/instanced draw** at infinity (same
     camera-relative, depth-off, never-clipped path as the planet posters);
     measure frame time.
   - Toggle `--galaxies off`; ON by default in the `black` sky.
   - **Same rules as the other objects** (Domingo: "same rules"): object only,
     no star specks/background in any sprite (reject on the contact sheet),
     cut out with feathered edges, placed like paintings on the black sky.
   - Britannica (Domingo shared https://www.britannica.com/science/nebula)
     is **reference only** (its images are copyrighted): use it for object
     names, then source images from NASA/ESA/NOIRLab.
   **Art reference (Domingo):** the "Council of Giants" post
   https://www.reddit.com/r/spaceporn/comments/1d4f313/the_council_of_giants_the_12_galaxies_in_our/
   (the ring of large galaxies around the Local Group, each shown as a big
   individual galaxy on black). **Reference only:** a user-posted image of
   unclear rights; don't use it. Use the idea: a few **large, named "hero"
   galaxies** per sector (bigger than the deep-field sprites, smaller than
   or similar to a planet), each sourced individually from NASA / ESA/Hubble /
   ESA/Webb / NOIRLab (verify each page's credit and licence), plus the
   hundreds of small deep-field galaxies. Identify the council members from a
   reliable source (the 2014 McCall study / NASA/ESA pages) rather than the post.
   **"Little Red Dots" (Domingo; https://en.wikipedia.org/wiki/Little_Red_Dots):**
   generate them **procedurally** (no imagery/licensing): the compact, very red,
   point-like JWST early-universe objects. A small soft disc with a bright
   compact core and a faint halo; hue from deep red to orange-red; varied
   brightness; always **tiny** (≈1–4 px at 2560×1440); a few dozen per sector,
   sparse, seeded per cluster, drawn in the same instanced galaxy-field batch.
   Unit tests for the generator (deterministic per seed, colour/size ranges).
4. Settings + `--help`; the aleph glimpse shows the destination's objects too.

## Proof (real window, P572 tooling)

Ganymede, Alderan, Rigel: looking toward the sun (flare elements visible,
moving correctly as you turn), toward the planet + moon(s), and toward
distant objects; a frame strip turning across the sun; readability (ship
silhouettes over each object); frame time. Tests: the flare element layout
vs native's table, the seeded placement determinism, toggles.
Report `docs/reports/P589_SUN_FLARE_MOONS_DISTANT_OBJECTS.md`.

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

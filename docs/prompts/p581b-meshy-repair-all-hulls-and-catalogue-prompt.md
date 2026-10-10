# P581b — Meshy repair for ALL remaining hulls + replacement catalogue (after the P581 scout pilot):  audit every one for floating pieces, repair them automatically (snap detached parts onto the hull, drop crumbs), and keep the repaired set as a CATALOGUE of available replacement models any hull can use through `ship-art.json`

## P581b — the rest: run P581's repair pipeline on ALL other Meshy hulls + the replacement catalogue

**Prerequisite:** P581 (Adv Scout pilot pass) has landed and **Domingo
approved its result**. Reuse P581's `tools/meshy-repair/` pipeline
unchanged unless his review asked for changes (read P581's report and any
notes). Then do the full scope below (the audit of every Meshy model, repair of every
repairable one, the catalogue + `catalogue:<id>` mapping + the inspector
catalogue browser), skipping the Adv Scout (already done).

## Why this task exists

Domingo (2026-10-07, P565 inspector, hull 1 Adv Scout id 13): "both versions
look cool, I think we should repurpose the meshy version but first we need to
fix the mesh … 2 of the protruding 'wings' of the ship [are] separated from
the hull, so that gap is all black space" (screenshot
`~/Pictures/Screenshots/Screenshot From 2026-10-07 18-52-40.png`). And: "keep
it on the list of available models to replace any ugly ship."

**Claude's analysis of `Content/Models/dreg-scout2.fbx`** (Blender headless,
script in the report brief below): 13,374 verts / 19,994 faces after welding
UV-seam duplicates (Meshy splits vertices per UV island: 519 raw "parts"
before welding, **142 after**). **4 large detached pieces**, mirrored ±Y: the
two main wings (~548 verts each, extent ≈ half the ship, **min gap to hull
0.061 = 3.2% of ship length**) and two smaller blades (~300 verts, **gap
0.148 = 7.8%**), plus ~135 tiny crumbs (3–21 verts, most touching the
hull). Coloured render: hull grey, the 4 pieces red/orange/blue/green.

Method used (reproduce it, put it in `tools/meshy-repair/`):
`bmesh.ops.remove_doubles(dist=0.0005)` → connected components → the
largest component = hull → BVH of hull faces → `find_nearest` from each
component's vertices = min gap. Use Solid/Workbench shading with per-piece
object colours for review renders (not the applied texture).

## Scope

1. **Audit every Meshy ship model** (`src/Allegiance.ClientCore.MonoGame/Content/Models/*.fbx`
   used as ship art): per model, the welded piece count, each significant
   piece (≥ 20 verts) with size and gap to hull, crumb count, non-manifold
   edges and flipped normals, and a 4-view coloured render (top/side/front/
   perspective). Output `docs/reports/P581_assets/audit/<model>.png` + an
   `index.html` sheet + `audit.json`. Grade each: **clean / repairable /
   poor** (with the rule used).
2. **Automatic repair pipeline** (Blender headless, deterministic,
   `tools/meshy-repair/repair.py` + a shell wrapper): for each significant
   detached piece, **translate it along the shortest-gap direction until it
   overlaps the hull by a small margin** (preserves shape and UVs/texture;
   never deform the hull); optionally bridge if translation would visibly
   distort the silhouette (report which). Delete crumbs under a size
   threshold that don't touch the hull. Recalculate normals consistently.
   **Keep UV seams** (weld only for analysis, or weld only coincident
   vertices with identical UVs). Export to a **new** file
   `Content/Models/repaired/<model>.fbx`; **never overwrite originals.**
   Before/after renders and gap numbers per model.
3. **Repair the Adv Scout (`dreg-scout2`) first** and prove it in the real
   window: the P565 inspector (`--inspect --inspect-hull "Adv Scout"`,
   Meshy art, side-by-side) and in flight, with no black slits from any
   angle. Then run the pipeline over every model graded repairable.
   **Domingo's decision: the Adv Scout itself stays on its ORIGINAL native
   model** (the default; add no `ship-art.json` entry for it). The repaired
   `dreg-scout2` goes into the catalogue (item 4) as an available design for
   replacing some other hull later. Prove the repair in the inspector by
   previewing it on the Adv Scout, without keeping that choice.
4. **Replacement catalogue (Domingo: "keep it on the list of available
   models to replace any ugly ship"):** a committed catalogue
   `assets/ships/catalogue.json` listing every usable model (native and
   repaired Meshy) with id, source, grade, size class and a thumbnail.
   Extend `ship-art.json` so a hull can map to **any catalogue model** (not
   only its own Meshy counterpart): `{ "<hull>": "native" | "meshy" |
   "catalogue:<model-id>" }`, with scale/orientation normalised to the hull's
   native length and forward axis (reuse the P511/P520 orientation data in
   `docs/reports/P520_SHIP_ORIENTATION_AUDIT_assets/orient_extents.json`).
   In the P565 inspector, add a **catalogue browser** (cycle catalogue
   models on the current hull, `K` keeps the choice). Repaired models
   are preferred over raw Meshy automatically.
5. Gameplay is untouched: collision/hit-test stay native (art only).

## Proof

- The audit sheet for all Meshy models; before/after for every repaired one
  with gap numbers going to ≤ 0 (overlap) for repaired pieces.
- Real-window screenshots (P572/P565 tooling): the Adv Scout repaired vs
  raw vs native from 6 angles, plus one hull mapped to a catalogue model of
  another hull, in flight.
- Tests: catalogue/override resolution (`--ships` flag > per-hull override
  > native default; `catalogue:<id>` resolves with normalised scale), the
  missing-asset fallback.
- Report `docs/reports/P581b_MESHY_REPAIR_ALL_AND_CATALOGUE.md`.

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

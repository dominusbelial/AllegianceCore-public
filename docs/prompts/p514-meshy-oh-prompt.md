# P514 — Meshy regeneration: Omicron (oh), submit → convert → wire in → live proof

## Why this task exists

**Domingo approved spending Meshy credits on 2026-10-03** ("i can get a few more
credits dont worry about it"). P510 (prep, no spend) produced
`docs/reports/P510_MESHY_SUBMISSION_PLAN.md`: the verified per-civ model
list, the reference-render paths and the costs. It also added Phoenix's civ
mapping. Check that P510's commit and the plan exist
(`git log --oneline | grep P510`). If they don't, **stop and report**.

This task does **Omicron only**. Claude's earlier count was 12 distinct
player-flyable native models; the P510 plan is authoritative. Today
Omicron's ships mostly draw as the `bios_scout` fallback.

**Follow the proven recipe exactly:**
`~/.hermes/skills/software-development/allegiancecore-port-workflow/references/meshy-faction-ship-swap.md`.
Read it in full, including Footguns. This recipe produced the Bios, GT and IC
regenerations (P351/P352/P353). The pipeline is
`~/Development/allegiance-tools/reference/esrgan-work/meshy_pipeline.py`, and
the key is in `~/meshyApi.txt`; never print or commit it.

## The full pipeline (Domingo confirmed: reference images MUST come from the original decoded models)

1. **Render from the native models.**
   `~/Development/allegiance-tools/reference/mdl_render_scripts/batch_render.sh <model…>`
   reads `~/Development/Artwork/<model>.mdl`, decodes it (`reference/mdl_parse.py`,
   plus `reference/decode_texture.py` for the native texture), and renders the
   **4 angles** (front, three-quarter, side, back) plus `_tex` in Blender.
   - ⚠️ Its `OUT=` points at a **stale scratchpad from the 2026-09-22
     session** (`/tmp/claude-1000/.../f943d74a…/scratchpad/mdl_render`).
     Write to `~/Development/allegiance-tools/reference/mdl_render_pngs/`
     instead, which holds the existing 186 renders (`<model>_front.png`, …).
   - Use Solid/matcap shading for mesh sanity checks.
2. **Meshy Multi-Image-to-3D.**
   `reference/esrgan-work/meshy_pipeline.py` (`submit_multi_image`) submits
   those 4 renders as **one** job per model. Never use text-to-3D or invented
   art.
3. **Wire in** per `meshy-faction-ship-swap.md`.
   ⚠️ That recipe says it's a "pure content swap, NO code change", which only
   held for civs whose P287 asset slots already existed (Bios, GT, IC).
   **Dreg, TF, OH and Giga have only 3 slots (scout, fighter, bomber), and
   Phoenix has none.** New asset slots are needed: the P287 manifest
   (`Content/Models/MODELS_MANIFEST_P287.json`/`.md`), `Content.mgcb`, and the
   `HullModelResolver` / `RosterPlanner` class mapping. Follow the existing
   slot conventions exactly. P510 defines the slot names in its plan.

## Scope

1. **Credit check first.** Read the balance:
   `GET https://api.meshy.ai/openapi/v1/balance` (read-only). If it's below
   this civ's cost (about 30 per model), **stop and report** the shortfall.
   Don't partially submit.
2. **Submit** this civ's models from the P510 reference renders. Wait for
   every job to finish (poll, don't busy-spin). Download the GLBs to a
   per-civ dir under `~/Development/allegiance-tools/reference/`. Record
   each job id and credits used.
3. **Convert and install** per the recipe: staging, then `glb_to_fbx.py`,
   then `extract_meshy_textures.py`, then `Content/Models` + `Content.mgcb`,
   then the `RosterPlanner` / `HullModelResolver` mapping. Use the exact
   asset names the resolver expects.
4. **Build** the MonoGame client with MGCB: 0 warnings, 0 errors.
   **Orientation check (P520):** after installing, run P520's orientation
   audit check over this civ's new assets and fix any rotated model with
   the per-asset override table, not by editing the global correction.
   Include the contact sheet in the report.
5. **Live proof, per the recipe:** `vizfight roster <port> oh` networked
   proof. Every Omicron player hull now resolves to a dedicated model, not
   `bios_scout`. Re-run the resolver coverage count and report
   before/after fallbacks. Do the scale check, and the dark-livery check
   (Solid/matcap shading for mesh checks).

## Explicitly out of scope

- Other civs. Each has its own task.
- Pipeline changes.
- Pushing.

## Verification

- The resolver coverage test or count: Omicron fallbacks before → after.
- Whole-solution build with `/home/domingo/.dotnet/dotnet build`, 0
  warnings and 0 errors. Full suite via `scripts/run-tests.sh` (P471), 3
  consecutive runs. Cite `docs/TEST_FLAKES.md` for any remaining flake.
  **Report only numbers you actually measured**, including credits spent.
- **Commit before you finish**, and confirm it with `git log -1`. Large
  binary assets go where the recipe says; check `.gitignore` and LFS
  conventions from P351-P353.

## Deliverables

1. Committed on **`master`**, not pushed. Check with
   `git branch --show-current`.
2. The report `docs/reports/P514_MESHY_OH.md`. Cover the
   job ids, credits spent, the coverage before and after, and the proof
   screenshots.

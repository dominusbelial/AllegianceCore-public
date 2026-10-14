# P510 — Meshy regeneration for the remaining civs, stage 1 (NO credit spend): reference renders, Phoenix mapping, priced submission manifest

## Why this task exists

**Domingo (2026-10-03):** "we have pending meshy work and we need to map all those
models". Three civs got native-reference Meshy regenerations on 2026-09-22
(Bios 13, GT 16, IC 16). The others still use the early generic batch. Running
the client's own `HullModelResolver` over all 295 player-flyable hulls
(GroupId ≥ 0) per civ gave:

- **Dreghklar, Technoflux, Omicron, Gigacorp, Belters:** only scout, fighter
  and bomber assets. **177 of 295** hull resolutions fall back to the
  `bios_scout` model.
- **Phoenix (civ 4): no mapping at all.** It's missing from
  `HullModelResolver.CivKeyById` (`src/Allegiance.Core/Content/HullModelResolver.cs:~88-92`)
  and from `RosterPlanner.CivModelPrefixes`
  (`src/Allegiance.Core/Content/RosterPlanner.cs:~90-102`), so every Phoenix
  ship draws as the fallback.
- Belters and Rix have **no dedicated native art by the original game's
  design** (P350 finding). They stay out of scope.

Claude counted the distinct **player-flyable native `ModelName`s** per
remaining civ, using `RosterPlanner.CivModelPrefixes` (plus Phoenix's
`dn_faphsh` family):

| Civ | Models |
|---|---|
| dreg | **6**: dn_fadrshfig, dn_fadrshstl, dregtt, drgas, dregcar, drgmf |
| tf | **10**: rainscoutv2, rainfigv2, rainsfv2, rainintv2, dn_fateshbmb, tffig, tfsb2, tftransport, … |
| oh | **12**: faohshsctb, faohshafig, faohshfig, faohshstl, faohshhint, faohshint, faohshbmb, faohshcrv, … |
| giga | **7**: wc_gcfigx, wc_gcfig, wc_gchvyx, wc_gchvy, wc_gcint, wc_gcbmbx, wc_gchttx. **4 already have reference renders** in `~/Development/allegiance-tools/reference/mdl_render_pngs/` |
| phoenix | **11**: dn_faphshsct, dn_faphshfig, dn_faphshstl, dn_faphshint, dn_faphshbmb, dn_faphshvng, dn_faphshbmbs, … |

That's **46 models, about 1,380 Meshy credits at 30/model. The current
balance is 1,290**, read-only checked 2026-10-03 via
`GET https://api.meshy.ai/openapi/v1/balance`, with the key in
`~/meshyApi.txt`. Spending is Domingo's decision, so this stage spends
**nothing**.

**Procedure reference (read it in full):**
`~/.hermes/skills/software-development/allegiancecore-port-workflow/references/meshy-faction-ship-swap.md`
(the P351/P352/P353 recipe). The pipeline is
`~/Development/allegiance-tools/reference/esrgan-work/meshy_pipeline.py`.
Earlier render scripts and outputs are under `~/Development/allegiance-tools/reference/`
(`mdl_render_scripts/`, `mdl_render_pngs/`).

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

## Scope (no Meshy API calls that create tasks or spend credits)

1. **Re-derive the exact model list** with the port's own code, never from
   memory. Use `RosterPlanner`, the resolver and the real `PCore001.igc`,
   restricted to player-flyable hulls. Confirm or correct Claude's 46.
   Account for models that an already-regenerated asset could legitimately
   cover; the resolver's class-sharing rules say which.
2. **Phoenix mapping:**
   - add `[4] = "phoenix"` to `HullModelResolver.CivKeyById`, plus its prefix
     family to `RosterPlanner.CivModelPrefixes` (verify the prefix from real
     hull data);
   - make Phoenix resolve to its **existing** generic class assets in the
     meantime, if the resolver's rules allow, documented;
   - add tests.
3. **Reference renders:** render the 4-angle native `.mdl` reference images
   for every model that lacks them, with the existing Blender render
   scripts. Use Solid/matcap shading for the checks: the
   `feedback_blender_solid_shading_for_mesh_checks` lesson applies. Verify
   each mesh decodes sensibly and report any that don't.
4. **The new asset-slot plan:** the exact new P287 asset names per civ and class, following the existing naming, plus which resolver/RosterPlanner and manifest entries each needs. P511-P515 implement them.
5. **The priced submission manifest:** commit
   `docs/reports/P510_MESHY_SUBMISSION_PLAN.md` with:
   - per civ: the model list, render paths, credits each and the total;
   - a recommended **priority order** for spending within the 1,290-credit
     balance. Suggest IC/GT-compatible play value first, then civs most
     likely to be played; Domingo decides;
   - which of Domingo's current playtest sides benefit. 221 currently runs
     **IC vs GT** (`civIds [25, 3]`).

## Explicitly out of scope

- **Any Meshy submission or credit spend.** That's a later task, per civ,
  after Domingo approves the plan.
- Belters and Rix art.
- Changing the GLB→FBX→MGCB pipeline.

## Verification

- Tests for the Phoenix mapping, failing first.
- Whole-solution build with `/home/domingo/.dotnet/dotnet build`, 0
  warnings and 0 errors. Full suite via `scripts/run-tests.sh` (P471), 3
  consecutive runs. Cite `docs/TEST_FLAKES.md` for any remaining flake.
  **Report only numbers you actually measured.**
- **Commit before you finish**, and confirm it with `git log -1`. The renders
  are outside the repo; commit only the plan, the code and the tests.

## Deliverables

1. Committed on **`master`**, not pushed. Check with
   `git branch --show-current`.
2. `docs/reports/P510_MESHY_SUBMISSION_PLAN.md`, as above.

# P352 — Wire the 16 Meshy-regenerated Ga'Taraan (GT) ship models into the MonoGame port

## Context

`AllegianceCore` is a C# port of native FreeAllegiance
(`~/Development/Allegiance`, read-only reference, never modify it).

Direct continuation of this session's arc: P351 did the same wiring
for Bios's 13 ships. This task is the GT (Ga'Taraan) equivalent — GT's
full 16-class P287 roster (`RosterPlanner.FactionOrder`'s `gt` entry)
has now been regenerated via Meshy's multi-image-to-3D, each one fed 4
real reference images rendered from the ACTUAL original native `.mdl`
geometry+texture (via `~/Development/allegiance-tools/reference/mdl_parse.py`
→ `decode_texture.py` → `textured_render.py`), replacing the old,
weaker Meshy models. Domingo's own words motivating this arc: "the
meshes are not great, we need to continue with the new hull pass on
meshy."

**The 16 new GLBs are already downloaded and waiting, split across two
directories** (both real Meshy multi-image-to-3D output,
`should_texture: true`, `meshy-7`, 2K texture, real PBR):

- `~/Development/allegiance-tools/reference/gt_regen_glbs/` — 12 of
  GT's 16 GLBs (this directory also holds unrelated Bios/GigaCorp GLBs
  from earlier in the arc — only use the ones named below):
  `cap502.glb`, `cap01.glb`, `utl18a.glb`, `utl93b.glb` (these 4 are
  GT's own real dedicated hulls reusing the same underlying native
  model as the equivalent Bios ship — not a mistake, GT and Bios share
  some native geometry by the original game's own design),
  `wc_gtpt.glb`, `wc_gtcon.glb`, `cap100.glb`, `wc_gtfig.glb`,
  `cap415.glb`, `fig22.glb`, `wc_gtmnr.glb`, `wc_gtstang.glb`.
- `~/Development/allegiance-tools/reference/gt_ic_missing_glbs/` — the
  remaining 4, generated in a later session after a planned reboot:
  `wc_gtpod.glb`, `fig12.glb`, `wc_gtsct.glb`, `wc_gtsf.glb`. This
  directory also holds 7 Iron Coalition GLBs for a separate future
  task (P353) — don't touch those here.

All 16 verified clean this session by direct visual inspection of
their Meshy thumbnails (single coherent mesh, no fragmentation,
correct proportions, matches each one's real native reference).

## The exact mapping (source GLB → P287 asset name to replace)

Pulled directly from a live `RosterPlanner.Plan` run
(`vizfight roster 0 gt`), not from memory — trust this table:

| Source GLB | Directory | P287 asset (existing `Content/Models/<asset>.fbx` + `Content/Textures/ships/<asset>.png` to REPLACE) | Native display name |
|---|---|---|---|
| `utl18a.glb` | `gt_regen_glbs/` | `gt-ass` | Corvette |
| `wc_gtpt.glb` | `gt_regen_glbs/` | `gt-bomber` | PT Bomber |
| `cap502.glb` | `gt_regen_glbs/` | `gt-carrier` | Attack Carrier [SR] |
| `wc_gtcon.glb` | `gt_regen_glbs/` | `gt-con` | Sm Std Constructor |
| `cap01.glb` | `gt_regen_glbs/` | `gt-cruiser` | Lt Cruiser |
| `cap100.glb` | `gt_regen_glbs/` | `gt-dev` | Devastator |
| `wc_gtfig.glb` | `gt_regen_glbs/` | `gt-fighter` | Fighter |
| `cap415.glb` | `gt_regen_glbs/` | `gt-hod` | Harbinger of Doom |
| `fig22.glb` | `gt_regen_glbs/` | `gt-int` | Hvy Interceptor |
| `wc_gtmnr.glb` | `gt_regen_glbs/` | `gt-miner` | Miner |
| `wc_gtstang.glb` | `gt_regen_glbs/` | `gt-mustang` | Mustang |
| `wc_gtpod.glb` | `gt_ic_missing_glbs/` | `gt-pod` | Lifepod |
| `fig12.glb` | `gt_ic_missing_glbs/` | `gt-rd` | Lxy Patroller |
| `wc_gtsct.glb` | `gt_ic_missing_glbs/` | `gt-scout` | Scout |
| `wc_gtsf.glb` | `gt_ic_missing_glbs/` | `gt-sf` | Stealth Fighter |
| `utl93b.glb` | `gt_regen_glbs/` | `gt-tt` | Hvy Troop Transport |

All 16 of GT's real P287 roster classes — the full lineup, not a
partial set. All 16 existing `Content/Models/gt-*.fbx` +
`Content/Textures/ships/gt-*.png` pairs already confirmed present on
disk (already wired into `Content.mgcb`/`HullModelResolver.ShipAssets`
— no new asset-registration code needed).

## Scope

1. **Convert each GLB to FBX** using
   `~/Development/allegiance-tools/reference/glb_to_fbx.py` (headless
   Blender: `blender -b --python glb_to_fbx.py -- input.glb output.fbx`).
   Strips materials to geometry+UVs+normals only, triangulates,
   decimates to a 20k-tri cap, exports FBX binary. **This is a
   reconstruction** of the original `/tmp/p287stage/glb_to_fbx.py`
   used for P287/P351 (lost to a tmpfs wipe on reboot) — rebuilt
   2026-09-22 from the kimi-k3 pipeline review's documented behavior
   (`materials.clear()` + `embed_textures=False`, 20k decimate cap) and
   already test-verified end-to-end this session on a real GT GLB
   (`wc_gtpod.glb` → 364,694 → 20,000 tris, re-imported clean: 1 mesh,
   0 materials, UVs intact). Trust it, but if any of the 16 conversions
   look visually wrong after step 4's build, that script is the first
   place to check. **The output filename must be the P287 ASSET name,
   not the source GLB's own name** (e.g. `utl18a.glb` → `gt-ass.fbx`)
   — rename/stage the GLBs under their target asset names before
   conversion, or adapt invocation accordingly; either is fine, just
   get the final filenames right.
2. **Extract each model's real texture** using the exact established
   script, `extract_meshy_textures.py` (repo root) — it reads
   `baseColorTexture` directly from the source `.glb` and writes
   `Content/Textures/ships/<asset>.png`. **Gotcha**: it looks up
   sources as `<meshy_dir>/<asset>.glb` (asset name, e.g. `gt-ass.glb`)
   — it does NOT know about native `ModelName`s like `utl18a.glb`.
   Stage all 16 GT GLBs (from both `gt_regen_glbs/` and
   `gt_ic_missing_glbs/`, per the table above) into one scratch
   directory, copied/symlinked and renamed to their target asset names
   (e.g. `utl18a.glb` → `<staging>/gt-ass.glb`), then run
   `python3 extract_meshy_textures.py <staging_dir> src/Allegiance.ClientCore.MonoGame/Content/Textures/ships`.
   It iterates its full internal `SHIP_ASSETS` list (all 72 across
   every faction) and prints `MISS <asset>: source not found` for
   every asset that isn't GT — that's expected and fine, **only check
   that all 16 `gt-*` lines say `OK`**; the script exits 1 because of
   the (expected, harmless) MISS lines for other factions, not because
   anything GT-related failed — don't treat that nonzero exit as this
   task's failure on its own, verify by reading the printed per-asset
   status.
3. **Replace the 16 existing files** at
   `src/Allegiance.ClientCore.MonoGame/Content/Models/gt-<class>.fbx`
   and `Content/Textures/ships/gt-<class>.png` with the newly
   converted ones.
4. **Rebuild the MonoGame content pipeline** (`dotnet build` on
   `Allegiance.ClientCore.MonoGame`, which invokes MGCB) and confirm
   the new `.fbx`/`.png` pairs actually compile into `.xnb` — a bad
   FBX export will surface here as a real build error, not a silent
   runtime fallback.
5. **Confirm scale still resolves sanely** — `ShipRenderScale`
   (P333/P336) auto-normalizes every ship's rendered bounding-sphere
   radius to `0.5 × HullType.Length` at runtime purely from the
   compiled model's own geometry, so no manual per-model scale tuning
   should be needed — confirm this live (see Verification), don't just
   assume it.

## Explicitly out of scope this task

- **Any other faction's ships**, including Iron Coalition — IC's 7
  regenerated GLBs are sitting in the same `gt_ic_missing_glbs/`
  directory but are a separate task (P353), queued behind this one.
  Do not touch `ic-*` assets here.
- **Re-running or second-guessing P350's civ-hull mapping fix** — that
  landed and is correct; this task only replaces the visual asset
  content behind names P350/this session already pointed correctly.
- **Any change to `HullModelResolver`, `RosterPlanner`, or
  `Content.mgcb`'s structure** — the asset names and their wiring
  already exist; this is a pure file-content swap.
- Do not touch native reference source (`~/Development/Allegiance`) —
  read-only.

## Verification — real proof, not just build/test

- **Live proof, required**: launch a real `vizfight roster gt` session
  (civ-filtered to GT, 16 bots) and a real MonoGame client, and
  visually confirm **all 16** ships render as the new, distinct,
  correctly-textured Meshy models — not the old placeholder geometry,
  not a fallback gray/untextured shape, not one ship duplicated across
  slots. Compare against the Meshy thumbnails already sitting next to
  each GLB in `gt_regen_glbs_thumbs/` or `gt_ic_missing_glbs/*_thumb.png`
  to confirm the in-game render actually matches what was approved.
  Also fly close to at least the carrier (`cap502`/`gt-carrier`, the
  largest) and the lifepod (`wc_gtpod`/`gt-pod`, the smallest) to
  confirm scale reads sensibly relative to each other and to the
  player's own ship.
- Whole-solution build: 0 warnings, 0 errors.
- Full test suite (`/home/domingo/.dotnet/dotnet test`), run at least
  2x: only the pre-existing documented live-wire timing flakes are
  expected (failing test names vary run to run — that's the known
  signature).

## Deliverables

1. The 16 replaced `.fbx`/`.png` pairs, committed on `master` (not
   pushed — Domingo pushes). Do not create a side branch. These are
   binary content files — commit them normally.
2. A short final summary: confirmation all 16 converted cleanly (any
   that needed the decimation cap, any FBX export warnings), the live
   in-game visual proof performed (describe what you saw per ship or
   at least call out anything that looked wrong), build/test status.

# P353 — Wire the 16 Meshy-regenerated Iron Coalition (IC) ship models into the MonoGame port

## Context

`AllegianceCore` is a C# port of native FreeAllegiance
(`~/Development/Allegiance`, read-only reference, never modify it).

Direct continuation of this session's arc: P351 did this wiring for
Bios's 13 ships, P352 for GT's 16. This task is the Iron Coalition (IC)
equivalent — IC's full 16-class P287 roster
(`RosterPlanner.FactionOrder`'s `ic` entry) has now been regenerated
via Meshy's multi-image-to-3D, each one fed 4 real reference images
rendered from the ACTUAL original native `.mdl` geometry+texture (via
`~/Development/allegiance-tools/reference/mdl_parse.py` →
`decode_texture.py` → `textured_render.py`), replacing the old, weaker
Meshy models. Domingo's own words motivating this arc: "the meshes are
not great, we need to continue with the new hull pass on meshy."

**The 16 new GLBs are already downloaded and waiting, split across two
directories** (both real Meshy multi-image-to-3D output,
`should_texture: true`, `meshy-7`, 2K texture, real PBR):

- `~/Development/allegiance-tools/reference/gt_regen_glbs/` — 9 of IC's
  16 GLBs, all reused from other factions' already-regenerated hulls
  (this directory also holds Bios/GT GLBs unrelated to IC — only use
  the ones named below): `cap502.glb`, `utl14a.glb`, `utl18a.glb`,
  `cap01.glb`, `cap100.glb`, `utl17a.glb`, `utl100.glb`, `utl91.glb`,
  `utl93b.glb`. These are IC's own real dedicated hulls that happen to
  reuse the same underlying native model as another faction's ship
  (e.g. Bios's constructor/miner/ejectpod, GT's carrier/cruiser/dev) —
  not a mistake, several civs share native geometry by the original
  game's own design.
- `~/Development/allegiance-tools/reference/gt_ic_missing_glbs/` — the
  remaining 7 IC-specific GLBs, submitted in the post-reboot session:
  `wc_icbmb.glb`, `wc_icfig.glb`, `wc_ichvy.glb`, `wc_icint.glb`,
  `wc_icsf.glb`, `cap02a.glb`, `fig05ops1.glb`. This directory also
  holds the 4 GT-specific GLBs P352 already wired — don't touch
  `gt-*` assets here, P352 owns those.

All 16 verified clean this session by direct visual inspection of
their Meshy thumbnails (single coherent mesh, no fragmentation,
correct proportions) — the 7 IC-specific ones directly, the 9 reused
ones already verified when they were wired for their other-faction use.

## The exact mapping (source GLB → P287 asset name to replace)

Pulled directly from a live `RosterPlanner.Plan` run
(`vizfight roster 0 ic`), not from memory — trust this table:

| Source GLB | Directory | P287 asset (existing `Content/Models/<asset>.fbx` + `Content/Textures/ships/<asset>.png` to REPLACE) | Native display name |
|---|---|---|---|
| `wc_icbmb.glb` | `gt_ic_missing_glbs/` | `ic-bomber` | Bomber |
| `cap502.glb` | `gt_regen_glbs/` | `ic-carrier` | Attack Carrier [SR] |
| `utl14a.glb` | `gt_regen_glbs/` | `ic-constructor` | Lg Adv Constructor |
| `utl18a.glb` | `gt_regen_glbs/` | `ic-corvette` | Corvette |
| `cap01.glb` | `gt_regen_glbs/` | `ic-cruiser` | Lt Cruiser |
| `cap100.glb` | `gt_regen_glbs/` | `ic-dev` | Devastator |
| `utl17a.glb` | `gt_regen_glbs/` | `ic-devastator` | Devastator |
| `utl100.glb` | `gt_regen_glbs/` | `ic-ejectpod` | Lifepod |
| `wc_icfig.glb` | `gt_ic_missing_glbs/` | `ic-fighter` | Enh Fighter |
| `cap02a.glb` | `gt_ic_missing_glbs/` | `ic-frigate` | Frigate |
| `wc_icint.glb` | `gt_ic_missing_glbs/` | `ic-int` | Interceptor |
| `wc_ichvy.glb` | `gt_ic_missing_glbs/` | `ic-interceptor` | Hvy Interceptor |
| `utl91.glb` | `gt_regen_glbs/` | `ic-miner` | Adv Miner |
| `fig05ops1.glb` | `gt_ic_missing_glbs/` | `ic-scout` | Lxy Scout |
| `wc_icsf.glb` | `gt_ic_missing_glbs/` | `ic-stealthfighter` | Stealth Bomber [NG] |
| `utl93b.glb` | `gt_regen_glbs/` | `ic-trooptransport` | Hvy Troop Transport |

Note `ic-dev` (Devastator, `cap100`) and `ic-devastator` (Devastator,
`utl17a`) are two DIFFERENT existing P287 asset slots that both happen
to have the native display name "Devastator" — they are not
duplicates, don't collapse them; wire both from their own distinct
source GLB exactly as the table shows.

All 16 of IC's real P287 roster classes — the full lineup, not a
partial set. All 16 existing `Content/Models/ic-*.fbx` +
`Content/Textures/ships/ic-*.png` pairs already confirmed present on
disk (already wired into `Content.mgcb`/`HullModelResolver.ShipAssets`
— no new asset-registration code needed).

## Scope

1. **Convert each GLB to FBX** using
   `~/Development/allegiance-tools/reference/glb_to_fbx.py` (headless
   Blender: `blender -b --python glb_to_fbx.py -- input.glb output.fbx`).
   Strips materials to geometry+UVs+normals only, triangulates,
   decimates to a 20k-tri cap, exports FBX binary. This is the same
   script P352 used (a from-scratch reconstruction of the original
   `/tmp/p287stage/glb_to_fbx.py`, lost to a tmpfs wipe on reboot, but
   test-verified end-to-end before first use). **The output filename
   must be the P287 ASSET name, not the source GLB's own name** (e.g.
   `wc_icbmb.glb` → `ic-bomber.fbx`) — rename/stage the GLBs under
   their target asset names before conversion, or adapt invocation
   accordingly; either is fine, just get the final filenames right.
   **Careful with the 9 reused GLBs** — the same source GLB (e.g.
   `cap502.glb`) must be converted twice under two different output
   names if another faction also uses it (`gt-carrier.fbx` from P352
   AND `ic-carrier.fbx` here) — don't skip a conversion just because a
   same-named output already exists from a prior task.
2. **Extract each model's real texture** using the exact established
   script, `extract_meshy_textures.py` (repo root) — it reads
   `baseColorTexture` directly from the source `.glb` and writes
   `Content/Textures/ships/<asset>.png`. **Gotcha**: it looks up
   sources as `<meshy_dir>/<asset>.glb` (asset name, e.g.
   `ic-bomber.glb`) — it does NOT know about native `ModelName`s like
   `wc_icbmb.glb`. Stage all 16 IC GLBs (from both `gt_regen_glbs/` and
   `gt_ic_missing_glbs/`, per the table above) into one scratch
   directory, copied/symlinked and renamed to their target asset names
   (e.g. `wc_icbmb.glb` → `<staging>/ic-bomber.glb`), then run
   `python3 extract_meshy_textures.py <staging_dir> src/Allegiance.ClientCore.MonoGame/Content/Textures/ships`.
   It iterates its full internal `SHIP_ASSETS` list (all 72 across
   every faction) and prints `MISS <asset>: source not found` for
   every asset that isn't IC — that's expected and fine, **only check
   that all 16 `ic-*` lines say `OK`**; the script exits 1 because of
   the (expected, harmless) MISS lines for other factions, not because
   anything IC-related failed — don't treat that nonzero exit as this
   task's failure on its own, verify by reading the printed per-asset
   status.
3. **Replace the 16 existing files** at
   `src/Allegiance.ClientCore.MonoGame/Content/Models/ic-<class>.fbx`
   and `Content/Textures/ships/ic-<class>.png` with the newly
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

- **Any other faction's ships**, including GT — GT's 16 assets were
  already wired by P352 (should have landed on `master` before this
  task starts; if for any reason it hasn't, do NOT also do GT's work
  here, that's a separate commit/task). Do not touch `gt-*` assets.
- **Re-running or second-guessing P350's civ-hull mapping fix** — that
  landed and is correct; this task only replaces the visual asset
  content behind names P350/this session already pointed correctly.
- **Any change to `HullModelResolver`, `RosterPlanner`, or
  `Content.mgcb`'s structure** — the asset names and their wiring
  already exist; this is a pure file-content swap.
- Do not touch native reference source (`~/Development/Allegiance`) —
  read-only.

## Verification — real proof, not just build/test

- **Live proof, required**: launch a real `vizfight roster ic` session
  (civ-filtered to IC, 16 bots) and a real MonoGame client, and
  visually confirm **all 16** ships render as the new, distinct,
  correctly-textured Meshy models — not the old placeholder geometry,
  not a fallback gray/untextured shape, not one ship duplicated across
  slots. Pay particular attention to `ic-dev` vs `ic-devastator` (two
  different real ships, easy to accidentally conflate) actually
  rendering as two visually distinct hulls. Compare against the Meshy
  thumbnails already sitting next to each GLB in `gt_regen_glbs_thumbs/`
  or `gt_ic_missing_glbs/*_thumb.png` to confirm the in-game render
  actually matches what was approved. Also fly close to at least the
  carrier (`cap502`/`ic-carrier`, the largest) and the lifepod
  (`utl100`/`ic-ejectpod`, the smallest) to confirm scale reads
  sensibly relative to each other and to the player's own ship.
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
   in-game visual proof performed (describe what you saw per ship,
   explicitly confirm `ic-dev`/`ic-devastator` are visually distinct,
   or at least call out anything that looked wrong), build/test
   status.

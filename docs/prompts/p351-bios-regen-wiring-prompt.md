# P351 — Wire the 13 Meshy-regenerated Bios ship models into the MonoGame port

## Context

`AllegianceCore` is a C# port of native FreeAllegiance
(`~/Development/Allegiance`, read-only reference, never modify it).

Direct continuation of this session's arc: P350 fixed which native hull
each Bios P287 asset *conceptually* represents (e.g. `bios-bomber` now
correctly means `quizbbrv2`, not the cross-civ `bom05b` it used to
mean). Separately, and using the exact same real content pipeline this
repo already established (P287's `glb_to_fbx.py` +P337's
`extract_meshy_textures.py`), all 13 of Bios's real P287 roster classes
were freshly regenerated via Meshy's multi-image-to-3D, each one fed 4
real reference images rendered from the ACTUAL original native `.mdl`
geometry+texture (via `~/Development/allegiance-tools/reference/mdl_parse.py`
→ `decode_texture.py` → `textured_render.py`) instead of whatever
weaker/wrong reference the original P287 batch used. Domingo's own
words motivating this: "the meshes are not great, we need to continue
with the new hull pass on meshy" — this task is the "actually put the
new ones in the game" half.

**The 13 new GLBs are already downloaded and waiting** at
`~/Development/allegiance-tools/reference/bios_regen_p351/` — real
Meshy multi-image-to-3D output (`should_texture: true`, `meshy-7`,
2K texture, real PBR), verified clean (single coherent mesh, no
fragmentation, correct proportions) by direct visual inspection this
session for every one of them.

## The exact mapping (GLB filename → P287 asset name to replace)

| Source GLB | P287 asset (existing `Content/Models/<asset>.fbx` + `Content/Textures/ships/<asset>.png` to REPLACE) |
|---|---|
| `quizbbrv2.glb` | `bios-bomber` |
| `cap502.glb` | `bios-carrier` |
| `utl14a.glb` | `bios-constructor` |
| `utl18a.glb` | `bios-corvette` |
| `cap01.glb` | `bios-cruiser` |
| `cap20.glb` | `bios-destroyer` |
| `utl17a.glb` | `bios-devastator` |
| `utl100.glb` | `bios-ejectpod` |
| `quizfigv2.glb` | `bios-fighter` |
| `quizintv2.glb` | `bios-interceptor` |
| `utl91.glb` | `bios-miner` |
| `quizsfv2.glb` | `bios-stealthfighter` |
| `utl93b.glb` | `bios-trooptransport` |

All 13 of Bios's real P287 roster classes (`RosterPlanner.FactionOrder`'s
`bios` entry) — the full lineup, not a partial set.

**Not in scope / do not add**: `quizscoutv2.glb` is also present in that
same directory (a 14th GLB) — it's a bonus render of the real Bios
Scout, but "bios-scout" is not one of the 13 existing P287 roster asset
names (Bios has no scout-class roster slot; the scout only exists today
as the unrelated pre-P287 `bios_scout_upscaled` fallback asset). Adding
a brand-new asset class is a bigger, separate scope decision Domingo
hasn't asked for here — leave `quizscoutv2.glb` alone, don't wire it
into anything.

## Scope

1. **Convert each GLB to FBX** using the exact established script,
   `/tmp/p287stage/glb_to_fbx.py` (headless Blender, strips materials to
   geometry+UVs+normals only, triangulates, decimates to a 20k-tri cap,
   exports FBX binary matching this repo's existing import convention —
   read the script, it's short and exact). **The output filename must be
   the P287 ASSET name, not the source GLB's own name** (e.g. `quizbbrv2.glb`
   → `bios-bomber.fbx`, not `quizbbrv2.fbx`) — rename/stage the GLBs
   under their target asset names before conversion (the script names
   its output after the input file's stem), or adapt invocation
   accordingly; either is fine, just get the final filenames right.
2. **Extract each model's real texture** using the exact established
   script, `extract_meshy_textures.py` (repo root) — it reads
   `baseColorTexture` directly from the source `.glb` (no Blender
   needed for this half) and writes `Content/Textures/ships/<asset>.png`.
   Read its own doc comment for the exact invocation shape (it takes a
   directory of GLBs + an output dir + asset-name keying — adapt paths
   to point at `bios_regen_p351/`, keyed by the same 13 target asset
   names from the table above, not the source GLB filenames).
3. **Replace the 13 existing files** at
   `src/Allegiance.ClientCore.MonoGame/Content/Models/bios-<class>.fbx`
   and `Content/Textures/ships/bios-<class>.png` with the newly
   converted ones — these paths already exist and are already wired
   into `Content.mgcb`/`HullModelResolver.ShipAssets` (confirmed this
   session — no new asset-registration code needed, this is a pure
   content replacement).
4. **Rebuild the MonoGame content pipeline** (`dotnet build` on
   `Allegiance.ClientCore.MonoGame`, which invokes MGCB) and confirm
   the new `.fbx`/`.png` pairs actually compile into `.xnb` — a bad FBX
   export (e.g. degenerate geometry, a missing texture UV channel) will
   surface here as a real build error, not a silent runtime fallback.
5. **Confirm scale still resolves sanely** — this port's existing
   `ShipRenderScale` (P333/P336) auto-normalizes every ship's rendered
   bounding-sphere radius to `0.5 × HullType.Length` at runtime purely
   from the compiled model's own geometry, so no manual per-model scale
   tuning should be needed — but confirm this live (see Verification),
   don't just assume it because it worked for the old models.

## Explicitly out of scope this task

- **Any other faction's ships** — this task is Bios only, the batch
  that's actually been regenerated and downloaded. The other 8 factions
  are real, separate future work.
- **`quizscoutv2.glb`** — see above, don't wire it in.
- **Re-running or second-guessing P350's civ-hull mapping fix** — that
  landed and is correct; this task only replaces the visual asset
  content behind names P350 already pointed correctly.
- **Any change to `HullModelResolver`, `RosterPlanner`, or
  `Content.mgcb`'s structure** — the asset names and their wiring
  already exist; this is a pure file-content swap.
- Do not touch native reference source (`~/Development/Allegiance`) —
  read-only.

## Verification — real proof, not just build/test

- **Live proof, required**: launch a real `vizfight roster bios`
  session (civ-filtered to Bios, 13 bots) and a real MonoGame client,
  and visually confirm **all 13** ships render as the new, distinct,
  correctly-textured Meshy models — not the old placeholder geometry,
  not a fallback gray/untextured shape, not one ship duplicated across
  slots. Compare against this session's own reference thumbnails in
  `bios_regen_p351/` (or re-derive thumbnails from the GLBs if those
  aren't kept) to confirm the in-game render actually matches what was
  approved. Also fly close to at least the carrier (`cap502`, the
  largest) and the ejectpod (`utl100`, the smallest) to confirm scale
  reads sensibly relative to each other and to the player's own ship.
- Whole-solution build: 0 warnings, 0 errors.
- Full test suite (`/home/domingo/.dotnet/dotnet test`), run at least
  2x: only the pre-existing documented live-wire timing flakes are
  expected (failing test names vary run to run — that's the known
  signature).

## Deliverables

1. The 13 replaced `.fbx`/`.png` pairs, committed on `master` (not
   pushed — Domingo pushes). Do not create a side branch. Note: these
   are binary content files — commit them normally, don't try to diff
   them meaningfully.
2. A short final summary: confirmation all 13 converted cleanly (any
   that needed the decimation cap, any FBX export warnings), the live
   in-game visual proof performed (describe what you saw per ship or
   at least call out anything that looked wrong), build/test status.

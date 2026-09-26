# P356 — Fix 4 shipped ships that render completely invisible (flat black at every orientation)

Landing: `master` (not pushed — Domingo pushes). Companion fix in the shared
conversion script `allegiance-tools/reference/glb_to_fbx.py`.

## TL;DR

All **4** shipped flat-black hulls — `gt-fighter`, `gt-tt`,
`ic-trooptransport`, `bios-trooptransport` — had **no normal attribute** in
their compiled `Model` (GPU vertex stride **20**, not the healthy **32**), so
`BasicEffect`'s per-vertex diffuse lighting had nothing to compute against and
every hull rasterized as an unlit flat silhouette regardless of ambient or
lights. The root cause was **not** a degenerate source UV or a missing texture
(the prior guesses): it is that Blender's FBX export carried the Meshy GLB's
**custom split normals** straight through, and **MGCB's Assimp-based FBX importer
silently dropped that normal channel during `JoinIdenticalVertices` welding** for
these (heavily-decimated) meshes. Fixed in `glb_to_fbx.py` by clearing the stale
custom split normals before export so Blender writes plain per-vertex smooth
normals for the decimated geometry — these survive MGCB's weld step, and are
also *more correct* than the stale dense-mesh normals. All 4 hulls re-exported,
now compile to stride 32, and render visibly (verified visually + by JPEG size).

---

## 1. Confirmed root cause (hypothesis held for all 4 hulls)

Measured through the real content-loading path (a new `roster-capture stride`
mode that loads each compiled `Model` via `ContentManager` and reads every
`ModelMeshPart.VertexBuffer.VertexDeclaration`, mirroring the in-game
`ShipRenderer` pipeline). Compiled-vertex census, all 72 shipped hulls:

```
bios-trooptransport   stride=20   Position+TextureCoordinate      <-- BROKEN (P351)
dreg-fig2             stride=20   Position+TextureCoordinate      <-- not wired yet (out of scope)
gt-fighter            stride=20   Position+TextureCoordinate      <-- BROKEN (P352)
gt-tt                 stride=20   Position+TextureCoordinate      <-- BROKEN (P352)
ic-trooptransport     stride=20   Position+TextureCoordinate      <-- BROKEN (P353)
gt-carrier  (control) stride=32   Position+Normal+TextureCoordinate  <- healthy
ic-fighter  (control) stride=32   Position+Normal+TextureCoordinate  <- healthy
rix-tt      (control) stride=32   Position+Normal+TextureCoordinate  <- healthy (same class)
```

The task's vertex-stride/missing-normal hypothesis is **fully confirmed for all
4 shipped hulls**, and the same defect is waiting in the unwired `dreg-fig2`
(see §5).

### A bonus structural finding
`gt-tt`, `ic-trooptransport`, and `bios-trooptransport` are **the same source
model** (the universal Troop-Transport hull): their source GLBs are
byte-identical (`md5 022763f4…` for `utl93b.glb` == `p353stage/ic-trooptransport.glb`),
and their compiled XNBs were byte-identical before this fix. So one shared hull
had been deployed to all three civ slots and each copy lost normals. The fix
re-exports that single shared hull for each civ.

### Why normals disappear — empirical isolation (this session)
Not guesses — measured:

1. **Source GLBs are clean.** Importing `wc_gtfig.glb`, `utl93b.glb`,
   `cap502.glb` in Blender: all report `has_custom_normals=True` with full
   per-corner normal data. Not a source-content defect.
2. **The FBX files DO contain normals** (Blender re-imports `wc_gtfig.fbx` and
   reads back 60000 corner normals) with an *identical* `LayerElementNormal`
   schema to the healthy `cap502` (`MappingInformationType=ByPolygonVertex`,
   `ReferenceInformationType=IndexToDirect`, same compressed-int encoding).
   So it is not a missing normal layer in the file.
3. **Raw Assimp sees the normals in every file.** Directly importing
   `wc_gtfig.fbx` / `utl93b.fbx` with libassimp (the exact bundled 7.5.0 that
   MGCB uses) → `HasNormals=True` for broken AND healthy alike.
4. **MGCB's post-processing is the dropping point.** Running the exact import
   flag set MGCB uses (`FindDegenerates | FindInvalidData | FlipUVs |
   FlipWindingOrder | JoinIdenticalVertices | ImproveCacheLocality |
   OptimizeMeshes | Triangulate`, via `AssimpContext.ImportFile` with
   `RemoveDegeneratePrimitivesConfig(true)`) reproduces the defect precisely:
   `cap502` → `HasNormals=True`; `wc_gtfig`/`utl93b` → `HasNormals=False`.
   The **`JoinIdenticalVertices` vertex-welding step drops the per-corner
   custom-split normals** for these meshes while keeping them for the control.
5. **Decimation aggressiveness is NOT the trigger.** `cap502` keeps its
   normals even when decimated down to ~96 tris; the broken models lose theirs.
   It is a property of how Blender serializes *these* meshes' custom split
   normals (multiple distinct normals per position) that Assimp's welder can't
   round-trip. The brief's "unusually heavy decimation" correlation is
   incidental — the real signal is the custom-split-normal encoding.
6. **Clearing custom split normals before export fixes all 4 and only the 4.**
   Re-running `glb_to_fbx.py` with an added
   `bpy.ops.mesh.customdata_custom_splitnormals_clear()` step → all 4 fixed
   hulls import through MGCB with `Normal0` (stride 32); the healthy control
   re-exported identically keeps stride 32 unchanged.

**Conclusion.** The normal channel is present and valid at every stage of the
source asset; the fault is Blender's FBX exporter carrying per-corner *custom
split normals* that MGCB's Assimp `JoinIdenticalVertices` silently discards for
these meshes. The prior P355 guess of "regenerate in Meshy" was correct to be
suspicious of — the source GLBs are fine and no Meshy credit is needed.

## 2. The fix applied and why

**`allegiance-tools/reference/glb_to_fbx.py`** (the shared Meshy→FBX conversion
script used by every P351/P352/P353 regen batch): after triangulate + decimate,
clear the custom split normals so Blender recomputes plain per-vertex smooth
normals for the *decimated* geometry:

```python
# P356 root-cause fix: ... (see file for the full comment)
for obj in meshes:
    bpy.context.view_layer.objects.active = obj
    bpy.ops.object.select_all(action='DESELECT')
    obj.select_set(True)
    bpy.ops.mesh.customdata_custom_splitnormals_clear()
```

**Why this is the right fix, not a workaround:**

- It makes MGCB reliably keep the normal channel: plain per-vertex smooth
  normals (one normal per position) round-trip through Assimp's
  `JoinIdenticalVertices` without loss, for every model.
- It is *more correct* for decimated content: the custom normals Blender was
  carrying came from the ORIGINAL dense triangles (690k→20k, a 97% reduction);
  after reshaping the mesh those normal vectors no longer match any triangle.
  Recomputed smooth normals match the actual decimated surface.
- It is in the shared conversion script, so future regenerations (GigaCorp /
  OH / TF / Dreg) get the fix automatically — including the already-broken
  `dreg-fig2` the moment Dreg's regen arc resumes (§5).
- No MGCB content-pipeline setting change was needed; the importer's
  post-process set is standard and correct.

**Applied to:** re-exported the 4 shipped broken FBX from their source GLBs
(`gt-fighter`←`wc_gtfig.glb`; `gt-tt`/`ic-trooptransport`/
`bios-trooptransport`←`utl93b.glb`, the shared universal hull). Textures were
already fine and are unchanged (per scope). Also added a permanent
`roster-capture stride` diagnostic mode (part of this landing) so the compiled
vertex-declaration census is repeatable in future regens.

## 3. Evidence — before / after for each of the 4 hulls

Rendered through the real content pipeline (`roster-capture dump`, same
`HullModelResolver` + `ShipRenderer.Draw` + compiled content the game uses) and
compared against healthy faction controls.

Compiled vertex stride (via `roster-capture stride`):

| hull | before | after | healthy control |
|---|---|---|---|
| `gt-fighter` | 20 (no normal) | **32 (normal)** | gt-carrier 32 |
| `gt-tt` | 20 | **32** | rix-tt 32 |
| `ic-trooptransport` | 20 | **32** | ic-fighter 32 |
| `bios-trooptransport` | 20 | **32** | bios-carrier 32 |

Single-hull reference-render JPEG byte size (dark-livery faction sheet; P355
found the broken hulls at ~14KB vs 19–33KB healthy — a fixed hull must land in
the normal range):

| hull | before (P355) | after |
|---|---|---|
| `gt-fighter` | ~14KB (flat black) | **29411 B** |
| `gt-tt` | ~14KB | **31290 B** |
| `ic-trooptransport` | ~14KB | **31290 B** |
| `bios-trooptransport` | ~14KB | **31290 B** |

Healthy controls in the same census (for reference): `gt-carrier` 23103 B,
`ic-fighter` 30699 B, `rix-fighter` 19579 B, `bios-carrier` 23099 B.

**Direct visual check:** rendered each fixed hull through the real pipeline and
visually inspected (vision-verified). Every one is a clearly visible, lit, 3-D
metallic ship — highlights, shading, engine glows — fully distinguishable from
the backdrop. **None is a flat black silhouette.** (Example renders saved under
`/tmp/roster-capture-variety/{gt-fighter,gt-tt,ic-trooptransport,
bios-trooptransport}.jpg`; reproducible via the command in §4.)

### Build / tests
- Whole-solution build (`AllegianceCore.slnx`): **0 warnings, 0 errors**.
- Full test suite run **twice** (`/home/domingo/.dotnet/dotnet test`): both runs
  green in the renderer-critical suites (**Allegiance.ClientCore.MonoGame.Tests
  652/652 passed** both times; Lobby 12/12). The only failures were the
  pre-existing **live-wire timing flakes** in `Allegiance.Core.Tests` (6 per
  run, a *different* 6 each run — the classic nondeterministic-timing
  signature, uncoupled from the MonoGame renderer/content; plus one occasional
  `Allegiance.ClientCore.Tests` seeded-session timing flake). Nothing failed
  consistently that relates to this change.

## 4. Reproduction / verification commands

```bash
# compiled vertex-declaration census (new P356 diagnostic mode)
tools/roster-capture/bin/Debug/net10.0/roster-capture stride

# reference-sheet render through the real pipeline (JPEG sizes + visuals)
tools/roster-capture/bin/Debug/net10.0/roster-capture dump
# per-asset singles land in /tmp/roster-capture-variety/

# (share the source→FBX conversion fix) re-run any regen batch with the updated
# allegiance-tools/reference/glb_to_fbx.py
```

## 5. Recurrence guard — this will NOT silently recur

Because the fix is in the **shared conversion script** (`glb_to_fbx.py`),
*every* future Meshy regeneration automatically gets clean, weld-safe normals —
no per-asset action needed:

- **Dreg (pending):** `dreg-fig2` is confirmed to carry the same defect today
  (stride 20). When Dreg's regen arc resumes, re-running the fixed
  `glb_to_fbx.py` on its source GLB resolves it in the normal batch — no
  separate wiring step required by this task.
- **GigaCorp / OH / TF (pending regens):** the fixed script is what their
  regen batches use, so they cannot reintroduce the flat-black normals-loss
  defect.
- The `roster-capture stride` census now makes the compiled-normal check
  cheap and explicit for any future batch: a hull compiling to stride 20 is an
  instant red flag before it ever ships.

## Out of scope (left untouched, per task)
- `dreg-fig2` — not generated/wired/shipped; resolved automatically when Dreg's
  regen resumes via the shared-script fix.
- No new Meshy API spend (source GLBs confirmed clean).
- P355's camera headlight / any lighting code — unrelated.
- Station/Probe/Building/missile renderers and `Sector.cs` collision — untouched.

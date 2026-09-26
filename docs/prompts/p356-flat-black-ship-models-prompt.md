# P356 — Fix 4 shipped ships that render completely invisible (flat black at every orientation)

## Why this task exists

P355 (landed `2ae913c2`) investigated a live-playtest report of a
"black ship" and, after empirically falsifying its own lighting
hypothesis, found something more serious: 5 hulls render **completely
flat black at every orientation, regardless of lighting** —
`gt-fighter`, `gt-tt`, `dreg-fig2`, `ic-trooptransport`,
`bios-trooptransport`. **4 of these are already-shipped, committed
content**: `bios-trooptransport` (P351), `gt-fighter`+`gt-tt` (P352),
`ic-trooptransport` (P353) — meaning 4 real ships are currently
invisible in actual gameplay right now. (`dreg-fig2` hasn't been
generated/wired yet — not this task's problem, but watch for it
recurring when Dreg's regen arc resumes, see Verification.)

P355's own report guessed the cause was a "degenerate UV / model
content defect" and recommended a full Meshy re-generation. **This
session (2026-09-23, immediately following P355) went further and
found that guess does not hold up** — do not start this task by
re-submitting these models to Meshy, you would likely be spending real
credits to fix nothing. Here is what was actually verified, and the
real leading suspect:

## What's already ruled out (verified this session, don't re-check these)

Using `gt-fighter` (`wc_gtfig`) as the primary case, cross-checked
against the healthy `gt-carrier` as a control:

1. **Source Meshy GLB UVs are clean.** Imported
   `~/Development/allegiance-tools/reference/gt_regen_glbs/wc_gtfig.glb`
   directly in Blender: UV x/y range `[0,1]`, 0% degenerate/zero-UV
   coordinates, across all ~690k source triangles.
2. **The converted FBX's UVs are also clean** — both a fresh
   re-conversion via `glb_to_fbx.py` AND the actual committed
   `Content/Models/gt-fighter.fbx` (md5-verified as the real shipped
   file): 0% zero-UV coordinates, full `[0,1]` range, in both.
3. **The texture PNG is not blank.** `Content/Textures/ships/gt-fighter.png`:
   real RGB content, avg brightness 46/255 (dark but real, matches the
   documented GT charcoal livery), max pixel value 255, alpha channel
   fully opaque (255) everywhere — same as the healthy `gt-carrier.png`.
4. **`BasicEffect` runtime state is identical between the broken and
   healthy hull.** Instrumented `ShipRenderer.Draw`'s effect right
   before drawing both `gt-fighter` and `gt-carrier`:
   `DiffuseColor={0.8,0.8,0.8}`, `Alpha=1`, `TextureEnabled=True`,
   `LightingEnabled=True` — byte-for-byte identical on both. This
   rules out any shared/stale-Effect-state bleed between draw calls.
5. **The compiled texture format is identical** — both `gt-fighter`
   and `gt-carrier` compile to `SurfaceFormat.Dxt1`, `1024x1024`,
   `levelCount=11`. Not a texture-compression difference.

## The real lead — MGCB's FBX importer appears to drop normals for these specific models

Reading each compiled `Model`'s actual GPU vertex buffer
(`ModelMeshPart.VertexBuffer.VertexDeclaration.VertexStride`):

- `gt-carrier` (healthy): **32-byte** vertex stride — consistent with
  Position (12B) + Normal (12B) + TexCoord (8B), a normal
  `VertexPositionNormalTexture`-shaped vertex.
- `gt-fighter` (flat-black): **20-byte** vertex stride — consistent
  with Position (12B) + TexCoord (8B) **and no normal data at all**.

Yet reading the SOURCE FBX for both directly in Blender's own Python
API shows **both report valid custom normals** (`has_custom_normals =
True`, `len(mesh.corner_normals) = 60000` for both, identical
triangle/loop counts). So the normal data is genuinely present in the
FBX file as far as Blender can tell, but MGCB's own (separate, much
stricter) FBX importer/processor appears to silently drop it for
`gt-fighter` while keeping it for `gt-carrier` — different FBX
content, same nominal export pipeline (`glb_to_fbx.py`), same MGCB
version.

**Why this would produce exactly the observed symptom**: with no
normal attribute reaching the vertex shader, `BasicEffect`'s per-vertex
diffuse lighting calculation (`dot(normal, lightDirection)`) has
nothing valid to compute against — every light contribution
(including P355's new camera headlight) collapses to zero or
undefined, leaving the model to render as an unlit flat silhouette
regardless of ambient, texture, or how many lights are added. This is
consistent with P355's own forensic finding that cranking ambient to
×20 still produced zero visible pixels — ambient alone (not
normal-dependent) should still show SOMETHING at ×20 if lighting were
the only factor, which is itself a clue this isn't a pure lighting-math
problem.

**This has not been independently confirmed for the other 3 affected
hulls yet (`gt-tt`, `ic-trooptransport`, `bios-trooptransport`)** —
only `gt-fighter` was measured directly. Confirming it holds for all 4
(or finding one doesn't match, which would be equally important to
know) is the first real step of this task.

## Scope

1. **Confirm the vertex-stride/missing-normal hypothesis for all 4
   shipped broken hulls**, not just `gt-fighter`. Reproduce the
   measurement method: load each compiled `Model` (e.g. via
   `tools/roster-capture`, same real content-loading path the game
   uses) and read `ModelMeshPart.VertexBuffer.VertexDeclaration.VertexStride`
   for each, compared against a few healthy control hulls (`gt-carrier`,
   `ic-fighter`, etc.). If a hull's stride is missing the normal
   component, that's the same defect; if any hull doesn't match this
   pattern, say so plainly and investigate that one separately rather
   than forcing the same fix onto it.
2. **Find why MGCB's FBX importer drops normals for these specific
   models but not others**, given both report valid normals when read
   independently in Blender. Plausible angles to check (verify, don't
   guess): whether these 4 models' FBX files have an unusual normal
   *encoding* Blender tolerates but MGCB's importer doesn't (e.g.
   per-vertex vs per-loop/split normals, a specific smoothing-group
   representation, degenerate/duplicate vertices from the aggressive
   `glb_to_fbx.py` decimation step — these 4 needed unusually heavy
   decimation ratios, e.g. `wc_gtfig` went 690,508→20,000 tris, a 97%
   reduction, among the most aggressive of the whole P352/P353 batch);
   or an MGCB content-pipeline processor setting that's silently
   stripping normals under some condition. Use MGCB's own build
   verbosity/logs if they surface anything relevant.
3. **Fix it** so all 4 hulls' compiled models carry real normal data
   and render normally (matching a healthy hull's vertex stride and,
   more importantly, actually being visible on screen). The fix is
   most likely one of: (a) a `glb_to_fbx.py` export-setting change that
   makes the FBX's normal encoding robust for MGCB's importer
   (re-export just these 4-5 affected source GLBs, no new Meshy spend
   needed — the source GLBs and their UVs/textures are already
   confirmed fine), or (b) an MGCB content-processor setting fix if the
   problem turns out to be on the import/processing side rather than
   the FBX export side. Prefer whichever actually fixes the root cause
   over a narrow workaround.
4. **Replace the 4 shipped broken files** in
   `Content/Models/{gt-fighter,gt-tt,ic-trooptransport,bios-trooptransport}.fbx`
   (textures are already fine, don't need replacing unless your fix
   somehow requires it) with corrected versions once the fix is
   verified.

## Explicitly out of scope

- **`dreg-fig2`** — not yet generated/wired, not shipped, not this
  task. If your root-cause fix is a `glb_to_fbx.py` change, it will
  automatically apply to Dreg's regen whenever that resumes — no
  separate action needed here, just don't go generate/wire Dreg now.
- **Any new Meshy API spend** — the defect is confirmed downstream of
  Meshy's output (source GLBs are clean); this is a conversion/import
  fix, not a regeneration. If your investigation genuinely finds the
  source GLB itself is the problem for one of the other 3 hulls
  (contradicting what was found for `gt-fighter`), stop and report that
  rather than spending credits — flag it for Domingo to decide.
- **P355's camera headlight or any other lighting code** — already
  landed, working as intended, unrelated to this defect (confirmed:
  cranking ambient ×20 didn't fix `gt-fighter`, so lighting-side
  changes categorically can't fix this).
- **The other renderers using the same ambient constant**
  (`StationRenderer`/`ProbeRenderer`/`BuildingEffectRenderer`/missile
  renderer) — unrelated, don't touch.
- **P354's collision physics** — unrelated, don't touch `Sector.cs`.

## Verification — real proof, not just build/test

- **Live proof, required**: for each of the 4 fixed hulls, render it
  through the real content pipeline (e.g. `roster-capture dump`, the
  same tool used to discover this) and confirm it's now visibly
  distinct from the black backdrop — compare JPEG byte size against
  the other ~15 hulls in its faction sheet (the broken ones were
  conspicuously smaller, ~14KB vs 19-33KB for healthy hulls in the
  same GT sheet — a fixed hull should land in the normal range) AND do
  a direct visual check of the rendered image.
- Confirm the compiled vertex stride now matches a healthy control
  hull (e.g. `gt-carrier`'s 32 bytes) for all 4 fixed models.
- Whole-solution build: 0 warnings, 0 errors.
- Full test suite (`/home/domingo/.dotnet/dotnet test`), run at least
  2x: only the pre-existing documented live-wire timing flakes are
  expected.

## Deliverables

1. The 4 replaced `.fbx` files (`gt-fighter`, `gt-tt`,
   `ic-trooptransport`, `bios-trooptransport`), plus any
   `glb_to_fbx.py`/MGCB fix that addresses the actual root cause,
   committed on `master` (not pushed — Domingo pushes). Do not create
   a side branch.
2. Report doc `P356_FLAT_BLACK_HULLS_FIX.md` in the repo root: what the
   confirmed root cause turned out to be for all 4 hulls (confirm or
   correct the vertex-stride/missing-normal hypothesis above), the
   exact fix applied and why, before/after evidence (JPEG byte sizes +
   visual renders) for each of the 4, and explicit confirmation this
   won't silently recur for future regenerations (GigaCorp/OH/TF/Dreg
   are still pending) if the fix is in the shared conversion script.

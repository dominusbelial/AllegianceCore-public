# P421 — use the real original per-resource-type rock textures instead of a procedural one

## Why this task exists

Domingo, watching old Allegiance footage: *"we need to find the original
asteroid textures, they actually look better than what we have? maybe
we can use ai to enhance them?"* Investigated — the real originals
already exist locally, unused.

## Confirmed real gap

This port's asteroid rendering (`src/Allegiance.ClientCore.MonoGame/
Rendering/RockTextureFactory.cs`) **procedurally generates** a single
256×256 synthetic diffuse texture per resource-type classification
(fixed seed `517613`) — it never loads a real texture asset at all.

Meanwhile the real, original per-resource-type rock bitmaps are sitting
unused in the already-configured local artwork corpus
(`~/Development/Artwork/`, the same one this port's `ArtPath` config
already points at):
```
drgrockbmp.mdl   tacrockbmp.mdl    goldrockbmp.mdl   carbonrockbmp.mdl
copperrockbmp.mdl heliumrockbmp.mdl hotrockbmp.mdl   waterrockbmp.mdl
```
And the port **already loads the matching real mesh geometry** for at
least two of these (confirmed live in this session's own MonoGame boot
logs: `"asteroid mesh 'Models/drgrock': 9974 verts, 11948 tris"`,
`'Models/tacrock'`) — via `drgrock.cvh`/`tacrock.cvh`, real original
mesh files, paired with `drgrock.mdl`/`tacrock.mdl` (likely
material/model definitions). So the real geometry is already faithfully
ported; only the matching real texture is missing, replaced by a
procedural stand-in.

## A proven, low-risk precedent already in this codebase

This port already has a real, working `*bmp.mdl` → `Texture2D` decode
pipeline, used for **other** real original bitmap assets — see
`Rendering/GaugeTextures.cs` (its own doc comment: *"each maps to a real
original-game `*Gaugebmp.mdl`"*), plus `ChaffModelSource.cs`,
`MineModelSource.cs`, `ExplosionAssets.cs`, `HudRenderer.cs` all doing
the same `.mdl`-bitmap decode for their own real assets. **Reuse this
exact pattern** for the rock bitmaps — don't build a second decoder.

## Scope

1. Trace `GaugeTextures.cs`'s (or whichever sibling is cleanest) real
   `.mdl`-bitmap decode path precisely, and confirm the same decoder
   works unmodified against a `*rockbmp.mdl` file (same container
   format expected, but verify — don't assume).
2. Wire each of the 8 real rock textures above into asteroid rendering,
   replacing `RockTextureFactory`'s procedural output for the matching
   resource-type classification (`RockTextureFactory.cs:36-41` already
   documents the exact resource-type→classification mapping this port
   uses — match real textures to the correct type, e.g. `goldrockbmp`
   for the gold-capability classification, not a guess).
3. Confirm mesh↔texture UV pairing is correct for `drgrock`/`tacrock`
   (the two meshes already confirmed loaded) — if other real mesh files
   exist for the remaining resource types (check the artwork corpus for
   any other `*rock.cvh`/`*rock.mdl` pairs beyond `drgrock`/`tacrock`/
   `rrocket`), wire those too; if a resource type has a real texture but
   this port has no matching real mesh, keep the existing procedural
   *mesh* (out of scope — this task is textures only) and just apply
   the real texture to whatever mesh is already used for that type.
4. Keep `RockTextureFactory`'s procedural generator as the documented
   fallback for any resource-type classification that has no real
   texture asset (don't delete it — native-faithful graceful
   degradation, matching this port's established convention elsewhere).

## Explicitly out of scope

- "Use AI to enhance them" — Domingo's own question, but not this task:
  land the real originals first, faithfully, before considering any
  upscale/enhancement pass. If the real textures turn out to be very
  low resolution once decoded, note that plainly in the report as a
  legitimate follow-up candidate — don't upscale anything here.
- Any other rendering pipeline change — ships, missiles, stations, etc.
  are untouched.
- P368-P420 — unrelated, don't touch.

## Verification — real proof, not just build/test

- A test proving each resource-type classification resolves to its
  correct real texture asset (not a mismatch, not silently falling back
  to procedural when a real one exists).
- **Live proof, required**: fly to a real asteroid field (this port's
  live test maps have plenty — Alderan-equivalent sectors, or the new
  generated PinWheel-topology sectors from this session's own testing)
  and visually confirm real rock textures render, distinct from the old
  procedural look, and distinct per resource type where multiple types
  are present in the same field.
- Whole-solution build: 0 warnings, 0 errors.
- Full test suite, run at least 2x: only the pre-existing documented
  live-wire timing flakes expected.

## Deliverables

1. The fix, committed on `master` (not pushed — Domingo pushes).
2. Report doc `P421_REAL_ASTEROID_TEXTURES.md` in the repo root: which
   real textures got wired to which resource types, what (if anything)
   still falls back to procedural and why, the real texture resolution
   found (relevant to the "should we AI-enhance" follow-up question),
   and the verification performed.

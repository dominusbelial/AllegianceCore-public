# MDL_PARSER_P233 — Port the `.mdl` binary model parser to C#

**Stage:** P233 · **Branch:** `master` (not pushed) · **Suite delta:** Allegiance.Core.Tests +7 (see below)

## Format recap and sources

Allegiance `.mdl` files are a binary object-graph format, fully cracked in an
earlier research session and written up in the Domingo LLM Wiki page
[`allegiance-mdl-model-format.md`](allegiance-mdl-model-format.md) (with native
citations: `engine/mdlbin.cpp`, `engine/value.h`, `engine/modeler.cpp`,
`effect/efapp.cpp`). A **proven, tested Python reference parser** that decoded a
real 46k-vertex ship end-to-end lives at
`~/Development/allegiance-tools/reference/mdl_parse.py` — read-only, never
modified. It is the authoritative spec this task ported from; this writeup
recaps it for reviewers.

### File header
Four DWORDs (`magic = 0x0DEBADF00`, `version = 0x00010000`) then four counts:
`countNamespaces, countImports, countExports, countDefinitions`. Followed by the
namespace name table, the import records (each a DWORD library index + a name),
the export name table, then `countExports + countDefinitions` object slots (each
a DWORD object index + a full object-graph).

### Strings
Padded C-strings with the exact formula `(strlen + 1 + 3) & ~3` bytes consumed
(the `+1` covers the NUL terminator; the whole rounded up to 4). This matters
for `FrameData` names and every header string.

### The generic object-graph reader (token-stack machine)
Objects are streams of DWORD tokens manipulated on a stack:

| # | Token | Meaning |
|---|-------|---------|
| 0 | `ObjectEnd` | pop the single stacked value and return it |
| 1 | `ObjectFloat` | push a LE float32 |
| 2 | `ObjectString` | push a padded C-string |
| 3 | `ObjectTrue` / 4 `ObjectFalse` | push a bool |
| 5 | `ObjectList` | push `count`, then recursively read `count` objects into a list |
| 6 | `ObjectApply` | pop function, call the generic `Apply` case |
| 7 | `ObjectBinary` | pop function, call the binary `Read` case |
| 8 | `ObjectReference` | push object `objects[idx]` (already-parsed) |
| 9 | `ObjectImport` | push an import marker carrying `import_names[idx]` |
| 10 | `ObjectPair` | pop `b`, pop `a`, push `(a, b)` |

The *function* values used by `Apply`/`Binary` are the import markers pushed by
token 9 — so a function's identity is its imported member name
(`GroupGeo`, `MeshGeo`, `TextureGeo`, …), with the library index ignored.

### `Apply` cases (generic)
- `GroupGeo` — unwrap the child list.
- `TextureGeo` — unwrap `(image, geo)`; keep `geo` (the image/texture decode is
  out of scope).
- `ModifiableNumber` — a wrapped float; no mesh content.
- `LODGeo` — pops `(geo, lodList)`; keep only `geo`, discard the LOD list (the
  lower-detail distance-swap variants). Per `engine/modeler.cpp`
  `LODGeoFactory::Apply`.
- `LightsGeo` — no mesh content.
- unknown — passed through minimally and ignored.

### `ObjectBinary` special cases
- `MeshGeo` (**the payload**): `DWORD countVertices, DWORD countIndices`, then
  `countVertices × 32-byte` records in **`x,y,z,u,v,nx,ny,nz`** order
  (position → texcoord → normal — **not** position → normal → texcoord; the
  single most error-prone detail), then `countIndices × WORD` triangle-list
  indices, raw LE.
- `LightsGeo`: `DWORD count`, then `48 × count` bytes skipped (cosmetic only,
  not needed for mesh geometry).
- `FrameData`: `DWORD count`, then per entry a padded C-string name + 3 raw
  `Vector`s (position, forward, up; 12 bytes each) — named hardpoint /
  attach-point metadata (weapon mounts, engine nozzles). Ported even though not
  strictly needed for a missile mesh; a natural tie-in to the queued smoke-trail
  task (engine-nozzle-positioned emission points).

### Mesh collection
Meshes are **not** collected eagerly during parsing. After the whole file is
read, the parser walks **only the resolved `"object"` export** (per
`mdlc.cpp`'s own `FindMemberLocal("object")` convention) and gathers every
`MeshGeo` reachable through `GroupGeo`/`TextureGeo` unwrapping. This is what
keeps `LODGeo`'s discarded low-detail variants from double-counting.

## What was ported

New files:
- `src/Allegiance.Core/Data/MdlFile.cs` — the parser plus the public result
  types `MdlFile` / `MdlMesh` / `MdlFrame`. Pure data layer, `namespace
  Allegiance.Core`, **no MonoGame types on the surface** (same discipline this
  port already uses for `FlightInput` / `RockMeshFactory` / `WarpMeshFactory`).
- `tests/Allegiance.Core.Tests/MdlParseTests.cs` — 7 tests, see below.

### Public surface
- `MdlFile.Parse(path)` / `MdlFile.Parse(byte[])` →
  `MdlFile` with `Namespaces`, `Imports`, `Exports`, `Meshes`
  (`IReadOnlyList<MdlMesh>`), `Frames` (`IReadOnlyList<MdlFrame>`).
- `MdlMesh`: flat `Positions` (3/vtx), `TexCoords` (2/vtx), `Normals` (3/vtx),
  `Indices` (`ushort[]`, **raw file order — no triangle-winding flip**; whether
  MonoGame/DirectX needs the flip is an empirical question for the renderer task,
  and is deliberately *not* guessed here), plus `VertexCount` / `TriangleCount`.
- `MdlFrame`: `Name`, `Position`, `Forward`, `Up`.

Multiple meshes per file are real and expected (a fighter can carry separate
turret/antenna/substructure chunks) — the parser returns all of them.

## Out of scope this task (unchanged from brief)
No MonoGame/`VertexBuffer`/`IndexBuffer`/rendering; no texture/`*bmp.mdl`
decoding; no triangle-winding correction; native reference tree
`~/Development/Allegiance` untouched; Python reference untouched.

## Cross-check: Python vs C# (must match exactly)

Verified by running the reference `mdl_parse.py` and the C# `MdlFile.Parse`
against the **same real Artwork files**. Beyond the mandated mesh/vertex/
triangle counts, I also emitted OBJ geometry (`v`/`vn`/`vt` lines, which are
winding-independent) from both implementations and diffed them — they match
**byte-for-byte**, which is the strongest possible agreement.

| File | Python meshes | Python verts/tris (per mesh) | Python frames | C# meshes | C# verts/tris | C# frames | Geometry diff |
|------|--------------|------------------------------|---------------|-----------|---------------|-----------|---------------|
| `mis02.mdl` | 1 | 81 / 126 | 3 (trail, thrust, rocket) | 1 | 81 / 126 | 3 | identical |
| `mis08.mdl` | 1 | 113 / 78 | 3 | 1 | 113 / 78 | 3 | identical |
| `p1_fig.mdl` | 3 | 5578/5018, 1993/1667, 205/222 | 5 | 3 | same | 5 | identical (7776 vertices) |

The `p1_fig.mdl` row is the important one: it exercises the multi-mesh walk
across a real fighter hull (a 46k-triangle-scale object), confirming that the
"collect from the `object` export only" walk yields the same meshes in the same
order as the Python oracle.

## Build / test status

- Whole solution (`src/Allegiance.Core`): **0 warnings, 0 errors**.
- New `MdlParseTests`: **7/7 pass** in isolation (mis02, mis08, multi-mesh
  p1_fig, header exposure, raw-index preservation, array sizing/finiteness,
  garbage/missing-file rejection).
- Full suite: ran twice vs the P232 baseline; only the documented
  pre-existing `LiveTurretGunnerBindingTests` timing flake (and the other
  documented machine-state flakes) may appear — see the counts below.

Suite baseline (Core.Tests) was 3050 at P232; this stage adds **+7**
(MdlParseTests) → **3057**.

## Commits
- `P233: port the .mdl binary model parser to C# (MdlFile)` — parser + tests +
  this doc, on `master` (not pushed; Domingo pushes).

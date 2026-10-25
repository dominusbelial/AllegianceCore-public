# P823 — glTF → native `.mdl` + `.cvh` writer (the inverse of P812's `tools/mdl-to-glb`)

**Task:** write the missing half of the new-art pipeline. Domingo's decision
(2026-10-25, on P812's recommendation §5): `.mdl`/`.cvh` stay the shipping
formats; glTF is the **authoring** format for NEW art (Mycelium race, Meshy HD
pack). P812 §6 listed the `glb → .mdl/.cvh` writer as the missing piece; this
task delivers it.

**Base:** master `3acb3e0a` (includes P812). Branch `pi/p823`, worktree
`allegiance-pi-p823` (public clone; nothing in `src/`/`tests/`/`tools/` carries
local paths, no media committed). Native reference re-grepped 2026-10-25 in
`~/Development/Allegiance/src/` (all `file:line` below re-verified).

**No runtime change:** `src/` is untouched. Everything lives in the P812 proof
tool (`tools/mdl-to-glb/`), one new render proof (`tools/p823-render-proof/`),
one new test file, docs. The game still ships native bytes.

---

## 1. What was built

| Piece | File | What |
|---|---|---|
| Binary `.mdl` writer | `tools/mdl-to-glb/MdlWriter.cs` | native token stream (`engine/mdlbin.cpp`): magic `0x0DEBADF00`/version `0x00010000`, namespace/import/export header, object table, postfix token objects |
| Text `.cvh` writer | `tools/mdl-to-glb/CvhWriter.cs` | two paths: sidecar round trip in native's printf formats; **generated** hulls for new art (ported `cvh/main.cpp` walk) + frames from hardpoint nodes |
| Convex hull | `tools/mdl-to-glb/ConvexHull.cs` | quickhull — the substitution for native's external `qhull` binary call |
| `HP_<Kind>_<Index>` → native names | `tools/mdl-to-glb/NativeNameMapper.cs` | the inverse of P812's `MountKindMapper` |
| Validation + conversion | `tools/mdl-to-glb/GlbImporter.cs` | rejects anything the port's own `MdlFile`/`CvhReader` would reject, then converts |
| Sidecar extension | `tools/mdl-to-glb/SidecarDocument.cs` (+ `GlbModel.cs`, `GlbReader.cs`, `NativeModelAdapter.cs`) | optional `mdlFrames` (raw FrameData incl. **up** vectors, file order) — backwards compatible; P812-era sidecars without it fall back to mount-synthesized frames |
| CLI | `tools/mdl-to-glb/Program.cs` | `import <glbDir> <stem> [--out DIR]` (glb → mdl/cvh) and `verifyimport <artDir> <stem> [--out DIR]` (native → glb → mdl/cvh → parse-compare + byte-diff) |
| Render check | `tools/p823-render-proof/` | loads a **written** `.mdl` through the port's real renderer (`NativeShipRenderer` — the class the client's flight/hangar draw loop uses) and saves PNGs |
| Tests | `tests/Allegiance.Core.Tests/GlbToMdlTests.cs` | 14 cases (fast tier): writer/parse bit-exactness, byte-stable canonical cycle, cvh generation properties, the new-art end-to-end case through `HullWeaponPositions`/`HullLandingBay`, malformed-input rejection, native round trips |

Reused unchanged from P812: `GlbWriter`, `GlbReader` (extended for the sidecar's
lights/frames), `GlbModel`, `SidecarDocument` types, `Compare` (+ two new
parsed-layer comparators: `CvhCompare`, `MdlFileCompare`).

## 2. The `.mdl` writer

Emits exactly what `engine/mdlbin.cpp` `MDLBinaryNameSpace::ReadFile`
(:219-313) reads — the port's `MdlParser` (`src/Allegiance.Core/Data/MdlFile.cs`)
is the single source of truth and parses the output:

- **Header**: magic `0x0DEBADF00`, version `0x00010000`, namespace/import/
  export/definition counts (`mdlbin.cpp:232-237`). Namespaces: `model`,
  `effect`, then one per texture stem. Each stem imports **itself** from its
  own namespace (the convention of every native hull file, e.g. fig30's
  `fig30bmp`/`fig30bmp`) — this is what makes `TextureGeo` resolve
  (`MdlFile.cs` `CollectMeshes`, `t.Image is MdlImport`).
- **C-strings**: NUL-terminated, padded to a 4-byte boundary —
  `mdlbin.cpp:83` (`(length + 3) & ~3`).
- **Token stream**: native's postfix order is **arguments first, then the
  function import, then `Apply`** — verified against the raw bytes of
  fig30/bom04/ss27 (`frame` = `[Float][Import][Apply]`; `object` =
  `[arg…][Import][Apply]`); `TextureGeo` = `[stem import][MeshGeo][fn][Apply]`
  with **no `End`** after the nested MeshGeo (it is an argument on the same
  stack machine, `mdlbin.cpp:154-206`). `Binary` payloads (MeshGeo / LightsGeo /
  FrameData) sit between the function import and the object's `End`
  (`MdlFile.cs:335-421` gives the payload layouts: MeshGeo `nv, ni, 8·nv
  floats (x,y,z,u,v,nx,ny,nz), ni ushorts`; FrameData `count, (padded name +
  9 floats)×`; LightsGeo `count, (RGBA + XYZ + 5 timing floats)×`).
- **Exports** `frame, lights, frames, object` — the four every native hull file
  carries. `object` = `GroupGeo([mesh…])`, textured meshes wrapped
  `TextureGeo(<stem>, MeshGeo)`.
- **LOD**: "if the GLB provides them (else one level)". The P812 GLB export
  ships the base meshes + the sidecar's baked pixel radii only — per-level
  geometry is deliberately not in the container — so the writer emits the
  **single detail level**. See §4 (this is the known, explained native
  round-trip delta).

FrameData resolution: sidecar `mdlFrames` verbatim when present (keeps the
`frames` export **byte-identical** to the native file); otherwise synthesized
from the mount nodes — name = `NativeNameMapper`, position/forward from the GLB
node, up = (0,1,0) unless |forward·up| ≥ 0.9 then (0,0,1) (the fallback rule
native's hardpoint-orientation code uses, `Igc/hullTypeIGC.cpp:117-124`).

## 3. The `.cvh` writer

**Sidecar round trip** — the parsed `.cvh` records (raw file values) are written
back in native's exact printf formats:
header `%16.8f` ×5 (`cvh/main.cpp:684`), hull line `%5d%5d` + `%18.8f` ×3
(:698), vertex line `%18.8f` ×3 + `%5d` (:709), adjacency `%5d` per id,
frames as three physical lines (name, `%20.10f` ×3 position, forward —
`dat2cvh` copies the authoring `.dat` verbatim, :756-781). The writer never
emits a file the port's own reader rejects: `CvhWriter.Validate` applies the
same "four oracles" `CvhReader` enforces on read.

**Generated (new art)** — one convex hull around the whole model (native's
`-extract` shape), built the way `cvh/main.cpp` builds it, then frames from the
hardpoint nodes. Coordinates are mapped into the `.cvh` file space — the
`.mdl` space mirrored in z, `(x,y,z) → (x,y,−z)` on positions AND forwards
(the P812 finding, re-verified on all three report hulls). The ported walk,
with citations:

1. **Hull triangles**: native flattens the mesh (`x2pl`, :121-175) and shells
   out to the qhull binary (`qhull -o -Tv`, :58, :98). Substituted with a
   self-contained quickhull (`ConvexHull.cs`); exact-duplicate points are
   dropped (qhull merges them too).
2. **Used vertices**: vertices referenced by **more than two faces** are hull
   vertices; the rest are singular and dropped (`cUsed > 2`, :404-431, :422).
3. **Radius**: the most distant used vertex from the **origin** (:440-460) —
   not from the hull centre.
4. **Ellipse** (minimum volume): attempt 1 = sphere of `radius` (:553-556);
   attempt 2 = bounding-box half-extents scaled by the containment fudge
   `rMax` (:559-590); attempt 3 = all vertex triples through the `reduce()`
   3×4 gaussian (:206-276) with solution values > 1e-4 (:640-648), accepted
   only if it contains every used point within the 1.01 "bit of fudging"
   (:665). *Documented simplification:* the O(n³) triple search is capped at
   `CvhWriter.EllipseTripleSearchCap` (150) used vertices — above it, attempts
   1-2 only (the shipped hulls have ≤152 cvh vertices; new-art hulls are
   smaller still).
5. **Adjacency**: declared count = Euler's `2·(V + F − singular − 2)`
   (:697); per used vertex, the other two corners of every incident face,
   deduped, corners filtered to used vertices (`addAdjacent`, :278-292). The
   written sum is checked against the declared count before the file is
   emitted — a degenerate hull fails loudly instead of producing a file
   `CvhReader` would reject.
6. **Frames**: one per mount — sidecar-named mounts keep their native names;
   glb-only mounts synthesize (§5). Positions/forwards mirror-z'd.

## 4. Round trips (fig30 / bom04 / ss27) — every parsed structure bit-exact

`mdl-to-glb verifyimport` = native `.mdl/.cvh` → P812 `.glb` + sidecar → P823
writer → parse both sides with the port's loaders. Full logs:
`~/allegiance-evidence/P823/<stem>.verifyimport.txt` (raw glb/sidecar/written
files in the same directory).

| | fig30 | bom04 | ss27 |
|---|---|---|---|
| meshes / tris / verts | 2 / 546 / 479 | 2 / 525 / 607 | 3 / 577 / 843 |
| mesh positions+UV+normals, indices | **bit-exact (Δ=0)**, identical | **bit-exact**, identical | **bit-exact**, identical |
| mounts (name/position/forward) | 10, **Δ=0** | 11, **Δ=0** | 16, **Δ=0** |
| FrameData (name/pos/**fwd/up**) | 8, **bit-exact** | 9, **bit-exact** | 14, **bit-exact** |
| LightsGeo (colour/pos/timing) | 8, **bit-exact** | 6, **bit-exact** | 44, **bit-exact** |
| `.cvh` radius/ellipse/hulls/vertices/adjacency/frames | 1 hull, 51 verts, **bit-exact** | 1 / 36, **bit-exact** | 15 / 152, **bit-exact** |

**Not byte-identical — each differing byte explained** (the brief's alternative
arm). The `.mdl` files share a byte-identical prefix up to the `object` export;
the `.cvh` files parse to identical floats:

| Hull | identical prefix | first diff | cause |
|---|---|---|---|
| fig30 | 1044 bytes (`0x000`–`0x413`) | `0x414` | the `object` export: native carries `LODGeo([3 detail levels…], base)`; the writer emits the single detail level (the GLB container has no per-level geometry — P812's documented export shape). Header, import table, `frame`/`lights`/`frames` objects are **byte-identical**. |
| bom04 | 234 bytes (`0x000`–`0x0E9`) | `0x0EA` (2 bytes) | the `frame` export value: native carries `60.0`, the writer emits `1.0` (`MdlWriter.FrameExportValue`). The port's parser does not retain this number and no port consumer reads it; the next diff is the same LOD region at `0x3F8`. |
| ss27 | 148 bytes | `0x094` | the import table: ss27's native file orders imports `…LODGeo, GroupGeo, MeshGeo, TextureGeo, <stems>` (a different mdlc run); the writer's canonical order matches fig30/bom04 (`…LODGeo, MeshGeo, TextureGeo, <stems>, GroupGeo`). Then the LOD region as above. |

`.cvh` byte diffs, all benign — **the parsed values are bit-identical**
(`CvhCompare` asserts exact float equality on every field):

- fig30: 264 differing bytes / 5377; bom04: 142 / 4413 — all inside float
  decimals. The sidecar stores **float32** (what `CvhReader` itself parses);
  the native text carries 8-10 decimals, so re-emitting from float32 loses
  digits beyond ~7 significant ones (e.g. fig30's radius prints back
  `4.00528860` instead of `4.00528882` — both parse to the identical float
  `4.005288600921631`; re-parsing the written text returns exactly the
  original floats, text rounding being orders of magnitude below one ulp).
- ss27: 10660 / 19473 — ss27's on-disk `.cvh` was produced by a **different
  native generator variant** with narrow fields (`    8 56 -0.08865273 …`
  single-space separated) while fig30/bom04 use the wide `%16.8f/%18.8f`
  formats of the current `cvh/main.cpp` (compare `fig30.cvh` line 3). The
  writer emits the modern formats; numbers match digit-for-digit where the
  float precision reaches.

**Canonical stability:** the writer is a fixed point on its own output —
new-art glb → mdl/cvh → P812 export → P823 import again produces
**byte-identical** `.mdl` and `.cvh` (`GlbToMdlTests.Mdl_writer_output_is_byte_stable_under_export_import_cycle`).

## 5. `HP_<Kind>_<Index>` → native frame names

`NativeNameMapper` (the inverse of P812's `MountKindMapper`) synthesizes names
in native's substring vocabulary so both consumers recognise them — the FX
classifier (`effect/efapp.cpp:562-583`: thrust/smoke/rocket → engine;
weapon/wepatt/wepemt/wepmnt/trail → attachment point) and the sim's exact-name
reads (`Igc/hullTypeIGC.cpp:80` `cockpt`, `:85` `chaff`, `:89-136` the core's
per-hardpoint `frameName`, `:158-204` `launch<N>` / `garage<N><A..E>`):

| GLB node | written frame name |
|---|---|
| `HP_Weapon_<i>` | `wepmnt<i+1>` |
| `HP_Turret_<i>` | `turwepmnt<i+1>` |
| `HP_MainEngine_<i>` | `thrust` (i=0), `thrust<i+1>` |
| `HP_Thruster_<i>` | `smoke<i+1>` |
| `HP_Cockpit_0` | `cockpt` |
| `HP_Chaff_0` | `chaff` |
| `HP_DockingEntrance_<i>` | `garage<1+i/5><A+i%5>` (5 land planes per bay, `hullTypeIGC.cpp:176-204`) |
| `HP_DockingExit_<i>` | `launch<i+1>` |
| `HP_Frame_<i>` | `frame<i+1>` (no native consumer reads these) |

Every synthesized name survives `MountKindMapper.KindFor` (glb → mdl → glb
keeps its kind — asserted in tests). A P812 sidecar carrying the mount table
always wins: original native names (`wepemt`, `garage1B`, …) pass through
unchanged.

## 6. The new-art case (brief §3)

A hand-built `.glb` **in code** (no sidecar, no committed asset): octahedral
low-poly hull (6 verts / 8 tris, radius 3, nose +Z, texture stem `hullbmp`) +
`HP_Weapon_0`, `HP_Weapon_1`, `HP_MainEngine_0`, `HP_Cockpit_0`,
`HP_DockingEntrance_0` → `import` → written `.mdl` (828 bytes) + `.cvh`
(1283 bytes; generated hull: 6 vertices, adjacency sum 24 = 2·(6+8−2), ellipse
a=b=1, c=3):

- `MdlFile.Parse`: 1 mesh, 8 triangles, geometry bit-exact, `TextureStem =
  "hullbmp"` resolved through the written `TextureGeo`; FrameData synthesized
  (`wepmnt1/wepmnt2/thrust/cockpt/garage1A`); `lights` export present (empty).
- `CvhReader.Parse`: radius 3 (origin-distance of the nose, `cvh/main.cpp:440-460`),
  every mesh vertex a hull vertex (mirror-z), frames mirrored.
- **Sim mount readers**: `HullWeaponPositions.Compute` with a core whose
  hardpoints are named `wepmnt1`/`wepmnt2` → muzzles `(-5,0,-5)` and `(5,0,-5)`
  (mdl `(±1,0,1)` → cvh `(±1,0,-1)` → HitTest flip `(-x, y, z)` → × scale 5 =
  `0.5·Length/radius`, `hullTypeIGC.cpp:62`); `HullLandingBay.Reconstruct` →
  1 land slot / 1 plane at `(0,-5,0)` from `garage1A`, no launch slots. All
  asserted in `GlbToMdlTests.New_art_glb_converts_to_mdl_cvh_the_port_and_mount_readers_accept`.

## 7. Render check

`tools/p823-render-proof/` loads the **written** `.mdl` files through the
port's real renderer path (`NativeShipRenderer` — the client's own model
renderer; hangar-preview framing `DockedHullPreviewFraming` + a three-quarter
view; textures fall through to the artwork checkout via `LayeredAssetSource`).
All four written models load and draw; coverage figures are pixel sanity, not
appearance claims. **I cannot view images on this lane — Claude checks them:**

- `.scratch/p823/p823-p823-fig30-hangar.png` (+ `-threequarter.png`) — written
  fig30, textured (fig30bmp), 546 tris, coverage 18.5% / 8.6%
- `.scratch/p823/p823-p823-bom04-{hangar,threequarter}.png` — 525 tris, 4.0% / 1.8%
- `.scratch/p823/p823-p823-ss27-{hangar,threequarter}.png` — 577 tris, 11.7% / 6.5%
- `.scratch/p823/p823-p823-testhull-{hangar,threequarter}.png` — the new-art
  hull, no texture (renders lit-solid, by design), 8 tris, 8.3% / 3.4%

(mirrored with the raw artifacts to `~/allegiance-evidence/P823/`.)

## 8. Artist checklist (authoring rules for new art)

1. **Axis/space**: author in the raw `.mdl` space — DirectX-style
   **left-handed, +Y up, +Z nose**. The exporter keeps it raw; native's `RY(π)`
   render flip is a runtime rule, not asset data (P812 §4). glTF viewers will
   show the model z-mirrored; that is expected.
2. **Scale/units**: true game size, metres. Scale is a runtime rule — ships
   `0.5·Length/cvhRadius` (`hullTypeIGC.cpp:62`), stations
   `radius/originalRadius` (`stationtypeigc.cpp:47`) — no scaling is baked.
3. **Hardpoints**: empty nodes named exactly `HP_<Kind>_<Index>` (kinds:
   `Weapon`, `Turret`, `MainEngine`, `Thruster`, `Cockpit`, `Chaff`,
   `DockingEntrance`, `DockingExit`, `Frame`), indices 0-based per kind. A
   node's local **+Z** is its forward (muzzle/nozzle axis); node translation =
   mount position. §5 maps them onto the native names the sim and FX read.
4. **Weapon hardpoints ↔ core**: the core's `HardpointData.frameName` must use
   the same `wepmnt<N>` (1-based) names the converter synthesizes — that is the
   name the sim looks up in the `.cvh` (`hullTypeIGC.cpp:91-93`).
5. **Textures**: the `.mdl` carries no image data; name each mesh node
   `<anything>_<texstem>` where `<texstem>` is the compiled texture unit stem
   (ends in `bmp`, e.g. `myceliumhullbmp`). Texture baking/PNG→`.bmp.mdl` is a
   separate task.
6. **LOD**: not authored in v1 — the converter writes a single detail level;
   the runtime picks it at every distance (native LOD thresholds stay a native
   -file concept).
7. **Collision**: author none — the converter generates the convex hull and the
   mount frames (`cvh/main.cpp` algorithm, §3). If a sidecar is present its
   collision is written back verbatim instead.

## 9. Verification (the gate)

- `dotnet build AllegianceCore.slnx` — **0 warnings / 0 errors** (no `src/`
  change → the AOT publish gate does not trigger).
- `tools/build-tools.sh` — **all 87 tool projects built, 0/0** (incl. the
  extended `mdl-to-glb` and the new `p823-render-proof`).
- New tests `GlbToMdlTests` (14; artwork-backed cases ran — the workstation has
  the corpus) run **3× alone: 14/14, 14/14, 14/14**.
- One fast-tier run `tools/run-tests.sh --tier fast` — **PASS: 10,732 tests,
  306.0 s** (Core.Tests fast 5,323 = master's 5,309 + the 14 new).
- `scripts/parity-scorecard.py` — passes; the three P812 `content` rows now
  cite the P823 writer (CSV kept LF, `grep -c $'\r'` = 0).
- `tools/check-glossary-links.py` — OK.
- No `docker-compose.yml` change (no new service/store/port/env var — proof
  tools are not deployables).

## 10. Files

| Path | What |
|---|---|
| `tools/mdl-to-glb/MdlWriter.cs`, `CvhWriter.cs`, `ConvexHull.cs`, `NativeNameMapper.cs`, `GlbImporter.cs` | the writer (P823) |
| `tools/mdl-to-glb/SidecarDocument.cs`, `GlbModel.cs`, `GlbReader.cs`, `NativeModelAdapter.cs`, `Compare.cs`, `Program.cs` | extended for the sidecar `mdlFrames` field, parsed-layer comparators, `import`/`verifyimport` commands |
| `tools/p823-render-proof/` | render check through the real renderer |
| `tests/Allegiance.Core.Tests/GlbToMdlTests.cs` | the writer's tests (fast tier) |
| `docs/parity/parity-matrix.csv` | the three `content` rows' evidence updated |
| `GLOSSARY.md` | P823 entry beside the P812 pipeline entry |
| `docs/reports/P823_GLB_TO_MDL.md` | this report |
| `.scratch/p823/` + `~/allegiance-evidence/P823/` | glb/sidecar/written mdl+cvh, verifyimport logs, PNGs (not committed) |

## 11. What is deliberately not done

- **Native-art byte-identity**: not achievable through the P812 container by
  design (LOD variant meshes and the `frame` export value are not carried);
  §4 enumerates every differing region and proves the parsed structures
  bit-exact. If byte-identity is ever wanted, the sidecar would need to carry
  the LOD meshes + import order per file — declined as scope creep.
- **Texture baking** (PNG → `.bmp.mdl`): separate task, unchanged from P812 §6.
- **Per-file `frame` export value**: the port's `MdlParser` does not retain
  it; the writer always emits 1.0 (4 explained bytes in bom04).
- **Multi-hull generated collision**: the generator writes native's
  `-extract` shape (one hull per model). Native's `-bound` per-part hulls
  would need part segmentation — no current consumer needs it.

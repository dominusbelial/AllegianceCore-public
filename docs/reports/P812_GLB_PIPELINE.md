# P812 — a GLB mesh pipeline (native `.mdl` → glTF) for the HD pack and new races

**Task:** investigation + prototype only, no runtime change. Idea source:
wivuu/stellarallegiance `docs/GLB-AND-HARDPOINT-FORMAT.md` (meshes as `.glb`,
hardpoints as empty nodes `HP_<Kind>_<Index>`, collision sidecar). Ideas only —
no code copied.

**Base:** master `9d78ec6b` (re-checked; all line numbers below re-grepped on
2026-10-25 in `~/Development/Allegiance/src/`). Branch `pi/p812`, worktree
`~/allegiance-pi-p812` (public clone; per the P812 lane rules nothing in
`src/`/`tests/`/`tools/` carries local paths or Meshy assets).

**Deliverable:** `tools/mdl-to-glb/` — export a native hull (`.mdl` + `.cvh`)
to a glTF 2.0 `.glb` with hardpoints as `HP_<Kind>_<Index>` empty nodes and a
collision sidecar, then re-import it and compare numerically. Proof tool only;
the game still ships `.mdl` (see §7).

**Port before/after:** before — the port could read native models only through
its own runtime loaders (`MdlFile`/`CvhReader`); there was no standard-tool
path for NEW art. After — a documented, tested `.mdl`↔glTF interchange
(export verified bit-exact on three hulls) that new Mycelium/Meshy art can be
authored against, with the port's own parser/reader as the single source of
truth. **How the data reaches the client:** unchanged — mesh/mount geometry
never rides the wire (no native message carries it); the client loads the
same art files via the runtime loaders (§1 "Binding").

---

## 1. Inventory — what a native ship/station model carries

A hull's model is **three files** plus the static-core record that binds them:

| # | Piece | Where it lives (native) | What the port reads |
|---|-------|-------------------------|---------------------|
| 1 | **Geometry** — MeshGeo token stream (per-vertex `x,y,z,u,v,nx,ny,nz` packed floats + `ushort` triangle indices), wrapped by GroupGeo / TextureGeo (texture namespace per subgroup) / LODGeo (distance detail levels) | binary `.mdl`, written by `mdlc/`, read by `engine/modeler.cpp` `MeshGeoFactory` (:1596-1664), `GroupGeoFactory` (:1570), `TextureGeoFactory` (:1310), `LODGeoFactory` (:1666-1687, pairs `(pixels, geo)` into `LODGeoImpl::m_radii`, `engine/geometry.cpp:1634`, consumed by `LODGeoImpl::Render` :1654) | `src/Allegiance.Core/Data/MdlFile.cs` (`MdlFile` :87, `MdlParser` :155; `MdlLodLevel` carries the baked pixel radii); rendered by `src/Allegiance.ClientCore.Game/Rendering/NativeShipRenderer.cs` / `NativeStationResolver.cs` |
| 2 | **Client FX frames** — named position/forward/up records (`FrameData`): engine nozzles, trail/rocket/smoke emitters, weapon emitters, turrets | `.mdl` `FrameData` binary chunks; `effect/efapp.cpp:352` `FrameDataFactory`; name classification at model-build time `effect/efapp.cpp:562-583` (substring rules: `thrust`/`smoke`/`rocket` → engine, `weapon`/`wepatt`/`wepemt`/`wepmnt`/`trail` → attachment point); consumers `effect/thinggeo.cpp:935` (`trail` offset), `:1428` (`thrust` glow decals) | `MdlFile.Frames` (`MdlFrame`) |
| 3 | **Blinking lights** — RGBA colour + position + period/phase/rampUp/hold/rampDown | `.mdl` `LightsGeo` binary chunks (`effect/efapp.cpp:101-112` `LightData`, Read :330-340, reached via the `lights` export) | `MdlFile.Lights` (`MdlLight`) |
| 4 | **Collision hulls** — one or more convex hulls (centre + vertices + vertex adjacency) under a bounding radius/ellipse | text `.cvh`, `HitTest::Load` `_Utility/HitTest.cpp:639-807`; `ConvexVertex` :228, `SingleHull` :287 | `src/Allegiance.Core/Collision/CvhReader.cs` / `CvhTypes.cs` (raw file values); dump tool `tools/cvhdump` |
| 5 | **Sim mount points** — named frames the hull/station loaders read BY NAME from the `.cvh`: per-hardpoint weapon muzzles + orientations, launch slots (`launch<N>`), landing planes (`garage<N><A..E>`), cockpit, chaff | `Igc/hullTypeIGC.cpp:80` (`cockpt`), `:85` (`chaff`), `:89-136` (weapon muzzles + orientations from `hd.frameName`), `:158-204` (launch/garage); stations `Igc/stationtypeigc.cpp:47,57-138`; ship cockpit `Igc/shipIGC.cpp:1423`; probe emitters `Igc/probetypeigc.cpp:43` | `src/Allegiance.Core/Collision/HullWeaponPositions.cs` (`ChullTypeIGC::Initialize` port), `HullLandingBay.cs` (+`StationLandingBayCache.cs`, `WeaponMuzzleCache.cs`) |
| 6 | **Textures** — the `.mdl` carries **no image data**; each texture namespace is its own compiled unit (`<stem>bmp.mdl`, e.g. `fig30.mdl` imports `fig30bmp`) shared across model variants | `LoadModel` texture-override rules `WinTrek/trekigc.cpp:1836-1839`; model's own import `effect/thinggeo.cpp:1038-1043` | `src/Allegiance.ClientCore.Game/Rendering/MdlTextureDecoder.cs`; resolution rules in `NativeShipResolver.ResolveTextureStem` |

**Binding.** `DataHullTypeIGC` / `DataStationTypeIGC` carry `ModelName`
(`src/Igc/igc.h:1767-1820` / `:2649`; decoded in
`src/Allegiance.Core/Data/DataHullTypeIGC.cs` / `DataStationTypeIGC.cs`).
Everything geometric is looked up on those two files; the wire never carries
mesh or mount geometry — the client loads the same art files.

**Key structural facts (each verified numerically in §5):**

- The `.mdl` and the `.cvh` are the **same model in two spaces**. The `.cvh`
  file is the `.mdl` space **mirrored in z** — position AND forward both map by
  `(x,y,z) → (x,y,−z)`. Verified on every frame the two files share across
  fig30/bom04/ss27: positions agree to ≤4e-5 (file text rounding), forwards to
  ≤0.002 (unit vectors). Native then applies further sign flips **at load**
  (`HitTest.cpp:712-715` negates centre/vertex `x`; `:790-797` negates frame
  `position.x` and `forward.y/forward.z`) — the port reproduces those flips in
  `HullLandingBay.Transform` (`HullLandingBay.cs:165-172`).
- Native renders the mesh with `RY(π)` (`m_bFlip`, `effect/thinggeo.cpp:1028-1029`,
  `:1500-1503`; `Igc/shipIGC.cpp:1411-1412`), so the game-facing nose is −Z
  while the `.mdl` nose axis is +Z (the port mirrors exactly this chain,
  `NativeShipRenderer.cs` `NoseFlip`).
- Native winding is **DirectX clockwise-front**; the port reverses each
  triangle for MonoGame CCW (`NativeShipResolver.CorrectWinding`).
- Native art contains **non-unit normals** (bom04 mesh 1: 43/599 vertices with
  |n|−1 up to 0.86) — glTF validators want unit normals, so the shipping
  decision must either normalize at import or keep the raw values and accept
  validator warnings (the prototype keeps raw values for bit-exact round trips).
- **Scale is a runtime rule, not asset data**: ships `m_scale = 0.5·length /
  originalRadius` (`Igc/hullTypeIGC.cpp:60-63`); stations
  `scale = radius / originalRadius` (`Igc/stationtypeigc.cpp:47`).
- Frame-name conventions are **substring** contracts, not enums
  (`effect/efapp.cpp:562-583`, sim reads by exact name in
  `hullTypeIGC.cpp`/`stationtypeigc.cpp` above). Classification:
  design (an authoring contract), era constraint (substring matching is brittle
  — a modern pipeline would use an explicit kind tag per mount, which is
  exactly what the `HP_<Kind>_<Index>` naming gives).

## 2. The prototype (`tools/mdl-to-glb/`)

Zero-dependency .NET console tool (references `Allegiance.Core` only, so the
port's own parser/reader are the single source of truth):

| Command | What it does |
|---------|--------------|
| `export <artDir> <stem> [--out DIR]` | Parses `<stem>.mdl` + `<stem>.cvh`, writes `<stem>.glb` + `<stem>.glbside.json` |
| `roundtrip <artDir> <stem> [--out DIR]` | Re-reads the `.glb` + sidecar back into the port-shaped structures and prints the numeric comparison (exit 1 on any mismatch) |
| `inspect <artDir> <stem>` | Inventory printout (meshes/frames/mounts/hulls + cvh-vs-mdl agreement) |

- **GLB** (`GlbWriter.cs`): one root node; one mesh node per resolved MeshGeo
  (POSITION/TEXCOORD_0/NORMAL + raw `ushort` indices, **native order and
  winding preserved**); one **empty node per mount** named
  `HP_<Kind>_<Index>`, translation = mount position, rotation = unit quaternion
  carrying local **+Z onto the mount's forward** (the wivuu convention); one
  node per nav light.
- **Kind mapping** (`MountKindMapper`, `GlbModel.cs`): native's own substring
  rules mapped onto the wivuu kinds — `tur*`→`Turret`,
  `*wepemt*/*wepmnt*/*wepatt*/*weapon*/*missemt*`→`Weapon`,
  `*thrust*/*trail*`→`MainEngine`, `*smoke*/*rocket*`→`Thruster`,
  `*cockpt*`→`Cockpit`, `*chaff*`→`Chaff`, `*garage*`→`DockingEntrance`,
  `*launch*`→`DockingExit`, else escape-kind `Frame`.
- **Sidecar** (`SidecarDocument.cs`): format id `allegiance-mdl-sidecar/1`;
  source file hashes; the coordinate-space/winding/scale notes; texture stems;
  LOD pixel radii; light blink timing (period/phase/rampUp/hold/rampDown —
  not expressible in core glTF); the mount table (each entry carries its
  native name, sources (`cvh` authoritative, `mdl` FX evidence) and the
  unmerged `.mdl` forward); and the full parsed `.cvh` (hulls + vertices +
  adjacency + frames, raw file values, with the `HitTest::Load` flip recorded
  as a note).
- **Reverse loader stub** (`GlbReader.cs`): reads the GLB (chunks, accessors,
  node tree) + sidecar back into `GlbExportModel` — the shape a future runtime
  loader would fold into `MdlFile`/`CvhFile`-equivalent port structures.
  It understands exactly what the writer emits (documented stub, not a
  general glTF loader).
- `render-check.py` — headless Blender (Workbench engine, Solid shading,
  MATCAP studio light, no materials) for the PNG evidence.

Tests: `tests/Allegiance.Core.Tests/MdlToGlbTests.cs` (28 cases — container
round trip, HP naming/kind mapping, cvh→mdl space mapping incl. the
real-artwork proof, sidecar round trip, and the three hulls' full round trip
via `[MemberData]`; artwork-backed cases are `[SkippableTheory]` +
`[Trait("Requires","Artwork")]` and skip on a clean clone).

## 3. Round-trip numbers (the report evidence)

Fighter = `fig30` (the PCore006 "Fighter" hull mesh), bomber = `bom04`
(the `HullModelResolver` bomber example), station = `ss27` (garrison).
Artwork: local native art corpus (outside the repo). Raw exports, sidecars,
logs and PNGs also copied to `~/allegiance-evidence/P812/`.

| Hull | meshes | tris | verts | mounts (`HP_*`) | lights | LOD levels | cvh hulls | cvh verts | GLB bytes | sidecar bytes |
|------|--------|------|-------|-----------------|--------|-----------|-----------|-----------|-----------|---------------|
| fig30 | 2 | 546 | 479 | 10 | 8 | 3 | 1 | 51 | 21,656 | 22,738 |
| bom04 | 2 | 525 | 607 | 11 | 6 | 3 | 1 | 36 | 25,732 | 19,682 |
| ss27 | 3 | 577 | 843 | 16 | 44 | 1 | 15 | 152 | 37,696 | 77,668 |

`mdl-to-glb roundtrip` (all rows PASS; full logs in `~/allegiance-evidence/P812/<hull>.roundtrip.txt`):

| Check | fig30 | bom04 | ss27 |
|-------|-------|-------|------|
| mesh.count / vertexCount / indices / names | 2/2, equal, identical, identical | 2/2, equal, identical, identical | 3/3, equal, identical, identical |
| mesh.maxFloatDelta (positions+UV+normals) | **0 (bit-exact)** | **0** | **0** |
| mount.count / nodeNames | 10/10, identical | 11/11, identical | 16/16, identical |
| mount.maxPositionDelta | **0** | **0** | **0** |
| mount.maxForwardDelta (quaternion-encoded, unit-compare) | 8.7e-08 | 1.75e-07 | 1.28e-06 |
| light.count | 8/8 | 6/6 | 44/44 |
| collision (hulls/vertices/adjacency/frames) | 1/51, identical, 5, Δ=0 | 1/36, identical, 8, Δ=0 | 15/152, identical, 16, Δ=0 |
| lod.radii | sidecar/mdl authoritative (not carried by glb alone) | 〃 | 〃 |

`ROUND-TRIP OK` for all three hulls.

**Headless Blender renders** (Workbench / Solid / matcap; I cannot view images
on this lane — Claude checks them in review; numbers above are the geometry
proof):

- `.scratch/p812/fig30.png` (1280×960)
- `.scratch/p812/bom04.png`
- `.scratch/p812/ss27.png`

(mirrored to `~/allegiance-evidence/P812/*.png`; pixel sanity: non-blank,
luminance σ 32–52 — evidence only, not a claim about appearance).

## 4. Space/winding/scale conventions the pipeline must pin down

1. **Export space = raw `.mdl` space** (+Z nose, DirectX LH, y-up 2-D).
   Every mount is emitted in this space; the cvh contribution is mapped by the
   verified mirror-z rule (§1). Rationale: the exporter's contract is "lossless
   container of the native files", so no transform is baked and the round trip
   is bit-exact. A runtime consumer chooses its own flip (the port does
   `RY(π)`; the sim mounts read `HitTest`-flipped values via
   `HullLandingBay.Transform`).
2. **Winding:** raw native order preserved; consumers reverse per triangle
   (glTF/CCW tools) or not (DirectX-style). Recorded in the sidecar.
3. **Scale:** not baked. Sidecar records the native formulas (§1); a GLB
   authored for the port should be built at true game size — the authoring
   convention to document for Meshy/Mycelium art.
4. **Normals:** kept raw (§1 non-unit finding); normalization is a consumer
   decision (glTF-strict pipeline should normalize at import).

Classifications: mirror-z `.cvh` file space — **era constraint** (a 1998-era
authoring artifact; a modern pipeline would keep one space); `RY(π)` mesh flip
— **era constraint** of the native render chain, not a data property;
substring frame-name contract — **design** (brittle; the `HP_<Kind>_<Index>`
node naming is the modern replacement); non-unit normals — **bug** (art data,
not spec).

## 5. Recommendation: keep `.mdl` as the shipping format; glTF as the authoring interchange

**Recommend `.mdl` stays the shipping format for native content; adopt glTF as
the authoring/interchange path for NEW art (Mycelium/Meshy), loaded through a
 converter at build/pack time — not a runtime glTF loader.**

- **Why keep `.mdl` shipping:**
  - The whole render path (P566 native-mesh renderer, P628 LOD, P530 lighting,
    HD texture add-on) is built around `MdlFile` + the compiled texture units;
    a runtime glTF loader would re-buy winding, axis, LOD, texture-atlas and
    material handling for zero native-parity gain.
  - Native behaviour parity stays trivially auditable: the shipping bytes are
    the native bytes.
  - The `.cvh` sidecar dependency (sim mounts + collision) already exists and
    is keyed by name; a GLB-only runtime would still need the `.cvh` or an
    equivalent sidecar.
- **Costs of keeping `.mdl`:** new art needs a converter run (this tool's
  inverse direction: glb→`mdl`/`cvh` writer, not yet written — see §6);
  `.mdl` is a DirectX-era binary nobody's tools author natively.
- **Why glTF for authoring:** Meshy/Blender/gltf ecosystems speak it natively;
  `HP_<Kind>_<Index>` nodes + the sidecar give the hardpoints/collision a
  standard home; the wivuu "GLB nodes are the authoritative mount inventory"
  model composes cleanly with the port's name-keyed `.cvh` frames.
- **Runtime-glTF alternative (rejected for now):** one loader feeding
  `MdlFile`-shaped data would remove the converter from the asset path, but
  costs a full glTF runtime (materials, skins, samplers, draco), a second
  asset-validation surface, and parity risk on every hull; revisit only if
  Mycelium art ships in volume.

## 6. What is deliberately not done (next steps)

- **`glb → .mdl/.cvh` writer** (the inverse of `GlbWriter`) — needed before any
  new-art pipeline can REPLACE a native file; the round-trip stub is the
  reading half only.
- Material/texture baking: the sidecar names texture stems; a
  `.bmp.mdl` → PNG → glTF-material export is unwritten (the HD pack already
  ships its own texture path — `~/Development/AllegianceCore-hd` is private
  read-only and was not modified).
- `KHR_lights_punctual` emission for the nav lights (kept in the sidecar
  because native's blink timing doesn't map to static punctual lights).
- Blender render is evidence-only; no visual parity claim is made.

## 7. Meshy rule

Old Meshy ships are never public; the HD pack is a separate download
(memory `project_meshy_ships_never_public`). This task generated no Meshy
assets and commits none: `.glb`/sidecar/PNG evidence lives in
`.scratch/p812/` (git-ignored) and `~/allegiance-evidence/P812/` (outside the
repo). Only the tool, its tests and this report are committed.

## 8. Parity rows added (`docs/parity/parity-matrix.csv`, area `content`)

| native_item | native_cite | status | evidence |
|-------------|-------------|--------|----------|
| `.mdl` binary model format (MeshGeo/GroupGeo/TextureGeo/LODGeo token stream; "object"-export walk; baked LOD pixel radii) | `engine/mdlbin.cpp`; `engine/modeler.cpp:1310,1570,1596-1664,1666-1687`; `engine/geometry.cpp:1622-1654` | done | `src/Allegiance.Core/Data/MdlFile.cs` (P566/P628 render path `NativeShipRenderer.cs`); tests `MdlParseTests`, `P701MdlSceneTests`; P812 prototype round-trip `tools/mdl-to-glb` |
| `.cvh` collision-hull grammar + frames, raw-file values with the `HitTest::Load` sign flips reproduced as port transforms | `_Utility/HitTest.cpp:228,287,639-807` | done | `src/Allegiance.Core/Collision/CvhReader.cs`, `HullLandingBay.cs:165-172`; tests `CvhParseTests`, `LandingBayTests`; `tools/cvhdump` |
| Frame-name → mount/FX classification and the sim's name-keyed mount reads (muzzles/launch/garage/cockpit/chaff) | `effect/efapp.cpp:562-583`; `effect/thinggeo.cpp:935,1428`; `Igc/hullTypeIGC.cpp:80-136,158-204`; `Igc/stationtypeigc.cpp:47-138` | done | sim side: `HullWeaponPositions.cs`, `HullLandingBay.cs` (tests `HullWeaponPositionsTests`, `LandingBayTests`); FX-side classification + modern `HP_<Kind>_<Index>` replacement documented and round-tripped by P812 (`tools/mdl-to-glb`, tests `MdlToGlbTests`) |

## 9. Verification (the gate)

- `dotnet build AllegianceCore.slnx` — **0 warnings / 0 errors**.
- `tools/build-tools.sh` — **all 84 tool projects built, 0/0** (incl. the new
  `tools/mdl-to-glb`).
- New tests `MdlToGlbTests` (28) run **3× alone: 28/28, 28/28, 28/28**.
- One fast-tier run `tools/run-tests.sh --tier fast` — **PASS: 10,696 tests,
  303.4 s** (Core.Tests fast count 5,310 = 5,282 + the 28 new).
- **Which tests fail on master:** none — this is an investigation + prototype
  with no runtime change (`src/` is untouched; the only existing-file edits
  are the test csproj reference, the `.slnx` tool registration, the parity
  rows and the glossary). The AOT publish gate does not trigger (no
  `Allegiance.Server`/`Allegiance.Core` source change).
- `scripts/parity-scorecard.py` — passes (new `content` area 3/3 rows validate).
- `tools/check-glossary-links.py` — OK (162 distinct paths).
- Blender renders: 3 PNGs written headless (paths §3).

## 10. Files

| Path | What |
|------|------|
| `tools/mdl-to-glb/mdl-to-glb.csproj` + `Program.cs`, `GlbModel.cs`, `NativeModelAdapter.cs`, `GlbWriter.cs`, `GlbReader.cs`, `SidecarDocument.cs`, `Compare.cs`, `render-check.py` | the prototype tool (proof tool; built by `tools/build-tools.sh`; registered in `AllegianceCore.slnx`) |
| `tests/Allegiance.Core.Tests/MdlToGlbTests.cs` (+ csproj project reference) | the tool's tests (fast tier) |
| `docs/reports/P812_GLB_PIPELINE.md` | this report |
| `docs/parity/parity-matrix.csv` | 3 new `content` rows (§8) |
| `GLOSSARY.md` | GLB pipeline entry under Tools |
| `.scratch/p812/` + `~/allegiance-evidence/P812/` | exports, sidecars, PNGs, logs (not committed) |

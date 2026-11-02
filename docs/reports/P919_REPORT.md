# P919 — model tools completion

Implemented the offline `optimize` command, certified the existing modding
commands, corrected inherited LOD textures in the game loader, and reconciled
only `docs/parity/model-tools-matrix.csv`. Changes remain in the working tree;
no commit or push was made. Work used the existing isolated `codex/p919`
worktree and its `local.env`. All generated art and raw logs are under the
ignored `.scratch/p919/` directory.

## Scorecard before and after

Both snapshots were produced by `python3 scripts/parity-scorecard.py`.

| Model tools | Before | After |
|---|---:|---:|
| Weighted credit | 0 / 18 (0.00%) | 16 / 16 (100.00%) |
| Completed / eligible rows | 0 / 8 | 7 / 7 |
| Credited offline-tool rows | 0 | 7 |
| Design-replaced weight | 0 | 5 |
| Excluded rows | 3 | 4 |

The denominator falls by weight 2 because TOOL-0004 is a confirmed native bug,
not working animation-compression behavior. TOOL-0001 and TOOL-0009 contribute
weight 5 under the scorecard's existing `native_class=design-replaced` policy:
they remain eligible, use `port_status=implemented`, and have a `REPLACED:`
reason. The other five eligible rows contribute weight 11. No scorecard code
or other parity matrix was changed. The four excluded rows retain their
uncounted reachability value; all seven eligible rows use `offline-tool`.

The complete local snapshots are `.scratch/p919/scorecard-before.txt` and
`.scratch/p919/scorecard-after.txt`.

## Row verdicts and evidence

Native paths below are relative to the read-only native `src/` tree. Line
numbers were checked directly in this session. Port paths are repository
relative. Test names identify executable regression coverage, not source-file
presence alone.

| Row | Verdict | Native citation | Port citation | Test / verification |
|---|---|---|---|---|
| TOOL-0001 | Implemented, **design-replaced**, offline-tool | `mdlc/mdlc.cpp:43-79` loads a model namespace, writes binary, then reloads | `tools/mdl-to-glb/GlbImporter.cs:15`, `MdlWriter.cs:56`; `src/Allegiance.Core/Data/MdlFile.cs:138,186` accepts binary magic only | `GlbToMdlTests.Written_mdl_parses_with_bit_exact_geometry_frames_and_lights`; P919 real CLI workflow |
| TOOL-0002 | Implemented, offline-tool | `effect/efapp.cpp:713-815`; `engine/geometry.cpp:327-377,1009-1068,1187-1399,1789-1900`; write/reload at `mdlc/mdlc.cpp:109-152` | `tools/mdl-to-glb/MdlOptimizer.cs:17,65,73,117,144`; CLI at `Program.cs:430`; inherited LOD texture traversal at `src/Allegiance.Core/Data/MdlFile.cs:437` | P919 material/texture/group fixed point, common-vertex/UV-seam/normal tests, LOD hoist test, five real corpus cases, CLI workflow |
| TOOL-0003 | Implemented, offline-tool | `mdlc/mdlc.cpp:153-207` converts an image into an engine texture namespace | `tools/mdl-to-glb/Program.cs:187,257`, `PngCodec.cs`, `src/Allegiance.Core/Data/MdlTextureWriter.cs` | `P824TexturePipelineTests.Writer_emits_native_byte_layout_for_a_2x2_texture`, PNG/GLB/texture tests; P919 CLI image/pixel comparison |
| TOOL-0004 | **n/a, bug** | `mdlc/mdlc.cpp:209-262`, specifically `231-240`: comment says broken after the texture-memory change, `ZAssert(false)`, null image, commented `CreateFrameImage` | Matrix reason records the source-confirmed failure; no compressor added | Direct native source audit; no fabricated port test for excluded behavior |
| TOOL-0005 | n/a, era constraint (unchanged ruling) | `mdlc/mdlc.cpp:98-108` generates a C++ type header | Matrix exclusion: native compile-time plumbing | Native source audit; no executable port claimed |
| TOOL-0006 | Implemented, offline-tool | `mdlc/mdlc.cpp:81-97` prints LOD geometry info | `tools/mdl-to-glb/Program.cs:143,461` prints mesh/triangle/vertex/mount/light/LOD/collision information | P919 CLI test asserts the actual inspect command's printed fields; deleting the dispatch or summary fails it |
| TOOL-0007 | Implemented, offline-tool | `cvh/main.cpp:294-750`; frame consumers at `Igc/hullTypeIGC.cpp:80-204` | `tools/mdl-to-glb/CvhWriter.cs:45`, `ConvexHull.cs`, export/import dispatch in `Program.cs` | `GlbToMdlTests.Cvh_writer_round_trips_sidecar_collision_bit_exactly`, `Generated_cvh_hulls_the_mesh_and_mirrors_frames_into_cvh_space`, real-art parsed CVH comparison |
| TOOL-0008 | Implemented, offline-tool, existing GLB bridge contract | Native namespace read/write pipeline at `mdlc/mdlc.cpp:43-79,109-152`; GLB itself is a port addition | `tools/mdl-to-glb/GlbWriter.cs`, `GlbReader.cs`, `NativeModelAdapter.cs`, `SidecarDocument.cs`, `GlbImporter.cs` | `MdlToGlbTests.Native_hull_roundtrips_through_glb`, `GlbToMdlTests.Native_hull_exports_to_glb_and_writes_back_with_parsed_structures_bit_exact`, P919 CLI/P916 orientation regression |
| TOOL-0009 | Implemented, **design-replaced**, offline-tool | `mdledit/mdledit.cpp:1219-1307,1660-1761` is a graphical test viewer with MDL/X/image/test-scene entry points | `REPLACED:` external GLB editors through `tools/mdl-to-glb/Program.cs` export/import and the reader/writer/importer | `GlbToMdlTests.New_art_glb_converts_to_mdl_cvh_the_port_and_mount_readers_accept`, P919 real CLI workflow; external editor UI itself is outside repository tests |
| TOOL-0010 | n/a, era constraint (unchanged ruling) | `mdlc/mdlc.cpp:280-300` prompts for video settings, initializes D3D9 and engine buffers | Headless CLI; matrix exclusion | Source audit plus successful CLI operation without a graphics context |
| TOOL-0011 | n/a, era constraint (unchanged ruling) | `mdlc/mdlc.cpp:272-277` combines JSON and Windows registry configuration | Explicit CLI input/output paths; matrix exclusion | Source audit plus CLI workflow with explicit paths |

No textual MDL interpreter exists in the port: `MdlFile.Parse` immediately
validates the compiled format's magic/version. TOOL-0001 therefore records the
approved GLB-to-native replacement rather than equating binary writing with
parsing native textual MDL programs.

## Optimization behavior

The optimizer keeps a token graph independent of the single-detail GLB
adapter. It retains all namespace exports, raw binary FrameData and LightsGeo
payloads, the `frame` value, texture imports and every LOD mesh/threshold.
It removes MaterialGeo wrappers, folds nested/shared TextureGeo bindings
(including common textures across LOD levels), removes empty/single-child
and nested groups, combines compatible meshes and maps common vertices.
Vertex equality includes position, UV and normal, as native
`engine/context.h:160-173` and `engine/geometry.cpp:327-377` require.
A conservative 16-bit vertex-count boundary keeps oversized meshes separate.
Material removal is followed by geometry folding; texture folding and geometry
folding repeat until the serialized graph stops changing. An already optimized
result is byte-stable on the next pass.

The native callback extracts `frm-*` groups before removing materials:
`effect/efapp.cpp:527-610,754-770`. Compiled binary GroupGeo has only its child
list, with no group-name field (`engine/geometry.h:464-493`,
`engine/value.cpp:282-296`, `engine/modeler.cpp:1570-1590`). For this command's
compiled inputs, the extraction result is already represented by the
`frames`/`lights` exports. The optimizer preserves these records instead of
trying to recover names absent from the binary. The command does not import
`.x` or interpret textual MDL. Unknown constructors/binary payloads are rejected
before output is written; this is not a general interpreter for arbitrary
native effect programs or animated transforms.

Both the optimizer API and the CLI reload the result with `MdlFile.Parse`.
This is the same loader used by the game at
`src/Allegiance.ClientCore.Game/Rendering/NativeShipRenderer.cs:128`.
The CLI requires a distinct output path and copies existing CVH and available
referenced textures unchanged alongside the optimized model.

Hoisting a shared texture above an LOD exposed a loader issue: levels were
collected while parsing LODGeo, before the enclosing TextureGeo was known.
`MdlFile` now retains that node until geometry collection, so base and lower
levels inherit the enclosing binding. The synthetic LOD test would fail with
the old loader, and tests compare every level's oriented triangle samples,
including per-corner UVs, normals and texture names. Tests separately compare
all light color/position/timing values, frame position/forward/up values and
CVH-authoritative mount names/vectors.

The five native corpus models were already folded: their reachable geometry
node counts stayed equal, and their full geometry/metadata survived. Reduction
is demonstrated by the synthetic nested/material/shared-texture case, rather
than attributed to a no-op on shipping assets.

| Native model | Nodes before → after | Base meshes | Triangles | Vertices | LOD levels | Lights | MDL frames |
|---|---:|---:|---:|---:|---:|---:|---:|
| fig30 | 9 → 9 | 2 | 546 | 479 | 3 | 8 | 8 |
| bom04 | 9 → 9 | 2 | 525 | 607 | 3 | 6 | 9 |
| ss27 | 13 → 13 | 3 | 577 | 843 | 1 | 44 | 14 |
| fig05 | 10 → 10 | 2 | 453 | 526 | 3 | 4 | 12 |
| utl18a | 9 → 9 | 2 | 540 | 531 | 3 | 9 | 9 |

## Real ship and P916 texture orientation

Executed real `export`, edit-free `import`, `optimize`, and `inspect` commands
for artwork ship `fig30`, plus `verifyimport`. The output model reloaded in the
game's MDL parser with 546 triangles, 479 vertices, 8 lights and 8 MDL frames;
including CVH supplied 10 merged hardpoints, 1 collision hull and 5 CVH frames.
The optimized imported model was 19,736 bytes with 4 → 4 geometry nodes.

The 512×512 `fig30bmp` texture survived MDL file rows → embedded GLB PNG →
imported MDL → copied optimized texture with identical decoded RGBA at
`flipVertical:false`. The CLI regression also checks the intermediate PNG
against the source, so matching but cancelling vertical flips would fail.
No export/import orientation correction was needed after P916. The existing
texture byte verifier was tightened to constrain allowed padding differences
to both ends of the three-byte BinarySurfaceInfo padding interval.
`verifyimport` passed: only the three uninitialized texture padding bytes
differed; parsed frames, lights and CVH were exact.

The GLB bridge's established P823 fallback remains explicit: export carries
base meshes and sidecar LOD radii, but import writes one detail level.
The native fig30 file was 53,676 bytes with 3 coarser levels; its imported file
has none. This size difference is not an optimizer win. CVH text formatting
also changes bytes while parsed values remain exact. The matrix notes and
modding guide now state the LOD limitation. Direct binary optimization retains
all the original levels, as the corpus table and tests demonstrate.

## mdledit replacement and abilities outside it

Native `mdledit` is a test/viewer application rather than a general-purpose
mesh editor. It opens `.x` geometry, MDL ThingGeo (with optional `_m.x` bounds),
model-namespace images, and developer test/effect scenes. The approved modern
replacement exports models to GLB for interactive viewing/editing in external
editors, then imports them to the native files loaded by the game.

The replacement does not provide direct `.x` or textual-MDL ingestion, a
standalone native image viewer, native animation/frame controls, `_m.x` bounds
overlays, or the native developer test scenes/effect playground. The existing
sidecar's raw native frame and collision data remain authoritative, so editing
those features requires updating that metadata too. No external editor was
installed or exercised in this task; the repository tests cover its format
boundary and the generated native assets.

`docs/GUIDE.md` now documents export/edit/import/optimize/inspect, destinations,
texture naming, sidecar handling, limitations and what the game loads. README
and the tool's old prototype/proof-only comments now describe this offline
modding path. GLOSSARY records optimization and the LOD inheritance rule.

## Gates

- `~/.dotnet/dotnet build AllegianceCore.slnx -p:UseSharedCompilation=false -v:quiet`:
  passed, 0 warnings and 0 errors.
- `tools/build-tools.sh`, with `DOTNET` selecting a scratch wrapper that invokes
  `~/.dotnet/dotnet` and appends `-p:UseSharedCompilation=false`:
  passed, all 110 tool projects built, 0 warnings and 0 errors.
- Focused tool tests, three isolated invocations, each with `--no-build` and
  filter `MdlToGlbTests|GlbToMdlTests|P824TexturePipelineTests|P919ModelToolsTests`:
  **90 passed, 0 failed, 0 skipped each time**, including artwork cases.
  The 11 new P919 cases also passed three times with only that class selected;
  the 10 directly related MDL loader/scene regressions passed separately.
- Real ship CLI workflow, `verifyimport`, and five native corpus optimization
  commands: passed. Generated assets/logs remain in `.scratch/p919/`.
- Scorecard validation and `git diff --check`: passed. Glossary link check:
  passed, 499 distinct paths checked.

Per the task-specific timing restriction, no Fast tier, full test suite,
training window, synthetic CPU load or remote-machine action was run. No new
service, port, store or environment variable was added. No media is added to
the public working-tree changes.

## Native-intent footer

**Design:** the working native compile/optimize/info/image/CVH pipeline serves
asset authoring. The modern equivalent is an offline GLB bridge, a dedicated
binary graph optimizer and external editors, with native output consumed by
the game. Preserve gameplay-relevant mounts/collision and metadata while
allowing geometry packing to change. Text-language compilation and the test
viewer are explicit design replacements, not claims of native UI/language
implementation.

**Era constraint:** C++ generated headers, D3D9 device startup, the Windows
registry and native video-setup prompts are build/host mechanisms. A headless,
explicit-path CLI avoids those dependencies while delivering the asset work.

**Bug:** native animation compression is explicitly broken and leaves
the frame image null; excluding it is the honest ruling. A future
working animation-authoring feature should have an explicit modern animation
contract and executable tests rather than replicate that stub. There is no
scorecard expressiveness blocker: `native_class=bug` with `port_status=n/a`
already represents this ruling without changing the scorer.

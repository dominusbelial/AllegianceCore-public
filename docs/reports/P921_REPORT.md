# P921 — preserve authored LODs through the GLB bridge

The offline bridge now exports and imports every authored reduced-detail
level. The game renderer, MDL loader, shipped artwork and optimizer behavior
are unchanged. No commits, pushes or remote-host operations were performed.

## Container and editor evidence

Each reduced level has a parent named `LOD_<index>_px_<radius>` with custom
properties `allegianceLodIndex` and `allegiancePixelRadius`, and its mesh nodes
as children. The properties are authoritative; the name is a fallback if an
editor strips properties. The index preserves **file order**, independently
of GLB node order. Base geometry stays outside these parents. Materials keep
the texture stems, including bindings used only by a reduced level. Geometry,
UVs, normals and indices are in the GLB, not the sidecar.

Blender **5.2.2 LTS** was actually run locally, using its bundled executable
because the Snap launcher failed before startup. A real glTF import/re-export
probe compared names, extras, `MSFT_lod` and a level outside the default scene:

- Names, parent/child hierarchy and custom properties survived with
  `export_extras=True` (**Custom Properties** in Blender's exporter).
- `MSFT_lod` was discarded.
- The secondary-scene objects survived, but Blender merged/reorganized scene
  content: the original scene arrangement/default visibility did not survive.

Consequently all levels share **one scene**. They overlap until the artist
hides the levels they are not editing; separate default visibility is not
promised. The tested workflow is in [GUIDE.md](../GUIDE.md#model-modding).
Unhide every level and disable selection/visibility export filters before
exporting. Retain the sidecar for collision, raw frame vectors and lights.

A second real import/re-export used exported **fig30**, then the actual CLI
imported Blender's output successfully: all three parents/radii, child mesh
counts, triangle counts and texture stems survived. Blender rewrote some
UV/normal float values and split one vertex (the 32 px mesh: 387 → 388),
producing a 53,780-byte MDL. This editor pass demonstrates preservation of the
LOD structure; **bit-exact geometry is the direct bridge's guarantee**, not a
claim about an external editor's mesh processing.

The small reproducible [editor probe](P921_assets/editor_probe.py) takes the
Blender executable, scratch destination and an already exported fig30 GLB.
[Editor transcript](../../.scratch/p921/editor-final.log) is retained locally.
Example after exporting fig30:

```bash
python3 docs/reports/P921_assets/editor_probe.py "$BLENDER" \
  .scratch/p921/editor-proof .scratch/p921/editor/fig30.glb
# Copy fig30.glbside.json beside the re-export, using its new filename.
dotnet tools/mdl-to-glb/bin/Debug/net10.0/mdl-to-glb.dll \
  import .scratch/p921/editor-proof fig30-edited --out .scratch/p921/editor-native
```

## Native and port evidence

Native references were re-read/re-grepped from the local read-only native tree:

| Rule | Native source | Port implementation / proof |
|---|---|---|
| List entries are complete objects ending in `End` | `engine/mdlbin.cpp:95-106` | `MdlWriter.cs:162`; independent `TokenProbe` in `P921LodBridgeTests.cs` |
| Pair pops radius first, then geometry | `engine/mdlbin.cpp:140-145` | `MdlWriter.cs:148-151` |
| Write reduced list, each `geo Float(px) Pair End`, then base, `Import(LODGeo) Apply` | `engine/geometry.cpp:1937-1953` | `tools/mdl-to-glb/MdlWriter.cs:140-173`; native file token envelopes compared for all five models |
| Apply consumes base then level pairs; radius and geometry remain paired | `engine/modeler.cpp:1666-1687` | `GlbReader.cs:158-251`, `NativeModelAdapter.cs:31-37` |
| Render selects by descending thresholds, using strict `radius > threshold` to stop | `engine/geometry.cpp:1760-1773` | existing `NativeShipRenderer.cs:195-242`, `ShipLod.cs:206`; `tools/p921-lod-proof/Program.cs` |

GLB parent metadata is a port authoring extension, not a native rule. Its
writer is `tools/mdl-to-glb/GlbWriter.cs:221-249`. The common intermediate
`GlbModel.cs:20-23` carries radius and geometry together. `GlbImporter.cs:31-67`
validates all base/level geometry and finite, positive, distinct radii;
`GlbImporter.cs:91-97` checks level-only texture images too. CLI texture rename
and strip options also traverse reduced levels (`Program.cs:214-241`).

Imported per-mesh TextureGeo bindings pass through the existing P919 inherited
texture traversal: `src/Allegiance.Core/Data/MdlFile.cs:437-442,460-474`.
No loader change was necessary. The parity note and evidence were updated on
**TOOL-0008** (and the obsolete TOOL-0009 fallback note removed); no rows added.

## Five-model direct round trips

File order below is the native serialized order, **not** descending rendering
order. All counts and float bits, including positions, UVs, normals, indices,
radii and texture stems, match after export → GLB read → MDL write → game
MDL parse. Source frame/light records travel through the sidecar.

| Model | Reduced levels | Radii, file order (px) | Triangles: base; reduced levels | Vertices: base; reduced levels | Native → imported bytes |
|---|---:|---|---|---|---:|
| fig30 | 3 | 8, 32, 64 | 546; 48, 414, 434 | 479; 102, 387, 399 | 53,676 → 53,748 |
| bom04 | 3 | 8, 32, 64 | 525; 25, 155, 296 | 607; 49, 207, 325 | 45,274 → 45,346 |
| ss27 | 1 | 64 | 577; 519 | 843; 782 | 61,996 → 61,996 |
| fig05 | 3 | 8, 32, 64 | 453; 19, 208, 365 | 526; 45, 265, 388 | 46,742 → 46,814 |
| utl18a | 3 | 8, 32, 64 | 540; 43, 393, 536 | 531; 87, 423, 527 | 60,624 → 60,696 |

The four +72-byte files gain a canonical single-child GroupGeo wrapper around
each of their three reduced levels: 24 bytes per wrapper (List/count, child
End, GroupGeo import/Apply). ss27 already groups its reduced meshes. Geometry
has not been inflated or lost. Canonical namespace/import order and the
existing constant `frame=1` writer policy can also change bytes (e.g. bom04's
native frame value is 60); identical size does not imply byte-identical files.
The exact parsed geometry is tested, as is preservation through subsequent
`optimize` (which may legitimately merge/reindex meshes).

## Rendering and regression checks

The new offline proof creates a real local MonoGame graphics device and two
production `NativeShipRenderer` instances: one loads native fig30, the other
loads the exported/reimported MDL through an asset-source override. It checks
the renderer's descending radii `[64,32,8]`, selects geometry with production
`ShipLod.NativeLodLevelFor`, draws both to a render target and compares every
pixel. All **11** samples (0, 1000, and one float below/at/above each threshold)
have identical level selection, vertex/triangle counts and nonempty pixels.
It writes no screenshots and opens no network connection.
[Local render output](../../.scratch/p921/render-proof.log).

Tests added in `P921LodBridgeTests.cs` cover unsorted authored order, several
meshes per level, a level-only texture, exact native token envelopes, all five
native models, optimize retention, invalid radii, invalid/empty level meshes,
missing images, legal no-LOD new art, and an old-export warning through the
real CLI. `GlbToMdlTests`' old empty-level assertion now requires a real level;
`MdlToGlbTests`' synthetic radii now have geometry. `Compare` and
`MdlFileCompare` also compare reduced geometry/radii instead of accepting loss.

Validation results:

- Whole solution: passed, **0 warnings / 0 errors**.
- All **111** tool projects: passed, **0 warnings / 0 errors**.
- Affected offline tests: **105 passed, 0 failed, 0 skipped**, three runs.
- New Live render test: **1 passed, 0 failed, 0 skipped**, three isolated runs.
- Exactly one Fast tier: **12,964 passed / 12 failed / 3 skipped**, nine
  infrastructure checks passed, two timing violations. **Overall FAIL** from
  untouched test families; every failing method/theory family and timing
  offender was rerun three times in isolation. Results are ledgered in
  [TEST_FLAKES.md](../TEST_FLAKES.md#p921-offline-lod-bridge-gates--2026-11-02).
  Held-Enter and combat-wire passed 3/3; side selection and timer boundaries
  were intermittent; wingman-defend and bundle-path cases reproduced. The
  read-only profile failure passed 3/3 with writable task configuration.
  The three skips were optional private Meshy-pack cases. The Fast sweep
  remains failed; these isolation results do not turn it green.
- Parity scorecard before/after: identical; model-tools weighted **16/16**, eligible rows **7/7**.
- Glossary path validation: **508 paths valid**; `git diff --check` passed.

Commands used `~/.dotnet/dotnet`; NuGet HTTP cache and temporary files were
redirected under `.scratch/p921/` because the sandbox cannot write NuGet's
home-directory HTTP cache. `local.env` was present and loaded; **none of the
five artwork cases skipped**. Raw local logs/TRX remain in ignored
[task scratch](../../.scratch/p921/) because this sandbox permits evidence
writes only inside the worktree. No raw artwork/GLB or media is added to Git.

## Remaining limits

- No automatic decimation, threshold tuning, renderer/HD-pack changes or new art.
- Mesh count changes cannot reliably diagnose stale reduced geometry: valid
  levels often have different mesh counts. No speculative warning is emitted;
  artists must review levels after base edits. Missing all geometry with
  sidecar radii **does** warn with model name and lost count (`GlbReader.cs:59-60`).
- Preserve parent metadata (or fallback names), consecutive indices and material
  names. Removing both metadata forms removes the only GLB level identity.
- Supported geometry is float32 positions/UVs/normals and indexed triangle
  primitives, including accessor offsets/strides and node transforms. Sparse
  accessors and native-overflowing indices are rejected. Embedded PNG textures
  retain the existing image-name/stem convention; this is not a general glTF
  importer for animation, skinning, compression, arbitrary materials or images.
- Frame/collision edits retain the existing sidecar-authoritative workflow.
  Node-transform baking applies to geometry; it does not change that workflow.
- Blender evidence is for the local version/options tested, not every editor.

New media: **none**. Evidence assets are the small Python editor probe and
[validation-summary.json](P921_assets/validation-summary.json), containing
counts, isolation results, editor findings and render readback metrics. The
proof emits text and performs render readback in memory.

## Native-intent footer

**Design:** native LODGeo deliberately pairs authored geometry with screen-pixel
thresholds so distant objects draw less geometry. Keep that choice, selection
boundary and ordering. **Era constraint:** MDL's postfix binary namespace and
16-bit mesh indices are legacy interchange constraints retained at the output
boundary. **Bug:** the earlier GLB bridge discarded reduced geometry while
retaining only sidecar thresholds; that was a port data-loss bug, not native
intent. A modern authoring bridge carries explicit level groups and metadata
through the editor, validates them, and converts back to native tokens at the
boundary. The chosen core glTF nodes/extras workflow is backed by an actual
editor round trip; it does not depend on unsupported `MSFT_lod` behavior.

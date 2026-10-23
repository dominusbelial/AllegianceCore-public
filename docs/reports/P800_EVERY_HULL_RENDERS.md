# P800 — every hull renders: a measured pass over all 447 hulls

**Base:** master `a1e6420a` (P795) — starts from acc393b9 (the hangar preview's native
z-clip fix Domingo confirmed 2026-10-23). **Branch:** `pi/p800`. **Scope:** client
(`src/Allegiance.ClientCore.Game/**`), a proof tool under `tools/`, tests, this report.
No server/wire/bot change. Ships: native only (`--ships native`); Meshy never used.

---

## 1. Method — measure, do not guess

New proof tool **`tools/p800-hull-proof`** (headless, offscreen GL, ~3 s for the whole
core). It loads the real `PCore001.igc` (447 hulls, **295** with `GroupId ≥ 0` =
player-buyable, **198** offered under testUnlock all-tech — both numbers reproduce the
brief and the live client's own hangar log), and renders **every hull** through the
client's three real render paths:

| view | reproduced exactly as | key numbers |
|---|---|---|
| hangar/loadout preview | the native branch of `Game1.DrawDockedHullPreview` (`Game1.Rendering.cs`): native art, `DockedHullOrbit.World(spin)`, eye = 2.4 × 0.5·Length, FOV 50°, z-clip 1..10000 (`DockedHullPreviewFraming`, native `load.cpp:860-934`), default BasicEffect lighting | 256² RT |
| in-flight ship, NEAR / MID | the flight draw loop (`Game1.Rendering.cs` ~640-810): real sector lighting, native scale `(0.5·Length)/meshRadius` (`thinggeo.cpp:1484-1503`), LODGeo level chosen by `ShipLod.NativeLodLevelFor` over `ShipLod.ProjectedPixelRadius` | eye at 2× / 15× gameplay radius |
| in-flight ship, FAR tier | the P628 far-tier sprite path (`ShipLodSpriteRenderer`, `FarSprite`, the client's exact half-extent + alpha formula) | eye at 100× gameplay radius |
| target-pane model preview | the HUD-21 path (`RenderTargetModelPreviewRenderer` + `TargetModelPreview`): ortho 2×2, z 1..20, radius-1 scale `1/MaxVertexRadius` (`thinggeo.cpp:373-381`), headlight | 256² RT |

Per hull it records: coverage (fraction of non-black pixels), mesh triangle count, mesh
radius (merged bounding sphere), max-vertex radius (native `m_radiusMesh`),
applied scale, texture stem + decode result, LOD levels + the level chosen at
near/mid. Outputs: CSV + per-civ contact sheets + a `P800_SUMMARY` line.

Thresholds detect **empty/degenerate renders** (≈0 coverage), not "small on screen" —
ships are legitimately sparse relative to their merged bounding sphere (a miner's truss
fills a fraction of it).

**Evidence:** `~/allegiance-evidence/P800/` (`before/`, `after/`, `live*/`),
small copies in `P800_EVERY_HULL_RENDERS_assets/` (CSVs, summaries, two before/after
contact sheets, three live-window hangar screenshots, the full per-hull table).

## 2. Gate result — before / after

```
P800_SUMMARY label=before total=447 buyable=295 offeredAllTech=198 ok=439 textureMissing=6 failures=2
             categories=[OK=439 FLIGHT_EMPTY=2 TEXTURE_MISSING=6]
  -> 68 'Stealth Bomber' tfsb2 FLIGHT_EMPTY
  -> 154 'Stealth Bomber [NG]' tfsb2 FLIGHT_EMPTY
P800_SUMMARY label=after  total=447 buyable=295 offeredAllTech=198 ok=441 textureMissing=6 failures=0
             categories=[OK=441 TEXTURE_MISSING=6]
```

Minimum coverage over all 447 hulls, **after** (fraction of the 256² frame):

| view | min over all hulls | hull at the min |
|---|---|---|
| hangar preview | 2.82 % | Bomber / Hvy Bomber / Gunship (id 604/689/690 family) |
| flight NEAR | 0.65 % | Stealth Bomber (`tfsb2`, ids 68/154) — was 0.25 % before the fix |
| flight MID | 0.65 % | Stealth Bomber |
| far sprite | 0.035 % | every hull (the ~5 px livery dot, by design) |
| target pane | 0.83 % | Stealth Bomber |

**Every hull native draws now measures > threshold in every view** (gate met; the only
non-OK category is `TEXTURE_MISSING`, §3.2). The per-hull table (447 rows: id, name,
model stem, civ, buyable, offered, per-view ok, category, note) is
[`per-hull-table.md`](P800_EVERY_HULL_RENDERS_assets/per-hull-table.md) and the raw CSVs
[`p800-hulls-before.csv`](P800_EVERY_HULL_RENDERS_assets/p800-hulls-before.csv) /
[`p800-hulls-after.csv`](P800_EVERY_HULL_RENDERS_assets/p800-hulls-after.csv).
Contact sheets before/after (red frame = classified failure): bios + oh civs in the
assets dir; all civs at full size under `~/allegiance-evidence/P800/{before,after}/`.

## 3. Root causes (with native cites), and fixes

### 3.1 The Stealth Bomber was invisible in flight — fixed in the shared path

**Measured:** hulls **68 / 154 (`tfsb2`, buyable, offered under testUnlock)** rendered
0.25 % coverage at NEAR/MID in flight — the frame showed only faint glints. The hangar
preview and target pane were fine, so the P607 startup audit ("mesh non-null") saw
nothing wrong.

**Why:** the hull's texture is genuinely near-black art — `sbbmp` (512², decoded with
the repo's own `MdlTextureDecoder`) averages **RGB (13, 7, 4)** (albedo ≈ 4 %), versus
(120, 115, 86) for a normal bomber texture. The native ship path lit it with the pure
bidirectional ambient **0.25** (`clusterIGC.h:331-334`) → 0.25 × 0.04 ≈ 1–3/255
luminance: below any visibility threshold. The P566 decision deliberately withheld the
Meshy path's ambient floor from native art and left the question open ("reported in
docs/reports/P566") — this pass answers it.

**The native rule:** native itself ships TWO ambient levels —
`GetBidirectionalAmbientLevel()` = 0.25 (`clusterIGC.h:331-334`, the default,
`WinTrek.cpp:2696` + preference default TRUE at `WinTrek.cpp:3296`) and
`GetAmbientLevel()` = **0.5** (`clusterIGC.h:326-329`), which
`UpdateBidirectionalLighting` applies whenever bidirectional lighting is off
(`WinTrek.cpp:4928-4935`). D3D multiplies material-ambient (= material diffuse,
`device3d.cpp:715-719`) by that bright ambient. A 0.5-class ambient is therefore
inside native's own lighting envelope.

**Fix (shared path, one place):** `NativeShipRenderer.Draw` now lifts the sector
ambient to the shared `ShipRenderer.ShipAmbientFloor` (0.50, 0.52, 0.56) with a
componentwise `Max` — exactly the floor the **Meshy** path
(`ShipRenderer.cs:86,177`) and the **HD/PBR** path (`HdMaterialRenderer.cs:130`)
already apply. Classic native ships now agree with both other paths; normally-painted
hulls (albedo ≫ floor) are untouched — the tool's before/after coverage for every
other hull is unchanged. The Stealth Bomber goes 0.25 % → 0.65 % in flight and
0.68 % → 1.79 % in the target pane, and — decisive for the task —
**a hull native draws now draws in every view**.

### 3.2 The 6 `TEXTURE_MISSING` hulls — artwork-tree gaps native hits identically

| hulls | stem | cause | native outcome |
|---|---|---|---|
| 89 Hvy Scout, 610 Scout [SR], 634 Hvy Scout [SR], 685 Adv Scout [SR] | `fig15` | `fig15bmp.mdl` in the artwork tree is a **65-byte stub** (magic `'use '`, not `0x0DEBADF00`) — the decode fails | native's modeler `LoadImage` (`xfile.cpp:571-586`) loads the same file and gets the same garbage → untextured hull |
| 444 / 844 Lifepod | `utl22` | the model's only texture import `ilpodbmp` has **no `ilpodbmp.mdl`** in the artwork tree | same missing-file load → untextured hull |

The port's rule for both is "render lit-solid, never invisible" — which is the same
outcome native produces with this artwork tree. **Exception accepted with these
cites;** the startup audit now reports texture-stem gaps explicitly (§4) instead of
hiding them behind "mesh resolves". Note the corpus drifted under us mid-task:
`sbbmp.mdl` was added to the artwork tree by another lane (it is now present and
decodes — the 68/154 renders above use it).

### 3.3 Views checked for further fixed-clip/framing assumptions (brief scope item)

- **Target-pane model preview** — ortho 2×2 with z-clip **1..20** and eye at 8, but the
  model is normalised to radius 1 (`TargetModelPreview.ScaleForMaxVertexRadius`,
  `thinggeo.cpp:373-381`), so every vertex lands at 8±1 → inside [1, 20] for every
  hull. No clip risk; measured: every hull ≥ 0.83 % coverage.
- **Inspector (offline + in-client)** — frames by radius through
  `InspectorOrbitCamera.FrameRadius` + `DockedHullPreviewFraming.FovRadians`; no fixed
  far plane.
- **Far-tier sprites** — billboard half-extent is computed from distance; no clip
  dependency.
- **Degeneracy sweep** — no hull has a zero/NaN mesh or vertex radius, a non-positive
  Length, an empty mesh, or a missing stem: all 447 `MeshFor`, all radii finite and
  positive, all Lengths > 0 (see the CSV columns). 352,613 triangles across the core.

## 4. Startup audit strengthened (regression tripwire)

- `ShipNativeAudit` (pure) gains `HullHealth` + `CollectDefects`: empty/zero-triangle
  mesh, non-finite/non-positive mesh or max-vertex radius (the scale and both previews
  divide by them), non-positive hull Length, unresolvable texture stem.
- `Game1.AuditNativeShipResolution` (the P607 startup audit) now runs the same checks
  over the whole loaded core (cheap: meshes are already compiled; the texture check is
  an existence probe, not a decode) and logs every defect by hull:
  `[client] P800 native ship health FAILED: <id> '<name>' <category>`; the summary line
  becomes `… 0 unresolved native, 0 degenerate (P800 health ok)`.

## 5. Tests (which fail on master)

New file `tests/Allegiance.ClientCore.MonoGame.Tests/P800EveryHullRendersTests.cs`:

- `RealCore_hull_population_matches_the_P800_baseline` — 447 / 295 / 198 against the
  real core (fast tier, artwork-gated).
- `RealCore_every_hull_mesh_is_structurally_renderable` — the full CPU audit over all
  447 hulls' real `.mdl`s (triangles > 0, both radii finite > 0, Length > 0, texture
  stem resolvable except the two documented lifepod hulls). **Passes on master**
  (structurally nothing was degenerate after acc393b9) — it is the tripwire that makes
  a future empty-mesh / bad-scale / bad-length hull fail CI instead of rendering black.
- `RealCore_the_only_texture_gaps_are_the_two_lifepod_hulls…` — pins the two justified
  artwork gaps so any NEW gap fails.
- `CollectDefects_flags_every_degeneracy_category` / `CollectUnresolved…` — pure
  decision tests.

New `P800HullProofGpuTests` — runs the proof tool as a child process (offscreen GL,
the CI-1 pattern) over the real core and asserts `total=447, buyable=295,
offeredAllTech=198, failures=0`. **This test fails on master by construction**: on
pre-fix code the tool classifies hulls 68/154 `FLIGHT_EMPTY`
(`p800-summary-before.txt`: `failures=2`); after the fix `failures=0`
(`p800-summary-after.txt`). 6/6 pass 3× alone (each < 10 s, fast tier).

## 6. Which hulls Domingo most likely hit

His session was a hero session with `testUnlock` all-tech → the hangar offered exactly
**198 hulls** (reproduced by the client log line "docked: hangar/loadout screen
(198 hulls …)" and by the tool's `FilteredFor` computation). Within that list:

1. **Flight "won't render at all": hulls 68 / 154 Stealth Bomber** — buyable, offered,
   flight luminance ≈ 1–3/255 before the fix (§3.1). The single flight-visible failure
   in the whole measured core.
2. **"Expensive ships, background goes black"** — the pre-`acc393b9` hangar-preview
   z-clip bug, which hit every hull longer than ~23 u (Destroyer/Devastator/Cruiser/
   Dreadnought class — all buyable+offered). Fixed on master before this pass started;
   verified live here (§7).
3. **"Grey/unfinished-looking ships": the `fig15` family** (89 Hvy Scout, 610/634/685
   [SR] scouts — buyable+offered) render lit-solid because `fig15bmp.mdl` is a stub
   (§3.2) — visible, but untextured.

## 7. Real-window verification (scripts/live-proof.sh)

Live server + windowed client (m2xspiderw, testUnlock allTech → "198 hulls offered"),
docked hangar sweep across the hull menu. Three fixed hulls, full-res 2560×1440
backbuffer shots (full-res + the entire sweeps in `~/allegiance-evidence/P800/live3/`,
1280×720 copies in the assets dir — opened and read: each shows the pane title and the
spinning preview behind the panes):

| hull | shot | shows |
|---|---|---|
| **Destroyer** (GA 11 Finki, long capital — the black-preview class) | `live-hangar-destroyer.png` | whole hull framed, z-clip correct, spin visible behind panes |
| **Stealth Bomber** (SB 1 Dengaroth — the flight-fixed hull) | `live-hangar-stealth-bomber.png` | pane + preview render; in flight the hull is now readable (floor lift) |
| **Hvy Scout** (`fig15`, lit-solid class) | `live-hangar-hvy-scout.png` | renders (untextured, native parity) |

## 8. Verification (light gate)

- Solution build: **0 warnings / 0 errors**; `tools/build-tools.sh`: **72/72 projects**.
- New/affected tests 3× alone: **6/6 pass ×3** (`--filter FullyQualifiedName~P800`).
- One fast-tier run (`tools/run-tests.sh --tier fast`): 10060 tests — **10059 passed**,
  1 failure **outside this task**: `PackagingManifestTests.Artwork_manifest_matches…`
  ("artwork-roots.txt is stale") — the shared artwork corpus was modified mid-suite by
  another lane (`sbbmp.mdl` added); the pinned manifests are byte-identical after
  `git checkout packaging/` and the test passes **3/3 alone**. Ledgered in
  `docs/TEST_FLAKES.md` per the 3/3-alone rule; no repo change made.
- No `src/Allegiance.Server` / `src/Allegiance.Core` change → no AOT publish required.

## 9. Unfinished / follow-ups

- The far-sprite livery for hulls whose first import is a weapon texture (`tfsb2` →
  `cannonbmp`) uses that stem as the sprite look key; the sprite is a ~5 px tinted dot
  so this is cosmetic-only. A per-part livery key would be a polish pass.
- `fig15bmp.mdl` / `ilpodbmp.mdl` live in the artwork corpus, not the repo; if the
  corpus owners fix the stub/missing files the four scouts + two lifepods become fully
  textured with no code change (the audit line will show it).

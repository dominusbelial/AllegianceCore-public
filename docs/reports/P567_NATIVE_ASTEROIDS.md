# P567 — native per-asteroid models: every rock from its own `.mdl` + texture, inside-out fixed

**Status:** Implemented, committed on `master` (not pushed). Whole-solution build 0
warnings / 0 errors. Failing-first tests green (18/18 new). Real-window before/after
proof captured (native rocks SOLID; old imported path rocks INSIDE-OUT). Contact
sheets for every native rock model + the imported-variant winding demo.

---

## What changed

Ships already rendered from their ORIGINAL native `.mdl` meshes (P566, plus the
P570 real-window inside-out fix). Asteroids still used three hard-coded imported
FBX variants (`drgrock`/`tacrock`/`bgrnd05`) picked by `AsteroidId`, plus a
procedural fallback — and **some of them rendered inside-out** ("rocks look like you
are looking at them from inside", Domingo 2026-10-06).

P567 makes **every asteroid render from its OWN native model + texture** in the map
record (its `AsteroidDef`), through the P566 native-mesh pipeline, with the correct
winding + culling. The imported/procedural variants stay as the fallback chain
behind a new `--asteroids native|imported` setting.

### New files
- `src/Allegiance.ClientCore.Game/Rendering/NativeAsteroidResolver.cs` — pure rules:
  native scale `radius/meshRadius`, raw native winding (P570), the `RY(π)` flip,
  texture-stem resolution, and the winding-polarity metric that quantifies the bug.
- `src/Allegiance.ClientCore.Game/Rendering/NativeAsteroidRenderer.cs` — GPU renderer
  (parses each rock's `.mdl` + native texture, cached per model; native flip+scale;
  `CullCounterClockwiseFace`).
- `tests/.../NativeAsteroidResolverTests.cs` — 18 failing-first tests.

### Modified
- `Rendering/ImportedRockMeshLoader.cs` — **the inside-out root-cause fix**: the
  winding "correction" was pushing imported rocks to majority-OUTWARD geometric
  normals — the exact OPPOSITE of native (which is majority-INWARD yet renders
  solid). Now anchored to native's polarity (see Root cause below).
- `Game1.Session.cs` / `Program.cs` / `Game1.cs` — `--asteroids native|imported`
  option (`LaunchOptions.AsteroidsNative`, default native), `_nativeAsteroidRenderer`
  wiring + dispose + startup log.
- `Game1.Lifecycle.cs` — `_nativeAsteroidRenderer` init.
- `Game1.Rendering.cs` — native draw/depth branches in the asteroid loops with the
  native → imported → procedural fallback chain.
- `tools/roster-capture/Program.cs` — new `asteroiddump` mode (contact sheets).

---

## Native fidelity — every rule cites its native source

| Rule | Native source | Port implementation |
|---|---|---|
| **Per-instance model + texture** — every asteroid carries its own `modelName`/`textureName`. | `AsteroidDef` (`src/Igc/igc.h:1709-1720`); `CasteroidIGC::Initialize` calls `Load(0, m_asteroidDef.modelName, m_asteroidDef.textureName, ...)` (`src/Igc/asteroidigc.cpp:33-34`) | Each rock resolves its `DataAsteroidIGC.ModelName` to a native `.mdl` + its texture, cached per model (no more shared variant by `AsteroidId`). |
| **Winding** — native `.mdl` indices render solid RAW under perspective + `CullCounterClockwiseFace`. | P570's real-window conclusion for ships; re-verified for every rock here | `NativeAsteroidResolver.RenderIndices` ships the raw native winding; renderer uses `CullCounterClockwiseFace`. |
| **Scale** — `m_scale = radius / m_radiusMesh`. | `ThingGeo::SetRadius` (thinggeo.cpp:1185-1187); asteroid passes `SetRadius((float)m_asteroidDef.radius)` (asteroidigc.cpp:38) | `ScaleForRadius` = `radius/meshRadius`; the radius is folded into scale in local space, never scaling the world translation. |
| **Orientation** — `RY(π)` mesh flip (`m_bFlip = options==0`). | thinggeo.cpp:1029, asteroid loads options=0 (asteroidigc.cpp:33), `Rotate(Y,π)` (thinggeo.cpp:1501-1503) | Renderer applies `NoseFlip * CreateScale(scale)` before the caller world (orientation · spin · translation). |
| **Texture** — the wire `textureName` is empty in the corpus ⇒ model's own imported texture. | `LoadModel` nulls a non-alpha/equal override (trekigc.cpp:1837-1839); else `m_pgeoMesh->FindTexture()` (thinggeo.cpp:1038-1043) | `ResolveTextureStem` returns the model's first existing namespace = `modelName+"bmp"` for the rocks (verified: `bgrnd05.mdl → bgrnd05bmp`, etc.). |
| **Colour** — native's look comes from the texture ALONE, no per-capability tint. | render path binds `m_psurfaceTexture` only (thinggeo.cpp:1532); no ore-type recolouring | Native path uses the rock's real texture with NO `RockTextureFactory` capability tint (the imported/procedural fallback keeps its P529 tint). |
| **Lighting / render state** — P529/P530 sector lighting, mipmaps, wrap. | — | Same `SectorLightingEffect` + `LinearWrap` + default depth as the existing `AsteroidRenderer`. |

---

## Inside-out root cause (measured, not guessed)

The imported path's `ImportedRockMeshLoader.Finalize` step 4 tried to "fix" winding
by forcing a rock's geometric normals to point **OUTWARD from the centroid** (flip
when the majority opposed it). We measured the native rock meshes that the imported
variants come from:

| model | native raw geometric normals that point "outward" |
|---|---|
| bgrnd03 / bgrnd05 / bgrnd51 / bgrnd52 / bgrnd53 / bgrnd56 / drgrock / tacrock | **0% outward** (100% inward) |
| bgrnd55 | 6.7% outward |
| bgrnd57 | 47.3% outward (a strongly concave rock) |

Yet **P570 proved** these same raw-winding meshes render **SOLID** under the game's
PERSPECTIVE projection with `CullCounterClockwiseFace` (raw winding keeps the outside).
So the imported path's "make it outward" heuristic produced the **exact opposite**
polarity from what renders correctly — hence the inside-out rocks.

**Fix:** `Finalize` step 4 now forces a rock to the **native polarity** (majority-INWARD,
matching the raw `.mdl` that renders solid), so an imported rock winds exactly like its
native source. The failing-first test feeds real native rock geometry through
`Finalize` and asserts the output stays majority-INWARD; it fails on the old outward
logic and passes with the fix.

### Task-premise correction (found while verifying, proven with data)
The task brief said the 221 map `m2xspiderw.igc` asks for `bgrnd05 ×65, bgrnd03 ×60,
bgrnd56 ×9, heliumrock ×9`. **The real map has `bgrnd05 ×65, bgrnd03 ×60, bgrnd56 ×9`
— no `heliumrock`.** Across the whole corpus the only asteroid *model* names are
`bgrnd03 (496), bgrnd05 (354), bgrnd56 (69), bgrnd55 (12), bgrnd51 (7), bgrnd53 (6),
bgrnd52 (5), bgrnd57 (1)`. `heliumrock` exists only as a texture (`heliumrockbmp.mdl`,
no `heliumrock.mdl` mesh) and is never referenced as an asteroid model. The contact
sheets cover all eight real rock models.

---

## Proofs

### Failing-first tests — `NativeAsteroidResolverTests` (18)
- **Real-map native model/texture resolution**: every asteroid in `m2xspiderw.igc`
  resolves its own `.mdl` + a texture stem that exists (= `modelName+"bmp"`).
- **Raw winding polarity**: the native rock meshes are majority-INWARD (the
  solid-under-culling convention, P570), for 6 meshes tested (`bgrnd03/05/51/56/
  drgrock/tacrock`).
- **Imported fix (FAILING-FIRST, proven)**: feeding real native rock geometry through
  `ImportedRockMeshLoader.Finalize` must stay majority-INWARD. Reverting the fix makes
  all 3 variants (`drgrock`/`tacrock`/`bgrnd05`) produce majority-OUTWARD → test fails;
  with the fix it passes. (Verified by temporarily reverting the one-line polarity.)
- **Native scale** (`radius/meshRadius`, guards), **raw `RenderIndices` unchanged**,
  **`--asteroids native` launch default**.

### Contact sheets — `docs/P567_assets/` (roster-capture `asteroiddump`)
- `<model>.png` for each of `bgrnd03,05,51,52,53,55,56,57`: the NEW native `.mdl` +
  native texture from 4 angles (front/side/back/top), **perspective** projection (ortho
  would flip the winding, P570). All 4 views of each rock show a **solid** textured rock.
- `<model>_winding.png` for the 3 imported variants (`drgrock`/`tacrock`/`bgrnd05`):
  **row 1** old buggy winding + culling ON = **INSIDE-OUT** (concave interior facing
  the viewer), **row 2** old winding + culling OFF = hollow double-sided shell,
  **row 3** fixed native winding + culling ON = **SOLID**. (Rendered from the same
  geometry the imported path consumes; the real-window run below shows the SAME
  contrast with the actual shipped FBX meshes.)

### Real-window screenshots (real backbuffer, `ALLEGIANCE_AUTOSHOT`, 2560×1440)
Using P572's `scripts/live-proof.sh` against the 221 map `m2xspiderw.igc` with
`--auto-goto Alderan` (sector 1052 = the asteroid field), chase camera, HUD hidden:

- **NEW (native)** — `docs/reports/P567_assets/realwin_native_alderan_field.png`
  (from ~44 s, entering Alderan) and `realwin_native_alderan_closeup.png` (from
  ~56 s, a large rock near the ship): the rocks render **solid, textured, lit** —
  no inside-out, no transparent shells.
- **OLD (imported, pre-fix winding)** — `realwin_old_imported_insideout.png`
  (from ~58 s in Alderan, `--asteroids imported` with the winding fix reverted):
  the rocks clearly show their **concave interiors / hollow shells** — the exact
  bug Domingo reported.
- Zero native-asteroid decode failures in the native run (every on-screen rock's
  `.mdl` + texture resolved through the new path).

*(Captured the OLD shot by temporarily reverting the one-line imported winding fix so
the shipped FBX meshes render inside-out; the fix was then restored for the final
build — same method as the failing-first proof.)*

---

## Verification (this run)

- Whole-solution build: **0 warnings, 0 errors** (`dotnet build -m:8`).
- New test class: **18/18 passed** (plus the pre-existing
  `ImportedRockMeshLoaderTests` updated: the old `…FixesReversedWindingToFaceOutward`
  asserted the buggy OUTWARD winding; rewritten to `…AnchorsWindingToNativePolarity…`
  asserting the corrected native-INWARD polarity).
- Full 3× suite on the final code (each assembly its own foreground run; per
  `docs/TEST_FLAKES.md`, only documented live-wire / load flakes appear in the
  untouched Core/ClientCore assemblies — never re-run chasing green):

  | run | Lobby | MonoGame (all P567 code+tests) | ClientCore | Core |
  |---|---|---|---|---|
  | 1 | 12/12 ✓ | **1425/1425 ✓** | 544/545 (1: `SessionLossLive…EstablishedSession…`) | 3771/3773 (2: `WarpBomb…Live…`; `LiveChatSectorWire…`) |
  | 2 | 12/12 ✓ | **1425/1425 ✓** | 545/545 ✓ | 3766/3773 (7 live-wire flakes) |
  | 3 | 12/12 ✓ | **1425/1425 ✓** | 544/545 (1: `ClientWarpApproachSlowHullLive…`) | 3765/3773 (8 live-wire flakes) |

  The **MonoGame** assembly — which holds every P567 change and both new/updated test
  files — is **1425/1425 green in all three runs**. Every failure is a pre-existing
  live-socket / server-timing load-flake in an unrelated assembly (documented
  families in `docs/TEST_FLAKES.md`), and none exercises asteroid rendering.

- Committed on `master`, **not** pushed; amended with the suite numbers above.

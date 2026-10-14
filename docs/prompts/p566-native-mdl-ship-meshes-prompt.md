# P566 — render ships from the ORIGINAL native .mdl meshes + native textures (not the Meshy AI models), with a `native | meshy` setting

## Why this task exists

**Domingo (2026-10-06):** "the quality of the ships we have created is not that
good, compared to the original ships, our generated mesh looks ugly/malformed,
we will probably end up replacing the whole thing". Live issues also reported:
flat shading under native lighting, and per-ship transparent/missing mesh
parts (P564 item 5).

**Today:** in-flight ships draw **Meshy AI-generated FBX** models (P287 manifest
`MODELS_MANIFEST_P287.json`, `HullModelResolver` civ+class assets,
P352/P353/P510–P515 per civ) through `Rendering/ShipRenderer.cs`
(`NoseAlignmentCorrection` −90° Y, `ShipRenderScale`, Content `Models/…`).
Fallback `bios_scout_upscaled`.

**The native meshes are already decodable in the port:**
`src/Allegiance.Core/Data/MdlFile.cs` (`MdlMesh`: positions/texcoords/normals/
triangle indices; `MdlFrame`: named frames, e.g. nozzle/weapon points;
multiple meshes per file, LOD variants discarded) is **already used** for
missiles, probes and model projectiles (`ProjectileModelRenderer.cs:59-71`,
`MissileRenderer`, `ProbeRenderer`; textures via `MdlTextureDecoder`, RGB565).
Wiki `allegiance-mdl-model-format` (and the Domingo wiki page of the same
name) documents the format, the **reversed triangle winding** (native draws
`c b a`; the normals are correct), and textures as their own compiled `*bmp.mdl`
units imported by namespace, **shared across model variants** (e.g. `ta_fig.mdl`
→ `utl18abmp`). Every hull's `DataHullTypeIGC.ModelName`/`TextureName` names
its art (PCore001: Adv Scout 285 → `fig05`, Adv Fighter 288 → `wc_gcfig`,
Lifepod 444 → `utl22`, 113 → `wc_gtpod`, …); `~/Development/Artwork` has the
`.mdl` + `bmp.mdl` (+ `.cvh` collision hulls) for all of them.

**Native ship rendering to mirror:** `ThingGeo`/`ThingSite::Load(modelName,
textureName)` in `effect/thinggeo.cpp` and `WinTrek/trekigc.cpp` (find the ship
load path and how the texture is resolved: the model's imported texture vs
the hull's `TextureName`), the model's scale (native radius vs the model's
units; see how `ShipRenderScale`/`load.cpp` preview framing relate), its
orientation conventions, and the **LOD** (`LODGeo`) behaviour.

## Scope

1. **Native ship mesh renderer:** for every hull, build the GPU geometry from
   its native `.mdl` (`MdlFile` → vertex/index buffers, cached per model
   name), with **winding corrected** and normals as decoded, the native
   texture(s) decoded and bound (resolve the texture exactly as native:
   the model's namespace import, shared textures), at **native scale and
   orientation**, lit by the P530 sector lighting with **native ambient
   (no P530 `ShipAmbientFloor` lift for native art, unless the readability
   metric from P564's spec says otherwise; report it)**. Use the
   highest-detail LOD; optionally implement native LOD switching.
2. **Frames:** expose the model's `MdlFrame` points (engine nozzles, weapon
   mounts, garage/landing points if present) and use them for engine-trail/
   nozzle-glow positions where the client currently guesses (report what
   changed).
3. **Setting `--ships native | meshy`** (and a settings entry), default
   **native**. The Meshy path stays intact. The **hangar/loadout preview**
   (P525/P526) uses the same setting.
4. **Coverage:** all player-flyable hulls of every civ in PCore001 (and the
   lifepods, drones and capitals the client draws). Report any hull whose
   native art fails to decode, with the reason, and fall back to Meshy, then to
   the placeholder.
5. Cloak/translucency: render cloaked native ships correctly (see P564 item 5:
   no self-occlusion holes).

## Explicitly out of scope

- Stations (P564 does native station models; reuse the same mesh pipeline
  if convenient and say so).
- Lighting retune beyond native (P564), VFX (P561).

## Proof

- Failing-first tests: winding correction (a known triangle faces outward);
  hull → model/texture resolution for several hulls; scale vs native length;
  the `--ships` setting default and fallback chain.
- **Contact sheets for ALL player hulls of the two 221 civs** (front/side/
  back/top), **native vs Meshy side by side**, with an index page
  (`docs/reports/P566_assets/index.html` or `.md`).
- **Real client screenshots** (window, `ALLEGIANCE_AUTOSHOT`): a dogfight and
  the hangar preview, native vs meshy, plus a lifepod next to a fighter.
- Performance: frame time with 30 native ships on screen vs Meshy.
- Report `docs/reports/P566_NATIVE_SHIP_MESHES.md`.

## Verification (common)

- **Failing-first tests** for every fix: the test fails on today's code and passes after.
- **Native fidelity:** cite native file:line for every rule you port. If you find the
  spot-check's reading wrong, prove it with native code and say so.
- Whole-solution build, 0 warnings and 0 errors. Full suite via `scripts/run-tests.sh`
  (P471), 3 consecutive runs **on the final code**. Cite `docs/TEST_FLAKES.md`.
  **Report only numbers you actually measured. Don't re-run a failing suite until it
  comes up clean;** report each run as it happened.
- ⚠️ **Commit as soon as the code and tests are done, BEFORE the 3× suite**, then amend
  with suite numbers. **Include every new file** (`git status --untracked-files=all`).
  Confirm with `git log -1` and a clean `git status`.
- Committed on **`master`**, not pushed. Check with `git branch --show-current`.

- **Visual proof rule:** screenshots must come from the **real client window** (backbuffer,
  e.g. `ALLEGIANCE_AUTOSHOT`, window 2560×1440). `--capture`/offscreen renders alone don't
  count; that gap hid the P547 (invisible geometry) and P562 (near-invisible aleph) regressions.

## ⚠️ Session rule: never end a turn while waiting

This session runs in one-shot mode: **your session ends the moment you finish a message.** Run long commands (the 3× `scripts/run-tests.sh` suite, live proofs, builds) **in the foreground**, blocking, one after another. **Never** start them in the background and then reply "waiting for it to finish". Two earlier sessions (P529, P542) did exactly that: the session ended mid-suite, the background processes were killed, and the suite results were never recorded. If a command might exceed a tool timeout, run each suite run as its own foreground call and record its numbers before starting the next.

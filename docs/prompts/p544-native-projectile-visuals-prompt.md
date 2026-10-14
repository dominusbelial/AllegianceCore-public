# P544 — render projectiles the native way: per-type textured, spinning, camera-facing sprites (and 3D models where the type names one), not one tinted box for every weapon

## Why this task exists

**Domingo (2026-10-04):** "look on the original client, all weapons had
different effect, power vs projectile had different render effect, right
now all is projectiles, we are missing power, and nanite looks like donuts
look for it".

**Native** (`~/Development/Allegiance/src/Igc/projectileIGC.cpp:50-80`): each
projectile takes its look from its `DataProjectileTypeIGC`:
- **if `modelName` is set** (`iswalpha(modelName[0])`), it loads a **3D model**
  (`Load(0, modelName, textureName, …)`) and `SetRadius(radius)`;
- **otherwise** it loads a **decal**: `LoadDecal(textureName, …, Color(color),
  bDirectional, width, …)`, with the thing-site radius = `radius` (the
  sprite size), plus a spin `Rotation(forward, rotation)` (`:76-80`).
  Non-directional decals are camera-facing sprites. Directional ones stretch
  along the travel direction with `width`. See the decal implementation in
  native's engine/ThingSite (`LoadDecal`, decal geo) for how directional vs
  billboard decals and the colour/additive blend are drawn.

**Real PCore001 data** (Claude dumped every weapon → projectile type):
- **Most guns are non-directional textured sprites with their own texture
  and a spin:** PE Gat Gun 1–3 `f99` (radius 1–2, rot 3.84); PE Mini-Gun
  `tf5`; PE Vul Can 1/2 `dn_ptvulcan1/2`; PE SkyCap `tf8` (r 4–5); PE
  AutoCan/Mini-AC `tf3`; PE EMP `tf7` (rot 4.71); EW Disruptor 1/2/3
  `f28/f19/f20` (rot 5.24); EW Gauss/Sniper `f06`/`f16`; EW Utl Can
  `f26/f27/f18`; EW A-B Stinger/SkyRip/PE Striker `f17`;
  **ER Nanite 1/2/Hvy `f44`, Nanite 3 `f49`** (Domingo's "donuts");
  **anti-base "power" rounds:** PW A-B Omega `faohomega1` (**radius 50**),
  PW A-B Hammer `faohhammer1-3` (rot **8**), PW A-B Ion Bls `dn_ptionbls`,
  EW A-B Can `fx92` (r 7–9), EW Hvy A-B Can `tf4` (r 30–35), EW A-BS Can
  `tf2` (r 15).
- **Directional:** EW Pulse Laser `fx95` (width 0.1, radius 15).
- **3D models:** EW Lsr Can 1/2 and EW Mini-LC 1/2 (`dn_ptlascan1/2`,
  radius 75–125), PE MassDriver (`bgrnd55`, radius 20).
- Colour: the decoded `color` field (check its parse in
  `src/Allegiance.Core/Data/DataProjectileTypeIGC.cs`). P465b noted
  "PCore001 carries no colour, so bolts render amber". Verify whether the
  colour really is empty/white, or whether the textures carry the colour,
  as with native's additive decals.

**The port today:** `Game1.Rendering.cs:~744-800` draws every projectile
through one `ProjectileRenderer` (`Rendering/ProjectileRenderer.cs`: a white
unit box stretched along velocity, tinted by `ProjectileStyle.For(typeId,
_projectileTypes)`, width from the core, amber fallback). The fields
`ModelName`, `TextureName`, `Directional`, `Width`, `Radius` and `Rotation`
are all parsed (`DataProjectileTypeIGC.cs:15-29`) but **unused** for visuals.

**Reuse:**
- `.mdl` texture decoding: the explosion path already decodes original
  RGB565 `.mdl` texture strips at runtime from `ALLEGIANCE_ARTWORK_PATH`
  (`Rendering/ExplosionAssets.cs`, plus `MdlTextureDecoder`), and the HUD
  gauges do too (P241). Load projectile textures the same way, cached per
  texture name.
- 3D projectile models: the missile-model path (P288 `PickMissileModel`,
  `DrawMissileContentModel`) and the `.mdl` mesh import used for rocks and
  ships. Check whether `dn_ptlascan1/2` and `bgrnd55` exist in Content or
  need importing.
- Additive blend and render-state hygiene as in `ExplosionRenderer`
  (restore state after, per the P339/P525/P529 lessons).

## Scope

1. **A per-type projectile visual resolver:** model / directional decal /
   billboard decal, from the real `DataProjectileTypeIGC`, cached.
2. **Billboard decals:** camera-facing quads with the type's texture, size
   from `Radius` (native's radius = sprite size; verify the exact scale
   against native's decal geo), spin by `Rotation` (rad/s) over the
   projectile's lifetime, additive blend, colour tint if real.
3. **Directional decals** (Pulse Laser): stretched along velocity with
   `Width`, faithful to native.
4. **Model projectiles** (laser cannons, MassDriver): draw the model at
   `Radius`, oriented along travel, spun by `Rotation`.
5. **Fallback:** if a texture or model is missing, keep today's box so
   nothing becomes invisible, and log once per missing asset.
6. Keep P462's own-shot local tracer and P239's camera-near cull on the
   same new path.

## Explicitly out of scope

- Missiles, mines, probes (separate renderers).
- Muzzle flashes / hit sparks (separate).
- The VFX spike (P543): bloom and soft particles.

## Verification

- **Failing-first unit tests** on the resolver: Gat Gun → billboard `f99`
  with radius 1 and rot 3.84; Pulse Laser → directional `fx95` with width
  0.1; Lsr Can 1 → model `dn_ptlascan1`; Nanite → billboard `f44`;
  missing-asset fallback → box.
- **Headless visual proof:** a `tools/roster-capture` `projectiles` mode
  (offscreen path as in P525/P529) rendering a contact sheet of every
  distinct projectile type in PCore001, plus one sheet comparing old box vs
  new for Gat Gun, Vul Can, Nanite, Pulse Laser, A-B Omega and Laser Cannon.
  Mention which one is the "donut".
- Whole-solution build, 0 warnings and 0 errors. Full suite via
  `scripts/run-tests.sh` (P471), 3 consecutive runs **on the final code**.
  Cite `docs/TEST_FLAKES.md`. **Report only numbers you actually measured.**
- ⚠️ **Commit as soon as the code, tests and proof are done, BEFORE the 3×
  suite**, then amend with suite numbers. **Include every new file**
  (`git status --untracked-files=all`). Confirm with `git log -1` and a
  clean `git status`.

## Deliverables

1. Committed on **`master`**, not pushed. Check with
   `git branch --show-current`.
2. The report `docs/reports/P544_NATIVE_PROJECTILE_VISUALS.md`: native
   citations, the resolver table (type → visual), contact sheets, tests.

## ⚠️ Session rule: never end a turn while waiting

This session runs in one-shot mode: **your session ends the moment you finish a message.** Run long commands (the 3× `scripts/run-tests.sh` suite, live proofs, builds) **in the foreground**, blocking, one after another. **Never** start them in the background and then reply "waiting for it to finish". Two earlier sessions (P529, P542) did exactly that: the session ended mid-suite, the background processes were killed, and the suite results were never recorded. If a command might exceed a tool timeout, run each suite run as its own foreground call and record its numbers before starting the next.

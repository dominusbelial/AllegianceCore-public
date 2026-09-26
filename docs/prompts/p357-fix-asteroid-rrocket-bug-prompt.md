# P357 — Remove the wrong non-rock mesh (`rrocket`) from the asteroid variety pool

## Why this task exists

A live playtest this session (2026-09-23, the dual-client remote
deployment experiment) surfaced a real, confirmed content bug: some
asteroids visually look ship-shaped, not rock-shaped, and Domingo also
reported they "felt different... like they floated freely instead of
being fixed and rotating" compared to normal asteroids.

**Root cause, confirmed this session, not assumed:**

- `Game1.cs:921` configures the asteroid visual-variety pool:
  `string[] meshAssets = ["Models/drgrock", "Models/rrocket",
  "Models/tacrock"];` — used by `AsteroidRenderer`/
  `ImportedRockMeshLoader` (`AsteroidRenderer.cs:126-131`,
  `ImportedRockMeshLoader.cs:8-9`) to give asteroids one of 3 random
  mesh silhouettes for visual variety, "regardless of the wire
  `ModelName`" (P253's own doc comment) — i.e. every real `OT_asteroid`
  entity gets ANY of these 3 meshes at random, purely cosmetic.
- Rendered all 3 source FBX files directly in Blender this session:
  `drgrock.fbx` and `tacrock.fbx` are genuine, believable rock/boulder
  shapes. **`rrocket.fbx` is not a rock at all** — it renders as a
  blocky, mechanical, panel-and-spike silhouette, nothing like the
  other two.
- Traced to the real native source: `~/Development/Artwork/rrocket.mdl`
  decodes (via `mdl_parse.py`, the project's own established native
  `.mdl` decoder) with **5 named hardpoints: `thrust`, `rthrust`,
  `lthrust`, `smoke`, `trail`** — real ship/projectile engine and
  effect attachment points. No real asteroid has thrust hardpoints.
  `rrocket` (**R-ROCKET**, a guided rocket/missile-type projectile
  model) was almost certainly included in this pool by a simple
  filename substring match on "rock" (`r-ROCK-et`) when the 3 variants
  were originally picked — not because it's actually a rock.
- This explains both symptoms: the wrong SHAPE (a rocket/projectile
  silhouette showing up where a boulder should be) and why it still
  looked "asteroid-textured" anyway — `ImportedRockMeshLoader`'s own
  doc comment confirms the renderer **always discards** whatever
  texture came with the imported mesh in favor of the procedural
  `RockTextureFactory` gray-brown diffuse, regardless of the mesh's
  real shape, so the wrong silhouette still got the right-looking
  rock coloring.

## Scope

1. **Remove `"Models/rrocket"` from the `meshAssets` array**
   (`Game1.cs:921`) — the asteroid variety pool should only ever
   contain genuine rock/boulder-shaped models.
2. **Investigate whether a real 3rd native rock-type asset exists** to
   restore 3-way visual variety (don't just assume none does — search
   thoroughly): check the native `Artwork` corpus for other
   asteroid/rock-shaped `.mdl` files beyond the two already confirmed
   good (`drgrock`, `tacrock`) — the earlier filename-substring search
   for "rock" only found these 3 (including the wrong one), but a
   genuine rock asset might exist under a different naming convention
   (check native `asteroidigc.cpp`'s `m_asteroidDef.modelName` usage
   and any real per-asteroid-type model name references in the mission
   corpus, e.g. `m2xspiderw.igc`, for what native asteroid model names
   actually look like). If a real replacement is found: render it in
   Blender first to CONFIRM it's actually rock-shaped (the same check
   that caught this bug — don't trust a name alone) before wiring it
   in via the same `mdl_parse.py`→`decode_texture.py` (or `glb_to_fbx.py`
   if it needs Meshy regeneration first) → FBX pipeline this project
   already established, and add it to `meshAssets`. If no good
   candidate is found, ship correctly with the 2 confirmed-genuine
   variants (`drgrock`/`tacrock`) — a real 2-way pool is correct,
   a fake 3rd variant is not.
3. **Verify Domingo's "felt like it floated freely instead of being
   fixed and rotating" report** — check whether this is a real,
   separate physics/behavior difference for asteroids using this
   specific mesh, or (more likely, given asteroids are server-side
   static — confirm this directly in
   `Sector.ResolveShipAsteroidCollisions`, `isStatic: true` for every
   asteroid collider) just a perceptual effect from the unfamiliar
   asymmetric rocket-shaped silhouette's rotation looking different
   from a normal round boulder's rotation. State plainly which it
   turned out to be, with evidence — don't just assume the innocent
   explanation without checking.

## Explicitly out of scope

- Any other asteroid rendering code (texture factory, procedural rock
  path, mesh-loading mechanics themselves) — this is a content-pool
  fix, not a rendering-pipeline change.
- P354/P355/P356 — unrelated, already landed, don't touch.
- Removing or renaming the actual `rrocket.mdl`/`rrocket.fbx` files
  themselves — they may be legitimately used elsewhere (it's a real
  rocket/missile asset, potentially with a real other purpose in this
  port); this task only removes it from the ASTEROID pool, it doesn't
  delete the asset.

## Verification — real proof, not just build/test

- **Live proof, required**: render the asteroid field through the real
  content pipeline (same method used to discover this bug — direct
  Blender renders of the FBX, or an in-game capture) and confirm no
  ship/rocket-shaped silhouette appears among asteroids anymore, only
  genuine rock shapes.
- If a 3rd variant was added: confirm it too, same method, plus
  confirm real visual variety still exists (not just 1 shape repeated).
- Confirm the static/dynamic behavior question from scope item 3 with
  actual evidence (a code citation and/or a live positional check
  across several ticks), not assumption either way.
- Whole-solution build: 0 warnings, 0 errors.
- Full test suite (`/home/domingo/.dotnet/dotnet test`), run at least
  2x: only the pre-existing documented live-wire timing flakes are
  expected.

## Deliverables

1. The `meshAssets` fix (and, if applicable, the new 3rd variant's
   asset files), committed on `master` (not pushed — Domingo pushes).
   Do not create a side branch.
2. Report doc `P357_ASTEROID_ROCKET_FIX.md` in the repo root: the
   confirmed root cause (already established above, just document the
   verification), whether a 3rd variant was found/added or the pool
   correctly stays at 2, and the answer (with evidence) to the
   static-vs-floating question.

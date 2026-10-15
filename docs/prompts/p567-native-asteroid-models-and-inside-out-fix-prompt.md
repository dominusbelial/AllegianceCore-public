# P567 — asteroids: some rocks look inside-out (back faces drawn), and the port ignores each asteroid's native model; render every asteroid from its own native .mdl + texture (via P566's native mesh pipeline) with correct winding/culling

## Why this task exists

**Domingo (2026-10-06):** "we need a new pass on the asteroids, some look like
you are looking them from inside".

**Findings (Claude):**
- `Game1.Lifecycle.cs:~284-311` builds 4 rock renderers from **3 hard-coded
  imported FBX meshes**, `["Models/drgrock", "Models/tacrock", "Models/bgrnd05"]`
  (`ImportedRockMeshLoader.FromModel`), plus the procedural `RockMeshFactory`
  fallback; each asteroid picks a variant (P253/P357 history).
- `Rendering/AsteroidRenderer.cs` draws with **back-face culling on**
  (`RasterizerState.CullCounterClockwise`, `:~139`, `:~172`), justified for the
  procedural mesh's controlled winding. But the **imported FBX rocks don't
  share that winding guarantee** (ships use `CullNone` for the same FBX reason).
  An imported variant with reversed winding renders its inside: the
  "inside-out" look.
- **Native gives each asteroid its own model and texture** in the map data:
  `AsteroidDef.modelName/textureName` (`Igc/igc.h:1709-1720`), loaded in
  `Igc/asteroidigc.cpp:~33-34`. The 221 map `m2xspiderw.igc` asks for
  **`bgrnd05` ×65, `bgrnd03` ×60, `bgrnd56` ×9, `heliumrock` ×9** (Claude's
  scan of the asteroid records). The port ignores these (only `bgrnd05` is among
  its imports).
- **P566** (runs before this) builds the native `.mdl` mesh + texture pipeline
  for ships (winding correction; `MdlFile`, `MdlTextureDecoder`). Reuse it.

## Scope

1. **Native per-asteroid models:** each asteroid renders its own
   `AsteroidDef.modelName` with its `textureName` (resolve exactly as native:
   `+ "bmp"`, namespace-imported textures), decoded via P566's pipeline, cached
   per model, with **corrected winding** and a culling mode that's right for
   native rocks (or `CullNone` if native draws them double-sided; check).
   Keep per-instance radius scale, orientation and spin (`AsteroidSpin`,
   native rotation rules), and the P529/P530 lighting, mipmaps and render
   state.
2. **Inside-out root cause:** confirm which of today's imported variants has
   reversed winding (render each with culling on/off; check face normals vs
   the winding). Fix the imported path too, since it stays as the `meshy`/
   fallback path behind P566's setting.
3. **Asteroid types:** keep the P529 per-capability tint (helium, uranium,
   silicon, carbonaceous, …) only where native does it. If native's look
   comes from the texture alone, follow native.
4. Fallback chain: native model → imported variant → procedural (logged).

## Explicitly out of scope

- Mining/asteroid gameplay, asteroid counts and placement.

## Proof

- Failing-first tests: asteroid → native model/texture resolution from the map
  record; winding of a known face outward; the fallback chain.
- Contact sheet: each native rock model (`bgrnd05`, `bgrnd03`, `bgrnd56`,
  `heliumrock`, plus any others in the corpus maps) from 4 angles, old vs new;
  plus the old imported variants with culling on/off showing which one was
  inside-out.
- **Real client screenshots** (window): Alderan's asteroid field close up and
  from 1 km.
- Report `docs/reports/P567_NATIVE_ASTEROIDS.md`.

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

- **Real-window proof tooling:** use P572's `scripts/live-proof.sh` and the client
  `--auto-side/--auto-ready/--auto-launch/--auto-goto/--autoshots` flags
  (`docs/LIVE_PROOF.md`) to take unattended real-window screenshots (with MCP bots
  where needed). "Couldn't automate the window" is no longer an acceptable gap.

## ⚠️ Session rule: never end a turn while waiting

This session runs in one-shot mode: **your session ends the moment you finish a message.** Run long commands (the 3× `scripts/run-tests.sh` suite, live proofs, builds) **in the foreground**, blocking, one after another. **Never** start them in the background and then reply "waiting for it to finish". Two earlier sessions (P529, P542) did exactly that: the session ended mid-suite, the background processes were killed, and the suite results were never recorded. If a command might exceed a tool timeout, run each suite run as its own foreground call and record its numbers before starting the next.

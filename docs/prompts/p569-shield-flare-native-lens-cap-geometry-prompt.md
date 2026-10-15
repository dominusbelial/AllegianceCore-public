# P569 — shield-hit flare is a flat quad; native wraps it on a curved lens cap (lens30/60/90) oriented to the hit and scaled by the shield ellipse

## Why this task exists

**Domingo (2026-10-06, live on 221):** liked the "nice sphere effect", then: "ah
the sphere, i just noticed is actually flat thing".

**Port (P545):** `Rendering/ShieldFlareRenderer.cs` draws the animated `fx09bmp`
flare as **a patch tangent to the shield at the hit**, i.e. a flat quad; side-on
it reads as a flat card.

**Native:**
- `WinTrek/trekigc.cpp:2517-2523`: `GetNameSpace(AWF_SHIELD_FLARE = "lens")`
  → `m_pgeoFlares[0..2] = lens30 / lens60 / lens90` (curved spherical-cap
  geometries); `m_pimageFlare` = animated `fx09bmp`
  (`AWF_SHIELD_FLARE_TEXTURE`, `:2512-2514`).
- `trekigc.cpp:1624-1645` `AddFlare(time, position, id, ellipse)`: cap
  `trekClient.m_pgeoFlares[id]` textured with the animated flare, capped at
  `GetFlareCount() < 4`. `Igc/shieldIGC.cpp:170-171` calls it with **id 0**
  (lens30) and the ship's `GetHitTest()->GetEllipseEquation()`.
- `effect/thinggeo.cpp:1243-1300` `ThingGeo::AddFlare`: transforms the hit
  direction to local space; with an ellipse, divides the direction by the
  ellipse axes, `orient.Set(vector)` and **`orient.Scale(ellipse / m_radius)`**,
  so the cap **follows the shield ellipsoid**; then
  `Matrix(orient, 0, m_radiusMesh)` → a `TimedGeo(TransformGeo(CopyModeGeo …))`
  for the flare's duration. Read the rest (blend/copy mode, duration, fade).
- Art: `~/Development/Artwork/lens.mdl` (namespace with `lens30/60/90`) plus
  `lens30.x`/`lens60.x`/`lens90.x`.

## Scope

1. Load the native `lens` caps (parse `lens.mdl` with the port's `MdlFile`
   pipeline (P566 extends it); if `lens.mdl` doesn't expose the members, use the
   `.x` meshes, with a loader cited).
2. Render each flare as native: the `id` cap (0 = lens30 for shield hits),
   oriented and scaled per `ThingGeo::AddFlare` with the ship's shield ellipse
   (the port's hit-test ellipse / hull extents; cite where it comes from),
   textured with animated `fx09bmp`, the native blend/copy mode, the native
   duration/fade, ≤ 4 per ship.
3. Render-state hygiene: pin sampler/blend/depth/raster (P568 lesson).

## Explicitly out of scope

- The explosion shock wave (`fx18`): native's `ShockwaveData` (`effect/explosion.cpp`,
  forward/right) is a **flat oriented ring** too, so flat is faithful there.
- Other effects (P561).

## Proof

- Failing-first tests: cap orientation/scale for a hit direction and an
  ellipse (pure math vs native formulas); flare cap count ≤ 4.
- **Real window screenshots** (and/or P565 inspector shots, if it has landed):
  a shielded ship taking hits, from the side and front, old (flat) vs new
  (wrapped cap).
- Report `docs/reports/P569_SHIELD_FLARE_LENS.md`.

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
- **Visual changes: the proof must include screenshots from the REAL client window**
  (the backbuffer, e.g. via `ALLEGIANCE_AUTOSHOT`, window 2560×1440). `--capture` /
  offscreen renders alone don't count (they hid the P547 and P562 regressions).
- Committed on **`master`**, not pushed. Check with `git branch --show-current`.

- **Real-window proof tooling:** use P572's `scripts/live-proof.sh` and the client
  `--auto-side/--auto-ready/--auto-launch/--auto-goto/--autoshots` flags
  (`docs/LIVE_PROOF.md`) to take unattended real-window screenshots (with MCP bots
  where needed). "Couldn't automate the window" is no longer an acceptable gap.

## ⚠️ Session rule: never end a turn while waiting

This session runs in one-shot mode: **your session ends the moment you finish a message.** Run long commands (the 3× `scripts/run-tests.sh` suite, live proofs, builds) **in the foreground**, blocking, one after another. **Never** start them in the background and then reply "waiting for it to finish". Two earlier sessions (P529, P542) did exactly that: the session ended mid-suite, the background processes were killed, and the suite results were never recorded. If a command might exceed a tool timeout, run each suite run as its own foreground call and record its numbers before starting the next.

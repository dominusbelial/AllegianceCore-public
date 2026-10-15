# P559 — URGENT: P547's soft-particle depth pass makes all solid geometry invisible in the real client (ships, alephs, stations gone; only lights/particles/HUD remain)

## Why this task exists

**Domingo, live on 221 (client `762dfe5e`, `--window 2560x1440`):** "ships are
invisible… i cant see anything… only lights… alephs are not visible, neither any
base". Relaunching with **`--soft off`** brought everything back ("yes is back"),
which confirms P547 (`a5ab6e4e`) soft particles as the cause.

**Root causes (found by Claude, verify them):**
1. **The depth pass runs mid-frame, after the opaque scene was drawn**
   (`src/Allegiance.ClientCore.Game/Game1.Rendering.cs:~1153-1167`: after the
   treasure draw, `SetRenderTarget(_post.DepthRT)` → `DrawSceneDepth` →
   `SetRenderTarget(bloom ? SceneRT : (capThis ? _capTarget : null))`). With
   MonoGame's default backbuffer `RenderTargetUsage.DiscardContents`, **switching
   away from the backbuffer and back discards its contents**: everything drawn
   before the switch (ships, asteroids, stations, alephs) is lost; only what's
   drawn after (particles, lights, HUD) survives. Exactly the symptom.
2. **`ShipRenderer.DrawDepth` permanently replaces each ship model's effect**
   (`Rendering/ShipRenderer.cs:~208-232`, `foreach (var part in mesh.MeshParts)
   part.Effect = depthEffect;`), never restoring the original BasicEffects, so
   the normal ship draw then runs with the wrong effect. Check
   `AsteroidRenderer.DrawDepth` / `StationRenderer.DrawDepth` for the same
   pattern.

**Why P547's proof missed it:** its headless proof renders into the capture
render target (`_capTarget`, offscreen) rather than the backbuffer, and its
"live sanity" only checked no exceptions and the HUD (which is drawn last).

## Scope

1. **Restructure the frame:** do the linear-depth pass **before** the color
   pass (render depth first, then set the color target once and draw the
   opaque scene, then particles sampling the depth target). Don't switch away
   from the color target mid-frame. Alternatively (second choice), ensure every
   target the opaque scene draws into uses `RenderTargetUsage.PreserveContents`.
   Explain the choice. Handle the bloom path (`SceneRT`) and the capture path
   (`_capTarget`) consistently.
2. **Never mutate model effects for the depth pass:** save and restore each
   part's original effect, or (better) draw depth with the depth effect without
   reassigning `part.Effect` (apply the effect and draw the mesh part buffers
   directly). Same for asteroid/station renderers if affected.
3. Keep `--soft off`, the Native/Enhanced setting and bloom working.

## Explicitly out of scope

- The visual design of soft particles/bloom (P546/P547).

## Proof (must exercise the REAL backbuffer path)

- **Failing-first test** that renders a frame through the **backbuffer
  path** (not `_capTarget`): e.g. a test harness that runs `Draw` with soft
  particles ON, then reads the backbuffer (`GraphicsDevice.GetBackBufferData`)
  and asserts a known ship/station/asteroid region is non-background. It fails
  on today's code (geometry gone), passes after. Also a test that a ship
  model's part effects are unchanged after a depth pass.
- **Live check on the real client** (local server + bots): soft particles ON,
  window 2560×1440. Take an actual **screen/window screenshot** (not the
  capture target) showing ships, an aleph and a station visible, with smoke
  near a hull. Repeat with bloom on, and with `--soft off`.
- Whole-solution build, 0 warnings and 0 errors; full suite as below.
- Report `docs/reports/P559_SOFT_PARTICLE_GEOMETRY_FIX.md`, plus a dated
  correction note in P547's report.

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

## ⚠️ Session rule: never end a turn while waiting

This session runs in one-shot mode: **your session ends the moment you finish a message.** Run long commands (the 3× `scripts/run-tests.sh` suite, live proofs, builds) **in the foreground**, blocking, one after another. **Never** start them in the background and then reply "waiting for it to finish". Two earlier sessions (P529, P542) did exactly that: the session ended mid-suite, the background processes were killed, and the suite results were never recorded. If a command might exceed a tool timeout, run each suite run as its own foreground call and record its numbers before starting the next.

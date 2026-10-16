# P582 — URGENT before Volumetric is the desktop default: (1) with `--fx volumetric` the WORLD goes black once explosions start (asteroids/stations vanish, only ships/effects/alephs/HUD remain); (2) `--fx displaced` explosions erode away far too fast; (3) volumetric timing out of sync

## Why this task exists

Domingo recorded both live on 221 (server + client at **P579 `04f63b8f`**,
2026-10-07):

1. **Volumetric (raymarch A): the world goes black.** Video
   `~/Videos/2026-10-07 20-34-20.mp4` (3:55): for the first ~30 s asteroids
   are visible (Ganymede, home); after explosions begin, **asteroids and the
   station disappear for the rest of the session**, while ships, beams,
   alephs, effects and the HUD still render. "Everything goes black and only
   the ships and the explosion effect is visible."
   - `VolumetricRenderer.Draw` **does** restore blend/raster/depth after
     drawing (`VolumetricRenderer.cs:~287-290`), so it's not leaked
     additive blending.
   - It does **not** unbind the textures/samplers it sets, notably the
     **soft-particle linear-depth render target** bound as `DepthTexture`
     (and the 1×1 "far" texture from `1bae5d0e`). **Prime suspect:** next
     frame the linear-depth pass renders INTO that render target while it is
     still bound as a texture (an OpenGL feedback loop / incomplete FBO in
     MonoGame DesktopGL), breaking the depth pass. P547/P559 already showed a
     broken depth pass can hide world geometry. Ships/effects use other paths.
   - Prove the cause before fixing: e.g. `--soft off` (does the world stay?),
     GL error checks, and a frame where you unbind `gd.Textures[n]` /
     reset `SamplerStates` after the volumetric draw. Also check the
     displaced renderer and every other renderer that binds the depth target
     (SoftParticleRenderer, EnhancedExplosionRenderer's soft path).
   - Fix at the root: after any pass that samples the depth render target,
     **unbind it** (set the texture slots it used to null) and restore the
     samplers it changed. Ideally add one shared helper that each
     depth-sampling renderer calls, plus a debug assert that the depth RT is
     not bound as a texture when it's set as a render target.
2. **Displaced (B): the effect disappears too fast.** Video
   `~/Videos/2026-10-07 20-29-39.mp4`. Life is right (native film 25 f @ 20
   fps = 1.25 s + `SmokeTailSeconds`, ≈ 3.4–4.0 s total,
   `VolumetricExplosionSampler.cs:~70-95`), but erosion is **linear from
   birth**: `ClipThreshold = age/life·1.25 − 0.15`
   (`DisplacedSphereRenderer.cs:~131-133`), so most of the surface (mid-range
   displacement) is clipped by ~40–50% of life (~1.5 s). **Fix:** hold the full
   fireball through the film phase (≈ the first 1.25 s / the share native's
   fireball frames are visible), then erode over the smoke tail with an
   eased curve (e.g. `smoothstep`), so the visible duration matches Enhanced's
   fireball + smoke at the same moment (measure it). Keep native
   timing/sizes/tints.

3. **Volumetric timing looks out of sync** (Domingo: "timing on those
   volumetric explosions was kind of out of sync too"). Measure, per
   technique (Enhanced / Volumetric / Displaced), on the same fighter death,
   from the server's `KILL_SHIP`/`ExplosionEvent` receive time: (a) when the
   victim's hull disappears, (b) the **first frame with visible fireball**
   (coverage above a threshold), (c) when the explosion sound starts (P574),
   (d) the peak; and the drift of the fireball centre vs where the ship died.
   Enhanced is the reference (it looked in sync). Likely causes to check:
   the sub-volume's growth curve starting near radius 0 (a slow ramp delays
   the first visible frame), the `ReceivedLocalMs` age base vs Enhanced's,
   the 0.25 s stagger for sub-volume 0 (native's first decal starts at
   t = 0), the velocity drift (native drifts too: keep it, but it must match
   **Domingo's description of the sequence:** "the ship exploded, then all
   turned black, then the animation we saw; maybe it's all too slow? This one,
   contrary to displaced, needs to happen faster." So: (i) the **blackout
   arrives at the moment of the first explosion** (it's issue 1 starting;
   confirm the trigger is the first volumetric draw, and whether there's
   also a frame-time spike/hitch on that first draw, e.g. first-use shader
   compile or render-target allocation: pre-warm it at load), and (ii) the
   volumetric fireball **plays too slowly**: compress its growth/cool so it
   peaks around native's film peak (fighter death: within ~0.3–0.5 s) and its
   total visible duration matches Enhanced's (measure both). Net: Displaced
   needs to last longer; Volumetric needs to happen faster. Both converge
   on Enhanced/native timing.
   Enhanced's). Fix so the first visible fireball lands within one frame of
   Enhanced's and the peak timing matches native's film; frame-strip proof.

## Proof (real window, P572 tooling + MCP bots)

- **2-minute fight with `--fx volumetric`** (soft particles ON, the default): real-window
  screenshots at 0:10, 0:30, 1:00, 1:30, 2:00 show asteroids and stations
  still visible, plus a pixel metric (e.g. the mean luminance of known rock
  regions, or rock visibility in the frame) that doesn't collapse after the first
  explosion. Same with `--fx displaced`.
- **Displaced timing:** frame strips at 0.25/0.5/1.0/1.5/2.0/3.0 s for a
  fighter death, Enhanced vs Displaced, with visible-coverage curves; the
  Displaced curve should track Enhanced's.
- Failing-first tests: the depth RT is unbound after each depth-sampling
  draw; the erosion curve holds ≥ 90% coverage through the film phase.
- Report `docs/reports/P582_VOLUMETRIC_BLACK_WORLD_AND_DISPLACED_EROSION.md`.

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

## ⚠️ Session rule: never end a turn while waiting

This session runs in one-shot mode: **your session ends the moment you finish a message.** Run long commands (the 3× `scripts/run-tests.sh` suite, live proofs, builds) **in the foreground**, blocking, one after another. **Never** start them in the background and then reply "waiting for it to finish". Two earlier sessions (P529, P542) did exactly that: the session ended mid-suite, the background processes were killed, and the suite results were never recorded. If a command might exceed a tool timeout, run each suite run as its own foreground call and record its numbers before starting the next.

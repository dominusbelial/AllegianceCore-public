# P582b — FIX the volumetric black world (P579 half-res render-target switch wipes the backbuffer), then volumetric TIMING: the fireball plays too slowly and lands out of sync with the ship's death (measure vs Enhanced, compress growth/peak/cool to native film timing, pre-warm the first draw)

## Why this task exists

Follow-up to **P582** (black world + displaced erosion; read its report
first: `docs/reports/P582_VOLUMETRIC_BLACK_WORLD_AND_DISPLACED_EROSION.md`).
These timing notes reached P582's prompt after it had launched, so they are
this task. Build on P582's code and proof tooling.

## ⛔ TOP PRIORITY (found by Claude 2026-10-07 22:10, after P582): the black world is P579's half-res pass, NOT (only) the depth-RT feedback loop

Domingo watched P582's own live proof windows twice: **"it still happens"**,
"same deal" (after `e6e3e407`'s depth unbind). P582's camera faced empty
space, so its proof couldn't see it.

**Cause:** `VolumetricRenderer.cs:~265-285` (added by **P579**): when any
near+large volume exists, it does `gd.SetRenderTarget(halfRes)` … draw …
`gd.SetRenderTarget(null)` **mid-frame**, then composites with a SpriteBatch.
MonoGame's backbuffer is `RenderTargetUsage.DiscardContents`, so switching
away and back **wipes the opaque scene already drawn** (sky, asteroids,
stations); only what's drawn after survives (ships drawn later, effects, HUD).
**This is exactly the P547 bug that P559 fixed** (`Game1.Rendering.cs:~505-516`,
"depth pass BEFORE the opaque color pass … never switched away from"). It also
explains why it starts at the first *near* explosion, why the P573 spike (no
half-res) didn't black out, and why ships survive. Also: restoring `null`
is wrong whenever the frame renders into `_post.SceneRT` (bloom) or
`_capTarget` (capture).

**Fix (do this first):** keep the colour target fixed for the whole frame
after the opaque pass starts. Either (a) render the half-res volume pass
**before** the opaque colour pass (next to the P559 depth pass, using that
frame's depth RT for occlusion) and composite it at the explosion stage, or
(b) restore the **actual previous** render target (`GetRenderTargets()`) and
make every colour target used mid-frame `PreserveContents` (backbuffer via
`PreparingDeviceSettings`, `SceneRT`, `_capTarget`). Prefer (a); justify the
choice. Add a debug guard (like P582's feedback-loop trip-wire) that fires if
the render target changes between the start of the opaque colour pass and
the HUD, and a failing-first test of that rule where feasible.

**Live confirmation (22:15):** Claude applied fix (b) as a LOCAL, uncommitted
patch in Domingo's play client (`~/allegiance-play2` @ `04f63b8f`):
`PreparingDeviceSettings` → `PresentationParameters.RenderTargetUsage =
PreserveContents`, plus `VolumetricRenderer` restoring
`gd.GetRenderTargets()` instead of `null`. **With it, Domingo sees the
explosions and the world in a live 15v15 on 221.** That confirms the cause. You
may adopt (b) (it's proven live) or do (a); either way, keep the debug guard
and the rocks-and-station-in-view proof.

**Proof that actually sees it:** real window, `--fx volumetric`, soft ON,
camera with **asteroids AND a station in view** (home sector, chase view),
a fighter death injected **close** (≤ 150 u, so the half-res path runs);
shots before/during/after, plus a pixel metric over the rock/station regions
that must not collapse; repeat with bloom ON. Then do item 0 below.

## Scope

0. **First: complete P582's real-window proof if its report lacks it.** As
   of 22:00 P582's report had `## 3. Proof` = "See §5" and `## 7. Honest
   caveats` = "TBD", and the session had only *read* `scripts/live-proof.sh`
   (no `P582_assets/`). If the final report still has no real-window
   evidence: run a **2-minute bot fight in the real window** (P572
   `live-proof.sh` + MCP `spawn_scenario`) with **soft particles ON**, once
   with `--fx volumetric` and once with `--fx displaced`; take real-backbuffer
   shots at 0:10/0:30/1:00/1:30/2:00, and a metric that asteroid/station
   pixels stay visible after the first explosions (e.g. the mean luminance of
   known rock regions, or a count of rock-coloured pixels per frame). Also
   check that Enhanced soft smoke alone (`--fx enhanced`) no longer leaves
   the depth RT bound (P582 found `SoftParticleRenderer` leaked it too). Add
   the results to P582's report as a dated addendum, then continue with the
   timing work.
1. **Volumetric timing looks out of sync** (Domingo: "timing on those
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


2. If P582 did not already, **pre-warm** the volumetric (and displaced)
   shaders and render targets at load (draw one invisible volume, or force
   compile), so the first real explosion causes no hitch.

## Proof

Frame strips (real window, P572 tooling) for a fighter death at 300 u,
Enhanced vs Volumetric vs Displaced, at 0.05/0.1/0.25/0.5/1.0/2.0 s after the
kill, plus a table of (a)–(d) per technique; frame-time trace around the first
explosion of a session (no hitch). Failing-first tests for the new growth
curve (peak time, total duration vs native film). Report
`docs/reports/P582b_VOLUMETRIC_TIMING.md`.


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

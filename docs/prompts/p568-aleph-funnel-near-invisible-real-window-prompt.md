# P568 — P562 regression: in the real client window the new aleph shows only a tiny bright point (the star core); the textured double-horn funnel is effectively invisible. Fix it and prove it in the REAL WINDOW

## Why this task exists

**Domingo, live on 221 with client `dec7e510` (P562), 2026-10-06:** "cant see
any aleph" … "nothing" (also with `--soft off`, so it isn't the P559 depth pass)
… "i think i saw the aleph but it was very tiny". The previous build
(`b8fb5099`, pre-P562) shows the old double-horn aleph normally.

**P562's proof never exercised the real window:** its "real client screenshot"
ran the client in **`--capture` mode** (offscreen capture target, 1280×720,
the overview camera; `docs/reports/P562_ALEPH_VORTEX.md` §"Real client
screenshot"). The same gap hid P547's bug (P559).

**Confirmed by Domingo:** what he sees is "some kind of mini star", i.e. the `f02bmp`
star core at the pinch; **the funnel itself isn't visible at all**.

**Leading hypothesis (Claude, verify it):** the funnel is drawn too dim to see.
P562 replaced the old deliberately-brightened vertex colours (P341: rims
"brightened so the funnel's outer extent survives the additive blend") with
native's `2r(1−r)` brightness (**max 0.5**) × the `alephbmp` texture, additive
(`Rendering/WarpRenderer.cs` `Draw`: `BlendState = _additive`,
`DepthStencilState.DepthRead`, cull none; `FunnelTexture` →
`Alephbmp.mdl`, case-insensitive). On a dark texture that contributes almost
nothing, so only the bright `f02bmp` star core at the pinch is visible ("very
tiny"). Other candidates to rule in or out with measurements: LOD tier
selection (wrong tier at gameplay distances), scale (`VisualScaleMultiplier`
9.5 × radius applied once?), UV range/wrap, the texture decoding to near-black
(RGB565 → premultiplied?), or the depth test (`DepthRead` vs the starfield or
backdrop writing depth).

**Native reference:** `effect/alephgeo.cpp` (P562's citations): check exactly how
native combines the per-vertex `VertexL` colour with the texture under
`BlendModeAdd` (`engine` `Context`/`device3d` texture-stage setup: is it
`texture × vertex colour`, or `texture × 2×vertex`, or `vertex + texture`?). The
native aleph looks bright and visible from afar; reproduce that, not just
the formula.

## Scope

1. **Diagnose in the real window:** log per aleph per frame (debug flag): drawn,
   world rim radius, camera distance, LOD tier, texture size and mean
   luminance, and the final average contribution. Take real window screenshots
   (`ALLEGIANCE_AUTOSHOT`, window 2560×1440) at the Rigel aleph from 3 km, 1 km
   and 200 u.
2. **Fix** so the funnel is clearly visible and reads as the flowing vortex at
   gameplay distances, matching native's texture/vertex-colour combine; keep
   the native UV flow, the star core and the swell. If a brightness factor is
   needed beyond native's formula, justify it from native's texture-stage
   math (not by eye).
3. **Standing rule:** the visual proof must come from the **real window**
   (backbuffer), not `--capture`/offscreen alone.

## Explicitly out of scope

- Redesigning the aleph (P562's design stays).

## Proof

- A failing-first check that measures the funnel's on-screen contribution
  (e.g. a backbuffer read in a windowed test harness, like P559's
  `GetBackBufferData` test): today ≈ only the core lit; after, the funnel area
  lit above a threshold.
- **Real window screenshots** before/after at the 3 distances, plus a short
  frame strip showing the flow, at 2560×1440.
- Report `docs/reports/P568_ALEPH_VISIBILITY.md`, plus a dated correction note in
  P562's report.

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

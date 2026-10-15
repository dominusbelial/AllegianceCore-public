# P562 — aleph art pass: give the port's double-horn (hourglass) aleph native's animated vortex look — the swirling, inward-flowing alephbmp texture, the bright ring band, the counter-rotating star core and the transit swell

## Why this task exists

**Domingo (2026-10-06):** "can we have a pass on the aleph art? we have the
double horn design but the original still looks better, looks like water
flushing down the toilet, way more realistic than ours … we can keep the
double horn design".

**Port today** (`src/Allegiance.ClientCore.Game/Rendering/WarpRenderer.cs`,
`WarpMeshFactory.cs`, `WarpCoreMeshFactory.cs`; P242/P258): a procedural
hourglass with **per-vertex colours only, no texture**, additive, cull none,
and the "swirl" is a **rigid rotation of the whole mesh** about its axis.
A rotating solid reads as spinning, not flowing.

**Native** (`~/Development/Allegiance/src/effect/alephgeo.cpp`, 251 lines; read it
all; hooked up in `WinTrek/trekigc.cpp:1900-1925` `LoadAleph`):
- **Texture:** `LoadImage(textureName + "bmp")`, where the warp's
  `WarpDef.textureName` is `"aleph"` for all 46 warps in `m2xspiderw.igc` →
  **`alephbmp`** (`~/Development/Artwork/Alephbmp.mdl`; decode through the
  existing `NativeEffectTextures`/`MdlTextureDecoder` path; use the per-warp
  texture name, not a hard-coded one).
- **Mesh:** a disc of `countDepth` rings × `countSection` sections (LOD
  8×32 → 4×16 @64 → 2×16 @32 → 2×8 @16, `CreateAlephGeo :239-251`); vertex at
  radius `r = depth/countDepth`, `z = (1−r)(r−1)` (a funnel); **vertex
  brightness `2r(1−r)`** (dark centre and rim, bright mid band) (`:~70-95`).
- **Animated UVs every frame** (`UpdateTextureCoordinates :~110-125`):
  `t = mod(time/10, 1)`; per ring `v = t + r` (the texture flows inward over
  time) and `u = 4·section/countSection + 5·(1 − (1−r)²)` (spiral twist,
  increasing toward the rim). **This is the "water draining" look.**
- **Render state:** additive blend, Gouraud, cull none, **no Z-write**
  (`RenderCallback :~215-230`).
- **Star core:** two decals of `f02bmp` (`AWF_EFFECT_ALEPH_STAR`,
  `effect/efart.h:5`) at the centre, white, scale 0.1, rotating at
  **+0.25 rad/s and −0.375 rad/s** (counter-rotating) (`Render :~160-210`).
- **Transit swell:** on the aleph event (`OnEvent`, fired when a ship
  transits; find the trigger in native) the star swells up to 6× with
  **8 pulses** over the event duration, then decays over 0.25 s (`:~165-190`).

## Scope (keep the double-horn shape)

1. **Texture the hourglass with native's flowing vortex:** apply
   `alephbmp` (per-warp `textureName + "bmp"`) to **each horn** as native maps
   its funnel: per horn, a radial parameter `r` from the pinch (0) to the rim (1)
   (or the inverse, whichever matches native's dark-centre/bright-band/dark-rim
   with the bright point at the pinch, as P258 intends), native's
   `u`/`v` formulas animated per frame (inward flow + spiral twist), and native's
   `2r(1−r)` brightness band. Native additive state, no Z-write, cull none.
   **Stop the rigid whole-mesh rotation** (or reduce it to a subtle drift):
   the flow comes from the UVs, as in native.
2. **Star core at the pinch:** two counter-rotating `f02bmp` decals at native
   rates, scaled to the aleph radius as native's 0.1 relates to its unit
   funnel.
3. **Transit swell:** when any ship transits this aleph (the client sees the
   transit: the own ship's SET_CLUSTER and other ships' sector changes),
   play native's star swell (8 pulses, ×6, 0.25 s decay).
4. **LOD** by on-screen size like native's tiers.
5. Optional, only if cheap: the P547 bloom/soft paths should treat the aleph
   like other additive effects (no clipping regressions).

## Explicitly out of scope

- The warp-transit flash/particles (P545 `e_telebmp`), sounds, map icons.
- Changing aleph size, position or transit radius.

## Proof

- Failing-first unit tests: UV formulas match native at several (r, section,
  time) samples; brightness band; star rotation rates; swell curve.
- **Contact sheet:** an aleph at 3 distances × 3 angles (head-on, 45°, side-on),
  old vs new, plus an **animation strip** (8 frames over 2 s) showing the
  inward flow, and the transit swell strip.
- **Real client screenshot** (window, not offscreen), an aleph in Alderan, and
  a short frame sequence from the client.
- Report `docs/reports/P562_ALEPH_VORTEX.md`.

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

# P579 — adopt the P573 volumetric explosions into `master`: port the spike + its two live fixes, calibrate brightness/density in the REAL window, rework LOD for fill rate (3–4 fps up close today), clip to the sphere (no square edges), behind `Effects: Native | Enhanced | Volumetric`

## Why this task exists

**P573** (spike, branch `spike/p573-volumetric`, worktree
`~/Development/AllegianceCore-p573`, commits `15847ffa` + **`1bae5d0e`**; report
`docs/reports/P573_VOLUMETRIC_EXPLOSIONS_SPIKE.md` on that branch) recommended
Volumetric A (raymarch) on desktop, baked C on Android, and B (displaced
sphere) as `--fx displaced`. **Its proof was offscreen only, and its numbers came
from llvmpipe.** Domingo flew it on 221 (2026-10-07) and **saw no explosions**.
Claude debugged it live:
1. **Positions not camera-relative.** The game's view matrix keeps the eye at
   the origin; every renderer subtracts `cameraEye`
   (`EnhancedExplosionRenderer.cs:120-122`, `ExplosionRenderer.cs:105-107`).
   Both P573 renderers used absolute positions → every explosion offset by
   the camera position. **Fixed in `1bae5d0e`.**
2. **Depth fade ~2000× too weak.** `Volumetric.fx` `DepthFade` used `* (1/8)`;
   `SoftParticle.fx` uses `SoftnessInv = 1/0.004 = 250`
   (`LinearDepth.SoftnessNormalized`). And with `--soft off` the unbound depth
   sampler read 0 → fade 0. **Fixed in `1bae5d0e`** (slope 250 + a 1×1 white
   "far" depth texture).
3. **Too faint at game scale.** With a diagnostic shader (R = fade,
   G = march×10), the real window showed **red quads with yellow multi-lobe
   blobs**: geometry, placement, timing and native sub-explosion staging are
   right, but the march output is **~10× too faint** to see additively over
   space. Video: `~/Videos/2026-10-07 16-27-01.mp4` (last half). Live
   per-second diagnostics then: ship deaths r=35 at 97–980 u = 110–1119 px,
   LOD buckets Med/High/Ultra.
4. **LOD inverted for fill rate: 3–4 fps up close.** `VolumeLod.ForPixelDiameter`
   gives **more** steps to **bigger** on-screen volumes (Ultra = nested, 255
   steps), over ~1 M pixels per bubble, ×N bubbles.
5. **Square edges.** The quad is drawn fully; pixels outside the sphere
   still cost and showed as squares in the diagnostic.

## Scope

1. **Port into `master`:** cherry-pick or re-apply P573's game code
   (`Shaders/{Volumetric,DisplacedSphere}.fx` + compiled `.ogl.mgfxo`,
   `Rendering/{VolumetricRenderer,DisplacedSphereRenderer,IcosphereGenerator}.cs`,
   `State/{VolumetricExplosionSampler,VolumetricQuality,EffectsQuality}.cs`,
   the `--fx volumetric|displaced` parsing, the tier resolver + budget
   fallback, tests) **including `1bae5d0e`**. Rebase onto current `master` (P561 smoke,
   P564 lighting, P569, P570/P572/P574 changes). Don't merge the branch wholesale.
2. **Calibrate brightness/density in the real window:** a fighter death
   (type 3) at 150/600/1500 u must read clearly, as bright as or brighter than
   Enhanced's fireball at the same moment (measure the peak luminance and
   coverage in real-window screenshots vs `--fx enhanced`), while keeping
   P546's blown-out budget (dense-combat blown-out ≤ ~20%). Named constants
   with their rationale. Keep native timing/sizes/tints (`ExplosionPreset`).
3. **Fill-rate LOD:** invert the policy. Step count (and step length) goes
   by **screen coverage**: big-on-screen → fewer, longer steps, optionally
   half-resolution for near volumes; small → more steps is fine. A **per-frame
   pixel/step budget** across all volumes (degrade the nearest/largest first),
   plus the existing volume cap. **Target: ≥ 60 fps at 2560×1440** with (a) one
   fighter death at 100 u (fills the screen), (b) 6 overlapping deaths at 300 u,
   (c) a 15v15 brawl. Measure on this GPU (real window, not llvmpipe).
4. **No square edges:** compute the ray–sphere intersection first and
   `clip()` / early-out outside it (also saves fill). Tighten the quad to the
   sphere's projected bounds if cheap.
5. **Setting:** `Effects: Native | Enhanced | Volumetric` (+ technique
   `raymarch|displaced`). **Defaults (Domingo, 2026-10-07): desktop (DesktopGL)
   → Volumetric (raymarch A); Android/low-end → Enhanced.** Automatic fallback
   to Enhanced on shader failure or sustained frame time over budget, logged
   once. **Gate:** make Volumetric the desktop default only if the scope-3
   performance target is met (≥ 60 fps at 2560×1440 in scenarios a/b/c on this
   GPU). If it isn't, keep Enhanced as the default, ship Volumetric as an
   opt-in, and report exactly what's missing.
6. B (displaced) gets the same camera-relative check and the same proof.

## Proof (real window: P572 `scripts/live-proof.sh` / `--autoshots`)

- Side-by-side real-window screenshots **Enhanced | Volumetric A |
  Displaced B** for a fighter death at 3 distances, a station death, and 6
  overlapping deaths; frame strips 0.1/0.3/0.6/1.0/2.0 s.
- **Frame times** (ms/frame, 1% lows) for scenarios (a)(b)(c) on this GPU,
  before (spike + `1bae5d0e`) vs after.
- Failing-first tests: camera-relative placement (a volume at world P with
  the camera at C renders centred at P−C), the fade (slope + no-depth = 1), LOD
  monotonic in coverage (bigger → cheaper per pixel), budget degradation order.
- Report `docs/reports/P579_ADOPT_VOLUMETRIC_EXPLOSIONS.md`.

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

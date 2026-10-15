# P561 — VFX tuning pass on P546/P547: smoke and particles are oversized vs ships, too dense/opaque, block the external (chase) view, and look flat up close or at angles

## Why this task exists

**Domingo, live on 221 (client `762dfe5e`, Enhanced VFX, `--soft off` because of
P559):** "vfx still look kind of flatty once you get close or from certain
angles. also the smoke/particles are so heavy they block the external view, the
size of it does not match the size of the ships". (He likes the overall
improvement: "nice new vfx look better".)

**Measured in code (Claude):**
- **Oversized smoke:** Enhanced trail smoke half-size =
  `p.Size * 2.4 * (0.5 + 2.2 * (1 − rem))` (`Rendering/MissileTrailRenderer.cs:~91`,
  `Rendering/ShipEngineTrailRenderer.cs:~69`), **up to ~6.5× `p.Size`**, while the
  native path uses `p.Size * 0.5 * NativeParticleCurves.SmokeSizeScale(rem)`
  (native SmokeBehavior grows 0.75→3.0, `particlegeo.cpp:~51-54`), so **~4× native**.
  Check explosion smoke sizing (`EnhancedExplosionRenderer`, `EnhancedSmokeField`)
  against the native `AddExplosion` sizes too.
- **Dense/opaque:** `EnhancedSmokeField.PeakAlpha = 0.65`,
  `EnhancedExplosionRenderer.MaxSmokePerExplosion = 14`,
  `EnhancedSmokeField.MaxSmokeOnScreen = 320`, `LingerExtraSeconds = 1.2`.
- **Chase view blocked:** `ParticleNearFade.Flight = (6, 45)`
  (`Rendering/EnhancedVfx.cs:43`) only fades within 45 u of the **camera**. In the
  external/chase camera the own ship's engine/missile/damage smoke sits **between
  the camera and the own ship**, beyond that range, so it covers the ship.
- **Flat look:** each puff is one camera-facing quad with a **96–128 px** flipbook
  frame (P543 downscaled the Unity CC0 sources; `Content/Vfx/atlas/*.png`):
  blurry and card-like up close, hard planar cuts against hulls when soft
  particles are off (P559 will restore soft particles; build on it).

## ➕ UPDATED FOCUS (Domingo, 2026-10-06, after P559/P562/P568/P566): size first

"smoke now does not look as bad with some of the fixes, still you can reconsider
its size; **from far it kind of looks like lots of ovals**, so maybe a smaller size
would help."

**Priority order for this run (do 1–3 fully; 4–5 only if cheap):**
1. **Smaller smoke:** bring Enhanced smoke/fire sizes to native-derived values
   (≤ 1.5× native, scope 1 below) and re-check against the native ships (P566)
   and the size sheet.
2. **No "discrete ovals" from afar:** puffs must merge into a continuous plume
   at distance: smaller, **more numerous** puffs (keep total opacity budget),
   softer radial falloff on the sprite edges (no visible oval rim), random
   rotation, slight size jitter, and **distance-aware size/alpha** (far puffs
   smaller/fainter so a trail reads as a line, not beads). Check the CC0 atlas
   frames for hard elliptical edges, and feather them if needed.
3. **Own-ship visibility in the chase view** (scope 3), since it was a complaint
   too; keep it if still needed after 1–2 (measure).
4. Density/linger tuning (scope 2), as needed to keep the look after 1–2.
5. Higher-res atlases / two-layer smoke / multi-decal fireballs (scope 4):
   **optional this run.**
Proof additions: a **far-distance** real-window screenshot (1–2 km) of missile
and engine trails and a damaged ship, before/after, showing a continuous plume
instead of separate ovals; plus the size sheet.

## Scope (Enhanced path only; Native stays exactly native)

1. **Size parity with ships:** derive Enhanced smoke/fire sizes from the
   **native** size curves and emitter sizes (`NativeParticleCurves`, missile
   `0.25 + radius*0.1`, explosion preset radii), allowing at most a small,
   named, justified factor (≤ 1.5× native). Produce a **size sheet**: a smoke
   puff at birth, mid-life and end next to an Adv Fighter and a Scout at the
   same distance, plus explosion sizes vs the exploding hull.
2. **Density/opacity:** lower `PeakAlpha` (justify the value), fewer explosion
   puffs, shorter linger, thinner trail emission. Use the blown-out metric
   **and a new "own-ship visibility" metric** to pick values.
3. **External/chase camera readability:** fade (or cull) particles that are
   between the camera and the **own ship** and overlap its screen footprint
   (e.g. a screen-space ellipse around the own ship's projected bounds, depth
   nearer than the ship), and re-tune the distance fade **per camera mode**
   (cockpit vs chase/external; find the camera modes in the client). Own-ship
   smoke (engine, missiles just launched, damage smoke) should never hide the
   own ship in the chase view.
4. **Less flat:**
   - **higher-res atlases:** re-extract from the Unity CC0 source flipbooks
     at **256 px frames** (keep licences; check VRAM, aim ≤ ~32 MiB total for the
     Enhanced atlases; mipmaps on);
   - per-particle random rotation **plus slow spin over life**, random frame
     offset, slight size jitter;
   - **two-layer smoke** (a large soft layer + a smaller detail layer offset
     in depth) and **depth jitter** of puffs so a cloud reads as a volume from
     any angle;
   - fireballs: several decals at different depths/sizes instead of one card;
   - verify with soft particles ON (after P559) that hull intersections are soft.
5. Keep the Native/Enhanced switch, `--soft`, `--bloom` and the P546/P547 tests.

## Explicitly out of scope

- 3D volumetric rendering / raymarching; new shader effects beyond P547's.
- Projectile/impact art (P544/P545).

## Proof (real backbuffer and real camera modes, not only the offscreen capture)

- Failing-first tests: Enhanced smoke size ≤ 1.5× native at every life
  fraction; the chase-view occluder fade removes a particle placed between the
  camera and the own ship; the density caps.
- **Metrics, before vs after** (same scenes):
  - own-ship visibility % in the chase view during a 4-missile volley and with
    a damaged (smoking) own ship;
  - blown-out % for dense combat (must stay ≤ ~15%);
  - smoke screen coverage for the volley.
- **Screenshots from the real client** (window 2560×1440, soft ON): chase view
  volley at 0.2/0.5/1.0 s, a close-up of an explosion from 30 u and at a
  grazing angle, a smoking damaged ship next to undamaged ones (size match).
  Old vs new.
- Report `docs/reports/P561_VFX_TUNING.md`.

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

## ⚠️ Session rule: never end a turn while waiting

This session runs in one-shot mode: **your session ends the moment you finish a message.** Run long commands (the 3× `scripts/run-tests.sh` suite, live proofs, builds) **in the foreground**, blocking, one after another. **Never** start them in the background and then reply "waiting for it to finish". Two earlier sessions (P529, P542) did exactly that: the session ended mid-suite, the background processes were killed, and the suite results were never recorded. If a command might exceed a tool timeout, run each suite run as its own foreground call and record its numbers before starting the next.

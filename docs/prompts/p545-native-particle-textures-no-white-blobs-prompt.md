# P545 — replace the port's generated white soft-blob particles with native's real effect textures and colours (missile/engine smoke and fire, shield flares, hull hits, debris, strobes, glows): dense combat and missile launches turn into a white patch

## Why this task exists

**Domingo (2026-10-04, watching 15v15 combat on 221):**
- "on the places where projectiles are concentrated things kind of become a
  white large patch hiding the actual impacts"
- "missiles is the same white flying patch that kind of hides everything
  right when it's released, same deal, all white"

**Root cause (traced by Claude, verify it):** eight port renderers draw a
**procedurally generated white soft blob** (`GenerateSoftSprite`, e.g.
`HullHitSparkRenderer.cs:151-165` fills `new Color(1f, 1f, 1f, a)`),
mostly with **additive** blending. Stacked where fire concentrates, they sum
to solid white. **Native draws real textures with specific colours, and its
smoke is dim, not additive white.**

| Port renderer (all in `src/Allegiance.ClientCore.Game/Rendering/`) | Native equivalent, art file, colour/blend |
|---|---|
| `MissileTrailRenderer` (+ `MissileTrailParticles`), `ShipEngineTrailRenderer` | `effect/particlegeo.cpp`: **SmokeBehavior** uses `e_smokebmp` (`:14,33`), colour `RGBA(0.25c, 0.2c, 0.25c, 0.25c)` with c = remaining fraction (`:~47-49`): **dark grey, at most 0.25**. Size grows 0.75→3.0× (`:~51-54`). **FireBehavior** uses `e_firebmp` (`:13,77`), colour `(1, c, c, s·c)` (`:~86`): red-orange fading, with a 10% ramp-up. Drawn with `pContext->DrawDecal(surface, color, …, size, angle)` (rotating). |
| `ShieldFlareRenderer` (+ `ShieldFlareParticles`) | `WinTrek/trekigc.cpp:1624-1645`: an **animated texture `fx09bmp`** (`AWF_SHIELD_FLARE_TEXTURE`, `artwork.h:209`; `m_pimageFlare`, `:2512-2514`) mapped **onto the shield geometry** `m_pgeoFlares[id]` with the hit's ellipse equation. A patch on the shield bubble, not a free-floating glow. Cap `GetFlareCount() < 4` (already reproduced). Triggered from `Igc/shieldIGC.cpp:170-171`. |
| `HullHitSparkRenderer` (+ `HullHitSparkParticles`; spawned at `Game1.Rendering.cs:~1224-1252` for hits on **any** model: ships, **asteroids**, stations) | Native `ThingGeo::AddHullHit` (`effect/thinggeo.cpp:1322-1340`) does **two things**: (1) `m_phullHitGeo->AddHullHit` is the **textured surface flash**, `HullHitGeoImpl` (`effect/efgeo.cpp:660-700+`), with `fx11bmp` (`AWF_EFFECT_NORMAL`) + `f103bmp` (`AWF_EFFECT_TANGENT`, `effect/efart.h:16-17`) oriented by normal/forward/right (read its `Render` for size, timing and colour; **the port appears to lack this layer, so verify**); (2) **two debris bits** via `m_pbitsGeo->AddBit(pos, speed·(normal ± 0.25 spread), size)` with `size = m_radius/2`, `speed = 0.4·size`. The bits are **`utl10` geometry** (`BitsGeo`, `AWF_EFFECT_BIT`, `efgeo.cpp:795`): small 3D debris chunks, **not glowing sprites**. The port draws those 2 bits as additive **white blobs at radius/2**, so **asteroid hits (large radius) throw out two huge white blobs** (Domingo: "same white thing happens when hitting asteroids and it's distracting"). Triggered from `Igc/projectileIGC.cpp:193` (hull damage only, visible) → `:224`. |
| `DamageDebrisRenderer` (+ `DamageDebrisParticles`, `Flight/DamageDebrisTracker.cs`, P297) | The **damage smoke stream**: `AddDamage` (`effect/thinggeo.cpp:1358-1382`, from `Igc/shipIGC.cpp:1311`) opens up to 10 damage frames; `AddDamageParticles` (`thinggeo.cpp:699-760`) emits from each via `m_pParticleGeo->AddSmoke()` (size 1.5–2.5), so it's drawn by **SmokeBehavior: `e_smokebmp`, dim grey ≤ 0.25** (same as the missile smoke row). *(Correction: an earlier draft mapped this row to `utl10`. `utl10` is the hull-hit **bits** geometry; see the HullHitSpark row.)* |
| `EngineNozzleGlowRenderer` | `effect/thinggeo.cpp:261`: `f101bmp` (`AWF_EFFECT_AFTERBURNER_GLOW`). |
| `NavStrobeRenderer` | `effect/efapp.cpp:128`: `f101bmp` (`AWF_EFFECT_STROBE`). |
| `WarpTransitParticleRenderer` | Find native's warp/aleph effect (`f02bmp` `AWF_EFFECT_ALEPH_STAR` and the aleph/teleport sparks in `thinggeo.cpp:~655-690`). Cite it, or document that it's a port-only effect. |

Also: the ship/missile **trail line** uses `trailbmp` (`efgeo.cpp:524`,
`TrailGeo`; `SetTrailColor`, `thinggeo.cpp:921-935`). Check whether the port
draws trails and whether they match.

**All art is present** in `~/Development/Artwork` (`ALLEGIANCE_ARTWORK_PATH`):
`e_firebmp.mdl`, `e_smokebmp.mdl` (plus `.png` copies), `fx09bmp.mdl`,
`fx11bmp.mdl`, `f103bmp.mdl`, `Trailbmp.mdl`. Check `f101bmp`, `f02bmp` and
`utl10`. Decode them as the explosions do (`ExplosionAssets.cs` +
`MdlTextureDecoder`), cached per name.

**Native blend modes:** check `engine/context.cpp` (`SetBlendMode`,
default `BlendModeSourceAlpha` `:343`) and `DrawDecal` for which blend each
effect uses. **Don't assume additive.**

## Scope

1. **A shared native-effect-texture cache** (load/decode once, per name),
   reused by every renderer below.
2. **Smoke and fire** (missile and engine trails): `e_smokebmp` and
   `e_firebmp` with native's exact colour/alpha/size curves and rotation.
   Use native's blend mode. If native's dim smoke is alpha-blended, smoke
   must **not** be additive.
3. **Shield flares:** `fx09bmp` animated, placed **on the shield surface**
   at the hit. A faithful approximation of native's flare geo + ellipse
   mapping is fine (e.g. a quad tangent to the shield ellipsoid at the hit
   point, sized and oriented as native). Keep the cap of 4 and the 0.75 s
   timing if native agrees.
4. **Hull hits (ships, asteroids, stations):** (a) the `fx11bmp` + `f103bmp` surface flash,
   hull-aligned (normal/tangent frames), with native size, timing and colour; (b) the
   2 debris bits as **`utl10` geometry** (load/convert it like other `.mdl`
   meshes), lit by the scene light, with native size/speed/spread. No additive white sprite.
5. **Damage smoke** (`e_smokebmp`, as in scope 2), **nozzle glow, nav strobe, warp transit:** native art per
   the table, or a documented reason to keep the current look.
6. **Remove `GenerateSoftSprite`** from every renderer that now has native
   art. Keep it only as a logged fallback when an art file is missing.
7. Render-state hygiene: set and restore blend/depth/sampler per pass
   (P339/P525/P529 lessons). Particles keep depth-read on and depth-write off
   unless native differs.

## Explicitly out of scope

- Projectiles (P544, already queued).
- Explosions (P263 is already native art; P543 is the enhancement spike).
- New CC0 or "enhanced" art, bloom, or soft particles. That's P543.
  **P545 is native parity.**
- Lighting of solid objects (P530).

## Verification

- **Failing-first unit tests:**
  - Smoke colour at c=1 is (0.25, 0.2, 0.25, 0.25) and fades to 0; size
    grows 0.75→3.0×.
  - Fire colour curve, including the ramp-up.
  - Each renderer resolves its native texture name (`e_smokebmp`,
    `e_firebmp`, `fx09bmp`, `fx11bmp`/`f103bmp`, …).
  - Smoke blend state = native's (not additive, if native says so).
  - Missing art → fallback, logged once.
- **Headless visual proof** (`tools/roster-capture`, offscreen as in
  P525/P529):
  - **(a) A missile launch close to the camera** at 0, 0.1, 0.3, 0.6 s,
    old vs new. **Plus a volley:** 4 missiles fired from the own ship,
    **from the cockpit/chase view the player actually flies with**, at 0.2, 0.5
    and 1.0 s. Domingo reports that a volley fills the view with white and
    makes flying difficult. Report the blown-out-pixel share for the volley too.
  - **(b) A "dense combat" scene:** ~10 ships trading fire in a small
    volume, with shield hits, hull hits, several missiles and **2–3 ships at
    low hull (damage smoke streams)**, old vs new.
  - **(c) Asteroid hits:** a gun stream hitting a large asteroid, close to
    the camera, old vs new (blown-out share too).
  - **Measure the share of blown-out pixels** (all channels ≥ 250) in the
    scene region, old vs new. The target is a large drop. Report the
    numbers.
  - Contact sheets in the report assets folder.
- Whole-solution build, 0 warnings and 0 errors. Full suite via
  `scripts/run-tests.sh` (P471), 3 consecutive runs **on the final code**.
  Cite `docs/TEST_FLAKES.md`. **Report only numbers you actually measured.**
- ⚠️ **Commit as soon as the code, tests and proof are done, BEFORE the 3×
  suite**, then amend with suite numbers. **Include every new file**
  (`git status --untracked-files=all`). Confirm with `git log -1` and a
  clean `git status`.

## Deliverables

1. Committed on **`master`**, not pushed. Check with
   `git branch --show-current`.
2. The report `docs/reports/P545_NATIVE_PARTICLE_TEXTURES.md`: native
   citations per effect, the renderer table before/after, the white-pixel
   numbers, contact sheets and tests.

## ⚠️ Session rule: never end a turn while waiting

This session runs in one-shot mode: **your session ends the moment you finish a message.** Run long commands (the 3× `scripts/run-tests.sh` suite, live proofs, builds) **in the foreground**, blocking, one after another. **Never** start them in the background and then reply "waiting for it to finish". Two earlier sessions (P529, P542) did exactly that: the session ended mid-suite, the background processes were killed, and the suite results were never recorded. If a command might exceed a tool timeout, run each suite run as its own foreground call and record its numbers before starting the next.

# P546 — adopt the P543 spike into the game: enhanced explosions (CC0 fireball flipbook, capped brightness, sparks, flash, fx18 ring) and animated, sector-lit, premultiplied smoke everywhere, with near-camera fade and a density budget. "Enhanced" by default, "Native" kept as a setting

## Why this task exists

**Domingo (2026-10-05):** after seeing the P543 results, "yes, scope
adopting P543 into master". Earlier: "we definitely need better particle
effects for everything", "smoke looks kind of flat texture that won't let
you see correctly", and missile volleys make "lots of white everywhere".

**P543** (branch `spike/p543-vfx`, commit `cd4d501f`; worktree
`~/Development/AllegianceCore-p543`; report
`docs/reports/P543_VFX_SPIKE.md` on that branch) measured, with real
renders:
- **Dense combat** (6 overlapping fighter deaths, t = 0.9 s): native
  explosions **90.22%** blown-out → Enhanced **15.01%**. Bloom brings it back
  to 89.70%, so **bloom is out of scope.**
- **4-missile volley from the flight camera:** 0% blown-out in all
  variants (after P545), and the **near-camera fade** cuts the smoke covering
  the view by ~60% at t = 0.2 s (full > 18 m, gone < 2 m).
- **Cost:** +0.067 ms/frame (+6%) for a BigShip explosion at 1280×720;
  **+9.89 MiB** VRAM of CC0 atlases.
- **Recommendation (its §"Recommendation"):** adopt the CC0 flipbook
  fireball plus animated, alpha-lit premultiplied smoke for explosions;
  premultiplied smoke wherever smoke appears (missile trails, engine
  trails, low-hull damage smoke); sector-light tint; near-fade; a density
  budget; **keep this repo's own CPU particles** (no MonoGame.Extended).

**The spike code lives only in the capture tool** (not the game):
`tools/roster-capture/VfxEnhancedRenderer.cs` (323 lines),
`Cc0SmokeField.cs` (141), `VfxAssets.cs` (94), `VfxProof.cs` (proof
scenes), `VfxBloom.cs` (not adopted). Assets:
`assets/vfx-spike/atlas/{Explosion00,Explosion01,Explosion01-light,Cloud01,WispySmoke01,DiscSmoke01}.png`
plus `README.md` and `licenses/` (all CC0: the Unity free VFX flipbooks;
Kenney packs were licensed but not committed).

**Game-side state to build on** (`master`):
- Explosions: `Rendering/ExplosionRenderer.cs` (P263, native `exp20-25` +
  `fx18`), driven by `ExplosionPreset`/`ExplosionSampler` (native
  `AddExplosion`, `trekigc.cpp:95-177`).
- Particles (P545): `Rendering/NativeEffectTextures.cs`,
  `NativeParticleCurves.cs`, `ParticleBillboardBatcher.cs`, and the
  renderers `MissileTrailRenderer`, `ShipEngineTrailRenderer`,
  `DamageDebrisRenderer`.
- Sector lighting (P530): `SectorLighting` / `_sectorLighting`.

## Scope

1. **Bring the spike's code into the game**, not the capture tool: an
   enhanced explosion renderer and an enhanced smoke path under
   `src/Allegiance.ClientCore.Game/Rendering/`, sharing P545's
   `ParticleBillboardBatcher` and texture cache. Don't fork a second
   batcher. Then make `tools/roster-capture`'s `vfx` mode use the **game's**
   classes, so the proof renders the real code path.
2. **Assets:** move the CC0 atlases into the game's assets the way this repo
   ships non-native art (check how Content/MGCB or runtime-loaded assets
   are packaged for **DesktopGL and Android**). Keep `README.md` with the
   per-file licence table and the `licenses/` folder alongside. They must
   load on a clean checkout without `ALLEGIANCE_ARTWORK_PATH` (they're
   ours to ship, unlike native art).
3. **Explosions (Enhanced):** CC0 fireball flipbook (additive, **capped
   intensity**), animated smoke (premultiplied, sector-lit, lingers longer),
   spark streaks, a flash, and the native `fx18` shock-wave ring. Timing and
   size still come from `ExplosionPreset`/`ExplosionSampler`. No gameplay
   change.
4. **Smoke everywhere (Enhanced):** missile trails, engine trails and damage
   smoke use the animated CC0 smoke (random start frame and rotation),
   **premultiplied alpha**, tinted by the P530 sector light. Fire puffs
   stay additive with capped intensity.
5. **Near-camera fade** for all smoke and fire particles. Re-pick N/M against
   the **real** flight/chase camera distances in the game (the spike's
   18 m / 2 m were measured offscreen). Explain the choice.
6. **Density budget:** cap smoke per emitter and on screen, thinning the
   oldest puffs first under load (the spike's `MaxSmokePerExplosion` idea),
   with named constants.
7. **A "Native vs Enhanced" setting**, default **Enhanced**: one switch
   (config/settings file or the existing options mechanism, plus a CLI flag
   for captures) that keeps the exact P545/P263 native path available for
   parity checks. Both paths stay tested.

## Explicitly out of scope

- Bloom and soft particles (they need compiled `.fx`; no Wine here). Keep the
  spike's `Bloom.fx`/`SoftParticle.fx` and compile instructions in the repo
  for later, unused.
- Shield flare, hull-hit, warp-transit enhancements (the spike's table lists
  candidates). A later task; leave them on P545's native art.
- Projectiles (P544), lighting (P530), HUD.
- Merging the `spike/p543-vfx` branch wholesale. Port the pieces; leave the
  branch as the record.

## Verification

- **Failing-first unit tests:**
  - Default setting is Enhanced, and Native selects the P545/P263 path.
  - The near-fade alpha factor at/inside/outside N/M.
  - The density budget evicts the oldest first.
  - Smoke uses premultiplied blend, and fire is capped.
  - Atlases resolve on a clean checkout (no artwork path).
  - Explosion timing and size are unchanged from `ExplosionSampler` for every
    preset.
- **Headless proof through the game's classes** (`roster-capture vfx`, now
  calling the game renderers): re-render the spike's scenes (fighter
  explosion, station explosion, dense combat, 4-missile volley from the
  flight camera at 0.2/0.5/1.0 s) for **Native vs Enhanced**. Report the
  blown-out share and smoke screen coverage. **The game path must match or
  beat the spike's numbers** (dense combat ≤ ~15%, volley near-fade
  coverage reduction ≥ ~50%). Contact sheets in the report assets.
- **Cost:** ms/frame Native vs Enhanced at 1280×720 (offscreen, averaged), and
  the VRAM added.
- **Live sanity:** run the real client against a local server with bots
  fighting for ≥ 60 s in Enhanced. No exceptions, no render-state leaks
  (HUD, asteroids and ships look as before), with a screenshot.
- Whole-solution build, 0 warnings and 0 errors. Full suite via
  `scripts/run-tests.sh` (P471), 3 consecutive runs **on the final code**.
  Cite `docs/TEST_FLAKES.md`. **Report only numbers you actually measured.
  Don't re-run a failing suite until it comes up clean;** report each run as
  it happened.
- ⚠️ **Commit as soon as the code, tests and proof are done, BEFORE the 3×
  suite**, then amend with suite numbers. **Include every new file,
  including the PNG atlases and licence files**
  (`git status --untracked-files=all`). Confirm with `git log -1`, a clean
  `git status`, and a clean-worktree build (`git worktree add /tmp/… HEAD`,
  then build) so nothing is missed.

## Deliverables

1. Committed on **`master`**, not pushed. Check with
   `git branch --show-current`. Don't touch the `spike/p543-vfx` branch.
2. The report `docs/reports/P546_ENHANCED_EXPLOSIONS_AND_SMOKE.md`: what was
   ported from where, asset packaging and licences, the setting, near-fade
   and budget choices, Native vs Enhanced sheets and numbers, cost, the live
   screenshot, tests.

## ⚠️ Session rule: never end a turn while waiting

This session runs in one-shot mode: **your session ends the moment you finish a message.** Run long commands (the 3× `scripts/run-tests.sh` suite, live proofs, builds) **in the foreground**, blocking, one after another. **Never** start them in the background and then reply "waiting for it to finish". Two earlier sessions (P529, P542) did exactly that: the session ended mid-suite, the background processes were killed, and the suite results were never recorded. If a command might exceed a tool timeout, run each suite run as its own foreground call and record its numbers before starting the next.

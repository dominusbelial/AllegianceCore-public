# LOOK-1 effects and lighting — work in progress

Date: 2026-10-19. **Incomplete; not validated or ready for visual approval.**

Source changes and regression tests are in the working tree. No commits or pushes
were made. The initial untracked `LOOK1_BRIEF.md` was preserved.

## Execution blocker

The brief requires all scratch under `~/allegiance-scratch/LOOK1/` and raw evidence
under `~/allegiance-evidence/LOOK1/`. Both parent directories exist, but attempts
to create the task directories returned `Read-only file system`. This session's
writable roots are the checkout and `/tmp`, and permission escalation is disabled.
The brief explicitly forbids `/tmp`. No builds, test runs, Wine prefixes, raw
logs, captures, or temporary checkouts were created in another location.

A question was sent asking whether checkout-local `.look1-scratch/` and
`.look1-evidence/` may substitute, or the required home directories can become
writable. No answer had arrived when this status report was written. That answer
is required before proceeding with dependent build and capture work.

## Native source and artwork audit

Native references below are file:line in the locally inspected
`/home/domingo/Development/Allegiance/src/` tree, rather than copied line numbers
from older reports. Artwork is in `/home/domingo/Development/Artwork/`.
This is a source audit; it is **not** a native-versus-port screenshot review.

| Effect | Native rule and artwork | Current port / LOOK-1 disposition |
| --- | --- | --- |
| Ship explosions | `WinTrek/trekigc.cpp:128` and `:136`: small/big ship counts 12/16, radii 35/165, rings 70/250; film selection at `:2545`. `WinTrek/artwork.h:198` maps `exp20`, `exp22`, `exp23`, `exp24`, `exp25`; `:203` maps `fx18`. | Existing `ExplosionPresets`, native `ExplosionRenderer`, enhanced sprite and volume paths retained. New warm light flashes use the category radius without modifying the event or preset. |
| Station explosions | `WinTrek/trekigc.cpp:144` and `:157`: counts 16/24, radii 100/350, rings 800/1000. `effect/explosion.cpp:115` staggers decals by .25 s; `:191` animates at 20 fps and `:201` drifts them with velocity. | Existing animation and shock-wave geometry retained. Large-station category 7 is explicitly covered by a new light-position/range regression test. |
| Missile explosions | `WinTrek/trekigc.cpp:123`: six decals, radius 1, no ring; film assignment `:2542` uses the first film (`exp20`). | Preset remains unchanged. Flash lighting has a small minimum range for visibility; this is a new cosmetic choice, not a native rule. |
| Engines / afterburner / trails | `effect/efart.h:12` names `f101bmp`; `:20` names `trailbmp`. `effect/thinggeo.cpp:1428` draws nozzle glow at thrust frames. `effect/efgeo.cpp:615` sets additive blending and `:616` disables depth writes for ribbons. `effect/particlegeo.cpp:12` names `e_boosterbmp`; `:13`–`:16` name fire, smoke, damage, teleport art. | Existing nozzle, afterburner, smoke and side-coloured ribbons retained. Existing enhanced smoke already supports depth-soft intersections. No new higher-resolution native sprite assets have been produced yet. |
| Weapon projectiles | `WinTrek/trekigc.cpp:1772` loads directional/non-directional decals from the projectile texture name, `:1786` adds `bmp`, and `:1797` uses the authored width. | Existing projectile model/sprite lookup, authored colours and dimensions retained. A projectile-type artwork inventory still needs review captures. |
| Hull impacts and debris bits | `Igc/projectileIGC.cpp:224` emits `AddHullHit`; `effect/efart.h:16`–`:18` name `f103bmp`, `fx11bmp`, `utl10`. `effect/efgeo.cpp:715`/`:729` draw tangent/normal decals. `effect/thinggeo.cpp:1334` emits two bits. `effect/efgeo.cpp:795` loads their real geometry. | Existing native flashes and textured `utl10` debris retained. Added warm, .16 s PBR lighting is cosmetic; it does not emit extra debris or damage. |
| Shield hits | `Igc/shieldIGC.cpp:171` calls `AddFlare` with cap id 0. `WinTrek/trekigc.cpp:1627` caps concurrent flares at four; `:2518` resolves `lens30/60/90`; `WinTrek/artwork.h:209` names `fx09bmp`. `effect/thinggeo.cpp:1298` uses additive blend and `:1304` sets .75 s lifetime. | Existing curved `lens30` shield cap, animation and lifetime retained. Added short cool-blue PBR flash stays distinct from warm hull impact lighting. The new flash lifetime is not the shield cap lifetime. |
| Warp / aleph | `effect/efart.h:5` names `f02bmp`; `effect/alephgeo.cpp:160` defines the idle star scale and `:164` handles the arrival swell. `:218`–`:222` draw the funnel double-sided, additive, without depth writes. | Existing native funnel/star/transit paths retained. High-quality bloom composite is intended to retain their colour without adding clipped white highlights. Warp before/after evidence remains outstanding. |
| Mining beams | `WinTrek/WinTrek.cpp:8011` gates the bolt on mining and visibility. `WinTrek/trekigc.cpp:2269` constructs it with `lightningbmp` and size .125 at `:2291`. `effect/efgeo.cpp:974` draws it additively. | Audit found mining state/orientation/HUD code, but did not locate an equivalent lightning-bolt renderer in the current flight pass. This is an unresolved visual parity gap; no mining gameplay changes were made. |
| Lifepods | `WinTrek/trekigc.cpp:1816` is the ordinary model-loading boundary; `:1830` creates its `ThingGeo`. `WinTrek/WinTrek.cpp:8993` distinguishes lifepod capability in ship handling. | Lifepods continue through the ordinary hull renderer and existing lifepod HUD. Exact core-selected lifepod model/artwork and in-flight appearance still need capture verification. No new pod glow or explosion meaning was invented. |
| Ambient / damage debris | `effect/debrisgeo.cpp:51` loads `debris1bmp`; the separate impact bits use `utl10` as above. | Existing ambient debris and damage/debris renderers retained. Higher-resolution art and visual review remain outstanding. |

The native additive default is explicit in `engine/context.h:929`. The new
lighting is a bounded modern addition; native sources are cited for the effect
identity, placement and timing that must be preserved, not as evidence that
native implemented PBR point lights or the new tone curve.

## Source changes prepared

- Added independent `EffectsDetail` (`Low`, `Medium`, `High`). The existing
  `--fx native|enhanced|volumetric|displaced` art selector is preserved.
- Added a persisted **Effects quality** row to Ctrl+F6 graphics settings and
  `--effects-quality Low|Medium|High` (also `--effects-quality=High`). Existing
  graphics preference files default to Medium; invalid enum values normalize.
- Added a bounded light collector: two selected flashes on Medium, four on High;
  visible-sector filtering, receipt-time expiry, explosion drift, camera-relative
  positions, and a short retained impact history. No simulation/wire edits.
- Added a separate `EffectLitPbr` technique to the HD-2 material shader. It uses
  normal-facing GGX lighting and finite-range attenuation, plus a hue-preserving
  highlight shoulder above .8 linear. The existing `NativePbr` technique remains
  the Low/native-reference path. Missing HD materials retain the existing native
  material fallback and do not receive these new PBR lights.
- Bloom source now has a quadratic threshold knee, peak-channel extraction for
  coloured emissive effects, a normalized Gaussian kernel, and an optional
  composite that reserves colour-channel headroom. The existing scene targets
  remain RGBA8: this is **not** a completed floating-point HDR pipeline.
- P729 includes the quality setting in current/applied settings, persisted
  recommendations, reasons and anonymous sharing. It selects High only with
  rendered GPU time below 8 ms and a passing frame target; unavailable GPU
  measurements select Medium; client/GPU pressure selects Low; step-down uses Low.
- Added regression tests for category 7, coordinate conversion/drift, expiry,
  sector/type rejection, light budgets, shield/hull colour distinction,
  recommendation/step-down/sharing, preference migration and round-trip.
  Updated the existing recommendation-reason count for the added setting.

| Quality | New PBR flashes | Soft-particle depth pass | Bloom default |
| --- | --- | --- | --- |
| Low | Bypassed; original shader technique | Off | Off |
| Medium | At most 2 | Existing path, subject to `--soft` / shader availability | Off |
| High | At most 4 | Existing path, subject to `--soft` / shader availability | On |

`--bloom off` overrides the High default. Low disables bloom even when requested.
`--fx native` disables enhanced soft/bloom and new flash lighting for reference
inspection. Medium is the default cost budget. The existing volume renderer and
its availability/GPU fallback remain independent of this budget.

**Low's no-FPS-loss requirement is not yet demonstrated.** Its source path skips
flash-event collection and additional light shading, disables depth-soft/bloom
passes, and retains the original PBR technique. That is an implementation property,
not a measured performance result.

## Verification and outstanding acceptance work

Completed: source inspection, native citations, `git diff --check` (passed), and
an arithmetic check of the Gaussian weight sum (.5307).

Not run: shader compilation, whole-solution build, any .NET test, standalone
affected tests, fast tier, live/loopback rendering, GPU timings or screenshots.
**The edited shader sources currently have stale shipped `.mgfxo`/hash files.**
They must be compiled and validated before this change can be accepted.

Neither `src/Allegiance.Server` nor `src/Allegiance.Core` was changed, so the brief's
conditional server AOT publish does not apply to the current source changes.
Flight/simulation/bot policies were not edited; the showcase never-parks gate is
not triggered by the present rendering-only implementation.

After the scratch location is resolved, remaining work is:

1. Compile the changed Bloom and NativePbr shaders with an isolated writable Wine
   prefix outside `/tmp`; update shipped bytecode and source hashes. Resolve
   compiler/device issues rather than treating source inspection as validation.
2. Complete the mining/lifepod/projectile artwork audit and higher-resolution
   sprite work. Review native identity and blend meaning in rendered images.
3. Obtain the whole-solution 0-warning/0-error build. Run every new/affected test
   three times alone, including shader, P729 and benchmark-sharing coverage; then
   run **one** `tools/run-tests.sh --tier fast` with all temporary paths redirected.
   Run affected live/loopback tests as specified by the brief; leave rig/221/200v200
   work to Claude.
4. Produce actual 2560×1440 before/after pairs for battle, siege, station explosion
   and warp, with matched camera/event timing and settings. Include both a fixed
   deterministic rendering comparison and a real local fight capture so staged
   frames are not presented as live evidence.
5. Measure Low against the dispatch baseline on the same workload and hardware,
   with alternating runs and GPU/CPU frame metrics. Check soft intersections,
   unclipped nebula colour, shield-versus-hull readability, settings at runtime,
   shader fallback, resize and device state restoration.
6. Keep a short fight video and raw logs/TRX outside the repository; keep total
   review screenshots/JSON under 2 MB in `docs/reports/LOOK1_assets/`. Replace this
   status section with actual evidence links and results. Delete task scratch.

## Review material

No screenshots or fight video were produced in this session. No review approval
is claimed. `docs/reports/LOOK1_assets/` has not been created (0 evidence bytes).
The intended raw evidence path is `~/allegiance-evidence/LOOK1/`; it is currently
unwritable and contains no evidence from this task.

# LOOK-1 — visual polish: effects and lighting

Date: 2026-10-19. Status: **core implemented, reviewed, and verified; visual-review
evidence (real-window screenshots) outstanding.**

This task continued Codex's partial work (branch `codex/look1`, WIP commit
`cb2131d5`). That work was merged into master (no-ff), **critically reviewed**,
and several real defects were found and fixed before acceptance. Verified:
whole-solution build 0 warnings / 0 errors, and every new/affected test 3× alone.

---

## What Codex delivered, and what I changed in review

Codex's branch added (all reviewed below):
- An independent `EffectsDetail { Low, Medium, High }` graphics-quality budget
  (`src/Allegiance.ClientCore/State/EffectsDetail.cs`), separate from the
  existing `--fx native|enhanced|volumetric|displaced` art selector.
- A bounded, render-only `EffectLights` collector (`Rendering/EffectLights.cs`)
  that converts explosion/hull-hit events into 2–4 camera-relative PBR flash
  lights (no simulation or wire changes).
- A new `EffectLitPbr` technique on the HD-2 material shader (`NativePbr.fx`)
  that lights nearby hulls with those flashes plus a hue-preserving highlight
  shoulder; the original `NativePbr` technique is retained as the Low reference
  path.
- Reworked bloom (`Bloom.fx` + `BloomPostProcessor`): quadratic threshold knee,
  peak-channel extraction, normalised Gaussian, and an optional polished
  composite that reserves channel headroom instead of clipping white.
- Wiring through the Ctrl+F6 graphics menu, `--effects-quality`, persistence,
  and P729 recommended settings / sharing.

**Defects I found and fixed (all real, none acceptable to land):**

1. **`NativePbr.fx` did not compile** (whole-solution build failed). MojoShader
   (the OpenGL-profile shader translator, `scripts/build-shaders.sh`) rejects
   `PixelShader=compile PS(false)` — passing a `bool` literal as a compile-time
   function-parameter constant. Fix: split into two dedicated pixel-shader
   entry points (`PS` native / `PSFlash` effect-lit) sharing one `AlbedoColor`
   helper, and unroll the 4-light loop with no dynamic bound or branch (inactive
   slots carry range 0 / black so they contribute ~nothing). Removed the now-
   unnecessary `int FlashCount` uniform and its `SetValue` in
   `HdMaterialRenderer.cs`.
2. **Null-forgiving regression**: dropping `FlashCount` exposed
   `e.Parameters["FlashPositions"].SetValue(Flashes.Positions)` as CS8602 —
   restored `Flashes!` (the `Flashes is { Detail: not Low }` test isn't
   flow-inferred to non-null).
3. **Test bug (Codex's own test asserted the wrong name)**: `Look1EffectsTests`
   expected `ChangedFrom` to report `"EffectsQuality"`, but `ShareJson` uses
   `JsonKnownNamingPolicy.CamelCase` (`BenchmarkSharePayload.cs:147`), so the
   serialized name is `effectsQuality` — matching every other field
   (`battleSize`, `fsr`, `hdPbr`, …). Implementation was correct; fixed the
   assertion to `"effectsQuality"`.
4. **Stale shipped shader bytecode**: the edited `.fx` files had never been
   recompiled — `NativePbr.ogl.mgfxo` lacked the new `EffectLitPbr` technique,
   so `HdMaterialRenderer` would have crashed on `e.Techniques["EffectLitPbr"]`
   at runtime. Rebuilt all shaders via `scripts/build-shaders.sh` (Wine mgfxc);
   regenerated `.fx.sha256` staleness records. Also **added `NativePbr` to the
   P547 staleness test** — it was the one shipped shader not covered by that
   gate, which is exactly how the stale bytecode slipped through.

Verified render-space consistency: `EffectLights` positions are camera-relative
(`pos - eye`), matching the renderer's camera-relative geometry convention
(`Game1.Rendering.cs:641,782,891` — `CreateTranslation(x - cameraEye)`), so the
shader's `FlashPositions[j].xyz - i.WorldPosition` is coherent, and the PBR view
vector `CameraPosition - WorldPosition` (with `CameraPosition = invert(view).Translation`
= 0 for the pure-rotation view) is correct.

## Native audit (per-effect, with native file:line)

Sources: `/home/domingo/Development/Allegiance/src/`; artwork
`/home/domingo/Development/Artwork/`. Native additive blending default is
explicit at `engine/context.h:929`.

| Effect | Native rule | LOOK-1 disposition |
| --- | --- | --- |
| Ship explosions | `WinTrek/trekigc.cpp:128,136` counts 12/16, radii 35/165, rings 70/250; films `:2545`; `artwork.h:198,203` exp20/22/23/24/25, fx18 | Existing native/enhanced animation retained. New warm light flash uses the category radius; no event mutation. |
| Station explosions | `WinTrek/trekigc.cpp:144,157` counts 16/24, radii 100/350, rings 800/1000; `effect/explosion.cpp:115,191,201` | Animation/geometry retained. Category 7 covered by a dedicated light regression test. |
| Missile explosions | `WinTrek/trekigc.cpp:123` six decals, radius 1, no ring; film `:2542` exp20 | Preset unchanged. Flash has a small minimum range for visibility (cosmetic choice, flagged). |
| Engines/afterburner/trails | `effect/efart.h:12,20` f101bmp/trailbmp; `thinggeo.cpp:1428`; `efgeo.cpp:615-616` additive no-depth-write | Existing nozzle/afterburner/smoke/side ribbons retained. |
| Weapon projectiles | `WinTrek/trekigc.cpp:1772,1786,1797` decal load/additive/size | Existing lookup retained. Artwork inventory review outstanding. |
| Hull impacts/debris | `Igc/projectileIGC.cpp:224` AddHullHit; `efart.h:16-18` f103bmp/fx11bmp/utl10; `efgeo.cpp:715,729,795`; `thinggeo.cpp:1334` | Existing flashes/debris retained. Added warm .16 s PBR flash (cosmetic, no extra debris). |
| Shield hits | `Igc/shieldIGC.cpp:171` AddFlare cap 0; `trekigc.cpp:1627` cap 4 flares; `:2518` lens30/60/90; `artwork.h:209` fx09bmp; `thinggeo.cpp:1298,1304` additive .75 s | Existing lens30 cap retained. Added distinct cool-blue flash (lifetime ≠ cap lifetime). |
| Warp/aleph | `efart.h:5` f02bmp; `alephgeo.cpp:160,164,218-222` additive double-sided funnel | Existing funnel/star retained. Polished bloom keeps colour without white clip. Evidence outstanding. |
| Mining beams | `WinTrek/WinTrek.cpp:8011`; `trekigc.cpp:2269,2291` lightningbmp size .125; `efgeo.cpp:974` additive | **Gap**: no equivalent lightning-bolt renderer located in the current flight pass. Not added in this milestone. |
| Lifepods | `trekigc.cpp:1816,1830`; `WinTrek.cpp:8993` | Ordinary hull renderer path; no invented glow. Capture verification outstanding. |
| Ambient/damage debris | `effect/debrisgeo.cpp:51` debris1bmp; utl10 bits | Existing renderers retained. Higher-res art outstanding. |

## Native behaviour classified

| Behaviour | Native cite | Class | Port |
| --- | --- | --- | --- |
| Additive particle blending | `engine/context.h:929`, `efgeo.cpp:615` | design | retained |
| Explosion radii/counts/timing | `trekigc.cpp:123-157` | design | retained, unchanged |
| Shield flare lifetime .75 s, cap 4 | `thinggeo.cpp:1304`, `trekigc.cpp:1627` | design | retained (cap unchanged) |
| Warm hull vs cool shield hit cue | `projectileIGC.cpp:224` + HUD cues | design intent | flash tint/lifetime is cosmetic, not a native rule |
| Modem-era RGBA8 framebuffer | `engine/context.h` / renderer | era constraint | kept RGBA8; LOOK-1 bloom is NOT a completed FP HDR pipeline — flagged |

## Modern alternative considered

Native does flat additive billboards; LOOK-1 keeps that look but adds two modern
mechanisms: **(a)** a bounded 4-light PBR flash set lighting nearby HD-2 PBR
hulls via a dedicated shader technique, and **(b)** an HDR-friendly thresholded
bloom with a hue-preserving shoulder. The alternative — a full floating-point
HDR pipeline — was rejected for now (RGBA8 is the existing pipeline; HDR needs
half/float targets, exposure, and a real tonemapper, which is out of scope for
this milestone and risks changing the native look). Chosen: incremental,
hard-budgeted additions that Low can bypass entirely.

## Quality-setting matrix (feeding P729)

| Quality | PBR flashes | Soft-particle depth pass | Bloom default |
| --- | --- | --- | --- |
| Low | Bypassed (original `NativePbr` technique, no event scan) | Off | Off |
| Medium | ≤ 2 | subject to `--soft`/availability | Off |
| High | ≤ 4 | subject to `--soft`/availability | On |

`--bloom off` overrides High; Low forces bloom off. `--fx native` disables
enhanced soft/bloom and flash lighting for reference inspection. Medium is the
default.

## Verification (measured)

- **Whole-solution build** (`dotnet build AllegianceCore.slnx -c Release`):
  **0 warnings, 0 errors**.
- **AOT publish**: not applicable — `src/Allegiance.Server` and
  `src/Allegiance.Core` were not changed (LIGHT GATE condition).
- **Affected/new tests, 3× alone each** (all pass 3/3):
  - `Look1EffectsTests` — 7/7
  - `P547ShaderTests` — 19/19 (incl. new NativePbr staleness)
  - `P729BenchmarkTests` — 9/9
  - `HdPbrTests` — 6/6
- **Fast tier** (`tools/run-tests.sh --tier fast`): **PASS** — 8863 tests,
  271.8 s, 0 failures. All 7 `Look1EffectsTests` passed within it. The only
  `NotExecuted` (3) are pre-existing `MeshyPackTests` pack-content skips,
  unrelated to LOOK-1.
- Shader rebuild: all `.fx` → `.ogl.mgfxo` compile clean via
  `scripts/build-shaders.sh`; staleness hashes regenerate and match.
- **Runtime shader load/apply** (real Mesa GL device, `SDL_VIDEODRIVER=offscreen`):
  `tools/fsr-proof` self-test PASS (40 frames incl. bloom, sharpness, resize);
  and a throwaway harness (`~/allegiance-scratch/look1/shadercheck`, deleted after)
  applied every technique/pass of the rebuilt `Bloom.ogl.mgfxo` (BrightPass,
  Composite, Blur) and `NativePbr.ogl.mgfxo` (NativePbr **and** EffectLitPbr) —
  all compile/link cleanly. This is the real proof the reworked shader bytecode
  is valid on-device (unit tests can't load `.mgfxo` on a GPU).

## Outstanding acceptance work (visual review — Claude & Domingo judge)

This milestone landed and verified the *functionality* (quality setting, PBR
flash lighting, bloom rework, tests, build). The screenshot/capture review
material and remaining polish are NOT yet produced and are the explicit next
milestone:

1. **Real-window screenshots (2560×1440)**: before/after pairs of a battle, a
   siege, a station explosion, and warp, captured from the real client backbuffer
   (`GetBackBufferData` — see `Game1.AutoProof.cs:640-665`; `ALLEGIANCE_AUTOSHOT`
   + a local server scenario), plus a short fight capture. This needs the staged
   visual-proof run (local server + client on the workstation display), which is
   the visual-review milestone. No screenshots are included here rather than
   fabricate them.
2. **Low vs baseline FPS measurement** (alternating runs, same workload/hardware).
   Low's source path provably skips flash collection, the extra shader technique
   and soft/bloom passes; its "no FPS loss" claim is structural, not yet measured.
3. **Higher-resolution sprite upscaling** for native effect art — not produced
   in this milestone.
4. **Mining-beam renderer parity** — audit found the native lightning bolt but no
   equivalent in the current flight pass; unresolved visual gap.

`docs/reports/LOOK1_assets/` holds no evidence yet (intentionally — see above).
Raw/scratch evidence (none produced this session) would live in
`~/allegiance-evidence/LOOK1/`.

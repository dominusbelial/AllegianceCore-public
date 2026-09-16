# P237 — Warp visual scale fix + the warp's ambient sound

Stage landing: P-series master, not pushed (Domingo pushes). No side branch.
Follows P227's procedural swirling-vortex warp renderer (the "fix warp visual
scale" half of this stage) and adds the warp's native ambient sound (the
`alephSound` the original client attaches to every warp). PURE client + content —
zero server / wire change. This stage's own P227 live-scale finding drove the
calibration; see §1.

---

## 1. Visual scale — why the warp read "tiny," and the 10x calibration

### 1a. The problem (measured, not assumed)

P227 rendered the warp funnel scaled by the raw wire `Radius`
(`DataWarpIGC.Radius`). Every warp in `m2xspiderw.igc` has `Radius = 100`
(re-confirmed live this stage from `ClientWorld.Warps`), and the shared funnel
mesh has a **unit** rim radius (`WarpMeshFactory.DefaultRimRadius = 1`), so P227's
gate was only **~200 units across** (rim radius 100 → diameter 2×100).

Meanwhile the ACTUAL rendered model this client draws for every ship — the single
`bios_scout_upscaled.fbx` (the P207 scope cut; there is no per-hull-type visual
variety) — measures **~190 × 102 × 179 units** (longest dimension ≈ **190.065**) in
world space under the renderer's own transform (measured live by instrumenting
`ShipRenderer`'s exact `ParentBone.Transform * NoseAlignmentCorrection`). So at
`Radius = 100` the warp's ~200-unit gate was basically **the length of the rendered
ship** — which is exactly why Domingo's playtest read it as "very tiny."

Native's own `warpIGC.cpp:39` `SetRadius((float)m_warpDef.radius)` sets only the
**gameplay/collision** radius from wire data; the real visual size came from a
separate authored 3D model this checkout does not have. So there is no "missed
constant" to find — the target size is Domingo's own accepted art-direction figure:
**the warp should read at roughly 10× the size of a ship.**

### 1b. The multiplier, calibrated against the real rendered ship

Target: warp rim **diameter** = 10 × the rendered ship's longest dimension = 10 ×
190.065 ≈ **1900 units**.

Because the mesh rim radius is 1, `rim diameter = 2 × Radius × multiplier =
2 × 100 × M`. Setting that to 1900:

    M = 1900 / (2 × 100) = 9.5

`VisualScaleMultiplier = 9.5` gives a 1900-unit rim = **9.996×** the measured
190.065-unit ship — "10x" to every practical purpose. This is Domingo's accepted
figure, not derived from source (native's real visual size was the missing authored
model).

**Rendering-only.** The multiplier is applied ONLY in `Game1`'s warp-draw world
matrix (`wWorld = wRot * CreateScale(Radius * WarpRenderer.VisualScaleMultiplier) *
…`). The real `DataWarpIGC.Radius` (=100) is untouched in every gameplay consumer —
transit-trigger logic, autopilot warp targeting, collision — exactly as scoped.
Verified: no other reader of `DataWarpIGC.Radius` changed (git diff touches only the
client renderer + audio).

### 1c. Live verification (off-screen RT, the P207/P218 technique)

Standalone harness (throwaway, `/tmp/warp_harness`, outside the repo) booted the
REAL stack (in-process Server + real `ConnectionSession` on `m2xspiderw`),
read a real warp's wire `Radius = 100` from `ClientWorld.Warps`, then rendered the
REAL ship model and the REAL `WarpRenderer` funnel at the SAME measured distance
(5000 units) to off-screen `RenderTarget2D` PNGs and measured non-background pixel
bounding boxes:

- `ship_only.png`: ship 24 × 10 px (its 190-unit length = 24 px)
- `warp_only.png`: 236 × 236 px (head-on rim circle, 1900-unit diameter)
- **ratio = 236/24 = 9.83×**

Vision-checked the combined frame `ship_and_warp.png` (both at the same distance,
ship offset right of the gate): the funnel reads **~10–12×** the ship — "dramatic and
obvious," a tiny fighter beside a vast gate. Evidence: `.verify_p237/`.

---

## 2. Ambient sound — native's `alephSound`, wired as a distance-scaled loop

### 2a. The native mechanism (a normal positional loop, not a proximity hack)

Native attaches the generic per-object `AmbientSoundSite` to every warp via the
same switch arm asteroids/missiles/probes/stations all use:

    case OT_warp:
        m_pSoundSite = new AmbientSoundSite(pmodel, alephSound);   // trekigc.cpp:1436-1437

The real cue is a **looping, 3D-positioned** sound whose volume the engine
attenuates by listener distance:

    alephSound = GainSound(SFXGain, ThreeDSound(200,
                  LoopingSound(ImportWave("alephloop"))));          // Artwork/sounddef.mdl:385

So Domingo's requested "a distinct sound that increases as you get close" is a
normal distance-falloff positional loop — no special proximity mechanic exists (and
none is needed). Real asset **`alephloop.ogg`** confirmed present in
`/home/domingo/Development/Artwork` (43,706 B).

### 2b. Port approximation (no 3D-positional engine yet — P213)

This client has no 3D-positional audio engine, so per the P232-blessed
scope-cut the same audible behaviour is approximated by computing the
listener→warp distance each frame and scaling a looped `alephloop` instance's
volume by that distance — the same pure-planner architecture P232/P236 use:

- **`Audio/WarpAmbientSoundCurve.cs`** *(new, pure)* — linear distance→volume,
  `AudibleRadius = 7700` (a few diameters of the ~1900-unit gate so it reads as a
  distance landmark that swells on approach; native's authored `ThreeDSound(200)`
  is unrealistically tiny against the resized gate — documented in the file),
  `MaxVolume = 0.8`.
- **`Audio/WarpSoundPlanner.cs`** *(new, pure)* — keyed by the warp's real
  `WarpId`; enter→Start / continue→Update(with louder volume as distance shrinks) /
  leave→Stop, mirroring `ProjectileSoundPlanner`/`MissileSoundPlanner`.
- **`SoundManager`** — loads `Sounds/alephloop`, `Start/Update/StopWarpAmbient`
  managing a `Dictionary<short, SoundEffectInstance>` of looped instances.
- **`Game1.UpdateWarpSounds()`** — each frame, under the Gate lock, gathers the
  LOCAL ship's in-sector warps within the audible radius (the same sector filter
  the draw loop uses) and replays the planner's commands against `SoundManager`;
  logs `[monogame] audio: warp ambient start/stop` (the P213/P216/P232/P236
  console audio-state surface).
- **Content** — `alephloop.ogg` copied to `Content/Sounds/`, `OggImporter` →
  `SoundEffectProcessor` block added to `Content/Content.mgcb`.

### 2c. Live audio proof

**(i) Real-device harness** (`/tmp/warp_audio_harness`, throwaway): booted the
real stack, read a real warp's position/radius, drove the committed planner +
`SoundManager` (real `alephloop` asset on the real audio device) as the simulated
ship closed on the warp:

    SoundManager.IsLoaded=True
    probe dist=8800: no live instance            (out of range — filter drops it)
    -> Start(warp 2167) vol=0.0062               (entering range)
    probe dist=7640: instance State=Playing Volume=0.0062
    -> Update ... vol=0.20 / 0.40 / 0.60 / 0.784 (approaching — louder each step)
    probe ... State=Playing Volume=0.784         (all steps State=Playing)
    RESULT volume climbed monotonically on approach: True
    -> Stop(warp 2167)                           (receding out of range)
    probe dist=7800: no live instance            (released)

**(ii) Real full client** on :0 against the real server (m2xspiderw, teams:1):
the moment the ship is flying in the spawn sector (Ganymede, 4 warps), the
committed `UpdateWarpSounds` fired on the wire-fed real positions:

    [monogame] audio: warp ambient start (warp 2168, vol 0.475)   dist 3129  (nearest)
    [monogame] audio: warp ambient start (warp 2176, vol 0.331)   dist 4516
    [monogame] audio: warp ambient start (warp 2167, vol 0.299)   dist 4819
    [monogame] audio: warp ambient start (warp 2178, vol 0.206)   dist 5714  (furthest)

The four volumes are inversely ordered by the warps' measured distances from the
spawn point (probed independently: 3129 < 4516 < 4819 < 5714) — the real
distance behavior in the real running game.

---

## 3. Scope boundary / exclusions

- `DataWarpIGC.Radius` (real wire value = 100) is **unchanged everywhere it is
  read for gameplay**: transit-trigger, autopilot warp-targeting, collision. The
  9.5 multiplier appears only in the client's draw-world composition.
- Per-hull-type ship visual variety (the P207 scope cut this stage's
  calibration works around) is **out of scope** and not attempted.
- Native reference (`~/Development/Allegiance`) untouched; P227's core
  swirl-geometry logic unchanged (only the render scale composes on top).
- No server / wire change; the client's existing `SoundManager` conventions and
  the P232 pure-planner pattern are reused.

## 4. Build / test

- Whole-solution build: 0 warnings, 0 errors.
- MonoGame.Tests: **216 passed** (was 206; +10: 4 `WarpAmbientSoundCurve`,
  5 `WarpSoundPlanner`, 1 `WarpRenderer.VisualScaleMultiplier` calibration that
  pins the 10x rim-diameter↔ship relationship). Ran the suite 2× — clean both.
- ClientCore.Tests: 83 passed (2×). Core.Tests: 3061 passed of 3062 (2×), the
  single failure both runs being the pre-existing
  `LiveTurretGunnerBindingTests` timing flake (documented; fails persistently in
  isolation and on the pristine tree — not this stage's regression). No Core
  code touched.
- Must-fail proven: with `VisualScaleMultiplier` forced to 1, the calibration
  test reds (ratio 1.05 ∉ [9.5, 10.5]) — reverted to 9.5.
- `git diff` shows zero Server change; temp diagnostics (bounds logger) removed.

# P213 — Real sound for the MonoGame POC: engine, cockpit ambience, autopilot, environment

## Summary

P213 gives the playable MonoGame POC (`src/Allegiance.ClientCore.MonoGame/`,
P207–P212) real audio for the first time, scoped strictly to what is actually
flyable today — no combat/docking/mining/chat/menu sound hooks (those systems
don't exist in the POC). It uses MonoGame's own audio stack
(`SoundEffect` / `SoundEffectInstance`), NOT the original's obsolete
DirectSound3D engine (`/home/domingo/Development/Allegiance/src/soundengine/` —
DirectX-specific, irrelevant to a cross-platform MonoGame port; reference only).

Every cue is a **real asset** copied from the port's artwork tree
(`/home/domingo/Development/Artwork`, the same `ALLEGIANCE_ARTWORK_PATH` the
server already reads) and shipped through the MonoGame content pipeline (`.ogg`
→ `.xnb` via NVorbis, which is bundled with `MonoGame.Framework.DesktopGL`).

Four cues:

1. **Engine/thruster loop** (own ship only), a single looping
   `SoundEffectInstance` whose volume/pitch are driven every frame by the
   ship's real `Controls.Throttle` and real `Velocity.Length()` — the exact
   read path P211's HUD already uses (reused, not duplicated).
2. **Cockpit ambient hum** — the small-ship interior loop, audible only in
   P211's `CameraMode.Cockpit`, silent in Chase.
3. **Autopilot engage/disengage voice chimes** — played at P212's real
   engage/disengage transitions.
4. **Background sector ambience** — one low, unobtrusive looping track for the
   whole session.

## Real-asset provenance (the "engine sound" hunt, resolved properly)

The task flagged that no `thrust`/`engine`/`burner` file is obviously named in
the artwork. Tracing the native reference resolved it. The per-hull thruster
sound IDs come from `MyHullType::GetMainThrusterInteriorSound()` etc.
(`src/Igc/shipIGC.cpp:4709`), which read fields of the compiled hull-type core
data — an indirect reference, not a literal filename. The actual asset names
are resolved in the artwork's sound-definition namespace,
`Artwork/sounddef.mdl` (the `soundEngine` namespace `treksound.cpp` builds):

```
smallShipThrustInteriorSound = GainSound(SFXGain, ThreeDSound(100, LoopingSound(ImportWave("accelSmall"))));  // sounddef.mdl:1014
smallShipInteriorSound       = GainSound(SFXGain, GainSound(-15, LoopingSound(ImportWave("sshipint"))));     // sounddef.mdl:1012
afterburner1InSound          = GainSound(SFXGain, ASRSound(..., ImportWave("iafter")));                       // sounddef.mdl:1326
```

So the REAL main-thruster loop for a small ship is **`accelsmall.ogg`**
(verified present in the artwork; `accelsmall` = `ImportWave("accelSmall")`,
case-insensitive on disk), the small-ship interior hum is **`sshipint.ogg`**,
and the afterburner is **`iafter.ogg`/`xafter.ogg`** (out of scope here — no
afterburner control exists in the POC yet, and `FlightInput.cs` was not to be
given a new binding just to hang a sound off of). The autopilot chimes are the
exact VO the task cited: **`vo_sal_autopilotengage.ogg`** /
**`vo_sal_autopilotdisengage.ogg`** (cited at `WinTrek/trekigc.cpp:4880`,
`PlaySoundEffect(salAutopilotEngageSound)`). The sector ambience is
**`bioambient_1.ogg`**, matching the POC's Bios faction.

All five are real, confirmed-present assets; no guessed substitutes were needed
for anything in scope.

## Implementation

- `Audio/EngineSoundCurve.cs` (new, pure): the throttle/speed → volume/pitch
  mapping. **No MonoGame types** (same discipline as `Flight/FlightInput.cs`),
  so it is unit-testable without a device. It computes a single normalised
  "engine load" in [0,1] blending `throttle` (−1..+1 → 0..1) and
  `speed/maxSpeed` (clamped), weighted 0.4/0.6 toward speed; volume is a linear
  ramp over a quiet floor (0.10..0.65); pitch sweeps a narrow band
  (−0.30..+0.20) upward with load so full thrust audibly "revs". `maxSpeed`
  defaults to 120 (the sim's `HullType.Speed` for the default scout — see
  `docs/simulation.md`, which drives `hull.Speed=120`), and is a parameter so
  the pure logic isn't hard-coded.
- `Audio/SoundManager.cs` (new, MonoGame-facing): loads the five cues, prepares
  the looping instances, and per-frame applies the curve + camera-mode routing.
  **Graceful degradation**: sound is cosmetic to the flyable POC, so a host with
  no OpenAL device logs once and leaves the manager inert rather than killing
  the game (a deliberate, documented divergence from the hard-exit-on-failure
  pattern used for the essential ship model). All audio touchpoints are guarded.
  No positional 3D (`Apply3D`) is used — these are cockpit/own-ship cues; the
  modern `SoundEffectInstance.Apply3D(AudioListener, AudioEmitter)` equivalent
  is only needed for positioned *world* sounds (remote ships), which are out of
  scope. The exact MonoGame API was confirmed by reflection against the real
  `MonoGame.Framework.dll` before use (`CreateInstance`, `IsLooped`, `Volume`,
  `Pitch`, `State`, `Play/Stop`, `ContentManager.Load<T>`).
- `Game1.cs`: owns a `SoundManager`; loads it at the end of `LoadContent`;
  drives it in `Update` from the real `ServerShipUpdate` telemetry exactly where
  the own-pose is read (`mineUp.Controls.Throttle`, `mineUp.Velocity.Length()`,
  `_cameraMode == CameraMode.Cockpit`); plays the engage chime where P212 sets
  `_autoPilotEngaged = true` and the disengage chime in `DisengageAutopilot`
  only when it was actually engaged (fires on G toggle-off, manual input, and
  target-lost — all real disengage paths). Disposes it in `UnloadContent`.
- `Content/Content.mgcb`: five new entries (`OggImporter` →
  `SoundEffectProcessor`, `Quality=Best`), the same entry format as the ship
  model and P211's fonts; source `.ogg` files copied into `Content/Sounds/`
  (new folder, mirroring `Content/Models/`).

## Pure-logic test

`tests/Allegiance.ClientCore.MonoGame.Tests/EngineSoundCurveTests.cs`: verifies
the load blend/clamps at the corners, monotonicity in throttle, the idle
volume floor + clipped peak (0.65, not full-scale 1.0), the pitch sweep staying
in MonoGame's legal ±1 range, non-positive `maxSpeed` not dividing by zero, and
that full thrust raises both volume and pitch vs idle. Genuine clamped curve —
worth testing; not a flat linear scale.

## Out-of-scope adherence

No combat/weapon/explosion/docking/cargo/mining/chat/menu sounds (systems don't
exist). No new input bindings (no afterburner key added). No modifications to
`Flight/FlightInput.cs`, `Rendering/ChaseCamera.cs`, `Rendering/ShipRenderer.cs`,
`Rendering/Starfield.cs`, `Flight/MouseFlightController.cs`, or
`Flight/AutopilotSteering.cs` (read their state, didn't touch their logic).
No changes to the original's `src/soundengine/`. Zero server / `Allegiance.Core`
changes — all additive client work.

## Verification

- `dotnet build AllegianceCore.slnx` → **0 warnings / 0 errors**, whole
  solution. The build runs the MonoGame content builder, which imported all
  five `.ogg` files cleanly (confirmed `.xnb` outputs in
  `Content/bin/DesktopGL/Content/Sounds/`) — so MonoGame's NVorbis-based `.ogg`
  import genuinely works, verified by real build, not assumed.
- `dotnet test tests/Allegiance.ClientCore.MonoGame.Tests` → **78 pass, 0 fail,
  clean across 3 consecutive runs** (existing tests + the new curve tests).
  No server/Core code changed, so the wider `Allegiance.Core.Tests` suite is
  unaffected by this stage.
- **Live check** against a fresh `Allegiance.Server` (temp config, `teams:1`,
  so one peer reaches `Started`; real display `:0` and PipeWire/OpenAL present
  on this host). Ran the actual client to `Started`, then via a **temporary
  diagnostic log** (removed before commit) confirmed:
  - app launches, logs on, auto-readies, auto-undocks → reaches `Started`;
  - **all cues load** with no audio exception (`audio: loaded ...`);
  - **engine `Playing`**, **ambience `Playing`**;
  - **hum `Stopped` in Chase** (correct — hum is cockpit-only), and in a second
    run with the default camera temporarily forced to Cockpit, **hum `Playing`**
    and the engine volume correctly bumped 0.10 → 0.18 (the cockpit boost);
  - the **autopilot engage AND disengage chimes both `Play()` without
    exception** on the real VO assets (probed through the real one-shot path).
  - No exceptions/silent failures in the content-load or playback path.

  **Honest limitation:** this host has real ALSA/PipeWire devices but no
  speakers/audio-capture tool, so I could not *hear* the output. I verified
  `SoundEffectInstance.State == SoundState.Playing` at the right moments via
  the temporary diagnostic (states and volumes logged above) and confirmed no
  exceptions — I did not and cannot claim the audio "sounds good". Domingo will
  do the real listening test.

## Assets used (all from `/home/domingo/Development/Artwork`)

- `accelsmall.ogg` — small-ship main-thruster interior loop (`sounddef.mdl:1014`)
- `sshipint.ogg` — small-ship interior hum (`sounddef.mdl:1012`)
- `bioambient_1.ogg` — Bios sector ambience loop
- `vo_sal_autopilotengage.ogg` / `vo_sal_autopilotdisengage.ogg` — P212 autopilot chimes (`trekigc.cpp:4880`)

Copied into `Content/Sounds/` and registered in `Content/Content.mgcb`.

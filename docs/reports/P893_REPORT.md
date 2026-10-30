# P893 — positional 3D sound for world sounds: distance rolloff, Doppler, direction cones (SND-0014, 0005, 0006, 0015)

All native cites were re-grepped in `~/Development/Allegiance/src/` and
`~/Development/Artwork/` at the start of this lane. No audio files or
recordings entered the repo; scratch (decompile dumps, probes, run logs) is
git-excluded under `.scratch/p893/`.

## 1. Native model (all cites re-grepped)

### The 3D engine core

| Native item | Cite | Rule |
|---|---|---|
| Per-frame 3D update | `soundengine/ds3dvirtualbuffer.cpp:692-830` (`DS3DVirtualSoundBuffer::Update`) | Every virtual buffer recomputes the listener→source vector each frame and derives gain/audibility/pan from it. |
| Distance rolloff | `ds3dvirtualbuffer.cpp:752-762` | `gainDb = min(0, rolloffFactor · −10 · log10(d² / minDistance²))` — full at/inside the minimum distance, **−20 dB per decade** of d/min beyond. |
| Rolloff factor | engine ctor `soundengine/ds3dengine.cpp:236` (`m_fRolloffFactor(1.0f)`); setter `ds3dengine.cpp:604-613`, interface `soundbase.h:220` | **1.0**; grep shows no caller ever overrides it. |
| Max distance | `ds3dbuffer.h:15` (`c_fMinToMaxDistanceRatio = 4000`), applied `ds3dbuffer.cpp:297,360,563` | `maxDistance = minDistance · 4000`; beyond it the voice is silent. |
| Audibility | `ds3dbuffer.h:12` (`c_fMinAudible = −60`), applied `ds3dvirtualbuffer.cpp:750-753,804-806` | A voice whose total gain ≤ −60 dB is inaudible and gets a **−100 dynamic-priority kick** — the budget path (SND-0001/0002) then never admits it. The −60 dB horizon is therefore d = **1000 · minDistance**. |
| Doppler | engine ctor `ds3dengine.cpp:237` (`m_fDopplerFactor(1.0f)`); setter `ds3dengine.cpp:619-628`, interface `soundbase.h:223`; applied by DirectSound from the buffer velocities (`ds3dbuffer.cpp:540-566` uploads velocity) | **1.0** factor, DirectSound's default 343 m/s speed of sound; no caller overrides the factor. |
| Cones | interface `soundbase.h:56` (`ISoundTweakable3D::SetCone`); math `ds3dvirtualbuffer.cpp:765-793` | Inside `innerAngle`: full. At/beyond `outerAngle`: `outsideGain` dB. Between: **linear in dB**. Skipped for listener-relative sources (TODO-HACK `:775-777`) and for degenerate cones (`outsideGain == 0` or `inner == 360`, `:771-773`). |
| Head-relative / disabled | `ds3dbuffer.cpp:296-299` (`DS3DMODE_NORMAL` / `HEADRELATIVE` / `DISABLE`), `ds3dvirtualbuffer.cpp:720-726` | A source whose position equals the listener's **skips 3D processing entirely** (plays 2D). |

### The listener (per camera mode)

`WinTrek/WinTrek.cpp:646-708` — `CameraListener : ISoundListener`:

- **Position** = `m_camera.GetCameraPosition()` (`:674-679`) — the listener is
  **the camera in every view mode**; cockpit vs chase differ only in where the
  camera sits (no per-mode listener switch exists).
- **Orientation/Up** = camera orientation forward/up (`:661-666, 693-698`).
- **Velocity** = the **own ship's** velocity when its cluster is known, else
  zero (`:686-691`) — native's own `REVIEW` comment notes "the camera's
  velocity is not the same as the ships!". The port reproduces exactly this
  combination (camera pose + ship velocity).
- Registered once via `SetListener(new CameraListener(...))` (`WinTrek.cpp:3026`).

### Emitters

`WinTrek/trekigc.cpp:453-541` — `ModelPointSoundSource`: position =
model position (+ optional model-space offset), velocity = the model's
`GetVelocity()` (zero when the offset is listener-relative), orientation =
model-space forward. Sounds are started with a source via
`StartSound(soundId, psource)` (`WinTrek.cpp:11271-11280`).

### Which sounds are 3D (families → `ThreeDSound(minDistance)`)

All from the native artwork `~/Development/Artwork/sounddef.mdl` (grep
`ThreeDSound(`, 248 hits). The port's families:

| Family | Native template | minDistance | Port voices |
|---|---|---:|---|
| Projectile in-flight ambient | `er_beam` (`sounddef.mdl:14`) | 200 | projectile ambient loops (`erbeam`) |
| Missile in-flight ambient | `HornetMissileFlight` (`:1303`) | 100 | missile ambient loops (`mmissile`) |
| Missile launch | `HornetMissileLaunch` (`:1302`) | 20 | launch one-shot (`lrgslideout`) |
| Explosions | `largeExplosionSound` `:268` / `mediumExplosionSound` `:269` / `smallExplosionSound` `:270` | 1000 / 400 / 200 | tiered explosion one-shots (`explosionshockwave`/`ef24`/`ef120`) |
| Impacts | `myShieldHitSound` `:250-254` / `myHullHitSound` `:255-259` / `otherHullHitSound` `:264` | 200 / 200 / 200 | hit one-shot pools (`hitmeshield1-3`, `hitmearmor1-3`, `ef120`) |
| Rock hit / collision | `rockHitSound` `:266` / `collisionSound` `:308` | 100 / 100 | rock-hit, collision one-shots |
| Warp ambient | `alephSound` `:385` | 200 | warp ambient loops (`alephloop`) |
| Station exterior ambient | `ILStationExternalSound` `:1156` | 200 | station ambient loops (`shipout`) |
| Asteroid ambient | `asteroidSound` `:382` (pitch 0.35) | 200 | asteroid ambient loops (`shipout` @ pitch −0.35) |
| Probe ambient | `er_beam` default `:14` | 200 | probe scanning loops |
| Mining loop | `miningSound` `:379` | 200 | own-ship mining loop (own ship ⇒ head-relative) |
| Ship exteriors (remote engines) | `smallShipExteriorSound` `:1013` (20), `capitalShipExteriorSound` `:1020` (60), thrust `:1014-1015` (100), `:1021-1022` (300), turn `:1016-1017` (60), `:1023-1024` (100) | — | **no remote-ship engine loops exist in the port yet** (the port's only engine beds are the local ship's own, which sit at the listener ⇒ head-relative exactly as native's equal-position rule) |
| Cockpit/interior | `smallShipInteriorSound` `:1018`, `capitalShipInteriorSound` `:1019`, `missileLockSound`/`missileToneSound` `:364-365`, deploy cues `:367-369`, all VO | — | plain `ImportWave`/`LoopingSound` — **not 3D natively**, not 3D in the port |

### Cones: capability exists, artwork never uses it

The `ConeSound(...)` MDL factory is wired (`WinTrek/treksound.cpp:93-120`,
`soundengine/soundtemplates.cpp:267-359`), but **zero** `ConeSound(`
declarations exist anywhere in the native artwork tree. So native ships no
sound that actually carries a cone; the port implements the math faithfully
(tested) and applies none — the same end state.

### Engine quality / hardware options; "3D off"

`WinTrek.cpp:3015-3024`: `UseDSound8` preference, `SoundQuality`
(min/mid/max, cycled in-game `:5355-5382`) and `SoundHardwareAcceleration`
(default off) are the **only** sound options; `SetQuality` trades primary
mix format (`ds3dengine.cpp:492-555`). **Native has no user-facing "3D
sound" on/off**: 3D is structural (a sound is 3D iff its template wraps
`ThreeDSound(...)`); with sound disabled entirely a dummy engine replaces
everything (`WinTrek.cpp:3016-3022`, `dummysound.cpp:17-289`). The P893
`Positional 3D Sound` switch is therefore a **port addition** (default ON =
native behaviour) purely so the 3D mix can be A/B'd by ear against the
pre-P893 curve mix; with it off the mix is structurally the old one.

## 2. Platform check — what MonoGame DesktopGL (OpenAL) actually gives

Measured, not doc-read: the shipped `MonoGame.Framework.DesktopGL 3.8.5.1`
assembly was decompiled (ILspy) and a runtime probe opened the real device
(`.scratch/p893/oalprobe/`):

- `SoundEffectInstance.Apply3D(AudioListener, AudioEmitter)` exists
  (`PlatformApply3D`): it uploads the **listener-space relative position**
  (`emitter.Position − listener.Position` rotated by listener Forward/Up) and
  relative velocity, sets `AL_REFERENCE_DISTANCE = SoundEffect.DistanceScale`
  and `AL DopplerFactor = SoundEffect.DopplerScale` — **both static globals**
  (`SoundEffect.DistanceScale`, `.DopplerScale`, `.SpeedOfSound`, defaults
  1 / 1 / 343.3). Play path sets `AL_DISTANCE_MODEL = InverseDistanceClamped`
  and `AL_SOURCE_RELATIVE = 1`.
- Consequence 1: the OpenAL inverse-distance-clamped model with
  `reference = minDistance`, `rolloff = 1` is **mathematically identical** to
  native's law (`min(0, −10·log10(d²/min²))` dB ≡ amplitude `min(d, min)/d`).
- Consequence 2: `DistanceScale` is a static global read at each `Apply3D`
  call — per-voice minimum distances would need a global-mutation-per-call
  hack across 20+ voices.
- Consequence 3: **cones are not in the XNA API** (no cone setter anywhere in
  MonoGame 3.8.5.1).
- Consequence 4: on this host the real device is OpenAL Soft 1.24.3
  (AL 1.1); `AL_EXT_STEREO_ANGLES` is **present** (MonoGame's own `Pan`
  property works for both mono and stereo sources — it drives AL position for
  mono, stereo angles for stereo), while `AL_SOFT_source_spatialization` is
  **absent**, so positional `Apply3D` on stereo buffers is the weak path —
  and most port cues are stereo oggs.
- MonoGame's source pool is hard-capped at **256** AL sources
  (`OpenALSoundController.MAX_NUMBER_OF_SOURCES`), unlike native's cheap
  software virtual buffers.

**Chosen approach (smallest honest):** drive the 3D mix **ourselves** — the
native rolloff/Doppler/cone math in a pure C# module feeding
`SoundEffectInstance.Volume` / `.Pan` / `.Pitch` per frame — instead of
`Apply3D`. This reproduces native exactly (same rolloff law, own Doppler with
native's factor, cone gain in our own volume path where needed), works for
stereo sources, is fully unit-testable without a device, and keeps the
graceful no-device degradation (pure arithmetic when the manager is inert).
Native DirectSound3D itself just produced gain/pan/frequency per voice — the
port computes the same three quantities on the CPU.

## 3. Design

- **`Audio/Sound3D.cs` (new, pure, device-free)**:
  - `Sound3DModel` — the native constants (rolloff 1.0, Doppler 1.0,
    min→max ×4000, −60 dB audible, 343 m/s) and math:
    `DistanceGainDb` (the `ds3dvirtualbuffer.cpp:752-762` law),
    `DbToLinear`, `ConeGainDb`/`ConeAngleDegrees` (`:765-793`),
    `DopplerPitchFactor` (classic radial model, factor 1.0; approaching ⇒
    upshift; clamped to [¼, 4]) and `DopplerPitchOctaves` (MonoGame's
    octave-based `Pitch`), `Pan` (projection on the camera right axis).
  - `Native3DRole` / `Native3DMix` — the per-family ported
    `ThreeDSound(minDistance)` values with sounddef.mdl cites, plus the
    derived native −60 dB audibility range (1000 × min).
  - `Sound3DMix.Solve` — one world voice per frame: option off ⇒ the base
    state verbatim (structurally today's mix); option on ⇒ native gain,
    audibility, pan and Doppler delta, with native's equal-position rule
    (emitter at the listener ⇒ head-relative full volume).
- **`SoundManager`** — the 3D pass runs inside `ApplyVoiceBudget` (per frame,
  native `DS3DVirtualSoundBuffer::Update` shape): every tracked world voice
  with an emitter gets its velocity estimated by finite difference of its
  streamed position (the 20 Hz wire carries no projectile/missile velocity;
  exponentially smoothed), then `Sound3DMix.Solve` **replaces** the curve
  loudness, feeds `Audible` to the existing voice budget (native's −100
  priority kick ⇒ a 200-ship battle cannot flood voices: desired 16 / max 24,
  unchanged), and stores pan + pitch delta applied in the settle pass. One
  world one-shots (explosions, hits, rock, collision, missile launch) compute
  the native gain at spawn and keep tracking per frame. Cockpit/own-ship beds
  have no emitter and stay head-relative.
- **Listener feeds** — `Game1.FlightInput.cs` sets the velocity (own ship's,
  native's quirk); `Game1.Rendering.cs` sets the camera eye + view basis after
  the smoothed flight camera resolves (the listener IS the camera, all modes).
- **Curve replacement** — while 3D is on, the hand-tuned per-loop linear
  falloffs (missile/projectile/station/asteroid/warp/probe curves) are
  replaced by the native distance model (their volumes only seed the solve
  and stay in force on the option-off path); the planners' start/stop gates
  widen to the native −60 dB range via an optional radius override, and the
  started loops are bounded to the **nearest 64 per family** (the port's
  stand-in for native's software virtual buffers against OpenAL's 256-source
  pool). No curve file changed.
- **Option** — `IntroPreferences.Enable3DSound` (default **true** = native
  default), Sound Options row "Positional 3D Sound" (`OptionsMenuModel`,
  hotkey P), live-applied to `SoundManager.Sound3DEnabled`; persisted in
  player.json.

## 4. Tests

`tests/Allegiance.ClientCore.MonoGame.Tests/Sound3DTests.cs` (22 cases, pure
math, no device):

- rolloff: 0 dB at/inside min, −20/−40/−60 dB at ×10/×100/×1000, monotonic,
  ratio-only scaling, `MaxDistance` ×4000, `AudibleRange` ×1000, dB→linear.
- cones: full inside inner, outside gain at/beyond outer, linear dB fade
  between, degenerate cones silent, acos dot-identity angle.
- Doppler: approaching upshift **343/243 ≈ 1.4118** at 100 m/s, receding
  downshift **343/443 ≈ 0.7742** (sign + size), zero radial ⇒ 1.0, listener
  motion counts, octave conversion.
- pan: right/left/ahead/behind/at-listener.
- `Native3DMix` table vs the sounddef.mdl cites (14 roles).
- mix pass: option-off identity (base loudness, no pan/pitch), head-relative
  at the listener, −40 dB at ×100 min, inaudible past the native range,
  panning by side, approaching pitch-up, no-basis ⇒ centred.
- Existing no-audio-device guard unchanged (SoundManager stays inert; the
  3D pass is skipped when not loaded). `P806OptionsMenuTests` updated for the
  fourth Sound-submenu row.

## 5. Ear-check list (for Domingo — not claimed correct by construction)

With the option ON (default), in a skirmish or training sector:

1. **Remote ship passing left→right** (any nearby ship flying past in chase
   view): engine/exterior ambience aside (no remote engine loops yet — the
   projectile/ambient layer carries the motion), passing bolts (`er_beam`
   loops, min 200) should swell as the ship closes, be loudest broadside, and
   pan hard to the matching ear with a slight downward pitch tail as it
   recedes. Native: same, because the same −20 dB/decade law pans around the
   camera.
2. **Missile approaching then passing**: the `mmissile` loop (min 100) rises
   from −20 dB at 1 km to full at 100 m and closer, pitches **up** on approach
   (factor ≈ 1.2–1.4 for a 60–100 m/s closing rate), then falls and pitches
   **down** after it passes; the launch crack sits at your own nose (min 20 at
   your ship ⇒ head-relative full).
3. **Explosion behind you**: an `ef24`/`explosionshockwave` boom at a ship
   destroyed behind the camera is centred in stereo (native pans
   front/back identically in a stereo down-mix) but attenuated by distance —
   louder as you face the wreck, noticeably quieter than a same-distance
   blast in front only by ear through the doppler of debris cues, not pan.
   Native behaves identically (no rear attenuation).
4. **Station ambient, far vs near** (launch/dock approach): the `shipout`
   hum (min 200) is faint but present from tens of kilometres (−20 dB at
   2 km, −40 dB at 20 km) and swells smoothly on approach — much wider range
   than the old 5 km hard cutoff. Native: same curve, same horizon (−60 dB at
   200 km).
5. **Chase vs cockpit camera**: stationary world ambients (station hum,
   warp `alephloop`) sound identical in both views only while the camera
   sits near the ship; in chase the camera hangs behind you, so a station you
   fly past reads slightly later/softer than in cockpit. Native: identical —
   the listener is the camera in both.
6. **Asteroid field fly-through** (min 200 per rock): individual rock rumbles
   pan across the ears and stack into a moving bed; only the nearest few are
   audible under the 16-voice budget — the far ones are muted, not dropped.
7. **Mining**: the mining loop stays a cockpit-range bed (own ship ⇒
   head-relative, native's equal-position rule) regardless of camera.
8. **Option off** (Sound Options → Positional 3D Sound → off): the mix must
   sound exactly as before this task — centred, linear curve falloffs, no
   Doppler.

## 6. Cost

Bounded measurement from the offscreen run (below): the periodic budget
telemetry now carries a `3D=N` count — **26 positioned voices solved per
frame** in the training sector (station + 25 rock ambients within the native
range). Per voice the solve is one subtract, one length, one `log10`, one
`pow`, one dot (≈ 20 flops) plus the emitter finite-difference: ~500 flops
per frame at 26 voices, no allocations (structs, no LINQ in the pass). The
started-loop caps (nearest 64 per family) bound the worst case; the budget
still admits only 16. No server change.

## 7. Verification

| Check | Result |
|---|---|
| `~/.dotnet/dotnet build AllegianceCore.slnx` | PASS, **0 warnings / 0 errors** |
| `tools/build-tools.sh` | PASS, all 94 tool projects |
| New `Sound3DTests` ×3 alone | PASS 22/22, three consecutive runs |
| ONE `tools/run-tests.sh --tier fast` | see below |
| Offscreen client run, sound on (`SDL_VIDEODRIVER=offscreen`, training mission, `ALLEGIANCE_AUDIO_TELEMETRY=1`, `--exit-after 210`) | PASS — exit 0, no audio exceptions, log shows `station ambient start`, 25× `asteroid ambient start`, and 43 budget frames with `3D=26` positioned voices active, `audible=8/16` |
| `scripts/parity-scorecard.py` | PASS (validated); soundengine area **64.81% (35/54) → 85.19% (46/54)** |
| `tools/check-glossary-links.py` | PASS (348 paths) |

Fast sweep `tiers-20261030T035703` (mid-lane): 11,763 tests, 1 real P893
failure (`P806OptionsMenuTests` row count — test updated in this lane, now
passing) plus ledgered pre-existing/contention failures; each non-task
failure isolated 3× alone and recorded in `docs/TEST_FLAKES.md`
(`DeadLatchRespawnLiveTests` 3/3 pass alone; `P739TimerLiveTests` 5/5 pass
alone; `MissileLockLiveTests`/`NpcWingmanDefendLiveTests`/quarantine
`AddPartTests` are the known ledgered baselines).

Confirming fast sweep `tiers-20261030T041824` (after the test fix): 11,763
tests, 5 failed + 6 timing offenders — **all pre-existing/contention, none on
the P893 path**; every new name passed 3/3 alone immediately (see the P893
entry in `docs/TEST_FLAKES.md`). The P893-specific tests all pass in-sweep.
No second erasure run was used; the failures are not claimed fixed.

## 8. Matrix before/after

`docs/parity/soundengine-matrix.csv` rows updated with the evidence above:

- **SND-0005** (3D attenuation/rolloff): partial → **implemented**.
- **SND-0006** (Doppler): missing → **implemented**.
- **SND-0014** (positional 3D for world sounds): partial → **implemented**.
- **SND-0015** (directional cone): missing → **implemented** (engine-level
  math ported + tested; zero artwork users, exactly like native).

soundengine area score: **64.81% (35/54) before → 85.19% (46/54) after**.

## Native-intent footer

**Classification:** native behaviour restoration. The 3D model (rolloff law,
per-family minimum distances, Doppler factor, cone math, camera listener with
the own-ship velocity quirk, equal-position head-relative rule) is ported
from the native soundengine/WinTrek lines cited in §1; the era constraint is
that native did all of this inside DirectSound3D hardware mixing with cheap
software virtual buffers — the port reproduces the same gain/pan/pitch per
voice on the CPU against OpenAL, bounds started loops to the nearest 64 per
family where native trusted unbounded software buffers, and widens loop
admission to native's −60 dB horizon only while the option is on. The bug in
native this lane deliberately does NOT port: none found — but the *absence*
of a native 3D toggle means the `Positional 3D Sound` switch (default on) is
a modern-approach addition for ear A/B, and the cone capability is ported
although no artwork sound ever used it natively (matching native's own dead
feature). Sounds are unverified by ear by construction; the §5 list exists so
Domingo can judge the mix.

P902 — effects and sound parity
==============================

2026-10-31, Codex lane, branch `codex/p902`. Changes are left in the working
tree. No commits, pushes, rig/mini/Mac access, production server changes, or simulation
changes were made.

Effects reaches **100% (18/18)** from **88.89% (16/18)**. Sound reaches
**100% (54/54)** from **85.19% (46/54)**, including **one explicitly reported
replacement point** for the DirectSound primary-buffer format. The replacement
has real production voice-budget controls and tests; it does not claim that
OpenAL's device format changes. Full [before](P902_assets/scorecard-before.txt)
and [after](P902_assets/scorecard-after.txt) scorecards are retained. Only the six
requested parity rows changed semantically; CSVs use LF.

Native citations below are relative to the read-only native `src/` tree. Line
numbers were re-read against the local source, including corrections to the
brief's older pitch-template range.

| Row | Verdict and production code | Native citation | Regression evidence |
|---|---|---|---|
| EFX-0015, w2 | Implemented. Validated cone parameters, facing and effect gain enter the actual mix and audibility gate through `StartSound` / `ConfigureEffectSource`. [SoundCone](../../src/Allegiance.ClientCore.Game/Audio/SoundTemplates.cs:21), [mix](../../src/Allegiance.ClientCore.Game/Audio/Sound3D.cs:286), [dispatch](../../src/Allegiance.ClientCore.Game/Audio/SoundManager.cs:1412). | `effect/efsound.cpp:82-100,133-190`; `soundengine/ds3dvirtualbuffer.cpp:764-804,859-879`. | `Cone_enters_the_production_mix` table (0/30/60/75/90/120/180 degrees), `Cone_can_make_a_voice_inaudible_but_is_skipped_at_listener`; real backend/manager proof. |
| SND-0008, w2 | Implemented. [AsrRegion / AsrCursor](../../src/Allegiance.ClientCore.Game/Audio/AsrSound.cs:54), [playback](../../src/Allegiance.ClientCore.Game/Audio/AudioVoice.cs:81), and [manager adapter](../../src/Allegiance.ClientCore.Game/Audio/SoundManager.cs:566). Shipint and projectile whole-wave loops use this primitive; weapon ASR uses authored regions. | `soundengine/ds3dengine.h:181-186`; `ds3dvirtualbuffer.cpp:41-133`; `soundtemplates.cpp:638-659`. | `Asr_plays_attack_then_repeats_sustain_then_tail`, `Asr_stop_during_attack_skips_sustain_and_force_stop_is_immediate`, `Whole_wave_adapters_keep_shipint_and_projectile_sample_order`; sustained backend playback and release. |
| SND-0016, w2 | Implemented. [PitchSoundTemplate](../../src/Allegiance.ClientCore.Game/Audio/SoundTemplates.cs:7) multiplies native playback ratios and converts them to MonoGame octaves. Authored station, asteroid and capital exterior beds and numeric template dispatch use it. | `soundengine/soundtemplates.cpp:282-365`, especially `SetPitch` at 302-304; `effect/efsound.cpp:107-130`; shipping `sounddef.mdl:382,1020,1032-1166`. | `Pitch_wrapper_scales_frequency`, `Pitch_is_applied_only_to_authored_templates`; generated metadata and backend proof. |
| SND-0020, w2 | Implemented, with a documented undefined-memory bug correction. [RepeatFirePcmCache](../../src/Allegiance.ClientCore.Game/Audio/AsrSound.cs:111) caches aligned, saturated overlap PCM; [StartWeaponBurst](../../src/Allegiance.ClientCore.Game/Audio/SoundManager.cs:1940) selects native repeat-fire versus ASR templates. | `soundengine/soundtemplates.cpp:986-1103,1133-1232,1245-1367`; shipping `sounddef.mdl:901-941`. | `Repeating_fire_overlaps_saturates_aligns_and_caches`, `Native_rapid_weapons_use_repeat_pcm`, `Native_asr_weapons_keep_authored_regions`, allocation test, real weapon/backend proof. |
| SND-0022, w1 | Implemented at the managed audio boundary. [AudioDevice](../../src/Allegiance.ClientCore.Game/Audio/AudioDevice.cs:7) latches one initialization attempt shared by SFX/music. Failed initialization leaves public calls inert; frame failure also disables further mixing. | `soundengine/dummysound.cpp:17-83,139-181,267-292`. | `Device_failure_keeps_sfx_music_and_updates_silent_without_retries`; real unavailable-driver proof and a launched local Game1 mission with SFX calls every frame. |
| SND-0025, w1 | Applicable behavior implemented; device-format portion **design-replaced**, separately counted. [Presets](../../src/Allegiance.ClientCore.Game/Audio/SoundTemplates.cs:42), [live budget](../../src/Allegiance.ClientCore.Game/Audio/SoundManager.cs:550), [shared menu](../../src/Allegiance.ClientCore.Game/OptionsMenuModel.cs:144), [command/persistence](../../src/Allegiance.ClientCore.Game/Game1.BenchmarkSharing.cs:236), [preferences](../../src/Allegiance.ClientCore.Game/IntroPreferences.cs:80). | `soundengine/ds3dengine.cpp:333,492-550`. | `Quality_menu_persists_all_three_real_budgets` for Classic and Modern; real Game1 command/save/reload assertions and six captures. |

All named regression tests are in
[P902SoundTemplateTests.cs](../../tests/Allegiance.ClientCore.MonoGame.Tests/P902SoundTemplateTests.cs).
The executable proof is [tools/p902-audio-proof](../../tools/p902-audio-proof/Program.cs).

**Behavior and backend details**

Cone interpolation is linear in decibels, using native angles directly, without
halving them. At the listener, cone attenuation is skipped. No bundled native
sound template declares a cone, so existing sounds acquire no invented
orientation/directionality. Optional effect cones nevertheless reach the real
production mixing path, rather than stopping at an unused math helper.

Native `PitchSound` has no random-variation behavior. Its template factor
multiplies runtime pitch. Shipint retains the fixed native **0.5 frequency
ratio**, correctly represented by **-1 octave**, rather than the old -0.5
octave conversion. The asteroid's 0.35 factor becomes about -1.514573 octaves.
Capital and station exterior beds use their authored 0.5 factors. The engine
control curve is retained, and no random pitch is added to weapons.

ASR plays the attack once, repeats the sustain region, then finishes the tail
after a soft stop. Stopping during attack completes the attack and skips the
sustain; a forced stop is immediate. Whole-wave adapters preserve shipint and
projectile sample order. Decoded PCM and repeat-fire templates are cached;
ordinary distance/volume planners are retained.

The shipping pipeline already provides uncompressed, 16-bit PCM SoundEffect
XNB files. The new reader consumes those files at runtime, including absolute
content roots used by proofs; it does not require original Ogg files in a
player bundle. Template metadata is generated from shipping `sounddef.mdl` by
[scripts/generate-client-sound-templates.py](../../scripts/generate-client-sound-templates.py):
94 authored ASR/repeat/pitch entries, with 61 bundled valid entries checked in
the backend proof. The existing small-turret declaration requests
0.341633 + 0.224671 seconds from a 0.557279-second wave. It is rejected, matching
native's out-of-range check; this task does not silently retune that template.

MonoGame DesktopGL ignores authored loop offsets and treats `Stop(false)` as
an immediate stop. Its public dynamic submission path also allocates a new
native-buffer wrapper for every submission. These constraints are visible in the upstream [SoundEffect](https://github.com/MonoGame/MonoGame/blob/v3.8.5/MonoGame.Framework/Platform/Audio/SoundEffect.OpenAL.cs), [SoundEffectInstance](https://github.com/MonoGame/MonoGame/blob/v3.8.5/MonoGame.Framework/Platform/Audio/SoundEffectInstance.OpenAL.cs), and [dynamic submission](https://github.com/MonoGame/MonoGame/blob/v3.8.5/MonoGame.Framework/Platform/Audio/DynamicSoundEffectInstance.OpenAL.cs) implementations. The desktop ASR adapter instead
borrows a source through a MonoGame dynamic voice and reuses three native
buffers. A single `UnsafeAccessor` for the pinned MonoGame 3.8.5.1 `SourceId`
field bridges source ownership; no reflection or per-frame buffer allocation
is involved. Framework upgrades must rerun the backend proof. Mobile heads
retain the public framework streaming path; mobile playback was not exercised
in this Linux lane.

Three 20 ms chunks bound already-submitted audio to 60 ms at unity pitch;
release follows buffered device playback. Pure cursor/cache-hit tests and 120
real desktop refill frames both measured **0 managed bytes after warmup**.
This does not claim that the pre-existing voice-budget pass allocates nothing;
the new steady refill/mix path adds no managed frame allocations.

The signed-16 native repeat-fire overlap loop at
`soundengine/soundtemplates.cpp:1205` compares `pwAdd < pwSample2Start` after
initializing `pwAdd = pwSample2Start`. It never executes, and later code reads
that uninitialized region. The port deterministically fills the intended
saturated overlap. Undefined native bytes cannot be reproduced meaningfully;
the well-defined cache key, PCM size, two-delay attack offset, one-delay
sustain, and release behavior are preserved and tested.

**Quality settings and screenshots**

| Preset | Native primary format | Native software desired / maximum | Actual OpenAL setting |
|---|---|---|---|
| Low | 22050 Hz, 8-bit, mono | 8 / 8 | Admit 8 ranked voices |
| Medium (default) | 22050 Hz, 16-bit, stereo | 16 / 24 | Admit 16 ranked voices |
| High | 44100 Hz, 16-bit, stereo | 24 / 32 | Admit 24 ranked voices |

The maximum values are retained in configuration; native uses them for capacity
reservation, while desired count drives admission. Existing essential-bed and
category policies remain. OpenAL and the host own the device mix format: these
controls do not advertise a sample-rate, bit-depth, channel, or hardware-mode
change. The matrix records this era constraint as `design-replaced`, rather
than crediting an unimplemented DirectSound format switch as exact parity.

All captures are actual 1280x720 Game1 backbuffers, opened and visually
inspected. Both HUD styles use the shared Options renderer, so their menu
appearance is the same. Each shows the Sound Options title, music rows,
positional sound toggle, highlighted quality row, and keyboard navigation;
there is no clipping. Quality was changed through the production command and
verified by reloading preferences after every selection.

| Preset | Classic | Modern |
|---|---|---|
| Low | [8 voices](P902_assets/sound-classic-low.png) | [8 voices](P902_assets/sound-modern-low.png) |
| Medium | [16 voices](P902_assets/sound-classic-medium.png) | [16 voices](P902_assets/sound-modern-medium.png) |
| High | [24 voices](P902_assets/sound-classic-high.png) | [24 voices](P902_assets/sound-modern-high.png) |

The separate [no-device flight capture](P902_assets/no-device-flight-modern.png)
was also opened and inspected. It shows the Ganymede cockpit scene, ship speed 153 m/s, minimap and Iron Coalition mission briefing with a five-second UI countdown still overlaid. The proof separately asserts that the server mission is Started and the pilot is undocked; the overlay is not evidence of combat or of an entire completed match.

**Validation**

| Gate | Result |
|---|---|
| Solution build | Pass, 0 warnings / 0 errors |
| `tools/build-tools.sh` | All 101 projects build; P902 proof rebuilt after its final flight assertions |
| Server Release linux-x64 self-contained AOT publish | Pass; no IL2026/IL3050 diagnostics |
| New tests alone | 32 cases pass on each of 3 runs |
| Affected options tests alone | 7 cases pass on each of 3 runs |
| Affected 3D tests alone | 22 cases pass on each of 3 runs |
| Actual PCM/backend proof | 3/3; 120 refill frames allocate 0 bytes on each run; authored ASR/repeat weapons, shipint, coned effect and release complete |
| Actual failed-device proof | 3/3; repeated SFX/music/frame/load calls, exactly one diagnostic per process |
| Real Game1 no-device local flight | Pass; Started mission, launched pilot, 1,139 flight frames over 10 seconds, SFX calls every frame, one unavailable-device diagnostic. Baseline proof explicitly selects parallel build and pool transport; native ship-update/GotoPlan defaults are retained. |
| One Fast tier | Completed once: 12,737 cases in 544.3 s; 12,730 passed, 4 failed, 3 skipped; 2 timing offenders. All P902 cases pass. Non-task failures and three-run isolations are ledgered below. |
| Showcase never-parks | Pass 5/5; TRX durations 00:03:44.9603655 / 00:03:58.2663281 / 00:03:35.1394481 / 00:03:50.1061572 / 00:05:32.8572615. Exact per-run idle/combat metrics are in validation.json. |
| Glossary, parity, hygiene checks | Pass: glossary checks 421 paths, parity scorecard validates all matrices, only six intended rows change, LF and working-tree hygiene checks pass; no commit-range publish guard is claimed. |

The Fast sweep is **not green**. Its four failures are unrelated to this lane:

| Failure / timing offender | Three isolated results | Disposition |
|---|---|---|
| P761 profile-editor test | pass / pass / pass using writable task-local `XDG_CONFIG_HOME` | Existing fixture tries restoring the read-only default user profile; original Fast failure retained. |
| P890 skirmish/port sustained aim | pass / fail / pass (four theory cases per run; other cases pass) | Intermittent drift in untouched prediction/server paths. |
| Missile-lock partial-tip test | pass / fail / pass | Existing intermittent missing tip; failing run takes 38.522 s despite lock reaching 1.000. |
| NPC wingman disengage/return | fail / fail / fail | Already-ledgered persistent baseline null-charge failure in unchanged simulation code. |
| P777 warp-exit measurement (passes, exceeds Fast timing limit) | pass / pass / pass, 10.077 / 10.114 / 10.071 s | Remains above the 10.000 s timing classification limit. |

See [TEST_FLAKES.md](../TEST_FLAKES.md) for exact method names and evidence.
These failures were recorded without retuning simulation, changing unrelated
fixtures, reclassifying unrelated rows, or running a second Fast sweep.

Raw logs and TRX remain in task-local scratch, outside the public evidence
assets: [build](../../.scratch/p902/build-final.log),
[tools](../../.scratch/p902/build-tools.log),
[AOT](../../.scratch/p902/server-aot.log),
[isolated tests](../../.scratch/p902/isolated/),
[backend runs](../../.scratch/p902/audio-backend-1.log),
[forced failure](../../.scratch/p902/device-failure-1.log),
[local flight](../../.scratch/p902/no-device-flight-final.log),
[Fast](../../.scratch/p902/fast.log), [complete Fast artifacts](../../.scratch/p902/fast-tier/), and
[showcase runs](../../.scratch/p902/never-parks/).
The completed runner artifacts were relocated from the runner's default TestResults directory into `.scratch/p902/fast-tier/`; the original sweep ID is retained in the flake ledger. The compact [validation summary](P902_assets/validation.json) records all gate outcomes and showcase idle/combat metrics. Public evidence stays below 2 MB. New captures are available for
media review before any later push; this lane performs no push.

**Not done and why**

No primary-device format/hardware control is fabricated for OpenAL. No random
pitch is added, because native has none. No arbitrary cones are assigned to
shipping sounds. Native's undefined signed-16 memory read is not reproduced.
The native dummy engine's object graph is not recreated: managed game callers
already accept absent audio instances, and the shared, latched no-device gate
provides the requested silent gameplay behavior. Android/iOS, physical audio
hardware, and remote machines were not tested; this lane's backend validation
uses the real OpenAL null driver and a deliberately unavailable driver.
No unrelated parity rows, server/simulation rules, or deployment settings were
changed.

**Native-intent footer**

| Rule | Native intent / constraint | Modern approach |
|---|---|---|
| Cone and gain | Design: directional emitters and decibel attenuation participate in audibility/priority. | Keep shared, allocation-free mix math and explicit emitter parameters. |
| ASR | Design: seamless authored lead-in/sustain/tail with graceful stop. | A reusable PCM cursor and buffered playback; a future framework should expose loop/source ownership publicly. |
| Pitch | Design: multiplicative frequency ratios, not random variety. | Convert ratios once to backend units; compose Doppler additively in octaves. |
| Repeat fire | Design: amortize rapid-shot overlap and cache PCM. Bug: signed-16 first overlap loop reads undefined bytes. | Cache deterministic saturated PCM; fix the undefined overlap rather than imitating arbitrary memory. |
| No device | Era constraint: absent sound card must not prevent play. | One shared device attempt and a silent, null-safe audio boundary; test forced failure. |
| Quality | Era constraint: DirectSound primary-buffer fidelity/CPU tradeoff and hardware-dependent buffer counts. | Keep meaningful voice budgets while leaving output format/resampling to the host mixer; expose a format selector only if the backend can actually implement it. |

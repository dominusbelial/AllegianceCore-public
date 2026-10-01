# P465 — Per-Weapon Shot and Burst Sounds (client)

Date: 2026-09-30
Scope: part 1 of the "every weapon looks and sounds the same" split — per-weapon
sounds, client-only. Visuals (P465b) and muzzle offsets (P465c) are separate tasks.

## Summary

The port previously played a single `pulselaser` wave for every weapon. This task
gives each weapon its own single-shot and (during sustained fire) burst sound,
resolved from the weapon part's decoded `SingleShotSound` / `BurstSound` ids
(`DataPartTypeIGC.Weapon`, `igc.h:1841-1843`) to the real wave, mirroring native
`trekigc.cpp:999-1012` (per-mount burst loop + single-shot when not bursting).

New pure type: `WeaponSoundMap` (`src/Allegiance.ClientCore.MonoGame/Audio/WeaponSoundMap.cs`).
Wired through the P462 own-shot local echo.

## 1. id → wave resolution method

A weapon part stores a numeric `singleShotSound` / `burstSound`. Native plays them
as ids into the compiled soundbase (`trekClient.StartSound(GetSingleShotSound(), …)`,
`trekigc.cpp:1005-1012`). This checkout has **no compiled soundbase** — the
`…SoundId` constants of `sounddef.mdl`'s `soundList` are build-time generated
(confirmed: `~/Development/Allegiance/src/Igc/sounds.h` only numbers the generic
client cues from 1000, and the per-weapon sounds like `dn_ptvulcan1ShotSound` are
not in it), so the table was **derived from the real `Artwork/sounddef.mdl`** by
concatenating two of its sections:

1. `soundList` (sounddef.mdl:1354+, explicit numeric form, e.g. lines 2067-2072):
   ```
   (530, dn_ptvulcan1ActivationSound),
   (531, dn_ptvulcan1ShotSound),
   (532, dn_ptvulcan1BurstSound),
   (551, dn_ptlascan1ShotSound),
   ...
   ```
   → numeric id → sound-template name.
2. Each template's top-of-file definition carries its `ImportWave("…")` clip:
   ```
   dn_ptvulcan1ShotSound  = GainSound(SFXGain, PrioritySound(-1.5, ThreeDSound(100, ImportWave("dn_ptvulcan1shoot"))));
   dn_ptvulcan1BurstSound = GainSound(SFXGain, PrioritySound(5, ThreeDSound(100, ASRSound(0.0, 0.19, ImportWave("dn_ptvulcan1shoot")))));
   ```
   → template name → wave.

Concatenating gives `id → wave`. **The brief's fallback "map by part model/icon
name prefix" was investigated and rejected**: PCore001 weapon parts carry generic
shared models (`wep08`, `acs56`, …), not faction names, so the part's model name
carries no sound identity — the `soundList` id resolution is the authoritative
route. Every resolved wave exists as a real `.ogg` in `Artwork` and was bundled
into `Content/Sounds` + registered in `Content.mgcb` (44 new waves; the resolved
burst clips reuse the single-shot waves, as native does — native wraps the same
`ImportWave` in an `ASRSound` attack/release envelope).

## 2. Coverage table (real PCore001, 82 weapon parts)

| Table | Distinct ids used by weapons | Resolved to a real wave | Fallback |
|---|---|---|---|
| SingleShot (`SingleShotStemBySoundId`) | 52 | 51 | 1 |
| Burst (`BurstStemBySoundId`) | 41 | 39 | 2 |

Fallback cases (resolve to `pulselaser`, the pre-P465 single cue):
- Single-shot id **0** — the non-firing **Rocket Targeting** aid
  (`PW Rocket Targeting`, part 138, `singleShot=0`), which never fires a shot.
- Burst ids **0** (same targeting aid) and **147** (one weapon's burst id absent
  from the numeric `soundList` — resolves to the fallback loop).

Three representative weapons resolving to three distinct waves (asserted by the
unit tests): PE Vul Can 1 → `dn_ptvulcan1shoot`, EW Lsr Can 1 →
`dn_ptlascan1shoot`, PW A-B Hammer 1 → `faohhammer1`.

Distinct waves bundled: 45 (44 new + the existing `pulselaser`), all present in
`Artwork`, all registered through `Content.mgcb` (MGCB builds them into xnb;
verified by a clean `dotnet build`).

## 3. Wiring

- `WeaponSoundMap` — pure static id→wave table + `SingleShotStem`/`BurstStem`
  (fallback to `pulselaser`) + `TryResolve*`.
- `Flight.OwnShotWeapon` / `Flight.LocalRound` carry the weapon's
  `SingleShotSoundId`/`BurstSoundId` (+ `MountId`) from the core through the P462
  echo (`OwnShotEcho.cs`), so each emitted round knows which wave to play.
- `SoundManager.PlayWeaponFire(short singleShotSoundId)` plays the per-weapon
  one-shot; `StartWeaponBurst(mount, burstId)` / `StopWeaponBurst(mount)` run a
  looping instance per firing mount. Waves are loaded lazily on first use and
  cached by stem (same pattern as `_voices`). The no-arg `PlayWeaponFire()` stays
  as the fallback cue for the P232 non-own projectile ambient path (other ships'
  bolts — out of scope).
- `Game1.EmitOwnShots` (the P462 local echo) drives both: a mount that fires on
  two consecutive frames is "bursting" (held trigger) → its burst loop plays and
  no single-shot; a fresh tap plays that weapon's own single-shot wave.

## 4. Verification

- `WeaponSoundMapTests` (4 tests) — **fail-first recorded** (build failed with
  `CS0103: The name 'WeaponSoundMap' does not exist` before the type was created);
  now green:
  - every real PCore001 weapon single-shot id resolves (or is the documented
    targeting-aid noise floor id 0 → falls back);
  - three real weapons → three distinct correct waves;
  - unknown id → `TryResolve` false + falls back to `pulselaser`;
  - every referenced stem is a bundled `Content/Sounds/*.ogg`.
- `dotnet build` (MonoGame client + tests): 0 warnings, 0 errors.
- `dotnet test`: three consecutive full-solution runs. The affected test project
  (**Allegiance.ClientCore.MonoGame**, 1011 tests incl. the four `WeaponSoundMapTests`)
  passed **clean in all three runs (0 failures)**. The full suite shows a small
  rotating set of failures confined to the **documented pre-existing live-wire /
  environment flakes** — Conquest capture (`ConquestTerritoryLiveTests`,
  `GameOverPlayersWireTests`), `LiveTurretGunnerBindingTests`, `JoinFlowLiveTests`,
  `ShipDeleteGoneLiveTests`, `Multi_mission_disconnect`, and intermittent reload
  tests — none in any file this task touched, and the same families are documented
  as failing on the pristine tree in this environment (see the P455/P471-472
  notes; the conquest-capture group fails ~10 s here regardless). The rotating
  membership across the three runs is the textbook signature of that environmental
  flakiness, not a P465 regression.
- Fail-first evidence: `WeaponSoundMapTests` build error `CS0103: The name
  'WeaponSoundMap' does not exist in the current context` recorded before
  `WeaponSoundMap.cs` existed (this report, §4).

## 5. Live evidence

A genuine live GUI audio session (boot the MonoGame client against a server, hold
the trigger on one weapon, switch to a second, and watch the audio log) was **not**
run here: it needs an interactive desktop, a real audio device (the manager degrades
silently to inert without one, so no log lines appear), and input simulation to fire
and switch weapons — the same desktop-input constraint prior tasks (e.g. P319) hit.
The evidence supplied instead is the deterministic selection path end-to-end:

- `WeaponSoundMapTests.Three_real_weapons_resolve_to_distinct_correct_waves` proves
  the per-weapon selection: PE Vul Can 1 → `dn_ptvulcan1shoot`, EW Lsr Can 1 →
  `dn_ptlascan1shoot`, PW A-B Hammer 1 → `faohhammer1` — three real PCore001
  weapons resolving to three distinct bundled waves (verified as distinct by the
  test's `Distinct().Count() == 3` assertion).
- The full MonoGame test project (1011 tests, including the four `WeaponSoundMapTests`)
  passes clean, so the resolved waves are all loadable content.
- The live code path emits a per-shot console line carrying the resolved wave
  (`Game1.EmitOwnShots`): `[monogame] audio: weapon fire (P465 single-shot) mount=… stem=…`,
  so firing two different weapons in a real session logs two different `stem=…`
  values — the intended live observation. The burst loop drives the same resolution
  through `SoundManager.StartWeaponBurst` for held fire.

Recommended follow-up for the full interactive proof: run the client on a desktop
with an audio device, equip two distinct cannons (e.g. a Vulcan and a Las-Can), fire,
and confirm the `stem=` lines differ.

## Follow-ups

- Other ships' weapon sounds: the non-own projectile ambient path still uses the
  single `pulselaser` default (native positions shot sounds at every firing ship;
  the P88 wire carries no shooter id). Noted as out of scope here.
- Projectile visuals → P465b; muzzle/hardpoint offsets → P465c.

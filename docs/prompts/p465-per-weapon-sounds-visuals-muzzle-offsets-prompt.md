# P465 — per-weapon shot and burst sounds

This is part 1 of a three-way split of the "every weapon looks and sounds
the same" finding:
- **P465 (this task):** per-weapon sounds, client-only.
- **P465b:** per-type projectile visuals, client-only (`p465b-per-type-projectile-visuals-prompt.md`).
- **P465c:** muzzle and hardpoint offsets, server sim (`p465c-weapon-muzzle-offsets-prompt.md`).

## Why this task exists

This comes from the 2026-09-29 gameplay-parity pass.

- **Native gives each weapon its own sound.** Every weapon part carries
  `activateSound`, `singleShotSound` and `burstSound` ids. The client plays
  the single-shot sound on each shot and the burst sound during sustained
  fire (`WinTrek/trekigc.cpp:1005-1012`, `fFiringShot`/`fFiringBurst`).
  `~/Development/Artwork/sounddef.mdl:38-163` defines about 60 distinct
  weapon sounds. Examples: `dn_ptvulcan1ShotSound`/`BurstSound`,
  `dn_ptlascan1ShotSound`, `faohhammer1ShotSound`. The `.ogg` files exist
  in `~/Development/Artwork`, for example `dn_ptvulcan1shoot.ogg`.
- **The port has one sound for everything.** It plays `pulselaser`
  (`src/Allegiance.ClientCore.MonoGame/Audio/SoundManager.cs:223,386`) for
  every weapon. The ids are already decoded:
  `DataPartTypeIGC.Weapon` has `ActivateSound`/`SingleShotSound`/`BurstSound`
  (`src/Allegiance.Core/Data/DataPartTypeIGC.cs:87-89,392-394`), and the
  client loads the core (`Rendering/ShopCatalog.cs:57`).
- **Known uncertainty:** the numeric sound id → sound-name table isn't in
  this checkout. `sounddef.mdl`'s `soundList` (`:1353+`) maps
  `(xxxSoundId, xxxSound)` pairs, but the `…SoundId` numbers themselves
  live in a generated header that's absent here.

## Scope

1. **Resolve id → wave**, in this order:
   - (a) Find the real numeric id table: search `~/Development/Allegiance`,
     the Artwork folder, and any `soundbase`/`sounds` generated file.
   - (b) If it doesn't exist, derive it from the part data instead. Map each
     weapon part to its wave by the part's model/icon name prefix, for
     example part `dn_ptvulcan1` → `dn_ptvulcan1shoot.ogg`, and cross-check
     against `sounddef.mdl` `ImportWave` names.

   Either way, write a small `WeaponSoundMap` (pure, testable) and document
   which method was used and its coverage (N of M weapon parts resolved).
2. Load the resolved waves through `Content/Content.mgcb`. Play the
   per-weapon single-shot sound when our own shot fires. Hook the same
   trigger point that plays `PlayWeaponFire` today; if P462 has landed,
   that's the local echo. Play the burst sound as a loop while sustained
   fire continues, if native does that; confirm in `trekigc.cpp`.
3. **Fallback:** any unresolved weapon keeps `pulselaser`.

## Explicitly out of scope

- Other ships' weapon sounds beyond whatever the existing projectile
  ambient path does. If native plays shot sounds for other ships too, note
  it as a follow-up.
- Projectile visuals (P465b) and muzzle offsets (P465c).
- Missile and expendable sounds, which already have their own cues.

## Verification

- Unit tests for `WeaponSoundMap`. At least three real PCore001 weapons
  should resolve to distinct correct waves, and an unknown weapon falls
  back. The tests **fail first** because the type doesn't exist yet; record
  that run.
- Live: fire two different weapons and confirm distinct sounds in the
  MonoGame audio log.
- `/home/domingo/.dotnet/dotnet build` → 0 warnings, 0 errors.
  `/home/domingo/.dotnet/dotnet test` → 3 consecutive clean runs.

## Deliverables

1. Committed on **`master`**, not pushed. Check the branch first.
2. Report `docs/reports/P465_PER_WEAPON_SOUNDS.md`: the id-resolution
   method, the coverage table, the fallback count and the live evidence.

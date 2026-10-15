# P463 — hit sounds (my/other × shield/hull) and hit feedback on shield-absorbed hits

This is part 1 of a three-way split of the "hit feedback" finding:
- **P463** (this task): the hit signal, the four hit sounds, and the damage flash on shield hits.
- **P463b**: the shield-hit flare visual. It consumes this task's signal.
- **P463c**: the collision and rock-hit sounds.

## Why this task exists

This came out of the 2026-09-29 gameplay-parity pass (weapons feel).

**Native feedback:**
- `RegisterHit` (`~/Development/Allegiance/src/WinTrek/trekigc.cpp:2365-2393`) plays one of four sounds:
  - `myShieldHitSound` or `myHullHitSound` when the struck ship is ours
  - `otherShieldHitSound` or `otherHullHitSound` otherwise
- The choice is keyed on `bAbsorbedByShield`, and each ship plays at most one hit sound per frame (`m_bPlayedHitSoundThisFrame`). Stations use the "other" pair (`clintlib/clintlib.cpp:3084-3086`).
- Sound definitions (`~/Development/Artwork/sounddef.mdl:250-264`):
  - `myShieldHitSound` picks randomly from `HitMeShield1-3`
  - `myHullHitSound` picks randomly from `HitMeArmor1-3`
  - `otherShieldHitSound` is `myShieldHitSound` at priority -1.6
  - `otherHullHitSound` is `ef120`
- All of these waves exist as `.ogg` in `~/Development/Artwork` (`hitmeshield1-3`, `hitmearmor1-3`, `ef120`).
- The screen damage flash arms on **any** damage (`flAmount > 0`, `trekigc.cpp:4278-4283`), shield-absorbed hits included.

**What the port has:**
- No hit sounds at all. None are loaded in `src/Allegiance.ClientCore.MonoGame/Audio/SoundManager.cs`. Client sounds go through `Content/Content.mgcb` into `Content/Sounds/`.
- `DamageFlashTracker` (`Flight/DamageFlashTracker.cs`) arms only when the **hull** fraction drops, so a shield-absorbed hit gives no flash.
- The only hit wire signal is P364's `PROJECTILE_HULL_HIT(213)`:
  - raised by `Sector.MaybeRaiseProjectileHit` (`src/Allegiance.Core/Model/Sector.cs:~1929-1946`)
  - only for `dr == HullDamage || Killed` (`:1938`)
  - broadcast by `Server.Combat.cs:126-160`

  A shield-absorbed hit sends the client nothing.

## Scope

1. **Hit signal for shield-absorbed hits.** Extend the P364 path so a projectile hit that `ShieldDamage`s a ship or station is also raised and broadcast, carrying an "absorbed by shield" flag.
   - `ProjectileHullHit` is port-own (no native `DEFINE_FEDMSG`, `Wire/ProjectileHullHit.cs`), so add a flag byte there, or add a sibling message. Choose one and justify it.
   - The P364 **spark** must still spawn only for hull hits (the native `AddHullHit` gate is unchanged).
   - Audience and cadence stay as P364/P375 set them.
2. **Four hit sounds.**
   - Add the `hitmeshield1-3`, `hitmearmor1-3` and `ef120` (ef120 is already loaded) assets to `Content.mgcb`.
   - Play them in `ClientWorld`/`Game1.Audio` from the hit signal: "my" when the target is our ship, "other" otherwise, shield vs hull by the flag.
   - Pick randomly among the 1-3 variants, allow at most one per target per frame (native's guard), and position/attenuate the "other" sounds the way existing positional cues do.
3. **Damage flash on shield hits.** Arm `DamageFlashTracker` when our own shield fraction drops, as well as the hull. Keep native's 2-second on/off phase logic unchanged.

## Explicitly out of scope

- The shield-flare visual (**P463b**).
- Collision and rock-hit sounds (**P463c**).
- Per-weapon firing sounds (P465).
- Named follow-ups: **out-of-bounds damage** and **crowded-sector damage** (`shipIGC.cpp:385-425`). They're missing damage sources, and when added they'll need their own feedback.

## Verification

- Server test: a shield-absorbed projectile hit now produces the broadcast with the shield flag, and a hull hit still produces the spark case. This **must fail on unfixed code first**; record that.
- Client unit tests (pure, no graphics device):
  - the sound-selection rule (my/other × shield/hull, with the once-per-frame guard)
  - the flash arms on a shield-only drop
- Live check against a real server: the log shows the right sound choice for each case when hitting and being hit.
- `/home/domingo/.dotnet/dotnet build` → 0 warnings, 0 errors.
- `/home/domingo/.dotnet/dotnet test` → 3 consecutive clean runs. Only the pre-existing documented timing flakes are acceptable; name any you see.

## Deliverables

1. Committed on **`master`**, not pushed. Check `git branch --show-current` first.
2. Report `docs/reports/P463_HIT_SOUNDS_AND_SHIELD_HIT_SIGNAL.md`, covering:
   - the wire decision (flag vs sibling message) and its layout
   - the pre-fix failing test
   - the sound mapping
   - the live evidence

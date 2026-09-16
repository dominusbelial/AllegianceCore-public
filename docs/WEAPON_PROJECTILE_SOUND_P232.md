# P232 — Weapon-fire and projectile in-flight sound in the MonoGame client

Date: 2026-09-16. Stage P232 of the staged AllegianceCore port, landed on `master`
directly (not pushed — Domingo pushes).

Domingo asked for "the sounds of the projectiles passing nearby your cockpit."
Research confirmed these are **two genuinely distinct, real native sound
layers**, kept separate here exactly as native does:

1. **The weapon's own firing sound** — a one-shot played by the shooter when the
   weapon fires a shot (or a looping burst sound for automatics), driven by
   `DataPartTypeIGC.Weapon`'s `ActivateSound`/`SingleShotSound`/`BurstSound`.
2. **The projectile's own in-flight ambient sound** — a continuous 3D-positioned
   sound attached to the projectile while it lives, driven by
   `DataProjectileTypeIGC.AmbientSound`.

The second layer is the actual mechanism behind "sounds passing nearby": a normal
distance-attenuated positional sound swells as the bolt flies close to the
listener and fades as it recedes — **not** a special near-miss detector (none
exists in the native source; none was invented here).

---

## 1. The static-core question (checked, per the brief)

Per-type sound selection is **not reachable client-side**, re-confirmed exactly
as in P230/P231: the MonoGame client is a pure renderer with a single
ProjectReference to `Allegiance.Core` (wire codecs only); `MissionInfo` carries
only the static-core **filename**; a repo-wide grep for
`StaticCore`/`MissionMap`/core-loading in the client projects returns nothing.
`DataPartTypeIGC.Weapon.SingleShotSound` and
`DataProjectileTypeIGC.AmbientSound` hold a per-type `SoundID` that resolves to
an asset only through a loaded core's sound table — unreachable without dragging
the whole static-core loader into the client, which the brief explicitly blesses
deferring.

**Consequence (documented scope cut):** both layers use a **single real default
asset**, standing in for every weapon/projectile type:

- **Weapon-fire one-shot** — `pulselaser.ogg` (native `pulselaserShotSound`,
  sounddef.mdl; energy-family, matching this POC's default live-fire Gat Gun,
  projectile type 4).
- **Projectile in-flight ambient loop** — `erbeam.ogg` (native `er_beam`,
  sounddef.mdl) looped for the projectile's lifetime, volume-scaled by listener
  distance.

Both are real, confirmed-present assets in `~/Development/Artwork` (this port's
`ALLEGIANCE_ARTWORK_PATH`), shipped through the standard `OggImporter` →
`SoundEffectProcessor` content pipeline (added to `Content.mgcb`), exactly like
every P213-P231 cue.

---

## 2. No 3D-positional engine — distance-scaled approximation (documented)

P213 established that this POC has **no 3D-positional audio** (engine/cockpit/
ambience are all non-positional own-ship cues; P213's remarks explicitly note
`SoundEffectInstance.Apply3D` was out of scope for remote/world sounds). The
brief blesses a simpler distance-based approximation when that is the case. So
instead of an `AudioListener`/`AudioEmitter` positional mix, the ambient loop's
**volume is recomputed each frame from the listener-to-projectile distance** via
a pure linear curve — reproducing the qualitative "swell on approach, fade on
recession" of native's positional attenuation without pretending to match its
exact falloff.

## 3. Implementation

### Pure logic (MonoGame-free, unit-testable — the P232 verification target)

- `Audio/ProjectileAmbientSoundCurve.cs` — `VolumeForDistance(d)` linear
  falloff from `MaxVolume(0.55)` at distance 0 to silence at `AudibleRadius`
  (=5900, chosen to match the P231 render-visibility radius so what you hear is
  exactly what you see), plus `IsLocalFire(spawnDistance)` =
  `spawnDistance <= FireOriginRadius(150)`.
- `Audio/ProjectileSoundPlanner.cs` — the spawn→Start / continue→Update /
  expire-or-recede→Stop transition tracker, keyed by the `ProjectileEntity`
  object's **reference identity** (projectiles carry no wire id; the same
  object persists from spawn until pruned). Emits a `List<ProjectileSoundCommand>`
  each frame; `Start` carries `IsLocalFire` when the bolt spawned within
  `FireOriginRadius` of the listener. Empty input stops every tracked bolt.

### SoundManager additions (`Audio/SoundManager.cs`)

- Loads `_fireSound` (`pulselaser`) and `_projectileAmbient` (`erbeam`).
- `PlayWeaponFire()` — one-shot, mirroring native's
  `fFiringShot() → StartSound(GetSingleShotSound(), …)` (trekigc.cpp:1005-1012).
- `StartProjectileAmbient(key, volume)` / `UpdateProjectileAmbient(key, volume)` /
  `StopProjectileAmbient(key)` — a `Dictionary<ProjectileEntity, SoundEffectInstance>`
  of looped `erbeam` instances: start on spawn, volume per distance, stop+release
  on expiry. All no-ops when the manager is inert (graceful-degrade, P213
  convention).

### Game1 wiring

`UpdateProjectileSounds()` (called each `Update`, after the existing
`_sound?.Update(...)`): under the `Gate` lock it prunes expired projectiles,
gathers every projectile within `AudibleRadius` of the local ship (position via
`PositionAt`, the P231 extrapolation), feeds the pure planner, and replays its
commands against the SoundManager. A newly-spawned bolt within
`FireOriginRadius` of the listener is attributed to the **local ship's own fire**
(the P88 wire carries no shooter id, so this position heuristic is the port's
own local-vs-other discrimination) and plays the weapon-fire one-shot; every
in-range bolt gets the distance-scaled ambient loop. The `[monogame] audio:`
console lines are the permanent live audio-state surface (the established
P213/P216 convention), used for the proof below.

## 4. Live proof (real stack, real weapon fire)

Reused the P221/P225/P231 live-fire recipe: a temp `teams:1` server config
(port 6291, m2xspiderw) + temp overrides that (a) spawn a fight-capable hull and
(b) auto-hold Fire from ~22 s past launch (this Wayland host has no
input-injection tool). The audio-state console lines are the live proof, per the
brief's "however this client already surfaces audio state changes" allowance.
**All temp overrides reverted; `Server.cs` diff is empty.**

Server (real fire broadcast):
```
[ctrl]   1 ship 8096 controls=(yaw 0.000 pitch 0.000 roll 0.000 thr -1.000) state=0x8000
[projectile] ship 8096 fired -> PROJ_INST(12) type 4 pos (720,730,920) vel (0,0,-600) life 1.1 -> 1 same-side peer(s)
```
Client (real audio state changes, one complete shot cycle):
```
[monogame] audio: weapon fire                    ← the one-shot fire cue on the local shot
[monogame][TEMP-P232] projectile ambient instance State=Playing   ← TEMP probe: ambient loop actually Playing on the device
[monogame] audio: projectile ambient start       ← in-flight ambient begins at spawn
[monogame] audio: projectile ambient stop        ← ambient stops when the bolt expires (life 1.1 s)
```
The cycle repeats per shot (a few hundred times over the run): each real local
fire produces exactly one fire one-shot + one ambient start, and the ambient
stop follows each bolt's expiry. The `State=Playing` probe confirms the looped
ambient truly reached the audio device (not just the decision path). Probe line
removed before commit.

## 5. Build & test status

- Whole-solution build (`AllegianceCore.slnx`): **0 warnings, 0 errors**.
- `Allegiance.ClientCore.MonoGame.Tests`: **170 total, 0 failed** (+11: 5
  `ProjectileAmbientSoundCurveTests` + 6 `ProjectileSoundPlannerTests`).
- `Allegiance.ClientCore.Tests`: **75 total** (unchanged — no ClientCore change).
- `Allegiance.Core.Tests`: **3050 total** (unchanged), the only failures the
  documented pre-existing live-network flakes (chiefly
  `LiveTurretGunnerBindingTests...`, persistent machine-state; see the skill's
  flake list).

Must-fail check: neutralizing the planner's Stop emission reds
`ProjectileSoundPlannerTests.ExpiredProjectile_EmitsStop` and
`.MultipleProjectiles_TrackedIndependently` (expected `Stop`, got `Update`);
restored green. The Start/IsLocalFire assertions likewise fail if the
corresponding planner lines are removed.

---

## Scope boundary

- Missile audio (launch/flight/explosion) — separate later task; untouched.
- Per-type sound selection (the real `SoundID` per weapon/projectile) — not
  reachable client-side this stage (no static core); single real defaults used
  and documented (§1), the brief's sanctioned fallback.
- No real 3D-positional audio engine — distance-scaled volume approximation,
  documented (§2), per the brief's allowed scope cut.
- Multiplayer-aware mixing/prioritization — out of scope; only the local ship's
  shots and any in-range projectile are voiced (§1 of the brief).
- P231's projectile rendering code — read as reference, not modified.
- Native reference source (`~/Development/Allegiance`) untouched (read-only).

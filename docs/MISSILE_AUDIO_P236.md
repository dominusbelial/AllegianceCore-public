# P236 — Missile launch, in-flight, and explosion sound

Stage landing: P-series master, not pushed (Domingo pushes). No side branch.
Closes out the missile-rendering arc (P233–P236): real missiles render (P234)
with a smoke/fire trail (P235) and now the three real sound cues native plays
for them.

---

## 1. The three cues, the native mechanism, and the fallback story

Native drives all three from the missile type's loaded-core data plus a shared
explosion-tier system. This port's MonoGame client is a **pure renderer with no
static core** (re-confirmed this stage: MissionInfo carries only the core
filename), so the per-type / per-scale values native reads are unreachable
client-side. Each therefore uses the **single-real-default convention P232
established for weapon/projectile sounds** — real, confirmed-present assets,
documented as defaults rather than fabricated.

### 1a. Launch — one-shot at the firing ship
Native: `clintlib.cpp:3103` `PlaySoundEffect(pMagazine->GetMissileType()->GetLaunchSound(), pShip)` —
positioned at the firing ship, played the instant of fire. Real per-type data
field `DataMissileTypeIGC.LaunchSound` (already decoded,
`DataMissileTypeIGC.cs:69,135`) is a SoundID into the core's sound table —
unreachable client-side. **Default:** `lrgslideout.ogg`
(`HornetMissileLaunch = GainSound(SFXGain, ThreeDSound(20, ImportWave("lrgslideout")))`,
sounddef.mdl:1302; confirmed present in Artwork).

**Attribution.** The wire FIRE_MISSILE(70) carries `LauncherId`, but this port
follows P232's proven proximity approach: a missile whose spawn is within
`LaunchOriginRadius` (200 units) of the listener is the LOCAL ship's fire and
gets the launch one-shot; a distant enemy launch is not attributed (native
would play it attenuated at the distant ship; with no 3D-positional audio we
suppress it — the same local-vs-other cut P232 makes for weapon fire). Our own
fire spawns at ~0 distance, so the launch cue fires exactly on our shots.

### 1b. In-flight — continuous 3D-positioned loop at the missile
Native: per-object `AmbientSoundSite` starts the real per-type
`DataMissileTypeIGC.AmbientSound` at the missile's position
(trekigc.cpp:694-728,1454-1463), a normal 3D-positional loop that swells on
approach / fades on recession — **not** a designed cockpit/exterior asset pair
(no such pair exists in the data for missiles, confirmed this session), so it
varies only by listener distance, same as P232's projectile in-flight sound.
**Default:** `mmissile.ogg` (`HornetMissileFlight = GainSound(SFXGain,
ThreeDSound(100, ASRSound(...ImportWave("mmissile"))))`, sounddef.mdl:1303;
present). No 3D engine (P213) → the P232-blessed distance-scaled approximation:
`MissileAmbientSoundCurve.VolumeForDistance` scales a looped `mmissile`
instance by the listener→missile distance each frame.

### 1c. Explosion — one-shot at the detonation, scale-tiered
Native: a shared, non-missile-specific tier system used for *any* explosion:
`scale > 100` → `largeExplosionSound` (`explosionshockwave.ogg`, ThreeDSound
1000), `> 10` → `mediumExplosionSound` (`ef24.ogg`, ThreeDSound 400), else
`smallExplosionSound` (`ef120.ogg`, ThreeDSound 200) (sounddef.mdl:268-270,
trekigc.cpp:80-101). The `scale` is the explosion's radius; native passes it as
`AddExplosion(e.position, e.radius, ...)` (clusterIGC.cpp:407).

**Scale is unreachable client-side.** This port DOES carry a real radius
server-side — the P96 `Explosion` engine (`Sim/Explosion.cs`, port of native
`ExplosionData`; its `Radius` is the blast radius) and the decoded
`DataMissileTypeIGC.BlastRadius` — but **none of it is transmitted to the
client**: FIRE_MISSILE(70) / MISSILE_UPDATE(205) / MISSILE_DESTROYED(83) carry
only type/id/cluster/position/heading (verified against the three codecs, no
radius/scale field). So the tier is unreachable client-side → **single default
tier** per the brief's blessed fallback. `MissileAmbientSoundCurve.DefaultExplosionScale
= 60f` selects the **medium** tier via `ExplosionSoundTierMapper.ForScale`
(a faithful port of native's thresholds, unit-tested at the >100/>10
boundaries) — the same tier native uses for its generic ship explosion
(`explodeShipSound = mediumExplosionSound`, sounddef.mdl:276). All three tier
assets are loaded; the mapper is ready for when a real scale reaches the client.

## 2. Implementation

Mirrors P232's pure-planner architecture exactly (pure logic emits commands;
SoundManager performs the audio; Game1 feeds live state and replays commands).

- **`Audio/MissileAmbientSoundCurve.cs`** *(new, pure)* — distance→volume
  falloffs, `AudibleRadius=6000`, ambient/explosion peak volumes,
  `LaunchOriginRadius=200` + `IsLocalLaunch`. No MonoGame type.
- **`Audio/ExplosionSoundTier.cs`** *(new, pure)* — the three tiers + native
  `ForScale` mapper.
- **`Audio/MissileSoundPlanner.cs`** *(new, pure)* — fed the full in-sector live
  missile set each frame (id, position, distance, local-launch flag), keyed by
  the missile's real `MissileID`. Emits `Launch` (new local missile),
  `AmbientStart/Update/Stop` (in-flight loop, gated by audible radius), and
  `Explosion` (a missile that vanished from the full live set = detonated →
  boom at last-known site, scaled by distance). **Feeds ALL in-sector missiles,
  not just in-range**, so "flew out of earshot" (→ just fade ambient) is
  distinguished from "detonated" (→ boom); the audible-radius gate is applied
  inside.
- **`Audio/SoundManager.cs`** — loads `lrgslideout`/`mmissile`/the three
  explosion assets; `PlayMissileLaunch`, `Start/Update/StopMissileAmbient`
  (looped instances keyed by missile id), `PlayMissileExplosion` (self-managed
  one-shot overload).
- **`Game1.cs`** — `UpdateMissileSounds()` each frame: gathers every missile in
  the local ship's sector (the P234 draw-loop visibility filter — a missile in a
  neighbouring sector is warp-separated and inaudible), feeds the planner, and
  replays commands with the established `[monogame] audio:` live-state logs.
- **Content** — copied `lrgslideout.ogg`, `mmissile.ogg`,
  `explosionshockwave.ogg`, `ef24.ogg`, `ef120.ogg` into `Content/Sounds/` +
  Content.mgcb entries.

Scope respected: no weapon/projectile audio changes (P232 untouched), no new
visual explosion effect (audio only; a blast flash/particle is a natural future
addition, not built), no native-source edits, no P233/P234/P235 code modified
(only hooked into P234's existing spawn/expire tracking via the live missile
set).

## 3. Live proof (real stack, all three cues at the right moments)

Booted the real `Allegiance.Server` (temp teams:1 config, spare port) + the real
MonoGame client on `:0` direct-connect. TEMP-overrode the spawn hull to mount a
real no-lock missile launcher (type 8, `MaxLock==0`, ammo 3, lifespan 5s) in
`SpawnDefaultShip` (Server.cs), and TEMP-added an auto-fire diagnostic in Game1
(~20–30s after ship assignment, 1/sec). All temp edits + the TEMP SoundState
probe **reverted before commit** (Server.cs diff empty; verified).

Server side (real missiles created + expired):
```
[TEMP-P236] armed ship 8096 with missile launcher type 8
[firemissile] 1 ship 8096 launched -> FIRE_MISSILE(70) lock 0.000 ammo 3 -> 2
[missile] ship 8096 fired -> FIRE_MISSILE(70) type 8 ... life 5 -> 1 same-side peer(s)
[missile] ship 8096 expired missile 5464 -> MISSILE_DESTROYED(83) pos ... -> 1 same-side peer(s)
```

Client side (the live audio-state surface), with the probe's on-device proof:
```
[monogame] audio: missile launch                                   <- t=20s, missile 5464 fired
[TEMP-P236] missile ambient id=5464 state=Playing vol=0.60         <- mmissile loop PLAYING on the device
[monogame] audio: missile ambient start
[monogame] audio: missile launch / ambient start (5465, 5466)      <- t=21s, 22s
[TEMP-P236-HB] ... missiles=3                                      <- three aloft together
[monogame] audio: missile ambient stop                             <- t=25s, 5464 expired (5s life)
[monogame] audio: missile explosion (tier Medium)                  <- explosion at detonation site
[monogame] audio: missile ambient stop + explosion (tier Medium)   <- 5465, then 5466 (t=26s, 27s)
[TEMP-P236-HB] ... missiles=0                                      <- all detonated
```

Every cue landed at the right moment and the in-flight loop was verified
`state=Playing` on the real audio device — launch at fire, ambient for the whole
5s flight (stopped on expiry), and the tier-Medium explosion exactly when each
missile's lifespan elapsed (its MISSILE_DESTROYED(83) reached the client). The
flight stayed within the spawn sector and well under `AudibleRadius`, so the
detonation was audible. Explosion tier logged as `Medium` — the documented
default (scale unreachable client-side).

## 4. What changed
- `src/Allegiance.ClientCore.MonoGame/Audio/MissileAmbientSoundCurve.cs` *(new)*
- `src/Allegiance.ClientCore.MonoGame/Audio/ExplosionSoundTier.cs` *(new)*
- `src/Allegiance.ClientCore.MonoGame/Audio/MissileSoundPlanner.cs` *(new)*
- `src/Allegiance.ClientCore.MonoGame/Audio/SoundManager.cs` — missile cue fields/
  load/methods/dispose
- `src/Allegiance.ClientCore.MonoGame/Game1.cs` — `UpdateMissileSounds()` wiring
- `Content/Content.mgcb` + `Content/Sounds/{lrgslideout,mmissile,explosionshockwave,ef24,ef120}.ogg`
- `tests/.../MissileAmbientSoundCurveTests.cs` *(new)* — 19 (curve falloff +
  explosion peak + local-launch threshold + tier thresholds via Theory)
- `tests/.../MissileSoundPlannerTests.cs` *(new)* — 9 (launch on local spawn,
  ambient start/update/stop, explosion on detonate, leave-earshot≠boom, id-keyed
  independence, clean empty world, id-respawn)

**Must-fail-if-removed verified:** neutralizing the planner's Launch emission and
Explosion emission (temporary `&& false`) reddened 4 planner tests (incl. both the
launch test and the explosion test); reverted to green.

## 5. Build / test status
- Whole solution (AllegianceCore.slnx, Debug): **0 warnings, 0 errors**.
- `Allegiance.ClientCore.MonoGame.Tests`: **206 passed** (178 at P235; +28 mine).
- `Allegiance.ClientCore.Tests`: **83 passed** (unchanged).
- `Allegiance.Core.Tests`: **3062 total / 3061 passed / 1 failed** on 2
  consecutive runs — the only failure each time the documented pre-existing
  `LiveTurretGunnerBindingTests` machine-state flake (fails in isolation 1/1;
  Core is untouched by this stage, which adds no Core code or tests).

## 6. Finding worth surfacing (out of scope, not changed)
While instrumenting the live proof I traced why the real client's keyboard
missile fire never fired: P226's `Game1._lastMissileSendMs` is initialized to
`long.MinValue`, and `Environment.TickCount64 - long.MinValue` **overflows**
(unchecked) to a large negative on any long-uptime host, so the held-Left-Ctrl
`if (nowMs - _lastMissileSendMs >= 250)` is permanently false — real-keyboard
missile fire never launches in a real client (P226 proved the wire via an
in-process harness, not the key path). This is a P226 pre-existing latent bug,
outside P236's mandate (no P-series code edits beyond the sound wiring), so it is
documented here and left for a small follow-up; P236's sounds trigger off the
missile-spawn event and work regardless of which path launched the missile.

# P463 — Hit sounds (my/other × shield/hull) and hit feedback on shield-absorbed hits

Part 1 of the three-way split of the "hit feedback" finding. Adds the hit
*wire signal* for shield-absorbed strikes, the four hit *sounds*
(my/other × shield/hull), and the *damage flash* on shield-absorbed hits.
(P463b = the shield-flare visual; P463c = collision/rock-hit sounds.)

---

## 1. The wire decision — a flag byte, not a sibling message

Native delivers hit feedback as a *same-process* `ThingSite::RegisterHit`
(trekigc.cpp:2365-2393) call, keyed on `bAbsorbedByShield`; there is no native
`DEFINE_FEDMSG` to be faithful to. The port's only hit wire is P364's
`PROJECTILE_HULL_HIT(213)` (`Wire/ProjectileHullHit.cs`), which is already
port-own. P463 chose to **widen that one message with a one-byte
`AbsorbedByShield` flag** rather than add a sibling message.

Justification:
- A shield-absorbed strike and a hull strike are the **same logical event**
  ("a projectile strike connected on a damagable target"); they differ only in
  the damage outcome. A sibling message would duplicate the whole 40-byte
  layout, the client `Apply` handler, and the renderer drain for a single
  boolean.
- The client needs **both** outcomes of the same message simultaneously: the
  sound plays for both (choosing shield vs hull), the hull spark must spawn
  only for hull, and P463b's flare consumes the shield case. One stream with a
  discriminator is the cleanest consumer contract.

### Layout (36 → 40 bytes)

Natural-4-byte-alignment convention preserved; the flag is inserted before the
4-aligned `radius` float with 3 alignment-pad bytes:

```
offset  0: ushort cbmsg
offset  2: ushort fmid = 213
offset  4: short targetId
offset  6: short clusterId
offset  8: byte  absorbedByShield (1 = shield-absorbed, 0 = hull/killed)
offset  9: byte  pad0
offset 10: ushort pad1
offset 12: float radius
offset 16: Vector3 position
offset 28: Vector3 normal
= 40 B fixed → cbmsg = 40
```

The flag is set server-side in `Sector.MaybeRaiseProjectileHit` from the
`DamageResult` of `ReceiveDamage`: `dr == ShieldDamage` ⇒ `true` (the round was
fully absorbed by the target's shield). The spark gate is unchanged — a shield
hit still never spawns the hull spark (the client suppresses it from the flag).

Signature plumbing threaded through `IIgcSite.ProjectileHitEvent`,
`RecordingIgcSite`, `ServerHost.ProjectileHit`, and
`Server.Combat.BroadcastProjectileHullHit`, which now sets `AbsorbedByShield`
and logs it (`[projectile-hit] … absorbedByShield=…`).

## 2. Pre-fix failing test (recorded)

The shield-raise test was written **before** the `Sector` gate change and run
against the still-unfixed code. With the wider message + plumbing in place but
`MaybeRaiseProjectileHit` still returning early on `ShieldDamage`:

```
ProjectileHullHitCombatTests.Shield_absorbed_hit_raises_projectile_hit_event_with_absorbed_flag [FAIL]
  Assert.Contains() Failure: Filter not matched in collection   (no ProjectileHitEvent raised at all)
```

(Red, then green after the gate was widened to admit `ShieldDamage`.)

Two server tests cover the discriminating cases:
- `Shield_absorbed_hit_raises_projectile_hit_event_with_absorbed_flag` — a
  fully shield-absorbed round (hull untouched) raises the event with
  `absorbedByShield=True`.
- `Hull_hit_raises_projectile_hit_event_with_clear_absorbed_flag` — a hull hit
  carries the flag cleared (`False`), so the spark case is unambiguous.

## 3. The four hit sounds — mapping

Native mapping (sounddef.mdl:250-264, trekigc.cpp:2365-2393):

| Case            | Native sound                                 | Assets                               | Port |
|-----------------|----------------------------------------------|--------------------------------------|------|
| myShieldHit     | `RandomSound(HitMeShield1-3)` @SFXGain       | `hitmeshield1-3.ogg`                 | random 1-3 pool, vol `MyVolume` |
| myHullHit       | `RandomSound(HitMeArmor1-3)` @SFXGain        | `hitmearmor1-3.ogg`                  | random 1-3 pool, vol `MyVolume` |
| otherShieldHit  | `myShieldHitSound` @priority −1.6            | same `hitmeshield1-3` pool           | same pool, distance-attenuated |
| otherHullHit    | `ef120` @priority −1.6                       | `ef120.ogg` (already loaded)         | `ef120`, distance-attenuated |

Notes:
- `otherShieldHitSound` **is** `myShieldHitSound` natively — one pool serves
  both; the "other" cut is the priority −1.6, reproduced as lower peak volume.
- `otherHullHitSound` is the single `ef120`, which the port already loads as
  the small-explosion tier; P463 **reuses that one `Content.Load`** (no double
  load).
- Six new assets added to `Content.mgcb` + `Content/Sounds/`
  (`hitmeshield1-3`, `hitmearmor1-3`); `ef120` was already present.

### Selection + guard + positional attenuation

`Audio/HitSoundSelector.cs` (pure, no graphics device):
- `HitSoundSelector.Select(events, myShipId)` reduces one frame's drained hits
  to at most one cue per distinct target — native's
  `m_bPlayedHitSoundThisFrame` guard (trekigc.cpp:2372), first strike in
  arrival order winning — classified by
  `HitSoundSelector.Classify(targetId, absorbed, myShipId)` into
  `MyShield/MyHull/OtherShield/OtherHull` (my = `TargetId == MyShipId`).
- `HitSoundCurve` gives the distance falloff: "my" cues are cockpit-range at
  `MyVolume = 0.9`; "other" cues attenuate linearly to silence at
  `AudibleRadius = 6000` and peak at `OtherPeakVolume = 0.6` (the −1.6 cut).

`Game1.Audio.UpdateHitSounds()` drains `world.TakeHitSoundEvents()`, filters to
the local sector, runs the selector, and calls
`SoundManager.PlayHitSound(kind, volume)` (random variant pick among the 1-3
pool, self-managed one-shot), logging
`[monogame] audio: hit sound <Kind> (target …, vol …)`.

`ClientWorld.Apply(ProjectileHullHit)` now feeds **two** streams: the existing
`HullHits` (renderer's spark) and a new `HitSoundEvents` buffer
(`HitSoundEntity`), drained independently so the Update-time audio pass doesn't
steal the Draw-time renderer's sparks. The renderer skips the spark when
`AbsorbedByShield` is true (native `AddHullHit` is hull-only;
projectileIGC.cpp:193).

## 4. Damage flash on shield hits

`Flight/DamageFlashTracker` previously armed only when the local ship's **hull**
fraction dropped. P463 feeds the **shield** fraction too
(`Update(now, hull, shield)`) and arms when **either** drops past the noise
epsilon (`HullDiffEpsilon`/`ShieldDiffEpsilon` = 0.002), matching native's
`flAmount > 0` arm (trekigc.cpp:4281) which fires for shield-absorbed hits. The
2-second on/off phase logic (`FlashOn/FlashOff/FlashingDuration`, boundary-fudge
re-arm) is unchanged. `Game1` gates on `world.MyShipId`, so only the local
ship's gauges can flash.

## 5. Build & test

- `/home/domingo/.dotnet/dotnet build` → **0 warnings, 0 errors**.
- New/updated tests green in isolation:
  - `ProjectileHullHitCombatTests` — 4/4 (the two P463 flag cases + the two P364
    guards, with the P364 sector assertion updated for the widened recording).
  - `DamageFlashTrackerTests` — 15/15 (existing suite adapted to the two-gauge
    `Update` + four new shield-drop tests).
  - `HitSoundSelectorTests` — new, covering the my/other × shield/hull
    classification, the once-per-frame guard, and the distance curve.
  - `ClientWorldHullHitTests` — 8/8 (absorbed-flag round-trip, wire size
    36→40, the dual-stream buffer independence).
- Full suite ×3: each run's only failures are **documented pre-existing live-wire
  timing flakes**, which rotate run to run and are unrelated to P463 (none
  reference any P463 symbol; all pass green in isolation):
  - Core: `ConquestTerritoryLiveTests.*` (win-condition / flag-capture tips),
    `GameOverPlayersWireTests.*` (the conquest game-over twins),
    `LiveTurretGunnerBindingTests.Board_binds_gunner_and_active_turret_update_roundtrips_live`
    (the repo's persistent documented flake),
    `LiveMoneyTests.Board_refund_broadcasts_money_change_sidewide_not_cross_side`,
    `LiveWinConditionTests.Multi_mission_disconnect_scopes_per_mission`, and one
    pure-logic ordering flake `CargoBayReloadTests.SwapPart_out_of_range_negative_mount_is_rejected_cleanly_not_thrown`
    (passes 9/9 in isolation → environmental/parallel-load artifact, not a
    regression — no cargo/mount/reload symbol is touched by P463).
  - ClientCore: `JoinFlowLiveTests.EntityTable_TracksARealTickingServer`,
    `ShipDeleteGoneLiveTests.DockOut_WatcherLatchesGone_UndockReturnsAndClears` —
    the exact two named in P462's report.

## 6. Live evidence

`tools/hitsound-proof` (headless) boots a **real in-process server** with real
bots + a real `ConnectionSession` observer, and exercises both hit directions
over the real `PROJECTILE_HULL_HIT(213)` wire. The server's own broadcast log
surfaces the flag; the observer classifies its drained hits with the **real**
`HitSoundSelector`.

**MODE other** (same-side observer watches an armed shooter hit a shielded
enemy target):

```
[projectile-hit] ship 8098 hit target 8099 sector 1050 absorbedByShield=True  radius 165 -> PROJ_HULL_HIT(213) -> 3 peer(s)
[projectile-hit] ship 8098 hit target 8099 sector 1050 absorbedByShield=False radius 165 -> PROJ_HULL_HIT(213) -> 3 peer(s)
[hitsound] MODE other: drained 4 OTHER_SHIELD + 2 OTHER_HULL hits (all correct) guard=121
[hitsound] hitsound-proof PASS (live wire + classification)        EXIT=0
```

**MODE my** (an enemy shooter hits the observer's OWN ship; the observer is a
cross-side viewer, so it receives its own hits with `TargetId == MyShipId`):

```
[projectile-hit] ship 8098 hit target 8099 sector 1050 absorbedByShield=True  -> PROJ_HULL_HIT(213) -> 2 peer(s)   (target == viewer MyShipId 8099)
[projectile-hit] ship 8098 hit target 8099 sector 1050 absorbedByShield=False -> PROJ_HULL_HIT(213) -> 2 peer(s)
[hitsound] MODE my: drained 3 MY_SHIELD + 2 MY_HULL hits (all correct)
[hitsound] hitsound-proof PASS (live wire + classification)        EXIT=0
```

The shield→hull transition in both modes comes from the victim's shield
depleting / being knocked down (≤ 0.1 "down" threshold), so subsequent rounds
leak to hull and broadcast `absorbedByShield=False` — proving the flag and the
sound choice flip together end-to-end.

## 7. Out of scope (unchanged)

- Shield-flare visual — **P463b** (consumes this task's signal: the true
  `AbsorbedByShield` case in `HullHits`/`HitSoundEvents`).
- Collision & rock-hit sounds — **P463c**.
- Per-weapon firing sounds — P465.
- Named follow-ups: out-of-bounds damage and crowded-sector damage
  (`shipIGC.cpp:385-425`) — missing damage sources, to get their own feedback.

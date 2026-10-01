# P463c — Collision event + collision sound (feeds P449b's jiggle) + rock-hit sound

Part 3 of the three-way hit-feedback split (P463 = sounds/signal, P463b =
shield flare, P463c = collisions). This is **independent** of P463/P463b. It ran
after P466 (station ramming) landed, so the station collision resolver also got
the event and the sound.

## Summary

Every collision the server already resolved (ship↔asteroid P254, ship↔ship
P354/P392/P408, ship↔station P466) previously deferred "collision sound / FF /
jiggle effects … a separate task" (`Sector.cs` remarks at the resolvers). This
task is that follow-up:

1. **Server** raises one `COLLISION_EVENT(216)` site event per resolved collision,
   carrying both party object ids, the sector, the impact point, and each
   party's native jiggle intensity, broadcast to the peers who can see either
   party (the P364/P375 audience rules).
2. **Client** plays `colisionlarge` positionally for the collision in its own
   sector, and feeds the local ship's jiggle intensity into **P449b's**
   `FlightJiggle` (`SetJiggle`) — only when the local ship IS a party.
3. **Rock-hit** sound: a projectile striking an asteroid already reaches the
   client via the P364 `PROJECTILE_HULL_HIT(213)` signal with target id NA(−1),
   so the client plays `hitrock` for that case (no change to the step-1 message).

Force feedback (`PlayFFEffect`) is out of scope (no FF device path in the port),
as is aleph bounce / out-of-bounds / crowded-sector damage (named follow-ups).

---

## 1. Wire layout — `COLLISION_EVENT(216)`

New port-own message `src/Allegiance.Core/Wire/CollisionEvent.cs` (FMID **216**,
above native's real FMID range that tops out at 200 — same convention as P230's
204 … P463's 213). Fixed 32-byte layout under the project's
natural-4-byte-alignment convention, fully documented in the file:

```
  offset  0: ushort cbmsg
  offset  2: ushort fmid = 216
  offset  4: short party1Id   (the ship in a ship↔rock/station collision,
                               one of the two ships in a ship↔ship collision)
  offset  6: short party2Id   (the other ship, or NA(-1) for a rock/station)
  offset  8: short clusterId  (the sector the collision occurred in)
  offset 10: short pad0 (alignment)
  offset 12: float jiggle1    (party1's camera-jiggle intensity)
  offset 16: float jiggle2    (party2's camera-jiggle intensity)
  offset 20: Vector3 position (world impact point, 12 B)
  = 32 B fixed → cbmsg = 32
```

The jiggle intensities are computed **server-side** from the native formula with
`c_impactJiggleCoefficient = 1/20` (shipIGC.cpp:624), so the client never
re-derives damage:

- **ship↔asteroid / ship↔station** (shared bounce block): only the ship shakes —
  `jiggle1 = 2·damage1·coeff`, `jiggle2 = 0` (shipIGC.cpp:836,1006).
- **ship↔ship**: both parties shake with their own intensity folding the two
  parties — `jiggle1 = (2·damage1+damage2)·coeff`, `jiggle2 = (damage1+2·damage2)·coeff`
  (shipIGC.cpp:1191,1195), where `damage1 = 2·base1+base2`, `damage2 = base1+2·base2`,
  `base_i = |v_i − v_i'|²/128`.

A rock/station party id is NA(−1) (no portable short id — the same convention as
`ProjectileHullHit.TargetId`), which also expresses "never a camera to shake".

**Raise sites** (all three resolvers in `Sector.cs`), routed through the
ship's mission site (`IIgcSite.CollisionEvent`, default no-op; the host seam the
broadcast rides on):

- `ResolveShipAsteroidCollision` — raised after `damage1/damage2` are known
  (so it fires even for a lethal hit or a lifepod, mirroring native playing the
  sound outside its damage gate).
- `ResolveShipShipCollision` — same placement, both parties.
- `ResolveShipStationCollision` (P466, covered) — same placement.

**Broadcast audience** — `Server.BroadcastCollision` reuses the P364/P375 rules,
united over both parties via `AddCollisionPartyAudience`: each party's **own
peer always** (a pilot always hears their own collision, even a side-less
body), plus — when the party carries a side — same-side teammates holding its
`ExportShip(26)` (`_teammateExportSent`) and the P369 cross-side viewers in the
party's sector. (`Vector3` is used by both `Allegiance.Core.Collision` and the
wire, so the wire message is fully qualified in the broadcaster.)

---

## 2. P449b jiggle hand-off

P449b had **landed** (`a18cdb2f`), so this task calls its model directly. Its
`FlightJiggle.SetJiggle(j)` (the native `SetJiggle`, WinTrek.cpp:503-508) is the
drop-in impact entry point the P449b report's "step 4" awaited; the P449b
0.25-second half-life decay and the cockpit-only application are unchanged.

Client wiring (`Game1.UpdateCollisionSounds`, in the audio pass next to
`UpdateHitSounds`): for each drained `CollisionEntity`,

- `CollisionFeedback.JiggleForOwnShip(party1, party2, jiggle1, jiggle2, myShipId)`
  returns `jiggle1` when the local ship IS party1, `jiggle2` when it IS party2,
  and `0` otherwise — the **"own ship only"** rule (native applies `SetJiggle`
  only to its own ship, trekigc.cpp:4992-5001). A witnessed collision never
  shakes the local camera.
- The selected intensity is fed to `_flightJiggle.SetJiggle(intensity)`.

Pure test coverage: `CollisionFeedbackTests` pins the own-ship-only rule and the
once-per-collision sound selection.

---

## 3. Rock-hit sound

Native plays `rockHitSound` (`hitrock`, sounddef.mdl:266) inside the asteroid's
`ReceiveDamage` when a round actually chips the rock (asteroidigc.h:154-155). I
chose the **existing** asteroid-hit signal rather than extending the step-1
message: a projectile striking an asteroid already fires the P364
`PROJECTILE_HULL_HIT(213)` to the client with target id NA(−1)
(`Sector.cs:1942`), carrying the impact position and cluster. The client now
classifies that NA target id as `HitSoundKind.Rock` in `HitSoundSelector`
(`trekigc.cpp:2373-2385` is extended: `-1 → Rock`), and `SoundManager.PlayHitSound`
plays `hitrock` (loaded via `Content.mgcb`) at the impact point, attenuated by
listener distance — the same positional treatment as "other" hit cues.

(The P423 `ASTEROIDS_UPDATE`/health path is the mining-health snapshot, not a
per-projectile hit signal, so it was not used.)

---

## 4. Assets

- Copied `colisionlarge.ogg` and `hitrock.ogg` from `~/Development/Artwork` into
  `Content/Sounds/` and registered both in `Content.mgcb` (OggImporter →
  SoundEffectProcessor, Quality=Best). Verified the MGCB pipeline emitted
  `colisionlarge.xnb` / `hitrock.xnb` into the built `Content/Sounds/`.
- `SoundManager` loads `_collision`/`_rockHit`, exposes `PlayCollisionSound(vol)`
  and `PlayRockHitSound(vol)` (self-managed one-shots), and disposes them.

---

## 5. Verification

### Server tests (fail-first)
Two live sim tests added to the existing collision suites, driven through real
`Sector.Update` ticks on a `RecordingIgcSite`; both **fail on the pre-change
code** (no `CollisionEvent` existed) and pass now:

- `ShipAsteroidCollisionTests.Asteroid_collision_emits_exactly_one_collision_event_with_native_ship_jiggle`
  — exactly one `CollisionEvent`, ship jiggle `2·damage1·coeff` (from the
  measured Δv), rock jiggle 0.
- `ShipShipCollisionTests.Ship_ship_collision_emits_exactly_one_collision_event_with_native_per_party_jiggles`
  — exactly one event, party jiggles `(2·d1+d2)·coeff` and `(d1+2·d2)·coeff`.
- Plus `CollisionEventWireTests` — 32-byte codec round-trips both the
  rock-vs-ship and ship-ship shapes.

### Client pure tests
`CollisionFeedbackTests` (own-ship-only jiggle feed; once-per-collision sound
selection in the local sector; empty without a local sector; volume falloff) and
`HitSoundSelectorTests.Classify_NaTargetId_IsRockHit`.

### Live
`ShipAsteroidCollisionTests.Loopback_collision_event_reaches_the_wire_as_collision_event_216`
— over real loopback TCP, the bot's ship is rammed into the injected rock
server-side and the peer decodes `COLLISION_EVENT(216)` with party1 = the bot's
ship id, party2 = NA, the right sector, positive jiggle1 and jiggle2 = 0. The
human-facing "confirm the sound on both sides / jiggle on your own screen only"
steps need the graphical+audio client (no audio device / display on this box);
their server-side wire driver is proven by the loopback test, and the client
classification/volume/jiggle decisions are covered by the pure tests above.

### Build / test hygiene
- `/home/domingo/.dotnet/dotnet build` → **0 warnings, 0 errors**.
- Full `dotnet test` run: all deterministic suites green; the **only** failures
  are the pre-existing flaky **live/timing** tests (ConquestTerritoryLiveTests,
  GameOverPlayersWireTests, LiveTurretGunnerBindingTests, ShipUpdateBudgetLiveTests,
  and the ClientCore live/loopback family) — the identical tests fail on a clean
  `git stash` baseline (verified), the failing set rotates run to run, and none
  are in files this task touched. The P463c test group (5 Core + 6 MonoGame)
  passed **3 consecutive clean runs**.

---

## 6. Coverage & scope notes

- **Station resolver covered: yes.** P466 had landed, so
  `ResolveShipStationCollision` also raises the event (ship jiggle only; station
  never shaken) — documented in the report.
- Out of scope (unchanged): aleph bounce (`shipIGC.cpp:655`), out-of-bounds and
  crowded-sector damage (`shipIGC.cpp:385-425`), and all force feedback.
- One audience refinement worth noting: `AddCollisionPartyAudience` always
  includes a party's **own** peer (a pilot hears their own collision) and only
  applies the side-gated teammate/cross-side reach when the party carries a
  side — the P364 "shooter must have a side" restriction is intentionally not
  copied here, because a collision's own pilot always perceives it.

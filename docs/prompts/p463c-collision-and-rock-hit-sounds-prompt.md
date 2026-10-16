# P463c — collision event + collision sound (feeds P449b's jiggle), and rock-hit sound

Part 3 of the three-way hit-feedback split: P463 is sounds and signal,
P463b is the shield flare, and this task is collisions. It's independent
of P463 and P463b. Ideally it runs after P466 (station ramming), so the
station collision resolver also gets the sound. If P466 hasn't landed,
cover the resolvers that exist and name the station one as a follow-up.

## Why this task exists

This came out of the 2026-09-29 gameplay-parity pass.

- **Native collision feedback.** Every ship collision plays
  `collisionSound` for both parties (`colisionlarge`,
  `~/Development/Artwork/sounddef.mdl:308`).
  - Sound sites: `~/Development/Allegiance/src/Igc/shipIGC.cpp:655`
    (aleph bounce), `:834` (station), `:1004` (damagable), `:1189,1193`
    (ship-ship).
  - It also triggers `effectJiggle` with intensity
    `(2·damage1 + damage2)·c_impactJiggleCoefficient` (`shipIGC.cpp:1191,1195`).
    The client turns that into a camera jiggle, applied only to its own
    ship (`WinTrek/trekigc.cpp:4992-5001` → `GetWindow()->SetJiggle`;
    the jiggle model itself is P449b's, `WinTrek.cpp:374-399,504-508`).
  - An asteroid struck by a projectile plays `rockHitSound` (`hitrock`,
    `sounddef.mdl:266`) at the impact offset (`Igc/asteroidigc.h:154-155`).
  - `colisionlarge.ogg` and `hitrock.ogg` both exist in `~/Development/Artwork`.
- **The port has the collisions but no feedback.** The damage resolvers
  exist: ship-asteroid at `src/Allegiance.Core/Model/Sector.cs:2015-2140`
  and ship-ship at `Sector.cs:2221-2340` (P354/P392/P408). Both explicitly
  defer "collision sound / FF / jiggle effects… a separate task"
  (`Sector.cs:2011,2217`). This is that task. Nothing tells the client
  about a collision, and no rock-hit sound exists.

## Scope

1. **Collision signal.**
   - From each existing collision resolver, raise one site event per
     collision carrying:
     - both model ids
     - the sector
     - the impact point
     - the jiggle intensity, from native's formula with
       `c_impactJiggleCoefficient` (find its value)
   - Broadcast it as a small port-own message to the peers who can see
     either party. Reuse the P364/P375 audience rules.
   - Document the layout, following `Wire/ProjectileHullHit.cs`.
2. **Client.**
   - Load `colisionlarge` and `hitrock` through `Content.mgcb`.
   - Play the collision sound positionally for each party in our sector.
   - When our own ship is a party, feed the event's jiggle intensity into
     **P449b's** jiggle model (`docs/prompts/p449b-camera-jiggle-and-fov-option-prompt.md`
     owns the model, `SetJiggle` replacing the accumulator, and the 0.25
     half-life decay). This event is the "impact source" P449b's step 4
     looks for.
     - If P449b has landed (`git log --oneline | grep P449b`), call its
       `SetJiggle` equivalent.
     - If it hasn't, don't build a jiggle model here. Expose the intensity
       through one clearly named client hook, and note in the report that
       P449b should consume it.
3. **Rock-hit sound.** Play `hitrock` when a projectile strikes an
   asteroid in our sector. Use the existing asteroid-hit signal if one
   reaches the client (check P423's `ASTEROIDS_UPDATE`/health path and
   P364). Otherwise add the asteroid case to the step-1 message, and say
   which you chose.

## Explicitly out of scope

- Adding new collision types. Ship-vs-station is P466. Named follow-ups:
  - aleph bounce (`shipIGC.cpp:655`)
  - **out-of-bounds damage** and **crowded-sector damage**
    (`shipIGC.cpp:385-425`)
- Force feedback (`PlayFFEffect`). There is no FF device path in the port.
- Projectile hit sounds (P463) and the shield flare (P463b).

## Verification

- Server tests: a ship-asteroid and a ship-ship collision each emit
  exactly one collision event with the native jiggle intensity. They
  **fail first**, since no event exists today; record that.
- Client pure tests for the "own ship only" jiggle-feed rule, and that the
  collision-sound selection happens once per party.
- Live: ram an asteroid and a teammate. Confirm the sound on both sides.
  If P449b has landed, also confirm the camera jiggle on your own screen
  only.
- `/home/domingo/.dotnet/dotnet build` → 0 warnings, 0 errors.
  `/home/domingo/.dotnet/dotnet test` → 3 consecutive clean runs.

## Deliverables

1. Committed on **`master`**, not pushed. Check the branch first.
2. Report `docs/reports/P463C_COLLISION_AND_ROCK_HIT_SOUNDS.md`: the wire
   layout, the P449b jiggle hand-off, the live evidence, and whether the
   station resolver was covered.

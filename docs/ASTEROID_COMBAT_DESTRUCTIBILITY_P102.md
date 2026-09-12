# P102 — Asteroid Combat Destructibility (direct-hit target, `ASTEROID_DESTROYED`)

Date: 2026-09-08 (conversation started). Branch: `master` (top `7ff1433` P101 + this task).

## Headline

An `Asteroid` is now a real, direct-hit damage target of real ordnance in the
shared swept combat detector — exactly the same integration shape P100 used for
`Probe`, not the dedicated proximity pass `Mine`/P95 uses. The native
`CasteroidIGC::ReceiveDamage` formula is ported; killing a rock removes it from
`Sector.Mineable` and broadcasts a real `FM_S_ASTEROID_DESTROYED(86)` to the
whole mission with `explodeF=true`.

## Design question settled (the task's reason to exist)

**Does an asteroid need its own proximity pass, or does it fit the shared swept
combat detector?** — Answered by the native source, same shape as `Probe`:

- `src/Igc/asteroidigc.cpp:36` — `Load(0, ..., c_mtStatic | c_mtHitable |
  c_mtDamagable | c_mtSeenBySide | c_mtPredictable)`. The identical attribute
  shape `CprobeIGC` carries (`probeigc.cpp:115`, cited in P98/P100) minus
  `c_mtScanner` (asteroids don't scan — unsurprising).
- `src/Igc/projectileIGC.cpp:166-169` excludes **only** `OT_mine`; nothing
  excludes `OT_asteroid`. `src/Igc/missileigc.cpp:435-447` is the identical
  shape for missiles (only `OT_mine` is special-cased). Both re-read and
  confirmed this task.
- `src/Igc/asteroidigc.h:126-171` (`ReceiveDamage`) has **no side / friendly-fire
  check at all** — simpler even than `Probe`'s "unarmed → no FF" finding (P100).

**Conclusion**: extend `Sector.ResolveCombatCollisions`'s existing swept
detector with `Asteroid` as a third static target type, mirroring how P100 added
`Probe` as the second. No dedicated pass.

**Structural gap closed**: `Asteroid` had no `Fraction`/hitpoints field. Added
`private float _fraction = 1.0f` + public `Fraction` getter (mirroring
`Probe.cs` exactly, `asteroidigc.h:27` constructor default) and `Dead`.
`Asteroid.Data.HitPoints` is the max HP, never hardcoded. `Asteroid` was **not**
made a `Model` — it stays in `Sector`'s `_mineable` list, respecting its own
doc comment. `Collider`'s constructor takes explicit position/velocity/radius,
not a `Model` reference (`src/Allegiance.Core/Collision/Collider.cs:29-31`), so
the pass walks `_mineable` and builds a collider per rock (`isStatic: true`,
position/radius from `Asteroid.Data.Position` / `Asteroid.Radius`).

## Integration (files changed)

- `src/Allegiance.Core/Sim/Asteroid.cs`
  - Added `DefenseType = 0` (`c_defidAsteroid`, `igc.h:567`), `_fraction`/
    `Fraction`/`Dead`, and `ReceiveDamage(byte type, float amount, double now,
    Vector3 position1, Vector3 position2, Model? launcher)` — the port of
    `CasteroidIGC::ReceiveDamage` (`asteroidigc.h:126-171`).
  - Formula: `amount *= DamageConstant(type, c_defidAsteroid)`
    (`asteroidigc.h:135`); `_fraction -= amount/Data.HitPoints`
    (`asteroidigc.h:152`); first crossing of `<= 0` clamps and returns
    `DamageResult.Killed` (`asteroidigc.h:159-163`), else `HullDamage`.
  - **No friendly-fire gate** — deliberate, native-verified (see Findings).
  - **Death bookkeeping stays in the combat pass** (not the rock): an asteroid
    is not a `Model` and carries no sector/launcher ref, so the pass owns
    `RemoveAsteroid` + `KillAsteroidEvent`. `DamageResult.Killed` is the
    single-crossing signal (no double-kill on a post-death call, mirroring
    native's `dr`-stays-default at `asteroidigc.h:160-165`).
  - No experience multiplier (`asteroidigc.h:137-139` skipped — ships here have
    no accumulated XP, same precedent as `Ship`/`Probe`).
- `src/Allegiance.Core/Model/Sector.cs`
  - `ResolveCombatCollisions` collider build now also walks `_mineable`,
    adding an `Asteroid` collider per rock.
  - Event-resolution generalized to a third target: `ship` / `probe` /
    `asteroid`. The asteroid branch has **no** friendly-fire gate.
  - On `Killed`: `RemoveAsteroid(asteroid)` then (via the ordnance launcher's
    `Ship.Mission`) `Site.KillAsteroidEvent(asteroid, true)`.
  - Added public `Sector.RemoveAsteroid(Asteroid)` (mirrors `Sector.Remove(Model)`),
    the port of native `pasteroid->Terminate()` inside `KillAsteroidEvent`
    (`FedSrv.CPP:12420`).
- `src/Allegiance.Core/Sim/ServerHost.cs` — added `AsteroidKilled` hook +
  `KillAsteroidEvent(IAsteroidIgc, bool)` bridge (casts marker→concrete
  `Asteroid`, mirrors `WarpBombEvent`'s bridge shape).
- `src/Allegiance.Server/Server.cs` — wired `_host.AsteroidKilled =
  BroadcastAsteroidDestroyed;` and implemented it as a **whole-mission**
  broadcast of `FM_S_ASTEROID_DESTROYED(86)`.
- `src/Allegiance.Core/Wire/AsteroidDestroyed.cs` — new codec (id 86): fields
  `clusterID`, `asteroidID`, `explodeF`; fixed 9 bytes
  (`Messages.h:613-617`).
- `src/Allegiance.Bot/Bot.cs` — collects `AsteroidDestroyeds` for the tests.

## Out-of-scope respected

- The station-pop `KillAsteroidEvent` overloads (`FedSrv/FedSrv.CPP:11033-11049`)
  and the side-scoped overloads (`FedSrv/FedSrv.CPP:12422-12440`) are untouched
  — only the plain `(IAsteroidIgc, bool)` overload is implemented (its declaration
  already existed at `IIgcSite.cs:109`).
- No new ore-forfeiture mechanic (native's call site has only `Terminate()`;
  a destroyed rock's `OreRemaining` just ceases to exist).
- `Ship`/`Probe` target paths, blast-on-impact, and the missile-destroyed
  consistency call are unchanged. `ResolveExplosion` (P96) still does NOT splash
  nearby asteroids — named below as the natural follow-up.

## Findings (with the real evidence this task was asked to provide)

### 1. Broadcast audience: WHOLE MISSION, not same-side (verified live)

Native `FedSrvSiteBase::KillAsteroidEvent(IasteroidIGC*, bool)`
(`FedSrv/FedSrv.CPP:12411-12421`) sends the `ASTEROID_DESTROYED` message to
`m_pfsMission->GetGroupRealSides()` (`FedSrv.CPP:12418`) — the whole mission,
the same audience P101 settled for `WARP_BOMB`, not a same-side gate. The
side-scoped overloads (`FedSrv.CPP:12430,12440`) send to a single side, but
those are the station-build/late-joiner variants, explicitly out of scope.

Implemented as whole-mission and **proven by a real live test**:
`AsteroidCombatLiveTests.Lethal_hit_kills_..._broadcasts_destroy_to_whole_mission`
fires one lethal cross-side missile; the server log shows
`ASTEROID_DESTROYED(86) sector 1051 -> 3 peer(s) [WHOLE MISSION]` and the test
asserts **both** the cross-side enemy bot and the same-side teammate bot receive
the message naming the correct asteroid id (6401) and sector (1051) with
`explodeF=true`. Cross-side arrival is the decisive evidence it is whole-mission,
not same-side.

### 2. No friendly-fire gate: a DELIBERATE finding, not an oversight

The native `CasteroidIGC::ReceiveDamage` (`asteroidigc.h:126-171`) contains **no
side/friendly-fire check at all** — unlike `Ship` (`shipIGC.cpp:1222-1228`) and
even simpler than `Probe` (`probeigc.h:74-89`, which at least reasoned about an
armed-probe branch). An asteroid has no `SideId`; it takes damage from anyone,
unconditionally, always. Proven by `Same_side_friendly_missile_damages_the_asteroid_no_friendly_fire_gate`
(live): a same-side teammate missile deals the identical 0.400 fraction to a
rock that a cross-side one does. This is asserted, not asserted-in-a-comment.

### 3. Damage formula, decoded

Verified real: `Power · DamageConstant(type, c_defidAsteroid=0) / maxHP` per hit.
`c_defidAsteroid = 0` confirmed at `src/Igc/igc.h:567`. The live tests assert
the exact computed fraction (`1 − 0.400 = 0.600`) numerically, from decoded
missile Power and the damage matrix.

## Tests added

- `tests/Allegiance.Core.Tests/AsteroidCombatTests.cs` (domain, no sockets):
  - `ReceiveDamage_partial_reduces_fraction_lethal_marks_dead_no_double_kill_and_leaves_removal_to_caller`
    — exact 0.600 fraction math, `Killed` on lethal, no double-kill, and the
    documented caller-owns-removal contract.
  - `Damaged_but_alive_asteroid_still_mines_normally` — a rock at
    `Fraction ∈ (0,1)` still feeds `Sector.ResolveMining` (no regression).
- `tests/Allegiance.Core.Tests/AsteroidCombatLiveTests.cs` (real loopback,
  3-bot / 2-side, synthetic asteroid in the zero-asteroid demo map):
  - `Cross_side_missile_hits_asteroid_with_exact_computed_damage_and_it_survives`
    (point 1 — 1.00→0.600, survives in `Mineable`).
  - `Same_side_friendly_missile_damages_the_asteroid_no_friendly_fire_gate`
    (point 2 — identical 0.400).
  - `Lethal_hit_kills_asteroid_removes_from_mineable_and_broadcasts_destroy_to_whole_mission`
    (point 3 + audience — removed from `Mineable`, `ASTEROID_DESTROYED` to both
    sides, `explodeF=true`).

Existing `CombatDamageTests`, `ProbeCombatTests`, `ProbeCombatLiveTests`,
`WarpBombTests`, and the mining suite all pass unmodified.

## Verification

Three consecutive clean `dotnet test` runs (each 39 s):

```
Passed: 2395, Failed: 0, Skipped: 0, Total: 2395   (run 1)
Passed: 2395, Failed: 0, Skipped: 0, Total: 2395   (run 2)
Passed: 2395, Failed: 0, Skipped: 0, Total: 2395   (run 3)
```

Baseline was 2390 (P101 checkpoint); this task adds 5 tests → 2395. All numbers
carry the file:line citations read above.

## Natural next follow-up (user's call whether it's the NEXT task)

Extending `Sector.ResolveExplosion` (P96) to also blast-damage nearby
asteroids. Native evidence already names this: `projectileIGC.cpp:206-219`
creates the area-effect explosion unconditionally, and `clusterIGC.cpp:411-458`
(`CreateExplosion`) splashes every `c_mtDamagable` body in radius — an asteroid
is `c_mtDamagable` (`asteroidigc.cpp:36`), so native blast fire would damage
adjacent rocks exactly like adjacent ships/mines. The probe-equivalent
finding was already recorded in P100 (`ProbeCombatTests` blast test notes that
`ResolveExplosion` targets Ship+Mine, not probes, as out of scope). This is a
clean, contained follow-up; whether it precedes the next task is the user's call.

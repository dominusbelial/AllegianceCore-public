# MISSILE_LOCK (P181) — Stage 2: real lock-quality buildup toward a target

Branch `master`, on top of `ca71841` (P180 fixup). Adds the real per-tick
lock-quality buildup mechanic (`Ship.MissileLock` / `Ship.UpdateMissileLock`)
ported from native `CmagazineIGC::Update`'s lock-build block
(`src/Igc/magazineigc.cpp:77-181`), as a directly-callable capability on
`Ship` — exactly mirroring how Stage 1 (P179) left `Ship.FireMissile`
"directly-callable only." No control-input / live-fire wiring was attempted
(the deferred `missileFireIGC` transmission question, `Igc/igc.h:829-834`).

## Background / relationship to native

Native `CmagazineIGC::Update` (`magazineigc.cpp:77-181`) computes the magazine's
`m_lock` each tick. The portable core of it (the whole `else` arm when no shot
is being fired, `:126-176`):

1. **Ripcord reset** (`:104-105`) — `if (GetRipcordModel() != NULL) m_lock = 0`.
   Port: `Ship.IsRipcordActive` (`Sim/Ship.cs:463`).
2. **Resolve target** (`:108`) — `GetCommandTarget(c_cmdCurrent)`.
   Port: `Ship.CommandTarget` (`Sim/Ship.cs:602`).
3. **Validity gate** (`:127-132`) — same cluster, hostile side
   (`!AlliedSides`), damageable (or warp for a warp-bomb type), `CanSee`.
4. **Range gate** (`:134-143`) — `1.0 < |dP|² < maxRange²`, where
   `maxRange = InitialSpeed·Lifespan + 0.5·Acceleration·Lifespan²`.
5. **Instant lock** (`:145-146`) — `MaxLock == 0` ⇒ `m_lock = 1`.
6. **Angle-off-bow buildup/decay** (`:148-167`).

## What changed

### `src/Allegiance.Core/Sim/Ship.cs`
- New `public float MissileLock { get; private set; }` (`:2017`) — the port's
  `m_lock`, `0` by default, written only by `UpdateMissileLock`, consumed by a
  caller as the `lockFraction` it hands `FireMissile`.
- New `public void UpdateMissileLock(DataMissileTypeIGC missileType, double
  dt)` (`:2056`) — the port, in native order:
  - ripcord active ⇒ `MissileLock = 0`;
  - `CommandTarget is not Ship` ⇒ `MissileLock = 0`;
  - same-sector (`Sector is { } sector && sector.Models.Contains(target)` — the
    Stage-1 Missile.cs `_sector.Models.Contains(t)` idiom, `Missile.cs:191`),
    hostile side (bare `SideId` inequality, no alliance system), damageable
    (`!target.Dead`) ⇒ any failure ⇒ `0`;
  - range gate ⇒ `0` on failure;
  - `MaxLock == 0` ⇒ `1f`;
  - else the angle-off-bow formula (below), clamped to `[0,1]`.
- `FireMissile` gained `float lockFraction = 1f` (`:1984`), threaded to the
  `Missile` constructor. Every pre-existing call site compiles unchanged and
  keeps Stage 1's exact `m_lock ≡ 1.0` behavior (default `1f`).

### `src/Allegiance.Core/Sim/Missile.cs`
- Constructor gained `float lockFraction = 1f` (`:82`), stored in new
  `public float LockFraction { get; }` (`:132`).
- The one Stage-1 line this task touches: in `OnUpdate`, the homing lead
  `deltaV` changed from `t.CollisionVelocity - Velocity` (Stage 1's `m_lock ≡
  1.0` written out) to `t.CollisionVelocity * LockFraction - Velocity`
  (`:218`) — `missileigc.cpp:386`. Everything else in the homing block is
  untouched.

### `tests/Allegiance.Core.Tests/MissileLockTests.cs` (new, 8 tests)
See Tests below.

## The verified derivation (confirming the brief's, plus one correction)

Native's buildup block (`magazineigc.cpp:148-167`):

```
dP.SetNormalize();
dot  = -(dP * GetBackward());
angleOffBow = (dot >= 1) ? 0 : (dot <= -1) ? pi : acos(dot);
lockTime  = GetLockTime();
lockAngle = GetLockAngle();
v = (lockAngle - angleOffBow) / lockAngle;
if (v < 0) v *= lockTime;
m_lock = oldLock + v * dt / (isShip ? lockTime * ECM : lockTime);
```

Because `GetBackward() = −GetForward()`, `dot = dP·forward`, so `angleOffBow`
is the angle between the direction to the target and the ship's nose — the
"angle off bow."

**In-cone** (`angleOffBow <= lockAngle`, `v ∈ [0,1]`, not scaled):
`Δlock/Δt = v / (lockTime·ECM)`. At `v = 1` (dead-on) that is
`1/(lockTime·ECM)` per second — full lock after `lockTime·ECM` seconds of
continuous aim. **Confirms the brief's in-cone claim**, with the ECM term the
brief dropped (see Divergences).

**Off-cone** (`angleOffBow > lockAngle`, `v < 0`): `v *= lockTime` puts a
`lockTime` in the numerator that cancels one `lockTime` in the denominator,
leaving `Δlock/Δt = (lockAngle − angleOffBow)/(lockAngle·ECM)` (negative) — a
decay that gets faster the further past the lock-angle boundary the target
strays. **Confirms the brief's off-cone claim**, again with the surviving
`÷ECM` term the brief's "two lockTime factors cancel" derivation dropped (the
two `lockTime`s do cancel; the `ECM` does not).

## Divergences from the brief's assumptions (all verified, none guessed)

1. **ECM is applied, not omitted.** The brief asserted "zero ECM field exists
   anywhere in `DataHullTypeIGC.cs` or elsewhere in this port" and directed
   omitting the multiplier. That premise is **factually wrong**: a fresh grep
   shows `DataHullTypeIGC.Ecm` is decoded (`Data/DataHullTypeIGC.cs:33,220`).
   Native applies the *target* ship's hull ECM in the denominator
   (`magazineigc.cpp:165-167`, the `OT_ship` branch). Since this port's lock
   target is always a ship, the ECM multiplier is ported faithfully
   (`Ship.cs:2105-2112`, `lockTime * target.HullType.Ecm`, with a `>0`
   defensive guard for degenerate data). The code and this doc both state it
   plainly as a correction of the brief's premise, not a silent omission.
2. **Target restricted to `Ship`.** Base `Model` has neither `SideId` nor a
   `Sector`; only `Ship` (of the Model kinds) carries both a side and a sector
   membership, and it is native's primary missile-lock target (and the
   ECM-bearing `OT_ship` branch). Mines/probes/stations are not lock targets
   here; the "damageable" gate is implemented as `!target.Dead` — the combat
   pass's established "can take damage" notion (`Sector.ResolveCombatCollisions`
   treats a living Ship as a damage target).
3. **Same-sector check** uses the ship's own sector's model list
   (`Sector.Models.Contains(target)`), the Stage-1 `Missile.cs:191` idiom.
4. **Verified omissions** (confirmed to have no port equivalent): `CanSee`
   (no sensor model), `AlliedSides` (no alliance system — bare `SideId`
   inequality), warp-bomb branch (no capability flag decoded on
   `DataMissileTypeIGC`; the separate `WarpBomb` feature is untouched).
5. **Defensive guards** (unreachable in real corpus data): `LockAngle <= 0`
   ⇒ lock `0` (avoids division by zero), `Ecm <= 0` treated as `1`.

## Tests

`tests/Allegiance.Core.Tests/MissileLockTests.cs` — 8 domain-level tests on an
assembled mission (real placed firing + target `Ship`, opposing `SideId`s,
`CommandTarget` set, `UpdateMissileLock` called directly):

1. Dead-ahead target ⇒ lock builds monotonically to `≥ 0.998` after ≈
   `LockTime·ECM` s of continuous aim.
2. Target dead astern (well off-cone) ⇒ lock never builds (stays 0).
3. Partial lock then target swung astern ⇒ decays by exactly
   `(LockAngle−π)/LockAngle / ECM` per second (the derived rate, asserted
   numerically, not "it went down").
4. `IsRipcordActive` ⇒ lock reset to 0 immediately regardless of aim.
5. No `CommandTarget` ⇒ lock stays 0.
6. Same-side `CommandTarget` ⇒ never builds (hostility gate).
7. `MaxLock == 0` ⇒ instant `1f` even dead astern (dumbfire allowance).
8. `FireMissile` `lockFraction` threads to `Missile.LockFraction` and genuinely
   changes the homing lead (a `lockFraction = 1` missile leads into a target's
   reported sideways velocity; `lockFraction = 0` does not); the **default**
   (no argument) reproduces Stage 1 exactly (`LockFraction == 1f`).

**"Must fail if removed" probe:** Test 8 was run with the `Missile.cs` `deltaV`
wiring temporarily reverted to Stage 1's `target.Velocity - myVelocity` — it
went RED (`full 0.1620 vs no-lock 0.1620`), proving the lockFraction wiring is
genuinely load-bearing, then restored to green.

## Verification results

- `/home/domingo/.dotnet/dotnet build` — clean, 0 warnings / 0 errors.
- `MissileLockTests` in isolation — **8/8 pass**, repeatedly (also all 42
  `~Missile` tests green together).
- Full solution `dotnet test` (baseline now **2852** tests):
  - Run 1: **2852/2852 pass** (fully clean).
  - Later runs intermittently hit only documented environmental live-network
    flakes — `LiveSuicideShipDeleteTests.Combat_death_produces_ship_delete_to_
    same_audience`, `LiveWinConditionTests.Multi_mission_disconnect_scopes_per_
    mission` (P179-recorded), `LiveCombatWireTests.Real_Bot_class_observes_its_
    own_kill_over_the_wire` (skill-recorded intermittent) — **each confirmed
    green on spaced isolated reruns**. Zero novel failure modes; this task's
    own tests are pure-domain (no sockets) and reliably clean in isolation.
  Landed per the task's stated criterion (only documented flakes, new tests
  clean) rather than chasing an unbroken full-suite streak.

## Out of scope (unchanged)

Control-input / `ShipControlState` / live-fire wiring; any new
`DataMagazineTypeIGC`-equivalent static-core record; ECM-free targeting,
visibility, alliance, warp-bomb targeting (except ECM — corrected above); any
NPC-AI behavior change; chaff/countermeasures.

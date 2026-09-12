# WARP_BOMB P101 — missile-vs-warp trigger, whole-mission broadcast, delayed explosion

Branch `master` @ `450192e` (P100) — all work committed on top.

## 1. The real delay constant — index and value (located this session)

The task left this open; I resolved it directly from the reference source:

- **Index:** `c_fcidWarpBombDelay = 32` — `src/Igc/igc.h:98`
  `const FloatConstantID c_fcidWarpBombDelay = 32;`
- **Value:** `c_fWarpBombDelay = 15.0f` — `src/Igc/missionigc.cpp:2726`, stored into
  the mission float-constant blob at init at `src/Igc/missionigc.cpp:2777`:
  `m_constants.floatConstants[c_fcidWarpBombDelay] = c_fWarpBombDelay;`
- The host reads it via `GetFloatConstant(c_fcidWarpBombDelay)` at
  `FedSrv.CPP:12551`: `timeExplosion = pwarp->GetLastUpdate() + <15.0>`.

Same slot this port's `DataConstantsIGC.FloatConstants` already indexes
(`DataConstantsIGC.cs:24`, `c_fcidMax = 40`); the index is in range. The port
hardcodes `Sector.WarpBombDelaySeconds = 15.0` and says so (the mission overwrites
whatever the core's `FloatConstants[32]` holds — the same rationale already used
for `Mission.IncomePerPayday`).

## 2. Sector integration shape — NEW dedicated pass (not parameterized ResolveWarpCollisions)

Chosen: a **new dedicated pass `ResolveWarpBombTriggers`**, mirroring
`ResolveWarpCollisions`' outer `for each model × for each warp` sphere-test
shape, plus a second tiny pass `ResolveWarpBombDetonations` for the timed half.
Why not parameterize `ResolveWarpCollisions` (target type union):

- The two bodies are **wholly disjoint**. The ship pass is debounced
  (`Ship.LastWarpTime`/`WarpCooldownSeconds`), gated on ship radius
  (`0.5·HullType.Length`), and routes to `HitWarpEvent` transit. The missile pass
  consumes the missile, schedules a delayed explosion, and raises **two** site
  events (`WarpBombEvent` + `MissileDestroyedEvent`). Parametrizing would thread
  mutually-exclusive branches through one method for zero shared logic beyond the
  outer loop.
- They also differ in a **semantic gate**: the ship transit pass skips a dangling
  warp (`warp.Destination is null → continue`), but the bomb trigger has **no**
  destination gate — native bombards whatever warp a missile hits
  (`missileigc.cpp:465-466`), destination only matters for the (unported)
  dual-end explosion. Keeping them separate avoids forcing a fake unification.
- Timer shape for the delayed half: a **stored absolute sim-time** per pending
  bomb, polled every tick — exactly how `Probe`/`Mine`/`Missile` expiry and the
  native warp's own `m_bombs` loop (`warpIGC.h:54-90`, `timeExplosion <= now`)
  work. A `ScheduledWarpBomb(timeExplosion, position, missileType)` record list
  on the sector; `ResolveWarpBombDetonations` pops due entries each tick. Nothing
  detonates before its scheduled instant.

## 3. The explosion — amount is Power, not BlastPower (native-corrected)

Both the task and this project's normal missile blast (P97) frame the warp-bomb
explosion amount as `BlastPower`, but the **native warp-bomb detonation uses the
missile's `Power`**:

`src/Igc/warpIGC.h:65-67` (`CwarpIGC::Update`, the timed bomb-pop):
```cpp
DamageTypeID dtid = pmt->GetDamageType();
float        p    = pmt->GetPower();      // ← Power, not BlastPower
float        r    = pmt->GetBlastRadius();
```
I follow the reference (this repo's fidelity convention): the scheduled explosion
is `Explosion(DamageType, Power, BlastRadius, timeExplosion, warpPosition, null)`.
`Launcher = null`, so `ResolveExplosion`'s friendly-fire gate is inert — every
ship in the blast takes damage, exactly native's `CreateExplosion(..., NULL)`
(`warpIGC.h:77`). Documented here rather than silently "fixing" the task's
`BlastPower` guess to match native.

Out of scope, not ported (per task): `WarpBombLink`/`GetBombs()` and the
late-joiner re-export (`fsmission.cpp:4188-4190`), and the dual-capability
destination-end explosion (`warpIGC.h:70-87`).

## 4. Whole-mission broadcast — proved with real test evidence

`FM_S_WARP_BOMB(188)` is broadcast to the **whole mission**, not same-side. This
is the one genuine behavioural departure from every prior ordnance broadcast in
this project (P88/P90/P94/P99 are all same-side-only), so it is asserted
explicitly, not just relied upon. Native proof:
`FedSrv.CPP:12558` `g.fm.SendMessages(m_pfsMission->GetGroupRealSides(), ...)`
(`GetGroupRealSides()` = all real sides). The port mirrors it in
`Server.BroadcastWarpBomb` (`src/Allegiance.Server/Server.cs`): every peer in
`_peerShips` receives it, regardless of side.

**Test evidence** (`tests/Allegiance.Core.Tests/WarpBombTests.cs`,
`Live_whole_mission_warp_bomb_reaches_the_cross_side_peer_and_the_delayed_blast_lands`):
3 real bots on 2 sides (round-robin A,B,A); a warp injected into sector 0; a real
missile placed at its centre. After the trigger, **all three bots** — including
the cross-side Bravo enemy — receive a decoded `WARP_BOMB(188)` with the correct
`warpidBombed` and `expendableidMissile`; `seat.Shooter.SideId != seat.Enemy.SideId`
guards the cross-side claim. (The server log line ends
`-> 3 peer(s) [WHOLE MISSION]`.) The same-side missile-destroy notice is separate.

## What was verified live (all four scopes)

1. **Whole-mission broadcast** — every peer got `WARP_BOMB(188)`, cross-side
   included, correct warp id + missile-type id (above).
2. **Consumed at trigger time** — the missile leaves the sector on the first tick,
   long before `timeExplosion`; its destroy notice reaches the same-side observer.
3. **Delayed real explosion** — pumping to `timeExplosion` (15 s × 20 ups = 300+
   sim ticks) resolves a real `Explosion` at the warp's position that deals the
   **exact computed falloff damage** (`Power·f²·dtm/HP`, `f = 1 − d/radius`,
   `clusterIGC.cpp:420-429`) to a ship in the blast, and nothing detonates before.
   (Domain test asserts the no-premature loop tick by tick too.)
4. **No regression** — a missile far from any warp expires normally, is removed,
   raises its destroy event, and raises **zero** warp-bomb events, with a warp in
   the same sector (`Domain_missile_far_from_any_warp_expires_normally...`).

Plus `WarpBombWireCodecsWireTests` (12-byte layout pinned byte-for-byte:
`Time`→uint, `WarpID`/`ExpendableTypeID`→short; 3-field round-trip; wrong-fmid /
short-buffer / cbmsg-mismatch rejection).

## Files touched

- `src/Allegiance.Core/Model/Sector.cs` — `ResolveWarpBombTriggers`,
  `ResolveWarpBombDetonations`, `ScheduleWarpBomb`, `ScheduledWarpBomb`,
  `_warpBombs`, `WarpBombDelaySeconds = 15.0`; two calls in `Update`.
- `src/Allegiance.Core/Sim/Missile.cs` — `Missile : …, IMissileIgc` (the existing
  seam `WarpBombEvent(IWarpIgc, IMissileIgc)` needed the marker, the same
  marker-shape pattern `ResolvedWarp : IWarpIgc` already established).
- `src/Allegiance.Core/Wire/WarpBomb.cs` — new codec (188).
- `src/Allegiance.Server/ServerHost.cs` — `WarpBomb` hook + `WarpBombEvent`
  bridge (mirrors `HitWarpEvent`).
- `src/Allegiance.Server/Server.cs` — wiring + `BroadcastWarpBomb` (whole mission).
- `src/Allegiance.Bot/Bot.cs` — `WarpBombs` receive surface (dispatch case).
- `tests/.../WarpBombTests.cs`, `tests/.../Wire/WarpBombWireCodecsWireTests.cs`.

## Build/test

Commit gated on 3 consecutive clean runs of the full suite. **Baseline at commit:
2390 / 2390 passed, 0 failed on 3 consecutive runs** (up from ~2380 going in — the
10 new WarpBomb + wire-codec tests; every pre-existing suite unmodified and green).
Session stats: `session_stats.py 20260907_233503_bbc564`.

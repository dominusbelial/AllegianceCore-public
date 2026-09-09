# P120 — ASTEROID_MINED (198): real-time ore broadcast to observers

Status: IMPLEMENTED + VERIFIED (build clean, full suite 2564/2564 ×3 consecutive)
Branch: master (28c1e80 P119 + P120)
Date: 2026-09-09

## Summary

Phase 4's intro named `ASTEROID_MINED`(198) as the model case for "native
broadcast forms this port does its own simplified way". Investigation confirmed
the seam was already wired but log-only: `Server.OnMine` (raised from
`Sector.ResolveMining` at exactly the native >3.0 ore-change threshold) just
printed. This task finishes that seam — a real `FM_S_ASTEROID_MINED`(198) is now
constructed and broadcast whole-mission to every connected seated peer, closing
the observer gap where no peer could see a rock's remaining ore change in real
time.

## What changed

1. `src/Allegiance.Core/Wire/AsteroidMined.cs` (new) — the `ASTEROID_MINED`(198)
   codec: `SectorID clusterID` (short), `AsteroidID asteroidID` (short),
   `BytePercentage bpOreFraction` (byte, div-240). Same fixed 9-byte
   short/short/byte layout as `AsteroidDestroyed`, differing only in fmid and in
   carrying the rock's remaining ore as a div-240 `CompactQuant.Percent` byte.
   A `ForAsteroid(Asteroid, float newOre)` factory bakes the wire convention in
   (mirrors `ExportAsteroid.FromAsteroid`), so the caller never touches it.

2. `src/Allegiance.Server/Server.cs:4962-4995` — `OnMine` extended: it still logs
   the mine event, and now also builds `AsteroidMined.ForAsteroid(ast, newOre)`,
   packs it, and sends it to every peer in `_peerShips` (whole mission). The
   `newOre` arg is the rock's ore fraction (`asteroidigc.h:249`,
   `ore/oreMax`), matching what native transmits.

3. `src/Allegiance.Bot/Bot.cs` — the Bot decodes `ASTEROID_MINED` into an
   `AsteroidMineds` collection (mirrors the `AsteroidDestroyeds` plumbing), so a
   loopback test can assert real TCP arrival at every peer.

4. Tests (9 new, all green):
   - `tests/.../Wire/AsteroidMinedWireCodecsWireTests.cs` — codec round-trip,
     exact 9-byte layout, `ForAsteroid` div-240 conversion, wrong-fmid/short-buf
     rejection.
   - `tests/.../AsteroidMinedLiveTests.cs` — a real 3-bot / 2-side loopback
     proving whole-mission broadcast to a same-side teammate AND a cross-side
     enemy; a throttle-halt no-broadcast test; and a deterministic unit test
     pinning the >3.0 reporting gate.

## The whole-mission broadcast precedent (the per-side-gate decision)

Native `MineAsteroidEvent` sends `ASTEROID_MINED` behind a per-side
`GetAsteroidCurrentEye(pside)` visibility gate (`FedSrv/FedSrv.CPP:12461`),
looping sides and sending only to the sides whose eye currently sees the rock
(`FedSrv/FedSrv.CPP:12454-12471`). That gate is the same class of cross-side /
scanning-visibility infrastructure this port lacks and does not build for
`PROBES_UPDATE`(92) / `ASTEROIDS_UPDATE`(179) (both gated on `SeenBySide` in the
same batched-update path). Building it is explicitly out of scope for this task.

Following the precedent this port's own sibling message already set —
`ASTEROID_DESTROYED`(86, P102) broadcasts whole-mission with no per-side gate,
whose native handler itself uses `m_pfsMission->GetGroupRealSides()`
(`FedSrv/FedSrv.CPP:12418`) — `ASTEROID_MINED` uses the identical audience:
every connected peer with a seated ship (`_peerShips`), cross-side included.
This is the same established simplification, not a new decision. Verified live:
the cross-side enemy bot received `ASTEROID_MINED` exactly like the same-side
teammate (both asserted >0 arrivals with correct `asteroidID`/`clusterID`).

## ASTEROID_DRAINED (180) — confirmed N/A, not built

Re-verified against the native source directly (not taken on faith):

- Definition: `Inc/Messages.h:930-933` — `SectorID clusterID`, `AsteroidID
  asteroidID`.
- The ONLY call site that would raise it is inside a literal NYI comment block:
  `Igc/asteroidigc.h:228-230`:
  ```
  /* NYI -- re-enable if He3 does not regenerate
     GetMyMission()->GetIgcSite()->DrainAsteroidEvent(this);
  */
  ```
  grep of the whole native tree finds no other `DrainAsteroidEvent` call — only
  the virtual no-op default (`igc.h:4840`) and the FedSrv implementation
  (`FedSrv.CPP:12443-12452`) which is therefore unreachable.
- Why it is dead by design: native asteroids regenerate ore
  (`asteroidigc.h:47-56`; mirrored by this port's `Asteroid.Regenerate`,
  `Sim/Asteroid.cs:166-172`), so a permanently "fully drained" rock is not a
  real, durable, broadcastable state — the very reason the reference disabled
  the event. Building `ASTEROID_DRAINED` here would port dead code.

Conclusion: no code for `ASTEROID_DRAINED`.

## ASTEROIDS_UPDATE (179) — still deferred

Definition `Inc/Messages.h:926-928` is the batched variant form
(`FM_VAR_ITEM(rgAsteroidStates)`) — the same `SeenBySide`-gated batched-state
update function as `PROBES_UPDATE`(92), confirmed blocked on the scanning /
cross-side-visibility system this port does not have. Not this task; unchanged.

## Does this close the asteroid-broadcast trio (86 / 180 / 198)?

Yes for the two real, live messages in this port's architecture:

- `ASTEROID_DESTROYED`(86) — P102: implemented, whole-mission broadcast. Done.
- `ASTEROID_MINED`(198) — P120 (this task): implemented, whole-mission broadcast.
  Done.
- `ASTEROID_DRAINED`(180) — native dead code (NYI-commented sole call site);
  correctly omitted. N/A, never to be built.

So the destructive/mining "the rock's state changed" broadcast pair is complete
(86 + 198); 180 is closed as N/A. `ASTEROIDS_UPDATE`(179), the batched
position/state sync, remains deferred behind the visibility system — it is not
part of the destruct/mining trio and stays out of scope.

## Citations (all read directly)

Native (`~/Development/Allegiance/src`):
- `Inc/Messages.h:1027-1031` — ASTEROID_MINED(198) def.
- `Inc/Messages.h:926-928` — ASTEROIDS_UPDATE(179) def (batched FM_VAR_ITEM).
- `Inc/Messages.h:930-933` — ASTEROID_DRAINED(180) def.
- `Igc/asteroidigc.h:247-251` — >3.0 ore-change threshold →
  `MineAsteroidEvent(this, GetOreFraction())`.
- `Igc/asteroidigc.h:228-230` — NYI-commented `DrainAsteroidEvent` (sole call
  site; dead).
- `FedSrv/FedSrv.CPP:12454-12471` — `MineAsteroidEvent`, per-side
  `GetAsteroidCurrentEye` gate at :12461.
- `FedSrv/FedSrv.CPP:12443-12452` — `DrainAsteroidEvent` impl (unreachable).
- `FedSrv/FedSrv.CPP:12411-12418` — `KillAsteroidEvent` whole-mission
  `GetGroupRealSides()` at :12418 (the P102 precedent).

Port:
- `src/Allegiance.Server/Server.cs:4962-4995` — OnMine + whole-mission
  `ASTEROID_MINED` broadcast (this task).
- `src/Allegiance.Server/Server.cs:4518-4536` — `BroadcastAsteroidDestroyed`
  (P102), the whole-mission pattern reused.
- `src/Allegiance.Core/Wire/AsteroidMined.cs` — new codec.
- `src/Allegiance.Core/Sim/Asteroid.cs:133-151` — MineOre >3.0 gate +
  PendingReport (the throttle the broadcast rides on).
- `src/Allegiance.Bot/Bot.cs` — `AsteroidMineds` decode collection.

## Verification

- Build: 0 warnings, 0 errors (explicit `/home/domingo/.dotnet/dotnet`).
- New tests: 9/9 pass (filtered run).
- Full suite: 2564/2564 passed, 0 failed — three consecutive runs
  (baseline 2555 + 9 new). No `LiveChatSectorWireTests` / `LiveCombatWireTests`
  flakes encountered this cycle.
- No VM used.

Session stats logged via
`python3 ~/Development/allegiance-tools/session_stats.py <session_id>`.

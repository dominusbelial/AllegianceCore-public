# P405 — Ripcord countdown freezes maneuvering and thrust

Status: IMPLEMENTED on `master` (unpushed; Domingo pushes).

## Summary

Native `CshipIGC::ExecuteShipMove` guards both the turn-rate ramp and the
entire engine-thrust block with `if (!m_pmodelRipcord)`
(`shipIGC.cpp:2885` and `:2962`). The port's `Ship.ExecuteMove` previously ran
both blocks unconditionally, so a player could keep dogfighting/evading at
full thrust through their own ~10s ripcord countdown — the port shipped the
ripcord mechanic without its only cost. This change wraps both blocks in the
port's equivalent `if (_ripcordModel is null)` guard, mirroring native exactly
and leaving orientation integration and drag unconditional (native does not
freeze those).

Scope boundary: only the missing freeze-during-countdown guard. No other part
of the ripcord chain (request gate, countdown timer, abort/teleport, energy
cost — P111-P113/P172) was touched.

## Relationship & confirmations

- Finding: `~/Development/allegiance-tools/reference/pi-3d-flight-review-kimik3-20260927.log`, finding C3.
- Native guard (both blocks): `~/Development/Allegiance/src/Igc/shipIGC.cpp:2885` (turn-rate ramp) and `:2962` (engine-thrust block), both `if (!m_pmodelRipcord)`; the enclosing function is `ExecuteShipMove` at `shipIGC.cpp:2852-3054`.
- Native orientation integration (`:2912-2917`) and drag (`:2926-2935`) are UNCONDITIONAL; the engine vector is zeroed (`:2937`) before the gated block and `*pVelocity += thrustToVelocity * (m_engineVector - drag)` (`:3037`) runs unconditionally — so a ripcording ship's velocity changes by drag only. Reproduced exactly.
- Port backing field: `Ship._ripcordModel` (`Ship.cs:781`, `private RipcordTarget?`), exposed as `Ship.RipcordModel` / `Ship.IsRipcordActive` (`Ship.cs:786-790`) and set by the real ripcord arc via `SetRipcordModel` (`RequestRipcordCore`, `Server.Docking.cs:411`). The guard uses the backing field directly, as the brief's `if (_ripcordModel is null)` specifies.

## The exact guard added (`src/Allegiance.Core/Sim/Ship.cs`)

1. **Manœuvre block** (sphere-clamp + turn-rate ramp, formerly `Ship.cs:3683-3698`):
   wrapped in `if (_ripcordModel is null) { ... }`. When a ripcord countdown is
   active the turn rates are left exactly at their pre-rip values — the ramp
   never runs ("no maneuvering").

2. **Engine-thrust block** (local-thrust branches + engine-vector clipping,
   formerly `Ship.cs:3717-3762`): wrapped in the same `if (_ripcordModel is
   null) { ... }`. To mirror native's `m_engineVector = 0` default, the local
   `engineVector` is now initialized to `Vector3.Zero` *outside* the guard and
   assigned inside it. When ripcording the engine vector stays zero, so the
   unconditional trailing `Velocity += thrustToVelocity * (engineVector -
   drag)` applies drag only.

3. **Unconditional (unchanged)**: orientation Yaw/Pitch/Roll + renormalize and
   the drag computation. Exactly matches native, which freezes only the ramp
   and thrust.

No behavior beyond the guard was added; the non-ripcord path is byte-for-byte
the previous code inside the always-true branch.

## Doc-comment correction

`Ship.cs:20-24`: the stale "**No ripcord.** … this ship has no ripcord path, so
both blocks always run" item (which predated P111-P113 and contradicted the
then-implemented ripcord arc) was rewritten as "**Ripcord freezing.**",
documenting that the port now reproduces the native `if (!m_pmodelRipcord)`
guards during an active countdown.

## Verification

### Whole-solution build
All production and test projects (7 src + 4 tests projects; this repo has no
`.sln`): **0 warnings, 0 errors**.

### Unit tests (new: `tests/Allegiance.Core.Tests/ShipRipcordFreezeTests.cs`)
- `Ripcord_active_freezes_turn_rates_and_thrust_only_drag` — arms a ripcord
  via the real `Ship.SetRipcordModel` (the exact setter `RequestRipcordCore`
  uses), commands full yaw + full forward thrust, and asserts: turn rates stay
  pinned at their pre-rip values (0), and velocity changes by **drag only** —
  it equals `v0 · fⁿ` exactly (analytic, where `f` is the per-step drag factor
  `exp(−thrust·dT/Mass/Speed)` from `shipIGC.cpp:2930`), a strict decay, with
  no thrust contribution.
- `No_ripcord_ship_is_completely_unaffected` — the must-fail control: the same
  full-yaw + full-forward inputs on a ship with no ripcord model ramp the turn
  rate up from 0 and accelerate the velocity along the facing axis, i.e.
  maneuvering and thrust run exactly as before the change.

Must-fail-if-removed proof (hard requirement): with both guards temporarily
forced to `if (true)`, `Ripcord_active_freezes_...` goes **RED** — the yaw ramp
runs and turn rate climbs to ~0.109 instead of staying frozen. Guard restored;
test green.

### Live proof (new: `LiveRipcordTests.Ripcord_countdown_freezes_maneuver_and_thrust_until_teleport`)
Real two-bot loopback server (in-process `Server` + two seated `Bot`s, XXV_simple),
a real flying ship. It builds forward cruising speed, engages a real ripcord to
an injected anchor, then commands full yaw + full forward thrust for the whole
countdown. Measured:

```
RIPCORD-P405-FREEZE: ship 8099 during ripcord countdown -> turn rates held ~0 &
speed 59.5->36.1u/s (drag only) under full yaw+thrust, drifted unpowered until
teleport to 1054 fired
```

i.e. during the countdown the ship's turn rate stayed frozen at 0 and its speed
DECAYED from 59.5 to 36.1 u/s under continuous full forward thrust (drag only,
no thrust contribution), drifting unpowered until the teleport fired and cleared
the state (relocated to the anchor's cluster 1054).

### Full suite
Baseline before P405 was 3480 tests; the change adds 3 (2 unit + 1 live) →
**3483 total**. Run twice with the change:

- Run 1: 3480 passed / 3 failed — `LiveTurretGunnerBindingTests.Board_binds...`,
  `LiveStationRescueTests.Lifepod_reaching_rescue_station...`,
  `GameOverPlayersWireTests.Conquest_win_in_one_mission...`.
- Run 2: 3481 passed / 2 failed — `LiveTurretGunnerBindingTests.Board_binds...`,
  `LiveStationRescueTests.Lifepod_reaching_rescue_station...`.

Attribution — none are P405 regressions:
- `LiveTurretGunnerBindingTests.Board_binds...` is in the repo's documented
  persistent machine-state flake list ("fails PERSISTENTLY in isolation AND on
  the pristine tree").
- `LiveStationRescueTests` passes in **true isolation** (alone, 118 ms green);
  `GameOverPlayersWireTests` likewise passes in isolation — classic LiveNetwork
  socket-contention timing flakes.
- Neither file references any P405 symbol (ripcord / `_ripcordModel` /
  `ExecuteMove` → 0 hits in all three files).
- Decisive pristine-tree control: with P405 fully stashed (Ship.cs guard + new
  tests removed; ShipRipcordFreezeTests.cs moved aside), the full suite showed
  a **different** flake set — 4 failures (`P366LeaderBotCommandLiveTests`,
  `LiveWinConditionTests.Multi_mission_disconnect...` [a documented flake],
  `LiveTurretGunnerBindingTests`, `BotFlightTests.Abrupt_disconnect...`). The
  flake set varies run-to-run regardless of this change, confirming these are
  environmental live-wire timing flakes, not a regression.

## Out of scope (untouched, per brief)
- Ripcord request gate / countdown timer / abort / teleport / energy cost
  (P111-P113/P172) — confirmed still correct.
- P368-P404 — unrelated.

## Files changed
- `src/Allegiance.Core/Sim/Ship.cs` — the guard + doc-comment fix.
- `tests/Allegiance.Core.Tests/ShipRipcordFreezeTests.cs` — new (2 unit tests).
- `tests/Allegiance.Core.Tests/LiveRipcordTests.cs` — new live proof test.

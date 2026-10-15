# P446 — apply the side's GlobalAttributeSet to ship speed, thrust, turn rate and torque

## Why this task exists

A gameplay-parity pass found that **completed developments and civilization
bonuses have no effect on how ships fly**, even though the port computes
and broadcasts exactly the multipliers native uses.

- **Native** hull stats are always read through `MyHullType`, which
  multiplies by the owning side's global attributes:
  - `GetMaxSpeed() = speed * GA[c_gaMaxSpeed]`
  - `GetMaxTurnRate(axis) = maxTurnRates[axis] * GA[c_gaTurnRate]`
  - `GetTurnTorque(axis) = turnTorques[axis] * GA[c_gaTurnTorque]`
  - `GetThrust() = thrust * GA[c_gaThrust]`

  (`~/Development/Allegiance/src/Igc/shipIGC.cpp:4528-4546`). The side and
  back multipliers are **not** scaled (`:4548-4555`). The indices are
  `c_gaMaxSpeed = 0`, `c_gaThrust = 1`, `c_gaTurnRate = 2`,
  `c_gaTurnTorque = 3` (`Igc/igc.h:585-589`). `ExecuteShipMove` reads every
  one of them through `m_myHullType` (`shipIGC.cpp:2863, 2900-2901, 2922`),
  and so does `GetTorqueMultiplier` (`shipIGC.h:546`).
- **The port** stores and maintains the real set:
  - `Side.GlobalAttributes` (`src/Allegiance.Core/Model/Side.cs:100`) is
    seeded from the civ's `GasBaseAttributes` (`Side.cs:357`).
  - It is multiplied by each completed development's `Gas`
    (`src/Allegiance.Server/Server.Economy.cs:708`).
  - It is broadcast as `SIDE_ATTRIBUTE_CHANGE(76)` (`Server.Economy.cs:778`).
  - `GlobalAttributeSet.cs`'s own summary says it "does **not** wire every
    gameplay consumer".
- **The sim never reads it.** `Ship.ExecuteMove` uses raw
  `_hull.Thrust` (`Ship.cs:3931`), `_hull.MaxTurnRates[i]` and
  `_hull.TurnTorques[i]` (`:3947-3948`), and `_hull.Speed` (`:3965`).
  `GetTorqueMultiplier` also uses raw `_hull.Speed` (`:4121`). Other raw
  reads: `HullType.Thrust` (`:4620`, obstacle-avoidance accel),
  `HullType.Speed` (`:5071`, wingman speed match), and
  `HullType.Speed/Thrust` (`:5325-5326`).
- **Client side:** `Allegiance.ClientCore` has **no handler** for
  `SIDE_ATTRIBUTE_CHANGE(76)`; grep finds zero references outside the
  server and wire codec. The client can't know its own side's multipliers,
  and later client-side prediction tasks (P447/P448) will need them. The
  autopilot's braking estimate uses raw `hull.Thrust / hull.Mass`
  (`src/Allegiance.ClientCore.MonoGame/Game1.FlightInput.cs:~1316-1330`).

## Scope

1. **One GA-aware accessor set on `Ship`**, mirroring native `MyHullType`:
   effective max speed, thrust, max turn rate(axis) and turn torque(axis).
   Each is the raw hull value times the side's
   `GlobalAttributes[c_ga…]`. A ship with no side (`SideId` null) uses
   identity, 1.0.
   - Add named index constants for at least these four (and any others you
     touch) to `GlobalAttributeSet`, citing `igc.h:585-610`.
   - Resolving `Side` from `Ship` must be cheap. Find how the ship reaches
     its `Side` (through `Mission`) and don't allocate per tick.
2. **Route every flight-physics read through them**: `ExecuteMove`,
   `GetTorqueMultiplier`, and the NPC/steering reads at `:4620`, `:5071`
   and `:5325-5326`. Native reads all of these via
   `m_myHullType`/`GetHullType()`. For each site, check whether native used
   the GA wrapper or the raw hull (for example `shipIGC.cpp:2506` uses
   `GetHullType()->GetMaxSpeed()`, and whether that goes through the
   wrapper matters). Follow native per site and document it.
   `SideMultiplier`/`BackMultiplier` stay unscaled.
3. **Client**: decode `SIDE_ATTRIBUTE_CHANGE(76)` in `ConnectionSession`'s
   dispatch and store it per side in `ClientWorld`, with an accessor like
   `GlobalAttributesFor(sideId)`, identity when unknown. Make the autopilot
   braking estimate use GA-scaled thrust for the local side.
4. **Don't wire other GA consumers here** (shields, scan range, damage,
   mining, ammo speed, and so on). List in the report every remaining
   `c_ga…` consumer that's still unwired, with the native file:line that
   uses it, so the follow-ups can be scoped. Several are already flagged in
   comments: `Ship.cs:3824`, `Weapon.cs:26,234`, `Ship.cs:4854`.

## Explicitly out of scope

- GA consumers other than max speed, thrust, turn rate and turn torque.
  List them only.
- Changing how developments complete or how GA is accumulated. That part is
  already correct (P189).
- Client-side prediction and dead reckoning (P447/P448). This task only
  makes the data available to them.
- Other queued work: P438–P440, P445, P447–P449, and the other areas'
  P441/P450.

## Verification

- A test that fails on unfixed code first:
  - Build a mission side whose `GlobalAttributes` has `c_gaMaxSpeed = 1.5`
    (for example via a real development's `Gas`, or by setting it directly).
  - Fly a ship at full throttle with the real `ExecuteMove` until it
    converges.
  - Assert the top speed is about `1.5 × hull.Speed`.
  - Record the pre-fix failure (top speed equals `hull.Speed`) in the
    report.
- Similar focused tests for thrust (acceleration), turn rate (steady-state
  yaw rate) and turn torque (ramp), plus identity (all 1.0) reproducing
  current behaviour exactly.
- Where the fixture data allows, an end-to-end server test: complete a real
  development from the core whose `Gas` changes one of these four
  attributes, and show the ship's top speed changes. If no real development
  in `PCore001.igc` touches these four, say so, list which do touch them if
  any, and use a synthetic `Gas`.
- A client test: a decoded `SIDE_ATTRIBUTE_CHANGE(76)` lands in
  `ClientWorld`.
- Whole-solution build with `/home/domingo/.dotnet/dotnet build`, 0
  warnings and 0 errors. Full suite with
  `/home/domingo/.dotnet/dotnet test`, 3 consecutive clean runs. Only the
  pre-existing documented live-wire timing flakes are acceptable, and each
  one seen must be named.

## Deliverables

1. The change and tests, committed on **`master`**, not pushed. Check
   `git branch --show-current` before committing.
2. Report doc `docs/reports/P446_SIDE_GA_FLIGHT_SCALING.md`. Include the
   pre-fix failing output, the per-site native-wrapper-or-raw table, which
   real developments and civs change these four attributes (with values from
   `PCore001.igc`), and the list of still-unwired GA consumers.

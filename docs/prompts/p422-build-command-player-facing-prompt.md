# P422 — a real player-issuable Build command (this port has never had one)

## Why this task exists

Domingo, live-testing: *"did you know the constructor needs an actual
asteroid to be deployed?"* Confirmed real against native, then
discovered a deeper gap: this port has **no player-facing Build command
at all**, only an NPC-only autonomous default order.

## Confirmed real native mechanic

`~/Development/Allegiance/src/Igc/shipIGC.h:1695-1707`
(`LegalCommand`'s `c_cidBuild` case) — the real targeting rule: a
`c_ptBuilder`-piloted ship's Build command is legal **only** against an
`OT_asteroid` target, and only if that asteroid
`HasCapability(m_abmOrders)` (the ship's own orders bitmask must match
the asteroid's buildable capability — not every asteroid is buildable,
confirmed by `RockTextureFactory.cs`'s own resource-type classification
this port already decodes, which includes a `0x08 buildable` bit).
`shipIGC.cpp:4012-4018`'s waypoint dispatch confirms the same: `OT_asteroid`
+ `c_cidBuild` + `c_ptBuilder` → `Waypoint::c_oEnter` (fly to and enter
the asteroid), anything else → plain `c_oGoto`.

**What actually happens on arrival** (traced this session,
`~/Development/Allegiance/src/FedSrv/FedSrv.CPP:13733-13767`,
`CreateBuildingEffect`): a real `IbuildingEffectIGC` object
(`Igc/buildingeffectigc.h`) is created at the asteroid, anchored to both
the asteroid and the builder ship, using the builder's own
`GetBaseData()` (a real `IstationTypeIGC*` — the station type it's
configured to build). This is a **timed** effect (`Update`,
`buildingeffectigc.h:160-225`: envelope → grow → opaque → shrink →
complete phases) that, per its own `Terminate()`
(`buildingeffectigc.h:112-134`), **consumes the builder ship**
(`KillShipEvent` with `amount=0.0`, i.e. the ship is expended as the
real cost of construction — not destroyed by damage) when the effect
completes. **Not yet located this session**: the exact call site that
creates the real finished `IstationIGC` once the effect completes —
`fsmission.cpp:4209`'s `GetBuildingEffect()` read was the closest lead
found, but the actual station-creation trigger needs a fresh, focused
trace by whoever picks this up. Don't guess at it; find the real call.

## Confirmed port gap

`src/Allegiance.Core/Sim/Ship.cs:1542-1544`'s own doc comment: *"This
port has no `CidMine`/`CidBuild` command constants and nothing ever
sets a ship's `AcceptedTarget` to an asteroid with a mine-class
command."* `Ship.PilotTypeBuilder` (`Ship.cs:771`) exists only as an
NPC autonomous default-order behavior (P165, `Ship.cs:5639+`) — there is
no `CommandID` constant for Build, no wire path for a real player to
target an asteroid and issue a Build order, and no building-effect
object/timed-construction mechanic in this port at all.

## Scope

1. **Finish the native trace this session left open**: find exactly
   where a completed `BuildingEffect` creates the real finished
   station (likely `fsmission.cpp` or `FedSrv.CPP`, near the
   `GetBuildingEffect()`/`AsteroidUpdate` machinery in
   `buildingeffectigc.h:312+`) — cite it precisely before building
   anything.
2. Add the real `CidBuild`/targeting-legality gate (mirroring
   `LegalCommand`'s asteroid+capability check above) to this port's
   existing Plan/command wire path — reuse whatever command-issuing
   infrastructure already exists for `Goto`/`Dock`/etc. (check
   `Server.ShipCommands.cs` for the real pattern to extend), don't
   invent a parallel one.
3. Add a real `BuildingEffect`-equivalent model: a timed construction
   object anchored at the target asteroid, consuming the builder ship
   on completion (reuse whatever ship-consumption/`KillShipEvent`-
   equivalent path P411/P412's work already established), and creating
   a real new station on completion per the trace in step 1.
4. Confirm the "only Builder-role pilots, only capability-matching
   asteroids" gate is real and tested — a request that fails either
   condition must be refused exactly as native refuses it, not silently
   accepted.

## Explicitly out of scope

- The Layer pilot's parallel `c_cidBuild` case (`shipIGC.h:1697-1698`,
  targeting a buoy/warp to lay mines/probes) — a completely separate
  mechanic from station construction; don't conflate the two.
- Any new client UI/key-binding for issuing the command (a "target
  asteroid, press Build" flow) — this task is the real server-side
  mechanic; a follow-up can wire a client control once this lands.
  If a trivial, obviously-correct key/UI hookup falls naturally out of
  this work, note it plainly rather than silently expanding scope.
- P368-P421 — unrelated, don't touch.

## Verification — real proof, not just build/test

- A test proving a Build request against a non-buildable asteroid, a
  non-asteroid target, or from a non-Builder-piloted ship is refused.
- A test proving a legal Build request creates a real timed
  construction effect, and that it consumes the builder ship and
  creates a real new station on completion.
- **Live proof, required**: issue a real Build command from a real
  Builder-hulled ship against a real buildable asteroid and confirm the
  full arc — approach, timed construction effect visible, builder ship
  consumed, a real new station exists afterward, dockable.
- Whole-solution build: 0 warnings, 0 errors.
- Full test suite, run at least 2x: only the pre-existing documented
  live-wire timing flakes expected.

## Deliverables

1. The feature, committed on `master` (not pushed — Domingo pushes).
2. Report doc `P422_BUILD_COMMAND.md` in the repo root: the completed
   native trace (exact station-creation call site), the wire path
   added, and the verification performed.

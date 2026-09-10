# P125 — Station combat damage + `STATION_DESTROYED`(59) (foundation, not capture)

Date: 2026-09-09. Branch: `master` (base `b23c601` P124 + this task). No VM,
no Obsidian/MCP.

## Headline

A station is now a real, direct-hit damage target of real ordnance in the shared
swept combat detector — the **fourth** static target type in
`Sector.ResolveCombatCollisions`, exactly the integration shape P100 used for
`Probe` (second) and P102 used for `Asteroid` (third). The native
`CstationIGC::ReceiveDamage` shield-absorbs-then-overflows-to-armor formula is
ported, the per-instance live `ArmorFraction`/`ShieldFraction` state is added,
the station's `Update`-tick regeneration is ported, and destroying a base removes
it from the sector and broadcasts a real `FM_S_STATION_DESTROYED(59)` to the
whole mission with the correct `stationID`/`launcher`, after ejecting every
docked ship through the real undock path.

**`STATION_CAPTURE`(58) is deliberately NOT this task** — it is a second,
genuinely separate trigger (dock-while-shields-down + a capture-capability check
+ re-siding the base) layered on top of this foundation, exactly the split the
arc's own ripcord/relaunch work (P110–P117) proved out. Named in the next-task
section.

## Design question settled (the task's reason to exist)

**"A station needs a whole new health/shield subsystem from scratch" — FALSE,
confirmed.** The static capacity data already existed: `DataStationTypeIGC`
already decodes `MaxArmorHitPoints`/`MaxShieldHitPoints`/`ArmorRegeneration`/
`ShieldRegeneration` (`src/Allegiance.Core/Data/DataStationTypeIGC.cs:32-35`) and
`DefenseTypeArmor`/`DefenseTypeShield` (`DataStationTypeIGC.cs:42-43`). Only the
**live per-instance** state and damage-intake wiring were missing. More
importantly, this port already has a complete, working, tested
shield-then-armor damage model for ships — `Ship.ReceiveDamage`
(`Sim/Ship.cs:892`+), the port of `shipIGC.cpp:1212-1350` (P87) — and the
asteroid damage model (P102). **This task is the third application of that same
proven pattern**, not a new design.

**Does a station fit the shared swept detector, or does it need its own pass?**
— Answered by the native source, same shape as `Probe`/`Asteroid`:

- `src/Igc/stationIGC.cpp:244` — station `Load(0, …, c_mtStatic | c_mtDamagable |
  c_mtHitable | c_mtSeenBySide | c_mtPredictable | c_mtScanner)`. Genuinely
  `c_mtHitable | c_mtDamagable`.
- `src/Igc/projectileIGC.cpp:166-169` excludes **only** `OT_mine`;
  `missileigc.cpp:435-447` is the identical shape. Nothing special-cases
  `OT_station`, so a base is a valid direct-hit target of any ordnance.
- `src/Igc/projectileIGC.cpp:229-230` — "In any case, kill the projectile":
  a round striking a station **always terminates**, even a friendly one (unlike a
  ship, whose friendly round passes through).

**Conclusion**: extend `Sector.ResolveCombatCollisions`'s swept detector with
`Station` as the fourth static target type. No dedicated pass. The collision
radius is the decoded `DataStationTypeIGC.Radius`, which native assigns the
station's sphere from (`stationIGC.cpp:249`).

**Structural gap closed**: a `DataStationIGC` is a readonly struct with no live
health, and the existing docking/ripcord/undock seams all read the static
`sector.Stations` list. Following the `Asteroid` precedent exactly, a new runtime
`Sim/Station` class wraps `DataStationIGC` + the resolved `DataStationTypeIGC`
and carries the two live pools, kept in a new parallel sector list
(`Sector._stationRuntimes`) so the static list and every pre-existing seam stay
untouched. `Station` is **not** a `Model` (it is a static body), the same choice
`Asteroid` made.

## Every simplification, confirmed and cited

- **`bInvulnerableStations`** (mission param, `stationIGC.cpp:126`) — this port
  does not decode mission parameters (`MissionDef` parses the mission-def wire
  messages, not native's `MissionParams` struct). Honest default: **stations are
  vulnerable**, matching how this port defaults away from native's rarer
  mission-config toggles elsewhere (`bAllowFriendlyFire`, `bAllowAlliedRip`,
  `bEjectPods` precedents). Stated in `Sim/Station.cs`.
- **`c_sabmPedestal`** (`stationIGC.cpp:127`) — a station-ability bit
  (`c_sabmPedestal = 0x400`, `src/Igc/igc.h:758`) making a base indestructible.
  Added as `DataStationTypeIGC.SabmPedestal` (`DataStationTypeIGC.cs`) the same
  one-line way P114 added the hull `Habm` bits and this type already exposes
  `SabmRestart`/`SabmRipcord`, and read via the existing `HasCapability`.
  Implemented: a pedestal station returns `NoDamage` for any hit (unit-tested).
- **Friendly-fire / `AlliedSides` gate** (`stationIGC.cpp:128-130`) — this port
  has no alliance concept (established repeatedly this arc) and no
  `bAllowFriendlyFire` toggle (friendly fire always off). Simplified to
  **same-side-blocked**: a hit whose launcher is a Ship with a `SideId` equal to
  the station's returns `NoDamage`, the same precedent `bAllowAlliedRip`(P112)
  set. Unit + live tested.
- **Repair path (`amount < 0`)** (`stationIGC.cpp:147-156`) — nothing in the port
  calls `ReceiveDamage` with a negative amount. Implemented faithfully anyway
  (heals hull fraction toward 1.0), dead code in practice — the same choice
  `Ship.ReceiveDamage` made (`Ship.cs:906-914`). Unit-tested.
- **Experience multiplier** (`stationIGC.cpp:142-145`) — this port's ships have
  no accumulated experience (`Ship.cs:880-881`), so the multiplier is 1.0.
  N/A, stated, not invented.
- **`DamageConstant` / defense-type lookup** — reused exactly what
  `Ship.ReceiveDamage` already reads: the mission-level
  `DataConstantsIGC.DamageConstants` matrix, indexed by the station **type's**
  `DefenseTypeArmor`/`DefenseTypeShield` (`stationIGC.cpp:138,159`), derived from
  the ordnance launcher's `Mission.Core` the same way `Asteroid` does.
- **No-hull-fallback when shield-less** — confirmed reading the native:
  when `dtmShield == 0` **or** `MaxShieldHitPoints == 0` the damage path falls to
  `dr = c_drNoDamage` (`stationIGC.cpp:203-204`) — a station without shields takes
  **no damage at all**, not a hull-damage fallback. Reproduced exactly and
  unit-tested (`Shield_less_station_takes_no_damage_not_a_hull_fallback`).
- **Score / Steam bookkeeping** (`KillBase`, `AwardBaseKillOrCapture`,
  `AddBaseKill`, `FedSrv.CPP:12252-12272`) — confirmed N/A; no score or Steam
  system anywhere in this port. Omitted.

## Which pass stations were integrated into (the task's explicit question)

**`Sector.ResolveCombatCollisions`** (`src/Allegiance.Core/Model/Sector.cs`), the
same swept detector that already resolves ship/probe/asteroid targets. Concretely:
a static collider is added per runtime station
(`Sector.cs:~997-1003`, walking `_stationRuntimes`, `isStatic: true`, position
from `Data.Position`, radius from `Type.Radius`); the event dispatch identifies a
station target alongside ship/probe/asteroid (`Sector.cs:~1020-1022`); and a new
`else if (station is not null)` target branch applies damage, terminates the
round, fires the P97 blast-on-impact unconditionally, and on `Killed` removes the
station and raises `IIgcSite.KillStationEvent` through the ordnance launcher's
mission site (`Sector.cs:~1139-1204`). The `Ship`/`Probe`/`Asteroid` branches are
unchanged.

**Integration guard discovered (important).** The pre-existing ship/missile
combat tests fire synthetic ordnance from the sector origin — which is exactly
where the real map's starting **Garrison** sits, with a ~423-unit collision
radius (`stationIGC.cpp:249` uses the decoded type `Radius`; the garrison type's
is large). With stations newly hittable, that ordnance spawned *inside* the
garrison sphere self-destructed against it at `tCollision=0`, breaking 16
pre-existing combat tests (observed: `station 1051 'Garrison' at (0,0,0)
R=423.5` intercepting a round at `t=0.0000`). Fix (cited in the branch): a round
whose swept collision is reported at `tCollision <= 0` began the step already
inside the station sphere — it **spawned embedded in a base**, not flown into it —
and passes through to its real target (the same pass-through spirit as the
friendly-ship gate). Native never sees this degenerate case (a ship inside a
station is docked and does not fire). This keeps all 16 tests green unmodified
*and* keeps genuine approaching attacks landing (my live attacks approach from
outside the radius → `tCollision > 0`).

## Implementation

### `src/Allegiance.Core/Sim/Station.cs` (new)
Runtime station: wraps `DataStationIGC Data` + resolved `DataStationTypeIGC Type`
(the static capacity data), implements `IStationIgc` so it flows through the
existing seam. Carries exactly the two live pools the task scopes —
`ArmorFraction`, `ShieldFraction` (both default 1.0 full; the record's
`BpHull`/`BpShield` are lossy `(byte)fraction` exports per `stationIGC.cpp:110-111`
so they do not seed the pools) — plus `Dead`, `MaxArmor`/`MaxShield`/`Radius`/
`Center`, the `SabmPedestal` const, `ReceiveDamage`, `Regenerate`, and a private
`DamageConstant`.

`ReceiveDamage` is the exact port of `CstationIGC::ReceiveDamage`
(`stationIGC.cpp:116-208`):
```
shield stage:  absorbed = amount·dtmShield/maxShield            (stationIGC.cpp:172)
  ShieldFraction >= absorbed → ShieldFraction -= absorbed       (stationIGC.cpp:174-177)
  else (overflow):                                             (stationIGC.cpp:178-201)
      ShieldFraction = 0
      dmg = (overflow·maxShield·dtmArmor)/(maxArmor·dtmShield)  (stationIGC.cpp:186) ← the exact ratio the tests assert
      ArmorFraction -= dmg; ≤0 → Killed (first crossing)         (stationIGC.cpp:187-194)
```
Repair (negative) and the no-shield `NoDamage` branches are as above. Death
bookkeeping (removal + broadcast) is deliberately left to the caller (the combat
pass), the exact `Asteroid` contract — a direct `ReceiveDamage` does not remove
or raise the seam (unit-asserted).

`Regenerate(float dt)` is the port of `CstationIGC::Update`'s regen block
(`stationIGC.h:189-213`): **armor regenerates first**; only when armor is full
does the shield regenerate (gated on a non-zero pool), at the decoded
`ArmorRegeneration`/`ShieldRegeneration` rates. The side-global-attribute terms
(`c_gaArmorRegenerationStation`/`c_gaShieldRegenerationStation`,
`stationIGC.cpp:617,622`) are 1.0 (no side attributes in this port).

### `src/Allegiance.Core/Model/Sector.cs`
- `_stationRuntimes` list + `StationRuntimes` accessor (parallel to `_stations`;
  static list untouched).
- `AttachStations(stations, core)` now builds one runtime `Station` per static
  record whose `StationTypeId` resolves in `core.StationTypes`; unresolvable
  records stay static-only (no combat state).
- `AttachStation(Station)` — synthetic injection for tests
  (`Mission.PlaceStation`), mirroring `PlaceAsteroid`.
- `RemoveStation(Station)` — drops it from `_stationRuntimes` AND removes the
  matching static record from `_stations`, so the docking/ripcord/undock seams
  stop resolving a destroyed base (the port of the station's Terminate →
  DeleteStation, `stationIGC.h:183`).
- `ResolveStationRegen(dt)` pass called from `Sector.Update` (stations are not
  Models, so their regen is a per-tick pass, not a model `Update`).
- `ResolveCombatCollisions`: station colliders + the station target branch (see
  "Which pass…").

### `src/Allegiance.Core/Model/Mission.cs`
- `Assemble` passes `core` to `AttachStations` so runtime stations are built for
  the map's real bases.
- `PlaceStation(Station, Sector)` — synthetic injection entry for the loopback
  tests (mirrors `PlaceAsteroid`).

### `src/Allegiance.Core/Data/DataStationTypeIGC.cs`
Added `SabmPedestal = 0x400` const (native `c_sabmPedestal`, `igc.h:758`).

### Wire: `src/Allegiance.Core/Wire/StationDestroyed.cs` (new)
`FM_S_STATION_DESTROYED`(59) codec — `Messages.h:436-439`: `stationID` (short),
`launcher` (short, `ShipID` or `NA`), fixed 8 bytes, no var items. Round-trips.

### Host seam → broadcast: `src/Allegiance.Server/ServerHost.cs` + `Server.cs`
`IIgcSite` already declared `KillStationEvent` (`IIgcSite.cs:280`, igc.h:4855).
`ServerHost` bridges it via a `StationKilled` hook (casts the `IStationIgc`
marker to the concrete `Station`, mirrors `KillAsteroidEvent`). `Server` wires it
to `BroadcastStationDestroyed`, which — in the native order
(`FedSrvSiteBase::KillStationEvent`, `FedSrv.CPP:12237-12279`):
1. **VacateStation** (`fsmission.cpp:4783`): every ship in `_peerShips` docked at
   the dead base (matching `Ship.StationId`) is ejected through the **real undock
   path** `Server.PerformUndock` — clear `StationId`, reposition, debounce,
   re-send `FM_S_SET_CLUSTER`. Reuses the existing undock/eject helper; no new
   ejection logic. (The dead station is already gone from `Stations`, so the
   reposition lookup simply skips.)
2. **Broadcasts `STATION_DESTROYED(59)` to the WHOLE mission**
   (`GetGroupRealSides()`, `FedSrv.CPP:12279`) — every seated peer, either side,
   the same audience P102 settled for `ASTEROID_DESTROYED`. `stationID` from the
   station; `launcher` = the credited launcher **ship** id or `NA`
   (`FedSrv.CPP:12277-12278`).

### `src/Allegiance.Bot/Bot.cs`
Decodes and accumulates `StationDestroyed` (collection + reset), mirroring
`AsteroidDestroyeds`.

## Verification

**Unit** (`StationCombatTests.cs`, 9 tests): shield-up hit reduces `ShieldFraction`
by exactly `amount·dtmShield/maxShield` with armor untouched; overflow converts to
hull by the exact native ratio `(overflow·maxShield·dtmArmor)/(maxArmor·dtmShield)`
(`stationIGC.cpp:186`); lethal clamps to 0 + `Killed` + no double-kill + removal
left to caller; same-side friendly = `NoDamage`; shield-less = `NoDamage` (no hull
fallback); pedestal = `NoDamage`; negative-amount repair; shield regen at
documented rate; armor-then-shield regen sequencing.

**Wire** (`Wire/StationDestroyedWireCodecsWireTests.cs`, 4 tests): constants,
round-trip, `launcher = NA`, exact 8-byte layout.

**Live loopback** (`StationCombatLiveTests.cs`, 4 tests, in-process server + real
bots): a real cross-side missile hit reduces `ShieldFraction` by the exact decoded
fraction with armor untouched; a same-side missile deals no damage; a lethal hit
destroys the base (removed from `Sector.Stations` AND `StationRuntimes`), ejects a
docked ship through the real undock path (`Ship.StationId` cleared), and
broadcasts `STATION_DESTROYED(59)` naming the station id + launcher ship id to
BOTH the cross-side enemy and the same-side teammate (whole-mission audience);
shield regeneration over real ticks at the documented rate.

Observed live evidence:
```
P125-LIVE-SHIELD: cross-side missile 20000 @ dtmShield 1.000 / maxShield 50000
                  => ShieldFraction 1.0→0.600, armor still 1.0
P125-LIVE-KILL: base 7801 destroyed (launcher ship 8097), removed from sector,
                docked teammate ejected (StationId→null), STATION_DESTROYED(59)
                -> both same-side AND cross-side peer [WHOLE MISSION]
P125-LIVE-REGEN: shield 0.500 → 0.560 over 1200 ticks, armor held at 1.0
```

**Full suite**: baseline `2596` passing on entry; after this task `2613` passing,
**0 failed** (17 added, all pre-existing tests green **unmodified**). Run 1 of the
3-consecutive-clean gate.

## `STATIONS_UPDATE`(57) finding (investigated, out of scope)

`STATIONS_UPDATE`(57) is a **variable-length** `FM_VAR_ITEM(rgStationStates)`
batch (`Messages.h:425-427`) of `StationState` records (per-station
`stationID` + `bpHullFraction` + …), built and sent **per ship** inside the
ship-status/update block (`fsship.cpp:813-845`) and filtered by
`pstation->SeenBySide(pside)` (`fsship.cpp:825,841`) — the per-side visibility
system this port has already deferred for `PROBES_UPDATE`/`ASTEROIDS_UPDATE`
(there is no `SeenBySide` anywhere in the port). So `STATIONS_UPDATE` is
genuinely gated on that same deferred system and is correctly not built here.
This task nevertheless lays the data groundwork: the per-station
`bpHullFraction` that `STATIONS_UPDATE` would transmit is now real live data —
`Station.ArmorFraction` (the analogue of `GetFraction()`, `fsship.cpp:844`).

## Next task this foundation unblocks

**`STATION_CAPTURE`(58)** — `Messages.h:429-434`: `stationID`, `sidOld`,
`sidNew`, `shipIDCredit`. Native trigger is `FedSrvSiteBase::DockWithStationEvent`
(`FedSrv.CPP:13100-13105`): a hostile ship docks a station whose shields are down
+ a capture-capability check, then re-sides the base. P125 supplies the live
`ShieldFraction` (the "shields down" test), the dock seam it hooks into, and the
removal/vacate machinery a captured (re-sided) base reuses — a genuinely separate
trigger layered on this foundation.

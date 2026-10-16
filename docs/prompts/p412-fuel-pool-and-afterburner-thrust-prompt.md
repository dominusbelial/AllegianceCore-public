# P412 — real fuel pool + real afterburner thrust boost (both currently no-ops)

## Why this task exists

P411 (landed) wired parts/ammo/flag loot drops on ship death, faithfully
porting native's `KillShipEvent`. Its fuel branch is dead code by
necessity: this port has never tracked fuel as a live per-ship value
(`DataHullTypeIGC.MaxFuel` is static core data only), so native's
`if (fuel > 0)` guard can never be true here. Investigating why revealed
a bigger, honestly-scoped gap: **fuel's only real consumer, the
afterburner, is itself a complete no-op in this port** —
`Ship.cs:3808` hardcodes `thrustRatio = 0` ("no AB"), and there is no
afterburner power-ramp/activation class at all, just an inert
`EquipmentType.Afterburner` bookkeeping tag. This task builds both,
since one without the other is pointless: a fuel pool with nothing
draining it, or a thrust boost with unlimited free fuel.

## Confirmed real native mechanic

**Fuel pool** (`~/Development/Allegiance/src/Igc/shipIGC.h:623-648`):
`m_fuel`, a live float clamped to `GetMaxFuel()` (hull stat). `SetFuel`
posts "Fuel depleted." when it transitions from >0 to 0 (with a
half-mount-rate reload debounce, `m_timeReloadFuel`) — the exact same
notification/debounce shape this port's `Ship.Ammo` already implements
faithfully (`Ship.cs` ammo-depleted debounce, ~line 2378-2442); mirror
that pattern exactly for fuel, don't re-derive it. Other real touch
points:
- Fuel-pickup powerup (`shipIGC.cpp:1341-1342`): `c_pcFuel` treasure →
  `SetFuel(FLT_MAX)` (clamped to max by `SetFuel` itself — a full
  refill).
- Child ship spawn (`shipIGC.cpp:3538`): inherits parent's current fuel
  (`SetFuel(pshipParent->GetFuel())`) — mirror however the port already
  handles the analogous ammo/hull inheritance for child ships.
- Station docking refuel (`stationIGC.cpp:449-489`,
  `CstationIGC::RepairAndRefuel`): ammo + fuel → max on dock. This
  port's `Ship.cs:3496-3568` (`RepairAndRefuel`-equivalent) already
  documents fuel as a no-op at `:3517-3525,3557` — wire the real top-up
  there, mirroring exactly how ammo already tops up in the same method.
- Fuel pack mount/consumption (native `c_packFuel`, `Igc/igc.h:550`):
  this port's `Ship.cs:2269-2320` already drains an ammo pack
  (`c_packAmmo`) into `Ship.Ammo` on the identical `OT_pack` path; the
  fuel-pack branch at `Ship.cs:2317-2319` currently says "SetFuel is a
  documented no-op" — wire the real drain into the new `Ship.Fuel` there.

**Afterburner thrust boost** (`shipIGC.cpp:2937-2959`, inside
`ExecuteMove`):
```cpp
bool afterF = (m_stateM & afterburnerButtonIGC) != 0;
float thrustRatio = 0.0f;
IafterburnerIGC* afterburner = m_mountedOthers[ET_Afterburner];
if (afterburner) {
    float abThrust = afterburner->GetMaxThrustWithGA();
    if (afterF) thrustRatio = abThrust / thrust;
    afterburner->IncrementalUpdate(timeStart, timeStop, false);  // bUseFuel=false HERE
    float power = afterburner->GetPower();
    if (power != 0.0f)
        drag += (power * abThrust) * myBackward;
}
```
**Fuel is NOT consumed by this call** — `bUseFuel=false`. The real,
fuel-consuming tick is the afterburner model's own regular per-tick
`Update`, separate from `ExecuteMove`:
```cpp
// afterburnerIGC.cpp:60-62
void CafterburnerIGC::Update(Time now) {
    IncrementalUpdate(m_ship->GetLastUpdate(), now, true);  // bUseFuel=true HERE
}
```
`IncrementalUpdate` itself (`afterburnerIGC.cpp:103-164`): a
mount-ramp gate (`m_mountedFraction`, same shape every other part's
mount-ramp already uses in this port — reuse, don't reinvent), then:
activation (`bActivated = button-held && fuel != 0`), a power ramp
(`m_power += dt*onRate` while activated, `-= dt*offRate` while not,
clamped [0,1]), and while active + `power != 0`:
`fuelUsed = power * fuelConsumption * maxThrust * dt`; if `fuelUsed <
fuel`, drain it; else zero fuel and deactivate ("out of gas").

**thrustRatio's actual effect**: this port's `ExecuteMove` already has
the afterburner-shaped thrust branch the 2026-09-27 kimi-k3 review
confirmed exists (`Ship.cs:3808`, currently hardcoded
`thrustRatio == 0 (no AB)`) — wire the real computed value in instead
of the constant.

## Scope

1. Add a real `Ship.Fuel` field, mirroring `Ship.Ammo`'s exact shape
   (clamp to max, depleted-notification debounce, reload-cooldown
   timing) — check `Ship.Ammo`'s real property/backing-field name and
   match its conventions precisely.
2. Add real afterburner state (mount-ramp fraction — reuse the existing
   part mount-ramp mechanism; activation; power ramp with real
   `onRate`/`offRate`/`fuelConsumption`/`maxThrust` read from the
   mounted afterburner's real `DataPartTypeIGC`/afterburner-specific
   stats — confirm exactly which decoded fields already carry these,
   they should already exist since this port already decodes part-type
   data faithfully).
3. Wire fuel consumption into the real per-tick update path (wherever
   this port's per-model `Update`/`OnUpdate` dispatch already runs other
   parts' incremental updates — reuse that dispatch, don't add a new
   one), using native's exact formula.
4. Wire `thrustRatio`/drag into `Ship.ExecuteMove` (`Ship.cs:3808` and
   surrounding thrust-branch code) reading the real afterburner power
   state instead of the hardcoded zero.
5. Wire every other real fuel touch point named above: fuel-pack
   mount-drain (`Ship.cs:2317-2319`), station `RepairAndRefuel` top-up
   (`Ship.cs:3517-3525,3557`), fuel-treasure pickup full-refill
   (wherever the port's existing ammo/hull/shield powerup-pickup
   handling lives — mirror it), child-ship fuel inheritance on spawn.
6. Close the loop on P411: add the fuel-drop-on-death branch to
   `Ship.CreateDropTreasure`'s caller in the kill-ship path (the same
   site P411 added parts/ammo/flag), now that `fuel > 0` can actually be
   true. Reuse `Ship.CreateDropTreasure` exactly as P411 already does
   for the other categories.

## Explicitly out of scope

- Any other afterburner-adjacent visual/audio effect (engine glow,
  trail particles, sound cues) — flight-model and resource-pool
  correctness only; note plainly in the report if you spot an obvious,
  cheap visual hookup, but don't build it speculatively.
- P368-P411 — unrelated, don't touch.

## Verification — real proof, not just build/test

- A test proving `Ship.Fuel` depletes at exactly native's rate while the
  afterburner is active and the button is held, and stops draining
  (deactivates, "out of gas") at zero.
- A test proving `thrustRatio`/drag actually changes `ExecuteMove`'s
  output speed/thrust when the afterburner is active vs. inactive vs.
  unmounted (a real, measurable flight-model difference — this is the
  actual point of the task).
- A test proving fuel refills correctly from: a fuel pack mount, station
  docking, and a fuel-treasure pickup (each to the real max, not an
  arbitrary value).
- A test proving a destroyed ship with remaining fuel now actually rolls
  the P411 fuel-drop (same 1-in-5 odds as parts/ammo), and a ship with
  zero fuel never does.
- **Live proof, required**: hold the afterburner button on a real ship
  with a mounted afterburner and confirm a real, visible speed increase
  that depletes over time and cuts out when fuel is exhausted; dock and
  confirm fuel refills to max; destroy a ship with fuel remaining and
  confirm a fuel treasure sometimes appears.
- Whole-solution build: 0 warnings, 0 errors.
- Full test suite, run at least 2x: only the pre-existing documented
  live-wire timing flakes expected.

## Deliverables

1. The feature, committed on `master` (not pushed — Domingo pushes).
2. Report doc `P412_FUEL_POOL_AND_AFTERBURNER_THRUST.md` in the repo
   root: the real `Ship.Fuel` shape, the afterburner state added, every
   touch point wired, the P411 fuel-drop connection, and the
   verification performed.

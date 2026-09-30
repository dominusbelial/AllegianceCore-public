# P461 — Ship energy never recharges in flight

## Summary

Added the native in-flight energy recharge to `Ship.OnUpdate`
(`src/Allegiance.Core/Sim/Ship.cs`), a direct port of `CshipIGC::Update`
(`src/Igc/shipIGC.cpp:295-303`). Before this task the port had NO recharge: the
only writers of `Energy` were spawn / energy power-up / parent-copy / station
refit / cloak drain / weapon spend, so energy-per-shot guns drained the pool and
stayed empty until the ship docked, and a cloak's drain was permanent.

## The fix

`Ship.OnUpdate` (Ship.cs:6514) now runs the recharge step **before**
`UpdateCloak` (Ship.cs:6520), exactly where native recharges before it calls
its mounted parts' Update — cloak gets "first dibs on energy" then weapons
(shipIGC.cpp:306-343):

```csharp
if (ParentShip is null && StationId is null &&
    (HullType.RechargeRate > 0.0f || Energy != 0.0f))
{
    Energy += HullType.RechargeRate * (float)dt;
    float eMax = HullType.MaxEnergy;
    if (Energy > eMax) Energy = eMax;
    else if (Energy < 0.0f) Energy = 0.0f;
}
```

- Adds `RechargeRate · dt` and clamps to `[0, HullType.MaxEnergy]`.
- Gated on `(rechargeRate > 0) || (energy != 0)` — the mmf 03/22/07 "don't let
  energy go negative" fix (shipIGC.cpp:296).
- Uses the base `HullType.MaxEnergy` as the ceiling. **No** `c_gaMaxEnergy`
  side multiplier (that is P467b's scope).
- The `ParentShip`/`StationId` conjuncts reproduce native's `Update`
  early-outs (see Findings below). `Weapon.FireWeapon`'s energy-deficit math is
  untouched — it already assumed recharge and is now simply correct.

## Verification

### New tests (tests/Allegiance.Core.Tests/EnergyRechargeTests.cs)

| Test | What it proves | Must-fail |
|---|---|---|
| `Ship_recharges_energy_in_flight_up_to_hull_max` | an energy-weapon hull fires the pool down, then (trigger released) the pool recovers by exactly `RechargeRate·Δt` and clamps at `MaxEnergy` | ✔ (red when recharge removed) |
| `An_active_cloak_with_enough_recharge_never_bottoms_out` | an active cloak whose draw is covered by recharge holds the small pool above zero instead of collapsing to 0 | ✔ (red when recharge removed) |
| `A_lifepod_recharges_in_flight_like_any_in_cluster_hull` | the recharge gate does not special-case a lifepod out (synthetic lifepod-capable hull with a real pool regenerates) | ✔ (red when recharge removed) |
| `A_docked_ship_does_not_recharge` | `StationId != null` → no recharge (native never sims a docked ship) | n/a (passes both ways — asserts absence) |
| `A_boarded_ship_does_not_recharge` | `ParentShip != null` → no recharge (native `Update` early-outs, shipIGC.cpp:271) | n/a (passes both ways — asserts absence) |
| `Own_ship_update_reports_real_energy_fraction` | the HUD energy gauge's server feed (see Item 4) reflects a drained pool and recovery | ✔ (red when FromModel energy unwired) |

**Must-fail evidence (recorded before the fix landed):** with the recharge
block removed from `Ship.cs`, the three recharge-dependent tests fail
(`Ship_recharges…`, `An_active_cloak…`, `A_lifepod…` all red), while the two
assert-absence tests (docked, boarded) stay green. With the fix in place, all
five + the HUD test pass in isolation.

### Build / suite
- Whole-solution build `/home/domingo/.dotnet/dotnet build AllegianceCore.slnx`
  → **0 warnings, 0 errors** (bare `dotnet` is the broken snap stub; the
  absolute `/home/domingo/.dotnet/dotnet` was used throughout).
- `tests/Allegiance.Core.Tests` full suite → see run log; only the
  pre-existing documented live-wire flakes interrupt.

## Findings

### Scope item 3 — lifepod / docked / boarded behaviour (native early-outs)

Native's whole `CshipIGC::Update` is gated on `now > lastUpdate &&
m_pshipParent == NULL` (shipIGC.cpp:271) and a docked ship is never simmed in a
cluster. The port reproduces these for the recharge step:

- **Boarded turret gunner / observer** (`ParentShip != null`): no recharge —
  native skips the whole update for a boarded ship. Confirmed by
  `A_boarded_ship_does_not_recharge`.
- **Docked ship** (`StationId != null`): no recharge — native never updates a
  docked ship (absent from `cluster->GetShips()`, Sector.cs:479). Confirmed by
  `A_docked_ship_does_not_recharge`. (Docking already refills energy via the
  P95 station refit, Ship.cs:3778.)
- **Lifepod**: recharges like any in-cluster hull — native has no lifepod
  special case in the recharge (shipIGC.cpp:296-303). **Measured finding:**
  every lifepod-capable hull in the representative core (including the
  civilisations' own pod hull, `civ.Lifepod`) carries `RechargeRate 0` and
  `MaxEnergy 0`, so in practice a real pod's pool neither drains nor recharges —
  identical to native (rate-0 recharge is a no-op and clamps the empty pool to
  0). The gate demonstrably does not exclude a pod (synthetic lifepod-capable
  hull regenerates).

### Scope item 4 — the HUD energy gauge (finding + small fix)

The brief's premise that the HUD gauge is "driven by the server's energy
fraction" was **false for the own-ship path**: `ServerShipUpdate.FromModel`
(`src/Allegiance.Core/Wire/ServerShipUpdate.cs:150`) hardcoded the energy byte
(`_b[44]`, read by `HudRenderer.cs:272,372` via `up.EnergyFraction`) to `1.0`.
So the gauge could never show the drain NOR the recovery — it read full always.

Fix (one line, same 47-byte slot, mirroring the P362 ammo precedent): wire
`_b[44]` to the real `ship.Energy / ship.HullType.MaxEnergy` (clamped, with a
`MaxEnergy <= 0 → 0` guard). Now the gauge shows a drained pool and visibly
climbs back as recharge restores it. No client change was needed. This is the
resolution the brief asked for under "if it doesn't recover, find out why."

## Ripple fixes to pre-existing tests (encoded the pre-P461 "no recharge" world)

The native-faithful clamp `Energy > MaxEnergy → Energy = MaxEnergy` zeroes any
energy set on a hull whose `MaxEnergy == 0` (lifepod-derived hulls). Several
older tests artificially seeded energy on such hulls; P461 makes that state
impossible to hold. Updates (each preserves the test's original intent):

1. **LiveRipcordTests** — the ship-anchor hull is the low-id lifepod hull
   (`MaxEnergy 0`, `RechargeRate 0`). The ripcord energy gate reads the
   anchor's energy, which the clamp now pins to 0, aborting the ripcord. Gave
   the anchor a real pool (`MaxEnergy 5000f`, recharge still 0) so it can hold
   the payment energy — frozen, as the test intended.
2. **MissileCombatTests** (`Missile_and_weapon_projectile_coexist…`) — the
   shooter reuses the low-HP target hull `Ppvp` (a lifepod, `MaxEnergy 0`);
   the clamp zeroed its `EnergyPerShot` seed so the weapon could never fire.
   Gave the shooter a real pool (`MaxEnergy 20000f`).
3. **CloakSimTests** (2 energy tests) — ran the cloak's OWN drain/throttle on a
   real recharging hull; now every real energy hull recharges (15–80/s) faster
   than the 6/s test cloak, so a cloak no longer net-drains on a real hull and
   those two tests could no longer observe the drain. Ran them on a synthetic
   **zero-recharge** hull (`NoRechargeHull`) to isolate the cloak's own
   mechanic, complementing P461's own recharge-vs-cloak test.

## Out of scope (confirmed not ported)

- `c_gaMaxEnergy` side-attribute scaling of max energy → **P467b**.
- `Weapon.FireWeapon` energy math → untouched (correct once recharge exists).
- Out-of-bounds hull damage (shipIGC.cpp:385-403) and crowded-sector damage
  (shipIGC.cpp:405-425) → real missing branches of the same native `Update`,
  explicitly deferred per the brief.

## Commit

Landed on `master` (checked `git branch --show-current` before committing),
not pushed — Domingo pushes.
